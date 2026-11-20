using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The standing rule of docs/ui-exposure-ledger.md, as tests: a
    /// value the engine reads sits behind a control the user can edit. An
    /// import's slot stamp is only a carrier, so it must be moved into the
    /// device's own setting at assignment and cleared, and the engine must
    /// never read it as a second, hidden setting.</summary>
    public class UiExposureLedgerTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        /// <summary>Every persisted Workshop* member of MappingSet.</summary>
        private static List<string> WorkshopStamps()
        {
            string set = Read("PadForge.Engine", "Data", "MappingSet.cs");
            var names = Regex.Matches(set, @"\[XmlAttribute\]\s*public\s+(?:string|bool)\s+(Workshop\w+)")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.True(names.Count >= 15, "the stamp scan found " + names.Count);
            return names;
        }

        [Fact]
        public void EveryWorkshopStamp_IsClearedAtAssignment()
        {
            string applier = Read("PadForge.App", "Services", "WorkshopTuningApplier.cs");
            var gaps = WorkshopStamps()
                .Where(n => !Regex.IsMatch(applier, @"set\." + n + @"\s*=\s*(""""|false)"))
                .ToList();
            Assert.True(gaps.Count == 0, "never folded and cleared: " + string.Join(", ", gaps));
        }

        /// <summary>The steering modes read ParamStickDeadzone before their
        /// math (SourceKindRuntime.TickWindingStick and TickAngleToAxis).
        /// The steering save stamps every other steering value from the
        /// stick's Steering rows, so this one needs a row and a stamp of its
        /// own, or it is a fixed 15 percent nobody can change.</summary>
        [Fact]
        public void TheSteeringStickDeadZone_HasARowAndIsStamped()
        {
            string svc = Read("PadForge.App", "Services", "SettingsService.cs");
            string xaml = Read("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("src.ParamStickDeadzone = cfg.", svc);
            Assert.Contains("Binding SteerStickDeadZone,", xaml);
            Assert.Contains("ResetSteerStickDeadZoneCommand", xaml);
        }

        [Fact]
        public void TheEngine_ReadsNoWorkshopStamp()
        {
            var dir = Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input");
            var reads = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "InputManager*.cs"))
            {
                // Any Workshop* member, the stamps' cached views included.
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\.(Workshop[A-Z]\w*)"))
                    reads.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
            }
            Assert.True(reads.Count == 0, "read at run time: " + string.Join(", ", reads));
        }
    }
}
