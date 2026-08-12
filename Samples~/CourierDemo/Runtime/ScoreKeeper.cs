using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>Deliveries and points for the current run.</summary>
    public sealed class ScoreKeeper : MonoBehaviour
    {
        private int m_Delivered;
        private int m_Score;
        private int m_Multiplier = 1;

        /// <summary>Parcels handed over so far.</summary>
        public int delivered => m_Delivered;

        /// <summary>Points earned so far: parcel value times the difficulty multiplier.</summary>
        public int score => m_Score;

        /// <summary>Start a fresh tally. Named BeginRun rather than Reset: Reset is an Editor message.</summary>
        public void BeginRun(int multiplier)
        {
            m_Delivered = 0;
            m_Score = 0;
            m_Multiplier = Mathf.Max(1, multiplier);
        }

        /// <summary>Bank a drop-off. Parcels and value are separate because a drop-off can be more than one parcel.</summary>
        public void Deliver(int parcels, int value)
        {
            m_Delivered += parcels;
            m_Score += value * m_Multiplier;
        }
    }
}
