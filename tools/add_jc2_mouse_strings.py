"""Add the #154 Joy-Con 2 mouse strings (Mapping_MouseMotionX/Y, Mouse_ScrollH)
to all 10 locales, anchored after keys present in every file. Idempotent,
BOM-preserving. Mirrors add_nfc_anytag_string.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# key -> anchor key it is inserted after -> per-file values
KEYS = [
    ("Mapping_MouseMotionX", "Mapping_JoyConIrBrightness", {
        "Strings.resx":         "Mouse Motion X",
        "Strings.de.resx":      "Mausbewegung X",
        "Strings.es.resx":      "Movimiento del ratón X",
        "Strings.fr.resx":      "Mouvement de la souris X",
        "Strings.it.resx":      "Movimento del mouse X",
        "Strings.ja.resx":      "マウス移動 X",
        "Strings.ko.resx":      "마우스 이동 X",
        "Strings.nl.resx":      "Muisbeweging X",
        "Strings.pt-BR.resx":   "Movimento do Mouse X",
        "Strings.zh-Hans.resx": "鼠标移动 X",
    }),
    ("Mapping_MouseMotionY", "Mapping_MouseMotionX", {
        "Strings.resx":         "Mouse Motion Y",
        "Strings.de.resx":      "Mausbewegung Y",
        "Strings.es.resx":      "Movimiento del ratón Y",
        "Strings.fr.resx":      "Mouvement de la souris Y",
        "Strings.it.resx":      "Movimento del mouse Y",
        "Strings.ja.resx":      "マウス移動 Y",
        "Strings.ko.resx":      "마우스 이동 Y",
        "Strings.nl.resx":      "Muisbeweging Y",
        "Strings.pt-BR.resx":   "Movimento do Mouse Y",
        "Strings.zh-Hans.resx": "鼠标移动 Y",
    }),
    ("Mouse_ScrollH", "Mouse_Scroll", {
        "Strings.resx":         "Scroll Horizontal",
        "Strings.de.resx":      "Horizontal scrollen",
        "Strings.es.resx":      "Desplazamiento horizontal",
        "Strings.fr.resx":      "Défilement horizontal",
        "Strings.it.resx":      "Scorrimento orizzontale",
        "Strings.ja.resx":      "水平スクロール",
        "Strings.ko.resx":      "가로 스크롤",
        "Strings.nl.resx":      "Horizontaal scrollen",
        "Strings.pt-BR.resx":   "Rolagem Horizontal",
        "Strings.zh-Hans.resx": "水平滚动",
    }),
]


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for key, anchor, values in KEYS:
    for fname, value in values.items():
        p = ROOT / fname
        raw = p.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        if f'<data name="{key}"' in text:
            print(f"OK   {fname}  {key} (already present)")
            continue
        m = re.search(
            rf'(  <data name="{re.escape(anchor)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)',
            text)
        if not m:
            print(f"FAIL {fname}: anchor {anchor} not found for {key}")
            continue
        line = f'  <data name="{key}" xml:space="preserve"><value>{xml_escape(value)}</value></data>\n'
        text = text[:m.end()] + line + text[m.end():]
        out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
        p.write_bytes(out)
        print(f"OK   {fname}  {key} (inserted)")
