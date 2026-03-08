<#
.SYNOPSIS
    Captures ALL PadForge screenshots for wiki and README.
.DESCRIPTION
    1. Backs up PadForge.xml
    2. Injects test macros into XML
    3. Kills and restarts PadForge
    4. Runs full UIA-based capture (13 pages + settings-drivers scrolled)
    5. Restores PadForge.xml backup
    Must run elevated (PadForge runs elevated for vJoy).
#>

param(
    [string]$OutputDir = "C:\Users\sonic\GitHub\PadForge.wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Assemblies ---------------------------------------------------------------
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# --- P/Invoke -----------------------------------------------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int sz);

    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    public static void ClickAt(int px, int py) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int nx = (int)(((long)px * 65535) / (sw - 1));
        int ny = (int)(((long)py * 65535) / (sh - 1));
        INPUT[] i = new INPUT[3];
        i[0].type = 0; i[0].mi.dx = nx; i[0].mi.dy = ny; i[0].mi.dwFlags = 0x8001;
        i[1].type = 0; i[1].mi.dx = nx; i[1].mi.dy = ny; i[1].mi.dwFlags = 0x8002;
        i[2].type = 0; i[2].mi.dx = nx; i[2].mi.dy = ny; i[2].mi.dwFlags = 0x8004;
        SendInput(3, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void MoveTo(int px, int py) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int nx = (int)(((long)px * 65535) / (sw - 1));
        int ny = (int)(((long)py * 65535) / (sh - 1));
        INPUT[] i = new INPUT[1];
        i[0].type = 0; i[0].mi.dx = nx; i[0].mi.dy = ny; i[0].mi.dwFlags = 0x8001;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void ScrollAt(int px, int py, int clicks) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        INPUT[] i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.dx = (int)(((long)px * 65535) / (sw - 1));
        i[0].mi.dy = (int)(((long)py * 65535) / (sh - 1));
        i[0].mi.mouseData = unchecked((uint)(clicks * 120));
        i[0].mi.dwFlags = 0x8001 | 0x0800;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void ForceFG(IntPtr hwnd) {
        IntPtr fg = GetForegroundWindow();
        uint fgTid, myTid = GetCurrentThreadId();
        GetWindowThreadProcessId(fg, out fgTid);
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, true);
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, false);
    }

    // Send a key via SendInput (VK code)
    public static void SendKey(ushort vk) {
        INPUT[] i = new INPUT[2];
        // KEYBDINPUT: type=1, offset 8: vk(2), scan(2), flags(4), time(4), extra(8)
        // But our INPUT struct only has MOUSEINPUT union. Reuse dx/dy fields.
        // Actually, let's use a simpler approach with SendKeys
        SendInput(0, null, 0); // no-op, we'll use SendKeys instead
    }
}
"@

[Win32]::SetProcessDPIAware() | Out-Null

# --- UIA helpers --------------------------------------------------------------
$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants

function Find-UIA {
    param(
        [System.Windows.Automation.AutomationElement]$Parent = $script:uiaWin,
        [string]$Name,
        [string]$Aid,
        [System.Windows.Automation.ControlType]$CT
    )
    $conds = @()
    if ($Name) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    }
    if ($Aid) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    }
    if ($CT) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
    }
    if ($conds.Count -eq 0) { return $null }
    $c = if ($conds.Count -eq 1) { $conds[0] }
         else { New-Object System.Windows.Automation.AndCondition($conds) }
    return $Parent.FindFirst($TD, $c)
}

function Find-AllUIA {
    param(
        [System.Windows.Automation.AutomationElement]$Parent = $script:uiaWin,
        [System.Windows.Automation.ControlType]$CT
    )
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
    return $Parent.FindAll($TD, $c)
}

function Click-El {
    param(
        [System.Windows.Automation.AutomationElement]$El,
        [int]$Delay = 800,
        [string]$Label
    )
    if (-not $El) { Write-Host "  !! NOT FOUND: $Label" -ForegroundColor Red; return $false }
    $r = $El.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0) {
        Write-Host "  !! EMPTY BOUNDS: $Label" -ForegroundColor Red; return $false
    }
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    $n = if ($Label) { $Label } else { $El.Current.Name }
    Write-Host ("  Click '{0}' at ({1},{2}) [{3}x{4}]" -f $n, $cx, $cy, [int]$r.Width, [int]$r.Height)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($cx, $cy)
    Start-Sleep -Milliseconds $Delay
    return $true
}

function Cap {
    param([string]$Name)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$r) | Out-Null
    [Win32]::MoveTo(($r.Right - 100), ($r.Bottom - 15))
    Start-Sleep -Milliseconds 200
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $p = Join-Path $script:OutputDir "$Name.png"
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $kb = [math]::Round((Get-Item $p).Length / 1024)
    Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
}

function Select-El {
    param(
        [System.Windows.Automation.AutomationElement]$El,
        [int]$Delay = 800,
        [string]$Label
    )
    if (-not $El) { Write-Host "  !! NOT FOUND: $Label" -ForegroundColor Red; return $false }
    $n = if ($Label) { $Label } else { $El.Current.Name }
    try {
        $pat = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        Write-Host "  Select '$n' (SelectionItemPattern)"
        $pat.Select()
        Start-Sleep -Milliseconds $Delay
        return $true
    } catch {
        Write-Host "  Select '$n' -- no SelectionItemPattern, falling back to click"
        return (Click-El $El -Label $Label -Delay $Delay)
    }
}

function Nav {
    param([string]$Name)
    foreach ($ctName in @("ListItem", "TreeItem")) {
        $ct = [System.Windows.Automation.ControlType]::$ctName
        $el = Find-UIA -Name $Name -CT $ct
        if ($el) { return (Select-El $el -Label $Name) }
    }
    $el = Find-UIA -Name $Name
    if ($el) { return (Select-El $el -Label $Name) }
    Write-Host "  !! Nav '$Name' not found" -ForegroundColor Red
    return $false
}

function Find-Slot {
    $skip = @("Dashboard", "Profiles", "Devices", "Add Controller", "About", "Settings",
              "", "PadForge", "Toggle navigation", "Back", "Close Navigation")
    $menuHost = Find-UIA -Aid "MenuItemsHost"
    $searchIn = if ($menuHost) { $menuHost } else { $script:uiaWin }
    $ct = [System.Windows.Automation.ControlType]::ListItem
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    $all = $searchIn.FindAll($TD, $cond)
    foreach ($item in $all) {
        $n = $item.Current.Name
        $cls = $item.Current.ClassName
        if ($cls -eq "NavigationViewItem" -and $n -notin $skip) {
            Write-Host "  Slot found: '$n' (class=$cls)"; return $item
        }
    }
    return $null
}

function Tab {
    param([string]$Name)
    $padPage = Find-UIA -Aid "PadPageView"
    $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
    $el = Find-UIA -Parent $searchIn -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton)
    if (-not $el) {
        Start-Sleep -Milliseconds 500
        $padPage = Find-UIA -Aid "PadPageView"
        $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
        $el = Find-UIA -Parent $searchIn -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton)
    }
    if (-not $el) { $el = Find-UIA -Name $Name }
    if ($el) { return (Click-El $el -Label "Tab:$Name") }
    Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
    return $false
}

# ==============================================================================
# STEP 0: Inject test macros into PadForge.xml
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 0: Inject test macros ===" -ForegroundColor Cyan

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

# Kill PadForge if running
$existing = Get-Process PadForge -EA SilentlyContinue
if ($existing) {
    Write-Host "  Stopping PadForge..."
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Backup XML
$xmlBak = "$PadForgeXml.bak"
Copy-Item $PadForgeXml $xmlBak -Force
Write-Host "  Backed up PadForge.xml"

# Inject macros
[xml]$xml = Get-Content $PadForgeXml
$ns = $xml.PadForgeSettings

# Ensure slot 0 is created
$created = $ns.AppSettings.SlotCreated.SelectNodes("Created")
if ($created.Count -gt 0) { $created[0].InnerText = "true" }

# Add macros element if not present
$macrosNode = $ns.SelectSingleNode("Macros")
if (-not $macrosNode) {
    $macrosNode = $xml.CreateElement("Macros")
    $ns.AppendChild($macrosNode) | Out-Null
}

# Clear existing macros
$macrosNode.RemoveAll()

# Add test macro 1: Quick Reload (3 actions)
$m1 = $xml.CreateElement("Macro")
$m1.SetAttribute("PadIndex", "0")
$fields1 = @{
    "Name" = "Quick Reload"
    "IsEnabled" = "true"
    "TriggerButtons" = "32"       # RB (bit 5)
    "TriggerSource" = "OutputController"
    "TriggerMode" = "OnPress"
    "ConsumeTriggerButtons" = "true"
    "RepeatMode" = "Once"
    "RepeatCount" = "1"
    "RepeatDelayMs" = "100"
}
foreach ($kv in $fields1.GetEnumerator()) {
    $el = $xml.CreateElement($kv.Key)
    $el.InnerText = $kv.Value
    $m1.AppendChild($el) | Out-Null
}
$acts1 = $xml.CreateElement("Actions")
# Action 1: Press X
$a1 = $xml.CreateElement("Action")
@{"Type"="ButtonPress";"ButtonFlags"="4";"DurationMs"="80";"KeyCode"="0";"AxisValue"="0";"AxisTarget"="LeftStickX"}.GetEnumerator() | ForEach-Object {
    $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $a1.AppendChild($e) | Out-Null
}
$acts1.AppendChild($a1) | Out-Null
# Action 2: Delay
$a2 = $xml.CreateElement("Action")
@{"Type"="Delay";"ButtonFlags"="0";"DurationMs"="120";"KeyCode"="0";"AxisValue"="0";"AxisTarget"="LeftStickX"}.GetEnumerator() | ForEach-Object {
    $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $a2.AppendChild($e) | Out-Null
}
$acts1.AppendChild($a2) | Out-Null
# Action 3: Press A
$a3 = $xml.CreateElement("Action")
@{"Type"="ButtonPress";"ButtonFlags"="1";"DurationMs"="50";"KeyCode"="0";"AxisValue"="0";"AxisTarget"="LeftStickX"}.GetEnumerator() | ForEach-Object {
    $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $a3.AppendChild($e) | Out-Null
}
$acts1.AppendChild($a3) | Out-Null
$m1.AppendChild($acts1) | Out-Null
$macrosNode.AppendChild($m1) | Out-Null

# Add test macro 2: Volume Up (key press)
$m2 = $xml.CreateElement("Macro")
$m2.SetAttribute("PadIndex", "0")
$fields2 = @{
    "Name" = "Volume Up"
    "IsEnabled" = "true"
    "TriggerButtons" = "8192"     # DPadUp (bit 13? using a value)
    "TriggerSource" = "OutputController"
    "TriggerMode" = "WhileHeld"
    "ConsumeTriggerButtons" = "false"
    "RepeatMode" = "FixedCount"
    "RepeatCount" = "3"
    "RepeatDelayMs" = "50"
}
foreach ($kv in $fields2.GetEnumerator()) {
    $el = $xml.CreateElement($kv.Key)
    $el.InnerText = $kv.Value
    $m2.AppendChild($el) | Out-Null
}
$acts2 = $xml.CreateElement("Actions")
$a4 = $xml.CreateElement("Action")
@{"Type"="VolumeUp";"ButtonFlags"="0";"DurationMs"="0";"KeyCode"="0";"AxisValue"="0";"AxisTarget"="LeftStickX"}.GetEnumerator() | ForEach-Object {
    $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $a4.AppendChild($e) | Out-Null
}
$acts2.AppendChild($a4) | Out-Null
$m2.AppendChild($acts2) | Out-Null
$macrosNode.AppendChild($m2) | Out-Null

$xml.Save($PadForgeXml)
Write-Host "  Injected 2 test macros" -ForegroundColor Green

# ==============================================================================
# STEP 1: Start PadForge
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 1: Start PadForge ===" -ForegroundColor Cyan
Start-Process $PadForgeExe
Write-Host "  Waiting for PadForge to start..."
$timeout = 30
$started = $false
for ($i = 0; $i -lt $timeout; $i++) {
    Start-Sleep -Seconds 1
    $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.MainWindowHandle -ne 0) {
        $started = $true
        break
    }
}
if (-not $started) {
    Write-Host "  !! PadForge failed to start in ${timeout}s" -ForegroundColor Red
    Copy-Item $xmlBak $PadForgeXml -Force
    exit 1
}

$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "  PadForge PID=$($proc.Id) HWND=$hwnd" -ForegroundColor Green

# ==============================================================================
# STEP 2: Setup window
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 2: Setup window ===" -ForegroundColor Cyan

[Win32]::ForceFG($hwnd)
[Win32]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE
Start-Sleep -Milliseconds 1000
[Win32]::SetWindowPos($hwnd, [Win32]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 500

$rect = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$winW = $rect.Right - $rect.Left; $winH = $rect.Bottom - $rect.Top
Write-Host "  Window: ${winW}x${winH} at ($($rect.Left),$($rect.Top))"

$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
if (-not $uiaWin) {
    Write-Host "  !! UIA fail" -ForegroundColor Red
    Copy-Item $xmlBak $PadForgeXml -Force
    exit 1
}
Write-Host "  UIA: '$($uiaWin.Current.Name)'"

# Expand sidebar if compact
$hamburger = Find-UIA -Aid "TogglePaneButton" -CT ([System.Windows.Automation.ControlType]::Button)
if (-not $hamburger) {
    $hamburger = Find-UIA -Name "Toggle navigation" -CT ([System.Windows.Automation.ControlType]::Button)
}
if ($hamburger) {
    $dash = Find-UIA -Name "Dashboard"
    if ($dash -and $dash.Current.BoundingRectangle.Width -lt 120) {
        Write-Host "  Sidebar compact -- expanding..."
        Click-El $hamburger -Label "Hamburger" -Delay 500
    } else {
        Write-Host "  Sidebar already expanded"
    }
}

# Warm-up click
[Win32]::ForceFG($hwnd)
Start-Sleep -Milliseconds 200
$wr = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
[Win32]::ClickAt([int](($wr.Left + $wr.Right) / 2), ($wr.Top + 30))
Start-Sleep -Milliseconds 500

# ==============================================================================
# STEP 3: Capture all pages
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3: Capture pages ===" -ForegroundColor Cyan

# 1. Dashboard
Write-Host "[1/15] Dashboard"
Nav "Dashboard"; Cap "dashboard"

# 2. Profiles
Write-Host "[2/15] Profiles"
Nav "Profiles"; Cap "profiles"

# 3. Devices
Write-Host "[3/15] Devices"
Nav "Devices"; Cap "devices"

# 4-10. Controller slot + tabs
Write-Host "[4/15] Controller (3D view)"
$slot = Find-Slot
if ($slot) {
    Select-El $slot -Label "Controller Slot" -Delay 1500

    # Click first RadioButton in PadPageView (3D tab)
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) {
            Click-El $tabs[0] -Label "3D View Tab" -Delay 2000
        }
    }
    Cap "pad-controller-3d"

    # 5. 2D view toggle
    Write-Host "[5/15] Controller (2D view)"
    $ppRect = $padPage.Current.BoundingRectangle
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    Write-Host "  Clicking 2D toggle at ($toggleX,$toggleY)"
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 1000
    Cap "pad-controller-2d"
    # Switch back to 3D
    Start-Sleep -Milliseconds 200
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500

    # 6. Macros -- click the tab via physical click, then select first macro + action
    Write-Host "[6/15] Macros"
    if (Tab "Macros") {
        Start-Sleep -Milliseconds 500
        $lists = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::List)
        $macroClicked = $false
        foreach ($list in $lists) {
            $liCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            $items = $list.FindAll($TC, $liCond)
            if ($items.Count -gt 0) {
                Click-El $items[0] -Label "Macro: $($items[0].Current.Name)"
                $macroClicked = $true
                Start-Sleep -Milliseconds 600
                # Select first action in second list
                $lists2 = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::List)
                foreach ($l2 in $lists2) {
                    if ([System.Windows.Automation.Automation]::Compare($l2, $list)) { continue }
                    $acts = $l2.FindAll($TC, $liCond)
                    if ($acts.Count -gt 0) {
                        Click-El $acts[0] -Label "Action: $($acts[0].Current.Name)" -Delay 400
                    }
                    break
                }
                break
            }
        }
        if (-not $macroClicked) { Write-Host "  !! No macros found" -ForegroundColor Yellow }
    }
    Cap "pad-macros"

    # 7. Mappings
    Write-Host "[7/15] Mappings"
    Tab "Mappings"; Cap "pad-mappings"

    # 8. Sticks
    Write-Host "[8/15] Sticks"
    Tab "Sticks"; Cap "pad-sticks"

    # 9. Triggers
    Write-Host "[9/15] Triggers"
    Tab "Triggers"; Cap "pad-triggers"

    # 10. Force Feedback
    Write-Host "[10/15] Force Feedback"
    Tab "Force Feedback"; Cap "pad-forcefeedback"

} else {
    Write-Host "  !! No controller slot found" -ForegroundColor Red
}

# 11. Settings (top)
Write-Host "[11/15] Settings"
Nav "Settings"
Start-Sleep -Milliseconds 500
Cap "settings"

# 12. Settings (scrolled down to drivers)
Write-Host "[12/15] Settings-Drivers (scrolled)"
# Click content area then scroll down
$sr = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$sr) | Out-Null
$cx = [int](($sr.Left + $sr.Right) / 2 + 100)
$cy = [int](($sr.Top + $sr.Bottom) / 2)
[Win32]::ForceFG($hwnd)
[Win32]::ClickAt($cx, $cy)
Start-Sleep -Milliseconds 300
# Scroll down substantially
for ($i = 0; $i -lt 25; $i++) {
    [Win32]::ScrollAt($cx, $cy, -3)
    Start-Sleep -Milliseconds 50
}
Start-Sleep -Milliseconds 600
Cap "settings-drivers"
# Scroll back up
for ($i = 0; $i -lt 25; $i++) {
    [Win32]::ScrollAt($cx, $cy, 3)
    Start-Sleep -Milliseconds 30
}

# 13. About
Write-Host "[13/15] About"
Nav "About"; Cap "about"

# 14. Add Controller popup
Write-Host "[14/15] Add Controller popup"
Nav "Dashboard"; Start-Sleep -Milliseconds 500
$addEl = Find-UIA -Name "Add Controller"
if ($addEl) {
    Click-El $addEl -Label "Add Controller" -Delay 800
    Cap "add-controller-popup"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300
}

# 15. Web controller screenshots
Write-Host "[15/15] Web controller (Edge headless)"
$webPort = 8080
try {
    # Landing page
    $edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
    $landingOut = Join-Path $OutputDir "web-landing.png"
    & $edgePath --headless --disable-gpu --screenshot="$landingOut" --window-size=1280,720 "http://localhost:${webPort}/" 2>$null
    Start-Sleep -Seconds 3
    if (Test-Path $landingOut) {
        Write-Host "  >> web-landing.png" -ForegroundColor Green
    }
    # Controller page
    $ctrlOut = Join-Path $OutputDir "web-controller.png"
    & $edgePath --headless --disable-gpu --screenshot="$ctrlOut" --window-size=1280,720 "http://localhost:${webPort}/controller.html?layout=xbox360" 2>$null
    Start-Sleep -Seconds 3
    if (Test-Path $ctrlOut) {
        Write-Host "  >> web-controller.png" -ForegroundColor Green
    }
} catch {
    Write-Host "  !! Web screenshots failed: $($_.Exception.Message)" -ForegroundColor Yellow
}

# ==============================================================================
# STEP 4: Cleanup
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 4: Cleanup ===" -ForegroundColor Cyan

[Win32]::SetWindowPos($hwnd, [Win32]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null

# Stop PadForge, restore XML
Write-Host "  Stopping PadForge..."
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item $xmlBak $PadForgeXml -Force
Remove-Item $xmlBak -Force
Write-Host "  Restored PadForge.xml from backup" -ForegroundColor Green

# Restart PadForge with original config
Write-Host "  Restarting PadForge with original config..."
Start-Process $PadForgeExe

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Cyan
Write-Host "Screenshots in: $OutputDir"
Write-Host ""
Get-ChildItem "$OutputDir\*.png" | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0} ({1}KB)" -f $_.Name, [math]::Round($_.Length / 1024))
}
