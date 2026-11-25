using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// One program's connection on the Automation pipe (#597), from its
    /// <c>hello</c> to its end. It reads one line at a time, answers it
    /// through its outbox, and keeps what the program owns: its devices,
    /// its holds, its values and what it follows, which the sessions hold
    /// for all of them.
    ///
    /// <para>Every connection opens with <c>hello name="..." kind=script</c>
    /// and is answered <c>ok padforge=&lt;version&gt; protocol=1
    /// level=&lt;level&gt; session=&lt;n&gt;</c>. Nothing is guessed from
    /// the first byte. A line runs to 8,192 bytes, a <c>declare</c> line to
    /// 65,536. A program sends at most <see cref="LinesPerSecond"/> lines a
    /// second, well above what a script that drives a device at 1,000 Hz
    /// sends.</para>
    /// </summary>
    internal sealed class AutomationSession
    {
        /// <summary>The steady rate of lines a program may send.</summary>
        internal const int LinesPerSecond = 4000;

        /// <summary>The lease a session that holds or drives something
        /// keeps unless its hello asks for another, and the bounds of what
        /// it may ask.</summary>
        internal const int DefaultLeaseMs = 2000, ShortestLeaseMs = 250, LongestLeaseMs = 10000;

        /// <summary>A script's lease unless its hello asks for another
        /// (#600 item 24): its holds and claims let go 250 ms after it stops
        /// beating. Measured with AutoHotkey 2.0.32, a 50 ms timer fires
        /// every 62.5 ms and a Critical section stops it for as long as it
        /// lasts, so a script that beats every 31 ms keeps it with room to
        /// spare.</summary>
        internal const int ScriptLeaseMs = 250;

        /// <summary>How much of an overlong line is read on before the
        /// connection is given up.</summary>
        internal const int LongestDrained = 1024 * 1024;

        private readonly AutomationSessions _hub;
        private readonly Stream _stream;
        private readonly CancellationTokenSource _cts;
        private readonly Func<long> _nowMs;
        private readonly AutomationLineRate _rate;
        private int _closed;

        internal AutomationSession(AutomationSessions hub, Stream stream, AutomationClientProcess process, int number,
            CancellationToken stopping, Func<long> nowMs)
        {
            _hub = hub;
            _stream = stream;
            Process = process ?? AutomationClientProcess.None;
            Number = number;
            _nowMs = nowMs;
            LastHeardMs = nowMs();
            _rate = new AutomationLineRate(LinesPerSecond, LastHeardMs);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _outer = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Outbox = new AutomationOutbox(stream, () => Close("cut-off"));
        }

        /// <summary>PadForge's number for the session, given in hello's
        /// answer.</summary>
        public int Number { get; }

        public AutomationClientProcess Process { get; }
        public AutomationOutbox Outbox { get; }

        /// <summary>What the program called itself. Its own claim: an
        /// interpreter's image does not name the script it runs.</summary>
        public string ClaimedName { get; private set; } = string.Empty;

        /// <summary>The name the hello gave, alone, without the host an
        /// assistant's copy of PadForge.exe adds to <see cref="ClaimedName"/>.
        /// Null when it gave none.</summary>
        public string GivenName { get; private set; }

        /// <summary>The name a device's identity is made from: the claimed
        /// name for a script, the approved plugin's name for a
        /// plugin.</summary>
        public string IdentityName { get; private set; } = string.Empty;

        public AutomationClientKind Kind { get; private set; } = AutomationClientKind.Script;

        /// <summary>The kind as a device's identity holds it.</summary>
        public string KindWord => Kind switch
        {
            AutomationClientKind.Plugin => "plugin",
            AutomationClientKind.Assistant => "assistant",
            AutomationClientKind.Web => "web",
            _ => "script",
        };

        public bool IsPlugin => Kind == AutomationClientKind.Plugin;

        /// <summary>What PadForge gave a plugin's token: the name it was
        /// approved under and where its <c>log</c> lines go. Null for every
        /// other kind.</summary>
        public AutomationPluginGrant Grant { get; private set; }

        /// <summary>Whether hello was answered yes.</summary>
        public bool Opened { get; private set; }

        public int LeaseMs { get; private set; } = DefaultLeaseMs;

        /// <summary>When the program last sent a line.</summary>
        public long LastHeardMs { get; private set; }

        /// <summary>Whether the lease ran out: holds and claims are let go
        /// and devices rest until the program speaks again.</summary>
        public bool Lapsed { get; set; }

        /// <summary>Whether the script was silent long enough for its
        /// devices to go offline, until it speaks again (#600 item
        /// 24).</summary>
        public bool Asleep { get; set; }

        /// <summary>Taken while the session's devices go offline for its
        /// silence or come back at its next line.</summary>
        internal readonly object SleepGate = new object();

        /// <summary>The program's entry in Clients, while it lasts.</summary>
        public AutomationClient Client { get; set; }

        /// <summary>The level the program's hello asked for (#600), or null
        /// when it named none, which asks for the user's level.</summary>
        internal AutomationLevel? Wanted { get; private set; }

        /// <summary>The level the program has been granted (#600): Commands
        /// until the user allows more. The user's level caps it
        /// (AutomationSessions.LevelOf).</summary>
        internal AutomationLevel Granted { get; set; }

        /// <summary>Who the program is for consent, from what Windows says
        /// of its process (#600).</summary>
        internal AllowedProgram Who { get; set; }

        /// <summary>The user's answer to the program's first connection,
        /// while one is asked for: true when allowed.</summary>
        internal Task<bool> Consent { get; set; }

        /// <summary>The resume secret an assistant's helper holds across a
        /// pipe drop (#600), or null.</summary>
        internal string Resume { get; private set; }

        /// <summary>What an assistant's undo, rate and waiting requests are
        /// kept by (#600): the first session of its resume secret, else this
        /// session's number.</summary>
        internal int CallerKey { get; set; }

        /// <summary>The program as Activity shows it.</summary>
        public AutomationProgram Program => new AutomationProgram(
            string.IsNullOrEmpty(ClaimedName) ? null : ClaimedName, Process.Id, Process.ImageName,
            Process.ImagePath, Process.Signer);

        public CancellationToken Stopping => _cts.Token;

        /// <summary>Canceled when the connection ends, however it ends. An
        /// assistant's session hands its stream over and its reader's token
        /// ends then, so what lasts as long as the connection waits on this
        /// one (#597).</summary>
        internal CancellationToken Lifetime => _outer.Token;

        /// <summary>A feedback event for a device this session owns. Queued
        /// without a wait from whatever thread raised it.</summary>
        public void Feedback(string line) => Outbox.Line(line);

        /// <summary>The program is still there. An assistant's session calls
        /// it while its protocol runs, since its lines no longer pass the
        /// reader that keeps the lease (#597).</summary>
        internal void Heard()
        {
            LastHeardMs = _nowMs();
            Outbox.Heard();
            if (Lapsed || Asleep) _hub.Woke(this);
        }

        /// <summary>Runs <paramref name="work"/> with the rights of the
        /// program at the other end of the pipe, so a file a tool opens for
        /// it is one the program could open itself. False when the
        /// connection is no pipe of Windows' and has no rights to borrow.</summary>
        /// <summary>While a tool runs a line of the session's on its behalf
        /// (AutomationSessions.RunCapturedAsync), the answers it gets, and
        /// one run at a time. Null otherwise.</summary>
        internal volatile List<string> Captured;
        internal readonly SemaphoreSlim CaptureGate = new SemaphoreSlim(1, 1);

        /// <summary>The tag the lines a tool runs carry, so their answers are
        /// told from the session's events.</summary>
        internal const string CaptureTag = "@pf";

        /// <summary>A line written to an assistant's session once its
        /// stream is the protocol's: an answer to a line a tool runs is
        /// kept, anything else is dropped.</summary>
        internal void Sink(string text)
        {
            var list = Captured;
            if (list == null || text == null || !text.StartsWith(CaptureTag + " ", StringComparison.Ordinal)) return;
            lock (list) list.Add(text.Substring(CaptureTag.Length + 1));
        }

        internal bool RunAsClient(Action work)
        {
            if (_stream is not System.IO.Pipes.NamedPipeServerStream pipe) return false;
            pipe.RunAsClient(() => work());
            return true;
        }

        // ── Reading ──

        /// <summary>Runs the connection to its end.</summary>
        public async Task RunAsync()
        {
            var token = _cts.Token;
            var writer = Outbox.RunAsync(token);
            byte[] leftover = null;
            try
            {
                leftover = await ReadLinesAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (_handOver == null)
            {
                // A session that closes after its last line waits for the
                // writer to put it in the pipe first, a second at most.
                if (_closing) await Task.WhenAny(_ended.Task, Task.Delay(1500)).ConfigureAwait(false);
                _hub.End(this);
            }

            if (_handOver != null)
            {
                // An assistant's connection: the ok is written, the writer
                // stops, and the rest of the stream is the delegate's.
                // A program that never reads its ok is not waited on for good.
                await Task.WhenAny(_handOverReady.Task, Task.Delay(5000)).ConfigureAwait(false);
                _cts.Cancel();
                try { await writer.ConfigureAwait(false); } catch { }
                try
                {
                    await _handOver(new PrefixedStream(leftover ?? Array.Empty<byte>(), _stream), _outer.Token).ConfigureAwait(false);
                }
                catch { /* the assistant's session is its own */ }
                finally
                {
                    _hub.End(this);
                }
                return;
            }

            _cts.Cancel();
            try { await writer.ConfigureAwait(false); } catch { }
        }

        // Canceled when the pipe stops or the session is closed. It
        // outlives the reader's own, which an assistant's hand-over ends.
        private readonly CancellationTokenSource _outer;

        private Func<Stream, CancellationToken, Task> _handOver;
        private readonly TaskCompletionSource<bool> _handOverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Reads lines until the pipe closes or the session is
        /// closed. Returns the bytes read past an assistant's hello, which
        /// belong to its protocol.</summary>
        private async Task<byte[]> ReadLinesAsync(CancellationToken token)
        {
            var buffer = new byte[16384];
            var line = new byte[1024];
            int kept = 0, limit = AutomationLine.LongestLine;
            long drained = 0;
            bool over = false;
            while (!token.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (n == 0) return null;
                for (int i = 0; i < n; i++)
                {
                    byte c = buffer[i];
                    if (c == (byte)'\n')
                    {
                        string text = Encoding.UTF8.GetString(line, 0, kept);
                        bool tooLong = over;
                        kept = 0;
                        over = false;
                        limit = AutomationLine.LongestLine;
                        drained = 0;
                        await HandleAsync(text, tooLong).ConfigureAwait(false);
                        if (_handOver != null)
                        {
                            var rest = new byte[n - i - 1];
                            Array.Copy(buffer, i + 1, rest, 0, rest.Length);
                            return rest;
                        }
                        if (_closing) return null;
                        continue;
                    }
                    if (kept < limit)
                    {
                        if (kept == line.Length) Array.Resize(ref line, Math.Min(limit, line.Length * 2));
                        line[kept++] = c;
                        // A declare line may run longer, since its JSON names
                        // every control of a device.
                        if (kept == AutomationLine.LongestLine && limit == AutomationLine.LongestLine && IsDeclare(line, kept))
                            limit = AutomationLine.LongestDeclare;
                    }
                    else
                    {
                        over = true;
                        if (++drained > LongestDrained)
                        {
                            Close("too-long");
                            return null;
                        }
                    }
                }
            }
            return null;
        }

        private static bool IsDeclare(byte[] line, int length)
        {
            int i = 0;
            while (i < length && line[i] == ' ') i++;
            if (i < length && line[i] == '@')
            {
                while (i < length && line[i] != ' ') i++;
                while (i < length && line[i] == ' ') i++;
            }
            const string verb = "declare ";
            if (length - i < verb.Length) return false;
            for (int k = 0; k < verb.Length; k++)
                if (char.ToLowerInvariant((char)line[i + k]) != verb[k]) return false;
            return true;
        }

        private bool _judged;

        private async Task HandleAsync(string text, bool tooLong)
        {
            // Who the client is, decided from its token once its first line
            // is in: Windows lets a server impersonate only after a read.
            if (!_judged)
            {
                _judged = true;
                string refusal = _hub.Judge(_stream as System.IO.Pipes.NamedPipeServerStream);
                if (refusal != null)
                {
                    Answer(null, AutomationLine.Error("refused", "PadForge does not take this program's connection: " + refusal));
                    CloseAfterWritten();
                    return;
                }
            }
            if (!_rate.Take(_nowMs()))
            {
                Answer(TagOf(text), AutomationLine.Error("rate-limited"));
                return;
            }
            LastHeardMs = _nowMs();
            Outbox.Heard();
            if (Lapsed || Asleep) _hub.Woke(this);

            if (tooLong)
            {
                Answer(TagOf(text), AutomationLine.Error("too-long"));
                return;
            }
            // A blank line asks nothing and keeps the lease.
            if (string.IsNullOrWhiteSpace(text)) return;

            var request = AutomationLine.Parse(text);
            if (!Opened)
            {
                if (request.Error == null && request.Verb == "hello")
                {
                    Hello(request);
                    return;
                }
                Answer(request.Tag, AutomationLine.Error("hello-first"));
                CloseAfterWritten();
                return;
            }
            if (request.Error != null)
            {
                Answer(request.Tag, AutomationLine.Error(request.Error));
                return;
            }
            // While a line runs the program waits on PadForge, and the lines
            // it sent behind it, its beats among them, wait in the pipe: the
            // lease does not run out meanwhile (#600 item 24).
            Interlocked.Increment(ref _handling);
            try
            {
                await _hub.RunAsync(this, request).ConfigureAwait(false);
            }
            finally
            {
                LastHeardMs = _nowMs();
                Interlocked.Decrement(ref _handling);
            }
        }

        private int _handling;

        /// <summary>Whether a line of the program's runs now.</summary>
        internal bool Handling => Volatile.Read(ref _handling) > 0;

        private static string TagOf(string text)
        {
            text = text?.TrimStart() ?? string.Empty;
            if (!text.StartsWith("@", StringComparison.Ordinal)) return null;
            int end = text.IndexOf(' ');
            string tag = end < 0 ? text : text.Substring(0, end);
            return tag.Length <= AutomationLine.LongestTag + 1 ? tag : null;
        }

        // ── hello ──

        private void Hello(AutomationRequest request)
        {
            string protocol = request.Get("protocol");
            if (protocol != null && protocol != "1")
            {
                Answer(request.Tag, AutomationLine.Error("unsupported-protocol"));
                CloseAfterWritten();
                return;
            }

            string kind = (request.Get("kind") ?? "script").ToLowerInvariant();
            string name = ProgramDeviceDeclaration.Clean(request.Get("name"), ProgramDeviceDeclaration.LongestName);
            string given = name;
            // The copy of PadForge.exe an assistant starts names the program
            // that started it, its host (#597). Both names are claims and
            // are shown as such. Clean takes the format characters out, so
            // neither name can read backward or hide part of itself on the
            // request card and in Activity.
            string host = kind == "mcp" ? ProgramDeviceDeclaration.Clean(request.Get("host"), ProgramDeviceDeclaration.LongestName) : string.Empty;
            if (host.Length > 0) name = name.Length > 0 ? name + " (" + host + ")" : host;
            if (name.Length == 0 && Process.ImageName != null)
                name = ProgramDeviceDeclaration.Clean(PadForge.Common.Input.RunningProcessList.ShortName(Process.ImageName),
                    ProgramDeviceDeclaration.LongestName);
            ClaimedName = name;
            IdentityName = name;
            GivenName = given.Length > 0 ? given : null;

            string lease = request.Get("lease");
            if (lease != null)
            {
                if (!AutomationLine.TryInt(lease, out int ms) || ms < ShortestLeaseMs || ms > LongestLeaseMs)
                {
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "lease is 250 to 10000 ms"));
                    return;
                }
                LeaseMs = ms;
            }

            // Plain units (#600): what the session reads comes as the
            // libraries and the assistant tools speak it.
            string units = request.Get("units");
            if (units != null)
            {
                if (units is not ("plain" or "raw"))
                {
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "units is plain or raw"));
                    return;
                }
                Outbox.Plain = units == "plain";
            }

            // The level the program asks for (#600), which the question
            // card names.
            string level = request.Get("level");
            if (level != null)
            {
                if (!AutomationConsent.TryLevel(level, out var wanted))
                {
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "level is commands, read, send-inputs or change-settings"));
                    return;
                }
                Wanted = wanted;
            }

            // An assistant's helper keeps one secret for its whole run, so
            // its undo, rate and waiting requests outlive a pipe drop.
            string resume = request.Get("resume");
            if (resume != null)
            {
                if (resume.Length < 16 || resume.Length > 64 || !IsSecret(resume))
                {
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "resume is 16 to 64 letters and digits"));
                    return;
                }
                Resume = resume;
            }

            string hwnd = request.Get("hwnd");
            if (hwnd != null)
            {
                if (!TryWindow(hwnd, out IntPtr window) || !Process.OwnsWindow(window))
                {
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "hwnd is a window of the connecting program"));
                    return;
                }
                Outbox.Process = Process;
                Outbox.WakeWindow = window;
            }

            // A script and an assistant speak while Automation is on. With
            // Plugins on alone, the pipe serves plugins and nothing else, so
            // no program takes a plugin's place, and an assistant is told so
            // before it takes a session's place: the copy it started then
            // says Automation is off. This is the one check of it.
            if (kind is "script" or "mcp" or "web" && !_hub.Host.IsOn)
            {
                Answer(request.Tag, AutomationLine.Error("not-allowed", "Automation is off, and the pipe serves plugins alone"));
                CloseAfterWritten();
                return;
            }

            switch (kind)
            {
                case "script":
                    Kind = AutomationClientKind.Script;
                    if (lease == null) LeaseMs = ScriptLeaseMs;
                    break;
                case "mcp":
                {
                    var bridge = _hub.Host.McpSession;
                    if (bridge == null)
                    {
                        Answer(request.Tag, AutomationLine.Error("not-available", "the assistant connection is not in this build"));
                        CloseAfterWritten();
                        return;
                    }
                    Kind = AutomationClientKind.Assistant;
                    if (!_hub.Open(this, out string full))
                    {
                        Answer(request.Tag, AutomationLine.Error(full));
                        CloseAfterWritten();
                        return;
                    }
                    _hub.BeginConsent(this);
                    var peer = Peer();
                    _handOver = (stream, stopping) => bridge(peer, stream, stopping);
                    Opened = true;
                    Answer(request.Tag, HelloAnswer(), () => _handOverReady.TrySetResult(true));
                    return;
                }
                case "web-helper":
                {
                    // The web helper PadForge started (#600 item 11), by its
                    // one-time key from that very process. Its pipe carries
                    // the web door's own lines, and it is no program of the
                    // connected list.
                    var door = PadForge.Services.Automation.Web.WebDoor.Of(_hub.Host);
                    if (door == null || !door.AcceptHelper(request.Get("token"), Process.Id))
                    {
                        Answer(request.Tag, AutomationLine.Error("denied", "PadForge takes only the web helper it started"));
                        CloseAfterWritten();
                        return;
                    }
                    _handOver = (stream, stopping) => door.ServeHelperAsync(stream, stopping);
                    _hub.Settled(this);
                    Opened = true;
                    Answer(request.Tag, "ok padforge=" + BuildIdentity.VersionText(BuildIdentity.Version) + " protocol=" + AutomationLine.Protocol + " web=helper",
                        () => _handOverReady.TrySetResult(true));
                    return;
                }
                case "web":
                {
                    // One web assistant through that helper, by the grant
                    // the user allowed. It is named by the address its
                    // sign-in returned to, and the name it gave is its claim.
                    var door = PadForge.Services.Automation.Web.WebDoor.Of(_hub.Host);
                    var grant = door?.AcceptWeb(request.Get("token"), Process.Id, request.Get("grant"));
                    var bridge = _hub.Host.McpSession;
                    if (grant == null || bridge == null)
                    {
                        Answer(request.Tag, AutomationLine.Error("denied", "no such web assistant"));
                        CloseAfterWritten();
                        return;
                    }
                    Kind = AutomationClientKind.Web;
                    ClaimedName = ProgramDeviceDeclaration.Clean(grant.Origin, ProgramDeviceDeclaration.LongestName);
                    IdentityName = ClaimedName;
                    GivenName = string.IsNullOrEmpty(grant.Name) ? null : grant.Name;
                    Who = new AllowedProgram(PadForge.Services.Automation.Web.WebDoor.WebKey(grant.Id), ClaimedName, "web", null, null, null,
                        AutomationLevel.ChangeSettings);
                    if (!_hub.Open(this, out string full))
                    {
                        Answer(request.Tag, AutomationLine.Error(full));
                        CloseAfterWritten();
                        return;
                    }
                    _hub.BeginWeb(this);
                    var webPeer = Peer();
                    _handOver = (stream, stopping) => bridge(webPeer, stream, stopping);
                    Opened = true;
                    Answer(request.Tag, HelloAnswer(), () => _handOverReady.TrySetResult(true));
                    return;
                }
                case "plugin":
                {
                    var accept = _hub.Host.AcceptPluginToken;
                    if (accept == null)
                    {
                        Answer(request.Tag, AutomationLine.Error("not-available", "plugins are not in this build"));
                        CloseAfterWritten();
                        return;
                    }
                    var grant = accept(request.Get("token"), Process.Id);
                    if (grant == null)
                    {
                        Answer(request.Tag, AutomationLine.Error("denied"));
                        CloseAfterWritten();
                        return;
                    }
                    Kind = AutomationClientKind.Plugin;
                    Grant = grant;
                    IdentityName = ProgramDeviceDeclaration.Clean(grant.Name, ProgramDeviceDeclaration.LongestName);
                    if (ClaimedName.Length == 0) ClaimedName = IdentityName;
                    if (given.Length > 0)
                    {
                        try { grant.Named?.Invoke(given); }
                        catch { /* the name is for the Plugins page, and the session goes on */ }
                    }
                    break;
                }
                default:
                    Answer(request.Tag, AutomationLine.Error("bad-argument", "kind is script, mcp or plugin"));
                    return;
            }

            if (!_hub.Open(this, out string reason))
            {
                Answer(request.Tag, AutomationLine.Error(reason));
                CloseAfterWritten();
                return;
            }
            _hub.BeginConsent(this);
            Opened = true;
            Answer(request.Tag, HelloAnswer());
        }

        private static bool IsSecret(string text)
        {
            foreach (char c in text)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_')) return false;
            return true;
        }

        /// <summary>hello's answer: the level the program works at now, and
        /// while the user is asked, the level it asked for (#600).</summary>
        private string HelloAnswer()
            => "ok padforge=" + BuildIdentity.VersionText(BuildIdentity.Version)
               + " protocol=" + AutomationLine.Protocol
               + " level=" + (_hub.Host.IsOn ? AutomationLevels.Word(_hub.LevelOf(this)) : "off")
               + (_hub.ConsentWaits(this) ? " wants=" + AutomationLevels.Word(Wanted ?? _hub.Host.Level) + " consent=waiting" : string.Empty)
               + " session=" + Number
               + (Outbox.Plain ? " units=plain" : string.Empty);

        /// <summary>The session as a later lane's delegates are told of
        /// it.</summary>
        internal AutomationPeer Peer()
            => new AutomationPeer(Number, ClaimedName, Kind, Process.Id, Process.ImageName);

        private static bool TryWindow(string text, out IntPtr window)
        {
            window = IntPtr.Zero;
            long value;
            bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!ok || value == 0) return false;
            window = new IntPtr(value);
            return true;
        }

        // ── Answers ──

        /// <summary>One line of an answer, with the request's tag.</summary>
        public void Answer(string tag, string line, Action after = null)
            => Outbox.Line(AutomationLine.Tagged(tag, line), after);

        // ── Closing ──

        private volatile bool _closing;

        /// <summary>Closes once what waits is written, or after a second
        /// when the program does not read.</summary>
        public void CloseAfterWritten()
        {
            _closing = true;
            Outbox.Line(null, () => Close("closed"));
            _ = Task.Delay(1000).ContinueWith(_ => Close("closed"), TaskScheduler.Default);
        }

        private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ends the session now.</summary>
        public void Close(string reason)
        {
            _closing = true;
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _ended.TrySetResult(true);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            try { _outer.Cancel(); } catch (ObjectDisposedException) { }
            try { _stream.Dispose(); } catch { }
        }

        /// <summary>A stream that gives back bytes already read before the
        /// rest of the pipe: what an assistant sent right after its hello
        /// belongs to it.</summary>
        private sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Stream _inner;
            private int _at;

            public PrefixedStream(byte[] prefix, Stream inner)
            {
                _prefix = prefix;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_at < _prefix.Length)
                {
                    int n = Math.Min(count, _prefix.Length - _at);
                    Array.Copy(_prefix, _at, buffer, offset, n);
                    _at += n;
                    return n;
                }
                return _inner.Read(buffer, offset, count);
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_at < _prefix.Length)
                {
                    int n = Math.Min(buffer.Length, _prefix.Length - _at);
                    _prefix.AsMemory(_at, n).CopyTo(buffer);
                    _at += n;
                    return n;
                }
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.WriteAsync(buffer, cancellationToken);

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _inner.WriteAsync(buffer, offset, count, cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>A session as the delegates a later lane sets on the host are
    /// told of it.</summary>
    /// <param name="Session">PadForge's number for it.</param>
    /// <param name="Name">What the program called itself: its own
    /// claim.</param>
    /// <param name="Kind">What kind of program it said it is.</param>
    /// <param name="ProcessId">The process at the other end, for
    /// display.</param>
    /// <param name="ImageName">That process's image, for display.</param>
    internal sealed record AutomationPeer(int Session, string Name, AutomationClientKind Kind, int ProcessId, string ImageName);

    /// <summary>A plugin's token, accepted.</summary>
    /// <param name="Name">The name of the plugin as the user approved it,
    /// which its devices' identity is made from.</param>
    /// <param name="Log">Takes each line the plugin writes with
    /// <c>log</c>, for its log on the Plugins page. Called on the session's
    /// worker. Null keeps no log.</param>
    /// <param name="Named">Takes the name the plugin's hello gave, when it
    /// gave one, for its row on the Plugins page. Called once, on the
    /// session's worker.</param>
    internal sealed record AutomationPluginGrant(string Name, Action<string> Log = null, Action<string> Named = null);

    /// <summary>A <c>call</c> line: an assistant tool run from a
    /// script.</summary>
    /// <param name="Peer">The session that asked.</param>
    /// <param name="Tool">The tool's name.</param>
    /// <param name="Arguments">The JSON object the line ended with, or
    /// <c>{}</c>.</param>
    internal sealed record AutomationCall(AutomationPeer Peer, string Tool, string Arguments);

    /// <summary>What a call came to: <c>ok</c> with one line of JSON, or an
    /// error's code and words.</summary>
    internal sealed record AutomationCallResult(bool Ok, string Json, string Code = null, string Words = null)
    {
        public static AutomationCallResult Success(string json) => new(true, json);
        public static AutomationCallResult Failure(string code, string words = null) => new(false, null, code, words);
    }
}
