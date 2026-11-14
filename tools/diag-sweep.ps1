# Runtime self-diagnostics harvest (code-audit lens 1o). Elevated.
# Deploys the freshly published build, launches it with PADFORGE_DIAG
# armed (the SdlDiagLog ring mirrors to a file), and walks the PadForge 6
# interface via UIA so lazily realized templates evaluate their bindings:
# every sidebar page, a controller's six sections and their parts, each
# extra's page with its More row, Search, Add a Controller, Get Profiles'
# three tabs (realizing the Workshop search results when the community
# config lookup is on) and Map Key Lights (the 1o coverage rule: page
# navigation alone never realizes dialogs). Then it relaunches clean with
# the mirror off. Acceptance bar: the harvest at
# C:\tmp\pfdiag-verify.log contains no error-class lines (BINDERR,
# FAILED, exception). Progress notes: C:\tmp\pf-sweep-out.txt
param(
    # The owner's answer to a run that stopped over a settings backup an
    # earlier run left (settings_guard.ps1). Ask, the default, is that
    # stop. File keeps the settings file and sets the backup aside. Backup puts
    # the backup back over the settings file. A run with nothing to ask
    # refuses File and Backup.
    [ValidateSet('Ask', 'File', 'Backup')]
    [string]$KeepSettingsFrom = 'Ask'
)
$ErrorActionPreference = 'Continue'
$out = 'C:\tmp\pf-sweep-out.txt'
Set-Content $out "sweep start $(Get-Date -Format o)" -Encoding utf8
function Note($m) { Add-Content $out $m -Encoding utf8 }

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

$src = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\PadForge.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\PadForge.exe'
$dst = 'C:\PadForge\PadForge.exe'
$diag = 'C:\tmp\pfdiag-verify.log'
Remove-Item $diag -Force -ErrorAction SilentlyContinue

# --- Owner settings guard -----------------------------------------------
# This sweep drives the real app through UIA, and on 2026-08-09 its page walk
# flipped a slot from Nintendo to Xbox and rewrote that slot's profile id
# (Type 5 to 0, switch-pro to xbox-series-xs-bt) in the owner's live
# PadForge.xml. The harness had no backup and no restore, so the damage was
# caught only by diffing against an external snapshot afterwards. Back the
# file up before touching anything, and put it back on every exit path.
#
# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. A backup a killed run left
# holds the owner's real settings, and the file beside it is the sweep's.
# One left behind after the settings were put back by hand sits beside the
# owner's current file. The marker tells the two apart (Get-BackupDoubt), and
# without its word the run stops and the owner decides. The sweep runs with
# ErrorActionPreference Continue, and this part does not: a backup that could
# not be taken ends the run before the sweep writes anything.
$pfXml = 'C:\PadForge\PadForge.xml'
$pfBak = Join-Path $env:TEMP 'PadForge.xml.diagsweep.bak'
$ErrorActionPreference = 'Stop'
$doubt = Get-BackupDoubt $pfXml $pfBak 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $pfXml $pfBak $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { Note $line; Write-Host $line -ForegroundColor Red }
    exit 1
}
# STOP THE APP FIRST. A leftover backup put back under a live PadForge would
# be saved over by the app's own state.
Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ. The run stops with
# neither file touched and starts PadForge again.
$doubt = Get-BackupDoubt $pfXml $pfBak $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $pfXml $pfBak $doubt)) { Note $line; Write-Host $line -ForegroundColor Red }
    Start-Process -FilePath $dst
    exit 1
}
if (Test-Path $pfBak) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $pfBak
        Remove-Item $pfBak -Force
        Remove-BackupMarker $pfBak
        Note "-KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept"
    } else {
        $kept = Save-SettingsCopy $pfXml
        Note "restoring the backup an earlier run left (the file it replaces is kept as $kept)"
        Copy-Item $pfBak $pfXml -Force
    }
}
# A backup is written only where none is, so a copy cut short can never leave
# half a file over a good backup. With no settings file there is nothing to
# back up, and no marker either.
if (-not (Test-Path $pfBak) -and (Test-Path $pfXml)) { Copy-Item $pfXml $pfBak -Force }
Set-BackupMarker $pfBak
$ErrorActionPreference = 'Continue'

try {

# Deploy
$copied = $false
for ($i = 1; $i -le 5; $i++) {
    taskkill /F /IM PadForge.exe 2>$null | Out-Null
    Start-Sleep 3
    try { Copy-Item $src $dst -Force -ErrorAction Stop; $copied = $true; break }
    catch { Note "copy attempt ${i}: $($_.Exception.Message)" }
}
Note "copied=$copied hash=$((Get-FileHash $dst -Algorithm SHA256).Hash.Substring(0,12))"

# The owner runs with StartMinimized and MinimizeToTray on, so a launch
# against their real settings goes straight to the notification area and
# NEVER CREATES A WINDOW. Every UIA lookup then fails, the sweep prints
# WINDOW NOT FOUND, walks nothing, and still finishes looking healthy.
# That is how two runs reported success while proving nothing at all.
# The settings file is already backed up and restored in the finally, so
# force the two flags off in the working copy before launching. An
# assignment is data: write it, do not try to click the window back.
if (Test-Path $pfXml) {
    $x = Read-SettingsXml $pfXml
    foreach ($tag in @('StartMinimized', 'MinimizeToTray')) {
        foreach ($n in @($x.SelectNodes("//$tag"))) { if ($n.InnerText -eq 'true') { $n.InnerText = 'false' } }
    }
    $kept = Save-SettingsXml $x $pfXml
    Note "forced StartMinimized=false and MinimizeToTray=false for this run (the file before it is kept as $kept)"
}

# Launch with the mirror armed
$env:PADFORGE_DIAG = $diag
Start-Process -FilePath $dst
Start-Sleep 15

# UIA navigation
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$root = $ae::RootElement
$TS = [System.Windows.Automation.TreeScope]
$TC = [System.Windows.Automation.Condition]::TrueCondition
$CT = [System.Windows.Automation.ControlType]
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, 'PadForge')
# The window with the most elements, once it has any. On 2026-09-30 the first
# top-level element named PadForge had none while the app's window was up
# (an elevated probe minutes later counted 376), and the walk that followed
# clicked nothing and still passed.
$win = $null
$best = 0
for ($i = 0; $i -lt 15 -and $best -eq 0; $i++) {
    foreach ($w in $root.FindAll($TS::Children, $cond)) {
        $n = $w.FindAll($TS::Descendants, $TC).Count
        if (-not $win -or $n -gt $best) { $win = $w; $best = $n }
    }
    if ($best -eq 0) { Start-Sleep 2 }
}
$walkSaw = 0
# What the walk meant to reach and did not. A clean harvest speaks for what ran.
$notReached = @()
if (-not $win) { Note 'WINDOW NOT FOUND' }
else {
    Note "window found, elements=$best"
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class W {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    public static void TakeForeground(IntPtr hwnd) {
        uint fgPid;
        uint fgTid = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
        uint myTid = GetCurrentThreadId();
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, true);
        SetForegroundWindow(hwnd);
        if (fgTid != myTid) AttachThreadInput(myTid, fgTid, false);
    }
    public static List<IntPtr> ForPid(uint want) {
        var r = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == want) r.Add(h); return true; }, IntPtr.Zero);
        return r;
    }
}
'@
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    $pfPid = [uint32]$win.Current.ProcessId

    # Foreground is a precondition for every synthetic click: a click that
    # lands while another window holds focus goes to that window. Retry the
    # restore/maximize/SetForegroundWindow trio until GetForegroundWindow
    # confirms the handle. Windows refuses SetForegroundWindow to a background
    # process while another window holds the focus, unless the two threads share
    # input for the call, which is what TakeForeground does, as capture_all.ps1's
    # ForceFG does.
    function ForceFG([IntPtr]$h) {
        for ($i = 0; $i -lt 5; $i++) {
            [W]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE (a minimized window defeats clicks)
            Start-Sleep -Milliseconds 250
            [W]::ShowWindow($h, 3) | Out-Null   # SW_MAXIMIZE for stable layout
            [W]::TakeForeground($h)
            Start-Sleep -Milliseconds 450
            if ([W]::GetForegroundWindow() -eq $h) { return $true }
        }
        return $false
    }

    # PadForge's window or a dialog of its own holds the focus.
    function InFront {
        $owner = [uint32]0
        [W]::GetWindowThreadProcessId([W]::GetForegroundWindow(), [ref]$owner) | Out-Null
        return $owner -eq $pfPid
    }

    function Click-At([int]$x, [int]$y) {
        [W]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 120
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)  # LDOWN
        [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)  # LUP
    }
    # Bounding-rect center, NOT GetClickablePoint, for an element that offers
    # no pattern for the step: a clickable point can miss the control.
    function Click-El($el, [double]$fx = 0.5, [double]$fy = 0.5) {
        if (-not $el) { return $false }
        if (-not (InFront)) {
            Note 'no click: another window is in front'
            return $false
        }
        try {
            $r = $el.Current.BoundingRectangle
            if ($r.Width -le 0 -or $r.Height -le 0) { return $false }
            Click-At ([int]($r.X + $r.Width * $fx)) ([int]($r.Y + $r.Height * $fy))
            return $true
        } catch { return $false }
    }
    function Find-ByName($name) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
        return $win.FindFirst($TS::Descendants, $c)
    }
    function Find-ByAid($aid) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $aid)
        return $win.FindFirst($TS::Descendants, $c)
    }
    function Find-Under($parent, $aid) {
        if (-not $parent) { return $null }
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $aid)
        try { return $parent.FindFirst($TS::Descendants, $c) } catch { return $null }
    }
    # An element by its AutomationId under $parent, waited for: a page lands
    # its content a beat after the step that shows it.
    function Wait-Aid($parent, $aid, [int]$ms = 3000) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        do {
            $el = Find-Under $parent $aid
            if ($el) { return $el }
            Start-Sleep -Milliseconds 150
        } while ($sw.ElapsedMilliseconds -lt $ms)
        return $null
    }
    # A Select answers as a click does on a sidebar entry
    # (NavItemAutomationPeer), a section or part button and a list row, with
    # no pointer moved. A click on the center is the fallback, as
    # capture_all.ps1's Select-El does.
    function Select-El($el) {
        if (-not $el) { return $false }
        try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return $true }
        catch { return (Click-El $el) }
    }
    function Invoke-El($el) {
        if (-not $el) { return $false }
        try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        catch { return (Click-El $el) }
    }
    function Expand-El($el) {
        if (-not $el) { return $false }
        try { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); return $true }
        catch { return $false }
    }
    # A page by its sidebar entry, Nav_ and the page's tag
    # (MainWindow.SetNavAutomationId). Home's tag is Dashboard.
    function Go($tag, $label) {
        ForceFG $hwnd | Out-Null
        $el = Find-ByAid ('Nav_' + $tag)
        $ok = Invoke-El $el
        Note "page ${label}: found=$($null -ne $el) opened=$ok"
        if (-not $ok) { $script:notReached += $label }
        Start-Sleep 2
        return $ok
    }

    Note "foreground: $(ForceFG $hwnd)"
    $allDesc = $win.FindAll($TS::Descendants, $TC)
    $walkSaw = $allDesc.Count
    Note "descendants=$walkSaw"

    # ---- Every page of the sidebar (#574). Devices opens its two folds, so
    # the rows of the devices PadForge remembers and of the other inputs
    # realize too. ----
    foreach ($p in @('Dashboard:Home', 'Devices:Devices', 'Profiles:Profiles', 'Settings:Settings', 'About:About')) {
        $tag, $label = $p.Split(':')
        if ((Go $tag $label) -and $tag -eq 'Devices') {
            foreach ($fold in @('Devices_Fold_NotConnected', 'Devices_Fold_OtherInputs')) {
                Note "devices fold ${fold}: $(Expand-El (Find-ByAid $fold))"
            }
            Start-Sleep 1
        }
    }

    # ---- A controller's page: the first controller entry (Nav_Pad and its
    # number), then each of the six sections and the parts of Buttons and of
    # Macros & Menus, by the ids capture_all.ps1's PadTabs table names. ----
    $navList = $win.FindFirst($TS::Children,
        (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, 'NavigationItems')))
    $pad = $null
    if ($navList) {
        foreach ($item in $navList.FindAll($TS::Children, $TC)) {
            if ($item.Current.AutomationId -match '^Nav_Pad\d+$') { $pad = $item; break }
        }
    }
    ForceFG $hwnd | Out-Null
    if ($pad -and (Invoke-El $pad)) {
        Start-Sleep 3
        Note "controller page: $($pad.Current.Name)"
        $padPage = Find-ByAid 'PadPageView'
        foreach ($step in @('Section_Buttons', 'TabController', 'MappingsTab', 'Section_Sticks', 'Section_Rumble',
                            'Section_Motion', 'Section_Lights', 'Section_Macros', 'MacrosTab', 'MenusTab')) {
            $ok = Select-El (Wait-Aid $padPage $step 4000)
            Note "controller ${step}: $ok"
            if (-not $ok) { $notReached += "controller $step" }
            Start-Sleep 2
        }
        Select-El (Find-Under $padPage 'Section_Buttons') | Out-Null
        Start-Sleep 1
    } else {
        Note 'no controller entry in the sidebar'
        $notReached += 'controller page'
    }

    # ---- Extras: each extra's page from its tile, with its More row open,
    # then back to the catalog. A tile opens through Extras_Open_ and the key
    # (ExtraTile.OpenIdPrefix). Its switch, which turns the extra on, is never
    # touched. ----
    if (Go 'Extras' 'Extras') {
        foreach ($key in @('WebController', 'HeadTracking', 'GameTelemetry', 'TwitchChat', 'RemoteLink',
                           'MotionServer', 'GameMods', 'Overlays', 'TouchpadOverlay', 'ScreenOverlay')) {
            $opened = Invoke-El (Find-ByAid ('Extras_Open_' + $key))
            $back = if ($opened) { Wait-Aid $win ('Extras_Back_' + $key) 3000 } else { $null }
            if (-not $back) {
                Note "extra ${key}: did not open"
                $notReached += "extra $key"
                continue
            }
            $more = Find-ByAid ('Extras_More_' + $key)
            $moreOpen = if ($more) { Expand-El $more } else { 'none' }
            Start-Sleep 2
            Note "extra ${key}: opened, more=$moreOpen, back=$(Invoke-El $back)"
            Start-Sleep 1
        }
    }

    # ---- Search over Home, with a query. No key is typed: the title bar's
    # box opens Search through its Invoke pattern, and the query is set
    # through the query box's Value pattern, as capture_all.ps1's
    # Capture-Search does. ----
    if (Go 'Dashboard' 'Home') {
        $opened = Invoke-El (Find-ByAid 'TitleSearchBox')
        $query = if ($opened) { Wait-Aid $win 'SearchQueryBox' 3000 } else { $null }
        if ($query) {
            try { $query.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('deadzone'); Note 'search query set: deadzone' }
            catch { Note "search query failed: $($_.Exception.Message)" }
            Start-Sleep 2
            Note "search closed: $(Invoke-El (Find-ByAid 'SearchCloseChip'))"
        } else {
            Note 'search did not open'
            $notReached += 'Search'
        }
        Start-Sleep 1
    }

    # ---- Add a Controller, drawn inside the window, from its sidebar entry,
    # and closed by its own close button with no tile picked. ----
    ForceFG $hwnd | Out-Null
    $opened = Invoke-El (Find-ByAid 'Nav_AddController')
    $addDlg = if ($opened) { Wait-Aid $win 'AddControllerDialog' 3000 } else { $null }
    if ($addDlg) {
        Start-Sleep 2
        Note "add a controller closed: $(Invoke-El (Find-Under $addDlg 'AddController_Close'))"
    } else {
        Note 'add a controller did not open'
        $notReached += 'Add a Controller'
    }
    Start-Sleep 1

    # ---- Get Profiles, opened from the Profiles page's head button, its
    # three tabs in turn (1o coverage rule: dialogs and their item templates
    # never realize from page navigation alone) ----
    $browse = $null
    if (Go 'Profiles' 'Profiles') { $browse = Find-ByAid 'Profiles_GetProfiles' }
    if ($browse) {
        Note "get profiles: $(Invoke-El $browse)"
        # The window is UIA-shy from the root: discover its HWND via Win32
        # EnumWindows (visible PadForge-PID window that is not the main
        # HWND, larger than 400x400), then attach with FromHandle.
        $dlgHwnd = [IntPtr]::Zero
        for ($t = 0; $t -lt 8 -and $dlgHwnd -eq [IntPtr]::Zero; $t++) {
            Start-Sleep 2
            foreach ($h in [W]::ForPid($pfPid)) {
                if ($h -ne $hwnd -and [W]::IsWindowVisible($h)) {
                    $r = New-Object W+RECT
                    [W]::GetWindowRect($h, [ref]$r) | Out-Null
                    if (($r.R - $r.L) -gt 400 -and ($r.B - $r.T) -gt 400) { $dlgHwnd = $h; break }
                }
            }
        }
        if ($dlgHwnd -eq [IntPtr]::Zero) {
            Note 'get profiles window not found'
            $notReached += 'Get Profiles'
        } else {
            $sb = New-Object System.Text.StringBuilder 256
            [W]::GetWindowText($dlgHwnd, $sb, 256) | Out-Null
            Note "get profiles hwnd=$dlgHwnd title='$($sb.ToString())'"
            $dlg = [System.Windows.Automation.AutomationElement]::FromHandle($dlgHwnd)
            [W]::TakeForeground($dlgHwnd)
            Start-Sleep 1
            # The window opens on Starters. Each tab is a radio selected
            # through its SelectionItem pattern, which raises Checked.
            foreach ($tabName in @('Starters', 'Community', 'File')) {
                $tab = Find-Under $dlg ('GetProfiles_' + $tabName)
                $ok = Select-El $tab
                Note "get profiles tab ${tabName}: $ok"
                if (-not $ok) { $notReached += "Get Profiles $tabName" }
                Start-Sleep 2
                if ($tabName -ne 'Community' -or -not $ok) { continue }
                $edit = $dlg.FindFirst($TS::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $CT::Edit)))
                if ($edit) {
                    try { ($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('skyrim'); Note 'search set: skyrim' }
                    catch { Note "search SetValue failed: $($_.Exception.Message)" }
                    Start-Sleep 15  # debounce + store search + card templates realizing
                    $li = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $CT::ListItem)
                    $tiles = $dlg.FindAll($TS::Descendants, $li)
                    Note "workshop tiles/cards=$($tiles.Count) first='$(if ($tiles.Count) { $tiles[0].Current.Name })'"
                } else {
                    # Not a defect on its own. CommunityConfigsPanel shows the
                    # games only when EnableCommunityConfigLookup is on
                    # (ApplyGateState). With it off the panel shows the cold gate
                    # and Get Profiles keeps its search box collapsed, so no search
                    # box exists to find. The gate view IS realized either way. Turning the
                    # setting on here would reach the live Steam network from a
                    # routine gate run, so the search view stays out of scope and
                    # the note says which of the two states was covered.
                    Note 'workshop search box not found (cold gate: community config lookup is off, search view not covered)'
                }
            }
            [W]::SendMessage($dlgHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
            Start-Sleep 2
            Note "get profiles closed=$(-not [W]::IsWindowVisible($dlgHwnd))"
        }
    } else {
        Note 'get profiles button not found'
        $notReached += 'Get Profiles'
    }

    # ---- Map Key Lights (1o coverage rule): opened from the first device row
    # that offers it and closed again. Nothing in it is pressed, so no light on
    # the device changes. A row is picked through its SelectionItem pattern, and
    # the button sits among the panel's tools, where it may lie below the
    # panel's view: it is opened through its Invoke pattern, which needs no
    # place on screen. ----
    $mapButton = $null
    if (Go 'Devices' 'Devices') {
        $listItem = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $CT::ListItem)
        $rows = @()
        $scroll = Find-ByAid 'Devices_ListScroll'
        if ($scroll) { $rows = @($scroll.FindAll($TS::Descendants, $listItem)) }
        for ($i = 0; $i -lt [Math]::Min($rows.Count, 40) -and -not $mapButton; $i++) {
            Select-El $rows[$i] | Out-Null
            Start-Sleep -Milliseconds 500
            $found = Find-ByName 'Map Key Lights'
            if ($found -and $found.Current.ControlType -eq $CT::Button) { $mapButton = $found }
        }
        Note "device rows read: $($rows.Count)"
    }
    if ($mapButton) {
        Note "map key lights: $(Invoke-El $mapButton)"
        $dlgHwnd = [IntPtr]::Zero
        for ($t = 0; $t -lt 4 -and $dlgHwnd -eq [IntPtr]::Zero; $t++) {
            Start-Sleep 1
            foreach ($h in [W]::ForPid($pfPid)) {
                if ($h -ne $hwnd -and [W]::IsWindowVisible($h)) {
                    $r = New-Object W+RECT
                    [W]::GetWindowRect($h, [ref]$r) | Out-Null
                    if (($r.R - $r.L) -gt 400 -and ($r.B - $r.T) -gt 400) { $dlgHwnd = $h; break }
                }
            }
        }
        if ($dlgHwnd -eq [IntPtr]::Zero) { Note 'map key lights dialog HWND not found' }
        else {
            $sb = New-Object System.Text.StringBuilder 256
            [W]::GetWindowText($dlgHwnd, $sb, 256) | Out-Null
            Note "map key lights dialog hwnd=$dlgHwnd title='$($sb.ToString())'"
            Start-Sleep 2
            [W]::SendMessage($dlgHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
            Start-Sleep 1
            Note "map key lights dialog closed=$(-not [W]::IsWindowVisible($dlgHwnd))"
        }
    } else {
        Note 'no row offers Map Key Lights'
        $notReached += 'Map Key Lights'
    }

    Go 'Dashboard' 'Home' | Out-Null
}

# Evaluate the acceptance bar this script's own header states. It used to
# stop at "sweep done", so the harness gathered the evidence and left the
# verdict to whoever remembered to grep the log. A bar nobody evaluates is a
# bar nobody fails.
$verdict = 'NO HARVEST'
if (Test-Path $diag) {
    # BINDERR and FAILED are matched CASE-SENSITIVELY on purpose. The ring
    # carries a VCGATE field literally spelled `failed=0`, and a
    # case-insensitive substring match reports five "error-class lines" on a
    # perfectly clean harvest. A bar that fails on a healthy run gets ignored
    # within a week, which is worse than no bar. Real failures in this log are
    # uppercase (BINDERR, "... FAILED"); the field is lowercase.
    $hits = Select-String -Path $diag -Pattern 'BINDERR', 'FAILED' -CaseSensitive -ErrorAction SilentlyContinue
    $hits += Select-String -Path $diag -Pattern 'exception' -ErrorAction SilentlyContinue
    $hits = $hits | Sort-Object LineNumber -Unique
    if ($hits) {
        $verdict = "FAIL ($($hits.Count) error-class line(s))"
        Note "ACCEPTANCE: $verdict"
        # Echo a bounded sample so the failure is actionable from this file
        # alone rather than requiring a second pass over the harvest.
        foreach ($h in $hits | Select-Object -First 20) {
            Note "    $($h.Line.Trim())"
        }
    }
    else {
        $verdict = 'PASS (no error-class lines)'
        Note "ACCEPTANCE: $verdict"
    }
}
else {
    Note "ACCEPTANCE: $verdict. $diag was never written. The mirror did not arm, so this run proves nothing."
}
# A clean harvest from a walk that saw no elements realized no page, tab or
# dialog, so it is no pass.
if ($walkSaw -eq 0) {
    $verdict = "BLIND: $verdict, but the walk saw no elements and realized nothing"
    Note "ACCEPTANCE: $verdict"
}
if ($notReached.Count -gt 0) {
    $verdict = "PARTIAL: $verdict, for what ran. Not reached: $($notReached -join ', ')"
    Note "ACCEPTANCE: $verdict"
}
Write-Host "diag-sweep acceptance: $verdict"

# Relaunch clean (no mirror)
taskkill /F /IM PadForge.exe 2>$null | Out-Null
Start-Sleep 3
Remove-Item Env:PADFORGE_DIAG -ErrorAction SilentlyContinue
Start-Process -FilePath $dst
Note "sweep done $(Get-Date -Format o)"

}
finally {
    # Always put the owner's settings back, including after a mid-sweep error.
    # With ErrorActionPreference Stop, as at the start: a restore that fails
    # ends here, and the backup and its marker stay for the next run.
    if (Test-Path $pfBak) {
        $ErrorActionPreference = 'Stop'
        Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 3
        # The file about to be replaced holds nothing but what the backup holds
        # and what this run wrote, so its copy is a courtesy and a failed one
        # does not hold the restore up. The marker goes as soon as the settings
        # are back, before the backup does.
        $kept = $null
        $keptBak = $null
        try { $kept = Save-SettingsCopy $pfXml }
        catch { Note "could not copy the sweep's settings file aside before the restore: $($_.Exception.Message)" }
        Copy-Item $pfBak $pfXml -Force
        Remove-BackupMarker $pfBak
        try {
            $keptBak = Save-SettingsCopy $pfBak
            Remove-Item $pfBak -Force
        } catch { Note "the backup could not be cleared and stays beside the restored file: $($_.Exception.Message)" }
        Note "owner settings restored (the file the restore replaced is kept as $kept, the backup as $keptBak)"
        Start-Process $dst
    }
}
