using System;
using System.Collections.Generic;
using System.Text;

namespace UnityFlow.Editor.Model
{
    /// <summary>
    /// A flow file could not be turned into a <see cref="FlowDocument"/>.
    ///
    /// Every instance carries the source path plus a 1-based line and column, because the whole
    /// point of parsing through YamlDotNet's representation model rather than its object
    /// deserializer is that the failure can point at the exact character the author typed.
    /// A parse error without a position is a bug report the author cannot act on.
    /// </summary>
    public sealed class FlowParseException : Exception
    {
        /// <summary>Path of the flow file exactly as it was handed to the parser.</summary>
        public string SourcePath { get; }

        /// <summary>1-based line of the offending token.</summary>
        public int Line { get; }

        /// <summary>1-based column of the offending token.</summary>
        public int Column { get; }

        /// <summary>The message without the "path:line:column " prefix, for callers that render their own gutter.</summary>
        public string Detail { get; }

        public FlowParseException(string sourcePath, int line, int column, string detail)
            : this(sourcePath, line, column, detail, null)
        {
        }

        /// <summary>
        /// Same, keeping the exception that exposed the problem. Used where a third-party scanner
        /// raised the failure: the position and sentence are for the author, the inner exception is
        /// the only way to tell a malformed document apart from a bug in the scanner.
        /// </summary>
        public FlowParseException(string sourcePath, int line, int column, string detail, Exception inner)
            : base(Compose(sourcePath, line, column, detail), inner)
        {
            SourcePath = sourcePath;
            Line = line;
            Column = column;
            Detail = detail;
        }

        private static string Compose(string sourcePath, int line, int column, string detail) =>
            $"{sourcePath}:{line}:{column} {detail}";
    }

    /// <summary>
    /// Turns a typo into "Did you mean ...?".
    ///
    /// It lives beside <see cref="FlowParseException"/> because its only reason to exist is to
    /// build the tail of a parse error message; nothing else in the package consumes it.
    /// State is per-instance (two reusable Levenshtein rows) so several parsers can run without
    /// sharing a buffer.
    /// </summary>
    internal sealed class NameSuggestion
    {
        private int[] m_Previous = new int[32];
        private int[] m_Current = new int[32];

        /// <summary>
        /// Closest candidate within an edit-distance budget that scales with the typed length, or
        /// null when nothing is close enough. Comparison is case-insensitive so a wrong-case name
        /// ("tapon") still suggests the real one.
        /// </summary>
        public string Closest(string typed, IReadOnlyList<string> candidates)
        {
            if (string.IsNullOrEmpty(typed) || candidates == null || candidates.Count == 0)
                return null;

            var budget = Math.Max(2, typed.Length / 3);
            var bestDistance = int.MaxValue;
            string best = null;

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (string.IsNullOrEmpty(candidate))
                    continue;

                if (Math.Abs(candidate.Length - typed.Length) > budget)
                    continue;

                var distance = Distance(typed, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= budget ? best : null;
        }

        /// <summary>Ready-to-append sentence, e.g. " Did you mean 'tapOn'?". Empty when nothing is close.</summary>
        public string DidYouMean(string typed, IReadOnlyList<string> candidates)
        {
            var closest = Closest(typed, candidates);
            return closest == null ? string.Empty : $" Did you mean '{closest}'?";
        }

        /// <summary>
        /// " Did you mean 'x'?" when a close match exists, otherwise " Known {noun}: a, b, c."
        /// so the author is never left without a next move.
        /// </summary>
        public string DidYouMeanOrList(string typed, IReadOnlyList<string> candidates, string noun, int maxListed)
        {
            var closest = Closest(typed, candidates);
            if (closest != null)
                return $" Did you mean '{closest}'?";

            if (candidates == null || candidates.Count == 0)
                return string.Empty;

            return $" Known {noun}: {Join(candidates, maxListed)}.";
        }

        /// <summary>Comma-separated list, truncated with an ellipsis so a 200-verb vocabulary stays readable.</summary>
        public string Join(IReadOnlyList<string> names, int maxListed)
        {
            var builder = new StringBuilder();
            var shown = Math.Min(maxListed, names.Count);

            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(names[i]);
            }

            if (names.Count > shown)
                builder.Append(", ... (").Append(names.Count - shown).Append(" more)");

            return builder.ToString();
        }

        private int Distance(string a, string b)
        {
            var width = b.Length + 1;
            if (m_Previous.Length < width)
            {
                m_Previous = new int[width];
                m_Current = new int[width];
            }

            for (var j = 0; j <= b.Length; j++)
                m_Previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                m_Current[0] = i;
                var ca = char.ToLowerInvariant(a[i - 1]);

                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = ca == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    var deletion = m_Previous[j] + 1;
                    var insertion = m_Current[j - 1] + 1;
                    var substitution = m_Previous[j - 1] + cost;

                    var best = deletion < insertion ? deletion : insertion;
                    m_Current[j] = best < substitution ? best : substitution;
                }

                var swap = m_Previous;
                m_Previous = m_Current;
                m_Current = swap;
            }

            return m_Previous[b.Length];
        }
    }
}
