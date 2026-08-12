using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityFlow.Editor.Runner;

namespace UnityFlow.Editor.PlayMode
{
    /// <summary>
    /// The only place UnityFlow is allowed to change the editor's play mode.
    ///
    /// It exists because <c>EditorApplication.isPlaying = true</c> is a REQUEST, not a result. The
    /// pipeline package's own <c>editor_play</c> sets that flag and returns in the same tick without
    /// checking anything, so when the project has a compiler error Unity silently refuses and the
    /// caller is told the editor is playing when it is not. Everything here is built around not
    /// having that bug: preflight before asking, and confirm afterwards by observing
    /// <see cref="EditorApplication.isPlaying"/> rather than by trusting the call.
    ///
    /// It also owns the scene the run opened, because entering play mode is only useful once the
    /// right scene is loaded, and a project whose <c>EditorBuildSettings.scenes</c> is empty enters
    /// play mode on whatever happens to be open in the editor.
    /// </summary>
    public static class PlayModeGate
    {
        /// <summary>
        /// Ceiling for a play mode transition when the step does not write <c>timeout:</c>.
        ///
        /// The runner's 7s per-step default is meaningless here: a transition costs a full domain
        /// reload plus a scene load, and a real project takes tens of seconds. A step that wants a
        /// tighter bound says so with <c>timeout:</c>.
        /// </summary>
        public static readonly TimeSpan DefaultTransitionTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Frames a transition may stay invisible before the request is called refused.
        ///
        /// Unity does not document whether <see cref="EditorApplication.EnterPlaymode"/> raises
        /// <see cref="EditorApplication.isPlayingOrWillChangePlaymode"/> in the same tick. Concluding
        /// refusal on the first frame would turn a one-frame delay into a false failure, so the
        /// observation must hold for several frames before it is believed.
        /// </summary>
        public const int AcknowledgementFrames = 10;

        /// <summary>
        /// Unity's own "are there compiler errors" flag — the same one that produces
        /// "All compiler errors have to be fixed before you can enter playmode!".
        ///
        /// It is internal, so it is read by reflection ONCE and cached. There is no public
        /// equivalent: CompilationPipeline exposes events during a compile but no state afterwards.
        /// When the member is absent the gate refuses to enter play mode rather than guessing.
        /// </summary>
        private static readonly PropertyInfo s_ScriptCompilationFailed =
            typeof(EditorUtility).GetProperty(
                "scriptCompilationFailed",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>True while the editor is in play mode.</summary>
        public static bool IsPlayModeActive => EditorApplication.isPlaying;

        /// <summary>True while the editor is fully back in edit mode, with no transition pending.</summary>
        public static bool IsEditModeActive =>
            !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>True while a play mode transition has been accepted but has not finished.</summary>
        public static bool IsTransitionInFlight =>
            EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying;

        /// <summary>True while the editor is doing work that a play mode request would collide with.</summary>
        public static bool IsEditorBusy => EditorApplication.isCompiling || EditorApplication.isUpdating;

        /// <summary>
        /// Everything that must be true before asking for play mode. Failing here is far better
        /// than being refused later: the reason is precise and nothing has been changed yet.
        /// </summary>
        public static bool TryPreflight(out string reason)
        {
            if (EditorApplication.isCompiling)
            {
                reason = "the editor is compiling scripts; entering play mode now would either be refused " +
                         "or immediately reloaded again. Wait for the compile to finish";
                return false;
            }

            if (EditorApplication.isUpdating)
            {
                reason = "the editor is importing assets; the scene about to be loaded may not exist yet in its final form";
                return false;
            }

            if (!TryReadCompilationFailed(out var failed, out var probeError))
            {
                reason = probeError;
                return false;
            }

            if (failed)
            {
                reason = "the project has compiler errors. Unity refuses to enter play mode while any exist, and it " +
                         "refuses SILENTLY — the request returns as if it worked. Fix the errors and re-run";
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
            {
                reason = "a play mode transition is already in flight; something outside this run is driving the editor";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Ask for play mode. Returns as soon as the request is issued: the transition itself takes
        /// a domain reload, so confirming it is the caller's job — poll
        /// <see cref="IsPlayModeActive"/> and treat <see cref="AcknowledgementFrames"/> frames with
        /// no <see cref="IsTransitionInFlight"/> as a refusal.
        /// </summary>
        public static bool TryRequestPlayMode(out string error)
        {
            if (EditorApplication.isPlaying)
            {
                error = "the editor is already in play mode";
                return false;
            }

            if (!TryPreflight(out var reason))
            {
                error = reason;
                return false;
            }

            EditorApplication.EnterPlaymode();

            error = null;
            return true;
        }

        /// <summary>
        /// Ask to leave play mode. Unity acknowledges nothing synchronously here either, so the
        /// confirmation is again the caller polling <see cref="IsEditModeActive"/>.
        /// </summary>
        public static bool TryRequestEditMode(out string error)
        {
            if (!EditorApplication.isPlaying)
            {
                error = "the editor is not in play mode";
                return false;
            }

            EditorApplication.ExitPlaymode();

            error = null;
            return true;
        }

        /// <summary>
        /// Open a scene before entering play mode.
        ///
        /// <paramref name="previousScene"/> is what must be put back afterwards: the project-relative
        /// path of the scene that was open, or an empty string when the editor was on an untitled
        /// scene. It is null when nothing was changed, in which case there is nothing to restore.
        /// </summary>
        public static bool TryOpenScene(string scene, out string previousScene, out string error)
        {
            previousScene = null;

            if (!TryResolveScene(scene, out var relative, out error))
                return false;

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var loaded = EditorSceneManager.GetSceneAt(i);
                if (!loaded.isDirty)
                    continue;

                // Discarding someone's unsaved work to run a test is never the right trade, and
                // OpenScene would do exactly that without asking.
                error = $"the open scene '{(string.IsNullOrEmpty(loaded.path) ? loaded.name : loaded.path)}' has unsaved changes. " +
                        "UnityFlow will not discard them: save the scene, or drop the 'scene:' argument and run against what is already open";
                return false;
            }

            var active = EditorSceneManager.GetActiveScene();
            if (EditorSceneManager.sceneCount == 1 &&
                string.Equals(active.path, relative, StringComparison.OrdinalIgnoreCase))
            {
                // Already exactly the requested setup. Reopening would be a pointless reload and
                // would make the run claim it changed something it did not.
                return true;
            }

            previousScene = active.path ?? string.Empty;

            try
            {
                EditorSceneManager.OpenScene(relative, OpenSceneMode.Single);
            }
            catch (Exception ex)
            {
                previousScene = null;
                error = $"opening '{relative}' failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Put back the scene the run replaced. An empty path means the editor was on an untitled
        /// scene, which is restored as a new empty one — its contents were never on disk to begin
        /// with, and TryOpenScene refuses to run at all when anything unsaved is open.
        /// </summary>
        public static bool TryRestoreScene(string scene, out string error)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                error = "the editor is in play mode; the edit-mode scene cannot be changed from here";
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(scene))
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                else
                    EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            }
            catch (Exception ex)
            {
                error = $"restoring '{(string.IsNullOrEmpty(scene) ? "an untitled scene" : scene)}' failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Restore the run's scene if one is pending, and clear the pending flag.
        ///
        /// Returns a note describing what happened, or null when there was nothing to do. A restore
        /// that cannot happen — the editor is still in play mode because the flow never asked to
        /// leave it — is reported, never swallowed: the editor is left in a state the operator did
        /// not choose and has to be told so.
        /// </summary>
        public static string ReleaseScene(FlowResumeState resume)
        {
            if (resume == null || !resume.SceneRestorePending)
                return null;

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "the editor is still in play mode, so the scene this run opened is left loaded; " +
                       $"{Describe(resume.SceneToRestore)} comes back when you exit play mode";
            }

            if (!TryRestoreScene(resume.SceneToRestore, out var error))
                return $"could not restore {Describe(resume.SceneToRestore)}: {error}";

            resume.SceneRestorePending = false;
            return $"restored {Describe(resume.SceneToRestore)}";
        }

        /// <summary>Turn a 'scene:' argument into a project-relative path Unity's scene API accepts.</summary>
        private static bool TryResolveScene(string scene, out string relative, out string error)
        {
            relative = null;

            if (string.IsNullOrWhiteSpace(scene))
            {
                error = "'scene' is empty; write the path of a .unity file, absolute or relative to the project root";
                return false;
            }

            var absolute = Path.GetFullPath(Path.IsPathRooted(scene)
                ? scene
                : Path.Combine(RunPaths.ProjectRoot, scene));

            if (!string.Equals(Path.GetExtension(absolute), ".unity", StringComparison.OrdinalIgnoreCase))
            {
                error = $"'{scene}' is not a .unity scene file";
                return false;
            }

            if (!File.Exists(absolute))
            {
                error = $"there is no scene at {absolute}. A scene is named by path here rather than by build " +
                        "index, so that it works whether or not EditorBuildSettings.scenes lists it";
                return false;
            }

            var root = Path.GetFullPath(RunPaths.ProjectRoot);
            if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{absolute} is outside the project at {root}; Unity can only open scenes that belong to the project";
                return false;
            }

            relative = absolute.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

            error = null;
            return true;
        }

        private static bool TryReadCompilationFailed(out bool failed, out string error)
        {
            failed = false;

            if (s_ScriptCompilationFailed == null)
            {
                error = "UnityEditor.EditorUtility.scriptCompilationFailed is not present in Unity " +
                        UnityEngine.Application.unityVersion +
                        ", so this run cannot tell whether the project compiles. Entering play mode blind is how a " +
                        "flow ends up reporting a timeout when the real answer was a compiler error";
                return false;
            }

            var value = s_ScriptCompilationFailed.GetValue(null);
            if (!(value is bool typed))
            {
                error = $"UnityEditor.EditorUtility.scriptCompilationFailed returned {(value == null ? "null" : value.GetType().Name)}, not a bool; " +
                        "this Unity version reports the compile state differently and the gate will not guess";
                return false;
            }

            failed = typed;
            error = null;
            return true;
        }

        private static string Describe(string scene) =>
            string.IsNullOrEmpty(scene) ? "the untitled scene that was open before the run" : $"'{scene}'";
    }
}
