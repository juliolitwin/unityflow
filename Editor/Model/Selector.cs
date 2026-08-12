using System;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityFlow.Editor.Model
{
    /// <summary>
    /// How a selector's text criterion is compared against a node's visible text.
    /// Named ...Mode so it never collides with the <see cref="Selector.TextMatch"/> property.
    /// </summary>
    public enum TextMatchMode
    {
        /// <summary>Whole string equality. What a bare <c>text:</c> means.</summary>
        Exact,

        /// <summary>Substring. Written as <c>contains:</c>.</summary>
        Contains,

        /// <summary>Regular expression, pre-compiled into <see cref="Selector.TextRegex"/>. Written as <c>matches:</c>.</summary>
        Regex
    }

    /// <summary>How the author wrote the selector, kept so an error can quote the shape they used.</summary>
    public enum SelectorForm
    {
        /// <summary>Shorthand scalar: <c>tapOn: "Shop"</c>.</summary>
        Scalar,

        /// <summary>Explicit mapping: <c>tapOn: { text: "Shop" }</c>.</summary>
        Mapping
    }

    /// <summary>
    /// A structured description of "which node", immutable after parse.
    ///
    /// The resolver applies the spec's priority order — testId, then visible text, then name, then
    /// hierarchy path — and this type deliberately does not encode that order: it only records what
    /// the author asked for. Encoding priority here would force a model change every time the
    /// resolution strategy is tuned.
    ///
    /// A regex criterion is compiled once, at parse time, and stored. The retry loop re-evaluates a
    /// selector every frame, so constructing a <see cref="System.Text.RegularExpressions.Regex"/>
    /// there would allocate on the hot path and hide a bad pattern until the first retry.
    /// </summary>
    public sealed class Selector
    {
        /// <summary>
        /// Ceiling on ONE match attempt of an author-supplied pattern.
        ///
        /// A <c>matches:</c> pattern is untrusted input, and a catastrophic one is not merely slow:
        /// <c>^(a+)+$</c> against a 29-character string backtracks for about eleven seconds, the
        /// retry loop re-evaluates every selector against every candidate node EVERY FRAME, and the
        /// pipeline's HTTP server is serial — so an infinite match timeout turns one typo into an
        /// editor that never answers again and cannot be told to stop. Visible UI text is short and
        /// a sane pattern over it finishes in microseconds, so anything beyond this ceiling is
        /// pathological rather than merely big, and failing the step is the honest answer.
        /// </summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Compile a text pattern the way EVERY UnityFlow regex must be compiled: culture-invariant
        /// and bounded by <see cref="MatchTimeout"/>. Throws <see cref="ArgumentException"/> for an
        /// invalid pattern — the caller owns the source position and turns that into a positioned
        /// message.
        /// </summary>
        public static Regex CompileTextRegex(string pattern) =>
            new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);

        private readonly string m_Description;

        /// <summary>
        /// Name bound earlier by a step's <c>as:</c>, when the selector was written as <c>"@sword"</c>.
        /// Mutually exclusive with every other criterion; the runner substitutes the bound handle.
        /// </summary>
        public string Reference { get; }

        /// <summary>Value of a <see cref="UnityFlow.FlowTestId"/> or the UI Toolkit equivalent.</summary>
        public string TestId { get; }

        /// <summary>Visible text pattern. Interpretation depends on <see cref="TextMatch"/>.</summary>
        public string Text { get; }

        /// <summary>How <see cref="Text"/> is compared. Meaningless when <see cref="Text"/> is null.</summary>
        public TextMatchMode TextMatch { get; }

        /// <summary>Compiled form of <see cref="Text"/>. Non-null exactly when <see cref="TextMatch"/> is <see cref="TextMatchMode.Regex"/>.</summary>
        public Regex TextRegex { get; }

        /// <summary>GameObject name (uGUI) or VisualElement name (UI Toolkit). Exact match.</summary>
        public string Name { get; }

        /// <summary>Full hierarchy path, e.g. <c>/Canvas/Shop/Buy</c>.</summary>
        public string Path { get; }

        /// <summary>Control type, matched against <see cref="Core.UiNode.Type"/>. Open string, not an enum.</summary>
        public string Type { get; }

        /// <summary>0-based index among the candidates that survive every other criterion. Null means "must be unique".</summary>
        public int? Index { get; }

        /// <summary>Restricts the search to descendants of this parent selector.</summary>
        public Selector Within { get; }

        /// <summary>Restricts the search to one backend id, e.g. "ugui" or "uitk". Open string; the registry validates it.</summary>
        public string Backend { get; }

        /// <summary>Shorthand or explicit mapping.</summary>
        public SelectorForm Form { get; }

        /// <summary>1-based line the selector was written on.</summary>
        public int Line { get; }

        /// <summary>1-based column the selector was written at.</summary>
        public int Column { get; }

        public Selector(
            string reference,
            string testId,
            string text,
            TextMatchMode textMatch,
            Regex textRegex,
            string name,
            string path,
            string type,
            int? index,
            Selector within,
            string backend,
            SelectorForm form,
            int line,
            int column)
        {
            Reference = reference;
            TestId = testId;
            Text = text;
            TextMatch = textMatch;
            TextRegex = textRegex;
            Name = name;
            Path = path;
            Type = type;
            Index = index;
            Within = within;
            Backend = backend;
            Form = form;
            Line = line;
            Column = column;
            m_Description = Describe();
        }

        /// <summary>True when the author supplied no criterion at all.</summary>
        public bool IsEmpty =>
            Reference == null && TestId == null && Text == null && Name == null &&
            Path == null && Type == null && Index == null && Within == null && Backend == null;

        /// <summary>
        /// True when at least one criterion can actually identify a node. Index, within and backend
        /// only narrow an existing candidate set, so a selector made only of those cannot resolve.
        /// </summary>
        public bool HasIdentifyingCriterion =>
            Reference != null || TestId != null || Text != null || Name != null || Path != null || Type != null;

        /// <summary>
        /// Compact form used in every failure line, e.g. <c>testId=shop.root</c> or <c>text~="Purch"</c>.
        /// Computed once at construction: failure reports are built from many of these and the
        /// string must never be re-derived inside a loop.
        /// </summary>
        public override string ToString() => m_Description;

        private string Describe()
        {
            if (Reference != null)
                return "@" + Reference;

            if (IsEmpty)
                return "<empty selector>";

            var builder = new StringBuilder();

            if (TestId != null)
                Append(builder, "testId=" + TestId);

            if (Text != null)
            {
                switch (TextMatch)
                {
                    case TextMatchMode.Exact:
                        Append(builder, $"text=\"{Text}\"");
                        break;
                    case TextMatchMode.Contains:
                        Append(builder, $"text~=\"{Text}\"");
                        break;
                    case TextMatchMode.Regex:
                        Append(builder, $"text=~/{Text}/");
                        break;
                }
            }

            if (Name != null)
                Append(builder, "name=" + Name);

            if (Path != null)
                Append(builder, "path=" + Path);

            if (Type != null)
                Append(builder, "type=" + Type);

            if (Backend != null)
                Append(builder, "backend=" + Backend);

            if (Index.HasValue)
                Append(builder, "[" + Index.Value + "]");

            if (Within != null)
                Append(builder, "within{" + Within + "}");

            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string part)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(part);
        }
    }
}
