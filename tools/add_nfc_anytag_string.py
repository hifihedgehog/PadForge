"""Add Mapping_NfcAnyTag (#150) to all 10 locales, anchored after the
DevObj_Button key present in every file. Idempotent, BOM-preserving.
Mirrors add_gyro_engage_stickside_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

VALUES = {
    "Strings.resx":        "Any NFC Tag",
    "Strings.de.resx":     "Beliebiger NFC-Tag",
    "Strings.es.resx":     "Cualquier etiqueta NFC",
    "Strings.fr.resx":     "N'importe quelle étiquette NFC",
    "Strings.it.resx":     "Qualsiasi tag NFC",
    "Strings.ja.resx":     "任意の NFC タグ",
    "Strings.ko.resx":     "모든 NFC 태그",
    "Strings.nl.resx":     "Elke NFC-tag",
    "Strings.pt-BR.resx":  "Qualquer Tag NFC",
    "Strings.zh-Hans.resx": "任意 NFC 标签",
}
ANCHOR = "DevObj_Button"
KEY = "Mapping_NfcAnyTag"


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for fname, value in VALUES.items():
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    if f'<data name="{KEY}"' in text:
        print(f"OK   {fname}  (already present)")
        continue
    m = re.search(
        rf'(  <data name="{re.escape(ANCHOR)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)',
        text)
    if not m:
        print(f"FAIL {fname}: anchor {ANCHOR} not found")
        continue
    line = f'  <data name="{KEY}" xml:space="preserve"><value>{xml_escape(value)}</value></data>\n'
    text = text[:m.end()] + line + text[m.end():]
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  (inserted)")
