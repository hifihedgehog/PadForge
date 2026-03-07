# Child script: queries a single vJoy device via vJoyInterface.dll
# Usage: vjoy_query_device.ps1 -DeviceId 1 -OutFile result.txt
param([int]$DeviceId = 1, [string]$OutFile)

$vjoyRoot = Join-Path $env:ProgramFiles 'vJoy'
$env:PATH = "$vjoyRoot;$vjoyRoot\x64;$env:PATH"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class VJ {
    const string DLL = "vJoyInterface.dll";
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool vJoyEnabled();
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDStatus(uint rID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDButtonNumber(uint rID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetVJDContPovNumber(uint rID);
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool GetVJDAxisExist(uint rID, uint Axis);
}
'@

$rid = [uint32]$DeviceId
$status = [VJ]::GetVJDStatus($rid)

$axisUsages = @(0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37)
$axisCount = 0
foreach ($u in $axisUsages) {
    if ([VJ]::GetVJDAxisExist($rid, [uint32]$u)) { $axisCount++ }
}
$buttons = [VJ]::GetVJDButtonNumber($rid)
$contPovs = [VJ]::GetVJDContPovNumber($rid)

$result = "status=$status axes=$axisCount buttons=$buttons contPovs=$contPovs"
if ($OutFile) { $result | Out-File $OutFile -Encoding utf8 -Force }
Write-Host $result
