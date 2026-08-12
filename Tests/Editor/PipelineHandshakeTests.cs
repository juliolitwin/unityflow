using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Pipeline.Commands;
using UnityEngine.TestTools;
using UnityFlow.Editor.Commands;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// The handshake the whole tool stands on, pinned so it cannot regress quietly.
    ///
    /// A synchronous <c>MainThreadRequired</c> handler owns the main thread for its entire
    /// duration: a probe that busy-waited 1.5 real seconds inside one saw Time.frameCount go from
    /// 12 to 12, so nothing that needs a frame can ever happen there. <c>flow.run</c> therefore
    /// returns a PENDING <c>Task&lt;T&gt;</c> from a NON-async method: the handler registers the
    /// frame driver and returns in the same tick, the server awaits the task off the main thread,
    /// and the flow advances frame by frame while the request is still open.
    ///
    /// If any part of that shape breaks — the method becomes async, or starts blocking — flows stop
    /// working with no error message at all, because the handler simply never yields the main
    /// thread back and the server's 60s Dispatcher.Invoke ceiling kills it.
    /// </summary>
    public sealed class PipelineHandshakeTests : UGuiSceneFixture
    {
        /// <summary>
        /// Ceiling for the SYNCHRONOUS part of the handler. The pipeline server abandons a
        /// MainThreadRequired invocation after 60 seconds, so the useful assertion is not "under
        /// 60s" but "nowhere near it": the handler only parses a file and registers a driver.
        /// </summary>
        private const double HandlerBudgetSeconds = 5.0;

        /// <summary>
        /// How long the test holds the main thread while the run is pending. Must comfortably
        /// exceed the flow's own <c>wait: 300ms</c>, so that a run driven by anything OTHER than the
        /// editor loop would have finished by the time the hold ends.
        /// </summary>
        private static readonly TimeSpan MainThreadHold = TimeSpan.FromMilliseconds(1500);

        private static readonly TimeSpan RunCeiling = TimeSpan.FromSeconds(60);

        [Test]
        public void FlowRun_IsDeclaredMainThreadRequired()
        {
            var attribute = RunMethod().GetCustomAttribute<CliCommandAttribute>();

            Assert.IsNotNull(attribute, "flow.run must stay a [CliCommand] or the CLI cannot reach it");
            Assert.AreEqual("flow.run", attribute.Name);
            Assert.IsTrue(attribute.MainThreadRequired,
                "the driver touches the scene and the PlayerLoop, so it must be invoked on the main thread");
        }

        [Test]
        public void FlowRun_IsNotAnAsyncMethod_SoItCanReturnBeforeTheFlowHasRun()
        {
            var method = RunMethod();

            Assert.AreEqual(typeof(Task<FlowRunResult>), method.ReturnType);
            Assert.IsNull(method.GetCustomAttribute<AsyncStateMachineAttribute>(),
                "an async handler would resume on the main thread and re-occupy it; the task must be completed by the frame driver instead");
        }

        /// <summary>
        /// THIS TEST'S CENTRAL ASSERTION REPLACES A VACUOUS ONE. It used to count
        /// EditorApplication.update callbacks and require that at least three elapsed between the
        /// handler returning and the task completing — but the test's own <c>yield return null</c>
        /// polling loop is what makes those ticks happen, so the counter climbed no matter what
        /// completed the task. Reintroducing the exact defect it was named after (complete the task
        /// from a thread-pool thread, <c>Task.Run(() =&gt; ...)</c>) left it green.
        ///
        /// Counting ticks cannot separate "driven by the editor loop" from "happened while the
        /// editor looped". Denying the editor loop can: the test holds the main thread for
        /// <see cref="MainThreadHold"/>, five times the flow's own wait. Nothing that needs a frame
        /// can progress during the hold, so a task that is complete when the hold ends was
        /// completed by something that never needed the loop at all — which is precisely the
        /// implementation this handshake forbids.
        /// </summary>
        [UnityTest]
        public IEnumerator FlowRun_ReturnsATaskThatOnlyTheEditorLoopCanComplete()
        {
            CreateCanvas("UnityFlowTestsHandshakeCanvas");

            var runId = "unityflow-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var flowPath = WriteFlow(runId);
            var paths = RunPaths.Existing(runId);

            var handlerWatch = Stopwatch.StartNew();
            var task = FlowCommands.Run(flowPath, runId, 30000);
            handlerWatch.Stop();

            Assert.Less(handlerWatch.Elapsed.TotalSeconds, HandlerBudgetSeconds,
                "the handler blocked the main thread; while it does that no frame can elapse and no flow can progress");

            if (task.IsCompleted)
            {
                Assert.Fail("flow.run completed synchronously, so nothing could ever happen between frames: " +
                            $"{task.Result.State} {task.Result.Failure}");
            }

            // Sleeping on the main thread is the measurement, not a delay: EditorApplication.update
            // and the PlayerLoop both run on this thread, so neither can tick until it returns.
            Thread.Sleep(MainThreadHold);

            Assert.IsFalse(task.IsCompleted,
                $"the run finished while the main thread was held for {MainThreadHold.TotalMilliseconds:0}ms, " +
                "so it was never driven by the editor loop; a flow implemented that way cannot observe a frame, " +
                "which is the one thing the frame driver exists to provide");

            var runWatch = Stopwatch.StartNew();

            while (!task.IsCompleted)
            {
                if (runWatch.Elapsed > RunCeiling)
                    Assert.Fail($"the run did not finish within {RunCeiling.TotalSeconds:0}s of editor ticks");

                yield return null;
            }

            var result = task.Result;
            Assert.AreEqual(nameof(RunState.Passed), result.State, result.Failure);
            Assert.AreEqual(runId, result.RunId);

            // Only reached once the run has ended and released its progress stream. A run that
            // never finished keeps its folder, which is exactly what a reader needs to see.
            File.Delete(flowPath);
            Directory.Delete(paths.RunDirectory, true);
        }

        /// <summary>
        /// A flow whose only step needs real time to pass, so completing it is proof that frames
        /// elapsed rather than proof that the handler ran it inline.
        /// </summary>
        private static string WriteFlow(string runId)
        {
            var directory = Path.Combine(RunPaths.ProjectRoot, "Temp");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, runId + ".flow.yaml");
            File.WriteAllText(path,
                "name: unityflow handshake\n" +
                "steps:\n" +
                "  - wait: 300ms\n");

            return path;
        }

        private static MethodInfo RunMethod() =>
            typeof(FlowCommands).GetMethod(nameof(FlowCommands.Run))
            ?? throw new InvalidOperationException("FlowCommands no longer declares Run; the flow.run handshake is gone.");
    }
}
