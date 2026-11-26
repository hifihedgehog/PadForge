using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PadForge.Services.Automation
{
    /// <summary>Where the running PadForge stands, as the copy an assistant
    /// starts sees it.</summary>
    internal enum MirrorState
    {
        /// <summary>Not tried yet.</summary>
        Starting,
        /// <summary>PadForge answers and its tools are listed.</summary>
        Connected,
        /// <summary>No PadForge runs.</summary>
        Closed,
        /// <summary>PadForge runs with Automation off.</summary>
        AutomationOff,
        /// <summary>Another program holds the pipe's name, or answered in
        /// PadForge's place and failed the owner check.</summary>
        NameTaken,
        /// <summary>PadForge answered and would not serve an assistant now.</summary>
        Refused,
    }

    /// <summary>
    /// What <c>PadForge.exe --mcp</c> runs (#600): the copy an assistant
    /// starts. It runs with the caller's rights and loads none of WPF.
    ///
    /// <para>Toward the assistant it is the SDK's server over standard input
    /// and output, and nothing but protocol bytes ever goes to standard
    /// output. It ends when standard input closes, and a
    /// <c>server/discover</c> probe is answered by the SDK like any request,
    /// with the copy staying up after it.</para>
    ///
    /// <para>Toward PadForge it is the SDK's client over
    /// <c>PadForge.Automation</c>, after a <c>hello kind=mcp</c> that names the
    /// assistant's client and the program that started this copy. Its
    /// protocol version is pinned to 2025-11-25, the newest with the
    /// handshake, so PadForge's list-changed notices reach it. The pipe is
    /// opened for identification only, and its owner must be Administrators
    /// or the system (<see cref="PipeOwner"/>) before a byte is
    /// sent: a pipe of that name made by another program counts as no
    /// PadForge.</para>
    ///
    /// <para>It mirrors and relays no bytes: for each tool, prompt and
    /// resource PadForge has, it stands one in its own collections that
    /// carries the definition unchanged and forwards the call, and it changes
    /// its collections when PadForge's change, which makes the SDK tell the
    /// assistant in the way its revision needs. The skill it serves itself.
    /// With PadForge closed, with Automation off and while PadForge
    /// restarts, it stays connected to the assistant, lists one tool,
    /// <c>status</c>, which says which is the case and what the user can do,
    /// and keeps trying the pipe.</para>
    /// </summary>
    internal sealed class AssistantMirror : IAsyncDisposable
    {
        /// <summary>The hello of an assistant's own copy, which names the
        /// assistant and the program that started it.</summary>
        internal static string McpHello(string clientName, string host)
            => "hello kind=mcp name=" + AutomationLine.Quote(clientName ?? "assistant")
               + (string.IsNullOrEmpty(host) ? string.Empty : " host=" + AutomationLine.Quote(host)) + " protocol=" + AutomationLine.Protocol;

        /// <summary>The protocol version toward PadForge.</summary>
        internal const string UpstreamVersion = "2025-11-25";

        /// <summary>How long the copy waits between tries of the pipe.</summary>
        internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(1);

        /// <summary>How long a first tools/list waits for the first try.</summary>
        internal static readonly TimeSpan FirstTryWait = TimeSpan.FromSeconds(5);

        internal const int ConnectMs = 1500;

        /// <summary>How long a call that started PadForge waits for it
        /// (#600).</summary>
        internal static readonly TimeSpan StartWait = TimeSpan.FromSeconds(60);

        /// <summary>How long after one start no other is made, so a refused
        /// prompt for rights is never put again at every call.</summary>
        internal static readonly TimeSpan StartQuiet = TimeSpan.FromSeconds(90);

        /// <summary>
        /// The resume secret this copy holds for its whole run (#600): its
        /// hello carries it, so PadForge keeps the copy's undo, rate and
        /// waiting requests across a pipe drop and a restart of either side
        /// within a while. Random, never written anywhere.
        /// </summary>
        internal readonly string Resume = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>What PadForge.xml says of Automation, read without
        /// loading the app: the switch and the level. A test gives its
        /// own.</summary>
        internal Func<AutomationFileSettings> FileSettings { get; set; } = () => AutomationFileSettings.Kept(AutomationFileSettings.DefaultPath());

        /// <summary>Starts PadForge minimized through the normal start path:
        /// the desktop shell starts PadForge.exe as a double-click does, with
        /// the user's rights, and PadForge asks Windows for its own. True when
        /// the shell took it. The test host starts nothing
        /// (ShellLaunch.OffLimitsForTest).</summary>
        internal Func<bool> StartPadForge { get; set; } = () =>
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            return PadForge.Common.ShellLaunch.AsTheUser(new System.Diagnostics.ProcessStartInfo(exe, "--minimized")
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            }, out _);
        };

        /// <summary>The hello this copy sends PadForge, when it is not an
        /// assistant's own: the web helper's copy for one web assistant
        /// (#600 item 11) says <c>kind=web</c> with its grant.</summary>
        internal Func<string> HelloLine { get; set; }

        /// <summary>Whether a tool's <c>path</c> is a file this copy opens
        /// with its own rights. False for a web assistant, whose paths are
        /// not this PC's to give: it sends a profile's bytes instead.</summary>
        internal bool LocalFiles { get; set; } = true;

        /// <summary>Whether the assistant hears when the lists change. False
        /// for the web, whose replies hold the result alone.</summary>
        internal bool ListChanges { get; set; } = true;

        private DateTimeOffset _startedAt = DateTimeOffset.MinValue;
        private readonly object _startSync = new object();

        private readonly string _pipeName;
        private readonly Func<bool> _padForgeRuns;
        private readonly Func<bool> _padForgeReady;
        private readonly Func<SafePipeHandle, bool> _trust;
        private readonly string _host;
        private readonly McpServerPrimitiveCollection<McpServerTool> _tools = new();
        private readonly McpServerPrimitiveCollection<McpServerPrompt> _prompts = new();
        private readonly McpServerResourceCollection _resources = new();
        private readonly TaskCompletionSource<bool> _firstTry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _named = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _sync = new object();
        private volatile McpClient _up;
        private volatile string _clientName;
        private volatile string _words;

        /// <param name="pipeName">The pipe PadForge serves. A test gives one
        /// of its own.</param>
        /// <param name="padForgeRuns">Whether a PadForge runs on this desktop,
        /// which tells Automation off from PadForge closed.</param>
        /// <param name="trustServer">Whether a connected pipe is PadForge's.
        /// The owner check when none is given.</param>
        /// <param name="host">The image name of the program that started
        /// this copy, for Activity. Found when none is given.</param>
        /// <param name="fileSettings">What PadForge.xml says of Automation
        /// (#600). The file beside this exe when none is given.</param>
        /// <param name="padForgeReady">Whether the PadForge that runs is
        /// past its start and not yet closing (RunningCopy.IsReady), which
        /// tells Automation off from a copy that does not serve its pipe
        /// yet.</param>
        public AssistantMirror(string pipeName = null, Func<bool> padForgeRuns = null, Func<SafePipeHandle, bool> trustServer = null, string host = null,
            Func<AutomationFileSettings> fileSettings = null, Func<bool> padForgeReady = null)
        {
            if (fileSettings != null) FileSettings = fileSettings;
            _padForgeReady = padForgeReady ?? RunningCopy.IsReady;
            _pipeName = pipeName ?? AutomationPipe.PipeName;
            _padForgeRuns = padForgeRuns ?? RunningCopy.Exists;
            _trust = trustServer ?? (handle => PipeOwner.IsServer(handle, _pipeName));
            _host = host ?? ParentImage();
            ShowOffline();
        }

        /// <summary>Where PadForge stands now.</summary>
        public MirrorState State { get; private set; } = MirrorState.Starting;

        /// <summary>Raised when <see cref="State"/> changes, for tests.</summary>
        public event EventHandler StateChanged;

        public McpServerPrimitiveCollection<McpServerTool> Tools => _tools;

        /// <summary>The server toward the assistant's options.</summary>
        internal McpServerOptions Options()
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "padforge",
                    Title = "PadForge",
                    Version = BuildIdentity.VersionText(BuildIdentity.Version),
                    WebsiteUrl = "https://padforge.org",
                },
                ProtocolVersion = null,
                ServerInstructions = AssistantGuidance.Instructions,
                ToolCollection = _tools,
                PromptCollection = _prompts,
                ResourceCollection = _resources,
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = ListChanges },
                    Prompts = new PromptsCapability { ListChanged = ListChanges },
                    Resources = new ResourcesCapability { ListChanged = ListChanges },
                    Extensions = new Dictionary<string, object> { [AssistantSkill.ExtensionId] = new JsonObject() },
                },
            };
#pragma warning disable MCPEXP002 // The skills extension's two methods.
            options.RequestHandlers = AssistantSkill.Handlers().ToList();
#pragma warning restore MCPEXP002
            // The first list waits for the first try of the pipe, so an
            // assistant that lists at once sees PadForge's tools.
            options.Handlers.ListToolsHandler = async (request, ct) =>
            {
                Name(request.Server?.ClientInfo?.Name);
                await Task.WhenAny(_firstTry.Task, Task.Delay(FirstTryWait, ct)).ConfigureAwait(false);
                return new ListToolsResult();
            };
            // The name the assistant's client gives, from its first request
            // that carries one, for the hello.
            options.Filters.Message.IncomingFilters.Add(next => async (context, ct) =>
            {
                if (context.JsonRpcMessage is JsonRpcRequest request) Name(ClientNameIn(request));
                await next(context, ct).ConfigureAwait(false);
            });
            return options;
        }

        private void Name(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || _clientName != null) return;
            _clientName = name.Trim();
            _named.TrySetResult(true);
        }

        /// <summary>The client's name in an initialize request or in a
        /// request's metadata.</summary>
        internal static string ClientNameIn(JsonRpcRequest request)
        {
            try
            {
                var p = request.Params;
                if (p == null) return null;
                if (request.Method == "initialize") return p["clientInfo"]?["name"]?.GetValue<string>();
                return p["_meta"]?[MetaKeys.ClientInfo]?["name"]?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>The whole run: the server toward the assistant over
        /// standard input and output, until it closes.</summary>
        internal static int Run() => Run(() => new AssistantMirror());

        /// <summary>The variable that names, for a test's child alone, the
        /// settings file its copy reads at its start in place of the
        /// PadForge.xml beside the exe (#600 item 52), so a test runs the
        /// production copy's start-up read under its own fences. It is read
        /// only in the run of a test's child (ClientRun.Seams.ForThisRun).</summary>
        internal const string TestsSettingsVariable = "PADFORGE_TEST_SETTINGS";

        /// <summary>The run of a test's child (#600 item 52), whose
        /// variables name a pipe of the test's own (ClientRun.Seams.ForThisRun),
        /// and a settings file of the test's when they name one.</summary>
        internal static int Run(string testsOwnPipe)
            => Run(() => ForTestsOwnPipe(testsOwnPipe, Environment.GetEnvironmentVariable(TestsSettingsVariable)));

        /// <summary>The copy for a test's own pipe: that pipe stands for
        /// PadForge, whether PadForge runs is whether it stands, the settings
        /// file read is the one the test names or none, never the
        /// PadForge.xml beside the exe, and a call that needs PadForge starts
        /// nothing. So a test that starts this copy never reaches a PadForge
        /// that runs on the PC.</summary>
        internal static AssistantMirror ForTestsOwnPipe(string pipe, string settingsFile = null)
            => new AssistantMirror(pipe, () => ControlPipeClient.Stands(pipe),
                fileSettings: string.IsNullOrEmpty(settingsFile) ? () => AutomationFileSettings.None : () => AutomationFileSettings.Kept(settingsFile),
                padForgeReady: () => ControlPipeClient.Stands(pipe))
            {
                StartPadForge = () => false,
            };

        private static int Run(Func<AssistantMirror> make)
        {
            try
            {
                RunAsync(make).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error)
            {
                // Standard error is where a host shows a server's own words.
                try { Console.Error.WriteLine("PadForge's assistant connection stopped: " + error.Message); } catch { }
                return 1;
            }
        }

        private static async Task RunAsync(Func<AssistantMirror> make)
        {
            await using var mirror = make();
            await using var transport = new StdioServerTransport("PadForge");
            await mirror.ServeAsync(transport, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Serves the assistant over <paramref name="transport"/> and
        /// keeps trying PadForge until the assistant's side closes.</summary>
        internal async Task ServeAsync(ITransport transport, CancellationToken stopping)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            await using var server = McpServer.Create(transport, Options());
            var upstream = Task.Run(() => KeepTryingAsync(stop.Token));
            try
            {
                await server.RunAsync(stop.Token).ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                try { await upstream.ConfigureAwait(false); } catch { }
            }
        }

        // ─────────────────────────────────────────────
        //  Toward PadForge
        // ─────────────────────────────────────────────

        /// <summary>Keeps trying PadForge until <paramref name="stopping"/>,
        /// for a copy whose server side runs per request (the web helper's,
        /// #600 item 11).</summary>
        internal Task RunUpstreamAsync(CancellationToken stopping) => KeepTryingAsync(stopping);

        private async Task KeepTryingAsync(CancellationToken stopping)
        {
            // The hello names the client: wait a moment for its first
            // request, never long. A hello of its own needs no name.
            if (HelloLine == null) await Task.WhenAny(_named.Task, Task.Delay(1000, stopping)).ConfigureAwait(false);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await TryOnceAsync(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A try that broke counts as no PadForge, and the next
                    // try comes as it would.
                }
                Offline();
                _firstTry.TrySetResult(true);
                try { await Task.Delay(RetryEvery, stopping).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Where PadForge stands when its pipe did not answer: a
        /// copy that runs and is ready serves no pipe because Automation is
        /// off, and one that runs and is not ready is still starting, or
        /// closing (RunningCopy).</summary>
        private MirrorState NoPipe()
            => !_padForgeRuns() ? MirrorState.Closed : _padForgeReady() ? MirrorState.AutomationOff : MirrorState.Starting;

        private async Task TryOnceAsync(CancellationToken stopping)
        {
            using var pipe = PipeOwner.Open(_pipeName);
            if (pipe == null)
            {
                Set(MirrorState.Closed, null);
                return;
            }
            try
            {
                await pipe.ConnectAsync(ConnectMs, stopping).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Set(NoPipe(), null);
                return;
            }
            catch (IOException)
            {
                Set(NoPipe(), null);
                return;
            }
            // Nothing goes to a pipe another program made.
            if (!_trust(pipe.SafePipeHandle))
            {
                Set(MirrorState.NameTaken, null);
                return;
            }

            string hello = (HelloLine != null ? HelloLine() : McpHello(_clientName, _host)) + " resume=" + Resume + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(hello);
            await pipe.WriteAsync(bytes, stopping).ConfigureAwait(false);
            await pipe.FlushAsync(stopping).ConfigureAwait(false);
            string answer = await ReadLineAsync(pipe, stopping).ConfigureAwait(false);
            if (answer == null || !answer.StartsWith("ok ", StringComparison.Ordinal))
            {
                Set(answer != null && answer.Contains("not-allowed", StringComparison.Ordinal) ? MirrorState.AutomationOff : MirrorState.Refused, answer);
                return;
            }
            if (answer.Contains(" level=off", StringComparison.Ordinal))
            {
                Set(MirrorState.AutomationOff, null);
                return;
            }

            var transport = new StreamClientTransport(pipe, pipe);
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ProtocolVersion = UpstreamVersion,
                ClientInfo = new Implementation { Name = "padforge-mcp", Version = BuildIdentity.VersionText(BuildIdentity.Version) },
            }, cancellationToken: stopping).ConfigureAwait(false);

            _up = client;
            await using var toolsChanged = client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, (n, ct) => MirrorAsync(client, ct));
            await using var promptsChanged = client.RegisterNotificationHandler(NotificationMethods.PromptListChangedNotification, (n, ct) => MirrorAsync(client, ct));
            await using var resourcesChanged = client.RegisterNotificationHandler(NotificationMethods.ResourceListChangedNotification, (n, ct) => MirrorAsync(client, ct));
            await MirrorAsync(client, stopping).ConfigureAwait(false);
            Set(MirrorState.Connected, null);
            _firstTry.TrySetResult(true);
            try
            {
                await client.Completion.WaitAsync(stopping).ConfigureAwait(false);
            }
            finally
            {
                _up = null;
            }
        }

        /// <summary>One line of the pipe, read a byte at a time so nothing
        /// after it is taken from the protocol.</summary>
        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken stopping)
        {
            var line = new List<byte>(128);
            var one = new byte[1];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(5000);
            while (line.Count < 8192)
            {
                int n;
                try { n = await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return null; }
                if (n == 0) return null;
                if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.Add(one[0]);
            }
            return null;
        }

        /// <summary>Stands PadForge's tools, prompts and resources in this
        /// copy's collections, in PadForge's order, each change in one
        /// step.</summary>
        private async ValueTask MirrorAsync(McpClient client, CancellationToken ct)
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
            var prompts = client.ServerCapabilities?.Prompts != null
                ? await client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false) : (IList<McpClientPrompt>)Array.Empty<McpClientPrompt>();
            var resources = client.ServerCapabilities?.Resources != null
                ? await client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false) : (IList<McpClientResource>)Array.Empty<McpClientResource>();
            if (!ReferenceEquals(_up, client)) return;
            lock (_sync)
            {
                Replace(_tools, tools.Select(t => (McpServerTool)new ForwardedTool(t.ProtocolTool, this)).ToList(),
                    t => JsonSerializer.Serialize(t.ProtocolTool, McpJsonUtilities.DefaultOptions));
                Replace(_prompts, prompts.Select(p => (McpServerPrompt)new ForwardedPrompt(p.ProtocolPrompt, this)).ToList(),
                    p => JsonSerializer.Serialize(p.ProtocolPrompt, McpJsonUtilities.DefaultOptions));
                // The skill's files this copy serves itself, from the same
                // files PadForge carries.
                var own = AssistantSkill.Resources().ToList();
                var forwarded = resources.Where(r => !r.ProtocolResource.Uri.StartsWith(AssistantSkill.Root, StringComparison.Ordinal))
                    .Select(r => (McpServerResource)new ForwardedResource(r.ProtocolResource, this));
                ReplaceResources(own.Concat(forwarded).ToList());
            }
        }

        private static void Replace<T>(McpServerPrimitiveCollection<T> collection, List<T> want, Func<T, string> form) where T : IMcpServerPrimitive
        {
            var have = collection.ToList();
            if (have.Count == want.Count && have.Select(form).SequenceEqual(want.Select(form))) return;
            using (collection.DeferChangedEvents())
            {
                collection.Clear();
                foreach (var item in want) collection.Add(item);
            }
        }

        private void ReplaceResources(List<McpServerResource> want)
        {
            var have = _resources.Select(r => r.ProtocolResource?.Uri).ToList();
            if (have.SequenceEqual(want.Select(r => r.ProtocolResource?.Uri))) return;
            using (_resources.DeferChangedEvents())
            {
                _resources.Clear();
                foreach (var r in want) _resources.Add(r);
            }
        }

        /// <summary>PadForge went away: one tool, status, and the skill.</summary>
        private void Offline()
        {
            _up = null;
            lock (_sync) ShowOffline();
        }

        /// <summary>While PadForge is closed (#600): with Automation on in
        /// PadForge.xml, the tools its level allows, each of which starts
        /// PadForge when called and waits for it, and status. With it off,
        /// status alone, which says what to do.</summary>
        private void ShowOffline()
        {
            var file = SafeFile();
            var want = new List<McpServerTool> { new OfflineStatus(this) };
            if (file.On && State is MirrorState.Closed or MirrorState.Starting)
                foreach (var tool in AssistantTools.All.Where(t => t.Level <= file.Level && t.Name != "status"))
                    want.Add(new StartingTool(tool.ProtocolTool, this));
            if (!(_tools.Count == want.Count && _tools.Select(x => x.ProtocolTool.Name).SequenceEqual(want.Select(x => x.ProtocolTool.Name))
                  && _tools.First() is OfflineStatus))
            {
                using (_tools.DeferChangedEvents())
                {
                    _tools.Clear();
                    foreach (var tool in want) _tools.Add(tool);
                }
            }
            if (_prompts.Count > 0) _prompts.Clear();
            ReplaceResources(AssistantSkill.Resources().ToList());
        }

        private void Set(MirrorState state, string words)
        {
            _words = words;
            if (State == state) return;
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private AutomationFileSettings SafeFile()
        {
            try { return FileSettings?.Invoke() ?? AutomationFileSettings.None; }
            catch (Exception) { return AutomationFileSettings.None; }
        }

        /// <summary>
        /// A call that needs PadForge while it is closed (#600): with
        /// Automation on in PadForge.xml, PadForge is started minimized
        /// through the normal start path, once in <see cref="StartQuiet"/>,
        /// and the call waits up to <see cref="StartWait"/> for it. False
        /// when it did not come: Automation is off in the file, the start was
        /// refused, or PadForge did not answer in time.
        /// </summary>
        internal async Task<bool> EnsurePadForgeAsync(CancellationToken ct)
        {
            if (_up != null && State == MirrorState.Connected) return true;
            if (!SafeFile().On) return false;
            if (State is MirrorState.AutomationOff or MirrorState.NameTaken or MirrorState.Refused) return false;
            bool start;
            lock (_startSync)
            {
                start = DateTimeOffset.UtcNow - _startedAt > StartQuiet;
                if (start) _startedAt = DateTimeOffset.UtcNow;
            }
            if (start && !(StartPadForge?.Invoke() ?? false))
            {
                _words = "the start was refused";
                return false;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < StartWait && !ct.IsCancellationRequested)
            {
                if (_up != null && State == MirrorState.Connected) return true;
                if (State is MirrorState.AutomationOff or MirrorState.NameTaken or MirrorState.Refused) return false;
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
            }
            return _up != null && State == MirrorState.Connected;
        }

        /// <summary>What status says while PadForge does not answer.</summary>
        internal string OfflineMessage() => State switch
        {
            MirrorState.AutomationOff => "PadForge runs with Automation off, so it takes no assistant. Ask the user to open PadForge, go to Extras, Automation, turn it on and pick a level. This connection finds PadForge again by itself.",
            MirrorState.NameTaken => "Another program holds the name of PadForge's pipe, so this connection sends it nothing. Ask the user to close programs that mimic PadForge, then restart PadForge. This connection keeps trying.",
            MirrorState.Refused => "PadForge did not take this connection" + (string.IsNullOrEmpty(_words) ? "." : ": " + _words + ".") + " This connection keeps trying.",
            MirrorState.Starting => "This connection is still looking for PadForge. Call status again in a moment.",
            _ => SafeFile().On
                ? "PadForge is not running. The next call that needs it starts PadForge minimized and waits for it, and Windows may ask the user for PadForge's rights. This connection finds it by itself."
                : "PadForge is not running, and its Automation is off. Ask the user to start PadForge, go to Extras, Automation, turn it on and pick a level. After that a call starts PadForge by itself when it is closed.",
        };

        private McpClient Up => _up;

        public async ValueTask DisposeAsync()
        {
            var up = _up;
            _up = null;
            if (up != null) { try { await up.DisposeAsync().ConfigureAwait(false); } catch { } }
        }

        /// <summary>The status tool while PadForge does not answer, with the
        /// definition and result type PadForge's own status has.</summary>
        private sealed class OfflineStatus : McpServerTool
        {
            private readonly AssistantMirror _mirror;

            public OfflineStatus(AssistantMirror mirror) => _mirror = mirror;

            public override Tool ProtocolTool => AssistantTools.All.First(t => t.Name == "status").ProtocolTool;

            public override IReadOnlyList<object> Metadata => Array.Empty<object>();

            public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                var result = new StatusResult
                {
                    Connected = false,
                    PadforgeVersion = BuildIdentity.VersionText(BuildIdentity.Version),
                    Message = _mirror.OfflineMessage(),
                };
                string json = AssistantJson.ToText(result, typeof(StatusResult));
                using var doc = JsonDocument.Parse(json);
                return ValueTask.FromResult(new CallToolResult
                {
                    StructuredContent = doc.RootElement.Clone(),
                    Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
                    IsError = false,
                });
            }
        }

        /// <summary>A tool PadForge's level allows, listed while PadForge is
        /// closed (#600): a call starts PadForge, waits for it and is then
        /// forwarded as any call is.</summary>
        private sealed class StartingTool : McpServerTool
        {
            private readonly Tool _tool;
            private readonly AssistantMirror _mirror;

            public StartingTool(Tool tool, AssistantMirror mirror)
            {
                _tool = tool;
                _mirror = mirror;
            }

            public override Tool ProtocolTool => _tool;

            public override IReadOnlyList<object> Metadata => Array.Empty<object>();

            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                if (!await _mirror.EnsurePadForgeAsync(cancellationToken).ConfigureAwait(false))
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock> { new TextContentBlock { Text = "PadForge did not start for this call. " + _mirror.OfflineMessage() + " (not-connected)" } },
                    };
                return await new ForwardedTool(_tool, _mirror).InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>A tool of PadForge's: its definition unchanged, and its
        /// call forwarded.</summary>
        private sealed class ForwardedTool : McpServerTool
        {
            private readonly Tool _tool;
            private readonly AssistantMirror _mirror;

            public ForwardedTool(Tool tool, AssistantMirror mirror)
            {
                _tool = tool;
                _mirror = mirror;
            }

            public override Tool ProtocolTool => _tool;

            public override IReadOnlyList<object> Metadata => Array.Empty<object>();

            public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
            {
                var up = _mirror.Up;
                if (up == null) return Gone();
                var arguments = request.Params?.Arguments;
                // A file named by its path is opened here, with the user's own
                // rights. PadForge, which has more, never opens it for a
                // program, and this copy opens the pipe for identification
                // only, so PadForge could not borrow these rights to.
                string exportTo = null;
                if (!_mirror.LocalFiles && _tool.Name is "import_profile" or "export_profile" && PathIn(arguments) != null)
                    return Failed("A web assistant reaches no file on this PC. Send the profile as content_base64, or take it from the result.", "bad-argument");
                if (_tool.Name == "import_profile" && PathIn(arguments) is { } readFrom)
                {
                    var (content, problem) = ReadFile(readFrom);
                    if (problem != null) return Failed(problem, "no-file");
                    arguments = new Dictionary<string, JsonElement>
                    {
                        ["file_name"] = JsonSerializer.SerializeToElement(Path.GetFileName(readFrom)),
                        ["content_base64"] = JsonSerializer.SerializeToElement(Convert.ToBase64String(content)),
                    };
                }
                else if (_tool.Name == "export_profile" && PathIn(arguments) is { } writeTo)
                {
                    exportTo = writeTo;
                    arguments = arguments.Where(p => p.Key != "path").ToDictionary(p => p.Key, p => p.Value);
                }
                try
                {
                    var result = await up.CallToolAsync(new CallToolRequestParams
                    {
                        Name = _tool.Name,
                        Arguments = arguments,
                    }, cancellationToken).ConfigureAwait(false);
                    return exportTo == null ? result : WriteExport(result, exportTo);
                }
                catch (McpProtocolException)
                {
                    // PadForge's own protocol error, such as a tool that left
                    // its list, goes to the assistant as it came.
                    throw;
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or McpException
                                              && !cancellationToken.IsCancellationRequested)
                {
                    return Gone();
                }
            }

            private static string PathIn(IDictionary<string, JsonElement> arguments)
                => arguments != null && arguments.TryGetValue("path", out var path) && path.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(path.GetString()) ? path.GetString() : null;

            /// <summary>The most bytes a profile file may hold, as PadForge
            /// takes it.</summary>
            private const int LongestFile = 16 * 1024 * 1024;

            private static (byte[] Content, string Problem) ReadFile(string path)
            {
                if (!Path.IsPathFullyQualified(path))
                    return (null, "Give the file's full path, as in C:\\Users\\name\\Downloads\\profile.pfprofile.");
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > LongestFile) return (null, "The file is over 16 MB, more than a profile file holds.");
                    return (File.ReadAllBytes(path), null);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return (null, "The file could not be read with the user's rights (" + error.GetType().Name.Replace("Exception", string.Empty) + "). Check the path.");
                }
            }

            /// <summary>An export's bytes, written to the path the assistant
            /// gave, and the result as PadForge's own write gives it.</summary>
            private static CallToolResult WriteExport(CallToolResult result, string path)
            {
                if (result.IsError == true || result.StructuredContent is not { } structured) return result;
                if (!Path.IsPathFullyQualified(path)) return Failed("Give the full path of the file to write.", "bad-argument");
                if (!path.EndsWith(PadForge.Common.ProfileTransfer.FileExtension, StringComparison.OrdinalIgnoreCase))
                    path += PadForge.Common.ProfileTransfer.FileExtension;
                var node = JsonNode.Parse(structured.GetRawText()).AsObject();
                string base64 = node["content_base64"]?.GetValue<string>();
                if (base64 == null) return result;
                try
                {
                    File.WriteAllBytes(path, Convert.FromBase64String(base64));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return Failed("The file could not be written with the user's rights (" + error.GetType().Name.Replace("Exception", string.Empty)
                        + "). Pick a folder the user can write to.", "no-file");
                }
                node.Remove("content_base64");
                node["file"] = Path.GetFileName(path);
                node["message"] = "Written.";
                string json = node.ToJsonString();
                using var doc = JsonDocument.Parse(json);
                return new CallToolResult
                {
                    StructuredContent = doc.RootElement.Clone(),
                    Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
                    IsError = false,
                };
            }

            private static CallToolResult Failed(string words, string code) => new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = words + " (" + code + ")" } },
            };

            private static CallToolResult Gone() => new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = "PadForge went away before it answered. Call status to see why. (not-connected)" } },
            };
        }

        private sealed class ForwardedPrompt : McpServerPrompt
        {
            private readonly Prompt _prompt;
            private readonly AssistantMirror _mirror;

            public ForwardedPrompt(Prompt prompt, AssistantMirror mirror)
            {
                _prompt = prompt;
                _mirror = mirror;
            }

            public override Prompt ProtocolPrompt => _prompt;

            public override IReadOnlyList<object> Metadata => Array.Empty<object>();

            public override async ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
            {
                var up = _mirror.Up ?? throw new McpProtocolException("PadForge is not connected. Call status.", McpErrorCode.InternalError);
                return await up.GetPromptAsync(new GetPromptRequestParams { Name = _prompt.Name, Arguments = request.Params?.Arguments }, cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class ForwardedResource : McpServerResource
        {
            private readonly Resource _resource;
            private readonly ResourceTemplate _template;
            private readonly AssistantMirror _mirror;

            public ForwardedResource(Resource resource, AssistantMirror mirror)
            {
                _resource = resource;
                _mirror = mirror;
                _template = new ResourceTemplate
                {
                    UriTemplate = resource.Uri, Name = resource.Name, Title = resource.Title,
                    Description = resource.Description, MimeType = resource.MimeType,
                };
            }

            public override ResourceTemplate ProtocolResourceTemplate => _template;

            public override Resource ProtocolResource => _resource;

            public override IReadOnlyList<object> Metadata => Array.Empty<object>();

            public override bool IsMatch(string uri) => string.Equals(uri, _resource.Uri, StringComparison.Ordinal);

            public override async ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken = default)
            {
                var up = _mirror.Up ?? throw new McpProtocolException("PadForge is not connected. Call status.", McpErrorCode.InternalError);
                return await up.ReadResourceAsync(new ReadResourceRequestParams { Uri = _resource.Uri }, cancellationToken).ConfigureAwait(false);
            }
        }

        // ─────────────────────────────────────────────
        //  The program that started this copy
        // ─────────────────────────────────────────────

        // ProcessBasicInformation, as .NET's Process.ParentProcessId asks it
        // (Process.Win32.cs). One question about this process, never a walk
        // of every process's record.
        private const int ProcessBasicInformation = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public uint ExitStatus;
            public IntPtr PebBaseAddress;
            public UIntPtr AffinityMask;
            public int BasePriority;
            public UIntPtr UniqueProcessId;
            public UIntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref PROCESS_BASIC_INFORMATION information,
            int length, out int returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>The image name of the process that started this one, or
        /// null when it is gone or answers no limited query. A claim, shown
        /// as one.</summary>
        internal static string ParentImage()
        {
            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformation, ref info,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            uint parent = unchecked((uint)info.InheritedFromUniqueProcessId.ToUInt64());
            string path = PadForge.Common.Input.ProcessImage.PathOf(parent);
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileName(path);
        }
    }

    /// <summary>
    /// What PadForge.xml says of Automation (#600), read by the copy an
    /// assistant starts, which loads no part of the app: the switch and the
    /// level, under PadForgeSettings, AppSettings. The file is the one
    /// beside PadForge.exe, as SettingsService.FindSettingsFile finds it.
    /// </summary>
    internal sealed record AutomationFileSettings(bool On, AutomationLevel Level)
    {
        internal static readonly AutomationFileSettings None = new(false, AutomationLevel.Commands);

        /// <summary>PadForge.xml beside this exe, else the older
        /// Settings.xml, as the app looks for them.</summary>
        internal static string DefaultPath()
        {
            string folder = AppContext.BaseDirectory;
            string primary = Path.Combine(folder, "PadForge.xml");
            if (File.Exists(primary)) return primary;
            string fallback = Path.Combine(folder, "Settings.xml");
            return File.Exists(fallback) ? fallback : primary;
        }

        /// <summary>The two values from a file, or <see cref="None"/> when
        /// it is missing or does not read.</summary>
        internal static AutomationFileSettings Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return None;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Read(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return None;
            }
        }

        private static readonly object KeptSync = new object();
        private static string _keptPath;
        private static DateTime _keptWritten;
        private static long _keptLength = -1;
        private static AutomationFileSettings _kept;

        /// <summary>The two values from a file, read again only when its
        /// last write time or its length changed. The copy asks at every try
        /// of the pipe while PadForge is away, and the file holds every saved
        /// device's settings before the two values (SettingsService writes
        /// AppSettings after them).</summary>
        internal static AutomationFileSettings Kept(string path, Func<string, AutomationFileSettings> read = null)
        {
            read ??= Read;
            DateTime written;
            long length;
            try
            {
                var info = new FileInfo(path ?? string.Empty);
                if (!info.Exists) return read(path);
                written = info.LastWriteTimeUtc;
                length = info.Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return read(path);
            }
            lock (KeptSync)
            {
                if (_kept != null && string.Equals(_keptPath, path, StringComparison.OrdinalIgnoreCase) && _keptWritten == written && _keptLength == length)
                    return _kept;
            }
            var settings = read(path);
            lock (KeptSync)
            {
                _keptPath = path;
                _keptWritten = written;
                _keptLength = length;
                _kept = settings;
            }
            return settings;
        }

        internal static AutomationFileSettings Read(Stream stream)
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = System.Xml.XmlReader.Create(stream, settings);
            bool on = false;
            int level = 0;
            if (!reader.ReadToFollowing("AppSettings")) return None;
            using var app = reader.ReadSubtree();
            app.Read();
            while (!app.EOF)
            {
                // Reading an element's content moves past it, so the next
                // element is looked at without another read.
                if (app.NodeType == System.Xml.XmlNodeType.Element && app.Depth == 1 && app.Name == "EnableAutomation")
                    on = string.Equals(app.ReadElementContentAsString().Trim(), "true", StringComparison.OrdinalIgnoreCase);
                else if (app.NodeType == System.Xml.XmlNodeType.Element && app.Depth == 1 && app.Name == "AutomationLevel")
                    int.TryParse(app.ReadElementContentAsString().Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out level);
                else app.Read();
            }
            return new AutomationFileSettings(on, AutomationLevels.FromSaved(level));
        }
    }
}
