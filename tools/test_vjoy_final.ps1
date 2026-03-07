# Final vJoy verification: navigate pads, check config bar, check mappings tab content
$outFile = "c:\Users\sonic\GitHub\PadForge\tools\vjoy_final_log.txt"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $ae  = [System.Windows.Automation.AutomationElement]
    $ct  = [System.Windows.Automation.ControlType]
    $tree = [System.Windows.Automation.TreeScope]
    $true_cond = [System.Windows.Automation.Condition]::TrueCondition

    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseF {
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

    $root = $ae::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $mainWin = $root.FindFirst($tree::Children, $cond)
    if (-not $mainWin) { "ERROR: PadForge not found" | Out-File $outFile; exit 1 }
    $out += "Found PadForge"

    # Find pad nav items
    $listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $navItems = $mainWin.FindAll($tree::Descendants, $listCond)
    $padNavItems = @()
    foreach ($nav in $navItems) {
        $name = $nav.Current.Name
        if ($name -match "^Pad\d+$") { $padNavItems += $nav }
    }
    $out += "Pad nav items: $($padNavItems.Count) ($($padNavItems | ForEach-Object { $_.Current.Name }))"

    if ($padNavItems.Count -lt 2) {
        $out += "Need at least 2 pads for full test. Found: $($padNavItems.Count)"
        $out | Out-File $outFile -Encoding utf8
        exit 1
    }

    # Helper: find MappingsTab RadioButton and click it
    function ClickMappingsTab {
        $mtCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "MappingsTab")
        $mt = $mainWin.FindFirst($tree::Descendants, $mtCond)
        if ($mt) {
            # Try InvokePattern first (RadioButton may support it)
            try {
                $inv = $mt.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $inv.Invoke()
            } catch {
                # Fallback: mouse click
                $r = $mt.Current.BoundingRectangle
                [MouseF]::Click([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
            }
            Start-Sleep -Milliseconds 500
            return $true
        }
        return $false
    }

    # Helper: count DataGrid rows (mapping entries)
    function CountMappingRows {
        $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
        $rows = $mainWin.FindAll($tree::Descendants, $dgCond)
        return $rows.Count
    }

    # ================================================================
    # TEST 1: Navigate to Pad1, verify VJoyPresetCombo exists
    # ================================================================
    $out += ""
    $out += "=== TEST 1: Navigate to Pad1, check vJoy config ==="
    $selPat = $padNavItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat.Select()
    Start-Sleep -Seconds 1

    $presetCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyPresetCombo")
    $presetCombo = $mainWin.FindFirst($tree::Descendants, $presetCond)
    if ($presetCombo) {
        $passed++; $out += "  PASS: VJoyPresetCombo found on Pad1"
        # Try to read current value
        try {
            $valPat = $presetCombo.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $out += "  Current value: '$($valPat.Current.Value)'"
        } catch {
            $out += "  (No ValuePattern on combo)"
        }
    } else {
        $failed++; $out += "  FAIL: VJoyPresetCombo NOT found on Pad1"
    }

    # ================================================================
    # TEST 2: Click Mappings tab, verify it has rows
    # ================================================================
    $out += ""
    $out += "=== TEST 2: Pad1 Mappings tab content ==="
    $mtFound = ClickMappingsTab
    if ($mtFound) {
        $out += "  MappingsTab clicked"
        $rowCount1 = CountMappingRows
        $out += "  Mapping rows: $rowCount1"
        if ($rowCount1 -gt 0) {
            $passed++; $out += "  PASS: Pad1 Mappings has $rowCount1 rows"
        } else {
            $failed++; $out += "  FAIL: Pad1 Mappings is EMPTY"
        }
    } else {
        $failed++; $out += "  FAIL: MappingsTab not found"
    }

    # ================================================================
    # TEST 3: Navigate to Pad2, check VJoyPresetCombo
    # ================================================================
    $out += ""
    $out += "=== TEST 3: Navigate to Pad2, check vJoy config ==="
    $selPat2 = $padNavItems[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat2.Select()
    Start-Sleep -Seconds 1

    $presetCombo2 = $mainWin.FindFirst($tree::Descendants, $presetCond)
    if ($presetCombo2) {
        $passed++; $out += "  PASS: VJoyPresetCombo found on Pad2"
    } else {
        $failed++; $out += "  FAIL: VJoyPresetCombo NOT found on Pad2"
    }

    # ================================================================
    # TEST 4: Pad2 Mappings tab has content
    # ================================================================
    $out += ""
    $out += "=== TEST 4: Pad2 Mappings tab content ==="
    $mtFound2 = ClickMappingsTab
    if ($mtFound2) {
        $rowCount2 = CountMappingRows
        $out += "  Mapping rows: $rowCount2"
        if ($rowCount2 -gt 0) {
            $passed++; $out += "  PASS: Pad2 Mappings has $rowCount2 rows"
        } else {
            $failed++; $out += "  FAIL: Pad2 Mappings is EMPTY"
        }
    } else {
        $failed++; $out += "  FAIL: MappingsTab not found on Pad2"
    }

    # ================================================================
    # TEST 5: Navigate back to Pad1, Mappings still populated
    # ================================================================
    $out += ""
    $out += "=== TEST 5: Back to Pad1, verify Mappings preserved ==="
    $selPat3 = $padNavItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat3.Select()
    Start-Sleep -Seconds 1

    $mtFound3 = ClickMappingsTab
    if ($mtFound3) {
        $rowCount3 = CountMappingRows
        $out += "  Mapping rows: $rowCount3"
        if ($rowCount3 -gt 0) {
            $passed++; $out += "  PASS: Pad1 Mappings preserved ($rowCount3 rows)"
        } else {
            $failed++; $out += "  FAIL: Pad1 Mappings CLEARED after switching"
        }
    } else {
        $failed++; $out += "  FAIL: MappingsTab not found after switching back"
    }

    # ================================================================
    # TEST 6: Verify stale config NOT loaded for uncreated slots
    # Check that the XML VJoyConfigs for non-existent slots didn't affect anything
    # Slot 2 has stale Custom config in XML but is not created
    # ================================================================
    $out += ""
    $out += "=== TEST 6: Stale config isolation ==="
    # Count total nav pad items - should be exactly 2 (not more from stale configs)
    if ($padNavItems.Count -eq 2) {
        $passed++; $out += "  PASS: Exactly 2 pad nav items (stale configs ignored)"
    } else {
        $failed++; $out += "  FAIL: Expected 2 pads, found $($padNavItems.Count)"
    }

    # ================================================================
    # SUMMARY
    # ================================================================
    $out += ""
    $out += "=============================="
    $out += "RESULTS: $passed PASSED, $failed FAILED"
    $out += "=============================="

    $out | Out-File $outFile -Encoding utf8

} catch {
    $errMsg = "EXCEPTION: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
    $errMsg | Out-File $outFile -Encoding utf8
}
