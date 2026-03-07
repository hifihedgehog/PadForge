Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "Not running"; exit 1 }
Write-Host "PID: $($proc.Id)"
Write-Host "MainWindowHandle: $($proc.MainWindowHandle)"
Write-Host "MainWindowTitle: $($proc.MainWindowTitle)"

Add-Type -Name Win32 -Namespace Interop -MemberDefinition '
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'
[Interop.Win32]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
[Interop.Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 2

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
Write-Host "Root: $($root.Current.Name)"
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)
Write-Host "Direct children: $($children.Count)"
foreach ($c in $children) {
    Write-Host "  [$($c.Current.ControlType.ProgrammaticName)] name='$($c.Current.Name)' class='$($c.Current.ClassName)'"
}
