# Debug: dump UIA tree of PadForge to see what's actually there.
$logFile = "C:\PadForge\debug_uia_log.txt"
"START $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process PadForge | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
"Window: $($win.Current.Name)" | Out-File $logFile -Encoding ascii -Append

# Find all elements with AutomationId set
$descendants = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
"Total descendants: $($descendants.Count)" | Out-File $logFile -Encoding ascii -Append

$aidCount = 0
foreach ($d in $descendants) {
    $aid = $d.Current.AutomationId
    if ($aid) {
        "  AID='$aid' Name='$($d.Current.Name)' Class='$($d.Current.ClassName)'" | Out-File $logFile -Encoding ascii -Append
        $aidCount++
    }
}
"AIDs found: $aidCount" | Out-File $logFile -Encoding ascii -Append
} catch { "FATAL: $_" | Out-File $logFile -Encoding ascii -Append }
"END $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii -Append
