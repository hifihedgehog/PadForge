$out = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_hid_enum.txt'
$results = @()
$results += "=== All HID children of ROOT\HIDCLASS\0000 ==="
# Get child devices of the vJoy node
$children = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'HID\*' }
foreach ($c in $children) {
    # Check if parent is ROOT\HIDCLASS
    $rel = Get-CimInstance -Query "ASSOCIATORS OF {Win32_PnPEntity.DeviceID='$($c.DeviceID.Replace('\','\\'))'} WHERE AssocClass=Win32_DeviceConnection" -ErrorAction SilentlyContinue
    if ($rel) {
        foreach ($r in $rel) {
            if ($r.DeviceID -like 'ROOT\HIDCLASS\*') {
                $results += "$($c.DeviceID) | $($c.Name) | $($c.Status) | parent=$($r.DeviceID)"
            }
        }
    }
}

$results += ""
$results += "=== Simpler: pnputil /enum-devices under ROOT\HIDCLASS ==="
$pnp = pnputil /enum-devices /connected 2>&1
$currentId = $null
$currentDesc = $null
foreach ($line in ($pnp -split "`n")) {
    $trimmed = $line.Trim()
    if ($trimmed -match 'Instance ID\s*:\s*(.+)') { $currentId = $matches[1].Trim(); $currentDesc = $null }
    elseif ($trimmed -match 'Device Description\s*:\s*(.+)') { $currentDesc = $matches[1].Trim() }
    elseif ($trimmed -match 'Status\s*:\s*(.+)' -and $currentId -like 'HID\*' -and $currentDesc -like '*joystick*') {
        $results += "  $currentId | $currentDesc | $($matches[1].Trim())"
    }
    elseif (-not $trimmed) { $currentId = $null }
}

$results += ""
$results += "=== WinMM check ==="
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinMM4 {
    [DllImport("winmm.dll")] public static extern int joyGetNumDevs();
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
$numDevs = [WinMM4]::joyGetNumDevs()
for ($i = 0; $i -lt $numDevs; $i++) {
    $caps = New-Object WinMM4+JOYCAPSW
    $res = [WinMM4]::joyGetDevCapsW($i, [ref]$caps, [Runtime.InteropServices.Marshal]::SizeOf($caps))
    if ($res -eq 0 -and $caps.wMid -eq 0x1234) {
        $results += "  WinMM ID=$i VID=0x$($caps.wMid.ToString('X4')) PID=0x$($caps.wPid.ToString('X4')) axes=$($caps.wNumAxes) btns=$($caps.wNumButtons)"
    }
}

$results | Out-File $out -Encoding utf8 -Force
Write-Host "Done. Output in $out"
