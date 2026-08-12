using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>The courier's remaining hits. Reaching zero ends the run.</summary>
    public sealed class PlayerHealth : MonoBehaviour
    {
        [SerializeField] private int m_Max = 3;

        private int m_Current;

        /// <summary>Hits left.</summary>
        public int current => m_Current;

        /// <summary>Hits a fresh run starts with.</summary>
        public int max => m_Max;

        private void Awake() => m_Current = m_Max;

        /// <summary>Refill for a new run.</summary>
        public void BeginRun() => m_Current = m_Max;

        /// <summary>Take damage, floored at zero so the run-over test only ever has one boundary to check.</summary>
        public void Hurt(int amount) => m_Current = Mathf.Max(0, m_Current - amount);
    }
}
