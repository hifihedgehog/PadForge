Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(100);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@
[W32]::SetProcessDPIAware()

$ch = [W32]::GetConsoleWindow()
if ($ch -ne [IntPtr]::Zero) { [W32]::ShowWindow($ch, 6) }

$log = "C:\Users\sonic\GitHub\PadForge\tools\capture_macros_log.txt"
"Starting..." | Out-File $log -Encoding ascii

try {
    $proc = Get-Process PadForge -ErrorAction Stop
    $hwnd = $proc.MainWindowHandle

    [W32]::ShowWindow($hwnd, 9)
    Start-Sleep -Milliseconds 300
    [W32]::MoveWindow($hwnd, 50, 30, 1280, 800, $true)
    Start-Sleep -Milliseconds 500
    [W32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 500

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

    # Click Pad1 in sidebar
    $pad1 = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "Pad1")))
    if ($pad1) {
        $selPat = $pad1.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selPat.Select()
        $rect = $pad1.Current.BoundingRectangle
        [W32]::Click([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
        Start-Sleep -Milliseconds 1000
        "Selected+Clicked Pad1" | Out-File $log -Append -Encoding ascii
    }

    # Click Macros tab
    [W32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 300
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $macros = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, "Macros")))
    if ($macros) {
        $rect = $macros.Current.BoundingRectangle
        [W32]::Click([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
        Start-Sleep -Milliseconds 1000
        "Clicked Macros tab" | Out-File $log -Append -Encoding ascii
    }

    # Now click "Volume Control" in the macro list
    # From the screenshot we can see the list. Volume Control is the 2nd item.
    # The list area starts around X=320..680 (physical), items at Y~250(Quick Melee), Y~308(Volume Control), Y~367(Rapid Fire A)
    # Window is at (50,30), 1800x1200 physical (1280x800 logical at 150% DPI)
    # From screenshot: "Volume Control" text is roughly at row 2 of the list
    # List left edge ~320px from window left, item height ~60px physical
    # First item Y center ~ 250 from window top, second ~ 310

    $wr = New-Object W32+RECT
    [W32]::GetWindowRect($hwnd, [ref]$wr)

    # Click Volume Control - second list item
    # From the screenshot proportions: list starts at ~22% from left, items at ~26%, 33%, 40% from top
    $clickX = $wr.Left + [int](0.37 * ($wr.Right - $wr.Left))  # center of list area
    $clickY = $wr.Top + [int](0.35 * ($wr.Bottom - $wr.Top))   # second item
    [W32]::Click($clickX, $clickY)
    Start-Sleep -Milliseconds 500
    "Clicked Volume Control area at ($clickX, $clickY)" | Out-File $log -Append -Encoding ascii

    # Bring to front and capture
    [W32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 500

    [W32]::GetWindowRect($hwnd, [ref]$wr)
    $w = $wr.Right - $wr.Left
    $h = $wr.Bottom - $wr.Top

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($wr.Left, $wr.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()

    $pngPath = "C:\Users\sonic\GitHub\PadForge\screenshots\macros.png"
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "Screenshot saved" | Out-File $log -Append -Encoding ascii
    "DONE" | Out-File $log -Append -Encoding ascii

} catch {
    "ERROR: $_" | Out-File $log -Append -Encoding ascii
}
