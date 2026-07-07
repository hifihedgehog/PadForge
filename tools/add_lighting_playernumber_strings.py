"""#191 follow-up (true Off vs PlayerNumber default): add the two
PlayerNumber option labels, strip the now-false "game owns the
lightbar" parenthetical from the Off label (that semantic moved to
PlayerNumber), and rewrite the Lighting tab idle hint to name the new
default and the deliberate Off. Anchored inserts + keyed replaces,
idempotent, BOM-preserving. Mirrors add_player_led_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# key -> anchor key (insert directly after anchor's data line) -> per-file values
INSERTS = [
    ("Pad_Lighting_Mode_PlayerNumber", "Pad_Lighting_Mode_Off", {
        "Strings.resx":         "Player Number (Default)",
        "Strings.de.resx":      "Spielernummer (Standard)",
        "Strings.es.resx":      "Número de jugador (predeterminado)",
        "Strings.fr.resx":      "Numéro de joueur (par défaut)",
        "Strings.it.resx":      "Numero giocatore (predefinito)",
        "Strings.ja.resx":      "プレイヤー番号 (既定)",
        "Strings.ko.resx":      "플레이어 번호 (기본값)",
        "Strings.nl.resx":      "Spelersnummer (standaard)",
        "Strings.pt-BR.resx":   "Número do Jogador (Padrão)",
        "Strings.zh-Hans.resx": "玩家编号(默认)",
    }),
    ("Pad_Lighting_PlayerLed_PlayerNumber", "Pad_Lighting_PlayerLed_Off", {
        "Strings.resx":         "Player Number (Default)",
        "Strings.de.resx":      "Spielernummer (Standard)",
        "Strings.es.resx":      "Número de jugador (predeterminado)",
        "Strings.fr.resx":      "Numéro de joueur (par défaut)",
        "Strings.it.resx":      "Numero giocatore (predefinito)",
        "Strings.ja.resx":      "プレイヤー番号 (既定)",
        "Strings.ko.resx":      "플레이어 번호 (기본값)",
        "Strings.nl.resx":      "Spelersnummer (standaard)",
        "Strings.pt-BR.resx":   "Número do Jogador (Padrão)",
        "Strings.zh-Hans.resx": "玩家编号(默认)",
    }),
]

REPLACES = [
    # The old parenthetical described the pre-#191 Off ("game owns the
    # lightbar"); game ownership now rides the PlayerNumber default and
    # Off is a deliberate hard-off.
    ("Pad_Lighting_Mode_Off", {
        "Strings.resx":         "Off",
        "Strings.de.resx":      "Aus",
        "Strings.es.resx":      "Desactivado",
        "Strings.fr.resx":      "Désactivé",
        "Strings.it.resx":      "Disattivato",
        "Strings.ja.resx":      "オフ",
        "Strings.ko.resx":      "끔",
        "Strings.nl.resx":      "Uit",
        "Strings.pt-BR.resx":   "Desativado",
        "Strings.zh-Hans.resx": "关闭",
    }),
    ("Pad_Lighting_PlayerIdle_Hint", {
        "Strings.resx":         "While set to Player Number, the pad idles showing its virtual controller's player identity: the Sony player color on the lightbar (1 blue, 2 red, 3 green, 4 pink) and the matching player pips on a DualSense. Every device on the same virtual controller shows the same number. Games take over whenever they write lighting, anything you set here always wins, and Off turns a light fully dark.",
        "Strings.de.resx":      "Solange Spielernummer gewählt ist, zeigt das Pad im Leerlauf die Spieler-Identität seines virtuellen Controllers: die Sony-Spielerfarbe auf der Lightbar (1 Blau, 2 Rot, 3 Grün, 4 Pink) und beim DualSense die passenden Spieler-LEDs. Alle Geräte am selben virtuellen Controller zeigen dieselbe Nummer. Spiele übernehmen, sobald sie Beleuchtung schreiben, alles, was hier eingestellt ist, hat immer Vorrang, und Aus macht das jeweilige Licht vollständig dunkel.",
        "Strings.es.resx":      "Mientras esté en Número de jugador, el mando en reposo muestra la identidad de jugador de su mando virtual: el color de jugador de Sony en la barra de luz (1 azul, 2 rojo, 3 verde, 4 rosa) y, en un DualSense, los LED de jugador correspondientes. Todos los dispositivos del mismo mando virtual muestran el mismo número. Los juegos toman el control cuando escriben iluminación, lo que configures aquí siempre gana, y las opciones de apagado dejan esa luz completamente a oscuras.",
        "Strings.fr.resx":      "Tant que Numéro de joueur est sélectionné, la manette au repos affiche l'identité de joueur de sa manette virtuelle : la couleur de joueur Sony sur la barre lumineuse (1 bleu, 2 rouge, 3 vert, 4 rose) et, sur une DualSense, les LED de joueur correspondantes. Tous les périphériques d'une même manette virtuelle affichent le même numéro. Les jeux prennent la main dès qu'ils écrivent l'éclairage, ce que vous réglez ici gagne toujours, et les options d'extinction éteignent complètement la lumière.",
        "Strings.it.resx":      "Finché è selezionato Numero giocatore, il pad a riposo mostra l'identità giocatore del suo controller virtuale: il colore giocatore Sony sulla lightbar (1 blu, 2 rosso, 3 verde, 4 rosa) e, su un DualSense, i LED giocatore corrispondenti. Tutti i dispositivi dello stesso controller virtuale mostrano lo stesso numero. I giochi prendono il controllo quando scrivono l'illuminazione, ciò che imposti qui vince sempre, e le opzioni di spegnimento lasciano quella luce completamente spenta.",
        "Strings.ja.resx":      "プレイヤー番号が選択されている間、パッドは待機時に仮想コントローラーのプレイヤー識別を表示します。ライトバーにはソニーのプレイヤーカラー（1 青、2 赤、3 緑、4 ピンク）、DualSense では対応するプレイヤーランプも点灯します。同じ仮想コントローラー上のすべてのデバイスが同じ番号を表示します。ゲームがライティングを書き込むとゲームが優先され、ここで設定した内容は常に最優先されます。オフを選ぶとそのライトは完全に消灯します。",
        "Strings.ko.resx":      "플레이어 번호가 선택된 동안 패드는 대기 상태에서 가상 컨트롤러의 플레이어 식별을 표시합니다. 라이트바에는 소니 플레이어 색상(1 파랑, 2 빨강, 3 초록, 4 분홍)이, DualSense에서는 해당 플레이어 LED가 켜집니다. 같은 가상 컨트롤러의 모든 장치가 같은 번호를 표시합니다. 게임이 조명을 쓰면 게임이 우선하고 여기서 설정한 내용은 항상 우선하며, 끔을 선택하면 해당 조명이 완전히 꺼집니다.",
        "Strings.nl.resx":      "Zolang Spelersnummer is gekozen, toont de pad in rust de spelersidentiteit van zijn virtuele controller: de Sony-spelerkleur op de lightbar (1 blauw, 2 rood, 3 groen, 4 roze) en op een DualSense de bijbehorende speler-leds. Alle apparaten op dezelfde virtuele controller tonen hetzelfde nummer. Games nemen het over zodra ze verlichting schrijven, wat je hier instelt wint altijd, en Uit maakt dat licht volledig donker.",
        "Strings.pt-BR.resx":   "Enquanto Número do Jogador estiver selecionado, o controle em repouso mostra a identidade de jogador do seu controle virtual: a cor de jogador da Sony na barra de luz (1 azul, 2 vermelho, 3 verde, 4 rosa) e, em um DualSense, os LEDs de jogador correspondentes. Todos os dispositivos do mesmo controle virtual mostram o mesmo número. Os jogos assumem quando escrevem iluminação, o que você definir aqui sempre vence, e as opções de desligar apagam essa luz por completo.",
        "Strings.zh-Hans.resx": "选择玩家编号时，手柄空闲状态会显示其虚拟控制器的玩家身份：灯条显示索尼玩家颜色（1 蓝、2 红、3 绿、4 粉），DualSense 还会点亮对应的玩家指示灯。同一虚拟控制器下的所有设备显示相同编号。游戏写入灯光时由游戏接管，此处的设置始终优先，选择关闭则该灯完全熄灭。",
    }),
]

DESIGNER = ROOT / "Strings.Designer.cs"
DESIGNER_ANCHORS = {
    "Pad_Lighting_Mode_PlayerNumber": "Pad_Lighting_Mode_Off",
    "Pad_Lighting_PlayerLed_PlayerNumber": "Pad_Lighting_PlayerLed_Off",
}


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def load(p):
    raw = p.read_bytes()
    return raw.startswith(b"\xef\xbb\xbf"), raw.decode("utf-8-sig")


def store(p, bom, text):
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))


for key, values in REPLACES:
    for fname, value in values.items():
        p = ROOT / fname
        bom, text = load(p)
        pat = re.compile(
            rf'(<data name="{re.escape(key)}" xml:space="preserve"><value>)[^<]*(</value></data>)')
        m = pat.search(text)
        if not m:
            print(f"FAIL {fname}: {key} not found for replace")
            continue
        new = pat.sub(lambda mm: mm.group(1) + xml_escape(value) + mm.group(2), text, count=1)
        if new == text:
            print(f"OK   {fname}  {key} (already current)")
        else:
            store(p, bom, new)
            print(f"OK   {fname}  {key} (replaced)")

for key, anchor, values in INSERTS:
    for fname, value in values.items():
        p = ROOT / fname
        bom, text = load(p)
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
        store(p, bom, text[:m.end()] + line + text[m.end():])
        print(f"OK   {fname}  {key} (inserted)")

bom, text = load(DESIGNER)
changed = False
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
    changed = True
    print(f"OK   Designer  {key} (inserted)")
if changed:
    store(DESIGNER, bom, text)
