using UnityEngine;
using UnityEngine.EventSystems;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Synthesizes uGUI's own pointer event sequence, mirroring StandaloneInputModule field for
    /// field.
    ///
    /// THE CLICK GATE CHANGED IN uGUI 2.0 AND MOST PUBLISHED SNIPPETS ARE NOW WRONG. The release
    /// path in <c>StandaloneInputModule.ReleaseMouse</c> reads:
    /// <code>
    /// var pointerClickHandler = ExecuteEvents.GetEventHandler&lt;IPointerClickHandler&gt;(currentOverGo);
    /// if (pointerEvent.pointerClick == pointerClickHandler &amp;&amp; pointerEvent.eligibleForClick)
    ///     ExecuteEvents.Execute(pointerEvent.pointerClick, pointerEvent, ExecuteEvents.pointerClickHandler);
    /// </code>
    /// It is no longer <c>pointerPress == currentOverGo</c>. Code that fills in only
    /// <c>pointerPress</c> leaves <c>pointerClick</c> null, the gate fails, IPointerClickHandler
    /// never runs and <c>Button.onClick</c> never fires — while every visual still animates, so the
    /// test looks like it worked.
    ///
    /// This class also never forges a RaycastResult for the intended target. The caller hit-probes
    /// first and passes in what the probe ACTUALLY hit; a probe that landed on a modal produces a
    /// refusal, not a click. Forcing pointerPress onto the target regardless is exactly how a
    /// runner goes green against a UI the user could not have touched.
    /// </summary>
    internal sealed class UGuiSemanticDispatch
    {
        /// <summary>StandaloneInputModule's default double-click window.</summary>
        private const float DoubleClickTime = 0.3f;

        /// <summary>Left mouse button id, as StandaloneInputModule uses it (kMouseLeftId).</summary>
        private const int LeftPointerId = -1;

        private PointerEventData m_Pointer;
        private BaseEventData m_BaseEvent;
        private EventSystem m_BoundEventSystem;

        /// <summary>
        /// Run a gesture at a point that has already been probed. <paramref name="hitObject"/> is
        /// the GameObject the probe landed on and <paramref name="raycast"/> describes that hit.
        /// </summary>
        public bool TryDispatch(
            in UGuiNodeParts parts,
            GameObject hitObject,
            in RaycastResult raycast,
            PointerGesture gesture,
            Vector2 screenPoint,
            out string error)
        {
            if (hitObject == null)
            {
                error = "no GameObject was hit at the requested point, so there is nothing to send events to";
                return false;
            }

            if (raycast.module == null)
            {
                error = $"Canvas '{parts.RootCanvas.name}' has no GraphicRaycaster, so a real pointer could never " +
                        $"reach '{parts.GameObject.name}'; add one to the canvas rather than driving the control directly";
                return false;
            }

            BindEventSystem();

            switch (gesture)
            {
                case PointerGesture.Click:
                    SendEnter(hitObject);
                    SendDown(hitObject, raycast, screenPoint);
                    SendUp(hitObject);
                    error = null;
                    return true;

                case PointerGesture.DoubleClick:
                    SendEnter(hitObject);
                    SendDown(hitObject, raycast, screenPoint);
                    SendUp(hitObject);
                    SendDown(hitObject, raycast, screenPoint);
                    SendUp(hitObject);
                    error = null;
                    return true;

                case PointerGesture.Down:
                    SendEnter(hitObject);
                    SendDown(hitObject, raycast, screenPoint);
                    error = null;
                    return true;

                case PointerGesture.Up:
                    if (m_Pointer == null || m_Pointer.pointerPress == null)
                    {
                        error = "no pointer press is outstanding; send Down before Up";
                        return false;
                    }

                    SendUp(hitObject);
                    error = null;
                    return true;

                default:
                    error = $"gesture {gesture} is not implemented by the uGUI backend";
                    return false;
            }
        }

        private void BindEventSystem()
        {
            var current = EventSystem.current;
            if (m_Pointer != null && m_BoundEventSystem == current)
                return;

            // A PointerEventData built against a null EventSystem works: verified by dispatching a
            // full down/up/click sequence in edit mode and observing Button.onClick fire once.
            m_Pointer = new PointerEventData(current);
            m_BaseEvent = new BaseEventData(current);
            m_BoundEventSystem = current;
        }

        /// <summary>
        /// The enter chain, as BaseInputModule.HandlePointerExitAndEnter builds it: exit everything
        /// currently hovered that is not on the new chain, then enter every object from the hit
        /// object up to the root. Sending only the click leaves Selectable press and highlight
        /// transitions untouched, so the UI never visually reacts and screenshots lie.
        /// </summary>
        // Derived from BaseInputModule.HandlePointerExitAndEnter in com.unity.ugui.
        // uGUI Copyright (c) 2015-2020 Unity Technologies ApS, Unity Companion License:
        // https://unity.com/legal/licenses/unity-companion-license
        private void SendEnter(GameObject hitObject)
        {
            if (m_Pointer.pointerEnter == hitObject)
                return;

            for (var i = 0; i < m_Pointer.hovered.Count; i++)
            {
                m_Pointer.fullyExited = true;
                ExecuteEvents.Execute(m_Pointer.hovered[i], m_Pointer, ExecuteEvents.pointerMoveHandler);
                ExecuteEvents.Execute(m_Pointer.hovered[i], m_Pointer, ExecuteEvents.pointerExitHandler);
            }

            m_Pointer.hovered.Clear();
            m_Pointer.pointerEnter = hitObject;

            for (var t = hitObject.transform; t != null; t = t.parent)
            {
                ExecuteEvents.Execute(t.gameObject, m_Pointer, ExecuteEvents.pointerEnterHandler);
                ExecuteEvents.Execute(t.gameObject, m_Pointer, ExecuteEvents.pointerMoveHandler);
                m_Pointer.hovered.Add(t.gameObject);
            }
        }

        private void SendDown(GameObject hitObject, in RaycastResult raycast, Vector2 screenPoint)
        {
            m_Pointer.position = screenPoint;
            m_Pointer.button = PointerEventData.InputButton.Left;
            m_Pointer.pointerId = LeftPointerId;
            m_Pointer.pressPosition = screenPoint;
            m_Pointer.delta = Vector2.zero;
            m_Pointer.dragging = false;
            m_Pointer.useDragThreshold = true;
            m_Pointer.eligibleForClick = true;
            m_Pointer.pointerCurrentRaycast = raycast;
            m_Pointer.pointerPressRaycast = raycast;

            DeselectIfSelectionChanged(hitObject);

            var pressed = ExecuteEvents.ExecuteHierarchy(hitObject, m_Pointer, ExecuteEvents.pointerDownHandler);
            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObject);

            // uGUI: an object with no down handler is still pressed, using whatever would be clicked.
            if (pressed == null)
                pressed = clickHandler;

            var time = Time.unscaledTime;
            if (pressed == m_Pointer.lastPress && time - m_Pointer.clickTime < DoubleClickTime)
                m_Pointer.clickCount++;
            else
                m_Pointer.clickCount = 1;

            m_Pointer.clickTime = time;
            m_Pointer.pointerPress = pressed;
            m_Pointer.rawPointerPress = hitObject;
            m_Pointer.pointerClick = clickHandler;
            m_Pointer.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(hitObject);

            if (m_Pointer.pointerDrag != null)
                ExecuteEvents.Execute(m_Pointer.pointerDrag, m_Pointer, ExecuteEvents.initializePotentialDrag);
        }

        // Derived from StandaloneInputModule.ReleaseMouse in com.unity.ugui.
        // uGUI Copyright (c) 2015-2020 Unity Technologies ApS, Unity Companion License:
        // https://unity.com/legal/licenses/unity-companion-license
        private void SendUp(GameObject hitObject)
        {
            if (m_Pointer.pointerPress != null)
                ExecuteEvents.Execute(m_Pointer.pointerPress, m_Pointer, ExecuteEvents.pointerUpHandler);

            var clickHandler = hitObject != null
                ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObject)
                : null;

            if (m_Pointer.pointerClick == clickHandler && m_Pointer.eligibleForClick && m_Pointer.pointerClick != null)
                ExecuteEvents.Execute(m_Pointer.pointerClick, m_Pointer, ExecuteEvents.pointerClickHandler);

            if (m_Pointer.pointerDrag != null && m_Pointer.dragging && hitObject != null)
                ExecuteEvents.ExecuteHierarchy(hitObject, m_Pointer, ExecuteEvents.dropHandler);

            m_Pointer.eligibleForClick = false;
            m_Pointer.pointerPress = null;
            m_Pointer.rawPointerPress = null;
            m_Pointer.pointerClick = null;

            if (m_Pointer.pointerDrag != null && m_Pointer.dragging)
                ExecuteEvents.Execute(m_Pointer.pointerDrag, m_Pointer, ExecuteEvents.endDragHandler);

            m_Pointer.dragging = false;
            m_Pointer.pointerDrag = null;
        }

        /// <summary>
        /// uGUI drops the current selection when a press lands somewhere that does not want it.
        /// This is what commits an InputField when the user clicks away, so a flow that types then
        /// taps elsewhere behaves the way it does for a human. It needs a live EventSystem; without
        /// one there is no selection to change and nothing is skipped silently.
        /// </summary>
        private void DeselectIfSelectionChanged(GameObject hitObject)
        {
            if (m_BoundEventSystem == null)
                return;

            var selectHandler = ExecuteEvents.GetEventHandler<ISelectHandler>(hitObject);
            if (selectHandler != m_BoundEventSystem.currentSelectedGameObject)
                m_BoundEventSystem.SetSelectedGameObject(null, m_BaseEvent);
        }
    }
}
