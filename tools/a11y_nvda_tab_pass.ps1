<#
.SYNOPSIS
  NVDA Tab pass over a PadForge page: every utterance NVDA speaks, logged.

.DESCRIPTION
  Starts a portable NVDA with a silent synthesizer and its IO log (level 12,
  one "Speaking [...]" line per utterance), brings PadForge to the front,
  optionally selects a sidebar page by its AutomationId (Nav_<Page>) and then
  a section or a group of that page (-Then Section_Sticks), sends Tab a set
  number of times, then quits NVDA. A page, a section and a group are
  selected through their SelectionItem pattern, as a screen reader selects
  them, and only Tab is sent, so the pass changes no setting. Read the log
  with tools/a11y_nvda_speech_parse.py.

  Run it elevated. PadForge runs as administrator, and a portable NVDA reads
  an elevated app only when it runs elevated too (NVDA user guide, portable
  copy restrictions). Start PadForge first with the deploy recipe, since a
  PadForge started from inside an elevated script exits when that script's
  process tree ends. Output goes to OutDir, never beside PadForge.exe.

  The NVDA folder holds portable\nvda.exe and config\nvda.ini, whose settings
  are synth = silence, showWelcomeDialogAtStartup = False, askToExit = False,
  playStartAndExitSounds = False, autoCheck = False, allowUsageStats = False,
  askedAllowUsageStats = True, and the vision highlighter off.

.EXAMPLE
  Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','tools\a11y_nvda_tab_pass.ps1','-Label','dashboard','-Page','Dashboard'
#>
param(
    [string]$NvdaDir = (Join-Path $env:LOCALAPPDATA 'PadForgeStudio\tooling\nvda'),
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'PadForgeA11y'),
    [string]$Label = 'pass',
    # Sidebar page to select first (Dashboard, Profiles, Devices, Pad1,
    # Settings, About). Empty: tab through whatever page is in front.
    [string]$Page,
    # AutomationIds to select after the page, in order: a controller's
    # section (Section_Sticks), a Settings group (Settings_Group_Input).
    [string[]]$Then,
    [int]$Tabs = 80,
    # The longest a Tab waits for NVDA to speak before the next one goes.
    # Each Tab moves on as soon as a new line reaches NVDA's log.
    [int]$MaxWaitMs = 600
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$log = Join-Path $OutDir "nvda-io-$Label.log"
$status = Join-Path $OutDir "status-$Label.txt"
"start $(Get-Date -Format o)" | Set-Content $status

$pf = Get-Process PadForge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $pf) { "PadForge is not running" | Add-Content $status; exit 1 }
# The main window is the largest of PadForge's top-level windows.
# Process.MainWindowHandle is the first visible unowned window in Z order,
# which can be a topmost overlay.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$h = $pf.MainWindowHandle
$area = 0
$own = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pf.Id)
foreach ($top in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $own)) {
    $r = $top.Current.BoundingRectangle
    if (-not $r.IsEmpty -and $r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $h = [IntPtr]$top.Current.NativeWindowHandle }
}
"padforge pid=$($pf.Id) hwnd=$h" | Add-Content $status
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NvdaPassGate {
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
}
'@
# A modal dialog disables the main window and takes the keys, so the pass
# would read the dialog or nothing. Name the cause and stop before NVDA starts.
if (-not [NvdaPassGate]::IsWindowEnabled($h)) { "a dialog is open over the PadForge window: close it, then run the pass again" | Add-Content $status; exit 1 }

$nvdaExe = Join-Path $NvdaDir 'portable\nvda.exe'
Start-Process $nvdaExe -ArgumentList @('-l', '12', "--log-file=$log", "--config-path=$(Join-Path $NvdaDir 'config')", '-m')
# NVDA logs "NVDA initialized" once it is ready (source/core.py), and each
# utterance as "Speaking [...]" (source/speech/speech.py), so the pass waits
# on its log rather than on fixed pauses.
function Log-Length { if (Test-Path $log) { (Get-Item $log).Length } else { 0 } }
function Wait-Spoken([long]$from, [int]$maxMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $maxMs) {
        Start-Sleep -Milliseconds 20
        if ((Log-Length) -gt $from) { Start-Sleep -Milliseconds 40; return }
    }
}
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 20000) {
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'NVDA initialized' -SimpleMatch -Quiet)) { break }
    Start-Sleep -Milliseconds 100
}
"nvda ready after $($sw.ElapsedMilliseconds) ms" | Add-Content $status

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NvdaPassWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    // The Alt tap and the attached input queue get SetForegroundWindow past
    // the foreground lock, which refused it alone on 2026-10-06. Restore only
    // a minimized window, since PadForge saves its size and state.
    public static void Front(IntPtr h) {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint me = GetCurrentThreadId();
        AttachThreadInput(me, fg, true);
        if (IsIconic(h)) ShowWindow(h, 9);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        AttachThreadInput(me, fg, false);
    }
}
'@
$ws = New-Object -ComObject WScript.Shell
$null = $ws.AppActivate($pf.Id)
for ($k = 0; $k -lt 5 -and [NvdaPassWin]::GetForegroundWindow() -ne $h; $k++) {
    [NvdaPassWin]::Front($h)
    for ($t = 0; $t -lt 16 -and [NvdaPassWin]::GetForegroundWindow() -ne $h; $t++) { Start-Sleep -Milliseconds 25 }
}
Wait-Spoken (Log-Length) 1500
"foreground_is_padforge=$([NvdaPassWin]::GetForegroundWindow() -eq $h)" | Add-Content $status

# A sidebar entry, a section and a group each offer SelectionItem
# (NavItemsControl's peer, a RadioButton's), so each is selected without the
# pointer. Selecting moves no keyboard focus, so the Tabs start where focus
# already stands.
$picks = @()
if ($Page) { $picks += "Nav_$Page" }
if ($Then) { $picks += @($Then | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
if ($picks.Count -gt 0) {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    foreach ($id in $picks) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if (-not $el) { "$id not found" | Add-Content $status; continue }
        $from = Log-Length
        try {
            $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Wait-Spoken $from 1500
            Start-Sleep -Milliseconds 600
            "$id selected" | Add-Content $status
        } catch { "$id could not be selected: $($_.Exception.Message)" | Add-Content $status }
    }
}

for ($i = 0; $i -lt $Tabs; $i++) {
    if ([NvdaPassWin]::GetForegroundWindow() -ne $h) { "lost focus at tab $i" | Add-Content $status; break }
    $from = Log-Length
    [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
    Wait-Spoken $from $MaxWaitMs
}
Start-Process $nvdaExe -ArgumentList '-q'
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 10000 -and (Get-Process nvda -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 100 }
"done $(Get-Date -Format o)" | Add-Content $status
