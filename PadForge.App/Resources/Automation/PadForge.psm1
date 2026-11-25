<#
PadForge Automation for PowerShell. Speaks protocol 1 of PadForge's
Automation pipe, PadForge.Automation, in Windows PowerShell 5.1 and in
PowerShell 7.

Install it from PadForge with Install on the PowerShell row of the
Scripts card (Extras, Automation), then a script anywhere starts with
Import-Module PadForge.
Turn on Automation at the level the script needs: Read to follow devices,
Send Inputs to hold controls, take them in hotkeys and add controllers.

Example:

    Import-Module PadForge
    $pad = Get-PadForgeDevice gamepad
    $pad.Hotkey('A', { $pad.Tap('B', 50) })               # A presses B for the game
    $pad.Hotkey('LeftX > 80%', { Write-Host 'right' })
    $pad.Hotkey('Back', { $pad.RightTrigger = 100 })      # holds it
    $pad.Hotkey('Back up', { $pad.RightTrigger = $null }) # gives it back
    Wait-PadForge

A device is an object found by its script ID, its type (gamepad, keyboard,
mouse, wheel, joystick, program...) or a part of its name. Its properties
are its controls by the names scripts use, the names its Script ID row on
PadForge's Devices page lists: A, B, LeftX, RightTrigger, DPad for a pad,
MouseX and MouseLeft for a mouse, KeyA for a keyboard, Axis0, Slider0,
Hat0 and Button0 for any device. Names are read without regard to case. By
number: $pad.Button[0], $pad.Axis[0], $pad.Slider[0], $pad.Hat[0]. A name
that is also a member's, such as Name, reads as $pad['Name'].

Values are plain units: a button is $true or $false, a stick runs from
-100 to 100 with 0 at rest, a trigger or slider from 0 to 100, a hat is
degrees clockwise from up or -1 at rest, and its directions read as HatUp,
HatRight, HatDown and HatLeft (DPadUp on a pad). LeftXPlus and LeftXMinus
are the two halves of an axis, 0 to 100 each. A read costs nothing: the
library keeps the device's whole state, which PadForge sends in full when
the script starts following it. A device that is not connected reads
$null and its Connected property is $false.

Setting a property holds the control at that value for the game:
$pad.A = $true. $null gives it back: $pad.A = $null, or $pad.Release('A').
$pad.Hold('LeftX', 50, 'LeftY', -20) holds several at once.
$pad.Set('LeftX', 40) puts a value once, until the device's own input
moves the control. $pad.Tap('A', 50) presses A for 50 ms and
$pad.Sequence('A', 50, 'B', 50) taps one after the other, timed on
PadForge's own clock, and a tap sent between two of PadForge's passes is
never lost.

Before the device is found, and while PadForge is not running, a property
set, Hold and Set wait under the control's name and apply when the device
is found, a Set once. A Tap or a Sequence throws then, since a tap sent
later is not the one the script asked for: "PadForge is not connected"
while PadForge does not run, and "the device is not connected" while it
runs and has no device that matches. PadForge refuses a Tap or a Sequence
on a device it has that is not connected, in the same words, and the
library throws its refusal. A Tap or a Sequence on the script's own
controller throws the same way until PadForge has added it.

A hat's direction is set as a button is: $pad.DPadUp = $true presses Up
and $false holds it released, beside the hat's other directions. The hat
itself, held or the device's own, is what the held directions go over, so
$pad.DPad = 'Down' with DPadRight held reads Down and Right. Up and Down
together cancel, as Left and Right do.

Hotkeys take AutoHotkey's forms: $pad.Hotkey('A', { ... }) takes A from the
game while the hotkey exists, '~A' leaves it to the game too, and 'A up',
'HatUp', 'LeftX > 80%', 'LeftX < -80%' and 'LeftX > 80% up' work. The
script block gets the hotkey's name. $pad.Hotkey('A', 'Off') removes it.
PadForge decides the claim inside its own pass, so the game never sees a
press the hotkey took. $pad.OnChange('RightX', { param($value, $name) })
follows a control, and $pad.OnConnect({ param($connected) }) the device.

A script block runs on the script's own thread, one at a time and in the
order the events came: while Wait-PadForge waits, and whenever the script
calls a command of this module or reads, sets or calls a member of a
device. A script with a loop of its own calls Wait-PadForge -Seconds 0.01
in it. What a script block outputs shows on the console.

The script and PadForge start in either order. While PadForge is not
running, (Get-PadForgeConnection).Why says so and the library keeps
trying, and when PadForge starts again it puts back what the script held,
claimed and followed. The library beats every 31 ms on a thread of its
own, so a script busy in Start-Sleep keeps what it holds. A script that
stops for 250 ms lets go of what it held and claimed until it beats again,
and its controllers go offline after 5 seconds of silence.

A plugin is a program PadForge starts itself (Extras, Plugins). It connects
with Connect-PadForgePlugin, which reads the one-time key and the pipe's
name PadForge put in its environment. A plugin declares, drives and removes
its own controllers whatever Automation's level, and writes to its log on
the Plugins page with Write-PadForgeLog.

Some online games' anti-cheat flags a player while a script drives the
game. Check a game's rules before a script drives it.
#>

$script:Source = @'
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForgeAutomation
{
    public sealed class LineEventArgs : EventArgs
    {
        public LineEventArgs(string line) { Line = line; }
        public string Line { get; private set; }
    }

    // One pipe to PadForge. It is opened for overlapped I/O, so the read
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
        private volatile bool _closed;
        private volatile bool _started;

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

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // A process's integrity level's RID, or -1.
        private static int Integrity(uint pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return -1;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, 8, out token)) return -1;
                try
                {
                    int size;
                    GetTokenInformation(token, 25, IntPtr.Zero, 0, out size);
                    if (size <= 0) return -1;
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        if (!GetTokenInformation(token, 25, buffer, size, out size)) return -1;
                        IntPtr sid = Marshal.ReadIntPtr(buffer);
                        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                        return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        // Whether PadForge runs: its window PadForge.Instance stands, made
        // by a process with administrator rights, which a program that made
        // one without them does not have.
        private static bool PadForgeRuns()
        {
            IntPtr after = IntPtr.Zero;
            for (int i = 0; i < 64; i++)
            {
                after = FindWindowExW(new IntPtr(-3), after, null, "PadForge.Instance");
                if (after == IntPtr.Zero) return false;
                uint pid;
                GetWindowThreadProcessId(after, out pid);
                if (Integrity(pid) >= 0x3000) return true;
            }
            return false;
        }

        // 0 the pipe stands, 1 nothing serves it, 2 PadForge runs with
        // Automation off.
        public static int Probe(string name, bool isTheAppsOwn)
        {
            if (WaitNamedPipeW(@"\\.\pipe\" + name, 1)) return 0;
            int error = Marshal.GetLastWin32Error();
            if (error != 2) return 0;
            if (isTheAppsOwn && PadForgeRuns()) return 2;
            return 1;
        }

        [DllImport("advapi32.dll")]
        private static extern int GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        // Opened at the identification level, so a server that is not
        // PadForge cannot act as this script's user. Nothing is read or
        // written before Start, which comes after the owner is checked.
        public Connection(string name, int timeoutMs)
        {
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            _pipe.Connect(timeoutMs);
        }

        // The SID of the pipe's owner, or null.
        public string Owner()
        {
            IntPtr owner, descriptor;
            if (GetSecurityInfo(_pipe.SafePipeHandle, 6, 1, out owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor) != 0)
                return null;
            try { return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner).Value; }
            finally { LocalFree(descriptor); }
        }

        // A PadForge pipe is owned by Administrators or the system, which a
        // program without administrator rights cannot give a pipe it made
        // under PadForge's name. A pipe of another name, which a test
        // serves, is taken from this script's own user too. Windows reads a
        // pipe's name without regard to case, so PadForge's own is told
        // apart the same way.
        public bool ServerIsPadForge(string name)
        {
            string owner = Owner();
            if (owner == "S-1-5-32-544" || owner == "S-1-5-18") return true;
            bool isTheAppsOwn = string.Equals(name, "PadForge.Automation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PadForge.Control", StringComparison.OrdinalIgnoreCase);
            if (isTheAppsOwn || owner == null) return false;
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                return me.User != null && owner == me.User.Value;
        }

        public void Start()
        {
            _started = true;
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
            string tag = "@" + Interlocked.Increment(ref _nextTag).ToString(CultureInfo.InvariantCulture);
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

        // Several requests written one after the other with no wait between
        // them, then each one's final line: null for one the closed pipe
        // ended.
        public string[] Requests(IList<string> lines, int timeoutMs)
        {
            List<KeyValuePair<string, Pending>> sent = new List<KeyValuePair<string, Pending>>();
            try
            {
                foreach (string line in lines)
                {
                    string tag = "@" + Interlocked.Increment(ref _nextTag).ToString(CultureInfo.InvariantCulture);
                    Pending pending = new Pending();
                    lock (_pendingLock) _pending[tag] = pending;
                    sent.Add(new KeyValuePair<string, Pending>(tag, pending));
                    Send(tag + " " + line);
                }
                string[] finals = new string[sent.Count];
                for (int i = 0; i < sent.Count; i++)
                {
                    if (!sent[i].Value.Done.WaitOne(timeoutMs)) throw new TimeoutException("PadForge did not answer.");
                    finals[i] = sent[i].Value.Final;
                }
                return finals;
            }
            finally
            {
                lock (_pendingLock)
                    foreach (KeyValuePair<string, Pending> s in sent) _pending.Remove(s.Key);
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

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            lock (_pendingLock)
            {
                foreach (Pending p in _pending.Values) p.Done.Set();
            }
            EventHandler handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // Says bye to a PadForge it spoke to, and closes the pipe. A pipe
        // whose owner was refused hears nothing.
        public void Dispose()
        {
            if (!_closed && _started)
            {
                try { Send("bye"); } catch (Exception) { }
            }
            Close();
            try { _pipe.Dispose(); } catch (Exception) { }
        }
    }

    // A line's words: the bare ones, the key=value pairs (read without
    // regard to case) and the JSON it ends with. Rows holds the rows of an
    // answer before its ok line.
    public sealed class Answer
    {
        public string[] Words { get; internal set; }
        public Hashtable Pairs { get; internal set; }
        public string Json { get; internal set; }
        public Answer[] Rows { get; internal set; }

        internal string Get(string key, string otherwise)
        {
            string value = Pairs[key] as string;
            return value ?? otherwise;
        }
    }

    internal static class Text
    {
        internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] Points = { "Up", "Up+Right", "Right", "Right+Down", "Down", "Down+Left", "Left", "Up+Left" };

        internal static object Plain(object value)
        {
            PSObject wrapped = value as PSObject;
            return wrapped != null ? wrapped.BaseObject : value;
        }

        internal static bool IsEmpty(object value)
        {
            value = Plain(value);
            string text = value as string;
            return value == null || (text != null && text.Length == 0);
        }

        internal static bool TryNumber(object value, out double number)
        {
            value = Plain(value);
            number = 0;
            if (value == null || value is bool || value is char) return false;
            string text = value as string;
            if (text != null) return double.TryParse(text.Trim(), NumberStyles.Float, Invariant, out number);
            IConvertible convertible = value as IConvertible;
            if (convertible == null) return false;
            try
            {
                number = convertible.ToDouble(Invariant);
                return true;
            }
            catch (FormatException) { return false; }
            catch (InvalidCastException) { return false; }
        }

        internal static double ToNumber(object value, string what)
        {
            value = Plain(value);
            if (value is bool) return (bool)value ? 1 : 0;
            double number;
            if (!TryNumber(value, out number))
                throw new ArgumentException(what + " takes a number, not " + (value == null ? "nothing" : "'" + value + "'") + ".");
            return number;
        }

        internal static string Number(double value)
        {
            string text = value.ToString("0.######", Invariant);
            return text == "-0" ? "0" : text;
        }

        internal static int Int(string text)
        {
            int value;
            int.TryParse(text, NumberStyles.Integer, Invariant, out value);
            return value;
        }

        internal static double Real(string text)
        {
            double value;
            double.TryParse(text, NumberStyles.Float, Invariant, out value);
            return value;
        }

        internal static bool IsOn(object value)
        {
            value = Plain(value);
            if (value is bool) return (bool)value;
            string text = value as string;
            if (text != null) return text.Trim() == "1" || string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            double number;
            return TryNumber(value, out number) && number != 0;
        }

        // A value as the pipe takes it for a control of a kind (b, a or h,
        // or d for a hat's direction, which takes a button's), in plain
        // units.
        internal static string Wire(char kind, object value)
        {
            value = Plain(value);
            if (kind == 'b' || kind == 'd') return IsOn(value) ? "1" : "0";
            string text = value as string;
            if (kind == 'a')
            {
                if (text != null && string.Equals(text.Trim(), "rest", StringComparison.OrdinalIgnoreCase)) return "rest";
                return Number(ToNumber(value, "An axis")) + "%";
            }
            double degrees;
            if (TryNumber(value, out degrees)) return degrees < 0 ? "-1" : Number(degrees) + "deg";
            text = value == null ? string.Empty : Convert.ToString(value, Invariant);
            return text.Length == 0 ? "-1" : text.Replace(" ", string.Empty);
        }

        internal static string Quote(object text)
        {
            string s = Convert.ToString(Plain(text), Invariant) ?? string.Empty;
            s = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
            return "\"" + s + "\"";
        }

        // The directions a hat in degrees holds: the nearest of the eight
        // points, a diagonal holding two, "" at rest.
        internal static string Directions(double degrees)
        {
            if (degrees < 0) return string.Empty;
            int point = (int)(Math.Round(degrees / 45, MidpointRounding.AwayFromZero) % 8);
            return Points[point];
        }

        internal static bool Holds(string directions, string direction)
        {
            return ("+" + directions + "+").IndexOf("+" + direction + "+", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The directions a hat value as the pipe takes it holds: "45deg",
        // "Up+Right", or "-1" at rest.
        internal static string HatWords(string wire)
        {
            if (string.IsNullOrEmpty(wire) || wire == "-1" || string.Equals(wire, "rest", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (Ends(wire, "deg")) return Directions(Real(wire.Substring(0, wire.Length - 3)));
            return wire;
        }

        // The degrees a hat that holds these directions reads, -1 for none.
        // Up and Down together cancel, as Left and Right do.
        internal static double DirectionDegrees(string words)
        {
            bool up = Holds(words, "Up"), down = Holds(words, "Down"), right = Holds(words, "Right"), left = Holds(words, "Left");
            if (up && down) up = down = false;
            if (right && left) right = left = false;
            if (up) return right ? 45 : left ? 315 : 0;
            if (down) return right ? 135 : left ? 225 : 180;
            return right ? 90 : left ? 270 : -1;
        }

        // words with one direction pressed or released.
        internal static string Turn(string words, string bit, bool pressed)
        {
            List<string> held = new List<string>();
            foreach (string w in (words ?? string.Empty).Split('+'))
                if (w.Trim().Length > 0 && !string.Equals(w.Trim(), bit, StringComparison.OrdinalIgnoreCase)) held.Add(w.Trim());
            if (pressed) held.Add(bit);
            return string.Join("+", held);
        }

        internal static bool Ends(string text, string end)
        {
            return text.Length >= end.Length && text.EndsWith(end, StringComparison.OrdinalIgnoreCase);
        }

        internal static Answer Split(string line)
        {
            List<string> words = new List<string>();
            Hashtable pairs = new Hashtable(StringComparer.OrdinalIgnoreCase);
            string json = string.Empty;
            int i = 0, n = line.Length;
            while (i < n)
            {
                while (i < n && line[i] == ' ') i++;
                if (i >= n) break;
                if (line[i] == '{')
                {
                    json = line.Substring(i);
                    break;
                }
                string key = null;
                StringBuilder value = new StringBuilder();
                bool quoted = false;
                while (i < n && (quoted || line[i] != ' '))
                {
                    char c = line[i];
                    if (quoted)
                    {
                        if (c == '\\' && i + 1 < n && (line[i + 1] == '"' || line[i + 1] == '\\'))
                        {
                            value.Append(line[i + 1]);
                            i += 2;
                            continue;
                        }
                        if (c == '"') quoted = false;
                        else value.Append(c);
                    }
                    else if (c == '"') quoted = true;
                    else if (c == '=' && key == null)
                    {
                        key = value.ToString();
                        value.Length = 0;
                    }
                    else value.Append(c);
                    i++;
                }
                if (key == null) words.Add(value.ToString());
                else pairs[key] = value.ToString();
            }
            Answer answer = new Answer();
            answer.Words = words.ToArray();
            answer.Pairs = pairs;
            answer.Json = json;
            answer.Rows = new Answer[0];
            return answer;
        }

        internal static string Json(object value)
        {
            value = Plain(value);
            if (value == null) return "null";
            string text = value as string;
            if (text != null) return JsonText(text);
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is char) return JsonText(value.ToString());
            IDictionary map = value as IDictionary;
            if (map != null)
            {
                List<string> parts = new List<string>();
                foreach (DictionaryEntry entry in map)
                    parts.Add(JsonText(Convert.ToString(Plain(entry.Key), Invariant)) + ":" + Json(entry.Value));
                return "{" + string.Join(",", parts) + "}";
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                List<string> parts = new List<string>();
                foreach (object item in list) parts.Add(Json(item));
                return "[" + string.Join(",", parts) + "]";
            }
            double number;
            if (TryNumber(value, out number)) return Number(number);
            return JsonText(Convert.ToString(value, Invariant));
        }

        private static string JsonText(string text)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in text)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // A list given in any form: nothing, one item or many.
        internal static List<object> Items(object list)
        {
            List<object> items = new List<object>();
            list = Plain(list);
            if (list == null) return items;
            if (list is string || list is IDictionary)
            {
                items.Add(list);
                return items;
            }
            IEnumerable many = list as IEnumerable;
            if (many == null)
            {
                items.Add(list);
                return items;
            }
            foreach (object item in many) items.Add(Plain(item));
            return items;
        }
    }

    // A control of a device: its name, its kind (b a button, a an axis or a
    // slider, h a hat), its index on the pipe (sliders from 24) and whether
    // an axis is a stick.
    internal sealed class Control
    {
        internal readonly string Name;
        internal readonly char Kind;
        internal readonly int Index;
        internal readonly bool Stick;

        internal Control(string name, char kind, int index, bool stick)
        {
            Name = name;
            Kind = kind;
            Index = index;
            Stick = stick;
        }
    }

    public sealed class ControlInfo
    {
        internal ControlInfo(Control c)
        {
            Name = c.Name;
            Kind = c.Kind == 'b' ? "button" : c.Kind == 'h' ? "hat" : c.Index >= 24 ? "slider" : "axis";
            Index = c.Index;
        }

        public string Name { get; private set; }
        public string Kind { get; private set; }
        public int Index { get; private set; }
        public override string ToString() { return Name; }
    }

    // A hotkey's words: its name, whether it leaves the control to the
    // game, on release, and what it follows.
    internal sealed class HotkeyItem
    {
        internal string Name;
        internal bool Pass;
        internal bool Up;
        internal char Kind;
        internal string What;
        internal string Line = string.Empty;
        internal string Key;
        internal int Hat;
        internal string Bit;
        internal object Callback;
    }

    // What the hotkeys of one control, direction or line share: the claim
    // and the line's number.
    internal sealed class HotkeyKey
    {
        internal string What;
        internal char Kind;
        internal string Line;
        internal int Number;
        internal int Claimed = -1;
        internal readonly List<HotkeyItem> List = new List<HotkeyItem>();
    }

    internal sealed class Call
    {
        internal readonly object Callback;
        internal readonly object[] Args;

        internal Call(object callback, object[] args)
        {
            Callback = callback;
            Args = args;
        }
    }

    // $pad.Button[0] and the like: a control by its number.
    public sealed class NumberedControls : DynamicObject
    {
        private readonly Device _device;
        private readonly string _stem;

        internal NumberedControls(Device device, string stem)
        {
            _device = device;
            _stem = stem;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            result = _device.Get(_stem + Number(indexes));
            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object value)
        {
            _device.Put(_stem + Number(indexes), value);
            return true;
        }

        private static string Number(object[] indexes)
        {
            if (indexes.Length != 1) throw new ArgumentException("A control's number is one whole number.");
            return ((int)Text.ToNumber(indexes[0], "A control's number")).ToString(Text.Invariant);
        }
    }

    // A device PadForge reads, found by Get-PadForgeDevice. Its properties
    // are its controls (see the top of this file). Everything it keeps is
    // read and changed under its connection's Sync.
    public class Device : DynamicObject
    {
        internal readonly Client _pf;
        internal readonly string _what;
        internal volatile string _id = string.Empty;
        internal volatile string _name = string.Empty;
        internal volatile string _type = string.Empty;
        internal volatile bool _online;
        internal volatile bool _followed;
        internal volatile bool _connected;
        internal readonly Dictionary<string, Control> _controls = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, Control> _byIndex = new Dictionary<string, Control>(StringComparer.Ordinal);
        internal readonly Dictionary<string, object> _values = new Dictionary<string, object>(StringComparer.Ordinal);
        internal readonly Dictionary<string, object> _holds = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // Sets made before the device was found, put once when it is.
        internal readonly Dictionary<string, object> _sets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, HotkeyKey> _keys = new Dictionary<string, HotkeyKey>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<int, HotkeyKey> _lines = new Dictionary<int, HotkeyKey>();
        internal readonly Dictionary<string, List<object>> _changes = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<object> _onConnect = new List<object>();
        internal readonly Dictionary<int, double> _hats = new Dictionary<int, double>();

        private static readonly string[] Stems = { "Button", "Axis", "Slider", "Hat" };
        private static readonly string[] Ends = { "Up", "Right", "Down", "Left" };
        private static readonly Regex Numbered = new Regex("^(Button|Axis|Slider|Hat)([0-9]+)$", RegexOptions.IgnoreCase);
        private static readonly Regex UpOrDown = new Regex("\\s+(up|down)$", RegexOptions.IgnoreCase);
        private static readonly Regex Form = new Regex("^([A-Za-z0-9_]+)\\s*(?:([<>])\\s*(-?[0-9]+(?:\\.[0-9]+)?)\\s*%?)?$");

        internal Device(Client pf, string what)
        {
            _pf = pf;
            _what = what;
        }

        // Its script ID, "" until it is found.
        public string Id { get { return _id; } }
        public virtual string Name { get { return _name; } }
        public virtual string Type { get { return _type; } }
        public virtual bool Connected { get { return _followed ? _connected : _online; } }

        // Its controls: each name to an object with Name, Kind (button,
        // axis, slider or hat) and Index.
        public Hashtable Controls
        {
            get
            {
                lock (_pf.Sync)
                {
                    if (_controls.Count == 0 && _id.Length > 0 && !(this is Controller) && _pf.Connected)
                    {
                        try { Caps(); }
                        catch (Exception) { }
                    }
                    Hashtable all = new Hashtable(StringComparer.OrdinalIgnoreCase);
                    foreach (Control c in _controls.Values) all[c.Name] = new ControlInfo(c);
                    return all;
                }
            }
        }

        public override string ToString() { return _name.Length > 0 ? _name : _what; }

        // ── Members by name ──

        public override IEnumerable<string> GetDynamicMemberNames()
        {
            lock (_pf.Sync) return _controls.Values.Select(c => c.Name).ToList();
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            foreach (string stem in Stems)
                if (string.Equals(binder.Name, stem, StringComparison.OrdinalIgnoreCase))
                {
                    result = new NumberedControls(this, stem);
                    return true;
                }
            result = Get(binder.Name);
            return true;
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            Put(binder.Name, value);
            return true;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            result = Get(IndexName(indexes));
            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object value)
        {
            Put(IndexName(indexes), value);
            return true;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            _pf.Pump();
            object[] a = new object[args.Length];
            for (int i = 0; i < args.Length; i++) a[i] = Text.Plain(args[i]);
            bool known;
            lock (_pf.Sync) result = Invoke(binder.Name.ToLowerInvariant(), a, out known);
            return known;
        }

        private static string IndexName(object[] indexes)
        {
            if (indexes.Length != 1) throw new ArgumentException("Name one control: $pad['A'].");
            return Word(indexes[0]);
        }

        internal object Get(string name)
        {
            _pf.Pump();
            lock (_pf.Sync) return Read(name);
        }

        internal void Put(string name, object value)
        {
            lock (_pf.Sync) Hold(name, Text.Plain(value));
            _pf.Pump();
        }

        internal virtual object Invoke(string method, object[] a, out bool known)
        {
            known = true;
            switch (method)
            {
                case "hold": Hold(a); return null;
                case "release": Release(a); return null;
                case "set":
                    Need(a, 2, "Set(name, value)");
                    Set(Word(a[0]), a[1]);
                    return null;
                case "tap":
                    Need(a, 1, "Tap(name, ms, value)");
                    Tap(Word(a[0]), a.Length > 1 ? Ms(a[1]) : 50, a.Length > 2 ? a[2] : null);
                    return null;
                case "sequence": Sequence(a); return null;
                case "hotkey":
                    Need(a, 1, "Hotkey(keyName, scriptBlock)");
                    Hotkey(Word(a[0]), a.Length > 1 ? a[1] : null);
                    return null;
                case "onchange":
                    Need(a, 2, "OnChange(name, scriptBlock)");
                    OnChange(Word(a[0]), a[1]);
                    return null;
                case "onconnect":
                    Need(a, 1, "OnConnect(scriptBlock)");
                    OnConnect(a[0]);
                    return null;
            }
            known = false;
            return null;
        }

        internal static void Need(object[] a, int count, string form)
        {
            if (a.Length < count) throw new ArgumentException("Call it as " + form + ".");
        }

        internal static string Word(object value)
        {
            value = Text.Plain(value);
            if (value == null) throw new ArgumentException("A control's name is missing.");
            return Convert.ToString(value, Text.Invariant);
        }

        internal static int Ms(object value)
        {
            return (int)Math.Round(Text.ToNumber(value, "A time in milliseconds"));
        }

        // Name, value, name, value: from a list, or from one hashtable.
        internal static List<KeyValuePair<string, object>> Pairs(object[] a)
        {
            List<KeyValuePair<string, object>> pairs = new List<KeyValuePair<string, object>>();
            IDictionary map = a.Length == 1 ? a[0] as IDictionary : null;
            if (map != null)
            {
                foreach (DictionaryEntry entry in map) pairs.Add(new KeyValuePair<string, object>(Word(entry.Key), Text.Plain(entry.Value)));
                return pairs;
            }
            for (int i = 0; i + 1 < a.Length; i += 2) pairs.Add(new KeyValuePair<string, object>(Word(a[i]), a[i + 1]));
            return pairs;
        }

        // ── Finding it ──

        internal void Locate()
        {
            Answer[] rows;
            try { rows = _pf.Ask("devices").Rows; }
            catch (Exception) { return; }
            string want = _what.ToLowerInvariant();
            Answer pick = null, spare = null;
            foreach (Answer row in rows)
            {
                if (row.Words.Length == 0 || row.Words[0] != "device") continue;
                string id = row.Get("id", string.Empty);
                bool hit = id == _what || row.Get("type", string.Empty).ToLowerInvariant() == want
                    || row.Get("name", string.Empty).ToLowerInvariant().Contains(want);
                if (!hit) continue;
                if (row.Get("online", "0") == "1")
                {
                    pick = row;
                    break;
                }
                if (spare == null) spare = row;
            }
            if (pick == null) pick = spare;
            if (pick == null) return;
            Row(pick.Get("id", string.Empty), pick.Get("name", string.Empty), pick.Get("type", string.Empty), pick.Get("online", "0") == "1");
            Attach();
        }

        internal void Row(string id, string name, string type, bool online)
        {
            Device mapped;
            if (_id.Length > 0 && _id != id && _pf.ById.TryGetValue(_id, out mapped) && mapped == this) _pf.ById.Remove(_id);
            _id = id;
            _name = name;
            _type = type;
            _online = online;
            _pf.ById[id] = this;
        }

        // Reads its controls, follows its whole state, and puts back what
        // the script holds and claims on it.
        private void Attach()
        {
            Caps();
            _pf.Ask("sub " + _id + " state");
            _followed = true;
            // Hotkeys and holds made before the controls were known are
            // read again by the device's own names.
            List<HotkeyItem> items = new List<HotkeyItem>();
            foreach (HotkeyKey key in _keys.Values) items.AddRange(key.List);
            _keys.Clear();
            _lines.Clear();
            foreach (HotkeyItem item in items)
            {
                try { Hotkey(item.Name, item.Callback); }
                catch (Exception e) { _pf.Note(e.Message); }
            }
            StringBuilder line = new StringBuilder();
            List<KeyValuePair<string, object>> held = new List<KeyValuePair<string, object>>();
            foreach (KeyValuePair<string, object> hold in _holds)
            {
                Control c = Target(hold.Key);
                if (c == null)
                {
                    _pf.Note("No such control: " + hold.Key);
                    continue;
                }
                held.Add(new KeyValuePair<string, object>(c.Name, hold.Value));
                line.Append(' ').Append(c.Name).Append('=').Append(Text.Wire(c.Kind, hold.Value));
            }
            _holds.Clear();
            foreach (KeyValuePair<string, object> hold in held) _holds[hold.Key] = hold.Value;
            if (line.Length > 0) _pf.Send("own " + _id + line);
            // Sets made before the device was found are put once now. One
            // whose name or value the device does not take is noted and
            // goes.
            StringBuilder sets = new StringBuilder();
            foreach (KeyValuePair<string, object> set in _sets)
            {
                Control c = Target(set.Key);
                if (c == null)
                {
                    _pf.Note("No such control: " + set.Key);
                    continue;
                }
                string wire;
                try { wire = Text.Wire(c.Kind, set.Value); }
                catch (Exception e)
                {
                    _pf.Note(e.Message);
                    continue;
                }
                sets.Append(' ').Append(c.Name).Append('=').Append(wire);
            }
            _sets.Clear();
            if (sets.Length > 0) _pf.Send("set " + _id + sets);
        }

        // A device Devices() listed follows itself on its first use of any
        // kind, a read, a hotkey, a change callback, a hold, Set, Tap or
        // Sequence, as one Device() found does: its controls are read, its
        // state and connection stay current, and PadForge finds it again
        // after a restart. A device already wanted is found again by
        // PadForge's restore and its device events.
        internal void FollowSelf()
        {
            if (_followed || _id.Length == 0 || this is Controller) return;
            string key = _what.ToLowerInvariant();
            if (_pf.Wanting.ContainsKey(key)) return;
            _pf.Wanting[key] = this;
            if (!_pf.Connected) return;
            try { Attach(); }
            catch (Exception e) { _pf.Note(e.Message); }
        }

        internal void Caps()
        {
            Answer[] rows = _pf.Ask("caps " + _id).Rows;
            _controls.Clear();
            _byIndex.Clear();
            foreach (Answer row in rows)
            {
                if (row.Words.Length == 0) continue;
                string kind = row.Words[0];
                if (kind != "button" && kind != "axis" && kind != "hat") continue;
                char k = kind[0];
                int index = Text.Int(row.Get("index", "0"));
                string name = row.Get("script", null)
                    ?? (k == 'b' ? "Button" + index : k == 'h' ? "Hat" + index : index >= 24 ? "Slider" + (index - 24) : "Axis" + index);
                string restText = row.Get("rest", null);
                int rest = restText != null ? Text.Int(restText) : (index < 24 ? 32768 : 0);
                Control c = new Control(name, k, index, rest == 32768 || rest == 32767);
                _controls[name] = c;
                _byIndex[k.ToString() + index] = c;
            }
        }

        internal void Lost()
        {
            // PadForge closed the pipe: the device is followed again, and
            // what the script holds put back, when PadForge has it again,
            // at once or when it connects after.
            _followed = false;
            _online = false;
            if (_connected)
            {
                _connected = false;
                Changed(string.Empty, false);
            }
        }

        // ── The copy ──

        // An event into the copy. False for a change the copy holds
        // already, as a second subscription's copy of it.
        internal bool Take(string[] w, string line)
        {
            if (w.Length < 4) return true;
            switch (w[1])
            {
                case "b":
                {
                    if (w.Length < 5) return true;
                    string key = "b" + Text.Int(w[3]);
                    bool down = w[4] == "1";
                    object old;
                    if (_values.TryGetValue(key, out old) && old is bool && (bool)old == down) return false;
                    _values[key] = down;
                    break;
                }
                case "h":
                case "a":
                {
                    if (w.Length < 5) return true;
                    string key = w[1] + Text.Int(w[3]);
                    double value = Text.Real(w[4]);
                    object old;
                    if (_values.TryGetValue(key, out old) && old is double && (double)old == value) return false;
                    _values[key] = value;
                    break;
                }
                case "state":
                {
                    bool was = _connected;
                    if (w[3] == "offline")
                    {
                        _connected = false;
                        return was;
                    }
                    State(Text.Split(line.Substring(("e state " + w[2] + " ").Length)).Pairs);
                    _connected = true;
                    return !was;
                }
            }
            return true;
        }

        private void State(Hashtable p)
        {
            string hex = p["b"] as string ?? string.Empty;
            for (int i = 0; i + 1 < hex.Length; i += 2)
            {
                int value;
                if (!int.TryParse(hex.Substring(i, 2), NumberStyles.HexNumber, Text.Invariant, out value)) continue;
                for (int bit = 0; bit < 8; bit++) _values["b" + (i / 2 * 8 + bit)] = ((value >> bit) & 1) == 1;
            }
            string axes = p["a"] as string;
            if (!string.IsNullOrEmpty(axes))
            {
                string[] list = axes.Split(',');
                for (int i = 0; i < list.Length; i++) _values["a" + i] = Text.Real(list[i]);
            }
            string hats = p["h"] as string;
            if (!string.IsNullOrEmpty(hats))
            {
                string[] list = hats.Split(',');
                for (int i = 0; i < list.Length; i++)
                {
                    double deg = Text.Real(list[i]);
                    _values["h" + i] = deg;
                    _hats[i] = deg;
                }
            }
        }

        // The control a name names, or null for none.
        internal Control Lookup(string name)
        {
            Control c;
            if (_controls.TryGetValue(name, out c)) return c;
            Match m = Numbered.Match(name);
            if (!m.Success) return null;
            string stem = null;
            foreach (string s in Stems)
                if (string.Equals(m.Groups[1].Value, s, StringComparison.OrdinalIgnoreCase)) stem = s;
            int index;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, Text.Invariant, out index)) return null;
            char k = stem == "Button" ? 'b' : stem == "Hat" ? 'h' : 'a';
            if (stem == "Slider") index += 24;
            if (_byIndex.TryGetValue(k.ToString() + index, out c)) return c;
            return new Control(stem + m.Groups[2].Value, k, index, stem == "Axis");
        }

        // What a setting names: a control, or one direction of a hat, kind
        // d, named by its hat's name and set as a button is. Null for
        // neither.
        internal Control Target(string name)
        {
            Control c = Lookup(name);
            if (c != null) return c;
            HotkeyItem d = Direction(name);
            if (d == null) return null;
            Control hat;
            string hatName = _byIndex.TryGetValue("h" + d.Hat, out hat) ? hat.Name : "Hat" + d.Hat;
            return new Control(hatName + d.Bit, 'd', d.Hat * 4 + Array.IndexOf(Ends, d.Bit), false);
        }

        // A hat direction a name names: the hat's index and the direction,
        // or null.
        internal HotkeyItem Direction(string name)
        {
            foreach (string end in Ends)
            {
                if (name.Length <= end.Length || !Text.Ends(name, end)) continue;
                string head = name.Substring(0, name.Length - end.Length);
                HotkeyItem d = new HotkeyItem();
                d.Name = name;
                d.Bit = end;
                if (string.Equals(head, "Hat", StringComparison.OrdinalIgnoreCase))
                {
                    d.Hat = 0;
                    return d;
                }
                Control c = Lookup(head);
                if (c != null && c.Kind == 'h')
                {
                    d.Hat = c.Index;
                    return d;
                }
            }
            return null;
        }

        internal virtual object Read(string name)
        {
            FollowSelf();
            bool plus = Text.Ends(name, "Plus"), minus = Text.Ends(name, "Minus");
            if (plus || minus)
            {
                Control half = Lookup(name.Substring(0, name.Length - (minus ? 5 : 4)));
                if (half != null && half.Kind == 'a')
                {
                    object v = Value(half);
                    if (v == null) return null;
                    double d = (double)v;
                    double part = half.Stick ? (minus ? -d : d) : (minus ? (50 - d) * 2 : (d - 50) * 2);
                    return Math.Max(0, part);
                }
            }
            Control c = Lookup(name);
            if (c != null) return Value(c);
            HotkeyItem direction = Direction(name);
            if (direction != null)
            {
                if (!_connected) return null;
                object hat;
                double deg = _values.TryGetValue("h" + direction.Hat, out hat) ? (double)hat : -1;
                return Text.Holds(Text.Directions(deg), direction.Bit);
            }
            // Not found yet: its controls are not known, and it reads
            // nothing.
            if (_controls.Count == 0) return null;
            throw new ArgumentException("No such control: " + name);
        }

        private object Value(Control c)
        {
            if (!_connected) return null;
            object value;
            if (_values.TryGetValue(c.Kind.ToString() + c.Index, out value)) return value;
            if (c.Kind == 'b') return false;
            return c.Kind == 'h' ? -1.0 : 0.0;
        }

        // ── Holding ──

        // Sets a property: holds the control at the value, or gives it back
        // for $null or "". A hat's direction is held as a button is, over
        // the hat's other directions: the hat itself, held or the device's
        // own, with each held direction pressed or released over it. Before
        // the device is found its controls are not known yet: the hold
        // waits under the name given.
        internal virtual void Hold(string name, object value)
        {
            FollowSelf();
            Control c = Target(name);
            if (c == null && _controls.Count > 0) throw new ArgumentException("No such control: " + name);
            string key = c == null ? name : c.Name;
            // A hold or a give-back takes the place of a Set that waits.
            _sets.Remove(key);
            if (Text.IsEmpty(value))
            {
                _holds.Remove(key);
                if (_id.Length > 0 && c != null) _pf.Send("rel " + _id + " " + key);
                return;
            }
            _holds[key] = value;
            if (_id.Length > 0 && c != null) _pf.Send("own " + _id + " " + key + "=" + Text.Wire(c.Kind, value));
        }

        // Holds several controls in one change: Hold('LeftX', 50, 'LeftY', -20).
        // $null gives a control back, as setting it to $null does, and
        // before the device is found each waits under the name given, as
        // Set does.
        internal virtual void Hold(object[] a)
        {
            FollowSelf();
            StringBuilder line = new StringBuilder(), back = new StringBuilder();
            foreach (KeyValuePair<string, object> pair in Pairs(a))
            {
                Control c = Target(pair.Key);
                if (c == null && _controls.Count > 0) throw new ArgumentException("No such control: " + pair.Key);
                string key = c == null ? pair.Key : c.Name;
                _sets.Remove(key);
                if (Text.IsEmpty(pair.Value))
                {
                    _holds.Remove(key);
                    if (c != null) back.Append(' ').Append(key);
                    continue;
                }
                _holds[key] = pair.Value;
                if (c != null) line.Append(' ').Append(key).Append('=').Append(Text.Wire(c.Kind, pair.Value));
            }
            if (_id.Length > 0 && line.Length > 0) _pf.Send("own " + _id + line);
            if (_id.Length > 0 && back.Length > 0) _pf.Send("rel " + _id + back);
        }

        // Gives controls back: Release('A', 'LeftX'), or all of them.
        internal virtual void Release(object[] names)
        {
            FollowSelf();
            if (names.Length == 0)
            {
                _holds.Clear();
                _sets.Clear();
                if (_id.Length > 0) _pf.Send("rel " + _id + " all");
                return;
            }
            StringBuilder line = new StringBuilder();
            foreach (object name in names)
            {
                // Before the device is found its controls are not known: a
                // hold or a Set that waits under the name given goes.
                Control c = Target(Word(name));
                if (c == null && _controls.Count > 0) throw new ArgumentException("No such control: " + Word(name));
                string key = c == null ? Word(name) : c.Name;
                _holds.Remove(key);
                _sets.Remove(key);
                if (c != null) line.Append(' ').Append(key);
            }
            if (_id.Length > 0 && line.Length > 0) _pf.Send("rel " + _id + line);
        }

        // Puts a value once, until the device's own input moves the
        // control. Before the device is found its controls are not known
        // yet: the value waits under the name given, and is put when the
        // device is found. While PadForge is not connected it waits the same
        // way, as a hold does, and is put when the device is found again.
        internal virtual void Set(string name, object value)
        {
            FollowSelf();
            Control c = Target(name);
            if (c == null)
            {
                if (_controls.Count > 0) throw new ArgumentException("No such control: " + name);
                _sets[name] = value;
                return;
            }
            if (_id.Length == 0 || !_pf.Send("set " + _id + " " + c.Name + "=" + Text.Wire(c.Kind, value))) _sets[c.Name] = value;
        }

        // The refusal of a timed action before the device is found: a tap
        // sent later, at a moment the script did not choose, is not the tap
        // it asked for. PadForge's own error while PadForge is not
        // connected, else a read's of a device that is not connected.
        internal Exception Absent(string verb)
        {
            if (!_pf.Connected) return new InvalidOperationException("PadForge is not connected: " + _pf.Why);
            return new InvalidOperationException("PadForge refused " + verb + ": the device is not connected (offline).");
        }

        // Presses a control for ms milliseconds on PadForge's clock: a
        // button or a hat's direction, or an axis to value (100 by default)
        // and back. Refused before the device is found, and by PadForge
        // while the device is not connected.
        internal virtual void Tap(string name, int ms, object value)
        {
            if (_id.Length == 0) throw Absent("tap");
            FollowSelf();
            Control c = Target(name);
            // Controls not read yet: PadForge is not connected, or has not
            // given them.
            if (c == null && _controls.Count == 0) throw Absent("tap");
            if (c == null) throw new ArgumentException("No such control: " + name);
            if (Text.IsEmpty(value)) value = c.Kind == 'b' || c.Kind == 'd' ? (object)true : c.Kind == 'h' ? 0.0 : 100.0;
            _pf.Timed("tap", new[]
            {
                "own " + _id + " " + c.Name + "=" + Text.Wire(c.Kind, value) + " in=0",
                "rel " + _id + " " + c.Name + " in=" + ms.ToString(Text.Invariant),
            });
        }

        // Taps controls one after the other, each held its ms:
        // Sequence('A', 50, 'B', 50). Each starts as the one before lets go.
        // Refused before the device is found, and by PadForge while the
        // device is not connected.
        internal virtual void Sequence(object[] steps)
        {
            if (_id.Length == 0) throw Absent("sequence");
            FollowSelf();
            if (_controls.Count == 0)
                for (int i = 0; i + 1 < steps.Length; i += 2)
                    if (Target(Word(steps[i])) == null) throw Absent("sequence");
            List<string> lines = new List<string>();
            foreach (KeyValuePair<Control, int> step in Steps(steps))
            {
                Control c = step.Key;
                string on = c.Kind == 'b' || c.Kind == 'd' ? "1" : c.Kind == 'h' ? "0deg" : "100%";
                lines.Add("own " + _id + " " + c.Name + "=" + on + (lines.Count == 0 ? " in=0" : " after=0"));
                lines.Add("rel " + _id + " " + c.Name + " after=" + step.Value.ToString(Text.Invariant));
            }
            if (lines.Count > 0) _pf.Timed("sequence", lines);
        }

        internal List<KeyValuePair<Control, int>> Steps(object[] steps)
        {
            List<KeyValuePair<Control, int>> list = new List<KeyValuePair<Control, int>>();
            for (int i = 0; i + 1 < steps.Length; i += 2)
            {
                Control c = Target(Word(steps[i]));
                if (c == null) throw new ArgumentException("No such control: " + Word(steps[i]));
                list.Add(new KeyValuePair<Control, int>(c, Ms(steps[i + 1])));
            }
            return list;
        }

        // ── Hotkeys ──

        // Hotkey(keyName, scriptBlock): see the top of this file. 'Off' or
        // $null removes it.
        internal virtual void Hotkey(string keyName, object callback)
        {
            FollowSelf();
            HotkeyItem h = Parse(keyName);
            string id = h.Key;
            string word = callback as string;
            if (callback == null || (word != null && string.Equals(word, "Off", StringComparison.OrdinalIgnoreCase)))
            {
                HotkeyKey gone;
                if (_keys.TryGetValue(id, out gone))
                {
                    gone.List.RemoveAll(item => string.Equals(item.Name, h.Name, StringComparison.OrdinalIgnoreCase));
                    if (gone.List.Count == 0)
                    {
                        _keys.Remove(id);
                        Unclaim(gone);
                    }
                    else Claim(gone);
                }
                return;
            }
            h.Callback = Client.Callable(callback, "A hotkey needs a script block or 'Off': " + keyName);
            HotkeyKey key;
            if (!_keys.TryGetValue(id, out key))
            {
                key = new HotkeyKey();
                key.What = h.What;
                key.Kind = h.Kind;
                key.Line = h.Line;
                _keys[id] = key;
            }
            key.List.RemoveAll(item => string.Equals(item.Name, h.Name, StringComparison.OrdinalIgnoreCase));
            key.List.Add(h);
            Claim(key);
        }

        internal HotkeyItem Parse(string keyName)
        {
            string text = keyName.Trim();
            HotkeyItem h = new HotkeyItem();
            h.Name = keyName;
            h.Pass = text.StartsWith("~", StringComparison.Ordinal);
            if (h.Pass) text = text.Substring(1).TrimStart();
            Match up = UpOrDown.Match(text);
            if (up.Success)
            {
                h.Up = string.Equals(up.Groups[1].Value, "up", StringComparison.OrdinalIgnoreCase);
                text = text.Substring(0, up.Index).TrimEnd();
            }
            Match m = Form.Match(text);
            if (!m.Success) throw new ArgumentException("Not a control: " + keyName);
            string name = m.Groups[1].Value, op = m.Groups[2].Value;
            if (op.Length > 0)
            {
                string half = string.Empty, stem = name;
                if (Text.Ends(name, "Plus") && name.Length > 4)
                {
                    half = "Plus";
                    stem = name.Substring(0, name.Length - 4);
                }
                else if (Text.Ends(name, "Minus") && name.Length > 5)
                {
                    half = "Minus";
                    stem = name.Substring(0, name.Length - 5);
                }
                Control c = Lookup(half.Length == 0 ? name : stem);
                if (c == null && half.Length > 0)
                {
                    c = Lookup(name);
                    half = string.Empty;
                }
                if (c == null && _controls.Count > 0)
                    throw new ArgumentException((Direction(name) != null ? "Only an axis or a slider takes a limit: " : "No such control: ") + keyName);
                if (c != null && c.Kind != 'a')
                    throw new ArgumentException("Only an axis or a slider takes a limit: " + keyName);
                double limit = Text.Real(m.Groups[3].Value);
                bool low = op == "<";
                if (half.Length > 0)
                {
                    bool stick = c == null || c.Stick;
                    if (half == "Minus")
                    {
                        limit = stick ? -limit : 50 - limit / 2;
                        low = !low;
                    }
                    else limit = stick ? limit : 50 + limit / 2;
                }
                h.Kind = 'l';
                h.What = c == null ? name : c.Name;
                h.Line = (low ? "below=" : "above=") + Text.Number(limit) + "%";
                h.Key = h.What + " " + h.Line;
                return h;
            }
            HotkeyItem d = Direction(name);
            if (d != null)
            {
                h.Kind = 'd';
                h.What = d.Name;
                h.Hat = d.Hat;
                h.Bit = d.Bit;
                h.Key = d.Name;
                return h;
            }
            Control control = Lookup(name);
            if (control == null && _controls.Count > 0) throw new ArgumentException("No such control: " + keyName);
            if (control != null && control.Kind == 'a')
                throw new ArgumentException("An axis or slider hotkey needs a limit, such as LeftX > 80%: " + keyName);
            if (control != null && control.Kind == 'h')
                throw new ArgumentException("A hat's directions are its hotkeys, such as " + control.Name + "Up: " + keyName);
            h.Kind = 'b';
            h.What = control == null ? name : control.Name;
            h.Key = h.What;
            return h;
        }

        // Subscribes what a hotkey follows, claimed unless every hotkey on
        // it leaves it to the game. A line's number comes back in the
        // answer.
        private void Claim(HotkeyKey key)
        {
            bool claim = key.List.Any(item => !item.Pass);
            if (_id.Length == 0 || !_pf.Connected)
            {
                key.Claimed = -1;
                return;
            }
            if (key.Kind != 'l' && !claim)
            {
                // A button or direction the game keeps: the whole state's
                // subscription carries its events.
                if (key.Claimed == 1) _pf.Ask("unsub " + _id + " " + key.What);
                key.Claimed = 0;
                return;
            }
            if (key.Kind == 'l' && key.Claimed == (claim ? 1 : 0) && key.Number != 0) return;
            if (key.Kind == 'l' && key.Number != 0)
            {
                _lines.Remove(key.Number);
                _pf.Ask("unsub " + _id + " line=" + key.Number.ToString(Text.Invariant));
            }
            Answer answer = _pf.Ask("sub " + _id + " " + key.What + (key.Line.Length > 0 ? " " + key.Line : string.Empty) + (claim ? " claim" : string.Empty));
            key.Claimed = claim ? 1 : 0;
            if (key.Kind == 'l')
            {
                key.Number = Text.Int(answer.Get("line", "0"));
                _lines[key.Number] = key;
            }
        }

        private void Unclaim(HotkeyKey key)
        {
            if (_id.Length == 0 || !_pf.Connected) return;
            if (key.Kind == 'l' && key.Number != 0)
            {
                _lines.Remove(key.Number);
                _pf.Ask("unsub " + _id + " line=" + key.Number.ToString(Text.Invariant));
            }
            else if (key.Claimed == 1) _pf.Ask("unsub " + _id + " " + key.What);
        }

        // ── Events ──

        // scriptBlock(value, name) each time a control changes: a button, a
        // hat, or an axis as it moves, up to 100 times a second.
        internal void OnChange(string name, object callback)
        {
            callback = Client.Callable(callback, "OnChange needs a script block.");
            FollowSelf();
            Control c = Lookup(name);
            string key = c == null ? name : c.Name;
            List<object> list;
            if (!_changes.TryGetValue(key, out list)) _changes[key] = list = new List<object>();
            list.Add(callback);
        }

        // scriptBlock(connected) when the device connects or disconnects.
        internal void OnConnect(object callback)
        {
            callback = Client.Callable(callback, "OnConnect needs a script block.");
            FollowSelf();
            _onConnect.Add(callback);
        }

        internal void Fire(string[] w)
        {
            if (w.Length < 4) return;
            switch (w[1])
            {
                case "b":
                {
                    if (w.Length < 5) return;
                    Control c;
                    string name = _byIndex.TryGetValue("b" + w[3], out c) ? c.Name : "Button" + w[3];
                    bool down = w[4] == "1";
                    Changed(name, down);
                    HotkeyKey key;
                    if (_keys.TryGetValue(name, out key))
                        foreach (HotkeyItem item in key.List.ToArray())
                            if (item.Up != down) Run(item);
                    break;
                }
                case "h":
                {
                    if (w.Length < 5) return;
                    int index = Text.Int(w[3]);
                    double deg = Text.Real(w[4]);
                    Control c;
                    Changed(_byIndex.TryGetValue("h" + index, out c) ? c.Name : "Hat" + index, deg);
                    double was;
                    string before = Text.Directions(_hats.TryGetValue(index, out was) ? was : -1);
                    string after = Text.Directions(deg);
                    _hats[index] = deg;
                    foreach (HotkeyKey key in _keys.Values.ToArray())
                    {
                        if (key.Kind != 'd' || key.List.Count == 0 || key.List[0].Hat != index) continue;
                        bool then = Text.Holds(before, key.List[0].Bit), now = Text.Holds(after, key.List[0].Bit);
                        if (then == now) continue;
                        foreach (HotkeyItem item in key.List.ToArray())
                            if (item.Up != now) Run(item);
                    }
                    break;
                }
                case "a":
                {
                    if (w.Length < 5) return;
                    Control c;
                    Changed(_byIndex.TryGetValue("a" + w[3], out c) ? c.Name : "Axis" + w[3], Text.Real(w[4]));
                    break;
                }
                case "x":
                {
                    if (w.Length < 5) return;
                    int number = w.Length > 5 ? Text.Int(w[5]) : 0;
                    HotkeyKey key;
                    if (!_lines.TryGetValue(number, out key)) return;
                    bool past = w[4] == "1";
                    foreach (HotkeyItem item in key.List.ToArray())
                        if (item.Up != past) Run(item);
                    break;
                }
                case "state":
                    Changed(string.Empty, _connected);
                    break;
            }
        }

        private void Run(HotkeyItem item)
        {
            _pf.Queue(item.Callback, item.Name);
        }

        internal void Changed(string name, object value)
        {
            if (name.Length == 0)
            {
                foreach (object callback in _onConnect.ToArray()) _pf.Queue(callback, value);
                return;
            }
            List<object> list;
            if (_changes.TryGetValue(name, out list))
                foreach (object callback in list.ToArray()) _pf.Queue(callback, value, name);
        }

        internal virtual void Feedback(string[] w)
        {
        }
    }

    // A controller of the script's own on PadForge's device list. Map and
    // assign it like any device. Setting a property sets that control:
    // $ctl.A = $true, $ctl.LeftX = -50, $ctl.DPad = 'Up'. $null puts it at
    // rest.
    public sealed class Controller : Device
    {
        private readonly string _key;
        private readonly string _label;
        private readonly IDictionary _spec;
        private readonly Dictionary<string, string> _set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<object>> _feedback = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _extras = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _extraWire = new Dictionary<string, string>(StringComparer.Ordinal);

        // The words of the pipe a declared name may not take, as PadForge
        // keeps them.
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gyro", "accel", "bat", "charging", "rest", "after", "in", "claim", "above", "below", "hyst", "line", "all",
            "state", "buttons", "hats", "axes", "axis", "hz", "devices", "profile", "engine",
        };

        private static readonly string[] Ends = { "Up", "Right", "Down", "Left" };

        internal Controller(Client pf, string key, string name, IDictionary spec) : base(pf, key)
        {
            _key = key;
            _label = name;
            _spec = spec ?? new Hashtable();
            _connected = true;
            _followed = true;
            Shape();
        }

        public string Key { get { return _key; } }
        public override string Name { get { return _label; } }
        public override string Type { get { return "program"; } }
        public override bool Connected { get { return _id.Length > 0 && _pf.Connected; } }

        private struct Declared
        {
            internal char Kind;
            internal int Index;
            internal string Label;
            internal bool Stick;
        }

        private object Spec(string name)
        {
            return _spec.Contains(name) ? _spec[name] : _spec.Contains(name.ToLowerInvariant()) ? _spec[name.ToLowerInvariant()] : null;
        }

        // The controls named by the declaration, as PadForge names them:
        // each declared name made one word, or the numbered name when that
        // word is empty, taken, a word of the pipe or reads as another
        // control.
        private void Shape()
        {
            List<Declared> buttons = Declare(Spec("Buttons"), 'b', 0, 0);
            List<Declared> axes = Declare(Spec("Axes"), 'a', 0, 50);
            axes.AddRange(Declare(Spec("Sliders"), 'a', 24, 0));
            List<Declared> hats = Declare(Spec("Hats"), 'h', 0, 0);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<Declared> list in new[] { buttons, axes, hats })
            {
                foreach (Declared d in list.OrderBy(x => x.Index))
                {
                    string numbered = d.Kind == 'b' ? "Button" + d.Index : d.Kind == 'h' ? "Hat" + d.Index
                        : d.Index >= 24 ? "Slider" + (d.Index - 24) : "Axis" + d.Index;
                    string word = ScriptWord(d.Label);
                    if (word == null || taken.Contains(word) || Reserved.Contains(word) || LooksNumbered(word)) word = numbered;
                    taken.Add(word);
                    Control c = new Control(word, d.Kind, d.Index, d.Stick);
                    _controls[word] = c;
                    _byIndex[d.Kind.ToString() + d.Index] = c;
                }
            }
        }

        private static List<Declared> Declare(object list, char kind, int first, double stickRest)
        {
            List<Declared> found = new List<Declared>();
            int position = 0;
            foreach (object item in Text.Items(list))
            {
                Declared d = new Declared();
                d.Kind = kind;
                int index = position;
                double rest = stickRest;
                IDictionary map = item as IDictionary;
                if (map != null)
                {
                    if (map.Contains("index")) index = (int)Text.ToNumber(map["index"], "An index");
                    if (map.Contains("name")) d.Label = Convert.ToString(Text.Plain(map["name"]), Text.Invariant);
                    if (map.Contains("rest")) rest = Share(map["rest"]);
                }
                else d.Label = Convert.ToString(item, Text.Invariant);
                d.Index = first + index;
                d.Stick = rest == 50;
                found.Add(d);
                position++;
            }
            return found;
        }

        private static double Share(object rest)
        {
            string text = Text.Plain(rest) as string;
            if (text != null) text = text.Trim().TrimEnd('%');
            return Text.ToNumber(text ?? rest, "A rest");
        }

        // A name as one word: its letters and digits, each word's first
        // letter upper case. Null when nothing is left, it starts with a
        // digit or it runs past 32 letters.
        private static string ScriptWord(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.Length > 64) name = name.Substring(0, 64);
            StringBuilder sb = new StringBuilder(name.Length);
            bool start = true;
            foreach (char c in name)
            {
                if (c < 128 && char.IsLetterOrDigit(c))
                {
                    sb.Append(start ? char.ToUpperInvariant(c) : c);
                    start = false;
                }
                else start = true;
            }
            if (sb.Length == 0 || char.IsDigit(sb[0]) || sb.Length > 32) return null;
            return sb.ToString();
        }

        private static bool LooksNumbered(string word)
        {
            string lower = word.ToLowerInvariant();
            if (lower.Length >= 2 && (lower[0] == 'b' || lower[0] == 'a' || lower[0] == 'h'))
            {
                int index;
                int end = lower[0] == 'b' ? 256 : lower[0] == 'a' ? 32 : 4;
                if (int.TryParse(lower.Substring(1), NumberStyles.None, Text.Invariant, out index) && index < end) return true;
            }
            foreach (string stem in new[] { "Button", "Axis", "Slider", "Hat" })
            {
                int index;
                if (word.Length > stem.Length && word.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(word.Substring(stem.Length), NumberStyles.None, Text.Invariant, out index)) return true;
            }
            if (lower.Length > 1 && lower[0] == 't' && (char.IsDigit(lower[1]) || lower.StartsWith("tc", StringComparison.Ordinal))) return true;
            foreach (string end in Ends)
                if (Text.Ends(word, end) && word.StartsWith("Hat", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Declares it, and puts back what the script set before.
        internal void Declare()
        {
            StringBuilder json = new StringBuilder("{");
            json.Append("\"buttons\":").Append(Text.Json(Listed(Spec("Buttons"))));
            json.Append(",\"axes\":").Append(Text.Json(Listed(Spec("Axes"))));
            json.Append(",\"sliders\":").Append(Text.Json(Listed(Spec("Sliders"))));
            json.Append(",\"hats\":").Append(Text.Json(Listed(Spec("Hats"))));
            foreach (string part in new[] { "gyro", "accel", "battery" })
                if (Text.IsOn(Spec(part))) json.Append(",\"").Append(part).Append("\":true");
            List<object> touch = Text.Items(Spec("Touchpads"));
            if (touch.Count > 0) json.Append(",\"touchpads\":").Append(Text.Json(touch));
            json.Append(",\"feedback\":").Append(Text.Json(Text.Items(Spec("Feedback")))).Append('}');
            Answer answer = _pf.Ask("declare key=" + _key + " name=" + Text.Quote(_label) + " " + json);
            Row(answer.Get("id", string.Empty), _label, "program", true);
            Again();
        }

        // Sends what the script set again: after PadForge starts again, and
        // after the lease lapsed and PadForge rested the controller.
        internal void Again()
        {
            List<string> line = _set.Select(s => s.Key + "=" + s.Value).Concat(_extraWire.Select(s => s.Key + "=" + s.Value)).ToList();
            if (_id.Length > 0 && line.Count > 0) _pf.Send("s " + _id + " " + string.Join(" ", line));
        }

        // A list of controls for the declaration: each object's keys in
        // lower case, as PadForge reads them, and a rest as a share: "50%".
        private static List<object> Listed(object list)
        {
            List<object> found = new List<object>();
            foreach (object item in Text.Items(list))
            {
                IDictionary map = item as IDictionary;
                if (map == null)
                {
                    found.Add(item);
                    continue;
                }
                Hashtable copy = new Hashtable();
                foreach (DictionaryEntry entry in map)
                {
                    string key = Convert.ToString(Text.Plain(entry.Key), Text.Invariant).ToLowerInvariant();
                    copy[key] = key == "rest" ? (object)(Text.Number(Share(entry.Value)) + "%") : Text.Plain(entry.Value);
                }
                found.Add(copy);
            }
            return found;
        }

        internal override object Read(string name)
        {
            Control c = Lookup(name);
            if (c == null)
            {
                object extra;
                if (Extra(name)) return _extras.TryGetValue(name, out extra) ? extra : null;
                HotkeyItem d = Direction(name);
                if (d == null) return base.Read(name);
                string set;
                return Text.Holds(Text.HatWords(_set.TryGetValue(HatName(d), out set) ? set : string.Empty), d.Bit);
            }
            string wire;
            if (!_set.TryGetValue(c.Name, out wire)) wire = string.Empty;
            if (c.Kind == 'b') return wire == "1";
            if (c.Kind == 'h')
            {
                if (wire.Length == 0 || wire == "-1") return -1.0;
                return Text.Ends(wire, "deg") ? (object)Text.Real(wire.Substring(0, wire.Length - 3)) : wire;
            }
            return wire.Length == 0 ? 0.0 : Text.Real(wire.TrimEnd('%'));
        }

        private static bool Extra(string name)
        {
            return string.Equals(name, "Gyro", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Accel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Battery", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Charging", StringComparison.OrdinalIgnoreCase);
        }

        // Sets a control of the script's own controller. $null puts it at
        // rest.
        internal override void Hold(string name, object value)
        {
            Control c = Lookup(name);
            if (c == null && Extra(name))
            {
                string word = name.ToLowerInvariant();
                string sent;
                if (word == "battery")
                {
                    word = "bat";
                    sent = Math.Round(Text.ToNumber(value, "Battery")).ToString(Text.Invariant);
                }
                else if (word == "charging") sent = Text.IsOn(value) ? "1" : "0";
                else sent = Three(value, name);
                _extraWire[word] = sent;
                _extras[name] = value;
                Send(word + "=" + sent);
                return;
            }
            if (c == null)
            {
                HotkeyItem d = Direction(name);
                if (d == null) throw new ArgumentException("No such control: " + name);
                Send(Turn(d, value));
                return;
            }
            string wire = Text.IsEmpty(value) ? (c.Kind == 'b' ? "0" : c.Kind == 'h' ? "-1" : "0%") : Text.Wire(c.Kind, value);
            if (wire == "rest") wire = "0%";
            _set[c.Name] = wire;
            Send(c.Name + "=" + wire);
        }

        // The name of the hat a direction is of.
        private string HatName(HotkeyItem d)
        {
            Control hat;
            return _byIndex.TryGetValue("h" + d.Hat, out hat) ? hat.Name : "Hat" + d.Hat;
        }

        // A hat's direction pressed or released ($true, $false or $null):
        // the hat the script set keeps it, for after a lapse or a restart,
        // and the word that sends it, which PadForge puts into the hat as
        // the change lands, beside the hat's other directions.
        private string Turn(HotkeyItem d, object value)
        {
            string hat = HatName(d);
            bool pressed = !Text.IsEmpty(value) && Text.IsOn(value);
            string set;
            string words = Text.Turn(Text.HatWords(_set.TryGetValue(hat, out set) ? set : string.Empty), d.Bit, pressed);
            _set[hat] = Text.Wire('h', Text.DirectionDegrees(words));
            return hat + d.Bit + "=" + (pressed ? "1" : "0");
        }

        private static string Three(object value, string what)
        {
            List<object> items = Text.Items(value);
            if (items.Count == 1 && items[0] is string) items = ((string)items[0]).Split(',').Cast<object>().ToList();
            if (items.Count != 3) throw new ArgumentException(what + " takes three numbers: x, y and z.");
            return string.Join(",", items.Select(v => Text.Number(Text.ToNumber(v, what))));
        }

        private void Send(string controls)
        {
            if (_id.Length > 0) _pf.Send("s " + _id + " " + controls);
        }

        // Several controls as one change: Hold('LeftX', 50, 'LeftY', -20).
        internal override void Hold(object[] a)
        {
            List<string> line = new List<string>();
            foreach (KeyValuePair<string, object> pair in Pairs(a))
            {
                Control c = Lookup(pair.Key);
                if (c == null)
                {
                    HotkeyItem d = Direction(pair.Key);
                    if (d == null) throw new ArgumentException("No such control: " + pair.Key);
                    line.Add(Turn(d, pair.Value));
                    continue;
                }
                // $null puts the control at rest, as setting it to $null
                // does.
                string wire = Text.IsEmpty(pair.Value) ? (c.Kind == 'b' ? "0" : c.Kind == 'h' ? "-1" : "0%") : Text.Wire(c.Kind, pair.Value);
                if (wire == "rest") wire = "0%";
                _set[c.Name] = wire;
                line.Add(c.Name + "=" + wire);
            }
            if (line.Count > 0) Send(string.Join(" ", line));
        }

        // Puts controls at rest: Release('A'), or every control.
        internal override void Release(object[] names)
        {
            if (names.Length == 0)
            {
                _set.Clear();
                if (_id.Length > 0) _pf.Send("rest " + _id);
                return;
            }
            foreach (object name in names) Hold(Word(name), null);
        }

        internal override void Set(string name, object value)
        {
            Hold(name, value);
        }

        // A tap before PadForge takes the declaration, or while PadForge is
        // not connected, is refused, as one on a device not found yet is.
        internal override void Tap(string name, int ms, object value)
        {
            if (_id.Length == 0 || !_pf.Connected) throw Absent("tap");
            Control c = Target(name);
            if (c == null) throw new ArgumentException("No such control: " + name);
            if (Text.IsEmpty(value)) value = c.Kind == 'b' || c.Kind == 'd' ? (object)true : c.Kind == 'h' ? 0.0 : 100.0;
            string rest = c.Kind == 'b' || c.Kind == 'd' ? "0" : c.Kind == 'h' ? "-1" : "0%";
            if (!_pf.Send("s " + _id + " " + c.Name + "=" + Text.Wire(c.Kind, value))) throw Absent("tap");
            Send(c.Name + "=" + rest + " in=" + ms.ToString(Text.Invariant));
        }

        internal override void Sequence(object[] steps)
        {
            if (_id.Length == 0 || !_pf.Connected) throw Absent("sequence");
            List<string> lines = new List<string>();
            foreach (KeyValuePair<Control, int> step in Steps(steps))
            {
                Control c = step.Key;
                string on = c.Kind == 'b' || c.Kind == 'd' ? "1" : c.Kind == 'h' ? "0deg" : "100%";
                string off = c.Kind == 'b' || c.Kind == 'd' ? "0" : c.Kind == 'h' ? "-1" : "0%";
                lines.Add(c.Name + "=" + on + (lines.Count == 0 ? string.Empty : " after=0"));
                lines.Add(c.Name + "=" + off + " after=" + step.Value.ToString(Text.Invariant));
            }
            for (int i = 0; i < lines.Count; i++)
                if (!_pf.Send("s " + _id + " " + lines[i]) && i == 0) throw Absent("sequence");
        }

        internal override void Hotkey(string keyName, object callback)
        {
            throw new InvalidOperationException("A controller of the script's own has no hotkeys. Follow a device PadForge reads with Get-PadForgeDevice.");
        }

        internal override object Invoke(string method, object[] a, out bool known)
        {
            known = true;
            switch (method)
            {
                // A finger on touchpad pad: x and y from 0 to 1.
                case "touch":
                    Need(a, 4, "Touch(pad, finger, x, y, down, pressure)");
                    Send("t" + Whole(a[0]) + "." + Whole(a[1]) + "=" + Text.Number(Text.ToNumber(a[2], "x")) + ","
                        + Text.Number(Text.ToNumber(a[3], "y")) + "," + (a.Length > 4 && !Text.IsOn(a[4]) ? "0" : "1") + ","
                        + Text.Number(a.Length > 5 ? Text.ToNumber(a[5], "Pressure") : 1));
                    return null;
                // A touchpad's click.
                case "click":
                    Need(a, 1, "Click(pad, down)");
                    Send("tc" + Whole(a[0]) + "=" + (a.Length > 1 && !Text.IsOn(a[1]) ? "0" : "1"));
                    return null;
                // scriptBlock(low, high) when a game rumbles the controller, 0
                // to 65535.
                case "onrumble": return On("rumble", a);
                // scriptBlock(left, right) for the trigger motors.
                case "ontriggerrumble": return On("trigrumble", a);
                // scriptBlock(left, right): each trigger's 11-byte effect as 22
                // hex digits.
                case "ontriggereffects": return On("trigfx", a);
                // scriptBlock('rrggbb').
                case "onlight": return On("led", a);
                // scriptBlock(number), from 1.
                case "onplayer": return On("player", a);
                // Takes the controller off PadForge's device list.
                case "remove":
                    _pf.Mine.Remove(_key);
                    if (_id.Length > 0) _pf.Ask("gone " + _id);
                    return null;
            }
            return base.Invoke(method, a, out known);
        }

        private static string Whole(object value)
        {
            return ((int)Text.ToNumber(value, "A touchpad's number")).ToString(Text.Invariant);
        }

        private object On(string kind, object[] a)
        {
            Need(a, 1, "On...(scriptBlock)");
            List<object> list;
            if (!_feedback.TryGetValue(kind, out list)) _feedback[kind] = list = new List<object>();
            list.Add(Client.Callable(a[0], "A feedback handler needs a script block."));
            return null;
        }

        internal override void Feedback(string[] w)
        {
            List<object> list;
            if (!_feedback.TryGetValue(w[1], out list)) return;
            List<object> args = new List<object>();
            for (int i = 3; i < w.Length; i++)
            {
                long number;
                if (w[1] != "trigfx" && w[1] != "led" && long.TryParse(w[i], NumberStyles.Integer, Text.Invariant, out number)) args.Add(number);
                else args.Add(w[i]);
            }
            foreach (object callback in list.ToArray()) _pf.Queue(callback, args.ToArray());
        }
    }

    // The script's connection to PadForge. It does not wait: while PadForge
    // is not running, Connected is false, Why says why, and the library
    // tries again every second. The pipe's reader hands events to a thread
    // of the connection's own, which keeps the copies, finds devices again
    // and queues the script blocks, and a timer beats and opens again.
    // Script blocks run only on the script's own thread, in Pump.
    public sealed class Client : IDisposable
    {
        public const string DefaultPipe = "PadForge.Automation";
        private const int BeatMs = 31;
        private const int RetryMs = 1000;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly AutoResetEvent Woken = new AutoResetEvent(false);
        private static readonly string LostMark = "\0lost";

        // Runs one script block on the script's thread: set by the module,
        // so what the block outputs shows on the console.
        public static ScriptBlock Runner;

        internal readonly object Sync = new object();
        private readonly object _state = new object();
        private readonly string _wanted;
        private readonly string _token;
        private readonly int _lease;
        private readonly Runspace _runspace;
        private readonly BlockingCollection<string> _events = new BlockingCollection<string>();
        private readonly ConcurrentQueue<Call> _calls = new ConcurrentQueue<Call>();
        private readonly List<string> _errors = new List<string>();
        private volatile Connection _link;
        private volatile bool _closed = true;
        private volatile bool _ready;
        private volatile bool _ended;
        private volatile bool _waiting;
        private long _nextTry;
        private int _opening;
        private int _dispatching;
        private Timer _timer;

        internal readonly Dictionary<string, Device> Wanting = new Dictionary<string, Device>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Device> ById = new Dictionary<string, Device>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Controller> Mine = new Dictionary<string, Controller>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _follows = new List<string>();
        private readonly Dictionary<string, List<object>> _callbacks = new Dictionary<string, List<object>>(StringComparer.Ordinal);

        public Client(string name, string pipe, string level) : this(name, pipe, level, null, 0)
        {
        }

        private Client(string name, string pipe, string level, string token, int lease)
        {
            Name = name;
            Pipe = pipe;
            _wanted = level;
            _token = token;
            _lease = lease;
            Why = "Not connected yet.";
            Level = string.Empty;
            Version = string.Empty;
            _runspace = Runspace.DefaultRunspace;
            Thread worker = new Thread(Work);
            worker.IsBackground = true;
            worker.Name = "PadForge events";
            worker.Start();
            TryOpen();
            if (token == null) _timer = new Timer(Tick, null, BeatMs, BeatMs);
            else if (!_closed)
            {
                int every = Math.Max(BeatMs, lease / 3);
                _timer = new Timer(Tick, null, every, every);
            }
        }

        // The pipe the script's connection opens: PadForge's own, or the
        // one PADFORGE_PIPE names, as a test's host serves.
        public static string PipeFromEnvironment()
        {
            string pipe = Environment.GetEnvironmentVariable("PADFORGE_PIPE");
            return string.IsNullOrEmpty(pipe) ? DefaultPipe : pipe;
        }

        // A plugin's connection: PadForge started this program, and its
        // environment holds the one-time key and the pipe's name. The key
        // is taken once, so a plugin connects once for each start and does
        // not try again.
        public static Client Plugin(string name, int lease)
        {
            string token = Environment.GetEnvironmentVariable("PADFORGE_PLUGIN_TOKEN");
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("PadForge did not start this program as a plugin. Add it on the Plugins page.");
            Client pf = new Client(name, PipeFromEnvironment(), null, token, lease);
            if (!pf.Connected) throw new InvalidOperationException(pf.Why);
            return pf;
        }

        public string Name { get; private set; }
        public string Pipe { get; private set; }
        public string Level { get; private set; }
        public string Version { get; private set; }
        public int Session { get; private set; }
        public string Why { get; private set; }
        // Connected once PadForge answered the hello.
        public bool Connected { get { return !_closed && _ready; } }
        public bool Ended { get { return _ended; } }
        public bool IsPlugin { get { return _token != null; } }

        // What went wrong where no command could say it: a hotkey or a hold
        // PadForge refused when it put them back.
        public string[] Errors { get { lock (_errors) return _errors.ToArray(); } }

        internal void Note(string message)
        {
            lock (_errors)
            {
                _errors.Add(message);
                if (_errors.Count > 100) _errors.RemoveAt(0);
            }
        }

        private static long Now() { return Clock.ElapsedMilliseconds; }

        // ── Opening, and opening again ──

        private static Connection Open(string pipe)
        {
            bool own = string.Equals(pipe, DefaultPipe, StringComparison.OrdinalIgnoreCase);
            int probe = Connection.Probe(pipe, own);
            if (probe == 2) throw new IOException("PadForge is running, but Automation is off. Turn it on in Extras, Automation.");
            if (probe == 1) throw new IOException(own ? "PadForge is not running." : "Nothing serves the pipe " + pipe + ".");
            Connection handle;
            try { handle = new Connection(pipe, 2000); }
            catch (TimeoutException) { throw new IOException("PadForge did not answer."); }
            if (!handle.ServerIsPadForge(pipe))
            {
                handle.Dispose();
                throw new IOException("Another program holds the pipe " + pipe + ". It is not PadForge, so nothing was sent to it.");
            }
            return handle;
        }

        private bool TryOpen()
        {
            _nextTry = Now() + RetryMs;
            Connection link;
            try { link = Open(Pipe); }
            catch (Exception e)
            {
                Why = e.Message;
                return false;
            }
            link.Event += OnLinkEvent;
            link.Closed += OnLinkClosed;
            lock (_state)
            {
                // A script that closed the connection while it opened keeps
                // it closed.
                if (_ended)
                {
                    link.Dispose();
                    return false;
                }
                _link = link;
                _closed = false;
                _ready = false;
            }
            link.Start();
            string hello = "hello name=" + Text.Quote(Name) + " " + (_token != null ? "kind=plugin token=" + Text.Quote(_token) : "kind=script")
                + " protocol=1 units=plain";
            if (_wanted != null) hello += " level=" + _wanted;
            if (_lease > 0) hello += " lease=" + _lease.ToString(Text.Invariant);
            Answer answer;
            try { answer = Ask(hello); }
            catch (Exception e)
            {
                Lose(link, e.Message);
                return false;
            }
            Version = answer.Get("padforge", string.Empty);
            Level = answer.Get("level", string.Empty);
            Session = Text.Int(answer.Get("session", "0"));
            _waiting = answer.Get("consent", string.Empty) == "waiting";
            Why = _waiting ? "PadForge asks you whether this script may work with it." : string.Empty;
            _ready = true;
            if (_token == null)
                lock (Sync) Restore();
            _waiting = false;
            if (!_closed) Why = string.Empty;
            return true;
        }

        // Everything the script holds, claims and follows, put back on a
        // new connection: its devices found again, its controllers declared
        // again under the same keys, its values set again.
        private void Restore()
        {
            foreach (string kind in _follows.ToArray())
                if (kind != "devices") Try(delegate { Ask("sub " + kind); });
            Try(delegate { Ask("sub devices"); });
            Try(Relist);
            foreach (Controller controller in Mine.Values.ToArray())
                Try(controller.Declare);
            foreach (KeyValuePair<string, string> value in _values.ToArray())
                Send("var " + value.Key + "=" + value.Value);
            foreach (Device pad in Wanting.Values.ToArray())
                Try(pad.Locate);
        }

        // Every device the script listed takes its connection from
        // PadForge's list again, on a new connection.
        private void Relist()
        {
            if (ById.Count == 0) return;
            foreach (Answer row in Ask("devices").Rows)
            {
                if (row.Words.Length == 0 || row.Words[0] != "device") continue;
                Device pad;
                if (ById.TryGetValue(row.Get("id", string.Empty), out pad) && !(pad is Controller)) pad._online = row.Get("online", "0") == "1";
            }
        }

        private void Try(Action step)
        {
            try { step(); }
            catch (Exception e) { Note(e.Message); }
        }

        private void Tick(object unused)
        {
            if (_closed)
            {
                if (_token != null || _ended) return;
                if (Now() < _nextTry || Interlocked.CompareExchange(ref _opening, 1, 0) != 0) return;
                try { TryOpen(); }
                catch (Exception e) { Note(e.Message); }
                finally { Interlocked.Exchange(ref _opening, 0); }
                return;
            }
            if (_ready) Send("beat");
        }

        private void OnLinkEvent(object sender, LineEventArgs e)
        {
            if (sender != _link) return;
            try { _events.Add(e.Line); }
            catch (InvalidOperationException) { }
        }

        private void OnLinkClosed(object sender, EventArgs e)
        {
            Lose((Connection)sender, _token != null ? "PadForge closed the connection." : "PadForge closed the connection. The library tries again.");
        }

        private void Lose(Connection link, string why)
        {
            lock (_state)
            {
                if (link != _link || _closed) return;
                _closed = true;
                _ready = false;
            }
            Why = why;
            _nextTry = Now() + RetryMs;
            try { link.Dispose(); }
            catch (Exception) { }
            try { _events.Add(LostMark); }
            catch (InvalidOperationException) { }
            if (_token != null && _timer != null) _timer.Dispose();
        }

        // ── The pipe ──

        // A line that needs no answer. While PadForge is not connected it
        // is dropped: what it changed is put back from what the library
        // keeps.
        internal bool Send(string line)
        {
            Connection link = _link;
            if (_closed || !_ready || link == null) return false;
            try
            {
                link.Send(line);
                return true;
            }
            catch (Exception) { return false; }
        }

        // Sends a line and waits for its answer. Throws PadForge's refusal
        // in words. A line that waits for the user's answer to this script's
        // first connection waits as long as PadForge's question card does.
        internal Answer Ask(string line)
        {
            Connection link = _link;
            if (_closed || link == null || (!_ready && !line.StartsWith("hello ", StringComparison.Ordinal)))
                throw new InvalidOperationException("PadForge is not connected: " + Why);
            string[] all;
            try { all = link.Request(line, _waiting ? 150000 : 10000); }
            catch (IOException) { all = null; }
            if (all == null) throw new IOException("PadForge is not connected: the pipe closed.");
            string final = all[all.Length - 1];
            if (final.StartsWith("err ", StringComparison.Ordinal)) throw Refusal(line.Split(' ')[0], final);
            Answer answer = Text.Split(final.Length > 2 ? final.Substring(2) : string.Empty);
            Answer[] rows = new Answer[all.Length - 1];
            for (int i = 0; i < rows.Length; i++) rows[i] = Text.Split(all[i]);
            answer.Rows = rows;
            return answer;
        }

        // PadForge's refusal of a request, in words, the request named by
        // verb.
        private Exception Refusal(string verb, string final)
        {
            string[] parts = final.Split(new[] { ' ' }, 3);
            string code = parts.Length > 1 ? parts[1] : "internal";
            if (code == "not-allowed")
                return new InvalidOperationException("PadForge refused " + verb + ": Automation's level is " + Level + ", too low for it. Raise it in Extras, Automation.");
            return new InvalidOperationException("PadForge refused " + verb + ": " + (parts.Length > 2 ? parts[2] : string.Empty) + " (" + code + ").");
        }

        // A Tap's or a Sequence's lines, written one after the other with no
        // wait between them, so their times on PadForge's clock stay as
        // sent, and then each one's answer. PadForge refuses a timed step on
        // a device that is not connected: the first refusal is thrown in
        // words, named by verb, the call's name.
        internal void Timed(string verb, IList<string> lines)
        {
            Connection link = _link;
            if (_closed || link == null || !_ready) throw new InvalidOperationException("PadForge is not connected: " + Why);
            string[] finals;
            try { finals = link.Requests(lines, _waiting ? 150000 : 10000); }
            catch (IOException) { finals = null; }
            if (finals == null || Array.IndexOf(finals, null) >= 0) throw new IOException("PadForge is not connected: the pipe closed.");
            foreach (string final in finals)
                if (final.StartsWith("err ", StringComparison.Ordinal)) throw Refusal(verb, final);
        }

        // ── Events ──

        private void Work()
        {
            foreach (string line in _events.GetConsumingEnumerable())
            {
                try
                {
                    lock (Sync)
                    {
                        if (ReferenceEquals(line, LostMark))
                        {
                            foreach (Device pad in ById.Values.ToArray()) pad.Lost();
                            continue;
                        }
                        if (Mirror(line)) Event(line);
                    }
                }
                catch (Exception e) { Note(e.Message); }
            }
        }

        // An event into the copy of its device. False for a change the
        // copy holds already.
        private bool Mirror(string line)
        {
            string[] w = line.Split(' ');
            Device pad;
            if (w.Length < 3 || !ById.TryGetValue(w[2], out pad)) return true;
            return pad.Take(w, line);
        }

        private void Event(string line)
        {
            Answer e = Text.Split(line);
            string[] w = e.Words;
            if (w.Length < 2) return;
            Device pad;
            switch (w[1])
            {
                case "b":
                case "h":
                case "a":
                case "x":
                case "state":
                    if (w.Length > 2 && ById.TryGetValue(w[2], out pad)) pad.Fire(line.Split(' '));
                    break;
                case "dev":
                {
                    if (w.Length < 4) return;
                    bool online = w[3] == "online";
                    // A device the script listed keeps its connection
                    // current.
                    Device listed;
                    if (ById.TryGetValue(w[2], out listed)) listed._online = online;
                    Call("dev", w[2], online);
                    if (online)
                        foreach (Device wanted in Wanting.Values.ToArray())
                            if (wanted.Id.Length == 0 || !wanted._followed) Try(wanted.Locate);
                    break;
                }
                case "profile": Call("profile", w.Length > 2 ? w[2] : "default"); break;
                case "engine": Call("engine", w.Length > 2 ? w[2] : string.Empty); break;
                case "level":
                    Level = w.Length > 2 ? w[2] : string.Empty;
                    Call("level", Level);
                    break;
                case "rumble":
                case "trigrumble":
                case "trigfx":
                case "led":
                case "player":
                    if (w.Length > 2 && ById.TryGetValue(w[2], out pad)) pad.Feedback(line.Split(' '));
                    break;
                case "lease":
                    // PadForge rests a script's own controllers when its lease
                    // lapses, and gives back only its holds and claims at the
                    // next line: the controllers' controls go again.
                    if (w.Length > 2 && w[2] == "lapsed")
                        foreach (Controller controller in Mine.Values.ToArray()) controller.Again();
                    break;
                case "error": Note(line.Substring(8)); break;
                default: Call("event|" + w[1], e.Pairs); break;
            }
        }

        private void Call(string key, params object[] args)
        {
            List<object> list;
            if (!_callbacks.TryGetValue(key, out list)) return;
            foreach (object callback in list.ToArray()) Queue(callback, args);
        }

        private void Add(string key, object callback)
        {
            List<object> list;
            if (!_callbacks.TryGetValue(key, out list)) _callbacks[key] = list = new List<object>();
            list.Add(callback);
        }

        internal static object Callable(object callback, string message)
        {
            callback = Text.Plain(callback);
            if (!(callback is ScriptBlock)) throw new ArgumentException(message);
            return callback;
        }

        internal void Queue(object callback, params object[] args)
        {
            _calls.Enqueue(new Call(callback, args));
            Woken.Set();
        }

        // Runs the script blocks of the events that wait, in their order,
        // on the script's own thread. One run at a time: an event that comes
        // while a block runs joins the queue this run empties. A block's
        // error is thrown once the queue is done.
        public void Pump()
        {
            if (_runspace == null || Runspace.DefaultRunspace != _runspace) return;
            if (Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0) return;
            Exception first = null;
            try
            {
                Call call;
                while (_calls.TryDequeue(out call))
                {
                    try
                    {
                        ScriptBlock runner = Runner;
                        if (runner != null) runner.Invoke(call.Callback, call.Args);
                        else ((ScriptBlock)call.Callback).Invoke(call.Args);
                    }
                    catch (Exception e)
                    {
                        if (first == null) first = e;
                    }
                }
            }
            finally { Interlocked.Exchange(ref _dispatching, 0); }
            if (first != null) throw first;
        }

        // Waits up to ms for an event's script block, and runs what waits.
        public void Wait(int ms)
        {
            Pump();
            if (_calls.IsEmpty) Woken.WaitOne(Math.Max(0, ms));
            Pump();
        }

        // ── Devices ──

        // A device by its script ID, its type or a part of its name. The
        // same words give the same object. It is found again whenever
        // PadForge connects and whenever a device connects while it is
        // missing.
        public Device Device(string what)
        {
            Pump();
            lock (Sync)
            {
                string key = what.ToLowerInvariant();
                Device pad;
                if (Wanting.TryGetValue(key, out pad)) return pad;
                // A device Devices() listed is the same object Device() gives
                // for its script ID.
                if (ById.TryGetValue(key, out pad) && !(pad is Controller) && pad._what.ToLowerInvariant() == key)
                {
                    Wanting[key] = pad;
                    if (Connected && !pad._followed) pad.Locate();
                    return pad;
                }
                pad = new Device(this, what);
                Wanting[key] = pad;
                if (Connected) pad.Locate();
                return pad;
            }
        }

        // Every device PadForge knows, connected or not, as device objects.
        // A device listed here is the same object Device() gives for its
        // script ID, and it follows itself on its first use of any kind: a
        // read, a hotkey, a change callback, a hold, Set, Tap or Sequence.
        public Device[] Devices()
        {
            Pump();
            lock (Sync)
            {
                List<Device> list = new List<Device>();
                foreach (Answer row in Ask("devices").Rows)
                {
                    if (row.Words.Length == 0 || row.Words[0] != "device") continue;
                    string id = row.Get("id", string.Empty);
                    Device pad;
                    if (!ById.TryGetValue(id, out pad)) pad = new Device(this, id);
                    pad.Row(id, row.Get("name", string.Empty), row.Get("type", string.Empty), row.Get("online", "0") == "1");
                    list.Add(pad);
                }
                return list.ToArray();
            }
        }

        // A controller of the script's own. key keeps its identity, and its
        // mappings, across runs. The same key gives the same controller.
        public Controller AddController(string key, string name, IDictionary spec)
        {
            Pump();
            lock (Sync)
            {
                Controller controller;
                if (Mine.TryGetValue(key, out controller)) return controller;
                controller = new Controller(this, key, name, spec);
                Mine[key] = controller;
                if (Connected) controller.Declare();
                return controller;
            }
        }

        // ── Events ──

        // scriptBlock(id, online) when a device connects or disconnects.
        public void OnDevice(object callback) { Follow("devices", "dev", callback); }

        // scriptBlock(name) when the profile changes, 'default' for the
        // default.
        public void OnProfile(object callback) { Follow("profile", "profile", callback); }

        // scriptBlock(state): running, idle, stopping or stopped.
        public void OnEngine(object callback) { Follow("engine", "engine", callback); }

        // scriptBlock(level) when the user changes Automation's level.
        public void OnLevel(object callback)
        {
            Pump();
            lock (Sync) Add("level", Callable(callback, "OnLevel needs a script block."));
        }

        // scriptBlock(pairs) for every event of this kind, with the line's
        // key=value words as a hashtable: setting, press and choices for a
        // plugin's declared settings.
        public void OnEvent(string kind, object callback)
        {
            Pump();
            lock (Sync) Add("event|" + kind, Callable(callback, "OnEvent needs a script block."));
        }

        private void Follow(string kind, string key, object callback)
        {
            Pump();
            lock (Sync)
            {
                Add(key, Callable(callback, "A handler needs a script block."));
                if (_follows.Contains(kind)) return;
                _follows.Add(kind);
                if (Connected && kind != "devices") Ask("sub " + kind);
            }
        }

        // ── Values and commands ──

        // A named value Custom Expression macro triggers read. Set again
        // after PadForge starts again.
        public void SetValue(string name, object value)
        {
            Pump();
            double number;
            object plain = Text.Plain(value);
            string text = plain is bool ? ((bool)plain ? "1" : "0")
                : Text.TryNumber(plain, out number) ? Text.Number(number) : Convert.ToString(plain, Text.Invariant);
            lock (Sync)
            {
                _values[name] = text;
                if (Connected) Ask("var " + name + "=" + text);
            }
        }

        // A line for this plugin's log on the Plugins page. Only a plugin
        // has a log.
        public void Log(string text)
        {
            Pump();
            Send("log " + Text.Quote(text));
        }

        // Any line of the pipe, and its answer.
        public Answer Request(string line)
        {
            Pump();
            return Ask(line);
        }

        public void SwitchProfile(string name) { Request("activate " + Text.Quote(name)); }
        public void ReleaseProfile() { Request("deactivate"); }
        public void Engine(bool on) { Request("engine " + (on ? "on" : "off")); }
        public void Controllers(bool on) { Request("controllers " + (on ? "on" : "off")); }
        public void Controller(int number, bool on) { Request("slot " + number.ToString(Text.Invariant) + " " + (on ? "on" : "off")); }
        public void Show() { Request("show"); }
        public void Hide() { Request("hide"); }
        public Hashtable Snapshot() { return Request("snapshot").Pairs; }

        // What runs now, as JSON text.
        public string State() { return Request("state").Json; }

        // What virtual controller number sends to the game now.
        public Hashtable Outputs(int number) { return Request("outputs " + number.ToString(Text.Invariant)).Pairs; }

        // Runs an assistant tool, at the tool's own level. Returns JSON
        // text.
        public string Tool(string name, string json)
        {
            return Request("call " + name + " " + (string.IsNullOrEmpty(json) ? "{}" : json)).Json;
        }

        // Closes the connection for good.
        public void Close()
        {
            if (_timer != null) _timer.Dispose();
            Connection link;
            bool open;
            lock (_state)
            {
                _ended = true;
                link = _link;
                open = !_closed;
                _closed = true;
                _ready = false;
            }
            if (open && link != null)
            {
                try { link.Dispose(); }
                catch (Exception) { }
                try { _events.Add(LostMark); }
                catch (InvalidOperationException) { }
            }
            // The event thread ends once it has taken what waits.
            _events.CompleteAdding();
            Why = "The script closed the connection.";
        }

        public void Dispose() { Close(); }
    }
}
'@

if (-not ('PadForgeAutomation.Client' -as [type])) {
    $options = @{ TypeDefinition = $script:Source }
    # Windows PowerShell compiles against what it is told. PowerShell 7
    # references every assembly of its own already.
    if ($PSVersionTable.PSEdition -ne 'Core') { $options.ReferencedAssemblies = @('System.Core', [psobject].Assembly.Location) }
    Add-Type @options
}

# Runs one script block for the library, on the script's thread, with what
# it outputs on the console.
[PadForgeAutomation.Client]::Runner = {
    param($Callback, [object[]]$Arguments)
    & $Callback @Arguments | Out-Host
}

$script:Default = $null

# ── Helpers ──

function Get-PadForgeScriptName {
    # The name of the script that called the module, for its connection.
    foreach ($frame in Get-PSCallStack) {
        if ($frame.ScriptName -and $frame.ScriptName -ne $PSCommandPath) { return [IO.Path]::GetFileNameWithoutExtension($frame.ScriptName) }
    }
    'PowerShell'
}

function Use-PadForge($Connection) {
    # The connection a command uses: the one it was given, or the script's
    # own, made on first use and named after the script.
    if ($null -ne $Connection) { return $Connection }
    if ($null -eq $script:Default -or $script:Default.Ended) {
        $script:Default = [PadForgeAutomation.Client]::new((Get-PadForgeScriptName), [PadForgeAutomation.Client]::PipeFromEnvironment(), 'send-inputs')
    }
    $script:Default
}

function Invoke-PadForgeCall([scriptblock]$Do) {
    # Runs a call into the library and throws its own words, not
    # PowerShell's wrapping of them.
    try { & $Do }
    catch [System.Management.Automation.MethodInvocationException] {
        if ($null -eq $_.Exception.InnerException) { throw }
        throw $_.Exception.InnerException
    }
}

# ── The connection ──

function Connect-PadForge {
    <# Makes the script's connection with a name and the level it asks for:
       read, send-inputs (the default) or change-settings. Call it before
       anything else to choose them. It does not wait for PadForge. #>
    param(
        [Parameter(Position = 0)][string]$Name,
        [ValidateSet('read', 'send-inputs', 'change-settings')][string]$Level = 'send-inputs',
        [string]$Pipe
    )
    if (-not $Name) { $Name = Get-PadForgeScriptName }
    if (-not $Pipe) { $Pipe = [PadForgeAutomation.Client]::PipeFromEnvironment() }
    if ($null -ne $script:Default) { $script:Default.Close() }
    $script:Default = [PadForgeAutomation.Client]::new($Name, $Pipe, $Level)
    $script:Default
}

function Connect-PadForgePlugin {
    <# Opens a plugin's connection, which every command then uses: PadForge
       started this script, and its environment holds the one-time key and
       the pipe's name. The key is taken once, so a plugin connects once for
       each start and does not try again. #>
    param([Parameter(Mandatory, Position = 0)][string]$Name, [int]$Lease = 2000)
    $plugin = Invoke-PadForgeCall { [PadForgeAutomation.Client]::Plugin($Name, $Lease) }
    if ($null -ne $script:Default) { $script:Default.Close() }
    $script:Default = $plugin
    $plugin
}

function Get-PadForgeConnection {
    <# The script's connection: Connected, Why, Level, Version, Session and
       Errors. #>
    param($Connection)
    Use-PadForge $Connection
}

function Disconnect-PadForge {
    <# Closes the connection for good. #>
    param($Connection)
    if ($null -ne $Connection) { $Connection.Close(); return }
    if ($null -ne $script:Default) {
        $script:Default.Close()
        $script:Default = $null
    }
}

function Wait-PadForge {
    <# Runs event script blocks as their events come, for -Seconds, or until
       the connection closes for good. #>
    param([double]$Seconds = 0, $Connection)
    $c = Use-PadForge $Connection
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $c.Ended -and -not ($c.IsPlugin -and -not $c.Connected)) {
        $slice = 100
        if ($Seconds -gt 0) {
            $left = ($Seconds - $clock.Elapsed.TotalSeconds) * 1000
            if ($left -le 0) { break }
            $slice = [int][Math]::Ceiling([Math]::Min(100, $left))
        }
        try { $c.Wait($slice) }
        catch [System.Management.Automation.MethodInvocationException] {
            $inner = $_.Exception.InnerException
            if ($inner -is [System.Management.Automation.IContainsErrorRecord]) { Write-Error -ErrorRecord $inner.ErrorRecord }
            else { Write-Error -Exception $inner }
        }
    }
}

function Write-PadForgeLog {
    <# A line for this plugin's log on the Plugins page. Only a plugin has a
       log. #>
    param([Parameter(Mandatory, Position = 0)][string]$Text, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Log($Text) }
}

function Invoke-PadForgeRequest {
    <# Sends any line of the pipe and gives its answer: Words, Pairs (a
       hashtable), Json and Rows. Throws PadForge's refusal in words. #>
    param([Parameter(Mandatory, Position = 0)][string]$Line, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Request($Line) }
}

# ── Devices ──

function Get-PadForgeDevice {
    <# A device by its script ID, its type (gamepad, keyboard, mouse, wheel,
       joystick, program...) or a part of its name. The same words give the
       same object, and it is found again when it or PadForge comes back.
       Without words: every device PadForge knows, connected or not. #>
    param([Parameter(Position = 0)][string]$What, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { if ($What) { $c.Device($What) } else { $c.Devices() } }
}

function Add-PadForgeController {
    <# Adds a controller of the script's own. -Key keeps its identity, and
       its mappings, across runs. -Buttons, -Axes, -Sliders and -Hats take
       names, or hashtables with index, name and, for an axis or slider,
       rest as a share of its travel, 0 to 100: 50 makes a stick, which an
       axis is unless told. -Touchpads takes each touchpad's finger count,
       -Feedback any of rumble, trigrumble, trigfx, light and player. Its
       properties are its controls by name, and Gyro, Accel (x, y, z),
       Battery (0 to 100) and Charging. #>
    param(
        [Parameter(Mandatory, Position = 0)][string]$Key,
        [Parameter(Mandatory, Position = 1)][string]$Name,
        [object[]]$Buttons = @(), [object[]]$Axes = @(), [object[]]$Sliders = @(), [object[]]$Hats = @(),
        [switch]$Gyro, [switch]$Accel, [int[]]$Touchpads = @(), [switch]$Battery, [string[]]$Feedback = @(),
        $Connection
    )
    $c = Use-PadForge $Connection
    $spec = @{
        Buttons = $Buttons; Axes = $Axes; Sliders = $Sliders; Hats = $Hats
        Gyro = [bool]$Gyro; Accel = [bool]$Accel; Touchpads = $Touchpads; Battery = [bool]$Battery; Feedback = $Feedback
    }
    Invoke-PadForgeCall { $c.AddController($Key, $Name, $spec) }
}

# ── Events ──

function Register-PadForgeDeviceChange {
    <# Runs -Action { param($id, $online) } when a device connects or
       disconnects. #>
    param([Parameter(Mandatory, Position = 0)][scriptblock]$Action, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.OnDevice($Action) }
}

function Register-PadForgeProfile {
    <# Runs -Action { param($name) } when the profile changes, 'default' for
       the default. #>
    param([Parameter(Mandatory, Position = 0)][scriptblock]$Action, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.OnProfile($Action) }
}

function Register-PadForgeEngine {
    <# Runs -Action { param($state) } when the engine starts or stops:
       running, idle, stopping or stopped. #>
    param([Parameter(Mandatory, Position = 0)][scriptblock]$Action, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.OnEngine($Action) }
}

function Register-PadForgeLevel {
    <# Runs -Action { param($level) } when the user changes Automation's
       level. #>
    param([Parameter(Mandatory, Position = 0)][scriptblock]$Action, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.OnLevel($Action) }
}

function Register-PadForgeEvent {
    <# Runs -Action { param($pairs) } for every event of -Kind, with the
       line's key=value words as a hashtable: setting, press and choices for
       a plugin's declared settings. #>
    param([Parameter(Mandatory, Position = 0)][string]$Kind, [Parameter(Mandatory, Position = 1)][scriptblock]$Action, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.OnEvent($Kind, $Action) }
}

# ── Values and commands ──

function Set-PadForgeValue {
    <# A named value Custom Expression macro triggers read. Set again after
       PadForge starts again. #>
    param([Parameter(Mandatory, Position = 0)][string]$Name, [Parameter(Mandatory, Position = 1)]$Value, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.SetValue($Name, $Value) }
}

function Switch-PadForgeProfile {
    param([Parameter(Mandatory, Position = 0)][string]$Name, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.SwitchProfile($Name) }
}

function Reset-PadForgeProfile {
    <# Lets go of the profile a script switched to. #>
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.ReleaseProfile() }
}

function Start-PadForgeEngine {
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Engine($true) }
}

function Stop-PadForgeEngine {
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Engine($false) }
}

function Enable-PadForgeController {
    <# Turns on virtual controller -Number, or all of them. #>
    param([Parameter(Position = 0)][int]$Number = 0, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { if ($Number -gt 0) { $c.Controller($Number, $true) } else { $c.Controllers($true) } }
}

function Disable-PadForgeController {
    <# Turns off virtual controller -Number, or all of them. #>
    param([Parameter(Position = 0)][int]$Number = 0, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { if ($Number -gt 0) { $c.Controller($Number, $false) } else { $c.Controllers($false) } }
}

function Show-PadForge {
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Show() }
}

function Hide-PadForge {
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Hide() }
}

function Save-PadForgeSnapshot {
    <# Saves a diagnostics snapshot, and gives what PadForge says of it. #>
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Snapshot() }
}

function Get-PadForgeState {
    <# What runs now, as JSON text. #>
    param($Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.State() }
}

function Get-PadForgeOutputs {
    <# What virtual controller -Number sends to the game now, as a
       hashtable. #>
    param([Parameter(Mandatory, Position = 0)][int]$Number, $Connection)
    $c = Use-PadForge $Connection
    Invoke-PadForgeCall { $c.Outputs($Number) }
}

function Invoke-PadForgeTool {
    <# Runs an assistant tool, at the tool's own level: Invoke-PadForgeTool
       press '{"id":"..."}' presses what the user could. -Arguments is JSON
       text or a hashtable. Gives JSON text. #>
    param([Parameter(Mandatory, Position = 0)][string]$Name, [Parameter(Position = 1)]$Arguments = '{}', $Connection)
    $c = Use-PadForge $Connection
    $json = if ($Arguments -is [string]) { $Arguments } else { ConvertTo-Json $Arguments -Compress -Depth 8 }
    Invoke-PadForgeCall { $c.Tool($Name, $json) }
}

Export-ModuleMember -Function Connect-PadForge, Connect-PadForgePlugin, Get-PadForgeConnection, Disconnect-PadForge, Wait-PadForge,
    Write-PadForgeLog, Invoke-PadForgeRequest, Get-PadForgeDevice, Add-PadForgeController,
    Register-PadForgeDeviceChange, Register-PadForgeProfile, Register-PadForgeEngine, Register-PadForgeLevel, Register-PadForgeEvent,
    Set-PadForgeValue, Switch-PadForgeProfile, Reset-PadForgeProfile, Start-PadForgeEngine, Stop-PadForgeEngine,
    Enable-PadForgeController, Disable-PadForgeController, Show-PadForge, Hide-PadForge, Save-PadForgeSnapshot,
    Get-PadForgeState, Get-PadForgeOutputs, Invoke-PadForgeTool
