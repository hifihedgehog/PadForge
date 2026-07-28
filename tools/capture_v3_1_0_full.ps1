<#
.SYNOPSIS
    Full v3.1.0 screenshot capture: prep PadForge.xml with one slot of
    every type (Xbox/PlayStation/Extended/KBM/MIDI), inject sample
    macros, restart, and capture every page including slot-type-
    specific tabs.
.NOTES
    - Stops PadForge, edits PadForge.xml directly, restarts. Backup
      written to PadForge.xml.bak-capture before each run.
    - Existing UserSettings (DualSense → Xbox slot mapping) is
      preserved so AT/Lighting tabs flip Visible.
#>

$logFile = "C:\PadForge\capture_v3_1_0_full_log.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, int extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    public static void ForceFG(IntPtr h) {
        ShowWindow(h, 5);
        SetForegroundWindow(h);
    }
}
"@

$XmlPath = "C:\PadForge\PadForge.xml"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
$ExePath = "C:\PadForge\PadForge.exe"
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

# ────────── STEP 1: Stop PadForge gracefully ──────────
Write-Host ""; Write-Host "=== STEP 1: Stop PadForge ==="
$proc = Get-Process PadForge -EA SilentlyContinue
if ($proc) {
    # Graceful close so it flushes any in-flight settings
    foreach ($p in $proc) {
        try { $p.CloseMainWindow() | Out-Null } catch {}
    }
    Start-Sleep -Seconds 3
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# ────────── STEP 2: Edit PadForge.xml ──────────
Write-Host ""; Write-Host "=== STEP 2: Edit PadForge.xml ==="
if (-not (Test-Path $XmlPath)) {
    Write-Host "  !! PadForge.xml not found at $XmlPath" -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}
# A re-run after a failed capture used to copy the already-prepped synthetic
# config over the only copy of the real settings. Follow the pattern
# capture_all.ps1 adopted after that destroyed a settings file on 2026-07-12:
# a leftover backup means an earlier run never restored, so it holds the real
# settings and the live file is capture residue. Restore, then re-back up.
if (Test-Path -LiteralPath "$XmlPath.bak-capture") {
    Write-Host "  !! Leftover backup from an interrupted run; restoring it before re-backup" -ForegroundColor Yellow
    Copy-Item -LiteralPath "$XmlPath.bak-capture" -Destination $XmlPath -Force
}
Copy-Item -LiteralPath $XmlPath -Destination "$XmlPath.bak-capture" -Force
Write-Host "  Backed up to $XmlPath.bak-capture"

[xml]$xml = Get-Content $XmlPath
$ns = $xml.PadForgeSettings

# Set first 5 slot types: Xbox, PlayStation, Extended, KBM, MIDI
# (post-reorder dashboard order). Enum: 0=Xbox, 1=PlayStation,
# 2=Extended, 3=Midi, 4=KeyboardMouse.
$desiredTypes = @(0, 1, 2, 4, 3)
$typesNode = $ns.SelectSingleNode("SlotControllerTypes")
$typeChildren = $typesNode.SelectNodes("Type")
for ($i = 0; $i -lt $desiredTypes.Count; $i++) {
    $typeChildren[$i].InnerText = $desiredTypes[$i]
}
Write-Host "  Set first 5 slot types: $(($desiredTypes -join ',')) (Xbox, PS, Extended, KBM, MIDI)"

# SlotCreated true for the first 5 slots
$createdNode = $ns.SelectSingleNode("SlotCreated")
$createdChildren = $createdNode.SelectNodes("Created")
for ($i = 0; $i -lt 5; $i++) { $createdChildren[$i].InnerText = "true" }
Write-Host "  Marked slots 0-4 as Created"

# SlotProfileIds — assign HM profile to each slot type that needs one.
# Xbox slot 0: leave existing (already xbox-series-xs-bt with DualSense)
# PlayStation slot 1: dualsense-bt
# Extended slot 2: padforge-custom (synthetic Custom profile)
# KBM slot 3: nil (KBM doesn't use HM)
# MIDI slot 4: nil (MIDI doesn't use HM)
$profilesNode = $ns.SelectSingleNode("SlotProfileIds")
$profileChildren = $profilesNode.SelectNodes("Id")

function Set-IdNode {
    param($parent, $idx, [string]$value)
    $node = $parent[$idx]
    $node.RemoveAllAttributes() | Out-Null
    $node.InnerText = $value
}
function Set-IdNil {
    param($parent, $idx)
    $node = $parent[$idx]
    $node.InnerText = ""
    $attr = $xml.CreateAttribute("xsi", "nil", "http://www.w3.org/2001/XMLSchema-instance")
    $attr.Value = "true"
    $node.Attributes.Append($attr) | Out-Null
}

# Slot 0 already set to xbox-series-xs-bt — leave it alone
Set-IdNode $profileChildren 1 "dualsense-bt"
Set-IdNode $profileChildren 2 "padforge-custom"
Set-IdNil  $profileChildren 3
Set-IdNil  $profileChildren 4
Write-Host "  Set SlotProfileIds for slots 1-4"

# ────────── Inject sample macros (so the Macros tab has content) ──────────
$macrosNode = $ns.SelectSingleNode("Macros")
if (-not $macrosNode) {
    $macrosNode = $xml.CreateElement("Macros")
    $ns.AppendChild($macrosNode) | Out-Null
}
# Replace any existing macros with the canonical capture set.
$macrosNode.RemoveAll()

$m1 = @'
<Macro PadIndex="0">
  <Name>Quick Combo</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>4096</TriggerButtons>
  <TriggerAxisTargets>LeftTrigger</TriggerAxisTargets>
  <TriggerAxisThreshold>50</TriggerAxisThreshold>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>ButtonPress</Type><ButtonFlags>4096</ButtonFlags><DurationMs>100</DurationMs></Action>
    <Action><Type>Delay</Type><DurationMs>200</DurationMs></Action>
    <Action><Type>KeyPress</Type><KeyCode>32</KeyCode><DurationMs>50</DurationMs></Action>
    <Action><Type>MouseButtonPress</Type><MouseButton>Left</MouseButton><DurationMs>50</DurationMs></Action>
  </Actions>
</Macro>
'@
$m2 = @'
<Macro PadIndex="0">
  <Name>Volume Control</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>Always</TriggerMode>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>SystemVolume</Type><AxisTarget>LeftTrigger</AxisTarget><VolumeLimit>75</VolumeLimit></Action>
    <Action><Type>MouseMove</Type><AxisTarget>RightStickX</AxisTarget><MouseSensitivity>15</MouseSensitivity></Action>
  </Actions>
</Macro>
'@
$frag = $xml.CreateDocumentFragment(); $frag.InnerXml = $m1.Trim()
$macrosNode.AppendChild($frag) | Out-Null
$frag = $xml.CreateDocumentFragment(); $frag.InnerXml = $m2.Trim()
$macrosNode.AppendChild($frag) | Out-Null
Write-Host "  Injected 2 sample macros (Quick Combo, Volume Control)"

# Make sure window is not minimized at launch
$appSettings = $ns.SelectSingleNode("AppSettings")
if ($appSettings) {
    $smNode = $appSettings.SelectSingleNode("StartMinimized")
    if ($smNode) { $smNode.InnerText = "false" }
}

$xml.Save($XmlPath)
Write-Host "  Saved PadForge.xml"

# ────────── STEP 3: Launch PadForge + wait for HM bring-up ──────────
Write-Host ""; Write-Host "=== STEP 3: Launch PadForge ==="
Start-Process $ExePath
# 5 fresh slot bring-ups can take ~8-12 seconds on cold HM start
Write-Host "  Waiting 15s for PadForge + 5-slot HM bring-up..."
Start-Sleep -Seconds 15

$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Host "  !! PadForge didn't launch" -ForegroundColor Red
    Stop-Transcript | Out-Null
    exit 1
}
$hwnd = $proc.MainWindowHandle
Write-Host "  PadForge PID=$($proc.Id) HWND=$hwnd"
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 1

$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if (-not $uiaWin) { Write-Host "  !! UIA window not found" -ForegroundColor Red; exit 1 }

$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants

function FindByAid {
    param([string]$Aid, [System.Windows.Automation.AutomationElement]$Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $where.FindFirst($TD, $cond)
}
function FindByName {
    param([string]$Name, [System.Windows.Automation.ControlType]$CT = $null,
          [System.Windows.Automation.AutomationElement]$Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $nameC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($CT) {
        $ctC = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
        $cond = New-Object System.Windows.Automation.AndCondition($nameC, $ctC)
    } else { $cond = $nameC }
    return $where.FindFirst($TD, $cond)
}
function ClickEl {
    param([System.Windows.Automation.AutomationElement]$El, [string]$Lbl, [int]$Delay = 800)
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl" -ForegroundColor Red; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke()
        Write-Host "  Click '$Lbl' (Invoke)"
    } catch {
        try {
            $sp = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select()
            Write-Host "  Click '$Lbl' (SelectionItem.Select)"
        } catch {
            $r = $El.Current.BoundingRectangle
            $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
            [W32]::ClickAt($x, $y)
            Write-Host "  Click '$Lbl' (coord $x,$y)"
        }
    }
    Start-Sleep -Milliseconds $Delay
    return $true
}
function Cap {
    param([string]$Name)
    [W32]::ForceFG($hwnd)
    Start-Sleep -Milliseconds 400
    $r = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { return }
    try {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
        $g.Dispose()
        $p = Join-Path $OutputDir "$Name.png"
        $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $kb = [math]::Round((Get-Item $p).Length / 1024)
        Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
    } catch {
        Write-Host "  !! Cap failed for $Name : $($_.Exception.Message)" -ForegroundColor Red
    }
}
function Nav {
    param([string]$Name)
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TreeItem)) {
        $el = FindByName -Name $Name -CT $ct
        if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    }
    $el = FindByName -Name $Name
    if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    Write-Host "  !! Nav '$Name' not found" -ForegroundColor Red
    return $false
}
function Tab {
    param([string]$Name)
    $padPage = FindByAid "PadPageView"
    $where = if ($padPage) { $padPage } else { $uiaWin }
    $el = FindByName -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton) -Parent $where
    if ($el) { return ClickEl -El $el -Lbl "Tab:$Name" -Delay 1200 }
    Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
    return $false
}
function Get-SlotCard {
    param([int]$Index)
    Nav "Dashboard"; Start-Sleep -Milliseconds 1000
    $slotsHost = FindByAid "SlotsItemsControl"
    if (-not $slotsHost) { return $null }
    $cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($cards.Count -le $Index) { return $null }
    return $cards[$Index]
}

# ────────── STEP 4: Capture global pages ──────────
Write-Host ""; Write-Host "=== STEP 4: Global pages ==="
Nav "Dashboard"; Start-Sleep -Milliseconds 800; Cap "dashboard"
Nav "Profiles"; Cap "profiles"
Nav "Devices"; Start-Sleep -Milliseconds 800; Cap "devices"
Nav "Settings"; Cap "settings"
# Settings sub-sections via section-header ScrollIntoView
$hidhide = FindByName "HidHide"
if ($hidhide) {
    try { $hidhide.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
    Start-Sleep -Milliseconds 600; Cap "settings-hidhide"
}
$drivers = FindByName "Drivers"
if ($drivers) {
    try { $drivers.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch {}
    Start-Sleep -Milliseconds 600; Cap "settings-drivers"
}
Nav "About"; Cap "about"

# Add Controller popup (open + capture + close)
Nav "Dashboard"; Start-Sleep -Milliseconds 600
$addBtn = FindByName "Add Controller"
if ($addBtn) {
    ClickEl $addBtn -Lbl "Add Controller" -Delay 800 | Out-Null
    Cap "add-controller-popup"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 400
}

# ────────── STEP 5: Slot 0 (Xbox + DualSense) — full PadPage capture ──────────
Write-Host ""; Write-Host "=== STEP 5: Xbox slot — full PadPage tabs ==="
$xboxCard = Get-SlotCard 0
if ($xboxCard) {
    ClickEl $xboxCard -Lbl "Xbox slot card" -Delay 2500 | Out-Null

    $padPage = FindByAid "PadPageView"
    $rbCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)

    # Land on Controller (3D) tab and let HM finish profile load before AT/Lighting flip Visible
    $tabs = $padPage.FindAll($TC, $rbCond)
    if ($tabs.Count -gt 0) { ClickEl $tabs[0] -Lbl "Controller Tab" -Delay 1800 | Out-Null }
    Start-Sleep -Milliseconds 1500
    $tabs = $padPage.FindAll($TC, $rbCond)
    Write-Host "  Tabs visible: $($tabs.Count)"
    for ($ti = 0; $ti -lt $tabs.Count; $ti++) { Write-Host "    [$ti] '$($tabs[$ti].Current.Name)'" }
    Cap "pad-controller-3d"

    # 2D toggle
    $rect = $padPage.Current.BoundingRectangle
    $toggleX = [int]($rect.X + 52); $toggleY = [int]($rect.Y + 124)
    [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
    [W32]::ClickAt($toggleX, $toggleY); Start-Sleep -Milliseconds 800
    Cap "pad-controller-2d"
    [W32]::ClickAt($toggleX, $toggleY); Start-Sleep -Milliseconds 500

    if (Tab "Macros") {
        # Click first macro list item to populate the right-hand action panel
        $padPage = FindByAid "PadPageView"
        $liCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $items = $padPage.FindAll($TC, $liCond)
        if ($items.Count -gt 0) {
            ClickEl $items[0] -Lbl "Macro 0" -Delay 600 | Out-Null
        }
        Start-Sleep -Milliseconds 600; Cap "pad-macros"
    }
    if (Tab "Mappings") { Cap "pad-mappings" }

    if (Tab "Sticks") {
        Cap "pad-sticks"
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 469); Start-Sleep -Milliseconds 800
        Cap "pad-sticks-deadzone-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 1046); Start-Sleep -Milliseconds 800
        Cap "pad-sticks-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300
    }

    if (Tab "Triggers") {
        Cap "pad-triggers"
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
        [W32]::ClickAt(946, 472); Start-Sleep -Milliseconds 800
        Cap "pad-triggers-sensitivity-dropdown"
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300
    }

    if (Tab "Force Feedback") { Cap "pad-forcefeedback" }

    # AT and Lighting need bigger settle time after a tab change for the
    # conditional content to flip visible. Use Tab() then a longer sleep
    # before Cap(), and verify the tab shows the right panel by re-finding
    # the radio button after a sleep — sometimes the click doesn't register
    # the first time on a fresh PadPage realization.
    if (Tab "Adaptive Triggers") {
        Start-Sleep -Milliseconds 1500
        Cap "pad-adaptive-triggers"
    }
    if (Tab "Lighting") {
        Start-Sleep -Milliseconds 1500
        # Defensive re-click in case the first SelectionItem.Select didn't
        # actually swap the panel content.
        $padPage = FindByAid "PadPageView"
        $lightTab = FindByName -Name "Lighting" -CT ([System.Windows.Automation.ControlType]::RadioButton) -Parent $padPage
        if ($lightTab) { ClickEl $lightTab -Lbl "Lighting (re-click)" -Delay 1500 | Out-Null }
        Cap "pad-lighting"
    }
}

# ────────── STEP 6: Slot-type-specific config bars ──────────
Write-Host ""; Write-Host "=== STEP 6: PlayStation / Extended / KBM / MIDI config bars ==="
function Cap-SlotControllerView {
    param([int]$SlotIndex, [string]$OutName)
    $card = Get-SlotCard $SlotIndex
    if (-not $card) { Write-Host "  !! Slot card $SlotIndex not found" -ForegroundColor Yellow; return }
    ClickEl $card -Lbl "Slot $SlotIndex card" -Delay 2500 | Out-Null
    # Land on the Controller (3D) tab
    $padPage = FindByAid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { ClickEl $tabs[0] -Lbl "Controller Tab" -Delay 1500 | Out-Null }
    }
    Start-Sleep -Milliseconds 800
    Cap $OutName
}

Cap-SlotControllerView -SlotIndex 1 -OutName "pad-playstation-configbar"
Cap-SlotControllerView -SlotIndex 2 -OutName "pad-extended-configbar"

# Extended schematic toggle
$padPage = FindByAid "PadPageView"
if ($padPage) {
    $rect = $padPage.Current.BoundingRectangle
    $toggleX = [int]($rect.X + 52); $toggleY = [int]($rect.Y + 124)
    [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
    [W32]::ClickAt($toggleX, $toggleY); Start-Sleep -Milliseconds 800
    Cap "pad-extended-schematic"
    [W32]::ClickAt($toggleX, $toggleY); Start-Sleep -Milliseconds 500
}

Cap-SlotControllerView -SlotIndex 3 -OutName "pad-kbm-preview"
Cap-SlotControllerView -SlotIndex 4 -OutName "pad-midi-configbar"

# ────────── STEP 7: Web controller ──────────
Write-Host ""; Write-Host "=== STEP 7: Web controller ==="
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
if (Test-Path $edge) {
    $landingOut = Join-Path $OutputDir "web-landing.png"
    $p = Start-Process -FilePath $edge -ArgumentList "--headless --disable-gpu --screenshot=`"$landingOut`" --window-size=1280,720 http://localhost:8080/" -PassThru -NoNewWindow
    $p.WaitForExit(15000) | Out-Null
    if (Test-Path $landingOut) { Write-Host "  >> web-landing.png ($([math]::Round((Get-Item $landingOut).Length/1024))KB)" -ForegroundColor Green }
    $ctrlOut = Join-Path $OutputDir "web-controller.png"
    $p = Start-Process -FilePath $edge -ArgumentList "--headless --disable-gpu --screenshot=`"$ctrlOut`" --window-size=1280,720 http://localhost:8080/controller.html?layout=xbox360" -PassThru -NoNewWindow
    $p.WaitForExit(15000) | Out-Null
    if (Test-Path $ctrlOut) { Write-Host "  >> web-controller.png ($([math]::Round((Get-Item $ctrlOut).Length/1024))KB)" -ForegroundColor Green }
}

Write-Host ""; Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
