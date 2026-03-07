# Add vJoy controller via UI automation and check HID state
param(
    [int]$Count = 1,
    [switch]$CheckOnly
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$auto = [System.Windows.Automation.AutomationElement]
$cond = [System.Windows.Automation.PropertyCondition]
$tree = [System.Windows.Automation.TreeScope]
$invoke = [System.Windows.Automation.InvokePattern]::Pattern

function Find-Element($parent, $name, $controlType) {
    $nameCond = New-Object $cond([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($controlType) {
        $typeCond = New-Object $cond([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
        $andCond = New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)
        return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $andCond)
    }
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
}

function Click-Element($element) {
    $pattern = $element.GetCurrentPattern($invoke)
    $pattern.Invoke()
}

# Find PadForge window
$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "PadForge not running!"; exit 1 }
$root = $auto::FromHandle($proc.MainWindowHandle)
if (-not $root) { Write-Host "Cannot find PadForge window!"; exit 1 }
Write-Host "Found PadForge window"

if (-not $CheckOnly) {
    for ($i = 0; $i -lt $Count; $i++) {
        Write-Host "`nAdding vJoy controller #$($i+1)..."

        # Click "Add Controller" button
        $addBtn = Find-Element $root "Add Controller"
        if (-not $addBtn) { Write-Host "Cannot find 'Add Controller' button!"; exit 1 }
        Click-Element $addBtn
        Start-Sleep -Milliseconds 500

        # Click "vJoy" button in popup
        $vjoyBtn = Find-Element $root "vJoy"
        if (-not $vjoyBtn) { Write-Host "Cannot find 'vJoy' button in popup!"; exit 1 }
        Click-Element $vjoyBtn
        Write-Host "Clicked vJoy button"

        # Wait for device to be created
        Start-Sleep -Seconds 8
    }
}

# Now run the HID check
Write-Host "`n=== Post-add HID Check ==="

# VJOYRAWPDO count
$rawPdo = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Write-Host "VJOYRAWPDO devices: $($rawPdo.Count)"
$rawPdo | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# HID devices with vJoy VID/PID
$hidDevices = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'HID\VID_1234&PID_BEAD*' }
Write-Host "HID vJoy collection devices: $($hidDevices.Count)"
$hidDevices | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode) name=$($_.Name)" }

# ROOT\HIDCLASS nodes
$rootNodes = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like 'ROOT\HIDCLASS\*' -and $_.Name -like '*vJoy*' }
Write-Host "ROOT\HIDCLASS vJoy nodes: $($rootNodes.Count)"
$rootNodes | ForEach-Object { Write-Host "  $($_.DeviceID) [$($_.Status)] err=$($_.ConfigManagerErrorCode)" }

# Registry
$base = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
if (Test-Path $base) {
    $devKeys = Get-ChildItem $base | Where-Object { $_.PSChildName -match '^Device\d+$' }
    Write-Host "Registry DeviceNN keys: $($devKeys.Count)"
    $devKeys | ForEach-Object { Write-Host "  $($_.PSChildName)" }
}

# WinMM
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WinMM2 {
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
$numDevs = [WinMM2]::joyGetNumDevs()
$vjoyCount = 0
for ($i = 0; $i -lt $numDevs; $i++) {
    $caps = New-Object WinMM2+JOYCAPSW
    $res = [WinMM2]::joyGetDevCapsW($i, [ref]$caps, [Runtime.InteropServices.Marshal]::SizeOf($caps))
    if ($res -eq 0 -and $caps.wMid -eq 0x1234 -and $caps.wPid -eq 0xBEAD) {
        $vjoyCount++
        Write-Host "  WinMM ID=$i name='$($caps.szPname)' axes=$($caps.wNumAxes) btns=$($caps.wNumButtons)"
    }
}
Write-Host "vJoy joysticks in WinMM: $vjoyCount"
