using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Locks for the delta audit of 2026-08-23 (d7bec27e..HEAD).
    ///
    /// <para>Every one of these covers a defect the delta itself created. The
    /// sparse axis list was the common root of the first: it changed what a
    /// long-standing number MEANS, and the consumers written against the old
    /// meaning went on compiling.</para></summary>
    public class AuditDelta20260823Tests
    {
        // ── CapAxeCount is no longer an upper bound ─────────────────────────

        /// <summary>The shape recorded for the owner's own Move Navigation in
        /// the live PadForge.xml: ten populated axis slots whose highest index
        /// is 15. Reading CapAxeCount as the bound calls "Axis 15" foreign,
        /// and a foreign verdict strips the device from every slot and
        /// replaces the user's PadSetting.</summary>
        [Fact]
        public void InventoryBounds_AxisBound_SurvivesASparseAxisList()
        {
            var nav = new UserDevice
            {
                CapAxeCount = 10,          // the sparse list's LENGTH
                RawAxisCount = 16,         // the real ceiling
                CapAxisIndices = new[] { 0, 1, 4, 6, 7, 10, 12, 13, 14, 15 },
                CapButtonCount = 13,
                RawButtonCount = 22,
                CapPovCount = 1,
            };

            var (buttons, axes, povs) = DeviceService.InventoryBounds(nav);

            Assert.Equal(16, axes);
            Assert.True(nav.CapAxisIndices[^1] < axes,
                "every recorded axis slot must sit inside the bound");
            Assert.Equal(22, buttons);
            Assert.Equal(1, povs);
        }

        /// <summary>The cap counts are the fallback, not the preference. A
        /// device PadForge has never seen online carries no raw counts, and
        /// there is nothing better to use.</summary>
        [Fact]
        public void InventoryBounds_FallsBackToTheCapCountsWithoutRawOnes()
        {
            var offline = new UserDevice { CapAxeCount = 6, CapButtonCount = 11, CapPovCount = 1 };
            var (buttons, axes, _) = DeviceService.InventoryBounds(offline);
            Assert.Equal(6, axes);
            Assert.Equal(11, buttons);
        }

        [Fact]
        public void InventoryBounds_NullDeviceIsUnknownRatherThanEmpty()
            => Assert.Equal((0, 0, 0), DeviceService.InventoryBounds(null));

        // ── the EQ reset actually drops history ─────────────────────────────

        private static List<EqBand> OneLoudBand() => new()
        {
            new EqBand { Type = EqBandType.Peaking, FrequencyHz = 200f, GainDb = 12f, Q = 0.7f },
        };

        private static ParametricEqStage StageFedABurst(bool reset)
        {
            var s = new ParametricEqStage();
            s.SetBands(OneLoudBand(), 48000);
            var burst = new float[512];
            for (int i = 0; i < burst.Length; i++) burst[i] = i % 2 == 0 ? 0.9f : -0.9f;
            s.Process(burst, burst.Length / 2);
            if (reset) s.Reset();
            return s;
        }

        private static (float[] Used, float[] Fresh) SilenceThrough(ParametricEqStage used)
        {
            var fresh = new ParametricEqStage();
            fresh.SetBands(OneLoudBand(), 48000);
            var a = new float[256];
            var b = new float[256];
            used.Process(a, a.Length / 2);
            fresh.Process(b, b.Length / 2);
            return (a, b);
        }

        /// <summary>Reset used to build a new holder around the SAME
        /// BiQuadFilter instances, so it carried their history and reset
        /// nothing while reading as though it had. The proof is a stage fed a
        /// loud burst then reset: it must agree with a stage that never heard
        /// the burst at all.</summary>
        [Fact]
        public void EqReset_DropsFilterHistory()
        {
            var (a, b) = SilenceThrough(StageFedABurst(reset: true));
            for (int i = 0; i < a.Length; i++) Assert.Equal(b[i], a[i], 6);
        }

        /// <summary>The positive control the assertion above needs: without
        /// the reset the two stages genuinely differ, so "they agree" is not
        /// vacuously true of a stage that does nothing.</summary>
        [Fact]
        public void EqReset_WithoutIt_TheHistoryIsAudible()
        {
            var (a, b) = SilenceThrough(StageFedABurst(reset: false));
            bool differs = false;
            for (int i = 0; i < a.Length && !differs; i++)
                if (Math.Abs(a[i] - b[i]) > 1e-6f) differs = true;
            Assert.True(differs, "the burst must leave history for the reset to have something to drop");
        }

        // ── the editor's clamp is the engine's ──────────────────────────────

        /// <summary>The band editor's doc says its clamps match the engine's.
        /// They now do, through one shared rule rather than two numbers that
        /// happened to sit near each other.</summary>
        [Fact]
        public void BandFrequencyCeiling_IsSharedBetweenEditorAndEngine()
        {
            Assert.Equal(21600f, EqBand.MaxFrequencyHz(48000));

            var vm = new PadForge.ViewModels.EqBandVm(new EqBand()) { FrequencyHz = 30000f };
            Assert.Equal(EqBand.MaxFrequencyHz(48000), vm.FrequencyHz, 3);

            // And the DSP honours the same number: a band pinned at the
            // ceiling still compiles into a live filter, which it can only do
            // if the stage does not reject it.
            var stage = new ParametricEqStage();
            stage.SetBands(new List<EqBand>
            {
                new EqBand { Type = EqBandType.Peaking, FrequencyHz = vm.FrequencyHz, GainDb = 6f, Q = 1f },
            }, 48000);
            Assert.True(stage.Active);
        }

        // ── the band-type picker is localized ───────────────────────────────

        /// <summary>The picker bound the enum values with no template, so it
        /// rendered ToString(): six raw English identifiers, in every locale.
        /// The contract is PROVENANCE, not spelling. Each member must resolve
        /// through the resource table, which is what makes the other nine
        /// locales reachable at all.
        ///
        /// <para>Deliberately not "the label differs from the identifier".
        /// Notch's correct English label IS "Notch", and an audio term that
        /// happens to match its enum member is not evidence of the bug.</para>
        /// </summary>
        [Theory]
        [InlineData(EqBandType.Peaking, "Pad_Audio_EqType_Peaking")]
        [InlineData(EqBandType.LowShelf, "Pad_Audio_EqType_LowShelf")]
        [InlineData(EqBandType.HighShelf, "Pad_Audio_EqType_HighShelf")]
        [InlineData(EqBandType.HighPass, "Pad_Audio_EqType_HighPass")]
        [InlineData(EqBandType.LowPass, "Pad_Audio_EqType_LowPass")]
        [InlineData(EqBandType.Notch, "Pad_Audio_EqType_Notch")]
        public void BandTypeNames_ComeFromTheResourceTable(EqBandType t, string key)
        {
            var c = new PadForge.Converters.EqBandTypeNameConverter();
            var s = c.Convert(t, typeof(string), null,
                System.Globalization.CultureInfo.InvariantCulture) as string;
            Assert.False(string.IsNullOrWhiteSpace(s));

            var prop = typeof(PadForge.Resources.Strings.Strings).GetProperty(key);
            Assert.NotNull(prop);
            Assert.Equal((string)prop.GetValue(PadForge.Resources.Strings.Strings.Instance), s);
        }

        /// <summary>The compound identifiers had no chance of being right as
        /// ToString(): "LowShelf" and "HighPass" are not words. Those four
        /// must genuinely differ from their member names.</summary>
        [Theory]
        [InlineData(EqBandType.LowShelf)]
        [InlineData(EqBandType.HighShelf)]
        [InlineData(EqBandType.HighPass)]
        [InlineData(EqBandType.LowPass)]
        public void BandTypeNames_CompoundOnesAreNotTheRawIdentifier(EqBandType t)
        {
            var c = new PadForge.Converters.EqBandTypeNameConverter();
            Assert.NotEqual(t.ToString(), (string)c.Convert(t, typeof(string), null,
                System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Distinct labels, because a converter that answered the
        /// same string for two members would pass the check above and still
        /// make the picker unusable.</summary>
        [Fact]
        public void BandTypeNames_AreDistinct()
        {
            var c = new PadForge.Converters.EqBandTypeNameConverter();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (EqBandType t in Enum.GetValues<EqBandType>())
                Assert.True(seen.Add((string)c.Convert(t, typeof(string), null,
                    System.Globalization.CultureInfo.InvariantCulture)), "duplicate label for " + t);
            Assert.Equal(6, seen.Count);
        }

        // ── the EQ grid follows the config swap, not one caller ────────────

        /// <summary>The grid holds rows decoded from whichever config was
        /// bound, and a row edit re-encodes ALL of them back into whichever
        /// config is bound NOW. A swap without a rebuild therefore does not
        /// merely display the outgoing device's EQ, it writes it onto the
        /// incoming one on the next keystroke.
        ///
        /// <para>Only the device-switch path used to rebuild. The slot delete,
        /// the uncreated-slot clear and the sentinel bind are three more
        /// replacement paths that did not, which is why the rebuild moved onto
        /// the swap itself.</para></summary>
        [Fact]
        public void EqGrid_RebuildsOnEveryDeviceConfigSwap()
        {
            var vm = new PadForge.ViewModels.PadViewModel(0);

            var outgoing = new PadForge.ViewModels.DeviceSlotConfig
            {
                AudioEqBands = "PK:1050:-3.5:1.2:1|LSC:105:5.5:0.7:1",
            };
            vm.DeviceConfig = outgoing;
            Assert.Equal(2, vm.EqBands.Count);

            // A fresh config owns no bands. The grid must say so.
            vm.DeviceConfig = new PadForge.ViewModels.DeviceSlotConfig();
            Assert.Empty(vm.EqBands);

            // And the outgoing rows must not have been written onto it.
            Assert.Equal(string.Empty, vm.DeviceConfig.AudioEqBands);
        }

        /// <summary>The clobber itself, stated as a test rather than as a
        /// worry: edit a row after a swap and the incoming config must receive
        /// only what the grid legitimately holds.</summary>
        [Fact]
        public void EqGrid_AfterASwap_ARowEditCannotResurrectTheOutgoingBands()
        {
            var vm = new PadForge.ViewModels.PadViewModel(0);
            vm.DeviceConfig = new PadForge.ViewModels.DeviceSlotConfig
            {
                AudioEqBands = "PK:1050:-3.5:1.2:1|LSC:105:5.5:0.7:1",
            };
            Assert.Equal(2, vm.EqBands.Count);   // positive control: the grid really holds them

            var incoming = new PadForge.ViewModels.DeviceSlotConfig();
            vm.DeviceConfig = incoming;

            vm.AddEqBandCommand.Execute(null);
            Assert.Single(vm.EqBands);
            Assert.DoesNotContain("LSC", incoming.AudioEqBands);
        }

        // ── Shutdown drains its own static state ────────────────────────────

        /// <summary>A source-text lock, because both live in private statics
        /// with no in-process seam and calling the real Shutdown from a test
        /// would tear down device I/O.
        ///
        /// <para>The contract: Shutdown must clear the routed edge detector
        /// and drain the jack watches. The worker exits WITHOUT a final
        /// reconcile, so nothing else ever writes the falling edge, and a
        /// restart came back with every previously-routed slot already reading
        /// routed. The rising edge then never fired and the firmware speaker
        /// path was never asserted, which is the exact defect 9751b818 exists
        /// to cure.</para></summary>
        [Fact]
        public void Shutdown_ClearsTheRoutedEdgeAndDrainsTheJackWatches()
        {
            string body = ShutdownBody();
            Assert.Contains("_lastRouted", body);
            Assert.Contains("StopJackWatch", body);
        }

        /// <summary>Positive control for the lock above: the body it read is
        /// really Shutdown's, not an empty string that would make any
        /// Contains vacuous.</summary>
        [Fact]
        public void Shutdown_BodyLockReadsARealMethod()
        {
            string body = ShutdownBody();
            Assert.Contains("_sinks.Clear()", body);
            Assert.True(body.Length > 200, "the extracted body is too short to be Shutdown");
        }

        private static string ShutdownBody()
        {
            string path = FindRepoFile(Path.Combine(
                "PadForge.App", "Common", "Input", "AudioPassthroughService.cs"));
            string src = File.ReadAllText(path);
            int i = src.IndexOf("public static void Shutdown()", StringComparison.Ordinal);
            Assert.True(i > 0, "Shutdown() not found; this lock needs re-anchoring");
            // The method ends at the first closing brace back at its own indent.
            var m = Regex.Match(src.Substring(i), "\n        \\}");
            Assert.True(m.Success, "could not find the end of Shutdown()");
            return src.Substring(i, m.Index);
        }

        internal static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string p = Path.Combine(dir.FullName, relative);
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(relative);
        }
    }
}
