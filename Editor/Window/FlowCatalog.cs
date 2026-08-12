using System;
using System.Collections.Generic;
using System.IO;

namespace UnityFlow.Editor.Window
{
    /// <summary>One flow file on disk, as the picker needs to show it.</summary>
    public sealed class FlowEntry
    {
        /// <summary>Absolute path, which is what every command in the package takes.</summary>
        public string Path;

        /// <summary>Project-relative path with forward slashes, which is what a human recognises.</summary>
        public string RelativePath;

        /// <summary>Project-relative directory the file sits in, used to group the list.</summary>
        public string Folder;

        /// <summary>
        /// The flow's declared <c>name:</c>, or null when the file does not declare one — which is
        /// a file that will not parse, so the picker says the file name instead of inventing a name.
        /// </summary>
        public string Name;

        /// <summary>What the row's primary line reads.</summary>
        public string Title => Name ?? System.IO.Path.GetFileName(Path);
    }

    /// <summary>A folder of flows, which is the unit the picker collapses.</summary>
    public sealed class FlowFolder
    {
        public string Path;
        public readonly List<FlowEntry> Flows = new List<FlowEntry>();
    }

    /// <summary>
    /// Every flow in the project, named and grouped by folder.
    ///
    /// The names come from a line scan rather than the parser on purpose: the picker lists files
    /// that are being EDITED, and a full parse of a half-written flow throws — a list that empties
    /// itself because one file is mid-edit is worse than a list that shows that file by its name.
    /// A <c>name:</c> at zero indentation is unambiguous in YAML, so the scan cannot pick up a
    /// nested key that happens to be called the same thing.
    /// </summary>
    public static class FlowCatalog
    {
        private const string Extension = "*.flow.yaml";

        /// <summary>
        /// Every flow under <c>Assets/</c> and under a <c>Flows/</c> folder at the project root —
        /// which is where a flow that is not an asset belongs, since nothing about a .flow.yaml
        /// needs importing.
        /// </summary>
        public static List<FlowEntry> Discover(string projectRoot)
        {
            var flows = new List<FlowEntry>();

            Collect(projectRoot, System.IO.Path.Combine(projectRoot, "Assets"), flows);
            Collect(projectRoot, System.IO.Path.Combine(projectRoot, "Flows"), flows);

            // By folder first, so a folder's own flows are listed before its subfolders' — sorting
            // whole paths puts "Flows/lib/..." ahead of "Flows/warmup...", which reverses the tree.
            flows.Sort((a, b) =>
            {
                var folder = string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase);
                return folder != 0 ? folder : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
            });

            return flows;
        }

        /// <summary>The flows grouped by folder, folders in path order, each keeping the given order.</summary>
        public static List<FlowFolder> Group(IReadOnlyList<FlowEntry> flows)
        {
            var folders = new List<FlowFolder>();
            var byPath = new Dictionary<string, FlowFolder>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < flows.Count; i++)
            {
                if (!byPath.TryGetValue(flows[i].Folder, out var folder))
                {
                    folder = new FlowFolder { Path = flows[i].Folder };
                    byPath.Add(folder.Path, folder);
                    folders.Add(folder);
                }

                folder.Flows.Add(flows[i]);
            }

            return folders;
        }

        /// <summary>
        /// The value of the document's own <c>name:</c>, or null when it declares none.
        ///
        /// Only a key at zero indentation counts. A step's <c>name:</c> selector criterion is
        /// indented under the step, and matching that would name the flow after a button.
        /// </summary>
        public static string ReadName(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line[0] == '-')
                    continue;

                if (!line.StartsWith("name:", StringComparison.Ordinal))
                    continue;

                var value = Uncomment(line.Substring("name:".Length)).Trim();
                return value.Length == 0 ? null : Unquote(value);
            }

            return null;
        }

        private static void Collect(string projectRoot, string directory, List<FlowEntry> into)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var file in Directory.EnumerateFiles(directory, Extension, SearchOption.AllDirectories))
            {
                var relative = Relative(projectRoot, file);

                into.Add(new FlowEntry
                {
                    Path = file,
                    RelativePath = relative,
                    Folder = Folder(relative),
                    Name = ReadName(File.ReadLines(file))
                });
            }
        }

        private static string Relative(string projectRoot, string absolute) =>
            absolute.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(projectRoot.Length + 1).Replace('\\', '/')
                : absolute.Replace('\\', '/');

        private static string Folder(string relative)
        {
            var cut = relative.LastIndexOf('/');
            return cut < 0 ? string.Empty : relative.Substring(0, cut);
        }

        /// <summary>A trailing <c>#</c> comment is not part of an unquoted scalar.</summary>
        private static string Uncomment(string value)
        {
            var quoted = false;

            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '"' || value[i] == '\'')
                    quoted = !quoted;
                else if (value[i] == '#' && !quoted && i > 0 && char.IsWhiteSpace(value[i - 1]))
                    return value.Substring(0, i);
            }

            return value;
        }

        private static string Unquote(string value)
        {
            if (value.Length < 2)
                return value;

            var quote = value[0];
            return (quote == '"' || quote == '\'') && value[value.Length - 1] == quote
                ? value.Substring(1, value.Length - 2)
                : value;
        }
    }
}
