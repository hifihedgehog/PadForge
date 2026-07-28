Add-Type -AssemblyName System.Drawing
$png = [System.Drawing.Image]::FromFile('C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\screenshots\macros.png')
$jpgEncoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$encoderParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encoderParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 90L)
$png.Save('C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\docs\images\macros.jpg', $jpgEncoder, $encoderParams)
$png.Dispose()
Write-Host 'JPG saved'

# The restore is unconditional no longer. Two capture flows write two
# different backup names: capture_all.ps1 writes '<xml>.bak', while
# prep_xml_for_capture.ps1 and capture_v3_1_0_full.ps1 write '.bak-capture'.
# This restored '.bak' and then printed "XML backup restored" whether or not
# the copy succeeded, so after a prep-based capture it announced success while
# the synthetic config stayed live as the user's real settings.
#
# Pick the most RECENTLY written backup rather than a fixed preference. With
# both names present, a fixed order restores whichever flow is named first
# rather than whichever actually ran, which is how stale settings come back.
$XmlPath = 'C:\PadForge\PadForge.xml'
$candidates = @("$XmlPath.bak-capture", "$XmlPath.bak")
$bak = $candidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending |
    Select-Object -First 1

if (-not $bak) {
    Write-Host "XML NOT restored: no backup found. Looked for:"
    $candidates | ForEach-Object { Write-Host "  $_" }
    Write-Host "The capture config is still live. Restore settings by hand."
    exit 1
}

try {
    Copy-Item -LiteralPath $bak -Destination $XmlPath -Force -ErrorAction Stop
} catch {
    Write-Host "XML NOT restored: $($_.Exception.Message)"
    Write-Host "The capture config is still live at $XmlPath."
    exit 1
}
Write-Host "XML restored from $bak"
