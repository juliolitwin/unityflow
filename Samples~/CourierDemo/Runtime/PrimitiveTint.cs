using UnityEngine;

namespace UnityFlow.Samples.Courier
{
    /// <summary>
    /// Gives a primitive its colour without the sample shipping a material asset.
    ///
    /// A Material asset can only reference one render pipeline's shader, and this sample has to look
    /// the same wherever it is imported. <c>Material.color</c> is the main-colour alias in every
    /// pipeline — <c>_Color</c> on the Built-in Standard shader, <c>_BaseColor</c> on URP/Lit — so
    /// tinting the renderer's own default material is the one way to keep a deliberate palette and
    /// stay pipeline-agnostic. The colour is a serialized field, so the palette lives in the scene.
    ///
    /// It applies in play mode only: doing it in edit mode would instantiate a material per object
    /// and dirty the scene every time it is opened.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class PrimitiveTint : MonoBehaviour
    {
        [SerializeField] private Color m_Color = Color.white;

        private void Awake() => GetComponent<MeshRenderer>().material.color = m_Color;
    }
}
