# Test: Search RootElement for popup buttons (WPF Popup is a separate top-level window)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

# P/Invoke for mouse click
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseHelper {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}
"@

$root = $ae::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $root.FindFirst($tree::Children, $cond)

if (-not $mainWin) {
    Write-Output "ERROR: PadForge window not found"
    exit 1
}
Write-Output "Found PadForge window"

# Find Add Controller nav item
$addCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "AddController")
$addNav = $mainWin.FindFirst($tree::Descendants, $addCond)

if (-not $addNav) {
    # Fallback: search by name
    $addCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")
    $addNav = $mainWin.FindFirst($tree::Descendants, $addCond)
}

if (-not $addNav) {
    Write-Output "ERROR: Add Controller not found"
    exit 1
}

$rect = $addNav.Current.BoundingRectangle
$cx = [int]($rect.X + $rect.Width / 2)
$cy = [int]($rect.Y + $rect.Height / 2)
Write-Output "Clicking Add Controller at ($cx, $cy)"
[MouseHelper]::Click($cx, $cy)
Start-Sleep -Milliseconds 500

# NOW search RootElement for the popup buttons (popup is a separate top-level window)
Write-Output "`nSearching RootElement for AddVJoyBtn..."
$vjoyBtnCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "AddVJoyBtn")
$vjoyBtn = $root.FindFirst($tree::Descendants, $vjoyBtnCond)

if ($vjoyBtn) {
    Write-Output "FOUND AddVJoyBtn in RootElement!"
    $r = $vjoyBtn.Current.BoundingRectangle
    Write-Output "  BoundingRect: X=$($r.X) Y=$($r.Y) W=$($r.Width) H=$($r.Height)"
} else {
    Write-Output "AddVJoyBtn NOT found in RootElement either"

    # Enumerate all top-level windows to find popup
    Write-Output "`nAll top-level windows:"
    $allWins = $root.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $allWins) {
        $name = $w.Current.Name
        $cls = $w.Current.ClassName
        $aid = $w.Current.AutomationId
        Write-Output "  Name='$name' Class='$cls' AutomationId='$aid'"

        # Check if this looks like a popup window (empty name, Popup class)
        if ($cls -eq "Popup" -or ($name -eq "" -and $cls -ne "Shell_TrayWnd" -and $cls -ne "Progman")) {
            Write-Output "    -> Searching this window for buttons..."
            $btns = $w.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Button)))
            foreach ($b in $btns) {
                Write-Output "      Button: Name='$($b.Current.Name)' AutomationId='$($b.Current.AutomationId)'"
            }
        }
    }
}

# Close popup by clicking elsewhere
[MouseHelper]::Click(100, 300)
