using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Advances a flow one frame at a time.
    ///
    /// This exists because a flow CANNOT be run inside a [CliCommand] handler directly, which was
    /// measured rather than assumed: a synchronous MainThreadRequired handler occupies the main
    /// thread for its whole duration, and a probe that busy-waited 1.5 real seconds inside one
    /// observed Time.frameCount go from 12 to 12 — zero frames elapsed. Nothing that needs a frame
    /// to happen (a UI fading in, a click being processed, a scene loading) can ever complete
    /// there.
    ///
    /// So the command handler registers this driver and returns immediately, and the driver pumps
    /// the flow from the editor's own loop. Verified working: a callback registered on
    /// EditorApplication.update kept ticking between HTTP requests (165 -> 186 ticks across two
    /// polls), which is exactly the behaviour the design depends on.
    ///
    /// Two pump sources, because "a frame" means different things in each mode:
    /// <list type="bullet">
    /// <item>EDIT MODE — EditorApplication.update. There is no player loop.</item>
    /// <item>PLAY MODE — a PlayerLoop hook in PostLateUpdate. This matters for correctness, not
    /// tidiness: uGUI dispatches pointer events from InputSystemUIInputModule.Process(), which
    /// runs from EventSystem.Update() inside the player loop. Ticking from EditorApplication.update
    /// while in play mode would advance the flow at moments unrelated to input processing, so a
    /// press and its release could land in the same player frame and never produce a click.</item>
    /// </list>
    /// A PlayerLoop hook is also why this is not a MonoBehaviour: nothing is added to the scene,
    /// nothing survives into a build, and there is no object for the game to accidentally find.
    /// </summary>
    public sealed class FlowDriver : IDisposable
    {
        private readonly Stack<IEnumerator> m_Stack = new Stack<IEnumerator>();
        private readonly Action<Exception> m_OnCompleted;

        private FlowYield m_PendingYield;
        private bool m_Disposed;
        private bool m_Completed;
        private bool m_HookedEditorUpdate;
        private bool m_HookedPlayerLoop;

        /// <summary>Frames pumped since the run started. Reported in progress records.</summary>
        public int FrameCount { get; private set; }

        /// <summary>
        /// Marker type inserted into the PlayerLoop. A distinct type is what makes the hook
        /// findable and removable later — the loop is a value-type tree with no identity otherwise.
        /// </summary>
        private struct UnityFlowDriverLoop { }

        /// <param name="routine">The flow to advance.</param>
        /// <param name="onCompleted">
        /// Called once, on the main thread, with the terminating exception or null on success.
        /// This is what completes the TaskCompletionSource the CLI command returned.
        /// </param>
        public FlowDriver(IEnumerator routine, Action<Exception> onCompleted)
        {
            if (routine == null) throw new ArgumentNullException(nameof(routine));

            m_OnCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
            m_Stack.Push(routine);
        }

        /// <summary>Attach to whichever loop is correct for the current mode and begin pumping.</summary>
        public void Start()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(FlowDriver));

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            Attach();
        }

        private void Attach()
        {
            if (EditorApplication.isPlaying)
                AttachPlayerLoop();
            else
                AttachEditorUpdate();
        }

        private void AttachEditorUpdate()
        {
            DetachPlayerLoop();

            if (m_HookedEditorUpdate)
                return;

            EditorApplication.update += Tick;
            m_HookedEditorUpdate = true;
        }

        private void AttachPlayerLoop()
        {
            DetachEditorUpdate();

            if (m_HookedPlayerLoop)
                return;

            var loop = PlayerLoop.GetCurrentPlayerLoop();

            // PostLateUpdate runs after EventSystem.Update() and after animation/physics have
            // settled, so the flow observes the fully-resolved state produced by whatever it did
            // on the previous frame.
            if (!TryInsertInto(ref loop, typeof(PostLateUpdate), new PlayerLoopSystem
                {
                    type = typeof(UnityFlowDriverLoop),
                    updateDelegate = Tick
                }))
            {
                throw new InvalidOperationException(
                    "Could not insert the UnityFlow driver into the PlayerLoop: no PostLateUpdate phase was found. " +
                    "Another package has replaced the player loop with a custom tree.");
            }

            PlayerLoop.SetPlayerLoop(loop);
            m_HookedPlayerLoop = true;
        }

        private void DetachEditorUpdate()
        {
            if (!m_HookedEditorUpdate)
                return;

            EditorApplication.update -= Tick;
            m_HookedEditorUpdate = false;
        }

        private void DetachPlayerLoop()
        {
            if (!m_HookedPlayerLoop)
                return;

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (TryRemoveFrom(ref loop, typeof(UnityFlowDriverLoop)))
                PlayerLoop.SetPlayerLoop(loop);

            m_HookedPlayerLoop = false;
        }

        private static bool TryInsertInto(ref PlayerLoopSystem parent, Type phase, PlayerLoopSystem child)
        {
            if (parent.subSystemList == null)
                return false;

            for (var i = 0; i < parent.subSystemList.Length; i++)
            {
                if (parent.subSystemList[i].type == phase)
                {
                    var sub = parent.subSystemList[i].subSystemList ?? Array.Empty<PlayerLoopSystem>();
                    var extended = new PlayerLoopSystem[sub.Length + 1];
                    Array.Copy(sub, extended, sub.Length);
                    extended[sub.Length] = child;
                    parent.subSystemList[i].subSystemList = extended;
                    return true;
                }

                if (TryInsertInto(ref parent.subSystemList[i], phase, child))
                    return true;
            }

            return false;
        }

        private static bool TryRemoveFrom(ref PlayerLoopSystem parent, Type marker)
        {
            if (parent.subSystemList == null)
                return false;

            for (var i = 0; i < parent.subSystemList.Length; i++)
            {
                var sub = parent.subSystemList[i].subSystemList;
                if (sub != null)
                {
                    var index = Array.FindIndex(sub, s => s.type == marker);
                    if (index >= 0)
                    {
                        var trimmed = new PlayerLoopSystem[sub.Length - 1];
                        Array.Copy(sub, 0, trimmed, 0, index);
                        Array.Copy(sub, index + 1, trimmed, index, sub.Length - index - 1);
                        parent.subSystemList[i].subSystemList = trimmed;
                        return true;
                    }
                }

                if (TryRemoveFrom(ref parent.subSystemList[i], marker))
                    return true;
            }

            return false;
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Unity rebuilds the PlayerLoop across these transitions, so the hook must be
            // re-established rather than assumed to have survived.
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.EnteredEditMode)
            {
                m_HookedPlayerLoop = false;
                Attach();
            }
        }

        private void OnBeforeAssemblyReload()
        {
            // A domain reload destroys this object mid-run. Completing with an explicit exception
            // means the caller reports "interrupted by a domain reload" instead of hanging until
            // its deadline with no explanation.
            if (m_Completed)
                return;

            Complete(new FlowInterruptedException(
                "The run was interrupted by a domain reload (a script recompiled, a package changed, " +
                "or play mode was entered with Reload Domain enabled). Use flow.start for flows that " +
                "cross a domain reload."));
        }

        private void Tick()
        {
            if (m_Completed || m_Disposed)
                return;

            FrameCount++;

            try
            {
                if (m_PendingYield != null)
                {
                    if (!m_PendingYield.IsDone)
                        return;

                    m_PendingYield = null;
                }

                Advance();
            }
            catch (Exception ex)
            {
                Complete(ex);
            }
        }

        private void Advance()
        {
            while (m_Stack.Count > 0)
            {
                var current = m_Stack.Peek();

                if (!current.MoveNext())
                {
                    m_Stack.Pop();
                    continue;
                }

                switch (current.Current)
                {
                    case null:
                        // Plain "yield return null" means: resume next frame. This is the whole
                        // retry mechanism — it costs nothing and needs no round trip.
                        return;

                    case FlowYield instruction:
                        instruction.Begin();
                        if (!instruction.IsDone)
                        {
                            m_PendingYield = instruction;
                            return;
                        }
                        continue;

                    case IEnumerator nested:
                        m_Stack.Push(nested);
                        continue;

                    default:
                        throw new InvalidOperationException(
                            $"A flow step yielded an unsupported value of type '{current.Current.GetType().FullName}'. " +
                            "Yield null to wait a frame, a FlowYield to wait frames or seconds, or a nested IEnumerator.");
                }
            }

            Complete(null);
        }

        private void Complete(Exception error)
        {
            if (m_Completed)
                return;

            m_Completed = true;
            Detach();

            m_OnCompleted(error);
        }

        private void Detach()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            DetachEditorUpdate();
            DetachPlayerLoop();
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            m_Disposed = true;
            Detach();
        }
    }

    /// <summary>Thrown when a run cannot continue for an environmental reason rather than a test failure.</summary>
    public sealed class FlowInterruptedException : Exception
    {
        public FlowInterruptedException(string message) : base(message) { }
    }
}
