Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ae = [System.Windows.Automation.AutomationElement]
$ct = [System.Windows.Automation.ControlType]
$tree = [System.Windows.Automation.TreeScope]
$out = "C:\Users\sonic\GitHub\PadForge\tools\popup_test2.txt"
$lines = @()

$cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
$win = $ae::RootElement.FindFirst($tree::Children, $cond)
if (-not $win) { "No window" | Out-File $out -Encoding utf8; exit }

# Find Add Controller nav item
$nameCond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "Add Controller")
$matches = $win.FindAll($tree::Descendants, $nameCond)
$navItem = $null
foreach ($m in $matches) {
    if ($m.Current.ControlType -eq $ct::ListItem) { $navItem = $m; break }
}

if ($navItem) {
    $lines += "Found Add Controller"
    $rect = $navItem.Current.BoundingRectangle
    $lines += "BoundingRect: X=$($rect.X) Y=$($rect.Y) W=$($rect.Width) H=$($rect.Height)"
    
    # Click center of the element with mouse
    $clickX = [int]($rect.X + $rect.Width / 2)
    $clickY = [int]($rect.Y + $rect.Height / 2)
    $lines += "Clicking at ($clickX, $clickY)"
    
    # Use Win32 API for mouse click
    Add-Type @"
    using System;
    using System.Runtime.InteropServices;
    public class MouseOps {
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
        public static void Click(int x, int y) {
            SetCursorPos(x, y);
            mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // MOUSEEVENTF_LEFTDOWN
            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // MOUSEEVENTF_LEFTUP
        }
    }
"@
    [MouseOps]::Click($clickX, $clickY)
    $lines += "Clicked"
    Start-Sleep -Seconds 2
    
    # Search for AddVJoyBtn
    $idCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "AddVJoyBtn")
    
    # Search main window
    $btn = $win.FindFirst($tree::Descendants, $idCond)
    if ($btn) {
        $lines += "FOUND AddVJoyBtn in main window!"
        $btnRect = $btn.Current.BoundingRectangle
        $lines += "  BtnRect: X=$($btnRect.X) Y=$($btnRect.Y) W=$($btnRect.Width) H=$($btnRect.Height)"
    } else {
        $lines += "AddVJoyBtn NOT found in main window"
    }
    
    # Search all top-level windows
    $allWins = $ae::RootElement.FindAll($tree::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $allWins) {
        $btn2 = $w.FindFirst($tree::Descendants, $idCond)
        if ($btn2) {
            $lines += "FOUND AddVJoyBtn in '$($w.Current.Name)'"
        }
    }
    
    # Also list new windows/popups that appeared
    $lines += "Windows after click: $($allWins.Count)"
    foreach ($w in $allWins) {
        $lines += "  '$($w.Current.Name)' Class=$($w.Current.ClassName)"
    }
    
    # Check for any Popup element type
    $allDescs = $win.FindAll($tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines += "Total descendants: $($allDescs.Count)"
    
    # Find buttons with AutomationId containing Add
    $btnsCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Button)
    $allBtns = $win.FindAll($tree::Descendants, $btnsCond)
    $lines += "All buttons ($($allBtns.Count)):"
    foreach ($b in $allBtns) {
        $id = $b.Current.AutomationId
        $n = $b.Current.Name
        if ($id -or $n) {
            $lines += "  Name='$n' AutomationId='$id'"
        }
    }
} else {
    $lines += "Add Controller not found"
}

$lines | Out-File $out -Encoding utf8 -Force
