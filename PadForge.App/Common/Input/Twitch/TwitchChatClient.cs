using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Common.Input.Twitch
{
    /// <summary>One IRC line from Twitch chat, IRCv3 tags included:
    /// "@tags :prefix COMMAND params :trailing".</summary>
    internal sealed class IrcLine
    {
        public IReadOnlyDictionary<string, string> Tags { get; private init; } = new Dictionary<string, string>();
        public string Prefix { get; private init; } = string.Empty;
        public string Command { get; private init; } = string.Empty;
        public IReadOnlyList<string> Params { get; private init; } = Array.Empty<string>();
        public string Trailing { get; private init; }

        /// <summary>The nick in the prefix ("name" from "name!name@host").</summary>
        public string Nick
        {
            get
            {
                int bang = Prefix.IndexOf('!');
                return bang > 0 ? Prefix.Substring(0, bang) : Prefix;
            }
        }

        public string Tag(string key) => Tags.TryGetValue(key, out var v) ? v : string.Empty;

        /// <summary>Parses a line, or returns null for an empty one.</summary>
        public static IrcLine Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            int i = 0;
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (line[0] == '@')
            {
                int end = line.IndexOf(' ');
                if (end < 0) return null;
                foreach (string pair in line.Substring(1, end - 1).Split(';'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string key = eq < 0 ? pair : pair.Substring(0, eq);
                    tags[key] = eq < 0 ? string.Empty : Unescape(pair.Substring(eq + 1));
                }
                i = end + 1;
            }
            while (i < line.Length && line[i] == ' ') i++;
            string prefix = string.Empty;
            if (i < line.Length && line[i] == ':')
            {
                int end = line.IndexOf(' ', i);
                if (end < 0) return null;
                prefix = line.Substring(i + 1, end - i - 1);
                i = end + 1;
            }
            string trailing = null;
            int colon = line.IndexOf(" :", i, StringComparison.Ordinal);
            string middle;
            if (colon >= 0)
            {
                trailing = line.Substring(colon + 2);
                middle = line.Substring(i, colon - i);
            }
            else
            {
                middle = line.Substring(i);
            }
            string[] words = middle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;
            var parameters = new string[words.Length - 1];
            Array.Copy(words, 1, parameters, 0, parameters.Length);
            return new IrcLine
            {
                Tags = tags,
                Prefix = prefix,
                Command = words[0].ToUpperInvariant(),
                Params = parameters,
                Trailing = trailing,
            };
        }

        // IRCv3 tag value escapes: \: is ";", \s a space, \\ a backslash,
        // \r and \n their characters.
        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { if (c != '\\') sb.Append(c); continue; }
                char n = value[++i];
                sb.Append(n switch { ':' => ';', 's' => ' ', 'r' => '\r', 'n' => '\n', _ => n });
            }
            return sb.ToString();
        }
    }

    /// <summary>The socket the client talks through: a WebSocket in the app,
    /// a scripted one in the tests.</summary>
    internal interface IChatSocket : IDisposable
    {
        Task ConnectAsync(Uri uri, CancellationToken ct);
        Task SendAsync(string line, CancellationToken ct);
        /// <summary>The next text message, or null once the socket closes.</summary>
        Task<string> ReceiveAsync(CancellationToken ct);
    }

    internal sealed class WebChatSocket : IChatSocket
    {
        private readonly ClientWebSocket _socket = new();
        private readonly byte[] _buffer = new byte[8192];

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);

        public Task SendAsync(string line, CancellationToken ct)
            => _socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, true, ct);

        public async Task<string> ReceiveAsync(CancellationToken ct)
        {
            using var message = new System.IO.MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(_buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(_buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    /// <summary>Where the client stands, for the Dashboard's status line.</summary>
    internal enum TwitchChatState { Connecting, Reading, Retrying, NoChat }

    /// <summary>
    /// Reads a Twitch channel's chat (issue #555). It connects to the secure
    /// WebSocket address on Twitch's chat IRC page, logs in anonymously as
    /// tmi.js does (a "justinfan" nick and the password "SCHMOOPIIE",
    /// ClientBase.js 1170 and 1190, the login DougDoug's TwitchPlays template
    /// uses too), asks for the tags and commands capabilities, answers each
    /// PING with a PONG carrying its text, and joins the channel. It reads and
    /// never posts.
    ///
    /// <para>A RECONNECT, a closed socket or a silent one is met by
    /// connecting again, at once after a RECONNECT and after a delay otherwise.
    /// The delay doubles to 30 seconds and starts over once a session has
    /// joined the channel, as tmi.js does on connecting (ClientBase.js,
    /// reconnections and reconnectTimer). A connection that never finishes
    /// counts as a failed one after <see cref="ConnectMs"/>.</para>
    ///
    /// <para>The client sends a PING of its own each minute whether or not
    /// chat is moving (tmi.js's pingLoop, ClientBase.js), and Twitch answers
    /// it, so a quiet channel stays connected and a link that dies without a
    /// word is noticed <see cref="SilenceMs"/> after the last thing it sent.
    /// The wait for that PING races the pending receive and never cancels it,
    /// because canceling a receive ends a ClientWebSocket.</para>
    ///
    /// <para>Twitch drops a JOIN for a channel that does not exist without a
    /// word, and answers one for a suspended channel with a NOTICE. Neither is
    /// a failed connection, so the session stays up, reads as
    /// <see cref="TwitchChatState.NoChat"/>, and asks again each minute.</para>
    ///
    /// <para>Twitch can send a message twice. Each carries an "id" tag, and a
    /// message whose id was seen among the last <see cref="RememberedIds"/>
    /// is dropped.</para>
    /// </summary>
    internal sealed class TwitchChatClient : IDisposable
    {
        public const string Host = "irc-ws.chat.twitch.tv";
        public static readonly Uri Endpoint = new("wss://" + Host + ":443");
        public const int KeepAliveMs = 60_000;
        public const int SilenceMs = 90_000;
        public const int MaxRetryMs = 30_000;
        public const int FirstRetryMs = 1000;
        public const int ConnectMs = 15_000;
        public const int JoinAnswerMs = 10_000;
        public const int RememberedIds = 256;

        // The NOTICE ids tmi.js lists for a channel that cannot be read
        // (ClientBase.js, "Permission error"). The others it lists there are
        // about the sender's own rights, and this client sends no message.
        private static readonly HashSet<string> s_noChatNotices = new(StringComparer.Ordinal)
        {
            "msg_channel_suspended", "msg_room_not_found", "tos_ban",
        };

        /// <summary>Test seams: the periods, shorter under test.</summary>
        internal int KeepAlivePeriodMs { get; init; } = KeepAliveMs;
        internal int SilencePeriodMs { get; init; } = SilenceMs;
        internal int ConnectPeriodMs { get; init; } = ConnectMs;
        internal int JoinAnswerPeriodMs { get; init; } = JoinAnswerMs;

        private readonly string _channel;
        private readonly Func<IChatSocket> _newSocket;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _lock = new();
        private Task _loop;
        private TwitchChatState _state = TwitchChatState.Connecting;
        private string _problem = string.Empty;
        private int _statusVersion;
        private bool _joined;
        private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new();

        /// <summary>A chat message: the sender's login, the name Twitch shows
        /// for them, whether a moderator or the broadcaster sent it, whether
        /// the broadcaster did, and its text.</summary>
        public event Action<string, string, bool, bool, string> Message;

        public TwitchChatClient(string channel, Func<IChatSocket> newSocket = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _newSocket = newSocket ?? (() => new WebChatSocket());
        }

        public string Channel => _channel;

        public TwitchChatState State { get { lock (_lock) return _state; } }

        /// <summary>Why the last connection ended, empty while reading.</summary>
        public string Problem { get { lock (_lock) return _problem; } }

        public int StatusVersion { get { lock (_lock) return _statusVersion; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_loop != null) return;
                _loop = Task.Run(() => RunAsync(SessionAsync, Task.Delay, _stop.Token));
            }
        }

        private void SetState(TwitchChatState state, string problem)
        {
            lock (_lock)
            {
                if (_state == state && _problem == problem) return;
                _state = state;
                _problem = problem ?? string.Empty;
                _statusVersion++;
            }
        }

        /// <summary>The loop around the sessions. The tests pass their own
        /// session and delay.</summary>
        internal async Task RunAsync(Func<CancellationToken, Task<bool>> session,
            Func<int, CancellationToken, Task> wait, CancellationToken ct)
        {
            int delay = FirstRetryMs;
            while (!ct.IsCancellationRequested)
            {
                SetState(TwitchChatState.Connecting, Problem);
                bool reconnectNow = false;
                string problem = string.Empty;
                _joined = false;
                try
                {
                    reconnectNow = await session(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                    if (problem != Problem)
                        PadForge.Engine.SdlDiagLog.WriteLine($"Twitch chat: #{_channel}: {problem}");
                }
                if (ct.IsCancellationRequested) break;
                // A session that joined was a healthy one, however it ended.
                if (reconnectNow || _joined) delay = FirstRetryMs;
                SetState(TwitchChatState.Retrying, problem);
                if (reconnectNow) continue;
                try { await wait(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delay = Math.Min(delay * 2, MaxRetryMs);
            }
        }

        // One connection. True when Twitch asked for a reconnect, which comes
        // at once.
        private async Task<bool> SessionAsync(CancellationToken ct)
        {
            if (PadForge.Engine.Common.OutboundNetworkGate.RefusesHost(Host))
                throw new InvalidOperationException("Twitch could not be reached.");
            using var socket = _newSocket();
            return await ReadAsync(socket, ct).ConfigureAwait(false);
        }

        /// <summary>Logs in on a socket and reads it until it ends. The tests
        /// call it with a scripted socket.</summary>
        internal async Task<bool> ReadAsync(IChatSocket socket, CancellationToken ct)
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connecting.CancelAfter(ConnectPeriodMs);
                try { await socket.ConnectAsync(Endpoint, connecting.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Twitch did not answer the connection.");
                }
            }
            await socket.SendAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", ct).ConfigureAwait(false);
            await socket.SendAsync("PASS SCHMOOPIIE", ct).ConfigureAwait(false);
            string nick = "justinfan" + Random.Shared.Next(10000, 100000);
            await socket.SendAsync("NICK " + nick, ct).ConfigureAwait(false);
            await socket.SendAsync("JOIN #" + _channel, ct).ConfigureAwait(false);

            long lastPing = Environment.TickCount64;
            long lastFrame = lastPing;
            long askAgainAt = lastPing + JoinAnswerPeriodMs;
            Task<string> receiving = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    receiving ??= socket.ReceiveAsync(ct);
                    if (!receiving.IsCompleted)
                    {
                        long now = Environment.TickCount64;
                        if (now - lastFrame >= SilencePeriodMs)
                            throw new TimeoutException("Twitch stopped answering.");
                        if (now - lastPing >= KeepAlivePeriodMs)
                        {
                            await socket.SendAsync("PING :tmi.twitch.tv", ct).ConfigureAwait(false);
                            lastPing = now;
                        }
                        if (!_joined && now >= askAgainAt)
                        {
                            SetState(TwitchChatState.NoChat, string.Empty);
                            await socket.SendAsync("JOIN #" + _channel, ct).ConfigureAwait(false);
                            askAgainAt = now + KeepAlivePeriodMs;
                        }
                        long wake = Math.Min(lastPing + KeepAlivePeriodMs, lastFrame + SilencePeriodMs);
                        if (!_joined) wake = Math.Min(wake, askAgainAt);
                        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        await Task.WhenAny(receiving, Task.Delay((int)Math.Max(1, wake - now), waiting.Token)).ConfigureAwait(false);
                        waiting.Cancel();
                        if (!receiving.IsCompleted) continue;
                    }
                    string frame = await receiving.ConfigureAwait(false);
                    receiving = null;
                    lastFrame = Environment.TickCount64;
                    if (frame == null) throw new InvalidOperationException("Twitch closed the connection.");
                    foreach (string raw in frame.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        var outcome = Handle(IrcLine.Parse(raw), nick, out string reply);
                        if (reply != null) await socket.SendAsync(reply, ct).ConfigureAwait(false);
                        if (outcome == LineOutcome.Joined) _joined = true;
                        if (outcome == LineOutcome.Reconnect) return true;
                    }
                }
                return false;
            }
            finally
            {
                // A receive left pending ends when the socket closes, and its
                // error has nobody else to read it.
                if (receiving != null)
                    _ = receiving.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        internal enum LineOutcome { None, Joined, Reconnect, Message }

        /// <summary>Acts on one line. <paramref name="reply"/> is a line to
        /// send back, the PONG for a PING.</summary>
        internal LineOutcome Handle(IrcLine line, string nick, out string reply)
        {
            reply = null;
            if (line == null) return LineOutcome.None;
            switch (line.Command)
            {
                case "PING":
                    reply = "PONG :" + (line.Trailing ?? (line.Params.Count > 0 ? line.Params[0] : "tmi.twitch.tv"));
                    return LineOutcome.None;
                case "RECONNECT":
                    return LineOutcome.Reconnect;
                case "JOIN":
                    if (string.Equals(line.Nick, nick, StringComparison.OrdinalIgnoreCase))
                    {
                        SetState(TwitchChatState.Reading, string.Empty);
                        return LineOutcome.Joined;
                    }
                    return LineOutcome.None;
                case "NOTICE":
                    if (s_noChatNotices.Contains(line.Tag("msg-id")))
                        SetState(TwitchChatState.NoChat, string.Empty);
                    return LineOutcome.None;
                case "PRIVMSG":
                    if (line.Trailing == null || !FirstTimeSeen(line.Tag("id"))) return LineOutcome.None;
                    // The login identifies the sender. A display name can be
                    // a translation of it (ComfyJS keeps both, app.js 1037).
                    string user = line.Tag("display-name");
                    if (user.Length == 0) user = line.Nick;
                    // ComfyJS's test (app.js 1039): the sender's login, the
                    // nick tmi.js reads from the prefix (ClientBase.js, the
                    // PRIVMSG case), is the channel's name, which only the
                    // channel's own account has. ForgePlay counts the
                    // broadcaster as a moderator (ChatMessage.js 22 to 28).
                    bool broadcaster = string.Equals(line.Nick, _channel, StringComparison.OrdinalIgnoreCase);
                    bool moderator = broadcaster || line.Tag("mod") == "1";
                    Message?.Invoke(line.Nick, user, moderator, broadcaster, line.Trailing);
                    return LineOutcome.Message;
                default:
                    return LineOutcome.None;
            }
        }

        // True for a message id not seen lately, and for a message with none.
        private bool FirstTimeSeen(string id)
        {
            if (id.Length == 0) return true;
            if (!_seenIds.Add(id)) return false;
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > RememberedIds) _seenIds.Remove(_seenOrder.Dequeue());
            return true;
        }

        /// <summary>A channel as typed, a name or its twitch.tv address, as
        /// Twitch names it, or empty when it cannot be one: 1 to 25 letters,
        /// digits and underscores, lower-cased.</summary>
        public static string NormalizeChannel(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return string.Empty;
            string c = channel.Trim();
            foreach (string lead in new[] { "https://", "http://" })
                if (c.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) c = c.Substring(lead.Length);
            foreach (string lead in new[] { "www.", "m.", "twitch.tv/" })
                if (c.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) c = c.Substring(lead.Length);
            c = c.TrimStart('#').TrimEnd('/').ToLowerInvariant();
            if (c.Length == 0 || c.Length > 25) return string.Empty;
            foreach (char ch in c)
                if (!(ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch == '_')) return string.Empty;
            return c;
        }

        /// <summary>Stops reading. The row closes on the poll thread, so the
        /// wait is a short courtesy, the Game Telemetry row's rule: a
        /// canceled receive ends the loop at once, and a message that comes
        /// after is dropped by the row.</summary>
        public void Dispose()
        {
            _stop.Cancel();
            Task loop;
            lock (_lock) loop = _loop;
            try { loop?.Wait(50); } catch { }
        }
    }
}
