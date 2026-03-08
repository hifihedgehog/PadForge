# Wrapper: runs capture_pads.ps1 and captures all output to log
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = "C:\PadForge\capture_pads_log.txt"
try {
    & "$scriptDir\capture_pads.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii
} catch {
    "FATAL ERROR: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
