using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The protocol server inside PadForge (#597): one server per connection
    /// on the Automation pipe whose hello says <c>kind=mcp</c>, made with the
    /// SDK's <c>McpServer.Create</c> over a <c>StreamServerTransport</c> on
    /// the pipe's own stream. Its protocol version is left open, so every
    /// published revision from 2024-11-05 to 2026-07-28 is served.
    ///
    /// <para>Each session has its own tool collection, built for the level
    /// in force. When the user changes the level the collection changes in
    /// one step, and the SDK tells the client in the way its revision needs:
    /// a list-changed notice on a session that opened with the handshake,
    /// and a tagged notice on a listen stream for the 2026-07-28 revision.
    /// The level is asked again when a tool is called.</para>
    ///
    /// <para>The host's <see cref="AutomationHost.Call"/> is set here too,
    /// so a script's <c>call</c> line takes the same path as an assistant's
    /// call (<see cref="AssistantCalls"/>).</para>
    /// </summary>
    internal sealed class AssistantServer
    {
        /// <summary>The longest line the SDK is given to read: room for a
        /// 16 MB profile file in base64. Its reader has no cap of its own, so
        /// a longer line ends the session.</summary>
        internal const int LongestLine = 24 * 1024 * 1024;

        /// <summary>How often a session's lease is kept while its protocol
        /// runs.</summary>
        internal const int KeepEveryMs = 500;

        internal const string CatalogUri = "padforge://catalog/settings";
        internal const string PagesUri = "padforge://pages";

        private readonly AutomationHost _host;
        private readonly AssistantCalls _calls;
        private readonly IAutomationUi _ui;

        public AssistantServer(AutomationHost host, AssistantCalls calls, IAutomationUi ui)
        {
            _host = host;
            _calls = calls;
            _ui = ui;
        }

        public AssistantCalls Calls => _calls;

        /// <summary>Joins the host: an assistant's connection, a script's
        /// call line, Save the Skill and the Activity card's Undo.</summary>
        public void Attach()
        {
            _host.McpSession = ServeAsync;
            _host.Call = CallAsync;
            // With the user's own rights, through --install (#600). Waits for
            // it, so the page calls it off the interface's thread.
            _host.SaveSkill = folder =>
            {
                var (code, error) = Install.UserInstall.AsUserAsync(new Install.InstallRequest(Install.InstallWhat.Skill, folder)).GetAwaiter().GetResult();
                if (code != Install.UserInstall.Done) throw new InvalidOperationException(error ?? "The install ended with code " + code + ".");
            };
            if (_calls.Undo != null)
            {
                var undo = _calls.Undo;
                _host.CanUndo = undo.CanUndo;
                _host.Undo = entry => undo.Undo(entry);
            }
        }

        /// <summary>The session a peer names, which owns what it adds and
        /// holds.</summary>
        private AutomationSession SessionOf(AutomationPeer peer)
            => peer == null ? null : _host.Sessions.Snapshot().FirstOrDefault(s => s.Number == peer.Session);

        /// <summary>A script's <c>call</c> line.</summary>
        private async Task<AutomationCallResult> CallAsync(AutomationCall call, CancellationToken stopping)
        {
            if (!AssistantJson.TryParse(call.Arguments, out var arguments))
                return AutomationCallResult.Failure("bad-argument", "the arguments are one JSON object");
            var caller = new AssistantCaller(call.Peer, SessionOf(call.Peer));
            var outcome = await _calls.CallAsync(caller, call.Tool, arguments, stopping).ConfigureAwait(false);
            return outcome.Ok ? AutomationCallResult.Success(outcome.Json) : AutomationCallResult.Failure(outcome.Code, outcome.Words);
        }

        /// <summary>Runs one assistant's session to its end.</summary>
        internal async Task ServeAsync(AutomationPeer peer, Stream stream, CancellationToken stopping)
        {
            var session = SessionOf(peer);
            var caller = new AssistantCaller(peer, session);
            if (session != null) session.Outbox.Sink = session.Sink;
            // With Automation off an assistant is served nothing: the
            // session ends, and the copy it started says Automation is off.
            if (!_host.IsOn) return;
            using var keep = session == null ? null : new Timer(_ => session.Heard(), null, 0, KeepEveryMs);

            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            var resources = new McpServerResourceCollection();
            var prompts = new McpServerPrimitiveCollection<McpServerPrompt>();
            foreach (var prompt in AssistantGuidance.Prompts) prompts.Add(new AssistantPrompt(prompt));
            Follow(tools, resources, caller);

            var options = Options(tools, prompts, resources);
            EventHandler changed = (s, e) => Follow(tools, resources, caller);
            _host.Changed += changed;
            try
            {
                await using var capped = new LineCappedStream(stream, LongestLine);
                await using var transport = new StreamServerTransport(capped, stream, "PadForge");
                await using var server = McpServer.Create(transport, options);
                await server.RunAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _host.Changed -= changed;
                _calls.Forget(caller.Key);
            }
        }

        /// <summary>The options of one session's server.</summary>
        internal static McpServerOptions Options(McpServerPrimitiveCollection<McpServerTool> tools,
            McpServerPrimitiveCollection<McpServerPrompt> prompts, McpServerResourceCollection resources)
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
                // Every revision: the newest dropped the handshake older
                // clients still send.
                ProtocolVersion = null,
                ServerInstructions = AssistantGuidance.Instructions,
                ToolCollection = tools,
                PromptCollection = prompts,
                ResourceCollection = resources,
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Prompts = new PromptsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = true },
                    Extensions = new Dictionary<string, object> { [AssistantSkill.ExtensionId] = new JsonObject() },
                },
            };
#pragma warning disable MCPEXP002 // The skills extension's two methods.
            options.RequestHandlers = AssistantSkill.Handlers().ToList();
#pragma warning restore MCPEXP002
            return options;
        }

        /// <summary>Puts a session's tools and resources in step with the
        /// level in force. One change raises one notice: removals only take
        /// tools out, and anything that adds builds the list again in its one
        /// order.</summary>
        private void Follow(McpServerPrimitiveCollection<McpServerTool> tools, McpServerResourceCollection resources, AssistantCaller caller)
        {
            var allowed = _calls.Allowed().ToList();
            lock (tools)
            {
                var now = tools.Select(t => ((AssistantMcpTool)t).Definition).ToList();
                if (!now.SequenceEqual(allowed))
                {
                    using (tools.DeferChangedEvents())
                    {
                        bool onlyRemoves = allowed.All(now.Contains)
                            && now.Where(allowed.Contains).SequenceEqual(allowed);
                        if (onlyRemoves)
                        {
                            foreach (var tool in tools.ToList())
                                if (!allowed.Contains(((AssistantMcpTool)tool).Definition)) tools.Remove(tool);
                        }
                        else
                        {
                            tools.Clear();
                            foreach (var definition in allowed) tools.Add(new AssistantMcpTool(definition, _calls, caller));
                        }
                    }
                }
            }

            // The skill's files and the pages are for any level. The
            // settings catalog holds values, which Read allows.
            bool catalog = _host.Allows(AutomationLevel.Read);
            lock (resources)
            {
                var want = new List<McpServerResource>();
                want.AddRange(AssistantSkill.Resources());
                want.Add(new AssistantTextResource(PagesUri, "pages", "What each page of PadForge is for.", "text/markdown", PagesText));
                if (catalog)
                    want.Add(new AssistantTextResource(CatalogUri, "settings-catalog",
                        "Every setting on PadForge's pages: its ID, name, place, kind and range or choices.", "application/json", CatalogText,
                        keep: TimeSpan.FromSeconds(30), scope: CacheScope.Private));
                var have = resources.Select(r => r.ProtocolResource?.Uri).ToList();
                if (!have.SequenceEqual(want.Select(r => r.ProtocolResource.Uri)))
                {
                    using (resources.DeferChangedEvents())
                    {
                        resources.Clear();
                        foreach (var r in want) resources.Add(r);
                    }
                }
            }
        }

        private static string PagesText()
        {
            var text = new System.Text.StringBuilder("# PadForge's pages\n\n");
            foreach (var (page, what) in AssistantGuidance.Pages) text.Append("- ").Append(page).Append(": ").Append(what).Append('\n');
            return text.ToString();
        }

        /// <summary>The settings catalog: every setting Search reads from the
        /// pages, read on the interface's thread when the resource is
        /// read.</summary>
        private string CatalogText()
        {
            var settings = _calls.Settings;
            if (settings == null) return "[]";
            List<AssistantSetting> all = null;
            _ui.Invoke(() =>
            {
                all = settings.All();
                return string.Empty;
            }, CancellationToken.None);
            var outcome = _calls.Success(new SettingsResult { Settings = all ?? new List<AssistantSetting>() }, typeof(SettingsResult));
            return outcome.Json;
        }
    }

    /// <summary>A prompt that starts a common job. Its title and description
    /// are read in the interface's language each time a host lists it.</summary>
    internal sealed class AssistantPrompt : McpServerPrompt
    {
        private readonly AssistantGuidance.PromptText _text;

        public AssistantPrompt(AssistantGuidance.PromptText text) => _text = text;

        public override Prompt ProtocolPrompt => new Prompt
        {
            Name = _text.Name,
            Title = PadForge.Resources.Strings.Strings.Get(_text.TitleKey),
            Description = PadForge.Resources.Strings.Strings.Get(_text.DescriptionKey),
            Arguments = _text.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required }).ToList(),
        };

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken = default)
        {
            string body = AssistantGuidance.Lf(_text.Body);
            foreach (var argument in _text.Arguments)
            {
                string value = null;
                if (request.Params?.Arguments != null && request.Params.Arguments.TryGetValue(argument.Name, out var given))
                    value = given.ValueKind == JsonValueKind.String ? given.GetString() : given.ToString();
                // A value the user gave goes in as data, marked as theirs.
                body = body.Replace("{" + argument.Name + "}", string.IsNullOrWhiteSpace(value)
                    ? string.Empty
                    : " (the user said: " + JsonSerializer.Serialize(new OutsideText(value), AssistantJson.Options) + ")");
            }
            return ValueTask.FromResult(new GetPromptResult
            {
                Description = PadForge.Resources.Strings.Strings.Get(_text.DescriptionKey),
                Messages = new List<PromptMessage>
                {
                    new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = body } },
                },
            });
        }
    }

    /// <summary>
    /// The pipe's read side as the SDK reads it: a line longer than
    /// <see cref="Longest"/> bytes ends the stream, since the SDK's reader
    /// would hold all of it in memory.
    /// </summary>
    internal sealed class LineCappedStream : Stream
    {
        private readonly Stream _inner;

        public LineCappedStream(Stream inner, int longest)
        {
            _inner = inner;
            Longest = longest;
        }

        public int Longest { get; }

        /// <summary>Whether a line ran past the cap.</summary>
        public bool Overran { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => Count(buffer.AsSpan(offset, _inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Overran) return 0;
            int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Count(buffer.Span.Slice(0, n));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(ReadOnlySpan<byte> read)
        {
            if (Overran) return 0;
            int at = 0;
            while (at < read.Length)
            {
                int end = read.Slice(at).IndexOf((byte)10);
                _run += end < 0 ? read.Length - at : end;
                if (_run > Longest)
                {
                    Overran = true;
                    throw new IOException("A line ran past " + Longest + " bytes.");
                }
                if (end < 0) break;
                _run = 0;
                at += end + 1;
            }
            return read.Length;
        }

        // The bytes of the line being read so far.
        private long _run;

        protected override void Dispose(bool disposing)
        {
            // The pipe is the session's, which closes it.
            base.Dispose(disposing);
        }
    }
}
