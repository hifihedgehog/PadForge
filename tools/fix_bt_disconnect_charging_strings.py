"""#162: remove the charging-skip promise from the three disconnect strings.
The charging gate was dropped (SDL power state measured unreliable), so the
UI must not claim it. In-place value replacement, BOM-preserving, idempotent."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "MacroAction_DisconnectController_Tooltip": [
        "Drops the controller's Bluetooth link so it goes to sleep. Bluetooth connections only.",
        "Trennt die Bluetooth-Verbindung des Controllers, sodass er in den Ruhezustand wechselt. Nur Bluetooth-Verbindungen.",
        "Corta el enlace Bluetooth del mando para que entre en reposo. Solo conexiones Bluetooth.",
        "Coupe la liaison Bluetooth de la manette pour qu'elle se mette en veille. Connexions Bluetooth uniquement.",
        "Interrompe il collegamento Bluetooth del controller in modo che entri in sospensione. Solo connessioni Bluetooth.",
        "コントローラーのBluetooth接続を切断してスリープさせます。Bluetooth接続のみ対象です。",
        "컨트롤러의 Bluetooth 연결을 끊어 절전 상태로 전환합니다. Bluetooth 연결에만 적용됩니다.",
        "Verbreekt de Bluetooth-verbinding van de controller zodat deze in slaapstand gaat. Alleen Bluetooth-verbindingen.",
        "Corta o link Bluetooth do controle para que ele entre em repouso. Somente conexões Bluetooth.",
        "断开控制器的蓝牙连接，使其进入休眠。仅适用于蓝牙连接。"],
    "MacroDisconnect_Caveat": [
        "Bluetooth only. The controller sleeps once the link drops.",
        "Nur Bluetooth. Der Controller wechselt in den Ruhezustand, sobald die Verbindung getrennt ist.",
        "Solo Bluetooth. El mando entra en reposo al cortarse el enlace.",
        "Bluetooth uniquement. La manette se met en veille dès que la liaison est coupée.",
        "Solo Bluetooth. Il controller entra in sospensione quando il collegamento cade.",
        "Bluetooth接続のみ対象です。接続が切れるとコントローラーはスリープします。",
        "Bluetooth 전용입니다. 연결이 끊기면 컨트롤러가 절전 상태가 됩니다.",
        "Alleen Bluetooth. De controller gaat in slaapstand zodra de verbinding wegvalt.",
        "Somente Bluetooth. O controle entra em repouso quando o link cai.",
        "仅适用于蓝牙。连接断开后控制器进入休眠。"],
    "Devices_IdleDisconnectTooltip": [
        "Disconnects this Bluetooth controller after this many minutes without input, so it sleeps and saves battery. Never fires over USB.",
        "Trennt diesen Bluetooth-Controller nach so vielen Minuten ohne Eingabe, sodass er in den Ruhezustand wechselt und Akku spart. Löst nie über USB aus.",
        "Desconecta este mando Bluetooth tras esos minutos sin entrada, para que entre en reposo y ahorre batería. Nunca se activa por USB.",
        "Déconnecte cette manette Bluetooth après ce nombre de minutes sans entrée, pour qu'elle se mette en veille et économise la batterie. Ne se déclenche jamais en USB.",
        "Disconnette questo controller Bluetooth dopo questi minuti senza input, così entra in sospensione e risparmia batteria. Non si attiva mai via USB.",
        "入力がないままこの分数が経過すると、このBluetoothコントローラーを切断してスリープさせ、バッテリーを節約します。USB接続時には作動しません。",
        "입력이 없는 상태로 설정한 분이 지나면 이 Bluetooth 컨트롤러의 연결을 끊어 절전 상태로 전환하고 배터리를 절약합니다. USB 연결 시에는 작동하지 않습니다.",
        "Koppelt deze Bluetooth-controller los na dit aantal minuten zonder invoer, zodat deze in slaapstand gaat en batterij bespaart. Wordt nooit geactiveerd via USB.",
        "Desconecta este controle Bluetooth após esses minutos sem entrada, para que entre em repouso e economize bateria. Nunca dispara via USB.",
        "在无输入达到设定分钟数后断开此蓝牙控制器，使其休眠以节省电量。USB 连接时不会触发。"],
}


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for li, fname in enumerate(LOCALES):
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    changed = 0
    for key, values in KEYS.items():
        new_line = f'  <data name="{key}" xml:space="preserve"><value>{xml_escape(values[li])}</value></data>'
        pattern = rf'  <data name="{re.escape(key)}" xml:space="preserve"><value>[^<]*</value></data>'
        text, n = re.subn(pattern, new_line, text)
        changed += n
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  ({changed} replaced)")
