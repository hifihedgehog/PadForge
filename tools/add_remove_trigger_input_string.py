"""Add the Macro_RemoveTriggerInput string (X button tooltip on each trigger
input row) to all 10 locales. Idempotent, BOM-preserving. Anchored after
Macro_AddTriggerFromList_Tooltip."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "Macro_RemoveTriggerInput": ["Remove input", "Eingabe entfernen", "Quitar entrada",
        "Retirer l'entrée", "Rimuovi input", "入力を削除", "입력 제거",
        "Invoer verwijderen", "Remover entrada", "移除输入"],
}

ANCHOR = "Macro_AddTriggerFromList_Tooltip"


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for li, fname in enumerate(LOCALES):
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    m = re.search(
        rf'(  <data name="{re.escape(ANCHOR)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)',
        text)
    if not m:
        print(f"FAIL {fname}: anchor {ANCHOR} not found")
        continue
    insert = []
    for key, values in KEYS.items():
        if f'<data name="{key}"' in text:
            continue
        insert.append(f'  <data name="{key}" xml:space="preserve"><value>{xml_escape(values[li])}</value></data>\n')
    if not insert:
        print(f"OK   {fname}  (already present)")
        continue
    text = text[:m.end()] + "".join(insert) + text[m.end():]
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  (+{len(insert)} keys)")
