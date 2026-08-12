using System;
using NUnit.Framework;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Window;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// The string that tells two steps with the same verb apart.
    ///
    /// A flow is mostly repetition — six asserts in a row is normal — so a row that shows only the
    /// verb shows nothing. Every case here is a verb whose identity lives somewhere different: in
    /// the selector, in a text argument, in a member path assembled from three separate keys, or in
    /// the key that will be pressed.
    /// </summary>
    public sealed class FlowStepCaptionTests
    {
        [Test]
        public void ASelectorVerbIsIdentifiedByItsSelector()
        {
            var step = Step("tapOn", selector: Selector(testId: "courier.menu.play"));

            Assert.AreEqual("testId=courier.menu.play", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AStateQueryReadsAsTheClaimItMakes()
        {
            var step = Step("assert",
                Arg("find", FlowArgKind.String, "PlayButton"),
                Arg("component", FlowArgKind.String, "Button"),
                Arg("field", FlowArgKind.String, "interactable"),
                Raw("is", "false"));

            Assert.AreEqual("PlayButton.Button.interactable is false", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AQueryWithoutAnObjectStillNamesTheMemberItReads()
        {
            var step = Step("assert",
                Arg("component", FlowArgKind.String, "ScoreKeeper"),
                Arg("field", FlowArgKind.String, "score"),
                Raw("eq", "400"));

            Assert.AreEqual("ScoreKeeper.score == 400", FlowStepCaption.Argument(step));
        }

        [Test]
        public void HowLongAClaimMustHoldIsPartOfTheClaim()
        {
            var step = Step("assert",
                Arg("component", FlowArgKind.String, "GameClock"),
                Arg("field", FlowArgKind.String, "running"),
                Raw("is", "false"),
                Arg("stableFor", FlowArgKind.Duration, TimeSpan.FromSeconds(1)));

            Assert.AreEqual("GameClock.running is false (stable for 1s)", FlowStepCaption.Argument(step));
        }

        [Test]
        public void ExistenceIsAPhraseRatherThanAComparisonAgainstTrue()
        {
            var step = Step("waitUntil",
                Arg("find", FlowArgKind.String, "Courier"),
                Arg("exists", FlowArgKind.Bool, true));

            Assert.AreEqual("Courier exists", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AnEscapeHatchQueryIsIdentifiedByItsExpression()
        {
            var step = Step("waitUntil",
                Arg("expr", FlowArgKind.String, "GameEntry.Network.IsConnectedZone()"),
                Raw("is", "true"));

            Assert.AreEqual("GameEntry.Network.IsConnectedZone() is true", FlowStepCaption.Argument(step));
        }

        [Test]
        public void TypingIsIdentifiedByTheTextAndNotByTheField()
        {
            var step = Step("inputText",
                new[] { Arg("text", FlowArgKind.String, "Ada") },
                Selector(name: "CourierNameField"));

            Assert.AreEqual("\"Ada\" → name=CourierNameField", FlowStepCaption.Argument(step));
        }

        [Test]
        public void ATextAssertionShowsWhichOfItsThreeCriteriaWasWritten()
        {
            var step = Step("assertText",
                new[] { Arg("contains", FlowArgKind.String, "sound off") },
                Selector(name: "MenuSummary"));

            Assert.AreEqual("name=MenuSummary contains \"sound off\"", FlowStepCaption.Argument(step));
        }

        [Test]
        public void ADragIsIdentifiedByBothOfItsEnds()
        {
            var step = Step("drag",
                Arg("from", FlowArgKind.Selector, Selector(testId: "courier.slot.0")),
                Arg("to", FlowArgKind.Selector, Selector(testId: "courier.slot.1")));

            Assert.AreEqual("testId=courier.slot.0 → testId=courier.slot.1", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AHeldKeyIsADifferentGestureFromATapAndSaysSo()
        {
            var step = Step("press",
                Arg("key", FlowArgKind.String, "w"),
                Arg("duration", FlowArgKind.Duration, TimeSpan.FromMilliseconds(2400)));

            Assert.AreEqual("w for 2.4s", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AScopeChangesWhichNodeTheStepMeansSoItIsShown()
        {
            var step = Step("assertText",
                new[] { Arg("equals", FlowArgKind.String, "A") },
                Selector(name: "ChipLabel"),
                on: Selector(testId: "courier.slot.0"));

            Assert.AreEqual("name=ChipLabel = \"A\"  on testId=courier.slot.0", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AProjectCommandWithOneArgumentShowsTheValueItWasGiven()
        {
            var step = Step("setTimer", Arg("seconds", FlowArgKind.Int, 30));

            Assert.AreEqual("30", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AScriptIsIdentifiedByItsFirstLineAndNotByItsBody()
        {
            var step = Step("runScript",
                Arg("code", FlowArgKind.String, "\n  GameEntry.Network.SendLogin(\"testgm\");\n  return true;\n"));

            Assert.AreEqual("GameEntry.Network.SendLogin(\"testgm\");", FlowStepCaption.Argument(step));
        }

        [Test]
        public void AVerbWithNoArgumentAtAllSaysNothingRatherThanAPlaceholder()
        {
            Assert.AreEqual(string.Empty, FlowStepCaption.Argument(Step("exitPlayMode")));
        }

        private static FlowStep Step(string verb, params FlowArgument[] args) =>
            Step(verb, args, null);

        private static FlowStep Step(string verb, FlowArgument[] args, Selector selector, Selector on = null) =>
            new FlowStep(verb, args, selector, on, null, null, 1, 1, "unityflow-tests.flow.yaml");

        private static FlowStep Step(string verb, Selector selector) =>
            Step(verb, Array.Empty<FlowArgument>(), selector);

        private static FlowArgument Arg(string name, FlowArgKind kind, object value) =>
            new FlowArgument(name, kind, value, null, FlowValue.OfScalar(Convert.ToString(value), false, 1, 1));

        /// <summary>
        /// A comparison argument is declared <see cref="FlowArgKind.Any"/>, so it reaches the caption
        /// as the raw YAML value rather than a converted one — the type it must be is the type of
        /// the field it will be compared with, which is unknown until the scene is read.
        /// </summary>
        private static FlowArgument Raw(string name, string text)
        {
            var value = FlowValue.OfScalar(text, false, 1, 1);
            return new FlowArgument(name, FlowArgKind.Any, value, null, value);
        }

        private static Selector Selector(string testId = null, string name = null) =>
            new Selector(null, testId, null, TextMatchMode.Exact, null, name, null, null, null, null, null,
                SelectorForm.Mapping, 1, 1);
    }
}
