using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every process PadForge starts with its rights gets the variables the
    /// allow list builds, never a copy of PadForge's own (#597). Each start
    /// site is checked: the block it passes holds no CORECLR_*, COR_*,
    /// DOTNET_* (but the unpack folder) or COMPlus_* variable, nor
    /// SDL3_DYNAMIC_API, even when this process has them.
    /// </summary>
    public class RightsVariablesTests
    {
        private const string Natives = @"C:\ProgramData\PadForge\Natives";

        /// <summary>The unpack folder a start here gets: none, since the test
        /// host's fence keeps ProtectedFolder's root empty
        /// (ProtectedFolder.OffLimitsForTest).</summary>
        private static string NativesHere => string.IsNullOrEmpty(ProtectedFolder.Natives) ? null : ProtectedFolder.Natives;

        /// <summary>The variables a program of the user can put in front of
        /// a start, each of a family that loads code into a .NET or SDL
        /// process. Only handed to the builders as lines: set in this test
        /// host they would reach the processes other tests start.</summary>
        private static readonly string[] Loaders =
        {
            @"CORECLR_ENABLE_PROFILING=1", @"CORECLR_PROFILER_PATH=C:\Users\someone\p.dll", @"COR_ENABLE_PROFILING=1",
            @"COR_PROFILER_PATH=C:\Users\someone\p.dll", @"DOTNET_STARTUP_HOOKS=C:\Users\someone\h.dll",
            @"COMPlus_EnableDiagnostics=1", @"dotnet_bundle_extract_base_dir=C:\Users\someone\planted",
            @"SDL3_DYNAMIC_API=C:\Users\someone\sdl.dll", @"PADFORGE_HANDOVER_EVENT=dead",
        };

        /// <summary>Variables of the same families that load nothing, set in
        /// this test host for the sites that read its variables
        /// themselves.</summary>
        private static readonly string[] Canaries =
            { "CORECLR_PADFORGE_CANARY", "COR_PADFORGE_CANARY", "DOTNET_PADFORGE_CANARY", "COMPlus_PadForgeCanary" };

        private static string[] OwnWithLoaders() => System32Calls.Variables().Concat(Loaders).ToArray();

        private static Dictionary<string, string> Map(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                string name = ProtectedStart.NameOf(line);
                Assert.False(map.ContainsKey(name), "twice: " + name);
                map[name] = line.Substring(name.Length + 1);
            }
            return map;
        }

        private static Dictionary<string, string> MapOfBlock(string block)
        {
            Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
            return Map(block[..^2].Split('\0'));
        }

        /// <summary>No variable that loads code, the unpack folder only as
        /// <paramref name="natives"/>, and every name one of the
        /// lists'.</summary>
        private static void HoldsNoLoader(IReadOnlyDictionary<string, string> map, string natives, params string[] alsoKept)
        {
            foreach (string name in map.Keys)
            {
                Assert.False(name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(name, ProtectedStart.BaseVariable, StringComparison.Ordinal), name);
                Assert.False(name.StartsWith("SDL", StringComparison.OrdinalIgnoreCase), name);
                Assert.True(RightsVariables.IsKept(name) || alsoKept.Contains(name), name);
            }
            if (natives == null) Assert.DoesNotContain(ProtectedStart.BaseVariable, map.Keys);
            else Assert.Equal(natives, map[ProtectedStart.BaseVariable]);
            // The machine's own folder names reach the start.
            Assert.True(map.ContainsKey("SystemRoot"));
            Assert.True(map.ContainsKey("Path"));
        }

        /// <summary>The machine's block, read through System32's userenv.dll,
        /// holds the system's variables, and the user's own Path entries
        /// are not in it.</summary>
        [Fact]
        public void TheMachinesBlock_IsReadThroughSystem32()
        {
            var machine = Map(System32Calls.MachineVariables());
            Assert.Equal(System32Calls.WindowsFolder, machine["SystemRoot"], ignoreCase: true);
            Assert.Contains("system32", machine["Path"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Environment.GetEnvironmentVariable("ComSpec"), machine["ComSpec"], ignoreCase: true);
        }

        // ── The protected start's restart ──

        [Fact]
        public void TheRestart_HandsOnNoLoader_AndTheHandover()
        {
            // The same-window control: the loaders are in what the start reads.
            var own = OwnWithLoaders();
            Assert.Contains(own, l => l.StartsWith("CORECLR_ENABLE_PROFILING=", StringComparison.Ordinal));
            var map = MapOfBlock(ProtectedStart.CopyBlock(System32Calls.MachineVariables(), own, Natives, "1f4"));
            HoldsNoLoader(map, Natives, ProtectedStart.HandoverVariable);
            Assert.Equal("1f4", map[ProtectedStart.HandoverVariable]);
            Assert.Equal(Environment.UserName, map["USERNAME"], ignoreCase: true);

            string source = OffLimitsSource.Read("PadForge.App/Services/ProtectedStart.cs");
            string start = FenceSource.Body(source, "private static int StartCopy(string natives, out bool started)");
            Assert.Contains("string block = CopyBlock(System32Calls.MachineVariables(), variables, natives, ", start, StringComparison.Ordinal);
            Assert.Contains("System32Calls.CreateProcess(exe, commandLine, block, folder, ref startup, out var child)", start, StringComparison.Ordinal);
        }

        // ── The failed start's copy ──

        [Fact]
        public void TheFailedStartsCopy_HandsOnNoLoader_AndNoUnpackFolder()
        {
            var map = MapOfBlock(StartFailure.CopyBlock(System32Calls.MachineVariables(), OwnWithLoaders()));
            HoldsNoLoader(map, natives: null);
            Assert.DoesNotContain(ProtectedStart.HandoverVariable, map.Keys);

            string source = OffLimitsSource.Read("PadForge.App/Services/StartFailure.cs");
            Assert.Contains("CopyBlock(System32Calls.MachineVariables(), variables), exe.Substring(0, slash + 1));",
                FenceSource.Body(source, "internal static void Show(Kind kind)"), StringComparison.Ordinal);
        }

        // ── The logon start ──

        [Fact]
        public void TheLogonStart_HandsOnNoLoader()
        {
            var user = new[] { @"USERNAME=someone", @"USERPROFILE=C:\Users\someone", @"Path=C:\Users\someone\bin" }.Concat(Loaders).ToArray();
            var map = Map(RightsVariables.Build(System32Calls.MachineVariables(), user, Natives));
            HoldsNoLoader(map, Natives);
            Assert.DoesNotContain(@"C:\Users\someone\bin", map["Path"], StringComparison.OrdinalIgnoreCase);

            string source = OffLimitsSource.Read("PadForge.App/Services/LogonStart.cs");
            string launch = FenceSource.Body(source, "internal static int Launch(string sid, string exe, string commandLine, string folder, string natives, out Report report)");
            Assert.Contains("var kept = RightsVariables.Build(system, user, natives);", launch, StringComparison.Ordinal);
            Assert.Contains("string block = RightsVariables.ToBlock(kept);", launch, StringComparison.Ordinal);
        }

        // ── Every start through ProcessStartInfo ──

        public static IEnumerable<object[]> Sites => new[]
        {
            // The installers' tools, pnputil, steamcmd and the cleanup script.
            new object[] { "PadForge.App/Common/DriverInstaller.cs", "internal static Process StartProcess(ProcessStartInfo psi)" },
            new object[] { "PadForge.App/Common/HidHideArm64Installer.cs", "int? Run(string arguments)" },
            // The DSX stand-in, the moved copy and the library host with UAC off.
            new object[] { "PadForge.App/Common/ShellLaunch.cs", "internal static Process StartOwnHelper(ProcessStartInfo psi)" },
            new object[] { "PadForge.App/Common/StartupHelper.cs", "private static int RunSchtasks(string arguments)" },
            new object[] { "PadForge.App/Common/StartupHelper.cs", "private static string RunSchtasksOutput(string arguments)" },
            new object[] { "PadForge.App/Services/Ds3DriverInstaller.cs", "private static (int Code, string Output) RunTool(string exe, string args, string workingDir, int timeoutMs = 120000)" },
            new object[] { "PadForge.App/Services/GiveBackTask.cs", "private static string Output(string arguments)" },
            new object[] { "PadForge.App/Services/GiveBackTask.cs", "private static int Run(string arguments)" },
            // The update helper and PadForge's start after the swap.
            new object[] { "PadForge.App/Services/UpdateService.cs", "internal static Process StartProcess(ProcessStartInfo psi)" },
            new object[] { "PadForge.App/Services/WebControllerServer.cs", "private static string RunNetsh(string arguments)" },
            new object[] { "PadForge.App/Services/WebControllerTls.cs", "private static string RunNetsh(string arguments)" },
        };

        /// <summary>Each site starts through RightsVariables.Start, and the
        /// start it makes there, run through the seam with this process
        /// carrying variables of every family that loads code, holds none of
        /// them.</summary>
        [Theory]
        [MemberData(nameof(Sites))]
        public void EachStartThroughProcessStartInfo_HandsOnNoLoader(string file, string member)
        {
            string body = FenceSource.Body(OffLimitsSource.Read(file), member);
            Assert.Contains("RightsVariables.Start(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start(", body.Replace("RightsVariables.Start(", string.Empty), StringComparison.Ordinal);
            var map = StartedThroughTheSeam(new ProcessStartInfo(file) { UseShellExecute = false });
            HoldsNoLoader(map, NativesHere);
        }

        /// <summary>The driver installer's tools with rights start with the
        /// built variables. Without rights they go to Windows' prompt, where
        /// Windows builds them, and PadForge hands on none.</summary>
        [Fact]
        public void AToolRunWithRights_HandsOnNoLoader_AndWithoutRightsGoesToThePrompt()
        {
            // In the app, a start gets the protected unpack folder, which the
            // test host's fence leaves empty here.
            Assert.Contains("ForChild(ProtectedFolder.Natives) ?? Build(System32Calls.MachineVariables(), null, ProtectedFolder.Natives)",
                FenceSource.Body(OffLimitsSource.Read("PadForge.App/Services/RightsVariables.Start.cs"), "internal static void Give(ProcessStartInfo psi)"),
                StringComparison.Ordinal);
            var with = DriverInstaller.ElevatedStart(@"C:\WINDOWS\system32\cmd.exe", "/d /c x", privileged: true);
            Assert.False(with.UseShellExecute);
            Assert.True(with.CreateNoWindow);
            HoldsNoLoader(StartedThroughTheSeam(with), NativesHere);

            var without = DriverInstaller.ElevatedStart(@"C:\WINDOWS\system32\cmd.exe", "/d /c x", privileged: false);
            Assert.True(without.UseShellExecute);
            Assert.Equal("runas", without.Verb);
            RightsVariables.StartSeam = _ => null;
            try { Assert.Null(RightsVariables.Start(without)); }
            finally { RightsVariables.StartSeam = null; }
        }

        /// <summary>The start through Windows' prompt is the one that hands
        /// on none of PadForge's variables at all: the shell's runas verb,
        /// with no variables set on it.</summary>
        [Fact]
        public void TheStartThroughThePrompt_TakesNoneOfPadForgesVariables()
        {
            string run = FenceSource.Body(OffLimitsSource.Read("PadForge.App/Services/StartWithRights.cs"), "internal static int Run(IReadOnlyList<string> args)");
            Assert.Contains("UseShellExecute = true,", run, StringComparison.Ordinal);
            Assert.Contains("Verb = \"runas\",", run, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment[", run, StringComparison.Ordinal);

            // The update helper's start of the updated PadForge goes through
            // the shell too, from the helper, whose own variables the app
            // built when it started the helper through StartProcess.
            string service = OffLimitsSource.Read("PadForge.App/Services/UpdateService.cs");
            string launch = FenceSource.Body(service, "private static string Launch(string exe, IEnumerable<string> args)");
            Assert.Contains("UseShellExecute = true,", launch, StringComparison.Ordinal);
            Assert.Contains("using var process = StartProcess(psi);", launch, StringComparison.Ordinal);
            Assert.Contains("return RightsVariables.Start(psi);", FenceSource.Body(service, "internal static Process StartProcess(ProcessStartInfo psi)"), StringComparison.Ordinal);
        }

        /// <summary>The builder runs in the protected start, so it makes no
        /// DllImport and reads no culture or System.Environment.</summary>
        [Fact]
        public void TheBuilder_CallsWindowsOnlyThroughSystem32()
        {
            string file = OffLimitsSource.Read("PadForge.App/Services/RightsVariables.cs");
            string code = string.Join("\n", file.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (string banned in new[] { "DllImport(", "LibraryImport(", "Environment.", " Path.", "(Path.", "ToString(\"", "throw ", "Guid.", "StringComparer", "StringComparison" })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }

        /// <summary>A start whose variables cannot be read from this process
        /// still gets the machine's and no more.</summary>
        [Fact]
        public void TheBuilder_TakesTheSystemsFromTheUsersOnlyWhenTheMachinesCannotBeRead()
        {
            var user = new[] { @"Path=C:\Users\someone\bin", @"SystemRoot=C:\WINDOWS", @"USERNAME=someone" }.Concat(Loaders).ToArray();
            var map = Map(RightsVariables.Build(null, user, null));
            Assert.Equal(@"C:\Users\someone\bin", map["Path"]);
            HoldsNoLoader(map, natives: null);
            var machine = new[] { @"Path=C:\WINDOWS\system32", @"SystemRoot=C:\WINDOWS" };
            Assert.Equal(@"C:\WINDOWS\system32", Map(RightsVariables.Build(machine, user, null))["Path"]);
        }

        /// <summary>Runs <paramref name="psi"/> through RightsVariables.Start
        /// with the seam in place and this process carrying the canaries, and
        /// answers the variables it was given.</summary>
        private static Dictionary<string, string> StartedThroughTheSeam(ProcessStartInfo psi)
        {
            ProcessStartInfo seen = null;
            foreach (string name in Canaries) Environment.SetEnvironmentVariable(name, "1");
            RightsVariables.StartSeam = p => { seen = p; return null; };
            try
            {
                // The same-window control: this process has them.
                string[] own = System32Calls.Variables();
                foreach (string name in Canaries)
                    Assert.Contains(own, l => l.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                Assert.Null(RightsVariables.Start(psi));
            }
            finally
            {
                RightsVariables.StartSeam = null;
                foreach (string name in Canaries) Environment.SetEnvironmentVariable(name, null);
            }
            Assert.Same(psi, seen);
            return new Dictionary<string, string>(seen.Environment.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
