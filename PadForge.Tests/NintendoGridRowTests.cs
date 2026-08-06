using System.Collections.Generic;
using System.Linq;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the rows InitializeRawSurfaceMappings emits for a Nintendo slot.
    ///
    /// The arrangement is fixed (owner direction 2026-07-19: analogous
    /// controls in analogous positions). The INDICES behind it are not: they
    /// are wire positions, and the two Nintendo wires agree only on the face
    /// diamond. The branch used to hardcode the original Pro Controller's
    /// indices for both, which on a Switch 2 Pro printed "Minus"/"Plus" over
    /// its D-pad Down/Right, "Home"/"Capture" over L/ZL, pointed the whole
    /// D-pad group at a hat the descriptor does not declare, and stopped at
    /// index 13 so Minus, LS, Home, Capture, GR, GL and C had no row at all.
    /// </summary>
    public class NintendoGridRowTests
    {
        private const string S1 = "switch-pro";
        private const string S2 = "switch2-pro-controller";

        private static List<(string Target, MappingCategory Cat)> Rows(string profileId)
        {
            var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Nintendo };
            vm.ProfileId = profileId;
            return vm.Mappings
                .Select(m => (m.TargetSettingName, m.Category))
                .ToList();
        }

        /// <summary>Every button the Switch 2 Pro's descriptor declares gets
        /// exactly one row. USAGE_MIN 1 / USAGE_MAX 21 / REPORT_COUNT 21 on
        /// the Button page, so that is 0..20 with nothing missing and nothing
        /// duplicated.</summary>
        [Fact]
        public void Switch2Pro_EmitsEveryWireButtonExactlyOnce()
        {
            var targets = Rows(S2).Select(r => r.Target).ToList();
            for (int i = 0; i < 21; i++)
                Assert.Equal(1, targets.Count(t => t == $"RawBtn{i}"));
            Assert.DoesNotContain(targets, t => t == "RawBtn21");
        }

        /// <summary>The original stays at its own 14, and none of the Switch 2
        /// indices leak into it.</summary>
        [Fact]
        public void SwitchPro_EmitsEveryWireButtonExactlyOnce()
        {
            var targets = Rows(S1).Select(r => r.Target).ToList();
            for (int i = 0; i < 14; i++)
                Assert.Equal(1, targets.Count(t => t == $"RawBtn{i}"));
            for (int i = 14; i < 21; i++)
                Assert.DoesNotContain(targets, t => t == $"RawBtn{i}");
        }

        /// <summary>The four controls the owner named as carrying over are
        /// present on the Switch 2 Pro, at their real wire indices.</summary>
        [Theory]
        [InlineData(14)]   // Minus
        [InlineData(6)]    // Plus
        [InlineData(16)]   // Home
        [InlineData(17)]   // Capture
        public void Switch2Pro_CarryOverSystemButtonsHaveRows(int wireIndex)
        {
            Assert.Contains(Rows(S2), r => r.Target == $"RawBtn{wireIndex}"
                                        && r.Cat == MappingCategory.Buttons);
        }

        /// <summary>And the three new ones.</summary>
        [Theory]
        [InlineData(18)]   // GR
        [InlineData(19)]   // GL
        [InlineData(20)]   // C
        public void Switch2Pro_NewButtonsHaveRows(int wireIndex)
        {
            Assert.Contains(Rows(S2), r => r.Target == $"RawBtn{wireIndex}"
                                        && r.Cat == MappingCategory.Buttons);
        }

        /// <summary>The D-pad group points at the four real buttons and NOT
        /// at a hat, and those four never also appear as loose button rows.
        /// Loose rows are what produced a stray "Down" and "Right" in the
        /// button list with no D-pad connection.</summary>
        [Fact]
        public void Switch2Pro_DPadGroupUsesButtonsAndOwnsThemExclusively()
        {
            var rows = Rows(S2);
            var dpad = rows.Where(r => r.Cat == MappingCategory.DPad)
                           .Select(r => r.Target).ToList();
            Assert.Equal(4, dpad.Count);
            Assert.Equal(new[] { "RawBtn11", "RawBtn8", "RawBtn10", "RawBtn9" }, dpad);
            Assert.DoesNotContain(rows, r => r.Target.StartsWith("RawPov"));

            foreach (var t in dpad)
                Assert.DoesNotContain(rows, r => r.Target == t
                                              && r.Cat == MappingCategory.Buttons);
        }

        /// <summary>The original keeps its hat-driven D-pad group.</summary>
        [Fact]
        public void SwitchPro_DPadGroupStaysOnTheHat()
        {
            var dpad = Rows(S1).Where(r => r.Cat == MappingCategory.DPad)
                               .Select(r => r.Target).ToList();
            Assert.Equal(new[] { "RawPov0Up", "RawPov0Down", "RawPov0Left", "RawPov0Right" }, dpad);
        }

        /// <summary>Both generations put the same controls in the same order,
        /// which is the whole point of the arrangement. Compare by LABEL, so
        /// a wire-index mistake on either side shows up as a mismatch.</summary>
        [Fact]
        public void BothGenerations_ShareTheSameRowOrderForSharedControls()
        {
            static List<string> Labels(string p) =>
                new PadViewModel(0) { OutputType = VirtualControllerType.Nintendo, ProfileId = p }
                    .Mappings.Where(m => m.Category == MappingCategory.Buttons)
                    .Select(m => m.TargetLabel).ToList();

            var s1 = Labels(S1);
            var s2 = Labels(S2).Where(l => l != "GL" && l != "GR" && l != "C").ToList();
            Assert.Equal(s1, s2);
        }
    }
}
