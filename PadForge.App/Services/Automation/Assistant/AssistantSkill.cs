using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// PadForge's own skill (#600): a folder with SKILL.md and its references,
    /// carried inside the executable, so it always matches the version in use.
    /// It is served through the protocol's Skills extension
    /// (<c>io.modelcontextprotocol/skills</c>): <c>skills/list</c> and
    /// <c>skills/get</c> give its entry, with every file's URI, SHA-256 digest
    /// and size computed from the bytes served, and <c>resources/read</c>
    /// serves the files. Save the Skill writes the same folder for a host that
    /// reads skills from disk.
    ///
    /// <para>The SDK's own Skills package needs its hosting package and a
    /// dependency-injection host, which a server made with
    /// <c>McpServer.Create</c> over the pipe has not. The two handlers are
    /// written here against the extension's text
    /// (docs/extensions/skills/overview.mdx): list and get results carry
    /// <c>resultType</c>, <c>ttlMs</c> and <c>cacheScope</c>, an unknown URI is
    /// -32602, and the manifest names SKILL.md and every supporting file.</para>
    ///
    /// <para>No part of this class touches WPF: the copy an assistant starts
    /// serves the skill itself.</para>
    /// </summary>
    internal static class AssistantSkill
    {
        internal const string ExtensionId = "io.modelcontextprotocol/skills";
        internal const string Name = "padforge";
        internal const string Root = "skill://" + Name + "/";
        internal const string ListMethod = "skills/list";
        internal const string GetMethod = "skills/get";

        /// <summary>The prefix the skill's files carry inside the
        /// executable.</summary>
        private const string ResourcePrefix = "PadForge.Assistant.Skill/";

        /// <summary>How long a host may keep what it read. The skill changes
        /// only with PadForge's version.</summary>
        internal static readonly TimeSpan Keep = TimeSpan.FromHours(1);

        /// <summary>One file of the skill.</summary>
        internal sealed record SkillFile(string Path, byte[] Bytes)
        {
            public string Uri => Root + Path;
            public string Digest => "sha256:" + Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
            public long Size => Bytes.LongLength;
            public string Text => Encoding.UTF8.GetString(Bytes);
        }

        private static IReadOnlyList<SkillFile> _files;

        /// <summary>The skill's files, SKILL.md first and the rest by path,
        /// read once from the executable.</summary>
        internal static IReadOnlyList<SkillFile> Files => _files ??= Load();

        private static IReadOnlyList<SkillFile> Load()
        {
            var assembly = typeof(AssistantSkill).Assembly;
            var files = new List<SkillFile>();
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                using var stream = assembly.GetManifestResourceStream(name);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                string path = name.Substring(ResourcePrefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new SkillFile(path, path == "SKILL.md" ? Stamped(copy.ToArray()) : copy.ToArray()));
            }
            return files.OrderBy(f => f.Path == "SKILL.md" ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
        }

        /// <summary>The line of SKILL.md's frontmatter that carries the
        /// version, as the repository keeps it.</summary>
        internal const string VersionLine = "version: dev";

        /// <summary>SKILL.md with this PadForge's version in its frontmatter
        /// (#600), so a copy on disk says which build it describes.</summary>
        internal static byte[] Stamped(byte[] skill)
        {
            string text = Encoding.UTF8.GetString(skill);
            string stamped = text.Replace(VersionLine, "version: " + BuildIdentity.VersionText(BuildIdentity.Version));
            return Encoding.UTF8.GetBytes(stamped);
        }

        /// <summary>The YAML frontmatter of SKILL.md, each field as it is
        /// written. It holds plain one-line fields.</summary>
        internal static IReadOnlyList<(string Key, string Value)> Frontmatter()
        {
            var fields = new List<(string, string)>();
            string text = Files[0].Text.Replace("\r\n", "\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) return fields;
            int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return fields;
            foreach (string line in text.Substring(4, end - 4).Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                fields.Add((line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()));
            }
            return fields;
        }

        /// <summary>The skill's entry, as both methods give it.</summary>
        internal static JsonObject Entry()
        {
            var frontmatter = new JsonObject();
            foreach (var (key, value) in Frontmatter()) frontmatter[key] = value;
            var resources = new JsonArray();
            foreach (var file in Files)
                resources.Add(new JsonObject { ["uri"] = file.Uri, ["digest"] = file.Digest, ["size"] = file.Size });
            return new JsonObject
            {
                ["uri"] = Files[0].Uri,
                ["frontmatter"] = frontmatter,
                ["resources"] = resources,
            };
        }

        private static JsonObject Complete(JsonObject result)
        {
            result["resultType"] = "complete";
            result["ttlMs"] = (long)Keep.TotalMilliseconds;
            result["cacheScope"] = "public";
            return result;
        }

        /// <summary><c>skills/list</c>: the one skill, in one page.</summary>
        internal static JsonObject List() => Complete(new JsonObject { ["skills"] = new JsonArray(Entry()) });

        /// <summary><c>skills/get</c>: the skill by its URI, or -32602 for
        /// any other.</summary>
        internal static JsonObject Get(JsonNode parameters)
        {
            string uri = parameters?["uri"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? parameters["uri"].GetValue<string>() : null;
            if (!string.Equals(uri, Files[0].Uri, StringComparison.Ordinal))
                throw new McpProtocolException("No skill has the URI " + (uri ?? "(none)") + ". skills/list gives " + Files[0].Uri + ".", McpErrorCode.InvalidParams);
            return Complete(new JsonObject { ["skill"] = Entry() });
        }

#pragma warning disable MCPEXP002 // The request handlers of an extension are the SDK's extension point.
        /// <summary>The two handlers, for a server's options.</summary>
        internal static IEnumerable<McpServerRequestHandler> Handlers()
        {
            yield return new McpServerRequestHandler
            {
                Method = ListMethod,
                Handler = (context, ct) => ValueTask.FromResult<JsonNode>(List()),
            };
            yield return new McpServerRequestHandler
            {
                Method = GetMethod,
                Handler = (context, ct) => ValueTask.FromResult<JsonNode>(Get(context.Params)),
            };
        }
#pragma warning restore MCPEXP002

        /// <summary>The skill's files as resources, for
        /// <c>resources/read</c>.</summary>
        internal static IEnumerable<McpServerResource> Resources()
        {
            foreach (var file in Files)
                yield return new AssistantTextResource(file.Uri, Name + "/" + file.Path,
                    file.Path == "SKILL.md" ? "PadForge's skill" : "PadForge's skill: " + file.Path,
                    MimeOf(file.Path), () => file.Text, file.Size);
        }

        /// <summary>A skill file's type by its extension: the project embeds
        /// the skill's Markdown and JSON files.</summary>
        internal static string MimeOf(string path)
            => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/markdown";

        /// <summary>Save the Skill in this process: brings the folder
        /// <c>padforge</c> in <paramref name="folder"/> to exactly the skill's
        /// files (#600), writing only what differs and removing what this
        /// copy no longer has, and gives the folder. The page starts it with
        /// the user's own rights through <c>--install skill</c>.</summary>
        internal static string Save(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("No folder was given.", nameof(folder));
            string root = Path.Combine(folder, Name);
            Install.OwnFiles.Mirror(root, Files.Select(f => (f.Path, f.Bytes)).ToList());
            return root;
        }
    }

    /// <summary>A resource of fixed URI whose text is made when it is
    /// read.</summary>
    internal sealed class AssistantTextResource : McpServerResource
    {
        private readonly ResourceTemplate _template;
        private readonly Resource _resource;
        private readonly Func<string> _text;
        private readonly string _mime;
        private readonly TimeSpan _keep;
        private readonly CacheScope _scope;

        public AssistantTextResource(string uri, string name, string description, string mime, Func<string> text, long? size = null,
            TimeSpan? keep = null, CacheScope? scope = null)
        {
            _text = text;
            _mime = mime;
            _keep = keep ?? AssistantSkill.Keep;
            _scope = scope ?? CacheScope.Public;
            _template = new ResourceTemplate { UriTemplate = uri, Name = name, Description = description, MimeType = mime };
            _resource = new Resource { Uri = uri, Name = name, Description = description, MimeType = mime, Size = size };
        }

        public override ResourceTemplate ProtocolResourceTemplate => _template;

        public override Resource ProtocolResource => _resource;

        public override IReadOnlyList<object> Metadata => Array.Empty<object>();

        public override bool IsMatch(string uri) => string.Equals(uri, _resource.Uri, StringComparison.Ordinal);

        public override ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ReadResourceResult
            {
                Contents = new List<ResourceContents> { new TextResourceContents { Uri = _resource.Uri, MimeType = _mime, Text = _text() } },
                TimeToLive = _keep,
                CacheScope = _scope,
            });
    }
}
