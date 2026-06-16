"""Move the #102 Trigger Routing card from the Impulse Triggers tab to the
Force Feedback tab (where the recipe + the user expect it). Marker-based so
the ~180-line block moves intact."""
p = "PadForge.App/Views/PadPage.xaml"
raw = open(p, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
nl = "\r\n" if "\r\n" in text else "\n"
lines = text.split(nl)

# 1) Extract the card block (Trigger Routing comment .. just before Audio Bass comment).
i = next(n for n, l in enumerate(lines) if "Trigger Routing section (issue #102)" in l)
j = next(n for n, l in enumerate(lines) if "Audio Bass Trigger Rumble section" in l)
card = lines[i:j]
while card and card[-1].strip() == "":   # drop trailing blank lines
    card.pop()
del lines[i:j]

# 2) Find the Force Feedback tab end (just before the TAB 6: ADAPTIVE TRIGGERS comment).
k = next(n for n, l in enumerate(lines) if "TAB 6: ADAPTIVE TRIGGERS" in l)
t = k
while "</TabItem>" not in lines[t]:
    t -= 1
assert "</ScrollViewer>" in lines[t - 1], lines[t - 1]
assert "</StackPanel>" in lines[t - 2], lines[t - 2]
ins = t - 2   # insert before the FFB content StackPanel close

newlines = lines[:ins] + [""] + card + [""] + lines[ins:]
out = (b"\xef\xbb\xbf" if bom else b"") + nl.join(newlines).encode("utf-8")
open(p, "wb").write(out)
print(f"Moved {len(card)} lines. Card now before FFB </StackPanel> at new line {ins+2}.")
