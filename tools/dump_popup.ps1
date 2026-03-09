Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\popup_dump.txt"

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
$lines = @()
if (-not $win) { "No window" | Out-File $out -Encoding utf8; exit }

# Click Add Controller
$navCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")
$addNav = $win.FindFirst($tree::Descendants, $navCond)
if ($addNav) {
    try {
        $sel = $addNav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    } catch {}
    Start-Sleep -Seconds 1
    
    # Dump all buttons
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Button)
    $btns = $win.FindAll($tree::Descendants, $btnCond)
    $lines += "Buttons after Add Controller click: $($btns.Count)"
    foreach ($b in $btns) {
        $n = $b.Current.Name
        $id = $b.Current.AutomationId
        $lines += "  Button: Name='$n' AutomationId='$id'"
    }
    
    # Also check for any popup/dialog windows
    $allWin = $ae::RootElement.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $lines += "Top-level windows: $($allWin.Count)"
    foreach ($w in $allWin) {
        $lines += "  Window: Name='$($w.Current.Name)' Class='$($w.Current.ClassName)'"
    }
} else {
    $lines += "Add Controller nav not found"
}

$lines | Out-File $out -Encoding utf8 -Force
