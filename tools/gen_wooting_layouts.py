"""The Wooting keyboard light tables, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/WootingKeyboardController:
- RGBController_WootingKeyboard.cpp: each keyboard type's matrix map (the
  index of each grid cell's light in the 6 by 21 frame buffer, or NA) and its
  LED names, the grids SetupZones walks row by row to make the lights.
- WootingV1KeyboardController/WootingV1KeyboardController.cpp:
  rgb_led_index[6][21], the LED driver index of each frame buffer cell, and
  pwm_mem_map[48], where each of 24 LEDs sits in a 96-byte driver buffer.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ name.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/WootingLayouts.g.cs,
the tables as OpenRGB holds them: per type, the lights in the order SetupZones
makes them, each with its row, column, frame buffer index and OpenRGB's name.

Run it without Python's -I switch: it imports tools/openrgb_cpp.py.

Usage:
    python tools/gen_wooting_layouts.py OPENRGB_ROOT
    python tools/gen_wooting_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys
import openrgb_cpp as oc

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "WootingLayouts.g.cs")

NA = 0xFFFFFFFF

# The matrix and name grids SetupZones takes per type, and their size
# (RGBController_WootingKeyboard.cpp:654-688).
TYPES = [
    ("Tkl", "matrix_map_tkl", "led_names_tkl", 6, 17),
    ("EightyHe", "matrix_map_80HE", "led_names_80HE", 6, 17),
    ("Sixty", "matrix_map_60", "led_names_60HE", 5, 14),
    ("ThreePad", "matrix_map_3pad", "led_names_3pad", 5, 7),
    ("Full", "matrix_map_full", "led_names_full", 6, 21),
]


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def strip_comments(text):
    """The source with its comments blanked out, string literals and line
    numbers kept."""
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
    """The text between the outer braces of the array named name, and the
    line its name sits on."""
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


def numbers(body, defines):
    out = []
    for token in re.findall(r"0x[0-9A-Fa-f]+|\d+|[A-Za-z_]\w*", body):
        if re.match(r"^(0x[0-9A-Fa-f]+|\d+)$", token):
            out.append(int(token, 0))
        elif token in defines:
            out.append(defines[token])
        else:
            sys.exit("unknown value " + token)
    return out


def strings(body, names):
    out = []
    for token in re.findall(r'"(?:[^"\\]|\\.)*"|[A-Za-z_]\w*', body):
        if token.startswith('"'):
            out.append(c_string(token))
        elif token in names:
            out.append(names[token])
        else:
            sys.exit("unknown key name " + token)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    folder = os.path.join(args.openrgb, "Controllers", "WootingKeyboardController")
    rgb_rel = "RGBController_WootingKeyboard.cpp"
    v1_rel = "WootingV1KeyboardController/WootingV1KeyboardController.cpp"
    rgb = strip_comments(read(os.path.join(folder, rgb_rel)))
    v1 = strip_comments(read(os.path.join(folder, v1_rel)))
    names = key_names(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp"))
    commit = oc.git_head(args.openrgb)

    tables = []
    for cs_name, matrix, led_names, rows, cols in TYPES:
        mbody, mline = array_body(rgb, matrix)
        nbody, nline = array_body(rgb, led_names)
        cells = numbers(mbody, {"NA": NA})
        labels = strings(nbody, names)
        if len(cells) != rows * cols or len(labels) != rows * cols:
            sys.exit("%s: %d cells, %d names, want %d" % (cs_name, len(cells), len(labels), rows * cols))
        lights = []
        for idx in range(rows * cols):
            if cells[idx] == NA:
                continue
            if cells[idx] >= 6 * 21:
                sys.exit("%s: index %d past the frame buffer" % (cs_name, cells[idx]))
            lights.append((idx // cols, idx % cols, cells[idx], labels[idx]))
        tables.append((cs_name, matrix, mline, led_names, nline, rows, cols, lights))

    lbody, lline = array_body(v1, "rgb_led_index")
    led_index = numbers(lbody, {"NA": NA})
    if len(led_index) != 6 * 21:
        sys.exit("rgb_led_index: %d cells" % len(led_index))
    pbody, pline = array_body(v1, "pwm_mem_map")
    pwm = numbers(pbody, {})
    if len(pwm) != 48:
        sys.exit("pwm_mem_map: %d entries" % len(pwm))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_wooting_layouts.py from OpenRGB at %s" % commit[:10])
    out.append("//     (Controllers/WootingKeyboardController: the matrix maps and LED names of")
    out.append("//     RGBController_WootingKeyboard.cpp, and rgb_led_index[] and pwm_mem_map[] of")
    out.append("//     WootingV1KeyboardController.cpp, with the key names of")
    out.append("//     RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class WootingLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for cs_name, matrix, mline, led_names, nline, rows, cols, lights in tables:
        out.append("")
        out.append("        /// <summary>%s:%d %s and %s:%d %s, %d by %d: each light in the"
                   % (rgb_rel, mline, matrix, rgb_rel, nline, led_names, rows, cols))
        out.append("        /// order SetupZones makes it, its row, column, frame buffer index and name.</summary>")
        out.append("        internal static readonly WootingLed[] %s =" % cs_name)
        out.append("        {")
        for r, c, value, name in lights:
            out.append("            new(%d, %d, %d, %s)," % (r, c, value, cs_string(name)))
        out.append("        };")
    out.append("")
    out.append("        /// <summary>%s:%d rgb_led_index: the LED driver index of each cell of the" % (v1_rel.split("/")[-1], lline))
    out.append("        /// 6 by 21 frame buffer, row by row, 255 for none.</summary>")
    out.append("        internal static readonly byte[] V1LedIndex =")
    out.append("        {")
    for r in range(6):
        part = led_index[r * 21:(r + 1) * 21]
        out.append("            " + ", ".join("%3d" % (255 if v == NA else v) for v in part) + ",")
    out.append("        };")
    out.append("")
    out.append("        /// <summary>%s:%d pwm_mem_map: where each of 24 LEDs sits in a" % (v1_rel.split("/")[-1], pline))
    out.append("        /// 96-byte driver buffer.</summary>")
    out.append("        internal static readonly byte[] V1PwmMap =")
    out.append("        {")
    for i in range(0, 48, 12):
        out.append("            " + ", ".join("0x%02X" % v for v in pwm[i:i + 12]) + ",")
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
