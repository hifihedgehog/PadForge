using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins for the VR virtual-controller vertical (issue #49): the enum
    /// tail, the VrRawState value semantics, the VrLayout / HMVRButton bit
    /// contract, the PadSetting dictionary lane, the layout translation,
    /// the target-kind classification, and the legacy-to-MappingSet
    /// migration lane (whose absence would wipe VR mappings on save, the
    /// 2026-07-23 MIDI data-loss shape).
    /// </summary>
    public class VrVcTypeTests
    {
        private const string Dev = "22222222-2222-2222-2222-222222222222";

        // ── Persistence pins ──

        [Fact]
        public void EnumValue_IsAppendedAtSix()
        {
            Assert.Equal(6, (int)VirtualControllerType.Vr);
            Assert.Equal(VirtualControllerType.Vr,
                VirtualControllerGroups.InOrder[^1]);
        }

        // ── VrRawState semantics ──

        [Fact]
        public void Merge_ButtonsOrTogether_AxesKeepLargerDeflection()
        {
            var a = new VrRawState();
            a.Left.Buttons = 0b0000_0101;
            a.Left.StickX = -20000;
            a.Left.Trigger = 100;
            a.Right.StickY = 5;

            var b = new VrRawState();
            b.Left.Buttons = 0b1000_0001;
            b.Left.StickX = 15000;
            b.Left.Trigger = 30000;
            b.Right.StickY = -12000;

            a.Merge(in b);

            Assert.Equal(0b1000_0101, a.Left.Buttons);
            Assert.Equal(-20000, a.Left.StickX);   // |−20000| > |15000|
            Assert.Equal(30000, a.Left.Trigger);   // pressed-wins
            Assert.Equal(-12000, a.Right.StickY);
        }

        /// <summary>A fully deflected axis is exactly short.MinValue, and
        /// Math.Abs's short overload THROWS there. The merge used it, so a
        /// two-device VR slot threw OverflowException out of Step 4 on
        /// every poll the moment any source drove a stick fully negative,
        /// and the catch cleared the slot's whole combined output ~1000x/s
        /// (owner report 2026-08-08, named from the DIAG ring). Both the
        /// no-throw and the magnitude verdict are pinned: short.MinValue
        /// has the LARGEST magnitude of any short, so it must win.</summary>
        [Theory]
        [InlineData(short.MinValue, (short)0)]
        [InlineData((short)0, short.MinValue)]
        [InlineData(short.MinValue, short.MaxValue)]
        [InlineData(short.MinValue, short.MinValue)]
        public void Merge_FullNegativeDeflection_DoesNotOverflow(short ax, short bx)
        {
            var a = new VrRawState();
            a.Left.StickX = ax;
            a.Left.StickY = ax;
            a.Right.StickX = bx;

            var b = new VrRawState();
            b.Left.StickX = bx;
            b.Left.StickY = bx;
            b.Right.StickX = ax;

            a.Merge(in b);   // threw OverflowException before the int widening

            short expected = Math.Abs((int)ax) >= Math.Abs((int)bx) ? ax : bx;
            Assert.Equal(expected, a.Left.StickX);
            Assert.Equal(expected, a.Left.StickY);
        }

        /// <summary>The trigger fills like a gas tank, so the ANALOG value
        /// must win whenever there is one.
        ///
        /// <para>Two defects meet here. A click-only source (the analog row
        /// unmapped) passed pull = 0 and painted the press at the same 0.4
        /// the preview uses for hover, so a real press looked like the
        /// pointer resting on the control. Pinning to full whenever the
        /// click bit was set fixed that and broke the fill outright: automap
        /// binds VrLTrigger AND VrLTriggerClick to the same Axis 2, so every
        /// pull past the click threshold slammed the tank to full and the
        /// trigger was on/off again. The click is a FALLBACK, not an
        /// override.</para></summary>
        [Theory]
        [InlineData((byte)0x20, (byte)0x20, (short)0, 1.0)]      // click alone, no analog: full
        [InlineData((byte)0x40, (byte)0x40, (short)0, 1.0)]      // grip click alone: full
        [InlineData((byte)0x00, (byte)0x20, (short)0, 0.0)]      // nothing at all
        [InlineData((byte)0x00, (byte)0x20, (short)32767, 1.0)]  // full analog, no click
        [InlineData((byte)0x00, (byte)0x40, (short)16384, 0.5)]  // half analog, no click
        [InlineData((byte)0x20, (byte)0x40, (short)0, 0.0)]      // a DIFFERENT click bit does not pin
        // The regression cases: automap drives BOTH rows off one axis, so
        // the click bit is set for every one of these and the fill must
        // still track the pull rather than jumping to full.
        [InlineData((byte)0x20, (byte)0x20, (short)16384, 0.5)]
        [InlineData((byte)0x20, (byte)0x20, (short)8192, 0.25)]
        [InlineData((byte)0x40, (byte)0x40, (short)24575, 0.75)]
        public void PullFor_AnalogWinsOverClick(byte buttons, byte clickBit, short analog, double expected)
        {
            Assert.Equal(expected, PadForge.Views.VRPreviewView.PullFor(buttons, clickBit, analog), 3);
        }

        [Fact]
        public void ButtonKeys_IndexTheHmvrButtonBitPositions()
        {
            // HMVRButton (HIDMaestro v1.6.0, reflection-dumped ground
            // truth): System=1, A=2, ATouch=4, B=8, BTouch=16,
            // TriggerClick=32, GripClick=64, StickClick=128. Index i in the
            // key arrays must be bit 1<<i, because the wrapper casts the
            // byte straight across.
            Assert.Equal(8, VrLayout.LeftButtonKeys.Length);
            Assert.Equal(8, VrLayout.RightButtonKeys.Length);
            Assert.Equal("VrLSystem", VrLayout.LeftButtonKeys[0]);
            Assert.Equal("VrLA", VrLayout.LeftButtonKeys[1]);
            Assert.Equal("VrLATouch", VrLayout.LeftButtonKeys[2]);
            Assert.Equal("VrLB", VrLayout.LeftButtonKeys[3]);
            Assert.Equal("VrLBTouch", VrLayout.LeftButtonKeys[4]);
            Assert.Equal("VrLTriggerClick", VrLayout.LeftButtonKeys[5]);
            Assert.Equal("VrLGripClick", VrLayout.LeftButtonKeys[6]);
            Assert.Equal("VrLStickClick", VrLayout.LeftButtonKeys[7]);
            Assert.Equal("VrRSystem", VrLayout.RightButtonKeys[0]);
            Assert.Equal("VrRStickClick", VrLayout.RightButtonKeys[7]);
        }

        // ── PadSetting dictionary lane ──

        [Fact]
        public void VrMappingLane_RoundTripsThroughFlush()
        {
            var ps = new PadSetting();
            ps.SetVrMapping("VrLA", "Button 2");
            ps.SetVrMapping("VrRStickX", "Axis 3");
            ps.FlushVrMappings();

            Assert.Equal("Button 2", ps.GetVrMapping("VrLA"));
            Assert.Equal("Axis 3", ps.GetVrMapping("VrRStickX"));
            Assert.NotNull(ps.VrMappingEntries);
            Assert.Equal(2, ps.VrMappingEntries.Length);
        }

        // ── Layout translation ──

        [Fact]
        public void MappingTranslation_RoundTripsEveryVrName()
        {
            foreach (var key in VrLayout.LeftButtonKeys)
                AssertRoundTrip(key, ControlCategory.Button);
            foreach (var key in VrLayout.RightButtonKeys)
                AssertRoundTrip(key, ControlCategory.Button);
            foreach (var key in new[]
            {
                VrLayout.LStickX, VrLayout.LStickY, VrLayout.LTrigger, VrLayout.LGrip,
                VrLayout.RStickX, VrLayout.RStickY, VrLayout.RTrigger, VrLayout.RGrip,
            })
                AssertRoundTrip(key, ControlCategory.Axis);
        }

        private static void AssertRoundTrip(string name, ControlCategory cat)
        {
            var slot = MappingTranslation.GetPosition(name, VirtualControllerType.Vr, isExtended: false);
            Assert.NotNull(slot);
            Assert.Equal(cat, slot.Category);
            Assert.Equal(name, MappingTranslation.GetPropertyName(
                slot, VirtualControllerType.Vr, isExtended: false));
        }

        // ── Target-kind classification ──

        [Theory]
        [InlineData("VrLStickX", TargetKind.BipolarAxis)]
        [InlineData("VrRStickY", TargetKind.BipolarAxis)]
        [InlineData("VrLTrigger", TargetKind.Trigger)]
        [InlineData("VrRGrip", TargetKind.Trigger)]
        [InlineData("VrLTriggerClick", TargetKind.Button)]
        [InlineData("VrRGripClick", TargetKind.Button)]
        [InlineData("VrLStickClick", TargetKind.Button)]
        [InlineData("VrRSystem", TargetKind.Button)]
        [InlineData("VrLATouch", TargetKind.Button)]
        public void TargetKind_ClassifiesVrTargets(string target, TargetKind expected)
        {
            Assert.Equal(expected, TargetKindResolver.Resolve(target));
        }

        // ── Migrator lane ──

        [Fact]
        public void VrAutomapSurvivesMigration()
        {
            var ps = new PadSetting();
            ps.SetVrMapping("VrLA", "Button 2");
            ps.SetVrMapping("VrRTriggerClick", "Axis 5");
            ps.SetVrMapping("VrLStickX", "Axis 0");
            ps.SetVrMapping("VrLStickXNeg", "Axis 1");
            ps.SetVrMapping("VrRGrip", "Button 5");
            ps.FlushVrMappings();

            var ms = MappingSetMigrator.BuildFromLegacy(0, new[]
            {
                (DeviceGuid: Dev, PadSetting: ps, IsGamepadEligible: true),
            });

            var btn = Assert.Single(ms.Rows, r => r.Target == "VrLA");
            Assert.Equal("Button 2", Assert.Single(btn.Sources).Descriptor);

            var click = Assert.Single(ms.Rows, r => r.Target == "VrRTriggerClick");
            Assert.Equal("Axis 5", Assert.Single(click.Sources).Descriptor);

            // Neg leg folds into the bipolar stick row.
            var stick = Assert.Single(ms.Rows, r => r.Target == "VrLStickX");
            Assert.Equal(2, stick.Sources.Count);

            var grip = Assert.Single(ms.Rows, r => r.Target == "VrRGrip");
            Assert.Equal("Button 5", Assert.Single(grip.Sources).Descriptor);
        }
    }
}
