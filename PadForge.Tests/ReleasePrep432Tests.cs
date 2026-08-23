using System;
using PadForge.Common.Input;
using PadForge.Engine.Common.Mapping;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Locks for the three code defects the 4.3.2 documentation sweep
    /// surfaced while verifying pages against HEAD. Each was a user-facing
    /// control offered where its effect never fired, which is the shape the
    /// docs lens is good at because the page claims the control works.</summary>
    [Collection("SettingsManagerStatics")]
    public class ReleasePrep432Tests
    {
        // ── Release to Aim on the trigger-routing activator ────────────────

        /// <summary>The picker offered four modes and the settle implemented
        /// three. ReleaseToEngage fell through to Hold, so choosing it did
        /// exactly what Hold did. The gyro engage's own settle already had the
        /// rule, and this mirrors it: engaged while the button is NOT held.</summary>
        [Theory]
        [InlineData("Hold", true, true)]
        [InlineData("Hold", false, false)]
        [InlineData("ReleaseToEngage", true, false)]
        [InlineData("ReleaseToEngage", false, true)]
        [InlineData("AlwaysOn", true, true)]
        [InlineData("AlwaysOn", false, true)]
        public void RouteActivator_ModesDiverge(string mode, bool held, bool expectedEngaged)
        {
            var saved = SourceCoercion.ButtonHeldProvider;
            try
            {
                SourceCoercion.ButtonHeldProvider = (_, _, _) => held;
                var prev = new bool[16];
                bool engaged = InputManager.SettleRouteActivator(0, "Button 3", "", mode, prev, false, out _);
                Assert.Equal(expectedEngaged, engaged);
            }
            finally { SourceCoercion.ButtonHeldProvider = saved; }
        }

        /// <summary>Positive control for the theory: ReleaseToEngage and Hold
        /// must come out OPPOSITE on the same input, or the new branch is a
        /// duplicate of the old and the option is still inert.</summary>
        [Fact]
        public void RouteActivator_ReleaseToEngage_IsHoldInverted()
        {
            var saved = SourceCoercion.ButtonHeldProvider;
            try
            {
                foreach (bool held in new[] { true, false })
                {
                    SourceCoercion.ButtonHeldProvider = (_, _, _) => held;
                    bool hold = InputManager.SettleRouteActivator(0, "Button 3", "", "Hold", new bool[16], false, out _);
                    bool rte = InputManager.SettleRouteActivator(0, "Button 3", "", "ReleaseToEngage", new bool[16], false, out _);
                    Assert.NotEqual(hold, rte);
                }
            }
            finally { SourceCoercion.ButtonHeldProvider = saved; }
        }

        /// <summary>An empty activator is always on in every non-Toggle mode,
        /// ReleaseToEngage included: a button that does not exist is never
        /// held, so "engaged while not held" is always true.</summary>
        [Fact]
        public void RouteActivator_EmptyDescriptor_AlwaysOn_InReleaseToEngage()
        {
            Assert.True(InputManager.SettleRouteActivator(0, "", "", "ReleaseToEngage", new bool[16], false, out _));
            Assert.True(InputManager.SettleRouteActivator(0, "", "", "Hold", new bool[16], false, out _));
        }

        // ── Reset All on the Sound Output card covers the whole card ───────

        /// <summary>Reset All skipped eight rows across three features that
        /// landed after it was written: persona haptics, the Bluetooth audio
        /// buffer, and the entire DSP chain. The rule is that a reset-all
        /// resets the ENTIRE row surface. Author every one of those rows away
        /// from default, reset, and assert each came back.</summary>
        [Fact]
        public void ResetSoundOutputAll_ResetsTheWholeCard()
        {
            var vm = new PadViewModel(0);
            var cfg = new DeviceSlotConfig
            {
                AudioPersonaHapticsEnabled = true,
                AudioPersonaHapticsGain = 250,
                Ds5AudioBufferLength = 200,
                AudioCrossfeedLevel = 7,
                AudioCrossfeedCutHz = 1200,
                AudioCrossfeedFeedDb = 9.5d,
                AudioEqEnabled = true,
                AudioEqPreampDb = -6d,
                AudioEqBands = "PK:1050:-3.5:1.2:1|LSC:105:5.5:0.7:1",
                AudioLimiterEnabled = false,
                AudioLimiterCeiling = 60,
                HeadphoneVolume = 30,
            };
            vm.DeviceConfig = cfg;
            Assert.Equal(2, vm.EqBands.Count);   // positive control: the grid holds the bands

            vm.ResetSoundOutputAllCommand.Execute(null);

            Assert.False(cfg.AudioPersonaHapticsEnabled);
            Assert.Equal(100, cfg.AudioPersonaHapticsGain);
            Assert.Equal(AudioPassthroughService.Ds5AudioBufferLengthDefault, cfg.Ds5AudioBufferLength);
            Assert.Equal(0, cfg.AudioCrossfeedLevel);
            Assert.Equal(700, cfg.AudioCrossfeedCutHz);
            Assert.Equal(4.5d, cfg.AudioCrossfeedFeedDb);
            Assert.False(cfg.AudioEqEnabled);
            Assert.Equal(0d, cfg.AudioEqPreampDb);
            Assert.Equal(string.Empty, cfg.AudioEqBands);
            Assert.Empty(vm.EqBands);
            Assert.True(cfg.AudioLimiterEnabled);
            Assert.Equal(98, cfg.AudioLimiterCeiling);
            Assert.Equal(100, cfg.HeadphoneVolume);
        }

        // ── the DSP rows show only where the chain runs ────────────────────

        /// <summary>The DSP chain runs on AudioPassthroughService sinks, which
        /// exist for the Sony pads only. The rows were gated on "has a
        /// speaker", which a Wii Remote and every haptic-tone pad also
        /// satisfy, so three cards showed there and did nothing. The gate is
        /// a new property with the two non-Sony arms removed. Source-text
        /// lock on the XAML, since the visibility binding has no in-process
        /// seam, plus a positive control that the OLD gate is still used by
        /// the cards that genuinely apply to every speaker.</summary>
        [Fact]
        public void DspRows_AreGatedOnTheSonyChainNotOnHavingASpeaker()
        {
            string xaml = System.IO.File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                System.IO.Path.Combine("PadForge.App", "Views", "PadPage.xaml")));
            int cf = xaml.IndexOf("<!-- Crossfeed -->", StringComparison.Ordinal);
            Assert.True(cf > 0, "crossfeed block not found; re-anchor this lock");
            // The StackPanel that wraps the chain is the nearest one above.
            int panel = xaml.LastIndexOf("<StackPanel Visibility=", cf, StringComparison.Ordinal);
            string open = xaml.Substring(panel, cf - panel);
            Assert.Contains("SelectedDeviceHasDspChain", open);
            Assert.DoesNotContain("SelectedDeviceHasSpeaker", open);
            // Positive control: the speaker gate is still real elsewhere.
            Assert.Contains("SelectedDeviceHasSpeaker", xaml);
        }
    }
}
