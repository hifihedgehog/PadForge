using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PadForge.Services.Install;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// An assistant's settings file as the user keeps it: the comments in an
    /// empty object stay when PadForge adds its entry, and Claude Desktop's
    /// entry goes into the file the packaged app reads. Every folder is a
    /// test's own (UserFolders.Seam).
    /// </summary>
    [Collection("UserInstallStatics")]
    public sealed class UserInstallSharedFilesTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Install." + Guid.NewGuid().ToString("N"));
        private const string Exe = @"C:\Apps\PadForge\PadForge.exe";

        public UserInstallSharedFilesTests()
        {
            Directory.CreateDirectory(_root);
            UserFolders.Seam = f => Path.Combine(_root, f switch
            {
                UserFolder.Documents => "Documents",
                UserFolder.RoamingData => @"Profile\AppData\Roaming",
                UserFolder.LocalData => @"Profile\AppData\Local",
                _ => "Profile",
            });
            UserInstall.Exe = () => Exe;
        }

        public void Dispose()
        {
            UserFolders.Seam = null;
            UserInstall.Exe = () => Environment.ProcessPath;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string At(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

        private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        /// <summary>The comments of an object with no member stay when the
        /// entry goes in, in the servers' object and in the root, and the
        /// entry goes on a line of its own after them.</summary>
        [Theory]
        [InlineData("{\n  \"context_servers\": {\n    // mine\n  }\n}\n", "// mine")]
        [InlineData("{ /* notes */ }", "/* notes */")]
        [InlineData("{\r\n  \"context_servers\": { /* a */ // b\r\n  }\r\n}\r\n", "// b")]
        public void AnEmptyObjectsComments_StayWhenTheEntryGoesIn(string text, string comment)
        {
            string set = JsoncEdit.Set(text, "context_servers", "padforge", "{\"command\": \"x\"}");
            Assert.Contains(comment, set, StringComparison.Ordinal);
            var root = JsonNode.Parse(set, documentOptions: Jsonc).AsObject();
            Assert.Equal("x", root["context_servers"]["padforge"]["command"].GetValue<string>());
        }

        /// <summary>Through the install: Zed's settings with a comment in
        /// context_servers keep it, and the entry reads as current.</summary>
        [Fact]
        public void ZedsCommentedServers_KeepTheComment_AndTheEntryIsCurrent()
        {
            string zed = At("Profile", "AppData", "Roaming", "Zed", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(zed));
            File.WriteAllText(zed, "{\n  \"context_servers\": {\n    // mine\n  }\n}\n");
            var request = new InstallRequest(InstallWhat.Assistant, "zed");
            Assert.Equal(UserInstall.Done, UserInstall.Run(request, _ => { }, _ => { }));
            Assert.Contains("// mine", File.ReadAllText(zed), StringComparison.Ordinal);
            Assert.Equal(InstallState.Current, UserInstall.Check(request));
        }

        /// <summary>A packaged Claude Desktop whose own data folder holds no
        /// settings file yet reads the documented one, so PadForge's entry
        /// joins that file and every server in it stays. A package of
        /// another publisher whose family starts with Claude_ is not
        /// Claude's.</summary>
        [Fact]
        public void ThePackagedClaudeApp_WithNoFileOfItsOwn_GetsTheEntryInTheDocumentedFile()
        {
            string real = At("Profile", "AppData", "Roaming", "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            File.WriteAllText(real, "{\"mcpServers\":{\"files\":{\"command\":\"npx\",\"args\":[\"files\"]}}}");
            Directory.CreateDirectory(At("Profile", "AppData", "Local", "Packages", AssistantPrograms.ClaudeFamily, "LocalCache", "Roaming"));
            string other = At("Profile", "AppData", "Local", "Packages", "Claude_0000000000000", "LocalCache", "Roaming");
            Directory.CreateDirectory(Path.Combine(other, "Claude"));
            File.WriteAllText(Path.Combine(other, "Claude", "claude_desktop_config.json"), "{}");

            Assert.Equal(real, AssistantHosts.ClaudeDesktopFile());
            var request = new InstallRequest(InstallWhat.Assistant, "claude-desktop");
            Assert.Equal(UserInstall.Done, UserInstall.Run(request, _ => { }, _ => { }));
            var root = JsonNode.Parse(File.ReadAllText(real)).AsObject();
            Assert.Equal("npx", root["mcpServers"]["files"]["command"].GetValue<string>());
            Assert.Equal(Exe, root["mcpServers"]["padforge"]["command"].GetValue<string>());
            Assert.False(File.Exists(At("Profile", "AppData", "Local", "Packages", AssistantPrograms.ClaudeFamily, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(other, "Claude", "claude_desktop_config.json")));
            Assert.Equal(InstallState.Current, UserInstall.Check(request));

            // Once the package holds its own copy, that copy is the one.
            string packaged = At("Profile", "AppData", "Local", "Packages", AssistantPrograms.ClaudeFamily, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(packaged));
            File.WriteAllText(packaged, "{}");
            Assert.Equal(packaged, AssistantHosts.ClaudeDesktopFile());
        }

        /// <summary>With neither file there, the entry goes where the
        /// packaged app writes: its own data folder.</summary>
        [Fact]
        public void ThePackagedClaudeApp_WithNoFileAtAll_GetsItInItsOwnFolder()
        {
            Directory.CreateDirectory(At("Profile", "AppData", "Local", "Packages", AssistantPrograms.ClaudeFamily, "LocalCache", "Roaming"));
            Assert.Equal(At("Profile", "AppData", "Local", "Packages", AssistantPrograms.ClaudeFamily, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"),
                AssistantHosts.ClaudeDesktopFile());
        }

        /// <summary>The places the hosts share are the ones their resolved
        /// paths share, under one profile.</summary>
        [Fact]
        public void TheSharedPlaces_AreTheOnesThePathsShare()
        {
            {
                foreach (var a in AssistantHosts.All)
                    foreach (var b in AssistantHosts.All)
                    {
                        if (a == b) continue;
                        string sa = a.SkillsFolder?.Invoke(), sb = b.SkillsFolder?.Invoke();
                        Assert.Equal(sa != null && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase), AssistantHosts.SharesSkills(a, b));
                        string fa = a.SettingsFile?.Invoke(), fb = b.SettingsFile?.Invoke();
                        Assert.Equal(fa != null && string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase) && a.Container == b.Container,
                            AssistantHosts.SharesEntry(a, b));
                    }
                Assert.True(AssistantHosts.SharesSkills(AssistantHosts.Find("visual-studio"), AssistantHosts.Find("copilot-cli")));
                Assert.True(AssistantHosts.SharesSkills(AssistantHosts.Find("codex"), AssistantHosts.Find("zed")));
                Assert.True(AssistantHosts.SharesEntry(AssistantHosts.Find("vscode"), AssistantHosts.Find("copilot-cli")));
            }
        }
    }
}
