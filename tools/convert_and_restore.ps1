Add-Type -AssemblyName System.Drawing
$png = [System.Drawing.Image]::FromFile('C:\Users\sonic\GitHub\PadForge\screenshots\macros.png')
$jpgEncoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$encoderParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encoderParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 90L)
$png.Save('C:\Users\sonic\GitHub\PadForge\docs\images\macros.jpg', $jpgEncoder, $encoderParams)
$png.Dispose()
Write-Host 'JPG saved'

Copy-Item 'C:\PadForge\PadForge.xml.bak' 'C:\PadForge\PadForge.xml' -Force
Write-Host 'XML backup restored'
