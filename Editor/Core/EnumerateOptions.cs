namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// Filters pushed DOWN into the backend rather than applied by the core afterwards.
    ///
    /// This is a performance contract, not a convenience. Enumerating uGUI naively allocates
    /// ~61 KB per call at 3000 nodes, and a UI Toolkit <c>Query&lt;VisualElement&gt;()</c> over a
    /// ScrollView containing a single Label returns 23 nodes of internal chrome. A retry loop runs
    /// this every frame, so the backend must filter at the source and fill a caller-owned list.
    /// </summary>
    public struct EnumerateOptions
    {
        /// <summary>Restrict to one surface. Null enumerates all of them.</summary>
        public int? SurfaceId;

        /// <summary>Match only this control type. Null matches any.</summary>
        public string TypeFilter;

        /// <summary>Match only this exact node name. Null matches any.</summary>
        public string NameFilter;

        /// <summary>Match only this testId. Null matches any.</summary>
        public string TestIdFilter;

        /// <summary>
        /// Include nodes that are inactive or hidden. Needed for diagnostics ("the popup exists but
        /// its alpha is 0"), which is precisely what turns a timeout into an actionable report.
        /// </summary>
        public bool IncludeInactive;

        /// <summary>Include backend-internal chrome elements. Off by default: it is noise.</summary>
        public bool IncludeInternal;

        /// <summary>Hard ceiling so a pathological scene cannot wedge a run. 0 means unlimited.</summary>
        public int MaxNodes;

        /// <summary>Everything visible and interactable, no internals — the default for selector resolution.</summary>
        public static EnumerateOptions Default => new EnumerateOptions { MaxNodes = 20000 };

        /// <summary>Everything, including hidden nodes and chrome — the default for failure snapshots.</summary>
        public static EnumerateOptions Diagnostic =>
            new EnumerateOptions { IncludeInactive = true, MaxNodes = 20000 };
    }
}
