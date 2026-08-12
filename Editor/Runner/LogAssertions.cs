using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityFlow.Editor.Report;

namespace UnityFlow.Editor.Runner
{
    /// <summary>
    /// How far back a log assertion looks.
    ///
    /// A log verb is meaningless without this: the interesting question is almost never "did
    /// anything log since this very step began" — the action that should have logged ran in an
    /// EARLIER step.
    /// </summary>
    public enum LogScope
    {
        /// <summary>Only what was logged after this step started. For waiting on something still to come.</summary>
        Step,

        /// <summary>Since the previous step began — "I clicked, did it log?", which is the usual question.</summary>
        Previous,

        /// <summary>Everything captured since the run started. The scope for a blanket "no errors" check.</summary>
        Run
    }

    /// <summary>
    /// Assertions over the Unity console.
    ///
    /// The capture already exists: <see cref="ConsoleRing"/> has been recording every message since
    /// the run began, because that is what puts the game's own exception next to the step it broke
    /// in a failure report. These verbs just let a flow assert on it.
    ///
    /// Two caveats belong in the docs rather than in a surprised bug report. Unity's own native
    /// errors — the "Assertion failed on expression" kind — never reach
    /// Application.logMessageReceived, so they are invisible here. And a project whose logging is
    /// [Conditional] on a define (a very common wrapper shape) strips those calls entirely on
    /// platforms that do not set it, so a flow asserting on an info-level line can pass in the
    /// Editor and fail on device. That is a property of the project, not of this verb.
    /// </summary>
    public static class LogAssertions
    {
        /// <summary>How long a negative assertion must keep holding before it counts.</summary>
        public static readonly TimeSpan DefaultStableFor = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Wait until a matching message appears, or fail at the deadline.
        /// Positive assertion: retries, exactly like assertVisible.
        /// </summary>
        public static IEnumerator AssertLog(StepContext ctx)
        {
            if (!TryReadMatcher(ctx, out var matcher, out var error))
            {
                ctx.Fail(error);
                yield break;
            }

            var since = ResolveCursor(ctx, matcher.Scope);

            while (!ctx.DeadlineReached)
            {
                if (Find(ctx, since, matcher, out _))
                    yield break;

                yield return null;
            }

            ctx.Fail(
                $"assertLog timed out: no {matcher.Describe()} was logged {DescribeScope(matcher.Scope)}",
                DescribeCaptured(ctx, since) + ctx.BuildDiagnostics());
        }

        /// <summary>
        /// Assert that NO matching message appears, and keep checking for a window.
        ///
        /// Same asymmetry as assertNotVisible, for the same reason: a negative assertion that
        /// returned the moment it looked true would pass before the system under test had even
        /// reacted, and a regression that logs the error 200ms later would still show green.
        /// </summary>
        public static IEnumerator AssertNoLog(StepContext ctx)
        {
            if (!TryReadMatcher(ctx, out var matcher, out var error))
            {
                ctx.Fail(error);
                yield break;
            }

            var since = ResolveCursor(ctx, matcher.Scope);
            var window = (ctx.Step.Has("stableFor") ? ctx.Step.Get<TimeSpan>("stableFor") : DefaultStableFor).TotalSeconds;
            var start = FlowClock.Now;

            while (true)
            {
                if (Find(ctx, since, matcher, out var offender))
                {
                    var elapsed = (FlowClock.Now - start) * 1000.0;
                    ctx.Fail(
                        $"assertNoLog failed: [{offender.Type}] {Truncate(offender.Message, 160)} " +
                        $"was logged after {elapsed:F0}ms (nothing matching {matcher.Describe()} was allowed " +
                        $"{DescribeScope(matcher.Scope)} for {window * 1000:F0}ms)",
                        FirstFrame(offender) + ctx.BuildDiagnostics());
                    yield break;
                }

                if (FlowClock.Now - start >= window)
                    yield break;

                yield return null;
            }
        }

        private readonly struct Matcher
        {
            public readonly LogType? Level;
            public readonly string Contains;
            public readonly Regex Pattern;
            public readonly LogScope Scope;

            public Matcher(LogType? level, string contains, Regex pattern, LogScope scope)
            {
                Level = level;
                Contains = contains;
                Pattern = pattern;
                Scope = scope;
            }

            public bool Matches(in ConsoleEntry entry)
            {
                if (Level.HasValue && entry.Type != Level.Value)
                    return false;

                if (Contains != null && entry.Message.IndexOf(Contains, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                return Pattern == null || Pattern.IsMatch(entry.Message);
            }

            public string Describe()
            {
                var sb = new StringBuilder();
                sb.Append(Level.HasValue ? Level.Value.ToString() : "message");

                if (Contains != null)
                    sb.Append(" containing \"").Append(Contains).Append('"');

                if (Pattern != null)
                    sb.Append(" matching /").Append(Pattern).Append('/');

                return sb.ToString();
            }
        }

        private static bool TryReadMatcher(StepContext ctx, out Matcher matcher, out string error)
        {
            matcher = default;

            LogType? level = null;
            if (ctx.Step.Has("level"))
            {
                var name = ctx.Step.Get<string>("level");
                if (!Enum.TryParse<LogType>(name, ignoreCase: true, out var parsed))
                {
                    error = $"'{name}' is not a log level. Valid values: {string.Join(", ", Enum.GetNames(typeof(LogType)))}";
                    return false;
                }

                level = parsed;
            }

            var contains = ctx.Step.Has("contains") ? ctx.Step.Get<string>("contains") : null;

            Regex pattern = null;
            if (ctx.Step.Has("matches"))
            {
                var source = ctx.Step.Get<string>("matches");
                try
                {
                    // Same timeout the selector patterns carry: this is evaluated on the retry path,
                    // and a catastrophic pattern would otherwise hang the editor with no way in.
                    pattern = new Regex(source, RegexOptions.None, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException exception)
                {
                    error = $"'matches' is not a valid regular expression: {exception.Message}";
                    return false;
                }
            }

            if (level == null && contains == null && pattern == null)
            {
                error = "a log assertion needs at least one of 'level', 'contains' or 'matches'; " +
                        "matching every message would assert nothing";
                return false;
            }

            var scope = LogScope.Previous;
            if (ctx.Step.Has("since"))
            {
                var name = ctx.Step.Get<string>("since");
                if (!Enum.TryParse<LogScope>(name, ignoreCase: true, out scope))
                {
                    error = $"'{name}' is not a scope. Valid values: step, previous, run";
                    return false;
                }
            }

            matcher = new Matcher(level, contains, pattern, scope);
            error = null;
            return true;
        }

        private static int ResolveCursor(StepContext ctx, LogScope scope)
        {
            switch (scope)
            {
                case LogScope.Step: return ctx.ConsoleCursorAtStart;
                case LogScope.Run: return 0;
                default: return ctx.ConsoleCursorBeforePreviousStep;
            }
        }

        private static bool Find(StepContext ctx, int since, in Matcher matcher, out ConsoleEntry found)
        {
            var entries = ctx.Console.Since(since);
            for (var i = 0; i < entries.Count; i++)
            {
                if (matcher.Matches(entries[i]))
                {
                    found = entries[i];
                    return true;
                }
            }

            found = default;
            return false;
        }

        private static string DescribeScope(LogScope scope)
        {
            switch (scope)
            {
                case LogScope.Step: return "since this step began";
                case LogScope.Run: return "at any point in this run";
                default: return "since the previous step began";
            }
        }

        /// <summary>
        /// What WAS captured in the window, so a failing log assertion says more than "not found".
        /// The dropped count matters: a quiet report and a truncated one look identical otherwise.
        /// </summary>
        private static string DescribeCaptured(StepContext ctx, int since)
        {
            var entries = ctx.Console.Since(since);
            var sb = new StringBuilder("\n  Console in that window:");

            if (entries.Count == 0)
            {
                sb.Append("\n    (nothing was logged at all)");
            }
            else
            {
                for (var i = 0; i < entries.Count && i < 12; i++)
                    sb.Append("\n    [").Append(entries[i].Type).Append("] ").Append(Truncate(entries[i].Message, 140));

                if (entries.Count > 12)
                    sb.Append("\n    ... and ").Append(entries.Count - 12).Append(" more");
            }

            var dropped = ctx.Console.DroppedSince(since);
            if (dropped > 0)
                sb.Append("\n  (").Append(dropped).Append(" earlier messages were dropped by the ring buffer)");

            return sb.ToString();
        }

        private static string FirstFrame(in ConsoleEntry entry)
        {
            if (string.IsNullOrEmpty(entry.StackTrace))
                return string.Empty;

            foreach (var line in entry.StackTrace.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("UnityEngine.Debug", StringComparison.Ordinal))
                    return "\n  at " + trimmed;
            }

            return string.Empty;
        }

        private static string Truncate(string value, int max) =>
            value != null && value.Length > max ? value.Substring(0, max) + "..." : value;
    }
}
