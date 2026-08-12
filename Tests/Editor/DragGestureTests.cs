using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Report;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// What separates a drag from a tap, asserted rather than assumed.
    ///
    /// A press at one place and a release at another IS NOT A DRAG. uGUI only starts one once the
    /// pointer has travelled past <c>EventSystem.pixelDragThreshold</c> while a button is held, and
    /// it observes that from <c>InputSystemUIInputModule.Process()</c> — once per frame, only on
    /// frames where the pointer moved. Without motion under the held button no IBeginDragHandler,
    /// IDragHandler, IEndDragHandler or IDropHandler ever runs, and a real drag-and-drop UI depends
    /// on all of them: the inventory grid this was measured against starts an item drag from OnDrag
    /// after 50px of travel, and its drop zone's IDropHandler is reached only by a release that
    /// happens while the pointer is dragging.
    ///
    /// So the gesture's SHAPE is the contract, and these tests lock it: one move to the source, a
    /// press, several moves on separate frames that cross the threshold, and a release only after
    /// them. The handler-level consequence cannot be observed in edit mode — EventSystem is not
    /// [ExecuteAlways], so no input module runs at all — which is exactly why the verb refuses to
    /// pretend it can drag without device injection, and why that refusal is tested here too.
    /// </summary>
    public sealed class DragGestureTests : UGuiSceneFixture
    {
        private const string CanvasName = "UnityFlowTestsDragCanvas";
        private const string SourceName = "UnityFlowTestsDragSource";
        private const string TargetName = "UnityFlowTestsDragTarget";
        private const string NearName = "UnityFlowTestsDragNear";
        private const string BlockerName = "UnityFlowTestsDragBlocker";

        /// <summary>Hard stop for a pumped step, so a verb that never finishes fails instead of hanging the editor.</summary>
        private const int MaxPumpedFrames = 400;

        /// <summary>
        /// Hard stop for the one gesture that waits on the WALL CLOCK. The pump spins as fast as the
        /// editor can settle a canvas rather than at 60Hz, so a 40ms hold is hundreds of frames on a
        /// slow machine and thousands on a fast one — the wall clock is what bounds it, and this only
        /// has to be too large to be reached by a working hold.
        /// </summary>
        private const int MaxTimedHoldFrames = 40000;

        /// <summary>
        /// How far apart the two endpoints sit, in canvas units. Comfortably past any plausible
        /// pixelDragThreshold, and small enough to stay inside a modest Game View — an element the
        /// surface clips to nothing has no injection point at all.
        /// </summary>
        private const float EndpointOffset = 150f;

        private Canvas m_Canvas;
        private RectTransform m_Source;
        private RectTransform m_Target;
        private ConsoleRing m_Console;
        private EventSystem m_EventSystem;
        private RecordingInputDriver m_Driver;

        [SetUp]
        public void BuildDragScene()
        {
            m_Canvas = CreateCanvas(CanvasName);

            m_Source = CreateElement(SourceName, m_Canvas.transform, new Vector2(80f, 80f), new Vector2(-EndpointOffset, 0f));
            m_Target = CreateElement(TargetName, m_Canvas.transform, new Vector2(80f, 80f), new Vector2(EndpointOffset, 0f));

            m_Console = new ConsoleRing();
            m_Driver = null;
        }

        [TearDown]
        public void ReleaseDragScene()
        {
            UnregisterEventSystem();
            m_Console?.Dispose();
            m_Console = null;
        }

        // ---- endpoint resolution -----------------------------------------------------------

        [Test]
        public void Endpoint_ResolvesToTheSameInjectionPointATapWouldUse()
        {
            var context = NewContext(WriteMode.DeviceInjection, SelectorArg("from", SourceName), SelectorArg("to", TargetName));

            var source = ResolveEndpoint(context, "from", "fromPoint");
            var target = ResolveEndpoint(context, "to", "toPoint");

            var sourceNode = NodeNamed(SourceName);
            var targetNode = NodeNamed(TargetName);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(sourceNode.Handle, out var expectedSource, out var sourceReason), sourceReason);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(targetNode.Handle, out var expectedTarget, out var targetReason), targetReason);

            Assert.AreEqual(expectedSource, FlowInternals.DragEndpointField<Vector2>(source, "Point"),
                "a drag endpoint must land where a tap on the same node would; anything else is a second, weaker targeting rule");
            Assert.AreEqual(expectedTarget, FlowInternals.DragEndpointField<Vector2>(target, "Point"));

            StringAssert.Contains(SourceName, FlowInternals.DragEndpointField<string>(source, "Path"));
            StringAssert.Contains(TargetName, FlowInternals.DragEndpointField<string>(target, "Path"));
        }

        /// <summary>
        /// The failure the requirement is written around: a source under something else must be
        /// refused, and the refusal must NAME what is covering it and which end of the drag it was.
        /// This mirrors tapOn exactly — an element none of whose probe positions can be reached is
        /// final there too, because retrying it would hide the blocker behind a timeout instead of
        /// reporting it.
        /// </summary>
        [Test]
        public void Endpoint_WithAnOccludedSource_ReportsWhatIsCoveringItAndWhichEndItWas()
        {
            // A later sibling on the same canvas draws on top, which is how a modal covers a control
            // the user can still see.
            CreateElement(BlockerName, m_Canvas.transform, new Vector2(4000f, 4000f));

            var context = NewContext(WriteMode.DeviceInjection, SelectorArg("from", SourceName), SelectorArg("to", TargetName));
            var source = FlowInternals.ReadDragEndpoint(context, "from", "fromPoint");
            Assert.IsNotNull(source, context.Failure?.Summary);

            var resolved = false;
            var fatal = false;

            for (var frame = 0; frame < 8 && !resolved && !fatal; frame++)
            {
                Settle();
                resolved = FlowInternals.AdvanceDragEndpoint(context, source, out fatal);
            }

            Assert.IsFalse(resolved, "a source that is completely covered must never be dragged from");

            var reason = FlowInternals.DragEndpointField<string>(source, "Reason");
            StringAssert.Contains("obscured by", reason);
            StringAssert.Contains(BlockerName, reason);
            StringAssert.Contains("'from'", reason, "the failure has to say WHICH end of the drag could not be resolved");
        }

        [Test]
        public void Endpoint_WrittenAsARawPoint_NeedsNoNodeAtAll()
        {
            var context = NewContext(WriteMode.DeviceInjection, PointArg("fromPoint", new Vector2(11f, 22f)), SelectorArg("to", TargetName));

            var source = FlowInternals.ReadDragEndpoint(context, "from", "fromPoint");
            Assert.IsNotNull(source, context.Failure?.Summary);

            Assert.IsTrue(FlowInternals.DragEndpointField<bool>(source, "Resolved"));
            Assert.AreEqual(new Vector2(11f, 22f), FlowInternals.DragEndpointField<Vector2>(source, "Point"));
        }

        [Test]
        public void Endpoint_GivenBothASelectorAndAPoint_IsRefusedRatherThanPreferringOne()
        {
            var context = NewContext(WriteMode.DeviceInjection, SelectorArg("from", SourceName), PointArg("fromPoint", Vector2.zero));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(context, "from", "fromPoint"));
            StringAssert.Contains("both 'from' and 'fromPoint'", context.Failure.Summary);
        }

        [Test]
        public void Endpoint_GivenNeither_NamesWhatIsMissing()
        {
            var context = NewContext(WriteMode.DeviceInjection, SelectorArg("to", TargetName));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(context, "from", "fromPoint"));
            StringAssert.Contains("drag needs 'from'", context.Failure.Summary);
        }

        /// <summary>
        /// A coordinate an earlier step computed, which is the only way a flow can aim at geometry
        /// that does not exist until the game is running.
        ///
        /// The alternative is a literal, and a literal is a measurement of one Game View size baked
        /// into the file: resize the view by two pixels and every drag in the flow lands on a
        /// different element and fails for a reason that is not the game's. So the value travels
        /// from a runScript's 'as:' to the verb, unconverted, and is compared here EXACTLY — a
        /// coordinate that arrived rounded or truncated would aim at a neighbouring cell.
        /// </summary>
        [Test]
        public void Endpoint_WrittenAsAReferenceToABoundPoint_UsesTheValueTheEarlierStepComputed()
        {
            var computed = new Vector2(613.5f, 271.25f);
            Resolver.BindValue("dropCorner", computed);

            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "dropCorner"), SelectorArg("to", TargetName));

            var source = FlowInternals.ReadDragEndpoint(context, "from", "fromPoint");
            Assert.IsNotNull(source, context.Failure?.Summary);

            Assert.IsTrue(FlowInternals.DragEndpointField<bool>(source, "Resolved"),
                "a computed coordinate names no element, so there is nothing left to wait for");
            Assert.AreEqual(computed, FlowInternals.DragEndpointField<Vector2>(source, "Point"));
        }

        [Test]
        public void Endpoint_ReferencingANameNothingBound_SaysSoInsteadOfDraggingFromTheOrigin()
        {
            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "neverBound"), SelectorArg("to", TargetName));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(context, "from", "fromPoint"));
            StringAssert.Contains("@neverBound", context.Failure.Summary);
            StringAssert.Contains("no earlier step bound a VALUE", context.Failure.Summary);
        }

        /// <summary>
        /// The form a runScript can actually produce.
        ///
        /// The pipeline's evaluator JSON round-trips every value a script returns, and a Vector2
        /// does not survive it — it arrives as Unity's ToString, rounded to two decimals and
        /// punctuated by the editor's culture, which on a pt-BR editor writes "875,40". So the wire
        /// format between a script and a drag is "x,y" in INVARIANT culture, and it is parsed
        /// exactly: the assertion below is on the unrounded value, because a coordinate that
        /// arrived rounded to the nearest hundredth of a pixel is a different aim than the one the
        /// script measured.
        /// </summary>
        [Test]
        public void Endpoint_ReferencingAPointWrittenAsInvariantText_ParsesItExactly()
        {
            Resolver.BindValue("dropCorner", "613.5,271.25");

            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "dropCorner"), SelectorArg("to", TargetName));

            var source = FlowInternals.ReadDragEndpoint(context, "from", "fromPoint");
            Assert.IsNotNull(source, context.Failure?.Summary);
            Assert.AreEqual(new Vector2(613.5f, 271.25f), FlowInternals.DragEndpointField<Vector2>(source, "Point"));
        }

        /// <summary>
        /// The mistake this WILL be made with: returning the Vector2 and letting the round trip
        /// stringify it. The value looks right and is neither exact nor culture-stable, so it is
        /// refused — and the refusal has to quote what arrived and name the fix, or the author sees
        /// a coordinate in the message and concludes the verb is broken.
        /// </summary>
        [Test]
        public void Endpoint_ReferencingAStringifiedVector_IsRefusedAndSaysHowToWriteIt()
        {
            Resolver.BindValue("dropCorner", "(875.00, 212.00)");

            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "dropCorner"), SelectorArg("to", TargetName));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(context, "from", "fromPoint"));
            StringAssert.Contains("(875.00, 212.00)", context.Failure.Summary);
            StringAssert.Contains("InvariantCulture", context.Failure.Summary);
        }

        /// <summary>
        /// Coercion is refused on purpose: a Vector3, a float pair or a comma-decimal string all
        /// LOOK like a point, and picking one interpretation would send a real gesture to a place
        /// nobody wrote down. Naming the type that arrived is the fix the author needs.
        /// </summary>
        [Test]
        public void Endpoint_ReferencingSomethingThatIsNotAPoint_IsRefusedRatherThanCoerced()
        {
            Resolver.BindValue("notAPoint", new Vector3(1f, 2f, 3f));

            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "notAPoint"), SelectorArg("to", TargetName));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(context, "from", "fromPoint"));
            StringAssert.Contains("UnityEngine.Vector3", context.Failure.Summary);
        }

        /// <summary>
        /// The comma is ALWAYS the separator and never a decimal point, and the numbers are always
        /// invariant. Locked because the tempting "fix" for a pt-BR editor — accepting the local
        /// culture — makes "875,4" mean both (875, 4) and the single number 875.4, and the verb
        /// would then aim three quarters of the way up a 601px screen instead of a fifth.
        /// </summary>
        [Test]
        public void Endpoint_ParsesTheWireFormatAsTwoInvariantNumbers_NeverAsTheEditorsCulture()
        {
            Resolver.BindValue("dropCorner", "875,4");

            var context = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "dropCorner"), SelectorArg("to", TargetName));

            var source = FlowInternals.ReadDragEndpoint(context, "from", "fromPoint");
            Assert.IsNotNull(source, context.Failure?.Summary);
            Assert.AreEqual(new Vector2(875f, 4f), FlowInternals.DragEndpointField<Vector2>(source, "Point"));

            // And a third number is a refusal, not "take the first two".
            Resolver.BindValue("threeNumbers", "875.4,212.1,0.0");
            var second = NewContext(WriteMode.DeviceInjection, ReferenceArg("fromPoint", "threeNumbers"), SelectorArg("to", TargetName));

            Assert.IsNull(FlowInternals.ReadDragEndpoint(second, "from", "fromPoint"));
            StringAssert.Contains("875.4,212.1,0.0", second.Failure.Summary);
        }

        // ---- the gesture ---------------------------------------------------------------------

        /// <summary>
        /// The whole verb, driven through a recording device.
        ///
        /// It asserts the ORDER and the GEOMETRY together, because either alone is satisfiable by a
        /// gesture that is not a drag: press-move-release in the right order but with two pixels of
        /// travel raises nothing, and a thousand pixels of travel emitted before the press raises
        /// nothing either.
        /// </summary>
        [Test]
        public void Gesture_MovesPastTheDragThresholdWhilePressed_BeforeItReleases()
        {
            const int moves = 6;

            var driver = InstallDriverAndEventSystem();

            var context = NewContext(WriteMode.DeviceInjection,
                SelectorArg("from", SourceName),
                SelectorArg("to", TargetName),
                CountArg("steps", moves),
                InstantArg("duration"),
                InstantArg("holdFor"));

            var frames = Pump(context);

            Assert.IsNull(context.Failure, context.Failure?.Summary);

            // One move to hover the source, the press, the travel, the release. Nothing else.
            var expected = new List<string> { "move", "press" };
            for (var i = 0; i < moves; i++)
                expected.Add("move");
            expected.Add("release");

            CollectionAssert.AreEqual(expected, driver.Operations,
                "the gesture's shape IS the verb: a press and a release with no motion between them is a click");

            var press = driver.Positions[0];
            var threshold = m_EventSystem.pixelDragThreshold;
            var crossedAt = -1;

            for (var i = 1; i < driver.Positions.Count; i++)
            {
                if (Vector2.Distance(driver.Positions[i], press) > threshold)
                {
                    crossedAt = i;
                    break;
                }
            }

            Assert.Greater(crossedAt, 0,
                $"no emitted position was more than pixelDragThreshold ({threshold}px) from the press, " +
                "so uGUI would never have begun a drag and no IBeginDragHandler would run");

            Assert.Less(crossedAt, driver.Positions.Count - 1,
                "at least one move has to follow the one that begins the drag, or IDragHandler never fires while dragging");

            var targetNode = NodeNamed(TargetName);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(targetNode.Handle, out var expectedTarget, out var reason), reason);
            Assert.AreEqual(expectedTarget, driver.Positions[driver.Positions.Count - 1],
                "the last position before the release must be the target itself, or the drop lands somewhere else");

            // Every move needs its own frame: the module reads the pointer once per player frame, so
            // two moves queued in one frame are one move as far as uGUI is concerned.
            Assert.GreaterOrEqual(frames, moves + 2,
                "the moves were not spread across frames, so the UI would see one jump instead of a gesture");
        }

        /// <summary>
        /// THE PHASE ORDER: press, then a stretch of NOTHING, then travel.
        ///
        /// This is the fix for the flakiness the verb had, so it is pinned rather than left to the
        /// shape assertion above — which a gesture that travels straight off the press satisfies
        /// perfectly. A UI that picks something up on a long press asks two questions, "has enough
        /// time passed" and "did the pointer stay put", and a gesture that spreads its moves evenly
        /// across the travel answers the second one differently for every distance and every
        /// duration. Live run 'demo-live2' is what that cost: 3.4s of gesture, nothing picked up,
        /// because the pointer was 21px from the press point when a 28px budget was checked.
        ///
        /// Two facts are locked, because the hold needs both to be a hold. It lasts FRAMES — a UI
        /// cannot run a long-press timer it has not been given a frame to run — and it lasts the
        /// WALL-CLOCK time the step asked for. Delete the phase and the first assertion fails;
        /// reduce it to a frame count and the second does.
        /// </summary>
        [Test]
        public void Gesture_HoldsThePointerCompletelyStillAfterThePress_BeforeAnyTravel()
        {
            var driver = InstallDriverAndEventSystem();

            var context = NewContext(WriteMode.DeviceInjection,
                SelectorArg("from", SourceName),
                SelectorArg("to", TargetName),
                CountArg("steps", 4),
                InstantArg("duration"),
                InstantArg("holdFor"));

            Pump(context);
            Assert.IsNull(context.Failure, context.Failure?.Summary);

            var press = driver.Operations.IndexOf("press");
            Assert.AreEqual(1, press, "one move to the source, then the button: anything else is a different gesture");
            Assert.AreEqual("move", driver.Operations[press + 1], "travel has to follow the hold, not replace it");

            var minimumHoldFrames = FlowInternals.StepLibraryConstant<int>("MinimumHoldFrames");
            Assert.GreaterOrEqual(driver.Frames[press + 1] - driver.Frames[press], minimumHoldFrames,
                $"the pointer travelled within {minimumHoldFrames} frames of the press, so a long-press UI never got a " +
                "frame to arm in and the drag depends on how far it happens to have to go");

            // A second gesture, this time asking for a real hold: the phase must honour the clock and
            // not merely count frames, or 'holdFor' would mean something different on a fast machine.
            var held = TimeSpan.FromMilliseconds(40);
            var timed = InstallDriverAndEventSystem();

            var timedContext = NewContext(WriteMode.DeviceInjection,
                SelectorArg("from", SourceName),
                SelectorArg("to", TargetName),
                CountArg("steps", 4),
                InstantArg("duration"),
                DurationArg("holdFor", held));

            Pump(timedContext, MaxTimedHoldFrames);
            Assert.IsNull(timedContext.Failure, timedContext.Failure?.Summary);

            var timedPress = timed.Operations.IndexOf("press");
            Assert.AreEqual("move", timed.Operations[timedPress + 1], "the hold ends in travel, never in a release");
            Assert.GreaterOrEqual(timed.Times[timedPress + 1] - timed.Times[timedPress], held.TotalSeconds,
                "the pointer started travelling before holdFor had elapsed, so the hold is a frame count wearing a " +
                "duration's name");
        }

        /// <summary>
        /// Two endpoints closer together than the threshold cannot be dragged between, and saying so
        /// is the point: the alternative is emitting a gesture the UI reads as a click and reporting
        /// it as a drag.
        /// </summary>
        [Test]
        public void Gesture_ShorterThanTheDragThreshold_IsRefusedInsteadOfEmitted()
        {
            var driver = InstallDriverAndEventSystem();

            // A child of the source, two pixels off its centre. A child cannot occlude its own
            // ancestor's probe — the hit is a descendant, which is on target — so the only thing
            // this changes is how far the gesture would travel.
            CreateElement(NearName, m_Source, new Vector2(20f, 20f), new Vector2(2f, 0f));

            var context = NewContext(WriteMode.DeviceInjection,
                SelectorArg("from", SourceName),
                SelectorArg("to", NearName),
                InstantArg("duration"),
                InstantArg("holdFor"));

            Pump(context);

            Assert.IsNotNull(context.Failure, "a gesture that cannot become a drag must not be reported as one");
            StringAssert.Contains("pixelDragThreshold", context.Failure.Summary);
            CollectionAssert.IsEmpty(driver.Operations, "nothing may be emitted once the gesture has been refused");
        }

        [Test]
        public void Gesture_WithFewerThanTwoMoves_IsRefused()
        {
            InstallDriverAndEventSystem();

            var context = NewContext(WriteMode.DeviceInjection,
                SelectorArg("from", SourceName),
                SelectorArg("to", TargetName),
                CountArg("steps", 1));

            Pump(context);

            Assert.IsNotNull(context.Failure);
            StringAssert.Contains("steps: 1", context.Failure.Summary);
        }

        /// <summary>
        /// The refusal that keeps the verb honest. Semantic dispatch has no device behind it, so the
        /// most it could produce is a press and a release — a tap. Reporting that as a drag would go
        /// green against a UI where dragging is broken, so the verb fails and touches nothing.
        /// </summary>
        [Test]
        public void SemanticDispatch_RefusesLoudlyAndDoesNotDegradeIntoATap()
        {
            var probe = m_Source.gameObject.AddComponent<PointerProbe>();

            var context = NewContext(WriteMode.SemanticDispatch, SelectorArg("from", SourceName), SelectorArg("to", TargetName));

            Pump(context);

            Assert.IsNotNull(context.Failure, "a drag that cannot be performed must fail, never quietly become something else");
            StringAssert.Contains("semantic dispatch", context.Failure.Summary);
            StringAssert.Contains("pixelDragThreshold", context.Failure.Summary);

            Assert.AreEqual(0, probe.Downs, "no press may reach the source");
            Assert.AreEqual(0, probe.Ups);
            Assert.AreEqual(0, probe.Clicks, "silently tapping instead of dragging is the exact failure this refusal prevents");
        }

        // ---- harness ---------------------------------------------------------------------------

        /// <summary>Run the step to completion the way the driver would, and report the frames it took.</summary>
        private int Pump(StepContext context) => Pump(context, MaxPumpedFrames);

        private int Pump(StepContext context, int maxFrames)
        {
            var pump = new FlowPump(StepLibrary.Execute(context));

            // Stamping each emitted operation with the frame it was emitted on is what lets a test
            // assert the gaps BETWEEN operations — a motionless hold emits nothing at all, so it is
            // invisible in the operation list and visible only as a gap.
            if (m_Driver != null)
                m_Driver.FrameSource = () => pump.Frames;

            pump.RunToCompletion(maxFrames, Settle);
            return pump.Frames;
        }

        private object ResolveEndpoint(StepContext context, string selectorKey, string pointKey)
        {
            var endpoint = FlowInternals.ReadDragEndpoint(context, selectorKey, pointKey);
            Assert.IsNotNull(endpoint, context.Failure?.Summary);

            for (var frame = 0; frame < 8; frame++)
            {
                Settle();

                if (FlowInternals.AdvanceDragEndpoint(context, endpoint, out var fatal))
                    return endpoint;

                Assert.IsFalse(fatal, FlowInternals.DragEndpointField<string>(endpoint, "Reason"));
            }

            Assert.Fail($"'{selectorKey}' never resolved: {FlowInternals.DragEndpointField<string>(endpoint, "Reason")}");
            return null;
        }

        private StepContext NewContext(WriteMode writeMode, params FlowArgument[] args)
        {
            var context = new StepContext(Registry, Resolver, RunPaths.Existing("unityflow-tests-drag"),
                m_Console, () => writeMode);

            FlowInternals.SetStep(context, new FlowStep("drag", args, null, null, null, null, 3, 5));
            FlowInternals.SetDeadline(context, FlowClock.Now + 5.0);
            return context;
        }

        private RecordingInputDriver InstallDriverAndEventSystem()
        {
            m_Driver = RecordingInputDriver.Create();
            FlowInternals.SetInputDriver(Registry, m_Driver);
            RegisterEventSystem();
            return m_Driver;
        }

        /// <summary>
        /// Make an EventSystem the current one WITHOUT play mode.
        ///
        /// EventSystem is not [ExecuteAlways], so its OnEnable never runs in edit mode and
        /// <c>EventSystem.current</c> stays null — which the drag verb correctly refuses to work
        /// without, since a null one means nothing is dispatching pointer events at all. The static
        /// list behind <c>current</c> is what OnEnable would have added to, so the test adds to it
        /// directly and takes it back out in teardown. Nothing else about the EventSystem is
        /// activated, so no input module starts and no other test's view of the world changes.
        /// </summary>
        private void RegisterEventSystem()
        {
            if (m_EventSystem != null)
                return;

            m_EventSystem = new GameObject("UnityFlowTestsEventSystem").AddComponent<EventSystem>();
            EventSystems().Insert(0, m_EventSystem);

            Assert.AreSame(m_EventSystem, EventSystem.current,
                "registering the EventSystem did not make it current, so the threshold read would not be this one's");
        }

        private void UnregisterEventSystem()
        {
            if (m_EventSystem == null)
                return;

            EventSystems().Remove(m_EventSystem);
            m_EventSystem = null;
        }

        private static List<EventSystem> EventSystems()
        {
            var field = typeof(EventSystem).GetField("m_EventSystems", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException(
                            "UnityEngine.EventSystems.EventSystem no longer keeps its systems in a static 'm_EventSystems' list; " +
                            "this test can no longer make one current without play mode.");

            return (List<EventSystem>)field.GetValue(null);
        }

        private static FlowArgument SelectorArg(string name, string nodeName) =>
            new FlowArgument(name, FlowArgKind.Selector, ByName(nodeName), null, FlowValue.OfScalar(nodeName, false, 3, 5));

        private static FlowArgument PointArg(string name, Vector2 point) =>
            new FlowArgument(name, FlowArgKind.Vector2, point, null, FlowValue.OfScalar(name, false, 3, 5));

        /// <summary>A point written as "@name": unconverted at parse time, because the value it names does not exist yet.</summary>
        private static FlowArgument ReferenceArg(string name, string reference) =>
            new FlowArgument(name, FlowArgKind.Vector2, null, reference, FlowValue.OfScalar("@" + reference, true, 3, 5));

        private static FlowArgument CountArg(string name, int value) =>
            new FlowArgument(name, FlowArgKind.Int, value, null, FlowValue.OfScalar(value.ToString(), false, 3, 5));

        private static FlowArgument DurationArg(string name, TimeSpan value) =>
            new FlowArgument(name, FlowArgKind.Duration, value, null,
                FlowValue.OfScalar($"{value.TotalMilliseconds:0}ms", false, 3, 5));

        /// <summary>
        /// Zero seconds, so the phase it names costs only the frames it structurally needs and the
        /// test does not spin on a wall clock. Written out at every call site rather than left to the
        /// verb's defaults: a 400ms default hold is 400ms of real time in a pump that runs as fast as
        /// the editor can go, which is thousands of wasted frames per test.
        /// </summary>
        private static FlowArgument InstantArg(string name) => DurationArg(name, TimeSpan.Zero);

        /// <summary>
        /// Records what the verb asked the device to do, and refuses everything else.
        ///
        /// ITS CONSTRUCTOR IS PRIVATE ON PURPOSE. BackendRegistry discovers input drivers with
        /// TypeCache and instantiates any implementation that has a PUBLIC parameterless
        /// constructor — including one declared in this assembly, which is loaded in the editor
        /// alongside every real run. A discoverable test double could therefore be picked as the
        /// driver of a genuine flow. With a non-public constructor it is invisible to discovery and
        /// reaches a registry only when a test puts it there.
        /// </summary>
        private sealed class RecordingInputDriver : IInputDriver
        {
            public readonly List<string> Operations = new List<string>();
            public readonly List<Vector2> Positions = new List<Vector2>();

            /// <summary>Pumped frame each operation was emitted on, parallel to <see cref="Operations"/>.</summary>
            public readonly List<int> Frames = new List<int>();

            /// <summary><see cref="FlowClock"/> reading for each operation, parallel to <see cref="Operations"/>.</summary>
            public readonly List<double> Times = new List<double>();

            /// <summary>Set by the pump, which is the only thing that knows what frame it is on.</summary>
            public Func<int> FrameSource = () => 0;

            private RecordingInputDriver() { }

            /// <summary>Constructed only from the enclosing test, which private access permits.</summary>
            public static RecordingInputDriver Create() => new RecordingInputDriver();

            public string Id => "unityflow-tests-recording";

            public bool IsAvailable(out string reason)
            {
                reason = "this is a test double and is never available to a real run";
                return false;
            }

            public IDisposable BeginSession() =>
                throw new NotSupportedException("the drag verb must use the session the run already opened, not open its own");

            public void MovePointer(Vector2 screenPoint)
            {
                Record("move");
                Positions.Add(screenPoint);
            }

            public void PressPointer(int button) => Record("press");

            public void ReleasePointer(int button) => Record("release");

            public void PressKey(string key) =>
                throw new NotSupportedException($"drag pressed the key '{key}'; a pointer gesture must send no keys");

            public void ReleaseKey(string key) =>
                throw new NotSupportedException($"drag released the key '{key}'; a pointer gesture must send no keys");

            // Flush is deliberately not recorded: the verb flushes after every operation, and
            // asserting that between each pair would say nothing the ordering assertion does not.
            public void Flush() { }

            private void Record(string operation)
            {
                Operations.Add(operation);
                Frames.Add(FrameSource());
                Times.Add(FlowClock.Now);
            }
        }
    }
}
