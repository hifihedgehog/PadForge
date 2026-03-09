# Stop PadForge
Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Fix PadForge.xml - ensure only slot 0 is created
[xml]$x = Get-Content 'C:\PadForge\PadForge.xml'
$created = $x.SelectNodes('//SlotCreated/Created')
for ($i = 0; $i -lt $created.Count; $i++) {
    $created[$i].InnerText = if ($i -eq 0) { 'true' } else { 'false' }
}
$x.Save('C:\PadForge\PadForge.xml')
Write-Host 'Fixed XML - only slot 0 created'

# Deploy
Copy-Item 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\PadForge.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\PadForge.exe' 'C:\PadForge\PadForge.exe' -Force
Write-Host 'Deployed PadForge.exe'

# Start PadForge
Start-Process 'C:\PadForge\PadForge.exe'
Write-Host 'Starting PadForge...'
Start-Sleep -Seconds 8
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
Write-Host "PID=$($proc.Id) HWND=$($proc.MainWindowHandle)"
