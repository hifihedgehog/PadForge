; A PadForge plugin in AutoHotkey v2, from the PadForge Plugin Kit.
;
; To run it, keep this file beside PadForge.ahk, which the kit carries too,
; and add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this
; file. PadForge shows the file, its signer and its SHA-256, and runs it with
; your ordinary rights whenever the engine runs, in the AutoHotkey Windows
; opens .ahk files with. An edited file is a new file, which PadForge asks
; about again before it runs it.
;
; What it does: it connects as a plugin with the one-time key PadForge put in
; its environment, declares a controller with two buttons, a stick and
; rumble, moves the stick around a circle and taps a button once a second,
; writes to its log on the Plugins page, and shows the feedback games send in
; a tooltip. Map the controller on PadForge's Devices page like any other.

#Requires AutoHotkey v2.0
#SingleInstance Off
Persistent
#Include PadForge.ahk

pf := PadForge.Plugin("Kit Plugin")
pad := pf.AddController("kitpad", "Kit Pad", ["Fire", "Jump"], [{name: "Stick X"}, {name: "Stick Y"}], [], [], ["rumble"])
pad.OnRumble((low, high) => ToolTip("rumble " low " " high))
pf.Log("Declared Kit Pad as " pad.Id ".")

start := A_TickCount
Move() {
    global pf, pad, start
    if pf.Closed
        ExitApp()
    t := (A_TickCount - start) / 1000
    pad.Set("a0", Round(32768 + 32767 * Cos(t * 3.141592653589793)))
    pad.Set("a1", Round(32768 + 32767 * Sin(t * 3.141592653589793)))
    pad.Set("b0", Mod(t, 1.0) < 0.2 ? 1 : 0)
    pad.Send()
}
SetTimer(Move, 20)
