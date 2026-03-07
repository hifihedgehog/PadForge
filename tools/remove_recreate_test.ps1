## Test: remove stuck node + recreate fresh
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "remove_recreate_log.txt"
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

Set-Content -Path $logFile -Value "=== Remove+Recreate Test $(Get-Date) ==="

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
    public const int CM_DISABLE_HARDWARE = 0x4;
}
"@ -ErrorAction SilentlyContinue

function Get-JoyCplCount {
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
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

function Check-NodeStatus($nodeId) {
    $devInst = 0
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0)
    if ($cr -eq 0 -and $devInst -ne 0) {
        $s = 0; $p = 0
        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        $needRestart = ($s -band 0x01000000) -ne 0
        return @{ Status = $s; NeedRestart = $needRestart; Problem = $p; DevInst = $devInst }
    }
    return $null
}

# Step 0: Current state
$nodeId = Get-VJoyNodeId
$jc = Get-JoyCplCount
Log ("Initial: node=" + $nodeId + " joyCpl=" + $jc)

if ($nodeId) {
    $info = Check-NodeStatus $nodeId
    if ($info) {
        Log ("Status=0x{0:X8} DN_NEED_RESTART={1}" -f $info.Status, $info.NeedRestart)
    }
}

# Step 1: Remove the stuck node
Log ""
Log "--- Step 1: Remove stuck node via pnputil ---"
if ($nodeId) {
    $result = pnputil /remove-device $nodeId 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Log ("pnputil /remove-device exit=" + $exitCode)
    Log ("Output: " + $result.Trim())
}

Start-Sleep -Seconds 2

$nodeId2 = Get-VJoyNodeId
$jc2 = Get-JoyCplCount
Log ("After remove: node=" + $nodeId2 + " joyCpl=" + $jc2)

# Step 2: Recreate the node via SetupAPI PowerShell (same as PadForge's CreateVJoyDevices)
Log ""
Log "--- Step 2: Recreate node via SetupAPI ---"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class SetupAPI {
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        int Property, byte[] PropertyBuffer, int PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId,
        string FullInfPath, int InstallFlags, IntPtr pRebootRequired);

    public const int DICD_GENERATE_ID = 1;
    public const int SPDRP_HARDWAREID = 1;
    public const int DIF_REGISTERDEVICE = 0x19;
}
"@ -ErrorAction SilentlyContinue

# Actually, let's just use pnputil to scan for hardware changes instead
# The node creation is complex via SetupAPI from PowerShell. Let's see if PadForge can do it.

# Alternative: just use devcon or pnputil /scan-devices
Log "Running pnputil /scan-devices..."
$result = pnputil /scan-devices 2>&1 | Out-String
Log ("scan-devices: " + $result.Trim())

Start-Sleep -Seconds 3

$nodeId3 = Get-VJoyNodeId
$jc3 = Get-JoyCplCount
Log ("After scan: node=" + $nodeId3 + " joyCpl=" + $jc3)

# If node doesn't come back from scan, we need PadForge to create it
if (-not $nodeId3) {
    Log ""
    Log "Node not recreated by scan. Starting PadForge to create it..."

    $diagLog = "C:\PadForge\vjoy_diag.log"
    if (Test-Path $diagLog) { Remove-Item $diagLog -Force }

    Start-Process "C:\PadForge\PadForge.exe"
    Start-Sleep -Seconds 10

    $nodeId4 = Get-VJoyNodeId
    $jc4 = Get-JoyCplCount
    Log ("After PadForge start: node=" + $nodeId4 + " joyCpl=" + $jc4)

    if ($nodeId4) {
        $info = Check-NodeStatus $nodeId4
        if ($info) {
            Log ("New node status=0x{0:X8} DN_NEED_RESTART={1}" -f $info.Status, $info.NeedRestart)

            # Test CfgMgr32 disable/enable on the fresh node
            Log ""
            Log "--- Step 3: Test CfgMgr32 on fresh node ---"
            $cr = [CfgMgr]::CM_Disable_DevNode($info.DevInst, [CfgMgr]::CM_DISABLE_HARDWARE)
            Log ("CM_Disable_DevNode: cr=" + $cr)
            Start-Sleep -Seconds 2

            $jc5 = Get-JoyCplCount
            Log ("After disable joyCpl=" + $jc5)

            $cr = [CfgMgr]::CM_Enable_DevNode($info.DevInst, 0)
            Log ("CM_Enable_DevNode: cr=" + $cr)
            Start-Sleep -Seconds 3

            $jc6 = Get-JoyCplCount
            Log ("After enable joyCpl=" + $jc6)

            $info2 = Check-NodeStatus $nodeId4
            if ($info2) {
                Log ("Final status=0x{0:X8} DN_NEED_RESTART={1}" -f $info2.Status, $info2.NeedRestart)
            }
        }
    }

    # Read diag log
    if (Test-Path $diagLog) {
        Log ""
        Log "--- DiagLog ---"
        foreach ($l in (Get-Content $diagLog)) {
            if ($l -notmatch 'Pass1b:') { Log ("  " + $l) }
        }
    }

    # Stop PadForge
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue
    if ($proc) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}

Log ""
Log "=== Done ==="
