using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Everything the backend knows about one node, resolved once and passed around by reference.
    /// Resolving these components is the only place that decides "what kind of thing is this",
    /// so visibility, dispatch and text entry can never disagree about it.
    /// </summary>
    internal readonly struct UGuiNodeParts
    {
        public readonly GameObject GameObject;
        public readonly RectTransform RectTransform;

        /// <summary>Most-derived meaningful component: Selectable, else ScrollRect, else Graphic, else the RectTransform.</summary>
        public readonly Component Control;

        /// <summary>Graphic on this GameObject, if any.</summary>
        public readonly Graphic Graphic;

        public readonly Selectable Selectable;
        public readonly ScrollRect ScrollRect;

        /// <summary>
        /// The Graphic that carries this node's pixels and its raycast: its own Graphic, or the
        /// Selectable's targetGraphic when the control itself draws nothing. Alpha and
        /// raycastTarget are read from here and nowhere else.
        /// </summary>
        public readonly Graphic VisualOwner;

        /// <summary>Nearest Canvas walking up parents. Never Canvas.rootCanvas.</summary>
        public readonly Canvas OwningCanvas;

        /// <summary>Topmost Canvas of the tree: owns the render mode, the raycaster and the pixel rect.</summary>
        public readonly Canvas RootCanvas;

        /// <summary>Camera as GraphicRaycaster would resolve it. Null is a valid answer for overlay canvases.</summary>
        public readonly Camera Camera;

        /// <summary>False when the canvas points at a destroyed camera; <see cref="CameraReason"/> says so.</summary>
        public readonly bool CameraUsable;

        public readonly string CameraReason;

        public readonly int SurfaceId;

        public UGuiNodeParts(
            RectTransform rectTransform,
            Component control,
            Graphic graphic,
            Selectable selectable,
            ScrollRect scrollRect,
            Canvas owningCanvas,
            Canvas rootCanvas,
            Camera camera,
            bool cameraUsable,
            string cameraReason,
            int surfaceId)
        {
            GameObject = rectTransform.gameObject;
            RectTransform = rectTransform;
            Control = control;
            Graphic = graphic;
            Selectable = selectable;
            ScrollRect = scrollRect;
            VisualOwner = graphic != null ? graphic : (selectable != null ? selectable.targetGraphic : null);
            OwningCanvas = owningCanvas;
            RootCanvas = rootCanvas;
            Camera = camera;
            CameraUsable = cameraUsable;
            CameraReason = cameraReason;
            SurfaceId = surfaceId;
        }
    }

    /// <summary>
    /// Finds uGUI nodes, owns their identity, and describes them.
    ///
    /// SOURCE OF NODES. There is exactly one, deliberately: canvases are discovered with
    /// <c>Resources.FindObjectsOfTypeAll&lt;Canvas&gt;()</c> and nodes are then collected per canvas
    /// with <c>GetComponentsInChildren(true, reusableList)</c>.
    ///
    /// Why not <c>FindObjectsByType(FindObjectsInactive.Include, ...)</c>: measured in this editor,
    /// it returns zero results for a GameObject whose hideFlags include <c>HideFlags.DontSave</c>
    /// even though that object sits in a valid, loaded scene — which is precisely how runtime
    /// tooltips, drag ghosts and pooled overlays are created. <c>Resources.FindObjectsOfTypeAll</c>
    /// saw the same object, and so did the canvas-rooted <c>GetComponentsInChildren</c>.
    ///
    /// Why not <c>GraphicRegistry</c> as a fast path when inactive nodes are not requested:
    /// registration happens in <c>Graphic.OnEnable</c>, so the registry holds only active+enabled
    /// Graphics and holds no ScrollRect or graphic-less Selectable at all. Using it for one option
    /// value and the hierarchy walk for another would make the same scene produce two different
    /// node sets depending on a flag — a node findable in a diagnostic snapshot but not in a
    /// selector. One source, filtered early, is worth more than the saved traversal.
    /// </summary>
    internal sealed class UGuiEnumerator
    {
        private struct SurfaceSlot
        {
            public Canvas Canvas;
            public int InstanceId;
            public Canvas RootCanvas;
            public Camera Camera;
            public bool CameraUsable;
            public string CameraReason;
            public bool IsTreeRoot;
            public bool Present;
        }

        private struct NodeSlot
        {
            public RectTransform Transform;
            public int InstanceId;
            public int Version;
            public int SurfaceId;
            public string Name;
            public string Path;
            public int PathKey;
            public int PathEpoch;
        }

        private readonly UGuiRectMath m_RectMath;
        private readonly UGuiVisibility m_Visibility;
        private readonly UGuiTmpBridge m_Tmp;

        private readonly List<SurfaceSlot> m_SurfaceSlots = new List<SurfaceSlot>();
        private readonly Dictionary<int, int> m_SurfaceIdByInstanceId = new Dictionary<int, int>();
        private readonly List<UiSurface> m_Surfaces = new List<UiSurface>();
        private readonly List<int> m_TreeRoots = new List<int>();

        private readonly List<NodeSlot> m_NodeSlots = new List<NodeSlot>();
        private readonly Dictionary<int, int> m_NodeSlotByInstanceId = new Dictionary<int, int>();
        private readonly List<int> m_FreeNodeSlots = new List<int>();

        private readonly List<RectTransform> m_RectBuffer = new List<RectTransform>(512);
        private readonly List<Graphic> m_LabelBuffer = new List<Graphic>(16);
        private readonly List<Transform> m_PathStack = new List<Transform>(32);
        private readonly StringBuilder m_PathBuilder = new StringBuilder(128);
        private readonly Comparison<int> m_TreeRootOrder;

        /// <summary>
        /// One inheritance-chain string per distinct control type, built on first sight and never
        /// again. A scene has a few dozen distinct types and tens of thousands of nodes, so this is
        /// what keeps <see cref="UiNode.Type"/> off the allocating path entirely.
        /// </summary>
        private readonly Dictionary<Type, string> m_TypeChains = new Dictionary<Type, string>();

        /// <summary>
        /// Bumped whenever a rename is observed. A path embeds the names of every ancestor, so one
        /// rename invalidates the cached path of an entire subtree and there is no cheap way to find
        /// which subtree. Comparing one int per node costs nothing and rebuilds them all exactly
        /// once — otherwise a failure report keeps printing the path an object no longer has.
        /// Starts at 1 so a freshly zeroed slot is stale by construction.
        /// </summary>
        private int m_NameEpoch = 1;

        private readonly string m_BackendId;

        public UGuiEnumerator(UGuiRectMath rectMath, UGuiVisibility visibility, UGuiTmpBridge tmp, string backendId)
        {
            m_RectMath = rectMath;
            m_Visibility = visibility;
            m_Tmp = tmp;
            m_BackendId = backendId;
            m_TreeRootOrder = CompareTreeRoots;
        }

        public IReadOnlyList<UiSurface> Surfaces => m_Surfaces;

        /// <summary>
        /// Re-read the canvas set and reclaim slots whose objects are gone. Called once per retry
        /// frame from <c>Settle</c>, never from the per-node path.
        /// </summary>
        public void Sweep()
        {
            for (var i = 0; i < m_SurfaceSlots.Count; i++)
            {
                var slot = m_SurfaceSlots[i];
                slot.Present = false;
                m_SurfaceSlots[i] = slot;
            }

            var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
            for (var i = 0; i < canvases.Length; i++)
            {
                var canvas = canvases[i];

                // Assets, prefab-stage previews and imported objects also come back from this sweep.
                // Only objects that belong to a real loaded scene are part of the running UI.
                if (!canvas.gameObject.scene.IsValid() || EditorSceneManager.IsPreviewSceneObject(canvas))
                    continue;

                InternSurface(canvas);
            }

            RebuildSurfaceViews();
            ReclaimDeadNodeSlots();
        }

        public SurfaceReadiness GetReadiness(int surfaceId)
        {
            if (surfaceId < 0 || surfaceId >= m_SurfaceSlots.Count)
                return SurfaceReadiness.Unavailable;

            var slot = m_SurfaceSlots[surfaceId];
            if (slot.Canvas == null || !slot.Present)
                return SurfaceReadiness.Unavailable;

            if (!slot.Canvas.gameObject.activeInHierarchy || !slot.Canvas.enabled)
                return SurfaceReadiness.NotRendered;

            var rect = ((RectTransform)slot.Canvas.transform).rect;
            if (!float.IsFinite(rect.width) || !float.IsFinite(rect.height) || rect.width <= 0f || rect.height <= 0f)
                return SurfaceReadiness.LayoutPending;

            return SurfaceReadiness.Ready;
        }

        /// <summary>
        /// Fill <paramref name="results"/> with every node the options admit.
        ///
        /// A name-filtered enumeration that finds nothing runs a second pass with the cached names
        /// re-read from the engine. <c>Object.name</c> allocates on every read, so refreshing them
        /// unconditionally would put ~37 B x candidate on the retry path of every <c>waitFor</c>;
        /// doing it only when the first pass came back empty leaves the steady state at zero and
        /// still lets a GameObject renamed in place resolve on the very next frame.
        /// </summary>
        public void Enumerate(int backendIndex, in EnumerateOptions options, List<UiNode> results)
        {
            var before = results.Count;
            Collect(backendIndex, in options, results, refreshNames: false);

            if (options.NameFilter != null && results.Count == before)
                Collect(backendIndex, in options, results, refreshNames: true);
        }

        private void Collect(int backendIndex, in EnumerateOptions options, List<UiNode> results, bool refreshNames)
        {
            var limit = options.MaxNodes > 0 ? options.MaxNodes : int.MaxValue;

            for (var r = 0; r < m_TreeRoots.Count && results.Count < limit; r++)
            {
                var rootSurfaceId = m_TreeRoots[r];
                var rootSlot = m_SurfaceSlots[rootSurfaceId];
                if (rootSlot.Canvas == null)
                    continue;

                rootSlot.Canvas.gameObject.GetComponentsInChildren(true, m_RectBuffer);

                for (var i = 0; i < m_RectBuffer.Count && results.Count < limit; i++)
                {
                    var rectTransform = m_RectBuffer[i];
                    var owningCanvas = UGuiRectMath.FindOwningCanvas(rectTransform);
                    if (owningCanvas == null)
                        continue;

                    if (!m_SurfaceIdByInstanceId.TryGetValue(owningCanvas.GetInstanceID(), out var surfaceId))
                        continue;

                    if (options.SurfaceId.HasValue && options.SurfaceId.Value != surfaceId)
                        continue;

                    var parts = BuildParts(rectTransform, owningCanvas, surfaceId);
                    var testId = ReadTestId(parts.GameObject);

                    // Cheapest gates first, and specifically before anything that touches a name:
                    // UnityEngine.Object.name marshals a fresh string on every access — measured at
                    // 30.6 bytes per read, so ~92 KB per enumeration across 3000 nodes.
                    if (!options.IncludeInternal && IsInternal(parts, testId))
                        continue;

                    var typeChain = TypeChainOf(parts.Control.GetType());

                    if (options.TypeFilter != null && !ChainContains(typeChain, options.TypeFilter))
                        continue;

                    if (options.TestIdFilter != null && !string.Equals(testId, options.TestIdFilter, StringComparison.Ordinal))
                        continue;

                    if (!options.IncludeInactive && !parts.GameObject.activeInHierarchy)
                        continue;

                    var handle = Intern(backendIndex, parts);
                    var name = RefreshIdentity(handle.Slot, rectTransform, refreshNames);

                    if (options.NameFilter != null && !string.Equals(name, options.NameFilter, StringComparison.Ordinal))
                        continue;

                    var node = BuildNode(handle, parts, typeChain, name, testId, options.IncludeInactive, out var included);
                    if (included)
                        results.Add(node);
                }
            }
        }

        /// <summary>Mint (or recover) the stable handle for a node.</summary>
        public UiHandle Intern(int backendIndex, in UGuiNodeParts parts)
        {
            var instanceId = parts.RectTransform.GetInstanceID();

            if (m_NodeSlotByInstanceId.TryGetValue(instanceId, out var existing))
            {
                var slot = m_NodeSlots[existing];
                return new UiHandle(backendIndex, parts.SurfaceId, existing, slot.Version);
            }

            NodeSlot fresh;
            int index;

            if (m_FreeNodeSlots.Count > 0)
            {
                index = m_FreeNodeSlots[m_FreeNodeSlots.Count - 1];
                m_FreeNodeSlots.RemoveAt(m_FreeNodeSlots.Count - 1);
                fresh = m_NodeSlots[index];

                // The slot is being handed to a different object, so every handle previously minted
                // for it must stop resolving rather than silently point at the new occupant.
                fresh.Version++;
            }
            else
            {
                index = m_NodeSlots.Count;
                fresh = default;
                fresh.Version = 0;
                m_NodeSlots.Add(fresh);
            }

            fresh.Transform = parts.RectTransform;
            fresh.InstanceId = instanceId;
            fresh.SurfaceId = parts.SurfaceId;
            fresh.Name = null;
            fresh.Path = null;
            fresh.PathKey = 0;
            fresh.PathEpoch = 0;
            m_NodeSlots[index] = fresh;
            m_NodeSlotByInstanceId[instanceId] = index;

            return new UiHandle(backendIndex, parts.SurfaceId, index, fresh.Version);
        }

        public bool IsAlive(UiHandle handle)
        {
            if (handle.Slot < 0 || handle.Slot >= m_NodeSlots.Count)
                return false;

            var slot = m_NodeSlots[handle.Slot];
            return slot.Version == handle.Version && slot.Transform != null;
        }

        /// <summary>Resolve a handle back to a fully described node, or say precisely why not.</summary>
        public bool TryGetParts(UiHandle handle, out UGuiNodeParts parts, out string reason)
        {
            parts = default;

            if (handle.Slot < 0 || handle.Slot >= m_NodeSlots.Count)
            {
                reason = $"handle {handle} was never minted by the uGUI backend";
                return false;
            }

            var slot = m_NodeSlots[handle.Slot];
            if (slot.Version != handle.Version)
            {
                reason = $"handle {handle} is stale; slot {handle.Slot} has since been reused (now version {slot.Version})";
                return false;
            }

            if (slot.Transform == null)
            {
                reason = $"handle {handle} refers to a destroyed GameObject";
                return false;
            }

            var owningCanvas = UGuiRectMath.FindOwningCanvas(slot.Transform);
            if (owningCanvas == null)
            {
                reason = $"'{slot.Transform.name}' has no Canvas ancestor; it was reparented out of the UI hierarchy";
                return false;
            }

            if (!m_SurfaceIdByInstanceId.TryGetValue(owningCanvas.GetInstanceID(), out var surfaceId))
            {
                reason = $"Canvas '{owningCanvas.name}' is not a known surface; call Settle before probing";
                return false;
            }

            parts = BuildParts(slot.Transform, owningCanvas, surfaceId);
            reason = null;
            return true;
        }

        /// <summary>Describe an arbitrary RectTransform whose canvas is already known — used to identify what a probe hit.</summary>
        public bool TryGetParts(RectTransform rectTransform, Canvas owningCanvas, out UGuiNodeParts parts)
        {
            if (!m_SurfaceIdByInstanceId.TryGetValue(owningCanvas.GetInstanceID(), out var surfaceId))
            {
                parts = default;
                return false;
            }

            parts = BuildParts(rectTransform, owningCanvas, surfaceId);
            return true;
        }

        /// <summary>
        /// The Graphic drawn on top at a screen point, honouring each candidate's own raycast filter
        /// chain through <c>Graphic.Raycast</c> — which is public, needs no EventSystem and works in
        /// edit mode, unlike GraphicRaycaster.
        ///
        /// Ordering is canvas sortingOrder ascending, then hierarchy traversal order, and the LAST
        /// accepted candidate wins: depth-first pre-order is uGUI's own draw order, so later
        /// siblings and deeper children are on top. The engine's authoritative <c>Graphic.depth</c>
        /// is unusable outside play mode (it stays at -1), which is precisely why this backend
        /// reports PerElement rather than CrossSurface fidelity when it uses this path.
        /// </summary>
        public bool TryFindTopmostAt(
            Vector2 screenPoint,
            List<Graphic> scratch,
            out Graphic hit,
            out Camera camera,
            out Canvas rootCanvas)
        {
            hit = null;
            camera = null;
            rootCanvas = null;

            for (var r = 0; r < m_TreeRoots.Count; r++)
            {
                var slot = m_SurfaceSlots[m_TreeRoots[r]];
                if (slot.Canvas == null || !slot.Canvas.isActiveAndEnabled || !slot.CameraUsable)
                    continue;

                slot.Canvas.gameObject.GetComponentsInChildren(true, scratch);

                for (var i = 0; i < scratch.Count; i++)
                {
                    var graphic = scratch[i];
                    if (!graphic.raycastTarget || !graphic.isActiveAndEnabled || graphic.canvasRenderer.cull)
                        continue;

                    if (!RectTransformUtility.RectangleContainsScreenPoint(
                            graphic.rectTransform, screenPoint, slot.Camera, graphic.raycastPadding))
                        continue;

                    if (!graphic.Raycast(screenPoint, slot.Camera))
                        continue;

                    hit = graphic;
                    camera = slot.Camera;
                    rootCanvas = slot.RootCanvas;
                }
            }

            return hit != null;
        }

        public bool TryGetNode(int backendIndex, UiHandle handle, out UiNode node, out string reason)
        {
            node = default;

            if (!TryGetParts(handle, out var parts, out reason))
                return false;

            var refreshed = Intern(backendIndex, parts);
            // No forced name re-read: this path resolves an already-bound handle, where identity
            // comes from the slot and the name is only description. It also runs every retry frame.
            var name = RefreshIdentity(refreshed.Slot, parts.RectTransform, refreshName: false);
            node = BuildNode(refreshed, parts, TypeChainOf(parts.Control.GetType()), name,
                ReadTestId(parts.GameObject), true, out _);
            return true;
        }

        /// <summary>
        /// Refresh and return the cached name of an interned node.
        ///
        /// <c>UnityEngine.Object.name</c> marshals a NEW string on every single access — measured in
        /// this editor at 30.6 bytes per read, roughly 92 KB per enumeration across 3000 nodes. The
        /// name and the path are therefore cached and invalidated together by the node's
        /// hierarchy-shape key (the chain of ancestor instance ids), which covers creation,
        /// destruction and reparenting.
        ///
        /// A GameObject renamed IN PLACE, with no hierarchy change, therefore keeps its cached name
        /// until something asks for a refresh — which the empty-result pass of
        /// <see cref="Enumerate"/> does, so a rename costs one extra pass and never a stale answer.
        /// </summary>
        private string RefreshIdentity(int slotIndex, RectTransform rectTransform, bool refreshName)
        {
            var slot = m_NodeSlots[slotIndex];
            var key = ComputePathKey(rectTransform);

            if (slot.Name != null && slot.PathKey == key && !refreshName)
                return slot.Name;

            var current = rectTransform.name;

            // A path embeds the name, so a rename with no reparent invalidates this node's path AND
            // every descendant's, even though the hierarchy-shape key is unchanged.
            if (slot.Name != null && !string.Equals(slot.Name, current, StringComparison.Ordinal))
                m_NameEpoch++;

            if (slot.PathKey != key)
                slot.Path = null;

            slot.Name = current;
            slot.PathKey = key;
            m_NodeSlots[slotIndex] = slot;
            return slot.Name;
        }

        /// <summary>Hierarchy path from the scene root, e.g. /Canvas/HUD/CoinLabel.</summary>
        public string GetPath(in UGuiNodeParts parts) => GetPath(parts.RectTransform);

        /// <summary>
        /// Path of any transform, cached per interned node. Objects the backend has never enumerated
        /// (something a probe hit outside a canvas) are built fresh, since there is no slot to cache
        /// them in and naming what blocked a tap is worth one string.
        /// </summary>
        public string GetPath(Transform transform)
        {
            if (!m_NodeSlotByInstanceId.TryGetValue(transform.GetInstanceID(), out var index))
                return BuildPath(transform);

            var slot = m_NodeSlots[index];
            var key = ComputePathKey(transform);
            if (slot.Path != null && slot.PathKey == key && slot.PathEpoch == m_NameEpoch)
                return slot.Path;

            slot.Name = transform.name;
            slot.Path = BuildPath(transform);
            slot.PathKey = key;
            slot.PathEpoch = m_NameEpoch;
            m_NodeSlots[index] = slot;
            return slot.Path;
        }

        public Canvas GetCanvas(int surfaceId) =>
            surfaceId >= 0 && surfaceId < m_SurfaceSlots.Count ? m_SurfaceSlots[surfaceId].Canvas : null;

        public bool HasAnyCanvas => m_TreeRoots.Count > 0;

        // ---- construction ------------------------------------------------------------------

        private UGuiNodeParts BuildParts(RectTransform rectTransform, Canvas owningCanvas, int surfaceId)
        {
            var slot = m_SurfaceSlots[surfaceId];
            var go = rectTransform.gameObject;

            // TryGetComponent throughout: GetComponent<T> allocates 674 bytes on every MISS in the
            // editor (measured), and most nodes miss on most of these.
            go.TryGetComponent<Selectable>(out var selectable);
            go.TryGetComponent<ScrollRect>(out var scrollRect);
            go.TryGetComponent<Graphic>(out var graphic);

            Component control;
            if (selectable != null)
                control = selectable;
            else if (scrollRect != null)
                control = scrollRect;
            else if (graphic != null)
                control = graphic;
            else
                control = rectTransform;

            return new UGuiNodeParts(
                rectTransform,
                control,
                graphic,
                selectable,
                scrollRect,
                owningCanvas,
                slot.RootCanvas,
                slot.Camera,
                slot.CameraUsable,
                slot.CameraReason,
                surfaceId);
        }

        private UiNode BuildNode(UiHandle handle, in UGuiNodeParts parts, string typeChain, string name, string testId, bool includeInactive, out bool included)
        {
            var hasRect = false;
            var visibleRect = default(Rect);
            string geometryReason = null;

            if (!parts.CameraUsable)
            {
                geometryReason = parts.CameraReason;
            }
            else if (m_RectMath.TryGetScreenRect(parts.RectTransform, parts.RootCanvas, parts.Camera, out visibleRect, out geometryReason))
            {
                hasRect = true;
            }

            var state = NodeState.None;

            // A geometry failure is the root cause of everything downstream, so it outranks the
            // derived complaints ("could not be projected") that the later checks would produce.
            var reason = geometryReason;

            if (parts.GameObject.activeInHierarchy)
                state |= NodeState.ActiveInHierarchy;

            if (m_Visibility.IsLayoutValid(parts, hasRect, out var layoutReason))
                state |= NodeState.LayoutValid;
            else if (reason == null)
                reason = layoutReason;

            if (m_Visibility.IsVisible(parts, hasRect, visibleRect, out var visibleReason))
                state |= NodeState.Visible;
            else if (reason == null)
                reason = visibleReason;

            if (m_Visibility.IsEnabled(parts, out var enabledReason))
                state |= NodeState.Enabled;
            else if (reason == null)
                reason = enabledReason;

            if (m_Visibility.IsHittable(parts, out var hittableReason))
                state |= NodeState.Hittable;
            else if (reason == null)
                reason = hittableReason;

            if (parts.Selectable is Toggle toggle && toggle.isOn)
                state |= NodeState.Checked;

            if (IsFocusedSelection(parts.GameObject))
                state |= NodeState.Focused;

            included = includeInactive ||
                       ((state & NodeState.ActiveInHierarchy) != 0 && (state & NodeState.Visible) != 0);

            if (!included)
                return default;

            // InjectionPoint stays null here, deliberately. The contract calls it "already
            // hit-probed", and enumeration cannot probe: a node under a modal has a perfectly good
            // projected centre that no pointer can reach, so filling it from geometry alone would
            // publish a coordinate this same backend refuses to use. IUiBackend.TryResolveInjectionPoint
            // is the only thing that answers the question, and it probes.
            return new UiNode(
                handle,
                parts.SurfaceId,
                typeChain,
                name,
                testId,
                GetPath(parts),
                null,
                ReadText(parts),
                ReadValue(parts),
                UGuiRectMath.SpaceOf(parts.RootCanvas),
                hasRect ? visibleRect : (Rect?)null,
                null,
                state,
                parts.OwningCanvas.sortingOrder,
                reason);
        }

        // ---- classification ----------------------------------------------------------------

        /// <summary>
        /// What this backend must hide from an author.
        ///
        /// It is NOT "has no Graphic". Graphic-less containers used to be filtered out here as
        /// layout scaffolding, and that made the single most common wait in a UI flow — waiting for
        /// a panel to appear — impossible: measured in a running game, the panel root object
        /// carried neither a Graphic nor a Selectable, so <c>waitFor { name: ConnectPanel }</c>
        /// reported "matched 0 nodes" while the panel was on screen. Containers are real nodes; they
        /// are simply never <see cref="NodeState.Hittable"/>.
        ///
        /// What remains genuinely internal is what the engine hides from the author in the first
        /// place. TMP's sub-text carriers are the concrete case: TMP_SubMeshUI.AddSubTextObject sets
        /// <c>HideFlags.HideAndDontSave</c> (which includes HideInHierarchy) whenever
        /// <c>TMP_Settings.hideSubTextObjects</c> is on, and each of those carries a MaskableGraphic
        /// that would otherwise enumerate as a sibling of the text that owns it. An explicit
        /// FlowTestId still wins, because an author tagging an object means it.
        /// </summary>
        private static bool IsInternal(in UGuiNodeParts parts, string testId) =>
            testId == null && (parts.GameObject.hideFlags & HideFlags.HideInHierarchy) != 0;

        /// <summary>
        /// The type name plus its base chain, colon separated — "MyButton:Button:Selectable:UIBehaviour".
        ///
        /// The Core contract (<c>SelectorResolver.TypeMatches</c>) splits on ':' and compares each
        /// segment, precisely because real UIs subclass uGUI everywhere (MyButton : Button,
        /// RichInputField : InputField, SlicedImage : Image). Reporting only the leaf name made
        /// the backend accept 'type: Button' during enumeration and the Core throw the very same
        /// node away afterwards, so every type selector over a subclassed control found nothing.
        ///
        /// The walk stops above UIBehaviour: MonoBehaviour, Behaviour and Component are shared by
        /// every UI type and identify nothing.
        /// </summary>
        private string TypeChainOf(Type type)
        {
            if (m_TypeChains.TryGetValue(type, out var chain))
                return chain;

            var builder = new StringBuilder(64);
            for (var t = type;
                 t != null && t != typeof(MonoBehaviour) && t != typeof(Behaviour) && t != typeof(Component);
                 t = t.BaseType)
            {
                if (builder.Length > 0)
                    builder.Append(':');

                builder.Append(t.Name);
            }

            chain = builder.ToString();
            m_TypeChains[type] = chain;
            return chain;
        }

        /// <summary>
        /// Whether a colon-separated type chain contains <paramref name="wanted"/> as a whole
        /// segment. Substring matching would be wrong in the direction that hurts: "Button" occurs
        /// inside "MyButton", so 'type: Button' would silently pull in every project subclass whose
        /// name merely ends in it.
        /// </summary>
        private static bool ChainContains(string chain, string wanted)
        {
            var start = 0;
            while (start < chain.Length)
            {
                var end = chain.IndexOf(':', start);
                if (end < 0)
                    end = chain.Length;

                if (end - start == wanted.Length &&
                    string.Compare(chain, start, wanted, 0, wanted.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }

                start = end + 1;
            }

            return false;
        }

        private static string ReadTestId(GameObject go)
        {
            if (!go.TryGetComponent<FlowTestId>(out var marker))
                return null;

            var id = marker.TestId;
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// A node's text is its own label, or — for a control that draws its caption in a child, the
        /// normal uGUI shape for Button and Toggle — the first label inside it. Without that a
        /// selector could only ever match the inner Text object and never the Button itself.
        /// </summary>
        private string ReadText(in UGuiNodeParts parts)
        {
            var own = ReadTextFrom(parts.Graphic);
            if (own != null)
                return own;

            if (parts.Selectable == null)
                return null;

            parts.GameObject.GetComponentsInChildren(true, m_LabelBuffer);
            for (var i = 0; i < m_LabelBuffer.Count; i++)
            {
                var text = ReadTextFrom(m_LabelBuffer[i]);
                if (text != null)
                    return text;
            }

            return null;
        }

        private string ReadTextFrom(Graphic graphic)
        {
            if (graphic == null)
                return null;

            if (graphic is Text text)
                return text.text;

            return m_Tmp.TryReadText(graphic, out var tmpText) ? tmpText : null;
        }

        private string ReadValue(in UGuiNodeParts parts)
        {
            switch (parts.Selectable)
            {
                case InputField inputField:
                    return inputField.text;
                case Toggle toggle:
                    return toggle.isOn ? "true" : "false";
                case Slider slider:
                    return slider.value.ToString("R", CultureInfo.InvariantCulture);
                case Scrollbar scrollbar:
                    return scrollbar.value.ToString("R", CultureInfo.InvariantCulture);
                case Dropdown dropdown:
                    return dropdown.value.ToString(CultureInfo.InvariantCulture);
            }

            if (parts.Selectable != null && m_Tmp.TryReadInputFieldText(parts.Selectable, out var tmpValue))
                return tmpValue;

            return null;
        }

        private static bool IsFocusedSelection(GameObject go)
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            return eventSystem != null && eventSystem.currentSelectedGameObject == go;
        }

        // ---- surface + slot bookkeeping ----------------------------------------------------

        private void InternSurface(Canvas canvas)
        {
            var instanceId = canvas.GetInstanceID();

            var rootCanvas = UGuiRectMath.FindTreeRootCanvas(canvas);
            var cameraUsable = UGuiRectMath.TryResolveCamera(rootCanvas, out var camera, out var cameraReason);

            if (m_SurfaceIdByInstanceId.TryGetValue(instanceId, out var existing))
            {
                var slot = m_SurfaceSlots[existing];
                slot.Canvas = canvas;
                slot.RootCanvas = rootCanvas;
                slot.Camera = camera;
                slot.CameraUsable = cameraUsable;
                slot.CameraReason = cameraReason;
                slot.IsTreeRoot = rootCanvas == canvas;
                slot.Present = true;
                m_SurfaceSlots[existing] = slot;
                return;
            }

            m_SurfaceSlots.Add(new SurfaceSlot
            {
                Canvas = canvas,
                InstanceId = instanceId,
                RootCanvas = rootCanvas,
                Camera = camera,
                CameraUsable = cameraUsable,
                CameraReason = cameraReason,
                IsTreeRoot = rootCanvas == canvas,
                Present = true
            });

            m_SurfaceIdByInstanceId[instanceId] = m_SurfaceSlots.Count - 1;
        }

        private void RebuildSurfaceViews()
        {
            m_Surfaces.Clear();
            m_TreeRoots.Clear();

            for (var i = 0; i < m_SurfaceSlots.Count; i++)
            {
                var slot = m_SurfaceSlots[i];
                if (!slot.Present || slot.Canvas == null)
                    continue;

                if (slot.IsTreeRoot)
                    m_TreeRoots.Add(i);

                m_Surfaces.Add(new UiSurface(
                    i,
                    m_BackendId,
                    slot.Canvas.name,
                    UGuiRectMath.SpaceOf(slot.RootCanvas),
                    slot.Canvas.sortingOrder,
                    UGuiRectMath.SurfaceRect(slot.RootCanvas)));
            }

            m_TreeRoots.Sort(m_TreeRootOrder);
        }

        /// <summary>
        /// Deterministic surface order so two runs over the same scene enumerate identically.
        /// Instance ids break the sortingOrder tie: they are assigned in creation order and are
        /// stable for the lifetime of a run, and reading them allocates nothing.
        /// </summary>
        private int CompareTreeRoots(int a, int b)
        {
            var left = m_SurfaceSlots[a];
            var right = m_SurfaceSlots[b];

            var byOrder = left.Canvas.sortingOrder.CompareTo(right.Canvas.sortingOrder);
            return byOrder != 0 ? byOrder : left.InstanceId.CompareTo(right.InstanceId);
        }

        private void ReclaimDeadNodeSlots()
        {
            for (var i = 0; i < m_NodeSlots.Count; i++)
            {
                var slot = m_NodeSlots[i];
                if (slot.InstanceId == 0 || slot.Transform != null)
                    continue;

                m_NodeSlotByInstanceId.Remove(slot.InstanceId);
                slot.InstanceId = 0;
                slot.Name = null;
                slot.Path = null;
                slot.PathKey = 0;
                slot.PathEpoch = 0;
                m_NodeSlots[i] = slot;
                m_FreeNodeSlots.Add(i);
            }
        }

        private string BuildPath(Transform transform)
        {
            m_PathStack.Clear();
            for (var t = transform; t != null; t = t.parent)
                m_PathStack.Add(t);

            m_PathBuilder.Clear();
            for (var i = m_PathStack.Count - 1; i >= 0; i--)
                m_PathBuilder.Append('/').Append(m_PathStack[i].name);

            return m_PathBuilder.ToString();
        }

        /// <summary>
        /// Cheap identity of a node's position in the hierarchy, used to keep cached path strings
        /// out of the retry loop. Instance ids only, because reading Transform.name allocates a new
        /// string on every call. A pure rename with no reparent therefore keeps the cached path;
        /// that is the deliberate trade for a per-frame path that costs nothing.
        /// </summary>
        private static int ComputePathKey(Transform transform)
        {
            unchecked
            {
                var hash = 17;
                for (var t = transform; t != null; t = t.parent)
                    hash = hash * 31 + t.GetInstanceID();

                return hash;
            }
        }
    }
}
