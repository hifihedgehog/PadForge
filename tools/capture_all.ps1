<#
.SYNOPSIS
    Captures ALL PadForge screenshots for wiki and README.
.DESCRIPTION
    1. Backs up PadForge.xml
    2. Injects test data (4 slot types, macros with mouse/AppVolume, sensitivity curves, profiles)
    3. Kills and restarts PadForge
    4. Runs full UIA-based capture (~30 screenshots)
    5. Restores PadForge.xml backup
    Must run elevated (PadForge runs elevated for HIDMaestro and HidHide).
#>

param(
    [string]$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$logPath = "C:\PadForge\capture_log.txt"
Start-Transcript -Path $logPath -Force | Out-Null

# --- Assemblies ---------------------------------------------------------------
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# --- P/Invoke -----------------------------------------------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int sz);

    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    public static void ClickAt(int px, int py) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int nx = (int)(((long)px * 65535) / (sw - 1));
        int ny = (int)(((long)py * 65535) / (sh - 1));
        INPUT[] i = new INPUT[3];
        i[0].type = 0; i[0].mi.dx = nx; i[0].mi.dy = ny; i[0].mi.dwFlags = 0x8001;
        i[1].type = 0; i[1].mi.dx = nx; i[1].mi.dy = ny; i[1].mi.dwFlags = 0x8002;
        i[2].type = 0; i[2].mi.dx = nx; i[2].mi.dy = ny; i[2].mi.dwFlags = 0x8004;
        SendInput(3, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void MoveTo(int px, int py) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int nx = (int)(((long)px * 65535) / (sw - 1));
        int ny = (int)(((long)py * 65535) / (sh - 1));
        INPUT[] i = new INPUT[1];
        i[0].type = 0; i[0].mi.dx = nx; i[0].mi.dy = ny; i[0].mi.dwFlags = 0x8001;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void ScrollAt(int px, int py, int clicks) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        INPUT[] i = new INPUT[1];
        i[0].type = 0;
        i[0].mi.dx = (int)(((long)px * 65535) / (sw - 1));
        i[0].mi.dy = (int)(((long)py * 65535) / (sh - 1));
        i[0].mi.mouseData = unchecked((uint)(clicks * 120));
        i[0].mi.dwFlags = 0x8001 | 0x0800;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void ForceFG(IntPtr hwnd) {
        IntPtr fg = GetForegroundWindow();
        uint fgTid, myTid = GetCurrentThreadId();
        GetWindowThreadProcessId(fg, out fgTid);
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, true);
        ShowWindow(hwnd, 5);  // SW_SHOW (not SW_RESTORE=9 which un-maximizes)
        SetForegroundWindow(hwnd);
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, false);
    }
}
"@

[Win32]::SetProcessDPIAware() | Out-Null

# --- UIA helpers --------------------------------------------------------------
$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants

function Find-UIA {
    param(
        [System.Windows.Automation.AutomationElement]$Parent = $script:uiaWin,
        [string]$Name,
        [string]$Aid,
        [System.Windows.Automation.ControlType]$CT
    )
    $conds = @()
    if ($Name) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    }
    if ($Aid) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Aid)
    }
    if ($CT) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
    }
    if ($conds.Count -eq 0) { return $null }
    $c = if ($conds.Count -eq 1) { $conds[0] }
         else { New-Object System.Windows.Automation.AndCondition($conds) }
    return $Parent.FindFirst($TD, $c)
}

function Find-AllUIA {
    param(
        [System.Windows.Automation.AutomationElement]$Parent = $script:uiaWin,
        [System.Windows.Automation.ControlType]$CT
    )
    $c = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $CT)
    return $Parent.FindAll($TD, $c)
}

function Click-El {
    param(
        [System.Windows.Automation.AutomationElement]$El,
        [int]$Delay = 800,
        [string]$Label
    )
    if (-not $El) { Write-Host "  !! NOT FOUND: $Label" -ForegroundColor Red; return $false }
    $r = $El.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0) {
        Write-Host "  !! EMPTY BOUNDS: $Label" -ForegroundColor Red; return $false
    }
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    $n = if ($Label) { $Label } else { $El.Current.Name }
    Write-Host ("  Click '{0}' at ({1},{2}) [{3}x{4}]" -f $n, $cx, $cy, [int]$r.Width, [int]$r.Height)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($cx, $cy)
    Start-Sleep -Milliseconds $Delay
    return $true
}

function Cap {
    param([string]$Name)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$r) | Out-Null
    [Win32]::MoveTo(($r.Right - 100), ($r.Bottom - 15))
    Start-Sleep -Milliseconds 200
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $p = Join-Path $script:OutputDir "$Name.png"
    $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $kb = [math]::Round((Get-Item $p).Length / 1024)
    Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
}

function Select-El {
    param(
        [System.Windows.Automation.AutomationElement]$El,
        [int]$Delay = 800,
        [string]$Label
    )
    if (-not $El) { Write-Host "  !! NOT FOUND: $Label" -ForegroundColor Red; return $false }
    $n = if ($Label) { $Label } else { $El.Current.Name }
    try {
        $pat = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        Write-Host "  Select '$n' (SelectionItemPattern)"
        $pat.Select()
        Start-Sleep -Milliseconds $Delay
        return $true
    } catch {
        Write-Host "  Select '$n' -- no SelectionItemPattern, falling back to click"
        return (Click-El $El -Label $Label -Delay $Delay)
    }
}

function Nav {
    param([string]$Name)
    foreach ($ctName in @("ListItem", "TreeItem")) {
        $ct = [System.Windows.Automation.ControlType]::$ctName
        $el = Find-UIA -Name $Name -CT $ct
        if ($el) { return (Select-El $el -Label $Name) }
    }
    $el = Find-UIA -Name $Name
    if ($el) { return (Select-El $el -Label $Name) }
    Write-Host "  !! Nav '$Name' not found" -ForegroundColor Red
    return $false
}

function Find-AllSlots {
    param([int]$Retries = 3, [int]$DelayMs = 1500)
    $skip = @("Dashboard", "Profiles", "Devices", "Add Controller", "About", "Settings",
              "", "PadForge", "Toggle navigation", "Back", "Close Navigation")
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $menuHost = Find-UIA -Aid "MenuItemsHost"
        $searchIn = if ($menuHost) { $menuHost } else { $script:uiaWin }
        $ct = [System.Windows.Automation.ControlType]::ListItem
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        $all = $searchIn.FindAll($TD, $cond)
        $slots = @()
        foreach ($item in $all) {
            $n = $item.Current.Name
            $cls = $item.Current.ClassName
            if ($cls -eq "NavigationViewItem" -and ($n -match '^Pad\d+$' -or ($n -notin $skip -and $n.Length -gt 0))) {
                Write-Host "    Slot: '$n' (class=$cls)"
                $slots += $item
            }
        }
        if ($slots.Count -gt 0) {
            Write-Host "  Found $($slots.Count) slot(s) on attempt $attempt"
            return $slots
        }
        # Diagnostic: list ALL NavigationViewItems
        if ($attempt -eq 1) {
            Write-Host "  Diagnostic: All nav items on attempt 1:"
            foreach ($item in $all) {
                Write-Host "    Name='$($item.Current.Name)' Class='$($item.Current.ClassName)'"
            }
        }
        Write-Host "  No slots found (attempt $attempt/$Retries), waiting ${DelayMs}ms..."
        Start-Sleep -Milliseconds $DelayMs
    }
    Write-Host "  !! No slots after $Retries retries" -ForegroundColor Red
    return @()
}

function Find-SlotByType {
    <#
    .SYNOPSIS
        Finds and selects a sidebar slot by controller type, returning the slot element.
        Identifies type by selecting each slot and checking which PadPage elements are
        present in the UIA tree (WPF Collapsed elements are removed from UIA):
        - Extended: ExtendedStickCountBox AutomationId present (Extended-specific config UI)
        - MIDI:     MidiConfigBar AutomationId present
        - KBM:      KBMPreview AutomationId present (keyboard+mouse preview view)
        - Xbox / PlayStation: none of the above config bars/previews
    #>
    param([string]$Type)  # "Xbox", "PlayStation", "Extended", "KBM", "MIDI"
    $slots = @(Find-AllSlots)
    foreach ($slot in $slots) {
        Select-El $slot -Label "Probe $($slot.Current.Name)" -Delay 800
        # Click the Controller tab so type-specific elements become visible in UIA
        $padPage = Find-UIA -Aid "PadPageView"
        if (-not $padPage) { continue }
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $probeTabs = $padPage.FindAll($TC, $rbCond)
        if ($probeTabs.Count -gt 0) {
            try { $probeTabs[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch {}
            Start-Sleep -Milliseconds 500
        }
        # WPF Collapsed elements are not in UIA tree, so presence = Visible
        $hasExtended = $null -ne (Find-UIA -Parent $padPage -Aid "ExtendedStickCountBox")
        $hasMidi = $null -ne (Find-UIA -Parent $padPage -Aid "MidiConfigBar")
        $hasKbm  = $null -ne (Find-UIA -Parent $padPage -Aid "KBMPreview")
        Write-Host "    $($slot.Current.Name): Extended=$hasExtended MIDI=$hasMidi KBM=$hasKbm"
        $matched = $false
        switch ($Type) {
            "Extended"    { $matched = $hasExtended }
            "MIDI"        { $matched = $hasMidi }
            "KBM"         { $matched = $hasKbm }
            "Xbox"        { $matched = -not $hasExtended -and -not $hasMidi -and -not $hasKbm }
            "PlayStation" { $matched = -not $hasExtended -and -not $hasMidi -and -not $hasKbm }
        }
        if ($matched) {
            Write-Host "  Found $Type slot: $($slot.Current.Name)" -ForegroundColor Green
            return $slot
        }
    }
    Write-Host "  !! Could not find $Type slot by content probing" -ForegroundColor Red
    return $null
}

function Tab {
    param([string]$Name)
    $padPage = Find-UIA -Aid "PadPageView"
    $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
    $el = Find-UIA -Parent $searchIn -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton)
    if (-not $el) {
        Start-Sleep -Milliseconds 500
        $padPage = Find-UIA -Aid "PadPageView"
        $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
        $el = Find-UIA -Parent $searchIn -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton)
    }
    if (-not $el) { $el = Find-UIA -Name $Name }
    if ($el) { return (Click-El $el -Label "Tab:$Name") }
    Write-Host "  !! Tab '$Name' not found" -ForegroundColor Yellow
    return $false
}

function ScrollContent {
    param([int]$Clicks = -15)
    $sr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$sr) | Out-Null
    $cx = [int](($sr.Left + $sr.Right) / 2 + 100)
    $cy = [int](($sr.Top + $sr.Bottom) / 2)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt($cx, $cy)
    Start-Sleep -Milliseconds 300
    $step = if ($Clicks -lt 0) { -3 } else { 3 }
    $count = [math]::Abs([math]::Ceiling($Clicks / $step))
    for ($i = 0; $i -lt $count; $i++) {
        [Win32]::ScrollAt($cx, $cy, $step)
        Start-Sleep -Milliseconds 50
    }
    Start-Sleep -Milliseconds 600
}

# ==============================================================================
# STEP 0: Inject test data into PadForge.xml
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 0: Inject test data ===" -ForegroundColor Cyan

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

# Kill PadForge if running (double-kill pattern — may auto-restart via startup entry)
$existing = Get-Process PadForge -EA SilentlyContinue
if ($existing) {
    Write-Host "  Stopping PadForge (first kill)..."
    $existing | Stop-Process -Force
    Start-Sleep -Seconds 3
    # Second kill in case it auto-restarted
    $respawned = Get-Process PadForge -EA SilentlyContinue
    if ($respawned) {
        Write-Host "  PadForge respawned -- killing again..."
        $respawned | Stop-Process -Force
    }
    Start-Sleep -Seconds 2
}

# If PadForge.xml doesn't exist, launch PadForge briefly to create default settings
if (-not (Test-Path $PadForgeXml)) {
    Write-Host "  PadForge.xml not found -- launching PadForge to create defaults..."
    Start-Process $PadForgeExe
    Start-Sleep -Seconds 10
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    if (-not (Test-Path $PadForgeXml)) {
        # Check fallback name
        $fallback = Join-Path (Split-Path $PadForgeXml) "Settings.xml"
        if (Test-Path $fallback) {
            Write-Host "  Found Settings.xml instead -- using it"
            $PadForgeXml = $fallback
        } else {
            Write-Host "  !! PadForge.xml still not found after launch" -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "  PadForge created default settings"
}

# Backup and delete XML for a clean start (no leftover slots from previous runs)
$xmlBak = "$PadForgeXml.bak"
Copy-Item $PadForgeXml $xmlBak -Force
Remove-Item $PadForgeXml -Force
Write-Host "  Backed up and deleted PadForge.xml for clean start"

# Launch PadForge briefly to regenerate default XML, then kill it.
# Poll for the file rather than wait a fixed interval — first-time launch
# on a stock system can take 15-20s for the SDL3 enumeration + initial
# settings flush to complete. Cap at 30s so we don't hang forever if
# something's broken.
Start-Process $PadForgeExe
$xmlAppeared = $false
for ($w = 0; $w -lt 30; $w++) {
    Start-Sleep -Seconds 1
    if (Test-Path $PadForgeXml) { $xmlAppeared = $true; break }
}
if ($xmlAppeared) {
    Write-Host "  PadForge.xml regenerated after ${w}s" -ForegroundColor Green
    # Give the settings flush + enumeration a couple more seconds before kill.
    Start-Sleep -Seconds 3
} else {
    Write-Host "  !! PadForge.xml never appeared (waited 30s)" -ForegroundColor Yellow
}
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (-not (Test-Path $PadForgeXml)) {
    Write-Host "  !! PadForge.xml not regenerated after clean launch" -ForegroundColor Red
    Copy-Item $xmlBak $PadForgeXml -Force
    Write-Host "  Restored backup"
}

# Load and modify XML
[xml]$xml = Get-Content $PadForgeXml
$ns = $xml.PadForgeSettings

# --- Preserve cached device list (incl. offline placeholder devices) from backup ---
# The clean-start regen above only enumerates currently-connected hardware, so the
# offline placeholder devices (DualSense, Wii Remote, NFC reader, Switch 2 Pro, ...)
# that live in the user's real config are gone. Those placeholders are how the
# device-gated tabs (Pointer, Adaptive Triggers, Lighting, Audio) and the NFC / Power
# Devices-page sections get surfaced for capture without real hardware. Capability
# gating is [XmlIgnore]-computed from VendorId + ProductName, so the cached <Device>
# element is enough to light up every gate even though the device never connects.
# Import the backup's <Devices> node wholesale (it holds both online and offline
# entries; runtime recomputes online state from what's actually plugged in).
try {
    [xml]$xmlBakDoc = Get-Content $xmlBak
    $bakDevices = $xmlBakDoc.PadForgeSettings.SelectSingleNode("Devices")
    if ($bakDevices) {
        $imported = $xml.ImportNode($bakDevices, $true)
        $freshDevices = $ns.SelectSingleNode("Devices")
        if ($freshDevices) { $ns.ReplaceChild($imported, $freshDevices) | Out-Null }
        else { $ns.AppendChild($imported) | Out-Null }
        $devCount = $imported.SelectNodes("Device").Count
        Write-Host "  Preserved $devCount cached devices from backup (incl. offline placeholders)" -ForegroundColor Green
    } else {
        Write-Host "  !! Backup had no <Devices> node -- device-gated captures may be skipped" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  !! Failed to preserve cached devices: $_" -ForegroundColor Yellow
}

# --- Clear all existing slots so we start fresh with exactly 5 ---
$slotCreatedNode = $ns.SelectSingleNode("SlotCreated")
if ($slotCreatedNode) {
    $slotCreatedNode.InnerText = ("false," * 15 + "false")
    Write-Host "  Cleared all existing slots"
}
$slotEnabledNode = $ns.SelectSingleNode("SlotEnabled")
if ($slotEnabledNode) {
    $slotEnabledNode.InnerText = ("false," * 15 + "false")
}
$slotTypesNode = $ns.SelectSingleNode("SlotControllerTypes")
if ($slotTypesNode) {
    $slotTypesNode.InnerText = ("Xbox360," * 15 + "Xbox360")
    Write-Host "  Reset all slot types to Xbox (XML enum 'Xbox360' kept verbatim for back-compat)"
}

# --- Inject a test profile (profiles only -- slots created via UI later) ---
$profilesNode = $ns.SelectSingleNode("Profiles")
if (-not $profilesNode) {
    $profilesNode = $xml.CreateElement("Profiles")
    $ns.AppendChild($profilesNode) | Out-Null
}
if ($profilesNode.ChildNodes.Count -eq 0) {
    $prof = $xml.CreateElement("Profile")
    @{
        "Name" = "Rocket League"
        "Executables" = "RocketLeague.exe"
        "IsActive" = "false"
    }.GetEnumerator() | ForEach-Object {
        $e = $xml.CreateElement($_.Key); $e.InnerText = $_.Value; $prof.AppendChild($e) | Out-Null
    }
    $profilesNode.AppendChild($prof) | Out-Null
    Write-Host "  Injected test profile"
}

# --- Inject test macros (so Macros tab screenshot shows content) ---
$macrosNode = $ns.SelectSingleNode("Macros")
if (-not $macrosNode) {
    $macrosNode = $xml.CreateElement("Macros")
    $ns.AppendChild($macrosNode) | Out-Null
}
if ($macrosNode.ChildNodes.Count -eq 0) {
    # Macro 1: "Quick Combo" — combo trigger (button + axis), multiple action types
    $m1Xml = @'
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
    # Macro 2: "Volume Control" — Always trigger mode, volume + mouse move
    $m2Xml = @'
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
    $frag1 = $xml.CreateDocumentFragment(); $frag1.InnerXml = $m1Xml.Trim()
    $macrosNode.AppendChild($frag1) | Out-Null
    $frag2 = $xml.CreateDocumentFragment(); $frag2.InnerXml = $m2Xml.Trim()
    $macrosNode.AppendChild($frag2) | Out-Null
    Write-Host "  Injected 2 test macros"
}

# --- Ensure PadForge starts with window visible (not minimized to tray) ---
$appSettings = $ns.SelectSingleNode("AppSettings")
if ($appSettings) {
    $smNode = $appSettings.SelectSingleNode("StartMinimized")
    if ($smNode) { $smNode.InnerText = "false" }
    else {
        $smNode = $xml.CreateElement("StartMinimized")
        $smNode.InnerText = "false"
        $appSettings.AppendChild($smNode) | Out-Null
    }
    Write-Host "  Set StartMinimized=false for capture"

    # Enable web controller server for web screenshots
    $wcNode = $appSettings.SelectSingleNode("EnableWebController")
    if ($wcNode) { $wcNode.InnerText = "true" }
    else {
        $wcNode = $xml.CreateElement("EnableWebController")
        $wcNode.InnerText = "true"
        $appSettings.AppendChild($wcNode) | Out-Null
    }
    Write-Host "  Set EnableWebController=true for capture"

    # Force English language for screenshots (nav items use localized text)
    $langNode = $appSettings.SelectSingleNode("Language")
    if ($langNode) { $langNode.InnerText = "en" }
    else {
        $langNode = $xml.CreateElement("Language")
        $langNode.InnerText = "en"
        $appSettings.AppendChild($langNode) | Out-Null
    }
    Write-Host "  Set Language=en for English screenshots"
}

$xml.Save($PadForgeXml)
Write-Host "  Saved modified PadForge.xml" -ForegroundColor Green

# ==============================================================================
# STEP 1: Start PadForge
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 1: Start PadForge ===" -ForegroundColor Cyan
Start-Process $PadForgeExe
Write-Host "  Waiting for PadForge to start..."
$timeout = 15
$started = $false
for ($i = 0; $i -lt $timeout; $i++) {
    Start-Sleep -Seconds 1
    $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.MainWindowHandle -ne 0) {
        $started = $true
        break
    }
}
if (-not $started) {
    Write-Host "  !! PadForge failed to start in ${timeout}s" -ForegroundColor Red
    Copy-Item $xmlBak $PadForgeXml -Force
    exit 1
}

$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
Write-Host "  PadForge PID=$($proc.Id) HWND=$hwnd" -ForegroundColor Green

# ==============================================================================
# STEP 2: Setup window
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 2: Setup window ===" -ForegroundColor Cyan

[Win32]::ForceFG($hwnd)
[Win32]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE
Start-Sleep -Milliseconds 500
[Win32]::SetWindowPos($hwnd, [Win32]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 200

$rect = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$winW = $rect.Right - $rect.Left; $winH = $rect.Bottom - $rect.Top
Write-Host "  Window: ${winW}x${winH} at ($($rect.Left),$($rect.Top))"

$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
if (-not $uiaWin) {
    Write-Host "  !! UIA fail" -ForegroundColor Red
    Copy-Item $xmlBak $PadForgeXml -Force
    exit 1
}
Write-Host "  UIA: '$($uiaWin.Current.Name)'"

# Expand sidebar if compact
$hamburger = Find-UIA -Aid "TogglePaneButton" -CT ([System.Windows.Automation.ControlType]::Button)
if (-not $hamburger) {
    $hamburger = Find-UIA -Name "Toggle navigation" -CT ([System.Windows.Automation.ControlType]::Button)
}
if ($hamburger) {
    $dash = Find-UIA -Name "Dashboard"
    if ($dash -and $dash.Current.BoundingRectangle.Width -lt 120) {
        Write-Host "  Sidebar compact -- expanding..."
        Click-El $hamburger -Label "Hamburger" -Delay 500
    } else {
        Write-Host "  Sidebar already expanded"
    }
}

# Warm-up click
[Win32]::ForceFG($hwnd)
Start-Sleep -Milliseconds 200
$wr = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
[Win32]::ClickAt([int](($wr.Left + $wr.Right) / 2), ($wr.Top + 30))
Start-Sleep -Milliseconds 500

# ==============================================================================
# STEP 2b: Create 5 controller slots via Add Controller popup
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 2b: Create controller slots via UI ===" -ForegroundColor Cyan

# Helper: click Add Controller sidebar item, then click a type button by AutomationId
$popupCaptured = $false
function Add-SlotViaPopup {
    param([string]$TypeBtnAid, [string]$TypeLabel)
    # Click "Add Controller" in sidebar
    $addNav = Find-UIA -Name "Add Controller"
    if (-not $addNav) { Write-Host "  !! Add Controller nav not found" -ForegroundColor Red; return $false }
    Click-El $addNav -Label "Add Controller" -Delay 600
    # Capture the popup on first open (shows all 5 type buttons)
    if (-not $script:popupCaptured) {
        Cap "add-controller-popup"
        $script:popupCaptured = $true
    }
    # Find and click the type button
    $typeBtn = Find-UIA -Aid $TypeBtnAid
    if (-not $typeBtn) {
        Write-Host "  !! Type button '$TypeBtnAid' not found in popup" -ForegroundColor Red
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 300
        return $false
    }
    Click-El $typeBtn -Label $TypeLabel -Delay 1500
    return $true
}

# Delete all existing slots to ensure a clean start
Write-Host "  Removing any existing slots..."
for ($delPass = 0; $delPass -lt 16; $delPass++) {
    $existingSlots = @(Find-AllSlots)
    if ($existingSlots.Count -eq 0) { break }
    # Select the first slot
    Select-El $existingSlots[0] -Label "Select for delete" -Delay 500
    # Find and click the delete/close button (X) — it's a Button with the delete tooltip
    $padPage = Find-UIA -Aid "PadPageView"
    $delBtn = $null
    if ($padPage) {
        $allBtns = $padPage.FindAll($TC, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
        foreach ($b in $allBtns) {
            if ($b.Current.Name -match "Delete|Remove|Close") { $delBtn = $b; break }
        }
    }
    if (-not $delBtn) {
        # Fallback: use keyboard shortcut or find by sidebar card X button
        Write-Host "  !! Could not find delete button, trying sidebar X..."
        # The sidebar card has its own X button — search within the slot element
        $slotBtns = $existingSlots[0].FindAll($TC, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
        foreach ($b in $slotBtns) {
            $delBtn = $b  # Last button in card is typically the X
        }
    }
    if ($delBtn) {
        Click-El $delBtn -Label "Delete slot" -Delay 800
    } else {
        Write-Host "  !! No delete button found, breaking" -ForegroundColor Red
        break
    }
}
$remainingSlots = @(Find-AllSlots)
Write-Host "  Slots remaining after cleanup: $($remainingSlots.Count)"

# Create: Xbox, PlayStation, KBM, Extended, MIDI (order matters for slot indices).
# AutomationIds AddXbox360Btn / AddDS4Btn are kept verbatim from v2 for stable
# automation hookup; the buttons' accessibility labels are now Xbox / PlayStation.
$slotTypes = @(
    @{ Aid = "AddXbox360Btn"; Label = "Xbox" },
    @{ Aid = "AddDS4Btn"; Label = "PlayStation" },
    @{ Aid = "AddKeyboardMouseBtn"; Label = "Keyboard+Mouse" },
    @{ Aid = "AddExtendedBtn"; Label = "Extended" },
    @{ Aid = "AddMidiBtn"; Label = "MIDI" }
)
foreach ($st in $slotTypes) {
    Write-Host "  Creating $($st.Label) slot..."
    $ok = Add-SlotViaPopup -TypeBtnAid $st.Aid -TypeLabel $st.Label
    if ($ok) { Write-Host "  Created $($st.Label)" -ForegroundColor Green }
    Start-Sleep -Milliseconds 500
}

# Wait for type-group reorder to fully settle before querying slots
Write-Host "  Waiting 3s for type-group reorder to settle..."
Start-Sleep -Milliseconds 3000

# Verify slots appeared
$slots = @(Find-AllSlots)
Write-Host "  Slots after creation: $($slots.Count)"

# ----------------------------------------------------------------------
# Assign a DualSense to the Xbox + PlayStation slots so their PadPages
# expose the conditional tabs:
#   - Force Feedback tab is gated on a gamepad-class device being assigned
#   - Adaptive Triggers + Lighting tabs are gated on a DualSense (or
#     DualSense Edge) device being assigned, per PadPage.xaml.cs:255-283
# Without this step those tabs stay Visibility=Collapsed and capture
# can't reach them.
# ----------------------------------------------------------------------
Write-Host ""
Write-Host "--- Assign DualSense to Xbox + PlayStation slots ---" -ForegroundColor Yellow
Nav "Devices"; Start-Sleep -Milliseconds 1500

function Assign-DeviceToSlot {
    param([string]$DeviceNamePart, [string]$SlotNumberLabel)
    $searchIn = $script:uiaWin
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    # Search the initially-realized rows first, then scroll DOWN on a miss to
    # reach lower rows in the virtualized card list. No scroll-to-top: that
    # de-realizes the top rows and broke nearby finds (e.g. DualSense).
    $wrA = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrA) | Out-Null
    $lx = [int]($wrA.Left + 400); $my = [int](($wrA.Top + $wrA.Bottom) / 2)
    $target = $null
    for ($stry = 0; $stry -lt 16 -and (-not $target); $stry++) {
        $items = $searchIn.FindAll($TD, $liCond)
        foreach ($it in $items) {
            if ($it.Current.Name -like "*$DeviceNamePart*") { $target = $it; break }
        }
        if (-not $target) { [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($lx, $my, -3); Start-Sleep -Milliseconds 350 }
    }
    if (-not $target) {
        Write-Host "  !! Device matching '$DeviceNamePart' not found after scroll" -ForegroundColor Yellow
        return $false
    }
    Write-Host "  Found device card '$DeviceNamePart'"
    Click-El $target -Label "Device card '$DeviceNamePart'" -Delay 1000 | Out-Null

    # Slot-assignment controls live in the device's detail panel (right column):
    # one ToggleButton per active slot (DevicesPage.xaml, ItemsControl bound to
    # ActiveSlotItems). The button gets NO UIA Name from WPF; its child TextBlocks
    # are a connection glyph (E7FC) + the SlotNumber. Identify each toggle by the
    # digits in its child Text (the SlotNumber). CRUCIAL: the left nav also has one
    # power ToggleButton per slot, and an unscoped window search grabbed those 5
    # sidebar toggles instead of these, silently toggling slot power. Discriminate
    # by POSITION: the detail panel is the right portion of the window; the sidebar
    # is the far left. Keep only toggles whose center-X is right of mid-window.
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $txtCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $wrB = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrB) | Out-Null
    $midX = ($wrB.Left + $wrB.Right) / 2
    $allButtons = $searchIn.FindAll($TD, $btnCond)
    $toggles = @()
    foreach ($b in $allButtons) {
        try {
            $null = $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            $r = $b.Current.BoundingRectangle
            $cx = $r.X + $r.Width / 2
            if ($cx -gt $midX) { $toggles += $b }   # detail panel only, exclude sidebar
        } catch {}
    }
    # Key each detail-panel toggle by its child SlotNumber (digits).
    $slotOf = @{}
    foreach ($t in $toggles) {
        $digits = ""
        try {
            foreach ($tx in $t.FindAll($TD, $txtCond)) {
                $d = ($tx.Current.Name -replace '[^\d]', '')
                if ($d -ne "") { $digits = $d; break }
            }
        } catch {}
        $slotOf[$t] = $digits
    }
    Write-Host "    Detail-panel assignment toggles: $($toggles.Count)"
    foreach ($t in $toggles) { Write-Host "      toggle slotNumber='$($slotOf[$t])'" }
    $btn = $null
    foreach ($t in $toggles) {
        if ($slotOf[$t] -eq $SlotNumberLabel) { $btn = $t; break }
    }
    if (-not $btn) {
        # Fallback: positional. ActiveSlotItems is in slot order, so the Nth
        # detail-panel toggle (0-based) is SlotNumber N+1.
        $idx = [int]$SlotNumberLabel - 1
        if ($idx -ge 0 -and $idx -lt $toggles.Count) {
            $btn = $toggles[$idx]
            Write-Host "    (slot-number match missed -- using positional toggle #$idx)" -ForegroundColor DarkGray
        }
    }
    if ($btn) {
        # Skip if already assigned (reading ToggleState is fine; it's the
        # Toggle() ACTION that's unreliable here).
        $already = $false
        try { $already = ($btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) } catch {}
        if ($already) {
            Write-Host "  Slot $SlotNumberLabel already assigned to $DeviceNamePart"
            return $true
        }
        # Bring the toggle into view first (the assign row can sit below the
        # fold on a tall detail panel), then use a real coordinate CLICK, not
        # TogglePattern.Toggle(). The toggle's IsChecked is OneWay-bound to
        # IsAssigned and the actual assignment is done by ToggleSlotCommand,
        # which fires on Click. UIA Toggle() only flips IsChecked (immediately
        # overwritten by the OneWay binding) and never runs the command, so the
        # assignment silently no-ops -- which is exactly why every slot read
        # "No device mapped" on the dashboard.
        try { $btn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 300 } catch {}
        Click-El $btn -Label "Slot $SlotNumberLabel toggle ($DeviceNamePart)" -Delay 900 | Out-Null
        Write-Host "  Assigned $DeviceNamePart to slot $SlotNumberLabel" -ForegroundColor Green
        return $true
    }
    Write-Host "  !! Slot $SlotNumberLabel toggle not found for $DeviceNamePart (had $($toggles.Count) toggles)" -ForegroundColor Yellow
    return $false
}

Assign-DeviceToSlot -DeviceNamePart "DualSense" -SlotNumberLabel "1" | Out-Null
Assign-DeviceToSlot -DeviceNamePart "DualSense" -SlotNumberLabel "2" | Out-Null
# Assign the Wii Remote to the Extended slot so its Pointer / Gyro tabs are
# reachable for the 3.6.0 Pointer-tab capture (issue #146). SlotNumber follows
# DevicesViewModel.RefreshSlotButtons, which walks slots in TYPE-GROUP order
# (Xbox -> PlayStation -> Extended -> KBM -> MIDI) to match the dashboard cards,
# NOT creation/PadIndex order. So SlotNumber 1=Xbox, 2=PlayStation, 3=Extended,
# 4=KBM, 5=MIDI. The Extended slot is SlotNumber 3 (KBM at 4 hides the capability
# tabs, which is why assigning the Wii to 4 left the Pointer tab unreachable).
# The Wii Remote's IR-camera capability is identity-derived (VID 0x057E + name),
# so the tab is offered whether the placeholder device is online or not.
Assign-DeviceToSlot -DeviceNamePart "Wii Remote" -SlotNumberLabel "3" | Out-Null

# Give the Devices page time to write the assignment back to the VMs and
# for the PadPage's hasForceFeedback / hasAdaptiveTriggers / hasLightbar
# gating to flip on for the affected slots.
Start-Sleep -Milliseconds 2000

# Web controller server is enabled via XML injection in Step 0 — no UI click needed.

# ==============================================================================
# STEP 3: Capture all pages
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3: Capture pages ===" -ForegroundColor Cyan
$n = 0
$total = 30

function Next { $script:n++; return $script:n }

# ---- 1. Dashboard ----
Write-Host "[$(Next)/$total] Dashboard"
Nav "Dashboard"; Start-Sleep -Milliseconds 500; Cap "dashboard"

# ---- 2. Profiles ----
Write-Host "[$(Next)/$total] Profiles"
Nav "Profiles"; Cap "profiles"

# ---- 3. Devices ----
Write-Host "[$(Next)/$total] Devices"
Nav "Devices"
Start-Sleep -Milliseconds 500
# Click a device in the list to show the raw input preview panel (axes, buttons, POV)
$devicesPage = Find-UIA -Aid "DevicesPageView"
$searchDevices = if ($devicesPage) { $devicesPage } else { $script:uiaWin }
$liCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$deviceItems = $searchDevices.FindAll($TD, $liCond)
if ($deviceItems -and $deviceItems.Count -gt 0) {
    # Click the last device (usually the gamepad like Xbox controller)
    $lastDev = $deviceItems[$deviceItems.Count - 1]
    Write-Host "  Clicking device: '$($lastDev.Current.Name)' (last of $($deviceItems.Count))"
    Click-El $lastDev -Label "Device card" -Delay 800
} else {
    Write-Host "  No device items found in list -- capturing without selection" -ForegroundColor Yellow
}
Cap "devices"

# ---- 4-12. Xbox slot (slot 0 -- macros/mappings/sticks/triggers/ff here) ----
# Use Dashboard slot cards (SlotsItemsControl) rather than the sidebar
# (Find-AllSlots / MenuItemsHost). Sidebar NavigationViewItems virtualize out
# of the UIA tree, which left this whole block finding 0 slots and skipping
# every Xbox-slot tab. Dashboard cards stay materialized; after the type-group
# reorder the Xbox slot is card index 0 (same convention the PlayStation /
# Extended blocks below rely on).
Write-Host ""
Write-Host "--- Xbox Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$slots = if ($slotsHost) { @($slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
Write-Host "  Found $($slots.Count) slot card(s)"
if ($slots.Count -ge 1) {
    Click-El $slots[0] -Label "Xbox Slot card" -Delay 2000 | Out-Null

    # 4. Controller 3D view
    Write-Host "[$(Next)/$total] Controller - 3D view"
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "3D View Tab" -Delay 1000 }
    }
    Cap "pad-controller-3d"

    # 5. Controller 2D view
    Write-Host "[$(Next)/$total] Controller - 2D view"
    $ppRect = $padPage.Current.BoundingRectangle
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 600
    Cap "pad-controller-2d"
    # Switch back to 3D
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500

    # 6. Macros (select first macro + first action)
    Write-Host "[$(Next)/$total] Macros"
    if (Tab "Macros") {
        Start-Sleep -Milliseconds 500
        # Try UIA first, then fallback to coordinate click
        $macroClicked = $false
        $liCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $lists = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::List)
        foreach ($list in $lists) {
            $items = $list.FindAll($TC, $liCond)
            if ($items.Count -gt 0) {
                $clicked = Click-El $items[0] -Label "Macro: $($items[0].Current.Name)"
                if ($clicked) {
                    $macroClicked = $true
                    Start-Sleep -Milliseconds 400
                    # Try to click first action in the second list
                    $lists2 = Find-AllUIA -CT ([System.Windows.Automation.ControlType]::List)
                    foreach ($l2 in $lists2) {
                        if ([System.Windows.Automation.Automation]::Compare($l2, $list)) { continue }
                        $acts = $l2.FindAll($TC, $liCond)
                        if ($acts.Count -gt 0) {
                            Click-El $acts[0] -Label "Action: $($acts[0].Current.Name)" -Delay 300
                        }
                        break
                    }
                }
                break
            }
        }
        if (-not $macroClicked) {
            # Fallback: click roughly where the first macro item would be
            # (left panel of Macros tab, below the header)
            $ppRect = (Find-UIA -Aid "PadPageView").Current.BoundingRectangle
            $macroX = [int]($ppRect.X + 180)
            $macroY = [int]($ppRect.Y + 200)
            Write-Host "  Fallback: clicking macro area at ($macroX, $macroY)"
            [Win32]::ClickAt($macroX, $macroY)
            Start-Sleep -Milliseconds 400
            # Click first action item area
            $actionX = [int]($ppRect.X + $ppRect.Width / 2 + 100)
            $actionY = [int]($ppRect.Y + 200)
            [Win32]::ClickAt($actionX, $actionY)
            Start-Sleep -Milliseconds 300
        }
    }
    Cap "pad-macros"

    # 7. Mappings
    Write-Host "[$(Next)/$total] Mappings"
    Tab "Mappings"; Cap "pad-mappings"

    # 8. Sticks (default view with curves and deadzone shapes visible)
    Write-Host "[$(Next)/$total] Sticks"
    Tab "Sticks"; Start-Sleep -Milliseconds 500; Cap "pad-sticks"

    # 9. Sticks — deadzone shape dropdown open
    # Original coords (946, 437) + 32px vertical offset for branding bar at 200% DPI
    Write-Host "[$(Next)/$total] Sticks - deadzone shape dropdown"
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    [Win32]::ClickAt(946, 469)
    Start-Sleep -Milliseconds 800
    Cap "pad-sticks-deadzone-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 10. Sticks — sensitivity preset dropdown open
    # Original coords (946, 1014) + 32px vertical offset
    Write-Host "[$(Next)/$total] Sticks - sensitivity preset dropdown"
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    [Win32]::ClickAt(946, 1046)
    Start-Sleep -Milliseconds 800
    Cap "pad-sticks-sensitivity-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 11. Triggers
    Write-Host "[$(Next)/$total] Triggers"
    Tab "Triggers"; Start-Sleep -Milliseconds 500; Cap "pad-triggers"

    # 12. Triggers — sensitivity preset dropdown open
    Write-Host "[$(Next)/$total] Triggers - sensitivity preset dropdown"
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    $ppRect = (Find-UIA -Aid "PadPageView").Current.BoundingRectangle
    # Original coords (946, 440) + 32px vertical offset for branding bar at 200% DPI
    [Win32]::ClickAt(946, 472)
    Start-Sleep -Milliseconds 800
    Cap "pad-triggers-sensitivity-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 13. Force Feedback
    Write-Host "[$(Next)/$total] Force Feedback"
    Tab "Force Feedback"; Cap "pad-forcefeedback"

} else {
    Write-Host "  !! No controller slots found" -ForegroundColor Red
}

# ---- 13a-b. PlayStation slot — Adaptive Triggers + Lighting tabs ----
# These are PS-only tabs not present on Xbox/Extended/KBM/MIDI. After
# the type-group reorder the PlayStation slot is at index 1 (Xbox at 0).
#
# Use Dashboard slot cards (SlotsItemsControl) rather than the sidebar
# (MenuItemsHost) — sidebar NavigationViewItems get virtualized out of
# the UIA tree after several tab captures, while Dashboard cards stay
# materialized.
Write-Host ""
Write-Host "--- PlayStation Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
if ($slotsHost) {
    $cards = $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)
    if ($cards.Count -ge 2) {
        Click-El $cards[1] -Label "PlayStation Slot card" -Delay 4000 | Out-Null

        # Land on the Controller tab first so the PadPage is fully realized
        # and the conditional AT/Lighting tabs have time to flip to Visible
        # via the PadPage code-behind's hasAdaptiveTriggers / hasLightbar
        # gating. The capability flags depend on HM profile load, which
        # can take several seconds for a fresh slot.
        $padPage = Find-UIA -Aid "PadPageView"
        if ($padPage) {
            $rbCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)

            # Poll for AT tab visibility — it flips visible only after the
            # slot's PlayStationSlotConfig is bound and capability gating
            # has propagated. Up to ~10s on a cold HM bring-up.
            $atVisible = $false
            for ($w = 0; $w -lt 10 -and -not $atVisible; $w++) {
                Start-Sleep -Milliseconds 1000
                $tabs = $padPage.FindAll($TC, $rbCond)
                if ($tabs | Where-Object { $_.Current.Name -eq "Adaptive Triggers" }) {
                    $atVisible = $true
                }
            }
            $tabs = $padPage.FindAll($TC, $rbCond)
            if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "PS Controller Tab" -Delay 1000 | Out-Null }
            $tabs = $padPage.FindAll($TC, $rbCond)
            Write-Host "  PadPage tabs visible to UIA: $($tabs.Count) (AT visible: $atVisible)"
            for ($ti = 0; $ti -lt $tabs.Count; $ti++) {
                Write-Host "    [$ti] Name='$($tabs[$ti].Current.Name)'"
            }

            Write-Host "[$(Next)/$total] Adaptive Triggers"
            $atTab = $tabs | Where-Object { $_.Current.Name -eq "Adaptive Triggers" } | Select-Object -First 1
            if ($atTab) {
                Click-El $atTab -Label "AT Tab" -Delay 1000 | Out-Null
                Cap "pad-adaptive-triggers"
            } else {
                Write-Host "  !! Adaptive Triggers tab not in UIA tree" -ForegroundColor Yellow
            }

            Write-Host "[$(Next)/$total] Lighting"
            # Re-enumerate (selection state changes can affect what's visible).
            $tabs = $padPage.FindAll($TC, $rbCond)
            $lightTab = $tabs | Where-Object { $_.Current.Name -eq "Lighting" } | Select-Object -First 1
            if ($lightTab) {
                Click-El $lightTab -Label "Lighting Tab" -Delay 1000 | Out-Null
                Cap "pad-lighting"
            } else {
                Write-Host "  !! Lighting tab not in UIA tree" -ForegroundColor Yellow
            }

            # The DualSense on this slot also surfaces Gyro (#120 engage gate),
            # Audio (#147 haptic tones expanded the tab), and Touchpad. Capture
            # each by name; re-enumerate every time since selection can change
            # the realized tab set. These follow SelectedMappedDevice, so they
            # are present whenever the DualSense is the slot's selected device.
            foreach ($gt in @(
                @{ Name = "Gyro";     File = "pad-gyro" },
                @{ Name = "Audio";    File = "pad-audio" },
                @{ Name = "Touchpad"; File = "pad-touchpad" }
            )) {
                Write-Host "[$(Next)/$total] $($gt.Name)"
                $tabs = $padPage.FindAll($TC, $rbCond)
                $theTab = $tabs | Where-Object { $_.Current.Name -eq $gt.Name } | Select-Object -First 1
                if ($theTab) {
                    Click-El $theTab -Label "$($gt.Name) Tab" -Delay 1000 | Out-Null
                    Cap $gt.File
                } else {
                    Write-Host "  !! $($gt.Name) tab not in UIA tree" -ForegroundColor Yellow
                }
            }
        } else {
            Write-Host "  !! PadPageView not found after PS slot click" -ForegroundColor Yellow
            $n += 2
        }
    } else {
        Write-Host "  !! Only $($cards.Count) slot cards on Dashboard" -ForegroundColor Yellow
        $n += 2
    }
} else {
    Write-Host "  !! SlotsItemsControl not found" -ForegroundColor Yellow
    $n += 2
}

# ---- 14. Extended slot ----
# After type-group reorder, order from end is always: ...Extended, KBM, MIDI.
# Use offsets from end to handle variable number of Xbox / PlayStation slots.
# Use Dashboard slot cards (SlotsItemsControl) instead of sidebar nav —
# sidebar NavigationViewItems virtualize out of the UIA tree after the
# Xbox-slot tab pass, but Dashboard cards stay materialized.
Write-Host ""
Write-Host "--- Extended Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
$extendedIdx = $cards.Count - 3  # After type-group reorder: ...Extended, KBM, MIDI
if ($extendedIdx -ge 0 -and $cards.Count -ge 3) {
    Write-Host "[$(Next)/$total] Extended config bar"
    Click-El $cards[$extendedIdx] -Label "Extended Slot card" -Delay 1500 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "Extended Controller Tab" -Delay 1000 }

        # Switch profile to "Custom" to show the config bar with axis/button/POV dropdowns.
        $profileCombo = Find-UIA -Parent $padPage -Aid "HMaestroProfileCombo"
        if ($profileCombo) {
            try {
                $expandPat = $profileCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                $expandPat.Expand()
                Start-Sleep -Milliseconds 500
                # Select "Custom" (third item, index 2)
                $itemsCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::ListItem)
                $items = $profileCombo.FindAll($TC, $itemsCond)
                if ($items.Count -ge 3) {
                    $selectPat = $items[2].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                    $selectPat.Select()
                    Write-Host "  Switched to Custom profile" -ForegroundColor Green
                }
                Start-Sleep -Milliseconds 800
            } catch {
                Write-Host "  !! Could not switch profile: $_" -ForegroundColor Yellow
            }
        }
    }
    Cap "pad-extended-configbar"

    # 15. Extended schematic view
    Write-Host "[$(Next)/$total] Extended schematic view"
    $ppRect = (Find-UIA -Aid "PadPageView").Current.BoundingRectangle
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 600
    Cap "pad-extended-schematic"
    # Switch back
    [Win32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500
} else {
    Write-Host "  !! Extended slot not found" -ForegroundColor Yellow
    $n += 2
}

# ---- 16. KBM slot ----
Write-Host ""
Write-Host "--- KBM Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
$kbmIdx = $cards.Count - 2  # second from end
if ($kbmIdx -ge 0 -and $cards.Count -ge 2) {
    Write-Host "[$(Next)/$total] Keyboard+Mouse preview"
    Click-El $cards[$kbmIdx] -Label "KBM Slot card" -Delay 1500 | Out-Null
    # KBM defaults to Controller tab (keyboard+mouse preview) — no need to click a tab
    Start-Sleep -Milliseconds 800
    Cap "pad-kbm-preview"
} else {
    Write-Host "  !! KBM slot not found" -ForegroundColor Yellow
    $n++
}

# ---- 17. MIDI slot ----
Write-Host ""
Write-Host "--- MIDI Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
$midiIdx = $cards.Count - 1  # last slot
if ($midiIdx -ge 0 -and $cards.Count -ge 1) {
    Write-Host "[$(Next)/$total] MIDI config bar"
    Click-El $cards[$midiIdx] -Label "MIDI Slot card" -Delay 1500 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ($tabs.Count -gt 0) { Click-El $tabs[0] -Label "MIDI Controller Tab" -Delay 1000 }
    }
    Cap "pad-midi-configbar"
} else {
    Write-Host "  !! MIDI slot not found" -ForegroundColor Yellow
    $n++
}

# ---- 18-20. Settings (three scroll positions) ----
Write-Host ""
Write-Host "--- Settings ---" -ForegroundColor Yellow

# 18. Settings top
Write-Host "[$(Next)/$total] Settings - top"
Nav "Settings"
Start-Sleep -Milliseconds 500
Cap "settings"

# 19. Settings mid (HidHide whitelist area)
Write-Host "[$(Next)/$total] Settings - HidHide / input engine"
ScrollContent -Clicks -10
Cap "settings-hidhide"

# 20. Settings bottom (drivers)
Write-Host "[$(Next)/$total] Settings - drivers"
ScrollContent -Clicks -20
Cap "settings-drivers"

# Scroll back up
ScrollContent -Clicks 40

# ---- 21. About ----
Write-Host "[$(Next)/$total] About"
Nav "About"; Cap "about"

# ---- 22. Add Controller popup (already captured in Step 2b) ----
Write-Host "[$(Next)/$total] Add Controller popup -- already captured in Step 2b"


# ==============================================================================
# STEP 3b: New 3.6.0 sections (Pointer tab, NFC, Consumer Control, Power/battery)
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3b: 3.6.0 new sections ===" -ForegroundColor Cyan

Start-Sleep -Milliseconds 500
Start-Sleep -Milliseconds 600

$li36 = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$btn36 = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

# Robust device selection on the Devices page: the card list is vertically
# virtualized, so a target lower than the ~12 realized rows must be scrolled
# into view first. Scroll the card list (left half of the window) to the top,
# then step down, searching the realized rows after each step. ScrollAt sign
# follows ScrollContent: positive = up, negative = down.
function Select-DeviceByName36 {
    param([string]$NamePart)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $listX = [int]($wr.Left + 400)
    $midY  = [int](($wr.Top + $wr.Bottom) / 2)
    # Scroll the card list to the top first so a top-of-list target (e.g. the
    # "All Consumer Controls (Merged)" row) is realized even if a prior capture
    # left the list scrolled down. Then step down searching each realized page.
    [Win32]::ForceFG($script:hwnd)
    for ($u = 0; $u -lt 8; $u++) { [Win32]::ScrollAt($listX, $midY, 3); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 300
    for ($try = 0; $try -lt 16; $try++) {
        $items = $script:uiaWin.FindAll($TD, $li36)
        foreach ($it in $items) {
            if ($it.Current.Name -like "*$NamePart*") {
                Click-El $it -Label "Device '$NamePart'" -Delay 900 | Out-Null
                return $true
            }
        }
        [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($listX, $midY, -3); Start-Sleep -Milliseconds 350
    }
    Write-Host "  !! device '$NamePart' not found after scroll" -ForegroundColor Yellow
    return $false
}

# --- Pointer tab (Wii Remote on the Extended slot) ---
# Use the SlotsItemsControl cards (proven to work late in the run for the
# KBM/MIDI captures) rather than Find-AllSlots, which returns nothing here.
# Click each slot card and try the Pointer tab; it appears only on the slot
# whose selected device is the Wii Remote (Extended slot).
Write-Host "[3b] Pointer tab"
$ptrDone = $false
$rbCondP = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::RadioButton)
# Count the cards once.
Nav "Dashboard"; Start-Sleep -Milliseconds 1200
$slotsHostP = Find-UIA -Aid "SlotsItemsControl"
$cardCountP = if ($slotsHostP) { @($slotsHostP.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)).Count } else { 0 }
Write-Host "  Pointer: $cardCountP slot card(s)"
for ($ci = 0; $ci -lt $cardCountP -and -not $ptrDone; $ci++) {
    # Re-navigate to the Dashboard and re-find the cards EACH iteration. After
    # a card click we're on the PadPage, so a stale card reference clicks a
    # PadPage coordinate, not a Dashboard card -- which left every probe stuck
    # on the first slot. Every working slot section re-nav's Dashboard first.
    Nav "Dashboard"; Start-Sleep -Milliseconds 900
    $sh = Find-UIA -Aid "SlotsItemsControl"
    $cds = if ($sh) { @($sh.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
    if ($ci -ge $cds.Count) { continue }
    Click-El $cds[$ci] -Label "slot card $ci (Pointer probe)" -Delay 1500 | Out-Null
    # Land on the Controller tab first so the PadPage realizes, then poll for
    # the Pointer tab to flip visible (Wii's HasIrCamera gate propagates a
    # few seconds after slot bind, like the PS-slot AT/Lighting gating).
    $padPageP = Find-UIA -Aid "PadPageView"
    if (-not $padPageP) { continue }
    $tabsP = $padPageP.FindAll($TC, $rbCondP)
    if ($tabsP.Count -gt 0) { Click-El $tabsP[0] -Label "Controller Tab (Pointer probe)" -Delay 800 | Out-Null }
    $ptrVisible = $false
    for ($w = 0; $w -lt 6 -and -not $ptrVisible; $w++) {
        Start-Sleep -Milliseconds 800
        $tabsP = $padPageP.FindAll($TC, $rbCondP)
        if ($tabsP | Where-Object { $_.Current.Name -eq "Pointer" }) { $ptrVisible = $true }
    }
    Write-Host ("    card $ci tabs: " + (($tabsP | ForEach-Object { $_.Current.Name }) -join ', '))
    if ($ptrVisible -and (Tab "Pointer")) { Start-Sleep -Milliseconds 800; Cap "pad-pointer"; $ptrDone = $true }
}
if (-not $ptrDone) { Write-Host "  !! Pointer tab not reachable" -ForegroundColor Yellow }

# --- Devices page: the new 3.6.0 device types ---
Nav "Devices"; Start-Sleep -Milliseconds 900

# Capture the non-modal device panes (Consumer, Power) FIRST, then NFC last.
# The NFC "Register / Manage NFC Tags" dialog is modal; running it before the
# others left its dialog covering the screen and stole their captures.
Write-Host "[3b] Consumer Control device"
if (Select-DeviceByName36 "Consumer Control") { Cap "devices-consumer" }

Write-Host "[3b] Power / idle disconnect + battery"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "DualSense") { Cap "devices-power" }

Write-Host "[3b] NFC reader device (last -- opens a modal dialog)"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "NFC") {
    Cap "devices-nfc"
    $nfcBtn = $null
    foreach ($b in $script:uiaWin.FindAll($TD, $btn36)) {
        if ($b.Current.Name -match "NFC Tag") { $nfcBtn = $b; break }
    }
    if ($nfcBtn) {
        Click-El $nfcBtn -Label "Register/Manage NFC Tags" -Delay 1300 | Out-Null
        Cap "nfc-register"
        # Close the modal via its Close button (SendKeys ESC did not reach it).
        # The window title bar ALSO has a "Close" button (the X), so exclude the
        # top strip by position -- the dialog's Close sits low in the window.
        $wrC = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrC) | Out-Null
        $closeBtn = $null
        foreach ($b in $script:uiaWin.FindAll($TD, $btn36)) {
            if ($b.Current.Name -eq "Close") {
                $r = $b.Current.BoundingRectangle
                if ($r.Y -gt ($wrC.Top + 150)) { $closeBtn = $b; break }  # not the title-bar X
            }
        }
        if ($closeBtn) { Click-El $closeBtn -Label "Close NFC dialog" -Delay 800 | Out-Null }
        else { [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 700 }
    }
}
# ---- 23-24. Web controller ----
Write-Host "[$(Next)/$total] Web controller screenshots"
# Remove TOPMOST from PadForge and minimize it so it doesn't cover Edge
[Win32]::SetWindowPos($hwnd, [Win32]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
[Win32]::ShowWindow($hwnd, 6) | Out-Null  # SW_MINIMIZE
Start-Sleep -Milliseconds 500
$webPort = 8080
try {
    $edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }

    # Capture web pages using Edge in app mode + GDI screen capture.
    # Uses a TEMPORARY user-data-dir so the user's real Edge profile is NEVER touched.
    # Edge --app splits into multiple processes; find the window via UIA by class name.
    $edgeTempProfile = Join-Path $env:TEMP "PadForge_EdgeCapture"
    if (Test-Path $edgeTempProfile) { Remove-Item $edgeTempProfile -Recurse -Force -EA SilentlyContinue }

    function Cap-Web {
        param([string]$Url, [string]$Name, [int]$WaitMs = 5000)
        # Kill only our temp-profile Edge processes (not the user's main browser).
        # We identify them by command line containing our temp profile path.
        Get-Process msedge -EA SilentlyContinue | Where-Object {
            try { $_.CommandLine -like "*PadForge_EdgeCapture*" } catch { $false }
        } | Stop-Process -Force -EA SilentlyContinue
        Start-Sleep -Milliseconds 1500
        # Launch Edge with an isolated temp profile — never touches the default profile.
        Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --app=$Url"
        Start-Sleep -Milliseconds $WaitMs
        # Find Edge window via process handles (check all msedge processes)
        $ehwnd = [IntPtr]::Zero
        $edgeProcs = Get-Process msedge -EA SilentlyContinue
        foreach ($ep in $edgeProcs) {
            $h = $ep.MainWindowHandle
            if ($h -ne [IntPtr]::Zero) {
                $ehwnd = $h
                Write-Host "  Edge window found: PID=$($ep.Id) HWND=$h"
                break
            }
        }
        if ($ehwnd -eq [IntPtr]::Zero) {
            Write-Host "  !! No Edge window found via process handles" -ForegroundColor Yellow
            Get-Process msedge -EA SilentlyContinue | Where-Object {
                try { $_.CommandLine -like "*PadForge_EdgeCapture*" } catch { $false }
            } | Stop-Process -Force -EA SilentlyContinue
            Start-Sleep -Milliseconds 500
            return
        }
        # Resize Edge to a consistent 1280x720 at position (200,200)
        [Win32]::SetWindowPos($ehwnd, [IntPtr]::Zero, 200, 200, 1280, 720, 0x0040) | Out-Null  # SWP_SHOWWINDOW
        Start-Sleep -Milliseconds 300
        [Win32]::ForceFG($ehwnd)
        Start-Sleep -Milliseconds 500
        $er = New-Object Win32+RECT
        [Win32]::GetWindowRect($ehwnd, [ref]$er) | Out-Null
        $ew = $er.Right - $er.Left; $eh = $er.Bottom - $er.Top
        Write-Host "  Edge rect: ${ew}x${eh} at ($($er.Left),$($er.Top))"
        if ($ew -gt 100 -and $eh -gt 100) {
            $bmp = New-Object System.Drawing.Bitmap($ew, $eh)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($er.Left, $er.Top, 0, 0, [System.Drawing.Size]::new($ew, $eh))
            $g.Dispose()
            $p = Join-Path $script:OutputDir "$Name.png"
            $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            $kb = [math]::Round((Get-Item $p).Length / 1024)
            Write-Host "  >> $Name.png (${kb}KB)" -ForegroundColor Green
        } else {
            Write-Host "  !! Edge window too small: ${ew}x${eh}" -ForegroundColor Yellow
        }
        # Kill only our temp-profile Edge processes (use WMI for CommandLine access).
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
            Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }

    # Landing page (needs a few seconds for Edge to fully render)
    Cap-Web "http://localhost:${webPort}/" "web-landing" 6000

    # Controller page (needs WebSocket for layout images)
    Write-Host "[$(Next)/$total] Web controller - gamepad"
    Cap-Web "http://localhost:${webPort}/controller.html?layout=xbox360" "web-controller" 6000

    # Bring PadForge back to foreground after web captures
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300

} catch {
    Write-Host "  !! Web screenshots failed: $($_.Exception.Message)" -ForegroundColor Yellow
    $n++
}


# ==============================================================================
# STEP 4: Cleanup
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 4: Cleanup ===" -ForegroundColor Cyan

[Win32]::SetWindowPos($hwnd, [Win32]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null

# Stop PadForge, restore XML
Write-Host "  Stopping PadForge..."
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Copy-Item $xmlBak $PadForgeXml -Force
Remove-Item $xmlBak -Force
Write-Host "  Restored PadForge.xml from backup" -ForegroundColor Green

# Clean up temporary Edge profile used for web screenshots.
$edgeTempProfile = Join-Path $env:TEMP "PadForge_EdgeCapture"
if (Test-Path $edgeTempProfile) {
    Remove-Item $edgeTempProfile -Recurse -Force -EA SilentlyContinue
    Write-Host "  Cleaned up temporary Edge profile"
}

Stop-Transcript | Out-Null

Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Cyan
Write-Host "Screenshots in: $OutputDir"
Write-Host ""
Get-ChildItem "$OutputDir\*.png" | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0} ({1}KB)" -f $_.Name, [math]::Round($_.Length / 1024))
}
