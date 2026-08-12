using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>Something that hurts on contact.</summary>
    public sealed class Hazard : MonoBehaviour
    {
        [SerializeField] private int m_Damage = 1;

        /// <summary>Hits it costs.</summary>
        public int damage => m_Damage;
    }
}
