using System;
using System.Collections.Generic;
using System.Text;

namespace UnityFlow.Editor.Model
{
    /// <summary>Shape of a parsed YAML value, with its source position preserved.</summary>
    public enum FlowValueKind
    {
        /// <summary>Written as nothing, <c>~</c> or <c>null</c>.</summary>
        Null,

        /// <summary>A single scalar.</summary>
        Scalar,

        /// <summary>A sequence.</summary>
        List,

        /// <summary>A mapping.</summary>
        Map
    }

    /// <summary>One key/value pair of a <see cref="FlowValueKind.Map"/>, with the key's own position.</summary>
    public sealed class FlowEntry
    {
        /// <summary>The key text.</summary>
        public string Key { get; }

        /// <summary>1-based line the key was written on. Errors about an unknown key point here, not at the value.</summary>
        public int KeyLine { get; }

        /// <summary>1-based column the key was written at.</summary>
        public int KeyColumn { get; }

        /// <summary>The value bound to the key.</summary>
        public FlowValue Value { get; }

        public FlowEntry(string key, int keyLine, int keyColumn, FlowValue value)
        {
            Key = key;
            KeyLine = keyLine;
            KeyColumn = keyColumn;
            Value = value;
        }
    }

    /// <summary>
    /// A YAML value reduced to what UnityFlow needs: shape, raw text, quoting, and position.
    ///
    /// The parser converts YamlDotNet's representation model into this once, then never touches
    /// YamlDotNet again. That keeps a third-party type out of the public model, and it lets every
    /// downstream error carry a line and column without threading a <c>Mark</c> around.
    ///
    /// Quoting is preserved because it is the only unambiguous signal a YAML author has to say
    /// "this is text, not a number": <c>seed: 5</c> is an integer, <c>seed: "5"</c> is a mistake
    /// worth reporting rather than silently coercing.
    /// </summary>
    public sealed class FlowValue
    {
        /// <summary>Shape of this value.</summary>
        public FlowValueKind Kind { get; }

        /// <summary>Raw scalar text. Null unless <see cref="Kind"/> is <see cref="FlowValueKind.Scalar"/>.</summary>
        public string Scalar { get; }

        /// <summary>True when the scalar was quoted or written as a block scalar, i.e. explicitly text.</summary>
        public bool IsQuoted { get; }

        /// <summary>
        /// True when the scalar was written as a BLOCK scalar (<c>|</c> or <c>&gt;</c>).
        ///
        /// It is recorded because such a node's <see cref="Line"/> is the line of the indicator,
        /// while its text begins on the NEXT line with its indentation already stripped. Anything
        /// reporting a position inside the text — a bad <c>${variable}</c> in a thirty-line
        /// <c>runScript</c> body — is off by one without it, and a parse error that names the wrong
        /// line is worse than one that names no line at all.
        /// </summary>
        public bool IsBlock { get; }

        /// <summary>Sequence items. Empty unless <see cref="Kind"/> is <see cref="FlowValueKind.List"/>.</summary>
        public IReadOnlyList<FlowValue> Items { get; }

        /// <summary>Mapping entries in source order. Empty unless <see cref="Kind"/> is <see cref="FlowValueKind.Map"/>.</summary>
        public IReadOnlyList<FlowEntry> Entries { get; }

        /// <summary>1-based line.</summary>
        public int Line { get; }

        /// <summary>1-based column.</summary>
        public int Column { get; }

        private FlowValue(
            FlowValueKind kind,
            string scalar,
            bool isQuoted,
            bool isBlock,
            IReadOnlyList<FlowValue> items,
            IReadOnlyList<FlowEntry> entries,
            int line,
            int column)
        {
            Kind = kind;
            Scalar = scalar;
            IsQuoted = isQuoted;
            IsBlock = isBlock;
            Items = items ?? Array.Empty<FlowValue>();
            Entries = entries ?? Array.Empty<FlowEntry>();
            Line = line;
            Column = column;
        }

        public static FlowValue Null(int line, int column) =>
            new FlowValue(FlowValueKind.Null, null, false, false, null, null, line, column);

        public static FlowValue OfScalar(string text, bool isQuoted, int line, int column, bool isBlock = false) =>
            new FlowValue(FlowValueKind.Scalar, text, isQuoted, isBlock, null, null, line, column);

        public static FlowValue OfList(IReadOnlyList<FlowValue> items, int line, int column) =>
            new FlowValue(FlowValueKind.List, null, false, false, items, null, line, column);

        public static FlowValue OfMap(IReadOnlyList<FlowEntry> entries, int line, int column) =>
            new FlowValue(FlowValueKind.Map, null, false, false, null, entries, line, column);

        /// <summary>Find a mapping entry by exact key. False for any non-mapping value.</summary>
        public bool TryGetEntry(string key, out FlowEntry entry)
        {
            for (var i = 0; i < Entries.Count; i++)
            {
                if (string.Equals(Entries[i].Key, key, StringComparison.Ordinal))
                {
                    entry = Entries[i];
                    return true;
                }
            }

            entry = null;
            return false;
        }

        /// <summary>Human phrase for the tail of an error message: "nothing", "'soon'", "a list of 3 items", "a mapping".</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case FlowValueKind.Null:
                    return "nothing";
                case FlowValueKind.Scalar:
                    return $"'{Scalar}'";
                case FlowValueKind.List:
                    return Items.Count == 1 ? "a list of 1 item" : $"a list of {Items.Count} items";
                default:
                    return "a mapping";
            }
        }

        public override string ToString() => Describe();
    }

    /// <summary>
    /// The type a verb expects for one of its arguments. The parser converts and validates against
    /// this at parse time, which is what makes <c>duration: soon</c> fail in milliseconds instead
    /// of halfway through a run.
    /// </summary>
    public enum FlowArgKind
    {
        /// <summary>Any scalar, taken as text.</summary>
        String,

        /// <summary>Whole number, written unquoted.</summary>
        Int,

        /// <summary>Real number, written unquoted. NaN and infinity are rejected.</summary>
        Float,

        /// <summary>Literal <c>true</c> or <c>false</c>, written unquoted.</summary>
        Bool,

        /// <summary>A duration: <c>5s</c>, <c>500ms</c>, <c>1.5s</c>, or a bare number of seconds.</summary>
        Duration,

        /// <summary>A nested selector, written as a mapping or as the text shorthand.</summary>
        Selector,

        /// <summary>Two numbers, written as <c>[x, y]</c>.</summary>
        Vector2,

        /// <summary>Three numbers, written as <c>[x, y, z]</c>.</summary>
        Vector3,

        /// <summary>A colour: <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>[r, g, b]</c>, <c>[r, g, b, a]</c> or a known name.</summary>
        Color,

        /// <summary>A member of <see cref="FlowArgSpec.EnumType"/>, matched by name, case-insensitively.</summary>
        Enum,

        /// <summary>A sequence whose items are each of <see cref="FlowArgSpec.ElementKind"/>. Converted to <c>object[]</c>.</summary>
        List,

        /// <summary>
        /// Left as the raw <see cref="FlowValue"/>. For verbs that bind their own arguments — the
        /// vocabulary is then responsible for reporting a bad value with the value's own position.
        /// </summary>
        Any
    }

    /// <summary>Whether a verb accepts, requires, or has no inline selector.</summary>
    public enum SelectorMode
    {
        /// <summary>The verb takes no selector; any selector key in its arguments is an error.</summary>
        None,

        /// <summary>The verb may be given a selector.</summary>
        Optional,

        /// <summary>The verb is meaningless without a selector.</summary>
        Required
    }

    /// <summary>One declared argument of a verb.</summary>
    public sealed class FlowArgSpec
    {
        /// <summary>Key as written in YAML. Case-sensitive.</summary>
        public string Name { get; }

        /// <summary>Type the parser converts the value to.</summary>
        public FlowArgKind Kind { get; }

        /// <summary>When true, omitting the argument is a parse error.</summary>
        public bool Required { get; }

        /// <summary>Item type. Only meaningful when <see cref="Kind"/> is <see cref="FlowArgKind.List"/>.</summary>
        public FlowArgKind ElementKind { get; }

        /// <summary>Enum type. Required when <see cref="Kind"/> or <see cref="ElementKind"/> is <see cref="FlowArgKind.Enum"/>.</summary>
        public Type EnumType { get; }

        /// <summary>One-line help, surfaced when the argument is missing or mistyped.</summary>
        public string Description { get; }

        public FlowArgSpec(
            string name,
            FlowArgKind kind,
            bool required = false,
            FlowArgKind elementKind = FlowArgKind.String,
            Type enumType = null,
            string description = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("An argument spec needs a name.", nameof(name));

            var needsEnum = kind == FlowArgKind.Enum || (kind == FlowArgKind.List && elementKind == FlowArgKind.Enum);
            if (needsEnum && (enumType == null || !enumType.IsEnum))
                throw new ArgumentException($"Argument '{name}' is declared as an enum but no enum type was supplied.", nameof(enumType));

            // A selector is structural: it is built by SelectorParser, which owns the source path
            // and can therefore report file:line:column. A list ELEMENT never reaches that parser,
            // so this declaration used to surface as an unpositioned ArgumentOutOfRangeException
            // from deep inside the value converter — a parse error the author cannot act on.
            // Rejecting the declaration keeps every reachable parse failure positioned.
            if (kind == FlowArgKind.List && elementKind == FlowArgKind.Selector)
            {
                throw new ArgumentException(
                    $"Argument '{name}' is declared as a list of selectors, which the parser cannot build: " +
                    "selectors come from SelectorParser, not from the value converter. Declare a single " +
                    "Selector argument per selector, or take the list as FlowArgKind.Any and parse it in the verb.",
                    nameof(elementKind));
            }

            Name = name;
            Kind = kind;
            Required = required;
            ElementKind = elementKind;
            EnumType = enumType;
            Description = description;
        }
    }

    /// <summary>
    /// Everything the parser needs to know about one verb. Supplied by the runner, never hardcoded
    /// in the parser, so built-in verbs and project <c>[FlowCommand]</c> methods are validated by
    /// exactly the same code path.
    /// </summary>
    public sealed class FlowVerbSpec
    {
        private readonly FlowArgSpec[] m_Args;

        /// <summary>Verb name as written in YAML. Case-sensitive.</summary>
        public string Name { get; }

        /// <summary>Declared arguments, in the order the vocabulary listed them.</summary>
        public IReadOnlyList<FlowArgSpec> Args => m_Args;

        /// <summary>Whether the verb's argument mapping may also carry selector keys.</summary>
        public SelectorMode Selector { get; }

        /// <summary>
        /// Argument filled when the verb is written with a bare scalar (<c>screenshot: shot-name</c>).
        /// Null means a bare scalar is only legal when <see cref="Selector"/> is not
        /// <see cref="SelectorMode.None"/>, where it is the selector text shorthand.
        /// </summary>
        public string BareScalarArg { get; }

        /// <summary>One-line help, surfaced by tooling and in "unknown verb" messages.</summary>
        public string Description { get; }

        public FlowVerbSpec(
            string name,
            IReadOnlyList<FlowArgSpec> args,
            SelectorMode selector = SelectorMode.None,
            string bareScalarArg = null,
            string description = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A verb spec needs a name.", nameof(name));

            m_Args = args == null ? Array.Empty<FlowArgSpec>() : ToArray(args);

            for (var i = 0; i < m_Args.Length; i++)
            {
                if (IsStepModifier(m_Args[i].Name))
                {
                    throw new ArgumentException(
                        $"Verb '{name}' declares an argument named '{m_Args[i].Name}', which is a step modifier every verb already accepts. Rename it.",
                        nameof(args));
                }
            }

            if (bareScalarArg != null && !Contains(m_Args, bareScalarArg))
            {
                throw new ArgumentException(
                    $"Verb '{name}' names '{bareScalarArg}' as its bare-scalar argument but does not declare it.",
                    nameof(bareScalarArg));
            }

            Name = name;
            Selector = selector;
            BareScalarArg = bareScalarArg;
            Description = description;
        }

        /// <summary>Look up a declared argument by exact name.</summary>
        public bool TryGetArg(string name, out FlowArgSpec spec)
        {
            for (var i = 0; i < m_Args.Length; i++)
            {
                if (string.Equals(m_Args[i].Name, name, StringComparison.Ordinal))
                {
                    spec = m_Args[i];
                    return true;
                }
            }

            spec = null;
            return false;
        }

        /// <summary>
        /// The three keys every step RESERVES, which therefore can never be a verb's own argument.
        /// Reserved is not the same as accepted: 'on' is refused by the parser on a verb whose
        /// <see cref="Selector"/> mode is <see cref="SelectorMode.None"/>, because there is no
        /// selector for it to scope.
        /// </summary>
        public static bool IsStepModifier(string name) =>
            string.Equals(name, "timeout", StringComparison.Ordinal) ||
            string.Equals(name, "on", StringComparison.Ordinal) ||
            string.Equals(name, "as", StringComparison.Ordinal);

        private static FlowArgSpec[] ToArray(IReadOnlyList<FlowArgSpec> args)
        {
            var copy = new FlowArgSpec[args.Count];
            for (var i = 0; i < args.Count; i++)
                copy[i] = args[i];

            return copy;
        }

        private static bool Contains(FlowArgSpec[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i].Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The set of verbs a flow may use. The parser is GIVEN one of these rather than owning a list,
    /// because the legal vocabulary is only known at run time: it is the built-in verbs plus every
    /// <see cref="UnityFlow.FlowCommandAttribute"/> method discovered in the project.
    /// </summary>
    public interface IFlowVerbVocabulary
    {
        /// <summary>All known verb names, used to build "Did you mean ...?" for a typo.</summary>
        IReadOnlyList<string> VerbNames { get; }

        /// <summary>Look up a verb by exact name.</summary>
        bool TryGetVerb(string name, out FlowVerbSpec spec);
    }

    /// <summary>One argument of a step after parsing: converted, positioned, and traceable to its source text.</summary>
    public sealed class FlowArgument
    {
        /// <summary>Key as written.</summary>
        public string Name { get; }

        /// <summary>Kind the verb declared for this argument.</summary>
        public FlowArgKind Kind { get; }

        /// <summary>
        /// Converted value, boxed: <c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c>,
        /// <c>TimeSpan</c>, <see cref="Selector"/>, <c>Vector2</c>, <c>Vector3</c>, <c>Color</c>,
        /// the enum value, <c>object[]</c>, or the raw <see cref="FlowValue"/> for
        /// <see cref="FlowArgKind.Any"/>. Null when <see cref="IsReference"/>.
        /// </summary>
        public object Value { get; }

        /// <summary>
        /// Name bound by an earlier step's <c>as:</c>, when the value was written as <c>"@sword"</c>.
        /// The parser cannot convert it because the value does not exist until the run reaches it.
        /// </summary>
        public string Reference { get; }

        /// <summary>The value exactly as it was written, kept so the runner can report a resolution failure in place.</summary>
        public FlowValue Raw { get; }

        /// <summary>True when the value is an unresolved <c>@name</c> reference.</summary>
        public bool IsReference => Reference != null;

        /// <summary>1-based line of the value.</summary>
        public int Line => Raw.Line;

        /// <summary>1-based column of the value.</summary>
        public int Column => Raw.Column;

        public FlowArgument(string name, FlowArgKind kind, object value, string reference, FlowValue raw)
        {
            Name = name;
            Kind = kind;
            Value = value;
            Reference = reference;
            Raw = raw;
        }

        public override string ToString() => IsReference ? $"{Name}=@{Reference}" : $"{Name}={Value}";
    }

    /// <summary>
    /// One executable line of a flow, immutable after parse.
    ///
    /// A step is always "a verb, its arguments, and where it came from". The three modifiers every
    /// verb accepts — <c>timeout</c>, <c>on</c> and <c>as</c> — are lifted out of the argument
    /// mapping into first-class properties, so the runner never has to know that they were written
    /// alongside the verb's own arguments.
    /// </summary>
    public sealed class FlowStep
    {
        private readonly FlowArgument[] m_Args;

        /// <summary>Verb name, already checked against the vocabulary.</summary>
        public string Verb { get; }

        /// <summary>Declared arguments that were actually supplied, in source order.</summary>
        public IReadOnlyList<FlowArgument> Args => m_Args;

        /// <summary>
        /// Selector written inline in the verb's argument mapping (<c>tapOn: { text: "Shop" }</c>).
        /// Null when the verb takes no selector or none was written.
        /// </summary>
        public Selector Selector { get; }

        /// <summary>
        /// Scope written as <c>on:</c>, restricting where the step's selector resolves. Null when
        /// absent, and never set for a verb that takes no selector — the parser rejects <c>on:</c>
        /// there rather than carrying a scope nothing would apply.
        /// </summary>
        public Selector On { get; }

        /// <summary>Per-step deadline written as <c>timeout:</c>. Null means the run's default applies.</summary>
        public TimeSpan? Timeout { get; }

        /// <summary>Name written as <c>as:</c>, binding this step's result for later <c>"@name"</c> references. Null when absent.</summary>
        public string As { get; }

        /// <summary>1-based line the step starts on.</summary>
        public int Line { get; }

        /// <summary>1-based column the step starts at.</summary>
        public int Column { get; }

        /// <summary>
        /// File the step was written in: the flow's own path, or the sub-flow's when
        /// <c>runFlow</c> brought the step in. Null only for a step built by hand rather than parsed.
        ///
        /// A sub-flow's steps keep the line and column they have in THEIR file, so without this a
        /// failure report would point at that line number in the parent — a position that either
        /// does not exist or, worse, holds an unrelated step.
        /// </summary>
        public string SourcePath { get; }

        public FlowStep(
            string verb,
            IReadOnlyList<FlowArgument> args,
            Selector selector,
            Selector on,
            TimeSpan? timeout,
            string bindAs,
            int line,
            int column,
            string sourcePath = null)
        {
            m_Args = new FlowArgument[args == null ? 0 : args.Count];
            for (var i = 0; i < m_Args.Length; i++)
                m_Args[i] = args[i];

            Verb = verb;
            Selector = selector;
            On = on;
            Timeout = timeout;
            As = bindAs;
            Line = line;
            Column = column;
            SourcePath = sourcePath;
        }

        /// <summary>Whether an argument was supplied.</summary>
        public bool Has(string name) => TryGetArg(name, out _);

        /// <summary>Look up a supplied argument by exact name.</summary>
        public bool TryGetArg(string name, out FlowArgument argument)
        {
            for (var i = 0; i < m_Args.Length; i++)
            {
                if (string.Equals(m_Args[i].Name, name, StringComparison.Ordinal))
                {
                    argument = m_Args[i];
                    return true;
                }
            }

            argument = null;
            return false;
        }

        /// <summary>
        /// Read a converted argument. Throws when the argument is absent, is still an unresolved
        /// reference, or is not of the requested type — all three are runner bugs, because the
        /// vocabulary already told the parser what this verb expects.
        /// </summary>
        public T Get<T>(string name)
        {
            if (!TryGetArg(name, out var argument))
                throw new InvalidOperationException($"Step '{Verb}' has no argument '{name}'.");

            if (argument.IsReference)
                throw new InvalidOperationException($"Argument '{name}' of step '{Verb}' is the unresolved reference '@{argument.Reference}'.");

            if (!(argument.Value is T typed))
            {
                var actual = argument.Value == null ? "null" : argument.Value.GetType().Name;
                throw new InvalidOperationException($"Argument '{name}' of step '{Verb}' is {actual}, not {typeof(T).Name}.");
            }

            return typed;
        }

        /// <summary>Read a converted argument, or report that it was not supplied as that type.</summary>
        public bool TryGet<T>(string name, out T value)
        {
            if (TryGetArg(name, out var argument) && !argument.IsReference && argument.Value is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public override string ToString()
        {
            var builder = new StringBuilder(Verb);

            if (Selector != null)
                builder.Append(' ').Append(Selector);

            for (var i = 0; i < m_Args.Length; i++)
                builder.Append(' ').Append(m_Args[i]);

            if (On != null)
                builder.Append(" on ").Append(On);

            return builder.ToString();
        }
    }
}
