using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The ChatGPT Secure MCP Tunnel (#600 DESIGN-2 N4): tunnel-client's
    /// command line carries PadForge's path with every backslash doubled,
    /// which its own parser reads back as the path, the runtime key and the
    /// tunnel ID go in its environment alone, the download is refused when
    /// its SHA-256 is not the release's, the key is kept only as a blob the
    /// helper made, and an empty box forgets it.
    /// </summary>
    public class TunnelClientTests
    {
        /// <summary>
        /// tunnel-client's parseCommandArgv (pkg/runtimeconfig/config.go at
        /// 393bbd74ff0d, saved in declines/src/A1-assistant-doors/
        /// tc_src_pkg_runtimeconfig_config.txt:2874-2940), line for line: a
        /// backslash escapes the next byte outside quotes and inside double
        /// quotes, single quotes take everything up to the next one, and
        /// spaces, tabs and line ends split words.
        /// </summary>
        internal static List<string> ParseCommandArgv(string raw)
        {
            string input = raw.Trim();
            if (input.Length == 0) throw new ArgumentException("command is empty");
            var args = new List<string>();
            var builder = new StringBuilder();
            bool inSingle = false, inDouble = false, escaped = false;
            foreach (char ch in input)
            {
                if (escaped)
                {
                    builder.Append(ch);
                    escaped = false;
                    continue;
                }
                if (inSingle)
                {
                    if (ch == '\'') { inSingle = false; continue; }
                    builder.Append(ch);
                    continue;
                }
                if (inDouble)
                {
                    switch (ch)
                    {
                        case '\\': escaped = true; break;
                        case '"': inDouble = false; break;
                        default: builder.Append(ch); break;
                    }
                    continue;
                }
                switch (ch)
                {
                    case '\\': escaped = true; break;
                    case '\'': inSingle = true; break;
                    case '"': inDouble = true; break;
                    case ' ': case '\t': case '\n': case '\r':
                        if (builder.Length > 0)
                        {
                            args.Add(builder.ToString());
                            builder.Clear();
                        }
                        break;
                    default: builder.Append(ch); break;
                }
            }
            if (escaped) throw new ArgumentException("unterminated escape sequence");
            if (inSingle || inDouble) throw new ArgumentException("unterminated quoted string");
            if (builder.Length > 0) args.Add(builder.ToString());
            if (args.Count == 0) throw new ArgumentException("command is empty");
            return args;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>A command line as Windows reads it into words, as Go's
        /// runtime reads its own on Windows.</summary>
        private static string[] Argv(string commandLine)
        {
            IntPtr list = CommandLineToArgvW(commandLine, out int count);
            try
            {
                var words = new string[count];
                for (int i = 0; i < count; i++) words[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                return words;
            }
            finally
            {
                LocalFree(list);
            }
        }

        [Theory]
        [InlineData(@"C:\PadForge\PadForge.exe")]
        [InlineData(@"C:\Program Files\PadForge Folder\PadForge.exe")]
        [InlineData(@"D:\a\b\c\")]
        public void TheMcpCommand_CarriesDoubledBackslashes_AndParsesBackToThePath(string path)
        {
            string command = TunnelClient.McpCommand(path);
            Assert.Equal("\"" + path.Replace(@"\", @"\\") + "\" --mcp", command);
            // Through Windows' own reading of the command line, then
            // tunnel-client's parser: the program it starts is the path.
            string line = "\"C:\\Work\\tunnel-client.exe\" " + TunnelClient.CommandLine(TunnelClient.Arguments(path, 51234));
            var argv = Argv(line);
            Assert.Equal(new[] { @"C:\Work\tunnel-client.exe", "run", "--mcp-command", command, "--health.listen-addr", "127.0.0.1:51234" }, argv);
            Assert.Equal(new[] { path, "--mcp" }, ParseCommandArgv(argv[3]));
        }

        [Fact]
        public void TheCommandAsCopiedBefore_LosesItsBackslashes_InTunnelClientsParser()
        {
            // The old row's command, which this replaces: the parser drops
            // every backslash inside the quotes.
            Assert.Equal(new[] { "C:PadForgePadForge.exe", "--mcp" }, ParseCommandArgv("\"C:\\PadForge\\PadForge.exe\" --mcp"));
        }

        [Fact]
        public void TheKeyAndTheTunnelId_GoInTheEnvironmentAlone_NeverOnTheCommandLine()
        {
            ProcessStartInfo seen = null;
            WebTunnels.StartSeam = info => { seen = info; return null; };
            try
            {
                const string key = "sk-proj-restricted-0123456789";
                Assert.Null(WebTunnels.StartVendor(@"C:\Work\tunnel-client.exe", @"C:\PadForge\PadForge.exe", key, "tunnel_7f3c2a91e4b05d68", 51234, null, null, null));
                Assert.NotNull(seen);
                Assert.DoesNotContain(key, seen.Arguments);
                Assert.DoesNotContain("tunnel_7f3c", seen.Arguments);
                Assert.Empty(seen.ArgumentList);
                Assert.Equal(key, seen.Environment[TunnelClient.KeyVariable]);
                Assert.Equal("tunnel_7f3c2a91e4b05d68", seen.Environment[TunnelClient.TunnelVariable]);
                Assert.False(seen.Environment.ContainsKey(WebHelper.KeyVariable));
                Assert.Contains("--mcp-command", seen.Arguments);
            }
            finally
            {
                WebTunnels.StartSeam = null;
            }
            // Without a test's own start, the test host starts nothing.
            Assert.Null(WebTunnels.StartVendor(@"C:\Work\tunnel-client.exe", @"C:\PadForge\PadForge.exe", "k", "tunnel_1", 51234, null, null, null));
        }

        private static byte[] Zip(string exeBody)
        {
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("tunnel-client-v0.0.21-windows-amd64/tunnel-client.exe").Open()))
                writer.Write(exeBody);
            return bytes.ToArray();
        }

        [Fact]
        public void TheChecksum_RefusesAChangedFile_AndTakesTheReleasesOwn()
        {
            byte[] zip = Zip("the real one");
            string asset = TunnelClient.AssetName("v0.0.21", Architecture.X64);
            Assert.Equal("tunnel-client-v0.0.21-windows-amd64.zip", asset);
            Assert.Equal("tunnel-client-v0.0.21-windows-arm64.zip", TunnelClient.AssetName("v0.0.21", Architecture.Arm64));
            string hash = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            string sums = new string('0', 64) + "  tunnel-client-v0.0.21-windows-arm64.zip\n" + hash + "  " + asset + "\n";
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.Tests.TunnelClient." + Guid.NewGuid().ToString("N"));
            try
            {
                // One byte changed: refused, and nothing is written.
                byte[] changed = (byte[])zip.Clone();
                changed[^5] ^= 1;
                Assert.Null(TunnelClient.Install(changed, sums, asset, folder, out string why));
                Assert.Contains("SHA-256", why);
                Assert.False(Directory.Exists(folder));
                Assert.Null(TunnelClient.Install(zip, sums.Replace(asset, "other.zip"), asset, folder, out why));
                Assert.Null(TunnelClient.Install(Zip("x"), sums, asset, folder, out _));

                string exe = TunnelClient.Install(zip, sums, asset, folder, out why);
                Assert.Null(why);
                Assert.Equal(Path.Combine(folder, "tunnel-client.exe"), exe);
                Assert.Equal("the real one", File.ReadAllText(exe));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void TheRelease_IsReadFromGitHubsAnswer_AndOnlyItsOwnDownloadsAreTaken()
        {
            string json = new JsonObject
            {
                ["tag_name"] = "v0.0.21",
                ["assets"] = new JsonArray(
                    new JsonObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = "https://github.com/openai/tunnel-client/releases/download/v0.0.21/SHA256SUMS.txt" },
                    new JsonObject { ["name"] = "tunnel-client-v0.0.21-windows-amd64.zip", ["browser_download_url"] = "https://github.com/openai/tunnel-client/releases/download/v0.0.21/tunnel-client-v0.0.21-windows-amd64.zip" },
                    new JsonObject { ["name"] = "tunnel-client-v0.0.21-windows-arm64.zip", ["browser_download_url"] = "https://evil.example/tunnel-client.zip" }),
            }.ToJsonString();
            var release = TunnelClient.Release(json, Architecture.X64);
            Assert.NotNull(release);
            Assert.Equal("v0.0.21", release.Value.Tag);
            Assert.EndsWith("/tunnel-client-v0.0.21-windows-amd64.zip", release.Value.Zip);
            // A download from anywhere else is not taken.
            Assert.Null(TunnelClient.Release(json, Architecture.Arm64));
            Assert.Null(TunnelClient.Release("{}", Architecture.X64));
        }

        [Fact]
        public async Task TheTestHost_DownloadsNothing()
        {
            var (exe, why) = await TunnelClient.DownloadAsync(default);
            Assert.Null(exe);
            Assert.NotNull(why);
        }

        [Fact]
        public void TheRuntimeKeyBox_HandsOnOnlyWhatWasTyped()
        {
            string standIn = PadForge.Views.AssistantsWebCard.StandIn;
            // Left as it was: no change.
            Assert.Null(PadForge.Views.AssistantsWebCard.Committed(standIn, kept: true));
            Assert.Null(PadForge.Views.AssistantsWebCard.Committed("", kept: false));
            // Emptied: the kept key is forgotten.
            Assert.Equal("", PadForge.Views.AssistantsWebCard.Committed("", kept: true));
            // A key pasted over the selected stand-in, or after it.
            Assert.Equal("sk-proj-1", PadForge.Views.AssistantsWebCard.Committed("sk-proj-1", kept: true));
            Assert.Equal("sk-proj-1", PadForge.Views.AssistantsWebCard.Committed(standIn + "sk-proj-1", kept: true));
            Assert.Equal("sk-proj-1", PadForge.Views.AssistantsWebCard.Committed("sk-proj-1" + standIn, kept: true));
            Assert.Equal("sk-proj-1", PadForge.Views.AssistantsWebCard.Committed("sk-proj-1", kept: false));
        }

        [Fact]
        public void TheKey_IsKeptForThisUserAlone_AsABlobOnly()
        {
            string blob = TunnelClient.Protect("sk-proj-restricted-0123456789");
            Assert.DoesNotContain("sk-proj", blob);
            Assert.Equal("sk-proj-restricted-0123456789", TunnelClient.Unprotect(blob));
            Assert.Null(TunnelClient.Unprotect("bm90IGEgYmxvYg=="));
            Assert.Null(TunnelClient.Unprotect(null));
            Assert.True(TunnelClient.IsTunnelId("tunnel_7f3c2a91e4b05d68"));
            Assert.False(TunnelClient.IsTunnelId("tunnel 1 && calc"));
        }
    }

    /// <summary>The app's side of the ChatGPT tunnel (#600 DESIGN-2 N4):
    /// the key reaches the helper once and comes back as a blob, which
    /// alone PadForge keeps and the helper is told, an empty box forgets
    /// it, and picking the tunnel fetches tunnel-client and allows its
    /// stdio copy.</summary>
    [Collection("SettingsManagerStatics")]
    public class TunnelClientDoorTests
    {
        [Fact]
        public async Task TheKey_GoesToTheHelperOnce_ComesBackAsABlob_AndAnEmptyBoxForgetsIt()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName)
            {
                DownloadSeam = _ => Task.FromResult<(string, string)>((@"C:\Work\v0.0.21\tunnel-client.exe", null)),
                KeptSeam = () => null,
            };
            door.TurnOn();
            // No helper connected: the key is not kept, and nothing holds it.
            Assert.False(door.SetRuntimeKey("sk-proj-1"));
            Assert.NotNull(door.RuntimeKeyWords);
            Assert.Null(store.RuntimeKeyBlob);

            // With the helper's pipe, the key goes once, as its own line.
            using var pipe = new PipePair();
            var serving = door.ServeHelperAsync(pipe.App, default);
            string first = await pipe.ReadAsync();
            Assert.Contains("\"t\":\"state\"", first);
            Assert.True(door.SetRuntimeKey("sk-proj-1"));
            string protect = await pipe.ReadAsync();
            Assert.Equal("protect", JsonNode.Parse(protect)["t"].GetValue<string>());
            door.Take(new JsonObject { ["t"] = "protected", ["blob"] = "QkxPQg==" });
            Assert.Equal("QkxPQg==", store.RuntimeKeyBlob);

            door.PickTunnel(TunnelClient.Key);
            await door.DownloadDone();
            door.SetTunnelId("tunnel_7f3c2a91e4b05d68");
            var state = door.State();
            Assert.Equal("QkxPQg==", state["tunnelKeyBlob"].GetValue<string>());
            Assert.Equal("tunnel_7f3c2a91e4b05d68", state["tunnelId"].GetValue<string>());
            Assert.Equal(@"C:\Work\v0.0.21\tunnel-client.exe", state["tunnelPath"].GetValue<string>());
            Assert.DoesNotContain("sk-proj", state.ToJsonString());
            Assert.DoesNotContain("sk-proj", Xml(store));
            // Its stdio copy is allowed, as Add allows an assistant.
            Assert.Contains((rig.Host.Sessions.Consent ?? AutomationConsent.Shared).Programs, p => p.Name == "ChatGPT");

            // An empty box forgets the key.
            Assert.True(door.SetRuntimeKey(""));
            Assert.Null(store.RuntimeKeyBlob);
            Assert.Null(door.State()["tunnelKeyBlob"]);
            Assert.DoesNotContain("QkxPQg==", Xml(store));
        }

        private static string Xml(WebAccessStore store)
        {
            var data = store.Save();
            if (data == null) return string.Empty;
            var text = new StringWriter();
            new System.Xml.Serialization.XmlSerializer(typeof(WebAccessData)).Serialize(text, data);
            return text.ToString();
        }

        /// <summary>Two ends of an in-memory pipe: what the door writes, the
        /// test reads line by line.</summary>
        private sealed class PipePair : IDisposable
        {
            private readonly System.IO.Pipes.AnonymousPipeServerStream _down = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeClientStream _read;
            private readonly System.IO.Pipes.AnonymousPipeServerStream _up = new(System.IO.Pipes.PipeDirection.Out);
            private readonly System.IO.Pipes.AnonymousPipeClientStream _upRead;
            public readonly Stream App;

            public PipePair()
            {
                _read = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _down.ClientSafePipeHandle);
                _upRead = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _up.ClientSafePipeHandle);
                App = new Duplex(_upRead, _down);
            }

            public Task<string> ReadAsync() => WebLink.ReadLineAsync(_read, default, timeoutMs: 5000);

            public void Dispose()
            {
                _up.Dispose();
                _upRead.Dispose();
                _read.Dispose();
                _down.Dispose();
            }

            /// <summary>Reads come up one pipe, which the test never writes,
            /// and writes go down the other.</summary>
            private sealed class Duplex : Stream
            {
                private readonly Stream _in, _out;
                public Duplex(Stream input, Stream output) { _in = input; _out = output; }
                public override bool CanRead => true;
                public override bool CanSeek => false;
                public override bool CanWrite => true;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override void Flush() => _out.Flush();
                public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
                // Each way on its own stream: Stream's own async calls take
                // turns, and the door's read waits for as long as the test.
                public override ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancel = default) => _in.ReadAsync(buffer, cancel);
                public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancel = default) => _out.WriteAsync(buffer, cancel);
                public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancel) => _out.WriteAsync(buffer, offset, count, cancel);
                public override Task FlushAsync(System.Threading.CancellationToken cancel) => _out.FlushAsync(cancel);
            }
        }
    }
}
