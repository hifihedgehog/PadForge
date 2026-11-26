<#
Builds the PadForge Plugin Kit's C# plugin, from the PadForge Plugin Kit.

Run it in the kit's folder, with the .NET SDK 10 installed:

    powershell -ExecutionPolicy Bypass -File build.ps1

It builds PadForgePlugin.csproj in Release and copies the program to
bin\PadForgePlugin.exe beside this script, with the files .NET runs it with.
To add the plugin, open PadForge, go to Extras, Plugins, press
Add a Plugin… and pick bin\PadForgePlugin.exe. A rebuilt file shows
Approve and Restart on its card.

-Configuration Debug builds with the debugger's settings. -Output names
another folder for the program.
#>

param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot 'bin')
)

$ErrorActionPreference = 'Stop'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Write-Error 'The .NET SDK is not installed. Get the .NET SDK 10 from https://dotnet.microsoft.com/download.'
    exit 1
}
$project = Join-Path $PSScriptRoot 'PadForgePlugin.csproj'
& $dotnet.Source build $project -c $Configuration -o $Output -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "The plugin did not build (dotnet exited with $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Output ('Built ' + (Join-Path $Output 'PadForgePlugin.exe') + '. Add it on PadForge''s Plugins page.')
