using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Reads uGUI's own per-pointer drag state out of the running input module.
    ///
    /// WHY THE PUBLIC EVENT DATA CANNOT BE USED, even though it is the obvious place to look.
    /// InputSystemUIInputModule keeps ONE <c>PointerEventData</c> per pointer and lends it to each
    /// mouse button in turn inside a single <c>Process()</c>: left, then right, then middle, each
    /// preceded by <c>ButtonState.CopyPressStateTo(eventData)</c>, which assigns
    /// <c>pointerDrag</c> and <c>dragging</c> unconditionally (PointerModel.cs:260-281). So what the
    /// shared copy describes when the frame ends depends on how far through that sequence the frame
    /// got — and there is an early-out in the middle of it: a frame where the pointer did not change
    /// returns straight after the LEFT button's copy (InputSystemUIInputModule.cs:487), while a frame
    /// where it moved runs on and finishes with the MIDDLE button's, which is idle in every run.
    ///
    /// The shared value is therefore not merely wrong, it ALTERNATES: true on the quiet frames of a
    /// drag and false on the moving ones. That is the worst possible signal, because it looks like it
    /// works. Measured on a live gesture by a flow that asserts the disagreement frame by frame
    /// rather than describing it. The authoritative per-button copy is
    /// <c>PointerModel.leftButton</c>, which no frame overwrites with another button's state, so that
    /// is what this reads.
    ///
    /// The module exposes neither, which is why this is reflection. Nothing is assumed: every member
    /// this walks through is looked up by name once and reported by name if it has moved, so an
    /// Input System upgrade that renames one produces a sentence naming the field rather than a
    /// silent "no drag" that would make a broken run look merely unconfirmed. The types are reached
    /// by name too, so this assembly needs no reference to the Input System at all.
    /// </summary>
    internal sealed class UGuiPointerState
    {
        private const string ModuleTypeName = "UnityEngine.InputSystem.UI.InputSystemUIInputModule";

        private System.Type m_BoundModuleType;
        private FieldInfo m_PointerStates;
        private FieldInfo m_LeftButton;
        private FieldInfo m_IsPressed;
        private FieldInfo m_Dragging;
        private FieldInfo m_DragObject;
        private string m_BindReason;

        /// <summary>
        /// The pressed pointer's left-button state. False with a reason when uGUI cannot be asked at
        /// all, which is a different answer from "nothing is pressed" and must never be flattened
        /// into it.
        /// </summary>
        public bool TryRead(out bool pressed, out bool dragging, out GameObject handler, out string reason)
        {
            pressed = false;
            dragging = false;
            handler = null;

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                reason = "no EventSystem is current, so nothing is dispatching uGUI pointer events (EventSystem is not " +
                         "[ExecuteAlways]; it only runs in play mode)";
                return false;
            }

            var module = eventSystem.currentInputModule;
            if (module == null)
            {
                reason = $"the EventSystem '{eventSystem.name}' has no active input module, so no pointer state exists to read";
                return false;
            }

            var moduleType = module.GetType();
            if (moduleType.FullName != ModuleTypeName)
            {
                reason = $"the active input module is a {moduleType.FullName}; only {ModuleTypeName} keeps the " +
                         "per-button drag state this reads";
                return false;
            }

            if (!TryBind(moduleType))
            {
                reason = m_BindReason;
                return false;
            }

            // InlinedArray<PointerModel> is IEnumerable<PointerModel> and its enumerator honours
            // 'length', so iterating it is the whole traversal; firstValue/additionalValues never
            // have to be split apart here.
            if (!(m_PointerStates.GetValue(module) is IEnumerable states))
            {
                reason = $"{ModuleTypeName}.m_PointerStates is no longer enumerable, so its pointers cannot be walked";
                return false;
            }

            foreach (var state in states)
            {
                var button = m_LeftButton.GetValue(state);
                if (!(bool)m_IsPressed.GetValue(button))
                    continue;

                pressed = true;
                dragging = (bool)m_Dragging.GetValue(button);
                handler = m_DragObject.GetValue(button) as GameObject;
                reason = null;
                return true;
            }

            reason = null;
            return true;
        }

        private bool TryBind(System.Type moduleType)
        {
            if (m_BoundModuleType == moduleType)
                return m_BindReason == null;

            m_BoundModuleType = moduleType;
            m_BindReason = null;
            m_PointerStates = m_LeftButton = m_IsPressed = m_Dragging = m_DragObject = null;

            m_PointerStates = moduleType.GetField("m_PointerStates", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m_PointerStates == null)
            {
                m_BindReason = $"{ModuleTypeName} no longer keeps its pointers in a private 'm_PointerStates' field";
                return false;
            }

            var pointerModel = m_PointerStates.FieldType.IsGenericType
                ? m_PointerStates.FieldType.GetGenericArguments()[0]
                : null;

            if (pointerModel == null)
            {
                m_BindReason = $"{ModuleTypeName}.m_PointerStates is a {m_PointerStates.FieldType.Name} rather than a " +
                               "generic collection of pointer models";
                return false;
            }

            m_LeftButton = pointerModel.GetField("leftButton", BindingFlags.Instance | BindingFlags.Public);
            if (m_LeftButton == null)
            {
                m_BindReason = $"{pointerModel.Name} no longer exposes a 'leftButton' field";
                return false;
            }

            m_IsPressed = Field(m_LeftButton.FieldType, "m_IsPressed");
            m_Dragging = Field(m_LeftButton.FieldType, "m_Dragging");
            m_DragObject = Field(m_LeftButton.FieldType, "m_DragObject");

            if (m_IsPressed == null || m_Dragging == null || m_DragObject == null)
            {
                m_BindReason = $"{m_LeftButton.FieldType.Name} no longer stores its press state in " +
                               "'m_IsPressed', 'm_Dragging' and 'm_DragObject'";
                return false;
            }

            return true;
        }

        private static FieldInfo Field(System.Type type, string name) =>
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
