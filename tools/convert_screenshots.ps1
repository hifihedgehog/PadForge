Add-Type -AssemblyName System.Drawing
$srcDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"
$dstDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\screenshots"

$map = @{
    'profiles-starter-gallery' = 'starter-profiles'
    "dashboard"              = "dashboard"
    "pad-controller-3d"      = "controller"
    "pad-controller-2d"      = "controller-2d"
    "pad-mappings"           = "mappings"
    "pad-sticks"             = "sticks"
    "pad-triggers"           = "triggers"
    "pad-forcefeedback"      = "force-feedback"
    "pad-macros"             = "macros"
    "pad-kbm-preview"        = "kbm-preview"
    "pad-midi-configbar"     = "midi"
    "pad-extended-schematic" = "extended"
    "pad-playstation-configbar" = "playstation"
    "pad-nintendo-configbar" = "nintendo"
    "pad-adaptive-triggers"  = "adaptive-triggers"
    "pad-lighting"           = "lighting"
    "pad-gyro"               = "gyro"
    "pad-audio"              = "audio"
    "pad-touchpad"           = "touchpad"
    "pad-impulse-triggers"   = "impulse-triggers"
    "pad-trigger-routing"    = "trigger-routing"
    "pad-wheel"              = "wheel"
    "midi-input"             = "midi-input"
    "remote-link"            = "remote-link"
    "wii-pair"               = "wii-pair"
    "pad-pointer"            = "pointer"
    "devices-nfc"            = "devices-nfc"
    "nfc-register"           = "nfc-register"
    "devices-consumer"       = "devices-consumer"
    "devices-power"          = "devices-power"
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
    # v4 additions
    "pad-lighting-guide-led" = "guide-led"
    "pad-kbm-socd"           = "kbm-socd"
    "pad-mouse-gestures"     = "mouse-gestures"
    "pad-ds3-gyro"           = "ds3-gyro"
    "devices-ds3"            = "devices-ds3"
    "ds3-pair"               = "ds3-pair"
    "wii-balance-sources"    = "wii-balance-sources"
    "joycon-ir-source"       = "joycon-ir-source"
    "joycon2-mouse-sources"  = "joycon2-mouse-sources"
    "macro-disconnect"       = "macro-disconnect"
    "pad-extended-configbar" = "extended-configbar"
    # wiki completeness additions (2026-07-12): one entry per new placeholder.
    # jpg names drop the pad- prefix, like the rest of the map.
    "pad-config-tabs"             = "config-tabs"
    "pad-mapping-annotations"     = "mapping-annotations"
    "3d-model-annotation-overlay" = "3d-model-annotation-overlay"
    "2d-annotation-overlay"       = "2d-annotation-overlay"
    "2d-touchpad-finger-dots"     = "2d-touchpad-finger-dots"
    "pad-stick-trim"              = "stick-trim"
    "pad-trigger-instrument"      = "trigger-instrument"
    "pad-sticks-boundary-calibration" = "sticks-boundary-calibration"
    "macro-add-from-list"         = "macro-add-from-list"
    "pad-audio-haptic-controls"   = "audio-haptic-controls"
    "pad-motor-activity"          = "motor-activity"
    "touchpad-gesture-recorder"   = "touchpad-gesture-recorder"
    "wii-pointer-mode"            = "wii-pointer-mode"
    "nfc-live-preview"            = "nfc-live-preview"
    "midi-input-mode-devices-page" = "midi-input-mode-devices-page"
    "pad-extended-clone-device"   = "extended-clone-device"
    "devices-facet-chips"         = "devices-facet-chips"
    "devices-dossier"             = "devices-dossier"
    "dashboard-slot-card"         = "dashboard-slot-card"
    "dashboard-polling-readout"   = "dashboard-polling-readout"
    "dsu-port-box"                = "dsu-port-box"
    "driver-status-flames"        = "driver-status-flames"
    "settings-driver-cards"       = "settings-driver-cards"
    "profiles-foreground-readout" = "profiles-foreground-readout"
    # v4.1.0 additions (#9 Workshop import + macro/mapping editors)
    "workshop-cold"               = "workshop-cold"
    "workshop-search"             = "workshop-search"
    "workshop-configs"            = "workshop-configs"
    "workshop-manifest"           = "workshop-manifest"
    "workshop-applied"            = "workshop-applied"
    "settings-community-configs"  = "settings-community-configs"
    "macro-move-mouse"            = "macro-move-mouse"
    "macro-repeat-key"            = "macro-repeat-key"
    "mapping-sensitivity"         = "mapping-sensitivity"
    "gamepad-source-picker"       = "gamepad-source-picker"
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
