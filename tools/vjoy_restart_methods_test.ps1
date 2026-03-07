## Test different device restart methods to find one that works without reboot
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_restart_methods_log.txt"
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

Set-Content -Path $logFile -Value "=== Restart Methods Test $(Get-Date) ==="

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
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Query_And_Remove_SubTreeW(int dnDevInst, out int pVetoType, IntPtr pszVetoName, int ulNameLength, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Setup_DevNode(int dnDevInst, int ulFlags);
    public const int CM_DISABLE_HARDWARE = 0x4;
    public const int CM_REENUMERATE_SYNCHRONOUS = 0x1;
    public const int CM_SETUP_DEVNODE_READY = 0;
    public const int CM_SETUP_DEVNODE_RESET = 4;
}

public class SetupApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_PROPCHANGE_PARAMS {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public int StateChange;
        public int Scope;
        public int HwProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_CLASSINSTALL_HEADER {
        public int cbSize;
        public int InstallFunction;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, int Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        System.Text.StringBuilder DeviceInstanceId, int DeviceInstanceIdSize, out int RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiSetClassInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_PROPCHANGE_PARAMS ClassInstallParams, int ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    public const int DIGCF_PRESENT = 0x2;
    public const int DICS_PROPCHANGE = 3;
    public const int DICS_ENABLE = 1;
    public const int DICS_DISABLE = 2;
    public const int DICS_FLAG_GLOBAL = 1;
    public const int DIF_PROPERTYCHANGE = 0x12;
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

function Get-RegDeviceCount {
    $baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
    if (-not (Test-Path $baseKey)) { return 0 }
    return (Get-ChildItem $baseKey -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } | Measure-Object).Count
}

function Get-NodeInfo($nodeId) {
    $devInst = 0
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, 0)
    if ($cr -eq 0 -and $devInst -ne 0) {
        $s = 0; $p = 0
        [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
        return @{ DevInst = $devInst; Status = $s; Problem = $p; NeedRestart = (($s -band 0x01000000) -ne 0) }
    }
    return $null
}

# Get current node
$nodeId = Get-VJoyNodeId
if (-not $nodeId) {
    Log "No vJoy node found. Start PadForge first."
    exit 1
}

$info = Get-NodeInfo $nodeId
$jc = Get-JoyCplCount
$reg = Get-RegDeviceCount
Log ("Node: " + $nodeId)
Log ("Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2} reg={3}" -f $info.Status, $info.NeedRestart, $jc, $reg)

# ======================================================================
# Method 1: CM_Reenumerate_DevNode (synchronous)
# ======================================================================
Log ""
Log "--- Method 1: CM_Reenumerate_DevNode (SYNCHRONOUS) ---"
$cr = [CfgMgr]::CM_Reenumerate_DevNode($info.DevInst, [CfgMgr]::CM_REENUMERATE_SYNCHRONOUS)
Log ("CM_Reenumerate_DevNode: cr=" + $cr)
Start-Sleep -Seconds 3
$info2 = Get-NodeInfo $nodeId
$jc2 = Get-JoyCplCount
if ($info2) {
    Log ("After reenum: Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info2.Status, $info2.NeedRestart, $jc2)
}

# ======================================================================
# Method 2: CM_Reenumerate_DevNode (normal, non-sync)
# ======================================================================
Log ""
Log "--- Method 2: CM_Reenumerate_DevNode (normal) ---"
$cr = [CfgMgr]::CM_Reenumerate_DevNode($info.DevInst, 0)
Log ("CM_Reenumerate_DevNode(0): cr=" + $cr)
Start-Sleep -Seconds 3
$info3 = Get-NodeInfo $nodeId
$jc3 = Get-JoyCplCount
if ($info3) {
    Log ("After reenum(0): Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info3.Status, $info3.NeedRestart, $jc3)
}

# ======================================================================
# Method 3: CM_Setup_DevNode (RESET)
# ======================================================================
Log ""
Log "--- Method 3: CM_Setup_DevNode (RESET) ---"
$cr = [CfgMgr]::CM_Setup_DevNode($info.DevInst, [CfgMgr]::CM_SETUP_DEVNODE_RESET)
Log ("CM_Setup_DevNode(RESET): cr=" + $cr)
Start-Sleep -Seconds 3
$info4 = Get-NodeInfo $nodeId
$jc4 = Get-JoyCplCount
if ($info4) {
    Log ("After setup_reset: Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info4.Status, $info4.NeedRestart, $jc4)
}

# ======================================================================
# Method 4: SetupDI DIF_PROPERTYCHANGE (DICS_PROPCHANGE) - devcon restart equivalent
# ======================================================================
Log ""
Log "--- Method 4: SetupDI DIF_PROPERTYCHANGE (DICS_PROPCHANGE) ---"

$hidGuid = [Guid]::new("745a17a0-74d3-11d0-b6fe-00a0c90f57da")

$devInfoSet = [SetupApi]::SetupDiGetClassDevsW([ref]$hidGuid, $null, [IntPtr]::Zero, [SetupApi]::DIGCF_PRESENT)
if ($devInfoSet -eq [IntPtr]::new(-1)) {
    Log ("SetupDiGetClassDevs FAILED: " + [Marshal]::GetLastWin32Error())
} else {
    $devInfoData = New-Object SetupApi+SP_DEVINFO_DATA
    $devInfoData.cbSize = [Marshal]::SizeOf($devInfoData)

    $found = $false
    $idx = 0
    while ([SetupApi]::SetupDiEnumDeviceInfo($devInfoSet, $idx, [ref]$devInfoData)) {
        $sb = New-Object System.Text.StringBuilder 256
        $reqSize = 0
        if ([SetupApi]::SetupDiGetDeviceInstanceIdW($devInfoSet, [ref]$devInfoData, $sb, 256, [ref]$reqSize)) {
            $instanceId = $sb.ToString()
            if ($instanceId -eq $nodeId) {
                $found = $true
                Log ("Found device at index " + $idx + ": " + $instanceId)

                # DICS_PROPCHANGE - this is what devcon restart uses
                $propChangeParams = New-Object SetupApi+SP_PROPCHANGE_PARAMS
                $propChangeParams.ClassInstallHeader.cbSize = [Marshal]::SizeOf([type][SetupApi+SP_CLASSINSTALL_HEADER])
                $propChangeParams.ClassInstallHeader.InstallFunction = [SetupApi]::DIF_PROPERTYCHANGE
                $propChangeParams.StateChange = [SetupApi]::DICS_PROPCHANGE
                $propChangeParams.Scope = [SetupApi]::DICS_FLAG_GLOBAL
                $propChangeParams.HwProfile = 0

                $setOk = [SetupApi]::SetupDiSetClassInstallParamsW($devInfoSet, [ref]$devInfoData, [ref]$propChangeParams, [Marshal]::SizeOf($propChangeParams))
                Log ("SetClassInstallParams: " + $setOk + " err=" + [Marshal]::GetLastWin32Error())

                if ($setOk) {
                    $callOk = [SetupApi]::SetupDiCallClassInstaller([SetupApi]::DIF_PROPERTYCHANGE, $devInfoSet, [ref]$devInfoData)
                    $err = [Marshal]::GetLastWin32Error()
                    Log ("CallClassInstaller(DIF_PROPERTYCHANGE): " + $callOk + " err=" + $err)

                    if ($callOk) {
                        Start-Sleep -Seconds 3
                        $info5 = Get-NodeInfo $nodeId
                        $jc5 = Get-JoyCplCount
                        if ($info5) {
                            Log ("After PROPCHANGE: Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info5.Status, $info5.NeedRestart, $jc5)
                        } else {
                            Log "Node not found after PROPCHANGE (may have new instance ID)"
                            $newNode = Get-VJoyNodeId
                            $jc5 = Get-JoyCplCount
                            Log ("New node: " + $newNode + " joyCpl=" + $jc5)
                        }
                    }
                }
                break
            }
        }
        $idx++
    }

    if (-not $found) { Log "vJoy device not found via SetupDI enumeration" }
    [SetupApi]::SetupDiDestroyDeviceInfoList($devInfoSet) | Out-Null
}

# ======================================================================
# Method 5: SetupDI DIF_PROPERTYCHANGE (DICS_DISABLE then DICS_ENABLE)
# ======================================================================
Log ""
Log "--- Method 5: SetupDI DICS_DISABLE + DICS_ENABLE ---"

$nodeId = Get-VJoyNodeId
if ($nodeId) {
    $devInfoSet = [SetupApi]::SetupDiGetClassDevsW([ref]$hidGuid, $null, [IntPtr]::Zero, [SetupApi]::DIGCF_PRESENT)
    if ($devInfoSet -ne [IntPtr]::new(-1)) {
        $devInfoData = New-Object SetupApi+SP_DEVINFO_DATA
        $devInfoData.cbSize = [Marshal]::SizeOf($devInfoData)

        $found = $false
        $idx = 0
        while ([SetupApi]::SetupDiEnumDeviceInfo($devInfoSet, $idx, [ref]$devInfoData)) {
            $sb = New-Object System.Text.StringBuilder 256
            $reqSize = 0
            if ([SetupApi]::SetupDiGetDeviceInstanceIdW($devInfoSet, [ref]$devInfoData, $sb, 256, [ref]$reqSize)) {
                if ($sb.ToString() -eq $nodeId) {
                    $found = $true

                    # DISABLE
                    $params = New-Object SetupApi+SP_PROPCHANGE_PARAMS
                    $params.ClassInstallHeader.cbSize = [Marshal]::SizeOf([type][SetupApi+SP_CLASSINSTALL_HEADER])
                    $params.ClassInstallHeader.InstallFunction = [SetupApi]::DIF_PROPERTYCHANGE
                    $params.StateChange = [SetupApi]::DICS_DISABLE
                    $params.Scope = [SetupApi]::DICS_FLAG_GLOBAL
                    $params.HwProfile = 0

                    [SetupApi]::SetupDiSetClassInstallParamsW($devInfoSet, [ref]$devInfoData, [ref]$params, [Marshal]::SizeOf($params)) | Out-Null
                    $ok = [SetupApi]::SetupDiCallClassInstaller([SetupApi]::DIF_PROPERTYCHANGE, $devInfoSet, [ref]$devInfoData)
                    $err = [Marshal]::GetLastWin32Error()
                    Log ("DICS_DISABLE: ok=" + $ok + " err=" + $err)
                    Start-Sleep -Seconds 2

                    $jcDis = Get-JoyCplCount
                    Log ("After DICS_DISABLE joyCpl=" + $jcDis)

                    # ENABLE
                    $params.StateChange = [SetupApi]::DICS_ENABLE
                    [SetupApi]::SetupDiSetClassInstallParamsW($devInfoSet, [ref]$devInfoData, [ref]$params, [Marshal]::SizeOf($params)) | Out-Null
                    $ok = [SetupApi]::SetupDiCallClassInstaller([SetupApi]::DIF_PROPERTYCHANGE, $devInfoSet, [ref]$devInfoData)
                    $err = [Marshal]::GetLastWin32Error()
                    Log ("DICS_ENABLE: ok=" + $ok + " err=" + $err)
                    Start-Sleep -Seconds 3

                    $info6 = Get-NodeInfo $nodeId
                    $jcEn = Get-JoyCplCount
                    if ($info6) {
                        Log ("After DICS_ENABLE: Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info6.Status, $info6.NeedRestart, $jcEn)
                    } else {
                        $newNode = Get-VJoyNodeId
                        Log ("After DICS_ENABLE: newNode=" + $newNode + " joyCpl=" + $jcEn)
                    }
                    break
                }
            }
            $idx++
        }
        if (-not $found) { Log "vJoy device not found for disable/enable" }
        [SetupApi]::SetupDiDestroyDeviceInfoList($devInfoSet) | Out-Null
    }
} else {
    Log "No vJoy node to test"
}

# ======================================================================
# Method 6: pnputil /restart-device (for comparison)
# ======================================================================
Log ""
Log "--- Method 6: pnputil /restart-device ---"
$nodeId = Get-VJoyNodeId
if ($nodeId) {
    $result = pnputil /restart-device $nodeId 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Log ("pnputil /restart-device: exit=" + $exitCode)
    Log ("Output: " + $result.Trim())
    Start-Sleep -Seconds 3
    $info7 = Get-NodeInfo $nodeId
    $jc7 = Get-JoyCplCount
    if ($info7) {
        Log ("After restart: Status=0x{0:X8} DN_NEED_RESTART={1} joyCpl={2}" -f $info7.Status, $info7.NeedRestart, $jc7)
    }
}

Log ""
Log "=== Done ==="
