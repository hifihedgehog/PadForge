# Self-elevate
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Wait
    exit
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process PadForge -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host 'PadForge not running'; exit 1 }

Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);' -Name 'W32' -Namespace 'I' -ErrorAction SilentlyContinue
[I.W32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$out = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\ui_tree2.txt"

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
function Dump($el, $depth) {
    if ($depth -gt 3) { return }
    $child = $walker.GetFirstChild($el)
    $count = 0
    while ($child -and $count -lt 80) {
        $count++
        $ct = $child.Current.ControlType.ProgrammaticName
        $name = $child.Current.Name
        $aid = $child.Current.AutomationId
        $cls = $child.Current.ClassName
        $line = ('  ' * $depth) + "[$ct] name='$name' aid='$aid' cls='$cls'"
        Add-Content $out $line
        Dump $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

Set-Content $out "UI Tree for PadForge (elevated):"
Add-Content $out "Root: $($root.Current.Name)"
Dump $root 0
Add-Content $out "--- END ---"
