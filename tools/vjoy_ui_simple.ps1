# Navigate to vJoy page and find config controls
$logFile = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_ui_test_log.txt'
$R = @()
$R += "=== UI Navigation Test $(Get-Date) ==="

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pfCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'PadForge')),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window)))
    $pfWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pfCond)
    if (-not $pfWin) { $R += "FATAL: PadForge not found"; $R | Out-File $logFile -Encoding utf8 -Force; exit 1 }

    # Find NavView
    $navCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavView')
    $navView = $pfWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
    $R += "NavView found: $($navView -ne $null)"

    # Explore NavView children to find sidebar items
    if ($navView) {
        $items = $navView.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $R += "NavView descendants: $($items.Count)"

        # Look for ListItems or NavigationViewItems that might be sidebar entries
        foreach ($item in $items) {
            $ct = $item.Current.ControlType.ProgrammaticName
            $name = $item.Current.Name
            $aid = $item.Current.AutomationId
            if ($name -or $aid) {
                if ($ct -match 'ListItem|MenuItem|Button|Tab' -or $name -match 'vJoy|Controller|Dashboard') {
                    $R += "  [$ct] Name='$name' AID='$aid'"
                }
            }
        }

        # Also find all ListItems specifically
        $listItemCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $listItems = $navView.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCond)
        $R += ""
        $R += "All ListItems in NavView ($($listItems.Count)):"
        foreach ($li in $listItems) {
            $R += "  Name='$($li.Current.Name)' AID='$($li.Current.AutomationId)'"
        }
    }
} catch {
    $R += "ERROR: $($_.Exception.Message)"
    $R += $_.ScriptStackTrace
}

$R | Out-File $logFile -Encoding utf8 -Force
$R | ForEach-Object { Write-Host $_ }
