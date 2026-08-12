using UnityEngine;
using UnityEngine.UI;

namespace UnityFlow.Samples.Courier
{
    /// <summary>Resume or give up. Both buttons hand the decision straight to <see cref="CourierGame"/>.</summary>
    public sealed class PauseScreen : MonoBehaviour
    {
        [SerializeField] private CourierGame m_Game;
        [SerializeField] private Button m_Resume;
        [SerializeField] private Button m_Menu;

        // The panel is authored inactive, so Awake runs the first time it is shown - which is still
        // before the first OnEnable, so the buttons are never live without their listeners.
        private void Awake()
        {
            m_Resume.onClick.AddListener(m_Game.Resume);
            m_Menu.onClick.AddListener(m_Game.ToMenu);
        }
    }
}
