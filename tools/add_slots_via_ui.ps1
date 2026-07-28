# Restore PadForge.xml backup, then add 4 more slot types via UI automation.
$logFile = "C:\PadForge\add_slots_log.txt"
"START $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms

    $XmlPath = "C:\PadForge\PadForge.xml"
    $BakPath = "$XmlPath.bak-capture"
    $ExePath = "C:\PadForge\PadForge.exe"

    "Stopping PadForge..." | Out-File $logFile -Encoding ascii -Append
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    if (Test-Path $BakPath) {
        Copy-Item $BakPath $XmlPath -Force
        "Restored backup" | Out-File $logFile -Encoding ascii -Append
    }

    Start-Process $ExePath
    Start-Sleep -Seconds 12
    "Launched PadForge" | Out-File $logFile -Encoding ascii -Append

    $proc = Get-Process PadForge | Select-Object -First 1
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
    if (-not $win) { "!! Window not found" | Out-File $logFile -Encoding ascii -Append; exit 1 }

    $TD = [System.Windows.Automation.TreeScope]::Descendants

    function Click-Anything {
        param($El)
        try { $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } catch {}
        try { $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true } catch {}
        # Coordinate click as last resort
        try {
            $r = $El.Current.BoundingRectangle
            Add-Type -AssemblyName System.Windows.Forms
            # Rect.Empty reports X as +Infinity and [int] on it throws.
            if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return $false }
            [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width/2), [int]($r.Y + $r.Height/2))
            Start-Sleep -Milliseconds 100
            # Use mouse_event via Win32 if possible (we already have W32 type below but not in this scope)
            return $true
        } catch { return $false }
    }
    function Add-SlotByName {
        param([string]$ByName, [string]$Label)
        $addCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "Add Controller")
        $addBtn = $win.FindFirst($TD, $addCond)
        if (-not $addBtn) { "  !! Add Controller not found" | Out-File $logFile -Encoding ascii -Append; return }
        # Always go through SelectionItem.Select for the sidebar nav — the
        # Invoke pattern doesn't fire the click handler that opens the popup.
        try {
            $addBtn.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        } catch {
            try { $addBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {}
        }
        Start-Sleep -Milliseconds 1500

        # Find the type button by accessibility Name. Search the desktop
        # root because WPF Popups can be hosted in a separate top-level
        # window outside the main app's UIA subtree.
        $btn = $null
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $ByName)
            $btn = $root.FindFirst($TD, $cond)
            if ($btn) { break }
            Start-Sleep -Milliseconds 600
        }
        if (-not $btn) {
            "  !! '$ByName' button not found in popup" | Out-File $logFile -Encoding ascii -Append
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
            Start-Sleep -Milliseconds 400
            return
        }
        try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {}
        Start-Sleep -Milliseconds 2500
        "  Created $Label" | Out-File $logFile -Encoding ascii -Append
    }

    # We have an Xbox slot already. Add the 4 missing types.
    # Type-group reorder will sort them: existing Xbox stays at 0,
    # then PlayStation, Extended, KBM, MIDI in that order.
    Add-SlotByName "PlayStation"     "PlayStation"
    Add-SlotByName "Extended"        "Extended"
    Add-SlotByName "Keyboard+Mouse"  "Keyboard+Mouse"
    Add-SlotByName "MIDI"            "MIDI"

    Start-Sleep -Seconds 5
    "Done adding. Waiting 10s for HM bring-up to complete..." | Out-File $logFile -Encoding ascii -Append
    Start-Sleep -Seconds 10
    "Ready." | Out-File $logFile -Encoding ascii -Append
} catch {
    "FATAL: $_" | Out-File $logFile -Encoding ascii -Append
}
"END $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii -Append
