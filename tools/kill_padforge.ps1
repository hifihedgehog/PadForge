param([switch]$Elevated)
if (-not $Elevated) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated" -Wait
        exit
    }
}
$p = Get-Process PadForge -ErrorAction SilentlyContinue
if ($p) { Stop-Process -Id $p.Id -Force; Write-Host "Killed PadForge (PID $($p.Id))" }
else { Write-Host "PadForge not running" }
