# Diagnostic: what's visible after clicking MappingsTab
$outFile = "c:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\mappings_diag_log.txt"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $ae  = [System.Windows.Automation.AutomationElement]
    $ct  = [System.Windows.Automation.ControlType]
    $tree = [System.Windows.Automation.TreeScope]
    $true_cond = [System.Windows.Automation.Condition]::TrueCondition

    $out = @()

    $root = $ae::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $mainWin = $root.FindFirst($tree::Children, $cond)

    # Navigate to Pad1
    $listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $navItems = $mainWin.FindAll($tree::Descendants, $listCond)
    foreach ($nav in $navItems) {
        if ($nav.Current.Name -eq "Pad1") {
            $selPat = $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat.Select()
            Start-Sleep -Seconds 1
            break
        }
    }
    $out += "Navigated to Pad1"

    # Find MappingsTab and invoke it
    $mtCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "MappingsTab")
    $mt = $mainWin.FindFirst($tree::Descendants, $mtCond)
    $out += "MappingsTab found: $($mt -ne $null)"

    if ($mt) {
        $out += "MappingsTab ControlType: $($mt.Current.ControlType.ProgrammaticName)"
        $out += "MappingsTab IsEnabled: $($mt.Current.IsEnabled)"

        # Check supported patterns
        $patterns = $mt.GetSupportedPatterns()
        $out += "Supported patterns: $($patterns | ForEach-Object { $_.ProgrammaticName })"

        # Try invoke
        try {
            $inv = $mt.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
            $out += "InvokePattern succeeded"
        } catch {
            $out += "InvokePattern failed: $($_.Exception.Message)"
            # Try TogglePattern (RadioButton)
            try {
                $tog = $mt.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $tog.Toggle()
                $out += "TogglePattern succeeded"
            } catch {
                $out += "TogglePattern failed too"
            }
        }
        Start-Sleep -Milliseconds 800
    }

    # Dump all descendants to see what's on screen now
    $out += ""
    $out += "=== All descendants after clicking Mappings ==="
    $allElems = $mainWin.FindAll($tree::Descendants, $true_cond)
    $out += "Total: $($allElems.Count)"

    # Look for buttons, datagrid, dataitem, text
    foreach ($e in $allElems) {
        $etype = $e.Current.ControlType.ProgrammaticName
        $ename = $e.Current.Name
        $eaid = $e.Current.AutomationId
        # Show interesting elements
        if ($etype -match "DataGrid|DataItem|Button|Table|Header" -or $ename -match "Clear|Copy|Paste|Map|mapping|Mapping" -or $eaid -match "Map|Mapping|DataGrid") {
            $out += "  $etype Name='$ename' AutomationId='$eaid'"
        }
    }

    $out += ""
    $out += "=== DataItem search ==="
    $dgCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)
    $rows = $mainWin.FindAll($tree::Descendants, $dgCond)
    $out += "DataItem count: $($rows.Count)"

    $out += ""
    $out += "=== Table search ==="
    $tblCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Table)
    $tables = $mainWin.FindAll($tree::Descendants, $tblCond)
    $out += "Table count: $($tables.Count)"
    foreach ($tbl in $tables) {
        $out += "  Table: Name='$($tbl.Current.Name)' AutomationId='$($tbl.Current.AutomationId)'"
        $tblChildren = $tbl.FindAll($tree::Children, $true_cond)
        $out += "  Children: $($tblChildren.Count)"
    }

    $out += ""
    $out += "=== DataGrid search ==="
    $dgCond2 = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataGrid)
    $dgs = $mainWin.FindAll($tree::Descendants, $dgCond2)
    $out += "DataGrid count: $($dgs.Count)"

    $out | Out-File $outFile -Encoding utf8

} catch {
    "EXCEPTION: $($_.Exception.Message)`n$($_.ScriptStackTrace)" | Out-File $outFile -Encoding utf8
}
