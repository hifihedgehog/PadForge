using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Models2D;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Seam pins for the 2026-07-19 raw-grammar rename ("Extended*" →
    /// "Raw*"). Every writer, reader, and normalizer of the shared
    /// PadSetting dictionaries must speak the SAME grammar, or a seam
    /// silently reads defaults. Each test pairs the contract under test
    /// with a same-shaped positive control that already holds, so a
    /// failure isolates the one broken seam rather than the harness.
    /// </summary>
    public class RawGrammarSeamRegressionTests
    {
        private static string[] InvokeKeyTable(string method, int index)
        {
            var mi = typeof(InputManager).GetMethod(method,
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(mi);
            return (string[])mi.Invoke(null, new object[] { index });
        }

        // ── Step 3 trigger-tuning key table ──
        //
        // Save side (InputService.UpdatePadSettingTuning, SettingsService
        // .UpdatePadSettingsFromViewModels) writes RawTrigger{g}Dz/Adz/Mr/
        // Curve. Load-side dict normalization (PadSetting.
        // EnsureRawMappingDict → NormalizeRawToken) upgrades legacy
        // ExtendedTrigger* keys to RawTrigger* too. The engine's Step 3
        // read (InputManager.Step3.UpdateOutputStates, ExtTriggerKeys)
        // must therefore ask for RawTrigger* or it can never hit.

        [Fact]
        public void Step3TriggerTuningKeys_UseTheRawGrammar()
        {
            // Positive control: the sibling stick table was renamed.
            var sk = InvokeKeyTable("ExtStickKeys", 2);
            Assert.Equal("RawStick2DzShape", sk[0]);

            var tk = InvokeKeyTable("ExtTriggerKeys", 2);
            Assert.Equal("RawTrigger2Dz", tk[0]);
            Assert.Equal("RawTrigger2Adz", tk[1]);
            Assert.Equal("RawTrigger2Mr", tk[2]);
            Assert.Equal("RawTrigger2Curve", tk[3]);
        }

        [Fact]
        public void Step3TriggerTuningRead_SeesTheSaveSideWrite()
        {
            // The exact seam: UI saves trigger-2 tuning, engine reads it.
            var ps = new PadSetting();
            ps.SetRawMapping("RawTrigger2Dz", "35");
            ps.SetRawMapping("RawStick2DzX", "12"); // positive control

            var sk = InvokeKeyTable("ExtStickKeys", 2);
            Assert.Equal("12", ps.GetRawMapping(sk[1])); // holds today

            var tk = InvokeKeyTable("ExtTriggerKeys", 2);
            Assert.Equal("35", ps.GetRawMapping(tk[0]));
        }

        // ── Per-mapping deadzone / bidirectional companion dicts ──
        //
        // Both dicts are keyed by TARGET NAME, the same token vocabulary
        // the raw mapping dict uses, and both are read post-rename under
        // "Raw*" keys (Step 3 fallback lane at GetMappingDeadZone(key)/
        // GetMappingBidirectional(key) with the renamed cached tables;
        // MappingSetMigrator.BuildSource via GetMappingDeadZone(target)).
        // A pre-rename save stores them under "ExtendedAxis2"-style keys,
        // so they need the same NormalizeRawToken upgrade the raw mapping
        // dict got. Without it, legacy Extended per-mapping deadzones and
        // bidirectional flags silently read as defaults after upgrade.

        [Fact]
        public void LegacyMappingDeadZoneKeys_NormalizeOnFirstRead()
        {
            var ps = new PadSetting
            {
                // Positive control: the raw MAPPING dict normalizes.
                RawMappingEntries = new[]
                {
                    new RawMappingEntry { Key = "ExtendedAxis2", Value = "Axis 2" },
                },
                MappingDeadZoneEntries = new[]
                {
                    new RawMappingEntry { Key = "ExtendedAxis2", Value = "30" },
                },
            };
            Assert.Equal("Axis 2", ps.GetRawMapping("RawAxis2")); // holds today
            Assert.Equal("30", ps.GetMappingDeadZone("RawAxis2"));
        }

        [Fact]
        public void LegacyMappingBidirectionalKeys_NormalizeOnFirstRead()
        {
            var ps = new PadSetting
            {
                MappingBidirectionalEntries = new[]
                {
                    new RawMappingEntry { Key = "ExtendedAxis2", Value = "1" },
                },
            };
            Assert.Equal("1", ps.GetMappingBidirectional("RawAxis2"));
        }

        // ── 2D hit-polygon data validity (all six layouts) ──
        //
        // ControllerModel2DView.BuildHitGeometry parses HitPath with
        // double.Parse and Substring(0, IndexOf(',')); a malformed token
        // throws and takes down view construction for that controller
        // model. This walks every layout's polygons through the same
        // parse contract so a data regression can't ship silently.

        public static TheoryData<string, OverlayElement[]> AllLayouts => new()
        {
            { "Xbox360", Xbox360Layout.Overlays },
            { "DS4", DS4Layout.Overlays },
            { "DualSense", DualSenseLayout.Overlays },
            { "XboxOneS", XboxOneSLayout.Overlays },
            { "XboxSeriesX", XboxSeriesXLayout.Overlays },
            { "SwitchPro", SwitchProLayout.Overlays },
        };

        [Theory]
        [MemberData(nameof(AllLayouts))]
        public void HitPaths_ParseCleanAndStayNormalized(string layout, OverlayElement[] overlays)
        {
            foreach (var ov in overlays)
            {
                if (string.IsNullOrEmpty(ov.HitPath)) continue;
                foreach (var poly in ov.HitPath.Split(';'))
                {
                    var pts = poly.Split(' ');
                    Assert.True(pts.Length >= 3,
                        $"{layout}/{ov.TargetName}: polygon with {pts.Length} points");
                    foreach (var t in pts)
                    {
                        int c = t.IndexOf(',');
                        Assert.True(c > 0 && c < t.Length - 1,
                            $"{layout}/{ov.TargetName}: malformed token '{t}'");
                        double x = double.Parse(t.Substring(0, c),
                            System.Globalization.CultureInfo.InvariantCulture);
                        double y = double.Parse(t.Substring(c + 1),
                            System.Globalization.CultureInfo.InvariantCulture);
                        Assert.InRange(x, 0.0, 1.0);
                        Assert.InRange(y, 0.0, 1.0);
                    }
                }
            }
        }
    }
}
