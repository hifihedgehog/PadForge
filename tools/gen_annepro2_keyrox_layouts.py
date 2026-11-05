"""The Anne Pro 2 and Red Square Keyrox TKL light tables, from OpenRGB (#557).

WHAT IT READS:
- Controllers/AnnePro2Controller/RGBController_AnnePro2.cpp: matrix_map, the
  5 by 14 grid with each cell the number of the light there or NA, and
  led_names, each light's name and index in light order.
- Controllers/RedSquareKeyroxController/RedSquareKeyroxController/RGBController_RedSquareKeyrox.cpp:
  keyrox_tkl, its width and height, its matrix map, its LED names by light
  number and its led_sequence_positions, each light's place in the color
  report.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ name.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/AnnePro2KeyroxLayouts.g.cs:
per board, each light in order with its name, its place in the board's
frame, and its row and column.

Run it without Python's -I switch: it imports tools/openrgb_cpp.py.

Usage:
    python tools/gen_annepro2_keyrox_layouts.py OPENRGB_ROOT
    python tools/gen_annepro2_keyrox_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys
import openrgb_cpp as oc

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "AnnePro2KeyroxLayouts.g.cs")

ANNE_REL = "Controllers/AnnePro2Controller/RGBController_AnnePro2.cpp"
KEYROX_REL = "Controllers/RedSquareKeyroxController/RedSquareKeyroxController/RGBController_RedSquareKeyrox.cpp"


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


def braces(text, start):
    """The text between the brace at or after start and its match, and the
    index after it."""
    i = text.index("{", start)
    depth, j = 1, i + 1
    while depth:
        c = text[j]
        if c == '"':
            j += 1
            while text[j] != '"':
                j += 2 if text[j] == "\\" else 1
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        j += 1
    return text[i + 1:j - 1], j


def top_level(body):
    """The top-level comma-separated parts of a brace body."""
    parts, depth, cur = [], 0, []
    i = 0
    while i < len(body):
        c = body[i]
        if c == '"':
            j = i + 1
            while body[j] != '"':
                j += 2 if body[j] == "\\" else 1
            cur.append(body[i:j + 1])
            i = j + 1
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        if c == "," and depth == 0:
            parts.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    if "".join(cur).strip():
        parts.append("".join(cur).strip())
    return parts


def cells(text):
    return [None if t == "NA" else int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\d+|NA", text)]


def line_of(text, index):
    return text.count("\n", 0, index) + 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    names = key_names(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp"))
    commit = oc.git_head(args.openrgb)

    anne = strip_comments(read(os.path.join(args.openrgb, ANNE_REL)))
    m = re.search(r"matrix_map\s*\[5\]\s*\[14\]\s*=", anne)
    body, _ = braces(anne, m.end())
    grid = cells(body)
    if len(grid) != 70:
        sys.exit("Anne Pro 2 matrix: %d cells" % len(grid))
    where = {v: (i // 14, i % 14) for i, v in enumerate(grid) if v is not None}
    n = re.search(r"led_names\s*\[\s*\]\s*=", anne)
    body, _ = braces(anne, n.end())
    anne_lights = []
    for name, idx in re.findall(r"\{\s*(\w+)\s*,\s*(\d+)\s*\}", body):
        anne_lights.append((names[name], int(idx)))
    if len(anne_lights) != 61 or sorted(where) != list(range(61)):
        sys.exit("Anne Pro 2: %d names, %d cells" % (len(anne_lights), len(where)))
    anne_rows = [(name, idx, where[i][0], where[i][1]) for i, (name, idx) in enumerate(anne_lights)]

    keyrox = strip_comments(read(os.path.join(args.openrgb, KEYROX_REL)))
    k = re.search(r"static\s+keyrox\s+keyrox_tkl\s*=", keyrox)
    body, _ = braces(keyrox, k.end())
    parts = top_level(body)
    if len(parts) != 5:
        sys.exit("keyrox_tkl: %d fields" % len(parts))
    width, height = int(parts[0], 0), int(parts[1], 0)
    grid = cells(parts[2])
    labels = [c_string(t) if t.startswith('"') else names[t]
              for t in re.findall(r'"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*', parts[3])]
    positions = cells(parts[4])
    if len(grid) != width * height:
        sys.exit("keyrox_tkl matrix: %d cells" % len(grid))
    keyrox_rows = []
    for i, value in enumerate(grid):
        if value is None:
            continue
        led = len(keyrox_rows)
        keyrox_rows.append((labels[value], positions[led], i // width, i % width))
    if len(keyrox_rows) != len(positions):
        sys.exit("keyrox_tkl: %d lights, %d positions" % (len(keyrox_rows), len(positions)))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_annepro2_keyrox_layouts.py from OpenRGB at %s" % commit[:10])
    out.append("//     (RGBController_AnnePro2.cpp matrix_map and led_names, and")
    out.append("//     RGBController_RedSquareKeyrox.cpp keyrox_tkl, with the key names of")
    out.append("//     RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class AnnePro2KeyroxLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    out.append("")
    out.append("        /// <summary>RGBController_AnnePro2.cpp:%d led_names and :%d matrix_map:"
               % (line_of(anne, n.start()), line_of(anne, m.start())))
    out.append("        /// each light in order with its name, index, row and column.</summary>")
    out.append("        internal static readonly AnneKeyroxLed[] AnnePro2 =")
    out.append("        {")
    for name, idx, row, col in anne_rows:
        out.append("            new(%s, %d, %d, %d)," % (cs_string(name), idx, row, col))
    out.append("        };")
    out.append("")
    out.append("        /// <summary>RGBController_RedSquareKeyrox.cpp:%d keyrox_tkl: each light in the" % line_of(keyrox, k.start()))
    out.append("        /// order SetupZones makes it, with its name, its led_sequence_positions")
    out.append("        /// entry, row and column.</summary>")
    out.append("        internal static readonly AnneKeyroxLed[] KeyroxTkl =")
    out.append("        {")
    for name, pos, row, col in keyrox_rows:
        out.append("            new(%s, %d, %d, %d)," % (cs_string(name), pos, row, col))
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
    print("wrote %s: Anne Pro 2 %d, Keyrox TKL %d" % (OUTPUT, len(anne_rows), len(keyrox_rows)))


if __name__ == "__main__":
    main()
