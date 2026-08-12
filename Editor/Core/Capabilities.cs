using System;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// What a backend can actually do to a node.
    ///
    /// The original two-tier model ("Tier 1 = no dependencies, no occlusion; Tier 2 = Input System,
    /// real injection, occlusion") cuts on the wrong axis. Occlusion does not need the Input
    /// System at all — <c>EventSystem.RaycastAll</c>, <c>Graphic.Raycast</c> and
    /// <c>IPanel.Pick</c> are public and touch no input code. What occlusion actually needs is
    /// play mode. So write mechanism and occlusion fidelity are two independent axes, reported
    /// separately, and printed in the run header so a reader always knows what was verified.
    /// </summary>
    [Flags]
    public enum WriteCapability
    {
        None = 0,

        /// <summary>Synthesize UI events directly (uGUI ExecuteEvents). No package dependency, works in edit mode.</summary>
        SemanticDispatch = 1 << 0,

        /// <summary>Move a real pointer device to a screen coordinate. Requires an input driver.</summary>
        ScreenPointerInjection = 1 << 1,

        /// <summary>Send a typed event straight to the element (UI Toolkit SendEvent). Fully public API.</summary>
        DirectEventSend = 1 << 2,

        /// <summary>Set the text of an input control.</summary>
        TextEntry = 1 << 3,

        /// <summary>Invoke the control's action directly (Button.onClick). Last resort; skips the input path entirely.</summary>
        Activate = 1 << 4
    }

    /// <summary>How faithfully occlusion could be checked for a given step.</summary>
    public enum OcclusionFidelity
    {
        /// <summary>Nothing was checked. A tap here can silently succeed through a modal.</summary>
        None,

        /// <summary>
        /// Per-element only: the node's own raycast filters were honoured (CanvasGroup.blocksRaycasts,
        /// Mask, RectMask2D, alphaHitTest), but nothing ruled out another surface on top.
        /// This is what edit mode can offer: EventSystem is not [ExecuteAlways], so
        /// EventSystem.current stays null and GraphicRaycaster returns zero hits.
        /// </summary>
        PerElement,

        /// <summary>Full cross-surface raycast through the live EventSystem. Requires play mode.</summary>
        CrossSurface
    }

    /// <summary>A pointer interaction to perform at an already-resolved screen point.</summary>
    public enum PointerGesture
    {
        /// <summary>Press then release at the same point.</summary>
        Click,

        /// <summary>Press and hold.</summary>
        Down,

        /// <summary>Release.</summary>
        Up,

        /// <summary>Two clicks within the double-click interval.</summary>
        DoubleClick
    }
}
