# Wrapper to run vjoy_ui_test.ps1 elevated and wait for completion
$scriptPath = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_ui_test.ps1'
$logFile = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_ui_test_log.txt'
$doneFile = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_ui_test_done.txt'

# Clean up markers
Remove-Item $logFile -Force -ErrorAction SilentlyContinue
Remove-Item $doneFile -Force -ErrorAction SilentlyContinue

# The actual test script writes to $logFile. We add a done marker at end.
$wrapperScript = @"
try {
    & '$scriptPath' -Action test
} catch {
    `$_ | Out-File '$logFile' -Append -Encoding utf8
}
'done' | Out-File '$doneFile' -Encoding utf8 -Force
"@

$tmpScript = "$env:TEMP\vjoy_ui_wrapper.ps1"
$wrapperScript | Out-File $tmpScript -Encoding utf8 -Force

Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tmpScript`"" -WindowStyle Hidden

# Wait for done marker (timeout 5 min)
$timeout = 300
for ($i = 0; $i -lt $timeout; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $doneFile) {
        Write-Host "Test completed after $i seconds"
        if (Test-Path $logFile) {
            Get-Content $logFile
        } else {
            Write-Host "WARNING: Log file not created"
        }
        Remove-Item $doneFile -Force -ErrorAction SilentlyContinue
        Remove-Item $tmpScript -Force -ErrorAction SilentlyContinue
        exit 0
    }
    # Print progress every 30s
    if ($i % 30 -eq 0 -and $i -gt 0) {
        Write-Host "Waiting... ($i s)"
        if (Test-Path $logFile) {
            Write-Host "--- partial log ---"
            Get-Content $logFile
            Write-Host "--- end partial ---"
        }
    }
}
Write-Host "TIMEOUT after $timeout seconds"
if (Test-Path $logFile) { Get-Content $logFile }
