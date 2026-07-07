"""Add the #178 touch-spot + #177 macro trigger dropdown strings to all 10
locales, plus their Strings.Designer.cs property lines. Anchored inserts,
idempotent, BOM-preserving. Mirrors add_boundary_strings.py with the
Designer step of add_mouse_cursor_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

KEYS = [
    ("Mapping_TouchpadGesture_TouchLeft", "Mapping_TouchpadGesture_LongPress", {
        "Strings.resx":         "Left Touch",
        "Strings.de.resx":      "Linke Berührung",
        "Strings.es.resx":      "Toque izquierdo",
        "Strings.fr.resx":      "Toucher gauche",
        "Strings.it.resx":      "Tocco sinistro",
        "Strings.ja.resx":      "左タッチ",
        "Strings.ko.resx":      "왼쪽 터치",
        "Strings.nl.resx":      "Linker aanraking",
        "Strings.pt-BR.resx":   "Toque esquerdo",
        "Strings.zh-Hans.resx": "左侧触摸",
    }),
    ("Mapping_TouchpadGesture_TouchRight", "Mapping_TouchpadGesture_TouchLeft", {
        "Strings.resx":         "Right Touch",
        "Strings.de.resx":      "Rechte Berührung",
        "Strings.es.resx":      "Toque derecho",
        "Strings.fr.resx":      "Toucher droit",
        "Strings.it.resx":      "Tocco destro",
        "Strings.ja.resx":      "右タッチ",
        "Strings.ko.resx":      "오른쪽 터치",
        "Strings.nl.resx":      "Rechter aanraking",
        "Strings.pt-BR.resx":   "Toque direito",
        "Strings.zh-Hans.resx": "右侧触摸",
    }),
    ("Mapping_TouchpadGesture_TouchTop", "Mapping_TouchpadGesture_TouchRight", {
        "Strings.resx":         "Top Touch",
        "Strings.de.resx":      "Obere Berührung",
        "Strings.es.resx":      "Toque superior",
        "Strings.fr.resx":      "Toucher haut",
        "Strings.it.resx":      "Tocco superiore",
        "Strings.ja.resx":      "上タッチ",
        "Strings.ko.resx":      "위쪽 터치",
        "Strings.nl.resx":      "Bovenste aanraking",
        "Strings.pt-BR.resx":   "Toque superior",
        "Strings.zh-Hans.resx": "顶部触摸",
    }),
    ("Mapping_TouchpadGesture_TouchMulti", "Mapping_TouchpadGesture_TouchTop", {
        "Strings.resx":         "Multitouch",
        "Strings.de.resx":      "Multitouch",
        "Strings.es.resx":      "Multitáctil",
        "Strings.fr.resx":      "Multitouche",
        "Strings.it.resx":      "Multitocco",
        "Strings.ja.resx":      "マルチタッチ",
        "Strings.ko.resx":      "멀티터치",
        "Strings.nl.resx":      "Multitouch",
        "Strings.pt-BR.resx":   "Multitoque",
        "Strings.zh-Hans.resx": "多点触摸",
    }),
    ("Pad_Touchpad_TouchSpots", "Pad_Touchpad_RadialZones_Tooltip", {
        "Strings.resx":         "Touch Spots (Left / Right / Top / Multitouch)",
        "Strings.de.resx":      "Berührungszonen (links / rechts / oben / Multitouch)",
        "Strings.es.resx":      "Zonas táctiles (izquierda / derecha / arriba / multitáctil)",
        "Strings.fr.resx":      "Zones tactiles (gauche / droite / haut / multitouche)",
        "Strings.it.resx":      "Zone di tocco (sinistra / destra / alto / multitocco)",
        "Strings.ja.resx":      "タッチスポット（左 / 右 / 上 / マルチタッチ）",
        "Strings.ko.resx":      "터치 영역 (왼쪽 / 오른쪽 / 위 / 멀티터치)",
        "Strings.nl.resx":      "Aanraakzones (links / rechts / boven / multitouch)",
        "Strings.pt-BR.resx":   "Zonas de toque (esquerda / direita / topo / multitoque)",
        "Strings.zh-Hans.resx": "触摸区域（左 / 右 / 上 / 多点触摸）",
    }),
    ("Pad_Touchpad_TouchSpots_Tooltip", "Pad_Touchpad_TouchSpots", {
        "Strings.resx":         "Held buttons for where the touchpad is touched. One finger lands in Left, Right, or Top (the top quarter). Two or more fingers hold Multitouch. The left/right split sits at two fifths of the width, matching DS4Windows, and exactly one spot is held at a time.",
        "Strings.de.resx":      "Gehaltene Tasten je nach Berührungsort des Touchpads. Ein Finger fällt in Links, Rechts oder Oben (das obere Viertel). Zwei oder mehr Finger halten Multitouch. Die Links/Rechts-Grenze liegt wie in DS4Windows bei zwei Fünfteln der Breite, und es ist immer genau eine Zone aktiv.",
        "Strings.es.resx":      "Botones mantenidos según dónde se toca el panel táctil. Un dedo cae en izquierda, derecha o arriba (el cuarto superior). Dos o más dedos mantienen multitáctil. La división izquierda/derecha está a dos quintos del ancho, como en DS4Windows, y solo una zona está activa a la vez.",
        "Strings.fr.resx":      "Boutons maintenus selon l'endroit touché sur le pavé tactile. Un doigt tombe dans gauche, droite ou haut (le quart supérieur). Deux doigts ou plus maintiennent multitouche. La séparation gauche/droite se situe aux deux cinquièmes de la largeur, comme dans DS4Windows, et une seule zone est active à la fois.",
        "Strings.it.resx":      "Pulsanti mantenuti in base a dove viene toccato il touchpad. Un dito ricade in sinistra, destra o alto (il quarto superiore). Due o più dita mantengono multitocco. La divisione sinistra/destra si trova a due quinti della larghezza, come in DS4Windows, e una sola zona è attiva alla volta.",
        "Strings.ja.resx":      "タッチパッドのどこに触れているかで押し続けるボタンです。1本指は左・右・上（上部4分の1）のいずれかに入ります。2本以上の指はマルチタッチを保持します。左右の境界はDS4Windowsと同じく幅の5分の2にあり、同時に有効になるスポットは1つだけです。",
        "Strings.ko.resx":      "터치패드의 터치 위치에 따라 유지되는 버튼입니다. 한 손가락은 왼쪽, 오른쪽, 위(상단 4분의 1) 중 하나에 해당합니다. 두 손가락 이상은 멀티터치를 유지합니다. 좌우 경계는 DS4Windows와 같이 너비의 5분의 2 지점이며 한 번에 하나의 영역만 활성화됩니다.",
        "Strings.nl.resx":      "Vastgehouden knoppen voor waar het touchpad wordt aangeraakt. Eén vinger valt in links, rechts of boven (het bovenste kwart). Twee of meer vingers houden multitouch vast. De links/rechts-grens ligt op twee vijfde van de breedte, zoals in DS4Windows, en er is steeds precies één zone actief.",
        "Strings.pt-BR.resx":   "Botões mantidos conforme onde o touchpad é tocado. Um dedo cai em esquerda, direita ou topo (o quarto superior). Dois ou mais dedos mantêm multitoque. A divisão esquerda/direita fica a dois quintos da largura, como no DS4Windows, e apenas uma zona fica ativa por vez.",
        "Strings.zh-Hans.resx": "根据触摸板的触摸位置持续按住的按钮。单指落在左、右或上（顶部四分之一）区域。两指及以上按住多点触摸。左右分界线与 DS4Windows 相同，位于宽度的五分之二处，且同一时间只有一个区域生效。",
    }),
    ("Pad_ResetTouchpadTouchSpots", "Pad_ResetTouchpadRadialZones", {
        "Strings.resx":         "Reset Touch Spots",
        "Strings.de.resx":      "Berührungszonen zurücksetzen",
        "Strings.es.resx":      "Restablecer zonas táctiles",
        "Strings.fr.resx":      "Réinitialiser les zones tactiles",
        "Strings.it.resx":      "Reimposta le zone di tocco",
        "Strings.ja.resx":      "タッチスポットをリセット",
        "Strings.ko.resx":      "터치 영역 재설정",
        "Strings.nl.resx":      "Aanraakzones resetten",
        "Strings.pt-BR.resx":   "Redefinir zonas de toque",
        "Strings.zh-Hans.resx": "重置触摸区域",
    }),
    ("Macro_AddTriggerFromList", "Macro_RecordHint", {
        "Strings.resx":         "Add from List",
        "Strings.de.resx":      "Aus Liste hinzufügen",
        "Strings.es.resx":      "Añadir desde lista",
        "Strings.fr.resx":      "Ajouter depuis la liste",
        "Strings.it.resx":      "Aggiungi da elenco",
        "Strings.ja.resx":      "リストから追加",
        "Strings.ko.resx":      "목록에서 추가",
        "Strings.nl.resx":      "Toevoegen uit lijst",
        "Strings.pt-BR.resx":   "Adicionar da lista",
        "Strings.zh-Hans.resx": "从列表添加",
    }),
    ("Macro_AddTriggerFromList_Tooltip", "Macro_AddTriggerFromList", {
        "Strings.resx":         "Pick a trigger input from the slot's devices instead of recording it. Touchpad gestures enabled on the Touchpad tab appear here.",
        "Strings.de.resx":      "Wählt einen Auslöser aus den Geräten des Slots, statt ihn aufzuzeichnen. Auf dem Touchpad-Tab aktivierte Gesten erscheinen hier.",
        "Strings.es.resx":      "Elige una entrada de activación de los dispositivos de la ranura en lugar de grabarla. Los gestos del panel táctil activados en la pestaña del panel táctil aparecen aquí.",
        "Strings.fr.resx":      "Choisissez une entrée de déclenchement parmi les périphériques de l'emplacement au lieu de l'enregistrer. Les gestes du pavé tactile activés dans l'onglet Pavé tactile apparaissent ici.",
        "Strings.it.resx":      "Scegli un input di attivazione dai dispositivi dello slot invece di registrarlo. I gesti del touchpad abilitati nella scheda Touchpad compaiono qui.",
        "Strings.ja.resx":      "記録する代わりに、スロットのデバイスからトリガー入力を選択します。タッチパッドタブで有効にしたジェスチャーがここに表示されます。",
        "Strings.ko.resx":      "녹화하는 대신 슬롯의 장치에서 트리거 입력을 선택합니다. 터치패드 탭에서 활성화한 제스처가 여기에 표시됩니다.",
        "Strings.nl.resx":      "Kies een triggerinvoer uit de apparaten van het slot in plaats van deze op te nemen. Op het tabblad Touchpad ingeschakelde gebaren verschijnen hier.",
        "Strings.pt-BR.resx":   "Escolha uma entrada de acionamento dos dispositivos do slot em vez de gravá-la. Os gestos do touchpad ativados na guia Touchpad aparecem aqui.",
        "Strings.zh-Hans.resx": "从该槽位的设备中选择触发输入，而无需录制。在触摸板选项卡中启用的手势会显示在这里。",
    }),
]

DESIGNER = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings" / "Strings.Designer.cs"
DESIGNER_ANCHORS = {
    "Mapping_TouchpadGesture_TouchLeft":  "Mapping_TouchpadGesture_LongPress",
    "Mapping_TouchpadGesture_TouchRight": "Mapping_TouchpadGesture_TouchLeft",
    "Mapping_TouchpadGesture_TouchTop":   "Mapping_TouchpadGesture_TouchRight",
    "Mapping_TouchpadGesture_TouchMulti": "Mapping_TouchpadGesture_TouchTop",
    "Pad_Touchpad_TouchSpots":            "Pad_Touchpad_RadialZones_Tooltip",
    "Pad_Touchpad_TouchSpots_Tooltip":    "Pad_Touchpad_TouchSpots",
    "Pad_ResetTouchpadTouchSpots":        "Pad_ResetTouchpadRadialZones",
    "Macro_AddTriggerFromList":           "Macro_RecordHint",
    "Macro_AddTriggerFromList_Tooltip":   "Macro_AddTriggerFromList",
}


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

# Designer property lines, anchored after each key's Designer anchor.
raw = DESIGNER.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
for key, anchor in DESIGNER_ANCHORS.items():
    if f'public string {key} =>' in text:
        print(f"OK   Designer  {key} (already present)")
        continue
    needle = f'    public string {anchor} => Get("{anchor}");'
    idx = text.find(needle)
    if idx < 0:
        print(f"FAIL Designer: anchor {anchor} not found for {key}")
        continue
    insert = f'\n    public string {key} => Get("{key}");'
    end = idx + len(needle)
    text = text[:end] + insert + text[end:]
    print(f"OK   Designer  {key} (inserted)")
out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
DESIGNER.write_bytes(out)
