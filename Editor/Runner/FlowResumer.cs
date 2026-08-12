using System;
using System.Collections.Generic;
using UnityEditor;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.PlayMode;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Picks a suspended run back up after a domain reload.
    ///
    /// This is the far side of <c>enterPlayMode</c>. The domain that started the run is gone —
    /// with the runner, the driver, the HTTP server and the request that was in flight — so nothing
    /// can be "continued"; the run is REBUILT from <see cref="FlowResumeState"/> and restarted at
    /// the step boundary the ledger recorded.
    ///
    /// [InitializeOnLoad] is the only hook that runs unprompted in a fresh domain, but its static
    /// constructor runs while that domain is still being assembled. So it does nothing but queue
    /// <see cref="EditorApplication.delayCall"/>, and even then the pickup waits until the editor
    /// is neither compiling nor importing before it touches anything.
    /// </summary>
    [InitializeOnLoad]
    public static class FlowResumer
    {
        private static Pickup s_Pickup;

        static FlowResumer()
        {
            // Never do work in a static constructor: TypeCache, the asset database and the scene
            // are not dependable yet, and an exception here poisons the whole domain load.
            EditorApplication.delayCall += Begin;
        }

        private static void Begin()
        {
            if (s_Pickup != null)
                return;

            var runId = FlowResumeState.PendingRunId();
            if (runId == null)
                return;

            s_Pickup = new Pickup(runId);
            s_Pickup.Attach();
        }

        /// <summary>
        /// Decide what a finished segment means, for both the first segment (flow.start) and every
        /// resumed one.
        ///
        /// The interesting case is the interruption. <see cref="FlowDriver"/> completes with a
        /// <see cref="FlowInterruptedException"/> the moment the domain starts unloading, and that
        /// is either the transition this run asked for or an accident — the ledger's armed flag is
        /// the only thing that tells them apart, and treating an accident as a resume would restart
        /// the flow in the middle with state that never survived.
        /// </summary>
        internal static void OnSegmentCompleted(RunPaths paths, FlowResumeState resume, FlowRunner runner, Exception error)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (resume == null) throw new ArgumentNullException(nameof(resume));

            if (error is FlowInterruptedException && resume.Armed)
            {
                // The run asked for this. The ledger is already on disk; the runner never reached
                // Finish, so the status still says Running and would look hung to a poller.
                resume.ToStatus(RunState.AwaitingReload, null).Save(paths.StatusFile);
                return;
            }

            if (error != null)
            {
                Abort(paths, resume, RunState.Errored, error is FlowInterruptedException
                    ? error.Message
                    : $"{error.GetType().Name}: {error.Message}");
                return;
            }

            var result = runner?.Result;
            if (result == null)
            {
                Abort(paths, resume, RunState.Errored, "the run produced no result");
                return;
            }

            // Terminal: FlowRunner.Finish has already written status.json and the run.end record.
            // All that is left is to undo what the run did to the editor.
            var note = PlayModeGate.ReleaseScene(resume);
            if (note != null)
                Append(paths, result.ProgressSequence, "run.note", note);

            resume.Clear(paths);
        }

        /// <summary>End a run that cannot go on, leaving a complete record and a restored editor.</summary>
        private static void Abort(RunPaths paths, FlowResumeState resume, RunState state, string failure)
        {
            resume.ToStatus(state, failure).Save(paths.StatusFile);
            Append(paths, resume.ProgressSequence, "run.end", failure, state);

            var note = PlayModeGate.ReleaseScene(resume);
            if (note != null)
                Append(paths, resume.ProgressSequence + 1, "run.note", note);

            resume.Clear(paths);
        }

        /// <summary>
        /// Add one record to the run's progress stream from outside the runner, continuing the
        /// sequence instead of restarting it so a host tailing the file never sees it rewind.
        /// </summary>
        private static void Append(RunPaths paths, int sequence, string type, string message, RunState? state = null)
        {
            var fields = new List<KeyValuePair<string, object>>(2)
            {
                new KeyValuePair<string, object>("message", message)
            };

            if (state.HasValue)
                fields.Add(new KeyValuePair<string, object>("state", state.Value.ToString()));

            using (var writer = new NdjsonWriter(paths.ProgressFile, append: true, startSequence: sequence))
                writer.Write(type, fields);
        }

        /// <summary>
        /// One suspended run being brought back.
        ///
        /// State lives on an instance rather than in statics so that everything it holds dies with
        /// the domain, exactly like the run it is rebuilding. The tick is deliberately allocation
        /// free: it runs every editor frame for as long as a play mode transition takes.
        /// </summary>
        private sealed class Pickup
        {
            private readonly string m_RunId;
            private readonly EditorApplication.CallbackFunction m_Tick;

            private RunPaths m_Paths;
            private FlowResumeState m_State;
            private bool m_Attached;

            public Pickup(string runId)
            {
                m_RunId = runId;
                m_Tick = Tick;
            }

            public void Attach()
            {
                if (m_Attached)
                    return;

                EditorApplication.update += m_Tick;
                m_Attached = true;
            }

            private void Detach()
            {
                if (!m_Attached)
                    return;

                EditorApplication.update -= m_Tick;
                m_Attached = false;
            }

            private void Tick()
            {
                // A compile or an import means the domain is about to go down again, or the assets
                // the flow is about to touch are still moving. Either way, nothing decided now
                // would survive to be acted on.
                if (PlayModeGate.IsEditorBusy)
                    return;

                if (m_State == null)
                {
                    if (!TryLoad())
                        Detach();

                    return;
                }

                switch (m_State.Gate)
                {
                    case ResumeGate.PlayMode:
                        if (PlayModeGate.IsPlayModeActive)
                            break;

                        if (FlowResumeState.NowUtc >= m_State.GateDeadlineUtc)
                        {
                            Detach();
                            Abort(m_Paths, m_State, RunState.Failed,
                                $"play mode did not become active within {Budget()}s of the request. " +
                                "The domain reloaded, so Unity accepted it, but the editor came back in edit mode");
                        }

                        return;

                    case ResumeGate.EditMode:
                        if (PlayModeGate.IsEditModeActive)
                            break;

                        if (FlowResumeState.NowUtc >= m_State.GateDeadlineUtc)
                        {
                            Detach();
                            Abort(m_Paths, m_State, RunState.Failed,
                                $"edit mode did not come back within {Budget()}s of the request");
                        }

                        return;
                }

                Detach();
                Resume();
            }

            private string Budget() => m_State.GateTimeoutSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

            /// <summary>
            /// Read the ledger and prove it describes a run that may be continued. Every check here
            /// exists because failing it silently would produce a report about a run that did not
            /// happen.
            /// </summary>
            private bool TryLoad()
            {
                m_Paths = RunPaths.Existing(m_RunId);

                if (!FlowResumeState.TryLoad(m_Paths, out var state, out var loadError))
                {
                    // Nothing to write a status against: the ledger IS the record of what this run
                    // was. Clearing the flag stops every later reload from retrying this.
                    UnityEngine.Debug.LogError($"UnityFlow cannot resume run '{m_RunId}': {loadError}");
                    FlowResumeState.ClearSession();
                    return false;
                }

                var token = FlowResumeState.PendingToken();
                if (!string.Equals(state.Token, token, StringComparison.Ordinal))
                {
                    Abort(m_Paths, state, RunState.Errored,
                        $"the resume ledger belongs to a different run attempt (ledger token '{state.Token}', session token '{token}'). " +
                        "Resuming it would replay steps of a run nobody is waiting for");
                    return false;
                }

                if (m_Paths.CancelRequested)
                {
                    Abort(m_Paths, state, RunState.Cancelled, "cancelled by the host while the run was suspended");
                    return false;
                }

                if (!state.Armed)
                {
                    Abort(m_Paths, state, RunState.Errored,
                        $"the run was interrupted by an unexpected domain reload during step {state.NextStepIndex} " +
                        $"('{state.StepDescription ?? "unknown"}'). Only a step that asks for a reload — enterPlayMode, " +
                        "exitPlayMode — can be resumed across one, because no other step's half-finished state survives it");
                    return false;
                }

                // Every file the step list was built from, sub-flows included: an edited sub-flow
                // moves the parent's step indices exactly as an edited parent does.
                var files = state.SourceFiles != null && state.SourceFiles.Length > 0
                    ? (IReadOnlyList<string>)state.SourceFiles
                    : new[] { state.FlowPath };

                for (var i = 0; i < files.Count; i++)
                {
                    if (System.IO.File.Exists(files[i]))
                        continue;

                    Abort(m_Paths, state, RunState.Errored,
                        i == 0
                            ? $"the flow file {files[i]} no longer exists, so the remaining steps cannot be re-parsed"
                            : $"the sub-flow {files[i]}, which {state.FlowPath} runs, no longer exists, so the remaining " +
                              "steps cannot be re-parsed");
                    return false;
                }

                var hash = FlowResumeState.HashFiles(files);
                if (!string.Equals(hash, state.FlowHash, StringComparison.Ordinal))
                {
                    Abort(m_Paths, state, RunState.Errored,
                        $"{state.FlowPath}{(files.Count > 1 ? " or one of the " + (files.Count - 1) + " sub-flows it runs" : string.Empty)} " +
                        $"changed while the run was suspended. Step {state.NextStepIndex} is no longer " +
                        "the step this run stopped at, and resuming would report on a flow that was never run as a whole");
                    return false;
                }

                if (state.SecondsLeft <= 0)
                {
                    Abort(m_Paths, state, RunState.Failed,
                        $"the run exceeded its budget of {state.BudgetSeconds:0.#}s while waiting for the domain reload");
                    return false;
                }

                m_State = state;
                return true;
            }

            /// <summary>Rebuild the run and hand it back to the frame pump.</summary>
            private void Resume()
            {
                if (m_State.Gate == ResumeGate.EditMode)
                {
                    // Leaving play mode is the moment the editor can take its own scene back, and
                    // the step that asked for it no longer exists to do the restoring.
                    var restored = PlayModeGate.ReleaseScene(m_State);
                    if (restored != null)
                        Append(m_Paths, m_State.ProgressSequence, "run.note", restored);
                }

                FlowVocabulary vocabulary;
                FlowDocument document;

                try
                {
                    vocabulary = new FlowVocabulary();
                    if (vocabulary.Conflicts.Count > 0)
                    {
                        Abort(m_Paths, m_State, RunState.Errored,
                            "conflicting [FlowCommand] names after the reload: " + string.Join("; ", vocabulary.Conflicts));
                        return;
                    }

                    // The ledger's env is re-applied as overrides. It holds the EFFECTIVE values, so
                    // every name in it is one the flow declares — the hash check above already
                    // proved the file has not changed — and re-parsing therefore reproduces exactly
                    // the steps the first segment ran, rather than the file's bare defaults.
                    if (!FlowEnv.TryParsePairs(m_State.Env, out var env, out var envError))
                    {
                        Abort(m_Paths, m_State, RunState.Errored,
                            $"the resume ledger's env is unreadable, so the remaining steps cannot be built with the " +
                            $"parameters the first segment used: {envError}");
                        return;
                    }

                    document = new FlowParser().ParseFile(m_State.FlowPath, vocabulary, env);
                }
                catch (FlowParseException ex)
                {
                    Abort(m_Paths, m_State, RunState.Errored, ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Abort(m_Paths, m_State, RunState.Errored, $"{ex.GetType().Name}: {ex.Message}");
                    return;
                }

                // Disarm before the first step runs. From here on an interruption is an accident
                // again, and replaying the steps this segment is about to run would be wrong.
                m_State.Disarm();
                m_State.Save(m_Paths);

                var runner = new FlowRunner(
                    document,
                    vocabulary,
                    m_Paths,
                    TimeSpan.FromSeconds(m_State.SecondsLeft),
                    m_State.AllowUnverifiedOcclusion,
                    m_State);

                var paths = m_Paths;
                var state = m_State;

                var driver = new FlowDriver(runner.Run(), error => OnSegmentCompleted(paths, state, runner, error));
                driver.Start();
            }
        }
    }
}
