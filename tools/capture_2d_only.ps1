# Capture pad-controller-2d.png by flipping Use2DControllerView=true in
# PadForge.xml and restarting. Then revert to false and restart again so
# subsequent captures (and the user's normal use) are back on 3D default.
$logFile = "C:\PadForge\capture_2d_only_log.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32B {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, int extra);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int extra);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(60);
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
        ShowWindow(h, 3);  // SW_MAXIMIZE
        BringWindowToTop(h);
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 0x02, 0);
        SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$XmlPath = "C:\PadForge\PadForge.xml"

# Back up the real settings before this test rewrites slot types and
# created flags. Nothing here restored them, so a run left the user's
# slot layout replaced by test values permanently.
$xmlBak = "$XmlPath.cap2d-bak"
if (Test-Path -LiteralPath $xmlBak) { Copy-Item -LiteralPath $xmlBak -Destination $XmlPath -Force }
elseif (Test-Path -LiteralPath $XmlPath) { Copy-Item -LiteralPath $XmlPath -Destination $xmlBak -Force }

$ExePath = "C:\PadForge\PadForge.exe"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"

function Set-Use2D {
    param([bool]$Value)
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    [xml]$xml = Get-Content $XmlPath
    $app = $xml.PadForgeSettings.AppSettings
    $existing = $app.SelectSingleNode("Use2DControllerView")
    $strVal = if ($Value) { "true" } else { "false" }
    if ($existing) {
        $existing.InnerText = $strVal
    } else {
        $node = $xml.CreateElement("Use2DControllerView")
        $node.InnerText = $strVal
        $app.AppendChild($node) | Out-Null
    }
    $xml.Save($XmlPath)
    Write-Host "Set Use2DControllerView=$strVal"
    Start-Process $ExePath
    Start-Sleep -Seconds 15
}

function Capture-2D {
    $proc = Get-Process PadForge | Select-Object -First 1
    $hwnd = $proc.MainWindowHandle
    [W32B]::ForceFG($hwnd)
    Start-Sleep -Seconds 1

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
    $TD = [System.Windows.Automation.TreeScope]::Descendants
    $TC = [System.Windows.Automation.TreeScope]::Children

    # Nav Dashboard via name-search + coord click
    $nav = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "Dashboard")))
    if ($nav) {
        $r = $nav.Current.BoundingRectangle
        # Rect.Empty reports X as +Infinity and [int] on it throws, so guard
        # before converting.
        if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
        [W32B]::ClickAt([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
        Start-Sleep -Milliseconds 1500
    }
    # Click slot 0
    $slots = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")))
    if ($slots) {
        $cards = $slots.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
        if ($cards.Count -gt 0) {
            $cr = $cards[0].Current.BoundingRectangle
            [W32B]::ClickAt([int]($cr.X + $cr.Width/2), [int]($cr.Y + $cr.Height/2))
            Start-Sleep -Milliseconds 2500
        }
    }
    # Click Controller tab (first radio in PadPageView) so we land on the 2D view
    $padPage = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "PadPageView")))
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) {
            $tr = $tabs[0].Current.BoundingRectangle
            [W32B]::ClickAt([int]($tr.X + $tr.Width/2), [int]($tr.Y + $tr.Height/2))
            Start-Sleep -Milliseconds 1500
        }
    }
    [W32B]::ForceFG($hwnd); Start-Sleep -Milliseconds 600
    # Screenshot
    $r = New-Object W32B+RECT
    [W32B]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $p = Join-Path $OutputDir "pad-controller-2d.png"
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $kb = [math]::Round((Get-Item $p).Length / 1024)
    Write-Host "  >> pad-controller-2d.png (${kb}KB)" -ForegroundColor Green
}

Write-Host "=== Step 1: Flip Use2DControllerView=true ==="
Set-Use2D -Value $true

Write-Host "=== Step 2: Capture 2D ==="
Capture-2D

Write-Host "=== Step 3: Flip back to false ==="
Set-Use2D -Value $false

Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
