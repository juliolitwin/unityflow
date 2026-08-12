using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// The negative-assertion window.
    ///
    /// A positive assertion retries until it becomes true, so it can only pass for a real reason. A
    /// negative one is the opposite: if it returned the moment it looked true it would pass BEFORE
    /// the system under test had a chance to be wrong, and a popup that appears 300ms late would
    /// still be reported green. These tests therefore assert on elapsed time, because a vacuous
    /// pass is indistinguishable from a real one by outcome alone.
    /// </summary>
    public sealed class StableForSemanticsTests : UGuiSceneFixture
    {
        private const string CanvasName = "UnityFlowStableCanvas";
        private const string PopupName = "UnityFlowTestsPopup";
        private const string AbsentName = "UnityFlowTestsNeverExists";

        /// <summary>Wall-clock ceiling for one pumped run, so a step that never terminates fails instead of hanging the editor.</summary>
        private static readonly TimeSpan PumpCeiling = TimeSpan.FromSeconds(30);

        private Canvas m_Canvas;
        private GameObject m_Popup;

        [SetUp]
        public void BuildCanvas()
        {
            m_Canvas = CreateCanvas(CanvasName);
            m_Popup = CreateElement(PopupName, m_Canvas.transform, new Vector2(160f, 80f)).gameObject;
            m_Popup.SetActive(false);
        }

        [Test]
        public void DefaultWindowIsFiveHundredMilliseconds()
        {
            Assert.AreEqual(500.0, StepLibrary.DefaultStableFor.TotalMilliseconds);
        }

        [Test]
        public void AbsentNode_PassesOnlyAfterTheWindowHasElapsed()
        {
            var outcome = RunFlow(AssertNotVisible(AbsentName, TimeSpan.FromMilliseconds(400)));

            Assert.AreEqual(nameof(RunState.Passed), outcome.Result.State, outcome.Result.Failure);
            Assert.GreaterOrEqual(outcome.ElapsedMs, 400.0,
                "the step returned before its window elapsed, so it never gave the popup a chance to appear");
            Assert.Greater(outcome.Frames, 1, "a single-frame pass is exactly the vacuous result this window prevents");
        }

        [Test]
        public void AbsentNode_WithNoStableForDeclared_HoldsTheDefaultWindow()
        {
            var outcome = RunFlow(AssertNotVisible(AbsentName, null));

            Assert.AreEqual(nameof(RunState.Passed), outcome.Result.State, outcome.Result.Failure);
            Assert.GreaterOrEqual(outcome.ElapsedMs, StepLibrary.DefaultStableFor.TotalMilliseconds);
        }

        /// <summary>
        /// The window is what costs the time, not the harness.
        ///
        /// "Elapsed &gt;= the window" is the right assertion but it has one blind spot: it is also
        /// satisfied if the step returns instantly and the surrounding machinery — building a run
        /// directory, opening a progress stream, writing a result — happens to be slower than the
        /// window. Two runs that differ ONLY in their declared window cancel that overhead out, so
        /// the difference between them can come from nothing except the wait itself.
        /// </summary>
        [Test]
        public void TheWindowItselfIsWhatCostsTheTime_NotTheRunnersOverhead()
        {
            var narrow = TimeSpan.FromMilliseconds(200);
            var wide = TimeSpan.FromMilliseconds(1200);

            var quick = RunFlow(AssertNotVisible(AbsentName, narrow));
            var slow = RunFlow(AssertNotVisible(AbsentName, wide));

            Assert.AreEqual(nameof(RunState.Passed), quick.Result.State, quick.Result.Failure);
            Assert.AreEqual(nameof(RunState.Passed), slow.Result.State, slow.Result.Failure);

            var declaredDifference = (wide - narrow).TotalMilliseconds;
            Assert.GreaterOrEqual(slow.ElapsedMs - quick.ElapsedMs, declaredDifference * 0.75,
                $"declaring a window {declaredDifference:0}ms longer barely changed how long the step took, " +
                "so the window is not being held and the pass is vacuous");
        }

        [Test]
        public void NodeThatAppearsMidWindow_FailsAndReportsWhenItAppeared()
        {
            const double appearAtMs = 200.0;
            var window = TimeSpan.FromMilliseconds(1000);

            var outcome = RunFlow(AssertNotVisible(PopupName, window), appearAtMs, () => m_Popup.SetActive(true));

            Assert.AreEqual(nameof(RunState.Failed), outcome.Result.State);
            Assert.Less(outcome.ElapsedMs, window.TotalMilliseconds,
                "a violated negative assertion must fail the instant it is violated, not wait out its window");

            var failure = outcome.Result.Failure;
            StringAssert.Contains($"/{CanvasName}/{PopupName}", failure);
            StringAssert.Contains("stay absent for 1000ms", failure);

            var reported = Regex.Match(failure, @"became visible after (\d+)ms");
            Assert.IsTrue(reported.Success, $"the failure must say WHEN the node appeared, got: {failure}");

            var reportedMs = double.Parse(reported.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.GreaterOrEqual(reportedMs, appearAtMs * 0.5);
            Assert.LessOrEqual(reportedMs, appearAtMs * 3.0);
        }

        private static FlowDocument AssertNotVisible(string nodeName, TimeSpan? stableFor)
        {
            var args = new List<FlowArgument>();
            if (stableFor.HasValue)
            {
                args.Add(new FlowArgument(
                    "stableFor", FlowArgKind.Duration, stableFor.Value, null,
                    FlowValue.OfScalar(stableFor.Value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms", false, 3, 5)));
            }

            var step = new FlowStep("assertNotVisible", args, ByName(nodeName), null, null, null, 3, 5);

            return new FlowDocument(
                "unityflow-tests.flow.yaml", "stableFor semantics", FlowRequirements.Unspecified,
                null, null, null, new[] { step }, null);
        }

        private RunOutcome RunFlow(FlowDocument document, double actAtMs = 0.0, Action act = null)
        {
            var paths = RunPaths.CreateFor("unityflow-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            var runner = new FlowRunner(document, new FlowVocabulary(), paths, TimeSpan.FromSeconds(30), false);
            var pump = new FlowPump(runner.Run());
            var watch = Stopwatch.StartNew();
            var acted = false;

            pump.RunToCompletion(int.MaxValue, () =>
            {
                if (watch.Elapsed > PumpCeiling)
                    throw new InvalidOperationException($"the run did not finish within {PumpCeiling.TotalSeconds:0}s");

                if (act != null && !acted && watch.Elapsed.TotalMilliseconds >= actAtMs)
                {
                    act();
                    acted = true;
                }
            });

            watch.Stop();
            Assert.IsNotNull(runner.Result, "the runner finished without producing a result");

            // Deliberately not in a finally: an abandoned run still owns its open progress stream,
            // so deleting the folder there would throw and hide whatever really went wrong. A run
            // that did not finish keeps its directory, which is where the diagnosis lives anyway.
            Directory.Delete(paths.RunDirectory, true);

            return new RunOutcome(runner.Result, watch.Elapsed.TotalMilliseconds, pump.Frames);
        }

        private readonly struct RunOutcome
        {
            public readonly FlowRunResult Result;
            public readonly double ElapsedMs;
            public readonly int Frames;

            public RunOutcome(FlowRunResult result, double elapsedMs, int frames)
            {
                Result = result;
                ElapsedMs = elapsedMs;
                Frames = frames;
            }
        }
    }
}
