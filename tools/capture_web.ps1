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
    to keep a capture whose window title says otherwise.

    It no longer REQUIRES the server to be on already. The owner ships with
    EnableWebController=false, so "PadForge must already be running with the
    Web Controller enabled" meant every unattended run captured nothing. An
    assignment is data: this backs up PadForge.xml, writes the flag, restarts
    the app, captures, and restores the owner's file in a finally.

    The shot list covers the #296 rebuild rather than one legacy layout:
    the landing grid, the DualSense (lightbar, player LEDs and the analog
    trigger sliders), a Steam Deck (trackpads), a Switch 2 Pro, and the
    custom controller builder.
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
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

[W32]::SetProcessDPIAware() | Out-Null

# #296 phase 0 binds a SELF-SIGNED cert and serves https:// whenever the
# binding succeeds, which it does whenever PadForge is elevated. Probing
# http:// therefore found nothing and every shot was skipped as "server
# down" while the server was up and answering on the secure lane. Trust
# the capture-local cert for probes, and tell Edge to do the same so it
# renders the controller instead of an interstitial.
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edgePath)) { $edgePath = "C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
$edgeTempProfile = Join-Path $env:TEMP "PadForge_EdgeCapture"

function Kill-CaptureEdge {
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like "*PadForge_EdgeCapture*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }
}

# The gate. No 200, no capture, no exceptions.
#
# curl.exe, not Invoke-WebRequest. PowerShell 5.1's client cannot complete
# the handshake against the #296 self-signed cert: schannel renegotiates
# and Invoke-WebRequest returns "the underlying connection was closed" for
# BOTH schemes. That reads exactly like a dead server, and it is why six
# shots were skipped as "server down" while curl -k got HTTP 200 and
# 11330 bytes from the same URL a second later. curl.exe ships in
# System32 on every supported Windows.
function Probe-Web {
    param([string]$Url)
    $code = & curl.exe -sk -o NUL -w '%{http_code}' --max-time 6 $Url 2>$null
    return ($code -eq '200')
}

function Wait-Web {
    param([string]$Url, [int]$TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Probe-Web $Url) {
            Write-Host "  answering: $Url" -ForegroundColor Green
            return $true
        }
        Start-Sleep -Milliseconds 800
    }
    Write-Host "  !! never answered: $Url" -ForegroundColor Red
    return $false
}

function Cap-Web {
    param([string]$Url, [string]$Name, [int]$WaitMs = 6000, [int]$Width = 1280, [int]$Height = 720)

    if (-not (Wait-Web $Url)) { Write-Host "  SKIP $Name (server down)" -ForegroundColor Yellow; return $false }

    Kill-CaptureEdge
    Start-Sleep -Milliseconds 1200

    Start-Process $edgePath "--user-data-dir=`"$edgeTempProfile`" --no-first-run --disable-sync --disable-session-crashed-bubble --disable-features=msEdgeSyncService,msEdgeAccountSSO --no-default-browser-check --ignore-certificate-errors --allow-insecure-localhost --test-type --app=$Url"
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

    [W32]::SetWindowPos($ehwnd, [IntPtr]::Zero, 120, 60, $Width, $Height, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 400
    [W32]::SetForegroundWindow($ehwnd) | Out-Null
    Start-Sleep -Milliseconds 700

    # GetWindowRect returns the rect INCLUDING the invisible resize border,
    # so a capture taken from it carries ~11px of whatever sits behind the
    # window down its left edge and ~8px down its right. On a dark page that
    # reads as a second window bleeding out of the first. DWM's extended
    # frame bounds is the visible frame, which is what a screenshot means.
    $r = New-Object W32+RECT
    $DWMWA_EXTENDED_FRAME_BOUNDS = 9
    $sz = [System.Runtime.InteropServices.Marshal]::SizeOf([type][W32+RECT])
    if ([W32]::DwmGetWindowAttribute($ehwnd, $DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$r, $sz) -ne 0) {
        [W32]::GetWindowRect($ehwnd, [ref]$r) | Out-Null
    }
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

$exe   = 'C:\PadForge\PadForge.exe'
$pfXml = 'C:\PadForge\PadForge.xml'
$pfBak = Join-Path $env:TEMP 'PadForge.xml.webcapture.bak'

# A leftover backup means an earlier run died before restoring, so that copy
# is the owner's real state. Put it back rather than overwrite it.
if (Test-Path $pfBak) {
    Copy-Item $pfBak $pfXml -Force
    Write-Host "  restored a leftover settings backup from an aborted run" -ForegroundColor Yellow
}
if (Test-Path $pfXml) { Copy-Item $pfXml $pfBak -Force }

$results = @{}
try {
    # Turn the server on in the file, not through the UI.
    #
    # VERIFY the kill before writing. A surviving instance re-saves
    # PadForge.xml from its own in-memory state on exit and silently puts
    # EnableWebController back to false, so the write lands, the app
    # starts, and the server never binds. That is exactly how this ran
    # three times reporting "server down" while the setting on disk read
    # true. taskkill also returns success having killed nothing when the
    # caller is not elevated and PadForge is, so count the processes
    # rather than trusting the exit code.
    for ($k = 1; $k -le 6; $k++) {
        taskkill /F /IM PadForge.exe 2>$null | Out-Null
        Start-Sleep 3
        $live = @(Get-Process PadForge -ErrorAction SilentlyContinue).Count
        if ($live -eq 0) { break }
        Write-Host "  kill attempt ${k}: $live instance(s) still up" -ForegroundColor Yellow
    }
    if (@(Get-Process PadForge -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'PadForge instances survived the kill; refusing to write settings that would be clobbered.'
    }

    $xml = Get-Content $pfXml -Raw
    $xml = $xml -replace '<EnableWebController>false</EnableWebController>', '<EnableWebController>true</EnableWebController>'
    Set-Content -Path $pfXml -Value $xml -Encoding UTF8
    Write-Host "  EnableWebController=true written; restarting PadForge" -ForegroundColor Cyan
    Start-Process -FilePath $exe
    Start-Sleep 18

    # Discover the scheme rather than assuming it: the secure lane is used
    # when the cert binds, and plain http is the documented fallback.
    $scheme = $null
    foreach ($try in 'https', 'http') {
        if (Probe-Web "${try}://localhost:${Port}/") { $scheme = $try; break }
    }
    if (-not $scheme) { $scheme = 'https' }
    Write-Host "  server scheme: $scheme" -ForegroundColor Cyan

    $shots = @(
        @{ Url = "${scheme}://localhost:${Port}/";                                 Name = 'web-landing';    Wait = 6000; W = 1900; H = 1300 },
        @{ Url = "${scheme}://localhost:${Port}/controller.html?layout=xbox360";   Name = 'web-controller'; Wait = 9500 },
        @{ Url = "${scheme}://localhost:${Port}/controller.html?layout=dualsense"; Name = 'web-dualsense';  Wait = 9500 },
        @{ Url = "${scheme}://localhost:${Port}/controller.html?layout=steamdeck"; Name = 'web-steamdeck';  Wait = 9500 },
        @{ Url = "${scheme}://localhost:${Port}/controller.html?layout=switch2pro";Name = 'web-switch2pro'; Wait = 9500 },
        @{ Url = "${scheme}://localhost:${Port}/custom.html";                      Name = 'web-custom';     Wait = 7000 }
    )
    foreach ($s in $shots) {
        $h = if ($s.ContainsKey('H')) { $s.H } else { 720 }
        $w = if ($s.ContainsKey('W')) { $s.W } else { 1280 }
        $results[$s.Name] = Cap-Web $s.Url $s.Name $s.Wait $w $h
    }
}
finally {
    # Always put the owner's settings back, including after a mid-run error.
    Kill-CaptureEdge
    if (Test-Path $pfBak) {
        taskkill /F /IM PadForge.exe 2>$null | Out-Null
        Start-Sleep 3
        Copy-Item $pfBak $pfXml -Force
        Remove-Item $pfBak -Force
        Write-Host "  owner settings restored" -ForegroundColor Cyan
        Start-Process -FilePath $exe
    }
}

Write-Host ""
$failed = @($results.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { $_.Key })
foreach ($k in $results.Keys | Sort-Object) { Write-Host ("  {0,-16} {1}" -f $k, $(if ($results[$k]) { 'ok' } else { 'FAILED' })) }
if ($failed.Count) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "all web shots captured" -ForegroundColor Green
