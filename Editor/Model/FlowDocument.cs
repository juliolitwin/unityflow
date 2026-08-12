using System;
using System.Collections.Generic;

namespace UnityFlow.Editor.Model
{
    /// <summary>
    /// What a flow needs from the input path before it is allowed to run.
    ///
    /// This exists so a flow that genuinely tests input handling cannot silently degrade into a
    /// flow that pokes buttons through <see cref="Core.WriteCapability.Activate"/> and reports a
    /// green result. A flow declaring <see cref="System"/> must be skipped, loudly, when no
    /// <see cref="Core.IInputDriver"/> is usable.
    /// </summary>
    public enum InputRequirement
    {
        /// <summary>Not declared. The runner picks the strongest write path available and reports which it used.</summary>
        Unspecified,

        /// <summary>Real device injection is mandatory. Written as <c>requires: { input: system }</c>.</summary>
        System,

        /// <summary>Synthesized UI events are acceptable. Written as <c>requires: { input: semantic }</c>.</summary>
        Semantic
    }

    /// <summary>Preconditions declared by <c>requires:</c>. Immutable.</summary>
    public sealed class FlowRequirements
    {
        /// <summary>A flow that declared no requirements at all.</summary>
        public static readonly FlowRequirements Unspecified = new FlowRequirements(InputRequirement.Unspecified);

        /// <summary>Write path the flow insists on.</summary>
        public InputRequirement Input { get; }

        public FlowRequirements(InputRequirement input)
        {
            Input = input;
        }

        public override string ToString() =>
            Input == InputRequirement.Unspecified ? "none" : $"input={Input.ToString().ToLowerInvariant()}";
    }

    /// <summary>
    /// One parsed flow file. Immutable after parse: the runner may execute it many times, and a
    /// step list that could be edited between runs would make a failure report unreproducible.
    ///
    /// <see cref="SourcePath"/> travels with the document rather than being held by the parser,
    /// because runtime failures need the same <c>path:line:column</c> gutter that parse failures do.
    /// </summary>
    public sealed class FlowDocument
    {
        private readonly FlowStep[] m_Before;
        private readonly FlowStep[] m_Steps;
        private readonly FlowStep[] m_After;
        private readonly string[] m_SourceFiles;

        /// <summary>Path of the file this was parsed from, exactly as handed to the parser.</summary>
        public string SourcePath { get; }

        /// <summary>
        /// Every file that had to be read to build this document: <see cref="SourcePath"/> first,
        /// then each file <c>runFlow</c> pulled in, in first-seen order and without duplicates.
        ///
        /// It exists for the resume ledger. A run that crosses a domain reload is REBUILT by
        /// re-parsing, and the guard that stops it being rebuilt from different YAML is a content
        /// hash — which has to cover every file the step list came from, not only the top one. A
        /// sub-flow edited while the run was suspended would otherwise shift every step index after
        /// it and the report would describe a run that never happened.
        /// </summary>
        public IReadOnlyList<string> SourceFiles => m_SourceFiles;

        /// <summary>Flow name from <c>name:</c>. Required, non-empty.</summary>
        public string Name { get; }

        /// <summary>Preconditions from <c>requires:</c>. Never null.</summary>
        public FlowRequirements Requires { get; }

        /// <summary>Value for <c>Time.timeScale</c> during the run, from <c>timeScale:</c>. Null when not declared.</summary>
        public float? TimeScale { get; }

        /// <summary>Deterministic seed from <c>seed:</c>. Null when not declared.</summary>
        public int? Seed { get; }

        /// <summary>
        /// The variables this document was parsed with: the <c>env:</c> defaults after the caller's
        /// overrides. Never null.
        ///
        /// It is kept on the document even though every <c>${name}</c> was already substituted,
        /// because the substituted steps no longer say what they were substituted FROM. A run whose
        /// parameters are invisible is not reproducible, so the runner writes this into the run
        /// header and the resume ledger carries it across a domain reload.
        /// </summary>
        public FlowEnv Env { get; }

        /// <summary>Setup steps, in source order. Empty when <c>before:</c> is absent.</summary>
        public IReadOnlyList<FlowStep> Before => m_Before;

        /// <summary>The flow proper, in source order. Always at least one step.</summary>
        public IReadOnlyList<FlowStep> Steps => m_Steps;

        /// <summary>Teardown steps, in source order. Empty when <c>after:</c> is absent.</summary>
        public IReadOnlyList<FlowStep> After => m_After;

        public FlowDocument(
            string sourcePath,
            string name,
            FlowRequirements requires,
            float? timeScale,
            int? seed,
            IReadOnlyList<FlowStep> before,
            IReadOnlyList<FlowStep> steps,
            IReadOnlyList<FlowStep> after,
            FlowEnv env = null,
            IReadOnlyList<string> sourceFiles = null)
        {
            SourcePath = sourcePath;
            Name = name;
            Requires = requires ?? FlowRequirements.Unspecified;
            TimeScale = timeScale;
            Seed = seed;
            Env = env ?? FlowEnv.Empty;
            m_Before = Copy(before);
            m_Steps = Copy(steps);
            m_After = Copy(after);
            m_SourceFiles = CopyFiles(sourcePath, sourceFiles);
        }

        /// <summary>
        /// The <c>path:line:column</c> gutter for a step, so a runtime failure reads exactly like a
        /// parse failure and an editor can jump straight to the line.
        /// </summary>
        public string Locate(FlowStep step)
        {
            if (step == null)
                throw new ArgumentNullException(nameof(step));

            // A step brought in by runFlow carries its own file; its line number means nothing in
            // the parent's.
            return $"{step.SourcePath ?? SourcePath}:{step.Line}:{step.Column}";
        }

        /// <summary>The <c>path:line:column</c> gutter for an arbitrary position in this file.</summary>
        public string Locate(int line, int column) => $"{SourcePath}:{line}:{column}";

        public override string ToString() => $"{Name} ({SourcePath}, {m_Steps.Length} steps)";

        /// <summary>
        /// The document's own file, followed by the sub-flow files, deduplicated. The parser is
        /// expected to have listed the parent first; this only guarantees it is present, so a
        /// document built by hand (every test that constructs one) still reports one file.
        /// </summary>
        private static string[] CopyFiles(string sourcePath, IReadOnlyList<string> sourceFiles)
        {
            var files = new List<string>((sourceFiles?.Count ?? 0) + 1) { sourcePath };

            for (var i = 0; sourceFiles != null && i < sourceFiles.Count; i++)
            {
                if (!files.Contains(sourceFiles[i]))
                    files.Add(sourceFiles[i]);
            }

            return files.ToArray();
        }

        private static FlowStep[] Copy(IReadOnlyList<FlowStep> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<FlowStep>();

            var copy = new FlowStep[source.Count];
            for (var i = 0; i < source.Count; i++)
                copy[i] = source[i];

            return copy;
        }
    }
}
