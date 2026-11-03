"""OpenRGB's device detector registrations for the families #557 ports.

OpenRGB finds a device by its detector registrations: one REGISTER_*_DETECTOR
line per device and interface in each family's *Detect.cpp, each a macro of
DetectionManager.h (lines 531-603) that constructs a detector object with a
name, a detector function and, for a HID device, the vendor and product IDs,
the interface, the usage page and the usage it answers to. This script reads
every registration of the 54 families issue #557 ports at an OpenRGB commit
and writes PadForge.App/Common/Input/Peripherals/HidFamilies/OpenRgbRegistrations.g.cs.

How a registration is read, as the compiler would:
- Only the files OpenRGB's Windows build compiles: a file whose name carries
  _Linux, _FreeBSD or _MacOS and not _Windows is left out (OpenRGB.pro:109-129
  and 344), and #if blocks are kept or dropped as MSVC on 64-bit Windows
  sees them.
- Each macro is expanded from its own #define, DetectionManager.h's or the
  family's (Wooting's REGISTER_WOOTING_DETECTOR makes three detectors), and
  each detector object it constructs is decoded against its class's
  constructor in DetectionManager.h, defaults included: interface, usage
  page, usage and bus are -1, "any", where the macro leaves them out.
- A registration inside a function (QMK's REGISTER_DYNAMIC_HID_DETECTOR_*)
  takes its vendor and product IDs from the user's OpenRGB settings at run
  time. It is kept with those two at -1 and FromSettings set.

The transport is HID for a HID or wrapped HID detector, USB serial for a
USB serial detector, and for a plain REGISTER_DETECTOR what its detector
function opens: a serial port, WMI or HID. A dynamic detector, which only
registers others, is Other.

Skipped: HIDLampArrayController, LogitechController, RazerController and
SteelSeriesController, which PadForge lights its own way.

Usage:
    python tools/gen_openrgb_registrations.py OPENRGB_ROOT
    python tools/gen_openrgb_registrations.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import sys

import openrgb_cpp as oc
from openrgb_cpp import CppError, Expr

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "OpenRgbRegistrations.g.cs")

# The families issue #557's coverage table marks "new" (C:/tmp/pf555/rgb538/
# issue557_live.md, the table under "Coverage").
FAMILIES = [
    "A4TechController", "AOCKeyboardController", "AcerNitroHidKeyboardController", "AlienwareController",
    "AlienwareKeyboardController", "AnnePro2Controller", "AsusAuraCoreController", "AsusAuraUSBController",
    "AsusLegacyUSBController", "AsusTUFLaptopController", "CherryKeyboardController", "ClevoKeyboardController",
    "CoolerMasterController", "CorsairPeripheralController", "CorsairPeripheralV2Controller", "CougarController",
    "DarkProject", "DasKeyboardController", "DuckyKeyboardController", "DygmaRaiseController", "EVGAUSBController",
    "EVisionKeyboardController", "FnaticStreakController", "GaiZongGaiKeyboardController",
    "GigabyteAorusLaptopController", "HPOmenLaptopController", "HYTEKeyboardController", "HyperXKeyboardController",
    "IonicoController", "KeychronKeyboardController", "LenovoControllers", "LightSaltController",
    "MNTKeyboardController", "MSI3ZoneController", "MSIKeyboardController", "MSILaptopController",
    "MSIVigorController", "MintakaKeyboardController", "MountainKeyboardController", "OKSController",
    "QMKController", "RedSquareKeyroxController", "RoccatController", "RoyuanKeyboardController",
    "SayoDeviceController", "SinowealthController", "SkyloongController", "ThermaltakePoseidonZRGBController",
    "ValkyrieKeyboardController", "WinbondGamingKeyboardController", "WitmodKeyboardController",
    "WootingKeyboardController", "XPGSummonerKeyboardController", "ZETKeyboardController",
]

# Macro forms none of those families uses, read the same way from families
# that do, so the decoding of each form is held to its source line too.
SPECIMENS = [
    ("HyperXMicrophoneController", "REGISTER_HID_WRAPPED_DETECTOR_I"),
    ("HyperXMousematController", "REGISTER_HID_WRAPPED_DETECTOR_IPU"),
    ("BlinkyTapeController", "REGISTER_USB_SERIAL_DETECTOR"),
    ("LogitechController", "REGISTER_HID_DETECTOR_BPU"),
    ("LogitechController", "REGISTER_HID_DETECTOR_BP"),
]

DETECTION_MANAGER = "DetectionManager.h"
HIDAPI = "dependencies/hidapi-win/include/hidapi.h"

# Detector classes and the transport each stands for.
CLASSES = {
    "HIDDeviceDetector": "Hid",
    "HIDWrappedDeviceDetector": "Hid",
    "USBSerialDeviceDetector": "UsbSerial",
    "DeviceDetector": None,          # from what the detector function opens
    "DynamicDetector": "Other",
}


def windows_compiled(name):
    """OpenRGB.pro:109-129 and 344: a file with an OS suffix is built only
    for that OS, and _Windows wins."""
    if "_Windows" in name:
        return True
    return not any(s in name for s in ("_Linux", "_FreeBSD", "_MacOS"))


def family_files(root, family):
    out = []
    base = os.path.join(root, "Controllers", family)
    for dirpath, _, names in os.walk(base):
        for n in names:
            if n.endswith((".cpp", ".h")) and windows_compiled(n):
                out.append(os.path.relpath(os.path.join(dirpath, n), root).replace("\\", "/"))
    return sorted(out)


def constructors(dm):
    """Each detector class's constructor parameters from DetectionManager.h:
    (name, default text or None)."""
    out = {}
    raw = oc.strip_comments(dm.raw)
    for cls in CLASSES:
        m = re.search(r"\b%s\s*\(([^)]*)\)\s*\{" % cls, raw)
        if not m:
            raise CppError("no constructor for " + cls)
        params = []
        for p in m.group(1).split(","):
            p = p.strip()
            default = None
            if "=" in p:
                p, default = (x.strip() for x in p.split("=", 1))
            params.append((p.split()[-1], default))
        out[cls] = params
    return out


def split_args(tokens, i):
    """The argument token lists of the call whose ( is tokens[i], and the
    index after its )."""
    assert tokens[i].text == "("
    args, cur, depth = [], [], 0
    i += 1
    while True:
        t = tokens[i]
        if t.text in "([{" and t.kind == "op":
            depth += 1
        elif t.text in ")]}" and t.kind == "op":
            if depth == 0:
                args.append(cur)
                return args, i + 1
            depth -= 1
        if t.text == "," and depth == 0:
            args.append(cur)
            cur = []
        else:
            cur.append(t)
        i += 1


def tokenize(text, line):
    return [oc.Token(kind, value, line) for kind, value in oc.Evaluator._tokenize(text)]


def expand(macro, args, line):
    """The detector objects a macro call constructs: (class, constructor
    argument token lists)."""
    params, body = macro
    if len(params) != len(args):
        raise CppError("line %d: macro takes %d arguments, given %d" % (line, len(params), len(args)))
    toks = tokenize(body.replace("##", ""), line)
    objects = []
    i = 0
    while i < len(toks):
        if toks[i].kind == "id" and toks[i].text in CLASSES:
            cls = toks[i].text
            j = i + 1
            while toks[j].text != "(":
                j += 1
            ctor_args, end = split_args(toks, j)
            subst = []
            for a in ctor_args:
                out = []
                for t in a:
                    if t.kind == "id" and t.text in params:
                        out.extend(args[params.index(t.text)])
                    else:
                        out.append(t)
                subst.append(out)
            objects.append((cls, subst))
            i = end
        else:
            i += 1
    # A macro that builds no detector, such as REGISTER_CUSTOM_UDEV_RULE or
    # REGISTER_PRE_DETECTION_HOOK, gives none.
    return objects


def function_body(sources, name):
    """The text of the function called name in the family's files."""
    for s in sources:
        text = oc.strip_comments(s.raw)
        m = re.search(r"\b%s\s*\([^;{]*\)\s*\{" % re.escape(name), text)
        if m:
            depth, i = 0, m.end() - 1
            while True:
                if text[i] == "{":
                    depth += 1
                elif text[i] == "}":
                    depth -= 1
                    if depth == 0:
                        return text[m.start():i + 1]
                i += 1
    raise CppError("no function " + name)


def transport_of(sources, function):
    """What a plain detector function opens."""
    body = function_body(sources, function)
    if re.search(r"find_usb_serial_port|serial_port", body):
        return "UsbSerial"
    if re.search(r"\bWmi\b|\bwmi\b|WMI", body + function):
        return "Wmi"
    if re.search(r"\bhid_enumerate\b|\bhid_open", body):
        return "Hid"
    return "Other"


class Registration:
    def __init__(self, **kw):
        self.__dict__.update(kw)


def translation_unit(source, by_rel, family_rels):
    """The family's files a .cpp file compiles with, headers first in the
    order they are included and the .cpp last, so its own #define lines win
    as the compiler meets them. A quoted include resolves in the including
    file's folder first and then in the family's folders, as OpenRGB's
    INCLUDEPATH holds every controller folder (OpenRGB.pro:99-104 and
    136-137). An include outside the family adds no constant a
    registration reads."""
    order = []
    seen = set()

    def visit(src):
        if src.rel in seen:
            return
        seen.add(src.rel)
        here = src.rel.rsplit("/", 1)[0]
        for inc in src.includes:
            target = here + "/" + inc
            if target not in by_rel:
                matches = [r for r in family_rels if r.rsplit("/", 1)[-1] == inc.rsplit("/", 1)[-1]]
                target = matches[0] if matches else None
            if target:
                visit(by_rel[target])
        order.append(src)

    visit(source)
    return order


def read_family(root, family, dm, dm_macros, ctors, hidapi_bus):
    rels = family_files(root, family)
    sources = [oc.Source(root, r) for r in rels]
    by_rel = {s.rel: s for s in sources}
    out = []
    for s in sources:
        if not s.rel.endswith(".cpp"):
            continue
        # Each file is read with what it compiles with: DetectionManager.h,
        # the family headers it includes, its own #define lines and the
        # constants of every enum among them.
        unit = translation_unit(s, by_rel, rels)
        macros = dict(dm_macros)
        for u in unit:
            macros.update({k: v for k, v in u.macros.items() if k.startswith("REGISTER_")})
        constants = {"true": 1, "false": 0}
        constants.update(hidapi_bus)
        ev = oc.Evaluator([dm] + unit, constants)
        for u in [dm] + unit:
            oc.add_enum_constants(ev, u)
        toks = s.tokens
        depth = 0
        for i, t in enumerate(toks):
            if t.text == "{":
                depth += 1
            elif t.text == "}":
                depth -= 1
            if not (t.kind == "id" and t.text.startswith("REGISTER_") and t.text in macros
                    and i + 1 < len(toks) and toks[i + 1].text == "("):
                continue
            args, _ = split_args(toks, i + 1)
            objects = expand(macros[t.text], args, t.line)
            if not any(cls in CLASSES for cls, _ in objects):
                continue
            for cls, ctor_args in objects:
                params = ctors[cls]
                if len(ctor_args) > len(params):
                    raise CppError("%s:%d: too many arguments for %s" % (s.rel, t.line, cls))
                values = {}
                for (pname, default), a in zip(params, ctor_args + [None] * (len(params) - len(ctor_args))):
                    if a is None:
                        if default is None:
                            raise CppError("%s:%d: %s needs %s" % (s.rel, t.line, cls, pname))
                        a = tokenize(default, t.line)
                    values[pname] = a
                detector = "".join(x.text for x in values["detector"])
                from_settings = False
                try:
                    name = ev.string(Expr(values["name"]))
                except CppError:
                    # A dynamic registration's name is a variable read from
                    # the settings too.
                    if depth == 0:
                        raise
                    name = ""
                    from_settings = True
                ints = {}
                for key in ("vid", "pid", "interface", "usage_page", "usage", "bus", "enabled_by_default"):
                    if key not in values:
                        continue
                    try:
                        ints[key] = ev.integer(Expr(values[key]))
                    except CppError:
                        # A dynamic registration's IDs are variables of the
                        # function that reads them from the settings.
                        if depth > 0 and key in ("vid", "pid"):
                            ints[key] = -1
                            from_settings = True
                        else:
                            raise
                transport = CLASSES[cls] or transport_of(sources, detector)
                out.append(Registration(
                    family=family, name=name, detector=detector, macro=t.text, cls=cls, transport=transport,
                    vid=ints.get("vid", -1), pid=ints.get("pid", -1), interface=ints.get("interface", -1),
                    usage_page=ints.get("usage_page", -1), usage=ints.get("usage", -1), bus=ints.get("bus", -1),
                    enabled=bool(ints.get("enabled_by_default", 1)), from_settings=from_settings,
                    source="%s:%d" % (s.rel.split("/")[-1], t.line), invocation=(s.rel, t.line)))
    return out


def row(r):
    return ("            new(%s, %s, %s, %s, OpenRgbTransport.%s, %s, %s, %d, %s, %s, %d, %s, %s, %s),"
            % (cs_string(r.family), cs_string(r.name), cs_string(r.detector), cs_string(r.macro), r.transport,
               cs_hex(r.vid, 4), cs_hex(r.pid, 4), r.interface, cs_hex(r.usage_page, 4), cs_hex(r.usage, 2), r.bus,
               "true" if r.enabled else "false", "true" if r.from_settings else "false", cs_string(r.source)))


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cs_hex(v, width):
    return "-1" if v == -1 else "0x%0*X" % (width, v)


def specimens(root, dm, dm_macros, ctors, hidapi_bus):
    """The first registration of each SPECIMENS form in its family."""
    out = []
    cache = {}
    for family, macro in SPECIMENS:
        if family not in cache:
            cache[family] = read_family(root, family, dm, dm_macros, ctors, hidapi_bus)
        found = [r for r in cache[family] if r.macro == macro]
        if not found:
            sys.exit("%s has no %s" % (family, macro))
        out.append(found[0])
    return out


def write_cs(commit, regs, samples):
    out = []
    w = out.append
    w("// <auto-generated>")
    w("//     Generated by tools/gen_openrgb_registrations.py from OpenRGB at")
    w("//     %s (the detector registrations of the families #557 ports," % commit[:10])
    w("//     decoded from DetectionManager.h). Do not edit by hand.")
    w("// </auto-generated>")
    w("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    w("{")
    w("    internal static partial class OpenRgbRegistrations")
    w("    {")
    w("        /// <summary>The OpenRGB commit the registrations were read at.</summary>")
    w("        internal const string OpenRgbCommit = \"%s\";" % commit)
    w("")
    w("        /// <summary>Every registration, family by family, file by file in path")
    w("        /// order, line by line. -1 is OpenRGB's \"any\".</summary>")
    w("        internal static readonly OpenRgbRegistration[] All =")
    w("        {")
    for r in regs:
        w(row(r))
    w("        };")
    w("")
    w("        /// <summary>The first registration of each macro form the families above")
    w("        /// do not use, read the same way from a family that does, for the tests")
    w("        /// that hold each form's decoding to its source line.</summary>")
    w("        internal static readonly OpenRgbRegistration[] Specimens =")
    w("        {")
    for r in samples:
        w(row(r))
    w("        };")
    w("    }")
    w("}")
    return "\r\n".join(out) + "\r\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--list", action="store_true", help="print the registrations, write nothing")
    args = parser.parse_args()
    root = args.openrgb
    commit = oc.git_head(root)
    dm = oc.Source(root, DETECTION_MANAGER)
    dm_macros = {k: v for k, v in dm.macros.items() if k.startswith("REGISTER_")}
    ctors = constructors(dm)
    hidapi_bus = {k: v for k, v in oc.enum_values(oc.Source(root, HIDAPI), "HID_API_BUS_UNKNOWN").items()}
    regs = []
    for family in FAMILIES:
        if not os.path.isdir(os.path.join(root, "Controllers", family)):
            sys.exit("no family " + family)
        found = read_family(root, family, dm, dm_macros, ctors, hidapi_bus)
        if not found:
            sys.exit("%s has no registration" % family)
        regs.extend(found)
    if args.list:
        for r in regs:
            print("%-34s %-44s %-28s %-9s %5s %5s %3d %6s %5s %s" % (
                r.family, r.name[:44], r.macro, r.transport, cs_hex(r.vid, 4), cs_hex(r.pid, 4), r.interface,
                cs_hex(r.usage_page, 4), cs_hex(r.usage, 2), r.source))
        print(len(regs), "registrations")
        return
    text = write_cs(commit, regs, specimens(root, dm, dm_macros, ctors, hidapi_bus))
    if args.check:
        try:
            with open(OUTPUT, encoding="utf-8", newline="") as f:
                current = f.read()
        except FileNotFoundError:
            current = ""
        print("up to date" if current == text else "differs")
        sys.exit(0 if current == text else 1)
    with open(OUTPUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote %s: %d registrations" % (OUTPUT, len(regs)))


if __name__ == "__main__":
    main()
