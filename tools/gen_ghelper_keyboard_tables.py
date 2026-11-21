#!/usr/bin/env python3
"""Writes HidFamilies/GHelperKeyboardTables.g.cs from G-Helper's external keyboards (#557).

    python -E -s tools/gen_ghelper_keyboard_tables.py GHELPER_CLONE [--check]

The keyboards here are the ones only G-Helper lights: no OpenRGB family and no SignalRGB
plugin names them, so PadForge's ASUS Aura USB family lights them as G-Helper's
AsusKeyboard does (AsusAuraUsbGHelperKeyboards.cs). Each comes from its model class in
app/Peripherals/Keyboard/Models: the product ID its constructor passes, the name
GetDisplayName returns, and the two layouts LayoutName picks between by IsIsoLayout. The
script stops when a class of the chain sets a report ID, a path, a packet size, a
timeout or media keys, or replaces how AsusKeyboard finds the collection, exchanges a
packet, counts its LEDs or says it has them, since the family ports AsusKeyboard's own.

A layout is its rows of keys and its LED IDs, AuraKeyboardLayouts.Data, key by key
(LedIds[i] is the LED of grid key i). An ISO layout made by ToIso is rebuilt here from
its rows by the same rule, and the script stops when ToIso's code changes. A key's HID
usage is the one whose name G-Helper's UsageName gives it. A key no usage names gets 0,
and of those only Fn keeps its name, which PluginKeyNames gives a label. Any other is
written with no name, the tables' mark for a key nothing names, which Map Key Lights
asks about.

A key's place is its row and the cell its left edge rounds to, never left of the key
before it, the rule gen_asus_laptop_tables.py places asusctl's keys by.

The pin is GHELPER in gen_asus_laptop_tables.py, the one release prep moves.
"""
import argparse
import os
import re
import subprocess
import sys

from gen_asus_laptop_tables import GHELPER

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                   "GHelperKeyboardTables.g.cs")

LAYOUTS_FILE = "app/Peripherals/Keyboard/AuraKeyboardLayouts.cs"
KEYBOARD_FILE = "app/Peripherals/Keyboard/AsusKeyboard.cs"
MODELS = "app/Peripherals/Keyboard/Models"

# The model classes PadForge lights this way, by file.
KEYBOARDS = (("Falchion.cs", "FalchionAce75HE"),)

# The keys no usage names that keep their name in the table.
LABELED = ("Fn",)

# ToIso (AuraKeyboardLayouts.cs), statement by statement, whitespace aside.
TO_ISO = [
    'if (def.Name == "\\\\") keys.Add(def with { Name = "Enter" });',
    'else if (def.Name == "Enter") { keys.Add(new("#")); keys.Add(def with { Width = 1.25f }); }',
    'else if (def.Name == "Shift") { keys.Add(def with { Width = 1.25f }); keys.Add(new("<")); }',
    'else keys.Add(def);',
]

# UsageName's ranges (AuraKeyboardLayouts.cs), as the code writes them.
USAGE_RANGES = [
    ">= 0x04 and <= 0x1D => ((char)('A' + usage - 0x04)).ToString(),",
    ">= 0x1E and <= 0x26 => ((char)('1' + usage - 0x1E)).ToString(),",
    '0x27 => "0",',
    '>= 0x3A and <= 0x45 => "F" + (usage - 0x39),',
    '>= 0x59 and <= 0x61 => "Num " + (usage - 0x58),',
]


def git_show(clone, path):
    done = subprocess.run(["git", "-C", clone, "show", f"{GHELPER}:{path}"], capture_output=True)
    if done.returncode != 0:
        sys.exit(f"git show failed for {path}: {done.stderr.decode(errors='replace')}")
    return done.stdout.decode("utf-8-sig")


def squash(text):
    return re.sub(r"\s+", " ", text).strip()


def line_of(text, index):
    return text.count("\n", 0, index) + 1


def unescape(literal):
    return re.sub(r"\\(.)", r"\1", literal)


def quote(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def usages(text):
    """Key name to HID usage, from UsageNames and UsageName's ranges."""
    flat = squash(text)
    for line in USAGE_RANGES:
        if squash(line) not in flat:
            sys.exit(f"UsageName no longer reads: {line}")
    by_name = {}

    def add(name, usage):
        if name in by_name:
            sys.exit(f"UsageName gives {name} twice")
        by_name[name] = usage

    for usage in range(0x04, 0x1E):
        add(chr(ord("A") + usage - 0x04), usage)
    for usage in range(0x1E, 0x27):
        add(chr(ord("1") + usage - 0x1E), usage)
    add("0", 0x27)
    for usage in range(0x3A, 0x46):
        add(f"F{usage - 0x39}", usage)
    for usage in range(0x59, 0x62):
        add(f"Num {usage - 0x58}", usage)
    block = re.search(r"Dictionary<int, string> UsageNames = new\(\)\s*\{(.*?)\};", text, re.S)
    if not block:
        sys.exit("no UsageNames in AuraKeyboardLayouts.cs")
    for usage, name in re.findall(r'\[0x([0-9A-Fa-f]{2})\] = "((?:[^"\\]|\\.)*)"', block.group(1)):
        add(unescape(name), int(usage, 16))
    return by_name


def rows_of(text, name):
    """A rows list: (key name, width, gap) per key, row by row, and its line."""
    found = re.search(r"List<KeyDef\[\]> " + name + r" = new\(\)\s*\{(.*?)\n\s*\};", text, re.S)
    if not found:
        sys.exit(f"no {name} in AuraKeyboardLayouts.cs")
    rows = []
    for row in re.findall(r"new KeyDef\[\] \{(.*?)\}", found.group(1)):
        keys = []
        for key, args in re.findall(r'new\("((?:[^"\\]|\\.)*)"((?:,\s*[\d.]+f)*)\)', row):
            numbers = [float(n) for n in re.findall(r"([\d.]+)f", args)]
            if len(numbers) > 2:
                sys.exit(f"{name}: {key} sets a height or a top, which this script does not place")
            width = numbers[0] if numbers else 1.0
            gap = numbers[1] if len(numbers) > 1 else 0.0
            keys.append((unescape(key), width, gap))
        rows.append(keys)
    return rows, line_of(text, found.start())


def to_iso(rows):
    iso = []
    for row in rows:
        keys = []
        for name, width, gap in row:
            if name == "\\":
                keys.append(("Enter", width, gap))
            elif name == "Enter":
                keys.append(("#", 1.0, 0.0))
                keys.append((name, 1.25, gap))
            elif name == "Shift":
                keys.append((name, 1.25, gap))
                keys.append(("<", 1.0, 0.0))
            else:
                keys.append((name, width, gap))
        iso.append(keys)
    return iso


def check_to_iso(text):
    body = re.search(r"private static List<KeyDef\[\]> ToIso\(List<KeyDef\[\]> rows\)\s*\{(.*?)\n        \}", text, re.S)
    if not body:
        sys.exit("no ToIso in AuraKeyboardLayouts.cs")
    flat = squash(body.group(1))
    for statement in TO_ISO:
        if squash(statement) not in flat:
            sys.exit(f"ToIso no longer reads: {statement}")
    if flat.count("if (def.Name") != 3:
        sys.exit("ToIso has more cases than this script rebuilds")


def layout(text, names, name):
    """(rows source line, data line, [(key, led, x, y, usage)]) of a Data entry."""
    entry = re.search(r'\["' + name + r'"\] = new\((ToIso\()?(\w+)\)?,\s*new byte\[\] \{([^}]*)\}\)', text)
    if not entry:
        sys.exit(f"no Data entry {name} in AuraKeyboardLayouts.cs")
    rows, rows_line = rows_of(text, entry.group(2))
    if entry.group(1):
        rows = to_iso(rows)
    leds = [int(v, 16) for v in re.findall(r"0x([0-9A-Fa-f]{2})", entry.group(3))]
    count = sum(len(r) for r in rows)
    if count != len(leds):
        sys.exit(f"{name}: {count} keys and {len(leds)} LED IDs")
    keys = []
    at_led = 0
    for y, row in enumerate(rows):
        x = 0.0
        cell = -1
        for key, width, gap in row:
            x += gap
            place = max(cell + 1, round(x))
            cell = place
            x += width
            keys.append((key, leds[at_led], place, y, names.get(key, 0)))
            at_led += 1
    return rows_line, line_of(text, entry.start()), keys


def class_body(text, name):
    parts = re.split(r"(?=\bpublic class )", text)
    for part in parts:
        if re.match(r"public class " + name + r"\b", part):
            return part
    return None


def keyboard(clone, file, name):
    """(product, display name, ANSI layout, ISO layout, line range) of a model class, after
    checking that nothing in its chain changes what the family sends."""
    text = git_show(clone, f"{MODELS}/{file}")
    body = class_body(text, name)
    if body is None:
        sys.exit(f"no class {name} in {file}")
    start = line_of(text, text.index("public class " + name))
    end = start + body.rstrip().count("\n")
    product = re.search(r"public " + name + r"\(\)\s*:\s*base\(0x([0-9A-Fa-f]{4})\)", body)
    if not product:
        sys.exit(f"{name}: its constructor passes more than a product ID")
    display = re.search(r'GetDisplayName\(\)\s*\{\s*return "([^"]*)";', body)
    picked = re.search(r'LayoutName\(\)\s*\{\s*return IsIsoLayout \? "(\w+)" : "(\w+)";', body)
    if not display or not picked:
        sys.exit(f"{name}: no display name or no layout pair")
    chain = name
    while True:
        part = class_body(text, chain)
        if part is None:
            break
        for member in ("USBPacketSize", "USBTimeout", "MediaKeyCount", "SetLedColors", "FindVendorDevices", "SetProvider",
                       "WriteForResponse", "LedCount", "HasRGB", "HasPerKeyRGB"):
            if member in part:
                sys.exit(f"{chain} sets {member}, which the family does not port")
        base = re.match(r"public class \w+\s*:\s*(\w+)", part).group(1)
        if base == "AsusKeyboard":
            break
        chain = base
    for parent, call in (("FalchionRX", "base(productId)"), ("Falchion", "base(0x0B05, productId)")):
        part = class_body(text, parent)
        if part is None or not re.search(r"protected " + parent + r"\(ushort productId\)\s*:\s*" + re.escape(call), part):
            sys.exit(f"{parent} no longer passes the product ID alone")
    return int(product.group(1), 16), display.group(1), picked.group(2), picked.group(1), f"{file}:{start}-{end}"


def iso_layouts(clone):
    text = git_show(clone, KEYBOARD_FILE)
    found = re.search(r"static readonly int\[\] IsoMultiLayouts = \{([^}]*)\};", text)
    if not found:
        sys.exit("no IsoMultiLayouts in AsusKeyboard.cs")
    return [int(v) for v in re.findall(r"\d+", found.group(1))], line_of(text, found.start())


def method_name(name):
    return "Layout_" + re.sub(r"\W", "_", name)


def generate(clone):
    text = git_show(clone, LAYOUTS_FILE)
    check_to_iso(text)
    names = usages(text)
    iso, iso_line = iso_layouts(clone)
    boards = [keyboard(clone, file, name) for file, name in KEYBOARDS]
    lines = [
        "// Generated by tools/gen_ghelper_keyboard_tables.py. Do not edit.",
        "namespace PadForge.Common.Input.Peripherals.HidFamilies",
        "{",
        "    /// <summary>The external keyboards only G-Helper lights, and their key layouts",
        "    /// (app/Peripherals/Keyboard at the pinned commit).</summary>",
        "    internal static class GHelperKeyboardTables",
        "    {",
        f"        internal const string GHelperCommit = {quote(GHELPER)};",
        "",
        f"        /// <summary>IsoMultiLayouts (AsusKeyboard.cs:{iso_line}): the layout IDs of an ISO board.</summary>",
        "        internal static readonly int[] IsoLayouts = { " + ", ".join(str(v) for v in iso) + " };",
        "",
        "        /// <summary>Each model class: product ID, name, ANSI layout, ISO layout, source.</summary>",
        "        internal static readonly GHelperKeyboard[] Keyboards =",
        "        {",
    ]
    for product, display, ansi, iso_name, source in boards:
        lines.append(f"            new(0x{product:04X}, {quote(display)}, {quote(ansi)}, {quote(iso_name)}, {quote(source)}),")
    lines += ["        };", ""]
    layouts = []
    for _, _, ansi, iso_name, _ in boards:
        for name in (ansi, iso_name):
            if name not in layouts:
                layouts.append(name)
    lines.append("        /// <summary>A layout's keys in G-Helper's order, or null.</summary>")
    lines.append("        internal static PluginKeyTable Layout(string name) => name switch")
    lines.append("        {")
    for name in layouts:
        lines.append(f"            {quote(name)} => {method_name(name)}(),")
    lines += ["            _ => null,", "        };", ""]
    for name in layouts:
        rows_line, data_line, keys = layout(text, names, name)
        lines.append(f"        private static PluginKeyTable {method_name(name)}() => new(")
        lines.append(f"            {quote(f'g-helper AuraKeyboardLayouts.cs:{data_line} and {rows_line}')},")
        lines.append("            new[] { " + ", ".join(quote(k if u or k in LABELED else "") for k, _, _, _, u in keys) + " },")
        lines.append("            new[] { " + ", ".join(f"0x{led:02X}" for _, led, _, _, _ in keys) + " },")
        lines.append("            new byte[] { " + ", ".join(str(x) for _, _, x, _, _ in keys) + " },")
        lines.append("            new byte[] { " + ", ".join(str(y) for _, _, _, y, _ in keys) + " },")
        lines.append("            new ushort[] { " + ", ".join(f"0x{u:02X}" for _, _, _, _, u in keys) + " });")
        lines.append("")
    while lines[-1] == "":
        lines.pop()
    lines += ["    }", "}", ""]
    return "\r\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("ghelper")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = generate(args.ghelper)
    if args.check:
        if not os.path.exists(OUT) or open(OUT, encoding="utf-8", newline="").read() != text:
            print("GHelperKeyboardTables.g.cs differs")
            return 1
        print("the G-Helper keyboard tables match")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote", os.path.normpath(OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
