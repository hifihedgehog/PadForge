"""Rename Settings_GyroInvertYaw label across all 10 resx locales.

Old label: "Invert Yaw (X)" (and localized equivalents)
New label: "Invert Yaw / Roll (X)" (and localized equivalents)

Tooltip already mentions roll/horizontal-blend; only the label is changed.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# (file, old_value, new_value) tuples
edits = [
    ("Strings.resx",        "Invert Yaw (X)",          "Invert Yaw / Roll (X)"),
    ("Strings.de.resx",     "Yaw (X) invertieren",     "Yaw / Roll (X) invertieren"),
    ("Strings.es.resx",     "Invertir Yaw (X)",        "Invertir Yaw / Roll (X)"),
    ("Strings.fr.resx",     "Inverser le lacet (X)",   "Inverser le lacet / roulis (X)"),
    ("Strings.it.resx",     "Inverti Yaw (X)",         "Inverti Yaw / Roll (X)"),
    ("Strings.ja.resx",     "ヨー (X) 反転",
                            "ヨー / ロール (X) 反転"),
    ("Strings.ko.resx",     "요(X) 반전",
                            "요 / 롤 (X) 반전"),
    ("Strings.nl.resx",     "Yaw (X) omkeren",         "Yaw / Roll (X) omkeren"),
    ("Strings.pt-BR.resx",  "Inverter Yaw (X)",        "Inverter Yaw / Roll (X)"),
    ("Strings.zh-Hans.resx","反转偏航（X）",
                            "反转偏航 / 滚转（X）"),
]

# Anchor the replacement to the label data line so we never touch the tooltip line.
TEMPLATE = '<data name="Settings_GyroInvertYaw" xml:space="preserve"><value>{val}</value></data>'

for fname, old, new in edits:
    p = ROOT / fname
    raw = p.read_bytes()
    bom = b"\xef\xbb\xbf"
    has_bom = raw.startswith(bom)
    text = raw.decode("utf-8-sig")
    old_line = TEMPLATE.format(val=old)
    new_line = TEMPLATE.format(val=new)
    if old_line not in text:
        print(f"SKIP {fname}: anchor not found (already renamed?)")
        continue
    text2 = text.replace(old_line, new_line, 1)
    out = (bom if has_bom else b"") + text2.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}")
