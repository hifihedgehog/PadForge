using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A start with rights that goes on as it is keeps only the allow list's
    /// variables (#600). Such a start already unpacked into the protected
    /// folder, because DOTNET_BUNDLE_EXTRACT_BASE_DIR named it, and every
    /// program of the user can name it in HKCU\Environment, from which
    /// Windows builds the variables of the copy its prompt starts
    /// (StartWithRights). Before this, that start went on with every variable
    /// the user's programs had set, past the allow list every other start
    /// with rights gets (RightsVariables).
    /// </summary>
    public class ProtectedStartAllowListTests
    {
        private const string Natives = @"C:\ProgramData\PadForge\Natives";

        private static readonly string[] Machine =
        {
            @"ComSpec=C:\WINDOWS\system32\cmd.exe", @"Path=C:\WINDOWS\system32;C:\WINDOWS", @"SystemRoot=C:\WINDOWS",
            @"windir=C:\WINDOWS",
        };

        /// <summary>The variables a start the user's programs steered would
        /// have: the protected folder named, a profiler, SDL's rows and a
        /// library, Windows' folder changed, beside the user's own names and
        /// a PadForge switch the diagnostics read.</summary>
        private static readonly string[] Steered =
        {
            "=C:=C:\\Users\\someone",
            "CORECLR_ENABLE_PROFILING=1", @"CORECLR_PROFILER_PATH=C:\Users\someone\p.dll",
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + Natives,
            "PADFORGE_DIAG=1",
            @"Path=C:\Users\someone\bin",
            "SDL_GAMECONTROLLERCONFIG=030000004c050000e60c000000000000,Pad,a:b0,",
            "SDL_GAMECONTROLLER_IGNORE_DEVICES=0x054c/0x0ce6",
            "USERNAME=someone",
            @"windir=C:\Users\someone\win",
        };

        private static Dictionary<string, string> Map(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                string name = ProtectedStart.NameOf(line);
                map[name] = line.Substring(name.Length + 1);
            }
            return map;
        }

        /// <summary>Every variable off the allow list goes, the system's
        /// names take the machine's values, and the user's names and
        /// PadForge's switches stay. The drive lines Windows keeps for each
        /// process stay as they are.</summary>
        [Fact]
        public void AStartThatGoesOn_KeepsOnlyTheAllowList()
        {
            var (drop, set) = ProtectedStart.AllowListOnly(Steered, Machine, Natives);

            Assert.Equal(new[] { "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "SDL_GAMECONTROLLERCONFIG", "SDL_GAMECONTROLLER_IGNORE_DEVICES" },
                drop.OrderBy(n => n, StringComparer.Ordinal).ToArray());
            var kept = Map(set);
            Assert.Equal(@"C:\WINDOWS", kept["windir"]);
            Assert.Equal(@"C:\WINDOWS\system32;C:\WINDOWS", kept["Path"]);
            Assert.Equal("someone", kept["USERNAME"]);
            Assert.Equal("1", kept["PADFORGE_DIAG"]);
            Assert.Equal(Natives, kept[ProtectedStart.BaseVariable]);
            Assert.False(kept.ContainsKey("SDL_GAMECONTROLLERCONFIG"));
            Assert.DoesNotContain(set, l => l.StartsWith("=", StringComparison.Ordinal));
            Assert.DoesNotContain("=C:", drop);
        }

        /// <summary>
        /// A start PadForge built itself holds only the allow list already,
        /// so going on changes none of its variables. The one line a start
        /// with rights is handed past the allow list, the handover event of
        /// the protected start's restart (ProtectedStart.Restart's block), is
        /// taken and removed before the variables are narrowed: it would be
        /// dropped otherwise. RightsVariables.Start hands on no added line,
        /// and the logon start none.
        /// </summary>
        [Fact]
        public void AStartPadForgeBuilt_LosesNothing()
        {
            var built = RightsVariables.Build(Machine, Steered, Natives);
            var (drop, set) = ProtectedStart.AllowListOnly(built, Machine, Natives);
            Assert.Empty(drop);
            Assert.Equal(built.OrderBy(l => l, StringComparer.Ordinal), set.OrderBy(l => l, StringComparer.Ordinal));

            string handover = ProtectedStart.HandoverVariable + "=1a4";
            var restarted = RightsVariables.Build(Machine, Steered, Natives, handover);
            Assert.Equal(new[] { ProtectedStart.HandoverVariable }, ProtectedStart.AllowListOnly(restarted, Machine, Natives).Drop);
            var taken = restarted.Where(l => l != handover).ToList();
            Assert.Empty(ProtectedStart.AllowListOnly(taken, Machine, Natives).Drop);

            string source = OffLimitsSource.Read("PadForge.App/Services/ProtectedStart.cs");
            string run = FenceSource.Body(source, "internal static void Run()");
            int take = run.IndexOf("TakeHandover(System32Calls.GetVariable(HandoverVariable), ", StringComparison.Ordinal);
            int keep = run.IndexOf("KeepAllowListOnly(natives)", StringComparison.Ordinal);
            Assert.True(take > 0 && keep > take, "the handover is not taken before the variables are narrowed");
            string start = OffLimitsSource.Read("PadForge.App/Services/RightsVariables.Start.cs");
            Assert.Contains("ForChild(ProtectedFolder.Natives) ?? Build(System32Calls.MachineVariables(), null, ProtectedFolder.Natives)",
                start, StringComparison.Ordinal);
        }

        /// <summary>
        /// The protected start makes the process's variables the allow list's
        /// before it lets a start with rights go on, and a start whose
        /// variables cannot be read starts again instead, as a start that
        /// cannot vouch for them. The pinned lines around it stand.
        /// </summary>
        [Fact]
        public void TheProtectedStart_KeepsTheAllowListBeforeItGoesOn()
        {
            string source = OffLimitsSource.Read("PadForge.App/Services/ProtectedStart.cs");
            string run = FenceSource.Body(source, "internal static void Run()");
            int privileged = run.IndexOf("if (!System32Calls.IsPrivileged()) return;", StringComparison.Ordinal);
            int goesOn = run.IndexOf("if (Decide(true, full, natives, System32Calls.SameName, ProtectedFolder.NativesAreSound) == Decision.GoOn\n"
                + "                && KeepAllowListOnly(natives))\n"
                + "                return;", StringComparison.Ordinal);
            int restart = run.IndexOf("System32Calls.ExitNow(Restart(natives));", StringComparison.Ordinal);
            Assert.True(privileged > 0 && goesOn > privileged && restart > goesOn, "a start with rights goes on before its variables are the allow list's");

            string keep = FenceSource.Body(source, "private static bool KeepAllowListOnly(string natives)");
            Assert.Contains("string[] own = System32Calls.Variables();", keep, StringComparison.Ordinal);
            Assert.Contains("if (own == null) return false;", keep, StringComparison.Ordinal);
            Assert.Contains("AllowListOnly(own, System32Calls.MachineVariables(), natives)", keep, StringComparison.Ordinal);
            Assert.Contains("System32Calls.SetVariable(name, null)", keep, StringComparison.Ordinal);
        }

        /// <summary>SDL's own variables are none of the allow list's, so the
        /// SDL_GAMECONTROLLERCONFIG variable reaches no start with rights, and
        /// with it no engine (#600 items 47 and 49).</summary>
        [Fact]
        public void SdlsVariables_AreNoneOfTheAllowLists()
        {
            foreach (string name in new[] { "SDL_GAMECONTROLLERCONFIG", "SDL_GAMECONTROLLERCONFIG_FILE", "SDL_GAMECONTROLLER_IGNORE_DEVICES" })
                Assert.False(RightsVariables.IsKept(name), name);
        }

        /// <summary>The second start that once kept its own variables, one
        /// whose System32 calls are not ready, is one no program without
        /// rights can cause: the calls come from System32's kernel32 and
        /// advapi32 by full path, under Windows' folder as the kernel's own
        /// page names it. They are ready in this process, which has no part
        /// in choosing them.</summary>
        [Fact]
        public void TheSystemCalls_AreReady_WhateverTheProcessSet()
        {
            Assert.True(System32Calls.Ready);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'), System32Calls.WindowsFolder,
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
