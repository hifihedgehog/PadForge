"""Holds the frames SignalRGB's plugins send against the frames PadForge's rows send.

    set PADFORGE_PLUGIN_ROWS_TSV=C:\\tmp\\rows.tsv
    dotnet test PadForge.Tests --filter FullyQualifiedName~PluginRowFramesDump
    node tools/verify_plugin_rows.mjs GITHUB_FOLDER C:/tmp/rows.tsv > C:/tmp/ran.jsonl
    python tools/verify_plugin_rows.py C:/tmp/rows.tsv C:/tmp/ran.jsonl [--starts]

Every light shows one color. A row in EXPLAINED differs for the reason beside it, and any other
difference is a row to look at. --starts lists what each plugin's Initialize sends beyond the row's
start, which is mostly its DPI, polling and battery settings.
"""
import json
import re
import sys

EXPLAINED = {
    "Thermal Take/ThermalTake_Argent_Mouse_Bungee.js": "the plugin colors the slot it names Null",
    "Alienware/Alienware_Monitor_Gen2_Controller.js": "byte 0: PadForge sends report ID 0 as OpenRGB does, the plugin leaves FF",
    "Corsair/Corsair_Nexus_Screen.js": "PadForge fills the screen, the plugin every other pixel",
    "OEM/OEM_Keyboard_Wireless_Controller.js": "the plugin's checksum loop runs off its array and gives 0",
    "Alpaca/Alpaca_WhiteFox_Eclipse_QMK_Keyboard.js": "three LEDs a report in the plugin, nine in PadForge, the same command",
    "Asus/ASUS_ROG_Claymore_II.js": "the plate's side comes from an answer the stand-in cannot give",
    "Asus/Omni Controller/ASUS_Omni_Device.js": "a paired device comes from an answer the stand-in cannot give",
    "Corsair/Modern_Corsair/Corsair_Headset_Controller.js": "the wireless models wait for the headset's answer",
    "GMMK/GMMK_3_QMK_Keyboard.js": "the layout comes from the firmware's unique ID",
    "GMMK/GMMK_Pro_QMK_Keyboard.js": "the layout comes from the firmware's unique ID",
    "Keychron/C Series/Pro/Keychron_C1_Pro_QMK_ANSI_Keyboard.js": "the plugin's product test names the K1 Pro and always picks ISO",
}

# Queries a plugin sends at the start whose answers a row asks for elsewhere or does not need.
QMK_QUESTIONS = re.compile(r"^O:00(28|21|22|23|27)$")


def trim(frame):
    kind, hexes = frame.split(":", 1)
    hexes = hexes.upper()
    while hexes.endswith("00"):
        hexes = hexes[:-2]
    return kind + ":" + hexes


def main():
    rows = {}
    for line in open(sys.argv[1], encoding="utf-8"):
        product, vendor, plugin, source, start, picture, note, ask, strips, answers = line.rstrip("\n").split("\t")
        rows[(product, plugin)] = dict(source=source, start=start, picture=picture, note=note, strips=strips)
    ran = {}
    for line in open(sys.argv[2], encoding="utf-8"):
        o = json.loads(line)
        ran[(o["product"], o["plugin"])] = o
    same = explained = 0
    problems, starts, errors = [], [], {}
    for key, row in rows.items():
        product, plugin = key
        o = ran.get(key)
        if (row["source"] and row["source"] != "SignalRGB") or not o or o.get("error") == "no source" or row["strips"] != "0" or row["note"]:
            continue
        if o.get("error"):
            errors.setdefault(o["error"][:80], set()).add(plugin)
            continue
        mine = [trim(x) for x in row["picture"].split(";") if x]
        theirs = [trim(x) for x in o.get("render", [])]
        known = {trim(x) for x in row["start"].split(";") if x}
        extra = [t for t in map(trim, o.get("init", [])) if t not in known and not QMK_QUESTIONS.match(t)]
        if extra:
            starts.append((product, plugin, extra))
        if mine == theirs:
            same += 1
        elif plugin in EXPLAINED:
            explained += 1
        else:
            problems.append((product, plugin, mine, theirs))
    print(f"same {same}, explained {explained}, to look at {len(problems)}")
    for product, plugin, mine, theirs in problems:
        print(f"* {product} {plugin}: {len(mine)} frames here, {len(theirs)} in the plugin")
        for a, b in zip(mine, theirs):
            if a != b:
                print(f"    here   {a[:100]}")
                print(f"    plugin {b[:100]}")
                break
    for message, plugins in errors.items():
        print(f"could not run: {message}: {sorted(plugins)[:4]}")
    if "--starts" in sys.argv:
        for product, plugin, extra in starts:
            print(f"start {product} {plugin}")
            for frame in extra[:6]:
                print(f"    {frame[:100]}")


main()
