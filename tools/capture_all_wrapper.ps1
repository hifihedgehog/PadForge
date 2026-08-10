# Wrapper: runs capture_all.ps1 elevated and captures all output to a known log path.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$logFile = Join-Path $pfLogDir "capture_all_log.txt"
try {
    & "$scriptDir\capture_all.ps1" *>&1 | Out-File -FilePath $logFile -Encoding ascii
} catch {
    "FATAL ERROR: $($_.Exception.Message)" | Out-File -FilePath $logFile -Encoding ascii -Append
    $_.ScriptStackTrace | Out-File -FilePath $logFile -Encoding ascii -Append
}
