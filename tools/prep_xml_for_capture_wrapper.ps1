# Wrapper for prep_xml_for_capture.ps1 with logging
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$logFile = Join-Path $pfLogDir "prep_xml_log.txt"
"START $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii
try {
    & "$scriptDir\prep_xml_for_capture.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii -Append
} catch {
    "FATAL: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
}
"END $(Get-Date -Format 'HH:mm:ss')" | Out-File -FilePath $logFile -Encoding ascii -Append
