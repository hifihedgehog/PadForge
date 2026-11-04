"""Second sources for the keys of Razer boards OpenRGB lays out with no
layout (#557), for tools/gen_razer_light_table.py.

WHAT IT READS, each at a pinned commit with git show:
- uchroma's device files (uchroma/server/data/keyboard.yaml, laptop.yaml,
  keypad.yaml): a tree of device configurations, each child taking its
  parent's fields unless it sets its own, keyed by product ID, with the
  matrix size (dimensions, rows then columns) and a key_mapping from a Linux
  input key name to the cells, row and column, its light sits in.
- Polychromatic's device maps (data/devicemaps/maps.json and the SVG each
  names): a drawing per board whose key groups carry the id x<column>-y<row>
  and the keycap's label. Polychromatic chooses a map by its name, not by
  product ID, so a map is taken for a product only where its name is the
  product's name, and only a map drawn for the US layout or for no locale.

WHAT IT GIVES. For a product and the frame it is painted with: the keys at
each cell, HID usages on the Keyboard/Keypad page (HID Usage Tables 1.6,
chapter 10) or a label, the sources that name them, and why a source was
not used. A source is used only where its matrix is the frame's size and,
where OpenRGB lists the board, OpenRGB's keyboard zone's size, and only where
every cell it names lies in the frame. Two sources that both fit must name
the same key at every cell both name, or neither is used; a cell only one of
them names takes that one's key.
"""
import json
import re
import subprocess
import xml.etree.ElementTree as ET

import yaml

UCHROMA_PIN = "ab05c7f"
POLYCHROMATIC_PIN = "cd024b2"

# Linux input key names (include/uapi/linux/input-event-codes.h) to HID
# usages on the Keyboard/Keypad page (HID Usage Tables 1.6, chapter 10), the
# pairs the kernel's HID input layer maps between (drivers/hid/hid-input.c,
# hid_keyboard[]). KEY_MENU stands where uchroma puts the context menu key,
# which sends Keyboard Application (0x65).
LINUX_USAGE = {
    **{"KEY_%s" % chr(ord("A") + i): 0x04 + i for i in range(26)},
    **{"KEY_%d" % d: 0x1E + (d - 1) for d in range(1, 10)},
    "KEY_0": 0x27, "KEY_ENTER": 0x28, "KEY_ESC": 0x29, "KEY_BACKSPACE": 0x2A, "KEY_TAB": 0x2B,
    "KEY_SPACE": 0x2C, "KEY_MINUS": 0x2D, "KEY_EQUAL": 0x2E, "KEY_LEFTBRACE": 0x2F, "KEY_RIGHTBRACE": 0x30,
    "KEY_BACKSLASH": 0x31, "KEY_SEMICOLON": 0x33, "KEY_APOSTROPHE": 0x34, "KEY_GRAVE": 0x35,
    "KEY_COMMA": 0x36, "KEY_DOT": 0x37, "KEY_SLASH": 0x38, "KEY_CAPSLOCK": 0x39,
    **{"KEY_F%d" % n: 0x3A + (n - 1) for n in range(1, 13)},
    "KEY_SYSRQ": 0x46, "KEY_SCROLLLOCK": 0x47, "KEY_PAUSE": 0x48, "KEY_INSERT": 0x49, "KEY_HOME": 0x4A,
    "KEY_PAGEUP": 0x4B, "KEY_DELETE": 0x4C, "KEY_END": 0x4D, "KEY_PAGEDOWN": 0x4E, "KEY_RIGHT": 0x4F,
    "KEY_LEFT": 0x50, "KEY_DOWN": 0x51, "KEY_UP": 0x52, "KEY_MENU": 0x65,
    "KEY_LEFTCTRL": 0xE0, "KEY_LEFTSHIFT": 0xE1, "KEY_LEFTALT": 0xE2, "KEY_LEFTMETA": 0xE3,
    "KEY_RIGHTCTRL": 0xE4, "KEY_RIGHTSHIFT": 0xE5, "KEY_RIGHTALT": 0xE6, "KEY_RIGHTMETA": 0xE7,
}
# Keys on the Consumer page and Fn, which no Keyboard/Keypad usage names:
# LightLabel members. KEY_MIN_INTERESTING is KEY_MUTE's other name.
LINUX_LABEL = {
    "KEY_MUTE": "Mute", "KEY_MIN_INTERESTING": "Mute", "KEY_PLAYPAUSE": "PlayPause",
    "KEY_NEXTSONG": "Next", "KEY_PREVIOUSSONG": "Previous", "KEY_STOPCD": "Stop", "KEY_FN": "Fn",
}

# Polychromatic's US keycap labels.
POLY_USAGE = {
    **{chr(ord("A") + i): 0x04 + i for i in range(26)},
    **{str(d): 0x1E + (d - 1) for d in range(1, 10)},
    "0": 0x27, "enter": 0x28, "esc": 0x29, "tab": 0x2B, "-": 0x2D, "=": 0x2E, "[": 0x2F, "]": 0x30,
    "\\": 0x31, ";": 0x33, "'": 0x34, "`": 0x35, ",": 0x36, ".": 0x37, "/": 0x38, "caps": 0x39,
    **{"F%d" % n: 0x3A + (n - 1) for n in range(1, 13)},
    "del": 0x4C,
}
# A modifier's label names no side: the first of a row is the left one and
# the next the right one, the order every keyboard has them in.
POLY_SIDED = {"shift": (0xE1, 0xE5), "ctrl": (0xE0, 0xE4), "alt": (0xE2, 0xE6)}
POLY_LABEL = {"fn": "Fn"}


def _show(repo, pin, path):
    return subprocess.run(["git", "-C", repo, "show", "%s:%s" % (pin, path)], capture_output=True,
                          check=True).stdout


class _Loader(yaml.SafeLoader):
    pass


_Loader.add_constructor("!device-config", lambda loader, node: loader.construct_mapping(node, deep=True))


def _walk(cfg, parent, out, path):
    merged = dict(parent)
    merged.update({k: v for k, v in cfg.items() if k != "children"})
    own = "key_mapping" in cfg
    if "product_id" in cfg:
        out[cfg["product_id"]] = (merged, path, own)
    for child in cfg.get("children", []) or []:
        _walk(child, merged, out, path)


def uchroma_devices(repo):
    devices = {}
    for name in ("keyboard.yaml", "laptop.yaml", "keypad.yaml"):
        path = "uchroma/server/data/" + name
        _walk(yaml.load(_show(repo, UCHROMA_PIN, path).decode("utf-8"), Loader=_Loader), {}, devices, path)
    return devices


def uchroma_keys(entry):
    """{(row, col): (usage, label)} from a uchroma configuration."""
    cfg, _, _ = entry
    keys = {}
    for name, cells in cfg.get("key_mapping") or []:
        usage, label = LINUX_USAGE.get(name, 0), LINUX_LABEL.get(name)
        if not usage and not label:
            continue
        for row, col in cells:
            keys.setdefault((row, col), (usage, label))
    return keys


def polychromatic_maps(repo):
    return json.loads(_show(repo, POLYCHROMATIC_PIN, "data/devicemaps/maps.json"))


def polychromatic_keys(repo, filename):
    root = ET.fromstring(_show(repo, POLYCHROMATIC_PIN, "data/devicemaps/" + filename))
    cells = {}
    for group in root.iter():
        m = re.fullmatch(r"x(\d+)-y(\d+)", group.get("id") or "")
        if m:
            text = " ".join("".join(t.itertext()).strip() for t in group.iter() if t.tag.endswith("text")).strip()
            cells[(int(m.group(2)), int(m.group(1)))] = text
    keys, seen = {}, {}
    for (row, col) in sorted(cells):
        label = cells[(row, col)]
        if label in POLY_SIDED:
            side = seen.get((row, label), 0)
            seen[(row, label)] = side + 1
            keys[(row, col)] = (POLY_SIDED[label][min(side, 1)], None)
        elif label in POLY_USAGE:
            keys[(row, col)] = (POLY_USAGE[label], None)
        elif label in POLY_LABEL:
            keys[(row, col)] = (0, POLY_LABEL[label])
    return keys


def name_keys(pid, name, rows, cols, zone, uch_repo, poly_repo, uch_devices, poly_maps):
    """(keys, source) where the sources name the board's keys, or (None, reason)."""
    candidates, reasons = [], []
    entry = uch_devices.get(pid)
    if entry is None:
        reasons.append("uchroma lists no product %04X" % pid)
    else:
        cfg, path, own = entry
        dims = list(cfg.get("dimensions") or [])
        keys = uchroma_keys(entry)
        where = "uchroma %s %s, %s key_mapping" % (path, UCHROMA_PIN, "its own" if own else "its parent's")
        if dims != [rows, cols]:
            reasons.append("%s is %s, the frame is %dx%d" % (where, dims, rows, cols))
        elif zone and tuple(zone) != (rows, cols):
            reasons.append("%s fits the frame, OpenRGB's zone is %dx%d" % (where, zone[0], zone[1]))
        elif any(r >= rows or c >= cols for r, c in keys):
            reasons.append("%s names cells outside its own %dx%d" % (where, rows, cols))
        elif keys:
            candidates.append((where, keys))
    found = [(n, m) for n, m in poly_maps.items() if n == name and m.get("locale") in (None, "en_US")]
    if not found:
        reasons.append("Polychromatic has no US map named %r" % name)
    for map_name, spec in found:
        keys = polychromatic_keys(poly_repo, spec["filename"])
        where = "Polychromatic %s %s" % (spec["filename"], POLYCHROMATIC_PIN)
        if (spec["rows"], spec["cols"]) != (rows, cols) or (zone and tuple(zone) != (rows, cols)):
            reasons.append("%s is %dx%d" % (where, spec["rows"], spec["cols"]))
        elif any(r >= rows or c >= cols for r, c in keys):
            reasons.append("%s names cells outside %dx%d" % (where, rows, cols))
        elif keys:
            candidates.append((where, keys))
    if not candidates:
        return None, "; ".join(reasons)
    if len(candidates) == 1:
        return candidates[0][1], "%s, one source (%s)" % (candidates[0][0], "; ".join(reasons))
    (wa, ka), (wb, kb) = candidates[:2]
    for cell in set(ka) & set(kb):
        if ka[cell] != kb[cell]:
            return None, "%s and %s disagree at row %d column %d" % (wa, wb, cell[0], cell[1])
    merged = dict(ka)
    merged.update(kb)
    return merged, "%s and %s, which agree" % (wa, wb)
