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
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x02, 0, 0, 0, 0); // LBUTTONDOWN
        System.Threading.Thread.Sleep(50);
        mouse_event(0x04, 0, 0, 0, 0); // LBUTTONUP
    }
    public static void ForceFG(IntPtr h) {
        ShowWindow(h, 5); // SW_SHOW
        SetForegroundWindow(h);
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
    Start-Sleep -Milliseconds 400
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
    $padPage = FindByAid "PadPageView"
    $where = if ($padPage) { $padPage } else { $uiaWin }
    $el = FindByName -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton) -Parent $where
    if ($el) { return ClickEl -El $el -Lbl "Tab:$Name" }
    Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
    return $false
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
        if ($tabs.Count -gt 0) { ClickEl $tabs[0] -Lbl "Controller Tab" -Delay 1500 | Out-Null }
        Write-Host "  Tabs visible to UIA: $($tabs.Count)"
        for ($ti = 0; $ti -lt $tabs.Count; $ti++) {
            Write-Host "    [$ti] '$($tabs[$ti].Current.Name)'"
        }
        Cap "pad-controller-3d"

        # Toggle the 3D/2D view button (top-left of PadPageView, ~52,124 DIPs)
        # to capture the 2D controller view.
        $rect = $padPage.Current.BoundingRectangle
        $toggleX = [int]($rect.X + 52)
        $toggleY = [int]($rect.Y + 124)
        [W32]::ForceFG($hwnd)
        Start-Sleep -Milliseconds 200
        [W32]::ClickAt($toggleX, $toggleY)
        Start-Sleep -Milliseconds 800
        Cap "pad-controller-2d"
        # Toggle back to 3D so the next capture (Macros) starts clean.
        [W32]::ClickAt($toggleX, $toggleY)
        Start-Sleep -Milliseconds 500
    }

    if (Tab "Macros") { Start-Sleep -Milliseconds 600; Cap "pad-macros" }
    if (Tab "Mappings") { Start-Sleep -Milliseconds 600; Cap "pad-mappings" }
    if (Tab "Sticks") {
        Start-Sleep -Milliseconds 600
        Cap "pad-sticks"

        # Open the deadzone-shape dropdown (ComboBox at fixed coords for the
        # sticks tab). The 2D-overlay coords used here come from the previous
        # April capture run; re-tested as still correct on the v3.1 layout.
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 469)
        Start-Sleep -Milliseconds 800
        Cap "pad-sticks-deadzone-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 300

        # Sensitivity preset dropdown — second combo lower in the same tab.
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 1046)
        Start-Sleep -Milliseconds 800
        Cap "pad-sticks-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 300
    }
    if (Tab "Triggers") {
        Start-Sleep -Milliseconds 600
        Cap "pad-triggers"

        # Triggers sensitivity preset dropdown.
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 472)
        Start-Sleep -Milliseconds 800
        Cap "pad-triggers-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 300
    }
    if (Tab "Force Feedback") { Start-Sleep -Milliseconds 600; Cap "pad-forcefeedback" }
    if (Tab "Adaptive Triggers") { Start-Sleep -Milliseconds 800; Cap "pad-adaptive-triggers" }
    if (Tab "Lighting") { Start-Sleep -Milliseconds 800; Cap "pad-lighting" }
}

Write-Host ""; Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
