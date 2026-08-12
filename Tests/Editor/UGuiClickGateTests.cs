using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// Records what a pointer event actually carried, so the click gate can be asserted on fields
    /// rather than on the fact that something happened.
    /// </summary>
    public sealed class PointerProbe : MonoBehaviour,
        IPointerEnterHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public int Enters;
        public int Downs;
        public int Ups;
        public int Clicks;

        public GameObject ClickPointerClick;
        public GameObject ClickPointerPress;
        public bool ClickEligible;
        public Vector2 ClickPosition;
        public Vector2 ClickPressPosition;
        public int ClickPointerId;
        public int ClickCount;
        public PointerEventData.InputButton ClickButton;
        public GameObject ClickRaycastObject;
        public bool ClickHadRaycastModule;

        public void OnPointerEnter(PointerEventData eventData) => Enters++;

        public void OnPointerDown(PointerEventData eventData) => Downs++;

        public void OnPointerUp(PointerEventData eventData) => Ups++;

        public void OnPointerClick(PointerEventData eventData)
        {
            Clicks++;
            ClickPointerClick = eventData.pointerClick;
            ClickPointerPress = eventData.pointerPress;
            ClickEligible = eventData.eligibleForClick;
            ClickPosition = eventData.position;
            ClickPressPosition = eventData.pressPosition;
            ClickPointerId = eventData.pointerId;
            ClickCount = eventData.clickCount;
            ClickButton = eventData.button;
            ClickRaycastObject = eventData.pointerCurrentRaycast.gameObject;
            ClickHadRaycastModule = eventData.pointerPressRaycast.module != null;
        }
    }

    /// <summary>
    /// The regression lock on the uGUI 2.0 click gate.
    ///
    /// StandaloneInputModule.ReleaseMouse now reads
    /// <c>pointerEvent.pointerClick == GetEventHandler&lt;IPointerClickHandler&gt;(currentOverGo)</c>.
    /// It used to read <c>pointerPress == currentOverGo</c>, and most published snippets still do.
    /// A dispatcher that fills only pointerPress leaves pointerClick null, the gate fails silently,
    /// and Button.onClick never runs — while every Selectable transition still animates, so a
    /// screenshot looks exactly like a successful click.
    /// </summary>
    public sealed class UGuiClickGateTests : UGuiSceneFixture
    {
        private const string ButtonName = "UnityFlowTestsSubmitButton";
        private const string OtherButtonName = "UnityFlowTestsOtherButton";

        private Canvas m_Canvas;
        private Button m_Button;
        private PointerProbe m_Probe;
        private int m_OnClickCalls;

        [SetUp]
        public void BuildButton()
        {
            m_Canvas = CreateCanvas("UnityFlowTestsClickCanvas");
            m_Button = CreateButton(ButtonName, m_Canvas.transform, new Vector2(200f, 60f));
            m_Probe = m_Button.gameObject.AddComponent<PointerProbe>();

            m_OnClickCalls = 0;
            m_Button.onClick.AddListener(() => m_OnClickCalls++);
        }

        [Test]
        public void Dispatch_Click_FiresOnClickExactlyOnce()
        {
            var node = NodeNamed(ButtonName);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(node.Handle, out var point, out var reason), reason);

            Assert.IsTrue(Backend.TryDispatch(node.Handle, PointerGesture.Click, point, out var error), error);

            Assert.AreEqual(1, m_OnClickCalls, "one click gesture must produce exactly one onClick");
            Assert.AreEqual(1, m_Probe.Clicks);
            Assert.AreEqual(1, m_Probe.Enters, "without the enter chain no Selectable transition runs and screenshots lie");
            Assert.AreEqual(1, m_Probe.Downs);
            Assert.AreEqual(1, m_Probe.Ups);
        }

        [Test]
        public void Dispatch_Click_DeliversTheFieldSetTheGateReads()
        {
            var node = NodeNamed(ButtonName);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(node.Handle, out var point, out var reason), reason);
            Assert.IsTrue(Backend.TryDispatch(node.Handle, PointerGesture.Click, point, out var error), error);

            Assert.AreEqual(1, m_Probe.Clicks);

            // This is the assertion the whole file exists for: pointerClick, not pointerPress, is
            // what uGUI 2.0 compares against the object under the pointer.
            Assert.AreSame(m_Button.gameObject, m_Probe.ClickPointerClick,
                "pointerClick must name the click handler, or the release gate can never fire");

            Assert.AreSame(m_Button.gameObject, m_Probe.ClickPointerPress);
            Assert.IsTrue(m_Probe.ClickEligible);
            Assert.AreEqual(PointerEventData.InputButton.Left, m_Probe.ClickButton);
            Assert.AreEqual(-1, m_Probe.ClickPointerId, "kMouseLeftId is what StandaloneInputModule uses for the left button");
            Assert.AreEqual(1, m_Probe.ClickCount);
            Assert.AreEqual(point, m_Probe.ClickPosition);
            Assert.AreEqual(point, m_Probe.ClickPressPosition);
            Assert.AreSame(m_Button.gameObject, m_Probe.ClickRaycastObject);
            Assert.IsTrue(m_Probe.ClickHadRaycastModule,
                "the raycast must carry the GraphicRaycaster that really answered, never a forged one");
        }

        /// <summary>
        /// The gate itself, driven through the product.
        ///
        /// THIS TEST REPLACES A VACUOUS ONE. Its predecessor built a PointerEventData by hand and
        /// ran a private copy of StandaloneInputModule.ReleaseMouse declared in this file, so it
        /// asserted that UnityEngine.UI behaves as documented and never called UnityFlow at all:
        /// deleting <c>m_Pointer.pointerClick = clickHandler</c> from UGuiSemanticDispatch.SendDown
        /// — the exact defect it was named after — left it green.
        ///
        /// What the product's release gate really decides is whether the pointer came UP over the
        /// same click handler it went DOWN on, which is why a press dragged off a button does not
        /// click it. Both arms below go through <c>Backend.TryDispatch</c>, so the assertions move
        /// when the dispatcher does: with pointerClick left unset the control arm's click never
        /// fires, and with the gate removed altogether the cross arm's click fires when it must not.
        /// </summary>
        [Test]
        public void Release_OverADifferentObjectThanThePress_ProducesNoClickOnEither()
        {
            var other = CreateButton(OtherButtonName, m_Canvas.transform, new Vector2(180f, 60f), new Vector2(220f, 0f));
            var otherProbe = other.gameObject.AddComponent<PointerProbe>();
            var otherClicks = 0;
            other.onClick.AddListener(() => otherClicks++);

            // Move the pressed button clear of the other one; overlapping rects would make the
            // dispatcher refuse for occlusion and the test would prove nothing about the gate.
            ((RectTransform)m_Button.transform).anchoredPosition = new Vector2(-220f, 0f);

            var pressed = NodeNamed(ButtonName);
            var released = NodeNamed(OtherButtonName);

            Assert.IsTrue(Backend.TryResolveInjectionPoint(pressed.Handle, out var pressPoint, out var pressReason), pressReason);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(released.Handle, out var releasePoint, out var releaseReason), releaseReason);

            Assert.IsTrue(Backend.TryDispatch(pressed.Handle, PointerGesture.Down, pressPoint, out var downError), downError);
            Assert.IsTrue(Backend.TryDispatch(released.Handle, PointerGesture.Up, releasePoint, out var upError), upError);

            Assert.AreEqual(1, m_Probe.Downs, "the press must still have been delivered");
            Assert.AreEqual(1, m_Probe.Ups, "uGUI sends the up to whatever was pressed, wherever the pointer now is");

            Assert.AreEqual(0, m_OnClickCalls,
                "a release over a different object must not click the pressed one; that is the whole purpose of the gate");
            Assert.AreEqual(0, m_Probe.Clicks);
            Assert.AreEqual(0, otherClicks, "nor may it click the object the pointer happened to end up over");
            Assert.AreEqual(0, otherProbe.Clicks);

            // Control arm. Same buttons, same dispatcher, release over the object that was pressed:
            // if this does not click, the gate is not merely strict, it is broken.
            Assert.IsTrue(Backend.TryDispatch(pressed.Handle, PointerGesture.Down, pressPoint, out downError), downError);
            Assert.IsTrue(Backend.TryDispatch(pressed.Handle, PointerGesture.Up, pressPoint, out upError), upError);

            Assert.AreEqual(1, m_OnClickCalls, "the only difference from the first attempt is where the release landed");
            Assert.AreEqual(1, m_Probe.Clicks);
            Assert.AreEqual(0, otherClicks);
        }

        /// <summary>
        /// An Up with no outstanding Down is refused rather than synthesised. Accepting it would let
        /// a flow "click" by sending a release alone, against a control no pointer ever pressed.
        /// </summary>
        [Test]
        public void Release_WithNoOutstandingPress_IsRefused()
        {
            var node = NodeNamed(ButtonName);
            Assert.IsTrue(Backend.TryResolveInjectionPoint(node.Handle, out var point, out var reason), reason);

            Assert.IsFalse(Backend.TryDispatch(node.Handle, PointerGesture.Up, point, out var error));
            StringAssert.Contains("send Down before Up", error);
            Assert.AreEqual(0, m_OnClickCalls);
            Assert.AreEqual(0, m_Probe.Ups);
        }

        [Test]
        public void Dispatch_RefusesWhenSomethingCoversTheTarget()
        {
            // A later sibling on the same canvas draws on top, which is exactly how a modal blocks
            // a button a user can still see.
            CreateElement("UnityFlowTestsBlocker", m_Canvas.transform, new Vector2(600f, 400f));

            var node = NodeNamed(ButtonName);
            var rect = m_Button.GetComponent<RectTransform>();
            var centre = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));

            Assert.IsFalse(Backend.TryDispatch(node.Handle, PointerGesture.Click, centre, out var error),
                "forcing the event onto the target would make a modal-blocked UI look clickable");

            StringAssert.Contains("obscured by", error);
            StringAssert.Contains("UnityFlowTestsBlocker", error);
            Assert.AreEqual(0, m_OnClickCalls);
        }
    }
}
