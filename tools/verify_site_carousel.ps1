<#
.SYNOPSIS
    Proves the padforge.org finish carousel actually advances, in a real browser.
.DESCRIPTION
    Headless cannot answer this. A headless page reports document.hidden true,
    and the carousel deliberately pauses when hidden, so a headless render shows
    a static stage whether the code works or not. This opens a visible window,
    screenshots the stage twice several seconds apart, and reports whether the
    pixels changed.
#>

param(
    [string]$Url = "file:///C:/Users/sonic/OneDrive/Documents/GitHub/padforge.org/index.html#finish",
    [int]$GapSeconds = 11
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class VW {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[VW]::SetProcessDPIAware() | Out-Null

$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$profileDir = Join-Path $env:TEMP "PadForge_CarouselCheck"

Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
    Where-Object { $_.CommandLine -like "*PadForge_CarouselCheck*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
Start-Sleep -Milliseconds 800

Start-Process $edge "--user-data-dir=`"$profileDir`" --no-first-run --disable-sync --no-default-browser-check --new-window --app=$Url"
Start-Sleep -Seconds 9

$hwnd = [IntPtr]::Zero
foreach ($procId in @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_CarouselCheck*" } |
        Select-Object -ExpandProperty ProcessId)) {
    $ep = Get-Process -Id $procId -EA SilentlyContinue
    if ($ep -and $ep.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $ep.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no browser window" -ForegroundColor Red; exit 1 }

[VW]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 60, 1500, 1000, 0x0040) | Out-Null
Start-Sleep -Milliseconds 500
[VW]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Seconds 3

function Shot([string]$path) {
    $r = New-Object VW+RECT
    [VW]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

Shot "C:\tmp\carousel_t0.png"
Write-Host "captured t0"
Start-Sleep -Seconds $GapSeconds
Shot "C:\tmp\carousel_t1.png"
Write-Host "captured t1 (+${GapSeconds}s)"

Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
    Where-Object { $_.CommandLine -like "*PadForge_CarouselCheck*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }

$a = (Get-FileHash "C:\tmp\carousel_t0.png").Hash
$b = (Get-FileHash "C:\tmp\carousel_t1.png").Hash
if ($a -eq $b) { Write-Host "IDENTICAL: the stage did not advance" -ForegroundColor Red }
else { Write-Host "DIFFERENT: the stage advanced" -ForegroundColor Green }
