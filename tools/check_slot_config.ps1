[xml]$x = Get-Content 'C:\PadForge\PadForge.xml'
$types = $x.PadForgeSettings.AppSettings.SlotControllerTypes
$created = $x.PadForgeSettings.AppSettings.SlotCreated
for ($i=0; $i -lt 4; $i++) {
    $t = $types.ChildNodes[$i].InnerText
    $c = $created.ChildNodes[$i].InnerText
    Write-Host "Slot $i : type=$t created=$c"
}
