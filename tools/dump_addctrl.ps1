Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\addctrl_dump.txt"
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
$lines = @()
if (-not $win) { "No window" | Out-File $out -Encoding utf8; exit }
$navCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")
$items = $win.FindAll($tree::Descendants, $navCond)
$lines += "Found $($items.Count) 'Add Controller' elements"
foreach ($item in $items) {
    $lines += "  Type=$($item.Current.ControlType.ProgrammaticName) AutoId=$($item.Current.AutomationId) Class=$($item.Current.ClassName)"
    $patterns = $item.GetSupportedPatterns()
    foreach ($p in $patterns) {
        $lines += "    Pattern: $($p.ProgrammaticName)"
    }
}
$lines | Out-File $out -Encoding utf8 -Force
