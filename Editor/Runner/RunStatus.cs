using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityFlow.Editor.Runner
{
    /// <summary>Lifecycle of a run, as seen by a poller.</summary>
    public enum RunState
    {
        /// <summary>Registered but not yet pumped.</summary>
        Pending,

        /// <summary>Advancing.</summary>
        Running,

        /// <summary>Waiting for the editor to come back from a domain reload.</summary>
        AwaitingReload,

        /// <summary>Every step passed.</summary>
        Passed,

        /// <summary>A step failed an assertion or timed out.</summary>
        Failed,

        /// <summary>Stopped by the cancel sentinel.</summary>
        Cancelled,

        /// <summary>Could not run at all (parse error, missing backend, wedged environment).</summary>
        Errored
    }

    /// <summary>
    /// The snapshot a poller reads.
    ///
    /// This is written to disk rather than held in memory because the two moments it matters most
    /// are exactly the moments memory does not survive: while the editor is recompiling, and
    /// across the domain reload that entering play mode causes. flow.status therefore reads this
    /// file and is declared MainThreadRequired = false, so it can answer even while the main
    /// thread is busy.
    /// </summary>
    public sealed class RunStatus
    {
        public string RunId;
        public RunState State = RunState.Pending;
        public string FlowName;
        public string FlowPath;
        public int StepIndex = -1;
        public int StepCount;
        public string StepDescription;
        public string FailureSummary;
        public int ProgressSequence;
        public double StartedAtUtc;
        public double UpdatedAtUtc;

        /// <summary>Occlusion fidelity actually achieved, so a reader knows what "passed" was worth.</summary>
        public string OcclusionFidelity;

        /// <summary>Input driver in use, or the reason there is none.</summary>
        public string InputDriver;

        public bool IsTerminal =>
            State == RunState.Passed || State == RunState.Failed ||
            State == RunState.Cancelled || State == RunState.Errored;

        /// <summary>
        /// Write atomically: write a temp file then move it over the target. A poller that reads
        /// while a plain writer is mid-write would otherwise get truncated JSON and report a
        /// parse error instead of the run's real state.
        /// </summary>
        public void Save(string path)
        {
            UpdatedAtUtc = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));

            if (File.Exists(path))
                File.Delete(path);

            File.Move(temp, path);
        }

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            Str(sb, "runId", RunId, first: true);
            Str(sb, "state", State.ToString());
            Str(sb, "flowName", FlowName);
            Str(sb, "flowPath", FlowPath);
            Num(sb, "stepIndex", StepIndex);
            Num(sb, "stepCount", StepCount);
            Str(sb, "step", StepDescription);
            Str(sb, "failure", FailureSummary);
            Num(sb, "progressSeq", ProgressSequence);
            Num(sb, "startedAtUtc", StartedAtUtc);
            Num(sb, "updatedAtUtc", UpdatedAtUtc);
            Str(sb, "occlusion", OcclusionFidelity);
            Str(sb, "inputDriver", InputDriver);
            sb.Append('}');
            return sb.ToString();
        }

        private static void Str(StringBuilder sb, string key, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":");
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Num(StringBuilder sb, string key, double value)
        {
            sb.Append(',').Append('"').Append(key).Append("\":");
            if (double.IsNaN(value) || double.IsInfinity(value))
                sb.Append("null");
            else
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
