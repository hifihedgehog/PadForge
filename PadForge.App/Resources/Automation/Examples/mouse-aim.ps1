# The right stick aims, faster the further it goes, and the right shoulder
# fires. PowerShell has no command of its own that moves the mouse, so this
# script drives a controller of its own instead: map its Aim X and Aim Y to
# the game's aim on PadForge's Devices page. A read of the stick costs
# nothing: the library keeps the pad's whole state. Needs Send Inputs,
# since the shoulder is taken from the game.
Import-Module PadForge

$aim = Add-PadForgeController aimpad 'Aim Pad' -Buttons Fire -Axes @{ name = 'Aim X' }, @{ name = 'Aim Y' }
$pad = Get-PadForgeDevice gamepad
$pad.Hotkey('RightShoulder', { $aim.Tap('Fire', 50) })

while ($true) {
    $x = $pad.RightX
    $y = $pad.RightY
    if ($null -ne $x) {
        # Past a tenth of the way, the further the stick goes the faster.
        if ([Math]::Abs($x) -le 10) { $x = 0 }
        if ([Math]::Abs($y) -le 10) { $y = 0 }
        $aim.Hold('AimX', $x * [Math]::Abs($x) / 100, 'AimY', $y * [Math]::Abs($y) / 100)
    }
    Wait-PadForge -Seconds 0.01
}
