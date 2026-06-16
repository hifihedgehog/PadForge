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
for binding, cmd, tip in INSERTS:
    if f"{cmd}}}" in text:   # idempotent
        continue
    bidx = text.find(binding)
    if bidx < 0:
        print(f"FAIL: binding not found: {binding}")
        continue
    cidx = text.find(row_close, bidx)
    if cidx < 0:
        print(f"FAIL: row close not found after {binding}")
        continue
    text = text[:cidx] + btn(cmd, tip) + text[cidx:]
    done += 1

open(p, "wb").write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
print(f"Inserted {done}/8 reset buttons.")
