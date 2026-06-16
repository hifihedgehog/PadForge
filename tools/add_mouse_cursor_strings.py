"""Add the issue #107 mouse-source strings (renamed Speed/Scroll axes, new
Position axes, per-source Cursor Sensitivity label + tooltip) to the neutral
Strings.resx, all 9 locale resx files, and Strings.Designer.cs.

Anchored after Mapping_GyroSensitivity_Tooltip, present in every locale.
Idempotent: keys already present are skipped.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"
DESIGNER = ROOT / "Strings.Designer.cs"
ANCHOR = "Mapping_GyroSensitivity_Tooltip"

KEYS = [
    "Mapping_MouseSpeedX", "Mapping_MouseSpeedY", "Mapping_MouseScroll",
    "Mapping_MousePositionX", "Mapping_MousePositionY",
    "Mapping_MouseCursorSensitivity", "Mapping_MouseCursorSensitivity_Tooltip",
]

TIP_EN = ("Per-source cursor sensitivity. 1.0 reaches full stick deflection at 10% "
          "of screen width from center. Values above 1.0 reach full deflection with "
          "less cursor travel. Values below 1.0 need more.")

VALUES = {
    "Strings.resx": ["Mouse Speed X", "Mouse Speed Y", "Mouse Scroll",
                     "Mouse Position X", "Mouse Position Y", "Sensitivity", TIP_EN],
    "Strings.de.resx": ["Maus Geschwindigkeit X", "Maus Geschwindigkeit Y", "Maus Scrollen",
                        "Maus Position X", "Maus Position Y", "Empfindlichkeit",
                        "Empfindlichkeit des Cursors pro Quelle. 1.0 erreicht volle Stick-Auslenkung bei 10 % der Bildschirmbreite ab der Mitte. Werte über 1.0 erreichen die volle Auslenkung mit weniger Cursorweg. Werte unter 1.0 brauchen mehr."],
    "Strings.es.resx": ["Velocidad ratón X", "Velocidad ratón Y", "Desplazamiento ratón",
                        "Posición ratón X", "Posición ratón Y", "Sensibilidad",
                        "Sensibilidad del cursor por fuente. 1.0 alcanza la deflexión completa del stick al 10 % del ancho de pantalla desde el centro. Valores superiores a 1.0 alcanzan la deflexión completa con menos recorrido del cursor. Valores inferiores a 1.0 necesitan más."],
    "Strings.fr.resx": ["Vitesse souris X", "Vitesse souris Y", "Défilement souris",
                        "Position souris X", "Position souris Y", "Sensibilité",
                        "Sensibilité du curseur par source. 1.0 atteint la déflexion complète du stick à 10 % de la largeur de l'écran depuis le centre. Les valeurs supérieures à 1.0 atteignent la déflexion complète avec moins de déplacement du curseur. Les valeurs inférieures à 1.0 en demandent plus."],
    "Strings.it.resx": ["Velocità mouse X", "Velocità mouse Y", "Scorrimento mouse",
                        "Posizione mouse X", "Posizione mouse Y", "Sensibilità",
                        "Sensibilità del cursore per sorgente. 1.0 raggiunge la deflessione completa dello stick al 10% della larghezza dello schermo dal centro. Valori superiori a 1.0 raggiungono la deflessione completa con meno movimento del cursore. Valori inferiori a 1.0 ne richiedono di più."],
    "Strings.ja.resx": ["マウス速度 X", "マウス速度 Y", "マウススクロール",
                        "マウス位置 X", "マウス位置 Y", "感度",
                        "ソースごとのカーソル感度。1.0 で中心から画面幅の 10% でスティックが最大まで倒れます。1.0 より大きいと少ないカーソル移動で最大に達し、1.0 より小さいとより多くの移動が必要です。"],
    "Strings.ko.resx": ["마우스 속도 X", "마우스 속도 Y", "마우스 스크롤",
                        "마우스 위치 X", "마우스 위치 Y", "감도",
                        "소스별 커서 감도입니다. 1.0이면 중앙에서 화면 너비의 10% 지점에서 스틱이 최대로 기울어집니다. 1.0보다 크면 더 적은 커서 이동으로 최대에 도달하고, 1.0보다 작으면 더 많이 움직여야 합니다."],
    "Strings.nl.resx": ["Muis snelheid X", "Muis snelheid Y", "Muis scrollen",
                        "Muis positie X", "Muis positie Y", "Gevoeligheid",
                        "Cursorgevoeligheid per bron. 1.0 bereikt volledige stickuitslag bij 10% van de schermbreedte vanaf het midden. Waarden boven 1.0 bereiken volledige uitslag met minder cursorbeweging. Waarden onder 1.0 hebben meer nodig."],
    "Strings.pt-BR.resx": ["Velocidade do Mouse X", "Velocidade do Mouse Y", "Rolagem do Mouse",
                           "Posição do Mouse X", "Posição do Mouse Y", "Sensibilidade",
                           "Sensibilidade do cursor por fonte. 1.0 atinge a deflexão total do direcional a 10% da largura da tela a partir do centro. Valores acima de 1.0 atingem a deflexão total com menos movimento do cursor. Valores abaixo de 1.0 precisam de mais."],
    "Strings.zh-Hans.resx": ["鼠标速度 X", "鼠标速度 Y", "鼠标滚动",
                             "鼠标位置 X", "鼠标位置 Y", "灵敏度",
                             "每个来源的光标灵敏度。1.0 时，从中心移动屏幕宽度的 10% 即可使摇杆完全偏转。大于 1.0 时用更少的光标移动即可完全偏转，小于 1.0 时需要更多。"],
}

import re
def read_text(p):
    raw = p.read_bytes(); return raw.decode("utf-8-sig"), raw.startswith(b"\xef\xbb\xbf")
def write_text(p, text, bom):
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
def xesc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

for fname, vals in VALUES.items():
    p = ROOT / fname
    text, bom = read_text(p)
    m = re.search(r'(  <data name="' + re.escape(ANCHOR) + r'"[^>]*><value>.*?</value></data>\s*\n)', text, re.S)
    if not m:
        print(f"FAIL {fname}: anchor not found"); continue
    lines = []
    for k, v in zip(KEYS, vals):
        if f'<data name="{k}"' in text: continue
        lines.append(f'  <data name="{k}" xml:space="preserve"><value>{xesc(v)}</value></data>\n')
    if lines:
        text = text[:m.end()] + "".join(lines) + text[m.end():]
        write_text(p, text, bom)
    print(f"OK   {fname}  (+{len(lines)})")

# Designer
dtext, dbom = read_text(DESIGNER)
anchor_line = f'    public string {ANCHOR} => Get("{ANCHOR}");'
idx = dtext.find(anchor_line)
if idx < 0:
    print("FAIL Designer anchor")
else:
    insert_at = idx + len(anchor_line)
    props = "".join(f'\r\n    public string {k} => Get("{k}");' for k in KEYS if f'Get("{k}")' not in dtext)
    dtext = dtext[:insert_at] + props + dtext[insert_at:]
    write_text(DESIGNER, dtext, dbom)
    print("OK   Designer")
