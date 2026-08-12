using System;
using System.Collections.Generic;
using UnityEngine;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Window
{
    /// <summary>What the progress stream has said about one step.</summary>
    public enum FlowStepState
    {
        /// <summary>Not reached. Every step starts here, which is what makes the whole flow visible before it runs.</summary>
        Pending,

        /// <summary>A <c>step.start</c> with no outcome after it yet.</summary>
        Running,

        /// <summary>Recorded a <c>step.pass</c>.</summary>
        Passed,

        /// <summary>Recorded a <c>step.fail</c>.</summary>
        Failed,

        /// <summary>Started, and the run ended before anything recorded its outcome.</summary>
        Interrupted
    }

    /// <summary>
    /// One step of the flow, in source order, with whatever the run has said about it.
    ///
    /// Everything down to <see cref="State"/> comes from the parsed document and is known before a
    /// single step executes; everything below it comes from the progress stream.
    /// </summary>
    public sealed class FlowStepProgress
    {
        /// <summary>Flat index across before/steps/after — the number every progress record uses.</summary>
        public int Index;

        /// <summary>Section the step was written in: "before", "steps" or "after".</summary>
        public string Section;

        public string Verb;

        /// <summary>
        /// The argument that identifies this step among the others with the same verb — see
        /// <see cref="FlowStepCaption"/>. Empty for a verb that takes none.
        /// </summary>
        public string Argument;

        /// <summary>
        /// File the step was written in, when <c>runFlow</c> spliced it in from another one; null
        /// when it belongs to the flow that was started. <see cref="Line"/> is a position in THIS
        /// file, so showing one without the other points at the wrong line.
        /// </summary>
        public string SourceFile;

        public int Line;

        public FlowStepState State = FlowStepState.Pending;

        /// <summary>What the step took, or -1 until it has an outcome.</summary>
        public int ElapsedMs = -1;

        public string FailureSummary;
        public string FailureDetail;

        /// <summary>Absolute path of the screenshot captured at the failure, when one could be taken.</summary>
        public string Screenshot;

        /// <summary>
        /// <c>step.assist</c> and <c>drag.attempt</c> records: things the step DID that change what
        /// its pass is worth.
        /// </summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// A run's progress stream folded into the state of every step, for a UI to render.
    ///
    /// The step LIST comes from the parsed document and the step STATES come from the
    /// <c>progress.ndjson</c> the runner already flushes a line to at every transition. Nothing
    /// here asks the runner anything, and that is the point: a view built on this renders a run
    /// started from anywhere, and it renders a run that crossed a domain reload by replaying the
    /// same file from its first line in the new domain.
    /// </summary>
    public sealed class FlowRunProgress
    {
        private readonly FlowStepProgress[] m_Steps;
        private readonly List<string> m_Warnings = new List<string>();
        private readonly List<string> m_Notes = new List<string>();

        private string[] m_Env = Array.Empty<string>();
        private string[] m_Backends = Array.Empty<string>();
        private int m_CurrentStep = -1;

        /// <param name="document">
        /// The flow as parsed. It has to be the document rather than the stream because the stream
        /// only ever mentions a step once it starts, and a list that grows as the run advances shows
        /// nothing of what is still to come.
        /// </param>
        public FlowRunProgress(FlowDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            FlowName = document.Name;
            FlowPath = document.SourcePath;

            var steps = new List<FlowStepProgress>(
                document.Before.Count + document.Steps.Count + document.After.Count);

            // The runner walks the three sections as ONE list and numbers every step by its position
            // in it, so the same flattening is what makes a record's index addressable here.
            Flatten(document, "before", document.Before, steps);
            Flatten(document, "steps", document.Steps, steps);
            Flatten(document, "after", document.After, steps);

            m_Steps = steps.ToArray();
        }

        /// <summary>Every step of the flow, in source order.</summary>
        public IReadOnlyList<FlowStepProgress> Steps => m_Steps;

        /// <summary>The run's own state, as the stream last reported it.</summary>
        public RunState State { get; private set; } = RunState.Pending;

        /// <summary>Whether the run will ever say anything more.</summary>
        public bool IsTerminal => RunStatus.IsTerminalState(State);

        /// <summary>True once a segment picked the run up on the far side of a domain reload.</summary>
        public bool Resumed { get; private set; }

        public string RunId { get; private set; }
        public string FlowName { get; private set; }
        public string FlowPath { get; private set; }

        /// <summary>The EFFECTIVE variables the run was built with, as <c>name=value</c>.</summary>
        public IReadOnlyList<string> Env => m_Env;

        public IReadOnlyList<string> Backends => m_Backends;

        /// <summary>Whether the editor was in play mode when the current segment started.</summary>
        public bool PlayMode { get; private set; }

        /// <summary>How input was produced, or null until a step first needed to produce some.</summary>
        public string WriteMode { get; private set; }

        /// <summary>How well a tap could be proven to reach its target. This is what a pass is worth.</summary>
        public string Occlusion { get; private set; }

        public string InputDriver { get; private set; }

        public double DurationSeconds { get; private set; }

        /// <summary>The run's verdict, from <c>run.end</c>.</summary>
        public string FailureSummary { get; private set; }

        /// <summary>
        /// The FIRST step that failed, which is the diagnosis. A later failure is teardown reacting
        /// to the first one, and the runner discards it for the same reason.
        /// </summary>
        public FlowStepProgress FailedStep { get; private set; }

        /// <summary>Anything the run flagged as reducing what it proves.</summary>
        public IReadOnlyList<string> Warnings => m_Warnings;

        /// <summary>Run-level notes, such as the scene the run put back.</summary>
        public IReadOnlyList<string> Notes => m_Notes;

        /// <summary>Records folded in so far, so a renderer can tell whether anything changed.</summary>
        public int RecordsApplied { get; private set; }

        /// <summary>
        /// Fold one line of <c>progress.ndjson</c> in. Lines must arrive in file order; replaying
        /// the file from its first line is how a view rebuilt by a domain reload catches up.
        /// </summary>
        public void Apply(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            var record = JsonUtility.FromJson<Record>(line);
            RecordsApplied++;

            switch (record.type)
            {
                case "run.start":
                case "run.resume":
                    Begin(record);
                    break;

                case "run.writeMode":
                    WriteMode = record.writeMode;
                    Occlusion = record.occlusion;
                    InputDriver = record.inputDriver;
                    break;

                case "run.warning":
                    m_Warnings.Add(record.message);
                    break;

                case "run.note":
                    m_Notes.Add(record.message);
                    break;

                case "run.end":
                    End(record);
                    break;

                case "step.start":
                    m_CurrentStep = record.index;
                    Step(record.index).State = FlowStepState.Running;
                    break;

                case "step.pass":
                {
                    var step = Step(record.index);
                    step.State = FlowStepState.Passed;
                    step.ElapsedMs = record.ms;
                    break;
                }

                case "step.fail":
                {
                    var step = Step(record.index);
                    step.State = FlowStepState.Failed;
                    step.ElapsedMs = record.ms;
                    step.FailureSummary = Text(record.summary);
                    step.FailureDetail = Detail(record);
                    step.Screenshot = Text(record.screenshot);
                    FailedStep = FailedStep ?? step;
                    break;
                }

                case "step.assist":
                    Note($"{record.verb} used {record.mechanism}: {record.message}");
                    break;

                case "drag.attempt":
                    Note($"attempt {record.attempt}: {record.outcome} ({record.confirmation})");
                    break;

                // A type this class does not render is skipped rather than refused: the stream
                // belongs to the runner and may carry records added after this was written.
            }
        }

        private void Begin(Record record)
        {
            RunId = record.runId;
            FlowName = record.flow;
            FlowPath = record.path;
            m_Env = record.env ?? Array.Empty<string>();
            m_Backends = record.backends ?? Array.Empty<string>();
            PlayMode = record.playMode;
            State = RunState.Running;

            // The header states how many steps the run is executing. A different number means the
            // file was edited since it started, so every index below points at a different step —
            // reported rather than reconciled, because there is no reconciling it.
            if (record.steps != m_Steps.Length)
            {
                m_Warnings.Add(
                    $"the run is executing {record.steps} steps but {FlowPath} now parses to {m_Steps.Length}: " +
                    "the file changed after the run started, so the list below does not describe it");
            }

            if (!string.Equals(record.type, "run.resume", StringComparison.Ordinal))
                return;

            Resumed = true;

            // A step that triggers the reload — enterPlayMode — is cut off mid-execution and never
            // writes an outcome, yet the runner resumes AFTER it and treats it as executed. Leaving
            // it Running would spin a spinner on a step nothing will ever report on again.
            for (var i = 0; i < record.nextStep; i++)
            {
                var step = Step(i);
                if (step.State == FlowStepState.Running)
                    step.State = FlowStepState.Passed;
            }
        }

        private void End(Record record)
        {
            if (!Enum.TryParse<RunState>(record.state, out var state))
                throw new InvalidOperationException($"A run.end record names the unknown run state '{record.state}'.");

            State = state;
            DurationSeconds = record.seconds;

            // FlowRunner writes the reason as 'failure'. FlowResumer.Abort, which ends a run the
            // runner is no longer alive to end, writes it as 'message'. Both are run.end records.
            FailureSummary = Text(record.failure) ?? Text(record.message);

            Interrupt();
        }

        /// <summary>
        /// Declare that nothing will ever append to this stream again: the domain the run was
        /// executing in went away and no resume ledger will bring it back.
        ///
        /// The verdict is not invented here — it is the one the tool itself records. A run started
        /// with <c>flow.run</c> is completed by <see cref="FlowDriver"/> with a
        /// <see cref="FlowInterruptedException"/> as the domain unloads, and the command writes
        /// exactly this state into the run's status. What it cannot do is append it to the progress
        /// stream, because it dies with the same domain.
        /// </summary>
        public void Abandon()
        {
            if (IsTerminal)
                throw new InvalidOperationException($"Run '{RunId}' already ended as {State}, so it cannot be abandoned.");

            State = RunState.Errored;
            FailureSummary =
                "the domain reloaded while this run was in flight. It was started with flow.run, which lives " +
                "inside one domain and cannot come back from a reload; only a flow that enters play mode is " +
                "started with flow.start, which can";

            Interrupt();
        }

        /// <summary>Nothing will report on a step that was still executing, so stop showing it as executing.</summary>
        private void Interrupt()
        {
            for (var i = 0; i < m_Steps.Length; i++)
            {
                if (m_Steps[i].State == FlowStepState.Running)
                    m_Steps[i].State = FlowStepState.Interrupted;
            }
        }

        private void Note(string text)
        {
            if (m_CurrentStep < 0)
                throw new InvalidOperationException($"A step note arrived before any step started: {text}");

            Step(m_CurrentStep).Notes.Add(text);
        }

        private FlowStepProgress Step(int index)
        {
            if (index < 0 || index >= m_Steps.Length)
            {
                throw new InvalidOperationException(
                    $"The run reported step {index}, which does not exist in {FlowPath} — it has {m_Steps.Length} steps.");
            }

            return m_Steps[index];
        }

        /// <summary>The whole failure block: the diagnostics, plus the near misses when there were any.</summary>
        private static string Detail(Record record)
        {
            var detail = Text(record.detail);

            if (record.nearMisses == null || record.nearMisses.Length == 0)
                return detail;

            return detail + "\n  Near misses:\n    " + string.Join("\n    ", record.nearMisses);
        }

        /// <summary>
        /// A record's optional text, or null when there is none.
        ///
        /// <see cref="JsonUtility"/> reads an explicit JSON null into an EMPTY STRING while an
        /// absent key leaves the field null, so the two ways the writer says "nothing here" arrive
        /// as different values — and a passing run, whose run.end carries <c>"failure":null</c>,
        /// would otherwise be rendered as a run with a blank verdict.
        /// </summary>
        private static string Text(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static void Flatten(
            FlowDocument document, string section, IReadOnlyList<FlowStep> source, List<FlowStepProgress> into)
        {
            for (var i = 0; i < source.Count; i++)
            {
                var step = source[i];

                into.Add(new FlowStepProgress
                {
                    Index = into.Count,
                    Section = section,
                    Verb = step.Verb,
                    Argument = FlowStepCaption.Argument(step),
                    SourceFile = step.SourcePath != null &&
                                 !string.Equals(step.SourcePath, document.SourcePath, StringComparison.Ordinal)
                        ? step.SourcePath
                        : null,
                    Line = step.Line
                });
            }
        }

        /// <summary>
        /// Every field any progress record carries, in one shape.
        ///
        /// One union rather than a type per record because <see cref="JsonUtility"/> is the only
        /// JSON reader in the package — <see cref="FlowResumeState"/> uses it too — and it ignores
        /// keys it does not declare, so this reads all ten record types. The names are the wire
        /// names and must stay exactly as <see cref="NdjsonWriter"/> spells them.
        /// </summary>
        [Serializable]
        private sealed class Record
        {
            public string type;

            // run.start, run.resume
            public string runId;
            public string flow;
            public string path;
            public int steps;
            public int nextStep;
            public string[] backends;
            public string[] env;
            public bool playMode;

            // run.writeMode
            public string writeMode;
            public string occlusion;
            public string inputDriver;

            // run.warning, run.note, and the run.end FlowResumer writes
            public string message;

            // run.end
            public string state;
            public double seconds;
            public string failure;

            // step.start, step.pass, step.fail
            public int index;
            public string verb;
            public int ms;
            public string summary;
            public string detail;
            public string[] nearMisses;
            public string screenshot;

            // step.assist
            public string mechanism;

            // drag.attempt
            public int attempt;
            public string outcome;
            public string confirmation;
        }
    }
}
