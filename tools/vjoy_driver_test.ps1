# Direct vJoy driver test: bypass PadForge, create devices via registry + SetupAPI
# Must run elevated

param([int]$DeviceCount = 1)

$ErrorActionPreference = 'Continue'

# Build a minimal Xbox 360-like HID descriptor (same as PadForge's BuildHidDescriptor)
# This is a simplified version - just enough for the driver to create collections
function Build-HidDescriptor($reportId) {
    # Minimal HID descriptor with 1 Application collection
    # Usage Page (Generic Desktop), Usage (Joystick), Collection (Application)
    # Report ID, 6 axes (32-bit), 1 POV (32-bit), 11 buttons (padded to 128 bits)
    # End Collection
    # This matches the vJoyConf format

    $desc = @(
        0x05, 0x01,       # Usage Page (Generic Desktop)
        0x09, 0x04,       # Usage (Joystick)
        0xA1, 0x01,       # Collection (Application)
        0x85, $reportId,  # Report ID

        # 6 axes (X, Y, Z, Rx, Ry, Rz) - 32-bit each, Data
        0x05, 0x01,       # Usage Page (Generic Desktop)
        0x09, 0x30,       # Usage (X)
        0x09, 0x31,       # Usage (Y)
        0x09, 0x32,       # Usage (Z)
        0x09, 0x33,       # Usage (Rx)
        0x09, 0x34,       # Usage (Ry)
        0x09, 0x35,       # Usage (Rz)
        0x15, 0x00,       # Logical Minimum (0)
        0x27, 0xFF, 0x7F, 0x00, 0x00, # Logical Maximum (32767)
        0x75, 0x20,       # Report Size (32)
        0x95, 0x06,       # Report Count (6)
        0x81, 0x02,       # Input (Data, Var, Abs)

        # Remaining 10 axes - Constant (inactive)
        0x75, 0x20,       # Report Size (32)
        0x95, 0x0A,       # Report Count (10)
        0x81, 0x01,       # Input (Constant)

        # 1 continuous POV (32-bit)
        0x05, 0x01,       # Usage Page (Generic Desktop)
        0x09, 0x39,       # Usage (Hat Switch)
        0x15, 0x00,       # Logical Minimum (0)
        0x27, 0x3C, 0x8C, 0x00, 0x00, # Logical Maximum (35900)
        0x35, 0x00,       # Physical Minimum (0)
        0x47, 0x3C, 0x8C, 0x00, 0x00, # Physical Maximum (35900)
        0x75, 0x20,       # Report Size (32)
        0x95, 0x01,       # Report Count (1)
        0x81, 0x02,       # Input (Data, Var, Abs)

        # Remaining 3 POVs - Constant
        0x75, 0x20,       # Report Size (32)
        0x95, 0x03,       # Report Count (3)
        0x81, 0x01,       # Input (Constant)

        # 11 buttons
        0x05, 0x09,       # Usage Page (Button)
        0x19, 0x01,       # Usage Minimum (1)
        0x29, 0x0B,       # Usage Maximum (11)
        0x15, 0x00,       # Logical Minimum (0)
        0x25, 0x01,       # Logical Maximum (1)
        0x75, 0x01,       # Report Size (1)
        0x95, 0x0B,       # Report Count (11)
        0x81, 0x02,       # Input (Data, Var, Abs)

        # Padding to 128 bits (128-11=117 bits)
        0x75, 0x01,       # Report Size (1)
        0x95, 0x75,       # Report Count (117)
        0x81, 0x01,       # Input (Constant)

        0xC0              # End Collection
    )
    return [byte[]]$desc
}

Write-Host "=== vJoy Driver Direct Test (DeviceCount=$DeviceCount) ==="

# Step 1: Write registry descriptors
$base = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
if (-not (Test-Path $base)) {
    Write-Host "vjoy service not found in registry!"
    exit 1
}

Write-Host "`nStep 1: Writing $DeviceCount descriptor(s) to registry..."
for ($i = 1; $i -le $DeviceCount; $i++) {
    $keyName = "Device{0:D2}" -f $i
    $keyPath = "$base\$keyName"
    $desc = Build-HidDescriptor $i

    if (-not (Test-Path $keyPath)) { New-Item -Path $keyPath -Force | Out-Null }
    Set-ItemProperty -Path $keyPath -Name 'HidReportDescriptor' -Value $desc -Type Binary
    Set-ItemProperty -Path $keyPath -Name 'HidReportDescriptorSize' -Value $desc.Length -Type DWord
    Write-Host "  Wrote $keyName - $($desc.Length) bytes, report ID=$i"
}

# Remove excess keys
$existing = Get-ChildItem $base | Where-Object { $_.PSChildName -match '^Device(\d+)$' }
foreach ($key in $existing) {
    $num = [int]($key.PSChildName -replace 'Device', '')
    if ($num -gt $DeviceCount) {
        Write-Host "  Removing $($key.PSChildName)"
        Remove-Item -Path $key.PSPath -Recurse -Force
    }
}

# Verify
$devKeys = Get-ChildItem $base | Where-Object { $_.PSChildName -match '^Device\d+$' }
Write-Host "  Registry now has $($devKeys.Count) DeviceNN key(s)"

# Step 2: Remove existing device nodes
Write-Host "`nStep 2: Cleaning existing device nodes..."
$devs = pnputil /enum-devices /class HIDClass 2>&1
$lines = $devs -split "`n"
$currentId = $null
foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if ($trimmed -match 'Instance ID\s*:\s*(.+)') {
        $currentId = $matches[1].Trim()
    }
    elseif ($currentId -and $trimmed -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') {
        Write-Host "  Removing $currentId"
        pnputil /remove-device "$currentId" /subtree 2>&1 | Out-Null
        $currentId = $null
    }
    elseif (-not $trimmed) {
        $currentId = $null
    }
}
Start-Sleep -Seconds 1

# Step 3: Create a fresh device node
Write-Host "`nStep 3: Creating fresh device node..."
$vjoyDir = Join-Path $env:ProgramFiles 'vJoy'
$infPath = Join-Path $vjoyDir 'vjoy.inf'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupApi {
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
$hwidBytes = [System.Text.Encoding]::Unicode.GetBytes($hwid + [char]0 + [char]0)

$dis = [SetupApi]::SetupDiCreateDeviceInfoList([ref]$hidGuid, [IntPtr]::Zero)
$did = New-Object SetupApi+SP_DEVINFO_DATA
$did.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][SetupApi+SP_DEVINFO_DATA])
$ok = [SetupApi]::SetupDiCreateDeviceInfoW($dis, 'HIDClass', [ref]$hidGuid, 'vJoy Device', [IntPtr]::Zero, [SetupApi]::DICD_GENERATE_ID, [ref]$did)
if (-not $ok) { Write-Host "  SetupDiCreateDeviceInfoW FAILED: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"; exit 1 }
$ok = [SetupApi]::SetupDiSetDeviceRegistryPropertyW($dis, [ref]$did, [SetupApi]::SPDRP_HARDWAREID, $hwidBytes, $hwidBytes.Length)
if (-not $ok) { Write-Host "  SetupDiSetDeviceRegistryPropertyW FAILED"; exit 1 }
$ok = [SetupApi]::SetupDiCallClassInstaller([SetupApi]::DIF_REGISTERDEVICE, $dis, [ref]$did)
if (-not $ok) { Write-Host "  DIF_REGISTERDEVICE FAILED"; exit 1 }
[SetupApi]::SetupDiDestroyDeviceInfoList($dis) | Out-Null
Write-Host "  Device node registered"

$reboot = $false
$ok = [SetupApi]::UpdateDriverForPlugAndPlayDevicesW([IntPtr]::Zero, $hwid, $infPath, 0, [ref]$reboot)
if (-not $ok) {
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "  UpdateDriverForPlugAndPlayDevices FAILED: err=$err"
} else {
    Write-Host "  Driver bound successfully (reboot=$reboot)"
}

# Step 4: Wait and check
Write-Host "`nStep 4: Waiting for PnP to enumerate HID children..."
Start-Sleep -Seconds 5

# VJOYRAWPDO
$rawPdo = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Write-Host "`nVJOYRAWPDO devices: $($rawPdo.Count)"
$rawPdo | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# HID collection children
$hidDevices = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'HID\VID_1234&PID_BEAD*' }
Write-Host "HID vJoy collection devices: $($hidDevices.Count)"
$hidDevices | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode) name=$($_.Name)" }

# ROOT\HIDCLASS nodes
$rootNodes = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'ROOT\HIDCLASS\*' -and $_.Name -like '*vJoy*' }
Write-Host "ROOT\HIDCLASS vJoy nodes: $($rootNodes.Count)"
$rootNodes | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# Registry
$devKeys2 = Get-ChildItem $base | Where-Object { $_.PSChildName -match '^Device\d+$' }
Write-Host "Registry DeviceNN keys: $($devKeys2.Count)"

# WinMM
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinMM3 {
    [DllImport("winmm.dll")]
    public static extern int joyGetNumDevs();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JOYCAPSW {
        public ushort wMid; public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int joyGetDevCapsW(uint uJoyID, ref JOYCAPSW pjc, int cbjc);
}
'@
$numDevs = [WinMM3]::joyGetNumDevs()
$vjoyCount = 0
for ($i = 0; $i -lt $numDevs; $i++) {
    $caps = New-Object WinMM3+JOYCAPSW
    $res = [WinMM3]::joyGetDevCapsW($i, [ref]$caps, [Runtime.InteropServices.Marshal]::SizeOf($caps))
    if ($res -eq 0 -and $caps.wMid -eq 0x1234) {
        $vjoyCount++
        Write-Host "  WinMM ID=$i VID=0x$($caps.wMid.ToString('X4')) PID=0x$($caps.wPid.ToString('X4')) name='$($caps.szPname)' axes=$($caps.wNumAxes) btns=$($caps.wNumButtons)"
    }
}
Write-Host "vJoy joysticks in WinMM: $vjoyCount"

Write-Host "`n=== SUMMARY ==="
Write-Host "Registry descriptors: $($devKeys2.Count)"
Write-Host "VJOYRAWPDO (sideband): $($rawPdo.Count)"
Write-Host "HID collections (actual joysticks): $($hidDevices.Count)"
Write-Host "WinMM joysticks: $vjoyCount"

Stop-Transcript | Out-Null
