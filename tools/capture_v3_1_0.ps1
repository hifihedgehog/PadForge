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
        System.Threading.Thread.Sleep(80);
        mouse_event(0x02, 0, 0, 0, 0); // LBUTTONDOWN
        System.Threading.Thread.Sleep(80);
        mouse_event(0x04, 0, 0, 0, 0); // LBUTTONUP
    }
    public static void DropdownClick(int x, int y) {
        // WPF ComboBox: open the dropdown by clicking the toggle arrow
        // explicitly. Plain ClickAt on the text area focuses the combo
        // but doesn't always pop the list. The ToggleButton inside the
        // combo template lives at the right edge.
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(120);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
    public static readonly IntPtr HWND_TOP = (IntPtr)0;
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static void ForceFG(IntPtr h) {
        // Aggressive raise: ShowWindow maximize + SetWindowPos topmost
        // (then back to non-topmost so we don't keep it pinned). Plus
        // SwitchToThisWindow which is the strongest raise API. Skip the
        // ALT-keypress that closes WPF combo dropdowns.
        ShowWindow(h, 3); // SW_MAXIMIZE
        SwitchToThisWindow(h, true);
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        IntPtr fg = GetForegroundWindow();
        if (fg == h) return;
        uint pidTmp;
        uint fgTid = GetWindowThreadProcessId(fg, out pidTmp);
        uint targetTid = GetWindowThreadProcessId(h, out pidTmp);
        uint myTid = GetCurrentThreadId();
        AttachThreadInput(myTid, fgTid, true);
        AttachThreadInput(myTid, targetTid, true);
        BringWindowToTop(h);
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
    param([string]$Name, [bool]$KeepCursor = $false)
    [W32]::ForceFG($hwnd)
    # Park cursor far from any title-bar button (Win11 snap-assist
    # appears if the cursor lingers on Maximize). Skip when capturing
    # an open dropdown — moving the cursor away can dismiss it.
    if (-not $KeepCursor) {
        [W32]::SetCursorPos(200, 1000) | Out-Null
    }
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
        # Scroll back to top — previous capture runs may have left the
        # Sticks ScrollViewer scrolled down to reach the sensitivity combo.
        $pp = $padPage.Current.BoundingRectangle
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        Start-Sleep -Milliseconds 100
        for ($w = 0; $w -lt 12; $w++) {
            [W32]::mouse_event(0x0800, 0, 0, 120, 0)
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 500
        Cap "pad-sticks"

        # Deadzone Shape combo (Width=200 DIPs = 300 px). Center near
        # combo body. Just click — F4 toggles (closes if click already opened).
        $pp = $padPage.Current.BoundingRectangle
        $cx = [int]($pp.X + 515); $cy = [int]($pp.Y + 550)
        [W32]::ClickAt($cx, $cy)
        Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-deadzone-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400

        # Sensitivity X combo (Width=120 DIPs = 180 px, narrower than Deadzone).
        # X must be left of where Deadzone clicks (which would land on this
        # combo's reset button). Combo center ~ PadPage.X + 455.
        $sx = [int]($pp.X + 455); $sy = [int]($pp.Y + 900)
        [W32]::ClickAt($sx, $sy)
        Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400
    }
    if (Tab "Triggers") {
        Start-Sleep -Milliseconds 600
        # Scroll Triggers ScrollViewer to top.
        $pp = $padPage.Current.BoundingRectangle
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        Start-Sleep -Milliseconds 100
        for ($w = 0; $w -lt 12; $w++) {
            [W32]::mouse_event(0x0800, 0, 0, 120, 0)
            Start-Sleep -Milliseconds 40
        }
        Start-Sleep -Milliseconds 500
        Cap "pad-triggers"

        # Trigger Preset combo (Width=120 DIPs). Use sensitivity-style offset
        # since it's also the narrower combo style.
        $pp = $padPage.Current.BoundingRectangle
        $tx = [int]($pp.X + 455); $ty = [int]($pp.Y + 550)
        [W32]::ClickAt($tx, $ty)
        Start-Sleep -Milliseconds 1000
        Cap "pad-triggers-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400
    }
    if (Tab "Force Feedback") { Start-Sleep -Milliseconds 600; Cap "pad-forcefeedback" }
    if (Tab "Adaptive Triggers") { Start-Sleep -Milliseconds 800; Cap "pad-adaptive-triggers" }
    if (Tab "Lighting") { Start-Sleep -Milliseconds 800; Cap "pad-lighting" }
}

Write-Host ""; Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
