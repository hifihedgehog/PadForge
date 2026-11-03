"""The key tables of the Razer Chroma SDK and the Logitech LED SDK (#557).

WHAT IT READS.
- The Chroma SDK's types header, Razer/RzChromaSDKTypes.h in
  razerofficial/CSDK_ChromaModSample: the RZKEY enum and RZLED_LOGO of the
  Keyboard namespace (each code is the key's cell in the 6 by 22 grid, row in
  the high byte and column in the low byte), the RZLED2 enum of the Mouse
  namespace (cells of the 9 by 7 grid, the same way), and every grid size and
  LED count the custom effects take. A code the enum names twice, such as
  RZKEY_KOR_1 beside RZKEY_JPN_1, keeps the name declared first.
- Colore's Effects/Keyboard/Key.cs (chroma-sdk/Colore), a second copy of the
  RZKEY codes. The script stops when a code of the header is missing there or
  sits at another value, so the two references never drift apart unseen.
- The Logitech LED SDK 9.00 header, LED/Include/LogitechLEDLib.h: the
  LogiLed::KeyName enum, which LogiLedSetLightingForKeyWithKeyName takes.
  The SDK folder is no clone, so its pin is the header's SHA-256.

WHAT IT ADDS. The HID usage on the Keyboard/Keypad page (0x07, HID Usage
Tables 1.6, section 10) of each key name, from the tables below. The script
stops when the header names a key the tables lack, so a new key is never
dropped unseen. A name with no usage (a macro key, Fn, a G-key, a logo) maps
to 0 and is labeled in the C#.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/SdkKeyTables.g.cs, with
CRLF line endings.

Usage:
    python tools/gen_sdk_key_tables.py CHROMA_SAMPLE_ROOT COLORE_ROOT LED_SDK_ROOT
    python tools/gen_sdk_key_tables.py CHROMA_SAMPLE_ROOT COLORE_ROOT LED_SDK_ROOT --check
"""
import argparse
import hashlib
import os
import re
import subprocess
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "SdkKeyTables.g.cs")

# HID Usage Tables 1.6, section 10, Keyboard/Keypad page 0x07.
LETTERS = {chr(ord("A") + i): 0x04 + i for i in range(26)}
DIGITS = {str(d): 0x1E + d - 1 for d in range(1, 10)}
DIGITS["0"] = 0x27
FKEYS = {"F%d" % n: 0x3A + n - 1 for n in range(1, 13)}

# RZKEY_ suffix -> usage. 0 for a key no keyboard-page usage names.
RZKEY_USAGE = {
    "ESC": 0x29, "NUMLOCK": 0x53, "NUMPAD0": 0x62,
    "NUMPAD_DIVIDE": 0x54, "NUMPAD_MULTIPLY": 0x55, "NUMPAD_SUBTRACT": 0x56, "NUMPAD_ADD": 0x57,
    "NUMPAD_ENTER": 0x58, "NUMPAD_DECIMAL": 0x63,
    "PRINTSCREEN": 0x46, "SCROLL": 0x47, "PAUSE": 0x48, "INSERT": 0x49, "HOME": 0x4A, "PAGEUP": 0x4B,
    "DELETE": 0x4C, "END": 0x4D, "PAGEDOWN": 0x4E, "RIGHT": 0x4F, "LEFT": 0x50, "DOWN": 0x51, "UP": 0x52,
    "TAB": 0x2B, "CAPSLOCK": 0x39, "BACKSPACE": 0x2A, "ENTER": 0x28, "SPACE": 0x2C,
    "LCTRL": 0xE0, "LSHIFT": 0xE1, "LALT": 0xE2, "LWIN": 0xE3,
    "RCTRL": 0xE4, "RSHIFT": 0xE5, "RALT": 0xE6, "RMENU": 0x65,
    "FN": 0,
    "MACRO1": 0, "MACRO2": 0, "MACRO3": 0, "MACRO4": 0, "MACRO5": 0,
    # The header's own comments name each OEM key by its US legend.
    "OEM_1": 0x35, "OEM_2": 0x2D, "OEM_3": 0x2E, "OEM_4": 0x2F, "OEM_5": 0x30, "OEM_6": 0x31,
    "OEM_7": 0x33, "OEM_8": 0x34, "OEM_9": 0x36, "OEM_10": 0x37, "OEM_11": 0x38,
    # "#" of an ISO board, Non-US # and ~, and "\" beside its left Shift,
    # Non-US \ and |.
    "EUR_1": 0x32, "EUR_2": 0x64,
    # Yen (International3), Ro (International1), Muhenkan (International5),
    # Henkan (International4), Katakana/Hiragana (International2).
    "JPN_1": 0x89, "JPN_2": 0x87, "JPN_3": 0x8B, "JPN_4": 0x8A, "JPN_5": 0x88,
}
for k, v in list(LETTERS.items()) + list(DIGITS.items()) + list(FKEYS.items()):
    RZKEY_USAGE[k] = v
for n in range(1, 10):
    RZKEY_USAGE["NUMPAD%d" % n] = 0x59 + n - 1

# LogiLed::KeyName -> usage. 0 for a G-key, the logo and the badge.
LOGI_USAGE = {
    "ESC": 0x29, "PRINT_SCREEN": 0x46, "SCROLL_LOCK": 0x47, "PAUSE_BREAK": 0x48,
    "TILDE": 0x35, "ONE": 0x1E, "TWO": 0x1F, "THREE": 0x20, "FOUR": 0x21, "FIVE": 0x22,
    "SIX": 0x23, "SEVEN": 0x24, "EIGHT": 0x25, "NINE": 0x26, "ZERO": 0x27,
    "MINUS": 0x2D, "EQUALS": 0x2E, "BACKSPACE": 0x2A, "INSERT": 0x49, "HOME": 0x4A, "PAGE_UP": 0x4B,
    "NUM_LOCK": 0x53, "NUM_SLASH": 0x54, "NUM_ASTERISK": 0x55, "NUM_MINUS": 0x56,
    "TAB": 0x2B, "OPEN_BRACKET": 0x2F, "CLOSE_BRACKET": 0x30, "BACKSLASH": 0x31,
    "KEYBOARD_DELETE": 0x4C, "END": 0x4D, "PAGE_DOWN": 0x4E,
    "NUM_SEVEN": 0x5F, "NUM_EIGHT": 0x60, "NUM_NINE": 0x61, "NUM_PLUS": 0x57,
    "CAPS_LOCK": 0x39, "SEMICOLON": 0x33, "APOSTROPHE": 0x34, "ENTER": 0x28,
    "NUM_FOUR": 0x5C, "NUM_FIVE": 0x5D, "NUM_SIX": 0x5E,
    "LEFT_SHIFT": 0xE1, "COMMA": 0x36, "PERIOD": 0x37, "FORWARD_SLASH": 0x38, "RIGHT_SHIFT": 0xE5,
    "ARROW_UP": 0x52, "NUM_ONE": 0x59, "NUM_TWO": 0x5A, "NUM_THREE": 0x5B, "NUM_ENTER": 0x58,
    "LEFT_CONTROL": 0xE0, "LEFT_WINDOWS": 0xE3, "LEFT_ALT": 0xE2, "SPACE": 0x2C,
    "RIGHT_ALT": 0xE6, "RIGHT_WINDOWS": 0xE7, "APPLICATION_SELECT": 0x65, "RIGHT_CONTROL": 0xE4,
    "ARROW_LEFT": 0x50, "ARROW_DOWN": 0x51, "ARROW_RIGHT": 0x4F, "NUM_ZERO": 0x62, "NUM_PERIOD": 0x63,
    "G_LOGO": 0, "G_BADGE": 0,
}
for k, v in list(LETTERS.items()) + list(FKEYS.items()):
    LOGI_USAGE[k] = v
for n in range(1, 10):
    LOGI_USAGE["G_%d" % n] = 0


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", lambda m: " " * len(m.group(0)), text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def block(text, start_pattern):
    """The text between the braces that open after start_pattern."""
    m = re.search(start_pattern, text)
    if not m:
        sys.exit("not found: " + start_pattern)
    i = text.index("{", m.end())
    depth, j = 1, i + 1
    while depth:
        depth += {"{": 1, "}": -1}.get(text[j], 0)
        j += 1
    return text[i + 1:j - 1]


def enum_entries(body):
    return [(n, int(v, 0)) for n, v in re.findall(r"\b(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)", body)]


def const(body, name):
    m = re.search(r"const\s+RZSIZE\s+" + name + r"\s*=\s*(\d+)\s*;", body)
    if not m:
        sys.exit("no const " + name)
    return int(m.group(1))


def commit_of(root):
    return subprocess.run(["git", "-C", root, "rev-parse", "HEAD"],
                          capture_output=True, text=True, check=True).stdout.strip()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("chroma_sample")
    parser.add_argument("colore")
    parser.add_argument("led_sdk")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    header_path = os.path.join(args.chroma_sample, "Razer", "RzChromaSDKTypes.h")
    header = strip_comments(read(header_path))
    keyboard = block(header, r"\bnamespace\s+Keyboard\b")
    mouse = block(header, r"\bnamespace\s+Mouse\b")
    headset = block(header, r"\bnamespace\s+Headset\b")
    mousepad = block(header, r"\bnamespace\s+Mousepad\b")
    keypad = block(header, r"\bnamespace\s+Keypad\b")
    link = block(header, r"\bnamespace\s+ChromaLink\b")

    sizes = {
        "KeyboardRows": const(keyboard, "MAX_ROW"), "KeyboardColumns": const(keyboard, "MAX_COLUMN"),
        "MouseRows": const(mouse, "MAX_ROW"), "MouseColumns": const(mouse, "MAX_COLUMN"),
        "KeypadRows": const(keypad, "MAX_ROW"), "KeypadColumns": const(keypad, "MAX_COLUMN"),
        "HeadsetLeds": const(headset, "MAX_LEDS"), "MousepadLeds": const(mousepad, "MAX_LEDS"),
        "ChromaLinkLeds": const(link, "MAX_LEDS"),
    }

    rzkeys = []
    seen = set()
    for name, code in enum_entries(block(keyboard, r"typedef\s+enum\s+RZKEY\b")):
        if name == "RZKEY_INVALID":
            continue
        if not name.startswith("RZKEY_"):
            sys.exit("unexpected RZKEY entry " + name)
        if code in seen:
            continue
        seen.add(code)
        short = name[len("RZKEY_"):]
        if short not in RZKEY_USAGE:
            sys.exit("no usage for " + name)
        row, column = code >> 8, code & 0xFF
        if row >= sizes["KeyboardRows"] or column >= sizes["KeyboardColumns"]:
            sys.exit("%s is outside the grid" % name)
        rzkeys.append((code, RZKEY_USAGE[short], name))
    logo = dict(enum_entries(block(keyboard, r"typedef\s+enum\s+RZLED\b")))["RZLED_LOGO"]
    if logo in seen:
        sys.exit("RZLED_LOGO shares a key's cell")
    leds2 = enum_entries(block(mouse, r"typedef\s+enum\s+RZLED2\b"))
    for name, code in leds2:
        if (code >> 8) >= sizes["MouseRows"] or (code & 0xFF) >= sizes["MouseColumns"]:
            sys.exit("%s is outside the mouse grid" % name)

    # Colore's copy of the codes must agree with the header's.
    colore_path = os.path.join(args.colore, "src", "Colore", "Effects", "Keyboard", "Key.cs")
    colore_codes = {int(v, 16) for v in re.findall(r"=\s*0x([0-9A-Fa-f]+)", strip_comments(read(colore_path)))}
    for code, _, name in rzkeys:
        if code not in colore_codes:
            sys.exit("%s (0x%04X) is not in Colore's Key.cs" % (name, code))
    if logo not in colore_codes:
        sys.exit("RZLED_LOGO is not in Colore's Key.cs")

    logi_path = os.path.join(args.led_sdk, "LED", "Include", "LogitechLEDLib.h")
    logi_text = read(logi_path)
    logi_sha = hashlib.sha256(open(logi_path, "rb").read()).hexdigest()
    logi = []
    for name, code in enum_entries(block(strip_comments(logi_text), r"\bnamespace\s+LogiLed\b").split("}KeyName")[0]):
        if name not in LOGI_USAGE:
            sys.exit("no usage for LogiLed::" + name)
        logi.append((code, LOGI_USAGE[name], name))

    chroma_commit = commit_of(args.chroma_sample)
    colore_commit = commit_of(args.colore)

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_sdk_key_tables.py from razerofficial/CSDK_ChromaModSample")
    out.append("//     at %s (Razer/RzChromaSDKTypes.h), checked against chroma-sdk/Colore" % chroma_commit[:10])
    out.append("//     at %s (src/Colore/Effects/Keyboard/Key.cs), and from the Logitech LED" % colore_commit[:10])
    out.append("//     SDK 9.00 (LED/Include/LogitechLEDLib.h, SHA-256 %s)." % logi_sha[:16])
    out.append("//     Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class ChromaLayouts")
    out.append("    {")
    out.append("        /// <summary>The CSDK_ChromaModSample commit the tables were read at.</summary>")
    out.append("        internal const string SampleCommit = \"%s\";" % chroma_commit)
    out.append("")
    for key in ("KeyboardRows", "KeyboardColumns", "MouseRows", "MouseColumns", "KeypadRows", "KeypadColumns",
                "HeadsetLeds", "MousepadLeds", "ChromaLinkLeds"):
        out.append("        internal const int %s = %d;" % (key, sizes[key]))
    out.append("")
    out.append("        /// <summary>RZLED_LOGO, the logo's cell in the keyboard grid.</summary>")
    out.append("        internal const ushort KeyboardLogo = 0x%04X;" % logo)
    out.append("")
    out.append("        /// <summary>The RZKEY enum in the header's order, each code once: the code")
    out.append("        /// (row in the high byte, column in the low byte), the key's HID usage")
    out.append("        /// (0 for none) and the name.</summary>")
    out.append("        internal static readonly (ushort Code, ushort Usage, string Name)[] KeyboardKeys =")
    out.append("        {")
    for code, usage, name in rzkeys:
        out.append("            (0x%04X, 0x%02X, \"%s\")," % (code, usage, name))
    out.append("        };")
    out.append("")
    out.append("        /// <summary>The RZLED2 enum of the mouse grid, in the header's order.</summary>")
    out.append("        internal static readonly (ushort Code, string Name)[] MouseLeds =")
    out.append("        {")
    for name, code in leds2:
        out.append("            (0x%04X, \"%s\")," % (code, name))
    out.append("        };")
    out.append("    }")
    out.append("")
    out.append("    internal static partial class LogiLedKeys")
    out.append("    {")
    out.append("        /// <summary>The SHA-256 of the LogitechLEDLib.h the table was read from.</summary>")
    out.append("        internal const string HeaderSha256 = \"%s\";" % logi_sha)
    out.append("")
    out.append("        /// <summary>LogiLed::KeyName in the header's order: the value")
    out.append("        /// LogiLedSetLightingForKeyWithKeyName takes, the key's HID usage (0 for")
    out.append("        /// none) and the name.</summary>")
    out.append("        internal static readonly (int Code, ushort Usage, string Name)[] Keys =")
    out.append("        {")
    for code, usage, name in logi:
        out.append("            (0x%X, 0x%02X, \"%s\")," % (code, usage, name))
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
    print("wrote %s: %d RZKEY codes, %d mouse LEDs, %d LogiLed keys" % (OUTPUT, len(rzkeys), len(leds2), len(logi)))


if __name__ == "__main__":
    main()
