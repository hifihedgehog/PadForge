# Test the stale config fix:
# 1. Put stale Custom/77btn config at slot 2 in XML (slot 2 is NOT created)
# 2. Set slot 2 to Created+VJoy via XML edit
# 3. Restart PadForge
# 4. Check if Pad3 gets the stale Custom/77 or fresh Xbox 360/11
#
# With the SettingsService fix, slot 2's config should NOT have been loaded
# at startup (because it wasn't created at load time).
# With the CreateSlot fix, even if some config leaked, Preset gets reset to Xbox360.
# BUT: since we're setting Created=true in XML before launch, the SettingsService
# WILL load it (correctly - it's a created vJoy slot).
#
# So the real test is: set Created=false + stale config, launch, then use
# set_vjoy_slot to create it at runtime. But we can't easily do that without
# UI automation for the Add Controller popup.
#
# Alternative: Test that the CreateSlot reset works by verifying the code path.
# We'll test both scenarios:
#   A) Slot already created+vJoy at startup: should load saved config (correct)
#   B) Slot uncreated at startup with stale XML: should NOT leak config

$padForgeXml = 'C:\PadForge\PadForge.xml'
$padForgeExe = 'C:\PadForge\PadForge.exe'
$logFile = 'C:\Users\sonic\GitHub\PadForge\tools\vjoy_stale_verify_log.txt'

function Log($msg) { $msg | Out-File $logFile -Append -Encoding utf8; Write-Host $msg }
"" | Out-File $logFile -Encoding utf8 -Force
Log "=== vJoy Stale Config Verification $(Get-Date) ==="

Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Setup: slot 0 = vJoy created, slot 1 = NOT created but has stale config
[xml]$xml = Get-Content $padForgeXml

# Slot 0: vJoy, created, Custom 32 buttons (should load this - existing slot)
$xml.PadForgeSettings.AppSettings.SlotControllerTypes.ChildNodes[0].InnerText = '2'
$xml.PadForgeSettings.AppSettings.SlotCreated.ChildNodes[0].InnerText = 'true'
foreach ($cfg in $xml.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes) {
    if ($cfg.SlotIndex -eq "0") {
        $cfg.Preset = "Custom"
        $cfg.ThumbstickCount = "2"
        $cfg.TriggerCount = "2"
        $cfg.PovCount = "1"
        $cfg.ButtonCount = "32"
    }
}
Log "Slot 0: vJoy+created, Custom/32btn (should load)"

# Slot 1: Xbox360 type, NOT created, but stale vJoy Custom 77 buttons in XML
$xml.PadForgeSettings.AppSettings.SlotControllerTypes.ChildNodes[1].InnerText = '0'
$xml.PadForgeSettings.AppSettings.SlotCreated.ChildNodes[1].InnerText = 'false'
foreach ($cfg in $xml.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes) {
    if ($cfg.SlotIndex -eq "1") {
        $cfg.Preset = "Custom"
        $cfg.ThumbstickCount = "1"
        $cfg.TriggerCount = "0"
        $cfg.PovCount = "0"
        $cfg.ButtonCount = "77"
    }
}
Log "Slot 1: Xbox360+uncreated, stale Custom/77btn (should NOT load)"

$xml.Save($padForgeXml)
Log "Saved XML, launching PadForge..."

Start-Process $padForgeExe
Start-Sleep -Seconds 6

# UI Automation
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $mainWin) { Log "FAIL: PadForge not found"; exit 1 }
Log "Found PadForge"

# Test A: Pad1 (slot 0) should have loaded Custom/32
Log ""
Log "--- Test A: Created vJoy slot loads saved config ---"
$navCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Pad1")
$pad1 = $mainWin.FindFirst($tree::Descendants, $navCond)
if (-not $pad1) { Log "FAIL: Pad1 not found"; exit 1 }
$sel = $pad1.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Seconds 2

$comboCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyPresetCombo")
$combo = $mainWin.FindFirst($tree::Descendants, $comboCond)
if (-not $combo) { Log "FAIL: VJoyPresetCombo not found on Pad1"; exit 1 }

$selPattern = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
$selected = $selPattern.Current.GetSelection()
$presetA = if ($selected.Count -gt 0) { $selected[0].Current.Name } else { "NONE" }

$btnCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "VJoyButtonCountBox")
$btnBox = $mainWin.FindFirst($tree::Descendants, $btnCond)
$btnsA = if ($btnBox) { ($btnBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } else { "NOT_FOUND" }

Log "Pad1: preset=$presetA buttons=$btnsA"
if ($presetA -eq "Custom" -and $btnsA -eq "32") {
    Log "  PASS: Created vJoy loaded saved Custom/32"
} else {
    Log "  FAIL: Expected Custom/32, got $presetA/$btnsA"
}

# Now create slot 1 as vJoy by editing XML and relaunching
# This simulates the "Add Controller -> vJoy" flow
Log ""
Log "--- Test B: Previously-uncreated slot with stale config ---"
Log "Now setting slot 1 to vJoy+created and relaunching..."

Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

[xml]$xml2 = Get-Content $padForgeXml
# The save from PadForge should have preserved slot 1's in-memory state
# Since the load guard blocked stale config, slot 1 should have Xbox 360 defaults
# in memory. When PadForge saved, it wrote Xbox 360 for slot 1.
# Let's check what PadForge actually wrote:
foreach ($cfg in $xml2.PadForgeSettings.AppSettings.VJoyConfigs.ChildNodes) {
    if ($cfg.SlotIndex -eq "1") {
        Log "Slot 1 in saved XML: Preset=$($cfg.Preset) Buttons=$($cfg.ButtonCount)"
        if ($cfg.Preset -eq "Xbox360" -and $cfg.ButtonCount -eq "11") {
            Log "  PASS: PadForge wrote Xbox 360 defaults (stale config was blocked at load)"
        } elseif ($cfg.Preset -eq "Custom" -and $cfg.ButtonCount -eq "77") {
            Log "  FAIL: Stale Custom/77 leaked into saved XML"
        } else {
            Log "  INFO: Unexpected values"
        }
    }
}

# Now set slot 1 to vJoy+created and relaunch for UI verification
$xml2.PadForgeSettings.AppSettings.SlotControllerTypes.ChildNodes[1].InnerText = '2'
$xml2.PadForgeSettings.AppSettings.SlotCreated.ChildNodes[1].InnerText = 'true'
$xml2.Save($padForgeXml)
Log "Set slot 1 to vJoy+created, relaunching..."

Start-Process $padForgeExe
Start-Sleep -Seconds 6

$mainWin2 = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $mainWin2) { Log "FAIL: PadForge not found after relaunch"; exit 1 }

$navCond2 = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Pad2")
$pad2 = $mainWin2.FindFirst($tree::Descendants, $navCond2)
if (-not $pad2) { Log "FAIL: Pad2 not found"; exit 1 }
$sel2 = $pad2.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel2.Select()
Start-Sleep -Seconds 2

$combo2 = $mainWin2.FindFirst($tree::Descendants, $comboCond)
if ($combo2) {
    $selP2 = $combo2.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $sel2Items = $selP2.Current.GetSelection()
    $presetB = if ($sel2Items.Count -gt 0) { $sel2Items[0].Current.Name } else { "NONE" }
    
    $btnBox2 = $mainWin2.FindFirst($tree::Descendants, $btnCond)
    $btnsB = if ($btnBox2) { ($btnBox2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } else { "NOT_FOUND" }
    
    Log "Pad2: preset=$presetB buttons=$btnsB"
    if ($presetB -eq "Xbox 360" -and $btnsB -eq "11") {
        Log "  PASS: Slot 1 got Xbox 360 default (stale Custom/77 was blocked)"
    } elseif ($presetB -eq "Custom" -and $btnsB -eq "77") {
        Log "  FAIL: Stale config leaked! Custom/77 should have been blocked"
    } else {
        Log "  INFO: Got $presetB/$btnsB"
    }
} else {
    Log "FAIL: VJoyPresetCombo not found on Pad2"
}

Log ""
Log "=== All Tests Complete ==="
