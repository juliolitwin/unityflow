using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Yaml
{
    /// <summary>
    /// Turns a parsed <see cref="FlowValue"/> into the CLR value a verb declared it wants.
    ///
    /// Every method returns false with a finished, quotable sentence instead of throwing, because
    /// the caller owns the source path and is the only one that can attach the
    /// <c>path:line:column</c> gutter. Nothing here ever substitutes a default for a value it could
    /// not understand: a value the author wrote and the tool did not understand is always an error.
    ///
    /// <para><b>Duration convention.</b> A duration is written as a number with an optional unit
    /// suffix: <c>500ms</c> is milliseconds, <c>5s</c> and <c>1.5s</c> are seconds, and a BARE
    /// NUMBER IS SECONDS — <c>timeout: 5</c> means five seconds, <c>timeout: 0.5</c> means half a
    /// second. Only lowercase <c>s</c> and <c>ms</c> are units; anything else (<c>5m</c>,
    /// <c>5S</c>, <c>5MS</c>, <c>soon</c>, <c>5 s</c>) is rejected rather than guessed at. The case
    /// rule earns its strictness: <c>5M</c> and <c>5Ms</c> are far likelier to be a reach for
    /// minutes than for milliseconds, and reading them as milliseconds would be a silent
    /// thousand-fold misinterpretation of the author's intent.</para>
    ///
    /// <para><b>Quoting is meaningful.</b> Numeric and boolean arguments must be written unquoted.
    /// <c>seed: "5"</c> is reported, not coerced, because quoting is the author's only way to say
    /// "this is text" and honouring it is what makes <c>text: "42"</c> reliable.</para>
    ///
    /// <para><b>References.</b> A scalar of the form <c>"@name"</c> refers to a value bound by an
    /// earlier step's <c>as:</c>, and cannot be converted at parse time. YAML reserves a leading
    /// <c>@</c> in a plain scalar, so a reference is always written quoted. A literal at-sign is
    /// written by doubling it: <c>"@@handle"</c> is the text <c>@handle</c>.</para>
    ///
    /// One instance is not thread safe; it owns small per-instance lookup tables and is meant to be
    /// held by a single parser.
    /// </summary>
    public sealed class ValueConverter
    {
        private const NumberStyles IntegerStyle = NumberStyles.AllowLeadingSign;
        private const NumberStyles RealStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

        private readonly Dictionary<Type, string[]> m_EnumNames = new Dictionary<Type, string[]>();
        private readonly Dictionary<string, Color> m_ColorNames;
        private readonly NameSuggestion m_Suggest = new NameSuggestion();

        public ValueConverter()
        {
            m_ColorNames = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                { "black", Color.black },
                { "blue", Color.blue },
                { "clear", Color.clear },
                { "cyan", Color.cyan },
                { "gray", Color.gray },
                { "green", Color.green },
                { "grey", Color.grey },
                { "magenta", Color.magenta },
                { "red", Color.red },
                { "white", Color.white },
                { "yellow", Color.yellow }
            };
        }

        /// <summary>
        /// True when the scalar is written as an <c>@name</c> reference rather than a literal.
        /// A doubled at-sign (<c>@@</c>) is an escaped literal and is not a reference.
        /// </summary>
        public bool IsReference(FlowValue value)
        {
            if (value == null || value.Kind != FlowValueKind.Scalar || string.IsNullOrEmpty(value.Scalar))
                return false;

            return value.Scalar[0] == '@' && !(value.Scalar.Length > 1 && value.Scalar[1] == '@');
        }

        /// <summary>
        /// Read the bound name out of an <c>@name</c> reference. Only call this when
        /// <see cref="IsReference"/> said yes; a malformed name is an error, never a literal.
        /// </summary>
        public bool TryReadReference(FlowValue value, string what, out string name, out string error)
        {
            name = null;
            var candidate = value.Scalar.Substring(1);

            if (!IsReferenceName(candidate))
            {
                error = $"'{what}' starts with '@', so it is a reference to a value bound by an earlier 'as:', " +
                        $"but '{candidate}' is not a valid name. Use letters, digits, '_' and '.', or write '@@' for a literal '@'";
                return false;
            }

            name = candidate;
            error = null;
            return true;
        }

        /// <summary>Undo the <c>@@</c> escape so a literal at-sign survives into the value.</summary>
        public string Unescape(string raw)
        {
            if (raw != null && raw.Length > 1 && raw[0] == '@' && raw[1] == '@')
                return raw.Substring(1);

            return raw;
        }

        /// <summary>Convert according to a declared argument.</summary>
        public bool TryConvert(FlowValue value, FlowArgSpec spec, out object result, out string error) =>
            TryConvert(value, spec.Name, spec.Kind, spec.ElementKind, spec.EnumType, out result, out error);

        /// <summary>
        /// Convert a value to <paramref name="kind"/>. <paramref name="what"/> names the thing being
        /// converted and is quoted verbatim into the error message.
        /// <see cref="FlowArgKind.Selector"/> is not handled here — selectors are structural and
        /// belong to <see cref="SelectorParser"/>.
        /// </summary>
        public bool TryConvert(
            FlowValue value,
            string what,
            FlowArgKind kind,
            FlowArgKind elementKind,
            Type enumType,
            out object result,
            out string error)
        {
            result = null;

            switch (kind)
            {
                case FlowArgKind.String:
                {
                    if (!TryParseString(value, what, out var text, out error))
                        return false;

                    result = text;
                    return true;
                }

                case FlowArgKind.Int:
                {
                    if (!TryParseInt(value, what, out var number, out error))
                        return false;

                    result = number;
                    return true;
                }

                case FlowArgKind.Float:
                {
                    if (!TryParseFloat(value, what, out var number, out error))
                        return false;

                    result = number;
                    return true;
                }

                case FlowArgKind.Bool:
                {
                    if (!TryParseBool(value, what, out var flag, out error))
                        return false;

                    result = flag;
                    return true;
                }

                case FlowArgKind.Duration:
                {
                    if (!TryParseDuration(value, what, out var duration, out error))
                        return false;

                    result = duration;
                    return true;
                }

                case FlowArgKind.Vector2:
                {
                    if (!TryParseVector2(value, what, out var vector, out error))
                        return false;

                    result = vector;
                    return true;
                }

                case FlowArgKind.Vector3:
                {
                    if (!TryParseVector3(value, what, out var vector, out error))
                        return false;

                    result = vector;
                    return true;
                }

                case FlowArgKind.Color:
                {
                    if (!TryParseColor(value, what, out var color, out error))
                        return false;

                    result = color;
                    return true;
                }

                case FlowArgKind.Enum:
                    return TryParseEnum(value, what, enumType, out result, out error);

                case FlowArgKind.List:
                    return TryParseList(value, what, elementKind, enumType, out result, out error);

                case FlowArgKind.Any:
                    result = value;
                    error = null;
                    return true;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind),
                        $"{kind} is not convertible by ValueConverter; selectors are built by SelectorParser.");
            }
        }

        /// <summary>Read any scalar as text, with the <c>@@</c> escape resolved.</summary>
        public bool TryParseString(FlowValue value, string what, out string result, out string error)
        {
            result = null;

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects text, got {value.Describe()}";
                return false;
            }

            result = Unescape(value.Scalar);
            error = null;
            return true;
        }

        /// <summary>Read an unquoted whole number.</summary>
        public bool TryParseInt(FlowValue value, string what, out int result, out string error)
        {
            result = 0;

            if (!TryReadNumericScalar(value, what, "a whole number like 3", out var text, out error))
                return false;

            if (!long.TryParse(text, IntegerStyle, CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"'{what}' expects a whole number like 3, got '{text}'";
                return false;
            }

            if (parsed < int.MinValue || parsed > int.MaxValue)
            {
                error = $"'{what}' expects a whole number between {int.MinValue} and {int.MaxValue}, got '{text}'";
                return false;
            }

            result = (int)parsed;
            error = null;
            return true;
        }

        /// <summary>Read an unquoted real number. NaN and infinity are rejected.</summary>
        public bool TryParseFloat(FlowValue value, string what, out float result, out string error)
        {
            result = 0f;

            if (!TryReadNumericScalar(value, what, "a number like 1.5", out var text, out error))
                return false;

            if (!TryReadReal(text, out var parsed))
            {
                error = $"'{what}' expects a number like 1.5, got '{text}'";
                return false;
            }

            var narrowed = (float)parsed;
            if (float.IsNaN(narrowed) || float.IsInfinity(narrowed))
            {
                error = $"'{what}' expects a finite number, got '{text}'";
                return false;
            }

            result = narrowed;
            error = null;
            return true;
        }

        /// <summary>Read an unquoted <c>true</c> or <c>false</c>. YAML's <c>yes</c>/<c>no</c> spellings are rejected on purpose.</summary>
        public bool TryParseBool(FlowValue value, string what, out bool result, out string error)
        {
            result = false;

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects true or false, got {value.Describe()}";
                return false;
            }

            if (value.IsQuoted)
            {
                error = $"'{what}' expects true or false, got the quoted string '{value.Scalar}'; remove the quotes";
                return false;
            }

            if (string.Equals(value.Scalar, "true", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                error = null;
                return true;
            }

            if (string.Equals(value.Scalar, "false", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                error = null;
                return true;
            }

            error = $"'{what}' expects true or false, got '{value.Scalar}'";
            return false;
        }

        /// <summary>
        /// Read a duration. Accepts <c>500ms</c>, <c>5s</c>, <c>1.5s</c> and a bare number of
        /// SECONDS. Units are lowercase; see the type doc for why the case matters. Quoting is
        /// allowed here because a duration is textual by nature.
        /// </summary>
        public bool TryParseDuration(FlowValue value, string what, out TimeSpan result, out string error)
        {
            result = TimeSpan.Zero;

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects a duration like 5s or 500ms, got {value.Describe()}";
                return false;
            }

            var text = value.Scalar;
            string number;
            double millisecondsPerUnit;

            if (EndsWithUnit(text, "ms", StringComparison.Ordinal))
            {
                number = text.Substring(0, text.Length - 2);
                millisecondsPerUnit = 1.0;
            }
            else if (EndsWithUnit(text, "s", StringComparison.Ordinal))
            {
                number = text.Substring(0, text.Length - 1);
                millisecondsPerUnit = 1000.0;
            }
            else
            {
                number = text;
                millisecondsPerUnit = 1000.0;
            }

            if (number.Length == 0 || !TryReadReal(number, out var amount))
            {
                error = $"'{what}' expects a duration like 5s or 500ms, got '{text}'" +
                        (IsWrongCaseUnit(text) ? "; the unit is lowercase 's' or 'ms'" : string.Empty);
                return false;
            }

            if (amount < 0.0)
            {
                error = $"'{what}' expects a duration of zero or more, got '{text}'";
                return false;
            }

            var milliseconds = amount * millisecondsPerUnit;
            if (milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            {
                error = $"'{what}' expects a duration that fits in a TimeSpan, got '{text}'";
                return false;
            }

            result = TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond));
            error = null;
            return true;
        }

        /// <summary>Read a member of <paramref name="enumType"/> by name, case-insensitively.</summary>
        public bool TryParseEnum(FlowValue value, string what, Type enumType, out object result, out string error)
        {
            result = null;

            if (enumType == null || !enumType.IsEnum)
                throw new ArgumentException($"'{what}' was declared as an enum but no enum type was supplied.", nameof(enumType));

            var names = EnumNames(enumType);

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects one of {m_Suggest.Join(names, names.Length)}, got {value.Describe()}";
                return false;
            }

            for (var i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], value.Scalar, StringComparison.OrdinalIgnoreCase))
                {
                    result = Enum.Parse(enumType, names[i], false);
                    error = null;
                    return true;
                }
            }

            error = $"'{what}' expects one of {m_Suggest.Join(names, names.Length)}, got '{value.Scalar}'." +
                    m_Suggest.DidYouMean(value.Scalar, names);
            return false;
        }

        /// <summary>Read two numbers written as <c>[x, y]</c>.</summary>
        public bool TryParseVector2(FlowValue value, string what, out Vector2 result, out string error)
        {
            result = Vector2.zero;
            var numbers = new double[2];

            if (!TryReadNumbers(value, what, "2 numbers like [0, 1]", numbers, out error))
                return false;

            result = new Vector2((float)numbers[0], (float)numbers[1]);
            return true;
        }

        /// <summary>Read three numbers written as <c>[x, y, z]</c>.</summary>
        public bool TryParseVector3(FlowValue value, string what, out Vector3 result, out string error)
        {
            result = Vector3.zero;
            var numbers = new double[3];

            if (!TryReadNumbers(value, what, "3 numbers like [0, 1, 0]", numbers, out error))
                return false;

            result = new Vector3((float)numbers[0], (float)numbers[1], (float)numbers[2]);
            return true;
        }

        /// <summary>
        /// Read a colour written as <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, a list of 3 or 4 components in
        /// the 0..1 range, or one of the names Unity itself defines.
        /// </summary>
        public bool TryParseColor(FlowValue value, string what, out Color result, out string error)
        {
            result = default;

            if (value.Kind == FlowValueKind.List)
            {
                if (value.Items.Count != 3 && value.Items.Count != 4)
                {
                    error = $"'{what}' expects 3 or 4 colour components in the 0..1 range, got {value.Describe()}";
                    return false;
                }

                var components = new double[value.Items.Count];
                for (var i = 0; i < value.Items.Count; i++)
                {
                    if (!TryReadNumericScalar(value.Items[i], what, "a number between 0 and 1", out var text, out error))
                        return false;

                    if (!TryReadReal(text, out components[i]))
                    {
                        error = $"'{what}' expects a number between 0 and 1, got '{text}'";
                        return false;
                    }

                    if (components[i] < 0.0 || components[i] > 1.0)
                    {
                        error = $"'{what}' expects colour components between 0 and 1, got '{text}'; write 0..255 values as #RRGGBB instead";
                        return false;
                    }
                }

                result = components.Length == 3
                    ? new Color((float)components[0], (float)components[1], (float)components[2])
                    : new Color((float)components[0], (float)components[1], (float)components[2], (float)components[3]);

                error = null;
                return true;
            }

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects a colour like #ff8800 or red, got {value.Describe()}";
                return false;
            }

            if (value.Scalar.Length > 0 && value.Scalar[0] == '#')
            {
                if (!ColorUtility.TryParseHtmlString(value.Scalar, out result))
                {
                    error = $"'{what}' expects a hex colour like #ff8800 or #ff8800cc, got '{value.Scalar}'";
                    return false;
                }

                error = null;
                return true;
            }

            if (m_ColorNames.TryGetValue(value.Scalar, out result))
            {
                error = null;
                return true;
            }

            error = $"'{what}' expects a hex colour like #ff8800 or one of {NamedColorList()}, got '{value.Scalar}'";
            return false;
        }

        private bool TryParseList(FlowValue value, string what, FlowArgKind elementKind, Type enumType, out object result, out string error)
        {
            result = null;

            if (value.Kind != FlowValueKind.List)
            {
                error = $"'{what}' expects a list, got {value.Describe()}";
                return false;
            }

            var items = new object[value.Items.Count];
            for (var i = 0; i < value.Items.Count; i++)
            {
                if (!TryConvert(value.Items[i], $"{what}[{i}]", elementKind, FlowArgKind.String, enumType, out items[i], out error))
                    return false;
            }

            result = items;
            error = null;
            return true;
        }

        private static bool TryReadNumbers(FlowValue value, string what, string expectation, double[] into, out string error)
        {
            if (value.Kind != FlowValueKind.List || value.Items.Count != into.Length)
            {
                error = $"'{what}' expects {expectation}, got {value.Describe()}";
                return false;
            }

            for (var i = 0; i < into.Length; i++)
            {
                if (!TryReadNumericScalar(value.Items[i], what, expectation, out var text, out error))
                    return false;

                if (!TryReadReal(text, out into[i]))
                {
                    error = $"'{what}' expects {expectation}, got '{text}'";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool TryReadNumericScalar(FlowValue value, string what, string expectation, out string text, out string error)
        {
            text = null;

            if (value.Kind != FlowValueKind.Scalar)
            {
                error = $"'{what}' expects {expectation}, got {value.Describe()}";
                return false;
            }

            if (value.IsQuoted)
            {
                error = $"'{what}' expects {expectation}, got the quoted string '{value.Scalar}'; remove the quotes";
                return false;
            }

            text = value.Scalar;
            error = null;
            return true;
        }

        private static bool TryReadReal(string text, out double result)
        {
            if (!double.TryParse(text, RealStyle, CultureInfo.InvariantCulture, out result))
                return false;

            return !double.IsNaN(result) && !double.IsInfinity(result);
        }

        /// <summary>
        /// Whether a rejected duration would have been valid had units been matched
        /// case-insensitively. Only asked once the strict read has already failed, so it turns
        /// "got '5S'" into an instruction instead of leaving the author to spot a capital letter.
        /// </summary>
        private static bool IsWrongCaseUnit(string text)
        {
            var unit = EndsWithUnit(text, "ms", StringComparison.OrdinalIgnoreCase) ? 2
                : EndsWithUnit(text, "s", StringComparison.OrdinalIgnoreCase) ? 1
                : 0;

            return unit != 0 && TryReadReal(text.Substring(0, text.Length - unit), out _);
        }

        private static bool EndsWithUnit(string text, string unit, StringComparison comparison)
        {
            if (text.Length <= unit.Length)
                return false;

            return string.Compare(text, text.Length - unit.Length, unit, 0, unit.Length, comparison) == 0;
        }

        private static bool IsReferenceName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            if (!char.IsLetter(text[0]) && text[0] != '_')
                return false;

            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                    return false;
            }

            return true;
        }

        private string[] EnumNames(Type enumType)
        {
            if (!m_EnumNames.TryGetValue(enumType, out var names))
            {
                names = Enum.GetNames(enumType);
                m_EnumNames.Add(enumType, names);
            }

            return names;
        }

        private string NamedColorList()
        {
            var names = new List<string>(m_ColorNames.Count);
            foreach (var pair in m_ColorNames)
                names.Add(pair.Key);

            names.Sort(StringComparer.Ordinal);
            return m_Suggest.Join(names, names.Count);
        }
    }
}
