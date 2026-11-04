"""The key tables of the keyboards PadForge lights from SignalRGB's plugins.

OpenRGB is PadForge's source for a device wherever it has one. It has no
controller for these keyboards, and SignalRGB's plugins (github.com/SRGBmods,
cloned as signal-plugins) drive them. Each plugin lists a keyboard's key names,
their places and the LED index each key takes. This script reads those three
lists, as text, and writes them to
PadForge.App/Common/Input/Peripherals/HidFamilies/PluginKeyTables.g.cs. It takes
the facts and no code. The repository has no license file.

Usage:
    python tools/gen_plugin_key_tables.py SIGNAL_PLUGINS_ROOT
    python tools/gen_plugin_key_tables.py SIGNAL_PLUGINS_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "PluginKeyTables.g.cs")

# The table's name, the plugin, and the names of its three lists.
TABLES = [
    ("SpeedlinkOriosKb", "SpeedLink/Speedlink_ORIOS_RGB_Opto-Mech_KB.js", "vKeyNames", "vKeyPositions", "vKeyIndexes"),
    ("FnaticMiniStreak", "Fnatic/Fnatic_MiniSTREAK_Speed_Keyboard.js", "vKeyNames", "vKeyPositions", "vKeys"),
    ("ClaymoreIiRight", "Asus/ASUS_ROG_Claymore_II.js", "vKeyNames", "vKeyPositionsRight", "vKeysRight"),
    ("ClaymoreIiLeft", "Asus/ASUS_ROG_Claymore_II.js", "vKeyNames", "vKeyPositionsLeft", "vKeysLeft"),
    ("K65Mini", "Corsair/Modern_Corsair/Corsair_K65_Mini_Keyboard.js", "vKeyNames", "vKeyPositions", "vKeys"),
    ("Strafe", "Corsair/Legacy_Corsair/Corsair_Strafe_Keyboard.js", "vKeyNames", "vKeyPositions", "vKeys"),
    ("Rk61", "Royal Kludge/RK_61_Keyboard.js", "vKeyNames", "vKeyPositions", "vKeys"),
    ("Rk84", "Royal Kludge/RK_84_Keyboard.js", "vKeyNames", "vKeyPositions", "vKeys"),
]

# Keyboards a plugin keeps in a library object: the table's name, the plugin and
# the entry, whose ledNames, ledPositions and ledMap are the three lists.
LIBRARY_TABLES = [
    ("K70ProMini", "Corsair/Modern_Corsair/Corsair_Bragi_Device.js", "K70 Pro Mini"),
    ("K100Air", "Corsair/Modern_Corsair/Corsair_Bragi_Device.js", "K100 Air"),
]

# The K70 Pro Mini's ledMap holds the second row's two side lights a row late,
# 157 and 168, where its names and places hold them with Tab and backslash. Read
# as written, every key of that row and the next would take its neighbor's LED.
# The K65 Mini's plugin holds the same LED numbers with each key's own name: Tab
# 39, Q 16, Caps Lock 53, A 0. A pair is the LED and the LED it goes beside.
MOVED_LEDS = {
    "K70ProMini": ((157, 39), (168, 45)),
}

# The Claymore II's table with the number pad on the right holds the two ISO
# keys, at the places the plugin gives them, and its list of names leaves them
# out, so every later name would land one or two keys off.
MISSING_NAMES = {
    "ClaymoreIiRight": (("'", "ISO #"), ("Left Shift", "ISO <")),
}


def parse_array(text, start):
    """The JavaScript array literal at text[start], as Python lists, numbers
    and strings, and the index just past it."""
    i = start
    assert text[i] == "["
    i += 1
    items = []
    while True:
        while True:
            while text[i] in " \t\r\n,":
                i += 1
            if text.startswith("//", i):
                i = text.index("\n", i)
                continue
            if text.startswith("/*", i):
                i = text.index("*/", i) + 2
                continue
            break
        c = text[i]
        if c == "]":
            return items, i + 1
        if c == "[":
            item, i = parse_array(text, i)
            items.append(item)
        elif c in "\"'":
            j = i + 1
            value = []
            while text[j] != c:
                if text[j] == "\\":
                    j += 1
                value.append(text[j])
                j += 1
            items.append("".join(value))
            i = j + 1
        else:
            m = re.compile(r"0[xX][0-9a-fA-F]+|-?\d+").match(text, i)
            if not m:
                raise ValueError(f"unexpected {text[i:i + 20]!r}")
            items.append(int(m.group(0), 0))
            i = m.end()


def read_entry_list(text, entry, name):
    start = re.search(r'"' + re.escape(entry) + r'"\s*:\s*\{', text)
    if not start:
        raise ValueError(f"no entry {entry}")
    m = re.compile(name + r"\s*:\s*\[").search(text, start.end())
    if not m:
        raise ValueError(f"no list {name} in {entry}")
    items, _ = parse_array(text, m.end() - 1)
    return items


def read_list(text, name):
    m = re.search(r"\b(?:const|let|var)\s+" + name + r"\s*=\s*\[", text)
    if not m:
        raise ValueError(f"no list {name}")
    items, _ = parse_array(text, m.end() - 1)
    return items


def quote(value):
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def write_table(lines, table, plugin, names, places, indexes):
    lines.append("")
    lines.append(f"        internal static readonly PluginKeyTable {table} = new(")
    lines.append(f"            {quote(plugin)},")
    lines.append("            new[] { " + ", ".join(quote(n) for n in names) + " },")
    lines.append("            new[] { " + ", ".join(str(i) for i in indexes) + " },")
    lines.append("            new byte[] { " + ", ".join(str(p[0]) for p in places) + " },")
    lines.append("            new byte[] { " + ", ".join(str(p[1]) for p in places) + " });")


def generate(root):
    commit = subprocess.run(["git", "-C", root, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    lines = [
        "// Generated by tools/gen_plugin_key_tables.py. Do not edit.",
        "namespace PadForge.Common.Input.Peripherals.HidFamilies",
        "{",
        "    /// <summary>The key names, LED indexes and places SignalRGB's plugins",
        "    /// list for the keyboards OpenRGB has no controller for.</summary>",
        "    internal static class PluginKeyTables",
        "    {",
        f'        internal const string SignalPluginsCommit = "{commit}";',
    ]
    for table, plugin, names_list, places_list, indexes_list in TABLES:
        path = os.path.join(root, "Plugins", plugin)
        text = open(path, encoding="utf-8", errors="replace").read()
        names = read_list(text, names_list)
        for after, name in MISSING_NAMES.get(table, ()):
            names.insert(names.index(after) + 1, name)
        places = read_list(text, places_list)
        indexes = read_list(text, indexes_list)
        # A plugin's loop runs over its places, so the places set the count.
        count = len(places)
        if len(names) < count or len(indexes) < count:
            raise ValueError(f"{table}: {len(names)} names, {len(places)} places, {len(indexes)} indexes")
        write_table(lines, table, plugin, names[:count], places[:count], indexes[:count])
    for table, plugin, entry in LIBRARY_TABLES:
        text = open(os.path.join(root, "Plugins", plugin), encoding="utf-8", errors="replace").read()
        names = read_entry_list(text, entry, "ledNames")
        places = read_entry_list(text, entry, "ledPositions")
        indexes = read_entry_list(text, entry, "ledMap")
        for led, before in MOVED_LEDS.get(table, ()):
            indexes.remove(led)
            indexes.insert(indexes.index(before) + (1 if led == 168 else 0), led)
        if not len(names) == len(places) == len(indexes):
            raise ValueError(f"{table}: {len(names)} names, {len(places)} places, {len(indexes)} indexes")
        write_table(lines, table, plugin, names, places, indexes)
    lines.append("    }")
    lines.append("}")
    return "\r\n".join(lines) + "\r\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = generate(args.root)
    if args.check:
        current = open(OUT, encoding="utf-8", newline="").read() if os.path.exists(OUT) else ""
        if current != text:
            print("PluginKeyTables.g.cs differs")
            return 1
        print("PluginKeyTables.g.cs matches")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote", os.path.normpath(OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
