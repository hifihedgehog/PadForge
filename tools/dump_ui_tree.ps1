## Dump UI Automation tree for PadForge window (run elevated)
$logFile = Join-Path $PSScriptRoot "ui_tree.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "PadForge not running"; Stop-Transcript; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Dump-Tree($el, $depth) {
    if ($depth -gt 4) { return }
    $indent = "  " * $depth
    $n = $el.Current.Name
    $t = $el.Current.ControlType.ProgrammaticName
    $aid = $el.Current.AutomationId
    $cls = $el.Current.ClassName

    $patterns = @()
    try { $el.GetSupportedPatterns() | ForEach-Object { $patterns += $_.ProgrammaticName } } catch {}
    $pstr = if ($patterns.Count -gt 0) { " patterns=[$($patterns -join ',')]" } else { "" }

    Write-Host "${indent}[$t] name='$n' aid='$aid' class='$cls'$pstr"

    $children = $el.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $children) {
        Dump-Tree $child ($depth + 1)
    }
}

Write-Host "UI Tree for PadForge:"
Dump-Tree $root 0

Stop-Transcript | Out-Null
