using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A test that starts PadForge.exe, run only in a test host
    /// without administrator rights. A child of a host with them would take
    /// the start with rights (ProtectedStart), which writes in
    /// %ProgramData% and can show a box.</summary>
    public sealed class UnelevatedFactAttribute : FactAttribute
    {
        public UnelevatedFactAttribute()
        {
            if (Environment.IsPrivilegedProcess)
                Skip = "The test host has administrator rights, and a PadForge.exe it started would take the start with rights. Run it in a host without them.";
        }
    }

    /// <summary>
    /// The client run as an assistant host meets it (#600 item 52, AS-131,
    /// AS-132, AS-194): the app's own build of PadForge.exe, started as a
    /// child of this process with <c>--mcp</c> and then <c>--web</c>, driven
    /// over its real standard input and output and over HTTP. The child
    /// starts without a prompt for rights, writes nothing but protocol to
    /// standard output, ends when its input closes (the web helper when its
    /// pipe does), shows no window, and loads none of WPF.
    ///
    /// <para>The child is fenced as this host is: its variables name a pipe
    /// of the test's own (PADFORGE_PIPE, the variable PadForge names its web
    /// helper's pipe in), so its run reaches that pipe alone and never a
    /// PadForge that runs on the PC (ClientRun.Seams.ForThisRun), and it runs
    /// in a job that ends it when the test lets go. A real web assistant's
    /// sign-in is not part of this test.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  The app's build, and a child of it
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

        /// <summary>A started child, in a job that ends it with this object,
        /// its standard output read byte for byte, its standard error drained,
        /// and its top-level windows watched from its start to its end.</summary>
        internal sealed class Child : IDisposable
        {
            public readonly Process Process;
            private readonly SafeFileHandle _job;
            private readonly MemoryStream _stdout = new();
            private readonly BlockingCollection<string> _lines = new();
            private readonly StringBuilder _stderr = new();
            private readonly Task _reading, _draining, _watching;
            private readonly CancellationTokenSource _stop = new();
            public readonly ConcurrentBag<string> VisibleWindows = new();
            public readonly ConcurrentBag<string> HiddenWindows = new();

            public Child(string exe, string arguments, IReadOnlyDictionary<string, string> variables)
            {
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                // The child's environment is this host's, with the test's own
                // pipe named and no web key left over.
                start.Environment.Remove(WebHelper.KeyVariable);
                foreach (var (name, value) in variables) start.Environment[name] = value;
                _job = CreateJobObjectW(IntPtr.Zero, null);
                Assert.False(_job.IsInvalid, "no job object");
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                Assert.True(SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()));
                // A start that needed rights would fail here with Windows'
                // ERROR_ELEVATION_REQUIRED: no prompt can stand between.
                Process = Process.Start(start);
                Assert.NotNull(Process);
                Assert.True(AssignProcessToJobObject(_job, Process.SafeHandle), "the child is in no job");
                _reading = Task.Run(ReadOut);
                _draining = Task.Run(async () =>
                {
                    var buffer = new char[4096];
                    int n;
                    while ((n = await Process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        lock (_stderr) _stderr.Append(buffer, 0, n);
                });
                _watching = Task.Run(Watch);
            }

            private void ReadOut()
            {
                var stream = Process.StandardOutput.BaseStream;
                var line = new List<byte>();
                var buffer = new byte[4096];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (_stdout) _stdout.Write(buffer, 0, n);
                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i] == (byte)'\n')
                        {
                            _lines.Add(Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'));
                            line.Clear();
                        }
                        else line.Add(buffer[i]);
                    }
                }
                _lines.CompleteAdding();
            }

            private async Task Watch()
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (var (_, visible, words) in TopLevelWindows(Process.Id))
                        (visible ? VisibleWindows : HiddenWindows).Add(words);
                    try { await Task.Delay(25, _stop.Token); } catch (OperationCanceledException) { return; }
                    if (Process.HasExited) return;
                }
            }

            public byte[] StandardOutput { get { lock (_stdout) return _stdout.ToArray(); } }

            /// <summary>Closes the job, which ends the child and what it
            /// started.</summary>
            public void EndJob() => _job.Dispose();

            /// <summary>Whether the test still holds the child's job, so
            /// nothing of the test's ended the child.</summary>
            public bool JobOpen => !_job.IsClosed;

            /// <summary>The next line of standard output, or null when none
            /// comes within <paramref name="ms"/>.</summary>
            public string NextLine(int ms) => _lines.TryTake(out string line, ms) ? line : null;

            public string StandardError { get { lock (_stderr) return _stderr.ToString(); } }

            /// <summary>Sends one JSON-RPC message as one line.</summary>
            public void Send(JsonObject message)
            {
                Process.StandardInput.Write(message.ToJsonString() + "\n");
                Process.StandardInput.Flush();
            }

            /// <summary>The answer to request <paramref name="id"/>, past any
            /// notice that comes first.</summary>
            public JsonNode Answer(int id, int ms = 30000)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < ms)
                {
                    if (!_lines.TryTake(out string line, 250)) { if (_lines.IsCompleted) break; continue; }
                    var node = JsonNode.Parse(line);
                    if (node?["id"] is JsonValue v && v.TryGetValue(out int got) && got == id) return node;
                }
                throw new TimeoutException("no answer to request " + id + ". Standard error: " + StandardError);
            }

            /// <summary>Waits for the child to end, then for everything it
            /// wrote.</summary>
            public bool Ends(int ms)
            {
                if (!Process.WaitForExit(ms)) return false;
                Process.WaitForExit();
                Task.WaitAll(new[] { _reading, _draining }, 10000);
                return true;
            }

            public void Dispose()
            {
                _stop.Cancel();
                try { _watching.Wait(2000); } catch { }
                // Closing the job's last handle ends the child and anything it
                // started.
                _job.Dispose();
                try { Process.WaitForExit(5000); } catch { }
                Process.Dispose();
            }
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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            Assert.True(process != IntPtr.Zero, "the process " + processId + " cannot be read: " + Marshal.GetLastWin32Error());
            try
            {
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
            }
            finally { CloseHandle(process); }
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

        /// <summary>WPF's assemblies and libraries: a process that loaded any
        /// part of WPF has one of these mapped.</summary>
        internal static readonly string[] Wpf =
        {
            "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "System.Xaml.dll",
            "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "PresentationFramework.Aero2.dll", "UIAutomationProvider.dll",
        };

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
        //  --mcp
        // ─────────────────────────────────────────────

        [UnelevatedFact]
        public void TheAssistantsCopy_AnsweredOverItsStandardInputAndOutput_IsAProtocolServerAlone()
        {
            TheManifestAsksForNoRights();
            TheListingsSeeWhatTheyLookFor();
            string exe = BuiltExe();
            using var rig = new AssistantRig(AutomationLevel.Read);
            using var child = new Child(exe, "--mcp", new Dictionary<string, string> { [WebHelper.PipeVariable] = rig.PipeName });

            // As a host opens a server: initialize, then initialized.
            child.Send(Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = AssistantMirror.UpstreamVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "PadForge Child Test", ["version"] = "1" },
            }));
            var hello = child.Answer(1);
            Assert.Equal("padforge", hello["result"]?["serverInfo"]?["name"]?.GetValue<string>());
            child.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });

            // The tools are the test's host's, through its own pipe: the fence
            // held, and the PC's PadForge was never reached.
            child.Send(Request(2, "tools/list"));
            var tools = child.Answer(2)["result"]?["tools"]?.AsArray().Select(t => t["name"]?.GetValue<string>()).ToList();
            Assert.Contains("list_controllers", tools);
            child.Send(Request(3, "tools/call", new JsonObject { ["name"] = "list_controllers", ["arguments"] = new JsonObject() }));
            var called = child.Answer(3)["result"];
            Assert.NotEqual(true, called?["isError"]?.GetValue<bool>());
            Assert.Equal(1, called?["structuredContent"]?["controllers"]?[0]?["number"]?.GetValue<int>());

            // The skill, a docs page's copy among it, is the child's own.
            child.Send(Request(4, "resources/read", new JsonObject { ["uri"] = AssistantSkill.Root + "references/docs/features/assistants.md" }));
            string page = child.Answer(4)["result"]?["contents"]?[0]?["text"]?.GetValue<string>();
            Assert.StartsWith("<!-- " + AssistantDocs.Root + "features/assistants/", page);

            // After real work: no part of WPF, beside the runtime and the
            // protocol's own library, which show the listing reads the child.
            var mapped = MappedFiles(child.Process.Id);
            Assert.Contains("System.Private.CoreLib.dll", mapped);
            Assert.Contains("ModelContextProtocol.Core.dll", mapped);
            Assert.Contains("System.IO.Pipes.dll", mapped);
            Assert.False(Wpf.Any(mapped.Contains), "WPF in the child: " + string.Join(", ", Wpf.Where(mapped.Contains)) + ". Everything mapped: " + string.Join(", ", mapped.OrderBy(m => m, StringComparer.OrdinalIgnoreCase)));

            // The child has this host's rights, no more: no prompt stood.
            Assert.Equal(Rights(Environment.ProcessId), Rights(child.Process.Id));

            // Closing its input ends it, with 0.
            child.Process.StandardInput.Close();
            Assert.True(child.Ends(20000), "the child did not end when its input closed. Standard error: " + child.StandardError);
            Assert.Equal(0, child.Process.ExitCode);
            Assert.True(OnlyProtocol(child.StandardOutput) >= 4, "fewer messages than answers");
            Assert.True(child.VisibleWindows.IsEmpty, "the child showed a window: " + string.Join(", ", child.VisibleWindows.Distinct()));
        }

        /// <summary>With no PadForge to reach, as a host usually finds it,
        /// the copy speaks its own words: status says PadForge is not
        /// running, and no part of WPF loads for them.</summary>
        [UnelevatedFact]
        public void TheAssistantsCopy_WithNoPadForge_SaysSoInStatus_AndLoadsNoWpf()
        {
            string exe = BuiltExe();
            string nobody = "PadForge.Tests.Nobody." + Guid.NewGuid().ToString("N");
            using var child = new Child(exe, "--mcp", new Dictionary<string, string> { [WebHelper.PipeVariable] = nobody });
            child.Send(Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = AssistantMirror.UpstreamVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "PadForge Child Test", ["version"] = "1" },
            }));
            Assert.Equal("padforge", child.Answer(1)["result"]?["serverInfo"]?["name"]?.GetValue<string>());
            child.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
            child.Send(Request(2, "tools/list"));
            Assert.Equal(new[] { "status" }, child.Answer(2)["result"]?["tools"]?.AsArray().Select(t => t["name"]?.GetValue<string>()));
            child.Send(Request(3, "tools/call", new JsonObject { ["name"] = "status", ["arguments"] = new JsonObject() }));
            var status = child.Answer(3)["result"]?["structuredContent"];
            Assert.False(status?["connected"]?.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(status?["message"]?.GetValue<string>()));

            var mapped = MappedFiles(child.Process.Id);
            Assert.Contains("System.Private.CoreLib.dll", mapped);
            Assert.Contains("ModelContextProtocol.Core.dll", mapped);
            Assert.False(Wpf.Any(mapped.Contains), "WPF in the child: " + string.Join(", ", Wpf.Where(mapped.Contains)));

            child.Process.StandardInput.Close();
            Assert.True(child.Ends(20000), "the child did not end when its input closed. Standard error: " + child.StandardError);
            Assert.Equal(0, child.Process.ExitCode);
            Assert.True(OnlyProtocol(child.StandardOutput) >= 3, "fewer messages than answers");
            Assert.True(child.VisibleWindows.IsEmpty, "the child showed a window: " + string.Join(", ", child.VisibleWindows.Distinct()));
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
