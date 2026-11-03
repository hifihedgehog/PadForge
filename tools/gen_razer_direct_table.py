"""The Razer direct-lighting product table, from the research table of #494.

WHAT IT READS. razer-direct-table.json, one entry for each of the 235 Razer
product IDs PadForge lists (PeripheralProductIds.RazerMice and
RazerKeyboards). The research extraction built it from the openrazer drivers
and from OpenRGB's RazerController, #557 added the Basilisk V4 Pro and the
Mouse Dock Pro from OpenRGB c6fc8016 (each entry's cites name its revision),
and it spells every report a product is sent as text: "0F/02 [00 04 01 00 00 01 R G B]". It is kept at
tools/data/razer-direct-table.json, which is read unless --json names
another.

WHAT IT WRITES. PadForge.App/Common/Input/Peripherals/RazerDirectTable.cs, one
row per product ID: the family that paints it, its transaction ID and the
other IDs to try, its LEDs or its frame size, the read that opens a claim and
the ID that read goes out under first where openrazer sends it under one that
is none of the product's own, the wait before a reply, and the collection to
open on Windows.

A ROW'S NAME is the openrazer daemon's text for the product. Where that text
does not start with "Razer ", as with a class's docstring, OpenRGB's name for
the product is written.

WHY THE TABLE HOLDS NO REPORT. A row says what a product is. The reports are
built from it by RazerDirectProtocol, so one builder serves every product of a
family and a test checks it byte for byte. To keep the two from drifting
apart, this script builds each row's reports again from the row it is about to
write, by the same rules the C# builders follow, and refuses to write the file
unless they equal the reports the research table spells. It then prints one
SHA-256 per family over those reports. RazerDirectProtocolTests holds the same
six digests and computes them from the C# builders over every row, so a
builder and the table cannot disagree on any product without a red test.

WHAT IS LEFT OUT. The alternates the research table marks as other forms of a
product's own family (zero_led, per_led) are not fallbacks and are not
written: nothing sends them. A product's per-zone frame path is not written
either: tools/gen_razer_light_table.py writes the light by light paint.

Usage:
    python tools/gen_razer_direct_table.py
    python tools/gen_razer_direct_table.py --check    (compare, write nothing)
    python tools/gen_razer_direct_table.py --frames out.txt
    python tools/gen_razer_direct_table.py --json path/to/another.json
"""
import argparse
import hashlib
import json
import os
import re
import sys

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "Common", "Input", "Peripherals", "RazerDirectTable.cs")
RESEARCH = os.path.join(PROJ_ROOT, "tools", "data", "razer-direct-table.json")

# The color the digests are computed with. Every byte differs from the others
# and from zero, so a swapped or dropped channel changes the digest.
TEST_COLOR = (0x12, 0x34, 0x56)

FAMILY = {
    "none": "None",
    "legacy": "Legacy",
    "mono": "Mono",
    "classic": "Classic",
    "ext": "Extended",
    "ext_frame1": "ExtendedFrame",
    "std": "Standard",
    "mext": "MouseExtended",
    "ext_matrix": "ExtendedMatrix",
}
COLOR_FAMILIES = ("classic", "ext", "ext_frame1", "std", "mext", "ext_matrix")

# RazerRowFlags, in the order the enum declares them.
FLAGS = ("LineRows", "HeldEffect", "EffectFirst", "NoState", "Laptop", "SpectrumBack")

# The Windows collections the research table names, each under the name the
# generated file gives it.
COLLECTIONS = {
    ((0, 0x01, 0x02),): "Interface0Mouse",
    ((2, 0x01, 0x02),): "Interface2Mouse",
    ((3, 0x0C, 0x01),): "Interface3Consumer",
    ((3, 0x01, 0x00),): "Interface3Generic",
    ((3, 0x01, 0x00), (3, 0x0C, 0x01)): "Interface3GenericThenConsumer",
}

# Alternates that are tried when the first paint is refused, in the order the
# research table lists them. Every other kind is another form of the same
# family and is never tried.
FALLBACK_KINDS = ("family", "frame", "effect")


class TableError(Exception):
    pass


def need(condition, pid, message):
    if not condition:
        raise TableError("%s: %s" % (pid, message))


def hx(value):
    return "%02X" % value


# ----------------------------------------------------------------------------
# The research table's report text
# ----------------------------------------------------------------------------

FRAME = re.compile(r"^([0-9A-F]{2})/([0-9A-F]{2}) \[(.*)\](?: size ([0-9A-F]{2}))?$")


def parse_frame(text, color=TEST_COLOR):
    """A report as the research table spells it: (class, command, size, arguments)."""
    match = FRAME.match(text.strip())
    if not match:
        raise TableError("cannot read the report %r" % text)
    args = []
    body = match.group(3)
    body = re.sub(r"\(R G B\)x(\d+)", lambda m: " ".join(["R G B"] * int(m.group(1))), body)
    for token in body.split():
        if token == "R":
            args.append(color[0])
        elif token == "G":
            args.append(color[1])
        elif token == "B":
            args.append(color[2])
        elif re.match(r"^[0-9A-F]{2}$", token):
            args.append(int(token, 16))
        else:
            raise TableError("cannot read %r in the report %r" % (token, text))
    size = int(match.group(4), 16) if match.group(4) else len(args)
    if len(args) > 80 or size > 80:
        raise TableError("the report %r is longer than 80 arguments" % text)
    if len(args) > size and args[-1] == 0:
        raise TableError("the report %r ends in a zero past its size" % text)
    return int(match.group(1), 16), int(match.group(2), 16), size, args


def canonical(tx, frame):
    """One report as a line of text. The C# test renders the same line from
    the 91 bytes a builder made: the arguments run to the data size, or to the
    last one that is not zero where a report carries more than its size says."""
    cls, cmd, size, args = frame
    shown = list(args) + [0] * max(0, size - len(args))
    return "%s %s/%s size %s [%s]" % (hx(tx), hx(cls), hx(cmd), hx(size), " ".join(hx(a) for a in shown))


# ----------------------------------------------------------------------------
# The reports a row builds, by the rules of RazerDirectProtocol
# ----------------------------------------------------------------------------

def rgb_run(count):
    return list(TEST_COLOR) * count


def build_probe(row):
    kind, led = row["probe"], row["probe_led"]
    if kind == "ExtendedBrightness":
        return (0x0F, 0x84, 3, [0x01, led, 0x00])
    if kind == "StandardBrightness":
        return (0x03, 0x83, 3, [0x01, led, 0x00])
    if kind == "LaptopBrightness":
        return (0x0E, 0x84, 2, [0x01, 0x00])
    if kind == "ClassicState":
        return (0x03, 0x80, 3, [0x01, led, 0x00])
    if kind == "RegionList":
        return (0x0F, 0x80, 0x50, [])
    if kind == "PairedDevices":
        return (0x00, 0xBF, 0x50, [])
    raise TableError("%s: no claim read" % row["pid"])


def build_reads(row):
    family = row["family"]
    frames = []
    if family in ("ext", "ext_frame1", "ext_matrix"):
        for led in row["leds"]:
            frames.append((0x0F, 0x82, 0x0C, [0x01, led]))
    elif family == "classic":
        for led in row["leds"]:
            frames.append((0x03, 0x80, 3, [0x01, led, 0x00]))
            frames.append((0x03, 0x81, 5, [0x01, led, 0x00, 0x00, 0x00]))
            frames.append((0x03, 0x82, 3, [0x01, led, 0x00]))
    return frames


def build_claim(row):
    family = row["family"]
    if family == "ext_frame1":
        return [(0x0F, 0x02, 6, [0x00, 0x00, 0x08, 0x00, 0x00, 0x00])]
    if family == "classic" and "NoState" not in row["flags"]:
        return [(0x03, 0x00, 3, [0x00, led, 0x01]) for led in row["leds"]]
    return []


def matrix_row(index, columns):
    return (0x03, 0x0B, 4 + 3 * columns, [0xFF, index, 0x00, columns - 1] + rgb_run(columns))


def line_row(columns):
    return (0x03, 0x0C, 0x32, [0x00, columns - 1] + rgb_run(columns))


SHOW = (0x03, 0x0A, 2, [0x05, 0x00])
CUSTOM = (0x0F, 0x02, 0x0C, [0x00, 0x00, 0x08])


def extended_row(index, columns):
    return (0x0F, 0x03, 5 + 3 * columns, [0x00, 0x00, index, 0x00, columns - 1] + rgb_run(columns))
STANDARD_STATIC = (0x03, 0x0A, 4, [0x06] + list(TEST_COLOR))


def build_paint(row):
    """The paint reports, each with the transaction ID it goes out under when
    the row's first ID was the one that answered."""
    family, tx = row["family"], row["tx"]
    if family == "ext":
        return [(tx, (0x0F, 0x02, 9, [0x00, led, 0x01, 0x00, 0x00, 0x01] + list(TEST_COLOR))) for led in row["leds"]]
    if family == "ext_frame1":
        count = row["columns"]
        return [(tx, (0x0F, 0x03, 5 + 3 * count, [0x00, 0x00, 0x00, 0x00, count - 1] + rgb_run(count)))]
    if family == "mext":
        return [(tx, (0x03, 0x0D, 6, [0x00, led, 0x06] + list(TEST_COLOR))) for led in row["leds"]]
    if family == "ext_matrix":
        return [(tx, extended_row(index, row["columns"])) for index in range(row["rows"])] + [(tx, CUSTOM)]
    if family == "classic":
        frames = []
        for led in row["leds"]:
            color = (0x03, 0x01, 5, [0x00, led] + list(TEST_COLOR))
            effect = (0x03, 0x02, 3, [0x00, led, 0x00])
            frames += [effect, color] if "EffectFirst" in row["flags"] else [color, effect]
        return [(tx, frame) for frame in frames]
    if family == "std":
        if "HeldEffect" in row["flags"]:
            return [(tx, STANDARD_STATIC)]
        row_tx = row["row_tx"] or tx
        show_tx = row["show_tx"] or tx
        if "LineRows" in row["flags"]:
            return [(row_tx, line_row(row["columns"])), (show_tx, SHOW)]
        return [(row_tx, matrix_row(index, row["columns"])) for index in range(row["rows"])] + [(show_tx, SHOW)]
    raise TableError("%s: no paint" % row["pid"])


def build_back(row):
    family = row["family"]
    if family in ("ext", "ext_frame1", "ext_matrix"):
        return [(0x0F, 0x02, 6, [0x00, led, 0x03, 0x00, 0x00, 0x00]) for led in row["spectrum"]]
    if family == "mext":
        return [(0x03, 0x0D, 3, [0x00, led, 0x04]) for led in row["spectrum"]]
    if family == "classic":
        return [(0x03, 0x02, 3, [0x00, led, 0x04]) for led in row["spectrum"]]
    if family == "std" and "SpectrumBack" in row["flags"]:
        return [(0x03, 0x0A, 1, [0x04])]
    return []


def build_fallback(fallback):
    kind, columns = fallback
    if kind == "MatrixRow":
        return [matrix_row(0, columns), SHOW]
    if kind == "LineRow":
        return [line_row(columns), SHOW]
    if kind == "Effect":
        return [STANDARD_STATIC]
    raise TableError("no fallback of kind %s" % kind)


def listing(row):
    """Every report of one row, one to a line, in the order the digest reads them."""
    tx = row["tx"]
    lines = ["%s tx %s%s" % (row["pid"], hx(tx), "".join(" " + hx(alt) for alt in row["alternates"]))]
    lines.append("  probe " + canonical(row["probe_tx"] or tx, build_probe(row)))
    lines += ["  read " + canonical(tx, frame) for frame in build_reads(row)]
    lines += ["  claim " + canonical(tx, frame) for frame in build_claim(row)]
    lines += ["  paint " + canonical(frame_tx, frame) for frame_tx, frame in build_paint(row)]
    lines += ["  back " + canonical(tx, frame) for frame in build_back(row)]
    for index, fallback in enumerate(row["fallbacks"], 1):
        lines += ["  fallback %d %s" % (index, canonical(tx, frame)) for frame in build_fallback(fallback)]
    return lines


# ----------------------------------------------------------------------------
# One research entry to one row
# ----------------------------------------------------------------------------

def led_of(pattern, text, pid):
    match = re.match(pattern, text)
    need(match, pid, "unexpected report %r" % text)
    return int(match.group(1), 16)


def to_row(pid, device):
    plan = device["plan"]
    method = plan["method"]
    need(method in FAMILY, pid, "unknown method %r" % method)
    name = device["name"]
    if not name.startswith("Razer ") and device.get("name_openrgb"):
        name = device["name_openrgb"]
    need(all(32 <= ord(c) < 127 for c in name) and '"' not in name and "\\" not in name, pid, "name is not plain ASCII")
    candidates = tuple((c["interface"], c["usage_page"], c["usage"]) for c in device["windows_collection"]["candidates"])
    need(candidates in COLLECTIONS, pid, "no name for the collection rule %r" % (candidates,))

    row = {
        "pid": pid,
        "name": name,
        "family": method,
        "tx": plan["tx"] or 0,
        "alternates": [a["tx"] for a in plan.get("tx_alternates", [])],
        "leds": list(plan["leds"]),
        "spectrum": [],
        "rows": 0,
        "columns": 0,
        "row_tx": 0,
        "show_tx": 0,
        "probe": "None",
        "probe_led": 0,
        "probe_tx": 0,
        "flags": [],
        "wait_us": device["reply_wait_us"],
        "collections": COLLECTIONS[candidates],
        "fallbacks": [],
        "note": device["confidence"],
    }
    if device["windows_collection"]["source"].startswith("inferred"):
        row["note"] += ", collection inferred"
    if device.get("hardware_report"):
        row["note"] += ", run by a third party"
    need(bool(plan["color"]) == (method in COLOR_FAMILIES), pid, "color flag and method disagree")
    if method not in COLOR_FAMILIES:
        return row

    need(row["tx"] != 0, pid, "a color row needs a transaction ID")
    need(row["tx"] not in row["alternates"], pid, "the first ID is among its alternates")
    if plan.get("keepalive_ms"):
        need(plan["keepalive_ms"] == 2500, pid, "unexpected keep-alive %r" % plan["keepalive_ms"])
        row["flags"].append("Laptop")

    # The read that opens a claim.
    probe = plan["probe"]["frame"]
    if probe == "0E/84 [01 00]":
        row["probe"] = "LaptopBrightness"
    elif probe == "0F/80 [] size 50":
        row["probe"] = "RegionList"
    elif probe == "00/BF [] size 50":
        row["probe"] = "PairedDevices"
    else:
        match = re.match(r"^(0F/84|03/83|03/80) \[01 ([0-9A-F]{2}) 00\]$", probe)
        need(match, pid, "unexpected claim read %r" % probe)
        row["probe"] = {"0F/84": "ExtendedBrightness", "03/83": "StandardBrightness", "03/80": "ClassicState"}[match.group(1)]
        row["probe_led"] = int(match.group(2), 16)
    # The ID openrazer sends that read under, where it is none of the IDs the
    # product is painted under. The read then goes out under it first.
    own = plan["probe"].get("openrazer_tx")
    if own and own != row["tx"] and own not in row["alternates"]:
        row["probe_tx"] = own

    # The fallback at hand-back.
    back = plan["handback_fallback"]
    if method in ("ext", "ext_frame1", "ext_matrix"):
        row["spectrum"] = [led_of(r"^0F/02 \[00 ([0-9A-F]{2}) 03 00 00 00\]$", f, pid) for f in back]
    elif method == "mext":
        row["spectrum"] = [led_of(r"^03/0D \[00 ([0-9A-F]{2}) 04\]$", f, pid) for f in back]
    elif method == "classic":
        row["spectrum"] = [led_of(r"^03/02 \[00 ([0-9A-F]{2}) 04\]$", f, pid) for f in back]
    elif back == ["03/0A [04]"]:
        row["flags"].append("SpectrumBack")
    else:
        need(back == [], pid, "unexpected hand-back %r" % back)

    # The paint.
    paint = plan["paint"]
    mode = plan["paint_mode"]
    if method == "ext_frame1":
        need(mode == "frame" and len(paint) == 1, pid, "unexpected one-row frame")
        match = re.match(r"^0F/03 \[00 00 00 00 ([0-9A-F]{2})((?: R G B)+)\]$", paint[0])
        need(match, pid, "unexpected one-row frame %r" % paint[0])
        row["columns"] = int(match.group(1), 16) + 1
        need(match.group(2).count("R G B") == row["columns"], pid, "the stop column and the colors disagree")
    elif method == "std":
        if mode == "frame":
            dims = plan["frame_dims"]
            row["rows"], row["columns"] = dims["rows"], dims["cols"]
            if paint[0].startswith("03/0C"):
                row["flags"].append("LineRows")
                need(row["rows"] == 1, pid, "a line of LEDs has one row")
            sent = plan.get("paint_tx")
            if sent:
                row["row_tx"] = sent["rows"] if sent["rows"] != row["tx"] else 0
                row["show_tx"] = sent["effect"] if sent["effect"] != row["tx"] else 0
        else:
            need(mode == "effect, after the color holds 1000 ms", pid, "unexpected paint mode %r" % mode)
            row["flags"].append("HeldEffect")
    elif method == "ext_matrix":
        need(mode == "frame", pid, "unexpected paint mode %r" % mode)
        dims = plan["frame_dims"]
        row["rows"], row["columns"] = dims["rows"], dims["cols"]
    elif method == "classic":
        need(mode == "led", pid, "unexpected paint mode %r" % mode)
        if not plan["claim"]:
            row["flags"].append("NoState")
        if paint[0].startswith("03/02"):
            row["flags"].append("EffectFirst")
    else:
        need(mode == "effect", pid, "unexpected paint mode %r" % mode)

    # What to try when the first paint is refused.
    for alternate in plan["alternates"]:
        if alternate["kind"] not in FALLBACK_KINDS:
            need(alternate["kind"] in ("zero_led", "per_led"), pid, "unknown alternate kind %r" % alternate["kind"])
            continue
        need(alternate["claim"] == [], pid, "a fallback with claim reports")
        need(alternate["tx"] in ([], [row["tx"]]), pid, "a fallback under another transaction ID")
        frames = alternate["paint"]
        if frames == ["03/0A [06 R G B]"]:
            row["fallbacks"].append(("Effect", 0))
            continue
        need(len(frames) == 2 and frames[1] == "03/0A [05 00]", pid, "unexpected fallback %r" % frames)
        match = re.match(r"^03/0B \[FF 00 00 ([0-9A-F]{2}) \(R G B\)x(\d+)\]$", frames[0])
        if match:
            row["fallbacks"].append(("MatrixRow", int(match.group(2))))
        else:
            match = re.match(r"^03/0C \[00 ([0-9A-F]{2}) \(R G B\)x(\d+)\] size 32$", frames[0])
            need(match, pid, "unexpected fallback row %r" % frames[0])
            row["fallbacks"].append(("LineRow", int(match.group(2))))
        need(int(match.group(1), 16) + 1 == int(match.group(2)), pid, "a fallback row's stop column and colors disagree")

    need(set(row["flags"]) <= set(FLAGS), pid, "a flag the enum does not have")
    verify(row, plan)
    return row


def verify(row, plan):
    """The reports the row builds are the reports the research table spells."""
    pid = row["pid"]

    def same(what, built, spelled):
        built = [canonical(0, frame) for frame in built]
        spelled = [canonical(0, parse_frame(text)) for text in spelled]
        need(built == spelled, pid, "%s differs from the research table:\n  built   %s\n  spelled %s" % (what, built, spelled))

    same("the claim read", [build_probe(row)], [plan["probe"]["frame"]])
    reads = plan.get("handback_read") or ""
    same("the reads before a claim", build_reads(row), [r for r in reads.split(" | ") if r])
    same("the claim", build_claim(row), plan["claim"])
    same("the paint", [frame for _, frame in build_paint(row)], plan["paint"])
    same("the hand-back", build_back(row), plan["handback_fallback"])
    fallbacks = [a for a in plan["alternates"] if a["kind"] in FALLBACK_KINDS]
    need(len(fallbacks) == len(row["fallbacks"]), pid, "a fallback was lost")
    for built, spelled in zip(row["fallbacks"], fallbacks):
        same("a fallback", build_fallback(built), spelled["paint"])
    sent = plan.get("paint_tx")
    if sent:
        ids = [frame_tx for frame_tx, _ in build_paint(row)]
        need(set(ids[:-1]) == {sent["rows"]} and ids[-1] == sent["effect"], pid, "the frame's transaction IDs differ")


# ----------------------------------------------------------------------------
# The C# file
# ----------------------------------------------------------------------------

def ids(values):
    return "Ids(%s)" % ", ".join("0x%02X" % v for v in values)


def emit_row(row):
    parts = [
        "ProductId = 0x%s" % row["pid"],
        'Name = "%s"' % row["name"],
        "Family = RazerFamily.%s" % FAMILY[row["family"]],
    ]
    if row["tx"]:
        parts.append("TransactionId = 0x%02X" % row["tx"])
    if row["alternates"]:
        parts.append("OtherTransactionIds = %s" % ids(row["alternates"]))
    if row["leds"]:
        parts.append("Leds = %s" % ids(row["leds"]))
    if row["spectrum"]:
        parts.append("SpectrumLeds = %s" % ids(row["spectrum"]))
    if row["rows"]:
        parts.append("Rows = %d" % row["rows"])
    if row["columns"]:
        parts.append("Columns = %d" % row["columns"])
    if row["row_tx"]:
        parts.append("RowTransactionId = 0x%02X" % row["row_tx"])
    if row["show_tx"]:
        parts.append("ShowTransactionId = 0x%02X" % row["show_tx"])
    if row["probe"] != "None":
        parts.append("Probe = RazerProbe.%s" % row["probe"])
    if row["probe_led"]:
        parts.append("ProbeLed = 0x%02X" % row["probe_led"])
    if row["probe_tx"]:
        parts.append("ProbeTransactionId = 0x%02X" % row["probe_tx"])
    if row["flags"]:
        parts.append("Flags = %s" % " | ".join("RazerRowFlags." + flag for flag in FLAGS if flag in row["flags"]))
    if row["fallbacks"]:
        parts.append("Fallbacks = new[] { %s }" % ", ".join(
            "new RazerFallback(RazerFallbackKind.%s, %d)" % fallback for fallback in row["fallbacks"]))
    parts.append("ReplyWaitUs = %d" % row["wait_us"])
    parts.append("Collections = %s" % row["collections"])
    return "            new() { %s }, // %s" % (", ".join(parts), row["note"])


def emit(rows, meta):
    counts = {}
    for row in rows:
        counts[row["family"]] = counts.get(row["family"], 0) + 1
    colored = sum(counts.get(family, 0) for family in COLOR_FAMILIES)
    sources = meta["sources"]
    out = []
    add = out.append
    add("// AUTO-GENERATED by tools/gen_razer_direct_table.py. Do not edit by hand.")
    add("//")
    add("// One row for each Razer product ID in PeripheralProductIds, from the research")
    add("// table of #494 (razer-direct-table.json), which was read out of openrazer %s" % sources["openrazer"])
    add("// and OpenRGB %s, with the rows of #557 read out of OpenRGB %s. %d rows, %d of them" % (
        sources["openrgb"], sources["openrgb_557"], len(rows), colored))
    add("// with a color path: %d extended matrix, %d standard matrix, %d classic LED, %d" % (
        counts.get("ext", 0), counts.get("std", 0), counts.get("classic", 0), counts.get("ext_frame1", 0)))
    add("// extended one-row frame, %d mouse extended matrix, %d extended matrix frame. The" % (
        counts.get("mext", 0), counts.get("ext_matrix", 0)))
    add("// comment on a row says how far the two projects agree on it.")
    add("")
    add("using System.Collections.Generic;")
    add("")
    add("namespace PadForge.Common.Input.Peripherals")
    add("{")
    add("    internal static class RazerDirectTable")
    add("    {")
    add("        private static byte[] Ids(params byte[] values) => values;")
    add("")
    for candidates, name in COLLECTIONS.items():
        add("        private static readonly RazerCollection[] %s = { %s };" % (
            name, ", ".join("new(%d, 0x%02X, 0x%02X)" % c for c in candidates)))
    add("")
    add("        internal static readonly RazerDirectRow[] Rows =")
    add("        {")
    for row in rows:
        add(emit_row(row))
    add("        };")
    add("")
    add("        private static readonly Dictionary<ushort, RazerDirectRow> s_byProductId = Index();")
    add("")
    add("        private static Dictionary<ushort, RazerDirectRow> Index()")
    add("        {")
    add("            var index = new Dictionary<ushort, RazerDirectRow>(Rows.Length);")
    add("            foreach (var row in Rows) index[row.ProductId] = row;")
    add("            return index;")
    add("        }")
    add("")
    add("        internal static RazerDirectRow Find(ushort productId)")
    add("            => s_byProductId.TryGetValue(productId, out var row) ? row : null;")
    add("    }")
    add("}")
    return "\r\n".join(out) + "\r\n"


def digests(rows):
    result = {}
    for family in COLOR_FAMILIES:
        text = "".join(line + "\n" for row in rows if row["family"] == family for line in listing(row))
        result[FAMILY[family]] = hashlib.sha256(text.encode("ascii")).hexdigest()
    return result


def main():
    parser = argparse.ArgumentParser(description="Writes RazerDirectTable.cs from the research table.")
    parser.add_argument("--json", default=RESEARCH, help="the research table, tools/data/razer-direct-table.json by default")
    parser.add_argument("--out", default=OUTPUT, help="the C# file to write")
    parser.add_argument("--check", action="store_true", help="compare with the file on disk and write nothing")
    parser.add_argument("--frames", help="write every row's reports to this file, as the digests read them")
    args = parser.parse_args()

    with open(args.json, encoding="utf-8") as source:
        table = json.load(source)
    try:
        rows = [to_row(pid, table["devices"][pid]) for pid in sorted(table["devices"])]
    except TableError as error:
        print("razer-direct-table.json: %s" % error, file=sys.stderr)
        return 1
    text = emit(rows, table["meta"])

    colored = [row for row in rows if row["family"] in COLOR_FAMILIES]
    print("%d rows, %d with a color path" % (len(rows), len(colored)))
    for family, digest in digests(rows).items():
        count = sum(1 for row in colored if FAMILY[row["family"]] == family)
        print("  %-14s %3d  %s" % (family, count, digest))

    if args.frames:
        with open(args.frames, "w", encoding="ascii", newline="\n") as target:
            for row in colored:
                target.write("\n".join(listing(row)) + "\n")

    if args.check:
        with open(args.out, "rb") as current:
            same = current.read() == text.encode("ascii")
        print("%s is %s" % (os.path.relpath(args.out, PROJ_ROOT), "up to date" if same else "OUT OF DATE"))
        return 0 if same else 1
    with open(args.out, "wb") as target:
        target.write(text.encode("ascii"))
    print("wrote %s" % os.path.relpath(args.out, PROJ_ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
