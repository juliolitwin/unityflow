using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityFlow.Editor.Commands;
using UnityFlow.Editor.Model;
using UnityFlow.Editor.Runner;
using UnityFlow.Editor.Yaml;

namespace UnityFlow.Editor.Window
{
    /// <summary>
    /// Pick a flow, run it, and watch every step resolve.
    ///
    /// The window drives the run IN PROCESS, through the same <see cref="FlowCommands"/> entry
    /// points the CLI uses, and it reads progress from the run's own <c>progress.ndjson</c>. There
    /// is deliberately no second channel: the runner already flushes one line per step transition
    /// so that a host can tail it, and a window that asked the runner directly would be a second
    /// account of the same run that could disagree with the report.
    ///
    /// UI Toolkit rather than IMGUI for one concrete reason: the step list is a retained tree whose
    /// rows are built once and then have two labels updated, so the marker can pulse every editor
    /// frame without re-laying out the flow. An IMGUI window rebuilds every control on every event.
    /// Every colour and metric lives in FlowRunnerWindow.uss, which carries a full palette for each
    /// editor skin — see the comment at the top of that file.
    ///
    /// Everything the window must not forget across a domain reload lives in
    /// <see cref="SessionState"/>, which is what <see cref="FlowResumeState"/> uses and for the same
    /// reason: it survives the reload that entering play mode causes but not an editor restart, so
    /// a run nobody is waiting for is never picked back up.
    /// </summary>
    public sealed class FlowRunnerWindow : EditorWindow
    {
        /// <summary>Flow the user last selected, so the window comes back pointing at it.</summary>
        private const string SelectedFlowKey = "UnityFlow.Window.Flow";

        /// <summary>Run the window is watching. Set while it is in flight, erased when it is not.</summary>
        private const string AttachedRunKey = "UnityFlow.Window.RunId";

        /// <summary>Flow that run was started from, so the step list can be rebuilt after a reload.</summary>
        private const string AttachedFlowKey = "UnityFlow.Window.RunFlow";

        /// <summary>Set while a domain unloads under a run that cannot survive it. See <see cref="Reattach"/>.</summary>
        private const string AbandonedRunKey = "UnityFlow.Window.Abandoned";

        /// <summary>Prefix of the per-folder collapsed flag of the picker.</summary>
        private const string FolderKeyPrefix = "UnityFlow.Window.Folder.";

        /// <summary>Verbs that move the run rather than drive or check the game, drawn back.</summary>
        private static readonly HashSet<string> LifecycleVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "enterPlayMode", "exitPlayMode", "runFlow", "wait", "screenshot"
        };

        private readonly List<StepRow> m_Rows = new List<StepRow>();
        private readonly List<FlowStepState> m_Rendered = new List<FlowStepState>();

        private string m_Root;
        private string m_Selected;
        private string m_Filter = string.Empty;
        private bool m_ProSkin;

        private List<FlowEntry> m_Flows = new List<FlowEntry>();

        private VisualElement m_Shell;
        private ScrollView m_Picker;
        private ToolbarButton m_RunButton;
        private ToolbarButton m_StopButton;
        /// <summary>
        /// Step-list width under which the fixed verb and duration columns are released.
        /// Measured: at a 580px window those columns reserve ~204px and the argument collapses to
        /// "assert Pla...true", which is the very failure the row layout exists to prevent.
        /// </summary>
        private const float NarrowStepsWidth = 420f;

        private ToolbarButton m_FolderButton;
        private VisualElement m_RailFill;
        private VisualElement m_Header;
        private ScrollView m_Steps;
        private VisualElement m_Empty;
        private Font m_Mono;
        private Texture2D m_Thumbnail;

        private FlowRunProgress m_Progress;
        private RunPaths m_Paths;
        private long m_Offset;
        private int m_RecordsRendered = -1;
        private bool m_CancelRequested;
        private string m_Refusal;

        [MenuItem("Window/UnityFlow")]
        private static void Open() => GetWindow<FlowRunnerWindow>();

        private void CreateGUI()
        {
            // Set here rather than passed to GetWindow, so the tab reads the same however the window
            // was opened — including when the editor recreates it after a domain reload.
            titleContent = new GUIContent("UnityFlow");
            minSize = new Vector2(560, 260);

            m_Root = RunPaths.ProjectRoot;
            m_Selected = SessionState.GetString(SelectedFlowKey, string.Empty);
            m_Mono = EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font;

            BuildUi();
            Discover();
            Reattach();

            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            ReleaseThumbnail();
        }

        /// <summary>
        /// Record that the run being watched will not survive the reload that is about to happen.
        ///
        /// Only a run with a resume ledger comes back, and the ledger publishes its run id to the
        /// session — so a run that is not the pending one dies here, together with the FlowDriver
        /// pumping it. Nothing will ever append its verdict to the progress stream, and without
        /// this the rebuilt window would replay a stream whose last word is a step.start and leave
        /// a marker pulsing on a step that no longer exists.
        /// </summary>
        private void OnBeforeAssemblyReload()
        {
            if (m_Progress != null && !m_Progress.IsTerminal &&
                !string.Equals(FlowResumeState.PendingRunId(), m_Paths.RunId, StringComparison.Ordinal))
            {
                SessionState.SetBool(AbandonedRunKey, true);
            }
        }

        // ------------------------------------------------------------------ chrome

        private void BuildUi()
        {
            m_Shell = new VisualElement();
            m_Shell.AddToClassList("uf-root");
            m_Shell.styleSheets.Add(Style());
            rootVisualElement.Add(m_Shell);

            ApplySkin();

            m_Shell.Add(BuildToolbar());
            m_Shell.Add(BuildRail());

            // 230, not 300: index 0 is the FIXED pane, so every pixel the window loses is charged
            // to the report. The picker only ever shows a flow name, which fits well inside this.
            var split = new TwoPaneSplitView(0, 230, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1;
            m_Shell.Add(split);

            m_Picker = new ScrollView();
            m_Picker.AddToClassList("uf-picker");
            split.Add(m_Picker);

            var report = new VisualElement();
            report.AddToClassList("uf-report");
            split.Add(report);

            m_Header = new VisualElement();
            m_Header.AddToClassList("uf-header");
            report.Add(m_Header);

            m_Steps = new ScrollView();
            m_Steps.AddToClassList("uf-steps");

            // The argument is the only part of a step row that can yield width, so below this the
            // fixed verb and duration columns are released rather than letting "assert Pla...true"
            // reappear — which is the exact failure the row layout exists to prevent.
            m_Steps.RegisterCallback<GeometryChangedEvent>(evt =>
                m_Steps.EnableInClassList("uf-steps--narrow", evt.newRect.width < NarrowStepsWidth));

            report.Add(m_Steps);

            m_Empty = new VisualElement();
            m_Empty.AddToClassList("uf-empty");
            report.Add(m_Empty);

            RefreshControls();
        }

        private Toolbar BuildToolbar()
        {
            var toolbar = new Toolbar();

            m_RunButton = IconButton("PlayButton", "Run", "Run the selected flow", RunSelected);
            m_StopButton = IconButton("StopButton", "Stop", "Ask the run to stop at its next step boundary", StopRun);
            m_FolderButton = IconButton("Folder Icon", "Run folder", "Reveal this run's artifacts and logs", OpenRunFolder);
            var refresh = IconButton("Refresh", string.Empty, "Rescan the project for flows", Discover);

            toolbar.Add(m_RunButton);
            toolbar.Add(m_StopButton);
            toolbar.Add(Separator());
            toolbar.Add(m_FolderButton);
            toolbar.Add(refresh);

            var gap = new VisualElement();
            gap.AddToClassList("uf-toolbar-gap");
            toolbar.Add(gap);

            var search = new ToolbarSearchField { value = m_Filter, tooltip = "Filter by flow name or path" };
            search.AddToClassList("uf-toolbar-search");
            search.RegisterValueChangedCallback(e =>
            {
                m_Filter = e.newValue;
                BuildPicker();
            });
            toolbar.Add(search);

            return toolbar;
        }

        private VisualElement BuildRail()
        {
            var rail = new VisualElement();
            rail.AddToClassList("uf-rail");

            m_RailFill = new VisualElement();
            m_RailFill.AddToClassList("uf-rail__fill");
            rail.Add(m_RailFill);

            return rail;
        }

        private static VisualElement Separator()
        {
            var separator = new VisualElement();
            separator.style.width = 1;
            separator.style.marginLeft = 3;
            separator.style.marginRight = 3;
            separator.style.marginTop = 2;
            separator.style.marginBottom = 2;
            separator.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.25f));
            return separator;
        }

        private static ToolbarButton IconButton(string icon, string text, string tooltip, Action action)
        {
            var button = new ToolbarButton(action) { tooltip = tooltip };
            button.style.flexDirection = FlexDirection.Row;
            button.style.alignItems = Align.Center;

            var image = new Image { image = EditorGUIUtility.IconContent(icon).image, scaleMode = ScaleMode.ScaleToFit };
            image.style.width = 14;
            image.style.height = 14;
            button.Add(image);

            if (text.Length > 0)
            {
                var label = new Label(text);
                label.style.marginLeft = 4;
                button.Add(label);
            }

            return button;
        }

        /// <summary>
        /// The stylesheet, found through the package this assembly belongs to rather than by a
        /// literal path, so the window is styled the same whether the package is embedded in a
        /// project or installed from a registry.
        /// </summary>
        private static StyleSheet Style()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FlowRunnerWindow).Assembly);
            var path = package.assetPath + "/Editor/Window/FlowRunnerWindow.uss";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);

            if (sheet == null)
                throw new InvalidOperationException($"UnityFlow's window stylesheet is missing from {path}.");

            return sheet;
        }

        /// <summary>
        /// Which of the two palettes applies. The editor offers no event for a theme change, so it
        /// is re-read on the tick the window already runs for its pulsing marker.
        /// </summary>
        private void ApplySkin()
        {
            m_ProSkin = EditorGUIUtility.isProSkin;
            m_Shell.EnableInClassList("uf-dark", m_ProSkin);
            m_Shell.EnableInClassList("uf-light", !m_ProSkin);
        }

        // ------------------------------------------------------------------ flow picker

        private void Discover()
        {
            m_Flows = FlowCatalog.Discover(m_Root);
            BuildPicker();
        }

        /// <summary>
        /// The picker, rebuilt whenever the set of visible flows changes.
        ///
        /// A plain tree of elements rather than a ListView because the rows are not uniform — a
        /// folder header is not a flow — and because a project has tens of flows, not thousands.
        /// </summary>
        private void BuildPicker()
        {
            m_Picker.Clear();

            var matches = Filter();

            foreach (var folder in FlowCatalog.Group(matches))
            {
                // A search has already narrowed the list to what the user asked for, so hiding half
                // of it behind a collapsed folder would answer the search with nothing.
                var expanded = m_Filter.Length > 0 || IsExpanded(folder.Path);

                var body = new VisualElement();
                m_Picker.Add(FolderHeader(folder, body, expanded));
                m_Picker.Add(body);

                body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;

                foreach (var flow in folder.Flows)
                    body.Add(FlowRow(flow));
            }
        }

        private List<FlowEntry> Filter()
        {
            if (m_Filter.Length == 0)
                return m_Flows;

            var matches = new List<FlowEntry>();

            for (var i = 0; i < m_Flows.Count; i++)
            {
                if (m_Flows[i].Title.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m_Flows[i].RelativePath.IndexOf(m_Filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matches.Add(m_Flows[i]);
                }
            }

            return matches;
        }

        private VisualElement FolderHeader(FlowFolder folder, VisualElement body, bool expanded)
        {
            var header = new VisualElement { tooltip = folder.Path };
            header.AddToClassList("uf-folder__header");

            var twisty = new Label(expanded ? "▼" : "►");
            twisty.AddToClassList("uf-folder__twisty");
            header.Add(twisty);

            var name = new Label(folder.Path.Length == 0 ? "/" : folder.Path);
            name.AddToClassList("uf-folder__name");
            header.Add(name);

            var count = new Label(folder.Flows.Count.ToString(CultureInfo.InvariantCulture));
            count.AddToClassList("uf-folder__count");
            header.Add(count);

            header.RegisterCallback<ClickEvent>(_ =>
            {
                var open = body.style.display == DisplayStyle.None;
                body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
                twisty.text = open ? "▼" : "►";
                SessionState.SetBool(FolderKeyPrefix + folder.Path, open);
            });

            return header;
        }

        private VisualElement FlowRow(FlowEntry flow)
        {
            var row = new VisualElement { tooltip = flow.RelativePath, userData = flow.Path };
            row.AddToClassList("uf-flow");
            row.EnableInClassList("uf-flow--selected", string.Equals(flow.Path, m_Selected, StringComparison.Ordinal));

            var title = new Label(flow.Title);
            title.AddToClassList("uf-flow__name");
            title.EnableInClassList("uf-flow__name--unnamed", flow.Name == null);
            row.Add(title);

            // The folder is already the group header above this row, so repeating it here would
            // spend the whole line on characters every sibling shares. What is left is the part
            // that differs, and it is still elided from the left in case the file name is long.
            var path = new Label(flow.RelativePath.Substring(flow.Folder.Length == 0 ? 0 : flow.Folder.Length + 1));
            path.AddToClassList("uf-flow__path");
            row.Add(path);

            if (flow.Name == null)
                title.tooltip = "This file declares no top-level 'name:', so it will not parse.";

            row.RegisterCallback<ClickEvent>(e =>
            {
                Select(flow.Path);

                if (e.clickCount == 2 && m_RunButton.enabledSelf)
                    RunSelected();
            });

            return row;
        }

        private void Select(string path)
        {
            m_Selected = path;
            m_Refusal = null;
            SessionState.SetString(SelectedFlowKey, m_Selected);

            m_Picker.Query<VisualElement>(className: "uf-flow").ForEach(row =>
                row.EnableInClassList("uf-flow--selected", string.Equals((string)row.userData, path, StringComparison.Ordinal)));

            RefreshControls();
            RefreshEmpty();
        }

        private bool IsExpanded(string folder) => SessionState.GetBool(FolderKeyPrefix + folder, true);

        /// <summary>The catalog entry for the selected path, or null when nothing usable is selected.</summary>
        private FlowEntry Selected()
        {
            for (var i = 0; i < m_Flows.Count; i++)
            {
                if (string.Equals(m_Flows[i].Path, m_Selected, StringComparison.Ordinal))
                    return m_Flows[i];
            }

            return null;
        }

        // ------------------------------------------------------------------ running

        private void RunSelected()
        {
            FlowDocument document;

            try
            {
                // Parsed here as well as inside the command, because the window needs the step list
                // BEFORE anything runs — that is what makes the pending steps visible — and because
                // the same document is what decides which of the two commands this run can use.
                document = new FlowParser().ParseFile(m_Selected, new FlowVocabulary());
            }
            catch (FlowParseException ex)
            {
                ShowRefusal(ex.Message);
                return;
            }

            var runId = FlowCommands.NewRunId();

            if (ReloadsTheDomain(document))
            {
                if (FlowCommands.Start(m_Selected, runId) is FlowRunResult refused)
                {
                    ShowRefusal(refused.Failure);
                    return;
                }
            }
            else
            {
                var run = FlowCommands.Run(m_Selected, runId);

                // The task can only be finished already when the command refused the run outright:
                // a run that starts is pumped by FlowDriver from the editor loop, never inside the
                // call that registered it.
                if (run.IsCompleted)
                {
                    ShowRefusal(run.Result.Failure);
                    return;
                }
            }

            Attach(runId, document);
        }

        /// <summary>
        /// Whether the flow contains a step that reloads the domain, which is what decides
        /// <c>flow.start</c> over <c>flow.run</c>.
        ///
        /// Exactly two verbs do, and both REFUSE to execute without a resume ledger — StepLibrary
        /// fails them with "needs a run that can survive a domain reload, and this one cannot".
        /// <c>flow.start</c> writes that ledger and FlowResumer rebuilds the run on the far side;
        /// <c>flow.run</c> cannot, so choosing it here would fail the run at its first play mode
        /// step. Nothing else in the vocabulary reloads the domain, so nothing else needs the
        /// slower path — a run started with <c>flow.start</c> occupies the editor's single resume
        /// slot and refuses every other run until it ends.
        /// </summary>
        private static bool ReloadsTheDomain(FlowDocument document) =>
            HasPlayModeStep(document.Before) ||
            HasPlayModeStep(document.Steps) ||
            HasPlayModeStep(document.After);

        private static bool HasPlayModeStep(IReadOnlyList<FlowStep> steps)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Verb == "enterPlayMode" || steps[i].Verb == "exitPlayMode")
                    return true;
            }

            return false;
        }

        private void Attach(string runId, FlowDocument document)
        {
            m_Paths = RunPaths.Existing(runId);
            m_Progress = new FlowRunProgress(document);
            m_Offset = 0;
            m_RecordsRendered = -1;
            m_CancelRequested = false;
            m_Refusal = null;

            SessionState.SetString(AttachedRunKey, runId);
            SessionState.SetString(AttachedFlowKey, document.SourcePath);
            SessionState.EraseBool(AbandonedRunKey);

            BuildStepRows();
            Pump();
            Refresh();
        }

        /// <summary>
        /// Pick the in-flight run back up after a domain reload.
        ///
        /// Entering play mode rebuilds this window from nothing while <see cref="FlowResumer"/>
        /// rebuilds the run itself, and the two meet at the run folder: the flow is re-parsed and
        /// the whole progress stream replayed from its first line, so the list comes back with
        /// every step already resolved and the marker on whatever the resumed segment is doing.
        /// </summary>
        private void Reattach()
        {
            var runId = SessionState.GetString(AttachedRunKey, string.Empty);
            var flowPath = SessionState.GetString(AttachedFlowKey, string.Empty);

            if (runId.Length == 0 || flowPath.Length == 0)
            {
                RefreshEmpty();
                return;
            }

            var paths = RunPaths.Existing(runId);

            if (!File.Exists(paths.ProgressFile) || !File.Exists(flowPath))
            {
                Forget();
                RefreshEmpty();
                return;
            }

            FlowDocument document;

            try
            {
                // The window starts runs with the flow's own env and no overrides, so re-parsing the
                // file reproduces exactly the step list the run is executing.
                document = new FlowParser().ParseFile(flowPath, new FlowVocabulary());
            }
            catch (FlowParseException ex)
            {
                ShowRefusal($"{flowPath} no longer parses, so run '{runId}' cannot be shown: {ex.Message}");
                return;
            }

            m_Paths = paths;
            m_Progress = new FlowRunProgress(document);
            m_Offset = 0;
            m_RecordsRendered = -1;
            m_CancelRequested = false;
            m_Refusal = null;

            BuildStepRows();
            Pump();

            // Checked after the whole stream is replayed: the reload can land between the run's own
            // run.end record and the tick that would have read it, and a finished run is not lost.
            if (SessionState.GetBool(AbandonedRunKey, false))
            {
                SessionState.EraseBool(AbandonedRunKey);

                if (!m_Progress.IsTerminal)
                    m_Progress.Abandon();
            }

            Refresh();
        }

        private void StopRun()
        {
            // The cancel mechanism is a sentinel FILE the runner polls at every step boundary, and
            // flow.cancel is the one thing that writes it.
            FlowCommands.Cancel(m_Paths.RunId);
            m_CancelRequested = true;

            // The header is only rebuilt when a record arrives, and the runner notices the sentinel
            // at its next step boundary — which can be seconds away.
            m_RecordsRendered = -1;
            Refresh();
        }

        private void OpenRunFolder() => EditorUtility.RevealInFinder(m_Paths.RunDirectory);

        private void Tick()
        {
            if (m_ProSkin != EditorGUIUtility.isProSkin)
                ApplySkin();

            if (m_Progress == null)
                return;

            var arrived = Pump();

            // The marker has to keep pulsing between records; nothing else needs a frame.
            if (arrived || !m_Progress.IsTerminal)
                Refresh();
        }

        /// <summary>
        /// Read whatever the run has appended since the last call.
        ///
        /// Only WHOLE lines are consumed. <see cref="NdjsonWriter"/> flushes per record, but a
        /// record longer than the writer's buffer reaches disk in two pieces, and half a line is
        /// not JSON.
        /// </summary>
        private bool Pump()
        {
            if (!File.Exists(m_Paths.ProgressFile))
                return false;

            byte[] buffer;

            // FileShare.ReadWrite because the runner is writing this file right now.
            using (var stream = new FileStream(m_Paths.ProgressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var available = stream.Length - m_Offset;
                if (available <= 0)
                    return false;

                stream.Seek(m_Offset, SeekOrigin.Begin);
                buffer = new byte[available];

                var read = stream.Read(buffer, 0, buffer.Length);
                if (read < buffer.Length)
                    Array.Resize(ref buffer, read);
            }

            var lastBreak = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastBreak < 0)
                return false;

            // Advance past the complete lines BEFORE applying them: a record this window cannot
            // read must be reported once, not re-thrown on every editor frame.
            m_Offset += lastBreak + 1;

            foreach (var line in Encoding.UTF8.GetString(buffer, 0, lastBreak + 1).Split('\n'))
                m_Progress.Apply(line);

            return true;
        }

        private void Refresh()
        {
            // Rows are the only thing that changes without a record, because of the pulsing marker.
            if (m_RecordsRendered != m_Progress.RecordsApplied)
            {
                m_RecordsRendered = m_Progress.RecordsApplied;
                RefreshHeader();
                RefreshFailure();
                RefreshControls();
                RefreshEmpty();
            }

            RefreshRows();
            RefreshRail();
        }

        // ------------------------------------------------------------------ step list

        /// <summary>
        /// One row per step, built once. The step list cannot change during a run — the resumer
        /// refuses to continue into an edited flow — so only the marker, the elapsed time and the
        /// state class are touched afterwards.
        /// </summary>
        private void BuildStepRows()
        {
            m_Steps.Clear();
            m_Rows.Clear();
            m_Rendered.Clear();

            string group = null;
            string section = null;

            foreach (var step in m_Progress.Steps)
            {
                // Which section a step is in is why it ran: 'after' executes even when the body
                // failed, so a teardown step passing under a failed run is not a contradiction.
                if (!string.Equals(step.Section, section, StringComparison.Ordinal))
                    m_Steps.Add(SectionHeader(step.Section));

                section = step.Section;

                // runFlow splices a sub-flow's steps into one flat list. Naming its file on every
                // row repeats the same string down the whole block; naming it once, above the
                // block, says the same thing and leaves the rows to say what differs.
                if (step.SourceFile != null && !string.Equals(step.SourceFile, group, StringComparison.Ordinal))
                    m_Steps.Add(SubFlowHeader(step.SourceFile));

                group = step.SourceFile;

                var row = BuildStepRow(step);
                m_Rows.Add(row);

                // Rows are built showing Pending, so that is what has been rendered. Anything the
                // replayed stream already said is applied by the RefreshRows that follows.
                m_Rendered.Add(FlowStepState.Pending);
                m_Steps.Add(row.Line);
            }
        }

        private static VisualElement SectionHeader(string section)
        {
            var header = new Label(section.ToUpperInvariant());
            header.AddToClassList("uf-section");
            return header;
        }

        private VisualElement SubFlowHeader(string file)
        {
            var header = new VisualElement { tooltip = file };
            header.AddToClassList("uf-subflow");

            var label = new Label("FROM");
            label.AddToClassList("uf-subflow__label");
            header.Add(label);

            var path = new Label(Relative(file));
            path.AddToClassList("uf-subflow__file");
            header.Add(path);

            return header;
        }

        private StepRow BuildStepRow(FlowStepProgress step)
        {
            var row = new StepRow();

            row.Line = new VisualElement
            {
                tooltip = $"{step.Section}[{step.Index}]  {Relative(step.SourceFile ?? m_Progress.FlowPath)}:{step.Line}"
            };
            row.Line.AddToClassList("uf-step");
            row.Line.AddToClassList("uf-step--pending");
            row.Line.EnableInClassList("uf-step--nested", step.SourceFile != null);

            row.Gutter = new Label(Glyph(FlowStepState.Pending));
            row.Gutter.AddToClassList("uf-step__gutter");
            row.Line.Add(row.Gutter);

            var verb = new Label(step.Verb);
            verb.AddToClassList("uf-step__verb");
            verb.EnableInClassList("uf-step__verb--check", step.Verb.StartsWith("assert", StringComparison.Ordinal) ||
                                                          step.Verb.StartsWith("waitFor", StringComparison.Ordinal) ||
                                                          step.Verb.StartsWith("waitUntil", StringComparison.Ordinal));
            verb.EnableInClassList("uf-step__verb--lifecycle", LifecycleVerbs.Contains(step.Verb));
            row.Line.Add(verb);

            var argument = new Label(step.Argument) { tooltip = step.Argument };
            argument.AddToClassList("uf-step__argument");
            row.Line.Add(argument);

            row.Note = new Label();
            row.Note.AddToClassList("uf-step__note");
            row.Line.Add(row.Note);

            row.Duration = new Label();
            row.Duration.AddToClassList("uf-step__duration");
            row.Line.Add(row.Duration);

            return row;
        }

        private void RefreshRows()
        {
            // A marker that only changed colour would say nothing to a reader who cannot see the
            // difference, so the running step pulses as well as being blue.
            var pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin((float)EditorApplication.timeSinceStartup * 3f));

            for (var i = 0; i < m_Rows.Count; i++)
            {
                var step = m_Progress.Steps[i];
                var row = m_Rows[i];

                if (step.State == FlowStepState.Running)
                    row.Gutter.style.opacity = pulse;

                if (m_Rendered[i] == step.State)
                    continue;

                m_Rendered[i] = step.State;
                row.Gutter.style.opacity = step.State == FlowStepState.Running ? pulse : 1f;
                row.Gutter.text = Glyph(step.State);

                row.Line.EnableInClassList("uf-step--pending", step.State == FlowStepState.Pending);
                row.Line.EnableInClassList("uf-step--running", step.State == FlowStepState.Running);
                row.Line.EnableInClassList("uf-step--passed", step.State == FlowStepState.Passed);
                row.Line.EnableInClassList("uf-step--failed", step.State == FlowStepState.Failed);
                row.Line.EnableInClassList("uf-step--stopped", step.State == FlowStepState.Interrupted);

                row.Duration.text = step.ElapsedMs >= 0 ? Elapsed(step.ElapsedMs) : string.Empty;

                // What a step DID without input changes what its pass is worth, so it is marked on
                // the row itself and spelled out in the tooltip.
                row.Note.text = step.Notes.Count == 0 ? string.Empty : "assisted";
                row.Note.tooltip = step.Notes.Count == 0 ? null : string.Join("\n", step.Notes);
            }
        }

        private static string Glyph(FlowStepState state)
        {
            switch (state)
            {
                case FlowStepState.Pending:
                    return "·";
                case FlowStepState.Running:
                    return "▶";
                case FlowStepState.Passed:
                    return "✓";
                case FlowStepState.Failed:
                    return "✗";
                case FlowStepState.Interrupted:
                    return "■";
                default:
                    throw new InvalidOperationException($"Unhandled step state '{state}'.");
            }
        }

        /// <summary>
        /// A step's duration in a column 62px wide. Milliseconds below a second, seconds below a
        /// minute, minutes and seconds above one — so the number never grows past the column and
        /// is never shown cut in half.
        /// </summary>
        private static string Elapsed(int milliseconds)
        {
            if (milliseconds < 1000)
                return milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

            if (milliseconds < 60000)
                return (milliseconds / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " s";

            return (milliseconds / 60000) + ":" + (milliseconds / 1000 % 60).ToString("00", CultureInfo.InvariantCulture) + " m";
        }

        // ------------------------------------------------------------------ header

        /// <summary>
        /// What the run is, and what a pass by it would be worth: the write mode it negotiated, the
        /// occlusion fidelity it achieved, and the variables its steps were built with — as chips
        /// rather than a sentence, because none of it is prose and all of it is scanned.
        /// </summary>
        private void RefreshHeader()
        {
            m_Header.Clear();
            m_Header.style.display = DisplayStyle.Flex;

            var top = new VisualElement();
            top.AddToClassList("uf-header__top");
            m_Header.Add(top);

            var pill = new Label(PillText());
            pill.AddToClassList("uf-pill");
            pill.AddToClassList("uf-pill--" + Mood());
            top.Add(pill);

            var flow = new Label(m_Progress.FlowName) { tooltip = Relative(m_Progress.FlowPath) };
            flow.AddToClassList("uf-header__flow");
            top.Add(flow);

            if (m_Progress.DurationSeconds > 0)
            {
                var elapsed = new Label(m_Progress.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                elapsed.AddToClassList("uf-header__elapsed");
                top.Add(elapsed);
            }

            var chips = new VisualElement();
            chips.AddToClassList("uf-chips");
            m_Header.Add(chips);

            chips.Add(Chip("run", m_Paths.RunId));
            chips.Add(Chip("steps", Completed() + " / " + m_Progress.Steps.Count));
            chips.Add(Chip("write", m_Progress.WriteMode ?? "—"));
            chips.Add(Chip("occlusion", m_Progress.Occlusion ?? "—"));
            chips.Add(Chip("input", m_Progress.InputDriver ?? "—"));

            if (m_Progress.PlayMode)
                chips.Add(Chip("mode", "play"));

            if (m_Progress.Resumed)
                chips.Add(Chip("resumed", "domain reload"));

            foreach (var variable in m_Progress.Env)
            {
                var split = variable.IndexOf('=');
                chips.Add(split < 0 ? Chip("env", variable) : Chip(variable.Substring(0, split), variable.Substring(split + 1)));
            }

            // Amber, not red: a warning does not fail the run, it lowers what passing it proves.
            foreach (var warning in m_Progress.Warnings)
                m_Header.Add(Note("! " + warning, true));

            foreach (var note in m_Progress.Notes)
                m_Header.Add(Note(note, false));
        }

        private string PillText()
        {
            if (m_CancelRequested && !m_Progress.IsTerminal)
                return "STOPPING";

            return m_Progress.State.ToString().ToUpperInvariant();
        }

        /// <summary>The three things a reader needs the colour to say, which is fewer than the run has states.</summary>
        private string Mood()
        {
            switch (m_Progress.State)
            {
                case RunState.Passed:
                    return "passed";
                case RunState.Failed:
                case RunState.Errored:
                    return "failed";
                case RunState.Cancelled:
                    return "stopped";
                default:
                    return m_CancelRequested ? "stopped" : "running";
            }
        }

        private int Completed()
        {
            var done = 0;

            for (var i = 0; i < m_Progress.Steps.Count; i++)
            {
                if (m_Progress.Steps[i].State != FlowStepState.Pending && m_Progress.Steps[i].State != FlowStepState.Running)
                    done++;
            }

            return done;
        }

        private static VisualElement Chip(string key, string value)
        {
            var chip = new VisualElement { tooltip = key + ": " + value };
            chip.AddToClassList("uf-chip");

            var name = new Label(key);
            name.AddToClassList("uf-chip__key");
            chip.Add(name);

            var text = new Label(value);
            text.AddToClassList("uf-chip__value");
            chip.Add(text);

            return chip;
        }

        private static Label Note(string text, bool warning)
        {
            var label = new Label(text);
            label.AddToClassList("uf-note");
            label.EnableInClassList("uf-note--warning", warning);
            return label;
        }

        private void RefreshRail()
        {
            var total = m_Progress.Steps.Count;

            m_RailFill.style.width = Length.Percent(total == 0 ? 0f : 100f * Completed() / total);
            m_RailFill.EnableInClassList("uf-rail__fill--passed", m_Progress.State == RunState.Passed);
            m_RailFill.EnableInClassList("uf-rail__fill--failed",
                m_Progress.State == RunState.Failed || m_Progress.State == RunState.Errored);
            m_RailFill.EnableInClassList("uf-rail__fill--stopped", m_Progress.State == RunState.Cancelled);
        }

        // ------------------------------------------------------------------ failure

        /// <summary>
        /// The diagnosis, inline, directly under the step that produced it — because the question a
        /// failure raises is "what was this step looking at", and an answer parked at the bottom of
        /// the window has to be matched back up to a row by hand.
        /// </summary>
        private void RefreshFailure()
        {
            ReleaseThumbnail();

            var existing = m_Steps.Q<VisualElement>(className: "uf-failure");
            existing?.RemoveFromHierarchy();

            var step = m_Progress.FailedStep;
            if (step == null)
                return;

            var block = new VisualElement();
            block.AddToClassList("uf-failure");

            var summary = new Label(step.FailureSummary ?? m_Progress.FailureSummary);
            summary.AddToClassList("uf-failure__summary");
            block.Add(summary);

            if (step.FailureDetail != null)
            {
                var foldout = new Foldout { text = "What the step was looking at", value = true };
                foldout.AddToClassList("uf-failure__foldout");

                var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
                scroll.AddToClassList("uf-failure__detail");

                var text = new Label(step.FailureDetail.TrimEnd());
                text.AddToClassList("uf-failure__text");

                if (m_Mono != null)
                    text.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(m_Mono));

                scroll.Add(text);
                foldout.Add(scroll);
                block.Add(foldout);
            }

            if (step.Screenshot != null && File.Exists(step.Screenshot))
                block.Add(Screenshot(step.Screenshot));

            m_Rows[step.Index].Line.parent.Insert(m_Rows[step.Index].Line.parent.IndexOf(m_Rows[step.Index].Line) + 1, block);
        }

        private VisualElement Screenshot(string file)
        {
            var shot = new VisualElement();
            shot.AddToClassList("uf-failure__shot");

            m_Thumbnail = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            m_Thumbnail.LoadImage(File.ReadAllBytes(file));

            var thumb = new Image { image = m_Thumbnail, scaleMode = ScaleMode.ScaleToFit };
            thumb.AddToClassList("uf-failure__thumb");
            shot.Add(thumb);

            var open = new Button(() => EditorUtility.RevealInFinder(file)) { text = Relative(file), tooltip = file };
            open.AddToClassList("uf-failure__path");
            shot.Add(open);

            return shot;
        }

        /// <summary>The thumbnail is created from raw bytes, so nothing else will ever collect it.</summary>
        private void ReleaseThumbnail()
        {
            if (m_Thumbnail == null)
                return;

            DestroyImmediate(m_Thumbnail);
            m_Thumbnail = null;
        }

        // ------------------------------------------------------------------ empty state

        private void RefreshEmpty()
        {
            m_Empty.Clear();

            var idle = m_Progress == null;
            m_Empty.style.display = idle ? DisplayStyle.Flex : DisplayStyle.None;
            m_Steps.style.display = idle ? DisplayStyle.None : DisplayStyle.Flex;
            m_Header.style.display = idle ? DisplayStyle.None : DisplayStyle.Flex;

            if (!idle)
                return;

            if (m_Refusal != null)
            {
                var refusal = new Label(m_Refusal);
                refusal.AddToClassList("uf-empty__error");
                m_Empty.Add(refusal);
                return;
            }

            var selected = Selected();

            var title = new Label(selected == null ? "No flow selected" : selected.Title);
            title.AddToClassList("uf-empty__title");
            m_Empty.Add(title);

            if (selected != null)
            {
                var path = new Label(selected.RelativePath);
                path.AddToClassList("uf-empty__path");
                m_Empty.Add(path);
            }

            var hint = new Label(selected == null
                ? "Pick a flow on the left. The list is every *.flow.yaml under Assets/ and Flows/."
                : "Press Run. Every step of the flow is listed here before it executes, so you can see what it will do.");
            hint.AddToClassList("uf-empty__hint");
            m_Empty.Add(hint);
        }

        private void RefreshControls()
        {
            var live = m_Progress != null && !m_Progress.IsTerminal;

            m_RunButton.SetEnabled(!live && m_Selected.Length > 0 && File.Exists(m_Selected));
            m_StopButton.SetEnabled(live && !m_CancelRequested);
            m_FolderButton.SetEnabled(m_Paths != null && Directory.Exists(m_Paths.RunDirectory));
        }

        /// <summary>Report a run that never started, which therefore has no folder to read.</summary>
        private void ShowRefusal(string message)
        {
            ReleaseThumbnail();

            m_Progress = null;
            m_Paths = null;
            m_RecordsRendered = -1;
            m_Refusal = message;

            m_Steps.Clear();
            m_Rows.Clear();
            m_Rendered.Clear();
            m_Header.Clear();
            m_Header.style.display = DisplayStyle.None;
            m_RailFill.style.width = Length.Percent(0);

            Forget();
            RefreshEmpty();
            RefreshControls();
        }

        private static void Forget()
        {
            SessionState.EraseString(AttachedRunKey);
            SessionState.EraseString(AttachedFlowKey);
            SessionState.EraseBool(AbandonedRunKey);
        }

        private string Relative(string absolute) =>
            absolute.StartsWith(m_Root, StringComparison.OrdinalIgnoreCase)
                ? absolute.Substring(m_Root.Length + 1).Replace('\\', '/')
                : absolute;

        /// <summary>The three parts of a step row that a progress record can change.</summary>
        private sealed class StepRow
        {
            public VisualElement Line;
            public Label Gutter;
            public Label Note;
            public Label Duration;
        }
    }
}
