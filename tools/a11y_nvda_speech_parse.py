"""Prints what NVDA spoke during a tools/a11y_nvda_tab_pass.ps1 run.

Usage: python a11y_nvda_speech_parse.py <nvda-io log>

NVDA's IO log (level 12) records each utterance as a "Speaking [...]" line.
Each distinct utterance prints once, in order, its fields joined by " | ".
A private-use glyph shows as the escape NVDA logged, such as \\ue700.
"""
import re
import sys

# The log holds any character a name can, and a console's own code page does not.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
path = sys.argv[1]
text = open(path, encoding="utf-8", errors="replace").read()
events = []
for line in text.splitlines():
    m = re.search(r"Speaking (\[.*\])\s*$", line)
    if not m:
        continue
    strings = re.findall(r"'([^']*)'", m.group(1))
    strings = [s for s in strings if s not in ("en",) and not s.startswith("say-all")]
    if strings:
        events.append(" | ".join(strings))

dedup = []
for e in events:
    if not dedup or dedup[-1] != e:
        dedup.append(e)

print("speech events:", len(events), "| distinct in order:", len(dedup))
for i, e in enumerate(dedup):
    print(f"{i:03d} {e[:240]}")
