using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Tests
{
    /// <summary>
    /// <c>runFlow</c>, which is a PARSE-TIME construct and not a step.
    ///
    /// The sub-flow's steps are spliced into the parent's list before the run starts, and every
    /// property that matters follows from that: one flat step list means one progress stream, one
    /// run folder, one set of step indices — and therefore a resume ledger and a domain-reload
    /// rebuild that need no notion of nesting at all. These tests hold that shape, and hold the
    /// refusals that make it safe: a missing file, a cycle, a variable nobody declared, and a
    /// sub-flow declaring something only a whole run can own.
    ///
    /// The hard case is a sub-flow containing <c>enterPlayMode</c>. Resuming works because the
    /// re-parse on the far side of the reload reproduces the SAME flat list, and because the ledger
    /// hashes every file that list came from — both of which are asserted here. The end-to-end proof
    /// is the into-world flow itself, whose enterPlayMode now lives in a sub-flow.
    /// </summary>
    public sealed class RunFlowTests
    {
        private const string ParentReference = "Flows/parent.flow.yaml";

        private MemoryFlowFileSystem m_Files;
        private FlowParser m_Parser;
        private IFlowVerbVocabulary m_Vocabulary;
        private string m_ParentPath;

        [SetUp]
        public void BuildParser()
        {
            m_Files = new MemoryFlowFileSystem();
            m_Parser = new FlowParser(m_Files);
            m_ParentPath = m_Files.Resolve(ParentReference);

            // The real vocabulary: runFlow has to be validated by exactly the same path as every
            // other verb, and a local double declaring its own version of the spec could drift from
            // it without anything noticing.
            m_Vocabulary = new FlowVocabulary();
        }

        [Test]
        public void SubFlowSteps_AreSplicedInPlace_AsOneFlatList()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - screenshot: b\n" +
                "  - screenshot: c\n");

            var document = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - screenshot: a\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n" +
                "  - screenshot: d\n");

            Assert.AreEqual(4, document.Steps.Count, "the sub-flow's steps must BE the parent's steps, not a nested thing");
            CollectionAssert.AreEqual(
                new[] { "a", "b", "c", "d" },
                Names(document.Steps),
                "spliced steps must keep their order and their position in the parent");

            foreach (var step in document.Steps)
                Assert.AreEqual("screenshot", step.Verb, "no runFlow step may survive parsing");
        }

        /// <summary>
        /// A spliced step keeps the line number it has in ITS file, so the file has to travel with
        /// it. Without that, a failure report points at that line number in the parent — a position
        /// that either does not exist or holds a completely unrelated step.
        /// </summary>
        [Test]
        public void SplicedSteps_CarryTheFileTheyWereWrittenIn()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var document = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - screenshot: a\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            Assert.AreEqual(m_ParentPath, document.Steps[0].SourcePath);
            Assert.AreEqual(m_Files.Resolve("Flows/lib/middle.flow.yaml"), document.Steps[1].SourcePath);
            Assert.AreEqual(3, document.Steps[1].Line, "the spliced step keeps the line it occupies in its own file");

            StringAssert.Contains("middle.flow.yaml:3:", document.Locate(document.Steps[1]));
        }

        [Test]
        public void EveryFileTheStepsCameFrom_IsListedOnTheDocument()
        {
            m_Files.Add("Flows/lib/inner.flow.yaml",
                "name: inner\n" +
                "steps:\n" +
                "  - screenshot: c\n");

            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/inner.flow.yaml\n");

            var document = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            CollectionAssert.AreEqual(
                new[]
                {
                    m_ParentPath,
                    m_Files.Resolve("Flows/lib/middle.flow.yaml"),
                    m_Files.Resolve("Flows/lib/inner.flow.yaml")
                },
                document.SourceFiles,
                "the resume ledger hashes this list; a file missing from it is a file whose edits would go undetected");
        }

        // ---- refusals --------------------------------------------------------------------------

        [Test]
        public void MissingFile_FailsAtTheRunFlowStep_NamingWhereItLooked()
        {
            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/absent.flow.yaml\n");

            Assert.AreEqual(m_ParentPath, failure.SourcePath);
            Assert.AreEqual(3, failure.Line);
            Assert.AreEqual(5, failure.Column);
            StringAssert.Contains("names no file", failure.Detail);
            StringAssert.Contains("absent.flow.yaml", failure.Detail);
            StringAssert.Contains("PROJECT ROOT", failure.Detail);
        }

        [Test]
        public void ABadStepInsideASubFlow_FailsWithTheSubFlowsOwnPosition()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - tapOm: \"Shop\"\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            Assert.AreEqual(m_Files.Resolve("Flows/lib/middle.flow.yaml"), failure.SourcePath,
                "a mistake in the sub-flow has to point at the sub-flow, not at the line that ran it");
            Assert.AreEqual(3, failure.Line);
            StringAssert.Contains("unknown step verb 'tapOm'", failure.Detail);
        }

        [Test]
        public void AFlowThatRunsItself_IsReportedAsACycleWithTheChain()
        {
            m_Files.Add("Flows/lib/a.flow.yaml",
                "name: a\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/b.flow.yaml\n");

            m_Files.Add("Flows/lib/b.flow.yaml",
                "name: b\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/a.flow.yaml\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/a.flow.yaml\n");

            StringAssert.Contains("already running it", failure.Detail);
            StringAssert.Contains("a.flow.yaml", failure.Detail);
            StringAssert.Contains("b.flow.yaml", failure.Detail);
            StringAssert.Contains("infinite step list", failure.Detail);
        }

        [Test]
        public void AFlowThatRunsItselfDirectly_IsACycleToo()
        {
            m_Files.Add("Flows/lib/a.flow.yaml",
                "name: a\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/a.flow.yaml\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/a.flow.yaml\n");

            StringAssert.Contains("already running it", failure.Detail);
        }

        [Test]
        public void ASubFlowDeclaringBeforeOrAfter_IsRefusedWithTheReason()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "after:\n" +
                "  - screenshot: teardown\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            Assert.AreEqual(m_Files.Resolve("Flows/lib/middle.flow.yaml"), failure.SourcePath);
            Assert.AreEqual(2, failure.Line);
            StringAssert.Contains("cannot declare 'after'", failure.Detail);
            StringAssert.Contains("teardown", failure.Detail);
        }

        [Test]
        public void ASubFlowDeclaringTimeScale_IsRefusedBecauseNothingWouldReadIt()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "timeScale: 2\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            StringAssert.Contains("cannot declare 'timeScale'", failure.Detail);
        }

        // ---- env plumbing ------------------------------------------------------------------------

        [Test]
        public void TheParentSuppliesTheSubFlowsDeclaredVariables()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "env:\n" +
                "  character: \"\"\n" +
                "steps:\n" +
                "  - screenshot: \"shot-${character}\"\n");

            var document = Parse(
                "name: parent\n" +
                "env:\n" +
                "  who: demo-user\n" +
                "steps:\n" +
                "  - runFlow: { file: Flows/lib/middle.flow.yaml, env: { character: \"${who}\" } }\n");

            Assert.AreEqual("shot-demo-user", document.Steps[0].Get<string>("name"),
                "the parent's own variable has to be substituted before it is handed on, or nothing could be passed through");
        }

        [Test]
        public void ASubFlowsDefaultIsUsedWhenTheParentSuppliesNothing()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "env:\n" +
                "  character: fallbackName\n" +
                "steps:\n" +
                "  - screenshot: \"shot-${character}\"\n");

            var document = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            Assert.AreEqual("shot-fallbackName", document.Steps[0].Get<string>("name"));
        }

        /// <summary>
        /// Exactly how <c>--env</c> behaves, and for the same reason: a variable nobody reads is a
        /// typo every time, and accepting it silently runs the flow with the default nobody wanted.
        /// The position must be the PARENT's key, because that is the line somebody has to fix.
        /// </summary>
        [Test]
        public void AVariableTheSubFlowDoesNotDeclare_IsRefusedAtTheParentsOwnKey()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "env:\n" +
                "  character: \"\"\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow:\n" +
                "      file: Flows/lib/middle.flow.yaml\n" +
                "      env:\n" +
                "        charater: bob\n");

            Assert.AreEqual(m_ParentPath, failure.SourcePath);
            Assert.AreEqual(6, failure.Line);
            Assert.AreEqual(9, failure.Column);
            StringAssert.Contains("declares no variable 'charater'", failure.Detail);
            StringAssert.Contains("Did you mean 'character'?", failure.Detail);
        }

        [Test]
        public void ASubFlowWithNoEnvBlock_RefusesAnyVariableAtAll()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: { file: Flows/lib/middle.flow.yaml, env: { character: bob } }\n");

            StringAssert.Contains("declares no variable 'character'", failure.Detail);
            StringAssert.Contains("no 'env:' block at all", failure.Detail);
        }

        [Test]
        public void ASubFlowsInputRequirement_IsFoldedIntoTheRun()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "requires: { input: system }\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var document = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            Assert.AreEqual(InputRequirement.System, document.Requires.Input,
                "dropping a sub-flow's demand would let the run fall back to synthesized events and report a pass worth less");
        }

        [Test]
        public void ConflictingInputRequirements_AreRefusedRatherThanPreferringOne()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "requires: { input: system }\n" +
                "steps:\n" +
                "  - screenshot: b\n");

            var failure = ExpectFailure(
                "name: parent\n" +
                "requires: { input: semantic }\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n");

            StringAssert.Contains("cannot both be honoured", failure.Detail);
            StringAssert.Contains("middle.flow.yaml", failure.Detail);
        }

        // ---- resume ------------------------------------------------------------------------------

        /// <summary>
        /// The property the whole resume mechanism rests on: re-parsing the same files produces the
        /// same flat list, so the ledger's step index still names the step the run stopped at. This
        /// is what makes an <c>enterPlayMode</c> INSIDE a sub-flow resumable at all.
        /// </summary>
        [Test]
        public void ReParsingProducesTheSameFlatListWithTheSameIndices()
        {
            m_Files.Add("Flows/lib/middle.flow.yaml",
                "name: middle\n" +
                "steps:\n" +
                "  - enterPlayMode: SomeScene.unity\n" +
                "  - screenshot: after-reload\n");

            var first = Parse(
                "name: parent\n" +
                "steps:\n" +
                "  - runFlow: Flows/lib/middle.flow.yaml\n" +
                "  - screenshot: last\n");

            var second = new FlowParser(m_Files).ParseFile(m_ParentPath, m_Vocabulary);

            Assert.AreEqual(first.Steps.Count, second.Steps.Count);
            for (var i = 0; i < first.Steps.Count; i++)
            {
                Assert.AreEqual(first.Steps[i].Verb, second.Steps[i].Verb, $"step {i} changed between parses");
                Assert.AreEqual(first.Steps[i].SourcePath, second.Steps[i].SourcePath);
                Assert.AreEqual(first.Steps[i].Line, second.Steps[i].Line);
            }

            Assert.AreEqual("enterPlayMode", first.Steps[0].Verb,
                "a sub-flow's enterPlayMode has to be an ordinary step of the parent's list, or the resume cursor means nothing");
        }

        /// <summary>
        /// And the guard that makes that safe: the ledger's hash covers every file the list came
        /// from, so a sub-flow edited while the run was suspended is caught exactly as an edited
        /// parent is. Real files, because the hash reads bytes off disk.
        /// </summary>
        [Test]
        public void TheResumeHashChangesWhenASubFlowChanges()
        {
            var directory = Path.Combine(Path.GetTempPath(), "unityflow-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);

            try
            {
                var parent = Path.Combine(directory, "parent.flow.yaml");
                var child = Path.Combine(directory, "child.flow.yaml");

                File.WriteAllText(parent, "name: parent\n");
                File.WriteAllText(child, "name: child\nsteps:\n  - screenshot: b\n");

                var files = new[] { parent, child };
                var before = FlowResumeState.HashFiles(files);

                Assert.AreEqual(before, FlowResumeState.HashFiles(files), "the hash has to be stable for unchanged files");

                File.WriteAllText(child, "name: child\nsteps:\n  - screenshot: b\n  - screenshot: c\n");

                Assert.AreNotEqual(before, FlowResumeState.HashFiles(files),
                    "an edited sub-flow shifts every step index after it; resuming through that would report on a run that never happened");

                Assert.AreNotEqual(before, FlowResumeState.HashFiles(new[] { parent }),
                    "hashing only the started file is exactly the blind spot this replaces");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        // ---- harness ------------------------------------------------------------------------------

        private FlowDocument Parse(string parentYaml)
        {
            m_Files.Add(ParentReference, parentYaml);
            return m_Parser.ParseFile(m_ParentPath, m_Vocabulary);
        }

        private FlowParseException ExpectFailure(string parentYaml)
        {
            m_Files.Add(ParentReference, parentYaml);
            return Assert.Throws<FlowParseException>(() => m_Parser.ParseFile(m_ParentPath, m_Vocabulary));
        }

        private static string[] Names(IReadOnlyList<FlowStep> steps)
        {
            var names = new string[steps.Count];
            for (var i = 0; i < steps.Count; i++)
                names[i] = steps[i].Get<string>("name");

            return names;
        }

        /// <summary>
        /// Flow files that never touch the disk.
        ///
        /// The parser reaching outside the document is exactly what <see cref="IFlowFileSystem"/>
        /// exists to name, and this is why: a sub-flow test that had to write real files would be
        /// slower, would leave artefacts in the project, and would make every assertion about
        /// resolution depend on where the test ran from.
        /// </summary>
        private sealed class MemoryFlowFileSystem : IFlowFileSystem
        {
            private const string Root = "/unityflow-tests-root/";

            private readonly Dictionary<string, string> m_Files = new Dictionary<string, string>(StringComparer.Ordinal);

            public void Add(string reference, string yaml) => m_Files[Resolve(reference)] = yaml;

            public string Resolve(string reference)
            {
                if (string.IsNullOrWhiteSpace(reference))
                    throw new ArgumentException("A flow reference cannot be empty.", nameof(reference));

                return reference.StartsWith(Root, StringComparison.Ordinal) ? reference : Root + reference;
            }

            public bool Exists(string absolutePath) => m_Files.ContainsKey(absolutePath);

            public string ReadAllText(string absolutePath) => m_Files[absolutePath];
        }
    }
}
