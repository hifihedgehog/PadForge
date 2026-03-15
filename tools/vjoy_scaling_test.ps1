# Test vJoy 1->2 scaling via UI Automation
# Must run elevated (PadForge is admin)
param([switch]$Elevated)
if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        exit
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);' -Name 'W32' -Namespace 'I' -ErrorAction SilentlyContinue

$diagLog = "C:\PadForge\vjoy_diag.log"
$regBase = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_scaling_test_log.txt"

function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}
function Get-JoyCplVJoyCount {
    $count = 0
    try {
        $ids = (pnputil /enum-devices /class HIDClass 2>$null) -join "`n"
        $found = [regex]::Matches($ids, 'ROOT\\HIDCLASS\\(\d+)')
        foreach ($m in $found) { $count++ }
    } catch {}
    return $count
}
function Get-RegDeviceCount {
    if (-not (Test-Path $regBase)) { return 0 }
    return @(Get-ChildItem $regBase -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^Device\d+$' }).Count
}

# Get PadForge window
$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Log "PadForge not running!"; exit 1 }
[I.W32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
Log "Connected to PadForge (PID $($proc.Id))"

# Find current state
$reg = Get-RegDeviceCount
$joy = Get-JoyCplVJoyCount
Log "Initial: regDevices=$reg, joyCpl=$joy"

# Click "Add Controller" in sidebar
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, "Add Controller")
$addBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $addBtn) { Log "ERROR: 'Add Controller' not found"; exit 1 }

# Click it using InvokePattern or SelectionItemPattern
$patterns = @(
    [System.Windows.Automation.InvokePatternIdentifiers]::Pattern,
    [System.Windows.Automation.SelectionItemPatternIdentifiers]::Pattern
)
$clicked = $false
foreach ($p in $patterns) {
    try {
        $pat = $addBtn.GetCurrentPattern($p)
        if ($pat -is [System.Windows.Automation.InvokePattern]) { $pat.Invoke(); $clicked = $true; break }
        if ($pat -is [System.Windows.Automation.SelectionItemPattern]) { $pat.Select(); $clicked = $true; break }
    } catch {}
}
if (-not $clicked) {
    # Fallback: click by coordinates
    $rect = $addBtn.Current.BoundingRectangle
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
    Add-Type -AssemblyName System.Windows.Forms
    Start-Sleep -Milliseconds 100
    Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(int f,int dx,int dy,int d,int e);' -Name 'Mouse' -Namespace 'U' -ErrorAction SilentlyContinue
    [U.Mouse]::mouse_event(2,0,0,0,0)  # LBUTTONDOWN
    [U.Mouse]::mouse_event(4,0,0,0,0)  # LBUTTONUP
    $clicked = $true
}
Log "Clicked 'Add Controller': $clicked"
Start-Sleep -Milliseconds 1000

# Find and click "vJoy" button in popup
$vjoyBtn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "vJoy")))
if ($vjoyBtn) {
    try {
        $inv = $vjoyBtn.GetCurrentPattern([System.Windows.Automation.InvokePatternIdentifiers]::Pattern)
        $inv.Invoke()
        Log "Clicked 'vJoy' button"
    } catch {
        Log "vJoy button found but click failed: $_"
    }
} else {
    Log "ERROR: 'vJoy' button not found in popup"
    # Try finding all buttons
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $buttons) {
        $n = $b.Current.Name
        if ($n -and $n -ne '' -and $n -ne '?') { Log "  Button: '$n'" }
    }
    exit 1
}

Start-Sleep -Milliseconds 3000

# Monitor for scaling
Log "Waiting for descriptor count and joy.cpl to update..."
$target = $reg + 1
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 2000
    $newReg = Get-RegDeviceCount
    $newJoy = Get-JoyCplVJoyCount
    Log "[poll-$i] regDevices=$newReg, joyCpl=$newJoy"
    if ($newReg -ge $target -and $newJoy -ge $target) {
        Log "SUCCESS: Both registry and joy.cpl show $target+ devices!"
        break
    }
}

# Print diag log tail
Log ""
Log "=== Last 40 lines of vjoy_diag.log ==="
if (Test-Path $diagLog) {
    Get-Content $diagLog -Tail 40 | ForEach-Object { Log "  $_" }
}
