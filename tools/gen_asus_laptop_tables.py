#!/usr/bin/env python3
"""Writes HidFamilies/AsusLaptopTables.g.cs from asusctl's per-laptop data (#557).

    python tools/gen_asus_laptop_tables.py ASUSCTL_CLONE GHELPER_CLONE [--check]

The board list is asusctl's rog-aura/data/aura_support.ron in file order, because
asusctl walks it from the end and takes the first name the board name holds
(LedSupportFile::match_device, rog-aura/src/aura_detection.rs). The lights of a
per-key board come from its layout file: the keys it names and the place of each.

A key's LED numbers are not taken from asusctl alone. Its table leaves out the number
pad and the media keys and reuses the main row's codes on the number pad, and its
row 11 falls past its eleven packets (rgb_for_led_code,
rog-aura/src/keyboard/advanced.rs). G-Helper's packetMap (app/USB/Aura.cs:553-575 at
the pin below) numbers every LED, and OpenRGB's AsusAuraCoreLaptopDevices.cpp agrees
with it where it names the same key. The numbers below follow G-Helper. The script
reads packetMap and stops on a number here that packetMap lacks, apart from the
spacebar's LEDs, which it checks against asusctl's table, and when packetMap holds an
LED the default table leaves out.

The key at the right end of a row depends on the laptop: Home on one, Media Play on
another. Its LED goes by its row, as G-Helper's map has it.

The arrows depend on the laptop too, since the LED numbers follow the key's place in a
matrix 21 wide. OpenRGB's G614JZ has them at 120, 140, 141 and 142, its G814JV and
G814JIR at 121, 141, 142 and 143, and its G533ZM and G713RW, like asusctl's table, at
139, 159, 160 and 161. G-Helper's map lights the first and last sets, with 143 for the
right arrow, on every laptop. No list says which set a given board has, so a table
holds the set its layout's OpenRGB board has at the keys' places, then the rest of
G-Helper's arrow LEDs on a row of their own, each lit with its arrow. An LED the
layout's own key holds stays that key's: 120 is Right Shift on gx502 and gx531, and
142 is the key right of Up on g513i and gx502, as OpenRGB's G533ZW has Print Screen
there (line 128).
"""
import argparse
import os
import re
import subprocess
import sys

ASUSCTL = "321b1114a0ac3782d67aa39201682effb749f4d9"
GHELPER = "f62d584d24d4ea12ff902b873f9127edafbcd742"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                   "AsusLaptopTables.g.cs")

LETTERS_ROW2 = "QWERTYUIOP"
LETTERS_ROW3 = "ASDFGHJKL"
LETTERS_ROW4 = "ZXCVBNM"


def led_table():
    leds = {
        "VolDown": [2], "VolUp": [3], "MicMute": [4], "RogFan": [5], "RogApp": [6],
        "Esc": [21], "F1": [23], "F2": [24], "F3": [25], "F4": [26], "F5": [28], "F6": [29], "F7": [30], "F8": [31],
        "F9": [33], "F10": [34], "F11": [35], "F12": [36], "Del": [37, 38], "Pause": [39],
        "Tilde": [42], "N1": [43], "N2": [44], "N3": [45], "N4": [46], "N5": [47], "N6": [48], "N7": [49], "N8": [50],
        "N9": [51], "N0": [52], "Hyphen": [53], "Equals": [54],
        "Backspace": [55, 56, 57], "Backspace3_1": [55], "Backspace3_2": [56], "Backspace3_3": [57],
        "Tab": [63], "LBracket": [74], "RBracket": [75], "BackSlash": [76],
        "Caps": [84], "SemiColon": [94], "Quote": [95],
        "Return": [96, 97, 98, 99], "Return3_1": [96, 97], "Return3_2": [98], "Return3_3": [99],
        "LShift": [105, 106], "LShift3_1": [105], "LShift3_2": [106], "Comma": [114], "Period": [115], "FwdSlash": [116],
        "Rshift": [117, 118, 119], "Rshift3_1": [117, 118], "Rshift3_2": [119], "Rshift3_3": [120],
        "LCtrl": [126], "LFn": [127], "Meta": [128], "LAlt": [129],
        "Spacebar": [130, 131, 132, 133, 134], "Spacebar5_1": [130], "Spacebar5_2": [131], "Spacebar5_3": [132],
        "Spacebar5_4": [133], "Spacebar5_5": [134],
        "RAlt": [135], "PrtSc": [136], "RCtrl": [137],
        "NumLock": [59], "Star": [61], "NumPadPlus": [83, 104], "NumPadEnter": [125, 146], "NumPadDel": [145],
        "NumPadPause": [39], "NumPadPrtSc": [40], "NumPadHome": [41],
        "LightbarRight": [169], "LightbarRightCorner": [170], "LightbarRightBottom": [171],
        "LightbarLeftBottom": [172], "LightbarLeftCorner": [173], "LightbarLeft": [174],
        "LidLogo": [167], "LidLeft": [176], "LidRight": [177],
    }
    for i, letter in enumerate(LETTERS_ROW2):
        leds[letter] = [64 + i]
    for i, letter in enumerate(LETTERS_ROW3):
        leds[letter] = [85 + i]
    for i, letter in enumerate(LETTERS_ROW4):
        leds[letter] = [107 + i]
    return leds


LEDS = led_table()

# Up, Left, Down, Right by layout (OpenRGB's AsusAuraCoreLaptopDevices.cpp:226-231 and
# the 18-inch values at lines 42-56).
ARROW_CODES = ("Up", "Left", "Down", "Right")
ARROWS = {
    "g634j-per-key": (120, 140, 141, 142),
    "g635l-per-key": (120, 140, 141, 142),
    "g814ji-per-key": (121, 141, 142, 143),
}
ARROWS_ELSE = (139, 159, 160, 161)

# The arrow LEDs G-Helper's packetMap paints on every laptop.
GHELPER_ARROWS = (("Up", 139), ("Left", 159), ("Down", 160), ("Right", 161),
                  ("Up", 120), ("Left", 140), ("Down", 141), ("Right", 143))

# A code that comes after NumLock in its row is the number pad's key, since g733pz and
# g814ji list the number pad's "/" a few rows above the main one. A code that comes a
# second time in a layout is the number pad's key or the other key of that name at the
# end of a row.
SECOND = {
    "N7": [80], "N8": [81], "N9": [82], "N4": [101], "N5": [102], "N6": [103], "N1": [122], "N2": [123], "N3": [124],
    "N0": [144], "FwdSlash": [60], "Hyphen": [62], "PrtSc": [142],
}
SECOND_NAMES = {
    "N7": "Num 7", "N8": "Num 8", "N9": "Num 9", "N4": "Num 4", "N5": "Num 5", "N6": "Num 6", "N1": "Num 1", "N2": "Num 2",
    "N3": "Num 3", "N0": "Num 0", "FwdSlash": "Num /", "Hyphen": "Num -", "PrtSc": "Print Screen",
}

# The end of a row. The first key of the row names the row.
ROW_END = {"Tilde": 58, "Tab": 79, "Caps": 100, "LShift": 121, "LCtrl": 142}
END_CODES = {
    "Home": "Home", "PgUp": "Page Up", "PgDn": "Page Down", "End": "End", "RFn": "Fn",
    "MediaPlay": "MediaPlayPause", "MediaStop": "MediaStop", "MediaPrev": "MediaRewind", "MediaNext": "MediaFastForward",
}

# The two media keys go by their row, as their LEDs do. asusctl's files disagree: g513i,
# g634j and g635l list MediaNext in the Caps row and MediaPrev in the Shift row, and g533q
# the other way round. LED 100 is Previous and 121 Next on G-Helper (PRV15 and NXT15), on
# OpenRGB (AsusAuraCoreLaptopDevices.cpp:95 and 97) and on RGB.NET
# (AsusKeyboardLedMapping.cs:225-226).
MEDIA_BY_ROW = {"Caps": "MediaRewind", "LShift": "MediaFastForward"}

# rgb_for_led_code's numbers for the same keys.
ASUSCTL_END = {"Home": 58, "PgUp": 79, "PgDn": 100, "End": 121, "RFn": 142}

# The one value of packetMap its column comments do not name, which no table lights.
UNNAMED_IN_PACKET_MAP = {0, 138}

# G-Helper's KSTN column holds a 0, the mark of an empty place in packetMap. OpenRGB has the
# keystone at 175 on the G533ZW and the G733QR (AsusAuraCoreLaptopDevices.cpp:129 and 662).
OPENRGB_ONLY = {175}

# Lights OpenRGB's G533ZW has that asusctl's file for its layout leaves out
# (AsusAuraCoreLaptopDevices.cpp:128-129).
OPENRGB_BESIDE_LAYOUT = {"g533q-per-key": (("Print Screen", 142), ("Led Keystone", 175))}

NAMES = {
    "VolDown": "Volume Down", "VolUp": "Volume Up", "MicMute": "Mute Mic", "RogFan": "Fan", "RogApp": "ROG",
    "Esc": "Esc", "Del": "Del", "Pause": "Pause Break", "Tilde": "`", "Hyphen": "-_", "Equals": "=+",
    "Backspace": "Backspace", "Tab": "Tab", "LBracket": "[", "RBracket": "]", "BackSlash": "\\", "Caps": "CapsLock",
    "SemiColon": ";", "Quote": "'", "Return": "Enter", "LShift": "Left Shift", "Comma": ",", "Period": ".",
    "FwdSlash": "/", "Rshift": "Right Shift", "LCtrl": "Left Ctrl", "LFn": "Fn", "Meta": "Left Win", "LAlt": "Left Alt",
    "Spacebar": "Space", "RAlt": "Right Alt", "PrtSc": "Print Screen", "RCtrl": "Right Ctrl", "Up": "Up Arrow",
    "Left": "Left Arrow", "Down": "Down Arrow", "Right": "Right Arrow", "NumLock": "NumLock", "Star": "Num *",
    "NumPadPlus": "Num +", "NumPadEnter": "Num Enter", "NumPadDel": "Num .", "NumPadPause": "Pause Break",
    "NumPadPrtSc": "Print Screen", "NumPadHome": "Home",
    "LightbarRight": "Led Bar Right", "LightbarRightCorner": "Led Bar Right Corner", "LightbarRightBottom": "Led Bar Right Bottom",
    "LightbarLeftBottom": "Led Bar Left Bottom", "LightbarLeftCorner": "Led Bar Left Corner", "LightbarLeft": "Led Bar Left",
    "LidLogo": "Logo", "LidLeft": "Led Lid Left", "LidRight": "Led Lid Right",
}
for i in range(1, 13):
    NAMES[f"F{i}"] = f"F{i}"
for i in range(10):
    NAMES[f"N{i}"] = str(i)
for letter in LETTERS_ROW2 + LETTERS_ROW3 + LETTERS_ROW4:
    NAMES[letter] = letter
for stem in ("Backspace3", "Return3", "LShift3", "Rshift3", "Spacebar5"):
    base = {"Backspace3": "Backspace", "Return3": "Return", "LShift3": "LShift", "Rshift3": "Rshift", "Spacebar5": "Spacebar"}[stem]
    for n in range(1, 6):
        NAMES[f"{stem}_{n}"] = NAMES[base]

# Every LED G-Helper's packetMap names, row by row, for a per-key keyboard no list
# describes. (name, LED).
DEFAULT_ROWS = [
    [("Volume Down", 2), ("Volume Up", 3), ("Mute Mic", 4), ("Fan", 5), ("ROG", 6)],
    [("Esc", 21), ("F1", 23), ("F2", 24), ("F3", 25), ("F4", 26), ("F5", 28), ("F6", 29), ("F7", 30), ("F8", 31), ("F9", 33),
     ("F10", 34), ("F11", 35), ("F12", 36), ("Del", 37), ("Del", 38), ("Pause Break", 39), ("Print Screen", 40), ("Home", 41)],
    [("`", 42), ("1", 43), ("2", 44), ("3", 45), ("4", 46), ("5", 47), ("6", 48), ("7", 49), ("8", 50), ("9", 51), ("0", 52),
     ("-_", 53), ("=+", 54), ("Backspace", 55), ("Backspace", 56), ("Backspace", 57), ("MediaPlayPause", 58), ("NumLock", 59),
     ("Num /", 60), ("Num *", 61), ("Num -", 62)],
    [("Tab", 63), ("Q", 64), ("W", 65), ("E", 66), ("R", 67), ("T", 68), ("Y", 69), ("U", 70), ("I", 71), ("O", 72), ("P", 73),
     ("[", 74), ("]", 75), ("\\", 76), ("MediaStop", 79), ("Num 7", 80), ("Num 8", 81), ("Num 9", 82), ("Num +", 83)],
    [("CapsLock", 84), ("A", 85), ("S", 86), ("D", 87), ("F", 88), ("G", 89), ("H", 90), ("J", 91), ("K", 92), ("L", 93),
     (";", 94), ("'", 95), ("Enter", 96), ("Enter", 97), ("Enter", 98), ("Enter", 99), ("MediaRewind", 100), ("Num 4", 101),
     ("Num 5", 102), ("Num 6", 103), ("Num +", 104)],
    [("Left Shift", 105), ("Left Shift", 106), ("Z", 107), ("X", 108), ("C", 109), ("V", 110), ("B", 111), ("N", 112), ("M", 113),
     (",", 114), (".", 115), ("/", 116), ("Right Shift", 117), ("Right Shift", 118), ("Right Shift", 119), ("Up Arrow", 139),
     ("Up Arrow", 120), ("MediaFastForward", 121), ("Num 1", 122), ("Num 2", 123), ("Num 3", 124), ("Num Enter", 125)],
    [("Left Ctrl", 126), ("Fn", 127), ("Left Win", 128), ("Left Alt", 129), ("Space", 131), ("Right Alt", 135),
     ("Print Screen", 136), ("Right Ctrl", 137), ("Left Arrow", 159), ("Down Arrow", 160), ("Right Arrow", 161),
     ("Left Arrow", 140), ("Down Arrow", 141), ("Print Screen", 142), ("Right Arrow", 143), ("Num 0", 144), ("Num .", 145),
     ("Num Enter", 146)],
    [("Led Bar Left", 174), ("Led Bar Left Corner", 173), ("Led Bar Left Bottom", 172), ("Led Bar Right Bottom", 171),
     ("Led Bar Right Corner", 170), ("Led Bar Right", 169)],
    [("Logo", 167), ("Led Lid Left", 176), ("Led Lid Right", 177), ("Led Keystone", 175)],
]


def git_show(clone, path, commit=ASUSCTL):
    done = subprocess.run(["git", "-C", clone, "show", f"{commit}:{path}"], capture_output=True)
    if done.returncode != 0:
        sys.exit(f"git show failed for {path}: {done.stderr.decode(errors='replace')}")
    return done.stdout.decode("utf-8")


def git_files(clone, folder):
    done = subprocess.run(["git", "-C", clone, "ls-tree", "--name-only", f"{ASUSCTL}:{folder}"], capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(done.stderr)
    return done.stdout.split()


def quote(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def packet_map(clone):
    """The LED numbers of G-Helper's packetMap."""
    text = git_show(clone, "app/USB/Aura.cs", GHELPER)
    body = re.search(r"static byte\[\] packetMap = new byte\[\]\s*\{(.*?)\};", text, re.S)
    if not body:
        sys.exit("no packetMap in G-Helper's app/USB/Aura.cs")
    return {int(n) for n in re.findall(r"\d+", re.sub(r"/\*.*?\*/", "", body.group(1), flags=re.S))}


def spacebar_leds(clone):
    """The spacebar's LEDs by asusctl's rgb_for_led_code: a code's (packet, byte) is LED
    16 times the packet, plus the byte less 9 in threes. packetMap holds only 131."""
    text = git_show(clone, "rog-aura/src/keyboard/advanced.rs")
    found = {f"Spacebar5_{n}": 16 * int(packet) + (int(byte) - 9) // 3
             for n, packet, byte in re.findall(r"LedCode::Spacebar5_(\d) => \((\d+), (\d+)\)", text)}
    for code, led in found.items():
        if LEDS[code] != [led]:
            sys.exit(f"{code} is LED {led} in asusctl's table and {LEDS[code]} here")
    if len(found) != 5:
        sys.exit(f"read {len(found)} spacebar LEDs of 5 from asusctl's table")
    return set(found.values())


def check_numbers(asusctl, ghelper):
    """Stops on a hand-written LED number G-Helper's packetMap lacks, and on an LED of
    packetMap the default table leaves out."""
    known = packet_map(ghelper)
    written = {led for leds in LEDS.values() for led in leds}
    for numbers in list(ARROWS.values()) + [ARROWS_ELSE, ROW_END.values(), ASUSCTL_END.values()]:
        written.update(numbers)
    written.update(led for _, led in GHELPER_ARROWS)
    written.update(led for leds in SECOND.values() for led in leds)
    default = {led for row in DEFAULT_ROWS for _, led in row}
    missing = sorted((written | default) - known - spacebar_leds(asusctl) - OPENRGB_ONLY)
    if missing:
        sys.exit(f"LEDs written here that G-Helper's packetMap lacks: {missing}")
    left_out = sorted(known - default - UNNAMED_IN_PACKET_MAP)
    if left_out:
        sys.exit(f"LEDs of G-Helper's packetMap the default table leaves out: {left_out}")


def read_boards(clone):
    text = git_show(clone, "rog-aura/data/aura_support.ron")
    rows = re.findall(
        r'device_name: "([^"]*)",\s*product_id: "([^"]*)",\s*layout_name: "([^"]*)",\s*basic_modes: \[[^\]]*\],'
        r'\s*basic_zones: \[([^\]]*)\],\s*advanced_type: ([^\n]*),\s*power_zones: \[[^\]]*\]', text)
    if len(rows) != text.count("device_name:"):
        sys.exit(f"read {len(rows)} boards of {text.count('device_name:')}")
    boards = []
    for name, product, layout, zones, advanced in rows:
        boards.append((name, product, layout, advanced.startswith("PerKey"), "Key1" in zones))
    return boards


def read_layout(clone, name):
    """The lights of a layout: (light name, LED, x, y) in row order."""
    text = git_show(clone, f"rog-aura/data/layouts/{name}_US.ron")
    widths = {}
    for shape, kind, width in re.findall(r'"(\w+)":\s*(\w+)\(\s*width: ([\d.]+)', text):
        widths[shape] = float(width)
    lights = []
    arrows = dict(zip(ARROW_CODES, ARROWS.get(name, ARROWS_ELSE)))
    used = set(arrows.values())
    seen = {}
    rows = re.split(r"\brow: \[", text)[1:]
    for y, row in enumerate(rows):
        items = re.findall(r'\(\s*(\w+),\s*"(\w+)"\s*\)', row[:row.index("]")])
        first = next((code for code, _ in items if code not in ("Spacing", "Blocking")), None)
        x = 0.0
        cell = -1
        after_num_lock = False
        for code, shape in items:
            width = widths[shape]
            at = x
            x += width
            if code in ("Spacing", "Blocking"):
                continue
            on_number_pad = after_num_lock and code in SECOND
            after_num_lock = after_num_lock or code == "NumLock"
            count = seen.get(code, 0)
            if not on_number_pad:
                seen[code] = count + 1
            if code in arrows:
                place = max(cell + 1, round(at))
                cell = place
                lights.append((NAMES[code], arrows[code], place, y))
                continue
            if code in END_CODES:
                # A key away from the row's end keeps the LED asusctl gives it.
                if first in ROW_END:
                    leds = [ROW_END[first]]
                elif code in ASUSCTL_END:
                    leds = [ASUSCTL_END[code]]
                else:
                    sys.exit(f"{name}: {code} in a row that starts with {first}")
                light = END_CODES[code]
                if code in ("MediaPrev", "MediaNext"):
                    if first not in MEDIA_BY_ROW:
                        sys.exit(f"{name}: {code} in a row that starts with {first}")
                    light = MEDIA_BY_ROW[first]
            elif on_number_pad or count > 0:
                if code not in SECOND:
                    continue
                leds = SECOND[code]
                light = SECOND_NAMES[code]
            elif code in LEDS:
                leds = LEDS[code]
                light = NAMES[code]
            else:
                sys.exit(f"{name}: no LED for {code}")
            named = [(light, led) for led in leds]
            # asusctl names one light on the lid, and G-Helper's map writes the logo and
            # the lid's two sides.
            if code == "LidLogo":
                named += [(NAMES[side], LEDS[side][0]) for side in ("LidLeft", "LidRight")]
            for i, (light, led) in enumerate(named):
                if led in used:
                    continue
                used.add(led)
                place = max(cell + 1, round(at + i * width / len(named)))
                cell = place
                lights.append((light, led, place, y))
    spare = [(code, led) for code, led in GHELPER_ARROWS if led not in used]
    for place, (code, led) in enumerate(spare):
        lights.append((NAMES[code], led, place, len(rows)))
    for place, (light, led) in enumerate(OPENRGB_BESIDE_LAYOUT.get(name, ()), len(spare)):
        if led not in used:
            lights.append((light, led, place, len(rows)))
    return lights


def default_lights():
    lights = []
    for y, row in enumerate(DEFAULT_ROWS):
        for x, (name, led) in enumerate(row):
            lights.append((name, led, x, y))
    return lights


def second_named(lights):
    """A second LED under a key lights with its key (PluginKeyNames.Lights)."""
    named = []
    seen = set()
    for name, led, x, y in lights:
        named.append(("2nd " + name if name in seen else name, led, x, y))
        seen.add(name)
    return named


def method_name(layout):
    return "Layout_" + re.sub(r"\W", "_", layout)


def write_table(lines, method, source, lights):
    lights = second_named(lights)
    lines.append(f"        private static PluginKeyTable {method}() => new(")
    lines.append(f"            {quote(source)},")
    lines.append("            new[] { " + ", ".join(quote(n) for n, _, _, _ in lights) + " },")
    lines.append("            new[] { " + ", ".join(str(led) for _, led, _, _ in lights) + " },")
    lines.append("            new byte[] { " + ", ".join(str(x) for _, _, x, _ in lights) + " },")
    lines.append("            new byte[] { " + ", ".join(str(y) for _, _, _, y in lights) + " });")
    lines.append("")


def generate(clone, ghelper):
    check_numbers(clone, ghelper)
    boards = read_boards(clone)
    per_key = sorted(set(b[2] for b in boards if b[3]))
    on_disk = {f[:-len("_US.ron")] for f in git_files(clone, "rog-aura/data/layouts") if f.endswith("_US.ron")}
    missing = [layout for layout in per_key if layout not in on_disk]
    if missing:
        sys.exit(f"no layout file for {missing}")
    lines = [
        "// Generated by tools/gen_asus_laptop_tables.py. Do not edit.",
        "namespace PadForge.Common.Input.Peripherals.HidFamilies",
        "{",
        "    /// <summary>The laptops asusctl lists, and the lights of its per-key",
        "    /// layouts at the LED numbers G-Helper and OpenRGB use.</summary>",
        "    internal static class AsusLaptopTables",
        "    {",
        f"        internal const string AsusctlCommit = {quote(ASUSCTL)};",
        f"        internal const string GHelperCommit = {quote(GHELPER)};",
        "",
        "        /// <summary>aura_support.ron in file order. asusctl walks it from the end.</summary>",
        "        internal static readonly AsusLaptopBoard[] Boards =",
        "        {",
    ]
    for name, product, layout, key, zones in boards:
        lines.append(f"            new({quote(name)}, {quote(product)}, {quote(layout)}, {'true' if key else 'false'}, {'true' if zones else 'false'}),")
    lines += ["        };", ""]
    lines.append("        /// <summary>The lights of a per-key layout, or null.</summary>")
    lines.append("        internal static PluginKeyTable Layout(string name) => name switch")
    lines.append("        {")
    for layout in per_key:
        lines.append(f"            {quote(layout)} => {method_name(layout)}(),")
    lines.append("            _ => null,")
    lines.append("        };")
    lines.append("")
    lines.append("        /// <summary>Every LED G-Helper's packetMap names, for a per-key keyboard no list describes.</summary>")
    lines.append("        internal static PluginKeyTable Default() => Layout_default();")
    lines.append("")
    for layout in per_key:
        write_table(lines, method_name(layout), f"rog-aura/data/layouts/{layout}_US.ron", read_layout(clone, layout))
    write_table(lines, "Layout_default", "g-helper app/USB/Aura.cs", default_lights())
    while lines[-1] == "":
        lines.pop()
    lines += ["    }", "}", ""]
    return "\r\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("asusctl")
    parser.add_argument("ghelper")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = generate(args.asusctl, args.ghelper)
    if args.check:
        if not os.path.exists(OUT) or open(OUT, encoding="utf-8", newline="").read() != text:
            print("AsusLaptopTables.g.cs differs")
            return 1
        print("the laptop tables match")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote", os.path.normpath(OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
