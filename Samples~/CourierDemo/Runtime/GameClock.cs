using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The round countdown.
    ///
    /// <see cref="remaining"/> is the sample's showcase <c>waitUntil</c> target: it changes every
    /// frame, so a flow that watches it is watching the game actually run rather than a value that
    /// was already true when the step started.
    /// </summary>
    public sealed class GameClock : MonoBehaviour
    {
        private float m_Limit;
        private float m_Remaining;
        private bool m_Running;

        /// <summary>Seconds left in the round.</summary>
        public float remaining => m_Remaining;

        /// <summary>Whether the countdown is ticking. False while paused, which is the assertable half of "the game is paused".</summary>
        public bool running => m_Running;

        /// <summary>Seconds spent so far, reported on the results screen.</summary>
        public float elapsed => m_Limit - m_Remaining;

        private void Update()
        {
            if (!m_Running)
                return;

            m_Remaining = Mathf.Max(0f, m_Remaining - Time.deltaTime);

            if (m_Remaining <= 0f)
                m_Running = false;
        }

        /// <summary>Start a fresh countdown. Named BeginRun rather than Reset: Reset is an Editor message.</summary>
        public void BeginRun(float seconds)
        {
            m_Limit = Mathf.Max(0f, seconds);
            m_Remaining = m_Limit;
            m_Running = m_Limit > 0f;
        }

        /// <summary>Run or freeze the countdown without touching the value. A frozen clock can never restart itself at zero.</summary>
        public void Hold(bool running) => m_Running = running && m_Remaining > 0f;

        /// <summary>
        /// Shorten the round from a flow, so a test that needs the results screen does not have to
        /// wait out a real one.
        ///
        /// This is the sample's <c>[FlowCommand]</c>: an INSTANCE method, so the flow never has to
        /// name an object — <c>this</c> is the clock the step acts on, and the runner resolves the
        /// single GameClock in the scene. Written in YAML as <c>- setTimer: 12</c>.
        /// </summary>
        [FlowCommand("setTimer", Description = "Set the round countdown to N seconds, so a flow can reach the results screen without waiting out a real round.")]
        public void SetTimer(float seconds)
        {
            m_Limit = Mathf.Max(m_Limit, Mathf.Max(0f, seconds));
            m_Remaining = Mathf.Max(0f, seconds);
        }
    }
}
