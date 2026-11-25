# A controller of the script's own: a wheel that a pad's shoulders turn and
# its triggers drive, with a battery, motion and a touchpad, which hears the
# game's rumble. Map it on PadForge's Devices page like any other. Needs
# Send Inputs.
Import-Module PadForge

$wheel = Add-PadForgeController padwheel 'Pad Wheel' -Buttons 'Shift Up', 'Shift Down' `
    -Axes @{ name = 'Steer' }, @{ name = 'Gas'; rest = 0 }, @{ name = 'Brake'; rest = 0 } `
    -Gyro -Touchpads 2 -Battery -Feedback rumble, trigfx
$wheel.Battery = 100
$wheel.OnRumble({ param($low, $high) Write-Host "rumble $low $high" })

$pad = Get-PadForgeDevice gamepad
$pad.Hotkey('LeftShoulder', { $wheel.Steer = [Math]::Max(-100, $wheel.Steer - 10) })
$pad.Hotkey('RightShoulder', { $wheel.Steer = [Math]::Min(100, $wheel.Steer + 10) })
$pad.Hotkey('Y', { $wheel.Tap('ShiftUp', 80) })
$pad.Hotkey('A', { $wheel.Tap('ShiftDown', 80) })
$pad.OnChange('RightTrigger', { param($value) $wheel.Gas = $value })
$pad.OnChange('LeftTrigger', { param($value) $wheel.Brake = $value })

Wait-PadForge
