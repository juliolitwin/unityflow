using System.Collections.Generic;
using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// What the courier is carrying, in slot order.
    ///
    /// Slot ORDER is state, not decoration: the inventory panel's drag-to-reorder rewrites it, and
    /// <see cref="firstLabel"/> is what a flow asserts on afterwards. <see cref="version"/> exists so
    /// the panel can refresh on change without this type owning an event and a subscription
    /// lifetime for two labels.
    /// </summary>
    public sealed class CourierInventory : MonoBehaviour
    {
        [SerializeField] private int m_Capacity = 2;

        private readonly List<string> m_Labels = new List<string>(2);
        private readonly List<int> m_Values = new List<int>(2);

        private int m_Version;

        /// <summary>How many parcels are being carried.</summary>
        public int count => m_Labels.Count;

        /// <summary>How many can be carried at once.</summary>
        public int capacity => m_Capacity;

        /// <summary>Label in the FIRST slot, empty when nothing is carried. This is what a drag-to-reorder changes.</summary>
        public string firstLabel => m_Labels.Count > 0 ? m_Labels[0] : string.Empty;

        /// <summary>Bumped on every change, so a view can tell "nothing happened" from "the same count, reordered".</summary>
        public int version => m_Version;

        /// <summary>Label in a slot, empty when the slot is empty or out of range.</summary>
        public string LabelAt(int slot) => slot >= 0 && slot < m_Labels.Count ? m_Labels[slot] : string.Empty;

        /// <summary>Pick a parcel up. False when full, which is what makes capacity mean something.</summary>
        public bool TryCarry(string label, int value)
        {
            if (m_Labels.Count >= m_Capacity)
                return false;

            m_Labels.Add(label);
            m_Values.Add(value);
            m_Version++;
            return true;
        }

        /// <summary>Hand everything over and report the total value.</summary>
        public int TakeAll()
        {
            var total = 0;
            for (var i = 0; i < m_Values.Count; i++)
                total += m_Values[i];

            Clear();
            return total;
        }

        /// <summary>Reorder two carried parcels. Out-of-range slots are ignored: an empty slot is a legal drop target with nothing to trade.</summary>
        public void Swap(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= m_Labels.Count || b >= m_Labels.Count)
                return;

            (m_Labels[a], m_Labels[b]) = (m_Labels[b], m_Labels[a]);
            (m_Values[a], m_Values[b]) = (m_Values[b], m_Values[a]);
            m_Version++;
        }

        /// <summary>Drop everything, without banking it.</summary>
        public void Clear()
        {
            m_Labels.Clear();
            m_Values.Clear();
            m_Version++;
        }
    }
}
