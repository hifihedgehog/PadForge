# End-to-end vJoy test: fresh start, add slots, verify configs, mappings, reorder
# Must run elevated (PadForge runs as admin with vJoy installed)

$padForgeXml = 'C:\PadForge\PadForge.xml'
$padForgeExe = 'C:\PadForge\PadForge.exe'
$logFile = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_e2e_test_log.txt'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

$global:passed = 0
$global:failed = 0
$global:mainWin = $null

function Log($msg) {
    $ts = (Get-Date).ToString("HH:mm:ss.fff")
    "$ts $msg" | Out-File $logFile -Append -Encoding utf8
    Write-Host "$ts $msg"
}

function Pass($msg) { $global:passed++; Log "  PASS: $msg" }
function Fail($msg) { $global:failed++; Log "  FAIL: $msg" }

function Find-ById($id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id)
    return $global:mainWin.FindFirst($tree::Descendants, $c)
}

function Find-ByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
    return $global:mainWin.FindFirst($tree::Descendants, $c)
}

function Navigate($name) {
    # Find all matching elements and pick the ListItem (NavigationViewItem)
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
    $matches = $global:mainWin.FindAll($tree::Descendants, $nameCond)
    $el = $null
    foreach ($m in $matches) {
        if ($m.Current.ControlType -eq $ct::ListItem) { $el = $m; break }
    }
    if (-not $el) {
        # Fallback: first match
        $el = $global:mainWin.FindFirst($tree::Descendants, $nameCond)
    }
    if (-not $el) { Log "  Nav '$name' not found"; return $false }
    try {
        $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Start-Sleep -Milliseconds 500
        return $true
    } catch {
        try {
            $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
            Start-Sleep -Milliseconds 500
            return $true
        } catch {
            Log "  Cannot select/invoke '$name': $_"
            return $false
        }
    }
}

function Click-Button($id) {
    $btn = Find-ById $id
    if (-not $btn) { Log "  Button '$id' not found"; return $false }
    try {
        $inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        return $true
    } catch { Log "  Cannot click '$id': $_"; return $false }
}

function Get-ComboValue($id) {
    $combo = Find-ById $id
    if (-not $combo) { return "NOT_FOUND" }
    try {
        $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $s = $sel.Current.GetSelection()
        if ($s.Count -gt 0) { return $s[0].Current.Name }
    } catch {}
    return "NONE"
}

function Get-TextValue($id) {
    $tb = Find-ById $id
    if (-not $tb) { return "NOT_FOUND" }
    try {
        return ($tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    } catch { return "ERROR" }
}

function Set-ComboIndex($id, $index) {
    $combo = Find-ById $id
    if (-not $combo) { Log "  Combo '$id' not found"; return }
    $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $exp.Expand()
    Start-Sleep -Milliseconds 300
    $itemCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $items = $combo.FindAll($tree::Children, $itemCond)
    if ($items.Count -gt $index) {
        $sel = $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    }
    Start-Sleep -Milliseconds 300
    $exp.Collapse()
}

function Set-TextValue($id, $value, [switch]$Apply) {
    $tb = Find-ById $id
    if (-not $tb) { Log "  TextBox '$id' not found"; return }
    $val = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $val.SetValue($value)
    if ($Apply) {
        $tb.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Start-Sleep -Milliseconds 200
    }
}

function Click-TabByName($name) {
    $radioCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::RadioButton)
    $radios = $global:mainWin.FindAll($tree::Descendants, $radioCond)
    foreach ($rb in $radios) {
        if ($rb.Current.Name -eq $name) {
            try {
                $inv = $rb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $inv.Invoke()
                Start-Sleep -Milliseconds 500
                return $true
            } catch {
                try {
                    $sel = $rb.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                    $sel.Select()
                    Start-Sleep -Milliseconds 500
                    return $true
                } catch {}
            }
        }
    }
    Log "  Tab '$name' not found"
    return $false
}

function Count-DataGridRows() {
    $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataGrid)
    $dg = $global:mainWin.FindFirst($tree::Descendants, $dgCond)
    if (-not $dg) { return -1 }
    $rowCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
    $rows = $dg.FindAll($tree::Children, $rowCond)
    return $rows.Count
}

function Get-DataGridCellText($rowIndex, $colIndex) {
    $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataGrid)
    $dg = $global:mainWin.FindFirst($tree::Descendants, $dgCond)
    if (-not $dg) { return "NO_DATAGRID" }
    $rowCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
    $rows = $dg.FindAll($tree::Children, $rowCond)
    if ($rows.Count -le $rowIndex) { return "NO_ROW" }
    $row = $rows[$rowIndex]
    $cells = $row.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    if ($cells.Count -le $colIndex) { return "NO_CELL" }
    return $cells[$colIndex].Current.Name
}

function Query-VJoyDevice($deviceId) {
    # Spawn fresh process to avoid DLL cache
    $script = @"
try {
    `$dllPath = 'C:\Program Files\vJoy\vJoyInterface.dll'
    if (-not (Test-Path `$dllPath)) { `$dllPath = 'C:\Program Files\vJoy\x64\vJoyInterface.dll' }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class VJQ {
    [DllImport("vJoyInterface.dll")] public static extern int GetVJDStatus(uint id);
    [DllImport("vJoyInterface.dll")] public static extern int GetVJDButtonNumber(uint id);
    [DllImport("vJoyInterface.dll")] public static extern int GetVJDContPovNumber(uint id);
    [DllImport("vJoyInterface.dll")] public static extern int GetVJDAxisExist(uint id, uint axis);
}
'@
    [System.Runtime.InteropServices.NativeLibrary]::TryLoad(`$dllPath, [ref]`$null) | Out-Null
    `$status = [VJQ]::GetVJDStatus($deviceId)
    `$btns = [VJQ]::GetVJDButtonNumber($deviceId)
    `$povs = [VJQ]::GetVJDContPovNumber($deviceId)
    Write-Output "status=`$status btns=`$btns povs=`$povs"
} catch {
    Write-Output "error: `$_"
}
"@
    $result = powershell -NoProfile -Command $script 2>&1
    return "$result"
}

function Refresh-Window() {
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $global:mainWin = $ae::RootElement.FindFirst($tree::Children, $cond)
    return ($global:mainWin -ne $null)
}

function Kill-PadForge() {
    Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Launch-PadForge() {
    Start-Process $padForgeExe
    Start-Sleep -Seconds 6
    return Refresh-Window
}

# ═══════════════════════════════════════════════════════
#  SETUP: Clean slate - no slots created
# ═══════════════════════════════════════════════════════
"" | Out-File $logFile -Encoding utf8 -Force
Log "═══════════════════════════════════════════════════"
Log "  vJoy End-to-End Test $(Get-Date)"
Log "═══════════════════════════════════════════════════"

Kill-PadForge

# Reset XML: no slots, clear stale vJoy configs
[xml]$xml = Get-Content $padForgeXml
for ($i = 0; $i -lt 16; $i++) {
    $xml.PadForgeSettings.AppSettings.SlotControllerTypes.ChildNodes[$i].InnerText = '0'
    $xml.PadForgeSettings.AppSettings.SlotCreated.ChildNodes[$i].InnerText = 'false'
}
# Inject stale Custom config at slot 0 to test the load guard
foreach ($cfg in $xml.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes) {
    if ($cfg.SlotIndex -eq "0") {
        $cfg.Preset = "Custom"
        $cfg.ThumbstickCount = "1"
        $cfg.TriggerCount = "0"
        $cfg.PovCount = "0"
        $cfg.ButtonCount = "99"
    }
}
$xml.Save($padForgeXml)
Log "Reset XML: all slots uncreated, injected stale Custom/99btn at slot 0"

if (-not (Launch-PadForge)) { Log "FATAL: PadForge not found"; exit 1 }
Log "PadForge launched"

# ═══════════════════════════════════════════════════════
#  TEST 1: Add vJoy slot - should get Xbox 360 default
#          (not stale Custom/99btn from XML)
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 1: Add vJoy - should get Xbox 360 default (not stale Custom/99) ---"

# Click Add Controller nav item to open popup
Navigate "Add Controller" | Out-Null

# Wait for popup with retry (popup may take a moment to appear)
$vjoyBtnFound = $false
for ($retry = 0; $retry -lt 5; $retry++) {
    Start-Sleep -Milliseconds 500
    $vjoyBtnEl = Find-ById "AddVJoyBtn"
    if ($vjoyBtnEl) { $vjoyBtnFound = $true; break }
    Log "  Retry $($retry+1): AddVJoyBtn not found yet, re-clicking..."
    Navigate "Add Controller" | Out-Null
}

# Click vJoy button in popup
if ($vjoyBtnFound -and (Click-Button "AddVJoyBtn")) {
    Log "Clicked AddVJoyBtn"
    Start-Sleep -Seconds 4  # Wait for vJoy node creation

    # Navigate to Pad1 (the new vJoy slot)
    if (Navigate "Pad1") {
        $preset = Get-ComboValue "VJoyPresetCombo"
        $btns = Get-TextValue "VJoyButtonCountBox"
        Log "Pad1: preset=$preset buttons=$btns"

        if ($preset -eq "Xbox 360" -and $btns -eq "11") {
            Pass "Fresh vJoy got Xbox 360 default (stale Custom/99 blocked)"
        } else {
            Fail "Expected Xbox 360/11, got $preset/$btns (stale config leaked)"
        }

        # Verify vJoy device via DLL query
        $devInfo = Query-VJoyDevice 1
        Log "  Device 1 query: $devInfo"
        if ($devInfo -match "btns=11") {
            Pass "Device 1 has 11 buttons (Xbox 360 HID descriptor)"
        } else {
            Fail "Device 1 expected 11 buttons: $devInfo"
        }
    } else {
        Fail "Pad1 not found after adding vJoy"
    }
} else {
    Fail "AddVJoyBtn not found - popup may not have opened"
}

# ═══════════════════════════════════════════════════════
#  TEST 2: Switch to Custom 32btn, verify Mappings tab
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 2: Change to Custom 32btn, check Mappings tab ---"

Navigate "Pad1" | Out-Null
Set-ComboIndex "VJoyPresetCombo" 2  # Custom
Start-Sleep -Seconds 1
Set-TextValue "VJoyButtonCountBox" "32" -Apply
Start-Sleep -Seconds 6  # Wait for node restart

$preset = Get-ComboValue "VJoyPresetCombo"
$btns = Get-TextValue "VJoyButtonCountBox"
Log "After config change: preset=$preset buttons=$btns"

if ($preset -eq "Custom" -and $btns -eq "32") {
    Pass "Config changed to Custom 32btn"
} else {
    Fail "Config change failed: $preset/$btns"
}

# Switch to Mappings tab and count rows
Click-TabByName "Mappings" | Out-Null
Start-Sleep -Seconds 1
$rowCount = Count-DataGridRows
Log "  Mappings tab row count: $rowCount"

# Custom 32btn + 2 sticks (4 axes) + 2 triggers (2 axes) + 1 POV (4 dirs) = 32+4+2+4 = 42
# Actually: 2 sticks = 4 rows, 2 triggers = 2 rows, 32 buttons = 32 rows, 1 POV = 4 rows = 42
if ($rowCount -ge 30) {
    Pass "Mappings tab has $rowCount rows (populated for Custom 32btn)"
} elseif ($rowCount -eq 0) {
    Fail "Mappings tab is EMPTY (0 rows)"
} else {
    Fail "Mappings tab has only $rowCount rows (expected ~42 for Custom 32btn)"
}

# Verify device descriptor matches
$devInfo = Query-VJoyDevice 1
Log "  Device 1 query: $devInfo"
if ($devInfo -match "btns=32") {
    Pass "Device 1 has 32 buttons"
} else {
    Fail "Device 1 expected 32 buttons: $devInfo"
}

# ═══════════════════════════════════════════════════════
#  TEST 3: Add 2nd vJoy - verify Pad1 config preserved
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 3: Add 2nd vJoy, verify Pad1 config preserved ---"

# Remember Pad1 state
$pad1PresetBefore = Get-ComboValue "VJoyPresetCombo"
Navigate "Pad1" | Out-Null  # Make sure we're on Pad1
$pad1PresetBefore = Get-ComboValue "VJoyPresetCombo"
$pad1BtnsBefore = Get-TextValue "VJoyButtonCountBox"
Log "Pad1 before: preset=$pad1PresetBefore buttons=$pad1BtnsBefore"

# Switch to Mappings tab and count before
Click-TabByName "Mappings" | Out-Null
Start-Sleep -Seconds 1
$pad1RowsBefore = Count-DataGridRows
Log "Pad1 mapping rows before: $pad1RowsBefore"

# Add 2nd vJoy
Navigate "Add Controller" | Out-Null
$vjoyBtnFound2 = $false
for ($retry = 0; $retry -lt 5; $retry++) {
    Start-Sleep -Milliseconds 500
    if (Find-ById "AddVJoyBtn") { $vjoyBtnFound2 = $true; break }
    Navigate "Add Controller" | Out-Null
}
if ($vjoyBtnFound2 -and (Click-Button "AddVJoyBtn")) {
    Log "Clicked AddVJoyBtn for 2nd vJoy"
    Start-Sleep -Seconds 6  # Wait for node restart (descriptor count change 1->2)

    # Check Pad2 config
    if (Navigate "Pad2") {
        $p2preset = Get-ComboValue "VJoyPresetCombo"
        $p2btns = Get-TextValue "VJoyButtonCountBox"
        Log "Pad2: preset=$p2preset buttons=$p2btns"

        if ($p2preset -eq "Xbox 360" -and $p2btns -eq "11") {
            Pass "2nd vJoy got Xbox 360 default"
        } else {
            Fail "2nd vJoy expected Xbox 360/11, got $p2preset/$p2btns"
        }
    } else {
        Fail "Pad2 not found after adding 2nd vJoy"
    }

    # Go back to Pad1 and verify config preserved
    if (Navigate "Pad1") {
        $pad1PresetAfter = Get-ComboValue "VJoyPresetCombo"
        $pad1BtnsAfter = Get-TextValue "VJoyButtonCountBox"
        Log "Pad1 after: preset=$pad1PresetAfter buttons=$pad1BtnsAfter"

        if ($pad1PresetAfter -eq $pad1PresetBefore -and $pad1BtnsAfter -eq $pad1BtnsBefore) {
            Pass "Pad1 config preserved ($pad1PresetAfter/$pad1BtnsAfter)"
        } else {
            Fail "Pad1 config changed! Before: $pad1PresetBefore/$pad1BtnsBefore, After: $pad1PresetAfter/$pad1BtnsAfter"
        }

        # Verify Mappings tab still populated
        Click-TabByName "Mappings" | Out-Null
        Start-Sleep -Seconds 1
        $pad1RowsAfter = Count-DataGridRows
        Log "Pad1 mapping rows after: $pad1RowsAfter"

        if ($pad1RowsAfter -eq $pad1RowsBefore) {
            Pass "Pad1 mapping row count preserved ($pad1RowsAfter rows)"
        } elseif ($pad1RowsAfter -gt 0) {
            Fail "Pad1 mapping rows changed: before=$pad1RowsBefore after=$pad1RowsAfter"
        } else {
            Fail "Pad1 Mappings tab EMPTY after adding 2nd vJoy!"
        }
    } else {
        Fail "Cannot navigate to Pad1 after adding Pad2"
    }
} else {
    Fail "AddVJoyBtn not found for 2nd vJoy"
}

# ═══════════════════════════════════════════════════════
#  TEST 4: Verify vJoy device descriptors (2 devices)
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 4: Verify both vJoy device descriptors ---"

$dev1 = Query-VJoyDevice 1
$dev2 = Query-VJoyDevice 2
Log "  Device 1: $dev1"
Log "  Device 2: $dev2"

if ($dev1 -match "btns=32") {
    Pass "Device 1 = 32 buttons (Custom config)"
} else {
    Fail "Device 1 expected 32 buttons: $dev1"
}

if ($dev2 -match "btns=11") {
    Pass "Device 2 = 11 buttons (Xbox 360 preset)"
} else {
    Fail "Device 2 expected 11 buttons: $dev2"
}

# ═══════════════════════════════════════════════════════
#  TEST 5: Change Pad2 to Custom 64btn, verify Pad1 untouched
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 5: Change Pad2 to Custom 64btn ---"

Navigate "Pad2" | Out-Null
Set-ComboIndex "VJoyPresetCombo" 2  # Custom
Start-Sleep -Seconds 1
Set-TextValue "VJoyButtonCountBox" "64" -Apply
Start-Sleep -Seconds 6

$p2preset = Get-ComboValue "VJoyPresetCombo"
$p2btns = Get-TextValue "VJoyButtonCountBox"
Log "Pad2 after change: preset=$p2preset buttons=$p2btns"

if ($p2preset -eq "Custom" -and $p2btns -eq "64") {
    Pass "Pad2 changed to Custom 64btn"
} else {
    Fail "Pad2 change failed: $p2preset/$p2btns"
}

# Verify Pad1 still correct
Navigate "Pad1" | Out-Null
$p1check = Get-ComboValue "VJoyPresetCombo"
$p1btnsCheck = Get-TextValue "VJoyButtonCountBox"
Log "Pad1 after Pad2 change: preset=$p1check buttons=$p1btnsCheck"

if ($p1check -eq "Custom" -and $p1btnsCheck -eq "32") {
    Pass "Pad1 preserved as Custom 32btn after Pad2 change"
} else {
    Fail "Pad1 corrupted after Pad2 change: $p1check/$p1btnsCheck"
}

# Check Pad1 Mappings tab
Click-TabByName "Mappings" | Out-Null
Start-Sleep -Seconds 1
$finalRows = Count-DataGridRows
Log "Pad1 final mapping rows: $finalRows"

if ($finalRows -gt 0) {
    Pass "Pad1 Mappings tab still populated ($finalRows rows)"
} else {
    Fail "Pad1 Mappings tab EMPTY after Pad2 config change!"
}

# Final device descriptor check
$dev1Final = Query-VJoyDevice 1
$dev2Final = Query-VJoyDevice 2
Log "  Device 1 final: $dev1Final"
Log "  Device 2 final: $dev2Final"

if ($dev1Final -match "btns=32" -and $dev2Final -match "btns=64") {
    Pass "Both devices have correct descriptors (32, 64)"
} else {
    Fail "Device descriptors wrong: dev1=$dev1Final dev2=$dev2Final"
}

# ═══════════════════════════════════════════════════════
#  TEST 6: Restart PadForge - configs survive restart
# ═══════════════════════════════════════════════════════
Log ""
Log "--- TEST 6: Restart PadForge - configs survive ---"

Kill-PadForge
if (-not (Launch-PadForge)) { Log "FATAL: PadForge not found after restart"; exit 1 }
Log "PadForge restarted"

# Check Pad1
if (Navigate "Pad1") {
    $p1r = Get-ComboValue "VJoyPresetCombo"
    $p1br = Get-TextValue "VJoyButtonCountBox"
    Log "Pad1 after restart: preset=$p1r buttons=$p1br"

    if ($p1r -eq "Custom" -and $p1br -eq "32") {
        Pass "Pad1 config survived restart (Custom 32)"
    } else {
        Fail "Pad1 config lost after restart: $p1r/$p1br"
    }

    Click-TabByName "Mappings" | Out-Null
    Start-Sleep -Seconds 1
    $restartRows = Count-DataGridRows
    Log "Pad1 mapping rows after restart: $restartRows"
    if ($restartRows -gt 0) {
        Pass "Pad1 Mappings tab populated after restart ($restartRows rows)"
    } else {
        Fail "Pad1 Mappings tab EMPTY after restart!"
    }
}

# Check Pad2
if (Navigate "Pad2") {
    $p2r = Get-ComboValue "VJoyPresetCombo"
    $p2br = Get-TextValue "VJoyButtonCountBox"
    Log "Pad2 after restart: preset=$p2r buttons=$p2br"

    if ($p2r -eq "Custom" -and $p2br -eq "64") {
        Pass "Pad2 config survived restart (Custom 64)"
    } else {
        Fail "Pad2 config lost after restart: $p2r/$p2br"
    }
}

# ═══════════════════════════════════════════════════════
#  RESULTS
# ═══════════════════════════════════════════════════════
Log ""
Log "═══════════════════════════════════════════════════"
Log "  RESULTS: $($global:passed) passed, $($global:failed) failed out of $($global:passed + $global:failed) tests"
Log "═══════════════════════════════════════════════════"
