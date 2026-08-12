using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// Binds a step's YAML arguments to a <c>[FlowCommand]</c> method and invokes it.
    ///
    /// The interesting half is the parameters the flow does NOT supply. A Component or GameObject
    /// parameter is resolved from the live scene, which is what lets an INSTANCE method work:
    /// <c>this</c> is the object the command acts on, so the flow never has to name it. That
    /// solves the reference problem by construction rather than by convention.
    ///
    /// Scene resolution follows the same 0 / 1 / N-fails rule as selector resolution. Two players
    /// in the scene and a <c>giveCoins</c> with no <c>on:</c> is an ambiguity the author must
    /// settle — picking one would make the flow pass while testing the wrong object.
    /// </summary>
    public static class FlowCommandInvoker
    {
        public static IEnumerator Invoke(StepContext ctx, MethodInfo method)
        {
            object target = null;
            object[] arguments;

            try
            {
                if (!method.IsStatic)
                {
                    target = ResolveSceneObject(ctx, method.DeclaringType, out var error);
                    if (target == null)
                    {
                        ctx.Fail($"cannot run '{ctx.Step.Verb}': {error}");
                        yield break;
                    }
                }

                arguments = BindArguments(ctx, method);
            }
            catch (FlowBindException ex)
            {
                ctx.Fail(ex.Message);
                yield break;
            }

            object returned;
            try
            {
                returned = method.Invoke(target, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ctx.Fail($"'{ctx.Step.Verb}' threw {ex.InnerException.GetType().Name}: {ex.InnerException.Message}",
                    ctx.BuildDiagnostics());
                yield break;
            }

            // Yielding on IEnumerator is what keeps a scene load or an animation from degrading
            // into a guessed 'wait: 2s', which is exactly the flakiness this tool exists to remove.
            switch (returned)
            {
                case IEnumerator nested:
                    yield return nested;
                    break;

                case Task task:
                    while (!task.IsCompleted)
                    {
                        if (ctx.DeadlineReached)
                        {
                            ctx.Fail($"'{ctx.Step.Verb}' did not complete before its timeout");
                            yield break;
                        }

                        yield return null;
                    }

                    if (task.IsFaulted)
                    {
                        var inner = task.Exception?.GetBaseException();
                        ctx.Fail($"'{ctx.Step.Verb}' failed: {inner?.Message ?? "the task faulted"}",
                            ctx.BuildDiagnostics());
                    }

                    break;
            }
        }

        internal static object[] BindArguments(StepContext ctx, MethodInfo method)
        {
            var parameters = method.GetParameters();
            var arguments = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                if (FlowVocabulary.IsSceneResolved(parameter.ParameterType))
                {
                    var resolved = ResolveSceneObject(ctx, parameter.ParameterType, out var error);
                    if (resolved == null)
                        throw new FlowBindException($"parameter '{parameter.Name}' of '{ctx.Step.Verb}': {error}");

                    arguments[i] = resolved;
                    continue;
                }

                if (ctx.Step.TryGetArg(parameter.Name, out var argument))
                {
                    arguments[i] = Coerce(argument.Value, parameter.ParameterType, parameter.Name, ctx.Step.Verb);
                    continue;
                }

                if (parameter.HasDefaultValue)
                {
                    arguments[i] = parameter.DefaultValue;
                    continue;
                }

                throw new FlowBindException(
                    $"'{ctx.Step.Verb}' requires parameter '{parameter.Name}' ({parameter.ParameterType.Name})");
            }

            return arguments;
        }

        private static object Coerce(object value, Type target, string parameterName, string verb)
        {
            if (value == null)
            {
                if (target.IsValueType)
                    throw new FlowBindException($"'{verb}' parameter '{parameterName}' is {target.Name} and cannot be null");

                return null;
            }

            if (target.IsInstanceOfType(value))
                return value;

            if (target.IsEnum && value is string enumName)
            {
                if (Enum.TryParse(target, enumName, ignoreCase: true, out var parsed))
                    return parsed;

                throw new FlowBindException(
                    $"'{verb}' parameter '{parameterName}': '{enumName}' is not a member of {target.Name}. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames(target))}");
            }

            if (target == typeof(TimeSpan) && value is TimeSpan)
                return value;

            try
            {
                return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new FlowBindException(
                    $"'{verb}' parameter '{parameterName}' expects {target.Name} but got {value.GetType().Name} ('{value}')");
            }
        }

        /// <summary>
        /// Find the single scene object of <paramref name="type"/>, honouring the step's
        /// <c>on:</c> selector when several exist.
        /// </summary>
        private static object ResolveSceneObject(StepContext ctx, Type type, out string error)
        {
            var componentType = type == typeof(GameObject) ? typeof(Transform) : type;

            var found = UnityEngine.Object.FindObjectsByType(
                componentType, FindObjectsInactive.Include, FindObjectsSortMode.None);

            var candidates = new List<Component>(found.Length);
            foreach (var obj in found)
            {
                if (obj is Component component)
                    candidates.Add(component);
            }

            if (ctx.Step.On != null && !TryFilterByOn(candidates, ctx.Step.On, out error))
                return null;

            if (candidates.Count == 0)
            {
                error = ctx.Step.On != null
                    ? $"no {type.Name} matched 'on: {ctx.Step.On}'"
                    : $"no {type.Name} exists in the loaded scenes";
                return null;
            }

            if (candidates.Count > 1)
            {
                var paths = new List<string>();
                for (var i = 0; i < candidates.Count && i < 6; i++)
                    paths.Add(HierarchyPath(candidates[i].transform));

                error = $"{candidates.Count} objects have a {type.Name}; add 'on:' to say which. " +
                        $"Candidates: {string.Join(", ", paths)}" +
                        (candidates.Count > paths.Count ? ", ..." : string.Empty);
                return null;
            }

            error = null;
            return type == typeof(GameObject) ? (object)candidates[0].gameObject : candidates[0];
        }

        /// <summary>
        /// Narrow the candidates with the step's <c>on:</c> selector.
        ///
        /// Only name and path can be honoured: this list came from FindObjectsByType, so there is no
        /// enumerated node behind it and nothing to compare a testId, text or visibility against.
        /// Refusing beats ignoring by a wide margin — an <c>on:</c> that quietly did nothing would
        /// bind whichever object happened to be the only one and run the command against it, which
        /// looks exactly like success until a second object appears and the same flow starts failing
        /// for a reason its author never wrote down.
        /// </summary>
        private static bool TryFilterByOn(List<Component> candidates, Selector on, out string error)
        {
            if (on.Path == null && on.Name == null)
            {
                error = $"'on: {on}' cannot narrow a scene-object binding: a scene object is matched by " +
                        "hierarchy name or path, and this selector supplies neither. Write it as " +
                        "'on: { name: ... }' or 'on: { path: ... }'.";
                return false;
            }

            FilterByPath(candidates, on);
            error = null;
            return true;
        }

        private static void FilterByPath(List<Component> candidates, Selector on)
        {
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var path = HierarchyPath(candidates[i].transform);
                var matches = on.Path != null
                    ? string.Equals(path, on.Path, StringComparison.Ordinal)
                    : string.Equals(candidates[i].gameObject.name, on.Name, StringComparison.Ordinal);

                if (!matches)
                    candidates.RemoveAt(i);
            }
        }

        private static string HierarchyPath(Transform transform)
        {
            var builder = new System.Text.StringBuilder();
            var stack = new Stack<string>();

            for (var t = transform; t != null; t = t.parent)
                stack.Push(t.name);

            while (stack.Count > 0)
                builder.Append('/').Append(stack.Pop());

            return builder.ToString();
        }
    }

    /// <summary>Thrown when a step's arguments cannot be bound to a command's signature.</summary>
    public sealed class FlowBindException : Exception
    {
        public FlowBindException(string message) : base(message) { }
    }
}
