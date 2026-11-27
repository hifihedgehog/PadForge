using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A test that starts PadForge.exe, run only in a test host
    /// without administrator rights, so the child runs with a user's
    /// ordinary rights.</summary>
    public sealed class UnelevatedFactAttribute : FactAttribute
    {
        public UnelevatedFactAttribute()
        {
            if (Environment.IsPrivilegedProcess)
                Skip = "The test host has administrator rights, and a PadForge.exe it started would take the start with rights. Run it in a host without them.";
        }
    }

    /// <summary>The same, for a test with rows.</summary>
    public sealed class UnelevatedTheoryAttribute : TheoryAttribute
    {
        public UnelevatedTheoryAttribute()
        {
            if (Environment.IsPrivilegedProcess)
                Skip = "The test host has administrator rights, and a PadForge.exe it started would take the start with rights. Run it in a host without them.";
        }
    }

    /// <summary>
    /// The client run as an assistant host meets it (#600 item 52, AS-131,
    /// AS-132, AS-194): the app's own build of PadForge.exe, started as a
    /// child of this process, and spoken to through the official C# SDK's
    /// client, the library a host is built on: over the child's standard
    /// input and output for <c>--mcp</c>, signed in over HTTP for
    /// <c>--web</c>. Each child starts without a prompt for rights, writes
    /// nothing but protocol to standard output, ends when its input closes
    /// (the web helper when its pipe does), shows no window from its start to
    /// its end, and loads none of WPF. Every switch of the client run is run
    /// the same way and checked by everything it mapped up to its exit.
    ///
    /// <para>The SDK's own stdio start (StdioClientTransport) is not used:
    /// on Windows it wraps the command in <c>cmd.exe /c</c> and keeps the
    /// process it starts to itself (csharp-sdk v2.4.0,
    /// src/ModelContextProtocol.Core/Client/StdioClientTransport.cs:66-72,
    /// :183 and :227), so the child would run before it could be put in the
    /// test's job or watched by its process. The test starts the child itself
    /// and hands its pipes to the SDK's StreamClientTransport, whose session
    /// class does every read and write of the stdio one
    /// (StdioClientSessionTransport derives from StreamClientSessionTransport,
    /// StdioClientSessionTransport.cs:8).</para>
    ///
    /// <para>Every child is fenced as this host is: its variables name a
    /// pipe of the test's own (PADFORGE_PIPE, the variable PadForge names its
    /// web helper's pipe in), so its run reaches that pipe alone and never a
    /// PadForge that runs on the PC (ClientRun.Seams.ForThisRun), and it runs
    /// in a job that ends it when the test lets go.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  The app's build
        // ─────────────────────────────────────────────

        /// <summary>The app's own build of PadForge.exe in the configuration
        /// this test host was built in. It is self-contained, so it starts
        /// from its own folder with the runtime beside it, and it is the
        /// build of the same source: its PadForge.dll is the one this host
        /// loaded.</summary>
        internal static string BuiltExe()
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string framework = here.Name, configuration = here.Parent?.Name;
            string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            string exe = Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "bin", configuration, framework, runtime, "PadForge.exe");
            Assert.True(File.Exists(exe), "The app's build is not at " + exe + ". Build the solution.");
            Guid built = Mvid(Path.Combine(Path.GetDirectoryName(exe), "PadForge.dll"));
            Assert.True(built == typeof(ClientRun).Assembly.ManifestModule.ModuleVersionId,
                "The app's build at " + exe + " is not the one this test host was built with. Build the solution.");
            return exe;
        }

        private static Guid Mvid(string dll)
        {
            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }

        /// <summary>A folder of the test's own under the temp folder.</summary>
        internal static string TempFolder(string tag)
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.Tests." + tag + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        internal static void RemoveFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // ─────────────────────────────────────────────
        //  What the PC says of a process
        // ─────────────────────────────────────────────

        /// <summary>Each top-level window of a process: its handle, whether
        /// it is visible, and its class and title.</summary>
        internal static List<(IntPtr Handle, bool Visible, string Words)> TopLevelWindows(int processId)
        {
            var found = new List<(IntPtr, bool, string)>();
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == (uint)processId)
                {
                    var name = new StringBuilder(256);
                    GetClassNameW(hwnd, name, name.Capacity);
                    // A window of this very process gets WM_GETTEXT sent to
                    // its thread (Microsoft, GetWindowText), which waits for
                    // good on a thread of another test that does not pump. Its
                    // class is enough to name it.
                    var title = new StringBuilder(256);
                    if (processId != Environment.ProcessId) GetWindowTextW(hwnd, title, title.Capacity);
                    found.Add((hwnd, IsWindowVisible(hwnd), name + " \"" + title + "\""));
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>The file name of every image and file a process has
        /// mapped, read region by region (VirtualQueryEx, GetMappedFileName):
        /// a native library and a managed assembly alike, whether the runtime
        /// loaded it as an image or mapped it flat.</summary>
        internal static HashSet<string> MappedFiles(int processId)
        {
            IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            Assert.True(process != IntPtr.Zero, "the process " + processId + " cannot be read: " + Marshal.GetLastWin32Error());
            try { return MappedFiles(process); }
            finally { CloseHandle(process); }
        }

        /// <summary>The same, through a handle this process holds, as the
        /// debugger lists a child at its exit event.</summary>
        internal static HashSet<string> MappedFiles(SafeProcessHandle process)
        {
            bool held = false;
            try
            {
                process.DangerousAddRef(ref held);
                return MappedFiles(process.DangerousGetHandle());
            }
            finally
            {
                if (held) process.DangerousRelease();
            }
        }

        private static HashSet<string> MappedFiles(IntPtr process)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long address = 0;
            var name = new StringBuilder(1024);
            while (address < 0x7FFF_FFFF_FFFFL)
            {
                if (VirtualQueryEx(process, new IntPtr(address), out var region, new IntPtr(Marshal.SizeOf<MEMORY_BASIC_INFORMATION>())) == IntPtr.Zero) break;
                if (region.State == MemCommit && (region.Type == MemImage || region.Type == MemMapped))
                {
                    name.Clear();
                    if (GetMappedFileNameW(process, region.BaseAddress, name, (uint)name.Capacity) > 0)
                        names.Add(Path.GetFileName(name.ToString()));
                }
                long next = region.BaseAddress.ToInt64() + region.RegionSize.ToInt64();
                if (next <= address) break;
                address = next;
            }
            return names;
        }

        /// <summary>Whether a process's token is elevated, and its integrity
        /// level.</summary>
        internal static (int? Elevated, int? Integrity) Rights(int processId)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            Assert.True(process != IntPtr.Zero);
            try
            {
                Assert.True(OpenProcessToken(process, TokenQuery, out var token));
                using (token)
                    return (AutomationClientIdentity.ReadInt(token, TokenElevation), AutomationClientIdentity.ReadIntegrity(token));
            }
            finally { CloseHandle(process); }
        }

        /// <summary>WPF's assemblies and libraries, the shared framework's
        /// Microsoft.WindowsDesktop.App part of it: a process that ran any
        /// part of WPF has one of these mapped. PresentationFramework's
        /// themes go by its name's start.</summary>
        internal static readonly string[] Wpf =
        {
            "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "System.Xaml.dll",
            "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "PresentationFramework.Aero2.dll", "UIAutomationProvider.dll",
            "UIAutomationTypes.dll", "PresentationUI.dll", "ReachFramework.dll", "DirectWriteForwarder.dll",
            "System.Windows.Presentation.dll", "PenImc_cor3.dll",
        };

        internal static bool IsWpf(string file)
            => Wpf.Contains(file, StringComparer.OrdinalIgnoreCase) || file.StartsWith("PresentationFramework.", StringComparison.OrdinalIgnoreCase);

        /// <summary>Fails on any part of WPF among <paramref name="files"/>,
        /// naming when they were listed and what else was there.</summary>
        internal static void NoWpf(IEnumerable<string> files, string when, Child child)
        {
            var all = files.ToList();
            var wpf = all.Where(IsWpf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.True(wpf.Count == 0, "WPF in the child " + when + ": " + string.Join(", ", wpf)
                + ". Everything listed: " + string.Join(", ", all.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                + ". Standard error: " + child.StandardError);
        }

        /// <summary>The method sees what it is to look for. In this process,
        /// which loaded WPF, the listing names WPF's assemblies. A window of
        /// this process, made and never shown, is found by its process and
        /// read as hidden.</summary>
        private static void TheListingsSeeWhatTheyLookFor()
        {
            // Naming a type of WPF's loads its assembly.
            Assert.NotNull(typeof(System.Windows.Controls.Button).Assembly.Location);
            var own = MappedFiles(Environment.ProcessId);
            Assert.Contains("PresentationFramework.dll", own);
            Assert.Contains("System.Private.CoreLib.dll", own);
            Assert.Contains(own, IsWpf);
            Exception failure = null;
            var thread = new Thread(() =>
            {
                IntPtr handle = IntPtr.Zero;
                try
                {
                    // A top-level window of Windows' own class, never shown.
                    handle = CreateWindowExW(0, "STATIC", "PadForge child test", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Assert.NotEqual(IntPtr.Zero, handle);
                    var mine = TopLevelWindows(Environment.ProcessId);
                    Assert.Contains(mine, w => w.Handle == handle && !w.Visible);
                }
                catch (Exception e) { failure = e; }
                finally { if (handle != IntPtr.Zero) DestroyWindow(handle); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the window listing of this process did not answer");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The window listing of this process waits on no thread: a
        /// window whose thread does not pump, as another test's may not, is
        /// listed at once. Reading its title would send it WM_GETTEXT and wait
        /// for good, which held the listing in a parallel run.</summary>
        [Fact]
        public async Task AWindowOfThisProcessWhoseThreadDoesNotPump_IsListedAtOnce()
        {
            using var made = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IntPtr handle = IntPtr.Zero;
            var owner = new Thread(() =>
            {
                handle = CreateWindowExW(0, "STATIC", "PadForge child test", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                made.Set();
                // No message loop, and an MTA thread's wait pumps nothing.
                release.WaitHandle.WaitOne(30000);
                if (handle != IntPtr.Zero) DestroyWindow(handle);
            }) { IsBackground = true };
            owner.SetApartmentState(ApartmentState.MTA);
            owner.Start();
            try
            {
                Assert.True(made.Wait(10000));
                Assert.NotEqual(IntPtr.Zero, handle);
                var listing = Task.Run(() => TopLevelWindows(Environment.ProcessId));
                Assert.True(await Task.WhenAny(listing, Task.Delay(5000)) == listing, "the listing waited on the window's thread");
                Assert.Contains(await listing, w => w.Handle == handle && !w.Visible);
            }
            finally
            {
                release.Set();
                owner.Join(10000);
            }
        }

        /// <summary>The manifest asks Windows for no rights.</summary>
        private static void TheManifestAsksForNoRights()
        {
            string manifest = OffLimitsSource.Read("PadForge.App/app.manifest");
            Assert.Contains("<requestedExecutionLevel level=\"asInvoker\"", manifest);
        }

        /// <summary>Every byte of standard output is a JSON-RPC message, one
        /// a line with no BOM: the stdio transport's rule, "The server MUST
        /// NOT write anything to its stdout that is not a valid MCP message",
        /// and messages "are delimited by newlines, and MUST NOT contain
        /// embedded newlines" (MCP 2025-11-25, basic/transports).</summary>
        private static int OnlyProtocol(byte[] output)
        {
            Assert.False(output.Length >= 3 && output[0] == 0xEF && output[1] == 0xBB && output[2] == 0xBF, "standard output starts with a BOM");
            string text = Encoding.UTF8.GetString(output);
            Assert.True(text.Length == 0 || text.EndsWith('\n'), "standard output ends inside a line");
            int count = 0;
            foreach (string line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
            {
                JsonNode node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { throw new Xunit.Sdk.XunitException("standard output holds a line that is not JSON: " + line); }
                Assert.True(node is JsonObject obj && obj["jsonrpc"]?.GetValue<string>() == "2.0", "not a JSON-RPC message: " + line);
                count++;
            }
            return count;
        }

        private static JsonObject Request(int id, string method, JsonObject parameters = null)
        {
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) request["params"] = parameters;
            return request;
        }

        /// <summary>The build's stamp is read without resolving the type of
        /// any attribute of the assembly, which loaded WPF into the client
        /// run, and it reads what reflection reads.</summary>
        [Fact]
        public void TheBuildsStamp_ReadsWhatReflectionReads()
        {
            string Reflected(string key) => typeof(ClientRun).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
            Assert.Equal(Reflected("PadForgeCommit"), BuildIdentity.Commit);
            Assert.Equal(Reflected("PadForgeCommitSha"), BuildIdentity.CommitSha);
            Assert.Equal(BuildIdentity.ParseNumber(Reflected("PadForgeBuildNumber")), BuildIdentity.BuildNumber);
            Assert.True(BuildIdentity.Commit.Length == 0 || BuildIdentity.CommitSha.StartsWith(BuildIdentity.Commit, StringComparison.Ordinal));
            string source = OffLimitsSource.Read("PadForge.App/Services/UpdateService.cs");
            Assert.DoesNotContain("GetCustomAttributes<AssemblyMetadataAttribute>", source);
        }

        // ─────────────────────────────────────────────
        //  --mcp, through the SDK's client
        // ─────────────────────────────────────────────

        /// <summary>The name the test's client gives in its initialize, which
        /// the question card shows.</summary>
        private const string ClientName = "PadForge End-to-End Test";

        /// <summary>
        /// PadForge's side of the child's pipe at Change Settings: its
        /// Automation host, its assistants' server and its calls, as
        /// AssistantRig builds them, with two differences. The consent list
        /// starts empty, so the child's program asks on the question card
        /// as a first connection does. And undo puts back the catalog's
        /// settings, as the app's undo puts back its app settings
        /// (AssistantUndoStore.Take and Restore), where AssistantRig's undo
        /// puts back a profile counter alone.
        /// </summary>
        private sealed class ConsentRig : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;

            public readonly string PipeName = "PadForge.Tests.ChildAssistant." + Guid.NewGuid().ToString("N");
            public readonly FakeAssistantApp App = new();
            public readonly FakeAssistantCatalog Catalog = new();
            public readonly InlineAutomationUi Ui = new();
            public readonly AutomationHost Host;
            public readonly AutomationConsent Consent;

            public ConsentRig()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                Host = new AutomationHost(new RecordingAutomationTarget(), Ui, "PadForge.Tests.ChildAssistantControl." + Guid.NewGuid().ToString("N"),
                    automationPipeName: PipeName);
                Consent = TestConsent.Empty(Host);
                var undo = new AssistantUndo(Take, Restore);
                var calls = new AssistantCalls(Host, App, Catalog, Ui, undo, AssistantTools.All);
                new AssistantServer(Host, calls, Ui).Attach();
                Host.Apply(true, AutomationLevel.ChangeSettings);
                Assert.Equal(ExternalControlService.PipeState.Serving, Host.AutomationPipeState);
            }

            private sealed record Kept(double Number, bool Switch, string Choice);

            private AssistantUndoState Take()
                => new() { AppSettings = new Kept(Catalog.Number, Catalog.Switch, Catalog.Choice), Taken = DateTimeOffset.Now };

            private string Restore(AssistantUndoState state)
            {
                var kept = (Kept)state.AppSettings;
                Catalog.Number = kept.Number;
                Catalog.Switch = kept.Switch;
                Catalog.Choice = kept.Choice;
                return "Put back the app settings.";
            }

            public void Dispose()
            {
                Host.Dispose();
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
            }
        }

        /// <summary>The SDK's client over the child's standard input and
        /// output: initialize, then initialized, as a host opens a
        /// server.</summary>
        private static Task<McpClient> ConnectAsync(Child child)
            => McpClient.CreateAsync(new StreamClientTransport(child.ProtocolInput, child.ProtocolOutput), new McpClientOptions
            {
                ProtocolVersion = AssistantMirror.UpstreamVersion,
                ClientInfo = new Implementation { Name = ClientName, Version = "1" },
            });

        /// <summary>The value a read gives for a place.</summary>
        private static double Value(CallToolResult read)
        {
            Assert.False(read.IsError == true, AssistantRig.Text(read));
            return read.StructuredContent.Value.GetProperty("place").GetProperty("value").GetDouble();
        }

        private static string Events(WindowWatch watch, int processId) => string.Join(", ", watch.Of(processId));

        /// <summary>
        /// What the reference's own end-to-end test does, exe to exe (the
        /// built program started, the door found, a number set and read
        /// back), as a host's assistant
        /// does it: the built PadForge.exe with <c>--mcp</c>, the SDK's
        /// client over its standard input and output, the program's consent
        /// given on the question card, then at Change Settings a number set
        /// and read back, undone, and the old value read back. The child
        /// shows no window from its start to its end, loads no part of WPF,
        /// writes nothing but protocol to standard output and ends with 0
        /// when its input closes.
        /// </summary>
        [UnelevatedFact]
        public async Task TheAssistantsCopy_SetsANumber_ReadsItBack_AndUndoesIt_ThroughTheSdksClient()
        {
            TheManifestAsksForNoRights();
            TheListingsSeeWhatTheyLookFor();
            string exe = BuiltExe();
            using var rig = new ConsentRig();
            using var watch = new WindowWatch();
            using var child = new Child(exe, "--mcp", new Dictionary<string, string> { [WebHelper.PipeVariable] = rig.PipeName }, watch);
            var client = await ConnectAsync(child);
            try
            {
                Assert.Equal("padforge", client.ServerInfo?.Name);

                // A first connection at Change Settings asks the user, on the
                // question card that names the program, and the user allows
                // it.
                await AssistantRig.Until(() => rig.Host.Approvals.Pending.Any(p => p.Request.Kind == ApprovalKind.Consent), 30000);
                var card = rig.Host.Approvals.Pending.Single(p => p.Request.Kind == ApprovalKind.Consent);
                Assert.Contains(ClientName, card.Request.Title);
                Assert.True(rig.Host.Approvals.Answer(card.Id, true));
                // The answer is kept as the card's question goes on after it.
                await AssistantRig.Until(() => rig.Consent.Programs.Count == 1, 10000);
                Assert.Equal(AutomationLevel.ChangeSettings, rig.Consent.Programs.Single().Level);

                // PadForge's list grows with the level, the copy hears it, and
                // the client gets the change tools.
                IList<McpClientTool> tools = null;
                await AssistantRig.Until(() => (tools = client.ListToolsAsync().AsTask().GetAwaiter().GetResult()).Any(t => t.Name == "set"), 30000);
                Assert.Contains(tools, t => t.Name == "read");
                Assert.Contains(tools, t => t.Name == "undo");

                // A number set and read back, then undone and read back.
                const string place = "pad/Pad_Deadzone@c1";
                Assert.Equal(8, Value(await client.CallToolAsync("read", AssistantRig.Args(("id", place)))));
                var set = await client.CallToolAsync("set", AssistantRig.Args(("id", place), ("value", 35)));
                Assert.False(set.IsError == true, AssistantRig.Text(set));
                Assert.True(set.StructuredContent.Value.GetProperty("done").GetBoolean());
                Assert.Equal(35, rig.Catalog.Number);
                Assert.Equal(35, Value(await client.CallToolAsync("read", AssistantRig.Args(("id", place)))));
                var undone = await client.CallToolAsync("undo");
                Assert.False(undone.IsError == true, AssistantRig.Text(undone));
                Assert.True(undone.StructuredContent.Value.GetProperty("done").GetBoolean());
                Assert.Equal(8, rig.Catalog.Number);
                Assert.Equal(8, Value(await client.CallToolAsync("read", AssistantRig.Args(("id", place)))));

                // The skill, a docs page's copy among it, is the child's own.
                var page = await client.ReadResourceAsync(AssistantSkill.Root + "references/docs/features/assistants.md");
                Assert.StartsWith("<!-- " + AssistantDocs.Root + "features/assistants/", Assert.IsType<TextResourceContents>(page.Contents[0]).Text);

                // The watch takes a window this test shows and hides at once,
                // in the same watch, while the child runs.
                watch.Drain();

                // After real work: no part of WPF, beside the runtime and the
                // protocol's own library, which show the listing reads the
                // child.
                var mapped = MappedFiles(child.Id);
                Assert.Contains("System.Private.CoreLib.dll", mapped);
                Assert.Contains("ModelContextProtocol.Core.dll", mapped);
                Assert.Contains("System.IO.Pipes.dll", mapped);
                NoWpf(mapped, "after its calls", child);
                Assert.DoesNotContain(TopLevelWindows(child.Id), w => w.Visible);

                // The child has this host's rights, no more: no prompt stood.
                Assert.Equal(Rights(Environment.ProcessId), Rights(child.Id));
            }
            finally
            {
                await client.DisposeAsync();
            }

            // The host lets go: its input closes, and the child ends with 0.
            child.EndInput();
            Assert.True(child.Ends(20000), "the child did not end when its input closed. Standard error: " + child.StandardError);
            Assert.Equal(0, child.ExitCode);
            Assert.True(OnlyProtocol(child.StandardOutput) >= 10, "fewer messages than answers");
            // A window shown after the child ended comes through, so every
            // event of the child's has: none showed a window.
            watch.Drain();
            Assert.True(watch.Shown(child.Id).Count == 0, "the child showed a window: " + Events(watch, child.Id));
        }

        /// <summary>With no PadForge to reach, as a host usually finds it,
        /// the copy speaks its own words through the SDK's client: status
        /// says PadForge is not running. It loads no part of WPF, and reads
        /// no settings file, so it maps none of the runtime's XML reader.</summary>
        [UnelevatedFact]
        public async Task TheAssistantsCopy_WithNoPadForge_SaysSoInStatus_AndLoadsNoWpf()
        {
            string exe = BuiltExe();
            string nobody = "PadForge.Tests.Nobody." + Guid.NewGuid().ToString("N");
            using var watch = new WindowWatch();
            using var child = new Child(exe, "--mcp", new Dictionary<string, string> { [WebHelper.PipeVariable] = nobody }, watch);
            var client = await ConnectAsync(child);
            try
            {
                Assert.Equal("padforge", client.ServerInfo?.Name);
                Assert.Equal(new[] { "status" }, (await client.ListToolsAsync()).Select(t => t.Name));
                var status = (await client.CallToolAsync("status")).StructuredContent.Value;
                Assert.False(status.GetProperty("connected").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("message").GetString()));

                watch.Drain();
                var mapped = MappedFiles(child.Id);
                Assert.Contains("System.Private.CoreLib.dll", mapped);
                Assert.Contains("ModelContextProtocol.Core.dll", mapped);
                Assert.DoesNotContain("System.Private.Xml.dll", mapped);
                NoWpf(mapped, "after its calls", child);
            }
            finally
            {
                await client.DisposeAsync();
            }

            child.EndInput();
            Assert.True(child.Ends(20000), "the child did not end when its input closed. Standard error: " + child.StandardError);
            Assert.Equal(0, child.ExitCode);
            Assert.True(OnlyProtocol(child.StandardOutput) >= 3, "fewer messages than answers");
            watch.Drain();
            Assert.True(watch.Shown(child.Id).Count == 0, "the child showed a window: " + Events(watch, child.Id));
        }

        /// <summary>
        /// The production copy's start-up read of PadForge.xml (item 5 of the
        /// lane, AS-131): a test's child reads the settings file its test
        /// names (AssistantMirror.TestsSettingsVariable) through
        /// AutomationFileSettings.Read, the read the production copy makes of
        /// the file beside its exe. With Automation on at Change Settings and
        /// no PadForge to reach, it lists every tool, each of which would
        /// start PadForge, and a test's child starts nothing, so a call says
        /// PadForge did not start. The runtime's XML reader is mapped, which
        /// shows the read ran, and no part of WPF is.
        /// </summary>
        [UnelevatedFact]
        public async Task TheAssistantsCopy_ReadsTheSettingsFileAtItsStart_ListsWhatItsLevelAllows_AndLoadsNoWpf()
        {
            string exe = BuiltExe();
            string folder = TempFolder("settings");
            try
            {
                // PadForge.xml's form, under a name of the test's own.
                string file = Path.Combine(folder, "settings.xml");
                File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<PadForgeSettings>\n  <AppSettings>\n"
                    + "    <EnableAutomation>true</EnableAutomation>\n    <AutomationLevel>3</AutomationLevel>\n  </AppSettings>\n</PadForgeSettings>\n");
                Assert.Equal(new AutomationFileSettings(true, AutomationLevel.ChangeSettings), AutomationFileSettings.Read(file));
                string nobody = "PadForge.Tests.Nobody." + Guid.NewGuid().ToString("N");
                using var watch = new WindowWatch();
                using var child = new Child(exe, "--mcp", new Dictionary<string, string>
                {
                    [WebHelper.PipeVariable] = nobody,
                    [AssistantMirror.TestsSettingsVariable] = file,
                }, watch);
                var client = await ConnectAsync(child);
                try
                {
                    Assert.Equal(AssistantTools.All.Select(t => t.Name), (await client.ListToolsAsync()).Select(t => t.Name));
                    var called = await client.CallToolAsync("list_profiles");
                    Assert.True(called.IsError);
                    Assert.Contains("(not-connected)", AssistantRig.Text(called));

                    watch.Drain();
                    var mapped = MappedFiles(child.Id);
                    Assert.Contains("System.Private.CoreLib.dll", mapped);
                    Assert.Contains("System.Private.Xml.dll", mapped);
                    NoWpf(mapped, "after it read the settings file and listed every tool", child);
                }
                finally
                {
                    await client.DisposeAsync();
                }

                child.EndInput();
                Assert.True(child.Ends(20000), "the child did not end when its input closed. Standard error: " + child.StandardError);
                Assert.Equal(0, child.ExitCode);
                OnlyProtocol(child.StandardOutput);
                watch.Drain();
                Assert.True(watch.Shown(child.Id).Count == 0, "the child showed a window: " + Events(watch, child.Id));
            }
            finally
            {
                RemoveFolder(folder);
            }
        }

        // ─────────────────────────────────────────────
        //  Windows
        // ─────────────────────────────────────────────

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);

        private const uint ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010, ProcessQueryLimitedInformation = 0x1000;
        private const uint MemCommit = 0x1000, MemImage = 0x1000000, MemMapped = 0x40000;
        private const uint TokenQuery = 0x0008;
        private const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public IntPtr RegionSize;
            public uint State, Protect, Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        private const uint DuplicateSameAccess = 0x2;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
            out SafeFileHandle targetHandle, uint access, bool inherit, uint options);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafeFileHandle pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafeFileHandle pipe, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MEMORY_BASIC_INFORMATION info, IntPtr length);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetMappedFileNameW(IntPtr process, IntPtr address, StringBuilder name, uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

        // The job, as PluginLauncher makes its own (Services/Plugins/PluginLauncher.cs).
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    }
}
