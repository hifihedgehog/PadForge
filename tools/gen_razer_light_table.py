"""The Razer light maps, light by light (#557).

WHAT IT READS.
- tools/data/razer-direct-table.json through tools/gen_razer_direct_table.py,
  which turns each research entry into the row RazerDirectTable.cs holds: the
  family that paints the product, its LEDs and its frame.
- OpenRGB's Controllers/RazerController/RazerDevices.cpp and RazerDevices.h at
  the pinned commit, read with git show, so the clone's checkout does not
  matter: each product's zones, in the order OpenRGB lists its LEDs, and its
  matrix, and the layout a keyboard or keypad zone is laid out by. OpenRGB
  numbers the LEDs zone after zone (RGBController_Razer.cpp:204-340) and
  sends LED n as cell n of its frame, row after row, the device's columns
  wide (RazerController.cpp:207-258), so a light's cell is its place in that
  list.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/RazerLightTable.g.cs, one
row for each product with more than one light:
- A product painted by frame (standard matrix, one-row extended frame,
  extended matrix frame) keeps the frame RazerDirectTable gives it, and its
  lights are OpenRGB's at the row and column OpenRGB sends them to.
- A product painted by effect on the extended matrix is painted by frame when
  OpenRGB lists more lights for it than its LEDs, or keys, with OpenRGB's
  matrix: that is the frame OpenRGB paints it with in its Direct mode. A
  keyboard OpenRGB does not list is painted by openrazer's frame.
- A product painted one report per LED with two LEDs or more is painted by
  LED, each LED a light named by its LED ID (razercommon.h:37-48).
A product with neither stays one color.

KEYS. A keyboard or keypad zone that OpenRGB lays out with its
KeyboardLayoutManager is written as the layout's name and the zone's place;
RazerLightTable lays it out at run time in the region the keyboard reports,
through the port in HidFamilies. A keyboard zone with no layout, and a
keyboard OpenRGB does not list, takes its keys from a second source where
one fits (tools/razer_key_sources.py: uchroma's device files and
Polychromatic's device maps, at their pinned commits, read from the clones
beside OPENRGB_ROOT), and every cell no source names is a key for Map Key
Lights. The output says for each such board which source named it, or why
none did.

HOW A LIGHT IS NAMED. A logo is a logo light, an LED strip, underglow, side
light or a dock's base is edge lighting, a keypad key is a key no source has
named (Map Key Lights can name it), the Void zone of a BlackWidow V4 Pro is
no light (its LEDs "were not connected", RazerDevices.cpp:1820), and anything
else, a scroll wheel or a Naga's side panel, is a numbered zone.

WHERE THE SOURCES DISAGREE the script says so on the row's comment and in its
output: a frame OpenRGB gives another size than openrazer, or than the frame
the product is painted with.

Usage:
    python tools/gen_razer_light_table.py OPENRGB_ROOT
    python tools/gen_razer_light_table.py OPENRGB_ROOT --check   (compare, write nothing)
"""
import argparse
import json
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_razer_direct_table as table  # noqa: E402
import razer_key_sources as key_sources  # noqa: E402

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "RazerLightTable.g.cs")
PIN = "b9d903b5"
DEVICES_CPP = "Controllers/RazerController/RazerDevices.cpp"
DEVICES_H = "Controllers/RazerController/RazerDevices.h"

# LED IDs, razercommon.h:37-48 at openrazer 477f4d6d.
LED_KIND = {0x01: "Zone", 0x04: "Logo", 0x05: "Zone", 0x0A: "Edge", 0x10: "Edge", 0x11: "Edge"}


def show(openrgb, path):
    result = subprocess.run(["git", "-C", openrgb, "show", "%s:%s" % (PIN, path)], capture_output=True, check=True)
    return result.stdout.decode("utf-8")


def blank_comments(text):
    """The source with its comments blanked out, line numbers kept."""
    text = re.sub(r"/\*.*?\*/", lambda m: re.sub(r"[^\n]", " ", m.group(0)), text, flags=re.S)
    return re.sub(r"//[^\n]*", lambda m: " " * len(m.group(0)), text)


def openrgb_devices(openrgb):
    header = show(openrgb, DEVICES_H)
    pids = {m.group(1): int(m.group(2), 16)
            for m in re.finditer(r"#define\s+(RAZER_\w+_PID)\s+0x([0-9A-Fa-f]+)", header)}
    src = blank_comments(show(openrgb, DEVICES_CPP))
    zones = {}
    for m in re.finditer(r"static const razer_zone (\w+)\s*=\s*\{\s*(\"[^\"]*\"|\w+)\s*,\s*(\w+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\}", src):
        zones[m.group(1)] = (m.group(2).strip('"'), int(m.group(4)), int(m.group(5)))
    devices = {}
    pattern = re.compile(r"static const razer_device (\w+)\s*=\s*\{\s*\"([^\"]*)\"\s*,\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*,"
                         r"\s*(\w+)\s*,\s*(\w+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*\{([^}]*)\}\s*,\s*(&?\w+)\s*\}")
    for m in pattern.finditer(src):
        names = [z.strip().lstrip("&") for z in m.group(10).split(",")]
        listed = [zones[z] for z in names if z and z != "NULL"]
        devices[pids[m.group(3)]] = {
            "name": m.group(2),
            "type": m.group(4),
            "matrix": m.group(5).replace("RAZER_MATRIX_TYPE_", ""),
            "rows": int(m.group(8)),
            "cols": int(m.group(9)),
            "zones": listed,
            "layout": m.group(11) != "NULL",
            "layout_name": m.group(11).lstrip("&"),
            "line": src.count("\n", 0, m.start()) + 1,
        }
    if len(devices) < 200:
        raise SystemExit("RazerDevices.cpp: only %d devices read" % len(devices))
    return devices


def zone_kind(name):
    if name in ("Logo", "Logo Zone", "Left Logo", "Right Logo"):
        return "Logo"
    if any(word in name for word in ("Strip", "Underglow", "Chroma Zone", "Channel", "Speaker")) \
            or name in ("Base", "Mousemat", "Case LEDs"):
        return "Edge"
    if name.startswith("Keypad"):
        return "Key"
    return "Zone"


KEYBOARD_ZONES = ("ZONE_EN_KEYBOARD", "Keyboard")


def is_key_grid(device):
    """A keyboard, laptop or keypad whose lights are keys: a keyboard zone of
    more than one row, or a zone the KeyboardLayoutManager lays out, which
    OpenRGB does for a keyboard or keypad zone of a product with a layout
    (RGBController_Razer.cpp:229-233)."""
    for name, rows, cols in device["zones"]:
        if name in KEYBOARD_ZONES and rows > 1:
            return True
        if device["layout"] and (name in KEYBOARD_ZONES or name == "Keypad"):
            return True
    return False


def openrgb_cells(device):
    """Every LED OpenRGB lists, in its order: (zone name, LED index, row,
    column) on the device's frame. OpenRGB numbers the LEDs zone after zone
    (RGBController_Razer.cpp:212-336) and sends LED n as cell n of its frame,
    row after row, the device's columns wide (RazerController.cpp:207-258)."""
    cells, index = [], 0
    orbweaver = device["matrix"] == "CUSTOM" and device["name"] == "Razer Orbweaver Chroma"
    for name, rows, cols in device["zones"]:
        for _ in range(rows * cols):
            if orbweaver:
                # OpenRGB shows the Orbweaver as 4 rows of 5 and sends each
                # row one row down and one column right on the 5 by 22
                # standard frame, and its fourth row's first key alone, the
                # other four two columns right (RazerController.cpp:1536-1566).
                row, col = divmod(index, 5)
                col = 1 if row == 3 and col == 0 else col + 2 if row == 3 else col + 1
                cells.append((name, index, row + 1, col))
            else:
                row, col = divmod(index, device["cols"])
                cells.append((name, index, row, col))
            index += 1
    return cells


def numbered(lights):
    """The lights with each zone numbered from 1 in order."""
    out, zone = [], 0
    for kind, address, _, row, col in lights:
        if kind == "Zone":
            zone += 1
        out.append((kind, address, zone if kind == "Zone" else 0, row, col))
    return out


def static_lights(device, rows, cols, note, keyboard_cells, positioned):
    """OpenRGB's LEDs as lights on a frame of rows by cols, but for the cells
    of a keyboard zone laid out at run time and the Void zone, whose LEDs
    "were not connected" (RazerDevices.cpp:1820)."""
    lights, dropped = [], 0
    for name, index, row, col in openrgb_cells(device):
        if name == "Void" or (name in KEYBOARD_ZONES or name == "Keypad") and keyboard_cells:
            continue
        kind = zone_kind(name)
        if name in KEYBOARD_ZONES:
            kind = "Zone"
        if row >= rows or col >= cols:
            dropped += 1
            continue
        lights.append((kind, row * cols + col, 0, row if positioned else -1, col if positioned else -1))
    if dropped:
        note.append("%d of OpenRGB's lights lie outside the %dx%d frame" % (dropped, rows, cols))
    return numbered(lights)


def keyboard_zone(device):
    """The keyboard or keypad zone's place: its first LED index, rows and
    columns."""
    base = 0
    for name, rows, cols in device["zones"]:
        if name in KEYBOARD_ZONES or name == "Keypad":
            return base, rows, cols
        base += rows * cols
    return None


# A board whose OpenRGB layout misplaces one row, which a map drawn for the
# board has right: product to row and map. OpenRGB's Blade 15 Advanced (2021)
# overlay inserts Esc at column 1, which moves the top row one column right,
# then swaps columns 14 and 15 for Insert and Delete as if PrtSc and ScrLk sat
# there, where F12 and PrtSc sit after the shift (RazerDevices.cpp:562-603). F1
# to F11 land a column off and no cell is F12. Polychromatic's map for the board
# (c12135c) has Esc, F1 to F12, Insert and Delete in columns 1 to 15, the same
# two keys at the row's end as the overlay. uchroma's entry ends the row Delete,
# Power (laptop.yaml:462-463), and is not read here: the board has an Insert key
# and its power button sits apart from the keyboard.
ROW_FROM_SECOND_SOURCE = {0x0276: (0, "blade_advanced_2021_en_US.svg")}

# Cells OpenRGB's layout leaves empty on a board, where both sources for the
# board put the key OpenRGB has beside it: product to the cell, then the key's
# cell. The Blade's overlay moves Backspace into the matrix's last column (line
# 590) and leaves \ and Enter a column short of it. Polychromatic's map names
# them in column 15 alone, uchroma's entry in columns 14 and 15. A cell with no
# LED behind it changes nothing, and the cell takes whatever key the region has
# beside it, since those two sources draw only the US layout.
SAME_KEY_CELLS = {0x0276: ((2, 15, 2, 14), (3, 15, 3, 14))}


def named_lights(named, cols):
    """The lights of the keys a second source names, each at its cell."""
    lights = []
    for (r, c), (usage, label) in sorted(named.items()):
        if usage:
            lights.append(("Named", r * cols + c, usage, r, c))
        else:
            lights.append(("Labeled", r * cols + c, label, r, c))
    return lights


def plan_for(pid, row, entry, device, sources=None, drawn=None):
    """The light row of one product as a dict, or a reason it has none.
    sources names the keys of a board OpenRGB lays out with no layout, from
    tools/razer_key_sources.py: (keys by cell, text) or (None, reason). drawn
    reads one Polychromatic map: (keys by cell, text)."""
    family, flags = row["family"], row["flags"]
    note = []
    source = "OpenRGB RazerDevices.cpp:%d at %s" % (device["line"], PIN) if device is not None else None
    keyboard = entry["list"] == "keyboard"
    stream = (entry["plan"].get("stream") or {}).get("matrix_openrazer")
    grid = device is not None and is_key_grid(device)

    # The frame painted: a frame product keeps its own. A product painted by
    # effect takes OpenRGB's frame, or openrazer's for a keyboard OpenRGB
    # does not list.
    if family == "std" and "HeldEffect" in flags:
        return "painted by an effect with no frame"
    if family in ("std", "ext_matrix"):
        rows, cols = row["rows"], row["columns"]
    elif family == "ext_frame1":
        rows, cols = 1, row["columns"]
    elif family == "ext" and device is not None and device["matrix"] == "EXTENDED":
        rows, cols = device["rows"], device["cols"]
        if stream and list(stream) != [rows, cols]:
            note.append("openrazer %dx%d" % tuple(stream))
    elif family == "ext" and device is None and keyboard and stream and stream[0] > 1:
        rows, cols = stream
    else:
        rows = cols = 0

    if rows and device is None and keyboard and rows > 1:
        # No OpenRGB entry: each cell of openrazer's frame is a key, named by
        # a second source where one fits, else for Map Key Lights.
        named, why = sources(pid, entry["name"], rows, cols, None) if sources else (None, "no second source read")
        if named:
            return {"plan": "Frame", "rows": rows, "cols": cols, "lights": named_lights(named, cols),
                    "keys": (None, 0, rows, cols, cols), "positioned": True,
                    "source": "openrazer frame of %d by %d, keys from %s" % (rows, cols, why),
                    "note": "no OpenRGB entry, keys from a second source"}
        return {"plan": "Frame", "rows": rows, "cols": cols, "lights": [],
                "keys": (None, 0, rows, cols, cols), "positioned": True,
                "source": "openrazer frame of %d by %d, keys unnamed" % (rows, cols),
                "note": "no OpenRGB entry, keys unnamed: " + why}
    if rows and device is not None:
        if device["matrix"] != "CUSTOM" and (device["rows"], device["cols"]) != (rows, cols):
            note.append("OpenRGB %dx%d, painted %dx%d" % (device["rows"], device["cols"], rows, cols))
        keys = None
        if grid:
            base, key_rows, key_cols = keyboard_zone(device)
            recipe = device["layout_name"] if device["layout"] else None
            keys = (recipe, base, key_rows, key_cols, device["cols"])
            named = None
            if recipe is None:
                named, why = (sources(pid, entry["name"], rows, cols, (key_rows, key_cols)) if sources
                              else (None, "no second source read"))
                if named and base != 0:
                    named, why = None, "the keyboard zone is not OpenRGB's first"
                if named:
                    note.append("no layout in OpenRGB, keys from a second source")
                    source = "%s, keys from %s" % (source, why)
                else:
                    note.append("no layout in OpenRGB, keys unnamed: " + why)
            elif pid in ROW_FROM_SECOND_SOURCE and drawn:
                fixed, filename = ROW_FROM_SECOND_SOURCE[pid]
                every, why = drawn(filename)
                named = {cell: key for cell, key in every.items() if cell[0] == fixed}
                if not named:
                    raise SystemExit("%04X: %s names nothing in row %d" % (pid, why, fixed))
                note.append("row %d from a second source, OpenRGB's layout misplaces it" % fixed)
                source = "%s, row %d keys from %s" % (source, fixed, why)
        lights = static_lights(device, rows, cols, note, keyboard_cells=grid, positioned=grid)
        if grid and named:
            lights = named_lights(named, cols) + lights
        count = len(lights) + (keys[2] * keys[3] if keys else 0)
        if count > 1 and (family != "ext" or grid or count > len(row["leds"])):
            if not grid and len(lights) < rows * cols and family == "ext":
                note.append("%d lights in %d cells" % (len(lights), rows * cols))
            return {"plan": "Frame", "rows": rows, "cols": cols, "lights": lights, "keys": keys,
                    "positioned": grid, "source": source, "note": "; ".join(note),
                    "same_key_cells": SAME_KEY_CELLS.get(pid, ())}
    if family in ("ext", "mext", "classic"):
        leds = row["leds"]
        if len(leds) < 2:
            return "one light"
        lights = numbered([(LED_KIND.get(led, "Zone"), led, 0, -1, -1) for led in leds])
        return {"plan": "PerLed", "rows": 0, "cols": 0, "lights": lights, "keys": None, "positioned": False,
                "source": "openrazer LED IDs, razercommon.h:37-48", "note": ""}
    if rows and device is None:
        lights = numbered([("Zone", cell, 0, -1, -1) for cell in range(rows * cols)])
        if len(lights) < 2:
            return "one light"
        return {"plan": "Frame", "rows": rows, "cols": cols, "lights": lights, "keys": None, "positioned": False,
                "source": "openrazer frame of %d by %d, numbered" % (rows, cols), "note": "no OpenRGB entry"}
    return "one light"


def emit(rows, commit):
    out = []
    add = out.append
    frames = sum(1 for _, r in rows if r["plan"] == "Frame")
    keyed = sum(1 for _, r in rows if r["keys"])
    add("// <auto-generated>")
    add("//     Generated by tools/gen_razer_light_table.py from tools/data/razer-direct-table.json")
    add("//     and OpenRGB %s (Controllers/RazerController/RazerDevices.cpp). Do not edit" % commit[:10])
    add("//     by hand. %d products are painted light by light: %d by frame, %d by LED, and" % (
        len(rows), frames, len(rows) - frames))
    add("//     %d of them have keys, %d laid out by OpenRGB's KeyboardLayoutManager." % (
        keyed, sum(1 for _, r in rows if r["keys"] and r["keys"][0])))
    add("// </auto-generated>")
    add("namespace PadForge.Common.Input.Peripherals")
    add("{")
    add("    internal static partial class RazerLightTable")
    add("    {")
    add("        /// <summary>The OpenRGB commit the lights were read at.</summary>")
    add("        internal const string OpenRgbCommit = \"%s\";" % commit)
    add("")
    add("        private static readonly RazerLightRow[] s_rows =")
    add("        {")
    for pid, r in rows:
        plan = r["plan"]
        parts = ["ProductId = 0x%04X" % pid, "Plan = RazerLightPlan.%s" % plan]
        if plan == "Frame":
            parts += ["Rows = %d" % r["rows"], "Columns = %d" % r["cols"]]
        if r["keys"]:
            recipe, base, key_rows, key_cols, device_cols = r["keys"]
            if recipe:
                parts.append('KeyboardRecipe = "%s"' % recipe)
            parts += ["KeyBase = %d" % base, "KeyRows = %d" % key_rows, "KeyColumns = %d" % key_cols,
                      "DeviceColumns = %d" % device_cols]
        calls = []
        for kind, address, zone, row, col in r["lights"]:
            a = "0x%02X" % address if plan == "PerLed" else str(address)
            if kind == "Named":
                call = "Named(%s, 0x%02X)" % (a, zone)
            elif kind == "Labeled":
                call = "Labeled(%s, LightLabel.%s)" % (a, zone)
            else:
                call = "Zone(%s, %d)" % (a, zone) if kind == "Zone" else "%s(%s)" % (kind, a)
            calls.append("At(%s, %d, %d)" % (call, row, col) if row >= 0 else call)
        if calls:
            parts.append("Lights = new[] { %s }" % ", ".join(calls))
        if r.get("same_key_cells"):
            parts.append("SameKeyCells = new[] { %s }" % ", ".join("(%d, %d, %d, %d)" % c for c in r["same_key_cells"]))
        parts.append('Source = "%s"' % r["source"])
        add("            new() { %s },%s" % (", ".join(parts), " // " + r["note"] if r["note"] else ""))
    add("        };")
    add("    }")
    add("}")
    return "\r\n".join(out) + "\r\n"


def main():
    parser = argparse.ArgumentParser(description="Writes RazerLightTable.g.cs.")
    parser.add_argument("openrgb", help="the OpenRGB clone; read at %s" % PIN)
    parser.add_argument("--json", default=table.RESEARCH)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    commit = subprocess.run(["git", "-C", args.openrgb, "rev-parse", PIN], capture_output=True, text=True,
                            check=True).stdout.strip()
    devices = openrgb_devices(args.openrgb)
    parent = os.path.dirname(os.path.abspath(args.openrgb))
    uch_repo, poly_repo = os.path.join(parent, "uchroma"), os.path.join(parent, "polychromatic")
    uch_devices = key_sources.uchroma_devices(uch_repo)
    poly_maps = key_sources.polychromatic_maps(poly_repo)

    def sources(pid, name, rows_, cols_, zone):
        return key_sources.name_keys(pid, name, rows_, cols_, zone, uch_repo, poly_repo, uch_devices, poly_maps)

    def drawn(filename):
        return (key_sources.polychromatic_keys(poly_repo, filename),
                "Polychromatic %s %s" % (filename, key_sources.POLYCHROMATIC_PIN))

    research = json.load(open(args.json, encoding="utf-8"))["devices"]
    rows, skipped = [], []
    for pid_text in sorted(research):
        entry = research[pid_text]
        row = table.to_row(pid_text, entry)
        if row["family"] not in table.COLOR_FAMILIES:
            continue
        pid = int(pid_text, 16)
        result = plan_for(pid, row, entry, devices.get(pid), sources, drawn)
        if isinstance(result, str):
            skipped.append((pid_text, entry["name"], result))
            continue
        rows.append((pid, result))
        if result["note"]:
            print("%s %s: %s" % (pid_text, entry["name"], result["note"]))
    text = emit(rows, commit)
    print("%d rows: %d by frame, %d by LED, %d with keys; %d one color" % (
        len(rows), sum(1 for _, r in rows if r["plan"] == "Frame"), sum(1 for _, r in rows if r["plan"] == "PerLed"),
        sum(1 for _, r in rows if r["keys"]), len(skipped)))
    for pid_text, name, reason in skipped:
        print("  one color: %s %s (%s)" % (pid_text, name, reason))
    if args.check:
        try:
            with open(OUTPUT, encoding="utf-8", newline="") as f:
                current = f.read()
        except FileNotFoundError:
            current = ""
        print("RazerLightTable.g.cs is %s" % ("up to date" if current == text else "OUT OF DATE"))
        return 0 if current == text else 1
    with open(OUTPUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote %s" % os.path.relpath(OUTPUT, PROJ_ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
