"""#102 follow-up: Title-Case the English routing-card labels + reset tooltips
to match the app convention ("Left Trigger", "Reset Left Trigger"), and add the
4 new per-option reset tooltips (Source / Mode / Scale / Activator Mode) across
all 10 locales + the Designer. English casing only (translations keep their own
conventions: German nouns already capitalized, Romance UI is sentence case)."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent / "PadForge.App" / "Resources" / "Strings"
DESIGNER = ROOT / "Strings.Designer.cs"

# English Title-Case corrections (neutral Strings.resx only).
EN_FIX = {
    "Pad_ResetTriggerRouting": "Reset Trigger Routing",
    "Pad_ResetTriggerRouteActivator": "Reset Activator",
    "Pad_TriggerRouting_LeftTrigger": "Left Trigger",
    "Pad_TriggerRouting_RightTrigger": "Right Trigger",
    "Pad_TriggerRouting_Source_None": "None (Off)",
    "Pad_TriggerRouting_Source_MainLeft": "Left Motor",
    "Pad_TriggerRouting_Source_MainRight": "Right Motor",
    "Pad_TriggerRouting_Source_MaxOfBoth": "Max of Both Motors",
    "Pad_TriggerRouting_Source_SumOfBoth": "Sum of Both Motors",
    "Pad_TriggerRouting_Mode_Duplicate": "Duplicate (Keep Main Motor)",
    "Pad_TriggerRouting_Mode_Redirect": "Redirect (Silence Main Motor)",
    "Pad_TriggerRouting_ActivatorMode": "Activator Mode",
    "Pad_TriggerRouting_ActivatorMode_AlwaysOn": "Always On",
}

NEW_KEYS = [
    "Pad_ResetTriggerRouteSource",
    "Pad_ResetTriggerRouteMode",
    "Pad_ResetTriggerRouteScale",
    "Pad_ResetTriggerRouteActivatorMode",
]
NEW = {
    "Strings.resx":        ["Reset Source", "Reset Mode", "Reset Scale", "Reset Activator Mode"],
    "Strings.de.resx":     ["Quelle zurücksetzen", "Modus zurücksetzen", "Skalierung zurücksetzen", "Aktivierungsmodus zurücksetzen"],
    "Strings.es.resx":     ["Restablecer fuente", "Restablecer modo", "Restablecer escala", "Restablecer modo de activación"],
    "Strings.fr.resx":     ["Réinitialiser la source", "Réinitialiser le mode", "Réinitialiser l'échelle", "Réinitialiser le mode d'activation"],
    "Strings.it.resx":     ["Ripristina sorgente", "Ripristina modalità", "Ripristina scala", "Ripristina modalità di attivazione"],
    "Strings.ja.resx":     ["ソースをリセット", "モードをリセット", "スケールをリセット", "アクティベーターモードをリセット"],
    "Strings.ko.resx":     ["소스 재설정", "모드 재설정", "배율 재설정", "활성화 모드 재설정"],
    "Strings.nl.resx":     ["Bron resetten", "Modus resetten", "Schaal resetten", "Activatormodus resetten"],
    "Strings.pt-BR.resx":  ["Redefinir fonte", "Redefinir modo", "Redefinir escala", "Redefinir modo de ativação"],
    "Strings.zh-Hans.resx":["重置来源", "重置模式", "重置缩放", "重置激活模式"],
}
ANCHOR = "Pad_ResetTriggerRouteActivator"

def read_text(p):
    raw = p.read_bytes()
    return raw.decode("utf-8-sig"), raw.startswith(b"\xef\xbb\xbf")

def write_text(p, text, bom):
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))

def xesc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

# 1) English casing fixes (neutral only).
neutral = ROOT / "Strings.resx"
ntext, nbom = read_text(neutral)
fixed = 0
for k, v in EN_FIX.items():
    pat = re.compile(r'(<data name="' + re.escape(k) + r'"[^>]*>\s*<value>)(.*?)(</value>)', re.S)
    def repl(m):
        return m.group(1) + xesc(v) + m.group(3)
    ntext, n = pat.subn(repl, ntext)
    fixed += n
write_text(neutral, ntext, nbom)
print(f"Strings.resx casing fixes: {fixed}/{len(EN_FIX)}")

# 2) Add 4 new reset tooltips to all 10 locales after the anchor.
for fname, vals in NEW.items():
    p = ROOT / fname
    text, bom = read_text(p)
    m = re.search(r'(  <data name="' + re.escape(ANCHOR) + r'"[^>]*><value>[^<]*</value></data>\s*\n)', text)
    if not m:
        print(f"FAIL {fname}: anchor not found")
        continue
    lines = []
    for k, v in zip(NEW_KEYS, vals):
        if f'<data name="{k}"' in text:
            continue
        lines.append(f'  <data name="{k}" xml:space="preserve"><value>{xesc(v)}</value></data>\n')
    if lines:
        text = text[:m.end()] + "".join(lines) + text[m.end():]
        write_text(p, text, bom)
    print(f"OK   {fname}  (+{len(lines)})")

# 3) Designer props after the anchor prop.
dtext, dbom = read_text(DESIGNER)
anchor_line = f'    public string {ANCHOR} => Get("{ANCHOR}");'
idx = dtext.find(anchor_line)
if idx < 0:
    print("FAIL Designer anchor")
else:
    insert_at = idx + len(anchor_line)
    props = "".join(f'\r\n    public string {k} => Get("{k}");' for k in NEW_KEYS if f'Get("{k}")' not in dtext)
    dtext = dtext[:insert_at] + props + dtext[insert_at:]
    write_text(DESIGNER, dtext, dbom)
    print("OK   Designer")
