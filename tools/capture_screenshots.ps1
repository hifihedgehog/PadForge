## PadForge Screenshot Capture
## Captures window screenshots using PrintWindow API + UI Automation navigation.
## Must be run elevated (admin) for UIPI access to PadForge.

$logFile = Join-Path $PSScriptRoot "capture_log.txt"
Start-Transcript -Path $logFile -Force | Out-Null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$sdAsm = [System.Drawing.Bitmap].Assembly.Location

Add-Type -ReferencedAssemblies $sdAsm -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Imaging;

public class WinCapture {
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static Bitmap CaptureWindow(IntPtr hWnd) {
        RECT rect;
        GetWindowRect(hWnd, out rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr hdc = g.GetHdc();
            PrintWindow(hWnd, hdc, 2);
            g.ReleaseHdc(hdc);
        }

        // Check for blank capture
        bool allBlack = true;
        for (int px = 0; px < Math.Min(20, w); px += 5) {
            var c = bmp.GetPixel(px, h / 2);
            if (c.R != 0 || c.G != 0 || c.B != 0) { allBlack = false; break; }
        }
        if (allBlack) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr screenDC = GetDC(IntPtr.Zero);
                IntPtr memDC = g.GetHdc();
                BitBlt(memDC, 0, 0, w, h, screenDC, rect.Left, rect.Top, 0x00CC0020);
                g.ReleaseHdc(memDC);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }
        return bmp;
    }

    public static void SaveJpeg(Bitmap bmp, string path, int quality) {
        ImageCodecInfo encoder = null;
        foreach (var codec in ImageCodecInfo.GetImageDecoders()) {
            if (codec.FormatID == ImageFormat.Jpeg.Guid) { encoder = codec; break; }
        }
        if (encoder == null) { bmp.Save(path); return; }
        var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bmp.Save(path, encoder, ep);
    }
}
"@

$outDir = Join-Path $PSScriptRoot "..\screenshots"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -or $proc.MainWindowHandle -eq 0) {
    Write-Host "PadForge is not running."
    Stop-Transcript | Out-Null
    exit 1
}

$hwnd = $proc.MainWindowHandle
Write-Host "Found PadForge window: $hwnd (PID $($proc.Id))"

[WinCapture]::ShowWindow($hwnd, 9) | Out-Null
Start-Sleep -Milliseconds 500
[WinCapture]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Capture-Shot($name) {
    Start-Sleep -Milliseconds 1200
    [WinCapture]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 300
    $bmp = [WinCapture]::CaptureWindow($hwnd)
    if ($bmp) {
        $path = Join-Path $outDir "$name.jpg"
        [WinCapture]::SaveJpeg($bmp, $path, 92)
        $bmp.Dispose()
        $sz = [int]((Get-Item $path).Length / 1024)
        Write-Host "  OK: $name.jpg ($sz KB)"
    } else {
        Write-Host "  FAILED: $name"
    }
}

function Select-NavItem($name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { Write-Host "  Nav not found: $name"; return $false }
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $p.Select()
        Start-Sleep -Milliseconds 400
        return $true
    } catch {}
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $p.Invoke()
        Start-Sleep -Milliseconds 400
        return $true
    } catch {}
    Write-Host "  No pattern for: $name"
    return $false
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseClick {
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    public static void Click() {
        mouse_event(0x02, 0, 0, 0, 0); // MOUSEEVENTF_LEFTDOWN
        mouse_event(0x04, 0, 0, 0, 0); // MOUSEEVENTF_LEFTUP
    }
}
"@

function Click-RadioButton($textContent) {
    # PadPage tabs are RadioButtons with Click handler (not just selection).
    # Must simulate a real mouse click to fire the WPF Click event.
    $radioCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $textContent)
    $andCond = New-Object System.Windows.Automation.AndCondition($radioCond, $nameCond)

    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $andCond)
    if (-not $el) {
        # Also try general name search
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $textContent)
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    if (-not $el) { Write-Host "  Tab not found: $textContent"; return $false }

    # Use clickable point to simulate real mouse click (fires WPF Click handler)
    try {
        $pt = $el.GetClickablePoint()
        [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$pt.X, [int]$pt.Y)
        Start-Sleep -Milliseconds 100
        [MouseClick]::Click()
        Write-Host "    (Clicked: $textContent at $([int]$pt.X),$([int]$pt.Y))"
        Start-Sleep -Milliseconds 500
        return $true
    } catch {
        Write-Host "    No clickable point: $textContent ($_)"
    }

    # Fallback: try InvokePattern
    try {
        $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $p.Invoke()
        Write-Host "    (Invoke: $textContent)"
        Start-Sleep -Milliseconds 500
        return $true
    } catch {}

    Write-Host "  Could not click: $textContent"
    return $false
}

function Find-ControllerNavItem() {
    $hostCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "MenuItemsHost")
    $menuHost = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $hostCond)
    if (-not $menuHost) { return $null }

    $listItemCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $menuHost.FindAll([System.Windows.Automation.TreeScope]::Children, $listItemCond)

    foreach ($item in $items) {
        $name = $item.Current.Name
        if ($name -like "*Border*" -or ($name -ne "Dashboard" -and $name -ne "Profiles" -and
            $name -ne "Devices" -and $name -ne "Add Controller" -and $name -ne "")) {
            return $item
        }
    }
    return $null
}

Write-Host "`n=== Capturing Screenshots ==="

# 1. Dashboard
Write-Host "Dashboard..."
Select-NavItem "Dashboard" | Out-Null
Capture-Shot "dashboard"

# 2-8. Controller slot tabs
Write-Host "Finding controller slot..."
$ctrlItem = Find-ControllerNavItem
if ($ctrlItem) {
    Write-Host "  Found: '$($ctrlItem.Current.Name)'"
    try {
        $p = $ctrlItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $p.Select()
        Start-Sleep -Milliseconds 800
        Write-Host "  Selected controller slot"
    } catch {
        Write-Host "  Could not select: $_"
    }

    # Tab 0 (Controller/3D model) is the default view
    Write-Host "  Controller (3D model)..."
    # The first RadioButton has the slot badges as content, not a simple text name.
    # Just capture the default view which shows the 3D model.
    Capture-Shot "controller"

    # Tab 2: Mappings
    Write-Host "  Mappings tab..."
    Click-RadioButton "Mappings" | Out-Null
    Capture-Shot "mappings"

    # Tab 3: Sticks (was Left Stick + Right Stick in old UI)
    Write-Host "  Sticks tab..."
    Click-RadioButton "Sticks" | Out-Null
    Capture-Shot "sticks"

    # Tab 4: Triggers
    Write-Host "  Triggers tab..."
    Click-RadioButton "Triggers" | Out-Null
    Capture-Shot "triggers"

    # Tab 5: Force Feedback
    Write-Host "  Force Feedback tab..."
    Click-RadioButton "Force Feedback" | Out-Null
    Capture-Shot "force-feedback"

    # Tab 1: Macros
    Write-Host "  Macros tab..."
    Click-RadioButton "Macros" | Out-Null
    Capture-Shot "macros"

    # Return to Controller tab for clean state
    # Tab 0 RadioButton has complex content; click the first radio button
    $radioCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $radios = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $radioCond)
    if ($radios.Count -gt 0) {
        try { $radios[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
    }
} else {
    Write-Host "  No controller slot found."
}

# 9. Devices
Write-Host "Devices..."
Select-NavItem "Devices" | Out-Null
Capture-Shot "devices"

# 10. Profiles
Write-Host "Profiles..."
Select-NavItem "Profiles" | Out-Null
Capture-Shot "profiles"

# 11. Settings (top)
Write-Host "Settings..."
Select-NavItem "Settings" | Out-Null
Capture-Shot "settings"

# 12. Settings - scroll to bottom
Write-Host "Settings (drivers section)..."
Start-Sleep -Milliseconds 300
$scrollCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, "ScrollViewer")
$scrollViewers = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollCond)
foreach ($sv in $scrollViewers) {
    try {
        $sp = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp.Current.VerticallyScrollable) {
            $sp.SetScrollPercent(-1, 100)
            Write-Host "  Scrolled to bottom"
            break
        }
    } catch {}
}
Start-Sleep -Milliseconds 500
Capture-Shot "settings-drivers"

# 13. About
Write-Host "About..."
Select-NavItem "About" | Out-Null
Capture-Shot "about"

Write-Host "`nDone! Screenshots in: $outDir"
Stop-Transcript | Out-Null
