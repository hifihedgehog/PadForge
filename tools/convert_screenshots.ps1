Add-Type -AssemblyName System.Drawing
$srcDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
$dstDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\screenshots"

$map = @{
    "dashboard"              = "dashboard"
    "pad-controller-3d"      = "controller"
    "pad-controller-2d"      = "controller-2d"
    "pad-mappings"           = "mappings"
    "pad-sticks"             = "sticks"
    "pad-triggers"           = "triggers"
    "pad-forcefeedback"      = "force-feedback"
    "pad-macros"             = "macros"
    "pad-kbm-preview"        = "kbm-preview"
    "pad-vjoy-configbar"     = "vjoy"
    "pad-vjoy-schematic"     = "vjoy-schematic"
    "pad-midi-configbar"     = "midi"
    "add-controller-popup"   = "add-controller-popup"
    "profiles"               = "profiles"
    "devices"                = "devices"
    "settings"               = "settings"
    "settings-hidhide"       = "settings-hidhide"
    "settings-drivers"       = "settings-drivers"
    "about"                  = "about"
    "web-landing"            = "web-landing"
    "web-controller"         = "web-controller"
    "pad-sticks-deadzone-dropdown"      = "sticks-deadzone-dropdown"
    "pad-sticks-sensitivity-dropdown"   = "sticks-sensitivity-dropdown"
    "pad-triggers-sensitivity-dropdown" = "triggers-sensitivity-dropdown"
}

$jpgEncoder = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
$encParams = New-Object System.Drawing.Imaging.EncoderParameters(1)
$encParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, 90L)

foreach ($kv in $map.GetEnumerator()) {
    $src = Join-Path $srcDir "$($kv.Key).png"
    $dst = Join-Path $dstDir "$($kv.Value).jpg"
    if (Test-Path $src) {
        $img = [System.Drawing.Image]::FromFile($src)
        $img.Save($dst, $jpgEncoder, $encParams)
        $img.Dispose()
        $kb = [math]::Round((Get-Item $dst).Length / 1024)
        Write-Host "  $($kv.Key).png -> $($kv.Value).jpg (${kb}KB)"
    } else {
        Write-Host "  MISSING: $src" -ForegroundColor Red
    }
}
Write-Host "Done converting."
