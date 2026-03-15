# Diagnostic: use SelectionItemPattern or mouse click for MappingsTab
$outFile = "c:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\mappings_diag2_log.txt"
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes

    $ae  = [System.Windows.Automation.AutomationElement]
    $ct  = [System.Windows.Automation.ControlType]
    $tree = [System.Windows.Automation.TreeScope]
    $true_cond = [System.Windows.Automation.Condition]::TrueCondition

    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MouseD {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(100);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@

    $out = @()

    $root = $ae::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "PadForge")
    $mainWin = $root.FindFirst($tree::Children, $cond)

    # Navigate to Pad1
    $listCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::ListItem)
    $navItems = $mainWin.FindAll($tree::Descendants, $listCond)
    foreach ($nav in $navItems) {
        if ($nav.Current.Name -eq "Pad1") {
            $selPat = $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $selPat.Select()
            Start-Sleep -Seconds 1
            break
        }
    }
    $out += "Navigated to Pad1"

    # Find MappingsTab and click it with mouse
    $mtCond = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "MappingsTab")
    $mt = $mainWin.FindFirst($tree::Descendants, $mtCond)

    if ($mt) {
        $r = $mt.Current.BoundingRectangle
        $cx = [int]($r.X + $r.Width / 2)
        $cy = [int]($r.Y + $r.Height / 2)
        $out += "MappingsTab at ($cx, $cy) size ($([int]$r.Width)x$([int]$r.Height))"

        # First bring PadForge to foreground
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinFg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
        $hwnd = (Get-Process PadForge).MainWindowHandle
        [WinFg]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300

        [MouseD]::Click($cx, $cy)
        $out += "Mouse clicked MappingsTab"
        Start-Sleep -Milliseconds 800

        # Now check what's visible
        $allElems = $mainWin.FindAll($tree::Descendants, $true_cond)
        $out += "Total descendants: $($allElems.Count)"

        # Look for Clear All, Copy, etc (Mappings toolbar buttons)
        $out += ""
        $out += "Elements with mapping-related names:"
        foreach ($e in $allElems) {
            $ename = $e.Current.Name
            $etype = $e.Current.ControlType.ProgrammaticName
            if ($ename -match "Clear|Copy|Paste|Map|mapping|DataGrid|Table|Source|Target|Axis|Button") {
                $out += "  $etype Name='$ename' AutomationId='$($e.Current.AutomationId)'"
            }
        }

        $out += ""
        $out += "DataGrid: $($mainWin.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataGrid))).Count)"
        $out += "DataItem: $($mainWin.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::DataItem))).Count)"
        $out += "Table: $($mainWin.FindAll($tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Table))).Count)"
    } else {
        $out += "MappingsTab NOT found"
    }

    $out | Out-File $outFile -Encoding utf8

} catch {
    "EXCEPTION: $($_.Exception.Message)`n$($_.ScriptStackTrace)" | Out-File $outFile -Encoding utf8
}
