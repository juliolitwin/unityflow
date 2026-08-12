using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Yaml
{
    /// <summary>
    /// Builds a <see cref="Selector"/> from the two shapes a flow may use: the text shorthand
    /// (<c>tapOn: "Shop"</c>) and the explicit mapping (<c>tapOn: { testId: shop.root }</c>).
    ///
    /// Unknown keys are rejected, never ignored. A selector that quietly drops <c>testid:</c>
    /// because of a lowercase 'd' would still match something, and the test would pass for the
    /// wrong reason — which is worse than failing.
    ///
    /// This type throws <see cref="FlowParseException"/> rather than returning a reason string,
    /// because unlike <see cref="ValueConverter"/> it is given the source path and can therefore
    /// build the complete, positioned message itself.
    /// </summary>
    public sealed class SelectorParser
    {
        private readonly ValueConverter m_Converter;
        private readonly NameSuggestion m_Suggest = new NameSuggestion();

        private readonly string[] m_Keys =
        {
            "testId", "text", "exact", "contains", "matches",
            "name", "path", "type", "index", "within", "backend"
        };

        public SelectorParser(ValueConverter converter)
        {
            m_Converter = converter ?? throw new ArgumentNullException(nameof(converter));
        }

        /// <summary>Every key a selector mapping accepts, for "Did you mean ...?" over the union with a verb's own arguments.</summary>
        public IReadOnlyList<string> Keys => m_Keys;

        /// <summary>Whether a key belongs to the selector rather than to the verb.</summary>
        public bool IsSelectorKey(string key)
        {
            for (var i = 0; i < m_Keys.Length; i++)
            {
                if (string.Equals(m_Keys[i], key, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Parse a standalone selector: the value of <c>on:</c>, of <c>within:</c>, or of a
        /// selector-typed verb argument such as <c>swipe</c>'s <c>from:</c>.
        /// </summary>
        public Selector Parse(FlowValue value, string sourcePath, string what)
        {
            switch (value.Kind)
            {
                case FlowValueKind.Scalar:
                    return FromScalar(value, sourcePath, what);

                case FlowValueKind.Map:
                    return FromEntries(value.Entries, value, SelectorForm.Mapping, sourcePath, what);

                default:
                    throw new FlowParseException(
                        sourcePath, value.Line, value.Column,
                        $"'{what}' expects a selector, either the text shorthand \"Shop\" or a mapping like {{ testId: shop.root }}, got {value.Describe()}");
            }
        }

        /// <summary>
        /// Build a selector from mapping entries the caller has already isolated. The parser uses
        /// this for the inline selector of a step, where the verb's own arguments and the step
        /// modifiers share one mapping with the selector keys and have already been removed.
        /// </summary>
        public Selector FromEntries(
            IReadOnlyList<FlowEntry> entries,
            FlowValue owner,
            SelectorForm form,
            string sourcePath,
            string what)
        {
            string testId = null;
            string text = null;
            var textMatch = TextMatchMode.Exact;
            Regex textRegex = null;
            string textKey = null;
            string name = null;
            string path = null;
            string type = null;
            int? index = null;
            Selector within = null;
            string backend = null;

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                switch (entry.Key)
                {
                    case "testId":
                        testId = RequireText(entry, sourcePath, what);
                        break;

                    case "text":
                    case "exact":
                        RejectSecondTextCriterion(entry, textKey, sourcePath, what);
                        text = RequireText(entry, sourcePath, what);
                        textMatch = TextMatchMode.Exact;
                        textKey = entry.Key;
                        break;

                    case "contains":
                        RejectSecondTextCriterion(entry, textKey, sourcePath, what);
                        text = RequireText(entry, sourcePath, what);
                        textMatch = TextMatchMode.Contains;
                        textKey = entry.Key;
                        break;

                    case "matches":
                        RejectSecondTextCriterion(entry, textKey, sourcePath, what);
                        text = RequireText(entry, sourcePath, what);
                        textMatch = TextMatchMode.Regex;
                        textRegex = Compile(text, entry, sourcePath, what);
                        textKey = entry.Key;
                        break;

                    case "name":
                        name = RequireText(entry, sourcePath, what);
                        break;

                    case "path":
                        path = RequireText(entry, sourcePath, what);
                        break;

                    case "type":
                        type = RequireText(entry, sourcePath, what);
                        break;

                    case "index":
                        index = RequireIndex(entry, sourcePath, what);
                        break;

                    case "within":
                        within = Parse(entry.Value, sourcePath, $"{what}.within");
                        break;

                    case "backend":
                        backend = RequireText(entry, sourcePath, what);
                        break;

                    default:
                        throw new FlowParseException(
                            sourcePath, entry.KeyLine, entry.KeyColumn,
                            $"'{what}' has no selector key '{entry.Key}'.{m_Suggest.DidYouMeanOrList(entry.Key, m_Keys, "selector keys", m_Keys.Length)}");
                }
            }

            var selector = new Selector(
                null, testId, text, textMatch, textRegex, name, path, type, index, within, backend,
                form, owner.Line, owner.Column);

            if (!selector.HasIdentifyingCriterion)
            {
                throw new FlowParseException(
                    sourcePath, owner.Line, owner.Column,
                    $"'{what}' needs at least one of testId, text, name, path or type; index, within and backend only narrow an existing match");
            }

            return selector;
        }

        private Selector FromScalar(FlowValue value, string sourcePath, string what)
        {
            if (m_Converter.IsReference(value))
            {
                if (!m_Converter.TryReadReference(value, what, out var reference, out var error))
                    throw new FlowParseException(sourcePath, value.Line, value.Column, error);

                return new Selector(
                    reference, null, null, TextMatchMode.Exact, null, null, null, null, null, null, null,
                    SelectorForm.Scalar, value.Line, value.Column);
            }

            var text = m_Converter.Unescape(value.Scalar);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FlowParseException(
                    sourcePath, value.Line, value.Column,
                    text.Length == 0
                        ? $"'{what}' expects the visible text to match, got an empty string"
                        : $"'{what}' expects the visible text to match, got only whitespace");
            }

            return new Selector(
                null, null, text, TextMatchMode.Exact, null, null, null, null, null, null, null,
                SelectorForm.Scalar, value.Line, value.Column);
        }

        private void RejectSecondTextCriterion(FlowEntry entry, string existingKey, string sourcePath, string what)
        {
            if (existingKey == null)
                return;

            throw new FlowParseException(
                sourcePath, entry.KeyLine, entry.KeyColumn,
                $"'{what}' matches text twice, with '{existingKey}' and '{entry.Key}'. Use exactly one of text, exact, contains or matches");
        }

        private string RequireText(FlowEntry entry, string sourcePath, string what)
        {
            if (!m_Converter.TryParseString(entry.Value, $"{what}.{entry.Key}", out var text, out var error))
                throw new FlowParseException(sourcePath, entry.Value.Line, entry.Value.Column, error);

            // A criterion of nothing but spaces identifies nothing, so accepting it would let a
            // selector match by accident instead of by intent.
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    text.Length == 0
                        ? $"'{what}.{entry.Key}' must not be empty"
                        : $"'{what}.{entry.Key}' must not be only whitespace");
            }

            return text;
        }

        private int RequireIndex(FlowEntry entry, string sourcePath, string what)
        {
            if (!m_Converter.TryParseInt(entry.Value, $"{what}.index", out var index, out var error))
                throw new FlowParseException(sourcePath, entry.Value.Line, entry.Value.Column, error);

            if (index < 0)
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'{what}.index' is 0-based and cannot be negative, got {index}");
            }

            return index;
        }

        private static Regex Compile(string pattern, FlowEntry entry, string sourcePath, string what)
        {
            try
            {
                return Selector.CompileTextRegex(pattern);
            }
            catch (ArgumentException ex)
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'{what}.matches' is not a valid regular expression: {ex.Message}");
            }
        }
    }
}
