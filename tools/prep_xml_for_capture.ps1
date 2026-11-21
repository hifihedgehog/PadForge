# Prep PadForge.xml: 5 slot types (Xbox/PS/Extended/KBM/MIDI) + sample macros.
# Stops PadForge, edits the XML, restarts. Backup at PadForge.xml.bak-capture.
# The owner's settings stay in that backup, marked as this run's, until
# add_slots_via_ui.ps1 or the next run of this script puts them back on the
# marker's word (settings_guard.ps1).
param(
    # The owner's answer to a run that stopped over a settings backup lying
    # beside the settings file (settings_guard.ps1). Ask, the default, is that
    # stop. File keeps the settings file and sets the backup aside. Backup puts
    # the backup back over the settings file. A run with nothing to ask
    # refuses File and Backup.
    [ValidateSet('Ask', 'File', 'Backup')]
    [string]$KeepSettingsFrom = 'Ask'
)

$XmlPath = "C:\PadForge\PadForge.xml"
$ExePath = "C:\PadForge\PadForge.exe"
$BakPath = "$XmlPath.bak-capture"

# --- The owner's settings file ------------------------------------------------
# The marker, the copies, and the loader and saver that every script under
# tools shares. settings_guard.ps1 says what each one does.
. (Join-Path $PSScriptRoot 'settings_guard.ps1')

# A backup already there is settled before the app is stopped or a file is
# touched, so a run that stops has changed nothing. Until the backup is taken
# and marked, any failure ends the run.
$prevPreference = $ErrorActionPreference
$ErrorActionPreference = 'Stop'
$doubt = Get-BackupDoubt $XmlPath $BakPath 'Ask'
$stop = @()
if ($KeepSettingsFrom -eq 'Ask' -and $doubt) {
    $stop = @(Get-BackupStopLines $XmlPath $BakPath $doubt)
} elseif ($KeepSettingsFrom -ne 'Ask' -and -not $doubt) {
    $stop = @("!! -KeepSettingsFrom $KeepSettingsFrom answers a run that stopped over a settings backup, and this run has nothing to ask. Leave it out.")
}
if ($stop.Count -gt 0) {
    foreach ($line in $stop) { Write-Host $line -ForegroundColor Red }
    exit 1
}

Write-Host "Stopping PadForge..."
$proc = Get-Process PadForge -EA SilentlyContinue
if ($proc) {
    foreach ($p in $proc) {
        try { $p.CloseMainWindow() | Out-Null } catch {}
    }
    Start-Sleep -Seconds 3
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $XmlPath)) { Write-Host "!! Missing $XmlPath" -ForegroundColor Red; exit 1 }

# This backed up with -Force, so a second run (the normal response to a failed
# capture) copied the ALREADY-PREPPED synthetic config over the only copy of
# the real settings. capture_all.ps1 already solves this, after the same thing
# destroyed a settings file on 2026-07-12 and it had to be recovered from a
# shadow copy: a leftover backup means an earlier run never restored, so that
# backup is the real settings and the live file is capture residue. Restore it
# first, then re-back it up. Same shape here, and only on the marker's word:
# a backup left behind after the settings were put back by hand sits beside
# the owner's current file, and the run stops above for the owner to decide.
#
# Asked again now that the app is down. A save it made after the question
# above can make two files that were the same differ. The run stops with
# neither file touched and starts PadForge again.
$doubt = Get-BackupDoubt $XmlPath $BakPath $KeepSettingsFrom
if ($doubt) {
    foreach ($line in (Get-BackupStopLines $XmlPath $BakPath $doubt)) { Write-Host $line -ForegroundColor Red }
    Start-Process $ExePath
    exit 1
}
if (Test-Path $BakPath) {
    if ($KeepSettingsFrom -eq 'File') {
        $kept = Save-SettingsCopy $BakPath
        Remove-Item -LiteralPath $BakPath -Force
        Remove-BackupMarker $BakPath
        Write-Host "-KeepSettingsFrom File: the settings file stays as it is, and the backup is set aside as $kept" -ForegroundColor Yellow
    } else {
        $kept = Save-SettingsCopy $XmlPath
        Write-Host "!! Restoring the backup an interrupted run left, before the backup is taken again" -ForegroundColor Yellow
        if ($kept) { Write-Host "!! The file it replaces is kept as $kept" -ForegroundColor Yellow }
        Copy-Item -LiteralPath $BakPath -Destination $XmlPath -Force
    }
}
# A backup is written only where none is, so a copy cut short can never leave
# half a file over a good backup.
if (-not (Test-Path $BakPath)) { Copy-Item -LiteralPath $XmlPath -Destination $BakPath -Force }
Set-BackupMarker $BakPath
Write-Host "Backed up real settings to $BakPath"
$ErrorActionPreference = $prevPreference

$xml = Read-SettingsXml $XmlPath
$ns = $xml.PadForgeSettings

# All slot arrays + SlotProfileIds live inside <AppSettings>, not at the root.
$app = $ns.AppSettings
$desiredTypes = @(0, 1, 2, 4, 3)
$typeChildren = $app.SlotControllerTypes.ChildNodes
Write-Host "Type children: $($typeChildren.Count)"
for ($i = 0; $i -lt $desiredTypes.Count; $i++) { $typeChildren[$i].InnerText = $desiredTypes[$i] }

$createdChildren = $app.SlotCreated.ChildNodes
for ($i = 0; $i -lt 5; $i++) { $createdChildren[$i].InnerText = "true" }

$profileChildren = $app.SlotProfileIds.ChildNodes
Write-Host "Profile children: $($profileChildren.Count)"
function Set-IdNode { param($node, [string]$value); $node.RemoveAllAttributes() | Out-Null; $node.InnerText = $value }
function Set-IdNil  { param($node); $node.InnerText = ""; $a = $xml.CreateAttribute("xsi","nil","http://www.w3.org/2001/XMLSchema-instance"); $a.Value = "true"; $node.Attributes.Append($a) | Out-Null }
Set-IdNode $profileChildren[1] "dualsense-bt"
Set-IdNode $profileChildren[2] "padforge-custom"
Set-IdNil  $profileChildren[3]
Set-IdNil  $profileChildren[4]

# Duplicate the existing slot-0 PadSetting block 4 more times so slots
# 1-4 each have a valid PadSetting at their index. The slot's TYPE
# comes from SlotControllerTypes, not from any field inside PadSetting,
# so cloning is safe. Home renders PS / Extended / KBM / MIDI
# cards with the right type's UI even though every slot uses the same
# default Xbox mapping.
$padSettingsNode = $ns.PadSettings
$existingCount = $padSettingsNode.ChildNodes.Count
Write-Host "PadSettings existing count: $existingCount"
$slot0PadSetting = $padSettingsNode.ChildNodes[0]
for ($i = $existingCount; $i -lt 5; $i++) {
    $clone = $slot0PadSetting.CloneNode($true)
    $padSettingsNode.AppendChild($clone) | Out-Null
}
Write-Host "PadSettings count after clone: $($padSettingsNode.ChildNodes.Count)"

# Inject sample macros
$macrosNode = $null
foreach ($n in $ns.ChildNodes) { if ($n.LocalName -eq "Macros") { $macrosNode = $n; break } }
if (-not $macrosNode) { $macrosNode = $xml.CreateElement("Macros"); $ns.AppendChild($macrosNode) | Out-Null }
$macrosNode.RemoveAll()

$m1 = "<Macro PadIndex=`"0`"><Name>Quick Combo</Name><IsEnabled>true</IsEnabled><TriggerButtons>4096</TriggerButtons><TriggerAxisTargets>LeftTrigger</TriggerAxisTargets><TriggerAxisThreshold>50</TriggerAxisThreshold><TriggerSource>OutputController</TriggerSource><TriggerMode>OnPress</TriggerMode><ConsumeTriggerButtons>true</ConsumeTriggerButtons><RepeatMode>Once</RepeatMode><Actions><Action><Type>ButtonPress</Type><ButtonFlags>4096</ButtonFlags><DurationMs>100</DurationMs></Action><Action><Type>Delay</Type><DurationMs>200</DurationMs></Action><Action><Type>KeyPress</Type><KeyCode>32</KeyCode><DurationMs>50</DurationMs></Action><Action><Type>MouseButtonPress</Type><MouseButton>Left</MouseButton><DurationMs>50</DurationMs></Action></Actions></Macro>"
$m2 = "<Macro PadIndex=`"0`"><Name>Volume Control</Name><IsEnabled>true</IsEnabled><TriggerSource>OutputController</TriggerSource><TriggerMode>Always</TriggerMode><RepeatMode>Once</RepeatMode><Actions><Action><Type>SystemVolume</Type><AxisTarget>LeftTrigger</AxisTarget><VolumeLimit>75</VolumeLimit></Action><Action><Type>MouseMove</Type><AxisTarget>RightStickX</AxisTarget><MouseSensitivity>15</MouseSensitivity></Action></Actions></Macro>"
$f = $xml.CreateDocumentFragment(); $f.InnerXml = $m1; $macrosNode.AppendChild($f) | Out-Null
$f = $xml.CreateDocumentFragment(); $f.InnerXml = $m2; $macrosNode.AppendChild($f) | Out-Null

if ($app -and $app.StartMinimized) { $app.StartMinimized = "false" }

$kept = Save-SettingsXml $xml $XmlPath
Write-Host "Saved PadForge.xml with 5 slot types + 2 sample macros (the file before it is kept as $kept)" -ForegroundColor Green

Start-Process $ExePath
Write-Host "Launched PadForge; waiting 18s for HM to bring up 5 slots..."
Start-Sleep -Seconds 18
Write-Host "Ready." -ForegroundColor Green
