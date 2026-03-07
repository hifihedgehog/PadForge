## Quick UI element dump for PadForge (self-elevating, outputs to file)
param([switch]$Elevated)

$outFile = Join-Path $PSScriptRoot "vjoy_ui_debug_log.txt"

if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $outFile) { Get-Content $outFile }
        exit
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$lines = @()
function Out($msg) { $script:lines += $msg; Write-Host $msg }

$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) {
    Out "PadForge not running or no window"
    Set-Content $outFile ($lines -join "`n")
    exit 1
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
Out "Window: $($root.Current.Name)"
Out ""

$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
Out "Total elements: $($all.Count)"
Out ""

Out "=== Navigation / List Items ==="
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($ct -in @('ListItem','TabItem','MenuItem','TreeItem','Hyperlink','Custom')) {
        $name = $el.Current.Name
        $aid = $el.Current.AutomationId
        $help = $el.Current.HelpText
        $cls = $el.Current.ClassName
        Out "  [$ct] name='$name' aid='$aid' help='$help' cls='$cls'"
    }
}

Out ""
Out "=== Buttons ==="
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($ct -eq 'Button') {
        $name = $el.Current.Name
        $aid = $el.Current.AutomationId
        $help = $el.Current.HelpText
        Out "  name='$name' aid='$aid' help='$help'"
    }
}

Out ""
Out "=== TextBoxes / ComboBoxes ==="
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    if ($ct -in @('Edit','ComboBox')) {
        $name = $el.Current.Name
        $aid = $el.Current.AutomationId
        Out "  [$ct] name='$name' aid='$aid'"
    }
}

Out ""
Out "=== Elements with 'Pad' or 'Dashboard' or 'Controller' or 'vJoy' ==="
foreach ($el in $all) {
    $name = $el.Current.Name
    $aid = $el.Current.AutomationId
    if ($name -match 'Pad|Dashboard|Controller|Devices|vJoy|Add' -or $aid -match 'Pad|Dashboard|Controller|Devices|vJoy|Add') {
        $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $help = $el.Current.HelpText
        Out "  [$ct] name='$name' aid='$aid' help='$help'"
    }
}

Set-Content $outFile ($lines -join "`n")
