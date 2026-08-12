using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>A parcel lying in the street, carrying only the two things a pickup needs to know.</summary>
    public sealed class Parcel : MonoBehaviour
    {
        [SerializeField] private string m_Label = "A";
        [SerializeField] private int m_Value = 100;

        /// <summary>Single letter shown on the inventory chip.</summary>
        public string label => m_Label;

        /// <summary>Points it is worth, before the difficulty multiplier.</summary>
        public int value => m_Value;
    }
}
