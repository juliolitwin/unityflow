using UnityEngine;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier
{
    /// <summary>How the run went, written once when the screen comes up.</summary>
    public sealed class ResultsScreen : MonoBehaviour
    {
        [SerializeField] private CourierGame m_Game;
        [SerializeField] private ScoreKeeper m_Score;
        [SerializeField] private GameClock m_Clock;
        [SerializeField] private Text m_Headline;
        [SerializeField] private Text m_Summary;
        [SerializeField] private Button m_PlayAgain;
        [SerializeField] private Button m_Menu;

        private void Awake()
        {
            m_PlayAgain.onClick.AddListener(m_Game.PlayAgain);
            m_Menu.onClick.AddListener(m_Game.ToMenu);
        }

        private void OnEnable()
        {
            m_Headline.text = m_Score.delivered > 0
                ? "SHIFT OVER, " + m_Game.courierName.ToUpperInvariant()
                : "NOTHING DELIVERED";

            m_Summary.text = string.Format(
                "{0} delivered   ·   {1}s on the clock   ·   {2} points",
                m_Score.delivered,
                Mathf.RoundToInt(m_Clock.elapsed),
                m_Score.score);
        }
    }
}
