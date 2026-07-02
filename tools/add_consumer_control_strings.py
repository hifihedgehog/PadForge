"""Add the #168 Consumer Control strings (DeviceType_ConsumerControl +
36 DevObj_Consumer* button names) to all 10 locales. Idempotent,
BOM-preserving. Mirrors add_jc2_mouse_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "DeviceType_ConsumerControl": ["Consumer Control", "Consumer Control", "Control multimedia", "Contrôle multimédia", "Controllo multimediale", "コンシューマーコントロール", "소비자 컨트롤", "Consumer Control", "Controle de Consumidor", "消费者控制"],
    "DevObj_ConsumerPower": ["Power", "Ein/Aus", "Encendido", "Alimentation", "Accensione", "電源", "전원", "Aan/uit", "Liga/Desliga", "电源"],
    "DevObj_ConsumerMenu": ["Menu", "Menü", "Menú", "Menu", "Menu", "メニュー", "메뉴", "Menu", "Menu", "菜单"],
    "DevObj_ConsumerOk": ["OK", "OK", "OK", "OK", "OK", "OK", "확인", "OK", "OK", "确定"],
    "DevObj_ConsumerMenuUp": ["Menu Up", "Menü hoch", "Menú arriba", "Menu haut", "Menu su", "メニュー上", "메뉴 위", "Menu omhoog", "Menu para Cima", "菜单上"],
    "DevObj_ConsumerMenuDown": ["Menu Down", "Menü runter", "Menú abajo", "Menu bas", "Menu giù", "メニュー下", "메뉴 아래", "Menu omlaag", "Menu para Baixo", "菜单下"],
    "DevObj_ConsumerMenuLeft": ["Menu Left", "Menü links", "Menú izquierda", "Menu gauche", "Menu sinistra", "メニュー左", "메뉴 왼쪽", "Menu links", "Menu à Esquerda", "菜单左"],
    "DevObj_ConsumerMenuRight": ["Menu Right", "Menü rechts", "Menú derecha", "Menu droite", "Menu destra", "メニュー右", "메뉴 오른쪽", "Menu rechts", "Menu à Direita", "菜单右"],
    "DevObj_ConsumerMenuEscape": ["Menu Escape", "Menü verlassen", "Salir del menú", "Quitter le menu", "Esci dal menu", "メニュー終了", "메뉴 나가기", "Menu verlaten", "Sair do Menu", "退出菜单"],
    "DevObj_ConsumerPlay": ["Play", "Wiedergabe", "Reproducir", "Lecture", "Riproduci", "再生", "재생", "Afspelen", "Reproduzir", "播放"],
    "DevObj_ConsumerPause": ["Pause", "Pause", "Pausa", "Pause", "Pausa", "一時停止", "일시 정지", "Pauze", "Pausar", "暂停"],
    "DevObj_ConsumerRecord": ["Record", "Aufnahme", "Grabar", "Enregistrer", "Registra", "録画", "녹화", "Opnemen", "Gravar", "录制"],
    "DevObj_ConsumerFastForward": ["Fast Forward", "Vorspulen", "Avance rápido", "Avance rapide", "Avanti veloce", "早送り", "빨리 감기", "Vooruitspoelen", "Avanço Rápido", "快进"],
    "DevObj_ConsumerRewind": ["Rewind", "Zurückspulen", "Rebobinar", "Retour rapide", "Riavvolgi", "巻き戻し", "되감기", "Terugspoelen", "Retroceder", "快退"],
    "DevObj_ConsumerNextTrack": ["Next Track", "Nächster Titel", "Pista siguiente", "Piste suivante", "Traccia successiva", "次のトラック", "다음 트랙", "Volgend nummer", "Próxima Faixa", "下一曲"],
    "DevObj_ConsumerPreviousTrack": ["Previous Track", "Vorheriger Titel", "Pista anterior", "Piste précédente", "Traccia precedente", "前のトラック", "이전 트랙", "Vorig nummer", "Faixa Anterior", "上一曲"],
    "DevObj_ConsumerMediaStop": ["Stop", "Stopp", "Detener", "Arrêt", "Interrompi", "停止", "정지", "Stoppen", "Parar", "停止"],
    "DevObj_ConsumerEject": ["Eject", "Auswerfen", "Expulsar", "Éjecter", "Espelli", "取り出し", "꺼내기", "Uitwerpen", "Ejetar", "弹出"],
    "DevObj_ConsumerPlayPause": ["Play/Pause", "Wiedergabe/Pause", "Reproducir/Pausa", "Lecture/Pause", "Riproduci/Pausa", "再生/一時停止", "재생/일시 정지", "Afspelen/Pauze", "Reproduzir/Pausar", "播放/暂停"],
    "DevObj_ConsumerVoiceCommand": ["Voice Command", "Sprachbefehl", "Comando de voz", "Commande vocale", "Comando vocale", "音声コマンド", "음성 명령", "Spraakopdracht", "Comando de Voz", "语音命令"],
    "DevObj_ConsumerMute": ["Mute", "Stumm", "Silencio", "Muet", "Muto", "ミュート", "음소거", "Dempen", "Mudo", "静音"],
    "DevObj_ConsumerVolumeUp": ["Volume Up", "Lauter", "Subir volumen", "Volume +", "Volume su", "音量を上げる", "볼륨 높이기", "Volume omhoog", "Aumentar Volume", "音量增大"],
    "DevObj_ConsumerVolumeDown": ["Volume Down", "Leiser", "Bajar volumen", "Volume -", "Volume giù", "音量を下げる", "볼륨 낮추기", "Volume omlaag", "Diminuir Volume", "音量减小"],
    "DevObj_ConsumerQuit": ["Quit", "Beenden", "Salir", "Quitter", "Esci", "終了", "종료", "Afsluiten", "Sair", "退出"],
    "DevObj_ConsumerChannelUp": ["Channel Up", "Kanal hoch", "Canal siguiente", "Chaîne suivante", "Canale su", "チャンネル上", "채널 위", "Kanaal omhoog", "Canal Acima", "频道上"],
    "DevObj_ConsumerChannelDown": ["Channel Down", "Kanal runter", "Canal anterior", "Chaîne précédente", "Canale giù", "チャンネル下", "채널 아래", "Kanaal omlaag", "Canal Abaixo", "频道下"],
    "DevObj_ConsumerMediaPlayer": ["Media Player", "Medienplayer", "Reproductor multimedia", "Lecteur multimédia", "Lettore multimediale", "メディアプレーヤー", "미디어 플레이어", "Mediaspeler", "Reprodutor de Mídia", "媒体播放器"],
    "DevObj_ConsumerEmail": ["Email", "E-Mail", "Correo", "E-mail", "E-mail", "メール", "이메일", "E-mail", "E-mail", "电子邮件"],
    "DevObj_ConsumerCalculator": ["Calculator", "Rechner", "Calculadora", "Calculatrice", "Calcolatrice", "電卓", "계산기", "Rekenmachine", "Calculadora", "计算器"],
    "DevObj_ConsumerFileBrowser": ["File Browser", "Dateibrowser", "Explorador de archivos", "Explorateur de fichiers", "Esplora file", "ファイルブラウザー", "파일 탐색기", "Bestandsbeheer", "Explorador de Arquivos", "文件浏览器"],
    "DevObj_ConsumerBrowserSearch": ["Search", "Suche", "Buscar", "Rechercher", "Cerca", "検索", "검색", "Zoeken", "Pesquisar", "搜索"],
    "DevObj_ConsumerBrowserHome": ["Browser Home", "Browser-Startseite", "Inicio del navegador", "Accueil du navigateur", "Home del browser", "ブラウザーホーム", "브라우저 홈", "Browser-startpagina", "Página Inicial do Navegador", "浏览器主页"],
    "DevObj_ConsumerBrowserBack": ["Browser Back", "Browser zurück", "Atrás del navegador", "Navigateur précédent", "Browser indietro", "ブラウザー戻る", "브라우저 뒤로", "Browser terug", "Voltar do Navegador", "浏览器后退"],
    "DevObj_ConsumerBrowserForward": ["Browser Forward", "Browser vor", "Adelante del navegador", "Navigateur suivant", "Browser avanti", "ブラウザー進む", "브라우저 앞으로", "Browser vooruit", "Avançar do Navegador", "浏览器前进"],
    "DevObj_ConsumerBrowserStop": ["Browser Stop", "Browser stopp", "Detener navegador", "Arrêt du navigateur", "Browser interrompi", "ブラウザー停止", "브라우저 정지", "Browser stoppen", "Parar Navegador", "浏览器停止"],
    "DevObj_ConsumerBrowserRefresh": ["Refresh", "Aktualisieren", "Actualizar", "Actualiser", "Aggiorna", "更新", "새로 고침", "Vernieuwen", "Atualizar", "刷新"],
    "DevObj_ConsumerBrowserBookmarks": ["Bookmarks", "Lesezeichen", "Marcadores", "Favoris", "Segnalibri", "ブックマーク", "북마크", "Bladwijzers", "Favoritos", "书签"],
}

ANCHOR = "DeviceType_Nfc"


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for li, fname in enumerate(LOCALES):
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    m = re.search(
        rf'(  <data name="{re.escape(ANCHOR)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)',
        text)
    if not m:
        print(f"FAIL {fname}: anchor {ANCHOR} not found")
        continue
    insert = []
    for key, values in KEYS.items():
        if f'<data name="{key}"' in text:
            continue
        insert.append(f'  <data name="{key}" xml:space="preserve"><value>{xml_escape(values[li])}</value></data>\n')
    if not insert:
        print(f"OK   {fname}  (already present)")
        continue
    text = text[:m.end()] + "".join(insert) + text[m.end():]
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  (+{len(insert)} keys)")
