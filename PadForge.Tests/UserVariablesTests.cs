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
        /// <summary>The builder needs no member of the protected start, the
        /// allow list or System32Calls.</summary>
        [Fact]
        public void TheBuilder_NeedsNoProtectedStartOrAllowList()
        {
            string file = OffLimitsSource.Read("PadForge.App/Services/UserVariables.cs");
            foreach (string banned in new[] { "ProtectedStart.", "RightsVariables.", "System32Calls.", "DllImport(" })
                Assert.DoesNotContain(banned, file, StringComparison.Ordinal);
        }

        /// <summary>The user's variables are PadForge's own with the user's
        /// registry values over them: a machine variable stands, and the
        /// user's Path, when the user has one, follows the machine's.</summary>
        [Fact]
        public void TheUsersVariables_ArePadForgesOwn_WithTheUsersRegistryValuesOverThem()
        {
            var lines = UserVariables.OfTheUser();
            var map = Map(lines);
            Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), map["SystemRoot"]);
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            if (key?.GetValue("Path") is string userPath && userPath.Length > 0)
            {
                string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
                Assert.StartsWith(machine.TrimEnd(';'), map["Path"], StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(userPath, map["Path"], StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(lines.Length, lines.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        internal static Dictionary<string, string> Map(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                string name = line.Substring(0, Math.Max(0, line.IndexOf('=', line.StartsWith('=') ? 1 : 0)));
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

        // ── The failed start's copy ──

    }
}
