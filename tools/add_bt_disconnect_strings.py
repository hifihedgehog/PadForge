"""Add the #162 Bluetooth-disconnect strings (Disconnect Controller macro
action + Devices-page idle countdown) to all 10 locales. Idempotent,
BOM-preserving. Mirrors add_consumer_control_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "MacroAction_Type_DisconnectController": ["Disconnect Controller", "Controller trennen", "Desconectar mando", "Déconnecter la manette", "Disconnetti controller", "コントローラーを切断", "컨트롤러 연결 해제", "Controller loskoppelen", "Desconectar Controle", "断开控制器"],
    "MacroAction_DisconnectController_Tooltip": [
        "Drops the controller's Bluetooth link so it goes to sleep. Bluetooth connections only. Devices that are charging are skipped.",
        "Trennt die Bluetooth-Verbindung des Controllers, sodass er in den Ruhezustand wechselt. Nur Bluetooth-Verbindungen. Geräte, die gerade laden, werden übersprungen.",
        "Corta el enlace Bluetooth del mando para que entre en reposo. Solo conexiones Bluetooth. Se omiten los dispositivos que se están cargando.",
        "Coupe la liaison Bluetooth de la manette pour qu'elle se mette en veille. Connexions Bluetooth uniquement. Les appareils en charge sont ignorés.",
        "Interrompe il collegamento Bluetooth del controller in modo che entri in sospensione. Solo connessioni Bluetooth. I dispositivi in carica vengono ignorati.",
        "コントローラーのBluetooth接続を切断してスリープさせます。Bluetooth接続のみ対象です。充電中のデバイスはスキップされます。",
        "컨트롤러의 Bluetooth 연결을 끊어 절전 상태로 전환합니다. Bluetooth 연결에만 적용됩니다. 충전 중인 장치는 건너뜁니다.",
        "Verbreekt de Bluetooth-verbinding van de controller zodat deze in slaapstand gaat. Alleen Bluetooth-verbindingen. Apparaten die opladen worden overgeslagen.",
        "Corta o link Bluetooth do controle para que ele entre em repouso. Somente conexões Bluetooth. Dispositivos em carregamento são ignorados.",
        "断开控制器的蓝牙连接，使其进入休眠。仅适用于蓝牙连接。正在充电的设备会被跳过。"],
    "MacroAction_DisconnectController_Format": ["Disconnect Controller: {0}", "Controller trennen: {0}", "Desconectar mando: {0}", "Déconnecter la manette : {0}", "Disconnetti controller: {0}", "コントローラーを切断: {0}", "컨트롤러 연결 해제: {0}", "Controller loskoppelen: {0}", "Desconectar Controle: {0}", "断开控制器：{0}"],
    "MacroDisconnect_Target_Label": ["Target", "Ziel", "Objetivo", "Cible", "Destinazione", "対象", "대상", "Doel", "Alvo", "目标"],
    "MacroDisconnect_TriggeringDevice": ["Triggering Device", "Auslösendes Gerät", "Dispositivo activador", "Appareil déclencheur", "Dispositivo attivante", "トリガーしたデバイス", "트리거한 장치", "Activerend apparaat", "Dispositivo Acionador", "触发设备"],
    "MacroDisconnect_SpecificDevice": ["Specific Device", "Bestimmtes Gerät", "Dispositivo específico", "Appareil spécifique", "Dispositivo specifico", "特定のデバイス", "특정 장치", "Specifiek apparaat", "Dispositivo Específico", "特定设备"],
    "MacroDisconnect_SlotDevices": ["All Devices on This Slot", "Alle Geräte dieses Slots", "Todos los dispositivos de esta ranura", "Tous les appareils de cet emplacement", "Tutti i dispositivi di questo slot", "このスロットの全デバイス", "이 슬롯의 모든 장치", "Alle apparaten op dit slot", "Todos os Dispositivos deste Slot", "此插槽的所有设备"],
    "MacroDisconnect_AllDevices": ["All Bluetooth Devices", "Alle Bluetooth-Geräte", "Todos los dispositivos Bluetooth", "Tous les appareils Bluetooth", "Tutti i dispositivi Bluetooth", "すべてのBluetoothデバイス", "모든 Bluetooth 장치", "Alle Bluetooth-apparaten", "Todos os Dispositivos Bluetooth", "所有蓝牙设备"],
    "MacroDisconnect_Device_Label": ["Device", "Gerät", "Dispositivo", "Appareil", "Dispositivo", "デバイス", "장치", "Apparaat", "Dispositivo", "设备"],
    "MacroDisconnect_Caveat": [
        "Bluetooth only. The controller sleeps once the link drops. Devices that are charging are skipped.",
        "Nur Bluetooth. Der Controller wechselt in den Ruhezustand, sobald die Verbindung getrennt ist. Geräte, die gerade laden, werden übersprungen.",
        "Solo Bluetooth. El mando entra en reposo al cortarse el enlace. Se omiten los dispositivos que se están cargando.",
        "Bluetooth uniquement. La manette se met en veille dès que la liaison est coupée. Les appareils en charge sont ignorés.",
        "Solo Bluetooth. Il controller entra in sospensione quando il collegamento cade. I dispositivi in carica vengono ignorati.",
        "Bluetooth接続のみ対象です。接続が切れるとコントローラーはスリープします。充電中のデバイスはスキップされます。",
        "Bluetooth 전용입니다. 연결이 끊기면 컨트롤러가 절전 상태가 됩니다. 충전 중인 장치는 건너뜁니다.",
        "Alleen Bluetooth. De controller gaat in slaapstand zodra de verbinding wegvalt. Apparaten die opladen worden overgeslagen.",
        "Somente Bluetooth. O controle entra em repouso quando o link cai. Dispositivos em carregamento são ignorados.",
        "仅适用于蓝牙。连接断开后控制器进入休眠。正在充电的设备会被跳过。"],
    "Devices_IdleDisconnect": ["Idle Disconnect", "Trennung bei Inaktivität", "Desconexión por inactividad", "Déconnexion en cas d'inactivité", "Disconnessione per inattività", "アイドル切断", "유휴 연결 해제", "Loskoppelen bij inactiviteit", "Desconexão por Inatividade", "空闲断开"],
    "Devices_IdleDisconnectMinutes": ["minutes (0 = off)", "Minuten (0 = aus)", "minutos (0 = desactivado)", "minutes (0 = désactivé)", "minuti (0 = disattivato)", "分 (0 = オフ)", "분 (0 = 끄기)", "minuten (0 = uit)", "minutos (0 = desativado)", "分钟（0 = 关闭）"],
    "Devices_IdleDisconnectTooltip": [
        "Disconnects this Bluetooth controller after this many minutes without input, so it sleeps and saves battery. Never fires while charging or over USB.",
        "Trennt diesen Bluetooth-Controller nach so vielen Minuten ohne Eingabe, sodass er in den Ruhezustand wechselt und Akku spart. Löst nie beim Laden oder über USB aus.",
        "Desconecta este mando Bluetooth tras esos minutos sin entrada, para que entre en reposo y ahorre batería. Nunca se activa durante la carga ni por USB.",
        "Déconnecte cette manette Bluetooth après ce nombre de minutes sans entrée, pour qu'elle se mette en veille et économise la batterie. Ne se déclenche jamais pendant la charge ni en USB.",
        "Disconnette questo controller Bluetooth dopo questi minuti senza input, così entra in sospensione e risparmia batteria. Non si attiva mai durante la carica o via USB.",
        "入力がないままこの分数が経過すると、このBluetoothコントローラーを切断してスリープさせ、バッテリーを節約します。充電中やUSB接続時には作動しません。",
        "입력이 없는 상태로 설정한 분이 지나면 이 Bluetooth 컨트롤러의 연결을 끊어 절전 상태로 전환하고 배터리를 절약합니다. 충전 중이거나 USB 연결 시에는 작동하지 않습니다.",
        "Koppelt deze Bluetooth-controller los na dit aantal minuten zonder invoer, zodat deze in slaapstand gaat en batterij bespaart. Wordt nooit geactiveerd tijdens opladen of via USB.",
        "Desconecta este controle Bluetooth após esses minutos sem entrada, para que entre em repouso e economize bateria. Nunca dispara durante o carregamento ou via USB.",
        "在无输入达到设定分钟数后断开此蓝牙控制器，使其休眠以节省电量。充电时或 USB 连接时不会触发。"],
}

ANCHOR = "DeviceType_ConsumerControl"


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
