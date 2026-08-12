using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.State
{
    /// <summary>What one evaluation of a query concluded.</summary>
    public enum StateOutcome
    {
        /// <summary>The comparison holds right now.</summary>
        Satisfied,

        /// <summary>The value was read and the comparison does not hold. Worth retrying.</summary>
        NotSatisfied,

        /// <summary>The object is not there yet. Worth retrying.</summary>
        Unresolved,

        /// <summary>The query can never be answered as written. Retrying is pointless; stop now.</summary>
        Fatal
    }

    /// <summary>
    /// Answers a <see cref="StateQuery"/> against the live scene.
    ///
    /// One instance serves one query for the whole of one step, and everything expensive is done
    /// once and kept in instance fields: the component <see cref="Type"/>, the
    /// <see cref="MemberInfo"/>, the compiled reader delegate, the coerced expectation, and the
    /// resolved target object. That is not an optimisation, it is what makes the verb usable:
    /// <c>waitUntil</c> re-evaluates EVERY FRAME until its timeout, so a five-second wait is ~300
    /// evaluations, and reflecting over a type 300 times to read one int would cost more than the
    /// thing being measured.
    ///
    /// <para><b>What still allocates, and why.</b> Searching for the target allocates: Unity has no
    /// scene-wide census that covers inactive objects and DontDestroyOnLoad without materialising an
    /// array, and reading <c>GameObject.name</c> allocates a string per call. So the search runs
    /// only while the target is missing, and the resolved object is then cached and re-validated
    /// with a null check — which is a native identity test, not a search. Once resolved, a member
    /// read costs one delegate call and zero bytes. <see cref="StateQueryMode.Count"/> is the honest
    /// exception: a census cannot be cached because its answer is the thing being watched.</para>
    ///
    /// <para>Nothing here ever picks a winner. An ambiguous type name, two objects matching one
    /// <c>find</c>, a member that is both a field and a property — all of them fail with the
    /// candidates listed, because a query that silently chose one of several would report a pass
    /// about an object the author never meant.</para>
    /// </summary>
    public sealed class StateResolver
    {
        /// <summary>How many candidates an ambiguity message lists before it summarises the rest.</summary>
        private const int MaxListedCandidates = 8;

        /// <summary>
        /// Tolerance for <c>eq</c> and <c>ne</c> on numbers, relative to the magnitude compared.
        ///
        /// A float that reads 0.30000001192 IS the number the author wrote as 0.3; failing that
        /// assertion would be a report about IEEE 754, not about the game. Ordering comparisons
        /// (gt/lt) are exact: there the author asked about a boundary, and blurring it would hide
        /// exactly the off-by-a-hair case they were testing for.
        /// </summary>
        private const double NumberTolerance = 1e-6;

        private readonly StateQuery m_Query;

        private bool m_Prepared;
        private string m_Fatal;

        private Type m_ComponentType;
        private string m_MatchName;

        private MemberInfo m_Member;
        private Type m_MemberType;
        private bool m_MemberIsStatic;
        private Func<object, StateValue> m_Read;
        private Func<StateValue> m_ReadExpression;

        private UnityEngine.Object m_Target;

        private bool m_ExpectedReady;
        private StateValueKind m_ExpectedKind;
        private double m_ExpectedNumber;
        private bool m_ExpectedFlag;
        private string m_ExpectedText;

        private bool m_HasLastValue;
        private StateValue m_LastValue;
        private int m_LastCount = -1;
        private Exception m_Thrown;

        private int m_Evaluations;
        private long m_EvaluationTicks;

        public StateResolver(StateQuery query)
        {
            m_Query = query ?? throw new ArgumentNullException(nameof(query));
        }

        /// <summary>The query this resolver answers.</summary>
        public StateQuery Query => m_Query;

        /// <summary>
        /// Read the game once and compare. Allocation-free once the target is resolved; see the
        /// type documentation for the two places that cannot be.
        /// </summary>
        /// <summary>
        /// How many times this query has been read, and how long those reads took in total.
        ///
        /// A <c>waitUntil</c> re-reads every frame, so this is the number that says whether the
        /// retry model is affordable — and it is measured rather than asserted, because "reflection
        /// was replaced by a compiled delegate" says nothing about what the AUTHOR's expression
        /// does. An <c>expr</c> that calls <c>FindObjectsByType</c> is a scene-wide search once a
        /// frame no matter how it was compiled.
        /// </summary>
        public int Evaluations => m_Evaluations;

        /// <summary>Total time spent inside <see cref="Evaluate"/>, in milliseconds.</summary>
        public double EvaluationMilliseconds =>
            m_EvaluationTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public StateOutcome Evaluate()
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                return EvaluateOnce();
            }
            finally
            {
                m_Evaluations++;
                m_EvaluationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            }
        }

        private StateOutcome EvaluateOnce()
        {
            if (!m_Prepared)
            {
                m_Prepared = true;
                Prepare();
            }

            if (m_Fatal != null)
                return StateOutcome.Fatal;

            StateValue value;

            switch (m_Query.Mode)
            {
                case StateQueryMode.Expression:
                    return TryRead(null, out value) ? EvaluateValue(value) : StateOutcome.Unresolved;

                case StateQueryMode.Count:
                    m_LastCount = CountMatches();
                    return EvaluateValue(StateValue.OfNumber(m_LastCount));

                case StateQueryMode.Existence:
                    m_LastCount = CountMatches();
                    return (m_LastCount > 0) == m_ExpectedFlag ? StateOutcome.Satisfied : StateOutcome.NotSatisfied;

                default:
                    if (!m_MemberIsStatic && !TryResolveTarget())
                        return m_Fatal != null ? StateOutcome.Fatal : StateOutcome.Unresolved;

                    return TryRead(m_Target, out value) ? EvaluateValue(value) : StateOutcome.Unresolved;
            }
        }

        /// <summary>
        /// Read, treating a throw as "not yet" rather than as a failure.
        ///
        /// A property getter on a half-initialised object, or an expression reaching through a
        /// reference the game has not assigned, throws for exactly as long as the thing being
        /// waited for is missing — which is the situation the retry loop exists to absorb. The
        /// exception is kept, not swallowed: if the step goes on to time out, the report names it,
        /// so "it kept throwing" never looks like "it was simply false".
        /// </summary>
        private bool TryRead(object target, out StateValue value)
        {
            try
            {
                value = m_Query.Mode == StateQueryMode.Expression ? m_ReadExpression() : m_Read(target);
            }
            catch (Exception exception)
            {
                m_Thrown = exception;
                value = default;
                return false;
            }

            m_Thrown = null;
            return true;
        }

        /// <summary>
        /// The whole story in one sentence: what was asked, what was found, where it was found, and
        /// what was expected. Called once, after a step has decided its fate, because it allocates
        /// freely — building a hierarchy path and formatting a value are not retry-path work.
        /// </summary>
        public string Explain()
        {
            if (m_Fatal != null)
                return m_Fatal;

            var builder = new StringBuilder();
            builder.Append(m_Query.Describe());

            if (m_Thrown != null)
            {
                return builder
                    .Append(" kept throwing ").Append(m_Thrown.GetType().Name).Append(": ").Append(m_Thrown.Message)
                    .Append(m_Target == null ? string.Empty : $" (reading {PathOf(m_Target)})")
                    .Append(". It was re-read every frame and never stopped throwing, so the value was never seen")
                    .ToString();
            }

            switch (m_Query.Mode)
            {
                case StateQueryMode.Expression:
                    builder.Append(" evaluated to ").Append(FormatLast());
                    break;

                case StateQueryMode.Count:
                    builder.Append(" counted ").Append(m_LastCount < 0 ? "nothing yet" : m_LastCount.ToString(CultureInfo.InvariantCulture))
                        .Append(' ').Append(CountSubject());
                    break;

                case StateQueryMode.Existence:
                    builder.Append(m_LastCount > 0
                        ? $" matched {m_LastCount} live object{(m_LastCount == 1 ? string.Empty : "s")}"
                        : " matched no object (searched every loaded scene, including inactive objects and DontDestroyOnLoad)");
                    break;

                default:
                    if (m_MemberIsStatic)
                    {
                        builder.Append(" (static on ").Append(m_Member.DeclaringType?.FullName).Append(") is ").Append(FormatLast());
                    }
                    else if (m_Target == null)
                    {
                        builder.Append(" never resolved: no object matched (searched every loaded scene, ")
                            .Append("including inactive objects and DontDestroyOnLoad)");
                    }
                    else
                    {
                        builder.Append(" on ").Append(PathOf(m_Target)).Append(" is ").Append(FormatLast());
                    }

                    break;
            }

            builder.Append(", expected ").Append(m_Query.DescribeComparison(
                quoteExpected: m_HasLastValue && m_LastValue.Kind == StateValueKind.Text));

            if (UsesTolerance())
            {
                builder.Append(" (numbers are compared with a relative tolerance of ")
                    .Append(NumberTolerance.ToString("R", CultureInfo.InvariantCulture)).Append(')');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Type name as a C# author would write it: <c>float?</c>, <c>List&lt;int&gt;</c>. Reflection's
        /// own <c>Nullable`1</c> spelling in a message about a field the author is looking at is
        /// noise they then have to decode.
        /// </summary>
        public static string FriendlyTypeName(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                return FriendlyTypeName(nullable) + "?";

            if (!type.IsGenericType)
                return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0)
                name = name.Substring(0, tick);

            var builder = new StringBuilder(name).Append('<');
            var arguments = type.GetGenericArguments();

            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(FriendlyTypeName(arguments[i]));
            }

            return builder.Append('>').ToString();
        }

        /// <summary>Full hierarchy path of an object, matching the form the UI backends report: <c>/Root/Child</c>.</summary>
        public static string PathOf(UnityEngine.Object target)
        {
            var transform = target is Component component ? component.transform
                : target is GameObject gameObject ? gameObject.transform
                : null;

            if (transform == null)
                return target == null ? "(null)" : target.name;

            var builder = new StringBuilder();
            var stack = new List<Transform>(8);

            for (var t = transform; t != null; t = t.parent)
                stack.Add(t);

            for (var i = stack.Count - 1; i >= 0; i--)
                builder.Append('/').Append(stack[i].name);

            if (target is Component owned)
                builder.Append(" [").Append(owned.GetType().Name).Append(']');

            return builder.ToString();
        }

        /// <summary>
        /// Find the one component type a name refers to.
        ///
        /// A full name always wins over a simple name, because a full name cannot be ambiguous. Two
        /// types sharing a simple name is not a corner case in a real game — types in the global
        /// namespace and duplicated simple names across assemblies are ordinary — so an ambiguous
        /// simple name FAILS with every candidate listed. Picking one would make a passing assertion mean
        /// nothing at all.
        /// </summary>
        public static bool TryResolveComponentType(string name, out Type type, out string error)
        {
            type = null;
            List<Type> simple = null;
            var known = new List<string>(512);

            foreach (var candidate in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (string.Equals(candidate.FullName, name, StringComparison.Ordinal))
                {
                    type = candidate;
                    error = null;
                    return true;
                }

                known.Add(candidate.Name);

                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                    continue;

                simple = simple ?? new List<Type>(4);
                simple.Add(candidate);
            }

            if (simple == null)
            {
                // Matching is case-sensitive on purpose, so the far likeliest mistake is a wrong
                // capital; naming the real spelling beats telling the author to go looking for it.
                error = $"no component type is named '{name}'.{new NameSuggestion().DidYouMean(name, known)} " +
                        "Names are matched exactly; write the full name including its namespace to be unambiguous, " +
                        "and use 'unity command flow.probe --component <name>' to see what is live in the scene";
                return false;
            }

            if (simple.Count > 1)
            {
                var names = new List<string>(simple.Count);
                for (var i = 0; i < simple.Count; i++)
                    names.Add(simple[i].FullName);

                names.Sort(StringComparer.Ordinal);

                error = $"'{name}' is ambiguous: {simple.Count} component types share that simple name " +
                        $"({Join(names)}). Write the full name; nothing will be picked for you";
                return false;
            }

            type = simple[0];
            error = null;
            return true;
        }

        /// <summary>
        /// Every live instance of a component type, optionally scoped to objects matching a name or
        /// hierarchy path. Shared with <c>flow.probe</c> so discovery and assertion see exactly the
        /// same set of objects.
        /// </summary>
        public static List<Component> FindComponents(Type componentType, string find, int max)
        {
            var found = UnityEngine.Object.FindObjectsByType(componentType, FindObjectsInactive.Include, FindObjectsSortMode.None);
            var matches = new List<Component>(found.Length < max ? found.Length : max);
            var normalized = NormalizeMatch(find);

            for (var i = 0; i < found.Length; i++)
            {
                if (!(found[i] is Component component))
                    continue;

                if (normalized != null && !MatchesName(component.gameObject, normalized))
                    continue;

                matches.Add(component);
                if (matches.Count >= max)
                    break;
            }

            return matches;
        }

        /// <summary>Every live GameObject matching a name or hierarchy path.</summary>
        public static List<GameObject> FindGameObjects(string find, int max)
        {
            var found = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var matches = new List<GameObject>();
            var normalized = NormalizeMatch(find);

            for (var i = 0; i < found.Length; i++)
            {
                if (normalized != null && !MatchesName(found[i].gameObject, normalized))
                    continue;

                matches.Add(found[i].gameObject);
                if (matches.Count >= max)
                    break;
            }

            return matches;
        }

        /// <summary>
        /// Resolve a field or property by name on a type, walking the whole hierarchy so a private
        /// member declared on a base class is found — <c>Type.GetField</c> alone does not return
        /// one, which would make <c>field: m_Health</c> report "no such member" about a member that
        /// plainly exists.
        /// </summary>
        public static bool TryFindMember(Type declaring, string name, out MemberInfo member, out Type memberType, out string error)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            member = null;
            memberType = null;

            for (var type = declaring; type != null && type != typeof(object); type = type.BaseType)
            {
                var field = type.GetField(name, Flags);

                PropertyInfo property;
                try
                {
                    property = type.GetProperty(name, Flags);
                }
                catch (AmbiguousMatchException)
                {
                    error = $"'{name}' is declared more than once on {type.FullName}; UnityFlow will not choose between overloads";
                    return false;
                }

                if (field != null && property != null)
                {
                    error = $"{type.FullName} declares both a field and a property named '{name}'; " +
                            "rename one or read it through 'expr'";
                    return false;
                }

                if (field != null)
                {
                    if (field.IsLiteral)
                    {
                        // Waiting for a compile-time constant to change is a flow that can only
                        // ever time out; say so instead of watching it for seven seconds.
                        error = $"'{name}' on {type.FullName} is a const: its value is fixed at compile time, " +
                                "so there is nothing for a flow to wait for";
                        return false;
                    }

                    member = field;
                    memberType = field.FieldType;
                    error = null;
                    return true;
                }

                if (property == null)
                    continue;

                if (property.GetMethod == null)
                {
                    error = $"'{name}' on {type.FullName} is write-only, so there is nothing to assert about it";
                    return false;
                }

                if (property.GetIndexParameters().Length > 0)
                {
                    error = $"'{name}' on {type.FullName} is an indexer and needs arguments; read it through 'expr'";
                    return false;
                }

                member = property;
                memberType = property.PropertyType;
                error = null;
                return true;
            }

            error = $"{declaring.FullName} has no field or property named '{name}'. " +
                    $"{DescribeMembers(declaring, name)}";
            return false;
        }

        private void Prepare()
        {
            switch (m_Query.Mode)
            {
                case StateQueryMode.Expression:
                    PrepareExpression();
                    break;

                case StateQueryMode.Count:
                    PrepareCount();
                    break;

                case StateQueryMode.Existence:
                    PrepareExistence();
                    break;

                default:
                    PrepareMember();
                    break;
            }
        }

        private void PrepareExpression()
        {
            if (!StateExpression.TryCompile(m_Query.Expr, out m_ReadExpression, out var error))
                m_Fatal = error;
        }

        private void PrepareCount()
        {
            // A count names either a component type or a GameObject. The rule is decided ONCE, here,
            // from the name alone, and Explain() always states which reading was used - so a report
            // never leaves a reader wondering what was counted.
            if (m_Query.Count.IndexOf('/') >= 0 || !TryResolveComponentType(m_Query.Count, out m_ComponentType, out _))
            {
                m_ComponentType = null;
                m_MatchName = m_Query.Count;
            }
        }

        private void PrepareExistence()
        {
            if (m_Query.Component != null && !TryResolveComponentType(m_Query.Component, out m_ComponentType, out var error))
            {
                m_Fatal = error;
                return;
            }

            m_MatchName = m_Query.Find;
            m_ExpectedFlag = string.Equals(m_Query.Expected, "true", StringComparison.Ordinal);
        }

        private void PrepareMember()
        {
            Type declaring;

            if (m_Query.Component != null)
            {
                if (!TryResolveComponentType(m_Query.Component, out m_ComponentType, out var typeError))
                {
                    m_Fatal = typeError;
                    return;
                }

                declaring = m_ComponentType;
            }
            else
            {
                declaring = typeof(GameObject);
            }

            if (!TryFindMember(declaring, m_Query.Field, out m_Member, out m_MemberType, out var memberError))
            {
                m_Fatal = memberError;
                return;
            }

            m_MemberIsStatic = m_Member is FieldInfo field
                ? field.IsStatic
                : ((PropertyInfo)m_Member).GetMethod.IsStatic;

            if (m_MemberIsStatic && m_Query.Find != null)
            {
                // Resolving an object and then not using it would be a lie in the report.
                m_Fatal = $"'{m_Query.Field}' is a static member of {m_Member.DeclaringType?.FullName}, so 'find' has " +
                          "nothing to do with the value being read. Remove 'find'";
                return;
            }

            m_MatchName = m_Query.Find;

            if (!TryCompileReader(m_Member, m_MemberType, out m_Read, out var readerError))
                m_Fatal = readerError;
        }

        /// <summary>
        /// Build the per-query reader.
        ///
        /// An expression tree, not <c>FieldInfo.GetValue</c>: reflection boxes the result of every
        /// read, and this runs once per frame for the life of the step. The compiled delegate reads
        /// the member as its own type and hands back a struct, so the steady state is one virtual
        /// call and no garbage.
        /// </summary>
        private static bool TryCompileReader(MemberInfo member, Type memberType, out Func<object, StateValue> reader, out string error)
        {
            reader = null;

            var target = Expression.Parameter(typeof(object), "target");
            var isStatic = member is FieldInfo field ? field.IsStatic : ((PropertyInfo)member).GetMethod.IsStatic;
            var instance = isStatic ? null : (Expression)Expression.Convert(target, member.DeclaringType);

            var access = member is FieldInfo fieldMember
                ? (Expression)Expression.Field(instance, fieldMember)
                : Expression.Property(instance, (PropertyInfo)member);

            Expression body;

            if (memberType.IsEnum)
            {
                var underlying = Expression.Convert(access, Enum.GetUnderlyingType(memberType));
                body = Expression.Call(Factory(nameof(StateValue.OfNumber)), Expression.Convert(underlying, typeof(double)));
            }
            else if (IsNumeric(memberType))
            {
                body = Expression.Call(Factory(nameof(StateValue.OfNumber)), Expression.Convert(access, typeof(double)));
            }
            else if (memberType == typeof(bool))
            {
                body = Expression.Call(Factory(nameof(StateValue.OfBool)), access);
            }
            else if (memberType == typeof(string))
            {
                body = Expression.Call(Factory(nameof(StateValue.OfText)), access);
            }
            else if (!memberType.IsValueType)
            {
                body = Expression.Call(Factory(nameof(StateValue.OfReference)), Expression.Convert(access, typeof(object)));
            }
            else
            {
                error = $"'{member.Name}' is of type {FriendlyTypeName(memberType)}, which a state query cannot compare. " +
                        "Numbers, enums, bool, string and reference types are readable directly; for anything else " +
                        "read one scalar out of it with 'expr', e.g. expr: \"Camera.main.transform.position.y\"";
                return false;
            }

            reader = Expression.Lambda<Func<object, StateValue>>(body, target).Compile();
            error = null;
            return true;
        }

        private static MethodInfo Factory(string name) =>
            typeof(StateValue).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

        private StateOutcome EvaluateValue(in StateValue value)
        {
            m_LastValue = value;
            m_HasLastValue = true;

            if (!TryCoerceExpectation(value.Kind))
                return StateOutcome.Fatal;

            return Compare(value) ? StateOutcome.Satisfied : StateOutcome.NotSatisfied;
        }

        /// <summary>
        /// Coerce the literal the flow wrote into the type of the value actually being read.
        ///
        /// It is done against the READ value's kind rather than against the written text, so
        /// <c>eq: 5</c> means the number five against an int field and the enum member numbered five
        /// against an enum one. The result is cached per kind: a member always reports the same
        /// kind, so this runs once and the retry path never parses anything.
        /// </summary>
        private bool TryCoerceExpectation(StateValueKind kind)
        {
            if (m_ExpectedReady && m_ExpectedKind == kind)
                return true;

            if (!CheckComparisonApplies(kind))
                return false;

            m_ExpectedKind = kind;
            m_ExpectedReady = true;

            if (m_Query.Comparison == StateComparison.Exists)
            {
                m_ExpectedFlag = string.Equals(m_Query.Expected, "true", StringComparison.Ordinal);
                return true;
            }

            switch (kind)
            {
                case StateValueKind.Number:
                    if (m_MemberType != null && m_MemberType.IsEnum)
                        return TryCoerceEnum();

                    if (!double.TryParse(m_Query.Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out m_ExpectedNumber))
                    {
                        m_Fatal = $"'{StateQuery.Keyword(m_Query.Comparison)}: {m_Query.Expected}' is not a number, but " +
                                  $"{Subject()} is {DescribeReadType()}";
                        return false;
                    }

                    return true;

                case StateValueKind.Bool:
                    if (string.Equals(m_Query.Expected, "true", StringComparison.Ordinal))
                    {
                        m_ExpectedFlag = true;
                        return true;
                    }

                    if (string.Equals(m_Query.Expected, "false", StringComparison.Ordinal))
                    {
                        m_ExpectedFlag = false;
                        return true;
                    }

                    m_Fatal = $"'{StateQuery.Keyword(m_Query.Comparison)}: {m_Query.Expected}' is not true or false, but " +
                              $"{Subject()} is a bool";
                    return false;

                default:
                    m_ExpectedText = m_Query.Expected;
                    return true;
            }
        }

        private bool TryCoerceEnum()
        {
            var names = Enum.GetNames(m_MemberType);
            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(names[i], m_Query.Expected, StringComparison.Ordinal))
                    continue;

                var member = Enum.Parse(m_MemberType, names[i], false);
                m_ExpectedNumber = Convert.ToDouble(Convert.ChangeType(member, Enum.GetUnderlyingType(m_MemberType), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                return true;
            }

            if (double.TryParse(m_Query.Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out m_ExpectedNumber))
                return true;

            var list = new List<string>(names);
            list.Sort(StringComparer.Ordinal);

            m_Fatal = $"'{m_Query.Expected}' is not a member of {m_MemberType.FullName}. Members: {Join(list)}";
            return false;
        }

        private bool CheckComparisonApplies(StateValueKind kind)
        {
            var comparison = m_Query.Comparison;

            if (comparison == StateComparison.Exists)
            {
                if (kind == StateValueKind.Text || kind == StateValueKind.Reference || kind == StateValueKind.Missing)
                    return true;

                m_Fatal = $"'exists' asks whether a value is absent, but {Subject()} is {DescribeReadType()} and is " +
                          "never absent. Compare its value instead";
                return false;
            }

            if (StateQuery.IsOrdering(comparison) && kind != StateValueKind.Number)
            {
                m_Fatal = $"'{StateQuery.Keyword(comparison)}' orders numbers, but {Subject()} is {DescribeReadType()}";
                return false;
            }

            if (comparison == StateComparison.Contains && kind != StateValueKind.Text)
            {
                m_Fatal = $"'contains' looks inside text, but {Subject()} is {DescribeReadType()}";
                return false;
            }

            if (kind == StateValueKind.Reference)
            {
                m_Fatal = $"{Subject()} is {DescribeReadType()}, which can only be tested with 'exists'. " +
                          "Read something scalar off it instead, e.g. field: name";
                return false;
            }

            return true;
        }

        private bool Compare(in StateValue value)
        {
            switch (m_Query.Comparison)
            {
                case StateComparison.Exists:
                    return !value.IsNull == m_ExpectedFlag;

                case StateComparison.Contains:
                    return value.Text != null &&
                           value.Text.IndexOf(m_ExpectedText, StringComparison.OrdinalIgnoreCase) >= 0;

                case StateComparison.Gt:
                    return value.Number > m_ExpectedNumber;

                case StateComparison.Gte:
                    return value.Number >= m_ExpectedNumber;

                case StateComparison.Lt:
                    return value.Number < m_ExpectedNumber;

                case StateComparison.Lte:
                    return value.Number <= m_ExpectedNumber;

                case StateComparison.Ne:
                    return !Equal(value);

                default:
                    return Equal(value);
            }
        }

        private bool Equal(in StateValue value)
        {
            switch (value.Kind)
            {
                case StateValueKind.Number:
                    var scale = Math.Abs(m_ExpectedNumber);
                    return Math.Abs(value.Number - m_ExpectedNumber) <= NumberTolerance * (scale < 1.0 ? 1.0 : scale);

                case StateValueKind.Bool:
                    return value.Flag == m_ExpectedFlag;

                default:
                    return string.Equals(value.Text, m_ExpectedText, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Resolve the one object the member is read from, and keep it.
        ///
        /// The cached reference is re-validated with Unity's null check, which reports true for a
        /// destroyed object, so a warp or a pooled respawn drops the stale target and searches
        /// again instead of reading a dead C# reference.
        /// </summary>
        private bool TryResolveTarget()
        {
            if (m_Target != null)
                return true;

            m_Target = null;

            if (m_ComponentType != null)
                return Accept(FindComponents(m_ComponentType, m_MatchName, MaxListedCandidates + 1));

            return Accept(FindGameObjects(m_MatchName, MaxListedCandidates + 1));
        }

        private bool Accept<T>(List<T> candidates) where T : UnityEngine.Object
        {
            if (candidates.Count == 0)
                return false;

            if (candidates.Count > 1)
            {
                var paths = new List<string>(candidates.Count);
                for (var i = 0; i < candidates.Count; i++)
                    paths.Add(PathOf(candidates[i]));

                // The search stopped at MaxListedCandidates + 1, so an exact total is unknown and
                // claiming one would be a made-up number in a failure report.
                var howMany = candidates.Count > MaxListedCandidates
                    ? $"more than {MaxListedCandidates}"
                    : candidates.Count.ToString(CultureInfo.InvariantCulture);

                m_Fatal = $"{m_Query.Describe()} matches {howMany} live objects ({Join(paths)}); " +
                          "UnityFlow will not choose one. Narrow it with a hierarchy path, e.g. find: \"/UI Root/HUD/Health\"";
                return false;
            }

            m_Target = candidates[0];
            return true;
        }

        /// <summary>
        /// Census of everything matching. Allocates every call by necessity: the answer is what is
        /// being watched, so it cannot be cached, and Unity's scene-wide search returns an array.
        /// </summary>
        private int CountMatches()
        {
            if (m_ComponentType != null)
                return FindComponents(m_ComponentType, m_MatchName, int.MaxValue).Count;

            return FindGameObjects(m_MatchName, int.MaxValue).Count;
        }

        private string CountSubject()
        {
            var plural = m_LastCount == 1 ? string.Empty : "s";

            // Always names WHICH reading of 'count' was used, so a report never leaves a reader
            // guessing whether a type or a GameObject name was counted.
            return m_ComponentType != null
                ? $"live {m_ComponentType.FullName} component{plural}"
                : $"live GameObject{plural} with that name";
        }

        private string Subject() =>
            m_Query.Field != null ? $"'{m_Query.Field}'" : m_Query.Describe();

        private string DescribeReadType()
        {
            if (m_MemberType != null)
                return $"of type {FriendlyTypeName(m_MemberType)}";

            if (!m_HasLastValue)
                return "of an unknown type";

            switch (m_LastValue.Kind)
            {
                case StateValueKind.Number: return "a number";
                case StateValueKind.Bool: return "a bool";
                case StateValueKind.Text: return "text";
                case StateValueKind.Reference: return m_LastValue.Reference == null ? "a reference" : $"a {m_LastValue.Reference.GetType().Name}";
                default: return "nothing";
            }
        }

        private string FormatLast() =>
            m_HasLastValue ? m_LastValue.Format(m_MemberType) : "never read";

        /// <summary>
        /// Whether the tolerance is worth mentioning. Only for a real-valued equality: on a count,
        /// or on an enum, saying "compared with a tolerance" would be noise about a whole number.
        /// </summary>
        private bool UsesTolerance()
        {
            if (!m_HasLastValue || m_LastValue.Kind != StateValueKind.Number)
                return false;

            if (m_Query.Mode == StateQueryMode.Count || (m_MemberType != null && m_MemberType.IsEnum))
                return false;

            return m_Query.Comparison == StateComparison.Eq ||
                   m_Query.Comparison == StateComparison.Is ||
                   m_Query.Comparison == StateComparison.Ne;
        }

        /// <summary>
        /// A written match is a NAME when it has no slash in it and a full hierarchy path when it
        /// has. There is no partial-path matching: a path either is the object's path or it is not.
        /// </summary>
        private static bool MatchesName(GameObject gameObject, string normalized)
        {
            if (normalized[0] != '/')
                return string.Equals(gameObject.name, normalized, StringComparison.Ordinal);

            return string.Equals(PathOf(gameObject), normalized, StringComparison.Ordinal);
        }

        private static string NormalizeMatch(string find)
        {
            if (find == null || find.IndexOf('/') < 0)
                return find;

            // Paths are reported as "/Root/Child" everywhere else in UnityFlow; accepting the same
            // path written without its leading slash is spelling, not a second matching rule.
            return find[0] == '/' ? find : "/" + find;
        }

        private static bool IsNumeric(Type type) =>
            type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong) ||
            type == typeof(float) || type == typeof(double) || type == typeof(decimal);

        private static string DescribeMembers(Type declaring, string typed)
        {
            var names = new List<string>(32);

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            for (var type = declaring; type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(Flags))
                    names.Add(field.Name);

                foreach (var property in type.GetProperties(Flags))
                {
                    if (property.GetMethod != null && property.GetIndexParameters().Length == 0)
                        names.Add(property.Name);
                }
            }

            names.Sort(StringComparer.Ordinal);

            var suggestion = new NameSuggestion().DidYouMean(typed, names);
            if (suggestion.Length > 0)
                return suggestion.TrimStart();

            return $"Readable members: {new NameSuggestion().Join(names, 20)}. " +
                   "'unity command flow.probe' prints them with their current values";
        }

        private static string Join(List<string> names)
        {
            var builder = new StringBuilder();
            var shown = names.Count < MaxListedCandidates ? names.Count : MaxListedCandidates;

            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(names[i]);
            }

            if (names.Count > shown)
                builder.Append(", ... (").Append(names.Count - shown).Append(" more)");

            return builder.ToString();
        }
    }

    /// <summary>
    /// The <c>expr</c> escape hatch: an author-written C# expression turned into a delegate.
    ///
    /// The compiling itself belongs to <see cref="Compilation.FlowCompiler"/>, which is shared with
    /// <c>runScript</c> and is where the two properties this verb depends on live: an expression is
    /// compiled ONCE per distinct text — a <c>waitUntil</c> polling for five seconds re-reads it
    /// ~300 times and every one of those is a delegate call — and the compiler is not made to
    /// re-decode the project's 355 assemblies to do it.
    /// </summary>
    internal static class StateExpression
    {
        public static bool TryCompile(string expression, out Func<StateValue> reader, out string error) =>
            Compilation.FlowCompiler.TryCompileExpression(expression, out reader, out error);
    }
}
