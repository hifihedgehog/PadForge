# The left stick as W, A, S and D, for a game that takes only keys. Each
# direction holds a button of a controller of the script's own while the
# stick is past 50% that way: PowerShell has no command of its own that
# presses a key, so map the four buttons to keys on PadForge's Devices
# page. Needs Send Inputs.
Import-Module PadForge

$keys = Add-PadForgeController stickkeys 'Stick Keys' -Buttons W, A, S, D
$pad = Get-PadForgeDevice gamepad

# "~" leaves the stick to the game as well.
$pad.Hotkey('~LeftY < -50%', { $keys.W = $true })
$pad.Hotkey('~LeftY < -50% up', { $keys.W = $false })
$pad.Hotkey('~LeftY > 50%', { $keys.S = $true })
$pad.Hotkey('~LeftY > 50% up', { $keys.S = $false })
$pad.Hotkey('~LeftX < -50%', { $keys.A = $true })
$pad.Hotkey('~LeftX < -50% up', { $keys.A = $false })
$pad.Hotkey('~LeftX > 50%', { $keys.D = $true })
$pad.Hotkey('~LeftX > 50% up', { $keys.D = $false })

Wait-PadForge
