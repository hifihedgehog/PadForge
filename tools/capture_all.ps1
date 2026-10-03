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
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);

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
    # A descendants search can fail with UIA_E_TIMEOUT (0x80131505). Run 5
    # of the 5.0.0 capture lost everything after the Sticks tab to one such
    # failure, about a minute into a search for PadPageView. Search once
    # more after a pause, then report not found, which every caller already
    # handles. Process.Responding asks the window with WM_NULL, so the log
    # tells a hung UI thread from a slow tree walk.
    $what = "Name='$Name' Aid='$Aid'"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $found = $Parent.FindFirst($TD, $c)
            if ($sw.ElapsedMilliseconds -gt 5000) {
                Write-Host ("  .. slow UIA search for {0}: {1:N1} s" -f $what, ($sw.ElapsedMilliseconds / 1000)) -ForegroundColor DarkGray
            }
            return $found
        } catch {
            $resp = try { (Get-Process -Id $script:proc.Id -EA Stop).Responding } catch { "unknown" }
            Write-Host ("  !! UIA search for {0} failed after {1:N1} s (attempt {2}), PadForge responding: {3}. {4}" -f $what, ($sw.ElapsedMilliseconds / 1000), $attempt, $resp, $_.Exception.Message) -ForegroundColor Yellow
            Microsoft.PowerShell.Utility\Start-Sleep -Seconds 3
            $sw.Restart()
        }
    }
    return $null
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
    Reset-KnownState
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
            # The slot arrays live under AppSettings (SettingsService reads
            # appSettings.SlotControllerTypes). Older files kept them at the
            # root, so look in both. At the root alone this lookup found
            # nothing and fell back to the passed pad index without a word.
            $typesNode = $root.SelectSingleNode("AppSettings/SlotControllerTypes")
            if (-not $typesNode) { $typesNode = $root.SelectSingleNode("SlotControllerTypes") }
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
                # The app was killed above, so this is a restart like any
                # other and the handles must follow it. Returning with the old
                # hwnd left every later UIA call on a dead window, and the next
                # FromHandle threw "Unrecognized error" (2026-09-22).
                return (Reset-PadForgeUia -ExePath $ExePath)
            }
        }

        # Clone an existing row so every field the serializer expects is present.
        $template = $usNode.FirstChild
        if (-not $template) {
            # A focused run skips the UI assignment block, so the regenerated
            # file can have NO rows to clone and the hand-built branch below
            # is all that is left. That branch produced a row the pad page's
            # device dropdown never listed, while the same call in a full run
            # (where a row existed to clone) worked. Rather than keep guessing
            # which field the loader wants, borrow a real row from the
            # OWNER'S BACKUP, which is a file the app itself wrote.
            try {
                $bakPath = "$XmlPath.bak"
                if (Test-Path $bakPath) {
                    [xml]$bx = Get-Content $bakPath
                    $bTpl = $bx.PadForgeSettings.SelectSingleNode("UserSettings/Setting")
                    if ($bTpl) {
                        $template = $ax.ImportNode($bTpl, $true)
                        Write-Host "  (no row to clone; borrowed the shape of one from the backup)" -ForegroundColor DarkGray
                    }
                }
            } catch { }
        }
        if ($template) {
            $row = $template.CloneNode($true)
            foreach ($pair in @(@("InstanceGuid", $guid), @("ProductGuid", $pguid),
                                @("InstanceName", $iname), @("ProductName", $iname),
                                @("MapTo", "$PadIndex"))) {
                $n = $row.SelectSingleNode($pair[0])
                if ($n) { $n.InnerText = $pair[1] }
            }
        } else {
            # DECLARED ORDER, NOT A CONVENIENT ONE. UserSetting.cs declares
            # InstanceGuid, InstanceName, ProductGuid, ProductName, MapTo, and
            # XmlSerializer reads a sequence in declared order: the old list
            # put ProductGuid second, so a hand-built row deserialized wrong
            # and the assignment vanished on load. It only ever showed up in a
            # focused run, because a full run has existing rows to clone and
            # never reaches this branch. That is how "mapped X to pad N by
            # XML" printed while the pad page's device dropdown came back
            # empty.
            $row = $ax.CreateElement("Setting")
            foreach ($pair in @(@("InstanceGuid", $guid), @("InstanceName", $iname),
                                @("ProductGuid", $pguid), @("ProductName", $iname),
                                @("MapTo", "$PadIndex"), @("IsEnabled", "true"))) {
                $e = $ax.CreateElement($pair[0]); $e.InnerText = $pair[1]; $row.AppendChild($e) | Out-Null
            }
            Write-Host "  (no existing <Setting> to clone; built one in declared order)" -ForegroundColor DarkGray
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
    Reset-KnownState
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
    Reset-KnownState
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    return $true
}

# Author the two SLOT-scoped structures the 4.4.0 shots need, into an
# already-open settings document, for the Xbox slot (pad index 0, the slot
# every injected macro rides).
#
#   1. A shift layer. macro-switch-layer photographs the SwitchLayer action's
#      layer dropdown, and that dropdown lists the layers the slot DECLARES.
#      Without an activator the slot declares none, so the shot would be an
#      action editor offering only Base, which is not what the page documents.
#   2. A radial menu whose second cell is bound to a macro by name
#      (MenuItemDefinition.MacroName), for menu-macro-cell.
#
# Both live on the MappingSet, and SlotMappingSets is indexed by PAD INDEX,
# so element 0 is the Xbox slot. Both DTOs are all-attribute
# (ShiftActivator.cs, MenuDefinitionEntry.cs), and XmlSerializer matches
# attributes by NAME rather than by position, so unlike the macro elements
# these carry no ordering hazard. Idempotent: a second call finds them and
# leaves them alone.
function Write-SlotStructures {
    param([xml]$Doc)
    $setsNode = $Doc.PadForgeSettings.SelectSingleNode("SlotMappingSets")
    if (-not $setsNode) {
        Write-Host "  !! no <SlotMappingSets> node -- SKIPPING the Aim shift layer AND the macro-cell menu" -ForegroundColor Red
        Write-Host "  !! macro-switch-layer and menu-macro-cell cannot be captured without them" -ForegroundColor Red
        return $false
    }
    $sets = @($setsNode.SelectNodes("MappingSet"))
    if ((Get-Count $sets) -lt 1) {
        Write-Host "  !! <SlotMappingSets> is empty -- SKIPPING the Aim shift layer AND the macro-cell menu" -ForegroundColor Red
        Write-Host "  !! macro-switch-layer and menu-macro-cell cannot be captured without them" -ForegroundColor Red
        return $false
    }
    $set = $sets[0]

    if ($set.SelectSingleNode("ShiftActivator[@LayerMask='Aim']")) {
        Write-Host "  Aim shift layer already on slot 0"
    } else {
        $sa = $Doc.CreateDocumentFragment()
        # Hold on the left shoulder, overlaying Base rather than replacing it,
        # which is the shape a scoped macro is documented against.
        $sa.InnerXml = '<ShiftActivator DeviceGuid="" Descriptor="Button 4" Mode="Hold" LayerMask="Aim" LayerName="Aim" InheritUnmapped="true" Color="#F2792B" />'
        $set.AppendChild($sa) | Out-Null
        Write-Host "  wrote the Aim shift layer onto slot 0" -ForegroundColor Green
    }

    if ($set.SelectSingleNode("Menu")) {
        Write-Host "  slot 0 already carries a menu"
    } else {
        # Radial, four ring cells, no center (HasCenter false, so index 0 is
        # unused and the ring is 1..4). Cell 2 is the macro cell: it names
        # "Quick Combo", which is macro 1 above, so the picker resolves it
        # instead of showing the "(no such macro)" stale form.
        $menuXml = @'
<Menu DeviceGuid="" MenuId="1" Name="Combat Wheel" Kind="Radial" HostDescriptor="Gamepad RightStick" HostHalf="0" CustomXDescriptor="" CustomYDescriptor="" ClickDescriptor="" LayerMask="" FireType="Click" CellCount="4" HasCenter="false" ShowLabels="true" PosXPercent="50" PosYPercent="50" ScalePercent="100" OpacityPercent="90" EngageDeadzonePercent="25" SensitivityPercent="100" Enabled="true">
  <Item Index="1" Label="Reload" VirtualKey="82" XboxButtons="0" ExtendedButton="0" MacroName="" Icon="" />
  <Item Index="2" Label="Quick Combo" VirtualKey="0" XboxButtons="0" ExtendedButton="0" MacroName="Quick Combo" Icon="" />
  <Item Index="3" Label="Crouch" VirtualKey="0" XboxButtons="64" ExtendedButton="0" MacroName="" Icon="" />
  <Item Index="4" Label="Map" VirtualKey="77" XboxButtons="0" ExtendedButton="0" MacroName="" Icon="" />
</Menu>
'@
        $mf = $Doc.CreateDocumentFragment()
        $mf.InnerXml = $menuXml.Trim()
        $set.AppendChild($mf) | Out-Null
        Write-Host "  wrote the Combat Wheel menu (cell 2 bound to the Quick Combo macro) onto slot 0" -ForegroundColor Green
    }

    # Three mapping editors, written as data so each shot opens on a row
    # that already holds its setting. The editors' combos sit in the pad
    # page tab body, which this harness cannot drive (the 4.4.0 Stick Trim
    # shot typed into the wrong row's combo). Descriptors are the auto-map's
    # own: Axis 2 and Axis 5 are the triggers, Axis 4 is Right Stick Y,
    # Button 4 and Button 5 are the shoulders.
    #   Slot 0, Left Trigger: Stick Trim. The last source is the trim
    #   stick (features/mappings.md), so it becomes the first device's
    #   Right Stick Y.
    $lt = $set.SelectSingleNode("Row[@Target='LeftTrigger' and @LayerMask='Base']")
    $ltSrc = if ($lt) { @($lt.SelectNodes("Source")) } else { @() }
    if ((Get-Count $ltSrc) -ge 2) {
        $ltSrc[-1].SetAttribute("DeviceGuid", $ltSrc[0].GetAttribute("DeviceGuid"))
        $ltSrc[-1].SetAttribute("Descriptor", "Axis 4")
        $lt.SetAttribute("CombineMode", "StickTrim")
        Write-Host "  slot 0 Left Trigger: Stick Trim, trimmed by Right Stick Y" -ForegroundColor Green
    } else {
        Write-Host "  !! slot 0 Left Trigger has $(Get-Count $ltSrc) source(s), Stick Trim needs two -- pad-stick-trim shows no strip" -ForegroundColor Red
    }
    #   Slot 0, Right Trigger: the primary source reads as Rapid Trigger.
    $rt = $set.SelectSingleNode("Row[@Target='RightTrigger' and @LayerMask='Base']")
    $rtSrc = if ($rt) { $rt.SelectSingleNode("Source") } else { $null }
    if ($rtSrc) {
        $rtSrc.SetAttribute("Kind", "RapidTrigger")
        Write-Host "  slot 0 Right Trigger: Rapid Trigger" -ForegroundColor Green
    } else {
        Write-Host "  !! no slot 0 Right Trigger source -- mapping-rapid-trigger shows Direct" -ForegroundColor Red
    }
    #   Slot 1 (PlayStation), Motion Roll: R1 rolls right and L1, inverted,
    #   rolls left, the pair "+ Opposite Direction" builds. Two sources
    #   keep the row out of the compact trivial rendering, so selecting it
    #   from the keyboard opens its editor. A new Row goes after the last
    #   Row: XmlSerializer reads MappingSet's elements in declared order.
    if ((Get-Count $sets) -ge 2) {
        $ps = $sets[1]
        # The app saves a PlayStation slot's Motion rows even while they are
        # empty, so the row is usually there already with no Source in it:
        # fill it rather than skip it, which is what the first 5.0.0 run did.
        $roll = $ps.SelectSingleNode("Row[@Target='MotionRoll' and @LayerMask='Base']")
        if ($roll -and $roll.SelectSingleNode("Source")) {
            Write-Host "  slot 1 Motion Roll already has sources"
        } else {
            $l1 = $ps.SelectSingleNode("Row[@Target='LeftShoulder' and @LayerMask='Base']")
            $r1 = $ps.SelectSingleNode("Row[@Target='RightShoulder' and @LayerMask='Base']")
            $l1s = if ($l1) { $l1.SelectSingleNode("Source") } else { $null }
            $r1s = if ($r1) { $r1.SelectSingleNode("Source") } else { $null }
            if ($l1s -and $r1s) {
                if (-not $roll) {
                    $roll = $l1.CloneNode($false)
                    $roll.SetAttribute("Target", "MotionRoll")
                    $ps.InsertAfter($roll, @($ps.SelectNodes("Row"))[-1]) | Out-Null
                }
                $roll.AppendChild($r1s.CloneNode($true)) | Out-Null
                $opp = $l1s.CloneNode($true)
                $opp.SetAttribute("Invert", "true")
                $roll.AppendChild($opp) | Out-Null
                Write-Host "  slot 1 Motion Roll: R1, and L1 inverted" -ForegroundColor Green
            } else {
                Write-Host "  !! slot 1 has no shoulder rows to build Motion Roll from -- mapping-motion-rows shows an empty row" -ForegroundColor Red
            }
        }
    }
    return $true
}

# Clear the assignment-prompt banner (#the Settings card of the same name).
# It appears under the device bar whenever a device connects while a slot's
# page is open, which is exactly what happens seconds after every restart
# this harness makes, and it sits ACROSS THE TOP of the pad page. One
# shipped in the 4.4.0 Valve frames reading "Microphone Array (Realtek(R)
# Audio) just connected". Not Now dismisses that device for that slot for
# the rest of the session and assigns nothing.
function Dismiss-AssignBanner {
    $btn = Find-UIA -Name "Not Now" -CT ([System.Windows.Automation.ControlType]::Button)
    if (-not $btn) { $btn = Find-UIA -Name "Not Now" }
    if ($btn) {
        Click-El $btn -Label "Not Now (assignment prompt)" -Delay 700 | Out-Null
        Start-Sleep -Milliseconds 400
        return $true
    }
    return $false
}

# The run seeds both assignment prompts OFF (see the AppSettings seeding).
# Five Settings frames show the card that governs them, and those frames
# have to show the defaults, which are on. Cap sets the two boxes for each
# of those frames and back after it. No pad page is open on Settings, so
# turning them on raises no offer. Both boxes are found before either is
# toggled: a change autosaves after 2 s of quiet and writes "Settings saved"
# into the status bar, so the frame has to be taken inside that window.
# Returns $false unless both boxes reached the wanted state.
$script:AssignOfferFrames = @("settings-input-engine", "settings-assignment-prompts",
                              "settings-handheld-buttons", "settings-battery-alerts",
                              "settings-hidhide")
function Set-AssignOfferBoxes {
    param([bool]$On)
    $want = if ($On) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    $boxes = @()
    foreach ($label in @("Offer New Devices to the Open Virtual Controller",
                         "Offer Any Connecting Device When the Open Virtual Controller Has No Devices")) {
        $cb = Find-UIA -Name $label -CT ([System.Windows.Automation.ControlType]::CheckBox)
        if (-not $cb) {
            Write-Host "  !! assignment prompt box '$label' not found" -ForegroundColor Red
            return $false
        }
        $boxes += $cb
    }
    try {
        foreach ($cb in $boxes) {
            $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -ne $want) { $tp.Toggle() }
        }
        Start-Sleep -Milliseconds 300
        foreach ($cb in $boxes) {
            $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -ne $want) {
                Write-Host "  !! assignment prompt box '$($cb.Current.Name)' did not reach $want" -ForegroundColor Red
                return $false
            }
        }
    } catch {
        Write-Host "  !! assignment prompt boxes: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
    return $true
}

# Wait for the input engine to be FORGING before a preview capture. A
# stopped engine draws an inert schematic and a 0 Hz status bar, and the
# 4.4.0 Valve frames shipped exactly that. The status bar carries the word
# as its own Text peer.
function Wait-EngineForging {
    param([int]$TimeoutSec = 25)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Find-UIA -Name "Forging") { return $true }
        Start-Sleep -Milliseconds 800
    }
    Write-Host "  !! the engine is not forging; a preview taken now would be inert" -ForegroundColor Red
    return $false
}

# Read the preset the pad page is ACTUALLY showing. Writing the id into the
# settings file and reading the file back proves only what is on disk: the
# 4.4.0 run did exactly that, reported "preset survived the load", and then
# photographed a slot still on the built-in Custom profile with no Valve
# body in it. The control is the only witness that counts.
function Get-PresetText {
    $padPage = Find-UIA -Aid "PadPageView"
    if (-not $padPage) { return $null }
    foreach ($aid in @("ExtendedProfileCombo", "HMaestroProfileCombo", "KbmSurfacesCombo")) {
        $cb = Find-UIA -Parent $padPage -Aid $aid
        if (-not $cb) { continue }
        try {
            $v = $cb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if ($v) { return "$v" }
        } catch {}
        try {
            $sel = $cb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            if ((Get-Count $sel) -gt 0) { return "$($sel[0].Current.Name)" }
        } catch {}
        # Last resort: the collapsed combo's own Name is the selected text
        # on a WPF ComboBoxAutomationPeer.
        try { if ($cb.Current.Name) { return "$($cb.Current.Name)" } } catch {}
    }
    return $null
}

# Put a HIDMaestro profile on a slot by writing it, not by picking it.
#
# The Extended picker holds the whole Extended catalog, 220-plus entries,
# and a WPF ComboBox VIRTUALIZES that list: only the couple of dozen
# realized rows expose ListItem peers, so a UIA search for an entry deep in
# the alphabet finds nothing and reports "not in the picker" about a profile
# that is plainly in it. Both Valve shots failed that way. The preset is one
# string in the settings file, AppSettings/SlotProfileIds indexed by PAD
# index, so write it with the app closed and restart, the same shape
# Ensure-DeviceAssigned and Seed-AudioDsp use for their state.
function Set-SlotPreset {
    # -ClearCustomize turns the slot's Extended Customize off. A preset
    # change keeps Customize as it was (PadViewModel), and the Extended
    # block's Custom preset turns it on, so a Valve persona picked after it
    # came up customized: a combination the shots must not show.
    param([int]$PadIndex, [string]$ProfileId, [string]$XmlPath, [string]$ExePath, [switch]$ClearCustomize)

    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    try {
        [xml]$px = Get-Content $XmlPath
        $appNode = $px.PadForgeSettings.SelectSingleNode("AppSettings")
        if (-not $appNode) { Write-Host "  !! no <AppSettings> node" -ForegroundColor Red; Start-Process $ExePath; Start-Sleep 8; return $false }
        $idsNode = $appNode.SelectSingleNode("SlotProfileIds")
        if (-not $idsNode) { Write-Host "  !! no <SlotProfileIds> node" -ForegroundColor Red; Start-Process $ExePath; Start-Sleep 8; return $false }
        $ids = @($idsNode.SelectNodes("Id"))
        if ((Get-Count $ids) -le $PadIndex) {
            Write-Host "  !! <SlotProfileIds> has $(Get-Count $ids) entries, need index $PadIndex" -ForegroundColor Red
            Start-Process $ExePath; Start-Sleep 8; return $false
        }
        $slot = $ids[$PadIndex]
        # The empty entries are written as xsi:nil, which keeps the element
        # empty however much text is put in it. Drop the attribute first.
        $nil = $slot.Attributes["nil", "http://www.w3.org/2001/XMLSchema-instance"]
        if ($nil) { $slot.Attributes.Remove($nil) | Out-Null }
        $slot.InnerText = $ProfileId
        if ($ClearCustomize) {
            $cfg = $appNode.SelectSingleNode("ExtendedConfigs/Config[@SlotIndex='$PadIndex']")
            if ($cfg) {
                $cfg.SetAttribute("Customize", "false")
                Write-Host "  slot $PadIndex Customize turned off" -ForegroundColor Green
            }
        }
        $px.Save($XmlPath)
        Write-Host "  set slot $PadIndex preset to '$ProfileId' by XML" -ForegroundColor Green
    } catch {
        Write-Host "  !! preset write failed: $($_.Exception.Message)" -ForegroundColor Red
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
    $script:proc = $pr
    $script:hwnd = $pr.MainWindowHandle
    Reset-KnownState
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800

    # VERIFY THE LOAD. A profile id the catalog does not carry is dropped on
    # apply and the slot keeps its old preset, which would ship the wrong
    # controller body under the right file name.
    try {
        [xml]$vx = Get-Content $XmlPath
        $back = @($vx.PadForgeSettings.SelectNodes("AppSettings/SlotProfileIds/Id"))
        $now = if ((Get-Count $back) -gt $PadIndex) { "$($back[$PadIndex].InnerText)".Trim() } else { "" }
        if ($now -ne $ProfileId) {
            Write-Host "  !! slot $PadIndex preset read back as '$now', not '$ProfileId'" -ForegroundColor Red
            return $false
        }
        Write-Host "  slot $PadIndex preset survived the load: $now" -ForegroundColor Green
        if ($ClearCustomize) {
            $cfgBack = $vx.PadForgeSettings.SelectSingleNode("AppSettings/ExtendedConfigs/Config[@SlotIndex='$PadIndex']")
            if ($cfgBack -and $cfgBack.GetAttribute("Customize") -eq "true") {
                Write-Host "  !! slot $PadIndex came back with Customize on" -ForegroundColor Red
                return $false
            }
        }
    } catch {
        Write-Host "  !! could not read the preset back: $($_.Exception.Message)" -ForegroundColor Red
    }
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
        # The shift layer and the macro-cell menu ride the SAME closed-app
        # window. They are slot-scoped rather than macro-scoped, but the state
        # that loads them cleanly is identical (written while PadForge is not
        # running, read on the next launch), and folding them in here spends
        # one restart instead of two.
        Write-SlotStructures -Doc $mx | Out-Null
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
    Reset-KnownState
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 800
    Write-Host "  PadForge restarted for macros (HWND=$($script:hwnd))" -ForegroundColor Green

    # PROVE THE LOAD, do not assume it. XmlSerializer drops a whole array on
    # one malformed element and PadForge re-saves what it has in memory, so
    # "the write succeeded" and "the app is showing them" are different
    # facts and the gap between them has already shipped five blank panes.
    # Read the file BACK after the restart: the app has re-serialized its own
    # state by now, so what survives here is what the UI holds.
    try {
        [xml]$vx = Get-Content $XmlPath
        $vRoot = $vx.PadForgeSettings
        $vMacros = @($vRoot.SelectNodes("Macros/Macro")).Count
        $vLayers = @($vRoot.SelectNodes("SlotMappingSets/MappingSet/ShiftActivator")).Count
        $vMenus  = @($vRoot.SelectNodes("SlotMappingSets/MappingSet/Menu")).Count
        Write-Host "  after restart: $vMacros macro(s), $vLayers shift activator(s), $vMenus menu(s)"
        if ($vMacros -lt (Get-Count $frags)) {
            Write-Host "  !! MACROS WERE DROPPED ON LOAD ($vMacros of $(Get-Count $frags)); check <Macro> element ORDER" -ForegroundColor Red
        }
        if ($vLayers -lt 1) {
            Write-Host "  !! NO SHIFT ACTIVATOR SURVIVED THE LOAD -- macro-switch-layer will show a Base-only dropdown" -ForegroundColor Red
        }
        if ($vMenus -lt 1) {
            Write-Host "  !! NO MENU SURVIVED THE LOAD -- menu-macro-cell and menu-icon-packs cannot be captured" -ForegroundColor Red
        }
    } catch {
        Write-Host "  !! could not read the settings file back to verify the load: $($_.Exception.Message)" -ForegroundColor Red
    }
    return $true
}

# Modal-leak counter, reported by the end-of-run coverage audit. A stuck
# modal is the highest-blast-radius failure this harness has: it disables
# the main window, so EVERY later Cap silently photographs the same frozen
# dialog under the wrong name (2026-08-19: six shots shipped that way).
$script:modalLeaks = 0

# The three shots of the 2D controller preview. When -Only names these and
# nothing else, STEP 0 saves Use2DControllerView=true so every pad page opens
# in 2D and Set-ViewMode has nothing to click. No click decides which view
# gets photographed. $script:Start2D is decided after STEP 0 splits -Only.
$script:TwoDShots = @('pad-controller-2d', '2d-annotation-overlay', '2d-touchpad-finger-dots')
$script:Start2D = $false
# Set when a view toggle, or an annotation toggle, did not do what was asked.
# From then on the state is unknown, and Cap saves nothing until a fresh app
# process makes both known again (Reset-KnownState): a switch left on, or a
# view left in 2D, would put the wrong picture under any later name.
$script:ViewUnknown = $false
$script:AnnotationUnknown = $false
# Shots not saved because of that, or because a check on the view or the
# overlay failed, and checks that failed with nothing left to refuse. Any of
# them makes the run end red with exit code 1.
$script:RefusedShots = @()
$script:StateFailures = @()

# A staging step that did not do what it asked leaves every later picture of
# PadForge suspect: a device left off its slot, or another left on it. The run
# records it, and Cap saves no more PadForge shots. The two browser shots
# (Cap-Web) go on: they photograph the local web landing page and the Xbox 360
# web controller, which carries no slot lighting, so no slot staging shows in
# them.
$script:StagingFailed = $false

function Assert-Staged {
    param($Result, [string]$What)
    if (@($Result).Count -gt 0 -and @($Result)[-1] -eq $true) { return $true }
    $script:StagingFailed = $true
    $script:StateFailures += "$What failed"
    Write-Host "  !! $What failed. No more PadForge shots are saved." -ForegroundColor Red
    return $false
}

function Refuse-Shots {
    param([string[]]$Names, [string]$Why)
    $wanted = @($Names | Where-Object { Want $_ })
    if ($wanted.Count -eq 0) { return }
    Write-Host "  !! NOT SAVED $($wanted -join ', ') -- $Why" -ForegroundColor Red
    $script:RefusedShots += $wanted
}

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
    if ($script:ViewUnknown -or $script:AnnotationUnknown -or $script:StagingFailed) {
        Refuse-Shots @($Name) "the view, the annotation overlay or the staging is in an unknown state"
        return
    }
    # Warn, loudly and by name, when a dialog is up for a shot that is not
    # a dialog shot. Deliberately does NOT auto-dismiss: several steps
    # legitimately photograph modals, and guessing wrong there would break
    # working captures. The point is that a leak can never again be
    # invisible in the log or the audit.
    #
    # A DISABLED MAIN WINDOW IS WHAT MAKES A DIALOG A LEAK. The old check
    # asked only whether a second top-level window existed, and a WPF
    # ComboBox popup is exactly that: a dialog-sized window of the app's own
    # process. So every shot whose whole subject is an open picker tripped
    # it. The 4.4.0 run flagged joycon-ir-source, joycon2-mouse-sources and
    # gamepad-source-picker, all three correct pictures, and then told the
    # operator that everything captured after them was corrupt. A warning
    # that cries wolf on good work costs the counter the meaning it exists
    # for. ShowDialog disables the owner for as long as it is up and a popup
    # never does, which separates the two exactly.
    if (-not $AllowModal -and $script:hwnd) {
        try {
            $leak = Find-DialogHwndByEnum -Retries 1 -DelayMs 0
            if ($leak -and [IntPtr]$leak -ne [IntPtr]::Zero -and
                -not [Win32]::IsWindowEnabled([IntPtr]$script:hwnd)) {
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
    # "Settings saved to PadForge.xml." holds the status bar's left end for
    # 5 to 10 s after each autosave, and a restart or one of this harness's
    # clicks sets one off. Run 9 of the 5.0.0 prep photographed it on the
    # Dashboard, Profiles, Devices and pad pages. Wait, up to 12 s, for that
    # corner to go dark. The empty bar never reads above 21 on any channel
    # there. The message reads 106 at full strength and still 43 to 58 while
    # it fades out, which a first threshold of 70 let through on ten run-11
    # frames, so anything above 30 counts. Dialog shots wait too, since most
    # dialogs leave the corner in view. One that covers it with something
    # bright costs the 12 s and is captured as it is.
    $sbDeadline = (Get-Date).AddSeconds(12)
    while ((Get-ScreenBrightCount -X ($r.Left + 20) -Y ($r.Bottom - 44) -W 700 -H 28 -Min 30) -ge 20) {
        if ((Get-Date) -gt $sbDeadline) {
            Write-Host "  !! the status bar corner stayed lit for 12 s, so $Name may carry a message" -ForegroundColor Yellow
            break
        }
        Start-Sleep -Milliseconds 500
    }
    # Last, so the frame lands inside the 2 s before the boxes' autosave.
    $aoFrame = $script:AssignOfferFrames -contains $Name
    if ($aoFrame -and -not (Set-AssignOfferBoxes $true)) {
        Refuse-Shots @($Name) "its Assignment Prompts boxes could not be set to their defaults"
        Set-AssignOfferBoxes $false | Out-Null
        return
    }
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
    if ($aoFrame) {
        if (-not (Set-AssignOfferBoxes $false)) {
            Write-Host "  !! the assignment prompts may still be ON, so a later pad-page shot can carry an offer banner" -ForegroundColor Red
        }
        # Two toggles mean one autosave 2 s later. Wait for it, so the next
        # frame's status bar check sees its message.
        Start-Sleep -Milliseconds 2500
    }
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
    # One 500 ms retry was not enough. A slot card click opens the Pad page
    # and the tab strip lands a beat later, so menu-macro-cell and
    # menu-icon-packs failed every run on "Tab 'Menus' not found" while the
    # strip plainly carries Menus. Recipes that happen to do other work
    # between the card click and the tab (selecting a mapped device, say)
    # were passing only because that work spent the time.
    for ($ti = 0; (-not $el) -and $ti -lt 8; $ti++) {
        Start-Sleep -Milliseconds 500
        $padPage = Find-UIA -Aid "PadPageView"
        $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
        $el = Find-UIA -Parent $searchIn -Name $Name -CT ([System.Windows.Automation.ControlType]::RadioButton)
    }
    if (-not $el) { $el = Find-UIARetry -Name $Name -Retries 4 -DelayMs 500 }
    if ($el) {
        $clicked = Click-El $el -Label "Tab:$Name"
        # A rect click on a tab can land and do nothing, the same way the
        # SteamVR Install button did. The Gyro device tab was the case that
        # proved it: the strip carried Gyro, the Wii Remote was the selected
        # device, the click reported success at the tab's own center, and the
        # content pane stayed on Preview through 3.6 s of retries. Confirm the
        # selection took and drive the pattern when it did not.
        try {
            $sip = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if (-not $sip.Current.IsSelected) {
                Write-Host "  .. Tab:$Name did not select on click, using SelectionItemPattern" -ForegroundColor DarkGray
                $sip.Select()
                Start-Sleep -Milliseconds 600
                $clicked = $sip.Current.IsSelected
            }
        } catch {
            # An element without SelectionItemPattern cannot be asked whether
            # it took, so the click's own result stands, as it did before
            # this check existed. Say so: a shot taken on an unverified tab
            # is one to look at twice.
            Write-Host "  .. Tab:$Name selection could not be verified ($($_.Exception.Message))" -ForegroundColor DarkGray
        }
        return $clicked
    }
    # Name what the strip DOES carry. "Tab X not found" on its own sent two
    # shots through four releases with nobody able to say whether the tab was
    # absent, renamed, or just late.
    $ppT = Find-UIA -Aid "PadPageView"
    if ($ppT) {
        $rbT = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $have = @($ppT.FindAll($TC, $rbT) | ForEach-Object { $_.Current.Name })
        Write-Host ("  !! Tab '$Name' not found. Strip carries ({0}): {1}" -f $have.Count, ($have -join " | ")) -ForegroundColor Yellow
    } else {
        Write-Host "  !! Tab '$Name' not found, and PadPageView was not found either" -ForegroundColor Yellow
    }
    # Leave a picture. UIA saying "not there" is one layer, and the Grip card
    # proved that layer can be wrong while the screen is right.
    try {
        [Win32]::ForceFG($script:hwnd)
        Start-Sleep -Milliseconds 300
        $tr = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$tr) | Out-Null
        $tw = $tr.Right - $tr.Left; $th = $tr.Bottom - $tr.Top
        $tbmp = New-Object System.Drawing.Bitmap($tw, $th)
        $tg = [System.Drawing.Graphics]::FromImage($tbmp)
        $tg.CopyFromScreen($tr.Left, $tr.Top, 0, 0, [System.Drawing.Size]::new($tw, $th))
        $tg.Dispose()
        $tp = Join-Path (Join-Path $env:TEMP "PadForge_Capture") (("tabfail-" + ($Name -replace "[^A-Za-z0-9]", "")) + ".png")
        $tbmp.Save($tp, [System.Drawing.Imaging.ImageFormat]::Png)
        $tbmp.Dispose()
        Write-Host "  .. tab-failure screenshot: $tp" -ForegroundColor DarkGray
    } catch { }
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
    # NAME WHAT THE DROPDOWN ACTUALLY HOLDS. Three runs were spent guessing
    # why a device "was not in the dropdown" when the answer is one list.
    $cbDump = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $liDump = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    # EXPAND BEFORE ENUMERATING. A closed WPF ComboBox realizes no items at
    # all, so a dump of one reads "items:" with nothing after it and says
    # nothing about whether the device is in the list.
    foreach ($cb in (Find-AllSafe $padPage $cbDump)) {
        $expDump = $null
        try { $expDump = $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $expDump.Expand() } catch {}
        Start-Sleep -Milliseconds 500
        $items = @()
        foreach ($it in $cb.FindAll($TD, $liDump)) { $items += $it.Current.Name }
        Write-Host ("    combo Aid='{0}' items: {1}" -f $cb.Current.AutomationId, ($items -join ' | '))
        try { $expDump.Collapse() } catch {}
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# A hash of a screen rectangle, for telling whether a click changed a view.
function Get-ScreenStripHash {
    param([int]$X, [int]$Y, [int]$W, [int]$H)
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, [System.Drawing.Size]::new($W, $H))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash($ms.ToArray()))
    $sha.Dispose(); $ms.Dispose()
    return $hash
}

# A screen rectangle's pixels as BGRA bytes, with the row stride. The pad
# page's tab body is invisible to UIA, so these two helpers below read what
# it draws: text on this dark theme is the only bright thing in a row.
function Get-ScreenBytes {
    param([int]$X, [int]$Y, [int]$W, [int]$H)
    $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
    $bmp = New-Object System.Drawing.Bitmap($W, $H, $fmt)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, [System.Drawing.Size]::new($W, $H))
    $g.Dispose()
    $data = $bmp.LockBits([System.Drawing.Rectangle]::new(0, 0, $W, $H),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, $fmt)
    $bytes = New-Object byte[] ($data.Stride * $H)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $stride = $data.Stride
    $bmp.UnlockBits($data); $bmp.Dispose()
    return @{ Bytes = $bytes; Stride = $stride }
}

# How many sampled pixels of a screen rectangle are bright (any channel
# above $Min), every $Step pixels on both axes.
function Get-ScreenBrightCount {
    param([int]$X, [int]$Y, [int]$W, [int]$H, [int]$Step = 2, [int]$Min = 160)
    $s = Get-ScreenBytes $X $Y $W $H
    $b = $s.Bytes; $n = 0
    for ($yy = 0; $yy -lt $H; $yy += $Step) {
        $row = $yy * $s.Stride
        for ($xx = 0; $xx -lt $W; $xx += $Step) {
            $i = $row + $xx * 4
            if ($b[$i] -gt $Min -or $b[$i + 1] -gt $Min -or $b[$i + 2] -gt $Min) { $n++ }
        }
    }
    return $n
}

# The screen Y of the center of the lowest band of bright text in a column
# strip, or $null. Scans up from the strip's bottom: the first pixel row
# with text is the band's bottom, and six text-free rows above it end the
# band. Used on a grid scrolled to its end, where the lowest label is the
# last row's.
function Find-LowestTextBandY {
    param([int]$X, [int]$Y, [int]$W, [int]$H, [int]$Min = 140)
    $s = Get-ScreenBytes $X $Y $W $H
    $b = $s.Bytes
    $bottom = -1; $gap = 0
    for ($yy = $H - 1; $yy -ge 0; $yy--) {
        $row = $yy * $s.Stride; $lit = $false
        for ($xx = 0; $xx -lt $W; $xx += 2) {
            $i = $row + $xx * 4
            if ($b[$i] -gt $Min -or $b[$i + 1] -gt $Min -or $b[$i + 2] -gt $Min) { $lit = $true; break }
        }
        if ($lit) {
            if ($bottom -lt 0) { $bottom = $yy }
            $gap = 0; $top = $yy
        } elseif ($bottom -ge 0) {
            $gap++
            if ($gap -ge 6) { return [int]($Y + ($top + $bottom) / 2) }
        }
    }
    if ($bottom -ge 0) { return [int]($Y + ($top + $bottom) / 2) }
    return $null
}

# Open a slot's pad page from its Dashboard card. The card's center sits on
# its controller-type strip, and a click there changes the slot's type: a
# focused run on 2026-09-22 turned the Xbox slot into an Extended one that
# way and photographed the Dashboard. On a card with a mapped device the
# center lands 2 px below the strip (measured 2026-09-22: strip y 473-505,
# center 507), which is the only reason the full pass never tripped on it.
# The "Slot" title opens the page and touches nothing else. Returns the
# PadPageView once it exists.
function Open-SlotCard {
    param($Card, [string]$Label)
    $title = $null
    try {
        $tcond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        # The title renders as two TextBlocks, "Slot" and the number.
        $title = @($Card.FindAll($TD, $tcond)) |
            Where-Object { $_.Current.Name -eq 'Slot' } | Select-Object -First 1
    } catch {}
    if (-not $title) {
        Write-Host "  !! no 'Slot' title on $Label; not clicking the card body" -ForegroundColor Red
        return $null
    }
    Click-El $title -Label "$Label title" -Delay 1500 | Out-Null
    for ($w = 0; $w -lt 10; $w++) {
        $pp = Find-UIA -Aid "PadPageView"
        if ($pp) { return $pp }
        Start-Sleep -Milliseconds 1000
    }
    Write-Host "  !! $Label did not open its pad page" -ForegroundColor Red
    return $null
}

# An annotation overlay shot: on, photographed, off, each step checked.
# The 2D and 3D views share one switch per pad (PadViewModel.
# AnnotationOverlayEnabled), and a fresh app process starts with it off, so
# the state is known only while every on-click and every off-click is seen to
# work. The check is the model area's pixels: the on-click must change them
# and the off-click must bring back the exact frame from before. A failed step
# sets $script:AnnotationUnknown, and every later annotation shot is skipped,
# because a switch left on would put a bare view under an overlay's name.
#
# The 2D toggle is AutomationId "AnnotationToggle2D". Where UIA does not reach
# it, its tag glyph is right-anchored at window-relative (width - 53, 255) on
# the maximized window, measured 2026-09-22 (the old 0.965 W fell beside the
# finish reset button once the 2D view gained its finish selector). The 3D
# toggle is "AnnotationToggle", just left of Reset View at about 0.925 W,
# 0.161 H (measured off pad-controller-3d at 2582x1550).
#
# On 2026-09-22 the 2D click turned the toggle on and still no chip appeared.
# That was the app: the 2D view laid its chips out on the view's SizeChanged,
# which never fires when the overlay canvas is first shown. The view now
# listens to the canvas (Annotation2DLayoutTriggerTests).
#
# The toggles are plain buttons, with no TogglePattern to read. Two checks
# stand in for one. The pixels say the model changed, and changed back. The
# toggle's own chrome says which way: its glyph and ring turn ColdBrush
# (#58B6E4 on the dark theme, #1E6E9F on the light one, EmberTheme.cs) while
# the overlay is on and carry none of it while it is off
# (UpdateAnnotationToggleChrome in ControllerModelView.Annotations.cs and
# ControllerModel2DView.Annotations.cs). A fresh process starts with the
# overlay off for every pad (session-only, PadPage.xaml.cs), so the chain
# begins from a known state. Live input or an animation that moves the model
# fails the back-off check, which skips later shots and saves nothing wrong.
function Get-ColdPixelCount {
    param([int]$X, [int]$Y, [int]$W, [int]$H)
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, [System.Drawing.Size]::new($W, $H)); $g.Dispose()
    $n = 0
    for ($i = 0; $i -lt $W; $i++) {
        for ($j = 0; $j -lt $H; $j++) {
            $c = $bmp.GetPixel($i, $j)
            if (([math]::Abs($c.R - 0x58) -le 24 -and [math]::Abs($c.G - 0xB6) -le 24 -and [math]::Abs($c.B - 0xE4) -le 24) -or
                ([math]::Abs($c.R - 0x1E) -le 24 -and [math]::Abs($c.G - 0x6E) -le 24 -and [math]::Abs($c.B - 0x9F) -le 24)) { $n++ }
        }
    }
    $bmp.Dispose()
    return $n
}

function Capture-AnnotationOverlay {
    param([string]$Aid, [scriptblock]$FallbackPoint, [string[]]$Shots, [string]$Label)
    if (@($Shots | Where-Object { Want $_ }).Count -eq 0) { return }
    if ($script:AnnotationUnknown -or $script:ViewUnknown -or $script:StagingFailed) {
        Refuse-Shots $Shots "the view, the annotation overlay or the staging is in an unknown state"
        return
    }
    $btn = Find-UIA -Aid $Aid
    if (-not $btn) {
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Start-Sleep -Milliseconds 400
        $btn = Find-UIA -Aid $Aid
    }
    if (-not $btn) { Write-Host "  $Aid not in UIA. Using the coordinate fallback." -ForegroundColor DarkGray }
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $mX = $wr.Left + 420; $mY = $wr.Top + 300
    $press = {
        param([string]$Step)
        if ($btn) { return ((Click-El $btn -Label "$Label $Step" -Delay 900) -eq $true) }
        $pt = & $FallbackPoint $wr
        [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
        [Win32]::ClickAt($pt[0], $pt[1]); Start-Sleep -Milliseconds 900
        return $true
    }
    $frame = {
        [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15)); Start-Sleep -Milliseconds 150
        return (Get-ScreenStripHash $mX $mY 1980 950)
    }
    # The toggle's box: its UIA rectangle, or 36 px around the fallback point.
    $box = $null
    $br = Get-Rect $btn
    if ($br) { $box = @([int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height) }
    if (-not $box) {
        $pt = & $FallbackPoint $wr
        $box = @(($pt[0] - 18), ($pt[1] - 18), 36, 36)
    }
    $cold = { return (Get-ColdPixelCount $box[0] $box[1] $box[2] $box[3]) }
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
    $before = & $frame
    $coldBefore = & $cold
    if ($coldBefore -gt 2) {
        # A fresh process starts off, so this says an earlier step left it on.
        $script:AnnotationUnknown = $true
        Refuse-Shots $Shots "$Label already looks on ($coldBefore ColdBrush pixels)"
        return
    }
    if (-not (& $press "on")) {
        # The click never reached the button, so the switch did not move.
        Refuse-Shots $Shots "$Label click failed"
        return
    }
    $afterOn = & $frame
    $coldOn = & $cold
    if ($afterOn -eq $before -or $coldOn -lt 12) {
        # The model did not change, or the toggle does not show on, so which
        # way the switch moved is unknown.
        $script:AnnotationUnknown = $true
        Refuse-Shots $Shots "$Label overlay did not appear (frame changed: $($afterOn -ne $before), ColdBrush pixels: $coldOn)"
        # Keep the frame for diagnosis, outside the docs folder.
        $dbg = New-Object System.Drawing.Bitmap(($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top))
        $dg = [System.Drawing.Graphics]::FromImage($dbg)
        $dg.CopyFromScreen($wr.Left, $wr.Top, 0, 0, $dbg.Size); $dg.Dispose()
        $dbg.Save((Join-Path $logDir "annotation-miss.png"), [System.Drawing.Imaging.ImageFormat]::Png); $dbg.Dispose()
        return
    }
    foreach ($shot in $Shots) { Cap $shot }
    # Poll the way back, in real time. The press waits only 900 ms scaled
    # by SettleScale (about 400 ms), the overlay can still be fading then,
    # and one exact-match look failed the 3D toggle on the first 5.0.0 run
    # and refused every later shot of STEP 3. The toggle's own chrome is
    # the direct witness of the switch, so it decides: chrome off with the
    # model frame still different (an animation, live input) is a warning,
    # not an unknown state.
    $offPressed = & $press "off"
    $offCold = 99; $offSame = $false
    for ($ow = 0; $offPressed -and $ow -lt 12; $ow++) {
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 250
        $offCold = & $cold
        $offSame = ((& $frame) -eq $before)
        if ($offCold -le 2 -and $offSame) { break }
    }
    if (-not $offPressed -or $offCold -gt 2) {
        $script:AnnotationUnknown = $true
        $script:StateFailures += "$Label did not go back off"
        Write-Host "  !! $Label overlay did not go back off ($offCold ColdBrush pixels). No more PadForge shots are saved until PadForge restarts." -ForegroundColor Red
    } elseif (-not $offSame) {
        Write-Host "  .. $Label reads off on its own chrome, but the model frame still differs from the one before, so the chrome decides" -ForegroundColor Yellow
    }
}

function Capture-2DAnnotationOverlay {
    Capture-AnnotationOverlay -Aid "AnnotationToggle2D" -Label "Annotation toggle (2D)" `
        -Shots @("2d-annotation-overlay") -FallbackPoint { param($r) @(($r.Right - 53), ($r.Top + 255)) }
}

# The pad page's 2D/3D view is a saved setting, AppSettings/Use2DControllerView,
# which PadPage.ViewModeToggle_Click flips and the app writes 2 s after the last
# change. STEP 0 sets it for every run (2D for a run of nothing but 2D shots, 3D
# otherwise), so a fresh app process opens in a known view, and Set-ViewMode
# reads the file for the view the app is in and waits for the file to show the
# view it asked for. Two capture passes shipped the 3D view under all three 2D
# names, and a changed pixel strip only proves that something changed.
function Get-SavedViewMode {
    # A read can land mid-write, so try a few times before giving up.
    for ($i = 0; $i -lt 5; $i++) {
        try {
            [xml]$vx = Get-Content $PadForgeXml -ErrorAction Stop
            $node = $vx.PadForgeSettings.SelectSingleNode("AppSettings/Use2DControllerView")
            if ($null -ne $node -and "$($node.InnerText)".Trim() -eq 'true') { return '2D' }
            return '3D'
        } catch { Start-Sleep -Seconds 1 }
    }
    return $null
}

# A fresh app process knows both states again: the annotation overlay is
# session-only and starts off for every pad, and the view is the saved
# Use2DControllerView that Set-ViewMode reads. Called wherever the harness
# restarts the app.
function Reset-KnownState {
    if (-not ($script:ViewUnknown -or $script:AnnotationUnknown)) { return }
    if ($null -eq (Get-SavedViewMode)) { return }
    $script:ViewUnknown = $false
    $script:AnnotationUnknown = $false
    Write-Host "  fresh PadForge process: the view and the annotation overlay are known again" -ForegroundColor DarkGray
}

# The toggle carries AutomationId "ViewModeToggle" (PadPage.xaml, inside
# ControllerModelHost) but usually has NO UIA PEER AT ALL: the Helix viewport
# host strips its whole subtree from the automation tree (probed 2026-07-30),
# so the coordinate is the normal path. Measured 2026-09-22 from a trimmed
# capture of the maximized window: the button spans window-relative x 409-454,
# y 233-280. PadPageView + (41, 61) stopped hitting it once the slot header
# rows moved inside PadPageView.
function Click-ViewModeToggle {
    $vmBtn = Find-UIA -Aid "ViewModeToggle"
    if (-not $vmBtn) {
        # Re-attach to the window. A stale cached element tree can hide
        # freshly realized peers.
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        $vmBtn = Find-UIA -Aid "ViewModeToggle"
    }
    if ($vmBtn) { return ((Click-El $vmBtn -Label "ViewModeToggle" -Delay 700) -eq $true) }
    Write-Host "  ViewModeToggle has no UIA peer (expected). Using the measured coordinate." -ForegroundColor DarkGray
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$r) | Out-Null
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 100
    [Win32]::ClickAt(($r.Left + 431), ($r.Top + 256))
    Start-Sleep -Milliseconds 900
    [Win32]::MoveTo(($r.Right - 100), ($r.Bottom - 15))
    return $true
}

function Set-ViewMode {
    param([ValidateSet('2D', '3D')][string]$Want)
    if ($script:ViewUnknown) {
        Write-Host "  !! the view is unknown since an earlier toggle. Nothing that depends on it is captured." -ForegroundColor Red
        return $false
    }
    $have = Get-SavedViewMode
    if ($null -eq $have) {
        $script:ViewUnknown = $true
        Write-Host "  !! could not read the saved view from $PadForgeXml" -ForegroundColor Red
        return $false
    }
    if ($have -eq $Want) { return $true }
    if (Click-ViewModeToggle) {
        for ($i = 0; $i -lt 12; $i++) {
            Start-Sleep -Seconds 1
            if ((Get-SavedViewMode) -eq $Want) { Start-Sleep -Milliseconds 600; return $true }
        }
    }
    $script:ViewUnknown = $true
    Write-Host "  !! the view did not change to $Want" -ForegroundColor Red
    return $false
}

# Waits until the settings file holds every assignment the capture needs, as
# the app saved it after the last click. The app saves 2 s after its last change
# (SettingsService.PersistQuietMs, checked every 250 ms), and a kill before that
# loses the change. An assignment already in a reused file proves nothing about
# the changes still waiting, so the file must also stay unwritten for 3 s,
# starting 3 s after the last click: by then any change the clicks made has
# been written. Each pair is a device name part and a controller type, and
# names the first slot of that type, the way Ensure-DeviceAssigned resolves it:
# the Devices page numbers slots in type-group order, and saved pad indexes
# follow creation order, so the two need not agree. Polls by whole seconds,
# which the Start-Sleep proxy leaves unscaled.
function Wait-SavedAssignments {
    param([object[]]$Pairs, [datetime]$LastClick, [int]$TimeoutSec = 20)
    $missing = @("nothing read yet")
    # A monotonic clock, so a wall-clock change cannot stretch the budget.
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $clickAge = ((Get-Date) - $LastClick).TotalSeconds
    # A round sleeps 1 s, then waits 3 s for the file to stay unwritten.
    while ($clock.Elapsed.TotalSeconds + 4 -le $TimeoutSec) {
        Start-Sleep -Seconds 1
        if ($clickAge + $clock.Elapsed.TotalSeconds -lt 3) { continue }
        $missing = @()
        try {
            $w1 = (Get-Item $PadForgeXml -ErrorAction Stop).LastWriteTimeUtc
            [xml]$wx = Get-Content $PadForgeXml -ErrorAction Stop
            $root = $wx.PadForgeSettings
            $names = @{}
            foreach ($d in $root.SelectSingleNode("Devices").ChildNodes) {
                $n = $d.SelectSingleNode("InstanceName"); $g = $d.SelectSingleNode("InstanceGuid")
                if ($n -and $g) { $names[$g.InnerText] = $n.InnerText }
            }
            $assigned = @()
            foreach ($st in $root.SelectSingleNode("UserSettings").ChildNodes) {
                $g = $st.SelectSingleNode("InstanceGuid"); $mt = $st.SelectSingleNode("MapTo")
                if ($g -and $mt -and $names.ContainsKey($g.InnerText)) {
                    $assigned += , @($names[$g.InnerText], "$($mt.InnerText)".Trim())
                }
            }
            # Under AppSettings in the current format, at the root in older
            # ones. Reading only the root threw on a null under StrictMode,
            # and the 5.0.0 run reported an unreadable file for 20 s.
            $typesNode = $root.SelectSingleNode("AppSettings/SlotControllerTypes")
            if (-not $typesNode) { $typesNode = $root.SelectSingleNode("SlotControllerTypes") }
            $types = if ($typesNode) { @($typesNode.ChildNodes | ForEach-Object { "$($_.InnerText)".Trim() }) } else { @() }
            foreach ($p in $Pairs) {
                $pad = [array]::IndexOf($types, "$($p[1])")
                $hit = $pad -ge 0 -and @($assigned | Where-Object { $_[0] -like "*$($p[0])*" -and $_[1] -eq "$pad" }).Count -gt 0
                if (-not $hit) { $missing += "$($p[0]) on the slot of type $($p[1])" }
            }
            if ($missing.Count -gt 0) { continue }
            # Reading the file took time too: the stability wait has to fit.
            if ($clock.Elapsed.TotalSeconds + 3 -gt $TimeoutSec) { break }
            Start-Sleep -Seconds 3
            $stable = (Get-Item $PadForgeXml -ErrorAction Stop).LastWriteTimeUtc -eq $w1
            if ($stable -and $clock.Elapsed.TotalSeconds -le $TimeoutSec) { return $true }
            $missing = @("the file was still being written")
        } catch { $missing = @("the settings file could not be read") }
    }
    Write-Host "  !! the staging was not saved within $TimeoutSec s: $($missing -join '; ')" -ForegroundColor Red
    return $false
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
    # Geometry measured off the 2560x1539 captures of 2026-10-03. No Clear
    # All / Map All: the DualSense stays assigned to slot 1 as the stable row
    # PRIMARY, and each swap-on picker device contributes exactly one
    # sub-source per row. Rows start at 0.2879 H, 0.02538 H apart, below the
    # injected Aim layer's Base/Aim row and SHIFT chip (Open-MappingRow). It
    # uses the X row (output index 2). Clicking a row expands the inline details
    # editor: PRIMARY MODE, COMBINE, then the swap-on device's sub-source
    # row, whose descriptor combo sits 0.1403 H below the row at 0.36 W. The
    # old 0.206 H origin predates the layer row: it opened the Left Shoulder
    # row instead and typed into nothing, so the four picker shots showed a
    # closed row with no source list.
    $rowY = 0.2879 + 2 * 0.02538
    [Win32]::ClickAt([int]($wrP.Left + 0.20 * $pw), [int]($wrP.Top + $rowY * $ph)); Start-Sleep -Milliseconds 1200
    # 0.1202 was measured off 2560x1539 frames. At the 2582x1550 window it
    # landed in the gap above the combo (Primary Mode 0.3926 H, Combine
    # 0.4335 H, the sub-source combo 0.479 H on run 11 of the 5.0.0 prep),
    # so no list opened and all four picker shots showed a closed row.
    $comboX = [int]($wrP.Left + 0.36 * $pw); $comboY = [int]($wrP.Top + ($rowY + 0.1403) * $ph)
    [Win32]::ClickAt($comboX, $comboY); Start-Sleep -Milliseconds 900
    # The first picker after staging can take the click as focus alone: the
    # second targeted run of the 5.0.0 prep left wii-balance-sources with the
    # combo closed while the next three opened. A combo's list is a top-level
    # window of the app's own process, so look for it, and click once more
    # only when it is not up, since a click on an open combo closes it.
    if ([IntPtr](Find-DialogHwndByEnum -MinW 150 -MinH 60 -Retries 2 -DelayMs 300) -eq [IntPtr]::Zero) {
        Write-Host "  picker: the list did not open, clicking again" -ForegroundColor DarkGray
        [Win32]::ClickAt($comboX, $comboY); Start-Sleep -Milliseconds 900
    }
    [System.Windows.Forms.SendKeys]::SendWait($TypeAhead); Start-Sleep -Milliseconds 800  # type-ahead to the gated source
    Write-Host "  picker: expanded X row + opened '$DeviceNamePart' sub-source combo, typed '$TypeAhead'" -ForegroundColor Green
    # -AllowModal: the open dropdown IS the subject of this shot. A WPF combo
    # popup is a separate top-level window of the app's own process, dialog
    # sized, so Find-DialogHwndByEnum counts it and the leak warning fired on
    # three good pictures in the 4.4.0 run (joycon-ir-source,
    # joycon2-mouse-sources, gamepad-source-picker were all correct). A
    # warning that cries wolf on the shots that need it costs the leak counter
    # its meaning, which is the one thing it exists for.
    Cap $ShotName -AllowModal
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 400  # close dropdown
}

# The four picker shots. Each device is swapped onto the Xbox slot (SlotNumber
# 1) beside the DualSense, photographed with its sub-source list open, and
# swapped off again. The tail runs all four on the full run's staging. The
# focused pass runs the ones named after writing the DualSense onto slot 1
# itself, since -Only skips that staging and returns before the tail.
$script:SourcePickerTargets = @(
    @{ Dev = "Balance Board";    Type = "Balance";      Shot = "wii-balance-sources" },
    @{ Dev = "Joy-Con (R)";      Type = "IR Bright";    Shot = "joycon-ir-source" },
    @{ Dev = "Switch 2 Joy-Con"; Type = "Mouse Motion"; Shot = "joycon2-mouse-sources" },
    # Abstract Gamepad descriptor branch (#9): any CapType-Gamepad device's
    # source combo carries the "Gamepad ..." family, and type-ahead scrolls
    # the open popup to it. The DS3 dummy is the swap-on device here.
    @{ Dev = "DualShock 3";      Type = "Gamepad";      Shot = "gamepad-source-picker" }
)
function Capture-SourcePickerSet {
    param([object[]]$Targets)
    foreach ($wp in $Targets) {
        Nav "Devices"; Start-Sleep -Milliseconds 600
        # Skip the whole picker on a failed assign. The 2026-07-30 run hung
        # inside the follow-up Unassign of a device the assign never found
        # (UIA FindAll blocked with no bound), and the rest of the tail
        # (DS3, devices details, workshop, web) never ran.
        $wpOk = Assign-DeviceToSlot -DeviceNamePart $wp.Dev -SlotNumberLabel "1"
        if (-not (Assert-Staged $wpOk "assigning $($wp.Dev) to slot 1")) { continue }
        Start-Sleep -Milliseconds 800
        Nav "Dashboard"; Start-Sleep -Milliseconds 900
        $shS = Find-UIA -Aid "SlotsItemsControl"
        $cdS = if ($shS) { @($shS.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
        if ((Get-Count $cdS) -ge 1) {
            Open-SlotCard $cdS[0] "Xbox card (picker $($wp.Dev))" | Out-Null
            Capture-SourcePicker -DeviceNamePart $wp.Dev -TypeAhead $wp.Type -ShotName $wp.Shot
        } else {
            Write-Host "  !! Xbox slot card not found for $($wp.Dev)" -ForegroundColor Yellow
        }
        Nav "Devices"; Start-Sleep -Milliseconds 600
        Assert-Staged (Assign-DeviceToSlot -DeviceNamePart $wp.Dev -SlotNumberLabel "1" -Unassign) "unassigning $($wp.Dev) from slot 1" | Out-Null
    }
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
    # the window's center, and once the VR slot made the Dashboard's card
    # grid one row taller that center landed on the ADD CONTROLLER card.
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
            # DECLINE BEFORE ACCEPT. On a two-button confirm, OK is the
            # AFFIRMATIVE: the Clone Device 1:1 dialog offers OK and Cancel,
            # and a scan that took whichever matched first CONFIRMED the
            # clone, rewriting the Extended slot as a passthrough descriptor
            # behind three later shots. Try the declining verbs first and
            # only fall back to OK when a dialog has nothing else, which is
            # the acknowledge-only shape where OK declines nothing.
            try {
                foreach ($pattern in @('^(Cancel|No)$', '^(Close|Dismiss)$', '^(OK|Done)$')) {
                    foreach ($b in $m.FindAll($TD, $btnCond)) {
                        if ($b.Current.Name -match $pattern) {
                            try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
                            catch { Click-El $b -Label "modal '$($b.Current.Name)'" -Delay 400 | Out-Null }
                            $done = $true; break
                        }
                    }
                    if ($done) { break }
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
# The prior values live on disk, not just in this shell. A run that is
# killed before STEP 4 leaves both keys at 0, and the NEXT run then reads 0
# as "the value to put back" and restores the suppression permanently. That
# happened during the 4.4.0 capture: a killed run left
# NOC_GLOBAL_SETTING_TOASTS_ENABLED at 0 while ToastEnabled had already been
# restored to 1, and the two keys disagreeing is the signature. Same shape as
# the settings backup guard above: a leftover file means an earlier run died,
# so put its values back BEFORE reading the current ones. The marker is
# deleted only after every key is back, so a restore that throws leaves the
# file on disk and the next run retries instead of recording the suppressed
# zeros as the values to put back.
$toastPriorPath = Join-Path $logDir "toast-prior.json"
if (Test-Path $toastPriorPath) {
    Write-Host "  A previous run died before restoring toasts; putting its values back first" -ForegroundColor Yellow
    try {
        $stale = Get-Content $toastPriorPath -Raw | ConvertFrom-Json
        foreach ($tk in $toastKeys) {
            $k = "$($tk.Path)|$($tk.Name)"
            # Set-StrictMode -Version Latest turns a missing property into a
            # terminating error, and a zero-length file leaves $stale itself
            # $null. Probe the property bag first so one absent key is read as
            # "the value was absent before" instead of aborting the loop and
            # stranding the other key at 0.
            $has = ($null -ne $stale) -and ($stale.PSObject.Properties.Name -contains $k)
            $v = if ($has) { $stale.$k } else { $null }
            if ($null -eq $v) { Remove-ItemProperty -Path $tk.Path -Name $tk.Name -EA SilentlyContinue }
            else { Set-ItemProperty -Path $tk.Path -Name $tk.Name -Value ([int]$v) -Type DWord }
        }
        Remove-Item $toastPriorPath -Force -EA SilentlyContinue
    } catch { Write-Host "  !! could not read $toastPriorPath : $_" -ForegroundColor Yellow }
}
$toastPrior = @{}
foreach ($tk in $toastKeys) {
    try {
        if (-not (Test-Path $tk.Path)) { New-Item -Path $tk.Path -Force | Out-Null }
        $cur = (Get-ItemProperty -Path $tk.Path -Name $tk.Name -EA SilentlyContinue).($tk.Name)
        $toastPrior["$($tk.Path)|$($tk.Name)"] = $cur   # $null = value absent before
        Set-ItemProperty -Path $tk.Path -Name $tk.Name -Value 0 -Type DWord
    } catch { Write-Host "  !! toast suppress failed for $($tk.Name): $_" -ForegroundColor Yellow }
}
try { $toastPrior | ConvertTo-Json | Set-Content -Path $toastPriorPath -Encoding utf8 }
catch { Write-Host "  !! could not write $toastPriorPath : $_" -ForegroundColor Yellow }
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
# Decided here, after the split, so "-Only a,b" counts the way "-Only a, b" does.
$script:Start2D = ($Only.Count -gt 0) -and
    (@($Only | Where-Object { $script:TwoDShots -notcontains $_ }).Count -eq 0)
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
        # motion) along with the Pitch and Roll Simulation card (#474,
        # SimulatedGyro.MissingAxes). Keeps gamepad DeviceObjects so the
        # mapping grid has rows. Named "DualShock 3", the name PadForge
        # gives the pad and the one its cached record carries: the old
        # "PLAYSTATION(R)3 Controller" is a Windows HID string the app no
        # longer shows, and four shots shipped with it. A machine with a
        # cached DualShock 3 keeps its real record, which the dedupe below
        # prefers.
        $ds3 = New-WiiDevice "ffff1111-2222-3333-4444-555566667777" "DualShock 3" 1356 616 "\\?\bthps3bus#{53f88889-1aaf-4353-a047-556b69ec6da6}&dev&vid_054c&pid_0268#a&dummy&1&bt"
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
        # Analog keyboard (#468). CapType 38 (InputDeviceType.AnalogKeyboard)
        # turns the Devices-page preview into the Key Depth chips. 1532:02A6
        # is the Razer Huntsman V3 Pro, the name AnalogKeyboardCatalog
        # .ModelName gives that ID, and the path takes the reader's
        # analogkb:// form (AnalogKeyboardDevice).
        Add-DeviceOnce (New-SyntheticDevice "cdcd1111-2222-3333-4444-555566667777" "Razer Huntsman V3 Pro" 5426 678 "analogkb://1532:02a6:dummy" 38 0 0 0)

        Write-Host "  Injected synthetic G29 wheel + MIDI Keyboard + 3 Wii-family + DS3 + Steam Controller + Xbox GIP + Wii Remote + NFC + DualSense + PlayStation Move + Microphone + analog keyboard" -ForegroundColor Green
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
    # Macro 6: "Aim Layer" (#377). One SwitchLayer action so the layer
    # dropdown renders for the macro-switch-layer capture. The action DTO is
    # ActionData's declared order (SettingsService.cs): Type first, then
    # SwitchLayerMask, which sits at position 38 and therefore after Type
    # whichever other fields are present. SwitchLayerMask is a layer MASK,
    # not a display name, so it must equal the ShiftActivator's LayerMask on
    # the same slot or the dropdown opens on a layer the slot never declared.
    # Write-SlotStructures below authors that activator on slot 0, the slot
    # every one of these macros rides.
    # Trigger = both face buttons X + Y (16384 + 32768), TWO chips, keeping
    # the trigger block the same height as Sleep Controller so the action-row
    # coordinate the other macro captures use stays valid for this one.
    $m6Xml = @'
<Macro PadIndex="0">
  <Name>Aim Layer</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>49152</TriggerButtons>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>SwitchLayer</Type><SwitchLayerMask>Aim</SwitchLayerMask></Action>
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
    # Macro 7: "Key Glow" (#468). One SetChromaColor action so the color
    # card renders for the macro-set-chroma-color capture. The action
    # reuses the lightbar fields for its color (MacroItem binds
    # LightbarR/G/B on this card), written in ActionData's declared order:
    # Type, DurationMs, then LightbarR, LightbarG, LightbarB. The color is
    # the app's ember, #FF6B2C. Trigger = D-pad Left + D-pad Right (4 + 8),
    # two chips, the same trigger block height as the macros above.
    $m7Xml = @'
<Macro PadIndex="0">
  <Name>Key Glow</Name>
  <IsEnabled>true</IsEnabled>
  <TriggerButtons>12</TriggerButtons>
  <TriggerSource>OutputController</TriggerSource>
  <TriggerMode>OnPress</TriggerMode>
  <ConsumeTriggerButtons>true</ConsumeTriggerButtons>
  <RepeatMode>Once</RepeatMode>
  <Actions>
    <Action><Type>SetChromaColor</Type><DurationMs>1000</DurationMs><LightbarR>255</LightbarR><LightbarG>107</LightbarG><LightbarB>44</LightbarB></Action>
  </Actions>
</Macro>
'@
    $frag6 = $xml.CreateDocumentFragment(); $frag6.InnerXml = $m6Xml.Trim()
    $macrosNode.AppendChild($frag6) | Out-Null
    $frag7 = $xml.CreateDocumentFragment(); $frag7.InnerXml = $m7Xml.Trim()
    $macrosNode.AppendChild($frag7) | Out-Null
    Write-Host "  Injected 7 test macros"
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

    # Both assignment prompts OFF for the run. Every Windows microphone is an
    # input device (#317), and this settings copy has never seen the PC's
    # microphone, so PadForge offered it as newly connected on every pad
    # page and the banner sat across the top of the frame. The real pads the
    # run assigns are new to this copy too. The one shot of the card that
    # governs the prompts turns them back on for its frame
    # (Set-AssignOfferBoxes). The owner's values ride the backup.
    foreach ($aoName in @("AssignOfferNewDevice", "AssignOfferEmptySlot")) {
        $aoNode = $appSettings.SelectSingleNode($aoName)
        if (-not $aoNode) {
            $aoNode = $xml.CreateElement($aoName)
            $appSettings.AppendChild($aoNode) | Out-Null
        }
        $aoNode.InnerText = "false"
    }
    Write-Host "  Set AssignOfferNewDevice=false, AssignOfferEmptySlot=false for capture"

    # Every run starts from a known view, because Set-ViewMode reads it from
    # this file: 2D for a run of nothing but 2D shots, which then never
    # toggles, 3D for every other run. The owner's value rides the backup and
    # is restored in STEP 4.
    $v2Node = $appSettings.SelectSingleNode("Use2DControllerView")
    if (-not $v2Node) {
        $v2Node = $xml.CreateElement("Use2DControllerView")
        $appSettings.AppendChild($v2Node) | Out-Null
    }
    $v2Node.InnerText = if ($script:Start2D) { "true" } else { "false" }
    Write-Host "  Set Use2DControllerView=$($v2Node.InnerText)"

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

    # Head tracking (#355). The Head Tracker (OpenTrack) device row exists
    # only while the master switch is on: with it off there is no row, no
    # socket and no thread, so devices-head-tracking would photograph a
    # Devices page that cannot contain what the shot is named after. The
    # Dashboard section renders either way, but its status line reads
    # "Stopped" while the feature is off, and the docs page shows it live.
    # This is the harness's own rule at work: the state is one bool in the
    # settings file, so write it rather than clicking for it. The owner's
    # real value rides the backup and is restored in STEP 4.
    $htNode = $appSettings.SelectSingleNode("HeadTrackingEnabled")
    if ($htNode) { $htNode.InnerText = "true" }
    else {
        $htNode = $xml.CreateElement("HeadTrackingEnabled")
        $htNode.InnerText = "true"
        $appSettings.AppendChild($htNode) | Out-Null
    }
    Write-Host "  Set HeadTrackingEnabled=true (Head Tracker device row + live status)"

    # Three 5.0.0 switches that decide what a shot can show, written the
    # same way. The web controller's plain HTTP address draws its port,
    # access code, status and QR only while it serves. The analog keyboard
    # and Bliss-Box readers print their status lines in the Settings Input
    # Engine card only while they run. The access code is the capture
    # file's own: it is stored encrypted in PadForge.xml, which STEP 0
    # regenerated, so the owner's code never reaches a picture. The owner's
    # values ride the backup and are restored in STEP 4.
    foreach ($sw in @("EnableWebControllerPlainHttp", "AnalogKeyboardsEnabled", "BlissBoxEnabled")) {
        $swNode = $appSettings.SelectSingleNode($sw)
        if (-not $swNode) {
            $swNode = $xml.CreateElement($sw)
            $appSettings.AppendChild($swNode) | Out-Null
        }
        $swNode.InnerText = "true"
        Write-Host "  Set $sw=true"
    }

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

# Tail mode skipped STEP 0 and reuses the file an earlier run left, so the
# starting view is written here the same way. The owner's backup is not touched.
if (-not $script:doSetup) {
    [xml]$tx = Get-Content $PadForgeXml
    $tApp = $tx.PadForgeSettings.SelectSingleNode("AppSettings")
    if ($tApp) {
        $tNode = $tApp.SelectSingleNode("Use2DControllerView")
        if (-not $tNode) {
            $tNode = $tx.CreateElement("Use2DControllerView")
            $tApp.AppendChild($tNode) | Out-Null
        }
        $tNode.InnerText = if ($script:Start2D) { "true" } else { "false" }
        $tx.Save($PadForgeXml)
        Write-Host "  Tail mode: set Use2DControllerView=$($tNode.InnerText) in the reused file"
    } else {
        Write-Host "  !! tail mode: no AppSettings in $PadForgeXml, so the starting view is unknown" -ForegroundColor Red
        $script:ViewUnknown = $true
    }
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

# Verify slots appeared. The UIA tree can go stale right after the
# type-group reorder: run 8 of the 5.0.0 prep logged "Operation is not
# valid due to the current state of the object" from the window's
# FindFirst, then every lookup came back empty, Nav 'Devices' included, and
# the run refused every shot after it. A fresh process gets a fresh tree,
# and the slots are on disk by now (this wait outlasts the 2 s autosave).
$slots = @(Find-AllSlots)
Write-Host "  Slots after creation: $(Get-Count $slots)"
if ((Get-Count $slots) -ne $slotTypes.Count) {
    Write-Host "  !! expected $($slotTypes.Count) slots, refreshing the UIA tree" -ForegroundColor Yellow
    if (Reset-PadForgeUia -ExePath $PadForgeExe) {
        $slots = @(Find-AllSlots)
        Write-Host "  Slots after the refresh: $(Get-Count $slots)"
    }
    if ((Get-Count $slots) -ne $slotTypes.Count) {
        throw "Expected $($slotTypes.Count) slots after creation, found $(Get-Count $slots)"
    }
}

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
    # band, and clicking its center lands on the chips instead: that is how the
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
    # Search the rows already drawn first, which keeps a find of a row near
    # the current position from moving the list at all.
    $wrA = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrA) | Out-Null
    $lx = [int]($wrA.Left + 400); $my = [int](($wrA.Top + $wrA.Bottom) / 2)
    # Only accept a card whose rect is actually INSIDE the viewport. A
    # virtualized row can report a rect above/below the visible list (the
    # 2026-07-12 run clicked a DualSense card at Y=-749, the click landed
    # nowhere, and the unassign silently no-oped). On an off-screen match,
    # scroll TOWARD it and re-find instead of clicking a phantom rect.
    $target = $null
    $triedIntoView = $false
    $listTop = Get-DeviceListTop
    # A miss among the rows already drawn scrolls the list to the TOP once,
    # then the search steps down, as Select-DeviceByName36 does. The list is
    # alphabetical with the merged devices last, and this search used to step
    # down only, from wherever the last assignment left it. Once the owner's
    # cache reached 43 devices, the G29 ("L") sat above the rows the Xbox GIP
    # assignment ("X") left drawn, so it could never be reached, and the
    # 5.0.0 run's staging failed on it twice. Sixty steps walk a 60-row list
    # end to end.
    $initial = @(Find-AllSafe $searchIn $liCond)
    $drawnHit = @($initial | Where-Object { try { $_.Current.Name -like "*$DeviceNamePart*" } catch { $false } })
    if ($drawnHit.Count -eq 0) {
        foreach ($it in $initial) {
            $ir = Get-Rect $it
            if ($null -ne $ir) { $lx = [int]($ir.X + $ir.Width / 2); break }
        }
        [Win32]::ForceFG($script:hwnd)
        for ($u = 0; $u -lt 40; $u++) { [Win32]::ScrollAt($lx, $my, 3); Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 400
    }
    for ($stry = 0; $stry -lt 60 -and (-not $target); $stry++) {
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
            # 100 px off the bottom, as Select-DeviceByName36 keeps it. Run 9
            # of the 5.0.0 prep accepted the Wii Remote's card ending 62 px
            # above the window's edge, clicked it, and the list never
            # selected it, so the Wii Remote went unassigned.
            if ($null -ne $fr -and $fr.Y -ge $listTop -and ($fr.Y + $fr.Height) -le ($wrA.Bottom - 100)) {
                $target = $found
            } else {
                # Ask the list to bring the row up first, as Select-DeviceByName36
                # does. A null rect means the row is virtualized out of view, on
                # EITHER side: this used to scroll up for it as if it sat above
                # the viewport, and once the owner's cache reached 43 devices the
                # G29 and the Wii Remote sat below it, so 24 steps scrolled the
                # wrong way and the 5.0.0 run's staging failed on the G29.
                # Once per search: a row ScrollIntoView left just outside the
                # strict bounds is then moved by its own rect.
                $intoView = $false
                if (-not $triedIntoView) {
                    $triedIntoView = $true
                    try {
                        $found.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
                        Start-Sleep -Milliseconds 400
                        $intoView = $true
                    } catch {}
                }
                if (-not $intoView) {
                    $dir = if ($null -ne $fr -and $fr.Y -lt $listTop) { 3 } else { -3 }  # positive scrolls up
                    [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($lx, $my, $dir); Start-Sleep -Milliseconds 350
                }
            }
        } else {
            [Win32]::ForceFG($script:hwnd); [Win32]::ScrollAt($lx, $my, -3); Start-Sleep -Milliseconds 350
        }
    }
    if (-not $target) {
        Write-Host "  !! Device matching '$DeviceNamePart' not found on-screen after scroll" -ForegroundColor Yellow
        # Name what the list holds, with each row's position, so a repeat says
        # whether the row is absent, virtualized or off screen.
        foreach ($it in (Find-AllSafe $searchIn $liCond)) {
            $rr = Get-Rect $it
            $yy = if ($null -eq $rr) { "no-rect" } else { "y=$([int]$rr.Y)" }
            Write-Host "    [$($it.Current.Name)] $yy" -ForegroundColor DarkGray
        }
        return $false
    }
    Write-Host "  Found device card '$DeviceNamePart'"
    if ((Click-El $target -Label "Device card '$DeviceNamePart'" -Delay 1000) -ne $true) {
        Write-Host "  !! the device card click did not land for $DeviceNamePart" -ForegroundColor Red
        return $false
    }

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
    # The toggles act on whichever device the list has selected, so the card
    # asked for must be the selected one (DevicesPage.xaml binds the ListBox's
    # SelectedItem), not merely the one clicked.
    $isSelected = $false
    try { $isSelected = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected } catch {}
    if (-not $isSelected) {
        Write-Host "  !! '$DeviceNamePart' is not the selected device, so its slot toggles are not touched" -ForegroundColor Red
        return $false
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
            if ((Click-El $btn -Label "Slot $SlotNumberLabel toggle OFF (re-assert $DeviceNamePart)" -Delay 900) -ne $true) {
                Write-Host "  !! the re-assert click did not land for $DeviceNamePart" -ForegroundColor Red
                return $false
            }
        }
        if ((Click-El $btn -Label "Slot $SlotNumberLabel toggle ($DeviceNamePart)" -Delay 900) -ne $true) {
            Write-Host "  !! the slot $SlotNumberLabel click did not land for $DeviceNamePart" -ForegroundColor Red
            return $false
        }
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
# The full name: a bare "DualSense" also matches the web-controller lane's
# "DualSense Web Controller 1" rows, which sort ahead of the pad.
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "DualSense Wireless Controller" -SlotNumberLabel "1") "assigning DualSense Wireless Controller to slot 1" | Out-Null
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "DualSense Wireless Controller" -SlotNumberLabel "2") "assigning DualSense Wireless Controller to slot 2" | Out-Null
# Also put the Xbox Series X and the synthetic G29 wheel on the Xbox slot
# (SlotNumber 1), beside the DualSense. DualSense stays the default selection
# (alphabetically first), so the main Xbox-slot captures are unchanged; the
# Impulse-Triggers and Wheel captures at the end of that section switch the
# mapped-device dropdown to the Xbox pad / the wheel to surface their tabs.
# Best effort: Ensure-DeviceAssigned below writes this one, and that is checked.
Assign-DeviceToSlot -DeviceNamePart "Xbox Series X GIP" -SlotNumberLabel "1" -Reassert | Out-Null
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Logitech G29" -SlotNumberLabel "1") "assigning Logitech G29 to slot 1" | Out-Null
# Mouse on the KBM slot (SlotNumber 4) for the #200 Mouse-gestures tab. The Mouse
# tab gates on the SELECTED device being IsMouse (CapType == Mouse == 18); a KBM
# slot with no device assigned surfaces no Mouse tab. "All Mice (Merged)" is a
# first-class UserDevice (CapType 18) in the cache, so assigning + selecting it
# lights TabMouse, the same shape the Wheel tab uses (assign G29, select it).
# Best effort: Ensure-DeviceAssigned below writes this one, and that is checked.
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
# Best effort: Ensure-DeviceAssigned below writes this one, and that is checked.
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

# The app saves 2 s after its last change, and Ensure-DeviceAssigned below
# kills it first thing, which would lose whatever was not saved yet: the
# assignments above and the mapping rows auto-map made for them. Wait until
# the clicked assignments are in the file and it has gone quiet.
Assert-Staged (Wait-SavedAssignments -LastClick (Get-Date) -Pairs @(
    @("DualSense Wireless Controller", 0), @("DualSense Wireless Controller", 1),
    @("Logitech G29", 0))) "saving the staged assignments" | Out-Null

# The Xbox GIP dummy is the one assignment the UI chain never lands, and two
# shots depend on it: the Impulse Triggers tab gates on HasRumbleTriggers and
# the Guide Button LED card wants an Xbox pad selected. Write the mapping
# instead of clicking for it. This runs at a clean boundary, before any
# capture, so the restart costs nothing and the full multi-slot topology the
# rest of the gallery shows is untouched.
Assert-Staged (Ensure-DeviceAssigned -DeviceNamePart "Xbox Series X GIP" -PadIndex 0 -SlotType 0 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe) "writing the Xbox Series X GIP assignment" | Out-Null

# Same treatment for the mouse. The Mouse tab gates on the SELECTED device
# being IsMouse, so pad-mouse-gestures needs "All Mice (Merged)" reachable in
# the KBM slot's device dropdown. The UI toggle assigned it and the dropdown
# still did not list it, exactly as with the Xbox GIP, and the shot has never
# been captured as a result.
#
# PAD INDEX IS CREATION ORDER, AND $slotTypes ABOVE CREATES KBM BEFORE
# EXTENDED: Xbox 0, PlayStation 1, Nintendo 2, KBM 3, Extended 4. The comment
# here said Extended 3 / KBM 4, which is the dashboard CARD order, and the
# number followed the comment: the 4.4.0 run wrote the mouse onto pad 4 and
# the Extended slot grew a Mouse tab while the KBM slot had no mouse to
# select. The SlotType argument is meant to catch exactly this, and it did
# not fire, so do not lean on it. Read the creation list above.
Assert-Staged (Ensure-DeviceAssigned -DeviceNamePart "All Mice (Merged)" -PadIndex 3 -SlotType 4 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe) "writing the All Mice (Merged) assignment" | Out-Null

# The Wii Remote, by the same route and for the same reason. Its UI toggle
# reported "Assigned Wii Remote to slot 4" in the 4.4.0 run and wrote NO
# UserSetting row at all, so the Pointer tab was unreachable on every slot
# and pad-pointer, wii-pointer-mode and pad-gyro-grip were all skipped. That
# is the fourth device to lose the card-scroll / detail-realize / toggle
# chain, and the answer has been the same every time: an assignment is one
# XML field, so write it. Extended is pad index 4 in creation order.
Assert-Staged (Ensure-DeviceAssigned -DeviceNamePart "Wii Remote" -PadIndex 4 -SlotType 2 `
    -XmlPath $PadForgeXml -ExePath $PadForgeExe) "writing the Wii Remote assignment" | Out-Null

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

# Scroll until EVERY named anchor is inside the window at the same time.
# Scroll-ToAnchor answers "has this card reached the screen", which is the
# wrong question for a shot that frames two neighboring sections: stopping at
# the first anchor ships the pair with its second half below the fold. The
# 4.4.0 Dashboard puts Lightbar Mirrors and Razer Sensa HD Haptics next to
# each other and one docs page shows both, so the gate has to be "all of
# them", and a run that cannot get there says so by name instead of
# photographing whatever it reached.
function Scroll-ToAnchors {
    param([string[]]$Anchors, [int]$MaxSteps = 40, [int]$ClicksPerStep = 3)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $top = $wr.Top + 80
    $bot = $wr.Bottom - 80
    $want = (Get-Count $Anchors)
    for ($i = 0; $i -le $MaxSteps; $i++) {
        $seen = 0
        foreach ($a in $Anchors) {
            $r = Get-Rect (Find-UIA -Name $a)
            if ($null -ne $r -and $r.Y -ge $top -and ($r.Y + $r.Height) -le $bot) { $seen++ }
        }
        if ($seen -eq $want) {
            Write-Host "  all $want anchor(s) in view after $i step(s): $($Anchors -join ' + ')"
            Start-Sleep -Milliseconds 500
            return $true
        }
        ScrollContent -Clicks (-1 * $ClicksPerStep)
        Start-Sleep -Milliseconds 200
    }
    Write-Host "  !! never got all of [$($Anchors -join ', ')] into one frame" -ForegroundColor Red
    return $false
}

# The Updates card (#457). Its status line stays empty until a check has run,
# so the shot clicks Check Now and waits for GitHub's answer first. The frame
# must hold the whole card, from its title to the Check Now button. A check
# that never answers skips the shot rather than photographing "Checking".
$script:UpdateAnswers = @("PadForge * is up to date.", "PadForge * is available.",
    "Pre-release * is available.", "Could not check for updates: *",
    "GitHub is limiting requests from this network. Try again later.",
    "The newest version has no build for this PC's processor yet.")
function Capture-UpdatesCard {
    if (-not (Want "settings-updates")) { return }
    ScrollContent -Clicks 90
    if (-not (Scroll-ToAnchors -Anchors @("Updates", "Check Now"))) {
        Write-Host "  !! SKIPPED settings-updates" -ForegroundColor Red
        return
    }
    $btn = Find-UIA -Name "Check Now" -CT ([System.Windows.Automation.ControlType]::Button)
    if (-not $btn) {
        Write-Host "  !! no Check Now button -- SKIPPED settings-updates" -ForegroundColor Red
        return
    }
    Click-El $btn -Label "Check Now" -Delay 500 | Out-Null
    $textCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $answer = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $answer -and $sw.Elapsed.TotalSeconds -lt 30) {
        foreach ($t in $script:uiaWin.FindAll($TD, $textCond)) {
            try { $name = $t.Current.Name } catch { continue }
            if (@($script:UpdateAnswers | Where-Object { $name -like $_ }).Count -gt 0) { $answer = $name; break }
        }
        if (-not $answer) { Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 500 }
    }
    if (-not $answer) {
        Write-Host "  !! the update check never answered in 30 s -- SKIPPED settings-updates" -ForegroundColor Red
        return
    }
    Write-Host "  update check answered: $answer"
    # The answer adds a line above the button, so frame the card again. Stop
    # on a card boundary, one wheel notch at a time from the top, with the
    # Appearance title just under the top edge: Appearance, Window and the
    # whole Updates card then sit in the frame. ScrollContent moves three
    # notches per step, and stopping on that grid cut the Language title in
    # half at the top edge.
    ScrollContent -Clicks 90
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $cx = [int](($wr.Left + $wr.Right) / 2 + 100)
    $cy = [int](($wr.Top + $wr.Bottom) / 2)
    for ($i = 0; $i -lt 60; $i++) {
        $a = Get-Rect (Find-UIA -Name "Appearance")
        if ($null -eq $a -or $a.Y -le ($wr.Top + 150)) { break }
        [Win32]::ScrollAt($cx, $cy, -1)
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 150
    }
    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 500
    $a = Get-Rect (Find-UIA -Name "Appearance")
    $b = Get-Rect (Find-UIA -Name "Check Now" -CT ([System.Windows.Automation.ControlType]::Button))
    $title = Get-Rect (Find-UIA -Name "Updates")
    if ($null -ne $a -and $a.Y -ge ($wr.Top + 90) -and $null -ne $title -and
        $null -ne $b -and ($b.Y + $b.Height) -le ($wr.Bottom - 80)) {
        Cap "settings-updates"
    } else {
        Write-Host "  !! the Updates card never fit one frame -- SKIPPED settings-updates" -ForegroundColor Red
    }
}

# Wheel over a chosen fraction of the window width instead of its center.
# WPF routes the wheel to whatever the pointer is over, and the Devices page
# is two independent scroll viewers: the card list on the left, the device
# dossier on the right. ScrollContent hovers the center, which is the LIST,
# so a control low in the dossier (the Power section and its Quick Charge
# row) could never be reached by it no matter how many clicks were spent.
function ScrollPane {
    param([double]$XFraction = 0.86, [int]$Clicks = -6)
    $sr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$sr) | Out-Null
    $cx = [int]($sr.Left + $XFraction * ($sr.Right - $sr.Left))
    $cy = [int](($sr.Top + $sr.Bottom) / 2)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::MoveTo($cx, $cy)
    Start-Sleep -Milliseconds 300
    $step = if ($Clicks -lt 0) { -3 } else { 3 }
    $count = [math]::Abs([math]::Ceiling($Clicks / $step))
    for ($i = 0; $i -lt $count; $i++) {
        [Win32]::ScrollAt($cx, $cy, $step)
        Start-Sleep -Milliseconds 50
    }
    Start-Sleep -Milliseconds 500
}

# Scroll-ToAnchor for a side pane. Same contract: the shot is taken only
# once the thing it is named after is on screen.
function Scroll-PaneToAnchor {
    param([string]$Anchor, [double]$XFraction = 0.86,
          [int]$MaxSteps = 25, [int]$ClicksPerStep = 3)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $top = $wr.Top + 80
    $bot = $wr.Bottom - 80
    for ($i = 0; $i -le $MaxSteps; $i++) {
        $r = Get-Rect (Find-UIA -Name $Anchor)
        if ($null -ne $r -and $r.Y -ge $top -and ($r.Y + $r.Height) -le $bot) {
            Write-Host "  pane anchor '$Anchor' in view after $i step(s)"
            Start-Sleep -Milliseconds 400
            return $true
        }
        ScrollPane -XFraction $XFraction -Clicks (-1 * $ClicksPerStep)
    }
    Write-Host "  !! pane anchor '$Anchor' never came into view" -ForegroundColor Red
    return $false
}

# Pick a HIDMaestro preset by its EXACT catalog name. The PlayStation block
# matches a fragment because one DualSense entry is wanted out of two, but
# the Valve family cannot be matched that way: "Steam Deck Controller" is a
# prefix of "Steam Deck Controller (Composite)" and "Steam Controller
# (2026)" sits beside four other Steam Controller entries, so a fragment
# match would silently photograph the wrong persona under the right name.
# The combo carries an AutomationId and real ListItem peers (unlike the
# per-card combos inside a tab), so this is ordinary UIA.
function Select-Preset {
    param([string]$ExactName)
    # HMaestroProfileBar ships Visibility=Collapsed and the code-behind shows
    # it once the slot's category is known, and a collapsed element has NO
    # UIA peer at all. So a lookup the instant after a card click reports "no
    # combo" for a bar that is about to appear, which is what happened to
    # both Valve shots in the 4.4.0 run. The Extended block that works lands
    # on the Preview tab first and only then reads the bar. Do the same, and
    # retry rather than believing one null.
    $combo = $null
    for ($sp = 0; $sp -lt 6 -and -not $combo; $sp++) {
        if ($sp -gt 0) {
            Start-Sleep -Milliseconds 700
            $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        }
        $padPage = Find-UIA -Aid "PadPageView"
        if (-not $padPage) { continue }
        if ($sp -eq 0) {
            $rbSp = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            $tabsSp = $padPage.FindAll($TC, $rbSp)
            if ((Get-Count $tabsSp) -gt 0) { Click-El $tabsSp[0] -Label "Preview tab (preset)" -Delay 1200 | Out-Null }
        }
        # TWO CONTROLS, ONE JOB. The Xbox / PlayStation / Nintendo slots put
        # the preset in the top chip bar as HMaestroProfileCombo. An EXTENDED
        # slot renders its own ExtendedConfigBar instead, and the picker
        # KbmSurfacesCombo is the Keyboard + Mouse slot's preset: HMaestroProfileCombo
        # is COLLAPSED on that slot type and collapsed elements are invisible to
        # UIA, so without this id the harness reports no preset combo on a slot
        # whose picker is plainly on screen (#408).
        # there is ExtendedProfileCombo (PadPage.xaml:808, x:Name only, which
        # WPF surfaces as the AutomationId). Looking for the chip-bar id
        # alone is why both Valve shots reported "no combo" on a slot whose
        # picker was plainly on screen, and why the Extended block's own
        # switch-to-Custom step has been silently doing nothing.
        foreach ($aid in @("ExtendedProfileCombo", "HMaestroProfileCombo", "KbmSurfacesCombo")) {
            $combo = Find-UIA -Parent $padPage -Aid $aid
            if ($combo) { break }
        }
    }
    if (-not $combo) {
        Write-Host "  !! Select-Preset: no preset combo after 6 tries" -ForegroundColor Red
        # Say WHAT IS THERE. A miss with no inventory beside it is a guess
        # about why, and this harness has burned two runs on such guesses.
        $cbD = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ComboBox)
        $ppD = Find-UIA -Aid "PadPageView"
        $whereD = if ($ppD) { "PadPageView" } else { "window root" }
        $srcD = if ($ppD) { $ppD } else { $script:uiaWin }
        Write-Host "  Diagnostic: ComboBoxes under the $whereD" -ForegroundColor Yellow
        foreach ($cb in (Find-AllSafe $srcD $cbD)) {
            $rD = Get-Rect $cb
            $posD = if ($null -eq $rD) { "no-rect" } else { "($([int]$rD.X),$([int]$rD.Y))" }
            Write-Host ("    Aid='{0}' Name='{1}' {2}" -f $cb.Current.AutomationId, $cb.Current.Name, $posD)
        }
        return $false
    }
    $exp = $null
    try { $exp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) }
    catch { Write-Host "  !! Select-Preset: preset combo exposes no ExpandCollapse" -ForegroundColor Red; return $false }
    try { $exp.Expand() } catch {}
    Start-Sleep -Milliseconds 800
    $liP = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $hit = $null
    foreach ($it in $combo.FindAll($TD, $liP)) { if ($it.Current.Name -eq $ExactName) { $hit = $it; break } }
    # A WPF popup can realize its item peers at the WINDOW ROOT rather than
    # under the combo, the same way the Pointer Mode popup does.
    if (-not $hit) {
        foreach ($it in $script:uiaWin.FindAll($TD, $liP)) { if ($it.Current.Name -eq $ExactName) { $hit = $it; break } }
    }
    if (-not $hit) {
        Write-Host "  !! preset '$ExactName' is not in the picker" -ForegroundColor Red
        try { $exp.Collapse() } catch {}
        return $false
    }
    try { $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    catch { Click-El $hit -Label "preset '$ExactName'" | Out-Null }
    Start-Sleep -Milliseconds 1400
    try { $exp.Collapse() } catch {}
    Start-Sleep -Milliseconds 1600
    Write-Host "  preset -> $ExactName" -ForegroundColor Green
    return $true
}

# Select a row in a WPF ListBox whose items come from DisplayMemberPath.
# The macro and menu lists are the same control shape, and the settings file
# is what says whether the row exists at all: the list itself hands UIA only
# Text peers, so a name find is the primary path and a measured window
# fraction is the fallback, exactly as the macro block already does.
function Select-ListRowByName {
    param([string]$Name, [double]$XFraction, [double]$YFraction, [string]$Label)
    # 1. The name, retried. The macro list does surface its rows as Text
    #    peers, so this is the primary path, but the peer can realize a beat
    #    after the tab does and one look is not an answer.
    $el = Find-UIARetry -Name $Name -Retries 6 -DelayMs 500
    if ($el) {
        if (Click-El $el -Label "$Label '$Name'" -Delay 700) { return $true }
    }
    # 2. THE LIST'S OWN RECT, not a window fraction. The 4.4.0 run's blind
    #    fraction landed above the Menus list (its Add / Remove / Duplicate
    #    strip is a WrapPanel, so the list top moves with the window width),
    #    nothing was selected, the editor stayed behind HasSelectedMenu, and
    #    both menu shots were skipped for want of a click 40 pixels lower.
    #    The ListBox itself has a List peer even when its items do not, so
    #    measure the row off the container.
    $listC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::List)
    $padPage = Find-UIA -Aid "PadPageView"
    $searchIn = if ($padPage) { $padPage } else { $script:uiaWin }
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $best = $null; $bestR = $null
    foreach ($lb in (Find-AllSafe $searchIn $listC)) {
        $r = Get-Rect $lb
        if ($null -eq $r) { continue }
        # Left column only, and tall enough to be the row list rather than a
        # combo popup's inner list.
        if (($r.X + $r.Width) -gt ($wr.Left + 0.45 * ($wr.Right - $wr.Left))) { continue }
        if ($r.Height -lt 80) { continue }
        if ($null -eq $bestR -or $r.Y -lt $bestR.Y) { $best = $lb; $bestR = $r }
    }
    if ($null -ne $bestR) {
        Write-Host ("  {0} '{1}': clicking the first row of the list at ({2},{3})" -f `
            $Label, $Name, [int]($bestR.X + $bestR.Width / 2), [int]($bestR.Y + 18))
        [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
        [Win32]::ClickAt([int]($bestR.X + $bestR.Width / 2), [int]($bestR.Y + 18))
        Start-Sleep -Milliseconds 800
        return $true
    }
    # 3. Last resort: the measured window fraction the caller supplied.
    Write-Host "  !! $Label '$Name': no peer and no list rect; blind fraction" -ForegroundColor Yellow
    Write-Host "  Diagnostic: List peers under the pad page" -ForegroundColor Yellow
    foreach ($lb in (Find-AllSafe $searchIn $listC)) {
        $rD = Get-Rect $lb
        $posD = if ($null -eq $rD) { "no-rect" } else { "($([int]$rD.X),$([int]$rD.Y)) $([int]$rD.Width)x$([int]$rD.Height)" }
        Write-Host ("    List Aid='{0}' Name='{1}' {2}" -f $lb.Current.AutomationId, $lb.Current.Name, $posD)
    }
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    [Win32]::ClickAt([int]($wr.Left + $XFraction * ($wr.Right - $wr.Left)),
                     [int]($wr.Top  + $YFraction * ($wr.Bottom - $wr.Top)))
    Start-Sleep -Milliseconds 700
    return $true
}

# Select the first menu on the Menus tab and prove the editor opened.
#
# Nothing in the pad page TAB BODY is visible to this harness through UIA:
# not the menu rows, not "Cell Bindings", not the editor's combos, not the
# Add button. The old opener asked UIA whether the editor had opened, got
# "no" every time whatever was on screen, and fell through to clicking Add,
# so every menu shot since 4.4.0 photographed a blank new "Menu 1" (all
# cells on None) instead of the injected Combat Wheel. The menu list is a
# fixed layout, so the first row is a measured spot: 0.226 W, 0.206 H on
# the maximized 2560x1539 window (menu-icon-packs, 2026-09-19). The proof
# is the editor column's pixels, which change from the empty-state pane to
# the editor when a menu is selected. No Add fallback: a menu made here
# has nothing bound, and a picture of it under these names is wrong.
function Open-MenuEditor {
    param([string]$Name)
    # The assignment banner sits across the top of the pad page and pushes
    # the list down, so it goes first.
    Dismiss-AssignBanner | Out-Null
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
    $ex = [int]($wr.Left + 0.32 * $w); $ey = [int]($wr.Top + 0.14 * $h)
    $ew = [int](0.60 * $w); $eh = [int](0.30 * $h)
    [Win32]::ForceFG($script:hwnd)
    [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15)); Start-Sleep -Milliseconds 200
    $before = Get-ScreenStripHash $ex $ey $ew $eh
    [Win32]::ClickAt([int]($wr.Left + 0.226 * $w), [int]($wr.Top + 0.206 * $h))
    Start-Sleep -Milliseconds 1300
    [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15)); Start-Sleep -Milliseconds 200
    $after = Get-ScreenStripHash $ex $ey $ew $eh
    if ($after -ne $before) {
        Write-Host "  menu editor open on the first menu row ('$Name' expected)" -ForegroundColor Green
        return $true
    }
    # A menu selected on an earlier visit stays selected, so the click
    # changes nothing while the editor already shows it: run 6 of the 5.0.0
    # capture skipped all three menu shots on an open Combat Wheel. The
    # editor's labels and fields make the region bright (about 1,440 sampled
    # points on menufail.png), and the empty pane is dark.
    $lit = Get-ScreenBrightCount $ex $ey $ew $eh
    if ($lit -ge 600) {
        Write-Host "  menu editor already open ($lit bright points, '$Name' expected)" -ForegroundColor Green
        return $true
    }
    # Keep the frame for re-measuring, outside the docs folder.
    try {
        $mbmp = New-Object System.Drawing.Bitmap($w, $h)
        $mg = [System.Drawing.Graphics]::FromImage($mbmp)
        $mg.CopyFromScreen($wr.Left, $wr.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
        $mg.Dispose()
        $mp = Join-Path (Join-Path $env:TEMP "PadForge_Capture") "menufail.png"
        $mbmp.Save($mp, [System.Drawing.Imaging.ImageFormat]::Png)
        $mbmp.Dispose()
        Write-Host "  .. menu-editor failure screenshot: $mp" -ForegroundColor DarkGray
    } catch { }
    Write-Host "  !! the first menu row click changed nothing: no menu in the list, or the row moved" -ForegroundColor Red
    return $false
}

# The Menus tab shots, all off one selected menu. pad-menus is the editor
# as it opens. menu-macro-cell frames Cell Bindings, where cell 2 names the
# Quick Combo macro. menu-icon-packs frames the Icon Packages block at the
# tab's end. Scroll amounts are ScrollContent clicks from the top, since the
# tab body cannot be anchored (see Open-MenuEditor).
function Capture-MenuShots {
    if (Want "pad-menus") { Start-Sleep -Milliseconds 500; Cap "pad-menus" }
    if (Want "menu-macro-cell") {
        ScrollContent -Clicks 90; ScrollContent -Clicks -14
        Start-Sleep -Milliseconds 600
        Cap "menu-macro-cell"
    }
    if (Want "menu-icon-packs") {
        ScrollContent -Clicks 90; ScrollContent -Clicks -22
        Start-Sleep -Milliseconds 600
        Cap "menu-icon-packs"
    }
    ScrollContent -Clicks 90
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
    # Forty, not eight: eight wheel steps no longer reach the top of a list
    # left at its bottom once the owner's cache passed 40 devices.
    for ($u = 0; $u -lt 40; $u++) { [Win32]::ScrollAt($listX, $midY, 3); Start-Sleep -Milliseconds 40 }
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
    # 100, not 40: the list's viewport ends about 102 px above the window's
    # bottom, over the "Drag a device onto a sidebar controller card" hint
    # and the status bar, so a last row that ends there still counts. Run 6 of the 5.0.0
    # capture clicked the Razer row at y=1435 of 1550, inside the window but
    # under the viewport's clip, the selection stayed on the Head Tracker,
    # and devices-analog-keyboard photographed that instead.
    $winBot36 = $wr.Bottom - 100
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
    # 60, matching the sibling: a 60-row list walked end to end. Sixteen
    # pages of a 35-device list did not reach the bottom once the owner's
    # own cached rows were merged in, and 24 no longer did at 43.
    for ($try = 0; $try -lt 60; $try++) {
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
# 5.0.0 RECIPES. Each one is a function so the full pass and the focused pass
# run the same steps. A shot that misses is retaken with -Only instead of a
# whole run.
# ==============================================================================

# Scroll the content pane one wheel notch at a time until $Anchor sits within
# $TopPx of the window's top edge, so a section that runs long below its
# heading fills the frame instead of starting halfway down it. The Updates
# card's framing (Capture-UpdatesCard), made general. False when the anchor
# never reached the screen or scrolled off the top.
function Scroll-AnchorToTop {
    param([string]$Anchor, [int]$TopPx = 160, [int]$MaxNotches = 90)
    if (-not (Scroll-ToAnchor -Anchor $Anchor)) { return $false }
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $cx = [int](($wr.Left + $wr.Right) / 2 + 100)
    $cy = [int](($wr.Top + $wr.Bottom) / 2)
    [Win32]::MoveTo($cx, $cy)
    for ($i = 0; $i -lt $MaxNotches; $i++) {
        $r = Get-Rect (Find-UIA -Name $Anchor)
        if ($null -eq $r -or $r.Y -le ($wr.Top + $TopPx)) { break }
        [Win32]::ScrollAt($cx, $cy, -1)
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 120
    }
    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 400
    $r = Get-Rect (Find-UIA -Name $Anchor)
    return ($null -ne $r -and $r.Y -ge ($wr.Top + 60))
}

# True when the named element's whole rect is inside the window, above the
# status bar.
function Test-InFrame {
    param([string]$Name)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $r = Get-Rect (Find-UIA -Name $Name)
    return ($null -ne $r -and $r.Y -ge ($wr.Top + 60) -and ($r.Y + $r.Height) -le ($wr.Bottom - 60))
}

# The web controller's plain HTTP address (5.0.0): its two checkboxes, the
# port and access code row, the status line and the QR. STEP 0 turns it on.
# The access code is the capture file's own (see STEP 0).
function Capture-WebPlainSection {
    if (-not (Want "dashboard-web-plain")) { return }
    Write-Host "[5.0.0] Dashboard: plain HTTP address"
    Nav "Dashboard"; Start-Sleep -Milliseconds 900
    ScrollContent -Clicks 90
    if ((Scroll-AnchorToTop -Anchor "Plain HTTP Address") -and (Test-InFrame "New Code")) {
        Cap "dashboard-web-plain"
    } else {
        Write-Host "  !! the Plain HTTP Address section never framed -- SKIPPED dashboard-web-plain" -ForegroundColor Red
    }
    ScrollContent -Clicks 90
}

# The Settings Input Engine card with the two 5.0.0 readers: Read Analog
# Keyboards and Read Bliss-Box Adapters, each with its status line (STEP 0
# turns both on). The card had no picture before this release.
function Capture-InputEngineCard {
    if (-not (Want "settings-input-engine")) { return }
    Write-Host "[5.0.0] Settings: Input Engine card"
    Nav "Settings"; Start-Sleep -Milliseconds 900
    ScrollContent -Clicks 90
    if ((Scroll-AnchorToTop -Anchor "Input Engine") -and (Test-InFrame "Read Bliss-Box Adapters")) {
        Cap "settings-input-engine"
    } else {
        Write-Host "  !! the Input Engine card never fit one frame -- SKIPPED settings-input-engine" -ForegroundColor Red
    }
    ScrollContent -Clicks 90
}

# The Devices page Light Gun section (#485), on the Wii Remote: an IR camera
# is all its gate reads (ComputeShowGunCalibration), so the cached or
# synthetic remote shows it offline. Calibrate stays disabled without the
# remote connected, which is what the docs say about it.
function Capture-LightGunSection {
    if (-not (Want "devices-light-gun")) { return }
    Write-Host "[5.0.0] Devices: Light Gun section"
    Nav "Devices"; Start-Sleep -Milliseconds 700
    if (-not (Select-DeviceByName36 "Nintendo Wii Remote")) {
        Write-Host "  !! no Nintendo Wii Remote row -- SKIPPED devices-light-gun" -ForegroundColor Red
        return
    }
    if (Scroll-PaneToAnchor -Anchor "Calibrate") {
        Cap "devices-light-gun"
    } else {
        Write-Host "  !! the Light Gun section never came into view -- SKIPPED devices-light-gun" -ForegroundColor Red
    }
    ScrollPane -Clicks 40
}

# Pick a family in the open Pair dialog by its exact name, then read the
# choice back. The combo has a UIA peer in the dialog's own tree
# (AutomationId FamilyCombo) and its items realize once it is expanded. The
# fallback is the combo's measured spot (0.50 W, 0.32 H of the dialog) and
# WPF's type-ahead.
function Set-PairFamily {
    param($DlgHwnd, [string]$Family, [string]$TypeAhead)
    $liC = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $fc = $null
    try {
        $dlgEl = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$DlgHwnd)
        $fc = $dlgEl.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "FamilyCombo")))
    } catch { $fc = $null }
    if ($fc) {
        try {
            $exp = $fc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $exp.Expand(); Start-Sleep -Milliseconds 600
            $hit = $null
            foreach ($it in $fc.FindAll($TD, $liC)) { if ($it.Current.Name -eq $Family) { $hit = $it; break } }
            if ($hit) { $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
            Start-Sleep -Milliseconds 300
            try { $exp.Collapse() } catch {}
            Start-Sleep -Milliseconds 1000
            $sel = $fc.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            if ((Get-Count $sel) -gt 0 -and $sel[0].Current.Name -eq $Family) {
                Write-Host "  pair family -> $Family" -ForegroundColor Green
                return $true
            }
            Write-Host "  .. the family read back as something other than '$Family', trying the fallback" -ForegroundColor DarkGray
        } catch { Write-Host "  .. family combo by UIA failed: $($_.Exception.Message)" -ForegroundColor DarkGray }
    }
    $dr = New-Object Win32+RECT
    [Win32]::GetWindowRect([IntPtr]$DlgHwnd, [ref]$dr) | Out-Null
    [Win32]::ForceFG([IntPtr]$DlgHwnd); Start-Sleep -Milliseconds 300
    [Win32]::ClickAt([int]($dr.Left + 0.50 * ($dr.Right - $dr.Left)), [int]($dr.Top + 0.32 * ($dr.Bottom - $dr.Top)))
    Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait($TypeAhead); Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 1000
    if ($fc) {
        try {
            $sel = $fc.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
            if ((Get-Count $sel) -gt 0 -and $sel[0].Current.Name -eq $Family) { return $true }
        } catch {}
        Write-Host "  !! the family never read back as '$Family'" -ForegroundColor Red
        return $false
    }
    # No peer to ask, so the picture is the check.
    Write-Host "  .. '$Family' picked by type-ahead, unverified" -ForegroundColor Yellow
    return $true
}

# The Pair dialog, one shot per family: Nintendo Wii, Sony DualShock 3,
# PlayStation Move / Navigation, and the two 5.0.0 families, serial
# controllers and DJI remotes. The dialog is a FluentWindow modal that holds
# the foreground when it opens, and EnumWindows is the fallback. It closes
# by WM_CLOSE, which pairs and adds nothing.
function Capture-PairDialog {
    $families = @(
        @{ Shot = "wii-pair";    Name = "Nintendo Wii";                  Type = "Nintendo" },
        @{ Shot = "ds3-pair";    Name = "Sony DualShock 3";              Type = "Sony" },
        @{ Shot = "move-pair";   Name = "PlayStation Move / Navigation"; Type = "PlayStation" },
        @{ Shot = "serial-pair"; Name = "Serial Controller (COM Port)";  Type = "Serial" },
        @{ Shot = "dji-pair";    Name = "DJI RC or RC 2 (Network)";      Type = "DJI" }
    )
    $wanted = @($families | Where-Object { Want $_.Shot })
    if ($wanted.Count -eq 0) { return }
    $wantedNames = ($wanted | ForEach-Object { $_.Shot }) -join ', '
    Write-Host "[3b] Pair dialog: $wantedNames"
    # The Pair control is an icon-only header button (glyph E702, ToolTip
    # "Pair"), so its UIA Name is the glyph. Match it in the header strip.
    $glyphPair = [char]0xE702
    $pairBtn = $null
    for ($ptry = 0; $ptry -lt 4 -and -not $pairBtn; $ptry++) {
        Nav "Devices"; Start-Sleep -Milliseconds 900
        $wrPH = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrPH) | Out-Null
        foreach ($b in $script:uiaWin.FindAll($TD, $btn36)) {
            $r = Get-Rect $b
            if ($null -eq $r -or $r.Y -gt ($wrPH.Top + 160)) { continue }
            $nm = $b.Current.Name
            if ($nm -eq "Pair" -or ($nm -and $nm.IndexOf($glyphPair) -ge 0)) { $pairBtn = $b; break }
            $childGlyph = $b.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, "$glyphPair")))
            if ($childGlyph) { $pairBtn = $b; break }
        }
        if (-not $pairBtn) { Start-Sleep -Milliseconds 600 }
    }
    if (-not $pairBtn) {
        Write-Host "  !! Pair button not found -- SKIPPED $wantedNames" -ForegroundColor Red
        return
    }
    Click-El $pairBtn -Label "Pair" -Delay 2200 | Out-Null
    $pairDlg = Get-ForegroundDialogHwnd
    if ($pairDlg -eq [IntPtr]::Zero) { $pairDlg = Find-DialogHwndByEnum }
    if ($pairDlg -eq [IntPtr]::Zero) {
        Write-Host "  !! the Pair dialog did not open -- SKIPPED $wantedNames" -ForegroundColor Red
        Close-AnyModal | Out-Null
        return
    }
    foreach ($f in $wanted) {
        if (Set-PairFamily -DlgHwnd $pairDlg -Family $f.Name -TypeAhead $f.Type) { Cap $f.Shot -AllowModal }
        else { Write-Host "  !! SKIPPED $($f.Shot)" -ForegroundColor Red }
    }
    Close-DialogHwnd $pairDlg
    Close-AnyModal | Out-Null
}

# Open a row of the Xbox slot's mapping grid by clicking its Output label.
# On a Mappings tab that carries the injected Aim layer (its Base/Aim row and
# the SHIFT chip sit above the grid) the first row is at 0.2879 H and rows
# step 0.02538 H, measured off the 2560x1539 captures of 2026-10-03. The old
# 0.206 H origin predates the layer row, and three shots opened D-pad and
# shoulder rows under the wrong names. Open rows bottom-up: an open row
# pushes the rows below it down and leaves the rows above it in place.
# An open row also scrolls the grid to bring its details into view. Run 5
# opened Left Stick X, the grid moved down one row, and the next two clicks
# opened the row below each one named, so mapping-rapid-trigger showed Left
# Stick X and pad-stick-trim showed Right Trigger. The wheel turns the grid
# back to its top before each click, over plain rows (0.40 H) where no
# slider or picker of an open row can take it.
function Open-MappingRow {
    param([int]$Index, [string]$Label)
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    $gx = [int]($wr.Left + 0.50 * $w); $gy = [int]($wr.Top + 0.40 * $h)
    [Win32]::MoveTo($gx, $gy)
    for ($n = 0; $n -lt 10; $n++) {
        [Win32]::ScrollAt($gx, $gy, 3)
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 500
    [Win32]::ClickAt([int]($wr.Left + 0.20 * $w), [int]($wr.Top + (0.2879 + $Index * 0.02538) * $h))
    Write-Host "  opened mapping row $Index ($Label)"
    Start-Sleep -Milliseconds 1300
    [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15))
}

# The icon picker (#471), which menu cells and shift layers share, opened
# from the shift layer dialog. The dialog is a FluentWindow reachable
# through its own HWND, and its icon button carries AutomationId
# IconPickerButton, so this path needs one measured click (+ Shift Layer,
# at 0.3495 W, 0.158 H in the Mappings toolbar the tab body hides from UIA)
# where a menu cell would need several. The dialog closes by WM_CLOSE,
# which adds no layer.
function Capture-IconPicker {
    if (-not (Want "icon-picker")) { return }
    Write-Host "[5.0.0] Icon picker (shift layer dialog)"
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    [Win32]::ClickAt([int]($wr.Left + 0.3495 * ($wr.Right - $wr.Left)), [int]($wr.Top + 0.158 * ($wr.Bottom - $wr.Top)))
    Start-Sleep -Milliseconds 1600
    $dlg = Find-DialogHwndByEnum -MinW 300 -MinH 200 -Retries 6
    if ($dlg -eq [IntPtr]::Zero) {
        Write-Host "  !! the shift layer dialog did not open -- SKIPPED icon-picker" -ForegroundColor Red
        return
    }
    $iconBtn = $null
    try {
        $dlgEl = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$dlg)
        $iconBtn = $dlgEl.FindFirst($TD, (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "IconPickerButton")))
    } catch {}
    $ir = Get-Rect $iconBtn
    if ($null -eq $ir) {
        Write-Host "  !! no IconPickerButton in the shift layer dialog -- SKIPPED icon-picker" -ForegroundColor Red
    } else {
        [Win32]::SetForegroundWindow([IntPtr]$dlg) | Out-Null; Start-Sleep -Milliseconds 200
        [Win32]::ClickAt([int]($ir.X + $ir.Width / 2), [int]($ir.Y + $ir.Height / 2))
        Start-Sleep -Milliseconds 1300
        Cap "icon-picker" -AllowModal
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 500
    }
    Close-DialogHwnd $dlg
    Close-AnyModal | Out-Null
}

# A Motion row's editor (#475) on the PlayStation slot, where Motion Pitch,
# Yaw and Roll are the grid's last three rows, below the fold. Write-
# SlotStructures gives Motion Roll two sources, so it opens when selected.
# Ctrl+End did not reach the grid in run 6 of the 5.0.0 capture: the row
# click opened D-Pad Down and the shot showed that. So the wheel scrolls the
# grid to its end, over plain rows, and the lowest label in the Output
# column (0.170 to 0.235 W, above the horizontal scrollbar at 0.948 H) is
# the last row's, Motion Roll's, wherever item scrolling leaves it.
function Capture-MotionRow {
    if (-not (Want "mapping-motion-rows")) { return }
    Write-Host "[5.0.0] PlayStation: Motion Roll row"
    if (-not (Tab "Mappings")) {
        Write-Host "  !! Mappings tab not found -- SKIPPED mapping-motion-rows" -ForegroundColor Red
        return
    }
    Start-Sleep -Milliseconds 1200
    $wr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wr) | Out-Null
    $w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    $gx = [int]($wr.Left + 0.50 * $w); $gy = [int]($wr.Top + 0.45 * $h)
    [Win32]::MoveTo($gx, $gy)
    for ($n = 0; $n -lt 16; $n++) {
        [Win32]::ScrollAt($gx, $gy, -3)
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 800
    [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15)); Start-Sleep -Milliseconds 200
    $rowY = Find-LowestTextBandY ([int]($wr.Left + 0.170 * $w)) ([int]($wr.Top + 0.22 * $h)) ([int](0.065 * $w)) ([int](0.72 * $h))
    if ($null -eq $rowY) {
        Write-Host "  !! no row label at the grid's end -- SKIPPED mapping-motion-rows" -ForegroundColor Red
        return
    }
    Write-Host ("  last row label at {0:N3} H" -f (($rowY - $wr.Top) / $h))
    [Win32]::ClickAt([int]($wr.Left + 0.20 * $w), $rowY)
    Start-Sleep -Milliseconds 1600
    [Win32]::MoveTo(($wr.Right - 100), ($wr.Bottom - 15))
    Cap "mapping-motion-rows"
}

# The DualShock 3's own 3D model (5.0.0) in the Preview tab of the
# PlayStation slot, with the DualShock 3 (SIXAXIS) preset written by pad
# index (PlayStation is pad 1 in creation order and card 1 in type-group
# order). Runs after every other PlayStation shot: the slot keeps the preset
# until STEP 4 restores the owner's file.
function Capture-Ds3Preset {
    if (-not (Want "pad-playstation-ds3")) { return }
    Write-Host "[5.0.0] PlayStation slot: DualShock 3 model"
    if (-not (Set-SlotPreset -PadIndex 1 -ProfileId "dualshock-3" -XmlPath $PadForgeXml -ExePath $PadForgeExe)) {
        Write-Host "  !! SKIPPED pad-playstation-ds3" -ForegroundColor Red
        return
    }
    Start-Sleep -Milliseconds 2000
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
    Nav "Dashboard"; Start-Sleep -Milliseconds 1500
    $shD3 = Find-UIA -Aid "SlotsItemsControl"
    $cdD3 = @()
    if ($shD3) { try { $cdD3 = @($shD3.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdD3 = @() } }
    $ppD3 = if ((Get-Count $cdD3) -ge 2) { Open-SlotCard $cdD3[1] "PlayStation slot card (DualShock 3)" } else { $null }
    if (-not $ppD3) {
        Write-Host "  !! the PlayStation pad page did not open -- SKIPPED pad-playstation-ds3" -ForegroundColor Red
        return
    }
    Tab "Preview" | Out-Null
    Start-Sleep -Milliseconds 3000
    Dismiss-AssignBanner | Out-Null
    Wait-EngineForging | Out-Null
    $seenD3 = Get-PresetText
    if (-not $seenD3 -or $seenD3 -notmatch 'DualShock 3') {
        Write-Host "  !! the preset reads '$seenD3', not a DualShock 3 -- SKIPPED pad-playstation-ds3" -ForegroundColor Red
        return
    }
    Write-Host "  preset on screen: $seenD3" -ForegroundColor Green
    Cap "pad-playstation-ds3"
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
        # The full product name. A bare "DualSense" also matches the
        # web-controller lane's "DualSense Web Controller 1" rows, which sort
        # ahead of the pad and have no Power section at all.
        @{ Match = "DualSense Wireless Controller"; Shots = @("devices-dualsense", "devices-power") },
        @{ Match = "Head Tracker"; Shots = @("devices-head-tracking") },
        # The synthetic analog keyboard (STEP 0).
        @{ Match = "Razer Huntsman V3 Pro"; Shots = @("devices-analog-keyboard") }
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

    # Devices-page shots that need the DOSSIER scrolled, which is a different
    # scroll viewer from the card list.
    if (Want "devices-quick-charge") {
        Write-Host "[focused] Devices: Quick Charge row"
        Nav "Devices"; Start-Sleep -Milliseconds 800
        if (Select-DeviceByName36 "DualSense Wireless Controller") {
            if (Scroll-PaneToAnchor -Anchor "Disconnect Bluetooth When Plugged In over USB") {
                Cap "devices-quick-charge"
            } else {
                Write-Host "  !! Quick Charge row never came into view -- SKIPPED devices-quick-charge" -ForegroundColor Red
            }
            ScrollPane -Clicks 40
        } else {
            Write-Host "  !! no DualSense Wireless Controller row -- SKIPPED devices-quick-charge" -ForegroundColor Red
        }
    }

    # Dashboard sections whose shot needs SEVERAL anchors in one frame.
    $multiAnchorTargets = @(
        @{ Shot = "dashboard-lightbar-mirrors"; Page = "Dashboard";
           Anchors = @("LIGHTBAR MIRRORS", "Send Rumble to Sensa HD Haptics") },
        @{ Shot = "dashboard-head-tracking";    Page = "Dashboard";
           Anchors = @("HEAD TRACKING", "Set Neutral") },
        @{ Shot = "remote-link";                Page = "Dashboard";
           Anchors = @("REMOTE LINK", "Or Connect by Address (Advanced)") }
    )
    foreach ($t in $multiAnchorTargets) {
        if (-not (Want $t.Shot)) { continue }
        Write-Host "[focused] $($t.Page) section pair: $($t.Shot)"
        Nav $t.Page; Start-Sleep -Milliseconds 900
        ScrollContent -Clicks 90
        if (Scroll-ToAnchors -Anchors $t.Anchors -MaxSteps 90) { Cap $t.Shot }
        else { Write-Host "  !! SKIPPED $($t.Shot)" -ForegroundColor Red }
        ScrollContent -Clicks 90
    }

    # Scrolled page sections. A fixed click count is a guess about page
    # LENGTH, and the page keeps growing: -40 reached the Community Configs
    # card when it was written and, once the Diagnostics section landed above
    # it, photographed HidHide instead. Scroll in steps and stop when the
    # anchor is actually on screen, so the shot cannot silently drift onto a
    # neighboring card.
    #
    # DASHBOARD SECTION TITLES ARE UPPERCASED IN THE VIEW (DashboardPage.xaml
    # runs every SectionTitle through UpperConverter), and a TextBlock's UIA
    # Name is what it RENDERS. "Remote Link" therefore matched nothing and
    # that target could never have fired. Settings CARD titles are not
    # converted, so those anchors stay as written.
    $scrollTargets = @(
        # remote-link and dashboard-head-tracking are in $multiAnchorTargets
        # above: each needs BOTH ends of its section in the frame, which a
        # single anchor cannot promise.
        @{ Shot = "dsu-port-box";               Page = "Dashboard"; Anchor = "MOTION SERVER"; After = -4 },
        @{ Shot = "settings-assignment-prompts"; Page = "Settings"; Anchor = "Assignment Prompts";  After = -4 },
        @{ Shot = "settings-handheld-buttons";  Page = "Settings";  Anchor = "Handheld PC Buttons"; After = -4 },
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

    if (Want "settings-updates") {
        Write-Host "[focused] Settings section: settings-updates"
        Nav "Settings"; Start-Sleep -Milliseconds 900
        Capture-UpdatesCard
        ScrollContent -Clicks 80
    }

    # The 5.0.0 recipes that need no slot staging. Each one checks Want
    # itself.
    Capture-WebPlainSection
    Capture-InputEngineCard
    Capture-LightGunSection
    Capture-PairDialog

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
        Ensure-DeviceAssigned -DeviceNamePart "DualSense Wireless Controller" -PadIndex 1 -SlotType 1 `
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
            Open-SlotCard $cards[1] "PlayStation Slot card" | Out-Null
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

    # ── The 2D controller preview: Xbox slot, then PlayStation slot ──
    # A run asking for nothing but these opens every pad page in 2D from the
    # saved setting (STEP 0), so Set-ViewMode has nothing to click. A mixed run
    # toggles, waits for the app to save the view, and puts 3D back at the end.
    # The view is one global setting, so the PlayStation page opens in the same
    # view.
    $twoDWanted = @($script:TwoDShots | Where-Object { Want $_ })
    if ($twoDWanted.Count -gt 0) {
        Write-Host "[focused] 2D controller preview: $($twoDWanted -join ', ')"
        # The full pass's own staging for slots 1 and 2. Assigning through the
        # Devices page runs auto-map, which gives the Xbox slot the mapped rows
        # the annotation chips draw from. Writing MapTo alone leaves every row
        # without a source. A failed assignment skips the shots rather than
        # photographing a slot that is not staged.
        Nav "Devices"; Start-Sleep -Milliseconds 1500
        $staged = $true
        foreach ($a in @(@("DualSense Wireless Controller", "1", $false), @("DualSense Wireless Controller", "2", $false),
                         @("Xbox Series X GIP", "1", $true), @("Logitech G29", "1", $false))) {
            $ok = if ($a[2]) { Assign-DeviceToSlot -DeviceNamePart $a[0] -SlotNumberLabel $a[1] -Reassert | Select-Object -Last 1 }
                  else { Assign-DeviceToSlot -DeviceNamePart $a[0] -SlotNumberLabel $a[1] | Select-Object -Last 1 }
            if ($ok -ne $true) { $staged = $false }
        }
        # The app writes its settings 2 s after the last change
        # (SettingsService.PersistQuietMs), and Ensure-DeviceAssigned kills it
        # first thing. Wait until every assignment above is in the file, as
        # saved after the last click.
        if ($staged) {
            # Slots 1 and 2 on the Devices page: the Xbox slot (type 0) and
            # the PlayStation slot (type 1).
            $staged = Wait-SavedAssignments -LastClick (Get-Date) -Pairs @(
                @("DualSense Wireless Controller", 0), @("DualSense Wireless Controller", 1),
                @("Xbox Series X GIP", 0), @("Logitech G29", 0))
        }
    }
    if ($twoDWanted.Count -gt 0 -and -not $staged) {
        Refuse-Shots $twoDWanted "the slots were not staged"
    }
    if ($twoDWanted.Count -gt 0 -and $staged) {
        $x = Ensure-DeviceAssigned -DeviceNamePart "Xbox Series X GIP" -PadIndex 0 -SlotType 0 `
            -XmlPath $PadForgeXml -ExePath $PadForgeExe | Select-Object -Last 1
        # Every synthetic device is offline, so nothing above starts the
        # engine. The full pass forges because the merged mouse, a real device,
        # sits on the keyboard-and-mouse slot (pad 3 in creation order). Without
        # it the status bar reads 0 Hz Idle beside a set that reads Forging.
        $m = Ensure-DeviceAssigned -DeviceNamePart "All Mice (Merged)" -PadIndex 3 -SlotType 4 `
            -XmlPath $PadForgeXml -ExePath $PadForgeExe | Select-Object -Last 1
        if ($x -ne $true -or $m -ne $true) {
            Refuse-Shots $twoDWanted "the Xbox or keyboard-and-mouse slot was not staged"
            $staged = $false
        }
    }
    if ($twoDWanted.Count -gt 0 -and $staged) {
        Start-Sleep -Milliseconds 2500
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Nav "Dashboard"; Start-Sleep -Milliseconds 1500
        $slotsHost = Find-UIA -Aid "SlotsItemsControl"
        $cards = if ($slotsHost) { @($slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
        Write-Host "  Found $((Get-Count $cards)) slot card(s)"
        # Xbox is card 0 and PlayStation card 1 in type-group order.
        $padPage = if ((Get-Count $cards) -ge 2) { Open-SlotCard $cards[0] "Xbox slot card" } else { $null }
        if ($padPage) {
            $rbCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            $tabs = $padPage.FindAll($TC, $rbCond)
            if ((Get-Count $tabs) -gt 0) { Click-El $tabs[0] -Label "Preview tab" -Delay 1000 | Out-Null }
            Dismiss-AssignBanner | Out-Null
            Wait-EngineForging | Out-Null
            if (Set-ViewMode '2D') {
                Start-Sleep -Milliseconds 600
                Cap "pad-controller-2d"
                Capture-2DAnnotationOverlay
                if (Want "2d-touchpad-finger-dots") {
                    Nav "Dashboard"; Start-Sleep -Milliseconds 1500
                    $slotsHost = Find-UIA -Aid "SlotsItemsControl"
                    $cards = if ($slotsHost) { @($slotsHost.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
                    $psPage = if ((Get-Count $cards) -ge 2) { Open-SlotCard $cards[1] "PlayStation slot card" } else { $null }
                    if ($psPage) {
                        Start-Sleep -Milliseconds 1500
                        Dismiss-AssignBanner | Out-Null
                        Wait-EngineForging | Out-Null
                        Cap "2d-touchpad-finger-dots"
                    } else {
                        Refuse-Shots @("2d-touchpad-finger-dots") "the PlayStation pad page did not open"
                    }
                }
                # A 2D-only run stays in 2D. A mixed run needs 3D back for what follows.
                if (-not $script:Start2D -and -not (Set-ViewMode '3D')) {
                    $script:StateFailures += "the 3D view did not come back after the 2D preview shots"
                    Write-Host "  !! 3D view did not come back" -ForegroundColor Red
                }
            } else {
                Refuse-Shots $twoDWanted "the 2D view did not open"
            }
        } else {
            Refuse-Shots $twoDWanted "the Xbox pad page did not open"
        }
    }

    # ── Profiles page, and the modal that hangs off it ──
    if (Want "profiles-external-control") {
        Write-Host "[focused] Profiles: external control checkbox"
        Nav "Profiles"; Start-Sleep -Milliseconds 900
        $exF = Get-Rect (Find-UIA -Name "Allow External Control by Launchers and Scripts")
        if ($null -eq $exF) {
            Write-Host "  !! External Control checkbox not found -- SKIPPED profiles-external-control" -ForegroundColor Red
        } else { Cap "profiles-external-control" }
    }
    if (Want "profile-polling-override") {
        Write-Host "[focused] Profiles: edit dialog polling override"
        Nav "Profiles"; Start-Sleep -Milliseconds 900
        $rlF = Find-UIARetry -Name "Rocket League"
        if (-not $rlF) {
            Write-Host "  !! no 'Rocket League' profile card -- SKIPPED profile-polling-override" -ForegroundColor Red
        } else {
            Click-El $rlF -Label "Rocket League profile card" -Delay 900 | Out-Null
            $edF = Find-UIA -Name "Edit" -CT ([System.Windows.Automation.ControlType]::Button)
            if (-not $edF) {
                Write-Host "  !! Edit button not found -- SKIPPED profile-polling-override" -ForegroundColor Red
            } else {
                Click-El $edF -Label "Edit profile" -Delay 1600 | Out-Null
                $dlgF = Find-DialogHwndByEnum -MinW 400 -MinH 300
                if ($dlgF -eq [IntPtr]::Zero) {
                    Write-Host "  !! profile dialog HWND not found -- SKIPPED profile-polling-override" -ForegroundColor Red
                } else {
                    Start-Sleep -Milliseconds 900
                    Cap "profile-polling-override" -AllowModal
                    Close-DialogHwnd $dlgF
                    Close-AnyModal | Out-Null
                }
            }
        }
    }

    # ── Xbox slot: the source pickers ──
    # Capture-SourcePicker's geometry is the full run's slot 1: the DualSense
    # assigned through the Devices page, so the app maps its rows and it is
    # every row's primary, and the Aim shift layer's Base/Aim row above the
    # grid. A focused run skips that staging, so stage it the same way here:
    # assign in the UI, let the save land, then write the macros and layers.
    # A settings-file assignment alone maps no rows.
    $pickWanted = @($script:SourcePickerTargets | Where-Object { Want $_.Shot })
    if ($pickWanted.Count -gt 0) {
        Write-Host "[focused] Xbox slot: DualSense and the Aim layer for the source pickers"
        Nav "Devices"; Start-Sleep -Milliseconds 1200
        $dsOk = Assign-DeviceToSlot -DeviceNamePart "DualSense Wireless Controller" -SlotNumberLabel "1"
        if (Assert-Staged $dsOk "assigning DualSense Wireless Controller to slot 1") {
            # The leading comma keeps a one-pair list a list of pairs. @(@(a, b))
            # flattens to a and b, and indexing the 0 threw under StrictMode,
            # which Wait-SavedAssignments reports as an unreadable file.
            Assert-Staged (Wait-SavedAssignments -LastClick (Get-Date) -Pairs @(
                , @("DualSense Wireless Controller", 0))) "saving the DualSense assignment" | Out-Null
            Ensure-MacrosLoaded -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
            Start-Sleep -Milliseconds 2000
            $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
            Write-Host "[focused] Source pickers: $(($pickWanted | ForEach-Object { $_.Shot }) -join ', ')"
            Capture-SourcePickerSet $pickWanted
        }
    }

    # ── Xbox slot: the Macros and Menus tabs ──
    # Both need slot 0's authored content, which the setup block writes
    # whether or not -Only is in play (Ensure-MacrosLoaded is inside the
    # assignment block, so a focused run rebuilds it here instead).
    # Open-SlotCard clicks the card's "Slot" title. The card's center is its
    # controller-type strip, and on a slot with no device mapped, which is
    # every focused run, a click there changes the slot's type: the 4.5.3
    # menu-icon-packs came from a slot 1 turned into a PlayStation slot.
    $xboxSlotTargets = @("macro-switch-layer", "macro-set-chroma-color", "macro-add-from-list", "pad-menus", "menu-macro-cell", "menu-icon-packs")
    $xboxWanted = @($xboxSlotTargets | Where-Object { Want $_ })
    if ($xboxWanted.Count -gt 0) {
        Write-Host "[focused] Xbox slot: $($xboxWanted -join ', ')"
        Ensure-MacrosLoaded -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
        Start-Sleep -Milliseconds 2000
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        # Macro row index on the 0.241 + n * 0.0441 ladder, per shot.
        $macroRows = @{ "macro-switch-layer" = 5; "macro-set-chroma-color" = 6 }
        $macroWanted = @($macroRows.Keys | Where-Object { Want $_ } | Sort-Object { $macroRows[$_] })
        if ($macroWanted.Count -gt 0) {
            Nav "Dashboard"; Start-Sleep -Milliseconds 1500
            $shF = Find-UIA -Aid "SlotsItemsControl"
            $cdF = @()
            if ($shF) { try { $cdF = @($shF.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdF = @() } }
            $ppF = if ((Get-Count $cdF) -ge 1) { Open-SlotCard $cdF[0] "Xbox slot card (Macros)" } else { $null }
            if ($ppF -and (Tab "Macros")) {
                Start-Sleep -Milliseconds 900
                $wrF = New-Object Win32+RECT
                [Win32]::GetWindowRect($script:hwnd, [ref]$wrF) | Out-Null
                $fw = $wrF.Right - $wrF.Left; $fh = $wrF.Bottom - $wrF.Top
                foreach ($shot in $macroWanted) {
                    [Win32]::ForceFG($script:hwnd)
                    [Win32]::ClickAt([int]($wrF.Left + 0.215 * $fw), [int]($wrF.Top + (0.241 + $macroRows[$shot] * 0.0441) * $fh)); Start-Sleep -Milliseconds 800
                    [Win32]::ClickAt([int]($wrF.Left + 0.383 * $fw), [int]($wrF.Top + 0.6897 * $fh)); Start-Sleep -Milliseconds 900
                    Cap $shot
                }
            } else { Write-Host "  !! the Xbox slot's Macros tab did not open -- SKIPPED $($macroWanted -join ', ')" -ForegroundColor Red }
        }
        # Add from List: Quick Combo is row 0 of the ladder above, and the
        # combo sits at the full run's measured fallback spot (0.4278 W,
        # 0.3981 H). UIA misses the row's label here, as it does in the full
        # run.
        if (Want "macro-add-from-list") {
            Nav "Dashboard"; Start-Sleep -Milliseconds 1500
            $shA = Find-UIA -Aid "SlotsItemsControl"
            $cdA = @()
            if ($shA) { try { $cdA = @($shA.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdA = @() } }
            $ppA = if ((Get-Count $cdA) -ge 1) { Open-SlotCard $cdA[0] "Xbox slot card (Add from List)" } else { $null }
            if ($ppA -and (Tab "Macros")) {
                Start-Sleep -Milliseconds 900
                $wrA2 = New-Object Win32+RECT
                [Win32]::GetWindowRect($script:hwnd, [ref]$wrA2) | Out-Null
                $aw = $wrA2.Right - $wrA2.Left; $ah = $wrA2.Bottom - $wrA2.Top
                [Win32]::ForceFG($script:hwnd)
                [Win32]::ClickAt([int]($wrA2.Left + 0.215 * $aw), [int]($wrA2.Top + 0.241 * $ah)); Start-Sleep -Milliseconds 900
                [Win32]::ClickAt([int]($wrA2.Left + 0.4278 * $aw), [int]($wrA2.Top + 0.3981 * $ah)); Start-Sleep -Milliseconds 800
                Cap "macro-add-from-list" -AllowModal
                [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300
            } else { Write-Host "  !! the Xbox slot's Macros tab did not open -- SKIPPED macro-add-from-list" -ForegroundColor Red }
        }
        $menuWanted = @(@("pad-menus", "menu-macro-cell", "menu-icon-packs") | Where-Object { Want $_ })
        if ($menuWanted.Count -gt 0) {
            Nav "Dashboard"; Start-Sleep -Milliseconds 1500
            $shM = Find-UIA -Aid "SlotsItemsControl"
            $cdM = @()
            if ($shM) { try { $cdM = @($shM.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdM = @() } }
            $ppM = if ((Get-Count $cdM) -ge 1) { Open-SlotCard $cdM[0] "Xbox slot card (Menus)" } else { $null }
            if ($ppM -and (Tab "Menus")) {
                Start-Sleep -Milliseconds 900
                if (Open-MenuEditor -Name "Combat Wheel") { Capture-MenuShots }
                else { Write-Host "  !! SKIPPED $($menuWanted -join ', ')" -ForegroundColor Red }
            } else { Write-Host "  !! the Xbox slot's Menus tab did not open -- SKIPPED $($menuWanted -join ', ')" -ForegroundColor Red }
        }
    }

    # ── KBM slot: the Mouse tab's gesture card ──
    if (Want "pad-mouse-gestures") {
        Write-Host "[focused] KBM slot: mouse gestures"
        # KBM is pad index 3 in creation order (Xbox 0, PlayStation 1,
        # Nintendo 2, KBM 3, Extended 4) and dashboard CARD index 4 in
        # type-group order. The two disagree and both are used here.
        Ensure-DeviceAssigned -DeviceNamePart "All Mice (Merged)" -PadIndex 3 -SlotType 4 `
            -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
        Start-Sleep -Milliseconds 2000
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        Nav "Dashboard"; Start-Sleep -Milliseconds 1500
        $shK = Find-UIA -Aid "SlotsItemsControl"
        $cdK = @()
        if ($shK) { try { $cdK = @($shK.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdK = @() } }
        if ((Get-Count $cdK) -le 4) {
            Write-Host "  !! KBM slot card missing -- SKIPPED pad-mouse-gestures" -ForegroundColor Red
        } else {
            Open-SlotCard $cdK[4] "KBM Slot card (focused)" | Out-Null
            Select-MappedDevice "All Mice (Merged)" | Out-Null
            if (Tab "Mouse") {
                Start-Sleep -Milliseconds 800
                ScrollContent -Clicks -10
                Start-Sleep -Milliseconds 400
                Cap "pad-mouse-gestures"
                ScrollContent -Clicks 90
            } else {
                Write-Host "  !! Mouse tab not found -- SKIPPED pad-mouse-gestures" -ForegroundColor Red
                Write-Host "  !! it gates on the SELECTED device being a mouse" -ForegroundColor Red
            }
        }
    }

    # ── Extended slot: the Wii Remote's Pointer and Grip cards, and the
    #    Valve personas ──
    $extWanted = @(@("pad-gyro-grip", "pad-pointer", "wii-pointer-mode",
                     "pad-extended-steam-controller", "pad-extended-steam-deck") |
                   Where-Object { Want $_ })
    if ($extWanted.Count -gt 0) {
        Write-Host "[focused] Extended slot: $($extWanted -join ', ')"
        # Extended is VirtualControllerType 2. Ensure-DeviceAssigned resolves
        # the pad index from the type rather than trusting a number, because
        # dashboard CARD order is type-group order while PAD index is creation
        # order and the two do not agree.
        $wiiWanted = @(@("pad-gyro-grip", "pad-pointer", "wii-pointer-mode") | Where-Object { Want $_ })
        if ($wiiWanted.Count -gt 0) {
            Ensure-DeviceAssigned -DeviceNamePart "Wii Remote" -PadIndex 4 -SlotType 2 `
                -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
            Start-Sleep -Milliseconds 2000
            $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
        }
        $extIdxF = 3
        if ($wiiWanted.Count -gt 0) {
            Nav "Dashboard"; Start-Sleep -Milliseconds 1500
            $shG = Find-UIA -Aid "SlotsItemsControl"
            $cdG = @()
            if ($shG) { try { $cdG = @($shG.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdG = @() } }
            if ((Get-Count $cdG) -le $extIdxF) {
                Write-Host "  !! Extended slot card missing -- SKIPPED $($wiiWanted -join ', ')" -ForegroundColor Red
            } else {
                Open-SlotCard $cdG[$extIdxF] "Extended Slot card (Wii Remote)" | Out-Null
                # The dropdown carries the device's full product name, and the
                # owner's real pad is enumerated as "Nintendo Wii Remote", so
                # the synthetic is skipped as a duplicate. Match the substring
                # both forms share rather than either exact label.
                if (-not (Select-MappedDevice "Wii Remote")) {
                    Write-Host "  !! Wii Remote is not in the Extended slot's device dropdown" -ForegroundColor Red
                    Write-Host "  !! the assignment did not land; SKIPPING $($wiiWanted -join ', ')" -ForegroundColor Red
                } else {
                    if (Want "pad-gyro-grip") {
                        # Tab returns true once the tab reports itself SELECTED,
                        # so that is the render gate. (A tab that exposes no
                        # SelectionItemPattern cannot be asked, and Tab then
                        # returns the click's own result and says so in the
                        # log. The pad page tabs do expose it.) There used to
                        # be a second gate here that looked up a UIA element
                        # named "Grip" and it failed every run from 4.4.0 on.
                        # A screenshot taken at the moment of that failure
                        # showed the Gyro tab open with the Grip card at the
                        # top of the frame, reading "Held As: Pointing". The
                        # card was never missing. PadPageView simply does not
                        # carry the scrolled tab body in its UIA subtree here
                        # (its Text descendants stop at the slot header and
                        # the Extended config bar), so the lookup was asking
                        # a layer that cannot answer. The guard went, not the
                        # shot.
                        if (Tab "Gyro") {
                            Start-Sleep -Milliseconds 900
                            Cap "pad-gyro-grip"
                        } else { Write-Host "  !! Gyro tab not found -- SKIPPED pad-gyro-grip" -ForegroundColor Red }
                    }
                    if ((Want "pad-pointer") -or (Want "wii-pointer-mode")) {
                        # The Pointer tab gates on HasIrCamera, which
                        # propagates a few seconds after the slot binds.
                        $ptrOk = $false
                        $rbW = New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                            [System.Windows.Automation.ControlType]::RadioButton)
                        $ppW = Find-UIA -Aid "PadPageView"
                        for ($w = 0; $w -lt 8 -and -not $ptrOk; $w++) {
                            Start-Sleep -Milliseconds 800
                            if ($ppW -and ($ppW.FindAll($TC, $rbW) | Where-Object { $_.Current.Name -eq "Pointer" })) { $ptrOk = $true }
                        }
                        if (-not ($ptrOk -and (Tab "Pointer"))) {
                            Write-Host "  !! Pointer tab never appeared -- SKIPPED pad-pointer, wii-pointer-mode" -ForegroundColor Red
                        } else {
                            Start-Sleep -Milliseconds 800
                            if (Want "pad-pointer") { Cap "pad-pointer" }
                            if (Want "wii-pointer-mode") {
                                # The Pointer Mode combo inside the card has no
                                # UIA peer: the card template strips its whole
                                # subtree. Click its measured position, after
                                # which the popup's ListItem peers realize at
                                # the WINDOW ROOT and keyboard selection works.
                                $wrPf = New-Object Win32+RECT
                                [Win32]::GetWindowRect($script:hwnd, [ref]$wrPf) | Out-Null
                                [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
                                [Win32]::ClickAt([int]($wrPf.Left + 0.232 * ($wrPf.Right - $wrPf.Left)),
                                                 [int]($wrPf.Top  + 0.466 * ($wrPf.Bottom - $wrPf.Top)))
                                Start-Sleep -Milliseconds 700
                                $liPf = New-Object System.Windows.Automation.PropertyCondition(
                                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                                    [System.Windows.Automation.ControlType]::ListItem)
                                $fpsPf = $null
                                foreach ($it in $script:uiaWin.FindAll($TD, $liPf)) {
                                    if ($it.Current.Name -eq "FPS Mouse") { $fpsPf = $it; break }
                                }
                                if ($fpsPf) {
                                    try { $fpsPf.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
                                    catch { Click-El $fpsPf -Label "FPS Mouse" | Out-Null }
                                } else {
                                    [System.Windows.Forms.SendKeys]::SendWait("{DOWN}"); Start-Sleep -Milliseconds 400
                                    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
                                }
                                Start-Sleep -Milliseconds 900
                                Cap "wii-pointer-mode"
                            }
                        }
                    }
                }
            }
        }
        # Extended is CARD index 3 (type-group order) and PAD index 4
        # (creation order). The preset is written by pad index and the card
        # is clicked by card index, so both numbers appear here on purpose.
        foreach ($vpF in @(
            @{ Shot = "pad-extended-steam-controller"; Id = "steam-controller-2" },
            # steam-deck-composite, not the plain steam-deck: only the
            # composite has an entry in ValveReportPackers.ByProfileId, so
            # the plain id falls back to a generic gamepad frame and is the
            # least representative of the five. Both resolve to the same
            # Deck body in the Preview tab.
            @{ Shot = "pad-extended-steam-deck";       Id = "steam-deck-composite" })) {
            if (-not (Want $vpF.Shot)) { continue }
            if (-not (Set-SlotPreset -PadIndex 4 -ProfileId $vpF.Id -XmlPath $PadForgeXml -ExePath $PadForgeExe -ClearCustomize)) {
                Write-Host "  !! SKIPPED $($vpF.Shot)" -ForegroundColor Red
                continue
            }
            Start-Sleep -Milliseconds 2000
            $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
            Nav "Dashboard"; Start-Sleep -Milliseconds 1500
            $shV2 = Find-UIA -Aid "SlotsItemsControl"
            $cdV2 = @()
            if ($shV2) { try { $cdV2 = @($shV2.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdV2 = @() } }
            if ((Get-Count $cdV2) -le $extIdxF) {
                Write-Host "  !! Extended slot card missing -- SKIPPED $($vpF.Shot)" -ForegroundColor Red
                continue
            }
            Open-SlotCard $cdV2[$extIdxF] "Extended Slot card ($($vpF.Id))" | Out-Null
            $ppV = Find-UIA -Aid "PadPageView"
            if (-not $ppV) {
                Write-Host "  !! no pad page -- SKIPPED $($vpF.Shot)" -ForegroundColor Red
                continue
            }
            Tab "Preview" | Out-Null
            # The Helix host rebuilds the persona's mesh on the profile
            # change, so give it a beat before the frame.
            Start-Sleep -Milliseconds 3000
            Dismiss-AssignBanner | Out-Null
            Wait-EngineForging | Out-Null
            # ASK THE CONTROL WHAT IT IS SHOWING. The file said the id had
            # landed and the page was still on Custom.
            $seenV = Get-PresetText
            if (-not $seenV -or $seenV -notmatch '(?i)steam') {
                Write-Host "  !! the preset reads '$seenV', not a Valve persona -- SKIPPED $($vpF.Shot)" -ForegroundColor Red
                continue
            }
            Write-Host "  preset on screen: $seenV" -ForegroundColor Green
            Cap $vpF.Shot
        }
    }

    # ── Mapping grid editors: the Xbox slot's rows and the PlayStation
    #    slot's Motion Roll row ──
    # The rows exist only after an auto-map, and an auto-map runs only on an
    # assignment made through the Devices page, so this stages the way the 2D
    # entry does, then lets Ensure-MacrosLoaded write the rows these shots show
    # (Write-SlotStructures) with the app closed.
    $xboxGridShots = @("pad-mappings", "mapping-sensitivity", "mapping-rapid-trigger", "pad-stick-trim", "icon-picker")
    $gridWanted = @($xboxGridShots | Where-Object { Want $_ })
    $motionWanted = Want "mapping-motion-rows"
    if ($gridWanted.Count -gt 0 -or $motionWanted) {
        Write-Host "[focused] Mapping grid: $((@($gridWanted) + @(if ($motionWanted) { 'mapping-motion-rows' })) -join ', ')"
        Nav "Devices"; Start-Sleep -Milliseconds 1500
        $gridStaged = $true
        $pairs = @()
        if ($gridWanted.Count -gt 0) {
            foreach ($a in @(@("DualSense Wireless Controller", "1", $false), @("Xbox Series X GIP", "1", $true))) {
                $ok = if ($a[2]) { Assign-DeviceToSlot -DeviceNamePart $a[0] -SlotNumberLabel $a[1] -Reassert | Select-Object -Last 1 }
                      else { Assign-DeviceToSlot -DeviceNamePart $a[0] -SlotNumberLabel $a[1] | Select-Object -Last 1 }
                if ($ok -ne $true) { $gridStaged = $false }
            }
            $pairs += , @("DualSense Wireless Controller", 0)
            $pairs += , @("Xbox Series X GIP", 0)
        }
        if ($motionWanted) {
            $ok = Assign-DeviceToSlot -DeviceNamePart "DualSense Wireless Controller" -SlotNumberLabel "2" | Select-Object -Last 1
            if ($ok -ne $true) { $gridStaged = $false }
            $pairs += , @("DualSense Wireless Controller", 1)
        }
        if ($gridStaged) { $gridStaged = Wait-SavedAssignments -LastClick (Get-Date) -Pairs $pairs }
        if (-not $gridStaged) {
            Refuse-Shots (@($gridWanted) + @(if ($motionWanted) { "mapping-motion-rows" })) "the grid slots were not staged"
        } else {
            Ensure-MacrosLoaded -XmlPath $PadForgeXml -ExePath $PadForgeExe | Out-Null
            Start-Sleep -Milliseconds 2000
            $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
            if ($gridWanted.Count -gt 0) {
                Nav "Dashboard"; Start-Sleep -Milliseconds 1500
                $shG = Find-UIA -Aid "SlotsItemsControl"
                $cdG = @()
                if ($shG) { try { $cdG = @($shG.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdG = @() } }
                $ppG = if ((Get-Count $cdG) -ge 1) { Open-SlotCard $cdG[0] "Xbox slot card (Mappings)" } else { $null }
                if ($ppG -and (Tab "Mappings")) {
                    Dismiss-AssignBanner | Out-Null
                    Start-Sleep -Milliseconds 800
                    Cap "pad-mappings"
                    Open-MappingRow 18 "Left Stick X"; Cap "mapping-sensitivity"
                    Open-MappingRow 17 "Right Trigger"; Cap "mapping-rapid-trigger"
                    Open-MappingRow 16 "Left Trigger"; Cap "pad-stick-trim"
                    Capture-IconPicker
                } else { Refuse-Shots $gridWanted "the Xbox slot's Mappings tab did not open" }
            }
            if ($motionWanted) {
                Nav "Dashboard"; Start-Sleep -Milliseconds 1500
                $shP = Find-UIA -Aid "SlotsItemsControl"
                $cdP = @()
                if ($shP) { try { $cdP = @($shP.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdP = @() } }
                $ppP = if ((Get-Count $cdP) -ge 2) { Open-SlotCard $cdP[1] "PlayStation slot card (Motion Roll)" } else { $null }
                if ($ppP) { Dismiss-AssignBanner | Out-Null; Capture-MotionRow }
                else { Refuse-Shots @("mapping-motion-rows") "the PlayStation pad page did not open" }
            }
        }
    }

    # Last: it changes the PlayStation slot's preset for the rest of the run.
    Capture-Ds3Preset

    # Missed means this run did not write it. Existence alone passed the four
    # picker shots of the 5.0.0 prep's first targeted run, which no focused
    # recipe took, because run 11 had left files under their names.
    $missed = @($Only | Where-Object {
        $mp = Join-Path $script:OutputDir "$_.png"
        -not (Test-Path $mp) -or ($script:CaptureRunStart -and (Get-Item $mp).LastWriteTime -lt $script:CaptureRunStart)
    })
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

# External control (#366): the Allow External Control checkbox sits with the
# auto-switch checkbox at the top of Profile Management, so it is already in
# the unscrolled frame. Verify that before capturing rather than trusting the
# layout: this is a new control on a page that moved this cycle, and a shot
# named after a checkbox that is off screen is worse than no shot.
$extCb = Find-UIA -Name "Allow External Control by Launchers and Scripts"
$extR = Get-Rect $extCb
$wrEx = New-Object Win32+RECT
[Win32]::GetWindowRect($script:hwnd, [ref]$wrEx) | Out-Null
if ($null -ne $extR -and $extR.Y -ge ($wrEx.Top + 60) -and ($extR.Y + $extR.Height) -le ($wrEx.Bottom - 60)) {
    Cap "profiles-external-control"
} else {
    Write-Host "  !! External Control checkbox not in frame -- SKIPPED profiles-external-control" -ForegroundColor Red
}
# Live FOREGROUND readout (Profiles.md): the readout line only renders while
# auto-switch is on, so flip the checkbox on, capture, then flip it back. The
# readout shows whatever exe is in front (PadForge during capture, so it reads
# unmatched); a matched/lit readout needs the profile's game running in front,
# which the capture can't stage. Toggling back keeps auto-switch behavior off
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
    Open-SlotCard $slots[0] "Xbox Slot card" | Out-Null

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
    # UIA can't always reach the 3D toggle inside the model host (it overlays a
    # Helix viewport), so the fallback is a window-fraction coordinate. The
    # check is Capture-AnnotationOverlay's, and it matters here because the 2D
    # shots below share this switch.
    Capture-AnnotationOverlay -Aid "AnnotationToggle" -Label "Annotation toggle (3D)" `
        -Shots @("pad-mapping-annotations", "3d-model-annotation-overlay") `
        -FallbackPoint { param($r) @([int]($r.Left + 0.925 * ($r.Right - $r.Left)), [int]($r.Top + 0.161 * ($r.Bottom - $r.Top))) }

    # 5. Controller 2D view. Set-ViewMode waits for the app to save the view
    # it asked for before anything is saved under a 2D name: an unverified
    # click shipped the 3D view as all three 2D shots, twice.
    Write-Host "[$(Next)/$total] Controller - 2D view"
    if (Set-ViewMode '2D') {
        Start-Sleep -Milliseconds 600
        Cap "pad-controller-2d"
        # 5a. Annotation overlay on the 2D preview (2D-Overlay-System.md).
        Capture-2DAnnotationOverlay
        if (-not (Set-ViewMode '3D')) {
            $script:StateFailures += "the 3D view did not come back after pad-controller-2d"
            Write-Host "  !! 3D view did not come back" -ForegroundColor Red
        }
        Start-Sleep -Milliseconds 500
    } else {
        Refuse-Shots @("pad-controller-2d", "2d-annotation-overlay") "the 2D view did not open"
    }

    # 7. Mappings, before any macro is selected. A selected macro breaks the
    # next tab switch (the rule above the Disconnect editor), and the run
    # that selected Quick Combo first shipped the Macros tab as pad-mappings.
    Write-Host "[$(Next)/$total] Mappings"
    Tab "Mappings"; Start-Sleep -Milliseconds 800; Cap "pad-mappings"

    # 7a. Three row editors, each on a row Write-SlotStructures set up, opened
    # bottom-up (Open-MappingRow has the geometry):
    #   Left Stick X (18): the generic Sensitivity knob, which rides a row
    #     whose primary descriptor is a plain "Axis N"
    #     (SourceCoercion.IsGenericSensitivityDescriptor), here "Axis 0".
    #   Right Trigger (17): Primary Mode Rapid Trigger and its Distance.
    #   Left Trigger (16): Combine Stick Trim and its settings strip.
    Write-Host "  Mapping: row editors (Sensitivity, Rapid Trigger, Stick Trim)"
    Open-MappingRow 18 "Left Stick X"; Cap "mapping-sensitivity"
    Open-MappingRow 17 "Right Trigger"; Cap "mapping-rapid-trigger"
    Open-MappingRow 16 "Left Trigger"; Cap "pad-stick-trim"

    # 7b. The icon picker, from the shift layer dialog the toolbar opens.
    Capture-IconPicker


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
    # The old -18 (six wheel events) left the Range heading on the bottom
    # edge, in 4.5.3 and in 5.0.0 run 5 alike, so the shot never showed the
    # button it is named for. Each event moves this page 72 px, measured off
    # run 5 (the Deadzone heading moved 432 px for six). The Range heading
    # sits 1878 px down the unscrolled page, so 22 events put it about
    # 0.19 H from the top, and one event either way still leaves it in view.
    Write-Host "  Sticks: boundary calibration (Range section)"
    ScrollContent -Clicks -66
    Start-Sleep -Milliseconds 400
    Cap "pad-sticks-boundary-calibration"
    ScrollContent -Clicks 90

    # 11. Triggers
    Write-Host "[$(Next)/$total] Triggers"
    # Leave the Sticks tab by the Triggers tab's measured spot before any UIA
    # search. While the Sticks tab is up, every UIA walk crawls: runs 5, 6
    # and 7 of the 5.0.0 capture spent 65 to 77 s per search here with
    # PadForge answering WM_NULL throughout, run 5 died of it and run 7
    # would have retried for about 40 minutes. Off Sticks, searches answer
    # at once (run 6). The spot is the device strip's Triggers tab on the
    # Xbox slot with the DualSense selected, 0.669 W, 0.1174 H, measured
    # off pad-sticks at 2582x1550. Tab then verifies the selection.
    $wrTr = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrTr) | Out-Null
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    [Win32]::ClickAt([int]($wrTr.Left + 0.669 * ($wrTr.Right - $wrTr.Left)), [int]($wrTr.Top + 0.1174 * ($wrTr.Bottom - $wrTr.Top)))
    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 1500
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
    # Leave the Triggers tab the way [11] left Sticks. Run 10 of the 5.0.0
    # prep found Triggers crawls too: 65 to 89 s per UIA search, and Tab's
    # retries would have spent most of an hour on this one switch. The spot
    # is the device strip's Force Feedback tab, 0.7246 W, 0.1174 H, measured
    # off run 10's pad-triggers at 2582x1550 (Triggers measured 0.669 there,
    # the constant above). Tab then verifies on the Force Feedback tab.
    $wrFf = New-Object Win32+RECT
    [Win32]::GetWindowRect($script:hwnd, [ref]$wrFf) | Out-Null
    [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
    [Win32]::ClickAt([int]($wrFf.Left + 0.7246 * ($wrFf.Right - $wrFf.Left)), [int]($wrFf.Top + 0.1174 * ($wrFf.Bottom - $wrFf.Top)))
    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 1500
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

    # 6. Macros. Runs after every other Xbox-slot tab: selecting a macro breaks
    # the next tab switch, so nothing but macro shots may follow it.
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
            # under a DisplayMemberPath template. When the name-find misses, the old
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
    $macroNames = @("Quick Combo", "Volume Control", "Sleep Controller", "Center Cursor", "Rapid Fire", "Aim Layer", "Key Glow")
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
    # UIA Text peer, and the combo sits directly to its right (label Width=80 then
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
        # row's combo sits at 0.4278 W / 0.3981 H, measured off pad-macros at
        # 2582x1550 in the 5.0.0 prep. The Trigger panel grew a Layer row above
        # it, and the old 0.36 H landed on Source: the 4.5.x frame and run 11's
        # both photographed the Source dropdown under this name.
        Write-Host "  Add from List label not in UIA, using the coordinate fallback" -ForegroundColor DarkGray
        $wrAL = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrAL) | Out-Null
        $comboX = [int]($wrAL.Left + 0.4278 * ($wrAL.Right - $wrAL.Left))
        $comboY = [int]($wrAL.Top  + 0.3981 * ($wrAL.Bottom - $wrAL.Top))
    }
    [Win32]::ForceFG($script:hwnd)
    Start-Sleep -Milliseconds 100
    [Win32]::ClickAt($comboX, $comboY); Start-Sleep -Milliseconds 800
    if ($script:MacrosPresent) { Cap "macro-add-from-list" -AllowModal } else { Write-Host "  skipped macro-add-from-list (no macros)" -ForegroundColor Yellow }
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 300

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
    # Macro list rows, measured off the 2560x1539 captures of 2026-10-03:
    # first row 0.241 H, 0.0441 H per row, list x 0.215 W. Sleep Controller is
    # row 3 (index 2). With a two-button trigger the chips stack one per line,
    # and the first action chip sits at 0.6897 H (x 0.383 W). The 0.654 H this
    # used before the chips stacked lands under the Add Action button, so
    # macro-disconnect, macro-move-mouse, macro-repeat-key, macro-turbo and
    # macro-switch-layer all shipped with no action editor open. The 0.594 H
    # before that landed on Add Action itself and appended a stray action.
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 2 * 0.0441) * $mh)); Start-Sleep -Milliseconds 800  # Sleep Controller row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.6897 * $mh)); Start-Sleep -Milliseconds 800  # its Disconnect action chip
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
    # sits at the same 0.6897 H. Selecting another macro row inside the Macros
    # tab is safe; it is the NEXT TAB SWITCH that macro selection breaks, and
    # the following section re-navs via Dashboard.
    Write-Host "  Macro: Move Mouse to Position editor (#9)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 3 * 0.0441) * $mh)); Start-Sleep -Milliseconds 800  # Center Cursor row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.6897 * $mh)); Start-Sleep -Milliseconds 900  # its MoveMouse action chip
    if ($script:MacrosPresent) { Cap "macro-move-mouse" } else { Write-Host "  skipped macro-move-mouse (no macros)" -ForegroundColor Yellow }

    Write-Host "  Macro: Repeat Key While Held editor (#9)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 4 * 0.0441) * $mh)); Start-Sleep -Milliseconds 800  # Rapid Fire row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.6897 * $mh)); Start-Sleep -Milliseconds 900  # its RepeatKey action chip
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

    # Switch Layer action editor (#377). Sixth macro row, so index 5 on the
    # same 0.241 + n*0.0441 ladder the three above ride, and the same 2-chip
    # trigger block, so its action chip sits at the same 0.6897 H. The editor
    # is gated on SelectedAction like every other one, so the row click alone
    # renders nothing: the action chip has to be clicked too.
    Write-Host "  Macro: Switch Layer (#377, layer dropdown)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 5 * 0.0441) * $mh)); Start-Sleep -Milliseconds 800  # Aim Layer row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.6897 * $mh)); Start-Sleep -Milliseconds 900  # its SwitchLayer action chip
    if ($script:MacrosPresent) { Cap "macro-switch-layer" } else { Write-Host "  !! skipped macro-switch-layer (no macros)" -ForegroundColor Red }

    # Set Chroma Color action editor (#468), the seventh macro row (index 6)
    # on the same ladder, with the same two-button trigger block.
    Write-Host "  Macro: Set Chroma Color (#468, color card)"
    [Win32]::ForceFG($script:hwnd)
    [Win32]::ClickAt([int]($wrMc.Left + 0.215 * $mw), [int]($wrMc.Top + (0.241 + 6 * 0.0441) * $mh)); Start-Sleep -Milliseconds 800  # Key Glow row
    [Win32]::ClickAt([int]($wrMc.Left + 0.383 * $mw), [int]($wrMc.Top + 0.6897 * $mh)); Start-Sleep -Milliseconds 900  # its SetChromaColor action chip
    if ($script:MacrosPresent) { Cap "macro-set-chroma-color" } else { Write-Host "  !! skipped macro-set-chroma-color (no macros)" -ForegroundColor Red }

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
        Open-SlotCard $cards[1] "PlayStation Slot card" | Out-Null

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
            if (Set-ViewMode '2D') {
                Start-Sleep -Milliseconds 700
                Cap "2d-touchpad-finger-dots"
                if (-not (Set-ViewMode '3D')) {
                    $script:StateFailures += "the 3D view did not come back after 2d-touchpad-finger-dots"
                    Write-Host "  !! 3D view did not come back" -ForegroundColor Red
                }
                Start-Sleep -Milliseconds 500
            } else {
                Refuse-Shots @("2d-touchpad-finger-dots") "the 2D view did not open"
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

            # The Motion Roll row's editor (#475), LAST on this slot: it
            # selects a mapping row, and the next block re-navs via the
            # Dashboard.
            Capture-MotionRow
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
    Open-SlotCard $cards[2] "Nintendo Slot card" | Out-Null
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
    Open-SlotCard $cards[$extendedIdx] "Extended Slot card" | Out-Null
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

    # 15. Extended schematic view. A custom Extended slot shows the schematic
    # by default and hides the 2D/3D button (PadPage.ApplyViewMode), so there
    # is nothing to toggle. A click at the button's position lands on this
    # page's Preset box instead.
    Write-Host "[$(Next)/$total] Extended schematic view"
    Start-Sleep -Milliseconds 600
    Cap "pad-extended-schematic"

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
    Open-SlotCard $cards[$kbmIdx] "KBM Slot card" | Out-Null
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
        # Leave Sticks for Mappings by its spot before the device and tab
        # searches below, for the reason [11] gives. Mappings is the top
        # row's second tab on this slot, 0.5072 W, 0.0755 H, measured off
        # pad-sticks-momentum at 2582x1550.
        $wrKm = New-Object Win32+RECT
        [Win32]::GetWindowRect($script:hwnd, [ref]$wrKm) | Out-Null
        [Win32]::ForceFG($script:hwnd); Start-Sleep -Milliseconds 150
        [Win32]::ClickAt([int]($wrKm.Left + 0.5072 * ($wrKm.Right - $wrKm.Left)), [int]($wrKm.Top + 0.0755 * ($wrKm.Bottom - $wrKm.Top)))
        Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 1500
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
    Open-SlotCard $cards[$midiIdx] "MIDI Slot card" | Out-Null
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
    Open-SlotCard $cards[$vrIdx] "VR Slot card" | Out-Null
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

# 18a. The Updates card, which needs a finished update check in its frame.
Capture-UpdatesCard
# 18a-bis. The Input Engine card, with the 5.0.0 analog keyboard and
# Bliss-Box readers.
Capture-InputEngineCard

# 18b-20b. Every scrolled Settings card, by ANCHOR rather than by a click
# count. A fixed scroll is a guess about page LENGTH, and this page grew two
# cards this cycle: Assignment Prompts and Handheld PC Buttons landed above
# Battery Alerts, which pushed every count below them out by a card and would
# have shipped four shots framing their neighbors. The focused pass has been
# anchor-driven since 2026-08; the main pass was still counting clicks.
#
# Card order (PageOrderContractTests): Language, Appearance, Window, Updates,
# Input Engine, Assignment Prompts, Handheld PC Buttons, Battery Alerts, HidHide,
# HIDMaestro, MIDI Services, SteamVR, Community Configs, Settings File,
# Diagnostics. An anchor in view proves the card STARTS on screen, not that
# it FITS, so each entry carries the extra scroll its card needs below its
# heading.
#
# The SteamVR card shows the install-location row only while SteamVR is
# ABSENT and the Uninstall button only for a PadForge-owned install, so what
# it captures depends on the machine's state. Both are honest.
# settings-drivers, driver-status-flames and settings-driver-cards are three
# docs pages pointing at the same driver band from different depths.
Write-Host "[$(Next)/$total] Settings - cards"
$settingsCards = @(
    @{ Shot = "settings-assignment-prompts"; Anchor = "Assignment Prompts";     After = -4 },
    @{ Shot = "settings-handheld-buttons";   Anchor = "Handheld PC Buttons";    After = -4 },
    @{ Shot = "settings-battery-alerts";     Anchor = "Battery Alerts";         After = -6 },
    @{ Shot = "settings-hidhide";            Anchor = "Whitelisted Applications"; After = -6 },
    @{ Shot = "settings-drivers";            Anchor = "HIDMaestro Driver";      After = -4 },
    @{ Shot = "settings-driver-cards";       Anchor = "Windows MIDI Services";  After = -4 },
    @{ Shot = "driver-status-flames";        Anchor = "Windows MIDI Services";  After = -7 },
    @{ Shot = "settings-steamvr";            Anchor = "SteamVR";                After = -6 }
)
foreach ($sc in $settingsCards) {
    ScrollContent -Clicks 90        # back to the top so each search starts level
    if (Scroll-ToAnchor -Anchor $sc.Anchor) {
        ScrollContent -Clicks $sc.After
        Start-Sleep -Milliseconds 500
        Cap $sc.Shot
    } else {
        Write-Host "  !! anchor '$($sc.Anchor)' never came into view -- SKIPPED $($sc.Shot)" -ForegroundColor Red
    }
}

# Scroll back up
ScrollContent -Clicks 90

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
    Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Wii Remote" -SlotNumberLabel "4") "assigning Wii Remote to slot 4" | Out-Null
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
    Open-SlotCard $cds[$ci] "slot card $ci (Pointer probe)" | Out-Null
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

        # Grip card (#392). Taken here because this is the one point in the
        # run where the Wii Remote is both assigned and SELECTED: the Held As
        # dropdown is what turns a Remote held sideways or in a Wii Wheel
        # into a flat pad, so the card belongs on the device it was written
        # for, not on a DualSense that only ever points forward. It is the
        # FIRST card on the Gyro tab, so the unscrolled tab frames it.
        #
        # The tab itself gates on HasGyro OR HasAccel (PadPage.xaml.cs), the
        # widening #392 made for accelerometer-only remotes, so an
        # accel-only Remote reaches it too.
        Write-Host "  Wii Remote: Grip card (#392)"
        # ESC FIRST. The step above leaves the Pointer Mode combo's popup
        # open on some paths, and a popup eats the next click: the tab strip
        # never sees it, the page stays on Pointer, and the Grip lookup then
        # reports a missing card on a tab that was never opened.
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Start-Sleep -Milliseconds 400
        # The Grip card is the tab's first card, so the unscrolled tab frames
        # it. Tab verifies the selection took. The old gate asked UIA for the
        # card's "Grip" title, which the tab body never exposes, so every run
        # skipped this shot (run 6 of 5.0.0 spent 30 s on six searches).
        if (Tab "Gyro") {
            Start-Sleep -Milliseconds 1200
            Cap "pad-gyro-grip"
        } else {
            Write-Host "  !! Gyro tab not found on the Wii Remote -- SKIPPED pad-gyro-grip" -ForegroundColor Red
        }
    }
}
if (-not $ptrDone) { Write-Host "  !! Pointer tab not reachable" -ForegroundColor Yellow }

# ---- Valve personas on the Extended slot (#337 / #338) ----
# Two Extended presets that carry a whole Valve pad: its USB identity, its
# control names in the grid, and its own body in the Preview tab. Both shots
# are the Preview tab with the persona picked, so the 3D model is the subject.
#
# Runs AFTER the Extended block's own captures (pad-extended-configbar,
# pad-extended-schematic, pad-extended-clone-device) and after the Pointer
# probe, because switching the preset rewrites what those shots frame. The
# preset is left on the last Valve persona; the owner's real settings ride
# the backup and are restored in STEP 4, and no later shot reads this slot.
#
# THE PRESET IS WRITTEN, NOT PICKED. The Extended picker is a plain WPF
# ComboBox over roughly 195 profiles and WPF virtualizes it, so only the
# realized rows exist in the automation tree: a name search finds whatever
# happens to be scrolled into being and reports "not in the picker" about
# entries that are plainly in the list. The slot's preset is one string in
# AppSettings/SlotProfileIds, indexed by PAD index, so write it.
#
# Extended is CARD index 3 (type-group order) and PAD index 4 (creation
# order). Both numbers appear here on purpose.
Write-Host "[3b] Valve personas on the Extended slot"
$valvePresets = @(
    @{ Shot = "pad-extended-steam-controller"; Id = "steam-controller-2" },
    # steam-deck-composite, not the plain steam-deck: only the composite
    # has an entry in ValveReportPackers.ByProfileId, so the plain id falls
    # back to a generic gamepad frame. Both draw the same Deck body.
    @{ Shot = "pad-extended-steam-deck";       Id = "steam-deck-composite" }
)
$extIdxV = 3
foreach ($vp in $valvePresets) {
    if (-not (Want $vp.Shot)) { continue }
    if (-not (Set-SlotPreset -PadIndex 4 -ProfileId $vp.Id -XmlPath $PadForgeXml -ExePath $PadForgeExe -ClearCustomize)) {
        Write-Host "  !! SKIPPED $($vp.Shot)" -ForegroundColor Red
        continue
    }
    Start-Sleep -Milliseconds 2000
    $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$script:hwnd)
    Nav "Dashboard"; Start-Sleep -Milliseconds 1500
    $shV = Find-UIA -Aid "SlotsItemsControl"
    $cdV = @()
    if ($shV) { try { $cdV = @($shV.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdV = @() } }
    if ((Get-Count $cdV) -le $extIdxV) {
        Write-Host "  !! Extended slot card missing at index $extIdxV -- SKIPPED $($vp.Shot)" -ForegroundColor Red
        continue
    }
    Open-SlotCard $cdV[$extIdxV] "Extended Slot card ($($vp.Id))" | Out-Null
    $padPageV = Find-UIA -Aid "PadPageView"
    if (-not $padPageV) {
        Write-Host "  !! the Extended card click did not open a pad page -- SKIPPED $($vp.Shot)" -ForegroundColor Red
        continue
    }
    # Land on Preview so the persona's body is what the shot shows. The
    # Helix host rebuilds the mesh on the profile change, so give it a beat.
    if (-not (Tab "Preview")) {
        $rbV = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $tabsV = $padPageV.FindAll($TC, $rbV)
        if ((Get-Count $tabsV) -gt 0) { Click-El $tabsV[0] -Label "Preview tab (Valve)" -Delay 1200 | Out-Null }
    }
    Start-Sleep -Milliseconds 3000
    Dismiss-AssignBanner | Out-Null
    Wait-EngineForging | Out-Null
    $seenP = Get-PresetText
    if (-not $seenP -or $seenP -notmatch '(?i)steam') {
        Write-Host "  !! the preset reads '$seenP', not a Valve persona -- SKIPPED $($vp.Shot)" -ForegroundColor Red
        continue
    }
    Write-Host "  preset on screen: $seenP" -ForegroundColor Green
    Cap $vp.Shot
}

# ---- The DualShock 3's own model on the PlayStation slot (5.0.0) ----
Capture-Ds3Preset

# ---- Menus tab: the editor, a macro cell and icon packages (#390) ----
# All three shots come off ONE menu selection on the Xbox slot, which is where
# the injected macros live, so the macro cell can name a macro that exists.
#
# ASK THE SETTINGS FILE WHETHER THE MENU IS THERE. The menu ListBox is the
# same DisplayMemberPath control the macro list is, and that list exposes no
# ListItem peers at all: a presence gate built on UIA counting reported an
# empty list with five macros plainly on screen and skipped five good shots.
# The file answers it definitively.
$menuShots = @("pad-menus", "menu-macro-cell", "menu-icon-packs")
Write-Host "[3b] Menus: $($menuShots -join ', ') (#390)"
$menuSeen = 0
try {
    [xml]$mnChk = Get-Content $PadForgeXml
    $menuSeen = @($mnChk.PadForgeSettings.SelectNodes("SlotMappingSets/MappingSet/Menu")).Count
} catch { $menuSeen = 0 }
Write-Host "  menus in settings: $menuSeen"
if ($menuSeen -lt 1) {
    Write-Host "  !! NO MENU IN THE SETTINGS FILE -- SKIPPED $($menuShots -join ', ')" -ForegroundColor Red
    Write-Host "  !! rather than shipping the Menus tab's empty-state pane. Write-SlotStructures" -ForegroundColor Red
    Write-Host "  !! authors it on slot 0; check that <SlotMappingSets> existed when it ran." -ForegroundColor Red
} else {
    Nav "Dashboard"; Start-Sleep -Milliseconds 1000
    $shM = Find-UIA -Aid "SlotsItemsControl"
    $cdM = @()
    if ($shM) { try { $cdM = @($shM.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } catch { $cdM = @() } }
    $ppM = if ((Get-Count $cdM) -ge 1) { Open-SlotCard $cdM[0] "Xbox slot card (Menus)" } else { $null }
    if (-not $ppM) {
        Write-Host "  !! the Xbox pad page did not open -- SKIPPED $($menuShots -join ', ')" -ForegroundColor Red
    } elseif (-not (Tab "Menus")) {
        Write-Host "  !! Menus tab not found -- SKIPPED $($menuShots -join ', ')" -ForegroundColor Red
    } else {
        Start-Sleep -Milliseconds 900
        # The editor is hidden behind HasSelectedMenu, so an unselected tab
        # is the cold empty-state pane and nothing these shots name.
        if (Open-MenuEditor -Name "Combat Wheel") { Capture-MenuShots }
        else { Write-Host "  !! SKIPPED $($menuShots -join ', ')" -ForegroundColor Red }
    }
}

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
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Xbox Series X GIP" -SlotNumberLabel "1" -Unassign) "unassigning Xbox Series X GIP from slot 1" | Out-Null
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Logitech G29"      -SlotNumberLabel "1" -Unassign) "unassigning Logitech G29 from slot 1" | Out-Null
Capture-SourcePickerSet $script:SourcePickerTargets

# --- DualShock 3 (v4): motion tab + Devices dossier ---
# The DS3 rides slot 1 ALONE for these shots, so unassign the anchoring
# DualSense first (the Gyro tab and Devices dossier follow the selected
# device either way, but a solo slot makes the selection deterministic).
Write-Host "[3c] DualShock 3"
Nav "Devices"; Start-Sleep -Milliseconds 600
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "DualSense Wireless Controller" -SlotNumberLabel "1" -Unassign) "unassigning DualSense Wireless Controller from slot 1" | Out-Null
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "DualShock 3" -SlotNumberLabel "1") "assigning DualShock 3 to slot 1" | Out-Null
Start-Sleep -Milliseconds 800
Nav "Dashboard"; Start-Sleep -Milliseconds 900
$shD = Find-UIA -Aid "SlotsItemsControl"
$cdD = if ($shD) { @($shD.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
if ((Get-Count $cdD) -ge 1) {
    Open-SlotCard $cdD[0] "Xbox card (DS3)" | Out-Null
    Select-MappedDevice "DualShock 3" | Out-Null
    if (Tab "Gyro") { Start-Sleep -Milliseconds 700; Cap "pad-ds3-gyro" }
    else { Write-Host "  !! Gyro tab not found for the DS3" -ForegroundColor Yellow }
} else { Write-Host "  !! slot card not found for the DS3" -ForegroundColor Yellow }
Nav "Devices"; Start-Sleep -Milliseconds 700
if (Select-DeviceByName36 "DualShock 3") {
    Cap "devices-ds3"
    # Device Dossier card (Devices.md): the DS3 dossier is a rich example --
    # bridged Bluetooth PATH plus LINK/SERIAL rows. It sits at the top of the
    # detail pane, in frame with the selection, so capture it here.
    Cap "devices-dossier"
}
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "DualShock 3" -SlotNumberLabel "1" -Unassign) "unassigning DualShock 3 from slot 1" | Out-Null

# --- Haptic-tone audio controls (Controller-Audio.md) ---
# The "Play mirrored audio" + "High tones" groups render on the Audio tab only
# for haptic-actuator pads (DeviceHasHaptics: Joy-Con / Pro / Steam / Deck /
# SC2026). The DualSense doesn't qualify, so its Audio tab (pad-audio) lacks
# them. Swap the synthetic Steam Controller (VID 28DE / PID 1102 -> Family.Steam)
# onto slot 1 alone, open its Audio tab, capture, then unassign.
Write-Host "[3c] Haptic-tone audio controls (Steam Controller)"
Nav "Devices"; Start-Sleep -Milliseconds 600
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Steam Controller" -SlotNumberLabel "1") "assigning Steam Controller to slot 1" | Out-Null
Start-Sleep -Milliseconds 800
Nav "Dashboard"; Start-Sleep -Milliseconds 900
$shH = Find-UIA -Aid "SlotsItemsControl"
$cdH = if ($shH) { @($shH.FindAll($TC, [System.Windows.Automation.Condition]::TrueCondition)) } else { @() }
if ($cdH.Count -ge 1) {
    Open-SlotCard $cdH[0] "Xbox card (Steam haptics)" | Out-Null
    Select-MappedDevice "Steam Controller" | Out-Null
    if (Tab "Audio") { Start-Sleep -Milliseconds 700; Cap "pad-audio-haptic-controls" }
    else { Write-Host "  !! Audio tab not found for the Steam Controller" -ForegroundColor Yellow }
} else { Write-Host "  !! slot card not found for the Steam Controller" -ForegroundColor Yellow }
Nav "Devices"; Start-Sleep -Milliseconds 600
Assert-Staged (Assign-DeviceToSlot -DeviceNamePart "Steam Controller" -SlotNumberLabel "1" -Unassign) "unassigning Steam Controller from slot 1" | Out-Null

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

Write-Host "[3b] Power / idle disconnect + battery, and Quick Charge (#372)"
Nav "Devices"; Start-Sleep -Milliseconds 600
# "DualSense Wireless Controller", not "DualSense". The web-controller lane
# creates rows named "DualSense Web Controller 1", they sort ahead of the pad,
# and a fragment match therefore selected a VIRTUAL device: the committed
# devices-power.png shows that row's dossier with no Power section in it at
# all, because a web:// device is not a disconnect target and has no battery.
# The full product name is unambiguous.
if (Select-DeviceByName36 "DualSense Wireless Controller") {
    Cap "devices-power"
    # The Power section lives low in the dossier, and the dossier is its OWN
    # scroll viewer on the right of the page. ScrollContent's center hover
    # lands in the card list, so the pane scroller is what reaches it.
    if (Scroll-PaneToAnchor -Anchor "Disconnect Bluetooth When Plugged In over USB") {
        Cap "devices-quick-charge"
    } else {
        Write-Host "  !! Quick Charge row never came into view -- SKIPPED devices-quick-charge" -ForegroundColor Red
        Write-Host "  !! it renders behind SelectedDevice.ShowQuickCharge, which needs a" -ForegroundColor Red
        Write-Host "  !! Bluetooth-pathed record or one whose serial parses as a BT address" -ForegroundColor Red
    }
    ScrollPane -Clicks 40
} else {
    Write-Host "  !! no DualSense Wireless Controller row -- SKIPPED devices-power AND devices-quick-charge" -ForegroundColor Red
}

# Head Tracker row (#355). The row exists only while HeadTrackingEnabled is
# on, which STEP 0 writes into the capture settings file.
Write-Host "[3b] Head Tracker device row"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "Head Tracker") {
    Cap "devices-head-tracking"
} else {
    Write-Host "  !! no Head Tracker row -- SKIPPED devices-head-tracking" -ForegroundColor Red
    Write-Host "  !! the row appears only with AppSettings/HeadTrackingEnabled true" -ForegroundColor Red
}

# Analog keyboard row (#468): the synthetic Razer Huntsman V3 Pro from STEP 0.
# With no key moving, the Key Depth preview shows its press-a-key hint.
Write-Host "[5.0.0] Devices: analog keyboard"
Nav "Devices"; Start-Sleep -Milliseconds 600
if (Select-DeviceByName36 "Razer Huntsman V3 Pro") {
    Cap "devices-analog-keyboard"
} else {
    Write-Host "  !! no Razer Huntsman V3 Pro row -- SKIPPED devices-analog-keyboard" -ForegroundColor Red
}

# The Light Gun section on the Wii Remote's dossier (#485).
Capture-LightGunSection

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
# Dashboard sections, by ANCHOR rather than by click count, for the same
# reason the Settings block above changed: the page was reordered this cycle.
# Head Tracking moved onto the Dashboard, Lightbar Mirrors and Razer Sensa HD
# Haptics are new, and the Drivers status strip left for Settings, so every
# fixed count below the Services header now lands a section early or late.
#
# Section order (PageOrderContractTests): Input Engine, Virtual Controllers,
# then Services: Web Controller, Remote Link, Head Tracking, Motion Server,
# Lightbar Mirrors, Razer Sensa HD Haptics, Overlays, Touchpad Overlay.
#
# THE SECTION TITLES ARE UPPERCASED IN THE VIEW. DashboardPage.xaml runs
# every SectionTitle through UpperConverter, and a TextBlock's UIA Name is
# what it RENDERS, so "Remote Link" matches nothing and "REMOTE LINK" is the
# anchor. The DSU shot anchors on its checkbox label instead, which is not
# converted.
Write-Host "[3b] Dashboard sections"
#
# ANCHOR ON THE SECTION'S LAST CONTROL, NOT ITS HEADING. A heading in view
# proves the section STARTS on screen. The 4.4.0 run anchored
# dashboard-head-tracking on its heading, the heading entered from the
# bottom, and the frame that shipped was four fifths Remote Link with one
# line of Head Tracking under it. Naming both ends of a section makes the
# gate say what the shot means.
$dashSections = @(
    @{ Shot = "remote-link";               Anchors = @("REMOTE LINK", "Or Connect by Address (Advanced)");  After = 0 },
    @{ Shot = "dashboard-head-tracking";   Anchors = @("HEAD TRACKING", "Set Neutral");                     After = 0 },
    @{ Shot = "dsu-port-box";              Anchors = @("MOTION SERVER", "Enable DSU Motion Server (CemuHook Motion Provider Protocol)"); After = -4 },
    # One frame carrying both mirror families and the haptics section beside
    # them, which is what features/lightbar-mirrors.md shows. The second
    # anchor is the Sensa section's own checkbox rather than its heading, so
    # the haptics controls are in the picture and not just its title.
    # SKIPS on this machine at 1033px of client height: the span from the
    # LIGHTBAR MIRRORS heading to the Sensa checkbox (measured at y=4252)
    # is taller than one frame, so the two anchors never share a picture.
    # The section content has not changed since the shot was last taken,
    # so the skip costs nothing today. Framing it again needs either a
    # taller capture window or a composition that does not demand both.
    @{ Shot = "dashboard-lightbar-mirrors"; Anchors = @("LIGHTBAR MIRRORS", "Send Rumble to Sensa HD Haptics"); After = 0 }
)
foreach ($ds in $dashSections) {
    Nav "Dashboard"; Start-Sleep -Milliseconds 800
    ScrollContent -Clicks 90
    # 90 steps: the 5.0.0 Dashboard is taller, and run 6 framed Head
    # Tracking at step 40, the old limit, then lost Motion Server below it.
    if (Scroll-ToAnchors -Anchors $ds.Anchors -MaxSteps 90) {
        if ($ds.After -ne 0) { ScrollContent -Clicks $ds.After; Start-Sleep -Milliseconds 400 }
        Cap $ds.Shot
    } else {
        Write-Host "  !! Dashboard anchors [$($ds.Anchors -join ', ')] never framed together -- SKIPPED $($ds.Shot)" -ForegroundColor Red
    }
    ScrollContent -Clicks 90
}
# The web controller's plain HTTP address (5.0.0), framed from its heading.
Capture-WebPlainSection

# The Pair dialog, every family it offers (Capture-PairDialog).
Capture-PairDialog

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
Write-Host "=== STEP 3c-ter: Profile edit dialog, polling override (#365) ===" -ForegroundColor Cyan
# The per-profile polling rate lives in the profile Edit dialog, so the shot
# needs the dialog open on a real profile. Edit is DISABLED with nothing
# selected and for the built-in Default, so the injected "Rocket League"
# profile is selected first. Ordinary FluentWindow modal: found by
# EnumWindows like the pair / NFC / workshop dialogs, closed by WM_CLOSE.
# Cancel, never Save: a save would rewrite the profile every later
# Profiles-page shot frames.
Nav "Profiles"; Start-Sleep -Milliseconds 1000
$rlCard = Find-UIARetry -Name "Rocket League"
if (-not $rlCard) {
    Write-Host "  !! no 'Rocket League' profile card -- SKIPPED profile-polling-override" -ForegroundColor Red
} else {
    Click-El $rlCard -Label "Rocket League profile card" -Delay 900 | Out-Null
    $editBtn = Find-UIA -Name "Edit" -CT ([System.Windows.Automation.ControlType]::Button)
    if (-not $editBtn) {
        Write-Host "  !! Edit button not found -- SKIPPED profile-polling-override" -ForegroundColor Red
    } else {
        $editEnabled = $true
        try { $editEnabled = [bool]$editBtn.Current.IsEnabled } catch {}
        if (-not $editEnabled) {
            Write-Host "  !! Edit is disabled, so the card click did not select the profile -- SKIPPED profile-polling-override" -ForegroundColor Red
        } else {
            Click-El $editBtn -Label "Edit profile" -Delay 1600 | Out-Null
            $pdDlg = Find-DialogHwndByEnum -MinW 400 -MinH 300
            if ($pdDlg -eq [IntPtr]::Zero) {
                Write-Host "  !! profile dialog HWND not found -- SKIPPED profile-polling-override" -ForegroundColor Red
            } else {
                $pdUia = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$pdDlg)
                Write-Host "  profile dialog hwnd=$pdDlg '$($pdUia.Current.Name)'" -ForegroundColor Green
                # Prove the polling row is in the dialog before photographing
                # it. The dialog is fixed-size and the row is its last field,
                # so a build without #365 would give a dialog that looks fine
                # and is missing the one thing this shot exists for.
                $pdC = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Text)
                $sawPolling = $false
                foreach ($t in $pdUia.FindAll($TD, $pdC)) {
                    if ($t.Current.Name -eq "Polling Rate") { $sawPolling = $true; break }
                }
                Start-Sleep -Milliseconds 800
                if ($sawPolling) {
                    Cap "profile-polling-override" -AllowModal
                } else {
                    Write-Host "  !! no 'Polling Rate' row in the dialog -- SKIPPED profile-polling-override" -ForegroundColor Red
                }
                Close-DialogHwnd $pdDlg
                Close-AnyModal | Out-Null
            }
        }
    }
}

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
# Scroll to the card's own heading, as the focused recipe does. A fixed -40
# was a guess at page length and the page grew past it: the 4.5.x frame and
# run 11 of the 5.0.0 prep both photographed Updates and Input Engine under
# this name.
ScrollContent -Clicks 90
if (Scroll-ToAnchor -Anchor "Community Configs") {
    ScrollContent -Clicks -10
    Start-Sleep -Milliseconds 500
    Cap "settings-community-configs"
} else {
    Write-Host "  !! anchor 'Community Configs' never came into view -- SKIPPED settings-community-configs" -ForegroundColor Red
}
ScrollContent -Clicks 90

# ---- 23-24. Web controller ----
Write-Host "[$(Next)/$total] Web controller screenshots"
# Minimize PadForge so it doesn't cover Edge (never TOPMOST anywhere)
if ($script:consoleWnd -and $script:consoleWnd -ne [IntPtr]::Zero) {
    [Win32]::ShowWindow($script:consoleWnd, 5) | Out-Null  # SW_SHOW
}
[Win32]::ShowWindow($hwnd, 6) | Out-Null  # SW_MINIMIZE
Start-Sleep -Milliseconds 500
$webPort = 8080
# A targeted run that names no web shot skips the probe below, which tries
# every address this machine holds and costs minutes.
$webWanted = (Want "web-landing") -or (Want "web-controller")
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
        # Launch Edge with an isolated temp profile. It never touches the
        # default profile.
        # The certificate flags are the sibling of the curl -k probe above:
        # the server's cert is self-signed, so without them Edge renders its
        # own interstitial and the capture photographs that instead of the
        # page. capture_web.ps1 carries the same three flags.
        Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --ignore-certificate-errors --allow-insecure-localhost --test-type --app=$Url"
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
    # curl.exe, NOT Invoke-WebRequest, and the scheme is discovered rather
    # than assumed. #296 binds a self-signed certificate and serves https
    # whenever the binding succeeds, which it does whenever PadForge is
    # elevated, and PowerShell 5.1's client cannot complete that handshake:
    # schannel renegotiates and Invoke-WebRequest returns "Unable to connect
    # to the remote server" for BOTH schemes. That reads exactly like a dead
    # server. capture_web.ps1 found this and fixed it in its own copy on
    # 2026-08; this twin kept the old probe, so the 4.4.0 run skipped every
    # web shot as "server down" while the server was up and answering.
    # Fixing one instance of a trap and leaving its sibling is how this keeps
    # recurring. curl.exe ships in System32 on every supported Windows.
    function Probe-Web {
        param([string]$Url)
        $code = & curl.exe -sk -o NUL -w '%{http_code}' --max-time 6 $Url 2>$null
        return ($code -eq '200')
    }
    function Wait-Web {
        param([string]$Url, [int]$TimeoutSec = 45)
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline) {
            if (Probe-Web $Url) {
                Write-Host "  web server answering: $Url" -ForegroundColor Green
                return $true
            }
            Start-Sleep -Milliseconds 800
        }
        Write-Host "  !! web server never answered $Url -- SKIPPING web shots (kept existing)" -ForegroundColor Red
        return $false
    }

    # THE SERVER DOES NOT BIND LOOPBACK. Its whole point is a phone on the
    # same network, so it listens on the machine's LAN address, and the
    # Dashboard prints that address next to its QR code
    # ("Running on https://10.19.90.40:8080"). Probing localhost therefore
    # answers nothing no matter which scheme is used, which is what made
    # both schemes time out after the curl fix went in. Try loopback first,
    # since a future build binding it would be cheapest, then every IPv4
    # address this machine holds.
    $webHosts = @()
    if ($webWanted) {
        $webHosts = @('localhost')
        try {
            $webHosts += @(Get-NetIPAddress -AddressFamily IPv4 -EA SilentlyContinue |
                Where-Object { $_.IPAddress -ne '127.0.0.1' } |
                Select-Object -ExpandProperty IPAddress)
        } catch {}
    }
    $webBase = $null
    foreach ($h in $webHosts) {
        foreach ($try in 'https', 'http') {
            if (Probe-Web "${try}://${h}:${webPort}/") { $webBase = "${try}://${h}:${webPort}"; break }
        }
        if ($webBase) { break }
    }
    if (-not $webBase) {
        # One more sweep with a real wait, in case the server is still coming up.
        foreach ($h in $webHosts) {
            foreach ($try in 'https', 'http') {
                if (Wait-Web "${try}://${h}:${webPort}/" -TimeoutSec 10) { $webBase = "${try}://${h}:${webPort}"; break }
            }
            if ($webBase) { break }
        }
    }
    $webUp = [bool]$webBase
    if ($webUp) { Write-Host "  web server base: $webBase" -ForegroundColor Cyan }
    elseif ($webWanted) { Write-Host "  !! no host answered on port $webPort -- SKIPPING web shots (kept existing)" -ForegroundColor Red }
    else { Write-Host "  .. no web shot requested, server not probed" -ForegroundColor DarkGray }

    # Landing page (needs a few seconds for Edge to fully render)
    if ($webUp) { Cap-Web "$webBase/" "web-landing" 6000 }

    # Controller page (needs WebSocket for layout images)
    Write-Host "[$(Next)/$total] Web controller - gamepad"
    if ($webUp) { Cap-Web "$webBase/controller.html?layout=xbox360" "web-controller" 6000 }

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
Remove-Item $toastPriorPath -Force -EA SilentlyContinue
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
# A shot refused for an unknown view or overlay keeps its old file, which the
# stale count alone would pass off as a quiet miss.
if ((Get-Count $script:RefusedShots) -gt 0) {
    Write-Host ("  NOT SAVED (state unknown): {0}  <-- the run is INCOMPLETE" -f (Get-Count $script:RefusedShots)) -ForegroundColor Red
    foreach ($x in ($script:RefusedShots | Sort-Object -Unique)) { Write-Host "     $x" -ForegroundColor Red }
} else { Write-Host "  refused for state       : 0" -ForegroundColor Green }
if ((Get-Count $script:StateFailures) -gt 0) {
    Write-Host ("  FAILED STATE CHECKS     : {0}  <-- the run is INCOMPLETE" -f (Get-Count $script:StateFailures)) -ForegroundColor Red
    foreach ($x in $script:StateFailures) { Write-Host "     $x" -ForegroundColor Red }
} else { Write-Host "  failed state checks     : 0" -ForegroundColor Green }
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
if ((Get-Count $script:RefusedShots) -gt 0 -or (Get-Count $script:StateFailures) -gt 0) { exit 1 }
