"""The Roccat Vulcan keyboard layouts, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/RoccatController/RoccatVulcanKeyboardController:
- RoccatVulcanKeyboardLayouts.h: the key grids (unsigned int NAME[rows][cols],
  each cell an index into the layout's LED list or NA) and the eight
  std::map<int, layout_info> tables, one entry per region: the grid, the LED
  count, the rows, the columns, and the LEDs in OpenRGB's order, each a name
  and the LED's value in the keyboard's frames.
- RGBController/RGBControllerKeyNames.cpp for the strings the KEY_ names
  stand for.

OpenRGB lays these keyboards out without its KeyboardLayoutManager, so the
family keeps the tables as OpenRGB holds them.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/RoccatVulcanLayouts.g.cs,
with CRLF line endings. Four Japanese legends (E/J, @, ^ and :) name a key
whose usage depends on the layout it sits in, and OpenRGB's key names give
them none. Each one's cell in its grid is looked up in the US entry of the
same table, and the US key at that cell is written beside it, so the key the
legend sits on keeps its usage. The script stops when a table has no US
entry, the cell holds nothing there, or the grids do not have the same
shape.

Usage:
    python tools/gen_roccat_vulcan_layouts.py OPENRGB_ROOT
    python tools/gen_roccat_vulcan_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError, Expr, Node

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "RoccatVulcanLayouts.g.cs")

HEADER = "Controllers/RoccatController/RoccatVulcanKeyboardController/RoccatVulcanKeyboardLayouts.h"
KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"

TABLES = [
    "RoccatVulcan120AimoLayouts",
    "RoccatPyroLayouts",
    "RoccatVulcanIILayouts",
    "RoccatVulcanIIMaxLayouts",
    "RoccatVulcanTKLLayouts",
    "TurtleBeachVulcanIITKLProLayouts",
    "RoccatVulcanIIMiniLayouts",
    "RoccatMagmaLayouts",
]

JAPANESE_LEGENDS = ("KEY_JP_EJ", "KEY_JP_AT", "KEY_JP_CHEVRON", "KEY_JP_COLON")

NA = 0xFFFFFFFF


def grids(text):
    """Every two-dimensional key grid of the header: name, rows, columns,
    cells (None for NA) and the line its name sits on."""
    out = {}
    for m in re.finditer(r"static\s+unsigned\s+int\s+(\w+)\s*\[\s*(\d+)\s*\]\s*\[\s*(\d+)\s*\]\s*=\s*\{", text):
        name, rows, cols = m.group(1), int(m.group(2)), int(m.group(3))
        depth, i = 1, m.end()
        while depth:
            depth += {"{": 1, "}": -1}.get(text[i], 0)
            i += 1
        body = text[m.end():i - 1]
        cells = [None if t == "NA" else int(t, 0) for t in re.findall(r"0x[0-9A-Fa-f]+|\d+|NA", body)]
        if len(cells) != rows * cols:
            sys.exit("%s: %d cells, not %d by %d" % (name, len(cells), rows, cols))
        out[name] = (rows, cols, cells, text.count("\n", 0, m.start()) + 1)
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    root = args.openrgb
    header = oc.Source(root, HEADER)
    with open(os.path.join(root, HEADER), encoding="utf-8-sig") as f:
        raw = oc.strip_comments(f.read())
    names = oc.Source(root, KEY_NAMES)
    key_names = {}
    key_ids = {}
    toks = names.tokens
    for i, t in enumerate(toks):
        if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
            key_names[t.text] = oc.decode_string(toks[i + 2].text)
    ev = oc.Evaluator([header], {}, key_names)
    oc.add_enum_constants(ev, header)
    layout_ids = {k: v for k, v in ev.constants.items() if k.startswith("ROCCAT_VULCAN_LAYOUT_")}
    if layout_ids.get("ROCCAT_VULCAN_LAYOUT_US") != 0:
        sys.exit("ROCCAT_VULCAN_LAYOUT_US is not 0")
    all_grids = grids(raw)
    commit = oc.git_head(root)

    tables = []
    used_grids = []
    for table in TABLES:
        node, line = header.definition(table)
        entries = []
        for entry in node:
            if not isinstance(entry, Node) or len(entry) != 2 or not isinstance(entry[1], Node):
                raise CppError("line %d: %s entry is not {layout, info}" % (entry.line, table))
            layout = ev.integer(entry[0])
            info = entry[1]
            if len(info) != 5:
                raise CppError("line %d: layout_info has %d fields" % (info.line, len(info)))
            grid_tokens = [t.text for t in info[0]]
            if len(grid_tokens) != 2 or grid_tokens[0] != "*":
                raise CppError("line %d: grid %r" % (info.line, grid_tokens))
            grid = grid_tokens[1]
            size, rows, cols = (ev.integer(info[k]) for k in (1, 2, 3))
            g_rows, g_cols, cells, _ = all_grids[grid]
            if (g_rows, g_cols) != (rows, cols):
                sys.exit("%s: %s is %d by %d, the entry says %d by %d" % (table, grid, g_rows, g_cols, rows, cols))
            leds = []
            for led in info[4]:
                if not isinstance(led, Node) or len(led) != 2:
                    raise CppError("line %d: led_value is not {name, id}" % led.line)
                token = led[0][0].text if len(led[0]) == 1 else None
                name = ev.string(led[0])
                value = ev.integer(led[1])
                # The Vulcan II TKL Pro's UK table names the key beside the left shift Y a second time.
                if table == "TurtleBeachVulcanIITKLProLayouts" and token == "KEY_EN_Y" and value == 0x3A:
                    name = key_names["KEY_EN_Z"]
                if not 0 <= value <= 255:
                    raise CppError("line %d: id %d is not a byte" % (led.line, value))
                leds.append((name, value, token))
            if len(leds) != size:
                sys.exit("%s layout %d: %d LEDs, size %d" % (table, layout, len(leds), size))
            for c in cells:
                if c is not None and not 0 <= c < size:
                    sys.exit("%s layout %d: cell %d outside %d LEDs" % (table, layout, c, size))
            entries.append((layout, grid, size, rows, cols, leds, entry.line))
            if grid not in used_grids:
                used_grids.append(grid)
        tables.append((table, line, entries))

    # The key under each Japanese legend, from the US entry of its table.
    unders = {}
    for table, _, entries in tables:
        us = [e for e in entries if e[0] == 0]
        for layout, grid, size, rows, cols, leds, _ in entries:
            for index, (name, value, token) in enumerate(leds):
                if token not in JAPANESE_LEGENDS:
                    continue
                if not us:
                    sys.exit("%s has no US entry for %s" % (table, token))
                u_layout, u_grid, _, u_rows, u_cols, u_leds, _ = us[0]
                if (u_rows, u_cols) != (rows, cols):
                    sys.exit("%s: the US grid is not the shape of %s" % (table, grid))
                cells = all_grids[grid][2]
                at = cells.index(index)
                u_index = all_grids[u_grid][2][at]
                if u_index is None:
                    sys.exit("%s: no US key at %s's cell" % (table, token))
                unders[(table, layout, index)] = u_leds[u_index][0]

    def cs(s):
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'

    def cs_name(grid):
        return "".join(part.capitalize() for part in grid.lower().split("_"))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_roccat_vulcan_layouts.py from OpenRGB at")
    out.append("//     %s (Controllers/RoccatController/RoccatVulcanKeyboardController/" % commit[:10])
    out.append("//     RoccatVulcanKeyboardLayouts.h). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class RoccatVulcanLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    out.append("")
    out.append("        /// <summary>The region values a keyboard reports (the enum at the top of")
    out.append("        /// RoccatVulcanKeyboardLayouts.h).</summary>")
    for k, v in sorted(layout_ids.items(), key=lambda kv: kv[1]):
        out.append("        internal const int %s = %d;" % (cs_name(k[len("ROCCAT_VULCAN_"):]), v))
    out.append("")
    for grid in used_grids:
        rows, cols, cells, gline = all_grids[grid]
        out.append("        /// <summary>%s, %d rows of %d (RoccatVulcanKeyboardLayouts.h:%d), each cell" % (grid, rows, cols, gline))
        out.append("        /// an index into its entry's LEDs, or -1 for no key.</summary>")
        out.append("        private static readonly short[] %s =" % cs_name(grid))
        out.append("        {")
        for r in range(rows):
            part = cells[r * cols:(r + 1) * cols]
            out.append("            " + ", ".join("%3d" % (-1 if v is None else v) for v in part) + ",")
        out.append("        };")
        out.append("")
    out.append("        /// <summary>Every entry of the eight tables, table by table, in the order")
    out.append("        /// of each std::map, which sorts its regions by value.</summary>")
    out.append("        internal static readonly RoccatVulcanLayout[] All =")
    out.append("        {")
    for table, line, entries in tables:
        for layout, grid, size, rows, cols, leds, eline in sorted(entries, key=lambda e: e[0]):
            out.append("            new(%s, %d, %d, %d, %s, \"RoccatVulcanKeyboardLayouts.h:%d\", new RoccatVulcanLed[]"
                       % (cs(table), layout, rows, cols, cs_name(grid), eline))
            out.append("            {")
            for index, (name, value, token) in enumerate(leds):
                under = unders.get((table, layout, index))
                if under is None:
                    out.append("                new(%s, 0x%02X)," % (cs(name), value))
                else:
                    out.append("                new(%s, 0x%02X) { Under = %s }," % (cs(name), value, cs(under)))
            out.append("            }),")
    out.append("        };")
    out.append("    }")
    out.append("}")
    text = "\r\n".join(out) + "\r\n"

    if args.check:
        with open(OUTPUT, encoding="utf-8", newline="") as f:
            if f.read() != text:
                sys.exit("RoccatVulcanLayouts.g.cs differs from OpenRGB at " + commit)
        print("RoccatVulcanLayouts.g.cs matches")
        return
    with open(OUTPUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote %s: %d tables, %d entries" % (OUTPUT, len(tables), sum(len(t[2]) for t in tables)))


if __name__ == "__main__":
    main()
