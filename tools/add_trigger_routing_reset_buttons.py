"""#102 follow-up: add a per-option reset button (Source / Mode / Scale /
Activator Mode, both triggers) to the Trigger Routing card. Keyed on each
control's unique binding; the button is inserted before that row's closing
</StackPanel>. The Activator picker already has its own reset."""
p = "PadForge.App/Views/PadPage.xaml"
raw = open(p, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
nl = "\r\n" if "\r\n" in text else "\n"
row_close = nl + "                                </StackPanel>"   # 32-space row close

def btn(cmd, tip):
    return (nl + '                                    <Button Command="{Binding ' + cmd + '}" Style="{StaticResource ResetButton}"'
            + nl + '                                            ToolTip="{Binding ' + tip + ', Source={x:Static strings:Strings.Instance}}"/>')

INSERTS = [
    ("LeftTriggerRouteSource, Mode=TwoWay",          "ResetLeftTriggerRouteSourceCommand",         "Pad_ResetTriggerRouteSource"),
    ("LeftTriggerRouteMode, Mode=TwoWay",            "ResetLeftTriggerRouteModeCommand",           "Pad_ResetTriggerRouteMode"),
    ("LeftTriggerRouteScale, Mode=TwoWay}",          "ResetLeftTriggerRouteScaleCommand",          "Pad_ResetTriggerRouteScale"),
    ("LeftTriggerRouteActivatorMode, Mode=TwoWay",   "ResetLeftTriggerRouteActivatorModeCommand",  "Pad_ResetTriggerRouteActivatorMode"),
    ("RightTriggerRouteSource, Mode=TwoWay",         "ResetRightTriggerRouteSourceCommand",        "Pad_ResetTriggerRouteSource"),
    ("RightTriggerRouteMode, Mode=TwoWay",           "ResetRightTriggerRouteModeCommand",          "Pad_ResetTriggerRouteMode"),
    ("RightTriggerRouteScale, Mode=TwoWay}",         "ResetRightTriggerRouteScaleCommand",         "Pad_ResetTriggerRouteScale"),
    ("RightTriggerRouteActivatorMode, Mode=TwoWay",  "ResetRightTriggerRouteActivatorModeCommand", "Pad_ResetTriggerRouteActivatorMode"),
]

done = 0
missed = []
for binding, cmd, tip in INSERTS:
    if f"{cmd}}}" in text:   # idempotent
        continue
    bidx = text.find(binding)
    if bidx < 0:
        missed.append(f"binding not found: {binding}")
        continue
    cidx = text.find(row_close, bidx)
    if cidx < 0:
        missed.append(f"row close not found after {binding}")
        continue
    text = text[:cidx] + btn(cmd, tip) + text[cidx:]
    done += 1

# Fail SAFE: write nothing when an anchor missed. This used to write
# unconditionally, so a partial run left PadForge.xaml half-patched on disk
# while printing "Inserted 3/8" as though that were a status rather than
# damage. Its sibling move_trigger_routing_card.py already gets this right
# (its next()/assert lookups raise before the single write), and it is the
# rule the project's resx recipe states outright: count anchors, write nothing
# when an anchor misses.
if missed:
    for m in missed:
        print(f"FAIL: {m}")
    raise SystemExit(
        f"Aborted without writing: {len(missed)} anchor(s) missed, "
        f"{done} insert(s) discarded. The file is unchanged.")

if done == 0:
    print("Nothing to do: all 8 reset buttons already present.")
else:
    open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
    print(f"Inserted {done}/8 reset buttons.")
