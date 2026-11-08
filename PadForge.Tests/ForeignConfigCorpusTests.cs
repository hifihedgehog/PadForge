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

            int imported = 0, other = 0, clean = 0, partial = 0, skipped = 0, tuned = 0, sticks = 0;
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
                sticks += AssertStickTuningCameAcross(result, file);
            }
            _output.WriteLine($"DS4Windows files {files.Count} imported {imported} not profiles {other} clean {clean} partial {partial} skipped {skipped}, trigger tuning checked on {tuned}, stick tuning on {sticks}");
            Assert.True(tuned > 0, "no sample tunes a trigger");
            Assert.True(sticks > 0, "no sample's stick was checked");
            foreach (var kv in reasons.OrderByDescending(k => k.Value))
                _output.WriteLine($"  {kv.Value,5} {kv.Key}");
            Assert.True(imported >= 46, "imported " + imported);
        }

        /// <summary>Holds the import against the file itself, since a fixture
        /// written from the same belief as the code proves nothing: each
        /// trigger that reads its own pull plays as Mapping.cs computes it from
        /// the file's values, through the fold onto the Triggers card. A
        /// custom curve and Mapping.cs's trigger easeout-cubic, which writes a
        /// negative value into a byte, are left out. Returns how many triggers
        /// it checked.</summary>
        private static int AssertTriggerTuningCameAcross(ForeignImportResult result, string file)
        {
            var root = XDocument.Load(file).Root;
            string sensitivity = root.Element("Sensitivity")?.Value ?? "";
            var parts = sensitivity.Split('|');
            if (parts.Length == 1) parts = sensitivity.Split(',');
            else parts = parts.Select(s => s.Replace(',', '.')).ToArray();
            int checkedTriggers = 0;
            foreach (var (element, prefix, target, index) in new[] { ("LeftTriggerMiddle", "L2", "LeftTrigger", 2), ("RightTriggerMiddle", "R2", "RightTrigger", 3) })
            {
                var row = result.Profile.SlotMappingSets[0].Rows
                    .Where(r => r.Target == target && r.LayerMask == "Base").ToList();
                if (row.Count != 1 || row[0].Sources.Count != 1 || row[0].Sources[0].Descriptor != "Gamepad " + target) continue;
                if (!TriggerCurve(root.Element(prefix + "OutputCurveMode")?.Value, out var curve)) continue;
                double dead = Num(root, element, 0);
                double anti = Math.Max(0.0, Num(root, prefix + "AntiDeadZone", 0));
                double max = Math.Clamp(Num(root, prefix + "MaxZone", 100), 0.0, 100.0);
                double maxOut = Math.Clamp(Num(root, prefix + "MaxOutput", 100), 0.0, 100.0);
                double sens = index < parts.Length && double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s)
                    ? Math.Clamp(s, 0.1, 10.0) : 1.0;
                var played = Ds4TriggerChain.Played(result.Profile, target);
                for (int i = 0; i <= 100; i++)
                {
                    double t = i / 100.0;
                    if (dead > 0 && Math.Abs(t * 255.0 - dead) < 1.0) continue;
                    double want = Ds4TriggerChain.Ds4(t, dead, anti, max, maxOut, sens, curve);
                    double got = played(t);
                    Assert.True(Math.Abs(got - want) <= 0.02, $"{Path.GetFileName(file)} {target} pull {t:0.00}: PadForge {got:0.000}, DS4Windows {want:0.000}");
                }
                checkedTriggers++;
            }
            return checkedTriggers;
        }

        /// <summary>Each stick that reads as a plain stick plays as Mapping.cs
        /// computes it from the file's values, through the fold onto the stick
        /// card: on both axes always, and on the diagonals too unless the file
        /// sets a Radial stick's dead zone, anti-dead zone, a max output that
        /// binds, or a curve, which Mapping.cs works out from the push's angle
        /// and the import says so. A custom curve, a square stick and a stick
        /// whose rows a binding replaced are left out. Returns how many sticks
        /// it checked.</summary>
        private static int AssertStickTuningCameAcross(ForeignImportResult result, string file)
        {
            var root = XDocument.Load(file).Root;
            string sensitivity = root.Element("Sensitivity")?.Value ?? "";
            var parts = sensitivity.Split('|');
            if (parts.Length == 1) parts = sensitivity.Split(',');
            else parts = parts.Select(s => s.Replace(',', '.')).ToArray();
            int checkedSticks = 0;
            foreach (var (prefix, side, index) in new[] { ("LS", "Left", 0), ("RS", "Right", 1) })
            {
                string mode = (root.Element(prefix + "OutputMode")?.Value ?? "Controls").Trim();
                if (!mode.Equals("Controls", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(root.Element(prefix + "SquareStick")?.Value, "True", StringComparison.OrdinalIgnoreCase)) continue;
                string curve = (root.Element(prefix + "OutputCurveMode")?.Value ?? "linear").Trim().ToLowerInvariant();
                if (curve is not ("linear" or "enhanced-precision" or "quadratic" or "cubic" or "easeout-quad" or "easeout-cubic")) continue;
                var rows = result.Profile.SlotMappingSets[0].Rows
                    .Where(r => (r.Target == side + "ThumbAxisX" || r.Target == side + "ThumbAxisY") && r.LayerMask == "Base").ToList();
                if (rows.Count != 2 || rows.Any(r => r.Sources.Count == 0
                        || r.Sources.Any(s => s.HalfAxis || !(s.Descriptor == "Gamepad " + side + "StickX" || s.Descriptor == "Gamepad " + side + "StickY"))))
                    continue;
                var opts = root.Element(prefix + "AxialDeadOptions");
                var p = new Ds4StickSettings
                {
                    Axial = string.Equals(root.Element(prefix + "DeadZoneType")?.Value, "Axial", StringComparison.OrdinalIgnoreCase),
                    MaxOutputForce = string.Equals(root.Element(prefix + "MaxOutputForce")?.Value, "True", StringComparison.OrdinalIgnoreCase),
                    VerticalScale = Math.Clamp(Num(root, prefix + "VerticalScale", 100), 0.0, 400.0),
                    Rotation = Math.Clamp(Num(root, prefix + "Rotation", 0), -180.0, 180.0),
                    Sens = index < parts.Length && double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sv)
                        ? Math.Clamp(sv, 0.1, 5.0) : 1.0,
                    Curve = curve,
                };
                if (p.Axial)
                {
                    p.Dead = Math.Clamp(Num(opts, "DeadZoneX", 0), 0.0, 127.0);
                    p.DeadY = Math.Clamp(Num(opts, "DeadZoneY", 0), 0.0, 127.0);
                    p.Anti = Math.Clamp(Num(opts, "AntiDeadZoneX", 0), 0.0, 100.0);
                    p.AntiY = Math.Clamp(Num(opts, "AntiDeadZoneY", 0), 0.0, 100.0);
                    p.MaxZone = Math.Clamp(Num(opts, "MaxZoneX", 100), 0.0, 100.0);
                    p.MaxZoneY = Math.Clamp(Num(opts, "MaxZoneY", 100), 0.0, 100.0);
                    p.MaxOutput = Math.Clamp(Num(opts, "MaxOutputX", 100), 0.0, 100.0);
                    p.MaxOutputY = Math.Clamp(Num(opts, "MaxOutputY", 100), 0.0, 100.0);
                }
                else
                {
                    p.Dead = Math.Clamp(Num(root, prefix + "DeadZone", 0), 0.0, 127.0);
                    p.Anti = Math.Clamp(Num(root, prefix + "AntiDeadZone", 0), 0.0, 100.0);
                    p.MaxZone = Math.Clamp(Num(root, prefix + "MaxZone", 100), 0.0, 100.0);
                    p.MaxOutput = Math.Clamp(Num(root, prefix + "MaxOutput", 100), 0.0, 100.0);
                }
                double reach = p.MaxZone / 100.0 / Math.Max(1.0, p.VerticalScale / 100.0);
                bool capBinds = (p.MaxOutput != 100 || p.MaxOutputForce) && p.MaxOutput > 0 && reach * p.MaxOutput / 100.0 / 0.99 < 1.0;
                bool angled = !p.Axial && (p.Dead > 0 || p.Anti > 0 || capBinds || p.Curve != "linear");
                Assert.True(angled == result.Report.Entries.Any(e => e.ReasonKey == ImportReasons.StickByAngle && e.SourcePath == prefix + " tuning"),
                    $"{Path.GetFileName(file)} {prefix}: the import's tuning line does not match the stick");
                double deadX = p.Dead / 127.0, deadY = (p.Axial ? p.DeadY : p.Dead) / 127.0;
                var played = Ds4StickChain.Played(result.Profile, left: prefix == "LS");
                for (int degrees = 0; degrees < 360; degrees += angled ? 90 : 15)
                {
                    for (int i = 1; i <= 20; i++)
                    {
                        double rho = i / 20.0;
                        double x = rho * Math.Cos(degrees * Math.PI / 180.0), y = rho * Math.Sin(degrees * Math.PI / 180.0);
                        if (p.Axial ? Math.Abs(Math.Abs(x) - deadX) < 0.01 || Math.Abs(Math.Abs(y) - deadY) < 0.01
                                    : deadX > 0 && Math.Abs(rho - deadX) < 0.01)
                            continue;
                        var want = Ds4StickChain.Ds4(x, y, p);
                        var got = played(x, y);
                        Assert.True(Math.Abs(got.X - want.X) <= 0.02 && Math.Abs(got.Y - want.Y) <= 0.02,
                            $"{Path.GetFileName(file)} {prefix} at {degrees} degrees, {rho:0.00}: PadForge ({got.X:0.000}, {got.Y:0.000}), DS4Windows ({want.X:0.000}, {want.Y:0.000})");
                    }
                }
                checkedSticks++;
            }
            return checkedSticks;
        }

        /// <summary>Mapping.cs's trigger curves (2045-2080). False for a custom
        /// curve and for easeout-cubic.</summary>
        private static bool TriggerCurve(string mode, out Func<double, double> curve)
        {
            curve = null;
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "": case "linear": return true;
                case "enhanced-precision": curve = x => x <= 0.4 ? 0.55 * x : x <= 0.75 ? x - 0.18 : x * 1.72 - 0.72; return true;
                case "quadratic": curve = x => x * x; return true;
                case "cubic": curve = x => x * x * x; return true;
                case "easeout-quad": curve = x => -x * (x - 2.0); return true;
                default: return false;
            }
        }

        private static double Num(XElement root, string element, double fallback)
            => double.TryParse(root.Element(element)?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

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
