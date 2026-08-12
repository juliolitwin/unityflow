using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The Tab panel: one chip per carried parcel, in slot order.
    ///
    /// It polls <see cref="CourierInventory.version"/> instead of subscribing to an event, because a
    /// panel that is switched off and on again would otherwise have to manage a subscription
    /// lifetime to keep two labels correct.
    /// </summary>
    public sealed class InventoryPanel : MonoBehaviour
    {
        [SerializeField] private CourierInventory m_Cargo;
        [SerializeField] private InventorySlot[] m_Slots;

        private int m_ShownVersion = -1;

        /// <summary>Open or close. Safe to call while closed: this is a plain C# call, not a message.</summary>
        public void Toggle() => gameObject.SetActive(!gameObject.activeSelf);

        /// <summary>Close, so leaving a run cannot strand the panel over the menu.</summary>
        public void Close() => gameObject.SetActive(false);

        private void OnEnable() => m_ShownVersion = -1;

        private void Update()
        {
            if (m_ShownVersion == m_Cargo.version)
                return;

            m_ShownVersion = m_Cargo.version;

            for (var i = 0; i < m_Slots.Length; i++)
                m_Slots[i].Show(m_Cargo.LabelAt(i));
        }
    }
}
