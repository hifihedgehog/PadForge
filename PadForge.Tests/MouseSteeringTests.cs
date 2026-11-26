using System;
using System.Diagnostics;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.Steering;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #600 items 34 to 37 and 41: the scroll wheels and a relative axis held
    /// as a wheel's position, the four Steer modes on every mouse source,
    /// Return Time 0, Mouse Speed's own settings and its scale at every
    /// rate, Mouse Position on its own monitor with its anchor and
    /// smoothing, and From Telemetry's car shape at every rate.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MouseSteeringTests : IDisposable
    {
        private const string Target = "LeftThumbAxisX";
        private const string MouseGuid = "6b29fc40-ca47-1067-b31d-00dd010662da";

        private readonly Func<string, bool> _savedIsMouse = SourceCoercion.IsMouseDeviceProvider;
        private readonly Func<(double, double, double, double, double)?> _savedPoint = SourceCoercion.MouseCursorPointProvider;
        private readonly Func<(float, float)> _savedCursor = SourceCoercion.MouseCursorProvider;
        private readonly Func<(double speed, double grip, bool lockAtSpeed)> _savedRoad = SourceCoercion.MouseTravelRoadProvider;

        public MouseSteeringTests()
        {
            SourceCoercion.IsMouseDeviceProvider = g => string.Equals(g, MouseGuid, StringComparison.OrdinalIgnoreCase);
            SourceCoercion.MouseTravelRoadProvider = null;
        }

        public void Dispose()
        {
            SourceCoercion.IsMouseDeviceProvider = _savedIsMouse;
            SourceCoercion.MouseCursorPointProvider = _savedPoint;
            SourceCoercion.MouseCursorProvider = _savedCursor;
            SourceCoercion.MouseTravelRoadProvider = _savedRoad;
        }

        private static MappingSource Source(string descriptor, string device = "") => new()
        {
            Kind = "Direct",
            Descriptor = descriptor,
            DeviceGuid = device,
        };

        private static CustomInputState Mouse(int dx = 0, int dy = 0, int wheel = 0, int wheelH = 0, bool steer = false)
        {
            var s = new CustomInputState { MouseRawDX = dx, MouseRawDY = dy, MouseRawWheel = wheel, MouseRawWheelH = wheelH };
            s.Buttons[2] = steer;
            return s;
        }

        /// <summary>One pass of a row as a stick's axis, through the
        /// evaluator a mapping row reads with.</summary>
        private static double Row(SourceKindRuntime rt, MappingSource src, CustomInputState state, double dt, string device = null)
        {
            rt.FrameSeq++;
            return SourceEvaluator.EvaluateForBipolarAxisTarget(state, src, 0, Target, 0, rt, dt, device);
        }

        // ── Item 34: the wheels ──

        [Fact]
        public void WheelTravel_HoldsTheWheelsUnits_AndSidewaysTheSidewaysWheels()
        {
            var rt = new SourceKindRuntime();
            var wheel = Source(SourceCoercion.WheelTravelDescriptor);
            Assert.Equal(0.06, Row(rt, wheel, Mouse(wheel: 120), 0.001), 6);   // a notch is 120 units
            Assert.Equal(0.06, Row(rt, wheel, Mouse(dx: 500, wheelH: 120), 0.001), 6); // nothing else moves it
            var sideways = Source(SourceCoercion.WheelTravelSidewaysDescriptor);
            var rt2 = new SourceKindRuntime();
            Assert.Equal(-0.06, Row(rt2, sideways, Mouse(wheelH: -120, wheel: 240), 0.001), 6);
            Assert.True(SourceEvaluator.IsMouseTravel(wheel));
            Assert.True(SourceEvaluator.IsMouseTravel(sideways));
        }

        [Fact]
        public void WheelTravel_KeepsAFineScrollingWheelsSmallStepsWhole()
        {
            // A wheel with a Resolution Multiplier of 8 sends 15 units a
            // step: eight steps are a notch, to the unit.
            var rt = new SourceKindRuntime();
            var wheel = Source(SourceCoercion.WheelTravelDescriptor);
            double at = 0;
            for (int i = 0; i < 8; i++) at = Row(rt, wheel, Mouse(wheel: 15), 0.001);
            // The row reads it at a float's precision.
            Assert.Equal(120.0 / MappingSource.DefaultTravelCounts, at, 6);
        }

        [Fact]
        public void RawInput_KeepsTheSidewaysWheelApart_AndATapThatNothingConsumes()
        {
            var handle = new IntPtr(0x0A600601);
            const ushort wheel = 0x0400, hwheel = 0x0800;
            var before = RawInputListener.MouseTotals();
            try
            {
                RawInputListener.ApplyMouseRecord(handle, 0, wheel, unchecked((ushort)(short)-15), 0, 0, 0);
                RawInputListener.ApplyMouseRecord(handle, 0, hwheel, 30, 0, 0, 0);
                RawInputListener.ApplyMouseRecord(handle, 0, 0, 0, 4, -3, 0);
                Assert.Equal(-15, RawInputListener.ConsumeMouseScroll(handle));
                Assert.Equal(30, RawInputListener.ConsumeMouseScrollH(handle));
                Assert.Equal(0, RawInputListener.ConsumeMouseScrollH(handle));
                var after = RawInputListener.MouseTotals();
                // The running totals saw it all, and the consumes took
                // nothing from them.
                Assert.Equal((4L, -3L, -15L, 30L), (after.x - before.x, after.y - before.y, after.wheel - before.wheel, after.wheelH - before.wheelH));
            }
            finally
            {
                RawInputListener.ConsumeMouseDelta(handle, out _, out _);
                RawInputListener.ConsumeMouseDelta(RawInputListener.AggregateMouseHandle, out _, out _);
                RawInputListener.ConsumeMouseScroll(RawInputListener.AggregateMouseHandle);
                RawInputListener.ConsumeMouseScrollH(RawInputListener.AggregateMouseHandle);
            }
        }

        [Fact]
        public void TheWheelsUnits_CrossRemoteLink()
        {
            var state = new CustomInputState { MouseRawWheel = -45, MouseRawWheelH = 360, MouseRawDX = 3 };
            var back = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(state, default));
            Assert.Equal((-45, 360, 3), (back.MouseRawWheel, back.MouseRawWheelH, back.MouseRawDX));
            var idle = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(new CustomInputState(), default));
            Assert.Equal((0, 0), (idle.MouseRawWheel, idle.MouseRawWheelH));
        }

        // ── Item 34: a relative axis ──

        private static CustomInputState Axis(int reading, int index = 3)
        {
            var s = new CustomInputState();
            s.Axis[index] = reading;
            return s;
        }

        [Fact]
        public void ARelativeAxis_HoldsTheChangeBetweenPasses_TakenTheShortWayRound()
        {
            var rt = new SourceKindRuntime();
            var src = Source("Axis 3");
            src.ParamAxisRelative = true;
            Assert.True(SourceEvaluator.IsMouseTravel(src));
            // A running total starts anywhere, so the first read moves nothing.
            Assert.Equal(0.0, Row(rt, src, Axis(40000), 0.001, "dev"));
            Assert.Equal(0.05, Row(rt, src, Axis(40100), 0.001, "dev"), 6);
            Assert.Equal(0.05, Row(rt, src, Axis(40100), 0.001, "dev"), 6);
            // Past the top of 16 bits it comes in at the bottom: 66 counts
            // on, not 65,470 back.
            var rt2 = new SourceKindRuntime();
            Row(rt2, src, Axis(65500), 0.001, "dev");
            Assert.Equal(66 / 2000.0, Row(rt2, src, Axis(30), 0.001, "dev"), 6);
            Assert.Equal(36, SourceCoercion.RelativeAxisChange(65530, 30));
            Assert.Equal(-36, SourceCoercion.RelativeAxisChange(30, 65530));
            Assert.Equal(-200, SourceCoercion.RelativeAxisChange(40200, 40000));
        }

        [Fact]
        public void ARelativeAxis_AfterAFrameWithoutARead_MovesNothingOnItsFirstReadBack()
        {
            var rt = new SourceKindRuntime();
            var src = Source("Slider 1");
            src.ParamAxisRelative = true;
            src.ParamTravelCentering = MappingSource.TravelCenteringOff;
            var s = new CustomInputState();
            s.Sliders[1] = 1000;
            Row(rt, src, s, 0.001, "dev");
            s.Sliders[1] = 1200;
            Assert.Equal(0.1, Row(rt, src, s, 0.001, "dev"), 6);
            rt.FrameSeq += 5;                       // the row was not read for a while
            s.Sliders[1] = 9000;
            Assert.Equal(0.0, Row(rt, src, s, 0.001, "dev"), 6);
            // An axis not marked reads as an axis does.
            Assert.False(SourceEvaluator.IsMouseTravel(Source("Slider 1")));
        }

        // ── Item 34: a relative axis from the device's own HID reports ──

        private static RelativeHidAxes.Value Hid(ushort page, ushort usage, bool relative, int bits = 8, int min = -127)
            => new(page, usage, 0, bits, min, relative);

        [Fact]
        public void TheHidReader_NumbersAxesAsSdlsDirectInputDoes()
        {
            // A trackball box: relative X and Y, an absolute throttle slider,
            // a relative dial (a spinner), a hat SDL reads as a hat.
            var values = new[]
            {
                Hid(0x01, 0x36, false, 8, 0),   // Slider, absolute
                Hid(0x01, 0x31, true),          // Y, relative
                Hid(0x01, 0x30, true),          // X, relative
                Hid(0x01, 0x39, false, 4, 0),   // Hat switch: no axis
                Hid(0x01, 0x37, true),          // Dial, relative: a slider
                Hid(0xFF00, 0x01, true),        // vendor value: no axis SDL names
            };
            // X, Y, then the sliders in the order the device lists them.
            Assert.Equal(new[] { 2, 1, 0, 4 }, RelativeHidAxes.SdlOrder(values, withoutRelative: false));
            Assert.Equal(new[] { 0 }, RelativeHidAxes.SdlOrder(values, withoutRelative: true));
            // The wheel reads as Z, steering as X and the brake as Rz.
            Assert.Equal(2, RelativeHidAxes.Slot(0x01, 0x38));
            Assert.Equal(0, RelativeHidAxes.Slot(0x02, 0xC8));
            Assert.Equal(5, RelativeHidAxes.Slot(0x02, 0xC5));
            Assert.Equal(-1, RelativeHidAxes.Slot(0x09, 0x01));
        }

        [Fact]
        public void TheHidReader_SignExtendsEachMovement()
        {
            Assert.Equal(-1, RelativeHidAxes.SignExtend(0xFF, 8, -127));
            Assert.Equal(127, RelativeHidAxes.SignExtend(0x7F, 8, -127));
            Assert.Equal(-2048, RelativeHidAxes.SignExtend(0x800, 12, -2047));
            Assert.Equal(200, RelativeHidAxes.SignExtend(200, 8, 0));        // an unsigned value stays
            Assert.Equal(-5, RelativeHidAxes.SignExtend(unchecked((uint)-5), 32, int.MinValue));
        }

        [Fact]
        public void TheHidReader_AddsEveryReportsMovement_WhereSdlHoldsOnlyTheLast()
        {
            const string path = @"\\?\HID#VID_1234&PID_5678#test#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var layout = RelativeHidAxes.Build(path, new[] { Hid(0x01, 0x30, true), Hid(0x01, 0x31, false, 8, 0) });
            RelativeHidAxes.RegisterForTest(layout);
            try
            {
                // A steady turn: the same movement in every report, which
                // DirectInput hands SDL as one unchanging value.
                for (int i = 0; i < 4; i++) RelativeHidAxes.Add(layout, v => v.Usage == 0x30 ? 5u : 128u);
                RelativeHidAxes.Add(layout, v => v.Usage == 0x30 ? 0xFDu : null);   // -3, a report that has no Y
                Assert.True(RelativeHidAxes.TryGetTotal(path.ToLowerInvariant(), 0, 2, out long x));
                Assert.Equal(17, x);
                // The absolute Y is no relative axis.
                Assert.False(RelativeHidAxes.TryGetTotal(path, 1, 2, out _));

                // SDL listing one axis means DirectInput left X out: SDL's
                // axis 0 is Y, and X follows as PadForge's axis 1.
                Assert.False(RelativeHidAxes.TryGetTotal(path, 0, 1, out _));
                Assert.True(RelativeHidAxes.TryGetTotal(path, 1, 1, out x));
                Assert.Equal(17, x);
            }
            finally
            {
                RelativeHidAxes.ForgetForTest(path);
            }
        }

        [Fact]
        public void ARelativeAxis_ReadsTheDevicesHidTotal_WhereItHasOne()
        {
            var saved = SourceCoercion.RelativeAxisTotalProvider;
            try
            {
                long total = 1000;
                SourceCoercion.RelativeAxisTotalProvider = (g, slider, index) => g == "spinner" && !slider && index == 3 ? total : null;
                var rt = new SourceKindRuntime();
                var src = Source("Axis 3");
                src.ParamAxisRelative = true;
                // The axis's reading jumps about as SDL's held movement does,
                // and is not read: the total is.
                Assert.Equal(0.0, Row(rt, src, Axis(41000), 0.001, "spinner"));
                total += 100;
                Assert.Equal(0.05, Row(rt, src, Axis(33000), 0.001, "spinner"), 6);
                total += 100;
                Assert.Equal(0.10, Row(rt, src, Axis(33000), 0.001, "spinner"), 6);
                // A device with no HID total reads its own running total.
                var rt2 = new SourceKindRuntime();
                Row(rt2, src, Axis(40000), 0.001, "other");
                Assert.Equal(0.05, Row(rt2, src, Axis(40100), 0.001, "other"), 6);
            }
            finally
            {
                SourceCoercion.RelativeAxisTotalProvider = saved;
            }
        }

        // ── Item 35: the four Steer modes and Return Time 0 ──

        private static MappingSource Steered(string descriptor, string steer, string centering = MappingSource.TravelCenteringOff)
        {
            var src = Source(descriptor, MouseGuid);
            src.ParamTravelSteer = steer;
            src.ParamTravelCentering = centering;
            src.GateDescriptor = "Button 2";
            return src;
        }

        [Fact]
        public void Travel_WhileHeldAndCenter_WithCenteringOff_CentersAtOnceWhenLetGo()
        {
            var rt = new SourceKindRuntime();
            var src = Steered("Mouse Travel X", MappingSource.TravelSteerHeldCenter);
            Assert.Equal(0.2, Row(rt, src, Mouse(400, steer: true), 0.001), 6);
            Assert.Equal(0.0, Row(rt, src, Mouse(400), 0.001), 6);
            Assert.Equal(0.1, Row(rt, src, Mouse(200, steer: true), 0.001), 6);
        }

        [Fact]
        public void Travel_WhileHeldAndStay_StaysWhereLetGo_EvenWithASpring()
        {
            var rt = new SourceKindRuntime();
            var src = Steered("Mouse Travel X", MappingSource.TravelSteerHeldStay, MappingSource.TravelCenteringSpring);
            Row(rt, src, Mouse(600, steer: true), 0.001);
            double left = 0;
            for (int i = 0; i < 3000; i++) left = Row(rt, src, Mouse(50), 0.001);
            Assert.Equal(0.3, left, 6);
            Assert.Equal(0.35, Row(rt, src, Mouse(100, steer: true), 0.001), 6);
        }

        [Fact]
        public void ReturnTimeZero_CentersInTheSamePassTheWheelIsLetGo()
        {
            var rt = new SourceKindRuntime();
            var src = Steered("Mouse Travel X", MappingSource.TravelSteerHeldCenter, MappingSource.TravelCenteringSpring);
            src.ParamTravelReturnTime = 0;
            Assert.Equal(0.25, Row(rt, src, Mouse(500, steer: true), 0.001), 6);
            Assert.Equal(0.0, Row(rt, src, Mouse(0), 0.001));
            Assert.Equal(0, MappingSource.EffectiveTravelReturnTime(0));
        }

        [Fact]
        public void TheOldWhileHeld_KeepsWhatItDid_UnderEveryCentering()
        {
            // Stay with Centering Off, Center by the Centering otherwise.
            Assert.Equal(MappingSource.TravelSteerHeldStay,
                MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerHeld, MappingSource.TravelCenteringOff, true, true));
            foreach (string c in new[] { MappingSource.TravelCenteringSpring, MappingSource.TravelCenteringTelemetry, MappingSource.TravelCenteringGameForce })
                Assert.Equal(MappingSource.TravelSteerHeldCenter, MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerHeld, c, true, true));
            // A speed or pointer source read nothing while its gate was open.
            Assert.Equal(MappingSource.TravelSteerHeldCenter,
                MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerAlways, null, true, false));
            // No input, no gate.
            Assert.Equal(MappingSource.TravelSteerAlways,
                MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerHeldStay, null, false, true));
            // A value a later build wrote reads as Always, and stays in the file.
            Assert.Equal(MappingSource.TravelSteerAlways, MappingSource.EffectiveTravelSteer("Sideways"));
        }

        [Fact]
        public void MouseSpeed_UnderEachSteerMode()
        {
            // Held and Center: the speed while held, center at once let go.
            var rt = new SourceKindRuntime();
            var center = Steered("Axis 0", MappingSource.TravelSteerHeldCenter);
            center.ParamMouseSpeedSensitivity = 2;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.Speed, SourceEvaluator.MouseSteerOf(center, MouseGuid, out bool y));
            Assert.False(y);
            double held = 0;
            for (int i = 0; i < 40; i++) held = Row(rt, center, Mouse(8, steer: true), 0.001, MouseGuid);
            Assert.InRange(held, 0.99, 1.0);              // 8,000 counts a second times 2 is the whole stick
            Assert.Equal(0.0, Row(rt, center, Mouse(8), 0.001, MouseGuid));

            // Held and Stay: where it was let go.
            rt = new SourceKindRuntime();
            var stay = Steered("Axis 0", MappingSource.TravelSteerHeldStay);
            for (int i = 0; i < 40; i++) held = Row(rt, stay, Mouse(2, steer: true), 0.001, MouseGuid);
            double kept = 0;
            for (int i = 0; i < 100; i++) kept = Row(rt, stay, Mouse(0), 0.001, MouseGuid);
            Assert.Equal(held, kept, 6);
            Assert.True(held > 0.1);

            // Toggle: a press turns it on, the next off.
            rt = new SourceKindRuntime();
            var toggle = Steered("Axis 0", MappingSource.TravelSteerToggle);
            Assert.Equal(0.0, Row(rt, toggle, Mouse(4), 0.001, MouseGuid));
            Row(rt, toggle, Mouse(4, steer: true), 0.001, MouseGuid);
            double on = 0;
            for (int i = 0; i < 40; i++) on = Row(rt, toggle, Mouse(4), 0.001, MouseGuid);
            Assert.True(on > 0.2, on.ToString());
            Row(rt, toggle, Mouse(4, steer: true), 0.001, MouseGuid);
            Assert.Equal(0.0, Row(rt, toggle, Mouse(4), 0.001, MouseGuid));
        }

        [Fact]
        public void MousePosition_UnderEachSteerMode()
        {
            (double, double, double, double, double)? point = (1060, 540, 960, 540, 192);
            SourceCoercion.MouseCursorPointProvider = () => point;
            var rt = new SourceKindRuntime();
            var stay = Steered("Mouse Position X", MappingSource.TravelSteerHeldStay);
            Assert.Equal(SourceEvaluator.MouseSteerFamily.Position, SourceEvaluator.MouseSteerOf(stay, null, out _));
            Assert.Equal(100.0 / 192, Row(rt, stay, Mouse(steer: true), 0.001), 6);
            point = (1160, 540, 960, 540, 192);
            Assert.Equal(100.0 / 192, Row(rt, stay, Mouse(), 0.001), 6);   // let go: it stays
            var center = Steered("Mouse Position X", MappingSource.TravelSteerHeldCenter);
            center.ParamTravelSmoothingMs = 1;                              // steers by its own settings
            var rt2 = new SourceKindRuntime();
            Row(rt2, center, Mouse(steer: true), 1.0);
            Assert.Equal(0.0, Row(rt2, center, Mouse(), 0.001));
        }

        [Fact]
        public void AtTheirDefaults_MouseSpeedAndMousePosition_ReadAsTheyAlwaysHave()
        {
            // A gate under While Held and Center is what a gate always did:
            // nothing while open, so the old read stands.
            var gated = Steered("Axis 0", MappingSource.TravelSteerHeldCenter);
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(gated, MouseGuid, out _));
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(Source("Axis 0", MouseGuid), MouseGuid, out _));
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(Source("Mouse Position Y"), null, out _));
            // Another device's Axis 0 is never Mouse Speed.
            var pad = Source("Axis 0", "11111111-2222-3333-4444-555555555555");
            pad.ParamMouseSpeedSensitivity = 3;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.None, SourceEvaluator.MouseSteerOf(pad, null, out _));
            // Nothing is written at the defaults.
            var xml = MouseTravelXml(Source("Axis 0"));
            foreach (string name in new[] { "ParamMouseSpeedSensitivity", "ParamMouseBackToCenter", "ParamMousePositionAnchor", "ParamAxisRelative" })
                Assert.DoesNotContain(name, xml);
        }

        private static string MouseTravelXml(MappingSource src)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MappingSource));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, src);
            return writer.ToString();
        }

        [Fact]
        public void TheNewSettings_RoundTripTheSavedForm()
        {
            var src = Source("Axis 1", MouseGuid);
            src.ParamMouseSpeedSensitivity = 2.5;
            src.ParamMouseBackToCenter = 0.4;
            src.ParamMousePositionAnchor = true;
            src.ParamAxisRelative = true;
            src.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(MappingSource));
            var back = (MappingSource)serializer.Deserialize(new System.IO.StringReader(MouseTravelXml(src)));
            Assert.Equal(2.5, back.ParamMouseSpeedSensitivity);
            Assert.Equal(0.4, back.ParamMouseBackToCenter);
            Assert.True(back.ParamMousePositionAnchor);
            Assert.True(back.ParamAxisRelative);
            Assert.Equal(MappingSource.TravelSteerHeldStay, back.ParamTravelSteer);
            Assert.Equal(MappingSource.DefaultMouseSpeedSensitivity, MappingSource.EffectiveMouseSpeedSensitivity(-1));
            Assert.Equal(MappingSource.MaxMouseSpeedSensitivity, MappingSource.EffectiveMouseSpeedSensitivity(1e9));
            Assert.Equal(MappingSource.MaxMouseBackToCenter, MappingSource.EffectiveMouseBackToCenter(9));
            Assert.Equal(0, MappingSource.EffectiveMouseBackToCenter(double.NaN));
        }

        // ── Item 36: Mouse Speed ──

        /// <summary>A mouse moving at <paramref name="countsPerSecond"/> read
        /// at <paramref name="hz"/> for a quarter second, through Mouse
        /// Speed's own settings: the counts each pass takes from a 1,000 Hz
        /// mouse, as Raw Input hands them over.</summary>
        private static double Speed(MappingSource src, int hz, int countsPerSecond)
        {
            var rt = new SourceKindRuntime();
            const int ticksPerSecond = 30000;
            int ticksPerPass = ticksPerSecond / hz;
            int perReport = countsPerSecond / 1000;
            int sent = 0;
            double last = 0;
            for (int p = 1; p <= hz / 4; p++)
            {
                int reportsBy = p * ticksPerPass / (ticksPerSecond / 1000);
                int counts = (reportsBy - sent) * perReport;
                sent = reportsBy;
                rt.FrameSeq++;
                last = rt.TickMouseSpeed(0, Target, 0, src, Mouse(counts), 1.0 / hz, false, false);
            }
            return last;
        }

        [Theory]
        [InlineData(60)]
        [InlineData(250)]
        [InlineData(1000)]
        [InlineData(10000)]
        public void MouseSpeed_ReadsTheSameAtEveryPollingRate(int hz)
        {
            var src = Source("Axis 0", MouseGuid);
            src.ParamMouseSpeedSensitivity = 0.5;
            // 8,000 counts a second is half the whole stick at 1, a quarter
            // at 0.5. At 60 Hz a pass takes 16 or 17 of the mouse's 1,000
            // reports a second, which moves the reading by up to 2%. The
            // window before #600 read 1.33 times true there (0.333).
            Assert.InRange(Speed(src, hz, 8000), 0.245, 0.256);
        }

        [Fact]
        public void TheSpeedWindow_IsTrueAt60Hz_AndDropsNothingAt10000Hz()
        {
            // A 1,000 counts a second stream in 16.7 ms passes.
            var w = new RelativeVelocityWindow();
            long ms = Stopwatch.Frequency / 1000;
            long t = 1000 * ms;
            long pass = Stopwatch.Frequency / 60;
            for (int i = 0; i < 30; i++) { t += pass; w.Add(t, 1000f / 60f, 0, 0); }
            w.CountsPerSecond(out float at60, out _, out _);
            Assert.InRange(at60, 990f, 1010f);

            // A 4,000 Hz mouse at 10,000 passes a second: one count in four
            // passes of 0.1 ms.
            var fast = new RelativeVelocityWindow();
            long tenth = Stopwatch.Frequency / 10000;
            t = 1000 * ms;
            for (int i = 0; i < 2000; i++) { t += tenth; fast.Add(t, i % 4 == 0 ? 1f : 0f, 0, 0); }
            fast.CountsPerSecond(out float at10k, out _, out _);
            Assert.InRange(at10k, 2400f, 2600f);
        }

        [Fact]
        public void MouseSpeed_SensitivityReachesPastWhereTheAxisClamps()
        {
            var src = Source("Axis 0", MouseGuid);
            src.ParamMouseSpeedSensitivity = 0.1;
            // 40,000 counts a second clamps the axis, and at 0.1 is a quarter stick.
            Assert.Equal(0.25, Speed(src, 1000, 40000), 2);
        }

        [Fact]
        public void MouseSpeed_SmoothingFollows_AndBackToCenterSlowsTheReturn()
        {
            var smooth = Source("Axis 0", MouseGuid);
            smooth.ParamTravelSmoothingMs = 50;
            var rt = new SourceKindRuntime();
            double first = 0;
            for (int i = 0; i < 30; i++) { rt.FrameSeq++; first = rt.TickMouseSpeed(0, Target, 0, smooth, Mouse(8), 0.001, false, false); }
            // 30 ms in, the 25 ms window holds the whole speed (0.5), and the
            // smoothing has followed it part of the way.
            Assert.InRange(first, 0.1, 0.45);

            var back = Source("Axis 0", MouseGuid);
            back.ParamMouseBackToCenter = 0.5;
            rt = new SourceKindRuntime();
            double v = 0;
            for (int i = 0; i < 60; i++) { rt.FrameSeq++; v = rt.TickMouseSpeed(0, Target, 0, back, Mouse(8), 0.001, false, false); }
            Assert.InRange(v, 0.49, 0.51);
            // Stopped: a plain speed is at rest a window later, this one is
            // still most of the way out.
            for (int i = 0; i < 60; i++) { rt.FrameSeq++; v = rt.TickMouseSpeed(0, Target, 0, back, Mouse(0), 0.001, false, false); }
            Assert.InRange(v, 0.3, 0.5);
        }

        // ── Item 37: Mouse Position ──

        [Fact]
        public void MousePosition_MeasuresOnThePointersOwnMonitor()
        {
            // A second monitor to the right of a 1,920 wide primary.
            var m = PadForge.Services.CursorControlService.Measure(1920 + 1280 + 256, 720, 1920, 0, 1920 + 2560, 1440);
            Assert.Equal(1.0, m.normX, 6);               // a tenth of its own width from its own center
            Assert.Equal(0.0, m.normY, 6);
            Assert.Equal(256, m.unit, 6);
            Assert.Equal(0f, PadForge.Services.CursorControlService.Measure(5, 5, 10, 0, 10, 10).unit);
        }

        [Fact]
        public void MousePosition_AnchoredWhereThePointerWasWhenSteeringBegan()
        {
            (double, double, double, double, double)? point = (400, 300, 960, 540, 192);
            SourceCoercion.MouseCursorPointProvider = () => point;
            var src = Steered("Mouse Position X", MappingSource.TravelSteerHeldCenter);
            src.ParamMousePositionAnchor = true;
            Assert.Equal(SourceEvaluator.MouseSteerFamily.Position, SourceEvaluator.MouseSteerOf(src, null, out _));
            var rt = new SourceKindRuntime();
            Assert.Equal(0.0, Row(rt, src, Mouse(steer: true), 0.001), 6);   // the press anchors here
            point = (496, 300, 960, 540, 192);
            Assert.Equal(0.5, Row(rt, src, Mouse(steer: true), 0.001), 6);
            Assert.Equal(0.0, Row(rt, src, Mouse(), 0.001), 6);              // let go: center
            point = (900, 300, 960, 540, 192);
            Assert.Equal(0.0, Row(rt, src, Mouse(steer: true), 0.001), 6);   // a new press, a new anchor
            // With no anchor, the monitor's center.
            src.ParamMousePositionAnchor = false;
            src.ParamTravelSmoothingMs = 1;
            var rt2 = new SourceKindRuntime();
            Assert.Equal(-60.0 / 192, Row(rt2, src, Mouse(steer: true), 10), 4);
        }

        [Fact]
        public void MousePosition_Smooths()
        {
            (double, double, double, double, double)? point = (1152, 540, 960, 540, 192);
            SourceCoercion.MouseCursorPointProvider = () => point;
            var src = Source("Mouse Position X");
            src.ParamTravelSmoothingMs = 100;
            var rt = new SourceKindRuntime();
            double v = Row(rt, src, Mouse(), 0.1);
            Assert.Equal(1 - Math.Exp(-1), v, 6);
        }

        // ── Item 41: From Telemetry's car shape, at every rate ──

        private static double TelemetryAfter(int hz, double start, double seconds)
        {
            var rt = new SourceKindRuntime();
            var src = Source("Mouse Travel X");
            src.ParamTravelCentering = MappingSource.TravelCenteringTelemetry;
            rt.FrameSeq++;
            rt.TickMouseTravel(0, Target, 0, src, Mouse((int)Math.Round(start * 2000)), 0, false);
            double v = 0;
            for (int i = 0; i < (int)Math.Round(seconds * hz); i++)
            {
                rt.FrameSeq++;
                v = rt.TickMouseTravel(0, Target, 0, src, Mouse(), 1.0 / hz, false);
            }
            return v;
        }

        [Fact]
        public void FromTelemetry_GivesTheSameWheel_At60And1000Hz()
        {
            SourceCoercion.MouseTravelRoadProvider = () => (80 / 3.6, 1.0, false);
            foreach (double start in new[] { 0.05, 0.4, 0.9 })
                Assert.Equal(TelemetryAfter(1000, start, 0.5), TelemetryAfter(60, start, 0.5), 3);
        }
    }
}
