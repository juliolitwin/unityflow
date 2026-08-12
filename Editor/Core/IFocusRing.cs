namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// One step along a UI system's keyboard/gamepad navigation graph.
    ///
    /// Four directions and no more, because this enum describes what a DEVICE can express through
    /// the module's Move action: uGUI's <c>InputSystemUIInputModule</c> reads Move as a Vector2 and
    /// quantises it into <c>MoveDirection.Up/Down/Left/Right</c>, and UI Toolkit's
    /// <c>NavigationMoveEvent</c> carries the same four for arrow input. Tab order is deliberately
    /// absent: uGUI has no Tab concept at all (nothing in its navigation graph answers it), so
    /// declaring Next/Previous here would put a member on the interface that the only existing
    /// backend could never answer.
    /// </summary>
    public enum FocusDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    /// <summary>
    /// What the focus ring says happens when focus moves one step in one direction — including,
    /// crucially, why NOTHING happens.
    ///
    /// The "nothing happens" case is the whole reason this type exists. An element a keyboard or
    /// controller player can never reach is a real accessibility bug, and the only useful report of
    /// one names the wiring that is missing ("selectOnDown = None") rather than saying that a key
    /// press did not change anything.
    /// </summary>
    public readonly struct FocusLink
    {
        /// <summary>Whether focus would actually land somewhere.</summary>
        public readonly bool HasTarget;

        /// <summary>
        /// Where focus would land. Always <see cref="UiHandle.None"/> when <see cref="HasTarget"/>
        /// is false — and legitimately None even when it is true, for the same reason
        /// <see cref="IFocusRing.TryGetFocused"/> can hand back None: the ring may point at
        /// something the backend does not enumerate. uGUI's automatic navigation searches a GLOBAL
        /// selectable list that owes nothing to the canvas tree, so a link can genuinely lead to a
        /// Selectable with no Canvas ancestor. Callers that need identity must check
        /// <see cref="UiHandle.IsNone"/>; callers that only need to report use
        /// <see cref="TargetPath"/>, which is filled in either way.
        /// </summary>
        public readonly UiHandle Target;

        /// <summary>Path of the target, for reports. Null when there is no target.</summary>
        public readonly string TargetPath;

        /// <summary>
        /// How the UI system decided, in ITS OWN vocabulary: "Explicit", "Automatic", "None",
        /// "Horizontal" for uGUI's <c>Navigation.Mode</c>; a UI Toolkit ring would say
        /// "focusable"/"not focusable"/"ring order". Deliberately a string: a shared enum would
        /// force one system's model onto the other and lose the term an author has to go and fix.
        /// </summary>
        public readonly string Mode;

        /// <summary>
        /// The missing wiring, phrased so it can be pasted into a report:
        /// "selectOnDown = None", "navigation.mode = None". Null when <see cref="HasTarget"/>.
        /// </summary>
        public readonly string Missing;

        private FocusLink(bool hasTarget, UiHandle target, string targetPath, string mode, string missing)
        {
            HasTarget = hasTarget;
            Target = target;
            TargetPath = targetPath;
            Mode = mode;
            Missing = missing;
        }

        public static FocusLink To(UiHandle target, string targetPath, string mode) =>
            new FocusLink(true, target, targetPath, mode, null);

        public static FocusLink Dead(string mode, string missing) =>
            new FocusLink(false, UiHandle.None, null, mode, missing);

        public override string ToString() =>
            HasTarget ? $"{Mode} -> {TargetPath}" : $"{Mode} -> nothing ({Missing})";
    }

    /// <summary>
    /// A UI system's keyboard/gamepad focus ring: what is focused, how focus is seeded, and how the
    /// system says focus travels.
    ///
    /// This is a FACET rather than three more members on <see cref="IUiBackend"/>, and it is
    /// nullable, because a focus ring is not something every UI surface has. IUiBackend's contract
    /// is "describe and probe"; a focus ring is a distinct, optional capability with its own small
    /// vocabulary, and a backend that has none says so by returning null instead of implementing
    /// three members that can only fail.
    ///
    /// IT DOES NOT MOVE FOCUS. There is exactly one deliberate exception,
    /// <see cref="TryEstablishInitialFocus"/>, and it refuses to run once anything is focused —
    /// because "select the element I want" through an API is not input, and a test that jumps the
    /// selection proves nothing about whether a player could have reached it. Moving focus is the
    /// input driver's job, through real key events.
    ///
    /// UI Toolkit maps onto this without a core change: <c>FocusController.focusedElement</c> for
    /// <see cref="TryGetFocused"/>, <c>Focusable.Focus()</c> for
    /// <see cref="TryEstablishInitialFocus"/>, the panel's focus ring order for
    /// <see cref="TryGetDefaultFocus"/>, and <c>focusable</c> / <c>tabIndex</c> / the
    /// NavigationMoveEvent resolution for <see cref="TryDescribeLink"/>.
    /// </summary>
    public interface IFocusRing
    {
        /// <summary>
        /// Whether the ring can be read and driven right now, with the reason when it cannot.
        ///
        /// The reason matters as much as it does on <see cref="IInputDriver.IsAvailable"/>: uGUI's
        /// EventSystem is not [ExecuteAlways], so in edit mode <c>EventSystem.current</c> is null
        /// even with an EventSystem sitting in the open scene. Without this, every navigation verb
        /// would report "nothing is selected" in edit mode — true, useless, and pointing the reader
        /// at the wrong problem.
        /// </summary>
        bool IsAvailable(out string reason);

        /// <summary>
        /// What is focused right now. False means the ring is empty, NOT that it failed — check
        /// <see cref="IsAvailable"/> first.
        ///
        /// <paramref name="handle"/> may come back as <see cref="UiHandle.None"/> while the method
        /// returns true: the focused object can be something this backend does not enumerate. The
        /// path is still filled in, because naming what unexpectedly holds focus is exactly what a
        /// reader needs at that point.
        /// </summary>
        bool TryGetFocused(out UiHandle handle, out string path);

        /// <summary>
        /// Seed the FIRST focus of the ring, and only the first: implementations must refuse when
        /// something is already focused. This is the assist path — a caller that uses it is
        /// obliged to say so in its report, because the resulting selection was not reached by
        /// input.
        /// </summary>
        bool TryEstablishInitialFocus(UiHandle handle, out string error);

        /// <summary>
        /// The element the ring would seed itself with on <paramref name="surfaceId"/>: the first
        /// element that can hold focus, in the UI system's own order. Used to answer "the keyboard
        /// is nowhere, where does it start" deterministically instead of guessing.
        /// </summary>
        bool TryGetDefaultFocus(int surfaceId, out UiHandle handle, out string reason);

        /// <summary>
        /// Where the ring says focus travels from <paramref name="handle"/> in
        /// <paramref name="direction"/>, WITHOUT moving it.
        ///
        /// False with a <paramref name="reason"/> means the question cannot be asked of this node
        /// at all (it is not part of the ring, its handle is stale). A node that is in the ring but
        /// wired to nowhere returns true with a <see cref="FocusLink"/> that has no target and
        /// names what is missing — those are different answers and a report must not merge them.
        /// </summary>
        bool TryDescribeLink(UiHandle handle, FocusDirection direction, out FocusLink link, out string reason);
    }
}
