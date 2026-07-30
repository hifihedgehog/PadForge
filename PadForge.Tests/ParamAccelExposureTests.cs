using System;
using System.IO;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Per-source Acceleration in the row editors, and the engine seam that
    /// makes it real.
    ///
    /// <para>Round 40's follow-up found the premise of "expose it" wrong in a
    /// useful way: ParamAccel was read ONLY by the touchpad and gyro lanes, so
    /// a stick-hosted Steam mouse group's acceleration was stamped by the
    /// translator and then read by nothing. Dead, not merely invisible. The
    /// shared bipolar/unipolar seam applies it now (accel before curve/range,
    /// the touchpad-feel order), and both row editors author it with the same
    /// continuous-family gate Half uses.</para>
    ///
    /// <para>It also LEFT the save capture net: the net's re-stamp would
    /// resurrect a value the user just zeroed, the exact defect the
    /// InvertOutput net was deleted for.</para>
    /// </summary>
    public class ParamAccelExposureTests
    {
        private static CustomInputState AxisAt(int axis, float value)
        {
            var s = new CustomInputState();
            s.Axis[axis] = 32768 + (int)Math.Round(value * 32767);
            return s;
        }

        // ── the engine seam ────────────────────────────────────────────────

        [Fact]
        public void BipolarAxisSeam_AppliesTheAcceleration()
        {
            var plain = new MappingSource { Descriptor = "Axis 2" };
            var accel = new MappingSource { Descriptor = "Axis 2", ParamAccel = 2.0 };
            var state = AxisAt(2, 0.4f);

            float p = SourceCoercion.EvaluateForBipolarAxisTarget(state, plain);
            float a = SourceCoercion.EvaluateForBipolarAxisTarget(state, accel);

            Assert.Equal(0.4f, p, 2);
            // v * (1 + a*|v|) = 0.4 * 1.8 = 0.72: the stick-hosted stamp is
            // no longer dead at the generic tail.
            Assert.Equal(0.72f, a, 2);
        }

        [Fact]
        public void UnipolarSeam_IsTheTwin()
        {
            // A centered axis reads (v+1)/2 on the trigger lane, so 0.4
            // bipolar deflection arrives as 0.7 pull. The first draft of this
            // test asserted 0.4 and failed on its own arithmetic, which was a
            // useful reminder of the lane's semantics, not a defect.
            var plain = new MappingSource { Descriptor = "Axis 2" };
            var accel = new MappingSource { Descriptor = "Axis 2", ParamAccel = 0.5 };
            var state = AxisAt(2, 0.4f);

            float p = SourceCoercion.EvaluateForTriggerTarget(state, plain);
            float a = SourceCoercion.EvaluateForTriggerTarget(state, accel);

            Assert.Equal(0.7f, p, 2);
            // 0.7 * (1 + 0.5 * 0.7) = 0.945
            Assert.Equal(0.945f, a, 3);
        }

        [Fact]
        public void TheClampHolds_FullDeflectionStaysFullScale()
        {
            // ApplyPerSourceAccel clamps symmetrically, so acceleration can
            // never push a deflection past full scale and wrap a target.
            var accel = new MappingSource { Descriptor = "Axis 2", ParamAccel = 5.0 };
            float v = SourceCoercion.EvaluateForBipolarAxisTarget(AxisAt(2, 0.9f), accel);
            Assert.Equal(1.0f, v, 3);

            var neg = SourceCoercion.EvaluateForBipolarAxisTarget(AxisAt(2, -0.9f), accel);
            Assert.Equal(-1.0f, neg, 3);
        }

        [Fact]
        public void ZeroIsExactlyIdentity()
        {
            // The default must not perturb anyone's existing rows: every
            // mapping in the field has ParamAccel 0.
            var src = new MappingSource { Descriptor = "Axis 2" };
            for (float v = -1.0f; v <= 1.0f; v += 0.25f)
            {
                float read = SourceCoercion.EvaluateForBipolarAxisTarget(AxisAt(2, v), src);
                Assert.Equal(v, read, 2);
            }
        }

        // ── the VM round-trip that replaced the net ────────────────────────

        [Theory]
        [InlineData(1.5)]
        [InlineData(0.0)]
        public void SourceItemRoundTripsTheValue(double value)
        {
            var src = new MappingSource { Descriptor = "Axis 2", ParamAccel = value };
            var item = MappingSourceItem.FromDomain(src);
            Assert.Equal(value, item.ParamAccel, 3);
            Assert.Equal(value, item.ToDomain().ParamAccel, 3);
        }

        [Fact]
        public void ZeroingSurvivesTheRoundTrip()
        {
            // The resurrection case: import stamps 1.5, the user drags the
            // slider to 0, and the domain built from the VM must carry 0.
            var item = MappingSourceItem.FromDomain(
                new MappingSource { Descriptor = "Axis 2", ParamAccel = 1.5 });
            item.ParamAccel = 0.0;
            Assert.Equal(0.0, item.ToDomain().ParamAccel, 3);
        }

        [Theory]
        [InlineData(7.0, 5.0)]
        [InlineData(-1.0, 0.0)]
        public void BothEditorsClampToTheTranslatorsRange(double set, double expected)
        {
            var chip = new MappingSourceItem();
            chip.ParamAccel = set;
            Assert.Equal(expected, chip.ParamAccel, 3);

            var row = new MappingItem("A", "KbmMouseX", MappingCategory.Buttons);
            row.ParamAccel = set;
            Assert.Equal(expected, row.ParamAccel, 3);
        }

        [Fact]
        public void ClearAllMappingsResetsIt()
        {
            var vm = new PadForge.ViewModels.PadViewModel(0);
            var m = new MappingItem("A", "KbmMouseX", MappingCategory.Buttons);
            m.ParamAccel = 2.0;
            vm.Mappings.Add(m);

            vm.ClearMappingsCommand.Execute(null);

            Assert.Equal(0.0, m.ParamAccel, 3);
        }

        // ── wiring pinned by inspection ────────────────────────────────────

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        [Fact]
        public void HydrationAndRebuildCarryIt_AndTheNetNoLongerDoes()
        {
            string input = Read("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("mapping.ParamAccel = primary.ParamAccel;", input);

            string settings = Read("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("ParamAccel = mapping.ParamAccel,", settings);
            // The net's re-stamp is gone: with the VM authoring the value, a
            // re-stamp would resurrect what the user just zeroed.
            Assert.DoesNotContain("p.stamp.ParamAccel", settings);
        }

        [Fact]
        public void BothEditorsOfferTheControlGatedOnTheContinuousFamily()
        {
            string xaml = Read("PadForge.App", "Views", "PadPage.xaml");

            // Keyed on the reset buttons, exactly one per row: the binding
            // string appears twice per row (Slider + TextBox), so keying on
            // it found one row's pair and never reached the second editor.
            var resets = new System.Collections.Generic.List<int>();
            for (int i = xaml.IndexOf("ResetParamAccelCommand", StringComparison.Ordinal);
                 i >= 0;
                 i = xaml.IndexOf("ResetParamAccelCommand", i + 1, StringComparison.Ordinal))
            {
                resets.Add(i);
            }
            Assert.Equal(2, resets.Count);

            foreach (int site in resets)
            {
                int start = Math.Max(0, site - 2200);
                string around = xaml.Substring(start, Math.Min(2400, xaml.Length - start));
                Assert.Contains("IsHalfAxisApplicable", around);
                Assert.Contains("Binding ParamAccel,", around);
            }
        }
    }
}
