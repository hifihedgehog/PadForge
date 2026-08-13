# DualSense Bluetooth audio trace (discussion #300).
#
# Launches the deployed PadForge with the diagnostic mirror armed, so the
# BTAUDIO heartbeat lands in a file. One line per second per pad while the
# stream thread lives. Its ABSENCE is a finding too: it means the thread
# stopped, not that the audio went quiet.
#
# Reading the result:
#   capW == 0          Windows stopped handing us the render stream (another
#                      app took the endpoint exclusively, or the default
#                      endpoint moved). Nothing to send.
#   capW > 0, peak 0   we are handed silence: the game is not rendering here.
#   peak > 0, sent 0   we have audio and are not sending it: the idle gate,
#                      the write pool, or the transport.
#   sent > 0           it left this machine, so what remains is pad-side.
#
# Usage:  right-click, Run with PowerShell.  Stop with Ctrl+C, or just close
# PadForge; the log stays on disk.

param(
    [string]$Exe = 'C:\PadForge\PadForge.exe',
    [string]$Log = "C:\tmp\padforge-btaudio-$(Get-Date -Format yyyyMMdd-HHmmss).log"
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
Write-Host "Launching PadForge with the trace armed (elevated)..."

# PadForge always runs elevated. Start-Process -Verb RunAs does not carry the
# parent environment, so the variable is set inside the elevated shell that
# launches the exe.
$cmd = "`$env:PADFORGE_DIAG = '$Log'; Start-Process '$Exe'"
Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd

Write-Host ""
Write-Host "PadForge is starting. Now:"
Write-Host "  1. Connect the DualSense over BLUETOOTH."
Write-Host "  2. Set Windows audio output to the controller."
Write-Host "  3. Play something (any audio) and confirm you hear it from the pad."
Write-Host "  4. Launch the game, reach the point where the sound stops."
Write-Host "  5. Close the game."
Write-Host ""
Write-Host "Then hand over the log. Waiting for heartbeat lines..."
Write-Host ""

# Tail the heartbeat so the transition is visible live.
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $Log) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $Log)) {
    Write-Warning "No log file appeared. PadForge may not have started, or the elevation prompt was declined."
    return
}
Get-Content $Log -Wait -Tail 0 | Where-Object { $_ -match 'BTAUDIO|SINK |PERSONA ' }
