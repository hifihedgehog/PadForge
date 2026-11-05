"""The key tables of the keyboards PadForge lights from SignalRGB's plugins.

OpenRGB is PadForge's source for a device wherever it has one. It has no
controller for these keyboards, and two sets of plugins drive them:

- signal-plugins (gitlab.com/signalrgb/signal-plugins), read at its beta branch,
  where the work goes on. Its master stopped in 2023, and a plugin beta no longer
  has is read at master's last commit. The repository has no license file.
- qmk-plugins (github.com/SRGBmods/qmk-plugins), GPL-2.0, which took over the QMK
  boards from signal-plugins in November 2023 and holds every board flashed with
  the SignalRGB protocol.

Each plugin lists a keyboard's key names, their places and the LED index each key
takes. This script reads those three lists, as text, at pinned commits, and writes
them to PadForge.App/Common/Input/Peripherals/HidFamilies/PluginKeyTables.g.cs and
PluginQmkTables.g.cs, with every product each plugin names in
PadForge.Tests/HidFamilies/PluginInventory.g.cs. It takes the facts and no code.

A plugin's names are labels nothing in SignalRGB checks, and many run off their
LEDs. The SignalRGB protocol numbers a board's LEDs as its QMK firmware does, so a
board whose firmware is in qmk_firmware or Keychron's fork takes its keys from
there: g_led_config gives each LED's matrix cell, keyboard.json each cell's place in
the layout, and the default keymap each place's keycode (FirmwareTree, below). A
board with no firmware there keeps its plugin's lists.

Release prep fetches the clones, moves the pins to the branch heads and reruns
this. PluginInventoryTests then fails by name on any product a family does not
light, and the --check run says what changed.

A form of plugin or firmware data this script does not read stops the run with a
message: a ProductId that yields no product, a library entry whose names, places
and LEDs differ in length, a Corsair product with no entry, a firmware config that
does not parse. Each exception is listed beside the code with the reason.

Usage:
    python tools/gen_plugin_key_tables.py SIGNAL_PLUGINS_CLONE QMK_PLUGINS_CLONE KEYCHRON_FIRMWARE_CLONE QMK_FIRMWARE_CLONE
    python tools/gen_plugin_key_tables.py SIGNAL_PLUGINS_CLONE QMK_PLUGINS_CLONE KEYCHRON_FIRMWARE_CLONE QMK_FIRMWARE_CLONE --check   (compare, write nothing)
"""
import argparse
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "PluginKeyTables.g.cs")
QMK_OUT = os.path.join(HERE, "..", "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "PluginQmkTables.g.cs")
INVENTORY = os.path.join(HERE, "..", "PadForge.Tests", "HidFamilies", "PluginInventory.g.cs")

SIGNAL_BETA = "9511979fa21a0a00a35a236d0aac8c438179094f"
SIGNAL_MASTER = "95031635526054e267124fb32cf04509375f34ea"

# Entries a plugin keeps in a block comment, with the reason its own comment gives. They
# go in the inventory, where PluginInventoryTests leaves them out by name. The run stops
# when the comment no longer says so. (plugin, product, the comment's words).
COMMENTED_OUT_PRODUCTS = [
    ("Plugins/Corsair/Modern_Corsair/Corsair_Headset_Controller.js", 0x0A5B, "doesnt control the headset"),
    ("Plugins/Corsair/Modern_Corsair/Corsair_Headset_Controller.js", 0x0A6A, "doesnt control the headset"),
]

# Products a plugin names only on a branch beta has not taken up.
SIGNAL_BRANCH_FILES = [
    ("de763e2a07a72458e1a39ac62cc6dd9ca47b9580", "Plugins/AOC/AOC_Keyboard_Controller.js"),
    ("565aff41dd0fdc211a6272740e6c5af955834629", "Plugins/Corsair/Modern_Corsair/Corsair_Bragi_Device.js"),
    ("d548f75504362996a20cca0be23450e0da9cdfe5", "Plugins/Steelseries/Steelseries_Keyboard_Laptop_Controller.js"),
    ("d2ee024bd6496808724bf2efccbc1c31698436d5", "Plugins/Thermal Take/Thermaltake_Dasher_RGB_Mousepad.js"),
]


def git_show(clone, commit, path):
    return subprocess.run(["git", "-C", clone, "show", f"{commit}:{path}"], capture_output=True, check=True).stdout.decode("utf-8", "replace")


def git_files(clone, commit):
    return subprocess.run(["git", "-C", clone, "ls-tree", "-r", "--name-only", commit], capture_output=True, check=True).stdout.decode("utf-8", "replace").splitlines()

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
    ("ApexM750Tkl", "Steelseries/Steelseries_Apex_M750_TKL_Keyboard.js", "vLedNames", "vLedPositions", "vKeymap"),
    ("ApexProMini", "Steelseries/Steelseries_Apex_Pro_Mini_Keyboard.js", "vLedNames", "vLedPositions", "vKeymap"),
    ("Ck720", "CoolerMaster/CoolerMaster_CK720_Keyboard.js", "vLedNames", "vLedPositions", "vLeds"),
    ("Ck721", "CoolerMaster/CoolerMaster_CK721_Keyboard.js", "vLedNames", "vLedPositions", "vLeds"),
    ("VulcanIIMiniAir", "Turtle Beach/Turtle_Beach_Vulcan_II_Mini_Air_Keyboard.js", "vLedsNames", "vLedsPositions", "vLeds"),
    ("Level20Desk", "Thermal Take/ThermalTake_Level20_Desk.js", "vLedNames", "vLedPositions", "vLeds"),
]

# The plugins beta no longer has, read at master's last commit.
READ_AT_MASTER = {"K65Mini", "Strafe"}

# Keyboards a plugin keeps in a library object: the table's name, the plugin and
# the entry, whose ledNames, ledPositions and ledMap are the three lists.
LIBRARY_TABLES = [
]

# Plugins whose name list runs out of step with their places, and what puts it back.
# DZTech 65RGB V3: an ISO key an ANSI board does not have.
# Q5 and V5 ISO with a knob: a Num + the plugin lists no LED for.
QMK_EXTRA_NAMES = {
    "DZTech/DZTech-65RGB-V3_Keyboard.js": ("ISO_<",),
    "Keychron/Q Series/Standard/Keychron_Q5_QMK_ISO_Encoder_Keyboard.js": ("Num +",),
    "Keychron/V Series/Standard/Keychron_V5_QMK_ISO_Encoder_Keyboard.js": ("Num +",),
}

# The Q10 is an Alice board with a second B beside the first, which its list of
# places holds. The V3 Max with a knob leaves out the bottom row's right Win and
# right Ctrl, which its places hold, as every other V3 lists them.
QMK_MISSING_NAMES = {
    "Keychron/Q Series/Standard/Keychron_Q10_QMK_ANSI_Keyboard.js": (("B", "B"),),
    "Keychron/V Series/Max/Keychron_V3_Max_QMK_ANSI_Encoder_Keyboard.js": (("Right Alt", "Right Win"), ("Fn", "Right Ctrl")),
}

# The K8 v2 TKL ISO's plugin leaves Enter out of the Tab row, where its LED is, as Keychron's other
# ISO plugins do, and lists it with the home row, so every name between runs one key off: CapsLock
# on Page Down's LED and A on CapsLock's. The name before Enter is the ISO # key. The boards with
# firmware here take their keys from it.
QMK_ISO_ENTER_ON_THE_TAB_ROW = {
    "Keychron/K Series/Standard/Keychron_K8_TKL_Version_2_QMK_ISO_Keyboard.js",
}

# Names a plugin writes wrong, by LED. The MCHOSE's left Ctrl is named B.
QMK_NAME_FIXES = {
    "MCHOSE/MCHOSE_GX87_QMK_ANSI_Keyboard.js": {77: "Left Ctrl"},
}

# A place the plugin's list leaves out, after the place named, and one it has too many: the French
# Berserker's names run a key off its places from F12 to the right shift.
QMK_PLACE_LIST_FIXES = {
    "DesignedByGG/GG_QMK_ISO_Keyboard_TKL_AZERTY_French.js": {"insert_after": ([12, 1], [13, 1]), "remove": [12, 5]},
}

# Places a plugin writes wrong, by list position: two keys on one place.
QMK_PLACE_FIXES = {
    "Keychron/K Series/Max/Keychron_K10_v2_QMK_ANSI_Keyboard.js": {"/": [11, 4]},
    "Keychron/K Series/Standard/Keychron_K8_TKL_QMK_ISO_Keyboard.js": {"F7": [8, 0], "F8": [9, 0], "F9": [10, 0], "F10": [11, 0], "F11": [12, 0]},
}

# Plugins whose places are not the board's: the tool that wrote them folds rows into one another.
# The key picker draws these from their usages on its own layout.
QMK_NO_PLACES = {
    "_Random-Brands/doio_doiokb38.js",
}

# Plugins that pick a board's lists when they run, by product, or by the third byte
# of the firmware's unique identifier (None: whatever else it answers). The C1 Pro's
# product test names the K1 Pro's product and would always pick ISO. The file is its
# ANSI plugin, and 0x0510 is the ANSI board.
QMK_LAYOUTS = {
    "GMMK/GMMK_Pro_QMK_Keyboard.js": {"asked": ((None, "ProAnsi"), (6, "ProIso"))},
    "GMMK/GMMK_3_QMK_Keyboard.js": {"asked": ((11, "ANSIp65"), (12, "ISOp65"), (13, "ANSIp75"), (14, "ISOp75"), (15, "ANSIp100"), (16, "ISOp100"))},
    "Keychron/C Series/Pro/Keychron_C1_Pro_QMK_ANSI_Keyboard.js": {0x0510: "ANSI"},
    "Keychron/K Series/Max/Keychron_K1_Max_QMK_Keyboard.js": {0x0A10: "ANSI", 0x0A11: "ISO"},
    "Keychron/K Series/Pro/Keychron_K1_Pro_QMK_Keyboard.js": {0x0210: "ANSI", 0x0211: "ISO"},
    "Keychron/Q Series/HE/Keychron_Q6_HE_QMK_ANSI_ISO_Keyboard.js": {0x0B60: "ANSI", 0x0B61: "ISO"},
    "Keychron/Q Series/Standard/Keychron_Q6_QMK_ANSI_ISO_Keyboard.js": {0x0160: "ANSI", 0x0161: "ANSI", 0x0162: "ISO", 0x0163: "ISO"},
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


def generate(signal):
    lines = [
        "// Generated by tools/gen_plugin_key_tables.py. Do not edit.",
        "namespace PadForge.Common.Input.Peripherals.HidFamilies",
        "{",
        "    /// <summary>The key names, LED indexes and places SignalRGB's plugins",
        "    /// list for the keyboards OpenRGB has no controller for.</summary>",
        "    internal static class PluginKeyTables",
        "    {",
        f'        internal const string SignalPluginsCommit = "{SIGNAL_BETA}";',
    ]
    for table, plugin, names_list, places_list, indexes_list in TABLES:
        commit = SIGNAL_MASTER if table in READ_AT_MASTER else SIGNAL_BETA
        text = git_show(signal, commit, "Plugins/" + plugin)
        names = read_list(text, names_list)
        places = read_list(text, places_list)
        indexes = read_list(text, indexes_list)
        # A plugin's loop runs over its places, so the places set the count.
        count = len(places)
        if len(names) < count or len(indexes) < count:
            raise ValueError(f"{table}: {len(names)} names, {len(places)} places, {len(indexes)} indexes")
        write_table(lines, table, plugin, names[:count], places[:count], indexes[:count])
    for table, plugin, entry in LIBRARY_TABLES:
        text = git_show(signal, SIGNAL_BETA, "Plugins/" + plugin)
        names = read_entry_list(text, entry, "ledNames")
        places = read_entry_list(text, entry, "ledPositions")
        indexes = read_entry_list(text, entry, "ledMap")
        if not len(names) == len(places) == len(indexes):
            raise ValueError(f"{table}: {len(names)} names, {len(places)} places, {len(indexes)} indexes")
        write_table(lines, table, plugin, names, places, indexes)
    lines.append("    }")
    lines.append("}")
    return "\r\n".join(lines) + "\r\n"


def without_comments(text):
    """The text with its comments gone and its strings left whole, so a product
    a plugin has commented out is not read as one."""
    kept = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c in "\"'`":
            j = i + 1
            while j < n and text[j] != c:
                if text[j] == "\\":
                    j += 1
                j += 1
            kept.append(text[i : j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        else:
            kept.append(c)
            i += 1
    return "".join(kept)


def object_keys(text, brace):
    """The number keys at the top of the object whose brace is at this index."""
    number = r"0[xX][0-9a-fA-F]+|\d+"
    keys = []
    depth = 0
    i = brace
    n = len(text)
    while i < n:
        c = text[i]
        if c in "\"'`":
            j = i + 1
            while j < n and text[j] != c:
                if text[j] == "\\":
                    j += 1
                j += 1
            if depth == 1 and re.fullmatch(number, text[i + 1 : j]) and re.match(r"\s*:", text[j + 1 :]):
                keys.append(int(text[i + 1 : j], 0))
            i = j + 1
            continue
        if c in "{[(":
            depth += 1
        elif c in "}])":
            depth -= 1
            if depth == 0:
                return keys
        elif depth == 1:
            m = re.match(r"(" + number + r")\s*:", text[i:])
            if m and not re.match(r"[\w.]", text[i - 1]):
                keys.append(int(m.group(1), 0))
                i += m.end()
                continue
        i += 1
    return keys


# Plugins whose ProductId returns names that are not USB products. The MSI Mystic Light
# plugins return the keys of a library of motherboard names, so they name no product.
NAMES_NO_USB_PRODUCT = {
    "Plugins/Msi/185 Byte Motherboard Controller/MSI_185Byte_Controller.js",
    "Plugins/Msi/185 Byte Motherboard Controller/MSI_185Byte_Controller_0x0DB0.js",
}


def products_of(text, path):
    """What a plugin's ProductId returns: a number, a list, or the keys of an
    object it names, which may be a table, a field or a method's result. A plugin
    with a ProductId that yields nothing stops the run, so a form this does not
    read cannot leave a plugin out of the tables and the coverage test."""
    found = products_in(text)
    if not found and re.search(r"function\s+ProductId\s*\(", text) and path not in NAMES_NO_USB_PRODUCT:
        raise SystemExit(f"{path}: ProductId names no product this script reads")
    return found


def products_in(text):
    number = r"0[xX][0-9a-fA-F]+|\d+"
    plain = re.search(r"function\s+ProductId\s*\(\s*\)\s*\{\s*return\s*(\[[^\]]*\]|" + number + ")", text, re.S)
    if plain:
        return [int(p, 0) for p in re.findall(number, plain.group(1))]
    keyed = re.search(r"function\s+ProductId\s*\(\s*\)\s*\{\s*return\s*Object\.keys\(\s*([\w.]+)(\(\s*\))?\s*\)", text)
    if keyed:
        name = keyed.group(1).split(".")[-1]
        # A method may hold a development list under an if, ahead of the list it returns
        # otherwise (Corsair_Bragi_Device.js at signal-plugins master). The first is the larger.
        pattern = r"\b" + re.escape(name) + r"\s*(?:\(\s*\)\s*\{\s*(?:if\s*\([^)]*\)\s*\{\s*)?return\s*(?:Object\.freeze\(\s*)?|[:=]\s*(?:Object\.freeze\(\s*)?)\{"
        found = re.search(pattern, text)
        if found:
            return object_keys(text, found.end() - 1)
        return []
    named = re.search(r"function\s+ProductId\s*\(\s*\)\s*\{\s*return\s*([\w.]+)", text)
    if named:
        listed = re.search(r"\b" + re.escape(named.group(1).split(".")[-1]) + r"\s*=\s*\[([^\]]*)\]", text, re.S)
        if listed:
            return [int(p, 0) for p in re.findall(number, listed.group(1))]
    return []



# ---------------------------------------------------------------------------
# The QMK boards: every plugin of github.com/SRGBmods/qmk-plugins and the QMK
# folder signal-plugins kept until 935142ad, which all speak one protocol.
# ---------------------------------------------------------------------------

QMK_MAIN = "08c909a574c977969be1b51e6a9d4585e08fdc39"

# The branches, best first. A product takes the first plugin that names it, so
# main outranks the staging branches, and the boards awaiting testing come last.
QMK_BRANCHES = [
    QMK_MAIN,
    "39f66abdde4320dec8c46643176971baee278697",  # nuphy-srgb
    "bbf87eb60b7fde179e11219ad78e4847ea68f093",  # redragon
    "c5a3d3f37c1053dae072bb86591fb495c55b86d7",  # redragon-k630
    "10adccaea89022408181627e6ae9a80476f9c178",  # nerdytec-staging
    "5d0877672bb645f351bb6d1349a6c01f5e790ee8",  # sonix-gmmk
    "06837c839cad18f36e5c10bb3d9bc0e80ae9fb35",  # sonixqmk-gmmk
    "d58bca3df00047df5ff893d928ee3d7dc832b651",  # needs-testing
]

# The names a key takes as it stands, whatever its place.
KEY_SYMBOLS = {
    "`": 0x35, "~": 0x35, "§": 0x35, "²": 0x35, "-": 0x2D, "_": 0x2D, "-_": 0x2D, "ß": 0x2D, "=": 0x2E, "+": 0x2E,
    "=+": 0x2E, "+=": 0x2E, "´": 0x2E, "[": 0x2F, "{": 0x2F, "[{": 0x2F, "Ü": 0x2F, "Å": 0x2F, "]": 0x30, "}": 0x30,
    "]}": 0x30, "¨": 0x30, "\\": 0x31, "|": 0x31, "\\|": 0x31, "\\\\": 0x31, ";": 0x33, ":": 0x33, ";:": 0x33,
    "Ö": 0x33, "Ø": 0x33, "'": 0x34, '"': 0x34, "Ä": 0x34, "Æ": 0x34, "‘": 0x34, ",": 0x36, "<": 0x36, ",<": 0x36,
    ".": 0x37, ">": 0x37, ".>": 0x37, "/": 0x38, "?": 0x38, "/?": 0x38, "!": 0x1E, "@": 0x1F, "#": 0x20, "$": 0x21,
    "%": 0x22, "^": 0x23, "&": 0x24, "*": 0x25, "(": 0x26, ")": 0x27, "<>": 0x64, "<>|": 0x64, ">|": 0x64,
}

KEY_WORDS = {
    "esc": 0x29, "escape": 0x29, "ese": 0x29, "tab": 0x2B, "caps": 0x39, "capslock": 0x39, "enter": 0x28, "return": 0x28,
    "ent": 0x28, "backspace": 0x2A, "bksp": 0x2A, "bspc": 0x2A, "bs": 0x2A, "space": 0x2C, "spacebar": 0x2C, "spc": 0x2C,
    "leftspace": 0x2C, "rightspace": 0x2C, "lspace": 0x2C, "rspace": 0x2C, "del": 0x4C, "delete": 0x4C, "entf": 0x4C,
    "ins": 0x49, "insert": 0x49, "home": 0x4A, "pos1": 0x4A, "enm": 0x4A, "end": 0x4D, "pageup": 0x4B, "pgup": 0x4B,
    "pagedown": 0x4E, "pgdn": 0x4E, "pagedn": 0x4E, "pgdown": 0x4E, "printscreen": 0x46, "prtsc": 0x46, "print": 0x46,
    "screenshot": 0x46, "pscr": 0x46, "snap": 0x46, "scrolllock": 0x47, "scrlk": 0x47, "scrl": 0x47, "scroll": 0x47,
    "scroll-lock": 0x47, "pause": 0x48, "pausebreak": 0x48, "paus": 0x48, "up": 0x52, "uparrow": 0x52, "upparrow": 0x52,
    "upwardsarrow": 0x52, "down": 0x51, "downarrow": 0x51, "downwardsarrow": 0x51, "dn": 0x51, "left": 0x50,
    "leftarrow": 0x50, "leftwardsarrow": 0x50, "lf": 0x50, "right": 0x4F, "rightarrow": 0x4F, "rightarror": 0x4F,
    "rightwardsarrow": 0x4F, "rght": 0x4F, "rt": 0x4F, "lshift": 0xE1, "leftshift": 0xE1, "lsft": 0xE1, "rshift": 0xE5,
    "rightshift": 0xE5, "rsft": 0xE5, "r_shift": 0xE5, "lctrl": 0xE0, "leftctrl": 0xE0, "lctl": 0xE0, "leftcontrol": 0xE0,
    "rctrl": 0xE4, "rightctrl": 0xE4, "rctl": 0xE4, "rightcontrol": 0xE4, "lalt": 0xE2, "leftalt": 0xE2, "ralt": 0xE6,
    "rightalt": 0xE6, "altgr": 0xE6, "lwin": 0xE3, "leftwin": 0xE3, "lgui": 0xE3, "rwin": 0xE7, "rightwin": 0xE7,
    "rgui": 0xE7, "menu": 0x65, "contextmenu": 0x65, "app": 0x65, "backslash": 0x31, "bsls": 0x31, "comm": 0x36,
    "comma": 0x36, "dot": 0x37, "period": 0x37, "forwardslash": 0x38, "slsh": 0x38, "semicolon": 0x33, "scln": 0x33,
    "quot": 0x34, "singletick": 0x34, "grv": 0x35, "backtick": 0x35, "mins": 0x2D, "minus": 0x2D, "eql": 0x2E,
    "equals": 0x2E, "lbrc": 0x2F, "leftbracket": 0x2F, "rbrc": 0x30, "rightbracket": 0x30, "nubs": 0x64, "nuhs": 0x32,
    "iso": 0x64, "iso#": 0x32, "iso/": 0x32, "#iso": 0x32, "iso<": 0x64, "<iso": 0x64, "numlock": 0x53, "num": 0x53,
    "num/": 0x54, "kp/": 0x54, "psls": 0x54, "num*": 0x55, "kp*": 0x55, "past": 0x55, "num-": 0x56, "kp-": 0x56,
    "pmns": 0x56, "num+": 0x57, "kp+": 0x57, "ppls": 0x57, "numenter": 0x58, "kpenter": 0x58, "pent": 0x58, "num.": 0x63,
    "kp.": 0x63, "pdot": 0x63, "num,": 0x63, "num=": 0x67, "latincapitalletteruwithdiaeresis": 0x2F,
    "latincapitalletterowithdiaeresis": 0x33, "latincapitalletterawithdiaeresis": 0x34,
}
for _i in range(1, 10):
    KEY_WORDS[f"num{_i}"] = 0x58 + _i
    KEY_WORDS[f"kp{_i}"] = 0x58 + _i
KEY_WORDS["num0"] = 0x62
KEY_WORDS["kp0"] = 0x62
for _i in range(1, 13):
    KEY_WORDS[f"f{_i}"] = 0x39 + _i
for _i in range(13, 25):
    KEY_WORDS[f"f{_i}"] = 0x68 + _i - 13

# A key lit by several LEDs names each: "Enter L", "Cntr BKSP", "Left Shift R 2".
PART_OF_A_KEY = re.compile(r"\s+(?:l|r|middle|lower|upper|l \d|r \d)$", re.I)
SPACE_PART = re.compile(r"^space \d$", re.I)
SIDES = {"ctrl": (0xE0, 0xE4), "control": (0xE0, 0xE4), "shift": (0xE1, 0xE5), "alt": (0xE2, 0xE6), "win": (0xE3, 0xE7), "gui": (0xE3, 0xE7)}

# Lights no keyboard-page usage names, as PluginKeyNames.Other reads them.
FN_NAMES = {"fn", "function", "fn1", "fn2", "fn3", "mo(1)", "layerfn", "leftfn", "rightfn", "righfn", "lower", "ralse"}
KNOB_NAMES = {"knob", "knob1", "knob2", "knob3", "encoderknob", "encoder"}
LIGHT_NAMES = {"rgb", "light", "led", "lightrgb", "cycleled", "rgb_mod", "rgbmod", "rgbback", "rgbforw", "rgboff", "lightbutton", "lightonoff", "ledprofile"}
MEDIA_NAMES = {
    "mute": "Mute", "mediaplaypause": "MediaPlayPause", "mediastop": "MediaStop", "medianexttrack": "MediaFastForward",
    "next": "MediaFastForward", "mediaprevioustrack": "MediaRewind", "previous": "MediaRewind", "pause/resume": "MediaPlayPause",
    "volu": "Volume Up", "volume+": "Volume Up", "vold": "Volume Down", "volume-": "Volume Down", "lock": "Lock",
    "circle": "Circle", "cross": "Cross", "square": "Square", "triangle": "Triangle",
}
EDGE = re.compile(r"^(under ?glow|ug|sidestrip|[lr]sidelight|(left|right) ?led|zargb|deco|top ?led|bottom ?led|(top|bottom|left|right) ?bar)", re.I)
G_KEY = re.compile(r"^g\d{1,2}$", re.I)
LOGO = re.compile(r"^logo", re.I)
MACRO = re.compile(r"^m(\d)$", re.I)


def key_usage(name):
    """The usage a key's name gives by itself, or None."""
    stripped = name.strip()
    if stripped in KEY_SYMBOLS:
        return KEY_SYMBOLS[stripped]
    if len(stripped) == 1 and stripped.isascii() and stripped.isalpha():
        return 0x04 + ord(stripped.upper()) - ord("A")
    if len(stripped) == 1 and stripped.isdigit():
        return 0x27 if stripped == "0" else 0x1D + int(stripped)
    if SPACE_PART.match(stripped):
        return 0x2C
    low = re.sub(r"^cntr\s+", "", stripped.lower())
    for candidate in (low, PART_OF_A_KEY.sub("", low)):
        word = re.sub(r"[\s]+", "", candidate).replace("_", "")
        if word in KEY_WORDS:
            return KEY_WORDS[word]
        if candidate in KEY_SYMBOLS:
            return KEY_SYMBOLS[candidate]
        if len(candidate) == 1 and candidate.isascii() and candidate.isalpha():
            return 0x04 + ord(candidate.upper()) - ord("A")
    return None


def other_name(name):
    """What PluginKeyNames.Other reads for a light no usage names. An unnamed
    entry is a key no plugin names."""
    stripped = name.strip()
    word = re.sub(r"\s+", "", stripped.lower())
    if word in FN_NAMES:
        return "Fn"
    if word in KNOB_NAMES:
        return "Knob"
    if word in LIGHT_NAMES:
        return "Light"
    if word in MEDIA_NAMES:
        return MEDIA_NAMES[word]
    if LOGO.match(stripped):
        return "Logo"
    if EDGE.match(stripped):
        return "Underglow"
    m = MACRO.match(stripped)
    if m:
        return "M" + m.group(1)
    if G_KEY.match(stripped):
        return stripped.upper()
    if word == "profile":
        return "Profile"
    return ""


QWERTY_TOP = [0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31]
QWERTY_HOME = [0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x32]
QWERTY_BOTTOM = [0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38]
QWERTY_NUMBERS = [0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E]
PUNCTUATION = {0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x64} | set(range(0x04, 0x28))


def is_letter(name):
    s = name.strip()
    return len(s) == 1 and s.isascii() and s.isalpha()


# A row's letters as the boards have them, left to right.
TOP_LETTERS = ("QWERTYUIOP", "QWERTZUIOP", "AZERTYUIOP")
HOME_LETTERS = ("ASDFGHJKL", "QSDFGHJKLM")
BOTTOM_LETTERS = ("ZXCVBNM", "YXCVBNM", "WXCVBN")
DIGITS = ("1234567890", "!@#$%^&*()")


def run_of(row, names, spellings):
    """Where the row holds one of the spellings, key after key, or None. A row
    that holds its letters any other way, in a plugin whose places are a loose
    grid, keeps the usages its names give."""
    text = "".join(names[i].strip().upper() if len(names[i].strip()) == 1 else "\0" for i in row)
    for spelling in spellings:
        at = text.find(spelling)
        if at >= 0:
            return at, len(spelling)
    return None


def by_place(row, names, usages, sequence, spellings):
    """Gives a row's keys their usages by place, from its first letter on: a
    legend names what the key types in the board's language, and a usage goes by
    the key's place on a US board. Past the letters a key takes the sequence only
    while its name is a character key."""
    found = run_of(row, names, spellings)
    if found is None:
        return None
    start, length = found
    for offset, i in enumerate(row[start : start + len(sequence)]):
        usage = usages[i]
        # A legend of one character is a character key, the AZERTY u-grave among them.
        if offset >= length and len(names[i].strip()) != 1 and (usage is None or usage not in PUNCTUATION):
            # A letter of the AZERTY home row's M runs past the nine of the others.
            if not (is_letter(names[i]) and offset < len(sequence)):
                break
        usages[i] = sequence[offset]
    return start


def place_fixes(names, places, usages):
    rows = {}
    for i, place in enumerate(places):
        rows.setdefault(place[1], []).append(i)
    for row in rows.values():
        row.sort(key=lambda i: places[i][0])
        # A key at another key's place is out of its row in the plugin's list.
        taken = set()
        row[:] = [i for i in row if not (places[i][0] in taken or taken.add(places[i][0]))]
        by_place(row, names, usages, QWERTY_TOP, TOP_LETTERS)
        by_place(row, names, usages, QWERTY_HOME, HOME_LETTERS)
        start = by_place(row, names, usages, QWERTY_BOTTOM, BOTTOM_LETTERS)
        if start:
            # The key between the left shift and the first letter is the ISO key.
            before = row[start - 1]
            if usages[before] != 0xE1 and (usages[before] is None or usages[before] in PUNCTUATION):
                usages[before] = 0x64
        at = by_place(row, names, usages, QWERTY_NUMBERS, DIGITS)
        if at:
            before = row[at - 1]
            if usages[before] is None or usages[before] in PUNCTUATION:
                usages[before] = 0x35


# The number pad's key for a name.
PAD_OF = {"1": 0x59, "2": 0x5A, "3": 0x5B, "4": 0x5C, "5": 0x5D, "6": 0x5E, "7": 0x5F, "8": 0x60, "9": 0x61, "0": 0x62,
          "/": 0x54, "*": 0x55, "-": 0x56, "+": 0x57, ".": 0x63, "Enter": 0x58, "NumLock": 0x53}


def number_pad_keys(names, places, usages):
    """A board with no letters is a number pad, and its digits are the pad's. On a board with
    letters a second copy of a key is the pad's when the pad has one, its names being "7" and
    "/" where the number row's are the same or a shifted "&" and "?". Any other second copy
    names nothing: a key lights one key's light."""
    letters = any(u is not None and 0x04 <= u <= 0x1D for u in usages)
    if not letters:
        for i, u in enumerate(usages):
            if names[i].strip() in PAD_OF:
                usages[i] = PAD_OF[names[i].strip()]
        return
    by_usage = {}
    for i, u in enumerate(usages):
        if u is not None and u != 0x2C and not PART_OF_A_KEY.search(names[i].strip()) and not names[i].startswith("2nd "):
            by_usage.setdefault(u, []).append(i)
    for u, copies in by_usage.items():
        if len(copies) < 2:
            continue
        copies.sort(key=lambda i: places[i][0])
        for i in copies[1:]:
            usages[i] = PAD_OF.get(names[i].strip())


def key_usages(names, places):
    usages = []
    for name in names:
        usages.append(key_usage(name))
    # "Ctrl", "Alt" and "Shift" with no side: the first in a row is the left one.
    seen = {}
    for i in sorted(range(len(names)), key=lambda i: (places[i][1], places[i][0])):
        word = names[i].strip().lower()
        if word in SIDES:
            count = seen.get((word, places[i][1]), 0)
            seen[(word, places[i][1])] = count + 1
            usages[i] = SIDES[word][0 if count == 0 else 1]
    place_fixes(names, places, usages)
    number_pad_keys(names, places, usages)
    return usages


def qmk_identifier(path):
    base = os.path.splitext(path.rsplit("/", 1)[-1])[0]
    word = re.sub(r"[^0-9A-Za-z]+", "_", base).strip("_")
    return "Qmk_" + word


def read_qmk_boards(qmk_clone, signal_clone):
    """Every QMK plugin once, by the products it names, best source first."""
    boards = []
    taken = set()
    sources = [(qmk_clone, c, f) for c in QMK_BRANCHES for f in git_files(qmk_clone, c)]
    sources += [(signal_clone, SIGNAL_MASTER, f) for f in git_files(signal_clone, SIGNAL_MASTER) if f.startswith("Plugins/QMK/")]
    for clone, commit, path in sources:
        if not path.endswith(".js") or ".test." in path:
            continue
        text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(clone, commit, path)))
        vendor = re.search(r"function\s+VendorId\s*\(\s*\)\s*\{\s*return\s*(0[xX][0-9a-fA-F]+)", text)
        products = products_of(text, path)
        name = re.search(r'function\s+Name\s*\(\s*\)\s*\{\s*return\s*"([^"]*)"', text)
        if not vendor or not products:
            continue
        vid = int(vendor.group(1), 16)
        # Vendor 0 is a plugin's placeholder (the K630 branch's K556 copy). No
        # device reports it, and the same plugin's real IDs are on another branch.
        if vid == 0:
            continue
        fresh = [p & 0xFFFF for p in products if (vid, p & 0xFFFF) not in taken]
        if not fresh:
            continue
        keys = read_list(text, "vKeys")
        key_names = read_list(text, "vKeyNames")
        places = read_list(text, "vKeyPositions")
        for product in fresh:
            taken.add((vid, product))
        boards.append({"clone": "qmk" if clone == qmk_clone else "signal", "commit": commit, "path": path, "title": name.group(1),
                       "vendor": vid, "products": fresh, "keys": keys, "names": key_names, "places": places})
    return boards


# ---------------------------------------------------------------------------
# Plugins that keep many boards in one object: the entry's name, its lists, and,
# where the entry only names a layout, the layout's lists.
# ---------------------------------------------------------------------------

# (prefix, plugin, the object of boards, the object of layouts or None)
LIBRARIES = [
    ("Nuvoton", "Nuvoton/Nuvoton_Keyboard_Controller.js", "LEDLibrary", "LEDLayout"),
    ("Nuvoton2", "Nuvoton/Nuvoton_Keyboard2_Controller.js", "LEDLibrary", "LEDLayout"),
    ("Sinowealth2", "Sinowealth/Sinowealth_Keyboard_Controller2.js", "LEDLibrary", "LEDLayout"),
    ("Kbm", "KBM Gaming/KBM_Keyboard_Controller.js", "LEDLibrary", "LEDLayout"),
    ("Xiaohua", "Xiaohua/Xiaohua_Keyboard_Controller.js", "LEDLibrary", "LEDLayout"),
    ("Sonix2", "Sonix/SONIX_Keyboard_Controller_2.js", "LEDLibrary", "LEDLayout"),
    ("EVision", "EVision/EVision_Keyboard_Controller.js", "LEDLibrary", None),
    ("ByTech", "BYTech/BYTech_Keyboard_Controller.js", "LEDLibrary", None),
    ("AsusKeyboard", "Asus/ASUS_Keyboard.js", "LEDLibrary", None),
    ("SteelSeriesKeyboard", "Steelseries/Steelseries_Keyboard_Controller.js", "PIDLibrary", None, ("LedNames", "LedPositions", "Leds")),
    ("SteelSeriesLaptop", "Steelseries/Steelseries_Keyboard_Laptop_Controller.js", "PIDLibrary", None, ("LedNames", "LedPositions", "Leds"),
     "d548f75504362996a20cca0be23450e0da9cdfe5"),
]


def object_entries(text, brace):
    """Each key of the object whose brace is at this index, with its value's
    text, for values that are objects themselves."""
    entries = []
    i = brace + 1
    n = len(text)
    while i < n:
        while i < n and text[i] in " \t\r\n,":
            i += 1
        if i >= n or text[i] == "}":
            break
        if text[i] in "\"'":
            j = text.index(text[i], i + 1)
            key = text[i + 1 : j]
            i = j + 1
        else:
            m = re.compile(r"[\w.]+").match(text, i)
            key = m.group(0)
            i = m.end()
        while text[i] in " \t\r\n":
            i += 1
        assert text[i] == ":", (key, text[i : i + 20])
        i += 1
        while text[i] in " \t\r\n":
            i += 1
        if text[i] == "{":
            end = closing_brace(text, i)
            entries.append((key, text[i : end + 1]))
            i = end + 1
        else:
            while text[i] not in ",}":
                i += 1
    return entries


def closing_brace(text, brace):
    depth = 0
    i = brace
    while True:
        c = text[i]
        if c in "\"'`":
            j = i + 1
            while text[j] != c:
                if text[j] == "\\":
                    j += 1
                j += 1
            i = j + 1
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1


def named_object(text, name):
    m = re.search(r"\b" + name + r"\s*=\s*\{", text)
    return m.end() - 1


def entry_string(body, field):
    m = re.search(r"\b" + field + r"\s*:\s*[\"']([^\"']*)[\"']", body)
    return m.group(1) if m else None


def entry_lists(body, names_field="vLedNames", places_field="vLedPositions", leds_field="vLeds"):
    """An entry's three lists, flat: a list of LEDs per zone makes each LED's
    number its zone times 256 plus its LED, and its flat order is the names'."""
    found = {}
    for field in (names_field, places_field, leds_field):
        m = re.search(r"\b" + field + r"\s*:\s*\[", body)
        if not m:
            return None
        found[field], _ = parse_array(body, m.end() - 1)
    leds = found[leds_field]
    if leds and isinstance(leds[0], list):
        leds = [zone * 256 + led for zone, part in enumerate(leds) for led in part]
    return found[names_field], found[places_field], leds


def layout_identifier(prefix, name):
    word = re.sub(r"[^0-9A-Za-z]+", "_", name.replace("%", "")).strip("_")
    return f"{prefix}_{word}"


def generate_libraries(signal, lines):
    # The Omni dongle's plugin names a board by the serial number it reports.
    text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(signal, SIGNAL_BETA, "Plugins/Asus/Omni Controller/ASUS_Keyboard_Protocol.js")))
    models = re.search(r"this\.modelLibrary\s*=\s*\{(.*?)\}", text, re.S).group(1)
    boards = dict(object_entries(text, named_object(text, "ledLibrary")))
    made = {}
    lines.append("")
    lines.append("        internal static readonly PluginBoard[] AsusOmniKeyboard =")
    lines.append("        {")
    for serial, name in re.findall(r'"([^"]+)"\s*:\s*"([^"]+)"', models):
        if name in boards and name not in made:
            made[name] = layout_identifier("AsusOmni", name)
        if name in made:
            lines.append(f"            new({quote(serial)}, {quote(name)}, {made[name]}),")
    lines.append("        };")
    for name, method in made.items():
        names, places, leds = fix_library_lists("AsusOmni", name, *entry_lists(boards[name]))
        names, places, leds = in_step(f"AsusOmni {name}", names, places, leds)
        write_qmk_method(lines, method, "Asus/Omni Controller/ASUS_Keyboard_Protocol.js", names, places, leds, key_usages(names, places))
    for library in LIBRARIES:
        prefix, plugin, boards_object, layouts_object = library[:4]
        fields = library[4] if len(library) > 4 else ("vLedNames", "vLedPositions", "vLeds")
        commit = library[5] if len(library) > 5 else SIGNAL_BETA
        text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(signal, commit, "Plugins/" + plugin)))
        boards = object_entries(text, named_object(text, boards_object))
        methods = {}
        if layouts_object:
            for name, body in object_entries(text, named_object(text, layouts_object)):
                lists = entry_lists(body)
                if lists:
                    methods[name] = (layout_identifier(prefix, name), lists)
        rows = []
        for key, body in boards:
            title = entry_string(body, "name") or key
            if layouts_object:
                layout = entry_string(body, "layout")
                if layout in methods:
                    rows.append((key, title, methods[layout][0]))
            else:
                lists = entry_lists(body, *fields)
                if lists:
                    method = layout_identifier(prefix, key)
                    methods[key] = (method, lists)
                    rows.append((key, title, method))
        lines.append("")
        lines.append(f"        internal static readonly PluginBoard[] {prefix} =")
        lines.append("        {")
        for key, title, method in rows:
            number = re.fullmatch(r"0[xX][0-9a-fA-F]+|\d+", key)
            model = str(int(key, 0)) if number else key
            lines.append(f"            new({quote(model)}, {quote(title)}, {method}),")
        lines.append("        };")
        used = {method for _, _, method in rows}
        for name, (method, lists) in methods.items():
            if method not in used:
                continue
            names, places, leds = fix_library_lists(prefix, name, *lists)
            names, places, leds = in_step(f"{prefix} {name}", names, places, leds)
            write_qmk_method(lines, method, plugin, names, places, leds, key_usages(names, places))


# Lists in a plugin's library that fall out of step, and what puts them back. The Kreo Hive98's
# number row lists an LED, 37, its names and places leave out, so every later key would land one
# off, and the plugin itself fails on it. The Strix Scope II's have the ISO # named "/" on the
# home row and no / beside the "." on the row below, and the Strix Scope RX's and NX's have them
# crossed, and their places leave out the second logo's, so a row's last key shows at the start
# of the next, down to a place too many beside the "/".
LIBRARY_FIXES = {
    ("EVision", "Kreo Hive98"): {"drop_leds": (37,)},
    ("AsusKeyboard", "ROG Strix Scope II NX"): {"rename_first": ("/", "ISO_#"), "insert_after": (".", "/")},
    ("AsusKeyboard", "ROG Strix Scope II RX"): {"rename_first": ("/", "ISO_#"), "insert_after": (".", "/")},
    ("AsusKeyboard", "ROG Strix Scope RX"): {"swap": ("/", "ISO_#"), "add_place": ([18, 0], [19, 0]), "drop_place": [12, 4]},
    ("AsusKeyboard", "ROG Strix Scope NX"): {"swap": ("/", "ISO_#"), "add_place": ([18, 0], [19, 0]), "drop_place": [12, 4]},
    ("AsusOmni", "ROG Strix Scope NX"): {"swap": ("/", "ISO_#"), "add_place": ([18, 0], [19, 0]), "drop_place": [12, 4]},
    # The ISO # sits on the home row, and LED 58 under the Enter's upper half, where the list has
    # the # and then a backslash, so the names from Del to the quote run an LED off.
    ("Nuvoton", "Pulsar TKL"): {"remove": ("ISO_#",), "rename_first": ("\\", "Enter Upper"), "insert_after": ("'", "ISO_#"), "places": {"/": [11, 4]}},
}


# Entries whose lists the plugin itself leaves out of step, and what the plugin does about it.
# Bragi Voyager A1600: ledMap has an 83rd LED that ledNames and ledPositions lack. The plugin walks
# ledPositions (Corsair_Bragi_Device.js:642 and 676), so that LED is never driven.
OUT_OF_STEP_IN_THE_PLUGIN = {"Bragi Voyager A1600"}


def in_step(label, names, places, leds):
    """A library entry's three lists as long as each other once the fixes are applied. An entry
    out of step stops the run, because cutting it to the shortest would move every later key off
    its LED, unless it is listed as out of step in the plugin, which walks its places."""
    if len(names) == len(places) == len(leds):
        return names, places, leds
    if label in OUT_OF_STEP_IN_THE_PLUGIN and len(names) >= len(places) <= len(leds):
        return names[:len(places)], places, leds[:len(places)]
    raise ValueError(f"OUT OF STEP {label}: {len(leds)} LEDs, {len(names)} names, {len(places)} places")


def fix_library_lists(prefix, name, names, places, leds):
    fix = LIBRARY_FIXES.get((prefix, name))
    if not fix:
        return names, places, leds
    names, places, leds = list(names), list(places), list(leds)
    for led in fix.get("drop_leds", ()):
        leds.remove(led)
    for name_ in fix.get("remove", ()):
        names.remove(name_)
    if "rename_first" in fix:
        old, new = fix["rename_first"]
        names[names.index(old)] = new
    if "insert_after" in fix:
        after, name_ = fix["insert_after"]
        names.insert(names.index(after) + 1, name_)
    if "swap" in fix:
        a, b = fix["swap"]
        i, j = names.index(a), names.index(b)
        names[i], names[j] = names[j], names[i]
    for name_, place in fix.get("places", {}).items():
        places[names.index(name_)] = place
    if "add_place" in fix:
        after, place = fix["add_place"]
        places.insert(places.index(after) + 1, place)
        places.remove(fix["drop_place"])
    return names, places, leds


# Corsair's wireless family: every board and mouse of its two plugins, with the
# product each ID names. A plugin leaves 0x2B07 out of its list as untested and its
# code treats it as the K65 Plus, and a branch adds the M75's other ID.
CORSAIR_PLUGINS = [
    ("Bragi", "Corsair/Modern_Corsair/Corsair_Bragi_Device.js", 1),
    ("BragiHeadset", "Corsair/Modern_Corsair/Corsair_Headset_Controller_Bragi.js", 4),
]
CORSAIR_EXTRA_PRODUCTS = {"Bragi": {0x2B07: "K65 Plus", 0x2B08: "M75"}}


def generate_corsair(signal, lines):
    lines += [
        "",
        "    /// <summary>Corsair's wireless family as SignalRGB's plugins list it.</summary>",
        "    internal static partial class PluginCorsairTables",
        "    {",
        "        internal static readonly PluginCorsairDevice[] Devices =",
        "        {",
    ]
    methods = []
    for prefix, plugin, interface in CORSAIR_PLUGINS:
        text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(signal, SIGNAL_BETA, "Plugins/" + plugin)))
        listed = re.search(r"static ProductIDList\(\)\s*\{\s*return Object\.freeze\(\{(.*?)\}\)", text, re.S).group(1)
        products = {int(p, 16): n for p, n in re.findall(r"(0[xX][0-9a-fA-F]+)\s*:\s*\"([^\"]+)\"", listed)}
        products.update(CORSAIR_EXTRA_PRODUCTS.get(prefix, {}))
        found = re.search(r"static DeviceList\(\)\s*\{\s*return Object\.freeze\(\{", text)
        entries = dict(object_entries(text, found.end() - 1))
        made = {}
        for product, name in sorted(products.items()):
            body = entries.get(name)
            if body is None:
                # The Bragi plugin names this receiver and gives it no DeviceList entry, so it has no lights to read.
                if name == "Laptop Slipstream Dongle":
                    continue
                raise ValueError(f"{plugin}: ProductIDList names {name} and DeviceList has no entry for it")
            lists = entry_lists(body, "ledNames", "ledPositions", "ledMap")
            if not lists or not lists[2]:
                continue
            spacing = re.search(r"ledSpacing\s*:\s*(\d+)", body)
            keyboard = entry_string(body, "keymapType") == "Keyboard"
            if name not in made:
                method = layout_identifier(prefix, name)
                names, places, leds = lists
                names, places, leds = in_step(f"{prefix} {name}", names, places, leds)
                if keyboard:
                    write_qmk_method(methods, method, plugin, names, places, leds, key_usages(names, places))
                else:
                    write_table_method(methods, method, plugin, names, places, leds)
                made[name] = method
            title = entry_string(body, "name") or name
            kind = "Keyboard" if keyboard else "Mouse" if prefix == "Bragi" else "Headset"
            lines.append(f"            new(0x{product:04X}, {quote('Corsair ' + title)}, PluginCorsairKind.{kind}, {interface}, {spacing.group(1) if spacing else 0}, {made[name]}),")
    lines.append("        };")
    lines += methods
    lines += ["    }"]


def write_table_method(lines, method, plugin, names, places, indexes):
    """A table whose names stay whole, for a device with no keys."""
    lines.append("")
    lines.append(f"        private static PluginKeyTable {method}() => new(")
    lines.append(f"            {quote(plugin)},")
    lines.append("            new[] { " + ", ".join(quote(n) for n in names) + " },")
    lines.append("            new[] { " + ", ".join(str(i) for i in indexes) + " },")
    lines.append("            new byte[] { " + ", ".join(str(p[0]) for p in places) + " },")
    lines.append("            new byte[] { " + ", ".join(str(p[1]) for p in places) + " });")


def write_qmk_method(lines, method, plugin, names, places, indexes, usages):
    placed = plugin not in QMK_NO_PLACES
    others = [(i, other_name(n)) for i, (n, u) in enumerate(zip(names, usages)) if u is None and other_name(n) != ""]
    lines.append("")
    lines.append(f"        private static PluginKeyTable {method}() => new(")
    lines.append(f"            {quote(plugin)},")
    lines.append(f"            Names({len(names)}" + "".join(f", ({i}, {quote(n)})" for i, n in others) + "),")
    lines.append("            new[] { " + ", ".join(str(i) for i in indexes) + " },")
    lines.append(("            new byte[] { " + ", ".join(str(p[0]) for p in places) + " },") if placed else "            null,")
    lines.append(("            new byte[] { " + ", ".join(str(p[1]) for p in places) + " },") if placed else "            null,")
    lines.append("            new ushort[] { " + ", ".join(str(u or 0) for u in usages) + " });")


def gmmk3_from_firmware(suffix, names, places, keys):
    """The plugin's numbers for the GMMK 3 run off the firmware's list on these layouts
    (g_aw20216s_leds in keyboards/gmmk/gmmk3/SIZE/LAYOUT/LAYOUT.c at ell1010/QMK-OpenRGB
    1ed5a75, built on Glorious's source). The 65% numbers Tab with Backspace and every key
    after it one low, and its ISO list is the ANSI one. The ISO 75% leaves out the key
    beside the left shift. Every ISO list has # and Enter crossed. The LEDs run in the
    order of the lists, with the ISO keys added and the badge last."""
    if suffix == "ISOp100":
        keys[names.index("Enter")], keys[names.index("#")] = keys[names.index("#")], keys[names.index("Enter")]
        return names, places, keys
    if suffix not in ("ANSIp65", "ISOp65", "ISOp75"):
        return names, places, keys
    if suffix == "ANSIp65":
        # The 65% ANSI has no badge: its last LED is the twentieth side light.
        at = names.index("Logo")
        del names[at], places[at], keys[at]
        return names, places, list(range(len(names)))
    # The # key sits on the home row, and Enter's LED where ANSI has its backslash.
    hash_key, enter = names.index("\\"), names.index("Enter")
    places[hash_key], places[enter] = places[enter], places[hash_key]
    names[hash_key] = "#"
    if suffix == "ISOp65":
        names[names.index("End")] = "Page Down"
    shift = names.index("Left Shift")
    x, y = places[shift]
    names.insert(shift + 1, "ISO_<")
    places.insert(shift + 1, [x + 1, y])
    keys.insert(shift + 1, 0)
    # The row's keys move right until a gap takes it up.
    at = shift + 2
    while at < len(places) and places[at][1] == y and places[at][0] <= places[at - 1][0]:
        places[at] = [places[at - 1][0] + 1, y]
        at += 1
    logo = names.index("Logo")
    numbered = [i for i in range(len(names)) if i != logo]
    for led, i in enumerate(numbered):
        keys[i] = led
    keys[logo] = len(numbered)
    return names, places, keys


def lists_of(board, text, suffix):
    """A plugin's three lists under a layout's suffix, in step."""
    path = board["path"]
    names = read_list(text, "vKeyNames" + suffix)
    places = read_list(text, "vKeyPositions" + suffix)
    keys = read_list(text, "vKeys" + suffix)
    for extra in QMK_EXTRA_NAMES.get(path, ()):
        names.remove(extra)
    for after, name in QMK_MISSING_NAMES.get(path, ()):
        names.insert(names.index(after) + 1, name)
    if not len(names) == len(places) == len(keys):
        raise ValueError(f"{path}: {len(keys)} keys, {len(names)} names, {len(places)} places")
    if path == "GMMK/GMMK_3_QMK_Keyboard.js":
        names, places, keys = gmmk3_from_firmware(suffix, names, places, keys)
    if path in QMK_ISO_ENTER_ON_THE_TAB_ROW:
        enter = names.index("Enter")
        hash_key = enter - 1
        if names[hash_key] != "'":
            names[hash_key] = "ISO_#"
        del names[enter]
        names.insert(names.index("]") + 1, "Enter")
    for name, place in QMK_PLACE_FIXES.get(path, {}).items():
        places[names.index(name)] = place
    for led, name in QMK_NAME_FIXES.get(path, {}).items():
        names[keys.index(led)] = name
    if path in QMK_PLACE_LIST_FIXES:
        fix = QMK_PLACE_LIST_FIXES[path]
        after, place = fix["insert_after"]
        places.insert(places.index(after) + 1, place)
        places.remove(fix["remove"])
    if "Square" in names and "X" in names:
        # The shapes above a number pad: its X is the cross, and the letter comes later.
        names[names.index("X")] = "Cross"
    # Two keys on one LED: the Zoom98 gives its Dual Lines key the backtick's. The
    # plugin's picture lets the last one write, so the last keeps it.
    last = {key: i for i, key in enumerate(keys)}
    kept = [i for i, key in enumerate(keys) if last[key] == i]
    if len(kept) != len(keys):
        names, places, keys = [names[i] for i in kept], [places[i] for i in kept], [keys[i] for i in kept]
    return names, places, keys


# ---------------------------------------------------------------------------
# The firmware a board runs
# ---------------------------------------------------------------------------

# Where the boards' firmware is, each tree at a pinned commit: the folders to read. Keychron keeps
# its boards on several branches, the newest first here: 2025q3, wireless_playground, playground
# and bluetooth_playground.
KEYCHRON_FIRMWARE = ("9ada9b7baecb9591c469b9b068146ac5891a480a", "666862cb8123b64a6b96718d739c6203ad99031f",
                     "4a8143cce9c2ab32a1384c352c3db324149abddd", "618127a725a1773e85f13455602cf6f72ab4de17")
KEYCHRON_FOLDERS = ("keyboards/keychron", "keyboards/lemokey")
QMK_FIRMWARE = "7a1bbf37c5c07da4ea0a162bb139083a46ef40a1"
QMK_FOLDERS = ("keyboards",)

KEYCODE_USAGES = {
    "KC_ESC": 0x29, "KC_ENT": 0x28, "KC_ENTER": 0x28, "KC_BSPC": 0x2A, "KC_TAB": 0x2B, "KC_SPC": 0x2C, "KC_SPACE": 0x2C, "KC_MINS": 0x2D, "KC_EQL": 0x2E,
    "KC_LBRC": 0x2F, "KC_RBRC": 0x30, "KC_BSLS": 0x31, "KC_NUHS": 0x32, "KC_SCLN": 0x33, "KC_QUOT": 0x34, "KC_GRV": 0x35, "KC_GESC": 0x29, "QK_GESC": 0x29,
    "KC_COMM": 0x36, "KC_DOT": 0x37, "KC_SLSH": 0x38, "KC_CAPS": 0x39, "KC_PSCR": 0x46, "KC_SCRL": 0x47, "KC_SLCK": 0x47, "KC_PAUS": 0x48, "KC_BRK": 0x48,
    "KC_INS": 0x49, "KC_HOME": 0x4A, "KC_PGUP": 0x4B, "KC_DEL": 0x4C, "KC_END": 0x4D, "KC_PGDN": 0x4E, "KC_RGHT": 0x4F, "KC_RIGHT": 0x4F, "KC_LEFT": 0x50,
    "KC_DOWN": 0x51, "KC_UP": 0x52, "KC_NUM": 0x53, "KC_NLCK": 0x53, "KC_PSLS": 0x54, "KC_PAST": 0x55, "KC_PMNS": 0x56, "KC_PPLS": 0x57, "KC_PENT": 0x58,
    "KC_P0": 0x62, "KC_PDOT": 0x63, "KC_NUBS": 0x64, "KC_APP": 0x65, "KC_LCTL": 0xE0, "KC_LSFT": 0xE1, "KC_LALT": 0xE2, "KC_LOPT": 0xE2, "KC_LOPTN": 0xE2,
    "KC_LGUI": 0xE3, "KC_LWIN": 0xE3, "KC_LCMD": 0xE3, "KC_LCMMD": 0xE3, "KC_RCTL": 0xE4, "KC_RSFT": 0xE5, "KC_RALT": 0xE6, "KC_ROPT": 0xE6, "KC_ROPTN": 0xE6,
    "KC_RGUI": 0xE7, "KC_RWIN": 0xE7, "KC_RCMD": 0xE7, "KC_RCMMD": 0xE7, "KC_MENU": 0x65,
}
for _i, _c in enumerate("ABCDEFGHIJKLMNOPQRSTUVWXYZ"):
    KEYCODE_USAGES["KC_" + _c] = 0x04 + _i
for _i, _c in enumerate("1234567890"):
    KEYCODE_USAGES["KC_" + _c] = 0x1E + _i
for _i in range(1, 13):
    KEYCODE_USAGES[f"KC_F{_i}"] = 0x39 + _i
for _i in range(13, 25):
    KEYCODE_USAGES[f"KC_F{_i}"] = 0x68 + _i - 13
for _i in range(1, 10):
    KEYCODE_USAGES[f"KC_P{_i}"] = 0x58 + _i

# What a keycode no usage names reads as, for a key's light.
KEYCODE_NAMES = {"KC_MUTE": "Mute", "KC_VOLU": "Volu", "KC_VOLD": "Vold", "KC_MPLY": "MediaPlayPause", "KC_MSTP": "MediaStop", "KC_MNXT": "MediaNextTrack",
                 "KC_MPRV": "MediaPreviousTrack", "RGB_MOD": "Light", "RGB_TOG": "Light", "RM_NEXT": "Light", "RM_TOGG": "Light", "UG_NEXT": "Light", "UG_TOGG": "Light",
                 "BL_STEP": "Light", "BL_TOGG": "Light"}


def strip_c_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def lenient_json(text):
    """QMK reads its JSON files as Hjson (qmk_cli depends on the hjson package): comments,
    trailing commas, a line break where a comma belongs, and \\' in a string are all allowed."""
    text = re.sub(r'("(?:[^"\\]|\\.)*")|//[^\n]*', lambda m: m.group(1) or "", text)
    out = []
    ended = False  # the last token closed a value, so a value that follows needs a comma first
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            if ended:
                out.append(",")
            out.append(text[i:j + 1].replace("\\'", "'"))
            i = j + 1
            k = i
            while k < n and text[k] in " \t\r\n":
                k += 1
            ended = not (k < n and text[k] == ":")
        elif c in "{[":
            if ended:
                out.append(",")
            out.append(c)
            ended = False
            i += 1
        elif c in "}]":
            out.append(c)
            ended = True
            i += 1
        elif c in ",:":
            out.append(c)
            ended = False
            i += 1
        elif c in " \t\r\n":
            out.append(c)
            i += 1
        else:
            m = re.match(r"-?\d[\w.+\-]*|true|false|null", text[i:i + 40])
            if m:
                if ended:
                    out.append(",")
                out.append(m.group())
                ended = True
                i += m.end()
            else:
                out.append(c)
                i += 1
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", "".join(out)))


def key_of_keycode(code):
    """A keymap entry's keycode, out of whatever wraps it: LT(1, KC_A), LCTL_T(KC_A)."""
    m = re.search(r"\b(KC_\w+|QK_GESC)\b", code)
    return m.group(1) if m else code


class FirmwareTree:
    """One firmware tree at a pinned commit."""

    def __init__(self, clone, commit, folders):
        self.clone, self.commit = clone, commit
        listed = subprocess.run(["git", "-C", clone, "ls-tree", "-r", "--name-only", commit, *folders], capture_output=True, check=True).stdout.decode("utf-8", "replace")
        self.files = set(listed.splitlines())
        self.beside = {}
        for path in self.files:
            self.beside.setdefault(path.rsplit("/", 1)[0], []).append(path)
        self.batch = subprocess.Popen(["git", "-C", clone, "cat-file", "--batch"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        self.boards = {}
        for path in sorted(self.files):
            if path.rsplit("/", 1)[-1] not in ("keyboard.json", "info.json") or "/keymaps/" in path:
                continue
            folder = path.rsplit("/", 1)[0]
            pid = self.config_value(folder, "pid")
            vid = self.config_value(folder, "vid")
            if pid and vid:
                key = (int(vid, 16), int(pid, 16))
                if folder not in self.boards.setdefault(key, []):
                    self.boards[key].append(folder)

    def read(self, path):
        if path not in self.files:
            return None
        self.batch.stdin.write(f"{self.commit}:{path}\n".encode())
        self.batch.stdin.flush()
        size = int(self.batch.stdout.readline().split()[2])
        data = self.batch.stdout.read(size)
        self.batch.stdout.read(1)
        return data.decode("utf-8", "replace")

    def configs(self, folder):
        """The folder's config files, nearest first, up to its vendor's folder."""
        found = []
        d = folder
        while d.count("/") >= 1:
            for name in ("keyboard.json", "info.json"):
                text = self.read(f"{d}/{name}")
                if text:
                    try:
                        found.append(lenient_json(text))
                    except ValueError as e:
                        # A config left out drops its board's vid and pid without a word.
                        raise ValueError(f"{self.commit}:{d}/{name} does not parse: {e}")
            d = d.rsplit("/", 1)[0]
        return found

    def config_value(self, folder, field):
        for config in self.configs(folder):
            value = config.get("usb", {}).get(field)
            if value:
                return value
        return None

    def up_from(self, folder):
        d = folder
        while d.count("/") >= 1:
            yield d
            d = d.rsplit("/", 1)[0]

    def leds(self, folder, configs):
        """Each LED's matrix cells and its point on the board, from g_led_config or, where a board has
        no such table, rgb_matrix.layout. The Q1 v1 ISO has both, and its layout has a cell twice."""
        for d in self.up_from(folder):
            sources = sorted(p for p in self.beside.get(d, ()) if p.endswith(".c"))
            for source in sources:
                text = strip_c_comments(self.read(source))
                m = re.search(r"g_led_config\s*=\s*\{\s*\{(.*?)\}\s*,\s*\{\s*\{(.*?)\}\s*,?\s*\}\s*,", text, re.S)
                if not m:
                    continue
                cells = {}
                for r, row in enumerate(re.findall(r"\{([^{}]*)\}", m.group(1))):
                    for c, v in enumerate(x.strip() for x in row.split(",")):
                        if re.fullmatch(r"\d+", v):
                            cells.setdefault(int(v), []).append((r, c))
                points = [(int(a), int(b)) for a, b in re.findall(r"\{\s*(\d+)\s*,\s*(\d+)\s*\}", "{" + m.group(2) + "}")]
                return [(cells.get(i, []), point) for i, point in enumerate(points)]
        for config in configs:
            layout = config.get("rgb_matrix", {}).get("layout")
            if layout:
                return [([tuple(l["matrix"])] if l.get("matrix") else [], (l.get("x", 0), l.get("y", 0))) for l in layout]
        return []

    def keymap(self, folder, configs):
        """Each matrix cell's keycode and place in the layout, from the default keymap's Windows
        layer and the layout it names."""
        args, layout_name = None, None
        for d in self.up_from(folder):
            text = self.read(f"{d}/keymaps/default/keymap.c")
            if text:
                text = strip_c_comments(text)
                m = None
                for layer in ("WIN_BASE", "WIN_BL", "_WIN", "WIN", "_BASE", "BASE", "_QWERTY", "0", "MAC_BASE"):
                    m = re.search(r"\[\s*" + layer + r"\s*\]\s*=\s*(\w+)\s*\(", text)
                    if m:
                        break
                m = m or re.search(r"\[[^\]]*\]\s*=\s*(LAYOUT\w*)\s*\(", text)
                if not m:
                    return {}
                layout_name = m.group(1)
                args, cur, depth, i = [], "", 1, m.end()
                while i < len(text):
                    c = text[i]
                    depth += c == "("
                    depth -= c == ")"
                    if depth == 0:
                        break
                    if c == "," and depth == 1:
                        args.append(cur.strip())
                        cur = ""
                    else:
                        cur += c
                    i += 1
                if cur.strip():
                    args.append(cur.strip())
                break
            text = self.read(f"{d}/keymaps/default/keymap.json")
            if text:
                data = lenient_json(text)
                layout_name, args = data.get("layout"), (data.get("layers") or [None])[0]
                break
        if not args:
            return {}
        layout = None
        for config in configs:
            layouts = config.get("layouts", {})
            name = config.get("layout_aliases", {}).get(layout_name, layout_name)
            if name in layouts:
                layout = layouts[name]["layout"]
                break
            if layout_name == "LAYOUT" and len(layouts) == 1:
                layout = next(iter(layouts.values()))["layout"]
                break
        if not layout or len(layout) != len(args):
            return {}
        return {tuple(k["matrix"]): (a, k.get("x", 0), k.get("y", 0)) for k, a in zip(layout, args)}

    def table(self, folder):
        """The board's names, places, LEDs and usages, or None when its firmware does not say."""
        configs = self.configs(folder)
        leds, keys = self.leds(folder, configs), self.keymap(folder, configs)
        if not leds or not keys:
            return None
        rows = sorted({int(k[2] + 0.5) for k in keys.values()})
        columns = {}
        for y in rows:
            at = -1
            for cell, k in sorted(((c, k) for c, k in keys.items() if int(k[2] + 0.5) == y), key=lambda e: e[1][1]):
                at = max(at + 1, int(k[1] + 0.5))
                columns[cell] = (at, rows.index(y))
        width = max((c[0] for c in columns.values()), default=0) + 1
        names, places, indexes, usages = [], [], [], []
        edges = 0
        taken = set()
        for led, (cells, point) in enumerate(leds):
            cell = next((c for c in cells if c in keys), None)
            if cell is None:
                # A light under no key: a row of its own below the keys, each in its own place.
                names.append("Underglow")
                usages.append(None)
                places.append([edges % width, len(rows) + 1 + edges // width])
                edges += 1
            else:
                code = key_of_keycode(keys[cell][0])
                usage = KEYCODE_USAGES.get(code)
                usages.append(usage)
                names.append(KEYCODE_NAMES.get(code, "Fn" if re.match(r"(MO|TG|TT|OSL|LT|FN|KC_FN|TL_|MO\()", code) else "") if usage is None else code)
                # A second LED under a key sits beside it in the picture.
                x, y = columns[cell]
                while (x, y) in taken:
                    x += 1
                taken.add((x, y))
                places.append([x, y])
            indexes.append(led)
        return names, places, indexes, usages


# Words every keyboard's name may share with another's.
COMMON_WORDS = {"keyboards", "keyboard", "qmk", "via", "ansi", "iso", "rgb", "random", "brands", "js", "the", "full", "encoder", "rev", "v1", "v2", "v3"}


def words_of(text):
    return {w for w in re.split(r"[^0-9a-z]+", text.lower()) if len(w) > 1} - COMMON_WORDS


def firmware_tables(trees, vendor, product, plugin_lights, plugin, title):
    """The firmware table of a product: where several boards share its IDs, the one nearest the
    plugin's LED count that names most keys. None when the firmware's count is too far off the
    plugin's, as with the Zoom98 whose only firmware here drives its underglow. Many hobby boards
    share a generic pair of IDs, so a folder has to share a word with the plugin's name as well."""
    best = None
    wanted = words_of(plugin + " " + title)
    for tree in trees:
        for folder in tree.boards.get((vendor, product), []):
            if not wanted & words_of(folder.split("keyboards/", 1)[1]):
                continue
            built = tree.table(folder)
            if not built:
                continue
            count = sum(1 for n in built[0] if n != "Underglow")
            if abs(count - plugin_lights) > max(4, plugin_lights // 8) and abs(len(built[2]) - plugin_lights) > max(4, plugin_lights // 8):
                continue
            named = sum(1 for u in built[3] if u)
            # RGB ahead of a white board with the same IDs.
            score = (-abs(count - plugin_lights), "white" not in folder, named)
            if best is None or score > best[0]:
                best = (score, folder, built)
    return (best[1], best[2]) if best else None


def generate_qmk(signal, qmk, keychron_firmware, qmk_firmware):
    """The boards, and each one's tables as methods, so a table is built when its
    board is lit and not when PadForge starts."""
    head = [
        "// Generated by tools/gen_plugin_key_tables.py. Do not edit.",
        "using System;",
        "",
        "namespace PadForge.Common.Input.Peripherals.HidFamilies",
        "{",
        "    /// <summary>The keyboards flashed with the SignalRGB protocol, from",
        "    /// qmk-plugins (GPL-2.0) and the QMK folder signal-plugins once held.</summary>",
        "    internal static partial class PluginQmkTables",
        "    {",
        f'        internal const string QmkPluginsCommit = "{QMK_MAIN}";',
        f'        internal const string KeychronFirmwareCommit = "{KEYCHRON_FIRMWARE[0]}";',
        f'        internal const string QmkFirmwareCommit = "{QMK_FIRMWARE}";',
        "",
        "        internal static readonly PluginQmkBoard[] Boards =",
        "        {",
    ]
    methods = []
    seen = set()
    trees = [FirmwareTree(keychron_firmware, commit, KEYCHRON_FOLDERS) for commit in KEYCHRON_FIRMWARE]
    trees.append(FirmwareTree(qmk_firmware, QMK_FIRMWARE, QMK_FOLDERS))
    made = {}

    def method_name(path, suffix=""):
        base = qmk_identifier(path) + ("_" + suffix if suffix else "")
        name = base
        n = 2
        while name in seen:
            name = f"{base}_{n}"
            n += 1
        seen.add(name)
        return name

    for board in read_qmk_boards(qmk, signal):
        path = board["path"]
        plugin = path if board["clone"] == "qmk" else path[len("Plugins/QMK/"):]
        source = (qmk if board["clone"] == "qmk" else signal)
        text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(source, board["commit"], path)))
        layout = QMK_LAYOUTS.get(path)
        products = board["products"]
        vendor = f"0x{board['vendor']:04X}"
        title = quote(board["title"])

        def table(suffix):
            names, places, keys = lists_of(board, text, suffix)
            method = method_name(path, suffix)
            write_qmk_method(methods, method, plugin, names, places, keys, key_usages(names, places))
            return method

        # The picture's report: how many LEDs and in what order of channels.
        per_report = re.search(r"LedsPerPacket\s*=\s*(\d+)", text)
        extras = f", LedsPerReport: {per_report.group(1)}" if per_report and per_report.group(1) != "9" else ""
        if re.search(r"rgbdata\[iLedIdx\]\s*=\s*\w+\[1\]", text):
            extras += ", GreenBlueRed: true"

        # What the firmware says, product by product, over the plugin's lists.
        if not (layout and "asked" in layout):
            kept = []
            for product in products:
                suffix = layout.get(product, "") if layout else ""
                from_firmware = firmware_tables(trees, board["vendor"], product, len(lists_of(board, text, suffix)[2]), plugin, board["title"])
                if from_firmware is None:
                    kept.append(product)
                    continue
                folder, (names, places, keys, usages) = from_firmware
                method = made.get(folder)
                if method is None:
                    method = made[folder] = method_name(path, "fw_" + re.sub(r"[^0-9A-Za-z]+", "_", folder.split("keyboards/", 1)[1]))
                    write_qmk_method(methods, method, plugin, names, places, keys, usages)
                head.append(f"            new({title}, {quote(plugin)}, {vendor}, new ushort[] {{ 0x{product:04X} }}, {method}, Firmware: {quote(folder)}{extras}),")
            products = kept
            if not products:
                continue

        if layout and "asked" in layout:
            variants = [(byte, table(suffix)) for byte, suffix in layout["asked"]]
            pairs = ", ".join(f"({'null' if b is None else b}, {m})" for b, m in variants)
            head.append(f"            new({title}, {quote(plugin)}, {vendor}, new ushort[] {{ {', '.join(f'0x{p:04X}' for p in products)} }}, {variants[0][1]}, new (byte?, Func<PluginKeyTable>)[] {{ {pairs} }}{extras}),")
        elif layout:
            by_suffix = {}
            for product in products:
                head.append(f"            new({title}, {quote(plugin)}, {vendor}, new ushort[] {{ 0x{product:04X} }}, {by_suffix.setdefault(layout[product], table(layout[product])) if layout[product] not in by_suffix else by_suffix[layout[product]]}),")
        else:
            head.append(f"            new({title}, {quote(plugin)}, {vendor}, new ushort[] {{ {', '.join(f'0x{p:04X}' for p in products)} }}, {table('')}{extras}),")
    tail = ["        };"] + methods + ["    }", ""]
    tail += [
        "    /// <summary>The boards of the plugins that keep many in one object.</summary>",
        "    internal static partial class PluginLibraryTables",
        "    {",
        f'        internal const string SignalPluginsCommit = "{SIGNAL_BETA}";',
    ]
    generate_libraries(signal, tail)
    tail += ["    }"]
    generate_corsair(signal, tail)
    tail += ["}"]
    return "\r\n".join(head + tail) + "\r\n"


def inventory_rows(clone, commit, prefix_strip, prefix_add, keep=None, only=None):
    rows = set()
    for path in git_files(clone, commit):
        if not path.endswith(".js") or ".test." in path:
            continue
        if keep and not keep(path):
            continue
        if only and path != only:
            continue
        text = without_comments(re.sub(r'return "(iVBOR|data:image)[^"]*"', 'return ""', git_show(clone, commit, path)))
        number = r"0[xX][0-9a-fA-F]+|\d+"
        vendor = re.search(r"function\s+VendorId\s*\(\s*\)\s*\{\s*return\s*(\[[^\]]*\]|" + number + ")", text, re.S)
        if not vendor:
            continue
        plugin = prefix_add + path[len(prefix_strip):]
        for vid in (int(v, 0) for v in re.findall(number, vendor.group(1))):
            for pid in products_of(text, path):
                if vid != 0 or pid != 0:
                    rows.add((vid, pid & 0xFFFF, plugin))
    return rows


def commented_out_rows(clone, rows):
    """The rows of COMMENTED_OUT_PRODUCTS: a plugin's vendor is the one its live rows have."""
    found = set()
    for path, pid, words in COMMENTED_OUT_PRODUCTS:
        comments = re.findall(r"/\*.*?\*/", git_show(clone, SIGNAL_BETA, path), re.S)
        entry = rf"0[xX]{pid:04X}\s*:\s*\{{[^\n]*{re.escape(words)}"
        if not any(re.search(entry, comment) for comment in comments):
            raise SystemExit(f"{path}: no comment on 0x{pid:04X} that says {words!r}")
        plugin = path[len("Plugins/"):]
        vendors = {v for v, _, p in rows if p == plugin}
        if not vendors:
            raise SystemExit(f"{path}: no product of the plugin to take a vendor from")
        found |= {(v, pid, plugin) for v in vendors}
    return found


def inventory(signal, qmk):
    """Every product every plugin names, for the coverage test, and the entries a
    plugin keeps in a comment with its reason. A product keeps the newest plugin that
    names it."""
    in_plugins = lambda path: path.startswith("Plugins/")
    rows = inventory_rows(signal, SIGNAL_BETA, "Plugins/", "", in_plugins)
    rows |= commented_out_rows(signal, rows)
    have = {(v, p) for v, p, _ in rows}

    def add(more):
        nonlocal rows, have
        fresh = {r for r in more if (r[0], r[1]) not in have}
        rows |= fresh
        have |= {(v, p) for v, p, _ in fresh}

    for commit, path in SIGNAL_BRANCH_FILES:
        add(inventory_rows(signal, commit, "Plugins/", "", only=path))
    add(inventory_rows(signal, SIGNAL_MASTER, "Plugins/", "", in_plugins))
    for commit in QMK_BRANCHES:
        add(inventory_rows(qmk, commit, "", "QMK/"))
    lines = [
        "// Generated by tools/gen_plugin_key_tables.py. Do not edit.",
        "namespace PadForge.Tests",
        "{",
        "    /// <summary>Every product SignalRGB's plugins name, and the plugin.</summary>",
        "    internal static class PluginInventory",
        "    {",
        "        internal static readonly (ushort Vendor, ushort Product, string Plugin)[] All =",
        "        {",
    ]
    for vid, pid, plugin in sorted(rows, key=lambda r: (r[2], r[1])):
        lines.append(f"            (0x{vid:04X}, 0x{pid:04X}, {quote(plugin)}),")
    lines += ["        };", "    }", "}"]
    return "\r\n".join(lines) + "\r\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("signal")
    parser.add_argument("qmk")
    parser.add_argument("keychron_firmware")
    parser.add_argument("qmk_firmware")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    outputs = [(OUT, generate(args.signal)), (QMK_OUT, generate_qmk(args.signal, args.qmk, args.keychron_firmware, args.qmk_firmware)), (INVENTORY, inventory(args.signal, args.qmk))]
    if args.check:
        differs = [p for p, t in outputs if not os.path.exists(p) or open(p, encoding="utf-8", newline="").read() != t]
        for p in differs:
            print(os.path.basename(p), "differs")
        if differs:
            return 1
        print("the plugin tables match")
        return 0
    for p, t in outputs:
        with open(p, "w", encoding="utf-8", newline="") as f:
            f.write(t)
        print("wrote", os.path.normpath(p))
    return 0


if __name__ == "__main__":
    sys.exit(main())
