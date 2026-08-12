using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityFlow.Editor.Capture;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.PlayMode;
using UnityFlow.Editor.State;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Implementations of the built-in verbs.
    ///
    /// Every one is a frame-aligned retry loop, and that is the whole product. Resolving, checking
    /// actionability and retrying next frame costs nothing — no round trip, no polling interval —
    /// so network latency, async loads and animation timing are absorbed for free. It is the
    /// reason a well-written flow never contains "wait: 2s".
    /// </summary>
    public static class StepLibrary
    {
        /// <summary>
        /// Frames a node must remain actionable before it is acted on.
        ///
        /// Almost every popup fades or scales in. Acting on the first frame a node reports
        /// actionable can land a click mid-tween, on a rect that moves out from under the pointer
        /// before the release. Two consecutive agreeing frames costs ~16ms and removes that class
        /// of flake entirely.
        /// </summary>
        private const int StabilityFrames = 2;

        /// <summary>
        /// Presses <c>navigateTo</c> will send before it gives up.
        ///
        /// Generous on purpose: a wrong answer here is a flaky test, and 40 presses cost about 120
        /// frames — under two seconds, well inside the default step timeout. A real navigation graph
        /// that needs more than 40 moves to cross one screen is itself the finding.
        /// </summary>
        private const int DefaultMaxNavigationSteps = 40;

        /// <summary>
        /// The keys the UI's Submit and Cancel actions are bound to on a keyboard.
        ///
        /// Not arbitrary and not configurable: the project's actions asset binds Submit to
        /// <c>*/{Submit}</c> and Cancel to <c>*/{Cancel}</c> — control USAGES, not paths — and the
        /// Input System's Keyboard layout tags exactly one control with each (verified against the
        /// live layout: <c>enter</c> carries the Submit usage, <c>escape</c> carries Back and Cancel;
        /// <c>numpadEnter</c> carries neither). Driving the device with these two keys therefore goes
        /// through the project's own bindings, which is the whole point of not driving the action.
        /// </summary>
        private const string SubmitKey = "enter";
        private const string CancelKey = "escape";

        /// <summary>Default window a negative assertion must hold for.</summary>
        public static readonly TimeSpan DefaultStableFor = TimeSpan.FromMilliseconds(500);

        public static IEnumerator Execute(StepContext ctx) => GuardMatchTimeout(ctx, Dispatch(ctx));

        /// <summary>
        /// Turn a regex match timeout into a step failure that names the pattern.
        ///
        /// <see cref="Selector.MatchTimeout"/> is what stops a catastrophic pattern from freezing
        /// the editor, but the exception the engine raises names neither the step nor the flow, so
        /// without this the report would say only "the Regex engine has timed out". C# forbids
        /// yielding inside a try that has a catch, so the inner step is stepped by hand.
        /// </summary>
        private static IEnumerator GuardMatchTimeout(StepContext ctx, IEnumerator body)
        {
            while (true)
            {
                object current;

                try
                {
                    if (!body.MoveNext())
                        yield break;

                    current = body.Current;
                }
                catch (RegexMatchTimeoutException ex)
                {
                    ctx.Fail(
                        $"{ctx.Step.Verb} gave up on the regular expression /{ex.Pattern}/ after " +
                        $"{ex.MatchTimeout.TotalMilliseconds:0}ms against \"{Truncate(ex.Input, 60)}\". " +
                        "The pattern backtracks catastrophically (nested quantifiers such as (a+)+ are the usual cause) " +
                        "and it is re-evaluated every frame, so it is refused rather than allowed to hang the editor.",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                yield return current;
            }
        }

        private static IEnumerator Dispatch(StepContext ctx)
        {
            switch (ctx.Step.Verb)
            {
                case "tapOn": return TapOn(ctx);
                case "drag": return Drag(ctx);
                case "inputText": return InputText(ctx);
                case "press": return Press(ctx);
                case "navigateTo": return NavigateTo(ctx);
                case "submit": return SubmitOrCancel(ctx, submit: true);
                case "cancel": return SubmitOrCancel(ctx, submit: false);
                case "waitFor": return WaitForVisible(ctx, assertion: false);
                case "assertVisible": return WaitForVisible(ctx, assertion: true);
                case "waitUntilNotVisible": return WaitUntilNotVisible(ctx);
                case "assertNotVisible": return AssertNotVisible(ctx);
                case "assertText": return AssertText(ctx);
                case "assert": return Assert(ctx);
                case "waitUntil": return WaitUntil(ctx);
                case "assertLog": return LogAssertions.AssertLog(ctx);
                case "assertNoLog": return LogAssertions.AssertNoLog(ctx);
                case "screenshot": return Screenshot(ctx);
                case "runScript": return ScriptStep.Run(ctx);
                case "wait": return Wait(ctx);
                case "enterPlayMode": return EnterPlayMode(ctx);
                case "exitPlayMode": return ExitPlayMode(ctx);
                case "runFlow":
                    // The parser splices a sub-flow's steps into the parent's list, so a runFlow
                    // step never reaches the runner. Arriving here means the expansion was skipped,
                    // and silently doing nothing would run a flow that is missing whole sections.
                    ctx.Fail("'runFlow' reached the runner, but it is resolved at parse time and must never be executed. " +
                             "The step list was built without expanding it, which is a bug in UnityFlow rather than in this flow.");
                    return Flow.Done();
                default:
                    ctx.Fail($"'{ctx.Step.Verb}' is not a built-in verb and no [FlowCommand] provides it");
                    return Flow.Done();
            }
        }

        private static IEnumerator TapOn(StepContext ctx)
        {
            var allowUnverified = ctx.Step.Has("allowUnverifiedOcclusion") &&
                                  ctx.Step.Get<bool>("allowUnverifiedOcclusion");

            var stable = 0;
            string lastReason = null;

            while (!ctx.DeadlineReached)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (!resolution.IsResolved)
                {
                    if (!resolution.IsRetryable)
                    {
                        ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                        yield break;
                    }

                    lastReason = resolution.Message;
                    stable = 0;
                    yield return null;
                    continue;
                }

                var node = resolution.Node;
                var backend = ctx.Registry.ForHandle(node.Handle);

                if (!backend.IsActionable(node.Handle, out lastReason))
                {
                    stable = 0;
                    yield return null;
                    continue;
                }

                if (++stable < StabilityFrames)
                {
                    yield return null;
                    continue;
                }

                if (!backend.TryResolveInjectionPoint(node.Handle, out var point, out lastReason))
                {
                    // No screen coordinate at all is not a timing problem and will not fix itself.
                    ctx.Fail($"{ctx.Step.Selector} resolved to {node.Path} but it cannot be tapped: {lastReason}",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                var hit = backend.HitTest(node.Handle, point);
                if (hit.Outcome == HitOutcome.Occluded)
                {
                    lastReason = $"obscured by {hit.HitPath}";
                    stable = 0;
                    yield return null;
                    continue;
                }

                if (hit.Outcome == HitOutcome.Unavailable && !allowUnverified)
                {
                    ctx.Fail(
                        $"refusing to tap {node.Path}: occlusion could not be verified ({hit.HitPath}). " +
                        "A tap that is not occlusion-checked can silently succeed through a modal, which is how a " +
                        "test goes green on broken UI. Enter play mode, or set allowUnverifiedOcclusion: true to accept the risk.",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                if (hit.Outcome == HitOutcome.NoHit)
                {
                    lastReason = "the pointer position hit nothing at all";
                    stable = 0;
                    yield return null;
                    continue;
                }

                yield return PerformTap(ctx, backend, node, point);

                if (ctx.Step.As != null)
                    ctx.Resolver.Bind(ctx.Step.As, node.Handle);

                yield break;
            }

            ctx.Fail($"tapOn {ctx.Step.Selector} timed out after {Describe(ctx)}: {lastReason ?? "never became actionable"}",
                ctx.BuildDiagnostics());
        }

        /// <summary>
        /// Produce the pointer interaction.
        ///
        /// The yields between move, press and release are load-bearing, not padding: uGUI
        /// dispatches pointer events from InputSystemUIInputModule.Process(), which runs from
        /// EventSystem.Update() inside the player loop. Queueing a press and a release without a
        /// frame between them collapses both into one poll and produces no click at all.
        /// </summary>
        private static IEnumerator PerformTap(StepContext ctx, IUiBackend backend, UiNode node, UnityEngine.Vector2 point)
        {
            if (ctx.WriteMode == WriteMode.DeviceInjection)
            {
                var input = ctx.Registry.InputDriver;

                input.MovePointer(point);
                input.Flush();
                yield return null;

                input.PressPointer(0);
                input.Flush();
                yield return null;

                input.ReleasePointer(0);
                input.Flush();
                yield return null;
            }
            else
            {
                if (!backend.TryDispatch(node.Handle, PointerGesture.Click, point, out var error))
                {
                    ctx.Fail($"could not dispatch a click to {node.Path}: {error}", ctx.BuildDiagnostics());
                    yield break;
                }

                yield return null;
            }
        }

        // ---- drag --------------------------------------------------------------------------

        /// <summary>
        /// TRAVEL time when the step does not say — the time spent moving from the source to the
        /// target, and nothing else. Arming a hold-to-pick-up UI is <see cref="DefaultDragHold"/>'s
        /// job now, so this no longer has to be stretched to cover it.
        /// </summary>
        private static readonly TimeSpan DefaultDragDuration = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How long the pointer is held ABSOLUTELY STILL after the press before any travel begins,
        /// when the step does not say.
        ///
        /// A long-press UI arms on two conditions: enough time, and no movement. The inventory grid
        /// this was measured against — a real, shipped game — waits 0.25s and cancels if the pointer
        /// left the tile it went down in. Holding still satisfies BOTH unconditionally, which is
        /// the entire reason the phase exists: while the travel and the arming were one motion,
        /// arming depended on the ratio of distance to duration and every drag had a different
        /// margin. At 3s over a 252px path the pointer was 21px from the press point when the 0.25s
        /// check fired, against a 28px budget, and run 'demo-live2' spent 3.4s to pick up nothing.
        ///
        /// 600ms is MEASURED, not reasoned about: 47 real gestures against that live game
        /// armed 15/15 at 600ms, 24/25 at 400ms, 2/3 at 300ms
        /// and 0/3 below 250ms. 400ms is 1.6x the game's own timer and still lost one; 600ms is
        /// 2.4x and lost none, across frame budgets from 9ms to 22ms. The extra 200ms buys the one
        /// failure a caller cannot see coming — uGUI reports `dragging` whether the long press won
        /// or the ScrollRect took the gesture, so the retry loop below cannot catch this particular
        /// loss and the margin has to.
        /// </summary>
        private static readonly TimeSpan DefaultDragHold = TimeSpan.FromMilliseconds(600);

        /// <summary>
        /// Frames the hold lasts at minimum, however short <c>holdFor</c> is.
        ///
        /// A duration alone is not enough: what a long press measures is where the pointer is when
        /// its own timer fires, and a UI cannot see a press it has not been given a frame to
        /// process. Three frames is the press being observed, the game reacting to it, and one
        /// frame of the pointer verifiably not moving — the smallest hold that is a hold.
        /// </summary>
        private const int MinimumHoldFrames = 3;

        /// <summary>Intermediate moves when the step does not say.</summary>
        private const int DefaultDragSteps = 12;

        /// <summary>
        /// Fewest intermediate moves that can still be called a drag: one to cross the threshold and
        /// begin it, one to move while it is in progress. One move is a jump, and a UI that measures
        /// its own travel — the inventory grid above does, at 50px — sees a single teleport
        /// rather than a gesture.
        /// </summary>
        private const int MinimumDragSteps = 2;

        /// <summary>
        /// Frames the target is given to resolve BEFORE the press, which is only enough for the
        /// stability gate plus one. It is deliberately small: a target that is not there yet is
        /// usually a drop zone that does not exist until the drag starts, and the way to find that
        /// out is to start the drag, not to wait.
        /// </summary>
        private const int PreflightTargetFrames = StabilityFrames + 1;

        /// <summary>
        /// Moves used to lift the pointer off the source when the target has to be found mid-drag.
        /// Enough to cross <c>pixelDragThreshold</c> gradually rather than in one jump.
        /// </summary>
        private const int PrimingMoves = 4;

        /// <summary>
        /// One end of a drag, and everything the failure report needs to say which end went wrong.
        /// Allocated twice per step and never per frame.
        /// </summary>
        private sealed class DragEndpoint
        {
            public string Key;
            public Selector Selector;
            public UnityEngine.Vector2 Point;
            public UiHandle Handle;
            public bool Resolved;
            public string Path;
            public string Reason;
            public int Stable;
        }

        /// <summary>What one attempt at the gesture did, so the retry loop and the failure can both read it.</summary>
        private sealed class DragAttempt
        {
            /// <summary>The travel finished and the pointer was released at the target.</summary>
            public bool Completed;

            /// <summary>The step has already been failed; the loop must stop rather than retry.</summary>
            public bool Fatal;

            /// <summary>A drag has been confirmed in progress, or the UI system could not be asked at all.</summary>
            public bool Confirmed;

            /// <summary>What the last confirmation said, in the words the UI system used.</summary>
            public string Confirmation;

            public void Reset()
            {
                Completed = false;
                Fatal = false;
                Confirmed = false;
                Confirmation = null;
            }
        }

        /// <summary>
        /// Drag with a real pointer, in phases: move to the source, PRESS, HOLD STILL, confirm a drag
        /// exists, travel across frames, release.
        ///
        /// <para><b>The hold is separate from the travel because arming is separate from moving.</b>
        /// A UI that picks something up on a long press is asking two questions — has enough time
        /// passed, and has the pointer stayed put — and a gesture that starts travelling immediately
        /// answers the second one differently depending on how far it has to go and how long it was
        /// given. That coupling is what made this verb flaky: the same flow armed at 3s and did not
        /// at 2.2s, for a rule that has nothing to do with either number. Holding still first
        /// satisfies both conditions by construction, and <c>duration</c> then means travel and only
        /// travel.</para>
        ///
        /// <para><b>Then it checks, and retries if it must.</b> After the hold the UI system is asked
        /// whether the press is live and something is registered to receive its drags
        /// (<see cref="IUiBackend.GetDragState"/>), and once the pointer has crossed the drag
        /// threshold it is asked whether a drag actually began. A no releases the button, returns to
        /// the source and starts the gesture over, until the step's deadline — the same model as
        /// tapOn retrying resolution, and for the same reason: the interesting failures here are
        /// timing, and timing is what a retry is for. Every attempt after the first is written to the
        /// progress stream, so a reader sees "armed on attempt 2" rather than a silent pass.</para>
        ///
        /// The intermediate moves are still the verb. uGUI does not begin a drag until the
        /// pointer has travelled past <c>EventSystem.pixelDragThreshold</c> while a button is held,
        /// and it observes that from <c>InputSystemUIInputModule.Process()</c> — once per frame,
        /// only on frames where the pointer actually moved. A press and a release at two different
        /// places therefore raise no IBeginDragHandler, no IDragHandler and no IDropHandler at all;
        /// they are a click whose release happened to land elsewhere, and uGUI's own release gate
        /// then discards even that. A real inventory grid makes the point twice over: it starts an
        /// item drag from OnDrag only after the pointer has moved 50px from where it went down, and
        /// the matching drop zone's IDropHandler is reached only by a release that happens while
        /// <c>dragging</c> is true.
        ///
        /// <para><b>The target may not exist yet.</b> A drop zone routinely activates on drag start,
        /// so resolving 'to' before the press can legitimately fail. When it does, the press is sent
        /// anyway, the pointer is primed past the drag threshold to make the zone appear, and 'to'
        /// is resolved again while the drag is in progress. The failure says which endpoint was
        /// missing and whether it was before or during the drag, because those are two different
        /// bugs.</para>
        /// </summary>
        private static IEnumerator Drag(StepContext ctx)
        {
            if (!TryReadEndpoint(ctx, "from", "fromPoint", out var source) ||
                !TryReadEndpoint(ctx, "to", "toPoint", out var target))
            {
                yield break;
            }

            var moves = ctx.Step.Has("steps") ? ctx.Step.Get<int>("steps") : DefaultDragSteps;
            if (moves < MinimumDragSteps)
            {
                ctx.Fail($"drag was given steps: {moves}. Below {MinimumDragSteps} there is no motion under the held " +
                         "button for uGUI to measure, so no drag begins and the gesture is a click with a misplaced release.");
                yield break;
            }

            var duration = (ctx.Step.Has("duration") ? ctx.Step.Get<TimeSpan>("duration") : DefaultDragDuration).TotalSeconds;
            if (duration < 0.0)
            {
                ctx.Fail($"drag was given a negative duration ({duration:0.###}s)");
                yield break;
            }

            var hold = (ctx.Step.Has("holdFor") ? ctx.Step.Get<TimeSpan>("holdFor") : DefaultDragHold).TotalSeconds;
            if (hold < 0.0)
            {
                ctx.Fail($"drag was given a negative holdFor ({hold:0.###}s)");
                yield break;
            }

            if (ctx.WriteMode != WriteMode.DeviceInjection)
            {
                ctx.Fail(
                    "drag needs a real pointer device, and this run resolved to semantic dispatch instead " +
                    $"({ctx.Registry.InputDriverRejection}). A drag is defined by pointer motion measured across frames " +
                    "against EventSystem.pixelDragThreshold; semantic dispatch synthesizes discrete events with no device " +
                    "behind them, so the strongest thing it could produce here is a press and a release — which is a tap. " +
                    "Reporting that as a drag would go green against a UI where dragging is broken, so it is refused. " +
                    "Enter play mode so device injection is available.",
                    ctx.BuildDiagnostics());
                yield break;
            }

            if (!TryReadDragThreshold(ctx, out var threshold))
                yield break;

            var input = ctx.Registry.InputDriver;

            // The source must be fully verified before anything is pressed: a press on an occluded
            // element is exactly the failure tapOn refuses, and starting a drag from one would be
            // the same lie told over more frames.
            while (!source.Resolved && !ctx.DeadlineReached)
            {
                if (!TryAdvanceEndpoint(ctx, source, out var fatal))
                {
                    if (fatal)
                    {
                        ctx.Fail($"drag cannot start: {source.Reason}", ctx.BuildDiagnostics());
                        yield break;
                    }

                    yield return null;
                }
            }

            if (!source.Resolved)
            {
                ctx.Fail($"drag timed out after {Describe(ctx)} resolving its SOURCE 'from' {source.Selector}: {source.Reason ?? "it never became actionable"}",
                    ctx.BuildDiagnostics());
                yield break;
            }

            // Best effort, on purpose. A target that is already there gives the straight path; one
            // that is not is looked for again once the drag has actually begun.
            for (var i = 0; i < PreflightTargetFrames && !target.Resolved; i++)
            {
                if (!TryAdvanceEndpoint(ctx, target, out var fatal) && fatal)
                    break;

                if (!target.Resolved)
                    yield return null;
            }

            var resolvedBeforePress = target.Resolved;

            if (resolvedBeforePress && !TryRequireTravel(ctx, source, target, threshold))
                yield break;

            var attempt = new DragAttempt();
            var attempts = 0;

            while (!ctx.DeadlineReached)
            {
                attempts++;
                attempt.Reset();

                yield return RunDragAttempt(ctx, input, source, target, threshold, moves, duration, hold,
                    resolvedBeforePress, attempt);

                if (attempt.Fatal)
                    yield break;

                if (attempt.Completed)
                {
                    // Only a retry is worth a record. A first attempt that worked is what the step
                    // already claims by passing, and the stream is read by people looking for
                    // surprises.
                    if (attempts > 1)
                        NoteDragAttempt(ctx, attempts, "armed", attempt.Confirmation);

                    if (ctx.Step.As != null && !target.Handle.IsNone)
                        ctx.Resolver.Bind(ctx.Step.As, target.Handle);

                    yield break;
                }

                NoteDragAttempt(ctx, attempts, "no-drag", attempt.Confirmation);
            }

            ctx.Fail(
                $"drag from {source.Path ?? Format(source.Point)} to {target.Path ?? Format(target.Point)} never began a " +
                $"drag. {attempts} attempt(s) in {Describe(ctx)}, each pressing and then holding the pointer completely " +
                $"still for {hold:0.###}s before travelling. The last attempt's confirmation said: " +
                $"{attempt.Confirmation ?? "nothing was read"}.",
                ctx.BuildDiagnostics());
        }

        /// <summary>
        /// One whole gesture: move, press, hold still, confirm, travel, release. Everything it
        /// learns is written to <paramref name="attempt"/>, because a coroutine cannot have out
        /// parameters and the caller has to be able to tell "no drag began" from "the step is over".
        /// </summary>
        private static IEnumerator RunDragAttempt(StepContext ctx, IInputDriver input, DragEndpoint source,
            DragEndpoint target, int threshold, int moves, double duration, double hold, bool resolvedBeforePress,
            DragAttempt attempt)
        {
            // 1. The pointer arrives and the UI sees it there before anything is pressed.
            input.MovePointer(source.Point);
            input.Flush();
            yield return null;

            // 2. PRESS.
            input.PressPointer(0);
            input.Flush();
            yield return null;

            // 3. HOLD, COMPLETELY STILL. Not one intermediate move: this is the phase a long press
            //    measures, and any motion at all is what cancels it.
            var holdUntil = FlowClock.Now + hold;
            for (var held = 0; held < MinimumHoldFrames || FlowClock.Now < holdUntil; held++)
                yield return null;

            // 4. CONFIRM, before travelling. uGUI cannot report `dragging` yet — that needs motion —
            //    so what is asked here is the strongest thing that is true of a motionless held
            //    press: the button is down and something is registered to receive its drags.
            var armed = ConfirmDrag(ctx);
            attempt.Confirmation = armed.Describe();

            if (armed.Outcome == PointerDragOutcome.None)
            {
                yield return ReleaseAfterFailedAttempt(input);
                yield break;
            }

            // Unavailable is not a yes; it is "this cannot be asked here", and the verb says so
            // rather than retrying a gesture against a signal that will never arrive.
            attempt.Confirmed = armed.Outcome != PointerDragOutcome.Armed;

            var pointer = source.Point;

            if (!resolvedBeforePress)
            {
                // Lift off the source far enough that uGUI raises OnBeginDrag; that is what makes a
                // drop zone which activates on drag start exist at all.
                var primed = PrimeTarget(source.Point, threshold);

                for (var i = 1; i <= PrimingMoves; i++)
                {
                    pointer = UnityEngine.Vector2.Lerp(source.Point, primed, i / (float)PrimingMoves);
                    input.MovePointer(pointer);
                    input.Flush();
                    yield return null;
                }

                if (!attempt.Confirmed)
                {
                    yield return null;

                    var primedState = ConfirmDrag(ctx);
                    attempt.Confirmation = primedState.Describe();

                    if (primedState.Outcome != PointerDragOutcome.Dragging)
                    {
                        yield return ReleaseAfterFailedAttempt(input);
                        yield break;
                    }

                    attempt.Confirmed = true;
                }

                // Every frame moves the pointer by a pixel, because InputSystemUIInputModule only
                // raises IDragHandler on frames where the pointer actually moved — a motionless wait
                // would leave the UI thinking the drag had stalled.
                var wobble = 1f;

                while (!target.Resolved && !ctx.DeadlineReached)
                {
                    if (!TryAdvanceEndpoint(ctx, target, out var fatal) && fatal)
                        break;

                    if (target.Resolved)
                        break;

                    pointer = new UnityEngine.Vector2(primed.x + wobble, primed.y);
                    wobble = -wobble;
                    input.MovePointer(pointer);
                    input.Flush();
                    yield return null;
                }

                if (!target.Resolved)
                {
                    // Put the button back before reporting, or the editor is left with a virtual
                    // mouse held down and the next step drags whatever it touches.
                    input.ReleasePointer(0);
                    input.Flush();
                    yield return null;

                    ctx.Fail(
                        $"drag pressed on {source.Path ?? Format(source.Point)} and moved past the {threshold}px drag " +
                        $"threshold, but its TARGET 'to' {target.Selector} never resolved while the drag was in progress: " +
                        $"{target.Reason ?? "it never appeared"}. It was already absent before the press, so this is not a " +
                        "drop zone that failed to activate — the selector matches nothing at either moment. The pointer was " +
                        "released where it stood.",
                        ctx.BuildDiagnostics());

                    attempt.Fatal = true;
                    yield break;
                }
            }

            // 5. TRAVEL. 'duration' is spent here and nowhere else.
            var pace = new WaitRealSeconds(duration / moves);
            var travelFrom = pointer;
            var paced = duration > 0.0;

            for (var i = 1; i <= moves; i++)
            {
                pointer = UnityEngine.Vector2.Lerp(travelFrom, target.Point, i / (float)moves);
                input.MovePointer(pointer);
                input.Flush();

                // A zero-length yield does NOT cost a frame — the driver runs an already-done
                // FlowYield straight through — so an unpaced drag has to yield null instead.
                if (paced)
                    yield return pace;
                else
                    yield return null;

                if (attempt.Confirmed ||
                    UnityEngine.Vector2.Distance(source.Point, pointer) <= threshold)
                {
                    continue;
                }

                // The move that crossed the threshold is promoted to a drag inside the Process()
                // that OBSERVES it, so the promotion is only readable from the following frame.
                yield return null;

                var travelling = ConfirmDrag(ctx);
                attempt.Confirmation = travelling.Describe();

                if (travelling.Outcome != PointerDragOutcome.Dragging)
                {
                    // Abort here rather than at the target: the pointer is still one threshold away
                    // from where it went down, so the release lands where the press did instead of
                    // dropping a gesture nobody asked for on the destination.
                    yield return ReleaseAfterFailedAttempt(input);
                    yield break;
                }

                attempt.Confirmed = true;
            }

            // 6. The module dispatches the last move on the frame that observes it; releasing before
            //    that would drop the pointer somewhere the UI never saw it.
            input.ReleasePointer(0);
            input.Flush();
            yield return null;

            // IDropHandler and IEndDragHandler are raised inside the Process() that observed the
            // release, so anything they changed only exists from the following frame.
            yield return null;

            attempt.Completed = true;
        }

        /// <summary>
        /// Ask every live UI system what its pressed pointer is doing and keep the strongest answer.
        ///
        /// Strongest, not first: two UI systems can be up at once (a uGUI canvas alongside a
        /// UI Toolkit panel), only one of them owns the pointer, and the one that does not answers
        /// truthfully that nothing is pressed in it. Taking the first answer would let an idle
        /// backend veto a real drag.
        /// </summary>
        private static PointerDragState ConfirmDrag(StepContext ctx)
        {
            var best = default(PointerDragState);
            var answered = false;

            foreach (var backend in ctx.Registry.Active)
            {
                var state = backend.GetDragState();
                if (answered && state.Outcome <= best.Outcome)
                    continue;

                best = state;
                answered = true;
            }

            return answered ? best : PointerDragState.CannotRead("no UI backend is active");
        }

        /// <summary>
        /// Put the button back after an attempt that never became a drag, and give the UI the frames
        /// it needs to finish reacting before the next attempt presses again.
        /// </summary>
        private static IEnumerator ReleaseAfterFailedAttempt(IInputDriver input)
        {
            input.ReleasePointer(0);
            input.Flush();
            yield return null;
            yield return null;
        }

        private static void NoteDragAttempt(StepContext ctx, int attempt, string outcome, string confirmation)
        {
            ctx.Note("drag.attempt", new[]
            {
                new KeyValuePair<string, object>("attempt", attempt),
                new KeyValuePair<string, object>("outcome", outcome),
                new KeyValuePair<string, object>("confirmation", confirmation)
            });
        }

        /// <summary>
        /// Read one endpoint, refusing the two ways a flow can fail to name it: not at all, or twice.
        /// </summary>
        private static bool TryReadEndpoint(StepContext ctx, string selectorKey, string pointKey, out DragEndpoint endpoint)
        {
            // default(UiHandle) is NOT UiHandle.None — its BackendId is 0, a real backend index — so
            // an endpoint that never resolves a node has to be given the sentinel explicitly.
            endpoint = new DragEndpoint { Key = selectorKey, Handle = UiHandle.None };

            var hasSelector = ctx.Step.TryGet<Selector>(selectorKey, out var selector);
            var hasPoint = ctx.Step.TryGetArg(pointKey, out var pointArg);

            if (hasSelector && hasPoint)
            {
                ctx.Fail($"drag was given both '{selectorKey}' and '{pointKey}'. One end of a drag is one place; " +
                         "which of the two was meant is not guessable, so neither is used.");
                return false;
            }

            if (!hasSelector && !hasPoint)
            {
                ctx.Fail($"drag needs '{selectorKey}', either a selector like {{ name: slot_0 }}, the raw screen " +
                         $"coordinate '{pointKey}: [x, y]', or '{pointKey}: \"@name\"' naming a point an earlier " +
                         "step bound with 'as:'.");
                return false;
            }

            if (hasSelector)
            {
                endpoint.Selector = selector;
                return true;
            }

            if (!TryReadPoint(ctx, pointKey, pointArg, out var point))
                return false;

            // A bare coordinate names no element, so there is nothing to check occlusion against and
            // nothing to wait for. It is available immediately and reported as a coordinate.
            endpoint.Point = point;
            endpoint.Resolved = true;
            return true;
        }

        /// <summary>
        /// A coordinate endpoint, written either literally as <c>[x, y]</c> or as <c>"@name"</c> —
        /// a value an earlier step bound with <c>as:</c>, typically a <c>runScript</c> that returns
        /// a <c>Vector2</c>.
        ///
        /// The reference form is how a flow aims at geometry it cannot know when it is written. A
        /// literal coordinate is only correct at the Game View size it was measured at, so a
        /// regression built out of literals is one two-pixel resize away from dragging somewhere
        /// else entirely and failing for a reason that has nothing to do with the game. Reading the
        /// live rect the game itself uses and handing the point straight to the verb removes that
        /// whole class of false failure without weakening anything: the gesture is still a real
        /// pointer at a real screen coordinate.
        ///
        /// Two forms are accepted. A <see cref="UnityEngine.Vector2"/> is the direct one: a binder
        /// inside the editor hands one over, and so now does a <c>runScript</c> that returns one —
        /// scripts used to be executed by the pipeline's HTTP evaluator, which JSON round-tripped
        /// every value it returned and turned a Vector2 into Unity's own ToString, rounded to two
        /// decimals and punctuated by the editor's culture (a comma, on a pt-BR editor). UnityFlow
        /// compiles and runs its own scripts now and there is no wire to cross, so the value arrives
        /// as itself.
        ///
        /// The second form is the string <c>"x,y"</c> in invariant culture, which is what the flows
        /// in this repository write. It is still accepted, and still deliberately exact.
        ///
        /// Nothing else is coerced. A Vector3, a rounded "(875.00, 212.00)" or anything else is
        /// reported with the fix, because a guess at what some other pair of numbers meant would
        /// aim a real gesture at a place nobody wrote down.
        /// </summary>
        private static bool TryReadPoint(StepContext ctx, string pointKey, FlowArgument argument, out UnityEngine.Vector2 point)
        {
            point = default;

            if (!argument.IsReference)
            {
                if (!(argument.Value is UnityEngine.Vector2 literal))
                {
                    ctx.Fail($"'{pointKey}' on line {argument.Line} is " +
                             $"{(argument.Value == null ? "null" : argument.Value.GetType().Name)}, not a screen " +
                             "coordinate. Write it as [x, y].");
                    return false;
                }

                point = literal;
                return true;
            }

            if (!ctx.Resolver.TryGetValue(argument.Reference, out var bound))
            {
                ctx.Fail($"'{pointKey}' on line {argument.Line} is the reference '@{argument.Reference}', and no " +
                         "earlier step bound a VALUE under that name. A coordinate reference reads what a step's " +
                         "'as:' bound — a runScript returning the point as \"x,y\". A UI node bound by 'as:' is not " +
                         "one; drag from a node by naming it in the selector form instead.",
                    ctx.BuildDiagnostics());
                return false;
            }

            if (bound is UnityEngine.Vector2 vector)
            {
                point = vector;
                return true;
            }

            if (bound is string text)
            {
                if (TryParseWirePoint(text, out point))
                    return true;

                ctx.Fail($"'{pointKey}' on line {argument.Line} resolved '@{argument.Reference}' to the string " +
                         $"\"{Truncate(text, 60)}\", which is not a coordinate. A runScript's return value is JSON " +
                         "round-tripped on its way back, so a Vector2 arrives as its ToString — rounded to two " +
                         "decimals, and punctuated by the editor's culture. Write the point out yourself instead: " +
                         "point.x.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + " +
                         "point.y.ToString(System.Globalization.CultureInfo.InvariantCulture).");
                return false;
            }

            ctx.Fail($"'{pointKey}' on line {argument.Line} resolved '@{argument.Reference}' to " +
                     $"{(bound == null ? "null" : bound.GetType().FullName)}, and one end of a drag is a screen " +
                     "coordinate. Bind a UnityEngine.Vector2, or — from a runScript, whose return value is JSON " +
                     "round-tripped — the string \"x,y\" in invariant culture.");
            return false;
        }

        /// <summary>
        /// Read <c>"x,y"</c>, invariant culture, nothing else. Not a lenient parser on purpose: this
        /// is a wire format between two steps of one file, and every string it refuses has a
        /// specific fix that the caller's message names.
        /// </summary>
        private static bool TryParseWirePoint(string text, out UnityEngine.Vector2 point)
        {
            point = default;

            var comma = text.IndexOf(',');
            if (comma <= 0 || comma == text.Length - 1 || text.IndexOf(',', comma + 1) >= 0)
                return false;

            const System.Globalization.NumberStyles style = System.Globalization.NumberStyles.Float;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            if (!float.TryParse(text.Substring(0, comma), style, invariant, out var x) ||
                !float.TryParse(text.Substring(comma + 1), style, invariant, out var y))
            {
                return false;
            }

            // An infinite or NaN coordinate would be injected as a pointer position and quietly do
            // nothing at all, which is the one outcome worse than a refusal.
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
                return false;

            point = new UnityEngine.Vector2(x, y);
            return true;
        }

        /// <summary>
        /// One frame's work on an endpoint: resolve, check it is actionable, hold it stable, find the
        /// injection point and prove nothing covers it. Exactly <see cref="TapOn"/>'s sequence,
        /// because an endpoint of a drag has to be as reachable as a tap target — a drag started on
        /// something behind a modal is the same lie.
        ///
        /// <paramref name="fatal"/> separates "not yet" from "never": a selector that cannot resolve
        /// on principle, or an element with no screen coordinate at all, will not fix itself and
        /// retrying it until the deadline only delays the report.
        /// </summary>
        private static bool TryAdvanceEndpoint(StepContext ctx, DragEndpoint endpoint, out bool fatal)
        {
            fatal = false;

            var resolution = ctx.Resolver.Resolve(endpoint.Selector);
            if (!resolution.IsResolved)
            {
                endpoint.Reason = resolution.Message;
                endpoint.Stable = 0;
                fatal = !resolution.IsRetryable;
                return false;
            }

            var node = resolution.Node;
            var backend = ctx.Registry.ForHandle(node.Handle);

            if (!backend.IsActionable(node.Handle, out var reason))
            {
                endpoint.Reason = $"{node.Path} is not actionable: {reason}";
                endpoint.Stable = 0;
                return false;
            }

            if (++endpoint.Stable < StabilityFrames)
                return false;

            if (!backend.TryResolveInjectionPoint(node.Handle, out var point, out reason))
            {
                endpoint.Reason = $"'{endpoint.Key}' {endpoint.Selector} resolved to {node.Path} but no pointer position " +
                                  $"can reach it: {reason}";
                fatal = true;
                return false;
            }

            var hit = backend.HitTest(node.Handle, point);

            if (hit.Outcome == HitOutcome.Occluded)
            {
                endpoint.Reason = $"'{endpoint.Key}' {endpoint.Selector} resolved to {node.Path}, but it is obscured by {hit.HitPath}";
                endpoint.Stable = 0;
                return false;
            }

            if (hit.Outcome == HitOutcome.Unavailable)
            {
                endpoint.Reason = $"refusing to drag {endpoint.Key} {node.Path}: occlusion could not be verified " +
                                  $"({hit.HitPath}). A drag needs play mode, where the live EventSystem answers the raycast.";
                fatal = true;
                return false;
            }

            if (hit.Outcome == HitOutcome.NoHit)
            {
                endpoint.Reason = $"'{endpoint.Key}' {endpoint.Selector} resolved to {node.Path}, but the pointer position hit nothing at all";
                endpoint.Stable = 0;
                return false;
            }

            endpoint.Point = point;
            endpoint.Path = node.Path;
            endpoint.Handle = node.Handle;
            endpoint.Resolved = true;
            return true;
        }

        /// <summary>
        /// Refuse a gesture that could never be a drag.
        ///
        /// Two endpoints closer together than <c>pixelDragThreshold</c> produce a press and a release
        /// that uGUI never promotes to a drag, which means the flow would be reporting a pass for a
        /// gesture the UI saw as a click.
        /// </summary>
        private static bool TryRequireTravel(StepContext ctx, DragEndpoint source, DragEndpoint target, int threshold)
        {
            var distance = UnityEngine.Vector2.Distance(source.Point, target.Point);
            if (distance > threshold)
                return true;

            ctx.Fail(
                $"drag would travel {distance:0.#}px, from {source.Path ?? Format(source.Point)} to " +
                $"{target.Path ?? Format(target.Point)}, and EventSystem.pixelDragThreshold is {threshold}px. " +
                "uGUI only begins a drag once the pointer has moved further than that while pressed, so this gesture " +
                "would raise no IBeginDragHandler, no IDragHandler and no IDropHandler — it would be a click whose " +
                "release landed slightly to one side. The two endpoints are too close together to be dragged between.",
                ctx.BuildDiagnostics());
            return false;
        }

        /// <summary>
        /// Where to take the pointer when the target has to be found mid-drag: past the drag
        /// threshold, toward the middle of the screen so the pointer cannot travel off it.
        /// </summary>
        private static UnityEngine.Vector2 PrimeTarget(UnityEngine.Vector2 from, int threshold)
        {
            var centre = new UnityEngine.Vector2(UnityEngine.Screen.width * 0.5f, UnityEngine.Screen.height * 0.5f);
            var toCentre = centre - from;
            var distance = threshold * 2f + 8f;

            // Dead centre is the one place the direction is undefined; any direction is as good as
            // another there, and +x keeps it on screen for every plausible resolution.
            if (toCentre.sqrMagnitude < 1f)
                return new UnityEngine.Vector2(from.x + distance, from.y);

            return from + toCentre.normalized * distance;
        }

        /// <summary>
        /// Read <c>EventSystem.pixelDragThreshold</c> from the live EventSystem.
        ///
        /// By reflection, because this assembly deliberately does not reference UnityEngine.UI — the
        /// runner is UI-system agnostic and the uGUI knowledge lives in its backend. Reading the real
        /// value rather than assuming uGUI's default of 10 matters: a project is free to raise it,
        /// and a hard-coded number would turn a refusal into a drag that silently does nothing.
        /// Looked up once per step, never on the retry path.
        /// </summary>
        private static bool TryReadDragThreshold(StepContext ctx, out int threshold)
        {
            threshold = 0;

            var type = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI", throwOnError: false);
            if (type == null)
            {
                ctx.Fail("drag cannot run: UnityEngine.EventSystems.EventSystem is not loaded, so no uGUI input module " +
                         "exists to turn pointer motion into drag events at all.");
                return false;
            }

            var currentProperty = type.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            var thresholdProperty = type.GetProperty("pixelDragThreshold", BindingFlags.Public | BindingFlags.Instance);

            if (currentProperty == null || thresholdProperty == null)
            {
                ctx.Fail("drag cannot run: UnityEngine.EventSystems.EventSystem no longer exposes both 'current' and " +
                         "'pixelDragThreshold', so the distance a drag must travel before uGUI begins one cannot be read. " +
                         "UnityFlow needs updating for this Unity version.");
                return false;
            }

            var current = currentProperty.GetValue(null);
            if (current == null)
            {
                ctx.Fail("drag cannot run: EventSystem.current is null, so nothing is dispatching pointer events. " +
                         "Injected motion would reach no handler at all.",
                    ctx.BuildDiagnostics());
                return false;
            }

            threshold = (int)thresholdProperty.GetValue(current);
            return true;
        }

        private static string Format(UnityEngine.Vector2 point) =>
            $"({point.x:0.#}, {point.y:0.#})";

        private static IEnumerator InputText(StepContext ctx)
        {
            var text = ctx.Step.Get<string>("text");
            string lastReason = null;

            while (!ctx.DeadlineReached)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (!resolution.IsResolved)
                {
                    if (!resolution.IsRetryable)
                    {
                        ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                        yield break;
                    }

                    lastReason = resolution.Message;
                    yield return null;
                    continue;
                }

                var node = resolution.Node;
                var backend = ctx.Registry.ForHandle(node.Handle);

                if (!backend.IsActionable(node.Handle, out lastReason))
                {
                    yield return null;
                    continue;
                }

                if (!backend.TrySetText(node.Handle, text, out var error))
                {
                    ctx.Fail($"could not enter text into {node.Path}: {error}", ctx.BuildDiagnostics());
                    yield break;
                }

                if (ctx.Step.As != null)
                    ctx.Resolver.Bind(ctx.Step.As, node.Handle);

                yield return null;
                yield break;
            }

            ctx.Fail($"inputText {ctx.Step.Selector} timed out after {Describe(ctx)}: {lastReason ?? "never became actionable"}",
                ctx.BuildDiagnostics());
        }

        // ---- Keyboard and navigation -------------------------------------------------------

        /// <summary>
        /// Send one or more real key presses through the injected keyboard.
        ///
        /// Deliberately dumb: it presses the key and says nothing about what should happen. Every
        /// assertion about the consequence belongs to the verbs around it, which is what keeps this
        /// usable for gameplay keys as well as UI ones.
        /// </summary>
        private static IEnumerator Press(StepContext ctx)
        {
            var key = ctx.Step.Get<string>("key");
            var count = ctx.Step.Has("count") ? ctx.Step.Get<int>("count") : 1;
            var hold = ctx.Step.Has("duration") ? ctx.Step.Get<TimeSpan>("duration").TotalSeconds : 0.0;

            if (count < 1)
            {
                ctx.Fail($"press was given count: {count}. A press count below 1 sends nothing at all, which is a " +
                         "step that silently does no work — write the step or delete it.");
                yield break;
            }

            if (!TryRequireDeviceInput(ctx, out var input))
                yield break;

            for (var i = 0; i < count; i++)
            {
                // Validated by pressing it: the driver owns the key table (it is built from the live
                // device), so asking it is the only answer that cannot drift from what will actually
                // be injected. Nothing is queued when the name is rejected.
                if (!TryPressKey(ctx, input, key))
                    yield break;

                // The flush carries the POINTER; the key travels on the frame that follows it. The
                // driver hands keyboard state to the player loop's own input update precisely so the
                // press is applied in the update step the game's Update then reads, which is what makes
                // a GetKeyDown shortcut reachable at all (InputSystemDriver, OnBeforeInputUpdate). The
                // yield below is therefore the step that delivers the key, not padding.
                input.Flush();
                yield return null;

                // A held key is what reaches press-and-hold behaviour, including uGUI's navigation
                // auto-repeat. Without it every press is one discrete event, which is what a
                // navigation step wants and what the default therefore is.
                if (hold > 0.0)
                    yield return Flow.Seconds(hold);

                input.ReleaseKey(key);
                input.Flush();
                yield return null;
            }
        }

        /// <summary>
        /// Walk uGUI's navigation graph to the target with REAL arrow keys.
        ///
        /// The rule that shapes everything here: the selection is only ever moved by injecting a key
        /// and re-reading <c>EventSystem.current.currentSelectedGameObject</c>. Nothing calls
        /// <c>SetSelectedGameObject</c> to get closer to the target — that is not input, it would
        /// make an unreachable element look reachable, and an unreachable element is a real bug for
        /// every keyboard and controller player. The single exception is seeding the FIRST selection
        /// when the ring is empty, which is reported on the progress stream as an assist.
        ///
        /// That is also why this verb doubles as an accessibility check: when it cannot get there, it
        /// reports the path the selection actually took and the <c>Selectable.navigation</c> wiring
        /// that stopped it, which is the finding rather than the excuse.
        /// </summary>
        private static IEnumerator NavigateTo(StepContext ctx)
        {
            var maxSteps = ctx.Step.Has("maxSteps") ? ctx.Step.Get<int>("maxSteps") : DefaultMaxNavigationSteps;
            if (maxSteps < 1)
            {
                ctx.Fail($"navigateTo was given maxSteps: {maxSteps}, which allows no key press at all; it could only " +
                         "ever pass when the target is already selected, which is an assertion and not a navigation.");
                yield break;
            }

            if (!TryRequireDeviceInput(ctx, out var input))
                yield break;

            var target = default(UiNode);
            IUiBackend backend = null;
            string lastReason = null;

            while (!ctx.DeadlineReached && backend == null)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (resolution.IsResolved)
                {
                    target = resolution.Node;
                    backend = ctx.Registry.ForHandle(target.Handle);
                    break;
                }

                if (!resolution.IsRetryable)
                {
                    ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                    yield break;
                }

                lastReason = resolution.Message;
                yield return null;
            }

            if (backend == null)
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} timed out after {Describe(ctx)}: {lastReason ?? "the target never appeared"}",
                    ctx.BuildDiagnostics());
                yield break;
            }

            var ring = backend.FocusRing;
            if (ring == null)
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} resolved to {target.Path}, but the '{backend.Id}' backend exposes " +
                         "no focus ring, so it has no navigation graph to walk and no way to report what is selected.",
                    ctx.BuildDiagnostics());
                yield break;
            }

            if (!ring.IsAvailable(out var ringReason))
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} cannot run: {ringReason}", ctx.BuildDiagnostics());
                yield break;
            }

            // Asked once, before a single key is pressed: a target that is not in the graph at all can
            // never be reached, and reporting that immediately is worth far more than reporting it
            // after 40 futile presses.
            if (!ring.TryDescribeLink(target.Handle, FocusDirection.Up, out _, out var targetReason))
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} resolved to {target.Path}, but it is not part of the " +
                         $"navigation graph: {targetReason}", ctx.BuildDiagnostics());
                yield break;
            }

            var currentHandle = UiHandle.None;
            string currentPath = null;

            if (!ring.TryGetFocused(out currentHandle, out currentPath))
            {
                if (!TryEstablishStart(ctx, ring, backend, in target, out currentHandle, out currentPath))
                    yield break;

                // The EventSystem raises OnSelect from its own Update, and a selection handler
                // routinely scrolls the list or rebuilds a highlight. Read geometry after it, never
                // in the same frame as the assignment.
                yield return null;
            }
            else if (currentHandle.IsNone)
            {
                // Something holds the selection that this backend does not enumerate. The walk needs
                // a handle to compare against the target and to detect a cycle, so it cannot start
                // here — and saying so is far more useful than the "lost the selected node" the
                // geometry step would otherwise report.
                ctx.Fail($"navigateTo {ctx.Step.Selector} cannot start: '{currentPath}' holds the selection but the " +
                         $"'{backend.Id}' backend does not enumerate it as a node (it has no RectTransform under a " +
                         "known Canvas), so there is nothing to navigate from.",
                    ctx.BuildDiagnostics());
                yield break;
            }

            var startPath = currentPath;
            var visited = new List<UiHandle>(maxSteps + 1);
            var trail = new List<string>(maxSteps + 1);
            visited.Add(currentHandle);
            trail.Add(currentPath);

            // Which of the two candidate axes has already been spent FROM THE CURRENT ELEMENT. A
            // press that moves nothing is information, not a failure: an element wired Horizontal
            // simply does not travel vertically, and the other axis is the next thing to try. Both
            // spent with no movement is a genuine dead end and is reported as one.
            var triedPrimary = false;
            var triedSecondary = false;
            var steps = 0;

            while (steps < maxSteps)
            {
                if (currentHandle == target.Handle)
                {
                    if (ctx.Step.As != null)
                        ctx.Resolver.Bind(ctx.Step.As, target.Handle);

                    yield break;
                }

                if (ctx.DeadlineReached)
                {
                    ctx.Fail(BuildNavigationFailure(ctx, ring, in target, trail, cycle: false, currentHandle,
                            currentPath, startPath, steps,
                            $"ran out of time after {Describe(ctx)} and {steps} of its {maxSteps} presses"),
                        ctx.BuildDiagnostics());
                    yield break;
                }

                if (!TryChooseDirections(ctx, backend, in target, currentHandle, currentPath, out var primary, out var secondary))
                    yield break;

                FocusDirection direction;
                if (!triedPrimary)
                {
                    direction = primary;
                    triedPrimary = true;
                }
                else if (!triedSecondary)
                {
                    direction = secondary;
                    triedSecondary = true;
                }
                else
                {
                    ctx.Fail(BuildNavigationFailure(ctx, ring, in target, trail, cycle: false, currentHandle,
                            currentPath, startPath, steps,
                            $"is stuck on {currentPath}: pressing {KeyFor(primary)} and then {KeyFor(secondary)} " +
                            "moved the selection nowhere"),
                        ctx.BuildDiagnostics());
                    yield break;
                }

                steps++;

                var key = KeyFor(direction);
                if (!TryPressKey(ctx, input, key))
                    yield break;

                input.Flush();
                yield return null;

                input.ReleaseKey(key);
                input.Flush();
                yield return null;

                // The Move is dispatched from InputSystemUIInputModule.Process() on the frame the key
                // is down; this third frame is for whatever the newly selected element does in its own
                // OnSelect — scrolling itself into view is the common one, and reading its rect before
                // that lands would pick the next direction from stale geometry.
                yield return null;

                backend.Settle();

                if (!ring.TryGetFocused(out var nextHandle, out var nextPath))
                {
                    ctx.Fail($"navigateTo {ctx.Step.Selector} pressed {key} on {currentPath} and the selection was CLEARED: " +
                             "nothing is selected any more. uGUI clears the selection when the selected object is " +
                             "deactivated or destroyed, so the element the path was standing on did not survive the move.",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                if (nextHandle.IsNone)
                {
                    ctx.Fail($"navigateTo {ctx.Step.Selector} pressed {key} on {currentPath} and the selection moved to " +
                             $"'{nextPath}', which the '{backend.Id}' backend does not enumerate as a node (it has no " +
                             "RectTransform under a known Canvas), so the walk cannot continue from there.",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                if (nextHandle == currentHandle)
                    continue;

                currentHandle = nextHandle;
                currentPath = nextPath;
                triedPrimary = false;
                triedSecondary = false;

                if (Contains(visited, nextHandle))
                {
                    trail.Add(nextPath);
                    ctx.Fail(BuildNavigationFailure(ctx, ring, in target, trail, cycle: true, currentHandle,
                            currentPath, startPath, steps,
                            $"could not reach it from {startPath} in {steps} steps"),
                        ctx.BuildDiagnostics());
                    yield break;
                }

                visited.Add(nextHandle);
                trail.Add(nextPath);
            }

            if (currentHandle == target.Handle)
            {
                if (ctx.Step.As != null)
                    ctx.Resolver.Bind(ctx.Step.As, target.Handle);

                yield break;
            }

            ctx.Fail(BuildNavigationFailure(ctx, ring, in target, trail, cycle: false, currentHandle, currentPath,
                    startPath, steps, $"could not reach it from {startPath} in {maxSteps} steps"),
                ctx.BuildDiagnostics());
        }

        /// <summary>
        /// Send the UI's Submit or Cancel action to the current selection.
        ///
        /// The selector is an ASSERTION, not a target. Resolving it and then submitting to it would
        /// be a second way of activating a control that bypasses the selection entirely; making it
        /// assert instead means the step can only ever confirm that the thing about to receive Enter
        /// is the thing the flow named.
        /// </summary>
        private static IEnumerator SubmitOrCancel(StepContext ctx, bool submit)
        {
            var verb = ctx.Step.Verb;
            var key = submit ? SubmitKey : CancelKey;
            var action = submit ? "Submit" : "Cancel";

            if (!TryRequireDeviceInput(ctx, out var input))
                yield break;

            var expected = default(UiNode);
            var hasExpected = ctx.Step.Selector != null;
            IUiBackend backend = null;

            if (hasExpected)
            {
                string lastReason = null;

                while (!ctx.DeadlineReached && backend == null)
                {
                    var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                    if (resolution.IsResolved)
                    {
                        expected = resolution.Node;
                        backend = ctx.Registry.ForHandle(expected.Handle);
                        break;
                    }

                    if (!resolution.IsRetryable)
                    {
                        ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                        yield break;
                    }

                    lastReason = resolution.Message;
                    yield return null;
                }

                if (backend == null)
                {
                    ctx.Fail($"{verb} {ctx.Step.Selector} timed out after {Describe(ctx)}: {lastReason ?? "it never appeared"}",
                        ctx.BuildDiagnostics());
                    yield break;
                }
            }
            else
            {
                backend = FindFocusBackend(ctx, out var rejection);
                if (backend == null)
                {
                    ctx.Fail($"{verb} has no UI system that can tell it what is selected: {rejection}", ctx.BuildDiagnostics());
                    yield break;
                }
            }

            var ring = backend.FocusRing;
            if (ring == null)
            {
                ctx.Fail($"{verb} cannot run: the '{backend.Id}' backend exposes no focus ring, so it cannot say what is " +
                         "selected and the key would be sent blind.", ctx.BuildDiagnostics());
                yield break;
            }

            if (!ring.IsAvailable(out var ringReason))
            {
                ctx.Fail($"{verb} cannot run: {ringReason}", ctx.BuildDiagnostics());
                yield break;
            }

            if (!ring.TryGetFocused(out var focusedHandle, out var focusedPath))
            {
                ctx.Fail($"{verb} has nothing to act on: nothing is selected. The module raises {action} on " +
                         "EventSystem.current.currentSelectedGameObject, so with an empty selection the key press would " +
                         "reach no control at all. Put a navigateTo before this step.",
                    ctx.BuildDiagnostics());
                yield break;
            }

            if (hasExpected && focusedHandle != expected.Handle)
            {
                ctx.Fail($"{verb} {ctx.Step.Selector} refuses to act: it names {expected.Path}, but the current selection is " +
                         $"{focusedPath}. Sending {action} anyway would activate the wrong control and the run would report " +
                         "a pass for something that never happened.",
                    ctx.BuildDiagnostics());
                yield break;
            }

            if (!TryPressKey(ctx, input, key))
                yield break;

            input.Flush();
            yield return null;

            input.ReleaseKey(key);
            input.Flush();
            yield return null;

            // Whatever the submit triggered is raised inside the Process() that observed the release,
            // so anything it changes only exists from the following frame.
            yield return null;

            if (ctx.Step.As != null && hasExpected)
                ctx.Resolver.Bind(ctx.Step.As, expected.Handle);
        }

        /// <summary>
        /// Refuse anything but real device input for the keyboard verbs.
        ///
        /// Semantic dispatch can synthesize a pointer click, but there is no honest semantic
        /// equivalent of a navigation key: pushing an <c>AxisEventData</c> straight into the
        /// EventSystem would skip the project's own action bindings, so it would prove that uGUI can
        /// move a selection and nothing about whether the game's own keyboard bindings can.
        /// </summary>
        private static bool TryRequireDeviceInput(StepContext ctx, out IInputDriver input)
        {
            if (ctx.WriteMode == WriteMode.DeviceInjection)
            {
                input = ctx.Registry.InputDriver;
                return true;
            }

            input = null;
            ctx.Fail($"{ctx.Step.Verb} needs a real keyboard device, and this run resolved to semantic dispatch instead " +
                     $"({ctx.Registry.InputDriverRejection}). Synthesizing the navigation event directly would bypass the " +
                     "project's own action bindings and prove nothing about a player's keyboard, so it is refused rather " +
                     "than downgraded.",
                ctx.BuildDiagnostics());
            return false;
        }

        /// <summary>
        /// Press a key, turning the driver's refusal of an unknown name into a step failure.
        ///
        /// The driver is the only thing that knows which key names exist — its table is built from
        /// the live device at session start — so validation has to be a real call to it. It throws
        /// before queueing anything, so a rejected name leaves no half-pressed key behind.
        /// </summary>
        private static bool TryPressKey(StepContext ctx, IInputDriver input, string key)
        {
            try
            {
                input.PressKey(key);
                return true;
            }
            catch (ArgumentException ex)
            {
                ctx.Fail($"{ctx.Step.Verb} cannot send the key '{key}': {WithoutParameterNote(ex.Message)}");
                return false;
            }
        }

        /// <summary>
        /// Drop the parameter-name tail ArgumentException appends to its own message.
        ///
        /// The tail is correct for an API contract and pure noise in a flow report — it names a C#
        /// parameter to a reader who wrote YAML. Both spellings are handled because the runtime
        /// decides which one it uses: Mono appends "\nParameter name: key", .NET appends
        /// " (Parameter 'key')", and this package is compiled for both.
        /// </summary>
        private static string WithoutParameterNote(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            var mono = message.IndexOf("\nParameter name:", StringComparison.Ordinal);
            if (mono >= 0)
                return message.Substring(0, mono);

            var net = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
            return net >= 0 ? message.Substring(0, net) : message;
        }

        /// <summary>The arrow key that produces this Move direction through the project's bindings.</summary>
        private static string KeyFor(FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up: return "upArrow";
                case FocusDirection.Down: return "downArrow";
                case FocusDirection.Left: return "leftArrow";
                default: return "rightArrow";
            }
        }

        /// <summary>
        /// Seed the selection when the ring is empty, and put it on the record.
        ///
        /// This is the only place in the runner allowed to move a selection without input, and it is
        /// hedged three ways: the ring itself refuses once anything is selected, starting ON the
        /// target is refused here because it would report a pass without a single key press, and the
        /// assist is written to the progress stream so no reader can mistake the result for a fully
        /// navigated path.
        /// </summary>
        private static bool TryEstablishStart(StepContext ctx, IFocusRing ring, IUiBackend backend,
            in UiNode target, out UiHandle handle, out string path)
        {
            handle = UiHandle.None;
            path = null;

            if (ctx.Step.TryGet<Selector>("from", out var fromSelector))
            {
                var resolution = ctx.Resolver.Resolve(fromSelector);
                if (!resolution.IsResolved)
                {
                    ctx.Fail($"navigateTo {ctx.Step.Selector} could not start: its 'from' selector {fromSelector} did not " +
                             $"resolve: {resolution.Message}", ctx.BuildDiagnostics(), resolution.NearMisses);
                    return false;
                }

                handle = resolution.Node.Handle;
                path = resolution.Node.Path;
            }
            else if (ring.TryGetDefaultFocus(target.Handle.SurfaceId, out handle, out var defaultReason))
            {
                path = backend.TryGetNode(handle, out var node) ? node.Path : handle.ToString();
            }
            else
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} could not start: nothing is selected, and {defaultReason}. " +
                         "Add 'from: { ... }' to say which element the keyboard should start on.",
                    ctx.BuildDiagnostics());
                return false;
            }

            if (handle == target.Handle)
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} refuses to start on the target itself ({target.Path}). The first " +
                         "selection is established with EventSystem.SetSelectedGameObject, which is not input, so starting " +
                         "there would report a pass without a single key press. Point 'from' at a different element.",
                    ctx.BuildDiagnostics());
                return false;
            }

            if (!ring.TryEstablishInitialFocus(handle, out var error))
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} could not establish the initial selection on {path}: {error}",
                    ctx.BuildDiagnostics());
                return false;
            }

            ctx.Note("step.assist", new[]
            {
                Pair("verb", "navigateTo"),
                Pair("mechanism", "EventSystem.SetSelectedGameObject"),
                Pair("selected", path),
                Pair("target", target.Path),
                Pair("message",
                    $"nothing was selected, so the initial selection was ESTABLISHED on '{path}' without input; " +
                    "every move from there to the target is a real key press")
            });

            return true;
        }

        /// <summary>
        /// Decide which way to press, from where the two elements actually are on screen.
        ///
        /// The screen rect is used for a DIRECTION, never for a coordinate — the caution that makes
        /// <see cref="UiNode.ScreenRect"/> reporting-only is about a rotated element's bounds not
        /// being a place to click, and it does not apply to "is the target above or below". Screen
        /// space and uGUI's MoveDirection agree that +y is up, so the sign maps straight across.
        ///
        /// The larger axis goes first because that is the one that can close most of the distance;
        /// the other is returned as the second attempt, so a graph wired Horizontal-only still gets
        /// its horizontal press.
        /// </summary>
        private static bool TryChooseDirections(StepContext ctx, IUiBackend backend, in UiNode target,
            UiHandle currentHandle, string currentPath, out FocusDirection primary, out FocusDirection secondary)
        {
            primary = FocusDirection.Down;
            secondary = FocusDirection.Right;

            if (!backend.TryGetNode(currentHandle, out var currentNode))
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} lost the currently selected node {currentPath}: its handle no " +
                         "longer resolves, so the UI rebuilt or destroyed it while the selection was standing on it.",
                    ctx.BuildDiagnostics());
                return false;
            }

            if (!backend.TryGetNode(target.Handle, out var targetNode))
            {
                ctx.Fail($"navigateTo {ctx.Step.Selector} lost the target {target.Path} while navigating: its handle no " +
                         "longer resolves, so it was destroyed or rebuilt mid-walk.", ctx.BuildDiagnostics());
                return false;
            }

            if (currentNode.ScreenRect == null || targetNode.ScreenRect == null)
            {
                var offender = currentNode.ScreenRect == null ? currentNode : targetNode;
                ctx.Fail($"navigateTo {ctx.Step.Selector} cannot tell which way to press: {offender.Path} has no screen " +
                         $"rect ({offender.Reason ?? "it is on a world-space or render-texture surface"}), and the " +
                         "direction is chosen by comparing the two elements' positions on screen.",
                    ctx.BuildDiagnostics());
                return false;
            }

            var from = currentNode.ScreenRect.Value.center;
            var to = targetNode.ScreenRect.Value.center;
            var dx = to.x - from.x;
            var dy = to.y - from.y;

            var horizontal = dx >= 0f ? FocusDirection.Right : FocusDirection.Left;
            var vertical = dy >= 0f ? FocusDirection.Up : FocusDirection.Down;

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                primary = horizontal;
                secondary = vertical;
            }
            else
            {
                primary = vertical;
                secondary = horizontal;
            }

            return true;
        }

        /// <summary>
        /// The failure that is the point of the verb: where the selection went, and what wiring
        /// stopped it going further.
        ///
        /// It reports the navigation of the element the walk got STUCK on, in all four directions,
        /// because "it did not move" is never actionable and "selectOnDown = None" is a field an
        /// author can go and fill in.
        /// </summary>
        private static string BuildNavigationFailure(StepContext ctx, IFocusRing ring, in UiNode target,
            List<string> trail, bool cycle, UiHandle stuckHandle, string stuckPath, string startPath, int steps,
            string headline)
        {
            var sb = new StringBuilder(512);

            sb.Append("navigateTo ").Append(ctx.Step.Selector).Append(' ').Append(headline);
            sb.Append("; the navigation path visited ");
            AppendTrail(sb, trail);

            if (cycle)
                sb.Append(" (a cycle)");

            sb.Append('.');

            var stuckName = LeafOf(stuckPath);

            if (ring.TryDescribeLink(stuckHandle, FocusDirection.Up, out _, out var stuckReason))
            {
                sb.Append(" Selectable.navigation on '").Append(stuckName).Append("' is:");
                AppendLink(sb, ring, stuckHandle, FocusDirection.Up);
                AppendLink(sb, ring, stuckHandle, FocusDirection.Down);
                AppendLink(sb, ring, stuckHandle, FocusDirection.Left);
                AppendLink(sb, ring, stuckHandle, FocusDirection.Right);
            }
            else
            {
                sb.Append(" The navigation of '").Append(stuckName).Append("' cannot be read: ").Append(stuckReason);
            }

            sb.Append("\n  target: ").Append(target.Path);
            sb.Append("\n  started on: ").Append(startPath);
            sb.Append("\n  presses sent: ").Append(steps);
            sb.Append("\n  An element the navigation graph cannot reach is unusable with a keyboard or a controller, " +
                      "so this is a UI defect and not a test artefact.");

            return sb.ToString();
        }

        private static void AppendLink(StringBuilder sb, IFocusRing ring, UiHandle handle, FocusDirection direction)
        {
            sb.Append("\n    ").Append(NameOf(direction)).Append(": ");

            if (!ring.TryDescribeLink(handle, direction, out var link, out var reason))
            {
                sb.Append(reason);
                return;
            }

            if (link.HasTarget)
                sb.Append(link.Mode).Append(" -> ").Append(link.TargetPath);
            else
                sb.Append(link.Missing);
        }

        private static void AppendTrail(StringBuilder sb, List<string> trail)
        {
            for (var i = 0; i < trail.Count; i++)
            {
                if (i > 0)
                    sb.Append(" -> ");

                sb.Append(LeafOf(trail[i]));
            }
        }

        /// <summary>Last segment of a hierarchy path. Full paths make a five-hop trail unreadable.</summary>
        private static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "<unknown>";

            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
        }

        private static string NameOf(FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up: return "up";
                case FocusDirection.Down: return "down";
                case FocusDirection.Left: return "left";
                default: return "right";
            }
        }

        private static bool Contains(List<UiHandle> handles, UiHandle handle)
        {
            for (var i = 0; i < handles.Count; i++)
            {
                if (handles[i] == handle)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The backend that can answer "what is selected" when the step named no element. Reports
        /// every backend it rejected and why, because "no focus ring anywhere" and "the ring exists
        /// but needs play mode" send a reader to completely different places.
        /// </summary>
        private static IUiBackend FindFocusBackend(StepContext ctx, out string rejection)
        {
            var reasons = new StringBuilder();

            for (var i = 0; i < ctx.Registry.Active.Count; i++)
            {
                var backend = ctx.Registry.Active[i];
                var ring = backend.FocusRing;

                if (ring == null)
                {
                    Append(reasons, $"{backend.Id}: exposes no focus ring");
                    continue;
                }

                if (!ring.IsAvailable(out var reason))
                {
                    Append(reasons, $"{backend.Id}: {reason}");
                    continue;
                }

                rejection = null;
                return backend;
            }

            rejection = reasons.Length == 0 ? "no UI backend is active at all" : reasons.ToString();
            return null;

            void Append(StringBuilder sb, string text)
            {
                if (sb.Length > 0)
                    sb.Append("; ");

                sb.Append(text);
            }
        }

        private static KeyValuePair<string, object> Pair(string key, object value) =>
            new KeyValuePair<string, object>(key, value);

        private static IEnumerator WaitForVisible(StepContext ctx, bool assertion)
        {
            string lastReason = null;

            while (!ctx.DeadlineReached)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (resolution.IsResolved && resolution.Node.IsVisible)
                {
                    if (ctx.Step.As != null)
                        ctx.Resolver.Bind(ctx.Step.As, resolution.Node.Handle);

                    yield break;
                }

                if (!resolution.IsResolved && !resolution.IsRetryable)
                {
                    ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                    yield break;
                }

                lastReason = resolution.IsResolved
                    ? resolution.Node.Reason ?? "resolved but not visible"
                    : resolution.Message;

                yield return null;
            }

            var verb = assertion ? "assertVisible" : "waitFor";
            ctx.Fail($"{verb} {ctx.Step.Selector} timed out after {Describe(ctx)}: {lastReason}",
                ctx.BuildDiagnostics());
        }

        private static IEnumerator WaitUntilNotVisible(StepContext ctx)
        {
            while (!ctx.DeadlineReached)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (!resolution.IsResolved || !resolution.Node.IsVisible)
                    yield break;

                yield return null;
            }

            ctx.Fail($"waitUntilNotVisible {ctx.Step.Selector} timed out after {Describe(ctx)}: it is still visible",
                ctx.BuildDiagnostics());
        }

        /// <summary>
        /// A negative assertion, with the opposite retry semantics of a positive one.
        ///
        /// A positive assertion retries until it becomes true. A negative one that returned as soon
        /// as it looked true would be VACUOUS: 'assertNotVisible' immediately after the action that
        /// might trigger the popup passes before the system has even evaluated, and a bug that
        /// makes the popup appear 300ms later still shows green. So this requires the condition to
        /// HOLD for a window, and fails the instant it is violated.
        /// </summary>
        private static IEnumerator AssertNotVisible(StepContext ctx)
        {
            var stableFor = ctx.Step.Has("stableFor")
                ? ctx.Step.Get<TimeSpan>("stableFor")
                : DefaultStableFor;

            var start = FlowClock.Now;
            var window = stableFor.TotalSeconds;

            while (true)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);

                if (resolution.IsResolved && resolution.Node.IsVisible)
                {
                    var elapsed = (FlowClock.Now - start) * 1000.0;
                    ctx.Fail(
                        $"assertNotVisible {ctx.Step.Selector} failed: {resolution.Node.Path} became visible after {elapsed:F0}ms " +
                        $"(it had to stay absent for {window * 1000:F0}ms)",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                if (FlowClock.Now - start >= window)
                    yield break;

                yield return null;
            }
        }

        private static IEnumerator AssertText(StepContext ctx)
        {
            var equals = ctx.Step.Has("equals") ? ctx.Step.Get<string>("equals") : null;
            var contains = ctx.Step.Has("contains") ? ctx.Step.Get<string>("contains") : null;
            var matches = ctx.Step.Has("matches") ? ctx.Step.Get<string>("matches") : null;

            if (equals == null && contains == null && matches == null)
            {
                ctx.Fail("assertText needs one of 'equals', 'contains' or 'matches'");
                yield break;
            }

            Regex regex = null;
            if (matches != null)
            {
                try
                {
                    // Same bounded compilation as a selector's 'matches:': this one is re-evaluated
                    // every frame too, so an unbounded match here hangs the editor just as hard.
                    regex = Selector.CompileTextRegex(matches);
                }
                catch (ArgumentException ex)
                {
                    ctx.Fail($"assertText 'matches' is not a valid regular expression: {ex.Message}");
                    yield break;
                }
            }

            string lastSeen = null;

            while (!ctx.DeadlineReached)
            {
                var resolution = ctx.Resolver.Resolve(ctx.Step.Selector);
                if (!resolution.IsResolved)
                {
                    if (!resolution.IsRetryable)
                    {
                        ctx.Fail(resolution.Message, ctx.BuildDiagnostics(), resolution.NearMisses);
                        yield break;
                    }

                    yield return null;
                    continue;
                }

                lastSeen = resolution.Node.Text;

                var ok =
                    (equals == null || string.Equals(lastSeen, equals, StringComparison.Ordinal)) &&
                    (contains == null || (lastSeen != null && lastSeen.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)) &&
                    (regex == null || (lastSeen != null && regex.IsMatch(lastSeen)));

                if (ok)
                    yield break;

                yield return null;
            }

            var expectation = equals != null ? $"equals \"{equals}\""
                : contains != null ? $"contains \"{contains}\""
                : $"matches /{matches}/";

            ctx.Fail($"assertText {ctx.Step.Selector} timed out after {Describe(ctx)}: expected text to {expectation}, " +
                     $"but it is {(lastSeen == null ? "null" : $"\"{lastSeen}\"")}",
                ctx.BuildDiagnostics());
        }

        private static IEnumerator Screenshot(StepContext ctx)
        {
            var name = ctx.Step.Get<string>("name");

            // Let the frame finish rendering before grabbing the backbuffer; the driver ticks in
            // PostLateUpdate, so one more frame guarantees a fully composited image.
            yield return null;

            var path = ctx.Paths.Artifact(FlowCapture.SanitizeName(name));
            if (!FlowCapture.TryCapture(path, out var error))
                ctx.Fail($"screenshot '{name}' failed: {error}");
        }

        private static IEnumerator Wait(StepContext ctx)
        {
            var duration = ctx.Step.Get<TimeSpan>("duration");
            yield return Flow.Seconds(duration.TotalSeconds);
        }

        /// <summary>
        /// Enter play mode, which means TEARING THIS RUN DOWN and rebuilding it.
        ///
        /// Unless EditorSettings.enterPlayModeOptionsEnabled disables it, the transition does a full
        /// domain reload: this coroutine, the runner, the driver and the HTTP request that started
        /// the run all cease to exist part way through. The step therefore does its work in an
        /// order that is safe to be killed in:
        /// <list type="number">
        /// <item>refuse outright if the run has no way back (flow.run);</item>
        /// <item>preflight, so a refusal is reported before anything has changed;</item>
        /// <item>open the scene, recording what to put back;</item>
        /// <item>ARM the resume ledger — on disk — so the far side of the reload knows the run
        /// continues at the NEXT step;</item>
        /// <item>only then ask for play mode.</item>
        /// </list>
        /// The wait below normally never finishes: the domain goes down inside it and
        /// <see cref="FlowResumer"/> takes over. Reaching the end of it means the editor entered
        /// play mode WITHOUT a reload, and the run simply carries on here with the ledger disarmed.
        /// </summary>
        private static IEnumerator EnterPlayMode(StepContext ctx)
        {
            if (EditorApplication.isPlaying)
            {
                if (ctx.Step.Has("scene"))
                {
                    ctx.Fail("enterPlayMode was given 'scene:' but the editor is already in play mode, where opening a " +
                             "scene in the editor does nothing. Put the scene on the enterPlayMode that starts play mode, " +
                             "or exit play mode first.");
                }

                yield break;
            }

            if (ctx.Resume == null)
            {
                ctx.Fail("enterPlayMode needs a run that can survive a domain reload, and this one cannot. " +
                         "flow.run holds one HTTP request open for the whole run and dies with the domain; start this " +
                         "flow with flow.start and poll flow.status instead.");
                yield break;
            }

            if (!PlayModeGate.TryPreflight(out var preflight))
            {
                ctx.Fail($"enterPlayMode refused to start the transition: {preflight}");
                yield break;
            }

            if (ctx.Step.Has("scene"))
            {
                if (!PlayModeGate.TryOpenScene(ctx.Step.Get<string>("scene"), out var previousScene, out var sceneError))
                {
                    ctx.Fail($"enterPlayMode could not open the scene: {sceneError}");
                    yield break;
                }

                if (previousScene != null)
                {
                    ctx.Resume.SceneRestorePending = true;
                    ctx.Resume.SceneToRestore = previousScene;
                }

                // Loading a scene rebuilds the editor's object graph; let it settle before the
                // transition so play mode starts from a scene that has finished arriving.
                yield return null;
            }

            // The per-step default of 7s is not a play mode budget: the transition costs a domain
            // reload plus a scene load. 'timeout:' on the step overrides this, and it is measured
            // on the wall clock because no in-domain clock survives to the far side.
            var timeout = ctx.Step.Timeout ?? PlayModeGate.DefaultTransitionTimeout;

            ctx.Resume.Arm(ResumeGate.PlayMode, ctx.StepIndex + 1, ctx.Section, timeout.TotalSeconds);
            ctx.Resume.Save(ctx.Paths);

            if (!PlayModeGate.TryRequestPlayMode(out var requestError))
            {
                ctx.Fail($"enterPlayMode could not request play mode: {requestError}{Abandon(ctx)}");
                yield break;
            }

            var unacknowledged = 0;

            while (!EditorApplication.isPlaying)
            {
                if (FlowResumeState.NowUtc >= ctx.Resume.GateDeadlineUtc)
                {
                    ctx.Fail($"enterPlayMode timed out after {timeout.TotalSeconds:0.#}s: the editor never entered play mode " +
                             $"and never reloaded the domain either.{Abandon(ctx)}", ctx.BuildDiagnostics());
                    yield break;
                }

                if (!PlayModeGate.IsTransitionInFlight && ++unacknowledged >= PlayModeGate.AcknowledgementFrames)
                {
                    ctx.Fail($"Unity refused the play mode request: no transition was in flight for " +
                             $"{PlayModeGate.AcknowledgementFrames} frames after EnterPlaymode(). The usual cause is a " +
                             $"compiler error appearing between the preflight and the request.{Abandon(ctx)}",
                        ctx.BuildDiagnostics());
                    yield break;
                }

                yield return null;
            }

            ctx.Resume.Disarm();
            ctx.Resume.Save(ctx.Paths);
        }

        /// <summary>
        /// Leave play mode. Symmetric to <see cref="EnterPlayMode"/>, including the reload: the
        /// return to edit mode reloads the domain too, so this arms the ledger the same way.
        ///
        /// The scene the run replaced is put back on the edit-mode side of the transition — by
        /// <see cref="FlowResumer"/> when there was a reload, and here when there was not.
        /// </summary>
        private static IEnumerator ExitPlayMode(StepContext ctx)
        {
            if (!EditorApplication.isPlaying)
            {
                // Teardown semantics: 'after:' runs even when the body failed before play mode was
                // ever entered. Demanding play mode here would turn every such failure into two.
                Restore(ctx);
                yield break;
            }

            if (ctx.Resume == null)
            {
                ctx.Fail("exitPlayMode needs a run that can survive a domain reload, and this one cannot. " +
                         "Leaving play mode reloads the domain; start this flow with flow.start.");
                yield break;
            }

            var timeout = ctx.Step.Timeout ?? PlayModeGate.DefaultTransitionTimeout;

            ctx.Resume.Arm(ResumeGate.EditMode, ctx.StepIndex + 1, ctx.Section, timeout.TotalSeconds);
            ctx.Resume.Save(ctx.Paths);

            if (!PlayModeGate.TryRequestEditMode(out var requestError))
            {
                ctx.Fail($"exitPlayMode could not request edit mode: {requestError}{Abandon(ctx)}");
                yield break;
            }

            while (!PlayModeGate.IsEditModeActive)
            {
                if (FlowResumeState.NowUtc >= ctx.Resume.GateDeadlineUtc)
                {
                    ctx.Fail($"exitPlayMode timed out after {timeout.TotalSeconds:0.#}s: the editor is still in play mode." +
                             Abandon(ctx), ctx.BuildDiagnostics());
                    yield break;
                }

                yield return null;
            }

            ctx.Resume.Disarm();
            ctx.Resume.Save(ctx.Paths);

            Restore(ctx);
        }

        /// <summary>
        /// Take back a resume point that will never be used, because the transition it was armed
        /// for failed. Leaving it armed would make the next unrelated reload replay the flow.
        ///
        /// Returns what it had to undo, as a clause to append to the failure: the editor is left in
        /// a state the flow did not ask for, and the report has to say which one.
        /// </summary>
        private static string Abandon(StepContext ctx)
        {
            ctx.Resume.Disarm();

            var note = PlayModeGate.ReleaseScene(ctx.Resume);
            ctx.Resume.Save(ctx.Paths);

            return note == null ? string.Empty : $" ({note})";
        }

        /// <summary>Put back the scene the run replaced, failing the step when it cannot be done.</summary>
        private static void Restore(StepContext ctx)
        {
            if (ctx.Resume == null || !ctx.Resume.SceneRestorePending)
                return;

            var wanted = ctx.Resume.SceneToRestore;
            if (!PlayModeGate.TryRestoreScene(wanted, out var error))
            {
                ctx.Fail($"exitPlayMode could not put back the scene the run replaced: {error}");
                return;
            }

            ctx.Resume.SceneRestorePending = false;
            ctx.Resume.Save(ctx.Paths);
        }

        /// <summary>
        /// Assert something about the GAME rather than about the UI.
        ///
        /// Positive by definition: it retries every frame until the comparison holds or the step
        /// times out, exactly like assertVisible, because state that is one network round trip away
        /// is not wrong just because it is not there yet.
        ///
        /// <para><b>stableFor.</b> A comparison that returns on the first agreeing frame can be
        /// satisfied by a value that was never real. Consider a networked game client with
        /// client-side prediction: the local simulation applies a movement or a hit immediately,
        /// the server reconciles it a few frames later, and a predicted value can be true for one
        /// frame and then be reverted. <c>stableFor</c> makes the assertion prove itself over a
        /// window instead — it must become true and then STAY true, and it fails the instant it
        /// flips, naming how long it lasted. Use it for anything negative (<c>ne</c>,
        /// <c>exists: false</c>) and for anything the server has the last word on.</para>
        /// </summary>
        private static IEnumerator Assert(StepContext ctx)
        {
            if (!TryBuildQuery(ctx, out var query))
                yield break;

            var resolver = new StateResolver(query);
            var stableFor = ctx.Step.Has("stableFor") ? ctx.Step.Get<TimeSpan>("stableFor").TotalSeconds : 0.0;

            try
            {
                while (!ctx.DeadlineReached)
                {
                    var outcome = resolver.Evaluate();

                    if (outcome == StateOutcome.Fatal)
                    {
                        ctx.Fail($"assert cannot be answered as written: {resolver.Explain()}", ctx.BuildDiagnostics());
                        yield break;
                    }

                    if (outcome != StateOutcome.Satisfied)
                    {
                        yield return null;
                        continue;
                    }

                    if (stableFor <= 0.0)
                        yield break;

                    var start = FlowClock.Now;

                    while (FlowClock.Now - start < stableFor)
                    {
                        yield return null;

                        var held = resolver.Evaluate();
                        if (held == StateOutcome.Satisfied)
                            continue;

                        var elapsed = (FlowClock.Now - start) * 1000.0;

                        if (held == StateOutcome.Fatal)
                        {
                            ctx.Fail($"assert became unanswerable {elapsed:F0}ms after it first held: {resolver.Explain()}",
                                ctx.BuildDiagnostics());
                            yield break;
                        }

                        ctx.Fail(
                            $"assert {query.Describe()} was true for only {elapsed:F0}ms of the {stableFor * 1000:F0}ms it had " +
                            $"to hold: {resolver.Explain()}",
                            ctx.BuildDiagnostics());
                        yield break;
                    }

                    yield break;
                }

                ctx.Fail($"assert timed out after {Describe(ctx)}: {resolver.Explain()}", ctx.BuildDiagnostics());
            }
            finally
            {
                Report.FlowProfiler.Retries("assert", resolver.Evaluations, resolver.EvaluationMilliseconds);
            }
        }

        /// <summary>
        /// Wait for game state to become what the flow says, then carry on.
        ///
        /// Identical machinery to <see cref="Assert"/> and a different promise: this one says "the
        /// run cannot continue until this is true", which is how a flow waits for a map load or a
        /// spawn without writing an unconditional <c>wait: 2s</c> that is either too short on a slow
        /// machine or wasted time on a fast one.
        /// </summary>
        private static IEnumerator WaitUntil(StepContext ctx)
        {
            if (!TryBuildQuery(ctx, out var query))
                yield break;

            var resolver = new StateResolver(query);

            try
            {
                while (!ctx.DeadlineReached)
                {
                    var outcome = resolver.Evaluate();

                    if (outcome == StateOutcome.Fatal)
                    {
                        ctx.Fail($"waitUntil cannot be answered as written: {resolver.Explain()}", ctx.BuildDiagnostics());
                        yield break;
                    }

                    if (outcome == StateOutcome.Satisfied)
                        yield break;

                    yield return null;
                }

                ctx.Fail($"waitUntil timed out after {Describe(ctx)}: {resolver.Explain()}", ctx.BuildDiagnostics());
            }
            finally
            {
                Report.FlowProfiler.Retries("waitUntil", resolver.Evaluations, resolver.EvaluationMilliseconds);
            }
        }

        /// <summary>
        /// Turn the step's arguments into a validated query, once, before the retry loop starts.
        ///
        /// Everything that can be wrong about the query itself — two comparisons, none, a mapping
        /// where a value belongs — is settled here, so the loop that runs every frame does nothing
        /// but read and compare.
        /// </summary>
        private static bool TryBuildQuery(StepContext ctx, out StateQuery query)
        {
            query = null;

            string comparisonKey = null;
            string expected = null;
            var comparison = StateComparison.Exists;

            for (var i = 0; i < ctx.Step.Args.Count; i++)
            {
                var argument = ctx.Step.Args[i];
                if (!TryMapComparison(argument.Name, out var candidate))
                    continue;

                if (comparisonKey != null)
                {
                    ctx.Fail($"{ctx.Step.Verb} on line {ctx.Step.Line} writes two comparisons, '{comparisonKey}' and " +
                             $"'{argument.Name}'; a query compares one thing, one way");
                    return false;
                }

                comparisonKey = argument.Name;
                comparison = candidate;

                if (candidate == StateComparison.Exists)
                {
                    expected = ctx.Step.Get<bool>("exists") ? "true" : "false";
                    continue;
                }

                if (!(argument.Value is FlowValue raw) || raw.Kind != FlowValueKind.Scalar)
                {
                    var written = argument.Value is FlowValue value ? value.Describe() : "nothing";
                    ctx.Fail($"'{argument.Name}' on line {argument.Line} expects a single value to compare with, got {written}");
                    return false;
                }

                // A comparison is declared as a raw value so the resolver can coerce it to whatever
                // the member's type turns out to be, which means the parser never got the chance to
                // reject an '@name' reference here.
                if (raw.Scalar.Length > 0 && raw.Scalar[0] == '@' && !(raw.Scalar.Length > 1 && raw.Scalar[1] == '@'))
                {
                    ctx.Fail($"'{argument.Name}' on line {argument.Line} is the reference '{raw.Scalar}'; a state " +
                             "comparison takes a literal value, because 'as:' binds UI nodes and not game values");
                    return false;
                }

                expected = raw.Scalar;
            }

            if (comparisonKey == null)
            {
                ctx.Fail($"{ctx.Step.Verb} on line {ctx.Step.Line} needs a comparison: " +
                         "is, eq, ne, gt, gte, lt, lte, contains or exists");
                return false;
            }

            if (!TryText(ctx, "find", out var find) ||
                !TryText(ctx, "component", out var component) ||
                !TryText(ctx, "field", out var field) ||
                !TryText(ctx, "count", out var count) ||
                !TryText(ctx, "expr", out var expr))
            {
                return false;
            }

            var created = StateQuery.TryCreate(find, component, field, count, expr, comparison, expected, out query, out var error);

            if (!created)
                ctx.Fail($"{ctx.Step.Verb} on line {ctx.Step.Line}: {error}");

            return created;
        }

        private static bool TryText(StepContext ctx, string name, out string value)
        {
            value = null;

            if (!ctx.Step.TryGetArg(name, out var argument))
                return true;

            // 'as:' binds UI nodes, and a UI node is not a type name or a field name. Reading the
            // reference as literal text would look for an object actually called "@login".
            if (argument.IsReference)
            {
                ctx.Fail($"'{name}' on line {argument.Line} is the reference '@{argument.Reference}', which names a UI " +
                         "node bound by an earlier 'as:'; a state query takes a plain name");
                return false;
            }

            value = (string)argument.Value;
            return true;
        }

        private static bool TryMapComparison(string name, out StateComparison comparison)
        {
            switch (name)
            {
                case "is": comparison = StateComparison.Is; return true;
                case "eq": comparison = StateComparison.Eq; return true;
                case "ne": comparison = StateComparison.Ne; return true;
                case "gt": comparison = StateComparison.Gt; return true;
                case "gte": comparison = StateComparison.Gte; return true;
                case "lt": comparison = StateComparison.Lt; return true;
                case "lte": comparison = StateComparison.Lte; return true;
                case "contains": comparison = StateComparison.Contains; return true;
                case "exists": comparison = StateComparison.Exists; return true;
                default: comparison = StateComparison.Exists; return false;
            }
        }

        private static string Describe(StepContext ctx)
        {
            var timeout = ctx.Step.Timeout ?? FlowRunner.DefaultStepTimeout;
            return $"{timeout.TotalSeconds:0.#}s";
        }

        private static string Truncate(string value, int max) =>
            value != null && value.Length > max ? value.Substring(0, max) + "..." : value;
    }
}
