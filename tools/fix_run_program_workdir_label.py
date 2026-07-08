"""Rename the Run Program working-folder label from "Folder" to the precise
"Working directory" (localized) and add a clarifying tooltip. "Folder" read
as if the user had to point it at the program's own folder. Idempotent,
BOM-preserving. Mirrors add_run_program_strings.py."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"

LOCALES = ["Strings.resx", "Strings.de.resx", "Strings.es.resx", "Strings.fr.resx",
           "Strings.it.resx", "Strings.ja.resx", "Strings.ko.resx", "Strings.nl.resx",
           "Strings.pt-BR.resx", "Strings.zh-Hans.resx"]

# [en, de, es, fr, it, ja, ko, nl, pt-BR, zh-Hans]
WORKDIR = ["Working directory", "Arbeitsverzeichnis", "Directorio de trabajo",
           "Répertoire de travail", "Directory di lavoro", "作業ディレクトリ",
           "작업 디렉터리", "Werkmap", "Diretório de trabalho", "工作目录"]

TOOLTIP = [
    "Optional. The folder the program uses as its current directory. Not the folder the program file is in. Leave blank for the default.",
    "Optional. Der Ordner, den das Programm als aktuelles Verzeichnis verwendet. Nicht der Ordner, in dem die Programmdatei liegt. Leer lassen für den Standard.",
    "Opcional. La carpeta que el programa usa como directorio actual. No es la carpeta donde está el archivo del programa. Déjalo en blanco para usar el valor predeterminado.",
    "Facultatif. Le dossier que le programme utilise comme répertoire courant. Ce n'est pas le dossier où se trouve le fichier du programme. Laissez vide pour la valeur par défaut.",
    "Facoltativo. La cartella che il programma usa come directory corrente. Non è la cartella in cui si trova il file del programma. Lascia vuoto per il valore predefinito.",
    "任意。プログラムが現在のディレクトリとして使用するフォルダーです。プログラムファイルがある場所ではありません。空欄にすると既定値が使われます。",
    "선택 사항. 프로그램이 현재 디렉터리로 사용하는 폴더입니다. 프로그램 파일이 있는 폴더가 아닙니다. 비워 두면 기본값이 사용됩니다.",
    "Optioneel. De map die het programma als huidige map gebruikt. Niet de map waarin het programmabestand staat. Laat leeg voor de standaardwaarde.",
    "Opcional. A pasta que o programa usa como diretório atual. Não é a pasta onde o arquivo do programa está. Deixe em branco para usar o padrão.",
    "可选。程序用作当前目录的文件夹。不是程序文件所在的文件夹。留空则使用默认值。",
]

KEY = "MacroAction_RunProgram_WorkingDir"
TIP_KEY = "MacroAction_RunProgram_WorkingDir_Tooltip"


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


for li, fname in enumerate(LOCALES):
    p = ROOT / fname
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    changed = False

    # 1. Rewrite the WorkingDir label value in place.
    new_val = xml_escape(WORKDIR[li])
    pat = re.compile(
        rf'(<data name="{re.escape(KEY)}" xml:space="preserve"><value>)[^<]*(</value></data>)')
    new_text, n = pat.subn(rf'\g<1>{new_val}\g<2>', text)
    if n == 1 and new_text != text:
        text = new_text
        changed = True

    # 2. Add the tooltip key right after the WorkingDir label, if absent.
    if f'<data name="{TIP_KEY}"' not in text:
        anchor = re.search(
            rf'(  <data name="{re.escape(KEY)}" xml:space="preserve"><value>[^<]*</value></data>\s*\n)',
            text)
        if anchor:
            ins = (f'  <data name="{TIP_KEY}" xml:space="preserve"><value>'
                   f'{xml_escape(TOOLTIP[li])}</value></data>\n')
            text = text[:anchor.end()] + ins + text[anchor.end():]
            changed = True
        else:
            print(f"WARN {fname}: anchor {KEY} not found for tooltip insert")

    if not changed:
        print(f"OK   {fname}  (already current)")
        continue
    out = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    p.write_bytes(out)
    print(f"OK   {fname}  (label + tooltip)")
