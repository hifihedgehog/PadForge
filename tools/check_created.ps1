[xml]$x = Get-Content 'C:\PadForge\PadForge.xml'
$nodes = $x.PadForgeSettings.AppSettings.SlotCreated.ChildNodes
for ($i=0; $i -lt 4; $i++) {
    Write-Host "SlotCreated[$i] = $($nodes[$i].InnerText)"
}
