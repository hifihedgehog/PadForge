# Button-surface trace: why does a mapped button do nothing?
#
# Launches the deployed PadForge with the diagnostic mirror armed and tails
# the button lines. One BTNSURFACE line per pad at open, then one BTNEDGE
# line per press and release.
#
# Reading BTNEDGE, which carries both bits for the same press:
#   gp=1 raw=1   the press arrived and decoded. Anything still wrong is
#                downstream of the device (mapping, layer, consume, output).
#   gp=0 raw=1   the pad sent it and SDL's gamepad MAPPING is dropping it.
#   gp=0 raw=0   nothing arrived: the pad is not sending it on this
#                transport, or its report is being misparsed.
#   no line      that position never changed at all.
#
# BTNSURFACE lists the positions the pad is allowed to report. A position
# missing from supported[] can never produce an edge, whatever the picker
# offers.
#
# PadForge canonical positions: 0 A/Cross, 1 B/Circle, 2 X/Square,
# 3 Y/Triangle, 4 LB, 5 RB, 6 Back/Create, 7 Start/Options, 8 LS, 9 RS,
# 10 Guide/PS, 11 Misc1 (DualSense MUTE), 12-15 paddles, 16 touchpad click.
#
# Usage: right-click, Run with PowerShell. Ctrl+C stops the tail and copies
# the trace to -Log.

param(
    [string]$Exe = 'C:\PadForge\PadForge.exe',
    [string]$Log = "C:\tmp\padforge-buttons-$(Get-Date -Format yyyyMMdd-HHmmss).log"
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "PadForge not found at $Exe" }
$dir = Split-Path $Log
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

Write-Host "Stopping any running PadForge..."
$n = 0
while ((Get-Process PadForge -ErrorAction SilentlyContinue) -and $n -lt 20) {
    taskkill /F /IM PadForge.exe 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    $n++
}

# PadForge with rights takes PADFORGE_DIAG only when it names diag.log
# directly in its own folder (SdlDiagLog.MirrorPathWithRights), so the mirror is
# diag.log beside the exe, and -Log is its copy, made when the tail stops.
# The elevated shell deletes the last run's mirror, which only an
# administrator may do in PadForge's folder, and this script waits for it.
$mirror = Join-Path (Split-Path -Parent $Exe) 'diag.log'
Write-Host "Log: $Log (copied from $mirror when the trace stops)"
$cmd = "Remove-Item -LiteralPath '$mirror' -Force -ErrorAction SilentlyContinue; `$env:PADFORGE_DIAG = '$mirror'; Start-Process '$Exe'"
$elevated = Start-Process powershell -Verb RunAs -PassThru -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd
$elevated.WaitForExit()

Write-Host ""
Write-Host "PadForge is starting. With the DualSense connected, press in this order:"
Write-Host "  1. A / Cross      (the positive control: this one works today)"
Write-Host "  2. X / Square"
Write-Host "  3. The mute button"
Write-Host "  4. A / Cross again"
Write-Host ""
Write-Host "Step 4 matters: if it still logs an edge, the trace was alive the"
Write-Host "whole time, so the silence on X and mute is real and not a dead log."
Write-Host ""

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $mirror) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $mirror)) {
    Write-Warning "No log file appeared. PadForge may not have started, or the elevation prompt was declined."
    return
}
try { Get-Content $mirror -Wait -Tail 0 | Where-Object { $_ -match 'BTNSURFACE|BTNEDGE' } }
finally {
    Copy-Item -LiteralPath $mirror -Destination $Log -Force
    Write-Host "The trace is copied to $Log"
}
