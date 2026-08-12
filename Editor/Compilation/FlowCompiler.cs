using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityFlow.Editor.Report;

namespace UnityFlow.Editor.Compilation
{
    /// <summary>
    /// The one place a flow's C# becomes a delegate.
    ///
    /// Two verbs let an author write C#: <c>expr</c> (a single expression, read every retry frame)
    /// and <c>runScript</c> (statements, executed once). Both used to reach the pipeline package's
    /// compiler directly, and both paid its full price on every use — which is what made a run
    /// FREEZE the editor rather than merely take a while. Measured on a real game's inventory
    /// drag-and-drop flow, run 'prof-before': 37 compilations,
    /// 20.6 seconds of compilation inside a 61.5-second run, and 25 seconds of an editor that drew
    /// nothing and processed no input. The flow's assertions all held; the editor was simply gone
    /// for a third of the run.
    ///
    /// <para><b>Where the 550ms went, and why it is now 20ms.</b> Roslyn is handed one metadata
    /// reference per loaded assembly — 355 of them where this was measured — and it decodes each one to
    /// resolve names. <see cref="Unity.Pipeline.Compilation.RoslynCompilationService.Compile"/>
    /// builds that list from scratch per call, so every compilation got FRESH reference objects and
    /// re-decoded all 355 assemblies from disk. The decode is cached inside the reference object, so
    /// sharing one list across compilations pays for it once. Measured in this editor, same source,
    /// six compilations: 370ms for the first and 17-22ms for each of the next five, against
    /// 496-684ms each when the list is rebuilt. Nothing about the compiler changed; it stopped being
    /// asked to re-read the project.</para>
    ///
    /// <para><b>And compiled source is remembered.</b> Keyed by the exact text, so the same
    /// expression written in three steps — or a sub-flow run six times — compiles once. This is what
    /// makes the retry model affordable in principle as well as in practice: a <c>waitUntil</c>
    /// polling for five seconds evaluates ~300 times, and if any of those re-entered the compiler
    /// the verb would be unusable. Failures are remembered too: source that does not compile fails
    /// identically every time, and re-reporting one syntax error 300 times is the same defect
    /// wearing a different hat.</para>
    ///
    /// <para><b>Domain-scoped, deliberately.</b> Everything here dies with the domain, which is
    /// correct on both counts: an emitted assembly cannot outlive the domain that loaded it, and the
    /// reference list describes the assemblies THIS domain loaded. In the editor a change to the set
    /// of assemblies is a recompile, and a recompile is a domain reload, so there is no state in
    /// which a cached reference list is stale and the domain is not.</para>
    ///
    /// <para><b>The escape hatch is still an escape hatch.</b> Each distinct source loads a small
    /// assembly that .NET cannot unload; it lives until the next domain reload. Compiling is now
    /// cheap, not free, and anything answerable with find/component/field should still be written
    /// that way.</para>
    /// </summary>
    public static class FlowCompiler
    {
        /// <summary>
        /// Compiled delegates by exact source text. A null <see cref="Compiled.Method"/> records
        /// source that cannot compile, so a retry loop reports the diagnosis instead of re-deriving
        /// it every frame.
        /// </summary>
        private static readonly Dictionary<string, Compiled> s_Cache =
            new Dictionary<string, Compiled>(StringComparer.Ordinal);

        /// <summary>
        /// Metadata for every assembly this domain loaded, decoded once.
        ///
        /// Held for the life of the domain on purpose: the decoded metadata inside these objects is
        /// the expensive part, and it is what every compilation after the first reuses.
        /// </summary>
        private static List<MetadataReference> s_References;

        private static readonly CSharpCompilationOptions s_Options =
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        /// <summary>
        /// Lines of generated wrapper above an author's script, so a diagnostic on the script's
        /// first line reads "script line 1" rather than naming a line of a file nobody wrote.
        /// </summary>
        private const int ScriptWrapperLines = 11;

        private readonly struct Compiled
        {
            public readonly MethodInfo Method;
            public readonly string Error;

            public Compiled(MethodInfo method, string error)
            {
                Method = method;
                Error = error;
            }
        }

        /// <summary>How many distinct sources this domain has compiled. Reported by the profile.</summary>
        public static int CompiledCount => s_Cache.Count;

        /// <summary>
        /// An author's <c>expr</c> as a delegate returning the value to compare.
        ///
        /// The generated method returns <see cref="State.StateValue"/> and lets C# overload
        /// resolution classify the expression at compile time, which keeps an int-valued expression
        /// out of the boxing path entirely.
        /// </summary>
        public static bool TryCompileExpression(string expression, out Func<State.StateValue> reader, out string error)
        {
            reader = null;

            // No line numbers: an expression is one line by definition, and "line 0" in a message
            // about text the author can see in full is noise they have to read past.
            var method = Resolve("expr", expression, BuildExpressionSource, "UnityFlowExpressions.Expr_", "Read",
                -1, out var failure);

            if (method == null)
            {
                error = $"expr: \"{expression}\" does not compile: {failure}. " +
                        "It must be a single C# EXPRESSION, not a statement, and it is compiled against " +
                        "System, System.Collections.Generic, System.Linq, UnityEngine and UnityEditor";
                return false;
            }

            reader = (Func<State.StateValue>)Delegate.CreateDelegate(typeof(Func<State.StateValue>), method);
            error = null;
            return true;
        }

        /// <summary>
        /// An author's <c>runScript</c> body as a delegate returning whatever the script returns, or
        /// null when it returns nothing.
        /// </summary>
        public static bool TryCompileScript(string code, out Func<object> run, out string error)
        {
            run = null;

            var method = Resolve("runScript", code, BuildScriptSource, "UnityFlowScripts.Script_", "Run",
                ScriptWrapperLines, out var failure);

            if (method == null)
            {
                error = failure;
                return false;
            }

            run = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), method);
            error = null;
            return true;
        }

        /// <summary>
        /// The cache, and the compiler behind it. Returns null and a described failure for source
        /// that cannot compile — including source that failed on an earlier step, which is answered
        /// from the cache without touching Roslyn.
        /// </summary>
        private static MethodInfo Resolve(string kind, string source, Func<string, string, string> build,
            string typePrefix, string methodName, int lineOffset, out string error)
        {
            if (s_Cache.TryGetValue(source, out var cached))
            {
                FlowProfiler.Compilation(kind, source, 0.0, true);
                error = cached.Error;
                return cached.Method;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var method = Compile(source, build, typePrefix, methodName, lineOffset, out error);
            clock.Stop();

            FlowProfiler.Compilation(kind, source, clock.Elapsed.TotalMilliseconds, false);

            s_Cache[source] = new Compiled(method, error);
            return method;
        }

        private static MethodInfo Compile(string source, Func<string, string, string> build,
            string typePrefix, string methodName, int lineOffset, out string error)
        {
            // A distinct assembly name per source, because two assemblies with the same name in one
            // domain resolve to whichever loaded first — which would silently run the wrong script.
            var id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tree = CSharpSyntaxTree.ParseText(build(id, source));

            var compilation = CSharpCompilation.Create("UnityFlowCompiled_" + id, new[] { tree }, References(), s_Options);

            var diagnostics = compilation.GetDiagnostics();
            if (HasError(diagnostics))
            {
                error = Describe(diagnostics, lineOffset);
                return null;
            }

            byte[] image;

            using (var stream = new MemoryStream())
            {
                var emit = compilation.Emit(stream);
                if (!emit.Success)
                {
                    error = Describe(emit.Diagnostics, lineOffset);
                    return null;
                }

                image = stream.ToArray();
            }

            // Loaded through the pipeline package's own loader, so an assembly emitted here is
            // registered exactly like one emitted by its eval command and is visible to anything
            // that enumerates loaded assemblies.
            var assembly = Unity.Pipeline.PipelineUtils.LoadFromBytes(image);
            var typeName = typePrefix + id;
            var method = assembly.GetType(typeName)?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);

            if (method == null)
            {
                error = $"the source compiled but {typeName}.{methodName} went missing from the emitted assembly";
                return null;
            }

            error = null;
            return method;
        }

        /// <summary>
        /// Metadata for the assemblies this domain loaded, built once.
        ///
        /// The list itself comes from the pipeline package, so UnityFlow does not carry a second
        /// opinion about which assemblies an author's C# may reach. What is different here is that
        /// it is asked ONCE per domain instead of once per compilation.
        /// </summary>
        private static List<MetadataReference> References()
        {
            if (s_References == null)
                s_References = Unity.Pipeline.Compilation.RoslynCompilationService.GetMetadataReferences(null);

            return s_References;
        }

        private static bool HasError(IEnumerable<Diagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Errors only, with line numbers translated out of the generated wrapper and into the
        /// author's own text — a reader shown "line 13" goes looking at a file that has no line 13.
        /// A negative <paramref name="lineOffset"/> means the source is a single expression and a
        /// position would say nothing.
        /// </summary>
        private static string Describe(IEnumerable<Diagnostic> diagnostics, int lineOffset)
        {
            var builder = new StringBuilder();
            var shown = 0;

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error)
                    continue;

                if (shown >= 4)
                    break;

                if (shown > 0)
                    builder.Append("; ");

                if (lineOffset >= 0)
                {
                    var position = diagnostic.Location.GetLineSpan().StartLinePosition;

                    builder.Append("script line ").Append(Math.Max(0, position.Line - lineOffset))
                        .Append(", col ").Append(position.Character).Append(": ");
                }

                builder.Append(diagnostic.Id).Append(": ").Append(diagnostic.GetMessage());
                shown++;
            }

            return shown == 0 ? "the compiler reported no errors at all" : builder.ToString();
        }

        /// <summary>
        /// The expression wrapper. <see cref="ExpressionWrapperLines"/> counts the lines above the
        /// author's text; change one and the other is wrong.
        /// </summary>
        private static string BuildExpressionSource(string id, string expression) =>
$@"using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace UnityFlowExpressions
{{
    public static class Expr_{id}
    {{
        public static UnityFlow.Editor.State.StateValue Read()
        {{
            return UnityFlow.Editor.State.StateValue.From({expression});
        }}
    }}
}}";

        /// <summary>
        /// The script wrapper. The same usings the pipeline's own evaluator provides, so a script
        /// that ran under the old path still compiles under this one; the trailing <c>return null</c>
        /// is what lets a script with nothing to say end without one.
        /// </summary>
        private static string BuildScriptSource(string id, string code) =>
$@"using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace UnityFlowScripts
{{
    public static class Script_{id}
    {{
        public static object Run()
        {{
{code}
            return null;
        }}
    }}
}}";
    }
}
