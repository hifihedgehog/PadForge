<#
PadForge Automation for PowerShell. Speaks protocol 1 of PadForge's
Automation pipe, PadForge.Automation. Works in Windows PowerShell 5.1.

Turn on Automation in PadForge (Extras, Automation) at the level your script
needs: Read to follow devices, Send Inputs to add controllers, hold controls
and set values.

Example:

    Import-Module .\PadForge.psm1
    Connect-PadForge -Name 'Turbo A' | Out-Null
    $pad = Find-PadForgeDevice gamepad
    Register-PadForgeButton -Device $pad -Button 0 -Action { param($down) "A down: $down" }
    Wait-PadForge

Buttons, axes and hats use PadForge's own numbers and ranges: an axis runs 0
to 65535 with a stick at rest on 32768, axes 0 to 23 are the axes and 24 to
31 the sliders, and a hat is in hundredths of a degree or -1. Event actions
run while the script waits in Wait-PadForge or Wait-Event.
#>

$script:Protocol = 1
$script:DefaultPipe = 'PadForge.Automation'

if (-not ('PadForgeAutomation.Connection' -as [type])) {
    Add-Type -ReferencedAssemblies System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadForgeAutomation
{
    public sealed class LineEventArgs : EventArgs
    {
        public LineEventArgs(string line) { Line = line; }
        public string Line { get; private set; }
    }

    // One connection. The pipe is opened for overlapped I/O, so the read
    // that waits on a pool thread never holds up a write.
    public sealed class Connection : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly object _writeLock = new object();
        private readonly object _pendingLock = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly byte[] _buffer = new byte[65536];
        private readonly List<byte> _line = new List<byte>();
        private int _nextTag;
        private Timer _beat;
        private volatile bool _closed;

        private sealed class Pending
        {
            public readonly List<string> Rows = new List<string>();
            public string Final;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        public event EventHandler<LineEventArgs> Event;
        public event EventHandler Closed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WaitNamedPipeW(string name, uint timeout);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string className, string windowName);

        // 0 the pipe stands, 1 nothing serves it, 2 PadForge runs with
        // Automation off.
        public static int Probe(string name, bool isTheAppsOwn)
        {
            if (WaitNamedPipeW(@"\\.\pipe\" + name, 1)) return 0;
            int error = Marshal.GetLastWin32Error();
            if (error != 2) return 0;
            if (isTheAppsOwn && FindWindowExW(new IntPtr(-3), IntPtr.Zero, null, "PadForge.Instance") != IntPtr.Zero) return 2;
            return 1;
        }

        public Connection(string name, int timeoutMs)
        {
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(timeoutMs);
            BeginRead();
        }

        public bool IsClosed { get { return _closed; } }

        private void BeginRead()
        {
            try { _pipe.BeginRead(_buffer, 0, _buffer.Length, OnRead, null); }
            catch (Exception) { Close(); }
        }

        private void OnRead(IAsyncResult result)
        {
            int n;
            try { n = _pipe.EndRead(result); }
            catch (Exception) { n = 0; }
            if (n <= 0) { Close(); return; }
            for (int i = 0; i < n; i++)
            {
                byte b = _buffer[i];
                if (b == (byte)'\n')
                {
                    string text = Encoding.UTF8.GetString(_line.ToArray()).TrimEnd('\r');
                    _line.Clear();
                    OnLine(text);
                }
                else _line.Add(b);
            }
            BeginRead();
        }

        private void OnLine(string line)
        {
            if (line.StartsWith("e ", StringComparison.Ordinal))
            {
                EventHandler<LineEventArgs> handler = Event;
                if (handler != null) handler(this, new LineEventArgs(line));
                return;
            }
            string tag = null;
            if (line.StartsWith("@", StringComparison.Ordinal))
            {
                int space = line.IndexOf(' ');
                tag = space < 0 ? line : line.Substring(0, space);
                line = space < 0 ? string.Empty : line.Substring(space + 1);
            }
            Pending pending = null;
            if (tag != null) lock (_pendingLock) _pending.TryGetValue(tag, out pending);
            if (pending == null)
            {
                if (line.StartsWith("err ", StringComparison.Ordinal))
                {
                    EventHandler<LineEventArgs> handler = Event;
                    if (handler != null) handler(this, new LineEventArgs("e error " + line.Substring(4)));
                }
                return;
            }
            if (line == "ok" || line.StartsWith("ok ", StringComparison.Ordinal) || line.StartsWith("err ", StringComparison.Ordinal))
            {
                pending.Final = line;
                pending.Done.Set();
            }
            else pending.Rows.Add(line);
        }

        // The answer's rows and then its final line, ok or err. Null when
        // the connection closed first.
        public string[] Request(string line, int timeoutMs)
        {
            string tag = "@" + Interlocked.Increment(ref _nextTag).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pending pending = new Pending();
            lock (_pendingLock) _pending[tag] = pending;
            try
            {
                Send(tag + " " + line);
                if (!pending.Done.WaitOne(timeoutMs)) throw new TimeoutException("PadForge did not answer.");
                if (pending.Final == null) return null;
                List<string> all = new List<string>(pending.Rows);
                all.Add(pending.Final);
                return all.ToArray();
            }
            finally
            {
                lock (_pendingLock) _pending.Remove(tag);
            }
        }

        public void Send(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            lock (_writeLock)
            {
                if (_closed) throw new IOException("The connection to PadForge is closed.");
                _pipe.Write(bytes, 0, bytes.Length);
                _pipe.Flush();
            }
        }

        // Keeps the lease: a line at least every lease / 3.
        public void KeepLease(int leaseMs)
        {
            int every = Math.Max(50, leaseMs / 3);
            _beat = new Timer(delegate { try { Send("beat"); } catch (Exception) { } }, null, every, every);
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            if (_beat != null) _beat.Dispose();
            lock (_pendingLock)
            {
                foreach (Pending p in _pending.Values) p.Done.Set();
            }
            EventHandler handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (!_closed)
            {
                try { Send("bye"); } catch (Exception) { }
            }
            Close();
            try { _pipe.Dispose(); } catch (Exception) { }
        }
    }
}
'@
}

# ── Words on the line ──

function ConvertTo-PadForgeQuoted([string]$Text) {
    '"' + ($Text -replace '\\', '\\' -replace '"', '\"' -replace "[`r`n]", ' ') + '"'
}

function Split-PadForgeLine([string]$Line) {
    # The bare words, the key=value pairs, and the JSON the line ends with.
    $words = New-Object System.Collections.Generic.List[string]
    $pairs = @{}
    $json = $null
    $i = 0; $n = $Line.Length
    while ($i -lt $n) {
        while ($i -lt $n -and $Line[$i] -eq ' ') { $i++ }
        if ($i -ge $n) { break }
        if ($Line[$i] -eq '{') { $json = $Line.Substring($i); break }
        $key = $null; $value = New-Object System.Text.StringBuilder; $quoted = $false
        while ($i -lt $n -and ($quoted -or $Line[$i] -ne ' ')) {
            $c = $Line[$i]
            if ($quoted) {
                if ($c -eq '\' -and $i + 1 -lt $n -and ($Line[$i + 1] -eq '"' -or $Line[$i + 1] -eq '\')) {
                    [void]$value.Append($Line[$i + 1]); $i += 2; continue
                }
                if ($c -eq '"') { $quoted = $false } else { [void]$value.Append($c) }
            }
            elseif ($c -eq '"') { $quoted = $true }
            elseif ($c -eq '=' -and $null -eq $key) { $key = $value.ToString(); [void]$value.Clear() }
            else { [void]$value.Append($c) }
            $i++
        }
        if ($null -eq $key) { $words.Add($value.ToString()) } else { $pairs[$key] = $value.ToString() }
    }
    [pscustomobject]@{ Words = $words.ToArray(); Pairs = $pairs; Json = $json }
}

# ── The connection ──

$script:Connection = $null
$script:Level = ''
$script:Callbacks = @{}
$script:Subscribed = @{}

function Get-PadForgeConnection {
    if ($null -eq $script:Connection -or $script:Connection.Handle.IsClosed) {
        throw 'Not connected to PadForge. Run Connect-PadForge first.'
    }
    $script:Connection
}

function Invoke-PadForgeRequest {
    <# Sends a line and gives the parsed ok line, with .Rows holding the
       rows before it. Throws PadForge's refusal in words. #>
    param([Parameter(Mandatory)][string]$Line)
    $c = Get-PadForgeConnection
    $answer = $c.Handle.Request($Line, 30000)
    if ($null -eq $answer) { throw 'The connection to PadForge closed.' }
    $final = $answer[$answer.Length - 1]
    if ($final.StartsWith('err ')) {
        $parts = $final.Split(' ', 3)
        $code = $parts[1]
        $verb = $Line.Split(' ')[0]
        if ($code -eq 'not-allowed') {
            throw "PadForge refused ${verb}: Automation's level is $($script:Level), too low for it. Raise it in Extras, Automation."
        }
        $words = if ($parts.Length -gt 2) { $parts[2] } else { '' }
        throw "PadForge refused ${verb}: $words ($code)."
    }
    $parsed = Split-PadForgeLine $final.Substring(2).Trim()
    $rows = @()
    if ($answer.Length -gt 1) { $rows = $answer[0..($answer.Length - 2)] | ForEach-Object { Split-PadForgeLine $_ } }
    $parsed | Add-Member -NotePropertyName Rows -NotePropertyValue @($rows) -PassThru
}

function Connect-PadForge {
    <# Opens a connection to the PadForge that runs on this PC. #>
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [string]$Pipe = $script:DefaultPipe,
        [int]$Lease = 2000,
        [int]$TimeoutSeconds = 5
    )
    $probe = [PadForgeAutomation.Connection]::Probe($Pipe, $Pipe -eq $script:DefaultPipe)
    if ($probe -eq 2) { throw 'PadForge is running, but Automation is off. Turn it on in Extras, Automation.' }
    if ($probe -eq 1) {
        if ($Pipe -eq $script:DefaultPipe) { throw 'PadForge is not running.' }
        throw "Nothing serves the pipe $Pipe."
    }
    $handle = New-Object PadForgeAutomation.Connection($Pipe, ($TimeoutSeconds * 1000))
    $script:Connection = [pscustomobject]@{ Handle = $handle; Lease = $Lease; Session = 0; Version = '' }
    $script:Callbacks = @{}
    $script:Subscribed = @{}
    Register-ObjectEvent -InputObject $handle -EventName Event -SourceIdentifier ('PadForge.Event.' + $handle.GetHashCode()) -Action {
        Invoke-PadForgeEvent $EventArgs.Line
    } | Out-Null
    $hello = Invoke-PadForgeRequest ("hello name=" + (ConvertTo-PadForgeQuoted $Name) + " kind=script protocol=$($script:Protocol) lease=$Lease")
    $script:Level = $hello.Pairs['level']
    $script:Connection.Session = [int]$hello.Pairs['session']
    $script:Connection.Version = $hello.Pairs['padforge']
    $handle.KeepLease($Lease)
    [pscustomobject]@{ Version = $hello.Pairs['padforge']; Level = $script:Level; Session = $script:Connection.Session }
}

function Disconnect-PadForge {
    if ($null -ne $script:Connection) {
        Get-EventSubscriber | Where-Object { $_.SourceIdentifier -like 'PadForge.Event.*' } | Unregister-Event
        $script:Connection.Handle.Dispose()
        $script:Connection = $null
    }
}

function Wait-PadForge {
    <# Runs event actions until the connection closes, or for -Seconds. #>
    param([double]$Seconds = 0)
    $until = if ($Seconds -gt 0) { [DateTime]::UtcNow.AddSeconds($Seconds) } else { [DateTime]::MaxValue }
    while ($null -ne $script:Connection -and -not $script:Connection.Handle.IsClosed -and [DateTime]::UtcNow -lt $until) {
        Wait-Event -Timeout 1 -SourceIdentifier 'PadForge.Wait' | Out-Null
    }
}

# ── Events ──

function Invoke-PadForgeEvent([string]$Line) {
    $e = Split-PadForgeLine $Line
    $w = $e.Words
    if ($w.Length -lt 2) { return }
    $key = $null; $value = $null
    switch ($w[1]) {
        'b' { $key = "b|$($w[2])|$($w[3])"; $value = ($w[4] -eq '1') }
        'h' { $key = "h|$($w[2])|$($w[3])"; $value = [int]$w[4] }
        'a' { $key = "a|$($w[2])|$($w[3])"; $value = [int]$w[4] }
        'x' { $key = "x|$($w[2])|$($w[3])"; $value = ($w[4] -eq '1') }
        'dev' { $key = 'dev'; $value = @($w[2], ($w[3] -eq 'online')) }
        'profile' { $key = 'profile'; $value = $(if ($w.Length -gt 2) { $w[2] } else { 'default' }) }
        'engine' { $key = 'engine'; $value = $w[2] }
        'level' { $script:Level = $w[2]; $key = 'level'; $value = $w[2] }
        'rumble' { $key = "rumble|$($w[2])"; $value = @([int]$w[3], [int]$w[4]) }
        'trigrumble' { $key = "trigrumble|$($w[2])"; $value = @([int]$w[3], [int]$w[4]) }
        'led' { $key = "led|$($w[2])"; $value = $w[3] }
        'player' { $key = "player|$($w[2])"; $value = [int]$w[3] }
        'lease' { $key = 'lease'; $value = $w[2] }
        'error' { $key = 'error'; $value = ($w[2..($w.Length - 1)] -join ' ') }
    }
    if ($null -ne $key -and $script:Callbacks.ContainsKey($key)) {
        foreach ($action in $script:Callbacks[$key]) {
            if ($value -is [array]) { & $action @value } else { & $action $value }
        }
    }
}

function Add-PadForgeCallback([string]$Key, [scriptblock]$Action) {
    if (-not $script:Callbacks.ContainsKey($Key)) { $script:Callbacks[$Key] = New-Object System.Collections.ArrayList }
    [void]$script:Callbacks[$Key].Add($Action)
}

function Get-PadForgeId($Device) {
    if ($Device -is [string]) { return $Device }
    $Device.Id
}

function Register-PadForgeButton {
    <# Runs -Action { param($down) } on every press and release of a button
       of any device, keyboards and mice included. #>
    param([Parameter(Mandatory)]$Device, [Parameter(Mandatory)][int]$Button, [Parameter(Mandatory)][scriptblock]$Action)
    $id = Get-PadForgeId $Device
    Add-PadForgeCallback "b|$id|$Button" $Action
    if (-not $script:Subscribed.ContainsKey("buttons|$id")) {
        Invoke-PadForgeRequest "sub $id buttons" | Out-Null
        $script:Subscribed["buttons|$id"] = $true
    }
}

function Register-PadForgeHat {
    <# Runs -Action { param($value) } when a hat moves: hundredths of a
       degree, or -1. #>
    param([Parameter(Mandatory)]$Device, [Parameter(Mandatory)][int]$Hat, [Parameter(Mandatory)][scriptblock]$Action)
    $id = Get-PadForgeId $Device
    Add-PadForgeCallback "h|$id|$Hat" $Action
    if (-not $script:Subscribed.ContainsKey("hats|$id")) {
        Invoke-PadForgeRequest "sub $id hats" | Out-Null
        $script:Subscribed["hats|$id"] = $true
    }
}

function Register-PadForgeAxis {
    <# With -Above, runs -Action { param($above) } when the axis reaches it
       and when it falls back past Above - Hysteresis, worked out by PadForge.
       Without it, runs -Action { param($value) } up to -Hz times a second. #>
    param([Parameter(Mandatory)]$Device, [Parameter(Mandatory)][int]$Axis, [Parameter(Mandatory)][scriptblock]$Action,
        [int]$Above = -1, [int]$Hysteresis = -1, [int]$Hz = 100)
    $id = Get-PadForgeId $Device
    if ($Above -ge 0) {
        Add-PadForgeCallback "x|$id|$Axis" $Action
        $line = "sub $id axis=$Axis above=$Above"
        if ($Hysteresis -ge 0) { $line += " hyst=$Hysteresis" }
        Invoke-PadForgeRequest $line | Out-Null
    }
    else {
        Add-PadForgeCallback "a|$id|$Axis" $Action
        Invoke-PadForgeRequest "sub $id axes $Hz" | Out-Null
    }
}

function Register-PadForgeDeviceChange {
    <# Runs -Action { param($id, $online) } when a device connects or
       disconnects. #>
    param([Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback 'dev' $Action
    if (-not $script:Subscribed.ContainsKey('devices')) { Invoke-PadForgeRequest 'sub devices' | Out-Null; $script:Subscribed['devices'] = $true }
}

function Register-PadForgeProfile {
    <# Runs -Action { param($name) } when the profile changes. #>
    param([Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback 'profile' $Action
    if (-not $script:Subscribed.ContainsKey('profile')) { Invoke-PadForgeRequest 'sub profile' | Out-Null; $script:Subscribed['profile'] = $true }
}

function Register-PadForgeEngine {
    <# Runs -Action { param($state) } when the engine starts or stops. #>
    param([Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback 'engine' $Action
    if (-not $script:Subscribed.ContainsKey('engine')) { Invoke-PadForgeRequest 'sub engine' | Out-Null; $script:Subscribed['engine'] = $true }
}

function Register-PadForgeLevel {
    <# Runs -Action { param($level) } when the user changes Automation's
       level. #>
    param([Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback 'level' $Action
}

# ── Read ──

function Get-PadForgeDevice {
    <# Every device PadForge knows, connected or not. #>
    $answer = Invoke-PadForgeRequest 'devices'
    foreach ($row in $answer.Rows) {
        if ($row.Words.Length -eq 0 -or $row.Words[0] -ne 'device') { continue }
        $p = $row.Pairs
        [pscustomobject]@{
            Id = $p['id']; Type = $p['type']; Online = ($p['online'] -eq '1'); Name = $p['name']
            Buttons = [int]$p['buttons']; Axes = [int]$p['axes']; Sliders = [int]$p['sliders']; Hats = [int]$p['hats']
        }
    }
}

function Find-PadForgeDevice {
    <# The first connected device whose type is -NameOrType (gamepad,
       keyboard, mouse, wheel, program, ...) or whose name holds it. #>
    param([Parameter(Mandatory, Position = 0)][string]$NameOrType)
    $wanted = $NameOrType.ToLowerInvariant()
    Get-PadForgeDevice | Where-Object { $_.Online -and ($_.Type -eq $wanted -or $_.Id -eq $NameOrType -or $_.Name.ToLowerInvariant().Contains($wanted)) } | Select-Object -First 1
}

function Get-PadForgeCaps {
    param([Parameter(Mandatory)]$Device)
    (Invoke-PadForgeRequest ("caps " + (Get-PadForgeId $Device))).Rows | ForEach-Object {
        $o = [ordered]@{ Kind = $_.Words[0] }
        foreach ($k in $_.Pairs.Keys) { $o[$k] = $_.Pairs[$k] }
        [pscustomobject]$o
    }
}

function Read-PadForgeDevice {
    <# A device's whole state: Buttons (bools), Axes 0 to 31, Hats, Gyro,
       Accel and Battery. #>
    param([Parameter(Mandatory)]$Device)
    $p = (Invoke-PadForgeRequest ("read " + (Get-PadForgeId $Device))).Pairs
    $hex = $p['b']
    $buttons = New-Object bool[] ($hex.Length * 4)
    for ($i = 0; $i -lt $hex.Length / 2; $i++) {
        $byte = [Convert]::ToInt32($hex.Substring($i * 2, 2), 16)
        for ($bit = 0; $bit -lt 8; $bit++) { $buttons[$i * 8 + $bit] = (($byte -shr $bit) -band 1) -eq 1 }
    }
    $battery = $p['bat'].Split(',')
    [pscustomobject]@{
        Buttons = $buttons
        Axes = @($p['a'].Split(',') | ForEach-Object { [int]$_ })
        Hats = @($p['h'].Split(',') | ForEach-Object { [int]$_ })
        Gyro = @($p['gyro'].Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        Accel = @($p['accel'].Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        Battery = [int]$battery[0]
        Charging = ($battery[1] -eq '1')
    }
}

function Get-PadForgeOutputs {
    <# What virtual controller -Number sends to the game now. #>
    param([Parameter(Mandatory)][int]$Number)
    (Invoke-PadForgeRequest "outputs $Number").Pairs
}

# ── Send Inputs ──

function New-PadForgeController {
    <# Adds a controller of the script's own. -Key keeps its identity, and
       its mappings, across runs. -Buttons, -Axes, -Sliders and -Hats take
       names, or hashtables with index, name and rest. -Feedback takes any of
       rumble, trigrumble, light and player. The controller has the methods
       Press, Release, Set and Send. #>
    param([Parameter(Mandatory)][string]$Key, [Parameter(Mandatory)][string]$Name,
        [object[]]$Buttons = @(), [object[]]$Axes = @(), [object[]]$Sliders = @(), [object[]]$Hats = @(),
        [switch]$Gyro, [switch]$Accel, [int[]]$Touchpads = @(), [switch]$Battery, [string[]]$Feedback = @())
    $shape = [ordered]@{
        buttons = @($Buttons); axes = @($Axes); sliders = @($Sliders); hats = @($Hats)
        gyro = [bool]$Gyro; accel = [bool]$Accel; touchpads = @($Touchpads); battery = [bool]$Battery; feedback = @($Feedback)
    }
    $json = ConvertTo-Json $shape -Compress -Depth 4
    $answer = Invoke-PadForgeRequest ("declare key=$Key name=" + (ConvertTo-PadForgeQuoted $Name) + " " + $json)
    $controller = [pscustomobject]@{ Id = $answer.Pairs['id']; Staged = [ordered]@{} }
    $controller | Add-Member -MemberType ScriptMethod -Name Press -Value { param([int]$b) Send-PadForgeControl $this @{ "b$b" = 1 } }
    $controller | Add-Member -MemberType ScriptMethod -Name Release -Value { param([int]$b) Send-PadForgeControl $this @{ "b$b" = 0 } }
    $controller | Add-Member -MemberType ScriptMethod -Name Set -Value { param([string]$control, $value) $this.Staged[$control] = $value }
    $controller | Add-Member -MemberType ScriptMethod -Name Send -Value { $staged = $this.Staged; $this.Staged = [ordered]@{}; if ($staged.Count -gt 0) { Send-PadForgeControl $this $staged } }
    $controller
}

function Send-PadForgeControl {
    <# Sets several controls of a controller of the script's own as one
       change: @{ a0 = 40000; a1 = 32768; b3 = 1 }. #>
    param([Parameter(Mandatory, Position = 0)]$Controller, [Parameter(Mandatory, Position = 1)][System.Collections.IDictionary]$Controls)
    $parts = foreach ($k in $Controls.Keys) { "$k=$($Controls[$k])" }
    (Get-PadForgeConnection).Handle.Send(("s " + (Get-PadForgeId $Controller) + " " + ($parts -join ' ')))
}

function Remove-PadForgeController {
    param([Parameter(Mandatory)]$Controller)
    Invoke-PadForgeRequest ("gone " + (Get-PadForgeId $Controller)) | Out-Null
}

function Register-PadForgeRumble {
    <# Runs -Action { param($low, $high) } when a game rumbles a controller
       of the script's own. #>
    param([Parameter(Mandatory)]$Controller, [Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback ("rumble|" + (Get-PadForgeId $Controller)) $Action
}

function Register-PadForgeLight {
    <# Runs -Action { param($rrggbb) } when PadForge sets the controller's
       light. #>
    param([Parameter(Mandatory)]$Controller, [Parameter(Mandatory)][scriptblock]$Action)
    Add-PadForgeCallback ("led|" + (Get-PadForgeId $Controller)) $Action
}

function Set-PadForgeHold {
    <# Holds controls of any device at a value until released:
       Set-PadForgeHold $pad @{ b3 = 1; a0 = 40000 }. #>
    param([Parameter(Mandatory, Position = 0)]$Device, [Parameter(Mandatory, Position = 1)][System.Collections.IDictionary]$Controls)
    $parts = foreach ($k in $Controls.Keys) { "$k=$($Controls[$k])" }
    Invoke-PadForgeRequest ("own " + (Get-PadForgeId $Device) + " " + ($parts -join ' ')) | Out-Null
}

function Clear-PadForgeHold {
    <# Lets go of the named controls of a device, or all it holds there. #>
    param([Parameter(Mandatory, Position = 0)]$Device, [Parameter(Position = 1)][string[]]$Controls = @('all'))
    Invoke-PadForgeRequest ("rel " + (Get-PadForgeId $Device) + " " + ($Controls -join ' ')) | Out-Null
}

function Set-PadForgeValue {
    <# A named value Custom Expression macro triggers read. #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][double]$Value)
    Invoke-PadForgeRequest ("var $Name=" + $Value.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) | Out-Null
}

# ── Commands ──

function Switch-PadForgeProfile { param([Parameter(Mandatory)][string]$Name) (Invoke-PadForgeRequest ("activate " + (ConvertTo-PadForgeQuoted $Name))).Words }
function Reset-PadForgeProfile { (Invoke-PadForgeRequest 'deactivate').Words }
function Start-PadForgeEngine { Invoke-PadForgeRequest 'engine on' | Out-Null }
function Stop-PadForgeEngine { Invoke-PadForgeRequest 'engine off' | Out-Null }
function Enable-PadForgeController { param([int]$Number = 0) if ($Number -gt 0) { Invoke-PadForgeRequest "slot $Number on" | Out-Null } else { Invoke-PadForgeRequest 'controllers on' | Out-Null } }
function Disable-PadForgeController { param([int]$Number = 0) if ($Number -gt 0) { Invoke-PadForgeRequest "slot $Number off" | Out-Null } else { Invoke-PadForgeRequest 'controllers off' | Out-Null } }
function Show-PadForge { Invoke-PadForgeRequest 'show' | Out-Null }
function Hide-PadForge { Invoke-PadForgeRequest 'hide' | Out-Null }
function Save-PadForgeSnapshot { (Invoke-PadForgeRequest 'snapshot').Words }
function Get-PadForgeState { ConvertFrom-Json (Invoke-PadForgeRequest 'state').Json }

function Invoke-PadForgeTool {
    <# Runs an assistant tool, at the tool's own level. #>
    param([Parameter(Mandatory)][string]$Tool, [hashtable]$Arguments = @{})
    ConvertFrom-Json (Invoke-PadForgeRequest ("call $Tool " + (ConvertTo-Json $Arguments -Compress -Depth 8))).Json
}

Export-ModuleMember -Function Connect-PadForge, Disconnect-PadForge, Wait-PadForge, Invoke-PadForgeRequest,
    Register-PadForgeButton, Register-PadForgeHat, Register-PadForgeAxis, Register-PadForgeDeviceChange,
    Register-PadForgeProfile, Register-PadForgeEngine, Register-PadForgeLevel, Register-PadForgeRumble, Register-PadForgeLight,
    Get-PadForgeDevice, Find-PadForgeDevice, Get-PadForgeCaps, Read-PadForgeDevice, Get-PadForgeOutputs,
    New-PadForgeController, Send-PadForgeControl, Remove-PadForgeController, Set-PadForgeHold, Clear-PadForgeHold,
    Set-PadForgeValue, Switch-PadForgeProfile, Reset-PadForgeProfile, Start-PadForgeEngine, Stop-PadForgeEngine,
    Enable-PadForgeController, Disable-PadForgeController, Show-PadForge, Hide-PadForge, Save-PadForgeSnapshot,
    Get-PadForgeState, Invoke-PadForgeTool, Invoke-PadForgeEvent
