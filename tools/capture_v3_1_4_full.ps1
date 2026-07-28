<#
.SYNOPSIS
    Full v3.1.4 screenshot capture in one atomic run. Produces every PNG
    used by the wiki, README, and padforge.org:

      Top-level:  dashboard, profiles, devices, settings, about
      Slot 0 (Xbox Series + DualSense):
                  pad-controller-3d, pad-macros, pad-mappings,
                  pad-sticks (+deadzone/sensitivity dropdowns),
                  pad-triggers (+sensitivity dropdown),
                  pad-forcefeedback, pad-adaptive-triggers, pad-lighting
      Slot 1 (PlayStation): pad-playstation-configbar
      Slot 2 (Extended):    pad-extended-configbar, pad-extended-schematic
      Slot 3 (KbM):         pad-kbm-preview
      Slot 4 (MIDI):        pad-midi-configbar
      Popup:                add-controller-popup
      Settings:             settings, settings-hidhide, settings-drivers
      2D pass:              pad-controller-2d (XML-flip approach)
      Web controller:       web-landing, web-controller (Edge headless,
                            1280x720 to match the existing assets)

    Restores the original PadForge.xml at the end.
.NOTES
    Run elevated. mouse_event data parameter is int (not uint) so wheel
    deltas of -120 work for scroll-down.
#>

$logFile = "C:\PadForge\capture_v3_1_4_full_log.txt"
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
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, int data, int extra);
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
        SetCursorPos(x, y); System.Threading.Thread.Sleep(80);
        mouse_event(0x02, 0, 0, 0, 0); System.Threading.Thread.Sleep(80);
        mouse_event(0x04, 0, 0, 0, 0);
    }
    public static void WheelAt(int x, int y, int delta) {
        SetCursorPos(x, y); System.Threading.Thread.Sleep(40);
        mouse_event(0x0800, 0, 0, delta, 0);
    }
    public static void ForceFG(IntPtr h) {
        ShowWindow(h, 3); SwitchToThisWindow(h, true);
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
        BringWindowToTop(h); SetForegroundWindow(h);
        AttachThreadInput(myTid, fgTid, false);
        AttachThreadInput(myTid, targetTid, false);
    }
}
"@

$XmlPath = "C:\PadForge\PadForge.xml"
$XmlBak = "$XmlPath.cap-full-bak"
$ExePath = "C:\PadForge\PadForge.exe"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
$EdgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

# ─── Step 0: backup XML ───
Write-Host "=== STEP 0: Backup XML ==="
Copy-Item $XmlPath $XmlBak -Force

# ─── Step 1: stop PadForge ───
Write-Host "=== STEP 1: Stop PadForge ==="
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 4

# ─── Step 2: XML setup (slots 1-4 + EnableWebController=true) ───
Write-Host "=== STEP 2: XML setup (slots 1-4, web server, 3D mode) ==="
function LoadXml { return [xml](Get-Content $XmlPath -Encoding UTF8) }
function SaveXml { param($x) $x.Save($XmlPath) }
function SetSlotType { param($root, [int]$idx, [int]$typeVal)
    $c = $root.AppSettings.SlotControllerTypes.ChildNodes
    if ($idx -lt $c.Count) { $c[$idx].InnerText = "$typeVal" }
}
function SetSlotCreated { param($root, [int]$idx, [bool]$val)
    $c = $root.AppSettings.SlotCreated.ChildNodes
    if ($idx -lt $c.Count) { $c[$idx].InnerText = if ($val) { "true" } else { "false" } }
}
function SetSlotProfileId { param($xml, $root, [int]$idx, [string]$id)
    $c = $root.AppSettings.SlotProfileIds.ChildNodes
    if ($idx -lt $c.Count) {
        $el = $c[$idx]
        $nilAttr = $el.Attributes["xsi:nil"]
        if ($id) {
            if ($nilAttr) { $el.Attributes.Remove($nilAttr) | Out-Null }
            $el.InnerText = $id
        }
    }
}
function SetSlotOrder { param($xml, $root, [string]$el, [int[]]$pi)
    $o = $root.AppSettings.SelectSingleNode($el); if (-not $o) { return }
    $o.RemoveAll() | Out-Null
    foreach ($p in $pi) { $e = $xml.CreateElement("PadIndex"); $e.InnerText = "$p"; $o.AppendChild($e) | Out-Null }
}
function SetUse2D { param($root, [bool]$on)
    $n = $root.AppSettings.SelectSingleNode("Use2DControllerView")
    if ($n) { $n.InnerText = if ($on) { "true" } else { "false" } }
}
function SetEnableWeb { param($root, [bool]$on)
    $n = $root.AppSettings.SelectSingleNode("EnableWebController")
    if ($n) { $n.InnerText = if ($on) { "true" } else { "false" } }
}

$xml = LoadXml; $root = $xml.PadForgeSettings
SetSlotType $root 1 1; SetSlotCreated $root 1 $true; SetSlotProfileId $xml $root 1 "dualsense"
SetSlotType $root 2 2; SetSlotCreated $root 2 $true
SetSlotType $root 3 4; SetSlotCreated $root 3 $true
SetSlotType $root 4 3; SetSlotCreated $root 4 $true
SetSlotOrder $xml $root "PlayStationSlotOrder" @(1)
SetSlotOrder $xml $root "ExtendedSlotOrder" @(2)
SetSlotOrder $xml $root "KeyboardMouseSlotOrder" @(3)
SetSlotOrder $xml $root "MidiSlotOrder" @(4)
SetUse2D $root $false
SetEnableWeb $root $true
SaveXml $xml

# ─── Step 3: launch PadForge + UIA ───
function LaunchAndAttach {
    Start-Process $ExePath
    Start-Sleep -Seconds 10
    $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    if (-not $proc) { Write-Host "!! PadForge failed to start"; throw }
    $hwnd = $proc.MainWindowHandle
    for ($w = 0; $w -lt 15 -and $hwnd -eq 0; $w++) { Start-Sleep -Seconds 1; $proc.Refresh(); $hwnd = $proc.MainWindowHandle }
    [W32]::ShowWindow($hwnd, 3) | Out-Null
    Start-Sleep -Seconds 3
    [W32]::ForceFG($hwnd) | Out-Null
    Start-Sleep -Seconds 3
    return @{ Proc = $proc; Hwnd = $hwnd }
}
function AttachUia { param($proc)
    $TC = [System.Windows.Automation.TreeScope]::Children
    $uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
    $pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
    $pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
    $win = $null
    for ($t = 0; $t -lt 20 -and -not $win; $t++) {
        $win = $uiaRoot.FindFirst($TC, $pidCond)
        if (-not $win) { Start-Sleep -Seconds 1 }
    }
    return $win
}

Write-Host "=== STEP 3: Launch PadForge ==="
$r = LaunchAndAttach
$proc = $r.Proc; $hwnd = $r.Hwnd
Write-Host "  PID=$($proc.Id) HWND=$hwnd"

$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants
$uiaWin = AttachUia $proc
if (-not $uiaWin) { Write-Host "!! UIA window not found"; Stop-Transcript | Out-Null; exit 1 }
Write-Host "  UIA window attached"

function FindByAid { param([string]$Aid, $Parent = $null)
    $where = if ($Parent) { $Parent } else { $script:uiaWin }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $where.FindFirst($TD, $cond)
}
function FindByName { param([string]$Name, $CT = $null, $Parent = $null)
    $where = if ($Parent) { $Parent } else { $script:uiaWin }
    $nC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($CT) {
        $tC = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
        $cond = New-Object System.Windows.Automation.AndCondition($nC, $tC)
    } else { $cond = $nC }
    return $where.FindFirst($TD, $cond)
}
function ClickEl { param($El, [string]$Lbl, [int]$Delay = 800)
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl" -ForegroundColor Yellow; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke(); Write-Host "  Click '$Lbl' (Invoke)"
    } catch {
        try {
            $sp = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select(); Write-Host "  Click '$Lbl' (SelItem)"
        } catch {
            $rc = $El.Current.BoundingRectangle
            $x = [int]($rc.X + $rc.Width / 2); $y = [int]($rc.Y + $rc.Height / 2)
            [W32]::ClickAt($x, $y); Write-Host "  Click '$Lbl' (coord $x,$y)"
        }
    }
    Start-Sleep -Milliseconds $Delay; return $true
}
function Cap { param([string]$Name, [bool]$KeepCursor = $false)
    [W32]::ForceFG($hwnd)
    if (-not $KeepCursor) { [W32]::SetCursorPos(200, 1000) | Out-Null }
    Start-Sleep -Milliseconds 700
    $rc = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
    $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
    if ($w -le 0 -or $h -le 0) { Write-Host "  !! bad rect"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rc.L, $rc.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $p = Join-Path $OutputDir "$Name.png"
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $kb = [math]::Round((Get-Item $p).Length / 1024)
    Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
}
function Nav { param([string]$Name)
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TreeItem)) {
        $el = FindByName -Name $Name -CT $ct
        if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" -Delay 1000 }
    }
    $el = FindByName -Name $Name
    if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" -Delay 1000 }
    return $false
}
function Tab { param([string]$Name)
    # Tabs are RadioButtons: SelectionItem.Select doesn't fire the Click
    # handler the codebehind needs to switch the page. Drive via coord.
    $aid = "Tab" + ($Name -replace ' ','')
    $el = FindByAid $aid
    if (-not $el) {
        $padPage = FindByAid "PadPageView"
        $where = if ($padPage) { $padPage } else { $script:uiaWin }
        $el = FindByName -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton) -Parent $where
    }
    if (-not $el) { Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow; return $false }
    [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
    $rc = $el.Current.BoundingRectangle
    $x = [int]($rc.X + $rc.Width / 2); $y = [int]($rc.Y + $rc.Height / 2)
    [W32]::ClickAt($x, $y)
    Write-Host "  Tab '$Name' (coord $x,$y)"
    Start-Sleep -Milliseconds 1000
    return $true
}
function SelectSlot { param([int]$idx, [string]$lbl)
    Nav "Dashboard" | Out-Null
    Start-Sleep -Milliseconds 800
    $sh = FindByAid "SlotsItemsControl"
    if (-not $sh) { Write-Host "  !! SlotsItemsControl missing"; return $false }
    $cards = $sh.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($idx -ge $cards.Count) { Write-Host "  !! only $($cards.Count) cards, want [$idx]"; return $false }
    $card = $cards[$idx]
    try { $card.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 400 } catch {}
    Write-Host "  $($cards.Count) cards; selecting [$idx] for $lbl"
    ClickEl $card -Lbl "$lbl card" -Delay 3000 | Out-Null
    return $true
}

# ─── Step 4: top-level ───
Write-Host "=== STEP 4: Top-level pages ==="
Nav "Dashboard"; Start-Sleep -Milliseconds 800; Cap "dashboard"
Nav "Profiles";  Start-Sleep -Milliseconds 600; Cap "profiles"
Nav "Devices";   Start-Sleep -Milliseconds 800; Cap "devices"
Nav "About";     Start-Sleep -Milliseconds 600; Cap "about"

# ─── Step 5: slot 0 ───
Write-Host "=== STEP 5: Slot 0 (Xbox Series) ==="
if (SelectSlot 0 "Xbox Series") {
    Tab "Controller" | Out-Null; Start-Sleep -Milliseconds 600; Cap "pad-controller-3d"
    Tab "Macros"     | Out-Null; Start-Sleep -Milliseconds 600; Cap "pad-macros"
    Tab "Mappings"   | Out-Null; Start-Sleep -Milliseconds 600; Cap "pad-mappings"
    if (Tab "Sticks") {
        Start-Sleep -Milliseconds 600
        $pp = (FindByAid "PadPageView").Current.BoundingRectangle
        [W32]::SetCursorPos([int]($pp.X + 800), [int]($pp.Y + 800))
        Start-Sleep -Milliseconds 100
        for ($i = 0; $i -lt 20; $i++) { [W32]::WheelAt([int]($pp.X + 800), [int]($pp.Y + 800), 120); Start-Sleep -Milliseconds 30 }
        Start-Sleep -Milliseconds 500
        Cap "pad-sticks"
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 560)); Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-deadzone-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 900)); Start-Sleep -Milliseconds 1000
        Cap "pad-sticks-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
    }
    if (Tab "Triggers") {
        Start-Sleep -Milliseconds 600
        $pp = (FindByAid "PadPageView").Current.BoundingRectangle
        for ($i = 0; $i -lt 12; $i++) { [W32]::WheelAt([int]($pp.X + 800), [int]($pp.Y + 800), 120); Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 500
        Cap "pad-triggers"
        [W32]::ClickAt([int]($pp.X + 455), [int]($pp.Y + 550)); Start-Sleep -Milliseconds 1000
        Cap "pad-triggers-sensitivity-dropdown" -KeepCursor $true
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400
    }
    Tab "Force Feedback"    | Out-Null; Start-Sleep -Milliseconds 600; Cap "pad-forcefeedback"
    Tab "Adaptive Triggers" | Out-Null; Start-Sleep -Milliseconds 800; Cap "pad-adaptive-triggers"
    Tab "Lighting"          | Out-Null; Start-Sleep -Milliseconds 800; Cap "pad-lighting"
}

# ─── Step 6-9: slots 1-4 ───
Write-Host "=== STEP 6: Slot 1 (PlayStation) ==="
if (SelectSlot 1 "PlayStation") {
    Tab "Controller" | Out-Null; Start-Sleep -Milliseconds 800; Cap "pad-playstation-configbar"
}
Write-Host "=== STEP 7: Slot 2 (Extended) ==="
if (SelectSlot 2 "Extended") {
    Tab "Controller" | Out-Null; Start-Sleep -Milliseconds 800
    Cap "pad-extended-configbar"
    Cap "pad-extended-schematic"
}
Write-Host "=== STEP 8: Slot 3 (KbM) ==="
if (SelectSlot 3 "KbM") { Start-Sleep -Milliseconds 800; Cap "pad-kbm-preview" }
Write-Host "=== STEP 9: Slot 4 (MIDI) ==="
if (SelectSlot 4 "MIDI") {
    Tab "Controller" | Out-Null; Start-Sleep -Milliseconds 800; Cap "pad-midi-configbar"
}

# ─── Step 10: Add Controller popup ───
Write-Host "=== STEP 10: Add Controller popup ==="
Nav "Dashboard"; Start-Sleep -Milliseconds 800
$tb = FindByName "Add Controller"
$target = $null
if ($tb) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $cur = $tb
    for ($d = 0; $d -lt 5 -and $cur; $d++) {
        $cur = $walker.GetParent($cur)
        if ($cur -and $cur.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane) {
            $target = $cur; break
        }
    }
    if (-not $target) { $target = $tb }
}
if ($target) {
    $rc = $target.Current.BoundingRectangle
    $x = [int]($rc.X + $rc.Width / 2); $y = [int]($rc.Y + $rc.Height / 2)
    [W32]::ClickAt($x, $y); Start-Sleep -Milliseconds 1500
    Cap "add-controller-popup"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 500
} else {
    Write-Host "  !! Add Controller card not located"
}

# ─── Step 11: Settings sub-views with proper scrolling ───
Write-Host "=== STEP 11: Settings sub-views ==="
Nav "Settings"; Start-Sleep -Milliseconds 800
$rc = New-Object W32+RECT
[W32]::GetWindowRect($hwnd, [ref]$rc) | Out-Null
$cx = [int](($rc.L + $rc.R) / 2); $cy = [int](($rc.T + $rc.B) / 2)
# Scroll all the way up
for ($i = 0; $i -lt 30; $i++) { [W32]::WheelAt($cx, $cy, 120); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 700
Cap "settings"
# Scroll to HidHide section
for ($i = 0; $i -lt 8; $i++) { [W32]::WheelAt($cx, $cy, -120); Start-Sleep -Milliseconds 40 }
Start-Sleep -Milliseconds 700
Cap "settings-hidhide"
# Scroll further to Drivers section
for ($i = 0; $i -lt 12; $i++) { [W32]::WheelAt($cx, $cy, -120); Start-Sleep -Milliseconds 40 }
Start-Sleep -Milliseconds 700
Cap "settings-drivers"

# ─── Step 12: 2D pass — flip XML, restart, capture pad-controller-2d ───
Write-Host "=== STEP 12: Flip XML to 2D, recapture pad-controller-2d ==="
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 4
$xml2 = LoadXml; $root2 = $xml2.PadForgeSettings
SetUse2D $root2 $true
SaveXml $xml2

$r2 = LaunchAndAttach
$proc = $r2.Proc; $hwnd = $r2.Hwnd
Write-Host "  Restarted PID=$($proc.Id) HWND=$hwnd"
$uiaWin = AttachUia $proc
if (-not $uiaWin) { Write-Host "  !! Step 12 UIA window not found"; Stop-Transcript | Out-Null; exit 1 }

if (SelectSlot 0 "Xbox Series 2D") {
    Tab "Controller" | Out-Null; Start-Sleep -Milliseconds 800
    Cap "pad-controller-2d"
}

# ─── Step 13: web controller via Edge headless ───
Write-Host "=== STEP 13: Web controller (Edge headless 1280x720) ==="
$port = 8080
$xml3 = LoadXml; $root3 = $xml3.PadForgeSettings
$portNode = $root3.AppSettings.SelectSingleNode("WebControllerPort")
if ($portNode -and $portNode.InnerText) { $port = [int]$portNode.InnerText }
Write-Host "  Web server port: $port"
# PadForge restarted at Step 12 with EnableWebController=true; give it a beat
Start-Sleep -Seconds 3

$landingPng = Join-Path $OutputDir "web-landing.png"
$ctrlPng    = Join-Path $OutputDir "web-controller.png"

& $EdgePath --headless=new --disable-gpu "--screenshot=$landingPng" --window-size=1280,720 "http://localhost:$port/" 2>&1 | Out-Null
Start-Sleep -Seconds 2
& $EdgePath --headless=new --disable-gpu "--screenshot=$ctrlPng" --window-size=1280,720 "http://localhost:$port/controller.html?layout=xbox360" 2>&1 | Out-Null
Start-Sleep -Seconds 2

if (Test-Path $landingPng) { Write-Host "  >> web-landing.png ($([math]::Round((Get-Item $landingPng).Length/1024))KB)" -ForegroundColor Green }
else { Write-Host "  !! web-landing.png not produced" }
if (Test-Path $ctrlPng) { Write-Host "  >> web-controller.png ($([math]::Round((Get-Item $ctrlPng).Length/1024))KB)" -ForegroundColor Green }
else { Write-Host "  !! web-controller.png not produced" }

# ─── Step 14: restore original XML ───
Write-Host "=== STEP 14: Restore original XML ==="
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 4
Copy-Item $XmlBak $XmlPath -Force
Write-Host "  Restored. Re-launching PadForge..."
Start-Process $ExePath
Start-Sleep -Seconds 5

Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
