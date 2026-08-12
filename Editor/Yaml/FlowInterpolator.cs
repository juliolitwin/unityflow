using System;
using System.Collections.Generic;
using System.Text;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Yaml
{
    /// <summary>
    /// Substitutes <c>${name}</c> from a flow's <see cref="FlowEnv"/> into every string scalar of a
    /// parsed tree, at PARSE time.
    ///
    /// Parse time is the whole point. The alternative — resolving a variable when the step that uses
    /// it runs — makes a typo in a variable name surface ninety seconds into a run, after the client
    /// has logged in, if the step is ever reached at all. Doing it here means a misspelled
    /// <c>${charater}</c> fails in milliseconds with a <c>file:line:column</c> gutter, exactly like
    /// every other thing this parser refuses.
    ///
    /// <para><b>An undefined variable is an error, never an empty string.</b> Substituting nothing
    /// for a name nobody declared is how a flow ends up typing "" into a login field and reporting
    /// a pass for a session it never had.</para>
    ///
    /// <para><b>Escaping.</b> <c>$${</c> is a literal <c>${</c>, mirroring the doubled <c>@@</c>
    /// that <see cref="ValueConverter"/> uses for a literal at-sign: the sigil is written twice to
    /// mean itself. A lone <c>$</c> is never special, so shell-looking text and C# interpolated
    /// strings inside a <c>runScript</c> body pass through untouched unless they actually write
    /// <c>${</c>.</para>
    ///
    /// <para><b>Quoting.</b> A scalar that was substituted into comes back UNQUOTED even when it was
    /// written with quotes. YAML forces the quotes: <c>{ timeout: ${wait} }</c> is not writable in
    /// flow style because <c>{</c> is an indicator there, so the author must write
    /// <c>"${wait}"</c> — and those quotes are the syntax's demand, not the author's assertion that
    /// the value is text. Keeping them would make every numeric and boolean argument reject its own
    /// variable with "remove the quotes", which cannot be done.</para>
    ///
    /// The original text of every substituted scalar is kept so a conversion failure downstream can
    /// say what was written as well as what it became: <c>'timeout' expects a duration ..., got
    /// 'soon' (substituted from "${wait}")</c> is actionable, while <c>got 'soon'</c> sends the
    /// author looking for a word that appears nowhere in the file.
    ///
    /// One instance belongs to one <see cref="FlowParser"/> and is not thread safe.
    /// </summary>
    internal sealed class FlowInterpolator
    {
        /// <summary>How much of a malformed scalar to quote back. Enough to find, short enough to read.</summary>
        private const int ExcerptLength = 32;

        /// <summary>
        /// Substituted scalar to the text it was written as, keyed by REFERENCE: a substituted
        /// <see cref="FlowValue"/> is a fresh instance created here, so identity is exactly the
        /// right key and no value ever collides with another that happens to read the same.
        /// </summary>
        private readonly Dictionary<FlowValue, string> m_Templates = new Dictionary<FlowValue, string>();

        private readonly NameSuggestion m_Suggest = new NameSuggestion();
        private readonly StringBuilder m_Builder = new StringBuilder(256);

        /// <summary>Forget the previous document. A parser instance may be reused for several files.</summary>
        public void Reset()
        {
            m_Templates.Clear();
        }

        /// <summary>
        /// Return <paramref name="value"/> with every scalar under it substituted.
        ///
        /// Values that contain no <c>$</c> are returned as the SAME instance, and a list or mapping
        /// whose children were all unchanged is returned unchanged too, so a flow that uses no
        /// variables is walked once and rebuilt not at all.
        /// </summary>
        public FlowValue Apply(FlowValue value, FlowEnv env, string sourcePath)
        {
            switch (value.Kind)
            {
                case FlowValueKind.Scalar:
                {
                    var text = Substitute(value.Scalar, env, value, sourcePath);
                    if (ReferenceEquals(text, value.Scalar))
                        return value;

                    var substituted = FlowValue.OfScalar(text, false, value.Line, value.Column);
                    m_Templates[substituted] = value.Scalar;
                    return substituted;
                }

                case FlowValueKind.List:
                {
                    FlowValue[] items = null;

                    for (var i = 0; i < value.Items.Count; i++)
                    {
                        var item = Apply(value.Items[i], env, sourcePath);
                        if (ReferenceEquals(item, value.Items[i]) && items == null)
                            continue;

                        if (items == null)
                        {
                            items = new FlowValue[value.Items.Count];
                            for (var j = 0; j < i; j++)
                                items[j] = value.Items[j];
                        }

                        items[i] = item;
                    }

                    return items == null ? value : FlowValue.OfList(items, value.Line, value.Column);
                }

                case FlowValueKind.Map:
                {
                    List<FlowEntry> entries = null;

                    for (var i = 0; i < value.Entries.Count; i++)
                    {
                        var entry = value.Entries[i];

                        // Keys are structural names — verbs, argument names, selector keys — and are
                        // matched against fixed vocabularies. A variable there would name a key that
                        // no vocabulary could have declared, so keys are deliberately left alone.
                        var child = Apply(entry.Value, env, sourcePath);
                        if (ReferenceEquals(child, entry.Value) && entries == null)
                            continue;

                        if (entries == null)
                        {
                            entries = new List<FlowEntry>(value.Entries.Count);
                            for (var j = 0; j < i; j++)
                                entries.Add(value.Entries[j]);
                        }

                        entries.Add(new FlowEntry(entry.Key, entry.KeyLine, entry.KeyColumn, child));
                    }

                    return entries == null ? value : FlowValue.OfMap(entries, value.Line, value.Column);
                }

                default:
                    return value;
            }
        }

        /// <summary>
        /// Read an <c>env:</c> DEFAULT, which is a literal: the <c>$${</c> escape is resolved so a
        /// default can hold a dollar-brace, but a live <c>${...}</c> is refused.
        ///
        /// Refused rather than resolved because a default that referred to another variable would
        /// need a defined resolution order between entries of the same mapping, and YAML mappings
        /// have none worth relying on. One level of substitution, in one direction, is a rule an
        /// author can hold in their head.
        /// </summary>
        public string ReadDefault(string text, FlowValue at, string name, string sourcePath)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('$') < 0)
                return text;

            m_Builder.Clear();

            for (var i = 0; i < text.Length;)
            {
                if (text[i] == '$' && i + 2 < text.Length && text[i + 1] == '$' && text[i + 2] == '{')
                {
                    m_Builder.Append("${");
                    i += 3;
                    continue;
                }

                if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    throw Failure(at, text, i, sourcePath,
                        $"the default for 'env.{name}' contains '{Excerpt(text, i)}', but an env default is a literal: " +
                        "a variable may not be defined in terms of another. Write '$${' for a literal dollar-brace");
                }

                m_Builder.Append(text[i]);
                i++;
            }

            return m_Builder.ToString();
        }

        /// <summary>
        /// Tail for an error about <paramref name="value"/>, naming the text it was substituted
        /// from. Empty for a value nobody substituted into, so the common message is unchanged.
        /// </summary>
        public string DescribeSubstitution(FlowValue value)
        {
            if (value == null || !m_Templates.TryGetValue(value, out var template))
                return string.Empty;

            return $" (substituted from \"{template}\")";
        }

        /// <summary>
        /// One pass over a scalar. Returns the SAME reference when nothing was rewritten, which is
        /// how <see cref="Apply"/> decides whether to allocate a new node.
        /// </summary>
        private string Substitute(string text, FlowEnv env, FlowValue at, string sourcePath)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var first = text.IndexOf('$');
            if (first < 0)
                return text;

            m_Builder.Clear();
            m_Builder.Append(text, 0, first);

            var rewritten = false;

            for (var i = first; i < text.Length;)
            {
                if (text[i] != '$')
                {
                    m_Builder.Append(text[i]);
                    i++;
                    continue;
                }

                // '$${' is a literal '${'. The closing brace is then an ordinary character, exactly
                // as '@@' makes only the at-sign literal and leaves the rest of the word alone.
                if (i + 2 < text.Length && text[i + 1] == '$' && text[i + 2] == '{')
                {
                    m_Builder.Append("${");
                    i += 3;
                    rewritten = true;
                    continue;
                }

                if (i + 1 >= text.Length || text[i + 1] != '{')
                {
                    m_Builder.Append(text[i]);
                    i++;
                    continue;
                }

                var close = text.IndexOf('}', i + 2);
                if (close < 0)
                {
                    throw Failure(at, text, i, sourcePath,
                        $"'{Excerpt(text, i)}' opens a variable with '${{' that is never closed. " +
                        "Write '$${' for a literal dollar-brace");
                }

                var name = text.Substring(i + 2, close - i - 2);

                if (name.Length == 0)
                {
                    throw Failure(at, text, i, sourcePath,
                        "'${}' names no variable. Write '$${}' for a literal dollar-brace");
                }

                if (!FlowEnv.IsName(name))
                {
                    throw Failure(at, text, i, sourcePath,
                        $"'${{{name}}}' is not a variable name; use letters, digits, '_' and '.', starting with a letter or '_'");
                }

                if (!env.TryGet(name, out var replacement))
                    throw Failure(at, text, i, sourcePath, Undefined(name, env));

                m_Builder.Append(replacement);
                i = close + 1;
                rewritten = true;
            }

            return rewritten ? m_Builder.ToString() : text;
        }

        private string Undefined(string name, FlowEnv env)
        {
            if (env.Count == 0)
            {
                return $"'${{{name}}}' is not defined: this flow has no 'env:' block. " +
                       $"Declare it at the top level, e.g. 'env: {{ {name}: \"\" }}', and override it with --env {name}=...";
            }

            return $"'${{{name}}}' is not defined by this flow's 'env:' block. " +
                   $"Defined: {m_Suggest.Join(env.Names, env.Names.Count)}." +
                   m_Suggest.DidYouMean(name, env.Names);
        }

        /// <summary>
        /// Position the failure inside the scalar rather than at its first character.
        ///
        /// It matters for a block scalar: a <c>runScript</c> body is one YAML node covering thirty
        /// lines, and pointing every one of its errors at the <c>|</c> would defeat the reason this
        /// parser reads the representation model at all. Such a node's own mark IS that indicator,
        /// so the text begins one line further down — see <see cref="FlowValue.IsBlock"/>.
        ///
        /// The column is only trustworthy on a scalar's first line. A block scalar's indentation has
        /// been stripped by the time the text reaches here, so the original column of a later line
        /// is not recoverable, and 1 is reported rather than a number that would be wrong.
        /// </summary>
        private static FlowParseException Failure(FlowValue at, string text, int index, string sourcePath, string detail)
        {
            var line = at.IsBlock ? at.Line + 1 : at.Line;
            var column = at.IsBlock ? 1 : at.Column;

            for (var i = 0; i < index; i++)
            {
                if (text[i] != '\n')
                    continue;

                line++;
                column = 1;
            }

            return new FlowParseException(sourcePath, line, column, detail);
        }

        private static string Excerpt(string text, int from)
        {
            var end = from;
            while (end < text.Length && end - from < ExcerptLength && text[end] != '\n')
                end++;

            var excerpt = text.Substring(from, end - from);
            return end - from == ExcerptLength ? excerpt + "..." : excerpt;
        }
    }
}
