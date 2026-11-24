using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The application's sources, read as text, for the guards
    /// that no harmless call can show.</summary>
    internal static class OffLimitsSource
    {
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        internal static string Read(string relative)
            => File.ReadAllText(Path.Combine(RepoRoot(), relative)).Replace("\r\n", "\n");

        /// <summary>Every C# source of a project, generated and built files
        /// left out.</summary>
        internal static IEnumerable<string> Sources(string project)
        {
            string root = Path.Combine(RepoRoot(), project);
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string rel = Path.GetRelativePath(root, f);
                    return !rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>Where each call of <paramref name="call"/> begins. A
        /// declaration of the same name (an extern, a method's own
        /// signature) is not a call.</summary>
        internal static List<int> Calls(string source, string call)
        {
            var at = new List<int>();
            for (int i = source.IndexOf(call, StringComparison.Ordinal); i >= 0; i = source.IndexOf(call, i + 1, StringComparison.Ordinal))
            {
                int lineStart = source.LastIndexOf('\n', i) + 1;
                string before = source[lineStart..i];
                if (before.Contains("extern ") || before.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    || before.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                at.Add(i);
            }
            return at;
        }

        /// <summary>The text of the statement a call sits in, up to the
        /// call: from the end of the statement or block before it.</summary>
        internal static string StatementUpTo(string source, int index)
        {
            int start = Math.Max(source.LastIndexOf(';', index), Math.Max(source.LastIndexOf('{', index), source.LastIndexOf('}', index)));
            return source[(start + 1)..index];
        }

        /// <summary>The body of the member whose signature is
        /// <paramref name="signature"/>, up to the line that closes it at
        /// the member's own indent, or for an expression body up to its
        /// semicolon.</summary>
        internal static string Member(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + signature);
            Assert.True(source.IndexOf(signature, at + 1, StringComparison.Ordinal) < 0, "found twice: " + signature);
            int lineStart = source.LastIndexOf('\n', at) + 1;
            string indent = source[lineStart..at];
            Assert.True(string.IsNullOrWhiteSpace(indent), "not at a line's start: " + signature);
            int afterSignature = source.IndexOf('\n', at);
            int brace = source.IndexOf("\n" + indent + "{\n", at, StringComparison.Ordinal);
            if (brace >= 0 && brace <= afterSignature)
            {
                int close = source.IndexOf("\n" + indent + "}\n", brace, StringComparison.Ordinal);
                Assert.True(close > brace, "never closed: " + signature);
                return source[at..close];
            }
            int end = source.IndexOf(";\n", at, StringComparison.Ordinal);
            Assert.True(end > at, "never ended: " + signature);
            return source[at..end];
        }

        /// <summary>A member's body begins with <paramref name="guard"/>,
        /// with nothing before it but comments.</summary>
        internal static void BeginsWith(string member, string guard)
        {
            int open = member.IndexOf('{');
            Assert.True(open >= 0, "no body: " + member[..Math.Min(80, member.Length)]);
            var lines = member[(open + 1)..].Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
            Assert.StartsWith(guard, lines.First(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The test process changes nothing on the PC it runs on. Every call in
    /// the MIDI code and in <see cref="DriverInstaller"/> that reaches the
    /// machine and can change it, or be seen by another program, refuses in
    /// the test host when no fake or seam stands in for it: a real MIDI
    /// backend, a session, a virtual device, a WinMM port, the MIDI service's
    /// restart, the endpoint janitor's sweep, a process start, a download, a
    /// registry write, a folder delete outside the temp folder. Reads are
    /// not on the list.
    ///
    /// <para>Two sides are pinned. Each real call refuses here, shown by
    /// calling it where a call that got through would do no harm (a program
    /// that does not exist, a port number no PC has, a key nothing made) and
    /// by reading the source where no such call exists. And nothing in the
    /// application sets a flag, so production runs exactly as before.</para>
    ///
    /// <para>The source is read before a call is made, so a guard that was
    /// removed fails its test on the read, and the call it guarded is never
    /// reached.</para>
    /// </summary>
    public class MachineOffLimitsTests
    {
        private const string Recovery = "PadForge.App/Common/Input/MidiServiceRecovery.cs";
        private const string Controller = "PadForge.App/Common/Input/MidiVirtualController.cs";
        private const string InBox = "PadForge.App/Common/Input/MidiBackendInBox.cs";
        private const string AppSdk = "PadForge.App/Common/Input/MidiBackendAppSdk.cs";
        private const string Legacy = "PadForge.App/Common/Input/MidiBackendLegacy.cs";
        private const string Janitor = "PadForge.App/Common/Input/MidiEndpointJanitor.cs";
        private const string Installer = "PadForge.App/Common/DriverInstaller.cs";

        /// <summary>The flags, each with the file that declares it and the
        /// class it belongs to. Other areas keep flags of their own, some by
        /// the same field name, so each is named with its class.</summary>
        private static readonly (string File, string Class, string Flag)[] Flags =
        {
            (Recovery, "MidiServiceRecovery", "SeamsOnlyForTest"),
            (Controller, "MidiVirtualController", "RealBackendsOffLimitsForTest"),
            (InBox, "MidiBackendInBox", "OffLimitsForTest"),
            (AppSdk, "MidiBackendAppSdk", "OffLimitsForTest"),
            (Legacy, "WinMmMidi", "OffLimitsForTest"),
            (Janitor, "MidiEndpointJanitor", "OffLimitsForTest"),
            (Installer, "DriverInstaller", "OffLimitsForTest"),
        };

        private const string Assigns = @"\s*(=(?!=)|\|=|&=|\^=)";

        /// <summary>A write of one of the flags through its class's
        /// name.</summary>
        private static readonly Regex SetsAFlagByName = new(
            @"\b(" + string.Join("|", Flags.Select(f => f.Class + @"\." + f.Flag)) + @")" + Assigns, RegexOptions.Compiled);

        // ── The flags ──

        [Fact]
        public void EveryFlag_IsSetInTheTestHost()
        {
            Assert.True(MidiServiceRecovery.SeamsOnlyForTest);
            Assert.True(MidiVirtualController.RealBackendsOffLimitsForTest);
            Assert.True(MidiBackendInBox.OffLimitsForTest);
            Assert.True(MidiBackendAppSdk.OffLimitsForTest);
            Assert.True(WinMmMidi.OffLimitsForTest);
            Assert.True(MidiEndpointJanitor.OffLimitsForTest);
            Assert.True(DriverInstaller.OffLimitsForTest);
        }

        /// <summary>Each flag is a plain field that starts false, and no
        /// line of the application or the engine writes one, in its own
        /// class or through its class's name, so production never refuses a
        /// call.</summary>
        [Fact]
        public void NothingInTheApplication_SetsAFlag()
        {
            var writes = new List<string>();
            foreach (var (file, _, flag) in Flags)
            {
                string source = OffLimitsSource.Read(file);
                Assert.Single(Regex.Matches(source, @"^\s*internal static bool " + flag + @";$", RegexOptions.Multiline));
                foreach (Match m in Regex.Matches(source, @"\b" + flag + Assigns))
                    writes.Add(Path.GetFileName(file) + ": " + m.Value);
            }

            int read = 0;
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
            {
                foreach (string path in OffLimitsSource.Sources(project))
                {
                    read++;
                    foreach (Match m in SetsAFlagByName.Matches(File.ReadAllText(path)))
                        writes.Add(Path.GetFileName(path) + ": " + m.Value);
                }
            }
            Assert.True(read > 200, "the application's sources were not found");
            Assert.Empty(writes);
        }

        /// <summary>In the tests, only the two module initializers write a
        /// flag, and only to set it, so no test lifts one for a while.</summary>
        [Fact]
        public void OnlyTheModuleInitializers_SetAFlag_AndNoTestClearsOne()
        {
            var writes = new List<string>();
            foreach (string path in OffLimitsSource.Sources("PadForge.Tests"))
            {
                string name = Path.GetFileName(path);
                if (name == "MachineOffLimitsTests.cs") continue;
                foreach (string line in File.ReadAllLines(path))
                    if (SetsAFlagByName.IsMatch(line)) writes.Add(name + ": " + line.Trim());
            }
            Assert.Equal(Flags.Length, writes.Count);
            Assert.All(writes, w => Assert.EndsWith("= true;", w, StringComparison.Ordinal));
            Assert.All(writes, w => Assert.True(
                w.StartsWith("MidiServiceOffLimits.cs: ", StringComparison.Ordinal)
                || w.StartsWith("DriverInstallerOffLimits.cs: ", StringComparison.Ordinal), w));
        }

        // ── The MIDI service's restart ──

        /// <summary>The restart asks twice: where a seam is picked, and
        /// again as the restart itself begins, below which the stop, the
        /// kill and the start all sit.</summary>
        [Fact]
        public void TheServiceRestart_IsGuardedWhereASeamIsPicked_AndAgainAsItBegins()
        {
            string source = OffLimitsSource.Read(Recovery);
            Assert.Contains(": SeamsOnlyForTest ? MidiServiceRestart.Unchanged\n", source);
            string recover = OffLimitsSource.Member(source, "private static MidiServiceRestart Recover(string trigger)");
            OffLimitsSource.BeginsWith(recover, "if (SeamsOnlyForTest) return MidiServiceRestart.Unchanged;");
            int bodyStart = source.IndexOf(recover, StringComparison.Ordinal);
            foreach (string call in new[] { "ControlService(", "StartServiceW(", ".Kill(" })
            {
                int at = Assert.Single(OffLimitsSource.Calls(source, call));
                Assert.InRange(at, bodyStart, bodyStart + recover.Length);
            }
            Assert.Single(OffLimitsSource.Calls(source, "Recover(trigger)"));
        }

        // ── The backends ──

        [Fact]
        public void TheProductionFactories_MakeNoBackend()
        {
            string source = OffLimitsSource.Read(Controller);
            OffLimitsSource.BeginsWith(OffLimitsSource.Member(source, "private static IMidiBackend CreateBackend()"),
                "if (RealBackendsOffLimitsForTest) return null;");
            Assert.Contains("private static IMidiBackend CreateLegacyBackend()\n            => RealBackendsOffLimitsForTest ? null : new MidiBackendLegacy();\n", source);
            foreach (string made in new[] { "new MidiBackendInBox()", "new MidiBackendAppSdk()", "new MidiBackendLegacy()" })
                Assert.Single(OffLimitsSource.Calls(source, made));

            EveryFlag_IsSetInTheTestHost();
            foreach (string factory in new[] { "CreateBackend", "CreateLegacyBackend" })
            {
                var method = typeof(MidiVirtualController).GetMethod(factory, BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);
                Assert.Null(method.Invoke(null, null));
            }
        }

        public static IEnumerable<object[]> WindowsMidiServicesBackends() => new[]
        {
            new object[] { InBox, (int)MidiApiKind.InBox },
            new object[] { AppSdk, (int)MidiApiKind.AppSdk },
        };

        /// <summary>A Windows MIDI Services backend made with no fake in
        /// its place starts nothing, opens no session, lists nothing and
        /// creates no virtual device. Every call that reaches the service
        /// sits in one of the four members read here.</summary>
        [Theory]
        [MemberData(nameof(WindowsMidiServicesBackends))]
        public void ARealWindowsMidiServicesBackend_ReachesNoService(string file, int kind)
        {
            string source = OffLimitsSource.Read(file);
            string start = OffLimitsSource.Member(source, "public bool Start()");
            if (kind == (int)MidiApiKind.InBox)
                Assert.EndsWith("=> !OffLimitsForTest && Midi2.MidiApi.EnsureServiceAvailable()", start, StringComparison.Ordinal);
            else
                OffLimitsSource.BeginsWith(start, "if (OffLimitsForTest) return false;");
            string create = OffLimitsSource.Member(source, "public IMidiVirtualEndpoint CreateVirtualEndpoint(string deviceName, string uniqueId, int padIndex)");
            OffLimitsSource.BeginsWith(create, "if (OffLimitsForTest) throw new InvalidOperationException(\"The test host creates no virtual MIDI device.\");");
            string session = OffLimitsSource.Member(source, "public IMidiInputSession CreateInputSession(string name)");
            OffLimitsSource.BeginsWith(session, "if (OffLimitsForTest) return null;");
            string list = OffLimitsSource.Member(source, "public List<(string Id, string Name)> EnumerateNormalEndpoints()");
            Assert.Contains("var result = new List<(string Id, string Name)>();\n            if (OffLimitsForTest) return result;\n", list);

            // What reaches the service is called from those four and nowhere
            // else. A session, a device or a connection exists only once one
            // of them has run.
            foreach (string call in new[] { "EnsureServiceAvailable()", "MidiSession.Create(", "CreateVirtualDevice(", ".FindAll()" })
            {
                foreach (int at in OffLimitsSource.Calls(source, call))
                    Assert.Contains(new[] { start, create, session, list },
                        member => InMember(source, member, at));
                Assert.NotEmpty(OffLimitsSource.Calls(source, call));
            }

            EveryFlag_IsSetInTheTestHost();
            IMidiBackend backend = kind == (int)MidiApiKind.InBox ? new MidiBackendInBox() : new MidiBackendAppSdk();
            Assert.False(backend.Start());
            Assert.Null(backend.CreateInputSession("PadForge off-limits check"));
            Assert.Empty(backend.EnumerateNormalEndpoints());
            var refused = Assert.Throws<InvalidOperationException>(
                () => backend.CreateVirtualEndpoint("PadForge off-limits check", "PADFORGE_OFF_LIMITS_CHECK", 0));
            Assert.Equal("The test host creates no virtual MIDI device.", refused.Message);
            backend.Stop(skipDispose: false);
        }

        private static bool InMember(string source, string member, int at)
        {
            int from = source.IndexOf(member, StringComparison.Ordinal);
            return at >= from && at < from + member.Length;
        }

        /// <summary>The numbers below name no port on any PC, so a call that
        /// got through would open and write nothing. WinMM answers a bad port
        /// number with MMSYSERR_BADDEVICEID (2) and a null handle with
        /// MMSYSERR_INVALHANDLE (5). The test host is answered
        /// MMSYSERR_NODRIVER (6) before winmm.dll is called.</summary>
        [Fact]
        public void RealWinMm_OpensAndWritesNoPort()
        {
            string source = OffLimitsSource.Read(Legacy);
            string[] functions =
            {
                "midiInGetNumDevs(", "midiOutGetNumDevs(", "midiInGetDevCapsW(", "midiOutGetDevCapsW(", "midiOutOpen(",
                "midiOutShortMsg(", "midiOutClose(", "midiInOpen(", "midiInStart(", "midiInStop(", "midiInReset(", "midiInClose(",
            };
            Assert.Equal(functions.Length, Regex.Matches(source, @"\[DllImport\(""winmm\.dll""").Count);
            foreach (string function in functions)
            {
                int at = Assert.Single(OffLimitsSource.Calls(source, function));
                Assert.Contains("OffLimitsForTest", OffLimitsSource.StatementUpTo(source, at));
            }

            EveryFlag_IsSetInTheTestHost();
            const int noSuchPort = 0x7FFFFFF0;
            var winMm = WinMmMidi.Instance;
            Assert.Equal(0, winMm.InputCount());
            Assert.Equal(0, winMm.OutputCount());
            Assert.Null(winMm.InputName(noSuchPort));
            Assert.Null(winMm.OutputName(noSuchPort));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.OpenOutput(noSuchPort, out IntPtr output));
            Assert.Equal(IntPtr.Zero, output);
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.OpenInput(noSuchPort, (_, _, _, _, _) => { }, out IntPtr input));
            Assert.Equal(IntPtr.Zero, input);
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.SendShort(IntPtr.Zero, 0));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.CloseOutput(IntPtr.Zero));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.StartInput(IntPtr.Zero));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.StopInput(IntPtr.Zero));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.ResetInput(IntPtr.Zero));
            Assert.Equal(WinMmMidi.MMSYSERR_NODRIVER, winMm.CloseInput(IntPtr.Zero));

            // The legacy backend over the real WinMM lists no port and
            // opens none.
            var backend = new MidiBackendLegacy();
            Assert.Empty(backend.EnumerateOutputPorts());
            Assert.Empty(backend.EnumerateNormalEndpoints());
            Assert.Throws<MidiPortNotConnectedException>(() => backend.OpenOutputPort("PadForge off-limits check"));
        }

        /// <summary>The sweep removes device nodes, and from a process that
        /// knows none of a running PadForge's endpoints it would remove them
        /// all. It is read, never run.</summary>
        [Fact]
        public void TheEndpointJanitor_SchedulesAndRunsNoSweep()
        {
            string source = OffLimitsSource.Read(Janitor);
            OffLimitsSource.BeginsWith(OffLimitsSource.Member(source, "public static void ScheduleSweep(int delayMs)"),
                "if (SweepDisabledForTest || OffLimitsForTest) return;");
            string sweep = OffLimitsSource.Member(source, "internal static int Sweep()");
            OffLimitsSource.BeginsWith(sweep, "int removed = 0;");
            Assert.Contains("int removed = 0;\n            if (SweepDisabledForTest || OffLimitsForTest) return removed;\n", sweep);
            foreach (string call in new[] { "CM_Query_And_Remove_SubTreeW(", "CM_Uninstall_DevNode(" })
            {
                int at = Assert.Single(OffLimitsSource.Calls(source, call));
                Assert.True(InMember(source, sweep, at), call);
            }
        }

        // ── DriverInstaller ──

        /// <summary>Every process the class starts (msiexec, a runtime's
        /// uninstaller, the Visual C++ Redistributable, a bundle's extractor,
        /// pnputil, steamcmd, a cleanup script) starts through one member,
        /// and in the test host that member starts none. The program named
        /// here does not exist, so a start that got through would fail as a
        /// file not found (2), not as access denied (5).</summary>
        [Fact]
        public void DriverInstaller_StartsNoProcess()
        {
            string source = OffLimitsSource.Read(Installer);
            string start = OffLimitsSource.Member(source, "internal static Process StartProcess(ProcessStartInfo psi)");
            OffLimitsSource.BeginsWith(start, "if (OffLimitsForTest)");
            int only = Assert.Single(OffLimitsSource.Calls(source, "Process.Start("));
            Assert.True(InMember(source, start, only));
            Assert.Empty(OffLimitsSource.Calls(source, "new Process("));
            Assert.Equal(6, OffLimitsSource.Calls(source, "StartProcess(psi)").Count);

            EveryFlag_IsSetInTheTestHost();
            var refused = Assert.Throws<System.ComponentModel.Win32Exception>(() => DriverInstaller.StartProcess(
                new ProcessStartInfo("PadForge-off-limits-check-no-such-program.exe") { UseShellExecute = false }));
            Assert.Equal(5, refused.NativeErrorCode);
            Assert.StartsWith("The test host starts no process", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>Every download (the MIDI runtime with the Visual C++
        /// Redistributable, steamcmd, and the S.T.R.I.K.E. 7 SDK and Visual C++
        /// 2008 runtime of #519) fails before a request is made. The address
        /// here is no web address, and no client is handed over, so a download
        /// that got through could reach nothing.</summary>
        [Fact]
        public void DriverInstaller_DownloadsNothing()
        {
            string source = OffLimitsSource.Read(Installer);
            string download = OffLimitsSource.Member(source, "internal static void Download(HttpClient http, string url, string path)");
            OffLimitsSource.BeginsWith(download, "ThrowIfDownloadOffLimits(url);");
            int send = Assert.Single(OffLimitsSource.Calls(source, "http.Send("));
            Assert.True(InMember(source, download, send));
            int get = Assert.Single(OffLimitsSource.Calls(source, "http.GetAsync("));
            Assert.EndsWith("ThrowIfDownloadOffLimits(SteamCmdZipUrl);\n                    using var response = await ",
                source[..get], StringComparison.Ordinal);
            foreach (string other in new[] { "GetStreamAsync(", "GetByteArrayAsync(", "GetStringAsync(", "PostAsync(", "SendAsync(", "WebClient" })
                Assert.Empty(OffLimitsSource.Calls(source, other));
            string guard = OffLimitsSource.Member(source, "private static void ThrowIfDownloadOffLimits(string url)");
            OffLimitsSource.BeginsWith(guard, "if (OffLimitsForTest)");

            EveryFlag_IsSetInTheTestHost();
            string target = Path.Combine(Path.GetTempPath(), "PadForge-off-limits-check-" + Guid.NewGuid().ToString("N"));
            var refused = Assert.Throws<HttpRequestException>(
                () => DriverInstaller.Download(null, "padforge-off-limits-check://nowhere/file", target));
            Assert.StartsWith("The test host downloads nothing", refused.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(target));
        }

        /// <summary>Every registry write in the class is a delete or the
        /// SteamVR path record, each through a member that refuses. The key
        /// named here was never made, so a delete that got through would
        /// find nothing and return.</summary>
        [Fact]
        public void DriverInstaller_WritesNothingToTheRegistry()
        {
            string source = OffLimitsSource.Read(Installer);
            string tree = OffLimitsSource.Member(source, "internal static void DeleteRegistryTree(RegistryKey root, string path)");
            OffLimitsSource.BeginsWith(tree, "if (OffLimitsForTest)");
            Assert.True(InMember(source, tree, Assert.Single(OffLimitsSource.Calls(source, "DeleteSubKeyTree("))));
            string set = OffLimitsSource.Member(source, "private static void SetSteamVrPathHint(string dir)");
            OffLimitsSource.BeginsWith(set, "if (OffLimitsForTest)");
            Assert.True(InMember(source, set, Assert.Single(OffLimitsSource.Calls(source, "HMVR.SetSteamVRPathHint("))));
            string clear = OffLimitsSource.Member(source, "private static void ClearSteamVrPathHint()");
            OffLimitsSource.BeginsWith(clear, "if (OffLimitsForTest)");
            Assert.True(InMember(source, clear, Assert.Single(OffLimitsSource.Calls(source, ".DeleteValue("))));
            Assert.True(InMember(source, clear, Assert.Single(OffLimitsSource.Calls(source, "writable: true"))));
            foreach (string other in new[] { ".SetValue(", "CreateSubKey(", ".DeleteSubKey(", "OpenSubKey(HmRegKeyPath, true)" })
                Assert.Empty(OffLimitsSource.Calls(source, other));

            EveryFlag_IsSetInTheTestHost();
            string key = @"Software\PadForge-off-limits-check-" + Guid.NewGuid().ToString("N");
            var refused = Assert.Throws<UnauthorizedAccessException>(() => DriverInstaller.DeleteRegistryTree(Registry.CurrentUser, key));
            Assert.StartsWith("The test host writes nothing to the registry", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>The one folder delete in the class takes a folder
        /// inside the temp folder, where the installs stage their files, and
        /// refuses any other, as the SteamVR folder is. The folder named
        /// first does not exist, so a delete that got through would fail as
        /// a folder not found. The second is this test's own.</summary>
        [Fact]
        public void DriverInstaller_DeletesNoFolderOutsideTheTempFolder()
        {
            string source = OffLimitsSource.Read(Installer);
            string delete = OffLimitsSource.Member(source, "internal static void DeleteFolder(string path)");
            OffLimitsSource.BeginsWith(delete, "if (OffLimitsForTest && !IsInTempFolder(path))");
            Assert.True(InMember(source, delete, Assert.Single(OffLimitsSource.Calls(source, "Directory.Delete("))));
            // The vJoy script is written to a staging folder of its own under
            // the protected folder (#597), and goes with that folder.
            Assert.Empty(OffLimitsSource.Calls(source, "File.Delete("));
            Assert.Contains("string scriptFolder = StagingFolder(\"vJoy\");", source);
            Assert.Contains("try { DeleteFolder(scriptFolder); } catch { }", source);
            Assert.DoesNotContain("Path.Combine(Path.GetTempPath(), \"PadForge_vjoy_uninstall.cmd\")", source);

            EveryFlag_IsSetInTheTestHost();
            Assert.True(DriverInstaller.IsInTempFolder(Path.Combine(Path.GetTempPath(), "PadForge_MidiRuntime", "x")));
            Assert.False(DriverInstaller.IsInTempFolder(Path.GetTempPath()));
            Assert.False(DriverInstaller.IsInTempFolder(Path.GetTempPath().TrimEnd('\\', '/') + "-beside"));
            Assert.False(DriverInstaller.IsInTempFolder(@"C:\SteamVR"));
            Assert.False(DriverInstaller.IsInTempFolder(Path.Combine(Path.GetTempPath(), "..", "PadForge-off-limits-check")));
            Assert.False(DriverInstaller.IsInTempFolder(null));

            string nowhere = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "PadForge-off-limits-check-" + Guid.NewGuid().ToString("N"));
            var refused = Assert.Throws<UnauthorizedAccessException>(() => DriverInstaller.DeleteFolder(nowhere));
            Assert.StartsWith("The test host deletes no folder outside the temp folder", refused.Message, StringComparison.Ordinal);

            // Where the tests themselves were built inside the temp folder,
            // their own folder is no folder outside it.
            string own = Path.Combine(AppContext.BaseDirectory, "off-limits-check-" + Guid.NewGuid().ToString("N"));
            if (!DriverInstaller.IsInTempFolder(own))
            {
                Directory.CreateDirectory(own);
                try
                {
                    DriverInstaller.CleanupTempDir(own);
                    Assert.True(Directory.Exists(own));
                }
                finally { Directory.Delete(own); }
            }

            // A staging folder in the temp folder still goes.
            string staging = Path.Combine(Path.GetTempPath(), "PadForge-off-limits-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(staging, "Extracted"));
            DriverInstaller.CleanupTempDir(staging);
            Assert.False(Directory.Exists(staging));
        }

        /// <summary>The SteamVR path registry and the two folders beside it
        /// are written only past the guard. Read, not run: what a run finds
        /// depends on the SteamVR this PC has.</summary>
        [Fact]
        public void DriverInstaller_RewritesNoSteamVrPathRegistry()
        {
            string source = OffLimitsSource.Read(Installer);
            string contain = OffLimitsSource.Member(source, "public static bool ContainOwnedSteamVrDataPaths()");
            int guard = contain.IndexOf("if (OffLimitsForTest) return false;", StringComparison.Ordinal);
            Assert.True(guard > 0);
            foreach (string write in new[] { "Directory.CreateDirectory(wantConfig)", "Directory.CreateDirectory(wantLog)", "File.WriteAllBytes(" })
            {
                int at = contain.IndexOf(write, StringComparison.Ordinal);
                Assert.True(at > guard, write);
            }
            Assert.Single(OffLimitsSource.Calls(source, "File.WriteAllBytes("));
            Assert.Empty(OffLimitsSource.Calls(source, "File.WriteAllText("));
        }
    }

    /// <summary>The restart's two guards, run. These touch the restart's
    /// cooldown, so they ride the MIDI collection.</summary>
    [Collection("MidiBackendStatics")]
    public class MidiServiceOffLimitsTests : IDisposable
    {
        public MidiServiceOffLimitsTests()
        {
            MidiServiceRecovery.RecoverSeam = null;
            MidiServiceRecovery.RestartSeam = null;
            MidiServiceRecovery.ResetCooldownForTest();
        }

        public void Dispose() => MidiServiceRecovery.ResetCooldownForTest();

        /// <summary>The lines the restart wrote since <paramref name="mark"/>.
        /// Its first act past the guard is a line saying it restarts
        /// midisrv, so no such line means it never began.</summary>
        private static List<string> RestartLinesAfter(string mark)
        {
            var lines = SdlDiagLog.Snapshot().Split('\n').Select(l => l.TrimEnd()).ToList();
            int at = lines.FindIndex(l => l.EndsWith(mark, StringComparison.Ordinal));
            Assert.True(at >= 0, "the mark rolled out of the diagnostics ring");
            return lines.Skip(at + 1).Where(l => l.Contains(": midisrv restart")).ToList();
        }

        [Fact]
        public void ARestartNoSeamStandsInFor_NeverBegins()
        {
            new MachineOffLimitsTests().TheServiceRestart_IsGuardedWhereASeamIsPicked_AndAgainAsItBegins();
            Assert.True(MidiServiceRecovery.SeamsOnlyForTest);
            string mark = "MidiServiceOffLimitsTests mark " + Guid.NewGuid().ToString("N");
            SdlDiagLog.WriteLine(mark);

            Assert.Equal(MidiServiceRestart.Unchanged, MidiServiceRecovery.TryRestart("an off-limits check"));
            Assert.Empty(RestartLinesAfter(mark));
        }

        /// <summary>The restart itself, reached past the lines that pick a
        /// seam, as a change to those lines would reach it.</summary>
        [Fact]
        public void TheRestartItself_ReturnsBeforeItTouchesTheService()
        {
            new MachineOffLimitsTests().TheServiceRestart_IsGuardedWhereASeamIsPicked_AndAgainAsItBegins();
            Assert.True(MidiServiceRecovery.SeamsOnlyForTest);
            var recover = typeof(MidiServiceRecovery).GetMethod("Recover", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(recover);
            string mark = "MidiServiceOffLimitsTests mark " + Guid.NewGuid().ToString("N");
            SdlDiagLog.WriteLine(mark);

            Assert.Equal(MidiServiceRestart.Unchanged, recover.Invoke(null, new object[] { "an off-limits check" }));
            Assert.Empty(RestartLinesAfter(mark));
        }
    }
}
