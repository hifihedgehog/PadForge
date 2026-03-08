<#
.SYNOPSIS
    Captures pad-controller-3d, pad-controller-2d, and web screenshots.
    Assumes PadForge is running with a slot. Must run elevated.
    Strategy: Navigate Dashboard -> Slot to reset to 3D default view.
#>

param([string]$OutputDir = "C:\Users\sonic\GitHub\PadForge.wiki\images")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int cx,int cy,uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool f);
    [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint n,INPUT[] inp,int sz);
    public static readonly IntPtr TOPMOST=new IntPtr(-1), NOTOPMOST=new IntPtr(-2);
    [StructLayout(LayoutKind.Sequential)] public struct RECT{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT{public int dx,dy;public uint mouseData,dwFlags,time;public IntPtr dwExtraInfo;}
    [StructLayout(LayoutKind.Explicit,Size=40)] public struct INPUT{[FieldOffset(0)]public uint type;[FieldOffset(8)]public MOUSEINPUT mi;}
    public static void ClickAt(int px,int py){
        int sw=GetSystemMetrics(0),sh=GetSystemMetrics(1);
        int nx=(int)(((long)px*65535)/(sw-1)),ny=(int)(((long)py*65535)/(sh-1));
        INPUT[] i=new INPUT[3];
        i[0].type=0;i[0].mi.dx=nx;i[0].mi.dy=ny;i[0].mi.dwFlags=0x8001;
        i[1].type=0;i[1].mi.dx=nx;i[1].mi.dy=ny;i[1].mi.dwFlags=0x8002;
        i[2].type=0;i[2].mi.dx=nx;i[2].mi.dy=ny;i[2].mi.dwFlags=0x8004;
        SendInput(3,i,Marshal.SizeOf(typeof(INPUT)));
    }
    public static void MoveTo(int px,int py){
        int sw=GetSystemMetrics(0),sh=GetSystemMetrics(1);
        INPUT[] i=new INPUT[1];
        i[0].type=0;i[0].mi.dx=(int)(((long)px*65535)/(sw-1));i[0].mi.dy=(int)(((long)py*65535)/(sh-1));i[0].mi.dwFlags=0x8001;
        SendInput(1,i,Marshal.SizeOf(typeof(INPUT)));
    }
    public static void ForceFG(IntPtr hwnd){
        IntPtr fg=GetForegroundWindow();uint fgTid,myTid=GetCurrentThreadId();
        GetWindowThreadProcessId(fg,out fgTid);
        if(fgTid!=myTid)AttachThreadInput(myTid,fgTid,true);
        ShowWindow(hwnd,9);SetForegroundWindow(hwnd);
        if(fgTid!=myTid)AttachThreadInput(myTid,fgTid,false);
    }
}
"@
[W32]::SetProcessDPIAware() | Out-Null

$TC = [System.Windows.Automation.TreeScope]::Children
$TD = [System.Windows.Automation.TreeScope]::Descendants
$out = @()
function Log($msg) { $script:out += $msg; Write-Host $msg }

function Find1 {
    param([System.Windows.Automation.AutomationElement]$P=$script:uiaWin,[string]$N,[string]$A,[System.Windows.Automation.ControlType]$CT)
    $cs=@()
    if($N){$cs+=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$N)}
    if($A){$cs+=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$A)}
    if($CT){$cs+=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,$CT)}
    if($cs.Count -eq 0){return $null}
    $c=if($cs.Count -eq 1){$cs[0]}else{New-Object System.Windows.Automation.AndCondition($cs)}
    return $P.FindFirst($TD,$c)
}

function ClickEl($el,[int]$d=800,[string]$lbl) {
    if(-not $el){Log "  !! NOT FOUND: $lbl";return $false}
    $r=$el.Current.BoundingRectangle
    if($r.IsEmpty -or $r.Width -le 0){Log "  !! EMPTY: $lbl";return $false}
    $cx=[int]($r.X+$r.Width/2);$cy=[int]($r.Y+$r.Height/2)
    Log("  Click '$lbl' at ($cx,$cy) [$([int]$r.Width)x$([int]$r.Height)]")
    [W32]::ForceFG($script:hwnd);Start-Sleep -Milliseconds 100
    [W32]::ClickAt($cx,$cy);Start-Sleep -Milliseconds $d
    return $true
}

function SelectEl($el,[int]$d=800,[string]$lbl) {
    if(-not $el){Log "  !! NOT FOUND: $lbl";return $false}
    try{$pat=$el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern);Log "  Select '$lbl'";$pat.Select();Start-Sleep -Milliseconds $d;return $true}catch{}
    return (ClickEl $el -d $d -lbl $lbl)
}

function Cap($name) {
    [W32]::ForceFG($script:hwnd);Start-Sleep -Milliseconds 300
    $r=New-Object W32+RECT;[W32]::GetWindowRect($script:hwnd,[ref]$r)|Out-Null
    [W32]::MoveTo(($r.Right-100),($r.Bottom-15));Start-Sleep -Milliseconds 200
    $w=$r.Right-$r.Left;$h=$r.Bottom-$r.Top
    $bmp=New-Object System.Drawing.Bitmap($w,$h)
    $g=[System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left,$r.Top,0,0,[System.Drawing.Size]::new($w,$h))
    $g.Dispose()
    $p=Join-Path $OutputDir "$name.png";$bmp.Save($p,[System.Drawing.Imaging.ImageFormat]::Png);$bmp.Dispose()
    $kb=[math]::Round((Get-Item $p).Length/1024)
    Log "  >> $name.png (${kb}KB)"
}

# --- Setup ---
$proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
if (-not $proc) { Log "PadForge not running!"; exit 1 }
$hwnd = $proc.MainWindowHandle
Log "PID=$($proc.Id) HWND=$hwnd"

[W32]::ForceFG($hwnd)
[W32]::ShowWindow($hwnd, 3) | Out-Null
Start-Sleep -Milliseconds 1000
[W32]::SetWindowPos($hwnd, [W32]::TOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 500

$uiaRoot = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
if (-not $uiaWin) { Log "UIA fail"; exit 1 }

# Warm-up
$wr = New-Object W32+RECT; [W32]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
[W32]::ClickAt([int](($wr.Left + $wr.Right) / 2), ($wr.Top + 30))
Start-Sleep -Milliseconds 500

# === Step 1: Navigate to Dashboard first (reset tab state) ===
Log "=== Navigate to Dashboard ==="
$dashEl = Find1 -N "Dashboard" -CT ([System.Windows.Automation.ControlType]::ListItem)
if ($dashEl) { SelectEl $dashEl -d 1000 -lbl "Dashboard" }

# === Step 2: Delete existing slot and create fresh one ===
Log "=== Delete and recreate slot ==="
$skip = @("Dashboard","Profiles","Devices","Add Controller","About","Settings","","PadForge","Toggle navigation","Back","Close Navigation")
$menuHost = Find1 -A "MenuItemsHost"
$si = if ($menuHost) { $menuHost } else { $uiaWin }
$liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
$allItems = @($si.FindAll($TD, $liCond))
$slot = $null
foreach ($item in $allItems) {
    $n = $item.Current.Name; $cls = $item.Current.ClassName
    if ($cls -eq "NavigationViewItem" -and $n -notin $skip) {
        Log "  Existing slot: '$n'"; $slot = $item; break
    }
}

# Delete existing slot if present (via sidebar X button)
if ($slot) {
    # The close/delete button is a child of the NavigationViewItem
    $closeBtnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $closeBtn = $slot.FindFirst($TD, $closeBtnCond)
    if ($closeBtn) {
        Log "  Deleting slot via close button..."
        ClickEl $closeBtn -d 1500 -lbl "Close Slot"
    } else {
        Log "  No close button found on slot"
    }
    Start-Sleep -Milliseconds 1000
}

# Create a new Xbox 360 slot via Add Controller
Log "  Creating new slot..."
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
$addNav = Find1 -N "Add Controller" -CT ([System.Windows.Automation.ControlType]::ListItem)
if ($addNav) {
    ClickEl $addNav -d 1000 -lbl "Add Controller Nav"
    Start-Sleep -Milliseconds 500
    $uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
    $xbox = Find1 -A "AddXbox360Btn"
    if (-not $xbox) { $xbox = Find1 -N "Xbox 360" -CT ([System.Windows.Automation.ControlType]::Button) }
    if ($xbox) {
        ClickEl $xbox -d 3000 -lbl "Xbox 360 Button"
    } else {
        Log "  !! Xbox 360 button not found"
    }
} else {
    Log "  !! Add Controller not found"
}

# Find the newly created slot
Start-Sleep -Milliseconds 1000
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
$menuHost = Find1 -A "MenuItemsHost"
$si = if ($menuHost) { $menuHost } else { $uiaWin }
$allItems = @($si.FindAll($TD, $liCond))
$slot = $null
foreach ($item in $allItems) {
    $n = $item.Current.Name; $cls = $item.Current.ClassName
    if ($cls -eq "NavigationViewItem" -and $n -notin $skip) {
        Log "  New slot: '$n'"; $slot = $item; break
    }
}

if (-not $slot) { Log "No slot found after creation!"; exit 1 }

# The slot is already selected (Add Controller navigates to it)
# Give it time to fully render the 3D view
Start-Sleep -Milliseconds 2000

# Refresh UIA and wait for page to fully load
Start-Sleep -Milliseconds 1000
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)

# === Step 3: Click the first tab (Controller/3D view) explicitly ===
Log "=== 3D Controller ==="
$padPage = Find1 -A "PadPageView"
if ($padPage) {
    $ppRect = $padPage.Current.BoundingRectangle
    Log "  PadPageView bounds: X=$([int]$ppRect.X) Y=$([int]$ppRect.Y) W=$([int]$ppRect.Width) H=$([int]$ppRect.Height)"

    # Find all RadioButton tabs
    $rbCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $tabs = @($padPage.FindAll($TD, $rbCond))
    Log "  Tabs: $($tabs.Count)"
    foreach ($t in $tabs) {
        $tr = $t.Current.BoundingRectangle
        Log "    '$($t.Current.Name)' at ($([int]$tr.X),$([int]$tr.Y)) $([int]$tr.Width)x$([int]$tr.Height)"
    }

    # Find the first tab (unnamed, Tag=0 = controller visualization)
    # It should be the one with an empty name. Click it physically.
    $firstTab = $null
    foreach ($t in $tabs) {
        if ($t.Current.Name -eq "") { $firstTab = $t; break }
    }
    if (-not $firstTab -and $tabs.Count -gt 0) { $firstTab = $tabs[0] }

    if ($firstTab) {
        $tr = $firstTab.Current.BoundingRectangle
        $tabCenterX = [int]($tr.X + $tr.Width / 2)
        $tabCenterY = [int]($tr.Y + $tr.Height / 2)
        Log "  First tab center: ($tabCenterX,$tabCenterY)"

        # Try InvokePattern first (works better than physical click for WPF RadioButtons)
        $invoked = $false
        try {
            $invPat = $firstTab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            Log "  InvokePattern available -- invoking"
            $invPat.Invoke()
            $invoked = $true
            Start-Sleep -Milliseconds 2000
        } catch {
            Log "  InvokePattern not available: $($_.Exception.Message)"
        }

        if (-not $invoked) {
            # Try TogglePattern
            try {
                $togPat = $firstTab.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                Log "  TogglePattern available -- toggling"
                $togPat.Toggle()
                Start-Sleep -Milliseconds 2000
            } catch {
                Log "  TogglePattern not available: $($_.Exception.Message)"
            }
        }

        # Also do a physical click as backup (try twice with delay)
        [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 100
        [W32]::ClickAt($tabCenterX, $tabCenterY)
        Start-Sleep -Milliseconds 500
        [W32]::ClickAt($tabCenterX, $tabCenterY)
        Start-Sleep -Milliseconds 2000

        # Check if we're now on the controller tab by looking for 3D/viewport element
        $uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
        $viewport = Find1 -A "ControllerModelView"
        if (-not $viewport) { $viewport = Find1 -A "HelixViewport3D" }
        if (-not $viewport) { $viewport = Find1 -A "ControllerModel2DView" }
        Log "  Viewport found: $(if($viewport){'YES'}else{'NO'})"
    }
}
# Wait for 3D to render
Start-Sleep -Milliseconds 1000
Cap "pad-controller-3d"

# === Step 4: 2D toggle ===
Log "=== 2D Controller ==="
$uiaWin = $uiaRoot.FindFirst($TC, $pidCond)
$padPage = Find1 -A "PadPageView"
if ($padPage) {
    $ppRect = $padPage.Current.BoundingRectangle
    # 2D/3D toggle button is inside the 3D viewport at top-left
    # Tab strip is ~36px physical height, then content area. Button at Margin="16,16" from viewport.
    $toggleX = [int]($ppRect.X + 52)
    $toggleY = [int]($ppRect.Y + 124)
    Log "  2D toggle at ($toggleX,$toggleY)"
    [W32]::ForceFG($hwnd); Start-Sleep -Milliseconds 100
    [W32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 1500
    Cap "pad-controller-2d"
    # Switch back to 3D
    Start-Sleep -Milliseconds 200
    [W32]::ClickAt($toggleX, $toggleY)
    Start-Sleep -Milliseconds 500
} else {
    Log "  PadPageView not found for 2D toggle"
}

# === Step 5: Web controller screenshots ===
Log "=== Web Controller ==="
$edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }

# Landing page
$landingOut = Join-Path $OutputDir "web-landing.png"
Log "  web-landing..."
Start-Process -FilePath $edgePath -ArgumentList "--headless --disable-gpu --screenshot=`"$landingOut`" --window-size=1280,720 http://localhost:8080/" -Wait -NoNewWindow
Start-Sleep -Seconds 2
if (Test-Path $landingOut) { Log "  >> web-landing.png" }

# Controller page — give it time to load assets
$ctrlOut = Join-Path $OutputDir "web-controller.png"
Log "  web-controller..."
# Use a longer delay by loading page first, then screenshot
Start-Process -FilePath $edgePath -ArgumentList "--headless --disable-gpu --virtual-time-budget=5000 --screenshot=`"$ctrlOut`" --window-size=1280,720 http://localhost:8080/controller.html?layout=xbox360" -Wait -NoNewWindow
Start-Sleep -Seconds 2
if (Test-Path $ctrlOut) { Log "  >> web-controller.png" }

# Cleanup
[W32]::SetWindowPos($hwnd, [W32]::NOTOPMOST, 0, 0, 0, 0, 0x0003) | Out-Null
Log "=== DONE ==="
$out | Out-File -Encoding ascii C:\PadForge\capture_3d_web_log.txt
