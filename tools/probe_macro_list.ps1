<#
.SYNOPSIS
    Dumps what the Macros tab actually exposes to UI Automation.
.DESCRIPTION
    Written because the capture harness's macro-presence gate reported an empty
    list while the five injected macros demonstrably survived an app load and
    save. One of those two readings is wrong, and guessing which is how blank
    screenshots shipped in the first place. This prints the control type, class
    and name of everything on the Macros tab so the gate can match on what is
    really there.

    Run it with PadForge already open on a slot whose Macros tab has content.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$TD = [System.Windows.Automation.TreeScope]::Descendants

$proc = Get-Process PadForge -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "PadForge is not running"; exit 1 }
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no main window"; exit 1 }

$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Host "window: $($win.Current.Name)" -ForegroundColor Cyan

$names = @("Quick Combo", "Volume Control", "Sleep Controller", "Center Cursor", "Rapid Fire")

foreach ($ctName in @("ListItem", "Text", "ListBox", "List", "DataItem", "Group")) {
    $ct = [System.Windows.Automation.ControlType]::$ctName
    if (-not $ct) { continue }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    $all = @($win.FindAll($TD, $cond))
    $hits = @()
    foreach ($e in $all) {
        try { if ($names -contains $e.Current.Name) { $hits += $e } } catch { }
    }
    Write-Host ("{0,-10} total={1,-5} macro-name matches={2}" -f $ctName, $all.Count, $hits.Count)
    foreach ($h in $hits) {
        Write-Host ("     '{0}'  class='{1}'" -f $h.Current.Name, $h.Current.ClassName) -ForegroundColor Green
    }
}

# Anything at all carrying one of the names, whatever its control type.
Write-Host ""
Write-Host "--- ANY element whose Name matches a macro ---" -ForegroundColor Cyan
$anyCond = [System.Windows.Automation.Condition]::TrueCondition
$found = 0
foreach ($e in @($win.FindAll($TD, $anyCond))) {
    try {
        if ($names -contains $e.Current.Name) {
            $found++
            Write-Host ("   '{0}'  type={1}  class='{2}'" -f $e.Current.Name, $e.Current.ControlType.ProgrammaticName, $e.Current.ClassName)
        }
    } catch { }
}
Write-Host "total matches: $found"
