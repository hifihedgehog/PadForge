<#
.SYNOPSIS
    Follow-up capture for the screens capture_v3_1_4.ps1 missed because
    the tabs use AutomationId (TabController) rather than a UIA Name.

    Captures (overwrites in PadForge.wiki/images):
      - pad-controller-3d.png        (slot 0 Xbox Series)
      - pad-playstation-configbar.png(slot 1)
      - pad-extended-configbar.png   (slot 2)
      - pad-extended-schematic.png   (slot 2 2D)
      - pad-midi-configbar.png       (slot 4)
      - add-controller-popup.png

    Re-injects slots 1-4 into the XML, restarts PadForge, captures, and
    restores the original XML at the end. Slot 0 is left untouched.
.NOTES
    Run elevated.
#>

$logFile = "C:\PadForge\capture_v3_1_4_fix_log.txt"
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
$XmlBak = "$XmlPath.cap-bak2"
$ExePath = "C:\PadForge\PadForge.exe"
$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"

# Step 0: backup
Write-Host "=== STEP 0: Backup XML ==="
Copy-Item $XmlPath $XmlBak -Force

# Step 1: stop PadForge
Write-Host "=== STEP 1: Stop PadForge ==="
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3

# Step 2: inject slots 1-4 (same as main script)
Write-Host "=== STEP 2: Inject slots 1-4 ==="
[xml]$xml = Get-Content $XmlPath -Encoding UTF8
$root = $xml.PadForgeSettings
function SetSlotType { param([int]$idx, [int]$typeVal)
    $c = $root.AppSettings.SlotControllerTypes.ChildNodes
    if ($idx -lt $c.Count) { $c[$idx].InnerText = "$typeVal" }
}
function SetSlotCreated { param([int]$idx, [bool]$val)
    $c = $root.AppSettings.SlotCreated.ChildNodes
    if ($idx -lt $c.Count) { $c[$idx].InnerText = if ($val) { "true" } else { "false" } }
}
function SetSlotProfileId { param([int]$idx, [string]$id)
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
SetSlotType 1 1; SetSlotCreated 1 $true; SetSlotProfileId 1 "dualsense"
SetSlotType 2 2; SetSlotCreated 2 $true
SetSlotType 3 4; SetSlotCreated 3 $true
SetSlotType 4 3; SetSlotCreated 4 $true

function SetSlotOrder { param([string]$el, [int[]]$pi)
    $o = $root.AppSettings.SelectSingleNode($el); if (-not $o) { return }
    $o.RemoveAll() | Out-Null
    foreach ($p in $pi) { $e = $xml.CreateElement("PadIndex"); $e.InnerText = "$p"; $o.AppendChild($e) | Out-Null }
}
SetSlotOrder "PlayStationSlotOrder" @(1)
SetSlotOrder "ExtendedSlotOrder" @(2)
SetSlotOrder "KeyboardMouseSlotOrder" @(3)
SetSlotOrder "MidiSlotOrder" @(4)

$xml.Save($XmlPath)

# Step 3: relaunch PadForge
Write-Host "=== STEP 3: Restart PadForge ==="
Start-Process $ExePath
Start-Sleep -Seconds 8
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
for ($w = 0; $w -lt 12 -and $hwnd -eq 0; $w++) { Start-Sleep -Seconds 1; $proc.Refresh(); $hwnd = $proc.MainWindowHandle }
Write-Host "  PID=$($proc.Id) HWND=$hwnd"
[W32]::ShowWindow($hwnd, 3) | Out-Null
Start-Sleep -Seconds 2
[W32]::ForceFG($hwnd) | Out-Null
Start-Sleep -Seconds 2

$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants
$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidProp = [System.Windows.Automation.AutomationElement]::ProcessIdProperty
$pidCond = New-Object System.Windows.Automation.PropertyCondition($pidProp, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)

function FindByAid { param([string]$Aid, $Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    return $where.FindFirst($TD, $cond)
}
function FindByName { param([string]$Name, $CT = $null, $Parent = $null)
    $where = if ($Parent) { $Parent } else { $uiaWin }
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
    if (-not $El) { Write-Host "  !! NOT FOUND: $Lbl"; return $false }
    try {
        $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $ip.Invoke(); Write-Host "  Click '$Lbl' (Invoke)"
    } catch {
        try {
            $sp = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select(); Write-Host "  Click '$Lbl' (SelectionItem)"
        } catch {
            $r = $El.Current.BoundingRectangle
            $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
            [W32]::ClickAt($x, $y); Write-Host "  Click '$Lbl' (coord $x,$y)"
        }
    }
    Start-Sleep -Milliseconds $Delay
    return $true
}
function Cap { param([string]$Name, [bool]$KeepCursor = $false)
    [W32]::ForceFG($hwnd)
    if (-not $KeepCursor) { [W32]::SetCursorPos(200, 1000) | Out-Null }
    Start-Sleep -Milliseconds 600
    $r = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { Write-Host "  !! bad rect"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $p = Join-Path $OutputDir "$Name.png"
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $kb = [math]::Round((Get-Item $p).Length / 1024)
    Write-Host "  >> $Name.png (${kb}KB)"
}
function Nav { param([string]$Name)
    foreach ($ct in @([System.Windows.Automation.ControlType]::ListItem,
                      [System.Windows.Automation.ControlType]::TreeItem)) {
        $el = FindByName -Name $Name -CT $ct
        if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    }
    $el = FindByName -Name $Name
    if ($el) { return ClickEl -El $el -Lbl "Nav:$Name" }
    return $false
}
function TabByAid { param([string]$Aid)
    $el = FindByAid $Aid
    if (-not $el) { Write-Host "  !! Tab AID '$Aid' not found"; return $false }
    [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 200
    $r = $el.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [W32]::ClickAt($x, $y); Write-Host "  Tab AID '$Aid' (coord $x,$y)"
    Start-Sleep -Milliseconds 800; return $true
}
function SelectSlot { param([int]$idx, [string]$lbl, [int]$delay = 2500)
    Nav "Dashboard"; Start-Sleep -Milliseconds 800
    $host_ = FindByAid "SlotsItemsControl"
    if (-not $host_) { Write-Host "  !! SlotsItemsControl missing"; return $false }
    $cards = $host_.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($idx -ge $cards.Count) { Write-Host "  !! only $($cards.Count) cards"; return $false }
    Write-Host "  $($cards.Count) cards; selecting [$idx] for $lbl"
    ClickEl $cards[$idx] -Lbl "$lbl card" -Delay $delay | Out-Null
    return $true
}

# STEP 4: Slot 0 - 3D capture (Controller tab IS the default; just force it)
Write-Host "=== STEP 4: Slot 0 (Xbox Series) - 3D ==="
if (SelectSlot 0 "Xbox Series" 3000) {
    TabByAid "TabController" | Out-Null
    Start-Sleep -Milliseconds 800
    Cap "pad-controller-3d"
}

# STEP 5: Slot 1 (PlayStation) configbar
Write-Host "=== STEP 5: Slot 1 (PlayStation) configbar ==="
if (SelectSlot 1 "PlayStation" 3000) {
    TabByAid "TabController" | Out-Null
    Start-Sleep -Milliseconds 800
    Cap "pad-playstation-configbar"
}

# STEP 6: Slot 2 (Extended) configbar + schematic
Write-Host "=== STEP 6: Slot 2 (Extended) configbar + schematic ==="
if (SelectSlot 2 "Extended" 3000) {
    TabByAid "TabController" | Out-Null
    Start-Sleep -Milliseconds 800
    Cap "pad-extended-configbar"
    # Toggle 2D
    $pp = (FindByAid "PadPageView").Current.BoundingRectangle
    $tx = [int]($pp.X + 52); $ty = [int]($pp.Y + 124)
    [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 700
    Cap "pad-extended-schematic"
    [W32]::ClickAt($tx, $ty); Start-Sleep -Milliseconds 500
}

# STEP 7: Slot 4 (MIDI) configbar
Write-Host "=== STEP 7: Slot 4 (MIDI) configbar ==="
if (SelectSlot 4 "MIDI" 3000) {
    TabByAid "TabController" | Out-Null
    Start-Sleep -Milliseconds 800
    Cap "pad-midi-configbar"
}

# STEP 8: Add Controller popup (find AddControllerCard Border)
Write-Host "=== STEP 8: Add Controller popup ==="
Nav "Dashboard"; Start-Sleep -Milliseconds 800
$addCard = FindByAid "AddControllerCard"
if (-not $addCard) {
    # Border has no AID; locate by walking Dashboard children for "Add Controller" text
    $tb = FindByName "Add Controller"
    if ($tb) {
        # Walk up to clickable parent
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $cur = $tb
        while ($cur -and $cur.Current.ControlType -ne [System.Windows.Automation.ControlType]::Pane) {
            $cur = $walker.GetParent($cur)
        }
        if ($cur) { $addCard = $cur }
        else { $addCard = $tb }
    }
}
if ($addCard) {
    $r = $addCard.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [W32]::ClickAt($x, $y); Start-Sleep -Milliseconds 1500
    Cap "add-controller-popup"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 500
} else {
    Write-Host "  !! Add Controller card not located"
}

# STEP 9: restore XML
Write-Host "=== STEP 9: Restore XML ==="
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
Copy-Item $XmlBak $XmlPath -Force
Write-Host "  Restored. Re-launching PadForge..."
Start-Process $ExePath
Start-Sleep -Seconds 4

Write-Host "=== DONE ==="
Stop-Transcript | Out-Null
