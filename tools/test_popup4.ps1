# Dump all nav items and buttons in PadForge to find Add Controller
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

$root = $ae::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $root.FindFirst($tree::Children, $cond)

if (-not $mainWin) { Write-Output "ERROR: PadForge not found"; exit 1 }

# Find all elements with "Add" or "Controller" in name or AutomationId
$allElems = $mainWin.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
Write-Output "Total elements: $($allElems.Count)"
Write-Output ""
Write-Output "Elements with 'Add' or 'Controller' or 'Pad' in name/id:"
foreach ($e in $allElems) {
    $name = $e.Current.Name
    $aid = $e.Current.AutomationId
    $ctype = $e.Current.ControlType.ProgrammaticName
    if ($name -match "Add|Controller|New" -or $aid -match "Add|Controller|New") {
        $r = $e.Current.BoundingRectangle
        Write-Output "  Type=$ctype Name='$name' AutomationId='$aid' Rect=($([int]$r.X),$([int]$r.Y),$([int]$r.Width),$([int]$r.Height))"
    }
}

Write-Output ""
Write-Output "All ListItem elements (nav items):"
$listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
$listItems = $mainWin.FindAll($tree::Descendants, $listCond)
foreach ($li in $listItems) {
    $r = $li.Current.BoundingRectangle
    Write-Output "  Name='$($li.Current.Name)' AutomationId='$($li.Current.AutomationId)' Rect=($([int]$r.X),$([int]$r.Y),$([int]$r.Width),$([int]$r.Height))"
}
