# Tap and turbo: A taps B for the game, and while X is held the game sees
# Y pressed and let go ten times a second. Needs Send Inputs.
Import-Module PadForge

$pad = Get-PadForgeDevice gamepad

# A belongs to the script: the game never sees it, and sees B tapped.
$pad.Hotkey('A', { $pad.Tap('B', 60) })

# X stays the game's too ("~"), and drives Y while it is held.
$turbo = $false
$pad.Hotkey('~X', { $script:turbo = $true })
$pad.Hotkey('~X up', { $script:turbo = $false })
while ($true) {
    if ($turbo) { $pad.Tap('Y', 40) }
    Wait-PadForge -Seconds 0.1
}
