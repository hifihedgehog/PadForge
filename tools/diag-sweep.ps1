# Runtime self-diagnostics harvest (code-audit lens 1o). Elevated.
# Deploys the freshly published build, launches it with PADFORGE_DIAG
# armed (the SdlDiagLog ring mirrors to a file), walks the pages via
# UIA so lazily realized templates evaluate their bindings, opens the
# Workshop browse dialog and realizes its search results (the 1o
# coverage rule: page navigation alone never realizes dialogs), then
# relaunches clean with the mirror off. Acceptance bar: the harvest at
# C:\tmp\pfdiag-verify.log contains no error-class lines (BINDERR,
# FAILED, exception). Progress notes: C:\tmp\pf-sweep-out.txt
$ErrorActionPreference = 'Continue'
$out = 'C:\tmp\pf-sweep-out.txt'
Set-Content $out "sweep start $(Get-Date -Format o)" -Encoding utf8
function Note($m) { Add-Content $out $m -Encoding utf8 }

$src = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\PadForge.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\PadForge.exe'
$dst = 'C:\PadForge\PadForge.exe'
$diag = 'C:\tmp\pfdiag-verify.log'
Remove-Item $diag -Force -ErrorAction SilentlyContinue

# Deploy
$copied = $false
for ($i = 1; $i -le 5; $i++) {
    taskkill /F /IM PadForge.exe 2>$null | Out-Null
    Start-Sleep 3
    try { Copy-Item $src $dst -Force -ErrorAction Stop; $copied = $true; break }
    catch { Note "copy attempt ${i}: $($_.Exception.Message)" }
}
Note "copied=$copied hash=$((Get-FileHash $dst -Algorithm SHA256).Hash.Substring(0,12))"

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
$win = $null
for ($i = 0; $i -lt 10 -and -not $win; $i++) {
    $win = $root.FindFirst($TS::Children, $cond)
    if (-not $win) { Start-Sleep 2 }
}
if (-not $win) { Note 'WINDOW NOT FOUND' }
else {
    Note 'window found'
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
    # confirms the handle (SetForegroundWindow is allowed to refuse).
    function ForceFG([IntPtr]$h) {
        for ($i = 0; $i -lt 5; $i++) {
            [W]::ShowWindow($h, 9) | Out-Null   # SW_RESTORE (a minimized window defeats clicks)
            Start-Sleep -Milliseconds 250
            [W]::ShowWindow($h, 3) | Out-Null   # SW_MAXIMIZE for stable layout
            [W]::SetForegroundWindow($h) | Out-Null
            Start-Sleep -Milliseconds 450
            if ([W]::GetForegroundWindow() -eq $h) { return $true }
        }
        return $false
    }

    function Click-At([int]$x, [int]$y) {
        [W]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 120
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)  # LDOWN
        [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)  # LUP
    }
    # Bounding-rect center, NOT GetClickablePoint: the nav rail's DataItems
    # report clickable points that miss the pill, and Invoke/Select patterns
    # on them bypass the visual tree this harvest exists to realize.
    function Click-El($el, [double]$fx = 0.5, [double]$fy = 0.5) {
        if (-not $el) { return $false }
        try {
            $r = $el.Current.BoundingRectangle
            if ($r.Width -le 0) { return $false }
            Click-At ([int]($r.X + $r.Width * $fx)) ([int]($r.Y + $r.Height * $fy))
            return $true
        } catch { return $false }
    }
    function Find-ByName($name) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
        return $win.FindFirst($TS::Descendants, $c)
    }
    function Find-NavItem($name) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
        foreach ($el in $win.FindAll($TS::Descendants, $c)) {
            if ($el.Current.ControlType -eq $CT::DataItem) { return $el }
        }
        return $null
    }
    function Find-ByAid($aid) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $aid)
        return $win.FindFirst($TS::Descendants, $c)
    }

    Note "foreground: $(ForceFG $hwnd)"
    $allDesc = $win.FindAll($TS::Descendants, $TC)
    Note "descendants=$($allDesc.Count)"

    foreach ($page in @('Profiles','Devices','Settings','About','Dashboard')) {
        ForceFG $hwnd | Out-Null
        $el = Find-NavItem $page
        if (-not $el) { $el = Find-ByName $page }
        $ok = Click-El $el
        Note "nav ${page}: found=$($null -ne $el) clicked=$ok"
        Start-Sleep 2
    }

    # Slot 1 pad page via the Dashboard slot card grid
    $slots = Find-ByAid 'SlotsItemsControl'
    if ($slots) {
        $kids = $slots.FindAll($TS::Children, $TC)
        if ($kids.Count -gt 0) { Note "slot card click: $(Click-El $kids[0])" } else { Note 'no slot cards' }
    } else { Note 'SlotsItemsControl not found' }
    Start-Sleep 3

    # Mappings tab if reachable
    $mt = Find-ByAid 'MappingsTab'
    Note "mappings tab: $(Click-El $mt)"
    Start-Sleep 2

    # ---- Workshop browse dialog (1o coverage rule: dialogs and their item
    # templates never realize from page navigation alone) ----
    ForceFG $hwnd | Out-Null
    Click-El (Find-NavItem 'Profiles') | Out-Null
    Start-Sleep 2
    $browse = $null
    $bc = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, 'Browse Community Configs')
    foreach ($el in $win.FindAll($TS::Descendants, $bc)) {
        if ($el.Current.ControlType -eq $CT::Button) { $browse = $el; break }
    }
    if ($browse) {
        Note "browse click: $(Click-El $browse)"
        # The modal is UIA-shy from the root: discover its HWND via Win32
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
        if ($dlgHwnd -eq [IntPtr]::Zero) { Note 'workshop dialog HWND not found' }
        else {
            $sb = New-Object System.Text.StringBuilder 256
            [W]::GetWindowText($dlgHwnd, $sb, 256) | Out-Null
            Note "workshop dialog hwnd=$dlgHwnd title='$($sb.ToString())'"
            $dlg = [System.Windows.Automation.AutomationElement]::FromHandle($dlgHwnd)
            [W]::SetForegroundWindow($dlgHwnd) | Out-Null
            Start-Sleep 1
            $edit = $dlg.FindFirst($TS::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $CT::Edit)))
            if ($edit) {
                Click-El $edit | Out-Null
                Start-Sleep -Milliseconds 400
                try { ($edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('skyrim'); Note 'search set: skyrim' }
                catch { Note "search SetValue failed: $($_.Exception.Message)" }
                Start-Sleep 15  # debounce + store search + card templates realizing
                $li = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $CT::ListItem)
                $tiles = $dlg.FindAll($TS::Descendants, $li)
                Note "workshop tiles/cards=$($tiles.Count) first='$(if ($tiles.Count) { $tiles[0].Current.Name })'"
            } else { Note 'workshop search box not found' }
            [W]::SendMessage($dlgHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
            Start-Sleep 2
            Note "workshop dialog closed=$(-not [W]::IsWindowVisible($dlgHwnd))"
        }
    } else { Note 'browse button not found' }

    foreach ($page in @('Devices','Dashboard')) {
        ForceFG $hwnd | Out-Null
        Click-El (Find-NavItem $page) | Out-Null
        Start-Sleep 1
    }
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
Write-Host "diag-sweep acceptance: $verdict"

# Relaunch clean (no mirror)
taskkill /F /IM PadForge.exe 2>$null | Out-Null
Start-Sleep 3
Remove-Item Env:PADFORGE_DIAG -ErrorAction SilentlyContinue
Start-Process -FilePath $dst
Note "sweep done $(Get-Date -Format o)"
