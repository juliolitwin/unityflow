using System;
using System.IO;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.Yaml
{
    /// <summary>
    /// How <c>runFlow</c> turns the path written in a flow into a file it can read.
    ///
    /// It is an interface for one reason: the parser must be testable without a project on disk.
    /// Every other part of parsing is a pure function of the text it was handed, and a sub-flow is
    /// the first thing that reaches outside the document — so the reach is named, injected, and
    /// replaceable rather than being a <c>File.ReadAllText</c> buried in the parser.
    /// </summary>
    public interface IFlowFileSystem
    {
        /// <summary>
        /// The absolute path a <c>runFlow</c> reference denotes. Throws
        /// <see cref="ArgumentException"/> for a reference that cannot name a file at all.
        /// </summary>
        string Resolve(string reference);

        /// <summary>Whether that file is there.</summary>
        bool Exists(string absolutePath);

        /// <summary>Its full text.</summary>
        string ReadAllText(string absolutePath);
    }

    /// <summary>
    /// The real one: a <c>runFlow</c> reference is resolved against the PROJECT ROOT, exactly like
    /// the <c>--file</c> of <c>flow.run</c> and <c>flow.start</c>.
    ///
    /// One rule, deliberately, and it is the rule the author already knows. Resolving relative to
    /// the including file instead would mean the same string means different files depending on
    /// which flow wrote it, and trying one and then the other would be a fallback chain that
    /// silently picks a file the author did not name. An absolute path is taken as written.
    /// </summary>
    public sealed class ProjectFlowFileSystem : IFlowFileSystem
    {
        private readonly string m_Root;

        /// <param name="root">
        /// Directory relative references resolve against. Defaults to the Unity project root, the
        /// folder holding Assets/ and Packages/.
        /// </param>
        public ProjectFlowFileSystem(string root = null)
        {
            m_Root = string.IsNullOrEmpty(root) ? RunPaths.ProjectRoot : root;
        }

        public string Resolve(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("A flow reference cannot be empty.", nameof(reference));

            return Path.GetFullPath(Path.IsPathRooted(reference)
                ? reference
                : Path.Combine(m_Root, reference));
        }

        public bool Exists(string absolutePath) => File.Exists(absolutePath);

        public string ReadAllText(string absolutePath) => File.ReadAllText(absolutePath);
    }
}
