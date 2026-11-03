"""The Lenovo light tables, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/LenovoControllers:
- LenovoDevices.h: every lenovo_led array (a light's number and OpenRGB's name
  for it) and every lenovo_zone (its name, type, zone ID, light array and the
  first and last index of the array it takes).
- LenovoUSBController_Gen7_8/RGBController_Lenovo_Gen7_8.cpp: the Legion 5
  Gen 10's 24-zone array and zone.
- Lenovo4ZoneUSBController/LenovoDevices4Zone.h: the four zones of the 4-zone
  keyboards.
- LenovoLegionK500Controller: led_names[] of RGBController_LenovoK500.cpp and
  k500_matrix[] of LenovoK500Controller.cpp, the byte and bit of each key in
  the K500's custom-mode key mask.
- RGBController/RGBControllerKeyNames.cpp: the string behind each KEY_ and
  ZONE_ name the tables use.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/LenovoLights.g.cs, the
tables as OpenRGB holds them, each OpenRGB identifier written in Pascal case
(legion_Y760_ansi_leds becomes LegionY760AnsiLeds). The families resolve the
names to keys and build their light maps in LenovoLights.cs.

Usage:
    python tools/gen_lenovo_lights.py OPENRGB_ROOT
    python tools/gen_lenovo_lights.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "LenovoLights.g.cs")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def strip_comments(text):
    """The source with its comments blanked out, string literals kept."""
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
    """The value of a C string literal, quotes included in the argument."""
    body = literal[1:-1]
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), body)


def cs_string(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def pascal(name):
    return "".join(part[:1].upper() + part[1:] for part in name.split("_") if part)


def key_names(path):
    names = {}
    for name, literal in re.findall(r'const\s+char\s*\*\s*(\w+)\s*=\s*("(?:[^"\\]|\\.)*")\s*;', read(path)):
        names[name] = c_string(literal)
    return names


def body_after(text, start):
    """The text between the brace that opens at or after start and its match."""
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
    return text[i + 1:j - 1]


def led_arrays(text, names, where):
    """Every lenovo_led array: its name and its (number, name) pairs, in order."""
    arrays = []
    for m in re.finditer(r"(?:static\s+)?const\s+lenovo_led\s+(\w+)\s*\[\s*\]\s*(?:=\s*)?", text):
        body = body_after(text, m.end())
        leds = []
        for number, value in re.findall(r'\{\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*("(?:[^"\\]|\\.)*"|\w+)\s*\}', body):
            if value.startswith('"'):
                name = c_string(value)
            elif value in names:
                name = names[value]
            else:
                sys.exit("%s: unknown key name %s" % (m.group(1), value))
            leds.append((int(number, 0), name))
        if not leds:
            sys.exit("%s: no lights" % m.group(1))
        arrays.append((m.group(1), leds, where))
    return arrays


def zones(text, names, where):
    """Every lenovo_zone: name, type, ID, light array, first and last index."""
    out = []
    for m in re.finditer(r"(?:static\s+)?(?:const\s+)?lenovo_zone\s+(\w+)\s*(?:=\s*)?(?=\{)", text):
        fields = [f.strip() for f in body_after(text, m.end()).split(",") if f.strip()]
        if len(fields) != 9:
            sys.exit("%s: %d fields" % (m.group(1), len(fields)))
        zname = c_string(fields[0]) if fields[0].startswith('"') else names[fields[0]]
        out.append((m.group(1), zname, fields[1], int(fields[2], 0), fields[6], int(fields[7], 0), int(fields[8], 0), where))
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    lenovo = os.path.join(args.openrgb, "Controllers", "LenovoControllers")
    names = key_names(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp"))
    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()

    sources = [
        ("LenovoDevices.h", os.path.join(lenovo, "LenovoDevices.h")),
        ("RGBController_Lenovo_Gen7_8.cpp", os.path.join(lenovo, "LenovoUSBController_Gen7_8", "RGBController_Lenovo_Gen7_8.cpp")),
        ("LenovoDevices4Zone.h", os.path.join(lenovo, "Lenovo4ZoneUSBController", "LenovoDevices4Zone.h")),
    ]
    arrays, all_zones = [], []
    for where, path in sources:
        text = strip_comments(read(path))
        arrays += led_arrays(text, names, where)
        all_zones += zones(text, names, where)
    known = {a[0] for a in arrays}
    for z in all_zones:
        if z[4] not in known:
            sys.exit("%s: unknown array %s" % (z[0], z[4]))
        count = len(next(a for a in arrays if a[0] == z[4])[1])
        if not 0 <= z[5] <= z[6] < count:
            sys.exit("%s: indexes %d to %d out of %d" % (z[0], z[5], z[6], count))

    k500 = os.path.join(lenovo, "LenovoLegionK500Controller")
    k500_rgb = strip_comments(read(os.path.join(k500, "RGBController_LenovoK500.cpp")))
    k500_names = []
    for token in re.findall(r"\w+", body_after(k500_rgb, re.search(r"led_names\s*\[\s*\]\s*=", k500_rgb).end())):
        k500_names.append(names[token])
    k500_ctl = strip_comments(read(os.path.join(k500, "LenovoK500Controller.cpp")))
    k500_matrix = [(int(b), int(bit)) for b, bit in re.findall(
        r"\{\s*(\d+)\s*,\s*(\d+)\s*\}", body_after(k500_ctl, re.search(r"k500_matrix\s*\[\s*104\s*\]\s*=", k500_ctl).end()))]
    if len(k500_names) != 104 or len(k500_matrix) != 104:
        sys.exit("K500: %d names, %d mask bits" % (len(k500_names), len(k500_matrix)))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_lenovo_lights.py from OpenRGB at %s" % commit[:10])
    out.append("//     (Controllers/LenovoControllers: LenovoDevices.h, RGBController_Lenovo_Gen7_8.cpp,")
    out.append("//     LenovoDevices4Zone.h and the Legion K500's led_names[] and k500_matrix[],")
    out.append("//     with the key names of RGBController/RGBControllerKeyNames.cpp). Do not edit")
    out.append("//     by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class LenovoLights")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for name, leds, where in arrays:
        out.append("")
        out.append("        /// <summary>%s %s.</summary>" % (where, name))
        out.append("        internal static readonly LenovoLed[] %s =" % pascal(name))
        out.append("        {")
        for number, value in leds:
            out.append("            new(0x%02X, %s)," % (number, cs_string(value)))
        out.append("        };")
    for name, zname, ztype, zid, leds, start, end, where in all_zones:
        out.append("")
        out.append("        /// <summary>%s %s.</summary>" % (where, name))
        out.append("        internal static readonly LenovoZone %s = new(%s, LenovoZoneType.%s, %d, %s, %d, %d);"
                   % (pascal(name), cs_string(zname), pascal(ztype.replace("ZONE_TYPE_", "").lower()), zid,
                      pascal(leds), start, end))
    out.append("")
    out.append("        /// <summary>RGBController_LenovoK500.cpp led_names[], in the order of the")
    out.append("        /// K500's lights.</summary>")
    out.append("        internal static readonly string[] K500LedNames =")
    out.append("        {")
    for value in k500_names:
        out.append("            %s," % cs_string(value))
    out.append("        };")
    out.append("")
    out.append("        /// <summary>LenovoK500Controller.cpp k500_matrix[]: the byte and the bit of")
    out.append("        /// each light in the custom mode's key mask.</summary>")
    out.append("        internal static readonly (byte Byte, byte Bit)[] K500Mask =")
    out.append("        {")
    for b, bit in k500_matrix:
        out.append("            (%d, %d)," % (b, bit))
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
    print("wrote %s: %d arrays, %d zones" % (OUTPUT, len(arrays), len(all_zones)))


if __name__ == "__main__":
    main()
