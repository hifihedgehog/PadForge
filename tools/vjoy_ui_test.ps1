# vJoy UI Automation Test
# Changes vJoy config via PadForge's UI controls (elevated)
# Must run elevated to interact with elevated PadForge.

param(
    [string]$Action = 'test'  # 'test' runs all configs, 'dump' shows UI tree
)

$logFile = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_ui_test_log.txt'
$queryScript = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_query_device.ps1'
$toolsDir = 'C:\Users\sonic\GitHub\PadForge\tools'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$auto = [System.Windows.Automation.AutomationElement]
$cond = [System.Windows.Automation.Condition]
$prop = [System.Windows.Automation.AutomationProperty]
$tree = [System.Windows.Automation.TreeScope]
$ct = [System.Windows.Automation.ControlType]

function Find-PadForgeWindow {
    $root = $auto::RootElement
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        $auto::NameProperty, 'PadForge', [System.Windows.Automation.PropertyConditionFlags]::IgnoreCase)
    $classCond = New-Object System.Windows.Automation.PropertyCondition(
        $auto::ClassNameProperty, 'Window')
    $combined = New-Object System.Windows.Automation.AndCondition($nameCond, $classCond)
    return $root.FindFirst($tree::Children, $combined)
}

function Find-Element([System.Windows.Automation.AutomationElement]$parent, [string]$automationId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $auto::AutomationIdProperty, $automationId)
    return $parent.FindFirst($tree::Descendants, $cond)
}

function Find-ElementByName([System.Windows.Automation.AutomationElement]$parent, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        $auto::NameProperty, $name)
    return $parent.FindFirst($tree::Descendants, $cond)
}

function Set-TextBoxValue([System.Windows.Automation.AutomationElement]$element, [string]$value, [switch]$Apply) {
    $valuePattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($value)
    if ($Apply) {
        # Simulate Enter key to trigger the KeyDown handler which applies all custom values
        $element.SetFocus()
        Start-Sleep -Milliseconds 100
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Milliseconds 200
    }
}

function Set-ComboBoxIndex([System.Windows.Automation.AutomationElement]$element, [int]$index) {
    $expandPattern = $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expandPattern.Expand()
    Start-Sleep -Milliseconds 300

    # Find items
    $liCond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $ct::ListItem)
    $items = $element.FindAll($tree::Children, $liCond)

    if ($index -lt $items.Count) {
        $selectPattern = $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selectPattern.Select()
    }
    Start-Sleep -Milliseconds 300
}

function Click-Element([System.Windows.Automation.AutomationElement]$element) {
    $invokePattern = $null
    try {
        $invokePattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
    } catch {
        # Try click via point
        $rect = $element.Current.BoundingRectangle
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
        Start-Sleep -Milliseconds 100

        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MouseClick {
    [DllImport("user32.dll")] public static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    public const int MOUSEEVENTF_LEFTDOWN = 0x02;
    public const int MOUSEEVENTF_LEFTUP = 0x04;
    public static void Click() { mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0); mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0); }
}
'@ -ErrorAction SilentlyContinue
        [MouseClick]::Click()
    }
    Start-Sleep -Milliseconds 300
}

function Query-Device([int]$deviceId) {
    $outFile = "$toolsDir\vjoy_query_result_$deviceId.txt"
    if (Test-Path $outFile) { Remove-Item $outFile -Force }
    $p = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$queryScript`" -DeviceId $deviceId -OutFile `"$outFile`"" -Wait -PassThru -WindowStyle Hidden
    if (-not (Test-Path $outFile)) { return $null }
    $line = Get-Content $outFile -Raw
    if ($line -match 'status=(\d+)\s+axes=(\d+)\s+buttons=(\d+)\s+contPovs=(\d+)') {
        return @{ Status=[int]$matches[1]; Axes=[int]$matches[2]; Buttons=[int]$matches[3]; ContPovs=[int]$matches[4] }
    }
    return $null
}

# ── Main ──

$R = [System.Collections.Generic.List[string]]::new()
$R.Add("=== vJoy UI Automation Test $(Get-Date) ===")
$R.Add("")

$win = Find-PadForgeWindow
if (-not $win) {
    $R.Add("FATAL: PadForge window not found")
    $R | Out-File $logFile -Encoding utf8 -Force
    exit 1
}
$R.Add("Found PadForge window")

if ($Action -eq 'dump') {
    # Dump UI tree for debugging
    function Dump-Tree([System.Windows.Automation.AutomationElement]$el, [int]$depth = 0) {
        $indent = '  ' * $depth
        $name = $el.Current.Name
        $aid = $el.Current.AutomationId
        $cls = $el.Current.ClassName
        $ctName = $el.Current.ControlType.ProgrammaticName
        $R.Add("$indent[$ctName] Name='$name' AutomationId='$aid' Class='$cls'")
        if ($depth -gt 4) { return }
        try {
            $children = $el.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($child in $children) { Dump-Tree $child ($depth + 1) }
        } catch {}
    }
    Dump-Tree $win
    $R | Out-File $logFile -Encoding utf8 -Force
    $R | ForEach-Object { Write-Host $_ }
    exit
}

# Navigate to the vJoy controller page via NavView sidebar
# Sidebar items are ListItems inside NavView. The first vJoy slot is named "Pad1".
$navView = Find-Element $win 'NavView'
if (-not $navView) {
    $R.Add("FATAL: NavView not found")
    $R | Out-File $logFile -Encoding utf8 -Force; exit 1
}

# Find all ListItems in sidebar — look for "Pad" items (Pad1, Pad2, etc.)
$listItemCond = New-Object System.Windows.Automation.PropertyCondition(
    $auto::ControlTypeProperty, $ct::ListItem)
$listItems = $navView.FindAll($tree::Descendants, $listItemCond)
$padItem = $null
foreach ($li in $listItems) {
    $name = $li.Current.Name
    $R.Add("  Sidebar: '$name'")
    if ($name -match '^Pad\d+$' -and -not $padItem) { $padItem = $li }
}

if (-not $padItem) {
    $R.Add("FATAL: No Pad item found in sidebar. Is a vJoy controller created?")
    $R | Out-File $logFile -Encoding utf8 -Force; exit 1
}

$R.Add("Navigating to '$($padItem.Current.Name)'...")
$rect = $padItem.Current.BoundingRectangle
$R.Add("  BoundingRect: X=$($rect.X) Y=$($rect.Y) W=$($rect.Width) H=$($rect.Height)")

# Try SelectionItemPattern first (ModernWpf NavigationViewItem supports this)
$navigated = $false
try {
    $selPattern = $padItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selPattern.Select()
    $R.Add("  SelectionItemPattern.Select() succeeded")
    $navigated = $true
} catch {
    $R.Add("  SelectionItemPattern failed: $($_.Exception.Message)")
}

if (-not $navigated) {
    try {
        $invokePattern = $padItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invokePattern.Invoke()
        $R.Add("  InvokePattern.Invoke() succeeded")
        $navigated = $true
    } catch {
        $R.Add("  InvokePattern failed: $($_.Exception.Message)")
    }
}

if (-not $navigated) {
    $R.Add("  Falling back to mouse click at center of bounding rect")
    Click-Element $padItem
}

Start-Sleep -Seconds 3

# Check which page is now active
$customCond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Custom)
$pageCheck = $win.FindAll($tree::Descendants, $customCond)
foreach ($p in $pageCheck) {
    $aid = $p.Current.AutomationId
    if ($aid -match 'PageView') {
        $R.Add("  Active page: $aid")
    }
}

# Now find vJoy config controls on the Pad page
$presetCombo = Find-Element $win 'VJoyPresetCombo'
if (-not $presetCombo) {
    # The Pad page might be showing Xbox360/DS4 output type, not vJoy
    # Try scrolling or waiting
    $R.Add("VJoyPresetCombo not found after navigation - checking if this is a vJoy slot...")
    Start-Sleep -Seconds 1
    $presetCombo = Find-Element $win 'VJoyPresetCombo'
}

if (-not $presetCombo) {
    $R.Add("FATAL: VJoyPresetCombo not found. Is this slot set to vJoy output type?")

    # Search for ALL ComboBox controls anywhere in the window
    $R.Add("Searching for all ComboBox controls...")
    $comboCond = New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $combos = $win.FindAll($tree::Descendants, $comboCond)
    foreach ($c in $combos) {
        $R.Add("  ComboBox: Name='$($c.Current.Name)' AID='$($c.Current.AutomationId)' Class='$($c.Current.ClassName)'")
    }

    # Search for PadPageView specifically and dump its children
    $R.Add("Dumping PadPageView descendants...")
    $padPageCond = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, 'PadPageView')
    $padPage = $win.FindFirst($tree::Descendants, $padPageCond)
    if ($padPage) {
        $ppDescendants = $padPage.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $R.Add("  PadPageView has $($ppDescendants.Count) descendants")
        $count = 0
        foreach ($d in $ppDescendants) {
            $aid = $d.Current.AutomationId
            $dname = $d.Current.Name
            $cls = $d.Current.ClassName
            $ctName = $d.Current.ControlType.ProgrammaticName
            $R.Add("  [$ctName] Name='$dname' AID='$aid' Class='$cls'")
            $count++
            if ($count -gt 80) { $R.Add("  ... (truncated)"); break }
        }
    } else {
        $R.Add("  PadPageView not found!")
    }
    $R | Out-File $logFile -Encoding utf8 -Force
    $R | ForEach-Object { Write-Host $_ }
    exit 1
}

$R.Add("Found VJoyPresetCombo on Pad page")

# ── Test configs ──

$tests = @(
    @{ Label = "Custom 32 btns, 2 POVs"; PresetIdx = 2; Sticks = '2'; Triggers = '2'; Buttons = '32'; Povs = '2' },
    @{ Label = "Custom 64 btns, 4 POVs"; PresetIdx = 2; Sticks = '4'; Triggers = '0'; Buttons = '64'; Povs = '4' },
    @{ Label = "Custom 128 btns, 3 POVs"; PresetIdx = 2; Sticks = '2'; Triggers = '2'; Buttons = '128'; Povs = '3' },
    @{ Label = "Custom 4 btns, 0 POVs"; PresetIdx = 2; Sticks = '1'; Triggers = '0'; Buttons = '4'; Povs = '0' },
    @{ Label = "Xbox 360 preset"; PresetIdx = 0; Sticks = '2'; Triggers = '2'; Buttons = '11'; Povs = '1' }
)

$pass = 0
$fail = 0

foreach ($test in $tests) {
    $R.Add("--- $($test.Label) ---")

    # Set preset
    Set-ComboBoxIndex $presetCombo $test.PresetIdx
    Start-Sleep -Milliseconds 500

    # If Custom preset, set values
    if ($test.PresetIdx -eq 2) {
        # Re-find controls (Custom panel might have just become visible)
        $sticksBox = Find-Element $win 'VJoyStickCountBox'
        $triggersBox = Find-Element $win 'VJoyTriggerCountBox'
        $povsBox = Find-Element $win 'VJoyPovCountBox'
        $buttonsBox = Find-Element $win 'VJoyButtonCountBox'

        # Set all values first without triggering apply, then apply once on last field
        if ($sticksBox) { Set-TextBoxValue $sticksBox $test.Sticks }
        if ($triggersBox) { Set-TextBoxValue $triggersBox $test.Triggers }
        if ($povsBox) { Set-TextBoxValue $povsBox $test.Povs }
        if ($buttonsBox) { Set-TextBoxValue $buttonsBox $test.Buttons -Apply }
        $R.Add("  Set: sticks=$($test.Sticks) triggers=$($test.Triggers) btns=$($test.Buttons) povs=$($test.Povs)")
    } else {
        $R.Add("  Set preset: Xbox 360")
    }

    # Wait for PadForge to restart vJoy device node
    # Multiple property changes may trigger multiple DICS_PROPCHANGE restarts
    $R.Add("  Waiting for vJoy node restart...")
    Start-Sleep -Seconds 15

    # Query vJoy device with retries — check for expected values too
    $dev = $null
    $expectedBtns = [int]$test.Buttons
    $expectedPovs = [int]$test.Povs
    for ($retry = 0; $retry -lt 5; $retry++) {
        $dev = Query-Device -deviceId 1
        if ($dev -and $dev.Status -ne 3 -and $dev.Buttons -eq $expectedBtns -and $dev.ContPovs -eq $expectedPovs) { break }
        $R.Add("  Retry $($retry+1): status=$($dev.Status) btns=$($dev.Buttons) povs=$($dev.ContPovs)")
        Start-Sleep -Seconds 3
    }
    if (-not $dev -or $dev.Status -eq 3) {
        $R.Add("  FAIL: Device not found")
        $fail++
    } else {
        $btnsOk = $dev.Buttons -eq [int]$test.Buttons
        $povOk = $dev.ContPovs -eq [int]$test.Povs

        if ($btnsOk -and $povOk) {
            $R.Add("  PASS btns=$($dev.Buttons)/$($test.Buttons) povs=$($dev.ContPovs)/$($test.Povs)")
            $pass++
        } else {
            $R.Add("  FAIL expected(btns=$($test.Buttons) povs=$($test.Povs)) actual(btns=$($dev.Buttons) povs=$($dev.ContPovs))")
            $fail++
        }
    }
    $R.Add("")

    # Write intermediate results
    $R | Out-File $logFile -Encoding utf8 -Force
}

# Cleanup temp files
for ($i = 1; $i -le 16; $i++) {
    $f = "$toolsDir\vjoy_query_result_$i.txt"
    if (Test-Path $f) { Remove-Item $f -Force }
}

$R.Add("========================================")
$R.Add("RESULTS: $pass passed, $fail failed out of $($tests.Count) tests")
$R.Add("========================================")

$R | Out-File $logFile -Encoding utf8 -Force
$R | ForEach-Object { Write-Host $_ }
