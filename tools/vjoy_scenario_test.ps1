## Comprehensive vJoy scenario test
## Modifies PadForge.xml between runs to simulate UI changes
## Tests: 1 vJoy, 2 vJoy, type switch, config changes
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_scenario_log.txt"
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $logFile) { Get-Content $logFile }
        exit
    }
}

Set-Content -Path $logFile -Value "=== vJoy Scenario Test $(Get-Date) ==="

$configPath = "C:\PadForge\PadForge.xml"
$backupPath = "C:\PadForge\PadForge.xml.bak"
$diagLog = "C:\PadForge\vjoy_diag.log"
$regBase = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'

# Backup original config
Copy-Item $configPath $backupPath -Force
Log "Backed up PadForge.xml"

function Stop-PadForge {
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
}

function Start-PadForgeAndWait($waitSeconds = 12) {
    if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
    Start-Process "C:\PadForge\PadForge.exe"
    Start-Sleep -Seconds $waitSeconds
    return (Get-Process PadForge -ErrorAction SilentlyContinue) -ne $null
}

function Get-VJoyNodeId {
    $output = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
    $currentId = $null
    foreach ($line in $output -split "`n") {
        $t = $line.Trim()
        if ($t -match 'Instance ID:\s+(.+)') { $currentId = $matches[1].Trim() }
        elseif ($currentId -and $t -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') { return $currentId }
        elseif ([string]::IsNullOrWhiteSpace($t)) { $currentId = $null }
    }
    return $null
}

function Get-JoyCplCount {
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
}

function Get-RegDeviceCount {
    if (-not (Test-Path $regBase)) { return 0 }
    return (Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } | Measure-Object).Count
}

function Get-DiagLogFiltered {
    if (Test-Path $diagLog) {
        return Get-Content $diagLog | Where-Object {
            $_ -notmatch 'FFB PT_GAINREP' -and $_ -notmatch 'Pass1b:'
        }
    }
    return @()
}

function Show-State($label) {
    $nodeId = Get-VJoyNodeId
    $jc = Get-JoyCplCount
    $reg = Get-RegDeviceCount
    Log ("  [$label] node=" + $nodeId + " joyCpl=" + $jc + " reg=" + $reg)
    return @{ Node = $nodeId; JoyCpl = $jc; Reg = $reg }
}

$pass = 0
$fail = 0
function Assert($condition, $message) {
    if ($condition) {
        $script:pass++
        Log ("  PASS: " + $message)
    } else {
        $script:fail++
        Log ("  FAIL: " + $message)
    }
}

function Set-SlotConfig($slotTypes, $slotCreated, $vjoyConfigs) {
    # Read current config
    [xml]$xml = Get-Content $configPath
    $app = $xml.PadForgeSettings.AppSettings

    # Update SlotControllerTypes
    $typeNodes = $app.SlotControllerTypes.ChildNodes
    for ($i = 0; $i -lt $slotTypes.Count -and $i -lt $typeNodes.Count; $i++) {
        $typeNodes[$i].InnerText = [string]$slotTypes[$i]
    }

    # Update SlotCreated
    $createdNodes = $app.SlotCreated.ChildNodes
    for ($i = 0; $i -lt $slotCreated.Count -and $i -lt $createdNodes.Count; $i++) {
        $createdNodes[$i].InnerText = if ($slotCreated[$i]) { "true" } else { "false" }
    }

    # Update VJoyConfigs if provided
    if ($vjoyConfigs) {
        $configNodes = $app.VJoyConfigs.ChildNodes
        for ($i = 0; $i -lt $vjoyConfigs.Count; $i++) {
            $cfg = $vjoyConfigs[$i]
            if ($i -lt $configNodes.Count) {
                $configNodes[$i].SetAttribute("Preset", $cfg.Preset)
                $configNodes[$i].SetAttribute("ThumbstickCount", [string]$cfg.Sticks)
                $configNodes[$i].SetAttribute("TriggerCount", [string]$cfg.Triggers)
                $configNodes[$i].SetAttribute("PovCount", [string]$cfg.Povs)
                $configNodes[$i].SetAttribute("ButtonCount", [string]$cfg.Buttons)
            }
        }
    }

    $xml.Save($configPath)
}

function Remove-VJoyNode {
    $nodeId = Get-VJoyNodeId
    if ($nodeId) {
        pnputil /remove-device $nodeId 2>&1 | Out-Null
        pnputil /scan-devices 2>&1 | Out-Null
        Start-Sleep -Seconds 2
    }
}

function Remove-AllRegDevices {
    if (Test-Path $regBase) {
        Get-ChildItem $regBase -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -match '^Device\d+$' } |
            ForEach-Object { Remove-Item $_.PSPath -Recurse -Force }
    }
}

# ======================================================================
# SETUP: Clean slate
# ======================================================================
Log ""
Log "=== SETUP: Clean slate ==="
Stop-PadForge
Remove-VJoyNode
Remove-AllRegDevices
Start-Sleep -Seconds 2

# ======================================================================
# SCENARIO 1: 1 vJoy controller (Custom: 128 buttons, 6 axes, 1 POV)
# ======================================================================
Log ""
Log "=== SCENARIO 1: 1 vJoy (Custom: 128 buttons, 2 sticks, 2 triggers, 1 POV) ==="
# Slot 0 = VJoy (type 2), Created = true
Set-SlotConfig @(2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0) @($true,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false) @(
    @{Preset="Custom"; Sticks=2; Triggers=2; Povs=1; Buttons=128}
)

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario1"
Assert ($state.Node -ne $null) "vJoy node exists"
Assert ($state.JoyCpl -ge 1) "joyCpl >= 1"
Assert ($state.Reg -eq 1) "1 registry Device key"

$diag = Get-DiagLogFiltered
$wrote = ($diag | Where-Object { $_ -match 'Wrote Device01.*128 buttons' }).Count
$acquired = ($diag | Where-Object { $_ -match 'AcquireVJD\(1\): True' }).Count
Assert ($acquired -ge 1) "Device 1 acquired"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SCENARIO 2: 2 vJoy controllers
# ======================================================================
Log ""
Log "=== SCENARIO 2: 2 vJoy controllers ==="
# Slot 0 = VJoy, Slot 1 = VJoy, both Created
# Need to add a UserSetting for slot 1 mapping a device to it
# Actually, slot 1 needs IsSlotActive = true. Without a device mapped, totalVJoyNeeded won't count it.
# The keyboard is already mapped to slot 0. Let me map the mouse (658d5467) to slot 1.
[xml]$xml = Get-Content $configPath
$app = $xml.PadForgeSettings.AppSettings

# Set slot types
$app.SlotControllerTypes.ChildNodes[0].InnerText = "2"  # VJoy
$app.SlotControllerTypes.ChildNodes[1].InnerText = "2"  # VJoy
$app.SlotCreated.ChildNodes[0].InnerText = "true"
$app.SlotCreated.ChildNodes[1].InnerText = "true"

# Add a UserSetting mapping mouse to slot 1
$mouseGuid = "658d5467-bd55-242e-a304-124c556c7099"  # All Mice (Merged)
$existingSettings = $xml.PadForgeSettings.UserSettings.Setting
$alreadyMapped = $false
foreach ($s in $existingSettings) {
    if ($s.InstanceGuid -eq $mouseGuid -and $s.MapTo -eq "1") { $alreadyMapped = $true; break }
}
if (-not $alreadyMapped) {
    $newSetting = $xml.CreateElement("Setting")
    $fields = @{
        InstanceGuid = $mouseGuid
        InstanceName = ""
        ProductGuid = "687ca4f2-d809-178e-5dad-e0ef7b16aa13"
        ProductName = ""
        MapTo = "1"
        PadSettingChecksum = "166F35FD"
        IsEnabled = "true"
        SortOrder = "0"
        DateCreated = "2026-03-05T08:00:00.0000000-05:00"
        DateUpdated = "2026-03-05T08:00:00.0000000-05:00"
        Comment = ""
        IsAutoMapped = "false"
    }
    foreach ($kv in $fields.GetEnumerator()) {
        $elem = $xml.CreateElement($kv.Key)
        $elem.InnerText = $kv.Value
        $newSetting.AppendChild($elem) | Out-Null
    }
    $xml.PadForgeSettings.UserSettings.AppendChild($newSetting) | Out-Null
}

# Set VJoyConfigs for slot 1
$vjoyConfigs = $app.VJoyConfigs.ChildNodes
$vjoyConfigs[1].SetAttribute("Preset", "Xbox360")
$vjoyConfigs[1].SetAttribute("ThumbstickCount", "2")
$vjoyConfigs[1].SetAttribute("TriggerCount", "2")
$vjoyConfigs[1].SetAttribute("PovCount", "1")
$vjoyConfigs[1].SetAttribute("ButtonCount", "11")

$xml.Save($configPath)

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario2"
Assert ($state.Node -ne $null) "vJoy node exists"
Assert ($state.Reg -eq 2) "2 registry Device keys"
Assert ($state.JoyCpl -ge 2) "joyCpl >= 2"

$diag = Get-DiagLogFiltered
$acq1 = ($diag | Where-Object { $_ -match 'AcquireVJD\(1\): True' }).Count
$acq2 = ($diag | Where-Object { $_ -match 'AcquireVJD\(2\): True' }).Count
Assert ($acq1 -ge 1) "Device 1 acquired"
Assert ($acq2 -ge 1) "Device 2 acquired"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SCENARIO 3: Type switch - slot 0 from vJoy to Xbox360
# ======================================================================
Log ""
Log "=== SCENARIO 3: Type switch (slot 0: vJoy -> Xbox360, slot 1: vJoy stays) ==="
Set-SlotConfig @(0,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0) @($true,$true,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false) $null

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario3"
Assert ($state.Reg -eq 1) "1 registry Device key (only slot 1)"

$diag = Get-DiagLogFiltered
# Should have restarted the device node (descriptor count changed from 2 to 1)
$restartOrRemove = ($diag | Where-Object { $_ -match 'Restarting device node|RemoveDeviceNode' }).Count
Assert ($restartOrRemove -ge 1) "Node restarted for descriptor change"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SCENARIO 4: Config change - change button count
# ======================================================================
Log ""
Log "=== SCENARIO 4: Config change (slot 1: 11 buttons -> 32 buttons, 0 POVs -> 2 POVs) ==="
[xml]$xml = Get-Content $configPath
$vjoyConfigs = $xml.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes
$vjoyConfigs[1].SetAttribute("Preset", "Custom")
$vjoyConfigs[1].SetAttribute("ButtonCount", "32")
$vjoyConfigs[1].SetAttribute("PovCount", "2")
$xml.Save($configPath)

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario4"
Assert ($state.Reg -eq 1) "1 registry Device key"

$diag = Get-DiagLogFiltered
# Should detect descriptor change and restart
$wrote = ($diag | Where-Object { $_ -match 'Wrote Device01.*32 buttons.*2 POVs' }).Count
Assert ($wrote -ge 1) "Descriptor written with 32 buttons, 2 POVs"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SCENARIO 5: Change stick count (0 sticks = 0 axes)
# ======================================================================
Log ""
Log "=== SCENARIO 5: Config change (slot 1: 0 sticks, 0 triggers, 4 POVs, 64 buttons) ==="
[xml]$xml = Get-Content $configPath
$vjoyConfigs = $xml.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes
$vjoyConfigs[1].SetAttribute("Preset", "Custom")
$vjoyConfigs[1].SetAttribute("ThumbstickCount", "0")
$vjoyConfigs[1].SetAttribute("TriggerCount", "0")
$vjoyConfigs[1].SetAttribute("PovCount", "4")
$vjoyConfigs[1].SetAttribute("ButtonCount", "64")
$xml.Save($configPath)

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario5"

$diag = Get-DiagLogFiltered
$wrote = ($diag | Where-Object { $_ -match 'Wrote Device01.*0 axes.*64 buttons.*4 POVs' }).Count
Assert ($wrote -ge 1) "Descriptor written with 0 axes, 64 buttons, 4 POVs"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SCENARIO 6: Remove all vJoy slots (go back to Xbox360 only)
# ======================================================================
Log ""
Log "=== SCENARIO 6: Remove all vJoy slots (both -> Xbox360) ==="
Set-SlotConfig @(0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0) @($true,$true,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false,$false) $null

$ok = Start-PadForgeAndWait 15
Assert $ok "PadForge started"
$state = Show-State "scenario6"

# After grace period (5 seconds), vJoy should clean up
# But we need to wait longer for the grace period to expire
Start-Sleep -Seconds 8  # Additional wait for 5-second grace + cleanup
$state2 = Show-State "scenario6-after-grace"

$diag = Get-DiagLogFiltered
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# CLEANUP: Restore original config
# ======================================================================
Log ""
Log "=== CLEANUP: Restoring original config ==="
Copy-Item $backupPath $configPath -Force
Remove-Item $backupPath -Force -ErrorAction SilentlyContinue
Remove-VJoyNode
Remove-AllRegDevices

# ======================================================================
# SUMMARY
# ======================================================================
Log ""
Log "========================================"
Log ("RESULTS: " + $pass + " passed, " + $fail + " failed")
Log "========================================"
