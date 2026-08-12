using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityFlow.Editor.Backends.InputInjection
{
    /// <summary>
    /// Removes virtual input devices that a previous UnityFlow session created and failed to clean up.
    ///
    /// This exists because a virtual device outlives the code that created it. An
    /// <c>InputSystem.AddDevice</c> that is never paired with a <c>RemoveDevice</c> — because the run
    /// threw, the editor was killed, or a C# eval aborted mid-statement — leaves the device wired into
    /// the input system for the rest of the editor's life. It then survives domain reloads, steals
    /// <c>Mouse.current</c> from the user's real mouse, and forces every later session's device name to
    /// be uniquified (<c>UnityFlowMouse</c> -&gt; <c>UnityFlowMouse1</c>), so the leak compounds.
    ///
    /// Ownership is decided by TWO conditions that must both hold, never by one:
    /// <list type="number">
    /// <item>
    ///   <c>InputDevice.description.empty</c> — a device that a platform backend reported always carries
    ///   a non-empty <see cref="InputDeviceDescription"/> (verified on Windows, where the real devices are
    ///   "Keyboard (RawInput)" and "Mouse (RawInput)"). A device added by
    ///   <c>InputSystem.AddDevice&lt;T&gt;(name)</c> has an empty one.
    /// </item>
    /// <item>
    ///   The device name starts with <see cref="OwnedDeviceNamePrefix"/>. The prefix survives the input
    ///   system's name uniquification, so a leaked <c>UnityFlowMouse1</c> is still matched.
    /// </item>
    /// </list>
    /// Both conditions together mean a real device can never be swept: a physical device fails the first
    /// test regardless of what it is called, and another tool's virtual device fails the second.
    /// </summary>
    public sealed class InputDeviceSweep
    {
        /// <summary>Every device UnityFlow creates is named with this prefix, which is what makes it identifiable later.</summary>
        public const string OwnedDeviceNamePrefix = "UnityFlow";

        /// <summary>Name of the injected mouse. Appears in control paths as <c>/UnityFlowMouse/leftButton</c>.</summary>
        public const string MouseDeviceName = "UnityFlowMouse";

        /// <summary>Name of the injected keyboard.</summary>
        public const string KeyboardDeviceName = "UnityFlowKeyboard";

        /// <summary>Usage tagged onto every device we create, so the ownership is also visible in the Input Debugger.</summary>
        public const string OwnerUsage = "UnityFlow";

        private readonly List<InputDevice> m_Doomed = new List<InputDevice>();
        private readonly StringBuilder m_Report = new StringBuilder();

        /// <summary>
        /// Remove every leaked UnityFlow device currently registered.
        /// </summary>
        /// <param name="removedDeviceNames">
        /// Comma-separated names of the devices that were removed, or null when there were none.
        /// </param>
        /// <returns>How many devices were removed.</returns>
        public int Run(out string removedDeviceNames)
        {
            m_Doomed.Clear();

            var devices = InputSystem.devices;
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (IsOwnedByUnityFlow(device))
                    m_Doomed.Add(device);
            }

            if (m_Doomed.Count == 0)
            {
                removedDeviceNames = null;
                return 0;
            }

            m_Report.Clear();
            for (var i = 0; i < m_Doomed.Count; i++)
            {
                if (i > 0)
                    m_Report.Append(", ");
                m_Report.Append(m_Doomed[i].name);

                InputSystem.RemoveDevice(m_Doomed[i]);
            }

            var removed = m_Doomed.Count;
            m_Doomed.Clear();
            removedDeviceNames = m_Report.ToString();
            return removed;
        }

        /// <summary>
        /// Whether this device was created by UnityFlow. Deliberately conservative: it must fail closed,
        /// because a false positive unregisters the user's real mouse or keyboard.
        /// </summary>
        public static bool IsOwnedByUnityFlow(InputDevice device)
        {
            if (device == null)
                return false;

            if (!device.description.empty)
                return false;

            return device.name != null &&
                   device.name.StartsWith(OwnedDeviceNamePrefix, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Reclaims, once per domain load, everything an injection session can leak: the virtual devices and
    /// the project-wide input settings.
    ///
    /// Deferred through <c>delayCall</c> rather than run straight from the static constructor so the
    /// input system has finished its own <c>InitializeOnLoad</c> and the device list is final. A session
    /// cannot be open at this point — <see cref="InputSessionScope"/> disposes itself before an assembly
    /// reload — so anything matched here is by definition a leak from a run that no longer exists.
    ///
    /// The two leaks have the same cause (a session that never got to dispose) but different lifetimes: a
    /// device leak dies with the editor process, a settings leak is serialized into the project and does
    /// not. Both are repaired here so an editor that was killed mid-run leaves nothing behind.
    /// </summary>
    [InitializeOnLoad]
    internal static class InputDeviceSweepBootstrap
    {
        static InputDeviceSweepBootstrap()
        {
            EditorApplication.delayCall += SweepOnce;
        }

        private static void SweepOnce()
        {
            var removed = new InputDeviceSweep().Run(out var removedDeviceNames);
            if (removed > 0)
                Debug.Log($"[UnityFlow] Removed {removed} leaked virtual input device(s) from an earlier session: {removedDeviceNames}");

            new InputSettingsStash().RestoreIfLeftOver();
        }
    }
}
