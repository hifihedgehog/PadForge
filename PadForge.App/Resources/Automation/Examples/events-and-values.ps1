# What happens in PadForge, and a value for a macro: a device that comes
# or goes and a profile that changes show on the console, and the left
# trigger's pull is handed to Custom Expression triggers as "pull".
# Needs Read, and Send Inputs for the value.
Import-Module PadForge

Register-PadForgeDeviceChange { param($id, $online) Write-Host ($id + $(if ($online) { ' connected' } else { ' disconnected' })) }
Register-PadForgeProfile { param($name) Write-Host "Profile: $name" }

$pad = Get-PadForgeDevice gamepad
$pad.OnChange('LeftTrigger', { param($value) Set-PadForgeValue pull $value })
$pad.OnConnect({ param($connected) Write-Host ($pad.Name + $(if ($connected) { ' is here' } else { ' went' })) })

Wait-PadForge
