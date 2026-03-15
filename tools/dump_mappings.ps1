# Dump the Mappings tab content for Pad1 via UI Automation
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\mappings_dump.txt"

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
$lines = @()
if (-not $win) { "Window not found" | Out-File $out -Encoding utf8; exit }
$lines += "Found PadForge"

# Navigate to Pad1
$navCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Pad1")
$pad1 = $win.FindFirst($tree::Descendants, $navCond)
if ($pad1) {
    $sel = $pad1.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Start-Sleep -Seconds 2
    $lines += "Navigated to Pad1"
    
    # Click Mappings tab (tab index 1)
    $radioButtons = $win.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::RadioButton)))
    $lines += "RadioButtons: $($radioButtons.Count)"
    foreach ($rb in $radioButtons) {
        $n = $rb.Current.Name
        $id = $rb.Current.AutomationId
        $lines += "  RadioButton: Name='$n' AutomationId='$id'"
    }
    
    # Dump all text elements on the page
    $texts = $win.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Text)))
    $lines += ""
    $lines += "Text elements (first 30):"
    $count = 0
    foreach ($t in $texts) {
        $n = $t.Current.Name
        if ($n -and $n.Length -gt 0) {
            $lines += "  Text: '$n'"
            $count++
            if ($count -ge 30) { break }
        }
    }
    
    # Look for DataGrid or List items that might contain mappings
    $dataItems = $win.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem)))
    $lines += ""
    $lines += "DataItems: $($dataItems.Count)"
    $count = 0
    foreach ($di in $dataItems) {
        $n = $di.Current.Name
        $id = $di.Current.AutomationId
        $lines += "  DataItem: Name='$n' AutomationId='$id'"
        $count++
        if ($count -ge 10) { break }
    }
} else {
    $lines += "Pad1 not found"
}

$lines | Out-File $out -Encoding utf8 -Force
