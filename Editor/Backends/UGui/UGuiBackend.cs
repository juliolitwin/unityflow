using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// The uGUI adapter: what exists on the canvases, where it is, and whether a pointer put there
    /// would really reach it.
    ///
    /// OCCLUSION IS REPORTED HONESTLY, NEVER OPTIMISTICALLY. Cross-surface occlusion needs
    /// <c>EventSystem.RaycastAll</c>, and that needs play mode: EventSystem and BaseRaycaster are
    /// not [ExecuteAlways] (Graphic, Selectable, Mask and RectMask2D are), so in edit mode
    /// <c>EventSystem.current</c> stays null, every Graphic keeps <c>depth == -1</c>, and
    /// GraphicRaycaster skips it — a direct GraphicRaycaster.Raycast() returned zero hits. Measured
    /// here: a freshly built overlay button reported depth -1 while <c>Graphic.Raycast</c> on the
    /// same element returned true.
    ///
    /// So there are exactly two probe modes and the backend reports which one it used.
    /// <c>Graphic.Raycast(screenPoint, camera)</c> is public, works with a null EventSystem, and
    /// evaluates the entire ICanvasRaycastFilter chain (CanvasGroup.blocksRaycasts, Mask,
    /// RectMask2D, alphaHitTest), which is why per-element fidelity is available everywhere.
    /// </summary>
    public sealed class UGuiBackend : IUiBackend
    {
        private const string BackendIdentifier = "ugui";

        private readonly UGuiRectMath m_RectMath = new UGuiRectMath();
        private readonly UGuiVisibility m_Visibility = new UGuiVisibility();
        private readonly UGuiTmpBridge m_Tmp = new UGuiTmpBridge();
        private readonly UGuiEnumerator m_Enumerator;
        private readonly UGuiSemanticDispatch m_Dispatch;
        private readonly UGuiTextAdapter m_TextAdapter;
        private readonly UGuiFocusRing m_FocusRing;
        private readonly UGuiPointerState m_PointerState = new UGuiPointerState();

        private readonly List<Graphic> m_HitBuffer = new List<Graphic>(512);
        private readonly List<RaycastResult> m_RaycastResults = new List<RaycastResult>(16);
        private readonly Vector2[] m_ProbeGrid = new Vector2[UGuiRectMath.ProbeGridLength];

        private PointerEventData m_ProbePointer;
        private EventSystem m_ProbeBoundEventSystem;
        private bool m_Swept;

        public UGuiBackend()
        {
            m_Enumerator = new UGuiEnumerator(m_RectMath, m_Visibility, m_Tmp, BackendIdentifier);
            m_Dispatch = new UGuiSemanticDispatch();
            m_TextAdapter = new UGuiTextAdapter(m_Tmp, m_Visibility);
            m_FocusRing = new UGuiFocusRing(this, m_Enumerator, m_Visibility);
        }

        public string Id => BackendIdentifier;

        /// <summary>
        /// Above UI Toolkit: most projects that have both keep their interactive UI in legacy
        /// uGUI, so when a selector could match in either system the uGUI answer is the one meant.
        /// </summary>
        public int Priority => 100;

        public WriteCapability Capabilities =>
            WriteCapability.SemanticDispatch | WriteCapability.TextEntry | WriteCapability.Activate;

        public int BackendIndex { get; set; }

        public bool IsAvailable(out string reason)
        {
            EnsureSwept();

            if (!m_Enumerator.HasAnyCanvas)
            {
                reason = "no uGUI Canvas exists in any loaded scene; open a scene containing UI or enter play mode";
                return false;
            }

            reason = null;
            return true;
        }

        public OcclusionFidelity OcclusionFidelity =>
            EventSystem.current != null && EditorApplication.isPlaying
                ? OcclusionFidelity.CrossSurface
                : OcclusionFidelity.PerElement;

        /// <summary>
        /// uGUI always has a navigation graph — <c>Selectable.navigation</c> exists whether or not
        /// anything is currently selected — so the ring is never null here. Whether it can be READ
        /// depends on play mode, and the ring itself says so through
        /// <see cref="IFocusRing.IsAvailable"/> rather than by vanishing: a null returned in edit
        /// mode would be reported as "this backend has no focus ring", which is false and points
        /// the reader away from the real cause.
        /// </summary>
        public IFocusRing FocusRing
        {
            get
            {
                // Handles minted by the ring index the enumerator's slot table, so the same
                // Settle-before-probe rule that every other member follows applies here too.
                EnsureSwept();
                return m_FocusRing;
            }
        }

        // ---- Read --------------------------------------------------------------------------

        public IReadOnlyList<UiSurface> GetSurfaces()
        {
            EnsureSwept();
            return m_Enumerator.Surfaces;
        }

        public SurfaceReadiness GetReadiness(int surfaceId)
        {
            EnsureSwept();
            return m_Enumerator.GetReadiness(surfaceId);
        }

        public void Settle()
        {
            // Layout first, then discovery, so geometry read straight afterwards reflects anything
            // that was rebuilt this frame rather than the previous frame's rects.
            Canvas.ForceUpdateCanvases();
            m_Enumerator.Sweep();
            m_Swept = true;
        }

        public void Enumerate(in EnumerateOptions options, List<UiNode> results)
        {
            EnsureSwept();
            m_Enumerator.Enumerate(BackendIndex, options, results);
        }

        public bool TryGetNode(UiHandle handle, out UiNode node)
        {
            EnsureSwept();
            return m_Enumerator.TryGetNode(BackendIndex, handle, out node, out _);
        }

        public bool IsHandleAlive(UiHandle handle) => m_Enumerator.IsAlive(handle);

        public bool IsVisible(UiHandle handle, out string reason)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out reason))
                return false;

            var hasRect = TryGetVisibleRect(parts, out var visibleRect, out var geometryReason);
            if (!hasRect && geometryReason != null)
            {
                reason = geometryReason;
                return false;
            }

            return m_Visibility.IsVisible(parts, hasRect, visibleRect, out reason);
        }

        public bool IsActionable(UiHandle handle, out string reason)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out reason))
                return false;

            var hasRect = TryGetVisibleRect(parts, out var visibleRect, out var geometryReason);
            if (!hasRect && geometryReason != null)
            {
                reason = geometryReason;
                return false;
            }

            return m_Visibility.IsActionable(parts, hasRect, visibleRect, out reason);
        }

        // ---- Probe -------------------------------------------------------------------------

        public bool TryResolveInjectionPoint(UiHandle handle, out Vector2 screenPoint, out string reason)
        {
            EnsureSwept();
            screenPoint = default;

            if (!m_Enumerator.TryGetParts(handle, out var parts, out reason))
                return false;

            if (!parts.CameraUsable)
            {
                reason = parts.CameraReason;
                return false;
            }

            if (!TryGetVisibleRect(parts, out var visibleRect, out var geometryReason))
            {
                reason = geometryReason;
                return false;
            }

            if (visibleRect.width <= 0f || visibleRect.height <= 0f)
            {
                reason = $"'{m_Enumerator.GetPath(parts)}' is clipped to nothing by an ancestor mask or by the edge " +
                         "of its surface, so no pointer position can land on it";
                return false;
            }

            // The projected centre of the element, not the centre of its bounds. For a canvas yawed
            // 55 degrees the AABB centre sat 5.91 px outside the real element centre, and a 25
            // degree rotation grew a 400x200 element's AABB to 254x199 px.
            if (!m_RectMath.TryGetCentrePoint(parts.RectTransform, parts.Camera, out var candidate, out var centreReason))
            {
                reason = centreReason;
                return false;
            }

            var centreValid = UGuiRectMath.ContainsPoint(parts.RectTransform, candidate, parts.Camera);
            var blockerPath = (string)null;

            // Counted, never assumed. The grid's first entry is the centre of the CLIPPED visible
            // rect, which coincides with the projected element centre only for an unclipped
            // axis-aligned element, and a point outside the element is skipped without being probed
            // at all — so neither the grid length nor grid length + 1 is the number actually tried.
            var attempted = 0;

            if (centreValid)
            {
                attempted++;
                var centreHit = Probe(parts, candidate);
                if (centreHit.IsOnTarget)
                {
                    screenPoint = candidate;
                    reason = null;
                    return true;
                }

                blockerPath = DescribeBlocker(centreHit);
            }

            UGuiRectMath.BuildProbeGrid(visibleRect, m_ProbeGrid);
            for (var i = 0; i < m_ProbeGrid.Length; i++)
            {
                var point = m_ProbeGrid[i];
                if (centreValid && point == candidate)
                    continue;

                if (!UGuiRectMath.ContainsPoint(parts.RectTransform, point, parts.Camera))
                    continue;

                attempted++;
                var hit = Probe(parts, point);
                if (hit.IsOnTarget)
                {
                    screenPoint = point;
                    reason = null;
                    return true;
                }

                blockerPath ??= DescribeBlocker(hit);
            }

            var targetPath = m_Enumerator.GetPath(parts);

            if (attempted == 0)
            {
                reason = $"'{targetPath}' has a visible rect of {UGuiRectMath.Format(visibleRect)} but not one of the " +
                         $"{m_ProbeGrid.Length + 1} candidate positions lies inside the element itself; the element is " +
                         "rotated or skewed so far that its axis-aligned bounds no longer overlap it";
                return false;
            }

            reason = blockerPath != null
                ? $"'{targetPath}' is obscured by {blockerPath}; every one of the {attempted} probe " +
                  $"positions inside its visible rect {UGuiRectMath.Format(visibleRect)} landed on something else"
                : $"'{targetPath}' rejected all {attempted} probe positions inside its visible rect " +
                  $"{UGuiRectMath.Format(visibleRect)}; no raycast-accepting graphic answered there " +
                  "(check raycastTarget, blocksRaycasts and alphaHitTest)";
            return false;
        }

        public HitResult HitTest(UiHandle handle, Vector2 screenPoint)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out var reason))
                return HitResult.Unavailable(reason);

            if (!parts.CameraUsable)
                return HitResult.Unavailable(parts.CameraReason);

            return Probe(parts, screenPoint);
        }

        public PointerDragState GetDragState()
        {
            if (!m_PointerState.TryRead(out var pressed, out var dragging, out var handler, out var reason))
                return PointerDragState.CannotRead(reason);

            if (!pressed)
                return PointerDragState.Nothing("no uGUI pointer is holding its left button");

            if (handler == null)
            {
                return PointerDragState.Nothing(
                    "a uGUI pointer is holding its left button, but nothing under the press point implements IDragHandler, " +
                    "so no drag can ever begin from it");
            }

            var path = m_Enumerator.GetPath(handler.transform);
            return dragging ? PointerDragState.Dragging(path) : PointerDragState.Armed(path);
        }

        // ---- Write -------------------------------------------------------------------------

        public bool TryDispatch(UiHandle handle, PointerGesture gesture, Vector2 screenPoint, out string error)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out error))
                return false;

            var hasRect = TryGetVisibleRect(parts, out var visibleRect, out _);
            if (!m_Visibility.IsActionable(parts, hasRect, visibleRect, out var actionableReason))
            {
                error = $"'{m_Enumerator.GetPath(parts)}' cannot be interacted with: {actionableReason}";
                return false;
            }

            var hitObject = FindHit(parts, screenPoint, out var raycast);
            if (hitObject == null)
            {
                error = $"nothing accepted a pointer hit at {UGuiRectMath.Format(screenPoint)}; " +
                        $"'{m_Enumerator.GetPath(parts)}' was the intended target";
                return false;
            }

            if (!IsTargetOrDescendant(parts, hitObject))
            {
                error = $"'{m_Enumerator.GetPath(parts)}' is obscured by {m_Enumerator.GetPath(hitObject.transform)} " +
                        $"at {UGuiRectMath.Format(screenPoint)}; refusing to dispatch, because forcing the " +
                        "event onto the target would make a modal-blocked UI look clickable";
                return false;
            }

            return m_Dispatch.TryDispatch(parts, hitObject, raycast, gesture, screenPoint, out error);
        }

        public bool TrySetText(UiHandle handle, string text, out string error)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out error))
                return false;

            return m_TextAdapter.TrySetText(parts, text, out error);
        }

        public bool TryActivate(UiHandle handle, out string error)
        {
            EnsureSwept();

            if (!m_Enumerator.TryGetParts(handle, out var parts, out error))
                return false;

            if (!m_Visibility.IsEnabled(parts, out var enabledReason))
            {
                error = $"'{m_Enumerator.GetPath(parts)}' cannot be activated: {enabledReason}";
                return false;
            }

            // Deliberately the control's own event and nothing more general: this path skips the
            // pointer entirely and proves nothing about reachability, so it must at least be the
            // exact action the control exposes.
            switch (parts.Selectable)
            {
                case Button button:
                    button.onClick.Invoke();
                    error = null;
                    return true;

                case Toggle toggle:
                    toggle.isOn = !toggle.isOn;
                    error = null;
                    return true;

                case Dropdown dropdown:
                    dropdown.Show();
                    error = null;
                    return true;
            }

            error = $"'{m_Enumerator.GetPath(parts)}' is a {parts.Control.GetType().Name}; it exposes no single " +
                    "activation action, so it can only be driven by a pointer gesture";
            return false;
        }

        // ---- internals ---------------------------------------------------------------------

        private void EnsureSwept()
        {
            if (m_Swept)
                return;

            m_Enumerator.Sweep();
            m_Swept = true;
        }

        private bool TryGetVisibleRect(in UGuiNodeParts parts, out Rect visibleRect, out string reason)
        {
            if (!parts.CameraUsable)
            {
                visibleRect = default;
                reason = parts.CameraReason;
                return false;
            }

            return m_RectMath.TryGetScreenRect(parts.RectTransform, parts.RootCanvas, parts.Camera, out visibleRect, out reason);
        }

        private HitResult Probe(in UGuiNodeParts parts, Vector2 screenPoint)
        {
            var hitObject = FindHit(parts, screenPoint, out _);
            if (hitObject == null)
                return HitResult.NoHit;

            if (hitObject == parts.GameObject)
                return new HitResult(HitOutcome.Self, HandleFor(hitObject, parts), m_Enumerator.GetPath(hitObject.transform));

            if (MaskUtilities.IsDescendantOrSelf(parts.RectTransform, hitObject.transform))
            {
                // The common case, not an edge case: a Button's raycast target is nearly always a
                // child Image, so demanding an exact match would reject almost every real click.
                return new HitResult(HitOutcome.Descendant, HandleFor(hitObject, parts), m_Enumerator.GetPath(hitObject.transform));
            }

            return new HitResult(HitOutcome.Occluded, HandleFor(hitObject, parts), m_Enumerator.GetPath(hitObject.transform));
        }

        /// <summary>
        /// What is on top at a screen point. In play mode with a live EventSystem this is the real
        /// raycast every input module runs; otherwise it is the per-element scan, which honours each
        /// candidate's full raycast filter chain but orders candidates by canvas sorting order and
        /// hierarchy draw order rather than by the engine's own depth (edit mode leaves every
        /// Graphic at depth -1). That is exactly the difference reported as
        /// <see cref="OcclusionFidelity.PerElement"/>.
        /// </summary>
        private GameObject FindHit(in UGuiNodeParts parts, Vector2 screenPoint, out RaycastResult raycast)
        {
            raycast = default;

            if (OcclusionFidelity == OcclusionFidelity.CrossSurface)
            {
                BindProbePointer();
                m_ProbePointer.Reset();
                m_ProbePointer.position = screenPoint;
                m_ProbePointer.button = PointerEventData.InputButton.Left;
                m_ProbePointer.pointerId = -1;

                m_RaycastResults.Clear();
                EventSystem.current.RaycastAll(m_ProbePointer, m_RaycastResults);

                if (m_RaycastResults.Count == 0)
                    return null;

                raycast = m_RaycastResults[0];
                return raycast.gameObject;
            }

            if (!m_Enumerator.TryFindTopmostAt(screenPoint, m_HitBuffer, out var graphic, out var camera, out var rootCanvas))
                return null;

            raycast = BuildRaycastResult(graphic, camera, rootCanvas, screenPoint);
            return graphic.gameObject;
        }

        private static RaycastResult BuildRaycastResult(Graphic graphic, Camera camera, Canvas rootCanvas, Vector2 screenPoint)
        {
            RectTransformUtility.ScreenPointToWorldPointInRectangle(graphic.rectTransform, screenPoint, camera, out var worldPosition);

            return new RaycastResult
            {
                gameObject = graphic.gameObject,
                module = rootCanvas.TryGetComponent<GraphicRaycaster>(out var raycaster) ? raycaster : null,
                distance = 0f,
                index = 0f,
                depth = graphic.depth,
                sortingLayer = rootCanvas.sortingLayerID,
                sortingOrder = rootCanvas.sortingOrder,
                worldPosition = worldPosition,
                worldNormal = -graphic.transform.forward,
                screenPosition = screenPoint,
                displayIndex = rootCanvas.targetDisplay
            };
        }

        private bool IsTargetOrDescendant(in UGuiNodeParts parts, GameObject hitObject) =>
            hitObject == parts.GameObject || MaskUtilities.IsDescendantOrSelf(parts.RectTransform, hitObject.transform);

        private UiHandle HandleFor(GameObject hitObject, in UGuiNodeParts parts)
        {
            if (hitObject == parts.GameObject)
                return m_Enumerator.Intern(BackendIndex, parts);

            var rectTransform = hitObject.transform as RectTransform;
            if (rectTransform == null)
                return UiHandle.None;

            var owningCanvas = UGuiRectMath.FindOwningCanvas(rectTransform);
            if (owningCanvas == null)
                return UiHandle.None;

            return m_Enumerator.TryGetParts(rectTransform, owningCanvas, out var hitParts)
                ? m_Enumerator.Intern(BackendIndex, hitParts)
                : UiHandle.None;
        }

        private static string DescribeBlocker(in HitResult hit) =>
            hit.Outcome == HitOutcome.Occluded ? hit.HitPath : null;

        private void BindProbePointer()
        {
            var current = EventSystem.current;
            if (m_ProbePointer != null && m_ProbeBoundEventSystem == current)
                return;

            m_ProbePointer = new PointerEventData(current);
            m_ProbeBoundEventSystem = current;
        }
    }
}
