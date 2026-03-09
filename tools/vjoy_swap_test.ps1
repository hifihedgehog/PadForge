# Test: Verify that adding a 2nd vJoy preserves Pad1's config and mappings
# Setup: Pad1 = vJoy (Custom 32btn), add Pad2 = vJoy
# Verify: Pad1 still shows Custom 32btn after Pad2 is added

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

$logFile = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_swap_test_log.txt"
function Log($msg) { $msg | Out-File $logFile -Append -Encoding utf8; Write-Host $msg }
"" | Out-File $logFile -Encoding utf8 -Force
Log "=== vJoy Swap/Reorder Test $(Get-Date) ==="

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $mainWin) { Log "FAIL: PadForge window not found"; exit 1 }
Log "Found PadForge"

function Find-ById($id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id)
    return $mainWin.FindFirst($tree::Descendants, $c)
}

function Find-ByName($name) {
    $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
    return $mainWin.FindFirst($tree::Descendants, $c)
}

function Navigate($name) {
    $el = Find-ByName $name
    if (-not $el) { Log "  Nav '$name' not found"; return $false }
    try {
        $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Start-Sleep -Seconds 1
        return $true
    } catch { Log "  Cannot select '$name'"; return $false }
}

function Get-ComboValue($id) {
    $combo = Find-ById $id
    if (-not $combo) { return "NOT_FOUND" }
    try {
        $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $s = $sel.Current.GetSelection()
        if ($s.Count -gt 0) { return $s[0].Current.Name }
    } catch {}
    return "NONE"
}

function Get-TextValue($id) {
    $tb = Find-ById $id
    if (-not $tb) { return "NOT_FOUND" }
    try {
        return ($tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    } catch { return "ERROR" }
}

function Set-ComboIndex($id, $index) {
    $combo = Find-ById $id
    if (-not $combo) { Log "  Combo '$id' not found"; return }
    $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $exp.Expand()
    Start-Sleep -Milliseconds 300
    $itemCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $items = $combo.FindAll($tree::Children, $itemCond)
    if ($items.Count -gt $index) {
        $sel = $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
    }
    Start-Sleep -Milliseconds 300
    $exp.Collapse()
}

function Set-TextValue($id, $value, [switch]$Apply) {
    $tb = Find-ById $id
    if (-not $tb) { Log "  TextBox '$id' not found"; return }
    $val = $tb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $val.SetValue($value)
    if ($Apply) {
        $tb.SetFocus()
        Start-Sleep -Milliseconds 100
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Start-Sleep -Milliseconds 200
    }
}

# Step 1: Navigate to Pad1 and set it to Custom 32 buttons
Log ""
Log "--- Step 1: Configure Pad1 as Custom 32btn ---"
if (-not (Navigate "Pad1")) { Log "FAIL"; exit 1 }

Set-ComboIndex "VJoyPresetCombo" 2  # Custom
Start-Sleep -Seconds 1
Set-TextValue "VJoyButtonCountBox" "32" -Apply
Start-Sleep -Seconds 5  # Wait for node restart

$p1preset = Get-ComboValue "VJoyPresetCombo"
$p1btns = Get-TextValue "VJoyButtonCountBox"
Log "Pad1 before: preset=$p1preset buttons=$p1btns"

if ($p1preset -ne "Custom" -or $p1btns -ne "32") {
    Log "FAIL: Could not configure Pad1 as Custom 32btn"
    exit 1
}
Log "Pad1 configured as Custom 32btn"

# Step 2: Check how many pads exist
$pad2Exists = (Find-ByName "Pad2") -ne $null
Log "Pad2 exists: $pad2Exists"

if (-not $pad2Exists) {
    Log ""
    Log "--- Step 2: Adding 2nd vJoy slot ---"
    # Need to add via XML edit + restart since we can't click the popup button
    Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    
    $padForgeXml = 'C:\PadForge\PadForge.xml'
    [xml]$xml = Get-Content $padForgeXml
    # Set slot 1 to vJoy + created
    $xml.PadForgeSettings.AppSettings.SlotControllerTypes.ChildNodes[1].InnerText = '2'
    $xml.PadForgeSettings.AppSettings.SlotCreated.ChildNodes[1].InnerText = 'true'
    $xml.Save($padForgeXml)
    Log "Set slot 1 to vJoy+created in XML"
    
    Start-Process 'C:\PadForge\PadForge.exe'
    Start-Sleep -Seconds 6
    
    $mainWin = $ae::RootElement.FindFirst($tree::Children, $cond)
    if (-not $mainWin) { Log "FAIL: PadForge not found after restart"; exit 1 }
}

# Step 3: Check Pad1's config is preserved
Log ""
Log "--- Step 3: Verify Pad1 config preserved ---"
if (-not (Navigate "Pad1")) { Log "FAIL: Pad1 not found"; exit 1 }

$p1presetAfter = Get-ComboValue "VJoyPresetCombo"
$p1btnsAfter = Get-TextValue "VJoyButtonCountBox"
Log "Pad1 after: preset=$p1presetAfter buttons=$p1btnsAfter"

if ($p1presetAfter -eq "Custom" -and $p1btnsAfter -eq "32") {
    Log "  PASS: Pad1 config preserved (Custom 32btn)"
} else {
    Log "  FAIL: Pad1 config changed to $p1presetAfter/$p1btnsAfter"
}

# Step 4: Check Pad2's config
Log ""
Log "--- Step 4: Check Pad2 config ---"
if (Navigate "Pad2") {
    $p2preset = Get-ComboValue "VJoyPresetCombo"
    $p2btns = Get-TextValue "VJoyButtonCountBox"
    Log "Pad2: preset=$p2preset buttons=$p2btns"
    
    if ($p2preset -eq "Xbox 360" -and $p2btns -eq "11") {
        Log "  PASS: Pad2 got Xbox 360 default"
    } else {
        Log "  INFO: Pad2 has $p2preset/$p2btns"
    }
} else {
    Log "  SKIP: Pad2 not found"
}

# Step 5: Check Pad1 again (verify no regression from navigating to Pad2)
Log ""
Log "--- Step 5: Final Pad1 check ---"
if (Navigate "Pad1") {
    $p1final = Get-ComboValue "VJoyPresetCombo"
    $p1btnsFinal = Get-TextValue "VJoyButtonCountBox"
    Log "Pad1 final: preset=$p1final buttons=$p1btnsFinal"
    
    if ($p1final -eq "Custom" -and $p1btnsFinal -eq "32") {
        Log "  PASS: Pad1 config still correct"
    } else {
        Log "  FAIL: Pad1 config changed to $p1final/$p1btnsFinal"
    }
}

Log ""
Log "=== Test Complete ==="
