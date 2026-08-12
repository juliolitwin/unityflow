using System;
using System.Collections.Generic;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Core
{
    /// <summary>How a selector resolved.</summary>
    public enum ResolutionOutcome
    {
        /// <summary>Exactly one node matched (or an explicit index picked one).</summary>
        Resolved,

        /// <summary>Nothing matched. The runner keeps retrying until the step deadline.</summary>
        NotFound,

        /// <summary>
        /// Several nodes matched and the author did not say which. This is a FAILURE, never a
        /// "take the first". Silently taking the first match is the single largest source of tests
        /// that pass by accident, because the flow keeps working while pointing at the wrong node.
        /// </summary>
        Ambiguous,

        /// <summary>The selector itself cannot identify anything (e.g. only an index was given).</summary>
        InvalidSelector
    }

    /// <summary>Outcome of one resolution attempt, with everything a failure report needs.</summary>
    public readonly struct ResolutionResult
    {
        public readonly ResolutionOutcome Outcome;

        /// <summary>The resolved node. Only meaningful when <see cref="Outcome"/> is Resolved.</summary>
        public readonly UiNode Node;

        /// <summary>All matches, when the result was ambiguous. Empty otherwise.</summary>
        public readonly IReadOnlyList<UiNode> Matches;

        /// <summary>Suggestions when nothing matched, e.g. "achievement.root", "achievements.list".</summary>
        public readonly IReadOnlyList<string> NearMisses;

        /// <summary>One-line explanation, ready to print.</summary>
        public readonly string Message;

        private ResolutionResult(ResolutionOutcome outcome, UiNode node,
            IReadOnlyList<UiNode> matches, IReadOnlyList<string> nearMisses, string message)
        {
            Outcome = outcome;
            Node = node;
            Matches = matches ?? Array.Empty<UiNode>();
            NearMisses = nearMisses ?? Array.Empty<string>();
            Message = message;
        }

        public bool IsResolved => Outcome == ResolutionOutcome.Resolved;

        /// <summary>Whether retrying could still succeed. An ambiguous or invalid selector never will.</summary>
        public bool IsRetryable => Outcome == ResolutionOutcome.NotFound;

        public static ResolutionResult Resolved(UiNode node) =>
            new ResolutionResult(ResolutionOutcome.Resolved, node, null, null, null);

        public static ResolutionResult NotFound(IReadOnlyList<string> nearMisses, string message) =>
            new ResolutionResult(ResolutionOutcome.NotFound, default, null, nearMisses, message);

        public static ResolutionResult Ambiguous(IReadOnlyList<UiNode> matches, string message) =>
            new ResolutionResult(ResolutionOutcome.Ambiguous, default, matches, null, message);

        public static ResolutionResult Invalid(string message) =>
            new ResolutionResult(ResolutionOutcome.InvalidSelector, default, null, null, message);
    }

    /// <summary>
    /// Turns a <see cref="Selector"/> into exactly one <see cref="UiNode"/>, or an explicit failure.
    ///
    /// Runs once per retry frame, so it reuses its buffers and never allocates on the success path.
    /// The expensive work — near-miss suggestions — happens only when a step is about to fail.
    /// </summary>
    public sealed class SelectorResolver
    {
        private const int MaxNearMisses = 5;
        private const int MaxAmbiguityListed = 8;

        private readonly BackendRegistry m_Registry;
        private readonly List<UiNode> m_Enumerated = new List<UiNode>(256);
        private readonly List<UiNode> m_Matches = new List<UiNode>(16);
        private readonly List<UiNode> m_WithinScope = new List<UiNode>(4);

        /// <summary>Handles bound by an earlier step's <c>as:</c>, keyed by reference name.</summary>
        private readonly Dictionary<string, UiHandle> m_References = new Dictionary<string, UiHandle>(StringComparer.Ordinal);

        public SelectorResolver(BackendRegistry registry) =>
            m_Registry = registry ?? throw new ArgumentNullException(nameof(registry));

        /// <summary>Values bound by a step's <c>as:</c> that are not UI nodes, e.g. a script's return value.</summary>
        private readonly Dictionary<string, object> m_Values = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>Bind a name for later <c>@name</c> selectors.</summary>
        public void Bind(string name, UiHandle handle) => m_References[name] = handle;

        /// <summary>
        /// Bind a plain value under a name. Separate from <see cref="Bind"/> because a value and a
        /// node handle are not interchangeable: a selector can resolve a handle, and only a
        /// comparison can consume a value. Keeping them apart makes using one where the other is
        /// meant a loud error rather than a confusing resolution failure.
        /// </summary>
        public void BindValue(string name, object value) => m_Values[name] = value;

        /// <summary>Read a value bound by an earlier <c>as:</c>.</summary>
        public bool TryGetValue(string name, out object value) => m_Values.TryGetValue(name, out value);

        /// <summary>
        /// Resolve, applying the priority order testId &gt; text &gt; name &gt; path and the
        /// 0 / 1 / N-fails rule.
        /// </summary>
        public ResolutionResult Resolve(Selector selector)
        {
            if (selector == null)
                return ResolutionResult.Invalid("no selector was supplied");

            if (selector.Reference != null)
                return ResolveReference(selector);

            if (!selector.HasIdentifyingCriterion)
            {
                return ResolutionResult.Invalid(
                    $"{selector} cannot identify a node: index, within and backend only narrow an " +
                    "existing set. Add a testId, text, name, path or type.");
            }

            string withinPathPrefix = null;
            if (selector.Within != null)
            {
                var parent = Resolve(selector.Within);
                if (!parent.IsResolved)
                {
                    return ResolutionResult.NotFound(parent.NearMisses,
                        $"the 'within' selector {selector.Within} did not resolve: {parent.Message}");
                }

                withinPathPrefix = parent.Node.Path + "/";
            }

            Collect(selector, withinPathPrefix, includeInactive: false, m_Matches);

            if (m_Matches.Count == 0)
                return NoMatch(selector, withinPathPrefix);

            if (m_Matches.Count == 1)
                return ResolutionResult.Resolved(m_Matches[0]);

            // Deterministic order so an index means the same thing across runs: topmost surface
            // first, then hierarchy path. Enumeration order alone is not stable.
            m_Matches.Sort(CompareForIndex);

            if (selector.Index.HasValue)
            {
                var index = selector.Index.Value;
                if (index < 0 || index >= m_Matches.Count)
                {
                    return ResolutionResult.Invalid(
                        $"{selector} asked for index {index} but only {m_Matches.Count} nodes matched (valid range 0..{m_Matches.Count - 1})");
                }

                return ResolutionResult.Resolved(m_Matches[index]);
            }

            return ResolutionResult.Ambiguous(CopyMatches(), BuildAmbiguityMessage(selector));
        }

        private ResolutionResult ResolveReference(Selector selector)
        {
            if (!m_References.TryGetValue(selector.Reference, out var handle))
            {
                return ResolutionResult.Invalid(
                    $"'@{selector.Reference}' was never bound. Add 'as: {selector.Reference}' to an earlier step.");
            }

            var backend = m_Registry.ForHandle(handle);
            if (backend == null || !backend.TryGetNode(handle, out var node))
            {
                return ResolutionResult.NotFound(Array.Empty<string>(),
                    $"'@{selector.Reference}' referred to a node that no longer exists (it was destroyed or rebuilt since it was bound)");
            }

            return ResolutionResult.Resolved(node);
        }

        private void Collect(Selector selector, string withinPathPrefix, bool includeInactive, List<UiNode> into)
        {
            into.Clear();

            var options = includeInactive ? EnumerateOptions.Diagnostic : EnumerateOptions.Default;

            // Push down whatever the backend can filter cheaply; the rest is matched here.
            options.TestIdFilter = selector.TestId;
            options.NameFilter = selector.Name;
            options.TypeFilter = selector.Type;

            foreach (var backend in m_Registry.Active)
            {
                if (selector.Backend != null &&
                    !string.Equals(backend.Id, selector.Backend, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                backend.Settle();

                m_Enumerated.Clear();
                backend.Enumerate(in options, m_Enumerated);

                for (var i = 0; i < m_Enumerated.Count; i++)
                {
                    var node = m_Enumerated[i];
                    if (Matches(selector, node, withinPathPrefix))
                        into.Add(node);
                }
            }
        }

        private static bool Matches(Selector selector, in UiNode node, string withinPathPrefix)
        {
            if (withinPathPrefix != null &&
                (node.Path == null || !node.Path.StartsWith(withinPathPrefix, StringComparison.Ordinal)))
            {
                return false;
            }

            if (selector.TestId != null && !string.Equals(node.TestId, selector.TestId, StringComparison.Ordinal))
                return false;

            if (selector.Name != null && !string.Equals(node.Name, selector.Name, StringComparison.Ordinal))
                return false;

            if (selector.Path != null && !string.Equals(node.Path, selector.Path, StringComparison.Ordinal))
                return false;

            if (selector.Type != null && !TypeMatches(node.Type, selector.Type))
                return false;

            if (selector.Text != null && !TextMatches(selector, node.Text))
                return false;

            return true;
        }

        private static bool TypeMatches(string nodeType, string wanted)
        {
            if (nodeType == null)
                return false;

            // Projects subclass uGUI types constantly (RichInputField : InputField), so an exact
            // compare would make 'type: InputField' miss the very controls a user means. The
            // backend reports the most-derived name plus its base chain separated by ':'.
            if (string.Equals(nodeType, wanted, StringComparison.OrdinalIgnoreCase))
                return true;

            var start = 0;
            while (start < nodeType.Length)
            {
                var end = nodeType.IndexOf(':', start);
                if (end < 0) end = nodeType.Length;

                if (end - start == wanted.Length &&
                    string.Compare(nodeType, start, wanted, 0, wanted.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }

                start = end + 1;
            }

            return false;
        }

        private static bool TextMatches(Selector selector, string text)
        {
            if (text == null)
                return false;

            switch (selector.TextMatch)
            {
                case TextMatchMode.Exact:
                    return string.Equals(text, selector.Text, StringComparison.Ordinal);
                case TextMatchMode.Contains:
                    return text.IndexOf(selector.Text, StringComparison.OrdinalIgnoreCase) >= 0;
                case TextMatchMode.Regex:
                    return selector.TextRegex != null && selector.TextRegex.IsMatch(text);
                default:
                    return false;
            }
        }

        private static int CompareForIndex(UiNode a, UiNode b)
        {
            var bySort = b.SortKey.CompareTo(a.SortKey);
            if (bySort != 0)
                return bySort;

            return string.CompareOrdinal(a.Path, b.Path);
        }

        private UiNode[] CopyMatches()
        {
            var copy = new UiNode[m_Matches.Count];
            m_Matches.CopyTo(copy);
            return copy;
        }

        private string BuildAmbiguityMessage(Selector selector)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(selector).Append(" matched ").Append(m_Matches.Count).Append(" nodes; it must match exactly one.");
            sb.Append("\n  Add 'index: <0-based>' to pick one, or 'within: { ... }' to narrow the search.");

            var listed = Math.Min(m_Matches.Count, MaxAmbiguityListed);
            for (var i = 0; i < listed; i++)
                sb.Append("\n    [").Append(i).Append("] ").Append(m_Matches[i].Path);

            if (m_Matches.Count > listed)
                sb.Append("\n    ... and ").Append(m_Matches.Count - listed).Append(" more");

            return sb.ToString();
        }

        private ResolutionResult NoMatch(Selector selector, string withinPathPrefix)
        {
            // Re-scan including hidden nodes. "It exists but its alpha is 0" is a completely
            // different bug from "it does not exist", and only this second pass can tell them
            // apart. This runs on the failure path only.
            Collect(selector, withinPathPrefix, includeInactive: true, m_WithinScope);

            if (m_WithinScope.Count > 0)
            {
                var hidden = m_WithinScope[0];
                var reason = hidden.Reason ?? "it is present but not visible";
                return ResolutionResult.NotFound(Array.Empty<string>(),
                    $"{selector} matched no VISIBLE node, but {m_WithinScope.Count} hidden node(s) match. " +
                    $"First: {hidden.Path} — {reason}");
            }

            return ResolutionResult.NotFound(FindNearMisses(selector), $"{selector} matched 0 nodes");
        }

        /// <summary>
        /// Suggest ids/texts/names that are close to what was asked for. A timeout that also says
        /// "you probably meant achievement.root" saves the reader a round trip.
        /// </summary>
        private List<string> FindNearMisses(Selector selector)
        {
            var wanted = selector.TestId ?? selector.Text ?? selector.Name ?? selector.Path;
            var suggestions = new List<string>();
            if (wanted == null)
                return suggestions;

            var scored = new List<(int distance, string value)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var options = EnumerateOptions.Diagnostic;

            foreach (var backend in m_Registry.Active)
            {
                m_Enumerated.Clear();
                backend.Enumerate(in options, m_Enumerated);

                for (var i = 0; i < m_Enumerated.Count; i++)
                {
                    var node = m_Enumerated[i];
                    Consider(node.TestId);
                    Consider(node.Text);
                    Consider(node.Name);
                }
            }

            scored.Sort((a, b) => a.distance.CompareTo(b.distance));
            for (var i = 0; i < scored.Count && suggestions.Count < MaxNearMisses; i++)
                suggestions.Add(scored[i].value);

            return suggestions;

            void Consider(string candidate)
            {
                if (string.IsNullOrEmpty(candidate) || !seen.Add(candidate))
                    return;

                var distance = Levenshtein(wanted, candidate);

                // Accept a suggestion only when it is genuinely close, or a substring match.
                // A list of unrelated names is noise that makes the report harder to read.
                var threshold = Math.Max(2, wanted.Length / 3);
                if (distance <= threshold ||
                    candidate.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    wanted.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    scored.Add((distance, candidate));
                }
            }
        }

        internal static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
