# vJoy lifecycle test: create vJoy slot, verify, switch to Xbox, verify cleanup
# Run elevated while PadForge is running

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "ERROR: PadForge not running"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$treeCond = [System.Windows.Automation.Condition]::TrueCondition

function Find-ElementByName($parent, $name, $depth = 8) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ElementByAid($parent, $aid) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $aid)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click-Element($el) {
    if (-not $el) { Write-Host "  ERROR: Element not found"; return $false }
    $invokePat = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    if ($invokePat) { $invokePat.Invoke(); return $true }
    # Fallback: click at center
    $rect = $el.Current.BoundingRectangle
    $x = [int]($rect.X + $rect.Width / 2)
    $y = [int]($rect.Y + $rect.Height / 2)
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
    Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);' -Name 'Win32Mouse' -Namespace 'Interop'
    [Interop.Win32Mouse]::mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    Start-Sleep -Milliseconds 50
    [Interop.Win32Mouse]::mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
    return $true
}

function Check-VJoyState($label) {
    Write-Host ""
    Write-Host "=== $label ==="
    $baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
    if (Test-Path $baseKey) {
        $subkeys = Get-ChildItem $baseKey -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^Device\d+$' }
        Write-Host "  Registry DeviceNN keys: $($subkeys.Count)"
    } else {
        Write-Host "  Registry: vjoy Parameters key not found"
    }
    $vjoyEntities = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*vJoy*' }
    Write-Host "  PnP vJoy entities: $($vjoyEntities.Count)"
    foreach ($e in $vjoyEntities) {
        Write-Host "    $($e.Name) | Status=$($e.Status) | Error=$($e.ConfigManagerErrorCode)"
    }
}

# --- Step 1: Check initial state ---
Check-VJoyState "INITIAL STATE"

# --- Step 2: Click Add Controller button ---
Write-Host ""
Write-Host "--- Clicking Add Controller ---"
$addBtn = Find-ElementByName $root "Add Controller"
if (-not $addBtn) {
    # Try looking for a "+" button or similar
    $addBtn = Find-ElementByName $root "+"
}
if ($addBtn) {
    Click-Element $addBtn
    Start-Sleep -Seconds 1
} else {
    Write-Host "ERROR: Could not find Add Controller button"
    exit 1
}

# --- Step 3: Click vJoy in the popup ---
Write-Host "--- Clicking vJoy type ---"
$vjoyBtn = Find-ElementByName $root "vJoy"
if ($vjoyBtn) {
    Click-Element $vjoyBtn
    Start-Sleep -Seconds 3
} else {
    Write-Host "ERROR: Could not find vJoy button"
    exit 1
}

Check-VJoyState "AFTER CREATING vJoy SLOT"

# --- Step 4: Find the vJoy slot's type switch buttons in sidebar ---
# We need to click the Xbox 360 type button for this slot
Write-Host ""
Write-Host "--- Looking for Xbox type button on the vJoy slot ---"

# Dump relevant UI tree to find the button
$allButtons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
$xboxBtns = @()
foreach ($btn in $allButtons) {
    $name = $btn.Current.Name
    if ($name -like '*Xbox*' -or $name -like '*xbox*' -or $name -eq 'Xbox 360') {
        $xboxBtns += $btn
        Write-Host "  Found button: '$name' aid='$($btn.Current.AutomationId)'"
    }
}

if ($xboxBtns.Count -gt 0) {
    # Click the last Xbox button (likely the type switch on the newest slot)
    $targetBtn = $xboxBtns[-1]
    Write-Host "--- Clicking Xbox 360 type button ---"
    Click-Element $targetBtn
    Start-Sleep -Seconds 3

    Check-VJoyState "AFTER SWITCHING TO XBOX 360"
} else {
    Write-Host "ERROR: Could not find Xbox 360 type button"
    Write-Host "  Listing all buttons:"
    foreach ($btn in $allButtons) {
        Write-Host "    '$($btn.Current.Name)' aid='$($btn.Current.AutomationId)'"
    }
}

# --- Step 5: Check log file ---
Write-Host ""
Write-Host "=== DiagLog Output ==="
$logFile = Join-Path (Split-Path (Get-Process PadForge | Select-Object -First 1).Path) "vjoy_diag.log"
if (Test-Path $logFile) {
    Get-Content $logFile | Select-Object -Last 30
} else {
    # Try C:\PadForge
    $logFile2 = "C:\PadForge\vjoy_diag.log"
    if (Test-Path $logFile2) {
        Get-Content $logFile2 | Select-Object -Last 30
    } else {
        Write-Host "No log file found at $logFile or $logFile2"
    }
}

Write-Host ""
Write-Host "Done."
