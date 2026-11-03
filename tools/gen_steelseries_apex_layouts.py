"""The SteelSeries Apex key layouts, from OpenRGB (#557).

WHAT IT READS, under OpenRGB's Controllers/SteelSeriesController:
- SteelSeriesApexController/SteelSeriesApexController.cpp, keys[]: the key
  codes of the per-key frame, in the order OpenRGB sends them.
- SteelSeriesApexRegions.h: MATRIX_MAP_ANSI, the 6 by 22 grid of the base
  layout, each cell an index into keys[] or NA; the TKL, Mini, ISO and JIS
  patches that rewrite cells; and patch_lookup, which picks the patches by the
  first five characters of a board's serial (its SKU).
- SteelSeriesApexMController/SteelSeriesApexMController.cpp, keys_m[]: the
  Apex M750's 6 by 22 grid, from the bottom row up, in the same index space.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/SteelSeriesApexLayouts.g.cs.
The tables are written as OpenRGB holds them and applied in
SteelSeriesApexLayouts.cs the way SetSkuRegion applies them: the base patch,
then the region patch. patch_lookup is a std::map built from an initializer
list, which keeps the first entry of a SKU listed twice, and so does this
script. Lines OpenRGB has commented out are left out.

Usage:
    python tools/gen_steelseries_apex_layouts.py OPENRGB_ROOT
    python tools/gen_steelseries_apex_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "SteelSeriesApexLayouts.g.cs")

ROWS, COLUMNS = 6, 22
PATCHES = [
    ("apex_tkl_us_region_patch", "TklUs"),
    ("apex_mini_us_region_patch", "MiniUs"),
    ("apex_iso_region_patch", "Iso"),
    ("apex_jp_region_patch", "Jp"),
]


def strip_comments(text):
    """The source with its // and /* */ comments blanked out."""
    text = re.sub(r"/\*.*?\*/", lambda m: " " * len(m.group(0)), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def array_body(text, name):
    """The text between the braces of the array named name."""
    m = re.search(r"\b" + re.escape(name) + r"\s*\[\s*\]\s*=\s*\{", text)
    if not m:
        sys.exit("no array " + name)
    depth, i = 1, m.end()
    while depth:
        depth += {"{": 1, "}": -1}.get(text[i], 0)
        i += 1
    return text[m.end():i - 1]


def cells(body, na):
    """Each number of an array or grid body, with NA as na."""
    out = []
    for token in re.findall(r"0x[0-9A-Fa-f]+|\d+|NA", body):
        out.append(na if token == "NA" else int(token, 0))
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    ss = os.path.join(args.openrgb, "Controllers", "SteelSeriesController")
    regions = strip_comments(read(os.path.join(ss, "SteelSeriesApexRegions.h")))
    apex = strip_comments(read(os.path.join(ss, "SteelSeriesApexController", "SteelSeriesApexController.cpp")))
    apex_m = strip_comments(read(os.path.join(ss, "SteelSeriesApexMController", "SteelSeriesApexMController.cpp")))
    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()

    keys = cells(array_body(apex, "keys"), None)
    if None in keys or len(keys) != 112:
        sys.exit("keys[] is not 112 key codes: %d" % len(keys))

    # MATRIX_MAP_ANSI is a #define continued with backslashes.
    define = re.search(r"#define\s+MATRIX_MAP_ANSI((?:[^\n]*\\\n)*[^\n]*)", regions).group(1)
    ansi = cells(define, -1)
    if len(ansi) != ROWS * COLUMNS:
        sys.exit("MATRIX_MAP_ANSI is not %d cells: %d" % (ROWS * COLUMNS, len(ansi)))

    patches = {}
    for name, cs_name in PATCHES:
        body = re.search(r"\b" + name + r"\s*=\s*\{(.*?)\};", regions, re.S)
        if not body:
            sys.exit("no patch " + name)
        triples = re.findall(r"\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\w+)\s*\}", body.group(1))
        patches[name] = [(int(r), int(c), -1 if v == "NA" else int(v)) for r, c, v in triples]

    lookup = re.search(r"patch_lookup\s*=\s*\{(.*?)\n\};", regions, re.S)
    if not lookup:
        sys.exit("no patch_lookup")
    skus = []
    seen = set()
    for sku, base, region in re.findall(r"\{\s*\"(\d+)\"\s*,\s*\{\s*(\{\}|\w+)\s*,\s*(\{\}|\w+)\s*,", lookup.group(1)):
        if sku in seen:
            continue
        seen.add(sku)
        skus.append((sku, None if base == "{}" else base, None if region == "{}" else region))

    m750 = cells(array_body(apex_m, "keys_m"), -1)
    m750 = [-1 if v == 0xFF else v for v in m750]
    if len(m750) != ROWS * COLUMNS:
        sys.exit("keys_m is not %d cells: %d" % (ROWS * COLUMNS, len(m750)))

    def grid(values):
        lines = []
        for row in range(ROWS):
            part = values[row * COLUMNS:(row + 1) * COLUMNS]
            lines.append("            " + ", ".join("%3d" % v for v in part) + ",")
        return "\n".join(lines)

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_steelseries_apex_layouts.py from OpenRGB at")
    out.append("//     %s (Controllers/SteelSeriesController: SteelSeriesApexRegions.h," % commit[:10])
    out.append("//     SteelSeriesApexController.cpp keys[] and SteelSeriesApexMController.cpp")
    out.append("//     keys_m[]). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class SteelSeriesApexLayouts")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    out.append("")
    out.append("        /// <summary>The key codes of the per-key frame, keys[], in OpenRGB's order.</summary>")
    out.append("        internal static readonly byte[] Keys =")
    out.append("        {")
    for i in range(0, len(keys), 10):
        out.append("            " + ", ".join("0x%02X" % k for k in keys[i:i + 10]) + ",")
    out.append("        };")
    out.append("")
    out.append("        /// <summary>MATRIX_MAP_ANSI: 6 rows of 22 from the top row down, each cell")
    out.append("        /// an index into <see cref=\"Keys\"/>, or -1 for no key.</summary>")
    out.append("        internal static readonly sbyte[] Ansi =")
    out.append("        {")
    out.append(grid(ansi))
    out.append("        };")
    for name, cs_name in PATCHES:
        out.append("")
        out.append("        /// <summary>%s: row, column and the index the cell takes, -1 for none.</summary>" % name)
        out.append("        internal static readonly Patch[] %s =" % cs_name)
        out.append("        {")
        for r, c, v in patches[name]:
            out.append("            new(%d, %d, %d)," % (r, c, v))
        out.append("        };")
    out.append("")
    out.append("        /// <summary>patch_lookup: a SKU, its base patch and its region patch,")
    out.append("        /// null for none, the first entry kept where OpenRGB lists a SKU twice.</summary>")
    out.append("        internal static readonly (string Sku, Patch[] Base, Patch[] Region)[] Skus =")
    out.append("        {")
    names = dict(PATCHES)
    for sku, base, region in skus:
        b = names[base] if base else "null"
        r = names[region] if region else "null"
        out.append("            (\"%s\", %s, %s)," % (sku, b, r))
    out.append("        };")
    out.append("")
    out.append("        /// <summary>keys_m: the Apex M750's 6 rows of 22 from the bottom row up,")
    out.append("        /// each cell an index into <see cref=\"Keys\"/>, or -1 for no key.</summary>")
    out.append("        internal static readonly sbyte[] M750 =")
    out.append("        {")
    out.append(grid(m750))
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
    print("wrote %s: %d keys, %d SKUs" % (OUTPUT, len(keys), len(skus)))


if __name__ == "__main__":
    main()
