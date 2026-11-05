"""The ASUS Aura SDK keyboard key table, from RGB.NET (#557).

WHAT IT READS, under RGB.NET's RGB.NET.Devices.Asus:
- Enum/AsusLedId.cs: the key codes the Aura SDK names a keyboard's keys by
  (IAuraSyncKeyboard.Key[code], IAuraRgbKey.Code), each a DirectInput scan
  code: 0x01 Escape, 0x1E A, and 0x80 plus the second byte of an E0-prefixed
  set 1 code for the keys a set 1 code gives two bytes, 0xC8 Up for E0 48.
- Keyboard/AsusKeyboardLedMapping.cs, LedMappings.KeyboardMapping: the RGB.NET
  LED each code is lit as, which says what key a code is. Only the codes this
  mapping names are taken, so every key in the table is one RGB.NET lights.

WHAT IT ADDS. The HID usage on the Keyboard/Keypad page (0x07, HID Usage
Tables 1.6, section 10) of each RGB.NET LED name, from the table below, and
for a light that is no keyboard-page key its kind and label. The script stops
when the mapping names an LED the table lacks, so a new key is never dropped
unseen.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/AuraKeys.g.cs, with CRLF
line endings.

Run it without Python's -I switch: it imports tools/openrgb_cpp.py.

Usage:
    python tools/gen_aura_key_table.py RGBNET_ROOT
    python tools/gen_aura_key_table.py RGBNET_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys
import openrgb_cpp as oc

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "AuraKeys.g.cs")

# RGB.NET LedId name (without "Keyboard_") -> HID usage, Keyboard/Keypad page.
USAGE = {
    "Escape": 0x29, "Backspace": 0x2A, "Tab": 0x2B, "Enter": 0x28, "Space": 0x2C, "CapsLock": 0x39,
    "MinusAndUnderscore": 0x2D, "EqualsAndPlus": 0x2E, "BracketLeft": 0x2F, "BracketRight": 0x30,
    "Backslash": 0x31, "NonUsTilde": 0x32, "SemicolonAndColon": 0x33, "ApostropheAndDoubleQuote": 0x34,
    "GraveAccentAndTilde": 0x35, "CommaAndLessThan": 0x36, "PeriodAndBiggerThan": 0x37,
    "SlashAndQuestionMark": 0x38, "NonUsBackslash": 0x64, "Application": 0x65,
    "PrintScreen": 0x46, "ScrollLock": 0x47, "PauseBreak": 0x48, "Insert": 0x49, "Home": 0x4A, "PageUp": 0x4B,
    "Delete": 0x4C, "End": 0x4D, "PageDown": 0x4E,
    "ArrowRight": 0x4F, "ArrowLeft": 0x50, "ArrowDown": 0x51, "ArrowUp": 0x52,
    "NumLock": 0x53, "NumSlash": 0x54, "NumAsterisk": 0x55, "NumMinus": 0x56, "NumPlus": 0x57,
    "NumEnter": 0x58, "NumPeriodAndDelete": 0x63, "NumComma": 0x85,
    "LeftCtrl": 0xE0, "LeftShift": 0xE1, "LeftAlt": 0xE2, "LeftGui": 0xE3,
    "RightCtrl": 0xE4, "RightShift": 0xE5, "RightAlt": 0xE6, "RightGui": 0xE7,
}
for i in range(26):
    USAGE[chr(ord("A") + i)] = 0x04 + i
for d in range(1, 10):
    USAGE[str(d)] = 0x1E + d - 1
USAGE["0"] = 0x27
for n in range(1, 13):
    USAGE["F%d" % n] = 0x3A + n - 1
for n in range(1, 10):
    USAGE["Num%d" % n] = 0x59 + n - 1
USAGE["Num0"] = 0x62

# Lights RGB.NET maps to an LED that names no key of its own: the Aura code
# says what they are. Kind, usage, label. RGB.NET lights these as
# Keyboard_CustomN, Logo and the media LEDs. A media LED this table lacks stops
# the run, since its label would be lost without a word.
OTHER = {
    # AsusLedId name -> (kind, usage, label)
    "KEY_F13": ("Key", 0x68, "None"), "KEY_F14": ("Key", 0x69, "None"), "KEY_F15": ("Key", 0x6A, "None"),
    # Katakana/Hiragana (International2), Ro (International1, the Brazilian
    # "/ ?" key RGB.NET names KEY_ABNT_C1), Henkan (International4), Muhenkan
    # (International5), Yen (International3) and Keypad = (0x67).
    "KEY_KANA": ("Key", 0x88, "None"), "KEY_ABNT_C1": ("Key", 0x87, "None"),
    "KEY_CONVERT": ("Key", 0x8A, "None"), "KEY_NOCONVERT": ("Key", 0x8B, "None"),
    "KEY_YEN": ("Key", 0x89, "None"), "KEY_NUMPADEQUALS": ("Key", 0x67, "None"),
    "KEY_FN": ("Other", 0, "Fn"),
    "KEY_MUTE": ("Other", 0, "Mute"), "KEY_PLAYPAUSE": ("Other", 0, "PlayPause"),
    "KEY_MEDIASTOP": ("Other", 0, "Stop"), "KEY_NEXTTRACK": ("Other", 0, "Next"),
    "KEY_VOLUMEDOWN": ("Other", 0, "VolumeDown"), "KEY_VOLUMEUP": ("Other", 0, "VolumeUp"),
    "UNDOCUMENTED_3": ("Logo", 0, "None"),
}


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", lambda m: " " * len(m.group(0)), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("rgbnet")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    asus = os.path.join(args.rgbnet, "RGB.NET.Devices.Asus")
    ids_text = strip_comments(read(os.path.join(asus, "Enum", "AsusLedId.cs")))
    mapping_text = strip_comments(read(os.path.join(asus, "Keyboard", "AsusKeyboardLedMapping.cs")))
    commit = oc.git_head(args.rgbnet)

    codes = {}
    for name, value in re.findall(r"\b([A-Z][A-Z0-9_]*)\s*=\s*(0x[0-9A-Fa-f]+)\s*,", ids_text):
        if name in codes:
            sys.exit("AsusLedId names %s twice" % name)
        codes[name] = int(value, 16)
    if len(codes) < 100:
        sys.exit("AsusLedId has only %d codes" % len(codes))

    block = re.search(r"KeyboardMapping\s*\{\s*get;\s*\}\s*=\s*new\(\)\s*\{(.*?)\};", mapping_text, re.S)
    if not block:
        sys.exit("no LedMappings.KeyboardMapping")
    pairs = re.findall(r"\{\s*LedId\.(\w+)\s*,\s*AsusLedId\.(\w+)\s*\}", block.group(1))
    if len(pairs) < 100:
        sys.exit("KeyboardMapping has only %d entries" % len(pairs))

    rows = []
    seen_codes = set()
    for led, asus_name in pairs:
        if asus_name not in codes:
            sys.exit("KeyboardMapping names AsusLedId.%s, which the enum lacks" % asus_name)
        code = codes[asus_name]
        if code in seen_codes:
            sys.exit("code 0x%X is mapped twice" % code)
        seen_codes.add(code)
        key = led[len("Keyboard_"):] if led.startswith("Keyboard_") else None
        if key is not None and key in USAGE:
            rows.append((code, "Key", USAGE[key], "None", asus_name, led))
        elif asus_name in OTHER:
            kind, usage, label = OTHER[asus_name]
            rows.append((code, kind, usage, label, asus_name, led))
        elif key is not None and key.startswith("Custom"):
            # A custom LED with no keyboard-page key and no label: a web, power
            # or calculator key, lit as a light of its own.
            rows.append((code, "Other", 0, "None", asus_name, led))
        else:
            sys.exit("no usage for LedId.%s (AsusLedId.%s)" % (led, asus_name))

    usages = {}
    for code, kind, usage, label, asus_name, led in rows:
        if usage:
            if usage in usages:
                sys.exit("usage 0x%02X given to %s and %s" % (usage, usages[usage], asus_name))
            usages[usage] = asus_name

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_aura_key_table.py from RGB.NET at %s" % commit[:10])
    out.append("//     (RGB.NET.Devices.Asus: Enum/AsusLedId.cs and Keyboard/AsusKeyboardLedMapping.cs,")
    out.append("//     LedMappings.KeyboardMapping). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class AuraLights")
    out.append("    {")
    out.append("        /// <summary>The RGB.NET commit the table was read at.</summary>")
    out.append("        internal const string RgbNetCommit = \"%s\";" % commit)
    out.append("")
    out.append("        /// <summary>The keyboard lights RGB.NET maps, in its mapping's order: the Aura")
    out.append("        /// SDK key code (AsusLedId), the light's kind, its HID usage (0 for none), its")
    out.append("        /// label, the AsusLedId name and the RGB.NET LED it is lit as.</summary>")
    out.append("        internal static readonly (ushort Code, LightKind Kind, ushort Usage, LightLabel Label, string Name, string Led)[] KeyboardKeys =")
    out.append("        {")
    for code, kind, usage, label, asus_name, led in rows:
        out.append("            (0x%X, LightKind.%s, 0x%02X, LightLabel.%s, \"%s\", \"%s\")," % (code, kind, usage, label, asus_name, led))
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
    print("wrote %s: %d lights" % (OUTPUT, len(rows)))


if __name__ == "__main__":
    main()
