# Deploy PadForge.exe to C:\PadForge\
$src = 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\publish\PadForge.exe'
$dst = 'C:\PadForge\PadForge.exe'

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

Copy-Item $src $dst -Force
if ($?) {
    Write-Host "Deployed successfully"
    Start-Process $dst
    Write-Host "PadForge launched"
} else {
    Write-Host "FAILED to copy"
}
