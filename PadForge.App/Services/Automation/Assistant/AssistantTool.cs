using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PadForge.Services.Automation
{
    /// <summary>A failure an assistant can act on: its code, which a script
    /// reads and Activity turns into plain words, and the words that tell the
    /// assistant what to do. A tool throws it and the call returns it inside
    /// its result.</summary>
    internal sealed class AssistantRefusal : Exception
    {
        public AssistantRefusal(string code, string words) : base(words)
        {
            Code = code;
            Words = words;
        }

        public string Code { get; }
        public string Words { get; }
    }

    /// <summary>What every tool that changes something returns beside its
    /// own fields: whether it was done, and when it waits for the user, the
    /// request to ask about again.</summary>
    internal class AssistantChangeResult
    {
        [Description("True when the change was made. False while it waits for the user's answer.")]
        public bool Done { get; set; } = true;

        [Description("Set while the change waits for the user. Pass it to check_request to learn how it ended.")]
        public int? PendingRequest { get; set; }

        [Description("What happened, in a sentence.")]
        public string Message { get; set; }
    }

    /// <summary>
    /// One tool (#597): its name, the words a host and an assistant read, the
    /// level it needs, its four hints, the types of its arguments and its
    /// result, and what it does. The list of them is
    /// <see cref="AssistantTools.All"/>, in the order every list keeps.
    /// </summary>
    internal sealed class AssistantToolDefinition
    {
        public string Name { get; init; }
        public string Title { get; init; }
        public string Description { get; init; }
        public AutomationLevel Level { get; init; }
        public bool ReadOnly { get; init; }
        public bool Destructive { get; init; }
        public bool Idempotent { get; init; }
        public bool OpenWorld { get; init; }
        public Type Arguments { get; init; }
        public Type Result { get; init; }

        /// <summary>Whether a call makes a change a person could make on the
        /// pages, which the undo journal follows.</summary>
        public bool Changes { get; init; }

        /// <summary>Runs the call with its arguments read. Called on a
        /// worker. Returns the result, of <see cref="Result"/>'s type, or
        /// throws <see cref="AssistantRefusal"/>.</summary>
        public Func<AssistantInvocation, object, CancellationToken, Task<object>> Run { get; init; }

        private Tool _tool;

        /// <summary>The tool as the protocol lists it, made once.</summary>
        public Tool ProtocolTool => _tool ??= new Tool
        {
            Name = Name,
            Title = Title,
            Description = Description,
            InputSchema = AssistantJson.SchemaOf(Arguments),
            OutputSchema = AssistantJson.SchemaOf(Result),
            Annotations = new ToolAnnotations
            {
                Title = Title,
                ReadOnlyHint = ReadOnly,
                DestructiveHint = Destructive,
                IdempotentHint = Idempotent,
                OpenWorldHint = OpenWorld,
            },
        };
    }

    /// <summary>
    /// A tool as the SDK serves it to one session: the definition's protocol
    /// form, and a call that goes to <see cref="AssistantCalls"/>, the one
    /// path an assistant's call and a script's <c>call</c> line share.
    /// </summary>
    internal sealed class AssistantMcpTool : McpServerTool
    {
        private readonly AssistantToolDefinition _definition;
        private readonly AssistantCalls _calls;
        private readonly AssistantCaller _caller;

        public AssistantMcpTool(AssistantToolDefinition definition, AssistantCalls calls, AssistantCaller caller)
        {
            _definition = definition;
            _calls = calls;
            _caller = caller;
        }

        public AssistantToolDefinition Definition => _definition;

        public override Tool ProtocolTool => _definition.ProtocolTool;

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            JsonElement arguments = default;
            var given = request.Params?.Arguments;
            if (given != null)
                arguments = JsonSerializer.SerializeToElement(given, AssistantJson.Options);
            var outcome = await _calls.CallAsync(_caller, _definition.Name, arguments, cancellationToken).ConfigureAwait(false);
            return ToResult(outcome);
        }

        /// <summary>An outcome as the protocol carries it: the structured
        /// result and the same JSON as text, or a failure's words inside the
        /// result.</summary>
        internal static CallToolResult ToResult(AssistantOutcome outcome)
        {
            if (outcome.Ok)
            {
                var content = new List<ContentBlock> { new TextContentBlock { Text = outcome.Json } };
                // A result that names the documentation page it comes from
                // points at it with a resource link (#600).
                if (outcome.Structured is { ValueKind: JsonValueKind.Object } structured
                    && structured.TryGetProperty("docs", out var docs) && docs.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(docs.GetString(), UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps)
                {
                    content.Add(new ResourceLinkBlock
                    {
                        Uri = page.AbsoluteUri,
                        Name = "padforge-docs",
                        Title = "PadForge's documentation",
                        Description = "The documentation page this comes from.",
                        MimeType = "text/html",
                    });
                    // The same page as the skill carries it, which
                    // resources/read serves, for an assistant that does not
                    // read the web. An https address is one the client
                    // fetches itself (MCP, resources, common URI schemes).
                    if (AssistantDocs.CopyUri(page.AbsoluteUri) is { } copy)
                        content.Add(new ResourceLinkBlock
                        {
                            Uri = copy,
                            Name = "padforge-docs-copy",
                            Title = "PadForge's documentation, in PadForge",
                            Description = "The same page, as PadForge's skill carries it.",
                            MimeType = "text/markdown",
                        });
                }
                return new CallToolResult
                {
                    StructuredContent = outcome.Structured,
                    Content = content,
                    IsError = false,
                };
            }
            if (outcome.UnknownTool)
                throw new McpProtocolException(outcome.Words, McpErrorCode.InvalidParams);
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = outcome.Words + " (" + outcome.Code + ")" } },
            };
        }
    }

    /// <summary>What a call came to, for an assistant and for a script
    /// alike.</summary>
    internal sealed class AssistantOutcome
    {
        public bool Ok { get; init; }
        public string Json { get; init; }
        public JsonElement? Structured { get; init; }
        public string Code { get; init; }
        public string Words { get; init; }

        /// <summary>No tool of that name serves the program now: a
        /// protocol error for an assistant, not a result.</summary>
        public bool UnknownTool { get; init; }

        internal static AssistantOutcome Failure(string code, string words, bool unknownTool = false)
            => new() { Ok = false, Code = code, Words = words, UnknownTool = unknownTool };
    }

    /// <summary>
    /// Who calls (#597): one program's connection on the Automation pipe, an
    /// assistant through its copy of PadForge.exe or a script through its
    /// <c>call</c> lines. The session it holds is where a device it adds and
    /// a control it holds belong.
    /// </summary>
    internal sealed class AssistantCaller
    {
        public AssistantCaller(AutomationPeer peer, AutomationSession session)
        {
            Peer = peer;
            Session = session;
        }

        public AutomationPeer Peer { get; }
        public AutomationSession Session { get; }

        /// <summary>The key the rate limit, the undo journal and the pending
        /// requests know the program by (#600): the first connection of an
        /// assistant's helper, which keeps one resume secret across a pipe
        /// drop, else the connection.</summary>
        public int Key => Session != null && Session.CallerKey > 0 ? Session.CallerKey : Peer?.Session ?? 0;

        /// <summary>The program as Activity shows it.</summary>
        public AutomationProgram Program
            => Session?.Program ?? new AutomationProgram(Peer?.Name, Peer?.ProcessId ?? 0, Peer?.ImageName);
    }
}
