"""The Corsair light tables, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers:
- CorsairPeripheralController/CorsairPeripheralController.cpp: keys[],
  keys_k70_mk2[], keys_k95_plat[] and keys_k95[] (the Corsair LED number of
  each OpenRGB light), st100[] (the ST100's color order) and the three
  key_mapping_*[] lists of numbers the key-input setup skips.
- CorsairPeripheralController/RGBController_CorsairPeripheral.cpp: the five
  matrix maps, every led_names array and the mouse light names.
- CorsairPeripheralController/CorsairPeripheralControllerDetect.cpp: the
  section ("Keyboards", "Mice", "Mousemats", "Headset Stands", ...) each
  REGISTER line sits in, which names the device type of its product ID.
- CorsairPeripheralController/RGBController_CorsairK65Mini.cpp: the K65
  Mini's matrix map and its keys[] (light position and name).
- CorsairPeripheralController/CorsairK55RGBPROXTController.cpp keys[] and
  RGBController_CorsairK55RGBPROXT.cpp: matrix_map and key_names.
- CorsairPeripheralV2Controller/CorsairPeripheralV2Devices.cpp and .h: every
  corsair_v2_zone, every corsair_v2_device and the device list's order.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ and
  ZONE_ name.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/CorsairLights.g.cs,
the tables as OpenRGB holds them. A matrix cell OpenRGB marks NA is -1. The
V2 keyboards' key layouts are KeyboardLayouts.g.cs's (tools/gen_openrgb_keyboard_layouts.py),
named here by OpenRGB's variable.

Usage:
    python tools/gen_corsair_lights.py OPENRGB_ROOT
    python tools/gen_corsair_lights.py OPENRGB_ROOT --check   (compare, write nothing)
Run it without Python's -I switch: it imports tools/openrgb_cpp.py.
"""
import argparse
import os
import re
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError, Expr, Node

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "CorsairLights.g.cs")

V1 = "Controllers/CorsairPeripheralController/"
V2 = "Controllers/CorsairPeripheralV2Controller/"
KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"

NA = 0xFFFFFFFF


def find(src, name):
    """The index of the brace that opens the top-level definition called
    name, after any number of [..] and an optional =. None when absent."""
    toks = src.tokens
    depth = 0
    for i, t in enumerate(toks):
        if t.text == "{":
            depth += 1
        elif t.text == "}":
            depth -= 1
        elif depth == 0 and t.kind == "id" and t.text == name and i > 0 and toks[i - 1].kind in ("id", "op"):
            j = i + 1
            while toks[j].text == "[":
                while toks[j].text != "]":
                    j += 1
                j += 1
            if toks[j].text == "=":
                j += 1
            if toks[j].text == "{":
                return j
    return None


def definition(src, name):
    j = find(src, name)
    if j is None:
        raise CppError("%s: no definition of %s" % (src.rel, name))
    k = j
    while src.tokens[k].text != name:
        k -= 1
    node, _ = oc.parse_braces(src.tokens, j)
    return node, src.tokens[k].line


def flat(node):
    out = []
    for e in node:
        if isinstance(e, Node):
            out.extend(flat(e))
        else:
            out.append(e)
    return out


def ints(src, ev, name):
    node, line = definition(src, name)
    return [ev.integer(e) for e in flat(node)], line


def matrix(src, ev, name):
    node, line = definition(src, name)
    rows = []
    for row in node:
        if not isinstance(row, Node):
            raise CppError("%s: %s is not a matrix" % (src.rel, name))
        rows.append([-1 if ev.integer(e) == NA else ev.integer(e) for e in row])
    width = len(rows[0])
    if any(len(r) != width for r in rows):
        raise CppError("%s: %s rows differ in length" % (src.rel, name))
    return rows, line


def strings(src, ev, name):
    node, line = definition(src, name)
    return [ev.string(e) for e in node], line


def section_types(root, rel):
    """For each REGISTER line, the heading of the comment box above it."""
    with open(os.path.join(root, rel), encoding="utf-8-sig") as f:
        lines = f.read().split("\n")
    heading = None
    out = []
    for n, line in enumerate(lines, 1):
        m = re.match(r"\|\s+(\S.*?)\s*\|\s*$", line.strip())
        if m and not line.strip().startswith("|   "):
            heading = m.group(1)
        m = re.match(r'\s*(REGISTER_HID_DETECTOR\w*)\s*\(\s*"([^"]+)"\s*,\s*(\w+)\s*,\s*\w+\s*,\s*(\w+)', line)
        if m:
            out.append((m.group(2), m.group(3), m.group(4), heading, n))
    return out


def cs_string(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def pascal(name):
    return "".join(part[:1].upper() + part[1:] for part in name.split("_") if part)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.openrgb
    commit = oc.git_head(root)

    names_src = oc.Source(root, KEY_NAMES)
    key_names = {}
    toks = names_src.tokens
    for i, t in enumerate(toks):
        if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
            key_names[t.text] = oc.decode_string(toks[i + 2].text)

    out = []
    w = out.append

    # -- V1 controller ---------------------------------------------------
    ctl = oc.Source(root, V1 + "CorsairPeripheralController.cpp")
    ctl_ev = oc.Evaluator([ctl])
    rgb = oc.Source(root, V1 + "RGBController_CorsairPeripheral.cpp")
    rgb_ev = oc.Evaluator([rgb], strings=key_names)

    int_arrays = [(n, ints(ctl, ctl_ev, n)) for n in
                  ("keys", "keys_k70_mk2", "keys_k95_plat", "keys_k95", "st100",
                   "key_mapping_k95_plat_ansi", "key_mapping_k95_plat_iso", "key_mapping_k70_mk2_plat_iso")]
    matrices = [(n, matrix(rgb, rgb_ev, n)) for n in
                ("matrix_map", "matrix_map_k70_lux_rgb", "matrix_map_k70_mk2", "matrix_map_k95_platinum", "matrix_map_k95")]
    name_arrays = [(n, strings(rgb, rgb_ev, n)) for n in
                   ("led_names", "led_names_k70_lux_rgb", "led_names_k70_mk2", "led_names_k95_plat", "led_names_k95",
                    "led_names_k55", "corsair_mouse_leds", "corsair_m65_elite_leds", "corsair_sabre_rgb_leds",
                    "corsair_harpoon_pro_leds")]

    # Each LED-number list holds one number per name of its table.
    sizes = {n: len(v[0]) for n, v in name_arrays}
    for keys, names in (("keys", "led_names"), ("keys_k70_mk2", "led_names_k70_mk2"),
                        ("keys_k95_plat", "led_names_k95_plat"), ("keys_k95", "led_names_k95")):
        count = len(dict(int_arrays)[keys][0])
        if count != sizes[names]:
            sys.exit("%s has %d numbers, %s %d names" % (keys, count, names, sizes[names]))

    sections = section_types(root, V1 + "CorsairPeripheralControllerDetect.cpp")
    detect = oc.Source(root, V1 + "CorsairPeripheralControllerDetect.cpp")
    detect_ev = oc.Evaluator([detect])

    # -- K65 Mini ----------------------------------------------------------
    k65 = oc.Source(root, V1 + "RGBController_CorsairK65Mini.cpp")
    k65_ev = oc.Evaluator([k65], strings=key_names)
    k65_matrix = matrix(k65, k65_ev, "matrix_map")
    node, k65_keys_line = definition(k65, "keys")
    k65_keys = []
    for e in node:
        if not isinstance(e, Node) or len(e) != 2:
            raise CppError("K65 Mini keys: %r" % e)
        k65_keys.append((k65_ev.integer(e[0]), k65_ev.string(e[1])))

    # -- K55 RGB PRO XT --------------------------------------------------------
    xt_ctl = oc.Source(root, V1 + "CorsairK55RGBPROXTController.cpp")
    xt_keys = ints(xt_ctl, oc.Evaluator([xt_ctl]), "keys")
    xt = oc.Source(root, V1 + "RGBController_CorsairK55RGBPROXT.cpp")
    xt_ev = oc.Evaluator([xt], strings=key_names)
    xt_matrix = matrix(xt, xt_ev, "matrix_map")
    xt_names = strings(xt, xt_ev, "key_names")
    if len(xt_keys[0]) != len(xt_names[0]):
        sys.exit("K55 RGB PRO XT: %d numbers, %d names" % (len(xt_keys[0]), len(xt_names[0])))

    # -- V2 devices ----------------------------------------------------------
    v2h = oc.Source(root, V2 + "CorsairPeripheralV2Devices.h")
    v2 = oc.Source(root, V2 + "CorsairPeripheralV2Devices.cpp")
    v2_ev = oc.Evaluator([v2h, v2], strings=key_names)
    zone_vars = v2.definitions_of_type("corsair_v2_zone")
    zones = {}
    for var in zone_vars:
        node, line = definition(v2, var)
        if len(node) != 4:
            raise CppError("%s: %d fields" % (var, len(node)))
        zones[var] = (v2_ev.string(node[0]), node[1][0].text, v2_ev.integer(node[2]), v2_ev.integer(node[3]), line)
    device_vars = v2.definitions_of_type("corsair_v2_device")
    devices = {}
    for var in device_vars:
        node, line = definition(v2, var)
        if len(node) != 6 or not isinstance(node[4], Node):
            raise CppError("%s: %d fields" % (var, len(node)))
        zl = []
        for e in node[4]:
            if len(e) == 1 and e[0].text == "nullptr":
                continue
            if len(e) != 2 or e[0].text != "&" or e[1].text not in zones:
                raise CppError("%s: zone %r" % (var, e))
            zl.append(e[1].text)
        layout = node[5]
        if len(layout) == 1 and layout[0].text == "nullptr":
            layout_name = None
        elif len(layout) == 2 and layout[0].text == "&":
            layout_name = layout[1].text
        else:
            raise CppError("%s: layout %r" % (var, layout))
        devices[var] = (v2_ev.integer(node[0]), node[1][0].text, v2_ev.integer(node[2]), v2_ev.integer(node[3]),
                        zl, layout_name, line)
    node, list_line = definition(v2, "corsair_v2_device_list_data")
    order = []
    for e in node:
        if len(e) != 2 or e[0].text != "&" or e[1].text not in devices:
            raise CppError("device list: %r" % e)
        order.append(e[1].text)

    # -- write -----------------------------------------------------------------
    w("// <auto-generated>")
    w("//     Generated by tools/gen_corsair_lights.py from OpenRGB at %s" % commit[:10])
    w("//     (Controllers/CorsairPeripheralController and CorsairPeripheralV2Controller,")
    w("//     with the key names of RGBController/RGBControllerKeyNames.cpp). Do not edit")
    w("//     by hand.")
    w("// </auto-generated>")
    w("namespace PadForge.Common.Input.Peripherals")
    w("{")
    w("    internal static partial class CorsairLights")
    w("    {")
    w("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    w("        internal const string OpenRgbCommit = \"%s\";" % commit)

    def int_array(name, values, line, where):
        w("")
        w("        /// <summary>%s:%d %s.</summary>" % (where, line, name))
        w("        internal static readonly int[] %s =" % pascal(name))
        w("        {")
        for k in range(0, len(values), 16):
            w("            " + " ".join("%d," % v for v in values[k:k + 16]))
        w("        };")

    def str_array(name, values, line, where):
        w("")
        w("        /// <summary>%s:%d %s.</summary>" % (where, line, name))
        w("        internal static readonly string[] %s =" % pascal(name))
        w("        {")
        for v in values:
            w("            %s," % cs_string(v))
        w("        };")

    def matrix_out(name, rows, line, where):
        w("")
        w("        /// <summary>%s:%d %s, %d rows of %d.</summary>" % (where, line, name, len(rows), len(rows[0])))
        w("        internal static readonly CorsairMatrix %s = new(%d, %d, new[]" % (pascal(name), len(rows), len(rows[0])))
        w("        {")
        for r in rows:
            w("            " + " ".join("%d," % v for v in r))
        w("        });")

    ctl_file = "CorsairPeripheralController.cpp"
    rgb_file = "RGBController_CorsairPeripheral.cpp"
    for name, (values, line) in int_arrays:
        int_array(name, values, line, ctl_file)
    for name, (rows, line) in matrices:
        matrix_out(name, rows, line, rgb_file)
    for name, (values, line) in name_arrays:
        str_array(name, values, line, rgb_file)

    w("")
    w("        /// <summary>CorsairPeripheralControllerDetect.cpp: each registration's")
    w("        /// name, detector, product ID and the heading of the section its line")
    w("        /// sits in, with its line.</summary>")
    w("        internal static readonly (string Name, string Detector, ushort ProductId, string Section, int Line)[] V1Sections =")
    w("        {")
    for rname, det, pid_name, heading, line in sections:
        pid = detect_ev.integer(Expr([oc.Token("id", pid_name, line)]))
        w("            (%s, %s, 0x%04X, %s, %d)," % (cs_string(rname), cs_string(det), pid, cs_string(heading or ""), line))
    w("        };")

    matrix_out("k65_mini_matrix_map", k65_matrix[0], k65_matrix[1], "RGBController_CorsairK65Mini.cpp")
    w("")
    w("        /// <summary>RGBController_CorsairK65Mini.cpp:%d keys: each key's position" % k65_keys_line)
    w("        /// in the frame and OpenRGB's name for it, in the order matrix_map")
    w("        /// numbers them.</summary>")
    w("        internal static readonly (int Position, string Name)[] K65MiniKeys =")
    w("        {")
    for pos, name in k65_keys:
        w("            (%d, %s)," % (pos, cs_string(name)))
    w("        };")

    int_array("k55_pro_xt_keys", xt_keys[0], xt_keys[1], "CorsairK55RGBPROXTController.cpp")
    matrix_out("k55_pro_xt_matrix_map", xt_matrix[0], xt_matrix[1], "RGBController_CorsairK55RGBPROXT.cpp")
    str_array("k55_pro_xt_key_names", xt_names[0], xt_names[1], "RGBController_CorsairK55RGBPROXT.cpp")

    v2_file = "CorsairPeripheralV2Devices.cpp"
    for var in zone_vars:
        name, ztype, rows, cols, line = zones[var]
        w("")
        w("        /// <summary>%s:%d %s.</summary>" % (v2_file, line, var))
        w("        internal static readonly CorsairV2Zone %s = new(%s, CorsairZoneType.%s, %d, %d);"
          % (pascal(var), cs_string(name), pascal(ztype.replace("ZONE_TYPE_", "").lower()), rows, cols))
    for var in device_vars:
        pid, dtype, rows, cols, zl, layout_name, line = devices[var]
        w("")
        w("        /// <summary>%s:%d %s.</summary>" % (v2_file, line, var))
        w("        internal static readonly CorsairV2Device %s = new(%s, 0x%04X, CorsairDeviceType.%s, %d, %d,"
          % (pascal(var), cs_string(var), pid, pascal(dtype.replace("DEVICE_TYPE_", "").lower()), rows, cols))
        w("            new[] { %s }, %s);" % (", ".join(pascal(z) for z in zl),
                                            cs_string(layout_name) if layout_name else "null"))
    w("")
    w("        /// <summary>%s:%d corsair_v2_device_list_data, in its order.</summary>" % (v2_file, list_line))
    w("        internal static readonly CorsairV2Device[] V2Devices =")
    w("        {")
    for var in order:
        w("            %s," % pascal(var))
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
    print("wrote %s: %d V1 registrations, %d V2 zones, %d V2 devices" % (OUTPUT, len(sections), len(zone_vars), len(device_vars)))


if __name__ == "__main__":
    main()
