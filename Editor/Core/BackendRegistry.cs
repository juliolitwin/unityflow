using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UnityFlow.Editor.Core
{
    /// <summary>
    /// Discovers and owns the backends and input driver for ONE run.
    ///
    /// This is deliberately an instance created per run rather than a static registry that
    /// backends push themselves into. Per-run construction means a run can never inherit stale
    /// state from a previous run, registration order cannot matter, and a domain reload needs no
    /// re-registration handshake — the next run simply rediscovers.
    ///
    /// Discovery is by TypeCache, which Unity indexes ahead of time, so this costs nothing
    /// measurable and the core never references a backend assembly. A backend whose optional
    /// package is missing does not compile into the domain at all (its assembly is gated by
    /// defineConstraints), so it is simply absent here rather than present-and-broken.
    /// </summary>
    public sealed class BackendRegistry
    {
        private readonly List<IUiBackend> m_Active = new List<IUiBackend>();
        private readonly List<string> m_Rejected = new List<string>();

        /// <summary>Backends that reported themselves live, highest priority first.</summary>
        public IReadOnlyList<IUiBackend> Active => m_Active;

        /// <summary>Human-readable reasons for every backend that was found but not usable.</summary>
        public IReadOnlyList<string> Rejected => m_Rejected;

        /// <summary>The input driver, or null when none is usable. Null means degraded, never silent.</summary>
        public IInputDriver InputDriver { get; }

        /// <summary>Why there is no input driver, when there isn't one.</summary>
        public string InputDriverRejection { get; }

        public BackendRegistry()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<IUiBackend>())
            {
                if (!IsInstantiable(type))
                    continue;

                IUiBackend backend;
                try
                {
                    backend = (IUiBackend)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    m_Rejected.Add($"{type.Name}: construction threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (backend.IsAvailable(out var reason))
                    m_Active.Add(backend);
                else
                    m_Rejected.Add($"{backend.Id}: {reason}");
            }

            m_Active.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            for (var i = 0; i < m_Active.Count; i++)
                m_Active[i].BackendIndex = i;

            InputDriver = ResolveInputDriver(out var driverRejection);
            InputDriverRejection = driverRejection;
        }

        /// <summary>Resolve a backend by the index stored in a handle.</summary>
        public IUiBackend ForHandle(UiHandle handle)
        {
            if (handle.IsNone || handle.BackendId < 0 || handle.BackendId >= m_Active.Count)
                return null;

            return m_Active[handle.BackendId];
        }

        /// <summary>Resolve a backend by its short id, e.g. "ugui".</summary>
        public IUiBackend ById(string id) =>
            m_Active.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The weakest occlusion fidelity across active backends. Reported in the run header:
        /// a reader must always know whether "the tap landed" was actually verified.
        /// </summary>
        public OcclusionFidelity EffectiveOcclusionFidelity
        {
            get
            {
                if (m_Active.Count == 0)
                    return OcclusionFidelity.None;

                var weakest = OcclusionFidelity.CrossSurface;
                foreach (var backend in m_Active)
                {
                    if (backend.OcclusionFidelity < weakest)
                        weakest = backend.OcclusionFidelity;
                }

                return weakest;
            }
        }

        private static IInputDriver ResolveInputDriver(out string rejection)
        {
            var reasons = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IInputDriver>())
            {
                if (!IsInstantiable(type))
                    continue;

                IInputDriver driver;
                try
                {
                    driver = (IInputDriver)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    reasons.Add($"{type.Name}: construction threw {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (driver.IsAvailable(out var reason))
                {
                    rejection = null;
                    return driver;
                }

                reasons.Add($"{driver.Id}: {reason}");
            }

            rejection = reasons.Count == 0
                ? "no input driver assembly is present (the Input System package is not installed)"
                : string.Join("; ", reasons);

            return null;
        }

        private static bool IsInstantiable(Type type) =>
            !type.IsAbstract &&
            !type.IsInterface &&
            !type.IsGenericTypeDefinition &&
            type.GetConstructor(Type.EmptyTypes) != null;
    }
}
