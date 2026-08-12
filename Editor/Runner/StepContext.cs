using System;
using System.Collections.Generic;
using System.Text;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Report;

namespace UnityFlow.Editor.Runner
{
    /// <summary>How pointer input is produced for this run. Chosen once, reported, never varied per step.</summary>
    public enum WriteMode
    {
        /// <summary>A real virtual device is driven. Occlusion is genuine; gameplay input works.</summary>
        DeviceInjection,

        /// <summary>
        /// The UI system's own event sequence is synthesized. Works with no Input System and in
        /// edit mode, but it proves less: nothing outside the UI system observes the input.
        /// </summary>
        SemanticDispatch
    }

    /// <summary>Why a step failed, with everything the report needs.</summary>
    public sealed class StepFailure
    {
        public string Summary { get; }
        public string Detail { get; }
        public IReadOnlyList<string> NearMisses { get; }

        public StepFailure(string summary, string detail = null, IReadOnlyList<string> nearMisses = null)
        {
            Summary = summary;
            Detail = detail;
            NearMisses = nearMisses ?? Array.Empty<string>();
        }
    }

    /// <summary>Everything a step implementation is allowed to touch.</summary>
    public sealed class StepContext
    {
        public BackendRegistry Registry { get; }
        public SelectorResolver Resolver { get; }
        public RunPaths Paths { get; }
        public ConsoleRing Console { get; }

        private readonly Func<WriteMode> m_ResolveWriteMode;
        private readonly NdjsonWriter m_Progress;
        private WriteMode? m_WriteMode;

        /// <summary>
        /// How pointer input is produced. Resolved on FIRST USE, then frozen for the rest of the run.
        ///
        /// It cannot be decided when the run starts, because a flow may CREATE the environment it
        /// needs: 'enterPlayMode' lives in the before: section, and device injection is genuinely
        /// unavailable until play mode is live (EventSystem is not [ExecuteAlways], so nothing
        /// dispatches in edit mode). Deciding up front made a flow that provisions itself fail
        /// before it had run a single step.
        ///
        /// Resolving lazily and then freezing keeps the property that matters — one mechanism for
        /// the whole run, reported once — without demanding the environment exist before the flow
        /// that builds it.
        /// </summary>
        public WriteMode WriteMode
        {
            get
            {
                if (m_WriteMode == null)
                    m_WriteMode = m_ResolveWriteMode();

                return m_WriteMode.Value;
            }
        }

        /// <summary>Whether the write mode has been decided yet. Used by reporting, never by steps.</summary>
        public bool WriteModeResolved => m_WriteMode != null;

        /// <summary>The step being executed.</summary>
        public FlowStep Step { get; internal set; }

        /// <summary>Section the current step belongs to: "before", "steps" or "after".</summary>
        public string Section { get; internal set; }

        /// <summary>Flat index of the current step across all three sections.</summary>
        public int StepIndex { get; internal set; }

        /// <summary>
        /// The run's resume ledger, or null when this run cannot survive a domain reload.
        ///
        /// Null is the answer for flow.run, which holds one HTTP request open for the whole run and
        /// dies with the domain. A step that deliberately reloads the domain must refuse to act on
        /// a null ledger rather than tear down a run that has no way back.
        /// </summary>
        public FlowResumeState Resume { get; internal set; }

        /// <summary>Absolute <see cref="FlowClock"/> time at which the step must give up.</summary>
        public double Deadline { get; internal set; }

        /// <summary>Console cursor when the step began, so the report shows only this step's output.</summary>
        public int ConsoleCursorAtStart { get; internal set; }

        /// <summary>
        /// Console cursor as it stood when the PREVIOUS step began.
        ///
        /// This is what a log assertion usually means by "since": the action that should have
        /// logged ran in the step before, so scoping to this step alone would look at an empty
        /// window and report nothing found.
        /// </summary>
        public int ConsoleCursorBeforePreviousStep { get; internal set; }

        /// <summary>Set by a step to fail it. Leaving it null means the step passed.</summary>
        public StepFailure Failure { get; set; }

        /// <param name="progress">
        /// The run's progress stream, so a step can put a record of its own on it. Null only for a
        /// context built outside a run (the invoker tests do this); <see cref="Note"/> then refuses
        /// rather than dropping the record, because the records that go through it are things a
        /// reader must not be able to miss.
        /// </param>
        public StepContext(BackendRegistry registry, SelectorResolver resolver, RunPaths paths,
            ConsoleRing console, Func<WriteMode> resolveWriteMode, NdjsonWriter progress = null)
        {
            Registry = registry;
            Resolver = resolver;
            Paths = paths;
            Console = console;
            m_ResolveWriteMode = resolveWriteMode ?? throw new ArgumentNullException(nameof(resolveWriteMode));
            m_Progress = progress;
        }

        public bool DeadlineReached => FlowClock.Now >= Deadline;

        public void Fail(string summary, string detail = null, IReadOnlyList<string> nearMisses = null) =>
            Failure = new StepFailure(summary, detail, nearMisses);

        /// <summary>
        /// Put a record on the run's progress stream from inside a step.
        ///
        /// This exists for one class of record: something the step DID that changes what a pass is
        /// worth. 'navigateTo' seeding the very first selection through
        /// <c>EventSystem.SetSelectedGameObject</c> is the case it was added for — that one call is
        /// not input, and a reader who cannot see it in the stream would believe the whole path was
        /// walked by real key presses. Console logging would not do: the console is sampled only for
        /// FAILED steps, so a note attached to a passing step would never be shown.
        /// </summary>
        public void Note(string type, IReadOnlyList<KeyValuePair<string, object>> fields)
        {
            if (m_Progress == null)
            {
                throw new InvalidOperationException(
                    $"a step tried to write the progress record '{type}' but this StepContext was built without a " +
                    "progress stream; records that qualify for Note() are ones a reader must not miss, so dropping " +
                    "it silently is not an option");
            }

            m_Progress.Write(type, fields);
        }

        /// <summary>
        /// Build the diagnostic block appended to every failure: what the UI actually looked like,
        /// and what was logged while the step ran.
        ///
        /// This is the part that turns a timeout into a diagnosis. "waitFor timed out" tells a
        /// reader nothing; "the object exists at alpha 0.00 AND a NullReferenceException was thrown
        /// in AchievementSystem.Evaluate:42" tells them exactly where to go.
        /// </summary>
        public string BuildDiagnostics(int maxNodes = 25)
        {
            var sb = new StringBuilder();

            sb.Append("\n  UI snapshot at failure:");
            var options = EnumerateOptions.Diagnostic;
            var buffer = new List<UiNode>(256);
            var shown = 0;
            var total = 0;

            foreach (var backend in Registry.Active)
            {
                buffer.Clear();
                backend.Enumerate(in options, buffer);
                total += buffer.Count;

                for (var i = 0; i < buffer.Count && shown < maxNodes; i++)
                {
                    var node = buffer[i];

                    // Nameless, textless decoration is noise in a report; it never identifies anything.
                    if (string.IsNullOrEmpty(node.Text) && string.IsNullOrEmpty(node.TestId) && !node.IsVisible)
                        continue;

                    sb.Append("\n    ").Append(node.Path);
                    if (!string.IsNullOrEmpty(node.Text))
                        sb.Append("  \"").Append(Truncate(node.Text, 40)).Append('"');
                    sb.Append(node.IsVisible ? "  visible" : "  hidden");
                    if (!node.IsVisible && !string.IsNullOrEmpty(node.Reason))
                        sb.Append(" (").Append(node.Reason).Append(')');

                    shown++;
                }
            }

            if (shown == 0)
                sb.Append("\n    (no UI nodes found at all)");
            else if (total > shown)
                sb.Append("\n    ... ").Append(total - shown).Append(" more nodes not shown");

            var problems = Console.Since(ConsoleCursorAtStart, problemsOnly: true);
            if (problems.Count > 0)
            {
                sb.Append("\n\n  Console since step start:");
                for (var i = 0; i < problems.Count && i < 10; i++)
                {
                    sb.Append("\n    [").Append(problems[i].Type).Append("] ").Append(Truncate(problems[i].Message, 200));

                    var frame = FirstUsefulFrame(problems[i].StackTrace);
                    if (frame != null)
                        sb.Append("\n        at ").Append(frame);
                }

                if (problems.Count > 10)
                    sb.Append("\n    ... ").Append(problems.Count - 10).Append(" more");
            }

            var dropped = Console.DroppedSince(ConsoleCursorAtStart);
            if (dropped > 0)
                sb.Append("\n  (").Append(dropped).Append(" earlier console messages were dropped by the ring buffer)");

            return sb.ToString();
        }

        private static string FirstUsefulFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return null;

            foreach (var line in stackTrace.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;

                // Skip the logging plumbing itself; the first frame below it is the real site.
                if (trimmed.StartsWith("UnityEngine.Debug", StringComparison.Ordinal) ||
                    trimmed.StartsWith("UnityEngine.Logger", StringComparison.Ordinal))
                {
                    continue;
                }

                return trimmed;
            }

            return null;
        }

        private static string Truncate(string value, int max) =>
            value != null && value.Length > max ? value.Substring(0, max) + "..." : value;
    }
}
