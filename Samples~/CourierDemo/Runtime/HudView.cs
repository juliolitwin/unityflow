using UnityEngine;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// The four numbers on screen during a run.
    ///
    /// Each label is written only when its own value changed. That is not micro-optimisation: a Text
    /// assignment marks the canvas dirty, and four unconditional assignments a frame would rebuild
    /// the HUD canvas every frame for a game that changes one digit a second.
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        [SerializeField] private GameClock m_Clock;
        [SerializeField] private ScoreKeeper m_Score;
        [SerializeField] private PlayerHealth m_Health;
        [SerializeField] private CourierInventory m_Cargo;

        [SerializeField] private Text m_TimeLabel;
        [SerializeField] private Text m_ScoreLabel;
        [SerializeField] private Text m_HealthLabel;
        [SerializeField] private Text m_CarryLabel;

        private int m_ShownSeconds = -1;
        private int m_ShownScore = -1;
        private int m_ShownHealth = -1;
        private int m_ShownCarried = -1;

        private void OnEnable()
        {
            m_ShownSeconds = -1;
            m_ShownScore = -1;
            m_ShownHealth = -1;
            m_ShownCarried = -1;
        }

        private void Update()
        {
            var seconds = Mathf.CeilToInt(m_Clock.remaining);
            if (seconds != m_ShownSeconds)
            {
                m_ShownSeconds = seconds;
                m_TimeLabel.text = string.Format("TIME  {0}:{1:00}", seconds / 60, seconds % 60);
            }

            if (m_Score.score != m_ShownScore)
            {
                m_ShownScore = m_Score.score;
                m_ScoreLabel.text = "SCORE  " + m_ShownScore;
            }

            if (m_Health.current != m_ShownHealth)
            {
                m_ShownHealth = m_Health.current;
                m_HealthLabel.text = "HEALTH  " + m_ShownHealth + "/" + m_Health.max;
            }

            if (m_Cargo.count != m_ShownCarried)
            {
                m_ShownCarried = m_Cargo.count;
                m_CarryLabel.text = "CARRYING  " + m_ShownCarried + "/" + m_Cargo.capacity + "   (TAB)";
            }
        }
    }
}
