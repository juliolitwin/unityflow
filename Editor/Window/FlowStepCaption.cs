using System;
using System.Globalization;
using System.Text;
using UnityFlow.Editor.Model;

namespace UnityFlow.Editor.Window
{
    /// <summary>
    /// The one argument that IDENTIFIES a step, for the row that shows it.
    ///
    /// A step list whose rows read "assert", "assert", "assert" tells the reader nothing, and six of
    /// those in a row is what a real flow looks like. What distinguishes them is always the same
    /// thing the author typed to say WHICH one: the selector for a UI verb, the text for inputText,
    /// the queried member for a state verb, the key for press. This turns a parsed step into exactly
    /// that string and nothing else — the verb is rendered separately, so repeating it here would
    /// only steal width from the part that varies.
    ///
    /// It is a pure function of the parsed step so it can be tested without an editor, and so the
    /// same caption can be reused by anything else that has to name a step.
    /// </summary>
    public static class FlowStepCaption
    {
        /// <summary>
        /// What to show after the verb. Empty when the verb genuinely takes no argument
        /// (<c>exitPlayMode</c>), never a placeholder — an empty column reads as "nothing to say"
        /// while a placeholder reads as a value.
        /// </summary>
        public static string Argument(FlowStep step)
        {
            if (step == null)
                throw new ArgumentNullException(nameof(step));

            var caption = Body(step);

            // 'on:' narrows where the selector is allowed to resolve, so a row without it describes
            // a different step from the one that was written.
            if (step.On != null)
                caption = caption.Length == 0 ? "on " + step.On : caption + "  on " + step.On;

            return caption;
        }

        private static string Body(FlowStep step)
        {
            switch (step.Verb)
            {
                case "assert":
                case "waitUntil":
                    return StateQuery(step);

                case "inputText":
                    return Quote(Value(step, "text")) + " " + Arrow + " " + step.Selector;

                case "assertText":
                    return step.Selector + " " + TextCriterion(step);

                case "drag":
                    return Endpoint(step, "from", "fromPoint") + " " + Arrow + " " + Endpoint(step, "to", "toPoint");

                case "press":
                    return Key(step);

                case "runScript":
                    return FirstLine(Value(step, "code"));

                case "runFlow":
                    return Value(step, "file");

                case "screenshot":
                    return Value(step, "name");

                case "wait":
                    return Value(step, "duration");

                case "enterPlayMode":
                    return Value(step, "scene");
            }

            // Everything else is either a selector verb or a project [FlowCommand], and both are
            // identified by what the author wrote after the verb.
            return step.Selector != null ? step.Selector.ToString() : Arguments(step);
        }

        /// <summary>
        /// What <c>assert</c> and <c>waitUntil</c> are actually asking, as one readable claim:
        /// <c>PlayButton.Button.interactable is true</c>.
        /// </summary>
        private static string StateQuery(FlowStep step)
        {
            var claim = new StringBuilder(Subject(step));

            // 'exists' asks about presence rather than value, so it reads as a phrase and not as a
            // comparison against the words true and false.
            if (step.TryGet<bool>("exists", out var exists))
                claim.Append(exists ? " exists" : " does not exist");

            foreach (var comparison in Comparisons)
            {
                if (!step.TryGetArg(comparison.Key, out var argument))
                    continue;

                if (claim.Length > 0)
                    claim.Append(' ');

                claim.Append(comparison.Symbol).Append(' ').Append(Text(argument));
                break;
            }

            if (step.TryGetArg("stableFor", out var stable))
                claim.Append(" (stable for ").Append(Text(stable)).Append(')');

            return claim.ToString();
        }

        /// <summary>What the query READS: an expression, a population count, or a member of an object.</summary>
        private static string Subject(FlowStep step)
        {
            if (step.Has("expr"))
                return Value(step, "expr");

            if (step.Has("count"))
                return "count of " + Value(step, "count");

            var subject = new StringBuilder();

            Join(subject, Value(step, "find"));
            Join(subject, Value(step, "component"));
            Join(subject, Value(step, "field"));

            return subject.ToString();
        }

        private static void Join(StringBuilder builder, string part)
        {
            if (part.Length == 0)
                return;

            if (builder.Length > 0)
                builder.Append('.');

            builder.Append(part);
        }

        /// <summary>Which of assertText's three mutually exclusive criteria was written.</summary>
        private static string TextCriterion(FlowStep step)
        {
            if (step.Has("equals"))
                return "= " + Quote(Value(step, "equals"));

            if (step.Has("contains"))
                return "contains " + Quote(Value(step, "contains"));

            if (step.Has("matches"))
                return "matches /" + Value(step, "matches") + "/";

            return string.Empty;
        }

        /// <summary>One end of a drag: a selector, or the bare screen point used when there is no node.</summary>
        private static string Endpoint(FlowStep step, string selectorArg, string pointArg) =>
            step.Has(selectorArg) ? Value(step, selectorArg) : Value(step, pointArg);

        private static string Key(FlowStep step)
        {
            var key = new StringBuilder(Value(step, "key"));

            if (step.TryGet<int>("count", out var count))
                key.Append(" x").Append(count.ToString(CultureInfo.InvariantCulture));

            // A held key is a different gesture from a tap, and the duration is the only thing that
            // says which one this is.
            if (step.Has("duration"))
                key.Append(" for ").Append(Value(step, "duration"));

            return key.ToString();
        }

        /// <summary>Whatever a project command was given, since its argument names are its own.</summary>
        private static string Arguments(FlowStep step)
        {
            if (step.Args.Count == 1)
                return Text(step.Args[0]);

            var text = new StringBuilder();

            for (var i = 0; i < step.Args.Count; i++)
            {
                if (text.Length > 0)
                    text.Append(' ');

                text.Append(step.Args[i].Name).Append(": ").Append(Text(step.Args[i]));
            }

            return text.ToString();
        }

        private static string Value(FlowStep step, string name) =>
            step.TryGetArg(name, out var argument) ? Text(argument) : string.Empty;

        /// <summary>
        /// An argument as the author wrote it.
        ///
        /// A comparison argument is <see cref="FlowArgKind.Any"/>, so it arrives as the raw
        /// <see cref="FlowValue"/> rather than a converted value — deliberately, because the type it
        /// must be is the type of the field it will be compared with. Its scalar text is therefore
        /// the only honest rendering.
        /// </summary>
        private static string Text(FlowArgument argument)
        {
            if (argument.IsReference)
                return "@" + argument.Reference;

            switch (argument.Value)
            {
                case null:
                    return string.Empty;
                case FlowValue raw:
                    return raw.Kind == FlowValueKind.Scalar ? raw.Scalar : raw.Describe();
                case Selector selector:
                    return selector.ToString();
                case TimeSpan duration:
                    return Duration(duration);
                case float number:
                    return number.ToString("0.###", CultureInfo.InvariantCulture);
                case bool flag:
                    return flag ? "true" : "false";
                default:
                    return Convert.ToString(argument.Value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Durations the way a flow author writes them, so the row matches the file.</summary>
        public static string Duration(TimeSpan span)
        {
            var milliseconds = span.TotalMilliseconds;

            if (milliseconds < 1000)
                return milliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms";

            return (milliseconds / 1000).ToString("0.###", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>A script's first real line. The rest is a body, and a body is not an identifier.</summary>
        private static string FirstLine(string code)
        {
            foreach (var line in code.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }

            return string.Empty;
        }

        private static string Quote(string text) => "\"" + text + "\"";

        /// <summary>Reads as a movement, which is what inputText and drag both are.</summary>
        private const string Arrow = "→";

        /// <summary>
        /// The comparisons a state query can carry, in the order they are looked for. Only one is
        /// ever written — the resolver refuses a query with two — so the first hit is the claim.
        /// </summary>
        private static readonly (string Key, string Symbol)[] Comparisons =
        {
            ("is", "is"),
            ("eq", "=="),
            ("ne", "!="),
            ("gte", ">="),
            ("lte", "<="),
            ("gt", ">"),
            ("lt", "<"),
            ("contains", "contains")
        };
    }
}
