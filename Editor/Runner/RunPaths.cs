using System;
using System.IO;
using UnityEngine;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// On-disk layout of a single run.
    ///
    /// Everything a run produces lives under one directory so the host CLI can find it knowing
    /// only the run id, with no knowledge of Unity's folder conventions. It deliberately does NOT
    /// use <c>Temp/</c>: artifacts must survive the editor closing so a failure can still be read
    /// afterwards, and the cancel sentinel must be writable by a process that is not Unity.
    ///
    /// The directory is outside Assets/ so nothing here is ever imported as an asset — a .png
    /// under Assets/ would trigger a texture import on every screenshot.
    /// </summary>
    public sealed class RunPaths
    {
        /// <summary>Folder name at the project root. Add it to .gitignore.</summary>
        public const string RootFolderName = ".unityflow";

        public string RunId { get; }

        /// <summary>Directory holding everything this run produced.</summary>
        public string RunDirectory { get; }

        /// <summary>Append-only progress stream, one JSON object per line.</summary>
        public string ProgressFile { get; }

        /// <summary>Latest status snapshot, rewritten atomically. Readable during a domain reload.</summary>
        public string StatusFile { get; }

        /// <summary>
        /// Sentinel written by the host CLI to request cancellation. It is a FILE and not an HTTP
        /// command on purpose: the pipeline server handles requests strictly one at a time, so
        /// while flow.run is in flight a second HTTP call cannot be served at all.
        /// </summary>
        public string CancelFile { get; }

        /// <summary>Screenshots and failure snapshots.</summary>
        public string ArtifactsDirectory { get; }

        /// <summary>The final machine-readable report.</summary>
        public string ReportFile { get; }

        private RunPaths(string projectRoot, string runId)
        {
            RunId = runId;
            RunDirectory = Path.Combine(projectRoot, RootFolderName, "runs", runId);
            ProgressFile = Path.Combine(RunDirectory, "progress.ndjson");
            StatusFile = Path.Combine(RunDirectory, "status.json");
            CancelFile = Path.Combine(RunDirectory, "cancel");
            ArtifactsDirectory = Path.Combine(RunDirectory, "artifacts");
            ReportFile = Path.Combine(RunDirectory, "report.json");
        }

        /// <summary>Absolute path of the folder that contains Assets/, Packages/ and Library/.</summary>
        public static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)?.FullName
            ?? throw new InvalidOperationException("Could not determine the Unity project root from Application.dataPath.");

        /// <summary>Create the layout for a run, making every directory it needs.</summary>
        public static RunPaths CreateFor(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new ArgumentException("A run id is required.", nameof(runId));

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                if (runId.IndexOf(c) >= 0)
                    throw new ArgumentException($"Run id '{runId}' contains the invalid path character '{c}'.", nameof(runId));
            }

            var paths = new RunPaths(ProjectRoot, runId);
            Directory.CreateDirectory(paths.RunDirectory);
            Directory.CreateDirectory(paths.ArtifactsDirectory);
            return paths;
        }

        /// <summary>Point at an existing run without creating anything — used by flow.status.</summary>
        public static RunPaths Existing(string runId) => new RunPaths(ProjectRoot, runId);

        /// <summary>Whether the host CLI has asked this run to stop.</summary>
        public bool CancelRequested => File.Exists(CancelFile);

        /// <summary>Path an artifact should be written to, inside this run's folder.</summary>
        public string Artifact(string fileName) => Path.Combine(ArtifactsDirectory, fileName);
    }
}
