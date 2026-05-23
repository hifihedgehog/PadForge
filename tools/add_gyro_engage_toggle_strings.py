"""Add the gyro engage-mode + SetGyroEngaged macro strings to all 9
non-English locales. Each string is anchored to an adjacent existing
key that's present in every locale.

Strings added (per locale):
  Settings_GyroAimEngageMode               (anchor: Settings_GyroAimEngageButton)
  Settings_GyroAimEngageMode_Tooltip
  Settings_GyroAimEngageMode_Hold
  Settings_GyroAimEngageMode_Toggle
  Pad_ResetGyroAimEngageMode               (anchor: Pad_ResetGyroAimEngageButton)
  Macro_SetGyroEngaged                     (anchor: MacroAction_RumbleStop_Tooltip)
  Macro_SetGyroEngaged_Tooltip
  Macro_SetGyroEngaged_Mode_Label
  Macro_SetGyroEngaged_Toggle
  Macro_SetGyroEngaged_On
  Macro_SetGyroEngaged_Off
  MacroAction_SetGyroEngaged_Format
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

# Per-locale translations.  Insertion happens after each anchor block,
# matching the English file's order.
LOCALES = {
    # German
    "Strings.de.resx": {
        "Settings_GyroAimEngageMode":           "Aktivierungsmodus",
        "Settings_GyroAimEngageMode_Tooltip":   "Halten: Gyro aktiv, solange die Aktivierungstaste gedrückt ist. Umschalten: Jeder Tastendruck schaltet den Gyro um; Loslassen hat keine Wirkung. Umschalten ist über den Polling-Tick hinweg persistent und setzt beim Profilwechsel zurück.",
        "Settings_GyroAimEngageMode_Hold":      "Halten",
        "Settings_GyroAimEngageMode_Toggle":    "Umschalten",
        "Pad_ResetGyroAimEngageMode":           "Aktivierungsmodus zurücksetzen",
        "Macro_SetGyroEngaged":                 "Gyro-Aktivierung setzen",
        "Macro_SetGyroEngaged_Tooltip":         "Setzt den Gyro-Aktivierungsstatus des Slots über ein Makro. Umschalten kippt den aktuellen Zustand; Ein erzwingt Aktivierung; Aus erzwingt Deaktivierung. Unabhängig von der Aktivierungstaste im Gyro-Tab — beide Quellen werden ODER-kombiniert, sodass keine die andere deaktivieren kann.",
        "Macro_SetGyroEngaged_Mode_Label":      "Modus",
        "Macro_SetGyroEngaged_Toggle":          "Umschalten",
        "Macro_SetGyroEngaged_On":              "Ein",
        "Macro_SetGyroEngaged_Off":             "Aus",
        "MacroAction_SetGyroEngaged_Format":    "Gyro-Aktivierung setzen: {0}",
    },
    # Spanish
    "Strings.es.resx": {
        "Settings_GyroAimEngageMode":           "Modo de activación",
        "Settings_GyroAimEngageMode_Tooltip":   "Mantener: el giroscopio se activa mientras se mantiene presionado el botón de activación. Alternar: cada pulsación enciende o apaga el giroscopio; soltar no hace nada. Alternar es persistente durante el ciclo de muestreo y se restablece al cambiar de perfil.",
        "Settings_GyroAimEngageMode_Hold":      "Mantener",
        "Settings_GyroAimEngageMode_Toggle":    "Alternar",
        "Pad_ResetGyroAimEngageMode":           "Restablecer modo de activación",
        "Macro_SetGyroEngaged":                 "Establecer giroscopio activado",
        "Macro_SetGyroEngaged_Tooltip":         "Establece el estado de activación del giroscopio del slot desde una macro. Alternar invierte el estado actual; Activar lo fuerza a activado; Desactivar lo fuerza a desactivado. Independiente del botón de activación de la pestaña Gyro — las dos fuentes se combinan con OR, por lo que ninguna puede desactivar lo que la otra activó.",
        "Macro_SetGyroEngaged_Mode_Label":      "Modo",
        "Macro_SetGyroEngaged_Toggle":          "Alternar",
        "Macro_SetGyroEngaged_On":              "Activar",
        "Macro_SetGyroEngaged_Off":             "Desactivar",
        "MacroAction_SetGyroEngaged_Format":    "Establecer giroscopio activado: {0}",
    },
    # French
    "Strings.fr.resx": {
        "Settings_GyroAimEngageMode":           "Mode d'activation",
        "Settings_GyroAimEngageMode_Tooltip":   "Maintenir : le gyroscope fonctionne tant que le bouton d'activation est maintenu. Bascule : chaque pression active ou désactive le gyroscope ; le relâchement n'a aucun effet. Bascule est persistante d'un cycle d'interrogation à l'autre et se réinitialise lors du changement de profil.",
        "Settings_GyroAimEngageMode_Hold":      "Maintenir",
        "Settings_GyroAimEngageMode_Toggle":    "Bascule",
        "Pad_ResetGyroAimEngageMode":           "Réinitialiser le mode d'activation",
        "Macro_SetGyroEngaged":                 "Définir l'activation du gyroscope",
        "Macro_SetGyroEngaged_Tooltip":         "Définit l'état d'activation du gyroscope du slot depuis une macro. Bascule inverse l'état actuel ; Activé force l'activation ; Désactivé force la désactivation. Indépendant du bouton d'activation de l'onglet Gyro — les deux sources se combinent par OU, donc aucune ne peut désactiver ce que l'autre a activé.",
        "Macro_SetGyroEngaged_Mode_Label":      "Mode",
        "Macro_SetGyroEngaged_Toggle":          "Bascule",
        "Macro_SetGyroEngaged_On":              "Activé",
        "Macro_SetGyroEngaged_Off":             "Désactivé",
        "MacroAction_SetGyroEngaged_Format":    "Définir l'activation du gyroscope : {0}",
    },
    # Italian
    "Strings.it.resx": {
        "Settings_GyroAimEngageMode":           "Modalità di attivazione",
        "Settings_GyroAimEngageMode_Tooltip":   "Tieni premuto: il giroscopio si attiva mentre il pulsante di attivazione è premuto. Attiva/disattiva: ogni pressione attiva o disattiva il giroscopio; il rilascio non fa nulla. Attiva/disattiva è persistente per ciclo di polling e si reimposta al cambio di profilo.",
        "Settings_GyroAimEngageMode_Hold":      "Tieni premuto",
        "Settings_GyroAimEngageMode_Toggle":    "Attiva/disattiva",
        "Pad_ResetGyroAimEngageMode":           "Ripristina modalità di attivazione",
        "Macro_SetGyroEngaged":                 "Imposta giroscopio attivato",
        "Macro_SetGyroEngaged_Tooltip":         "Imposta lo stato di attivazione del giroscopio dello slot da una macro. Attiva/disattiva inverte lo stato corrente; Attivato forza l'attivazione; Disattivato forza la disattivazione. Indipendente dal pulsante di attivazione della scheda Gyro — le due fonti si combinano con OR, quindi nessuna può disattivare ciò che l'altra ha attivato.",
        "Macro_SetGyroEngaged_Mode_Label":      "Modalità",
        "Macro_SetGyroEngaged_Toggle":          "Attiva/disattiva",
        "Macro_SetGyroEngaged_On":              "Attivato",
        "Macro_SetGyroEngaged_Off":             "Disattivato",
        "MacroAction_SetGyroEngaged_Format":    "Imposta giroscopio attivato: {0}",
    },
    # Japanese
    "Strings.ja.resx": {
        "Settings_GyroAimEngageMode":           "起動モード",
        "Settings_GyroAimEngageMode_Tooltip":   "ホールド: 起動ボタンを押している間、ジャイロが作動します。トグル: ボタンを押すたびにジャイロのオン/オフが切り替わります。離しても何も起こりません。トグルはポーリング間で維持され、プロファイル切り替え時にリセットされます。",
        "Settings_GyroAimEngageMode_Hold":      "ホールド",
        "Settings_GyroAimEngageMode_Toggle":    "トグル",
        "Pad_ResetGyroAimEngageMode":           "起動モードをリセット",
        "Macro_SetGyroEngaged":                 "ジャイロ起動を設定",
        "Macro_SetGyroEngaged_Tooltip":         "マクロからスロットのジャイロ起動状態を設定します。トグルは現在の状態を反転します。オンは強制的に起動します。オフは強制的に解除します。Gyro タブの起動ボタンとは独立しており、両ソースは OR で結合されるため、どちらも相手が起動したものを無効化できません。",
        "Macro_SetGyroEngaged_Mode_Label":      "モード",
        "Macro_SetGyroEngaged_Toggle":          "トグル",
        "Macro_SetGyroEngaged_On":              "オン",
        "Macro_SetGyroEngaged_Off":             "オフ",
        "MacroAction_SetGyroEngaged_Format":    "ジャイロ起動を設定: {0}",
    },
    # Korean
    "Strings.ko.resx": {
        "Settings_GyroAimEngageMode":           "활성화 모드",
        "Settings_GyroAimEngageMode_Tooltip":   "유지: 활성화 버튼을 누르고 있는 동안 자이로가 작동합니다. 토글: 누를 때마다 자이로가 켜지거나 꺼지며, 떼는 동작은 영향을 주지 않습니다. 토글은 폴링 틱 사이에 유지되며 프로필 전환 시 초기화됩니다.",
        "Settings_GyroAimEngageMode_Hold":      "유지",
        "Settings_GyroAimEngageMode_Toggle":    "토글",
        "Pad_ResetGyroAimEngageMode":           "활성화 모드 재설정",
        "Macro_SetGyroEngaged":                 "자이로 활성 설정",
        "Macro_SetGyroEngaged_Tooltip":         "매크로에서 슬롯의 자이로 활성 상태를 설정합니다. 토글은 현재 상태를 뒤집습니다. 켜기는 강제로 활성화합니다. 끄기는 강제로 비활성화합니다. Gyro 탭의 활성화 버튼과 독립적이며, 두 소스는 OR로 결합되므로 어느 쪽도 다른 쪽이 활성화한 것을 비활성화할 수 없습니다.",
        "Macro_SetGyroEngaged_Mode_Label":      "모드",
        "Macro_SetGyroEngaged_Toggle":          "토글",
        "Macro_SetGyroEngaged_On":              "켜기",
        "Macro_SetGyroEngaged_Off":             "끄기",
        "MacroAction_SetGyroEngaged_Format":    "자이로 활성 설정: {0}",
    },
    # Dutch
    "Strings.nl.resx": {
        "Settings_GyroAimEngageMode":           "Activeringsmodus",
        "Settings_GyroAimEngageMode_Tooltip":   "Vasthouden: gyro is actief zolang de activeringsknop wordt ingedrukt. Schakelen: elke druk schakelt de gyro aan of uit; loslaten doet niets. Schakelen is persistent over de polling-tick en wordt gereset bij profielwisseling.",
        "Settings_GyroAimEngageMode_Hold":      "Vasthouden",
        "Settings_GyroAimEngageMode_Toggle":    "Schakelen",
        "Pad_ResetGyroAimEngageMode":           "Activeringsmodus resetten",
        "Macro_SetGyroEngaged":                 "Gyro-activering instellen",
        "Macro_SetGyroEngaged_Tooltip":         "Stelt de gyro-activeringsstatus van het slot in vanuit een macro. Schakelen wisselt de huidige status; Aan forceert activering; Uit forceert deactivering. Onafhankelijk van de activeringsknop van het Gyro-tabblad — de twee bronnen worden OR-gecombineerd, dus geen van beide kan uitschakelen wat de ander heeft geactiveerd.",
        "Macro_SetGyroEngaged_Mode_Label":      "Modus",
        "Macro_SetGyroEngaged_Toggle":          "Schakelen",
        "Macro_SetGyroEngaged_On":              "Aan",
        "Macro_SetGyroEngaged_Off":             "Uit",
        "MacroAction_SetGyroEngaged_Format":    "Gyro-activering instellen: {0}",
    },
    # Brazilian Portuguese
    "Strings.pt-BR.resx": {
        "Settings_GyroAimEngageMode":           "Modo de Ativação",
        "Settings_GyroAimEngageMode_Tooltip":   "Segurar: o giroscópio dispara enquanto o botão de ativação é segurado. Alternar: cada pressionamento liga ou desliga o giroscópio; soltar não faz nada. Alternar é persistente entre ciclos de polling e reseta na troca de perfil.",
        "Settings_GyroAimEngageMode_Hold":      "Segurar",
        "Settings_GyroAimEngageMode_Toggle":    "Alternar",
        "Pad_ResetGyroAimEngageMode":           "Restaurar Modo de Ativação",
        "Macro_SetGyroEngaged":                 "Definir Giroscópio Ativado",
        "Macro_SetGyroEngaged_Tooltip":         "Define o estado de ativação do giroscópio do slot a partir de uma macro. Alternar inverte o estado atual; Ligar força a ativação; Desligar força a desativação. Independente do botão de ativação da aba Gyro — as duas fontes são combinadas com OR, então nenhuma pode desativar o que a outra ativou.",
        "Macro_SetGyroEngaged_Mode_Label":      "Modo",
        "Macro_SetGyroEngaged_Toggle":          "Alternar",
        "Macro_SetGyroEngaged_On":              "Ligar",
        "Macro_SetGyroEngaged_Off":             "Desligar",
        "MacroAction_SetGyroEngaged_Format":    "Definir giroscópio ativado: {0}",
    },
    # Simplified Chinese
    "Strings.zh-Hans.resx": {
        "Settings_GyroAimEngageMode":           "激活模式",
        "Settings_GyroAimEngageMode_Tooltip":   "保持:按住激活按钮时陀螺仪开启。切换:每次按下切换陀螺仪开关;释放无效。切换在轮询周期之间持续,切换配置文件时重置。",
        "Settings_GyroAimEngageMode_Hold":      "保持",
        "Settings_GyroAimEngageMode_Toggle":    "切换",
        "Pad_ResetGyroAimEngageMode":           "重置激活模式",
        "Macro_SetGyroEngaged":                 "设置陀螺仪激活",
        "Macro_SetGyroEngaged_Tooltip":         "从宏中设置插槽的陀螺仪激活状态。切换:翻转当前状态;开:强制激活;关:强制停用。独立于 Gyro 选项卡的激活按钮——两个来源以 OR 方式组合,因此任何一方都无法停用另一方激活的状态。",
        "Macro_SetGyroEngaged_Mode_Label":      "模式",
        "Macro_SetGyroEngaged_Toggle":          "切换",
        "Macro_SetGyroEngaged_On":              "开",
        "Macro_SetGyroEngaged_Off":             "关",
        "MacroAction_SetGyroEngaged_Format":    "设置陀螺仪激活:{0}",
    },
}

# Each new key is inserted directly after a known existing anchor key
# present in every locale. Anchor → list of new keys to insert after it.
INSERTION_PLAN = [
    ("Settings_GyroAimEngageButton_Tooltip", [
        "Settings_GyroAimEngageMode",
        "Settings_GyroAimEngageMode_Tooltip",
        "Settings_GyroAimEngageMode_Hold",
        "Settings_GyroAimEngageMode_Toggle",
    ]),
    ("Pad_ResetGyroAimEngageButton", [
        "Pad_ResetGyroAimEngageMode",
    ]),
    ("MacroAction_RumbleStop_Tooltip", [
        "Macro_SetGyroEngaged",
        "Macro_SetGyroEngaged_Tooltip",
        "Macro_SetGyroEngaged_Mode_Label",
        "Macro_SetGyroEngaged_Toggle",
        "Macro_SetGyroEngaged_On",
        "Macro_SetGyroEngaged_Off",
        "MacroAction_SetGyroEngaged_Format",
    ]),
]

import re

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

# Strip already-existing keys from the insertion plan (idempotency).
def insert_keys(text, anchor_key, new_keys, translations, xml_escape):
    # Find the anchor's full <data...>...</data> block.
    pat = re.compile(
        rf'(  <data name="{re.escape(anchor_key)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)'
    )
    m = pat.search(text)
    if not m:
        return text, [], [f"anchor {anchor_key} not found"]
    inserted = []
    skipped = []
    insertion_lines = []
    for k in new_keys:
        if f'<data name="{k}"' in text:
            skipped.append(k)
            continue
        v = xml_escape(translations.get(k, k))
        insertion_lines.append(f'  <data name="{k}" xml:space="preserve"><value>{v}</value></data>\n')
        inserted.append(k)
    if not insertion_lines:
        return text, [], skipped
    insertion = "".join(insertion_lines)
    new_text = text[:m.end()] + insertion + text[m.end():]
    return new_text, inserted, skipped

def xml_escape(s):
    return (s.replace("&", "&amp;")
             .replace("<", "&lt;")
             .replace(">", "&gt;"))

for fname, translations in LOCALES.items():
    p = ROOT / fname
    text, bom = read_text(p)
    total_inserted = []
    total_skipped = []
    errors = []
    for anchor, new_keys in INSERTION_PLAN:
        text, ins, skp = insert_keys(text, anchor, new_keys, translations, xml_escape)
        if isinstance(skp, list) and skp and isinstance(skp[0], str) and skp[0].startswith("anchor"):
            errors.extend(skp)
            continue
        total_inserted.extend(ins)
        total_skipped.extend(skp)
    if errors:
        print(f"FAIL {fname}: {', '.join(errors)}")
        continue
    if total_inserted:
        write_text(p, text, bom)
    print(f"OK   {fname}  (inserted {len(total_inserted)}, skipped {len(total_skipped)})")
