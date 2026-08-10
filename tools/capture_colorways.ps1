# Themed-colorway + VR capture, by WRITING STATE rather than driving UI.
#
# Why this exists separately from capture_all.ps1: the appearance picker and
# the Devices-page assignment toggles are the two least reliable UI paths in
# the app for automation (the full harness stalls in the toggle-enumeration
# chain, which memory has recorded dying across four separate runs). Every
# input this script needs is a persisted field:
#
#   PadSetting.Model3DAppearances  "DualSense=SpiderMan2,XboxSeries=Starfield"
#   AppSettings.Use2DControllerView  true|false
#   AppSettings.SlotCreated / SlotControllerTypes  the slot itself
#
# So it injects, launches, clicks only to NAVIGATE, captures, and restores.
# ASCII-only (PS 5.1 reads a BOM-less .ps1 as ANSI).

param(
    [string]$OutputDir   = "C:\Users\sonic\OneDrive\Documents\GitHub\padforge.org\wiki\images",
    [string]$PadForgeExe = "C:\PadForge\PadForge.exe",
    [string]$PadForgeXml = "C:\PadForge\PadForge.xml"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Logs never go beside the exe. Only PadForge.xml and crash.log may live
# in the deploy directory, and these transcripts were breaking that bar.
$pfLogDir = Join-Path $env:TEMP "PadForge_Capture"
if (-not (Test-Path $pfLogDir)) { New-Item -ItemType Directory -Path $pfLogDir | Out-Null }
$log = Join-Path $pfLogDir "colorway_out.txt"
function Note($m) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Add-Content $log $line -Encoding utf8
    Write-Host $line
}
Set-Content $log "colorway capture start" -Encoding utf8

# ---- Win32 interop: capture + real clicks (UIA cannot drive some of this) ----
Add-Type -Namespace Win32 -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, int e);
public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Shot($name) {
    $r = New-Object Win32.U+RECT
    [void][Win32.U]::GetWindowRect($script:hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Note "  !! bad rect for $name"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $path = Join-Path $OutputDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Note "  >> $name.png ($([math]::Round((Get-Item $path).Length / 1KB))KB)"
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$TD = [System.Windows.Automation.TreeScope]::Descendants

function Find-ByName($name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $script:uiaWin.FindFirst($TD, $cond)
}

function Click-Rect($el, $label) {
    if (-not $el) { Note "  !! no element for $label"; return $false }
    $r = $el.Current.BoundingRectangle
    if ($r.IsEmpty) { Note "  !! empty rect for $label"; return $false }
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [void][Win32.U]::SetForegroundWindow($script:hwnd)
    [void][Win32.U]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [Win32.U]::mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    [Win32.U]::mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP
    Note "  click '$label' at ($x,$y)"
    Start-Sleep -Milliseconds 900
    return $true
}

function Kill-PadForge {
    # taskkill writes "ERROR: The process ... not found." to STDERR when the
    # app is not running. Under $ErrorActionPreference='Stop' PowerShell 5.1
    # turns that into a terminating NativeCommandError, which killed this
    # script at its first line every run (the log stopped at "capture start"
    # and nothing else ever happened). Suppress locally rather than globally.
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill.exe /F /IM PadForge.exe 2>&1 | Out-Null } catch { }
    $ErrorActionPreference = $old
}

# ---- The scenes. One slot per family, appearance written into the slot. ----
# Hero first: the Spider-Man 2 DualSense is the owner's chosen top shot.
$scenes = @(
    @{ Shot = "colorway-dualsense-spiderman2";  Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "SpiderMan2" },
    @{ Shot = "colorway-dualsense-ffxvi";       Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "FFXVI" },
    @{ Shot = "colorway-dualsense-cosmicred";   Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "CosmicRed" },
    @{ Shot = "colorway-dualsense-novapink";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "NovaPink" },
    @{ Shot = "colorway-dualsense-cobalt";      Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "DeepEarthCobalt" },
    @{ Shot = "colorway-dualsense-volcanic";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "DeepEarthVolcanic" },
    @{ Shot = "colorway-dualsense-graycamo";    Type = 1; Profile = "dualsense-composite"; Fam = "DualSense";  App = "GrayCamo" },
    @{ Shot = "colorway-xbox-halo";             Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "HaloInfinite" },
    @{ Shot = "colorway-xbox-starfield";        Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Starfield" },
    @{ Shot = "colorway-xbox-stellarshift";     Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "StellarShift" },
    @{ Shot = "colorway-xbox-porsche";          Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Porsche75th" },
    @{ Shot = "colorway-xbox-velocitygreen";    Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "VelocityGreen" },
    @{ Shot = "colorway-xbox-remix";            Type = 0; Profile = "xbox-series-xs-bt"; Fam = "XboxSeries"; App = "Remix" }
)

# ---- Backup with the clobber guard (never overwrite an existing backup) ----
$bak = "$PadForgeXml.bak"
if (Test-Path $bak) {
    Note "leftover backup found: restoring it BEFORE taking a new one"
    Copy-Item $bak $PadForgeXml -Force
    Remove-Item $bak -Force
}
Kill-PadForge
Start-Sleep 3
Copy-Item $PadForgeXml $bak -Force
Note "backed up owner settings ($((Get-Item $bak).Length) bytes)"

try {
    foreach ($sc in $scenes) {
        # --- write the scene into a fresh copy of the owner's settings ---
        [xml]$xml = Get-Content $bak -Raw
        $ns = $xml.DocumentElement
        # THE TRAP that cost three runs of wrong images: the slot arrays and
        # the app flags do NOT live at the document root. The real path is
        # PadForgeSettings > AppSettings, and PadSettings is the root child.
        # Writing them at the root created ignored duplicate elements while
        # the real values sat untouched, so every scene rendered whatever the
        # slot already was (a Switch Pro) no matter what was "written".
        $app = $ns.SelectSingleNode("AppSettings")
        if (-not $app) { throw "AppSettings node missing from PadForge.xml" }

        function Set-Node($parent, $name, $value) {
            $n = $parent.SelectSingleNode($name)
            if (-not $n) { $n = $xml.CreateElement($name); [void]$parent.AppendChild($n) }
            $n.InnerText = $value
        }

        # One slot, created and enabled, of the scene's type. The arrays are
        # element-per-item; rebuild them wholesale so there is no stale tail.
        foreach ($pair in @(@("SlotCreated","Created"), @("SlotEnabled","Enabled"), @("SlotControllerTypes","Type"))) {
            $arr = $app.SelectSingleNode($pair[0])
            if (-not $arr) { $arr = $xml.CreateElement($pair[0]); [void]$app.AppendChild($arr) }
            while ($arr.HasChildNodes) { [void]$arr.RemoveChild($arr.FirstChild) }
            for ($i = 0; $i -lt 16; $i++) {
                $e = $xml.CreateElement($pair[1])
                $e.InnerText = switch ($pair[0]) {
                    "SlotCreated"         { if ($i -eq 0) { "true" } else { "false" } }
                    "SlotEnabled"         { if ($i -eq 0) { "true" } else { "false" } }
                    "SlotControllerTypes" { if ($i -eq 0) { "$($sc.Type)" } else { "0" } }
                }
                [void]$arr.AppendChild($e)
            }
        }

        # The slot TYPE alone does not choose the model: SlotProfileIds picks
        # the HIDMaestro profile, and that is what the preview renders. The
        # first run wrote type=PlayStation onto a slot whose stored profile
        # was still "switch-pro" and captured a Switch Pro in every shot.
        # InputManager.GetDefaultProfileId: Xbox -> xbox-series-xs-bt,
        # PlayStation -> dualsense-composite.
        $profArr = $app.SelectSingleNode("SlotProfileIds")
        if (-not $profArr) { $profArr = $xml.CreateElement("SlotProfileIds"); [void]$app.AppendChild($profArr) }
        while ($profArr.HasChildNodes) { [void]$profArr.RemoveChild($profArr.FirstChild) }
        for ($i = 0; $i -lt 16; $i++) {
            $e = $xml.CreateElement("Id")
            if ($i -eq 0) { $e.InnerText = $sc.Profile }
            else { [void]$e.SetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance", "true") }
            [void]$profArr.AppendChild($e)
        }

        # 3D view (the colorways are the point), tour off, English.
        Set-Node $app "Use2DControllerView" "false"
        Set-Node $app "FirstRunTourCompleted" "true"
        Set-Node $app "StartMinimized" "false"
        Set-Node $app "Language" "en"

        # The appearance itself, on slot 0's PadSetting.
        $psList = $ns.SelectSingleNode("PadSettings")
        if ($psList -and $psList.HasChildNodes) {
            $ps0 = $psList.ChildNodes[0]
            Set-Node $ps0 "Model3DAppearances" "$($sc.Fam)=$($sc.App)"
        } else {
            Note "  !! no PadSettings to write the appearance into"
        }

        $xml.Save($PadForgeXml)
        Note "scene $($sc.Shot): type=$($sc.Type) $($sc.Fam)=$($sc.App)"

        # --- launch, navigate, capture ---
        Start-Process $PadForgeExe
        Start-Sleep 14
        $proc = Get-Process PadForge -EA SilentlyContinue | Select-Object -First 1
        if (-not $proc) { Note "  !! PadForge did not start"; continue }
        $script:hwnd = $proc.MainWindowHandle
        [void][Win32.U]::ShowWindow($script:hwnd, 3)   # SW_MAXIMIZE
        [void][Win32.U]::SetForegroundWindow($script:hwnd)
        Start-Sleep 2
        $script:uiaWin = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)

        # Navigate via the DASHBOARD card, never the sidebar entry. The
        # sidebar slot card carries a row of type-switch tiles (Xbox /
        # PlayStation / Nintendo / Extended / KBM / MIDI / VR), and a click
        # at its center lands ON that row: the first cut clicked (159,333),
        # hit the Nintendo tile, and every "DualSense" scene captured a
        # Switch Pro because the click had CHANGED the slot's type before
        # the shot. capture_all.ps1 carries the same rule for the same
        # reason ("Dashboard SlotsItemsControl cards, NOT the sidebar").
        $slotsHost = $null
        $aidCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "SlotsItemsControl")
        $slotsHost = $script:uiaWin.FindFirst($TD, $aidCond)
        $card = $null
        if ($slotsHost) {
            $kids = $slotsHost.FindAll([System.Windows.Automation.TreeScope]::Children,
                        [System.Windows.Automation.Condition]::TrueCondition)
            if ($kids.Count -gt 0) { $card = $kids[0] }
        }
        if ($card) { [void](Click-Rect $card "dashboard slot card 1") }
        else { Note "  !! dashboard slot card not found" }
        Start-Sleep 2
        Shot $sc.Shot

        Kill-PadForge
        Start-Sleep 3
    }
}
finally {
    Kill-PadForge
    Start-Sleep 2
    Copy-Item $bak $PadForgeXml -Force
    Remove-Item $bak -Force
    Note "RESTORED owner settings"
    Start-Process $PadForgeExe
    Note "relaunched PadForge"
}
Note "=== DONE ==="
