using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.ConfigImport;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>Reads every public reWASD and DS4Windows config beside the
    /// repository, when those clones are on disk. Their repositories carry no
    /// license, so they are never committed as fixtures, and a machine without
    /// them runs nothing here.</summary>
    [Collection("SettingsManagerStatics")]
    public class ForeignConfigCorpusTests
    {
        private readonly ITestOutputHelper _output;

        public ForeignConfigCorpusTests(ITestOutputHelper output) => _output = output;

        private static string ClonesRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            return dir?.Parent?.FullName;
        }

        private static List<string> Collect(string root, string pattern, params string[] clones)
        {
            var files = new List<string>();
            foreach (string clone in clones)
            {
                string path = Path.Combine(root, clone);
                if (!Directory.Exists(path)) continue;
                files.AddRange(Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)));
            }
            return files;
        }

        [Fact]
        public void EveryPublicRewasdFileImports()
        {
            string root = ClonesRoot();
            if (root == null) return;
            var files = Collect(root, "*.rewasd", "rewasd-profiles-supersmo", "rewasd-profiles-yungcheeze",
                "shunt-hcn1z1", "reWASD-Powertools-DatMoshu", "reWASDMappings-ShashankaNataraj");
            if (files.Count == 0) return;

            int imported = 0, refused = 0, clean = 0, partial = 0, skipped = 0;
            var reasons = new Dictionary<string, int>();
            foreach (string file in files)
            {
                var result = ForeignConfigImporter.Translate(file);
                if (result.Refused)
                {
                    refused++;
                    _output.WriteLine("REFUSED " + Path.GetFileName(file) + " " + result.RefusedKey);
                    continue;
                }
                imported++;
                clean += result.Report.CleanCount;
                partial += result.Report.PartialCount;
                skipped += result.Report.SkippedCount;
                Tally(reasons, result.Report);
                AssertWellFormed(result, file);
            }
            _output.WriteLine($"reWASD files {files.Count} imported {imported} refused {refused} clean {clean} partial {partial} skipped {skipped}");
            foreach (var kv in reasons.OrderByDescending(k => k.Value))
                _output.WriteLine($"  {kv.Value,5} {kv.Key}");

            // One public sample is a hand edit missing a comma. Everything
            // else reads.
            Assert.True(refused <= 1, "refused " + refused);
        }

        [Fact]
        public void EveryPublicDs4WindowsProfileImports()
        {
            string root = ClonesRoot();
            if (root == null) return;
            var files = Collect(root, "*.xml", "ds4w-profiles-Saclagofast1", "ds4w-profiles-NoBigHomies",
                "ds4w-profiles-vishless", "ds4w-profiles-Gymnast544");
            if (files.Count == 0) return;

            int imported = 0, other = 0, clean = 0, partial = 0, skipped = 0, tuned = 0;
            var reasons = new Dictionary<string, int>();
            foreach (string file in files)
            {
                var result = ForeignConfigImporter.Translate(file);
                if (result.Refused)
                {
                    other++;
                    continue;
                }
                imported++;
                clean += result.Report.CleanCount;
                partial += result.Report.PartialCount;
                skipped += result.Report.SkippedCount;
                Tally(reasons, result.Report);
                AssertWellFormed(result, file);
                tuned += AssertTriggerTuningCameAcross(result, file);
            }
            _output.WriteLine($"DS4Windows files {files.Count} imported {imported} not profiles {other} clean {clean} partial {partial} skipped {skipped}, trigger tuning checked on {tuned}");
            Assert.True(tuned > 0, "no sample tunes a trigger");
            foreach (var kv in reasons.OrderByDescending(k => k.Value))
                _output.WriteLine($"  {kv.Value,5} {kv.Key}");
            Assert.True(imported >= 46, "imported " + imported);
        }

        /// <summary>Holds the import against the file itself, since a fixture
        /// written from the same belief as the code proves nothing. Returns how
        /// many values it checked.</summary>
        private static int AssertTriggerTuningCameAcross(ForeignImportResult result, string file)
        {
            var root = XDocument.Load(file).Root;
            var parts = (root.Element("Sensitivity")?.Value ?? "").Split('|');
            int checkedValues = 0;
            foreach (var (element, target, index) in new[] { ("LeftTriggerMiddle", "LeftTrigger", 2), ("RightTriggerMiddle", "RightTrigger", 3) })
            {
                var src = result.Profile.SlotMappingSets.SelectMany(s => s.Rows)
                    .Where(r => r.Target == target && r.LayerMask == "Base")
                    .SelectMany(r => r.Sources)
                    .FirstOrDefault(s => s.Descriptor == "Gamepad " + target);
                if (src == null) continue;
                if (double.TryParse(root.Element(element)?.Value, out double dead) && dead > 0)
                {
                    Assert.Equal(dead / 255.0, src.ParamStickDeadZoneInner, 3);
                    checkedValues++;
                }
                if (index < parts.Length && double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sens) && sens != 1.0)
                {
                    Assert.Equal(Math.Clamp(sens, 0.1, 5.0), src.Sensitivity, 3);
                    checkedValues++;
                }
            }
            return checkedValues;
        }

        private static void Tally(Dictionary<string, int> reasons, TranslationReport report)
        {
            foreach (var e in report.Entries.Where(e => e.Status != TranslationStatus.Clean))
            {
                string key = e.ReasonKey + " | " + (e.ReasonArgs.Count > 0 ? e.ReasonArgs[0] : e.Binding);
                reasons[key] = reasons.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }

        private static void AssertWellFormed(ForeignImportResult result, string file)
        {
            var p = result.Profile;
            Assert.NotNull(p);
            Assert.NotNull(p.Macros);
            string name = Path.GetFileName(file);
            for (int slot = 0; slot < p.SlotMappingSets.Length; slot++)
            {
                var set = p.SlotMappingSets[slot];
                Assert.NotNull(set);
                if (!p.SlotCreated[slot])
                {
                    Assert.Empty(set.Rows);
                    continue;
                }
                Assert.True(set.Authoritative, name);
                // A row on a layer no activator declares would never run, and
                // a Base row with no source would pin its target.
                var layers = set.ShiftActivators.Select(a => a.LayerMask).ToHashSet();
                foreach (var row in set.Rows)
                {
                    if (row.LayerMask != "Base")
                        Assert.True(layers.Contains(row.LayerMask) || row.Sources.Count == 0 || MacroSwitchesTo(p, row.LayerMask),
                            $"{name}: row {row.Target} on undeclared layer {row.LayerMask}");
                    else
                        Assert.NotEmpty(row.Sources);
                }
                // Two rows for one target on one layer would split its sources.
                Assert.Equal(set.Rows.Count, set.Rows.Select(r => (r.LayerMask, r.Target)).Distinct().Count());
            }
            foreach (var m in p.Macros)
            {
                Assert.NotNull(m.Actions);
                // An empty macro is only ever a block: it consumes the one key
                // the file stops reaching Windows.
                Assert.True(m.Actions.Length > 0 || m.ConsumeTriggerButtons, $"{name}: macro {m.Name} does nothing");
                Assert.True(m.TriggerMode == ViewModels.MacroTriggerMode.Always || !string.IsNullOrEmpty(m.TriggerInputs),
                    $"{name}: macro {m.Name} has no trigger");
            }
        }

        private static bool MacroSwitchesTo(ProfileData p, string mask)
            => p.Macros.Any(m => m.Actions.Any(a =>
                a.Type == ViewModels.MacroActionType.SwitchLayer && a.SwitchLayerMask == mask));
    }
}
