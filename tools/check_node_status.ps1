Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class CfgMgr {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(int dnDevInst, int ulFlags);
    public const int CM_DISABLE_HARDWARE = 0x4;
}
"@ -ErrorAction SilentlyContinue

$devInst = 0
$cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, 'ROOT\HIDCLASS\0000', 0)
Write-Host "Locate: cr=$cr devInst=$devInst"
if ($cr -eq 0 -and $devInst -ne 0) {
    $s = 0; $p = 0
    [CfgMgr]::CM_Get_DevNode_Status([ref]$s, [ref]$p, $devInst, 0) | Out-Null
    $needRestart = ($s -band 0x01000000) -ne 0
    Write-Host ("Status=0x{0:X8} DN_NEED_RESTART={1} Problem={2}" -f $s, $needRestart, $p)

    # Count joy.cpl vJoy entries
    $jc = (Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | Where-Object { $_.DeviceID -like '*VJOYRAWPDO*' } | Measure-Object).Count
    Write-Host "joyCpl vJoy count: $jc"

    # Check registry
    $baseKey = 'HKLM:\SYSTEM\CurrentControlSet\services\vjoy\Parameters'
    if (Test-Path $baseKey) {
        $devKeys = Get-ChildItem $baseKey -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^Device\d+$' }
        Write-Host ("Registry DeviceNN keys: " + ($devKeys | Measure-Object).Count)
    }
}
