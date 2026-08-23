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
    # The GitHub wiki was RETIRED to pointer pages on 2026-07-30; the live
    # documentation is Material for MkDocs in the padforge.org repo, source
    # under wiki/ and the built site committed to docs/. Capturing into the
    # old PadForge.wiki\images ships nothing, so this points at the docs
    # source that is actually published.
    [string]$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml",
    # Tail mode: reuse the capture-configured PadForge.xml an aborted full
    # run left behind (slots + dummies + assignments intact, owner backup
    # still in .bak) and jump straight to the STEP 3b tail on a FRESH app
    # process. Exists because the WPF UIA tree degrades over a marathon
    # run and late-run device FindAlls hang (2026-07-30, twice).
    [switch]$SkipToTail,
    # Capture ONLY these shots (by Cap name), e.g.
    #   -SkipToTail -Only midi-input,devices-nfc
    # Every other Cap becomes a no-op, so a handful of stale images can be
    # refreshed without re-photographing 116 of them. Combine with
    # -SkipToTail to skip the per-pad-page passes as well; tail mode now
    # PREPARES the environment itself when no capture-configured settings
    # file is lying around, so a targeted refresh is a single command from
    # a clean machine.
    [string[]]$Only = @(),
    # UI-settle scale. Every Start-Sleep -Milliseconds in this script is a
    # guess at how long WPF needs to finish a transition, and the guesses were
    # made one at a time and always upward: 225 call sites totalling 145
    # seconds of dead wait, on top of an 800ms delay after EVERY click. A full
    # run spent more time asleep than working.
    #
    # Scaling happens in ONE place (the Start-Sleep proxy below) rather than by
    # editing 225 literals, so the ratios between waits are preserved and the
    # whole harness can be tuned or reverted with one number. Process-lifecycle
    # waits (-Seconds: app kill, restart, driver settle) are NOT scaled: those
    # are waiting on real work, not on a repaint.
    [double]$SettleScale = 0.45,
    # Floor, so a scaled wait never collapses to nothing on a fast machine.
    [int]$SettleFloorMs = 150
)

Set-StrictMode -Version Latest

# Proxy that shadows the cmdlet for the whole script. -Milliseconds waits are
# UI settle time and get scaled; -Seconds waits are process lifecycle and pass
# through untouched.
$script:SettleScaleValue = $SettleScale
$script:SettleFloorValue = $SettleFloorMs
function Start-Sleep {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)][int]$Seconds,
        [int]$Milliseconds
    )
    if ($PSBoundParameters.ContainsKey('Milliseconds')) {
        $scaled = [int]($Milliseconds * $script:SettleScaleValue)
        if ($Milliseconds -gt 0 -and $scaled -lt $script:SettleFloorValue) {
            $scaled = [math]::Min($Milliseconds, $script:SettleFloorValue)
        }
        if ($scaled -gt 0) { Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds $scaled }
        return
    }
    if ($PSBoundParameters.ContainsKey('Seconds') -and $Seconds -gt 0) {
        Microsoft.PowerShell.Utility\Start-Sleep -Seconds $Seconds
    }
}
$ErrorActionPreference = "Stop"

# NOT beside the exe. The standing bar is that only PadForge.xml and crash.log
# may ever sit in the deploy directory, and this transcript was breaking it on
# every run: the 2026-08-09 release prep found six stray log files there, all
# written by this harness and its siblings. TEMP keeps them out of the way and
# out of the hygiene sweep.
$logDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
$logPath = Join-Path $logDir "capture_log.txt"
$script:CaptureRunStart = Get-Date

# --- Single instance ---------------------------------------------------------
# Two capture runs at once fight over the settings file, the foreground window
# and this transcript, and the second one silently inherits the first one's
# half-finished state. On 2026-08-09 a run launched before a script edit was
# still going when the next was launched, so the "fixed" run was actually the
# old script and its log looked like the fix had failed. Refuse to start.
$lockPath = Join-Path $logDir "capture.lock"
if (Test-Path $lockPath) {
    $otherPid = (Get-Content $lockPath -EA SilentlyContinue | Select-Object -First 1)
    $alive = $false
    if ($otherPid) { $alive = [bool](Get-Process -Id ([int]$otherPid) -EA SilentlyContinue) }
    if ($alive) {
        Write-Host "!! A capture is ALREADY RUNNING (pid $otherPid). Refusing to start a second." -ForegroundColor Red
        Write-Host "!! Wait for it, or stop it, then run again." -ForegroundColor Red
        exit 1
    }
    Remove-Item $lockPath -Force -EA SilentlyContinue
}
Set-Content -Path $lockPath -Value $PID -Encoding ascii
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
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int sz);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    // Top-level windows belonging to a PID (workshop/pair FluentWindow modals are
    // UIA-shy from RootElement; EnumWindows + FromHandle is the proven discovery,
    // same mechanic as tools/diag-sweep.ps1).
    public static System.Collections.Generic.List<IntPtr> WindowsForPid(uint want) {
        var r = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == want) r.Add(h); return true; }, IntPtr.Zero);
        return r;
    }

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

function Reset-PadForgeUia {
    # The WPF UIA tree degrades over a long capture run: late FindAlls come back
    # empty for elements that are plainly on screen. The script header has noted
    # this since 2026-07-30 and -SkipToTail exists because of it. The KBM block
    # sits near the end and hit exactly that, scanning 0 slot cards four times
    # over, which cost pad-kbm-preview, pad-kbm-socd and pad-mouse-gestures.
    # A fresh process gets a fresh tree. Settings are already on disk, so this
    # costs a restart and nothing else.
    param([string]$ExePath)
    Write-Host "  restarting PadForge for a fresh UIA tree" -ForegroundColor Cyan
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Start-Process $ExePath
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Seconds 1
        $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($pr -and $pr.MainWindowHandle -ne 0) { break }
    }
    Start-Sleep -Seconds 6
    $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    if (-not $pr -or $pr.MainWindowHandle -eq 0) { Write-Host "  !! PadForge did not come back" -ForegroundColor Red; return $false }
    # REBIND THE PROCESS TOO, not just the window. Every modal helper keys
    # on $script:proc.Id: Close-AnyModal filters candidate windows by it,
    # and Get-ForegroundDialogHwnd / Find-DialogHwndByEnum enumerate by it.
    # Leaving it pointed at the process this restart just KILLED makes all
    # three silently find nothing, because no live window carries a dead
    # PID. That is how the Voice Macros modal survived every close attempt
    # across two full runs, disabled the main window, and shipped as six
    # later screenshots while the log read "hwnd not found by enum" and the
    # leak counter read zero. All three restart helpers had it.
    $script:proc = $pr
    $script:hwnd = $pr.MainWindowHandle
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    return $true
}

function Get-Count {
    # Set-StrictMode -Version Latest makes a .Count read on anything that is not
    # a collection a TERMINATING error, and UIA hands back such values routinely.
    # This exact trap has now killed three separate runs: at $r.IsEmpty, at
    # (Get-Count $cds), and at (Get-Count $cards) in the KBM block, where the "fix" for the
    # second one was not applied to the third. Count through here, always.
    param($Value)
    if ($null -eq $Value) { return 0 }
    try { return ([object[]]$Value).Length } catch { }
    try { return $Value.Count } catch { }
    return 0
}

function Get-Rect {
    # UIA can hand back a BoundingRectangle that is not a System.Windows.Rect:
    # a stale element, or a virtualized row that scrolled out between the
    # FindFirst and the read. Under Set-StrictMode -Version Latest, reading
    # .IsEmpty off that object is a TERMINATING error, and on 2026-08-09 one
    # stale device card killed a whole capture run at the Logitech G29, taking
    # the last six shots AND the settings restore with it. Every rect read goes
    # through here now. It returns $null rather than throwing, and callers
    # treat $null as "skip this element" instead of dying.
    param($El)
    if (-not $El) { return $null }
    try { $r = $El.Current.BoundingRectangle } catch { return $null }
    if ($null -eq $r) { return $null }
    if ($r -isnot [System.Windows.Rect]) { return $null }
    try { if ($r.IsEmpty) { return $null } } catch { return $null }
    if ($r.Width -le 0 -or $r.Height -le 0) { return $null }
    return $r
}

function Click-El {
    param(
        [System.Windows.Automation.AutomationElement]$El,
        # Was 800. A click needs long enough for the target to react, not
        # long enough to be sure, and several hundred clicks a run made this
        # the single largest line item in the harness's wall clock. Callers
        # that genuinely need longer already pass -Delay explicitly.
        [int]$Delay = 400,
        [string]$Label
    )
    if (-not $El) { Write-Host "  !! NOT FOUND: $Label" -ForegroundColor Red; return $false }
    # Height is checked alongside Width inside Get-Rect. IsEmpty alone does not
    # catch a rect with width but no height, and such an element passed the old
    # guard and got clicked at its top edge, landing on whatever sat above it.
    $r = Get-Rect $El
    if ($null -eq $r) {
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

function Ensure-DeviceAssigned {
    # Driving the Devices page to assign a device is the flakiest chain in this
    # script: scroll the virtualized card list, wait for the detail pane to
    # realize, enumerate toggles, click the right one. The Xbox GIP dummy lost
    # that fight run after run, and pad-impulse-triggers plus
    # pad-lighting-guide-led stayed weeks stale because the tabs they need only
    # appear when that device is selectable in the pad page DEVICE dropdown.
    #
    # An assignment is nothing but UserSetting.MapTo, so write it. The comment
    # further down this script has said to do exactly this since 2026-07-30.
    # MapTo is the ZERO-BASED pad index (InputManager filters on
    # `us.MapTo == slot`), not the UI slot number.
    #
    # Runs with PadForge closed and restarts it, which is the state proven to
    # load settings cleanly.
    # SlotType resolves the pad index by VirtualControllerType instead of
    # trusting a hardcoded number. Dashboard CARD order is type-group order
    # (Xbox, PlayStation, Nintendo, Extended, KBM, MIDI, VR) while PAD INDEX is
    # creation order, and they are not the same: on 2026-08-10 the slots came
    # out 0,1,5,4,2,2, so the KBM slot was pad 3 while its card sat fifth. A
    # mouse mapped to "pad 4" landed on an Extended slot and the Mouse tab
    # never appeared. Pass the type and let the file say where it is.
    param([string]$DeviceNamePart, [int]$PadIndex, [int]$SlotType = -1,
          [string]$XmlPath, [string]$ExePath)

    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    try {
        [xml]$ax = Get-Content $XmlPath
        $root = $ax.PadForgeSettings

        if ($SlotType -ge 0) {
            $typesNode = $root.SelectSingleNode("SlotControllerTypes")
            if ($typesNode) {
                $i = 0; $resolved = -1
                foreach ($t in $typesNode.ChildNodes) {
                    if ("$($t.InnerText)".Trim() -eq "$SlotType") { $resolved = $i; break }
                    $i++
                }
                if ($resolved -ge 0) {
                    if ($resolved -ne $PadIndex) {
                        Write-Host "  slot type $SlotType is pad $resolved (not $PadIndex); using $resolved" -ForegroundColor DarkGray
                    }
                    $PadIndex = $resolved
                } else {
                    Write-Host "  !! no slot of type $SlotType in SlotControllerTypes" -ForegroundColor Yellow
                }
            }
        }

        $devsNode = $root.SelectSingleNode("Devices")
        if (-not $devsNode) { Write-Host "  !! no <Devices> node" -ForegroundColor Red; Start-Process $ExePath; return $false }
        $dev = $null
        foreach ($d in $devsNode.ChildNodes) {
            $nameNode = $d.SelectSingleNode("InstanceName")
            if ($nameNode -and $nameNode.InnerText -like "*$DeviceNamePart*") { $dev = $d; break }
        }
        if (-not $dev) { Write-Host "  !! device '$DeviceNamePart' not in <Devices>" -ForegroundColor Red; Start-Process $ExePath; return $false }

        $guid = $dev.SelectSingleNode("InstanceGuid").InnerText
        $pguidNode = $dev.SelectSingleNode("ProductGuid")
        $pguid = if ($pguidNode) { $pguidNode.InnerText } else { $guid }
        $iname = $dev.SelectSingleNode("InstanceName").InnerText

        $usNode = $root.SelectSingleNode("UserSettings")
        if (-not $usNode) { $usNode = $ax.CreateElement("UserSettings"); $root.AppendChild($usNode) | Out-Null }

        # Already mapped to this pad? Nothing to do.
        foreach ($st in $usNode.ChildNodes) {
            $g = $st.SelectSingleNode("InstanceGuid"); $mt = $st.SelectSingleNode("MapTo")
            if ($g -and $mt -and $g.InnerText -eq $guid -and [int]$mt.InnerText -eq $PadIndex) {
                Write-Host "  '$iname' already mapped to pad $PadIndex"
                Start-Process $ExePath; Start-Sleep -Seconds 8
                return $true
            }
        }

        # Clone an existing row so every field the serializer expects is present.
        $template = $usNode.FirstChild
        if ($template) {
            $row = $template.CloneNode($true)
            foreach ($pair in @(@("InstanceGuid", $guid), @("ProductGuid", $pguid),
                                @("InstanceName", $iname), @("ProductName", $iname),
                                @("MapTo", "$PadIndex"))) {
                $n = $row.SelectSingleNode($pair[0])
                if ($n) { $n.InnerText = $pair[1] }
            }
        } else {
            $row = $ax.CreateElement("Setting")
            foreach ($pair in @(@("InstanceGuid", $guid), @("ProductGuid", $pguid),
                                @("InstanceName", $iname), @("ProductName", $iname),
                                @("MapTo", "$PadIndex"))) {
                $e = $ax.CreateElement($pair[0]); $e.InnerText = $pair[1]; $row.AppendChild($e) | Out-Null
            }
        }
        $usNode.AppendChild($row) | Out-Null
        $ax.Save($XmlPath)
        Write-Host "  mapped '$iname' to pad $PadIndex by XML" -ForegroundColor Green
    } catch {
        Write-Host "  !! assignment write failed: $($_.Exception.Message)" -ForegroundColor Red
        Start-Process $ExePath
        return $false
    }

    Start-Process $ExePath
    $ok = $false
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Seconds 1
        $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($pr -and $pr.MainWindowHandle -ne 0) { $ok = $true; break }
    }
    if (-not $ok) { Write-Host "  !! PadForge did not come back up" -ForegroundColor Red; return $false }
    Start-Sleep -Seconds 6
    $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    # REBIND THE PROCESS TOO, not just the window. Every modal helper keys
    # on $script:proc.Id: Close-AnyModal filters candidate windows by it,
    # and Get-ForegroundDialogHwnd / Find-DialogHwndByEnum enumerate by it.
    # Leaving it pointed at the process this restart just KILLED makes all
    # three silently find nothing, because no live window carries a dead
    # PID. That is how the Voice Macros modal survived every close attempt
    # across two full runs, disabled the main window, and shipped as six
    # later screenshots while the log read "hwnd not found by enum" and the
    # leak counter read zero. All three restart helpers had it.
    $script:proc = $pr
    $script:hwnd = $pr.MainWindowHandle
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    return $true
}

# The 4.3.2 audio DSP chain (#347) renders on the DualSense's Audio tab below
# Output Path, and the EQ rows and curve render only while the EQ is ON with
# bands authored. A scroll-down shot of the stock tab would show an empty
# curve and no rows, which is the "captured whatever was on screen" failure
# this harness is built to refuse. Per-device settings are one <Config>
# attribute row keyed by SlotIndex + DeviceGuid, so write the EQ the way the
# assignments are written: with the app closed, then restart. The synthetic
# DualSense carries a fixed guid. Jan Meier crossfeed (level 7) plus a
# four-band correction in AutoEq's own shape, and the limiter at its default.
function Seed-AudioDsp {
    param([string]$DeviceGuid, [string]$DeviceNamePart, [int]$PadIndex, [string]$XmlPath, [string]$ExePath)
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    try {
        [xml]$ax = Get-Content $XmlPath
        $root = $ax.PadForgeSettings
        # Resolve the guid from the device list by NAME when asked. The
        # synthetic DualSense carries a fixed guid, but on a machine where the
        # owner's real pad is already cached the injector skips the synthetic
        # one ("already enumerated, synthetic skipped") and the slot holds the
        # REAL pad's guid instead. A seed keyed to the synthetic guid then
        # lands on a device that is not on the slot and the shot shows the
        # chain switched off, which is what the first three attempts did.
        if ($DeviceNamePart) {
            $devsNode = $root.SelectSingleNode("Devices")
            if ($devsNode) {
                foreach ($d in $devsNode.ChildNodes) {
                    $nameNode = $d.SelectSingleNode("InstanceName")
                    if ($nameNode -and $nameNode.InnerText -like "*$DeviceNamePart*") {
                        $DeviceGuid = $d.SelectSingleNode("InstanceGuid").InnerText
                        Write-Host "  DSP seed: resolved '$DeviceNamePart' to $($DeviceGuid.Substring(0,8))" -ForegroundColor DarkGray
                        break
                    }
                }
            }
        }
        # The regenerated capture file has no <DeviceSlotConfigs> at all until
        # some per-device card is touched (the first full run bailed here with
        # "no <DeviceSlotConfigs> node" and photographed an empty chain). Create
        # the container and the row. Every attribute the serializer reads has
        # a default, so a row carrying only the keys and the DSP attributes
        # loads as a stock config plus the EQ.
        # DeviceSlotConfigs is a child of <AppSettings>, not of the root.
        # The serializer's AppSettingsData owns the bag ([XmlArray
        # "DeviceSlotConfigs"] on AppSettingsData, reached through
        # PadForgeSettings/AppSettings). Writing it at the root created a
        # node the loader never reads, so the seed landed in the file and
        # the app came up with the chain off, twice, while the log reported
        # success. The 1u rule in reverse: the artifact LOOKED right.
        $appNode = $root.SelectSingleNode("AppSettings")
        if (-not $appNode) { Write-Host "  !! no <AppSettings> node" -ForegroundColor Red; Start-Process $ExePath; Start-Sleep 8; return $false }
        $cfgs = $appNode.SelectSingleNode("DeviceSlotConfigs")
        if (-not $cfgs) {
            $cfgs = $ax.CreateElement("DeviceSlotConfigs")
            $appNode.AppendChild($cfgs) | Out-Null
            Write-Host "  created <AppSettings>/<DeviceSlotConfigs>" -ForegroundColor DarkGray
        }
        $row = $null
        foreach ($c in $cfgs.ChildNodes) {
            if ($c.GetAttribute("DeviceGuid") -eq $DeviceGuid -and $c.GetAttribute("SlotIndex") -eq "$PadIndex") { $row = $c; break }
        }
        if (-not $row) {
            $tpl = $cfgs.FirstChild
            if ($tpl) {
                # Clone an existing row so every attribute is present.
                $row = $tpl.CloneNode($true)
            } else {
                $row = $ax.CreateElement("Config")
            }
            $row.SetAttribute("SlotIndex", "$PadIndex")
            $row.SetAttribute("DeviceGuid", $DeviceGuid)
            $cfgs.AppendChild($row) | Out-Null
        }
        $row.SetAttribute("AudioCrossfeedLevel", "7")
        $row.SetAttribute("AudioEqEnabled", "true")
        $row.SetAttribute("AudioEqPreampDb", "-4.5")
        $row.SetAttribute("AudioEqBands", "LSC:105:5.5:0.7:1|PK:1050:-3.5:1.2:1|PK:3200:2.5:2:1|HSC:8000:-2:0.7:1")
        $row.SetAttribute("AudioLimiterEnabled", "true")
        $ax.Save($XmlPath)
        Write-Host "  seeded audio DSP (crossfeed + 4-band EQ) on pad $PadIndex / $($DeviceGuid.Substring(0,8))" -ForegroundColor Green
    } catch {
        Write-Host "  !! audio DSP seed failed: $($_.Exception.Message)" -ForegroundColor Red
        Start-Process $ExePath; Start-Sleep 8
        return $false
    }
    Start-Process $ExePath
    $ok = $false
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Seconds 1
        $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($pr -and $pr.MainWindowHandle -ne 0) { $ok = $true; break }
    }
    if (-not $ok) { Write-Host "  !! PadForge did not come back up" -ForegroundColor Red; return $false }
    Start-Sleep -Seconds 6
    $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    # Rebind process + window + UIA root, exactly as Ensure-DeviceAssigned
    # does and for the same reason: every modal helper keys on $script:proc.Id.
    $script:proc = $pr
    $script:hwnd = $pr.MainWindowHandle
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    return $true
}

function Ensure-MacrosLoaded {
    # "Injected the macros" is not the same as "the app is showing macros", and
    # on 2026-08-09 the gap between those two shipped five blank screenshots.
    # The macros survive a load when they are written while PadForge is CLOSED
    # (proven: inject, launch, exit, and the file comes back re-serialized with
    # all five and their actions). During a capture run something between
    # startup and the Macros tab empties them. Rather than keep guessing at
    # which step, write them again with the app closed and restart, which is
    # the state that is known to work. Returns $true when macros are present.
    param([string]$XmlPath, [string]$ExePath)

    Write-Host "  Ensuring macros: rewriting them with PadForge closed, then restarting" -ForegroundColor Cyan

    $src = Get-Content $PSCommandPath -Raw
    $frags = [regex]::Matches($src, "(?s)<Macro PadIndex=""0"">.*?</Macro>") | ForEach-Object { $_.Value }
    if (-not $frags -or @($frags).Count -eq 0) {
        Write-Host "  !! no macro fragments found in this script" -ForegroundColor Red
        return $false
    }

    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    try {
        [xml]$mx = Get-Content $XmlPath
        $mroot = $mx.PadForgeSettings.SelectSingleNode("Macros")
        if (-not $mroot) {
            $mroot = $mx.CreateElement("Macros")
            $mx.PadForgeSettings.AppendChild($mroot) | Out-Null
        }
        while ($mroot.HasChildNodes) { $mroot.RemoveChild($mroot.FirstChild) | Out-Null }
        foreach ($f in $frags) {
            $fr = $mx.CreateDocumentFragment()
            $fr.InnerXml = $f.Trim()
            $mroot.AppendChild($fr) | Out-Null
        }
        $mx.Save($XmlPath)
        Write-Host "  wrote $(@($frags).Count) macros into the closed settings file"
    } catch {
        Write-Host "  !! could not write macros: $($_.Exception.Message)" -ForegroundColor Red
        Start-Process $ExePath
        return $false
    }

    Start-Process $ExePath
    $ok = $false
    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Seconds 1
        $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if ($pr -and $pr.MainWindowHandle -ne 0) { $ok = $true; break }
    }
    if (-not $ok) { Write-Host "  !! PadForge did not come back up" -ForegroundColor Red; return $false }

    Start-Sleep -Seconds 6
    $pr = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
    # REBIND THE PROCESS TOO, not just the window. Every modal helper keys
    # on $script:proc.Id: Close-AnyModal filters candidate windows by it,
    # and Get-ForegroundDialogHwnd / Find-DialogHwndByEnum enumerate by it.
    # Leaving it pointed at the process this restart just KILLED makes all
    # three silently find nothing, because no live window carries a dead
    # PID. That is how the Voice Macros modal survived every close attempt
    # across two full runs, disabled the main window, and shipped as six
    # later screenshots while the log read "hwnd not found by enum" and the
    # leak counter read zero. All three restart helpers had it.
    $script:proc = $pr
    $script:hwnd = $pr.MainWindowHandle
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    Write-Host "  PadForge restarted for macros (HWND=$($script:hwnd))" -ForegroundColor Green
    return $true
}

# Modal-leak counter, reported by the end-of-run coverage audit. A stuck
# modal is the highest-blast-radius failure this harness has: it disables
# the main window, so EVERY later Cap silently photographs the same frozen
# dialog under the wrong name (2026-08-19: six shots shipped that way).
$script:modalLeaks = 0

# True when this shot was asked for. With no -Only, everything is wanted.
function Want {
    param([string]$Name)
    if ($Only.Count -eq 0) { return $true }
    return ($Only -contains $Name)
}

function Cap {
    param([string]$Name, [switch]$AllowModal)
    if (-not (Want $Name)) {
        Write-Host "  .. skipped $Name (not in -Only)" -ForegroundColor DarkGray
        return
    }
    # Warn, loudly and by name, when a dialog is up for a shot that is not
    # a dialog shot. Deliberately does NOT auto-dismiss: several steps
    # legitimately photograph modals, and guessing wrong there would break
    # working captures. The point is that a leak can never again be
    # invisible in the log or the audit.
    if (-not $AllowModal -and $script:hwnd) {
        try {
            $leak = Find-DialogHwndByEnum -Retries 1 -DelayMs 0
            if ($leak -and [IntPtr]$leak -ne [IntPtr]::Zero) {
                Write-Host "  !! MODAL LEAK: a dialog is open while capturing '$Name'" -ForegroundColor Red
                $script:modalLeaks++
            }
        } catch {}
    }
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
        # Was 800. A click needs long enough for the target to react, not
        # long enough to be sure, and several hundred clicks a run made this
        # the single largest line item in the harness's wall clock. Callers
        # that genuinely need longer already pass -Delay explicitly.
        [int]$Delay = 400,
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

# A UIA lookup that misses is not proof the element is absent. The
# 2026-08-19 run enumerated 'Add Controller' in the nav diagnostic and then
# got NULL from an exact-name search nine seconds later, with nothing having
# clicked in between, and the run refused to capture because all seven slot
# types "failed". An elevated probe against the same build found the item
# immediately, so the miss was transient realization, not absence. Retry
# before believing a null, wherever a null aborts the run.
function Find-UIARetry {
    param([string]$Name, [string]$Aid,
          [System.Windows.Automation.ControlType]$CT,
          [int]$Retries = 5, [int]$DelayMs = 600)
    for ($i = 0; $i -lt $Retries; $i++) {
        $el = if ($CT) { Find-UIA -Name $Name -Aid $Aid -CT $CT }
              else     { Find-UIA -Name $Name -Aid $Aid }
        if ($el) { return $el }
        Start-Sleep -Milliseconds $DelayMs
    }
    return $null
}

function Nav {
    param([string]$Name)
    foreach ($ctName in @("ListItem", "TreeItem")) {
        $ct = [System.Windows.Automation.ControlType]::$ctName
        $el = Find-UIA -Name $Name -CT $ct
        if ($el) { return (Select-El $el -Label $Name) }
    }
    $el = Find-UIARetry -Name $Name
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
        # Sidebar nav items surface as DataItem, NOT ListItem. A ListItem-only
        # search returns an empty set, which reads exactly like "no slots
        # exist" even when all six were just created successfully, and then
        # every device assignment downstream silently no-ops (0 toggles ->
        # dropdowns empty -> wheel / impulse-triggers / consumer / guide-LED /
        # balance-source shots all stay stale). Match BOTH control types and
        # let the ClassName filter below do the real discrimination.
        $orCond = New-Object System.Windows.Automation.OrCondition(@(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::DataItem))
        ))
        $all = $searchIn.FindAll($TD, $orCond)
        $slots = @()
        foreach ($item in $all) {
            $n = $item.Current.Name
            $cls = $item.Current.ClassName
            # Slot entries report ClassName 'ItemsControlItem' now, not
            # 'NavigationViewItem'. Accept both, and drop the entries whose
            # Name is a namespace-qualified type ('Wpf.Ui.Controls.
            # NavigationViewItemSeparator', 'System.Windows.Controls.Grid'),
            # which is what the class check used to filter out for free.
            if (($cls -eq "NavigationViewItem" -or $cls -eq "ItemsControlItem") -and
                ($n -match '^Pad\d+$' -or ($n -notin $skip -and $n.Length -gt 0 -and $n -notmatch '\.'))) {
                Write-Host "    Slot: '$n' (class=$cls)"
                $slots += $item
            }
        }
        if ((Get-Count $slots) -gt 0) {
            Write-Host "  Found $(Get-Count $slots) slot(s) on attempt $attempt"
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

# Select a device in the PadPage's mapped-device dropdown by name, so the
# device-gated tabs (Impulse Triggers, Wheel, etc.) follow it. A slot can carry
# several devices; the tabs reflect whichever is picked here. Walks every
# ComboBox in the PadPage (device dropdown + Preset + Profile) and only selects
# on the one whose items include the device name, so it never disturbs the
# preset/profile combos.
function Select-MappedDevice {
    param([string]$NamePart, [int]$Retries = 3)
    for ($smAttempt = 1; $smAttempt -le $Retries; $smAttempt++) {
        if (Select-MappedDeviceOnce $NamePart) { return $true }
        Write-Host "  Select-MappedDevice '$NamePart' attempt $smAttempt failed; retrying" -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 900
    }
    return $false
}
function Select-MappedDeviceOnce {
    param([string]$NamePart)
    $padPage = Find-UIA -Aid "PadPageView"
    if (-not $padPage) { Write-Host "  !! Select-MappedDevice: no PadPageView" -ForegroundColor Yellow; return $false }
    $cbCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    foreach ($combo in $padPage.FindAll($TD, $cbCond)) {
        $expand = $null
        try { $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $expand.Expand(); Start-Sleep -Milliseconds 500 } catch { continue }
        $match = $null
        foreach ($it in $combo.FindAll($TD, $liCond)) {
            if ($it.Current.Name -like "*$NamePart*") { $match = $it; break }
        }
        if ($match) {
            try { $match.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
            catch { Click-El $match -Label "device '$NamePart'" | Out-Null }
            Start-Sleep -Milliseconds 1200
            try { $expand.Collapse() } catch {}
            Write-Host "  Selected mapped device '$NamePart'" -ForegroundColor Green
            return $true
        }
        try { $expand.Collapse(); Start-Sleep -Milliseconds 200 } catch {}
    }
    Write-Host "  !! mapped device '$NamePart' not found in dropdown" -ForegroundColor Yellow
    return $false
}

# Toggle the PadPage 2D/3D view. The button carries AutomationId
# "ViewModeToggle" (PadPage.xaml:955) but usually has NO UIA PEER AT ALL:
# the Helix viewport host strips its whole subtree from the automation
# tree, so neither the Aid lookup nor an all-Buttons scan of that corner
# finds anything (probed empirically 2026-07-30). The coordinate is
# therefore the normal path, not the exception.
#
# The 4.1.0 prep shipped a WRONG pad-controller-2d because the old blind
# offset (ppRect+52,+124) missed the button and the "2D" shot showed the
# 3D model. Measured position on the maximized window (2582x1550):
# window-relative (431, 249), which is ppRect + (41, 61).
#
# DETERMINISTIC ALTERNATIVE, preferred when a run can control the xml:
# the view is a PERSISTED SETTING (AppSettings/Use2DControllerView, flipped
# by PadPage.xaml.cs:206). Writing it in STEP 0 opens the PadPage in 2D
# with no click at all. Use that when adding a new 2D capture.
function Toggle-ViewMode {
    $vmBtn = Find-UIA -Aid "ViewModeToggle"
    if (-not $vmBtn) {
        # Re-attach to the window; a stale cached element tree can hide
        # freshly-realized peers.
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        $vmBtn = Find-UIA -Aid "ViewModeToggle"
    }
    if ($vmBtn) { return (Click-El $vmBtn -Label "ViewModeToggle" -Delay 700) }
    Write-Host "  ViewModeToggle has no UIA peer (expected); measured coordinate" -ForegroundColor DarkGray
    $pp = Find-UIA -Aid "PadPageView"
    if (-not $pp) { Write-Host "  !! Toggle-ViewMode: no PadPageView" -ForegroundColor Red; return $false }
    $pr = Get-Rect $pp
    if ($null -eq $pr) { Write-Host "  !! Toggle-ViewMode: PadPageView has no rect" -ForegroundColor Red; return $false }
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
    [Win32]::ClickAt([int]($pr.X + 41), [int]($pr.Y + 61))
    Start-Sleep -Milliseconds 900
    return $true
}

# Capture a mapping row's Source ComboBox dropdown for the currently-mapped
# device, with the gated Wii source (Balance / IR Brightness / Mouse Motion)
# scrolled into view. The DataGrid cell ComboBoxes expose NO UIA peers (WPF-UI
# virtualized grid), so this is driven by COORDINATE + KEYBOARD: click the first
# row's Source-cell chevron to open the dropdown, then type-ahead to the gated
# source (WPF ComboBox jumps selection to the first item whose text starts with
# the typed prefix, scrolling it into the visible popup). Assumes the target
# device is ALONE on the slot (single-source grid) and already navigated to.
function Capture-SourcePicker {
    param([string]$DeviceNamePart, [string]$TypeAhead, [string]$ShotName)
    Select-MappedDevice $DeviceNamePart | Out-Null
    Start-Sleep -Milliseconds 800
    if (-not (Tab "Mappings")) { Write-Host "  !! picker: Mappings tab not found" -ForegroundColor Yellow; return }
    Start-Sleep -Milliseconds 1200
    $wrP = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrP) | Out-Null
    $pw = $wrP.Right - $wrP.Left; $ph = $wrP.Bottom - $wrP.Top
    [Win32]::ForceFG($script:hwnd)
    # HEAD geometry (measured off the committed 2582x1550 mappings.jpg + the
    # expanded-row wii-balance-sources.jpg). No Clear All / Map All: their old
    # left-toolbar fractions now land on Copy / "+ Shift Layer" (the toolbar
    # was rearranged), and the rebuild is unnecessary anyway -- the DualSense
    # stays assigned to slot 1 as the stable row PRIMARY, and each swap-on
    # picker device contributes exactly one sub-source per row. Clicking a row
    # expands the inline details editor: PRIMARY MODE (+0.050 H), COMBINE
    # (+0.091 H), then the swap-on device's sub-source combo (+0.129 H).
    # Rows start at 0.206 H, 0.0251 H apart; use the X row (output index 2).
    $rowY = 0.206 + 2 * 0.0251
    [Win32]::ClickAt([int]($wrP.Left + 0.25 * $pw), [int]($wrP.Top + $rowY * $ph)); Start-Sleep -Milliseconds 1000
    # Sub-source combo of the expanded X row (the swap-on device's own picker):
    # 0.329 W, 0.385 H on the reference shot.
    [Win32]::ClickAt([int]($wrP.Left + 0.329 * $pw), [int]($wrP.Top + 0.385 * $ph)); Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($TypeAhead); Start-Sleep -Milliseconds 800  # type-ahead to the gated source
    Write-Host "  picker: expanded X row + opened '$DeviceNamePart' sub-source combo, typed '$TypeAhead'" -ForegroundColor Green
    Cap $ShotName
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400  # close dropdown
}

function ScrollContent {
    param([int]$Clicks = -15)
    $sr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$sr) | Out-Null
    $cx = [int](($sr.Left + $sr.Right) / 2 + 100)
    $cy = [int](($sr.Top + $sr.Bottom) / 2)
    [Win32]::ForceFG($script:hwnd)
    # HOVER, never click. WPF routes the wheel to whatever the pointer is
    # over, so the click bought nothing and cost correctness: the point is
    # the window's centre, and once the VR slot made the Dashboard's card
    # grid one row taller that centre landed on the ADD CONTROLLER card.
    # The click opened the type-picker popup, the popup ate the wheel
    # events, the page never scrolled, and remote-link.png / dsu-port-box
    # .png shipped as the un-scrolled Dashboard with a popup floating over
    # it (2026-08-19). ESC first so a popup from an earlier step cannot
    # swallow this scroll either.
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 200
    [Win32]::MoveTo($cx, $cy)
    Start-Sleep -Milliseconds 300
    $step = if ($Clicks -lt 0) { -3 } else { 3 }
    $count = [math]::Abs([math]::Ceiling($Clicks / $step))
    for ($i = 0; $i -lt $count; $i++) {
        [Win32]::ScrollAt($cx, $cy, $step)
        Start-Sleep -Milliseconds 50
    }
    Start-Sleep -Milliseconds 600
}

# Dismiss ANY modal dialog PadForge left open (Clone Device confirm, Pair, NFC
# register, gesture recorder). These are SEPARATE top-level windows, not children
# of the main PadForge window, so the old per-block closes that scanned
# $script:uiaWin never found their Cancel/Close buttons -- the modal stayed up and
# the NEXT Find-UIA (a Descendants walk of the window) HUNG the whole run with the
# modal blocking it (run 1 froze on the KBM slot's SlotsItemsControl lookup exactly
# this way). Find each modal top-level window by process, click a Cancel/Close/No/
# OK/Done button in its OWN small subtree, else WindowPattern.Close(). Never clicks
# a primary/destructive button (Clone / Pair / Yes are excluded from the set).
function Close-AnyModal {
    $closed = $false
    $winCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    for ($pass = 0; $pass -lt 5; $pass++) {
        $modals = @()
        foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll($TC, $winCond)) {
            try {
                if ($w.Current.ProcessId -ne $script:proc.Id) { continue }
                if ([IntPtr]$w.Current.NativeWindowHandle -eq [IntPtr]$script:hwnd) { continue }  # skip main window
                $modals += $w
            } catch {}
        }
        if ((Get-Count $modals) -eq 0) { break }
        foreach ($m in $modals) {
            Write-Host "  Close-AnyModal: dismissing '$($m.Current.Name)'" -ForegroundColor DarkGray
            $done = $false
            try {
                foreach ($b in $m.FindAll($TD, $btnCond)) {
                    if ($b.Current.Name -match '^(Cancel|Close|No|OK|Done|Dismiss)$') {
                        try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                        catch { Click-El $b -Label "modal '$($b.Current.Name)'" -Delay 400 | Out-Null }
                        $done = $true; break
                    }
                }
            } catch {}
            if (-not $done) { try { $m.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); $done = $true } catch {} }
            if ($done) { $closed = $true }
        }
        Start-Sleep -Milliseconds 700
    }
    return $closed
}

# ── Robust close for wpf-ui FluentWindow modals ──
# The Pair (PairDeviceDialog), NFC-register (RegisterNfcTagDialog) and touchpad
# gesture-recorder (TouchpadGestureRecorderDialog) dialogs are wpf-ui FluentWindows
# shown via ShowDialog (ExtendsContentIntoTitleBar + Mica). Those modals are NOT
# surfaced by RootElement's top-level Window enumeration, so Close-AnyModal (and any
# Name-matched window search) never finds them. On 2026-07-12 the Pair dialog stayed
# stuck, corrupted devices-nfc / nfc-live-preview, skipped nfc-register, and broke
# ds3-pair. A modal ShowDialog grabs foreground, so GetForegroundWindow returns its
# hwnd at open time. Grab it there, drive it by rect-relative coordinate, close with
# WM_CLOSE. If the "dialog" is actually hosted in the main window (foreground stays on
# the main hwnd), this returns Zero and callers fall back to Close-AnyModal.
function Get-ForegroundDialogHwnd {
    param([int]$Retries = 10, [int]$DelayMs = 250)
    for ($i = 0; $i -lt $Retries; $i++) {
        $fg = [Win32]::GetForegroundWindow()
        if ($fg -ne [IntPtr]::Zero -and [IntPtr]$fg -ne [IntPtr]$script:hwnd) {
            $dpid = [uint32]0
            [Win32]::GetWindowThreadProcessId($fg, [ref]$dpid) | Out-Null
            if ($dpid -eq [uint32]$script:proc.Id) { return $fg }
        }
        Start-Sleep -Milliseconds $DelayMs
    }
    return [IntPtr]::Zero
}
function Close-DialogHwnd {
    param($Hwnd)
    if (-not $Hwnd -or [IntPtr]$Hwnd -eq [IntPtr]::Zero) { return }
    [Win32]::PostMessage([IntPtr]$Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
    Start-Sleep -Milliseconds 800
    # VERIFY, then escalate. A modal that survives WM_CLOSE keeps the main
    # window disabled, and every later Cap then photographs that frozen
    # dialog instead of the page it names. On 2026-08-19 the Voice Macros
    # dialog did exactly that and shipped as midi-input, dsu-port-box,
    # remote-link, devices-nfc, nfc-live-preview and
    # settings-community-configs. Posting the message is not closing it.
    for ($esc = 0; $esc -lt 3; $esc++) {
        if (-not [Win32]::IsWindowVisible([IntPtr]$Hwnd)) { return }
        Write-Host "  Close-DialogHwnd: still up after WM_CLOSE, escalating" -ForegroundColor Yellow
        [Win32]::ForceFG([IntPtr]$Hwnd); Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 600
        if (-not [Win32]::IsWindowVisible([IntPtr]$Hwnd)) { return }
        # Its own Close button, by UIA on the dialog's hwnd (these modals
        # are reachable that way even though the ROOT scan never lists them).
        try {
            $dlgEl = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
            $btnC = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            foreach ($b in $dlgEl.FindAll($TD, $btnC)) {
                if ($b.Current.Name -match '^(Close|Cancel|OK|Done|Dismiss)$') {
                    Click-El $b -Label "dialog '$($b.Current.Name)'" -Delay 600 | Out-Null
                    break
                }
            }
        } catch {}
        [Win32]::PostMessage([IntPtr]$Hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 700
    }
    if ([Win32]::IsWindowVisible([IntPtr]$Hwnd)) {
        Write-Host "  !! DIALOG WOULD NOT CLOSE -- later shots will be corrupt" -ForegroundColor Red
        $script:modalLeaks++
    }
}

# Find a modal FluentWindow by Win32 EnumWindows: a visible PadForge-PID
# top-level window that is not the main HWND and is dialog-sized. The pair /
# NFC / workshop modals never surface in RootElement's child Window scan, so
# this is the authoritative discovery (proven in tools/diag-sweep.ps1).
# Complements Get-ForegroundDialogHwnd, which needs the modal to hold
# foreground at call time.
function Find-DialogHwndByEnum {
    param([int]$MinW = 400, [int]$MinH = 300, [int]$Retries = 10, [int]$DelayMs = 800)
    for ($i = 0; $i -lt $Retries; $i++) {
        foreach ($h in [Win32]::WindowsForPid([uint32]$script:proc.Id)) {
            if ([IntPtr]$h -eq [IntPtr]$script:hwnd) { continue }
            if (-not [Win32]::IsWindowVisible($h)) { continue }
            $r = New-Object Win32+RECT
            [Win32]::GetWindowRect($h, [ref]$r) | Out-Null
            if (($r.Right - $r.Left) -ge $MinW -and ($r.Bottom - $r.Top) -ge $MinH) { return $h }
        }
        Start-Sleep -Milliseconds $DelayMs
    }
    return [IntPtr]::Zero
}

# ==============================================================================
# STEP -1: Suppress Windows toast notifications for the run (a toast once
# landed on top of mid-run shots and the whole set had to be redone). Values
# restored in STEP 4. Same user hive as this elevated session.
# ==============================================================================
$toastKeys = @(
    @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications"; Name = "ToastEnabled" },
    @{ Path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings"; Name = "NOC_GLOBAL_SETTING_TOASTS_ENABLED" }
)
$toastPrior = @{}
foreach ($tk in $toastKeys) {
    try {
        if (-not (Test-Path $tk.Path)) { New-Item -Path $tk.Path -Force | Out-Null }
        $cur = (Get-ItemProperty -Path $tk.Path -Name $tk.Name -EA SilentlyContinue).($tk.Name)
        $toastPrior["$($tk.Path)|$($tk.Name)"] = $cur   # $null = value absent before
        Set-ItemProperty -Path $tk.Path -Name $tk.Name -Value 0 -Type DWord
    } catch { Write-Host "  !! toast suppress failed for $($tk.Name): $_" -ForegroundColor Yellow }
}
Write-Host "  Toast notifications suppressed for the run"

# ==============================================================================
# STEP 0: Inject test data into PadForge.xml
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 0: Inject test data ===" -ForegroundColor Cyan
# Accept the obvious spellings. Invoked through Start-Process -ArgumentList
# (which is how every elevated run reaches this script) a comma-joined list
# arrives as ONE token, and -File binding does not split it, so every name
# silently failed to match and the run captured nothing while reporting
# success. Split here and the operator can write it either way.
$Only = @($Only | ForEach-Object { $_ -split ',' } |
          ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Only.Count -gt 0) {
    Write-Host ("  -Only parsed to {0} name(s): {1}" -f $Only.Count, ($Only -join " | ")) -ForegroundColor Cyan
}

# Tail mode reuses a capture-configured settings file when an earlier run
# left one behind. When it did NOT, tail mode used to abort and tell the
# operator to run the whole harness, which is how refreshing six stale
# images turned into re-photographing all 116. Setting the devices up is
# STEP 0's job and STEP 0 is cheap, so run it and then jump to the tail.
$script:doSetup = $true
if ($SkipToTail) {
    if (Test-Path "$PadForgeXml.bak") {
        Write-Host "  Tail mode: reusing the staged xml from an earlier run; killing for a fresh process"
        $script:doSetup = $false
        Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 3
        Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2
    } else {
        Write-Host "  Tail mode: nothing staged, so STEP 0 runs first and the per-page passes are skipped" -ForegroundColor Cyan
    }
}
if ($script:doSetup) {
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
if (Test-Path $xmlBak) {
    # A leftover backup means a previous run was interrupted before its
    # restore. That backup holds the USER'S REAL SETTINGS and the current
    # xml is capture residue. Overwriting it here is how an original
    # settings file got destroyed on 2026-07-12 (recovered from a volume
    # shadow copy). Restore the leftover backup first, then re-back it up.
    Write-Host "  !! Leftover backup from an interrupted run; restoring it before re-backup" -ForegroundColor Yellow
    Copy-Item $xmlBak $PadForgeXml -Force
}
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

# --- Inject synthetic dummy devices for the Wheel + MIDI-input captures ---
# The Wheel tab needs a force-feedback wheel and the MIDI-input Devices-page
# preview needs a MIDI controller; neither is among the cached placeholders.
# Gating is offline-safe: the Wheel tab is IsLogitechWheel(VendorId, ProdId)
# (G29 = 0x046D / 0xC24F, CapType Driving=22 also lights Force Feedback), and
# the MIDI note/CC preview is CapType == Midi(27). Clone an existing <Device>
# so the XmlSerializer field order matches exactly, then override the identity
# fields and empty the DeviceObjects (the captures need the tab/preview, not
# the mapping picker).
try {
    $devicesNode = $ns.SelectSingleNode("Devices")
    $tmplDev = $devicesNode.SelectSingleNode("Device")
    if ($tmplDev) {
        # Idempotence: a device with this GUID may already exist when the
        # imported Devices node came from an interrupted capture run's xml.
        # Duplicate InstanceGuids degrade the Devices list at runtime
        # (2026-07-12: every post-import lookup failed on such a run).
        function Test-DeviceExists($guid) {
            return ($null -ne $devicesNode.SelectSingleNode("Device[InstanceGuid='$guid']"))
        }
        # A synthetic row stands in for hardware that is NOT already known. The
        # owner's cached <Devices> list grows as they acquire real pads, and a
        # GUID-only check cannot see that: a real PlayStation Move and a real
        # DualSense both arrived during 4.3.x, so the injection added a second
        # row for each and devices-move shipped photographing the SAME
        # controller listed twice. Identity is VID+PID, so dedupe on that too.
        # Snapshot the identities present BEFORE any injection. Testing against
        # the live node instead would compare synthetics to each other, and two
        # of them deliberately share an identity: the Wii Remote and the Wii
        # Balance Board are both 057E:0306, distinguished only by ProductName
        # because the picker's gates compute from VendorId + ProductName. The
        # first version of this check dropped whichever of the pair came second
        # and silently removed both Wii rows from the Devices list.
        $preExistingIds = New-Object System.Collections.Generic.HashSet[string]
        foreach ($d in $devicesNode.SelectNodes("Device")) {
            $v = $d.SelectSingleNode("VendorId"); $p = $d.SelectSingleNode("ProdId")
            $nm = $d.SelectSingleNode("ProductName")
            if ($null -ne $v -and $null -ne $p -and $null -ne $nm) {
                [void]$preExistingIds.Add("$($v.InnerText):$($p.InnerText):$($nm.InnerText)")
            }
        }
        # NAME is part of the key, not decoration. VID:PID alone is too blunt in
        # both directions. The owner owns a real G29, MIDI keyboard, NFC reader
        # and Joy-Cons, so an identity-only check skipped those synthetics and
        # took pad-wheel, midi-input, nfc-register, joycon-ir-source and the
        # source-picker shots down with them: the real rows carry Windows'
        # names, and the synthetics' EXACT names are what trip the offline
        # identity gates (IsBalanceBoard, HasJoyConIr, HasJoyCon2Mouse compute
        # from VendorId + ProductName). Matching on the name too skips only a
        # synthetic that would render as a visually identical second row, which
        # is the duplicate this check exists to stop.
        # NOT $pid: that is a PowerShell automatic read-only variable (the
        # current process id), and binding it as a parameter throws
        # "Cannot overwrite variable pid because it is read-only or constant"
        # INSIDE the injection try-block, which swallowed the whole synthetic
        # device set and took eighteen shots stale with it in one run.
        function Test-DeviceIdentityExists($vid, $prodId, $name) {
            return $preExistingIds.Contains("${vid}:${prodId}:${name}")
        }
        function Add-DeviceOnce($node) {
            $g = $node.SelectSingleNode("InstanceGuid").InnerText
            if (Test-DeviceExists $g) { Write-Host "  (dummy $g already present, skipped)"; return }
            $vn = $node.SelectSingleNode("VendorId"); $pn = $node.SelectSingleNode("ProdId")
            $nn = $node.SelectSingleNode("ProductName")
            if ($null -ne $vn -and $null -ne $pn -and $null -ne $nn -and
                (Test-DeviceIdentityExists $vn.InnerText $pn.InnerText $nn.InnerText)) {
                Write-Host ("  (real '{0}' already enumerated, synthetic skipped)" -f $nn.InnerText)
                return
            }
            $devicesNode.AppendChild($node) | Out-Null
        }
        function New-SyntheticDevice($guid, $name, $vid, $prodId, $path, $capType, $axes, $buttons, $povs) {
            $d = $tmplDev.CloneNode($true)
            $set = { param($tag, $val) $n = $d.SelectSingleNode($tag); if ($n) { $n.InnerText = "$val" } }
            & $set "InstanceGuid" $guid; & $set "InstanceName" $name
            & $set "ProductGuid" $guid;  & $set "ProductName" $name
            & $set "VendorId" $vid;      & $set "ProdId" $prodId
            & $set "DevicePath" $path
            & $set "CapAxeCount" $axes;  & $set "CapButtonCount" $buttons
            & $set "RawButtonCount" $buttons; & $set "CapPovCount" $povs
            & $set "CapType" $capType
            & $set "HasGyro" "false"; & $set "HasAccel" "false"; & $set "HasTouchpad" "false"
            & $set "HasRumbleTriggers" "false"; & $set "IsEnabled" "true"; & $set "IsHidden" "false"
            $doNode = $d.SelectSingleNode("DeviceObjects"); if ($doNode) { $doNode.RemoveAll() }
            return $d
        }
        Add-DeviceOnce (New-SyntheticDevice "aaaa1111-2222-3333-4444-555566667777" "Logitech G29 Driving Force Racing Wheel" 1133 49743 "HID\VID_046D&PID_C24F\dummy" 22 4 24 1)
        Add-DeviceOnce (New-SyntheticDevice "bbbb1111-2222-3333-4444-555566667777" "MIDI Keyboard" 4661 22 "HID\VID_1235&PID_0016\dummy" 27 4 24 1)
        # Wii-family devices for the mapping-source-picker captures (issues #146/#151/#154).
        # The picker offers "Balance Total Weight/Lean X/Lean Y" (IsBalanceBoard),
        # "IR Brightness" (HasJoyConIr), and "Mouse Motion X/Y" (HasJoyCon2Mouse) when the
        # SELECTED device's identity gate fires. All three gates are [XmlIgnore]-computed
        # from VendorId (0x057E = 1406) + ProductName (UserDevice.cs); the exact names below
        # trip them offline. These KEEP their DeviceObjects (cloned from a gamepad template):
        # auto-map builds the mapping-grid rows from a device's DeviceObjects, so an
        # emptied-objects device gives an EMPTY grid with no Source combo to open.
        $gpTemplate = $null
        foreach ($d in $devicesNode.SelectNodes("Device")) {
            $pn = $d.SelectSingleNode("ProductName"); $ct = $d.SelectSingleNode("CapType")
            if ($pn -and $pn.InnerText -like "*DualSense*") { $gpTemplate = $d; break }
            if (-not $gpTemplate -and $ct -and $ct.InnerText -eq "21") { $gpTemplate = $d }
        }
        function New-WiiDevice($guid, $name, $vid, $prodId, $path) {
            $src = if ($gpTemplate) { $gpTemplate } else { $tmplDev }
            $d = $src.CloneNode($true)
            $set = { param($tag, $val) $n = $d.SelectSingleNode($tag); if ($n) { $n.InnerText = "$val" } }
            & $set "InstanceGuid" $guid; & $set "InstanceName" $name
            & $set "ProductGuid" $guid;  & $set "ProductName" $name
            & $set "VendorId" $vid;      & $set "ProdId" $prodId
            & $set "DevicePath" $path;   & $set "CapType" 21
            & $set "HasGyro" "false"; & $set "HasAccel" "false"; & $set "HasTouchpad" "false"
            & $set "HasRumbleTriggers" "false"; & $set "IsEnabled" "true"; & $set "IsHidden" "false"
            return $d
        }
        Add-DeviceOnce (New-WiiDevice "cccc1111-2222-3333-4444-555566667777" "Nintendo Wii Balance Board" 1406 774 "HID\VID_057E&PID_0306\dummy")
        Add-DeviceOnce (New-WiiDevice "dddd1111-2222-3333-4444-555566667777" "Nintendo Switch Joy-Con (R)" 1406 8199 "HID\VID_057E&PID_2007\dummy")
        Add-DeviceOnce (New-WiiDevice "eeee1111-2222-3333-4444-555566667777" "Nintendo Switch 2 Joy-Con (L)" 1406 8198 "HID\VID_057E&PID_2066\dummy")

        # v4 additions. DualShock 3 (#194/#195 motion + the BT/USB support):
        # a Bluetooth-looking path so the Devices-page dossier shows the BT
        # link line, gyro+accel true so the Gyro tab gates on (SDL sixaxis
        # motion). Keeps gamepad DeviceObjects so the mapping grid has rows.
        $ds3 = New-WiiDevice "ffff1111-2222-3333-4444-555566667777" "PLAYSTATION(R)3 Controller" 1356 616 "BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0268\dummy"
        $n = $ds3.SelectSingleNode("HasGyro"); if ($n) { $n.InnerText = "true" }
        $n = $ds3.SelectSingleNode("HasAccel"); if ($n) { $n.InnerText = "true" }
        Add-DeviceOnce $ds3
        # Steam Controller 2015 (#202 haptic high-tone + #209 home-LED steam
        # lane): VID 0x28DE PID 0x1102 trips IsSteamController2015 and the
        # Guide LED card's steam path.
        Add-DeviceOnce (New-WiiDevice "abab1111-2222-3333-4444-555566667777" "Steam Controller" 10462 4354 "HID\VID_28DE&PID_1102\dummy")
        # Xbox Series X: deterministic stand-in (the cached list may only
        # carry an Xbox One pad). The XInput# path gates the Guide LED
        # card, the Series PID (0x0B12) passes the impulse-trigger set,
        # and HasRumbleTriggers surfaces the Impulse Triggers tab.
        # UNIQUE ProductName ("...GIP...") on purpose: the user's REAL cached
        # Xbox Series X is also named "Xbox Series X Controller", and worse, the
        # Xbox slot's PRESET is "Xbox Series X|S Controller (Bluetooth)". A bare
        # "Xbox Series X" name-part therefore collides with the preset combo item,
        # so Select-MappedDevice picked the PRESET dropdown instead of the device
        # and the Impulse-Triggers / Guide-LED tabs never followed. Matching the
        # GIP suffix (below) selects THIS device unambiguously; the preset has no
        # "GIP" in it. The Devices page trims the "Controller" suffix, so the card
        # reads "Xbox Series X GIP" and the dropdown reads the full name. Both
        # contain "Xbox Series X GIP".
        $xsx = New-WiiDevice "acac1111-2222-3333-4444-555566667777" "Xbox Series X GIP Controller" 1118 2834 "XInput#0\dummy"
        $n = $xsx.SelectSingleNode("HasRumbleTriggers"); if ($n) { $n.InnerText = "true" }
        Add-DeviceOnce $xsx
        # Wii Remote (#146 Pointer tab + #196 Clone Device on the Extended slot).
        # HasIrCamera is identity-derived: VendorId 0x057E (1406) AND ProductName
        # starting "Nintendo Wii Remote" (UserDevice.cs:142). The user's cache no
        # longer carries a paired Wii Remote, so the Pointer tab + Extended-slot
        # clone had no device to gate on. New-WiiDevice keeps gamepad DeviceObjects
        # so auto-map builds a grid and the Clone-Device button has rows to clone.
        Add-DeviceOnce (New-WiiDevice "11110000-2222-3333-4444-555566667777" "Nintendo Wii Remote" 1406 774 "HID\VID_057E&PID_0306\wiimotedummy")
        # NFC reader (#150 live tag preview + register modal). CapType 28 (Nfc,
        # InputTypes.cs:85) drives IsNfcDevice -> the Devices-page NFC section and
        # the "Register / Manage NFC Tags" button (ShowRegisterNfcTag). The user's
        # cache has no NFC reader anymore, so the whole NFC block was skipped.
        Add-DeviceOnce (New-SyntheticDevice "22220000-2222-3333-4444-555566667777" "NFC Reader" 1839 8704 "HID\VID_072F&PID_2200\dummy" 28 0 0 0)

        # ── THE RULE: a synthetic device is all a capture EVER needs ──
        #
        # Owner ruling 2026-08-19, and it applies to ANY device, not just
        # these: the harness must never depend on real hardware being
        # plugged in, powered on, or charged. Every gated surface is gated
        # on IDENTITY (vendor/product id and capability flags), all of
        # which a dummy row carries perfectly well.
        #
        # This block exists because the DualSense-gated tabs (Adaptive
        # Triggers, Lighting, Gyro, Touchpad, Audio, plus devices-power)
        # were reached by name-matching the owner's REAL pad. When that pad
        # was flat, six shots silently went stale and a whole capture run
        # was spent discovering it. There was never a reason for it: the
        # gates read VendorId 0x054C with ProdId 0x0CE6 / 0x0DF2
        # (UserDevice.HasVoicePhrases and its siblings), which is three
        # numbers in an XML row.
        #
        # DualSense: gyro + accel + touchpad true so every conditional tab
        # on the Pad page realizes.
        $ds5 = New-WiiDevice "bbbb2222-3333-4444-5555-666677778888" "DualSense Wireless Controller" 1356 3302 "HID\VID_054C&PID_0CE6\dummy"
        foreach ($f in @("HasGyro", "HasAccel", "HasTouchpad")) {
            $n = $ds5.SelectSingleNode($f); if ($n) { $n.InnerText = "true" }
        }
        Add-DeviceOnce $ds5
        # PlayStation Move (#277): VID 0x054C PID 0x03D5, motion-only wand.
        $move = New-WiiDevice "bbbb3333-3333-4444-5555-666677778888" "PlayStation Move Motion Controller" 1356 981 "HID\VID_054C&PID_03D5\dummy"
        foreach ($f in @("HasGyro", "HasAccel")) {
            $n = $move.SelectSingleNode($f); if ($n) { $n.InnerText = "true" }
        }
        Add-DeviceOnce $move
        # Microphone row for the voice-macro block (#317). CapType 31 =
        # Microphone (InputTypes.cs:98). The old comment here claimed live
        # Windows audio endpoints meant "there is no dummy to inject";
        # the type is a number in the same row as every other one.
        Add-DeviceOnce (New-SyntheticDevice "bbbb4444-3333-4444-5555-666677778888" "Microphone Array (Synthetic)" 19785 17232 "SWD\MMDEVAPI\dummy-mic" 31 0 1 0)

        Write-Host "  Injected synthetic G29 wheel + MIDI Keyboard + 3 Wii-family + DS3 + Steam Controller + Xbox GIP + Wii Remote + NFC + DualSense + PlayStation Move + Microphone" -ForegroundColor Green
    }
} catch {
    Write-Host "  !! Failed to inject synthetic devices: $_" -ForegroundColor Yellow
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
# These are written in MacroData's declared element order. That is worth keeping
# because XmlSerializer reads elements in declared order, but be clear about
# what it does NOT explain: on 2026-08-09 all five macros were discarded at load
# and every macro shot came out blank, and reordering them did not fix it. The
# 2026-07-30 pad-macros.png shows the same five loaded correctly from the
# ORIGINAL out-of-order XML, complete with the "Left Trigger > 50%" chip, so
# order was never the cause. What changed between those two dates has not been
# identified. Do not treat this ordering as the fix.
# MacroData order:  PadIndex Name IsEnabled TriggerButtons TriggerDeviceGuid
#   TriggerRawButtons TriggerSource TriggerMode TriggerHoldMs
#   TriggerDoublePressMs LayerMask ConsumeTriggerButtons RepeatMode RepeatCount
#   RepeatDelayMs PairId ReleaseLingerMs TriggerCustomButtons TriggerAxisTargets
#   TriggerAxisDirections TriggerAxisThreshold TriggerPovs TriggerInputs
#   TriggerExpression TriggerExpressionVariables Actions
# ActionData order: Type(0) ButtonFlags(1) KeyCode(3) KeyString(4) DurationMs(5)
#   AxisTarget(7) VolumeLimit(12) MouseSensitivity(13) MouseButton(14)
#   MouseX(51) MouseY(52) IntervalMs(53) DisconnectTarget(57)
# Adding a field means inserting it at its declared position, not appending.
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
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <TriggerAxisTargets>LeftTrigger</TriggerAxisTargets>
  <TriggerAxisThreshold>50</TriggerAxisThreshold>
  <Actions>
    <Action><Type>ButtonPress</Type><ButtonFlags>4096</ButtonFlags><DurationMs>100</DurationMs></Action>
    <Action><Type>Delay</Type><DurationMs>200</DurationMs></Action>
    <Action><Type>KeyPress</Type><KeyCode>32</KeyCode><DurationMs>50</DurationMs></Action>
    <Action><Type>MouseButtonPress</Type><DurationMs>50</DurationMs><MouseButton>Left</MouseButton></Action>
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
    # Macro 3: "Sleep Controller". Chord trigger, single DisconnectController action.
    # Surfaces the #162 Disconnect editor (Target dropdown) for the Macros screenshot.
    $m3Xml = @'
<Macro PadIndex="0">
  <Name>Sleep Controller</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>48</TriggerButtons>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>DisconnectController</Type><DisconnectTarget>TriggeringDevice</DisconnectTarget></Action>
  </Actions>
</Macro>
'@
    # Macro 4: "Center Cursor" (#9). Single MoveMouseToScreenPosition action so
    # the new editor (Mouse X / Mouse Y + "Pick on screen") renders for the
    # macro-move-mouse capture. Trigger = both stick buttons (2 chips, keeping
    # the trigger block the same height as Sleep Controller so the action-row
    # coordinates below match across all three macro captures).
    $m4Xml = @'
<Macro PadIndex="0">
  <Name>Center Cursor</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>192</TriggerButtons>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>MoveMouseToScreenPosition</Type><MouseX>960</MouseX><MouseY>540</MouseY></Action>
  </Actions>
</Macro>
'@
    # Macro 5: "Rapid Fire" (#9). Single RepeatKeyWhileHeld action so the
    # interval editor + key-combo panel render for the macro-repeat-key capture.
    # Trigger = both shoulders (2 chips, same layout parity).
    $m5Xml = @'
<Macro PadIndex="0">
  <Name>Rapid Fire</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>768</TriggerButtons>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>RepeatKeyWhileHeld</Type><KeyCode>32</KeyCode><KeyString>{Space}</KeyString><IntervalMs>75</IntervalMs></Action>
  </Actions>
</Macro>
'@
    $frag1 = $xml.CreateDocumentFragment(); $frag1.InnerXml = $m1Xml.Trim()
    $macrosNode.AppendChild($frag1) | Out-Null
    $frag2 = $xml.CreateDocumentFragment(); $frag2.InnerXml = $m2Xml.Trim()
    $macrosNode.AppendChild($frag2) | Out-Null
    $frag3 = $xml.CreateDocumentFragment(); $frag3.InnerXml = $m3Xml.Trim()
    $macrosNode.AppendChild($frag3) | Out-Null
    $frag4 = $xml.CreateDocumentFragment(); $frag4.InnerXml = $m4Xml.Trim()
    $macrosNode.AppendChild($frag4) | Out-Null
    $frag5 = $xml.CreateDocumentFragment(); $frag5.InnerXml = $m5Xml.Trim()
    $macrosNode.AppendChild($frag5) | Out-Null
    Write-Host "  Injected 5 test macros"
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

    # Suppress the first-run welcome tour. Since v4 the completed flag
    # lives in PadForge.xml (not a marker file), so a regenerated xml
    # re-triggers the tour and its full-window overlay swallows every
    # click the capture makes (0 slots created, identical overlay shots).
    $frNode = $appSettings.SelectSingleNode("FirstRunTourCompleted")
    if ($frNode) { $frNode.InnerText = "true" }
    else {
        $frNode = $xml.CreateElement("FirstRunTourCompleted")
        $frNode.InnerText = "true"
        $appSettings.AppendChild($frNode) | Out-Null
    }
    Write-Host "  Set FirstRunTourCompleted=true for capture"

    # Enable web controller server for web screenshots
    $wcNode = $appSettings.SelectSingleNode("EnableWebController")
    if ($wcNode) { $wcNode.InnerText = "true" }
    else {
        $wcNode = $xml.CreateElement("EnableWebController")
        $wcNode.InnerText = "true"
        $appSettings.AppendChild($wcNode) | Out-Null
    }
    Write-Host "  Set EnableWebController=true for capture"

    # Workshop gate (#9): seed the community-config opt-in OFF so the browse
    # dialog opens on its cold-forge state for the workshop-cold capture. This
    # is a TEMPORARY capture-xml toggle only: the owner's real setting lives in
    # the backup and is restored untouched in STEP 4. The dialog's own Enable
    # button flips this capture-xml copy for the search/manifest shots.
    $ccNode = $appSettings.SelectSingleNode("EnableCommunityConfigLookup")
    if ($ccNode) { $ccNode.InnerText = "false" }
    else {
        $ccNode = $xml.CreateElement("EnableCommunityConfigLookup")
        $ccNode.InnerText = "false"
        $appSettings.AppendChild($ccNode) | Out-Null
    }
    Write-Host "  Set EnableCommunityConfigLookup=false (workshop cold-forge shot)"

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
}

# ==============================================================================
# STEP 1: Start PadForge
# ==============================================================================
# Everything from here to STEP 4 runs inside a try/finally. Twice on 2026-08-09
# a StrictMode property read threw mid-run, STEP 4 never executed, and the
# owner's real PadForge.xml was left replaced by the capture file complete with
# injected dummy devices. A capture run may fail. It may not walk away holding
# someone's settings hostage, so the restore is now in a finally and runs on
# every exit path.
try {
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

## No TOPMOST, ever (feedback_no_topmost_in_capture): ForceFG before each
## click/Cap is the mechanism; TOPMOST pins PadForge over the user's other
## windows and a mid-script failure leaves it stuck there.
[Win32]::ForceFG($hwnd)
# The elevated console is OUR window and it must never appear in a shot.
# Cap calls ForceFG first, but ForceFG can lose to Windows' foreground-lock
# rules and Cap does not verify it won, so a losing race put the console on
# top of devices.png and devices-facet-chips.png in the 4.1.0 set (both
# shipped to the repo, the website and the docs before anyone looked).
# Hiding it outright removes the race instead of narrowing it.
# Default false so the macro gates below are readable even if the macro section
# never runs. Under Set-StrictMode -Version Latest an unset variable is a
# terminating error, and that class of mistake already cost this script two runs.
$script:MacrosPresent = $false
$script:consoleWnd = [Win32]::GetConsoleWindow()
if ($script:consoleWnd -ne [IntPtr]::Zero) {
    [Win32]::ShowWindow($script:consoleWnd, 0) | Out-Null  # SW_HIDE
    Write-Host "Console hidden for the capture run."
}

[Win32]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE
Start-Sleep -Milliseconds 700

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
    $dashR = Get-Rect $dash
    if ($null -ne $dashR -and $dashR.Width -lt 120) {
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
    $addNav = Find-UIARetry -Name "Add Controller"
    if (-not $addNav) { Write-Host "  !! Add Controller nav not found" -ForegroundColor Red; return $false }
    # Out-Null, and it is load-bearing. An uncaptured Click-El return joins
    # this function's OUTPUT, so "return $false" comes back as a two-element
    # array and `if ($ok)` reads TRUE. That is how a gated MIDI/VR button
    # logged "NO NEW SLOT appeared" and "Created MIDI" one line apart, and
    # how the abort guard written for exactly this case never fired.
    Click-El $addNav -Label "Add Controller" -Delay 600 | Out-Null
    # Capture the popup on first open (shows all 5 type buttons)
    if (-not $script:popupCaptured) {
        Cap "add-controller-popup" -AllowModal
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
    # A DISABLED button is found, clicks, and does nothing. The VR button
    # disables itself when SteamVR is absent, and on 2026-08-19 this
    # function returned $true for that click, the caller printed
    # "Created VR", and the ENTIRE screenshot set shipped with six slots
    # in the always-visible left rail instead of seven. Report it here.
    try {
        if (-not $typeBtn.Current.IsEnabled) {
            Write-Host "  !! Type button '$TypeBtnAid' is DISABLED (prerequisite missing)" -ForegroundColor Red
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
            Start-Sleep -Milliseconds 300
            return $false
        }
    } catch {}
    $before = Get-Count @(Find-AllSlots)
    Click-El $typeBtn -Label $TypeLabel -Delay 1500 | Out-Null
    # VERIFY, never assume. The click is not the creation: poll until the
    # slot list actually grows.
    for ($w = 0; $w -lt 10; $w++) {
        if ((Get-Count @(Find-AllSlots)) -gt $before) { return $true }
        Start-Sleep -Milliseconds 400
    }
    Write-Host "  !! '$TypeLabel' click landed but NO NEW SLOT appeared (was $before)" -ForegroundColor Red
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300
    return $false
}

# STAGING, not capture: tail mode needs the slots created and the
# devices assigned, or it lands on an empty Dashboard. Gating this on
# -not $SkipToTail meant a tail run injected the synthetic devices and
# then created NO virtual controllers, so every card index was out of
# range and nothing could be photographed.
if ($script:doSetup) {
# Delete all existing slots to ensure a clean start
Write-Host "  Removing any existing slots..."
for ($delPass = 0; $delPass -lt 16; $delPass++) {
    $existingSlots = @(Find-AllSlots)
    if ((Get-Count $existingSlots) -eq 0) { break }
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
        # This used to assign every button in turn and keep whichever came last,
        # on the assumption that the X is always last, then click it up to 16
        # times. Nothing checked what it was clicking. Try the same name match
        # the primary path uses, scoped to the card this time, and only fall
        # back to the last button while SAYING which one that is, so a wrong
        # click shows up in the log instead of silently happening 16 times.
        foreach ($b in $slotBtns) {
            if ($b.Current.Name -match "Delete|Remove|Close") { $delBtn = $b; break }
        }
        if (-not $delBtn -and (Get-Count $slotBtns) -gt 0) {
            $delBtn = $slotBtns[(Get-Count $slotBtns) - 1]
            Write-Host "  (fallback) clicking last card button: '$($delBtn.Current.Name)'" -ForegroundColor Yellow
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
Write-Host "  Slots remaining after cleanup: $(Get-Count $remainingSlots)"

# Create one slot of EVERY VirtualControllerType. SlotNumber and dashboard-card
# order follow VirtualControllerGroups.InOrder regardless of creation order:
# Xbox 1, PlayStation 2, Nintendo 3, Extended 4, KBM 5, MIDI 6, VR 7.
# AutomationIds AddXbox360Btn / AddDS4Btn are kept verbatim from v2 for stable
# automation hookup. 4.1.0 renamed the Extended button's id to AddRawBtn and
# added the Nintendo type (virtual Switch Pro, #246); 4.2.0 added VR (#49).
$slotTypes = @(
    @{ Aid = "AddXbox360Btn"; Label = "Xbox" },
    @{ Aid = "AddDS4Btn"; Label = "PlayStation" },
    @{ Aid = "AddNintendoBtn"; Label = "Nintendo" },
    @{ Aid = "AddKeyboardMouseBtn"; Label = "Keyboard+Mouse" },
    @{ Aid = "AddRawBtn"; Label = "Extended" },
    @{ Aid = "AddMidiBtn"; Label = "MIDI" },
    # VR (#49, 4.2.0) completes the set: every VirtualControllerType the
    # popup can create is represented here. The popup's button DISABLES
    # itself when SteamVR is absent (HMaestroVRController.IsAvailable), so
    # the capture machine MUST carry the Steam-free SteamVR runtime. This
    # is not optional and it is not a machine-specific nicety: the slot
    # rail is visible in EVERY screenshot the harness takes, so a missing
    # type makes the whole set wrong, not one image.
    @{ Aid = "AddVrBtn"; Label = "VR" }
)
$failedTypes = @()
foreach ($st in $slotTypes) {
    Write-Host "  Creating $($st.Label) slot..."
    # Take the LAST emitted value, never the whole stream: a helper that
    # leaks one uncaptured return turns this boolean into an array, and an
    # array is always truthy. Belt to the Out-Null braces inside.
    $ok = @(Add-SlotViaPopup -TypeBtnAid $st.Aid -TypeLabel $st.Label)[-1] -eq $true
    if ($ok) { Write-Host "  Created $($st.Label)" -ForegroundColor Green }
    else { $failedTypes += $st.Label }
    Start-Sleep -Milliseconds 500
}
if ($failedTypes.Count -gt 0) {
    # ABORT, loudly. Continuing produces a full set of screenshots that all
    # carry a wrong slot rail, which is worse than no screenshots: the run
    # LOOKS successful and every image ships a missing controller type.
    # 2026-08-19 shipped exactly that with VR absent, twice.
    Write-Host ""
    Write-Host "!! SLOT TYPES FAILED TO CREATE: $($failedTypes -join ', ')" -ForegroundColor Red
    Write-Host "!! The slot rail appears in EVERY screenshot, so the whole set" -ForegroundColor Red
    Write-Host "!! would be wrong. Refusing to capture." -ForegroundColor Red
    if ($failedTypes -contains "VR") {
        Write-Host "!! VR needs the Steam-free SteamVR runtime:" -ForegroundColor Yellow
        Write-Host "!!   C:\SteamVR\bin\win64\vrpathreg.exe present, and" -ForegroundColor Yellow
        Write-Host "!!   HKLM\SOFTWARE\HIDMaestro\SteamVRPath naming that dir." -ForegroundColor Yellow
        Write-Host "!!   Settings > SteamVR installs it (steamcmd, app 250820)." -ForegroundColor Yellow
    }
    # NO EXEMPTION, INCLUDING -Only. A short rail is a screenshot OMISSION,
    # and an omission is never allowed to ship. An earlier version of this
    # let a scoped run continue with a warning, on the theory that the
    # operator had named the shots and could judge the rail themselves.
    # That reasoning is wrong: the rail is in every image, so a scoped run
    # produces images that silently disagree with the rest of the set, and
    # "I checked" is not a property the next person inherits. Install the
    # missing prerequisite and run again.
    throw "Slot type creation failed: $($failedTypes -join ', ')"
}

# Wait for type-group reorder to fully settle before querying slots
Write-Host "  Waiting 3s for type-group reorder to settle..."
Start-Sleep -Milliseconds 3000

# Verify slots appeared
$slots = @(Find-AllSlots)
Write-Host "  Slots after creation: $(Get-Count $slots)"

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
}

# UIA's FindAll throws a transient "Unrecognized error" COM fault under load,
# and an unguarded call is TERMINATING: it killed a targeted run outright
# (2026-08-19) after the staging pass had already created the slots. Same
# family as the Get-Count trap above, one layer out: the call itself rather
# than what it returns. Retry, then hand back an empty result so the caller
# degrades instead of the run dying.
function Find-AllSafe {
    param($Element, $Condition, [int]$Retries = 4, [int]$DelayMs = 400)
    for ($i = 0; $i -lt $Retries; $i++) {
        try { return $Element.FindAll($TD, $Condition) }
        catch {
            if ($i -eq $Retries - 1) {
                Write-Host "  !! FindAll failed $Retries times: $($_.Exception.Message)" -ForegroundColor Yellow
                return @()
            }
            Start-Sleep -Milliseconds $DelayMs
        }
    }
    return @()
}

function Reset-DeviceTypeFilter {
    # The 4.1.0 Devices page carries type-filter chips (ALL / GAMEPAD /
    # KEYBOARD / ...) right under the header. A stray click leaves a family
    # filter active, which hides every non-matching card, and each later
    # device find becomes a full 24-retry scroll miss. The 2026-07-30 runs
    # stranded on KEYBOARD this way. Probe-verified UIA shape: each chip
    # label is a LETTER-SPACED TextBlock ('A L L') with the count in a
    # separate sibling, and no Button/ListItem wrapper exposes a pattern.
    # So match on the space-stripped text and click the label's rect.
    $txtC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $wrF = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrF) | Out-Null
    foreach ($el in (Find-AllSafe $script:uiaWin $txtC)) {
        try {
            if (($el.Current.Name -replace '\s', '') -ne 'ALL') { continue }
            $r = Get-Rect $el
            if ($null -eq $r) { continue }
            # Constrain to the chip band near the page top so a stray
            # 'ALL' elsewhere can't be clicked.
            if (($r.Y - $wrF.Top) -gt 350) { continue }
            Click-El $el -Label "ALL device-type chip" -Delay 600 | Out-Null
            return $true
        } catch {}
    }
    return $false
}

function Get-DeviceListTop {
    # The Devices page has a sticky header and a chip row above the card list.
    # A card whose rect merely clears an arbitrary 120px still overlaps that
    # band, and clicking its centre lands on the chips instead: that is how the
    # G29 assignment enumerated 0 toggles on 2026-08-09 and took pad-wheel,
    # pad-impulse-triggers, pad-lighting-guide-led and wii-balance-sources with
    # it. Measure the real boundary off the ALL chip rather than guessing, and
    # only fall back to a constant when the chip cannot be read.
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $txtC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($el in (Find-AllSafe $script:uiaWin $txtC)) {
        try {
            if (($el.Current.Name -replace '\s', '') -ne 'ALL') { continue }
            $r = Get-Rect $el
            if ($null -eq $r) { continue }
            if (($r.Y - $wr.Top) -gt 350) { continue }
            return [int]($r.Y + $r.Height + 18)
        } catch { }
    }
    return [int]($wr.Top + 230)
}

function Assign-DeviceToSlot {
    param([string]$DeviceNamePart, [string]$SlotNumberLabel, [switch]$Unassign, [switch]$Reassert)
    $searchIn = $script:uiaWin
    Reset-DeviceTypeFilter | Out-Null
    $liCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    # Search the initially-realized rows first, then scroll DOWN on a miss to
    # reach lower rows in the virtualized card list. No scroll-to-top: that
    # de-realizes the top rows and broke nearby finds (e.g. DualSense).
    $wrA = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrA) | Out-Null
    $lx = [int]($wrA.Left + 400); $my = [int](($wrA.Top + $wrA.Bottom) / 2)
    # Only accept a card whose rect is actually INSIDE the viewport. A
    # virtualized row can report a rect above/below the visible list (the
    # 2026-07-12 run clicked a DualSense card at Y=-749, the click landed
    # nowhere, and the unassign silently no-oped). On an off-screen match,
    # scroll TOWARD it and re-find instead of clicking a phantom rect.
    $target = $null
    $listTop = Get-DeviceListTop
    for ($stry = 0; $stry -lt 24 -and (-not $target); $stry++) {
        $found = $null
        $items = $searchIn.FindAll($TD, $liCond)
        # Wheel at the card list's OWN center-x, read from any realized row.
        # The 4.1.0 Devices page layout moved the list, and the old fixed
        # Left+400 landed outside its scroll viewer, so every below-the-fold
        # device (Xbox GIP, All Mice, Wii Remote) went unreachable.
        foreach ($it in $items) {
            $ir = Get-Rect $it
            if ($null -ne $ir) { $lx = [int]($ir.X + $ir.Width / 2); break }
        }
        foreach ($it in $items) {
            if ($it.Current.Name -like "*$DeviceNamePart*") { $found = $it; break }
        }
        if ($found) {
            $fr = Get-Rect $found
            if ($null -ne $fr -and $fr.Y -ge $listTop -and ($fr.Y + $fr.Height) -le ($wrA.Bottom - 40)) {
                $target = $found
            } else {
                # A null rect means the row is virtualized out of view, so scroll
                # toward it exactly as if it sat above the viewport.
                $dir = if ($null -eq $fr -or $fr.Y -lt $listTop) { 3 } else { -3 }  # positive scrolls up
                [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($lx, $my, $dir); Start-Sleep -Milliseconds 350
            }
        } else {
            [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($lx, $my, -3); Start-Sleep -Milliseconds 350
        }
    }
    if (-not $target) {
        Write-Host "  !! Device matching '$DeviceNamePart' not found on-screen after scroll" -ForegroundColor Yellow
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
    # The detail pane can realize its toggles a beat after the card click
    # (the 2026-07-30 run enumerated 0 toggles on a found Wii Remote card
    # and the assignment silently failed). One re-enumerate after a wait.
    $toggles = @()
    for ($tenum = 0; $tenum -lt 3 -and (Get-Count $toggles) -eq 0; $tenum++) {
        if ($tenum -gt 0) { Start-Sleep -Milliseconds 1500 }
        # Second miss means the click probably did not SELECT the card at all
        # (it landed on the header, or the row de-realized under the pointer),
        # so waiting longer cannot help. Re-click the card once before the
        # final enumerate.
        if ($tenum -eq 2) {
            Write-Host "    0 toggles twice; re-clicking the device card"
            Click-El $target -Label "Device card '$DeviceNamePart' (retry)" -Delay 1200 | Out-Null
        }
        $allButtons = $searchIn.FindAll($TD, $btnCond)
        foreach ($b in $allButtons) {
            try {
                $null = $b.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                $r = Get-Rect $b
                if ($null -eq $r) { continue }
                $cx = $r.X + $r.Width / 2
                if ($cx -gt $midX) { $toggles += $b }   # detail panel only, exclude sidebar
            } catch {}
        }
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
    Write-Host "    Detail-panel assignment toggles: $(Get-Count $toggles)"
    foreach ($t in $toggles) { Write-Host "      toggle slotNumber='$($slotOf[$t])'" }
    $btn = $null
    foreach ($t in $toggles) {
        if ($slotOf[$t] -eq $SlotNumberLabel) { $btn = $t; break }
    }
    if (-not $btn) {
        # Fallback: positional. ActiveSlotItems is in slot order, so the Nth
        # detail-panel toggle (0-based) is SlotNumber N+1.
        $idx = [int]$SlotNumberLabel - 1
        if ($idx -ge 0 -and $idx -lt (Get-Count $toggles)) {
            $btn = $toggles[$idx]
            Write-Host "    (slot-number match missed -- using positional toggle #$idx)" -ForegroundColor DarkGray
        }
    }
    if ($btn) {
        # Reading ToggleState is fine; it's the Toggle() ACTION that's unreliable.
        # Assign: skip if already ON. Unassign: skip if already OFF. Otherwise the
        # single Click below flips it (assign turns on, unassign turns off).
        $isOn = $false
        try { $isOn = ($btn.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) } catch {}
        if ($Unassign -and -not $isOn) {
            Write-Host "  Slot $SlotNumberLabel already unassigned from $DeviceNamePart"
            return $true
        }
        if (-not $Unassign -and $isOn -and -not $Reassert) {
            Write-Host "  Slot $SlotNumberLabel already assigned to $DeviceNamePart"
            return $true
        }
        # A toggle reading ON only proves the SETTINGS say assigned. For an
        # injected dummy device it does NOT mean the device is live, because
        # what makes it live is ToggleSlotCommand, and that runs on Click. The
        # Xbox GIP dummy came pre-assigned, the shortcut above skipped the
        # click, the device never appeared in the pad page's device dropdown,
        # and pad-impulse-triggers plus pad-lighting-guide-led stayed stale
        # while the log cheerfully reported "already assigned". Re-assert by
        # clicking twice: off, then on. It ends in the same state, having
        # actually run the command.
        $reassertOff = ($Reassert -and $isOn -and -not $Unassign)
        # Bring the toggle into view first (the assign row can sit below the
        # fold on a tall detail panel), then use a real coordinate CLICK, not
        # TogglePattern.Toggle(). The toggle's IsChecked is OneWay-bound to
        # IsAssigned and the actual assignment is done by ToggleSlotCommand,
        # which fires on Click. UIA Toggle() only flips IsChecked (immediately
        # overwritten by the OneWay binding) and never runs the command, so the
        # assignment silently no-ops -- which is exactly why every slot read
        # "No device mapped" on the dashboard.
        try { $btn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 300 } catch {}
        $verb = if ($Unassign) { "Unassigned" } else { "Assigned" }
        if ($reassertOff) {
            Click-El $btn -Label "Slot $SlotNumberLabel toggle OFF (re-assert $DeviceNamePart)" -Delay 900 | Out-Null
        }
        Click-El $btn -Label "Slot $SlotNumberLabel toggle ($DeviceNamePart)" -Delay 900 | Out-Null
        Write-Host "  $verb $DeviceNamePart $(if ($Unassign) { 'from' } else { 'to' }) slot $SlotNumberLabel" -ForegroundColor Green
        return $true
    }
    Write-Host "  !! Slot $SlotNumberLabel toggle not found for $DeviceNamePart (had $(Get-Count $toggles) toggles)" -ForegroundColor Yellow
    return $false
}

# STAGING, not capture: tail mode needs the slots created and the
# devices assigned, or it lands on an empty Dashboard. Gating this on
# -not $SkipToTail meant a tail run injected the synthetic devices and
# then created NO virtual controllers, so every card index was out of
# range and nothing could be photographed.
#
# -Only skips this block. Slot-to-device assignment exists for the PAD pages,
# and every one of those is UI-driven: scroll the card list, realize the detail
# pane, hunt the toggle, click it twice. A request for three Devices-page and
# Settings-page images was spending minutes mapping a racing wheel and a merged
# mouse to slots that none of those images show. The devices themselves are
# already in the list from STEP 0's XML injection, which is all a Devices-page
# shot needs. A focused target that DOES need an assignment adds it beside its
# own recipe in the focused pass.
if ($script:doSetup -and $Only.Count -eq 0) {
Assign-DeviceToSlot -DeviceNamePart "DualSense" -SlotNumberLabel "1" | Out-Null
Assign-DeviceToSlot -DeviceNamePart "DualSense" -SlotNumberLabel "2" | Out-Null
# Also put the Xbox Series X and the synthetic G29 wheel on the Xbox slot
# (SlotNumber 1), beside the DualSense. DualSense stays the default selection
# (alphabetically first), so the main Xbox-slot captures are unchanged; the
# Impulse-Triggers and Wheel captures at the end of that section switch the
# mapped-device dropdown to the Xbox pad / the wheel to surface their tabs.
Assign-DeviceToSlot -DeviceNamePart "Xbox Series X GIP" -SlotNumberLabel "1" -Reassert | Out-Null
Assign-DeviceToSlot -DeviceNamePart "Logitech G29" -SlotNumberLabel "1" | Out-Null
# Mouse on the KBM slot (SlotNumber 4) for the #200 Mouse-gestures tab. The Mouse
# tab gates on the SELECTED device being IsMouse (CapType == Mouse == 18); a KBM
# slot with no device assigned surfaces no Mouse tab. "All Mice (Merged)" is a
# first-class UserDevice (CapType 18) in the cache, so assigning + selecting it
# lights TabMouse, the same shape the Wheel tab uses (assign G29, select it).
Assign-DeviceToSlot -DeviceNamePart "All Mice (Merged)" -SlotNumberLabel "5" | Out-Null
# WHEN THIS KEEPS FAILING, STOP DRIVING THE UI. A device assignment is
# nothing but `UserSetting.MapTo = slotIndex` in PadForge.xml: clone an
# existing <Setting>, override InstanceGuid / ProductGuid / MapTo, and
# append it under <UserSettings>. That skips the whole flaky chain (device
# card scroll, detail-pane realize, toggle enumeration) which stranded
# pad-pointer and wii-pointer-mode across four runs on 2026-07-30. It
# cannot live in STEP 0 as written, because the slots are created through
# the UI afterwards, so a targeted recapture script is the place for it.
#
# Assign the Wii Remote to the Extended slot so its Pointer / Gyro tabs are
# reachable for the 3.6.0 Pointer-tab capture (issue #146). SlotNumber follows
# DevicesViewModel.RefreshSlotButtons, which walks slots in TYPE-GROUP order
# (Xbox -> PlayStation -> Extended -> KBM -> MIDI) to match the dashboard cards,
# NOT creation/PadIndex order. So SlotNumber 1=Xbox, 2=PlayStation, 3=Extended,
# 4=KBM, 5=MIDI. The Extended slot is SlotNumber 3 (KBM at 4 hides the capability
# tabs, which is why assigning the Wii to 4 left the Pointer tab unreachable).
# The Wii Remote's IR-camera capability is identity-derived (VID 0x057E + name),
# so the tab is offered whether the placeholder device is online or not.
Assign-DeviceToSlot -DeviceNamePart "Wii Remote" -SlotNumberLabel "4" | Out-Null
# The 3 Wii source-picker devices are NOT assigned here. They must NOT ride the
# Xbox slot during STEP 3, or auto-map would combine every slot device into
# multi-source rows and busy the pad-mappings shot. The source-picker block in
# STEP 3b swaps them onto slot 1 ALONE (after the Xbox captures are done) so each
# gets a clean single-source grid.

# Give the Devices page time to write the assignment back to the VMs and
# for the PadPage's hasForceFeedback / hasAdaptiveTriggers / hasLightbar
# gating to flip on for the affected slots.
Start-Sleep -Milliseconds 2000

# The Xbox GIP dummy is the one assignment the UI chain never lands, and two
# shots depend on it: the Impulse Triggers tab gates on HasRumbleTriggers and
# the Guide Button LED card wants an Xbox pad selected. Write the mapping
# instead of clicking for it. This runs at a clean boundary, before any
# capture, so the restart costs nothing and the full multi-slot topology the
# rest of the gallery shows is untouched.
Ensure-DeviceAssigned -DeviceNamePart "Xbox Series X GIP" -PadIndex 0 -SlotType 0 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null

# Same treatment for the mouse. The Mouse tab gates on the SELECTED device
# being IsMouse, so pad-mouse-gestures needs "All Mice (Merged)" reachable in
# the KBM slot's device dropdown. The UI toggle assigned it and the dropdown
# still did not list it, exactly as with the Xbox GIP, and the shot has never
# been captured as a result. KBM is pad index 4 in creation order
# (Xbox 0, PlayStation 1, Nintendo 2, Extended 3, KBM 4).
Ensure-DeviceAssigned -DeviceNamePart "All Mice (Merged)" -PadIndex 4 -SlotType 4 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null

# PlayStation is pad index 1 in creation order (Xbox 0, PlayStation 1).
Seed-AudioDsp -DeviceGuid "bbbb2222-3333-4444-5555-666677778888" -DeviceNamePart "DualSense Wireless Controller" -PadIndex 1 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null

# Macros, written AFTER the slots exist. STEP 0 clears SlotCreated to all-false
# and saves, and LoadMacros skips any macro whose slot is not created, so five
# macros injected in STEP 0 are discarded the moment the app reads that file.
# The slots are created through the UI afterwards, by which point the macros
# are already gone from memory and the next save writes <Macros /> back. That
# is why every macro shot came out as an empty pane while the injection step
# cheerfully logged success. Writing them here, with the topology already
# persisted, is the same state that loads them correctly by hand.
Ensure-MacrosLoaded -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null

# Web controller server is enabled via XML injection in Step 0. No UI click needed.
}

$li36 = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$btn36 = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

# Scroll the content pane down until $Anchor is inside the window, then stop
# with it comfortably in frame. Returns false rather than capturing a page
# that does not contain what the shot is named after.
function Scroll-ToAnchor {
    param([string]$Anchor, [int]$MaxSteps = 30, [int]$ClicksPerStep = 4)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $top = $wr.Top + 80
    $bot = $wr.Bottom - 80
    for ($i = 0; $i -le $MaxSteps; $i++) {
        $el = Find-UIA -Name $Anchor
        if ($el) {
            $r = Get-Rect $el
            if ($null -ne $r -and $r.Y -ge $top -and ($r.Y + $r.Height) -le $bot) {
                Write-Host "  anchor '$Anchor' in view after $i step(s)"
                Start-Sleep -Milliseconds 500
                return $true
            }
        }
        ScrollContent -Clicks (-1 * $ClicksPerStep)
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# Defined here rather than beside the Devices block below it, because the
# focused pass calls it and PowerShell resolves a function from what has
# already executed, not from the whole file.

function Select-DeviceByName36 {
    param([string]$NamePart)
    Reset-DeviceTypeFilter | Out-Null
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $midY  = [int](($wr.Top + $wr.Bottom) / 2)
    # Wheel at the card list's OWN center-x, read from any realized row, NOT at
    # a fixed Left+400. Assign-DeviceToSlot fixed exactly this in 4.1.0 ("the
    # old fixed Left+400 landed outside its scroll viewer, so every
    # below-the-fold device went unreachable") and this twin kept the broken
    # constant, so the wheel spun over the page instead of the list. On a
    # 35-device machine the search never realized anything past the H rows and
    # devices-move silently went stale: the run reported "not found after
    # scroll" while printing a diagnostic full of rows from the top of a list
    # it had never moved.
    $listX = [int]($wr.Left + 400)
    foreach ($it0 in $script:uiaWin.FindAll($TD, $li36)) {
        $r0 = Get-Rect $it0
        if ($null -ne $r0 -and $r0.Width -gt 0) { $listX = [int]($r0.X + $r0.Width / 2); break }
    }
    # Scroll the card list to the top first so a top-of-list target (e.g. the
    # "All Consumer Controls (Merged)" row) is realized even if a prior capture
    # left the list scrolled down. Then step down searching each realized page.
    [Win32]::ForceFG($script:hwnd)
    for ($u = 0; $u -lt 8; $u++) { [Win32]::ScrollAt($listX, $midY, 3); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 300
    # Match the card's NAME first, then its child text. A device card shows the
    # product name on line one and its TYPE on line two, and the consumer
    # devices on this machine are named "USB Receiver" with CONSUMER CONTROL
    # only on the type line. A name-only match therefore could never find them,
    # which is why devices-consumer sat stale while two such devices were
    # enumerated three rows apart.
    $txtCond36 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    # Only ever click a row that is actually ON SCREEN. A virtualized list hands
    # back rects for rows far below the fold, and clicking one lands nowhere:
    # the 19:27 run clicked "All Consumer Controls (Merged)" at y=2275 on a
    # 1550-tall window, the selection never moved, and devices-consumer shipped
    # showing whatever had been selected before. Same rule the assignment path
    # already uses: in view, click. Out of view, scroll toward it and re-find.
    $listTop36 = Get-DeviceListTop
    $winBot36 = $wr.Bottom - 40
    $inView36 = {
        param($el)
        $r = Get-Rect $el
        if ($null -eq $r) { return $false }
        return ($r.Y -ge $listTop36 -and ($r.Y + $r.Height) -le $winBot36)
    }
    # A row that matches by NAME but sits below the fold is not a miss, it is a
    # scroll. The wheel-scroll below moves the page, not this virtualized card
    # list: 16 steps of it left every row at the same y, so "NFC Reader" stayed
    # parked at y=1477 against a 1499 cutoff and reported "not found after
    # scroll" while sitting on screen the whole time. ScrollItemPattern is what
    # the assignment path already uses for exactly this, so ask the list to
    # bring the row up before deciding anything.
    $scrollIntoView36 = {
        param($el)
        try {
            $el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
            Start-Sleep -Milliseconds 400
            return $true
        } catch { return $false }
    }
    # 24, matching the sibling. Sixteen pages of a 35-device list does not
    # reach the bottom once the owner's own cached rows are merged in.
    for ($try = 0; $try -lt 24; $try++) {
        $items = $script:uiaWin.FindAll($TD, $li36)
        # Re-read the list's center-x each pass: the first pass may have run
        # before any row was realized.
        foreach ($it0 in $items) {
            $r0 = Get-Rect $it0
            if ($null -ne $r0 -and $r0.Width -gt 0) { $listX = [int]($r0.X + $r0.Width / 2); break }
        }
        foreach ($it in $items) {
            if (($it.Current.Name -like "*$NamePart*") -and -not (& $inView36 $it)) {
                & $scrollIntoView36 $it | Out-Null
            }
        }
        $items = $script:uiaWin.FindAll($TD, $li36)
        foreach ($it in $items) {
            if (($it.Current.Name -like "*$NamePart*") -and (& $inView36 $it)) {
                Click-El $it -Label "Device '$NamePart'" -Delay 900 | Out-Null
                return $true
            }
        }
        foreach ($it in $items) {
            $hit = $false
            foreach ($t in $it.FindAll($TD, $txtCond36)) {
                if ($t.Current.Name -like "*$NamePart*") { $hit = $true; break }
            }
            if ($hit -and (& $inView36 $it)) {
                Click-El $it -Label "Device '$NamePart' (matched on type line)" -Delay 900 | Out-Null
                return $true
            }
        }
        [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($listX, $midY, -3); Start-Sleep -Milliseconds 350
    }
    Write-Host "  !! device '$NamePart' not found after scroll" -ForegroundColor Yellow
    Write-Host "  Diagnostic: realized device rows at the end of the search:" -ForegroundColor Yellow
    foreach ($it in $script:uiaWin.FindAll($TD, $li36)) {
        $rr = Get-Rect $it
        $yy = if ($null -eq $rr) { "no-rect" } else { "y=$($rr.Y)" }
        Write-Host "    [$($it.Current.Name)] $yy"
    }
    return $false
}

# ==============================================================================
# FOCUSED PASS: -Only goes STRAIGHT to its targets, then stops
# ==============================================================================
# -Only used to filter Cap and nothing else, so asking for six images still
# walked all 39 blocks, opened every modal on the way, and inherited every
# hazard in them. That is how the 2026-08-19 run left the Voice Macros
# FluentWindow up and then photographed it, frozen, as six different
# screenshots. Filtering the OUTPUT is not scoping the WORK.
#
# Each entry below carries the navigation its shot needs and nothing else, so
# a targeted refresh cannot be corrupted by a page it never had to visit.
# Adding a target here is the cost of admission for a name that needs to be
# refreshable on its own.
if ($Only.Count -gt 0) {
    Write-Host ""
    Write-Host "=== FOCUSED PASS ($($Only.Count) target(s)) ===" -ForegroundColor Cyan

    # Devices page, one selected device, one or more shots off that selection.
    $deviceTargets = @(
        @{ Match = "NFC";           Shots = @("devices-nfc", "nfc-live-preview") },
        @{ Match = "MIDI Keyboard"; Shots = @("midi-input", "midi-input-mode-devices-page") },
        @{ Match = "Microphone";    Shots = @("devices-voice") },
        @{ Match = "PlayStation Move"; Shots = @("devices-move") },
        @{ Match = "DualSense";     Shots = @("devices-dualsense") }
    )
    foreach ($t in $deviceTargets) {
        $wanted = @($t.Shots | Where-Object { Want $_ })
        if ($wanted.Count -eq 0) { continue }
        Write-Host "[focused] Devices: $($t.Match)"
        Nav "Devices"; Start-Sleep -Milliseconds 800
        if (Select-DeviceByName36 $t.Match) {
            foreach ($shot in $wanted) { Cap $shot }
        } else {
            Write-Host "  !! no '$($t.Match)' row -- SKIPPED $($wanted -join ', ')" -ForegroundColor Red
        }
    }

    # Scrolled page sections. A fixed click count is a guess about page
    # LENGTH, and the page keeps growing: -40 reached the Community Configs
    # card when it was written and, once the Diagnostics section landed above
    # it, photographed HidHide instead. Scroll in steps and stop when the
    # anchor is actually on screen, so the shot cannot silently drift onto a
    # neighbouring card.
    $scrollTargets = @(
        @{ Shot = "dsu-port-box";               Page = "Dashboard"; Anchor = "Enable DSU Motion Server (CemuHook Motion Provider Protocol)" },
        @{ Shot = "remote-link";                Page = "Dashboard"; Anchor = "Remote Link" },
        @{ Shot = "settings-community-configs"; Page = "Settings";  Anchor = "Community Configs"; After = -10 },
        @{ Shot = "settings-battery-alerts";    Page = "Settings";  Anchor = "Battery Alerts";           After = -6 },
        @{ Shot = "settings-drivers";           Page = "Settings";  Anchor = "HIDMaestro Driver";        After = -4 },
        @{ Shot = "settings-driver-cards";      Page = "Settings";  Anchor = "Windows MIDI Services";    After = -4 },
        @{ Shot = "driver-status-flames";       Page = "Settings";  Anchor = "Windows MIDI Services";    After = -7 },
        @{ Shot = "settings-hidhide";           Page = "Settings";  Anchor = "Whitelisted Applications"; After = -6 },
        @{ Shot = "settings-steamvr";           Page = "Settings";  Anchor = "SteamVR";                  After = -6 },
        @{ Shot = "settings-diagnostics";       Page = "Settings";  Anchor = "Diagnostics";              After = -14 }
    )
    foreach ($t in $scrollTargets) {
        if (-not (Want $t.Shot)) { continue }
        Write-Host "[focused] $($t.Page) section: $($t.Shot)"
        Nav $t.Page; Start-Sleep -Milliseconds 900
        if (Scroll-ToAnchor -Anchor $t.Anchor) {
            # An anchor in view proves the card STARTS on screen, not that it
            # FITS: the Community Configs heading landed on the last visible
            # line with its opt-in checkbox and buttons below the fold. Where a
            # card is taller than its heading, keep scrolling past the anchor.
            if ($t.ContainsKey("After")) {
                ScrollContent -Clicks $t.After
                Start-Sleep -Milliseconds 500
            }
            Cap $t.Shot
        } else {
            Write-Host "  !! anchor '$($t.Anchor)' never came into view -- SKIPPED $($t.Shot)" -ForegroundColor Red
        }
        ScrollContent -Clicks 80
    }

    # Unscrolled whole-page shots.
    $pageTargets = @(
        @{ Shot = "dashboard"; Page = "Dashboard" },
        @{ Shot = "profiles";  Page = "Profiles" },
        @{ Shot = "devices";   Page = "Devices" },
        @{ Shot = "settings";  Page = "Settings" },
        @{ Shot = "about";     Page = "About" }
    )
    foreach ($t in $pageTargets) {
        if (-not (Want $t.Shot)) { continue }
        Write-Host "[focused] page: $($t.Shot)"
        Nav $t.Page; Start-Sleep -Milliseconds 800
        Cap $t.Shot
    }

    # Pad-page tab shots on the PlayStation slot. These need an assignment
    # (the Audio tab only appears when the slot's selected device has a
    # speaker) and, for the DSP shot, the EQ seeded ON so the curve and rows
    # render. Both are data, so both are written by XML and the app restarted,
    # the same way Ensure-DeviceAssigned and Seed-AudioDsp do it for the full
    # run. The full run's Seed-AudioDsp call sits inside the setup block that
    # -Only deliberately skips, which is why the first targeted run reported
    # "no focused recipe" for this name.
    $psTabTargets = @(
        @{ Shot = "pad-audio";     Tab = "Audio"; Scroll = 0 },
        @{ Shot = "pad-audio-dsp"; Tab = "Audio"; Scroll = -16 }
    )
    $psWanted = @($psTabTargets | Where-Object { Want $_.Shot })
    if ($psWanted.Count -gt 0) {
        Write-Host "[focused] PlayStation slot: $(($psWanted | ForEach-Object { $_.Shot }) -join ', ')"
        # PlayStation is pad index 1 in creation order (Xbox 0, PlayStation 1).
        Ensure-DeviceAssigned -DeviceNamePart "DualSense" -PadIndex 1 -SlotType 1 `
            -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
        Seed-AudioDsp -DeviceGuid "bbbb2222-3333-4444-5555-666677778888" -DeviceNamePart "DualSense Wireless Controller" -PadIndex 1 `
            -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null

        # Two restarts just happened. Re-attach the UIA root and give the
        # dashboard cards time to realize, or the first child match under
        # SlotsItemsControl is a 27x27 badge rather than the card (that is
        # exactly what the first focused run clicked, and the pad page it
        # opened had no Audio tab because it was not the PlayStation slot).
        Start-Sleep -Milliseconds 2500
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Nav "Dashboard"; Start-Sleep -Milliseconds 1500
        $slotsHost = Find-UIA -Aid "SlotsItemsControl"
        $cards = if ($slotsHost) { @($slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
        Write-Host "  Found $((Get-Count $cards)) slot card(s)"
        if ((Get-Count $cards) -ge 2) {
            Click-El $cards[1] -Label "PlayStation Slot card" -Delay 4000 | Out-Null
            # The device-gated tabs flip visible only after the slot's config
            # binds and capability gating propagates, up to ~10 s on a cold
            # bring-up. Poll for the Audio tab the way the full run polls for
            # Adaptive Triggers, instead of asking once and giving up.
            $padPage = $null; $audioTab = $null
            $rbCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            for ($w = 0; $w -lt 12 -and -not $audioTab; $w++) {
                Start-Sleep -Milliseconds 1000
                $padPage = Find-UIA -Aid "PadPageView"
                if ($padPage) {
                    $tabs = $padPage.FindAll($TC, $rbCond)
                    $audioTab = $tabs | Where-Object { $_.Current.Name -eq "Audio" } | Select-Object -First 1
                }
            }
            if (-not $audioTab) {
                $names = if ($padPage) { ($padPage.FindAll($TC, $rbCond) | ForEach-Object { $_.Current.Name }) -join ', ' } else { '(no PadPageView)' }
                Write-Host "  !! Audio tab never appeared. Tabs seen: $names" -ForegroundColor Red
            }
            foreach ($t in $psWanted) {
                if ($audioTab) {
                    Click-El $audioTab -Label "Tab:Audio" -Delay 1000 | Out-Null
                    Start-Sleep -Milliseconds 900
                    if ($t.Scroll -ne 0) { ScrollContent -Clicks $t.Scroll; Start-Sleep -Milliseconds 500 }
                    Cap $t.Shot
                    if ($t.Scroll -ne 0) { ScrollContent -Clicks (-$t.Scroll) }
                } else {
                    Write-Host "  !! SKIPPED $($t.Shot)" -ForegroundColor Red
                }
            }
        } else {
            Write-Host "  !! fewer than 2 slot cards -- SKIPPED $(($psWanted | ForEach-Object { $_.Shot }) -join ', ')" -ForegroundColor Red
        }
    }

    $missed = @($Only | Where-Object { -not (Test-Path (Join-Path $script:OutputDir "$_.png")) })
    if ($missed.Count -gt 0) {
        Write-Host ""
        Write-Host "!! -Only names with no focused recipe (or that failed): $($missed -join ', ')" -ForegroundColor Red
        Write-Host "!! Add them to the focused pass rather than running the whole harness." -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "Focused pass done." -ForegroundColor Green
    return
}

# ==============================================================================
# STEP 3: Capture all pages
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3: Capture pages ===" -ForegroundColor Cyan
$n = 0
# Count of NUMBERED blocks below, which is what Next() advances, not the number
# of screenshots (76 distinct Cap names, since several blocks take more than
# one shot). This read 62, so a complete run ended far short of its own total
# and looked like it had skipped steps on a script that runs for many minutes.
#
# 36, not the 34 Next calls written in the source: the one inside the Gyro /
# Audio / Touchpad loop runs three times, so it contributes 3 rather than 1.
$total = 39

function Next { $script:n++; return $script:n }

if (-not $SkipToTail) {
# ---- 1. Dashboard ----
Write-Host "[$(Next)/$total] Dashboard"
Nav "Dashboard"; Start-Sleep -Milliseconds 500; Cap "dashboard"
# Dashboard slot card (Dashboard.md) and the live polling-rate readout on the
# engine card (Input-Precision.md). Both live in the Dashboard's top view: the
# engine card carries the live Hz next to the power flame while the engine is
# forging (5 slots exist by now), and the slot cards sit right under it. Same
# unscrolled view as "dashboard"; the wiki crops to the region it documents.
Cap "dashboard-polling-readout"
Cap "dashboard-slot-card"

# ---- 2. Profiles ----
Write-Host "[$(Next)/$total] Profiles"
Nav "Profiles"; Cap "profiles"
# Live FOREGROUND readout (Profiles.md): the readout line only renders while
# auto-switch is on, so flip the checkbox on, capture, then flip it back. The
# readout shows whatever exe is in front (PadForge during capture, so it reads
# unmatched); a matched/lit readout needs the profile's game running in front,
# which the capture can't stage. Toggling back keeps auto-switch behaviour off
# for the rest of the run (foreground is always PadForge -> always Default).
# Matched CASE-INSENSITIVELY on a stable fragment, not on the full label.
# UIA's NameProperty condition is case-sensitive, the checkbox has no
# AutomationId, and the Title Case UI sweep re-cased this label to
# "Auto-Switch Profiles Based on Foreground Application". The harness kept
# the old sentence-case literal, so the lookup silently missed and
# profiles-foreground-readout had been skipped on every run since.
$autoSw = $null
$cbCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::CheckBox)
foreach ($cb in $script:uiaWin.FindAll($TD, $cbCond)) {
    try {
        if ($cb.Current.Name -match '(?i)auto-?switch.*foreground') { $autoSw = $cb; break }
    } catch {}
}
if ($autoSw) {
    Click-El $autoSw -Label "Auto-switch checkbox" -Delay 800 | Out-Null
    Cap "profiles-foreground-readout"
    Click-El $autoSw -Label "Auto-switch checkbox (off)" -Delay 600 | Out-Null
} else {
    Write-Host "  !! Auto-switch checkbox not found; skipping profiles-foreground-readout" -ForegroundColor Yellow
}

# ---- 3. Devices ----
Write-Host "[$(Next)/$total] Devices"
Nav "Devices"
Start-Sleep -Milliseconds 500
# The canonical devices shots must show the UNFILTERED list: reset any
# leftover type-filter chip before capturing.
Reset-DeviceTypeFilter | Out-Null
Start-Sleep -Milliseconds 400
# Type-filter chips (Devices.md): the chip row sits above the card list and is
# visible with no device selected, so capture it before clicking a card.
Cap "devices-facet-chips"
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
Write-Host "  Found $((Get-Count $slots)) slot card(s)"
if ((Get-Count $slots) -ge 1) {
    Click-El $slots[0] -Label "Xbox Slot card" -Delay 2000 | Out-Null

    # 4. Controller 3D view
    Write-Host "[$(Next)/$total] Controller - 3D view"
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "3D View Tab" -Delay 1000 }
    }
    Cap "pad-controller-3d"

    # 4a. Config tab row (Controller-Slots.md): the Controller tab shows the full
    # per-slot tab strip. Same view as the 3D shot; the wiki points at the strip.
    Cap "pad-config-tabs"

    # 4b. Mapping annotations on the 3D model (3D-and-2D-Visualization.md +
    # 3D-Model-System.md). The annotation toggle (glyph E8EC, top-right of the
    # model host) flips the chip/leader-line/trigger-bar overlay on. The Xbox
    # slot carries an auto-mapped multi-device grid, so chips have rows to draw.
    # AutomationId "AnnotationToggle" belongs to the 3D view's toggle button.
    $annBtn = Find-UIA -Aid "AnnotationToggle"
    if (-not $annBtn) {
        # Re-attach: a stale cached UIA tree can hide peers realized after the
        # Helix viewport settles (both toggles carry AutomationIds at HEAD).
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Start-Sleep -Milliseconds 400
        $annBtn = Find-UIA -Aid "AnnotationToggle"
    }
    if ($annBtn) {
        Click-El $annBtn -Label "Annotation toggle (3D)" -Delay 900 | Out-Null
        Cap "pad-mapping-annotations"
        Cap "3d-model-annotation-overlay"
        Click-El $annBtn -Label "Annotation toggle (3D) off" -Delay 500 | Out-Null
    } else {
        # UIA can't reach the toggle inside the 3D model host (it overlays a Helix
        # viewport and never surfaced its AutomationId peer here), so use a
        # window-fraction coordinate. The tag glyph (E8EC) sits top-right of the
        # model host, just LEFT of "Reset View", ~0.925 W / 0.161 H (measured off
        # pad-controller-3d at 2582x1550).
        Write-Host "  AnnotationToggle (3D) not in UIA; coordinate fallback" -ForegroundColor DarkGray
        $wrAn = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrAn) | Out-Null
        $anW = $wrAn.Right - $wrAn.Left; $anH = $wrAn.Bottom - $wrAn.Top
        $anX = [int]($wrAn.Left + 0.925 * $anW); $anY = [int]($wrAn.Top + 0.161 * $anH)
        [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
        [Win32]::ClickAt($anX, $anY); Start-Sleep -Milliseconds 900
        Cap "pad-mapping-annotations"
        Cap "3d-model-annotation-overlay"
        [Win32]::ClickAt($anX, $anY); Start-Sleep -Milliseconds 500   # toggle off
    }

    # 5. Controller 2D view (weak-9 recapture: the old blind coordinate left
    # the 3D view on screen; the ViewModeToggle Aid click is deterministic)
    Write-Host "[$(Next)/$total] Controller - 2D view"
    Toggle-ViewMode | Out-Null
    Start-Sleep -Milliseconds 600
    Cap "pad-controller-2d"
    # 5a. Annotation overlay on the 2D preview (2D-Overlay-System.md). The 2D
    # view has its own toggle (AutomationId "AnnotationToggle2D"), same top-right
    # spot. Toggle on, capture, toggle off before switching back to 3D.
    $annBtn2D = Find-UIA -Aid "AnnotationToggle2D"
    if (-not $annBtn2D) {
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Start-Sleep -Milliseconds 400
        $annBtn2D = Find-UIA -Aid "AnnotationToggle2D"
    }
    if ($annBtn2D) {
        Click-El $annBtn2D -Label "Annotation toggle (2D)" -Delay 900 | Out-Null
        Cap "2d-annotation-overlay"
        Click-El $annBtn2D -Label "Annotation toggle (2D) off" -Delay 500 | Out-Null
    } else {
        # Coordinate fallback (same reason as the 3D toggle). The 2D view has NO
        # Reset View sibling, so its tag glyph sits alone at the far right of the
        # model host, ~0.965 W / 0.161 H.
        Write-Host "  AnnotationToggle2D not in UIA; coordinate fallback" -ForegroundColor DarkGray
        $wrA2 = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrA2) | Out-Null
        $a2W = $wrA2.Right - $wrA2.Left; $a2H = $wrA2.Bottom - $wrA2.Top
        $a2X = [int]($wrA2.Left + 0.965 * $a2W); $a2Y = [int]($wrA2.Top + 0.161 * $a2H)
        [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
        [Win32]::ClickAt($a2X, $a2Y); Start-Sleep -Milliseconds 900
        Cap "2d-annotation-overlay"
        [Win32]::ClickAt($a2X, $a2Y); Start-Sleep -Milliseconds 500   # toggle off
    }
    # Switch back to 3D
    Toggle-ViewMode | Out-Null
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
        # The macro ListBox uses DisplayMemberPath, so items expose their text as
        # Text peers, not named ListItems -- FindAll(ListItem) returns nothing (any
        # scope), which is why this always fell to the coordinate fallback and left
        # nothing selected. Select "Quick Combo" by exact Name from the root, which
        # reaches the Text peer and highlights the macro (its action list renders).
        $qc = Find-UIA -Name "Quick Combo"
        if ($qc) {
            Click-El $qc -Label "Macro: Quick Combo" -Delay 500 | Out-Null
            $macroClicked = $true
            Start-Sleep -Milliseconds 400
        }
        if (-not $macroClicked) {
            # Fallback by window fraction. The macro ListBox items are Text peers
            # under a DisplayMemberPath template; when the name-find misses, the old
            # fallback clicked ppRect+(180,200) which landed on the Add/Remove row,
            # not a macro, so NOTHING was selected and the trigger editor (with the
            # "Add from List" combo) never rendered. Click the first item ("Quick
            # Combo") directly: left column, first row ~0.242 H / ~0.175 W (read off
            # pad-macros at 2582x1550).
            $wrMk = New-Object Win32+RECT
            [Win32]::GetWindowRect($script:hwnd, [ref]$wrMk) | Out-Null
            $mkW = $wrMk.Right - $wrMk.Left; $mkH = $wrMk.Bottom - $wrMk.Top
            Write-Host "  Fallback: clicking Quick Combo macro by coordinate"
            [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
            [Win32]::ClickAt([int]($wrMk.Left + 0.175 * $mkW), [int]($wrMk.Top + 0.242 * $mkH))
            Start-Sleep -Milliseconds 500
        }
    }

    # HARD GATE. Every macro shot on 2026-08-09 shipped as the same empty pane
    # reading "Select a macro on the left, or press Add to create one", because
    # this section photographs whatever is on screen and never asked whether a
    # macro existed. If the list is empty the injection failed, and a blank
    # frame is worse than a missing one, so say so and skip rather than ship it.
    # ASK THE SETTINGS FILE, NOT UI AUTOMATION. The macro ListBox uses a
    # DataTemplate, and WPF exposes NO ListItem peers for it at all: a probe with
    # the tab open, five macros plainly visible on screen, returned ListItem
    # count 0 and zero name matches. Every name-based lookup here is blind by
    # construction, which is why the old gate reported an empty list and skipped
    # five perfectly good shots, and why the selection code has always fallen
    # through to its coordinate click. The settings file answers the only
    # question the gate actually has, and answers it definitively.
    $macroNames = @("Quick Combo", "Volume Control", "Sleep Controller", "Center Cursor", "Rapid Fire")
    $macroSeen = 0
    try {
        [xml]$mkChk = Get-Content $PadForgeXml
        $mkRoot = $mkChk.PadForgeSettings.SelectSingleNode("Macros")
        if ($mkRoot) { $macroSeen = @($mkRoot.SelectNodes("Macro")).Count }
    } catch { $macroSeen = 0 }
    Write-Host "  macros in settings: $macroSeen"
    $script:MacrosPresent = ($macroSeen -gt 0)

    # Empty list: repair it rather than shrug. Write the macros with the app
    # closed (the state proven to load them), restart, come back to this tab and
    # look again. Only if it is STILL empty do the macro shots get skipped.
    if (-not $script:MacrosPresent) {
        Write-Host "  !! macro list empty -- repairing" -ForegroundColor Yellow
        if (Ensure-MacrosLoaded -XmlPath $PadForgeXml -ExePath $PadForgeExe) {
            Nav "Dashboard"; Start-Sleep -Milliseconds 1200
            $slotsMk = Find-UIA -Aid "SlotsItemsControl"
            if ($slotsMk) {
                $firstCard = $slotsMk.FindFirst($TC, [System.Windows.Automation.Condition]::TrueCondition)
                if ($firstCard) { Click-El $firstCard -Label "slot 1 card (macro repair)" -Delay 1500 | Out-Null }
            }
            $mkTab = Find-UIA -Name "Macros"
            if ($mkTab) { Click-El $mkTab -Label "Tab:Macros (after repair)" -Delay 1200 | Out-Null }
            $macroSeen = 0
            try {
                [xml]$mkChk2 = Get-Content $PadForgeXml
                $mkRoot2 = $mkChk2.PadForgeSettings.SelectSingleNode("Macros")
                if ($mkRoot2) { $macroSeen = @($mkRoot2.SelectNodes("Macro")).Count }
            } catch { $macroSeen = 0 }
            $script:MacrosPresent = ($macroSeen -gt 0)
            if ($script:MacrosPresent) {
                Write-Host "  macro list repaired ($macroSeen names visible)" -ForegroundColor Green
                # Selecting by name cannot work here either, for the same reason.
                # The list's first row sits at a stable fraction of the window,
                # which is how the main path already selects it.
                $wrMk2 = New-Object Win32+RECT
                [Win32]::GetWindowRect($script:hwnd, [ref]$wrMk2) | Out-Null
                [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
                [Win32]::ClickAt([int]($wrMk2.Left + 0.175 * ($wrMk2.Right - $wrMk2.Left)),
                                 [int]($wrMk2.Top  + 0.242 * ($wrMk2.Bottom - $wrMk2.Top)))
                Start-Sleep -Milliseconds 600
            }
        }
    }

    if (-not $script:MacrosPresent) {
        Write-Host "  !! NO MACROS IN THE LIST after repair. SKIPPING every macro shot" -ForegroundColor Red
        Write-Host "  !! rather than shipping blank panes. Check element ORDER in the" -ForegroundColor Red
        Write-Host "  !! injected <Macro> XML: XmlSerializer drops the whole array on" -ForegroundColor Red
        Write-Host "  !! an out-of-order element." -ForegroundColor Red
    } else {
        Write-Host "  macro list populated ($macroSeen of $($macroNames.Count) names visible)" -ForegroundColor Green
        Cap "pad-macros"
    }

    # 6a. Add-from-List trigger dropdown (Macros.md): with a macro selected, the
    # Trigger panel shows an "Add from List" label followed by a ComboBox of
    # buttons / POV / axes / touchpad click / enabled gestures. The label is a
    # UIA Text peer; the combo sits directly to its right (label Width=80 then
    # the combo, one horizontal StackPanel). Click into the combo and capture the
    # open list, then ESC so the later Mappings tab-switch is undisturbed.
    Write-Host "  Macro: Add from List dropdown"
    $comboX = 0; $comboY = 0
    $addListLbl = Find-UIA -Name "Add from List"
    $lr = Get-Rect $addListLbl
    if ($null -ne $lr) {
        $comboX = [int]($lr.X + $lr.Width + 120)
        $comboY = [int]($lr.Y + $lr.Height / 2)
    } else {
        # The label's UIA Name-find is flaky here (returned null in run 1 even though
        # the macro was selected and the label + combo were plainly on screen). Fall
        # back to a window fraction: with Quick Combo selected, the "Add from List"
        # row's combo sits at ~0.415 W / ~0.36 H (read off pad-macros at 2582x1550).
        Write-Host "  Add from List label not in UIA; coordinate fallback" -ForegroundColor DarkGray
        $wrAL = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrAL) | Out-Null
        $comboX = [int]($wrAL.Left + 0.415 * ($wrAL.Right - $wrAL.Left))
        $comboY = [int]($wrAL.Top  + 0.360 * ($wrAL.Bottom - $wrAL.Top))
    }
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($comboX, $comboY); Start-Sleep -Milliseconds 800
    if ($script:MacrosPresent) { Cap "macro-add-from-list" -AllowModal } else { Write-Host "  skipped macro-add-from-list (no macros)" -ForegroundColor Yellow }
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300

    # 7. Mappings
    Write-Host "[$(Next)/$total] Mappings"
    Tab "Mappings"; Cap "pad-mappings"

    # 7-0. Per-source Sensitivity slider (#9). The generic Sensitivity knob
    # rides the selected row's editor strip when the primary descriptor is a
    # plain "Axis N" / "Slider N" (SourceCoercion.IsGenericSensitivityDescriptor).
    # The DualSense Left Stick X row stores "Axis 0" behind its friendly name,
    # so selecting it (output index 18; rows start 0.206 H, 0.0251 H/row on the
    # HEAD mappings.jpg) renders the slider in the expanded editor. MUST run
    # BEFORE the Stick Trim block: an expanded row shifts every row BELOW it,
    # and the Stick Trim block's Right Trigger row (index 17) sits above this
    # one, so its coordinates stay valid while this row is expanded.
    Write-Host "  Mapping: per-source Sensitivity slider (#9, Left Stick X row)"
    Start-Sleep -Milliseconds 700
    $wrSe = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrSe) | Out-Null
    $sew = $wrSe.Right - $wrSe.Left; $seh = $wrSe.Bottom - $wrSe.Top
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrSe.Left + 0.22 * $sew), [int]($wrSe.Top + (0.206 + 18 * 0.0251) * $seh)); Start-Sleep -Milliseconds 1100
    Cap "mapping-sensitivity"

    # 7a. Stick Trim combine (#155, Trigger-Deadzones/Settings). The Xbox slot is
    # still the multi-device crowd (DualSense + Xbox Series X, both gamepad-class
    # auto-mapped), so its trigger rows are multi-source and expose the Combine
    # dropdown. Map All first to be sure every device contributes a source (the
    # Combine row only shows on multi-source rows). Then select the Right Trigger
    # row so its details editor opens, set Combine = Stick Trim by type-ahead
    # ("Stick"), and the Trim Deadzone / Trim Rate / Reset on Release strip
    # renders (ShouldShowTrimSettings gate). The grid cell/detail combos have no
    # UIA peers, so this is coordinate + keyboard, same idiom as
    # Capture-SourcePicker. Fractions are read off the maximized-window Mappings
    # shot and are the least-certain part of this pass -- tune the row/combo Y on
    # the first real run if the strip doesn't render.
    Write-Host "  Mapping: Stick Trim combine (Right Trigger row details)"
    Select-MappedDevice "DualSense" | Out-Null
    if (Tab "Mappings") {
        Start-Sleep -Milliseconds 1200
        $wrST = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrST) | Out-Null
        $stw = $wrST.Right - $wrST.Left; $sth = $wrST.Bottom - $wrST.Top
        [Win32]::ForceFG($script:hwnd)
        # NO Clear All / Map All. The Xbox slot carries DualSense + Xbox Series X
        # GIP (both gamepad-class), so auto-map ALREADY produced multi-source rows:
        # a collapsed row shows the primary source plus a combine badge ([MAXABS]
        # on triggers), and clicking the row expands it to every source + the
        # Combine dropdown (the old pad-stick-trim confirmed the Guide row was
        # multi-source with no Map All ever run). Map All here is the wrong tool
        # anyway: the Mappings-toolbar "Map All" is the sequential-record TOGGLE
        # (MapAllToggle_Click -> MapAllCommand, gated on an ONLINE selected device),
        # and Clear All wipes the whole grid behind a confirm dialog.
        # Grid output order (pad-mappings): A B X Y LB RB Back Start Guide Share LSB
        # RSB Dpad(4) LT RT LSX LSY RSX RSY. First row ~0.206 H, ~0.025 H/row, so
        # Right Trigger (index 17) sits ~0.632 H. Click it to select+expand the
        # multi-source detail editor (DataGridDetailsPresenter in PadPage.xaml).
        [Win32]::ClickAt([int]($wrST.Left + 0.22 * $stw), [int]($wrST.Top + 0.633 * $sth)); Start-Sleep -Milliseconds 1000
        # The COMBINE dropdown sits in the expanded editor below the selected
        # row. Offsets measured off the HEAD expanded-row reference
        # (wii-balance-sources.jpg): COMBINE combo center = row Y + 0.091 H at
        # 0.2425 W. RT row 0.633 H -> combine 0.724 H. Open it, type-ahead to
        # "Stick Trim", accept.
        [Win32]::ClickAt([int]($wrST.Left + 0.2425 * $stw), [int]($wrST.Top + 0.724 * $sth)); Start-Sleep -Milliseconds 800
        [System.Windows.Forms.SendKeys]::SendWait("Stick"); Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 1000
        Cap "pad-stick-trim"
    } else { Write-Host "  !! Stick Trim: Mappings tab not found" -ForegroundColor Yellow }

    # 8. Sticks (default view with curves and deadzone shapes visible)
    Write-Host "[$(Next)/$total] Sticks"
    Tab "Sticks"; Start-Sleep -Milliseconds 500; Cap "pad-sticks"

    # 9. Sticks: deadzone shape dropdown open (weak-9 recapture). The old
    # absolute pixels (946, 469) predate the v4 Sticks layout and landed on
    # the Center Offset sliders. Window fractions measured off the committed
    # HEAD sticks.jpg: Deadzone Shape combo center 0.3775 W / 0.4746 H.
    Write-Host "[$(Next)/$total] Sticks - deadzone shape dropdown"
    $wrSt = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrSt) | Out-Null
    $stW = $wrSt.Right - $wrSt.Left; $stH = $wrSt.Bottom - $wrSt.Top
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    [Win32]::ClickAt([int]($wrSt.Left + 0.3775 * $stW), [int]($wrSt.Top + 0.4746 * $stH))
    Start-Sleep -Milliseconds 800
    Cap "pad-sticks-deadzone-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 10. Sticks: sensitivity preset dropdown open (weak-9 recapture).
    # Sensitivity X combo center 0.354 W / 0.7635 H on HEAD sticks.jpg.
    Write-Host "[$(Next)/$total] Sticks - sensitivity preset dropdown"
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    [Win32]::ClickAt([int]($wrSt.Left + 0.354 * $stW), [int]($wrSt.Top + 0.7635 * $stH))
    Start-Sleep -Milliseconds 800
    Cap "pad-sticks-sensitivity-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 10a. Boundary calibration (Stick-Deadzones.md): the Range section with the
    # Calibrate Boundary button + circularity readout sits below both curve
    # editors, so scroll the Sticks tab down to reach it. The live measured-edge
    # outline and circularity percent only fill in during a real stick sweep
    # (hardware), so this captures the resting Range controls -- button, reset,
    # and the four cardinal caps below.
    Write-Host "  Sticks: boundary calibration (Range section)"
    ScrollContent -Clicks -18
    Start-Sleep -Milliseconds 400
    Cap "pad-sticks-boundary-calibration"
    ScrollContent -Clicks 18

    # 11. Triggers
    Write-Host "[$(Next)/$total] Triggers"
    Tab "Triggers"; Start-Sleep -Milliseconds 500; Cap "pad-triggers"

    # 12. Triggers: sensitivity preset dropdown open (weak-9 recapture).
    # Left Trigger Preset combo center 0.354 W / 0.4363 H on HEAD triggers.jpg.
    Write-Host "[$(Next)/$total] Triggers - sensitivity preset dropdown"
    $wrTg = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrTg) | Out-Null
    $tgW = $wrTg.Right - $wrTg.Left; $tgH = $wrTg.Bottom - $wrTg.Top
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300
    [Win32]::ClickAt([int]($wrTg.Left + 0.354 * $tgW), [int]($wrTg.Top + 0.4363 * $tgH))
    Start-Sleep -Milliseconds 800
    Cap "pad-triggers-sensitivity-dropdown"
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 300

    # 12a. Live trigger instrument (Trigger-Deadzones.md): the RAW/OUT bars and
    # the quarter-arc travel gauge sit under each trigger's curve editor, so
    # scroll the Triggers tab down to bring the instrument into frame. Bars read
    # zero at rest (no live pull), which still shows the two-stage layout and the
    # arc gauge the page documents.
    Write-Host "  Triggers: live instrument (RAW/OUT + arc)"
    ScrollContent -Clicks -8
    Start-Sleep -Milliseconds 400
    Cap "pad-trigger-instrument"
    ScrollContent -Clicks 8

    # 13. Force Feedback
    Write-Host "[$(Next)/$total] Force Feedback"
    Tab "Force Feedback"; Cap "pad-forcefeedback"

    # 13-0. Live motor activity (Force-Feedback.md): the stacked RAW/OUT bars for
    # the left and right motors sit between Test Rumble and the Audio Rumble /
    # Trigger Routing cards, so a small scroll from the top lands on them. Bars
    # read zero without a game sending rumble; the panel structure and per-motor
    # readouts still show. Scroll back to the top before the Trigger Routing shot
    # so its -12 scroll starts from a known position.
    Write-Host "  Force Feedback: motor activity panel"
    ScrollContent -Clicks -6
    Start-Sleep -Milliseconds 400
    Cap "pad-motor-activity"
    ScrollContent -Clicks 6

    # 13a. Trigger Routing: the Force Feedback tab scrolled down to the Audio
    # Rumble + Trigger Routing cards.
    Write-Host "[$(Next)/$total] Trigger Routing"
    ScrollContent -Clicks -12
    Cap "pad-trigger-routing"
    ScrollContent -Clicks 12

    # 13b. Impulse Triggers: switch the mapped device to the Xbox Series X pad
    # (HasRumbleTriggers) so its Impulse Triggers tab appears on this slot.
    Write-Host "[$(Next)/$total] Impulse Triggers"
    if (Select-MappedDevice "Xbox Series X GIP") {
        if (Tab "Impulse Triggers") { Start-Sleep -Milliseconds 700; Cap "pad-impulse-triggers" }
        else { Write-Host "  !! Impulse Triggers tab not found" -ForegroundColor Yellow }
    }

    # 13c. Wheel: switch to the synthetic G29 so its Wheel tab (rotation range,
    # auto-center, RPM LEDs) appears. Retry the device-combo walk: run 1 missed the
    # G29 in the dropdown on the first pass (flaky UIA combo enumeration right after
    # the Impulse-Triggers device switch).
    Write-Host "[$(Next)/$total] Wheel"
    $wheelSel = $false
    for ($ws = 0; $ws -lt 3 -and -not $wheelSel; $ws++) {
        $wheelSel = Select-MappedDevice "Logitech G29"
        if (-not $wheelSel) { Start-Sleep -Milliseconds 900 }
    }
    if ($wheelSel) {
        if (Tab "Wheel") { Start-Sleep -Milliseconds 700; Cap "pad-wheel" }
        else { Write-Host "  !! Wheel tab not found" -ForegroundColor Yellow }
    }
    # 13d. Guide Button LED (#209): the Xbox pad is XInput-pathed, so the
    # Lighting tab surfaces the Guide LED brightness card (the lightbar
    # card hides for a guide-LED-only device).
    Write-Host "[$(Next)/$total] Guide Button LED"
    if (Select-MappedDevice "Xbox Series X GIP") {
        # One canonical name: the wiki references pad-lighting-guide-led.
        if (Tab "Lighting") { Start-Sleep -Milliseconds 700; Cap "pad-lighting-guide-led" }
        else { Write-Host "  !! Lighting tab not found for the Xbox pad" -ForegroundColor Yellow }
    }

    # 13e. Bass Shakers. This tab had NO capture step at all, while
    # features/bass-shakers.md and features/force-feedback.md both carried a
    # <!-- SCREENSHOT: pad-bass-shakers --> placeholder that has therefore
    # never been filled. It is a SLOT-tier tab (Xbox and PlayStation slots
    # surface it, PadViewModel gates it), so it needs no device switch.
    Write-Host "[$(Next)/$total] Bass Shakers"
    if (Tab "Bass Shakers") { Start-Sleep -Milliseconds 800; Cap "pad-bass-shakers" }
    else { Write-Host "  !! Bass Shakers tab not found" -ForegroundColor Yellow }

    # Return the selection to the DualSense so later navigation is predictable.
    Select-MappedDevice "DualSense" | Out-Null

    # Disconnect Controller action editor (#162), LAST in the Xbox block: selecting
    # a macro leaves the Macros tab in a state where the next tab-switch fails, so
    # it must not precede another Xbox-tab capture (the next block re-navs via
    # Dashboard, resetting the PadPage). The macro ListBox and action list expose
    # no UIA peers (WPF-UI virtualized), so both are clicked by coordinate: the
    # "Sleep Controller" row (3rd in the list, ~0.343 H), then its "Disconnect
    # Controller: Triggering Device" action (~0.59 H) so the editor Border (gated
    # on SelectedAction) renders with the Target dropdown. Fractions are read off
    # the maximized-window screenshot and are resolution-independent.
    Write-Host "  Macro: Disconnect Controller (Target dropdown)"
    Tab "Macros"; Start-Sleep -Milliseconds 900
    $wrMc = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrMc) | Out-Null
    $mw = $wrMc.Right - $wrMc.Left; $mh = $wrMc.Bottom - $wrMc.Top
    [Win32]::ForceFG($script:hwnd)
    # Macro list rows on HEAD macro-disconnect.jpg: first row 0.241 H, 0.0433 H
    # per row, list x 0.215 W. Sleep Controller is row 3 (index 2). The action
    # chips start at 0.654 H (x 0.383 W); the old 0.594 H fraction landed on
    # the Add Action button and quietly appended a stray "Press (none)" action,
    # which is what the committed v4.0.0 macro-disconnect frame shows.
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 2 * 0.0433) * $mh)); Start-Sleep -Milliseconds 800  # Sleep Controller row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.654 * $mh)); Start-Sleep -Milliseconds 800  # its Disconnect action chip
    # Best effort: expand the Target combo (a StackPanel ComboBox in the editor,
    # not an opaque grid cell) so all four target modes show. Capture either way.
    $cbCondM = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $liCondM = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $padPageM = Find-UIA -Aid "PadPageView"
    $searchM = if ($padPageM) { $padPageM } else { $script:uiaWin }
    $targetCombo = $null
    foreach ($cb in $searchM.FindAll($TD, $cbCondM)) {
        $expM = $null
        try { $expM = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { continue }
        try { $expM.Expand(); Start-Sleep -Milliseconds 350 } catch { continue }
        $hasTrig = $false
        foreach ($ci in $cb.FindAll($TD, $liCondM)) { if ($ci.Current.Name -like "*Triggering Device*") { $hasTrig = $true; break } }
        if ($hasTrig) { $targetCombo = $cb; break }
        try { $expM.Collapse(); Start-Sleep -Milliseconds 150 } catch {}
    }
    if ($targetCombo) { Write-Host "  Expanded Disconnect Target dropdown" -ForegroundColor Green }
    else { Write-Host "  Target combo not UIA-visible; capturing editor with Target field as-is" -ForegroundColor Yellow }
    if ($script:MacrosPresent) { Cap "macro-disconnect" } else { Write-Host "  skipped macro-disconnect (no macros)" -ForegroundColor Yellow }
    if ($targetCombo) { try { $targetCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {} }

    # New #9 macro editors, same terminal-macro-zone idiom and the same
    # trigger-block height (2 chips) as Sleep Controller, so the action chip
    # sits at the same 0.654 H. Selecting another macro row inside the Macros
    # tab is safe; it is the NEXT TAB SWITCH that macro selection breaks, and
    # the following section re-navs via Dashboard.
    Write-Host "  Macro: Move Mouse to Position editor (#9)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 3 * 0.0433) * $mh)); Start-Sleep -Milliseconds 800  # Center Cursor row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.654 * $mh)); Start-Sleep -Milliseconds 900  # its MoveMouse action chip
    if ($script:MacrosPresent) { Cap "macro-move-mouse" } else { Write-Host "  skipped macro-move-mouse (no macros)" -ForegroundColor Yellow }

    Write-Host "  Macro: Repeat Key While Held editor (#9)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 4 * 0.0433) * $mh)); Start-Sleep -Milliseconds 800  # Rapid Fire row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.654 * $mh)); Start-Sleep -Milliseconds 900  # its RepeatKey action chip
    if ($script:MacrosPresent) { Cap "macro-repeat-key" } else { Write-Host "  skipped macro-repeat-key (no macros)" -ForegroundColor Yellow }

    # Pressure-sensitive turbo (#290, 4.3.0). The Rate Curve row and the
    # analog-source picker that feeds it sit below the Repeat block this
    # editor already frames, so scroll the open editor rather than opening
    # a different macro.
    Write-Host "  Macro: pressure-sensitive turbo (#290)"
    ScrollContent -Clicks -8
    Start-Sleep -Milliseconds 400
    if ($script:MacrosPresent) { Cap "macro-turbo" } else { Write-Host "  !! skipped macro-turbo (no macros)" -ForegroundColor Red }
    ScrollContent -Clicks 8

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
    if ((Get-Count $cards) -ge 2) {
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
            if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "PS Controller Tab" -Delay 1000 | Out-Null }
            $tabs = $padPage.FindAll($TC, $rbCond)
            Write-Host "  PadPage tabs visible to UIA: $((Get-Count $tabs)) (AT visible: $atVisible)"
            for ($ti = 0; $ti -lt (Get-Count $tabs); $ti++) {
                Write-Host "    [$ti] Name='$($tabs[$ti].Current.Name)'"
            }

            # The PlayStation controller-page screenshot must ALWAYS show a
            # DualSense, the convention since the page's introduction, not the
            # slot's default DualShock 4 preset. Switch the preset combo
            # (HMaestroProfileCombo) to the DualSense profile so the 3D model and
            # the preset row read DualSense. It also keeps the Adaptive Triggers /
            # Lighting / Gyro / Touchpad shots that follow on a DualSense.
            $psPreset = Find-UIA -Parent $padPage -Aid "HMaestroProfileCombo"
            if ($psPreset) {
                try {
                    $expPS = $psPreset.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    $expPS.Expand(); Start-Sleep -Milliseconds 600
                    $liCPS = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::ListItem)
                    $dsItem = $null
                    foreach ($it in $psPreset.FindAll($TD, $liCPS)) {
                        $nm = $it.Current.Name
                        if ($nm -like "*DualSense*" -and $nm -notlike "*Edge*") { $dsItem = $it; break }
                    }
                    if ($dsItem) {
                        try { $dsItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
                        catch { Click-El $dsItem -Label "DualSense preset" | Out-Null }
                        Write-Host "  PS preset -> $($dsItem.Current.Name)" -ForegroundColor Green
                        # Collapse the dropdown so it does not cover the controller model.
                        try { $expPS.Collapse() } catch {}
                        Start-Sleep -Milliseconds 1800
                    } else {
                        Write-Host "  !! DualSense preset not found in HMaestroProfileCombo" -ForegroundColor Yellow
                        try { $expPS.Collapse() } catch {}
                    }
                } catch { Write-Host "  !! PS preset switch failed: $_" -ForegroundColor Yellow }
            } else {
                Write-Host "  !! HMaestroProfileCombo not found on PS slot" -ForegroundColor Yellow
            }

            # PlayStation slot Controller tab (DualSense 3D model + preset row).
            Write-Host "[$(Next)/$total] PlayStation config bar / Controller view"
            Start-Sleep -Milliseconds 500
            Cap "pad-playstation-configbar"

            # 2D touchpad finger dots (2D-Overlay-System.md): the DualSense 2D
            # preview shows the touchpad. The orange/blue finger dots only render
            # under live touch (hardware), so this captures the resting 2D preview
            # with the touchpad in frame. Toggle to 2D via the view-mode button
            # (top-left of the model host, same offset the Xbox 2D shot uses),
            # capture, toggle back to 3D so the AT/Lighting shots stay on 3D.
            Write-Host "  PlayStation: 2D touchpad preview"
            Toggle-ViewMode | Out-Null
            Start-Sleep -Milliseconds 700
            Cap "2d-touchpad-finger-dots"
            Toggle-ViewMode | Out-Null
            Start-Sleep -Milliseconds 500

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

                    # 4.3.0 additions live below the fold on two of these tabs.
                    # Gyro Tilt (#292) is an envelope card under the existing
                    # gyro rows, and the touchpad Momentum knobs (#291) sit in
                    # the mouse-output area. Both need a scroll, and both are
                    # captured from the same tab visit rather than a second one.
                    if ($gt.Name -eq "Gyro") {
                        Write-Host "  Gyro: tilt envelope card (#292)"
                        ScrollContent -Clicks -14
                        Start-Sleep -Milliseconds 400
                        Cap "pad-gyro-tilt"
                        ScrollContent -Clicks 14
                    }
                    elseif ($gt.Name -eq "Touchpad") {
                        Write-Host "  Touchpad: momentum knobs (#291)"
                        ScrollContent -Clicks -10
                        Start-Sleep -Milliseconds 400
                        Cap "pad-touchpad-momentum"
                        ScrollContent -Clicks 10
                    }
                    elseif ($gt.Name -eq "Audio") {
                        # 4.3.2: the DSP chain (#347) sits under Output Path,
                        # crossfeed picker then the graphic EQ curve and rows
                        # then the limiter. Seed-AudioDsp wrote the EQ on so
                        # the curve and rows render rather than an empty box.
                        Write-Host "  Audio: DSP chain, crossfeed + graphic EQ + limiter (#347)"
                        ScrollContent -Clicks -16
                        Start-Sleep -Milliseconds 500
                        Cap "pad-audio-dsp"
                        ScrollContent -Clicks 16
                    }
                } else {
                    Write-Host "  !! $($gt.Name) tab not in UIA tree -- SKIPPED $($gt.File)" -ForegroundColor Red
                }
            }

            # Custom gesture recorder dialog (Touchpad.md): the "+ Record New
            # Gesture" button in the Touchpad tab's Custom Gestures section opens
            # a modal recorder. The section is near the bottom of the tab, so land
            # on Touchpad, scroll down, click the button, capture the dialog, then
            # close it (a low Close/Cancel button, else ESC), like the NFC/pair
            # modals. Done LAST on the PS slot since it opens a modal.
            Write-Host "  PlayStation: custom gesture recorder"
            $tpTab = ($padPage.FindAll($TC, $rbCond)) | Where-Object { $_.Current.Name -eq "Touchpad" } | Select-Object -First 1
            if ($tpTab) { Click-El $tpTab -Label "Touchpad Tab (recorder)" -Delay 800 | Out-Null }
            Start-Sleep -Milliseconds 600
            # Enable gestures FIRST. The Custom Gestures section (the StackPanel holding
            # the "+ Record New Gesture" button) is IsEnabled-bound to
            # TouchpadCustomSectionEnabled = _touchpadGesturesEnabled && mode != InBoxOnly
            # (PadViewModel.Touchpad.cs). With gestures off (the default) that section is
            # DISABLED and the record button is unclickable. UIA Name lookups fail for the
            # touchpad-tab content controls (the 2026-07-12 run could not find the
            # "Enable Gestures on This Touchpad" checkbox OR the record button by Name), so
            # drive by COORDINATE off the pad-touchpad geometry: on the fresh (unscrolled)
            # Touchpad tab the checkbox/label sits at ~0.19 W, 0.807 H of the window.
            $wrTp = New-Object Win32+RECT
            [Win32]::GetWindowRect($script:hwnd, [ref]$wrTp) | Out-Null
            $tpw = $wrTp.Right - $wrTp.Left; $tph = $wrTp.Bottom - $wrTp.Top
            [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
            [Win32]::ClickAt([int]($wrTp.Left + 0.19 * $tpw), [int]($wrTp.Top + 0.807 * $tph)); Start-Sleep -Milliseconds 700
            # Scroll to the bottom so the Custom Gestures card realizes on-screen, then find
            # the record button by POSITION: its Name isn't matchable, but after
            # scroll-to-bottom the empty CustomTouchpadGestures ItemsControl leaves the
            # record button as the BOTTOM-MOST content button (X past the ~280px sidebar,
            # Y below the tab strip). Name-match still wins if it ever resolves.
            ScrollContent -Clicks -90
            $recBtnCT = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)
            $recBtn = $null; $recByName = $null; $bestY = -1e9
            foreach ($b in $script:uiaWin.FindAll($TD, $recBtnCT)) {
                $rb = Get-Rect $b
                if ($null -eq $rb) { continue }
                if ($b.Current.Name -match "Record New Gesture") { $recByName = $b }
                if ($rb.X -gt ($wrTp.Left + 0.14 * $tpw) -and $rb.Y -gt ($wrTp.Top + 0.16 * $tph)) {
                    $by = $rb.Y + $rb.Height
                    if ($by -gt $bestY) { $bestY = $by; $recBtn = $b }
                }
            }
            if ($recByName) { $recBtn = $recByName; Write-Host "  recorder: found record button by Name" }
            elseif ($recBtn) {
                $rbR = Get-Rect $recBtn
                if ($null -ne $rbR) { Write-Host ("  recorder: bottom-most content button '{0}' [{1}x{2}] at ({3},{4})" -f $recBtn.Current.Name, [int]$rbR.Width, [int]$rbR.Height, [int]$rbR.X, [int]$rbR.Y) }
            }
            if ($recBtn) {
                Click-El $recBtn -Label "Record New Gesture" -Delay 1500 | Out-Null
                # Recorder is a wpf-ui FluentWindow modal; grab its hwnd at open time.
                # Only capture if a modal actually opened (a wrong button = no modal = skip,
                # so we never save a non-recorder frame as touchpad-gesture-recorder.png).
                $recDlg = Get-ForegroundDialogHwnd
                if ($recDlg -ne [IntPtr]::Zero) {
                    Cap "touchpad-gesture-recorder" -AllowModal
                    Close-DialogHwnd $recDlg
                } else {
                    Write-Host "  !! recorder dialog did not open (no foreground modal)" -ForegroundColor Yellow
                }
                Close-AnyModal | Out-Null
            } else {
                Write-Host "  !! Record New Gesture button not found" -ForegroundColor Yellow
            }
            ScrollContent -Clicks 140
            Close-AnyModal | Out-Null
        } else {
            Write-Host "  !! PadPageView not found after PS slot click" -ForegroundColor Yellow
            $n += 6   # PS block advances Next() six times (Gyro/Audio/Touchpad loop is three)
        }
    } else {
        Write-Host "  !! Only $((Get-Count $cards)) slot cards on Dashboard" -ForegroundColor Yellow
        $n += 6
    }
} else {
    Write-Host "  !! SlotsItemsControl not found" -ForegroundColor Yellow
    $n += 6
}

# ---- 13c. Nintendo slot (virtual Switch Pro, #246, 4.1.0) ----
# Card index 2 after the type-group reorder (Xbox 0, PlayStation 1,
# Nintendo 2). One shot: the Switch Pro config bar + controller view.
Write-Host ""
Write-Host "--- Nintendo Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
if ((Get-Count $cards) -gt 2) {
    Write-Host "[$(Next)/$total] Nintendo config bar"
    Click-El $cards[2] -Label "Nintendo Slot card" -Delay 4000 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "Nintendo Controller Tab" -Delay 2500 | Out-Null }
        Cap "pad-nintendo-configbar"
    } else { Write-Host "  !! PadPageView not found for the Nintendo slot" -ForegroundColor Yellow }
} else { Write-Host "  !! Nintendo slot card not found" -ForegroundColor Yellow }

# ---- 14. Extended slot ----
# Use Dashboard slot cards (SlotsItemsControl) instead of sidebar nav.
# Sidebar NavigationViewItems virtualize out of the UIA tree after the
# Xbox-slot tab pass, but Dashboard cards stay materialized.
Write-Host ""
Write-Host "--- Extended Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
# Positive index, NOT from-end. The dashboard SlotsItemsControl carries a
# trailing "Add Controller" card. Slots are always Xbox 0, PlayStation 1,
# Nintendo 2, Extended 3, KBM 4, MIDI 5 after the type-group reorder, then
# the Add card at 6.
$extendedIdx = 3
if ((Get-Count $cards) -gt $extendedIdx) {
    Write-Host "[$(Next)/$total] Extended config bar"
    Click-El $cards[$extendedIdx] -Label "Extended Slot card" -Delay 1500 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "Extended Controller Tab" -Delay 1000 }

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
    Toggle-ViewMode | Out-Null
    Start-Sleep -Milliseconds 600
    Cap "pad-extended-schematic"
    # Switch back
    Toggle-ViewMode | Out-Null
    Start-Sleep -Milliseconds 500

    # 15a. Clone Device 1:1 confirm dialog (Button-and-Axis-Mappings.md). The
    # Extended config bar's "Clone Device 1:1" button opens a modal MessageBox
    # listing the resulting axis/button/POV counts. The Extended slot carries the
    # Wii Remote, so select it, click Clone, capture the confirm dialog, then
    # CANCEL -- never the primary "Clone" button, which would rewrite the slot's
    # mapping. Modal, so done last in the Extended block.
    Write-Host "  Extended: Clone Device 1:1 confirm dialog"
    Select-MappedDevice "Wii Remote" | Out-Null
    Start-Sleep -Milliseconds 500
    $cloneBtn = Find-UIA -Name "Clone Device 1:1" -CT ([System.Windows.Automation.ControlType]::Button)
    if (-not $cloneBtn) { $cloneBtn = Find-UIA -Name "Clone Device 1:1" }
    if ($cloneBtn) {
        Click-El $cloneBtn -Label "Clone Device 1:1" -Delay 1400 | Out-Null
        Cap "pad-extended-clone-device" -AllowModal
        # The confirm is a SEPARATE top-level window, so the old $script:uiaWin
        # button scan never found its Cancel and ESC did not reach it. Run 1 froze
        # right after this because the modal stayed up and the KBM block's next
        # Descendants walk hung on it. Close-AnyModal dismisses it by title-scoped
        # Cancel/Close (never the primary "Clone").
        Close-AnyModal | Out-Null
    } else {
        Write-Host "  !! Clone Device 1:1 button not found" -ForegroundColor Yellow
    }
    # Belt-and-suspenders: guarantee no modal survives this block before the next
    # slot section's Descendants walk.
    Close-AnyModal | Out-Null
} else {
    Write-Host "  !! Extended slot not found" -ForegroundColor Yellow
    $n += 2
}

# ---- 16. KBM slot ----
Write-Host ""
Write-Host "--- KBM Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
# Retry the card enumeration. A single FindAll that comes back short is a UIA
# hiccup, not a missing slot, and this block used to skip on it with NO else
# branch: no shot, no warning, nothing in the log at all. That is how
# pad-mouse-gestures went missing entirely while the run reported success, and
# how screenshot-mouse-gestures.jpg stayed at its July version on the site.
$kbmIdx = 4  # Xbox 0, PlayStation 1, Nintendo 2, Extended 3, KBM 4 (Add card at 6);
             # old Count-2 landed on the MIDI slot (pad-kbm-preview showed MIDI).
$cards = @()
for ($kbmTry = 1; $kbmTry -le 4; $kbmTry++) {
    $slotsHost = Find-UIA -Aid "SlotsItemsControl"
    $cards = if ($slotsHost) { @($slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
    if ((Get-Count $cards) -gt $kbmIdx) { break }
    Write-Host "  KBM card scan attempt ${kbmTry}: only $(Get-Count $cards) cards, retrying" -ForegroundColor DarkGray
    if ($kbmTry -eq 2) {
        # Two empty scans is not a slow tree, it is a stale one. Restart.
        Reset-PadForgeUia -ExePath $PadForgeExe | Out-Null
    }
    Nav "Dashboard"; Start-Sleep -Milliseconds 1400
}
if ((Get-Count $cards) -le $kbmIdx) {
    Write-Host "  !! KBM BLOCK SKIPPED: $(Get-Count $cards) slot cards, needed more than $kbmIdx." -ForegroundColor Red
    Write-Host "  !! pad-kbm-preview, pad-kbm-socd and pad-mouse-gestures are NOT captured." -ForegroundColor Red
}
if ((Get-Count $cards) -gt $kbmIdx) {
    Write-Host "[$(Next)/$total] Keyboard+Mouse preview"
    Click-El $cards[$kbmIdx] -Label "KBM Slot card" -Delay 1500 | Out-Null
    # KBM defaults to Controller tab (keyboard+mouse preview) — no need to click a tab
    Start-Sleep -Milliseconds 800
    Cap "pad-kbm-preview"

    # SOCD cleaning (#205): the Snap Tap card sits below the KBM preview.
    Write-Host "[$(Next)/$total] KBM SOCD"
    ScrollContent -Clicks -12
    Start-Sleep -Milliseconds 400
    Cap "pad-kbm-socd"
    ScrollContent -Clicks 12

    # Stick trackball momentum (#291, 4.3.0). The row is Visibility-bound to
    # IsMouseStick, so it exists ONLY on the KBM slot's mouse stick (stick 0)
    # and never on a gamepad slot. An earlier version scrolled a PlayStation
    # slot's Sticks tab looking for it and framed the Range section instead.
    Write-Host "[$(Next)/$total] KBM stick momentum (trackball)"
    if (Tab "Sticks") {
        Start-Sleep -Milliseconds 700
        ScrollContent -Clicks -10
        Start-Sleep -Milliseconds 400
        Cap "pad-sticks-momentum"
        ScrollContent -Clicks 10
    } else {
        Write-Host "  !! Sticks tab not found on KBM slot -- SKIPPED pad-sticks-momentum" -ForegroundColor Red
    }

    # Mouse gestures (#200): hold a mouse button, flick, an action fires. The
    # gesture card lives on the Mouse tab, which gates on the SELECTED device being
    # a mouse (IsMouse). Select the mouse assigned to this KBM slot first, else the
    # tab stays hidden (SelectedMappedDevice null -> hasMouse false).
    Write-Host "[$(Next)/$total] Mouse gestures"
    Select-MappedDevice "All Mice (Merged)" | Out-Null
    Start-Sleep -Milliseconds 500
    if (Tab "Mouse") {
        Start-Sleep -Milliseconds 700
        ScrollContent -Clicks -10
        Start-Sleep -Milliseconds 300
        Cap "pad-mouse-gestures"
    } else { Write-Host "  !! Mouse tab not found on the KBM slot" -ForegroundColor Yellow }
} else {
    Write-Host "  !! KBM slot not found" -ForegroundColor Yellow
    $n += 3   # preview + SOCD + mouse gestures
}

# ---- 17. MIDI slot ----
Write-Host ""
Write-Host "--- MIDI Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
$midiIdx = 5  # Xbox 0, PlayStation 1, Nintendo 2, Extended 3, KBM 4, MIDI 5 (Add card at 6);
              # old Count-1 landed on the Add Controller card (opened the type picker).
if ((Get-Count $cards) -gt $midiIdx) {
    Write-Host "[$(Next)/$total] MIDI config bar"
    Click-El $cards[$midiIdx] -Label "MIDI Slot card" -Delay 1500 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if ($padPage) {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "MIDI Controller Tab" -Delay 1000 }
    }
    Cap "pad-midi-configbar"
} else {
    Write-Host "  !! MIDI slot not found" -ForegroundColor Yellow
    $n++
}

# ---- 17b. VR slot (#49, 4.2.0) ----
# Card index 6 by VirtualControllerGroups.InOrder (Xbox 0 .. MIDI 5, VR 6,
# Add card at 7). Slot creation now ABORTS the run when the VR type fails,
# so reaching here without a VR card means the card list is short for some
# OTHER reason and the index would land on the Add Controller card. That
# is not a harmless miss: on 2026-08-19 it opened the type-picker popup
# and shipped the Dashboard-with-popup as BOTH pad-vr-configbar.png and
# pad-vr-mappings.png. Verify we are on a pad page before capturing.
Write-Host ""
Write-Host "--- VR Slot ---" -ForegroundColor Yellow
Nav "Dashboard"; Start-Sleep -Milliseconds 1000
$slotsHost = Find-UIA -Aid "SlotsItemsControl"
$cards = if ($slotsHost) { $slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition) } else { @() }
$vrIdx = 6
if ((Get-Count $cards) -gt $vrIdx) {
    Write-Host "[$(Next)/$total] VR preview + config bar + mappings"
    Click-El $cards[$vrIdx] -Label "VR Slot card" -Delay 1500 | Out-Null
    $padPage = Find-UIA -Aid "PadPageView"
    if (-not $padPage) {
        # The click did not land on a slot. Capturing here would ship the
        # Dashboard under three VR names.
        Write-Host "  !! VR card click did not open a pad page -- SKIPPED all three VR shots" -ForegroundColor Red
        $n++
    } else {
        $rbCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabs = $padPage.FindAll($TC, $rbCond)
        if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "VR Controller Tab" -Delay 1200 }
        # The Controller tab renders the hand-controller preview. It is the
        # lead image of features/vr-controllers.md and NOTHING in this
        # harness had ever captured it: the converter mapped
        # pad-vr-preview -> vr-preview while no Cap ever wrote the source,
        # so the docs page carried whatever hand-made file predated the
        # script, and every coverage audit listed it STALE forever.
        Cap "pad-vr-preview"
        Cap "pad-vr-configbar"
        # The Mappings tab shows the VR source family (both hands' sticks,
        # triggers, grips and clicks) that the preview mirrors.
        if (Tab "Mappings") {
            Start-Sleep -Milliseconds 800
            Cap "pad-vr-mappings"
        } else {
            Write-Host "  !! VR Mappings tab not found -- SKIPPED pad-vr-mappings" -ForegroundColor Red
        }
    }
} else {
    Write-Host "  !! VR slot card missing at index $vrIdx (cards=$((Get-Count $cards)))" -ForegroundColor Red
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

# 18b. Battery Alerts (#293, 4.3.0). Card order on this page is Language,
# Appearance, Input Engine, Window, Battery Alerts, HidHide, HIDMaestro,
# MIDI Services, SteamVR, Community Configs, Settings File, Diagnostics,
# so the alerts card sits just above the HidHide view the next shot frames.
Write-Host "[$(Next)/$total] Settings - battery alerts"
ScrollContent -Clicks -8
Cap "settings-battery-alerts"

# 19. Settings mid (HidHide whitelist area)
Write-Host "[$(Next)/$total] Settings - HidHide / input engine"
ScrollContent -Clicks -2
Cap "settings-hidhide"

# 20. Settings bottom (drivers)
Write-Host "[$(Next)/$total] Settings - drivers"
ScrollContent -Clicks -20
Cap "settings-drivers"
# Same drivers view under two more placeholder names: Driver-Management.md's
# flame indicators and Installation.md's HIDMaestro / HidHide / MIDI cards both
# document this section.
Cap "driver-status-flames"
Cap "settings-driver-cards"

# 20b. SteamVR card (#49, 4.2.0). Card order on this page is Language,
# Appearance, Input Engine, Window, HidHide, HIDMaestro, MIDI Services,
# SteamVR, Community Configs, Settings File, Diagnostics, so SteamVR sits
# just below the driver trio the previous shot framed. The card shows the
# install-location row only while SteamVR is ABSENT and the Uninstall
# button only for a PadForge-owned install, so what this captures depends
# on the machine's state; both are honest.
Write-Host "[$(Next)/$total] Settings - SteamVR"
ScrollContent -Clicks -5
Cap "settings-steamvr"

# Scroll back up
ScrollContent -Clicks 45

# ---- 21. About ----
Write-Host "[$(Next)/$total] About"
Nav "About"; Cap "about"

# ---- 22. Add Controller popup (already captured in Step 2b) ----
Write-Host "[$(Next)/$total] Add Controller popup -- already captured in Step 2b"
}

# ==============================================================================
# STEP 3b: New 3.6.0 sections (Pointer tab, NFC, Consumer Control, Power/battery)
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3b: 3.6.0 new sections ===" -ForegroundColor Cyan

Start-Sleep -Milliseconds 1100


# Robust device selection on the Devices page: the card list is vertically
# virtualized, so a target lower than the ~12 realized rows must be scrolled
# into view first. Scroll the card list (left half of the window) to the top,
# then step down, searching the realized rows after each step. ScrollAt sign
# follows ScrollContent: positive = up, negative = down.

if ($SkipToTail) {
    # Tail mode: the middle pass is skipped. The xml already carries the
    # full run's assignments, except the Wii Remote one that failed in the
    # aborted run. Make it on the fresh UIA tree (idempotent: the toggle
    # short-circuits when already on).
    Nav "Devices"; Start-Sleep -Milliseconds 1200
    Assign-DeviceToSlot -DeviceNamePart "Wii Remote" -SlotNumberLabel "4" | Out-Null
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
# Cap at 6 real slots: the 7th card is "Add Controller" and clicking it opens the
# type-picker popup. The Wii Remote (now injected) rides the Extended slot (card
# index 3), so the probe finds the Pointer tab well before the Add card anyway.
$realSlots = [math]::Min($cardCountP, 6)
for ($ci = 0; $ci -lt $realSlots -and -not $ptrDone; $ci++) {
    # Re-navigate to the Dashboard and re-find the cards EACH iteration. After
    # a card click we're on the PadPage, so a stale card reference clicks a
    # PadPage coordinate, not a Dashboard card -- which left every probe stuck
    # on the first slot. Every working slot section re-nav's Dashboard first.
    Nav "Dashboard"; Start-Sleep -Milliseconds 900
    $sh = Find-UIA -Aid "SlotsItemsControl"
    # Same StrictMode trap as the rect reads: a FindAll that returns nothing
    # usable leaves a value whose .Count read is a TERMINATING error, and this
    # one killed the 19:46 run before STEP 4, so the owner's settings were left
    # as the capture file. Count defensively and treat a failure as zero cards.
    $cds = @()
    if ($sh) { try { $cds = @($sh.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cds = @() } }
    $cdsCount = 0
    try { $cdsCount = ([object[]]$cds).Length } catch { $cdsCount = 0 }
    if ($ci -ge $cdsCount) { continue }
    Click-El $cds[$ci] -Label "slot card $ci (Pointer probe)" -Delay 1500 | Out-Null
    # Land on the Controller tab first so the PadPage realizes, then poll for
    # the Pointer tab to flip visible (Wii's HasIrCamera gate propagates a
    # few seconds after slot bind, like the PS-slot AT/Lighting gating).
    $padPageP = Find-UIA -Aid "PadPageView"
    if (-not $padPageP) { continue }
    # The Extended slot now carries several Wii-family devices; the Pointer tab
    # gates on HasIrCamera, which only the Wii Remote has. Explicitly select it so
    # a non-Remote default selection can't hide the tab (no-op on other slots).
    Select-MappedDevice "Wii Remote" | Out-Null
    $tabsP = $padPageP.FindAll($TC, $rbCondP)
    if ((Get-Count $tabsP) -gt 0) { Click-El $tabsP[0] -Label "Controller Tab (Pointer probe)" -Delay 800 | Out-Null }
    $ptrVisible = $false
    for ($w = 0; $w -lt 6 -and -not $ptrVisible; $w++) {
        Start-Sleep -Milliseconds 800
        $tabsP = $padPageP.FindAll($TC, $rbCondP)
        if ($tabsP | Where-Object { $_.Current.Name -eq "Pointer" }) { $ptrVisible = $true }
    }
    Write-Host ("    card $ci tabs: " + (($tabsP | ForEach-Object { $_.Current.Name }) -join ', '))
    if ($ptrVisible -and (Tab "Pointer")) {
        Start-Sleep -Milliseconds 800; Cap "pad-pointer"; $ptrDone = $true
        # Pointer Mode card set to FPS Mouse (Wii-Controllers.md): switch the
        # Pointer Mode dropdown to "FPS Mouse" so the card shows the mode combo
        # plus the FPS Speed slider (the slider is FpsMouse-only).
        #
        # CORRECTED 2026-07-30: this combo has NO UIA PEER. Only the config
        # bar's DEVICE and Preset combos expose peers; every combo inside the
        # IR Pointer card is stripped from the tree with its card template.
        # The loop below therefore finds nothing to expand and silently skips
        # the shot. What works: CLICK the combo's measured position (window-
        # relative 631, 721 on the maximized 2582x1550 window), after which
        # the popup's ListItem peers DO realize at the window root and
        # "FPS Mouse" can be selected normally.
        $liP = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)
        $cbP = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ComboBox)
        $pmDone = $false
        foreach ($cb in $padPageP.FindAll($TD, $cbP)) {
            $expP = $null
            try { $expP = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } catch { continue }
            try { $expP.Expand(); Start-Sleep -Milliseconds 500 } catch { continue }
            $fps = $null
            foreach ($it in $cb.FindAll($TD, $liP)) { if ($it.Current.Name -eq "FPS Mouse") { $fps = $it; break } }
            # WPF ComboBox popups can realize their item peers at the window root
            # rather than under the combo, so search the whole window too.
            if (-not $fps) { foreach ($it in $script:uiaWin.FindAll($TD, $liP)) { if ($it.Current.Name -eq "FPS Mouse") { $fps = $it; break } } }
            if ($fps) {
                try { $fps.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
                catch { Click-El $fps -Label "FPS Mouse" | Out-Null }
                Start-Sleep -Milliseconds 700
                try { $expP.Collapse() } catch {}
                $pmDone = $true; break
            }
            try { $expP.Collapse(); Start-Sleep -Milliseconds 150 } catch {}
        }
        if (-not $pmDone) {
            # Coordinate + keyboard fallback (the 2026-07-12 run's UIA enumeration found
            # no "FPS Mouse" item). The Pointer Mode card is fully visible on the
            # Extended-slot Wii-Remote Pointer tab: combo center ~0.232 W, 0.466 H of the
            # window. Mouse is item 0 and is the current selection, so opening the combo
            # and one {DOWN} lands on FPS Mouse (item 1); {ENTER} commits and the
            # FpsMouse-only FPS Speed slider appears.
            Write-Host "  Pointer Mode: UIA combo search failed; coordinate fallback" -ForegroundColor Yellow
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300
            $wrPm = New-Object Win32+RECT
            [Win32]::GetWindowRect($script:hwnd, [ref]$wrPm) | Out-Null
            $pmw = $wrPm.Right - $wrPm.Left; $pmh = $wrPm.Bottom - $wrPm.Top
            [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
            [Win32]::ClickAt([int]($wrPm.Left + 0.232 * $pmw), [int]($wrPm.Top + 0.466 * $pmh)); Start-Sleep -Milliseconds 700
            [System.Windows.Forms.SendKeys]::SendWait("{DOWN}"); Start-Sleep -Milliseconds 400
            [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 700
            $pmDone = $true
        }
        if ($pmDone) { Cap "wii-pointer-mode" }
        else { Write-Host "  !! Pointer Mode combo (FPS Mouse) not found" -ForegroundColor Yellow }
    }
}
if (-not $ptrDone) { Write-Host "  !! Pointer tab not reachable" -ForegroundColor Yellow }

# --- Mapping source picker: Wii-family gated sources (#146/#151/#154) ---
# Each Wii device is swapped onto the XBOX slot (SlotNumber 1) ALONE so its
# mapping grid is single-source. The Xbox slot's normal captures are already done
# by now, so clearing it is safe; the config is restored from backup at the end.
# The grid Source combos expose no UIA peers, so Capture-SourcePicker opens the
# first row's combo by coordinate and type-aheads to the gated source.
Write-Host "[3b] Mapping source picker (Wii-family + abstract Gamepad sources)"
Nav "Devices"; Start-Sleep -Milliseconds 800
# Slim slot 1 down to the DualSense alone. The DualSense INTENTIONALLY stays
# assigned: it anchors every mapping row's PRIMARY source, so each swap-on
# picker device contributes exactly one sub-source per row and the expanded-row
# sub-source combo lands at the fixed 0.385 H Capture-SourcePicker clicks
# (the geometry of the accepted v4.0.0 wii-balance-sources frame).
Assign-DeviceToSlot -DeviceNamePart "Xbox Series X GIP" -SlotNumberLabel "1" -Unassign | Out-Null
Assign-DeviceToSlot -DeviceNamePart "Logitech G29"      -SlotNumberLabel "1" -Unassign | Out-Null
$wiiPick = @(
    @{ Dev = "Balance Board";    Type = "Balance";      Shot = "wii-balance-sources" },
    @{ Dev = "Joy-Con (R)";      Type = "IR Bright";    Shot = "joycon-ir-source" },
    @{ Dev = "Switch 2 Joy-Con"; Type = "Mouse Motion"; Shot = "joycon2-mouse-sources" },
    # Abstract Gamepad descriptor branch (#9): any CapType-Gamepad device's
    # source combo carries the "Gamepad ..." family; type-ahead scrolls the
    # open popup to it. The DS3 dummy is the swap-on device here.
    @{ Dev = "PLAYSTATION(R)3";  Type = "Gamepad";      Shot = "gamepad-source-picker" }
)
foreach ($wp in $wiiPick) {
    Nav "Devices"; Start-Sleep -Milliseconds 600
    # Skip the whole picker on a failed assign. The 2026-07-30 run hung
    # inside the follow-up Unassign of a device the assign never found
    # (UIA FindAll blocked with no bound), and the rest of the tail
    # (DS3, devices details, workshop, web) never ran.
    $wpOk = Assign-DeviceToSlot -DeviceNamePart $wp.Dev -SlotNumberLabel "1"
    if (-not $wpOk) {
        Write-Host "  !! skipping picker for $($wp.Dev): assign failed" -ForegroundColor Yellow
        continue
    }
    Start-Sleep -Milliseconds 800
    Nav "Dashboard"; Start-Sleep -Milliseconds 900
    $shS = Find-UIA -Aid "SlotsItemsControl"
    $cdS = if ($shS) { @($shS.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
    if ((Get-Count $cdS) -ge 1) {
        Click-El $cdS[0] -Label "Xbox card (picker $($wp.Dev))" -Delay 1500 | Out-Null
        Capture-SourcePicker -DeviceNamePart $wp.Dev -TypeAhead $wp.Type -ShotName $wp.Shot
    } else {
        Write-Host "  !! Xbox slot card not found for $($wp.Dev)" -ForegroundColor Yellow
    }
    Nav "Devices"; Start-Sleep -Milliseconds 600
    Assign-DeviceToSlot -DeviceNamePart $wp.Dev -SlotNumberLabel "1" -Unassign | Out-Null
}

# --- DualShock 3 (v4): motion tab + Devices dossier ---
# The DS3 rides slot 1 ALONE for these shots, so unassign the anchoring
# DualSense first (the Gyro tab and Devices dossier follow the selected
# device either way, but a solo slot makes the selection deterministic).
Write-Host "[3c] DualShock 3"
Nav "Devices"; Start-Sleep -Milliseconds 600
Assign-DeviceToSlot -DeviceNamePart "DualSense" -SlotNumberLabel "1" -Unassign | Out-Null
Assign-DeviceToSlot -DeviceNamePart "PLAYSTATION(R)3" -SlotNumberLabel "1" | Out-Null
Start-Sleep -Milliseconds 800
Nav "Dashboard"; Start-Sleep -Milliseconds 900
$shD = Find-UIA -Aid "SlotsItemsControl"
$cdD = if ($shD) { @($shD.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
if ((Get-Count $cdD) -ge 1) {
    Click-El $cdD[0] -Label "Xbox card (DS3)" -Delay 1500 | Out-Null
    Select-MappedDevice "PLAYSTATION(R)3" | Out-Null
    if (Tab "Gyro") { Start-Sleep -Milliseconds 700; Cap "pad-ds3-gyro" }
    else { Write-Host "  !! Gyro tab not found for the DS3" -ForegroundColor Yellow }
} else { Write-Host "  !! slot card not found for the DS3" -ForegroundColor Yellow }
Nav "Devices"; Start-Sleep -Milliseconds 700
if (Select-DeviceByName36 "PLAYSTATION(R)3") {
    Cap "devices-ds3"
    # Device Dossier card (Devices.md): the DS3 dossier is a rich example --
    # bridged Bluetooth PATH plus LINK/SERIAL rows. It sits at the top of the
    # detail pane, in frame with the selection, so capture it here.
    Cap "devices-dossier"
}
Assign-DeviceToSlot -DeviceNamePart "PLAYSTATION(R)3" -SlotNumberLabel "1" -Unassign | Out-Null

# --- Haptic-tone audio controls (Controller-Audio.md) ---
# The "Play mirrored audio" + "High tones" groups render on the Audio tab only
# for haptic-actuator pads (DeviceHasHaptics: Joy-Con / Pro / Steam / Deck /
# SC2026). The DualSense doesn't qualify, so its Audio tab (pad-audio) lacks
# them. Swap the synthetic Steam Controller (VID 28DE / PID 1102 -> Family.Steam)
# onto slot 1 alone, open its Audio tab, capture, then unassign.
Write-Host "[3c] Haptic-tone audio controls (Steam Controller)"
Nav "Devices"; Start-Sleep -Milliseconds 600
Assign-DeviceToSlot -DeviceNamePart "Steam Controller" -SlotNumberLabel "1" | Out-Null
Start-Sleep -Milliseconds 800
Nav "Dashboard"; Start-Sleep -Milliseconds 900
$shH = Find-UIA -Aid "SlotsItemsControl"
$cdH = if ($shH) { @($shH.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
if ($cdH.Count -ge 1) {
    Click-El $cdH[0] -Label "Xbox card (Steam haptics)" -Delay 1500 | Out-Null
    Select-MappedDevice "Steam Controller" | Out-Null
    if (Tab "Audio") { Start-Sleep -Milliseconds 700; Cap "pad-audio-haptic-controls" }
    else { Write-Host "  !! Audio tab not found for the Steam Controller" -ForegroundColor Yellow }
} else { Write-Host "  !! slot card not found for the Steam Controller" -ForegroundColor Yellow }
Nav "Devices"; Start-Sleep -Milliseconds 600
Assign-DeviceToSlot -DeviceNamePart "Steam Controller" -SlotNumberLabel "1" -Unassign | Out-Null

# --- Devices page: the new 3.6.0 device types ---
Nav "Devices"; Start-Sleep -Milliseconds 900

# Capture the non-modal device panes (Consumer, Power) FIRST, then NFC last.
# The NFC "Register / Manage NFC Tags" dialog is modal; running it before the
# others left its dialog covering the screen and stole their captures.
Write-Host "[3b] Consumer Control device"
if (Select-DeviceByName36 "Consumer Control") { Cap "devices-consumer" }

# The UIA tree has degraded by this point in the run TWICE now
# (2026-07-30 cost the KBM shots, 2026-08-19 cost every device-row
# select from here to the end: devices-power, devices-move, the voice
# pair, midi-input, and the whole NFC block all read "not found after
# scroll" against rows plainly in the list). The KBM block already
# takes the fresh-process cure at its own head; this tail cluster is
# its sibling and gets the same one.
Reset-PadForgeUia -ExePath $PadForgeExe | Out-Null
Nav "Devices"; Start-Sleep -Milliseconds 800

Write-Host "[3b] Power / idle disconnect + battery"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "DualSense") { Cap "devices-power" }

# Voice macros (#317, 4.3.0). ShowManageVoicePhrases is true for a
# Microphone-type row, and for a DualSense over Bluetooth whose embedded
# mic carries the phrases. Microphone rows come from live Windows audio
# endpoints rather than the cached <Device> list, so there is no dummy to
# inject: this finds one by its TYPE line, which is exactly the match
# Select-DeviceByName36 already does for the consumer rows.
# PlayStation Move (#277). The row is injected synthetically above, and
# 4.3.1 renamed it: the short "PS Move" form no longer matches anything,
# because "PlayStation Move" does not contain it as a substring.
Write-Host "[3b] PlayStation Move"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "PlayStation Move Motion") {
    Cap "devices-move"
} else {
    Write-Host "  !! no PlayStation Move row -- SKIPPED devices-move" -ForegroundColor Red
}

Write-Host "[3b] Voice macros (microphone row)"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "Microphone") {
    Cap "devices-voice"
    # The dialog behind "Manage Voice Macros" is where phrases are registered.
    # Modal FluentWindows never appear in the UIA root child scan, so this
    # clicks the button and captures whatever modal comes up, then closes it.
    $voiceBtn = Find-UIA -Name "Manage Voice Macros" -CT ([System.Windows.Automation.ControlType]::Button)
    if ($voiceBtn) {
        Click-El $voiceBtn -Label "Manage Voice Macros" -Delay 1200 | Out-Null
        # The Voice Macros dialog (RegisterVoicePhraseDialog) is a wpf-ui
        # FluentWindow shown via ShowDialog, the same family as Pair /
        # NFC-register / the gesture recorder: Close-AnyModal CANNOT see it
        # (RootElement's Window enumeration never surfaces these), so the
        # 2026-08-19 run left it up, it disabled the main window, and every
        # later Cap photographed this dialog frozen over the Devices page
        # (midi-input, dsu-port-box, remote-link, devices-nfc,
        # nfc-live-preview, settings-community-configs all shipped as the
        # Voice Macros modal).
        #
        # Find it by ENUMERATION, not by foreground. Get-ForegroundDialogHwnd
        # needs the modal to hold foreground at the exact call moment and
        # returned Zero here, so the close was a no-op against Zero and the
        # dialog stayed up through the whole rest of the run.
        Cap "voice-phrases" -AllowModal
        $voiceDlg = Find-DialogHwndByEnum
        if ($voiceDlg -and [IntPtr]$voiceDlg -ne [IntPtr]::Zero) {
            Close-DialogHwnd $voiceDlg
        } else {
            Write-Host "  !! Voice Macros dialog hwnd not found by enum" -ForegroundColor Red
        }
        Close-AnyModal | Out-Null
    } else {
        Write-Host "  !! 'Manage Voice Macros' button not found -- SKIPPED voice-phrases" -ForegroundColor Red
    }
} else {
    Write-Host "  !! no Microphone device row -- SKIPPED devices-voice AND voice-phrases" -ForegroundColor Red
}

Write-Host "[3b] MIDI input device"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "MIDI Keyboard") {
    Cap "midi-input"
    # Same Devices-page MIDI live preview under the 2D-Overlay-System.md name
    # (MIDI Input Mode), which references a distinct image file.
    Cap "midi-input-mode-devices-page"
}

# DSU Port box (DSU-Motion-Server.md): the DSU toggle + Port box + reset sit on
# the Dashboard between the slot cards and the Remote Link section, above the
# web-controller port. A shorter scroll than Remote Link (-16) lands on them.
Write-Host "[3b] DSU Port box (Dashboard section)"
Nav "Dashboard"; Start-Sleep -Milliseconds 800
ScrollContent -Clicks -11
Cap "dsu-port-box"
ScrollContent -Clicks 11

Write-Host "[3b] Remote Link (Dashboard section)"
Nav "Dashboard"; Start-Sleep -Milliseconds 800
ScrollContent -Clicks -16
Cap "remote-link"
ScrollContent -Clicks 16

Write-Host "[3b] Wii pairing dialog"
# The Pair control is now an icon-only header button (glyph E702 = Bluetooth,
# ToolTip "Pair"), so its UIA Name is the glyph, not "Pair". The old
# Name -eq "Pair" search never matched on the current build, which is why run 3
# logged "Pair button not found" and wii-pair.png on disk is a stale 2026-07-03
# artifact. Find it by the E702 glyph in the Devices header strip, with a re-nav +
# retry in case the header has not realized yet.
$glyphPair = [char]0xE702
$pairBtn = $null
for ($ptry = 0; $ptry -lt 4 -and -not $pairBtn; $ptry++) {
    Nav "Devices"; Start-Sleep -Milliseconds 900
    $wrPH = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrPH) | Out-Null
    foreach ($b in $script:uiaWin.FindAll($TD, $btn36)) {
        $r = Get-Rect $b
        if ($null -eq $r -or $r.Y -gt ($wrPH.Top + 160)) { continue }   # header strip only
        $nm = $b.Current.Name
        if ($nm -eq "Pair" -or ($nm -and $nm.IndexOf($glyphPair) -ge 0)) { $pairBtn = $b; break }
        # Name may be empty on some peers; match the button's child glyph TextBlock.
        $childGlyph = $b.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "$glyphPair")))
        if ($childGlyph) { $pairBtn = $b; break }
    }
    if (-not $pairBtn) { Start-Sleep -Milliseconds 600 }
}
if ($pairBtn) {
    Click-El $pairBtn -Label "Pair" -Delay 2200 | Out-Null
    # The Pair dialog is a wpf-ui FluentWindow modal (ShowDialog, Owner=MainWindow).
    # It grabs foreground, so grab its hwnd HERE -- RootElement's top-level Window
    # enumeration does not surface it (the 2026-07-12 run logged "Pair dialog window
    # not found", left it stuck, and corrupted the NFC shots).
    $pairDlg = Get-ForegroundDialogHwnd
    # Only capture if the modal actually opened. This shot was taken
    # unconditionally, so a Pair button that failed to open its dialog
    # overwrote wii-pair.png with whatever was on screen, which is the Devices
    # page. The gesture-recorder block above already guards for exactly this
    # reason. Skipping keeps the previous good screenshot.
    if ($pairDlg -ne [IntPtr]::Zero) { Cap "wii-pair" -AllowModal }
    else { Write-Host "  !! Pair dialog did not open; keeping the existing wii-pair.png" -ForegroundColor Yellow }
    # DualShock 3 family (v4, WiiPair_FamilyDs3): the Controller Family combo is a
    # 2-item ComboBox (Nintendo Wii = index 0, Sony DualShock 3 = index 1). Drive it by
    # a dialog-rect-relative coordinate (the modal is centered + fixed width): open the
    # combo (~0.50 W, 0.32 H of the dialog), then {DOWN}{ENTER} selects DS3, swapping in
    # the DS3 USB-pairing instructions. Then Cap.
    if ($pairDlg -ne [IntPtr]::Zero) {
        Write-Host "  ds3-pair: switching family (dialog hwnd=$pairDlg)"
        [Win32]::ForceFG([IntPtr]$pairDlg); Start-Sleep -Milliseconds 400
        $dr = New-Object Win32+RECT
        [Win32]::GetWindowRect([IntPtr]$pairDlg, [ref]$dr) | Out-Null
        $drw = $dr.Right - $dr.Left; $drh = $dr.Bottom - $dr.Top
        [Win32]::ClickAt([int]($dr.Left + 0.50 * $drw), [int]($dr.Top + 0.32 * $drh)); Start-Sleep -Milliseconds 700
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN}"); Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 1000
        Cap "ds3-pair" -AllowModal
    } else {
        Write-Host "  !! Pair dialog hwnd not found (foreground grab failed)" -ForegroundColor Yellow
    }
    # Close the FluentWindow modal reliably with WM_CLOSE; Close-AnyModal cannot see it.
    Close-DialogHwnd $pairDlg
    Close-AnyModal | Out-Null
} else {
    Write-Host "  !! Pair button not found" -ForegroundColor Yellow
}

Write-Host "[3b] NFC reader device (last -- opens a modal dialog)"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "NFC") {
    Cap "devices-nfc"
    # Live tag preview (NFC-Tags.md): with the NFC reader selected, the detail
    # pane lists the named tags plus an "Any NFC Tag" row. The tapped-row
    # highlight needs a live tag tap (hardware), so this captures the resting
    # named-tag list -- same selection, before the register/manage modal opens.
    Cap "nfc-live-preview"
    $nfcBtn = $null
    foreach ($b in $script:uiaWin.FindAll($TD, $btn36)) {
        if ($b.Current.Name -match "NFC Tag") { $nfcBtn = $b; break }
    }
    if ($nfcBtn) {
        Click-El $nfcBtn -Label "Register/Manage NFC Tags" -Delay 1300 | Out-Null
        # RegisterNfcTagDialog is a wpf-ui FluentWindow modal; grab its hwnd at open,
        # capture, then close with WM_CLOSE so it can't survive and hang the
        # web-capture step (Close-AnyModal can't see these FluentWindow modals).
        $nfcDlg = Get-ForegroundDialogHwnd
        # Same guard as wii-pair and the gesture recorder: if the modal did not
        # open, this saved the Devices page as nfc-register.png.
        if ($nfcDlg -ne [IntPtr]::Zero) { Cap "nfc-register" -AllowModal }
        else { Write-Host "  !! NFC register dialog did not open; keeping the existing nfc-register.png" -ForegroundColor Yellow }
        Close-DialogHwnd $nfcDlg
        Close-AnyModal | Out-Null
    }
}
# Final safety: no leftover modal before the web-capture / cleanup steps.
Close-AnyModal | Out-Null

# ==============================================================================
# STEP 3d: Steam Workshop community configs (#9, v4.1.0)
# Owner-directed sequence: cold-forge opt-in, search performed with the game
# selected (Sonic X Shadow Generations), the game's config list, the manifest
# dossier for a selected config, then Save and Apply so the imported profile
# shows on the Profiles page. The import mutates ONLY the regenerated capture
# xml; the owner's real settings ride the backup and are restored in STEP 4.
# The dialog is a FluentWindow modal, UIA-shy from RootElement, so all
# in-dialog work goes through Find-DialogHwndByEnum + FromHandle and never
# walks the (disabled) main window while the modal is up.
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 3c-bis: Starter profile gallery (#256) ===" -ForegroundColor Cyan
# The gallery was never in this harness: its one shot was taken by hand on
# 2026-07-30 and then sat there while every automated shot around it was
# renewed. It is an ordinary modal FluentWindow, so it takes the same
# EnumWindows route the Workshop dialog below already uses.
Nav "Profiles"; Start-Sleep -Milliseconds 1000
$starterBtn = Find-UIA -Name "Browse Starter Profiles" -CT ([System.Windows.Automation.ControlType]::Button)
if (-not $starterBtn) { $starterBtn = Find-UIA -Name "Browse Starter Profiles" }
if ($starterBtn) {
    Click-El $starterBtn -Label "Browse Starter Profiles" -Delay 1500 | Out-Null
    $stDlg = Find-DialogHwndByEnum -MinW 600 -MinH 400
    if ($stDlg -eq [IntPtr]::Zero) {
        Write-Host "  !! starter gallery HWND not found" -ForegroundColor Red
    } else {
        $stUia = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$stDlg)
        Write-Host "  starter dialog hwnd=$stDlg '$($stUia.Current.Name)'" -ForegroundColor Green
        Start-Sleep -Milliseconds 900
        Cap "profiles-starter-gallery" -AllowModal
        # Leave without saving: a starter save would add a profile to the
        # capture xml and change every later Profiles-page shot.
        $stBtnCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)
        $cancelBtn = $null
        foreach ($b in $stUia.FindAll($TD, $stBtnCond)) {
            if ($b.Current.Name -eq "Cancel") { $cancelBtn = $b; break }
        }
        # Click inline rather than through Click-DlgEl: that helper is defined
        # further down the script and is not in scope yet at this point.
        $cr = Get-Rect $cancelBtn
        if ($null -ne $cr) {
            [Win32]::SetForegroundWindow([IntPtr]$stDlg) | Out-Null
            Start-Sleep -Milliseconds 150
            [Win32]::ClickAt([int]($cr.X + $cr.Width / 2), [int]($cr.Y + $cr.Height / 2))
            Write-Host "  closed the starter gallery"
        } else {
            Write-Host "  !! starter Cancel button has no rect; gallery may stay open" -ForegroundColor Yellow
        }
        Start-Sleep -Milliseconds 800
    }
} else {
    Write-Host "  !! 'Browse Starter Profiles' button not found" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== STEP 3d: Steam Workshop (#9) ===" -ForegroundColor Cyan

function Click-DlgEl {
    param($DlgHwnd, [System.Windows.Automation.AutomationElement]$El, [int]$Delay = 700, [string]$Label)
    if (-not $El) { Write-Host "  !! ws NOT FOUND: $Label" -ForegroundColor Yellow; return $false }
    $r = Get-Rect $El
    if ($null -eq $r) { Write-Host "  !! ws EMPTY BOUNDS: $Label" -ForegroundColor Yellow; return $false }
    [Win32]::SetForegroundWindow([IntPtr]$DlgHwnd) | Out-Null
    Start-Sleep -Milliseconds 150
    [Win32]::ClickAt([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds $Delay
    Write-Host "  ws click '$Label'"
    return $true
}

$btnCondWs = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
$liCondWs = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

Nav "Profiles"; Start-Sleep -Milliseconds 1000
$browseBtn = Find-UIA -Name "Browse Community Configs" -CT ([System.Windows.Automation.ControlType]::Button)
if (-not $browseBtn) { $browseBtn = Find-UIA -Name "Browse Community Configs" }
if ($browseBtn) {
    Click-El $browseBtn -Label "Browse Community Configs" -Delay 1500 | Out-Null
    $wsDlg = Find-DialogHwndByEnum -MinW 800 -MinH 500
    if ($wsDlg -eq [IntPtr]::Zero) {
        Write-Host "  !! workshop dialog HWND not found" -ForegroundColor Red
    } else {
        $wsUia = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$wsDlg)
        Write-Host "  workshop dialog hwnd=$wsDlg '$($wsUia.Current.Name)'" -ForegroundColor Green

        # 1) Cold forge (EnableCommunityConfigLookup seeded false in STEP 0).
        Start-Sleep -Milliseconds 700
        Cap "workshop-cold" -AllowModal

        # 2) Opt in from inside the dialog (flips the capture-xml copy only).
        $enableBtn = $null
        foreach ($b in $wsUia.FindAll($TD, $btnCondWs)) {
            if ($b.Current.Name -eq "Enable Community Configs") { $enableBtn = $b; break }
        }
        if (Click-DlgEl $wsDlg $enableBtn -Delay 1000 -Label "Enable Community Configs") {

            # 3) Search Sonic X Shadow Generations (live storesearch).
            $searchEdit = $wsUia.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)))
            if ($searchEdit) {
                Click-DlgEl $wsDlg $searchEdit -Delay 300 -Label "search box" | Out-Null
                try { ($searchEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue("sonic x shadow generations") }
                catch { Write-Host "  !! ws search SetValue failed: $_" -ForegroundColor Yellow }
                # Debounce (500ms) + storesearch + cover art. Poll the shelf.
                $tiles = $null
                for ($w = 0; $w -lt 30; $w++) {
                    Start-Sleep -Milliseconds 1000
                    $tiles = $wsUia.FindAll($TD, $liCondWs)
                    if ($tiles.Count -gt 0) { break }
                }
                Write-Host "  ws shelf tiles: $($tiles.Count)"
                Start-Sleep -Milliseconds 2500   # portrait art settling
                # Select the first tile from the keyboard so the ember
                # selection ring is on for the shot (a mouse click would
                # open the game on mouse-up before we can capture).
                [Win32]::SetForegroundWindow([IntPtr]$wsDlg) | Out-Null
                Start-Sleep -Milliseconds 200
                [System.Windows.Forms.SendKeys]::SendWait("{DOWN}"); Start-Sleep -Milliseconds 600
                Cap "workshop-search" -AllowModal

                # 4) Open the game; wait for its config list.
                [Win32]::SetForegroundWindow([IntPtr]$wsDlg) | Out-Null
                Start-Sleep -Milliseconds 200
                [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
                $cfgList = $null; $cfgRows = @()
                for ($w = 0; $w -lt 40; $w++) {
                    Start-Sleep -Milliseconds 1000
                    $cfgList = $wsUia.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ConfigList")))
                    if ($cfgList) {
                        $cfgRows = @($cfgList.FindAll($TD, $liCondWs))
                        if ($cfgRows.Count -gt 0) { break }
                    }
                }
                Write-Host "  ws config rows: $($cfgRows.Count)"
                Start-Sleep -Milliseconds 2000   # avatars / vote bars settling
                Cap "workshop-configs" -AllowModal
                if ($cfgRows.Count -eq 0) {
                    Write-Host "  !! no workshop configs listed for the game (captured the state as-is; NOT substituting another game per owner directive)" -ForegroundColor Yellow
                } else {
                    # 5) Select the top config; the dossier translates it.
                    Click-DlgEl $wsDlg $cfgRows[0] -Delay 800 -Label "config row 0" | Out-Null
                    $statEl = $null
                    for ($w = 0; $w -lt 30; $w++) {
                        Start-Sleep -Milliseconds 1000
                        $statEl = $wsUia.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "StatCleanNum")))
                        if ($statEl) { break }
                    }
                    if (-not $statEl) { Write-Host "  !! ws manifest stat blocks never appeared" -ForegroundColor Yellow }
                    Start-Sleep -Milliseconds 1200
                    Cap "workshop-manifest" -AllowModal

                    # 6) Save and Apply -> dialog closes, profile lands in the
                    # capture xml's Profiles and is applied.
                    $applyBtn = $null
                    foreach ($b in $wsUia.FindAll($TD, $btnCondWs)) {
                        if ($b.Current.Name -eq "Save and Apply") { $applyBtn = $b; break }
                    }
                    if (Click-DlgEl $wsDlg $applyBtn -Delay 1800 -Label "Save and Apply") {
                        if (-not [Win32]::IsWindowVisible([IntPtr]$wsDlg)) {
                            Write-Host "  ws dialog closed after import" -ForegroundColor Green
                        }
                        Nav "Profiles"; Start-Sleep -Milliseconds 1500
                        Cap "workshop-applied" -AllowModal
                    } else {
                        Write-Host "  !! Save and Apply not clickable (legacy config?)" -ForegroundColor Yellow
                    }
                }
            } else { Write-Host "  !! ws search box not found" -ForegroundColor Yellow }
        }
        # Close the modal if it survived any branch above.
        if ([Win32]::IsWindowVisible([IntPtr]$wsDlg)) { Close-DialogHwnd $wsDlg }
        Close-AnyModal | Out-Null
    }
} else {
    Write-Host "  !! Browse Community Configs button not found on Profiles" -ForegroundColor Red
}

# Settings card for the feature (#9): scroll the Settings page to the bottom
# where the Community Configs card sits (opt-in now checked from the dialog
# enable, so the dependent legacy checkbox and cache/update buttons show).
Write-Host "[3d] Settings Community Configs card"
Nav "Settings"; Start-Sleep -Milliseconds 900
ScrollContent -Clicks -40
Start-Sleep -Milliseconds 500
Cap "settings-community-configs"
ScrollContent -Clicks 60

# ---- 23-24. Web controller ----
Write-Host "[$(Next)/$total] Web controller screenshots"
# Minimize PadForge so it doesn't cover Edge (never TOPMOST anywhere)
if ($script:consoleWnd -and $script:consoleWnd -ne [IntPtr]::Zero) {
    [Win32]::ShowWindow($script:consoleWnd, 5) | Out-Null  # SW_SHOW
}
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
        #
        # Via Get-CimInstance, NOT Get-Process. Process objects only carry a
        # CommandLine property on PowerShell 7+; on the 5.1 this repo runs under
        # it does not exist, so $_.CommandLine was always empty, the -like never
        # matched, and this kill kept nothing from a previous run from lingering.
        # The two kills further down this same function already use the CIM form.
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
            Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
        Start-Sleep -Milliseconds 1500
        # Launch Edge with an isolated temp profile — never touches the default profile.
        Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --app=$Url"
        Start-Sleep -Milliseconds $WaitMs
        # Find the window that belongs to OUR temp-profile launch. Scope to the
        # msedge processes whose command line carries the temp-profile dir --
        # the same filter the kill above already uses. The original code took
        # the first msedge window on the machine, which is the user's real
        # browser when it happens to be open, and that once captured a private
        # page. Matching the temp profile fixes it without guessing at titles.
        $ehwnd = [IntPtr]::Zero
        $tempPids = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
            Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
            Select-Object -ExpandProperty ProcessId)
        foreach ($procId in $tempPids) {
            $ep = Get-Process -Id $procId -EA SilentlyContinue
            if ($ep -and $ep.MainWindowHandle -ne [IntPtr]::Zero) {
                $ehwnd = $ep.MainWindowHandle
                Write-Host "  Edge (temp-profile) window: PID=$procId HWND=$ehwnd title='$($ep.MainWindowTitle)'"
                break
            }
        }
        if ($ehwnd -eq [IntPtr]::Zero) {
            Write-Host "  !! No PadForge temp-profile Edge window found -- skipping $Name (kept existing screenshot)" -ForegroundColor Yellow
            Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
                Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
                ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
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

    # The server has to be ANSWERING before Edge is pointed at it. Without this
    # probe the capture happily photographs Edge's "localhost refused to
    # connect" page and ships it: that is exactly what web-landing.png and
    # web-controller.png were on 2026-08-09, on the site and in the README.
    # A shot nobody verified is worse than a missing shot, because the missing
    # one gets noticed.
    function Wait-Web {
        param([string]$Url, [int]$TimeoutSec = 45)
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline) {
            try {
                $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
                if ($resp.StatusCode -eq 200) {
                    Write-Host "  web server answering: $Url ($($resp.RawContentLength) bytes)" -ForegroundColor Green
                    return $true
                }
            } catch { Start-Sleep -Milliseconds 1000 }
        }
        Write-Host "  !! web server never answered $Url -- SKIPPING web shots (kept existing)" -ForegroundColor Red
        return $false
    }

    $webUp = Wait-Web "http://localhost:${webPort}/"

    # Landing page (needs a few seconds for Edge to fully render)
    if ($webUp) { Cap-Web "http://localhost:${webPort}/" "web-landing" 6000 }

    # Controller page (needs WebSocket for layout images)
    Write-Host "[$(Next)/$total] Web controller - gamepad"
    if ($webUp) { Cap-Web "http://localhost:${webPort}/controller.html?layout=xbox360" "web-controller" 6000 }

    # Bring PadForge back to foreground after web captures
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 300

} catch {
    Write-Host "  !! Web screenshots failed: $($_.Exception.Message)" -ForegroundColor Yellow
    $n++
}


}
finally {
# ==============================================================================
# STEP 4: Cleanup (ALWAYS runs, including after a mid-run terminating error)
# ==============================================================================
Write-Host ""
Write-Host "=== STEP 4: Cleanup ===" -ForegroundColor Cyan

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

# Restore toast-notification settings to their pre-run values.
foreach ($tk in $toastKeys) {
    try {
        $prior = $toastPrior["$($tk.Path)|$($tk.Name)"]
        if ($null -eq $prior) { Remove-ItemProperty -Path $tk.Path -Name $tk.Name -EA SilentlyContinue }
        else { Set-ItemProperty -Path $tk.Path -Name $tk.Name -Value $prior -Type DWord }
    } catch { Write-Host "  !! toast restore failed for $($tk.Name): $_" -ForegroundColor Yellow }
}
Write-Host "  Toast notification settings restored"

# Relaunch PadForge clean on the restored (owner) settings so the app is left
# running exactly as before the run.
Start-Process $PadForgeExe
Write-Host "  Relaunched PadForge on restored settings" -ForegroundColor Green


Write-Host ""
Write-Host "=== DONE ===" -ForegroundColor Cyan
# ==============================================================================
# COVERAGE AUDIT. Runs at the end of every capture, always.
# ==============================================================================
# Every wrong screenshot this project has shipped got through because nothing
# compared what the run PRODUCED against what the docs EXPECT. A stale image,
# a skipped step and a healthy one all look identical in a log full of green.
# So the run ends by answering three questions out loud:
#
#   1. Which images does a docs page reference that do not exist?   (broken)
#   2. Which images exist but are older than this run?              (stale)
#   3. Which images exist that no docs page and no site asset uses?  (orphan,
#      which in this project has always meant a page is missing its picture)
#
# None of these is fatal. All of them are printed. A run that ends with a
# non-empty list is a run whose output still needs work, and saying so here is
# the whole point.
$auditRoot = Split-Path (Split-Path $OutputDir -Parent) -Parent   # padforge.org
$wikiDir   = Join-Path (Split-Path $OutputDir -Parent) ""
$mdFiles   = @(Get-ChildItem -Path (Split-Path $OutputDir -Parent) -Filter *.md -Recurse -EA SilentlyContinue)
$imgFiles  = @(Get-ChildItem -Path $OutputDir -Filter *.png -EA SilentlyContinue)

$referenced = New-Object System.Collections.Generic.HashSet[string]
foreach ($md in $mdFiles) {
    $txt = Get-Content $md.FullName -Raw -EA SilentlyContinue
    if (-not $txt) { continue }
    foreach ($m in [regex]::Matches($txt, '!\[[^\]]*\]\((?:\.\./)*images/([A-Za-z0-9._-]+)\)')) {
        $null = $referenced.Add($m.Groups[1].Value)
    }
}

$knownAssets = @("logo.png","icon.png","hidmaestro-logo-dark.png","hidmaestro-logo-light.png","about.png")
$runStart = $script:CaptureRunStart
if (-not $runStart) { $runStart = (Get-Date).AddHours(-6) }

$broken = @(); $stale = @(); $orphan = @()
foreach ($r in $referenced) {
    if (-not (Test-Path (Join-Path $OutputDir $r))) { $broken += $r }
}
foreach ($f in $imgFiles) {
    if ($knownAssets -contains $f.Name) { continue }
    if ($f.LastWriteTime -lt $runStart) { $stale += $f.Name }
    $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    $usedBySite = $base -like "colorway-*"
    if (-not $usedBySite) {
        $siteJpg = Join-Path $auditRoot ("assets\screenshot-" + $base + ".jpg")
        if (Test-Path $siteJpg) { $usedBySite = $true }
    }
    if (-not $referenced.Contains($f.Name) -and -not $usedBySite) { $orphan += $f.Name }
}

Write-Host ""
Write-Host "=== COVERAGE AUDIT ===" -ForegroundColor Cyan
Write-Host ("  images produced/present : {0}" -f $imgFiles.Count)
Write-Host ("  referenced by docs      : {0}" -f $referenced.Count)
if ($broken.Count -gt 0) {
    Write-Host ("  BROKEN (referenced, missing): {0}" -f $broken.Count) -ForegroundColor Red
    foreach ($b in ($broken | Sort-Object)) { Write-Host "     $b" -ForegroundColor Red }
} else { Write-Host "  broken references       : 0" -ForegroundColor Green }
if ($stale.Count -gt 0) {
    Write-Host ("  STALE (older than this run): {0}" -f $stale.Count) -ForegroundColor Yellow
    foreach ($x in ($stale | Sort-Object)) { Write-Host "     $x" -ForegroundColor Yellow }
} else { Write-Host "  stale images            : 0" -ForegroundColor Green }
if ($orphan.Count -gt 0) {
    Write-Host ("  ORPHAN (used by nothing): {0}" -f $orphan.Count) -ForegroundColor Yellow
    Write-Host "     an orphan here has always meant a page is missing its picture" -ForegroundColor DarkGray
    foreach ($o in ($orphan | Sort-Object)) { Write-Host "     $o" -ForegroundColor Yellow }
} else { Write-Host "  orphan images           : 0" -ForegroundColor Green }
# A modal leak corrupts every shot after it, so it belongs in the audit
# beside the stale/broken counts rather than buried in the transcript.
if ($script:modalLeaks -gt 0) {
    Write-Host ("  MODAL LEAKS             : {0}  <-- shots after the leak are CORRUPT" -f $script:modalLeaks) -ForegroundColor Red
} else { Write-Host "  modal leaks             : 0" -ForegroundColor Green }
Write-Host ""

Write-Host "Screenshots in: $OutputDir"
Write-Host ""
Get-ChildItem "$OutputDir\*.png" | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0} ({1}KB)" -f $_.Name, [math]::Round($_.Length / 1024))
}
}

# Transcript closes LAST so the coverage audit above is recorded in the log.
# It used to stop before the audit ran, which meant the one report that says
# whether the run actually covered everything went to the console and nowhere else.
Stop-Transcript | Out-Null
Remove-Item $lockPath -Force -EA SilentlyContinue
