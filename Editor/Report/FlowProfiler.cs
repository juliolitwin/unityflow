using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;

namespace UnityFlow.Editor.Report
{
    /// <summary>
    /// What a run COST the editor, recorded next to what it proved.
    ///
    /// A flow report says whether the assertions held. It said nothing at all about the machine
    /// they held on, and that gap hid a real defect for as long as it existed: the editor froze
    /// solid for stretches of a run and every report still read "Passed". Wall clock alone does not
    /// expose it either — a step that waits three seconds for a server and a step that pins the main
    /// thread for three seconds are the same number in <c>step.pass</c>, and only one of them is a
    /// bug.
    ///
    /// So two things are measured, and nothing else:
    ///
    /// <list type="bullet">
    /// <item><b>Compilations.</b> Every invocation of Roslyn a run performs, with the source key,
    /// the time it took, and whether it was served from cache. A compile is the only thing in the
    /// harness that occupies the main thread for hundreds of milliseconds at a time, so counting
    /// them turns "it feels slow" into an arithmetic question.</item>
    /// <item><b>Stalls.</b> The gap between consecutive editor ticks. The editor pumps this
    /// callback continuously; a gap of hundreds of milliseconds means nothing rendered, no input was
    /// processed, and the window was frozen for exactly that long. This is the user's complaint,
    /// measured directly rather than inferred from a step's duration.</item>
    /// </list>
    ///
    /// <para><b>Cost.</b> The sampler is one delegate on <c>EditorApplication.update</c> doing a
    /// subtraction and a comparison; only gaps past <see cref="StallThresholdMs"/> are written, and
    /// a healthy run writes none. Compile records are bounded by the number of compiles, which is
    /// the thing being counted. Nothing here is conditional on a debug flag, because a measurement
    /// that has to be switched on is one nobody has when they need it.</para>
    ///
    /// <para><b>Across a domain reload.</b> A flow that enters play mode reloads the domain, and the
    /// statics here die with it. The active run's directory is kept in <see cref="SessionState"/>,
    /// which survives, and <see cref="Rehook"/> re-attaches the sampler in the new domain — so the
    /// records either side of a reload land in the same file. The file is append-only for the same
    /// reason.</para>
    /// </summary>
    [InitializeOnLoad]
    public static class FlowProfiler
    {
        /// <summary>
        /// A gap between editor ticks past this is a freeze rather than a slow frame.
        ///
        /// 100ms is three frames at 30fps: long enough that no ordinary frame reaches it, short
        /// enough to catch a stutter a user would notice. Gaps below it are not written at all —
        /// recording every tick of a 68-second run would produce tens of thousands of lines saying
        /// nothing.
        /// </summary>
        public const double StallThresholdMs = 100.0;

        /// <summary>Run directory of the profile in progress, so a new domain can find the file again.</summary>
        private const string DirectoryKey = "uf.profile.dir";

        /// <summary>Step the run is on, so a stall recorded from the editor loop can name its cause.</summary>
        private const string StepKey = "uf.profile.step";

        private static readonly System.Diagnostics.Stopwatch s_Clock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly StringBuilder s_Builder = new StringBuilder(256);

        private static string s_File;
        private static double s_LastTick;
        private static bool s_Hooked;

        static FlowProfiler()
        {
            Rehook();
        }

        /// <summary>Whether a profile is being recorded right now.</summary>
        public static bool Active => s_File != null;

        /// <summary>
        /// Start recording into a run's directory. Called once per run segment; a segment that
        /// resumes after a domain reload appends to the file the previous segment wrote.
        /// </summary>
        public static void Begin(string runDirectory)
        {
            if (string.IsNullOrEmpty(runDirectory))
                throw new ArgumentException("A profile needs the run directory to write into.", nameof(runDirectory));

            SessionState.SetString(DirectoryKey, runDirectory);
            Attach(runDirectory);

            Write("profile.begin", new[]
            {
                Pair("domain", AppDomain.CurrentDomain.Id),
                Pair("stallThresholdMs", StallThresholdMs)
            });
        }

        /// <summary>Stop recording. The file stays; only the sampler goes away.</summary>
        public static void End()
        {
            if (s_File != null)
            {
                Write("profile.end", new[] { Pair("domain", AppDomain.CurrentDomain.Id) });
            }

            SessionState.EraseString(DirectoryKey);
            SessionState.EraseString(StepKey);
            Detach();
        }

        /// <summary>
        /// Name the step now executing, so a stall sampled from the editor loop can be attributed
        /// without the sampler knowing anything about the runner.
        /// </summary>
        public static void SetStep(int index, string verb, string file)
        {
            if (s_File == null)
                return;

            SessionState.SetString(StepKey, index.ToString(CultureInfo.InvariantCulture) + " " + verb +
                                            (file == null ? string.Empty : " (" + Path.GetFileName(file) + ")"));
        }

        /// <summary>
        /// Record one attempt to turn source text into a delegate.
        /// </summary>
        /// <param name="kind">Which path asked: <c>expr</c> or <c>runScript</c>.</param>
        /// <param name="source">The source text, hashed rather than stored — a script is far too long for a log line.</param>
        /// <param name="milliseconds">Wall time the caller was blocked for.</param>
        /// <param name="cacheHit">
        /// Whether the compiler was skipped. Counting hits and misses separately is what makes a
        /// cache's effect measurable rather than asserted.
        /// </param>
        public static void Compilation(string kind, string source, double milliseconds, bool cacheHit)
        {
            if (s_File == null)
                return;

            Write("profile.compile", new[]
            {
                Pair("kind", kind),
                Pair("key", Fingerprint(source)),
                Pair("ms", milliseconds),
                Pair("cacheHit", cacheHit),
                Pair("step", SessionState.GetString(StepKey, string.Empty))
            });
        }

        /// <summary>
        /// Record what a retry loop cost: how many times a step re-read the game, and how long
        /// those reads took in total.
        ///
        /// Separate from the compile record on purpose. Compiling is a fixed cost paid once per
        /// source; re-reading is paid every frame the step waits, and only one of the two gets
        /// worse when a flow waits longer. Without both numbers, "the step took two seconds" cannot
        /// be told apart from "the step waited two seconds".
        /// </summary>
        public static void Retries(string verb, int evaluations, double milliseconds)
        {
            if (s_File == null || evaluations == 0)
                return;

            Write("profile.retries", new[]
            {
                Pair("verb", verb),
                Pair("n", evaluations),
                Pair("ms", milliseconds),
                Pair("step", SessionState.GetString(StepKey, string.Empty))
            });
        }

        /// <summary>
        /// Short, stable identity for a piece of source, so repeated compilations of the SAME text
        /// are visible as repeats in the record. FNV-1a: no allocation, no cryptographic pretence,
        /// and collisions in a set of a few dozen strings are not a practical concern.
        /// </summary>
        public static string Fingerprint(string source)
        {
            if (source == null)
                return "null";

            unchecked
            {
                var hash = 2166136261u;
                for (var i = 0; i < source.Length; i++)
                {
                    hash ^= source[i];
                    hash *= 16777619u;
                }

                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Re-attach in a domain that inherited a run in progress.</summary>
        private static void Rehook()
        {
            var directory = SessionState.GetString(DirectoryKey, string.Empty);
            if (directory.Length > 0)
                Attach(directory);
        }

        private static void Attach(string runDirectory)
        {
            s_File = Path.Combine(runDirectory, "profile.ndjson");
            s_LastTick = s_Clock.Elapsed.TotalMilliseconds;

            if (s_Hooked)
                return;

            EditorApplication.update += Sample;
            s_Hooked = true;
        }

        private static void Detach()
        {
            s_File = null;

            if (!s_Hooked)
                return;

            EditorApplication.update -= Sample;
            s_Hooked = false;
        }

        /// <summary>
        /// One tick. The measurement IS the gap since the previous one: whatever occupied the main
        /// thread in between — a compile, an import, a GC — kept this callback from running, and
        /// kept the editor from drawing a frame just as completely.
        /// </summary>
        private static void Sample()
        {
            var now = s_Clock.Elapsed.TotalMilliseconds;
            var gap = now - s_LastTick;
            s_LastTick = now;

            if (gap < StallThresholdMs || s_File == null)
                return;

            Write("profile.stall", new[]
            {
                Pair("ms", gap),
                Pair("step", SessionState.GetString(StepKey, string.Empty))
            });
        }

        /// <summary>
        /// Append one record. Opened and closed per line: a run crosses domain reloads and play mode
        /// transitions, and a held handle would either be lost across one or lock the file against
        /// the reader tailing it.
        /// </summary>
        private static void Write(string type, IReadOnlyList<KeyValuePair<string, object>> fields)
        {
            var file = s_File;
            if (file == null)
                return;

            s_Builder.Clear();
            s_Builder.Append("{\"t\":").Append(s_Clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
            s_Builder.Append(",\"type\":\"").Append(type).Append('"');

            for (var i = 0; i < fields.Count; i++)
            {
                s_Builder.Append(",\"").Append(fields[i].Key).Append("\":");

                switch (fields[i].Value)
                {
                    case null:
                        s_Builder.Append("null");
                        break;
                    case bool flag:
                        s_Builder.Append(flag ? "true" : "false");
                        break;
                    case int number:
                        s_Builder.Append(number.ToString(CultureInfo.InvariantCulture));
                        break;
                    case double number:
                        s_Builder.Append(number.ToString("F2", CultureInfo.InvariantCulture));
                        break;
                    default:
                        s_Builder.Append('"').Append(fields[i].Value.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                        break;
                }
            }

            s_Builder.Append("}\n");

            try
            {
                File.AppendAllText(file, s_Builder.ToString());
            }
            catch (IOException)
            {
                // A profile is evidence about a run, never a reason to fail one. If the file cannot
                // be written the run carries on and the record is simply missing.
            }
        }

        private static KeyValuePair<string, object> Pair(string key, object value) =>
            new KeyValuePair<string, object>(key, value);
    }
}
