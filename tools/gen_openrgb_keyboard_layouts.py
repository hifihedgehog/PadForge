"""The keyboard layouts of OpenRGB's KeyboardLayoutManager, for PadForge (#557).

OpenRGB builds a keyboard's key grid in KeyboardLayoutManager
(KeyboardLayoutManager/KeyboardLayoutManager.cpp): four base blocks put
together by size (main keys, the F-key row, the navigation block, the number
pad), a regional overlay (ANSI, ISO, QWERTZ, AZERTY, JIS, ABNT2), a list of
LED values, a regional fix list from the caller, and then the caller's own
per-model edit lists. Fifteen of its families build their keyboards this way.
PadForge.App/Common/Input/Peripherals/HidFamilies/KeyboardLayoutManager.cs is
a line-for-line port of the algorithm. This script reads its inputs from
OpenRGB at a commit and writes them to KeyboardLayouts.g.cs beside it:

- the base blocks and the six regional overlays of KeyboardLayoutManager.cpp,
- the enums of KeyboardLayoutManager.h, so a test can hold the port's enums
  to them,
- one recipe per layout a family builds: the size, the LED values, the
  regional fixes and the edit lists, in the order and under the condition the
  family's SetupZones passes them, with every region the family can pick,
- one row per model naming its recipe: Razer, Cooler Master and Corsair by
  product ID, ASUS Aura Core by laptop and zone, Sinowealth by model byte,
  Logitech by product ID, the rest by the rule their code picks with.

Winbond fills its LED values in code (InitLayouts in
RGBController_WinbondGamingKeyboard.cpp), so this script ports that function
and stops when its text at OpenRGB differs from the text it was ported from.

--harness also writes tools/openrgb_klm/klm_harness.cpp, a C++ program built
from OpenRGB's own KeyboardLayoutManager.cpp and the families' own tables that
dumps every recipe in every region it can take (tools/openrgb_klm/build.cmd).
Its dump, tools/openrgb_klm/klm_dump.tsv, is the fixture PadForge's port is
tested against cell by cell.

Usage:
    python tools/gen_openrgb_keyboard_layouts.py OPENRGB_ROOT [--harness]
    python tools/gen_openrgb_keyboard_layouts.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import hashlib
import os
import re
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError, Expr, Node

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies", "KeyboardLayouts.g.cs")
HARNESS = os.path.join(PROJ_ROOT, "tools", "openrgb_klm", "klm_harness.cpp")

KLM_CPP = "KeyboardLayoutManager/KeyboardLayoutManager.cpp"
KLM_H = "KeyboardLayoutManager/KeyboardLayoutManager.h"
KEY_NAMES = "RGBController/RGBControllerKeyNames.cpp"
RGB_INTERFACE = "RGBController/RGBControllerInterface.h"

C = "Controllers/"
AOC = C + "AOCKeyboardController/RGBController_AOCKeyboard.cpp"
ASUS = C + "AsusAuraCoreController/AsusAuraCoreLaptopController/AsusAuraCoreLaptopDevices.cpp"
CLEVO = C + "ClevoKeyboardController/ClevoKeyboardDevices.cpp"
CM = C + "CoolerMasterController/CMKeyboardController/CMKeyboardDevices.cpp"
CM_H = C + "CoolerMasterController/CMKeyboardController/CMKeyboardDevices.h"
CORSAIR = C + "CorsairPeripheralV2Controller/CorsairPeripheralV2Devices.cpp"
CORSAIR_H = C + "CorsairPeripheralV2Controller/CorsairPeripheralV2Devices.h"
HYTE = C + "HYTEKeyboardController/RGBController_HYTEKeyboard.cpp"
HYPERX = C + "HyperXKeyboardController/HyperXAlloyOriginsCoreController/RGBController_HyperXAlloyOriginsCore.cpp"
LOGI = C + "LogitechController/LogitechHIDPP20Controller/RGBController_LogitechHIDPP20.cpp"
MINTAKA = C + "MintakaKeyboardController/RGBController_MintakaKeyboard.cpp"
MOUNTAIN = C + "MountainKeyboardController/RGBController_Mountain60Keyboard.cpp"
RAZER = C + "RazerController/RazerDevices.cpp"
RAZER_H = C + "RazerController/RazerDevices.h"
SINO = C + "SinowealthController/SinowealthKeyboard10cController/SinowealthKeyboard10cDevices.cpp"
SKYLOONG = C + "SkyloongController/RGBController_SkyloongGK104Pro.cpp"
WINBOND = C + "WinbondGamingKeyboardController/RGBController_WinbondGamingKeyboard.cpp"
WITMOD = C + "WitmodKeyboardController/RGBController_WitmodKeyboard.cpp"
WITMOD_H = C + "WitmodKeyboardController/RGBController_WitmodKeyboard.h"

LAYOUT_NAMES = ["Default", "AnsiQwerty", "IsoQwerty", "IsoQwertz", "IsoAzerty", "Jis", "Abnt2"]
OPCODE_NAMES = ["InsertShiftRight", "SwapOnly", "RemoveShiftLeft", "InsShftAdjacent", "InsertRow", "RemoveRow", "AddAltName"]


def pascal(name):
    return "".join(p[:1].upper() + p[1:] for p in re.split(r"[_\W]+", name) if p)


class Reader:
    """The OpenRGB sources, the constants they share and the typed readers
    of their layout tables."""

    def __init__(self, root):
        self.root = root
        self.sources = {}
        klm_h = self.src(KLM_H)
        zone_bits = oc.enum_values(klm_h, "KEYBOARD_ZONE_BITS")
        self.sizes = oc.enum_values(klm_h, "KEYBOARD_SIZE", zone_bits)
        self.layouts = oc.enum_values(klm_h, "KEYBOARD_LAYOUT")
        self.opcodes = oc.enum_values(klm_h, "KEYBOARD_OPCODE")
        self.fills = oc.enum_values(klm_h, "KEYBOARD_MAP_FILL_TYPE")
        self.zone_bits = zone_bits
        self.zone_types = oc.enum_values(self.src(RGB_INTERFACE), "ZONE_TYPE_SINGLE")
        constants = {}
        for table, prefix in ((zone_bits, None), (self.sizes, "KEYBOARD_SIZE"), (self.layouts, "KEYBOARD_LAYOUT"),
                              (self.opcodes, "KEYBOARD_OPCODE"), (self.zone_types, "zone_type")):
            for k, v in table.items():
                constants[k] = v
                if prefix:
                    constants[prefix + "::" + k] = v
        self.constants = constants
        self.key_names = {}
        names = self.src(KEY_NAMES)
        toks = names.tokens
        for i, t in enumerate(toks):
            if t.kind == "id" and toks[i + 1].text == "=" and toks[i + 2].kind == "str":
                self.key_names[t.text] = oc.decode_string(toks[i + 2].text)
        self.key_names["NULL"] = None
        # The OpenRGB name of each value list and key set read from a
        # definition of its own, so the C# tables keep it.
        self.names = {}

    def src(self, rel):
        if rel not in self.sources:
            self.sources[rel] = oc.Source(self.root, rel)
        return self.sources[rel]

    def ev(self, *rels):
        return oc.Evaluator([self.src(r) for r in rels], self.constants, self.key_names)

    # -- typed readers -------------------------------------------------

    def led(self, node, ev):
        if not isinstance(node, Node) or len(node) != 7:
            raise CppError("line %d: a keyboard_led has 7 fields" % node.line)
        zone, row, col, value = (ev.integer(node[i]) for i in range(4))
        name = ev.string(node[4])
        alt = ev.string(node[5])
        opcode = ev.integer(node[6])
        for f, v in (("zone", zone), ("row", row), ("col", col)):
            if not 0 <= v <= 255:
                raise CppError("line %d: %s %d is not a byte" % (node.line, f, v))
        if not 0 <= value <= 0xFFFFFFFF:
            raise CppError("line %d: value %d" % (node.line, value))
        return (zone, row, col, value, name, alt, opcode)

    def key_set(self, element, rel, ev):
        """A key_set: braces of keyboard_led, or the name of one."""
        if isinstance(element, Expr):
            return self.named_key_set(rel, self.ref(element), ev)
        return [self.led(n, ev) for n in element]

    def named_key_set(self, rel, name, ev):
        node, _ = self.src(rel).definition(name)
        keys = [self.led(n, ev) for n in node]
        self.names.setdefault(("Keys", repr(keys)), name)
        return keys

    def values_vector(self, element, rel, ev):
        name = None
        if isinstance(element, Expr):
            name = self.ref(element)
            node, _ = self.src(rel).definition(name)
            element = node
        values = [ev.integer(e) for e in element]
        if name:
            self.names.setdefault(("Values", repr(values)), name)
        return values

    def overlay_map(self, element, rel, ev):
        """A std::map<KEYBOARD_LAYOUT, key_set>: the first entry of a
        layout listed twice wins, as std::map's initializer keeps it, and
        the entries come back in key order."""
        if isinstance(element, Expr):
            node, _ = self.src(rel).definition(self.ref(element))
            element = node
        out = {}
        for pair in element:
            if not isinstance(pair, Node) or len(pair) > 2:
                raise CppError("line %d: a regional overlay entry is a layout and a key set" % element.line)
            # Braces that leave members out value-initialize them: {} is
            # the Default layout with no keys.
            layout = ev.integer(pair[0]) if len(pair) > 0 else 0
            keys = self.key_set(pair[1], rel, ev) if len(pair) > 1 else []
            out.setdefault(layout, keys)
        return sorted(out.items())

    def layout_values(self, element, rel, ev):
        """layout_values: default_values, then regional_overlay. Members
        the braces leave out are empty."""
        if isinstance(element, Expr):
            node, _ = self.src(rel).definition(self.ref(element))
            element = node
        defaults = self.values_vector(element[0], rel, ev) if len(element) > 0 else []
        overlays = self.overlay_map(element[1], rel, ev) if len(element) > 1 else []
        if len(element) > 2:
            raise CppError("line %d: layout_values has two members" % element.line)
        return (defaults, overlays)

    def overlay_values(self, name, rel, ev):
        """keyboard_keymap_overlay_values: base_size, key_values, edit_keys."""
        node, line = self.src(rel).definition(name)
        if len(node) > 3:
            raise CppError("%s:%d: keyboard_keymap_overlay_values has three members" % (rel, line))
        size = ev.integer(node[0]) if len(node) > 0 else 0
        values = self.layout_values(node[1], rel, ev) if len(node) > 1 else ([], [])
        edits = self.key_set(node[2], rel, ev) if len(node) > 2 else []
        return size, values, edits, line

    @staticmethod
    def ref(expr):
        """The name an element points at: name or &name."""
        toks = [t.text for t in expr]
        if toks and toks[0] == "&":
            toks = toks[1:]
        if len(toks) != 1 or not re.match(r"^[A-Za-z_]\w*$", toks[0]):
            raise CppError("line %d: not a name: %r" % (expr.line, toks))
        return toks[0]


class Recipe:
    """One layout a family builds: KeyboardLayoutManager(layout, size,
    values), then ChangeKeys for each edit list, as the family's code does.
    cpp_build is the same build in C++, from the family's own names, for the
    harness."""

    def __init__(self, family, name, source, size, values, edits, layouts, cpp_build, cpp_setup=""):
        self.family, self.name, self.source = family, name, source
        self.size, self.values, self.edits, self.layouts = size, values, edits, layouts
        self.cpp_build, self.cpp_setup = cpp_build, cpp_setup


class Model:
    def __init__(self, family, model, product_id, key, recipe, zone="", rows=0, cols=0):
        self.family, self.model, self.product_id, self.key = family, model, product_id, key
        self.recipe, self.zone, self.rows, self.cols = recipe, zone, rows, cols


def is_null(element):
    """Whether an element is a null pointer."""
    return isinstance(element, Expr) and len(element) == 1 and element[0].text in ("NULL", "nullptr")


def cite(rd, rel, name):
    return "%s:%d" % (rel.split("/")[-1], rd.src(rel).line_of(name))


def overlay_values_recipes(rd, family, rel, names, layouts, condition):
    """Recipes for families that hand KeyboardLayoutManager a
    keyboard_keymap_overlay_values: its base size and values, then the
    overlay itself as the one edit list when condition holds. condition is
    "always", "sized" (base_size != EMPTY) or "sized_or_edits" (base_size !=
    EMPTY or edit_keys not empty)."""
    ev = rd.ev(rel, rel.replace(".cpp", ".h") if os.path.exists(rd.root + "/" + rel.replace(".cpp", ".h")) else rel)
    out = []
    for name in names:
        size, values, edits, line = rd.overlay_values(name, rel, ev)
        if condition == "always":
            apply = True
            cpp_if = ""
        elif condition == "sized":
            apply = size != rd.sizes["KEYBOARD_SIZE_EMPTY"]
            cpp_if = "if(%s.base_size != KEYBOARD_SIZE_EMPTY) " % name
        else:
            apply = size != rd.sizes["KEYBOARD_SIZE_EMPTY"] or len(edits) > 0
            cpp_if = "if(%s.base_size != KEYBOARD_SIZE_EMPTY || %s.edit_keys.size() > 0) " % (name, name)
        cpp = ("KeyboardLayoutManager k(l, %s.base_size, %s.key_values); %sk.ChangeKeys(%s);"
               % (name, name, cpp_if, name))
        out.append(Recipe(family, name, "%s:%d" % (rel.split("/")[-1], line), size, values,
                          [edits] if apply else [], layouts, cpp))
    return out


def struct_list(rd, rel, type_name):
    """Every top-level definition of type_name in rel: (name, node)."""
    s = rd.src(rel)
    return [(n, s.definition(n)[0]) for n in s.definitions_of_type(type_name)]


def device_list(rd, rel, list_name):
    """The devices a device list array points at, in its order: (name,
    node). A device defined but left off the list is never built."""
    node, _ = rd.src(rel).definition(list_name)
    out = []
    for element in node:
        if is_null(element):
            continue
        name = rd.ref(element)
        out.append((name, rd.src(rel).definition(name)[0]))
    return out


# ---------------------------------------------------------------------
# The families, each read the way its SetupZones builds its keyboard.
# ---------------------------------------------------------------------

def family_aoc(rd):
    ev = rd.ev(AOC)
    L = rd.layouts
    values = rd.layout_values(Expr([oc.Token("id", "aoc_keyboard_offset_values", 0)]), AOC, ev)
    # RGBController_AOCKeyboard.cpp:267
    r = Recipe("AOCKeyboard", "aoc_keyboard_offset_values", cite(rd, AOC, "aoc_keyboard_offset_values"),
               rd.sizes["KEYBOARD_SIZE_FULL"], values, [], [L["KEYBOARD_LAYOUT_ANSI_QWERTY"]],
               "KeyboardLayoutManager k(l, KEYBOARD_SIZE_FULL, aoc_keyboard_offset_values);")
    return [r], [Model("AOCKeyboard", "AOC keyboard", -1, "", r.name)]


def family_asus(rd):
    L = rd.layouts
    names = rd.src(ASUS).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_AsusAuraCoreLaptop.cpp:290-299 picks ISO or ANSI, 322-333 builds.
    recipes = overlay_values_recipes(rd, "AsusAuraCore", ASUS, names,
                                     [L["KEYBOARD_LAYOUT_ANSI_QWERTY"], L["KEYBOARD_LAYOUT_ISO_QWERTY"]],
                                     "sized_or_edits")
    ev = rd.ev(ASUS)
    zones = {}
    for name, node in struct_list(rd, ASUS, "aura_core_laptop_zone"):
        zones[name] = (ev.string(node[0]), None if is_null(node[1]) else rd.ref(node[1]))
    models = []
    # Lines 306-321: each zone with a layout builds it.
    for name, node in device_list(rd, ASUS, "aura_core_laptop_device_list_data"):
        dmi = ev.string(node[0])
        for z in node[1]:
            if is_null(z):
                continue
            zone_name, layout = zones[rd.ref(z)]
            if layout:
                models.append(Model("AsusAuraCore", dmi, -1, dmi, layout, zone_name))
    return recipes, models


def family_clevo(rd):
    L = rd.layouts
    names = rd.src(CLEVO).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_ClevoKeyboard.cpp:392-399: ISO QWERTY, ChangeKeys always.
    recipes = overlay_values_recipes(rd, "ClevoKeyboard", CLEVO, names, [L["KEYBOARD_LAYOUT_ISO_QWERTY"]], "always")
    models = [
        Model("ClevoKeyboard", "Clevo ITE 829x", -1, "CLEVO_KB_ITE829X", "clevo_829x_keyboard_layout"),
        Model("ClevoKeyboard", "TUXEDO Stellaris 16 Gen7 AMD", -1, "STELLARIS16A07", "clevo_stellaris16a07_keyboard_layout"),
        Model("ClevoKeyboard", "Clevo keyboard", -1, "", "clevo_keyboard_layout"),
    ]
    return recipes, models


def family_coolermaster(rd):
    L = rd.layouts
    names = rd.src(CM).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_CMKeyboardController.cpp:85-93. The region comes from
    # CMKeyboardV1Controller.cpp:470-499 and CMKeyboardV2Controller.cpp:35-80:
    # Default, ANSI, ISO or JIS.
    recipes = overlay_values_recipes(rd, "CoolerMaster", CM, names,
                                     [L["KEYBOARD_LAYOUT_DEFAULT"], L["KEYBOARD_LAYOUT_ANSI_QWERTY"],
                                      L["KEYBOARD_LAYOUT_ISO_QWERTY"], L["KEYBOARD_LAYOUT_JIS"]], "sized")
    ev = rd.ev(CM, CM_H)
    zones = {}
    for name, node in struct_list(rd, CM, "cm_kb_zone"):
        zones[name] = (ev.string(node[0]), ev.integer(node[1]))
    matrix = rd.zone_types["ZONE_TYPE_MATRIX"]
    models = []
    # Lines 71-85: a matrix zone builds the device's layout.
    for name, node in device_list(rd, CM, "cm_kb_devices"):
        layout = None if is_null(node[2]) else rd.ref(node[2])
        for z in node[1]:
            if is_null(z):
                continue
            zname, ztype = zones[rd.ref(z)]
            if ztype != matrix:
                continue
            if layout is None:
                raise CppError("cooler master %s has a matrix zone and no layout" % name)
            models.append(Model("CoolerMaster", name, ev.integer(node[0]), "", layout, zname))
    return recipes, models


def family_corsair(rd):
    L = rd.layouts
    names = rd.src(CORSAIR).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_CorsairV2Hardware.cpp:95-110 and 130-140 (the Software
    # controller, lines 82-127, does the same): ISO, JIS or ANSI.
    recipes = overlay_values_recipes(rd, "CorsairPeripheralV2", CORSAIR, names,
                                     [L["KEYBOARD_LAYOUT_ANSI_QWERTY"], L["KEYBOARD_LAYOUT_ISO_QWERTY"],
                                      L["KEYBOARD_LAYOUT_JIS"]], "sized")
    ev = rd.ev(CORSAIR, CORSAIR_H)
    zones = {}
    for name, node in struct_list(rd, CORSAIR, "corsair_v2_zone"):
        zones[name] = (ev.string(node[0]), ev.integer(node[1]), ev.integer(node[2]), ev.integer(node[3]))
    models = []
    matrix = rd.zone_types["ZONE_TYPE_MATRIX"]
    # Lines 115-140: a matrix zone builds the device's layout into the
    # zone's rows and columns.
    for name, node in device_list(rd, CORSAIR, "corsair_v2_device_list_data"):
        layout = None if is_null(node[5]) else rd.ref(node[5])
        for z in node[4]:
            if is_null(z):
                continue
            zname, ztype, zrows, zcols = zones[rd.ref(z)]
            if ztype != matrix:
                continue
            if layout is None:
                raise CppError("corsair %s has a matrix zone and no layout" % name)
            models.append(Model("CorsairPeripheralV2", name, ev.integer(node[0]), "", layout, zname, zrows, zcols))
    return recipes, models


def family_hyte(rd):
    ev = rd.ev(HYTE)
    size, values, edits, line = rd.overlay_values("hyte_keeb_tkl_layout", HYTE, ev)
    # RGBController_HYTEKeyboard.cpp:89-90: Default layout, the edits always.
    r = Recipe("HYTEKeyboard", "hyte_keeb_tkl_layout", "RGBController_HYTEKeyboard.cpp:%d" % line, size, values,
               [edits], [rd.layouts["KEYBOARD_LAYOUT_DEFAULT"]],
               "KeyboardLayoutManager k(l, hyte_keeb_tkl_layout.base_size, hyte_keeb_tkl_layout.key_values); "
               "k.ChangeKeys(hyte_keeb_tkl_layout.edit_keys);")
    return [r], [Model("HYTEKeyboard", "HYTE Keeb TKL", -1, "", r.name)]


def family_hyperx(rd):
    ev = rd.ev(HYPERX)
    L = rd.layouts
    defaults, overlays = rd.layout_values(Expr([oc.Token("id", "hyperx_core_layout", 0)]), HYPERX, ev)
    abnt2 = rd.overlay_map(Expr([oc.Token("id", "regional_overlay_abnt2", 0)]), HYPERX, ev)
    if overlays:
        raise CppError("hyperx_core_layout has regional overlays of its own now")
    # RGBController_HyperXAlloyOriginsCore.cpp:154-167: an ABNT2 board sets
    # hyperx_core_layout.regional_overlay to regional_overlay_abnt2 and builds
    # ABNT2, any other builds ANSI. The ABNT2 overlay is keyed by ABNT2 alone,
    # so an ANSI build after an ABNT2 one is unchanged by it, and one recipe
    # with the overlay in place holds both.
    r = Recipe("HyperXKeyboard", "hyperx_core_layout", cite(rd, HYPERX, "hyperx_core_layout"),
               rd.sizes["KEYBOARD_SIZE_TKL"], (defaults, abnt2),
               [], [L["KEYBOARD_LAYOUT_ANSI_QWERTY"], L["KEYBOARD_LAYOUT_ABNT2"]],
               "layout_values v = hyperx_core_layout; v.regional_overlay = regional_overlay_abnt2; "
               "KeyboardLayoutManager k(l, KEYBOARD_SIZE_TKL, v);")
    return [r], [Model("HyperXKeyboard", "HyperX Alloy Origins Core", -1, "", r.name)]


def family_logitech(rd):
    ev = rd.ev(LOGI)
    L = rd.layouts
    S = rd.sizes
    regions = [L[k] for k in ("KEYBOARD_LAYOUT_ANSI_QWERTY", "KEYBOARD_LAYOUT_ISO_QWERTY", "KEYBOARD_LAYOUT_ISO_QWERTZ",
                              "KEYBOARD_LAYOUT_ISO_AZERTY", "KEYBOARD_LAYOUT_JIS", "KEYBOARD_LAYOUT_ABNT2")]
    sizes = [("full", S["KEYBOARD_SIZE_FULL"], "KEYBOARD_SIZE_FULL"), ("tkl", S["KEYBOARD_SIZE_TKL"], "KEYBOARD_SIZE_TKL")]
    recipes, models = [], []
    src = rd.src(LOGI)
    # 0x8081: RGBController_LogitechHIDPP20.cpp:1532-1549, KLM(layout, size)
    # and the board's edit list from known_kb_layout_edits (lines 459-476).
    edits_8081 = []
    node, _ = src.definition("known_kb_layout_edits")
    for entry in node:
        pid = ev.integer(entry[0])
        strip = rd.ref(entry[1])
        count = ev.integer(entry[2])
        keys = rd.named_key_set(LOGI, strip, ev)
        if count != len(keys):
            raise CppError("known_kb_layout_edits: %s lists %d keys, holds %d" % (strip, count, len(keys)))
        edits_8081.append((pid, strip, keys))
    seen = set()
    for strip, keys in [(None, [])] + [(s, k) for _, s, k in edits_8081]:
        if strip in seen:
            continue
        seen.add(strip)
        for tag, size, cpp_size in sizes:
            name = "logitech_8081_%s%s" % (tag, "" if strip is None else "_" + strip)
            cpp = "KeyboardLayoutManager k(l, %s);" % cpp_size
            if strip is not None:
                cpp += " k.ChangeKeys(key_set(%s, %s + %d));" % (strip, strip, len(keys))
            recipes.append(Recipe("Logitech", name, "RGBController_LogitechHIDPP20.cpp:1536" if strip is None
                                  else cite(rd, LOGI, strip), size, ([], []), [keys] if keys else [], regions, cpp))
    for pid, strip, keys in edits_8081:
        for tag, size, _ in sizes:
            # Size by number pad, lines 1429 and 1534.
            models.append(Model("Logitech", "HID++ 0x8081 keyboard", pid, "numpad" if tag == "full" else "no numpad",
                                "logitech_8081_%s_%s" % (tag, strip)))
    # 0x8080: lines 1962-1980, KLM(layout, size, hidpp20_kb_8080_values) and
    # the strip of known_kb_8080_strips (lines 867-891).
    values = rd.layout_values(Expr([oc.Token("id", "hidpp20_kb_8080_values", 0)]), LOGI, ev)
    node, _ = src.definition("known_kb_8080_strips")
    entries = []
    for entry in node:
        pid = ev.integer(entry[0])
        full = entry[1][0].text == "true"
        strip = rd.ref(entry[2])
        count = ev.integer(entry[3])
        keys = rd.named_key_set(LOGI, strip, ev)
        if count != len(keys):
            raise CppError("known_kb_8080_strips: %s lists %d keys, holds %d" % (strip, count, len(keys)))
        entries.append((pid, full, strip, keys))
    seen = set()
    for strip, keys in [(None, [])] + [(s, k) for _, _, s, k in entries]:
        if strip in seen:
            continue
        seen.add(strip)
        for tag, size, cpp_size in sizes:
            name = "logitech_8080_%s%s" % (tag, "" if strip is None else "_" + strip)
            cpp = "KeyboardLayoutManager k(l, %s, hidpp20_kb_8080_values);" % cpp_size
            if strip is not None:
                cpp += " k.ChangeKeys(key_set(%s, %s + %d));" % (strip, strip, len(keys))
            recipes.append(Recipe("Logitech", name, "RGBController_LogitechHIDPP20.cpp:1975" if strip is None
                                  else cite(rd, LOGI, strip), size, values, [keys] if keys else [], regions, cpp))
    for pid, full, strip, keys in entries:
        for tag, size, _ in sizes:
            # Size by number pad when the key types enumerate, line 1964.
            models.append(Model("Logitech", "HID++ 0x8080 keyboard", pid, "numpad" if tag == "full" else "no numpad",
                                "logitech_8080_%s_%s" % (tag, strip)))
        # Without enumeration the strip entry's full_size picks, lines 1970-1973.
        models.append(Model("Logitech", "HID++ 0x8080 keyboard", pid, "not enumerated",
                            "logitech_8080_%s_%s" % ("full" if full else "tkl", strip)))
    return recipes, models


def family_mintaka(rd):
    ev = rd.ev(MINTAKA)
    values = rd.layout_values(Expr([oc.Token("id", "mintaka_offset_values", 0)]), MINTAKA, ev)
    # RGBController_MintakaKeyboard.cpp:255
    r = Recipe("MintakaKeyboard", "mintaka_offset_values", cite(rd, MINTAKA, "mintaka_offset_values"),
               rd.sizes["KEYBOARD_SIZE_SIXTY"], values, [], [rd.layouts["KEYBOARD_LAYOUT_ISO_QWERTY"]],
               "KeyboardLayoutManager k(l, KEYBOARD_SIZE_SIXTY, mintaka_offset_values);")
    return [r], [Model("MintakaKeyboard", "VSG Mintaka", -1, "", r.name)]


def family_mountain(rd):
    ev = rd.ev(MOUNTAIN)
    values = rd.layout_values(Expr([oc.Token("id", "mountain60_layout", 0)]), MOUNTAIN, ev)
    _, _, edits, _ = rd.overlay_values("mountain60_keyboard_overlay_no_numpad", MOUNTAIN, ev)
    # RGBController_Mountain60Keyboard.cpp:359-360: ChangeKeys on an overlay
    # uses its edit_keys alone (KeyboardLayoutManager.cpp:489-492).
    r = Recipe("MountainKeyboard", "mountain60_layout", cite(rd, MOUNTAIN, "mountain60_layout"),
               rd.sizes["KEYBOARD_SIZE_SIXTY"], values, [edits], [rd.layouts["KEYBOARD_LAYOUT_ANSI_QWERTY"]],
               "KeyboardLayoutManager k(l, KEYBOARD_SIZE_SIXTY, mountain60_layout); "
               "k.ChangeKeys(mountain60_keyboard_overlay_no_numpad);")
    return [r], [Model("MountainKeyboard", "Mountain Everest 60", -1, "", r.name)]


def family_razer(rd):
    L = rd.layouts
    names = rd.src(RAZER).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_Razer.cpp:236-268: AZERTY, ISO, QWERTZ or ANSI (a JIS
    # board builds ANSI), the overlay's edits when it has a size or edits.
    recipes = overlay_values_recipes(rd, "Razer", RAZER, names,
                                     [L["KEYBOARD_LAYOUT_ANSI_QWERTY"], L["KEYBOARD_LAYOUT_ISO_QWERTY"],
                                      L["KEYBOARD_LAYOUT_ISO_QWERTZ"], L["KEYBOARD_LAYOUT_ISO_AZERTY"]],
                                     "sized_or_edits")
    ev = rd.ev(RAZER, RAZER_H)
    zones = {}
    for name, node in struct_list(rd, RAZER, "razer_zone"):
        zones[name] = (ev.string(node[0]), ev.integer(node[1]), ev.integer(node[2]), ev.integer(node[3]))
    models = []
    matrix = rd.zone_types["ZONE_TYPE_MATRIX"]
    for name, node in device_list(rd, RAZER, "razer_device_list"):
        layout = None if is_null(node[9]) else rd.ref(node[9])
        if layout is None:
            continue
        for z in node[8]:
            if is_null(z):
                continue
            zname, ztype, zrows, zcols = zones[rd.ref(z)]
            # Lines 228-230: a matrix zone named Keyboard or Keypad.
            if ztype == matrix and zname in ("Keyboard", "Keypad"):
                models.append(Model("Razer", ev.string(node[0]), ev.integer(node[1]), "", layout, zname, zrows, zcols))
    return recipes, models


def family_sinowealth(rd):
    L = rd.layouts
    names = rd.src(SINO).definitions_of_type("keyboard_keymap_overlay_values")
    # RGBController_SinowealthKeyboard10c.cpp:96-100: ANSI, edits always.
    recipes = overlay_values_recipes(rd, "Sinowealth", SINO, names, [L["KEYBOARD_LAYOUT_ANSI_QWERTY"]], "always")
    for r in recipes:
        r.cpp_build = ("KeyboardLayoutManager k(l, %s.base_size, %s.key_values); k.ChangeKeys(%s.edit_keys);"
                       % (r.name, r.name, r.name))
    ev = rd.ev(SINO)
    node, _ = rd.src(SINO).definition("sinowealth_10c_keyboards")
    models = []
    seen = set()
    for entry in node:
        key = ev.integer(entry[0])
        if key in seen:
            continue
        seen.add(key)
        models.append(Model("Sinowealth", ev.string(entry[1][0]), -1, "0x%02X" % key, rd.ref(entry[1][1])))
    return recipes, models


def family_skyloong(rd):
    ev = rd.ev(SKYLOONG)
    values = rd.layout_values(Expr([oc.Token("id", "keyboard_offset_values", 0)]), SKYLOONG, ev)
    edits, line = skyloong_edits(rd, ev)
    # RGBController_SkyloongGK104Pro.cpp:93-101
    r = Recipe("Skyloong", "skyloong_gk104_pro", "RGBController_SkyloongGK104Pro.cpp:%d" % line,
               rd.sizes["KEYBOARD_SIZE_FULL"], values, [edits], [rd.layouts["KEYBOARD_LAYOUT_ANSI_QWERTY"]],
               "KeyboardLayoutManager k(l, KEYBOARD_SIZE_FULL, keyboard_offset_values); k.ChangeKeys(skyloong_edits);")
    return [r], [Model("Skyloong", "Skyloong GK104 Pro", -1, "", r.name)]


def skyloong_edits(rd, ev):
    """The edit list SetupZones hands ChangeKeys inline."""
    toks = rd.src(SKYLOONG).tokens
    for i, t in enumerate(toks):
        if t.text == "ChangeKeys" and toks[i + 1].text == "(" and toks[i + 2].text == "{":
            node, _ = oc.parse_braces(toks, i + 2)
            return [rd.led(n, ev) for n in node], t.line
    raise CppError("no inline ChangeKeys in the Skyloong controller")


def family_winbond(rd):
    ev = rd.ev(WINBOND)
    L = rd.layouts
    S = rd.sizes
    check_winbond_text(rd)
    mm = rd.named_key_set(WINBOND, "additional_mm_leds", ev)
    recipes = []
    # RGBController_WinbondGamingKeyboard.cpp:747-765: TKL and 60% take
    # their own values, anything else is built full size. The region comes
    # from WinbondGamingKeyboardController.cpp:80-138: ISO, JIS or ANSI.
    for var, size_name in (("winbond_gaming_keyboard_full_layouts", "KEYBOARD_SIZE_FULL"),
                           ("winbond_gaming_keyboard_tkl_layouts", "KEYBOARD_SIZE_TKL"),
                           ("winbond_gaming_keyboard_60_layouts", "KEYBOARD_SIZE_SIXTY")):
        defaults, overlays = rd.layout_values(Expr([oc.Token("id", var, 0)]), WINBOND, ev)
        if defaults:
            raise CppError("%s has default values of its own now" % var)
        for vendor in ("Hator", ""):
            size = S[size_name]
            values = winbond_values(rd, size, vendor, overlays, mm)
            name = "%s_%s" % (var, "hator" if vendor else "other")
            cpp = ('layout_values v = %s; InitLayouts(v, %s, "%s"); KeyboardLayoutManager k(l, %s, v);'
                   % (var, size_name, vendor, size_name))
            recipes.append(Recipe("WinbondGamingKeyboard", name, cite(rd, WINBOND, var), size, values, [],
                                  [L["KEYBOARD_LAYOUT_ANSI_QWERTY"], L["KEYBOARD_LAYOUT_ISO_QWERTY"],
                                   L["KEYBOARD_LAYOUT_JIS"]], cpp))
    models = []
    for r in recipes:
        size = {"full": "full size", "tkl": "TKL", "60": "60%"}[r.name.split("_")[3]]
        vendor = "Hator" if r.name.endswith("hator") else "any other vendor"
        models.append(Model("WinbondGamingKeyboard", "Winbond gaming keyboard", -1, "%s, %s" % (size, vendor), r.name))
    return recipes, models


def winbond_init_text(rd):
    raw = rd.src(WINBOND).raw
    start = raw.index("static void InitLayouts(")
    depth = 0
    i = raw.index("{", start)
    while True:
        if raw[i] == "{":
            depth += 1
        elif raw[i] == "}":
            depth -= 1
            if depth == 0:
                return raw[start:i + 1]
        i += 1


def check_winbond_text(rd):
    text = winbond_init_text(rd).replace("\r\n", "\n")
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    if digest != WINBOND_INIT_DIGEST:
        sys.exit("InitLayouts in %s changed (SHA-256 %s): port it again in winbond_values()" % (WINBOND, digest))


# The SHA-256 of the InitLayouts text winbond_values() was ported from.
WINBOND_INIT_DIGEST = "74bc9786ff5ed3c123dec5aa2fbf9d7f3b7a0e30f1138b702e72f712673acd07"


def winbond_values(rd, size, vendor, overlays, mm):
    """InitLayouts (RGBController_WinbondGamingKeyboard.cpp:233-531) for one
    size and vendor, on a fresh copy of the size's layout_values: the LED
    values in the order it pushes them and, for a full-size board, the
    four media keys added to every regional overlay and an ANSI overlay of
    them alone."""
    Z = rd.zone_bits
    main, fn, extra, numpad = (Z["KEYBOARD_ZONE_MAIN"], Z["KEYBOARD_ZONE_FN_ROW"],
                               Z["KEYBOARD_ZONE_EXTRA"], Z["KEYBOARD_ZONE_NUMPAD"])

    def kv(msg, idx):
        return (msg << 8) | idx | (1 << 16)

    def span(msg, a, b):
        return [kv(msg, i) for i in range(a, b + 1)]

    k = []
    if size & fn:
        k += [kv(0, 0)] + span(0, 2, 5) + span(0, 7, 14)
    if size & extra:
        k += span(0, 15, 17)
    if size & main:
        k += span(1, 4, 16)
    if size & main:
        k += [kv(2, 0)]
    if size & extra:
        k += span(2, 1, 3)
    if size & numpad:
        k += span(2, 4, 7)
    if size & main:
        k += span(2, 8, 17)
    if size & main:
        k += span(3, 0, 2)
        k += [kv(3, 3)] if vendor != "Hator" else [kv(3, 4)]
    if size & extra:
        k += span(3, 5, 7)
    if size & numpad:
        k += span(3, 8, 11)
    if size & main:
        k += [kv(3, 12)] + span(3, 14, 17)
    if size & main:
        k += span(4, 0, 6) + [kv(4, 7), kv(4, 8)]
    if size & numpad:
        k += span(4, 12, 14)
    if size & main:
        k += [kv(4, 16), kv(4, 17)]
    if size & main:
        k += span(5, 0, 9) + [kv(5, 12)]
    if size & extra:
        k += [kv(5, 14)]
    if size & numpad:
        k += [kv(5, 16), kv(5, 17)]
    if size & numpad:
        k += span(6, 0, 1)
    if size & main:
        k += span(6, 2, 4) + [kv(6, 8)] + span(6, 12, 15)
    if size & extra:
        k += [kv(6, 17)]
    if size & extra:
        k += [kv(7, 0), kv(7, 1)]
    if size & numpad:
        k += [kv(7, 3), kv(7, 5)]
    overlays = [(layout, list(keys)) for layout, keys in overlays]
    if size == rd.sizes["KEYBOARD_SIZE_FULL"]:
        for _, keys in overlays:
            keys.extend(mm)
        ansi = rd.layouts["KEYBOARD_LAYOUT_ANSI_QWERTY"]
        if ansi not in [layout for layout, _ in overlays]:
            overlays.append((ansi, list(mm)))
            overlays.sort(key=lambda p: p[0])
    return (k, overlays)


def family_witmod(rd):
    ev = rd.ev(WITMOD, WITMOD_H)
    L = rd.layouts
    S = rd.sizes
    recipes = []
    # RGBController_WitmodKeyboard.cpp:241-251: GK8120 values for a full
    # board, GK8110 values for any other (WitmodKeyboardController.cpp
    # 147-154 sets TKL or full).
    for var, size_name in (("gk8110_offset_values", "KEYBOARD_SIZE_TKL"), ("gk8120_offset_values", "KEYBOARD_SIZE_FULL")):
        values = rd.layout_values(Expr([oc.Token("id", var, 0)]), WITMOD, ev)
        recipes.append(Recipe("WitmodKeyboard", var, cite(rd, WITMOD, var), S[size_name], values, [],
                              [L["KEYBOARD_LAYOUT_ANSI_QWERTY"]],
                              "KeyboardLayoutManager k(l, %s, %s);" % (size_name, var)))
    height = ev.integer(Expr([oc.Token("id", "WITMOD_MATRIX_HEIGHT", 0)]))
    models = [Model("WitmodKeyboard", "Witmod GK8110", -1, "KEYBOARD_SIZE_TKL", "gk8110_offset_values", "Keyboard",
                    height, ev.integer(Expr([oc.Token("id", "GK8110_WIDTH", 0)]))),
              Model("WitmodKeyboard", "Witmod GK8120", -1, "KEYBOARD_SIZE_FULL", "gk8120_offset_values", "Keyboard",
                    height, ev.integer(Expr([oc.Token("id", "GK8120_WIDTH", 0)])))]
    return recipes, models


def family_klm(rd):
    """KeyboardLayoutManager on its own: each standard size in every region,
    with no values and no edits, as Logitech's 0x8081 path builds a board
    without an edit list."""
    recipes = []
    for size_name in ("KEYBOARD_SIZE_FULL", "KEYBOARD_SIZE_TKL", "KEYBOARD_SIZE_SEVENTY_FIVE", "KEYBOARD_SIZE_SIXTY",
                      "KEYBOARD_SIZE_EMPTY"):
        name = "klm_" + size_name[len("KEYBOARD_SIZE_"):].lower()
        recipes.append(Recipe("KeyboardLayoutManager", name, "KeyboardLayoutManager.cpp:317", rd.sizes[size_name],
                              ([], []), [], list(range(len(LAYOUT_NAMES))),
                              "KeyboardLayoutManager k(l, %s);" % size_name))
    return recipes, []


FAMILIES = [family_klm, family_aoc, family_asus, family_clevo, family_coolermaster, family_corsair, family_hyte, family_hyperx,
            family_logitech, family_mintaka, family_mountain, family_razer, family_sinowealth, family_skyloong,
            family_winbond, family_witmod]


def base_tables(rd):
    """The four base blocks and six regional overlays of
    KeyboardLayoutManager.cpp."""
    ev = rd.ev(KLM_CPP)
    blocks = []
    for name in ("keyboard_zone_main", "keyboard_zone_fn_row", "keyboard_zone_extras", "keyboard_zone_numpad"):
        blocks.append((name, rd.named_key_set(KLM_CPP, name, ev), cite(rd, KLM_CPP, name)))
    overlays = []
    for name in ("ansi_qwerty", "iso_qwerty", "iso_qwertz", "iso_azerty", "jis", "abnt2"):
        node, line = rd.src(KLM_CPP).definition(name)
        size = ev.integer(node[0])
        overlays.append((name, size, rd.key_set(node[1], KLM_CPP, ev), "KeyboardLayoutManager.cpp:%d" % line))
    return blocks, overlays


# ---------------------------------------------------------------------
# Writing C#
# ---------------------------------------------------------------------

def cs_size(size):
    names = {0: "Empty", 1: "Sixty", 3: "SeventyFive", 7: "Tkl", 15: "Full"}
    return "KeyboardSize." + names[size] if size in names else "(KeyboardSize)%d" % size


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cs_led(led):
    zone, row, col, value, name, alt, opcode = led
    return "new(%d, %d, %d, %d, %s, %s, KeyboardOpcode.%s)" % (zone, row, col, value, cs_string(name), cs_string(alt),
                                                               OPCODE_NAMES[opcode])


def write_cs(commit, rd, blocks, overlays, recipes, models):
    out = []
    w = out.append
    w("// <auto-generated>")
    w("//     Generated by tools/gen_openrgb_keyboard_layouts.py from OpenRGB at")
    w("//     %s (KeyboardLayoutManager/KeyboardLayoutManager.cpp and .h, and the" % commit[:10])
    w("//     tables of the fifteen families that build keyboards with it). Do not")
    w("//     edit by hand.")
    w("// </auto-generated>")
    w("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    w("{")
    w("    internal static partial class KeyboardLayouts")
    w("    {")
    w("        /// <summary>The OpenRGB commit the tables were read at.</summary>")
    w("        internal const string OpenRgbCommit = \"%s\";" % commit)
    w("")
    w("        /// <summary>The values of OpenRGB's enums in KeyboardLayoutManager.h, by")
    w("        /// name, for the test that holds this port's enums to them.</summary>")
    w("        internal static readonly (string Name, int Value)[] OpenRgbEnums =")
    w("        {")
    for table in (rd.zone_bits, rd.sizes, rd.layouts, rd.opcodes, rd.fills):
        for k, v in table.items():
            w("            (\"%s\", %d)," % (k, v))
    w("        };")
    for name, keys, src in blocks:
        w("")
        w("        /// <summary>%s, %s.</summary>" % (name, src))
        w("        internal static readonly KeyboardLed[] %s =" % pascal(name.replace("keyboard_", "")))
        w("        {")
        for led in keys:
            w("            " + cs_led(led) + ",")
        w("        };")
    for name, size, keys, src in overlays:
        w("")
        w("        /// <summary>The %s overlay, %s.</summary>" % (name, src))
        w("        internal static readonly KeyboardLed[] %sOverlay =" % pascal(name))
        w("        {")
        for led in keys:
            w("            " + cs_led(led) + ",")
        w("        };")
    # Arrays: a list OpenRGB defines once under a name is written once under
    # that name, a list written inline is written for the recipe it is in.
    arrays = {}

    def array_name(kind, content, hint):
        source_name = rd.names.get((kind, repr(content)), hint)
        key = (kind, repr(content), source_name)
        if key not in arrays:
            name = pascal(source_name)
            if not name.endswith(kind):
                name += kind
            base, n = name, 2
            while name in [v[0] for v in arrays.values()]:
                name = "%s%d" % (base, n)
                n += 1
            arrays[key] = (name, kind, content)
        return arrays[key][0]

    recipe_lines = []
    for r in recipes:
        defaults, regional = r.values
        vname = array_name("Values", defaults, r.name) if defaults else None
        reg = []
        for layout, keys in regional:
            reg.append("(KeyboardLayout.%s, %s)" % (LAYOUT_NAMES[layout], array_name("Keys", keys, r.name + "_" + LAYOUT_NAMES[layout])))
        edits = [array_name("Keys", e, r.name + "_edits") for e in r.edits]
        layouts = ", ".join("KeyboardLayout." + LAYOUT_NAMES[x] for x in r.layouts)
        recipe_lines.append("            new(%s, %s, %s, %s, %s, new (KeyboardLayout, KeyboardLed[])[] { %s }, new KeyboardLed[][] { %s }, new[] { %s }),"
                            % (cs_string(r.family), cs_string(r.name), cs_string(r.source), cs_size(r.size),
                               vname or "System.Array.Empty<uint>()", ", ".join(reg), ", ".join(edits), layouts))
    # Static fields start in the order they are written, so the arrays the
    # recipes point at come first.
    for name, kind, content in arrays.values():
        w("")
        if kind == "Values":
            w("        private static readonly uint[] %s =" % name)
            w("        {")
            for i in range(0, len(content), 16):
                w("            " + ", ".join(str(v) for v in content[i:i + 16]) + ",")
        else:
            w("        private static readonly KeyboardLed[] %s =" % name)
            w("        {")
            for led in content:
                w("            " + cs_led(led) + ",")
        w("        };")
    w("")
    w("        /// <summary>Every layout a family builds: its family, OpenRGB's name for it,")
    w("        /// where it is defined, the size, the LED values, the regional fixes, the")
    w("        /// edit lists in the order the family applies them, and every region the")
    w("        /// family can build it in.</summary>")
    w("        internal static readonly KeyboardLayoutRecipe[] Recipes =")
    w("        {")
    out.extend(recipe_lines)
    w("        };")
    w("")
    w("        /// <summary>Which recipe each model builds, by product ID, or by the key")
    w("        /// its family picks with, and the matrix zone and size the family maps it")
    w("        /// into where it gives one.</summary>")
    w("        internal static readonly KeyboardLayoutModel[] Models =")
    w("        {")
    for m in models:
        w("            new(%s, %s, %d, %s, %s, %s, %d, %d)," % (cs_string(m.family), cs_string(m.model), m.product_id,
                                                           cs_string(m.key), cs_string(m.recipe), cs_string(m.zone),
                                                           m.rows, m.cols))
    w("        };")
    w("    }")
    w("}")
    return "\r\n".join(out) + "\r\n"


# ---------------------------------------------------------------------
# Writing the C++ harness
# ---------------------------------------------------------------------

HARNESS_SNIPPETS = [
    (AOC, ["aoc_keyboard_offset_values"]),
    (HYTE, ["hyte_keeb_tkl_values", "hyte_keeb_tkl_layout"]),
    (HYPERX, ["hyperx_core_default_values", "hyperx_core_layout", "regional_overlay_abnt2"]),
    (LOGI, ["proxrapid_top_strip", "g915x_strip_and_gkeys", "hidpp20_kb_8080_values", "g810_top_strip",
            "gpro_top_strip", "g512_top_strip", "g910_side_strip"]),
    (MINTAKA, ["mintaka_offset_values"]),
    (MOUNTAIN, ["mountain60_keyboard_key_id_values", "mountain60_layout", "mountain60_keyboard_overlay_no_numpad"]),
    (SKYLOONG, ["keyboard_offset_values"]),
    (WINBOND, ["additional_mm_leds", "winbond_gaming_keyboard_full_layouts", "winbond_gaming_keyboard_tkl_layouts",
               "winbond_gaming_keyboard_60_layouts"]),
    (WITMOD, ["gk8110_offset_values", "gk8120_offset_values"]),
]


def definition_text(rd, rel, name):
    """The source text of a top-level definition, from the start of its
    line to its closing semicolon, as OpenRGB writes it."""
    s = rd.src(rel)
    j = s.find(name)
    toks = s.tokens
    _, end = oc.parse_braces(toks, j)
    if toks[end].text != ";":
        raise CppError("%s: %s does not end in ;" % (rel, name))
    k = j
    while toks[k].text != name:
        k -= 1
    lines = s.raw.split("\n")
    start_line = toks[k].line
    end_line = toks[end].line
    return "\n".join(lines[start_line - 1:end_line]).rstrip() + "\n", start_line


def harness_models(rd, recipes):
    """C++ that walks OpenRGB's own device tables and writes, for each model
    the layout manager builds, the row the Models table holds for it. A
    pointer is named by looking it up among the names this script knows,
    and Sinowealth's copied layouts by comparing them with each named one."""
    out = []
    w = out.append

    def table(var, names):
        w("    static const named %s[] = {" % var)
        for n in names:
            w("        { &%s, \"%s\" }," % (n, n))
        w("    };")

    for fam, rel in (("Razer", RAZER), ("CoolerMaster", CM), ("CorsairPeripheralV2", CORSAIR), ("AsusAuraCore", ASUS),
                     ("Sinowealth", SINO)):
        table("layouts_" + fam, [r.name for r in recipes if r.family == fam])
    table("devices_CoolerMaster", [n for n, _ in device_list(rd, CM, "cm_kb_devices")])
    table("devices_CorsairPeripheralV2", [n for n, _ in device_list(rd, CORSAIR, "corsair_v2_device_list_data")])
    w("    for(unsigned int i = 0; i < RAZER_NUM_DEVICES; i++)")
    w("    {")
    w("        const razer_device* d = razer_device_list[i];")
    w("        if(d->layout == NULL) continue;")
    w("        for(unsigned int z = 0; z < RAZER_MAX_ZONES; z++)")
    w("        {")
    w("            const razer_zone* zn = d->zones[z];")
    w("            if(zn == NULL || zn->type != ZONE_TYPE_MATRIX || (zn->name != ZONE_EN_KEYBOARD && zn->name != \"Keypad\")) continue;")
    w("            fprintf(f, \"model\\tRazer\\t%s\\t%d\\t\\t%s\\t%s\\t%u\\t%u\\n\", d->name.c_str(), (int)d->pid,")
    w("                    lookup(layouts_Razer, sizeof(layouts_Razer) / sizeof(named), d->layout), zn->name.c_str(), zn->rows, zn->cols);")
    w("        }")
    w("    }")
    w("    for(unsigned int i = 0; i < COOLERMASTER_KEYBOARD_DEVICE_COUNT; i++)")
    w("    {")
    w("        const cm_kb_device* d = cm_kb_devices[i];")
    w("        for(unsigned int z = 0; z < CM_KB_ZONES_MAX; z++)")
    w("        {")
    w("            if(d->zones[z] == NULL || d->zones[z]->type != ZONE_TYPE_MATRIX) continue;")
    w("            fprintf(f, \"model\\tCoolerMaster\\t%s\\t%d\\t\\t%s\\t%s\\t0\\t0\\n\",")
    w("                    lookup(devices_CoolerMaster, sizeof(devices_CoolerMaster) / sizeof(named), d), (int)d->product_id,")
    w("                    lookup(layouts_CoolerMaster, sizeof(layouts_CoolerMaster) / sizeof(named), d->layout_new), d->zones[z]->name.c_str());")
    w("        }")
    w("    }")
    w("    for(unsigned int i = 0; i < CORSAIR_V2_DEVICE_COUNT; i++)")
    w("    {")
    w("        const corsair_v2_device* d = corsair_v2_device_list_data[i];")
    w("        for(unsigned int z = 0; z < CORSAIR_ZONES_MAX; z++)")
    w("        {")
    w("            const corsair_v2_zone* zn = d->zones[z];")
    w("            if(zn == NULL || zn->type != ZONE_TYPE_MATRIX) continue;")
    w("            fprintf(f, \"model\\tCorsairPeripheralV2\\t%s\\t%d\\t\\t%s\\t%s\\t%u\\t%u\\n\",")
    w("                    lookup(devices_CorsairPeripheralV2, sizeof(devices_CorsairPeripheralV2) / sizeof(named), d), (int)d->pid,")
    w("                    lookup(layouts_CorsairPeripheralV2, sizeof(layouts_CorsairPeripheralV2) / sizeof(named), d->layout_new),")
    w("                    zn->name.c_str(), (unsigned)zn->rows, (unsigned)zn->cols);")
    w("        }")
    w("    }")
    w("    for(unsigned int i = 0; i < AURA_CORE_LAPTOP_DEVICE_COUNT; i++)")
    w("    {")
    w("        const aura_core_laptop_device* d = aura_core_laptop_device_list_data[i];")
    w("        for(unsigned int z = 0; z < AURA_CORE_LAPTOP_ZONES_MAX; z++)")
    w("        {")
    w("            const aura_core_laptop_zone* zn = d->zones[z];")
    w("            if(zn == NULL || zn->layout_new == NULL) continue;")
    w("            fprintf(f, \"model\\tAsusAuraCore\\t%s\\t-1\\t%s\\t%s\\t%s\\t0\\t0\\n\", d->dmi_name.c_str(), d->dmi_name.c_str(),")
    w("                    lookup(layouts_AsusAuraCore, sizeof(layouts_AsusAuraCore) / sizeof(named), zn->layout_new), zn->name.c_str());")
    w("        }")
    w("    }")
    w("    for(const auto& e : sinowealth_10c_keyboards)")
    w("    {")
    w("        const char* recipe = \"?\";")
    w("        for(const named& n : layouts_Sinowealth)")
    w("            if(same(*(const keyboard_keymap_overlay_values*)n.p, e.second.keyboard_layout)) { recipe = n.n; break; }")
    w("        fprintf(f, \"model\\tSinowealth\\t%s\\t-1\\t0x%02X\\t%s\\t\\t0\\t0\\n\", e.second.device_name.c_str(), e.first, recipe);")
    w("    }")
    return "\n".join(out)


def write_harness(commit, rd, recipes):
    out = []
    w = out.append
    w("// Generated by tools/gen_openrgb_keyboard_layouts.py --harness from OpenRGB at")
    w("// %s. Do not edit by hand." % commit)
    w("//")
    w("// Builds every recipe of KeyboardLayouts.g.cs with OpenRGB's own")
    w("// KeyboardLayoutManager.cpp and the families' own tables, in every region,")
    w("// and writes the key list KeyboardLayoutManager holds after each build.")
    w("// The tables of the family controllers below are copied from their files")
    w("// verbatim, since those files also hold the controller code. Build it with")
    w("// tools/openrgb_klm/build.cmd.")
    w("#include <cstdio>")
    w("#include <cstring>")
    w("#include <string>")
    w("#include <vector>")
    w("#include <map>")
    w("#include \"RGBController.h\"")
    w("#include \"RGBControllerKeyNames.h\"")
    w("// The dump reads each key's row and column from the key list itself.")
    w("#define private public")
    w("#include \"KeyboardLayoutManager.h\"")
    w("#undef private")
    w("")
    w("// The device tables are compiled into this file whole, so the layouts they")
    w("// keep to themselves (static or const) are in reach.")
    for rel in (RAZER, CM, CORSAIR, ASUS, SINO, CLEVO):
        w("#include \"%s\"" % rel.split("/")[-1])
    for rel, names in HARNESS_SNIPPETS:
        w("")
        w("// ---- %s" % rel)
        if rel == WINBOND:
            w("#define KV(MSG_NUM, IDX)  ((MSG_NUM) << 8 | (IDX) | (1 << 16))")
        for name in names:
            text, line = definition_text(rd, rel, name)
            w("// %s:%d" % (rel.split("/")[-1], line))
            out.append(text.rstrip("\n"))
        if rel == WINBOND:
            w("// %s, InitLayouts" % rel.split("/")[-1])
            out.append(winbond_init_text(rd).replace("\r\n", "\n"))
        if rel == SKYLOONG:
            toks = rd.src(SKYLOONG).tokens
            for i, t in enumerate(toks):
                if t.text == "ChangeKeys" and toks[i + 1].text == "(" and toks[i + 2].text == "{":
                    _, end = oc.parse_braces(toks, i + 2)
                    lines = rd.src(SKYLOONG).raw.split("\n")
                    w("// RGBController_SkyloongGK104Pro.cpp:%d, the list SetupZones hands ChangeKeys" % toks[i + 2].line)
                    w("static key_set skyloong_edits =")
                    body = "\n".join(lines[toks[i + 2].line - 1:toks[end - 1].line])
                    out.append(body.rstrip() + ";")
                    break
    w("")
    w("// Logitech's tables are static const arrays of keyboard_led. The")
    w("// controller builds a key_set from pointer and count (lines 471 and 1979).")
    w("")
    w("struct named { const void* p; const char* n; };")
    w("")
    w("static const char* lookup(const named* t, size_t count, const void* p)")
    w("{")
    w("    for(size_t i = 0; i < count; i++) if(t[i].p == p) return t[i].n;")
    w("    return \"?\";")
    w("}")
    w("")
    w("static bool same(const keyboard_led& a, const keyboard_led& b)")
    w("{")
    w("    return a.zone == b.zone && a.row == b.row && a.col == b.col && a.value == b.value && a.opcode == b.opcode")
    w("        && strcmp(a.name, b.name) == 0 && strcmp(a.alt_name, b.alt_name) == 0;")
    w("}")
    w("")
    w("static bool same(const key_set& a, const key_set& b)")
    w("{")
    w("    if(a.size() != b.size()) return false;")
    w("    for(size_t i = 0; i < a.size(); i++) if(!same(a[i], b[i])) return false;")
    w("    return true;")
    w("}")
    w("")
    w("static bool same(const keyboard_keymap_overlay_values& a, const keyboard_keymap_overlay_values& b)")
    w("{")
    w("    if(a.base_size != b.base_size || a.key_values.default_values != b.key_values.default_values) return false;")
    w("    if(a.key_values.regional_overlay.size() != b.key_values.regional_overlay.size()) return false;")
    w("    for(const auto& o : a.key_values.regional_overlay)")
    w("    {")
    w("        auto it = b.key_values.regional_overlay.find(o.first);")
    w("        if(it == b.key_values.regional_overlay.end() || !same(o.second, it->second)) return false;")
    w("    }")
    w("    return same(a.edit_keys, b.edit_keys);")
    w("}")
    w("")
    w("static const char* layout_names[] = { \"Default\", \"AnsiQwerty\", \"IsoQwerty\", \"IsoQwertz\", \"IsoAzerty\", \"Jis\", \"Abnt2\" };")
    w("")
    w("static void dump(FILE* f, const char* family, const char* recipe, int layout, KeyboardLayoutManager& k)")
    w("{")
    w("    fprintf(f, \"case\\t%s\\t%s\\t%s\\t%u\\t%u\\t%u\\t%s\\n\", family, recipe, layout_names[layout],")
    w("            k.GetRowCount(), k.GetColumnCount(), k.GetKeyCount(), k.GetName().c_str());")
    w("    for(unsigned int i = 0; i < k.GetKeyCount(); i++)")
    w("    {")
    w("        fprintf(f, \"%u\\t%u\\t%u\\t%s\\t%s\\n\", k.keymap[i].row, k.keymap[i].col, k.GetKeyValueAt(i),")
    w("                k.GetKeyNameAt(i).c_str(), k.GetKeyAltNameAt(i).c_str());")
    w("    }")
    w("    matrix_map_type m = k.GetKeyMap(KEYBOARD_MAP_FILL_TYPE_COUNT);")
    w("    fprintf(f, \"map\\t%u\\t%u\", m.height, m.width);")
    w("    for(unsigned int c = 0; c < m.map.size(); c++) fprintf(f, \"\\t%d\", (int)m.map[c]);")
    w("    fprintf(f, \"\\n\");")
    w("}")
    w("")
    w("int main(int argc, char** argv)")
    w("{")
    w("    FILE* f = fopen(argv[1], \"wb\");")
    w("    fprintf(f, \"openrgb\\t%s\\n\");" % commit)
    out.append(harness_models(rd, recipes))
    for r in recipes:
        for layout in r.layouts:
            w("    { KEYBOARD_LAYOUT l = (KEYBOARD_LAYOUT)%d; %s dump(f, \"%s\", \"%s\", %d, k); }"
              % (layout, r.cpp_build, r.family, r.name, layout))
    w("    fclose(f);")
    w("    return 0;")
    w("}")
    return "\n".join(out) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--harness", action="store_true")
    args = parser.parse_args()

    rd = Reader(args.openrgb)
    commit = oc.git_head(args.openrgb)
    # The C# names of the regions and opcodes follow OpenRGB's enum order.
    if [pascal(k[len("KEYBOARD_LAYOUT_"):].lower()) for k in rd.layouts] != LAYOUT_NAMES \
            or list(rd.layouts.values()) != list(range(len(LAYOUT_NAMES))):
        sys.exit("KEYBOARD_LAYOUT changed: %s" % rd.layouts)
    if [pascal(k[len("KEYBOARD_OPCODE_"):].lower()) for k in rd.opcodes] != OPCODE_NAMES \
            or list(rd.opcodes.values()) != list(range(len(OPCODE_NAMES))):
        sys.exit("KEYBOARD_OPCODE changed: %s" % rd.opcodes)
    blocks, overlays = base_tables(rd)
    recipes, models = [], []
    for family in FAMILIES:
        r, m = family(rd)
        recipes.extend(r)
        models.extend(m)
    names = [r.name for r in recipes]
    dupes = sorted(set(n for n in names if names.count(n) > 1))
    if dupes:
        sys.exit("recipe names repeat: %s" % dupes)
    for m in models:
        if m.recipe not in names:
            sys.exit("model %s names no recipe %s" % (m.model, m.recipe))

    files = [(OUTPUT, write_cs(commit, rd, blocks, overlays, recipes, models))]
    if args.harness or args.check:
        files.append((HARNESS, write_harness(commit, rd, recipes).replace("\n", "\r\n")))
    if args.check:
        stale = []
        for path, text in files:
            try:
                with open(path, encoding="utf-8", newline="") as f:
                    current = f.read()
            except FileNotFoundError:
                current = ""
            if current != text:
                stale.append(path)
        print("up to date" if not stale else "differs: " + ", ".join(stale))
        sys.exit(1 if stale else 0)
    for path, text in files:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
        print("wrote %s" % path)
    print("%d recipes, %d models" % (len(recipes), len(models)))


if __name__ == "__main__":
    main()
