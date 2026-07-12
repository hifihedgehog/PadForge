# Runtime self-diagnostics harvest (code-audit lens 1o). Elevated.
# Deploys the freshly published build, launches it with PADFORGE_DIAG
# armed (the SdlDiagLog ring mirrors to a file), walks the pages via
# UIA so lazily realized templates evaluate their bindings, then
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
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, 'PadForge')
$win = $null
for ($i = 0; $i -lt 10 -and -not $win; $i++) {
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep 2 }
}
if (-not $win) { Note 'WINDOW NOT FOUND' }
else {
    Note 'window found'
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
}
'@
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [W]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE first (a minimized window defeats clicks)
    Start-Sleep 1
    [W]::ShowWindow($hwnd, 3) | Out-Null  # SW_MAXIMIZE for stable layout
    [W]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep 1

    function Click-El($el) {
        if (-not $el) { return $false }
        try {
            $p = $el.GetClickablePoint()
            [W]::SetCursorPos([int]$p.X, [int]$p.Y) | Out-Null
            [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)  # LDOWN
            [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)  # LUP
            return $true
        } catch { return $false }
    }
    function Find-ByName($name) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, $name)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    }
    $allDesc = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    Note "descendants=$($allDesc.Count)"

    function Find-ByAid($aid) {
        $c = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $aid)
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    }

    foreach ($page in @('Profiles','Devices','Settings','About','Dashboard')) {
        $el = Find-ByName $page
        $ok = Click-El $el
        Note "nav ${page}: found=$($null -ne $el) clicked=$ok"
        Start-Sleep 2
    }

    # Slot 1 pad page via the Dashboard slot card grid
    $slots = Find-ByAid 'SlotsItemsControl'
    if ($slots) {
        $kids = $slots.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($kids.Count -gt 0) { Note "slot card click: $(Click-El $kids[0])" } else { Note 'no slot cards' }
    } else { Note 'SlotsItemsControl not found' }
    Start-Sleep 3

    # Mappings tab if reachable
    $mt = Find-ByAid 'MappingsTab'
    Note "mappings tab: $(Click-El $mt)"
    Start-Sleep 2

    foreach ($page in @('Devices','Dashboard')) {
        Click-El (Find-ByName $page) | Out-Null
        Start-Sleep 1
    }
}

# Relaunch clean (no mirror)
taskkill /F /IM PadForge.exe 2>$null | Out-Null
Start-Sleep 3
Remove-Item Env:PADFORGE_DIAG -ErrorAction SilentlyContinue
Start-Process -FilePath $dst
Note "sweep done $(Get-Date -Format o)"
