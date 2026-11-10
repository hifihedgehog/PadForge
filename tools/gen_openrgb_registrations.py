"""OpenRGB's device detector registrations for the families #557 ports.

OpenRGB finds a device by its detector registrations: one REGISTER_*_DETECTOR
line per device and interface in each family's *Detect.cpp, each a macro of
DetectionManager.h (lines 531-603) that constructs a detector object with a
name, a detector function and, for a HID device, the vendor and product IDs,
the interface, the usage page and the usage it answers to. This script reads
every registration of the 54 families issue #557 ports at an OpenRGB commit,
and of OpenRGB's families for other lit peripherals (PERIPHERAL_FAMILIES),
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

Every other folder of OpenRGB's Controllers has a decision here: a family
PadForge lights another way (LIT_ANOTHER_WAY), one no Windows build compiles
(NOT_ON_WINDOWS), or one #557's coverage table left out at OpenRGB c6fc8016
(DECIDED_IN_557: parts inside a PC, lights on the network, code for Linux or
testing, and the rest of that table's rows). A ported family of which only some
products are lit names them in SOME_PRODUCTS. The folders are read from git at
the clone's commit, so an empty folder left on disk counts for nothing. The run
stops on a folder with no decision, so a family OpenRGB adds is named at
release prep until it is ported or given its reason.

Usage:
    python tools/gen_openrgb_registrations.py OPENRGB_ROOT
    python tools/gen_openrgb_registrations.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
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

# OpenRGB's other families for lit peripherals and laptop lights, each its own
# folder at the pinned commit: mice, mousepads, microphones, speakers and a
# headset stand, a keypad, gamepads, desk lights and toys, monitors and their
# light bars, and a laptop light bar. The families for parts inside a PC
# (motherboards, graphics cards, memory, coolers, fans, cases) and for lights
# on the network are not peripherals and are not listed.
PERIPHERAL_FAMILIES = [
    "AOCMouseController", "AOCMousematController", "AlienwareMonitorController", "AresonController",
    "AsusMonitorController", "BlinkyTapeController", "ClevoLightbarController", "CorsairLightingNodeController",
    "CorsairVoidController", "CreativeController",
    "DreamCheekyController", "EdifierHaloController", "GameSirController", "GigabyteAorusMouseController",
    "HYTEMousematController", "HoltekController", "HyperXMicrophoneController", "HyperXMicrophoneV2Controller",
    "HyperXMouseController", "HyperXMousematController", "InstantMouseController", "LGMonitorController",
    "LaviewTechnologyController", "LegoDimensionsToypadBaseController", "LexipMouseController",
    "LuxaforController", "MSIClutchController", "MSIMonitorController", "MSIOptixController",
    "MadCatzCyborgController", "N5312AController", "NZXTMouseController", "PatriotViperMouseController",
    "RedragonController", "RobobloqLightStripController", "SkydimoHIDController", "SkydimoSerialController",
    "StreamDeckController", "TecknetController", "ThingMController", "ThrustmasterSolController",
    "TrustController", "TurtleBeachKP7Controller", "ViewSonicController", "WushiController",
]

ALL_FAMILIES = FAMILIES + PERIPHERAL_FAMILIES

# The families PadForge lights another way, each with how.
LIT_ANOTHER_WAY = {
    "HIDLampArrayController": "any HID LampArray, through LampArrayBackend",
    "LogitechController": "PadForge's Logitech families",
    "RazerController": "PadForge's Razer tables",
    "SteelSeriesController": "PadForge's SteelSeries families",
    # Added after c6fc8016. Its lamps are a HID LampArray on interface 5
    # (KLIPSCH_LUMINA_LAMPARRAY_IFACE, KlipschLuminaControllerDetect.cpp:27-28), which
    # LampArrayBackend lights as it lights any LampArray. OpenRGB writes the vendor
    # interface on Windows because the system holds the LampArray (lines 40-53 and 91).
    "KlipschLuminaController": "its HID LampArray, through LampArrayBackend",
}

# The families no Windows build compiles, each with why.
NOT_ON_WINDOWS = {
    # Added after c6fc8016: every file carries _Linux, and it lights through the
    # kernel's /sys/class/leds (RetroidAYNController_Linux.cpp).
    "RetroidAYNController": "Linux only",
}

# Ported families of which only some products are lit, each with the
# products kept. The Lighting Node family's other five registrations are
# parts inside a PC, #557's rule: the Lighting Node Core and Pro, the
# Commander Pro and the 1000D and SPEC OMEGA cases
# (CorsairLightingNodeControllerDetect.cpp:44-50). The LT100 towers stand on a
# desk and the LS100 strips go behind a monitor.
SOME_PRODUCTS = {
    "CorsairLightingNodeController": {(0x1B1C, 0x0C1E), (0x1B1C, 0x0C23)},
}

# The other folders OpenRGB had at c6fc8016, where #557 settled its coverage.
DECIDED_IN_557 = (
    "AMBXController", "AMDWraithPrismController", "ASRockPolychromeUSBController", "ASRockSMBusController",
    "ArcticController", "AsusAuraGPUController", "ColorfulGPUController", "ColorfulTuringGPUController",
    "CorsairCommanderCoreController", "CorsairController", "CorsairDRAMController", "CorsairHydro2Controller",
    "CorsairHydroController", "CorsairHydroPlatinumController", "CorsairICueLinkController",
    "CorsairVengeanceController", "CrucialController",
    "CryorigH7QuadLumiController", "DDPController", "DMXController", "DRGBController", "DebugController",
    "E131Controller", "EKController", "ENESMBusController", "EVGAAmpereGPUController",
    "EVGAGP102GPUController", "EVGAPascalGPUController", "EVGASMBusController", "EVGATuringGPUController",
    "ElgatoKeyLightController", "ElgatoLightStripController", "EspurnaController", "FanBusController",
    "FaustusController", "FrameworkDesktopController", "GalaxGPUController",
    "GigabyteAorusCPUCoolerController", "GigabyteAorusPCCaseController", "GigabyteCastor3Controller",
    "GigabyteRGBFusion2BlackwellGPUController", "GigabyteRGBFusion2DRAMController",
    "GigabyteRGBFusion2GPUController", "GigabyteRGBFusion2SMBusController", "GigabyteRGBFusion2USBController",
    "GigabyteRGBFusionController", "GigabyteRGBFusionGPUController", "GigabyteSuperIORGBController",
    "GoveeController", "HPOmen30LController", "HYTENexusController", "HyperXDRAMController",
    "IntelArcA770LEController", "JGINYUEInternalUSBController", "JGINYUEInternalUSBV2Controller",
    "KasaSmartController", "KingstonFuryDRAMController", "LEDStripController", "LIFXController",
    "LenovoMotherboardController", "LianLiController", "LinuxLEDController", "MSIGPUController",
    "MSIMotherboardController", "MSIRGBController", "ManliGPUController", "NVIDIAIlluminationController",
    "NZXTHue1Controller", "NZXTHue2Controller", "NZXTHuePlusController", "NZXTKrakenController",
    "NanoleafController", "NollieController", "NvidiaESAController", "PNYARGBEpicXGPUController",
    "PNYLovelaceGPUController", "PalitGPUController", "PatriotViperController", "PatriotViperSteelController",
    "PhilipsHueController", "PhilipsWizController", "PowerColorGPUController", "RealtekARGBController",
    "RealtekBridgeController", "SRGBmodsController", "SapphireGPUController", "SeagateController",
    "SonyGamepadController", "TForceXtreemController", "ThermaltakeRiingController",
    "ValveSteamMachineController", "YeelightController", "ZalmanZSyncController",
    "ZotacBlackwellGPUController", "ZotacTuringGPUController", "ZotacV2GPUController",
)

# Macro forms none of the 54 families uses, each decoded from a family that
# does and held to the values on its source line every run, so a form the
# 54 take up later is read right. They are checked only, never written.
# (family, macro, source, (vid, pid, interface, usage page, usage, bus))
FORM_CHECKS = [
    # REGISTER_HID_WRAPPED_DETECTOR_I, DetectionManager.h:576. HYPERX_VID
    # 0x0951 and HYPERX_QS_PID 0x171F, HyperXMicrophoneControllerDetect.cpp.
    ("HyperXMicrophoneController", "REGISTER_HID_WRAPPED_DETECTOR_I", "HyperXMicrophoneControllerDetect.cpp:52",
     (0x0951, 0x171F, 0, -1, -1, -1)),
    # REGISTER_HID_WRAPPED_DETECTOR_IPU, line 578. HYPERX_VID_2 0x03F0 and
    # HYPERX_PULSEFIRE_PID 0x0F8D, HyperXMousematControllerDetect.cpp.
    ("HyperXMousematController", "REGISTER_HID_WRAPPED_DETECTOR_IPU", "HyperXMousematControllerDetect.cpp:49",
     (0x03F0, 0x0F8D, 1, 0xFF90, 0xFF00, -1)),
    # REGISTER_USB_SERIAL_DETECTOR, line 592: no interface, page or usage.
    ("BlinkyTapeController", "REGISTER_USB_SERIAL_DETECTOR", "BlinkyTapeControllerDetect.cpp:37",
     (0x1D50, 0x605E, -1, -1, -1, -1)),
    # REGISTER_HID_DETECTOR_BPU and _BP, lines 563-564, with
    # HID_API_BUS_USB, 1 (hidapi.h:129).
    ("LogitechController", "REGISTER_HID_DETECTOR_BPU", "LogitechControllerDetect.cpp:1791",
     (0x046D, -1, -1, 0xFF00, 2, 1)),
    ("LogitechController", "REGISTER_HID_DETECTOR_BP", "LogitechControllerDetect.cpp:1792",
     (0x046D, -1, -1, 0xFF43, -1, 1)),
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


def check_forms(root, dm, dm_macros, ctors, hidapi_bus):
    """Stops the script when a FORM_CHECKS line decodes to other values than
    its source line holds."""
    cache = {}
    for family, macro, source, expected in FORM_CHECKS:
        if family not in cache:
            cache[family] = read_family(root, family, dm, dm_macros, ctors, hidapi_bus)
        found = [r for r in cache[family] if r.source == source]
        if len(found) != 1 or found[0].macro != macro:
            sys.exit("form check: %s is not one %s line" % (source, macro))
        r = found[0]
        got = (r.vid, r.pid, r.interface, r.usage_page, r.usage, r.bus)
        if got != expected:
            sys.exit("form check: %s decodes to %s, its line holds %s" % (source, got, expected))


def write_cs(commit, regs):
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
    folders = subprocess.run(["git", "-C", root, "ls-tree", "--name-only", "HEAD", "Controllers/"],
                             capture_output=True, text=True, check=True).stdout.split()
    folders = [f.split("/", 1)[1] for f in folders if "/" in f]
    decided = set(ALL_FAMILIES) | set(LIT_ANOTHER_WAY) | set(NOT_ON_WINDOWS) | set(DECIDED_IN_557)
    new = [f for f in folders if f not in decided]
    if new:
        sys.exit("OpenRGB families with no decision: " + ", ".join(new)
                 + ". Port each, or add it to LIT_ANOTHER_WAY or NOT_ON_WINDOWS with its reason.")
    gone = sorted(decided - set(folders))
    if gone:
        sys.exit("families OpenRGB no longer has: " + ", ".join(gone) + ". Take them out of the lists.")
    regs = []
    for family in ALL_FAMILIES:
        if not os.path.isdir(os.path.join(root, "Controllers", family)):
            sys.exit("no family " + family)
        found = read_family(root, family, dm, dm_macros, ctors, hidapi_bus)
        if family in SOME_PRODUCTS:
            kept = SOME_PRODUCTS[family]
            missing = kept - {(r.vid, r.pid) for r in found}
            if missing:
                sys.exit("%s no longer registers %s" % (family, ", ".join("%04X:%04X" % p for p in sorted(missing))))
            found = [r for r in found if (r.vid, r.pid) in kept]
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
    check_forms(root, dm, dm_macros, ctors, hidapi_bus)
    text = write_cs(commit, regs)
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
