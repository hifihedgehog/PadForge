using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Services.Automation
{
    /// <summary>One word of a line: a bare word, a quoted text, or a
    /// <c>key=value</c> pair whose value may be quoted.</summary>
    /// <param name="Key">The part before the first unquoted <c>=</c>, or
    /// null for a word with none.</param>
    /// <param name="Value">The word, or the value after the <c>=</c>, with
    /// its quotes taken off and its escapes read.</param>
    /// <param name="Raw">The word as it came, quotes and all.</param>
    internal readonly record struct AutomationWord(string Key, string Value, string Raw)
    {
        public bool IsPair => Key != null;
    }

    /// <summary>A line read into its parts.</summary>
    internal sealed class AutomationRequest
    {
        /// <summary>The tag the line opened with, <c>@7</c>, or null.</summary>
        public string Tag { get; init; }

        /// <summary>The verb in lower case.</summary>
        public string Verb { get; init; }

        /// <summary>The words after the verb.</summary>
        public IReadOnlyList<AutomationWord> Words { get; init; } = Array.Empty<AutomationWord>();

        /// <summary>The JSON object the line ends with, from its opening
        /// brace, or null.</summary>
        public string Json { get; init; }

        /// <summary>The line after the tag, as it came.</summary>
        public string Body { get; init; }

        /// <summary>The line's error when it could not be read, or
        /// null.</summary>
        public string Error { get; init; }

        /// <summary>The value of <paramref name="key"/>, or null.</summary>
        public string Get(string key)
        {
            foreach (var word in Words)
                if (word.IsPair && string.Equals(word.Key, key, StringComparison.OrdinalIgnoreCase)) return word.Value;
            return null;
        }

        /// <summary>The bare words, in order.</summary>
        public List<string> Bare()
        {
            var list = new List<string>();
            foreach (var word in Words) if (!word.IsPair) list.Add(word.Value);
            return list;
        }
    }

    /// <summary>
    /// Protocol 1 of the Automation pipe (#597): UTF-8 text, one message per
    /// line. A line is a verb and its words. Text with spaces goes in double
    /// quotes, with <c>\"</c> and <c>\\</c> inside. A request may open with a
    /// tag such as <c>@7</c>, and every line of its answer carries the same
    /// tag. An answer is <c>ok ...</c>, <c>err &lt;code&gt; &lt;words&gt;</c>,
    /// or rows followed by <c>ok</c>. Events open with <c>e</c> and carry no
    /// tag. Verbs, codes and words are fixed ASCII and never localized: this
    /// is a machine interface.
    /// </summary>
    internal static class AutomationLine
    {
        /// <summary>The protocol this build speaks.</summary>
        internal const int Protocol = 1;

        /// <summary>The longest line, in bytes, the newline left out.</summary>
        internal const int LongestLine = 8192;

        /// <summary>The longest <c>declare</c> line, whose JSON names every
        /// control of a device.</summary>
        internal const int LongestDeclare = 65536;

        /// <summary>The longest tag, the <c>@</c> left out.</summary>
        internal const int LongestTag = 16;

        /// <summary>
        /// Reads a line. Leading and trailing spaces and a carriage return
        /// are dropped. A line that opens a quote and never closes it, or a
        /// tag that is not <c>@</c> and up to 16 letters and digits, comes
        /// back with <see cref="AutomationRequest.Error"/> set.
        /// </summary>
        internal static AutomationRequest Parse(string line)
        {
            line = (line ?? string.Empty).Trim();
            string tag = null;
            if (line.StartsWith("@", StringComparison.Ordinal))
            {
                int end = line.IndexOf(' ');
                string word = end < 0 ? line : line.Substring(0, end);
                if (!IsTag(word))
                    return new AutomationRequest { Error = "bad-tag" };
                tag = word;
                line = end < 0 ? string.Empty : line.Substring(end + 1).TrimStart();
            }
            if (line.Length == 0) return new AutomationRequest { Tag = tag, Error = "empty", Body = line };

            int space = line.IndexOf(' ');
            string verb = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : line.Substring(space + 1);
            if (!TrySplit(rest, out var words, out string json))
                return new AutomationRequest { Tag = tag, Verb = verb, Body = line, Error = "bad-quote" };
            return new AutomationRequest { Tag = tag, Verb = verb, Words = words, Json = json, Body = line };
        }

        private static bool IsTag(string word)
        {
            if (word.Length < 2 || word.Length > LongestTag + 1 || word[0] != '@') return false;
            for (int i = 1; i < word.Length; i++)
                if (!(word[i] is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_')) return false;
            return true;
        }

        /// <summary>Splits the words after the verb. A word that begins
        /// with <c>{</c> starts the JSON object, which runs to the end of
        /// the line.</summary>
        internal static bool TrySplit(string text, out List<AutomationWord> words, out string json)
        {
            words = new List<AutomationWord>();
            json = null;
            int i = 0;
            var value = new StringBuilder();
            while (i < text.Length)
            {
                while (i < text.Length && text[i] == ' ') i++;
                if (i >= text.Length) break;
                if (text[i] == '{')
                {
                    json = text.Substring(i).TrimEnd();
                    return true;
                }
                int start = i;
                string key = null;
                value.Clear();
                bool quoted = false;
                while (i < text.Length && (quoted || text[i] != ' '))
                {
                    char c = text[i];
                    if (quoted)
                    {
                        if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
                        {
                            value.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (c == '"') quoted = false;
                        else value.Append(c);
                        i++;
                        continue;
                    }
                    if (c == '"') quoted = true;
                    else if (c == '=' && key == null)
                    {
                        key = value.ToString();
                        value.Clear();
                    }
                    else value.Append(c);
                    i++;
                }
                if (quoted) return false;
                words.Add(new AutomationWord(key, value.ToString(), text.Substring(start, i - start)));
            }
            return true;
        }

        /// <summary>Text in double quotes, with <c>\"</c> and <c>\\</c> for
        /// the two characters that need it. A line break, which would end
        /// the line, becomes a space.</summary>
        internal static string Quote(string text)
        {
            var sb = new StringBuilder((text?.Length ?? 0) + 2);
            sb.Append('"');
            foreach (char c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r':
                    case '\n': sb.Append(' '); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>A word for the line: bare when it holds no space, quote,
        /// equals sign or backslash, quoted otherwise.</summary>
        internal static string Word(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\"\"";
            foreach (char c in text)
                if (c is ' ' or '"' or '\\' or '=' or '\r' or '\n' || c == '{') return Quote(text);
            return text;
        }

        /// <summary>An answer line with the request's tag in front.</summary>
        internal static string Tagged(string tag, string line) => tag == null ? line : tag + " " + line;

        /// <summary><c>err &lt;code&gt; &lt;words&gt;</c>.</summary>
        internal static string Error(string code, string words = null)
            => "err " + code + " " + (words ?? WordsFor(code));

        /// <summary>The plain words that follow an error's code, for a
        /// person reading a log. Fixed English: the code is the contract.</summary>
        internal static string WordsFor(string code) => code switch
        {
            "empty" => "the line is empty",
            "bad-tag" => "a tag is @ and up to 16 letters or digits",
            "bad-quote" => "a quote is not closed",
            "unknown-command" => "PadForge does not know this verb",
            "bad-argument" => "an argument is missing or not valid",
            "not-allowed" => "the Automation level does not allow this",
            "not-available" => "this is not available in this build or now",
            "internal" => "PadForge failed to do it",
            "busy" => "another program holds it, or PadForge is busy",
            "unknown-slot" => "no virtual controller has that number",
            "unknown-profile" => "no profile has that name or ID",
            "unknown-device" => "no device has that ID",
            "offline" => "the device is not connected",
            "not-owner" => "the device belongs to another program",
            "too-long" => "the line is too long",
            "rate-limited" => "too many lines a second",
            "too-many" => "a limit on sessions, devices or subscriptions is reached",
            "hello-first" => "a session opens with hello",
            "unsupported-protocol" => "PadForge speaks protocol 1",
            "bad-declaration" => "the device's description is not valid",
            "denied" => "the token is not one PadForge gave",
            "refused" => "PadForge does not take this program's connection",
            "engine-stopped" => "the engine is stopped",
            "too-soon" => "this was asked too soon after the last time",
            "limit-reached" => "PadForge takes no more of these until it starts again",
            _ => "the request failed",
        };

        /// <summary>The control pipe's answer in this pipe's form:
        /// <c>error x</c> becomes <c>err x words</c>, and <c>ok</c> stays.</summary>
        internal static string FromCommandSet(string answer)
        {
            if (string.IsNullOrEmpty(answer)) return Error("internal");
            if (answer.StartsWith("error ", StringComparison.Ordinal))
            {
                string code = answer.Substring(6).Trim();
                int space = code.IndexOf(' ');
                if (space >= 0) code = code.Substring(0, space);
                return Error(code);
            }
            return answer;
        }

        /// <summary>A number in the invariant culture.</summary>
        internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>A float in the invariant culture, short and exact
        /// enough for motion.</summary>
        internal static string Number(float value)
            => float.IsFinite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "0";

        internal static bool TryInt(string text, out int value)
            => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        internal static bool TryFloat(string text, out float value)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }
}

namespace PadForge.Services.Automation
{
    /// <summary>
    /// How many lines a session may send: a bucket that refills
    /// <c>perSecond</c> lines a second and holds one second's worth, so a
    /// steady stream under the rate is never refused and a burst over it is
    /// cut to the rate.
    /// </summary>
    internal sealed class AutomationLineRate
    {
        private readonly int _perSecond;
        private double _tokens;
        private long _atMs;

        public AutomationLineRate(int perSecond, long nowMs)
        {
            _perSecond = perSecond;
            _tokens = perSecond;
            _atMs = nowMs;
        }

        /// <summary>Takes one line at <paramref name="nowMs"/>. False when
        /// the line is over the rate.</summary>
        public bool Take(long nowMs)
        {
            if (nowMs > _atMs)
            {
                _tokens = System.Math.Min(_perSecond, _tokens + (nowMs - _atMs) * (_perSecond / 1000.0));
                _atMs = nowMs;
            }
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
