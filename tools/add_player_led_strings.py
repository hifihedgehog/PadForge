"""Add the #191 player-identity idle-floor string to all 10 locales plus
its Strings.Designer.cs property line. Anchored insert, idempotent,
BOM-preserving. Mirrors add_touch_spot_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

KEYS = [
    ("Pad_Lighting_PlayerIdle_Hint", "Pad_Lighting_Subtitle", {
        "Strings.resx":         "While the settings here are Off, the pad idles showing its virtual controller's player identity: the Sony player color on the lightbar (1 blue, 2 red, 3 green, 4 pink) and the matching player pips on a DualSense. Every device on the same virtual controller shows the same number. Games take over whenever they write lighting, and anything you set here always wins.",
        "Strings.de.resx":      "Solange die Einstellungen hier auf Aus stehen, zeigt das Pad im Leerlauf die Spieler-Identität seines virtuellen Controllers: die Sony-Spielerfarbe auf der Lightbar (1 Blau, 2 Rot, 3 Grün, 4 Pink) und beim DualSense die passenden Spieler-LEDs. Alle Geräte am selben virtuellen Controller zeigen dieselbe Nummer. Spiele übernehmen, sobald sie Beleuchtung schreiben, und alles, was hier eingestellt ist, hat immer Vorrang.",
        "Strings.es.resx":      "Mientras los ajustes de aquí estén en Apagado, el mando en reposo muestra la identidad de jugador de su mando virtual: el color de jugador de Sony en la barra de luz (1 azul, 2 rojo, 3 verde, 4 rosa) y, en un DualSense, los LED de jugador correspondientes. Todos los dispositivos del mismo mando virtual muestran el mismo número. Los juegos toman el control cuando escriben iluminación, y lo que configures aquí siempre gana.",
        "Strings.fr.resx":      "Tant que les réglages ici sont sur Désactivé, la manette au repos affiche l'identité de joueur de sa manette virtuelle : la couleur de joueur Sony sur la barre lumineuse (1 bleu, 2 rouge, 3 vert, 4 rose) et, sur une DualSense, les LED de joueur correspondantes. Tous les périphériques d'une même manette virtuelle affichent le même numéro. Les jeux prennent la main dès qu'ils écrivent l'éclairage, et ce que vous réglez ici gagne toujours.",
        "Strings.it.resx":      "Finché le impostazioni qui sono su Off, il pad a riposo mostra l'identità giocatore del suo controller virtuale: il colore giocatore Sony sulla lightbar (1 blu, 2 rosso, 3 verde, 4 rosa) e, su un DualSense, i LED giocatore corrispondenti. Tutti i dispositivi dello stesso controller virtuale mostrano lo stesso numero. I giochi prendono il controllo quando scrivono l'illuminazione, e ciò che imposti qui vince sempre.",
        "Strings.ja.resx":      "ここの設定がオフの間、パッドは待機時に仮想コントローラーのプレイヤー識別を表示します。ライトバーにはソニーのプレイヤーカラー（1 青、2 赤、3 緑、4 ピンク）、DualSense では対応するプレイヤーランプも点灯します。同じ仮想コントローラー上のすべてのデバイスが同じ番号を表示します。ゲームがライティングを書き込むとゲームが優先され、ここで設定した内容は常に最優先されます。",
        "Strings.ko.resx":      "여기 설정이 꺼짐인 동안 패드는 대기 상태에서 가상 컨트롤러의 플레이어 식별을 표시합니다. 라이트바에는 소니 플레이어 색상(1 파랑, 2 빨강, 3 초록, 4 분홍)이, DualSense에서는 해당 플레이어 LED가 켜집니다. 같은 가상 컨트롤러의 모든 장치가 같은 번호를 표시합니다. 게임이 조명을 쓰면 게임이 우선하며, 여기서 설정한 내용은 항상 우선합니다.",
        "Strings.nl.resx":      "Zolang de instellingen hier op Uit staan, toont de pad in rust de spelersidentiteit van zijn virtuele controller: de Sony-spelerkleur op de lightbar (1 blauw, 2 rood, 3 groen, 4 roze) en op een DualSense de bijbehorende speler-leds. Alle apparaten op dezelfde virtuele controller tonen hetzelfde nummer. Games nemen het over zodra ze verlichting schrijven, en wat je hier instelt wint altijd.",
        "Strings.pt-BR.resx":   "Enquanto as configurações aqui estiverem em Desligado, o controle em repouso mostra a identidade de jogador do seu controle virtual: a cor de jogador da Sony na barra de luz (1 azul, 2 vermelho, 3 verde, 4 rosa) e, em um DualSense, os LEDs de jogador correspondentes. Todos os dispositivos do mesmo controle virtual mostram o mesmo número. Os jogos assumem quando escrevem iluminação, e o que você definir aqui sempre vence.",
        "Strings.zh-Hans.resx": "当此处的设置为关闭时，手柄空闲状态会显示其虚拟控制器的玩家身份：灯条显示索尼玩家颜色（1 蓝、2 红、3 绿、4 粉），DualSense 还会点亮对应的玩家指示灯。同一虚拟控制器下的所有设备显示相同编号。游戏写入灯光时由游戏接管，而此处的设置始终优先。",
    }),
]

DESIGNER = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings" / "Strings.Designer.cs"
DESIGNER_ANCHORS = {
    "Pad_Lighting_PlayerIdle_Hint": "Pad_Lighting_Subtitle",
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
