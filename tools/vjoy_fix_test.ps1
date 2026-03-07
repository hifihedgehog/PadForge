## Test alternative pnputil operations for stuck vJoy node
param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_fix_test_log.txt"
function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Elevating..."
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $logFile) { Get-Content $logFile }
        exit
    }
}

Set-Content -Path $logFile -Value "=== vJoy Fix Test $(Get-Date) ==="
Log "Admin: True"

$nodeId = "ROOT\HIDCLASS\0000"

# Check current state
$status = pnputil /enum-devices /instanceid "$nodeId" 2>&1 | Out-String
Log "Current state:"
foreach ($l in ($status -split "`n")) { if ($l.Trim()) { Log "  $l" } }

# Test 1: Try /restart-device
Log ""
Log "--- Test 1: pnputil /restart-device ---"
$r = pnputil /restart-device "$nodeId" 2>&1 | Out-String
Log "Result: $($r.Trim())"
Log "Exit: $LASTEXITCODE"
Start-Sleep -Seconds 2

# Check joy.cpl
$jc = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Log ("joyCpl count: " + ($jc | Measure-Object).Count)

# Test 2: Try /remove-device
Log ""
Log "--- Test 2: pnputil /remove-device ---"
$r = pnputil /remove-device "$nodeId" 2>&1 | Out-String
Log "Result: $($r.Trim())"
Log "Exit: $LASTEXITCODE"
Start-Sleep -Seconds 2

# Check if node still exists
$status2 = pnputil /enum-devices /instanceid "$nodeId" 2>&1 | Out-String
Log "After remove:"
foreach ($l in ($status2 -split "`n")) { if ($l.Trim()) { Log "  $l" } }

$jc2 = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Log ("joyCpl count: " + ($jc2 | Measure-Object).Count)

# Test 3: Try devcon-style SetupAPI restart via PowerShell
Log ""
Log "--- Test 3: SetupAPI CM_Locate_DevNode + CM_Reenumerate ---"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class CfgMgr32 {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Reenumerate_DevNode(int dnDevInst, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(int dnDevInst, int ulFlags);

    // Flags
    public const int CM_LOCATE_DEVNODE_NORMAL = 0;
    public const int CM_REENUMERATE_NORMAL = 0;
    public const int CM_REENUMERATE_SYNCHRONOUS = 1;
    public const int CM_DISABLE_HARDWARE = 0x4; // Win 10+
    public const int CR_SUCCESS = 0;
}
"@ -ErrorAction SilentlyContinue

$devInst = 0
$locResult = [CfgMgr32]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, [CfgMgr32]::CM_LOCATE_DEVNODE_NORMAL)
Log ("CM_Locate_DevNode: result=" + $locResult + " devInst=" + $devInst)

if ($locResult -eq 0 -and $devInst -ne 0) {
    # Try CM_Disable_DevNode
    Log "  Trying CM_Disable_DevNode..."
    $disResult = [CfgMgr32]::CM_Disable_DevNode($devInst, [CfgMgr32]::CM_DISABLE_HARDWARE)
    Log ("  CM_Disable_DevNode: result=" + $disResult)
    Start-Sleep -Seconds 1

    # Try CM_Enable_DevNode
    Log "  Trying CM_Enable_DevNode..."
    $enResult = [CfgMgr32]::CM_Enable_DevNode($devInst, 0)
    Log ("  CM_Enable_DevNode: result=" + $enResult)
    Start-Sleep -Seconds 2

    # Try CM_Reenumerate_DevNode
    Log "  Trying CM_Reenumerate_DevNode..."
    $reenumResult = [CfgMgr32]::CM_Reenumerate_DevNode($devInst, [CfgMgr32]::CM_REENUMERATE_SYNCHRONOUS)
    Log ("  CM_Reenumerate_DevNode: result=" + $reenumResult)
    Start-Sleep -Seconds 2
} else {
    Log "  Cannot locate device node"
}

# Final state
$status3 = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
$vjoyLines = ($status3 -split "`n") | Where-Object { $_ -match 'vJoy|ROOT\\HIDCLASS' }
Log ""
Log "Final HIDClass entries with vJoy:"
foreach ($l in $vjoyLines) { Log "  $l" }

$jc3 = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' }
Log ("Final joyCpl count: " + ($jc3 | Measure-Object).Count)

Log ""
Log "=== Done ==="
