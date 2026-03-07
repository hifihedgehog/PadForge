# Dump all nav items - writes output to file (for elevated execution)
$outFile = "c:\Users\sonic\GitHub\PadForge\tools\popup_test5.txt"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

$out = @()
$out += "IsAdmin: $(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

$root = $ae::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $root.FindFirst($tree::Children, $cond)

if (-not $mainWin) {
    $out += "ERROR: PadForge not found"
    $out | Out-File $outFile -Encoding utf8
    exit 1
}

$out += "Found PadForge: Class=$($mainWin.Current.ClassName)"

$allElems = $mainWin.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$out += "Total descendants: $($allElems.Count)"
$out += ""

foreach ($e in $allElems) {
    $name = $e.Current.Name
    $aid = $e.Current.AutomationId
    $ctype = $e.Current.ControlType.ProgrammaticName
    if ($name -match "Add|Controller|Pad|Dashboard|Device|Setting" -or $aid -match "Add|Controller|Pad|Dashboard|Device|Setting") {
        $out += "  Type=$ctype Name='$name' AutomationId='$aid'"
    }
}

$out += ""
$out += "First 20 elements:"
$count = 0
foreach ($e in $allElems) {
    if ($count -ge 20) { break }
    $out += "  [$count] Type=$($e.Current.ControlType.ProgrammaticName) Name='$($e.Current.Name)' AutomationId='$($e.Current.AutomationId)'"
    $count++
}

$out | Out-File $outFile -Encoding utf8
