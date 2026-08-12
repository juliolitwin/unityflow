using System.Collections.Generic;
using UnityEngine;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// A UI system adapter: "what exists, and where is it on screen".
    ///
    /// Backends describe and probe. They do not own the pointer — device injection is global and
    /// lives in <see cref="IInputDriver"/>, because ONE injected mouse feeds every UI system at
    /// once (uGUI through InputSystemUIInputModule, UI Toolkit through the InputForUI event
    /// provider). Giving each backend its own Tap/Drag/Type would multiply
    /// (N ui systems x M actions) and still leave gameplay input out.
    ///
    /// What a backend DOES own is everything that is genuinely system-specific and that no generic
    /// core could get right: how to enumerate, how to project geometry, what "visible" means, and
    /// where a pointer must land. Those differ completely between uGUI and UI Toolkit — there is
    /// no shared implementation to hoist, only a shared question to ask.
    ///
    /// Implementations are discovered by TypeCache and instantiated fresh per run, so no stale
    /// state can survive a run or a domain reload.
    /// </summary>
    public interface IUiBackend
    {
        /// <summary>Stable short id, e.g. "ugui", "uitk". Appears in reports and selectors.</summary>
        string Id { get; }

        /// <summary>Resolution order when several backends match. Higher wins ties.</summary>
        int Priority { get; }

        /// <summary>
        /// Whether this UI system is actually live right now. When false, <paramref name="reason"/>
        /// explains why in a sentence a user can act on — never a bare false.
        /// </summary>
        bool IsAvailable(out string reason);

        /// <summary>What this backend can do to a node without an input driver.</summary>
        WriteCapability Capabilities { get; }

        /// <summary>Index assigned by the registry; written into every <see cref="UiHandle"/> this backend mints.</summary>
        int BackendIndex { get; set; }

        // ---- Read ------------------------------------------------------------------------

        /// <summary>All surfaces this backend currently owns.</summary>
        IReadOnlyList<UiSurface> GetSurfaces();

        /// <summary>Whether a surface's geometry can be trusted this frame.</summary>
        SurfaceReadiness GetReadiness(int surfaceId);

        /// <summary>
        /// Force pending layout so geometry read immediately afterwards is correct
        /// (Canvas.ForceUpdateCanvases, panel layout validation). Called once per retry frame,
        /// before enumeration.
        /// </summary>
        void Settle();

        /// <summary>
        /// Fill <paramref name="results"/> with nodes matching <paramref name="options"/>.
        /// The list is caller-owned and reused across frames; implementations must not retain it.
        /// </summary>
        void Enumerate(in EnumerateOptions options, List<UiNode> results);

        /// <summary>Re-read a single node. Returns false when the handle is stale.</summary>
        bool TryGetNode(UiHandle handle, out UiNode node);

        /// <summary>Whether the handle still refers to the object it was minted for.</summary>
        bool IsHandleAlive(UiHandle handle);

        /// <summary>
        /// Whether the node is actually drawn, with the reason when it is not
        /// ("CanvasGroup alpha 0.00", "display:None on ancestor Root/Panel").
        /// </summary>
        bool IsVisible(UiHandle handle, out string reason);

        /// <summary>
        /// Whether the node can be acted on: visible, enabled, hit-testable and laid out.
        /// Deliberately separate from <see cref="IsVisible"/> — a CanvasGroup at alpha 0 with
        /// blocksRaycasts on is fully clickable, and one at alpha 1 with interactable off must
        /// never be tapped.
        /// </summary>
        bool IsActionable(UiHandle handle, out string reason);

        // ---- Probe -----------------------------------------------------------------------

        /// <summary>
        /// Where a pointer must be placed to hit this node. Returns false with a reason when the
        /// node has no screen coordinate at all (render-texture panel, world-space surface,
        /// off-screen, behind the camera). The core never guesses a point on its own.
        /// </summary>
        bool TryResolveInjectionPoint(UiHandle handle, out Vector2 screenPoint, out string reason);

        /// <summary>
        /// What actually sits at <paramref name="screenPoint"/>, relative to the intended target.
        /// The runner uses this to refuse a tap that would land on a modal instead of the button.
        /// </summary>
        HitResult HitTest(UiHandle handle, Vector2 screenPoint);

        /// <summary>How faithfully <see cref="HitTest"/> can answer in the current environment.</summary>
        OcclusionFidelity OcclusionFidelity { get; }

        /// <summary>
        /// What the pointer that is currently holding a button is doing in THIS UI system: nothing,
        /// holding a press some node is registered to receive drags from, or actually dragging.
        ///
        /// It exists because a drag gesture cannot otherwise tell "the item was picked up" from "the
        /// press went nowhere and the pointer is just sliding across the screen". Both leave the
        /// injected pointer in exactly the same place, so without this the verb can only emit and
        /// hope, and the flow finds out three assertions later.
        ///
        /// IT TAKES NO POINTER ID, on purpose. A device id would be an Input System concept in a
        /// contract that must also be answerable where there is no Input System, and the runner
        /// drives exactly one pointer with exactly one button: "the pointer that is pressed" names
        /// it without naming a device. When nothing is pressed the answer is
        /// <see cref="PointerDragOutcome.None"/>, which is what the caller wants to know anyway.
        ///
        /// Every UI system that has drags can answer it. uGUI keeps <c>pointerDrag</c> and
        /// <c>dragging</c> per pointer in its input module; UI Toolkit has the same fact as pointer
        /// capture (<c>PointerCaptureHelper.GetCapturingElement</c>), which its drag manipulators
        /// take on press and release on end. A backend for a surface with no drag concept at all
        /// answers <see cref="PointerDragOutcome.Unavailable"/> with a reason, and the runner then
        /// says it could not confirm rather than inventing a confirmation.
        /// </summary>
        PointerDragState GetDragState();

        // ---- Focus -----------------------------------------------------------------------

        /// <summary>
        /// This UI system's keyboard/gamepad focus ring, or null when it has none.
        ///
        /// Nullable on purpose. A focus ring is a genuinely optional capability — a backend for a
        /// surface that is only ever touched has no navigation graph to describe — and returning
        /// null is a far better answer than implementing members that can only fail. The runner
        /// treats null as "this backend cannot answer navigation questions" and says so by name.
        ///
        /// It is one member rather than several because the ring's own vocabulary (focused element,
        /// seed focus, describe a link) belongs together and does not belong on an interface whose
        /// contract is "describe and probe". See <see cref="IFocusRing"/> for why it never moves
        /// focus itself.
        /// </summary>
        IFocusRing FocusRing { get; }

        // ---- Write (fallbacks; the primary path is IInputDriver) --------------------------

        /// <summary>
        /// Synthesize the UI system's own event sequence at a point, without a device.
        /// Used when no input driver is available, and for UI Toolkit surfaces that have no screen
        /// coordinate (a render-texture panel can only be driven this way).
        /// </summary>
        bool TryDispatch(UiHandle handle, PointerGesture gesture, Vector2 screenPoint, out string error);

        /// <summary>Set the text of an input control through its own edit path, not by assigning a field.</summary>
        bool TrySetText(UiHandle handle, string text, out string error);

        /// <summary>
        /// Invoke the control's action directly. Last resort: it bypasses the input path entirely,
        /// so it proves nothing about whether a real user could have clicked it. The runner only
        /// reaches for this when a flow explicitly opts in.
        /// </summary>
        bool TryActivate(UiHandle handle, out string error);
    }
}
