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
            _ => "script",
        };

        public bool IsPlugin => Kind == AutomationClientKind.Plugin;

        /// <summary>Whether hello was answered yes.</summary>
        public bool Opened { get; private set; }

        public int LeaseMs { get; private set; } = DefaultLeaseMs;

        /// <summary>When the program last sent a line.</summary>
        public long LastHeardMs { get; private set; }

        /// <summary>Whether the lease ran out: holds are let go and devices
        /// rest until the program speaks again.</summary>
        public bool Lapsed { get; set; }

        /// <summary>The program's entry in Clients, while it lasts.</summary>
        public AutomationClient Client { get; set; }

        /// <summary>The program as Activity shows it.</summary>
        public AutomationProgram Program => new AutomationProgram(
            string.IsNullOrEmpty(ClaimedName) ? null : ClaimedName, Process.Id, Process.ImageName);

        public CancellationToken Stopping => _cts.Token;

        /// <summary>A feedback event for a device this session owns. Queued
        /// without a wait from whatever thread raised it.</summary>
        public void Feedback(string line) => Outbox.Line(line);

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
            if (Lapsed) Lapsed = false;

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
            await _hub.RunAsync(this, request).ConfigureAwait(false);
        }

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
            if (name.Length == 0 && Process.ImageName != null)
                name = PadForge.Common.Input.RunningProcessList.ShortName(Process.ImageName);
            ClaimedName = name;
            IdentityName = name;

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

            switch (kind)
            {
                case "script":
                    Kind = AutomationClientKind.Script;
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
                    var peer = Peer();
                    _handOver = (stream, stopping) => bridge(peer, stream, stopping);
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
                    IdentityName = ProgramDeviceDeclaration.Clean(grant.Name, ProgramDeviceDeclaration.LongestName);
                    if (ClaimedName.Length == 0) ClaimedName = IdentityName;
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
            Opened = true;
            Answer(request.Tag, HelloAnswer());
        }

        private string HelloAnswer()
            => "ok padforge=" + BuildIdentity.VersionText(BuildIdentity.Version)
               + " protocol=" + AutomationLine.Protocol
               + " level=" + (_hub.Host.IsOn ? AutomationLevels.Word(_hub.Host.Level) : "off")
               + " session=" + Number;

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

    /// <summary>A plugin's token, accepted: the name of the plugin as the
    /// user approved it, which its devices' identity is made from.</summary>
    internal sealed record AutomationPluginGrant(string Name);

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
