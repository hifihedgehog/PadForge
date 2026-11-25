; The right stick moves the mouse, faster the further it goes, and the
; right shoulder clicks. A read of the stick costs nothing: the library
; keeps the pad's whole state. Needs Send Inputs, since the shoulder is
; taken from the game.
#Requires AutoHotkey v2.0
#Include <PadForge>

pad := PadForge.Device("gamepad")
pad.Hotkey("RightShoulder", (*) => Click())

SetTimer(Aim, 10)
Aim() {
    x := pad.RightX, y := pad.RightY
    if (x = "")                 ; not connected
        return
    if (Abs(x) > 10 || Abs(y) > 10)
        MouseMove(x * Abs(x) / 500, y * Abs(y) / 500, 0, "R")
}

Persistent
