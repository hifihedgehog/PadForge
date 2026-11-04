"""The light tables of OpenRGB families that lay out their lights without the
keyboard layout manager, for PadForge (#557).

Six of OpenRGB's families keep each light's name, its index in the family's
frames and its place on a grid in tables of their own. This script reads those
tables from OpenRGB at a commit and writes them, one generated file per
family, beside the family's class in
PadForge.App/Common/Input/Peripherals/HidFamilies/:

- MsiLaptopLighting.g.cs from Controllers/MSILaptopController/
  MSILaptopControllerDetect.cpp: the per-key keyboard's LEDs and grid, the
  light bars' LEDs, and the laptop models that pick them by DMI name.
- AlienwareLighting.g.cs from Controllers/AlienwareController/
  AlienwareController.cpp: the zone count, zone names and zone IDs per
  platform.
- AlienwareKeyboardLighting.g.cs from Controllers/AlienwareKeyboardController/:
  the AW410K's and the AW510K's LEDs and grids.
- EvgaLighting.g.cs from Controllers/EVGAUSBController/EVGAKeyboardController/:
  the keyboard's packet map, LED names, grid and the Z20's extra zones.
- GigabyteAorusLaptopLighting.g.cs from Controllers/
  GigabyteAorusLaptopController/RGBController_GigabyteAorusLaptop.cpp: the
  keyboard's grid, key names and buffer positions.
- A4TechLighting.g.cs from Controllers/A4TechController/: the B820R's LEDs and
  grid, and the Bloody mice's zones.

Names are written as OpenRGB's key name strings (RGBControllerKeyNames.cpp),
grids row by row with uint.MaxValue where OpenRGB has NA. Every file is
written with CRLF line endings.

Usage:
    python tools/gen_laptop_misc_led_tables.py OPENRGB_ROOT
    python tools/gen_laptop_misc_led_tables.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import openrgb_cpp as oc  # noqa: E402
from openrgb_cpp import CppError, Expr, Node  # noqa: E402

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies")

KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"
C = "Controllers/"
MSI_LAPTOP = C + "MSILaptopController/MSILaptopControllerDetect.cpp"
AW = C + "AlienwareController/AlienwareController.cpp"
AW410 = C + "AlienwareKeyboardController/AlienwareAW410KController/RGBController_AlienwareAW410K.cpp"
AW410_H = C + "AlienwareKeyboardController/AlienwareAW410KController/AlienwareAW410KController.h"
AW510 = C + "AlienwareKeyboardController/AlienwareAW510KController/RGBController_AlienwareAW510K.cpp"
AW510_H = C + "AlienwareKeyboardController/AlienwareAW510KController/AlienwareAW510KController.h"
EVGA_CTRL = C + "EVGAUSBController/EVGAKeyboardController/EVGAKeyboardController.cpp"
EVGA_H = C + "EVGAUSBController/EVGAKeyboardController/EVGAKeyboardController.h"
EVGA_RGB = C + "EVGAUSBController/EVGAKeyboardController/RGBController_EVGAKeyboard.cpp"
AORUS = C + "GigabyteAorusLaptopController/RGBController_GigabyteAorusLaptop.cpp"
B820R = C + "A4TechController/BloodyB820RController/RGBController_BloodyB820R.cpp"
BLOODY = C + "A4TechController/BloodyMouseController/RGBController_BloodyMouse.cpp"

NA = 0xFFFFFFFF


class Reader:
    def __init__(self, root):
        self.root = root
        self.sources = {}
        self.key_names = {}
        toks = self.src(KEY_NAMES).tokens
        for i, t in enumerate(toks):
            if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
                self.key_names[t.text] = oc.decode_string(toks[i + 2].text)

    def src(self, rel):
        if rel not in self.sources:
            self.sources[rel] = oc.Source(self.root, rel)
        return self.sources[rel]

    def ev(self, *rels):
        ev = oc.Evaluator([self.src(r) for r in rels], {}, self.key_names)
        for r in rels:
            oc.add_enum_constants(ev, self.src(r))
        return ev

    def table(self, rel, name):
        """The braces of the top-level definition called name, whatever
        array bounds or template arguments come between the name and the
        braces, and the line the name sits on."""
        s = self.src(rel)
        toks = s.tokens
        depth = 0
        for i, t in enumerate(toks):
            if t.text == "{":
                depth += 1
            elif t.text == "}":
                depth -= 1
            elif depth == 0 and t.kind == "id" and t.text == name and i > 0 and toks[i - 1].kind in ("id", "op"):
                j = i + 1
                while toks[j].text not in ("{", ";", "(", ")"):
                    j += 1
                if toks[j].text != "{":
                    continue
                node, _ = oc.parse_braces(toks, j)
                return node, t.line
        raise CppError("%s: no definition of %s" % (rel, name))


def flat(node):
    """The leaf expressions of a braced list, row by row."""
    out = []
    for e in node:
        if isinstance(e, Node):
            out.extend(flat(e))
        else:
            out.append(e)
    return out


def ints(node, ev):
    return [ev.integer(e) for e in flat(node)]


def last_id(expr):
    ids = [t.text for t in expr if t.kind == "id"]
    if not ids:
        raise CppError("line %d: no name in %r" % (expr.line, expr))
    return ids[-1]


def pascal(name):
    return "".join(p[:1].upper() + p[1:] for p in name.replace("-", "_").split("_") if p)


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cs_uint(v):
    return "NA" if v == NA else str(v)


class Writer:
    def __init__(self, commit, cls, summary):
        self.lines = [
            "// <auto-generated>",
            "//     Generated by tools/gen_laptop_misc_led_tables.py from OpenRGB at",
            "//     %s (%s)." % (commit[:10], summary),
            "//     Do not edit by hand.",
            "// </auto-generated>",
            "namespace PadForge.Common.Input.Peripherals",
            "{",
            "    internal sealed partial class %s" % cls,
            "    {",
        ]
        self.na = False

    def doc(self, text):
        if self.lines[-1] != "    {":
            self.lines.append("")
        self.lines.append("        /// <summary>%s</summary>" % text)

    def names_bytes(self, name, rows):
        self.lines.append("        internal static readonly (string Name, byte Value)[] %s =" % name)
        self.lines.append("        {")
        for n, v in rows:
            self.lines.append("            (%s, 0x%02X)," % (cs_string(n), v))
        self.lines.append("        };")

    def strings(self, name, rows):
        self.lines.append("        internal static readonly string[] %s =" % name)
        self.lines.append("        {")
        for n in rows:
            self.lines.append("            %s," % cs_string(n))
        self.lines.append("        };")

    def grid(self, name, values, width, kind="uint", fmt=cs_uint):
        if kind == "uint" and not self.na:
            # OpenRGB's NA, declared once, where a grid first needs it.
            self.na = True
            self.lines[9:9] = ["        /// <summary>OpenRGB's NA, a grid cell with no light.</summary>",
                               "        private const uint NA = uint.MaxValue;", ""]
        self.lines.append("        internal static readonly %s[] %s =" % (kind, name))
        self.lines.append("        {")
        for r in range(0, len(values), width):
            self.lines.append("            " + ", ".join(fmt(v) for v in values[r:r + width]) + ",")
        self.lines.append("        };")

    def const(self, name, value):
        self.lines.append("        internal const int %s = %d;" % (name, value))

    def raw(self, line):
        self.lines.append(line)

    def text(self):
        return "\r\n".join(self.lines + ["    }", "}"]) + "\r\n"


# ── MSI laptop ───────────────────────────────────────────────────────────

def msi_laptop(rd, commit):
    ev = rd.ev(MSI_LAPTOP)
    w = Writer(commit, "MsiLaptopLighting", "Controllers/MSILaptopController/MSILaptopControllerDetect.cpp")
    led_tables = {}
    models, line = rd.table(MSI_LAPTOP, "msi_laptop_models")
    order = []
    for m in models:
        for idx in (2, 7):
            name = last_id(m[idx])
            if name not in order:
                order.append(name)
        map_name = last_id(m[6])
        if map_name not in order:
            order.append(map_name)
    for name in order:
        node, nline = rd.table(MSI_LAPTOP, name)
        if name.endswith("matrix_map"):
            led_tables[name] = ("grid", ints(node, ev), nline)
        else:
            rows = [(ev.string(Expr(e[0])), ev.integer(e[1])) for e in node]
            led_tables[name] = ("leds", rows, nline)
    for name in order:
        kind, data, nline = led_tables[name]
        if kind == "leds":
            w.doc("%s, MSILaptopControllerDetect.cpp:%d: each LED's name and ID, in OpenRGB's order." % (name, nline))
            w.names_bytes(pascal(name), data)
    for name in order:
        kind, data, nline = led_tables[name]
        if kind == "grid":
            w.doc("%s, MSILaptopControllerDetect.cpp:%d: the keyboard's grid, row by row, each cell an index into its LED list." % (name, nline))
            width = ev.integer(models[0][5])
            w.grid(pascal(name), data, width)
    w.doc("msi_laptop_models, MSILaptopControllerDetect.cpp:%d: the DMI manufacturer and product name each model is found by, its keyboard LEDs, grid height and width, its light bar LEDs and the light bar's LED count." % line)
    w.raw("        internal static readonly ModelRow[] Models =")
    w.raw("        {")
    for m in models:
        vendor = ev.string(m[0])
        product = ev.string(m[1])
        klc = pascal(last_id(m[2]))
        height = ev.integer(m[4])
        width = ev.integer(m[5])
        grid = pascal(last_id(m[6]))
        alc = pascal(last_id(m[7]))
        bar = ev.integer(m[9])
        w.raw("            new(%s, %s, %s, %d, %d, %s, %s, %d)," % (cs_string(vendor), cs_string(product), klc, height, width, grid, alc, bar))
    w.raw("        };")
    return "MsiLaptopLighting.g.cs", w.text()


# ── Alienware G series ───────────────────────────────────────────────────

def alienware(rd, commit):
    ev = rd.ev(AW)
    w = Writer(commit, "AlienwareLighting", "Controllers/AlienwareController/AlienwareController.cpp")
    quirks, ql = rd.table(AW, "zone_quirks_table")
    names, nl = rd.table(AW, "zone_names_table")
    ids, il = rd.table(AW, "zone_ids_table")
    w.doc("zone_quirks_table, AlienwareController.cpp:%d: the zone count of each platform that reports a wrong one." % ql)
    w.raw("        internal static readonly (int Platform, int Zones)[] ZoneQuirks =")
    w.raw("        {")
    for e in quirks:
        w.raw("            (0x%04X, %d)," % (ev.integer(e[0]), ev.integer(e[1])))
    w.raw("        };")
    w.doc("zone_names_table, AlienwareController.cpp:%d: the zone names of each known platform." % nl)
    w.raw("        internal static readonly (int Platform, string[] Names)[] ZoneNames =")
    w.raw("        {")
    for e in names:
        w.raw("            (0x%04X, new[] { %s })," % (ev.integer(e[0]), ", ".join(cs_string(ev.string(x)) for x in e[1])))
    w.raw("        };")
    w.doc("zone_ids_table, AlienwareController.cpp:%d: the hardware zone IDs of each platform that does not number its zones from 0." % il)
    w.raw("        internal static readonly (int Platform, byte[] Ids)[] ZoneIds =")
    w.raw("        {")
    for e in ids:
        w.raw("            (0x%04X, new byte[] { %s })," % (ev.integer(e[0]), ", ".join("0x%02X" % ev.integer(x) for x in e[1])))
    w.raw("        };")
    return "AlienwareLighting.g.cs", w.text()


# ── Alienware AW410K / AW510K ────────────────────────────────────────────

def alienware_keyboards(rd, commit):
    w = Writer(commit, "AlienwareKeyboardLighting", "Controllers/AlienwareKeyboardController: RGBController_AlienwareAW410K.cpp and RGBController_AlienwareAW510K.cpp")
    for tag, rel, hdr in (("Aw410k", AW410, AW410_H), ("Aw510k", AW510, AW510_H)):
        ev = rd.ev(hdr, rel)
        leds, ll = rd.table(rel, "led_names")
        grid, gl = rd.table(rel, "matrix_map")
        rows = len(grid)
        cols = len(grid[0])
        file = rel.rsplit("/", 1)[1]
        w.doc("led_names, %s:%d: each LED's name and its index in the direct frames, in OpenRGB's order." % (file, ll))
        w.names_bytes(tag + "Leds", [(ev.string(e[0]), ev.integer(e[2])) for e in leds])
        w.doc("matrix_map, %s:%d: %d rows of %d, each cell an index into %sLeds." % (file, gl, rows, cols, tag))
        w.const(tag + "Rows", rows)
        w.const(tag + "Cols", cols)
        w.grid(tag + "Grid", ints(grid, ev), cols)
    return "AlienwareKeyboardLighting.g.cs", w.text()


# ── EVGA keyboards ───────────────────────────────────────────────────────

def evga(rd, commit):
    ev = rd.ev(EVGA_H, EVGA_CTRL, EVGA_RGB)
    w = Writer(commit, "EvgaLighting", "Controllers/EVGAUSBController/EVGAKeyboardController")
    packet, pl = rd.table(EVGA_CTRL, "packet_map")
    names, nl = rd.table(EVGA_RGB, "led_names")
    grid, gl = rd.table(EVGA_RGB, "full_matrix_map")
    extra, el = rd.table(EVGA_RGB, "Z20_extra_zones")
    extra_names, xl = rd.table(EVGA_RGB, "Z20_zone_names")
    w.doc("packet_map, EVGAKeyboardController.cpp:%d: each LED's slot in the direct frame, in LED order." % pl)
    w.grid("PacketMap", ints(packet, ev), 10, "byte", lambda v: str(v))
    w.doc("led_names, RGBController_EVGAKeyboard.cpp:%d: each LED's name, in LED order." % nl)
    w.strings("LedNames", [ev.string(e) for e in names])
    w.doc("full_matrix_map, RGBController_EVGAKeyboard.cpp:%d: the keyboard zone's 6 rows of 21, each cell an LED." % gl)
    w.grid("KeyboardGrid", ints(grid, ev), 21)
    w.doc("Z20_extra_zones, RGBController_EVGAKeyboard.cpp:%d: the Z20's three extra zones, one row of 9 each, each cell an LED of the zone." % el)
    w.grid("Z20ExtraZones", ints(extra, ev), 9)
    w.doc("Z20_zone_names, RGBController_EVGAKeyboard.cpp:%d." % xl)
    w.strings("Z20ZoneNames", [ev.string(e) for e in extra_names])
    return "EvgaLighting.g.cs", w.text()


# ── Gigabyte Aorus laptops ───────────────────────────────────────────────

def aorus(rd, commit):
    ev = rd.ev(AORUS)
    w = Writer(commit, "GigabyteAorusLaptopLighting", "Controllers/GigabyteAorusLaptopController/RGBController_GigabyteAorusLaptop.cpp")
    layout, ll = rd.table(AORUS, "aorus_laptop_default_keyboard_layout")
    width = ev.integer(layout[0])
    height = ev.integer(layout[1])
    w.doc("aorus_laptop_default_keyboard_layout, RGBController_GigabyteAorusLaptop.cpp:%d: the grid's width and height." % ll)
    w.const("GridWidth", width)
    w.const("GridHeight", height)
    w.doc("Its matrix_map, row by row, each cell the key's index in KeyNames.")
    w.grid("Grid", ints(layout[2], ev), width)
    w.doc("Its led_names, in key order.")
    w.strings("KeyNames", [ev.string(e) for e in flat(layout[3])])
    w.doc("Its led_sequence_positions: each key's place in the custom frame's color buffer, in key order.")
    w.grid("Positions", ints(layout[4], ev), 19, "byte", lambda v: str(v))
    return "GigabyteAorusLaptopLighting.g.cs", w.text()


# ── A4Tech ───────────────────────────────────────────────────────────────

def a4tech(rd, commit):
    w = Writer(commit, "A4TechLighting", "Controllers/A4TechController")
    ev = rd.ev(B820R)
    grid, gl = rd.table(B820R, "matrix_map")
    names, nl = rd.table(B820R, "led_names")
    w.doc("led_names, RGBController_BloodyB820R.cpp:%d: each key's name, in LED order." % nl)
    w.strings("B820rKeyNames", [ev.string(e) for e in names])
    w.doc("matrix_map, RGBController_BloodyB820R.cpp:%d: 6 rows of 21, each cell an LED." % gl)
    w.grid("B820rGrid", ints(grid, ev), 21)
    ev = rd.ev(BLOODY)
    for name in ("w60_pro", "w90_max", "mp_50rs"):
        node, line = rd.table(BLOODY, name)
        w.doc("%s, RGBController_BloodyMouse.cpp:%d: each zone's name and its LEDs' slots." % (name, line))
        w.raw("        internal static readonly (string Name, byte[] Leds)[] %s =" % pascal(name))
        w.raw("        {")
        for zone in node:
            w.raw("            (%s, new byte[] { %s })," % (cs_string(ev.string(zone[0])), ", ".join(str(v) for v in ints(zone[1], ev))))
        w.raw("        };")
    return "A4TechLighting.g.cs", w.text()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    rd = Reader(args.openrgb)
    commit = oc.git_head(args.openrgb)
    files = [f(rd, commit) for f in (msi_laptop, alienware, alienware_keyboards, evga, aorus, a4tech)]
    differs = 0
    for name, text in files:
        path = os.path.join(OUT_DIR, name)
        if args.check:
            try:
                with open(path, encoding="utf-8-sig", newline="") as f:
                    current = f.read()
            except FileNotFoundError:
                current = ""
            if current != text:
                differs += 1
                print("differs: " + name)
        else:
            with open(path, "w", encoding="utf-8", newline="") as f:
                f.write(text)
            print("wrote " + name)
    if args.check:
        print("up to date" if differs == 0 else "%d file(s) differ" % differs)
        sys.exit(1 if differs else 0)


if __name__ == "__main__":
    main()
