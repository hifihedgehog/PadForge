# Minimal vJoy type-switch test -- must run elevated
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$logPath = "C:\PadForge\vjoy_type_switch_test.log"
$diagPath = "C:\PadForge\vjoy_diag.log"
$deployPath = "C:\PadForge\PadForge.exe"
$pass = 0; $fail = 0

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss.fff"
    $line = "[$ts] $msg"
    Write-Host $line
    Add-Content $logPath $line
}

function Assert($cond, $msg) {
    if ($cond) { $script:pass++; Log "  PASS: $msg" }
    else { $script:fail++; Log "  FAIL: $msg" }
}

function Get-VJoyPdoCountAny {
    return (Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
}

function Get-PadForgeRoot {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "PadForge")
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-AllByType($parent, $type) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    return $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByName($parent, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByAutomationId($parent, $aid) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-Element($el) {
    try {
        $invPat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($invPat) { $invPat.Invoke(); return $true }
    } catch {}
    try {
        $rect = $el.Current.BoundingRectangle
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
        Start-Sleep -Milliseconds 100
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class MouseClick3 {
    [DllImport("user32.dll")]
    public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    public static void Click() { mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0); }
}
"@ -ErrorAction SilentlyContinue
        [MouseClick3]::Click()
        return $true
    } catch { return $false }
}

function Find-ButtonByTooltip($parent, $tooltip) {
    $buttons = Find-AllByType $parent ([System.Windows.Automation.ControlType]::Button)
    foreach ($btn in $buttons) {
        $help = $btn.Current.HelpText
        $name = $btn.Current.Name
        if ($help -eq $tooltip -or $name -eq $tooltip) { return $btn }
    }
    return $null
}

function Navigate-ToSidebarItem($root, $itemName) {
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TabItem,
                      [System.Windows.Automation.ControlType]::Button)) {
        $items = Find-AllByType $root $ct
        foreach ($item in $items) {
            $name = $item.Current.Name
            $aid = $item.Current.AutomationId
            if ($name -eq $itemName -or $aid -eq $itemName -or $name -like "*$itemName*") {
                Click-Element $item | Out-Null
                Start-Sleep -Milliseconds 500
                return $true
            }
        }
    }
    Log "  WARN: Sidebar item $itemName not found"
    return $false
}

function Click-PopupButton($tooltipText) {
    Start-Sleep -Milliseconds 500
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $btn = Find-ButtonByTooltip $desktop $tooltipText
    if ($btn) { Click-Element $btn | Out-Null; return $true }
    $btn = Find-ByName $desktop $tooltipText
    if ($btn) { Click-Element $btn | Out-Null; return $true }
    Log "  ERROR: Popup button $tooltipText not found"
    return $false
}

function Dump-Elements($parent, $depth = 0) {
    if ($depth -gt 2) { return }
    $indent = "  " * ($depth + 1)
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($parent)
    $count = 0
    while ($child -and $count -lt 80) {
        $ct = $child.Current.ControlType.ProgrammaticName
        $name = $child.Current.Name
        $aid = $child.Current.AutomationId
        $help = $child.Current.HelpText
        if ($name -or $aid -or $help) {
            Log "${indent}[$ct] Name=$name AID=$aid Help=$help"
        }
        Dump-Elements $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
        $count++
    }
}

# SETUP
if (Test-Path $logPath) { Remove-Item $logPath -Force }
if (Test-Path $diagPath) { Remove-Item $diagPath -Force }
Log "=== vJoy Type-Switch Test $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="

Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$configPath = "C:\PadForge\PadForge.xml"
$configBackup = "C:\PadForge\PadForge.xml.typeswitch.bak"
if (Test-Path $configPath) {
    Copy-Item $configPath $configBackup -Force
    Remove-Item $configPath -Force
    Log "Backed up and deleted PadForge.xml"
}

Log "Starting PadForge..."
Start-Process $deployPath
Start-Sleep -Seconds 10

$root = Get-PadForgeRoot
Assert ($null -ne $root) "PadForge window found"
if (-not $root) { Log "ABORT: No PadForge window"; exit 1 }

Log "UI tree dump:"
Dump-Elements $root

# CREATE VJOY
Log ""
Log "=== Step 1: Create vJoy controller ==="
$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) {
    Click-Element $addCard | Out-Null
    Log "  Clicked Add Controller"
    Start-Sleep -Milliseconds 500
} else {
    Log "  Add Controller not found in UI"
}

Click-PopupButton "vJoy" | Out-Null
Start-Sleep -Seconds 3

# Assign keyboard to slot 0
$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Devices"
Start-Sleep -Milliseconds 800
$root = Get-PadForgeRoot

$listItems = Find-AllByType $root ([System.Windows.Automation.ControlType]::ListItem)
foreach ($item in $listItems) {
    $name = $item.Current.Name
    if ($name -like "*Keyboard*") {
        Log "  Found keyboard: $name"
        try { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { Click-Element $item | Out-Null }
        Start-Sleep -Milliseconds 500
        break
    }
}

$buttons = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
foreach ($btn in $buttons) {
    $aid = $btn.Current.AutomationId
    if ($aid -like "*ToggleAssign*" -or $aid -like "*SlotAssign*") {
        Click-Element $btn | Out-Null
        Log "  Clicked assignment toggle"
        break
    }
}

Start-Sleep -Seconds 10
$pdoCount = Get-VJoyPdoCountAny
Log "  VJOYRAWPDO count: $pdoCount"
Assert ($pdoCount -ge 1) "vJoy PDO exists after creation"

# SWITCH TO XBOX 360
Log ""
Log "=== Step 2: Switch to Xbox 360 ==="
$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
$switched = $false
foreach ($btn in $allBtns) {
    $help = $btn.Current.HelpText
    $name = $btn.Current.Name
    if ($help -eq 'Xbox 360' -or $name -eq 'Xbox 360') {
        Click-Element $btn | Out-Null
        Log "  Clicked Xbox 360 type button"
        $switched = $true
        break
    }
}
if (-not $switched) {
    Log "  WARN: No Xbox 360 button, dumping buttons:"
    foreach ($btn in $allBtns) {
        $n = $btn.Current.Name; $h = $btn.Current.HelpText
        if ($n -or $h) { Log "    Name=$n Help=$h" }
    }
}

Log "  Waiting 15s for node removal..."
Start-Sleep -Seconds 15

$pdoCountAny = Get-VJoyPdoCountAny
Log "  VJOYRAWPDO (any): $pdoCountAny"
Assert ($pdoCountAny -eq 0) "vJoy PDO fully removed after Xbox 360 switch"

# SWITCH BACK TO VJOY
Log ""
Log "=== Step 3: Switch back to vJoy ==="
$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
foreach ($btn in $allBtns) {
    $help = $btn.Current.HelpText
    $name = $btn.Current.Name
    if ($help -eq 'vJoy' -or $name -eq 'vJoy') {
        Click-Element $btn | Out-Null
        Log "  Clicked vJoy type button"
        break
    }
}

Log "  Waiting 15s for node creation..."
Start-Sleep -Seconds 15

$pdoCountAny = Get-VJoyPdoCountAny
Log "  VJOYRAWPDO (any): $pdoCountAny"
Assert ($pdoCountAny -ge 1) "vJoy PDO back after switching to vJoy type"

# CLEANUP
Log ""
Log "=== Cleanup ==="
Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

if (Test-Path $configBackup) {
    Copy-Item $configBackup $configPath -Force
    Remove-Item $configBackup -Force
    Log "Restored PadForge.xml"
}

if (Test-Path $diagPath) {
    Log ""
    Log "=== vjoy_diag.log ==="
    Get-Content $diagPath | ForEach-Object { Log "  $_" }
}

Log ""
Log "========================================"
Log "RESULTS: $pass passed, $fail failed"
Log "========================================"
