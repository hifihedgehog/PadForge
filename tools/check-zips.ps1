Add-Type -AssemblyName System.IO.Compression.FileSystem
$workDir = "C:\Users\sonic\AppData\Local\Temp\check-zips"
if (!(Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir | Out-Null }

foreach ($tag in @("v2.0.0-RC4", "v2.0.0-RC3", "v2.0.0-RC2")) {
    $dir = Join-Path $workDir $tag
    if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $zipPath = Join-Path $dir "PadForge-$tag-win-x64.zip"
    if (!(Test-Path $zipPath)) {
        gh release download $tag -R hifihedgehog/PadForge -p "*.zip" -D $dir --clobber
    }
    Write-Host "=== $tag ==="
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    $zip.Entries | ForEach-Object { Write-Host "  $($_.FullName)" }
    $zip.Dispose()
}
