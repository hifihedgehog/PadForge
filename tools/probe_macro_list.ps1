<#
.SYNOPSIS
    Navigates to a slot's Macros tab and dumps what UI Automation exposes there.
.DESCRIPTION
    Written because the capture harness finds a macro by searching UIA Text
    elements for its name, and that search comes back empty while the macros
    demonstrably load: the live settings file holds all five AND the app has
    re-serialized them, which only happens from the pad ViewModels. So the data
    is fine and the LOOKUP is wrong. This prints control type, class name and
    name for everything on the tab so the lookup can be written against what is
    really there instead of what was assumed.

    PadForge must already be running with macros in its settings.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PW {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@
[PW]::SetProcessDPIAware() | Out-Null

$TD = [System.Windows.Automation.TreeScope]::Descendants
$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { Write-Host "PadForge is not running"; exit 1 }
$hwnd = $proc.MainWindowHandle
$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
[PW]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 800

function Get-Rect($el) {
    if (-not $el) { return $null }
    try { $r = $el.Current.BoundingRectangle } catch { return $null }
    if ($null -eq $r -or $r -isnot [System.Windows.Rect]) { return $null }
    try { if ($r.IsEmpty) { return $null } } catch { return $null }
    if ($r.Width -le 0 -or $r.Height -le 0) { return $null }
    return $r
}

function Click-Named([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    foreach ($el in $win.FindAll($TD, $cond)) {
        $r = Get-Rect $el
        if ($null -eq $r) { continue }
        [PW]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 120
        [PW]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 900
        Write-Host "  clicked '$name'"
        return $true
    }
    Write-Host "  !! '$name' not found"
    return $false
}

# Dashboard, first slot card, Macros tab.
Click-Named "Dashboard" | Out-Null
Start-Sleep -Milliseconds 900
$slots = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")))
if ($slots) {
    $card = $slots.FindFirst([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $r = Get-Rect $card
    if ($null -ne $r) {
        [PW]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 1500
        Write-Host "  clicked slot 1 card"
    }
}
Click-Named "Macros" | Out-Null
Start-Sleep -Milliseconds 1500

$names = @("Quick Combo", "Volume Control", "Sleep Controller", "Center Cursor", "Rapid Fire")

Write-Host ""
Write-Host "=== elements whose Name matches a macro (ANY control type) ===" -ForegroundColor Cyan
$found = 0
foreach ($el in $win.FindAll($TD, [System.Windows.Automation.Condition]::TrueCondition)) {
    try {
        if ($names -contains $el.Current.Name) {
            $found++
            $r = Get-Rect $el
            $pos = if ($r) { "at ($([int]$r.X),$([int]$r.Y))" } else { "no rect" }
            Write-Host ("   '{0}'  type={1}  class='{2}'  {3}" -f $el.Current.Name,
                $el.Current.ControlType.ProgrammaticName, $el.Current.ClassName, $pos)
        }
    } catch { }
}
Write-Host "matches: $found"

Write-Host ""
Write-Host "=== every List / ListItem / DataItem on the tab ===" -ForegroundColor Cyan
foreach ($ctName in @("List", "ListItem", "DataItem", "Text", "Group")) {
    $ct = [System.Windows.Automation.ControlType]::$ctName
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    $all = @($win.FindAll($TD, $cond))
    Write-Host ("{0,-10} count={1}" -f $ctName, $all.Count)
    if ($ctName -in @("List", "ListItem", "DataItem")) {
        foreach ($e in $all) {
            try {
                $r = Get-Rect $e
                Write-Host ("     name='{0}' class='{1}' {2}" -f $e.Current.Name, $e.Current.ClassName,
                    $(if ($r) { "at ($([int]$r.X),$([int]$r.Y)) $([int]$r.Width)x$([int]$r.Height)" } else { "no rect" }))
            } catch { }
        }
    }
}
