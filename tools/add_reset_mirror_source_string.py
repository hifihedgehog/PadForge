"""Add the Pad_ResetMirrorSource tooltip (Sound Output mirror-source reset
button) to all 10 locales plus its Strings.Designer.cs property. Title-case
"Reset <Noun>" form, composed to match the sibling Pad_ResetMirrorEngageMode
verb pattern and the translated Pad_Audio_MirrorSource_Label noun. Anchored,
idempotent, BOM-preserving. Mirrors add_lighting_playernumber_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

KEY = "Pad_ResetMirrorSource"
ANCHOR = "Pad_ResetMirrorEngageMode"
VALUES = {
    "Strings.resx":         "Reset Mirror Source",
    "Strings.de.resx":      "Spiegelquelle zurücksetzen",
    "Strings.es.resx":      "Restablecer fuente de duplicado",
    "Strings.fr.resx":      "Réinitialiser la source du miroir",
    "Strings.it.resx":      "Reimposta sorgente mirroring",
    "Strings.ja.resx":      "ミラーソースをリセット",
    "Strings.ko.resx":      "미러 소스 재설정",
    "Strings.nl.resx":      "Spiegelbron resetten",
    "Strings.pt-BR.resx":   "Redefinir fonte do espelhamento",
    "Strings.zh-Hans.resx": "重置镜像来源",
}

DESIGNER = ROOT / "Strings.Designer.cs"


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

raw = DESIGNER.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
if f'public string {KEY} =>' in text:
    print(f"OK   Designer  (already present)")
else:
    needle = f'    public string {ANCHOR} => Get("{ANCHOR}");'
    idx = text.find(needle)
    if idx < 0:
        print(f"FAIL Designer: anchor {ANCHOR} not found")
    else:
        insert = f'\n    public string {KEY} => Get("{KEY}");'
        end = idx + len(needle)
        text = text[:end] + insert + text[end:]
        out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
        DESIGNER.write_bytes(out)
        print(f"OK   Designer  (inserted)")
