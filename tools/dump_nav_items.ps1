Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\nav_dump.txt"
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
$lines = @()
if ($win) {
    $lines += "Found PadForge window"
    $selCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $items = $win.FindAll($tree::Descendants, $selCond)
    $lines += "ListItems: $($items.Count)"
    foreach ($item in $items) {
        $n = $item.Current.Name
        $id = $item.Current.AutomationId
        $lines += "  Name='$n' AutomationId='$id'"
    }
} else {
    $lines += "Window not found"
}
$lines | Out-File $out -Encoding utf8 -Force
