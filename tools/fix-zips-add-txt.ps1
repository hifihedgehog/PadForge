Add-Type -AssemblyName System.IO.Compression.FileSystem
$workDir = "C:\Users\sonic\AppData\Local\Temp\fix-zips2"
$txtSource = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\PadForge.App\gamecontrollerdb_padforge.txt"

foreach ($tag in @("v2.0.0-RC4", "v2.0.0-RC3", "v2.0.0-RC2")) {
    $zipName = "PadForge-$tag-win-x64.zip"
    $dir = Join-Path $workDir $tag
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

    # Download current zip
    $dlPath = Join-Path $dir $zipName
    gh release download $tag -R hifihedgehog/PadForge -p "*.zip" -D $dir --clobber

    # Extract
    $extractDir = Join-Path $dir "extract"
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }
    Expand-Archive -Path $dlPath -DestinationPath $extractDir -Force

    # Add the txt file
    Copy-Item $txtSource $extractDir

    # Re-zip
    $newZip = Join-Path $workDir $zipName
    if (Test-Path $newZip) { Remove-Item $newZip -Force }
    Compress-Archive -Path (Join-Path $extractDir "*") -DestinationPath $newZip -Force

    # List contents AND gate on them. This printed the entries and then deleted
    # the published asset regardless of what it saw, with the delete's errors
    # sent to $null, so a bad re-zip left a release with no download and no way
    # back from inside this script. Same defect as fix-release-zips.ps1.
    Write-Host "=== $tag ==="
    $zip = [System.IO.Compression.ZipFile]::OpenRead($newZip)
    $names = @($zip.Entries | ForEach-Object { $_.FullName })
    $names | ForEach-Object { Write-Host "  $_" }
    $hasExe = @($zip.Entries | Where-Object { $_.FullName -eq 'PadForge.exe' -and $_.Length -gt 0 }).Count -eq 1
    $hasTxt = @($zip.Entries | Where-Object { $_.FullName -eq 'gamecontrollerdb_padforge.txt' -and $_.Length -gt 0 }).Count -eq 1
    $zip.Dispose()

    if (-not ($hasExe -and $hasTxt)) {
        Write-Host "  !! Rebuilt zip is missing PadForge.exe or gamecontrollerdb_padforge.txt -- leaving the release asset alone" -ForegroundColor Red
        continue
    }

    # --clobber replaces the asset in place, so no delete is needed. The delete
    # only opened a window where the release had nothing attached.
    gh release upload $tag $newZip -R hifihedgehog/PadForge --clobber
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  !! Upload FAILED for $tag. Original asset left in place." -ForegroundColor Red
        continue
    }
    Write-Host "  Uploaded!" -ForegroundColor Green
}
