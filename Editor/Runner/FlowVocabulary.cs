using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// The set of verbs a flow may use: the built-ins plus every <c>[FlowCommand]</c> in the
    /// project.
    ///
    /// This is handed to the parser so a typo fails in milliseconds instead of mid-flow, and so a
    /// project command is validated by exactly the same code path as a built-in — there is no
    /// second, weaker validator for user commands.
    ///
    /// Discovery uses TypeCache, which Unity maintains as part of its own indexing, so it costs
    /// nothing and survives domain reloads without a registration handshake.
    /// </summary>
    public sealed class FlowVocabulary : IFlowVerbVocabulary
    {
        private readonly Dictionary<string, FlowVerbSpec> m_Verbs = new Dictionary<string, FlowVerbSpec>(StringComparer.Ordinal);
        private readonly Dictionary<string, MethodInfo> m_Commands = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        private readonly List<string> m_Names = new List<string>();

        public IReadOnlyList<string> VerbNames => m_Names;

        /// <summary>Project <c>[FlowCommand]</c> methods, by verb name.</summary>
        public IReadOnlyDictionary<string, MethodInfo> Commands => m_Commands;

        /// <summary>Names that collided between the built-ins and project commands, or between two commands.</summary>
        public IReadOnlyList<string> Conflicts { get; }

        public FlowVocabulary()
        {
            var conflicts = new List<string>();

            foreach (var spec in BuiltinVerbSpecs())
                m_Verbs.Add(spec.Name, spec);

            foreach (var method in TypeCache.GetMethodsWithAttribute<FlowCommandAttribute>())
            {
                var attribute = method.GetCustomAttribute<FlowCommandAttribute>();
                if (attribute == null)
                    continue;

                if (m_Verbs.ContainsKey(attribute.Name))
                {
                    // A user command shadowing a built-in would silently change what an existing
                    // flow does. Refusing both and naming the collision is the only safe answer.
                    conflicts.Add(
                        $"'{attribute.Name}' is declared by {Describe(method)} but that name is already taken " +
                        (m_Commands.TryGetValue(attribute.Name, out var other)
                            ? $"by {Describe(other)}"
                            : "by a built-in verb"));
                    continue;
                }

                m_Verbs.Add(attribute.Name, BuildCommandSpec(attribute, method));
                m_Commands.Add(attribute.Name, method);
            }

            m_Names.AddRange(m_Verbs.Keys);
            m_Names.Sort(StringComparer.Ordinal);
            Conflicts = conflicts;
        }

        public bool TryGetVerb(string name, out FlowVerbSpec spec) => m_Verbs.TryGetValue(name, out spec);

        private static string Describe(MethodInfo method) =>
            $"{method.DeclaringType?.Name}.{method.Name}";

        /// <summary>
        /// Derive a verb spec from a method signature. Component and GameObject parameters are not
        /// declared as YAML arguments: they are resolved from the scene by the binder, which is
        /// what makes an instance <c>[FlowCommand]</c> work without the flow naming an object.
        /// </summary>
        private static FlowVerbSpec BuildCommandSpec(FlowCommandAttribute attribute, MethodInfo method)
        {
            var args = new List<FlowArgSpec>();

            foreach (var parameter in method.GetParameters())
            {
                if (IsSceneResolved(parameter.ParameterType))
                    continue;

                args.Add(new FlowArgSpec(
                    parameter.Name,
                    KindOf(parameter.ParameterType),
                    required: !parameter.HasDefaultValue,
                    enumType: parameter.ParameterType.IsEnum ? parameter.ParameterType : null,
                    description: $"{parameter.ParameterType.Name} parameter of {Describe(method)}"));
            }

            // A command declared on a MonoBehaviour needs an object to run on, and 'on:' is how a
            // flow disambiguates when several exist.
            var selectorMode = method.IsStatic && !HasSceneResolvedParameter(method)
                ? SelectorMode.None
                : SelectorMode.Optional;

            return new FlowVerbSpec(
                attribute.Name,
                args,
                selectorMode,
                bareScalarArg: args.Count == 1 && args[0].Required ? args[0].Name : null,
                description: attribute.Description ?? $"project command {Describe(method)}");
        }

        private static bool HasSceneResolvedParameter(MethodInfo method)
        {
            foreach (var parameter in method.GetParameters())
            {
                if (IsSceneResolved(parameter.ParameterType))
                    return true;
            }

            return false;
        }

        internal static bool IsSceneResolved(Type type) =>
            typeof(UnityEngine.Component).IsAssignableFrom(type) || type == typeof(UnityEngine.GameObject);

        internal static FlowArgKind KindOf(Type type)
        {
            if (type.IsEnum) return FlowArgKind.Enum;
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return FlowArgKind.Int;
            if (type == typeof(float) || type == typeof(double)) return FlowArgKind.Float;
            if (type == typeof(bool)) return FlowArgKind.Bool;
            if (type == typeof(UnityEngine.Vector2)) return FlowArgKind.Vector2;
            if (type == typeof(UnityEngine.Vector3)) return FlowArgKind.Vector3;
            if (type == typeof(UnityEngine.Color)) return FlowArgKind.Color;
            if (type == typeof(TimeSpan)) return FlowArgKind.Duration;
            return FlowArgKind.String;
        }

        /// <summary>
        /// The arguments 'assert' and 'waitUntil' share.
        ///
        /// Every comparison is declared as <see cref="FlowArgKind.Any"/> on purpose: the type the
        /// value must be is the type of the FIELD, which is unknown until the scene is looked at.
        /// Converting <c>eq: 5</c> to an int at parse time would then be wrong for an enum, a float
        /// or a string field, so the raw value travels to <see cref="UnityFlow.Editor.State.StateResolver"/>
        /// and is coerced there, against the member it is actually being compared with.
        /// </summary>
        private static IReadOnlyList<FlowArgSpec> StateQueryArgs(bool includeStableFor)
        {
            var args = new List<FlowArgSpec>(12)
            {
                new FlowArgSpec("find", FlowArgKind.String, description:
                    "GameObject name, or a full hierarchy path when it contains '/'. Matched exactly, across every loaded scene, including inactive objects and DontDestroyOnLoad."),
                new FlowArgSpec("component", FlowArgKind.String, description:
                    "Component type, by simple name or full name. An ambiguous simple name fails with the candidates listed; nothing is picked for you."),
                new FlowArgSpec("field", FlowArgKind.String, description:
                    "Field or property to read, public or not, instance or static. Exact name; flow.probe lists them with their current values."),
                new FlowArgSpec("count", FlowArgKind.String, description:
                    "Count live objects instead of reading one: a component type name when one exists with that name, otherwise a GameObject name or path."),
                new FlowArgSpec("expr", FlowArgKind.String, description:
                    "Escape hatch: a C# EXPRESSION, compiled once by Roslyn and cached. Use it for statics and for state that does not live on any component."),
                new FlowArgSpec("is", FlowArgKind.Any, description: "Equal to. Spelled for booleans and enums."),
                new FlowArgSpec("eq", FlowArgKind.Any, description: "Equal to."),
                new FlowArgSpec("ne", FlowArgKind.Any, description: "Not equal to. Negative: pair it with stableFor."),
                new FlowArgSpec("gt", FlowArgKind.Any, description: "Greater than. Numbers only."),
                new FlowArgSpec("gte", FlowArgKind.Any, description: "Greater than or equal. Numbers only."),
                new FlowArgSpec("lt", FlowArgKind.Any, description: "Less than. Numbers only."),
                new FlowArgSpec("lte", FlowArgKind.Any, description: "Less than or equal. Numbers only."),
                new FlowArgSpec("contains", FlowArgKind.Any, description: "Substring, case-insensitive. Text only."),
                new FlowArgSpec("exists", FlowArgKind.Bool, description:
                    "Whether the object, or the value, is there at all. Written without 'field' to ask only about the object.")
            };

            if (includeStableFor)
            {
                args.Add(new FlowArgSpec("stableFor", FlowArgKind.Duration, description:
                    "How long the comparison must STAY true after it first holds. Off by default. This client predicts locally and the server reconciles, so a value can be right for one frame and be reverted on the next."));
            }

            return args;
        }

        private static IEnumerable<FlowVerbSpec> BuiltinVerbSpecs()
        {
            yield return new FlowVerbSpec("tapOn", new[]
                {
                    new FlowArgSpec("allowUnverifiedOcclusion", FlowArgKind.Bool, description:
                        "Proceed even when occlusion could not be verified (edit mode). Off by default: an unverified tap can silently succeed through a modal."),
                },
                SelectorMode.Required,
                description: "Tap the resolved node by placing a real pointer at it.");

            yield return new FlowVerbSpec("drag", new[]
                {
                    new FlowArgSpec("from", FlowArgKind.Selector, description:
                        "Where the gesture starts. Resolved exactly like tapOn's target — actionable, stable, with a real " +
                        "injection point and an occlusion check — so a drag whose source is covered fails with the same " +
                        "'obscured by ...' as a tap would."),
                    new FlowArgSpec("to", FlowArgKind.Selector, description:
                        "Where it ends. Resolved BEFORE the press if it can be; a drop zone that only exists while a drag " +
                        "is in progress is resolved again after the press, and the failure says which endpoint was missing and when."),
                    new FlowArgSpec("fromPoint", FlowArgKind.Vector2, description:
                        "Screen coordinate to start from, for a source that is not a UI node at all: either [x, y], or " +
                        "\"@name\" naming a point an earlier step bound with 'as:' — a runScript that read the live " +
                        "rect and returned \"x,y\" in invariant culture, so the aim survives a Game View resize " +
                        "instead of pointing at whatever is now there. " +
                        "Mutually exclusive with 'from'; nothing is occlusion-checked for a bare coordinate, because there " +
                        "is no intended target to check it against."),
                    new FlowArgSpec("toPoint", FlowArgKind.Vector2, description:
                        "Screen coordinate to end at, written [x, y] or as the reference \"@name\". Mutually exclusive with 'to'."),
                    new FlowArgSpec("holdFor", FlowArgKind.Duration, description:
                        "How long the pointer is held COMPLETELY STILL after the press, before any travel. Defaults to " +
                        "600ms. This is what arms a hold-to-pick-up UI: such a UI waits a fixed time AND cancels if the " +
                        "pointer moved, so holding still satisfies both regardless of how far or how fast the drag then " +
                        "goes. No intermediate move is sent during it."),
                    new FlowArgSpec("duration", FlowArgKind.Duration, description:
                        "How long the TRAVEL takes — the source-to-target motion only, not the hold before it. Defaults to " +
                        "250ms, spent on the wall clock between the intermediate moves. It used to have to cover arming as " +
                        "well; 'holdFor' does that now, so this can be as short as the UI can follow."),
                    new FlowArgSpec("steps", FlowArgKind.Int, description:
                        "How many intermediate moves to send between press and release. Defaults to 12, minimum 2. Each one " +
                        "costs its own frame: uGUI only starts a drag once the pointer has travelled past " +
                        "EventSystem.pixelDragThreshold WHILE PRESSED, and it observes that from " +
                        "InputSystemUIInputModule.Process() once per frame."),
                },
                SelectorMode.None,
                description: "Drag from one place to another with a real pointer, in phases: move to the source, press, " +
                             "hold still for 'holdFor', confirm with the UI system that a drag exists, travel across real " +
                             "frames, release. The intermediate moves are the verb — without motion under a held button " +
                             "uGUI raises no IBeginDragHandler, no IDragHandler and no IDropHandler, and a " +
                             "press-then-release-elsewhere is a click, not a drag. If no drag began, the button goes back " +
                             "up and the whole gesture is retried until the step's timeout, with every attempt after the " +
                             "first written to the progress stream. Needs device injection; it refuses to run under " +
                             "semantic dispatch rather than degrade into a tap.");

            yield return new FlowVerbSpec("runFlow", new[]
                {
                    new FlowArgSpec("file", FlowArgKind.String, required: true, description:
                        "Path to the sub-flow, resolved against the PROJECT ROOT exactly like the CLI's --file."),
                    new FlowArgSpec("env", FlowArgKind.Any, description:
                        "Variables to supply to the sub-flow, as a mapping. The sub-flow's own 'env:' block declares what it " +
                        "accepts; a name it does not declare is refused, the same way --env is."),
                },
                SelectorMode.None, bareScalarArg: "file",
                description: "Run another flow's steps INLINE, as part of this run. They are spliced into this flow's step " +
                             "list at parse time, so they share one progress stream, one run folder and one set of step " +
                             "indices — which is what keeps the resume ledger and the domain-reload rebuild working. The " +
                             "sub-flow is parsed and validated with the parent, so a missing file, a bad step or a cycle " +
                             "fails in milliseconds instead of mid-run. It may declare 'name', 'requires', 'env', 'defs' and " +
                             "'steps'; 'before', 'after', 'timeScale' and 'seed' belong to the flow that is started.");

            yield return new FlowVerbSpec("inputText", new[]
                {
                    new FlowArgSpec("text", FlowArgKind.String, required: true, description: "Text to enter."),
                },
                SelectorMode.Required, bareScalarArg: null,
                description: "Type text into the resolved input control.");

            yield return new FlowVerbSpec("press", new[]
                {
                    new FlowArgSpec("key", FlowArgKind.String, required: true, description:
                        "Input System control name on the Keyboard layout, plus the aliases up/down/left/right for the arrow keys: " +
                        "enter, escape, space, tab, backspace, a..z, 0..9, f1..f24, leftShift, rightCtrl. An unknown name fails the " +
                        "step and lists the closest real names; it is never silently ignored."),
                    new FlowArgSpec("count", FlowArgKind.Int, description:
                        "How many separate press/release cycles to send. Defaults to 1. Each cycle takes its own frames, so three " +
                        "presses are three navigation moves and not one held key."),
                    new FlowArgSpec("duration", FlowArgKind.Duration, description:
                        "How long each press is HELD before release. Omitted means one frame, which is one discrete key event. " +
                        "Use it to reach press-and-hold behaviour such as uGUI's navigation auto-repeat."),
                },
                SelectorMode.None, bareScalarArg: "key",
                description: "Send a real key through the injected keyboard device. Requires play mode and device injection: " +
                             "the key travels through the project's own action bindings, so it proves what a player's keyboard would.");

            yield return new FlowVerbSpec("navigateTo", new[]
                {
                    new FlowArgSpec("maxSteps", FlowArgKind.Int, description:
                        "Ceiling on the number of arrow-key presses. Defaults to 40. Exhausting it fails with the path the selection " +
                        "actually took."),
                    new FlowArgSpec("from", FlowArgKind.Selector, description:
                        "Where the selection should START when nothing is selected yet. Establishing it uses " +
                        "EventSystem.SetSelectedGameObject, which is not input, so it is reported as an assist on the progress stream. " +
                        "Omitted, the first navigable Selectable of the target's own Canvas is used."),
                },
                SelectorMode.Required,
                description: "Move the selection to the node by sending REAL arrow keys through uGUI's navigation graph, one press at " +
                             "a time, re-reading EventSystem.current.currentSelectedGameObject after each. Never jumps with " +
                             "SetSelectedGameObject. A failure names the path the selection took and the navigation wiring that is " +
                             "missing, which makes this an accessibility check for keyboard and controller players.");

            yield return new FlowVerbSpec("submit", Array.Empty<FlowArgSpec>(),
                SelectorMode.Optional,
                description: "Send the UI Submit action (Enter) to whatever is selected. With a selector it first asserts that the " +
                             "selector IS the current selection and fails if it is not, so it can never submit to the wrong thing.");

            yield return new FlowVerbSpec("cancel", Array.Empty<FlowArgSpec>(),
                SelectorMode.Optional,
                description: "Send the UI Cancel action (Escape). Same selector assertion as submit.");

            yield return new FlowVerbSpec("waitFor", Array.Empty<FlowArgSpec>(), SelectorMode.Required,
                description: "Wait until the node exists and is visible.");

            yield return new FlowVerbSpec("waitUntilNotVisible", Array.Empty<FlowArgSpec>(), SelectorMode.Required,
                description: "Wait until the node is gone or hidden.");

            yield return new FlowVerbSpec("assertVisible", Array.Empty<FlowArgSpec>(), SelectorMode.Required,
                description: "Assert the node is visible, retrying until the timeout.");

            yield return new FlowVerbSpec("assertNotVisible", new[]
                {
                    new FlowArgSpec("stableFor", FlowArgKind.Duration, description:
                        "How long the node must STAY absent. Defaults to 500ms. A negative assertion that passes instantly is vacuous."),
                },
                SelectorMode.Required,
                description: "Assert the node stays absent for a window.");

            yield return new FlowVerbSpec("assertText", new[]
                {
                    new FlowArgSpec("equals", FlowArgKind.String, description: "Exact text."),
                    new FlowArgSpec("contains", FlowArgKind.String, description: "Substring, case-insensitive."),
                    new FlowArgSpec("matches", FlowArgKind.String, description: "Regular expression."),
                },
                SelectorMode.Required,
                description: "Assert the node's visible text.");

            yield return new FlowVerbSpec("assert", StateQueryArgs(includeStableFor: true),
                SelectorMode.None,
                description: "Assert something about the GAME: { find: Player, component: Health, field: current, gt: 0 }. " +
                             "Positive, so it retries until true or the step times out. 'unity command flow.probe' lists the field names.");

            yield return new FlowVerbSpec("waitUntil", StateQueryArgs(includeStableFor: false),
                SelectorMode.None,
                description: "Wait until a game-state query holds: { find: Player, component: PlayerController, field: isGrounded, is: true }. " +
                             "Same query as assert; use it to wait for a load or a spawn instead of 'wait: 2s'.");

            yield return new FlowVerbSpec("screenshot", new[]
                {
                    new FlowArgSpec("name", FlowArgKind.String, required: true, description: "File name for the PNG."),
                },
                SelectorMode.None, bareScalarArg: "name",
                description: "Capture the screen to the run's artifacts folder.");

            yield return new FlowVerbSpec("assertLog", new[]
                {
                    new FlowArgSpec("level", FlowArgKind.String, description: "Log, Warning, Error, Exception or Assert."),
                    new FlowArgSpec("contains", FlowArgKind.String, description: "Substring of the message, case-insensitive."),
                    new FlowArgSpec("matches", FlowArgKind.String, description: "Regular expression over the message."),
                    new FlowArgSpec("since", FlowArgKind.String, description:
                        "step, previous or run. Defaults to 'previous' — the action that logged usually ran in the step before."),
                },
                SelectorMode.None, bareScalarArg: "contains",
                description: "Wait until a matching console message is logged. Retries until the timeout.");

            yield return new FlowVerbSpec("assertNoLog", new[]
                {
                    new FlowArgSpec("level", FlowArgKind.String, description: "Log, Warning, Error, Exception or Assert."),
                    new FlowArgSpec("contains", FlowArgKind.String, description: "Substring of the message, case-insensitive."),
                    new FlowArgSpec("matches", FlowArgKind.String, description: "Regular expression over the message."),
                    new FlowArgSpec("since", FlowArgKind.String, description: "step, previous or run."),
                    new FlowArgSpec("stableFor", FlowArgKind.Duration, description:
                        "How long the absence must hold. Defaults to 500ms; a negative assertion that passes instantly is vacuous."),
                },
                SelectorMode.None,
                description: "Assert no matching console message appears, and keep checking for a window.");

            yield return new FlowVerbSpec("runScript", new[]
                {
                    new FlowArgSpec("code", FlowArgKind.String, required: true, description:
                        "C# executed inside the running game, on the main thread, via Roslyn."),
                },
                SelectorMode.None, bareScalarArg: "code",
                description: "Run C# in the live game. The escape hatch for state that has no UI affordance " +
                             "and for setup not worth driving through the interface. It proves the code works, " +
                             "not that a player could reach it.");

            yield return new FlowVerbSpec("wait", new[]
                {
                    new FlowArgSpec("duration", FlowArgKind.Duration, required: true, description: "How long to wait."),
                },
                SelectorMode.None, bareScalarArg: "duration",
                description: "Wait a fixed duration. Prefer waitFor: an unconditional wait is either too short or too slow.");

            yield return new FlowVerbSpec("enterPlayMode", new[]
                {
                    new FlowArgSpec("scene", FlowArgKind.String, description:
                        "Scene to open first, absolute or relative to the project root. Without it, play mode starts on whatever the editor happens to have open."),
                },
                SelectorMode.None, bareScalarArg: "scene",
                description: "Enter play mode and wait until it is actually active. Entering play mode reloads the domain, so this needs flow.start; flow.run cannot survive it.");

            yield return new FlowVerbSpec("exitPlayMode", Array.Empty<FlowArgSpec>(),
                SelectorMode.None,
                description: "Leave play mode, wait for edit mode, and put back the scene the run replaced.");
        }
    }
}
