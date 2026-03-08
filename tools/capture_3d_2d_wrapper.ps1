$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = "C:\PadForge\capture_3d_2d_log.txt"
try {
    & "$scriptDir\capture_3d_2d.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii
} catch {
    "FATAL: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
