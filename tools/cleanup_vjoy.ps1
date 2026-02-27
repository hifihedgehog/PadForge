#Requires -RunAsAdministrator
# Fully removes a legacy vJoy Inno Setup installation.
# Run as Administrator: right-click -> Run with PowerShell (Admin)

$ErrorActionPreference = 'SilentlyContinue'
Write-Host "=== vJoy Legacy Cleanup ===" -ForegroundColor Cyan

# 1. Remove driver from driver store (pnputil)
Write-Host "`n[1/8] Removing driver store entries..." -ForegroundColor Yellow
$pnpOutput = pnputil /enum-drivers 2>&1 | Out-String
$currentOem = $null
$isVJoy = $false
foreach ($line in $pnpOutput -split "`n") {
    $trimmed = $line.Trim()
    if ($trimmed -match '^Published\s*[Nn]ame\s*:\s*(oem\d+\.inf)') {
        if ($isVJoy -and $currentOem) {
            Write-Host "  Deleting $currentOem"
            pnputil /delete-driver $currentOem /uninstall /force 2>&1 | Out-Null
        }
        $currentOem = $Matches[1]
        $isVJoy = $false
    }
    elseif ($currentOem -and ($trimmed -match 'vjoy|shaul')) {
        $isVJoy = $true
    }
}
if ($isVJoy -and $currentOem) {
    Write-Host "  Deleting $currentOem"
    pnputil /delete-driver $currentOem /uninstall /force 2>&1 | Out-Null
}

# 2. Stop and delete the vjoy service
Write-Host "`n[2/8] Stopping and deleting vjoy service..." -ForegroundColor Yellow
sc.exe stop vjoy 2>&1 | Out-Null
sc.exe delete vjoy 2>&1 | Out-Null

# 3. Remove driver binaries from System32\drivers
Write-Host "`n[3/8] Removing driver binaries from System32\drivers..." -ForegroundColor Yellow
foreach ($f in @("vjoy.sys", "hidkmdf.sys")) {
    $p = Join-Path $env:SystemRoot "System32\drivers\$f"
    if (Test-Path $p) {
        Remove-Item $p -Force
        Write-Host "  Deleted $p"
    }
}

# 4. Delete Program Files\vJoy (entire directory)
Write-Host "`n[4/8] Removing Program Files\vJoy..." -ForegroundColor Yellow
$vjoyDir = Join-Path $env:ProgramFiles "vJoy"
if (Test-Path $vjoyDir) {
    Remove-Item $vjoyDir -Recurse -Force
    Write-Host "  Deleted $vjoyDir"
} else {
    Write-Host "  Not found (already clean)"
}

# 5. Remove Start Menu shortcuts
Write-Host "`n[5/8] Removing Start Menu shortcuts..." -ForegroundColor Yellow
$startMenuPaths = @(
    (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\vJoy"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\vJoy")
)
foreach ($sm in $startMenuPaths) {
    if (Test-Path $sm) {
        Remove-Item $sm -Recurse -Force
        Write-Host "  Deleted $sm"
    }
}

# 6. Remove registry uninstall entries (Add/Remove Programs)
Write-Host "`n[6/8] Removing registry uninstall entries..." -ForegroundColor Yellow
$uninstallPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
)
foreach ($path in $uninstallPaths) {
    Get-ChildItem $path -EA SilentlyContinue | ForEach-Object {
        $dn = (Get-ItemProperty $_.PSPath -EA SilentlyContinue).DisplayName
        if ($dn -and $dn -like '*vJoy*') {
            Write-Host "  Removing $($_.PSChildName) ($dn)"
            Remove-Item $_.PSPath -Recurse -Force
        }
    }
}

# 7. Remove vJoy-specific registry keys
Write-Host "`n[7/8] Removing vJoy registry artifacts..." -ForegroundColor Yellow
$regKeys = @(
    "HKLM:\SYSTEM\CurrentControlSet\Services\vjoy",
    "HKLM:\SYSTEM\ControlSet001\Services\vjoy",
    "HKLM:\SYSTEM\ControlSet001\Services\EventLog\System\vjoy",
    "HKLM:\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
    "HKCU:\System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys"
)
foreach ($key in $regKeys) {
    if (Test-Path $key) {
        Remove-Item $key -Recurse -Force
        Write-Host "  Removed $key"
    }
}

# 8. Clean vJoy device class entries
Write-Host "`n[8/8] Cleaning device class entries..." -ForegroundColor Yellow
$classPath = "HKLM:\SYSTEM\ControlSet001\Control\Class\{781ef630-72b2-11d2-b852-00c04fad5101}"
if (Test-Path $classPath) {
    Get-ChildItem $classPath -EA SilentlyContinue | ForEach-Object {
        $cls = (Get-ItemProperty $_.PSPath -EA SilentlyContinue).Class
        if ($cls -eq 'vjoy') {
            Write-Host "  Removing device class entry $($_.PSChildName)"
            Remove-Item $_.PSPath -Recurse -Force
        }
    }
}

Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Legacy vJoy installation has been fully removed."
Write-Host "You can now install vJoy cleanly through PadForge."
Read-Host "`nPress Enter to close"
