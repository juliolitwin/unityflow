namespace UnityFlow.Editor.Core
{
    /// <summary>Outcome of probing a screen point against an intended target.</summary>
    public enum HitOutcome
    {
        /// <summary>The point hits the target itself.</summary>
        Self,

        /// <summary>
        /// The point hits a descendant of the target. This PASSES. It is the common case, not an
        /// edge case: a uGUI Button's raycast target is usually a child Image, and a UI Toolkit
        /// Button's hit is usually its inner TextElement.
        /// </summary>
        Descendant,

        /// <summary>Something else is on top. The tap must fail, and the report must name what blocked it.</summary>
        Occluded,

        /// <summary>Nothing was hit at all — the point is outside every raycaster.</summary>
        NoHit,

        /// <summary>Occlusion could not be evaluated in this environment (e.g. no EventSystem in edit mode).</summary>
        Unavailable
    }

    /// <summary>Result of a hit probe, carrying enough detail to write a useful failure line.</summary>
    public readonly struct HitResult
    {
        public readonly HitOutcome Outcome;

        /// <summary>What was actually hit. <see cref="UiHandle.None"/> when nothing was.</summary>
        public readonly UiHandle Hit;

        /// <summary>Full path of what was hit, for the "obscured by /Canvas/Modal/Blocker" message.</summary>
        public readonly string HitPath;

        public HitResult(HitOutcome outcome, UiHandle hit, string hitPath)
        {
            Outcome = outcome;
            Hit = hit;
            HitPath = hitPath;
        }

        /// <summary>True when the probe landed on the target or inside it.</summary>
        public bool IsOnTarget => Outcome == HitOutcome.Self || Outcome == HitOutcome.Descendant;

        public static HitResult Unavailable(string reason) =>
            new HitResult(HitOutcome.Unavailable, UiHandle.None, reason);

        public static readonly HitResult NoHit = new HitResult(HitOutcome.NoHit, UiHandle.None, null);

        public override string ToString() =>
            Outcome == HitOutcome.Occluded ? $"obscured by {HitPath}" : Outcome.ToString();
    }
}
