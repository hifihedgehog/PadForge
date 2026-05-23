"""Add Settings_GyroLiveAccel_Label to all 10 locales.

Anchored after the existing Settings_GyroLiveRate_Label line. Translations
mirror the corresponding 'Live rate' wording in each locale.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

ENTRIES = {
    "Strings.resx":         ("Live rate",
                             "Live accel"),
    "Strings.de.resx":      ("Aktuelle Rate",
                             "Aktuelle Beschleunigung"),
    "Strings.es.resx":      ("Tasa actual",
                             "Aceleración actual"),
    "Strings.fr.resx":      ("Taux en direct",
                             "Accélération en direct"),
    "Strings.it.resx":      ("Frequenza attuale",
                             "Accelerazione attuale"),
    "Strings.ja.resx":      ("現在のレート",
                             "現在の加速度"),
    "Strings.ko.resx":      ("현재 속도",
                             "현재 가속도"),
    "Strings.nl.resx":      ("Huidige snelheid",
                             "Huidige versnelling"),
    "Strings.pt-BR.resx":   ("Taxa atual",
                             "Aceleração atual"),
    "Strings.zh-Hans.resx": ("当前速率",
                             "当前加速度"),
}

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

for fname, (existing_val, new_val) in ENTRIES.items():
    p = ROOT / fname
    text, bom = read_text(p)
    if 'name="Settings_GyroLiveAccel_Label"' in text:
        print(f"SKIP {fname}: already present")
        continue
    anchor = f'<data name="Settings_GyroLiveRate_Label" xml:space="preserve"><value>{existing_val}</value></data>'
    insertion = f'\n  <data name="Settings_GyroLiveAccel_Label" xml:space="preserve"><value>{new_val}</value></data>'
    if anchor not in text:
        print(f"FAIL {fname}: anchor not found")
        continue
    text = text.replace(anchor, anchor + insertion, 1)
    write_text(p, text, bom)
    print(f"OK   {fname}")
