# Set slot 0 to vJoy (type=2) in PadForge.xml and restart PadForge
$padForgeXml = 'C:\PadForge\PadForge.xml'
$padForgeExe = 'C:\PadForge\PadForge.exe'

Stop-Process -Name PadForge -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Every other script in tools/ that writes this file takes a backup first.
# This one edits the live settings in place with no way back, and the change
# it makes is permanent rather than a temporary capture swap.
if (-not (Test-Path -LiteralPath $padForgeXml)) {
    Write-Host "!! $padForgeXml not found. Nothing changed."
    exit 1
}
$bak = "$padForgeXml.setslot-bak"
if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $padForgeXml -Destination $bak }

[xml]$xml = Get-Content $padForgeXml
$appSettings = $xml.PadForgeSettings.AppSettings
$appSettings.SlotControllerTypes.ChildNodes[0].InnerText = '2'  # VJoy = 2
$appSettings.SlotCreated.ChildNodes[0].InnerText = 'true'
$xml.Save($padForgeXml)

Write-Host "Set slot 0 to vJoy, launching PadForge..."
Start-Process $padForgeExe
Start-Sleep -Seconds 5
Write-Host "Done"
