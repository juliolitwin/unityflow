using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// Parse failures, judged by their POSITION as much as by their text.
    ///
    /// The parser reads YamlDotNet's representation model instead of its object deserializer for
    /// exactly one reason: every node carries the line and column it came from. A message that
    /// loses those is a bug report the author cannot act on, so every case here asserts the exact
    /// line and column alongside the sentence.
    ///
    /// The vocabulary is a local double rather than <c>FlowVocabulary</c>: the real one also
    /// contains whatever <c>[FlowCommand]</c> methods the surrounding project happens to declare,
    /// which would make "Did you mean ...?" depend on code these tests do not own.
    /// </summary>
    public sealed class FlowParserTests
    {
        private const string SourcePath = "unityflow-tests.flow.yaml";

        private FlowParser m_Parser;
        private IFlowVerbVocabulary m_Vocabulary;

        [SetUp]
        public void BuildParser()
        {
            m_Parser = new FlowParser();
            m_Vocabulary = new TestVocabulary();
        }

        [Test]
        public void UnknownVerb_IsReportedAtTheVerbWithASuggestion()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - tapOm: \"Shop\"\n");

            AssertPosition(failure, 3, 5);
            StringAssert.Contains("unknown step verb 'tapOm'", failure.Detail);
            StringAssert.Contains("Did you mean 'tapOn'?", failure.Detail);
        }

        [Test]
        public void UnknownArgumentKey_IsReportedAtTheKeyWithASuggestion()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - tapOn:\n" +
                "      text: \"Shop\"\n" +
                "      timeut: 5s\n");

            AssertPosition(failure, 5, 7);
            StringAssert.Contains("'tapOn' has no argument 'timeut'", failure.Detail);
            StringAssert.Contains("Did you mean 'timeout'?", failure.Detail);
        }

        [Test]
        public void MisspelledSelectorKey_IsRejectedRatherThanIgnored()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - tapOn:\n" +
                "      testid: shop.root\n");

            AssertPosition(failure, 4, 7);
            StringAssert.Contains("Did you mean 'testId'?", failure.Detail);
        }

        [Test]
        public void BadDuration_IsReportedAtTheValue()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - wait: soon\n");

            AssertPosition(failure, 3, 11);
            StringAssert.Contains("'duration' expects a duration like 5s or 500ms, got 'soon'", failure.Detail);
        }

        [Test]
        public void MissingRequiredArgument_IsReportedAtTheVerb()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - screenshot:\n");

            AssertPosition(failure, 3, 5);
            StringAssert.Contains("'screenshot' requires 'name'", failure.Detail);
        }

        [Test]
        public void UnknownTopLevelKey_IsReportedAtTheKey()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "stpes:\n" +
                "  - wait: 1s\n");

            AssertPosition(failure, 2, 1);
            StringAssert.Contains("unknown top-level key 'stpes'", failure.Detail);
            StringAssert.Contains("Did you mean 'steps'?", failure.Detail);
        }

        [Test]
        public void MultipleDocuments_AreRejectedAtTheSecondOne()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - wait: 1s\n" +
                "---\n" +
                "name: u\n" +
                "steps:\n" +
                "  - wait: 1s\n");

            AssertPosition(failure, 5, 1);
            StringAssert.Contains("must hold exactly one YAML document, found 2", failure.Detail);
        }

        [Test]
        public void SingleAtSignIsAReference_AndDoubledAtSignIsALiteral()
        {
            var document = m_Parser.Parse(
                "name: t\n" +
                "steps:\n" +
                "  - screenshot: \"@shotName\"\n" +
                "  - screenshot: \"@@shotName\"\n" +
                "  - tapOn: \"@sword\"\n" +
                "  - tapOn: \"@@sword\"\n",
                SourcePath, m_Vocabulary);

            Assert.AreEqual(4, document.Steps.Count);

            var reference = document.Steps[0].Args[0];
            Assert.IsTrue(reference.IsReference, "'@name' names a value an earlier step bound, so it cannot be converted at parse time");
            Assert.AreEqual("shotName", reference.Reference);

            var literal = document.Steps[1].Args[0];
            Assert.IsFalse(literal.IsReference);
            Assert.AreEqual("@shotName", literal.Value, "'@@' is the escape for a literal at-sign");

            Assert.AreEqual("sword", document.Steps[2].Selector.Reference);

            Assert.IsNull(document.Steps[3].Selector.Reference);
            Assert.AreEqual("@sword", document.Steps[3].Selector.Text);
        }

        private FlowParseException ExpectFailure(string yaml) =>
            Assert.Throws<FlowParseException>(() => m_Parser.Parse(yaml, SourcePath, m_Vocabulary));

        private static void AssertPosition(FlowParseException failure, int line, int column)
        {
            Assert.AreEqual(SourcePath, failure.SourcePath);
            Assert.AreEqual(line, failure.Line, $"wrong line for: {failure.Detail}");
            Assert.AreEqual(column, failure.Column, $"wrong column for: {failure.Detail}");
        }

        /// <summary>
        /// The three verbs these tests need, declared exactly as the runner declares them so the
        /// argument names a suggestion can reach are the real ones.
        /// </summary>
        private sealed class TestVocabulary : IFlowVerbVocabulary
        {
            private readonly Dictionary<string, FlowVerbSpec> m_Verbs = new Dictionary<string, FlowVerbSpec>(StringComparer.Ordinal);
            private readonly List<string> m_Names = new List<string>();

            public TestVocabulary()
            {
                Add(new FlowVerbSpec("tapOn", new[]
                    {
                        new FlowArgSpec("allowUnverifiedOcclusion", FlowArgKind.Bool)
                    },
                    SelectorMode.Required));

                Add(new FlowVerbSpec("wait", new[]
                    {
                        new FlowArgSpec("duration", FlowArgKind.Duration, required: true)
                    },
                    SelectorMode.None, bareScalarArg: "duration"));

                Add(new FlowVerbSpec("screenshot", new[]
                    {
                        new FlowArgSpec("name", FlowArgKind.String, required: true)
                    },
                    SelectorMode.None, bareScalarArg: "name"));

                m_Names.Sort(StringComparer.Ordinal);
            }

            public IReadOnlyList<string> VerbNames => m_Names;

            public bool TryGetVerb(string name, out FlowVerbSpec spec) => m_Verbs.TryGetValue(name, out spec);

            private void Add(FlowVerbSpec spec)
            {
                m_Verbs.Add(spec.Name, spec);
                m_Names.Add(spec.Name);
            }
        }
    }
}
