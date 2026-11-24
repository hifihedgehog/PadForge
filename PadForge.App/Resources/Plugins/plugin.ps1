<#
A PadForge plugin in PowerShell, from the PadForge Plugin Kit.

To run it, keep this file beside PadForge.psm1, which the kit carries too,
and add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this file.
PadForge shows the file, its signer and its SHA-256, and runs it with your
ordinary rights whenever the engine runs, in Windows PowerShell. An edited
file is a new file, which PadForge asks about again before it runs it.

What it does: it connects as a plugin with the one-time key PadForge put in
its environment, declares a controller with two buttons, a stick and rumble,
moves the stick around a circle and taps a button once a second, writes to
its log on the Plugins page, and prints the feedback games send. Map the
controller on PadForge's Devices page like any other.
#>

Import-Module (Join-Path $PSScriptRoot 'PadForge.psm1')

Connect-PadForgePlugin -Name 'Kit Plugin' | Out-Null
$pad = New-PadForgeController -Key 'kitpad' -Name 'Kit Pad' -Buttons 'Fire', 'Jump' `
    -Axes @{ name = 'Stick X' }, @{ name = 'Stick Y' } -Feedback 'rumble'
Register-PadForgeRumble -Controller $pad -Action { param($low, $high) [Console]::WriteLine("rumble $low $high") }
Write-PadForgeLog "Declared Kit Pad as $($pad.Id)."

$clock = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $t = $clock.Elapsed.TotalSeconds
    $fire = if (($t % 1.0) -lt 0.2) { 1 } else { 0 }
    Send-PadForgeControl $pad @{
        a0 = [int][Math]::Round(32768 + 32767 * [Math]::Cos($t * [Math]::PI))
        a1 = [int][Math]::Round(32768 + 32767 * [Math]::Sin($t * [Math]::PI))
        b0 = $fire
    }
    # Event actions run while the script sleeps.
    Start-Sleep -Milliseconds 20
}
