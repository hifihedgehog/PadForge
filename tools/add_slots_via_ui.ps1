# Restore PadForge.xml backup, then add 4 more slot types via UI automation.
# The backup is the one prep_xml_for_capture.ps1 took, and it goes back under
# the rules of settings_guard.ps1: only on its marker's word, with the file it
# replaces copied to %TEMP%\PadForge_Capture first. Once it is back, the
# backup and its marker go, as a capture run's do.
param(
    # The owner's answer to a run that stopped over a settings backup lying
    # beside the settings file (settings_guard.ps1). Ask, the default, is that
    # stop. File keeps the settings file and sets the backup aside. Backup puts
    # the backup back over the settings file. A run with nothing to ask
    # refuses File and Backup.
    [ValidateSet('Ask', 'File', 'Backup')]
    [string]$KeepSettingsFrom = 'Ask'
)
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$logFile = Join-Path $pfLogDir "add_slots_log.txt"
"START $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii

$XmlPath = "C:\PadForge\PadForge.xml"
$BakPath = "$XmlPath.bak-capture"
$ExePath = "C:\PadForge\PadForge.exe"

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. Until the backup is back,
# any failure ends the run.
$prevPreference = $ErrorActionPreference
$ErrorActionPreference = 'Stop'
$doubt = Get-BackupDoubt $XmlPath $BakPath 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $XmlPath $BakPath $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { $line | Out-File $logFile -Encoding ascii -Append; Write-Host $line -ForegroundColor Red }
    exit 1
}
"Stopping PadForge..." | Out-File $logFile -Encoding ascii -Append
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ. The run stops with
# neither file touched and starts PadForge again.
$doubt = Get-BackupDoubt $XmlPath $BakPath $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $XmlPath $BakPath $doubt)) { $line | Out-File $logFile -Encoding ascii -Append; Write-Host $line -ForegroundColor Red }
    Start-Process $ExePath
    exit 1
}
if (Test-Path $BakPath) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $BakPath
        Remove-Item $BakPath -Force
        Remove-BackupMarker $BakPath
        "-KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept" | Out-File $logFile -Encoding ascii -Append
    } else {
        # The marker goes as soon as the settings are back, before the backup
        # does, as in a capture run's finally.
        $kept = Save-SettingsCopy $XmlPath
        Copy-Item $BakPath $XmlPath -Force
        Remove-BackupMarker $BakPath
        $keptBak = Save-SettingsCopy $BakPath
        Remove-Item $BakPath -Force
        "Restored backup (the file it replaced is kept as $kept, the backup as $keptBak)" | Out-File $logFile -Encoding ascii -Append
    }
}
$ErrorActionPreference = $prevPreference

# Set by the catch below, for the exit code.
$script:failed = $false
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Windows.Forms

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

    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AddSlotsWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
}
'@

    # Controls are found by AutomationId, which stays put when a name is
    # translated or reworded.
    function Add-SlotById {
        param([string]$Aid, [string]$Label)
        $addCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "Nav_AddController")
        $addBtn = $win.FindFirst($TD, $addCond)
        if (-not $addBtn) { "  !! Add Controller not found" | Out-File $logFile -Encoding ascii -Append; return }
        # The sidebar entry's UIA node is WPF's DataItem wrapper, which offers
        # no SelectionItem or Invoke pattern (ItemAutomationPeer.GetPattern),
        # so the entry is clicked at its center, as tools/a11y_uia_audit.ps1
        # selects pages.
        [AddSlotsWin]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 200
        # A click while another program's window is in front goes to that window.
        if ([AddSlotsWin]::GetForegroundWindow() -ne $proc.MainWindowHandle) { "  !! another window is in front, no click" | Out-File $logFile -Encoding ascii -Append; return $false }
        $r = $addBtn.Current.BoundingRectangle
        [AddSlotsWin]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
        [AddSlotsWin]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        [AddSlotsWin]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 1500

        # The tiles are in Add a Controller, drawn inside PadForge's window
        # (#574). Driven By picks a connected pad by default, so Choose Later
        # is selected first: the slots made here take no device.
        $dialog = $null
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            $dialog = $win.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AddControllerDialog")))
            if ($dialog) { break }
            Start-Sleep -Milliseconds 600
        }
        $btn = $null
        if ($dialog) {
            $later = $dialog.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "AddController_ChooseLater")))
            if ($later) {
                try { $later.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
                Start-Sleep -Milliseconds 200
            }
            $btn = $dialog.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)))
        }
        if (-not $btn) {
            "  !! '$Aid' tile not found in Add a Controller" | Out-File $logFile -Encoding ascii -Append
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
    Add-SlotById "AddDS4Btn"            "PlayStation"
    Add-SlotById "AddRawBtn"            "Extended"
    Add-SlotById "AddKeyboardMouseBtn"  "Keyboard+Mouse"
    Add-SlotById "AddMidiBtn"           "MIDI"

    Start-Sleep -Seconds 5
    "Done adding. Waiting 10s for HM bring-up to complete..." | Out-File $logFile -Encoding ascii -Append
    Start-Sleep -Seconds 10
    "Ready." | Out-File $logFile -Encoding ascii -Append
} catch {
    $script:failed = $true
    "FATAL: $_" | Out-File $logFile -Encoding ascii -Append
}
"END $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii -Append
# A failed run ends with exit code 1, as the catch above kept the error from
# ending the script. The exit code is always written out: a script that simply
# ends hands its caller the exit code of its last native command.
if ($script:failed) { exit 1 }
exit 0
