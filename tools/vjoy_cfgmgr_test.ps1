## Test CfgMgr32 disable/enable on vJoy device node
## Tests whether CM_Disable_DevNode / CM_Enable_DevNode work
## even when pnputil is stuck in reboot-pending state.
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_cfgmgr_test_log.txt"
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Elevating..."
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $logFile) { Get-Content $logFile }
        exit
    }
}

Set-Content -Path $logFile -Value "=== CfgMgr32 Test $(Get-Date) ==="
Log "Admin: True"

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
    public static extern int CM_Reenumerate_DevNode(int dnDevInst, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);

    public const int CM_LOCATE_DEVNODE_NORMAL = 0;
    public const int CM_LOCATE_DEVNODE_PHANTOM = 1;
    public const int CM_DISABLE_HARDWARE = 0x4;
    public const int CM_REENUMERATE_SYNCHRONOUS = 0x1;
    public const int CR_SUCCESS = 0;
}
"@ -ErrorAction SilentlyContinue

function Get-JoyCplCount {
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
}

# Find the vJoy node
$output = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$nodeId = $null
$currentId = $null
foreach ($line in $output -split "`n") {
    $t = $line.Trim()
    if ($t -match 'Instance ID:\s+(.+)') { $currentId = $matches[1].Trim() }
    elseif ($currentId -and $t -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') {
        $nodeId = $currentId; break
    }
    elseif ([string]::IsNullOrWhiteSpace($t)) { $currentId = $null }
}

if (-not $nodeId) {
    Log "No vJoy device node found. Creating one first..."
    # Try to create a node via PadForge's approach
    Log "Skipping - need PadForge to create the node. Please start PadForge, add a vJoy controller, then rerun."
    exit 1
}

Log "Found vJoy node: $nodeId"
Log ("Initial joyCpl: " + (Get-JoyCplCount))

# Step 1: Locate the device node via CM API
$devInst = 0
$cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, [CfgMgr]::CM_LOCATE_DEVNODE_NORMAL)
Log ("CM_Locate_DevNode: cr=$cr devInst=$devInst")

if ($cr -ne 0 -or $devInst -eq 0) {
    Log "Failed to locate device node via CM API."
    # Try phantom flag
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, [CfgMgr]::CM_LOCATE_DEVNODE_PHANTOM)
    Log ("CM_Locate_DevNode (phantom): cr=$cr devInst=$devInst")
    if ($cr -ne 0) {
        Log "Cannot locate device node at all. Exiting."
        exit 1
    }
}

# Get current status
$status = 0; $problem = 0
$cr = [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
Log ("CM_Get_DevNode_Status: cr=$cr status=0x{0:X8} problem=$problem" -f $status)

# Step 2: Try CM_Disable_DevNode
Log ""
Log "--- Disabling via CM_Disable_DevNode ---"
$cr = [CfgMgr]::CM_Disable_DevNode($devInst, [CfgMgr]::CM_DISABLE_HARDWARE)
Log ("CM_Disable_DevNode: cr=$cr")
Start-Sleep -Seconds 2

$cr = [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
Log ("After disable - status=0x{0:X8} problem=$problem" -f $status)
Log ("joyCpl: " + (Get-JoyCplCount))

# Step 3: Try CM_Enable_DevNode
Log ""
Log "--- Enabling via CM_Enable_DevNode ---"
$cr = [CfgMgr]::CM_Enable_DevNode($devInst, 0)
Log ("CM_Enable_DevNode: cr=$cr")
Start-Sleep -Seconds 2

$cr = [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
Log ("After enable - status=0x{0:X8} problem=$problem" -f $status)
Log ("joyCpl: " + (Get-JoyCplCount))

# Step 4: Try CM_Reenumerate_DevNode
Log ""
Log "--- Re-enumerating via CM_Reenumerate_DevNode ---"
$cr = [CfgMgr]::CM_Reenumerate_DevNode($devInst, [CfgMgr]::CM_REENUMERATE_SYNCHRONOUS)
Log ("CM_Reenumerate_DevNode: cr=$cr")
Start-Sleep -Seconds 2

$cr = [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
Log ("After reenum - status=0x{0:X8} problem=$problem" -f $status)
Log ("joyCpl: " + (Get-JoyCplCount))

# Step 5: Full cycle — disable, then enable (the restart pattern)
Log ""
Log "--- Full restart cycle (disable + enable) ---"
$cr = [CfgMgr]::CM_Disable_DevNode($devInst, [CfgMgr]::CM_DISABLE_HARDWARE)
Log ("Disable: cr=$cr")
Start-Sleep -Milliseconds 200
$cr = [CfgMgr]::CM_Enable_DevNode($devInst, 0)
Log ("Enable: cr=$cr")
Start-Sleep -Seconds 3

$cr = [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0)
Log ("Final status=0x{0:X8} problem=$problem" -f $status)
Log ("Final joyCpl: " + (Get-JoyCplCount))

Log ""
Log "=== Done ==="
