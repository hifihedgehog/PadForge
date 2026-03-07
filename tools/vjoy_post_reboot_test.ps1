## Post-reboot vJoy test
## Tests: CfgMgr32 disable/enable, registry writes, restart via remove+recreate
## Run after reboot to verify the stuck state is cleared
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_post_reboot_log.txt"
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

Set-Content -Path $logFile -Value "=== Post-Reboot vJoy Test $(Get-Date) ==="

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CfgMgr {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);
    public const int CM_DISABLE_HARDWARE = 0x4;
}
"@ -ErrorAction SilentlyContinue

function Get-JoyCplCount {
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
}

function Get-NodeId {
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

function Get-RegCount {
    $baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
    if (-not (Test-Path $baseKey)) { return 0 }
    return (Get-ChildItem $baseKey -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } | Measure-Object).Count
}

# Kill PadForge if running
$existing = Get-Process PadForge -ErrorAction SilentlyContinue
if ($existing) {
    Log "Killing existing PadForge..."
    Stop-Process -Id $existing.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Check initial state
$nodeId = Get-NodeId
$jc = Get-JoyCplCount
$reg = Get-RegCount
Log ("Initial: node=$nodeId joyCpl=$jc regKeys=$reg")

if ($nodeId) {
    # Check for DN_NEED_RESTART
    $devInst = 0
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0)
    if ($cr -eq 0 -and $devInst -ne 0) {
        $s = 0; $p = 0
        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        $needRestart = ($s -band 0x01000000) -ne 0
        Log ("Node status=0x{0:X8} DN_NEED_RESTART={1}" -f $s, $needRestart)
        if ($needRestart) {
            Log "WARNING: Node still in DN_NEED_RESTART state! Reboot may not have cleared it."
        }
    }
}

# Start PadForge
$diagLog = "C:\PadForge\vjoy_diag.log"
if (Test-Path $diagLog) { Remove-Item $diagLog -Force }
Log "Starting PadForge..."
Start-Process "C:\PadForge\PadForge.exe"
Start-Sleep -Seconds 8

$nodeId = Get-NodeId
$jc = Get-JoyCplCount
$reg = Get-RegCount
Log ("After PadForge start: node=$nodeId joyCpl=$jc regKeys=$reg")

# Read diag log (non-Pass1b entries)
if (Test-Path $diagLog) {
    $content = Get-Content $diagLog
    Log "DiagLog (key events):"
    foreach ($l in $content) {
        if ($l -notmatch 'Pass1b:') { Log "  $l" }
    }
}

# Test CfgMgr32 disable/enable
if ($nodeId) {
    $devInst = 0
    [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0) | Out-Null
    if ($devInst -ne 0) {
        Log ""
        Log "--- Testing CfgMgr32 Disable ---"
        $cr = [CfgMgr]::CM_Disable_DevNode($devInst, [CfgMgr]::CM_DISABLE_HARDWARE)
        Log ("CM_Disable_DevNode: $cr")
        Start-Sleep -Seconds 2
        Log ("joyCpl after disable: " + (Get-JoyCplCount))

        Log "--- Testing CfgMgr32 Enable ---"
        $cr = [CfgMgr]::CM_Enable_DevNode($devInst, 0)
        Log ("CM_Enable_DevNode: $cr")
        Start-Sleep -Seconds 3
        Log ("joyCpl after enable: " + (Get-JoyCplCount))
    }
}

# Kill PadForge
Log ""
Log "Killing PadForge..."
$proc = Get-Process PadForge -ErrorAction SilentlyContinue
if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

Log "=== Done ==="
