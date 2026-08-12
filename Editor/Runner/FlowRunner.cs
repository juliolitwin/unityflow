using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Report;

namespace UnityFlow.Editor.Runner
{
    /// <summary>Final outcome of a run, returned to the CLI.</summary>
    public sealed class FlowRunResult
    {
        public string RunId;
        public string FlowName;
        public string State;
        public int StepsRun;
        public int StepCount;
        public string Failure;
        public string FailedStep;
        public double DurationSeconds;
        public string WriteMode;
        public string OcclusionFidelity;
        public string RunDirectory;

        /// <summary>NDJSON sequence the run reached, so anything appended afterwards continues it.</summary>
        public int ProgressSequence;

        public bool Passed => State == nameof(RunState.Passed);
    }

    /// <summary>
    /// The interpreter. Walks a parsed flow, one step per retry loop, inside the frame pump.
    ///
    /// It runs entirely in the editor rather than being driven step-by-step from the host CLI,
    /// because a per-step round trip would make the retry model unaffordable: a waitFor polling at
    /// 100ms for 5s becomes 50 process spawns and 50 HTTP requests. In here the same wait is 300
    /// frames that cost nothing.
    /// </summary>
    public sealed class FlowRunner
    {
        /// <summary>Per-step ceiling when a step does not set its own. Matches Maestro's default.</summary>
        public static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(7);

        private readonly FlowDocument m_Document;
        private readonly FlowVocabulary m_Vocabulary;
        private readonly RunPaths m_Paths;
        private readonly TimeSpan m_Budget;
        private readonly bool m_AllowUnverifiedOcclusion;
        private readonly FlowResumeState m_Resume;

        private BackendRegistry m_Registry;
        private SelectorResolver m_Resolver;
        private ConsoleRing m_Console;
        private NdjsonWriter m_Progress;
        private RunStatus m_Status;
        private IDisposable m_InputSession;
        private StepContext m_Context;

        public FlowRunResult Result { get; private set; }

        /// <param name="resume">
        /// The run's resume ledger, or null for a run that cannot survive a domain reload.
        /// A non-null ledger with a non-zero <see cref="FlowResumeState.NextStepIndex"/> means this
        /// is the continuation of a run that was suspended at that step boundary: the steps before
        /// it already ran, in a domain that no longer exists.
        /// </param>
        public FlowRunner(FlowDocument document, FlowVocabulary vocabulary, RunPaths paths,
            TimeSpan budget, bool allowUnverifiedOcclusion, FlowResumeState resume = null)
        {
            m_Document = document ?? throw new ArgumentNullException(nameof(document));
            m_Vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
            m_Paths = paths ?? throw new ArgumentNullException(nameof(paths));
            m_Budget = budget;
            m_AllowUnverifiedOcclusion = allowUnverifiedOcclusion;
            m_Resume = resume;
        }

        /// <summary>The whole run, as one enumerator for <see cref="FlowDriver"/> to pump.</summary>
        public IEnumerator Run()
        {
            var started = FlowClock.Now;
            var runDeadline = started + m_Budget.TotalSeconds;

            // A resumed segment continues one run: the same progress stream, the same start time,
            // and the same verdict so far. Only the domain it executes in is new.
            var startIndex = m_Resume?.NextStepIndex ?? 0;
            var resuming = startIndex > 0;

            m_Console = new ConsoleRing();
            m_Progress = new NdjsonWriter(m_Paths.ProgressFile, resuming, resuming ? m_Resume.ProgressSequence : 0);
            m_Status = new RunStatus
            {
                RunId = m_Paths.RunId,
                FlowName = m_Document.Name,
                FlowPath = m_Document.SourcePath,
                StartedAtUtc = resuming
                    ? m_Resume.StartedAtUtc
                    : (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };

            try
            {
                // What the run costs the editor, recorded alongside what it proves. Started before
                // the first step so a stall inside step zero is attributed rather than missed.
                FlowProfiler.Begin(m_Paths.RunDirectory);

                m_Registry = new BackendRegistry();

                if (m_Registry.Active.Count == 0)
                {
                    Finish(RunState.Errored,
                        "no UI backend is available: " +
                        (m_Registry.Rejected.Count == 0
                            ? "none is installed"
                            : string.Join("; ", m_Registry.Rejected)),
                        null, started);
                    yield break;
                }

                m_Resolver = new SelectorResolver(m_Registry);
                m_Context = new StepContext(m_Registry, m_Resolver, m_Paths, m_Console, ResolveWriteMode, m_Progress);
                m_Context.Resume = m_Resume;

                WriteHeader(resuming);

                var sections = new[]
                {
                    ("before", m_Document.Before),
                    ("steps", m_Document.Steps),
                    ("after", m_Document.After)
                };

                m_Status.StepCount = m_Document.Before.Count + m_Document.Steps.Count + m_Document.After.Count;
                m_Status.State = RunState.Running;

                // A verdict reached before the reload must survive it. Without this, a flow whose
                // body failed and whose teardown crossed a reload would come back reporting a pass.
                var failed = resuming && m_Resume.Failed;
                if (failed)
                    m_Status.FailureSummary = m_Resume.FailureSummary;

                m_Status.StepIndex = startIndex - 1;
                m_Status.Save(m_Paths.StatusFile);

                var index = 0;

                foreach (var (section, steps) in sections)
                {
                    for (var i = 0; i < steps.Count; i++)
                    {
                        // The index must advance for skipped steps too: it is the resume cursor, so
                        // a gap here would make a teardown that resumes after a domain reload
                        // re-number from zero and silently drop its own steps.
                        var flatIndex = index++;

                        // 'after' is teardown: it runs even when the body failed, which is what
                        // keeps a failed run from leaving persisted state behind for the next one.
                        if (failed && section != "after")
                            continue;

                        // Already executed, in the domain this run started in.
                        if (flatIndex < startIndex)
                            continue;

                        var step = steps[i];

                        // Keep the ledger current BEFORE the step runs, so a step that arms a
                        // resume point saves a complete picture rather than one missing the run's
                        // verdict so far.
                        if (m_Resume != null)
                        {
                            m_Resume.StepCount = m_Status.StepCount;
                            m_Resume.ProgressSequence = m_Progress.Sequence;
                            m_Resume.Failed = failed;
                            m_Resume.FailureSummary = m_Status.FailureSummary;
                        }

                        var enumerator = RunStep(section, flatIndex, step, runDeadline);
                        while (enumerator.MoveNext())
                            yield return enumerator.Current;

                        if (m_Paths.CancelRequested)
                        {
                            Finish(RunState.Cancelled, "cancelled by the host", step, started);
                            yield break;
                        }

                        if (m_Context.Failure != null)
                        {
                            if (section == "after")
                            {
                                // A teardown failure must not overwrite the real diagnosis.
                                m_Context.Failure = null;
                                continue;
                            }

                            failed = true;
                        }

                        if (FlowClock.Now >= runDeadline)
                        {
                            Finish(RunState.Failed,
                                $"the run exceeded its budget of {m_Budget.TotalSeconds:0.#}s", step, started);
                            yield break;
                        }
                    }
                }

                if (failed)
                {
                    Finish(RunState.Failed, m_Status.FailureSummary, null, started);
                    yield break;
                }

                Finish(RunState.Passed, null, null, started);
            }
            finally
            {
                // Only when the run actually ends. A segment cut short by a domain reload never
                // reaches here — its iterator is destroyed, not disposed — which is exactly right:
                // the profile stays open and the segment on the far side appends to the same file.
                FlowProfiler.End();

                m_InputSession?.Dispose();
                m_Progress?.Dispose();
                m_Console?.Dispose();
            }
        }

        private IEnumerator RunStep(string section, int index, FlowStep step, double runDeadline)
        {
            var stepTimeout = (step.Timeout ?? DefaultStepTimeout).TotalSeconds;
            var stepStarted = FlowClock.Now;

            m_Context.Step = step;
            m_Context.Section = section;
            m_Context.StepIndex = index;
            m_Context.Failure = null;
            m_Context.ConsoleCursorBeforePreviousStep = m_Context.ConsoleCursorAtStart;
            m_Context.ConsoleCursorAtStart = m_Console.Cursor;
            m_Context.Deadline = Math.Min(stepStarted + stepTimeout, runDeadline);

            var description = Describe(step);

            FlowProfiler.SetStep(index, step.Verb, SubFlowOf(step));

            m_Status.StepIndex = index;
            m_Status.StepDescription = description;
            m_Status.ProgressSequence = m_Progress.Sequence;
            m_Status.Save(m_Paths.StatusFile);

            if (m_Resume != null)
                m_Resume.StepDescription = description;

            m_Progress.Write("step.start", new[]
            {
                Pair("section", section),
                Pair("index", index),
                Pair("verb", step.Verb),
                Pair("step", description),
                Pair("line", step.Line),

                // Only when the step came from somewhere else. Its line number is a position in
                // THAT file, so a reader who cannot see the file name would look up the wrong line
                // of the flow that was started.
                Pair("file", SubFlowOf(step))
            });

            IEnumerator body;
            if (m_Vocabulary.Commands.TryGetValue(step.Verb, out var method))
                body = FlowCommandInvoker.Invoke(m_Context, method);
            else
                body = StepLibrary.Execute(m_Context);

            var faulted = (Exception)null;

            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext())
                        break;

                    current = body.Current;
                }
                catch (Exception ex)
                {
                    faulted = ex;
                    break;
                }

                yield return current;
            }

            if (faulted != null)
            {
                m_Context.Fail(
                    $"{step.Verb} threw {faulted.GetType().Name}: {faulted.Message}",
                    m_Context.BuildDiagnostics());
            }

            var elapsed = (FlowClock.Now - stepStarted) * 1000.0;

            if (m_Context.Failure == null)
            {
                m_Progress.Write("step.pass", new[]
                {
                    Pair("index", index),
                    Pair("verb", step.Verb),
                    Pair("ms", (int)elapsed)
                });
            }
            else
            {
                m_Status.FailureSummary = m_Context.Failure.Summary;

                var screenshot = TryCaptureFailure(index);

                m_Progress.Write("step.fail", new[]
                {
                    Pair("index", index),
                    Pair("verb", step.Verb),
                    Pair("ms", (int)elapsed),
                    Pair("line", step.Line),
                    Pair("file", SubFlowOf(step)),
                    Pair("summary", m_Context.Failure.Summary),
                    Pair("detail", m_Context.Failure.Detail),
                    Pair("nearMisses", (object)m_Context.Failure.NearMisses),
                    Pair("screenshot", screenshot)
                });
            }
        }

        private string TryCaptureFailure(int index)
        {
            if (!Capture.FlowCapture.IsAvailable(out _))
                return null;

            var path = m_Paths.Artifact($"fail-{index:D2}.png");
            return Capture.FlowCapture.TryCapture(path, out _) ? path : null;
        }

        /// <summary>
        /// Choose how input is produced, once, and record it.
        ///
        /// This is a capability negotiation, not a fallback chain: one mechanism is selected up
        /// front from what the environment actually supports, it is written into the run header and
        /// the report, and it never changes mid-run. A flow that declares
        /// <c>requires: { input: system }</c> fails immediately rather than quietly running with
        /// the weaker mechanism and reporting a pass that means less than the reader thinks.
        /// </summary>
        /// <summary>
        /// Decide how input is produced, at the moment a step first needs to produce some.
        ///
        /// Called lazily by <see cref="StepContext.WriteMode"/> and then frozen. It must not run at
        /// the start of the run: a flow may PROVISION its own environment, and real flows do —
        /// 'enterPlayMode' sits in the before: section, and until play mode is live the input
        /// driver correctly reports itself unavailable (EventSystem is not [ExecuteAlways], so
        /// InputSystemUIInputModule.Process never runs and an injected event reaches nothing).
        /// Deciding up front therefore rejected a valid flow before its first step.
        ///
        /// Throwing rather than returning an error code is deliberate: the caller is a property on
        /// the step's hot path, and the runner already turns a step exception into a proper failure
        /// record with full diagnostics.
        /// </summary>
        private WriteMode ResolveWriteMode()
        {
            var demandsDevice = m_Document.Requires.Input == InputRequirement.System;

            // Deliberately NOT rebuilding the registry here. A step that changes the environment
            // (enterPlayMode) ends its segment at that boundary, and the segment that resumes on
            // the far side constructs its own registry in the world it will actually run in — so
            // m_Registry is already current. Rebuilding would hand back a driver whose session this
            // method opens while every step keeps using the registry captured in StepContext,
            // producing "the driver has no open session" at the first tap.
            if (m_Registry.InputDriver != null)
            {
                m_InputSession = m_Registry.InputDriver.BeginSession();
                ReportWriteMode(WriteMode.DeviceInjection);
                return WriteMode.DeviceInjection;
            }

            if (demandsDevice)
            {
                throw new FlowInterruptedException(
                    "this flow declares 'requires: { input: system }' but real device injection is unavailable: " +
                    m_Registry.InputDriverRejection);
            }

            foreach (var backend in m_Registry.Active)
            {
                if ((backend.Capabilities & WriteCapability.SemanticDispatch) != 0)
                {
                    ReportWriteMode(WriteMode.SemanticDispatch);
                    return WriteMode.SemanticDispatch;
                }
            }

            throw new FlowInterruptedException(
                "no way to produce input: device injection is unavailable (" +
                m_Registry.InputDriverRejection + ") and no backend supports semantic dispatch");
        }

        /// <summary>Record the decision once, so a reader always knows what a pass was worth.</summary>
        private void ReportWriteMode(WriteMode writeMode)
        {
            var fidelity = m_Registry.EffectiveOcclusionFidelity;

            m_Status.OcclusionFidelity = fidelity.ToString();
            m_Status.InputDriver = writeMode == WriteMode.DeviceInjection
                ? m_Registry.InputDriver.Id
                : "none (" + m_Registry.InputDriverRejection + ")";

            m_Progress.Write("run.writeMode", new[]
            {
                Pair("writeMode", writeMode.ToString()),
                Pair("occlusion", fidelity.ToString()),
                Pair("inputDriver", m_Status.InputDriver)
            });

            if (fidelity != OcclusionFidelity.CrossSurface)
            {
                m_Progress.Write("run.warning", new[]
                {
                    Pair("message",
                        $"occlusion fidelity is {fidelity}: a tap cannot be proven to have reached its target " +
                        "through anything covering it. Run in play mode for full verification.")
                });
            }
        }

        /// <summary>
        /// Announce the run before any step executes.
        ///
        /// It deliberately does NOT report the write mode or occlusion: those are resolved on first
        /// use, because a flow may create the environment it needs (see <see cref="ResolveWriteMode"/>).
        /// They are emitted as their own "run.writeMode" record the moment they are decided, so the
        /// header never claims a capability the run had not yet established.
        ///
        /// It DOES report the effective env, on every segment. A flow's steps are substituted before
        /// they reach the runner, so nothing further down the stream says which character or account
        /// this run used; without this record a report is not reproducible, and a resumed segment
        /// that came back with different parameters than the first would be invisible.
        /// </summary>
        private void WriteHeader(bool resuming)
        {
            var backends = new List<string>();
            foreach (var backend in m_Registry.Active)
                backends.Add(backend.Id);

            m_Progress.Write(resuming ? "run.resume" : "run.start", new[]
            {
                Pair("runId", m_Paths.RunId),
                Pair("flow", m_Document.Name),
                Pair("path", m_Document.SourcePath),
                Pair("steps", m_Document.Before.Count + m_Document.Steps.Count + m_Document.After.Count),
                Pair("nextStep", m_Resume?.NextStepIndex ?? 0),
                Pair("section", resuming ? m_Resume.Section : "before"),
                Pair("backends", (object)backends),
                Pair("env", (object)m_Document.Env.ToPairs()),
                Pair("playMode", EditorApplication.isPlaying)
            });

            if (m_Resume != null)
            {
                m_Resume.FlowName = m_Document.Name;
                m_Resume.StepCount = m_Document.Before.Count + m_Document.Steps.Count + m_Document.After.Count;

                // Re-stated from the document rather than trusted from the ledger, so the ledger can
                // only ever hold the parameters the steps were actually built with.
                m_Resume.Env = m_Document.Env.ToPairs();
            }
        }

        private void Finish(RunState state, string failure, FlowStep failedStep, double started)
        {
            // FlowClock restarts with every domain, so a run that crossed a reload can only be
            // timed against the wall clock it recorded when it began.
            var duration = m_Resume != null
                ? FlowResumeState.NowUtc - m_Status.StartedAtUtc
                : FlowClock.Now - started;

            m_Status.State = state;
            m_Status.FailureSummary = failure ?? m_Status.FailureSummary;
            m_Status.ProgressSequence = m_Progress?.Sequence ?? 0;
            m_Status.Save(m_Paths.StatusFile);

            m_Progress?.Write("run.end", new[]
            {
                Pair("state", state.ToString()),
                Pair("seconds", duration),
                Pair("failure", m_Status.FailureSummary)
            });

            Result = new FlowRunResult
            {
                RunId = m_Paths.RunId,
                FlowName = m_Document.Name,
                State = state.ToString(),
                StepsRun = m_Status.StepIndex + 1,
                StepCount = m_Status.StepCount,
                Failure = m_Status.FailureSummary,
                FailedStep = failedStep != null ? Describe(failedStep) : m_Status.StepDescription,
                DurationSeconds = duration,
                // Read it only if a step actually resolved it. Touching the property here would
                // force the negotiation during teardown — including on a run that failed before it
                // ever needed to produce input, where it would throw inside the reporting path.
                WriteMode = m_Context != null && m_Context.WriteModeResolved
                    ? m_Context.WriteMode.ToString()
                    : "not needed",
                OcclusionFidelity = m_Status.OcclusionFidelity,
                RunDirectory = m_Paths.RunDirectory,
                ProgressSequence = m_Progress?.Sequence ?? m_Status.ProgressSequence
            };
        }

        /// <summary>The sub-flow a step came from, or null when it was written in the flow being run.</summary>
        private string SubFlowOf(FlowStep step) =>
            step.SourcePath != null && !string.Equals(step.SourcePath, m_Document.SourcePath, StringComparison.Ordinal)
                ? step.SourcePath
                : null;

        private static string Describe(FlowStep step) =>
            step.Selector != null ? $"{step.Verb} {step.Selector}" : step.Verb;

        private static KeyValuePair<string, object> Pair(string key, object value) =>
            new KeyValuePair<string, object>(key, value);
    }
}
