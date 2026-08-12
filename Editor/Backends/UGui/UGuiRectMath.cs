using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Turns uGUI transforms into screen pixels, and screen pixels into a pointer target.
    ///
    /// Every number this class produces is either measured from the engine or rejected outright.
    /// There is no "best effort" coordinate: when a node cannot be projected the caller is told
    /// exactly which corner failed and why, because a plausible-looking wrong coordinate is far
    /// more expensive than a loud refusal.
    ///
    /// Instance state (the corner scratch buffers) is owned per backend so nothing is shared and
    /// the retry loop allocates nothing.
    /// </summary>
    internal sealed class UGuiRectMath
    {
        /// <summary>Number of deterministic fallback probe points offered when the centre is occluded.</summary>
        internal const int ProbeGridLength = 9;

        /// <summary>Probes sit 10% inside the visible rect so a 1px border or soft mask edge cannot swallow them.</summary>
        private const float ProbeInset = 0.1f;

        private readonly Vector3[] m_Corners = new Vector3[4];
        private readonly Vector3[] m_MaskCorners = new Vector3[4];

        /// <summary>
        /// The Canvas that governs this transform: the nearest Canvas component walking up parents.
        ///
        /// Deliberately NOT <c>Canvas.rootCanvas</c>. A nested Canvas whose component is disabled or
        /// whose GameObject is inactive still reports <c>isRootCanvas == true</c>, so rootCanvas
        /// hands back the wrong Canvas exactly in the cases a test runner cares about.
        /// </summary>
        public static Canvas FindOwningCanvas(Transform transform)
        {
            // TryGetComponent, never GetComponent: measured in this editor, GetComponent<T> allocates
            // 674 bytes every time the component is ABSENT, and this walk misses on every non-canvas
            // ancestor of every node on every retry frame. TryGetComponent allocates nothing either way.
            for (var t = transform; t != null; t = t.parent)
            {
                if (t.TryGetComponent<Canvas>(out var canvas))
                    return canvas;
            }

            return null;
        }

        /// <summary>
        /// The topmost Canvas above <paramref name="canvas"/>. This is where the GraphicRaycaster,
        /// the render mode and the pixel rect actually live, so it is the canvas that answers
        /// "which camera" and "how big is the screen".
        /// </summary>
        public static Canvas FindTreeRootCanvas(Canvas canvas)
        {
            var result = canvas;
            for (var t = canvas.transform.parent; t != null; t = t.parent)
            {
                if (t.TryGetComponent<Canvas>(out var parentCanvas))
                    result = parentCanvas;
            }

            return result;
        }

        /// <summary>
        /// Exact mirror of <c>GraphicRaycaster.eventCamera</c> as shipped in com.unity.ugui.
        /// Reproduced rather than approximated: if this disagrees with the raycaster by even one
        /// case, every projected coordinate on that canvas is silently wrong.
        ///
        /// Note the asymmetry that is easy to get backwards — ScreenSpaceCamera with no worldCamera
        /// yields null, but WorldSpace with no worldCamera falls through to Camera.main.
        /// </summary>
        // Derived from GraphicRaycaster.eventCamera in com.unity.ugui.
        // uGUI Copyright (c) 2015-2020 Unity Technologies ApS, Unity Companion License:
        // https://unity.com/legal/licenses/unity-companion-license
        public static Camera ResolveEventCamera(Canvas rootCanvas)
        {
            var renderMode = rootCanvas.renderMode;
            if (renderMode == RenderMode.ScreenSpaceOverlay ||
                (renderMode == RenderMode.ScreenSpaceCamera && rootCanvas.worldCamera == null))
                return null;

            return rootCanvas.worldCamera ?? Camera.main;
        }

        /// <summary>
        /// The camera to project this canvas with, and whether the canvas has usable screen geometry
        /// at all. A null camera on success is the correct, expected answer for an overlay canvas.
        ///
        /// Two failure shapes are separated here because they need different fixes and because
        /// conflating them is dangerous: a DESTROYED Camera survives the <c>??</c> inside
        /// <see cref="ResolveEventCamera"/> (that operator bypasses Unity's null override) and then
        /// compares equal to null everywhere else, which would silently downgrade a
        /// ScreenSpaceCamera canvas to the overlay code path and produce world units where screen
        /// pixels were expected.
        /// </summary>
        public static bool TryResolveCamera(Canvas rootCanvas, out Camera camera, out string reason)
        {
            var resolved = ResolveEventCamera(rootCanvas);

            if (!ReferenceEquals(resolved, null) && resolved == null)
            {
                camera = null;
                reason = $"Canvas '{rootCanvas.name}' renders through a Camera that has been destroyed; " +
                         "assign Canvas.worldCamera or tag an active camera as MainCamera";
                return false;
            }

            if (ReferenceEquals(resolved, null))
            {
                camera = null;

                if (rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    reason = null;
                    return true;
                }

                reason = $"Canvas '{rootCanvas.name}' is set to {rootCanvas.renderMode} but has no worldCamera and " +
                         "there is no MainCamera, so it is not rendered and has no screen geometry";
                return false;
            }

            camera = resolved;
            reason = null;
            return true;
        }

        /// <summary>The space a canvas's nodes live in, from the canvas that owns the render mode.</summary>
        public static NodeSpace SpaceOf(Canvas rootCanvas)
        {
            switch (rootCanvas.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    return NodeSpace.ScreenOverlay;
                case RenderMode.ScreenSpaceCamera:
                    return NodeSpace.ScreenCamera;
                default:
                    return NodeSpace.World3D;
            }
        }

        /// <summary>
        /// Screen bounds of the surface itself, straight from the engine.
        ///
        /// <c>Screen.width/height</c> is not usable here: inside an Editor call it reports the size
        /// of the focused EditorWindow, measured as 640x480 while the Game View — and every canvas
        /// in it — was 1115x601. <c>Canvas.pixelRect</c> returned exactly (0,0,1115,601) for both an
        /// overlay and a ScreenSpaceCamera canvas, so it is the only source used.
        /// </summary>
        public static Rect SurfaceRect(Canvas rootCanvas) => rootCanvas.pixelRect;

        /// <summary>
        /// Project a node's rect to an axis-aligned screen rect, clipped by every ancestor mask and
        /// by the surface. Returns false only for a genuine projection failure; a node that is
        /// merely clipped to nothing comes back true with a zero-size rect, because "clipped away"
        /// is a visibility fact and not a geometry error.
        /// </summary>
        public bool TryGetScreenRect(
            RectTransform rectTransform,
            Canvas rootCanvas,
            Camera camera,
            out Rect screenRect,
            out string reason)
        {
            if (!TryProjectLocalRect(rectTransform, rectTransform.rect, camera, m_Corners, out screenRect, out reason))
                return false;

            var stopAt = rootCanvas.transform;
            for (var t = rectTransform.parent; t != null; t = t.parent)
            {
                if (t.TryGetComponent<RectMask2D>(out var rectMask) && rectMask.isActiveAndEnabled)
                {
                    var maskRect = ApplyPadding(rectMask.rectTransform.rect, rectMask.padding);
                    if (!TryProjectLocalRect(rectMask.rectTransform, maskRect, camera, m_MaskCorners, out var maskScreen, out reason))
                        return false;

                    screenRect = Intersect(screenRect, maskScreen);
                }

                if (t.TryGetComponent<Mask>(out var mask) && mask.isActiveAndEnabled && mask.graphic != null)
                {
                    var maskGraphicRect = mask.graphic.rectTransform;
                    if (!TryProjectLocalRect(maskGraphicRect, maskGraphicRect.rect, camera, m_MaskCorners, out var stencilScreen, out reason))
                        return false;

                    screenRect = Intersect(screenRect, stencilScreen);
                }

                if (t == stopAt)
                    break;
            }

            screenRect = Intersect(screenRect, SurfaceRect(rootCanvas));
            return true;
        }

        /// <summary>
        /// The point a pointer should be placed at, before occlusion is considered: the projected
        /// centre of the element itself, never the centre of its axis-aligned bounds. A 55 degree
        /// yaw on a world canvas moved the AABB centre 5.91 px off the real element centre, and a
        /// 25 degree rotation grew a 400x200 element's AABB to 254x199 px.
        /// </summary>
        public bool TryGetCentrePoint(RectTransform rectTransform, Camera camera, out Vector2 point, out string reason)
        {
            var world = rectTransform.TransformPoint(rectTransform.rect.center);

            if (camera != null)
            {
                var projected = camera.WorldToScreenPoint(world);
                if (projected.z < camera.nearClipPlane)
                {
                    point = default;
                    reason = $"element centre is at view depth {Format(projected.z)}, nearer than the camera near plane " +
                             $"{Format(camera.nearClipPlane)}; screen coordinates there are meaningless";
                    return false;
                }
            }

            point = RectTransformUtility.WorldToScreenPoint(camera, world);

            if (!IsFinite(point))
            {
                reason = $"projected element centre is not a finite coordinate ({Format(point.x)}, {Format(point.y)})";
                point = default;
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// uGUI's own containment test, used to confirm a candidate point really lies inside the
        /// element before it is hit-probed. This is the same call GraphicRaycaster makes, so a point
        /// that fails here could never have been delivered by a real pointer either.
        /// </summary>
        public static bool ContainsPoint(RectTransform rectTransform, Vector2 screenPoint, Camera camera) =>
            RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPoint, camera);

        /// <summary>
        /// Fill nine deterministic probe points inside the already-clipped visible rect, ordered
        /// most-likely-to-succeed first: centre, then edge midpoints, then corners. Deterministic
        /// ordering matters because a flaky tap point produces a flaky test.
        /// </summary>
        public static void BuildProbeGrid(in Rect visible, Vector2[] into)
        {
            var left = visible.xMin + visible.width * ProbeInset;
            var right = visible.xMax - visible.width * ProbeInset;
            var midX = visible.center.x;
            var bottom = visible.yMin + visible.height * ProbeInset;
            var top = visible.yMax - visible.height * ProbeInset;
            var midY = visible.center.y;

            into[0] = new Vector2(midX, midY);
            into[1] = new Vector2(left, midY);
            into[2] = new Vector2(right, midY);
            into[3] = new Vector2(midX, top);
            into[4] = new Vector2(midX, bottom);
            into[5] = new Vector2(left, top);
            into[6] = new Vector2(right, top);
            into[7] = new Vector2(left, bottom);
            into[8] = new Vector2(right, bottom);
        }

        /// <summary>
        /// Project the four corners of a local rect belonging to <paramref name="rectTransform"/>.
        ///
        /// Overlay canvases are NOT projected: <c>RectTransform.GetWorldCorners</c> already returns
        /// physical screen pixels with the CanvasScaler factor baked in. Feeding a camera in anyway
        /// turned a corner whose true screen position was (507.5, 275.5) into (26971.92, 14639.76).
        /// </summary>
        private bool TryProjectLocalRect(
            RectTransform rectTransform,
            in Rect localRect,
            Camera camera,
            Vector3[] scratch,
            out Rect screenRect,
            out string reason)
        {
            scratch[0] = rectTransform.TransformPoint(new Vector3(localRect.xMin, localRect.yMin, 0f));
            scratch[1] = rectTransform.TransformPoint(new Vector3(localRect.xMin, localRect.yMax, 0f));
            scratch[2] = rectTransform.TransformPoint(new Vector3(localRect.xMax, localRect.yMax, 0f));
            scratch[3] = rectTransform.TransformPoint(new Vector3(localRect.xMax, localRect.yMin, 0f));

            var xMin = float.MaxValue;
            var yMin = float.MaxValue;
            var xMax = float.MinValue;
            var yMax = float.MinValue;

            for (var i = 0; i < 4; i++)
            {
                float x;
                float y;

                if (camera == null)
                {
                    x = scratch[i].x;
                    y = scratch[i].y;
                }
                else
                {
                    // WorldToScreenPoint keeps z (view depth); RectTransformUtility.WorldToScreenPoint
                    // discards it, and z is the only thing that catches a near-plane straddler.
                    var projected = camera.WorldToScreenPoint(scratch[i]);
                    if (projected.z < camera.nearClipPlane)
                    {
                        // "z > 0" is not enough: a corner at view z = 0.10 projected to +-3000 px.
                        screenRect = default;
                        reason = $"corner {i} of '{rectTransform.name}' is at view depth {Format(projected.z)}, " +
                                 $"nearer than the camera near plane {Format(camera.nearClipPlane)}; " +
                                 "the node straddles or sits behind the near plane and has no screen rect";
                        return false;
                    }

                    x = projected.x;
                    y = projected.y;
                }

                // Per corner, not per rect: NaN compares false against BOTH xMin and xMax, so a
                // single NaN corner slips through the min/max loop untouched and the rect is built
                // from the surviving three — finite, plausible, and pointing at the wrong pixels.
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    screenRect = default;
                    reason = $"corner {i} of '{rectTransform.name}' projected to a non-finite coordinate " +
                             $"({Format(x)}, {Format(y)}); layout has not produced usable numbers";
                    return false;
                }

                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }

            screenRect = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);

            // Every corner is finite by now, so this catches only an infinite EXTENT: two corners at
            // opposite ends of the float range subtract to Infinity. NaN passes Rect.Contains and
            // every size comparison, so it must be caught here or it becomes a wrong answer three
            // layers up.
            if (!IsFinite(screenRect))
            {
                reason = $"projected bounds of '{rectTransform.name}' are not finite " +
                         $"(x {Format(screenRect.x)}, y {Format(screenRect.y)}, " +
                         $"w {Format(screenRect.width)}, h {Format(screenRect.height)}); " +
                         "layout has not produced usable numbers";
                screenRect = default;
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>RectMask2D padding is (left, bottom, right, top) and shrinks the clip rect.</summary>
        private static Rect ApplyPadding(in Rect rect, in Vector4 padding) =>
            Rect.MinMaxRect(
                rect.xMin + padding.x,
                rect.yMin + padding.y,
                rect.xMax - padding.z,
                rect.yMax - padding.w);

        public static Rect Intersect(in Rect a, in Rect b)
        {
            var xMin = Mathf.Max(a.xMin, b.xMin);
            var yMin = Mathf.Max(a.yMin, b.yMin);
            var xMax = Mathf.Min(a.xMax, b.xMax);
            var yMax = Mathf.Min(a.yMax, b.yMax);
            return new Rect(xMin, yMin, Mathf.Max(0f, xMax - xMin), Mathf.Max(0f, yMax - yMin));
        }

        public static bool IsFinite(in Rect rect) =>
            float.IsFinite(rect.x) && float.IsFinite(rect.y) &&
            float.IsFinite(rect.width) && float.IsFinite(rect.height);

        public static bool IsFinite(in Vector2 point) =>
            float.IsFinite(point.x) && float.IsFinite(point.y);

        /// <summary>
        /// Invariant formatting for every number this backend puts in a message. The editor runs
        /// under the user's locale — pt-BR here — and interpolation would otherwise emit "327,5",
        /// which reads as a second coordinate and breaks any tooling that parses a report.
        /// </summary>
        public static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public static string Format(in Vector2 point) => $"({Format(point.x)}, {Format(point.y)})";

        public static string Format(in Rect rect) =>
            $"({Format(rect.x)}, {Format(rect.y)}, {Format(rect.width)} x {Format(rect.height)})";
    }
}
