# Attempt to purge ghost vJoy device nodes using CfgMgr32 API
$out = @()
$out += "=== vJoy Ghost Purge $(Get-Date) ==="

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CfgMgr {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out int pdnDevInst, string pDeviceID, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Uninstall_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out int pulStatus, out int pulProblemNumber, int dnDevInst, int ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Query_And_Remove_SubTreeW(int dnDevInst, out int pVetoType, StringBuilder pszVetoName, int ulNameLength, int ulFlags);

    public const int CM_LOCATE_DEVNODE_NORMAL = 0;
    public const int CM_LOCATE_DEVNODE_PHANTOM = 1;
    public const int CM_REMOVE_NO_RESTART = 2;
    public const int CR_SUCCESS = 0;
}
'@

# Try to locate and uninstall each ghost node
for ($i = 0; $i -le 10; $i++) {
    $nodeId = "ROOT\HIDCLASS\{0:D4}" -f $i
    $devInst = 0
    # Try normal first, then phantom
    $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, [CfgMgr]::CM_LOCATE_DEVNODE_NORMAL)
    if ($cr -ne 0) {
        $cr = [CfgMgr]::CM_Locate_DevNodeW([ref]$devInst, $nodeId, [CfgMgr]::CM_LOCATE_DEVNODE_PHANTOM)
    }
    if ($cr -ne 0) { continue }

    # Get status
    $status = 0; $problem = 0
    [CfgMgr]::CM_Get_DevNode_Status([ref]$status, [ref]$problem, $devInst, 0) | Out-Null
    $out += "Found $nodeId (devInst=$devInst status=0x$($status.ToString('X')) problem=$problem)"

    # Try CM_Query_And_Remove_SubTreeW first
    $vetoType = 0
    $vetoName = New-Object System.Text.StringBuilder 260
    $cr = [CfgMgr]::CM_Query_And_Remove_SubTreeW($devInst, [ref]$vetoType, $vetoName, 260, [CfgMgr]::CM_REMOVE_NO_RESTART)
    $out += "  CM_Query_And_Remove_SubTree: cr=$cr vetoType=$vetoType veto=$($vetoName.ToString())"

    if ($cr -ne 0) {
        # Fallback: CM_Uninstall_DevNode
        $cr2 = [CfgMgr]::CM_Uninstall_DevNode($devInst, 0)
        $out += "  CM_Uninstall_DevNode: cr=$cr2"
    }
}

Start-Sleep -Seconds 2
pnputil /scan-devices 2>&1 | Out-Null
Start-Sleep -Seconds 2

# Check what's left
$out += ""
$out += "After purge:"
$pnp = pnputil /enum-devices /class HIDClass 2>&1
$currentId = $null
foreach ($line in ($pnp -split "`n")) {
    $t = $line.Trim()
    if ($t -match 'Instance ID\s*:\s*(ROOT\\HIDCLASS\\\d+)') { $currentId = $matches[1].Trim() }
    elseif ($t -match 'Status\s*:\s*(.+)' -and $currentId) {
        $out += "  $currentId | $($matches[1].Trim())"
        $currentId = $null
    }
    elseif (-not $t) { $currentId = $null }
}

$out | Out-File 'C:\Users\sonic\OneDrive\Documents\GitHub\PadForge\tools\vjoy_purge_log.txt' -Encoding utf8 -Force
$out | ForEach-Object { Write-Host $_ }
