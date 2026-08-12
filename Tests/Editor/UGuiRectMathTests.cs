using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// Screen geometry, where every wrong answer is a plausible-looking number.
    ///
    /// The failures guarded here are the ones that do not look like failures: an overlay canvas run
    /// through a camera produced (26971, 14639) for a corner whose real position was (507, 275); a
    /// node straddling the near plane projects to finite coordinates thousands of pixels wide; and
    /// an axis-aligned bounding box is not the element, so its centre is not a tap point. Each case
    /// must either produce the measured coordinate or be refused with a reason.
    /// </summary>
    public sealed class UGuiRectMathTests : UGuiSceneFixture
    {
        private const float Tolerance = 0.05f;

        private Camera m_Camera;

        [SetUp]
        public void BuildCamera()
        {
            m_Camera = CreateCamera("UnityFlowTestsCamera", new Vector3(0f, 0f, -10f));
        }

        [Test]
        public void OverlayCanvas_IsNotProjectedThroughACamera()
        {
            var canvas = CreateCanvas("UnityFlowTestsOverlay");
            var element = CreateElement("UnityFlowTestsOverlayItem", canvas.transform, new Vector2(160f, 40f));

            var node = NodeNamed("UnityFlowTestsOverlayItem");

            Assert.AreEqual(NodeSpace.ScreenOverlay, node.Space);
            Assert.IsTrue(node.ScreenRect.HasValue);

            // GetWorldCorners on an overlay canvas already returns physical screen pixels, scaler
            // factor included, so the untransformed corners ARE the answer.
            var expected = Bounds(element, null);
            AssertRect(expected, node.ScreenRect.Value);
            Assert.AreEqual(160f, node.ScreenRect.Value.width, Tolerance);
            Assert.AreEqual(40f, node.ScreenRect.Value.height, Tolerance);

            var projected = Bounds(element, m_Camera);
            Assert.Greater(Vector2.Distance(projected.center, expected.center), 100f,
                "this fixture only proves anything if projecting would have produced a different rect");
        }

        [Test]
        public void ScreenSpaceCameraCanvas_IsProjectedThroughItsCamera()
        {
            var canvas = CreateCanvas("UnityFlowTestsCameraCanvas", RenderMode.ScreenSpaceCamera, m_Camera);
            canvas.planeDistance = 5f;
            var element = CreateElement("UnityFlowTestsCameraItem", canvas.transform, new Vector2(160f, 40f));

            var node = NodeNamed("UnityFlowTestsCameraItem");

            Assert.AreEqual(NodeSpace.ScreenCamera, node.Space);
            Assert.IsTrue(node.ScreenRect.HasValue);

            AssertRect(Bounds(element, m_Camera), node.ScreenRect.Value);

            var unprojected = Bounds(element, null);
            Assert.Greater(Vector2.Distance(unprojected.center, node.ScreenRect.Value.center), 100f,
                "a ScreenSpaceCamera canvas lives in world units; skipping the projection would leave them unconverted");
        }

        [Test]
        public void CanvasBehindTheCamera_HasNoScreenRectAndSaysWhy()
        {
            var canvas = CreateCanvas("UnityFlowTestsBehindCanvas", RenderMode.WorldSpace, m_Camera);
            var canvasRect = (RectTransform)canvas.transform;
            canvasRect.sizeDelta = new Vector2(400f, 200f);
            canvasRect.position = new Vector3(0f, 0f, -30f);
            canvasRect.localScale = Vector3.one * 0.01f;

            var element = CreateElement("UnityFlowTestsBehindItem", canvas.transform, new Vector2(160f, 40f));

            var node = NodeNamed("UnityFlowTestsBehindItem");

            Assert.IsFalse(AnyCornerInFront(element), "this fixture must sit entirely behind the camera");
            Assert.IsFalse(node.ScreenRect.HasValue);
            Assert.IsFalse(node.IsVisible);
            StringAssert.Contains("near plane", node.Reason);

            Assert.IsFalse(Backend.TryResolveInjectionPoint(node.Handle, out _, out var reason));
            StringAssert.Contains("near plane", reason);
        }

        [Test]
        public void NearPlaneStraddler_IsRejectedRatherThanProjected()
        {
            var canvas = CreateCanvas("UnityFlowTestsStraddleCanvas", RenderMode.WorldSpace, m_Camera);
            var canvasRect = (RectTransform)canvas.transform;
            canvasRect.sizeDelta = new Vector2(400f, 200f);
            canvasRect.position = new Vector3(0f, 0f, -9.5f);
            canvasRect.rotation = Quaternion.Euler(0f, 90f, 0f);
            canvasRect.localScale = Vector3.one * 0.02f;

            var element = CreateElement("UnityFlowTestsStraddleItem", canvas.transform, new Vector2(160f, 40f));

            var node = NodeNamed("UnityFlowTestsStraddleItem");

            Assert.IsTrue(AnyCornerInFront(element), "a straddler has corners on both sides of the near plane");
            Assert.IsTrue(AnyCornerBehind(element));

            Assert.IsFalse(node.ScreenRect.HasValue,
                "three finite corners and one behind the camera still produce a finite rect, pointing at the wrong pixels");
            StringAssert.Contains("near plane", node.Reason);
            StringAssert.Contains("corner", node.Reason);
        }

        [Test]
        public void RotatedElement_HasAnInflatedBoundingBoxThatIsNotTheElement()
        {
            var canvas = CreateCanvas("UnityFlowTestsRotationCanvas");
            var element = CreateElement("UnityFlowTestsRotatedItem", canvas.transform, new Vector2(200f, 100f));
            element.localRotation = Quaternion.Euler(0f, 0f, 25f);

            var node = NodeNamed("UnityFlowTestsRotatedItem");

            Assert.IsTrue(node.ScreenRect.HasValue);

            var cos = Mathf.Cos(25f * Mathf.Deg2Rad);
            var sin = Mathf.Sin(25f * Mathf.Deg2Rad);
            Assert.AreEqual(200f * cos + 100f * sin, node.ScreenRect.Value.width, 0.1f);
            Assert.AreEqual(200f * sin + 100f * cos, node.ScreenRect.Value.height, 0.1f);
            Assert.Greater(node.ScreenRect.Value.width, 200f, "the AABB of a rotated element is bigger than the element");
        }

        [Test]
        public void YawedElement_IsTappedAtItsProjectedCentreNotItsBoundingBoxCentre()
        {
            var canvas = CreateCanvas("UnityFlowTestsYawCanvas", RenderMode.WorldSpace, m_Camera);
            var canvasRect = (RectTransform)canvas.transform;
            canvasRect.sizeDelta = new Vector2(400f, 200f);
            canvasRect.position = Vector3.zero;
            canvasRect.rotation = Quaternion.Euler(0f, 55f, 0f);
            canvasRect.localScale = Vector3.one * 0.02f;

            // Off-centre on the yawed canvas: an element straddling the axis of rotation projects
            // symmetrically and would hide the very error this test is about.
            var element = CreateElement("UnityFlowTestsYawItem", canvas.transform, new Vector2(160f, 40f), new Vector2(60f, 0f));

            var node = NodeNamed("UnityFlowTestsYawItem");
            Assert.IsTrue(node.ScreenRect.HasValue);

            var centre = (Vector2)m_Camera.WorldToScreenPoint(element.TransformPoint(element.rect.center));
            var boundsCentre = node.ScreenRect.Value.center;

            Assert.Greater(Vector2.Distance(centre, boundsCentre), 1f,
                "under a perspective yaw the bounding-box centre is not the element centre");

            Assert.IsTrue(Backend.TryResolveInjectionPoint(node.Handle, out var point, out var reason), reason);
            Assert.AreEqual(centre.x, point.x, Tolerance);
            Assert.AreEqual(centre.y, point.y, Tolerance);
            Assert.Greater(Vector2.Distance(point, boundsCentre), 1f, "the tap point must not be derived from the AABB");
        }

        [Test]
        public void RectMask2D_ShrinksTheActionableRectToTheClippedRegion()
        {
            var canvas = CreateCanvas("UnityFlowTestsMaskCanvas");

            var maskGo = new GameObject("UnityFlowTestsMaskPanel", typeof(RectTransform), typeof(RectMask2D));
            maskGo.transform.SetParent(canvas.transform, false);
            var maskRect = (RectTransform)maskGo.transform;
            maskRect.sizeDelta = new Vector2(100f, 50f);

            CreateElement("UnityFlowTestsMaskedItem", maskRect, new Vector2(400f, 200f));

            var node = NodeNamed("UnityFlowTestsMaskedItem");

            Assert.IsTrue(node.ScreenRect.HasValue);
            Assert.AreEqual(100f, node.ScreenRect.Value.width, Tolerance, "the mask, not the element, bounds what can be touched");
            Assert.AreEqual(50f, node.ScreenRect.Value.height, Tolerance);
            AssertRect(Bounds(maskRect, null), node.ScreenRect.Value);

            Assert.IsTrue(Backend.TryResolveInjectionPoint(node.Handle, out var point, out var reason), reason);
            Assert.IsTrue(node.ScreenRect.Value.Contains(point), "a tap must land inside the visible part of a clipped element");
        }

        private static Rect Bounds(RectTransform rect, Camera camera)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            var xMin = float.MaxValue;
            var yMin = float.MaxValue;
            var xMax = float.MinValue;
            var yMax = float.MinValue;

            for (var i = 0; i < corners.Length; i++)
            {
                var point = camera == null
                    ? new Vector2(corners[i].x, corners[i].y)
                    : (Vector2)camera.WorldToScreenPoint(corners[i]);

                xMin = Mathf.Min(xMin, point.x);
                yMin = Mathf.Min(yMin, point.y);
                xMax = Mathf.Max(xMax, point.x);
                yMax = Mathf.Max(yMax, point.y);
            }

            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private bool AnyCornerInFront(RectTransform rect) => CornerDepthCrosses(rect, inFront: true);

        private bool AnyCornerBehind(RectTransform rect) => CornerDepthCrosses(rect, inFront: false);

        private bool CornerDepthCrosses(RectTransform rect, bool inFront)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            for (var i = 0; i < corners.Length; i++)
            {
                var depth = m_Camera.WorldToScreenPoint(corners[i]).z;
                if (inFront ? depth >= m_Camera.nearClipPlane : depth < m_Camera.nearClipPlane)
                    return true;
            }

            return false;
        }

        private static void AssertRect(Rect expected, Rect actual)
        {
            Assert.AreEqual(expected.xMin, actual.xMin, Tolerance);
            Assert.AreEqual(expected.yMin, actual.yMin, Tolerance);
            Assert.AreEqual(expected.width, actual.width, Tolerance);
            Assert.AreEqual(expected.height, actual.height, Tolerance);
        }
    }
}
