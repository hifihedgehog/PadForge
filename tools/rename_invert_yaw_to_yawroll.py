"""Rename InvertYaw → InvertYawRoll across PadForge (engine/app/resx/xaml).

Why: the toggle has always covered yaw + roll + horizontal-blend (Steam-Input
convention). Label was updated already. This pass aligns the variable names.

XML disk format is preserved via [XmlElement("GyroInvertYaw")] so existing
user profiles still load. Only the C# property name and resx key change.

Run once. Asserts on expected occurrence counts so a re-run fails loudly.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# (relative path, expected count, old, new)
# Single-token swap: InvertYaw → InvertYawRoll. The substring covers every
# variant we touch (GyroInvertYaw, _gyroInvertYaw, ResetGyroInvertYawCommand,
# Settings_GyroInvertYaw, Pad_ResetGyroInvertYaw, etc.). Pitch is unaffected.
TOKEN_EDITS = [
    # Engine
    ("PadForge.Engine/Common/Mapping/SourceCoercion.cs", 4),
    # PadSetting — property decl, sb.Append, nameof
    ("PadForge.Engine/Data/PadSetting.cs", 3),
    # App services
    ("PadForge.App/MainWindow.xaml.cs", 1),
    ("PadForge.App/Services/SettingsService.cs", 2),
    ("PadForge.App/Services/InputService.cs", 3),
    # ViewModel — private field, property, reset command (3 lines), reset-all
    ("PadForge.App/ViewModels/PadViewModel.cs", 6),
    # Generated accessor (we hand-edit; build will regenerate consistently)
    ("PadForge.App/Resources/Strings/Strings.Designer.cs", 3),
    # XAML bindings
    ("PadForge.App/Views/PadPage.xaml", 4),
    # Resx — Settings_GyroInvertYaw + _Tooltip + Pad_ResetGyroInvertYaw = 3 each
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

OLD_TOKEN = "InvertYaw"
NEW_TOKEN = "InvertYawRoll"

def read_text(p: Path):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p: Path, text: str, bom: bool):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

# 1. Token rename
for rel, expected in TOKEN_EDITS:
    p = ROOT / rel
    text, bom = read_text(p)
    n = text.count(OLD_TOKEN)
    # Subtract any pre-existing NEW_TOKEN occurrences (none expected, but guard).
    n_existing_new = text.count(NEW_TOKEN)
    if n_existing_new:
        print(f"WARN {rel}: {n_existing_new} pre-existing '{NEW_TOKEN}' tokens")
    # The substring count of OLD_TOKEN includes occurrences that are already
    # NEW_TOKEN (since NEW_TOKEN starts with OLD_TOKEN). Adjust.
    actual_old_only = n - n_existing_new
    if actual_old_only != expected:
        raise SystemExit(
            f"FAIL {rel}: expected {expected} '{OLD_TOKEN}' tokens, "
            f"found {actual_old_only} (raw count {n}, "
            f"pre-existing new {n_existing_new})"
        )
    # Idempotency: replace only OLD_TOKEN that is NOT followed by 'Roll'.
    # Simplest: walk and rebuild.
    out = []
    i = 0
    while i < len(text):
        if text.startswith(OLD_TOKEN, i):
            if text.startswith(NEW_TOKEN, i):
                # Already renamed — leave alone.
                out.append(NEW_TOKEN)
                i += len(NEW_TOKEN)
            else:
                out.append(NEW_TOKEN)
                i += len(OLD_TOKEN)
        else:
            out.append(text[i])
            i += 1
    new_text = "".join(out)
    write_text(p, new_text, bom)
    print(f"OK   {rel}  ({actual_old_only} replacements)")

# 2. Pin XML disk format so existing user profiles still load.
ps_path = ROOT / "PadForge.Engine/Data/PadSetting.cs"
text, bom = read_text(ps_path)
old_decl = '[XmlElement] public string GyroInvertYawRoll { get; set; } = "0";'
new_decl = '[XmlElement("GyroInvertYaw")] public string GyroInvertYawRoll { get; set; } = "0";'
if old_decl not in text:
    raise SystemExit(f"FAIL PadSetting decl pin: anchor not found")
text = text.replace(old_decl, new_decl, 1)
write_text(ps_path, text, bom)
print("OK   PadSetting.cs  ([XmlElement(\"GyroInvertYaw\")] pinned)")

# 3. Reset-button tooltip text per locale.
RESET_TEXT_EDITS = {
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
strings_dir = ROOT / "PadForge.App/Resources/Strings"
for fname, (old, new) in RESET_TEXT_EDITS.items():
    p = strings_dir / fname
    text, bom = read_text(p)
    # Anchor on the full Pad_ResetGyroInvertYawRoll line to be precise.
    anchor_old = f'<data name="Pad_ResetGyroInvertYawRoll" xml:space="preserve"><value>{old}</value></data>'
    anchor_new = f'<data name="Pad_ResetGyroInvertYawRoll" xml:space="preserve"><value>{new}</value></data>'
    if anchor_old not in text:
        if anchor_new in text:
            print(f"SKIP {fname} reset-text: already updated")
            continue
        raise SystemExit(f"FAIL {fname} reset-text: anchor not found")
    text = text.replace(anchor_old, anchor_new, 1)
    write_text(p, text, bom)
    print(f"OK   {fname} reset-text")

print("\nAll renames applied.")
