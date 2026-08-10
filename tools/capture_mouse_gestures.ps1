<#
.SYNOPSIS
    Captures pad-mouse-gestures on its own, with the full slot topology.
.DESCRIPTION
    The full run takes about twenty five minutes, and re-running it to recover a
    single shot is not a reasonable way to fix one picture. This does the same
    thing for one shot in about two minutes.

    It builds the same seven-slot topology the rest of the gallery shows, so the
    sidebar matches every other screenshot, maps "All Mice (Merged)" onto the
    Keyboard and Mouse slot by writing UserSetting.MapTo (the pad page's device
    dropdown never picked it up from a UI toggle), opens the Mouse tab and
    captures it.

    Pad index is resolved from SlotControllerTypes, not assumed. Dashboard card
    order is type-group order while pad index is creation order, and mixing them
    is what put the mouse on an Extended slot.

    The owner's settings are backed up first and restored in a finally.
#>

param(
    [string]$OutputDir   = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$mgLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $mgLogDir)) { New-Item -ItemType Directory -Path $mgLogDir | Out-Null }
Start-Transcript -Path (Join-Path $mgLogDir "mousegestures.txt") -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MG {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[MG]::SetProcessDPIAware() | Out-Null

$TD = [System.Windows.Automation.TreeScope]::Descendants
$bak = Join-Path $env:TEMP "PadForge.xml.mousegestures.bak"

function Get-Rect($el) {
    if (-not $el) { return $null }
    try { $r = $el.Current.BoundingRectangle } catch { return $null }
    if ($null -eq $r -or $r -isnot [System.Windows.Rect]) { return $null }
    try { if ($r.IsEmpty) { return $null } } catch { return $null }
    if ($r.Width -le 0 -or $r.Height -le 0) { return $null }
    return $r
}

# A leftover backup means an earlier run died holding the real settings.
if (Test-Path $bak) { Copy-Item $bak $PadForgeXml -Force; Write-Host "restored a leftover backup" }
Copy-Item $PadForgeXml $bak -Force

try {
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    # Seven slots, matching the rest of the gallery.
    # 0 Xbox, 1 PlayStation, 5 Nintendo, 4 KeyboardMouse, 2 Extended, 3 MIDI, 6 VR
    $wanted = @(0, 1, 5, 4, 2, 3, 6)
    [xml]$x = Get-Content $PadForgeXml
    $root = $x.PadForgeSettings

    # The slot arrays live under AppSettings, NOT at the root. Project memory
    # says so plainly ("slot arrays and app flags under PadForgeSettings >
    # AppSettings") and I still looked for them at the root first.
    $app = $root.SelectSingleNode("AppSettings")
    if (-not $app) { throw "AppSettings node missing" }
    $created = $app.SelectSingleNode("SlotCreated")
    $types   = $app.SelectSingleNode("SlotControllerTypes")
    $enabled = $app.SelectSingleNode("SlotEnabled")
    if (-not $created) { throw "SlotCreated node missing" }
    if (-not $types)   { throw "SlotControllerTypes node missing" }
    $nCreated = @($created.ChildNodes).Count
    $nTypes   = @($types.ChildNodes).Count
    $nEnabled = if ($enabled) { @($enabled.ChildNodes).Count } else { 0 }
    Write-Host "slot arrays: created=$nCreated types=$nTypes enabled=$nEnabled"
    for ($i = 0; $i -lt $nCreated; $i++) {
        $on = ($i -lt $wanted.Count)
        $created.ChildNodes[$i].InnerText = if ($on) { "true" } else { "false" }
        if ($i -lt $nEnabled) { $enabled.ChildNodes[$i].InnerText = if ($on) { "true" } else { "false" } }
        if ($on -and $i -lt $nTypes) { $types.ChildNodes[$i].InnerText = "$($wanted[$i])" }
    }
    $kbmPad = [Array]::IndexOf($wanted, 4)
    Write-Host "KBM slot is pad index $kbmPad"

    # Map the merged mouse onto that pad. It is already in the device cache.
    $dev = $null
    foreach ($d in $root.SelectSingleNode("Devices").ChildNodes) {
        $n = $d.SelectSingleNode("InstanceName")
        if ($n -and $n.InnerText -eq "All Mice (Merged)") { $dev = $d; break }
    }
    if (-not $dev) { throw "All Mice (Merged) is not in the device cache" }
    $guid  = $dev.SelectSingleNode("InstanceGuid").InnerText
    $pguid = $dev.SelectSingleNode("ProductGuid").InnerText

    $us = $root.SelectSingleNode("UserSettings")
    $template = $us.FirstChild
    $row = $template.CloneNode($true)
    foreach ($pair in @(@("InstanceGuid",$guid), @("ProductGuid",$pguid),
                        @("InstanceName","All Mice (Merged)"), @("ProductName","All Mice (Merged)"),
                        @("MapTo","$kbmPad"))) {
        $n = $row.SelectSingleNode($pair[0]); if ($n) { $n.InnerText = $pair[1] }
    }
    $us.AppendChild($row) | Out-Null

    $ft = $root.SelectSingleNode("AppSettings/FirstRunTourCompleted")
    if ($ft) { $ft.InnerText = "true" }
    $x.Save($PadForgeXml)
    Write-Host "wrote 7 slots and mapped the mouse to pad $kbmPad"

    Start-Process $PadForgeExe
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $p = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "PadForge did not start" }
    Start-Sleep -Seconds 10
    [MG]::ShowWindow($hwnd, 3) | Out-Null      # maximize
    [MG]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds 2
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    function Click-Named([string]$name, [int]$delay = 900) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        foreach ($el in $win.FindAll($TD, $cond)) {
            $r = Get-Rect $el
            if ($null -eq $r) { continue }
            [MG]::SetForegroundWindow($hwnd) | Out-Null
            Start-Sleep -Milliseconds 120
            [MG]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            Start-Sleep -Milliseconds $delay
            Write-Host "  clicked '$name'"
            return $true
        }
        Write-Host "  !! '$name' not found" -ForegroundColor Yellow
        return $false
    }

    # The KBM slot card sits fifth in the sidebar (type-group order).
    $slots = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")))
    if (-not $slots) { throw "SlotsItemsControl not found" }
    $cards = @($slots.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
    Write-Host "  slot cards: $($cards.Count)"
    $kbmCard = 4   # Xbox, PlayStation, Nintendo, Extended, KBM in card order
    $r = Get-Rect $cards[$kbmCard]
    if ($null -eq $r) { throw "KBM card has no rect" }
    [MG]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Seconds 2

    Click-Named "Mouse" 1200 | Out-Null
    Start-Sleep -Seconds 1

    $wr = New-Object MG+RECT
    [MG]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $out = Join-Path $OutputDir "pad-mouse-gestures.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ">> pad-mouse-gestures.png ($([math]::Round((Get-Item $out).Length/1024))KB)" -ForegroundColor Green
}
catch {
    Write-Host "!! FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   at $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" -ForegroundColor Red
}
finally {
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Copy-Item $bak $PadForgeXml -Force
    Remove-Item $bak -Force -EA SilentlyContinue
    Start-Process $PadForgeExe
    Write-Host "owner settings restored" -ForegroundColor Green
}
Stop-Transcript | Out-Null
