$logFile = "C:\PadForge\debug_uia2_log.txt"
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

    $padPage = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "PadPageView")))
    if ($padPage) {
        $pr = $padPage.Current.BoundingRectangle
        "PadPage rect: X=$($pr.X) Y=$($pr.Y) W=$($pr.Width) H=$($pr.Height)" | Out-File $logFile -Encoding ascii -Append
        $kids = $padPage.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        "PadPage descendant count: $($kids.Count)" | Out-File $logFile -Encoding ascii -Append
        foreach ($k in $kids) {
            $aid = $k.Current.AutomationId
            $cls = $k.Current.ClassName
            $nm  = $k.Current.Name
            $ctn = $k.Current.ControlType.ProgrammaticName
            if ($aid -or ($cls -match 'Button|Radio|Tab')) {
                $r = $k.Current.BoundingRectangle
                "  AID='$aid' CT='$ctn' Cls='$cls' Name='$nm' rect=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" | Out-File $logFile -Encoding ascii -Append
            }
        }
    } else { "PadPageView not found" | Out-File $logFile -Encoding ascii -Append }
} catch { "FATAL: $_" | Out-File $logFile -Encoding ascii -Append }
"END $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii -Append
