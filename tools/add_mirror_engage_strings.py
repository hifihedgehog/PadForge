"""Add the #185 haptic-mirror engage strings (Pad_Audio_Engage_*) to all 10
locales, anchored after keys present in every file. Idempotent, BOM-preserving.
Mirrors add_jc2_mouse_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# key -> anchor key it is inserted after -> per-file values
KEYS = [
    ("Pad_Audio_Engage_Label", "Pad_Audio_Mirror", {
        "Strings.resx":         "Play mirrored audio",
        "Strings.de.resx":      "Gespiegeltes Audio abspielen",
        "Strings.es.resx":      "Reproducir audio reflejado",
        "Strings.fr.resx":      "Lire l'audio en miroir",
        "Strings.it.resx":      "Riproduci audio duplicato",
        "Strings.ja.resx":      "ミラー音声を再生",
        "Strings.ko.resx":      "미러 오디오 재생",
        "Strings.nl.resx":      "Gespiegelde audio afspelen",
        "Strings.pt-BR.resx":   "Reproduzir áudio espelhado",
        "Strings.zh-Hans.resx": "播放镜像音频",
    }),
    ("Pad_Audio_Engage_Tooltip", "Pad_Audio_Engage_Label", {
        "Strings.resx":         "When the mirrored audio plays as haptic tones. Gate it on a held input or on game rumble so music does not buzz the controller all the time.",
        "Strings.de.resx":      "Wann das gespiegelte Audio als haptische Töne abgespielt wird. An eine gehaltene Eingabe oder die Spielvibration koppeln, damit Musik den Controller nicht ständig vibrieren lässt.",
        "Strings.es.resx":      "Cuándo se reproduce el audio reflejado como tonos hápticos. Condiciónalo a una entrada mantenida o a la vibración del juego para que la música no haga vibrar el mando todo el tiempo.",
        "Strings.fr.resx":      "Quand l'audio en miroir est joué en tonalités haptiques. Conditionnez-le à une entrée maintenue ou à la vibration du jeu pour que la musique ne fasse pas vibrer la manette en permanence.",
        "Strings.it.resx":      "Quando l'audio duplicato viene riprodotto come toni aptici. Vincolalo a un input premuto o alla vibrazione del gioco così la musica non fa vibrare il controller di continuo.",
        "Strings.ja.resx":      "ミラー音声をハプティックトーンとして再生するタイミング。入力の押下やゲームの振動に連動させると、音楽で常にコントローラーが振動しなくなります。",
        "Strings.ko.resx":      "미러 오디오가 햅틱 톤으로 재생되는 시점입니다. 입력 유지나 게임 진동에 연동하면 음악 때문에 컨트롤러가 계속 진동하지 않습니다.",
        "Strings.nl.resx":      "Wanneer de gespiegelde audio als haptische tonen speelt. Koppel het aan een ingedrukte invoer of aan de trilfunctie van de game, zodat muziek de controller niet constant laat trillen.",
        "Strings.pt-BR.resx":   "Quando o áudio espelhado toca como tons hápticos. Vincule a uma entrada mantida ou à vibração do jogo para que a música não fique vibrando o controle o tempo todo.",
        "Strings.zh-Hans.resx": "镜像音频以触觉音调播放的时机。可将其绑定到按住的输入或游戏震动，避免音乐一直震动手柄。",
    }),
    ("Pad_Audio_Engage_Always", "Pad_Audio_Engage_Tooltip", {
        "Strings.resx":         "Always",
        "Strings.de.resx":      "Immer",
        "Strings.es.resx":      "Siempre",
        "Strings.fr.resx":      "Toujours",
        "Strings.it.resx":      "Sempre",
        "Strings.ja.resx":      "常時",
        "Strings.ko.resx":      "항상",
        "Strings.nl.resx":      "Altijd",
        "Strings.pt-BR.resx":   "Sempre",
        "Strings.zh-Hans.resx": "始终",
    }),
    ("Pad_Audio_Engage_Input", "Pad_Audio_Engage_Always", {
        "Strings.resx":         "While an input is held",
        "Strings.de.resx":      "Solange eine Eingabe gehalten wird",
        "Strings.es.resx":      "Mientras se mantiene una entrada",
        "Strings.fr.resx":      "Tant qu'une entrée est maintenue",
        "Strings.it.resx":      "Mentre un input è premuto",
        "Strings.ja.resx":      "入力を押している間",
        "Strings.ko.resx":      "입력을 누르고 있는 동안",
        "Strings.nl.resx":      "Zolang een invoer ingedrukt is",
        "Strings.pt-BR.resx":   "Enquanto uma entrada é mantida",
        "Strings.zh-Hans.resx": "按住某个输入时",
    }),
    ("Pad_Audio_Engage_Rumble", "Pad_Audio_Engage_Input", {
        "Strings.resx":         "While the game rumbles",
        "Strings.de.resx":      "Solange das Spiel vibriert",
        "Strings.es.resx":      "Mientras el juego vibra",
        "Strings.fr.resx":      "Tant que le jeu vibre",
        "Strings.it.resx":      "Mentre il gioco vibra",
        "Strings.ja.resx":      "ゲームの振動中",
        "Strings.ko.resx":      "게임 진동 중",
        "Strings.nl.resx":      "Zolang de game trilt",
        "Strings.pt-BR.resx":   "Enquanto o jogo vibra",
        "Strings.zh-Hans.resx": "游戏震动时",
    }),
    ("Pad_Audio_EngageInput_Label", "Pad_Audio_Engage_Rumble", {
        "Strings.resx":         "Engage input",
        "Strings.de.resx":      "Aktivierungseingabe",
        "Strings.es.resx":      "Entrada de activación",
        "Strings.fr.resx":      "Entrée d'activation",
        "Strings.it.resx":      "Input di attivazione",
        "Strings.ja.resx":      "有効化入力",
        "Strings.ko.resx":      "활성화 입력",
        "Strings.nl.resx":      "Activeringsinvoer",
        "Strings.pt-BR.resx":   "Entrada de ativação",
        "Strings.zh-Hans.resx": "触发输入",
    }),
    ("Pad_Audio_EngageRelease_Label", "Pad_Audio_EngageInput_Label", {
        "Strings.resx":         "Release delay",
        "Strings.de.resx":      "Nachlaufzeit",
        "Strings.es.resx":      "Retardo de liberación",
        "Strings.fr.resx":      "Délai de relâchement",
        "Strings.it.resx":      "Ritardo di rilascio",
        "Strings.ja.resx":      "リリース遅延",
        "Strings.ko.resx":      "해제 지연",
        "Strings.nl.resx":      "Loslaatvertraging",
        "Strings.pt-BR.resx":   "Atraso de liberação",
        "Strings.zh-Hans.resx": "释放延迟",
    }),
    ("Pad_ResetMirrorEngageMode", "Pad_Audio_Engage_Label", {
        "Strings.resx":         "Reset Engage Mode",
        "Strings.de.resx":      "Aktivierungsmodus zurücksetzen",
        "Strings.es.resx":      "Restablecer modo de activación",
        "Strings.fr.resx":      "Réinitialiser le mode d'activation",
        "Strings.it.resx":      "Reimposta modalità di attivazione",
        "Strings.ja.resx":      "有効化モードをリセット",
        "Strings.ko.resx":      "활성화 모드 재설정",
        "Strings.nl.resx":      "Activeringsmodus resetten",
        "Strings.pt-BR.resx":   "Redefinir modo de ativação",
        "Strings.zh-Hans.resx": "重置触发模式",
    }),
    ("Pad_ResetMirrorEngageInput", "Pad_ResetMirrorEngageMode", {
        "Strings.resx":         "Reset Engage Input",
        "Strings.de.resx":      "Aktivierungseingabe zurücksetzen",
        "Strings.es.resx":      "Restablecer entrada de activación",
        "Strings.fr.resx":      "Réinitialiser l'entrée d'activation",
        "Strings.it.resx":      "Reimposta input di attivazione",
        "Strings.ja.resx":      "有効化入力をリセット",
        "Strings.ko.resx":      "활성화 입력 재설정",
        "Strings.nl.resx":      "Activeringsinvoer resetten",
        "Strings.pt-BR.resx":   "Redefinir entrada de ativação",
        "Strings.zh-Hans.resx": "重置触发输入",
    }),
    ("Pad_ResetMirrorEngageRelease", "Pad_ResetMirrorEngageInput", {
        "Strings.resx":         "Reset Release Delay",
        "Strings.de.resx":      "Nachlaufzeit zurücksetzen",
        "Strings.es.resx":      "Restablecer retardo de liberación",
        "Strings.fr.resx":      "Réinitialiser le délai de relâchement",
        "Strings.it.resx":      "Reimposta ritardo di rilascio",
        "Strings.ja.resx":      "リリース遅延をリセット",
        "Strings.ko.resx":      "해제 지연 재설정",
        "Strings.nl.resx":      "Loslaatvertraging resetten",
        "Strings.pt-BR.resx":   "Redefinir atraso de liberação",
        "Strings.zh-Hans.resx": "重置释放延迟",
    }),
    ("Pad_Audio_EngageRelease_Tooltip", "Pad_Audio_EngageRelease_Label", {
        "Strings.resx":         "How long the mirror keeps playing after the engage source drops, so the tone does not clip off instantly.",
        "Strings.de.resx":      "Wie lange der Spiegel nach dem Ende der Aktivierungsquelle weiterspielt, damit der Ton nicht abrupt abbricht.",
        "Strings.es.resx":      "Cuánto tiempo sigue sonando el reflejo después de que cese la fuente de activación, para que el tono no se corte de golpe.",
        "Strings.fr.resx":      "Durée pendant laquelle le miroir continue après la fin de la source d'activation, pour que la tonalité ne se coupe pas net.",
        "Strings.it.resx":      "Per quanto tempo il mirror continua dopo la fine della sorgente di attivazione, così il tono non si interrompe di colpo.",
        "Strings.ja.resx":      "有効化ソースが切れた後もミラー再生を続ける時間。トーンが急に途切れるのを防ぎます。",
        "Strings.ko.resx":      "활성화 소스가 끊긴 뒤에도 미러 재생을 유지하는 시간입니다. 톤이 갑자기 끊기지 않습니다.",
        "Strings.nl.resx":      "Hoe lang de spiegel blijft spelen nadat de activeringsbron wegvalt, zodat de toon niet abrupt stopt.",
        "Strings.pt-BR.resx":   "Por quanto tempo o espelho continua tocando depois que a fonte de ativação cessa, para o tom não cortar de repente.",
        "Strings.zh-Hans.resx": "触发源消失后镜像继续播放的时长，避免音调突然中断。",
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
