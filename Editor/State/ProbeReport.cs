using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace UnityFlow.Editor.State
{
    /// <summary>One readable field or property of a live object, with the value it holds right now.</summary>
    public sealed class ProbeMember
    {
        /// <summary>Name to write as <c>field:</c>.</summary>
        public string Name;

        /// <summary><c>field</c> or <c>property</c>.</summary>
        public string Kind;

        /// <summary>Declared type, as written in C#.</summary>
        public string Type;

        /// <summary>Type that declares it, when that is not the probed type itself.</summary>
        public string DeclaredBy;

        /// <summary>True for a static member: readable without any instance existing.</summary>
        public bool IsStatic;

        /// <summary>Current value, formatted.</summary>
        public string Value;

        /// <summary>Why the value could not be read. Null when it was read.</summary>
        public string Error;

        /// <summary>Whether a state query can compare this member directly, or needs <c>expr</c>.</summary>
        public bool Queryable;
    }

    /// <summary>One live object the probe found.</summary>
    public sealed class ProbeInstance
    {
        /// <summary>Full hierarchy path, in the same form every UnityFlow failure message uses.</summary>
        public string Path;

        /// <summary>Concrete type of the instance, which may be a subclass of the probed type.</summary>
        public string Type;

        /// <summary>Whether the object is active in the hierarchy right now.</summary>
        public bool ActiveInHierarchy;

        /// <summary>Scene the object lives in, or <c>DontDestroyOnLoad</c>.</summary>
        public string Scene;

        /// <summary>Readable members, with values.</summary>
        public List<ProbeMember> Members = new List<ProbeMember>();

        /// <summary>How many members were left out by the member cap. Zero when all are listed.</summary>
        public int MembersOmitted;
    }

    /// <summary>
    /// What <c>flow.probe</c> returns: every live instance of a type, its path, and every readable
    /// member with its CURRENT VALUE.
    ///
    /// It exists because nobody can guess a field name. Without it, <c>assert</c> and
    /// <c>waitUntil</c> are a syntax with nothing to say — an author would be reading source files
    /// to find out that health is <c>m_Current</c> and not <c>current</c>, and getting it wrong is
    /// indistinguishable from the assertion failing.
    ///
    /// <para><b>What it cannot see.</b> It walks <see cref="UnityEngine.Component"/> instances, so
    /// it covers the MonoBehaviour surface — views, UI, cameras, managers — and nothing else. A
    /// project that keeps its gameplay state off components (an ECS world, statics, plain C#
    /// services) will find that state absent here, which reads as "there is no state" unless the
    /// report says otherwise; hence the note every probe carries. Reach it with <c>expr</c>.</para>
    /// </summary>
    public sealed class ProbeReport
    {
        /// <summary>Members whose value string is truncated past this length. A probe is for orientation, not a dump.</summary>
        private const int MaxValueLength = 120;

        /// <summary>Full name of the component type that was probed. Null when only <c>find</c> was given.</summary>
        public string Component;

        /// <summary>The <c>find</c> filter, as written.</summary>
        public string Find;

        /// <summary>How many instances were listed.</summary>
        public int Count;

        /// <summary>True when the instance cap cut the list short.</summary>
        public bool Capped;

        /// <summary>Says in words what was capped and how to see more. Null when nothing was capped.</summary>
        public string CapNote;

        /// <summary>The off-component caveat, always present, because a silent empty probe reads as "there is no state".</summary>
        public string Note;

        /// <summary>Why the probe found nothing to report. Null when it did.</summary>
        public string Error;

        /// <summary>The instances.</summary>
        public List<ProbeInstance> Instances = new List<ProbeInstance>();

        /// <summary>
        /// Probe a component type, optionally scoped to one object.
        ///
        /// Members declared by UnityEngine's own base classes are omitted by default: probing a
        /// gameplay MonoBehaviour would otherwise bury its four interesting fields under
        /// <c>transform</c>, <c>tag</c>, <c>hideFlags</c> and the obsolete component shortcuts.
        /// </summary>
        public static ProbeReport ForComponent(
            string component, string find, int maxInstances, int maxMembers, bool includeUnityBase)
        {
            var report = new ProbeReport
            {
                Component = component,
                Find = find,
                Note = OffComponentStateNote()
            };

            if (!StateResolver.TryResolveComponentType(component, out var type, out var error))
            {
                report.Error = error;
                return report;
            }

            report.Component = type.FullName;

            var instances = StateResolver.FindComponents(type, find, maxInstances + 1);
            report.Capped = instances.Count > maxInstances;

            for (var i = 0; i < instances.Count && i < maxInstances; i++)
                report.Instances.Add(Describe(instances[i], instances[i].GetType(), maxMembers, includeUnityBase));

            report.Count = report.Instances.Count;

            if (report.Capped)
            {
                report.CapNote = $"more than {maxInstances} instances are live; only the first {maxInstances} are listed. " +
                                 "Raise --max, or scope the probe with --find <name-or-path>";
            }

            if (report.Count == 0)
            {
                report.Error = find == null
                    ? $"no live instance of {type.FullName} exists (searched every loaded scene, including inactive objects and DontDestroyOnLoad)"
                    : $"no live instance of {type.FullName} is on an object matching '{find}'";
            }

            return report;
        }

        /// <summary>
        /// Probe every component on the objects matching a name or path. This is the "what is even
        /// on this thing" question, asked before the author knows which component to name.
        /// </summary>
        public static ProbeReport ForObject(string find, int maxInstances, int maxMembers, bool includeUnityBase)
        {
            var report = new ProbeReport
            {
                Find = find,
                Note = OffComponentStateNote()
            };

            var objects = StateResolver.FindGameObjects(find, maxInstances + 1);
            report.Capped = objects.Count > maxInstances;

            for (var i = 0; i < objects.Count && i < maxInstances; i++)
            {
                var components = objects[i].GetComponents<Component>();
                for (var c = 0; c < components.Length; c++)
                {
                    // A missing script leaves a null slot; naming it is more useful than skipping it,
                    // because a missing script is a real reason a flow cannot find what it expects.
                    if (components[c] == null)
                    {
                        report.Instances.Add(new ProbeInstance
                        {
                            Path = StateResolver.PathOf(objects[i]),
                            Type = "(missing script)",
                            ActiveInHierarchy = objects[i].activeInHierarchy,
                            Scene = SceneOf(objects[i])
                        });

                        continue;
                    }

                    report.Instances.Add(Describe(components[c], components[c].GetType(), maxMembers, includeUnityBase));
                }
            }

            report.Count = report.Instances.Count;

            if (report.Capped)
            {
                report.CapNote = $"more than {maxInstances} objects match '{find}'; only the first {maxInstances} are listed. " +
                                 "Raise --max, or use a full hierarchy path such as \"/UI Root/HUD\"";
            }

            if (report.Count == 0)
            {
                report.Error = $"no live GameObject matches '{find}' (searched every loaded scene, including inactive " +
                               "objects and DontDestroyOnLoad). A name must match exactly; a value containing '/' is " +
                               "matched as a full hierarchy path";
            }

            return report;
        }

        private static ProbeInstance Describe(Component component, Type type, int maxMembers, bool includeUnityBase)
        {
            var instance = new ProbeInstance
            {
                Path = StateResolver.PathOf(component),
                Type = type.FullName,
                ActiveInHierarchy = component.gameObject.activeInHierarchy,
                Scene = SceneOf(component.gameObject)
            };

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var candidates = new List<Candidate>(64);

            for (var level = type; level != null && level != typeof(object); level = level.BaseType)
            {
                if (!includeUnityBase && IsUnityBase(level))
                    break;

                foreach (var field in level.GetFields(Flags))
                {
                    // A const can never change, so it is noise in a list whose purpose is to show
                    // what a flow could wait for.
                    if (field.IsLiteral)
                        continue;

                    candidates.Add(new Candidate(field, field.FieldType, level, "field", field.IsStatic));
                }

                foreach (var property in level.GetProperties(Flags))
                {
                    if (property.GetMethod == null || property.GetIndexParameters().Length > 0)
                        continue;

                    candidates.Add(new Candidate(property, property.PropertyType, level, "property", property.GetMethod.IsStatic));
                }
            }

            // Instance state first, statics after. A static on a MonoBehaviour is almost always
            // tuning data that never changes, and when the cap bites it is the live per-object
            // state a flow wants to see, not the configuration constants.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].IsStatic != (pass == 1))
                        continue;

                    Add(instance, component, type, candidates[i], maxMembers);
                }
            }

            return instance;
        }

        /// <summary>A member the probe intends to read, kept until the ordering is settled.</summary>
        private readonly struct Candidate
        {
            public readonly MemberInfo Member;
            public readonly Type MemberType;
            public readonly Type Level;
            public readonly string Kind;
            public readonly bool IsStatic;

            public Candidate(MemberInfo member, Type memberType, Type level, string kind, bool isStatic)
            {
                Member = member;
                MemberType = memberType;
                Level = level;
                Kind = kind;
                IsStatic = isStatic;
            }
        }

        private static void Add(ProbeInstance instance, Component target, Type probed, in Candidate candidate, int maxMembers)
        {
            if (instance.Members.Count >= maxMembers)
            {
                instance.MembersOmitted++;
                return;
            }

            var entry = new ProbeMember
            {
                Name = candidate.Member.Name,
                Kind = candidate.Kind,
                Type = StateResolver.FriendlyTypeName(candidate.MemberType),
                DeclaredBy = candidate.Level == probed ? null : candidate.Level.FullName,
                IsStatic = candidate.IsStatic,
                Queryable = IsQueryable(candidate.MemberType)
            };

            try
            {
                var value = candidate.Member is FieldInfo field
                    ? field.GetValue(candidate.IsStatic ? null : target)
                    : ((PropertyInfo)candidate.Member).GetValue(candidate.IsStatic ? null : target);

                entry.Value = Format(value);
            }
            catch (Exception exception)
            {
                // Unity properties throw for real reasons (a MissingComponentException, an obsolete
                // accessor). Reporting which one is the point: it tells the author this member is
                // not something a flow can assert on.
                var cause = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;

                entry.Error = $"{cause.GetType().Name}: {cause.Message}";
            }

            instance.Members.Add(entry);
        }

        private static string Format(object value)
        {
            if (value == null)
                return "null";

            if (value is string text)
                return Truncate($"\"{text}\"");

            // Written the way a flow writes it: 'is: true', never 'is: True'.
            if (value is bool flag)
                return flag ? "true" : "false";

            if (value is UnityEngine.Object unityObject)
            {
                return unityObject == null
                    ? "null (destroyed)"
                    : Truncate($"{unityObject.name} ({value.GetType().Name})");
            }

            if (value is float single)
                return single.ToString("R", CultureInfo.InvariantCulture);

            if (value is double real)
                return real.ToString("R", CultureInfo.InvariantCulture);

            return Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name);
        }

        private static string Truncate(string value) =>
            value.Length > MaxValueLength ? value.Substring(0, MaxValueLength) + "..." : value;

        private static bool IsQueryable(Type type) =>
            type.IsEnum || type == typeof(string) || !type.IsValueType ||
            type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
            type == typeof(double) || type == typeof(decimal);

        private static bool IsUnityBase(Type type) =>
            type == typeof(MonoBehaviour) || type == typeof(Behaviour) ||
            type == typeof(Component) || type == typeof(UnityEngine.Object);

        private static string SceneOf(GameObject gameObject)
        {
            var scene = gameObject.scene;
            return scene.IsValid() ? scene.name : "(no scene)";
        }

        private static string OffComponentStateNote() =>
            "A probe walks Component instances, so it lists the MonoBehaviour surface only. State that lives " +
            "anywhere else — ECS worlds, statics, plain C# services, singletons reached through a manager — is " +
            "invisible here even when it is the state you are asserting on. Reach that with assert/waitUntil 'expr'.";
    }
}
