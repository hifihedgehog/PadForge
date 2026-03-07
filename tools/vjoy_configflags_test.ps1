## Check and try to clear CONFIGFLAGS to remove DN_NEED_RESTART
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_configflags_log.txt"
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

Set-Content -Path $logFile -Value "=== ConfigFlags Test $(Get-Date) ==="

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CfgMgr {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Reenumerate_DevNode(int dnDevInst, int ulFlags);
    public const int CM_DISABLE_HARDWARE = 0x4;
}
"@ -ErrorAction SilentlyContinue

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

$nodeId = Get-VJoyNodeId
if (-not $nodeId) {
    Log "No vJoy node found"
    exit 1
}

Log ("Node: " + $nodeId)

# Check ConfigFlags in registry
# Device registry key: HKLM\SYSTEM\CurrentControlSet\Enum\<instanceId>
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Enum\" + $nodeId
Log ("Registry path: " + $regPath)

if (Test-Path $regPath) {
    $configFlags = (Get-ItemProperty -Path $regPath -Name ConfigFlags -ErrorAction SilentlyContinue).ConfigFlags
    Log ("Current ConfigFlags: " + $configFlags + " (0x{0:X8})" -f $configFlags)

    # CONFIGFLAG_NEEDS_FORCED_CONFIG = 0x00000040
    # CONFIGFLAG_REINSTALL = 0x00000020
    # CONFIGFLAG_FAILEDINSTALL = 0x00000080
    # CONFIGFLAG_FINISH_INSTALL = 0x00000400
    $needsForced = ($configFlags -band 0x40) -ne 0
    $reinstall = ($configFlags -band 0x20) -ne 0
    $failedInstall = ($configFlags -band 0x80) -ne 0
    $finishInstall = ($configFlags -band 0x400) -ne 0
    Log ("  NEEDS_FORCED_CONFIG=" + $needsForced)
    Log ("  REINSTALL=" + $reinstall)
    Log ("  FAILED_INSTALL=" + $failedInstall)
    Log ("  FINISH_INSTALL=" + $finishInstall)

    # Try clearing ConfigFlags completely
    Log ""
    Log "--- Clearing ConfigFlags to 0 ---"
    Set-ItemProperty -Path $regPath -Name ConfigFlags -Value 0 -Type DWord -Force
    $newFlags = (Get-ItemProperty -Path $regPath -Name ConfigFlags -ErrorAction SilentlyContinue).ConfigFlags
    Log ("ConfigFlags after clear: " + $newFlags)

    # Check if DN_NEED_RESTART is cleared now
    $devInst = 0
    [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0) | Out-Null
    $s = 0; $p = 0
    [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
    $needRestart = ($s -band 0x01000000) -ne 0
    Log ("After clear: Status=0x{0:X8} DN_NEED_RESTART={1}" -f $s, $needRestart)

    if ($needRestart) {
        Log ""
        Log "DN_NEED_RESTART still set. Trying CM_Reenumerate..."
        [CfgMgr]::CM_Reenumerate_DevNode($devInst, 1) | Out-Null
        Start-Sleep -Seconds 2
        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        $needRestart = ($s -band 0x01000000) -ne 0
        Log ("After reenum: Status=0x{0:X8} DN_NEED_RESTART={1}" -f $s, $needRestart)
    }

    if ($needRestart) {
        Log ""
        Log "Still stuck. Trying CM_Disable + CM_Enable..."
        $cr = [CfgMgr]::CM_Disable_DevNode($devInst, [CfgMgr]::CM_DISABLE_HARDWARE)
        Log ("CM_Disable: cr=" + $cr)
        if ($cr -eq 0) {
            Start-Sleep -Milliseconds 500
            $cr = [CfgMgr]::CM_Enable_DevNode($devInst, 0)
            Log ("CM_Enable: cr=" + $cr)
            Start-Sleep -Seconds 2
            [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
            $needRestart = ($s -band 0x01000000) -ne 0
            Log ("After disable+enable: Status=0x{0:X8} DN_NEED_RESTART={1}" -f $s, $needRestart)
        }
    }

    $jc = Get-JoyCplCount
    Log ""
    Log ("Final joyCpl: " + $jc)
} else {
    Log "Registry path not found"
}

# Also check all Enum subkeys for the device
Log ""
Log "--- All registry values ---"
if (Test-Path $regPath) {
    Get-ItemProperty -Path $regPath | Format-List | Out-String | ForEach-Object { Log $_ }
}

Log "=== Done ==="
