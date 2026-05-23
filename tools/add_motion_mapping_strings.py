"""Add Mapping_MotionGyro / Mapping_MotionAccel strings to all 9 locales.

Anchored on the existing Mapping_TouchpadClick line so insertion is
deterministic and idempotent (skip if already present).
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# Anchor + insertion: matched against the literal Mapping_TouchpadClick line
# already present in every locale. New lines inserted directly after.
INSERTIONS = {
    "Strings.de.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Bewegung Gyro</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Bewegung Beschleunigung</value></data>',
    ],
    "Strings.es.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Movimiento giroscopio</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Movimiento acelerómetro</value></data>',
    ],
    "Strings.fr.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Mouvement gyroscope</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Mouvement accéléromètre</value></data>',
    ],
    "Strings.it.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Movimento giroscopio</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Movimento accelerometro</value></data>',
    ],
    "Strings.ja.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>モーション ジャイロ</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>モーション 加速度</value></data>',
    ],
    "Strings.ko.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>모션 자이로</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>모션 가속도</value></data>',
    ],
    "Strings.nl.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Beweging Gyro</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Beweging Versnelling</value></data>',
    ],
    "Strings.pt-BR.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>Movimento Giroscópio</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>Movimento Acelerômetro</value></data>',
    ],
    "Strings.zh-Hans.resx": [
        '  <data name="Mapping_MotionGyro" xml:space="preserve"><value>运动 陀螺仪</value></data>',
        '  <data name="Mapping_MotionAccel" xml:space="preserve"><value>运动 加速度计</value></data>',
    ],
}

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

# Find the Mapping_TouchpadClick line in each locale and insert after it.
import re
PATTERN = re.compile(r'(\s*<data name="Mapping_TouchpadClick"[^>]*>\s*<value>[^<]*</value>\s*</data>\s*\n)')

for fname, new_lines in INSERTIONS.items():
    p = ROOT / fname
    text, bom = read_text(p)
    if 'name="Mapping_MotionGyro"' in text:
        print(f"SKIP {fname}: already has MotionGyro")
        continue
    m = PATTERN.search(text)
    if not m:
        print(f"FAIL {fname}: TouchpadClick anchor not found")
        continue
    insertion = "\n".join(new_lines) + "\n"
    new_text = text[:m.end()] + insertion + text[m.end():]
    write_text(p, new_text, bom)
    print(f"OK   {fname}")
