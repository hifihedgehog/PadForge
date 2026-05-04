# Wrapper: runs capture_v3_1_0_full.ps1 elevated and captures all output to a known log path.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = "C:\PadForge\capture_v3_1_0_full_log.txt"
"WRAPPER START $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii
try {
    & "$scriptDir\capture_v3_1_0_full.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii -Append
} catch {
    "FATAL: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
"WRAPPER END $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii -Append
