using System;
using System.Collections.Generic;
using System.Globalization;

namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Parses and evaluates a row's <c>Custom</c> combine expression.
    /// Sandboxed: numeric only, fixed function registry, no I/O, no state,
    /// no user-defined functions, bounded execution time. The grammar:
    ///
    /// <para>
    /// Variables: <c>a..z</c> bind to the first 26 sources in row order;
    /// <c>s[i]</c> indexes the source list by integer index. Booleans
    /// (button sources) feed in as <c>0.0</c> or <c>1.0</c>. Out-of-range
    /// references resolve to <c>0.0</c>.
    /// </para>
    ///
    /// <para>
    /// Per-source "active" flags: <c>aD..zD</c> bind to the parallel
    /// <c>activeFlags</c> list when the caller supplies one (currently only
    /// the touchpad-passthrough evaluator does). Each <c>aD</c>-style ref
    /// reads <c>1.0</c> when the source is currently "live" (a touchpad
    /// source whose paired <c>TouchpadDown</c> is true, or a non-touchpad
    /// source which is always live by definition) and <c>0.0</c> when
    /// idle. The built-in combine modes already filter idle sources out
    /// before combining; Custom formulas get ALL sources + the flag
    /// channel so the user can write their own gating
    /// (e.g. <c>aD ? a : (bD ? b : 0.5)</c>). Out-of-range or
    /// caller-didn't-supply-flags references resolve to <c>0.0</c>.
    /// </para>
    ///
    /// <para>
    /// Literals: numbers (decimal), <c>true</c>/<c>false</c>, <c>pi</c>.
    /// There is deliberately NO <c>e</c> constant: every letter <c>a..z</c>
    /// addresses a source, so a constant named <c>e</c> would shadow the
    /// fifth one.
    /// </para>
    ///
    /// <para>
    /// Operators (high-to-low precedence): unary <c>+ - !</c>; <c>* / %</c>;
    /// <c>+ -</c>; <c>&lt; &lt;= &gt; &gt;=</c>; <c>== !=</c>; <c>&amp;&amp;</c>;
    /// <c>||</c>; <c>?:</c> ternary; parens.
    /// </para>
    ///
    /// <para>
    /// Functions (fixed registry): <c>abs, min, max, clamp(x,lo,hi),
    /// sign, floor, ceil, round, sqrt, sin, cos, tan, atan2(y,x),
    /// lerp(a,b,t)</c>.
    /// </para>
    ///
    /// <para>
    /// Compile once at config-load (<see cref="Compile"/>); the resulting
    /// <see cref="Compiled"/> caches the AST and is safe to reuse. Runtime
    /// evaluation walks the cached tree — no string parsing on the hot
    /// path. NaN / Infinity / divide-by-zero return <c>0</c> from
    /// <see cref="Compiled.Evaluate"/>; the engine never throws into the
    /// polling loop.
    /// </para>
    /// </summary>
    public static class MappingExpression
    {
        /// <summary>Stable identifier for each parse-error template the
        /// parser can emit. The App side registers a localized formatter
        /// via <see cref="ErrorFormatter"/> so error messages surface in
        /// the active culture; without a registered formatter the parser
        /// falls back to <see cref="DefaultEnglishFormatter"/>.</summary>
        public enum ParseError
        {
            UnexpectedTokenAtEnd,        // args: position, text
            InvalidNumber,               // args: text, position
            SingleEqualsNotSupported,    // args: position
            UnexpectedCharacter,         // args: char, position
            ExpectedColonInTernary,      // no args
            ExpectedRParen,              // no args
            ExpectedRParenAfterArgs,     // no args
            ExpectedRBracketAfterIndex,  // no args
            ExpectedTokenSuffix,         // args: gotText, position — appended after the per-site "Expected …" base
            UnknownIdentifier,           // args: text, position
            UnexpectedToken,             // args: text, position
            UnexpectedParseError,        // args: message
        }

        /// <summary>App-registered translator. Defaults to English. Set
        /// at App startup with a switch over <see cref="ParseError"/>
        /// that pulls from the localized Pad_Formula_Error_* resources.</summary>
        public static System.Func<ParseError, object[], string> ErrorFormatter { get; set; }
            = DefaultEnglishFormatter;

        internal static string FormatError(ParseError code, params object[] args)
            => (ErrorFormatter ?? DefaultEnglishFormatter)(code, args);

        private static string DefaultEnglishFormatter(ParseError code, object[] args)
        {
            args ??= System.Array.Empty<object>();
            return code switch
            {
                ParseError.UnexpectedTokenAtEnd       => $"Unexpected token at position {Arg(args,0)}: '{Arg(args,1)}'",
                ParseError.InvalidNumber              => $"Invalid number '{Arg(args,0)}' at {Arg(args,1)}",
                ParseError.SingleEqualsNotSupported   => $"Single '=' not supported at {Arg(args,0)}; use '==' for equality",
                ParseError.UnexpectedCharacter        => $"Unexpected character '{Arg(args,0)}' at {Arg(args,1)}",
                ParseError.ExpectedColonInTernary     => "Expected ':' in ternary",
                ParseError.ExpectedRParen             => "Expected ')'",
                ParseError.ExpectedRParenAfterArgs    => "Expected ')' after function arguments",
                ParseError.ExpectedRBracketAfterIndex => "Expected ']' after source index",
                ParseError.ExpectedTokenSuffix        => $" (got '{Arg(args,0)}' at {Arg(args,1)})",
                ParseError.UnknownIdentifier          => $"Unknown identifier '{Arg(args,0)}' at {Arg(args,1)}",
                ParseError.UnexpectedToken            => $"Unexpected token '{Arg(args,0)}' at {Arg(args,1)}",
                ParseError.UnexpectedParseError       => $"Unexpected parse error: {Arg(args,0)}",
                _ => code.ToString(),
            };
        }
        private static object Arg(object[] a, int i) => (a != null && i < a.Length) ? a[i] : "";


        /// <summary>Result of compiling an expression. <c>IsValid</c>
        /// false carries <see cref="Error"/> with the parse failure
        /// message for the UI to surface.</summary>
        public sealed class Compiled
        {
            internal Node Root;
            public bool IsValid { get; internal set; }
            public string Error { get; internal set; } = "";

            /// <summary>Names referenced by single-letter variables (e.g.
            /// <c>"abc"</c> means a, b, c are referenced). Useful for the
            /// edit-time warning that flags references past the source
            /// count.</summary>
            public string ReferencedSingleLetterVars { get; internal set; } = "";

            /// <summary>Maximum integer index seen in <c>s[i]</c>
            /// references. -1 if none. Useful for the same edit-time
            /// warning.</summary>
            public int MaxIndexedRef { get; internal set; } = -1;

            /// <summary>Evaluates the compiled expression against a list of
            /// per-source float values (the row's already-coerced source
            /// contributions). Returns 0 on any runtime error.</summary>
            public float Evaluate(IList<float> sources) => Evaluate(sources, null);

            /// <summary>Evaluates with a parallel "active flag" channel so
            /// <c>aD..zD</c> references resolve to <c>1.0</c> / <c>0.0</c>.
            /// Callers that don't supply flags (the gamepad / extended /
            /// MIDI / KBM combine paths) pass <c>null</c>; <c>aD</c>-style
            /// refs in their formulas resolve to <c>0.0</c>.</summary>
            public float Evaluate(IList<float> sources, IList<float> activeFlags)
            {
                if (!IsValid || Root == null) return 0f;
                try
                {
                    double r = Root.Eval(sources, activeFlags);
                    if (double.IsNaN(r) || double.IsInfinity(r)) return 0f;
                    return (float)r;
                }
                catch
                {
                    return 0f;
                }
            }
        }

        /// <summary>Parses an expression. Returns a <see cref="Compiled"/>
        /// with <c>IsValid=false</c> + an <c>Error</c> message if the
        /// grammar rejects it. Empty / whitespace-only input compiles as
        /// the literal 0.</summary>
        public static Compiled Compile(string expression)
        {
            var result = new Compiled();
            try
            {
                if (string.IsNullOrWhiteSpace(expression))
                {
                    result.Root = new NumberNode(0);
                    result.IsValid = true;
                    return result;
                }

                var tokens = Tokenize(expression);
                var parser = new Parser(tokens);
                var root = parser.ParseExpression();
                if (parser.HasMore)
                {
                    result.Error = FormatError(ParseError.UnexpectedTokenAtEnd, parser.Peek().Position, parser.Peek().Text);
                    return result;
                }
                result.Root = root;
                result.IsValid = true;
                root.Walk(ref result);
                return result;
            }
            catch (ParseException ex)
            {
                result.Error = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = FormatError(ParseError.UnexpectedParseError, ex.Message);
                return result;
            }
        }

        // ─── Tokens ─────────────────────────────────────────────────────

        internal enum TokenKind
        {
            Number, Identifier, LParen, RParen, LBracket, RBracket, Comma,
            Plus, Minus, Star, Slash, Percent,
            Lt, Le, Gt, Ge, Eq, Ne,
            AndAnd, OrOr, Not,
            Question, Colon,
            End,
        }

        internal struct Token
        {
            public TokenKind Kind;
            public string Text;
            public double Number;
            public int Position;
        }

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                int start = i;

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int j = i;
                    while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                    string num = s.Substring(i, j - i);
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        throw new ParseException(FormatError(ParseError.InvalidNumber, num, start));
                    tokens.Add(new Token { Kind = TokenKind.Number, Number = v, Text = num, Position = start });
                    i = j;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    tokens.Add(new Token { Kind = TokenKind.Identifier, Text = s.Substring(i, j - i), Position = start });
                    i = j;
                    continue;
                }

                if (c == '(') { tokens.Add(new Token { Kind = TokenKind.LParen, Text = "(", Position = start }); i++; continue; }
                if (c == ')') { tokens.Add(new Token { Kind = TokenKind.RParen, Text = ")", Position = start }); i++; continue; }
                if (c == '[') { tokens.Add(new Token { Kind = TokenKind.LBracket, Text = "[", Position = start }); i++; continue; }
                if (c == ']') { tokens.Add(new Token { Kind = TokenKind.RBracket, Text = "]", Position = start }); i++; continue; }
                if (c == ',') { tokens.Add(new Token { Kind = TokenKind.Comma, Text = ",", Position = start }); i++; continue; }
                if (c == '+') { tokens.Add(new Token { Kind = TokenKind.Plus, Text = "+", Position = start }); i++; continue; }
                if (c == '-') { tokens.Add(new Token { Kind = TokenKind.Minus, Text = "-", Position = start }); i++; continue; }
                if (c == '*') { tokens.Add(new Token { Kind = TokenKind.Star, Text = "*", Position = start }); i++; continue; }
                if (c == '/') { tokens.Add(new Token { Kind = TokenKind.Slash, Text = "/", Position = start }); i++; continue; }
                if (c == '%') { tokens.Add(new Token { Kind = TokenKind.Percent, Text = "%", Position = start }); i++; continue; }
                if (c == '?') { tokens.Add(new Token { Kind = TokenKind.Question, Text = "?", Position = start }); i++; continue; }
                if (c == ':') { tokens.Add(new Token { Kind = TokenKind.Colon, Text = ":", Position = start }); i++; continue; }

                if (c == '<')
                {
                    if (i + 1 < s.Length && s[i + 1] == '=')
                    { tokens.Add(new Token { Kind = TokenKind.Le, Text = "<=", Position = start }); i += 2; continue; }
                    tokens.Add(new Token { Kind = TokenKind.Lt, Text = "<", Position = start }); i++; continue;
                }
                if (c == '>')
                {
                    if (i + 1 < s.Length && s[i + 1] == '=')
                    { tokens.Add(new Token { Kind = TokenKind.Ge, Text = ">=", Position = start }); i += 2; continue; }
                    tokens.Add(new Token { Kind = TokenKind.Gt, Text = ">", Position = start }); i++; continue;
                }
                if (c == '=')
                {
                    if (i + 1 < s.Length && s[i + 1] == '=')
                    { tokens.Add(new Token { Kind = TokenKind.Eq, Text = "==", Position = start }); i += 2; continue; }
                    throw new ParseException(FormatError(ParseError.SingleEqualsNotSupported, start));
                }
                if (c == '!')
                {
                    if (i + 1 < s.Length && s[i + 1] == '=')
                    { tokens.Add(new Token { Kind = TokenKind.Ne, Text = "!=", Position = start }); i += 2; continue; }
                    tokens.Add(new Token { Kind = TokenKind.Not, Text = "!", Position = start }); i++; continue;
                }
                if (c == '&' && i + 1 < s.Length && s[i + 1] == '&')
                { tokens.Add(new Token { Kind = TokenKind.AndAnd, Text = "&&", Position = start }); i += 2; continue; }
                if (c == '|' && i + 1 < s.Length && s[i + 1] == '|')
                { tokens.Add(new Token { Kind = TokenKind.OrOr, Text = "||", Position = start }); i += 2; continue; }

                throw new ParseException(FormatError(ParseError.UnexpectedCharacter, c, start));
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = "", Position = s.Length });
            return tokens;
        }

        // ─── Parser (Pratt-style operator precedence) ───────────────────

        private sealed class ParseException : Exception
        {
            public ParseException(string msg) : base(msg) { }
        }

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private int _pos;

            public Parser(List<Token> tokens) { _tokens = tokens; _pos = 0; }
            public bool HasMore => _tokens[_pos].Kind != TokenKind.End;
            public Token Peek() => _tokens[_pos];
            private Token Advance() => _tokens[_pos++];

            private Token Expect(TokenKind k, string msg)
            {
                if (_tokens[_pos].Kind != k)
                    throw new ParseException(msg + FormatError(ParseError.ExpectedTokenSuffix, _tokens[_pos].Text, _tokens[_pos].Position));
                return Advance();
            }

            public Node ParseExpression() => ParseTernary();

            private Node ParseTernary()
            {
                var cond = ParseOr();
                if (Peek().Kind == TokenKind.Question)
                {
                    Advance();
                    var then = ParseTernary();
                    Expect(TokenKind.Colon, FormatError(ParseError.ExpectedColonInTernary));
                    var els = ParseTernary();
                    return new TernaryNode { Cond = cond, Then = then, Else = els };
                }
                return cond;
            }

            private Node ParseOr()
            {
                var left = ParseAnd();
                while (Peek().Kind == TokenKind.OrOr)
                { Advance(); left = new BinaryNode { Op = "||", L = left, R = ParseAnd() }; }
                return left;
            }
            private Node ParseAnd()
            {
                var left = ParseEquality();
                while (Peek().Kind == TokenKind.AndAnd)
                { Advance(); left = new BinaryNode { Op = "&&", L = left, R = ParseEquality() }; }
                return left;
            }
            private Node ParseEquality()
            {
                var left = ParseComparison();
                while (Peek().Kind == TokenKind.Eq || Peek().Kind == TokenKind.Ne)
                { var op = Advance().Text; left = new BinaryNode { Op = op, L = left, R = ParseComparison() }; }
                return left;
            }
            private Node ParseComparison()
            {
                var left = ParseAdditive();
                while (Peek().Kind == TokenKind.Lt || Peek().Kind == TokenKind.Le ||
                       Peek().Kind == TokenKind.Gt || Peek().Kind == TokenKind.Ge)
                { var op = Advance().Text; left = new BinaryNode { Op = op, L = left, R = ParseAdditive() }; }
                return left;
            }
            private Node ParseAdditive()
            {
                var left = ParseMultiplicative();
                while (Peek().Kind == TokenKind.Plus || Peek().Kind == TokenKind.Minus)
                { var op = Advance().Text; left = new BinaryNode { Op = op, L = left, R = ParseMultiplicative() }; }
                return left;
            }
            private Node ParseMultiplicative()
            {
                var left = ParseUnary();
                while (Peek().Kind == TokenKind.Star || Peek().Kind == TokenKind.Slash || Peek().Kind == TokenKind.Percent)
                { var op = Advance().Text; left = new BinaryNode { Op = op, L = left, R = ParseUnary() }; }
                return left;
            }
            private Node ParseUnary()
            {
                if (Peek().Kind == TokenKind.Plus) { Advance(); return ParseUnary(); }
                if (Peek().Kind == TokenKind.Minus) { Advance(); return new UnaryNode { Op = "-", Operand = ParseUnary() }; }
                if (Peek().Kind == TokenKind.Not) { Advance(); return new UnaryNode { Op = "!", Operand = ParseUnary() }; }
                return ParsePrimary();
            }
            private Node ParsePrimary()
            {
                var t = Peek();
                switch (t.Kind)
                {
                    case TokenKind.Number:
                        Advance();
                        return new NumberNode(t.Number);
                    case TokenKind.LParen:
                    {
                        Advance();
                        var inner = ParseExpression();
                        Expect(TokenKind.RParen, FormatError(ParseError.ExpectedRParen));
                        return inner;
                    }
                    case TokenKind.Identifier:
                    {
                        Advance();
                        // Function call?
                        if (Peek().Kind == TokenKind.LParen)
                        {
                            Advance();
                            var args = new List<Node>();
                            if (Peek().Kind != TokenKind.RParen)
                            {
                                args.Add(ParseExpression());
                                while (Peek().Kind == TokenKind.Comma)
                                { Advance(); args.Add(ParseExpression()); }
                            }
                            Expect(TokenKind.RParen, FormatError(ParseError.ExpectedRParenAfterArgs));
                            return new CallNode { Name = t.Text, Args = args };
                        }
                        // Indexed source `s[i]`
                        if (t.Text == "s" && Peek().Kind == TokenKind.LBracket)
                        {
                            Advance();
                            var idxNode = ParseExpression();
                            Expect(TokenKind.RBracket, FormatError(ParseError.ExpectedRBracketAfterIndex));
                            return new IndexedSourceNode { IndexNode = idxNode };
                        }
                        // Constants / single-letter variables / true/false
                        if (t.Text.Equals("true", StringComparison.Ordinal)) return new NumberNode(1);
                        if (t.Text.Equals("false", StringComparison.Ordinal)) return new NumberNode(0);
                        if (t.Text.Equals("pi", StringComparison.Ordinal)) return new NumberNode(Math.PI);
                        // NO "e" constant. It used to return Math.E here, which
                        // shadowed the FIFTH source: letters address sources by
                        // Letter - 'a', so 'e' is index 4, and a formula on a
                        // row with five or more sources silently read
                        // 2.718281828 instead of that source's value. There was
                        // no error and no warning. Euler's number also had no
                        // real use in this grammar, since there is no power
                        // operator and no exp(), so it could only ever be a
                        // magic multiplier. "pi" keeps its constant because it
                        // is two characters and cannot collide with a
                        // single-letter source.
                        if (t.Text.Length == 1 && t.Text[0] >= 'a' && t.Text[0] <= 'z')
                            return new SingleLetterSourceNode { Letter = t.Text[0] };
                        // Per-source "active" flag — aD, bD, cD, … zD.
                        // Touchpad-passthrough rows supply a parallel
                        // activeFlags list; everywhere else they're null
                        // and aD-style refs resolve to 0.
                        if (t.Text.Length == 2 && t.Text[0] >= 'a' && t.Text[0] <= 'z' && t.Text[1] == 'D')
                            return new SingleLetterActiveFlagNode { Letter = t.Text[0] };
                        throw new ParseException(FormatError(ParseError.UnknownIdentifier, t.Text, t.Position));
                    }
                    default:
                        throw new ParseException(FormatError(ParseError.UnexpectedToken, t.Text, t.Position));
                }
            }
        }

        // ─── AST ────────────────────────────────────────────────────────

        internal abstract class Node
        {
            public abstract double Eval(IList<float> sources, IList<float> activeFlags);
            public virtual void Walk(ref Compiled c) { }
        }

        internal sealed class NumberNode : Node
        {
            private readonly double _v;
            public NumberNode(double v) { _v = v; }
            public override double Eval(IList<float> _, IList<float> __) => _v;
        }

        internal sealed class SingleLetterSourceNode : Node
        {
            public char Letter;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                int idx = Letter - 'a';
                if (idx < 0 || sources == null || idx >= sources.Count) return 0;
                return sources[idx];
            }
            public override void Walk(ref Compiled c)
            {
                if (!c.ReferencedSingleLetterVars.Contains(Letter))
                    c.ReferencedSingleLetterVars += Letter;
            }
        }

        internal sealed class SingleLetterActiveFlagNode : Node
        {
            public char Letter;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                int idx = Letter - 'a';
                if (idx < 0 || activeFlags == null || idx >= activeFlags.Count) return 0;
                return activeFlags[idx];
            }
            public override void Walk(ref Compiled c)
            {
                // The .D flags share the same positional-source space as
                // plain a/b/c, so aD references should count toward the
                // "missing source" warning the same way a does — a formula
                // referencing aD on a row with zero sources is just as
                // broken as one referencing a.
                if (!c.ReferencedSingleLetterVars.Contains(Letter))
                    c.ReferencedSingleLetterVars += Letter;
            }
        }

        internal sealed class IndexedSourceNode : Node
        {
            public Node IndexNode;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                int idx = (int)IndexNode.Eval(sources, activeFlags);
                if (idx < 0 || sources == null || idx >= sources.Count) return 0;
                return sources[idx];
            }
            public override void Walk(ref Compiled c)
            {
                IndexNode?.Walk(ref c);
                if (IndexNode is NumberNode n)
                {
                    int idx = (int)n.Eval(null, null);
                    if (idx > c.MaxIndexedRef) c.MaxIndexedRef = idx;
                }
            }
        }

        internal sealed class UnaryNode : Node
        {
            public string Op;
            public Node Operand;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                double v = Operand.Eval(sources, activeFlags);
                return Op switch
                {
                    "-" => -v,
                    "!" => v == 0 ? 1 : 0,
                    _ => 0,
                };
            }
            public override void Walk(ref Compiled c) => Operand?.Walk(ref c);
        }

        internal sealed class BinaryNode : Node
        {
            public string Op;
            public Node L, R;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                double l = L.Eval(sources, activeFlags);
                double r = R.Eval(sources, activeFlags);
                switch (Op)
                {
                    case "+": return l + r;
                    case "-": return l - r;
                    case "*": return l * r;
                    case "/": return r == 0 ? 0 : l / r;
                    case "%": return r == 0 ? 0 : l % r;
                    case "<": return l < r ? 1 : 0;
                    case "<=": return l <= r ? 1 : 0;
                    case ">": return l > r ? 1 : 0;
                    case ">=": return l >= r ? 1 : 0;
                    case "==": return l == r ? 1 : 0;
                    case "!=": return l != r ? 1 : 0;
                    case "&&": return (l != 0 && r != 0) ? 1 : 0;
                    case "||": return (l != 0 || r != 0) ? 1 : 0;
                    default: return 0;
                }
            }
            public override void Walk(ref Compiled c) { L?.Walk(ref c); R?.Walk(ref c); }
        }

        internal sealed class TernaryNode : Node
        {
            public Node Cond, Then, Else;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
                => Cond.Eval(sources, activeFlags) != 0 ? Then.Eval(sources, activeFlags) : Else.Eval(sources, activeFlags);
            public override void Walk(ref Compiled c) { Cond?.Walk(ref c); Then?.Walk(ref c); Else?.Walk(ref c); }
        }

        internal sealed class CallNode : Node
        {
            public string Name;
            public List<Node> Args;
            public override double Eval(IList<float> sources, IList<float> activeFlags)
            {
                int n = Args.Count;
                switch (Name)
                {
                    case "abs":   return Need(n, 1) ? Math.Abs(Args[0].Eval(sources, activeFlags)) : 0;
                    case "min":
                        if (n == 0) return 0;
                        { double m = Args[0].Eval(sources, activeFlags); for (int i = 1; i < n; i++) m = Math.Min(m, Args[i].Eval(sources, activeFlags)); return m; }
                    case "max":
                        if (n == 0) return 0;
                        { double m = Args[0].Eval(sources, activeFlags); for (int i = 1; i < n; i++) m = Math.Max(m, Args[i].Eval(sources, activeFlags)); return m; }
                    case "clamp":
                        return Need(n, 3)
                            ? Math.Max(Args[1].Eval(sources, activeFlags), Math.Min(Args[2].Eval(sources, activeFlags), Args[0].Eval(sources, activeFlags)))
                            : 0;
                    case "sign":
                        if (!Need(n, 1)) return 0;
                        { double v = Args[0].Eval(sources, activeFlags); return v > 0 ? 1 : v < 0 ? -1 : 0; }
                    case "floor": return Need(n, 1) ? Math.Floor(Args[0].Eval(sources, activeFlags)) : 0;
                    case "ceil":  return Need(n, 1) ? Math.Ceiling(Args[0].Eval(sources, activeFlags)) : 0;
                    case "round": return Need(n, 1) ? Math.Round(Args[0].Eval(sources, activeFlags)) : 0;
                    case "sqrt":  return Need(n, 1) ? Math.Sqrt(Args[0].Eval(sources, activeFlags)) : 0;
                    case "sin":   return Need(n, 1) ? Math.Sin(Args[0].Eval(sources, activeFlags)) : 0;
                    case "cos":   return Need(n, 1) ? Math.Cos(Args[0].Eval(sources, activeFlags)) : 0;
                    case "tan":   return Need(n, 1) ? Math.Tan(Args[0].Eval(sources, activeFlags)) : 0;
                    case "atan2": return Need(n, 2) ? Math.Atan2(Args[0].Eval(sources, activeFlags), Args[1].Eval(sources, activeFlags)) : 0;
                    case "lerp":
                        if (!Need(n, 3)) return 0;
                        { double a = Args[0].Eval(sources, activeFlags), b = Args[1].Eval(sources, activeFlags), t = Args[2].Eval(sources, activeFlags); return a + (b - a) * t; }
                    default:
                        return 0;
                }
            }
            private static bool Need(int n, int expected) => n == expected;
            public override void Walk(ref Compiled c)
            {
                if (Args != null) foreach (var a in Args) a?.Walk(ref c);
            }
        }
    }
}
