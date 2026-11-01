using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>What an action in a step does to its word's button.</summary>
    internal enum ChatActionKind { Press, Hold, Release }

    /// <summary>One action in a step. <see cref="DurationMs"/> is -1 when the
    /// action has none: a hold that lasts until released or until the
    /// command ends, or a release that stays released.</summary>
    internal readonly record struct ChatAction(int Button, ChatActionKind Kind, int DurationMs);

    /// <summary>One step of a command: actions that start together, or a
    /// pause when <see cref="Actions"/> is empty.</summary>
    internal sealed class ChatStep
    {
        public ChatAction[] Actions { get; init; } = Array.Empty<ChatAction>();
        public int DelayMs { get; init; }
    }

    /// <summary>One press or release on the timeline a command plays.</summary>
    internal readonly record struct ChatEvent(int AtMs, int Button, bool Down);

    /// <summary>
    /// Yuriy's command language from ForgePlay (src/main/input/CommandParser.js,
    /// issue #555): words separated by spaces play in order, "a+b" presses both,
    /// "a500ms" or "a1s" sets the hold (200 ms otherwise), "_a" holds until
    /// released or the command ends, "-a" releases, "#500ms" pauses, "[a b]*3"
    /// repeats a group and "[a+b]500ms" times one.
    ///
    /// <para>ForgePlay sets no limit. Here a command plays at most
    /// <see cref="MaxCommandMs"/> and <see cref="MaxSteps"/> steps, repeats
    /// included, so one viewer cannot hold the game for minutes. A command
    /// over a limit, or with a word the streamer has not added, does not
    /// play at all.</para>
    /// </summary>
    internal static class ChatCommandParser
    {
        /// <summary>A press with no time of its own (ForgePlay's
        /// DEFAULT_DURATION).</summary>
        public const int DefaultPressMs = 200;

        /// <summary>The longest a command plays.</summary>
        public const int MaxCommandMs = 10_000;

        /// <summary>The most steps a command plays, repeats included.</summary>
        public const int MaxSteps = 50;

        /// <summary>How deep groups nest.</summary>
        public const int MaxNesting = 4;

        /// <summary>Twitch's own limit on a chat message.</summary>
        public const int MaxLength = 500;

        // ForgePlay's two patterns: a time at the end of a token, and a whole
        // token that is a time.
        private static readonly Regex s_trailingTime = new(@"(\d+(?:\.\d+)?)(ms|s)$", RegexOptions.CultureInvariant);
        private static readonly Regex s_time = new(@"^(\d+(?:\.\d+)?)(ms|s)$", RegexOptions.CultureInvariant);
        private static readonly Regex s_repeat = new(@"^\*(\d+)$", RegexOptions.CultureInvariant);

        private sealed class Rejected : Exception { }

        /// <summary>The steps for the text after "!", or null when the row
        /// does not play it. <paramref name="buttonFor"/> gives a word's
        /// button, below 1 for a word the list does not have.</summary>
        public static IReadOnlyList<ChatStep> Parse(string text, Func<string, int> buttonFor)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || buttonFor == null) return null;
            try
            {
                var steps = new List<ChatStep>();
                ParseTokens(Tokenize(text.Trim()), null, steps, buttonFor);
                if (steps.Count == 0 || Duration(steps) > MaxCommandMs) return null;
                return steps;
            }
            catch (Rejected)
            {
                return null;
            }
        }

        /// <summary>How long the steps play: each step lasts as long as its
        /// longest timed action (ForgePlay's executor waits for all of
        /// them), a pause as long as its time.</summary>
        public static int Duration(IReadOnlyList<ChatStep> steps)
        {
            long total = 0;
            foreach (var step in steps)
                total += StepMs(step);
            return (int)Math.Min(total, int.MaxValue);
        }

        private static int StepMs(ChatStep step)
        {
            if (step.Actions.Length == 0) return step.DelayMs;
            int longest = 0;
            foreach (var action in step.Actions)
                if (action.DurationMs > longest) longest = action.DurationMs;
            return longest;
        }

        /// <summary>The presses and releases the steps play, in order, and
        /// the time the command ends. Every button still down at the end is
        /// released then, ForgePlay's reset.</summary>
        public static List<ChatEvent> Timeline(IReadOnlyList<ChatStep> steps, out int endMs)
        {
            var events = new List<ChatEvent>();
            int t = 0;
            foreach (var step in steps)
            {
                if (step.Actions.Length == 0)
                {
                    t += step.DelayMs;
                    continue;
                }
                int stepEnd = t;
                foreach (var action in step.Actions)
                {
                    int d = action.DurationMs;
                    if (action.Kind == ChatActionKind.Release)
                    {
                        // A timed release presses the button again after its
                        // time, ForgePlay's executor.
                        events.Add(new ChatEvent(t, action.Button, false));
                        if (d >= 0) events.Add(new ChatEvent(t + d, action.Button, true));
                    }
                    else
                    {
                        events.Add(new ChatEvent(t, action.Button, true));
                        if (d >= 0) events.Add(new ChatEvent(t + d, action.Button, false));
                    }
                    if (d > 0 && t + d > stepEnd) stepEnd = t + d;
                }
                t = stepEnd;
            }
            endMs = t;
            // Stable by time, so presses and releases that share a moment keep
            // the order the command gave them.
            var ordered = new List<ChatEvent>(events.Count);
            var indices = new int[events.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (x, y) =>
            {
                int c = events[x].AtMs.CompareTo(events[y].AtMs);
                return c != 0 ? c : x.CompareTo(y);
            });
            foreach (int i in indices) ordered.Add(events[i]);
            return ordered;
        }

        private static List<string> Tokenize(string input)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();
            int depth = 0;
            foreach (char c in input)
            {
                if (c == '[')
                {
                    if (++depth > MaxNesting) throw new Rejected();
                    current.Append(c);
                    continue;
                }
                if (c == ']')
                {
                    if (--depth < 0) throw new Rejected();
                    current.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (depth != 0) throw new Rejected();
            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        private static void ParseTokens(List<string> tokens, int? groupMs, List<ChatStep> steps, Func<string, int> buttonFor)
        {
            foreach (string token in tokens)
            {
                if (token.StartsWith('[') && token.Contains(']'))
                {
                    ParseGroup(token, steps, buttonFor);
                    continue;
                }
                Add(steps, ParseToken(token, groupMs, buttonFor));
            }
        }

        private static void Add(List<ChatStep> steps, ChatStep step)
        {
            if (step == null) return;
            if (steps.Count >= MaxSteps) throw new Rejected();
            steps.Add(step);
        }

        private static void ParseGroup(string token, List<ChatStep> steps, Func<string, int> buttonFor)
        {
            int closing = ClosingBracket(token);
            if (closing < 0) throw new Rejected();
            string content = token.Substring(1, closing - 1);
            string suffix = token.Substring(closing + 1);
            var inner = Tokenize(content);

            var repeat = s_repeat.Match(suffix);
            if (repeat.Success)
            {
                if (!int.TryParse(repeat.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                    || count <= 0 || count > MaxSteps)
                    throw new Rejected();
                for (int i = 0; i < count; i++)
                    ParseTokens(inner, null, steps, buttonFor);
                return;
            }

            int? groupMs = null;
            if (suffix.Length > 0)
            {
                int ms = ParseTime(suffix);
                if (ms < 0) throw new Rejected();
                groupMs = ms;
            }
            ParseTokens(inner, groupMs, steps, buttonFor);
        }

        private static int ClosingBracket(string token)
        {
            int depth = 0;
            for (int i = 0; i < token.Length; i++)
            {
                if (token[i] == '[') depth++;
                if (token[i] == ']' && --depth == 0) return i;
            }
            return -1;
        }

        private static ChatStep ParseToken(string token, int? groupMs, Func<string, int> buttonFor)
        {
            if (token.Length == 0) return null;
            if (token[0] == '#')
            {
                int ms = ParseTime(token.Substring(1));
                if (ms < 0) throw new Rejected();
                return new ChatStep { DelayMs = ms };
            }
            string[] parts = token.Split('+');
            var actions = new ChatAction[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) throw new Rejected();
                actions[i] = ParseAction(parts[i], groupMs, buttonFor);
            }
            return new ChatStep { Actions = actions };
        }

        private static ChatAction ParseAction(string token, int? groupMs, Func<string, int> buttonFor)
        {
            var kind = ChatActionKind.Press;
            string input = token;
            if (input.StartsWith('_')) { kind = ChatActionKind.Hold; input = input.Substring(1); }
            else if (input.StartsWith('-')) { kind = ChatActionKind.Release; input = input.Substring(1); }
            if (input.Length == 0) throw new Rejected();

            int duration = -1;
            var time = s_trailingTime.Match(input);
            if (time.Success)
            {
                duration = ParseTime(time.Value);
                if (duration < 0) throw new Rejected();
                input = input.Substring(0, time.Index);
            }
            else if (groupMs.HasValue)
            {
                duration = groupMs.Value;
            }

            int button = buttonFor(ChatCommandRegistry.Normalize(input));
            if (button < 1) throw new Rejected();
            if (kind == ChatActionKind.Press && duration < 0) duration = DefaultPressMs;
            return new ChatAction(button, kind, duration);
        }

        /// <summary>Milliseconds for "500ms" or "1.5s", or -1. A time past
        /// the command limit is refused here, before it can overflow a
        /// sum.</summary>
        private static int ParseTime(string value)
        {
            var match = s_time.Match(value ?? string.Empty);
            if (!match.Success) return -1;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount))
                return -1;
            double ms = match.Groups[2].Value == "s" ? amount * 1000 : amount;
            if (ms > MaxCommandMs) return -1;
            return (int)Math.Round(ms);
        }
    }
}
