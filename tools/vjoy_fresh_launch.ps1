# Kill PadForge, remove vJoy nodes, relaunch fresh
param([switch]$SkipLaunch)

Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Remove existing vJoy ROOT\HIDCLASS nodes
$pnp = pnputil /enum-devices /class HIDClass 2>&1
$currentId = $null
foreach ($line in ($pnp -split "`n")) {
    $t = $line.Trim()
    if ($t -match 'Instance ID\s*:\s*(ROOT\\HIDCLASS\\\d+)') { $currentId = $matches[1].Trim() }
    elseif ($currentId -and $t -match 'vJoy') {
        Write-Host "Removing $currentId..."
        pnputil /remove-device $currentId /subtree 2>&1 | Out-Null
        $currentId = $null
    }
    elseif (-not $t) { $currentId = $null }
}

Start-Sleep -Seconds 1
pnputil /scan-devices 2>&1 | Out-Null
Start-Sleep -Seconds 1
Write-Host "vJoy nodes removed"

if (-not $SkipLaunch) {
    Start-Process 'C:\PadForge\PadForge.exe'
    Write-Host "PadForge launched"
}
