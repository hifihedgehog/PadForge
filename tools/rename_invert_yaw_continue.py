"""Continuation of the InvertYaw → InvertYawRoll rename.

The first run completed SourceCoercion.cs, PadSetting.cs, and
MainWindow.xaml.cs before aborting on a wrong-expected-count for
SettingsService. This pass:
  1. Skips already-renamed files (verifies 0 bare InvertYaw remaining)
  2. Renames the rest with corrected occurrence counts
  3. Pins [XmlElement("GyroInvertYaw")] on PadSetting
  4. Updates Pad_ResetGyroInvertYawRoll localized text per locale
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

ALREADY_DONE = [
    "PadForge.Engine/Common/Mapping/SourceCoercion.cs",
    "PadForge.Engine/Data/PadSetting.cs",
    "PadForge.App/MainWindow.xaml.cs",
]

# (relative path, expected occurrence count of 'InvertYaw' substring)
TOKEN_EDITS = [
    ("PadForge.App/Services/SettingsService.cs", 4),
    ("PadForge.App/Services/InputService.cs", 6),
    ("PadForge.App/ViewModels/PadViewModel.cs", 9),
    ("PadForge.App/Resources/Strings/Strings.Designer.cs", 6),
    ("PadForge.App/Views/PadPage.xaml", 5),
    ("PadForge.App/Resources/Strings/Strings.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.de.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.es.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.fr.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.it.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.ja.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.ko.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.nl.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.pt-BR.resx", 3),
    ("PadForge.App/Resources/Strings/Strings.zh-Hans.resx", 3),
]

OLD = "InvertYaw"
NEW = "InvertYawRoll"

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

def replace_old_only(text):
    """Replace OLD with NEW only when OLD is not already part of NEW."""
    out = []
    i = 0
    while i < len(text):
        if text.startswith(NEW, i):
            out.append(NEW); i += len(NEW)
        elif text.startswith(OLD, i):
            out.append(NEW); i += len(OLD)
        else:
            out.append(text[i]); i += 1
    return "".join(out)

# 0. Verify already-done files have only NEW tokens remaining
for rel in ALREADY_DONE:
    p = ROOT / rel
    text, _ = read_text(p)
    bare_old = text.count(OLD) - text.count(NEW)
    if bare_old != 0:
        raise SystemExit(f"FAIL {rel}: {bare_old} bare '{OLD}' tokens linger after first pass")
    print(f"OK   {rel}  (already renamed, no bare OLD)")

# 1. Token rename on remaining files
for rel, expected in TOKEN_EDITS:
    p = ROOT / rel
    text, bom = read_text(p)
    n = text.count(OLD)
    if n != expected:
        raise SystemExit(f"FAIL {rel}: expected {expected} '{OLD}' substrings, got {n}")
    new_text = replace_old_only(text)
    write_text(p, new_text, bom)
    print(f"OK   {rel}  ({n} substring renames)")

# 2. Pin XML disk format
ps_path = ROOT / "PadForge.Engine/Data/PadSetting.cs"
text, bom = read_text(ps_path)
old_decl = '[XmlElement] public string GyroInvertYawRoll { get; set; } = "0";'
new_decl = '[XmlElement("GyroInvertYaw")] public string GyroInvertYawRoll { get; set; } = "0";'
if new_decl in text:
    print("SKIP PadSetting.cs XmlElement pin: already pinned")
elif old_decl in text:
    text = text.replace(old_decl, new_decl, 1)
    write_text(ps_path, text, bom)
    print('OK   PadSetting.cs  ([XmlElement("GyroInvertYaw")] pinned)')
else:
    raise SystemExit("FAIL PadSetting.cs XmlElement pin: anchor not found")

# 3. Reset-button localized text update
RESET_TEXT = {
    "Strings.resx":         ("Reset Invert Yaw",            "Reset Invert Yaw / Roll"),
    "Strings.de.resx":      ("Yaw-Invertierung zurücksetzen",
                             "Yaw / Roll-Invertierung zurücksetzen"),
    "Strings.es.resx":      ("Restablecer inversión de yaw",
                             "Restablecer inversión de yaw / roll"),
    "Strings.fr.resx":      ("Réinitialiser l'inversion du lacet",
                             "Réinitialiser l'inversion du lacet / roulis"),
    "Strings.it.resx":      ("Ripristina inversione yaw",
                             "Ripristina inversione yaw / roll"),
    "Strings.ja.resx":      ("ヨー反転をリセット",
                             "ヨー / ロール反転をリセット"),
    "Strings.ko.resx":      ("요 반전 재설정",
                             "요 / 롤 반전 재설정"),
    "Strings.nl.resx":      ("Yaw-omkering resetten",
                             "Yaw / Roll-omkering resetten"),
    "Strings.pt-BR.resx":   ("Restaurar Inversão de Yaw",
                             "Restaurar Inversão de Yaw / Roll"),
    "Strings.zh-Hans.resx": ("重置偏航反转",
                             "重置偏航 / 滚转反转"),
}
sdir = ROOT / "PadForge.App/Resources/Strings"
for fname, (old, new) in RESET_TEXT.items():
    p = sdir / fname
    text, bom = read_text(p)
    anchor_old = f'<data name="Pad_ResetGyroInvertYawRoll" xml:space="preserve"><value>{old}</value></data>'
    anchor_new = f'<data name="Pad_ResetGyroInvertYawRoll" xml:space="preserve"><value>{new}</value></data>'
    if anchor_new in text:
        print(f"SKIP {fname} reset-text: already updated")
        continue
    if anchor_old not in text:
        raise SystemExit(f"FAIL {fname} reset-text: anchor not found")
    text = text.replace(anchor_old, anchor_new, 1)
    write_text(p, text, bom)
    print(f"OK   {fname} reset-text")

print("\nRename complete.")
