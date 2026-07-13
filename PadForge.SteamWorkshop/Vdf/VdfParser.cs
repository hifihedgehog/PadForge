using System;
using System.Collections.Generic;
using System.Text;

namespace PadForge.SteamWorkshop.Vdf
{
    /// <summary>
    /// Original recursive-descent parser for Steam's KeyValues (VDF) text format.
    /// Handles quoted and unquoted tokens, duplicate keys, <c>//</c> line comments and
    /// <c>/* */</c> block comments (only outside quoted strings), escape sequences, empty
    /// objects and empty values, and embedded Unicode. Rejects binary KeyValues (VBKV),
    /// inputs over 10 MB, and nesting deeper than the depth cap.
    /// </summary>
    public static class VdfParser
    {
        /// <summary>Default maximum object-nesting depth before the parser rejects the input.</summary>
        public const int DefaultMaxDepth = 32;

        /// <summary>Hard cap on input size (10 MB) measured as UTF-8 bytes.</summary>
        public const int MaxInputBytes = 10 * 1024 * 1024;

        /// <summary>
        /// Parses a VDF document and returns the root object node. The root's children are
        /// the top-level key/value pairs (Steam Input configs have a single
        /// <c>controller_mappings</c> child).
        /// </summary>
        public static VdfNode Parse(string text, int maxDepth = DefaultMaxDepth)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));

            // Size cap. The char-length guard rejects oversized input without the O(n)
            // byte count; UTF-8 bytes are always >= char count for a same-length reject.
            if (text.Length > MaxInputBytes || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
                throw new VdfSyntaxException("VDF input exceeds the 10 MB limit.", 0, 1, 1);

            var start = 0;
            if (text.Length > 0 && text[0] == '\uFEFF') start = 1; // strip UTF-8 BOM

            if (LooksBinary(text, start))
                throw new VdfSyntaxException("Binary VDF (VBKV) is not supported.", start, 1, 1);

            var reader = new Reader(text, start, maxDepth);
            return reader.ParseDocument();
        }

        /// <summary>
        /// Non-throwing wrapper. Returns false and the <see cref="VdfSyntaxException"/> on a
        /// malformed / oversized / binary / too-deep document. Argument errors still throw.
        /// </summary>
        public static bool TryParse(string text, out VdfNode root, out VdfSyntaxException error)
        {
            try
            {
                root = Parse(text);
                error = null;
                return true;
            }
            catch (VdfSyntaxException ex)
            {
                root = null;
                error = ex;
                return false;
            }
        }

        private static bool LooksBinary(string t, int start)
        {
            // Binary KeyValues begins with a NUL byte (often followed by the "VBKV" tag).
            if (start < t.Length && t[start] == '\0') return true;
            return HasAt(t, start, "VBKV");
        }

        private static bool HasAt(string t, int start, string token)
        {
            if (start + token.Length > t.Length) return false;
            for (var i = 0; i < token.Length; i++)
            {
                if (t[start + i] != token[i]) return false;
            }
            return true;
        }

        private sealed class Reader
        {
            private readonly string _s;
            private readonly int _maxDepth;
            private int _pos;
            private int _line;
            private int _col;

            public Reader(string s, int start, int maxDepth)
            {
                _s = s;
                _pos = start;
                _line = 1;
                _col = 1;
                _maxDepth = maxDepth;
            }

            private bool Eof => _pos >= _s.Length;

            private char Cur => _s[_pos];

            private char PeekAt(int offset)
            {
                var i = _pos + offset;
                return i < _s.Length ? _s[i] : '\0';
            }

            private void Advance()
            {
                if (_s[_pos] == '\n')
                {
                    _line++;
                    _col = 1;
                }
                else
                {
                    _col++;
                }
                _pos++;
            }

            private VdfSyntaxException Error(string message) =>
                new VdfSyntaxException(message, _pos, _line, _col);

            public VdfNode ParseDocument()
            {
                var children = ParsePairs(depth: 0, insideBraces: false);
                return VdfNode.NewObject(children);
            }

            /// <summary>
            /// Parses key/value pairs until a closing brace (when <paramref name="insideBraces"/>)
            /// or end of input (at document level). Returns the ordered child list.
            /// </summary>
            private List<KeyValuePair<string, VdfNode>> ParsePairs(int depth, bool insideBraces)
            {
                var children = new List<KeyValuePair<string, VdfNode>>();

                while (true)
                {
                    SkipWhitespaceAndComments();

                    if (Eof)
                    {
                        if (insideBraces)
                            throw Error("Unexpected end of input; expected '}'.");
                        return children;
                    }

                    var c = Cur;
                    if (c == '}')
                    {
                        if (!insideBraces)
                            throw Error("Unexpected '}'.");
                        Advance();
                        return children;
                    }

                    if (c == '{')
                        throw Error("Expected a key but found '{'.");

                    var key = ReadToken(isKey: true);

                    SkipWhitespaceAndComments();

                    if (Eof)
                        throw Error($"Unexpected end of input after key '{key}'; expected a value or '{{'.");

                    if (Cur == '{')
                    {
                        var childDepth = depth + 1;
                        if (childDepth > _maxDepth)
                            throw Error($"Maximum nesting depth of {_maxDepth} exceeded.");
                        Advance(); // consume '{'
                        var grandChildren = ParsePairs(childDepth, insideBraces: true);
                        children.Add(new KeyValuePair<string, VdfNode>(key, VdfNode.NewObject(grandChildren)));
                        SkipOptionalConditional();
                    }
                    else if (Cur == '}')
                    {
                        throw Error($"Unexpected '}}' after key '{key}'; expected a value.");
                    }
                    else
                    {
                        var value = ReadToken(isKey: false);
                        children.Add(new KeyValuePair<string, VdfNode>(key, VdfNode.NewValue(value)));
                        SkipOptionalConditional();
                    }
                }
            }

            private string ReadToken(bool isKey)
            {
                return Cur == '"' ? ReadQuoted() : ReadUnquoted(isKey);
            }

            private string ReadQuoted()
            {
                Advance(); // consume opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (Eof)
                        throw Error("Unterminated quoted string.");

                    var c = Cur;
                    if (c == '"')
                    {
                        Advance(); // consume closing quote
                        return sb.ToString();
                    }

                    if (c == '\\')
                    {
                        Advance();
                        if (Eof)
                            throw Error("Unterminated escape sequence.");
                        var e = Cur;
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            default:
                                // Unknown escape: pass through literally (backslash retained).
                                sb.Append('\\');
                                sb.Append(e);
                                break;
                        }
                        Advance();
                    }
                    else
                    {
                        sb.Append(c);
                        Advance();
                    }
                }
            }

            private string ReadUnquoted(bool isKey)
            {
                var sb = new StringBuilder();
                while (!Eof)
                {
                    var c = Cur;
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '{' || c == '}' || c == '"')
                        break;
                    // A comment start also terminates an unquoted token.
                    if (c == '/' && (PeekAt(1) == '/' || PeekAt(1) == '*'))
                        break;
                    sb.Append(c);
                    Advance();
                }

                if (sb.Length == 0)
                    throw Error(isKey ? "Expected a key." : "Expected a value.");

                return sb.ToString();
            }

            /// <summary>
            /// After a scalar value or a closed object, tolerate a Valve platform
            /// conditional (<c>[$WIN32]</c> etc.) on the same line by skipping it. None of
            /// the ground-truth fixtures use conditionals; this keeps the parser robust to
            /// configs that do.
            /// </summary>
            private void SkipOptionalConditional()
            {
                var savedPos = _pos;
                var savedLine = _line;
                var savedCol = _col;

                while (!Eof && (Cur == ' ' || Cur == '\t'))
                    Advance();

                if (!Eof && Cur == '[')
                {
                    while (!Eof && Cur != ']' && Cur != '\n')
                        Advance();
                    if (!Eof && Cur == ']')
                    {
                        Advance();
                        return;
                    }
                }

                // Not a conditional; rewind so the next pair is parsed normally.
                _pos = savedPos;
                _line = savedLine;
                _col = savedCol;
            }

            private void SkipWhitespaceAndComments()
            {
                while (!Eof)
                {
                    var c = Cur;
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        Advance();
                    }
                    else if (c == '/' && PeekAt(1) == '/')
                    {
                        while (!Eof && Cur != '\n')
                            Advance();
                    }
                    else if (c == '/' && PeekAt(1) == '*')
                    {
                        Advance();
                        Advance();
                        while (!Eof && !(Cur == '*' && PeekAt(1) == '/'))
                            Advance();
                        if (!Eof)
                        {
                            Advance(); // '*'
                            Advance(); // '/'
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }
    }
}
