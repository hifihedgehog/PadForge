; A PadForge plugin in AutoHotkey v2, from the PadForge Plugin Kit.
;
; To run it, keep this file beside PadForge.ahk, which the kit carries too,
; and add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this
; file, or drop it on that page. PadForge shows the file, its signer and its
; SHA-256, and runs it with your ordinary rights whenever the engine runs, in
; the AutoHotkey Windows opens .ahk files with. An edited file shows Approve
; and Restart on its card.
;
; What it does: it connects as a plugin with the one-time key PadForge put in
; its environment, declares a controller with two buttons, a stick and
; rumble, and four settings PadForge draws on its card: Speed, Tap Fire,
; Center and Turns. It moves the stick around a circle at the Speed set, taps
; Fire once a second while Tap Fire is on, counts its turns in Turns, writes
; to its log on the Plugins page, and shows the feedback games send in a
; tooltip. Map the controller on PadForge's Devices page like any other.
; README.md lists every verb, event and kind of setting.

#Requires AutoHotkey v2.0
#SingleInstance Off
Persistent
#Include PadForge.ahk

pf := PadForge.Plugin("Kit Plugin")
pad := pf.AddController("kitpad", "Kit Pad", ["Fire", "Jump"], [{name: "Stick X"}, {name: "Stick Y"}], [], [], ["rumble"])
pad.OnRumble((low, high) => ToolTip("rumble " low " " high))
pf.Log("Declared Kit Pad as " pad.Id ".")

; What the user sets on the plugin's card. Each declaration's answer carries
; the value set in an earlier run, and a change arrives as an event.
speed := 1.0
tapFire := true
centered := false

OnSetting(pairs) {
    global speed, tapFire, pf
    key := pairs.Has("key") ? pairs["key"] : ""
    value := pairs.Has("value") ? pairs["value"] : ""
    if (key = "speed")
        speed := Number(value)
    else if (key = "fire")
        tapFire := value = "true"
    else
        return
    pf.Log(key " is " value ".")
}

OnPress(pairs) {
    global centered
    if (pairs.Has("key") && pairs["key"] = "center")
        centered := true
}

pf.OnEvent("setting", OnSetting)
pf.OnEvent("press", OnPress)
speed := Number(pf.Request('setting key=speed kind=number name=Speed min=0.5 max=4 step=0.5 unit=x default=1 help="How fast the stick goes round."')["value"])
tapFire := pf.Request('setting key=fire kind=switch name="Tap Fire" default=true help="Taps Fire once a second."')["value"] = "true"
pf.Request('setting key=center kind=button name=Center help="Takes the stick back to the top."')
pf.Request("setting key=turns kind=status name=Turns")

angle := 0.0
last := A_TickCount
start := A_TickCount
shown := -1

Move() {
    global pf, pad, angle, last, start, shown, speed, tapFire, centered
    if pf.Closed
        ExitApp()
    now := A_TickCount
    angle += (now - last) / 1000 * 3.141592653589793 * speed
    last := now
    if centered {
        centered := false
        angle := 0.0
    }
    turns := Floor(angle / (2 * 3.141592653589793))
    if (turns != shown) {
        shown := turns
        pf.Request("setting key=turns value=" turns)
    }
    pad.Set("a0", Round(100 * Sin(angle), 1))
    pad.Set("a1", Round(-100 * Cos(angle), 1))
    pad.Set("b0", tapFire && Mod((now - start) / 1000, 1.0) < 0.2)
    pad.Send()
}
SetTimer(Move, 20)
