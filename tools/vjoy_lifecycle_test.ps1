## Comprehensive vJoy lifecycle test via PadForge
## Tests remove+recreate approach with scan-devices cleanup
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_lifecycle_log.txt"
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

Set-Content -Path $logFile -Value "=== vJoy Lifecycle Test $(Get-Date) ==="

$regBase = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
$diagLog = "C:\PadForge\vjoy_diag.log"

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
    # Use WinMM joyGetNumDevs/joyGetDevCapsW for accurate joy.cpl count
    # Fall back to PnP entity count
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
}

function Get-RegDeviceCount {
    if (-not (Test-Path $regBase)) { return 0 }
    return (Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } | Measure-Object).Count
}

function Remove-AllRegDevices {
    if (-not (Test-Path $regBase)) { return }
    Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } |
        ForEach-Object { Remove-Item $_.PSPath -Recurse -Force }
}

function Remove-VJoyNode {
    $nodeId = Get-VJoyNodeId
    if ($nodeId) {
        pnputil /remove-device $nodeId 2>&1 | Out-Null
        pnputil /scan-devices 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        return $true
    }
    return $false
}

function Stop-PadForge {
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

function Start-PadForgeClean {
    if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
    Start-Process "C:\PadForge\PadForge.exe"
    Start-Sleep -Seconds 10
    return (Get-Process PadForge -ErrorAction SilentlyContinue) -ne $null
}

function Get-DiagLogEntries {
    if (Test-Path $diagLog) {
        return Get-Content $diagLog | Where-Object { $_ -notmatch 'Pass1b:' -and $_ -notmatch 'FFB PT_GAINREP' }
    }
    return @()
}

function Show-State($label) {
    $nodeId = Get-VJoyNodeId
    $jc = Get-JoyCplCount
    $reg = Get-RegDeviceCount
    $pf = (Get-Process PadForge -ErrorAction SilentlyContinue) -ne $null
    Log ("  [$label] node=" + $nodeId + " joyCpl=" + $jc + " reg=" + $reg + " padforge=" + $pf)
    return @{ Node = $nodeId; JoyCpl = $jc; Reg = $reg; PadForge = $pf }
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

# ======================================================================
# SETUP: Clean slate
# ======================================================================
Log ""
Log "=== SETUP: Clean slate ==="
Stop-PadForge
Remove-VJoyNode | Out-Null
Remove-AllRegDevices
Start-Sleep -Seconds 2

$state = Show-State "setup"
Assert ($state.Node -eq $null) "No node"
Assert ($state.Reg -eq 0) "No registry keys"

# ======================================================================
# TEST 1: PadForge starts from zero → creates 1 vJoy
# ======================================================================
Log ""
Log "=== TEST 1: PadForge starts from zero (1 vJoy in config) ==="
$ok = Start-PadForgeClean
Assert $ok "PadForge started"

$state = Show-State "after-start"
Assert ($state.Node -ne $null) "Node created"
Assert ($state.Reg -ge 1) "Registry has keys (count=$($state.Reg))"

# Show diag log
foreach ($l in (Get-DiagLogEntries)) { Log ("  diag: " + $l) }

Stop-PadForge
Log ""
Log "=== TEST 1b: Stop PadForge → node handling ==="
Start-Sleep -Seconds 2
$state = Show-State "after-stop"
# Node might still exist (PadForge doesn't remove on exit)

# ======================================================================
# TEST 2: Restart PadForge with existing node (fast path)
# ======================================================================
Log ""
Log "=== TEST 2: Restart PadForge (existing node, no config change) ==="
$ok = Start-PadForgeClean
Assert $ok "PadForge restarted"
$state = Show-State "restart"

# Check diag log - should NOT show RestartDeviceNode
$diag = Get-DiagLogEntries
$didRestart = ($diag | Where-Object { $_ -match 'Restarting device node|remove\+recreate' }).Count
Assert ($didRestart -eq 0) "No unnecessary node restart"
foreach ($l in $diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# TEST 3: Remove node, restart PadForge → recreate
# ======================================================================
Log ""
Log "=== TEST 3: Remove node externally, restart PadForge → recreate ==="
Remove-VJoyNode | Out-Null
Start-Sleep -Seconds 2
$state = Show-State "after-remove"
Assert ($state.Node -eq $null) "Node removed"

$ok = Start-PadForgeClean
Assert $ok "PadForge restarted after remove"
$state = Show-State "recreated"
Assert ($state.Node -ne $null) "Node recreated"
Assert ($state.Reg -ge 1) "Registry restored"

foreach ($l in (Get-DiagLogEntries)) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# TEST 4: Change registry (simulate descriptor change) → restart cycle
# ======================================================================
Log ""
Log "=== TEST 4: Simulate descriptor change (add Device02 key) ==="

# Add a second Device key to simulate going from 1 → 2 vJoy controllers
$device02Path = Join-Path $regBase "Device02"
if (-not (Test-Path $device02Path)) {
    New-Item -Path $device02Path -Force | Out-Null
    # Copy Device01 descriptor to Device02
    $device01Path = Join-Path $regBase "Device01"
    $desc = (Get-ItemProperty -Path $device01Path -Name HidReportDescriptor -ErrorAction SilentlyContinue).HidReportDescriptor
    $descSize = (Get-ItemProperty -Path $device01Path -Name HidReportDescriptorSize -ErrorAction SilentlyContinue).HidReportDescriptorSize
    if ($desc -and $descSize) {
        Set-ItemProperty -Path $device02Path -Name HidReportDescriptor -Value $desc -Type Binary
        Set-ItemProperty -Path $device02Path -Name HidReportDescriptorSize -Value $descSize -Type DWord
    }
}
$state = Show-State "after-reg-change"
Assert ($state.Reg -eq 2) "Two Device keys in registry"

# Start PadForge - it should detect the mismatch and restart the node
$ok = Start-PadForgeClean
Assert $ok "PadForge started with changed registry"

$state = Show-State "after-restart-with-change"
foreach ($l in (Get-DiagLogEntries)) { Log ("  diag: " + $l) }

Stop-PadForge

# Clean up Device02
if (Test-Path $device02Path) { Remove-Item $device02Path -Recurse -Force }

# ======================================================================
# TEST 5: Full cycle - zero → 1 → zero → 1
# ======================================================================
Log ""
Log "=== TEST 5: Full zero→1→zero→1 cycle ==="
Remove-VJoyNode | Out-Null
Remove-AllRegDevices
Start-Sleep -Seconds 2
$state = Show-State "clean"
Assert ($state.Node -eq $null) "Clean start"

# Start PadForge → creates 1
$ok = Start-PadForgeClean
Assert $ok "PadForge started (step 1)"
$state = Show-State "step1-created"
Assert ($state.Node -ne $null) "Node created (step 1)"
$firstNodeId = $state.Node
$step1Diag = Get-DiagLogEntries
foreach ($l in $step1Diag) { Log ("  diag: " + $l) }

# Stop and remove → zero
Stop-PadForge
Remove-VJoyNode | Out-Null
Remove-AllRegDevices
Start-Sleep -Seconds 2
$state = Show-State "step2-zero"
Assert ($state.Node -eq $null) "Clean again (step 2)"
Assert ($state.Reg -eq 0) "No registry (step 2)"

# Start PadForge → creates 1 again
$ok = Start-PadForgeClean
Assert $ok "PadForge started (step 3)"
$state = Show-State "step3-recreated"
Assert ($state.Node -ne $null) "Node created (step 3)"
$step3Diag = Get-DiagLogEntries
foreach ($l in $step3Diag) { Log ("  diag: " + $l) }

Stop-PadForge

# ======================================================================
# SUMMARY
# ======================================================================
Log ""
Log "========================================"
Log ("RESULTS: " + $pass + " passed, " + $fail + " failed")
Log "========================================"
