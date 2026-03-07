# Test: Click "Add Controller" nav item, then search RootElement for popup buttons
$outFile = "c:\Users\sonic\GitHub\PadForge\tools\popup_test6.txt"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Mouse2 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // down
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // up
    }
}
"@

$out = @()

$root = $ae::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$mainWin = $root.FindFirst($tree::Children, $cond)
if (-not $mainWin) { "ERROR: PadForge not found" | Out-File $outFile; exit 1 }
$out += "Found PadForge"

# Find "Add Controller" ListItem in sidebar
$andCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")),
    (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem))
)
$addNav = $mainWin.FindFirst($tree::Descendants, $andCond)
if (-not $addNav) { "ERROR: Add Controller ListItem not found" | Out-File $outFile; exit 1 }

$rect = $addNav.Current.BoundingRectangle
$cx = [int]($rect.X + $rect.Width / 2)
$cy = [int]($rect.Y + $rect.Height / 2)
$out += "Clicking Add Controller at ($cx, $cy)"
[Mouse2]::Click($cx, $cy)
Start-Sleep -Milliseconds 800

# Search RootElement for AddVJoyBtn
$vjoyBtnCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "AddVJoyBtn")
$vjoyBtn = $root.FindFirst($tree::Descendants, $vjoyBtnCond)

if ($vjoyBtn) {
    $out += "FOUND AddVJoyBtn via RootElement!"
    $r = $vjoyBtn.Current.BoundingRectangle
    $out += "  Rect: X=$($r.X) Y=$($r.Y) W=$($r.Width) H=$($r.Height)"
} else {
    $out += "AddVJoyBtn NOT found in RootElement"

    # Enumerate all top-level windows
    $out += ""
    $out += "All top-level windows:"
    $allWins = $root.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $allWins) {
        $name = $w.Current.Name
        $cls = $w.Current.ClassName
        $out += "  Name='$name' Class='$cls'"

        # Search unnamed/Popup windows for buttons
        if ($cls -eq "Popup" -or ($name -eq "" -and $cls -ne "Shell_TrayWnd" -and $cls -ne "Progman" -and $cls -ne "Shell_SecondaryTrayWnd")) {
            $btns = $w.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $out += "    Descendants: $($btns.Count)"
            foreach ($b in $btns) {
                $out += "      Type=$($b.Current.ControlType.ProgrammaticName) Name='$($b.Current.Name)' AutomationId='$($b.Current.AutomationId)'"
            }
        }
    }
}

# Close popup
[Mouse2]::Click(100, 300)

$out | Out-File $outFile -Encoding utf8
