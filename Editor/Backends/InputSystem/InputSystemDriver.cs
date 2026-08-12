using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.InputInjection
{
    /// <summary>
    /// Produces real device input through the Input System's public event queue.
    ///
    /// This is the PRIMARY write path, not a nicety. On a project configured with
    /// <c>activeInputHandler:1</c> the legacy <c>UnityEngine.Input</c> throws and the UI runs on
    /// <c>InputSystemUIInputModule</c>, so this is the only way in. A UnityFlow tap is therefore a
    /// real mouse event that a real raycast resolves, which buys correct occlusion for free.
    ///
    /// The driver deliberately does NOT use <c>Unity.InputSystem.TestFramework</c>. That assembly's
    /// <c>InputTestFixture.Setup()</c> calls
    /// <c>InputSystem.SaveAndReset(enableRemoting: false, runtime: new InputTestRuntime())</c>, which
    /// severs the input system from the Unity runtime and removes every device — the game under test
    /// loses its own input. Its <c>Press</c>/<c>Release</c>/<c>Click</c> helpers also dereference NUnit's
    /// <c>TestContext.CurrentTestExecutionContext</c>, and its asmdef is gated behind
    /// <c>UNITY_TESTS_FRAMEWORK</c> with <c>autoReferenced: false</c>. Only the raw public API is used
    /// here: <c>AddDevice</c>, <c>QueueStateEvent</c>, <c>QueueEvent</c>, <c>Update</c>, <c>RemoveDevice</c>.
    ///
    /// FRAME CONTRACT (this is the part callers get wrong). Every method below only ENQUEUES; nothing is
    /// observable until <see cref="Flush"/> runs <c>InputSystem.Update()</c>, and even then the UI has not
    /// reacted. UI dispatch happens in <c>InputSystemUIInputModule.Process()</c>, which is called by
    /// <c>EventSystem.Update()</c> from the player loop — once per rendered frame. So the FlowDriver must
    /// let a real frame elapse after each flush. See the per-method documentation for the exact counts.
    ///
    /// THE KEYBOARD IS THE ONE EXCEPTION, and it is not a style choice. Pointer input reaches the game
    /// through <c>InputSystemUIInputModule</c>, which BUFFERS what the actions reported and replays it from
    /// <c>Process()</c>, so it does not care which update step applied the event. Gameplay keyboard input
    /// does not go through any of that: gameplay code typically reads
    /// <c>Keyboard.current[key].wasPressedThisFrame</c> directly, and that property is
    /// <c>InputUpdate.s_UpdateStepCount == m_UpdateCountLastPressed</c> — true during EXACTLY ONE input
    /// update step, the one that applied the event. <see cref="Flush"/> is called from the flow driver's
    /// tick in PostLateUpdate, and the player loop runs its own input update before the next
    /// <c>MonoBehaviour.Update</c>, so a key applied by <see cref="Flush"/> is already one step stale by the
    /// time the game looks: measured as 'w' moving the player (a level read, <c>isPressed</c>) while 'i'
    /// never opened the panel bound to it (an edge read, <c>wasPressedThisFrame</c>).
    ///
    /// So keyboard changes are NOT queued when they are asked for. They are handed to the player loop's own
    /// input update from <see cref="OnBeforeInputUpdate"/>, which is
    /// <c>InputSystem.onBeforeUpdate</c> — documented as "triggered before the input system runs its own
    /// update and before it flushes out its event queue... events queued from a callback will be fed right
    /// into the upcoming update", and that upcoming update is the Dynamic one the Input System runs right
    /// before <c>MonoBehaviour.Update</c>. The edge is therefore produced by the identical code path a real
    /// keyboard takes, which is why it is live for the whole of the frame the game reads it in, rather than
    /// by a manual update whose placement in the player loop we would have to keep guessing at.
    /// </summary>
    public sealed class InputSystemDriver : IInputDriver
    {
        private const int LeftButtonIndex = 0;
        private const int RightButtonIndex = 1;
        private const int MiddleButtonIndex = 2;

        private readonly Dictionary<string, Key> m_KeyByControlName =
            new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);

        private readonly StringBuilder m_Diagnostics = new StringBuilder();

        private InputSessionScope m_Session;
        private Mouse m_Mouse;
        private Keyboard m_Keyboard;

        // The authoritative pointer state. MouseState is a FULL state event: a queued MouseState that omits
        // the position writes 0 into it and teleports the pointer to the bottom-left corner (measured).
        // Keeping the whole state here means every full-state event we send carries the current position AND
        // the currently held buttons.
        private MouseState m_MouseState;
        private KeyboardState m_KeyboardState;
        private bool m_HasPointerPosition;

        // The keyboard state is authoritative here and is NOT queued when a key changes; see the type
        // documentation. These three fields are the whole handoff to the player loop's own input update.
        private bool m_KeyboardStatePending;
        private int m_FlushesWaitingForKeyboardHandoff;
        private bool m_HookedBeforeUpdate;

        // True only for the duration of the InputSystem.Update() that Flush() runs. The handoff must not
        // happen inside it: that update is driven from the flow driver's PostLateUpdate tick, so an edge
        // placed there sits in an update step the game's next Update never observes.
        private bool m_InDriverUpdate;

        public string Id => "inputsystem";

        /// <summary>
        /// Reports, as one diagnostic trail, every condition injection depends on: the Input System itself,
        /// play mode, the live EventSystem/module PAIRING that would actually dispatch, and the Game View
        /// focus state.
        ///
        /// The pairing is checked as one thing rather than as an "is there an EventSystem" scan plus an
        /// "is there a module" scan, because uGUI only ever runs a module that sits on
        /// <c>EventSystem.current</c>'s own GameObject: <c>EventSystem.UpdateModules()</c> is
        /// <c>GetComponents(m_SystemInputModules)</c> and <c>EventSystem.Update()</c> early-returns on
        /// <c>if (current != this) return;</c>. Two independent scans report ok for combinations that can
        /// never dispatch anything — a module on a sibling GameObject, or a second EventSystem from an
        /// additively loaded scene — which is precisely the silent availability this diagnostic exists to
        /// prevent.
        ///
        /// Reading <c>EventSystem.current</c> is safe here even though it is assigned from <c>OnEnable</c>:
        /// <see cref="CheckPlayMode"/> already fails the whole check outside play mode, and in play mode
        /// <c>EventSystem.current</c> is the authority on which instance dispatches.
        ///
        /// Focus is reported but never fails the check: <see cref="BeginSession"/> is what makes unfocused
        /// injection work, so refusing to run while unfocused would refuse the entire CI case.
        /// </summary>
        public bool IsAvailable(out string reason)
        {
            m_Diagnostics.Clear();

            var available = CheckInputSystem();
            available &= CheckPlayMode();
            available &= CheckEventSystemPairing();

            ReportFocusState();

            reason = m_Diagnostics.ToString();
            return available;
        }

        /// <summary>
        /// Creates the injected mouse and keyboard and applies the settings that keep input flowing while
        /// the Game View is unfocused. Disposing the returned scope removes both devices and restores every
        /// mutated setting; see <see cref="InputSessionScope"/> for why each one is needed.
        /// </summary>
        public IDisposable BeginSession()
        {
            if (m_Session != null && !m_Session.IsDisposed)
                throw new InvalidOperationException(
                    "the inputsystem driver already has an open session; dispose it before opening another, " +
                    "otherwise two virtual mice would compete for Mouse.current");

            if (!IsAvailable(out var reason))
                throw new InvalidOperationException($"the inputsystem driver cannot open a session: {reason}");

            m_Session = new InputSessionScope();
            m_Mouse = m_Session.Mouse;
            m_Keyboard = m_Session.Keyboard;

            m_MouseState = default;
            m_KeyboardState = default;
            m_HasPointerPosition = false;
            m_KeyboardStatePending = false;
            m_FlushesWaitingForKeyboardHandoff = 0;
            m_InDriverUpdate = false;

            VerifyButtonsShareOneDeltaEvent();
            BuildKeyTable();
            HookBeforeUpdate();

            return m_Session;
        }

        /// <summary>
        /// Queues a full mouse state event that moves the pointer to <paramref name="screenPoint"/>, in
        /// physical screen pixels with the origin at the bottom-left.
        ///
        /// FRAMES: call, then <see cref="Flush"/>, then let ONE frame elapse before pressing. The module
        /// computes the hover target and <c>pointerCurrentRaycast</c> for the new position inside
        /// <c>Process()</c>, and pointer-enter handlers routinely change the UI (tooltips, hover-expanded
        /// menus, list highlighting). Pressing in the same frame as the first move means pressing against
        /// geometry the UI has not yet acknowledged.
        ///
        /// The event is a full state event and therefore also re-asserts every currently held button, so a
        /// move during a drag does not release it.
        /// </summary>
        public void MovePointer(Vector2 screenPoint)
        {
            EnsureSession();

            // Real backends report movement as a delta alongside the absolute position; drag thresholds and
            // scroll-inertia code read it. The first move of a session has no previous position to subtract.
            m_MouseState.delta = m_HasPointerPosition
                ? screenPoint - m_MouseState.position
                : Vector2.zero;

            m_MouseState.position = screenPoint;
            m_HasPointerPosition = true;

            InputSystem.QueueStateEvent(m_Mouse, m_MouseState);
        }

        /// <summary>
        /// Queues a press of <paramref name="button"/> (0 = left, 1 = right, 2 = middle) at the pointer's
        /// current position.
        ///
        /// FRAMES: call, then <see cref="Flush"/>, then let ONE frame elapse before releasing. Collapsing
        /// press and release into a single frame drives <c>PointerModel.ButtonState</c> to
        /// <c>FramePressState.PressedAndReleased</c>: the click still fires, but nothing that lives between
        /// down and up survives — no drag ever starts, no press-and-hold ever triggers, and the runner loses
        /// its chance to re-verify that the press landed on the intended node.
        ///
        /// This uses a DELTA state event rather than <c>InputSystem.QueueDeltaStateEvent</c>, which throws
        /// for buttons: "Size 4 of delta state of type Single provided for control
        /// 'Button:/UnityFlowMouse/leftButton' does not match size 1 of control" (measured). A delta also
        /// leaves the position and the accumulated delta untouched, which a full state event would overwrite.
        /// </summary>
        public void PressPointer(int button)
        {
            EnsureSession();
            RequirePointerPosition(nameof(PressPointer));

            ResolveMouseButton(button, out var mouseButton);
            m_MouseState = m_MouseState.WithButton(mouseButton, true);

            QueueButtonState();
        }

        /// <summary>
        /// Queues a release of <paramref name="button"/> (0 = left, 1 = right, 2 = middle) at the pointer's
        /// current position.
        ///
        /// FRAMES: call, then <see cref="Flush"/>, then let ONE frame elapse before asserting on the result.
        /// The <c>pointerClick</c> and <c>endDrag</c> events are raised inside the <c>Process()</c> call
        /// that observes the release, so any scene change they cause only exists from the following frame.
        /// </summary>
        public void ReleasePointer(int button)
        {
            EnsureSession();
            RequirePointerPosition(nameof(ReleasePointer));

            ResolveMouseButton(button, out var mouseButton);
            m_MouseState = m_MouseState.WithButton(mouseButton, false);

            QueueButtonState();
        }

        /// <summary>
        /// Queues ONE delta state event carrying the state of ALL tracked mouse buttons.
        ///
        /// Writing only the button that changed loses presses. <c>DeltaStateEvent.From(control, ...)</c>
        /// seeds the event payload by <c>MemCpy</c>ing from <c>control.currentStatePtr</c> — the device state
        /// as ALREADY APPLIED — and left, right and middle all live in the same byte of
        /// <c>MouseState.buttons</c>. So a second button event built before <see cref="Flush"/> carries a
        /// STALE copy of that byte and, when both drain in the same <c>InputSystem.Update()</c>, overwrites
        /// the first event's bit. Measured: <c>PressPointer(0); PressPointer(1); Flush();</c> produced
        /// <c>L=False R=True</c> — the left press vanished with no error. Nothing in
        /// <c>IInputDriver</c> promises a flush between two button calls (Flush is the driver loop's job),
        /// so that sequence is legal and must not lose an event.
        ///
        /// Re-asserting every button from the authoritative <see cref="m_MouseState"/> makes the stale seed
        /// irrelevant: whatever the payload started as, all three bits are rewritten before it is queued.
        /// </summary>
        private void QueueButtonState()
        {
            using (DeltaStateEvent.From(m_Mouse.leftButton, out var eventPtr))
            {
                m_Mouse.leftButton.WriteValueIntoEvent(ButtonValue(MouseButton.Left), eventPtr);
                m_Mouse.rightButton.WriteValueIntoEvent(ButtonValue(MouseButton.Right), eventPtr);
                m_Mouse.middleButton.WriteValueIntoEvent(ButtonValue(MouseButton.Middle), eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }
        }

        private float ButtonValue(MouseButton button)
        {
            return (m_MouseState.buttons & (1 << (int)button)) != 0 ? 1f : 0f;
        }

        /// <summary>
        /// Proves, once per session, that all three injectable buttons fall inside the single delta event
        /// <see cref="QueueButtonState"/> builds from <c>leftButton</c>.
        ///
        /// This is not paranoia about a constant: <c>InputControlExtensions.WriteValueIntoEvent</c> returns
        /// SILENTLY when <c>GetStatePtrFromStateEvent</c> finds the control outside the event's range. If a
        /// future Mouse layout moved the buttons apart, every right/middle press would be dropped without a
        /// word — the same silent-loss failure this whole method exists to eliminate. Checked at session
        /// start rather than per event so the injection path stays a straight line.
        /// </summary>
        private void VerifyButtonsShareOneDeltaEvent()
        {
            using (DeltaStateEvent.From(m_Mouse.leftButton, out var eventPtr))
            {
                RequireControlInEvent(m_Mouse.leftButton, eventPtr);
                RequireControlInEvent(m_Mouse.rightButton, eventPtr);
                RequireControlInEvent(m_Mouse.middleButton, eventPtr);
            }
        }

        private void RequireControlInEvent(ButtonControl control, InputEventPtr eventPtr)
        {
            // ReadUnprocessedValueFromEvent reports false for exactly the condition that makes
            // WriteValueIntoEvent a no-op: the control is not covered by the event's state range.
            if (control.ReadUnprocessedValueFromEvent(eventPtr, out _))
                return;

            throw new InvalidOperationException(
                $"the mouse control '{control.path}' is not covered by a delta state event built from " +
                $"'{m_Mouse.leftButton.path}', so writing it would be silently discarded; this driver requires " +
                "left, right and middle to share one byte of MouseState.buttons, which the Mouse layout of " +
                $"Input System {InputSystem.version} no longer guarantees");
        }

        /// <summary>
        /// Records <paramref name="key"/> as held. <paramref name="key"/> is an Input System control name on
        /// the Keyboard layout — "enter", "a", "escape", "leftShift" — matched case-insensitively, plus the
        /// aliases <see cref="Alias"/> lists ("up" for "upArrow" and so on). An unknown name throws, naming
        /// the closest real control names, rather than being quietly ignored.
        ///
        /// Nothing is queued here. The state event is queued from <see cref="OnBeforeInputUpdate"/> so that
        /// the PLAYER LOOP'S OWN input update applies it, which is the only way an edge-triggered read
        /// (<c>wasPressedThisFrame</c>) can still be true when the game's <c>Update</c> runs. See the type
        /// documentation for the measurement behind that.
        ///
        /// FRAMES: call, then let ONE frame elapse before releasing — that frame is what carries the press
        /// into the game. Navigation (submit, cancel, move) is likewise processed once per frame in
        /// <c>InputSystemUIInputModule.ProcessNavigation</c>, so a key pressed and released inside one frame
        /// can be seen as never having changed.
        ///
        /// Held keys are tracked here and re-asserted on every event, so pressing a second key does not
        /// release the first — which is what makes modifier combinations work.
        /// </summary>
        public void PressKey(string key)
        {
            EnsureSession();

            m_KeyboardState.Set(ResolveKey(key), true);
            m_KeyboardStatePending = true;
        }

        /// <summary>
        /// Records <paramref name="key"/> as no longer held, on the same terms as <see cref="PressKey"/>:
        /// the event is queued by the player loop's own input update, not here.
        ///
        /// FRAMES: call, then let ONE frame elapse before asserting on the result, for the same reason as
        /// <see cref="ReleasePointer"/>.
        /// </summary>
        public void ReleaseKey(string key)
        {
            EnsureSession();

            m_KeyboardState.Set(ResolveKey(key), false);
            m_KeyboardStatePending = true;
        }

        /// <summary>
        /// Runs <c>InputSystem.Update()</c>, which drains the POINTER queue into device state and fires the
        /// action callbacks <c>InputSystemUIInputModule</c> listens to.
        ///
        /// This is NOT enough on its own. It updates what the module will read; the module only READS it
        /// from <c>Process()</c>, driven by <c>EventSystem.Update()</c> in the player loop. The caller must
        /// still yield a frame — several consecutive <c>Flush</c> calls without frames between them produce
        /// exactly one UI reaction, not several.
        ///
        /// It deliberately does NOT carry the keyboard. <see cref="m_InDriverUpdate"/> tells
        /// <see cref="OnBeforeInputUpdate"/> that this update is the flow driver's own, made from
        /// PostLateUpdate, and handing a key edge to it would place that edge in an update step the game's
        /// next <c>Update</c> never observes — which is the whole defect. The pending keyboard state is left
        /// alone and reaches the device on the player loop's next input update instead.
        /// </summary>
        public void Flush()
        {
            EnsureSession();

            // One flush per driver tick, and the player loop runs exactly one input update between two
            // ticks, so a state still pending at a SECOND flush means no input update ran at all and the
            // key would be lost without a word. Refusing here is the only place that can tell.
            if (m_KeyboardStatePending && ++m_FlushesWaitingForKeyboardHandoff > 1)
                throw new InvalidOperationException(
                    "a keyboard state has been waiting for the player loop's input update across two flushes, so no " +
                    "input update ran in between. Injected keys reach the game through InputSystem.onBeforeUpdate, " +
                    "which only fires while the player loop is running its own updates; a paused player loop or an " +
                    $"InputSettings.updateMode that stops them (currently {DescribeUpdateMode()}) leaves every key " +
                    "press silently undelivered, so the run is stopped instead of reporting a pass for input nothing saw");

            m_InDriverUpdate = true;
            try
            {
                InputSystem.Update();
            }
            finally
            {
                m_InDriverUpdate = false;
            }
        }

        // ---- Keyboard handoff to the player loop's own input update ------------------------

        private void HookBeforeUpdate()
        {
            if (m_HookedBeforeUpdate)
                return;

            InputSystem.onBeforeUpdate += OnBeforeInputUpdate;
            m_HookedBeforeUpdate = true;
        }

        private void UnhookBeforeUpdate()
        {
            if (!m_HookedBeforeUpdate)
                return;

            InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
            m_HookedBeforeUpdate = false;
        }

        /// <summary>
        /// Queues the pending keyboard state INTO the update that is about to run.
        ///
        /// <c>InputSystem.onBeforeUpdate</c> fires after the update type has been established and before the
        /// event queue is drained, so an event queued here is processed by that same update — the Input
        /// System's own documented contract for producing input at the last possible moment. When that
        /// update is the Dynamic one the player loop runs before <c>MonoBehaviour.Update</c>,
        /// <c>InputUpdate.s_UpdateStepCount</c> still equals the step that applied the press for the whole of
        /// that frame's script update, so <c>wasPressedThisFrame</c> reads true exactly once and exactly
        /// where a player's key press would read true.
        ///
        /// Two update types are refused rather than used. EDITOR updates write the editor's own state
        /// buffers and set the editor-side press counters, which the game never reads, so a key handed to one
        /// would be applied and then be invisible. BEFORE-RENDER updates run after the script update, so an
        /// edge placed there is already gone by the next frame's read.
        /// </summary>
        private void OnBeforeInputUpdate()
        {
            if (m_Session == null || m_Session.IsDisposed)
            {
                // The scope disposes itself on an assembly reload and on leaving play mode without telling
                // the driver, so the hook has to notice on its own rather than keep a dead callback alive.
                UnhookBeforeUpdate();
                return;
            }

            if (!m_KeyboardStatePending || m_InDriverUpdate)
                return;

            var updateType = InputState.currentUpdateType;
            if (updateType == InputUpdateType.Editor || updateType == InputUpdateType.BeforeRender)
                return;

            InputSystem.QueueStateEvent(m_Keyboard, m_KeyboardState);
            m_KeyboardStatePending = false;
            m_FlushesWaitingForKeyboardHandoff = 0;
        }

        private string DescribeUpdateMode()
        {
            var settings = InputSystem.settings;
            return settings == null ? "no InputSettings asset" : settings.updateMode.ToString();
        }

        // ---- Diagnostics -------------------------------------------------------------------

        private bool CheckInputSystem()
        {
            var settings = InputSystem.settings;
            if (settings == null)
            {
                Report("FAIL", "the Input System has no InputSettings asset, so the focus behaviour injection " +
                               "depends on cannot be configured; assign one under Project Settings > Input System Package");
                return false;
            }

            if (settings.updateMode == InputSettings.UpdateMode.ProcessEventsManually)
            {
                Report("FAIL", "InputSettings.updateMode is ProcessEventsManually, so the player loop runs no input " +
                               "update of its own. Injected keys are handed to the player loop's update on purpose — an " +
                               "edge-triggered read (wasPressedThisFrame) is only true during the update step that " +
                               "applied the event, and the flow driver's own update runs in PostLateUpdate, after the " +
                               "game has read input for the frame. With no automatic update there is no step left that " +
                               "the game observes, so every key press would be delivered where nothing is looking; set " +
                               "updateMode to ProcessEventsInDynamicUpdate under Project Settings > Input System Package");
                return false;
            }

            Report("ok", $"Input System {InputSystem.version} present " +
                         $"(updateMode={settings.updateMode}, backgroundBehavior={settings.backgroundBehavior}, " +
                         $"editorInputBehaviorInPlayMode={settings.editorInputBehaviorInPlayMode})");
            return true;
        }

        private bool CheckPlayMode()
        {
            if (!EditorApplication.isPlaying)
            {
                Report("FAIL", "the editor is not in play mode; EventSystem is not [ExecuteAlways], so no " +
                               "InputSystemUIInputModule.Process() runs and injected events reach nothing");
                return false;
            }

            if (EditorApplication.isPaused)
            {
                Report("FAIL", "play mode is paused; the player loop is stopped, so no frame will ever dispatch " +
                               "the injected events");
                return false;
            }

            Report("ok", "in play mode and running");
            return true;
        }

        /// <summary>
        /// Checks the ONE EventSystem that dispatches and the ONE module it runs, in that order. A scan is
        /// still used, but only to explain a failure precisely — never to grant availability.
        /// </summary>
        private bool CheckEventSystemPairing()
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                Report("FAIL", DescribeMissingCurrentEventSystem());
                return false;
            }

            if (!eventSystem.isActiveAndEnabled)
            {
                Report("FAIL", $"EventSystem.current is {Describe(eventSystem.gameObject)} but it is not active and " +
                               "enabled, so its Update() never runs and no injected event is ever dispatched");
                return false;
            }

            ReportCompetingEventSystems(eventSystem);

            var module = ResolveDispatchingModule(eventSystem, out var moduleFailure);
            if (module == null)
            {
                Report("FAIL", moduleFailure);
                return false;
            }

            Report("ok", $"EventSystem.current is {Describe(eventSystem.gameObject)} and is the dispatcher " +
                         "(EventSystem.Update() early-returns for every other instance)");

            return CheckModuleActions(module);
        }

        /// <summary>
        /// Returns the module that would actually turn the injected devices into UI events, or null with a
        /// precise <paramref name="failure"/>.
        ///
        /// Two distinct states, not a fallback chain. Once the EventSystem has run a frame it has ACTIVATED
        /// a module and <c>currentInputModule</c> is the answer — activation only ever picks from
        /// <c>GetComponents</c> on the EventSystem itself, so the type is the only thing left to verify.
        /// Before that first frame nothing is activated yet, and the only module that CAN be activated is one
        /// on the EventSystem's own GameObject, so that is what gets checked.
        /// </summary>
        private InputSystemUIInputModule ResolveDispatchingModule(EventSystem eventSystem, out string failure)
        {
            var activeModule = eventSystem.currentInputModule;
            if (activeModule != null)
            {
                if (activeModule is InputSystemUIInputModule activeUiModule)
                {
                    failure = null;
                    return activeUiModule;
                }

                failure = $"EventSystem.current {Describe(eventSystem.gameObject)} has activated " +
                          $"{activeModule.GetType().FullName} on {Describe(activeModule.gameObject)}; only an " +
                          "InputSystemUIInputModule can consume injected devices, so the flow would drive nothing";
                return null;
            }

            var pairedModule = eventSystem.GetComponent<InputSystemUIInputModule>();
            if (pairedModule == null)
            {
                failure = $"EventSystem.current {Describe(eventSystem.gameObject)} has no InputSystemUIInputModule on " +
                          "its own GameObject, and uGUI only ever runs modules found by GetComponents on the current " +
                          $"EventSystem{DescribeModulesElsewhere(eventSystem)}";
                return null;
            }

            if (!pairedModule.isActiveAndEnabled)
            {
                failure = $"the InputSystemUIInputModule on EventSystem.current {Describe(eventSystem.gameObject)} is " +
                          "not active and enabled, so EventSystem.UpdateModules() drops it and nothing dispatches";
                return null;
            }

            failure = null;
            return pairedModule;
        }

        private string DescribeMissingCurrentEventSystem()
        {
            var eventSystems = UnityEngine.Object.FindObjectsByType<EventSystem>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (eventSystems.Length == 0)
                return "EventSystem.current is null and no EventSystem exists in any loaded scene, so no UI event " +
                       "can be dispatched at all";

            return $"EventSystem.current is null although {eventSystems.Length} EventSystem(s) exist (first: " +
                   $"{Describe(eventSystems[0].gameObject)}); EventSystem.current is assigned from OnEnable, so every " +
                   "one of them is disabled";
        }

        /// <summary>
        /// A second active EventSystem is a real hazard — it looks healthy in the Inspector while its own
        /// Update() early-returns — but it does not make injection impossible, so it is a note, not a failure.
        /// </summary>
        private void ReportCompetingEventSystems(EventSystem dispatcher)
        {
            var eventSystems = UnityEngine.Object.FindObjectsByType<EventSystem>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (var i = 0; i < eventSystems.Length; i++)
            {
                var other = eventSystems[i];
                if (other == dispatcher)
                    continue;

                Report("note", $"a second active EventSystem exists on {Describe(other.gameObject)}; it dispatches " +
                               "nothing because EventSystem.current is elsewhere, so any module on it is inert");
                return;
            }
        }

        private string DescribeModulesElsewhere(EventSystem eventSystem)
        {
            var modules = UnityEngine.Object.FindObjectsByType<BaseInputModule>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (var i = 0; i < modules.Length; i++)
            {
                var module = modules[i];
                if (module.gameObject == eventSystem.gameObject)
                    continue;

                return $" (an unusable {module.GetType().Name} sits on {Describe(module.gameObject)} instead)";
            }

            return " (no input module exists in any loaded scene)";
        }

        private string Describe(GameObject gameObject)
        {
            return $"'{gameObject.name}' in scene '{gameObject.scene.name}'";
        }

        private bool CheckModuleActions(InputSystemUIInputModule module)
        {
            var assetName = module.actionsAsset == null ? "<none>" : module.actionsAsset.name;

            if (module.point == null || module.leftClick == null)
            {
                Report("FAIL", $"the dispatching InputSystemUIInputModule on {Describe(module.gameObject)} has no " +
                               $"{(module.point == null ? "point" : "leftClick")} action assigned " +
                               $"(actionsAsset={assetName}), so injected pointer input has nothing to bind to");
                return false;
            }

            Report("ok", $"the dispatching InputSystemUIInputModule is on {Describe(module.gameObject)} " +
                         $"(actionsAsset={assetName}, point={module.point.name}, leftClick={module.leftClick.name})");
            return true;
        }

        private void ReportFocusState()
        {
            if (Application.isFocused)
            {
                Report("ok", "the Game View has focus");
                return;
            }

            Report("note", "the Game View is unfocused; BeginSession() applies " +
                           "editorInputBehaviorInPlayMode=AllDeviceInputAlwaysGoesToGameView, " +
                           "backgroundBehavior=IgnoreFocus and Application.runInBackground=true, which is what " +
                           "keeps injected events from being discarded");
        }

        private void Report(string verdict, string message)
        {
            if (m_Diagnostics.Length > 0)
                m_Diagnostics.Append("; ");

            m_Diagnostics.Append('[').Append(verdict).Append("] ").Append(message);
        }

        // ---- Session and control resolution ------------------------------------------------

        private void EnsureSession()
        {
            if (m_Session == null)
                throw new InvalidOperationException(
                    "the inputsystem driver has no open session; call BeginSession() before injecting input");

            if (m_Session.IsDisposed)
                throw new InvalidOperationException(
                    "the inputsystem driver's session has ended (an assembly reload or a play mode exit disposes it); " +
                    "the devices it created no longer exist, so the run cannot continue");
        }

        private void RequirePointerPosition(string operation)
        {
            if (m_HasPointerPosition)
                return;

            throw new InvalidOperationException(
                $"{operation} was called before MovePointer, so the pointer has no position; a mouse state event " +
                "written without one places the pointer at (0, 0) and the press would land on the bottom-left " +
                "corner of the screen");
        }

        private void ResolveMouseButton(int button, out MouseButton mouseButton)
        {
            switch (button)
            {
                case LeftButtonIndex:
                    mouseButton = MouseButton.Left;
                    return;
                case RightButtonIndex:
                    mouseButton = MouseButton.Right;
                    return;
                case MiddleButtonIndex:
                    mouseButton = MouseButton.Middle;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(button), button,
                        "only 0 (left), 1 (right) and 2 (middle) are injectable mouse buttons");
            }
        }

        /// <summary>
        /// Caches every KeyControl name on the injected keyboard once per session. Resolving a key by
        /// walking control paths on every keystroke would allocate on the retry path, which runs each frame.
        /// </summary>
        private void BuildKeyTable()
        {
            m_KeyByControlName.Clear();

            var controls = m_Keyboard.allControls;
            for (var i = 0; i < controls.Count; i++)
            {
                if (controls[i] is KeyControl keyControl)
                    m_KeyByControlName[keyControl.name] = keyControl.keyCode;
            }
        }

        private Key ResolveKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("a key name cannot be null or empty", nameof(key));

            var controlName = Alias(key);

            if (!m_KeyByControlName.TryGetValue(controlName, out var keyCode))
                throw new ArgumentException(DescribeUnknownKey(key, controlName), nameof(key));

            return keyCode;
        }

        /// <summary>
        /// Translate the handful of names people actually write into the Keyboard layout's own
        /// control names.
        ///
        /// This is a rename, not a second key table: everything still resolves through
        /// <see cref="m_KeyByControlName"/>, which is built from the live device. The aliases exist
        /// because the layout's names are not the ones a flow author reaches for — the arrow keys are
        /// "upArrow", not "up", and in Input System 1.19 the digit row is named "1".."0" while the
        /// Key enum calls the same controls Digit1..Digit0, so both spellings turn up in the wild.
        ///
        /// A switch rather than a dictionary keeps this off the allocation ledger entirely and keeps
        /// the driver free of a static collection.
        /// </summary>
        private static string Alias(string key)
        {
            switch (key)
            {
                // Arrows: the four that navigation is actually driven with.
                case "up": case "Up": return "upArrow";
                case "down": case "Down": return "downArrow";
                case "left": case "Left": return "leftArrow";
                case "right": case "Right": return "rightArrow";

                // Names carried over from other input stacks and from everyday usage.
                case "esc": return "escape";
                case "return": return "enter";
                case "del": return "delete";
                case "ins": return "insert";
                case "pgup": return "pageUp";
                case "pgdn": case "pgdown": return "pageDown";

                // The Key enum's spelling of the digit row, which the Input Debugger shows as "1".."0".
                case "digit0": return "0";
                case "digit1": return "1";
                case "digit2": return "2";
                case "digit3": return "3";
                case "digit4": return "4";
                case "digit5": return "5";
                case "digit6": return "6";
                case "digit7": return "7";
                case "digit8": return "8";
                case "digit9": return "9";

                default: return key;
            }
        }

        /// <summary>
        /// Refuse an unknown key by NAME, with the closest real control names listed.
        ///
        /// Silently doing nothing is the failure mode this exists to prevent: a flow that presses
        /// "Enter" instead of "enter" would otherwise inject an empty keyboard state, the UI would
        /// not react, and the step would time out complaining about the UI. Runs only on the failure
        /// path, so the sort and the strings it allocates cost nothing in a passing run.
        /// </summary>
        private string DescribeUnknownKey(string key, string controlName)
        {
            var scored = new List<KeyValuePair<int, string>>(m_KeyByControlName.Count);
            foreach (var candidate in m_KeyByControlName.Keys)
                scored.Add(new KeyValuePair<int, string>(Score(controlName, candidate), candidate));

            scored.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : string.CompareOrdinal(a.Value, b.Value));

            var suggestions = new StringBuilder();
            for (var i = 0; i < scored.Count && i < 6; i++)
            {
                if (i > 0)
                    suggestions.Append(", ");

                suggestions.Append('"').Append(scored[i].Value).Append('"');
            }

            var aliased = string.Equals(key, controlName, StringComparison.Ordinal)
                ? string.Empty
                : $" (aliased to \"{controlName}\")";

            return $"'{key}'{aliased} is not a key on the Keyboard layout. Closest names: {suggestions}. " +
                   "Key names are the Input System control names shown by the Input Debugger under the keyboard " +
                   "device, plus the aliases up/down/left/right for the arrow keys; letters are \"a\"..\"z\", digits " +
                   "are \"0\"..\"9\", function keys are \"f1\"..\"f24\", and modifiers are sided (\"leftShift\", \"rightCtrl\").";
        }

        /// <summary>
        /// Rank one candidate name against what was asked for. Lower is closer.
        ///
        /// Containment outranks edit distance outright, because the names that get mistyped are the
        /// sided ones: "ctrl" is 4 edits from "leftCtrl" and only 3 from the letter "c", so pure
        /// Levenshtein confidently suggests "c" and never mentions either control the author meant.
        /// Among containing candidates the shortest wins, so "shift" suggests "leftShift" before
        /// "rightShift" only by name length and not by chance.
        /// </summary>
        private static int Score(string wanted, string candidate)
        {
            if (candidate.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                return int.MinValue + candidate.Length;

            return Distance(wanted, candidate);
        }

        /// <summary>
        /// Levenshtein distance, case-insensitive, for the suggestion list above. Written here rather
        /// than shared with the core's selector suggestions because that one is internal to another
        /// assembly, and one small failure-path helper is cheaper than widening its visibility.
        /// </summary>
        private static int Distance(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
            if (string.IsNullOrEmpty(b)) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
