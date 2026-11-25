<#
A PadForge plugin in PowerShell, from the PadForge Plugin Kit.

To run it, keep this file beside PadForge.psm1, which the kit carries too,
and add it in PadForge: Extras, Plugins, Add a Plugin..., then pick this file,
or drop it on that page. PadForge shows the file, its signer and its SHA-256,
and runs it with your ordinary rights whenever the engine runs, in Windows
PowerShell. An edited file shows Approve and Restart on its card.

What it does: it connects as a plugin with the one-time key PadForge put in
its environment, declares a controller with two buttons, a stick and rumble,
and four settings PadForge draws on its card: Speed, Tap Fire, Center and
Turns. It moves the stick around a circle at the Speed set, taps Fire once a
second while Tap Fire is on, counts its turns in Turns, writes to its log on
the Plugins page, and prints the feedback games send. Map the controller on
PadForge's Devices page like any other. README.md lists every verb, event
and kind of setting.
#>

Import-Module (Join-Path $PSScriptRoot 'PadForge.psm1')

Connect-PadForgePlugin -Name 'Kit Plugin' | Out-Null
$pad = New-PadForgeController -Key 'kitpad' -Name 'Kit Pad' -Buttons 'Fire', 'Jump' `
    -Axes @{ name = 'Stick X' }, @{ name = 'Stick Y' } -Feedback 'rumble'
Register-PadForgeRumble -Controller $pad -Action { param($low, $high) [Console]::WriteLine("rumble $low $high") }
Write-PadForgeLog "Declared Kit Pad as $($pad.Id)."

# What the user sets on the plugin's card. Each declaration's answer carries
# the value set in an earlier run, and a change arrives as an event.
$script:Speed = 1.0
$script:TapFire = $true
$script:Center = $false
Register-PadForgeEvent -Kind 'setting' -Action {
    param($pairs)
    switch ($pairs['key']) {
        'speed' { $script:Speed = [double]::Parse($pairs['value'], [Globalization.CultureInfo]::InvariantCulture) }
        'fire' { $script:TapFire = $pairs['value'] -eq 'true' }
        default { return }
    }
    Write-PadForgeLog "$($pairs['key']) is $($pairs['value'])."
}
Register-PadForgeEvent -Kind 'press' -Action { param($pairs) if ($pairs['key'] -eq 'center') { $script:Center = $true } }
$answer = Invoke-PadForgeRequest 'setting key=speed kind=number name=Speed min=0.5 max=4 step=0.5 unit=x default=1 help="How fast the stick goes round."'
$script:Speed = [double]::Parse($answer.Pairs['value'], [Globalization.CultureInfo]::InvariantCulture)
$answer = Invoke-PadForgeRequest 'setting key=fire kind=switch name="Tap Fire" default=true help="Taps Fire once a second."'
$script:TapFire = $answer.Pairs['value'] -eq 'true'
Invoke-PadForgeRequest 'setting key=center kind=button name=Center help="Takes the stick back to the top."' | Out-Null
Invoke-PadForgeRequest 'setting key=turns kind=status name=Turns' | Out-Null

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$angle = 0.0
$last = 0.0
$shown = -1
while ($true) {
    $t = $clock.Elapsed.TotalSeconds
    $angle += ($t - $last) * [Math]::PI * $script:Speed
    $last = $t
    if ($script:Center) {
        $script:Center = $false
        $angle = 0.0
    }
    $turns = [int][Math]::Floor($angle / (2 * [Math]::PI))
    if ($turns -ne $shown) {
        $shown = $turns
        Invoke-PadForgeRequest "setting key=turns value=$turns" | Out-Null
    }
    Send-PadForgeControl $pad @{
        a0 = [Math]::Round(100 * [Math]::Sin($angle), 1)
        a1 = [Math]::Round(-100 * [Math]::Cos($angle), 1)
        b0 = $script:TapFire -and (($t % 1.0) -lt 0.2)
    }
    # Event actions run while the script sleeps.
    Start-Sleep -Milliseconds 20
}
