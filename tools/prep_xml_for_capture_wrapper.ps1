# Wrapper for prep_xml_for_capture.ps1 with logging
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile = "C:\PadForge\prep_xml_log.txt"
"START $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii
try {
    & "$scriptDir\prep_xml_for_capture.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii -Append
} catch {
    "FATAL: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
}
"END $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii -Append
