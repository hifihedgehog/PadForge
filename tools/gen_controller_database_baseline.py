"""Writes the list of what this release carries of the community controller
database, and counts what the community has changed since (#597).

WHEN TO RUN IT
- At each SDL take, after the new SDL3.dll files are in the repo: run the write
  mode against the fork commit the files were built from and commit the result
  with the take. ControllerDatabaseBaselineTests then holds the list to both
  SDL3.dll files, so a take that skips this step fails there.
- At each release prep: run `git fetch` in the community clone, then the
  --since-pin mode. It prints how many Windows rows the community added,
  changed and removed since the fork's pin, and how many rows and bytes a
  download of the community's current copy puts in PadForge.xml. When the
  first count is above zero, the pin moves in the fork (its
  build-scripts/gen_community_mappings.c says how), and the take that follows
  runs the write mode again.

WHY THE LIST EXISTS
SDL3.dll carries a pinned copy of the community database (SDL_GameControllerDB)
less the rows SDL reads some other way. Settings, Updates, Controller Database
downloads the current copy and keeps, inside PadForge.xml, only the rows that
are new or changed against what the release carries. PadForge cannot ask
SDL3.dll which rows those are, so this list says it: one line per controller
ID of the pinned copy's Windows rows, with a hash of the row and a verdict.

  K  the fork carries the row (src/joystick/SDL_gamepad_db_community.h)
  S  SDL maps the controller in the Windows section of SDL_gamepad_db.h
  O  SDL maps the controller on other platforms only
  N  the fork leaves the row out for another reason: a HIDAPI driver reads the
     controller, controller_list.h lists it, SDL types it as a wheel or a
     flight stick, it is vJoy's shared ID, or its ID is not in the USB form

An S line with no hash names a controller ID of SDL's own Windows section that
the pinned copy has no row for.

WHAT IT READS, all with git show, so no file of either clone is opened for
writing and no git command that writes is run:
- SDL_FORK at FORK_COMMIT (default HEAD):
    build-scripts/gamecontrollerdb/gamecontrollerdb.txt   the pinned copy
    build-scripts/gamecontrollerdb/source.txt             its repository and commit
    src/joystick/SDL_gamepad_db.h                         SDL's own rows
    src/joystick/SDL_gamepad_db_community.h               the rows the fork kept
- COMMUNITY, a clone of https://github.com/mdqinc/SDL_GameControllerDB: the
  pin commit's time, and with --since-pin the file at REF.

The counts are checked against the ones the fork's generator wrote into the
community header. A mismatch stops the run.

WHAT IT WRITES: PadForge.App/gamecontrollerdb_baseline.txt, with CRLF line
endings. The --check and --since-pin modes write nothing.

Usage:
    python tools/gen_controller_database_baseline.py SDL_FORK COMMUNITY
    python tools/gen_controller_database_baseline.py SDL_FORK COMMUNITY --check
    python tools/gen_controller_database_baseline.py SDL_FORK COMMUNITY --since-pin [REF]

  --fork-commit COMMIT  the fork commit the bundled SDL3.dll files carry
                        (default: the clone's HEAD)
  --pin-time TIME       the pin commit's time, as 2026-09-24T16:11:31Z, for a
                        run with no community clone at hand (pass "-" for
                        COMMUNITY)
  REF                   what --since-pin compares with (default: origin/master)

Exit status: 0 on success. --check exits 1 when the file differs from what the
fork gives. --since-pin exits 1 when any Windows row changed since the pin.
"""
import argparse
import datetime
import hashlib
import os
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")

PROJ_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(PROJ_ROOT, "PadForge.App", "gamecontrollerdb_baseline.txt")
OWN_ROWS = os.path.join(PROJ_ROOT, "PadForge.App", "gamecontrollerdb_padforge.txt")

PINNED = "build-scripts/gamecontrollerdb/gamecontrollerdb.txt"
SOURCE = "build-scripts/gamecontrollerdb/source.txt"
SDL_ROWS = "src/joystick/SDL_gamepad_db.h"
KEPT_ROWS = "src/joystick/SDL_gamepad_db_community.h"
COMMUNITY_FILE = "gamecontrollerdb.txt"

WINDOWS_FIELD = "platform:Windows"
WINDOWS_SECTION = "#ifdef SDL_JOYSTICK_DINPUT"
KEPT_INCLUDE = '#include "SDL_gamepad_db_community.h"'
HEX32 = re.compile(r"^[0-9a-fA-F]{32}$")
USB_FORM = re.compile(r"^03000000([0-9a-fA-F]{4})0000([0-9a-fA-F]{4})000000000000$")
HASH_DIGITS = 16

# What the fork's generator counted, as it wrote it into the community header.
HEADER_COUNTS = re.compile(
    r"Windows rows: (\d+)\. In the USB form with a vendor and product: (\d+)\.\s+"
    r"Left out because SDL_gamepad_db\.h maps the device on some platform: (\d+),\s+"
    r"because controller_list\.h lists it: (\d+), because a HIDAPI driver\s+"
    r"supports it: (\d+), because SDL types it as a flight stick or a wheel: (\d+),\s+"
    r"because it is vJoy's shared ID: (\d+)\. Kept: (\d+)\.")
HEADER_COMMIT = re.compile(r"^\s*Commit: ([0-9a-f]{40})\s*$", re.M)
HEADER_SHA256 = re.compile(r"^\s*gamecontrollerdb\.txt SHA-256: ([0-9a-f]{64})\s*$", re.M)


def git(clone, *args):
    """A git command that reads. It takes no optional lock, so it writes nothing."""
    result = subprocess.run(["git", "--no-optional-locks", "-C", clone, *args],
                            capture_output=True, stdin=subprocess.DEVNULL)
    if result.returncode != 0:
        sys.exit("git %s failed in %s: %s" % (" ".join(args), clone, result.stderr.decode("utf-8", "replace").strip()))
    return result.stdout


def show(clone, commit, path):
    return git(clone, "show", "%s:%s" % (commit, path))


def quoted_strings(text):
    """Every quoted string of a C source with the line it starts on, comments
    skipped, as the fork's generator reads SDL_gamepad_db.h."""
    found = []
    i, n, line = 0, len(text), 1
    while i < n:
        c = text[i]
        if c == "\n":
            line += 1
            i += 1
        elif c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
        elif c == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                if text[i] == "\n":
                    line += 1
                i += 1
            i += 2
        elif c == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == "\\" else 1
            i += 1
        elif c == '"':
            i += 1
            start = i
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
            found.append((line, text[start:i]))
            i += 1
        else:
            i += 1
    return found


def mapping_rows(text):
    """The quoted strings that open with a 32-digit controller ID and a comma."""
    return [(line, s) for line, s in quoted_strings(text) if len(s) > 32 and s[32] == "," and HEX32.match(s[:32])]


def vendor_product(guid):
    """What SDL_GetJoystickGUIDInfo reads from an ID in the standard form: a
    bus below 0x20, or the virtual bus, with zero in the words after the vendor
    and the product. (0, 0) for any other form."""
    data = bytes.fromhex(guid)
    words = [data[i] | (data[i + 1] << 8) for i in range(0, 16, 2)]
    if (words[0] < 0x20 or words[0] == 0xFF) and words[3] == 0 and words[5] == 0:
        return words[2], words[4]
    return 0, 0


def usb_form(guid):
    """The vendor and product of 03000000vvvv0000pppp000000000000, both
    nonzero, or None."""
    m = USB_FORM.match(guid)
    if not m:
        return None
    v, p = m.group(1), m.group(2)
    vendor = int(v[2:4] + v[0:2], 16)
    product = int(p[2:4] + p[0:2], 16)
    return (vendor, product) if vendor and product else None


def windows_rows(text):
    """The rows that carry the Windows platform field, as written."""
    rows = []
    for line in text.split("\n"):
        if not line or line[0] == "#":
            continue
        if WINDOWS_FIELD in line.split(","):
            rows.append(line)
    return rows


def row_hash(row):
    return hashlib.sha256(row.encode("utf-8")).hexdigest()[:HASH_DIGITS]


def sdl_windows_section(header):
    """The first and last line of SDL's Windows section. Nothing but the
    community include may stand between them as a preprocessor line."""
    lines = header.split("\n")
    starts = [n for n, l in enumerate(lines, 1) if l.strip() == WINDOWS_SECTION]
    if len(starts) != 1:
        sys.exit("%s has %d lines that read %s" % (SDL_ROWS, len(starts), WINDOWS_SECTION))
    start = starts[0]
    end = next((n for n, l in enumerate(lines, 1) if n > start and l.strip() == "#endif"), None)
    if end is None:
        sys.exit("the Windows section of %s never closes" % SDL_ROWS)
    for n in range(start + 1, end):
        text = lines[n - 1].strip()
        if text.startswith("#") and text != KEPT_INCLUDE:
            sys.exit("%s:%d is a preprocessor line inside the Windows section: %s" % (SDL_ROWS, n, text))
    return start, end


class Fork:
    """What one fork commit carries."""

    def __init__(self, clone, commit):
        self.commit = git(clone, "rev-parse", "--verify", commit + "^{commit}").decode("ascii").strip()
        pinned = show(clone, self.commit, PINNED)
        if b"\r" in pinned:
            sys.exit("%s has carriage returns at %s. It must be the community file byte for byte." % (PINNED, self.commit[:10]))
        self.pinned_sha256 = hashlib.sha256(pinned).hexdigest()
        self.pinned = pinned.decode("utf-8")
        source = show(clone, self.commit, SOURCE).decode("utf-8").split()
        if len(source) != 2 or not re.match(r"^[0-9a-f]{40}$", source[1]):
            sys.exit("%s must hold the repository URL and a 40-digit commit hash" % SOURCE)
        self.repository, self.pin = source
        sdl = show(clone, self.commit, SDL_ROWS).decode("utf-8")
        kept = show(clone, self.commit, KEPT_ROWS).decode("utf-8")

        m = HEADER_COMMIT.search(kept)
        if not m or m.group(1) != self.pin:
            sys.exit("%s names another commit than %s" % (KEPT_ROWS, SOURCE))
        m = HEADER_SHA256.search(kept)
        if not m or m.group(1) != self.pinned_sha256:
            sys.exit("%s names another SHA-256 than the pinned copy has" % KEPT_ROWS)
        m = HEADER_COUNTS.search(kept)
        if not m:
            sys.exit("the counts in %s are not in the form this script reads" % KEPT_ROWS)
        (self.h_windows, self.h_usb, self.h_mapped, self.h_listed, self.h_hidapi, self.h_shape,
         self.h_vjoy, self.h_kept) = (int(x) for x in m.groups())

        start, end = sdl_windows_section(sdl)
        self.mapped = set()
        self.windows_pairs = set()
        self.windows_guids = []
        self.sdl_windows_rows = 0
        for line, row in mapping_rows(sdl):
            guid = row[:32].lower()
            pair = vendor_product(guid)
            inside = start < line < end
            if pair[0] and pair[1]:
                self.mapped.add(pair)
                if inside:
                    self.windows_pairs.add(pair)
            if inside:
                self.sdl_windows_rows += 1
                if guid not in self.windows_guids:
                    self.windows_guids.append(guid)
        self.kept = {}
        for _, row in mapping_rows(kept):
            guid = row[:32].lower()
            if guid in self.kept:
                sys.exit("%s holds %s twice" % (KEPT_ROWS, guid))
            self.kept[guid] = row

    def verdict(self, guid, row):
        """K, S, O or N for one Windows row of the pinned copy."""
        if guid in self.kept:
            without_platform = ",".join(f for f in row.split(",") if f != WINDOWS_FIELD)
            if without_platform != self.kept[guid]:
                sys.exit("%s is not the pinned row for %s less its platform field" % (KEPT_ROWS, guid))
            return "K"
        pair = vendor_product(guid)
        known = pair[0] and pair[1]
        if guid in self.windows_guids or (known and pair in self.windows_pairs):
            return "S"
        if known and pair in self.mapped:
            return "O"
        return "N"

    def entries(self):
        """(guid, hash or None, verdict) for each controller ID of the pinned
        copy's Windows rows, then each ID of SDL's own Windows section that
        the pinned copy has no row for, sorted by ID. Also the count of
        Windows rows, with and without an ID."""
        rows = windows_rows(self.pinned)
        entries = {}
        usb = {"K": 0, "S": 0, "O": 0, "N": 0}
        usb_rows = 0
        for row in rows:
            guid = row.split(",")[0]
            if not HEX32.match(guid):
                continue  # the xinput row, which has no ID
            guid = guid.lower()
            if guid in entries:
                sys.exit("the pinned copy has two Windows rows for %s" % guid)
            verdict = self.verdict(guid, row)
            entries[guid] = (row_hash(row), verdict)
            if usb_form(guid):
                usb_rows += 1
                usb[verdict] += 1

        checks = (
            ("Windows rows", len(rows), self.h_windows),
            ("rows in the USB form", usb_rows, self.h_usb),
            ("rows the fork kept", usb["K"], self.h_kept),
            ("rows the fork kept, counted in its header", len(self.kept), self.h_kept),
            ("rows SDL maps on some platform", usb["S"] + usb["O"], self.h_mapped),
            ("rows left out for another reason", usb["N"], self.h_listed + self.h_hidapi + self.h_shape + self.h_vjoy),
        )
        for what, got, expected in checks:
            if got != expected:
                sys.exit("%s: this script counts %d and the fork's generator counted %d" % (what, got, expected))

        for guid in self.windows_guids:
            entries.setdefault(guid, (None, "S"))
        return rows, [(g, h, v) for g, (h, v) in sorted(entries.items())], usb


def pin_time(community, pin, given):
    """The pin commit's time in UTC, to the second."""
    if given:
        when = datetime.datetime.strptime(given, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
    elif community == "-":
        sys.exit("give the community clone, or --pin-time")
    else:
        text = git(community, "show", "-s", "--format=%cI", pin + "^{commit}").decode("ascii").strip()
        when = datetime.datetime.fromisoformat(text).astimezone(datetime.timezone.utc)
    return when.strftime("%Y-%m-%dT%H:%M:%SZ")


def baseline_text(fork, when, rows, entries):
    out = [
        "# What this release's SDL3.dll carries of the community controller database",
        "# (SDL_GameControllerDB), for Settings, Updates, Controller Database.",
        "# Written by tools/gen_controller_database_baseline.py at each SDL take. Do not edit.",
        "# A line is a verdict, a controller ID and a hash of the pinned row.",
        "# K: the fork carries the row. S: SDL maps it in its Windows section.",
        "# O: SDL maps it on other platforms only. N: left out for another reason.",
        "format 1",
        "fork %s" % fork.commit,
        "pin %s %s" % (fork.pin, when),
        "sha256 %s" % fork.pinned_sha256,
        "windows-rows %d" % len(rows),
        "sdl-windows-rows %d" % fork.sdl_windows_rows,
    ]
    out += ["%s %s %s" % (verdict, guid, digest or "-") for guid, digest, verdict in entries]
    return "\r\n".join(out) + "\r\n"


def own_rows():
    """PadForge's own rows, as (guid, row)."""
    try:
        with open(OWN_ROWS, encoding="utf-8-sig") as f:
            lines = [l.strip() for l in f.read().split("\n")]
    except FileNotFoundError:
        return []
    return [(l.split(",")[0].lower(), l) for l in lines if l and l[0] != "#"]


def lookup_id(guid):
    """The ID SDL's lookup reaches from a joystick's GUID: its name hash and
    its version cleared (ControllerDatabaseRow.LookupId)."""
    g = guid.lower()
    return g[:4] + "0000" + g[8:24] + "0000" + g[28:]


def what_is_stored(listed, own_ids, key, row):
    """What Update Now stores for one Windows row of the community's file
    (ControllerDatabaseSection.Classify): New, Changed, or None with why.
    The row grammar is the app's, and this does not repeat it."""
    if not usb_form(key):
        return None, "not kept: its ID is not in the USB form"
    if key in own_ids:
        return None, "not kept: PadForge's own file carries this controller"
    digest, verdict = listed.get(key, (None, None))
    if verdict is None:
        return "New", "kept as New"
    if verdict == "K":
        if digest == row_hash(row):
            return None, "not kept: the release carries this row"
        return "Changed", "kept as Changed"
    if verdict == "O":
        return "New", "kept as New"
    if verdict == "S":
        return None, "not kept: SDL maps it in its Windows section"
    return None, "not kept: the fork leaves it out"


def xml_text(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def report(fork, when, rows, entries, usb):
    by_verdict = {}
    for _, _, verdict in entries:
        by_verdict[verdict] = by_verdict.get(verdict, 0) + 1
    print("fork %s, pin %s of %s" % (fork.commit[:10], fork.pin[:10], when))
    print("pinned copy: %d Windows rows, %d in the USB form, SHA-256 %s" % (len(rows), sum(usb.values()), fork.pinned_sha256))
    print("  of the USB form: K %d, S %d, O %d, N %d" % (usb["K"], usb["S"], usb["O"], usb["N"]))
    print("SDL's own Windows section: %d rows, %d controller IDs" % (fork.sdl_windows_rows, len(fork.windows_guids)))
    print("lines: %d (K %d, S %d, O %d, N %d)" % (len(entries), by_verdict.get("K", 0), by_verdict.get("S", 0),
                                                 by_verdict.get("O", 0), by_verdict.get("N", 0)))
    listed = {guid: (digest, verdict) for guid, digest, verdict in entries}
    for guid, row in own_rows():
        digest, verdict = listed.get(guid, (None, None))
        name = row.split(",")[1] if "," in row else ""
        if verdict == "K":
            same = digest == row_hash(row)
            print("  PadForge's own row %s (%s): the fork carries %s. %s" % (
                guid, name, "the same row" if same else "another row for it",
                "Remove it from gamecontrollerdb_padforge.txt." if same else "PadForge's row replaces the fork's."))
        elif verdict == "S":
            print("  PadForge's own row %s (%s): SDL maps this ID in its Windows section, and PadForge's row replaces SDL's." % (guid, name))


def since_pin(fork, community, ref):
    """Counts the Windows rows the community added, changed and removed since
    the pin, and says what Update Now stores for each."""
    if community == "-":
        sys.exit("--since-pin needs the community clone")
    commit = git(community, "rev-parse", "--verify", ref + "^{commit}").decode("ascii").strip()
    when = git(community, "show", "-s", "--format=%cI", commit).decode("ascii").strip()
    current = show(community, commit, COMMUNITY_FILE).decode("utf-8").replace("\r\n", "\n")
    _, entries, _ = fork.entries()
    listed = {guid: (digest, verdict) for guid, digest, verdict in entries}

    def keyed(text):
        return {row.split(",")[0].lower(): row for row in windows_rows(text)}

    then, now = keyed(fork.pinned), keyed(current)
    added = sorted(k for k in now if k not in then)
    removed = sorted(k for k in then if k not in now)
    changed = sorted(k for k in now if k in then and now[k] != then[k])

    own_ids = {lookup_id(guid) for guid, _ in own_rows() if HEX32.match(guid)}

    def stored(key):
        return what_is_stored(listed, own_ids, key, now[key])[1]

    print("pin %s, compared with %s (%s of %s)" % (fork.pin[:10], ref, commit[:10], when))
    print("Windows rows: %d at the pin, %d now" % (len(then), len(now)))
    print("added %d, changed %d, removed %d" % (len(added), len(changed), len(removed)))
    for mark, keys, source in (("+", added, now), ("~", changed, now), ("-", removed, then)):
        for key in keys:
            name = source[key].split(",")[1] if "," in source[key] else ""
            print("  %s %s %s%s" % (mark, key, name, "" if mark == "-" else "  [" + stored(key) + "]"))

    # What a download of REF puts in PadForge.xml: one Row element per kept
    # row, as XmlSerializer writes it under the section, and the section's
    # own element with its attributes. Each line ends in CR LF, two bytes.
    kinds = {"New": 0, "Changed": 0}
    size = 0
    for key, row in now.items():
        kind, _ = what_is_stored(listed, own_ids, key, row)
        if kind:
            kinds[kind] += 1
            size += len(('    <Row Kind="%s">%s</Row>' % (kind, xml_text(row))).encode("utf-8")) + 2
    if kinds["New"] or kinds["Changed"]:
        size += len(('  <ControllerDatabase Source="https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/'
                     'gamecontrollerdb.txt" FetchedUtc="2026-11-23T14:17:35Z" ETag="W/&quot;%s&quot;" Sha256="%s" '
                     'WindowsRows="%d" BaselinePin="%s" AppVersion="6.0.1">'
                     % ("0" * 40, "0" * 64, len(now), fork.pin)).encode("utf-8")) + 2
        size += len("  </ControllerDatabase>") + 2
    print("a download of %s stores %d rows (%d New, %d Changed), about %d bytes of PadForge.xml" % (
        ref, kinds["New"] + kinds["Changed"], kinds["New"], kinds["Changed"], size))
    return 1 if added or changed or removed else 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("fork")
    parser.add_argument("community")
    parser.add_argument("--fork-commit", default="HEAD")
    parser.add_argument("--pin-time")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--since-pin", nargs="?", const="origin/master", metavar="REF")
    args = parser.parse_args()

    fork = Fork(args.fork, args.fork_commit)
    if args.since_pin:
        sys.exit(since_pin(fork, args.community, args.since_pin))

    when = pin_time(args.community, fork.pin, args.pin_time)
    rows, entries, usb = fork.entries()
    text = baseline_text(fork, when, rows, entries)
    report(fork, when, rows, entries, usb)
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
    print("wrote %s" % OUTPUT)


if __name__ == "__main__":
    main()
