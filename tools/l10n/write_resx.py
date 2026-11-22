"""Writes Strings.<tag>.resx from a translation, in the layout of Strings.resx.

usage: python tools/l10n/write_resx.py TAG TRANSLATION.json

TRANSLATION.json is one JSON object that maps resource keys to translated text. The new
file is Strings.resx line for line: the same header, the same section comments and the same
key order, each value replaced by its translation, with the byte order mark and the CRLF line
endings of Strings.resx. A key with no translation is left out, so it shows in English.
Nothing is written when the translation breaks a rule in rules.py. Add the language's row to
Languages.tsv first.
"""
import io
import json
import os
import re
import sys
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import rules  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")
if len(sys.argv) != 3:
    sys.exit(__doc__)
tag, source_path = sys.argv[1], sys.argv[2]
table = rules.languages()
if tag not in table:
    sys.exit("%s has no row in Languages.tsv" % tag)
translation = json.load(io.open(source_path, encoding="utf-8-sig"))
english = rules.read_resx(os.path.join(rules.STRINGS, "Strings.resx"))
problems = rules.check(english, translation, table[tag][3])
if problems:
    for kind, key, message in problems:
        print("%s %s: %s" % (kind, key, message))
    sys.exit("%d problem(s). Nothing written." % len(problems))

raw = io.open(os.path.join(rules.STRINGS, "Strings.resx"), "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
base = raw.decode("utf-8-sig")
# An entry sits on one line or spreads over three, so entries are matched in the whole text. A value
# never runs past its own </value>, and an English entry may carry a translator's <comment> after it,
# which a translation leaves out, as every translation in the tree does. A value that could run on
# let a commented entry reach the next entry's </value> and swallow that entry (#590).
ENTRY = re.compile(r'(  <data name="([^"]+)" xml:space="preserve">\s*<value>)((?:(?!</value>).)*)(</value>)'
                   r'(\s*<comment>(?:(?!</comment>).)*</comment>)?(\s*</data>\r?\n?)', re.S)
kept = [0]


def replace(m):
    key = m.group(2)
    if key not in translation:
        return ""
    kept[0] += 1
    return m.group(1) + escape(translation[key]) + m.group(4) + m.group(6)


out = ENTRY.sub(replace, base)
target = os.path.join(rules.STRINGS, "Strings.%s.resx" % tag)
io.open(target, "wb").write((b"\xef\xbb\xbf" if bom else b"") + out.encode("utf-8"))
back = rules.read_resx(target)
if back != translation:
    lost = sorted(set(translation) - set(back))
    sys.exit("written %s, but it reads back %d of %d strings: %s" % (target, len(back), len(translation), lost[:8]))
print("written %s: %d of %d strings" % (os.path.relpath(target, rules.REPO), kept[0], len(english)))
