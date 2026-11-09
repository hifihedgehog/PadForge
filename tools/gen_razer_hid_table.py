"""The Razer devices OpenRGB lights outside its 90-byte report path (#557).

OpenRGB's RazerController folder lights most Razer products through
RazerController and the 90-byte feature report, which PadForge's Razer worker
speaks (RazerDirectBackend). Five of its detector functions make controllers
of their own, each with its own wire format, and PadForge lights those
through the shared HID worker's families (HidFamilies/RazerKraken*.cs,
RazerHanboLighting.cs, RazerArgbLighting.cs):

- DetectRazerKrakenControllers   RazerKrakenController, 37-byte output reports
- DetectRazerKrakenV3Controllers RazerKrakenV3Controller, 15-byte output reports
- DetectRazerKrakenV4Controllers RazerKrakenV4Controller, 64-byte output reports
- DetectRazerHanboControllers    RazerHanboController, 65-byte output reports
- DetectRazerARGBControllers     RazerController on two interfaces

and the Charging Pad Chroma, whose DetectRazerControllers registration the
Razer worker's table does not list (RazerChargingPadLighting.cs).

WHAT IT READS, at the pinned OpenRGB commit:
- Every REGISTER_*_DETECTOR line of the RazerController folder, decoded the
  way tools/gen_openrgb_registrations.py decodes every other family's (its
  read_family, which this script imports), kept where the detector is one of
  the five above or the line names the Charging Pad Chroma.
- RazerDevices.cpp and RazerDevices.h, read with git show at the pin: each
  kept product's razer_device entry, its name, transaction ID, LED ID, rows,
  columns and zones, in the order OpenRGB lists its LEDs.

WHAT IT WRITES: PadForge.App/Common/Input/Peripherals/HidFamilies/RazerHidTable.g.cs,
with CRLF line endings.

Usage:
    python tools/gen_razer_hid_table.py OPENRGB_ROOT
    python tools/gen_razer_hid_table.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_openrgb_registrations as regs  # noqa: E402
import openrgb_cpp as oc  # noqa: E402

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "HidFamilies",
                      "RazerHidTable.g.cs")
PIN = "b9d903b5"
FAMILY = "RazerController"
DETECTORS = (
    "DetectRazerKrakenControllers",
    "DetectRazerKrakenV3Controllers",
    "DetectRazerKrakenV4Controllers",
    "DetectRazerHanboControllers",
    "DetectRazerARGBControllers",
)
CHARGING_PAD = "Razer Charging Pad Chroma"
DEVICES_CPP = "Controllers/RazerController/RazerDevices.cpp"
DEVICES_H = "Controllers/RazerController/RazerDevices.h"


def show(openrgb, path):
    result = subprocess.run(["git", "-C", openrgb, "show", "%s:%s" % (PIN, path)], capture_output=True, check=True)
    return result.stdout.decode("utf-8")


def blank_comments(text):
    """The source with its comments blanked out, line numbers kept."""
    text = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    return re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), text)


def devices(openrgb):
    """Every razer_device entry of RazerDevices.cpp by product ID: the struct
    of RazerDevices.h, name, pid, type, matrix type, transaction ID, LED ID,
    rows, columns, zones and layout."""
    header = show(openrgb, DEVICES_H)
    pids = {m.group(1): int(m.group(2), 16)
            for m in re.finditer(r"#define\s+(RAZER_\w+_PID)\s+0x([0-9A-Fa-f]+)", header)}
    src = blank_comments(show(openrgb, DEVICES_CPP))
    zones = {}
    for m in re.finditer(r"static const razer_zone (\w+)\s*=\s*\{\s*(\"[^\"]*\"|\w+)\s*,\s*ZONE_TYPE_(\w+)\s*,"
                         r"\s*(\d+)\s*,\s*(\d+)\s*\}", src):
        zones[m.group(1)] = (m.group(2).strip('"'), m.group(3), int(m.group(4)), int(m.group(5)))
    out = {}
    pattern = re.compile(r"static const razer_device (\w+)\s*=\s*\{\s*\"([^\"]*)\"\s*,\s*(\w+)\s*,\s*(\w+)\s*,"
                         r"\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*\{([^}]*)\}\s*,\s*(&?\w+)\s*\}")
    for m in pattern.finditer(src):
        names = [z.strip().lstrip("&") for z in m.group(10).split(",")]
        out[pids[m.group(3)]] = {
            "name": m.group(2),
            "transaction": int(m.group(6), 0),
            "led": led_value(m.group(7)),
            "rows": int(m.group(8)),
            "cols": int(m.group(9)),
            "zones": [zones[z] for z in names if z and z != "NULL"],
            "line": src.count("\n", 0, m.start()) + 1,
        }
    if len(out) < 200:
        raise SystemExit("RazerDevices.cpp: only %d devices read" % len(out))
    return out


# RazerController.h:77-97, the LED IDs a razer_device entry names.
LED_IDS = {"RAZER_LED_ID_ZERO": 0x00, "RAZER_LED_ID_BACKLIGHT": 0x05, "RAZER_LED_ID_LOGO": 0x04,
           "RAZER_LED_ID_SCROLL_WHEEL": 0x01}


def led_value(token):
    if token in LED_IDS:
        return LED_IDS[token]
    return int(token, 0)


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def write_cs(commit, kept, table):
    out = []
    w = out.append
    w("// <auto-generated>")
    w("//     Generated by tools/gen_razer_hid_table.py from OpenRGB at %s" % commit[:10])
    w("//     (Controllers/RazerController: RazerControllerDetect.cpp, RazerDevices.cpp")
    w("//     and RazerDevices.h). Do not edit by hand.")
    w("// </auto-generated>")
    w("namespace PadForge.Common.Input.Peripherals.HidFamilies")
    w("{")
    w("    internal static partial class RazerHidTable")
    w("    {")
    w("        /// <summary>The OpenRGB commit the table was read at.</summary>")
    w("        internal const string OpenRgbCommit = \"%s\";" % commit)
    w("")
    w("        /// <summary>The registrations of the five detectors and the Charging")
    w("        /// Pad Chroma, line by line. -1 is OpenRGB's \"any\".</summary>")
    w("        internal static readonly OpenRgbRegistration[] Registrations =")
    w("        {")
    for r in kept:
        w(regs.row(r))
    w("        };")
    w("")
    w("        /// <summary>The razer_device entry of each product the registrations")
    w("        /// name that OpenRGB lists, in product ID order.</summary>")
    w("        internal static readonly RazerHidDevice[] Devices =")
    w("        {")
    for pid in sorted(table):
        d = table[pid]
        zones = ", ".join("new(%s, RazerHidZoneType.%s, %d, %d)" % (cs_string(n), t.capitalize(), r, c)
                          for n, t, r, c in d["zones"])
        w("            new(0x%04X, %s, 0x%02X, 0x%02X, %d, %d, new RazerHidZone[] { %s }, \"RazerDevices.cpp:%d\")," % (
            pid, cs_string(d["name"]), d["transaction"], d["led"], d["rows"], d["cols"], zones, d["line"]))
    w("        };")
    w("    }")
    w("}")
    return "\r\n".join(out) + "\r\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("openrgb")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = args.openrgb
    commit = oc.git_head(root)
    if not commit.startswith(PIN):
        sys.exit("OpenRGB is at %s, not %s" % (commit[:10], PIN))
    dm = oc.Source(root, regs.DETECTION_MANAGER)
    dm_macros = {k: v for k, v in dm.macros.items() if k.startswith("REGISTER_")}
    ctors = regs.constructors(dm)
    hidapi_bus = {k: v for k, v in oc.enum_values(oc.Source(root, regs.HIDAPI), "HID_API_BUS_UNKNOWN").items()}
    found = regs.read_family(root, FAMILY, dm, dm_macros, ctors, hidapi_bus)
    kept = [r for r in found if r.detector in DETECTORS or r.name == CHARGING_PAD]
    for detector in DETECTORS:
        if not any(r.detector == detector for r in kept):
            sys.exit("no registration of " + detector)
    if not any(r.name == CHARGING_PAD for r in kept):
        sys.exit("no registration of the Charging Pad Chroma")
    every = devices(root)
    table = {r.pid: every[r.pid] for r in kept if r.pid in every}
    text = write_cs(commit, kept, table)
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
    print("wrote %s: %d registrations, %d devices" % (OUTPUT, len(kept), len(table)))
    for r in kept:
        print("  %-40s %-32s %04X if=%d page=%s usage=%s %s" % (r.name, r.detector, r.pid, r.interface,
                                                             regs.cs_hex(r.usage_page, 4), regs.cs_hex(r.usage, 2),
                                                             r.source))
    missing = sorted({r.pid for r in kept} - set(table))
    print("  not in device_list:", ", ".join("%04X" % p for p in missing))


if __name__ == "__main__":
    main()
