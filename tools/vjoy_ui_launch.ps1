# Launch vjoy_ui_test.ps1 elevated with done marker
$logFile = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_ui_test_log.txt'
$doneFile = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_ui_test_done.txt'
$wrapperScript = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_ui_wrapper_temp.ps1'

Remove-Item $logFile -Force -ErrorAction SilentlyContinue
Remove-Item $doneFile -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\PadForge\padpage_debug.log' -Force -ErrorAction SilentlyContinue

# Write wrapper
$content = @"
try {
    & 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_ui_test.ps1' -Action test
} catch {
    `$_ | Out-File '$logFile' -Append -Encoding utf8
    `$_.ScriptStackTrace | Out-File '$logFile' -Append -Encoding utf8
}
'done' | Out-File '$doneFile' -Encoding utf8 -Force
"@
$content | Out-File $wrapperScript -Encoding utf8 -Force

# Launch elevated
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$wrapperScript`"" -WindowStyle Hidden

# Wait for done marker
$timeout = 300
for ($i = 0; $i -lt $timeout; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $doneFile) {
        Write-Host "Completed after $i seconds"
        if (Test-Path $logFile) { Get-Content $logFile }
        if (Test-Path 'C:\PadForge\padpage_debug.log') {
            Write-Host ""
            Write-Host "=== PadPage Debug Log ==="
            Get-Content 'C:\PadForge\padpage_debug.log'
        }
        Remove-Item $doneFile -Force -ErrorAction SilentlyContinue
        Remove-Item $wrapperScript -Force -ErrorAction SilentlyContinue
        exit 0
    }
    if ($i % 30 -eq 0 -and $i -gt 0) {
        Write-Host "Waiting... ($i s)"
        if (Test-Path $logFile) {
            Write-Host "--- partial ---"
            Get-Content $logFile
        }
    }
}
Write-Host "TIMEOUT"
if (Test-Path $logFile) { Get-Content $logFile }
