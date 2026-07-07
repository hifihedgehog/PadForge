"""Add the #174 stick boundary calibration strings (Pad_Sticks_Boundary_* and
Pad_ResetStickBoundary) to all 10 locales, anchored after Pad_ResetCenterOffsetX
which is present in every file. Idempotent, BOM-preserving. Mirrors
add_mirror_engage_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

KEYS = [
    ("Pad_Sticks_Boundary_Calibrate", "Pad_ResetCenterOffsetX", {
        "Strings.resx":         "Calibrate boundary",
        "Strings.de.resx":      "Begrenzung kalibrieren",
        "Strings.es.resx":      "Calibrar límite",
        "Strings.fr.resx":      "Calibrer la limite",
        "Strings.it.resx":      "Calibra il bordo",
        "Strings.ja.resx":      "境界を調整",
        "Strings.ko.resx":      "경계 보정",
        "Strings.nl.resx":      "Grens kalibreren",
        "Strings.pt-BR.resx":   "Calibrar limite",
        "Strings.zh-Hans.resx": "校准边界",
    }),
    ("Pad_Sticks_Boundary_Recalibrate", "Pad_Sticks_Boundary_Calibrate", {
        "Strings.resx":         "Recalibrate boundary",
        "Strings.de.resx":      "Begrenzung neu kalibrieren",
        "Strings.es.resx":      "Recalibrar límite",
        "Strings.fr.resx":      "Recalibrer la limite",
        "Strings.it.resx":      "Ricalibra il bordo",
        "Strings.ja.resx":      "境界を再調整",
        "Strings.ko.resx":      "경계 재보정",
        "Strings.nl.resx":      "Grens opnieuw kalibreren",
        "Strings.pt-BR.resx":   "Recalibrar limite",
        "Strings.zh-Hans.resx": "重新校准边界",
    }),
    ("Pad_Sticks_Boundary_Sweeping", "Pad_Sticks_Boundary_Recalibrate", {
        "Strings.resx":         "Sweep the rim... {0} left",
        "Strings.de.resx":      "Rand umkreisen... noch {0}",
        "Strings.es.resx":      "Recorre el borde... quedan {0}",
        "Strings.fr.resx":      "Parcourez le bord... {0} restants",
        "Strings.it.resx":      "Percorri il bordo... {0} rimasti",
        "Strings.ja.resx":      "縁をなぞってください... 残り {0}",
        "Strings.ko.resx":      "가장자리를 훑으세요... {0}개 남음",
        "Strings.nl.resx":      "Volg de rand... nog {0}",
        "Strings.pt-BR.resx":   "Percorra a borda... faltam {0}",
        "Strings.zh-Hans.resx": "沿边缘转动… 还剩 {0}",
    }),
    ("Pad_Sticks_Boundary_Circularity", "Pad_Sticks_Boundary_Sweeping", {
        "Strings.resx":         "Circularity {0:F0}%",
        "Strings.de.resx":      "Rundheit {0:F0}%",
        "Strings.es.resx":      "Circularidad {0:F0}%",
        "Strings.fr.resx":      "Circularité {0:F0}%",
        "Strings.it.resx":      "Circolarità {0:F0}%",
        "Strings.ja.resx":      "真円度 {0:F0}%",
        "Strings.ko.resx":      "원형도 {0:F0}%",
        "Strings.nl.resx":      "Rondheid {0:F0}%",
        "Strings.pt-BR.resx":   "Circularidade {0:F0}%",
        "Strings.zh-Hans.resx": "圆度 {0:F0}%",
    }),
    ("Pad_Sticks_Boundary_Tooltip", "Pad_Sticks_Boundary_Circularity", {
        "Strings.resx":         "Sweep the stick around its rim to map the reachable boundary, then reshape it to a full circle.",
        "Strings.de.resx":      "Umkreise den Rand mit dem Stick, um die erreichbare Begrenzung zu erfassen, und forme sie zu einem vollen Kreis.",
        "Strings.es.resx":      "Recorre el borde con el stick para mapear el límite alcanzable y reformarlo a un círculo completo.",
        "Strings.fr.resx":      "Parcourez le bord avec le stick pour cartographier la limite atteignable, puis reformez-la en cercle complet.",
        "Strings.it.resx":      "Percorri il bordo con lo stick per mappare il confine raggiungibile e rimodellarlo in un cerchio completo.",
        "Strings.ja.resx":      "スティックで縁をなぞって到達可能な境界を測定し、真円に整形します。",
        "Strings.ko.resx":      "스틱으로 가장자리를 훑어 도달 가능한 경계를 측정한 뒤 완전한 원으로 변형합니다.",
        "Strings.nl.resx":      "Volg de rand met de stick om de bereikbare grens te meten en vervorm die tot een volledige cirkel.",
        "Strings.pt-BR.resx":   "Percorra a borda com o direcional para mapear o limite alcançável e remodele-o em um círculo completo.",
        "Strings.zh-Hans.resx": "用摇杆沿边缘转动以测量可达边界，然后将其重塑为完整圆形。",
    }),
    ("Pad_ResetStickBoundary", "Pad_Sticks_Boundary_Tooltip", {
        "Strings.resx":         "Reset boundary calibration",
        "Strings.de.resx":      "Begrenzungskalibrierung zurücksetzen",
        "Strings.es.resx":      "Restablecer calibración del límite",
        "Strings.fr.resx":      "Réinitialiser le calibrage de la limite",
        "Strings.it.resx":      "Reimposta la calibrazione del bordo",
        "Strings.ja.resx":      "境界調整をリセット",
        "Strings.ko.resx":      "경계 보정 재설정",
        "Strings.nl.resx":      "Grenskalibratie resetten",
        "Strings.pt-BR.resx":   "Redefinir calibração do limite",
        "Strings.zh-Hans.resx": "重置边界校准",
    }),
    ("Pad_Sticks_Section_CenterPoint", "Pad_CalibrateCenter", {
        "Strings.resx":         "Center Point",
        "Strings.de.resx":      "Mittelpunkt",
        "Strings.es.resx":      "Punto central",
        "Strings.fr.resx":      "Point central",
        "Strings.it.resx":      "Punto centrale",
        "Strings.ja.resx":      "中心点",
        "Strings.ko.resx":      "중심점",
        "Strings.nl.resx":      "Middelpunt",
        "Strings.pt-BR.resx":   "Ponto Central",
        "Strings.zh-Hans.resx": "中心点",
    }),
    ("Pad_Sticks_Section_DeadZone", "Pad_Sticks_Section_CenterPoint", {
        "Strings.resx":         "Deadzone",
        "Strings.de.resx":      "Deadzone",
        "Strings.es.resx":      "Zona muerta",
        "Strings.fr.resx":      "Zone morte",
        "Strings.it.resx":      "Zona morta",
        "Strings.ja.resx":      "デッドゾーン",
        "Strings.ko.resx":      "데드존",
        "Strings.nl.resx":      "Deadzone",
        "Strings.pt-BR.resx":   "Deadzone",
        "Strings.zh-Hans.resx": "死区",
    }),
    ("Pad_Sticks_Section_Range_Hint", "Pad_Sticks_Section_Range", {
        "Strings.resx":         "Calibrate Boundary measures the stick's true reach at every angle and reshapes it to a full circle, correcting the diagonals the four sliders below cannot. Use the sliders only to limit range on purpose.",
        "Strings.de.resx":      "Begrenzung kalibrieren misst die tatsächliche Reichweite des Sticks in jedem Winkel und formt sie zu einem vollen Kreis, samt der Diagonalen, die die vier Regler unten nicht erfassen. Die Regler nur zum bewussten Begrenzen der Reichweite verwenden.",
        "Strings.es.resx":      "Calibrar límite mide el alcance real del stick en cada ángulo y lo remodela a un círculo completo, corrigiendo las diagonales que los cuatro deslizadores de abajo no pueden. Usa los deslizadores solo para limitar el rango a propósito.",
        "Strings.fr.resx":      "Calibrer la limite mesure la portée réelle du stick sous chaque angle et la remodèle en cercle complet, corrigeant les diagonales que les quatre curseurs ci-dessous ne peuvent pas corriger. N'utilisez les curseurs que pour limiter la plage volontairement.",
        "Strings.it.resx":      "Calibra il bordo misura la portata reale dello stick a ogni angolo e la rimodella in un cerchio completo, correggendo le diagonali che i quattro cursori sotto non possono correggere. Usa i cursori solo per limitare il range di proposito.",
        "Strings.ja.resx":      "境界を調整は全角度でスティックの実際の可動域を測定し、完全な円に整形します。下の4つのスライダーでは補正できない斜め方向も補正されます。スライダーは意図的に範囲を制限したい場合にのみ使用してください。",
        "Strings.ko.resx":      "경계 보정은 모든 각도에서 스틱의 실제 도달 범위를 측정해 완전한 원으로 재구성하며, 아래 슬라이더 4개로는 보정할 수 없는 대각선 방향까지 보정합니다. 슬라이더는 의도적으로 범위를 제한할 때만 사용하세요.",
        "Strings.nl.resx":      "Grens kalibreren meet het werkelijke bereik van de stick onder elke hoek en hervormt het tot een volledige cirkel, inclusief de diagonalen die de vier schuifregelaars hieronder niet kunnen corrigeren. Gebruik de schuifregelaars alleen om het bereik bewust te beperken.",
        "Strings.pt-BR.resx":   "Calibrar limite mede o alcance real do analógico em todos os ângulos e o remodela em um círculo completo, corrigindo as diagonais que os quatro controles abaixo não conseguem. Use os controles apenas para limitar o alcance de propósito.",
        "Strings.zh-Hans.resx": "校准边界会测量摇杆在每个角度的实际可达范围，并将其重塑为完整的圆形，可修正下方四个滑块无法处理的对角线方向。滑块仅用于有意限制范围。",
    }),
    ("Pad_Sticks_Section_Range", "Pad_Sticks_Section_DeadZone", {
        "Strings.resx":         "Range",
        "Strings.de.resx":      "Bereich",
        "Strings.es.resx":      "Rango",
        "Strings.fr.resx":      "Plage",
        "Strings.it.resx":      "Range",
        "Strings.ja.resx":      "範囲",
        "Strings.ko.resx":      "범위",
        "Strings.nl.resx":      "Bereik",
        "Strings.pt-BR.resx":   "Alcance",
        "Strings.zh-Hans.resx": "范围",
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
