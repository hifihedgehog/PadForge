; Tap and turbo: A taps B for the game, and while X is held the game sees
; Y pressed and let go ten times a second. Needs Send Inputs.
#Requires AutoHotkey v2.0
#Include <PadForge>

pad := PadForge.Device("gamepad")

; A belongs to the script: the game never sees it, and sees B tapped.
pad.Hotkey("A", (*) => pad.Tap("B", 60))

; X stays the game's too ("~"), and drives Y while it is held.
pad.Hotkey("~X", (*) => SetTimer(Turbo, 100))
pad.Hotkey("~X up", (*) => SetTimer(Turbo, 0))
Turbo() => pad.Tap("Y", 40)

Persistent
