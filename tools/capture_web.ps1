<#
.SYNOPSIS
    Recaptures the two Web Controller screenshots against a PROVEN-LIVE server.
.DESCRIPTION
    Split out of capture_all.ps1 because the web shots do not depend on the
    dummy-device topology the full run builds, and because they were the two
    that shipped broken on 2026-08-09: both were Edge's "localhost refused to
    connect" page, captured because the old code photographed whatever Edge
    rendered without ever asking whether the server had answered.

    This script refuses to capture until an HTTP 200 comes back, and refuses
    to keep a capture whose window title says otherwise. PadForge must already
    be running with the Web Controller enabled.
#>

param(
    [string]$OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [int]$Port = 8080
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[W32]::SetProcessDPIAware() | Out-Null

$edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$edgeTempProfile = Join-Path $env:TEMP "PadForge_EdgeCapture"

function Kill-CaptureEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
}

# The gate. No 200, no capture, no exceptions.
function Wait-Web {
    param([string]$Url, [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($r.StatusCode -eq 200) {
                Write-Host "  answering: $Url ($($r.RawContentLength) bytes)" -ForegroundColor Green
                return $true
            }
        } catch { Start-Sleep -Milliseconds 800 }
    }
    Write-Host "  !! never answered: $Url" -ForegroundColor Red
    return $false
}

function Cap-Web {
    param([string]$Url, [string]$Name, [int]$WaitMs = 6000)

    if (-not (Wait-Web $Url)) { Write-Host "  SKIP $Name (server down)" -ForegroundColor Yellow; return $false }

    Kill-CaptureEdge
    Start-Sleep -Milliseconds 1200

    Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --app=$Url"
    Start-Sleep -Milliseconds $WaitMs

    $ehwnd = [IntPtr]::Zero
    $title = ""
    $pids = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($procId in $pids) {
        $ep = Get-Process -Id $procId -EA SilentlyContinue
        if ($ep -and $ep.MainWindowHandle -ne [IntPtr]::Zero) {
            $ehwnd = $ep.MainWindowHandle; $title = $ep.MainWindowTitle
            Write-Host "  Edge window PID=$procId title='$title'"
            break
        }
    }
    if ($ehwnd -eq [IntPtr]::Zero) {
        Write-Host "  !! no temp-profile Edge window -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    # A title that still says the host name means Edge is on its own error page,
    # because the served pages set a real <title>. Refuse rather than ship it.
    if ($title -match '^\s*localhost' -or $title -match "can't be reached" -or $title -match 'refused') {
        Write-Host "  !! Edge is showing an error page (title='$title') -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    [W32]::SetWindowPos($ehwnd, [IntPtr]::Zero, 200, 200, 1280, 720, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 400
    [W32]::SetForegroundWindow($ehwnd) | Out-Null
    Start-Sleep -Milliseconds 700

    $r = New-Object W32+RECT
    [W32]::GetWindowRect($ehwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 100 -or $h -le 100) {
        Write-Host "  !! window too small ${w}x${h} -- SKIP $Name" -ForegroundColor Red
        Kill-CaptureEdge; return $false
    }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $out = Join-Path $OutputDir "$Name.png"
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  >> $Name.png ($([math]::Round((Get-Item $out).Length/1024))KB)" -ForegroundColor Green

    Kill-CaptureEdge
    Start-Sleep -Milliseconds 500
    return $true
}

Write-Host "Web Controller capture, port $Port" -ForegroundColor Cyan
$okA = Cap-Web "http://localhost:${Port}/" "web-landing" 6000
$okB = Cap-Web "http://localhost:${Port}/controller.html?layout=xbox360" "web-controller" 7000
Write-Host ""
Write-Host "landing=$okA controller=$okB" -ForegroundColor Cyan
if (-not ($okA -and $okB)) { exit 1 }
