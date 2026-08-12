using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Types into uGUI text controls through the only door uGUI actually leaves open.
    ///
    /// <c>InputField</c> and <c>TMP_InputField</c> both read keystrokes by draining the IMGUI event
    /// queue with <c>Event.PopEvent</c> inside <c>OnUpdateSelected</c>. Nothing bridges the Input
    /// System into that queue, so <c>InputSystem.QueueTextEvent</c> can never reach them no matter
    /// how correct the device injection is. The public hatch is
    /// <c>InputField.ProcessEvent(Event)</c>, which hands an <c>Event</c> straight to the same
    /// <c>KeyPressed</c> the queue would have fed.
    ///
    /// Two consequences follow, and both are handled here rather than hidden:
    /// <list type="bullet">
    /// <item><c>ProcessEvent</c> throws away the <c>EditState</c> that <c>KeyPressed</c> returns, so
    /// the end-of-edit transition never happens by itself.</item>
    /// <item><c>ActivateInputField</c> only queues activation; <c>m_AllowInput</c> (exposed as
    /// <c>isFocused</c>) is set by the control's own LateUpdate, which has not run yet. Measured:
    /// ActivateInputField followed immediately by DeactivateInputField fired no onEndEdit at all,
    /// because DeactivateInputField returns early while <c>m_AllowInput</c> is false.</item>
    /// </list>
    /// </summary>
    internal sealed class UGuiTextAdapter
    {
        private readonly UGuiTmpBridge m_Tmp;
        private readonly UGuiVisibility m_Visibility;
        private readonly Event m_KeyEvent = new Event();

        public UGuiTextAdapter(UGuiTmpBridge tmp, UGuiVisibility visibility)
        {
            m_Tmp = tmp;
            m_Visibility = visibility;
        }

        public bool TrySetText(in UGuiNodeParts parts, string text, out string error)
        {
            if (text == null)
            {
                error = "text is null; pass an empty string to clear a field";
                return false;
            }

            if (!parts.GameObject.activeInHierarchy)
            {
                error = $"'{parts.GameObject.name}' is inactive; an inactive control cannot be typed into";
                return false;
            }

            if (!m_Visibility.IsEnabled(parts, out var enabledReason))
            {
                error = $"'{parts.GameObject.name}' cannot be typed into: {enabledReason}";
                return false;
            }

            if (parts.Selectable is InputField inputField)
                return TrySetLegacy(inputField, text, out error);

            if (parts.Selectable != null && m_Tmp.IsInputField(parts.Selectable))
                return TrySetTmp(parts.Selectable, text, out error);

            error = $"'{parts.GameObject.name}' is a {parts.Control.GetType().Name}; the uGUI backend can only type " +
                    "into UnityEngine.UI.InputField or TMPro.TMP_InputField (or a subclass of either)";

            if (!m_Tmp.IsAvailable)
                error += $" — and {m_Tmp.UnavailableReason}";

            return false;
        }

        private bool TrySetLegacy(InputField field, string text, out string error)
        {
            if (field.textComponent == null)
            {
                error = $"InputField '{field.name}' has no Text component assigned to textComponent, so it cannot render or edit text";
                return false;
            }

            if (field.textComponent.font == null)
            {
                error = $"InputField '{field.name}' has a textComponent with no font; uGUI refuses to activate such a field";
                return false;
            }

            field.ActivateInputField();

            field.ProcessEvent(SelectAllEvent());
            field.ProcessEvent(DeleteEvent());
            for (var i = 0; i < text.Length; i++)
                field.ProcessEvent(CharacterEvent(text[i]));

            field.ForceLabelUpdate();

            // onValueChanged already fired once per keystroke from inside KeyPressed (measured:
            // three characters produced three invocations), so only the end-of-edit event is owed.
            if (field.isFocused)
                field.DeactivateInputField();
            else
                field.onEndEdit.Invoke(field.text);

            if (!string.Equals(field.text, text, StringComparison.Ordinal))
            {
                error = $"InputField '{field.name}' rejected part of the input: asked for \"{text}\", field now holds " +
                        $"\"{field.text}\" (characterLimit {field.characterLimit}, contentType {field.contentType})";
                return false;
            }

            error = null;
            return true;
        }

        private bool TrySetTmp(Component field, string text, out string error)
        {
            m_Tmp.Activate(field);

            m_Tmp.ProcessEvent(field, SelectAllEvent());
            m_Tmp.ProcessEvent(field, DeleteEvent());
            for (var i = 0; i < text.Length; i++)
                m_Tmp.ProcessEvent(field, CharacterEvent(text[i]));

            m_Tmp.ForceLabelUpdate(field);

            if (m_Tmp.IsFocused(field))
                m_Tmp.Deactivate(field);
            else
                m_Tmp.InvokeEndEdit(field, m_Tmp.GetInputFieldText(field));

            var actual = m_Tmp.GetInputFieldText(field);
            if (!string.Equals(actual, text, StringComparison.Ordinal))
            {
                error = $"TMP_InputField '{field.name}' rejected part of the input: asked for \"{text}\", field now holds \"{actual}\"";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Existing content is cleared through the control's own editing commands rather than by
        /// assigning the text property: ctrl+A selects, Delete removes the selection, and both run
        /// the field's validation and fire onValueChanged exactly as a user's keystrokes would.
        /// InputField.KeyPressed treats ctrl+A as select-all only when modifiers are exactly
        /// Control, so the reused Event is reset in full before every send.
        /// </summary>
        private Event SelectAllEvent()
        {
            m_KeyEvent.type = EventType.KeyDown;
            m_KeyEvent.character = '\0';
            m_KeyEvent.keyCode = KeyCode.A;
            m_KeyEvent.modifiers = EventModifiers.Control;
            return m_KeyEvent;
        }

        private Event DeleteEvent()
        {
            m_KeyEvent.type = EventType.KeyDown;
            m_KeyEvent.character = '\0';
            m_KeyEvent.keyCode = KeyCode.Delete;
            m_KeyEvent.modifiers = EventModifiers.None;
            return m_KeyEvent;
        }

        private Event CharacterEvent(char character)
        {
            m_KeyEvent.type = EventType.KeyDown;
            m_KeyEvent.keyCode = KeyCode.None;
            m_KeyEvent.modifiers = EventModifiers.None;
            m_KeyEvent.character = character;
            return m_KeyEvent;
        }
    }

    /// <summary>
    /// Typed access to TextMeshPro without a compile-time reference to it.
    ///
    /// TextMeshPro is merged into com.unity.ugui 2.0 for Unity 6 — verified: <c>TMPro.TMP_Text</c>
    /// resolves out of the <c>Unity.TextMeshPro</c> assembly and derives from
    /// <c>UnityEngine.UI.MaskableGraphic</c>, so every TMP label is already a uGUI Graphic and is
    /// found by the normal enumeration. What is deliberately avoided is an asmdef reference: this
    /// assembly is gated on com.unity.ugui 1.0.0 and up, and on 1.x TextMeshPro is a separate
    /// package that may be absent. A hard reference would make the whole backend fail to resolve
    /// there, which is the exact trap the defineConstraint pattern exists to avoid.
    ///
    /// The bindings are compiled once into delegates, so calling them costs a delegate invoke
    /// rather than a reflection dispatch and allocates nothing on the retry path.
    /// </summary>
    internal sealed class UGuiTmpBridge
    {
        private const string TextTypeName = "TMPro.TMP_Text, Unity.TextMeshPro";
        private const string InputFieldTypeName = "TMPro.TMP_InputField, Unity.TextMeshPro";

        private readonly Type m_TextType;
        private readonly Type m_InputFieldType;

        private readonly Func<Component, string> m_GetParsedText;
        private readonly Func<Component, string> m_GetInputFieldText;
        private readonly Action<Component, Event> m_ProcessEvent;
        private readonly Action<Component> m_ForceLabelUpdate;
        private readonly Action<Component> m_Activate;
        private readonly Action<Component, bool> m_Deactivate;
        private readonly Func<Component, bool> m_IsFocused;
        private readonly Action<Component, string> m_InvokeEndEdit;

        public UGuiTmpBridge()
        {
            m_TextType = Type.GetType(TextTypeName, false);
            m_InputFieldType = Type.GetType(InputFieldTypeName, false);

            if (m_TextType == null || m_InputFieldType == null)
            {
                UnavailableReason = "TextMeshPro is not loaded in this domain (TMPro.TMP_Text could not be resolved " +
                                    "from the Unity.TextMeshPro assembly), so TMP controls cannot be read or driven";
                return;
            }

            m_GetParsedText = CompileFunc<string>(m_TextType, m_TextType.GetMethod("GetParsedText", Type.EmptyTypes));
            m_GetInputFieldText = CompileGetter<string>(m_InputFieldType, "text");
            m_IsFocused = CompileGetter<bool>(m_InputFieldType, "isFocused");
            m_ProcessEvent = CompileAction<Event>(m_InputFieldType, m_InputFieldType.GetMethod("ProcessEvent", new[] { typeof(Event) }));
            m_ForceLabelUpdate = CompileAction(m_InputFieldType, m_InputFieldType.GetMethod("ForceLabelUpdate", Type.EmptyTypes));
            m_Activate = CompileAction(m_InputFieldType, m_InputFieldType.GetMethod("ActivateInputField", Type.EmptyTypes));
            m_Deactivate = CompileAction<bool>(m_InputFieldType, m_InputFieldType.GetMethod("DeactivateInputField", new[] { typeof(bool) }));
            m_InvokeEndEdit = CompileEventInvoke(m_InputFieldType, "onEndEdit");
        }

        public bool IsAvailable => m_TextType != null;

        /// <summary>Why TMP support is absent, when it is. Never null while <see cref="IsAvailable"/> is false.</summary>
        public string UnavailableReason { get; }

        public bool IsInputField(Component component) =>
            m_InputFieldType != null && m_InputFieldType.IsInstanceOfType(component);

        /// <summary>
        /// The text TMP has actually laid out, tags resolved. This reflects what is on screen: a
        /// label whose mesh has not been generated yet honestly reports nothing rather than
        /// reporting markup the user cannot see.
        /// </summary>
        public bool TryReadText(Component graphic, out string text)
        {
            if (m_TextType == null || !m_TextType.IsInstanceOfType(graphic))
            {
                text = null;
                return false;
            }

            text = m_GetParsedText(graphic);
            return true;
        }

        public bool TryReadInputFieldText(Component component, out string text)
        {
            if (!IsInputField(component))
            {
                text = null;
                return false;
            }

            text = m_GetInputFieldText(component);
            return true;
        }

        public string GetInputFieldText(Component field) => m_GetInputFieldText(field);
        public bool IsFocused(Component field) => m_IsFocused(field);
        public void ProcessEvent(Component field, Event e) => m_ProcessEvent(field, e);
        public void ForceLabelUpdate(Component field) => m_ForceLabelUpdate(field);
        public void Activate(Component field) => m_Activate(field);
        public void Deactivate(Component field) => m_Deactivate(field, false);
        public void InvokeEndEdit(Component field, string text) => m_InvokeEndEdit(field, text);

        private static Func<Component, TResult> CompileFunc<TResult>(Type declaring, MethodInfo method)
        {
            var instance = Expression.Parameter(typeof(Component), "c");
            var call = Expression.Call(Expression.Convert(instance, declaring), method);
            return Expression.Lambda<Func<Component, TResult>>(call, instance).Compile();
        }

        private static Func<Component, TResult> CompileGetter<TResult>(Type declaring, string propertyName)
        {
            var instance = Expression.Parameter(typeof(Component), "c");
            var property = Expression.Property(Expression.Convert(instance, declaring), propertyName);
            return Expression.Lambda<Func<Component, TResult>>(property, instance).Compile();
        }

        private static Action<Component> CompileAction(Type declaring, MethodInfo method)
        {
            var instance = Expression.Parameter(typeof(Component), "c");
            var call = Expression.Call(Expression.Convert(instance, declaring), method);
            return Expression.Lambda<Action<Component>>(call, instance).Compile();
        }

        private static Action<Component, TArg> CompileAction<TArg>(Type declaring, MethodInfo method)
        {
            var instance = Expression.Parameter(typeof(Component), "c");
            var arg = Expression.Parameter(typeof(TArg), "a");
            var call = Expression.Call(Expression.Convert(instance, declaring), method, arg);
            return Expression.Lambda<Action<Component, TArg>>(call, instance, arg).Compile();
        }

        private static Action<Component, string> CompileEventInvoke(Type declaring, string propertyName)
        {
            var instance = Expression.Parameter(typeof(Component), "c");
            var arg = Expression.Parameter(typeof(string), "s");
            var unityEvent = Expression.Property(Expression.Convert(instance, declaring), propertyName);
            var invoke = unityEvent.Type.GetMethod("Invoke", new[] { typeof(string) });
            var call = Expression.Call(unityEvent, invoke, arg);
            return Expression.Lambda<Action<Component, string>>(call, instance, arg).Compile();
        }
    }
}
