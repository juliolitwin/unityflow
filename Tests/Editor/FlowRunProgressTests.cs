using System;
using NUnit.Framework;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;
using UnityFlow.Editor.Window;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// The fold from a run's progress stream to the state of every step.
    ///
    /// The cases worth writing down are the ones where the stream does NOT simply say what
    /// happened. A step that triggers a domain reload never records an outcome, and neither does a
    /// step whose run is aborted from outside — so a reader that only believed step.pass and
    /// step.fail would leave a spinner turning forever on a step nothing will ever mention again.
    /// The rest is bookkeeping the window must not invent: the step list comes from the document,
    /// never from the stream, or nothing would be visible before it ran.
    /// </summary>
    public sealed class FlowRunProgressTests
    {
        private const string FlowPath = "unityflow-tests.flow.yaml";
        private const string SubFlowPath = "lib/unityflow-tests-sub.flow.yaml";

        private FlowRunProgress m_Progress;

        [SetUp]
        public void BuildProgress()
        {
            // before: enterPlayMode · steps: tapOn, then one step runFlow spliced in · after: exitPlayMode
            m_Progress = new FlowRunProgress(new FlowDocument(
                FlowPath, "t", null, null, null,
                new[] { Step("enterPlayMode", 4) },
                new[] { Step("tapOn", 8), Step("assertVisible", 3, SubFlowPath) },
                new[] { Step("exitPlayMode", 12) }));
        }

        [Test]
        public void EverySectionIsListedInSourceOrderBeforeAnythingRuns()
        {
            Assert.AreEqual(4, m_Progress.Steps.Count);
            Assert.AreEqual(
                new[] { "enterPlayMode", "tapOn", "assertVisible", "exitPlayMode" },
                new[] { m_Progress.Steps[0].Verb, m_Progress.Steps[1].Verb, m_Progress.Steps[2].Verb, m_Progress.Steps[3].Verb });

            Assert.AreEqual("before", m_Progress.Steps[0].Section);
            Assert.AreEqual("after", m_Progress.Steps[3].Section);

            foreach (var step in m_Progress.Steps)
                Assert.AreEqual(FlowStepState.Pending, step.State);
        }

        [Test]
        public void OnlyASplicedStepCarriesItsOwnFile()
        {
            Assert.IsNull(m_Progress.Steps[1].SourceFile);
            Assert.AreEqual(SubFlowPath, m_Progress.Steps[2].SourceFile);
            Assert.AreEqual(3, m_Progress.Steps[2].Line);
        }

        [Test]
        public void AStepRunsUntilItPasses()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"section\":\"steps\",\"index\":1,\"verb\":\"tapOn\"}");
            Assert.AreEqual(FlowStepState.Running, m_Progress.Steps[1].State);
            Assert.AreEqual(-1, m_Progress.Steps[1].ElapsedMs);

            m_Progress.Apply("{\"seq\":2,\"type\":\"step.pass\",\"index\":1,\"verb\":\"tapOn\",\"ms\":412}");
            Assert.AreEqual(FlowStepState.Passed, m_Progress.Steps[1].State);
            Assert.AreEqual(412, m_Progress.Steps[1].ElapsedMs);
        }

        [Test]
        public void AFailureKeepsItsDiagnosticsAndItsScreenshot()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":1,\"verb\":\"tapOn\"}");
            m_Progress.Apply(
                "{\"seq\":2,\"type\":\"step.fail\",\"index\":1,\"verb\":\"tapOn\",\"ms\":7000," +
                "\"summary\":\"tapOn btn_ok found nothing\",\"detail\":\"UI snapshot at failure:\"," +
                "\"nearMisses\":[\"btn_okay\"],\"screenshot\":\"/runs/r/artifacts/fail-01.png\"}");

            var step = m_Progress.Steps[1];
            Assert.AreEqual(FlowStepState.Failed, step.State);
            Assert.AreEqual("tapOn btn_ok found nothing", step.FailureSummary);
            Assert.AreEqual("/runs/r/artifacts/fail-01.png", step.Screenshot);

            // The near misses belong in the detail block: they are the answer to "then what DID it find".
            StringAssert.Contains("UI snapshot at failure:", step.FailureDetail);
            StringAssert.Contains("btn_okay", step.FailureDetail);

            Assert.AreSame(step, m_Progress.FailedStep);
        }

        [Test]
        public void OnlyTheFirstFailureIsTheDiagnosis()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":1}");
            m_Progress.Apply("{\"seq\":2,\"type\":\"step.fail\",\"index\":1,\"summary\":\"the real one\"}");
            m_Progress.Apply("{\"seq\":3,\"type\":\"step.start\",\"index\":3}");
            m_Progress.Apply("{\"seq\":4,\"type\":\"step.fail\",\"index\":3,\"summary\":\"teardown reacting to it\"}");

            Assert.AreEqual("the real one", m_Progress.FailedStep.FailureSummary);
        }

        [Test]
        public void TheHeaderSaysWhatAPassWouldBeWorth()
        {
            m_Progress.Apply(
                "{\"seq\":1,\"type\":\"run.start\",\"runId\":\"r1\",\"flow\":\"t\",\"path\":\"" + FlowPath + "\"," +
                "\"steps\":4,\"nextStep\":0,\"section\":\"before\",\"backends\":[\"ugui\"]," +
                "\"env\":[\"account=testgm\"],\"playMode\":true}");
            m_Progress.Apply(
                "{\"seq\":2,\"type\":\"run.writeMode\",\"writeMode\":\"DeviceInjection\"," +
                "\"occlusion\":\"CrossSurface\",\"inputDriver\":\"inputsystem\"}");

            Assert.AreEqual(RunState.Running, m_Progress.State);
            Assert.AreEqual("r1", m_Progress.RunId);
            Assert.AreEqual("DeviceInjection", m_Progress.WriteMode);
            Assert.AreEqual("CrossSurface", m_Progress.Occlusion);
            Assert.AreEqual("inputsystem", m_Progress.InputDriver);
            Assert.IsTrue(m_Progress.PlayMode);
            Assert.AreEqual(new[] { "account=testgm" }, m_Progress.Env);
            Assert.AreEqual(new[] { "ugui" }, m_Progress.Backends);
            Assert.IsEmpty(m_Progress.Warnings);
        }

        [Test]
        public void AFlowEditedAfterTheRunStartedIsReported()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"run.start\",\"path\":\"" + FlowPath + "\",\"steps\":9}");

            Assert.AreEqual(1, m_Progress.Warnings.Count);
            StringAssert.Contains("9 steps", m_Progress.Warnings[0]);
        }

        [Test]
        public void AWarningAboutFidelityIsKept()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"run.warning\",\"message\":\"occlusion fidelity is SurfaceLocal\"}");

            Assert.AreEqual(new[] { "occlusion fidelity is SurfaceLocal" }, m_Progress.Warnings);
        }

        [Test]
        public void TheStepThatTriggeredTheReloadIsAccountedForOnResume()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"run.start\",\"steps\":4,\"nextStep\":0}");
            m_Progress.Apply("{\"seq\":2,\"type\":\"step.start\",\"index\":0,\"verb\":\"enterPlayMode\"}");

            // The domain died here: enterPlayMode never wrote an outcome, and the resumed segment
            // starts at the step AFTER it.
            m_Progress.Apply("{\"seq\":3,\"type\":\"run.resume\",\"steps\":4,\"nextStep\":1,\"section\":\"steps\"}");

            Assert.IsTrue(m_Progress.Resumed);
            Assert.AreEqual(FlowStepState.Passed, m_Progress.Steps[0].State);
            Assert.AreEqual(FlowStepState.Pending, m_Progress.Steps[1].State);
        }

        [Test]
        public void NoStepIsLeftRunningOnceTheRunEnds()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":0,\"verb\":\"enterPlayMode\"}");
            m_Progress.Apply("{\"seq\":2,\"type\":\"run.end\",\"message\":\"cancelled by the host while the run was suspended\",\"state\":\"Cancelled\"}");

            Assert.AreEqual(RunState.Cancelled, m_Progress.State);
            Assert.IsTrue(m_Progress.IsTerminal);
            Assert.AreEqual(FlowStepState.Interrupted, m_Progress.Steps[0].State);

            // FlowResumer ends an abandoned run with the reason under 'message'; FlowRunner uses
            // 'failure'. Both are run.end, and a reader that knew only one would show no verdict.
            Assert.AreEqual("cancelled by the host while the run was suspended", m_Progress.FailureSummary);
        }

        [Test]
        public void APassingRunEndsWithNoVerdictToShow()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"run.end\",\"state\":\"Passed\",\"seconds\":12.5,\"failure\":null}");

            Assert.AreEqual(RunState.Passed, m_Progress.State);
            Assert.IsNull(m_Progress.FailureSummary);
            Assert.AreEqual(12.5, m_Progress.DurationSeconds, 0.001);
        }

        [Test]
        public void ARunThatDiedWithItsDomainStopsLookingLikeItIsStillGoing()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":1,\"verb\":\"tapOn\"}");
            m_Progress.Abandon();

            Assert.AreEqual(RunState.Errored, m_Progress.State);
            Assert.AreEqual(FlowStepState.Interrupted, m_Progress.Steps[1].State);
            StringAssert.Contains("flow.run", m_Progress.FailureSummary);

            Assert.Throws<InvalidOperationException>(() => m_Progress.Abandon());
        }

        [Test]
        public void WhatAStepDidWithoutInputIsAttachedToIt()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":1,\"verb\":\"navigateTo\"}");
            m_Progress.Apply(
                "{\"seq\":2,\"type\":\"step.assist\",\"verb\":\"navigateTo\"," +
                "\"mechanism\":\"EventSystem.SetSelectedGameObject\",\"message\":\"nothing was selected\"}");
            m_Progress.Apply("{\"seq\":3,\"type\":\"drag.attempt\",\"attempt\":2,\"outcome\":\"picked up\",\"confirmation\":\"dragState\"}");

            Assert.AreEqual(2, m_Progress.Steps[1].Notes.Count);
            StringAssert.Contains("EventSystem.SetSelectedGameObject", m_Progress.Steps[1].Notes[0]);
            StringAssert.Contains("attempt 2", m_Progress.Steps[1].Notes[1]);
        }

        [Test]
        public void ARecordThisWindowDoesNotRenderIsSkipped()
        {
            m_Progress.Apply("{\"seq\":1,\"type\":\"run.somethingAddedLater\",\"message\":\"hello\"}");

            Assert.AreEqual(1, m_Progress.RecordsApplied);
            Assert.AreEqual(RunState.Pending, m_Progress.State);
        }

        [Test]
        public void AStepIndexOutsideTheFlowIsRefused()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => m_Progress.Apply("{\"seq\":1,\"type\":\"step.start\",\"index\":9}"));

            StringAssert.Contains("step 9", error.Message);
            StringAssert.Contains("4 steps", error.Message);
        }

        [Test]
        public void AnUnknownRunStateIsRefused()
        {
            Assert.Throws<InvalidOperationException>(
                () => m_Progress.Apply("{\"seq\":1,\"type\":\"run.end\",\"state\":\"Melted\"}"));
        }

        private static FlowStep Step(string verb, int line, string sourcePath = FlowPath) =>
            new FlowStep(verb, null, null, null, null, null, line, 5, sourcePath);
    }
}
