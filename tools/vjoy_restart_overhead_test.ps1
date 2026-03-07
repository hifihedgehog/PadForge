## Test: does PadForge do an unnecessary remove+recreate on restart?
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_restart_overhead_log.txt"
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

Set-Content -Path $logFile -Value "=== Restart Overhead Test $(Get-Date) ==="

$diagLog = "C:\PadForge\vjoy_diag.log"

function Stop-PadForge {
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue
    if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
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

# Step 1: Start PadForge fresh, let it create a node
Log "Step 1: Initial PadForge start"
Stop-PadForge
if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
Start-Process "C:\PadForge\PadForge.exe"
Start-Sleep -Seconds 12

$nodeId = Get-VJoyNodeId
Log ("Node after initial start: " + $nodeId)
if (-not $nodeId) {
    Log "ABORT: No node created. Need a device connected to vJoy slot."
    Stop-PadForge
    exit 1
}

$diag1 = @()
if (Test-Path $diagLog) { $diag1 = Get-Content $diagLog }
Log "Diag log from initial start:"
foreach ($l in $diag1) { Log ("  " + $l) }

# Step 2: Stop and restart PadForge (node already exists, same config)
Log ""
Log "Step 2: Restart PadForge (node exists, no changes)"
Stop-PadForge
Start-Sleep -Seconds 2
$nodeId2 = Get-VJoyNodeId
Log ("Node after stop: " + $nodeId2)

if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
Start-Process "C:\PadForge\PadForge.exe"
Start-Sleep -Seconds 12

$nodeId3 = Get-VJoyNodeId
Log ("Node after restart: " + $nodeId3)

$diag2 = @()
if (Test-Path $diagLog) { $diag2 = Get-Content $diagLog }
Log ""
Log "Diag log from restart:"
foreach ($l in $diag2) { Log ("  " + $l) }

# Check if unnecessary remove+recreate happened
$removes = $diag2 | Where-Object { $_ -match 'RemoveDeviceNode' }
$restarts = $diag2 | Where-Object { $_ -match 'Restarting device node|remove\+recreate' }
$disables = $diag2 | Where-Object { $_ -match 'DisableDeviceNode|Disabling device node' }

Log ""
Log ("RemoveDeviceNode calls: " + $removes.Count)
Log ("RestartDeviceNode calls: " + $restarts.Count)
Log ("DisableDeviceNode calls: " + $disables.Count)

if ($removes.Count -eq 0 -and $restarts.Count -eq 0) {
    Log "GOOD: No unnecessary remove+recreate on restart"
} else {
    Log "ISSUE: Unnecessary remove+recreate on restart!"
    foreach ($l in $removes) { Log ("  " + $l) }
    foreach ($l in $restarts) { Log ("  " + $l) }
    foreach ($l in $disables) { Log ("  " + $l) }
}

Stop-PadForge
Log ""
Log "=== Done ==="
