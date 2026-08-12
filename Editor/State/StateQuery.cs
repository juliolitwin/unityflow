using System;
using System.Globalization;
using System.Text;

namespace UnityFlow.Editor.State
{
    /// <summary>The comparison a query applies to the value it read.</summary>
    public enum StateComparison
    {
        /// <summary>Same as <see cref="Eq"/>, spelled for booleans and enums: <c>is: true</c>.</summary>
        Is,

        /// <summary>Equal.</summary>
        Eq,

        /// <summary>Not equal. A NEGATIVE comparison; see the note on <see cref="StateQuery"/> about stability.</summary>
        Ne,

        /// <summary>Greater than. Numbers only.</summary>
        Gt,

        /// <summary>Greater than or equal. Numbers only.</summary>
        Gte,

        /// <summary>Less than. Numbers only.</summary>
        Lt,

        /// <summary>Less than or equal. Numbers only.</summary>
        Lte,

        /// <summary>Substring, case-insensitive. Text only.</summary>
        Contains,

        /// <summary>Whether the object, or the value, is there at all. Written <c>exists: true</c> or <c>exists: false</c>.</summary>
        Exists
    }

    /// <summary>What a read produced. Chosen at compile time from the member's declared type.</summary>
    public enum StateValueKind
    {
        /// <summary>Nothing was read.</summary>
        Missing,

        /// <summary>Any numeric type, and any enum, widened to <c>double</c>.</summary>
        Number,

        /// <summary><c>bool</c>.</summary>
        Bool,

        /// <summary><c>string</c>.</summary>
        Text,

        /// <summary>A reference of any other class type. Only <see cref="StateComparison.Exists"/> applies to it.</summary>
        Reference
    }

    /// <summary>Which of the four ways of naming state a query uses. Decided once, at parse time.</summary>
    public enum StateQueryMode
    {
        /// <summary>Read a field or property off an object found in the scene.</summary>
        Member,

        /// <summary>Ask only whether the object is there. <c>find</c> and/or <c>component</c> with <c>exists</c>.</summary>
        Existence,

        /// <summary>Count live objects.</summary>
        Count,

        /// <summary>Evaluate a C# expression. The escape hatch.</summary>
        Expression
    }

    /// <summary>
    /// One value read from the running game.
    ///
    /// It is a struct, and every reader is a <c>Func&lt;object, StateValue&gt;</c> compiled once, so
    /// a read on the retry path neither boxes nor allocates. That matters because the retry path
    /// runs EVERY FRAME for the whole of a step's timeout: a five-second waitUntil is roughly three
    /// hundred reads, and a boxing read would put three hundred objects on the heap purely to look
    /// at an int.
    /// </summary>
    public readonly struct StateValue
    {
        /// <summary>What was read.</summary>
        public StateValueKind Kind { get; }

        /// <summary>The value when <see cref="Kind"/> is <see cref="StateValueKind.Number"/>. Enums arrive here as their underlying number.</summary>
        public double Number { get; }

        /// <summary>The value when <see cref="Kind"/> is <see cref="StateValueKind.Bool"/>.</summary>
        public bool Flag { get; }

        /// <summary>The value when <see cref="Kind"/> is <see cref="StateValueKind.Text"/>.</summary>
        public string Text { get; }

        /// <summary>The value when <see cref="Kind"/> is <see cref="StateValueKind.Reference"/>.</summary>
        public object Reference { get; }

        private StateValue(StateValueKind kind, double number, bool flag, string text, object reference)
        {
            Kind = kind;
            Number = number;
            Flag = flag;
            Text = text;
            Reference = reference;
        }

        public static StateValue OfNumber(double value) => new StateValue(StateValueKind.Number, value, false, null, null);

        public static StateValue OfBool(bool value) => new StateValue(StateValueKind.Bool, 0.0, value, null, null);

        public static StateValue OfText(string value) => new StateValue(StateValueKind.Text, 0.0, false, value, null);

        public static StateValue OfReference(object value) => new StateValue(StateValueKind.Reference, 0.0, false, null, value);

        /// <summary>
        /// Entry points for the compiled <c>expr</c> delegate.
        ///
        /// The generated source calls <c>StateValue.From(&lt;the author's expression&gt;)</c> and lets
        /// C# overload resolution classify it AT COMPILE TIME, which is why an <c>expr</c> that
        /// evaluates to an int is read every frame without boxing, exactly like a field read.
        ///
        /// <para>The generic overload is not a convenience, it is a correctness fix. Every
        /// <see cref="UnityEngine.Object"/> defines an implicit <c>operator bool</c>, and by C#'s
        /// better-conversion rules <c>bool</c> beats <c>object</c> as a target — so without a
        /// candidate that binds by IDENTITY, <c>expr: "Camera.main"</c> would silently be read as
        /// the boolean "is it there", and comparing it with anything else would compare the wrong
        /// thing entirely. Constraining the generic to reference types keeps numbers, bools and
        /// enums on their own overloads.</para>
        /// </summary>
        public static StateValue From(double value) => OfNumber(value);

        public static StateValue From(long value) => OfNumber(value);

        public static StateValue From(bool value) => OfBool(value);

        public static StateValue From(string value) => OfText(value);

        /// <summary>
        /// An enum expression is read as its NAME. Its identity is what it is called — an author
        /// writes <c>is: Dead</c>, not <c>is: 2</c> — and an expression has no declared member type
        /// for the resolver to map a name back onto, the way a field query does.
        /// </summary>
        public static StateValue From(Enum value) => OfText(value == null ? null : value.ToString());

        public static StateValue From<T>(T value) where T : class => OfReference(value);

        public static StateValue From(object value) => OfReference(value);

        /// <summary>
        /// Whether the value is absent. A destroyed <see cref="UnityEngine.Object"/> counts as
        /// absent even though its managed reference is not null, because that is what the author
        /// means by "the weapon no longer exists".
        /// </summary>
        public bool IsNull
        {
            get
            {
                if (Kind == StateValueKind.Missing)
                    return true;

                if (Kind == StateValueKind.Text)
                    return Text == null;

                if (Kind != StateValueKind.Reference)
                    return false;

                return Reference is UnityEngine.Object unityObject ? unityObject == null : Reference == null;
            }
        }

        /// <summary>
        /// Render for a failure message. <paramref name="declaredType"/> is the member's declared
        /// type when there is one, so an enum reads as <c>Dead (2)</c> rather than as <c>2</c>.
        /// Allocates, and is therefore only ever called once a step has already failed.
        /// </summary>
        public string Format(Type declaredType)
        {
            switch (Kind)
            {
                case StateValueKind.Number:
                    if (declaredType != null && declaredType.IsEnum)
                    {
                        var name = EnumName(declaredType, Number);
                        return name == null
                            ? Number.ToString("R", CultureInfo.InvariantCulture)
                            : $"{name} ({Number.ToString("R", CultureInfo.InvariantCulture)})";
                    }

                    return Number.ToString("R", CultureInfo.InvariantCulture);

                case StateValueKind.Bool:
                    return Flag ? "true" : "false";

                case StateValueKind.Text:
                    return Text == null ? "null" : $"\"{Text}\"";

                case StateValueKind.Reference:
                    if (IsNull)
                        return Reference is UnityEngine.Object ? "null (destroyed or never assigned)" : "null";

                    return Reference is UnityEngine.Object named
                        ? $"{named.name} ({Reference.GetType().Name})"
                        : $"{Reference} ({Reference.GetType().Name})";

                default:
                    return "nothing";
            }
        }

        /// <summary>Name of an enum member by its numeric value, or null when the value is not a declared member.</summary>
        public static string EnumName(Type enumType, double value)
        {
            var rounded = (long)Math.Round(value);
            if (Math.Abs(value - rounded) > double.Epsilon)
                return null;

            var underlying = Enum.GetUnderlyingType(enumType);
            var boxed = Convert.ChangeType(rounded, underlying, CultureInfo.InvariantCulture);
            return Enum.GetName(enumType, boxed);
        }
    }

    /// <summary>
    /// A declarative question about the running game, validated the moment it is written.
    ///
    /// This is the model only: what to look at and what to compare it with.
    /// <see cref="StateResolver"/> is what turns it into reflection over the live scene. The split
    /// exists so the legality of a query — "you wrote both 'expr' and 'find'", "'contains' needs
    /// something to look for" — is decided in one place with no scene present, and so
    /// <c>flow.probe</c> can share the type and object resolution without dragging the comparison
    /// machinery along.
    ///
    /// <para><b>On negative comparisons.</b> <see cref="StateComparison.Ne"/>,
    /// <c>exists: false</c>, and anything that is true for a moment and then stops being true are
    /// the reason <c>assert</c> accepts <c>stableFor</c>. Consider a networked game client with
    /// client-side prediction: the local simulation applies a hit immediately, the server later
    /// reconciles it, and a value can therefore be right for a single frame and be reverted on the
    /// next. An assertion that returns on the first agreeing frame will happily go green on state
    /// the server never accepted.</para>
    /// </summary>
    public sealed class StateQuery
    {
        /// <summary>GameObject name, or full hierarchy path when it contains '/'. Null when not written.</summary>
        public string Find { get; }

        /// <summary>Component type name, simple or full. Null when not written.</summary>
        public string Component { get; }

        /// <summary>Field or property name. Null when not written.</summary>
        public string Field { get; }

        /// <summary>What to count: a component type name, or a GameObject name or path. Null when not written.</summary>
        public string Count { get; }

        /// <summary>C# expression, compiled once by Roslyn. Null when not written.</summary>
        public string Expr { get; }

        /// <summary>The comparison to apply.</summary>
        public StateComparison Comparison { get; }

        /// <summary>The literal the flow wrote, exactly as written. Coerced to the value's type by <see cref="StateResolver"/>.</summary>
        public string Expected { get; }

        /// <summary>Which of the four ways of naming state this query uses.</summary>
        public StateQueryMode Mode { get; }

        private StateQuery(
            string find, string component, string field, string count, string expr,
            StateComparison comparison, string expected, StateQueryMode mode)
        {
            Find = find;
            Component = component;
            Field = field;
            Count = count;
            Expr = expr;
            Comparison = comparison;
            Expected = expected;
            Mode = mode;
        }

        /// <summary>
        /// Validate a written query and settle its mode, or say precisely what is wrong with it.
        /// Every rejection names what was written and what to write instead; none of them guesses.
        /// </summary>
        public static bool TryCreate(
            string find,
            string component,
            string field,
            string count,
            string expr,
            StateComparison comparison,
            string expected,
            out StateQuery query,
            out string error)
        {
            query = null;

            find = Trimmed(find);
            component = Trimmed(component);
            field = Trimmed(field);
            count = Trimmed(count);
            expr = expr == null || expr.Trim().Length == 0 ? null : expr;

            var groups = 0;
            if (find != null || component != null || field != null) groups++;
            if (count != null) groups++;
            if (expr != null) groups++;

            if (groups == 0)
            {
                error = "a state query must name what to look at: 'find' and/or 'component' with 'field', " +
                        "or 'count', or 'expr'";
                return false;
            }

            if (groups > 1)
            {
                error = $"a state query names one thing at a time, but this one wrote {Written(find, component, field, count, expr)}. " +
                        "Use 'find'/'component'/'field' to read a member, 'count' to count objects, or 'expr' for a C# expression";
                return false;
            }

            if (comparison != StateComparison.Exists && expected == null)
            {
                error = $"'{Keyword(comparison)}' needs a value to compare with";
                return false;
            }

            if (comparison == StateComparison.Exists && !IsBoolLiteral(expected))
            {
                error = $"'exists' expects true or false, got '{expected}'";
                return false;
            }

            StateQueryMode mode;

            if (expr != null)
            {
                mode = StateQueryMode.Expression;
            }
            else if (count != null)
            {
                if (comparison == StateComparison.Exists)
                {
                    error = "'count' always has a value, so 'exists' says nothing about it; compare it with " +
                            "eq, ne, gt, gte, lt or lte instead";
                    return false;
                }

                if (comparison == StateComparison.Contains)
                {
                    error = "'count' is a number, so 'contains' cannot apply to it";
                    return false;
                }

                mode = StateQueryMode.Count;
            }
            else if (field != null)
            {
                mode = StateQueryMode.Member;
            }
            else
            {
                if (comparison != StateComparison.Exists)
                {
                    error = $"'{Keyword(comparison)}' needs a 'field' to read; without one, the only thing that can be " +
                            $"asked about {(find != null ? $"find: {find}" : $"component: {component}")} is 'exists'";
                    return false;
                }

                mode = StateQueryMode.Existence;
            }

            query = new StateQuery(find, component, field, count, expr, comparison, expected, mode);
            error = null;
            return true;
        }

        /// <summary>The query as the author wrote it, for the head of a failure message.</summary>
        public string Describe()
        {
            var builder = new StringBuilder();

            if (Expr != null)
                builder.Append("expr: ").Append(Expr);

            if (Count != null)
                builder.Append("count: ").Append(Count);

            if (Find != null)
                builder.Append("find: ").Append(Find);

            if (Component != null)
            {
                if (builder.Length > 0)
                    builder.Append(", ");

                builder.Append("component: ").Append(Component);
            }

            if (Field != null)
            {
                if (builder.Length > 0)
                    builder.Append(", ");

                builder.Append("field: ").Append(Field);
            }

            return builder.ToString();
        }

        /// <summary>
        /// The comparison as a phrase: <c>&gt; 0</c>, <c>is true</c>, <c>contains "gold"</c>.
        /// <paramref name="quoteExpected"/> is set once the value turned out to be text, so a
        /// failure reads <c>is "Untagged", expected == "MainCamera"</c> and the two sides of the
        /// comparison are written the same way.
        /// </summary>
        public string DescribeComparison(bool quoteExpected = false)
        {
            var expected = quoteExpected ? $"\"{Expected}\"" : Expected;

            switch (Comparison)
            {
                case StateComparison.Is: return $"is {expected}";
                case StateComparison.Eq: return $"== {expected}";
                case StateComparison.Ne: return $"!= {expected}";
                case StateComparison.Gt: return $"> {expected}";
                case StateComparison.Gte: return $">= {expected}";
                case StateComparison.Lt: return $"< {expected}";
                case StateComparison.Lte: return $"<= {expected}";
                case StateComparison.Contains: return $"contains \"{Expected}\"";
                default: return $"exists {Expected}";
            }
        }

        /// <summary>The YAML key a comparison is written as.</summary>
        public static string Keyword(StateComparison comparison)
        {
            switch (comparison)
            {
                case StateComparison.Is: return "is";
                case StateComparison.Eq: return "eq";
                case StateComparison.Ne: return "ne";
                case StateComparison.Gt: return "gt";
                case StateComparison.Gte: return "gte";
                case StateComparison.Lt: return "lt";
                case StateComparison.Lte: return "lte";
                case StateComparison.Contains: return "contains";
                default: return "exists";
            }
        }

        /// <summary>Whether the comparison orders two values, and therefore needs numbers.</summary>
        public static bool IsOrdering(StateComparison comparison) =>
            comparison == StateComparison.Gt || comparison == StateComparison.Gte ||
            comparison == StateComparison.Lt || comparison == StateComparison.Lte;

        private static bool IsBoolLiteral(string text) =>
            string.Equals(text, "true", StringComparison.Ordinal) ||
            string.Equals(text, "false", StringComparison.Ordinal);

        private static string Trimmed(string text)
        {
            if (text == null)
                return null;

            var trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string Written(string find, string component, string field, string count, string expr)
        {
            var builder = new StringBuilder();

            Append(builder, "find", find);
            Append(builder, "component", component);
            Append(builder, "field", field);
            Append(builder, "count", count);
            Append(builder, "expr", expr);

            return builder.ToString();
        }

        private static void Append(StringBuilder builder, string key, string value)
        {
            if (value == null)
                return;

            if (builder.Length > 0)
                builder.Append(" and ");

            builder.Append('\'').Append(key).Append('\'');
        }
    }
}
