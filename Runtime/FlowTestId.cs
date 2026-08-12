using UnityEngine;

namespace UnityFlow
{
    /// <summary>
    /// Optional stable automation id for a GameObject.
    ///
    /// UnityFlow resolves selectors by testId, visible text, name and hierarchy path, in that
    /// order. Names and paths are usually enough; attach this component only where they are
    /// unstable (procedurally generated lists, localized labels, prefabs renamed by designers).
    ///
    /// It is deliberately Inspector-assignable and carries no logic, so using it never requires
    /// writing C#. It is a pure data marker: no Awake, no Update, no registry.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UnityFlow/Flow Test Id")]
    public sealed class FlowTestId : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Stable id used by flow selectors, e.g. shop.item.sword. Should be unique in the scene.")]
        private string m_TestId;

        /// <summary>The automation id, or null/empty when unset.</summary>
        public string TestId => m_TestId;
    }
}
