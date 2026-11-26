using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace PadForge.Services.Install
{
    /// <summary>
    /// One assistant program PadForge adds itself to (#600): where its maker
    /// documents its Windows setup, through the maker's own add command where
    /// one is documented, else its own settings file, and the folder it reads
    /// skills from. The sources are the makers' own pages, read 2026-11-24
    /// (research R6 of #600).
    /// </summary>
    internal sealed class AssistantHost
    {
        /// <summary>The stable key a command line and the page name the
        /// program by: claude-code, codex, ...</summary>
        public string Key { get; init; }

        /// <summary>The program's name, the maker's own.</summary>
        public string Name { get; init; }

        /// <summary>The maker's command-line program whose own add command
        /// writes the entry, or null when the entry goes into a file.</summary>
        public string Command { get; init; }

        /// <summary>The command's words that add PadForge, for the exe
        /// given.</summary>
        public Func<string, string[]> AddWords { get; init; }

        /// <summary>The command's words that take PadForge out.</summary>
        public string[] RemoveWords { get; init; }

        /// <summary>When the command is not on this PC, the entry goes into
        /// <see cref="SettingsFile"/> instead. False when only the command
        /// may write it.</summary>
        public bool FileWhenNoCommand { get; init; }

        /// <summary>The settings file the entry lives in, or null when no
        /// folder of the user's is known.</summary>
        public Func<string> SettingsFile { get; init; }

        /// <summary>The key of the JSON object that holds the servers, or
        /// null for a TOML file.</summary>
        public string Container { get; init; }

        /// <summary>The entry for the exe given.</summary>
        public Func<string, JsonObject> Entry { get; init; }

        /// <summary>The folder the program reads skills from, or null when
        /// it takes skills only as an upload.</summary>
        public Func<string> SkillsFolder { get; init; }
    }

    /// <summary>The assistant programs whose makers document a Windows setup
    /// for a local server.</summary>
    internal static class AssistantHosts
    {
        /// <summary>The name PadForge's entry carries in every host.</summary>
        internal const string EntryName = "padforge";

        private static string InProfile(params string[] parts)
        {
            string profile = UserFolders.Of(UserFolder.Profile);
            return string.IsNullOrEmpty(profile) ? null : Path.Combine(new[] { profile }.Concat(parts).ToArray());
        }

        private static string Variable(string name)
        {
            // A folder a variable moves is the program's own documented
            // override. The test host reads the seam's folders alone.
            if (UserFolders.Seam != null || UserFolders.OffLimitsForTest) return null;
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static JsonObject Stdio(string exe, bool typed)
        {
            var entry = new JsonObject();
            if (typed) entry["type"] = "stdio";
            entry["command"] = exe;
            entry["args"] = new JsonArray("--mcp");
            return entry;
        }

        /// <summary>Claude Desktop's settings file, the one it reads: the copy
        /// in its package's own data folder when that copy exists, as on the
        /// PC this was built on, else the documented
        /// %APPDATA%\Claude\claude_desktop_config.json when that file exists,
        /// which the packaged app reads while it has no copy of its own, else
        /// the package's folder, where the packaged app writes, and with no
        /// package the documented file.</summary>
        internal static string ClaudeDesktopFile()
        {
            string packaged = UserFolders.PackagedRoaming(AssistantPrograms.ClaudeFamily) is { } folder
                ? Path.Combine(folder, "Claude", "claude_desktop_config.json")
                : null;
            string roaming = UserFolders.Of(UserFolder.RoamingData);
            string documented = string.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "Claude", "claude_desktop_config.json");
            if (packaged != null && File.Exists(packaged)) return packaged;
            if (documented != null && File.Exists(documented)) return documented;
            return packaged ?? documented;
        }

        private static string CopilotHome() => Variable("COPILOT_HOME") ?? InProfile(".copilot");

        // A place more than one program reads is one function that every host
        // reading it names, so two hosts share a place exactly when they hold
        // the same function (SharesSkills, SharesEntry). Declared before All,
        // which the class's static fields fill in their order.
        private static readonly Func<string> CopilotSkills = () => CopilotHome() is { } home ? Path.Combine(home, "skills") : null;
        private static readonly Func<string> CopilotSettings = () => CopilotHome() is { } home ? Path.Combine(home, "mcp-config.json") : null;
        private static readonly Func<string> AgentsSkills = () => InProfile(".agents", "skills");

        /// <summary>Every host, in the order a list shows them.</summary>
        internal static IReadOnlyList<AssistantHost> All { get; } = new[]
        {
            // claude.com/docs connect-local-servers: %APPDATA%\Claude\claude_desktop_config.json,
            // mcpServers. Skills only as a ZIP upload (Customize, Skills).
            new AssistantHost
            {
                Key = "claude-desktop", Name = "Claude Desktop",
                SettingsFile = ClaudeDesktopFile, Container = "mcpServers", Entry = exe => Stdio(exe, typed: false),
            },
            // code.claude.com/docs/en/mcp: claude mcp add [options] <name> -- <command> [args...],
            // user scope in ~/.claude.json. Skills in ~/.claude/skills. CLAUDE_CONFIG_DIR
            // moves both: with it set, Claude Code 2.1.331's own add wrote
            // %CLAUDE_CONFIG_DIR%\.claude.json (run on this PC on 2026-11-24).
            new AssistantHost
            {
                Key = "claude-code", Name = "Claude Code", Command = "claude",
                AddWords = exe => new[] { "mcp", "add", "--scope", "user", EntryName, "--", exe, "--mcp" },
                RemoveWords = new[] { "mcp", "remove", "--scope", "user", EntryName },
                SettingsFile = () => Variable("CLAUDE_CONFIG_DIR") is { } home ? Path.Combine(home, ".claude.json") : InProfile(".claude.json"),
                Container = "mcpServers", Entry = exe => Stdio(exe, typed: true),
                SkillsFolder = () => Variable("CLAUDE_CONFIG_DIR") is { } home ? Path.Combine(home, "skills") : InProfile(".claude", "skills"),
            },
            // learn.chatgpt.com extend/mcp: codex mcp add <server-name> -- <stdio server-command>,
            // ~/.codex/config.toml, which CODEX_HOME moves. Skills in $HOME/.agents/skills.
            new AssistantHost
            {
                Key = "codex", Name = "ChatGPT and Codex", Command = "codex",
                AddWords = exe => new[] { "mcp", "add", EntryName, "--", exe, "--mcp" },
                RemoveWords = new[] { "mcp", "remove", EntryName },
                SettingsFile = () => Variable("CODEX_HOME") is { } home ? Path.Combine(home, "config.toml") : InProfile(".codex", "config.toml"),
                SkillsFolder = AgentsSkills,
            },
            // gemini-cli docs/tools/mcp-server.md: gemini mcp add [options] <name> <commandOrUrl> [args...],
            // --scope user writes ~/.gemini/settings.json. Skills in ~/.gemini/skills.
            new AssistantHost
            {
                Key = "gemini-cli", Name = "Gemini CLI", Command = "gemini",
                AddWords = exe => new[] { "mcp", "add", "--scope", "user", EntryName, exe, "--mcp" },
                RemoveWords = new[] { "mcp", "remove", "--scope", "user", EntryName },
                SettingsFile = () => InProfile(".gemini", "settings.json"), Container = "mcpServers", Entry = exe => Stdio(exe, typed: false),
                SkillsFolder = () => InProfile(".gemini", "skills"),
            },
            // docs.github.com add-mcp-servers: copilot mcp add SERVER-NAME -- COMMAND [ARGS...],
            // ~/.copilot/mcp-config.json, which COPILOT_HOME moves. Skills in ~/.copilot/skills.
            new AssistantHost
            {
                Key = "copilot-cli", Name = "GitHub Copilot CLI", Command = "copilot", FileWhenNoCommand = true,
                AddWords = exe => new[] { "mcp", "add", EntryName, "--", exe, "--mcp" },
                RemoveWords = new[] { "mcp", "remove", EntryName },
                SettingsFile = CopilotSettings,
                Container = "mcpServers", Entry = exe => Stdio(exe, typed: true),
                SkillsFolder = CopilotSkills,
            },
            // VS Code docs agents/reference/mcp-configuration.md: the Copilot harness reads
            // ~/.copilot/mcp-config.json, mcpServers, type stdio. Skills in ~/.copilot/skills.
            new AssistantHost
            {
                Key = "vscode", Name = "Visual Studio Code",
                SettingsFile = CopilotSettings,
                Container = "mcpServers", Entry = exe => Stdio(exe, typed: true),
                SkillsFolder = CopilotSkills,
            },
            // learn.microsoft.com visualstudio/ide/mcp-servers: %USERPROFILE%\.mcp.json, servers.
            // Personal skills in ~/.copilot/skills.
            new AssistantHost
            {
                Key = "visual-studio", Name = "Visual Studio",
                SettingsFile = () => InProfile(".mcp.json"), Container = "servers", Entry = exe => Stdio(exe, typed: true),
                SkillsFolder = CopilotSkills,
            },
            // zed.dev/docs ai/mcp: %APPDATA%\Zed\settings.json, context_servers. Skills in ~/.agents/skills.
            new AssistantHost
            {
                Key = "zed", Name = "Zed",
                SettingsFile = () => UserFolders.Of(UserFolder.RoamingData) is { } roaming ? Path.Combine(roaming, "Zed", "settings.json") : null,
                Container = "context_servers", Entry = exe => Stdio(exe, typed: false),
                SkillsFolder = AgentsSkills,
            },
            // junie.jetbrains.com junie-cli-mcp-configuration: ~/.junie/mcp/mcp.json, mcpServers.
            // Skills in %USERPROFILE%\.junie\skills.
            new AssistantHost
            {
                Key = "junie", Name = "JetBrains Junie",
                SettingsFile = () => InProfile(".junie", "mcp", "mcp.json"), Container = "mcpServers", Entry = exe => Stdio(exe, typed: false),
                SkillsFolder = () => InProfile(".junie", "skills"),
            },
        };

        /// <summary>Whether two hosts read their skills from one folder, as
        /// GitHub Copilot CLI, VS Code and Visual Studio do, and Codex and
        /// Zed: taking the skill out for one takes it out for the
        /// other.</summary>
        internal static bool SharesSkills(AssistantHost a, AssistantHost b)
            => a != null && b != null && a != b && a.SkillsFolder != null && ReferenceEquals(a.SkillsFolder, b.SkillsFolder);

        /// <summary>Whether two hosts read PadForge's entry from one settings
        /// file, as GitHub Copilot CLI and VS Code do.</summary>
        internal static bool SharesEntry(AssistantHost a, AssistantHost b)
            => a != null && b != null && a != b && a.SettingsFile != null && ReferenceEquals(a.SettingsFile, b.SettingsFile)
               && string.Equals(a.Container, b.Container, StringComparison.Ordinal);

        /// <summary>The host with this key, or null.</summary>
        internal static AssistantHost Find(string key)
            => All.FirstOrDefault(h => string.Equals(h.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
