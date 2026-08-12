using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityFlow.Editor.Core;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// The 0 / 1 / N rule, which is the single behaviour that decides whether a flow tests what its
    /// author thinks it tests. Silently resolving N matches to the first one keeps every flow green
    /// while pointing at the wrong node, so the ambiguous cases are asserted as hard as the
    /// successful ones.
    /// </summary>
    public sealed class SelectorResolverTests : UGuiSceneFixture
    {
        private const string CanvasName = "UnityFlowTestsCanvas";
        private const string AlphaPanel = "UnityFlowTestsAlpha";
        private const string BetaPanel = "UnityFlowTestsBeta";
        private const string ItemName = "UnityFlowTestsItem";
        private const string SubmitName = "UnityFlowTestsSubmit";
        private const string SubmitTestId = "unityflow.tests.submit";
        private const string FadedName = "UnityFlowTestsFaded";

        private Canvas m_Canvas;

        [SetUp]
        public void BuildCanvas()
        {
            m_Canvas = CreateCanvas(CanvasName);
        }

        [Test]
        public void Resolve_WithNoMatch_IsRetryableAndSuggestsTheNameThatWasMeant()
        {
            BuildSubmitButton();

            var result = Resolver.Resolve(ByName("UnityFlowTestsSubmt"));

            Assert.AreEqual(ResolutionOutcome.NotFound, result.Outcome);
            Assert.IsTrue(result.IsRetryable, "nothing matched yet, so a later frame could still succeed");
            StringAssert.Contains("matched 0 nodes", result.Message);

            Assert.IsNotEmpty(result.NearMisses, "a timeout with no suggestion costs the reader a round trip");
            Assert.AreEqual(SubmitName, result.NearMisses[0],
                "the closest name must come first, otherwise the suggestion list is noise");
        }

        [Test]
        public void Resolve_WithOneMatch_ResolvesThatNode()
        {
            BuildSubmitButton();

            var result = Resolver.Resolve(ByName(SubmitName));

            Assert.AreEqual(ResolutionOutcome.Resolved, result.Outcome);
            Assert.AreEqual(SubmitName, result.Node.Name);
            Assert.AreEqual($"/{CanvasName}/{SubmitName}", result.Node.Path);
        }

        [Test]
        public void Resolve_ByTestId_ResolvesThatNode()
        {
            BuildSubmitButton();

            var result = Resolver.Resolve(ByTestId(SubmitTestId));

            Assert.AreEqual(ResolutionOutcome.Resolved, result.Outcome);
            Assert.AreEqual(SubmitTestId, result.Node.TestId);
        }

        [Test]
        public void Resolve_TwiceInARow_ReturnsTheSameNode()
        {
            BuildSubmitButton();

            var first = Resolver.Resolve(ByName(SubmitName));
            var second = Resolver.Resolve(ByName(SubmitName));

            Assert.AreEqual(ResolutionOutcome.Resolved, first.Outcome);
            Assert.AreEqual(ResolutionOutcome.Resolved, second.Outcome);
            Assert.AreEqual(first.Node.Handle, second.Node.Handle);
        }

        [Test]
        public void Resolve_WithSeveralMatches_IsAmbiguousAndListsEveryCandidate()
        {
            BuildTwoItems(betaFirst: false);

            var result = Resolver.Resolve(ByName(ItemName));

            Assert.AreEqual(ResolutionOutcome.Ambiguous, result.Outcome,
                "two matches must fail; taking the first is how a flow passes against the wrong node");
            Assert.IsFalse(result.IsRetryable, "retrying cannot make an ambiguous selector unique");
            Assert.AreEqual(2, result.Matches.Count);

            StringAssert.Contains("matched 2 nodes", result.Message);
            StringAssert.Contains($"/{CanvasName}/{AlphaPanel}/{ItemName}", result.Message);
            StringAssert.Contains($"/{CanvasName}/{BetaPanel}/{ItemName}", result.Message);
            StringAssert.Contains("index", result.Message);
        }

        [Test]
        public void Resolve_WithIndex_PicksTheSameNodeWhicheverOrderTheSceneWasBuiltIn()
        {
            BuildTwoItems(betaFirst: false);
            var firstBuild = Resolver.Resolve(IndexedItem(1));
            Assert.AreEqual(ResolutionOutcome.Resolved, firstBuild.Outcome);
            var firstPath = firstBuild.Node.Path;

            DestroyPanels();

            // A rebuilt scene gets a fresh registry, exactly as a second run would, so nothing about
            // the previous enumeration can carry the ordering over.
            ResetBackends();
            BuildTwoItems(betaFirst: true);

            var secondBuild = Resolver.Resolve(IndexedItem(1));

            Assert.AreEqual(ResolutionOutcome.Resolved, secondBuild.Outcome);
            Assert.AreEqual(firstPath, secondBuild.Node.Path,
                "index must name the same node across runs; if it followed enumeration order it would flip here");
            Assert.AreEqual($"/{CanvasName}/{BetaPanel}/{ItemName}", secondBuild.Node.Path);
        }

        /// <summary>
        /// The numbers the ambiguity message prints must be the numbers <c>index:</c> accepts.
        ///
        /// This is not presentation. The message is the only place an author ever learns which
        /// index is which, so a report listed in enumeration order while the index is taken from a
        /// sorted list hands them a number that silently selects the other node — the ambiguity
        /// failure would then be worse than useless, because acting on it produces a green flow
        /// against the wrong element.
        /// </summary>
        [Test]
        public void Resolve_AmbiguityMessage_NumbersTheCandidatesTheWayIndexReadsThem()
        {
            BuildTwoItems(betaFirst: false);

            var ambiguous = Resolver.Resolve(ByName(ItemName));
            Assert.AreEqual(ResolutionOutcome.Ambiguous, ambiguous.Outcome);

            for (var index = 0; index < ambiguous.Matches.Count; index++)
            {
                var picked = Resolver.Resolve(IndexedItem(index));

                Assert.AreEqual(ResolutionOutcome.Resolved, picked.Outcome);
                Assert.AreEqual(ambiguous.Matches[index].Path, picked.Node.Path,
                    $"the message offers [{index}] but 'index: {index}' resolves something else");
                StringAssert.Contains($"[{index}] {picked.Node.Path}", ambiguous.Message);
            }
        }

        /// <summary>
        /// Ordering across surfaces, which is the half of the index rule that hierarchy paths cannot
        /// express. The topmost canvas comes first, so on a screen with a modal over a menu
        /// 'index: 0' is the node the user can actually touch. The canvas that must win here is
        /// named so that ordering by path alone would put it LAST — otherwise a resolver that had
        /// dropped the sort key entirely would still look correct.
        /// </summary>
        [Test]
        public void Resolve_WithIndexAcrossCanvases_OrdersTopmostFirst()
        {
            var bottom = CreateCanvas("UnityFlowTestsACanvas");
            bottom.sortingOrder = short.MaxValue - 1;
            CreateElement(ItemName, bottom.transform, new Vector2(120f, 40f));

            var top = CreateCanvas("UnityFlowTestsZCanvas");
            top.sortingOrder = short.MaxValue;
            CreateElement(ItemName, top.transform, new Vector2(120f, 40f));

            Assert.Less(string.CompareOrdinal("/UnityFlowTestsACanvas", "/UnityFlowTestsZCanvas"), 0,
                "this fixture only proves anything if path order and sort order disagree");

            var first = Resolver.Resolve(IndexedItem(0));
            var second = Resolver.Resolve(IndexedItem(1));

            Assert.AreEqual($"/UnityFlowTestsZCanvas/{ItemName}", first.Node.Path,
                "index 0 must be the topmost node, not the first one a hierarchy walk happens to reach");
            Assert.AreEqual($"/UnityFlowTestsACanvas/{ItemName}", second.Node.Path);
        }

        [Test]
        public void Resolve_WithIndexOutOfRange_IsInvalidSelectorRatherThanACrash()
        {
            BuildTwoItems(betaFirst: false);

            var result = Resolver.Resolve(IndexedItem(5));

            Assert.AreEqual(ResolutionOutcome.InvalidSelector, result.Outcome);
            Assert.IsFalse(result.IsRetryable);
            StringAssert.Contains("valid range 0..1", result.Message);
        }

        [Test]
        public void Resolve_WithinAParent_NarrowsToTheOneInsideIt()
        {
            BuildTwoItems(betaFirst: false);

            var result = Resolver.Resolve(WithinPanel(BetaPanel));

            Assert.AreEqual(ResolutionOutcome.Resolved, result.Outcome);
            Assert.AreEqual($"/{CanvasName}/{BetaPanel}/{ItemName}", result.Node.Path);
        }

        [Test]
        public void Resolve_WithinAParentThatDoesNotExist_SaysSoInsteadOfSearchingEverywhere()
        {
            BuildTwoItems(betaFirst: false);

            var result = Resolver.Resolve(WithinPanel("UnityFlowTestsNoSuchPanel"));

            Assert.AreEqual(ResolutionOutcome.NotFound, result.Outcome);
            StringAssert.Contains("'within' selector", result.Message);
            StringAssert.Contains("did not resolve", result.Message);
        }

        [Test]
        public void Resolve_WithOnlyNarrowingCriteria_IsInvalidSelector()
        {
            BuildTwoItems(betaFirst: false);

            var selector = new Selector(
                null, null, null, TextMatchMode.Exact, null, null, null, null,
                0, null, "ugui", SelectorForm.Mapping, 1, 1);

            var result = Resolver.Resolve(selector);

            Assert.AreEqual(ResolutionOutcome.InvalidSelector, result.Outcome);
            StringAssert.Contains("cannot identify a node", result.Message);
        }

        [Test]
        public void Resolve_WhenTheOnlyMatchIsHidden_SaysItExistsAndWhyItIsNotVisible()
        {
            var faded = CreateElement(FadedName, m_Canvas.transform, new Vector2(120f, 40f));
            var image = faded.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);

            var result = Resolver.Resolve(ByName(FadedName));

            Assert.AreEqual(ResolutionOutcome.NotFound, result.Outcome);
            StringAssert.Contains("matched no VISIBLE node", result.Message);
            StringAssert.Contains("1 hidden node(s)", result.Message);
            StringAssert.Contains("alpha", result.Message);
        }

        private void BuildSubmitButton()
        {
            var submit = CreateElement(SubmitName, m_Canvas.transform, new Vector2(160f, 40f));
            SetTestId(submit.gameObject, SubmitTestId);
        }

        /// <summary>
        /// Two identically named items under differently named parents. Creation order is a
        /// parameter because that is the only thing an index must NOT depend on.
        /// </summary>
        private void BuildTwoItems(bool betaFirst)
        {
            if (betaFirst)
            {
                BuildPanelWithItem(BetaPanel, new Vector2(280f, 0f));
                BuildPanelWithItem(AlphaPanel, new Vector2(-280f, 0f));
            }
            else
            {
                BuildPanelWithItem(AlphaPanel, new Vector2(-280f, 0f));
                BuildPanelWithItem(BetaPanel, new Vector2(280f, 0f));
            }
        }

        private void BuildPanelWithItem(string panelName, Vector2 position)
        {
            var panel = CreateContainer(panelName, m_Canvas.transform, new Vector2(200f, 120f), position);
            CreateElement(ItemName, panel, new Vector2(120f, 40f));
        }

        private void DestroyPanels()
        {
            foreach (var panelName in new[] { AlphaPanel, BetaPanel })
            {
                var panel = m_Canvas.transform.Find(panelName);
                if (panel != null)
                    Object.DestroyImmediate(panel.gameObject);
            }
        }

        private static Selector IndexedItem(int index) => new Selector(
            null, null, null, TextMatchMode.Exact, null, ItemName, null, null,
            index, null, null, SelectorForm.Mapping, 1, 1);

        private static Selector WithinPanel(string panelName) => new Selector(
            null, null, null, TextMatchMode.Exact, null, ItemName, null, null,
            null, ByName(panelName), null, SelectorForm.Mapping, 1, 1);
    }
}
