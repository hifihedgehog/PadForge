"""The light tables of the OpenRGB keyboard families that lay their keys out
without KeyboardLayoutManager (#557).

WHAT IT READS, under OpenRGB's Controllers folder, each array as the compiler
sees it (tools/openrgb_cpp.py), with OpenRGB's key names from
RGBController/RGBControllerKeyNames.cpp:
- MountainKeyboardController/RGBController_MountainKeyboard.cpp: led_names,
  the four zone lookup tables and their enums (the Everest).
- ZETKeyboardController: led_names, and SKIP_INDICES from the controller.
- XPGSummonerKeyboardController/RGBController_XPGSummoner.cpp: led_names and
  matrix_map.
- OKSController/RGBController_OKSKeyboard.cpp: led_names.
- LightSaltController: the keyboard's and the keypad's led_names,
  led_indices and led_deflections.
- DarkProject: led_names, and packet_map from the controller.
- MNTKeyboardController: the Reform's and the Pocket Reform's led_names.
- FnaticStreakController/RGBController_FnaticStreak.cpp: led_names_full and
  led_names_mini.
- ThermaltakePoseidonZRGBController: led_names, and keys from the
  controller.
- KeychronKeyboardController/RGBController_KeychronKeyboard.cpp: the matrix,
  the names and the buffer places of the k3_keychron struct, its third,
  fourth and fifth members.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/HidFamilies/KeyboardLedNames.g.cs,
each array as OpenRGB holds it, a name as its string and NA as -1, with
CRLF line endings.

Run it without Python's -I switch: it imports tools/openrgb_cpp.py.

Usage:
    python tools/gen_keyboard_led_names.py OPENRGB_ROOT
    python tools/gen_keyboard_led_names.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import openrgb_cpp as oc  # noqa: E402
from openrgb_cpp import CppError, Node  # noqa: E402

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "KeyboardLedNames.g.cs")

NA = 0xFFFFFFFF

# (C# name, file under Controllers, array, kind, summary[, member]): member
# is the index of the braced member of a struct the table is.
TABLES = [
    ("MountainNames", "MountainKeyboardController/RGBController_MountainKeyboard.cpp", "led_names", "str",
     "The Everest's names by color index, led_names."),
    ("MountainKeyLut", "MountainKeyboardController/RGBController_MountainKeyboard.cpp", "matrix_tkl_map_lut", "int",
     "The Everest's main keys in light order, each its color index, matrix_tkl_map_lut."),
    ("MountainNumpadLut", "MountainKeyboardController/RGBController_MountainKeyboard.cpp", "matrix_num_map_lut", "int",
     "The Everest's number pad keys in light order, matrix_num_map_lut."),
    ("MountainEdgeLut", "MountainKeyboardController/RGBController_MountainKeyboard.cpp", "matrix_edge_tkl_map_lut", "int",
     "The Everest's edge lights in light order, each its edge color index, matrix_edge_tkl_map_lut."),
    ("MountainNumpadEdgeLut", "MountainKeyboardController/RGBController_MountainKeyboard.cpp",
     "matrix_edge_numpad_map_lut", "int",
     "The number pad's edge lights in light order, matrix_edge_numpad_map_lut."),
    ("ZetNames", "ZETKeyboardController/RGBController_ZETBladeOptical.cpp", "led_names", "str",
     "The Blade Optical's names in light order, led_names."),
    ("ZetSkips", "ZETKeyboardController/ZETBladeOpticalController.cpp", "SKIP_INDICES", "int",
     "The key IDs the Blade Optical's frames step over, SKIP_INDICES."),
    ("XpgNames", "XPGSummonerKeyboardController/RGBController_XPGSummoner.cpp", "led_names", "str",
     "The Summoner's names in light order, led_names."),
    ("XpgMatrix", "XPGSummonerKeyboardController/RGBController_XPGSummoner.cpp", "matrix_map", "int",
     "The Summoner's 6 by 21 grid, each cell the light's index in the frame, matrix_map."),
    ("OksNames", "OKSController/RGBController_OKSKeyboard.cpp", "led_names", "str",
     "The Optical Axis's 6 by 21 names, led_names."),
    ("LightSaltKeyboardNames", "LightSaltController/RGBController_LightSaltKeyboard.cpp", "led_names", "str",
     "The LightSalt keyboard's names, led_names."),
    ("LightSaltKeyboardIndices", "LightSaltController/RGBController_LightSaltKeyboard.cpp", "led_indices", "int",
     "The keyboard's cells, row times 10 plus column, led_indices."),
    ("LightSaltKeyboardDeflections", "LightSaltController/RGBController_LightSaltKeyboard.cpp", "led_deflections",
     "int", "The keyboard's per-set lightening bits, led_deflections."),
    ("LightSaltKeypadNames", "LightSaltController/RGBController_LightSaltKeypad.cpp", "led_names", "str",
     "The LightSalt keypad's names, led_names."),
    ("LightSaltKeypadIndices", "LightSaltController/RGBController_LightSaltKeypad.cpp", "led_indices", "int",
     "The keypad's cells, led_indices."),
    ("LightSaltKeypadDeflections", "LightSaltController/RGBController_LightSaltKeypad.cpp", "led_deflections", "int",
     "The keypad's per-set lightening bits, led_deflections."),
    ("DarkProjectNames", "DarkProject/RGBController_DarkProjectKeyboard.cpp", "led_names", "str",
     "The KD3B V2's names in light order, led_names."),
    ("DarkProjectPacketMap", "DarkProject/DarkProjectKeyboardController.cpp", "packet_map", "int",
     "Each light's place in the frames, packet_map."),
    ("MntReformNames", "MNTKeyboardController/RGBController_MNTReformKeyboard.cpp", "led_names_MNTReform", "str",
     "The Reform keyboard's 6 by 14 names, led_names_MNTReform."),
    ("MntPocketReformNames", "MNTKeyboardController/RGBController_MNTPocketReformKeyboard.cpp",
     "led_names_MNTPocketReform", "str", "The Pocket Reform keyboard's 6 by 12 names, led_names_MNTPocketReform."),
    ("FnaticFullNames", "FnaticStreakController/RGBController_FnaticStreak.cpp", "led_names_full", "str",
     "The Streak's names by LED index, led_names_full."),
    ("FnaticMiniNames", "FnaticStreakController/RGBController_FnaticStreak.cpp", "led_names_mini", "str",
     "The miniStreak's names by LED index, led_names_mini."),
    ("PoseidonNames", "ThermaltakePoseidonZRGBController/RGBController_ThermaltakePoseidonZRGB.cpp", "led_names",
     "str", "The Poseidon Z RGB's names in light order, led_names."),
    ("PoseidonKeys", "ThermaltakePoseidonZRGBController/ThermaltakePoseidonZRGBController.cpp", "keys", "int",
     "Each light's byte in the color reports, keys."),
    ("KeychronMatrix", "KeychronKeyboardController/RGBController_KeychronKeyboard.cpp", "k3_keychron", "int",
     "The K3 V2's 6 by 16 grid, each cell the index of its name, the matrix of k3_keychron.", 2),
    ("KeychronNames", "KeychronKeyboardController/RGBController_KeychronKeyboard.cpp", "k3_keychron", "str",
     "The K3 V2's names, the led_names of k3_keychron.", 3),
    ("KeychronPlaces", "KeychronKeyboardController/RGBController_KeychronKeyboard.cpp", "k3_keychron", "int",
     "Each LED's place in the color buffer, in LED order, the led_sequence_positions of k3_keychron.", 4),
]

def key_names(root):
    path = os.path.join(root, "RGBController", "RGBControllerKeyNames.cpp")
    with open(path, encoding="utf-8-sig") as f:
        text = f.read()
    names = {}
    for name, literal in re.findall(r'const\s+char\s*\*\s*(\w+)\s*=\s*("(?:\\.|[^"\\])*")\s*;', text):
        names[name] = oc.decode_string(literal)
    if "KEY_EN_ESCAPE" not in names or names.get("KEY_EN_UNUSED") != "":
        sys.exit("RGBControllerKeyNames.cpp did not read as expected")
    return names


def definition(source, name):
    """The parsed braces of the top-level array called name and the line its
    name sits on. Unlike Source.definition it steps over every [n] after the
    name, so a grid declared [6][21] is found too."""
    toks = source.tokens
    depth = 0
    for i, t in enumerate(toks):
        if t.text == "{":
            depth += 1
        elif t.text == "}":
            depth -= 1
        elif depth == 0 and t.kind == "id" and t.text == name and i > 0 and toks[i - 1].kind in ("id", "op"):
            j = i + 1
            while toks[j].text == "[":
                while toks[j].text != "]":
                    j += 1
                j += 1
            if toks[j].text == "=":
                j += 1
            if toks[j].text == "{":
                node, _ = oc.parse_braces(toks, j)
                return node, t.line
    raise CppError("%s: no definition of %s" % (source.rel, name))


def flatten(node):
    for element in node:
        if isinstance(element, Node):
            yield from flatten(element)
        else:
            yield element


def cs_string(value):
    out = []
    for ch in value:
        if ch == "\\":
            out.append("\\\\")
        elif ch == "\"":
            out.append("\\\"")
        elif ord(ch) < 0x20 or ord(ch) > 0x7E:
            out.append("\\u%04X" % ord(ch))
        else:
            out.append(ch)
    return "\"" + "".join(out) + "\""


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    root = args.openrgb
    commit = oc.git_head(root)
    names = key_names(root)
    sources = {}

    out = []
    out.append("// <auto-generated>")
    out.append("//     Generated by tools/gen_keyboard_led_names.py from OpenRGB at")
    out.append("//     %s (the light tables of the keyboard families that lay their" % commit[:10])
    out.append("//     keys out without KeyboardLayoutManager, with the key names of")
    out.append("//     RGBController/RGBControllerKeyNames.cpp). Do not edit by hand.")
    out.append("// </auto-generated>")
    out.append("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    out.append("{")
    out.append("    internal static partial class KeyboardLedNames")
    out.append("    {")
    out.append("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    out.append("        internal const string OpenRgbCommit = \"%s\";" % commit)
    counts = []
    for cs_name, rel, array, kind, summary, *member in TABLES:
        if rel not in sources:
            source = oc.Source(os.path.join(root, "Controllers"), rel)
            evaluator = oc.Evaluator([source], strings=names)
            oc.add_enum_constants(evaluator, source)
            sources[rel] = (source, evaluator)
        source, evaluator = sources[rel]
        try:
            node, line = definition(source, array)
        except CppError as e:
            sys.exit(str(e))
        if member:
            node = node[member[0]]
            if not isinstance(node, Node):
                sys.exit("%s: member %d of %s is not braced" % (rel, member[0], array))
            line = node.line
        values = []
        for element in flatten(node):
            if kind == "str":
                values.append(evaluator.string(element))
            else:
                v = evaluator.integer(element)
                values.append(-1 if v == NA else v)
        counts.append("%s %d" % (cs_name, len(values)))
        out.append("")
        out.append("        /// <summary>%s Controllers/%s:%d.</summary>" % (summary, rel, line))
        if kind == "str":
            out.append("        internal static readonly string[] %s =" % cs_name)
            out.append("        {")
            for v in values:
                out.append("            %s," % cs_string(v))
        else:
            out.append("        internal static readonly int[] %s =" % cs_name)
            out.append("        {")
            for i in range(0, len(values), 16):
                out.append("            " + ", ".join(str(v) for v in values[i:i + 16]) + ",")
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
    print("wrote %s: %s" % (OUTPUT, ", ".join(counts)))


if __name__ == "__main__":
    main()
