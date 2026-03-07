# Quick cleanup: remove stale vJoy device node
# Must run elevated
$nodes = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$matches = [regex]::Matches($nodes, 'Instance ID:\s+(ROOT\\HIDCLASS\\\d+)')
foreach ($m in $matches) {
    $id = $m.Groups[1].Value
    Write-Host "Removing $id..."
    pnputil /remove-device $id /subtree
}
Start-Sleep 2
pnputil /scan-devices
Write-Host "Done. Checking VJOYRAWPDO..."
$pdos = Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Write-Host "VJOYRAWPDO count: $($pdos.Count)"
Start-Sleep 3
