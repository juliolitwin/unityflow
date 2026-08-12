using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityFlow.Editor.Runner
{
    /// <summary>What a resumed segment must observe before it is allowed to run the next step.</summary>
    public enum ResumeGate
    {
        /// <summary>Nothing to wait for; the next step may run as soon as the editor is idle.</summary>
        None,

        /// <summary>Play mode must be active before the next step runs.</summary>
        PlayMode,

        /// <summary>Edit mode must be back before the next step runs.</summary>
        EditMode
    }

    /// <summary>
    /// The ledger that lets one run span a domain reload.
    ///
    /// Entering play mode does a FULL domain reload wherever EditorSettings
    /// enterPlayModeOptionsEnabled is false, which is the default and which most projects with
    /// non-reload-safe statics keep. A reload destroys the runner, the driver, the HTTP
    /// server and the in-flight request, so a run cannot be kept alive across it; it can only be
    /// REBUILT on the far side. This is everything needed to rebuild it.
    ///
    /// Resume granularity is the STEP BOUNDARY. Mid-step state (a half-issued tap, a resolver
    /// binding) cannot survive and is never faked: a step that triggers the reload records the
    /// index of the step AFTER itself, and the resumed segment starts there.
    ///
    /// Two copies are kept, and they answer different questions:
    /// <list type="bullet">
    /// <item>DISK (<c>resume.json</c> in the run folder) is the source of truth. It survives an
    /// editor kill and the host CLI can read it without asking Unity anything.</item>
    /// <item><see cref="SessionState"/> is only the "a run is in progress" flag. It survives a
    /// domain reload but NOT an editor restart, which is exactly the discrimination wanted: a
    /// resume.json left behind by a killed editor must never make the next session resume a run
    /// nobody is waiting for.</item>
    /// </list>
    /// The two carry a matching <see cref="Token"/> so a disagreement between them is detected and
    /// reported rather than resumed through.
    /// </summary>
    [Serializable]
    public sealed class FlowResumeState
    {
        /// <summary>SessionState key holding the run id awaiting pickup. Empty when no run is in progress.</summary>
        public const string SessionRunIdKey = "UnityFlow.Resume.RunId";

        /// <summary>SessionState key holding the run's <see cref="Token"/>, to detect a stale disk ledger.</summary>
        public const string SessionTokenKey = "UnityFlow.Resume.Token";

        /// <summary>File name inside the run folder.</summary>
        public const string FileName = "resume.json";

        /// <summary>Run this ledger belongs to.</summary>
        public string RunId;

        /// <summary>Identifies this ledger to the session. Regenerated per run, never reused.</summary>
        public string Token;

        /// <summary>Absolute path of the flow file, so the resumed segment can re-parse it.</summary>
        public string FlowPath;

        /// <summary>
        /// SHA-256 over EVERY file the step list was built from, as they were when the run started.
        /// Guards against resuming into a different flow.
        /// </summary>
        public string FlowHash;

        /// <summary>
        /// Absolute paths of those files, <see cref="FlowPath"/> first and then each sub-flow
        /// <c>runFlow</c> pulled in.
        ///
        /// It has to be recorded rather than rediscovered, because the resumed segment must be able
        /// to detect a change BEFORE it re-parses — and re-parsing is the only way to learn the list
        /// again. Recording it closes the loop: a sub-flow edited while the run was suspended
        /// changes the hash, and a parent edited to include different sub-flows changes it too, so
        /// the recorded set can never silently stop describing the run.
        /// </summary>
        public string[] SourceFiles;

        /// <summary>Flow name, so an aborted run still reports a complete status.</summary>
        public string FlowName;

        /// <summary>
        /// The EFFECTIVE variables of the run, as <c>name=value</c> strings — the flow's <c>env:</c>
        /// defaults with the caller's <c>--env</c> overrides already applied.
        ///
        /// It has to travel in the ledger for the same reason <see cref="FlowHash"/> does. The
        /// resumed segment re-parses the flow file from scratch in a brand new domain, and the
        /// overrides were arguments to a CLI process that died with the first domain. Without this
        /// the second segment would substitute the flow's DEFAULTS and run the rest of the flow
        /// against a different character than the half that already ran — silently, because every
        /// step would still pass its own validation.
        ///
        /// It is a <c>string[]</c> rather than a dictionary because <see cref="JsonUtility"/>
        /// serializes the former and ignores the latter, and because it is then the same text the
        /// CLI accepts and the run header prints. Empty means the flow declares no variables.
        /// </summary>
        public string[] Env;

        /// <summary>Section holding <see cref="NextStepIndex"/>: "before", "steps" or "after".</summary>
        public string Section;

        /// <summary>Flat index, across all three sections, of the first step the resumed segment must run.</summary>
        public int NextStepIndex;

        /// <summary>Total steps in the flow, so a status written without a runner is still complete.</summary>
        public int StepCount;

        /// <summary>Description of the last step that ran, for the same reason.</summary>
        public string StepDescription;

        /// <summary>NDJSON sequence reached before the reload, so the resumed segment continues the stream instead of restarting it.</summary>
        public int ProgressSequence;

        /// <summary>Unix seconds at which the whole run started. The budget is measured from here, never from a per-domain clock.</summary>
        public double StartedAtUtc;

        /// <summary>Ceiling for the whole run, in seconds.</summary>
        public double BudgetSeconds;

        /// <summary>The flag the run was started with, re-applied to every segment.</summary>
        public bool AllowUnverifiedOcclusion;

        /// <summary>Write mode reported in the run header, so a change across the reload is reported rather than hidden.</summary>
        public string ReportedWriteMode;

        /// <summary>Occlusion fidelity reported in the run header, for the same reason.</summary>
        public string ReportedOcclusion;

        /// <summary>Input driver reported in the run header, for the same reason.</summary>
        public string ReportedInputDriver;

        /// <summary>Whether a step of the body has already failed. Without this a resumed teardown would report a pass.</summary>
        public bool Failed;

        /// <summary>The failure that made <see cref="Failed"/> true.</summary>
        public string FailureSummary;

        /// <summary>
        /// True when a step DELIBERATELY triggered the reload. An unarmed ledger found after a
        /// reload means something else tore the domain down mid-step, which is a failure to report,
        /// not a state to resume from.
        /// </summary>
        public bool Armed;

        /// <summary>What the resumed segment must wait for.</summary>
        public ResumeGate Gate;

        /// <summary>Unix seconds after which the gate has waited long enough. Wall clock, because no in-domain clock survives.</summary>
        public double GateDeadlineUtc;

        /// <summary>How long the gate was given, kept so a timeout can say what it was measured against.</summary>
        public double GateTimeoutSeconds;

        /// <summary>True when the run opened a scene and the editor's previous scene must be put back.</summary>
        public bool SceneRestorePending;

        /// <summary>
        /// Project-relative path of the scene that was open before the run changed it. Empty means
        /// the editor was on an untitled empty scene, which is restored by making a new one.
        /// </summary>
        public string SceneToRestore;

        /// <summary>Unix seconds. The one clock a run may measure itself against across a domain reload.</summary>
        public static double NowUtc => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

        /// <summary>Seconds left of the run's budget, measured from the original start.</summary>
        public double SecondsLeft => BudgetSeconds - (NowUtc - StartedAtUtc);

        /// <summary>Absolute path of the ledger for a run.</summary>
        public static string PathFor(RunPaths paths) => Path.Combine(paths.RunDirectory, FileName);

        /// <summary>The run id awaiting pickup in this editor session, or null when there is none.</summary>
        public static string PendingRunId()
        {
            var runId = SessionState.GetString(SessionRunIdKey, string.Empty);
            return string.IsNullOrEmpty(runId) ? null : runId;
        }

        /// <summary>The token the session expects the disk ledger to carry.</summary>
        public static string PendingToken() => SessionState.GetString(SessionTokenKey, string.Empty);

        /// <summary>Record a resume point and what the resumed segment must wait for.</summary>
        public void Arm(ResumeGate gate, int nextStepIndex, string section, double timeoutSeconds)
        {
            if (gate == ResumeGate.None)
                throw new ArgumentException("Arming with ResumeGate.None would resume with nothing to wait for.", nameof(gate));

            Armed = true;
            Gate = gate;
            NextStepIndex = nextStepIndex;
            Section = section;
            GateTimeoutSeconds = timeoutSeconds;
            GateDeadlineUtc = NowUtc + timeoutSeconds;
        }

        /// <summary>Cancel a resume point, for a transition that completed without a reload or failed before one.</summary>
        public void Disarm()
        {
            Armed = false;
            Gate = ResumeGate.None;
            GateDeadlineUtc = 0;
        }

        /// <summary>
        /// Write the ledger to disk and publish the session flag.
        ///
        /// Atomic (temp file then move) for the same reason <see cref="RunStatus.Save"/> is: the
        /// host CLI may read it at any moment, and half a JSON object reads as a corrupt run rather
        /// than as a run in progress.
        /// </summary>
        public void Save(RunPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));

            var path = PathFor(paths);
            Directory.CreateDirectory(paths.RunDirectory);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(this, true), new UTF8Encoding(false));

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temp, path);

            SessionState.SetString(SessionRunIdKey, RunId);
            SessionState.SetString(SessionTokenKey, Token);
        }

        /// <summary>Read a run's ledger. A missing or unreadable file is reported, never treated as "no resume needed".</summary>
        public static bool TryLoad(RunPaths paths, out FlowResumeState state, out string error)
        {
            state = null;
            var path = PathFor(paths);

            if (!File.Exists(path))
            {
                error = $"the resume ledger {path} does not exist, so there is nothing to rebuild the run from";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                error = $"the resume ledger {path} could not be read: {ex.Message}";
                return false;
            }

            try
            {
                state = JsonUtility.FromJson<FlowResumeState>(json);
            }
            catch (Exception ex)
            {
                error = $"the resume ledger {path} is not readable JSON ({ex.GetType().Name}: {ex.Message})";
                return false;
            }

            if (state == null || string.IsNullOrEmpty(state.RunId))
            {
                error = $"the resume ledger {path} parsed to nothing usable; it names no run";
                state = null;
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>Drop the ledger and the session flag. Called once a run reaches a terminal state.</summary>
        public void Clear(RunPaths paths)
        {
            var path = PathFor(paths);
            if (File.Exists(path))
                File.Delete(path);

            ClearSession();
        }

        /// <summary>Drop only the session flag, for a ledger whose run folder is already gone.</summary>
        public static void ClearSession()
        {
            SessionState.EraseString(SessionRunIdKey);
            SessionState.EraseString(SessionTokenKey);
        }

        /// <summary>
        /// Hash the flow file. Compared on resume so a run cannot silently continue into an edited
        /// flow: the step indices in this ledger would then point at different steps, and the report
        /// would describe a run that never happened.
        /// </summary>
        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Hex(sha.ComputeHash(stream));
        }

        /// <summary>
        /// Hash a whole set of flow files as one value, in the order given.
        ///
        /// Order is part of the hash on purpose: it is the order the parser read them in, so two
        /// runs whose sub-flows were rearranged are correctly reported as different flows even when
        /// the bytes are identical. Each file's own hash is folded in with its length, so no
        /// concatenation of two files can collide with a different split of the same bytes.
        /// </summary>
        public static string HashFiles(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0)
                throw new ArgumentException("A run has to be hashed against at least one flow file.", nameof(paths));

            if (paths.Count == 1)
                return HashFile(paths[0]);

            var combined = new StringBuilder(paths.Count * 80);

            for (var i = 0; i < paths.Count; i++)
                combined.Append(HashFile(paths[i])).Append(':').Append(paths[i].Length).Append('\n');

            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(combined.ToString())));
        }

        private static string Hex(byte[] hash)
        {
            var text = new StringBuilder(hash.Length * 2);

            foreach (var b in hash)
                text.Append(b.ToString("x2"));

            return text.ToString();
        }

        /// <summary>
        /// Build a status snapshot from the ledger alone, for the moments there is no runner to ask:
        /// the run is suspended awaiting a reload, or it is being aborted by the resumer.
        /// </summary>
        public RunStatus ToStatus(RunState state, string failure)
        {
            return new RunStatus
            {
                RunId = RunId,
                State = state,
                FlowName = FlowName,
                FlowPath = FlowPath,
                StepIndex = NextStepIndex - 1,
                StepCount = StepCount,
                StepDescription = StepDescription,
                FailureSummary = failure ?? FailureSummary,
                ProgressSequence = ProgressSequence,
                StartedAtUtc = StartedAtUtc,
                OcclusionFidelity = ReportedOcclusion,
                InputDriver = ReportedInputDriver
            };
        }
    }
}
