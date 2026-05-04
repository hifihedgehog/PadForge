# Wrapper: runs capture_all.ps1 elevated and captures all output to a known log path.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = "C:\PadForge\capture_all_log.txt"
try {
    & "$scriptDir\capture_all.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii
} catch {
    "FATAL ERROR: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
