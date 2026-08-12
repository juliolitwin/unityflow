using NUnit.Framework;
using UnityFlow.Editor.Window;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// Reading a flow's declared name without parsing it.
    ///
    /// The picker lists files that are being EDITED, so it cannot afford to parse them: one
    /// half-written flow would empty the whole list. A <c>name:</c> at zero indentation is
    /// unambiguous in YAML, which is the entire reason a line scan is allowed to stand in for the
    /// parser here — and the cases below are the ones where a looser scan would name a flow after
    /// a button.
    /// </summary>
    public sealed class FlowCatalogTests
    {
        [Test]
        public void TheDocumentsOwnNameIsRead()
        {
            Assert.AreEqual("courier-menu-form", FlowCatalog.ReadName(new[]
            {
                "# 01 - THE FORM.",
                "",
                "name: courier-menu-form",
                "requires: { input: system }"
            }));
        }

        [Test]
        public void AnIndentedNameIsASelectorCriterionAndNotTheFlowsName()
        {
            Assert.IsNull(FlowCatalog.ReadName(new[]
            {
                "steps:",
                "  - waitFor: { name: MenuPanel }"
            }));
        }

        [Test]
        public void QuotingSaysThisIsTextAndIsNotPartOfTheText()
        {
            Assert.AreEqual("kundun into world", FlowCatalog.ReadName(new[] { "name: \"kundun into world\"" }));
        }

        [Test]
        public void ATrailingCommentIsNotPartOfTheName()
        {
            Assert.AreEqual("tap-proof", FlowCatalog.ReadName(new[] { "name: tap-proof  # the smallest flow there is" }));
        }
    }
}
