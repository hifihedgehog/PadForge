# Quick diagnostic: count vJoy HID devices properly
# VJOYRAWPDO = sideband communication PDO (always 1 per device node)
# HID\VID_1234&PID_BEAD = actual joystick devices from HID collections

Write-Host "=== vJoy HID Device Check ==="

# 1. VJOYRAWPDO count (sideband - always 1 per node)
$rawPdo = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Write-Host "`nVJOYRAWPDO devices: $($rawPdo.Count)"
$rawPdo | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# 2. HID devices with vJoy VID/PID (actual joystick collections)
$hidDevices = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'HID\VID_1234&PID_BEAD*' }
Write-Host "`nHID vJoy collection devices: $($hidDevices.Count)"
$hidDevices | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode) name=$($_.Name)" }

# 3. ROOT\HIDCLASS nodes (parent device nodes)
$rootNodes = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'ROOT\HIDCLASS\*' -and $_.Name -like '*vJoy*' }
Write-Host "`nROOT\HIDCLASS vJoy nodes: $($rootNodes.Count)"
$rootNodes | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# 4. Registry descriptor count
$base = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
if (Test-Path $base) {
    $devKeys = Get-ChildItem $base | Where-Object { $_.PSChildName -match '^Device\d+$' }
    Write-Host "`nRegistry DeviceNN keys: $($devKeys.Count)"
    $devKeys | ForEach-Object { Write-Host "  $($_.PSChildName)" }
} else {
    Write-Host "`nRegistry: vjoy\Parameters not found"
}

# 5. WinMM joystick count
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinMM {
    [DllImport("winmm.dll")]
    public static extern int joyGetNumDevs();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct JOYCAPSW {
        public ushort wMid;
        public ushort wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax;
        public uint wNumButtons, wPeriodMin, wPeriodMax;
        public uint wRmin, wRmax, wUmin, wUmax, wVmin, wVmax;
        public uint wCaps;
        public uint wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szOEMVxD;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern int joyGetDevCapsW(uint uJoyID, ref JOYCAPSW pjc, int cbjc);
}
'@

$numDevs = [WinMM]::joyGetNumDevs()
Write-Host "`nWinMM joyGetNumDevs: $numDevs"
$vjoyCount = 0
for ($i = 0; $i -lt $numDevs; $i++) {
    $caps = New-Object WinMM+JOYCAPSW
    $res = [WinMM]::joyGetDevCapsW($i, [ref]$caps, [Runtime.InteropServices.Marshal]::SizeOf($caps))
    if ($res -eq 0 -and $caps.wMid -eq 0x1234 -and $caps.wPid -eq 0xBEAD) {
        $vjoyCount++
        Write-Host "  ID=$i VID=0x$($caps.wMid.ToString('X4')) PID=0x$($caps.wPid.ToString('X4')) name='$($caps.szPname)' axes=$($caps.wNumAxes) btns=$($caps.wNumButtons)"
    }
}
Write-Host "vJoy joysticks in WinMM: $vjoyCount"
