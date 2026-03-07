## vJoy Environment Diagnostic Script
## Run elevated (Admin PowerShell)

$ErrorActionPreference = "Continue"
Write-Host "=== vJoy Environment Diagnostic ==="
Write-Host ""

# 1. Check elevation
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Running elevated: $isElevated"
Write-Host ""

# 2. Check vJoy service
Write-Host "=== vJoy Service ==="
$svc = Get-Service vjoy -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "  Status: $($svc.Status)"
    Write-Host "  StartType: $($svc.StartType)"
} else {
    Write-Host "  Service NOT found"
}
Write-Host ""

# 3. Check vJoy driver in driver store
Write-Host "=== Driver Store ==="
$driverStore = pnputil /enum-drivers 2>&1
$vjoyDrivers = ($driverStore | Out-String) -split '(?=Published Name)' | Where-Object { $_ -match 'vjoy' }
if ($vjoyDrivers) {
    foreach ($d in $vjoyDrivers) {
        Write-Host $d.Trim()
        Write-Host "---"
    }
} else {
    Write-Host "  No vJoy driver found in driver store!"
}
Write-Host ""

# 4. Check vJoy device nodes
Write-Host "=== Device Nodes (pnputil) ==="
$pnpOutput = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$vjoyNodes = $pnpOutput -split '(?=Instance ID)' | Where-Object { $_ -match 'vjoy|VID_1234|PID_BEAD|PID_0FFB' }
if ($vjoyNodes) {
    foreach ($node in $vjoyNodes) {
        Write-Host $node.Trim()
        Write-Host "---"
    }
} else {
    Write-Host "  No vJoy device nodes found in HIDClass"
}
Write-Host ""

# Also check ROOT\HIDCLASS nodes specifically
Write-Host "=== ROOT\HIDCLASS Nodes ==="
$rootNodes = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\*" 2>&1 | Out-String
$rootVjoy = $rootNodes -split '(?=Instance ID)' | Where-Object { $_ -match 'VID_1234|vjoy' }
if ($rootVjoy) {
    foreach ($node in $rootVjoy) {
        Write-Host $node.Trim()
        Write-Host "---"
    }
} else {
    Write-Host "  No ROOT\HIDCLASS vJoy nodes found"
    # Show all ROOT\HIDCLASS nodes for reference
    $allRoot = pnputil /enum-devices /instanceid "ROOT\HIDCLASS\*" 2>&1
    Write-Host "  All ROOT\HIDCLASS:"
    foreach ($line in $allRoot) { Write-Host "    $line" }
}
Write-Host ""

# 5. Check registry
Write-Host "=== Registry ==="
$baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy'
if (Test-Path $baseKey) {
    Write-Host "  vjoy service key: EXISTS"
    $paramsKey = "$baseKey\Parameters"
    if (Test-Path $paramsKey) {
        Write-Host "  Parameters key: EXISTS"
        $subkeys = Get-ChildItem $paramsKey -ErrorAction SilentlyContinue
        Write-Host "  Subkeys:"
        foreach ($sk in $subkeys) {
            $name = $sk.PSChildName
            $descSize = $sk.GetValue("HidReportDescriptorSize")
            Write-Host "    ${name}: DescriptorSize=${descSize}"
        }
        if ($subkeys.Count -eq 0) {
            Write-Host "    (none)"
        }

        # Test write access
        try {
            $testKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
                "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
            if ($testKey) {
                Write-Host "  Write access: YES"
                $testKey.Close()
            } else {
                Write-Host "  Write access: CANNOT OPEN FOR WRITE"
            }
        } catch {
            Write-Host "  Write access: FAILED ($($_.Exception.Message))"
        }
    } else {
        Write-Host "  Parameters key: MISSING"
    }
} else {
    Write-Host "  vjoy service key: MISSING"
}
Write-Host ""

# 6. Check vJoy DLL
Write-Host "=== vJoy DLL ==="
$dllPaths = @(
    "C:\Program Files\vJoy\vJoyInterface.dll",
    "C:\Program Files\vJoy\x64\vJoyInterface.dll"
)
foreach ($p in $dllPaths) {
    if (Test-Path $p) {
        $info = Get-Item $p
        Write-Host "  $p : $($info.Length) bytes, $($info.LastWriteTime)"
    } else {
        Write-Host "  $p : NOT FOUND"
    }
}
Write-Host ""

# 7. Test pnputil disable/enable on existing node
Write-Host "=== Test pnputil Operations ==="
$allDevices = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
# Find ROOT\HIDCLASS nodes with vJoy hardware IDs
$instanceIds = @()
$lines = (pnputil /enum-devices /class HIDClass 2>&1)
$currentId = $null
foreach ($line in $lines) {
    if ($line -match 'Instance ID:\s+(.+)') {
        $currentId = $matches[1].Trim()
    }
    if ($currentId -and $line -match 'Hardware IDs:\s+.*VID_1234') {
        $instanceIds += $currentId
    }
    if ($currentId -and $line -match 'Hardware IDs:\s+.*vjoy') {
        $instanceIds += $currentId
    }
}
$instanceIds = $instanceIds | Select-Object -Unique

if ($instanceIds.Count -gt 0) {
    $testId = $instanceIds[0]
    Write-Host "  Found vJoy node: $testId"

    # Check current status
    $status = pnputil /enum-devices /instanceid "$testId" 2>&1 | Out-String
    Write-Host "  Current status:"
    foreach ($l in ($status -split "`n")) { if ($l.Trim()) { Write-Host "    $l" } }

    # Try disable
    Write-Host "  Attempting disable..."
    $disResult = pnputil /disable-device "$testId" 2>&1 | Out-String
    Write-Host "  Result: $($disResult.Trim())"
    Write-Host "  Exit code: $LASTEXITCODE"

    Start-Sleep -Seconds 1

    # Check status after disable
    $statusAfter = pnputil /enum-devices /instanceid "$testId" 2>&1 | Out-String
    Write-Host "  Status after disable:"
    foreach ($l in ($statusAfter -split "`n")) { if ($l.Trim()) { Write-Host "    $l" } }

    # Try re-enable
    Write-Host "  Attempting re-enable..."
    $enResult = pnputil /enable-device "$testId" 2>&1 | Out-String
    Write-Host "  Result: $($enResult.Trim())"
    Write-Host "  Exit code: $LASTEXITCODE"
} else {
    Write-Host "  No vJoy device nodes found to test"
}
Write-Host ""

# 8. Check PnP entities (joy.cpl visible)
Write-Host "=== PnP vJoy Entities ==="
$vjoyEntities = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*vJoy*' }
Write-Host "  Count: $($vjoyEntities.Count)"
foreach ($e in $vjoyEntities) {
    Write-Host "  $($e.Name) | Status=$($e.Status) | Error=$($e.ConfigManagerErrorCode) | DeviceID=$($e.DeviceID)"
}
Write-Host ""

# 9. Check Windows version (recent updates may affect pnputil)
Write-Host "=== Windows Version ==="
$os = Get-CimInstance Win32_OperatingSystem
Write-Host "  $($os.Caption) $($os.Version) Build $($os.BuildNumber)"
$lastUpdate = Get-HotFix -ErrorAction SilentlyContinue | Sort-Object InstalledOn -Descending | Select-Object -First 3
if ($lastUpdate) {
    Write-Host "  Last 3 updates:"
    foreach ($u in $lastUpdate) {
        Write-Host "    $($u.HotFixID) - $($u.InstalledOn) - $($u.Description)"
    }
}
Write-Host ""

Write-Host "=== Diagnostic Complete ==="
