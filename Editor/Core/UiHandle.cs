using System;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// Opaque, backend-owned reference to a single UI node.
    ///
    /// This is deliberately NOT an int instance id. A uGUI node is a <c>UnityEngine.Object</c> and
    /// has <c>GetInstanceID()</c>, but a UI Toolkit <c>VisualElement</c> is a plain C# object
    /// (VisualElement -> Focusable -> CallbackEventHandler -> object) with no engine identity at
    /// all. Freezing the handle as an int would make the UI Toolkit backend impossible without a
    /// core change, which is exactly the failure the Phase 2 litmus test is meant to catch.
    ///
    /// <see cref="Version"/> exists because UI Toolkit recycles elements aggressively (ListView
    /// virtualization, UXML re-clone). A backend bumps the version when it reuses a slot, so a
    /// handle captured before a rebuild fails loudly through <see cref="IUiBackend.IsHandleAlive"/>
    /// instead of silently resolving to whatever object now occupies that slot.
    /// </summary>
    public readonly struct UiHandle : IEquatable<UiHandle>
    {
        /// <summary>Index of the owning backend within the run's registry. Avoids a string compare per retry frame.</summary>
        public readonly int BackendId;

        /// <summary>Surface (Canvas / panel) the node belongs to.</summary>
        public readonly int SurfaceId;

        /// <summary>Backend-owned table index identifying the node.</summary>
        public readonly int Slot;

        /// <summary>Incremented by the backend whenever <see cref="Slot"/> is reused.</summary>
        public readonly int Version;

        public UiHandle(int backendId, int surfaceId, int slot, int version)
        {
            BackendId = backendId;
            SurfaceId = surfaceId;
            Slot = slot;
            Version = version;
        }

        /// <summary>A handle that refers to nothing. Distinguishable from a valid slot-0 handle.</summary>
        public static readonly UiHandle None = new UiHandle(-1, -1, -1, 0);

        public bool IsNone => BackendId < 0;

        public bool Equals(UiHandle other) =>
            BackendId == other.BackendId &&
            SurfaceId == other.SurfaceId &&
            Slot == other.Slot &&
            Version == other.Version;

        public override bool Equals(object obj) => obj is UiHandle other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = BackendId;
                hash = (hash * 397) ^ SurfaceId;
                hash = (hash * 397) ^ Slot;
                hash = (hash * 397) ^ Version;
                return hash;
            }
        }

        public static bool operator ==(UiHandle a, UiHandle b) => a.Equals(b);
        public static bool operator !=(UiHandle a, UiHandle b) => !a.Equals(b);

        public override string ToString() =>
            IsNone ? "UiHandle(none)" : $"UiHandle(b{BackendId}/s{SurfaceId}/#{Slot}v{Version})";
    }
}
