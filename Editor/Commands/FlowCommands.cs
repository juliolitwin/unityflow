using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;
using UnityFlow.Editor.State;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Commands
{
    /// <summary>
    /// The CLI surface. Thin by design: everything of substance happens in the editor, and these
    /// only start it, report on it, or describe it.
    /// </summary>
    public static class FlowCommands
    {
        /// <summary>
        /// Help for the <c>env</c> argument of both run commands. Written once because the two must
        /// describe the same surface; a drift between them is a bug report waiting to happen.
        /// </summary>
        private const string EnvHelp =
            "Variables for the flow's 'env:' block, each written 'name=value'. Repeat the option, or pass a JSON " +
            "array. An override beats the flow's default; a name the flow does not declare is refused rather than " +
            "ignored. The effective set is recorded in the run's progress header.";

        /// <summary>
        /// Run a flow.
        ///
        /// The signature is deliberately <c>Task&lt;T&gt;</c> returned from a NON-async method, and
        /// that detail is what makes the whole design work. The pipeline server invokes a
        /// MainThreadRequired handler through Dispatcher.Invoke, which occupies the main thread for
        /// the handler's whole duration — measured: a probe that busy-waited 1.5 real seconds saw
        /// Time.frameCount go from 12 to 12, so nothing that needs a frame can ever happen inside
        /// one. This method therefore only REGISTERS the frame driver and returns a pending Task in
        /// the same tick. The server then awaits that Task with ConfigureAwait(false) on a
        /// background thread, which leaves the main thread free to keep pumping — and the flow
        /// advances frame by frame while the request is still open.
        ///
        /// Live progress goes to the run's NDJSON file, not to this response: the server serves
        /// requests strictly one at a time, so nothing can poll the editor while this is in flight.
        ///
        /// What it CANNOT do is span a domain reload, because the reload destroys the server and
        /// this very request along with the runner. A flow that uses <c>enterPlayMode</c> must be
        /// started with <see cref="Start"/> instead.
        /// </summary>
        [CliCommand("flow.run",
            "Run a UnityFlow YAML flow. Progress streams to <project>/.unityflow/runs/<runId>/progress.ndjson.",
            MainThreadRequired = true)]
        public static Task<FlowRunResult> Run(
            [CliArg("file", "Path to the .flow.yaml, absolute or relative to the project root", Required = true)] string file,
            [CliArg("runId", "Caller-supplied run id; the host CLI tails this run's folder")] string runId = null,
            [CliArg("budgetMs", "Hard ceiling for the whole run")] int budgetMs = 300000,
            [CliArg("allowUnverifiedOcclusion", "Permit taps when occlusion cannot be verified (edit mode)")] bool allowUnverifiedOcclusion = false,
            [CliArg("env", EnvHelp)] string[] env = null)
        {
            runId = string.IsNullOrWhiteSpace(runId) ? NewRunId() : runId;

            var paths = RunPaths.CreateFor(runId);
            var completion = new TaskCompletionSource<FlowRunResult>();

            FlowDocument document;
            FlowVocabulary vocabulary;

            try
            {
                var absolute = ResolvePath(file);
                if (!File.Exists(absolute))
                    return Completed(Errored(runId, paths, $"flow file not found: {absolute}"));

                if (!FlowEnv.TryParsePairs(env, out var overrides, out var envError))
                    return Completed(Errored(runId, paths, envError));

                vocabulary = new FlowVocabulary();
                if (vocabulary.Conflicts.Count > 0)
                    return Completed(Errored(runId, paths, "conflicting [FlowCommand] names: " + string.Join("; ", vocabulary.Conflicts)));

                document = new FlowParser().ParseFile(absolute, vocabulary, overrides);
            }
            catch (FlowParseException ex)
            {
                return Completed(Errored(runId, paths, ex.Message));
            }
            catch (Exception ex)
            {
                return Completed(Errored(runId, paths, $"{ex.GetType().Name}: {ex.Message}"));
            }

            var runner = new FlowRunner(document, vocabulary, paths,
                TimeSpan.FromMilliseconds(budgetMs), allowUnverifiedOcclusion);

            var driver = new FlowDriver(runner.Run(), error =>
            {
                if (error != null && runner.Result == null)
                {
                    // A domain reload is the one interruption with a cure, and naming it is the
                    // whole value of the message: the caller has to switch commands, not retry.
                    var summary = error is FlowInterruptedException
                        ? $"{error.Message} This run was started with flow.run, which lives inside a single HTTP " +
                          $"request and cannot come back from a reload. Re-run it as: flow.start --file {file}"
                        : $"{error.GetType().Name}: {error.Message}";

                    completion.TrySetResult(Errored(runId, paths, summary));
                    return;
                }

                completion.TrySetResult(runner.Result ?? Errored(runId, paths, "the run produced no result"));
            });

            driver.Start();
            return completion.Task;
        }

        /// <summary>
        /// Register a run and return its id immediately, without waiting for it to finish.
        ///
        /// This is the mode for a flow that enters play mode, and wherever the transition performs a
        /// domain reload it is the ONLY mode that can: the reload destroys the HTTP
        /// server, the bearer token and any request in flight. Nothing that waits for a response can
        /// survive that, so this deliberately does not wait for one — it writes a resume ledger,
        /// starts the frame driver and returns in the same tick. <see cref="Runner.FlowResumer"/>
        /// rebuilds the run on the far side of the reload.
        ///
        /// The caller then polls <see cref="Status"/>, which reads a file and is declared
        /// MainThreadRequired = false, so it keeps answering while the editor compiles and reloads.
        /// </summary>
        [CliCommand("flow.start",
            "Start a UnityFlow run and return its id immediately; poll flow.status for the outcome. Required for flows that enter play mode.",
            MainThreadRequired = true)]
        public static object Start(
            [CliArg("file", "Path to the .flow.yaml, absolute or relative to the project root", Required = true)] string file,
            [CliArg("runId", "Caller-supplied run id; the host CLI tails this run's folder")] string runId = null,
            [CliArg("budgetMs", "Hard ceiling for the whole run, measured on the wall clock across reloads")] int budgetMs = 300000,
            [CliArg("allowUnverifiedOcclusion", "Permit taps when occlusion cannot be verified (edit mode)")] bool allowUnverifiedOcclusion = false,
            [CliArg("env", EnvHelp)] string[] env = null)
        {
            runId = string.IsNullOrWhiteSpace(runId) ? NewRunId() : runId;

            if (!TryClaimResumeSlot(out var claimError))
                return Errored(runId, RunPaths.Existing(runId), claimError);

            var paths = RunPaths.CreateFor(runId);

            FlowDocument document;
            FlowVocabulary vocabulary;
            string absolute;

            try
            {
                absolute = ResolvePath(file);
                if (!File.Exists(absolute))
                    return Errored(runId, paths, $"flow file not found: {absolute}");

                if (!FlowEnv.TryParsePairs(env, out var overrides, out var envError))
                    return Errored(runId, paths, envError);

                vocabulary = new FlowVocabulary();
                if (vocabulary.Conflicts.Count > 0)
                    return Errored(runId, paths, "conflicting [FlowCommand] names: " + string.Join("; ", vocabulary.Conflicts));

                document = new FlowParser().ParseFile(absolute, vocabulary, overrides);
            }
            catch (FlowParseException ex)
            {
                return Errored(runId, paths, ex.Message);
            }
            catch (Exception ex)
            {
                return Errored(runId, paths, $"{ex.GetType().Name}: {ex.Message}");
            }

            var resume = new FlowResumeState
            {
                RunId = runId,
                Token = Guid.NewGuid().ToString("N"),
                FlowPath = absolute,

                // Every file the step list came from, not only the one that was started: a sub-flow
                // edited while the run is suspended shifts every step index after it, and the
                // resumed segment has to detect that just as loudly as an edit to the parent.
                SourceFiles = ToArray(document.SourceFiles),
                FlowHash = FlowResumeState.HashFiles(document.SourceFiles),
                FlowName = document.Name,

                // The effective env, not the overrides: the CLI process that supplied them dies with
                // the first domain, and the segment on the far side must not fall back to defaults.
                Env = document.Env.ToPairs(),
                Section = "before",
                NextStepIndex = 0,
                StepCount = document.Before.Count + document.Steps.Count + document.After.Count,
                StartedAtUtc = FlowResumeState.NowUtc,
                BudgetSeconds = budgetMs / 1000.0,
                AllowUnverifiedOcclusion = allowUnverifiedOcclusion
            };

            resume.Save(paths);
            resume.ToStatus(RunState.Pending, null).Save(paths.StatusFile);

            var runner = new FlowRunner(document, vocabulary, paths,
                TimeSpan.FromMilliseconds(budgetMs), allowUnverifiedOcclusion, resume);

            var driver = new FlowDriver(runner.Run(),
                error => FlowResumer.OnSegmentCompleted(paths, resume, runner, error));

            driver.Start();

            return new
            {
                runId,
                state = nameof(RunState.Running),
                flow = document.Name,
                steps = resume.StepCount,
                env = resume.Env,
                runDirectory = paths.RunDirectory,
                statusFile = paths.StatusFile,
                progressFile = paths.ProgressFile,
                resumeFile = FlowResumeState.PathFor(paths),
                poll = $"flow.status --runId {runId}"
            };
        }

        /// <summary>
        /// Refuse to start while another run still owns the session's resume slot.
        ///
        /// There is exactly one slot, because there is exactly one editor to put into play mode.
        /// Overwriting it would orphan the other run: its ledger would still be on disk while the
        /// resumer picked up the newer one, and the older run would hang at Running forever.
        /// </summary>
        private static bool TryClaimResumeSlot(out string error)
        {
            var pending = FlowResumeState.PendingRunId();
            if (pending == null)
            {
                error = null;
                return true;
            }

            var paths = RunPaths.Existing(pending);

            if (FlowResumeState.TryLoad(paths, out _, out _))
            {
                error = $"run '{pending}' is still in progress and owns this editor session. " +
                        $"Wait for it, or stop it with flow.cancel --runId {pending}";
                return false;
            }

            // The ledger is gone, so the run it belonged to is over and only the session flag was
            // left behind — by an editor kill, or by a run that ended while suspended.
            FlowResumeState.ClearSession();
            error = null;
            return true;
        }

        /// <summary>
        /// Read a run's current state.
        ///
        /// MainThreadRequired is false on purpose: it reads a file, so it can answer while the main
        /// thread is busy compiling or mid-domain-reload — which are exactly the moments a caller
        /// most needs to know what happened.
        /// </summary>
        [CliCommand("flow.status", "Read the current state of a run by id.", MainThreadRequired = false)]
        public static object Status(
            [CliArg("runId", "The run id to report on", Required = true)] string runId)
        {
            var paths = RunPaths.Existing(runId);

            if (!File.Exists(paths.StatusFile))
                return new { runId, state = "Unknown", error = $"no run named '{runId}' under {paths.RunDirectory}" };

            return new
            {
                runId,
                status = File.ReadAllText(paths.StatusFile),
                progressFile = paths.ProgressFile,
                runDirectory = paths.RunDirectory
            };
        }

        /// <summary>Ask a run to stop. The runner notices the sentinel at its next step boundary.</summary>
        [CliCommand("flow.cancel", "Request cancellation of a run.", MainThreadRequired = false)]
        public static object Cancel(
            [CliArg("runId", "The run id to cancel", Required = true)] string runId)
        {
            var paths = RunPaths.Existing(runId);
            if (!Directory.Exists(paths.RunDirectory))
                return new { runId, cancelled = false, error = "no such run" };

            File.WriteAllText(paths.CancelFile, "cancel");
            return new { runId, cancelled = true };
        }

        /// <summary>
        /// List every verb a flow may use, built-ins and project commands alike.
        /// This is how an agent discovers the surface instead of guessing at it.
        /// </summary>
        [CliCommand("flow.commands", "List all flow verbs, including project [FlowCommand] methods.", MainThreadRequired = true)]
        public static object Commands()
        {
            var vocabulary = new FlowVocabulary();
            var verbs = new List<object>();

            foreach (var name in vocabulary.VerbNames)
            {
                vocabulary.TryGetVerb(name, out var spec);

                var args = new List<object>();
                foreach (var arg in spec.Args)
                    args.Add(new { name = arg.Name, type = arg.Kind.ToString(), required = arg.Required, description = arg.Description });

                vocabulary.Commands.TryGetValue(name, out var method);

                verbs.Add(new
                {
                    name,
                    source = method == null ? "builtin" : $"{method.DeclaringType?.Name}.{method.Name}",
                    selector = spec.Selector.ToString(),
                    description = spec.Description,
                    args
                });
            }

            return new { count = verbs.Count, verbs, conflicts = vocabulary.Conflicts };
        }

        /// <summary>
        /// Dump the live UI as UnityFlow sees it.
        ///
        /// This is the discovery half of the agent loop: without it, both a human and an agent are
        /// guessing at testIds and node names. It reports hidden nodes too, with the reason, because
        /// "it exists at alpha 0" and "it does not exist" call for completely different fixes.
        /// </summary>
        [CliCommand("flow.snapshot", "Dump the live UI tree as UnityFlow sees it.", MainThreadRequired = true)]
        public static object Snapshot(
            [CliArg("visibleOnly", "Omit hidden nodes")] bool visibleOnly = false,
            [CliArg("text", "Only nodes whose visible text contains this")] string text = null,
            [CliArg("max", "Maximum nodes to return")] int max = 400)
        {
            var registry = new BackendRegistry();
            if (registry.Active.Count == 0)
                return new { error = "no UI backend available", rejected = registry.Rejected };

            var options = visibleOnly ? EnumerateOptions.Default : EnumerateOptions.Diagnostic;
            var buffer = new List<UiNode>(256);
            var nodes = new List<object>();

            foreach (var backend in registry.Active)
            {
                backend.Settle();
                buffer.Clear();
                backend.Enumerate(in options, buffer);

                foreach (var node in buffer)
                {
                    if (nodes.Count >= max)
                        break;

                    if (text != null && (node.Text == null ||
                        node.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        continue;
                    }

                    // UiNode.InjectionPoint is defined as already hit-probed, and enumeration cannot
                    // probe, so the backend is asked here instead. Only for nodes that could be
                    // tapped at all: probing every hidden node would raycast the whole scene once
                    // per node, and a non-actionable node has nowhere to inject by definition.
                    object injectionPoint = null;
                    string injectionReason = null;
                    if (node.IsActionable &&
                        backend.TryResolveInjectionPoint(node.Handle, out var point, out injectionReason))
                    {
                        injectionPoint = new { x = point.x, y = point.y };
                    }

                    nodes.Add(new
                    {
                        backend = backend.Id,
                        path = node.Path,
                        type = node.Type,
                        testId = node.TestId,
                        text = node.Text,
                        value = node.Value,
                        visible = node.IsVisible,
                        enabled = node.IsEnabled,
                        actionable = node.IsActionable,
                        rect = node.ScreenRect.HasValue
                            ? new { x = node.ScreenRect.Value.x, y = node.ScreenRect.Value.y, w = node.ScreenRect.Value.width, h = node.ScreenRect.Value.height }
                            : null,
                        injectionPoint,
                        injectionPointReason = injectionReason,
                        reason = node.Reason
                    });
                }
            }

            return new
            {
                playMode = EditorApplication.isPlaying,
                occlusion = registry.EffectiveOcclusionFidelity.ToString(),
                inputDriver = registry.InputDriver?.Id ?? "none",
                inputDriverReason = registry.InputDriverRejection,
                count = nodes.Count,
                nodes
            };
        }

        /// <summary>
        /// Dump the live objects a state query can read, with the CURRENT VALUE of every field and
        /// property on them.
        ///
        /// This is the discovery half of 'assert' and 'waitUntil'. Nobody guesses that health is
        /// 'm_Current' and not 'current', and a query that names the wrong member fails in a way
        /// that looks exactly like the game being wrong — so without this the verbs are unusable.
        /// </summary>
        [CliCommand("flow.probe",
            "List live instances of a component type with every readable field/property and its current value, for writing assert/waitUntil. " +
            "NOTE: a probe lists MonoBehaviours only - state held off components (ECS worlds, statics, plain C# services) " +
            "will not appear here; reach it with assert's 'expr'.",
            MainThreadRequired = true)]
        public static object Probe(
            [CliArg("component", "Component type name, simple or full. An ambiguous simple name fails with the candidates listed")] string component = null,
            [CliArg("find", "Scope to GameObjects with this name, or this full hierarchy path when it contains '/'")] string find = null,
            [CliArg("max", "Maximum instances to list")] int max = 10,
            [CliArg("maxMembers", "Maximum fields/properties per instance")] int maxMembers = 80,
            [CliArg("includeUnityBase", "Also list members inherited from MonoBehaviour/Behaviour/Component/Object")] bool includeUnityBase = false)
        {
            if (string.IsNullOrWhiteSpace(component) && string.IsNullOrWhiteSpace(find))
                return new { error = "flow.probe needs --component <type>, --find <name-or-path>, or both" };

            if (max < 1 || maxMembers < 1)
                return new { error = "--max and --maxMembers must be at least 1" };

            return string.IsNullOrWhiteSpace(component)
                ? ProbeReport.ForObject(find.Trim(), max, maxMembers, includeUnityBase)
                : ProbeReport.ForComponent(component.Trim(), string.IsNullOrWhiteSpace(find) ? null : find.Trim(), max, maxMembers, includeUnityBase);
        }

        /// <summary>
        /// Preflight the environment and say precisely what would stop a flow from working.
        ///
        /// The failure this exists to prevent is the silent one: injected input being discarded
        /// because the Game View is unfocused, which is the default configuration and exactly the
        /// CI and agent case. Reporting it up front beats a flow that times out with no explanation.
        /// </summary>
        [CliCommand("flow.doctor", "Report whether the environment can actually run flows.", MainThreadRequired = true)]
        public static object Doctor()
        {
            var registry = new BackendRegistry();
            var vocabulary = new FlowVocabulary();
            var backends = new List<object>();

            foreach (var backend in registry.Active)
            {
                backends.Add(new
                {
                    id = backend.Id,
                    capabilities = backend.Capabilities.ToString(),
                    occlusion = backend.OcclusionFidelity.ToString(),
                    surfaces = backend.GetSurfaces().Count
                });
            }

            var driverOk = registry.InputDriver != null;
            var capture = Capture.FlowCapture.IsAvailable(out var captureReason);

            return new
            {
                ok = registry.Active.Count > 0 && driverOk,
                playMode = EditorApplication.isPlaying,
                compiling = EditorApplication.isCompiling,
                backends,
                rejectedBackends = registry.Rejected,
                inputDriver = registry.InputDriver?.Id,
                inputDriverReason = registry.InputDriverRejection,
                occlusion = registry.EffectiveOcclusionFidelity.ToString(),
                screenshots = capture ? "available" : captureReason,
                verbs = vocabulary.VerbNames.Count,
                verbConflicts = vocabulary.Conflicts,
                runsDirectory = Path.Combine(RunPaths.ProjectRoot, RunPaths.RootFolderName, "runs")
            };
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
                copy[i] = values[i];

            return copy;
        }

        private static Task<FlowRunResult> Completed(FlowRunResult result) =>
            Task.FromResult(result);

        private static FlowRunResult Errored(string runId, RunPaths paths, string message)
        {
            var status = new RunStatus
            {
                RunId = runId,
                State = RunState.Errored,
                FailureSummary = message,
                StartedAtUtc = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
            };

            status.Save(paths.StatusFile);

            return new FlowRunResult
            {
                RunId = runId,
                State = nameof(RunState.Errored),
                Failure = message,
                RunDirectory = paths.RunDirectory
            };
        }

        private static string ResolvePath(string file) =>
            Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(RunPaths.ProjectRoot, file));

        /// <summary>
        /// A fresh run id. Internal because an in-process caller — the runner window — has to know
        /// the id BEFORE the run starts, in order to tail its folder, and both commands only report
        /// theirs back in a result that a started run does not produce until it ends.
        /// </summary>
        internal static string NewRunId() =>
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
            Guid.NewGuid().ToString("N").Substring(0, 6);
    }
}
