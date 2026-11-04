"""The GaiZhongGai light tables, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/GaiZongGaiKeyboardController:
- RGBController_GaiZhongGai.cpp: the matrix maps of the 68+4 PRO, the 42 PRO,
  the Dial, the 17 PRO, the 20 PRO and the 17+4+Touch PRO, each cell the
  number of the light there or NA, and led_names_general and led_names_42key,
  each light's name in light order.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ name.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/GaiZhongGaiLayouts.g.cs:
per board, the lights its matrix holds, by number, each with its name, row
and column.

Usage:
    python tools/gen_gaizhonggai_layouts.py OPENRGB_ROOT
    python tools/gen_gaizhonggai_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "GaiZhongGaiLayouts.g.cs")

RGB_REL = "RGBController_GaiZhongGai.cpp"
# The C# name, the matrix, its rows and columns as declared, and the names
# the board's lights take (RGBController_GaiZhongGai.cpp:653-669).
BOARDS = [
    ("Pro68", "matrix_map_68", 5, 17, "led_names_general"),
    ("Pro42", "matrix_map_42", 4, 12, "led_names_42key"),
    ("Dial", "matrix_map_dial", 2, 2, "led_names_general"),
    ("Pad17", "matrix_map_17PAD", 5, 4, "led_names_general"),
    ("Pad20", "matrix_map_20PAD", 6, 4, "led_names_general"),
    ("PadTouch", "matrix_map_PAD_Touch", 5, 5, "led_names_general"),
]


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def strip_comments(text):
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif text.startswith("/*", i):
            j = text.index("*/", i) + 2
            out.append(re.sub(r"[^\n]", " ", text[i:j]))
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def c_string(literal):
    body = literal[1:-1]
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), body)


def cs_string(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def key_names(path):
    names = {}
    for name, literal in re.findall(r'const\s+char\s*\*\s*(\w+)\s*=\s*("(?:[^"\\]|\\.)*")\s*;', read(path)):
        names[name] = c_string(literal)
    return names


def array_body(text, name):
    m = re.search(r"\b" + re.escape(name) + r"\s*(?:\[[^\]]*\]\s*)+=?\s*\{", text)
    if not m:
        sys.exit("no array " + name)
    depth, i = 1, m.end()
    while depth:
        c = text[i]
        if c == '"':
            i += 1
            while text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        i += 1
    return text[m.end():i - 1], text.count("\n", 0, m.start()) + 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    folder = os.path.join(args.openrgb, "Controllers", "GaiZongGaiKeyboardController")
    rgb = strip_comments(read(os.path.join(folder, RGB_REL)))
    names = key_names(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp"))
    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()

    name_lists = {}
    for list_name in ("led_names_general", "led_names_42key"):
        body, line = array_body(rgb, list_name)
        labels = []
        for token in re.findall(r'"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*', body):
            labels.append(c_string(token) if token.startswith('"') else names[token])
        name_lists[list_name] = (labels, line)

    tables = []
    for cs_name, matrix, rows, cols, list_name in BOARDS:
        body, line = array_body(rgb, matrix)
        cells = [None if t == "NA" else int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\d+|NA", body)]
        if len(cells) != rows * cols:
            sys.exit("%s: %d cells" % (matrix, len(cells)))
        labels = name_lists[list_name][0]
        lights = []
        for idx, value in enumerate(cells):
            if value is None:
                continue
            if value >= len(labels):
                sys.exit("%s: light %d has no name" % (matrix, value))
            lights.append((value, labels[value], idx // cols, idx % cols))
        lights.sort()
        if len({v for v, _, _, _ in lights}) != len(lights):
            sys.exit("%s: a light twice" % matrix)
        tables.append((cs_name, matrix, line, list_name, name_lists[list_name][1], lights))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_gaizhonggai_layouts.py from OpenRGB at %s" % commit[:10])
    out.append("//     (Controllers/GaiZongGaiKeyboardController: the matrix maps and LED names of")
    out.append("//     RGBController_GaiZhongGai.cpp, with the key names of")
    out.append("//     RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class GaiZhongGaiLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for cs_name, matrix, line, list_name, nline, lights in tables:
        out.append("")
        out.append("        /// <summary>%s:%d %s with the names of %s (line %d): the lights"
                   % (RGB_REL, line, matrix, list_name, nline))
        out.append("        /// the matrix holds, by number, each with its name, row and column.</summary>")
        out.append("        internal static readonly GaiZhongGaiLed[] %s =" % cs_name)
        out.append("        {")
        for value, name, row, col in lights:
            out.append("            new(%d, %s, %d, %d)," % (value, cs_string(name), row, col))
        out.append("        };")
    out.append("    }")
    out.append("}")
    text = "\r\n".join(out) + "\r\n"

    if args.check:
        try:
            with open(OUTPUT, encoding="utf-8-sig", newline="") as f:
                current = f.read()
        except FileNotFoundError:
            current = ""
        print("up to date" if current == text else "differs")
        sys.exit(0 if current == text else 1)
    with open(OUTPUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote %s: %s" % (OUTPUT, ", ".join("%s %d" % (t[0], len(t[5])) for t in tables)))


if __name__ == "__main__":
    main()
