using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The main-menu form: a name, a difficulty, a sound toggle, a volume slider.
    ///
    /// The rule worth testing is the one every real form has: <b>Play stays disabled until the
    /// courier has a name</b>. Nothing else guards it, so a flow that types a name and finds Play
    /// still dead has found a genuine regression rather than a timing artefact.
    ///
    /// <see cref="m_Summary"/> is deliberately a single line that restates the WHOLE form, so one
    /// <c>assertText</c> covers four controls.
    /// </summary>
    public sealed class MenuScreen : MonoBehaviour
    {
        [SerializeField] private CourierGame m_Game;
        [SerializeField] private InputField m_NameField;
        [SerializeField] private Dropdown m_Difficulty;
        [SerializeField] private Toggle m_Sound;
        [SerializeField] private Slider m_Volume;
        [SerializeField] private Button m_Play;
        [SerializeField] private Button m_Quit;
        [SerializeField] private Text m_Summary;

        private void Awake()
        {
            m_NameField.onValueChanged.AddListener(_ => Refresh());
            m_Difficulty.onValueChanged.AddListener(_ => Refresh());
            m_Sound.onValueChanged.AddListener(_ => Refresh());
            m_Volume.onValueChanged.AddListener(_ => Refresh());

            m_Play.onClick.AddListener(Play);
            m_Quit.onClick.AddListener(Quit);
        }

        // Refreshing on enable, not only on change, means returning from a run cannot leave Play
        // enabled for a field that was cleared in between.
        private void OnEnable() => Refresh();

        /// <summary>
        /// Tab moves down the form, wrapping back to the name field.
        ///
        /// Without it this form would be a keyboard TRAP: a focused uGUI InputField consumes every
        /// navigation event — <c>OnUpdateSelected</c> ends in an unconditional <c>eventData.Use()</c>
        /// — so once the caret is in the name field, no arrow key can ever leave it and the rest of
        /// the form is unreachable without a mouse. Tab is the way out, and wrapping is what makes
        /// it an ORDER rather than a one-way exit.
        /// </summary>
        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.tabKey.wasPressedThisFrame)
                return;

            var events = EventSystem.current;
            if (events == null)
                return;

            var selected = events.currentSelectedGameObject;
            var current = selected == null ? null : selected.GetComponent<Selectable>();
            var next = current == null ? null : current.FindSelectableOnDown();

            events.SetSelectedGameObject((next == null ? m_NameField : next).gameObject);
        }

        private void Refresh()
        {
            var courier = m_NameField.text.Trim();

            m_Play.interactable = courier.Length > 0;

            m_Summary.text = string.Format(
                "{0}   ·   {1}   ·   sound {2}   ·   volume {3}%",
                courier.Length == 0 ? "no courier" : courier,
                (CourierDifficulty)m_Difficulty.value,
                m_Sound.isOn ? "on" : "off",
                Mathf.RoundToInt(m_Volume.value * 100f));
        }

        private void Play() =>
            m_Game.StartRun(m_NameField.text.Trim(), (CourierDifficulty)m_Difficulty.value, m_Sound.isOn, m_Volume.value);

        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
