param(
    [string]$WorkDir = "C:\Users\sonic\AppData\Local\Temp\fix-releases"
)

$releases = @(
    @{ Tag = "v2.0.0-RC4"; Dirty = $true },
    @{ Tag = "v2.0.0-RC3"; Dirty = $true },
    @{ Tag = "v2.0.0-RC2"; Dirty = $true },
    @{ Tag = "v2.0.0-RC1"; Dirty = $true },
    @{ Tag = "v2.0.0-beta6"; Dirty = $true },
    @{ Tag = "v2.0.0-beta4"; Dirty = $true }
)

foreach ($rel in $releases) {
    $tag = $rel.Tag
    $zipName = "PadForge-$tag-win-x64.zip"
    $dlDir = Join-Path $WorkDir $tag
    $extractDir = Join-Path $WorkDir "$tag-extract"
    $newZip = Join-Path $WorkDir $zipName

    Write-Host "`n=== $tag ===" -ForegroundColor Cyan

    # Download
    if (!(Test-Path $dlDir)) { New-Item -ItemType Directory -Path $dlDir | Out-Null }
    $dlPath = Join-Path $dlDir $zipName
    if (!(Test-Path $dlPath)) {
        Write-Host "  Downloading..."
        gh release download $tag -R hifihedgehog/PadForge -p "*.zip" -D $dlDir --clobber
    }

    # Extract
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    New-Item -ItemType Directory -Path $extractDir | Out-Null
    Expand-Archive -Path $dlPath -DestinationPath $extractDir -Force

    # Find PadForge.exe (may be in root or publish\ subfolder)
    $exe = Get-ChildItem -Path $extractDir -Recurse -Filter "PadForge.exe" | Select-Object -First 1
    if (!$exe) {
        Write-Host "  !! PadForge.exe not found, skipping" -ForegroundColor Red
        continue
    }

    # Create clean zip with only PadForge.exe at the root
    if (Test-Path $newZip) { Remove-Item $newZip -Force }
    $cleanDir = Join-Path $WorkDir "$tag-clean"
    if (Test-Path $cleanDir) { Remove-Item $cleanDir -Recurse -Force }
    New-Item -ItemType Directory -Path $cleanDir | Out-Null
    Copy-Item $exe.FullName (Join-Path $cleanDir "PadForge.exe")
    Compress-Archive -Path (Join-Path $cleanDir "*") -DestinationPath $newZip -Force

    $oldSize = [math]::Round((Get-Item $dlPath).Length / 1MB, 1)
    $newSize = [math]::Round((Get-Item $newZip).Length / 1MB, 1)
    Write-Host "  Old: ${oldSize}MB -> New: ${newSize}MB"

    # Delete old asset and upload new one
    Write-Host "  Deleting old asset..."
    gh release delete-asset $tag $zipName -R hifihedgehog/PadForge --yes 2>$null
    Write-Host "  Uploading clean zip..."
    gh release upload $tag $newZip -R hifihedgehog/PadForge --clobber
    Write-Host "  Done!" -ForegroundColor Green
}
