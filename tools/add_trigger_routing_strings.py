"""Add the issue #102 Trigger Routing + Rumble Trigger Override macro strings
to the neutral Strings.resx, all 9 locale resx files, and Strings.Designer.cs.

Macro keys anchor after MacroAction_RumbleStop_Tooltip; routing keys anchor
after Pad_ConstantTriggerForce_Header. Both anchors exist in every locale.
Idempotent: keys already present are skipped.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"
DESIGNER = ROOT / "Strings.Designer.cs"

MACRO_KEYS = [
    "MacroAction_Type_RumbleTrigger",
    "MacroAction_RumbleTrigger_Tooltip",
    "MacroAction_Type_RumbleTriggerStop",
    "MacroAction_RumbleTriggerStop",
    "MacroAction_RumbleTriggerStop_Tooltip",
]
ROUTE_KEYS = [
    "Pad_TriggerRouting_Header",
    "Pad_TriggerRouting_Description",
    "Pad_ResetTriggerRouting",
    "Pad_TriggerRouting_LeftTrigger",
    "Pad_TriggerRouting_RightTrigger",
    "Pad_TriggerRouting_Source",
    "Pad_TriggerRouting_Source_None",
    "Pad_TriggerRouting_Source_MainLeft",
    "Pad_TriggerRouting_Source_MainRight",
    "Pad_TriggerRouting_Source_MaxOfBoth",
    "Pad_TriggerRouting_Source_SumOfBoth",
    "Pad_TriggerRouting_Mode",
    "Pad_TriggerRouting_Mode_Off",
    "Pad_TriggerRouting_Mode_Duplicate",
    "Pad_TriggerRouting_Mode_Redirect",
    "Pad_TriggerRouting_Scale",
    "Pad_TriggerRouting_Activator",
    "Pad_TriggerRouting_ActivatorMode",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn",
    "Pad_ResetTriggerRouteActivator",
]

EN = {
    "MacroAction_Type_RumbleTrigger": "Rumble Trigger Override",
    "MacroAction_RumbleTrigger_Tooltip": "Drives the slot's trigger vibration from a macro. Same hold modes as Rumble Override, but the strength feeds the trigger channel (Xbox impulse triggers and DualSense Adaptive Trigger Vibration) instead of the grip motors. Max-combines with the trigger routing.",
    "MacroAction_Type_RumbleTriggerStop": "Stop Trigger Vibration",
    "MacroAction_RumbleTriggerStop": "Stop trigger vibration",
    "MacroAction_RumbleTriggerStop_Tooltip": "Releases any active macro trigger vibration on the slot. Pair with a Sticky Rumble Trigger Override to end the hold from a macro.",
    "Pad_TriggerRouting_Header": "Trigger Routing",
    "Pad_TriggerRouting_Description": "Route the main rumble motors into the trigger channel, one trigger at a time. Works with XInput rumble and force feedback. An activator can gate it so the trigger only buzzes while you hold a button.",
    "Pad_ResetTriggerRouting": "Reset trigger routing",
    "Pad_TriggerRouting_LeftTrigger": "Left trigger",
    "Pad_TriggerRouting_RightTrigger": "Right trigger",
    "Pad_TriggerRouting_Source": "Source",
    "Pad_TriggerRouting_Source_None": "None (off)",
    "Pad_TriggerRouting_Source_MainLeft": "Left motor",
    "Pad_TriggerRouting_Source_MainRight": "Right motor",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Max of both motors",
    "Pad_TriggerRouting_Source_SumOfBoth": "Sum of both motors",
    "Pad_TriggerRouting_Mode": "Mode",
    "Pad_TriggerRouting_Mode_Off": "Off",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplicate (keep main motor)",
    "Pad_TriggerRouting_Mode_Redirect": "Redirect (silence main motor)",
    "Pad_TriggerRouting_Scale": "Scale",
    "Pad_TriggerRouting_Activator": "Activator",
    "Pad_TriggerRouting_ActivatorMode": "Activator mode",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Always on",
    "Pad_ResetTriggerRouteActivator": "Reset activator",
}

TR = {
 "Strings.de.resx": {
    "MacroAction_Type_RumbleTrigger": "Trigger-Vibration überschreiben",
    "MacroAction_RumbleTrigger_Tooltip": "Steuert die Trigger-Vibration des Slots per Makro. Gleiche Haltemodi wie bei der Vibrationsüberschreibung, aber die Stärke speist den Trigger-Kanal (Xbox-Impulstrigger und DualSense Adaptive-Trigger-Vibration) statt der Griffmotoren. Wird per Maximum mit der Trigger-Weiterleitung kombiniert.",
    "MacroAction_Type_RumbleTriggerStop": "Trigger-Vibration stoppen",
    "MacroAction_RumbleTriggerStop": "Trigger-Vibration stoppen",
    "MacroAction_RumbleTriggerStop_Tooltip": "Hebt eine aktive Makro-Trigger-Vibration auf dem Slot auf. Mit einer haltenden Trigger-Vibrationsüberschreibung kombinieren, um das Halten per Makro zu beenden.",
    "Pad_TriggerRouting_Header": "Trigger-Weiterleitung",
    "Pad_TriggerRouting_Description": "Leitet die Hauptvibrationsmotoren in den Trigger-Kanal, pro Trigger einzeln. Funktioniert mit XInput-Vibration und Force Feedback. Ein Aktivator kann sie steuern, sodass der Trigger nur vibriert, solange eine Taste gehalten wird.",
    "Pad_ResetTriggerRouting": "Trigger-Weiterleitung zurücksetzen",
    "Pad_TriggerRouting_LeftTrigger": "Linker Trigger",
    "Pad_TriggerRouting_RightTrigger": "Rechter Trigger",
    "Pad_TriggerRouting_Source": "Quelle",
    "Pad_TriggerRouting_Source_None": "Keine (aus)",
    "Pad_TriggerRouting_Source_MainLeft": "Linker Motor",
    "Pad_TriggerRouting_Source_MainRight": "Rechter Motor",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Maximum beider Motoren",
    "Pad_TriggerRouting_Source_SumOfBoth": "Summe beider Motoren",
    "Pad_TriggerRouting_Mode": "Modus",
    "Pad_TriggerRouting_Mode_Off": "Aus",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplizieren (Hauptmotor behalten)",
    "Pad_TriggerRouting_Mode_Redirect": "Umleiten (Hauptmotor stummschalten)",
    "Pad_TriggerRouting_Scale": "Skalierung",
    "Pad_TriggerRouting_Activator": "Aktivator",
    "Pad_TriggerRouting_ActivatorMode": "Aktivierungsmodus",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Immer an",
    "Pad_ResetTriggerRouteActivator": "Aktivator zurücksetzen",
 },
 "Strings.es.resx": {
    "MacroAction_Type_RumbleTrigger": "Anular vibración del gatillo",
    "MacroAction_RumbleTrigger_Tooltip": "Controla la vibración del gatillo del slot desde una macro. Mismos modos de retención que Anular vibración, pero la intensidad alimenta el canal del gatillo (gatillos de impulso Xbox y Vibración de Gatillo Adaptativo DualSense) en lugar de los motores. Se combina por máximo con el enrutamiento del gatillo.",
    "MacroAction_Type_RumbleTriggerStop": "Detener vibración del gatillo",
    "MacroAction_RumbleTriggerStop": "Detener vibración del gatillo",
    "MacroAction_RumbleTriggerStop_Tooltip": "Libera cualquier vibración de gatillo por macro activa en el slot. Combina con una Anulación de vibración del gatillo fija para terminar la retención desde una macro.",
    "Pad_TriggerRouting_Header": "Enrutamiento de gatillos",
    "Pad_TriggerRouting_Description": "Enruta los motores de vibración principales al canal del gatillo, un gatillo a la vez. Funciona con vibración XInput y force feedback. Un activador puede controlarlo para que el gatillo solo vibre mientras mantienes un botón.",
    "Pad_ResetTriggerRouting": "Restablecer enrutamiento de gatillos",
    "Pad_TriggerRouting_LeftTrigger": "Gatillo izquierdo",
    "Pad_TriggerRouting_RightTrigger": "Gatillo derecho",
    "Pad_TriggerRouting_Source": "Fuente",
    "Pad_TriggerRouting_Source_None": "Ninguna (desactivado)",
    "Pad_TriggerRouting_Source_MainLeft": "Motor izquierdo",
    "Pad_TriggerRouting_Source_MainRight": "Motor derecho",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Máximo de ambos motores",
    "Pad_TriggerRouting_Source_SumOfBoth": "Suma de ambos motores",
    "Pad_TriggerRouting_Mode": "Modo",
    "Pad_TriggerRouting_Mode_Off": "Desactivado",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplicar (mantener motor principal)",
    "Pad_TriggerRouting_Mode_Redirect": "Redirigir (silenciar motor principal)",
    "Pad_TriggerRouting_Scale": "Escala",
    "Pad_TriggerRouting_Activator": "Activador",
    "Pad_TriggerRouting_ActivatorMode": "Modo de activación",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Siempre activo",
    "Pad_ResetTriggerRouteActivator": "Restablecer activador",
 },
 "Strings.fr.resx": {
    "MacroAction_Type_RumbleTrigger": "Remplacer la vibration de gâchette",
    "MacroAction_RumbleTrigger_Tooltip": "Pilote la vibration de gâchette du slot depuis une macro. Mêmes modes de maintien que Remplacer la vibration, mais l'intensité alimente le canal de gâchette (gâchettes à impulsion Xbox et Vibration de Gâchette Adaptative DualSense) au lieu des moteurs. Combinée par maximum avec le routage de gâchette.",
    "MacroAction_Type_RumbleTriggerStop": "Arrêter la vibration de gâchette",
    "MacroAction_RumbleTriggerStop": "Arrêter la vibration de gâchette",
    "MacroAction_RumbleTriggerStop_Tooltip": "Libère toute vibration de gâchette par macro active sur le slot. À associer à un Remplacement de vibration de gâchette persistant pour terminer le maintien depuis une macro.",
    "Pad_TriggerRouting_Header": "Routage des gâchettes",
    "Pad_TriggerRouting_Description": "Route les moteurs de vibration principaux vers le canal de gâchette, une gâchette à la fois. Fonctionne avec la vibration XInput et le retour de force. Un activateur peut le conditionner pour que la gâchette ne vibre que lorsqu'un bouton est maintenu.",
    "Pad_ResetTriggerRouting": "Réinitialiser le routage des gâchettes",
    "Pad_TriggerRouting_LeftTrigger": "Gâchette gauche",
    "Pad_TriggerRouting_RightTrigger": "Gâchette droite",
    "Pad_TriggerRouting_Source": "Source",
    "Pad_TriggerRouting_Source_None": "Aucune (désactivé)",
    "Pad_TriggerRouting_Source_MainLeft": "Moteur gauche",
    "Pad_TriggerRouting_Source_MainRight": "Moteur droit",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Maximum des deux moteurs",
    "Pad_TriggerRouting_Source_SumOfBoth": "Somme des deux moteurs",
    "Pad_TriggerRouting_Mode": "Mode",
    "Pad_TriggerRouting_Mode_Off": "Désactivé",
    "Pad_TriggerRouting_Mode_Duplicate": "Dupliquer (garder le moteur principal)",
    "Pad_TriggerRouting_Mode_Redirect": "Rediriger (couper le moteur principal)",
    "Pad_TriggerRouting_Scale": "Échelle",
    "Pad_TriggerRouting_Activator": "Activateur",
    "Pad_TriggerRouting_ActivatorMode": "Mode d'activation",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Toujours actif",
    "Pad_ResetTriggerRouteActivator": "Réinitialiser l'activateur",
 },
 "Strings.it.resx": {
    "MacroAction_Type_RumbleTrigger": "Override vibrazione grilletto",
    "MacroAction_RumbleTrigger_Tooltip": "Pilota la vibrazione del grilletto dello slot da una macro. Stesse modalità di mantenimento dell'Override vibrazione, ma l'intensità alimenta il canale del grilletto (grilletti a impulso Xbox e Vibrazione Grilletto Adattivo DualSense) anziché i motori. Si combina per massimo con l'instradamento del grilletto.",
    "MacroAction_Type_RumbleTriggerStop": "Ferma vibrazione grilletto",
    "MacroAction_RumbleTriggerStop": "Ferma vibrazione grilletto",
    "MacroAction_RumbleTriggerStop_Tooltip": "Rilascia qualsiasi vibrazione del grilletto da macro attiva sullo slot. Abbinare a un Override vibrazione grilletto persistente per terminare il mantenimento da una macro.",
    "Pad_TriggerRouting_Header": "Instradamento grilletti",
    "Pad_TriggerRouting_Description": "Instrada i motori di vibrazione principali nel canale del grilletto, un grilletto alla volta. Funziona con la vibrazione XInput e il force feedback. Un attivatore può condizionarlo in modo che il grilletto vibri solo mentre tieni premuto un pulsante.",
    "Pad_ResetTriggerRouting": "Ripristina instradamento grilletti",
    "Pad_TriggerRouting_LeftTrigger": "Grilletto sinistro",
    "Pad_TriggerRouting_RightTrigger": "Grilletto destro",
    "Pad_TriggerRouting_Source": "Sorgente",
    "Pad_TriggerRouting_Source_None": "Nessuna (disattivato)",
    "Pad_TriggerRouting_Source_MainLeft": "Motore sinistro",
    "Pad_TriggerRouting_Source_MainRight": "Motore destro",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Massimo dei due motori",
    "Pad_TriggerRouting_Source_SumOfBoth": "Somma dei due motori",
    "Pad_TriggerRouting_Mode": "Modalità",
    "Pad_TriggerRouting_Mode_Off": "Disattivato",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplica (mantieni motore principale)",
    "Pad_TriggerRouting_Mode_Redirect": "Reindirizza (silenzia motore principale)",
    "Pad_TriggerRouting_Scale": "Scala",
    "Pad_TriggerRouting_Activator": "Attivatore",
    "Pad_TriggerRouting_ActivatorMode": "Modalità di attivazione",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Sempre attivo",
    "Pad_ResetTriggerRouteActivator": "Ripristina attivatore",
 },
 "Strings.ja.resx": {
    "MacroAction_Type_RumbleTrigger": "トリガー振動のオーバーライド",
    "MacroAction_RumbleTrigger_Tooltip": "マクロからスロットのトリガー振動を駆動します。振動オーバーライドと同じホールドモードですが、強度はグリップモーターではなくトリガーチャンネル（Xbox インパルストリガーと DualSense アダプティブトリガー振動）に送られます。トリガールーティングと最大値で合成されます。",
    "MacroAction_Type_RumbleTriggerStop": "トリガー振動を停止",
    "MacroAction_RumbleTriggerStop": "トリガー振動を停止",
    "MacroAction_RumbleTriggerStop_Tooltip": "スロット上のアクティブなマクロトリガー振動を解除します。スティッキーなトリガー振動オーバーライドと組み合わせて、マクロからホールドを終了させます。",
    "Pad_TriggerRouting_Header": "トリガールーティング",
    "Pad_TriggerRouting_Description": "メインの振動モーターをトリガーチャンネルへ、トリガーごとに振り分けます。XInput 振動とフォースフィードバックに対応します。アクティベーターで制御すれば、ボタンを押している間だけトリガーが振動します。",
    "Pad_ResetTriggerRouting": "トリガールーティングをリセット",
    "Pad_TriggerRouting_LeftTrigger": "左トリガー",
    "Pad_TriggerRouting_RightTrigger": "右トリガー",
    "Pad_TriggerRouting_Source": "ソース",
    "Pad_TriggerRouting_Source_None": "なし（オフ）",
    "Pad_TriggerRouting_Source_MainLeft": "左モーター",
    "Pad_TriggerRouting_Source_MainRight": "右モーター",
    "Pad_TriggerRouting_Source_MaxOfBoth": "両モーターの最大値",
    "Pad_TriggerRouting_Source_SumOfBoth": "両モーターの合計",
    "Pad_TriggerRouting_Mode": "モード",
    "Pad_TriggerRouting_Mode_Off": "オフ",
    "Pad_TriggerRouting_Mode_Duplicate": "複製（メインモーターを維持）",
    "Pad_TriggerRouting_Mode_Redirect": "リダイレクト（メインモーターを無音化）",
    "Pad_TriggerRouting_Scale": "スケール",
    "Pad_TriggerRouting_Activator": "アクティベーター",
    "Pad_TriggerRouting_ActivatorMode": "アクティベーターモード",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "常にオン",
    "Pad_ResetTriggerRouteActivator": "アクティベーターをリセット",
 },
 "Strings.ko.resx": {
    "MacroAction_Type_RumbleTrigger": "트리거 진동 재정의",
    "MacroAction_RumbleTrigger_Tooltip": "매크로에서 슬롯의 트리거 진동을 구동합니다. 진동 재정의와 동일한 유지 모드이지만, 강도가 그립 모터 대신 트리거 채널(Xbox 임펄스 트리거 및 DualSense 적응형 트리거 진동)로 전달됩니다. 트리거 라우팅과 최댓값으로 결합됩니다.",
    "MacroAction_Type_RumbleTriggerStop": "트리거 진동 정지",
    "MacroAction_RumbleTriggerStop": "트리거 진동 정지",
    "MacroAction_RumbleTriggerStop_Tooltip": "슬롯에서 활성화된 매크로 트리거 진동을 해제합니다. 고정형 트리거 진동 재정의와 함께 사용하여 매크로로 유지를 종료합니다.",
    "Pad_TriggerRouting_Header": "트리거 라우팅",
    "Pad_TriggerRouting_Description": "메인 진동 모터를 트리거 채널로, 트리거별로 라우팅합니다. XInput 진동과 포스 피드백에서 작동합니다. 활성화기로 제어하면 버튼을 누르고 있는 동안에만 트리거가 진동합니다.",
    "Pad_ResetTriggerRouting": "트리거 라우팅 재설정",
    "Pad_TriggerRouting_LeftTrigger": "왼쪽 트리거",
    "Pad_TriggerRouting_RightTrigger": "오른쪽 트리거",
    "Pad_TriggerRouting_Source": "소스",
    "Pad_TriggerRouting_Source_None": "없음(끄기)",
    "Pad_TriggerRouting_Source_MainLeft": "왼쪽 모터",
    "Pad_TriggerRouting_Source_MainRight": "오른쪽 모터",
    "Pad_TriggerRouting_Source_MaxOfBoth": "두 모터의 최댓값",
    "Pad_TriggerRouting_Source_SumOfBoth": "두 모터의 합",
    "Pad_TriggerRouting_Mode": "모드",
    "Pad_TriggerRouting_Mode_Off": "끄기",
    "Pad_TriggerRouting_Mode_Duplicate": "복제(메인 모터 유지)",
    "Pad_TriggerRouting_Mode_Redirect": "리디렉션(메인 모터 음소거)",
    "Pad_TriggerRouting_Scale": "배율",
    "Pad_TriggerRouting_Activator": "활성화기",
    "Pad_TriggerRouting_ActivatorMode": "활성화 모드",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "항상 켜기",
    "Pad_ResetTriggerRouteActivator": "활성화기 재설정",
 },
 "Strings.nl.resx": {
    "MacroAction_Type_RumbleTrigger": "Triggervibratie overschrijven",
    "MacroAction_RumbleTrigger_Tooltip": "Stuurt de triggervibratie van het slot aan vanuit een macro. Dezelfde vasthoudmodi als Vibratie overschrijven, maar de sterkte voedt het triggerkanaal (Xbox-impulstriggers en DualSense Adaptieve Triggervibratie) in plaats van de grijpmotoren. Wordt per maximum gecombineerd met de triggerroutering.",
    "MacroAction_Type_RumbleTriggerStop": "Triggervibratie stoppen",
    "MacroAction_RumbleTriggerStop": "Triggervibratie stoppen",
    "MacroAction_RumbleTriggerStop_Tooltip": "Heft een actieve macro-triggervibratie op het slot op. Combineer met een vasthoudende Triggervibratie overschrijven om het vasthouden vanuit een macro te beëindigen.",
    "Pad_TriggerRouting_Header": "Triggerroutering",
    "Pad_TriggerRouting_Description": "Routeert de hoofdvibratiemotoren naar het triggerkanaal, per trigger. Werkt met XInput-vibratie en force feedback. Een activator kan dit beperken zodat de trigger alleen trilt zolang je een knop ingedrukt houdt.",
    "Pad_ResetTriggerRouting": "Triggerroutering resetten",
    "Pad_TriggerRouting_LeftTrigger": "Linkertrigger",
    "Pad_TriggerRouting_RightTrigger": "Rechtertrigger",
    "Pad_TriggerRouting_Source": "Bron",
    "Pad_TriggerRouting_Source_None": "Geen (uit)",
    "Pad_TriggerRouting_Source_MainLeft": "Linkermotor",
    "Pad_TriggerRouting_Source_MainRight": "Rechtermotor",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Maximum van beide motoren",
    "Pad_TriggerRouting_Source_SumOfBoth": "Som van beide motoren",
    "Pad_TriggerRouting_Mode": "Modus",
    "Pad_TriggerRouting_Mode_Off": "Uit",
    "Pad_TriggerRouting_Mode_Duplicate": "Dupliceren (hoofdmotor behouden)",
    "Pad_TriggerRouting_Mode_Redirect": "Omleiden (hoofdmotor dempen)",
    "Pad_TriggerRouting_Scale": "Schaal",
    "Pad_TriggerRouting_Activator": "Activator",
    "Pad_TriggerRouting_ActivatorMode": "Activatormodus",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Altijd aan",
    "Pad_ResetTriggerRouteActivator": "Activator resetten",
 },
 "Strings.pt-BR.resx": {
    "MacroAction_Type_RumbleTrigger": "Substituir Vibração do Gatilho",
    "MacroAction_RumbleTrigger_Tooltip": "Controla a vibração do gatilho do slot a partir de uma macro. Mesmos modos de retenção da Substituição de Vibração, mas a intensidade alimenta o canal do gatilho (gatilhos de impulso Xbox e Vibração de Gatilho Adaptável DualSense) em vez dos motores. Combina por máximo com o roteamento de gatilho.",
    "MacroAction_Type_RumbleTriggerStop": "Parar Vibração do Gatilho",
    "MacroAction_RumbleTriggerStop": "Parar vibração do gatilho",
    "MacroAction_RumbleTriggerStop_Tooltip": "Libera qualquer vibração de gatilho por macro ativa no slot. Combine com uma Substituição de Vibração do Gatilho fixa para encerrar a retenção a partir de uma macro.",
    "Pad_TriggerRouting_Header": "Roteamento de Gatilhos",
    "Pad_TriggerRouting_Description": "Roteia os motores de vibração principais para o canal do gatilho, um gatilho por vez. Funciona com vibração XInput e force feedback. Um ativador pode condicioná-lo para que o gatilho só vibre enquanto você segura um botão.",
    "Pad_ResetTriggerRouting": "Redefinir roteamento de gatilhos",
    "Pad_TriggerRouting_LeftTrigger": "Gatilho esquerdo",
    "Pad_TriggerRouting_RightTrigger": "Gatilho direito",
    "Pad_TriggerRouting_Source": "Fonte",
    "Pad_TriggerRouting_Source_None": "Nenhuma (desligado)",
    "Pad_TriggerRouting_Source_MainLeft": "Motor esquerdo",
    "Pad_TriggerRouting_Source_MainRight": "Motor direito",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Máximo dos dois motores",
    "Pad_TriggerRouting_Source_SumOfBoth": "Soma dos dois motores",
    "Pad_TriggerRouting_Mode": "Modo",
    "Pad_TriggerRouting_Mode_Off": "Desligado",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplicar (manter motor principal)",
    "Pad_TriggerRouting_Mode_Redirect": "Redirecionar (silenciar motor principal)",
    "Pad_TriggerRouting_Scale": "Escala",
    "Pad_TriggerRouting_Activator": "Ativador",
    "Pad_TriggerRouting_ActivatorMode": "Modo de ativação",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Sempre ligado",
    "Pad_ResetTriggerRouteActivator": "Redefinir ativador",
 },
 "Strings.zh-Hans.resx": {
    "MacroAction_Type_RumbleTrigger": "覆盖扳机振动",
    "MacroAction_RumbleTrigger_Tooltip": "通过宏驱动插槽的扳机振动。保持模式与覆盖振动相同，但强度馈送到扳机通道（Xbox 脉冲扳机和 DualSense 自适应扳机振动），而非握把马达。与扳机路由按最大值合成。",
    "MacroAction_Type_RumbleTriggerStop": "停止扳机振动",
    "MacroAction_RumbleTriggerStop": "停止扳机振动",
    "MacroAction_RumbleTriggerStop_Tooltip": "释放插槽上任何活动的宏扳机振动。与粘滞的覆盖扳机振动搭配，以通过宏结束保持。",
    "Pad_TriggerRouting_Header": "扳机路由",
    "Pad_TriggerRouting_Description": "将主振动马达路由到扳机通道，每个扳机单独设置。支持 XInput 振动和力反馈。激活器可对其进行门控，使扳机仅在按住按钮时振动。",
    "Pad_ResetTriggerRouting": "重置扳机路由",
    "Pad_TriggerRouting_LeftTrigger": "左扳机",
    "Pad_TriggerRouting_RightTrigger": "右扳机",
    "Pad_TriggerRouting_Source": "来源",
    "Pad_TriggerRouting_Source_None": "无（关闭）",
    "Pad_TriggerRouting_Source_MainLeft": "左马达",
    "Pad_TriggerRouting_Source_MainRight": "右马达",
    "Pad_TriggerRouting_Source_MaxOfBoth": "两马达的最大值",
    "Pad_TriggerRouting_Source_SumOfBoth": "两马达之和",
    "Pad_TriggerRouting_Mode": "模式",
    "Pad_TriggerRouting_Mode_Off": "关闭",
    "Pad_TriggerRouting_Mode_Duplicate": "复制（保留主马达）",
    "Pad_TriggerRouting_Mode_Redirect": "重定向（静音主马达）",
    "Pad_TriggerRouting_Scale": "缩放",
    "Pad_TriggerRouting_Activator": "激活器",
    "Pad_TriggerRouting_ActivatorMode": "激活模式",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "始终开启",
    "Pad_ResetTriggerRouteActivator": "重置激活器",
 },
}

def read_text(p):
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    return raw.decode("utf-8-sig"), bom

def write_text(p, text, bom):
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)

def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

def insert_after_anchor(text, anchor_key, new_keys, values):
    pat = re.compile(
        rf'(  <data name="{re.escape(anchor_key)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)')
    m = pat.search(text)
    if not m:
        return text, None
    lines = []
    for k in new_keys:
        if f'<data name="{k}"' in text:
            continue
        v = xml_escape(values.get(k, EN[k]))
        lines.append(f'  <data name="{k}" xml:space="preserve"><value>{v}</value></data>\n')
    if not lines:
        return text, 0
    return text[:m.end()] + "".join(lines) + text[m.end():], len(lines)

# 1) resx files: neutral (EN) + 9 locales.
files = {"Strings.resx": EN}
files.update(TR)
for fname, values in files.items():
    p = ROOT / fname
    text, bom = read_text(p)
    changed = 0
    for anchor, keys in [("MacroAction_RumbleStop_Tooltip", MACRO_KEYS),
                         ("Pad_ConstantTriggerForce_Header", ROUTE_KEYS)]:
        text, n = insert_after_anchor(text, anchor, keys, values)
        if n is None:
            print(f"FAIL {fname}: anchor {anchor} not found")
            changed = None
            break
        changed = (changed or 0) + n
    if changed:
        write_text(p, text, bom)
    print(f"OK   {fname}  (+{changed})")

# 2) Designer: append instance properties (one file, neutral lookups).
dtext, dbom = read_text(DESIGNER)
def add_designer(dtext, anchor_prop, keys):
    anchor_line = f'    public string {anchor_prop} => Get("{anchor_prop}");'
    idx = dtext.find(anchor_line)
    if idx < 0:
        print(f"FAIL Designer: anchor {anchor_prop} not found")
        return dtext
    insert_at = idx + len(anchor_line)
    lines = []
    for k in keys:
        if f'Get("{k}")' in dtext:
            continue
        lines.append(f'\r\n    public string {k} => Get("{k}");')
    if not lines:
        return dtext
    return dtext[:insert_at] + "".join(lines) + dtext[insert_at:]

dtext = add_designer(dtext, "MacroAction_RumbleStop_Tooltip", MACRO_KEYS)
dtext = add_designer(dtext, "Pad_ConstantTriggerForce_Header", ROUTE_KEYS)
write_text(DESIGNER, dtext, dbom)
print("OK   Strings.Designer.cs")
