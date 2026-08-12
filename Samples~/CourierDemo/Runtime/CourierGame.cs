using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityFlow.Samples.Courier
{
    /// <summary>Which screen the game is on. A flow reads it with <c>field: phase</c>.</summary>
    public enum CourierPhase
    {
        Menu,
        Playing,
        Paused,
        Results
    }

    /// <summary>Difficulty as the menu dropdown lists it: the enum order IS the option order.</summary>
    public enum CourierDifficulty
    {
        Relaxed,
        Normal,
        Rush
    }

    /// <summary>
    /// The one place a phase changes, and therefore the one place that decides which screens exist.
    ///
    /// Every other component reads <see cref="phase"/> and acts on it rather than turning screens on
    /// and off for itself. That is what lets a flow assert about the game with one query — a screen
    /// being visible and the game believing it is playing can never disagree.
    /// </summary>
    public sealed class CourierGame : MonoBehaviour
    {
        [SerializeField] private GameObject m_MenuPanel;
        [SerializeField] private GameObject m_HudPanel;
        [SerializeField] private GameObject m_PausePanel;
        [SerializeField] private GameObject m_ResultsPanel;
        [SerializeField] private InventoryPanel m_InventoryPanel;

        [SerializeField] private GameClock m_Clock;
        [SerializeField] private ScoreKeeper m_Score;
        [SerializeField] private PlayerHealth m_Health;
        [SerializeField] private CourierInventory m_Cargo;
        [SerializeField] private CourierPlayer m_Player;
        [SerializeField] private ParcelField m_Parcels;

        private CourierPhase m_Phase = CourierPhase.Menu;
        private CourierDifficulty m_Difficulty = CourierDifficulty.Normal;
        private string m_CourierName = string.Empty;
        private bool m_Sound = true;
        private float m_Volume = 0.7f;

        /// <summary>Which screen is up. Readable from a flow: <c>assert: { component: CourierGame, field: phase, is: Playing }</c>.</summary>
        public CourierPhase phase => m_Phase;

        /// <summary>Difficulty the run was started on.</summary>
        public CourierDifficulty difficulty => m_Difficulty;

        /// <summary>Name typed into the menu form.</summary>
        public string courierName => m_CourierName;

        private void Start()
        {
            // The scene is authored on the menu, but saying so here means the screens can never be
            // left in a half-open state by an editor session that saved mid-run.
            SetPhase(CourierPhase.Menu);
        }

        private void Update()
        {
            ReadHotkeys();

            if (m_Phase == CourierPhase.Playing && IsRunOver())
                SetPhase(CourierPhase.Results);
        }

        /// <summary>Begin a run with the settings the menu form collected.</summary>
        public void StartRun(string courier, CourierDifficulty difficulty, bool sound, float volume)
        {
            m_CourierName = courier;
            m_Difficulty = difficulty;
            m_Sound = sound;
            m_Volume = volume;
            AudioListener.volume = sound ? volume : 0f;

            m_Health.BeginRun();
            m_Cargo.Clear();
            m_Score.BeginRun(ScoreMultiplier(difficulty));
            m_Parcels.BeginRun();
            m_Player.BeginRun();
            m_Clock.BeginRun(TimeLimit(difficulty));

            SetPhase(CourierPhase.Playing);
        }

        /// <summary>Replay with the same courier and settings. Bound to the results screen's button.</summary>
        public void PlayAgain() => StartRun(m_CourierName, m_Difficulty, m_Sound, m_Volume);

        /// <summary>Leave the pause or results screen for the menu. Bound to both screens' buttons.</summary>
        public void ToMenu() => SetPhase(CourierPhase.Menu);

        /// <summary>Bound to the pause screen's Resume button; Escape does the same thing.</summary>
        public void Resume()
        {
            if (m_Phase == CourierPhase.Paused)
                SetPhase(CourierPhase.Playing);
        }

        private void ReadHotkeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.escapeKey.wasPressedThisFrame)
                TogglePause();

            if (keyboard.tabKey.wasPressedThisFrame && (m_Phase == CourierPhase.Playing || m_Phase == CourierPhase.Paused))
                m_InventoryPanel.Toggle();
        }

        private void TogglePause()
        {
            if (m_Phase == CourierPhase.Playing)
                SetPhase(CourierPhase.Paused);
            else if (m_Phase == CourierPhase.Paused)
                SetPhase(CourierPhase.Playing);
        }

        /// <summary>Three ways a run ends: the clock, the courier, or an empty street.</summary>
        private bool IsRunOver() =>
            m_Clock.remaining <= 0f ||
            m_Health.current <= 0 ||
            (m_Parcels.available == 0 && m_Cargo.count == 0);

        private void SetPhase(CourierPhase phase)
        {
            m_Phase = phase;

            m_MenuPanel.SetActive(phase == CourierPhase.Menu);
            m_HudPanel.SetActive(phase == CourierPhase.Playing || phase == CourierPhase.Paused);
            m_PausePanel.SetActive(phase == CourierPhase.Paused);
            m_ResultsPanel.SetActive(phase == CourierPhase.Results);

            m_Clock.Hold(phase == CourierPhase.Playing);

            if (phase == CourierPhase.Menu || phase == CourierPhase.Results)
                m_InventoryPanel.Close();
        }

        private float TimeLimit(CourierDifficulty difficulty)
        {
            switch (difficulty)
            {
                case CourierDifficulty.Relaxed: return 90f;
                case CourierDifficulty.Rush: return 40f;
                default: return 60f;
            }
        }

        private int ScoreMultiplier(CourierDifficulty difficulty)
        {
            switch (difficulty)
            {
                case CourierDifficulty.Relaxed: return 1;
                case CourierDifficulty.Rush: return 3;
                default: return 2;
            }
        }
    }
}
