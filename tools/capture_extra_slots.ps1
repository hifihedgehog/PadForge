<#
.SYNOPSIS
    Captures the slot-type-specific config bars (PlayStation, Extended,
    KBM, MIDI) from the running PadForge. Assumes 5 slots already exist
    after running prep_xml_for_capture.ps1.
#>

$logFile = "C:\PadForge\capture_extra_log.txt"
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
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    public static void ForceFG(IntPtr h) {
        IntPtr fg = GetForegroundWindow();
        uint pidTmp;
        uint fgTid = GetWindowThreadProcessId(fg, out pidTmp);
        uint targetTid = GetWindowThreadProcessId(h, out pidTmp);
        uint myTid = GetCurrentThreadId();
        AttachThreadInput(myTid, fgTid, true);
        AttachThreadInput(myTid, targetTid, true);
        ShowWindow(h, 9);
        BringWindowToTop(h);
        keybd_event(0x12, 0, 0, 0);
        keybd_event(0x12, 0, 0x02, 0);
        SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "  !! PadForge not running" -ForegroundColor Red; Stop-Transcript | Out-Null; exit 1 }
$hwnd = $proc.MainWindowHandle
[W32]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE
Start-Sleep -Milliseconds 600
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 1

$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants

function FindByAid {
    param([string]$Aid)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $uiaWin.FindFirst($TD, $cond)
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
    param($El, [string]$Lbl, [int]$Delay = 800)
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl" -ForegroundColor Red; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
    } catch {
        try {
            $sp = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select()
        } catch {
            $r = $El.Current.BoundingRectangle
            [W32]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        }
    }
    Write-Host "  Click '$Lbl'"
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
    if ($w -le 0 -or $h -le 0) { return }
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
        Write-Host "  !! Cap failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}
function Nav {
    param([string]$Name)
    $el = FindByName -Name $Name -CT ([System.Windows.Automation.ControlType]::ListItem)
    if (-not $el) { $el = FindByName -Name $Name }
    if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" -Delay 800 }
    Write-Host "  !! Nav '$Name' not found" -ForegroundColor Red
    return $false
}
function Try-ClickSlot {
    param([int]$Index)
    $script:slotOk = $false
    Nav "Dashboard" | Out-Null
    Start-Sleep -Milliseconds 1500
    $cards = $null
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $slotsHost = FindByAid "SlotsItemsControl"
        if ($slotsHost) {
            $cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
            if ($cards -and $cards.Count -gt $Index) { break }
        }
        Start-Sleep -Milliseconds 800
    }
    if (-not $cards -or $cards.Count -le $Index) {
        Write-Host "  !! Slot card $Index not found (cards=$(if ($cards) { $cards.Count } else { 0 }))" -ForegroundColor Red
        return
    }
    [void](ClickEl $cards[$Index] -Lbl "Slot $Index card" -Delay 2500)
    $padPage = FindByAid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) {
            $cr = $tabs[0].Current.BoundingRectangle
            [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
            [W32]::ClickAt([int]($cr.X + $cr.Width / 2), [int]($cr.Y + $cr.Height / 2))
            Start-Sleep -Milliseconds 1500
        }
    }
    Start-Sleep -Milliseconds 1000
    $script:slotOk = $true
}

Write-Host ""; Write-Host "=== Slot 1: PlayStation ==="
Try-ClickSlot 1
if ($script:slotOk) { Cap "pad-playstation-configbar" }

Write-Host ""; Write-Host "=== Slot 2: Extended ==="
Try-ClickSlot 2
if ($script:slotOk) {
    Cap "pad-extended-configbar"
    $padPage = FindByAid "PadPageView"
    if ($padPage) {
        $rect = $padPage.Current.BoundingRectangle
        $tx = [int]($rect.X + 52); $ty = [int]($rect.Y + 124)
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 800
        Cap "pad-extended-schematic"
        [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 500
    }
}

Write-Host ""; Write-Host "=== Slot 3: KBM ==="
Try-ClickSlot 3
if ($script:slotOk) { Cap "pad-kbm-preview" }

Write-Host ""; Write-Host "=== Slot 4: MIDI ==="
Try-ClickSlot 4
if ($script:slotOk) { Cap "pad-midi-configbar" }

Write-Host ""; Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
