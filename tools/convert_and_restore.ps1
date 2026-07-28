Add-Type -AssemblyName System.Drawing
$png = [System.Drawing.Image]::FromFile('C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\screenshots\macros.png')
$jpgEncoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$encoderParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encoderParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 90L)
$png.Save('C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\docs\images\macros.jpg', $jpgEncoder, $encoderParams)
$png.Dispose()
Write-Host 'JPG saved'

# This restored 'PadForge.xml.bak', which nothing in this repo has ever
# written. Every backup writer here (prep_xml_for_capture.ps1,
# capture_v3_1_0_full.ps1, add_slots_via_ui.ps1) writes '.bak-capture'. The
# copy therefore failed on a missing source every time and the next line
# announced success regardless, so the synthetic capture config stayed live as
# the user's real settings with nothing on screen to say so.
$XmlPath = 'C:\PadForge\PadForge.xml'
$candidates = @("$XmlPath.bak-capture", "$XmlPath.bak")
$bak = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

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
