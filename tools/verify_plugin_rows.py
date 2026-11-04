"""Holds the frames SignalRGB's plugins send against the frames PadForge's rows send.

    set PADFORGE_PLUGIN_ROWS_TSV=C:\\tmp\\rows.tsv
    dotnet test PadForge.Tests --filter FullyQualifiedName~PluginRowFramesDump
    node tools/verify_plugin_rows.mjs GITHUB_FOLDER C:/tmp/rows.tsv > C:/tmp/ran.jsonl
    python tools/verify_plugin_rows.py C:/tmp/rows.tsv C:/tmp/ran.jsonl [--starts]

A light shows a color made of its place, or of where its row and its plugin list it, so a frame
shows which LED has which place. A row that asks its device what it is has a line for each answer. A
row in EXPLAINED differs for the reason beside it, and any other difference is a row to look at.
A plugin that keeps sending with nothing changed proves its device only under that stream, so such
a row has to send its picture again too. --starts lists what each plugin's Initialize sends beyond
the row's start, which is mostly its DPI, polling and battery settings.
"""
import json
import re
import sys

# A row differs for the reason here, by its plugin or by its plugin and the answer it was given.
EXPLAINED = {
    "Thermal Take/ThermalTake_Argent_Mouse_Bungee.js": "the plugin colors the slot it names Null",
    "Thermal Take/Thermaltake_Dasher_RGB_Mousepad.js": "each of the plugin's reports carries the first light's color, each here its own",
    "_Random-Brands/Meletrix_Zoom98_QMK_Keyboard.js": "the plugin lists LED 17 twice and so sends one black LED past the last",
    "Sonix/SONIX_Keyboard_Controller_2.js|DP-KD-87A-000102-GMS": "the plugin's TKL list of LEDs is nested one level too deep, so it sends no colors",
    "DesignedByGG/GG_QMK_ISO_Keyboard_TKL_AZERTY_French.js": "the plugin's places run a key off its names from F12 to the right shift",
    "Keychron/K Series/Max/Keychron_K10_v2_QMK_ANSI_Keyboard.js": "the plugin puts / at the right shift's place",
    "Keychron/K Series/Standard/Keychron_K8_TKL_QMK_ISO_Keyboard.js": "the plugin puts F11 at F12's place",
    "Nuvoton/Nuvoton_Keyboard_Controller.js|K221UKCVRGB": "the plugin puts / at the dot's place",
    "Asus/ASUS_Keyboard.js": "the plugin's places leave out the second logo's, so a row's last key shows at the start of the next",
    "Asus/Omni Controller/ASUS_Omni_Device.js|A23114802911": "the plugin's places leave out the second logo's, so a row's last key shows at the start of the next",
    "Asus/Omni Controller/ASUS_Omni_Device.js|A23114802912": "the plugin's places leave out the second logo's, so a row's last key shows at the start of the next",
    "GMMK/GMMK_3_QMK_Keyboard.js|11": "the plugin's numbers run off the firmware's list",
    "GMMK/GMMK_3_QMK_Keyboard.js|12": "the plugin's numbers run off the firmware's list",
    "GMMK/GMMK_3_QMK_Keyboard.js|14": "the plugin's numbers run off the firmware's list",
    "GMMK/GMMK_3_QMK_Keyboard.js|16": "the plugin has the firmware's # and Enter crossed",
    "Alienware/Alienware_Monitor_Gen2_Controller.js": "byte 0: PadForge sends report ID 0 as OpenRGB does, the plugin leaves FF",
    "Corsair/Corsair_Nexus_Screen.js": "PadForge fills the screen, the plugin every other pixel",
    "OEM/OEM_Keyboard_Wireless_Controller.js": "the plugin's checksum loop runs off its array and gives 0",
    "Keychron/C Series/Pro/Keychron_C1_Pro_QMK_ANSI_Keyboard.js": "the plugin's product test names the K1 Pro and always picks ISO",
}

# Queries a plugin sends at the start whose answers a row asks for elsewhere or does not need.
QMK_QUESTIONS = re.compile(r"^O:00(28|21|22|23|27)$")

# What a plugin asks between its pictures, by plugin.
ASKED = {"Corsair/Modern_Corsair/Corsair_Headset_Controller.js": re.compile(r"^O:02090(2|6)10$")}


def trim(frame):
    kind, hexes = frame.split(":", 1)
    hexes = hexes.upper()
    while hexes.endswith("00"):
        hexes = hexes[:-2]
    return kind + ":" + hexes


def main():
    rows = {}
    for line in open(sys.argv[1], encoding="utf-8"):
        product, vendor, plugin, source, start, picture, note, ask, strips, answers, model, strip_leds, again, shading = line.rstrip("\n").split("\t")
        rows[(product, plugin, model)] = dict(source=source, start=start, picture=picture, note=note, again=int(again), shading=shading)
    ran = {}
    for line in open(sys.argv[2], encoding="utf-8"):
        o = json.loads(line)
        ran[(o["product"], o["plugin"], o.get("model", ""))] = o
    same = explained = firmware = 0
    problems, starts, errors, quiet, skipped = [], [], {}, [], []
    for key, row in rows.items():
        product, plugin, model = key
        o = ran.get(key)
        if row["source"] and row["source"] != "SignalRGB":
            continue
        if not o or o.get("error") == "no source" or row["note"]:
            skipped.append((product, plugin, model, row["note"] or "no source"))
            continue
        if o.get("error"):
            errors.setdefault(o["error"][:80], set()).add(plugin)
            continue
        if (o.get("again") or o.get("later")) and not row["again"]:
            quiet.append((product, plugin))
        mine = [trim(x) for x in row["picture"].split(";") if x]
        theirs = [trim(x) for x in o.get("render", [])]
        if plugin in ASKED:
            theirs = [t for t in theirs if not ASKED[plugin].match(t)]
        known = {trim(x) for x in row["start"].split(";") if x}
        extra = [t for t in map(trim, o.get("init", [])) if t not in known and not QMK_QUESTIONS.match(t)]
        if extra:
            starts.append((product, plugin, extra))
        if row["shading"] == "firmware" and mine and theirs and mine[0][:10] == theirs[0][:10]:
            firmware += 1
        elif mine == theirs:
            same += 1
        elif plugin in EXPLAINED or f"{plugin}|{model}" in EXPLAINED:
            explained += 1
        else:
            problems.append((product, plugin, model, mine, theirs))
    print(f"same {same}, keys from the firmware {firmware}, explained {explained}, to look at {len(problems)}, not run {len(skipped)}, quiet where the plugin streams {len(quiet)}")
    for product, plugin, model, note in skipped:
        print(f"not run {product} {plugin} {model}: {note}")
    for product, plugin in quiet:
        print(f"quiet {product} {plugin}")
    for product, plugin, model, mine, theirs in problems:
        print(f"* {product} {plugin} {model}: {len(mine)} frames here, {len(theirs)} in the plugin")
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
