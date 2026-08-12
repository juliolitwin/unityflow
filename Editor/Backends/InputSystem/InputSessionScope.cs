using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace UnityFlow.Editor.Backends.InputInjection
{
    /// <summary>
    /// Owns every piece of global state an injection run needs, and guarantees it is handed back.
    ///
    /// The scope exists because injecting input into an UNFOCUSED Game View — the CI and agent case, and
    /// the default configuration — is silently blocked by five independent gates. All five were traced in
    /// com.unity.inputsystem 1.19.0:
    /// <list type="number">
    /// <item>
    ///   <c>InputManager.defaultUpdateType</c> returns <c>InputUpdateType.Editor</c> whenever
    ///   <c>!gameIsPlaying || !gameHasFocus</c>, so <c>InputSystem.Update()</c> runs as an editor update.
    /// </item>
    /// <item>
    ///   <c>InputActionState.NotifyControlStateChanged</c> hard-returns when
    ///   <c>InputState.currentUpdateType == InputUpdateType.Editor</c>, so no action callback ever fires —
    ///   which is what actually drives <c>InputSystemUIInputModule</c>.
    /// </item>
    /// <item>
    ///   <c>InputManager.OnUpdate</c> withholds pointer and keyboard events on editor updates.
    /// </item>
    /// <item>
    ///   <c>BackgroundBehavior.ResetAndDisableNonBackgroundDevices</c> (the default) disables the mouse and
    ///   drops its events while the game does not have focus.
    /// </item>
    /// <item>
    ///   <c>InputSystemUIInputModule.Process</c> skips all pointer processing when
    ///   <c>!eventSystem.isFocused &amp;&amp; !shouldIgnoreFocus</c>.
    /// </item>
    /// </list>
    /// Two settings unlock all five at once, because <c>InputManager.gameShouldGetInputRegardlessOfFocus</c>
    /// requires BOTH of them together, and it is what makes <c>gameHasFocus</c> report true:
    /// <c>backgroundBehavior = IgnoreFocus</c> and
    /// <c>editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView</c>. Once <c>gameHasFocus</c>
    /// is true the update type becomes Dynamic, so gates 1-4 open; gate 5 opens because
    /// <c>InputSystemUIInputModule.shouldIgnoreFocus</c> reads <c>IgnoreFocus</c> directly.
    /// <c>Application.runInBackground</c> is set as well because gate 5 also accepts it on its own, and the
    /// player loop must keep ticking for <c>EventSystem.Update</c> to run at all.
    ///
    /// Every one of those is PROJECT-WIDE state, serialized into the InputSettings asset and into
    /// ProjectSettings. The scope therefore captures the previous values up front and restores them from
    /// <see cref="Dispose"/>, which additionally runs from <c>AssemblyReloadEvents.beforeAssemblyReload</c>
    /// and from <c>EditorApplication.playModeStateChanged</c> — a run that recompiles or is stopped from the
    /// toolbar must not leave the editor's input configuration rewritten behind the user's back.
    ///
    /// None of those hooks run when the editor is KILLED (a crash, or the taskkill an agent uses to end a
    /// hung run), and that is the case the settings would otherwise stay rewritten forever. So the captured
    /// values are also written to <see cref="InputSettingsStash"/>, which outlives the process, and
    /// <c>InputDeviceSweepBootstrap</c> restores them on the next editor start alongside the devices it
    /// already reclaims.
    /// </summary>
    public sealed class InputSessionScope : IDisposable
    {
        private const InputSettings.EditorInputBehaviorInPlayMode AppliedEditorInputBehavior =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

        private const InputSettings.BackgroundBehavior AppliedBackgroundBehavior =
            InputSettings.BackgroundBehavior.IgnoreFocus;

        private const bool AppliedRunInBackground = true;

        private readonly InputSettings m_Settings;
        private readonly InputSettingsStash m_Stash = new InputSettingsStash();
        private readonly InputSettings.EditorInputBehaviorInPlayMode m_PreviousEditorInputBehavior;
        private readonly InputSettings.BackgroundBehavior m_PreviousBackgroundBehavior;
        private readonly bool m_PreviousRunInBackground;

        private Mouse m_Mouse;
        private Keyboard m_Keyboard;
        private bool m_Disposed;
        private bool m_HooksInstalled;

        /// <summary>The injected mouse. Valid until the scope is disposed.</summary>
        public Mouse Mouse
        {
            get
            {
                ThrowIfDisposed();
                return m_Mouse;
            }
        }

        /// <summary>The injected keyboard. Valid until the scope is disposed.</summary>
        public Keyboard Keyboard
        {
            get
            {
                ThrowIfDisposed();
                return m_Keyboard;
            }
        }

        /// <summary>
        /// True once the scope has handed its global state back. The scope disposes ITSELF on an assembly
        /// reload or on leaving play mode, so a driver must re-check this before every injection instead of
        /// assuming the session it opened is still alive.
        /// </summary>
        public bool IsDisposed => m_Disposed;

        public InputSessionScope()
        {
            m_Settings = InputSystem.settings;
            if (m_Settings == null)
                throw new InvalidOperationException(
                    "the Input System has no InputSettings asset, so focus behaviour cannot be configured; " +
                    "assign one under Project Settings > Input System Package");

            m_PreviousEditorInputBehavior = m_Settings.editorInputBehaviorInPlayMode;
            m_PreviousBackgroundBehavior = m_Settings.backgroundBehavior;
            m_PreviousRunInBackground = Application.runInBackground;

            try
            {
                // A device leaked by an earlier run would push our names to UnityFlowMouse1 and keep
                // stealing Mouse.current, so clear it before claiming the canonical names.
                new InputDeviceSweep().Run(out _);

                // Written BEFORE the first mutation: a kill in between must never find a rewritten setting
                // with no record of what it used to be. The applied values are constants, so they are known
                // ahead of time.
                m_Stash.Write(
                    m_Settings,
                    new InputSettingsStash.Snapshot(
                        m_PreviousEditorInputBehavior, m_PreviousBackgroundBehavior, m_PreviousRunInBackground),
                    new InputSettingsStash.Snapshot(
                        AppliedEditorInputBehavior, AppliedBackgroundBehavior, AppliedRunInBackground));

                m_Settings.editorInputBehaviorInPlayMode = AppliedEditorInputBehavior;
                m_Settings.backgroundBehavior = AppliedBackgroundBehavior;

                if (Application.runInBackground != AppliedRunInBackground)
                    Application.runInBackground = AppliedRunInBackground;

                m_Mouse = InputSystem.AddDevice<Mouse>(InputDeviceSweep.MouseDeviceName);
                InputSystem.AddDeviceUsage(m_Mouse, InputDeviceSweep.OwnerUsage);

                m_Keyboard = InputSystem.AddDevice<Keyboard>(InputDeviceSweep.KeyboardDeviceName);
                InputSystem.AddDeviceUsage(m_Keyboard, InputDeviceSweep.OwnerUsage);

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                m_HooksInstalled = true;
            }
            catch
            {
                // Partial construction is the most dangerous state there is: half the settings rewritten and
                // one device registered. Unwind everything before the exception leaves the constructor.
                RemoveOwnedDevices();
                RestoreGlobalState();
                throw;
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Disposed = true;

            if (m_HooksInstalled)
            {
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
                m_HooksInstalled = false;
            }

            try
            {
                RemoveOwnedDevices();
            }
            finally
            {
                // Settings are editor-wide and visible in the Inspector; they must come back even if
                // device removal threw.
                RestoreGlobalState();
            }
        }

        private void OnBeforeAssemblyReload()
        {
            Dispose();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Injection is only meaningful while the player loop runs. Leaving play mode ends the run, and
            // the devices must not survive into edit mode where they would shadow the real ones.
            if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
                Dispose();
        }

        private void RemoveOwnedDevices()
        {
            if (m_Mouse != null)
            {
                if (m_Mouse.added)
                    InputSystem.RemoveDevice(m_Mouse);
                m_Mouse = null;
            }

            if (m_Keyboard != null)
            {
                if (m_Keyboard.added)
                    InputSystem.RemoveDevice(m_Keyboard);
                m_Keyboard = null;
            }
        }

        private void RestoreGlobalState()
        {
            if (m_Settings != null)
            {
                m_Settings.editorInputBehaviorInPlayMode = m_PreviousEditorInputBehavior;
                m_Settings.backgroundBehavior = m_PreviousBackgroundBehavior;
            }

            if (Application.runInBackground != m_PreviousRunInBackground)
                Application.runInBackground = m_PreviousRunInBackground;

            // Cleared last: while the stash exists the next editor start would redo this restore, which is
            // exactly what we want if anything above throws.
            m_Stash.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(
                    nameof(InputSessionScope),
                    "the injection session has already ended (it is disposed on assembly reload and on leaving play mode); " +
                    "open a new session before injecting again");
        }
    }

    /// <summary>
    /// Remembers, across an editor kill, which project-wide input settings an open injection session
    /// rewrote and what they were before.
    ///
    /// <see cref="InputSessionScope"/> restores those settings from <c>Dispose</c>, but none of the hooks
    /// that call <c>Dispose</c> run when the editor process dies — a crash, or the taskkill an agent uses to
    /// end a hung run. Without a record that outlives the process, the project keeps
    /// <c>editorInputBehaviorInPlayMode=AllDeviceInputAlwaysGoesToGameView</c>,
    /// <c>backgroundBehavior=IgnoreFocus</c> and <c>runInBackground=true</c> written to disk, with no way to
    /// tell they were not the user's choice.
    ///
    /// <c>EditorPrefs</c> is the store because it is written through immediately and survives the process;
    /// <c>SessionState</c> is cleared on editor restart, which is the only moment this record is ever read.
    /// A second copy in a project file would be a second source of truth that could disagree with the first,
    /// so there is exactly one. The key carries the project path because EditorPrefs is machine-wide and two
    /// projects open side by side must not restore each other's settings.
    ///
    /// Restoration is CONDITIONAL. Each setting is only reverted while it still holds the value UnityFlow
    /// wrote; if the user changed it after the kill, that change wins and the reason is logged. The
    /// InputSettings asset is identified by path for the same reason: values captured from one asset must
    /// never be written into another.
    /// </summary>
    public sealed class InputSettingsStash
    {
        /// <summary>The three project-wide values an injection session touches, captured together.</summary>
        public readonly struct Snapshot
        {
            public readonly InputSettings.EditorInputBehaviorInPlayMode EditorInputBehavior;
            public readonly InputSettings.BackgroundBehavior BackgroundBehavior;
            public readonly bool RunInBackground;

            public Snapshot(
                InputSettings.EditorInputBehaviorInPlayMode editorInputBehavior,
                InputSettings.BackgroundBehavior backgroundBehavior,
                bool runInBackground)
            {
                EditorInputBehavior = editorInputBehavior;
                BackgroundBehavior = backgroundBehavior;
                RunInBackground = runInBackground;
            }
        }

        private const string KeyPrefix = "UnityFlow.InputSessionStash.";
        private const string FieldSeparator = "|";
        private const char FieldSeparatorChar = '|';
        private const string PayloadVersion = "1";
        private const int FieldCount = 8;
        private const int PreviousFieldIndex = 2;
        private const int AppliedFieldIndex = 5;

        private readonly string m_Key;

        public InputSettingsStash()
        {
            m_Key = KeyPrefix + Application.dataPath;
        }

        /// <summary>
        /// Records that a session is about to rewrite <paramref name="settings"/> from
        /// <paramref name="previous"/> to <paramref name="applied"/>. Call before the first mutation.
        /// </summary>
        public void Write(InputSettings settings, Snapshot previous, Snapshot applied)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var payload = string.Join(FieldSeparator, new[]
            {
                PayloadVersion,
                DescribeSettingsIdentity(settings),
                previous.EditorInputBehavior.ToString(),
                previous.BackgroundBehavior.ToString(),
                previous.RunInBackground ? "1" : "0",
                applied.EditorInputBehavior.ToString(),
                applied.BackgroundBehavior.ToString(),
                applied.RunInBackground ? "1" : "0"
            });

            EditorPrefs.SetString(m_Key, payload);
        }

        /// <summary>Drops the record, because the session handed its state back the normal way.</summary>
        public void Clear()
        {
            EditorPrefs.DeleteKey(m_Key);
        }

        /// <summary>
        /// Restores the settings of a session that never got to dispose, and drops the record. Does nothing
        /// when there is no record, which is the normal case.
        /// </summary>
        public void RestoreIfLeftOver()
        {
            if (!EditorPrefs.HasKey(m_Key))
                return;

            var payload = EditorPrefs.GetString(m_Key);

            // Dropped up front: a record that cannot be acted on must not be retried on every domain load.
            EditorPrefs.DeleteKey(m_Key);

            var fields = payload.Split(FieldSeparatorChar);
            if (fields.Length != FieldCount || fields[0] != PayloadVersion ||
                !TryParseSnapshot(fields, PreviousFieldIndex, out var previous) ||
                !TryParseSnapshot(fields, AppliedFieldIndex, out var applied))
            {
                Debug.LogError(
                    "[UnityFlow] an injection session was interrupted, but its record of the input settings it " +
                    $"rewrote cannot be read back and has been discarded: '{payload}'. Check " +
                    "Project Settings > Input System Package for editorInputBehaviorInPlayMode / " +
                    "backgroundBehavior and Player Settings for Run In Background.");
                return;
            }

            var settings = InputSystem.settings;
            if (settings == null)
            {
                Debug.LogError(
                    "[UnityFlow] an injection session was interrupted with rewritten input settings, but the project " +
                    "now has no InputSettings asset to restore them into; the record has been discarded.");
                return;
            }

            var currentIdentity = DescribeSettingsIdentity(settings);
            if (string.Equals(currentIdentity, fields[1], StringComparison.Ordinal))
            {
                RestoreEditorInputBehavior(settings, previous, applied);
                RestoreBackgroundBehavior(settings, previous, applied);
            }
            else
            {
                Debug.LogWarning(
                    $"[UnityFlow] an injection session was interrupted while it had rewritten '{fields[1]}', but the " +
                    $"project's InputSettings are now '{currentIdentity}'; leaving them alone rather than writing " +
                    "values captured from a different asset.");
            }

            RestoreRunInBackground(previous, applied);
        }

        /// <summary>
        /// Identifies WHICH InputSettings the captured values came from. Values taken from one asset must
        /// never be written into another, and a project can swap its InputSettings asset between the
        /// interrupted run and the next editor start. Settings that are not an asset at all (Unity's
        /// implicit default) have no path, so they get a fixed identity rather than an empty field.
        /// </summary>
        private string DescribeSettingsIdentity(InputSettings settings)
        {
            var path = AssetDatabase.GetAssetPath(settings);
            return string.IsNullOrEmpty(path) ? "<project default InputSettings>" : path;
        }

        private void RestoreEditorInputBehavior(InputSettings settings, Snapshot previous, Snapshot applied)
        {
            var current = settings.editorInputBehaviorInPlayMode;
            if (current != applied.EditorInputBehavior)
            {
                LogLeftAlone("InputSettings.editorInputBehaviorInPlayMode", current.ToString(),
                    applied.EditorInputBehavior.ToString(), previous.EditorInputBehavior.ToString());
                return;
            }

            settings.editorInputBehaviorInPlayMode = previous.EditorInputBehavior;
            LogRestored("InputSettings.editorInputBehaviorInPlayMode", previous.EditorInputBehavior.ToString());
        }

        private void RestoreBackgroundBehavior(InputSettings settings, Snapshot previous, Snapshot applied)
        {
            var current = settings.backgroundBehavior;
            if (current != applied.BackgroundBehavior)
            {
                LogLeftAlone("InputSettings.backgroundBehavior", current.ToString(),
                    applied.BackgroundBehavior.ToString(), previous.BackgroundBehavior.ToString());
                return;
            }

            settings.backgroundBehavior = previous.BackgroundBehavior;
            LogRestored("InputSettings.backgroundBehavior", previous.BackgroundBehavior.ToString());
        }

        private void RestoreRunInBackground(Snapshot previous, Snapshot applied)
        {
            var current = Application.runInBackground;
            if (current != applied.RunInBackground)
            {
                LogLeftAlone("Application.runInBackground", current.ToString(),
                    applied.RunInBackground.ToString(), previous.RunInBackground.ToString());
                return;
            }

            if (current != previous.RunInBackground)
                Application.runInBackground = previous.RunInBackground;

            LogRestored("Application.runInBackground", previous.RunInBackground.ToString());
        }

        private void LogRestored(string setting, string restoredValue)
        {
            Debug.Log(
                $"[UnityFlow] restored {setting} to {restoredValue}; an injection session was interrupted (the editor " +
                "exited without disposing it) after rewriting it project-wide.");
        }

        private void LogLeftAlone(string setting, string currentValue, string writtenValue, string previousValue)
        {
            Debug.LogWarning(
                $"[UnityFlow] left {setting} at {currentValue}: an interrupted injection session had written " +
                $"{writtenValue}, but the value is no longer the one UnityFlow wrote, so restoring {previousValue} " +
                "would overwrite a deliberate change.");
        }

        private bool TryParseSnapshot(string[] fields, int index, out Snapshot snapshot)
        {
            snapshot = default;

            if (!Enum.TryParse(fields[index], out InputSettings.EditorInputBehaviorInPlayMode editorInputBehavior))
                return false;

            if (!Enum.TryParse(fields[index + 1], out InputSettings.BackgroundBehavior backgroundBehavior))
                return false;

            var runInBackgroundField = fields[index + 2];
            if (runInBackgroundField != "0" && runInBackgroundField != "1")
                return false;

            snapshot = new Snapshot(editorInputBehavior, backgroundBehavior, runInBackgroundField == "1");
            return true;
        }
    }
}
