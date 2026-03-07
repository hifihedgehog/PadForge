# Quick diagnostic: test disable/enable and count ghost devices
$out = @()
$out += "=== vJoy Restart Diagnostic $(Get-Date) ==="

# Check WinMM before anything
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinMMDiag {
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

function Count-VJoy {
    $count = 0
    $details = @()
    $numDevs = [WinMMDiag]::joyGetNumDevs()
    for ($i = 0; $i -lt $numDevs; $i++) {
        $caps = New-Object WinMMDiag+JOYCAPSW
        $res = [WinMMDiag]::joyGetDevCapsW($i, [ref]$caps, [Runtime.InteropServices.Marshal]::SizeOf($caps))
        if ($res -eq 0 -and $caps.wMid -eq 0x1234 -and $caps.wPid -eq 0xBEAD) {
            $count++
            $details += "  ID=$i axes=$($caps.wNumAxes) btns=$($caps.wNumButtons) maxBtns=$($caps.wMaxButtons) caps=0x$($caps.wCaps.ToString('X'))"
        }
    }
    return @{ Count = $count; Details = $details }
}

$before = Count-VJoy
$out += "BEFORE: $($before.Count) vJoy devices"
$before.Details | ForEach-Object { $out += $_ }

# List all ROOT\HIDCLASS nodes
$out += ""
$out += "ROOT\HIDCLASS nodes:"
$pnp = pnputil /enum-devices /class HIDClass 2>&1
$currentId = $null
$currentDesc = $null
$currentStatus = $null
foreach ($line in ($pnp -split "`n")) {
    $t = $line.Trim()
    if ($t -match 'Instance ID\s*:\s*(.+)') { $currentId = $matches[1].Trim(); $currentDesc = $null; $currentStatus = $null }
    elseif ($t -match 'Device Description\s*:\s*(.+)') { $currentDesc = $matches[1].Trim() }
    elseif ($t -match 'Status\s*:\s*(.+)') {
        $currentStatus = $matches[1].Trim()
        if ($currentId -match '^ROOT\\HIDCLASS\\') {
            $out += "  $currentId | $currentDesc | $currentStatus"
        }
        $currentId = $null
    }
    elseif (-not $t) { $currentId = $null }
}

# Try disable
$out += ""
$out += "Disabling ROOT\HIDCLASS\0000..."
$disResult = pnputil /disable-device "ROOT\HIDCLASS\0000" 2>&1
$out += "  Result: $($disResult -join ' | ')"

Start-Sleep -Seconds 2

$afterDisable = Count-VJoy
$out += "AFTER DISABLE: $($afterDisable.Count) vJoy devices"

# Try enable
$out += ""
$out += "Enabling ROOT\HIDCLASS\0000..."
$enResult = pnputil /enable-device "ROOT\HIDCLASS\0000" 2>&1
$out += "  Result: $($enResult -join ' | ')"

Start-Sleep -Seconds 3

$afterEnable = Count-VJoy
$out += "AFTER ENABLE: $($afterEnable.Count) vJoy devices"
$afterEnable.Details | ForEach-Object { $out += $_ }

$out | Out-File 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_diag_restart_log.txt' -Encoding utf8 -Force
$out | ForEach-Object { Write-Host $_ }
