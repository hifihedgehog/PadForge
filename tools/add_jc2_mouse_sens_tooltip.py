"""Add Mapping_MouseMotionSensitivity_Tooltip (#154) to all 10 locales,
anchored after Mapping_IrPointerSensitivity_Tooltip. Idempotent,
BOM-preserving. Mirrors add_jc2_mouse_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

KEY = "Mapping_MouseMotionSensitivity_Tooltip"
ANCHOR = "Mapping_IrPointerSensitivity_Tooltip"
VALUES = {
    "Strings.resx":         "Sensitivity of the mouse motion per source. 1.0 reaches full deflection at the same speed a real mouse would, values above 1.0 need less motion, values below need more.",
    "Strings.de.resx":      "Empfindlichkeit der Mausbewegung pro Quelle. 1,0 erreicht die volle Auslenkung bei derselben Geschwindigkeit wie eine echte Maus, Werte über 1,0 brauchen weniger Bewegung, Werte darunter mehr.",
    "Strings.es.resx":      "Sensibilidad del movimiento del ratón por origen. 1.0 alcanza la deflexión completa a la misma velocidad que un ratón real, los valores superiores a 1.0 necesitan menos movimiento y los inferiores más.",
    "Strings.fr.resx":      "Sensibilité du mouvement de la souris par source. 1.0 atteint la déflexion complète à la même vitesse qu'une vraie souris, les valeurs supérieures à 1.0 demandent moins de mouvement, les valeurs inférieures en demandent plus.",
    "Strings.it.resx":      "Sensibilità del movimento del mouse per origine. 1.0 raggiunge la deflessione completa alla stessa velocità di un mouse reale, i valori sopra 1.0 richiedono meno movimento, quelli sotto di più.",
    "Strings.ja.resx":      "ソースごとのマウス移動の感度。1.0 は実際のマウスと同じ速度で最大偏向に達します。1.0 より大きい値は少ない動きで、より小さい値はより多くの動きで最大に達します。",
    "Strings.ko.resx":      "소스별 마우스 이동 감도입니다. 1.0은 실제 마우스와 같은 속도에서 최대 편향에 도달합니다. 1.0보다 크면 더 적은 움직임으로, 작으면 더 많은 움직임이 필요합니다.",
    "Strings.nl.resx":      "Gevoeligheid van de muisbeweging per bron. 1.0 bereikt volledige uitslag bij dezelfde snelheid als een echte muis, waarden boven 1.0 hebben minder beweging nodig, waarden eronder meer.",
    "Strings.pt-BR.resx":   "Sensibilidade do movimento do mouse por origem. 1.0 atinge a deflexão total na mesma velocidade de um mouse real, valores acima de 1.0 exigem menos movimento e valores abaixo exigem mais.",
    "Strings.zh-Hans.resx": "每个来源的鼠标移动灵敏度。1.0 在与真实鼠标相同的速度下达到最大偏转，大于 1.0 的值需要更少的移动，小于 1.0 的值需要更多。",
}


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
