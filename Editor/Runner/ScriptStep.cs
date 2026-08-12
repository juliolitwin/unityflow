using System;
using System.Collections;
using UnityFlow.Editor.Compilation;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// The escape hatch: run C# inside the live game from a flow.
    ///
    /// Every generic UI runner needs one, because a game is not a form. Plenty of state changes
    /// have no UI affordance at all, and plenty that do have one are not worth driving through it
    /// on every run. This is how a flow reaches the game directly instead of pretending the whole
    /// world is reachable through pointers and keys.
    ///
    /// It is deliberately a LAST resort in the vocabulary, not because it is unclean, but because
    /// it proves less: a step that calls the game's own method verifies that the method works, not
    /// that a player could ever get there. A flow mixing the two should say which it is testing.
    ///
    /// <para><b>The same source runs once and is compiled once.</b>
    /// <see cref="FlowCompiler"/> keys the compiled delegate on the script's exact text, which is
    /// what makes a sub-flow affordable: a helper flow that aims at a grid cell is run six times by
    /// one drag flow, and its script used to be compiled six times, at half a second each.</para>
    ///
    /// <para><b>What a script returns is bound as itself.</b> This step used to go through the
    /// pipeline package's <c>eval</c> evaluator, which exists to answer an HTTP request and
    /// therefore JSON round-trips its return value on the way out. Nothing here is going over a
    /// wire — the value is bound into the run's own table for a later step to read — so the trip
    /// is gone with the evaluator. Strings and numbers survived it unchanged and are unaffected;
    /// what changes is that a script returning a <see cref="UnityEngine.Vector2"/> now binds a
    /// Vector2, which <c>drag</c> already accepted and preferred, instead of Unity's own
    /// <c>ToString</c> rounded to two decimals and punctuated by the editor's culture.</para>
    /// </summary>
    public static class ScriptStep
    {
        public static IEnumerator Run(StepContext ctx)
        {
            var code = ctx.Step.Get<string>("code");

            if (string.IsNullOrWhiteSpace(code))
            {
                ctx.Fail("runScript needs some C# to run");
                yield break;
            }

            // Source that does not compile fails identically every time, and the compiler remembers
            // that, so a flow that runs the same broken script twice pays for the diagnosis once.
            if (!FlowCompiler.TryCompileScript(code, out var script, out var compileError))
            {
                ctx.Fail($"runScript did not compile: {compileError}");
                yield break;
            }

            object result;

            try
            {
                result = script();
            }
            catch (Exception exception)
            {
                // A script's own throw is the normal way a flow's fixture refuses: aim-grid-point
                // throws when the point it computed would not land on the tile the caller named.
                // The message the author wrote is the diagnosis, so it leads.
                var thrown = exception is System.Reflection.TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;

                ctx.Fail($"runScript failed: {thrown.GetType().Name}: {thrown.Message}",
                    thrown.StackTrace + ctx.BuildDiagnostics());
                yield break;
            }

            if (ctx.Step.As != null && result != null)
                ctx.Resolver.BindValue(ctx.Step.As, result);

            // Give the game a frame to act on whatever the script just did, so a following
            // waitFor observes the consequence rather than the moment before it.
            yield return null;
        }
    }
}
