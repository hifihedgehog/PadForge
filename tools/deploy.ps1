# Deploy PadForge.exe to C:\PadForge\
#
# The source path used to point at a repo-root `publish\` folder that the build
# has not written to for a long time, so every run of this script killed a
# working PadForge, failed the copy, and returned without relaunching it. The
# path now comes off the script's own location and matches where the publish
# step actually puts the single-file exe.
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'PadForge.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\PadForge.exe'
$dst = 'C:\PadForge\PadForge.exe'

# Check the source BEFORE stopping anything. Killing the running app and only
# then discovering there is nothing to deploy leaves the machine with no
# PadForge at all, which is worse than not having run the script.
if (-not (Test-Path -LiteralPath $src)) {
    Write-Host "FAILED: no published exe at $src"
    Write-Host "Run the publish step first. PadForge was left running."
    exit 1
}

# Kill any running PadForge
$procs = Get-Process -Name PadForge -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-Process -Name PadForge -ErrorAction SilentlyContinue)) { break }
    }
}

# Extra wait for file handle release
Start-Sleep -Seconds 2

try {
    Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
} catch {
    # The app is already stopped here, so say so rather than leaving the
    # operator to guess whether it is still up.
    Write-Host "FAILED to copy: $($_.Exception.Message)"
    Write-Host "PadForge is stopped. Relaunch $dst by hand or re-run after publishing."
    exit 1
}

Write-Host "Deployed successfully"
Start-Process $dst
Write-Host "PadForge launched"
