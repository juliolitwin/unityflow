using System;
using UnityEngine;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// The coordinate space a node's geometry lives in.
    ///
    /// The original design assumed every node collapses to one screen-space Rect. Two cases break
    /// that outright: a UI Toolkit panel rendering into a <c>PanelSettings.targetTexture</c> (the
    /// screen-to-panel mapping is an arbitrary user-supplied function and is not invertible), and
    /// a world-space surface (a 3D quad has no axis-aligned screen rect). Those nodes are still
    /// enumerable and assertable; they are simply not injectable by screen coordinate.
    /// </summary>
    public enum NodeSpace
    {
        /// <summary>Screen-space overlay. World corners already are physical screen pixels.</summary>
        ScreenOverlay,

        /// <summary>Screen-space through a camera. Requires projection and a near-plane guard.</summary>
        ScreenCamera,

        /// <summary>Panel-local coordinates (UI Toolkit). Convertible to screen only for on-screen panels.</summary>
        PanelLocal,

        /// <summary>Genuinely 3D. No meaningful axis-aligned screen rect.</summary>
        World3D
    }

    /// <summary>
    /// Observable state of a node. Flags rather than bools so a backend reports everything it knows
    /// in one pass, and so the failure report can show precisely which condition failed.
    /// </summary>
    [Flags]
    public enum NodeState
    {
        None = 0,

        /// <summary>The node and all its ancestors are active/attached.</summary>
        ActiveInHierarchy = 1 << 0,

        /// <summary>Actually drawn: non-zero cumulative alpha, not culled, on screen.</summary>
        Visible = 1 << 1,

        /// <summary>Interactable in its own right (Selectable.IsInteractable / enabledInHierarchy).</summary>
        Enabled = 1 << 2,

        /// <summary>Accepts pointer hits (raycastTarget / blocksRaycasts / pickingMode).</summary>
        Hittable = 1 << 3,

        /// <summary>Currently focused / selected by the event system.</summary>
        Focused = 1 << 4,

        /// <summary>Toggle-like control in the "on" state.</summary>
        Checked = 1 << 5,

        /// <summary>Layout has been computed and produced finite numbers.</summary>
        LayoutValid = 1 << 6
    }

    /// <summary>
    /// One UI element, normalized across backends. Read-only by construction: backends never
    /// mutate through a node, they only describe.
    /// </summary>
    public readonly struct UiNode
    {
        /// <summary>Backend-owned identity. Use this, never the index into a result list.</summary>
        public readonly UiHandle Handle;

        /// <summary>Surface (Canvas / panel) this node belongs to.</summary>
        public readonly int SurfaceId;

        /// <summary>
        /// Control type as an OPEN string, not an enum. A UI Toolkit button may be a
        /// <c>Button</c>, or a bare <c>VisualElement</c> carrying a <c>Clickable</c> manipulator;
        /// a uGUI button may be a project subclass. A closed enum loses that.
        /// </summary>
        public readonly string Type;

        /// <summary>GameObject name (uGUI) or VisualElement name (UI Toolkit).</summary>
        public readonly string Name;

        /// <summary>Stable automation id from <see cref="FlowTestId"/> or the UI Toolkit equivalent. May be null.</summary>
        public readonly string TestId;

        /// <summary>Full hierarchy path. Drives deterministic ordering and the "obscured by X" message.</summary>
        public readonly string Path;

        /// <summary>USS classes (UI Toolkit). Empty for uGUI. Real UI Toolkit code identifies controls by class far more than by name.</summary>
        public readonly string[] Classes;

        /// <summary>Visible text, already parsed (rich-text tags resolved). May be null.</summary>
        public readonly string Text;

        /// <summary>Current value of an input/toggle/slider, stringified. May be null.</summary>
        public readonly string Value;

        /// <summary>Which space <see cref="ScreenRect"/> and <see cref="InjectionPoint"/> were derived from.</summary>
        public readonly NodeSpace Space;

        /// <summary>
        /// Axis-aligned screen bounds, already intersected with ancestor masks and the screen.
        /// FOR REPORTING AND SCREENSHOT OVERLAYS ONLY. Null when the node has no screen rect.
        ///
        /// The core must never derive a tap point from this. A rotated element's AABB is not the
        /// element: a 25 degree rotation grew a 400x200 element's AABB to 254x199 px, and for a
        /// world-space canvas yawed 55 degrees the AABB centre sat 5.91 px outside the projected
        /// centre. Ask the backend for <see cref="InjectionPoint"/> instead.
        /// </summary>
        public readonly Rect? ScreenRect;

        /// <summary>
        /// Where a pointer must be placed to hit this node, computed by the owning backend and
        /// already hit-probed. Null means "not injectable HERE" — the runner must refuse to guess
        /// and fail loudly rather than click a plausible-looking coordinate.
        ///
        /// A backend fills this only when it can probe during enumeration. The uGUI backend cannot:
        /// probing raycasts the whole surface per point, which enumeration runs for every node on
        /// every retry frame, and a geometric centre that has not been probed is exactly the
        /// plausible-looking wrong coordinate this field exists to prevent (an element under a modal
        /// has one). It therefore reports null for every node, and
        /// <see cref="IUiBackend.TryResolveInjectionPoint"/> — which probes, and falls back through
        /// a deterministic grid — is the authoritative answer for one specific node.
        /// </summary>
        public readonly Vector2? InjectionPoint;

        /// <summary>Everything the backend could determine about the node's state in one pass.</summary>
        public readonly NodeState State;

        /// <summary>Draw/sort order (Canvas.sortingOrder, panel sortingPriority). Higher is on top.</summary>
        public readonly float SortKey;

        /// <summary>
        /// Why the node is not visible / not actionable, in plain language
        /// ("CanvasGroup alpha 0.00", "obscured by /Canvas/Modal/Blocker", "layout not computed").
        /// This is the single highest-value field in a failure report. Null when nothing is wrong.
        /// </summary>
        public readonly string Reason;

        public UiNode(
            UiHandle handle,
            int surfaceId,
            string type,
            string name,
            string testId,
            string path,
            string[] classes,
            string text,
            string value,
            NodeSpace space,
            Rect? screenRect,
            Vector2? injectionPoint,
            NodeState state,
            float sortKey,
            string reason)
        {
            Handle = handle;
            SurfaceId = surfaceId;
            Type = type;
            Name = name;
            TestId = testId;
            Path = path;
            Classes = classes ?? Array.Empty<string>();
            Text = text;
            Value = value;
            Space = space;
            ScreenRect = screenRect;
            InjectionPoint = injectionPoint;
            State = state;
            SortKey = sortKey;
            Reason = reason;
        }

        public bool IsVisible => (State & NodeState.Visible) != 0;
        public bool IsEnabled => (State & NodeState.Enabled) != 0;
        public bool IsHittable => (State & NodeState.Hittable) != 0;
        public bool HasValidLayout => (State & NodeState.LayoutValid) != 0;

        /// <summary>
        /// Actionable in the Playwright sense: present, laid out, visible, enabled and hit-testable.
        /// Occlusion is verified separately, at injection time, because it depends on the point.
        /// </summary>
        public bool IsActionable =>
            (State & (NodeState.ActiveInHierarchy | NodeState.Visible | NodeState.Enabled |
                      NodeState.Hittable | NodeState.LayoutValid))
            == (NodeState.ActiveInHierarchy | NodeState.Visible | NodeState.Enabled |
                NodeState.Hittable | NodeState.LayoutValid);

        public override string ToString() =>
            $"{Path} [{Type}]{(string.IsNullOrEmpty(Text) ? "" : $" \"{Text}\"")}";
    }
}
