## Direct vJoy Operations Test
## Tests registry writes + pnputil enable/disable/restart — same ops as PadForge
## Self-elevating. No PadForge UI needed.

param([switch]$Elevated)

$logFile = Join-Path $PSScriptRoot "vjoy_ops_test_log.txt"

function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

# Self-elevate
if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Elevating..."
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        if (Test-Path $logFile) { Get-Content $logFile }
        exit
    }
}

Set-Content -Path $logFile -Value "=== vJoy Operations Test $(Get-Date) ==="
Log "Running as Admin: True"

$regBase = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'

# ─── Helper: Count joy.cpl controllers ───
function Get-JoyCplCount {
    $entities = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*vJoy*' -and $_.DeviceID -like '*VJOYRAWPDO*' }
    return ($entities | Measure-Object).Count
}

# ─── Helper: Find vJoy device node instance ID ───
function Get-VJoyNodeId {
    $output = pnputil /enum-devices /class HIDClass 2>&1 | Out-String
    $currentId = $null
    foreach ($line in $output -split "`n") {
        $trimmed = $line.Trim()
        if ($trimmed -match 'Instance ID:\s+(.+)') {
            $currentId = $matches[1].Trim()
        }
        elseif ($currentId -and $trimmed -match 'vJoy' -and $currentId -match '^ROOT\\HIDCLASS\\') {
            return $currentId
        }
        elseif ([string]::IsNullOrWhiteSpace($trimmed)) {
            $currentId = $null
        }
    }
    return $null
}

# ─── Helper: Get node status ───
function Get-NodeStatus($instanceId) {
    if (-not $instanceId) { return "NO NODE" }
    $output = pnputil /enum-devices /instanceid "$instanceId" 2>&1 | Out-String
    if ($output -match 'Status:\s+(\S+)') { return $matches[1].Trim() }
    return "UNKNOWN"
}

# ─── Helper: Registry state ───
function Get-RegState {
    if (-not (Test-Path $regBase)) { return @{ Keys = @(); Count = 0 } }
    $subkeys = Get-ChildItem $regBase -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^Device\d+$' }
    $keys = @()
    foreach ($sk in $subkeys) {
        $keys += @{ Name = $sk.PSChildName; DescSize = $sk.GetValue("HidReportDescriptorSize") }
    }
    return @{ Keys = $keys; Count = $keys.Count }
}

function Log-State($label) {
    $nodeId = Get-VJoyNodeId
    $nodeStatus = Get-NodeStatus $nodeId
    $reg = Get-RegState
    $joyCpl = Get-JoyCplCount
    Log ($label + ": node=" + $nodeId + " status=" + $nodeStatus + " regKeys=" + $reg.Count + " joyCpl=" + $joyCpl)
    foreach ($k in $reg.Keys) {
        Log ("  " + $k.Name + ": DescSize=" + $k.DescSize)
    }
}

# ═══ TEST SEQUENCE ═══

Log ""
Log-State "INITIAL"

# Step 1: Ensure Device01 exists with a basic descriptor
Log ""
Log "--- Step 1: Write Device01 registry key ---"
try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
    if (-not $key) { Log "FATAL: Cannot open Parameters key for write"; exit 1 }

    # Create Device01 with a minimal descriptor (or keep existing)
    $dev01 = $key.OpenSubKey("Device01")
    if ($dev01) {
        $existingSize = $dev01.GetValue("HidReportDescriptorSize")
        Log ("  Device01 already exists (DescSize=" + $existingSize + ")")
        $dev01.Close()
    } else {
        Log "  Device01 does not exist - NOT creating (should already be there from PadForge)"
    }
    $key.Close()
} catch {
    Log "  ERROR: $_"
}
Log-State "AFTER Step 1"

# Step 2: Enable the node (in case it's disabled)
Log ""
Log "--- Step 2: Ensure node is enabled ---"
$nodeId = Get-VJoyNodeId
if ($nodeId) {
    $status = Get-NodeStatus $nodeId
    if ($status -ne 'Started') {
        Log "  Node is $status, enabling..."
        $result = pnputil /enable-device "$nodeId" 2>&1 | Out-String
        Log "  pnputil enable: $($result.Trim())"
        Start-Sleep -Seconds 2
    } else {
        Log "  Node already Started"
    }
} else {
    Log "  No vJoy node found!"
}
Log-State "AFTER Step 2"

# Step 3: Write Device02 (simulate adding a second vJoy controller)
Log ""
Log "--- Step 3: Write Device02 registry key ---"
try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
    # Read Device01's descriptor and duplicate it for Device02
    $dev01 = $key.OpenSubKey("Device01")
    if ($dev01) {
        $desc = $dev01.GetValue("HidReportDescriptor")
        $descSize = $dev01.GetValue("HidReportDescriptorSize")
        $dev01.Close()

        $dev02 = $key.CreateSubKey("Device02")
        $dev02.SetValue("HidReportDescriptor", $desc, [Microsoft.Win32.RegistryValueKind]::Binary)
        $dev02.SetValue("HidReportDescriptorSize", $descSize, [Microsoft.Win32.RegistryValueKind]::DWord)
        $dev02.Close()
        Log "  Wrote Device02 (DescSize=$descSize)"
    } else {
        Log "  ERROR: Device01 not found to duplicate"
    }
    $key.Close()
} catch {
    Log "  ERROR: $_"
}
Log-State "AFTER Step 3 (before restart)"

# Step 4: Restart node so driver picks up new descriptor
Log ""
Log "--- Step 4: Restart node (disable + enable) ---"
$nodeId = Get-VJoyNodeId
if ($nodeId) {
    Log "  Disabling $nodeId..."
    $r1 = pnputil /disable-device "$nodeId" 2>&1 | Out-String
    Log "  disable result: $($r1.Trim())"
    Log "  LASTEXITCODE: $LASTEXITCODE"
    Start-Sleep -Seconds 1

    Log "  Enabling $nodeId..."
    $r2 = pnputil /enable-device "$nodeId" 2>&1 | Out-String
    Log "  enable result: $($r2.Trim())"
    Log "  LASTEXITCODE: $LASTEXITCODE"
    Start-Sleep -Seconds 2
} else {
    Log "  No node found!"
}
Log-State "AFTER Step 4 (should show 2 controllers in joy.cpl)"

# Step 5: Delete Device02 (simulate switching second vJoy to Xbox)
Log ""
Log "--- Step 5: Delete Device02 ---"
try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
    $key.DeleteSubKeyTree("Device02", $false)
    $key.Close()
    Log "  Deleted Device02"
} catch {
    Log "  ERROR: $_"
}
Log-State "AFTER Step 5 (before restart)"

# Step 6: Restart node
Log ""
Log "--- Step 6: Restart node ---"
$nodeId = Get-VJoyNodeId
if ($nodeId) {
    Log "  Disabling $nodeId..."
    $r1 = pnputil /disable-device "$nodeId" 2>&1 | Out-String
    Log "  disable: $($r1.Trim()) (exit=$LASTEXITCODE)"
    Start-Sleep -Seconds 1

    Log "  Enabling $nodeId..."
    $r2 = pnputil /enable-device "$nodeId" 2>&1 | Out-String
    Log "  enable: $($r2.Trim()) (exit=$LASTEXITCODE)"
    Start-Sleep -Seconds 2
} else {
    Log "  No node found!"
}
Log-State "AFTER Step 6 (should show 1 controller in joy.cpl)"

# Step 7: Delete Device01 + disable node (simulate removing last vJoy)
Log ""
Log "--- Step 7: Delete Device01 + disable node ---"
try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
    $key.DeleteSubKeyTree("Device01", $false)
    $key.Close()
    Log "  Deleted Device01"
} catch {
    Log "  ERROR: $_"
}

$nodeId = Get-VJoyNodeId
if ($nodeId) {
    Log "  Disabling $nodeId..."
    $r1 = pnputil /disable-device "$nodeId" 2>&1 | Out-String
    Log "  disable: $($r1.Trim()) (exit=$LASTEXITCODE)"
    Start-Sleep -Seconds 2
} else {
    Log "  No node to disable!"
}
Log-State "AFTER Step 7 (should show 0 controllers in joy.cpl)"

# Step 8: Restore — write Device01 back and re-enable
Log ""
Log "--- Step 8: Restore Device01 + re-enable ---"
try {
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\services\vjoy\Parameters", $true)
    # Write a default Xbox 360 descriptor
    # (PadForge will rewrite with correct descriptor on next start)
    $dev01 = $key.CreateSubKey("Device01")
    # Just set a placeholder size — PadForge overwrites on start
    Log "  Created Device01 placeholder"
    $dev01.Close()
    $key.Close()
} catch {
    Log "  ERROR: $_"
}

$nodeId = Get-VJoyNodeId
if ($nodeId) {
    Log "  Enabling $nodeId..."
    $r2 = pnputil /enable-device "$nodeId" 2>&1 | Out-String
    Log "  enable: $($r2.Trim()) (exit=$LASTEXITCODE)"
    Start-Sleep -Seconds 2
}
Log-State "AFTER Step 8 (restored)"

Log ""
Log "=== Test Complete ==="
