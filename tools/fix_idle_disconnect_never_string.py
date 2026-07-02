"""#162: reword the idle-disconnect suffix from "0 = off" to "0 = never"
across all 10 locales. In-place value replacement, BOM-preserving,
idempotent."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "Devices_IdleDisconnectMinutes": [
        "minutes (0 = never)", "Minuten (0 = nie)", "minutos (0 = nunca)",
        "minutes (0 = jamais)", "minuti (0 = mai)", "分 (0 = しない)",
        "분 (0 = 안 함)", "minuten (0 = nooit)", "minutos (0 = nunca)",
        "分钟（0 = 从不）"],
}


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for li, fname in enumerate(LOCALES):
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    changed = 0
    for key, values in KEYS.items():
        new_line = f'  <data name="{key}" xml:space="preserve"><value>{xml_escape(values[li])}</value></data>'
        pattern = rf'  <data name="{re.escape(key)}" xml:space="preserve"><value>[^<]*</value></data>'
        text, n = re.subn(pattern, new_line, text)
        changed += n
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  ({changed} replaced)")
