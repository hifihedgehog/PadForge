#!/usr/bin/env python3
"""Writes PadForge.Tests/HidFamilies/OtherProjectsInventory.g.cs (#557).

    python tools/gen_other_lighting_inventory.py GITHUB_FOLDER [--check]

Every product the other open lighting projects name, for PluginInventoryTests: each is
lit by a family of PadForge's or stands in that test's list with its reason, so a device
one of them adds fails there by name when the pins below move at release prep.

alienfx-tools names no products. It goes by vendor and report length, and so does
AlienFxLighting.
"""
import argparse
import os
import re
import subprocess
import sys

PINS = {
    "ckb-next": "e22eaf2fba7f93764b4c6d4413bb83eeca75d10a",
    "libratbag": "b8d4d3ca1f4d6b23c664ffee2888b8eb669bee21",
    "g-helper": "f62d584d24d4ea12ff902b873f9127edafbcd742",
    "asusctl": "321b1114a0ac3782d67aa39201682effb749f4d9",
}
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "PadForge.Tests", "HidFamilies", "OtherProjectsInventory.g.cs")


def show(root, project, path):
    done = subprocess.run(["git", "-C", f"{root}/{project}", "show", f"{PINS[project]}:{path}"], capture_output=True)
    if done.returncode != 0:
        sys.exit(f"git show failed for {project} {path}: {done.stderr.decode(errors='replace')}")
    return done.stdout.decode("utf-8-sig", errors="replace")


def files(root, project, folder):
    done = subprocess.run(["git", "-C", f"{root}/{project}", "ls-tree", "--name-only", f"{PINS[project]}:{folder}"], capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(done.stderr)
    return done.stdout.split()


def ckb_next(root):
    header = show(root, "ckb-next", "src/daemon/usb.h")
    source = show(root, "ckb-next", "src/daemon/usb.c")
    ids = {name: int(value, 16) for name, value in re.findall(r"#define\s+(P_\w+)\s+0x([0-9a-fA-F]{4})", header)}
    rows = set()
    for name in re.findall(r"\{\s*V_CORSAIR,\s*(P_\w+)\s*,", source):
        if name not in ids:
            sys.exit(f"ckb-next: no ID for {name}")
        rows.add((0x1B1C, ids[name], f"ckb-next {name}"))
    return rows


def libratbag(root):
    rows = set()
    for name in files(root, "libratbag", "data/devices"):
        if not name.endswith(".device") or name == "device.example":
            continue
        text = show(root, "libratbag", f"data/devices/{name}")
        driver = re.search(r"^Driver=(\S+)", text, re.M)
        match = re.search(r"^DeviceMatch=(.*)$", text, re.M)
        if not driver or not match:
            continue
        # A device file says how many LEDs it has, or its driver lists them by firmware.
        leds = re.search(r"^Leds=(\d+)", text, re.M)
        led_types = re.findall(r"^LedType=(\w+)", text, re.M)
        has = (leds and int(leds.group(1)) > 0) or any(t != "None" for t in led_types) or driver.group(1) in ("sinowealth_nubwo", "marsgaming", "roccat-kone-emp")
        if driver.group(1) == "test" or not has:
            continue
        for part in match.group(1).split(";"):
            found = re.match(r"(?:usb|bluetooth):([0-9a-fA-F]{4}):([0-9a-fA-F]{4})", part)
            if found:
                rows.add((int(found.group(1), 16), int(found.group(2), 16), f"libratbag {name}"))
    return rows


def g_helper(root):
    rows = set()
    aura = show(root, "g-helper", "app/USB/AsusHid.cs")
    for name in ("MAIN_AURA_PIDS", "REAR_LIGHT_PIDS"):
        block = re.search(name + r"\s*=\s*\{([^}]*)\}", aura).group(1)
        for value in re.findall(r"0x([0-9a-fA-F]+)", block):
            rows.add((0x0B05, int(value, 16), f"g-helper {name}"))
    for kind, default_rgb in (("Mouse", "false"), ("Keyboard", "true"), ("Headset", "true")):
        folder = f"app/Peripherals/{kind}/Models"
        classes = {}
        for name in files(root, "g-helper", folder):
            text = show(root, "g-helper", f"{folder}/{name}")
            for part in re.split(r"(?=\bpublic class )", text)[1:]:
                head = re.match(r"public class (\w+)\s*:\s*(\w+)", part)
                if not head:
                    continue
                ctor = re.search(r"public " + head.group(1) + r"\(\)\s*:\s*base\(([^)]*)\)", part)
                ids = [p for p in re.findall(r"0x([0-9A-Fa-f]{4})", ctor.group(1) if ctor else "") if p.upper() != "0B05"]
                rgb = re.search(r"HasRGB\(\)\s*\{\s*return (\w+)", part)
                classes[head.group(1)] = (head.group(2), ids[0] if ids else None, rgb.group(1) if rgb else None)

        def lit(name):
            while name in classes:
                if classes[name][2]:
                    return classes[name][2] == "true"
                name = classes[name][0]
            return default_rgb == "true"

        for name, (_, product, _) in classes.items():
            if product and lit(name):
                rows.add((0x0B05, int(product, 16), f"g-helper {name}"))
    return rows


def asusctl(root):
    rows = set()
    support = show(root, "asusctl", "rog-aura/data/aura_support.ron")
    for product in re.findall(r'product_id: "([0-9a-fA-F]{4})"', support):
        rows.add((0x0B05, int(product, 16), "asusctl aura_support.ron"))
    types = show(root, "asusctl", "rog-aura/src/lib.rs")
    for line, kind in re.findall(r'^\s*("[^=]*)=> (?:Self|AuraDeviceType)::(\w+),', types, re.M):
        for product in re.findall(r'"([0-9a-fA-F]{4})"', line):
            rows.add((0x0B05, int(product, 16), f"asusctl {kind}"))
    return rows


def generate(root):
    rows = ckb_next(root) | libratbag(root) | g_helper(root) | asusctl(root)
    lines = [
        "// Generated by tools/gen_other_lighting_inventory.py. Do not edit.",
        "namespace PadForge.Tests",
        "{",
        "    /// <summary>Every product the other open lighting projects name, and where.</summary>",
        "    internal static class OtherProjectsInventory",
        "    {",
        "        internal static readonly (ushort Vendor, ushort Product, string Source)[] All =",
        "        {",
    ]
    for vendor, product, source in sorted(rows, key=lambda r: (r[2], r[0], r[1])):
        lines.append(f'            (0x{vendor:04X}, 0x{product:04X}, "{source}"),')
    lines += ["        };", "    }", "}", ""]
    return "\r\n".join(lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = generate(args.folder)
    if args.check:
        if not os.path.exists(OUT) or open(OUT, encoding="utf-8", newline="").read() != text:
            print("OtherProjectsInventory.g.cs differs")
            return 1
        print("the inventory matches")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print("wrote", os.path.normpath(OUT), len(text.splitlines()), "lines")
    return 0


if __name__ == "__main__":
    sys.exit(main())
