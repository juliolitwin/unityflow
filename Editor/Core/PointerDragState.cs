namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// What a UI system says about the pointer that is currently holding a button down.
    ///
    /// DECLARED WEAKEST FIRST, and the order is load-bearing: a run with two UI systems up asks both
    /// and keeps the strongest answer, because the one that does not own the pointer answers "None"
    /// perfectly truthfully and must not be able to veto the one that does.
    /// </summary>
    public enum PointerDragOutcome
    {
        /// <summary>
        /// This UI system cannot answer at all — nothing is dispatching pointer events, or the module
        /// that is doing it keeps its pointer state somewhere this build cannot read. Never treat it
        /// as "no drag": it is "no answer", and the two lead to opposite conclusions.
        /// </summary>
        Unavailable,

        /// <summary>No pointer is holding a button, or the one that is has nothing registered to receive its drags.</summary>
        None,

        /// <summary>A pointer holds a button and a node is registered to receive its drags, but no drag has begun yet.</summary>
        Armed,

        /// <summary>A drag is in progress and the registered node is receiving it.</summary>
        Dragging
    }

    /// <summary>
    /// The generic "is a drag happening" answer, plus who would receive it.
    ///
    /// It is deliberately the UI SYSTEM's own view and nothing else: a harness that reached into the
    /// game's own drag component to answer this would only ever work for one game, and would report
    /// green on a UI whose pointer plumbing is broken.
    /// </summary>
    public readonly struct PointerDragState
    {
        public PointerDragOutcome Outcome { get; }

        /// <summary>Path of the node registered to receive the drag, or null when there is none.</summary>
        public string HandlerPath { get; }

        /// <summary>Why the answer is <see cref="PointerDragOutcome.Unavailable"/> or <see cref="PointerDragOutcome.None"/>.</summary>
        public string Reason { get; }

        private PointerDragState(PointerDragOutcome outcome, string handlerPath, string reason)
        {
            Outcome = outcome;
            HandlerPath = handlerPath;
            Reason = reason;
        }

        public static PointerDragState CannotRead(string reason) =>
            new PointerDragState(PointerDragOutcome.Unavailable, null, reason);

        public static PointerDragState Nothing(string reason) =>
            new PointerDragState(PointerDragOutcome.None, null, reason);

        public static PointerDragState Armed(string handlerPath) =>
            new PointerDragState(PointerDragOutcome.Armed, handlerPath, null);

        public static PointerDragState Dragging(string handlerPath) =>
            new PointerDragState(PointerDragOutcome.Dragging, handlerPath, null);

        /// <summary>One line for a progress record or a failure message.</summary>
        public string Describe()
        {
            switch (Outcome)
            {
                case PointerDragOutcome.Dragging: return $"a drag is in progress on {HandlerPath}";
                case PointerDragOutcome.Armed: return $"the press is live and {HandlerPath} is registered to receive its drags";
                case PointerDragOutcome.None: return Reason;
                default: return $"the UI system could not be asked: {Reason}";
            }
        }
    }
}
