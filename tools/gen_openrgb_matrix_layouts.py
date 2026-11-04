"""The key grids of OpenRGB's Cherry, EVision and Das Keyboard families (#557).

OpenRGB lays these keyboards out without its KeyboardLayoutManager: each
family's RGBController file holds a key grid, each cell an index into the
keyboard's LED list or NA, and some hold the LED names too. The families keep
those tables as OpenRGB holds them.

WHAT IT READS, under OpenRGB's Controllers:
- CherryKeyboardController/RGBController_CherryKeyboard.cpp: matrix_map, 126
  LEDs named "Keyboard LED n" (no key names).
- EVisionKeyboardController/EVisionV2KeyboardController/RGBController_EVisionKeyboard.cpp:
  matrix_map of the first-generation EVision keyboards, 126 LEDs, no key names.
- EVisionKeyboardController/EVisionV2KeyboardController/RGBController_EVisionV2Keyboard.cpp:
  evisionv2_matrix and led_evisionv2 (106 LEDs), endorfy_matrix and
  led_endorfy (104 LEDs).
- EVisionKeyboardController/EVisionV2KeyboardController/EVisionV2KeyboardController.cpp:
  evisionv2_map and endorfy_map, where each LED's color sits in the frame.
- DasKeyboardController/RGBController_DasKeyboard.cpp: matrix_map_us,
  matrix_map_eu and led_names (131 LEDs).
- RGBController/RGBControllerKeyNames.cpp for the strings the KEY_ names
  stand for.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/MatrixKeyboardLayouts.g.cs,
with CRLF line endings.

Usage:
    python tools/gen_openrgb_matrix_layouts.py OPENRGB_ROOT
    python tools/gen_openrgb_matrix_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "MatrixKeyboardLayouts.g.cs")

C = "Controllers/"
CHERRY = C + "CherryKeyboardController/RGBController_CherryKeyboard.cpp"
EVISION = C + "EVisionKeyboardController/EVisionV2KeyboardController/RGBController_EVisionKeyboard.cpp"
EVISION_V2 = C + "EVisionKeyboardController/EVisionV2KeyboardController/RGBController_EVisionV2Keyboard.cpp"
EVISION_V2_H = C + "EVisionKeyboardController/EVisionV2KeyboardController/EVisionV2KeyboardController.h"
EVISION_V2_CPP = C + "EVisionKeyboardController/EVisionV2KeyboardController/EVisionV2KeyboardController.cpp"
DAS = C + "DasKeyboardController/RGBController_DasKeyboard.cpp"
KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"


def read(root, rel):
    with open(os.path.join(root, rel), encoding="utf-8-sig") as f:
        return oc.strip_comments(f.read())


def body_of(text, name, rel):
    """The text between the braces of the definition called name, and the
    line its name sits on."""
    m = re.search(r"\b" + re.escape(name) + r"\s*(\[[^\]]*\]\s*)*=\s*\{", text)
    if not m:
        sys.exit("%s: no %s" % (rel, name))
    depth, i = 1, m.end()
    while depth:
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    return text[m.end():i - 1], text.count("\n", 0, m.start()) + 1


def grid(text, name, rel, rows, cols):
    body, line = body_of(text, name, rel)
    cells = [None if t == "NA" else int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\b\d+\b|\bNA\b", body)]
    if len(cells) != rows * cols:
        sys.exit("%s: %s has %d cells, not %d by %d" % (rel, name, len(cells), rows, cols))
    return cells, line


def numbers(text, name, rel):
    body, line = body_of(text, name, rel)
    return [int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\b\d+\b", body)], line


def names(text, name, rel, key_names):
    body, line = body_of(text, name, rel)
    out = []
    for token in re.findall(r'"(?:\\.|[^"\\])*"|\b[A-Z][A-Z0-9_]*\b', body):
        if token.startswith('"'):
            out.append(oc.decode_string(token))
        elif token in key_names:
            out.append(key_names[token])
        else:
            sys.exit("%s: %s names %s, which is no key name" % (rel, name, token))
    return out, line


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.openrgb

    key_names = {}
    toks = oc.Source(root, KEY_NAMES).tokens
    for i, t in enumerate(toks):
        if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
            key_names[t.text] = oc.decode_string(toks[i + 2].text)

    v2h = oc.Source(root, EVISION_V2_H)
    height = int(v2h.defines["EVISION_V2_MATRIX_HEIGHT"], 0)
    width = int(v2h.defines["EVISION_V2_MATRIX_WIDTH"], 0)

    cherry = read(root, CHERRY)
    evision = read(root, EVISION)
    v2 = read(root, EVISION_V2)
    v2cpp = read(root, EVISION_V2_CPP)
    das = read(root, DAS)

    def define(text, name):
        m = re.search(r"#define\s+" + name + r"\s+(\w+)", text)
        return int(m.group(1), 0)

    layouts = []
    c_rows, c_cols = define(cherry, "CHERRY_MATRIX_MAP_HEIGHT"), define(cherry, "CHERRY_MATRIX_MAP_WIDTH")
    cells, line = grid(cherry, "matrix_map", CHERRY, c_rows, c_cols)
    layouts.append(("Cherry", c_rows, c_cols, cells, c_rows * c_cols, None,
                    "RGBController_CherryKeyboard.cpp:%d" % line))
    cells, line = grid(evision, "matrix_map", EVISION, 6, 23)
    count = int(re.search(r"leds_count\s*=\s*(\d+)", evision).group(1))
    layouts.append(("EVision", 6, 23, cells, count, None, "RGBController_EVisionKeyboard.cpp:%d" % line))
    for cs, matrix, led in (("EVisionV2", "evisionv2_matrix", "led_evisionv2"), ("Endorfy", "endorfy_matrix", "led_endorfy")):
        cells, line = grid(v2, matrix, EVISION_V2, height, width)
        led_names, _ = names(v2, led, EVISION_V2, key_names)
        layouts.append((cs, height, width, cells, len(led_names), led_names, "RGBController_EVisionV2Keyboard.cpp:%d" % line))
    das_names, das_line = names(das, "led_names", DAS, key_names)
    for cs, matrix in (("DasUs", "matrix_map_us"), ("DasEu", "matrix_map_eu")):
        cells, line = grid(das, matrix, DAS, 7, 21)
        layouts.append((cs, 7, 21, cells, len(das_names), das_names, "RGBController_DasKeyboard.cpp:%d" % line))

    for cs, rows, cols, cells, count, led_names, source in layouts:
        for c in cells:
            if c is not None and not 0 <= c < count:
                sys.exit("%s: cell %d outside %d LEDs" % (cs, c, count))

    keyvalues = []
    for cs, name in (("EVisionV2Values", "evisionv2_map"), ("EndorfyValues", "endorfy_map")):
        values, line = numbers(v2cpp, name, EVISION_V2_CPP)
        keyvalues.append((cs, values, "EVisionV2KeyboardController.cpp:%d" % line))

    commit = oc.git_head(root)

    def cs_str(s):
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_openrgb_matrix_layouts.py from OpenRGB at")
    out.append("//     %s (the key grids and LED names of the Cherry, EVision and Das" % commit[:10])
    out.append("//     Keyboard families). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class MatrixKeyboardLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for cs, rows, cols, cells, count, led_names, source in layouts:
        out.append("")
        out.append("        /// <summary>%s, %d rows of %d, each cell an index into the LEDs or -1." % (source, rows, cols))
        out.append("        /// %d LEDs.</summary>" % count)
        out.append("        internal static readonly MatrixLayout %s = new(%s, %d, %d, %d, new short[]" % (cs, cs_str(source), rows, cols, count))
        out.append("        {")
        for r in range(rows):
            part = cells[r * cols:(r + 1) * cols]
            out.append("            " + ", ".join("%3d" % (-1 if v is None else v) for v in part) + ",")
        if led_names is None:
            out.append("        }, null);")
        else:
            out.append("        }, new[]")
            out.append("        {")
            for i in range(0, len(led_names), 4):
                out.append("            " + " ".join(cs_str(n) + "," for n in led_names[i:i + 4]))
            out.append("        });")
    for cs, values, source in keyvalues:
        out.append("")
        out.append("        /// <summary>%s: where each LED's color sits in the frame, in" % source)
        out.append("        /// colors of three bytes.</summary>")
        out.append("        internal static readonly byte[] %s =" % cs)
        out.append("        {")
        for i in range(0, len(values), 16):
            out.append("            " + ", ".join("%3d" % v for v in values[i:i + 16]) + ",")
        out.append("        };")
    out.append("    }")
    out.append("}")
    text = "\r\n".join(out) + "\r\n"

    if args.check:
        with open(OUTPUT, encoding="utf-8", newline="") as f:
            if f.read() != text:
                sys.exit("MatrixKeyboardLayouts.g.cs differs from OpenRGB at " + commit)
        print("MatrixKeyboardLayouts.g.cs matches")
        return
    with open(OUTPUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote %s: %d layouts" % (OUTPUT, len(layouts)))


if __name__ == "__main__":
    main()
