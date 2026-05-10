<#
.SYNOPSIS
    v3.1.4 release capture. Additively sets up slots 1-4 (PlayStation,
    Extended, KbM, MIDI) on top of the user's existing slot 0 (Xbox
    Series + DualSense), captures every wiki/repo screenshot, then
    restores the original PadForge.xml. The user's manual slot 0
    setup is preserved end-to-end.
.NOTES
    Run elevated. PadForge runs elevated for vJoy/HM auto-elevation,
    so UIA needs the same.
#>

$logFile = "C:\PadForge\capture_v3_1_4_log.txt"
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
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    public static void ForceFG(IntPtr h) {
        ShowWindow(h, 3);
        SwitchToThisWindow(h, true);
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        IntPtr fg = GetForegroundWindow();
        if (fg == h) return;
        uint pidTmp;
        uint fgTid = GetWindowThreadProcessId(fg, out pidTmp);
        uint targetTid = GetWindowThreadProcessId(h, out pidTmp);
        uint myTid = GetCurrentThreadId();
        AttachThreadInput(myTid, fgTid, true);
        AttachThreadInput(myTid, targetTid, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$XmlPath = "C:\PadForge\PadForge.xml"
$XmlBak = "$XmlPath.cap-bak"
$ExePath = "C:\PadForge\PadForge.exe"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

# ---------- Step 0: backup XML ----------
Write-Host "=== STEP 0: Backup XML ===" -ForegroundColor Cyan
Copy-Item $XmlPath $XmlBak -Force
Write-Host "  $XmlPath -> $XmlBak"

# ---------- Step 1: stop PadForge so we can edit XML ----------
Write-Host ""; Write-Host "=== STEP 1: Stop PadForge ===" -ForegroundColor Cyan
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
$still = Get-Process PadForge -EA SilentlyContinue
if ($still) {
    Write-Host "  Still running, killing again..."
    $still | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# ---------- Step 2: additively set up slots 1-4 in XML ----------
Write-Host ""; Write-Host "=== STEP 2: Inject slots 1-4 ===" -ForegroundColor Cyan
[xml]$xml = Get-Content $XmlPath -Encoding UTF8
$root = $xml.PadForgeSettings
if (-not $root) { $root = $xml.DocumentElement }

# Slot type ints: 0=Xbox, 1=PlayStation, 2=Extended, 3=Midi, 4=KeyboardMouse
# Profile IDs by slot:
#   slot 1 (PlayStation) → "dualsense"
#   slot 2 (Extended)   → null (Custom default)
#   slot 3 (KbM)        → null
#   slot 4 (MIDI)       → null
function SetSlotType {
    param([int]$idx, [int]$typeVal)
    $node = $root.AppSettings.SlotControllerTypes
    $children = $node.ChildNodes
    if ($idx -lt $children.Count) { $children[$idx].InnerText = "$typeVal" }
}
function SetSlotCreated {
    param([int]$idx, [bool]$val)
    $node = $root.AppSettings.SlotCreated
    $children = $node.ChildNodes
    if ($idx -lt $children.Count) { $children[$idx].InnerText = if ($val) { "true" } else { "false" } }
}
function SetSlotProfileId {
    param([int]$idx, [string]$id)
    $node = $root.AppSettings.SlotProfileIds
    $children = $node.ChildNodes
    if ($idx -lt $children.Count) {
        $el = $children[$idx]
        $nilAttr = $el.Attributes["xsi:nil"]
        if ($id) {
            if ($nilAttr) { $el.Attributes.Remove($nilAttr) | Out-Null }
            $el.InnerText = $id
        }
    }
}

SetSlotType 1 1; SetSlotCreated 1 $true; SetSlotProfileId 1 "dualsense"
SetSlotType 2 2; SetSlotCreated 2 $true
SetSlotType 3 4; SetSlotCreated 3 $true
SetSlotType 4 3; SetSlotCreated 4 $true

# Slot-order lists. PlayStation/Extended/KbM/MIDI need their PadIndex
# in the corresponding *SlotOrder element so the Dashboard groups
# render them in the right type group.
function SetSlotOrder {
    param([string]$elementName, [int[]]$padIndices)
    $orderEl = $root.AppSettings.SelectSingleNode($elementName)
    if (-not $orderEl) { return }
    # Clear existing children
    $orderEl.RemoveAll() | Out-Null
    foreach ($pi in $padIndices) {
        $piEl = $xml.CreateElement("PadIndex")
        $piEl.InnerText = "$pi"
        $orderEl.AppendChild($piEl) | Out-Null
    }
}
SetSlotOrder "PlayStationSlotOrder" @(1)
SetSlotOrder "ExtendedSlotOrder" @(2)
SetSlotOrder "KeyboardMouseSlotOrder" @(3)
SetSlotOrder "MidiSlotOrder" @(4)

# Assign DualSense to PlayStation slot 1 too (so AT/Lighting tabs show
# there if needed). The DualSense Device entry is already in <Devices>;
# we just add a UserSetting that maps it to slot 1 with its own
# PadSettingChecksum referencing the existing PadSetting.
$existingUS = $root.UserSettings.SelectSingleNode("Setting")
if ($existingUS) {
    $dsGuid = $existingUS.InstanceGuid
    $dsProductGuid = $existingUS.ProductGuid
    $dsChecksum = $existingUS.PadSettingChecksum
    # Create a clone for slot 1
    $clone = $existingUS.Clone()
    $clone.MapTo = "1"
    $root.UserSettings.AppendChild($clone) | Out-Null
    Write-Host "  Cloned UserSetting for DualSense -> slot 1"
}

$xml.Save($XmlPath)
Write-Host "  Saved XML with slots 0-4 wired" -ForegroundColor Green

# ---------- Step 3: restart PadForge, wait for window ----------
Write-Host ""; Write-Host "=== STEP 3: Restart PadForge ===" -ForegroundColor Cyan
Start-Process $ExePath
Start-Sleep -Seconds 8
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Host "  !! PadForge didn't start" -ForegroundColor Red
    Stop-Transcript | Out-Null
    Copy-Item $XmlBak $XmlPath -Force
    exit 1
}
$hwnd = $proc.MainWindowHandle
for ($w = 0; $w -lt 12 -and $hwnd -eq 0; $w++) {
    Start-Sleep -Seconds 1
    $proc.Refresh()
    $hwnd = $proc.MainWindowHandle
}
Write-Host "  PadForge PID=$($proc.Id) HWND=$hwnd"
[W32]::ShowWindow($hwnd, 3) | Out-Null
Start-Sleep -Seconds 2
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 2

# Wire up UIA root + window
$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants
$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
if (-not $uiaWin) {
    Write-Host "  !! UIA window not found" -ForegroundColor Red
    Stop-Transcript | Out-Null
    Copy-Item $XmlBak $XmlPath -Force
    exit 1
}

# ---------- UIA helpers ----------
function FindByAid {
    param([string]$Aid, [System.Windows.Automation.AutomationElement]$Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $where.FindFirst($TD, $cond)
}
function FindByName {
    param([string]$Name, $CT = $null,
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
            $x = [int]($r.X + $r.Width / 2)
            $y = [int]($r.Y + $r.Height / 2)
            [W32]::ClickAt($x, $y)
            Write-Host "  Click '$Lbl' (coord $x,$y)"
        }
    }
    Start-Sleep -Milliseconds $Delay
    return $true
}
function Cap {
    param([string]$Name, [bool]$KeepCursor = $false)
    [W32]::ForceFG($hwnd)
    if (-not $KeepCursor) { [W32]::SetCursorPos(200, 1000) | Out-Null }
    Start-Sleep -Milliseconds 600
    $r = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { Write-Host "  !! bad rect ${w}x${h}" -ForegroundColor Red; return }
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
    if (-not $el) {
        Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
        return $false
    }
    [W32]::ForceFG($hwnd)
    Start-Sleep -Milliseconds 200
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2)
    $y = [int]($r.Y + $r.Height / 2)
    [W32]::ClickAt($x, $y)
    Write-Host "  Tab '$Name' (coord $x,$y)"
    Start-Sleep -Milliseconds 800
    return $true
}
function SelectSlot {
    param([int]$idx, [string]$lbl, [int]$delay = 2500)
    Nav "Dashboard"; Start-Sleep -Milliseconds 800
    $slotsHost = FindByAid "SlotsItemsControl"
    if (-not $slotsHost) { Write-Host "  !! SlotsItemsControl not found" -ForegroundColor Red; return $false }
    $cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($idx -ge $cards.Count) {
        Write-Host "  !! Only $($cards.Count) slot cards on Dashboard, wanted [$idx]" -ForegroundColor Red
        return $false
    }
    Write-Host "  $($cards.Count) slot card(s); selecting [$idx] for $lbl"
    ClickEl $cards[$idx] -Lbl "$lbl card" -Delay $delay | Out-Null
    return $true
}

# ---------- STEP 4: Capture top-level pages ----------
Write-Host ""; Write-Host "=== STEP 4: Top-level pages ===" -ForegroundColor Cyan
Nav "Dashboard"; Start-Sleep -Milliseconds 800; Cap "dashboard"
Nav "Profiles"; Start-Sleep -Milliseconds 600; Cap "profiles"
Nav "Devices"; Start-Sleep -Milliseconds 800; Cap "devices"
Nav "Settings"; Start-Sleep -Milliseconds 600; Cap "settings"
Nav "About"; Start-Sleep -Milliseconds 600; Cap "about"

# ---------- STEP 5: Slot 0 (Xbox Series + DualSense) - full per-pad ----------
Write-Host ""; Write-Host "=== STEP 5: Slot 0 (Xbox Series) per-pad ===" -ForegroundColor Cyan
if (SelectSlot 0 "Xbox Series" 3000) {
    if (Tab "Controller") { Start-Sleep -Milliseconds 600; Cap "pad-controller-3d" }
    # 2D toggle (top-left of PadPage)
    $pp = (FindByAid "PadPageView").Current.BoundingRectangle
    $tx = [int]($pp.X + 52); $ty = [int]($pp.Y + 124)
    [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 700
    Cap "pad-controller-2d"
    [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 500

    if (Tab "Macros") { Start-Sleep -Milliseconds 600; Cap "pad-macros" }
    if (Tab "Mappings") { Start-Sleep -Milliseconds 600; Cap "pad-mappings" }
    if (Tab "Sticks") {
        Start-Sleep -Milliseconds 600
        # Scroll Sticks ScrollViewer to top
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        Start-Sleep -Milliseconds 100
        for ($i = 0; $i -lt 20; $i++) { [W32]::mouse_event(0x0800, 0, 0, 120, 0); Start-Sleep -Milliseconds 30 }
        Start-Sleep -Milliseconds 500
        Cap "pad-sticks"
        # Deadzone shape combo at PadPage.X+455, Y+560 (approx)
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 560)); Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-deadzone-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
        # Sensitivity preset combo lower
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 900)); Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
    }
    if (Tab "Triggers") {
        Start-Sleep -Milliseconds 600
        $pp = (FindByAid "PadPageView").Current.BoundingRectangle
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        for ($i = 0; $i -lt 12; $i++) { [W32]::mouse_event(0x0800, 0, 0, 120, 0); Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 500
        Cap "pad-triggers"
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 550)); Start-Sleep -Milliseconds 1000
        Cap "pad-triggers-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
    }
    if (Tab "Force Feedback") { Start-Sleep -Milliseconds 600; Cap "pad-forcefeedback" }
    if (Tab "Adaptive Triggers") { Start-Sleep -Milliseconds 800; Cap "pad-adaptive-triggers" }
    if (Tab "Lighting") { Start-Sleep -Milliseconds 800; Cap "pad-lighting" }
}

# ---------- STEP 6: Slot 1 (PlayStation) ----------
Write-Host ""; Write-Host "=== STEP 6: Slot 1 (PlayStation) ===" -ForegroundColor Cyan
if (SelectSlot 1 "PlayStation" 3000) {
    if (Tab "Controller") { Start-Sleep -Milliseconds 600; Cap "pad-playstation-configbar" }
}

# ---------- STEP 7: Slot 2 (Extended) ----------
Write-Host ""; Write-Host "=== STEP 7: Slot 2 (Extended) ===" -ForegroundColor Cyan
if (SelectSlot 2 "Extended" 3000) {
    if (Tab "Controller") {
        Start-Sleep -Milliseconds 800
        Cap "pad-extended-configbar"
        # Toggle 2D
        $pp = (FindByAid "PadPageView").Current.BoundingRectangle
        $tx = [int]($pp.X + 52); $ty = [int]($pp.Y + 124)
        [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 700
        Cap "pad-extended-schematic"
        [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 500
    }
}

# ---------- STEP 8: Slot 3 (KbM) ----------
Write-Host ""; Write-Host "=== STEP 8: Slot 3 (KbM) ===" -ForegroundColor Cyan
if (SelectSlot 3 "KbM" 3000) {
    Start-Sleep -Milliseconds 800
    Cap "pad-kbm-preview"
}

# ---------- STEP 9: Slot 4 (MIDI) ----------
Write-Host ""; Write-Host "=== STEP 9: Slot 4 (MIDI) ===" -ForegroundColor Cyan
if (SelectSlot 4 "MIDI" 3000) {
    if (Tab "Controller") { Start-Sleep -Milliseconds 800; Cap "pad-midi-configbar" }
}

# ---------- STEP 10: Add Controller popup ----------
Write-Host ""; Write-Host "=== STEP 10: Add Controller popup ===" -ForegroundColor Cyan
Nav "Dashboard"; Start-Sleep -Milliseconds 800
$addBtn = FindByName "Add Controller" -CT ([System.Windows.Automation.ControlType]::Button)
if (-not $addBtn) { $addBtn = FindByAid "AddControllerButton" }
if ($addBtn) {
    ClickEl $addBtn -Lbl "Add Controller" -Delay 1500 | Out-Null
    Cap "add-controller-popup"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 500
} else {
    Write-Host "  !! Add Controller button not found" -ForegroundColor Yellow
}

# ---------- STEP 11: Settings sub-views ----------
Write-Host ""; Write-Host "=== STEP 11: Settings tabs ===" -ForegroundColor Cyan
Nav "Settings"; Start-Sleep -Milliseconds 600
# Scroll to top
$pageRoot = FindByAid "SettingsPageView"
if (-not $pageRoot) { $pageRoot = $uiaWin }
$prRect = $pageRoot.Current.BoundingRectangle
[W32]::SetCursorPos([int]($prRect.X + 800), [int]($prRect.Y + 600))
for ($i = 0; $i -lt 30; $i++) { [W32]::mouse_event(0x0800, 0, 0, 120, 0); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 500
Cap "settings"
# Scroll mid for HidHide
for ($i = 0; $i -lt 10; $i++) { [W32]::mouse_event(0x0800, 0, 0, -120, 0); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 600
Cap "settings-hidhide"
# Scroll to bottom for drivers
for ($i = 0; $i -lt 20; $i++) { [W32]::mouse_event(0x0800, 0, 0, -120, 0); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 600
Cap "settings-drivers"

Write-Host ""; Write-Host "=== DONE - XML preserved (slot 0 untouched) ===" -ForegroundColor Green

# ---------- Step 12: restore the original XML ----------
Write-Host ""; Write-Host "=== STEP 12: Restore XML from $XmlBak ===" -ForegroundColor Cyan
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
Copy-Item $XmlBak $XmlPath -Force
Write-Host "  Restored. Re-launching PadForge..."
Start-Process $ExePath
Start-Sleep -Seconds 4

Stop-Transcript | Out-Null
