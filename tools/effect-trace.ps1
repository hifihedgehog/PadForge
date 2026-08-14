# DualSense effect-lane trace: ghost adaptive triggers after a game exits.
#
# Answers three questions the pad cannot tell you by feel:
#   1. Is the GAME still writing effects?           (enq)
#   2. Is a backlog building up?                    (depth, drop)
#   3. Are writes still going out after the game    (wr with a climbing
#      stopped?                                      sinceEnq)
#
# DS5EFFECT fields:
#   enq       packets the game produced this second. 0 = the game stopped.
#   drop      enqueues refused because the queue was full.
#   wr        packets actually written to the physical pad.
#   depth     packets still queued right now. A depth that climbs and stays
#             high IS the growing delay.
#   wmax      worst single write this second, in ms.
#   sinceEnq  ms since the last game packet.
#
# Verdicts:
#   depth climbing, drop > 0, wmax large    the pad cannot keep up. The lag
#                                           is transport backpressure.
#   enq=0, wr>0, sinceEnq climbing          the backlog is still draining
#                                           after the game exited.
#   enq=0, wr=0, and the pad STILL acts     nothing in this lane is writing.
#                                           Either the effect is latched in
#                                           the pad's firmware (expected: DS5
#                                           trigger state persists until
#                                           overwritten), or PadForge's own
#                                           30 Hz effect pass is the writer.
#   no lines at all                         the dispatcher never started, so
#                                           this slot is not passthrough at all.
#
# Usage: right-click, Run with PowerShell.

param(
    [string]$Exe = 'C:\PadForge\PadForge.exe',
    [string]$Log = "C:\tmp\padforge-effects-$(Get-Date -Format yyyyMMdd-HHmmss).log"
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

Write-Host "Log: $Log"
$cmd = "`$env:PADFORGE_DIAG = '$Log'; Start-Process '$Exe'"
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd

Write-Host ""
Write-Host "Setup that matters (the reporters all had these):"
Write-Host "  * Physical DualSense connected."
Write-Host "  * Slot output = PlayStation, profile 'DualSense (PS5) - Full'."
Write-Host "  * Windows default audio output SET TO THE CONTROLLER."
Write-Host "    This is the one that is not a default. Without it the audio"
Write-Host "    lane is idle and cannot contend with the effect lane."
Write-Host ""
Write-Host "Then:"
Write-Host "  1. Watch a few idle lines first (the baseline)."
Write-Host "  2. Launch a game that swaps trigger effects constantly."
Write-Host "  3. Play for several minutes, watching depth and wmax."
Write-Host "  4. CLOSE the game, and keep watching for 30 seconds."
Write-Host "  5. Note whether the pad still clicks, and what enq/wr say."
Write-Host ""

$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $Log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $Log)) {
    Write-Warning "No log file appeared. PadForge may not have started, or the elevation prompt was declined."
    return
}
Get-Content $Log -Wait -Tail 0 | Where-Object { $_ -match 'DS5EFFECT|BTAUDIO' }
