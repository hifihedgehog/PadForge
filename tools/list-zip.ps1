param([string]$Path)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
$zip.Entries | ForEach-Object { Write-Host $_.FullName }
$zip.Dispose()
