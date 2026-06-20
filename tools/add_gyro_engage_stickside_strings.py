"""Add the issue #120 gyro engage stick-side selector strings to all 10
locales (English base + 9 translations). Each string is anchored to an
adjacent existing key present in every locale. Idempotent: re-running
skips keys already present. Mirrors add_gyro_engage_toggle_strings.py.

Strings added (per locale):
  Settings_GyroEngageStickSide          (anchor: Settings_GyroAimEngageMode_Toggle)
  Settings_GyroEngageStickSide_Tooltip
  Settings_GyroEngageStickSide_Right
  Settings_GyroEngageStickSide_Left
  Settings_GyroEngageStickSide_Either
  Pad_ResetGyroEngageStickSide          (anchor: Pad_ResetGyroAimEngageMode)
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = {
    "Strings.resx": {  # English base (Title Case labels, sentence-case tooltip)
        "Settings_GyroEngageStickSide":         "Engage Stick",
        "Settings_GyroEngageStickSide_Tooltip": "Which stick's deflection the Easy Aim threshold gates on. Right (the default) keeps existing profiles unchanged. Either gates on whichever stick moves further.",
        "Settings_GyroEngageStickSide_Right":   "Right Stick",
        "Settings_GyroEngageStickSide_Left":    "Left Stick",
        "Settings_GyroEngageStickSide_Either":  "Either Stick",
        "Pad_ResetGyroEngageStickSide":         "Reset Engage Stick",
    },
    "Strings.de.resx": {
        "Settings_GyroEngageStickSide":         "Aktivierungs-Stick",
        "Settings_GyroEngageStickSide_Tooltip": "Welcher Stick die Easy-Aim-Schwelle steuert. Rechts (Standard) lässt vorhandene Profile unverändert. Beide steuert über den Stick, der weiter ausgelenkt wird.",
        "Settings_GyroEngageStickSide_Right":   "Rechter Stick",
        "Settings_GyroEngageStickSide_Left":    "Linker Stick",
        "Settings_GyroEngageStickSide_Either":  "Beide Sticks",
        "Pad_ResetGyroEngageStickSide":         "Aktivierungs-Stick zurücksetzen",
    },
    "Strings.es.resx": {
        "Settings_GyroEngageStickSide":         "Stick de activación",
        "Settings_GyroEngageStickSide_Tooltip": "Qué stick controla el umbral de Easy Aim. Derecho (predeterminado) mantiene los perfiles existentes sin cambios. Cualquiera usa el stick que se desplace más.",
        "Settings_GyroEngageStickSide_Right":   "Stick derecho",
        "Settings_GyroEngageStickSide_Left":    "Stick izquierdo",
        "Settings_GyroEngageStickSide_Either":  "Cualquier stick",
        "Pad_ResetGyroEngageStickSide":         "Restablecer stick de activación",
    },
    "Strings.fr.resx": {
        "Settings_GyroEngageStickSide":         "Stick d'activation",
        "Settings_GyroEngageStickSide_Tooltip": "Quel stick conditionne le seuil Easy Aim. Droit (par défaut) laisse les profils existants inchangés. Les deux utilisent le stick le plus déplacé.",
        "Settings_GyroEngageStickSide_Right":   "Stick droit",
        "Settings_GyroEngageStickSide_Left":    "Stick gauche",
        "Settings_GyroEngageStickSide_Either":  "Les deux sticks",
        "Pad_ResetGyroEngageStickSide":         "Réinitialiser le stick d'activation",
    },
    "Strings.it.resx": {
        "Settings_GyroEngageStickSide":         "Stick di attivazione",
        "Settings_GyroEngageStickSide_Tooltip": "Quale stick controlla la soglia di Easy Aim. Destro (predefinito) lascia invariati i profili esistenti. Entrambi usa lo stick spostato di più.",
        "Settings_GyroEngageStickSide_Right":   "Stick destro",
        "Settings_GyroEngageStickSide_Left":    "Stick sinistro",
        "Settings_GyroEngageStickSide_Either":  "Entrambi gli stick",
        "Pad_ResetGyroEngageStickSide":         "Ripristina stick di attivazione",
    },
    "Strings.ja.resx": {
        "Settings_GyroEngageStickSide":         "起動スティック",
        "Settings_GyroEngageStickSide_Tooltip": "Easy Aim しきい値をどのスティックでゲートするか。右（既定）は既存のプロファイルをそのまま維持します。両方はより大きく傾けたスティックを使用します。",
        "Settings_GyroEngageStickSide_Right":   "右スティック",
        "Settings_GyroEngageStickSide_Left":    "左スティック",
        "Settings_GyroEngageStickSide_Either":  "両スティック",
        "Pad_ResetGyroEngageStickSide":         "起動スティックをリセット",
    },
    "Strings.ko.resx": {
        "Settings_GyroEngageStickSide":         "활성화 스틱",
        "Settings_GyroEngageStickSide_Tooltip": "Easy Aim 임계값을 게이트할 스틱. 오른쪽(기본값)은 기존 프로필을 그대로 유지합니다. 둘 다는 더 많이 기울어진 스틱을 사용합니다.",
        "Settings_GyroEngageStickSide_Right":   "오른쪽 스틱",
        "Settings_GyroEngageStickSide_Left":    "왼쪽 스틱",
        "Settings_GyroEngageStickSide_Either":  "양쪽 스틱",
        "Pad_ResetGyroEngageStickSide":         "활성화 스틱 재설정",
    },
    "Strings.nl.resx": {
        "Settings_GyroEngageStickSide":         "Activeringsstick",
        "Settings_GyroEngageStickSide_Tooltip": "Welke stick de Easy Aim-drempel regelt. Rechts (standaard) laat bestaande profielen ongewijzigd. Beide gebruikt de stick die het verst wordt uitgeslagen.",
        "Settings_GyroEngageStickSide_Right":   "Rechterstick",
        "Settings_GyroEngageStickSide_Left":    "Linkerstick",
        "Settings_GyroEngageStickSide_Either":  "Beide sticks",
        "Pad_ResetGyroEngageStickSide":         "Activeringsstick resetten",
    },
    "Strings.pt-BR.resx": {
        "Settings_GyroEngageStickSide":         "Analógico de Ativação",
        "Settings_GyroEngageStickSide_Tooltip": "Qual analógico controla o limiar do Easy Aim. Direito (padrão) mantém os perfis existentes inalterados. Qualquer um usa o analógico mais deslocado.",
        "Settings_GyroEngageStickSide_Right":   "Analógico Direito",
        "Settings_GyroEngageStickSide_Left":    "Analógico Esquerdo",
        "Settings_GyroEngageStickSide_Either":  "Qualquer Analógico",
        "Pad_ResetGyroEngageStickSide":         "Restaurar Analógico de Ativação",
    },
    "Strings.zh-Hans.resx": {
        "Settings_GyroEngageStickSide":         "激活摇杆",
        "Settings_GyroEngageStickSide_Tooltip": "Easy Aim 阈值由哪个摇杆门控。右（默认）保持现有配置不变。任一使用偏移更大的摇杆。",
        "Settings_GyroEngageStickSide_Right":   "右摇杆",
        "Settings_GyroEngageStickSide_Left":    "左摇杆",
        "Settings_GyroEngageStickSide_Either":  "任一摇杆",
        "Pad_ResetGyroEngageStickSide":         "重置激活摇杆",
    },
}

INSERTION_PLAN = [
    ("Settings_GyroAimEngageMode_Toggle", [
        "Settings_GyroEngageStickSide",
        "Settings_GyroEngageStickSide_Tooltip",
        "Settings_GyroEngageStickSide_Right",
        "Settings_GyroEngageStickSide_Left",
        "Settings_GyroEngageStickSide_Either",
    ]),
    ("Pad_ResetGyroAimEngageMode", [
        "Pad_ResetGyroEngageStickSide",
    ]),
]


def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom


def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)


def xml_escape(s):
    return (s.replace("&", "&amp;")
             .replace("<", "&lt;")
             .replace(">", "&gt;"))


def insert_keys(text, anchor_key, new_keys, translations):
    pat = re.compile(
        rf'(  <data name="{re.escape(anchor_key)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)'
    )
    m = pat.search(text)
    if not m:
        return text, [], [f"anchor {anchor_key} not found"]
    inserted, skipped, lines = [], [], []
    for k in new_keys:
        if f'<data name="{k}"' in text:
            skipped.append(k)
            continue
        v = xml_escape(translations.get(k, k))
        lines.append(f'  <data name="{k}" xml:space="preserve"><value>{v}</value></data>\n')
        inserted.append(k)
    if not lines:
        return text, [], skipped
    new_text = text[:m.end()] + "".join(lines) + text[m.end():]
    return new_text, inserted, skipped


for fname, translations in LOCALES.items():
    p = ROOT / fname
    text, bom = read_text(p)
    total_inserted, total_skipped, errors = [], [], []
    for anchor, new_keys in INSERTION_PLAN:
        text, ins, skp = insert_keys(text, anchor, new_keys, translations)
        if skp and isinstance(skp[0], str) and skp[0].startswith("anchor"):
            errors.extend(skp)
            continue
        total_inserted.extend(ins)
        total_skipped.extend(skp)
    if errors:
        print(f"FAIL {fname}: {', '.join(errors)}")
        continue
    if total_inserted:
        write_text(p, text, bom)
    print(f"OK   {fname}  (inserted {len(total_inserted)}, skipped {len(total_skipped)})")
