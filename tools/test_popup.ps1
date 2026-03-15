Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\popup_test.txt"
$lines = @()

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $win) { "No window" | Out-File $out -Encoding utf8; exit }

# Click Add Controller
$nameCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")
$matches = $win.FindAll($tree::Descendants, $nameCond)
$navItem = $null
foreach ($m in $matches) {
    if ($m.Current.ControlType -eq $ct::ListItem) { $navItem = $m; break }
}

if ($navItem) {
    $lines += "Found Add Controller ListItem"
    $sel = $navItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    $lines += "Selected"
    Start-Sleep -Seconds 2
    
    # Check ALL top-level windows
    $allWins = $ae::RootElement.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $lines += "Top-level windows: $($allWins.Count)"
    foreach ($w in $allWins) {
        $lines += "  Win: Name='$($w.Current.Name)' Class='$($w.Current.ClassName)' NativeWindowHandle=$($w.Current.NativeWindowHandle)"
    }
    
    # Search for AddVJoyBtn in ALL top-level windows
    $idCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "AddVJoyBtn")
    foreach ($w in $allWins) {
        $btn = $w.FindFirst($tree::Descendants, $idCond)
        if ($btn) {
            $lines += "FOUND AddVJoyBtn in window '$($w.Current.Name)'"
            break
        }
    }
    
    # Also search main window descendants for any button
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Button)
    $buttons = $win.FindAll($tree::Descendants, $btnCond)
    $lines += "Buttons in main window: $($buttons.Count)"
    foreach ($b in $buttons) {
        $n = $b.Current.Name
        $id = $b.Current.AutomationId
        if ($id -match "Add" -or $n -match "vJoy|Xbox|DS4") {
            $lines += "  Match: Name='$n' AutomationId='$id'"
        }
    }
    
    # Look for Popup control type
    $popCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $popups = $ae::RootElement.FindAll($tree::Children, $popCond)
    $lines += "Windows: $($popups.Count)"
    foreach ($p in $popups) {
        $lines += "  Win: '$($p.Current.Name)' Class='$($p.Current.ClassName)'"
        $btn = $p.FindFirst($tree::Descendants, $idCond)
        if ($btn) {
            $lines += "  -> FOUND AddVJoyBtn here!"
        }
    }
} else {
    $lines += "Add Controller not found"
}

$lines | Out-File $out -Encoding utf8 -Force
