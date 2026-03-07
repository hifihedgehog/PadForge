# Simplified vJoy verify with error handling
$outFile = "c:\Users\sonic\GitHub\PadForge\tools\vjoy_verify_log.txt"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $ae  = [System.Windows.Automation.AutomationElement]
    $ct  = [System.Windows.Automation.ControlType]
    $tree = [System.Windows.Automation.TreeScope]
    $true_cond = [System.Windows.Automation.Condition]::TrueCondition

    $out = @()
    $out += "IsAdmin: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

    $root = $ae::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $mainWin = $root.FindFirst($tree::Children, $cond)
    if (-not $mainWin) { $out += "ERROR: PadForge not found"; $out | Out-File $outFile -Encoding utf8; exit 1 }
    $out += "Found PadForge"

    # Get nav items
    $listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $navItems = $mainWin.FindAll($tree::Descendants, $listCond)
    $out += "Nav items: $($navItems.Count)"

    # Find pad nav items (not Dashboard/Devices/Settings/Add Controller)
    $padNavItems = @()
    foreach ($nav in $navItems) {
        $name = $nav.Current.Name
        $out += "  Nav: '$name'"
        if ($name -match "^Pad\d+$") {
            $padNavItems += $nav
        }
    }
    $out += "Pad items: $($padNavItems.Count)"

    if ($padNavItems.Count -eq 0) {
        $out += "No pad nav items. Cannot test."
        $out | Out-File $outFile -Encoding utf8
        exit 1
    }

    # Navigate to first pad
    $firstPad = $padNavItems[0]
    $out += ""
    $out += "=== Navigating to '$($firstPad.Current.Name)' ==="
    $selPat = $firstPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPat.Select()
    Start-Sleep -Seconds 1

    # Check for page
    $padPageCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "PadPageView")
    $padPage = $mainWin.FindFirst($tree::Descendants, $padPageCond)
    $out += "PadPageView found: $($padPage -ne $null)"

    # Check VJoyPresetCombo
    $presetCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyPresetCombo")
    $presetCombo = $mainWin.FindFirst($tree::Descendants, $presetCond)
    $out += "VJoyPresetCombo found: $($presetCombo -ne $null)"

    # Check for tabs
    $tabCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::TabItem)
    $tabs = $mainWin.FindAll($tree::Descendants, $tabCond)
    $out += "Tabs: $($tabs.Count)"
    foreach ($tab in $tabs) { $out += "  Tab: '$($tab.Current.Name)'" }

    # Click Mappings tab if found
    $mappingsTab = $null
    foreach ($tab in $tabs) { if ($tab.Current.Name -eq "Mappings") { $mappingsTab = $tab } }

    if ($mappingsTab) {
        $out += ""
        $out += "=== Clicking Mappings tab ==="
        $selPat2 = $mappingsTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat2.Select()
        Start-Sleep -Milliseconds 500

        # Count DataItems
        $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
        $rows = $mainWin.FindAll($tree::Descendants, $dgCond)
        $out += "Mapping DataItem rows: $($rows.Count)"

        if ($rows.Count -gt 0) {
            $out += "PASS: Mappings has content"
            # Show first 3 rows
            $showCount = [Math]::Min(3, $rows.Count)
            for ($i = 0; $i -lt $showCount; $i++) {
                $rowCells = $rows[$i].FindAll($tree::Children, $true_cond)
                $cellVals = @()
                foreach ($cell in $rowCells) { $cellVals += $cell.Current.Name }
                $joined = $cellVals -join " | "
                $out += "  Row ${i} - $joined"
            }
        } else {
            $out += "FAIL: Mappings tab is EMPTY"
        }
    }

    # If 2+ pads, check second and verify first doesn't clear
    if ($padNavItems.Count -ge 2) {
        $secondPad = $padNavItems[1]
        $out += ""
        $out += "=== Navigating to '$($secondPad.Current.Name)' ==="
        $selPat3 = $secondPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat3.Select()
        Start-Sleep -Seconds 1

        # Check Mappings on second pad
        $tabs2 = $mainWin.FindAll($tree::Descendants, $tabCond)
        $mt2 = $null
        foreach ($t in $tabs2) { if ($t.Current.Name -eq "Mappings") { $mt2 = $t } }
        if ($mt2) {
            $selPat4 = $mt2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat4.Select()
            Start-Sleep -Milliseconds 500
            $rows2 = $mainWin.FindAll($tree::Descendants, $dgCond)
            $out += "Pad 2 Mapping rows: $($rows2.Count)"
            if ($rows2.Count -gt 0) { $out += "PASS: Pad 2 Mappings has content" }
            else { $out += "FAIL: Pad 2 Mappings is EMPTY" }
        }

        # Go back to first pad
        $out += ""
        $out += "=== Navigating back to '$($firstPad.Current.Name)' ==="
        $selPat5 = $firstPad.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat5.Select()
        Start-Sleep -Seconds 1

        $tabs3 = $mainWin.FindAll($tree::Descendants, $tabCond)
        $mt3 = $null
        foreach ($t in $tabs3) { if ($t.Current.Name -eq "Mappings") { $mt3 = $t } }
        if ($mt3) {
            $selPat6 = $mt3.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat6.Select()
            Start-Sleep -Milliseconds 500
            $rows3 = $mainWin.FindAll($tree::Descendants, $dgCond)
            $out += "Pad 1 Mapping rows after switch: $($rows3.Count)"
            if ($rows3.Count -gt 0) { $out += "PASS: Pad 1 Mappings preserved after switching" }
            else { $out += "FAIL: Pad 1 Mappings CLEARED after switching" }
        }
    }

    $out | Out-File $outFile -Encoding utf8

} catch {
    $errMsg = "EXCEPTION: $($_.Exception.Message)`n$($_.ScriptStackTrace)"
    $errMsg | Out-File $outFile -Encoding utf8
}
