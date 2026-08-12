using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// A uGUI scene each test gets to itself, by building into the active scene and removing
    /// exactly what it built.
    ///
    /// The obvious implementation — <c>NewScene(EmptyScene, Additive)</c> per test, closed in
    /// teardown — CANNOT WORK, and measuring that is what this fixture is built around. Unity's
    /// EditMode test runner has already replaced the whole scene setup with one untitled, empty
    /// scene before the first [SetUp] runs (measured: sceneCount=1, name="", path=""), and Unity
    /// permits at most one untitled scene to exist at a time. The additive call therefore throws
    /// "Cannot create a new scene additively with an untitled scene unsaved" for EVERY test, first
    /// one included — it is not a leak from a previous test.
    ///
    /// Adopting the runner's scene is not a concession, it is the point: that scene is already
    /// disposable and already empty of product UI, which is exactly what the additive scene was
    /// trying to obtain. A preview scene is not an option either — the uGUI enumerator's sweep
    /// skips <c>EditorSceneManager.IsPreviewSceneObject</c>, so nothing built in one is enumerable.
    ///
    /// ISOLATION. Every root GameObject present at [SetUp] is recorded, and teardown destroys every
    /// root that is not in that set. Tests create objects directly with <c>new GameObject(...)</c>
    /// as well as through the helpers here, so tracking the helper calls alone would leak; the
    /// difference of the root set catches both. Leaks are not cosmetic — a single surviving
    /// FlowTestTarget turns "no instance exists" into "one instance exists" and the next test's
    /// assertion inverts.
    ///
    /// The user's scene stays untouched: nothing here opens, closes or replaces a scene, so no run
    /// can produce a save prompt. Scene dirtiness is deliberately not restored — the only scene a
    /// [SetUp] can ever see is the runner's untitled one, which the runner discards when the run
    /// ends, so there is no user edit for a dirty flag to endanger.
    /// </summary>
    public abstract class UGuiSceneFixture
    {
        /// <summary>
        /// Sorting order for every canvas a fixture creates. The uGUI backend orders candidate
        /// surfaces by sortingOrder and lets the last accepted one win, so the maximum value makes
        /// a fixture element unconditionally topmost regardless of what the user has open.
        /// </summary>
        private const int TopSortingOrder = short.MaxValue;

        private Scene m_Scene;
        private readonly HashSet<int> m_PreExistingRoots = new HashSet<int>();
        private readonly List<GameObject> m_RootBuffer = new List<GameObject>(32);
        private BackendRegistry m_Registry;
        private SelectorResolver m_Resolver;

        [SetUp]
        public void OpenFixtureScene()
        {
            m_Scene = SceneManager.GetActiveScene();

            if (!m_Scene.IsValid() || !m_Scene.isLoaded)
            {
                throw new InvalidOperationException(
                    "no valid active scene, so nothing this fixture creates would be enumerable: " +
                    $"name='{m_Scene.name}' valid={m_Scene.IsValid()} loaded={m_Scene.isLoaded}");
            }

            m_PreExistingRoots.Clear();
            m_Scene.GetRootGameObjects(m_RootBuffer);
            foreach (var root in m_RootBuffer)
                m_PreExistingRoots.Add(root.GetInstanceID());
        }

        [TearDown]
        public void CloseFixtureScene()
        {
            m_Registry = null;
            m_Resolver = null;

            if (!m_Scene.IsValid() || !m_Scene.isLoaded)
                return;

            m_Scene.GetRootGameObjects(m_RootBuffer);
            for (var i = m_RootBuffer.Count - 1; i >= 0; i--)
            {
                var root = m_RootBuffer[i];
                if (!m_PreExistingRoots.Contains(root.GetInstanceID()))
                    UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>Backends discovered once per test, exactly as a real run discovers them.</summary>
        protected BackendRegistry Registry => m_Registry ??= new BackendRegistry();

        protected SelectorResolver Resolver => m_Resolver ??= new SelectorResolver(Registry);

        /// <summary>
        /// Forget the discovered backends so the next access rediscovers them. A run gets a fresh
        /// registry, so any test claiming something is stable "across runs" must do the same.
        /// </summary>
        protected void ResetBackends()
        {
            m_Registry = null;
            m_Resolver = null;
        }

        protected IUiBackend Backend
        {
            get
            {
                var backend = Registry.ById("ugui");
                if (backend == null)
                {
                    throw new InvalidOperationException(
                        "the uGUI backend is not active, so this test cannot run: " +
                        string.Join("; ", Registry.Rejected));
                }

                return backend;
            }
        }

        /// <summary>Force layout and re-sweep, the way the runner does at the top of every retry frame.</summary>
        protected void Settle() => Backend.Settle();

        protected Canvas CreateCanvas(
            string name,
            RenderMode renderMode = RenderMode.ScreenSpaceOverlay,
            Camera worldCamera = null)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = renderMode;

            if (worldCamera != null)
                canvas.worldCamera = worldCamera;

            canvas.sortingOrder = TopSortingOrder;
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>A camera tagged MainCamera, so <c>Camera.main</c> resolves inside the fixture scene.</summary>
        protected Camera CreateCamera(string name, Vector3 position)
        {
            var go = new GameObject(name) { tag = "MainCamera" };
            var camera = go.AddComponent<Camera>();
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 1000f;
            return camera;
        }

        /// <summary>A drawn element: it owns a Graphic, so it is visible and hit-testable.</summary>
        protected RectTransform CreateElement(string name, Transform parent, Vector2 size, Vector2 position = default)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        /// <summary>
        /// A layout container: no Graphic at all. These are real, enumerable nodes — the panel a
        /// flow waits for usually is one — they are simply never hittable.
        /// </summary>
        protected RectTransform CreateContainer(string name, Transform parent, Vector2 size, Vector2 position = default)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        protected Button CreateButton(string name, Transform parent, Vector2 size, Vector2 position = default)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            var button = go.GetComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            return button;
        }

        /// <summary>
        /// Assign a <see cref="FlowTestId"/> through SerializedObject: the id is a private
        /// serialized field with no setter, because in the product it is filled in the Inspector.
        /// </summary>
        protected void SetTestId(GameObject target, string testId)
        {
            var marker = target.AddComponent<FlowTestId>();
            var serialized = new SerializedObject(marker);
            var property = serialized.FindProperty("m_TestId");

            if (property == null)
                throw new InvalidOperationException("FlowTestId no longer serializes 'm_TestId'; this fixture must be updated.");

            property.stringValue = testId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Every node the backend can see, hidden ones included.</summary>
        protected List<UiNode> EnumerateAll()
        {
            var options = EnumerateOptions.Diagnostic;
            var nodes = new List<UiNode>(256);
            Settle();
            Backend.Enumerate(in options, nodes);
            return nodes;
        }

        /// <summary>The one node with this name, or a failure naming how many were actually found.</summary>
        protected UiNode NodeNamed(string name)
        {
            var matches = new List<UiNode>();
            foreach (var node in EnumerateAll())
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                    matches.Add(node);
            }

            Assert.AreEqual(1, matches.Count, $"expected exactly one node named '{name}', found {matches.Count}");
            return matches[0];
        }

        protected static Selector ByName(string name) => new Selector(
            null, null, null, TextMatchMode.Exact, null, name, null, null, null, null, null,
            SelectorForm.Mapping, 1, 1);

        protected static Selector ByTestId(string testId) => new Selector(
            null, testId, null, TextMatchMode.Exact, null, null, null, null, null, null, null,
            SelectorForm.Mapping, 1, 1);
    }

    /// <summary>
    /// Advances a flow enumerator one frame at a time, with exactly the yield vocabulary
    /// <see cref="FlowDriver"/> implements.
    ///
    /// It is a synchronous stand-in rather than the real driver because the driver pumps from
    /// EditorApplication.update, and a test that has to give the editor a frame back cannot observe
    /// or control WHEN a step is between frames. Here the test owns the frame boundary, which is
    /// what makes "the command advanced across three frames" and "the node appeared 200ms into the
    /// window" assertable instead of approximate.
    /// </summary>
    public sealed class FlowPump
    {
        private readonly Stack<IEnumerator> m_Stack = new Stack<IEnumerator>();
        private FlowYield m_Pending;
        private bool m_Done;

        public FlowPump(IEnumerator routine)
        {
            if (routine == null) throw new ArgumentNullException(nameof(routine));
            m_Stack.Push(routine);
        }

        /// <summary>Frames pumped so far.</summary>
        public int Frames { get; private set; }

        /// <summary>Advance one frame. Returns true once the routine has run to completion.</summary>
        public bool Frame()
        {
            if (m_Done)
                return true;

            Frames++;

            if (m_Pending != null)
            {
                if (!m_Pending.IsDone)
                    return false;

                m_Pending = null;
            }

            while (m_Stack.Count > 0)
            {
                var current = m_Stack.Peek();

                if (!current.MoveNext())
                {
                    m_Stack.Pop();
                    continue;
                }

                switch (current.Current)
                {
                    case null:
                        return false;

                    case FlowYield instruction:
                        instruction.Begin();
                        if (!instruction.IsDone)
                        {
                            m_Pending = instruction;
                            return false;
                        }

                        continue;

                    case IEnumerator nested:
                        m_Stack.Push(nested);
                        continue;

                    default:
                        throw new InvalidOperationException(
                            $"a flow step yielded an unsupported value of type '{current.Current.GetType().FullName}'");
                }
            }

            m_Done = true;
            return true;
        }

        /// <summary>
        /// Pump until the routine finishes. <paramref name="maxFrames"/> is a hard stop so a step
        /// that never terminates fails the test with a precise reason instead of hanging the editor.
        /// </summary>
        public void RunToCompletion(int maxFrames, Action onFrame = null)
        {
            while (!Frame())
            {
                onFrame?.Invoke();

                if (Frames > maxFrames)
                    throw new InvalidOperationException($"the flow did not finish within {maxFrames} pumped frames");
            }
        }
    }

    /// <summary>
    /// Reaches the two members of the runner that are internal to UnityFlow.Editor.
    ///
    /// The alternative — an InternalsVisibleTo on the frozen contract assembly — would widen the
    /// product's surface for the sole benefit of tests. Reflection keeps the change on this side of
    /// the boundary, and every lookup throws with the member's name if it ever moves, so a contract
    /// change fails the suite loudly rather than silently skipping the assertion.
    /// </summary>
    public static class FlowInternals
    {
        private static readonly MethodInfo s_SetStep = Setter(typeof(StepContext), nameof(StepContext.Step));
        private static readonly MethodInfo s_SetDeadline = Setter(typeof(StepContext), nameof(StepContext.Deadline));

        private static readonly MethodInfo s_BindArguments =
            typeof(FlowCommandInvoker).GetMethod(
                "BindArguments",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("FlowCommandInvoker no longer declares BindArguments(StepContext, MethodInfo).");

        private static readonly MethodInfo s_TryReadDragEndpoint = Static(typeof(StepLibrary), "TryReadEndpoint");
        private static readonly MethodInfo s_TryAdvanceDragEndpoint = Static(typeof(StepLibrary), "TryAdvanceEndpoint");

        /// <summary>
        /// The backing field of <see cref="BackendRegistry.InputDriver"/>, which is a get-only
        /// property filled by TypeCache discovery.
        ///
        /// A test cannot get a real driver: injection needs play mode, and a test double that
        /// ADVERTISED itself would be discovered by every real run in this editor session and could
        /// hijack one. So the double stays undiscoverable (its constructor is not public) and is
        /// installed here instead, on the one registry the test owns.
        /// </summary>
        private static readonly FieldInfo s_InputDriverField =
            typeof(BackendRegistry).GetField($"<{nameof(BackendRegistry.InputDriver)}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "BackendRegistry.InputDriver is no longer a get-only auto-property, so its backing field cannot be found.");

        public static void SetStep(StepContext context, FlowStep step) =>
            s_SetStep.Invoke(context, new object[] { step });

        /// <summary>Install a driver on a registry the test owns. Never touches a registry a run built.</summary>
        public static void SetInputDriver(BackendRegistry registry, IInputDriver driver) =>
            s_InputDriverField.SetValue(registry, driver);

        /// <summary>
        /// Build one end of a drag from the step's arguments, as the verb does. Null when the verb
        /// refused the arguments, in which case the refusal is on the context.
        /// </summary>
        public static object ReadDragEndpoint(StepContext context, string selectorKey, string pointKey)
        {
            var args = new object[] { context, selectorKey, pointKey, null };
            return Call(s_TryReadDragEndpoint, args) ? args[3] : null;
        }

        /// <summary>One frame's attempt at resolving that end, exactly as the verb makes it.</summary>
        public static bool AdvanceDragEndpoint(StepContext context, object endpoint, out bool fatal)
        {
            var args = new object[] { context, endpoint, null };
            var resolved = Call(s_TryAdvanceDragEndpoint, args);
            fatal = (bool)args[2];
            return resolved;
        }

        /// <summary>
        /// Read one of StepLibrary's private constants, so a test asserts against the number the verb
        /// actually uses instead of a copy that can drift away from it. The lookup names the constant
        /// if it is ever renamed, which fails the suite loudly rather than skipping the assertion.
        /// </summary>
        public static T StepLibraryConstant<T>(string name)
        {
            var field = typeof(StepLibrary).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        ?? throw new InvalidOperationException($"StepLibrary no longer declares a '{name}' constant.");

            return (T)field.GetRawConstantValue();
        }

        /// <summary>Read a field of the verb's private endpoint record.</summary>
        public static T DragEndpointField<T>(object endpoint, string name)
        {
            var field = endpoint.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException($"the drag endpoint no longer has a '{name}' field.");

            return (T)field.GetValue(endpoint);
        }

        private static bool Call(MethodInfo method, object[] args)
        {
            try
            {
                return (bool)method.Invoke(null, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static MethodInfo Static(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{type.Name} no longer declares a static '{name}' method.");

        public static void SetDeadline(StepContext context, double deadline) =>
            s_SetDeadline.Invoke(context, new object[] { deadline });

        /// <summary>
        /// Call the argument binder directly, rethrowing what it threw. Going through
        /// <c>Invoke</c> instead would turn every binding failure into a StepFailure string and
        /// lose the exception type the contract promises.
        /// </summary>
        public static object[] BindArguments(StepContext context, MethodInfo method)
        {
            try
            {
                return (object[])s_BindArguments.Invoke(null, new object[] { context, method });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static MethodInfo Setter(Type type, string propertyName)
        {
            var property = type.GetProperty(propertyName)
                           ?? throw new InvalidOperationException($"{type.Name} no longer declares a '{propertyName}' property.");

            return property.GetSetMethod(nonPublic: true)
                   ?? throw new InvalidOperationException($"{type.Name}.{propertyName} no longer has a setter.");
        }
    }
}
