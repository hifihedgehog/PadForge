# Test vJoy device creation via SetupAPI (must run as Administrator)
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TestSetupApi {
    public const int DIF_REGISTERDEVICE = 0x19;
    public const int SPDRP_HARDWAREID = 0x01;
    public const int DICD_GENERATE_ID = 0x01;
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, int Property, byte[] PropertyBuffer, int PropertyBufferSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId, string FullInfPath, int InstallFlags, out bool bRebootRequired);
}
'@

$hidGuid = [Guid]::new('{745a17a0-74d3-11d0-b6fe-00a0c90f57da}')
$hwid = 'root\VID_1234&PID_BEAD&REV_0222'
$infPath = 'C:\Program Files\vJoy\vjoy.inf'

Write-Host "=== vJoy Device Creation Test ==="
Write-Host "Hardware ID: $hwid"
Write-Host "INF Path:    $infPath"
Write-Host "INF exists:  $(Test-Path $infPath)"
Write-Host ""

# Step 1: Create device info list
$dis = [TestSetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host "[1] SetupDiCreateDeviceInfoList: handle=$dis (err=$err)"
if ($dis -eq [IntPtr]::new(-1)) { Write-Host "FATAL: Cannot create device info list"; exit 1 }

# Step 2: Create device info (generates instance ID like ROOT\HIDCLASS\0000)
$did = New-Object TestSetupApi+SP_DEVINFO_DATA
$did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][TestSetupApi+SP_DEVINFO_DATA])
$ok = [TestSetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [TestSetupApi]::DICD_GENERATE_ID, [ref]$did)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host "[2] SetupDiCreateDeviceInfoW: ok=$ok (err=$err, devInst=$($did.DevInst))"
if (-not $ok) { Write-Host "FATAL: Cannot create device info"; [TestSetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }

# Step 3: Set hardware ID
$hwidBytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + [char]0 + [char]0)
$ok = [TestSetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [TestSetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host "[3] SetHardwareID: ok=$ok (err=$err)"
if (-not $ok) { Write-Host "FATAL: Cannot set hardware ID"; [TestSetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }

# Step 4: Register device
$ok = [TestSetupApi]::SetupDiCallClassInstaller([TestSetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host "[4] RegisterDevice: ok=$ok (err=$err)"
if (-not $ok) { Write-Host "FATAL: Cannot register device"; [TestSetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null; exit 1 }

[TestSetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null

# Step 5: Install driver on device
Write-Host "[5] UpdateDriverForPlugAndPlayDevices..."
$reboot = $false
$ok = [TestSetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 1, [ref]$reboot)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Host "    ok=$ok (err=$err, rebootRequired=$reboot)"

if ($ok) {
    Write-Host ""
    Write-Host "SUCCESS: Device created and driver installed!"
    Write-Host "Check 'joy.cpl' (Windows Game Controllers) for 'vJoy Device'"
} else {
    Write-Host ""
    Write-Host "FAILED: UpdateDriverForPlugAndPlayDevices returned false"
    Write-Host "Error $err may mean:"
    Write-Host "  5 = Access Denied (not running as admin)"
    Write-Host "  2 = File not found (inf path wrong)"
    Write-Host "  259 = No more data (no matching device)"
}

Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
