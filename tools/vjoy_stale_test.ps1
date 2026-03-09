# Test: verify fresh vJoy slots get Xbox 360 default, not stale Custom config
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_stale_test_log.txt"
function Log($msg) { $msg | Out-File $logFile -Append -Encoding utf8; Write-Host $msg }

"" | Out-File $logFile -Encoding utf8 -Force
Log "=== vJoy Stale Config Test $(Get-Date) ==="

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $mainWin) { Log "FAIL: PadForge window not found"; exit 1 }
Log "Found PadForge window"

function Find-ById($automationId) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $automationId)
    return $mainWin.FindFirst($tree::Descendants, $c)
}

function Find-ByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
    return $mainWin.FindFirst($tree::Descendants, $c)
}

function Navigate($name) {
    $el = Find-ByName $name
    if (-not $el) { Log "  FAIL: nav item '$name' not found"; return $false }
    try {
        $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    } catch {
        try {
            $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
        } catch {
            Log "  FAIL: Cannot select/invoke '$name'"
            return $false
        }
    }
    Start-Sleep -Seconds 1
    return $true
}

function Get-ComboValue($automationId) {
    $combo = Find-ById $automationId
    if (-not $combo) { return "NOT_FOUND" }
    try {
        $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $selected = $sel.Current.GetSelection()
        if ($selected.Count -gt 0) { return $selected[0].Current.Name }
    } catch {}
    return "NONE"
}

function Get-TextBoxValue($automationId) {
    $tb = Find-ById $automationId
    if (-not $tb) { return "NOT_FOUND" }
    try {
        $val = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $val.Current.Value
    } catch {}
    return "ERROR"
}

# Test 1: Navigate to existing Pad1 and check its config
Log ""
Log "--- Test 1: Check existing Pad1 config ---"
if (-not (Navigate "Pad1")) { Log "FAIL: Cannot navigate to Pad1"; exit 1 }
Log "Navigated to Pad1"

$preset1 = Get-ComboValue "VJoyPresetCombo"
$btnCount1 = Get-TextBoxValue "VJoyButtonCountBox"
Log "Pad1 preset: $preset1, buttons: $btnCount1"

# Slot 0 is an existing created vJoy - its saved config SHOULD be loaded
# XML has: Preset=Custom ButtonCount=1
# With our fix, created+vJoy slots still get their saved config (correct behavior)
Log "  (Existing slot - config loaded from XML is expected)"

# Test 2: Add a 2nd vJoy and verify it gets Xbox 360 default
Log ""
Log "--- Test 2: Add 2nd vJoy - should get Xbox 360 default ---"

# Click Add Controller in sidebar
$addNav = Find-ByName "Add Controller"
if ($addNav) {
    try {
        $sel = $addNav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    } catch {
        try {
            $inv = $addNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
        } catch {
            Log "  Cannot click Add Controller"
        }
    }
    Start-Sleep -Seconds 1

    # Find vJoy button in popup
    $allBtns = $mainWin.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Button)))
    $vjoyBtn = $null
    foreach ($btn in $allBtns) {
        if ($btn.Current.Name -match "vJoy") { $vjoyBtn = $btn; break }
    }

    if ($vjoyBtn) {
        Log "Found vJoy button: '$($vjoyBtn.Current.Name)'"
        $inv = $vjoyBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        Log "Clicked vJoy button"
        Start-Sleep -Seconds 3

        # Navigate to new Pad2
        if (Navigate "Pad2") {
            Log "Navigated to Pad2"
            Start-Sleep -Seconds 1

            $preset2 = Get-ComboValue "VJoyPresetCombo"
            $btnCount2 = Get-TextBoxValue "VJoyButtonCountBox"
            Log "Pad2 preset: $preset2, buttons: $btnCount2"

            # XML had stale SlotIndex=1 with Xbox360 preset (which is actually correct default)
            # But the key test: it should NOT have inherited any Custom config
            if ($preset2 -eq "Xbox 360" -and $btnCount2 -eq "11") {
                Log "  PASS: New vJoy slot got Xbox 360 default (not stale config)"
            } else {
                Log "  FAIL: Expected Xbox 360/11, got preset='$preset2' buttons='$btnCount2'"
            }
        } else {
            Log "  FAIL: Pad2 nav item not found"
        }
    } else {
        Log "  SKIP: vJoy button not found in popup (may be at capacity)"
    }
} else {
    Log "  SKIP: Add Controller not found"
}

# Test 3: Go back to Pad1 and verify config preserved
Log ""
Log "--- Test 3: Pad1 config preserved after adding Pad2 ---"
if (Navigate "Pad1") {
    Start-Sleep -Seconds 1
    $preset1After = Get-ComboValue "VJoyPresetCombo"
    $btnCount1After = Get-TextBoxValue "VJoyButtonCountBox"
    Log "Pad1 preset after: $preset1After, buttons: $btnCount1After"

    if ($preset1After -eq $preset1 -and $btnCount1After -eq $btnCount1) {
        Log "  PASS: Pad1 config unchanged"
    } else {
        Log "  FAIL: Pad1 changed! Before: $preset1/$btnCount1, After: $preset1After/$btnCount1After"
    }
}

Log ""
Log "=== Tests Complete ==="
