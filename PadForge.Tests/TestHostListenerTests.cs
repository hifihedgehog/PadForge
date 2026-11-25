using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>The TCP listeners and UDP sockets this process holds, read
    /// from the system's own tables with their owners. Get-NetTCPConnection
    /// and Get-NetUDPEndpoint show the same rows.</summary>
    internal static class TestHostListeners
    {
        private const int AF_INET = 2, AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3, UDP_TABLE_OWNER_PID = 1;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122, ERROR_NOT_SUPPORTED = 50;

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        internal static List<IPEndPoint> Tcp() => Read(tcp: true, v6: false).Concat(Read(tcp: true, v6: true)).ToList();
        internal static List<IPEndPoint> Udp() => Read(tcp: false, v6: false).Concat(Read(tcp: false, v6: true)).ToList();

        // The four owner tables share one shape: a row count, then rows that
        // end in the owner's process id. MIB_TCPROW_OWNER_PID is state,
        // address, port and three more fields. MIB_UDPROW_OWNER_PID is address
        // and port. The IPv6 rows open with a 16-byte address and its scope.
        private static List<IPEndPoint> Read(bool tcp, bool v6)
        {
            int family = v6 ? AF_INET6 : AF_INET;
            int rowSize = tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12);
            int addressAt = tcp && !v6 ? 4 : 0, portAt = v6 ? 20 : (tcp ? 8 : 4), ownerAt = rowSize - 4;
            var mine = new List<IPEndPoint>();
            IntPtr table = IntPtr.Zero;
            try
            {
                int size = 0;
                uint result;
                do
                {
                    Marshal.FreeHGlobal(table);
                    table = Marshal.AllocHGlobal(Math.Max(size, 4));
                    result = tcp
                        ? GetExtendedTcpTable(table, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0)
                        : GetExtendedUdpTable(table, ref size, false, family, UDP_TABLE_OWNER_PID, 0);
                }
                while (result == ERROR_INSUFFICIENT_BUFFER);
                // A PC with no IPv6 has no IPv6 rows to read.
                if (v6 && result == ERROR_NOT_SUPPORTED) return mine;
                Assert.Equal(0u, result);

                int rows = Marshal.ReadInt32(table);
                for (int i = 0; i < rows; i++)
                {
                    IntPtr row = table + 4 + i * rowSize;
                    if (Marshal.ReadInt32(row, ownerAt) != Environment.ProcessId) continue;
                    var address = new byte[v6 ? 16 : 4];
                    Marshal.Copy(row + addressAt, address, 0, address.Length);
                    // The port is in network order in the field's first two bytes.
                    int port = (Marshal.ReadByte(row, portAt) << 8) | Marshal.ReadByte(row, portAt + 1);
                    mine.Add(new IPEndPoint(new IPAddress(address), port));
                }
            }
            finally { Marshal.FreeHGlobal(table); }
            return mine;
        }
    }

    /// <summary>No test opens a listener the network can reach. Windows
    /// Firewall asks about a program the first time it listens beyond
    /// loopback, once for every path the program runs from, and the test
    /// host runs from a new path in every worktree. So every listener a test
    /// starts is on 127.0.0.1, the app's own classes included, which take a
    /// bind address for it. The app itself listens where it always did.
    ///
    /// <para>The rule is held four ways. Each fixture reads back the address
    /// its listener bound (LinkServerTests.StartOnFreePort,
    /// HeadTrackingInputTests). LinkServerTests reads the system's tables
    /// for the whole test host while its servers are up. The tests here read
    /// the sources, which is what catches a listener in a test that has not
    /// been written yet. And the gate every listener of the app asks refuses
    /// a bind beyond loopback in this process (NetworkListenerGateTests
    /// below), which holds when the code under test is what changed.</para></summary>
    public class TestHostListenerTests
    {
        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found above " + AppContext.BaseDirectory);
        }

        /// <summary>App classes that listen on every address and take no
        /// bind address. No test builds one. A test that needs one gives it
        /// a bind address first, as LinkServer and HeadTrackerDevice
        /// have.</summary>
        private static readonly string[] EveryAddressOnly =
        {
            "LinkDiscovery", "UdpKrpcTransport", "OutGaugeTelemetrySource", "ForzaUdpTelemetrySource",
            "CodemastersUdpTelemetrySource", "TelemetryHub", "HttpWebControllerListener",
        };

        /// <summary>A source line without its comment.</summary>
        private static string CodeOf(string line)
        {
            bool text = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (text && c == '\\') i++;
                else if (c == '"') text = !text;
                else if (!text && c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line.Substring(0, i);
            }
            return line;
        }

        /// <summary>The statement that starts at <paramref name="start"/>,
        /// through the semicolon that ends it. Semicolons inside brackets
        /// and strings belong to lambdas and literals.</summary>
        private static string StatementAt(string code, int start)
        {
            int depth = 0;
            bool text = false;
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (text) { if (c == '\\') i++; else if (c == '"') text = false; }
                else if (c == '"') text = true;
                else if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ';' && depth <= 0) return code.Substring(start, i - start + 1);
            }
            return code.Substring(start);
        }

        private static IEnumerable<string> Names(string code, string pattern)
            => Regex.Matches(code, pattern).Select(match => match.Groups[1].Value).Distinct();

        /// <summary>Every place one test source opens a listener beyond
        /// loopback, or could.</summary>
        private static List<string> Violations(string file, string source)
        {
            var found = new List<string>();
            string[] lines = source.Replace("\r\n", "\n").Split('\n').Select(CodeOf).ToArray();
            string code = string.Join("\n", lines);
            void Flag(int line, string why) => found.Add($"{file}({line + 1}): {why}: {lines[line].Trim()}");
            void FlagEach(string pattern, Func<string, bool> allowed, string why)
            {
                for (int i = 0; i < lines.Length; i++)
                    if (Regex.IsMatch(lines[i], pattern) && !allowed(lines[i])) Flag(i, why);
            }
            bool Loopback(string line) => line.Contains("IPAddress.Loopback") || line.Contains("IPAddress.IPv6Loopback");

            // Every address, by name or parsed from its literal. A receive
            // call's placeholder endpoint and an assertion bind nothing.
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"IPAddress\.(Any|IPv6Any)\b|\bIP(Address|EndPoint)\.(Try)?Parse\s*\(\s*""(0\.0\.0\.0|::|\[::\])")) continue;
                bool placeholder = lines[i].Contains("ReceiveFrom") || (i > 0 && lines[i - 1].Contains("ReceiveFrom"));
                if (!placeholder && !lines[i].Contains("Assert.")) Flag(i, "names every address");
            }

            // Sockets and listeners the framework puts on every address
            // unless told otherwise.
            FlagEach(@"new\s+UdpClient\s*\(|\bUdpClient\s+\w+\s*=\s*new\s*\(", Loopback,
                "a UdpClient takes every address unless it is given a loopback endpoint");
            FlagEach(@"new\s+TcpListener\s*\(|\bTcpListener\s+\w+\s*=\s*new\s*\(", Loopback,
                "a TcpListener is given a loopback address");
            FlagEach(@"TcpListener\.Create\s*\(", _ => false, "TcpListener.Create listens on every address");
            FlagEach(@"\.Bind\s*\(\s*new\s+IPEndPoint\s*\(", Loopback, "a socket binds a loopback address");
            FlagEach(@"\.Prefixes\.Add\s*\(", line => Regex.IsMatch(line, @"127\.0\.0\.1|localhost|Loopback\("),
                "an HttpListener prefix names 127.0.0.1");

            // App listeners with no bind address to pass.
            FlagEach(@"new\s+(" + string.Join("|", EveryAddressOnly) + @")\s*\(", _ => false,
                "this class listens on every address and takes no bind address");

            // App listeners with one. A LinkServer starts on the address it
            // is passed, and its one-argument Start is the app's.
            foreach (string server in Names(code, @"\bLinkServer\s+(\w+)\b")
                .Concat(Names(code, @"\b(\w+)\s*=\s*new\s+LinkServer\s*\(")).Distinct())
                FlagEach(@"\b" + server + @"\s*\??\.\s*Start\s*\(", Loopback, "a LinkServer starts on IPAddress.Loopback");

            // A WebControllerServer built with no arguments opens the real
            // listener on every address and writes firewall rules.
            foreach (string server in Names(code, @"\b(\w+)\s*=\s*new\s+WebControllerServer\s*\(\s*\)"))
                FlagEach(@"\b" + server + @"\s*\??\.\s*Start\s*\(", _ => false,
                    "a WebControllerServer with no injected listener is never started");

            // A head tracker with UDP on binds every address unless it is
            // told loopback. The row built from the app's settings is told
            // nothing, so it is never opened.
            string[] opened = Names(code, @"\b(\w+)\s*=\s*(?:new\s+HeadTrackerDevice\s*\(|HeadTrackerDevice\.FromCurrentSettings\s*\()")
                .Where(tracker => Regex.IsMatch(code, @"\b" + tracker + @"\s*\??\.\s*Open\s*\(")).ToArray();
            if (opened.Length > 0)
            {
                foreach (Match built in Regex.Matches(code, @"new\s+HeadTrackerDevice\s*\("))
                {
                    string statement = StatementAt(code, built.Index);
                    if (Regex.IsMatch(statement, @"^new\s+HeadTrackerDevice\s*\(\s*false\s*,")) continue;
                    if (Regex.IsMatch(statement, @"UdpBindAddress\s*=\s*IPAddress\.Loopback")) continue;
                    Flag(code.Substring(0, built.Index).Count(c => c == '\n'),
                        "a head tracker in a file that opens one sets UdpBindAddress = IPAddress.Loopback");
                }
                foreach (string tracker in Names(code, @"\b(\w+)\s*=\s*HeadTrackerDevice\.FromCurrentSettings\s*\(").Intersect(opened))
                    FlagEach(@"\b" + tracker + @"\s*\??\.\s*Open\s*\(", _ => false,
                        "a head tracker built from the app's settings is never opened");
            }

            // The Game Telemetry row (#531) by the head tracker's rule: every
            // address unless told loopback, and the row built from the app's
            // settings is never opened.
            string[] rows = Names(code, @"\b(\w+)\s*=\s*(?:new\s+GameTelemetryDevice\s*\(|GameTelemetryDevice\.FromCurrentSettings\s*\()")
                .Where(row => Regex.IsMatch(code, @"\b" + row + @"\s*\??\.\s*Open\s*\(")).ToArray();
            if (rows.Length > 0)
            {
                foreach (Match built in Regex.Matches(code, @"new\s+GameTelemetryDevice\s*\("))
                {
                    if (Regex.IsMatch(StatementAt(code, built.Index), @"BindAddress\s*=\s*IPAddress\.Loopback")) continue;
                    Flag(code.Substring(0, built.Index).Count(c => c == '\n'),
                        "a game telemetry row in a file that opens one sets BindAddress = IPAddress.Loopback");
                }
                foreach (string row in Names(code, @"\b(\w+)\s*=\s*GameTelemetryDevice\.FromCurrentSettings\s*\(").Intersect(rows))
                    FlagEach(@"\b" + row + @"\s*\??\.\s*Open\s*\(", _ => false,
                        "a game telemetry row built from the app's settings is never opened");
            }
            return found;
        }

        [Fact]
        public void NoTestSourceOpensAListenerBeyondLoopback()
        {
            string tests = Path.Combine(RepoRoot(), "PadForge.Tests");
            // This file is left out: its controls below are the very lines
            // the rules reject. It opens no socket of its own.
            string[] files = Directory.GetFiles(tests, "*.cs").Where(file => Path.GetFileName(file) != "TestHostListenerTests.cs").ToArray();
            Assert.True(files.Length > 500, "the test sources were not found");
            var found = files.SelectMany(file => Violations(Path.GetFileName(file), File.ReadAllText(file))).ToList();
            Assert.True(found.Count == 0, string.Join("\n", found));

            // The scan read the fixtures it exists for.
            string link = File.ReadAllText(Path.Combine(tests, "LinkServerTests.cs"));
            Assert.Contains("s.Start(p, IPAddress.Loopback)", link);
            string head = File.ReadAllText(Path.Combine(tests, "HeadTrackingInputTests.cs"));
            Assert.True(Regex.Matches(head, @"UdpBindAddress = IPAddress\.Loopback").Count >= 3,
                "HeadTrackingInputTests no longer holds its three head trackers on loopback");
            string telemetry = File.ReadAllText(Path.Combine(tests, "GameTelemetryTests.cs"));
            Assert.Contains("BindAddress = IPAddress.Loopback,", telemetry);
        }

        /// <summary>Each rule rejects the line it is for, so a rule that
        /// stopped matching fails here and not in silence.</summary>
        [Theory]
        [InlineData("udp.Bind(new IPEndPoint(IPAddress.Any, 0));")]
        [InlineData("var everywhere = new IPEndPoint(IPAddress.IPv6Any, 0);")]
        [InlineData("var everywhere = IPAddress.Parse(\"0.0.0.0\");")]
        [InlineData("using var sender = new UdpClient();")]
        [InlineData("using var sender = new UdpClient(4242);")]
        [InlineData("private readonly UdpClient _client = new();")]
        [InlineData("var listener = new TcpListener(address, port);")]
        [InlineData("var listener = TcpListener.Create(port);")]
        [InlineData("socket.Bind(new IPEndPoint(address, port));")]
        [InlineData("listener.Prefixes.Add($\"http://+:{port}/\");")]
        [InlineData("using var discovery = new LinkDiscovery();")]
        [InlineData("var source = new OutGaugeTelemetrySource();")]
        [InlineData("using var server = new LinkServer(identity, trust, _ => true);\nserver.Start(port);")]
        [InlineData("private readonly LinkServer _server = new(identity, trust, _ => true);\nvoid Run() { _server.Start(27500); }")]
        [InlineData("using var server = new WebControllerServer();\nserver.Start(8080);")]
        [InlineData("using var tracker = new HeadTrackerDevice(true, 4242, false, 0, null);\ntracker.Open();")]
        [InlineData("using var tracker = new HeadTrackerDevice(4242, false, 0, () => { ticks++; return ticks; });\ntracker.Open();")]
        [InlineData("using var tracker = HeadTrackerDevice.FromCurrentSettings();\ntracker.Open();")]
        [InlineData("using var row = new GameTelemetryDevice(20777, reader, variables, 0, null);\nrow.Open();")]
        [InlineData("using var row = GameTelemetryDevice.FromCurrentSettings();\nrow.Open();")]
        public void ALineThatOpensAListenerBeyondLoopbackIsFound(string source)
            => Assert.NotEmpty(Violations("control", source));

        /// <summary>The loopback forms of the same lines pass, and so do the
        /// uses that bind nothing.</summary>
        [Theory]
        [InlineData("udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));")]
        [InlineData("var packet = await udp.ReceiveFromAsync(bytes, SocketFlags.None,\n    new IPEndPoint(IPAddress.Any, 0), stop);")]
        [InlineData("Assert.Equal(IPAddress.Any, row.UdpBindAddress);")]
        [InlineData("// The app binds IPAddress.Any here.\nint port = 0;")]
        [InlineData("using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));")]
        [InlineData("var listener = new TcpListener(IPAddress.Loopback, port);")]
        [InlineData("listener.Prefixes.Add($\"http://127.0.0.1:{port}/\");")]
        [InlineData("using var server = new LinkServer(identity, trust, _ => true);\nserver.Start(port, IPAddress.Loopback);")]
        [InlineData("using var server = new WebControllerServer();\nserver.Stop();")]
        [InlineData("using var tracker = new HeadTrackerDevice(udp, 0, false, 0, null,\n    () => { readers++; return reader; }, _ => { }) { UdpBindAddress = IPAddress.Loopback };\ntracker.Open();")]
        [InlineData("using var tracker = new HeadTrackerDevice(false, 4242, true, 0, null);\ntracker.Open();")]
        [InlineData("using var tracker = new HeadTrackerDevice(4242, false, 0, null);\ntracker.AttachForTest();")]
        [InlineData("using var row = HeadTrackerDevice.FromCurrentSettings();\nAssert.True(row.UdpEnabled);")]
        [InlineData("using var row = new GameTelemetryDevice(port, reader, variables, 0, null)\n{\n    BindAddress = IPAddress.Loopback,\n};\nrow.Open();")]
        [InlineData("using var row = GameTelemetryDevice.FromCurrentSettings();\nAssert.Equal(20777, row.Port);")]
        public void ALoopbackListenerAndALineThatBindsNothingPass(string source)
            => Assert.Empty(Violations("control", source));

        /// <summary>Every place the app opens a socket or a listener, by
        /// file. A new one fails here until it is entered below with the way
        /// a test starts it: on a bind address the test passes, or not at
        /// all, by a line in <see cref="EveryAddressOnly"/>.</summary>
        [Fact]
        public void EverySocketTheAppOpensIsAccountedFor()
        {
            string root = RepoRoot();
            var expected = new SortedDictionary<string, int>
            {
                // A bind address the tests pass. Every address in the app.
                ["PadForge.Engine/RemoteLink/LinkServer.cs"] = 3,
                ["PadForge.App/Common/Input/HeadTrackerDevice.cs"] = 2,
                // Its listening socket the same, its forward socket on
                // loopback everywhere.
                ["PadForge.App/Common/Input/Telemetry/GameTelemetryDevice.cs"] = 4,
                // Loopback in the app too.
                ["PadForge.App/Services/DsuMotionServer.cs"] = 2,
                ["PadForge.App/Services/GameModServer.cs"] = 2,
                // The web helper (#600 item 11): its HttpListener on a
                // 127.0.0.1 prefix, past NetworkListenerGate.EnterPrefix, and
                // the loopback probe that finds it a free port.
                ["PadForge.App/Services/Automation/Web/WebHelper.cs"] = 2,
                // No listener: the outbound socket that fetches a web
                // assistant's client document from a public address, past
                // OutboundNetworkGate.RefusesHost, which the test host closes.
                ["PadForge.App/Services/Automation/Web/WebClientDocuments.cs"] = 1,
                // Every address, and no test builds one.
                ["PadForge.Engine/RemoteLink/LinkDiscovery.cs"] = 2,
                ["PadForge.Engine/RemoteLink/Dht/UdpKrpcTransport.cs"] = 2,
                ["PadForge.App/Common/Telemetry/OutGaugeTelemetrySource.cs"] = 1,
                ["PadForge.App/Common/Telemetry/ForzaUdpTelemetrySource.cs"] = 1,
                ["PadForge.App/Common/Telemetry/CodemastersUdpTelemetrySource.cs"] = 1,
                ["PadForge.App/Services/WebControllerListener.cs"] = 1,
                // No listener: a UDP socket that is connected to read this
                // PC's own address and closed at once.
                ["PadForge.App/Services/WebControllerServer.cs"] = 1,
            };
            const string opens = @"\.Bind\s*\(\s*new\s+IPEndPoint\s*\(|new\s+(Socket|TcpListener|UdpClient|HttpListener)\s*\("
                + @"|\b(Socket|TcpListener|UdpClient|HttpListener)\s+\w+\s*=\s*new\s*\(|TcpListener\.Create\s*\(";
            var actual = new SortedDictionary<string, int>();
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
                foreach (string file in Directory.GetFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
                    int count = File.ReadAllLines(file).Sum(line => Regex.Matches(CodeOf(line), opens).Count);
                    if (count > 0) actual[relative] = count;
                }
            Assert.Equal(string.Join("\n", expected.Select(entry => entry.Key + " " + entry.Value)),
                string.Join("\n", actual.Select(entry => entry.Key + " " + entry.Value)));

            // The app's own addresses stand.
            string At(string relative) => File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains("public bool Start(int port) => Start(port, IPAddress.Any);", At("PadForge.Engine/RemoteLink/LinkServer.cs"));
            Assert.Contains("_tcp = new TcpListener(bindAddress, port);", At("PadForge.Engine/RemoteLink/LinkServer.cs"));
            Assert.Contains("_udp.Bind(new IPEndPoint(bindAddress, port));", At("PadForge.Engine/RemoteLink/LinkServer.cs"));
            Assert.Contains("internal IPAddress UdpBindAddress { get; init; } = IPAddress.Any;", At("PadForge.App/Common/Input/HeadTrackerDevice.cs"));
            Assert.Contains("s.Bind(new IPEndPoint(UdpBindAddress, _port));", At("PadForge.App/Common/Input/HeadTrackerDevice.cs"));
            string telemetry = At("PadForge.App/Common/Input/Telemetry/GameTelemetryDevice.cs");
            Assert.Contains("internal IPAddress BindAddress { get; init; } = IPAddress.Any;", telemetry);
            Assert.Contains("s.Bind(new IPEndPoint(BindAddress, _port));", telemetry);
            Assert.Contains("forward.Bind(new IPEndPoint(IPAddress.Loopback, 0));", telemetry);
            Assert.Contains("_socket.Bind(new IPEndPoint(IPAddress.Loopback, port));", At("PadForge.App/Services/DsuMotionServer.cs"));
            Assert.Contains("_socket.Bind(new IPEndPoint(IPAddress.Loopback, port));", At("PadForge.App/Services/GameModServer.cs"));
            string helper = At("PadForge.App/Services/Automation/Web/WebHelper.cs");
            Assert.Contains("string prefix = WebAddress.Loopback(port) + \"/\";", helper);
            Assert.Contains("var probe = new TcpListener(IPAddress.Loopback, 0);", helper);
            Assert.Contains("internal static string Loopback(int port) => \"http://127.0.0.1:\" + port.ToString(CultureInfo.InvariantCulture);",
                At("PadForge.App/Services/Automation/Web/WebGrant.cs"));
            Assert.Contains("if (!IsPublic(address)) continue;", At("PadForge.App/Services/Automation/Web/WebClientDocuments.cs"));
        }
    }

    /// <summary>The gate every listener of the app asks before it binds
    /// (NetworkListenerGate). The test host closes it (ListenerOffLimits in
    /// TestHostFences.cs), so a listener beyond loopback is refused here even
    /// in a build of the code with one of its checks removed. The rows quote
    /// the app's own lines, which the scan above rejects in a test, so these
    /// tests sit in the file that scan leaves out.</summary>
    public class NetworkListenerGateTests
    {
        private const string Gate = "PadForge.Engine/Common/NetworkListenerGate.cs";
        private const string Telemetry = "PadForge.App/Common/Telemetry/";
        private const string EnterAny = "PadForge.Engine.Common.NetworkListenerGate.Enter(IPAddress.Any);";

        [Fact]
        public void TheGate_IsClosedInTheTestHost() => Assert.True(NetworkListenerGate.OffLimitsForTest);

        public static IEnumerable<object[]> AskedFirst() => new[]
        {
            new object[] { Gate, "public static void Enter(IPAddress address)", "if (OffLimitsForTest && (address == null || !IPAddress.IsLoopback(address)))" },
            new object[] { Gate, "public static void EnterPrefix(string prefix)", "if (OffLimitsForTest && !IsLoopbackPrefix(prefix))" },
            new object[] { "PadForge.App/Services/WebControllerListener.cs", "public void Start(string prefix)", "PadForge.Engine.Common.NetworkListenerGate.EnterPrefix(prefix);" },
            new object[] { "PadForge.App/Services/WebControllerListener.cs", "public void AddPrefix(string prefix)", "PadForge.Engine.Common.NetworkListenerGate.EnterPrefix(prefix);" },
            new object[] { "PadForge.Engine/RemoteLink/Dht/UdpKrpcTransport.cs", "public UdpKrpcTransport()", EnterAny },
        };

        [Theory]
        [MemberData(nameof(AskedFirst))]
        public void AListener_AsksTheGateBeforeAnythingElse(string file, string signature, string guard)
            => FenceSource.AsksFirst(file, signature, guard);

        public static IEnumerable<object[]> AskedBeforeTheBind() => new[]
        {
            new object[] { "PadForge.App/Common/Input/HeadTrackerDevice.cs", "internal sealed class HeadTrackerDevice : ISdlInputDevice", "PadForge.Engine.Common.NetworkListenerGate.Enter(UdpBindAddress);", "new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)" },
            new object[] { "PadForge.App/Common/Input/Telemetry/GameTelemetryDevice.cs", "private void BindUdp()", "NetworkListenerGate.Enter(BindAddress);", "new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)" },
            new object[] { Telemetry + "CodemastersUdpTelemetrySource.cs", "public void Start()", EnterAny, "new UdpClient(" },
            new object[] { Telemetry + "ForzaUdpTelemetrySource.cs", "public void Start()", EnterAny, "new UdpClient(" },
            new object[] { Telemetry + "OutGaugeTelemetrySource.cs", "public void Start()", EnterAny, "new UdpClient(" },
            new object[] { "PadForge.Engine/RemoteLink/LinkDiscovery.cs", "public void Start(int linkPort, string displayName, string fingerprintHex)", EnterAny, "_socket = new Socket(" },
            new object[] { "PadForge.Engine/RemoteLink/LinkServer.cs", "internal bool Start(int port, IPAddress bindAddress)", "PadForge.Engine.Common.NetworkListenerGate.Enter(bindAddress);", "_tcp = new TcpListener(bindAddress, port);" },
        };

        /// <summary>A listener that does other work first asks the gate
        /// before it makes its socket. The census of every socket the app
        /// opens is EverySocketTheAppOpensIsAccountedFor above.</summary>
        [Theory]
        [MemberData(nameof(AskedBeforeTheBind))]
        public void AListener_AsksTheGateBeforeItBinds(string file, string signature, string guard, string bind)
            => FenceSource.AsksBefore(file, signature, guard, bind);

        public static IEnumerable<object[]> Addresses() => new[]
        {
            new object[] { IPAddress.Loopback, true },
            new object[] { IPAddress.IPv6Loopback, true },
            new object[] { IPAddress.Parse("127.0.0.2"), true },
            new object[] { IPAddress.Any, false },
            new object[] { IPAddress.IPv6Any, false },
            new object[] { IPAddress.Parse("192.168.1.20"), false },
            new object[] { null, false },
        };

        [Theory]
        [MemberData(nameof(Addresses))]
        public void TheGate_PassesLoopbackAlone(IPAddress address, bool passes)
        {
            if (passes) { NetworkListenerGate.Enter(address); return; }
            var refused = Assert.Throws<SocketException>(() => NetworkListenerGate.Enter(address));
            Assert.Equal(SocketError.AccessDenied, refused.SocketErrorCode);
        }

        [Theory]
        [InlineData("http://127.0.0.1:8080/", true)]
        [InlineData("http://localhost:8080/", true)]
        [InlineData("https://[::1]:8443/", true)]
        [InlineData("http://+:8080/", false)]
        [InlineData("http://*:8080/", false)]
        [InlineData("http://0.0.0.0:8080/", false)]
        [InlineData("http://192.168.1.20:8080/", false)]
        [InlineData("http://padforge.local:8080/", false)]
        [InlineData("no prefix", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void TheGate_PassesALoopbackPrefixAlone(string prefix, bool passes)
        {
            Assert.Equal(passes, NetworkListenerGate.IsLoopbackPrefix(prefix));
            if (passes) { NetworkListenerGate.EnterPrefix(prefix); return; }
            var refused = Assert.Throws<HttpListenerException>(() => NetworkListenerGate.EnterPrefix(prefix));
            Assert.Equal(5, refused.ErrorCode);
        }
    }
}
