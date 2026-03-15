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

    # List contents to verify
    Write-Host "=== $tag ==="
    $zip = [System.IO.Compression.ZipFile]::OpenRead($newZip)
    $zip.Entries | ForEach-Object { Write-Host "  $($_.FullName)" }
    $zip.Dispose()

    # Upload
    gh release delete-asset $tag $zipName -R hifihedgehog/PadForge --yes 2>$null
    gh release upload $tag $newZip -R hifihedgehog/PadForge --clobber
    Write-Host "  Uploaded!" -ForegroundColor Green
}
