using System;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Services.Install
{
    /// <summary>
    /// One key of a JSON settings file changed in its text (#600), so the
    /// rest of the file stays as the user wrote it: comments, trailing
    /// commas, order, spacing and line ends. Zed's and VS Code's settings are
    /// JSON with comments, and any host's file may hold the user's notes.
    ///
    /// <para>It reads JSON with <c>//</c> and <c>/* */</c> comments and
    /// trailing commas, finds the object under a key of the root and the
    /// member under that, and replaces, adds or takes out that member alone.
    /// A file whose root is no object is refused.</para>
    /// </summary>
    internal static class JsoncEdit
    {
        /// <summary>One member of an object: where its key starts, where its
        /// value starts and ends, and the comma after it, or -1.</summary>
        private readonly record struct Member(string Key, int KeyStart, int ValueStart, int ValueEnd, int Comma);

        /// <summary>The text with <paramref name="container"/>.<paramref name="name"/>
        /// set to <paramref name="valueJson"/> (one JSON value, any
        /// layout).</summary>
        internal static string Set(string text, string container, string name, string valueJson)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "{\n  " + Quote(container) + ": {\n    " + Quote(name) + ": " + Indent(valueJson, "    ") + "\n  }\n}\n";
            int root = RootObject(text);
            string nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var rootMembers = Members(text, root, out int rootClose);
            string unit = IndentUnit(text, rootMembers);
            foreach (var c in rootMembers)
            {
                if (c.Key != container) continue;
                if (text[c.ValueStart] != '{') throw new InvalidOperationException("The file's " + container + " is no object, so PadForge leaves it as it is.");
                var members = Members(text, c.ValueStart, out int close);
                foreach (var m in members)
                    if (m.Key == name)
                        return text.Substring(0, m.ValueStart) + Indent(valueJson, unit + unit).Replace("\n", nl) + text.Substring(m.ValueEnd);
                string member = Quote(name) + ": " + Indent(valueJson, unit + unit).Replace("\n", nl);
                return Insert(text, members, c.ValueStart, close, member, unit + unit, unit, nl);
            }
            string block = Quote(container) + ": {" + nl + unit + unit + Quote(name) + ": " + Indent(valueJson, unit + unit).Replace("\n", nl) + nl + unit + "}";
            return Insert(text, rootMembers, root, rootClose, block, unit, string.Empty, nl);
        }

        /// <summary>The text without <paramref name="container"/>.<paramref name="name"/>,
        /// or null when it is not there.</summary>
        internal static string Remove(string text, string container, string name)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int root = RootObject(text);
            foreach (var c in Members(text, root, out _))
            {
                if (c.Key != container || text[c.ValueStart] != '{') continue;
                var members = Members(text, c.ValueStart, out _);
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (m.Key != name) continue;
                    if (m.Comma >= 0)
                    {
                        int end = m.Comma + 1;
                        while (end < text.Length && text[end] is ' ' or '\t') end++;
                        if (end < text.Length && text[end] == '\r') end++;
                        if (end < text.Length && text[end] == '\n') end++;
                        return text.Substring(0, LineStart(text, m.KeyStart)) + text.Substring(end);
                    }
                    if (i > 0) return text.Substring(0, members[i - 1].ValueEnd) + text.Substring(m.ValueEnd);
                    return text.Substring(0, LineStart(text, m.KeyStart)) + text.Substring(m.ValueEnd).TrimStart(' ', '\t', '\r', '\n');
                }
            }
            return null;
        }

        private static int LineStart(string text, int at)
        {
            int start = at;
            while (start > 0 && text[start - 1] is ' ' or '\t') start--;
            return start > 0 && text[start - 1] == '\n' ? start : at;
        }

        private static string Insert(string text, List<Member> members, int open, int close, string member, string indent, string closeIndent, string nl)
        {
            if (members.Count == 0)
                return text.Substring(0, open + 1) + nl + indent + member + nl + closeIndent + text.Substring(close);
            var last = members[^1];
            string comma = last.Comma >= 0 ? string.Empty : ",";
            int at = last.Comma >= 0 ? last.Comma + 1 : last.ValueEnd;
            return text.Substring(0, at) + comma + nl + indent + member + (last.Comma >= 0 ? "," : string.Empty) + text.Substring(at);
        }

        private static string IndentUnit(string text, List<Member> members)
        {
            if (members.Count == 0) return "  ";
            int start = members[0].KeyStart, i = start;
            while (i > 0 && text[i - 1] is ' ' or '\t') i--;
            return i < start ? text.Substring(i, start - i) : "  ";
        }

        private static string Indent(string json, string indent)
            => json.Replace("\r\n", "\n").Replace("\n", "\n" + indent);

        private static string Quote(string s) => System.Text.Json.JsonSerializer.Serialize(s);

        private static int RootObject(string text)
        {
            int i = Skip(text, 0);
            if (i >= text.Length || text[i] != '{') throw new InvalidOperationException("The file's root is no JSON object, so PadForge leaves it as it is.");
            return i;
        }

        /// <summary>The members of the object that opens at
        /// <paramref name="open"/>, and where it closes.</summary>
        private static List<Member> Members(string text, int open, out int close)
        {
            var list = new List<Member>();
            int i = Skip(text, open + 1);
            while (true)
            {
                if (i >= text.Length) throw Bad();
                if (text[i] == '}') { close = i; return list; }
                if (text[i] != '"') throw Bad();
                int keyStart = i;
                int keyEnd = StringEnd(text, i);
                string key = System.Text.Json.JsonSerializer.Deserialize<string>(text.Substring(keyStart, keyEnd - keyStart));
                i = Skip(text, keyEnd);
                if (i >= text.Length || text[i] != ':') throw Bad();
                int valueStart = Skip(text, i + 1);
                int valueEnd = ValueEnd(text, valueStart);
                i = Skip(text, valueEnd);
                int comma = -1;
                if (i < text.Length && text[i] == ',') { comma = i; i = Skip(text, i + 1); }
                list.Add(new Member(key, keyStart, valueStart, valueEnd, comma));
            }
        }

        private static int ValueEnd(string text, int i)
        {
            if (i >= text.Length) throw Bad();
            char c = text[i];
            if (c == '"') return StringEnd(text, i);
            if (c is '{' or '[')
            {
                int depth = 0;
                while (i < text.Length)
                {
                    char d = text[i];
                    if (d == '"') { i = StringEnd(text, i); continue; }
                    if (d == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') { i = Skip(text, i); continue; }
                    if (d is '{' or '[') depth++;
                    else if (d is '}' or ']') { depth--; if (depth == 0) return i + 1; }
                    i++;
                }
                throw Bad();
            }
            while (i < text.Length && !(text[i] is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n' or '/')) i++;
            return i;
        }

        private static int StringEnd(string text, int i)
        {
            for (i++; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i + 1;
            }
            throw Bad();
        }

        /// <summary>Past whitespace and comments.</summary>
        private static int Skip(string text, int i)
        {
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) throw Bad();
                    i = end + 2;
                    continue;
                }
                return i;
            }
            return i;
        }

        private static InvalidOperationException Bad() => new("The file is not JSON PadForge can read, so PadForge leaves it as it is.");
    }
}
