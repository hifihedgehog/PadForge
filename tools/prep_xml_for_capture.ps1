# Prep PadForge.xml: 5 slot types (Xbox/PS/Extended/KBM/MIDI) + sample macros.
# Stops PadForge, edits the XML, restarts. Backup at PadForge.xml.bak-capture.

$XmlPath = "C:\PadForge\PadForge.xml"
$ExePath = "C:\PadForge\PadForge.exe"

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
Copy-Item $XmlPath "$XmlPath.bak-capture" -Force

[xml]$xml = Get-Content $XmlPath
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
# so cloning is safe — the dashboard will render PS / Extended / KBM /
# MIDI cards using the right type's UI even though every slot uses the
# same default Xbox mapping.
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

$xml.Save($XmlPath)
Write-Host "Saved PadForge.xml with 5 slot types + 2 sample macros" -ForegroundColor Green

Start-Process $ExePath
Write-Host "Launched PadForge; waiting 18s for HM to bring up 5 slots..."
Start-Sleep -Seconds 18
Write-Host "Ready." -ForegroundColor Green
