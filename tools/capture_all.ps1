<#
.SYNOPSIS
    Captures ALL PadForge screenshots for wiki and README.
.DESCRIPTION
    1. Backs up PadForge.xml
    2. Injects test data (4 slot types, macros with mouse/AppVolume, sensitivity curves, profiles)
    3. Kills and restarts PadForge
    4. Runs full UIA-based capture (~30 screenshots)
    5. Restores PadForge.xml backup
    Must run elevated (PadForge runs elevated for vJoy).
#>

param(
    [string]$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$logPath = "C:\PadForge\capture_log.txt"
Start-Transcript -Path $logPath -Force | Out-Null

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

function Find-AllSlots {
    param([int]$Retries = 3, [int]$DelayMs = 1500)
    $skip = @("Dashboard", "Profiles", "Devices", "Add Controller", "About", "Settings",
              "", "PadForge", "Toggle navigation", "Back", "Close Navigation")
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $menuHost = Find-UIA -Aid "MenuItemsHost"
        $searchIn = if ($menuHost) { $menuHost } else { $script:uiaWin }
        $ct = [System.Windows.Automation.ControlType]::ListItem
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        $all = $searchIn.FindAll($TD, $cond)
        $slots = @()
        foreach ($item in $all) {
            $n = $item.Current.Name
            $cls = $item.Current.ClassName
            if ($cls -eq "NavigationViewItem" -and ($n -match '^Pad\d+$' -or ($n -notin $skip -and $n.Length -gt 0))) {
                Write-Host "    Slot: '$n' (class=$cls)"
                $slots += $item
            }
        }
        if ($slots.Count -gt 0) {
            Write-Host "  Found $($slots.Count) slot(s) on attempt $attempt"
            return $slots
        }
        # Diagnostic: list ALL NavigationViewItems
        if ($attempt -eq 1) {
            Write-Host "  Diagnostic: All nav items on attempt 1:"
            foreach ($item in $all) {
                Write-Host "    Name='$($item.Current.Name)' Class='$($item.Current.ClassName)'"
            }
        }
        Write-Host "  No slots found (attempt $attempt/$Retries), waiting ${DelayMs}ms..."
        Start-Sleep -Milliseconds $DelayMs
    }
    Write-Host "  !! No slots after $Retries retries" -ForegroundColor Red
    return @()
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

function ScrollContent {
    param([int]$Clicks = -15)
    $sr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$sr) | Out-Null
    $cx = [int](($sr.Left + $sr.Right) / 2 + 100)
    $cy = [int](($sr.Top + $sr.Bottom) / 2)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt($cx, $cy)
    Start-Sleep -Milliseconds 300
    $step = if ($Clicks -lt 0) { -3 } else { 3 }
    $count = [math]::Abs([math]::Ceiling($Clicks / $step))
    for ($i = 0; $i -lt $count; $i++) {
        [Win32]::ScrollAt($cx, $cy, $step)
        Start-Sleep -Milliseconds 50
    }
    Start-Sleep -Milliseconds 600
}

# ==============================================================================
# STEP 0: Inject test data into PadForge.xml
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 0: Inject test data ===" -ForegroundColor Cyan

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

# Kill PadForge if running (double-kill pattern — may auto-restart via startup entry)
$existing = Get-Process PadForge -EA SilentlyContinue
if ($existing) {
    Write-Host "  Stopping PadForge (first kill)..."
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 3
    # Second kill in case it auto-restarted
    $respawned = Get-Process PadForge -EA SilentlyContinue
    if ($respawned) {
        Write-Host "  PadForge respawned -- killing again..."
        $respawned | Stop-Process -Force
    }
    Start-Sleep -Seconds 2
}

# Backup XML
$xmlBak = "$PadForgeXml.bak"
Copy-Item $PadForgeXml $xmlBak -Force
Write-Host "  Backed up PadForge.xml"

# Load and modify XML
[xml]$xml = Get-Content $PadForgeXml
$ns = $xml.PadForgeSettings

# --- Inject a test profile (profiles only -- slots created via UI later) ---
$profilesNode = $ns.SelectSingleNode("Profiles")
if (-not $profilesNode) {
    $profilesNode = $xml.CreateElement("Profiles")
    $ns.AppendChild($profilesNode) | Out-Null
}
if ($profilesNode.ChildNodes.Count -eq 0) {
    $prof = $xml.CreateElement("Profile")
    @{
        "Name" = "Rocket League"
        "Executables" = "RocketLeague.exe"
        "IsActive" = "false"
    }.GetEnumerator() | ForEach-Object {
        $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $prof.AppendChild($e) | Out-Null
    }
    $profilesNode.AppendChild($prof) | Out-Null
    Write-Host "  Injected test profile"
}
$xml.Save($PadForgeXml)
Write-Host "  Saved modified PadForge.xml" -ForegroundColor Green

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
# STEP 2b: Create 4 controller slots via Add Controller popup
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 2b: Create controller slots via UI ===" -ForegroundColor Cyan

# Helper: click Add Controller sidebar item, then click a type button by AutomationId
$popupCaptured = $false
function Add-SlotViaPopup {
    param([string]$TypeBtnAid, [string]$TypeLabel)
    # Click "Add Controller" in sidebar
    $addNav = Find-UIA -Name "Add Controller"
    if (-not $addNav) { Write-Host "  !! Add Controller nav not found" -ForegroundColor Red; return $false }
    Click-El $addNav -Label "Add Controller" -Delay 600
    # Capture the popup on first open (shows all 5 type buttons)
    if (-not $script:popupCaptured) {
        Cap "add-controller-popup"
        $script:popupCaptured = $true
    }
    # Find and click the type button
    $typeBtn = Find-UIA -Aid $TypeBtnAid
    if (-not $typeBtn) {
        Write-Host "  !! Type button '$TypeBtnAid' not found in popup" -ForegroundColor Red
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 300
        return $false
    }
    Click-El $typeBtn -Label $TypeLabel -Delay 2000
    return $true
}

# Create: Xbox360, KBM, vJoy, MIDI (order matters for slot indices)
$slotTypes = @(
    @{ Aid = "AddXbox360Btn"; Label = "Xbox 360" },
    @{ Aid = "AddKeyboardMouseBtn"; Label = "Keyboard+Mouse" },
    @{ Aid = "AddVJoyBtn"; Label = "vJoy" },
    @{ Aid = "AddMidiBtn"; Label = "MIDI" }
)
foreach ($st in $slotTypes) {
    Write-Host "  Creating $($st.Label) slot..."
    $ok = Add-SlotViaPopup -TypeBtnAid $st.Aid -TypeLabel $st.Label
    if ($ok) { Write-Host "  Created $($st.Label)" -ForegroundColor Green }
    Start-Sleep -Milliseconds 500
}

# Verify slots appeared
$slots = @(Find-AllSlots)
Write-Host "  Slots after creation: $($slots.Count)"

# ==============================================================================
# STEP 3: Capture all pages
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3: Capture pages ===" -ForegroundColor Cyan
$n = 0
$total = 30

function Next { $script:n++; return $script:n }

# ---- 1. Dashboard ----
Write-Host "[$(Next)/$total] Dashboard"
Nav "Dashboard"; Start-Sleep -Milliseconds 1000; Cap "dashboard"

# ---- 2. Profiles ----
Write-Host "[$(Next)/$total] Profiles"
Nav "Profiles"; Cap "profiles"

# ---- 3. Devices ----
Write-Host "[$(Next)/$total] Devices"
Nav "Devices"; Cap "devices"

# ---- 4-12. Xbox360 slot (slot 0) ----
Write-Host ""
Write-Host "--- Xbox360 Slot ---" -ForegroundColor Yellow
$slots = @(Find-AllSlots)
Write-Host "  Found $($slots.Count) slot(s)"
if ($slots.Count -ge 1) {
    Select-El $slots[0] -Label "Xbox360 Slot" -Delay 1500

    # 4. Controller 3D view
    Write-Host "[$(Next)/$total] Controller - 3D view"
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "3D View Tab" -Delay 2000 }
    }
    Cap "pad-controller-3d"

    # 5. Controller 2D view
    Write-Host "[$(Next)/$total] Controller - 2D view"
    $ppRect = $padPage.Current.BoundingRectangle
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 1000
    Cap "pad-controller-2d"
    # Switch back to 3D
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500

    # 6. Macros (select first macro + first action)
    Write-Host "[$(Next)/$total] Macros"
    if (Tab "Macros") {
        Start-Sleep -Milliseconds 500
        $lists = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::List)
        foreach ($list in $lists) {
            $liCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            $items = $list.FindAll($TC, $liCond)
            if ($items.Count -gt 0) {
                Click-El $items[0] -Label "Macro: $($items[0].Current.Name)"
                Start-Sleep -Milliseconds 600
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
    }
    Cap "pad-macros"

    # 7. Mappings
    Write-Host "[$(Next)/$total] Mappings"
    Tab "Mappings"; Cap "pad-mappings"

    # 8. Sticks (default view with curves and dead zone shapes visible)
    Write-Host "[$(Next)/$total] Sticks"
    Tab "Sticks"; Start-Sleep -Milliseconds 500; Cap "pad-sticks"

    # 9. Sticks — dead zone shape dropdown open
    Write-Host "[$(Next)/$total] Sticks - dead zone shape dropdown"
    $combos = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::ComboBox)
    $dzDropdown = $null
    foreach ($cb in $combos) {
        try {
            $ep = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            # Check if it's a dead zone shape combo by expanding and looking for "Scaled Radial"
            $ep.Expand()
            Start-Sleep -Milliseconds 400
            $srItem = Find-UIA -Parent $cb -Name "Scaled Radial"
            if ($srItem) {
                $dzDropdown = $cb
                Cap "pad-sticks-deadzone-dropdown"
                $ep.Collapse()
                Start-Sleep -Milliseconds 300
                break
            }
            $ep.Collapse()
            Start-Sleep -Milliseconds 200
        } catch {}
    }
    if (-not $dzDropdown) { Write-Host "  !! Dead zone shape dropdown not found" -ForegroundColor Yellow }

    # 10. Sticks — sensitivity preset dropdown open
    Write-Host "[$(Next)/$total] Sticks - sensitivity preset dropdown"
    $presetFound = $false
    foreach ($cb in $combos) {
        if ($cb -eq $dzDropdown) { continue }
        try {
            $ep = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $ep.Expand()
            Start-Sleep -Milliseconds 400
            $linItem = Find-UIA -Parent $cb -Name "Linear"
            $sCurveItem = Find-UIA -Parent $cb -Name "S-Curve"
            if ($linItem -and $sCurveItem) {
                Cap "pad-sticks-sensitivity-dropdown"
                $ep.Collapse()
                Start-Sleep -Milliseconds 300
                $presetFound = $true
                break
            }
            $ep.Collapse()
            Start-Sleep -Milliseconds 200
        } catch {}
    }
    if (-not $presetFound) { Write-Host "  !! Sensitivity preset dropdown not found" -ForegroundColor Yellow }

    # 11. Triggers
    Write-Host "[$(Next)/$total] Triggers"
    Tab "Triggers"; Start-Sleep -Milliseconds 500; Cap "pad-triggers"

    # 12. Triggers — sensitivity preset dropdown open
    Write-Host "[$(Next)/$total] Triggers - sensitivity preset dropdown"
    $combos = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::ComboBox)
    $trigPresetFound = $false
    foreach ($cb in $combos) {
        try {
            $ep = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $ep.Expand()
            Start-Sleep -Milliseconds 400
            $linItem = Find-UIA -Parent $cb -Name "Linear"
            $aggItem = Find-UIA -Parent $cb -Name "Aggressive"
            if ($linItem -and $aggItem) {
                Cap "pad-triggers-sensitivity-dropdown"
                $ep.Collapse()
                Start-Sleep -Milliseconds 300
                $trigPresetFound = $true
                break
            }
            $ep.Collapse()
            Start-Sleep -Milliseconds 200
        } catch {}
    }
    if (-not $trigPresetFound) { Write-Host "  !! Trigger sensitivity preset dropdown not found" -ForegroundColor Yellow }

    # 13. Force Feedback
    Write-Host "[$(Next)/$total] Force Feedback"
    Tab "Force Feedback"; Cap "pad-forcefeedback"

} else {
    Write-Host "  !! No controller slots found" -ForegroundColor Red
}

# ---- 14. KBM slot (slot 1) ----
Write-Host ""
Write-Host "--- KBM Slot ---" -ForegroundColor Yellow
$slots = @(Find-AllSlots)
if ($slots.Count -ge 2) {
    Write-Host "[$(Next)/$total] Keyboard+Mouse preview"
    Select-El $slots[1] -Label "KBM Slot" -Delay 2000
    # Click first tab (Controller) to show KBM preview
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "KBM Controller Tab" -Delay 1500 }
    }
    Cap "pad-kbm-preview"
} else {
    Write-Host "  !! KBM slot not found (only $($slots.Count) slots)" -ForegroundColor Yellow
    $n++
}

# ---- 15. vJoy slot (slot 2) ----
Write-Host ""
Write-Host "--- vJoy Slot ---" -ForegroundColor Yellow
$slots = @(Find-AllSlots)
if ($slots.Count -ge 3) {
    Write-Host "[$(Next)/$total] vJoy config bar"
    Select-El $slots[2] -Label "vJoy Slot" -Delay 2000
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "vJoy Controller Tab" -Delay 1500 }
    }
    Cap "pad-vjoy-configbar"

    # 16. vJoy schematic view
    Write-Host "[$(Next)/$total] vJoy schematic view"
    $ppRect = (Find-UIA -Aid "PadPageView").Current.BoundingRectangle
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 1000
    Cap "pad-vjoy-schematic"
    # Switch back
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500
} else {
    Write-Host "  !! vJoy slot not found" -ForegroundColor Yellow
    $n += 2
}

# ---- 17. MIDI slot (slot 3) ----
Write-Host ""
Write-Host "--- MIDI Slot ---" -ForegroundColor Yellow
$slots = @(Find-AllSlots)
if ($slots.Count -ge 4) {
    Write-Host "[$(Next)/$total] MIDI config bar"
    Select-El $slots[3] -Label "MIDI Slot" -Delay 2000
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "MIDI Controller Tab" -Delay 1500 }
    }
    Cap "pad-midi-configbar"
} else {
    Write-Host "  !! MIDI slot not found" -ForegroundColor Yellow
    $n++
}

# ---- 18-20. Settings (three scroll positions) ----
Write-Host ""
Write-Host "--- Settings ---" -ForegroundColor Yellow

# 18. Settings top
Write-Host "[$(Next)/$total] Settings - top"
Nav "Settings"
Start-Sleep -Milliseconds 500
Cap "settings"

# 19. Settings mid (HidHide whitelist area)
Write-Host "[$(Next)/$total] Settings - HidHide / input engine"
ScrollContent -Clicks -10
Cap "settings-hidhide"

# 20. Settings bottom (drivers)
Write-Host "[$(Next)/$total] Settings - drivers"
ScrollContent -Clicks -20
Cap "settings-drivers"

# Scroll back up
ScrollContent -Clicks 40

# ---- 21. About ----
Write-Host "[$(Next)/$total] About"
Nav "About"; Cap "about"

# ---- 22. Add Controller popup (already captured in Step 2b) ----
Write-Host "[$(Next)/$total] Add Controller popup -- already captured in Step 2b"

# ---- 23-24. Web controller ----
Write-Host "[$(Next)/$total] Web controller screenshots"
$webPort = 8080
try {
    $edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
    # Landing page
    $landingOut = Join-Path $OutputDir "web-landing.png"
    & $edgePath --headless --disable-gpu --screenshot="$landingOut" --window-size=1280,720 "http://localhost:${webPort}/" 2>$null
    Start-Sleep -Seconds 3
    if (Test-Path $landingOut) { Write-Host "  >> web-landing.png" -ForegroundColor Green }
    # Controller page
    Write-Host "[$(Next)/$total] Web controller - gamepad"
    $ctrlOut = Join-Path $OutputDir "web-controller.png"
    & $edgePath --headless --disable-gpu --screenshot="$ctrlOut" --window-size=1280,720 "http://localhost:${webPort}/controller.html?layout=xbox360" 2>$null
    Start-Sleep -Seconds 3
    if (Test-Path $ctrlOut) { Write-Host "  >> web-controller.png" -ForegroundColor Green }
} catch {
    Write-Host "  !! Web screenshots failed: $($_.Exception.Message)" -ForegroundColor Yellow
    $n++
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

Stop-Transcript | Out-Null

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Cyan
Write-Host "Screenshots in: $OutputDir"
Write-Host ""
Get-ChildItem "$OutputDir\*.png" | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0} ({1}KB)" -f $_.Name, [math]::Round($_.Length / 1024))
}
