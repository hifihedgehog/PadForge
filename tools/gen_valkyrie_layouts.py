"""The Valkyrie VK99 light tables, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/ValkyrieKeyboardController:
- RGBController_ValkyrieKeyboard.cpp: matrix_map and normal_matrix_map, the
  6 by 22 grids of the VK99 Pro and the VK99 with each cell the number of
  the light there or NA, and led_names and normal_led_names, each light's
  name in light order.
- ValkyrieKeyboardController.h: key_code_99 and key_code_103, the key code
  each light's color goes out with, in light order.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ name.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/ValkyrieLayouts.g.cs:
per board, each light in order with its name, key code, row and column.

Usage:
    python tools/gen_valkyrie_layouts.py OPENRGB_ROOT
    python tools/gen_valkyrie_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "ValkyrieLayouts.g.cs")

RGB_REL = "RGBController_ValkyrieKeyboard.cpp"
CTL_REL = "ValkyrieKeyboardController.h"
BOARDS = [
    ("Pro", "matrix_map", "led_names", "key_code_99", 98),
    ("Normal", "normal_matrix_map", "normal_led_names", "key_code_103", 102),
]
ROWS, COLS = 6, 22


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

    folder = os.path.join(args.openrgb, "Controllers", "ValkyrieKeyboardController")
    rgb = strip_comments(read(os.path.join(folder, RGB_REL)))
    ctl = strip_comments(read(os.path.join(folder, CTL_REL)))
    names = key_names(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp"))
    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()

    tables = []
    for cs_name, matrix, led_names, codes_name, count in BOARDS:
        mbody, mline = array_body(rgb, matrix)
        cells = [None if t == "NA" else int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\d+|NA", mbody)]
        if len(cells) != ROWS * COLS:
            sys.exit("%s: %d cells" % (matrix, len(cells)))
        where = {}
        for idx, value in enumerate(cells):
            if value is not None:
                if value in where:
                    sys.exit("%s: light %d twice" % (matrix, value))
                where[value] = (idx // COLS, idx % COLS)
        nbody, nline = array_body(rgb, led_names)
        labels = []
        for token in re.findall(r'"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*', nbody):
            labels.append(c_string(token) if token.startswith('"') else names[token])
        cbody, cline = array_body(ctl, codes_name)
        codes = [int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\d+", cbody)]
        if len(labels) != count or len(codes) != count or sorted(where) != list(range(count)):
            sys.exit("%s: %d names, %d codes, %d cells, want %d" % (cs_name, len(labels), len(codes), len(where), count))
        lights = [(labels[n], codes[n], where[n][0], where[n][1]) for n in range(count)]
        tables.append((cs_name, matrix, mline, led_names, nline, codes_name, cline, lights))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_valkyrie_layouts.py from OpenRGB at %s" % commit[:10])
    out.append("//     (Controllers/ValkyrieKeyboardController: the matrix maps and LED names of")
    out.append("//     RGBController_ValkyrieKeyboard.cpp and the key codes of")
    out.append("//     ValkyrieKeyboardController.h, with the key names of")
    out.append("//     RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class ValkyrieLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for cs_name, matrix, mline, led_names, nline, codes_name, cline, lights in tables:
        out.append("")
        out.append("        /// <summary>%s:%d %s, %s:%d %s and %s:%d %s:" % (RGB_REL, nline, led_names, CTL_REL, cline, codes_name, RGB_REL, mline, matrix))
        out.append("        /// each light in order with its name, key code, row and column.</summary>")
        out.append("        internal static readonly ValkyrieLed[] %s =" % cs_name)
        out.append("        {")
        for name, code, row, col in lights:
            out.append("            new(%s, 0x%02X, %d, %d)," % (cs_string(name), code, row, col))
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
    print("wrote %s: %s" % (OUTPUT, ", ".join("%s %d" % (t[0], len(t[7])) for t in tables)))


if __name__ == "__main__":
    main()
