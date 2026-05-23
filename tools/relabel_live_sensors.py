"""Relabel Settings_GyroLiveRate_Label + Settings_GyroLiveAccel_Label
to parallel sensor-name forms across all 10 locales. No abbreviations.

Anchored on each locale's existing value string for both keys.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# (file, (rate_old, rate_new), (accel_old, accel_new))
EDITS = {
    "Strings.resx":         (("Live gyroscope",            "Live gyroscope"),  # already set, no-op
                             ("Live accel",                "Live accelerometer")),
    "Strings.de.resx":      (("Aktuelle Rate",             "Aktuelles Gyroskop"),
                             ("Aktuelle Beschleunigung",   "Aktueller Beschleunigungssensor")),
    "Strings.es.resx":      (("Tasa actual",               "Giroscopio actual"),
                             ("Aceleración actual",        "Acelerómetro actual")),
    "Strings.fr.resx":      (("Taux en direct",            "Gyroscope en direct"),
                             ("Accélération en direct",    "Accéléromètre en direct")),
    "Strings.it.resx":      (("Frequenza attuale",         "Giroscopio attuale"),
                             ("Accelerazione attuale",     "Accelerometro attuale")),
    "Strings.ja.resx":      (("現在のレート",
                              "現在のジャイロスコープ"),
                             ("現在の加速度",
                              "現在の加速度センサー")),
    "Strings.ko.resx":      (("현재 속도",
                              "현재 자이로스코프"),
                             ("현재 가속도",
                              "현재 가속도계")),
    "Strings.nl.resx":      (("Huidige snelheid",          "Huidige gyroscoop"),
                             ("Huidige versnelling",       "Huidige versnellingsmeter")),
    "Strings.pt-BR.resx":   (("Taxa atual",                "Giroscópio atual"),
                             ("Aceleração atual",          "Acelerômetro atual")),
    "Strings.zh-Hans.resx": (("当前速率",
                              "当前陀螺仪"),
                             ("当前加速度",
                              "当前加速度计")),
}

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

def swap(text, key, old, new):
    if old == new:
        return text, 0  # no-op
    anchor_old = f'<data name="{key}" xml:space="preserve"><value>{old}</value></data>'
    anchor_new = f'<data name="{key}" xml:space="preserve"><value>{new}</value></data>'
    if anchor_new in text:
        return text, 0  # already updated
    if anchor_old not in text:
        return None, 0  # anchor missing — fail loudly
    return text.replace(anchor_old, anchor_new, 1), 1

for fname, (rate, accel) in EDITS.items():
    p = ROOT / fname
    text, bom = read_text(p)

    text2, n1 = swap(text, "Settings_GyroLiveRate_Label",  rate[0],  rate[1])
    if text2 is None:
        print(f"FAIL {fname}: rate anchor not found ({rate[0]!r})")
        continue
    text3, n2 = swap(text2, "Settings_GyroLiveAccel_Label", accel[0], accel[1])
    if text3 is None:
        print(f"FAIL {fname}: accel anchor not found ({accel[0]!r})")
        continue

    if n1 == 0 and n2 == 0:
        print(f"SKIP {fname}: both already current")
        continue
    write_text(p, text3, bom)
    print(f"OK   {fname}  (rate: {'changed' if n1 else 'skip'}, accel: {'changed' if n2 else 'skip'})")
