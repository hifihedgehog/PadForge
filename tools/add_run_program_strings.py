"""Add the Run Program macro-action strings to all 10 locales. Idempotent,
BOM-preserving. Mirrors add_bt_disconnect_strings.py. Anchored after
MacroDisconnect_Caveat so the RunProgram keys sit with the other macro-action
strings."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# key -> [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
KEYS = {
    "MacroAction_Type_RunProgram": ["Run Program", "Programm ausführen", "Ejecutar programa", "Exécuter un programme", "Esegui programma", "プログラムを実行", "프로그램 실행", "Programma uitvoeren", "Executar Programa", "运行程序"],
    "MacroAction_RunProgram_Tooltip": [
        "Launch a program or file with optional arguments. You choose what runs.",
        "Startet ein Programm oder eine Datei mit optionalen Argumenten. Sie wählen, was ausgeführt wird.",
        "Inicia un programa o archivo con argumentos opcionales. Tú eliges qué se ejecuta.",
        "Lance un programme ou un fichier avec des arguments facultatifs. Vous choisissez ce qui s'exécute.",
        "Avvia un programma o un file con argomenti facoltativi. Scegli tu cosa viene eseguito.",
        "任意の引数を指定してプログラムやファイルを起動します。実行する対象はユーザーが選びます。",
        "선택적 인수를 사용하여 프로그램이나 파일을 실행합니다. 무엇을 실행할지는 사용자가 선택합니다.",
        "Start een programma of bestand met optionele argumenten. U kiest wat er wordt uitgevoerd.",
        "Inicia um programa ou arquivo com argumentos opcionais. Você escolhe o que é executado.",
        "使用可选参数启动程序或文件。运行内容由你选择。"],
    "MacroAction_RunProgram_Format": ["Run {0}", "{0} ausführen", "Ejecutar {0}", "Exécuter {0}", "Esegui {0}", "{0} を実行", "{0} 실행", "{0} uitvoeren", "Executar {0}", "运行 {0}"],
    "MacroAction_RunProgram_NoProgram": ["(no program)", "(kein Programm)", "(sin programa)", "(aucun programme)", "(nessun programma)", "(プログラムなし)", "(프로그램 없음)", "(geen programma)", "(nenhum programa)", "(无程序)"],
    "MacroAction_RunProgram_Path": ["Program", "Programm", "Programa", "Programme", "Programma", "プログラム", "프로그램", "Programma", "Programa", "程序"],
    "MacroAction_RunProgram_Args": ["Arguments", "Argumente", "Argumentos", "Arguments", "Argomenti", "引数", "인수", "Argumenten", "Argumentos", "参数"],
    "MacroAction_RunProgram_WorkingDir": ["Folder", "Ordner", "Carpeta", "Dossier", "Cartella", "フォルダー", "폴더", "Map", "Pasta", "文件夹"],
}

ANCHOR = "MacroDisconnect_Caveat"


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
