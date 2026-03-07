# Check vJoy device state: registry keys, device nodes, and PnP entities
Write-Host "=== vJoy Registry Descriptors ==="
$baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
if (Test-Path $baseKey) {
    $subkeys = Get-ChildItem $baseKey -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^Device\d+$' }
    Write-Host "DeviceNN keys: $($subkeys.Count)"
    foreach ($k in $subkeys) {
        $desc = $k.GetValue('HidReportDescriptor')
        $size = if ($desc) { $desc.Length } else { 0 }
        Write-Host "  $($k.PSChildName) - descriptor $size bytes"
    }
} else {
    Write-Host "vjoy Parameters key not found"
}

Write-Host ""
Write-Host "=== vJoy Device Nodes (pnputil) ==="
$pnpOutput = & pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$lines = $pnpOutput -split "`n"
$inVjoy = $false
foreach ($line in $lines) {
    if ($line -match 'Instance ID') { $currentId = $line }
    if ($line -match 'vJoy' -or $line -match 'vjoy') {
        $inVjoy = $true
        Write-Host $currentId.Trim()
        Write-Host $line.Trim()
    }
    if ($inVjoy -and $line -match 'Status') {
        Write-Host $line.Trim()
        $inVjoy = $false
        Write-Host ""
    }
}

Write-Host "=== vJoy PnP Entities ==="
Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -like '*vJoy*' } | ForEach-Object {
    Write-Host "  $($_.Name) | Status=$($_.Status) | Error=$($_.ConfigManagerErrorCode)"
}

Write-Host ""
Write-Host "Done."
