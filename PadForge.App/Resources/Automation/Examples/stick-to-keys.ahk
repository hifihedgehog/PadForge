; The left stick as W, A, S and D, for a game that takes only keys. Each
; direction is held down while the stick is past 50% that way. Needs Read.
#Requires AutoHotkey v2.0
#Include <PadForge>

pad := PadForge.Device("gamepad")

Bind(line, key) {
    ; "~" leaves the stick to the game as well.
    pad.Hotkey("~" line, (*) => Send("{" key " down}"))
    pad.Hotkey("~" line " up", (*) => Send("{" key " up}"))
}
Bind("LeftY < -50%", "w")
Bind("LeftY > 50%", "s")
Bind("LeftX < -50%", "a")
Bind("LeftX > 50%", "d")

Persistent
