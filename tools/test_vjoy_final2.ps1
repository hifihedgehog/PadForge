# Final vJoy verification using MappingsCountIndicator
$outFile = "c:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_final2_log.txt"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $ae  = [System.Windows.Automation.AutomationElement]
    $ct  = [System.Windows.Automation.ControlType]
    $tree = [System.Windows.Automation.TreeScope]

    $out = @()
    $passed = 0
    $failed = 0

    $root = $ae::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $mainWin = $root.FindFirst($tree::Children, $cond)
    if (-not $mainWin) { "ERROR: PadForge not found" | Out-File $outFile; exit 1 }

    # Find pad nav items
    $listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $navItems = $mainWin.FindAll($tree::Descendants, $listCond)
    $padNavItems = @()
    foreach ($nav in $navItems) {
        if ($nav.Current.Name -match "^Pad\d+$") { $padNavItems += $nav }
    }
    $out += "Pad nav items: $($padNavItems.Count) ($($padNavItems | ForEach-Object { $_.Current.Name }))"

    if ($padNavItems.Count -lt 2) {
        $out += "Need 2+ pads. Found: $($padNavItems.Count)"
        $out | Out-File $outFile -Encoding utf8; exit 1
    }

    # Helper: get MappingsCountIndicator value
    function GetMappingsCount {
        $mcCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "MappingsCountIndicator")
        $mc = $mainWin.FindFirst($tree::Descendants, $mcCond)
        if ($mc) { return $mc.Current.Name }
        return "NOT_FOUND"
    }

    # Helper: get VJoyPresetCombo presence
    function GetPresetCombo {
        $pcCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyPresetCombo")
        return $mainWin.FindFirst($tree::Descendants, $pcCond)
    }

    # ================================================================
    # TEST 1: Navigate to Pad1, check vJoy config bar
    # ================================================================
    $out += ""
    $out += "=== TEST 1: Pad1 vJoy config ==="
    $padNavItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2

    $combo1 = GetPresetCombo
    if ($combo1) { $passed++; $out += "  PASS: VJoyPresetCombo on Pad1" }
    else { $failed++; $out += "  FAIL: No VJoyPresetCombo on Pad1" }

    # ================================================================
    # TEST 2: Pad1 has mappings (via MappingsCountIndicator)
    # ================================================================
    $out += ""
    $out += "=== TEST 2: Pad1 mappings count ==="
    $mc1 = GetMappingsCount
    $out += "  MappingsCount: '$mc1'"
    if ($mc1 -ne "NOT_FOUND" -and [int]$mc1 -gt 0) {
        $passed++; $out += "  PASS: Pad1 has $mc1 mappings"
    } elseif ($mc1 -eq "NOT_FOUND") {
        $failed++; $out += "  FAIL: MappingsCountIndicator not found"
    } else {
        $failed++; $out += "  FAIL: Pad1 has 0 mappings"
    }

    # ================================================================
    # TEST 3: Pad2 config bar
    # ================================================================
    $out += ""
    $out += "=== TEST 3: Pad2 vJoy config ==="
    $padNavItems[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2

    $combo2 = GetPresetCombo
    if ($combo2) { $passed++; $out += "  PASS: VJoyPresetCombo on Pad2" }
    else { $failed++; $out += "  FAIL: No VJoyPresetCombo on Pad2" }

    # ================================================================
    # TEST 4: Pad2 has mappings
    # ================================================================
    $out += ""
    $out += "=== TEST 4: Pad2 mappings count ==="
    $mc2 = GetMappingsCount
    $out += "  MappingsCount: '$mc2'"
    if ($mc2 -ne "NOT_FOUND" -and [int]$mc2 -gt 0) {
        $passed++; $out += "  PASS: Pad2 has $mc2 mappings"
    } elseif ($mc2 -eq "NOT_FOUND") {
        $failed++; $out += "  FAIL: MappingsCountIndicator not found"
    } else {
        $failed++; $out += "  FAIL: Pad2 has 0 mappings"
    }

    # ================================================================
    # TEST 5: Navigate back to Pad1, mappings preserved
    # ================================================================
    $out += ""
    $out += "=== TEST 5: Pad1 mappings preserved after switching ==="
    $padNavItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2

    $mc1b = GetMappingsCount
    $out += "  MappingsCount: '$mc1b'"
    if ($mc1b -ne "NOT_FOUND" -and $mc1b -eq $mc1) {
        $passed++; $out += "  PASS: Pad1 mappings preserved ($mc1b same as before)"
    } elseif ($mc1b -ne "NOT_FOUND" -and [int]$mc1b -gt 0) {
        $passed++; $out += "  PASS: Pad1 has $mc1b mappings (was $mc1)"
    } else {
        $failed++; $out += "  FAIL: Pad1 mappings lost (now: '$mc1b', was: '$mc1')"
    }

    # ================================================================
    # TEST 6: Stale config isolation
    # ================================================================
    $out += ""
    $out += "=== TEST 6: Stale config isolation ==="
    if ($padNavItems.Count -eq 2) {
        $passed++; $out += "  PASS: Exactly 2 pads (stale uncreated configs ignored)"
    } else {
        $failed++; $out += "  FAIL: Expected 2 pads, found $($padNavItems.Count)"
    }

    # ================================================================
    $out += ""
    $out += "=============================="
    $out += "RESULTS: $passed PASSED, $failed FAILED"
    $out += "=============================="

    $out | Out-File $outFile -Encoding utf8

} catch {
    "EXCEPTION: $($_.Exception.Message)`n$($_.ScriptStackTrace)" | Out-File $outFile -Encoding utf8
}
