$logFile = "C:\PadForge\debug_toggle_log.txt"
"START $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $proc = Get-Process PadForge | Select-Object -First 1
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
    "Window rect: $($win.Current.BoundingRectangle.X),$($win.Current.BoundingRectangle.Y) $($win.Current.BoundingRectangle.Width)x$($win.Current.BoundingRectangle.Height)" | Out-File $logFile -Encoding ascii -Append

    # Find by AID anywhere in window tree
    foreach ($aid in @("ViewModeToggle", "ControllerModelHost", "PadPageView", "HMaestroProfileCombo")) {
        $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid)))
        if ($el) {
            $r = $el.Current.BoundingRectangle
            "AID '$aid' FOUND: rect=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height) Cls=$($el.Current.ClassName) CT=$($el.Current.ControlType.ProgrammaticName)" | Out-File $logFile -Encoding ascii -Append
        } else { "AID '$aid' NOT FOUND" | Out-File $logFile -Encoding ascii -Append }
    }

    # Search by name (tooltip) for Switch to 2D
    foreach ($n in @("Switch to 2D", "Switch to 3D")) {
        $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $n)))
        if ($el) {
            $r = $el.Current.BoundingRectangle
            "Name '$n' FOUND: rect=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height) AID=$($el.Current.AutomationId) CT=$($el.Current.ControlType.ProgrammaticName)" | Out-File $logFile -Encoding ascii -Append
        } else { "Name '$n' NOT FOUND" | Out-File $logFile -Encoding ascii -Append }
    }

    # Enumerate all Buttons in entire window
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    "Total Buttons in window: $($btns.Count)" | Out-File $logFile -Encoding ascii -Append
    foreach ($b in $btns) {
        try {
            $r = $b.Current.BoundingRectangle
            if ([double]::IsInfinity($r.X) -or [double]::IsNaN($r.X)) { continue }
            $aid = $b.Current.AutomationId
            $nm = $b.Current.Name
            "  Btn AID='$aid' Name='$nm' rect=$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" | Out-File $logFile -Encoding ascii -Append
        } catch {}
    }
} catch { "FATAL: $_" | Out-File $logFile -Encoding ascii -Append }
"END $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii -Append
