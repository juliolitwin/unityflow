using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityFlow.Editor.Core;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// uGUI's navigation graph, read out loud.
    ///
    /// The graph is <c>Selectable.navigation</c>, and every answer here comes from the engine's own
    /// resolution (<c>Selectable.FindSelectableOnUp</c> and friends) rather than from a
    /// reimplementation of it. That matters because those methods already fold in everything a
    /// hand-rolled search would get subtly wrong: <c>Navigation.Mode.Automatic</c>'s directional
    /// scoring, the axis masks of Horizontal and Vertical, <c>wrapAround</c>, and the interactability
    /// and activeness of the candidate. A second implementation would drift from the one the player
    /// actually experiences, and a navigation report that disagrees with the game is worse than none.
    ///
    /// It never moves the selection. <see cref="TryEstablishInitialFocus"/> is the single deliberate
    /// exception and it refuses once anything is selected — see <see cref="IFocusRing"/>.
    /// </summary>
    internal sealed class UGuiFocusRing : IFocusRing
    {
        private readonly UGuiBackend m_Backend;
        private readonly UGuiEnumerator m_Enumerator;
        private readonly UGuiVisibility m_Visibility;

        // Reused for the default-focus scan. That scan runs once per navigateTo, never per frame,
        // but the buffer costs nothing to keep and matches how every other list here is owned.
        private readonly List<Selectable> m_SelectableBuffer = new List<Selectable>(64);

        public UGuiFocusRing(UGuiBackend backend, UGuiEnumerator enumerator, UGuiVisibility visibility)
        {
            m_Backend = backend;
            m_Enumerator = enumerator;
            m_Visibility = visibility;
        }

        /// <summary>
        /// Three conditions, reported separately, because each sends the reader somewhere else.
        ///
        /// Play mode is checked first and by itself: EventSystem is not [ExecuteAlways], so in edit
        /// mode <c>EventSystem.current</c> is null even with a perfectly good EventSystem in the open
        /// scene (measured). Reporting that as "nothing is selected" would be true
        /// and would point at the UI instead of at the missing play mode.
        /// </summary>
        public bool IsAvailable(out string reason)
        {
            if (!EditorApplication.isPlaying)
            {
                reason = "uGUI navigation requires play mode: EventSystem is not [ExecuteAlways], so outside play " +
                         "mode EventSystem.current is null even when an EventSystem sits in the open scene, and no " +
                         "InputSystemUIInputModule.Process() runs to turn a key press into a Move";
                return false;
            }

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                reason = "EventSystem.current is null in play mode, so nothing holds or dispatches a selection; " +
                         "every EventSystem in the loaded scenes is disabled (EventSystem.current is assigned from OnEnable)";
                return false;
            }

            if (!eventSystem.isActiveAndEnabled)
            {
                reason = $"EventSystem.current is '{eventSystem.gameObject.name}' but it is not active and enabled, " +
                         "so its Update() never runs and a Move would never be dispatched";
                return false;
            }

            reason = null;
            return true;
        }

        public bool TryGetFocused(out UiHandle handle, out string path)
        {
            handle = UiHandle.None;
            path = null;

            var eventSystem = EventSystem.current;
            var selected = eventSystem == null ? null : eventSystem.currentSelectedGameObject;
            if (selected == null)
                return false;

            // The path is filled in even when the object is not one of this backend's nodes. Naming
            // whatever unexpectedly holds the selection is precisely what a reader needs at that point.
            path = m_Enumerator.GetPath(selected.transform);
            handle = TryIntern(selected, out var interned) ? interned : UiHandle.None;
            return true;
        }

        /// <summary>
        /// Seed the selection, once, when the ring is empty.
        ///
        /// The refusal below is the load-bearing part. <c>SetSelectedGameObject</c> can put the
        /// selection anywhere, instantly, and a navigation test that uses it to reach its target has
        /// proven nothing about whether a keyboard or a controller could — it has proven that C# can
        /// assign a field. So it is legal exactly once, to answer "the keyboard is nowhere", and
        /// illegal the moment there is a selection to move.
        /// </summary>
        public bool TryEstablishInitialFocus(UiHandle handle, out string error)
        {
            if (!IsAvailable(out error))
                return false;

            var eventSystem = EventSystem.current;
            var current = eventSystem.currentSelectedGameObject;
            if (current != null)
            {
                error = $"'{m_Enumerator.GetPath(current.transform)}' is already selected. " +
                        "EventSystem.SetSelectedGameObject is only legitimate to establish the FIRST selection; " +
                        "using it to move an existing one replaces navigation with teleportation and would prove " +
                        "nothing about whether the element is reachable by keyboard or controller";
                return false;
            }

            if (!m_Enumerator.TryGetParts(handle, out var parts, out error))
                return false;

            var path = m_Enumerator.GetPath(parts);

            if (parts.Selectable == null)
            {
                error = $"'{path}' has no Selectable component, and uGUI's EventSystem only keeps a selection on a " +
                        "Selectable — anything else is deselected on the next navigation event";
                return false;
            }

            if (!m_Visibility.IsEnabled(parts, out var enabledReason))
            {
                error = $"'{path}' cannot hold the selection: {enabledReason}";
                return false;
            }

            eventSystem.SetSelectedGameObject(parts.GameObject);
            error = null;
            return true;
        }

        /// <summary>
        /// The Selectable uGUI would land on first: hierarchy order within the surface, skipping
        /// anything that cannot hold a selection.
        ///
        /// Hierarchy order is not an arbitrary choice — <c>GetComponentsInChildren</c> walks
        /// depth-first pre-order, which is uGUI's own draw and traversal order, so this is the same
        /// sequence the author sees in the Hierarchy window and the same one
        /// <c>Navigation.Mode.Automatic</c> scores against.
        /// </summary>
        public bool TryGetDefaultFocus(int surfaceId, out UiHandle handle, out string reason)
        {
            handle = UiHandle.None;

            var canvas = m_Enumerator.GetCanvas(surfaceId);
            if (canvas == null)
            {
                reason = $"surface {surfaceId} is not a known Canvas; call Settle before asking for its default focus";
                return false;
            }

            canvas.gameObject.GetComponentsInChildren(true, m_SelectableBuffer);

            var considered = 0;
            for (var i = 0; i < m_SelectableBuffer.Count; i++)
            {
                var selectable = m_SelectableBuffer[i];
                if (selectable == null || !selectable.isActiveAndEnabled || !selectable.IsInteractable())
                    continue;

                considered++;

                // A Selectable wired to Navigation.Mode.None is a dead end by construction: seeding
                // the selection there could never move anywhere, so it is not a starting point.
                if (selectable.navigation.mode == Navigation.Mode.None)
                    continue;

                if (!TryIntern(selectable.gameObject, out handle))
                    continue;

                reason = null;
                return true;
            }

            reason = considered == 0
                ? $"Canvas '{canvas.name}' contains no interactable Selectable at all, so its keyboard navigation " +
                  "graph is empty and no element on it can ever be reached without a pointer"
                : $"every one of the {considered} interactable Selectables under Canvas '{canvas.name}' has " +
                  "navigation.mode = None, so none of them can start a navigation path";
            return false;
        }

        public bool TryDescribeLink(UiHandle handle, FocusDirection direction, out FocusLink link, out string reason)
        {
            link = default;

            if (!m_Enumerator.TryGetParts(handle, out var parts, out reason))
                return false;

            var selectable = parts.Selectable;
            if (selectable == null)
            {
                reason = $"'{m_Enumerator.GetPath(parts)}' has no Selectable component; uGUI's navigation graph is " +
                         "built entirely out of Selectables, so this element can never hold or receive the selection " +
                         "and is unreachable by keyboard or controller";
                return false;
            }

            var navigation = selectable.navigation;
            var mode = navigation.mode.ToString();

            // The engine's own resolution, never a reimplementation: it already applies the mode's
            // axis mask, the explicit override, wrapAround, and each candidate's interactability.
            var found = FindNext(selectable, direction);
            if (found != null)
            {
                // The handle can legitimately be None here. uGUI's automatic navigation searches
                // Selectable.allSelectablesArray, which is GLOBAL and owes nothing to the canvas
                // tree: measured in a real project, a 'Back' button navigated down onto a pooled
                // message-box button that had no Canvas ancestor at all. The link is real
                // and the press really does move the selection there, so reporting it as a dead end
                // would be a lie; the path is filled in either way and is what a report needs.
                link = FocusLink.To(
                    TryIntern(found.gameObject, out var target) ? target : UiHandle.None,
                    m_Enumerator.GetPath(found.transform),
                    mode);
                reason = null;
                return true;
            }

            link = FocusLink.Dead(mode, DescribeMissing(navigation, direction));
            reason = null;
            return true;
        }

        // ---- internals ---------------------------------------------------------------------

        private static Selectable FindNext(Selectable selectable, FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up: return selectable.FindSelectableOnUp();
                case FocusDirection.Down: return selectable.FindSelectableOnDown();
                case FocusDirection.Left: return selectable.FindSelectableOnLeft();
                default: return selectable.FindSelectableOnRight();
            }
        }

        /// <summary>
        /// Name the wiring that is missing, in the author's own vocabulary, so the report says what
        /// to go and fix rather than that a key press did nothing.
        ///
        /// The cases are genuinely different bugs: an Explicit link left empty is a forgotten field
        /// in the Inspector, mode None is an element deliberately taken out of the graph, an axis
        /// mode is a graph that only travels one way, and an Automatic miss means the geometry
        /// search found no interactable Selectable on that side at all.
        /// </summary>
        private static string DescribeMissing(Navigation navigation, FocusDirection direction)
        {
            var field = FieldName(direction);

            if (navigation.mode == Navigation.Mode.None)
                return "navigation.mode = None, so it is out of the navigation graph entirely";

            if (navigation.mode == Navigation.Mode.Explicit)
            {
                var explicitTarget = ExplicitTarget(navigation, direction);
                if (explicitTarget == null)
                    return $"{field} = None";

                // Wired, but the engine still refused it: the object is there and cannot take the
                // selection. Saying "= None" here would send the reader to a field that is filled in.
                return $"{field} = '{explicitTarget.name}' but it is not interactable or not active, " +
                       "so the engine skips it";
            }

            if (navigation.mode == Navigation.Mode.Horizontal &&
                (direction == FocusDirection.Up || direction == FocusDirection.Down))
            {
                return "navigation.mode = Horizontal, which never travels vertically";
            }

            if (navigation.mode == Navigation.Mode.Vertical &&
                (direction == FocusDirection.Left || direction == FocusDirection.Right))
            {
                return "navigation.mode = Vertical, which never travels horizontally";
            }

            return $"navigation.mode = {navigation.mode} found no interactable Selectable on that side";
        }

        private static string FieldName(FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up: return "selectOnUp";
                case FocusDirection.Down: return "selectOnDown";
                case FocusDirection.Left: return "selectOnLeft";
                default: return "selectOnRight";
            }
        }

        private static Selectable ExplicitTarget(Navigation navigation, FocusDirection direction)
        {
            switch (direction)
            {
                case FocusDirection.Up: return navigation.selectOnUp;
                case FocusDirection.Down: return navigation.selectOnDown;
                case FocusDirection.Left: return navigation.selectOnLeft;
                default: return navigation.selectOnRight;
            }
        }

        /// <summary>
        /// Mint this backend's handle for an arbitrary GameObject the ring pointed at. Fails rather
        /// than inventing a handle when the object is not a uGUI node under a known Canvas — a
        /// fabricated handle would resolve to nothing later and turn a precise answer into a stale one.
        /// </summary>
        private bool TryIntern(GameObject gameObject, out UiHandle handle)
        {
            handle = UiHandle.None;

            if (!(gameObject.transform is RectTransform rectTransform))
                return false;

            var owningCanvas = UGuiRectMath.FindOwningCanvas(rectTransform);
            if (owningCanvas == null)
                return false;

            if (!m_Enumerator.TryGetParts(rectTransform, owningCanvas, out var parts))
                return false;

            handle = m_Enumerator.Intern(m_Backend.BackendIndex, parts);
            return true;
        }
    }
}
