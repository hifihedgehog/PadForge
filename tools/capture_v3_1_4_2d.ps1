<#
.SYNOPSIS
    Capture pad-controller-2d.png by setting <Use2DControllerView>true</> in
    PadForge.xml directly, restarting PadForge, navigating to slot 0, and
    snapping the window. The toggle button is unreliable to drive via UIA
    (HelixViewport3D blocks the UIA tree, and coord-based clicks have
    landed off-target), so flip the persisted setting instead.
.NOTES
    Run elevated.
#>

$logFile = "C:\PadForge\capture_v3_1_4_2d_log.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, int extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x02, 0, 0, 0, 0); System.Threading.Thread.Sleep(80);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    public static void ForceFG(IntPtr h) {
        ShowWindow(h, 3); SwitchToThisWindow(h, true);
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
        BringWindowToTop(h); SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$XmlPath = "C:\PadForge\PadForge.xml"
$ExePath = "C:\PadForge\PadForge.exe"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"

function Set2DView { param([bool]$on)
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
    Start-Sleep -Seconds 4
    [xml]$xml = Get-Content $XmlPath -Encoding UTF8
    $node = $xml.SelectSingleNode("//Use2DControllerView")
    if (-not $node) { Write-Host "  !! Use2DControllerView node missing"; return }
    $node.InnerText = if ($on) { "true" } else { "false" }
    $xml.Save($XmlPath)
    Write-Host "  Use2DControllerView -> $($node.InnerText)"
}

# Step 1: flip XML to 2D
Write-Host "=== STEP 1: Flip XML to 2D ==="
Set2DView -on $true

# Step 2: launch PadForge
Write-Host "=== STEP 2: Launch PadForge ==="
Start-Process $ExePath
Start-Sleep -Seconds 10
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "!! PadForge failed to start"; Stop-Transcript | Out-Null; exit 1 }
$hwnd = $proc.MainWindowHandle
for ($w = 0; $w -lt 15 -and $hwnd -eq 0; $w++) { Start-Sleep -Seconds 1; $proc.Refresh(); $hwnd = $proc.MainWindowHandle }
Write-Host "  PID=$($proc.Id) HWND=$hwnd"
[W32]::ShowWindow($hwnd, 3) | Out-Null
Start-Sleep -Seconds 3
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 3

# UIA helpers
$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants
$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)

function FindByAid { param([string]$Aid)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $uiaWin.FindFirst($TD, $cond)
}
function FindByName { param([string]$Name, $CT = $null)
    $nC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($CT) {
        $tC = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
        $cond = New-Object System.Windows.Automation.AndCondition($nC, $tC)
    } else { $cond = $nC }
    return $uiaWin.FindFirst($TD, $cond)
}
function Click { param($El, [string]$Lbl, [int]$Delay = 1500)
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl"; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke(); Write-Host "  Click '$Lbl' (Invoke)"
    } catch {
        $r = $El.Current.BoundingRectangle
        $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
        [W32]::ClickAt($x, $y); Write-Host "  Click '$Lbl' (coord $x,$y)"
    }
    Start-Sleep -Milliseconds $Delay; return $true
}

# Step 3: navigate Dashboard → slot 0 → Controller tab
Write-Host "=== STEP 3: Navigate to slot 0 Controller tab ==="
$dash = FindByName "Dashboard" -CT ([System.Windows.Automation.ControlType]::ListItem)
if (-not $dash) { $dash = FindByName "Dashboard" }
Click $dash -Lbl "Dashboard" -Delay 1200 | Out-Null

$slotsHost = FindByAid "SlotsItemsControl"
$cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
$card0 = $cards[0]
try {
    $sip = $card0.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
    $sip.ScrollIntoView()
    Start-Sleep -Milliseconds 400
} catch { }
$rc = $card0.Current.BoundingRectangle
Write-Host "  card[0] rect: X=$($rc.X) Y=$($rc.Y) W=$($rc.Width) H=$($rc.Height)"
Click $card0 -Lbl "slot 0 card" -Delay 3000 | Out-Null

$tab = FindByAid "TabController"
Click $tab -Lbl "TabController" -Delay 1500 | Out-Null

# Step 4: capture
Write-Host "=== STEP 4: Capture pad-controller-2d ==="
[W32]::ForceFG($hwnd)
[W32]::SetCursorPos(200, 1000) | Out-Null
Start-Sleep -Milliseconds 800
$r = New-Object W32+RECT
[W32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
$g.Dispose()
$p = Join-Path $OutputDir "pad-controller-2d.png"
$bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$kb = [math]::Round((Get-Item $p).Length / 1024)
Write-Host "  >> pad-controller-2d.png (${kb}KB)"

# Step 5: flip XML back to 3D, restart
Write-Host "=== STEP 5: Restore 3D view ==="
Set2DView -on $false
Start-Process $ExePath
Start-Sleep -Seconds 4

Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
