## Quick check: start PadForge, wait for vJoy node creation, test CfgMgr32, stop PadForge
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_quick_check_log.txt"
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

Set-Content -Path $logFile -Value "=== Quick Check $(Get-Date) ==="

# Kill any existing PadForge
$existing = Get-Process PadForge -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Process -Id $existing.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Clear old diag log
$diagLog = "C:\PadForge\vjoy_diag.log"
if (Test-Path $diagLog) { Remove-Item $diagLog -Force }

# Start PadForge
Log "Starting PadForge..."
Start-Process "C:\PadForge\PadForge.exe"
Start-Sleep -Seconds 8

# Check if vJoy node exists now
$output = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$nodeId = $null; $currentId = $null
foreach ($line in $output -split "`n") {
    $t = $line.Trim()
    if ($t -match 'Instance ID:\s+(.+)') { $currentId = $matches[1].Trim() }
    elseif ($currentId -and $t -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') {
        $nodeId = $currentId; break
    }
    elseif ([string]::IsNullOrWhiteSpace($t)) { $currentId = $null }
}

Log ("Node found: " + $nodeId)

# Check diag log
if (Test-Path $diagLog) {
    $content = Get-Content $diagLog
    Log "DiagLog entries:"
    foreach ($l in $content) {
        # Only show non-Pass1b lines
        if ($l -notmatch 'Pass1b:') {
            Log "  $l"
        }
    }
    # Show last Pass1b line
    $pass1b = $content | Where-Object { $_ -match 'Pass1b:' } | Select-Object -Last 1
    if ($pass1b) { Log "  (last Pass1b) $pass1b" }
}

# Check joy.cpl
$jc = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Log ("joyCpl count: " + ($jc | Measure-Object).Count)

# Now test CfgMgr32
if ($nodeId) {
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

    $devInst = 0
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0)
    Log ("CM_Locate_DevNode: cr=$cr devInst=$devInst")

    if ($cr -eq 0 -and $devInst -ne 0) {
        $s = 0; $p = 0
        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        Log ("Initial status=0x{0:X8} problem=$p" -f $s)

        Log "CM_Disable_DevNode..."
        $cr = [CfgMgr]::CM_Disable_DevNode($devInst, [CfgMgr]::CM_DISABLE_HARDWARE)
        Log ("Disable result: $cr")
        Start-Sleep -Seconds 2

        $jc2 = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
        Log ("After disable joyCpl: " + ($jc2 | Measure-Object).Count)

        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        Log ("After disable status=0x{0:X8} problem=$p" -f $s)

        Log "CM_Enable_DevNode..."
        $cr = [CfgMgr]::CM_Enable_DevNode($devInst, 0)
        Log ("Enable result: $cr")
        Start-Sleep -Seconds 3

        $jc3 = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
        Log ("After enable joyCpl: " + ($jc3 | Measure-Object).Count)

        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        Log ("After enable status=0x{0:X8} problem=$p" -f $s)
    }
}

# Kill PadForge
Log "Stopping PadForge..."
$proc = Get-Process PadForge -ErrorAction SilentlyContinue
if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

Log "=== Done ==="
