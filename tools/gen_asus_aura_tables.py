"""The ASUS Aura tables OpenRGB keeps in its own formats, for PadForge (#557).

OpenRGB lights the ASUS keyboards, mice and the Cerberus Mech from tables of
its own rather than through KeyboardLayoutManager. This script reads them at
an OpenRGB commit and writes them to
PadForge.App/Common/Input/Peripherals/HidFamilies/AsusAuraTables.g.cs:

- Controllers/AsusAuraUSBController/AsusAuraKeyboardController/
  AsusAuraKeyboardLayouts.h: every std::map<int,layout_info> (one per
  keyboard line), each layout with its region key, its matrix, its size,
  rows and columns, and its LEDs as name and LED value, in OpenRGB's order.
- Controllers/AsusAuraUSBController/AsusAuraMouseController/
  AsusAuraMouseDevices.h: aura_mouse_devices and aura_mouse_led_maps. Both
  are std::maps built from initializer lists, which keep the first entry of
  a key listed twice (0x189E and 0x1979 are), and so does this script.
- Controllers/AsusAuraUSBController/AsusAuraMouseController/
  RGBController_AsusAuraMouse.cpp: aura_mouse_zone_names.
- Controllers/AsusLegacyUSBController/AsusCerberusKeyboardController/
  RGBController_AsusCerberusKeyboard.cpp: matrix_map and led_names.

Key names (KEY_EN_..., KEY_NORD_...) are written as their strings from
RGBController/RGBControllerKeyNames.cpp. NA (0xFFFFFFFF) cells are written
as uint.MaxValue.

Usage:
    python tools/gen_asus_aura_tables.py OPENRGB_ROOT
    python tools/gen_asus_aura_tables.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError, Expr, Node

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "AsusAuraTables.g.cs")

KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"
KB = "Controllers/AsusAuraUSBController/AsusAuraKeyboardController/AsusAuraKeyboardLayouts.h"
MOUSE = "Controllers/AsusAuraUSBController/AsusAuraMouseController/AsusAuraMouseDevices.h"
MOUSE_RGB = "Controllers/AsusAuraUSBController/AsusAuraMouseController/RGBController_AsusAuraMouse.cpp"
CERBERUS = "Controllers/AsusLegacyUSBController/AsusCerberusKeyboardController/RGBController_AsusCerberusKeyboard.cpp"

NA = 0xFFFFFFFF


def key_names(root):
    src = oc.Source(root, KEY_NAMES)
    names = {}
    toks = src.tokens
    for i, t in enumerate(toks):
        if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
            names[t.text] = oc.decode_string(toks[i + 2].text)
    return names


def array_definition(src, name):
    """The parsed braces of an array definition with any number of [n]
    dimensions, and the line its name sits on."""
    toks = src.tokens
    depth = 0
    for i, t in enumerate(toks):
        if t.text == "{":
            depth += 1
        elif t.text == "}":
            depth -= 1
        elif depth == 0 and t.kind == "id" and t.text == name:
            j = i + 1
            while toks[j].text == "[":
                while toks[j].text != "]":
                    j += 1
                j += 1
            if toks[j].text == "=":
                j += 1
            if toks[j].text == "{":
                node, _ = oc.parse_braces(toks, j)
                return node, t.line
    raise CppError("%s: no array %s" % (src.rel, name))


def flat(node, ev):
    out = []
    for e in node:
        if isinstance(e, Node):
            out.extend(flat(e, ev))
        else:
            out.append(ev.integer(e))
    return out


def ident(expr):
    ids = [t.text for t in expr if t.kind == "id"]
    if len(ids) != 1:
        raise CppError("line %d: not one name: %r" % (expr.line, expr))
    return ids[0]


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.openrgb
    commit = oc.git_head(root)
    names = key_names(root)

    # -- keyboards ------------------------------------------------------
    kb = oc.Source(root, KB)
    layout_enum = oc.enum_values(kb, "ASUS_TUF_K7_LAYOUT_CA")
    ev = oc.Evaluator([kb], dict(layout_enum, NA=NA), names)
    maps = []
    toks = kb.tokens
    # Each map is "static std::map<int,layout_info> NAME = {".
    for i, t in enumerate(toks):
        if t.kind == "id" and t.text == "layout_info" and toks[i + 1].text == ">" and toks[i + 2].kind == "id":
            maps.append(toks[i + 2].text)
    if not maps:
        sys.exit("no layout maps")
    matrices = {}
    sets = []
    for name in maps:
        node, line = kb.definition(name)
        entries = []
        seen = set()
        for entry in node:
            if not isinstance(entry, Node) or len(entry) != 2:
                raise CppError("%s: entry of %s" % (KB, name))
            key = ev.integer(entry[0])
            if key in seen:
                continue
            seen.add(key)
            body = entry[1]
            matrix = ident(body[0])
            size = ev.integer(body[1])
            rows = ev.integer(body[2])
            cols = ev.integer(body[3])
            leds = []
            for led in body[4]:
                leds.append((ev.string(led[0]), ev.integer(led[1])))
            if matrix not in matrices:
                mnode, mline = array_definition(kb, matrix)
                cells = flat(mnode, ev)
                matrices[matrix] = (cells, mline)
            cells = matrices[matrix][0]
            if len(cells) != rows * cols:
                sys.exit("%s: %s has %d cells, not %d by %d" % (name, matrix, len(cells), rows, cols))
            if len(leds) != size:
                sys.exit("%s: %d LEDs, size %d" % (name, len(leds), size))
            entries.append((key, matrix, size, rows, cols, leds, entry.line))
        sets.append((name, line, entries))

    # -- mice -------------------------------------------------------------
    mouse = oc.Source(root, MOUSE)
    mev = oc.Evaluator([mouse])
    oc.add_enum_constants(mev, mouse)
    mev.constants.update({"true": 1, "false": 0})
    devices = []
    node, dline = mouse.definition("aura_mouse_devices")
    seen = set()
    for entry in node:
        pid = mev.integer(entry[0])
        if pid in seen:
            continue
        seen.add(pid)
        b = entry[1]
        vals = [mev.integer(b[k]) for k in range(7)]
        zones = [mev.integer(z) for z in b[7]]
        modes = [mev.integer(m) for m in b[8]]
        devices.append((pid, vals, zones, modes, entry.line))
    led_maps = []
    node, lline = mouse.definition("aura_mouse_led_maps")
    seen = set()
    for entry in node:
        pid = mev.integer(entry[0])
        if pid in seen:
            continue
        seen.add(pid)
        amount = mev.integer(entry[1][0])
        zone_leds = []
        zseen = set()
        for z in entry[1][1]:
            zone = mev.integer(z[0])
            if zone in zseen:
                continue
            zseen.add(zone)
            zone_leds.append((zone, [mev.integer(x) for x in z[1]]))
        led_maps.append((pid, amount, zone_leds, entry.line))
    mrgb = oc.Source(root, MOUSE_RGB)
    znode, zline = array_definition(mrgb, "aura_mouse_zone_names")
    zone_names = [oc.Evaluator([mrgb]).string(e) for e in znode]

    # -- Cerberus ---------------------------------------------------------
    cer = oc.Source(root, CERBERUS)
    cev = oc.Evaluator([cer], {"NA": NA}, names)
    cnode, cline = array_definition(cer, "matrix_map")
    cer_matrix = flat(cnode, cev)
    lnode, lline2 = cer.definition("led_names")
    cer_leds = [(cev.string(e[0]), cev.integer(e[1])) for e in lnode]

    # -- write --------------------------------------------------------------
    out = []
    w = out.append
    w("// <auto-generated>")
    w("//     Generated by tools/gen_asus_aura_tables.py from OpenRGB at")
    w("//     %s (AsusAuraKeyboardLayouts.h, AsusAuraMouseDevices.h," % commit[:10])
    w("//     RGBController_AsusAuraMouse.cpp aura_mouse_zone_names and")
    w("//     RGBController_AsusCerberusKeyboard.cpp). Do not edit by hand.")
    w("// </auto-generated>")
    w("namespace PadForge.Common.Input.Peripherals")
    w("{")
    w("    internal static partial class AsusAuraTables")
    w("    {")
    w("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    w("        internal const string OpenRgbCommit = \"%s\";" % commit)
    w("")
    w("        private const uint NA = uint.MaxValue;")
    for matrix, (cells, mline) in matrices.items():
        w("")
        w("        /// <summary>%s (AsusAuraKeyboardLayouts.h:%d).</summary>" % (matrix, mline))
        w("        private static readonly uint[] %s =" % oc_pascal(matrix))
        w("        {")
        w("            " + ", ".join("NA" if c == NA else str(c) for c in cells) + ",")
        w("        };")
    w("")
    w("        /// <summary>Every std::map&lt;int,layout_info&gt; of AsusAuraKeyboardLayouts.h,")
    w("        /// in file order, each layout with its region key, matrix, size, rows,")
    w("        /// columns and LEDs (name, LED value).</summary>")
    w("        internal static readonly AsusKeyboardLayoutSet[] KeyboardSets =")
    w("        {")
    for name, line, entries in sets:
        w("            new(\"%s\", \"AsusAuraKeyboardLayouts.h:%d\", new AsusKeyboardLayout[]" % (name, line))
        w("            {")
        for key, matrix, size, rows, cols, leds, eline in entries:
            led_text = ", ".join("(%s, 0x%02X)" % (cs_string(n), v) for n, v in leds)
            w("                new(%d, %s, %d, %d, %d, new (string, byte)[] { %s }),"
              % (key, oc_pascal(matrix), size, rows, cols, led_text))
        w("            }),")
    w("        };")
    w("")
    w("        /// <summary>aura_mouse_devices (AsusAuraMouseDevices.h:%d), the first" % dline)
    w("        /// entry of a product ID listed twice.</summary>")
    w("        internal static readonly AsusMouseDevice[] Mice =")
    w("        {")
    for pid, vals, zones, modes, eline in devices:
        w("            new(0x%04X, %d, %d, %d, %d, %s, %d, %s, new byte[] { %s }, new byte[] { %s }), // line %d"
          % (pid, vals[0], vals[1], vals[2], vals[3], "true" if vals[4] else "false", vals[5],
             "true" if vals[6] else "false", ", ".join(str(z) for z in zones), ", ".join(str(m) for m in modes), eline))
    w("        };")
    w("")
    w("        /// <summary>aura_mouse_led_maps (AsusAuraMouseDevices.h:%d): a product ID," % lline)
    w("        /// its LED count and each zone's LEDs.</summary>")
    w("        internal static readonly AsusMouseLedMap[] MouseLedMaps =")
    w("        {")
    for pid, amount, zone_leds, eline in led_maps:
        zl = ", ".join("(%d, new byte[] { %s })" % (z, ", ".join(str(x) for x in l)) for z, l in zone_leds)
        w("            new(0x%04X, %d, new (byte, byte[])[] { %s }), // line %d" % (pid, amount, zl, eline))
    w("        };")
    w("")
    w("        /// <summary>aura_mouse_zone_names (RGBController_AsusAuraMouse.cpp:%d).</summary>" % zline)
    w("        internal static readonly string[] MouseZoneNames = { %s };" % ", ".join(cs_string(n) for n in zone_names))
    w("")
    w("        /// <summary>The Cerberus Mech's matrix_map, 6 rows of 24")
    w("        /// (RGBController_AsusCerberusKeyboard.cpp:%d).</summary>" % cline)
    w("        internal static readonly uint[] CerberusMatrix =")
    w("        {")
    w("            " + ", ".join("NA" if c == NA else str(c) for c in cer_matrix) + ",")
    w("        };")
    w("")
    w("        /// <summary>The Cerberus Mech's led_names, name and key code")
    w("        /// (RGBController_AsusCerberusKeyboard.cpp:%d).</summary>" % lline2)
    w("        internal static readonly (string Name, byte Id)[] CerberusLeds =")
    w("        {")
    for n, v in cer_leds:
        w("            (%s, 0x%02X)," % (cs_string(n), v))
    w("        };")
    w("    }")
    w("}")
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
    print("wrote %s: %d keyboard sets, %d matrices, %d mice, %d LED maps, %d Cerberus LEDs"
          % (OUTPUT, len(sets), len(matrices), len(devices), len(led_maps), len(cer_leds)))


def oc_pascal(name):
    import re
    return "".join(p[:1].upper() + p[1:].lower() for p in re.split(r"[_\W]+", name) if p)


if __name__ == "__main__":
    main()
