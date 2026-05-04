$logFile = "C:\PadForge\capture_web_log.txt"
"START $(Get-Date -Format HH:mm:ss)" | Out-File $logFile -Encoding ascii
try {
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W3W {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fAltTab);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
}
"@
    # Make this PowerShell DPI-aware so GetWindowRect returns physical
    # pixels (matching what CopyFromScreen captures). Without this,
    # GetWindowRect returns DIPs and we capture only the top-left 2/3
    # of the actual window on a 150% DPI display.
    [W3W]::SetProcessDPIAware() | Out-Null

    $XmlPath = "C:\PadForge\PadForge.xml"
    $ExePath = "C:\PadForge\PadForge.exe"
    $OutputDir = "C:\Users\sonic\OneDrive\Documents\GitHub\PadForge.wiki\images"

    # Enable web server in XML
    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    [xml]$xml = Get-Content $XmlPath
    $app = $xml.PadForgeSettings.AppSettings
    $node = $app.SelectSingleNode("EnableWebController")
    if (-not $node) {
        $node = $xml.CreateElement("EnableWebController")
        $app.AppendChild($node) | Out-Null
    }
    $node.InnerText = "true"
    $xml.Save($XmlPath)
    "Set EnableWebController=true" | Out-File $logFile -Encoding ascii -Append

    Start-Process $ExePath
    Start-Sleep -Seconds 18
    "PadForge launched" | Out-File $logFile -Encoding ascii -Append

    # Find local IP
    $localIp = (Test-NetConnection -ComputerName 8.8.8.8 -InformationLevel Quiet -EA SilentlyContinue) |
        Out-Null
    $localIp = (Get-NetIPAddress -AddressFamily IPv4 -PrefixOrigin Dhcp -EA SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.*' } | Select-Object -First 1).IPAddress
    if (-not $localIp) { $localIp = "127.0.0.1" }
    $url = "http://$($localIp):8080"
    "Web URL: $url" | Out-File $logFile -Encoding ascii -Append

    # Open Edge in --app mode at landscape size matching the old screenshots
    # (controller's CSS expects landscape; oversized window rotates it).
    Start-Process "msedge.exe" -ArgumentList "--app=$url", "--window-size=1280,720", "--window-position=0,0"
    Start-Sleep -Seconds 8
    "Browser opened" | Out-File $logFile -Encoding ascii -Append

    # Find the Edge window by TITLE — Edge --app spawns several processes,
    # only one of which owns the visible content HWND. Match titles
    # starting with "PadForge".
    $edgeProc = Get-Process msedge -EA SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like 'PadForge*' } |
        Sort-Object StartTime -Descending | Select-Object -First 1
    if (-not $edgeProc) {
        "Edge content window not found" | Out-File $logFile -Encoding ascii -Append
        exit
    }
    $hwnd = $edgeProc.MainWindowHandle
    "Edge HWND: $hwnd, Title: $($edgeProc.MainWindowTitle)" | Out-File $logFile -Encoding ascii -Append
    # Don't maximize — the controller view auto-rotates in oversized
    # windows. Just bring to top at the size Edge launched with.
    [W3W]::SwitchToThisWindow($hwnd, $true)
    [W3W]::SetWindowPos($hwnd, [W3W]::HWND_TOPMOST, 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040) | Out-Null
    [W3W]::SetWindowPos($hwnd, [W3W]::HWND_NOTOPMOST, 0, 0, 0, 0, 0x0002 -bor 0x0001 -bor 0x0040) | Out-Null
    Start-Sleep -Seconds 2

    function Cap-Web {
        param([string]$Name)
        $r = New-Object W3W+RECT
        [W3W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.R - $r.L; $h = $r.B - $r.T
        if ($w -le 0 -or $h -le 0) { return }
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, [System.Drawing.Size]::new($w, $h))
        $g.Dispose()
        $p = Join-Path $OutputDir "$Name.png"
        $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $kb = [math]::Round((Get-Item $p).Length / 1024)
        "Saved $Name.png (${kb}KB)" | Out-File $logFile -Encoding ascii -Append
    }

    # Landing page
    Cap-Web "web-landing"

    # Tab to first card (Xbox 360 layout) and press Enter to enter controller view.
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Seconds 5
    Cap-Web "web-controller"

    # Cleanup: close Edge, disable web server
    Stop-Process -Name msedge -Force -EA SilentlyContinue
    Start-Sleep -Seconds 2

    Get-Process PadForge -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    [xml]$xml = Get-Content $XmlPath
    $app = $xml.PadForgeSettings.AppSettings
    $node = $app.SelectSingleNode("EnableWebController")
    if ($node) { $node.InnerText = "false" }
    $xml.Save($XmlPath)
    Start-Process $ExePath
    "Cleanup done" | Out-File $logFile -Encoding ascii -Append
} catch { "FATAL: $_" | Out-File $logFile -Encoding ascii -Append }
"END" | Out-File $logFile -Encoding ascii -Append
