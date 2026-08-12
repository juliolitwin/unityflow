using System.Collections;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Yield instructions understood by <see cref="FlowDriver"/>.
    ///
    /// UnityEngine's own YieldInstruction types (WaitForSeconds, WaitForEndOfFrame) are
    /// meaningless here: they are interpreted by the MonoBehaviour coroutine scheduler, which is
    /// not what drives a flow. A flow is pumped by the editor loop in edit mode and by a
    /// PlayerLoop hook in play mode, so it needs its own small, explicit vocabulary.
    /// </summary>
    public abstract class FlowYield
    {
        /// <summary>True once the driver may advance past this instruction.</summary>
        public abstract bool IsDone { get; }

        /// <summary>Called on the first frame the instruction is observed.</summary>
        public virtual void Begin() { }
    }

    /// <summary>Wait a fixed number of frames. Used where a UI system needs a frame to react.</summary>
    public sealed class WaitFrames : FlowYield
    {
        private int m_Remaining;

        public WaitFrames(int frames)
        {
            // A caller asking for 0 frames means "do not wait"; negative is a caller bug, not a
            // value to silently clamp, but clamping to 0 here keeps a step from hanging forever.
            m_Remaining = frames < 0 ? 0 : frames;
        }

        public override bool IsDone
        {
            get
            {
                if (m_Remaining <= 0)
                    return true;

                m_Remaining--;
                return false;
            }
        }
    }

    /// <summary>
    /// Wait a wall-clock duration. Deliberately real time, never scaled time: a flow must not
    /// change meaning because the game set Time.timeScale to 0 during a pause menu.
    /// </summary>
    public sealed class WaitRealSeconds : FlowYield
    {
        private readonly double m_Seconds;
        private double m_Deadline;

        public WaitRealSeconds(double seconds) => m_Seconds = seconds;

        public override void Begin() => m_Deadline = FlowClock.Now + m_Seconds;

        public override bool IsDone => FlowClock.Now >= m_Deadline;
    }

    /// <summary>
    /// The one clock the runner uses.
    ///
    /// UnityEngine.Time.realtimeSinceStartup is a float that loses resolution as a session ages,
    /// and it does not advance at all in edit mode between some editor ticks. A stopwatch-based
    /// monotonic clock behaves identically in both modes and is what timeouts must be measured
    /// against.
    /// </summary>
    public static class FlowClock
    {
        private static readonly System.Diagnostics.Stopwatch s_Stopwatch = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Monotonic seconds since this domain loaded.</summary>
        public static double Now => s_Stopwatch.Elapsed.TotalSeconds;
    }

    /// <summary>Helpers for writing step coroutines readably.</summary>
    public static class Flow
    {
        /// <summary>Yield this to advance exactly one frame.</summary>
        public static readonly object NextFrame = null;

        /// <summary>Wait a number of frames.</summary>
        public static FlowYield Frames(int count) => new WaitFrames(count);

        /// <summary>Wait a wall-clock duration.</summary>
        public static FlowYield Seconds(double seconds) => new WaitRealSeconds(seconds);

        /// <summary>An enumerator that completes immediately, for steps with nothing to wait on.</summary>
        public static IEnumerator Done()
        {
            yield break;
        }
    }
}
