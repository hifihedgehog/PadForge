; What happens in PadForge, and a value for a macro: a device that comes
; or goes and a profile that changes show in a tooltip, and the left
; trigger's pull is handed to Custom Expression triggers as "pull".
; Needs Read, and Send Inputs for the value.
#Requires AutoHotkey v2.0
#Include <PadForge>

PadForge.OnDevice((id, online) => ToolTip(id (online ? " connected" : " disconnected")))
PadForge.OnProfile((name) => ToolTip("Profile: " name))

pad := PadForge.Device("gamepad")
pad.OnChange("LeftTrigger", (value, *) => PadForge.SetValue("pull", value))
pad.OnConnect((connected) => ToolTip(pad.Name (connected ? " is here" : " went")))

Persistent
