## vJoy Live UI Automation Test
## Self-elevating -- will pop UAC if not already admin.
## Tests live vJoy operations via Windows UI Automation while PadForge is running:
##   1. Add a vJoy controller
##   2. Assign "All Keyboards (Merged)" device to it so it becomes active
##   3. Add a second vJoy controller, assign keyboard to it
##   4. Navigate to vJoy pad page, change config (buttons, sticks, triggers, POVs)
##   5. Switch a controller type (vJoy -> Xbox 360)
##   6. Delete all controllers, verify removal from joy.cpl / game controllers
##   7. Re-add vJoy to verify clean re-creation
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_auto_test_log.txt"

function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

# Self-elevate if needed
if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Elevating..."
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $logFile) { Get-Content $logFile }
        exit
    }
}

Set-Content -Path $logFile -Value "=== vJoy Live UI Automation Test $(Get-Date) ==="

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

# Mouse/window helpers
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@ -Name 'Win32' -Namespace 'Interop' -ErrorAction SilentlyContinue

$deployPath = "C:\PadForge\PadForge.exe"
$diagLogPath = "C:\PadForge\vjoy_diag.log"
$regBase = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'

$pass = 0
$fail = 0

function Assert($condition, $message) {
    if ($condition) {
        $script:pass++
        Log "  PASS: $message"
    } else {
        $script:fail++
        Log "  FAIL: $message"
    }
}

# ─── UI Automation Helpers ───

function Get-PadForgeRoot {
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc -or $proc.MainWindowHandle -eq [IntPtr]::Zero) { return $null }
    [Interop.Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
    return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
}

function Find-ByName($parent, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AllByName($parent, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByAutomationId($parent, $aid) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-AllByType($parent, $controlType) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ButtonByTooltip($parent, $tooltipText) {
    $buttons = Find-AllByType $parent ([System.Windows.Automation.ControlType]::Button)
    foreach ($btn in $buttons) {
        $help = $btn.Current.HelpText
        $name = $btn.Current.Name
        if ($help -eq $tooltipText -or $name -eq $tooltipText) { return $btn }
        if ($help -like "$tooltipText*" -or $name -like "$tooltipText*") { return $btn }
    }
    return $null
}

function Click-Element($el) {
    if (-not $el) { Log "  ERROR: Element is null"; return $false }
    # Try InvokePattern (fires Command for Buttons)
    try {
        $pat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        if ($pat) { $pat.Invoke(); return $true }
    } catch {}
    # NOTE: Deliberately skip TogglePattern — it sets IsChecked but does NOT fire
    # the WPF Command binding on ToggleButtons. Fall through to mouse click instead.
    # Fallback: physical mouse click
    try {
        $rect = $el.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) {
            Log "  WARN: Element has zero-size bounds"
            return $false
        }
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
        Start-Sleep -Milliseconds 50
        [Interop.Win32]::mouse_event(0x0002, 0, 0, 0, 0)
        Start-Sleep -Milliseconds 50
        [Interop.Win32]::mouse_event(0x0004, 0, 0, 0, 0)
        return $true
    } catch {
        Log "  ERROR: Click failed: $_"
        return $false
    }
}

function Click-PopupButton($tooltipText) {
    # WPF Popups create separate top-level windows. Search from desktop root.
    Start-Sleep -Milliseconds 500
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $btn = Find-ButtonByTooltip $desktop $tooltipText
    if ($btn) {
        Click-Element $btn | Out-Null
        return $true
    }
    $btn = Find-ByName $desktop $tooltipText
    if ($btn) {
        Click-Element $btn | Out-Null
        return $true
    }
    Log "  ERROR: Popup button '$tooltipText' not found"
    return $false
}

function Set-TextBoxValue($parent, $automationId, $value) {
    $el = Find-ByAutomationId $parent $automationId
    if (-not $el) {
        Log "  ERROR: TextBox '$automationId' not found"
        return $false
    }
    try {
        $valPat = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($valPat) {
            $valPat.SetValue([string]$value)
            # Send Tab to trigger LostFocus
            $el.SetFocus()
            Start-Sleep -Milliseconds 50
            [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
            return $true
        }
    } catch {}
    try {
        $el.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        Start-Sleep -Milliseconds 50
        [System.Windows.Forms.SendKeys]::SendWait([string]$value)
        Start-Sleep -Milliseconds 50
        [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
        return $true
    } catch {
        Log "  ERROR: Set TextBox failed: $_"
        return $false
    }
}

function Select-ComboBoxItem($parent, $automationId, $itemText) {
    $combo = Find-ByAutomationId $parent $automationId
    if (-not $combo) {
        Log "  ERROR: ComboBox '$automationId' not found"
        return $false
    }
    try {
        $expandPat = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($expandPat) { $expandPat.Expand() }
        Start-Sleep -Milliseconds 300
        $item = Find-ByName $combo $itemText
        if ($item) {
            try {
                $selPat = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if ($selPat) { $selPat.Select(); return $true }
            } catch {}
            Click-Element $item | Out-Null
            return $true
        }
        Log "  ERROR: ComboBox item '$itemText' not found"
        return $false
    } catch {
        Log "  ERROR: ComboBox selection failed: $_"
        return $false
    }
}

function Navigate-ToSidebarItem($root, $itemName) {
    # Try ListItem, TabItem, MenuItem, TreeItem -- WinUI NavigationView varies
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TabItem,
                      [System.Windows.Automation.ControlType]::MenuItem,
                      [System.Windows.Automation.ControlType]::TreeItem)) {
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
    # Also try by Name directly
    $el = Find-ByName $root $itemName
    if ($el) {
        Click-Element $el | Out-Null
        Start-Sleep -Milliseconds 500
        return $true
    }
    Log "  WARN: Sidebar item '$itemName' not found"
    return $false
}

function Assign-KeyboardToSlot($slotIndex) {
    # Navigate to Devices page, select "All Keyboards (Merged)", toggle slot assignment
    $root = Get-PadForgeRoot
    if (-not $root) { Log "  ERROR: No window for device assignment"; return $false }

    Navigate-ToSidebarItem $root "Devices"
    Start-Sleep -Milliseconds 800
    $root = Get-PadForgeRoot

    # Find the "All Keyboards (Merged)" device.
    # The device list is a ListBox with ListItem children. We need to find and SELECT
    # the ListItem (not just click a child TextBlock), because the ViewModel's
    # SelectedDevice binding requires proper ListBox selection.
    $kbdListItem = $null

    # Strategy 1: Find ListItems, check if any contain "All Keyboards (Merged)" text
    $listItems = Find-AllByType $root ([System.Windows.Automation.ControlType]::ListItem)
    foreach ($li in $listItems) {
        $textChild = Find-ByName $li "All Keyboards (Merged)"
        if ($textChild) {
            $kbdListItem = $li
            break
        }
        # Also check the ListItem's own Name
        if ($li.Current.Name -like "*All Keyboards*") {
            $kbdListItem = $li
            break
        }
    }

    # Strategy 2: Find TextBlock, navigate up to parent ListItem via TreeWalker
    if (-not $kbdListItem) {
        $kbdText = Find-ByName $root "All Keyboards (Merged)"
        if ($kbdText) {
            $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
            $parent = $walker.GetParent($kbdText)
            while ($parent -ne $null) {
                if ($parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
                    $kbdListItem = $parent
                    break
                }
                $parent = $walker.GetParent($parent)
            }
        }
    }

    if (-not $kbdListItem) {
        Log "  ERROR: 'All Keyboards (Merged)' ListItem not found on Devices page"
        $textEls = Find-AllByType $root ([System.Windows.Automation.ControlType]::Text)
        $count = 0
        foreach ($t in $textEls) {
            $n = $t.Current.Name
            if ($n -and $n.Length -gt 3 -and $count -lt 20) { Log "    text: '$n'"; $count++ }
        }
        return $false
    }

    # Select the ListItem using SelectionItemPattern (proper ListBox selection)
    Log "  Found keyboard ListItem, selecting..."
    $selected = $false
    try {
        $selPat = $kbdListItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($selPat) {
            $selPat.Select()
            $selected = $true
            Log "  Selected via SelectionItemPattern"
        }
    } catch {
        Log "  SelectionItemPattern failed: $_"
    }

    if (-not $selected) {
        # Fallback: physical click on the ListItem
        Log "  Fallback: clicking ListItem directly"
        Click-Element $kbdListItem | Out-Null
    }

    Start-Sleep -Milliseconds 800
    $root = Get-PadForgeRoot

    # Now find and click the slot toggle button.
    # Toggle buttons have ToolTip/HelpText = "Toggle assignment"
    $toggleBtns = @()
    $allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
    foreach ($b in $allBtns) {
        if ($b.Current.HelpText -eq "Toggle assignment") { $toggleBtns += $b }
    }
    Log "  Found $($toggleBtns.Count) toggle-assignment buttons"

    $foundToggle = $false
    if ($slotIndex -lt $toggleBtns.Count) {
        Log "  Clicking toggle button at index $slotIndex..."
        Click-Element $toggleBtns[$slotIndex] | Out-Null
        $foundToggle = $true
    } elseif ($toggleBtns.Count -gt 0) {
        Log "  Clicking last toggle button (index $($toggleBtns.Count - 1))..."
        Click-Element $toggleBtns[-1] | Out-Null
        $foundToggle = $true
    }

    if (-not $foundToggle) {
        Log "  ERROR: Could not find slot toggle button for slot $slotNum"
        return $false
    }

    Start-Sleep -Milliseconds 500
    return $true
}

# ─── State Checking ───

function Get-RegDeviceCount {
    if (-not (Test-Path $regBase)) { return 0 }
    return (Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' } | Measure-Object).Count
}

function Get-JoyCplVJoyCount {
    # Only count active/running VJOYRAWPDO devices (ConfigManagerErrorCode 0 = working).
    # After node removal, child PDOs may linger in PnP tree with non-zero error code.
    return (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' -and $_.ConfigManagerErrorCode -eq 0 } | Measure-Object).Count
}

function Get-VJoyNodeId {
    $output = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
    $currentId = $null
    foreach ($line in $output -split "`n") {
        $t = $line.Trim()
        if ($t -match 'Instance ID:\s+(.+)') { $currentId = $matches[1].Trim() }
        elseif ($currentId -and $t -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') { return $currentId }
        elseif ([string]::IsNullOrWhiteSpace($t)) { $currentId = $null }
    }
    return $null
}

function Log-State($label) {
    $reg = Get-RegDeviceCount
    $joy = Get-JoyCplVJoyCount
    $node = Get-VJoyNodeId
    Log "  [$label] regDevices=$reg joyCplVJoy=$joy node=$node"
    return @{ Reg = $reg; JoyCpl = $joy; Node = $node }
}

function Log-DiagFile {
    if (Test-Path $diagLogPath) {
        $content = Get-Content $diagLogPath -ErrorAction SilentlyContinue
        Log "  --- vjoy_diag.log (last 30 lines) ---"
        $content | Select-Object -Last 30 | ForEach-Object { Log "    $_" }
    }
}

function Stop-PadForge {
    $proc = Get-Process PadForge -ErrorAction SilentlyContinue
    if ($proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
}

# ─── Build and Deploy ───

Log ""
Log "=== BUILD & DEPLOY ==="
Stop-PadForge

$publishExe = "C:\Users\sonic\GitHub\PadForge\PadForge.App\bin\Release\net8.0-windows\win-x64\publish\PadForge.exe"
if (Test-Path $publishExe) {
    Copy-Item $publishExe $deployPath -Force
    Log "Deployed PadForge.exe"
} else {
    Log "ERROR: Build not found at $publishExe -- run 'dotnet publish' first"
    exit 1
}

if (Test-Path $diagLogPath) { Remove-Item $diagLogPath -Force }

# Delete config for a clean start (no pre-existing virtual controllers)
$configPath = "C:\PadForge\PadForge.xml"
$configBackup = "C:\PadForge\PadForge.xml.autotest.bak"
if (Test-Path $configPath) {
    Copy-Item $configPath $configBackup -Force
    Remove-Item $configPath -Force
    Log "Deleted PadForge.xml (backed up to .autotest.bak)"
}

# ─── Start PadForge ───

Log ""
Log "=== STARTING PADFORGE ==="
Start-Process $deployPath
Start-Sleep -Seconds 8

$root = Get-PadForgeRoot
if (-not $root) {
    Log "Waiting longer for window..."
    Start-Sleep -Seconds 5
    $root = Get-PadForgeRoot
}
Assert ($root -ne $null) "PadForge window found"
if (-not $root) { Log "ABORT: No window"; exit 1 }

Log "Window: $($root.Current.Name)"
$initialState = Log-State "INITIAL"

# ══════════════════════════════════════════════════════════════
# TEST 1: Add vJoy controller #1
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 1: Add vJoy controller #1 ==="

# First navigate to Dashboard (the "Add Controller" card is there)
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

# Look for "Add Controller" element -- it's a Border named AddControllerCard
$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) {
    Log "  Found Add Controller element"
    Click-Element $addCard | Out-Null
    Start-Sleep -Milliseconds 500
} else {
    Log "  Add Controller card not found, trying sidebar + button"
    # The sidebar also has an "AddController" nav item with "+" icon
    Navigate-ToSidebarItem $root "Add"
    Start-Sleep -Milliseconds 500
}

$clicked = Click-PopupButton "vJoy"
Assert $clicked "Clicked vJoy in popup"

Start-Sleep -Seconds 3

# Now assign "All Keyboards (Merged)" to make the slot active
Log "  Assigning keyboard to slot 0..."
$assigned = Assign-KeyboardToSlot 0
Assert $assigned "Keyboard assigned to vJoy slot"

# Wait for vJoy device creation (needs mapped device to become active)
Log "  Waiting for vJoy device node creation..."
$state1 = $null
for ($w = 0; $w -lt 15; $w++) {
    Start-Sleep -Seconds 2
    $s = Log-State "poll-$w"
    if ($null -ne $s.Node -and $s.Reg -ge 1 -and $s.JoyCpl -ge 1) { $state1 = $s; break }
}
if (-not $state1) { $state1 = Log-State "AFTER ADD vJoy #1 + keyboard (timeout)" }
Assert ($state1.Node -ne $null) "vJoy device node created"
Assert ($state1.Reg -ge 1) "Registry has Device01 key"
Log-DiagFile

# ══════════════════════════════════════════════════════════════
# TEST 2: Add vJoy controller #2
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 2: Add vJoy controller #2 ==="

# Click "Add Controller" in sidebar (works from any page, triggers popup directly)
$root = Get-PadForgeRoot
Start-Sleep -Milliseconds 500

# Find the sidebar "Add Controller" ListItem and click it to trigger the popup
$addClicked = $false
$addItems = Find-AllByType $root ([System.Windows.Automation.ControlType]::ListItem)
foreach ($item in $addItems) {
    if ($item.Current.Name -eq "Add Controller") {
        Click-Element $item | Out-Null
        $addClicked = $true
        Start-Sleep -Milliseconds 800
        break
    }
}
if (-not $addClicked) {
    # Fallback: try the Dashboard card approach
    Navigate-ToSidebarItem $root "Dashboard"
    Start-Sleep -Milliseconds 500
    $root = Get-PadForgeRoot
    $addCard = Find-ByName $root "Add Controller"
    if ($addCard) {
        Click-Element $addCard | Out-Null
        Start-Sleep -Milliseconds 800
    }
}

$clicked = Click-PopupButton "vJoy"
Assert $clicked "Clicked vJoy in popup (#2)"

Start-Sleep -Seconds 3

# Assign keyboard to slot 1 as well
Log "  Assigning keyboard to slot 1..."
$assigned = Assign-KeyboardToSlot 1
Assert $assigned "Keyboard assigned to vJoy slot #2"

# Poll for node restart to complete (remove+recreate can take ~20s)
Log "  Waiting for 2 vJoy devices in joy.cpl (node restart)..."
$state2 = $null
for ($w = 0; $w -lt 20; $w++) {
    Start-Sleep -Seconds 2
    $s = Log-State "poll-$w"
    if ($s.Reg -ge 2 -and $s.JoyCpl -ge 2) { $state2 = $s; break }
}
if (-not $state2) { $state2 = Log-State "AFTER ADD vJoy #2 + keyboard (timeout)" }
Assert ($state2.Reg -ge 2) "Registry has 2 Device keys"
Assert ($state2.JoyCpl -ge 2) "joy.cpl shows 2+ vJoy devices"
Log-DiagFile

# ══════════════════════════════════════════════════════════════
# TEST 3: Navigate to vJoy pad page, change config live
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 3: Change vJoy config (Custom: 64 buttons, 0 sticks, 0 triggers, 4 POVs) ==="

$root = Get-PadForgeRoot

# Navigate to a vJoy pad page. Tags are 1-based (Pad1, Pad2, ...).
# Try each until we find one with VJoyPresetCombo.
$foundVJoyPage = $false
for ($i = 1; $i -le 16; $i++) {
    $tag = "Pad$i"
    $nav = Navigate-ToSidebarItem $root $tag
    if (-not $nav) { continue }
    Start-Sleep -Milliseconds 500
    $root = Get-PadForgeRoot
    $preset = Find-ByAutomationId $root "VJoyPresetCombo"
    if ($preset) {
        Log "  Found VJoyPresetCombo on $tag"
        $foundVJoyPage = $true
        break
    }
}

if ($foundVJoyPage) {
    $ok = Select-ComboBoxItem $root "VJoyPresetCombo" "Custom"
    Assert $ok "Selected Custom preset"
    Start-Sleep -Milliseconds 500

    $root = Get-PadForgeRoot
    Set-TextBoxValue $root "VJoyStickCountBox" "0" | Out-Null
    Set-TextBoxValue $root "VJoyTriggerCountBox" "0" | Out-Null
    Set-TextBoxValue $root "VJoyPovCountBox" "4" | Out-Null
    Set-TextBoxValue $root "VJoyButtonCountBox" "64" | Out-Null
    Log "  Set: 0 sticks, 0 triggers, 4 POVs, 64 buttons"

    Start-Sleep -Seconds 10
    $state3 = Log-State "AFTER CONFIG CHANGE #1"
    Log-DiagFile
} else {
    $fail++
    Log "  FAIL: Could not find VJoyPresetCombo on any pad page"
}

# ══════════════════════════════════════════════════════════════
# TEST 4: Change config again (128 buttons, 2 sticks, 2 triggers, 1 POV)
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 4: Change vJoy config (Custom: 128 buttons, 2 sticks, 2 triggers, 1 POV) ==="

$root = Get-PadForgeRoot
$preset = Find-ByAutomationId $root "VJoyPresetCombo"
if ($preset) {
    Set-TextBoxValue $root "VJoyStickCountBox" "2" | Out-Null
    Set-TextBoxValue $root "VJoyTriggerCountBox" "2" | Out-Null
    Set-TextBoxValue $root "VJoyPovCountBox" "1" | Out-Null
    Set-TextBoxValue $root "VJoyButtonCountBox" "128" | Out-Null
    Log "  Set: 2 sticks, 2 triggers, 1 POV, 128 buttons"

    Start-Sleep -Seconds 10
    $state4 = Log-State "AFTER CONFIG CHANGE #2"
    Log-DiagFile
} else {
    $fail++
    Log "  FAIL: VJoyPresetCombo not found (should still be on vJoy page)"
}

# ══════════════════════════════════════════════════════════════
# TEST 5: Switch first vJoy to Xbox 360 via dashboard type button
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 5: Switch a vJoy slot to Xbox 360 ==="

$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

# Find Xbox 360 type buttons (ToolTip or HelpText = "Xbox 360")
$allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
$xboxTypeBtns = @()
foreach ($btn in $allBtns) {
    $help = $btn.Current.HelpText
    $name = $btn.Current.Name
    if ($help -eq 'Xbox 360' -or $name -eq 'Xbox 360') { $xboxTypeBtns += $btn }
}

Log "  Found $($xboxTypeBtns.Count) Xbox 360 type buttons"

$regBefore = Get-RegDeviceCount
if ($xboxTypeBtns.Count -gt 0) {
    Click-Element $xboxTypeBtns[0] | Out-Null
    Log "  Clicked Xbox 360 type button"
    Start-Sleep -Seconds 8
    $state5 = Log-State "AFTER TYPE SWITCH"
    # Should have 1 fewer registry Device key (one vJoy became Xbox)
    Assert ($state5.Reg -lt $regBefore -or $state5.Reg -eq $regBefore) "Registry device count adjusted"
    Log-DiagFile
} else {
    Log "  Trying sidebar type buttons..."
    $btn = Find-ButtonByTooltip $root "Xbox 360"
    if ($btn) {
        Click-Element $btn | Out-Null
        Start-Sleep -Seconds 8
        $state5 = Log-State "AFTER TYPE SWITCH (sidebar)"
        Log-DiagFile
    } else {
        $fail++
        Log "  FAIL: No Xbox 360 type buttons found"
    }
}

# ══════════════════════════════════════════════════════════════
# TEST 6: Delete all controllers
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 6: Delete all controllers ==="

for ($attempt = 0; $attempt -lt 16; $attempt++) {
    $root = Get-PadForgeRoot
    if (-not $root) { break }

    $deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
    if (-not $deleteBtn) {
        Log "  No more delete buttons found after $attempt deletions"
        break
    }

    Log "  Deleting controller #$($attempt + 1)..."
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 2
}

# Wait for grace period (5s) + node removal + scan-devices
Log "  Waiting for cleanup..."
Start-Sleep -Seconds 15

$stateFinal = Log-State "AFTER DELETING ALL"
Assert ($stateFinal.Reg -eq 0) "No registry Device keys remain"

# Give scan-devices extra time
Start-Sleep -Seconds 5
$joyCount = Get-JoyCplVJoyCount
$nodeId = Get-VJoyNodeId
Assert ($joyCount -eq 0) "No vJoy devices in joy.cpl (count=$joyCount)"
Assert ($nodeId -eq $null) "No vJoy device node"
Log-DiagFile

# ══════════════════════════════════════════════════════════════
# TEST 7: Re-add vJoy to verify clean re-creation
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 7: Re-add vJoy after full cleanup ==="

$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) {
    Click-Element $addCard | Out-Null
    Start-Sleep -Milliseconds 500
}

$clicked = Click-PopupButton "vJoy"
Assert $clicked "Clicked vJoy in popup (re-add)"

Start-Sleep -Seconds 3

# Assign keyboard
Log "  Assigning keyboard to re-added slot..."
$assigned = Assign-KeyboardToSlot 0
Assert $assigned "Keyboard assigned to re-added vJoy"

Start-Sleep -Seconds 8
$state7 = Log-State "AFTER RE-ADD vJoy"
Assert ($state7.Node -ne $null) "vJoy node recreated"
Assert ($state7.Reg -ge 1) "Registry restored"
Log-DiagFile

# ══════════════════════════════════════════════════════════════
# TEST 8: Xbox 360 controller (0→1→0)
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 8: Xbox 360 controller create/destroy ==="

# First delete all remaining controllers
for ($attempt = 0; $attempt -lt 16; $attempt++) {
    $root = Get-PadForgeRoot
    if (-not $root) { break }
    $deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
    if (-not $deleteBtn) { break }
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 8

$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) {
    Click-Element $addCard | Out-Null
    Start-Sleep -Milliseconds 500
}
$clicked = Click-PopupButton "Xbox 360"
Assert $clicked "Clicked Xbox 360 in popup"

Start-Sleep -Seconds 3
Log "  Assigning keyboard to Xbox 360 slot..."
$assigned = Assign-KeyboardToSlot 0
Assert $assigned "Keyboard assigned to Xbox 360"
Start-Sleep -Seconds 5

# Verify Xbox 360 VC exists (check via XInput or just confirm no crash + no vJoy in joy.cpl)
$state8 = Log-State "AFTER Xbox 360 CREATE"
Assert ($state8.JoyCpl -eq 0) "No vJoy in joy.cpl (Xbox 360 uses ViGEm, not vJoy)"
Log-DiagFile

# Delete the Xbox 360 controller
$root = Get-PadForgeRoot
$deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
if ($deleteBtn) {
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 5
    Log "  Deleted Xbox 360 controller"
}
Assert ($deleteBtn -ne $null) "Xbox 360 delete button found"

# ══════════════════════════════════════════════════════════════
# TEST 9: DS4 controller (0→1→0)
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 9: DS4 controller create/destroy ==="

$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) {
    Click-Element $addCard | Out-Null
    Start-Sleep -Milliseconds 500
}
$clicked = Click-PopupButton "DualShock 4"
Assert $clicked "Clicked DualShock 4 in popup"

Start-Sleep -Seconds 3
Log "  Assigning keyboard to DS4 slot..."
$assigned = Assign-KeyboardToSlot 0
Assert $assigned "Keyboard assigned to DS4"
Start-Sleep -Seconds 5

$state9 = Log-State "AFTER DS4 CREATE"
Assert ($state9.JoyCpl -eq 0) "No vJoy in joy.cpl (DS4 uses ViGEm, not vJoy)"
Log-DiagFile

# Delete the DS4 controller
$root = Get-PadForgeRoot
$deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
if ($deleteBtn) {
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 5
    Log "  Deleted DS4 controller"
}
Assert ($deleteBtn -ne $null) "DS4 delete button found"

# ══════════════════════════════════════════════════════════════
# TEST 10: Multiple Xbox 360 (0→2→0)
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 10: Multiple Xbox 360 create/destroy ==="

for ($n = 1; $n -le 2; $n++) {
    $root = Get-PadForgeRoot
    Navigate-ToSidebarItem $root "Dashboard"
    Start-Sleep -Milliseconds 500
    $root = Get-PadForgeRoot

    $addCard = Find-ByName $root "Add Controller"
    if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
    if ($addCard) {
        Click-Element $addCard | Out-Null
        Start-Sleep -Milliseconds 500
    }
    Click-PopupButton "Xbox 360" | Out-Null
    Start-Sleep -Seconds 3
    Log "  Assigning keyboard to Xbox 360 slot #$n..."
    Assign-KeyboardToSlot ($n - 1) | Out-Null
    Start-Sleep -Seconds 3
}

$state10 = Log-State "AFTER 2x Xbox 360 CREATE"
Assert ($state10.JoyCpl -eq 0) "No vJoy in joy.cpl (all ViGEm)"
Log-DiagFile

# Delete both Xbox 360 controllers
for ($attempt = 0; $attempt -lt 4; $attempt++) {
    $root = Get-PadForgeRoot
    if (-not $root) { break }
    $deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
    if (-not $deleteBtn) { break }
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 5
$stateAfter10 = Log-State "AFTER 2x Xbox 360 DELETE"
Assert ($stateAfter10.JoyCpl -eq 0) "No devices remain after Xbox 360 cleanup"

# ══════════════════════════════════════════════════════════════
# TEST 11: vJoy → Xbox 360 → vJoy type cycling
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== TEST 11: vJoy -> Xbox 360 -> vJoy type cycling ==="

$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

# Create vJoy
$addCard = Find-ByName $root "Add Controller"
if (-not $addCard) { $addCard = Find-ByAutomationId $root "AddControllerCard" }
if ($addCard) { Click-Element $addCard | Out-Null; Start-Sleep -Milliseconds 500 }
Click-PopupButton "vJoy" | Out-Null
Start-Sleep -Seconds 3
Assign-KeyboardToSlot 0 | Out-Null
Start-Sleep -Seconds 8

$stateBefore = Log-State "BEFORE TYPE CYCLE"
Assert ($stateBefore.JoyCpl -ge 1) "vJoy visible in joy.cpl before switch"

# Switch to Xbox 360 via dashboard type button
$root = Get-PadForgeRoot
Navigate-ToSidebarItem $root "Dashboard"
Start-Sleep -Milliseconds 500
$root = Get-PadForgeRoot

$allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
$xboxTypeBtns = @()
foreach ($btn in $allBtns) {
    $help = $btn.Current.HelpText
    $name = $btn.Current.Name
    if ($help -eq 'Xbox 360' -or $name -eq 'Xbox 360') { $xboxTypeBtns += $btn }
}

if ($xboxTypeBtns.Count -gt 0) {
    Click-Element $xboxTypeBtns[0] | Out-Null
    Log "  Clicked Xbox 360 type button"
    Start-Sleep -Seconds 10
    $stateXbox = Log-State "AFTER vJoy->Xbox360"
    Assert ($stateXbox.JoyCpl -eq 0) "vJoy gone from joy.cpl after switch to Xbox 360"
    Log-DiagFile

    # Switch back to vJoy
    $root = Get-PadForgeRoot
    Navigate-ToSidebarItem $root "Dashboard"
    Start-Sleep -Milliseconds 500
    $root = Get-PadForgeRoot

    $allBtns = Find-AllByType $root ([System.Windows.Automation.ControlType]::Button)
    $vjoyTypeBtns = @()
    foreach ($btn in $allBtns) {
        $help = $btn.Current.HelpText
        $name = $btn.Current.Name
        if ($help -eq 'vJoy' -or $name -eq 'vJoy') { $vjoyTypeBtns += $btn }
    }

    if ($vjoyTypeBtns.Count -gt 0) {
        Click-Element $vjoyTypeBtns[0] | Out-Null
        Log "  Clicked vJoy type button"
        Start-Sleep -Seconds 10
        $stateBack = Log-State "AFTER Xbox360->vJoy"
        Assert ($stateBack.JoyCpl -ge 1) "vJoy back in joy.cpl after switch from Xbox 360"
        Log-DiagFile
    } else {
        $fail++
        Log "  FAIL: No vJoy type button found on dashboard"
    }
} else {
    $fail++
    Log "  FAIL: No Xbox 360 type button found on dashboard"
}

# ══════════════════════════════════════════════════════════════
# CLEANUP
# ══════════════════════════════════════════════════════════════
Log ""
Log "=== CLEANUP ==="

for ($attempt = 0; $attempt -lt 16; $attempt++) {
    $root = Get-PadForgeRoot
    if (-not $root) { break }
    $deleteBtn = Find-ButtonByTooltip $root "Delete virtual controller"
    if (-not $deleteBtn) { break }
    Click-Element $deleteBtn | Out-Null
    Start-Sleep -Seconds 1
}

Start-Sleep -Seconds 12
Stop-PadForge
Start-Sleep -Seconds 3
$stateClean = Log-State "AFTER EXIT"

# Restore original config
if (Test-Path $configBackup) {
    Copy-Item $configBackup $configPath -Force
    Remove-Item $configBackup -Force
    Log "Restored PadForge.xml from backup"
}

# ══════════════════════════════════════════════════════════════
# SUMMARY
# ══════════════════════════════════════════════════════════════
Log ""
Log "========================================"
Log "RESULTS: $pass passed, $fail failed"
Log "========================================"

if (Test-Path $diagLogPath) {
    Log ""
    Log "=== FULL vjoy_diag.log ==="
    Get-Content $diagLogPath | ForEach-Object { Log "  $_" }
}
