using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Yaml
{
    /// <summary>
    /// Turns flow YAML into a <see cref="FlowDocument"/>, or into a <see cref="FlowParseException"/>
    /// that names the file, the line, the column and what was expected.
    ///
    /// It reads YamlDotNet's REPRESENTATION MODEL (<c>YamlStream</c> / <c>YamlNode</c>) rather than
    /// its object deserializer, because every node in that model carries a <c>Mark</c> — the
    /// line/column of the token it came from. An object deserializer would produce the same data
    /// and lose every position, and a position is what separates "something is wrong with your
    /// flow" from "line 9, column 18, 'timeout' wants a duration".
    ///
    /// The legal verbs are supplied through <see cref="IFlowVerbVocabulary"/>, never hardcoded
    /// here, so built-in verbs and project <c>[FlowCommand]</c> methods are validated identically.
    /// Validation is exhaustive and eager: unknown keys, wrong shapes and bad values all fail
    /// before a single step runs, which is the only way a typo in a parameter name can fail in
    /// milliseconds instead of halfway through a run.
    ///
    /// <para><b>Step shape.</b> A step is a verb name on its own, or exactly one
    /// <c>verb: arguments</c> pair. The three modifiers every verb accepts — <c>timeout</c>,
    /// <c>on</c> and <c>as</c> — are written inside that argument mapping, next to the verb's own
    /// arguments, which is why a two-key step mapping is reported rather than accepted.</para>
    ///
    /// <para><b>Argument precedence.</b> When a verb accepts an inline selector, its argument
    /// mapping holds both the verb's arguments and the selector's keys. A key that the verb
    /// declares always binds to the verb. That is what makes
    /// <c>inputText: { testId: login.account, text: "demo" }</c> mean "type demo into
    /// login.account" instead of "find the element whose visible text is demo"; select by visible
    /// text with <c>on:</c> when a verb shadows a selector key this way.</para>
    ///
    /// <para><b>Variables.</b> The document is read in two passes. The first builds the tree and
    /// reads <c>env:</c>, whose defaults are merged with the caller's overrides; the second
    /// substitutes <c>${name}</c> into every string scalar of everything else, before a single key
    /// is interpreted. Steps therefore reach validation already parameterized, which is what lets a
    /// misspelled variable fail here — with a position — instead of ninety seconds into a run.
    /// See <see cref="FlowInterpolator"/> for the escaping and quoting rules.</para>
    ///
    /// <para><b>Sub-flows.</b> <c>runFlow</c> is resolved HERE and never reaches the runner: the
    /// sub-flow is read, validated and its steps are spliced into the parent's list in place of the
    /// step. That is what keeps everything downstream working unchanged — one flat step list means
    /// one progress stream, one run folder, one set of step indices, and therefore a resume ledger
    /// and a domain-reload rebuild that need no notion of nesting at all. Doing it at parse time is
    /// also what makes a missing file or a bad sub-flow fail in milliseconds instead of mid-run.</para>
    ///
    /// An instance owns its converters and is not thread safe; construct one per parsing thread.
    /// </summary>
    public sealed class FlowParser
    {
        /// <summary>Nesting limit. Well past anything a hand-written flow needs, and it stops a pathological alias graph.</summary>
        private const int MaxDepth = 64;

        /// <summary>
        /// Node budget. YAML aliases are expanded into an independent subtree here, so a document
        /// that aliases an anchor into another anchor grows exponentially. The budget turns that
        /// from a hang into a parse error.
        ///
        /// It is shared by the parent and every sub-flow of one parse for the same reason: a fan of
        /// includes multiplies just as an alias graph does, and a limit that reset per file would
        /// bound nothing.
        /// </summary>
        private const int MaxNodes = 50000;

        /// <summary>
        /// How deep <c>runFlow</c> may nest. A cycle is caught exactly by the include stack and
        /// reported as a cycle; this is the separate, blunter guard for a legal but absurd chain,
        /// and it is generous — a library of flows three or four deep is already unusual.
        /// </summary>
        private const int MaxIncludeDepth = 16;

        /// <summary>The verb the parser resolves itself instead of handing to the runner.</summary>
        internal const string RunFlowVerb = "runFlow";

        private readonly ValueConverter m_Converter = new ValueConverter();
        private readonly SelectorParser m_SelectorParser;
        private readonly NameSuggestion m_Suggest = new NameSuggestion();
        private readonly FlowInterpolator m_Interpolator = new FlowInterpolator();
        private readonly IFlowFileSystem m_Files;

        /// <summary>Absolute paths of the sub-flows currently being expanded, outermost first. A repeat is a cycle.</summary>
        private readonly List<string> m_IncludeStack = new List<string>();

        /// <summary>Every file this parse read, parent first. Handed to the document for the resume hash.</summary>
        private readonly List<string> m_SourceFiles = new List<string>();

        /// <summary>Remaining node budget for the whole parse, parent and sub-flows together.</summary>
        private int m_NodeBudget;

        /// <summary>The strongest <c>requires.input</c> any sub-flow declared, and the file that declared it.</summary>
        private InputRequirement m_IncludedInput;
        private string m_IncludedInputSource;

        private readonly string[] m_RootKeys =
        {
            "name", "requires", "timeScale", "seed", "env", "defs", "before", "steps", "after"
        };

        private readonly string[] m_SubFlowKeys = { "name", "requires", "env", "defs", "steps" };
        private readonly string[] m_RequiresKeys = { "input" };
        private readonly string[] m_InputRequirements = { "system", "semantic" };
        private readonly string[] m_StepModifiers = { "timeout", "on", "as" };

        /// <param name="files">
        /// Where <c>runFlow</c> reads sub-flows from. Omitted, references resolve against the Unity
        /// project root, which is what the CLI's own <c>--file</c> does.
        /// </param>
        public FlowParser(IFlowFileSystem files = null)
        {
            m_SelectorParser = new SelectorParser(m_Converter);
            m_Files = files ?? new ProjectFlowFileSystem();
        }

        /// <summary>Read and parse a flow file. The path is kept verbatim so error gutters match what the caller passed in.</summary>
        /// <param name="overrides">
        /// Variables supplied by the caller, beating the flow's own <c>env:</c> defaults. A name the
        /// flow does not declare is a parse error rather than a new variable: an override the flow
        /// never reads is a typo every time, and accepting it silently is how a run ends up using
        /// the default nobody wanted.
        /// </param>
        public FlowDocument ParseFile(string path, IFlowVerbVocabulary vocabulary, FlowEnv overrides = null)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("A flow file needs a path.", nameof(path));

            // Through the same file system a sub-flow is read with, so a parse is a pure function of
            // what that file system holds — which is what makes the whole include mechanism testable
            // without a project on disk.
            return Parse(m_Files.ReadAllText(path), path, vocabulary, overrides);
        }

        /// <summary>Parse flow YAML that is already in memory.</summary>
        public FlowDocument Parse(string yaml, string sourcePath, IFlowVerbVocabulary vocabulary, FlowEnv overrides = null)
        {
            if (yaml == null)
                throw new ArgumentNullException(nameof(yaml));

            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentException("A flow needs a source path so errors can point at a file.", nameof(sourcePath));

            if (vocabulary == null)
                throw new ArgumentNullException(nameof(vocabulary));

            m_Interpolator.Reset();
            m_IncludeStack.Clear();
            m_SourceFiles.Clear();
            m_SourceFiles.Add(sourcePath);
            m_NodeBudget = MaxNodes;
            m_IncludedInput = InputRequirement.Unspecified;
            m_IncludedInputSource = null;

            var root = LoadRoot(yaml, sourcePath);
            var env = ReadEnv(root, sourcePath, overrides ?? FlowEnv.Empty);

            return ReadDocument(Interpolate(root, env, sourcePath), sourcePath, vocabulary, env);
        }

        /// <summary>
        /// Read one flow file's text into a positioned tree, with every check that is about the FILE
        /// rather than about what a flow means. Shared by the top-level parse and by every sub-flow,
        /// so a <c>runFlow</c> target is held to the same standard as the flow that ran it.
        /// </summary>
        private FlowValue LoadRoot(string yaml, string sourcePath)
        {
            var stream = new YamlStream();
            try
            {
                using (var reader = new StringReader(yaml))
                    stream.Load(reader);
            }
            catch (YamlException ex)
            {
                // Keep the scanner's own exception: its stack is the only thing that can tell a
                // malformed document apart from a bug in YamlDotNet itself.
                throw new FlowParseException(sourcePath, LineOf(ex.Start), ColumnOf(ex.Start), $"invalid YAML: {FirstLine(ex.Message)}", ex);
            }

            if (stream.Documents.Count == 0)
            {
                throw new FlowParseException(
                    sourcePath, 1, 1,
                    "the file is empty; a flow needs at least 'name' and 'steps'");
            }

            if (stream.Documents.Count > 1)
            {
                var second = stream.Documents[1].RootNode;
                throw new FlowParseException(
                    sourcePath, LineOf(second.Start), ColumnOf(second.Start),
                    $"a flow file must hold exactly one YAML document, found {stream.Documents.Count}; remove the '---' separator or split the file");
            }

            var rootNode = stream.Documents[0].RootNode;
            if (!(rootNode is YamlMappingNode))
            {
                throw new FlowParseException(
                    sourcePath, LineOf(rootNode.Start), ColumnOf(rootNode.Start),
                    "a flow file must be a mapping with 'name' and 'steps' at the top level");
            }

            return Build(rootNode, sourcePath, 0);
        }

        /// <summary>
        /// Substitute every root entry EXCEPT <c>env:</c> itself.
        ///
        /// The env block is skipped because its defaults have already been read and resolved, and
        /// running them through substitution again would either re-expand a value that happened to
        /// contain a dollar-brace or, worse, quietly define a variable in terms of another with no
        /// defined order between them.
        /// </summary>
        private FlowValue Interpolate(FlowValue root, FlowEnv env, string sourcePath)
        {
            var entries = new List<FlowEntry>(root.Entries.Count);

            for (var i = 0; i < root.Entries.Count; i++)
            {
                var entry = root.Entries[i];

                entries.Add(string.Equals(entry.Key, "env", StringComparison.Ordinal)
                    ? entry
                    : new FlowEntry(entry.Key, entry.KeyLine, entry.KeyColumn, m_Interpolator.Apply(entry.Value, env, sourcePath)));
            }

            return FlowValue.OfMap(entries, root.Line, root.Column);
        }

        /// <summary>
        /// Read the <c>env:</c> defaults and lay the caller's overrides on top.
        ///
        /// Both directions are validated: a default that is not a scalar is refused where it was
        /// written, and an override naming something the flow does not declare is refused at the env
        /// block. The second is the one that earns its keep — <c>--env charater=X</c> against a flow
        /// declaring <c>character</c> would otherwise run the whole flow with the default and report
        /// a pass for parameters nobody asked for.
        /// </summary>
        private FlowEnv ReadEnv(FlowValue root, string sourcePath, FlowEnv overrides)
        {
            root.TryGetEntry("env", out var entry);

            var line = entry?.KeyLine ?? root.Line;
            var column = entry?.KeyColumn ?? root.Column;
            var defaults = new List<KeyValuePair<string, string>>();

            if (entry != null)
            {
                if (entry.Value.Kind != FlowValueKind.Map)
                {
                    throw new FlowParseException(
                        sourcePath, entry.Value.Line, entry.Value.Column,
                        $"'env' expects a mapping of variable defaults like {{ account: demo }}, got {entry.Value.Describe()}");
                }

                for (var i = 0; i < entry.Value.Entries.Count; i++)
                {
                    var variable = entry.Value.Entries[i];

                    if (!FlowEnv.IsName(variable.Key))
                    {
                        throw new FlowParseException(
                            sourcePath, variable.KeyLine, variable.KeyColumn,
                            $"'{variable.Key}' is not a variable name; use letters, digits, '_' and '.', starting with a letter or '_'");
                    }

                    // A null default is written as an empty value, and an empty default is a real
                    // choice — 'character: ""' means "no preference". It is read as the empty string
                    // rather than refused, and a step that cannot work with an empty value is the
                    // place to say so.
                    string text;
                    if (variable.Value.Kind == FlowValueKind.Null)
                    {
                        text = string.Empty;
                    }
                    else if (variable.Value.Kind == FlowValueKind.Scalar)
                    {
                        // Taken verbatim, apart from the dollar-brace escape. A default is substituted
                        // as TEXT and the substituted text is then read by the ordinary argument
                        // rules, so '@@' still means a literal at-sign there exactly as it would have
                        // had the value been typed into the step — one rule, applied in one place.
                        text = m_Interpolator.ReadDefault(variable.Value.Scalar, variable.Value, variable.Key, sourcePath);
                    }
                    else
                    {
                        throw new FlowParseException(
                            sourcePath, variable.Value.Line, variable.Value.Column,
                            $"the default for 'env.{variable.Key}' must be a single value, got {variable.Value.Describe()}; " +
                            "a variable is substituted into text and has no other shape");
                    }

                    defaults.Add(new KeyValuePair<string, string>(variable.Key, text));
                }
            }

            for (var i = 0; i < overrides.Names.Count; i++)
            {
                var name = overrides.Names[i];
                var declared = false;

                for (var j = 0; j < defaults.Count; j++)
                {
                    if (!string.Equals(defaults[j].Key, name, StringComparison.Ordinal))
                        continue;

                    overrides.TryGet(name, out var value);
                    defaults[j] = new KeyValuePair<string, string>(name, value);
                    declared = true;
                    break;
                }

                if (declared)
                    continue;

                var known = new List<string>(defaults.Count);
                for (var j = 0; j < defaults.Count; j++)
                    known.Add(defaults[j].Key);

                throw new FlowParseException(
                    sourcePath, line, column,
                    known.Count == 0
                        ? $"'{name}' was supplied as a variable but this flow declares no 'env:' block, so nothing reads it"
                        : $"'{name}' was supplied as a variable but this flow's 'env:' block does not declare it." +
                          m_Suggest.DidYouMeanOrList(name, known, "variables", known.Count));
            }

            return FlowEnv.Of(defaults);
        }

        private FlowDocument ReadDocument(FlowValue root, string sourcePath, IFlowVerbVocabulary vocabulary, FlowEnv env)
        {
            string name = null;
            var requires = FlowRequirements.Unspecified;
            float? timeScale = null;
            int? seed = null;
            List<FlowStep> before = null;
            List<FlowStep> steps = null;
            List<FlowStep> after = null;
            FlowEntry stepsEntry = null;

            for (var i = 0; i < root.Entries.Count; i++)
            {
                var entry = root.Entries[i];

                switch (entry.Key)
                {
                    case "name":
                        name = ReadNonEmptyString(entry, sourcePath, "name");
                        break;

                    case "requires":
                        requires = ReadRequirements(entry, sourcePath);
                        break;

                    case "timeScale":
                        timeScale = ReadTimeScale(entry, sourcePath);
                        break;

                    case "seed":
                        seed = ReadInt(entry, sourcePath, "seed");
                        break;

                    case "env":
                        // Already read and validated by ReadEnv, before anything was substituted.
                        break;

                    case "defs":
                        // Exists purely so a flow can host YAML anchors it reuses further down.
                        // Never executed and deliberately never validated.
                        break;

                    case "before":
                        before = ReadStepList(entry, sourcePath, vocabulary, "before");
                        break;

                    case "steps":
                        stepsEntry = entry;
                        steps = ReadStepList(entry, sourcePath, vocabulary, "steps");
                        break;

                    case "after":
                        after = ReadStepList(entry, sourcePath, vocabulary, "after");
                        break;

                    default:
                        throw new FlowParseException(
                            sourcePath, entry.KeyLine, entry.KeyColumn,
                            $"unknown top-level key '{entry.Key}'.{m_Suggest.DidYouMeanOrList(entry.Key, m_RootKeys, "keys", m_RootKeys.Length)}");
                }
            }

            if (name == null)
            {
                throw new FlowParseException(
                    sourcePath, root.Line, root.Column,
                    "'name' is required and must be a non-empty string");
            }

            if (steps == null)
            {
                throw new FlowParseException(
                    sourcePath, root.Line, root.Column,
                    "'steps' is required and must be a list");
            }

            if (steps.Count == 0)
            {
                throw new FlowParseException(
                    sourcePath, stepsEntry.KeyLine, stepsEntry.KeyColumn,
                    "'steps' must contain at least one step");
            }

            requires = MergeIncludedRequirements(requires, sourcePath, root);

            return new FlowDocument(sourcePath, name, requires, timeScale, seed, before, steps, after, env, m_SourceFiles);
        }

        /// <summary>
        /// Fold what the sub-flows demand of the input path into what the parent demands.
        ///
        /// A sub-flow that declares <c>requires: { input: system }</c> means it, and dropping that
        /// because the parent said nothing would let the whole run fall back to synthesized events
        /// and report a pass worth less than the reader thinks. Two flows demanding DIFFERENT things
        /// is not resolvable by preferring one, so it is refused and both files are named.
        /// </summary>
        private FlowRequirements MergeIncludedRequirements(FlowRequirements requires, string sourcePath, FlowValue root)
        {
            if (m_IncludedInput == InputRequirement.Unspecified)
                return requires;

            if (requires.Input == InputRequirement.Unspecified)
                return new FlowRequirements(m_IncludedInput);

            if (requires.Input == m_IncludedInput)
                return requires;

            root.TryGetEntry("requires", out var entry);

            throw new FlowParseException(
                sourcePath, entry?.KeyLine ?? root.Line, entry?.KeyColumn ?? root.Column,
                $"this flow declares 'requires: {{ input: {Spell(requires.Input)} }}' but the sub-flow " +
                $"{m_IncludedInputSource} it runs declares 'input: {Spell(m_IncludedInput)}'. One run has one input " +
                "mechanism, so these cannot both be honoured; make them agree.");
        }

        private static string Spell(InputRequirement input) => input.ToString().ToLowerInvariant();

        /// <summary>
        /// Read one section's steps, resolving <c>runFlow</c> into the list as it goes so the
        /// caller only ever sees a flat list of executable steps.
        /// </summary>
        private List<FlowStep> ReadStepList(FlowEntry entry, string sourcePath, IFlowVerbVocabulary vocabulary, string section)
        {
            if (entry.Value.Kind != FlowValueKind.List)
            {
                var detail = section == "steps"
                    ? "'steps' is required and must be a list"
                    : $"'{section}' must be a list of steps";

                throw new FlowParseException(sourcePath, entry.KeyLine, entry.KeyColumn, detail);
            }

            var steps = new List<FlowStep>(entry.Value.Items.Count);
            AppendSteps(entry.Value, sourcePath, vocabulary, steps);
            return steps;
        }

        /// <summary>Append every step of a step LIST to <paramref name="steps"/>, expanding sub-flows in place.</summary>
        private void AppendSteps(FlowValue list, string sourcePath, IFlowVerbVocabulary vocabulary, List<FlowStep> steps)
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                var step = ReadStep(list.Items[i], sourcePath, vocabulary);

                if (string.Equals(step.Verb, RunFlowVerb, StringComparison.Ordinal))
                    ExpandSubFlow(step, sourcePath, vocabulary, steps);
                else
                    steps.Add(step);
            }
        }

        private FlowStep ReadStep(FlowValue value, string sourcePath, IFlowVerbVocabulary vocabulary)
        {
            string verb;
            FlowValue args;
            int verbLine;
            int verbColumn;

            switch (value.Kind)
            {
                case FlowValueKind.Scalar:
                    verb = value.Scalar;
                    args = null;
                    verbLine = value.Line;
                    verbColumn = value.Column;
                    break;

                case FlowValueKind.Map:
                    if (value.Entries.Count == 0)
                    {
                        throw new FlowParseException(
                            sourcePath, value.Line, value.Column,
                            "a step must name a verb, e.g. 'tapOn: { text: \"Shop\" }'");
                    }

                    if (value.Entries.Count > 1)
                    {
                        var offending = value.Entries[1];
                        throw new FlowParseException(
                            sourcePath, offending.KeyLine, offending.KeyColumn,
                            $"a step is a single 'verb: arguments' mapping, but this one also has '{offending.Key}'. " +
                            "Step modifiers such as 'timeout', 'on' and 'as' belong inside the verb's argument mapping");
                    }

                    verb = value.Entries[0].Key;
                    args = value.Entries[0].Value;
                    verbLine = value.Entries[0].KeyLine;
                    verbColumn = value.Entries[0].KeyColumn;
                    break;

                default:
                    throw new FlowParseException(
                        sourcePath, value.Line, value.Column,
                        $"a step is either a verb name or a 'verb: arguments' mapping, got {value.Describe()}");
            }

            if (string.IsNullOrEmpty(verb))
            {
                throw new FlowParseException(
                    sourcePath, verbLine, verbColumn,
                    "a step must name a verb, e.g. 'tapOn: { text: \"Shop\" }'");
            }

            if (!vocabulary.TryGetVerb(verb, out var spec))
            {
                throw new FlowParseException(
                    sourcePath, verbLine, verbColumn,
                    $"unknown step verb '{verb}'.{m_Suggest.DidYouMeanOrList(verb, vocabulary.VerbNames, "verbs", 12)}");
            }

            return BuildStep(spec, args, sourcePath, verbLine, verbColumn);
        }

        private FlowStep BuildStep(FlowVerbSpec spec, FlowValue args, string sourcePath, int verbLine, int verbColumn)
        {
            var arguments = new List<FlowArgument>();
            var selectorEntries = new List<FlowEntry>();
            Selector selector = null;
            Selector on = null;
            TimeSpan? timeout = null;
            string bindAs = null;

            if (args != null && args.Kind != FlowValueKind.Null)
            {
                switch (args.Kind)
                {
                    case FlowValueKind.Scalar:
                        if (spec.BareScalarArg != null)
                        {
                            spec.TryGetArg(spec.BareScalarArg, out var bareSpec);
                            arguments.Add(ReadArgument(bareSpec, args, sourcePath));
                        }
                        else if (spec.Selector != SelectorMode.None)
                        {
                            selector = m_SelectorParser.Parse(args, sourcePath, spec.Name);
                        }
                        else
                        {
                            throw new FlowParseException(
                                sourcePath, args.Line, args.Column,
                                $"'{spec.Name}' expects a mapping of arguments, got {args.Describe()}");
                        }

                        break;

                    case FlowValueKind.Map:
                        for (var i = 0; i < args.Entries.Count; i++)
                        {
                            var entry = args.Entries[i];

                            if (string.Equals(entry.Key, "timeout", StringComparison.Ordinal))
                            {
                                timeout = ReadDuration(entry, sourcePath, "timeout");
                            }
                            else if (string.Equals(entry.Key, "on", StringComparison.Ordinal))
                            {
                                // 'on' scopes where the step's selector resolves, and a verb with
                                // SelectorMode.None has no selector to scope. Accepting the key and
                                // dropping it is the same failure as silently taking the first
                                // match: the author asked for something the run never does.
                                if (spec.Selector == SelectorMode.None)
                                {
                                    throw new FlowParseException(
                                        sourcePath, entry.KeyLine, entry.KeyColumn,
                                        $"'{spec.Name}' takes no selector, so 'on' has nothing to scope and would be ignored. Remove it");
                                }

                                on = m_SelectorParser.Parse(entry.Value, sourcePath, "on");
                            }
                            else if (string.Equals(entry.Key, "as", StringComparison.Ordinal))
                            {
                                bindAs = ReadBindName(entry, sourcePath);
                            }
                            else if (spec.TryGetArg(entry.Key, out var argSpec))
                            {
                                arguments.Add(ReadArgument(argSpec, entry.Value, sourcePath));
                            }
                            else if (spec.Selector != SelectorMode.None && m_SelectorParser.IsSelectorKey(entry.Key))
                            {
                                selectorEntries.Add(entry);
                            }
                            else
                            {
                                throw UnknownArgument(spec, entry, sourcePath);
                            }
                        }

                        if (selectorEntries.Count > 0)
                            selector = m_SelectorParser.FromEntries(selectorEntries, args, SelectorForm.Mapping, sourcePath, spec.Name);

                        break;

                    default:
                        throw new FlowParseException(
                            sourcePath, args.Line, args.Column,
                            $"'{spec.Name}' expects a mapping of arguments, got {args.Describe()}");
                }
            }

            if (spec.Selector == SelectorMode.Required && selector == null)
            {
                throw new FlowParseException(
                    sourcePath, verbLine, verbColumn,
                    $"'{spec.Name}' needs a selector, written either as the text shorthand \"Shop\" or as a mapping like {{ testId: shop.root }}");
            }

            for (var i = 0; i < spec.Args.Count; i++)
            {
                var argSpec = spec.Args[i];
                if (!argSpec.Required || Contains(arguments, argSpec.Name))
                    continue;

                var help = string.IsNullOrEmpty(argSpec.Description) ? string.Empty : $" ({argSpec.Description})";
                throw new FlowParseException(
                    sourcePath, verbLine, verbColumn,
                    $"'{spec.Name}' requires '{argSpec.Name}'{help}");
            }

            return new FlowStep(spec.Name, arguments, selector, on, timeout, bindAs, verbLine, verbColumn, sourcePath);
        }

        // ---- runFlow -----------------------------------------------------------------------

        /// <summary>
        /// Read the sub-flow a <c>runFlow</c> step names and splice its steps into
        /// <paramref name="steps"/>.
        ///
        /// Everything that can be wrong is settled here, before the parent has finished parsing:
        /// the file is missing, the flow inside it does not parse, it declares something a sub-flow
        /// cannot honour, or it eventually runs itself. That is the whole point of resolving
        /// includes at parse time — a run that is going to fail because of a typo in a path must
        /// fail in milliseconds, not after entering play mode.
        /// </summary>
        private void ExpandSubFlow(FlowStep step, string parentPath, IFlowVerbVocabulary vocabulary, List<FlowStep> steps)
        {
            var reference = step.Get<string>("file");

            string absolute;
            try
            {
                absolute = m_Files.Resolve(reference);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' does not name a file that can be opened: {ex.Message}");
            }

            var cycle = IndexInStack(absolute);
            if (cycle >= 0)
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' runs a flow that is already running it: " +
                    $"{DescribeCycle(absolute)}. A sub-flow is spliced into its parent, so a cycle is not a loop " +
                    "that eventually stops — it is an infinite step list.");
            }

            if (m_IncludeStack.Count >= MaxIncludeDepth)
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' is {m_IncludeStack.Count + 1} sub-flows deep, past the limit of " +
                    $"{MaxIncludeDepth}: {DescribeCycle(absolute)}");
            }

            if (!m_Files.Exists(absolute))
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' names no file: there is nothing at {absolute}. " +
                    "A runFlow path is resolved against the PROJECT ROOT, the same way the CLI's --file is, " +
                    "not against the folder of the flow that wrote it.");
            }

            string text;
            try
            {
                text = m_Files.ReadAllText(absolute);
            }
            catch (IOException ex)
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' could not be read: {ex.Message}");
            }

            if (!ContainsPath(m_SourceFiles, absolute))
                m_SourceFiles.Add(absolute);

            var root = LoadRoot(text, absolute);
            var overrides = ReadSubFlowOverrides(step, parentPath, reference, root, absolute);
            var env = ReadEnv(root, absolute, overrides);
            var interpolated = Interpolate(root, env, absolute);

            var stepsEntry = ReadSubFlowRoot(interpolated, absolute, reference, parentPath, step);

            m_IncludeStack.Add(absolute);
            try
            {
                if (stepsEntry.Value.Kind != FlowValueKind.List)
                {
                    throw new FlowParseException(
                        absolute, stepsEntry.KeyLine, stepsEntry.KeyColumn,
                        "'steps' is required and must be a list");
                }

                if (stepsEntry.Value.Items.Count == 0)
                {
                    throw new FlowParseException(
                        absolute, stepsEntry.KeyLine, stepsEntry.KeyColumn,
                        "'steps' must contain at least one step");
                }

                AppendSteps(stepsEntry.Value, absolute, vocabulary, steps);
            }
            finally
            {
                m_IncludeStack.RemoveAt(m_IncludeStack.Count - 1);
            }
        }

        /// <summary>
        /// Validate the sub-flow's top level and return its <c>steps</c> entry.
        ///
        /// A sub-flow may declare less than a flow: <c>before</c> and <c>after</c> are refused
        /// because their whole meaning is "setup and TEARDOWN of a run" — an <c>after</c> spliced
        /// into the middle of the parent's body would stop running when the body fails, which is the
        /// one guarantee it exists to give. <c>timeScale</c> and <c>seed</c> are refused because the
        /// runner reads them from the document alone, so a sub-flow's would be silently dropped.
        /// </summary>
        private FlowEntry ReadSubFlowRoot(FlowValue root, string absolute, string reference, string parentPath, FlowStep step)
        {
            FlowEntry stepsEntry = null;
            var named = false;

            for (var i = 0; i < root.Entries.Count; i++)
            {
                var entry = root.Entries[i];

                switch (entry.Key)
                {
                    case "name":
                        ReadNonEmptyString(entry, absolute, "name");
                        named = true;
                        break;

                    case "requires":
                        RecordIncludedRequirement(ReadRequirements(entry, absolute), absolute, entry);
                        break;

                    case "env":
                    case "defs":
                        break;

                    case "steps":
                        stepsEntry = entry;
                        break;

                    case "before":
                    case "after":
                        throw new FlowParseException(
                            absolute, entry.KeyLine, entry.KeyColumn,
                            $"a flow run with '{RunFlowVerb}' cannot declare '{entry.Key}': its steps are spliced into the " +
                            "middle of the parent's list, where 'before' has nothing left to precede and 'after' would " +
                            "stop being teardown — it would be skipped exactly when the run fails, which is when teardown " +
                            "matters. Move these steps into 'steps', or keep this flow as a top-level flow.");

                    case "timeScale":
                    case "seed":
                        throw new FlowParseException(
                            absolute, entry.KeyLine, entry.KeyColumn,
                            $"a flow run with '{RunFlowVerb}' cannot declare '{entry.Key}': it applies to a whole run, and " +
                            "the run is the parent's. Declare it in the flow that is started.");

                    default:
                        throw new FlowParseException(
                            absolute, entry.KeyLine, entry.KeyColumn,
                            $"unknown top-level key '{entry.Key}'.{m_Suggest.DidYouMeanOrList(entry.Key, m_SubFlowKeys, "keys", m_SubFlowKeys.Length)}");
                }
            }

            if (!named)
            {
                throw new FlowParseException(
                    absolute, root.Line, root.Column,
                    "'name' is required and must be a non-empty string");
            }

            if (stepsEntry == null)
            {
                throw new FlowParseException(
                    parentPath, step.Line, step.Column,
                    $"'{RunFlowVerb}: {reference}' names a flow with no 'steps', so it contributes nothing");
            }

            return stepsEntry;
        }

        private void RecordIncludedRequirement(FlowRequirements requirements, string absolute, FlowEntry entry)
        {
            if (requirements.Input == InputRequirement.Unspecified)
                return;

            if (m_IncludedInput != InputRequirement.Unspecified && m_IncludedInput != requirements.Input)
            {
                throw new FlowParseException(
                    absolute, entry.KeyLine, entry.KeyColumn,
                    $"this sub-flow declares 'input: {Spell(requirements.Input)}' but {m_IncludedInputSource} — another " +
                    $"sub-flow of the same run — declares 'input: {Spell(m_IncludedInput)}'. One run has one input " +
                    "mechanism, so these cannot both be honoured.");
            }

            m_IncludedInput = requirements.Input;
            m_IncludedInputSource = absolute;
        }

        /// <summary>
        /// Read the variables the parent supplies to the sub-flow.
        ///
        /// A name the sub-flow does not declare is REFUSED, at the parent's own key, exactly as
        /// <c>--env</c> is refused against a flow that declares nothing by that name: an override
        /// nobody reads is a typo every time, and accepting it silently is how a run ends up using
        /// the default nobody wanted. The check is made here rather than left to
        /// <see cref="ReadEnv"/> so the position it reports is the line the author actually wrote.
        /// </summary>
        private FlowEnv ReadSubFlowOverrides(FlowStep step, string parentPath, string reference, FlowValue subRoot, string absolute)
        {
            if (!step.TryGetArg("env", out var argument))
                return FlowEnv.Empty;

            var value = (FlowValue)argument.Value;

            if (value.Kind == FlowValueKind.Null)
                return FlowEnv.Empty;

            if (value.Kind != FlowValueKind.Map)
            {
                throw new FlowParseException(
                    parentPath, value.Line, value.Column,
                    $"'{RunFlowVerb}' expects 'env' to be a mapping of variables for the sub-flow, like " +
                    $"{{ character: \"${{character}}\" }}, got {value.Describe()}");
            }

            var declared = DeclaredNames(subRoot);
            var entries = new List<KeyValuePair<string, string>>(value.Entries.Count);

            for (var i = 0; i < value.Entries.Count; i++)
            {
                var supplied = value.Entries[i];

                if (!declared.Contains(supplied.Key))
                {
                    throw new FlowParseException(
                        parentPath, supplied.KeyLine, supplied.KeyColumn,
                        $"'{reference}' declares no variable '{supplied.Key}', so supplying it would change nothing." +
                        (declared.Count == 0
                            ? $" That flow has no 'env:' block at all ({absolute})."
                            : m_Suggest.DidYouMeanOrList(supplied.Key, declared, "variables", declared.Count)));
                }

                if (ContainsName(entries, supplied.Key))
                {
                    throw new FlowParseException(
                        parentPath, supplied.KeyLine, supplied.KeyColumn,
                        $"'{supplied.Key}' is supplied twice to '{reference}'; which one was meant is not guessable");
                }

                // A supplied value is taken verbatim, exactly like a --env value: the parent's own
                // '${...}' have already been substituted into it by the time it reaches here, and
                // substituting a second time would make the text's meaning depend on the parent's
                // variable values.
                string text;
                if (supplied.Value.Kind == FlowValueKind.Null)
                {
                    text = string.Empty;
                }
                else if (supplied.Value.Kind == FlowValueKind.Scalar)
                {
                    text = supplied.Value.Scalar;
                }
                else
                {
                    throw new FlowParseException(
                        parentPath, supplied.Value.Line, supplied.Value.Column,
                        $"the value supplied for '{supplied.Key}' must be a single value, got {supplied.Value.Describe()}; " +
                        "a variable is substituted into text and has no other shape");
                }

                entries.Add(new KeyValuePair<string, string>(supplied.Key, text));
            }

            return FlowEnv.Of(entries);
        }

        /// <summary>The variable names a flow's <c>env:</c> block declares, read before any substitution.</summary>
        private static List<string> DeclaredNames(FlowValue root)
        {
            var names = new List<string>();

            if (!root.TryGetEntry("env", out var entry) || entry.Value.Kind != FlowValueKind.Map)
                return names;

            for (var i = 0; i < entry.Value.Entries.Count; i++)
                names.Add(entry.Value.Entries[i].Key);

            return names;
        }

        private int IndexInStack(string absolute)
        {
            for (var i = 0; i < m_IncludeStack.Count; i++)
            {
                if (SamePath(m_IncludeStack[i], absolute))
                    return i;
            }

            return -1;
        }

        private string DescribeCycle(string absolute)
        {
            var chain = new StringBuilder(128);
            chain.Append(m_SourceFiles.Count > 0 ? m_SourceFiles[0] : "<flow>");

            for (var i = 0; i < m_IncludeStack.Count; i++)
                chain.Append(" -> ").Append(m_IncludeStack[i]);

            return chain.Append(" -> ").Append(absolute).ToString();
        }

        /// <summary>
        /// Whether two resolved paths name the same file. Ordinal-insensitive because this package
        /// runs on Windows and macOS, whose file systems are case-insensitive by default; treating
        /// two spellings of one path as two files would defeat the cycle check outright.
        /// </summary>
        private static bool SamePath(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool ContainsPath(List<string> values, string value)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (SamePath(values[i], value))
                    return true;
            }

            return false;
        }

        private static bool ContainsName(List<KeyValuePair<string, string>> entries, string key)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Key, key, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private FlowArgument ReadArgument(FlowArgSpec spec, FlowValue value, string sourcePath)
        {
            if (spec.Kind == FlowArgKind.Selector)
            {
                var nested = m_SelectorParser.Parse(value, sourcePath, spec.Name);
                return new FlowArgument(spec.Name, spec.Kind, nested, null, value);
            }

            // FlowArgKind.Any keeps the raw value, references included, because the vocabulary that
            // declared it has taken responsibility for binding it.
            if (spec.Kind != FlowArgKind.Any && m_Converter.IsReference(value))
            {
                if (!m_Converter.TryReadReference(value, spec.Name, out var reference, out var referenceError))
                    throw Rejected(value, sourcePath, referenceError);

                return new FlowArgument(spec.Name, spec.Kind, null, reference, value);
            }

            if (!m_Converter.TryConvert(value, spec, out var converted, out var error))
                throw Rejected(value, sourcePath, error);

            return new FlowArgument(spec.Name, spec.Kind, converted, null, value);
        }

        /// <summary>
        /// A value the converter refused, positioned, and naming what it was written as when a
        /// variable was substituted into it.
        ///
        /// Without the tail, <c>'timeout' expects a duration like 5s or 500ms, got 'soon'</c> points
        /// at a line where the word 'soon' does not appear, and the author goes looking for a typo
        /// in the wrong file. The flow said <c>"${wait}"</c>; the value came from --env or from the
        /// env block, and the message has to say so.
        /// </summary>
        private FlowParseException Rejected(FlowValue value, string sourcePath, string error) =>
            new FlowParseException(sourcePath, value.Line, value.Column, error + m_Interpolator.DescribeSubstitution(value));

        private FlowParseException UnknownArgument(FlowVerbSpec spec, FlowEntry entry, string sourcePath)
        {
            var known = new List<string>(spec.Args.Count + m_StepModifiers.Length + m_SelectorParser.Keys.Count);

            for (var i = 0; i < spec.Args.Count; i++)
                known.Add(spec.Args[i].Name);

            if (spec.Selector != SelectorMode.None)
            {
                for (var i = 0; i < m_SelectorParser.Keys.Count; i++)
                    known.Add(m_SelectorParser.Keys[i]);
            }

            for (var i = 0; i < m_StepModifiers.Length; i++)
                known.Add(m_StepModifiers[i]);

            return new FlowParseException(
                sourcePath, entry.KeyLine, entry.KeyColumn,
                $"'{spec.Name}' has no argument '{entry.Key}'.{m_Suggest.DidYouMeanOrList(entry.Key, known, "arguments", known.Count)}");
        }

        private FlowRequirements ReadRequirements(FlowEntry entry, string sourcePath)
        {
            if (entry.Value.Kind != FlowValueKind.Map)
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'requires' expects a mapping like {{ input: system }}, got {entry.Value.Describe()}");
            }

            var input = InputRequirement.Unspecified;

            for (var i = 0; i < entry.Value.Entries.Count; i++)
            {
                var requirement = entry.Value.Entries[i];

                if (!string.Equals(requirement.Key, "input", StringComparison.Ordinal))
                {
                    throw new FlowParseException(
                        sourcePath, requirement.KeyLine, requirement.KeyColumn,
                        $"'requires' has no key '{requirement.Key}'.{m_Suggest.DidYouMeanOrList(requirement.Key, m_RequiresKeys, "keys", m_RequiresKeys.Length)}");
                }

                var text = ReadNonEmptyString(requirement, sourcePath, "requires.input");

                if (string.Equals(text, "system", StringComparison.Ordinal))
                    input = InputRequirement.System;
                else if (string.Equals(text, "semantic", StringComparison.Ordinal))
                    input = InputRequirement.Semantic;
                else
                    throw new FlowParseException(
                        sourcePath, requirement.Value.Line, requirement.Value.Column,
                        $"'requires.input' expects 'system' (real device injection) or 'semantic' (synthesized UI events), got '{text}'." +
                        m_Suggest.DidYouMean(text, m_InputRequirements));
            }

            return new FlowRequirements(input);
        }

        private float ReadTimeScale(FlowEntry entry, string sourcePath)
        {
            if (!m_Converter.TryParseFloat(entry.Value, "timeScale", out var value, out var error))
                throw Rejected(entry.Value, sourcePath, error);

            if (value <= 0f)
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'timeScale' must be greater than 0, got {value}");
            }

            return value;
        }

        private int ReadInt(FlowEntry entry, string sourcePath, string what)
        {
            if (!m_Converter.TryParseInt(entry.Value, what, out var value, out var error))
                throw Rejected(entry.Value, sourcePath, error);

            return value;
        }

        private TimeSpan ReadDuration(FlowEntry entry, string sourcePath, string what)
        {
            if (!m_Converter.TryParseDuration(entry.Value, what, out var value, out var error))
                throw Rejected(entry.Value, sourcePath, error);

            return value;
        }

        private string ReadNonEmptyString(FlowEntry entry, string sourcePath, string what)
        {
            if (!m_Converter.TryParseString(entry.Value, what, out var text, out var error))
                throw Rejected(entry.Value, sourcePath, error);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw Rejected(entry.Value, sourcePath,
                    text.Length == 0
                        ? $"'{what}' must not be empty"
                        : $"'{what}' must not be only whitespace");
            }

            return text;
        }

        private string ReadBindName(FlowEntry entry, string sourcePath)
        {
            var text = ReadNonEmptyString(entry, sourcePath, "as");

            if (text[0] == '@')
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'as' expects a plain name like sword, got '{text}'; the '@' belongs only where the name is used again");
            }

            if (!IsBindName(text))
            {
                throw new FlowParseException(
                    sourcePath, entry.Value.Line, entry.Value.Column,
                    $"'as' expects a name of letters, digits and '_' starting with a letter, got '{text}'");
            }

            return text;
        }

        private FlowValue Build(YamlNode node, string sourcePath, int depth)
        {
            if (--m_NodeBudget < 0)
            {
                throw new FlowParseException(
                    sourcePath, LineOf(node.Start), ColumnOf(node.Start),
                    $"the flow expands to more than {MaxNodes} nodes; an alias is very likely being expanded into itself");
            }

            if (depth > MaxDepth)
            {
                throw new FlowParseException(
                    sourcePath, LineOf(node.Start), ColumnOf(node.Start),
                    $"the flow nests more than {MaxDepth} levels deep");
            }

            var line = LineOf(node.Start);
            var column = ColumnOf(node.Start);

            if (node is YamlScalarNode scalar)
            {
                var quoted = scalar.Style != ScalarStyle.Plain;
                if (!quoted && IsNullText(scalar.Value))
                    return FlowValue.Null(line, column);

                // A block scalar's mark is its '|' or '>' indicator, not its text: the text starts on
                // the next line. Recorded so a failure inside a long runScript body can name the line
                // the author actually wrote the mistake on.
                var block = scalar.Style == ScalarStyle.Literal || scalar.Style == ScalarStyle.Folded;

                return FlowValue.OfScalar(scalar.Value, quoted, line, column, block);
            }

            if (node is YamlSequenceNode sequence)
            {
                var items = new FlowValue[sequence.Children.Count];
                for (var i = 0; i < sequence.Children.Count; i++)
                    items[i] = Build(sequence.Children[i], sourcePath, depth + 1);

                return FlowValue.OfList(items, line, column);
            }

            if (node is YamlMappingNode mapping)
            {
                var entries = new List<FlowEntry>(mapping.Children.Count);
                foreach (var pair in mapping.Children)
                {
                    if (!(pair.Key is YamlScalarNode key))
                    {
                        throw new FlowParseException(
                            sourcePath, LineOf(pair.Key.Start), ColumnOf(pair.Key.Start),
                            "a key must be a plain name; complex mapping keys are not part of the flow format");
                    }

                    entries.Add(new FlowEntry(
                        key.Value,
                        LineOf(key.Start),
                        ColumnOf(key.Start),
                        Build(pair.Value, sourcePath, depth + 1)));
                }

                return FlowValue.OfMap(entries, line, column);
            }

            throw new FlowParseException(
                sourcePath, line, column,
                $"unsupported YAML node of type {node.NodeType}");
        }

        private static bool Contains(List<FlowArgument> arguments, string name)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                if (string.Equals(arguments[i].Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool IsBindName(string text)
        {
            if (!char.IsLetter(text[0]) && text[0] != '_')
                return false;

            for (var i = 1; i < text.Length; i++)
            {
                if (!char.IsLetterOrDigit(text[i]) && text[i] != '_')
                    return false;
            }

            return true;
        }

        private static bool IsNullText(string text) =>
            text.Length == 0 ||
            string.Equals(text, "~", StringComparison.Ordinal) ||
            string.Equals(text, "null", StringComparison.Ordinal) ||
            string.Equals(text, "Null", StringComparison.Ordinal) ||
            string.Equals(text, "NULL", StringComparison.Ordinal);

        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "the parser rejected the document";

            var breakAt = message.IndexOf('\n');
            return breakAt < 0 ? message : message.Substring(0, breakAt).TrimEnd('\r');
        }

        private static int LineOf(Mark mark) => mark.Line < 1 ? 1 : (int)mark.Line;

        private static int ColumnOf(Mark mark) => mark.Column < 1 ? 1 : (int)mark.Column;
    }
}
