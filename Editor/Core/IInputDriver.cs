using System;
using UnityEngine;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// The single write path: produce real device input.
    ///
    /// This is NOT a backend method, and that is the important part. One injected mouse is read
    /// independently by uGUI (through InputSystemUIInputModule) and by UI Toolkit (through the
    /// InputForUI event provider), so the driver is global and surface-agnostic. Putting injection
    /// on IUiBackend would force every backend to reimplement it and would still leave gameplay
    /// input — which belongs to no UI system — without a home.
    ///
    /// Producing a real event rather than simulating one is what buys occlusion for free: if a
    /// modal covers the button, the raycast hits the modal and the test fails, which is correct.
    /// </summary>
    public interface IInputDriver
    {
        /// <summary>Short id for reports, e.g. "inputsystem".</summary>
        string Id { get; }

        /// <summary>
        /// Whether injection can work right now. The reason matters more here than anywhere else:
        /// several independent settings silently swallow injected input when the Game View is
        /// unfocused, which is exactly the CI and agent case.
        /// </summary>
        bool IsAvailable(out string reason);

        /// <summary>
        /// Open an injection session: create virtual devices and apply the settings that keep
        /// input flowing while unfocused. Disposing restores every mutated setting and removes
        /// every device created. Global state is only ever touched inside a session, so a failed
        /// run cannot leave the editor in a strange input state.
        /// </summary>
        IDisposable BeginSession();

        /// <summary>Move the pointer to a screen coordinate. Must be a full state event, not a delta.</summary>
        void MovePointer(Vector2 screenPoint);

        /// <summary>Press a pointer button (0 = left).</summary>
        void PressPointer(int button);

        /// <summary>Release a pointer button (0 = left).</summary>
        void ReleasePointer(int button);

        /// <summary>Press a key by its Input System control name, e.g. "enter", "a", "escape".</summary>
        void PressKey(string key);

        /// <summary>Release a key by its Input System control name.</summary>
        void ReleaseKey(string key);

        /// <summary>
        /// Push queued events into the input system so the next frame observes them.
        /// Called by the driver loop, not by steps.
        /// </summary>
        void Flush();
    }
}
