<#
.SYNOPSIS
    Captures the v3.1.0 user-facing pages without wiping the user's
    PadForge.xml. Assumes a slot exists with a DualSense assigned so
    Force Feedback / Adaptive Triggers / Lighting tabs are visible.
.NOTES
    Run elevated (PadForge runs elevated for vJoy/HM auto-elevation,
    so UIA needs the same).
#>

$logFile = "C:\PadForge\capture_v3_1_0_log.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, int extra);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x02, 0, 0, 0, 0); // LBUTTONDOWN
        System.Threading.Thread.Sleep(50);
        mouse_event(0x04, 0, 0, 0, 0); // LBUTTONUP
    }
    public static void ForceFG(IntPtr h) {
        // AttachThreadInput trick: lets us bypass SetForegroundWindow's
        // foreground-lock restriction. Also a no-op ALT keypress nudges
        // Windows into accepting the change.
        IntPtr fg = GetForegroundWindow();
        uint pidTmp;
        uint fgTid = GetWindowThreadProcessId(fg, out pidTmp);
        uint targetTid = GetWindowThreadProcessId(h, out pidTmp);
        uint myTid = GetCurrentThreadId();
        AttachThreadInput(myTid, fgTid, true);
        AttachThreadInput(myTid, targetTid, true);
        ShowWindow(h, 9); // SW_RESTORE
        BringWindowToTop(h);
        keybd_event(0x12, 0, 0, 0);            // ALT down
        keybd_event(0x12, 0, 0x02, 0);         // ALT up
        SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants

# Find PadForge window
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Host "  !! PadForge not running. Launch it first." -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}
$hwnd = $proc.MainWindowHandle
Write-Host "PadForge PID=$($proc.Id) HWND=$hwnd"
# Maximize so taskbar is excluded from capture region.
[W32]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE
Start-Sleep -Milliseconds 600
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 1

# Wire up UIA root + window
$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
if (-not $uiaWin) { Write-Host "  !! UIA window not found" -ForegroundColor Red; exit 1 }

function FindByAid {
    param([string]$Aid, [System.Windows.Automation.AutomationElement]$Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $where.FindFirst($TD, $cond)
}

function FindByName {
    param([string]$Name, [System.Windows.Automation.ControlType]$CT = $null,
          [System.Windows.Automation.AutomationElement]$Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $nameC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($CT) {
        $ctC = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
        $cond = New-Object System.Windows.Automation.AndCondition($nameC, $ctC)
    } else { $cond = $nameC }
    return $where.FindFirst($TD, $cond)
}

function ClickEl {
    param([System.Windows.Automation.AutomationElement]$El, [string]$Lbl, [int]$Delay = 800)
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl" -ForegroundColor Red; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
        Write-Host "  Click '$Lbl' (Invoke)"
    } catch {
        try {
            $sp = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select()
            Write-Host "  Click '$Lbl' (SelectionItem.Select)"
        } catch {
            $r = $El.Current.BoundingRectangle
            $x = [int]($r.X + $r.Width / 2)
            $y = [int]($r.Y + $r.Height / 2)
            [W32]::ClickAt($x, $y)
            Write-Host "  Click '$Lbl' (coord $x,$y)"
        }
    }
    Start-Sleep -Milliseconds $Delay
    return $true
}

function Cap {
    param([string]$Name)
    [W32]::ForceFG($hwnd)
    # Park cursor far from any title-bar button (Win11 snap-assist
    # appears if the cursor lingers on Maximize).
    [W32]::SetCursorPos(200, 1000) | Out-Null
    Start-Sleep -Milliseconds 600
    $r = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { Write-Host "  !! bad rect ${w}x${h}" -ForegroundColor Red; return }
    try {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
        $g.Dispose()
        $p = Join-Path $OutputDir "$Name.png"
        $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $kb = [math]::Round((Get-Item $p).Length / 1024)
        Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
    } catch {
        Write-Host "  !! Cap failed for $Name : $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Nav {
    param([string]$Name)
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TreeItem)) {
        $el = FindByName -Name $Name -CT $ct
        if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    }
    $el = FindByName -Name $Name
    if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    Write-Host "  !! Nav '$Name' not found" -ForegroundColor Red
    return $false
}

function Tab {
    param([string]$Name)
    # WPF tab radios fire content swap via Click handler, not via
    # SelectionItem.Select. Always coord-click so Click fires.
    $padPage = FindByAid "PadPageView"
    $where = if ($padPage) { $padPage } else { $uiaWin }
    $el = FindByName -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton) -Parent $where
    if (-not $el) {
        Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
        return $false
    }
    [W32]::ForceFG($hwnd)
    Start-Sleep -Milliseconds 200
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2)
    $y = [int]($r.Y + $r.Height / 2)
    [W32]::ClickAt($x, $y)
    Write-Host "  Tab '$Name' (coord $x,$y)"
    Start-Sleep -Milliseconds 800
    return $true
}

function SelectFirstSlot {
    Nav "Dashboard"; Start-Sleep -Milliseconds 1000
    $slotsHost = FindByAid "SlotsItemsControl"
    if (-not $slotsHost) { Write-Host "  !! SlotsItemsControl not found" -ForegroundColor Red; return $false }
    $cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($cards.Count -lt 1) { Write-Host "  !! No slot cards on Dashboard" -ForegroundColor Red; return $false }
    Write-Host "  Found $($cards.Count) slot card(s); selecting [0]"
    ClickEl $cards[0] -Lbl "First slot card" -Delay 2500 | Out-Null
    return $true
}

# ────────── Capture ──────────

Write-Host ""; Write-Host "=== Dashboard ==="
Nav "Dashboard"; Start-Sleep -Milliseconds 600; Cap "dashboard"

Write-Host ""; Write-Host "=== Profiles ==="
Nav "Profiles"; Cap "profiles"

Write-Host ""; Write-Host "=== Devices ==="
Nav "Devices"; Start-Sleep -Milliseconds 800; Cap "devices"

Write-Host ""; Write-Host "=== Settings ==="
Nav "Settings"; Cap "settings"

Write-Host ""; Write-Host "=== About ==="
Nav "About"; Cap "about"

Write-Host ""; Write-Host "=== Slot tabs (assumes slot 0 has DualSense assigned) ==="
if (SelectFirstSlot) {
    # Land on Controller (3D) tab first
    $padPage = FindByAid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) {
            # Coord-click so TabBtn_Click fires (UIA SelectionItem.Select skips it).
            $cr = $tabs[0].Current.BoundingRectangle
            [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
            [W32]::ClickAt([int]($cr.X + $cr.Width / 2), [int]($cr.Y + $cr.Height / 2))
            Start-Sleep -Milliseconds 1500
        }
        Write-Host "  Tabs visible to UIA: $($tabs.Count)"
        for ($ti = 0; $ti -lt $tabs.Count; $ti++) {
            Write-Host "    [$ti] '$($tabs[$ti].Current.Name)'"
        }
        Cap "pad-controller-3d"

        # Toggle to 2D. The toggle Button is hidden from UIA (Viewport3D
        # parent suppresses children). Anchor coords off HMaestroProfileCombo
        # — that IS in UIA and sits directly above the controller-view Grid.
        # Toggle is at the top-left of the controller view, ~22px below the combo.
        $combo = FindByAid "HMaestroProfileCombo"
        if ($combo) {
            $cr = $combo.Current.BoundingRectangle
            $tx = [int]($padPage.Current.BoundingRectangle.X + 30)
            $ty = [int]($cr.Y + $cr.Height + 22)
            [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
            [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 1000
            Cap "pad-controller-2d"
            [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
            [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 600
        } else {
            Write-Host "  !! HMaestroProfileCombo not found (cannot anchor toggle coords)" -ForegroundColor Yellow
        }
    }

    if (Tab "Macros") { Start-Sleep -Milliseconds 600; Cap "pad-macros" }
    if (Tab "Mappings") { Start-Sleep -Milliseconds 600; Cap "pad-mappings" }
    if (Tab "Sticks") {
        Start-Sleep -Milliseconds 600
        Cap "pad-sticks"

        # Deadzone Shape combo. UIA can't see the combo itself (TabControl
        # ContentPresenter strips children). Coords anchored to PadPage,
        # measured from a known-good capture of the Sticks tab layout.
        $pp = $padPage.Current.BoundingRectangle
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt([int]($pp.X + 419), [int]($pp.Y + 755))
        Start-Sleep -Milliseconds 900
        Cap "pad-sticks-deadzone-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400

        # Sensitivity X combo (further down — past deadzone/anti-deadzone
        # rows). Sticks tab needs to be scrolled to make this visible, so
        # we scroll the page first via mouse wheel.
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        Start-Sleep -Milliseconds 200
        # Mouse wheel down (negative delta in mouse_event)
        for ($w = 0; $w -lt 5; $w++) {
            [W32]::mouse_event(0x0800, 0, 0, [uint32]::MaxValue - 119, 0)
            Start-Sleep -Milliseconds 80
        }
        Start-Sleep -Milliseconds 600
        [W32]::ClickAt([int]($pp.X + 419), [int]($pp.Y + 755))
        Start-Sleep -Milliseconds 900
        Cap "pad-sticks-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400
        # Scroll back up
        for ($w = 0; $w -lt 6; $w++) {
            [W32]::mouse_event(0x0800, 0, 0, 120, 0)
            Start-Sleep -Milliseconds 80
        }
        Start-Sleep -Milliseconds 400
    }
    if (Tab "Triggers") {
        Start-Sleep -Milliseconds 600
        Cap "pad-triggers"

        # Trigger Preset combo (top of Triggers tab, similar offset to Sticks).
        $pp = $padPage.Current.BoundingRectangle
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt([int]($pp.X + 394), [int]($pp.Y + 460))
        Start-Sleep -Milliseconds 900
        Cap "pad-triggers-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400
    }
    if (Tab "Force Feedback") { Start-Sleep -Milliseconds 600; Cap "pad-forcefeedback" }
    if (Tab "Adaptive Triggers") { Start-Sleep -Milliseconds 800; Cap "pad-adaptive-triggers" }
    if (Tab "Lighting") { Start-Sleep -Milliseconds 800; Cap "pad-lighting" }
}

Write-Host ""; Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
