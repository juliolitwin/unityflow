using UnityEngine;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// A rendering surface that owns nodes: a uGUI Canvas, or a UI Toolkit runtime panel.
    /// Surfaces exist in the contract because sort order and layout readiness are per-surface
    /// properties, and both are needed before a single node can be trusted.
    /// </summary>
    public readonly struct UiSurface
    {
        /// <summary>Stable id for the lifetime of a run.</summary>
        public readonly int Id;

        /// <summary>Id of the backend that owns this surface.</summary>
        public readonly string BackendId;

        /// <summary>Human-readable name, used in reports.</summary>
        public readonly string Name;

        /// <summary>Coordinate space of everything on this surface.</summary>
        public readonly NodeSpace Space;

        /// <summary>Higher draws on top. Used to order candidates and to reason about occlusion.</summary>
        public readonly float SortOrder;

        /// <summary>Screen bounds of the surface, when it has any.</summary>
        public readonly Rect? ScreenRect;

        public UiSurface(int id, string backendId, string name, NodeSpace space, float sortOrder, Rect? screenRect)
        {
            Id = id;
            BackendId = backendId;
            Name = name;
            Space = space;
            SortOrder = sortOrder;
            ScreenRect = screenRect;
        }

        public override string ToString() => $"{BackendId}:{Name} ({Space}, sort {SortOrder})";
    }

    /// <summary>
    /// Whether a surface's geometry can be trusted yet.
    ///
    /// This exists because a freshly enabled UI Toolkit panel reports NaN for every
    /// <c>worldBound</c> until the first layout pass — and NaN silently PASSES most
    /// <c>Rect.Contains</c> and size comparisons. Without an explicit readiness gate the runner
    /// would tap a NaN coordinate and report a confusing failure somewhere else entirely.
    /// </summary>
    public enum SurfaceReadiness
    {
        /// <summary>Layout is computed and finite. Safe to read geometry.</summary>
        Ready,

        /// <summary>Attached but layout has not run yet. Retry next frame.</summary>
        LayoutPending,

        /// <summary>Exists but is not being rendered (disabled canvas, panel with no target display).</summary>
        NotRendered,

        /// <summary>Gone. Any handle into it is stale.</summary>
        Unavailable
    }
}
