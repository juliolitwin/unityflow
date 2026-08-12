using System;
using System.Collections.Generic;
using System.Text;

namespace UnityFlow.Editor.Model
{
    /// <summary>
    /// The variables a flow actually ran with: every name its <c>env:</c> block declares, holding
    /// the caller's override where one was supplied and the flow's own default everywhere else.
    ///
    /// It is a VALUE, computed once before the first step is parsed and never mutated afterwards.
    /// A parameter set that could change between the header record and the last step would make a
    /// report describe a run that never happened, and this same object is what gets written into
    /// the progress header and into the resume ledger for exactly that reason.
    ///
    /// Names are held sorted so that every rendering of an env — the run header, the ledger, the
    /// "defined: ..." tail of a parse error — is byte-identical regardless of the order the pairs
    /// were assembled in. Two runs with the same parameters must produce the same text.
    ///
    /// Values are always STRINGS here. Typing belongs to the argument the variable is substituted
    /// into: <see cref="Yaml.ValueConverter"/> converts the substituted text against the declared
    /// <see cref="FlowArgKind"/>, so <c>timeout: "${wait}"</c> with <c>wait: 5s</c> is a duration
    /// and the same variable used as <c>text:</c> is a string.
    /// </summary>
    public sealed class FlowEnv
    {
        /// <summary>No variables at all: a flow with no <c>env:</c> block and no overrides.</summary>
        public static readonly FlowEnv Empty = new FlowEnv(Array.Empty<string>(), Array.Empty<string>());

        private readonly string[] m_Names;
        private readonly string[] m_Values;

        /// <summary>Declared names, sorted ordinal. Listed verbatim when a substitution names something undefined.</summary>
        public IReadOnlyList<string> Names => m_Names;

        /// <summary>How many variables are defined.</summary>
        public int Count => m_Names.Length;

        private FlowEnv(string[] names, string[] values)
        {
            m_Names = names;
            m_Values = values;
        }

        /// <summary>
        /// Build from name/value pairs. Duplicates throw rather than letting one win, because the
        /// two callers that construct an env — the parser merging defaults with overrides, and
        /// <see cref="TryParsePairs"/> — both detect a duplicate first and report it with the
        /// context needed to fix it. Reaching here with one is a bug in this package.
        /// </summary>
        public static FlowEnv Of(IReadOnlyList<KeyValuePair<string, string>> entries)
        {
            if (entries == null || entries.Count == 0)
                return Empty;

            var names = new string[entries.Count];
            var values = new string[entries.Count];

            for (var i = 0; i < entries.Count; i++)
            {
                names[i] = entries[i].Key;
                values[i] = entries[i].Value;
            }

            Array.Sort(names, values, StringComparer.Ordinal);

            for (var i = 1; i < names.Length; i++)
            {
                if (string.Equals(names[i], names[i - 1], StringComparison.Ordinal))
                    throw new ArgumentException($"'{names[i]}' was supplied twice to FlowEnv.Of; the caller must resolve the duplicate.", nameof(entries));
            }

            return new FlowEnv(names, values);
        }

        /// <summary>Read a variable. False means undefined, which is always an error at the call site.</summary>
        public bool TryGet(string name, out string value)
        {
            // Linear: an env holds a handful of names, and a binary search over that is slower than
            // the comparisons it saves.
            for (var i = 0; i < m_Names.Length; i++)
            {
                if (string.Equals(m_Names[i], name, StringComparison.Ordinal))
                {
                    value = m_Values[i];
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// The env as <c>name=value</c> strings, sorted by name.
        ///
        /// This is the wire form: it is what the CLI accepts, what the resume ledger stores (Unity's
        /// <c>JsonUtility</c> serializes a <c>string[]</c> and would not serialize a dictionary), and
        /// what the run header records. One shape for all three means a resumed segment is
        /// reconstructed from the same text a reader sees in the report.
        /// </summary>
        public string[] ToPairs()
        {
            if (m_Names.Length == 0)
                return Array.Empty<string>();

            var pairs = new string[m_Names.Length];
            var builder = new StringBuilder(32);

            for (var i = 0; i < m_Names.Length; i++)
            {
                builder.Clear();
                builder.Append(m_Names[i]).Append('=').Append(m_Values[i]);
                pairs[i] = builder.ToString();
            }

            return pairs;
        }

        /// <summary>
        /// Read <c>name=value</c> pairs, as given on the command line or read back from a resume
        /// ledger. A malformed pair is named in the error and nothing is dropped: an override the
        /// tool silently ignored would run the flow with parameters nobody asked for, which is the
        /// exact failure this whole surface exists to prevent.
        ///
        /// The value may be empty (<c>character=</c>) and may itself contain '=' — only the FIRST
        /// '=' separates the two.
        /// </summary>
        public static bool TryParsePairs(IReadOnlyList<string> pairs, out FlowEnv env, out string error)
        {
            env = null;

            if (pairs == null || pairs.Count == 0)
            {
                env = Empty;
                error = null;
                return true;
            }

            var entries = new List<KeyValuePair<string, string>>(pairs.Count);

            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];

                if (string.IsNullOrWhiteSpace(pair))
                {
                    error = "an empty --env was given; write it as 'name=value'";
                    return false;
                }

                var separator = pair.IndexOf('=');
                if (separator < 0)
                {
                    error = $"--env '{pair}' is not a 'name=value' pair; it has no '='";
                    return false;
                }

                var name = pair.Substring(0, separator);
                if (!IsName(name))
                {
                    error = name.Length == 0
                        ? $"--env '{pair}' names no variable before its '='"
                        : $"--env '{pair}' has '{name}' as a variable name; use letters, digits, '_' and '.', starting with a letter or '_'";
                    return false;
                }

                for (var j = 0; j < entries.Count; j++)
                {
                    if (!string.Equals(entries[j].Key, name, StringComparison.Ordinal))
                        continue;

                    error = $"--env '{name}' was given twice ('{entries[j].Value}' and '{pair.Substring(separator + 1)}'); " +
                            "which one was meant is not guessable, so neither is used";
                    return false;
                }

                entries.Add(new KeyValuePair<string, string>(name, pair.Substring(separator + 1)));
            }

            env = Of(entries);
            error = null;
            return true;
        }

        /// <summary>
        /// Whether a string may name a variable. Same alphabet as an <c>@reference</c> name, so the
        /// two things a flow author writes with a sigil follow one rule instead of two.
        /// </summary>
        public static bool IsName(string text)
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

        public override string ToString()
        {
            if (m_Names.Length == 0)
                return "(no env)";

            var builder = new StringBuilder(64);
            for (var i = 0; i < m_Names.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(m_Names[i]).Append('=').Append(m_Values[i]);
            }

            return builder.ToString();
        }
    }
}
