"""The LED name tables of the OpenRGB keyboards that list their keys by hand (#557).

WHAT IT READS, at the OpenRGB commit the clone is on:
- RGBController/RGBControllerKeyNames.cpp: the string of each KEY_EN_ name.
- The led_names arrays of the HyperX, Ducky and Cougar keyboard controllers
  in TABLES below, in order, each entry a KEY_EN_ name or a string literal.
  Lines OpenRGB has commented out are left out, as the compiler leaves them.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/OpenRgbLedNames.g.cs:
one string array per table, the names as OpenRGB's controller names its LEDs,
in the order its frames carry their colors.

Usage:
    python tools/gen_openrgb_led_names.py OPENRGB_ROOT
    python tools/gen_openrgb_led_names.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "OpenRgbLedNames.g.cs")

HX = "Controllers/HyperXKeyboardController/"
TABLES = [
    ("HyperXAlloyElite2", HX + "HyperXAlloyElite2Controller/RGBController_HyperXAlloyElite2.cpp", "led_names", 128),
    ("HyperXAlloyElite", HX + "HyperXAlloyEliteController/RGBController_HyperXAlloyElite.cpp", "led_names", 128),
    ("HyperXAlloyFps", HX + "HyperXAlloyFPSController/RGBController_HyperXAlloyFPS.cpp", "led_names", 106),
    ("HyperXAlloyOrigins", HX + "HyperXAlloyOriginsController/RGBController_HyperXAlloyOrigins.cpp", "led_names", 107),
    ("HyperXAlloyOrigins60", HX + "HyperXAlloyOrigins60and65Controller/RGBController_HyperXAlloyOrigins60and65.cpp",
     "led_names_60", 71),
    ("HyperXAlloyOrigins65", HX + "HyperXAlloyOrigins60and65Controller/RGBController_HyperXAlloyOrigins60and65.cpp",
     "led_names_65", 77),
    ("HyperXOrigins2_65", HX + "HyperXOrigins2_65Controller/RGBController_HyperXOrigins2_65.cpp", "led_names", 74),
    ("DuckyKeyboard", "Controllers/DuckyKeyboardController/RGBController_DuckyKeyboard.cpp", "led_names", 132),
    ("CougarKeyboard", "Controllers/CougarController/CougarKeyboardController/RGBController_CougarKeyboard.cpp",
     "led_names", 113),
]


def strip_comments(text):
    """The source with its // and /* */ comments blanked out, string literals kept."""
    out, i, n = [], 0, len(text)
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
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            i = text.index("*/", i) + 2
            out.append(" ")
        else:
            out.append(c)
            i += 1
    return "".join(out)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def array_body(text, name, path):
    m = re.search(r"\b" + re.escape(name) + r"\s*\[\s*\]\s*=\s*\{", text)
    if not m:
        sys.exit("no array %s in %s" % (name, path))
    depth, i = 1, m.end()
    while depth:
        if text[i] == '"':
            i += 1
            while text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    return text[m.end():i - 1]


def unescape(literal):
    body = literal[1:-1]
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), body)


def cs_string(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    keynames = {}
    source = strip_comments(read(os.path.join(args.openrgb, "RGBController", "RGBControllerKeyNames.cpp")))
    for name, literal in re.findall(r"const\s+char\s*\*\s*(KEY_\w+)\s*=\s*(\"(?:\\.|[^\"\\])*\")\s*;", source):
        keynames[name] = unescape(literal)
    if len(keynames) < 100:
        sys.exit("too few key names: %d" % len(keynames))

    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()

    tables = []
    for cs_name, rel, array, count in TABLES:
        path = os.path.join(args.openrgb, *rel.split("/"))
        body = array_body(strip_comments(read(path)), array, rel)
        names = []
        for token in re.findall(r"\"(?:\\.|[^\"\\])*\"|[A-Za-z_]\w*", body):
            if token.startswith('"'):
                names.append(unescape(token))
            elif token in keynames:
                names.append(keynames[token])
            else:
                sys.exit("unknown name %s in %s" % (token, rel))
        if len(names) != count:
            sys.exit("%s %s has %d names, not %d" % (rel, array, len(names), count))
        tables.append((cs_name, rel, array, names))

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_openrgb_led_names.py from OpenRGB at")
    out.append("//     %s (the led_names arrays of the HyperX, Ducky and Cougar keyboard" % commit[:10])
    out.append("//     controllers, with RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class OpenRgbLedNames")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    for cs_name, rel, array, names in tables:
        out.append("")
        out.append("        /// <summary>%s, %s.</summary>" % (rel.split("/")[-1], array))
        out.append("        internal static readonly string[] %s =" % cs_name)
        out.append("        {")
        for name in names:
            out.append("            %s," % cs_string(name))
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
    print("wrote %s: %d tables" % (OUTPUT, len(tables)))


if __name__ == "__main__":
    main()
