using System;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Steam config assumes ONE controller, so its tuning is per physical
    /// input: "the right stick uses this deadzone shape", "gyro engages on
    /// this button". PadForge already owns settings for those, with cards.
    /// The import could not write them because it runs before a device is
    /// assigned and they are keyed by device guid, so it parked them on the
    /// slot and the engine consulted the parking spot at runtime.
    ///
    /// <para>That made the parking spot a second, invisible settings system.
    /// Worst case: the stick deadzone shape read returned the stamp
    /// unconditionally on an Authoritative slot, so the user's own Dead Zone
    /// Shape control was overridden and editing it did nothing.</para>
    ///
    /// <para>The stamps are folded into the device's own settings at
    /// assignment now, and cleared.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopTuningApplierTests : IDisposable
    {
        private const int Slot = 0;

        // SlotMappingSets is a STATIC. Replacing it without putting it back
        // leaks into every other test that reads it: doing so reddened five
        // ShortPressAndMacroLayerTests with nothing to do with this applier.
        private readonly MappingSet[] _priorSets = SettingsManager.SlotMappingSets;

        public void Dispose() => SettingsManager.SlotMappingSets = _priorSets;

        private static MappingSet ArrangeSlot(Action<MappingSet> tune)
        {
            var sets = new MappingSet[InputManager.MaxPads];
            var set = new MappingSet { Authoritative = true };
            tune(set);
            sets[Slot] = set;
            SettingsManager.SlotMappingSets = sets;
            return set;
        }

        [Fact]
        public void EveryDeviceAssignmentPathAppliesTheStamps()
        {
            // Grep-as-a-test, and it exists because the first cut wired the
            // applier into ONE of the two assignment paths. The runtime
            // overlays it replaced applied on every path by construction, so
            // an entry point that assigns a device without folding the stamps
            // silently drops the imported tuning for whichever path the user
            // takes. A third path added later fails here on arrival.
            var src = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Services", "DeviceService.cs"));

            // Split into member bodies on the indentation marker. Plain
            // string splitting on purpose: no regex, no escape literals.
            var methods = src.Split(
                new[] { "        private ", "        public ", "        internal " },
                StringSplitOptions.None);

            var assigners = methods
                .Where(m => m.Contains("SettingsManager.AssignDeviceToSlot("))
                .ToList();
            Assert.True(assigners.Count >= 2,
                "expected at least 2 assignment paths, found " + assigners.Count);

            var unguarded = assigners
                .Where(m => !m.Contains("WorkshopTuningApplier.ApplyToAssignedDevice"))
                .Select(m => new string(m.TakeWhile(c => c != '(').ToArray()).Trim())
                .ToList();

            Assert.True(unguarded.Count == 0,
                "these assignment paths never fold the Workshop stamps into the "
                + "device's own settings: " + string.Join(" | ", unguarded));
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void StickDeadZoneShapeLandsInTheDevicesOwnSetting()
        {
            var set = ArrangeSlot(s =>
            {
                s.WorkshopLeftStickDeadZoneShape = "1";
                s.WorkshopRightStickDeadZoneShape = "0";
            });
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("1", ps.LeftThumbDeadZoneShape);
            Assert.Equal("0", ps.RightThumbDeadZoneShape);
            // Consumed, so it cannot re-apply over a later user edit.
            Assert.Equal("", set.WorkshopLeftStickDeadZoneShape);
            Assert.Equal("", set.WorkshopRightStickDeadZoneShape);
        }

        [Fact]
        public void AUserChoiceIsNeverOverwritten()
        {
            // Re-assigning a device must not silently discard tuning the user
            // set by hand.
            var set = ArrangeSlot(s => s.WorkshopLeftStickDeadZoneShape = "1");
            var ps = new PadSetting { LeftThumbDeadZoneShape = "0" };

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("0", ps.LeftThumbDeadZoneShape);
            Assert.Equal("", set.WorkshopLeftStickDeadZoneShape);
        }

        [Fact]
        public void GyroEngageButtonLandsInTheDevicesOwnSetting()
        {
            var set = ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageToggle = false;
                s.WorkshopGyroEngageInvert = false;
            });
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("Gamepad LeftShoulder", ps.GyroAimEngageButton);
            Assert.Equal("Hold", ps.GyroAimEngageMode);
            Assert.Equal("", set.WorkshopGyroEngageDescriptor);
        }

        [Fact]
        public void SteamsInvertedEngageBecomesTheReleaseToEngageMode()
        {
            // gyro_button_invert means the gyro fires while the button is NOT
            // held. It used to ride a hidden per-slot flag no card could
            // reach; removing the overlay without this mapping would have
            // silently dropped the behavior, which is what a CS0649 "never
            // assigned" warning caught mid-change.
            ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageInvert = true;
            });
            var ps = new PadSetting();

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("ReleaseToEngage", ps.GyroAimEngageMode);
        }

        [Fact]
        public void ToggleWinsOverInvert()
        {
            ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageToggle = true;
                s.WorkshopGyroEngageInvert = true;
            });
            var ps = new PadSetting();

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("Toggle", ps.GyroAimEngageMode);
        }

        [Fact]
        public void PositiveControl_NoStampsMeansNoChange()
        {
            // Without this every assertion above could pass on an applier that
            // wrote unconditionally.
            ArrangeSlot(_ => { });
            var ps = new PadSetting();
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            Assert.Equal("2", ps.LeftThumbDeadZoneShape);
            Assert.Equal("", ps.GyroAimEngageButton);
        }

        [Fact]
        public void ApplyingTwiceIsAnoop()
        {
            // The stamp is cleared on the first pass, so a second assignment
            // cannot resurrect it over a value the user has since changed.
            ArrangeSlot(s => s.WorkshopLeftStickDeadZoneShape = "1");
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            ps.LeftThumbDeadZoneShape = "0";           // user changes their mind
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            Assert.Equal("0", ps.LeftThumbDeadZoneShape);
        }

        [Fact]
        public void NoSlotMappingSetIsSafe()
        {
            SettingsManager.SlotMappingSets = null;
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, new PadSetting()));
        }

        // ── per-source response shaping ───────────────────────────────────
        //
        // Curve exponent, outer range and anti-deadzone were stamped on the
        // ROWS by the import and read by the engine at row read, while the
        // device cards for those same three knobs sat at their defaults. So
        // the values were live and invisible: nothing on screen showed them
        // and editing the card fought a stamp the user could not see.
        //
        // They move here now. Move, not copy: both layers are live, so a
        // stamp left in place beside a populated card applies the curve
        // TWICE.

        private static MappingSet ArrangeRow(string target, Action<MappingSource> tune)
        {
            return ArrangeSlot(s =>
            {
                var src = new MappingSource { Descriptor = "Gamepad LeftStickX" };
                tune(src);
                s.Rows.Add(new MappingRow { Target = target, Sources = { src } });
            });
        }

        [Fact]
        public void CurveExponentBecomesControlPointsOnTheCard()
        {
            var set = ArrangeRow("LeftThumbAxisX", s => s.ParamCurveExponent = 2.0);
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            // Control points, NOT the legacy single number. That form is read
            // on a 4^(-v/100) scale, so a bare "2" would land at very nearly
            // linear and quietly discard the curve.
            Assert.Contains(";", ps.LeftThumbSensitivityCurveX);
            Assert.False(PadForge.Common.CurveLut.IsLinear(ps.LeftThumbSensitivityCurveX));

            // And the shape is really x^2: half deflection gives a quarter.
            var lut = PadForge.Common.CurveLut.GetOrBuild(ps.LeftThumbSensitivityCurveX);
            Assert.Equal(0.25, PadForge.Common.CurveLut.Lookup(lut, 0.5), 2);

            // Moved, so the engine's row read no longer applies it.
            Assert.Equal(0.0, set.Rows[0].Sources[0].ParamCurveExponent);
        }

        [Fact]
        public void AntiDeadZoneAndOuterRangeBecomePercents()
        {
            var set = ArrangeRow("LeftThumbAxisX", s =>
            {
                s.ParamAntiDeadzone = 0.15;
                s.ParamRangeOuter = 0.80;
            });
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("15", ps.LeftThumbAntiDeadZoneX);
            Assert.Equal("80", ps.LeftThumbMaxRangeX);
            Assert.Equal(0.0, set.Rows[0].Sources[0].ParamAntiDeadzone);
            Assert.Equal(0.0, set.Rows[0].Sources[0].ParamRangeOuter);
        }

        [Fact]
        public void OuterRangeStaysPutWhenAStickGeometryOwnsIt()
        {
            // THE trap in this fold. With a shape stamped, the engine spends
            // ParamRangeOuter as the deadzone's outer radius inside
            // ApplyStickDeadZoneShape and suppresses its own scalar tail (its
            // hasOuter requires shape == 0). Folding the radius into MaxRange
            // would strip the geometry of the bound it still needs, so a
            // Circle deadzone import would lose its outer edge.
            var set = ArrangeRow("LeftThumbAxisX", s =>
            {
                s.ParamStickDeadZoneShape = 2;      // Steam Circle
                s.ParamStickDeadZoneInner = 0.10;
                s.ParamRangeOuter = 0.80;
            });
            var ps = new PadSetting();

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("100", ps.LeftThumbMaxRangeX);                     // card untouched
            Assert.Equal(0.80, set.Rows[0].Sources[0].ParamRangeOuter);     // radius preserved
            Assert.Equal(2, set.Rows[0].Sources[0].ParamStickDeadZoneShape);
        }

        [Fact]
        public void TheTargetPicksTheCardNotTheSourceDescriptor()
        {
            // The card applies to the OUTPUT axis, so a config that redirects
            // the left stick onto the right pair (Steam's output_joystick)
            // must land on the RIGHT stick's card. Keying off the source
            // descriptor would tune the wrong stick.
            var set = ArrangeSlot(s => s.Rows.Add(new MappingRow
            {
                Target = "RightThumbAxisY",
                Sources = { new MappingSource
                {
                    Descriptor = "Gamepad LeftStickX",   // left INPUT
                    ParamAntiDeadzone = 0.20,
                } },
            }));
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("20", ps.RightThumbAntiDeadZoneY);
            Assert.Equal("0", ps.LeftThumbAntiDeadZoneX);
        }

        [Theory]
        [InlineData("LeftTrigger")]
        [InlineData("RightTrigger")]
        public void TriggerTargetsFoldToTriggerCards(string target)
        {
            // The corpus stamps these on trigger hosts as well as sticks, and
            // triggers own the same three fields.
            ArrangeRow(target, s => s.ParamAntiDeadzone = 0.25);
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("25", target == "LeftTrigger"
                ? ps.LeftTriggerAntiDeadZone : ps.RightTriggerAntiDeadZone);
        }

        [Fact]
        public void AValueTheUserAlreadyChoseSurvives()
        {
            var set = ArrangeRow("LeftThumbAxisX", s => s.ParamAntiDeadzone = 0.15);
            var ps = new PadSetting { LeftThumbAntiDeadZoneX = "5" };   // user's own

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("5", ps.LeftThumbAntiDeadZoneX);
            // Still cleared: a stamp offered once has done its job, and
            // leaving it would re-apply after a deliberate change back.
            Assert.Equal(0.0, set.Rows[0].Sources[0].ParamAntiDeadzone);
        }

        [Fact]
        public void ACurveTheUserAlreadyDrewSurvives()
        {
            // The curve needs its own case: it is guarded by IsLinear rather
            // than a percent compare, and mutation testing found that guard
            // unlocked while the anti-deadzone one beside it was covered.
            var set = ArrangeRow("LeftThumbAxisX", s => s.ParamCurveExponent = 2.0);
            var ps = new PadSetting { LeftThumbSensitivityCurveX = "0,0;0.5,0.9;1,1" };

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("0,0;0.5,0.9;1,1", ps.LeftThumbSensitivityCurveX);
            Assert.Equal(0.0, set.Rows[0].Sources[0].ParamCurveExponent);
        }

        [Fact]
        public void AnUnshapedTargetIsLeftAlone()
        {
            // A button target has no curve card, and the params should not be
            // silently eaten looking for one.
            var set = ArrangeRow("ButtonA", s => s.ParamAntiDeadzone = 0.15);
            var ps = new PadSetting();

            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            Assert.Equal(0.15, set.Rows[0].Sources[0].ParamAntiDeadzone);
        }

        [Fact]
        public void PositiveControl_ADefaultPadSettingReallyReadsAsUnset()
        {
            // Without this, every "the user had not chosen" guard above could
            // be passing because the guard never fires at all.
            var ps = new PadSetting();
            Assert.True(PadForge.Common.CurveLut.IsLinear(ps.LeftThumbSensitivityCurveX));
            Assert.Equal("100", ps.LeftThumbMaxRangeX);
            Assert.Equal("0", ps.LeftThumbAntiDeadZoneX);
        }
    }
}
