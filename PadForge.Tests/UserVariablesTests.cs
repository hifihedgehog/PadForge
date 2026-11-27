using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A process PadForge starts as the user without rights (a plugin, the
    /// failed start's copy) gets the variables Windows
    /// builds for that user's token, never PadForge's own, which after a
    /// start with rights are the allow list (#597). The builder drops
    /// <c>__COMPAT_LAYER</c>, lets the given variables win and runs in the
    /// protected start.
    /// </summary>
    public class UserVariablesTests
    {
        internal static Dictionary<string, string> Map(IEnumerable<string> lines)
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

        /// <summary>The Path entries the user set in the profile
        /// (HKCU\Environment) that name no other variable, as stored.</summary>
        internal static IEnumerable<string> UsersOwnPathEntries()
        {
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            string raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return (raw ?? string.Empty).Split(';').Select(e => e.Trim()).Where(e => e.Length > 0 && e.IndexOf('%') < 0).ToList();
        }

        [Fact]
        public void TheCompatibilityLayer_StaysOut_FromTheUsersAndFromTheGiven()
        {
            var lines = UserVariables.Build(new[] { "__COMPAT_LAYER=RunAsAdmin", "__compat_layer=Second", "A=1" },
                new Dictionary<string, string> { ["__Compat_Layer"] = "RunAsAdmin" });
            Assert.Equal(new[] { "A=1" }, lines);
        }

        [Fact]
        public void TheGivenVariables_Win_AndEachNameIsKeptOnce()
        {
            var lines = UserVariables.Build(new[] { @"Path=C:\Users\someone\bin", "path=second", "USERNAME=someone" },
                new Dictionary<string, string> { ["PATH"] = @"C:\given", ["NEW"] = "x" });
            Assert.Equal(new[] { "NEW=x", @"PATH=C:\given", "USERNAME=someone" }, lines);
        }

        [Fact]
        public void DriveLines_BadNames_AndNullCharacters_CannotSplitTheBlock()
        {
            var lines = UserVariables.Build(new[] { @"=C:=C:\", "", "NoEquals", "B=x\0y" },
                new Dictionary<string, string> { ["a=b"] = "1", [""] = "2", ["c\0d"] = "3", ["E"] = "f\0g", ["Z"] = null });
            Assert.Equal(new[] { "B=xy", "E=fg", "Z=" }, lines);
            Assert.Null(UserVariables.Block(null, null));
            Assert.Equal("\0\0", UserVariables.Block(Array.Empty<string>(), null));
            Assert.Equal("B=xy\0\0", UserVariables.Block(new[] { "B=xy" }, null));
        }

        /// <summary>Sorted by name, upper and lower case alike, as Windows
        /// keeps a block, with the characters past the ASCII letters by
        /// their code.</summary>
        [Fact]
        public void TheLines_AreSortedAsWindowsKeepsABlock()
        {
            var lines = UserVariables.Build(new[] { "zeta=1", "Alpha=2", "_x=3", "beta=4" }, null);
            Assert.Equal(new[] { "Alpha=2", "beta=4", "zeta=1", "_x=3" }, lines);
        }

        /// <summary>The real reader: this process's own token gives the
        /// variables Windows builds for this user, which hold none of the
        /// variables set in this process alone and every Path entry the user
        /// set. A handle that may not read the token is refused with
        /// Windows' error.</summary>
        [Fact]
        public void TheReader_GivesTheUsersOwnVariables_NotThisProcesss()
        {
            string only = "PADFORGE_TEST_ONLY_HERE_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(only, "1");
            try
            {
                Assert.Contains(System32Calls.Variables(), l => l.StartsWith(only + "=", StringComparison.Ordinal));
                string[] lines = System32Calls.VariablesOfProcess(System32Calls.CurrentProcess, out int error);
                Assert.NotNull(lines);
                Assert.Equal(0, error);
                var map = Map(lines.Where(l => l.Length > 0 && l[0] != '='));
                Assert.DoesNotContain(only, map.Keys);
                Assert.Equal(Environment.UserName, map["USERNAME"], ignoreCase: true);
                foreach (string entry in UsersOwnPathEntries())
                    Assert.Contains(entry, map["Path"].Split(';'), StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Environment.SetEnvironmentVariable(only, null);
            }
        }

        // ── The failed start's copy ──

        /// <summary>The failed start's copy runs as the user without rights,
        /// so it gets the user's own variables: the ones Windows builds for
        /// the start's token, no unpack folder and no handover of the start's,
        /// none set in the start's process alone, and no compatibility
        /// layer.</summary>
        [Fact]
        public void TheFailedStartsCopy_GetsTheUsersOwnVariables()
        {
            string only = "PADFORGE_TEST_ONLY_HERE_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(only, "1");
            try
            {
                string[] user = System32Calls.VariablesOfProcess(System32Calls.CurrentProcess, out _);
                Assert.NotNull(user);
                string block = StartFailure.CopyBlock(user.Concat(new[] { "__COMPAT_LAYER=RunAsAdmin" }).ToArray());
                Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
                var map = Map(block[..^2].Split('\0'));
                Assert.DoesNotContain(only, map.Keys);
                Assert.DoesNotContain(UserVariables.CompatLayer, map.Keys);
                Assert.DoesNotContain(ProtectedStart.HandoverVariable, map.Keys);
                var expected = Map(user.Where(l => l.Length > 0 && l[0] != '='));
                Assert.Equal(expected.Count, map.Count);
                foreach (var pair in expected) Assert.Equal(pair.Value, map[pair.Key]);
                Assert.Null(StartFailure.CopyBlock(null));
            }
            finally
            {
                Environment.SetEnvironmentVariable(only, null);
            }

            string show = FenceSource.Body(OffLimitsSource.Read("PadForge.App/Services/StartFailure.cs"), "internal static void Show(Kind kind)");
            Assert.Contains("string block = CopyBlock(System32Calls.VariablesOfProcess(System32Calls.CurrentProcess, out _));", show, StringComparison.Ordinal);
            Assert.Contains("if (block != null && slash > 0)", show, StringComparison.Ordinal);
            Assert.DoesNotContain("System32Calls.Variables()", show, StringComparison.Ordinal);
            Assert.DoesNotContain("RightsVariables", show, StringComparison.Ordinal);
        }

        /// <summary>The builder runs in the protected start, so it makes no
        /// DllImport and reads no culture or System.Environment.</summary>
        [Fact]
        public void TheBuilder_CallsWindowsOnlyThroughSystem32()
        {
            string file = OffLimitsSource.Read("PadForge.App/Services/UserVariables.cs");
            string code = string.Join("\n", file.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (string banned in new[] { "DllImport(", "LibraryImport(", "Environment.", " Path.", "(Path.", "ToString(\"", "throw ", "Guid.", "StringComparer", "StringComparison" })
                Assert.DoesNotContain(banned, code, StringComparison.Ordinal);
        }
    }
}
