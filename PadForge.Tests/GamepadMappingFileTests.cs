using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The mappings PadForge adds to SDL's own at startup
    /// (gamecontrollerdb_padforge.txt, embedded, one SDL_AddGamepadMapping call
    /// per line). A line SDL cannot parse is dropped without a word, so each
    /// one is held to the gamecontrollerdb form here: a 32-digit GUID, a name,
    /// key:value fields and the Windows platform.
    /// </summary>
    public class GamepadMappingFileTests
    {
        private static string[] MappingLines()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllLines(Path.Combine(d.FullName, "PadForge.App", "gamecontrollerdb_padforge.txt"))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && l[0] != '#')
                .ToArray();
        }

        [Fact]
        public void EveryMapping_IsInTheDatabaseForm_AndNoGuidRepeats()
        {
            var lines = MappingLines();
            Assert.NotEmpty(lines);
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                Assert.Matches(new Regex("^[0-9a-f]{32},[^,]+,"), line);
                Assert.True(guids.Add(line.Substring(0, 32)), "repeated GUID: " + line);
                Assert.EndsWith(",platform:Windows,", line);
                foreach (string field in line.Split(',').Skip(2).Where(f => f.Length > 0))
                    Assert.Matches(new Regex("^[a-z0-9+-]+:[+-]?(b\\d+|a\\d+~?|h\\d+\\.\\d+|Windows)$"), field);
            }
        }

        /// <summary>#506: the PS VR Aim Controller, mapped as submitted. The
        /// GUID carries no name CRC, so it matches the controller whatever
        /// name its driver reports.</summary>
        [Fact]
        public void ThePsVrAimController_IsMapped_ForAnyNameCrc()
        {
            string aim = Assert.Single(MappingLines(), l => l.StartsWith("030000004c050000b20b000000000000,", StringComparison.Ordinal));
            foreach (string binding in new[] { "a:b1", "b:b2", "x:b0", "y:b3", "leftshoulder:b4", "rightshoulder:b5", "back:b8",
                         "start:b9", "leftstick:b10", "rightstick:b11", "guide:b12", "leftx:a0", "lefty:a1", "rightx:a2",
                         "righty:a5", "lefttrigger:a3", "righttrigger:a4", "dpup:h0.1", "dpright:h0.2", "dpdown:h0.4", "dpleft:h0.8" })
                Assert.Contains("," + binding + ",", aim);
        }

        /// <summary>The GameSir G7 Pro keeps the Windows mapping it shipped with.
        /// Upstream SDL maps the pad in its macOS section alone, and since
        /// SDL#53 the fork keeps the community's Windows row for such a pad,
        /// so PadForge's file no longer carries it. The release's list of
        /// carried rows holds this row, binding for binding, by its hash.</summary>
        [Fact]
        public void TheGameSirG7Pro_KeepsItsWindowsMapping()
        {
            const string g7 = "03000000373500002210000000000000";
            Assert.DoesNotContain(MappingLines(), l => l.StartsWith(g7 + ",", StringComparison.Ordinal));
            string row = g7 + ",GameSir G7 Pro,a:b0,b:b1,back:b10,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,"
                + "leftshoulder:b6,leftstick:b13,lefttrigger:b8,leftx:a0,lefty:a1,rightshoulder:b7,rightstick:b14,righttrigger:b9,"
                + "rightx:a3,righty:a4,start:b11,x:b3,y:b4,platform:Windows,";
            Assert.True(PadForge.Common.Input.ControllerDatabaseBaseline.Embedded.TryFind(g7, out var entry));
            Assert.Equal(new PadForge.Common.Input.ControllerDatabaseBaselineEntry(PadForge.Common.Input.ControllerDatabaseVerdict.Kept,
                PadForge.Common.Input.ControllerDatabaseRow.HashOf(row)), entry);
        }
    }
}
