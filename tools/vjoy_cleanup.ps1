# Quick cleanup: remove stale vJoy device node
# Must run elevated
#
# Two fixes. The instance-ID match keyed off the English "Instance ID:" label,
# which pnputil localizes, so on a non-English Windows nothing matched, nothing
# was removed, and the script still printed "Done." Matching the
# ROOT\HIDCLASS\NNNN path itself is locale-invariant.
#
# The removal also had no vJoy filter, so it removed every ROOT\HIDCLASS node
# it found while the file's own first line says "vJoy device node". The two
# siblings that walk the same namespace both filter by name
# (add_vjoy_test.ps1 and remove_recreate_test.ps1), and this now matches them.
$nodes = pnputil /enum-devices /class HIDClass 2>&1 | Out-String

$targets = @()
$currentId = $null
foreach ($line in $nodes -split "`n") {
    $t = $line.Trim()
    if ($t -match '(ROOT\\HIDCLASS\\\S+)') { $currentId = $Matches[1] }
    elseif ($currentId -and $t -match 'vJoy') { $targets += $currentId; $currentId = $null }
    elseif ([string]::IsNullOrWhiteSpace($t)) { $currentId = $null }
}

if (-not $targets) {
    Write-Host "No vJoy ROOT\HIDCLASS nodes found."
} else {
    foreach ($id in $targets) {
        Write-Host "Removing $id..."
        pnputil /remove-device $id /subtree
    }
    Start-Sleep 2
    pnputil /scan-devices
}

Write-Host "Done. Checking VJOYRAWPDO..."
$pdos = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' })
Write-Host "VJOYRAWPDO count: $($pdos.Count)"
Start-Sleep 3
