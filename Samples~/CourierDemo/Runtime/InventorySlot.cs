using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// One inventory slot, and the drag that reorders two of them.
    ///
    /// The chip is reparented to a drag layer for the duration of the gesture so it draws above both
    /// slots, and its raycast is turned OFF while it travels — otherwise the chip under the pointer
    /// is the only thing the raycast can ever hit and no slot would receive <c>OnDrop</c>.
    ///
    /// The model, not the hierarchy, is the reorder: <c>OnDrop</c> swaps two entries in
    /// <see cref="CourierInventory"/> and the panel redraws from it. That is why the assertion a
    /// flow writes afterwards survives — it is reading state, not the position a chip happens to
    /// have been left in.
    /// </summary>
    public sealed class InventorySlot : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private CourierInventory m_Cargo;
        [SerializeField] private int m_Index;
        [SerializeField] private RectTransform m_Chip;
        [SerializeField] private Image m_ChipImage;
        [SerializeField] private Text m_ChipLabel;
        [SerializeField] private RectTransform m_DragLayer;

        private bool m_Dragging;

        /// <summary>Draw what the model says this slot holds. An empty slot shows its frame and nothing else.</summary>
        public void Show(string label)
        {
            var filled = label.Length > 0;

            m_ChipLabel.text = label;
            m_ChipImage.enabled = filled;
            m_ChipLabel.enabled = filled;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (m_Cargo.LabelAt(m_Index).Length == 0)
                return;

            m_Dragging = true;
            m_ChipImage.raycastTarget = false;
            m_Chip.SetParent(m_DragLayer, true);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!m_Dragging)
                return;

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    m_DragLayer, eventData.position, eventData.pressEventCamera, out var local))
            {
                m_Chip.localPosition = local;
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!m_Dragging)
                return;

            m_Dragging = false;
            m_Chip.SetParent(transform, false);
            m_Chip.anchoredPosition = Vector2.zero;
            m_ChipImage.raycastTarget = true;
        }

        // uGUI raises OnDrop on the slot under the pointer BEFORE OnEndDrag on the slot that started
        // the gesture, so the swap is banked before the chip is sent home and redrawn.
        public void OnDrop(PointerEventData eventData)
        {
            var source = eventData.pointerDrag == null ? null : eventData.pointerDrag.GetComponent<InventorySlot>();

            if (source != null && source != this)
                m_Cargo.Swap(source.m_Index, m_Index);
        }
    }
}
