using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Plugins;

namespace PadForge.Services.Automation.Web
{
    /// <summary>Where the web helper stands, as the page says it.</summary>
    internal enum WebHelperState
    {
        Off,
        Starting,
        Running,
        /// <summary>It could not start, or it ended and was not started again.</summary>
        Stopped,

        /// <summary>The switch is on and Automation is off: it starts once
        /// Automation is on.</summary>
        WaitsForAutomation,
    }

    /// <summary>A tunnel program's report, as the page says it.</summary>
    internal sealed record WebTunnelStatus(string Key, string State, string Address, string Words);

    /// <summary>
    /// The app's side of Reach PadForge From the Web (#600 item 11). It
    /// starts the web helper (<c>PadForge.exe --web</c>) through the plugin
    /// launcher, with the user's ordinary rights, no engine and no window,
    /// and a one-time key in its variables. It takes the helper's pipe
    /// connection only from that process with that key, so a helper
    /// anything else starts is refused. It owns what the user decides: the
    /// switch, the armed window of Accept a New Web Assistant, the question
    /// card of each sign-in, the allowed web assistants and their hashed
    /// tokens in PadForge.xml, and Revoke. The helper owns none of it.
    /// </summary>
    internal sealed class WebDoor : IDisposable
    {
        /// <summary>How long Accept a New Web Assistant stays armed.</summary>
        internal static readonly TimeSpan ArmedFor = TimeSpan.FromMinutes(5);

        /// <summary>The waits before the helper is started again after it
        /// ended by itself. Past the last, it stays stopped and the page says
        /// so.</summary>
        internal static readonly TimeSpan[] Restarts = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

        private static readonly ConditionalWeakTable<AutomationHost, WebDoor> Doors = new();

        /// <summary>The web door of a host, or null.</summary>
        internal static WebDoor Of(AutomationHost host) => host != null && Doors.TryGetValue(host, out var door) ? door : null;

        private readonly AutomationHost _host;
        private readonly WebAccessStore _store;
        private readonly IPluginLauncher _launcher;
        private readonly Func<string> _exe;
        private readonly string _pipeName;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _sync = new object();
        private IPluginProcess _helper;
        private byte[] _key;
        private Stream _link;
        private readonly SemaphoreSlim _write = new(1, 1);
        private DateTimeOffset _armedUntil = DateTimeOffset.MinValue;
        private int _failures;
        private int _tunnelRun;
        private bool _disposed;
        private IReadOnlyList<WebTunnelFound> _found = Array.Empty<WebTunnelFound>();

        /// <param name="host">The Automation host whose pipe the helper
        /// reaches and whose question card asks.</param>
        /// <param name="store">The settings and allowed web assistants.</param>
        /// <param name="launcher">The plugin launcher. A test gives its own.</param>
        /// <param name="exe">PadForge.exe's path.</param>
        /// <param name="pipeName">The pipe's name, handed to the helper.</param>
        public WebDoor(AutomationHost host, WebAccessStore store, IPluginLauncher launcher, Func<string> exe = null,
            string pipeName = null, Func<DateTimeOffset> now = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _launcher = launcher;
            _exe = exe ?? (() => Environment.ProcessPath);
            _pipeName = pipeName ?? AutomationPipe.PipeName;
            _now = now ?? (() => DateTimeOffset.UtcNow);
            Doors.AddOrUpdate(host, this);
            _store.Changed += OnStoreChanged;
            _store.Loaded += OnStoreLoaded;
            _host.Changed += OnHostChanged;
        }

        /// <summary>Automation turned on or off: the helper follows it, since
        /// nothing serves its pipe while Automation is off.</summary>
        private void OnHostChanged(object sender, EventArgs e)
        {
            bool running;
            lock (_sync) running = _helper != null;
            if (_host.IsOn && _store.On && !running)
            {
                lock (_sync) _failures = 0;
                Start();
            }
            else if (!_host.IsOn && running) Stop(WebHelperState.WaitsForAutomation, null);
            else if (!_host.IsOn && _store.On && HelperState != WebHelperState.WaitsForAutomation) SetState(WebHelperState.WaitsForAutomation, null);
        }

        private void OnStoreLoaded(object sender, EventArgs e)
        {
            Raise();
            if (_store.On) Start();
        }

        /// <summary>Raised on any change the page shows, on any thread.</summary>
        public event EventHandler Changed;

        public WebHelperState HelperState { get; internal set; } = WebHelperState.Off;

        /// <summary>Why the helper stopped, in words, or null.</summary>
        public string HelperWords { get; private set; }

        /// <summary>The loopback port the helper listens on, or 0.</summary>
        public int Port => _store.Port;

        public IReadOnlyList<WebTunnelFound> Found
        {
            get { lock (_sync) return _found; }
        }

        public WebTunnelStatus Tunnel { get; private set; }

        public DateTimeOffset ArmedUntil
        {
            get { lock (_sync) return _armedUntil; }
        }

        public bool Armed => _now() < ArmedUntil;

        private long _helperArmedUntil;

        /// <summary>Whether the helper holds the armed window the page set,
        /// so a sign-in now is taken.</summary>
        public bool HelperArmed
        {
            get
            {
                lock (_sync)
                    return _armedUntil != DateTimeOffset.MinValue && _helperArmedUntil == _armedUntil.ToUnixTimeMilliseconds();
            }
        }

        /// <summary>The address to give a web assistant now, or null.</summary>
        public string PublicAddress
        {
            get
            {
                string base_ = _store.Tunnel == "paste" ? WebAddress.Base(_store.Address)
                    : Tunnel?.State == "running" && Tunnel.Key == _store.Tunnel ? WebAddress.Base(Tunnel.Address) : null;
                return base_ == null ? null : base_ + WebAddress.McpPath;
            }
        }

        // ─────────────────────────────────────────────
        //  The switch and the helper
        // ─────────────────────────────────────────────

        /// <summary>Turns the door on, after the user said yes to the
        /// question the page puts: kept in PadForge.xml, and the helper
        /// starts.</summary>
        public void TurnOn()
        {
            _store.SetOn(true);
            _failures = 0;
            Start();
        }

        /// <summary>Turns the door off: the helper and its tunnel end, and
        /// nothing listens.</summary>
        public void TurnOff()
        {
            _store.SetOn(false);
            Disarm();
            Stop(WebHelperState.Off, null);
        }

        /// <summary>Starts the helper when the switch is on and none runs,
        /// as at PadForge's start.</summary>
        public void Start()
        {
            IPluginProcess helper;
            lock (_sync)
            {
                if (_disposed || !_store.On || _helper != null) return;
                if (!_host.IsOn)
                {
                    SetState(WebHelperState.WaitsForAutomation, null);
                    return;
                }
                var s = PadForge.Resources.Strings.Strings.Instance;
                string exe = _exe();
                if (_launcher == null || string.IsNullOrEmpty(exe))
                {
                    SetState(WebHelperState.Stopped, s.Web_Helper_Why_NotStarted);
                    return;
                }
                byte[] key = RandomNumberGenerator.GetBytes(32);
                var launch = _launcher.Start(new PluginCommand(exe, "\"" + exe + "\" --web", Path.GetDirectoryName(exe) ?? string.Empty),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [WebHelper.KeyVariable] = WebSecrets.Base64Url(key),
                        [WebHelper.PipeVariable] = _pipeName,
                    });
                if (launch?.Process == null)
                {
                    SetState(WebHelperState.Stopped, s.Web_Helper_Why_NotStarted);
                    return;
                }
                _helper = helper = launch.Process;
                _key = key;
                SetState(WebHelperState.Starting, null);
            }
            helper.Exited += code => OnHelperExited(helper, code);
        }

        private void OnHelperExited(IPluginProcess helper, int code)
        {
            bool again;
            int failures;
            lock (_sync)
            {
                if (!ReferenceEquals(_helper, helper)) return;
                _helper = null;
                _key = null;
                failures = _failures++;
                again = !_disposed && _store.On && failures < Restarts.Length;
                SetState(again ? WebHelperState.Starting : WebHelperState.Stopped,
                    string.Format(CultureInfo.CurrentCulture, PadForge.Resources.Strings.Strings.Instance.Web_Helper_Why_Ended_Format,
                        code.ToString(CultureInfo.InvariantCulture)));
                Tunnel = null;
            }
            try { helper.Dispose(); } catch { }
            if (again) _ = Task.Delay(Restarts[failures]).ContinueWith(_ => Start(), TaskScheduler.Default);
        }

        private void Stop(WebHelperState state, string words)
        {
            IPluginProcess helper;
            lock (_sync)
            {
                helper = _helper;
                _helper = null;
                _key = null;
                Tunnel = null;
                SetState(state, words);
            }
            if (helper != null)
            {
                try { helper.Stop(); } catch { }
                try { helper.Dispose(); } catch { }
            }
        }

        private void SetState(WebHelperState state, string words)
        {
            HelperState = state;
            HelperWords = words;
            Raise();
        }

        /// <summary>Whether a pipe hello with this key comes from the helper
        /// PadForge started: the key it was given, from that very process
        /// (not one it started in turn, which a tunnel is).</summary>
        internal bool AcceptHelper(string key, int processId)
        {
            lock (_sync)
            {
                if (_helper == null || _key == null || string.IsNullOrEmpty(key) || processId <= 0) return false;
                byte[] given;
                try { given = Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + new string('=', (4 - key.Length % 4) % 4)); }
                catch (FormatException) { return false; }
                if (!CryptographicOperations.FixedTimeEquals(given, _key)) return false;
                return _helper.Id == processId && _helper.Owns(processId);
            }
        }

        /// <summary>The grant a web assistant's pipe hello names, when it
        /// comes from the helper PadForge started with its key, or null.</summary>
        internal WebGrant AcceptWeb(string key, int processId, string grantId)
        {
            if (!AcceptHelper(key, processId) || string.IsNullOrEmpty(grantId)) return null;
            return _store.Grants.FirstOrDefault(g => g.Id == grantId);
        }

        // ─────────────────────────────────────────────
        //  The helper's pipe
        // ─────────────────────────────────────────────

        /// <summary>Serves the helper's pipe after its hello until it
        /// closes: says the state, and takes its sign-ins, tokens, port and
        /// tunnel reports.</summary>
        internal async Task ServeHelperAsync(Stream stream, CancellationToken stopping)
        {
            lock (_sync) _link = stream;
            if (HelperState == WebHelperState.Starting) SetState(WebHelperState.Running, null);
            lock (_sync) _failures = 0;
            await SayStateAsync().ConfigureAwait(false);
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    string line;
                    try { line = await WebLink.ReadLineAsync(stream, stopping, timeoutMs: 0).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { break; }
                    if (line == null) break;
                    JsonObject message;
                    try { message = JsonNode.Parse(line) as JsonObject; }
                    catch (System.Text.Json.JsonException) { continue; }
                    if (message != null) Take(message);
                }
            }
            finally
            {
                lock (_sync) if (ReferenceEquals(_link, stream)) _link = null;
            }
        }

        internal void Take(JsonObject message)
        {
            switch (Str(message["t"]))
            {
                case "port":
                    if (message["port"] is JsonValue p && p.TryGetValue(out int port) && port is > 1024 and < 65536) _store.SetPort(port);
                    break;
                case "found":
                    var found = new List<WebTunnelFound>();
                    if (message["tunnels"] is JsonArray list)
                        foreach (var item in list.OfType<JsonObject>())
                            if (WebTunnels.Of(Str(item["key"])) != null && Str(item["path"]) is { } path && Path.IsPathFullyQualified(path))
                                found.Add(new WebTunnelFound(Str(item["key"]), path));
                    lock (_sync) _found = found;
                    Raise();
                    _ = SayStateAsync();
                    break;
                case "tunnel":
                    Tunnel = new WebTunnelStatus(Str(message["key"]), Str(message["state"]), WebAddress.Base(Str(message["address"])),
                        Str(message["words"]) is { } words ? ProgramDeviceDeclaration.Clean(words, 200) : null);
                    Raise();
                    break;
                case "ask":
                    _ = AnswerAsync(message);
                    break;
                case "seen":
                    if (message["armedUntil"] is JsonValue seen && seen.TryGetValue(out long until))
                        lock (_sync) _helperArmedUntil = until;
                    Raise();
                    break;
                case "tokens":
                    var grant = WebWire.FromJson(message["grant"] as JsonObject);
                    if (grant?.Id != null) _store.Tokens(grant with { LastUsed = _now() });
                    break;
                case "used":
                    if (Str(message["id"]) is { } id) _store.Used(id, _now());
                    break;
            }
        }

        /// <summary>
        /// A sign-in waits: outside an armed window it is refused at once and
        /// nothing asks. Inside it, the question card asks "Allow claude.ai
        /// to work with PadForge from the web?", names it by the address its
        /// sign-in returns to and gives the name it calls itself as its
        /// claim, and shows the sign-in's code. Allow makes the grant, keeps
        /// it and ends the armed window.
        /// </summary>
        private async Task AnswerAsync(JsonObject message)
        {
            string sid = Str(message["sid"]);
            string grantId = null;
            try
            {
                var signIn = new WebSignIn(sid, Str(message["origin"]), Str(message["name"]), Str(message["code"]), Str(message["through"]),
                    message["loopback"] is JsonValue lv && lv.TryGetValue(out bool loop) && loop,
                    Str(message["clientId"]), Str(message["redirect"]), Str(message["resource"]));
                grantId = await AskAsync(signIn).ConfigureAwait(false);
            }
            finally
            {
                await SayAsync(new JsonObject { ["t"] = "answer", ["sid"] = sid, ["grant"] = grantId }).ConfigureAwait(false);
            }
        }

        /// <summary>Asks the user about a sign-in. The new grant's ID when
        /// allowed, else null.</summary>
        internal async Task<string> AskAsync(WebSignIn signIn)
        {
            if (signIn?.Origin == null || signIn.Code is not { Length: 6 } || !signIn.Code.All(char.IsAsciiDigit)) return null;
            if (!Armed) return null;
            // A client that signed in before signs in again in its own place.
            // Past the most kept, no card asks for one that could not be kept.
            var before = _store.Grants.FirstOrDefault(g => g.ClientId == signIn.ClientId && g.RedirectUri == signIn.RedirectUri);
            if (before == null && _store.Grants.Count >= WebAccessStore.MostAssistants) return null;
            var s = PadForge.Resources.Strings.Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string origin = ProgramDeviceDeclaration.Clean(signIn.Origin, 253);
            string from = string.Format(culture, s.Web_Ask_ReturnsTo_Format, origin)
                + (string.IsNullOrEmpty(signIn.Name) ? string.Empty : " " + string.Format(culture, s.Web_Ask_CallsItself_Format, ProgramDeviceDeclaration.Clean(signIn.Name, 64)));
            var detail = new List<string> { s.Web_Ask_CodeOnCard };
            if (signIn.Loopback) detail.Add(s.Web_Ask_LocalWarning);
            string levelName = LevelName(_host.Level);
            string after = string.Format(culture, s.Web_Ask_Through_Format, levelName, ProgramDeviceDeclaration.Clean(signIn.Through ?? string.Empty, 253));
            detail.Add(after);
            var request = new ApprovalRequest(new AutomationProgram(origin, 0, null),
                string.Format(culture, s.Web_Ask_Title_Format, origin), detail)
            {
                Kind = ApprovalKind.WebSignIn,
                From = from,
                Code = signIn.Code,
                CodeLead = s.Web_Ask_CodeLead,
                CodeAfter = signIn.Loopback ? s.Web_Ask_LocalWarning + " " + after : after,
            };
            ApprovalAnswer answer;
            try { answer = await _host.Approvals.AskAsync(request).ConfigureAwait(false); }
            catch (OperationCanceledException) { answer = ApprovalAnswer.TimedOut; }
            if (answer != ApprovalAnswer.Allowed) return null;
            // A window that ran out while the card showed still counts: the
            // user answered a card the armed window raised.
            var now = _now();
            var grant = new WebGrant
            {
                Id = WebSecrets.New(12),
                Origin = origin,
                Name = string.IsNullOrEmpty(signIn.Name) ? null : ProgramDeviceDeclaration.Clean(signIn.Name, 64),
                ClientId = signIn.ClientId,
                RedirectUri = signIn.RedirectUri,
                Resource = signIn.Resource,
                Allowed = now,
            };
            before = _store.Grants.FirstOrDefault(g => g.ClientId == signIn.ClientId && g.RedirectUri == signIn.RedirectUri);
            if (before != null)
            {
                _store.Revoke(before.Id);
                _host.Sessions.CloseWeb(WebKey(before.Id));
            }
            if (!_store.Add(grant)) return null;
            Disarm();
            await SayStateAsync().ConfigureAwait(false);
            return grant.Id;
        }

        internal static string LevelName(AutomationLevel level)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            return level switch
            {
                AutomationLevel.Commands => s.Extras_Automation_Level_Commands,
                AutomationLevel.Read => s.Extras_Automation_Level_Read,
                AutomationLevel.SendInputs => s.Extras_Automation_Level_SendInputs,
                _ => s.Extras_Automation_Level_ChangeSettings,
            };
        }

        // ─────────────────────────────────────────────
        //  What the user does on the page
        // ─────────────────────────────────────────────

        /// <summary>Accept a New Web Assistant: arms the door for five
        /// minutes.</summary>
        public void Arm()
        {
            lock (_sync) _armedUntil = _now() + ArmedFor;
            Raise();
            _ = SayStateAsync();
        }

        /// <summary>Stop Accepting, or an allowed sign-in.</summary>
        public void Disarm()
        {
            lock (_sync) _armedUntil = DateTimeOffset.MinValue;
            Raise();
            _ = SayStateAsync();
        }

        /// <summary>Revoke: the web assistant's tokens stop working at once,
        /// its hashes leave PadForge.xml, and its connection closes.</summary>
        public void Revoke(string grantId)
        {
            if (!_store.Revoke(grantId)) return;
            _host.Sessions.CloseWeb(WebKey(grantId));
            _ = SayStateAsync();
        }

        /// <summary>The tunnel picked: a tunnel program's key, "paste" with
        /// an address, or null for none.</summary>
        public void PickTunnel(string key, string address = null)
        {
            if (key != null && key != "paste" && WebTunnels.Of(key) == null) key = null;
            lock (_sync) _tunnelRun++;
            _store.SetTunnel(key, key == "paste" ? address?.Trim() : _store.Address);
            Tunnel = null;
            Raise();
            _ = SayStateAsync();
        }

        /// <summary>The consent key a web assistant's sessions carry.</summary>
        internal static string WebKey(string grantId) => "web:" + grantId;

        // ─────────────────────────────────────────────

        private void OnStoreChanged(object sender, EventArgs e) => Raise();

        private void Raise()
        {
            try { Changed?.Invoke(this, EventArgs.Empty); } catch { /* the page's own */ }
        }

        /// <summary>What the helper is told: the armed window, the grants, the
        /// tunnel and address, the port and the language.</summary>
        internal JsonObject State()
        {
            string tunnel = _store.Tunnel;
            string path = null;
            lock (_sync)
            {
                if (tunnel != null && tunnel != "paste") path = _found.FirstOrDefault(f => f.Key == tunnel)?.Path;
            }
            return new JsonObject
            {
                ["t"] = "state",
                ["armedUntil"] = ArmedUntil == DateTimeOffset.MinValue ? 0 : ArmedUntil.ToUnixTimeMilliseconds(),
                ["grants"] = new JsonArray(_store.Grants.Select(g => (JsonNode)WebWire.ToJson(g)).ToArray()),
                ["pasted"] = tunnel == "paste" ? _store.Address : null,
                ["tunnel"] = tunnel == "paste" ? null : tunnel,
                ["tunnelPath"] = path,
                ["tunnelRun"] = _tunnelRun,
                ["port"] = _store.Port,
                ["language"] = CultureInfo.CurrentUICulture.Name,
                // The pages' words in that language: the helper reads none of
                // PadForge's strings (WebPages).
                ["words"] = new JsonObject(WebPages.WordKeys.Select(k => KeyValuePair.Create(k, (JsonNode)JsonValue.Create(PadForge.Resources.Strings.Strings.Get(k))))),
            };
        }

        internal Task SayStateAsync() => SayAsync(State());

        private async Task SayAsync(JsonObject message)
        {
            Stream link;
            lock (_sync) link = _link;
            if (link == null) return;
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await link.WriteAsync(bytes).ConfigureAwait(false);
                await link.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            finally { _write.Release(); }
        }

        private static string Str(JsonNode node) => node is JsonValue v && v.TryGetValue(out string s) && !string.IsNullOrEmpty(s) ? s : null;

        public void Dispose()
        {
            lock (_sync) _disposed = true;
            _store.Changed -= OnStoreChanged;
            _store.Loaded -= OnStoreLoaded;
            _host.Changed -= OnHostChanged;
            Stop(WebHelperState.Off, null);
            Doors.Remove(_host);
        }
    }
}
