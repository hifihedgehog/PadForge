# Verify vJoy fixes: navigate to Pad pages, check VJoyConfig and Mappings
# Assumes PadForge is already running with at least one vJoy slot
$outFile = "c:\Users\sonic\GitHub\PadForge\tools\vjoy_verify_log.txt"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$true_cond = [System.Windows.Automation.Condition]::TrueCondition

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseV {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@

$out = @()
$passed = 0
$failed = 0

function Log($msg) { $script:out += $msg }
function Pass($test) { $script:passed++; Log "  PASS: $test" }
function Fail($test) { $script:failed++; Log "  FAIL: $test" }

$root = $ae::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $root.FindFirst($tree::Children, $cond)
if (-not $mainWin) { "ERROR: PadForge not found" | Out-File $outFile; exit 1 }
Log "Found PadForge"

# Discover nav items (ListItems in sidebar)
$listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
$navItems = $mainWin.FindAll($tree::Descendants, $listCond)
Log ""
Log "Nav items found: $($navItems.Count)"
$padNavItems = @()
foreach ($nav in $navItems) {
    $name = $nav.Current.Name
    Log "  Nav: '$name'"
    if ($name -match "^Pad \d+" -or $name -match "^Controller \d+") {
        $padNavItems += $nav
    }
}

if ($padNavItems.Count -eq 0) {
    Log ""
    Log "No Pad/Controller nav items found. Checking SlotsItemsControl..."
    $slotsCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "SlotsItemsControl")
    $slotsCtrl = $mainWin.FindFirst($tree::Descendants, $slotsCond)
    if ($slotsCtrl) {
        $slotChildren = $slotsCtrl.FindAll($tree::Children, $true_cond)
        Log "  SlotsItemsControl children: $($slotChildren.Count)"
        foreach ($sc in $slotChildren) {
            Log "    Type=$($sc.Current.ControlType.ProgrammaticName) Name='$($sc.Current.Name)' AutomationId='$($sc.Current.AutomationId)'"
        }
    }
    # Try clicking on the first pad in the sidebar by looking for items after Dashboard/Devices
    $dashboardSeen = $false
    foreach ($nav in $navItems) {
        $name = $nav.Current.Name
        if ($name -eq "Dashboard") { $dashboardSeen = $true; continue }
        if ($name -eq "Devices" -or $name -eq "Settings" -or $name -eq "Add Controller") { continue }
        if ($dashboardSeen) {
            $padNavItems += $nav
        }
    }
    Log "  Pad nav items (heuristic): $($padNavItems.Count)"
    foreach ($p in $padNavItems) { Log "    '$($p.Current.Name)'" }
}

if ($padNavItems.Count -eq 0) {
    Log ""
    Log "ERROR: No pad navigation items found at all"
    $out | Out-File $outFile -Encoding utf8
    exit 1
}

# ============================================================
# TEST 1: Navigate to first pad, verify page loads
# ============================================================
Log ""
Log "=== TEST 1: Navigate to first pad ==="
$firstPad = $padNavItems[0]
$padName = $firstPad.Current.Name
Log "Navigating to '$padName'..."

try {
    $selPat = $firstPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat.Select()
} catch {
    # Fallback: click it
    $rect = $firstPad.Current.BoundingRectangle
    [MouseV]::Click([int]($rect.X + $rect.Width/2), [int]($rect.Y + $rect.Height/2))
}
Start-Sleep -Seconds 1

# Check for PadPageView
$padPageCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "PadPageView")
$padPage = $mainWin.FindFirst($tree::Descendants, $padPageCond)
if ($padPage) { Pass "PadPageView found after navigating to $padName" }
else { Fail "PadPageView NOT found after navigating to $padName" }

# ============================================================
# TEST 2: Check VJoyPresetCombo exists (vJoy config bar)
# ============================================================
Log ""
Log "=== TEST 2: Check vJoy config controls ==="
$presetCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyPresetCombo")
$presetCombo = $mainWin.FindFirst($tree::Descendants, $presetCond)
if ($presetCombo) {
    Pass "VJoyPresetCombo found"
    # Read current selection
    try {
        $selPat2 = $presetCombo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $curSel = $selPat2.Current.GetSelection()
        if ($curSel.Count -gt 0) {
            Log "  Current preset: '$($curSel[0].Current.Name)'"
        }
    } catch {
        Log "  (Could not read selection: $($_.Exception.Message))"
    }
} else {
    # Check if this is actually a vJoy pad
    $allText = $mainWin.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Text)))
    $isVJoy = $false
    foreach ($t in $allText) {
        if ($t.Current.Name -match "vJoy") { $isVJoy = $true; break }
    }
    if ($isVJoy) { Fail "VJoyPresetCombo NOT found on vJoy pad page" }
    else { Log "  SKIP: This pad is not a vJoy pad (VJoyPresetCombo not expected)" }
}

# ============================================================
# TEST 3: Check Mappings tab has content
# ============================================================
Log ""
Log "=== TEST 3: Check Mappings tab content ==="

# Find tabs - look for TabItem elements
$tabCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::TabItem)
$tabs = $mainWin.FindAll($tree::Descendants, $tabCond)
Log "TabItems found: $($tabs.Count)"
$mappingsTab = $null
foreach ($tab in $tabs) {
    $tabName = $tab.Current.Name
    Log "  Tab: '$tabName'"
    if ($tabName -eq "Mappings") { $mappingsTab = $tab }
}

if ($mappingsTab) {
    # Click Mappings tab
    try {
        $selPat3 = $mappingsTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat3.Select()
    } catch {
        $rect2 = $mappingsTab.Current.BoundingRectangle
        [MouseV]::Click([int]($rect2.X + $rect2.Width/2), [int]($rect2.Y + $rect2.Height/2))
    }
    Start-Sleep -Milliseconds 500

    # Count DataGrid rows or DataItems
    $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
    $rows = $mainWin.FindAll($tree::Descendants, $dgCond)
    Log "  DataItem rows: $($rows.Count)"

    if ($rows.Count -gt 0) {
        Pass "Mappings tab has $($rows.Count) mapping rows"
        # Show first few
        $showCount = [Math]::Min(3, $rows.Count)
        for ($i = 0; $i -lt $showCount; $i++) {
            $rowCells = $rows[$i].FindAll($tree::Children, $true_cond)
            $cellNames = @()
            foreach ($cell in $rowCells) { $cellNames += $cell.Current.Name }
            Log "    Row $i: $($cellNames -join ' | ')"
        }
    } else {
        # Maybe it's not a DataGrid, check for any content
        $allDescendants = $mainWin.FindAll($tree::Descendants, $true_cond)
        $mappingRelated = 0
        foreach ($d in $allDescendants) {
            $n = $d.Current.Name
            if ($n -match "Button|Axis|Trigger|Stick|DPad|LX|LY|RX|RY") { $mappingRelated++ }
        }
        if ($mappingRelated -gt 0) {
            Pass "Mappings tab has $mappingRelated mapping-related elements"
        } else {
            Fail "Mappings tab appears EMPTY (no DataItems or mapping elements)"
        }
    }
} else {
    Log "  SKIP: No Mappings tab found (might not be visible)"
}

# ============================================================
# TEST 4: If multiple pads, navigate to 2nd pad and check
# ============================================================
if ($padNavItems.Count -ge 2) {
    Log ""
    Log "=== TEST 4: Check second pad ==="
    $secondPad = $padNavItems[1]
    $pad2Name = $secondPad.Current.Name
    Log "Navigating to '$pad2Name'..."

    try {
        $selPat4 = $secondPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat4.Select()
    } catch {
        $rect3 = $secondPad.Current.BoundingRectangle
        [MouseV]::Click([int]($rect3.X + $rect3.Width/2), [int]($rect3.Y + $rect3.Height/2))
    }
    Start-Sleep -Seconds 1

    $padPage2 = $mainWin.FindFirst($tree::Descendants, $padPageCond)
    if ($padPage2) { Pass "PadPageView found for $pad2Name" }
    else { Fail "PadPageView NOT found for $pad2Name" }

    # Check Mappings tab on second pad too
    $tabs2 = $mainWin.FindAll($tree::Descendants, $tabCond)
    $mappingsTab2 = $null
    foreach ($tab in $tabs2) { if ($tab.Current.Name -eq "Mappings") { $mappingsTab2 = $tab } }
    if ($mappingsTab2) {
        try {
            $selPat5 = $mappingsTab2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat5.Select()
        } catch {
            $rect4 = $mappingsTab2.Current.BoundingRectangle
            [MouseV]::Click([int]($rect4.X + $rect4.Width/2), [int]($rect4.Y + $rect4.Height/2))
        }
        Start-Sleep -Milliseconds 500

        $rows2 = $mainWin.FindAll($tree::Descendants, $dgCond)
        Log "  DataItem rows on $pad2Name: $($rows2.Count)"
        if ($rows2.Count -gt 0) { Pass "Second pad Mappings has $($rows2.Count) rows" }
        else { Fail "Second pad Mappings appears EMPTY" }
    }

    # Navigate back to first pad and re-check Mappings (the "clearing" bug)
    Log ""
    Log "=== TEST 5: Re-check first pad Mappings after switching ==="
    try {
        $selPat6 = $firstPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat6.Select()
    } catch {
        $rect5 = $firstPad.Current.BoundingRectangle
        [MouseV]::Click([int]($rect5.X + $rect5.Width/2), [int]($rect5.Y + $rect5.Height/2))
    }
    Start-Sleep -Seconds 1

    $tabs3 = $mainWin.FindAll($tree::Descendants, $tabCond)
    $mappingsTab3 = $null
    foreach ($tab in $tabs3) { if ($tab.Current.Name -eq "Mappings") { $mappingsTab3 = $tab } }
    if ($mappingsTab3) {
        try {
            $selPat7 = $mappingsTab3.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat7.Select()
        } catch {
            $rect6 = $mappingsTab3.Current.BoundingRectangle
            [MouseV]::Click([int]($rect6.X + $rect6.Width/2), [int]($rect6.Y + $rect6.Height/2))
        }
        Start-Sleep -Milliseconds 500

        $rows3 = $mainWin.FindAll($tree::Descendants, $dgCond)
        Log "  DataItem rows on $padName after switching back: $($rows3.Count)"
        if ($rows3.Count -gt 0) { Pass "First pad Mappings still has $($rows3.Count) rows after switching" }
        else { Fail "First pad Mappings CLEARED after navigating to second pad and back" }
    }
} else {
    Log ""
    Log "=== TEST 4-5: SKIP (only 1 pad found) ==="
}

# ============================================================
# SUMMARY
# ============================================================
Log ""
Log "=============================="
Log "RESULTS: $passed PASSED, $failed FAILED"
Log "=============================="

$out | Out-File $outFile -Encoding utf8
