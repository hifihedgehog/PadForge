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

    # Verify the replacement BEFORE destroying the published asset. This
    # deleted first and uploaded second, so an empty or corrupt Compress-Archive
    # result, or an upload that failed, left a published release with no
    # download at all. Nothing here could put it back: the original was already
    # gone from the release and only survived in $WorkDir.
    $verifyOk = $false
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $probe = [System.IO.Compression.ZipFile]::OpenRead($newZip)
        $verifyOk = @($probe.Entries | Where-Object { $_.FullName -eq 'PadForge.exe' -and $_.Length -gt 0 }).Count -eq 1
        $probe.Dispose()
    } catch { $verifyOk = $false }
    if (-not $verifyOk) {
        Write-Host "  !! New zip does not contain a single non-empty PadForge.exe -- leaving the release asset alone" -ForegroundColor Red
        continue
    }

    # --clobber replaces an existing asset of the same name, so the delete is
    # not needed and only opens a window where the release has no download.
    Write-Host "  Uploading clean zip (replaces in place)..."
    gh release upload $tag $newZip -R hifihedgehog/PadForge --clobber
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  !! Upload FAILED for $tag. Original asset left in place." -ForegroundColor Red
        continue
    }
    Write-Host "  Done!" -ForegroundColor Green
}
