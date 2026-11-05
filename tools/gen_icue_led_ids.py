"""The keyboard LED ids of Corsair's iCUE SDK (#557).

WHAT IT READS.
- The iCUE SDK's LED id header, src/include/iCUESDKLedIdEnum.h in
  CorsairOfficial/cue-sdk: the CorsairLedId_Keyboard enum, the LED index of
  each key in the CLG_Keyboard group (a LED id is the group in bits 31 to 16
  and the index in bits 15 to 0, docs/index.html "LED identification"), and
  the CorsairLedGroup enum.
- RGB.NET's copy of the same enum, RGB.NET.Devices.Corsair/Enum/
  CorsairLedIdKeyboard.cs in DarthAffe/RGB.NET. The script stops when an
  index of the header is missing there or sits at another value, so the two
  never drift apart unseen. RGB.NET spells the digits out (One for CLK_1).

WHAT IT ADDS. The HID usage on the Keyboard/Keypad page (0x07, HID Usage
Tables 1.6, section 10) of each key, from the table below, whose names are
the usage names of that section. The script stops when the header names a
key the table lacks, so a new key is never dropped unseen. A key with no
keyboard-page usage maps to 0 and carries the label the chip shows, or none.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/IcueLedIds.g.cs, with
CRLF line endings.

Run it without Python's -I switch: it imports tools/openrgb_cpp.py.

Usage:
    python tools/gen_icue_led_ids.py CUE_SDK_ROOT RGBNET_ROOT
    python tools/gen_icue_led_ids.py CUE_SDK_ROOT RGBNET_ROOT --check
"""
import argparse
import os
import re
import sys
import openrgb_cpp as oc

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "IcueLedIds.g.cs")

LETTERS = {chr(ord("A") + i): 0x04 + i for i in range(26)}
DIGITS = {str(d): 0x1E + d - 1 for d in range(1, 10)}
DIGITS["0"] = 0x27
FKEYS = {"F%d" % n: 0x3A + n - 1 for n in range(1, 13)}

# CLK_ suffix -> (usage, label). Label is a LightLabel member name or None.
USAGE = {
    "Escape": 0x29, "GraveAccentAndTilde": 0x35, "MinusAndUnderscore": 0x2D, "EqualsAndPlus": 0x2E,
    "Backspace": 0x2A, "Tab": 0x2B, "BracketLeft": 0x2F, "BracketRight": 0x30, "CapsLock": 0x39,
    "SemicolonAndColon": 0x33, "ApostropheAndDoubleQuote": 0x34, "Backslash": 0x31, "Enter": 0x28,
    "LeftShift": 0xE1, "NonUsBackslash": 0x64, "CommaAndLessThan": 0x36, "PeriodAndBiggerThan": 0x37,
    "SlashAndQuestionMark": 0x38, "RightShift": 0xE5, "LeftCtrl": 0xE0, "LeftGui": 0xE3, "LeftAlt": 0xE2,
    "Space": 0x2C, "RightAlt": 0xE6, "RightGui": 0xE7, "Application": 0x65, "RightCtrl": 0xE4,
    "Lang1": 0x90, "Lang2": 0x91,
    "International1": 0x87, "International2": 0x88, "International3": 0x89, "International4": 0x8A,
    "International5": 0x8B,
    "PrintScreen": 0x46, "ScrollLock": 0x47, "PauseBreak": 0x48, "Insert": 0x49, "Home": 0x4A,
    "PageUp": 0x4B, "Delete": 0x4C, "End": 0x4D, "PageDown": 0x4E,
    "UpArrow": 0x52, "LeftArrow": 0x50, "DownArrow": 0x51, "RightArrow": 0x4F,
    "NonUsTilde": 0x32, "NumLock": 0x53, "KeypadSlash": 0x54, "KeypadAsterisk": 0x55, "KeypadMinus": 0x56,
    "KeypadPlus": 0x57, "KeypadEnter": 0x58, "KeypadComma": 0x85, "KeypadPeriodAndDelete": 0x63,
    "Keypad0": 0x62,
}
for k, v in list(LETTERS.items()) + list(DIGITS.items()) + list(FKEYS.items()):
    USAGE[k] = v
for n in range(1, 10):
    USAGE["Keypad%d" % n] = 0x59 + n - 1

# Lights with no keyboard-page usage. The media keys send consumer-page
# usages, so they carry the label the chip shows. The rest have no label yet.
LABEL = {
    "Brightness": "Brightness", "Mute": "Mute", "Stop": "Stop", "ScanPreviousTrack": "Previous",
    "PlayPause": "PlayPause", "ScanNextTrack": "Next", "Fn": "Fn",
    "LedProgramming": None, "WinLock": None, "VolumeUp": None, "VolumeDown": None,
    "MR": None, "M1": None, "M2": None, "M3": None,
}

RGBNET_DIGITS = {"One": "1", "Two": "2", "Three": "3", "Four": "4", "Five": "5", "Six": "6", "Seven": "7",
                 "Eight": "8", "Nine": "9", "Zero": "0"}


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def block(text, start_pattern):
    m = re.search(start_pattern, text)
    if not m:
        sys.exit("not found: " + start_pattern)
    i = text.index("{", m.end() - 1)
    j = text.index("}", i)
    return text[i + 1:j]


def entries(body):
    body = re.sub(r"//[^\n]*", "", body)
    return [(n, int(v, 0)) for n, v in re.findall(r"\b(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)", body)]


def commit_of(root):
    return oc.git_head(root)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("cue_sdk")
    parser.add_argument("rgbnet")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    header = read(os.path.join(args.cue_sdk, "src", "include", "iCUESDKLedIdEnum.h"))
    groups = entries(block(header, r"enum\s+CorsairLedGroup\b"))
    keys = entries(block(header, r"enum\s+CorsairLedId_Keyboard\b"))

    rgbnet = read(os.path.join(args.rgbnet, "RGB.NET.Devices.Corsair", "Enum", "CorsairLedIdKeyboard.cs"))
    rgbnet_keys = {RGBNET_DIGITS.get(n, n): v for n, v in entries(block(rgbnet, r"enum\s+CorsairLedIdKeyboard\b"))}

    rows = []
    for name, index in keys:
        if not name.startswith("CLK_"):
            sys.exit("unexpected entry " + name)
        key = name[4:]
        if key == "Invalid":
            continue
        if rgbnet_keys.get(key) != index:
            sys.exit("%s (%d) differs in RGB.NET's CorsairLedIdKeyboard.cs" % (name, index))
        if key in USAGE:
            rows.append((index, USAGE[key], None, name))
        elif key in LABEL:
            rows.append((index, 0, LABEL[key], name))
        else:
            sys.exit("no usage or label for " + name)

    if dict(groups).get("CLG_Keyboard") != 0:
        sys.exit("CLG_Keyboard is not 0")

    sdk_commit = commit_of(args.cue_sdk)
    rgbnet_commit = commit_of(args.rgbnet)
    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_icue_led_ids.py from CorsairOfficial/cue-sdk")
    out.append("//     at %s (src/include/iCUESDKLedIdEnum.h), checked against DarthAffe/RGB.NET" % sdk_commit[:10])
    out.append("//     at %s (RGB.NET.Devices.Corsair/Enum/CorsairLedIdKeyboard.cs)." % rgbnet_commit[:10])
    out.append("//     Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals")
    out.append("{")
    out.append("    internal static partial class IcueLeds")
    out.append("    {")
    out.append("        /// <summary>The cue-sdk commit the table was read at.</summary>")
    out.append("        internal const string SdkCommit = \"%s\";" % sdk_commit)
    out.append("")
    out.append("        /// <summary>The CorsairLedGroup enum: each group's number.</summary>")
    for name, value in groups:
        out.append("        internal const int %s = %d;" % (name.replace("CLG_", "Group").replace("_", ""), value))
    out.append("")
    out.append("        /// <summary>CorsairLedId_Keyboard in the header's order, without CLK_Invalid:")
    out.append("        /// the key's LED index in the CLG_Keyboard group, its HID usage (0 for")
    out.append("        /// none), the label a light with no usage shows, and the name.</summary>")
    out.append("        internal static readonly (int Index, ushort Usage, LightLabel Label, string Name)[] KeyboardKeys =")
    out.append("        {")
    for index, usage, label, name in rows:
        out.append("            (%d, 0x%02X, LightLabel.%s, \"%s\")," % (index, usage, label or "None", name))
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
    print("wrote %s: %d keyboard LEDs, %d groups" % (OUTPUT, len(rows), len(groups)))


if __name__ == "__main__":
    main()
