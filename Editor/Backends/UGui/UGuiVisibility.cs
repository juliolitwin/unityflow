using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UnityFlow.Editor.Backends.UGui
{
    /// <summary>
    /// Answers "is it drawn" and "can it be acted on" as two separate questions.
    ///
    /// They are not the same question and collapsing them produces both kinds of wrong test: a
    /// CanvasGroup at alpha 0 with blocksRaycasts on is fully clickable by a real user, and one at
    /// alpha 1 with interactable off must never be tapped no matter how visible it looks.
    ///
    /// No DRAWN element gets a hand-rolled CanvasGroup alpha walk. uGUI already computes the
    /// cumulative product for every drawn element, including the "a disabled CanvasGroup
    /// contributes nothing" rule, and exposes it as <c>CanvasRenderer.GetInheritedAlpha()</c>.
    /// Measured in this editor: alpha 0.5 -&gt; 0.5; alpha 0 -&gt; 0; the same group at alpha 0 but
    /// with the component disabled -&gt; 1. Re-deriving that by hand only creates a second, wrong
    /// answer. A graphic-less container has no CanvasRenderer to ask, so it is the single case that
    /// walks the groups itself — and <see cref="CumulativeGroupAlpha"/> is checked against the
    /// engine's own number for every drawn element in the scene.
    /// </summary>
    internal sealed class UGuiVisibility
    {
        /// <summary>
        /// Anything above this is drawn. The intuitive "alpha &gt; 0.95" threshold is wrong in both
        /// directions: three nested CanvasGroups at 0.98 multiply to 0.941 and would be reported
        /// invisible while plainly on screen.
        /// </summary>
        private const float MinVisibleAlpha = 0.001f;

        /// <summary>Reused by the CanvasGroup walks so the per-node diagnostics allocate nothing.</summary>
        private readonly List<CanvasGroup> m_GroupBuffer = new List<CanvasGroup>(4);

        /// <summary>
        /// Whether the node is actually drawn. <paramref name="visibleRect"/> is the already
        /// mask-and-screen-clipped rect, so a node clipped to nothing is caught here.
        /// </summary>
        public bool IsVisible(in UGuiNodeParts parts, bool hasScreenRect, in Rect visibleRect, out string reason)
        {
            if (!parts.GameObject.activeInHierarchy)
            {
                var inactive = FindInactiveAncestor(parts.GameObject);
                reason = inactive == parts.GameObject
                    ? $"GameObject '{parts.GameObject.name}' is inactive"
                    : $"GameObject '{parts.GameObject.name}' is inactive because ancestor '{inactive.name}' is inactive";
                return false;
            }

            var disabledCanvas = FindDisabledCanvas(parts.RectTransform, parts.RootCanvas);
            if (disabledCanvas != null)
            {
                reason = $"Canvas '{disabledCanvas.name}' is disabled, so nothing beneath it is drawn";
                return false;
            }

            var visual = parts.VisualOwner;
            if (visual != null)
            {
                if (!visual.enabled)
                {
                    reason = $"{visual.GetType().Name} on '{visual.name}' is disabled";
                    return false;
                }

                if (visual.canvasRenderer.cull)
                {
                    reason = $"CanvasRenderer of '{visual.name}' is culled; an ancestor RectMask2D clips it away entirely";
                    return false;
                }

                var inherited = visual.canvasRenderer.GetInheritedAlpha();
                if (inherited <= MinVisibleAlpha)
                {
                    reason = $"cumulative CanvasGroup alpha is {UGuiRectMath.Format(inherited)}";
                    return false;
                }

                var ownAlpha = visual.color.a;
                if (ownAlpha <= MinVisibleAlpha)
                {
                    reason = $"{visual.GetType().Name} on '{visual.name}' has color alpha {UGuiRectMath.Format(ownAlpha)}";
                    return false;
                }

                var effective = inherited * ownAlpha;
                if (effective <= MinVisibleAlpha)
                {
                    reason = $"effective alpha is {UGuiRectMath.Format(effective)} " +
                             $"(CanvasGroup {UGuiRectMath.Format(inherited)} x color {UGuiRectMath.Format(ownAlpha)})";
                    return false;
                }
            }
            else
            {
                // A container draws nothing itself, so it has no CanvasRenderer to ask for the
                // cumulative alpha and the walk has to be done by hand. There is no colour and no
                // cull flag to consider either: the only way to hide a container is to fade the
                // CanvasGroups above it, deactivate it, or push it off screen.
                var inherited = CumulativeGroupAlpha(parts.RectTransform);
                if (inherited <= MinVisibleAlpha)
                {
                    reason = $"cumulative CanvasGroup alpha is {UGuiRectMath.Format(inherited)}";
                    return false;
                }
            }

            if (!hasScreenRect)
            {
                reason = "the node has no screen rect this frame";
                return false;
            }

            if (visibleRect.width <= 0f || visibleRect.height <= 0f)
            {
                reason = "clipped to nothing by an ancestor mask or by the edge of the surface";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Whether the control accepts interaction in its own right.
        ///
        /// <c>Selectable.IsInteractable()</c>, never <c>.interactable</c>: measured with a parent
        /// CanvasGroup at interactable = false, the property still reported True while
        /// <c>IsInteractable()</c> correctly reported False. The property alone would greenlight a
        /// tap on a control uGUI itself refuses to drive.
        /// </summary>
        public bool IsEnabled(in UGuiNodeParts parts, out string reason)
        {
            var selectable = parts.Selectable;
            if (selectable != null)
            {
                if (selectable.IsInteractable())
                {
                    reason = null;
                    return true;
                }

                if (!selectable.interactable)
                {
                    reason = $"{selectable.GetType().Name} on '{selectable.name}' has interactable = false";
                    return false;
                }

                var group = FindNonInteractableGroup(selectable.transform);
                reason = group != null
                    ? $"an ancestor CanvasGroup on '{group.name}' has interactable = false"
                    : $"{selectable.GetType().Name} on '{selectable.name}' reports IsInteractable() = false although its " +
                      "own interactable flag is true and no enabled CanvasGroup above it forbids interaction; uGUI's " +
                      "cached answer (Selectable.m_GroupsAllowInteraction, refreshed only in OnCanvasGroupChanged) is " +
                      "out of date for the current hierarchy";
                return false;
            }

            if (parts.Control is Behaviour behaviour && !behaviour.enabled)
            {
                reason = $"{behaviour.GetType().Name} on '{behaviour.name}' is disabled";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Whether a pointer hit can reach this node at all, independent of where it is on screen.
        /// Mirrors the two gates uGUI applies before it ever calls a raycast filter:
        /// <c>Graphic.raycastTarget</c> and the CanvasGroup <c>blocksRaycasts</c> chain.
        /// </summary>
        public bool IsHittable(in UGuiNodeParts parts, out string reason)
        {
            var visual = parts.VisualOwner;
            if (visual == null)
            {
                reason = "the node carries no Graphic and no Selectable.targetGraphic, so no pointer hit can land on it";
                return false;
            }

            if (!visual.raycastTarget)
            {
                reason = $"{visual.GetType().Name} on '{visual.name}' has raycastTarget = false";
                return false;
            }

            var blocking = FindRaycastBlockingGroup(visual.transform);
            if (blocking != null)
            {
                reason = $"CanvasGroup on '{blocking.name}' has blocksRaycasts = false";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Whether layout has produced usable numbers for this node. Checked on the unclipped local
        /// rect so a legitimately mask-clipped element is not misreported as "layout not computed".
        /// </summary>
        public bool IsLayoutValid(in UGuiNodeParts parts, bool projectionSucceeded, out string reason)
        {
            var rect = parts.RectTransform.rect;

            if (!float.IsFinite(rect.width) || !float.IsFinite(rect.height))
            {
                reason = $"layout has not produced finite dimensions for '{parts.GameObject.name}' ({UGuiRectMath.Format(rect.width)} x {UGuiRectMath.Format(rect.height)})";
                return false;
            }

            if (rect.width <= 0f || rect.height <= 0f)
            {
                reason = $"'{parts.GameObject.name}' has a zero-area rect ({UGuiRectMath.Format(rect.width)} x {UGuiRectMath.Format(rect.height)}); layout has not run or the element is collapsed";
                return false;
            }

            if (!projectionSucceeded)
            {
                reason = "the node could not be projected to screen space";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The composite question the runner asks before it taps. Occlusion is deliberately NOT part
        /// of it: occlusion depends on the chosen point and is verified at injection time.
        /// </summary>
        public bool IsActionable(in UGuiNodeParts parts, bool projectionSucceeded, in Rect visibleRect, out string reason)
        {
            if (!IsLayoutValid(parts, projectionSucceeded, out reason))
                return false;

            if (!IsVisible(parts, projectionSucceeded, visibleRect, out reason))
                return false;

            if (!IsEnabled(parts, out reason))
                return false;

            if (!IsHittable(parts, out reason))
                return false;

            reason = null;
            return true;
        }

        /// <summary>The topmost ancestor (or the object itself) whose own activeSelf is false.</summary>
        private static GameObject FindInactiveAncestor(GameObject go)
        {
            var result = go;
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                    result = t.gameObject;
            }

            return result;
        }

        /// <summary>First disabled Canvas between the node and its tree root, inclusive.</summary>
        private static Canvas FindDisabledCanvas(Transform from, Canvas rootCanvas)
        {
            var stopAt = rootCanvas.transform;
            for (var t = from; t != null; t = t.parent)
            {
                if (t.TryGetComponent<Canvas>(out var canvas) && !canvas.enabled)
                    return canvas;

                if (t == stopAt)
                    break;
            }

            return null;
        }

        /// <summary>
        /// The CanvasGroup that stops pointer hits, following <c>Graphic.Raycast</c> exactly:
        /// disabled groups are skipped entirely, <c>ignoreParentGroups</c> is INCLUSIVE (the group
        /// that raises the flag is still evaluated, everything above it is not), and a Canvas with
        /// overrideSorting ends the walk after its own components are considered.
        /// </summary>
        private static CanvasGroup FindRaycastBlockingGroup(Transform from)
        {
            var ignoreParentGroups = false;

            for (var t = from; t != null; t = t.parent)
            {
                if (t.TryGetComponent<CanvasGroup>(out var group) && group.enabled && !ignoreParentGroups)
                {
                    if (group.ignoreParentGroups)
                        ignoreParentGroups = true;

                    if (!group.blocksRaycasts)
                        return group;
                }

                if (t.TryGetComponent<Canvas>(out var canvas) && canvas.overrideSorting)
                    break;
            }

            return null;
        }

        /// <summary>
        /// The CanvasGroup responsible for a Selectable reporting not-interactable, mirroring
        /// <c>Selectable.ParentGroupAllowsInteraction</c> line for line.
        ///
        /// Two details this must not paraphrase: <c>ignoreParentGroups</c> ends the walk even on a
        /// DISABLED group (the real code tests it outside the enabled check), and every CanvasGroup
        /// on a level is examined in component order rather than just the first. Getting either
        /// wrong makes the report name a CanvasGroup that is not the culprit, which is worse than
        /// naming none.
        /// </summary>
        // Derived from Selectable.ParentGroupAllowsInteraction in com.unity.ugui.
        // uGUI Copyright (c) 2015-2020 Unity Technologies ApS, Unity Companion License:
        // https://unity.com/legal/licenses/unity-companion-license
        private CanvasGroup FindNonInteractableGroup(Transform from)
        {
            for (var t = from; t != null; t = t.parent)
            {
                t.GetComponents(m_GroupBuffer);
                for (var i = 0; i < m_GroupBuffer.Count; i++)
                {
                    var group = m_GroupBuffer[i];
                    if (group.enabled && !group.interactable)
                        return group;

                    if (group.ignoreParentGroups)
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Cumulative CanvasGroup alpha for a node that has no CanvasRenderer of its own.
        ///
        /// Hand-rolled only because there is nothing to ask: every drawn element gets this for free
        /// from <c>CanvasRenderer.GetInheritedAlpha()</c> and that remains the only source used for
        /// them. The two rules that make the numbers agree are that a DISABLED CanvasGroup
        /// contributes nothing, and that <c>ignoreParentGroups</c> stops the inheritance above it.
        /// </summary>
        private float CumulativeGroupAlpha(Transform from)
        {
            var alpha = 1f;

            for (var t = from; t != null; t = t.parent)
            {
                t.GetComponents(m_GroupBuffer);

                var ignoreParents = false;
                for (var i = 0; i < m_GroupBuffer.Count; i++)
                {
                    var group = m_GroupBuffer[i];
                    if (group.enabled)
                        alpha *= group.alpha;

                    if (group.ignoreParentGroups)
                        ignoreParents = true;
                }

                if (ignoreParents)
                    break;
            }

            return alpha;
        }
    }
}
