using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Report;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Tests
{
    /// <summary>Severity values used only to exercise enum binding by name.</summary>
    public enum FlowTestSeverity
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// The object a scene-resolved command runs on.
    ///
    /// A MonoBehaviour is not a stylistic choice here: the binder's whole point is that a Component
    /// or GameObject parameter is resolved from the live scene, and that behaviour cannot be
    /// exercised by a plain class. Every command records what it was given on its own instance, so
    /// the suite needs no static state to observe a call.
    /// </summary>
    public sealed class FlowTestTarget : MonoBehaviour
    {
        public string LastMessage;
        public int LastCount;
        public bool LastVerbose;
        public FlowTestSeverity LastSeverity;
        public int LastNumber;
        public float LastRatio;
        public bool LastFlag;
        public int Touches;
        public string Progress = string.Empty;

        /// <summary>Completed by a test to prove the invoker waits for a Task instead of blocking on it.</summary>
        public readonly TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>();

        public void Ping(string message) => LastMessage = message;

        public void Configure(int count = 7, bool verbose = true, FlowTestSeverity severity = FlowTestSeverity.Medium)
        {
            LastCount = count;
            LastVerbose = verbose;
            LastSeverity = severity;
        }

        public void Coerce(int number, float ratio, bool flag, FlowTestSeverity severity)
        {
            LastNumber = number;
            LastRatio = ratio;
            LastFlag = flag;
            LastSeverity = severity;
        }

        public void Instant() => Progress += "done";

        public IEnumerator ThreeFrames()
        {
            Progress += "a";
            yield return null;
            Progress += "b";
            yield return null;
            Progress += "c";
        }

        public Task Awaited() => Gate.Task;

        public Task Faulted()
        {
            var completion = new TaskCompletionSource<bool>();
            completion.SetException(new InvalidOperationException("the command blew up"));
            return completion.Task;
        }
    }

    /// <summary>Static commands, so their parameters are bound rather than used as the target.</summary>
    public static class FlowTestCommands
    {
        public static void Touch(FlowTestTarget target) => target.Touches++;

        /// <summary>
        /// A GameObject parameter, which is the other half of scene resolution and takes a
        /// different route: there is no GameObject component to search for, so the binder searches
        /// Transforms and must hand back the OWNER, not the Transform it found.
        /// </summary>
        public static void TouchObject(GameObject subject) => subject.GetComponent<FlowTestTarget>().Touches++;
    }

    /// <summary>
    /// The argument binder: what a flow supplies, what C# defaults supply, and what the scene
    /// supplies. Scene resolution follows the same 0 / 1 / N-fails rule as selector resolution,
    /// because picking one of two players would make the flow pass while driving the wrong object.
    /// </summary>
    public sealed class FlowCommandInvokerTests : UGuiSceneFixture
    {
        private ConsoleRing m_Console;
        private StepContext m_Context;

        [SetUp]
        public void BuildContext()
        {
            // A canvas has to exist before the registry is built: the uGUI backend reports itself
            // unavailable without one, and StepContext.BuildDiagnostics enumerates through it.
            CreateCanvas("UnityFlowTestsInvokerCanvas");

            m_Console = new ConsoleRing();
            m_Context = new StepContext(
                Registry, Resolver, RunPaths.Existing("unityflow-tests"), m_Console, () => WriteMode.SemanticDispatch);

            FlowInternals.SetDeadline(m_Context, FlowClock.Now + 30.0);
        }

        [TearDown]
        public void DisposeContext()
        {
            // Null-safe because NUnit still runs teardown when setup threw, and an NRE here would
            // bury the real error under a second one.
            m_Console?.Dispose();
            m_Console = null;
            m_Context = null;
        }

        [Test]
        public void Bind_WithARequiredArgumentMissing_ThrowsNamingTheParameter()
        {
            FlowInternals.SetStep(m_Context, Step("ping"));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Ping))));

            StringAssert.Contains("'ping' requires parameter 'message'", exception.Message);
            StringAssert.Contains("String", exception.Message);
        }

        [Test]
        public void Bind_WithAnOptionalArgumentOmitted_UsesTheCSharpDefault()
        {
            FlowInternals.SetStep(m_Context, Step("configure"));

            var bound = FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Configure)));

            Assert.AreEqual(3, bound.Length);
            Assert.AreEqual(7, bound[0]);
            Assert.AreEqual(true, bound[1]);
            Assert.AreEqual(FlowTestSeverity.Medium, bound[2]);
        }

        [Test]
        public void Bind_CoercesIntFloatBoolAndEnumWrittenAsText()
        {
            FlowInternals.SetStep(m_Context, Step("coerce", new[]
            {
                Arg("number", FlowArgKind.Int, "42"),
                Arg("ratio", FlowArgKind.Float, "1.5"),
                Arg("flag", FlowArgKind.Bool, "true"),
                Arg("severity", FlowArgKind.Enum, "hIGh")
            }));

            var bound = FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Coerce)));

            Assert.AreEqual(42, bound[0]);
            Assert.AreEqual(1.5f, bound[1]);
            Assert.AreEqual(true, bound[2]);
            Assert.AreEqual(FlowTestSeverity.High, bound[3], "enum members must match case-insensitively");
        }

        [Test]
        public void Bind_WithAnUnknownEnumName_ListsTheValidValues()
        {
            FlowInternals.SetStep(m_Context, Step("coerce", new[]
            {
                Arg("number", FlowArgKind.Int, 1),
                Arg("ratio", FlowArgKind.Float, 1f),
                Arg("flag", FlowArgKind.Bool, true),
                Arg("severity", FlowArgKind.Enum, "Critical")
            }));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Coerce))));

            StringAssert.Contains("'Critical' is not a member of FlowTestSeverity", exception.Message);
            StringAssert.Contains("Low, Medium, High", exception.Message);
        }

        [Test]
        public void Bind_ComponentParameter_ResolvesTheOnlyInstanceInTheScene()
        {
            var target = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("touch"));

            var bound = FlowInternals.BindArguments(m_Context, TouchMethod());

            Assert.AreSame(target, bound[0]);
        }

        [Test]
        public void Bind_ComponentParameter_WithNoInstance_Fails()
        {
            FlowInternals.SetStep(m_Context, Step("touch"));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TouchMethod()));

            StringAssert.Contains("parameter 'target' of 'touch'", exception.Message);
            StringAssert.Contains("no FlowTestTarget exists in the loaded scenes", exception.Message);
        }

        [Test]
        public void Bind_ComponentParameter_WithSeveralInstances_FailsListingCandidates()
        {
            var first = CreateTarget("UnityFlowTestsTargetA");
            var second = CreateTarget("UnityFlowTestsTargetB");
            FlowInternals.SetStep(m_Context, Step("touch"));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TouchMethod()));

            StringAssert.Contains("2 objects have a FlowTestTarget", exception.Message);
            StringAssert.Contains("add 'on:'", exception.Message);
            StringAssert.Contains("/UnityFlowTestsTargetA", exception.Message);
            StringAssert.Contains("/UnityFlowTestsTargetB", exception.Message);

            Assert.AreEqual(0, first.Touches, "an ambiguous binding must not run the command at all");
            Assert.AreEqual(0, second.Touches);
        }

        [Test]
        public void Bind_ComponentParameter_WithAnOnSelector_ResolvesExactlyOne()
        {
            CreateTarget("UnityFlowTestsTargetA");
            var second = CreateTarget("UnityFlowTestsTargetB");
            FlowInternals.SetStep(m_Context, Step("touch", on: ByName("UnityFlowTestsTargetB")));

            var bound = FlowInternals.BindArguments(m_Context, TouchMethod());

            Assert.AreSame(second, bound[0]);
        }

        [Test]
        public void Bind_WithAnOnSelectorThatMatchesNothing_SaysSoRatherThanFallingBackToTheOnlyCandidate()
        {
            var only = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("touch", on: ByName("UnityFlowTestsTargetNobody")));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TouchMethod()));

            StringAssert.Contains("no FlowTestTarget matched 'on:", exception.Message);
            Assert.AreEqual(0, only.Touches, "an unmatched 'on:' must not quietly bind the object that does exist");
        }

        /// <summary>
        /// An <c>on:</c> the binder cannot honour must fail, not be ignored.
        ///
        /// Scene binding matches against a hierarchy path, so a selector written with testId, text
        /// or type has nothing to compare. Skipping it leaves a step that reads as targeted and
        /// behaves as untargeted: with one candidate present it binds that one and passes, and the
        /// day a second appears the same flow starts failing for a reason the author never wrote.
        /// </summary>
        [Test]
        public void Bind_WithAnOnSelectorItCannotHonour_RefusesInsteadOfIgnoringIt()
        {
            var only = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("touch", on: ByTestId("unityflow.tests.absent")));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TouchMethod()));

            StringAssert.Contains("cannot narrow a scene-object binding", exception.Message);
            StringAssert.Contains("name", exception.Message);
            StringAssert.Contains("path", exception.Message);
            Assert.AreEqual(0, only.Touches);
        }

        [Test]
        public void Bind_GameObjectParameter_WithAnOnSelector_BindsTheOwnerNotTheTransform()
        {
            var target = CreateTarget("UnityFlowTestsObjectA");
            CreateTarget("UnityFlowTestsObjectB");
            FlowInternals.SetStep(m_Context, Step("touchObject", on: ByName("UnityFlowTestsObjectA")));

            var bound = FlowInternals.BindArguments(m_Context, TouchObjectMethod());

            Assert.IsInstanceOf<GameObject>(bound[0],
                "the search is over Transforms, so returning what it found would bind a Transform to a GameObject parameter");
            Assert.AreSame(target.gameObject, bound[0]);
        }

        /// <summary>
        /// Every GameObject has a Transform, so an unqualified GameObject parameter matches the
        /// whole scene. Ambiguity is the only correct answer; picking one would run the command
        /// against an arbitrary object and report success.
        /// </summary>
        [Test]
        public void Bind_GameObjectParameter_WithNoOnSelector_IsAmbiguousAcrossTheWholeScene()
        {
            CreateTarget("UnityFlowTestsObjectA");
            FlowInternals.SetStep(m_Context, Step("touchObject"));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TouchObjectMethod()));

            StringAssert.Contains("objects have a GameObject", exception.Message);
            StringAssert.Contains("add 'on:'", exception.Message);
        }

        [Test]
        public void Bind_WithAValueThatDoesNotConvert_NamesBothTypesAndTheValue()
        {
            FlowInternals.SetStep(m_Context, Step("coerce", new[]
            {
                Arg("number", FlowArgKind.Int, "seven"),
                Arg("ratio", FlowArgKind.Float, 1f),
                Arg("flag", FlowArgKind.Bool, true),
                Arg("severity", FlowArgKind.Enum, "Low")
            }));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Coerce))));

            StringAssert.Contains("parameter 'number' expects Int32 but got String ('seven')", exception.Message);
        }

        [Test]
        public void Bind_WithNullForAValueType_SaysItCannotBeNullRatherThanBindingZero()
        {
            FlowInternals.SetStep(m_Context, Step("coerce", new[]
            {
                Arg("number", FlowArgKind.Int, null),
                Arg("ratio", FlowArgKind.Float, 1f),
                Arg("flag", FlowArgKind.Bool, true),
                Arg("severity", FlowArgKind.Enum, "Low")
            }));

            var exception = Assert.Throws<FlowBindException>(
                () => FlowInternals.BindArguments(m_Context, TargetMethod(nameof(FlowTestTarget.Coerce))));

            StringAssert.Contains("parameter 'number' is Int32 and cannot be null", exception.Message);
        }

        /// <summary>
        /// The 0 / 1 / N rule applies to the object a command RUNS ON, not only to its parameters.
        /// That target is resolved on a different code path, and it is the one that decides which
        /// object the flow actually drove.
        /// </summary>
        [Test]
        public void Invoke_InstanceCommand_WithNoTargetInTheScene_FailsNamingTheVerb()
        {
            FlowInternals.SetStep(m_Context, Step("instant"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Instant))));
            pump.RunToCompletion(16);

            Assert.IsNotNull(m_Context.Failure);
            StringAssert.Contains("cannot run 'instant'", m_Context.Failure.Summary);
            StringAssert.Contains("no FlowTestTarget exists in the loaded scenes", m_Context.Failure.Summary);
        }

        [Test]
        public void Invoke_InstanceCommand_WithTwoTargets_RunsOnNeither()
        {
            var first = CreateTarget("UnityFlowTestsTargetA");
            var second = CreateTarget("UnityFlowTestsTargetB");
            FlowInternals.SetStep(m_Context, Step("instant"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Instant))));
            pump.RunToCompletion(16);

            Assert.IsNotNull(m_Context.Failure);
            StringAssert.Contains("2 objects have a FlowTestTarget", m_Context.Failure.Summary);

            Assert.AreEqual(string.Empty, first.Progress, "an ambiguous target must not run the command on either object");
            Assert.AreEqual(string.Empty, second.Progress);
        }

        [Test]
        public void Invoke_VoidCommand_RunsOnTheResolvedSceneObject()
        {
            var target = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("instant"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Instant))));
            pump.RunToCompletion(16);

            Assert.IsNull(m_Context.Failure);
            Assert.AreEqual("done", target.Progress);
        }

        [Test]
        public void Invoke_EnumeratorCommand_AdvancesOneStagePerFrame()
        {
            var target = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("threeFrames"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.ThreeFrames))));

            Assert.IsFalse(pump.Frame());
            Assert.AreEqual("a", target.Progress, "an IEnumerator command must not be drained in a single frame");

            Assert.IsFalse(pump.Frame());
            Assert.AreEqual("ab", target.Progress);

            Assert.IsTrue(pump.Frame());
            Assert.AreEqual("abc", target.Progress);
            Assert.IsNull(m_Context.Failure);
        }

        [Test]
        public void Invoke_TaskCommand_KeepsYieldingUntilTheTaskCompletes()
        {
            var target = CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("awaited"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Awaited))));

            for (var frame = 0; frame < 5; frame++)
                Assert.IsFalse(pump.Frame(), "a pending Task must leave the step running, not complete it");

            Assert.IsNull(m_Context.Failure);

            target.Gate.SetResult(true);
            pump.RunToCompletion(16);

            Assert.IsNull(m_Context.Failure);
        }

        [Test]
        public void Invoke_FaultedTask_FailsTheStepWithTheTaskMessage()
        {
            CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("faulted"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Faulted))));
            pump.RunToCompletion(16);

            Assert.IsNotNull(m_Context.Failure);
            StringAssert.Contains("'faulted' failed: the command blew up", m_Context.Failure.Summary);
        }

        [Test]
        public void Invoke_WithAnUnbindableArgument_FailsTheStepInsteadOfThrowing()
        {
            CreateTarget("UnityFlowTestsTargetA");
            FlowInternals.SetStep(m_Context, Step("ping"));

            var pump = new FlowPump(FlowCommandInvoker.Invoke(m_Context, TargetMethod(nameof(FlowTestTarget.Ping))));
            pump.RunToCompletion(16);

            Assert.IsNotNull(m_Context.Failure);
            StringAssert.Contains("'ping' requires parameter 'message'", m_Context.Failure.Summary);
        }

        private FlowTestTarget CreateTarget(string name) =>
            new GameObject(name).AddComponent<FlowTestTarget>();

        private static MethodInfo TargetMethod(string name) =>
            typeof(FlowTestTarget).GetMethod(name)
            ?? throw new InvalidOperationException($"FlowTestTarget no longer declares '{name}'.");

        private static MethodInfo TouchMethod() =>
            typeof(FlowTestCommands).GetMethod(nameof(FlowTestCommands.Touch))
            ?? throw new InvalidOperationException("FlowTestCommands no longer declares 'Touch'.");

        private static MethodInfo TouchObjectMethod() =>
            typeof(FlowTestCommands).GetMethod(nameof(FlowTestCommands.TouchObject))
            ?? throw new InvalidOperationException("FlowTestCommands no longer declares 'TouchObject'.");

        private static FlowStep Step(string verb, FlowArgument[] args = null, Selector on = null) =>
            new FlowStep(verb, args, null, on, null, null, 1, 1);

        private static FlowArgument Arg(string name, FlowArgKind kind, object value) =>
            new FlowArgument(name, kind, value, null,
                FlowValue.OfScalar(value == null ? string.Empty : value.ToString(), false, 1, 1));
    }
}
