using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// Flow parameters: the <c>env:</c> block, <c>${name}</c> substitution, and the overrides a
    /// caller lays on top.
    ///
    /// The cases worth writing down are the ones about what the tool REFUSES. A parameterized flow
    /// is only worth having if a misspelled variable is louder than a working one — an undefined
    /// name that resolved to an empty string would type "" into a login field and report a pass for
    /// a session that never existed, which is the failure the whole feature exists to prevent. So
    /// every negative case here asserts the position as well as the sentence, exactly as
    /// <see cref="FlowParserTests"/> does.
    /// </summary>
    public sealed class FlowEnvTests
    {
        private const string SourcePath = "unityflow-tests.flow.yaml";

        private FlowParser m_Parser;
        private IFlowVerbVocabulary m_Vocabulary;

        [SetUp]
        public void BuildParser()
        {
            m_Parser = new FlowParser();
            m_Vocabulary = new EnvVocabulary();
        }

        [Test]
        public void DeclaredVariable_IsSubstitutedIntoAnArgument()
        {
            var document = Parse(
                "name: t\n" +
                "env:\n" +
                "  account: demo\n" +
                "steps:\n" +
                "  - inputText: { name: Input, text: \"${account}\" }\n");

            Assert.AreEqual("demo", document.Steps[0].Get<string>("text"));
        }

        [Test]
        public void Override_BeatsTheDeclaredDefault()
        {
            var document = Parse(
                "name: t\n" +
                "env: { account: demo }\n" +
                "steps:\n" +
                "  - inputText: { name: Input, text: \"${account}\" }\n",
                "account=someone");

            Assert.AreEqual("someone", document.Steps[0].Get<string>("text"));
        }

        [Test]
        public void EmptyDefault_IsAValueAndNotAnAbsence()
        {
            var document = Parse(
                "name: t\n" +
                "env: { character: \"\" }\n" +
                "steps:\n" +
                "  - screenshot: \"shot-${character}\"\n");

            Assert.AreEqual("shot-", document.Steps[0].Get<string>("name"));
        }

        [Test]
        public void EffectiveEnv_TravelsOnTheDocument_SortedAndComplete()
        {
            var document = Parse(
                "name: t\n" +
                "env: { zeta: 1, alpha: 2 }\n" +
                "steps:\n" +
                "  - screenshot: shot\n",
                "zeta=9");

            // Sorted and complete, because this is what the run header prints and what the resume
            // ledger carries across a domain reload: two runs with the same parameters have to
            // produce the same text, and a defaulted variable is as much a parameter as an
            // overridden one.
            CollectionAssert.AreEqual(new[] { "alpha=2", "zeta=9" }, document.Env.ToPairs());
        }

        [Test]
        public void Substitution_ReachesSelectorsAndScriptBodiesAlike()
        {
            var document = Parse(
                "name: t\n" +
                "env: { button: btn_enter, character: demo-user }\n" +
                "steps:\n" +
                "  - tapOn: { name: \"${button}\" }\n" +
                "  - runScript: |\n" +
                "      var wanted = \"${character}\";\n" +
                "      return wanted;\n");

            Assert.AreEqual("btn_enter", document.Steps[0].Selector.Name);
            Assert.AreEqual("var wanted = \"demo-user\";\nreturn wanted;\n", document.Steps[1].Get<string>("code"));
        }

        [Test]
        public void QuotedSubstitution_StillConvertsToTheDeclaredType()
        {
            // The quotes are YAML's demand, not the author's: '{ timeout: ${wait} }' is unwritable
            // in flow style because '{' is an indicator there. A substituted scalar is therefore
            // read as unquoted, or every numeric argument would reject its own variable with
            // "remove the quotes" — advice that cannot be followed.
            var document = Parse(
                "name: t\n" +
                "env: { wait: 90s, presses: 3 }\n" +
                "steps:\n" +
                "  - wait: \"${wait}\"\n" +
                "  - press: { key: enter, count: \"${presses}\" }\n");

            Assert.AreEqual(TimeSpan.FromSeconds(90), document.Steps[0].Get<TimeSpan>("duration"));
            Assert.AreEqual(3, document.Steps[1].Get<int>("count"));
        }

        [Test]
        public void DoubledDollar_EscapesALiteralDollarBrace()
        {
            var document = Parse(
                "name: t\n" +
                "env: { a: 1 }\n" +
                "steps:\n" +
                "  - screenshot: \"$${literal}-${a}\"\n" +
                "  - screenshot: \"cost $5 or $$\"\n");

            Assert.AreEqual("${literal}-1", document.Steps[0].Get<string>("name"));

            // A lone '$' is never special, so shell-looking text passes through untouched.
            Assert.AreEqual("cost $5 or $$", document.Steps[1].Get<string>("name"));
        }

        [Test]
        public void SubstitutedText_IsThenReadByTheOrdinaryArgumentRules()
        {
            // '@@' is resolved by the argument layer, not by the env layer, so a variable holding a
            // password that starts with '@' behaves exactly as if it had been typed into the step.
            var document = Parse(
                "name: t\n" +
                "env: { password: \"@@#secret\" }\n" +
                "steps:\n" +
                "  - inputText: { name: Input, text: \"${password}\" }\n");

            Assert.AreEqual("@#secret", document.Steps[0].Get<string>("text"));
        }

        [Test]
        public void UndefinedVariable_IsAParseErrorListingWhatIsDefined()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { account: demo, character: \"\" }\n" +
                "steps:\n" +
                "  - screenshot: \"${charater}\"\n");

            AssertPosition(failure, 4, 17);
            StringAssert.Contains("is not defined by this flow's 'env:' block", failure.Detail);
            StringAssert.Contains("Defined: account, character", failure.Detail);
            StringAssert.Contains("Did you mean 'character'?", failure.Detail);
        }

        [Test]
        public void UndefinedVariable_InAFlowWithNoEnvBlock_SaysHowToDeclareOne()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "steps:\n" +
                "  - screenshot: \"${account}\"\n");

            AssertPosition(failure, 3, 17);
            StringAssert.Contains("no 'env:' block", failure.Detail);
        }

        [Test]
        public void UndefinedVariable_InsideABlockScalar_ReportsItsOwnLine()
        {
            // A block scalar's YAML mark is its '|' indicator and its text starts on the next line.
            // Pointing every failure in a thirty-line runScript body at that indicator would defeat
            // the reason this parser reads positions at all.
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { a: 1 }\n" +
                "steps:\n" +
                "  - runScript: |\n" +
                "      var x = 1;\n" +
                "      var y = 2;\n" +
                "      var z = \"${nope}\";\n");

            AssertPosition(failure, 7, 1);
            StringAssert.Contains("${nope}", failure.Detail);
        }

        [Test]
        public void UnclosedVariable_IsReportedRatherThanTakenAsText()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { a: 1 }\n" +
                "steps:\n" +
                "  - screenshot: \"${a\"\n");

            AssertPosition(failure, 4, 17);
            StringAssert.Contains("never closed", failure.Detail);
        }

        [Test]
        public void MalformedVariableName_IsReported()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { a: 1 }\n" +
                "steps:\n" +
                "  - screenshot: \"${a-b}\"\n");

            AssertPosition(failure, 4, 17);
            StringAssert.Contains("is not a variable name", failure.Detail);
        }

        [Test]
        public void OverrideOfAnUndeclaredName_IsRefusedAtTheEnvBlock()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { account: demo }\n" +
                "steps:\n" +
                "  - screenshot: shot\n",
                "accont=x");

            AssertPosition(failure, 2, 1);
            StringAssert.Contains("does not declare it", failure.Detail);
            StringAssert.Contains("Did you mean 'account'?", failure.Detail);
        }

        [Test]
        public void SubstitutedValueThatDoesNotConvert_NamesWhatItWasWrittenAs()
        {
            // Without the tail, the message points at a line where the word 'soon' does not appear.
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { wait: soon }\n" +
                "steps:\n" +
                "  - wait: \"${wait}\"\n");

            AssertPosition(failure, 4, 11);
            StringAssert.Contains("expects a duration like 5s or 500ms, got 'soon'", failure.Detail);
            StringAssert.Contains("substituted from \"${wait}\"", failure.Detail);
        }

        [Test]
        public void EnvDefault_MayNotBeDefinedInTermsOfAnotherVariable()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env: { a: 1, b: \"${a}\" }\n" +
                "steps:\n" +
                "  - screenshot: shot\n");

            AssertPosition(failure, 2, 17);
            StringAssert.Contains("an env default is a literal", failure.Detail);
        }

        [Test]
        public void EnvDefault_MustBeASingleValue()
        {
            var failure = ExpectFailure(
                "name: t\n" +
                "env:\n" +
                "  a: [1, 2]\n" +
                "steps:\n" +
                "  - screenshot: shot\n");

            AssertPosition(failure, 3, 6);
            StringAssert.Contains("must be a single value", failure.Detail);
        }

        [Test]
        public void Pairs_AreParsedStrictly()
        {
            Assert.IsFalse(FlowEnv.TryParsePairs(new[] { "account" }, out _, out var noEquals));
            StringAssert.Contains("has no '='", noEquals);

            Assert.IsFalse(FlowEnv.TryParsePairs(new[] { "a=1", "a=2" }, out _, out var duplicate));
            StringAssert.Contains("was given twice", duplicate);

            Assert.IsFalse(FlowEnv.TryParsePairs(new[] { "=1" }, out _, out var noName));
            StringAssert.Contains("names no variable", noName);

            // A value may hold '=' and may be empty; only the FIRST '=' separates the two.
            Assert.IsTrue(FlowEnv.TryParsePairs(new[] { "a=x=y", "b=" }, out var env, out _));
            Assert.IsTrue(env.TryGet("a", out var a));
            Assert.AreEqual("x=y", a);
            Assert.IsTrue(env.TryGet("b", out var b));
            Assert.AreEqual(string.Empty, b);
        }

        [Test]
        public void EffectiveEnv_RoundTripsThroughItsWireForm()
        {
            // This is the guarantee the resume ledger depends on: what a segment writes to disk as
            // 'name=value' must parse back into the same env, or the segment on the far side of a
            // domain reload would build its steps with different parameters.
            var original = Parse(
                "name: t\n" +
                "env: { account: demo, character: \"\", password: \"@@#secret\" }\n" +
                "steps:\n" +
                "  - screenshot: shot\n",
                "character=demo-user");

            Assert.IsTrue(FlowEnv.TryParsePairs(original.Env.ToPairs(), out var reloaded, out var error), error);
            CollectionAssert.AreEqual(original.Env.ToPairs(), reloaded.ToPairs());
        }

        private FlowDocument Parse(string yaml, params string[] overrides)
        {
            Assert.IsTrue(FlowEnv.TryParsePairs(overrides, out var env, out var error), error);
            return m_Parser.Parse(yaml, SourcePath, m_Vocabulary, env);
        }

        private FlowParseException ExpectFailure(string yaml, params string[] overrides)
        {
            Assert.IsTrue(FlowEnv.TryParsePairs(overrides, out var env, out var error), error);
            return Assert.Throws<FlowParseException>(() => m_Parser.Parse(yaml, SourcePath, m_Vocabulary, env));
        }

        private static void AssertPosition(FlowParseException failure, int line, int column)
        {
            Assert.AreEqual(SourcePath, failure.SourcePath);
            Assert.AreEqual(line, failure.Line, $"wrong line for: {failure.Detail}");
            Assert.AreEqual(column, failure.Column, $"wrong column for: {failure.Detail}");
        }

        /// <summary>The verbs these tests substitute into, declared as the runner declares them.</summary>
        private sealed class EnvVocabulary : IFlowVerbVocabulary
        {
            private readonly Dictionary<string, FlowVerbSpec> m_Verbs = new Dictionary<string, FlowVerbSpec>(StringComparer.Ordinal);
            private readonly List<string> m_Names = new List<string>();

            public EnvVocabulary()
            {
                Add(new FlowVerbSpec("tapOn", Array.Empty<FlowArgSpec>(), SelectorMode.Required));

                Add(new FlowVerbSpec("inputText", new[]
                    {
                        new FlowArgSpec("text", FlowArgKind.String, required: true)
                    },
                    SelectorMode.Required));

                Add(new FlowVerbSpec("wait", new[]
                    {
                        new FlowArgSpec("duration", FlowArgKind.Duration, required: true)
                    },
                    SelectorMode.None, bareScalarArg: "duration"));

                Add(new FlowVerbSpec("press", new[]
                    {
                        new FlowArgSpec("key", FlowArgKind.String, required: true),
                        new FlowArgSpec("count", FlowArgKind.Int)
                    },
                    SelectorMode.None, bareScalarArg: "key"));

                Add(new FlowVerbSpec("screenshot", new[]
                    {
                        new FlowArgSpec("name", FlowArgKind.String, required: true)
                    },
                    SelectorMode.None, bareScalarArg: "name"));

                Add(new FlowVerbSpec("runScript", new[]
                    {
                        new FlowArgSpec("code", FlowArgKind.String, required: true)
                    },
                    SelectorMode.None, bareScalarArg: "code"));

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
