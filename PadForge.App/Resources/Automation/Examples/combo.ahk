; A combo on one button: Back plays A, B, then X, each held 50 ms, timed on
; PadForge's own clock. Needs Send Inputs.
#Requires AutoHotkey v2.0
#Include <PadForge>

pad := PadForge.Device("gamepad")
pad.Hotkey("Back", (*) => pad.Sequence("A", 50, "B", 50, "X", 50))

; The left trigger, pulled past 90%, holds the right one full until it
; comes back.
pad.Hotkey("~LeftTrigger > 90%", (*) => pad.RightTrigger := 100)
pad.Hotkey("~LeftTrigger > 90% up", (*) => pad.RightTrigger := "")

Persistent
