"""Read C++ data out of OpenRGB's source for PadForge's generators (#557).

OpenRGB keeps its device tables as C++ initializers. This module reads them
the way a compiler would for the plain cases those tables use: it drops
comments, collects #define lines, splits the rest into tokens, finds a
top-level definition by name and parses its braces into nested lists. It
evaluates integer expressions built from numbers, the #define constants and
function-like macros of the file, enum constants handed in by the caller and
the operators | & << >> + - * ( ). Anything else is an error, so a table
OpenRGB writes in a new way stops the generator instead of being misread.
"""
import re
import subprocess

_TOKEN = re.compile(r"""
    (?P<ws>\s+)
  | (?P<str>"(?:\\.|[^"\\\n])*")
  | (?P<chr>'(?:\\.|[^'\\\n])')
  | (?P<num>0[xX][0-9A-Fa-f]+[uUlL]*|\d+[uUlL]*)
  | (?P<id>[A-Za-z_]\w*(?:\s*::\s*[A-Za-z_]\w*)*)
  | (?P<op><<|>>|[{}()\[\],;=&|<>+\-*/~!?:.%^])
""", re.X)

_ESCAPES = {"n": "\n", "t": "\t", "\\": "\\", "\"": "\"", "'": "'", "0": "\0"}


class Token:
    __slots__ = ("kind", "text", "line")

    def __init__(self, kind, text, line):
        self.kind, self.text, self.line = kind, text, line

    def __repr__(self):
        return "%s(%r)@%d" % (self.kind, self.text, self.line)


class CppError(Exception):
    pass


def strip_comments(text):
    """The source with comments blanked to spaces, newlines and string
    literals kept, so line numbers stay true."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"' or c == "'":
            j = i + 1
            while j < n and text[j] != c:
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            if j < 0:
                raise CppError("unclosed comment")
            out.append(re.sub(r"[^\n]", " ", text[i:j + 2]))
            i = j + 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def decode_string(literal):
    """The value of a C string literal."""
    body = literal[1:-1]
    out = []
    i = 0
    while i < len(body):
        if body[i] == "\\":
            esc = body[i + 1]
            if esc not in _ESCAPES:
                raise CppError("escape \\%s" % esc)
            out.append(_ESCAPES[esc])
            i += 2
        else:
            out.append(body[i])
            i += 1
    return "".join(out)


class Source:
    """One OpenRGB file: its tokens, its #define lines and where each
    top-level definition starts."""

    def __init__(self, root, rel):
        self.rel = rel.replace("\\", "/")
        with open(root + "/" + self.rel, encoding="utf-8") as f:
            self.raw = f.read()
        text = strip_comments(self.raw)
        self.defines = {}
        self.macros = {}
        lines = text.split("\n")
        i = 0
        while i < len(lines):
            if lines[i].lstrip().startswith("#"):
                start = i
                directive = lines[i]
                while directive.endswith("\\"):
                    i += 1
                    directive = directive[:-1] + " " + lines[i]
                self._directive(directive.strip())
                for k in range(start, i + 1):
                    lines[k] = ""
            i += 1
        self.tokens = []
        for number, line in enumerate(lines, 1):
            pos = 0
            while pos < len(line):
                m = _TOKEN.match(line, pos)
                if not m:
                    raise CppError("%s:%d: cannot read %r" % (self.rel, number, line[pos:pos + 20]))
                pos = m.end()
                kind = m.lastgroup
                if kind != "ws":
                    self.tokens.append(Token(kind, re.sub(r"\s+", "", m.group()) if kind == "id" else m.group(), number))

    def _directive(self, line):
        m = re.match(r"#\s*define\s+(\w+)\(([^)]*)\)\s*(.*)$", line)
        if m:
            self.macros[m.group(1)] = ([a.strip() for a in m.group(2).split(",")], m.group(3))
            return
        m = re.match(r"#\s*define\s+(\w+)\s+(.*)$", line)
        if m:
            self.defines[m.group(1)] = m.group(2).strip()

    def find(self, name):
        """The index of the first token after the name of the top-level
        definition called name, or None."""
        depth = 0
        toks = self.tokens
        for i, t in enumerate(toks):
            if t.text == "{":
                depth += 1
            elif t.text == "}":
                depth -= 1
            elif depth == 0 and t.kind == "id" and t.text == name and i > 0 and toks[i - 1].kind in ("id", "op"):
                j = i + 1
                if toks[j].text == "[":
                    while toks[j].text != "]":
                        j += 1
                    j += 1
                if toks[j].text == "=":
                    j += 1
                if toks[j].text == "{":
                    return j
        return None

    def definition(self, name):
        """The parsed braces of the definition called name and the line
        its name sits on."""
        j = self.find(name)
        if j is None:
            raise CppError("%s: no definition of %s" % (self.rel, name))
        node, end = parse_braces(self.tokens, j)
        return node, self.tokens[j].line

    def line_of(self, name):
        j = self.find(name)
        if j is None:
            raise CppError("%s: no definition of %s" % (self.rel, name))
        k = j
        while self.tokens[k].text != name:
            k -= 1
        return self.tokens[k].line

    def definitions_of_type(self, type_name):
        """The names of every top-level definition whose type is type_name,
        in file order."""
        names = []
        depth = 0
        toks = self.tokens
        for i, t in enumerate(toks):
            if t.text == "{":
                depth += 1
            elif t.text == "}":
                depth -= 1
            elif depth == 0 and t.kind == "id" and t.text == type_name and i + 1 < len(toks) and toks[i + 1].kind == "id":
                name = toks[i + 1].text
                if self.find(name) is not None:
                    names.append(name)
        return names


class Node(list):
    """A braced list. Each element is a Node or an Expr."""
    line = 0


class Expr(list):
    """The tokens of one element that is not braced."""
    line = 0


def parse_braces(tokens, i):
    """Parse the braces opening at tokens[i]. Returns the Node and the index
    after its closing brace."""
    assert tokens[i].text == "{"
    node = Node()
    node.line = tokens[i].line
    i += 1
    current = None
    parens = 0
    while True:
        t = tokens[i]
        if t.text == "(":
            parens += 1
        elif t.text == ")":
            parens -= 1
        if parens > 0 and t.text not in ("{", "}"):
            if current is None:
                current = Expr()
                current.line = t.line
            current.append(t)
            i += 1
            continue
        if t.text == "}":
            if current is not None:
                node.append(current)
            return node, i + 1
        if t.text == "{":
            if current is not None and len(current):
                raise CppError("line %d: braces after an expression" % t.line)
            child, i = parse_braces(tokens, i)
            node.append(child)
            current = None
            if tokens[i].text == ",":
                i += 1
            elif tokens[i].text != "}":
                raise CppError("line %d: %r after braces" % (tokens[i].line, tokens[i].text))
            continue
        if t.text == ",":
            if current is None:
                raise CppError("line %d: empty element" % t.line)
            node.append(current)
            current = None
            i += 1
            continue
        if current is None:
            current = Expr()
            current.line = t.line
        current.append(t)
        i += 1


class Evaluator:
    """Integer and string values of expressions, with the #define constants
    and macros of the sources given and the constants the caller adds."""

    def __init__(self, sources, constants=None, strings=None):
        self.defines = {}
        self.macros = {}
        for s in sources:
            self.defines.update(s.defines)
            self.macros.update(s.macros)
        self.constants = dict(constants or {})
        self.strings = dict(strings or {})

    def string(self, expr):
        if len(expr) != 1:
            raise CppError("line %d: not a single string: %r" % (expr.line, expr))
        t = expr[0]
        if t.kind == "str":
            return decode_string(t.text)
        if t.kind == "id" and t.text in self.strings:
            return self.strings[t.text]
        raise CppError("line %d: unknown string %r" % (t.line, t.text))

    def integer(self, expr):
        toks = [(t.kind, t.text) for t in expr]
        value, rest = self._expr(self._expand(toks, 0), 0)
        if rest:
            raise CppError("line %d: left over %r" % (expr.line, rest))
        return value

    def _expand(self, toks, depth):
        if depth > 20:
            raise CppError("macro recursion")
        out = []
        i = 0
        while i < len(toks):
            kind, text = toks[i]
            if kind == "id" and text in self.macros and i + 1 < len(toks) and toks[i + 1][1] == "(":
                params, body = self.macros[text]
                args, cur, level, j = [], [], 0, i + 2
                while True:
                    k2, t2 = toks[j]
                    if t2 == "(":
                        level += 1
                    elif t2 == ")":
                        if level == 0:
                            break
                        level -= 1
                    if t2 == "," and level == 0:
                        args.append(cur)
                        cur = []
                    else:
                        cur.append((k2, t2))
                    j += 1
                args.append(cur)
                if len(args) != len(params):
                    raise CppError("macro %s takes %d arguments" % (text, len(params)))
                body_toks = self._tokenize(body)
                sub = []
                for bk, bt in body_toks:
                    if bk == "id" and bt in params:
                        sub.append(("op", "("))
                        sub.extend(args[params.index(bt)])
                        sub.append(("op", ")"))
                    else:
                        sub.append((bk, bt))
                out.extend(self._expand(sub, depth + 1))
                i = j + 1
            elif kind == "id" and text in self.defines and text not in self.constants:
                out.append(("op", "("))
                out.extend(self._expand(self._tokenize(self.defines[text]), depth + 1))
                out.append(("op", ")"))
                i += 1
            else:
                out.append((kind, text))
                i += 1
        return out

    @staticmethod
    def _tokenize(text):
        out = []
        pos = 0
        while pos < len(text):
            m = _TOKEN.match(text, pos)
            if not m:
                raise CppError("cannot read %r" % text[pos:])
            pos = m.end()
            if m.lastgroup != "ws":
                out.append((m.lastgroup, re.sub(r"\s+", "", m.group())))
        return out

    _LEVELS = [["|"], ["^"], ["&"], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]]

    def _expr(self, toks, level):
        if level == len(self._LEVELS):
            return self._unary(toks)
        value, toks = self._expr(toks, level + 1)
        while toks and toks[0][1] in self._LEVELS[level]:
            op = toks[0][1]
            rhs, toks = self._expr(toks[1:], level + 1)
            value = {
                "|": lambda a, b: a | b, "^": lambda a, b: a ^ b, "&": lambda a, b: a & b,
                "<<": lambda a, b: a << b, ">>": lambda a, b: a >> b,
                "+": lambda a, b: a + b, "-": lambda a, b: a - b,
                "*": lambda a, b: a * b, "/": lambda a, b: a // b, "%": lambda a, b: a % b,
            }[op](value, rhs)
        return value, toks

    def _unary(self, toks):
        if not toks:
            raise CppError("expression ends early")
        kind, text = toks[0]
        if text == "-":
            v, rest = self._unary(toks[1:])
            return -v, rest
        if text == "~":
            v, rest = self._unary(toks[1:])
            return ~v, rest
        if text == "(":
            v, rest = self._expr(toks[1:], 0)
            if not rest or rest[0][1] != ")":
                raise CppError("no closing parenthesis")
            return v, rest[1:]
        if kind == "num":
            return int(text.rstrip("uUlL"), 0), toks[1:]
        if kind == "id" and text in self.constants:
            return self.constants[text], toks[1:]
        raise CppError("unknown value %r" % text)


def enum_values(source, enum_name, constants=None):
    """The constants of a C++ enum as a dict, each value worked out the way
    the compiler does: the given value, or one more than the one before."""
    toks = source.tokens
    for i, t in enumerate(toks):
        if t.text == "enum" and toks[i + 1].text == enum_name and toks[i + 2].text == "{":
            node, _ = parse_braces(toks, i + 2)
            break
        # An unnamed enum is found by its first constant.
        if t.text == "enum" and toks[i + 1].text == "{" and toks[i + 2].text == enum_name:
            node, _ = parse_braces(toks, i + 1)
            break
    else:
        raise CppError("%s: no enum %s" % (source.rel, enum_name))
    values = {}
    ev = Evaluator([source], constants)
    nxt = 0
    for element in node:
        name = element[0].text
        if len(element) > 1:
            if element[1].text != "=":
                raise CppError("enum %s: %r" % (enum_name, element))
            sub = Expr(element[2:])
            sub.line = element.line
            ev.constants.update(values)
            nxt = ev.integer(sub)
        values[name] = nxt
        nxt += 1
    return values


def git_head(root):
    return subprocess.run(["git", "-C", root, "rev-parse", "HEAD"],
                          capture_output=True, text=True, check=True).stdout.strip()
