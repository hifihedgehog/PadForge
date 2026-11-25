; A controller of the script's own: a wheel that a pad's shoulders turn and
; its triggers drive, with a battery, motion and a touchpad, which hears the
; game's rumble. Map it on PadForge's Devices page like any other. Needs
; Send Inputs.
#Requires AutoHotkey v2.0
#Include <PadForge>

wheel := PadForge.AddController("padwheel", "Pad Wheel", {
    Buttons: ["Shift Up", "Shift Down"],
    Axes: [{name: "Steer"}, {name: "Gas", rest: 0}, {name: "Brake", rest: 0}],
    Gyro: true, Touchpads: [2], Battery: true,
    Feedback: ["rumble", "trigfx"]})
wheel.Battery := 100
wheel.OnRumble((low, high) => ToolTip("rumble " low " " high))

pad := PadForge.Device("gamepad")
pad.Hotkey("LeftShoulder", (*) => wheel.Steer := Max(-100, wheel.Steer - 10))
pad.Hotkey("RightShoulder", (*) => wheel.Steer := Min(100, wheel.Steer + 10))
pad.Hotkey("Y", (*) => wheel.Tap("ShiftUp", 80))
pad.Hotkey("A", (*) => wheel.Tap("ShiftDown", 80))
pad.OnChange("RightTrigger", (value, *) => wheel.Gas := value)
pad.OnChange("LeftTrigger", (value, *) => wheel.Brake := value)

Persistent
