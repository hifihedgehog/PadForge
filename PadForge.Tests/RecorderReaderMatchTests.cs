using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Recording;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pure half of the reader comparison (#600 item 32): SDL's DirectInput
    /// numbering replayed on DirectInput's own objects, DirectInput's scaling,
    /// SDL's hat rounding, SDL's mapping, PadForge's conversion, and the proof
    /// that SDL's Sony drivers mean what SDL's DirectInput rows mean.
    /// </summary>
    public class RecorderReaderMatchTests
    {
        // ── Fixtures ──

        private const uint AbsAxis = 0x2, PushButton = 0x4, PovKind = 0x10;

        private static DiObject Axis(Guid type, ushort page, ushort usage, bool taken = true)
            => new(type, AbsAxis, page, usage, 1, 0, taken);

        private static DiObject Hat() => new(RecorderReaderMatch.Pov, PovKind, 0x01, 0x39, 1, 0, false);

        private static DiObject ButtonObject(ushort usage) => new(RecorderReaderMatch.Button, PushButton, 0x09, usage, 1, 0, false);

        private static HidControl Value(ushort page, ushort usage, ushort index, int min = 0, int max = 255, int bits = 8, bool absolute = true)
            => new(page, usage, 1, 0, false, index, min, max, bits, absolute);

        private static HidControl ButtonControl(ushort usage, ushort index) => new(0x09, usage, 1, 0, true, index, 0, 1, 1, true);

        /// <summary>A flight stick as Wine's DirectInput lists it, values first
        /// in the descriptor's order, then buttons (Wine joystick_hid.c:801-905):
        /// X, Y, Rz, a slider, a hat and twelve buttons.</summary>
        private static (List<DiObject> Objects, List<HidControl> Controls) FlightStick()
        {
            var objects = new List<DiObject>
            {
                Axis(RecorderReaderMatch.XAxis, 0x01, 0x30), Axis(RecorderReaderMatch.YAxis, 0x01, 0x31),
                Axis(RecorderReaderMatch.RzAxis, 0x01, 0x35), Axis(RecorderReaderMatch.Slider, 0x01, 0x36), Hat(),
            };
            var controls = new List<HidControl>
            {
                Value(0x01, 0x30, 0, 0, 1023, 10), Value(0x01, 0x31, 1, 0, 1023, 10), Value(0x01, 0x35, 2), Value(0x01, 0x36, 3),
                new(0x01, 0x39, 1, 0, false, 4, 0, 7, 4, true),
            };
            for (ushort b = 1; b <= 12; b++)
            {
                objects.Add(ButtonObject(b));
                controls.Add(ButtonControl(b, (ushort)(4 + b)));
            }
            return (objects, controls);
        }

        // ── The SDL revision ──

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>Every rule the comparison replays was read from SDL at one
        /// fork commit. A new SDL delivery trips this until the replay is read
        /// against the new SDL and the constant moves with it.</summary>
        [Fact]
        public void TheReplayedRules_WereReadFromTheSdlPadForgeShips()
        {
            string folder = Path.Combine(Root(), "PadForge.App", "Resources", "SDL3");
            int checkedBuilds = 0;
            foreach (string dir in Directory.EnumerateDirectories(folder))
            {
                string dll = Path.Combine(dir, "SDL3.dll");
                if (!File.Exists(dll)) continue;
                string text = Encoding.ASCII.GetString(File.ReadAllBytes(dll));
                Assert.Contains("-g" + RecorderReaderMatch.SdlRevision + "\0", text);
                checkedBuilds++;
            }
            Assert.True(checkedBuilds > 0);
        }

        // ── SDL's numbering ──

        [Fact]
        public void SdlNumbersAFlightSticksInputs_ByTheirOffsets()
        {
            var (objects, _) = FlightStick();
            var n = RecorderReaderMatch.Number(objects);
            Assert.Equal(new uint[] { 0, 4, 20, 24 }, n.Axes.Select(a => a.Offset).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, n.Axes.Select(a => a.Source).ToArray());
            Assert.Single(n.Hats);
            Assert.Equal(4, n.Hats[0].Source);
            Assert.Equal(12, n.Buttons.Length);
            Assert.Equal(Enumerable.Range(5, 12).ToArray(), n.Buttons.Select(b => b.Source).ToArray());
        }

        [Fact]
        public void AWheelsSteeringAcceleratorAndBrake_AreXYAndRz_AndItsClutchIsNoSdlAxis()
        {
            // Wine names steering X, the accelerator Y and the brake Rz
            // (joystick_hid.c:503-511), and a clutch Unknown, which SDL passes
            // over (SDL_dinputjoystick.c:548-550).
            var objects = new List<DiObject>
            {
                Axis(RecorderReaderMatch.XAxis, 0x02, 0xC8), Axis(RecorderReaderMatch.YAxis, 0x02, 0xC4),
                Axis(RecorderReaderMatch.RzAxis, 0x02, 0xC5), Axis(RecorderReaderMatch.Unknown, 0x02, 0xC6),
            };
            var n = RecorderReaderMatch.Number(objects);
            Assert.Equal(new uint[] { 0, 4, 20 }, n.Axes.Select(a => a.Offset).ToArray());
            Assert.Equal(new[] { 0, 1, 2 }, n.Axes.Select(a => a.Source).ToArray());
        }

        [Fact]
        public void TwoAxesOfOneType_BothReadTheFirstOnesValue()
        {
            // Z and Wheel are both Z (Wine joystick_hid.c:518-519). SDL counts
            // both (SDL_dinputjoystick.c:537-538, 579) and reads both from lZ,
            // which the format fills with the first Z object (Wine device.c:229-258).
            var objects = new List<DiObject>
            {
                Axis(RecorderReaderMatch.XAxis, 0x01, 0x30), Axis(RecorderReaderMatch.ZAxis, 0x01, 0x32),
                Axis(RecorderReaderMatch.ZAxis, 0x01, 0x38),
            };
            var n = RecorderReaderMatch.Number(objects);
            Assert.Equal(3, n.Axes.Length);
            Assert.Equal(new uint[] { 0, 8, 8 }, n.Axes.Select(a => a.Offset).ToArray());
            Assert.Equal(new[] { 0, 1, 1 }, n.Axes.Select(a => a.Source).ToArray());
        }

        [Fact]
        public void AThirdSlider_AndTheFirstSliderItShares_AreNotRebuilt()
        {
            // The velocity slider entries point at the slider slots again
            // (SDL_dinputjoystick.c:226-228), and a third slider's own offset
            // is POV 0's (545-547).
            var objects = new List<DiObject>
            {
                Axis(RecorderReaderMatch.Slider, 0x01, 0x36), Axis(RecorderReaderMatch.Slider, 0x01, 0x37),
                Axis(RecorderReaderMatch.Slider, 0x02, 0xBB), Hat(),
            };
            var n = RecorderReaderMatch.Number(objects);
            Assert.Equal(new uint[] { 24, 28, 32 }, n.Axes.Select(a => a.Offset).ToArray());
            Assert.Equal(new[] { -1, 1, -1 }, n.Axes.Select(a => a.Source).ToArray());
            Assert.Equal(3, n.Hats[0].Source);
        }

        [Fact]
        public void AnAxisThatRefusesSdlsProperties_IsNoSdlAxis_AndARefusedSliderStillTakesItsSlot()
        {
            var objects = new List<DiObject>
            {
                Axis(RecorderReaderMatch.Slider, 0x01, 0x36, taken: false), Axis(RecorderReaderMatch.Slider, 0x01, 0x37),
                Axis(RecorderReaderMatch.YAxis, 0x01, 0x31, taken: false),
            };
            var n = RecorderReaderMatch.Number(objects);
            var axis = Assert.Single(n.Axes);
            Assert.Equal(28u, axis.Offset);
        }

        [Fact]
        public void AnObjectFindsItsReportControl_ByReportAndCollectionFirst()
        {
            var objects = new List<DiObject> { new(RecorderReaderMatch.XAxis, AbsAxis, 0x01, 0x30, 2, 1, true) };
            var controls = new List<HidControl>
            {
                new(0x01, 0x30, 1, 0, false, 0, 0, 255, 8, true), new(0x01, 0x30, 2, 1, false, 1, 0, 255, 8, true),
            };
            Assert.Equal(new[] { 1 }, RecorderReaderMatch.MatchControls(objects, controls));
            controls.RemoveAt(1);
            Assert.Equal(new[] { 0 }, RecorderReaderMatch.MatchControls(objects, controls));
        }

        // ── Values ──

        [Theory]
        [InlineData(0u, 0, 255, 8, -32768)]
        [InlineData(255u, 0, 255, 8, 32767)]
        [InlineData(127u, 0, 255, 8, -1)]
        [InlineData(128u, 0, 255, 8, 255)]
        [InlineData(1023u, 0, 1023, 10, 32767)]
        [InlineData(0x8000u, -32768, 32767, 16, -32768)]
        [InlineData(0x7FFFu, -32768, 32767, 16, 32767)]
        [InlineData(0xFFFFu, 0, -1, 16, 32767)]
        public void DirectInputScalesAnAxis_IntoTheRangeSdlSets(uint raw, int min, int max, int bits, int expected)
        {
            var c = new HidControl(0x01, 0x30, 1, 0, false, 0, min, max, bits, true);
            Assert.Equal((short)expected, RecorderReaderMatch.ScaleAxis(raw, c));
        }

        // SDL's hat bits: up 1, right 2, down 4, left 8.
        [Theory]
        [InlineData(0u, 0, 7, 1)]
        [InlineData(1u, 0, 7, 1 | 2)]
        [InlineData(4u, 0, 7, 4)]
        [InlineData(7u, 0, 7, 1 | 8)]
        [InlineData(8u, 0, 7, 0)]
        [InlineData(1u, 1, 8, 1)]
        [InlineData(0u, 1, 8, 0)]
        [InlineData(1u, 0, 3, 2)]
        [InlineData(3u, 0, 3, 8)]
        public void AHatSwitch_IsDirectInputsAngle_RoundedAsSdlRoundsIt(uint raw, int min, int max, int expected)
        {
            var c = new HidControl(0x01, 0x39, 1, 0, false, 0, min, max, 4, true);
            Assert.Equal(expected, (int)RecorderReaderMatch.HatOf(raw, c));
        }

        // ── SDL's mapping ──

        private const string Ps4Row = "030000004c050000c405000000000000,PS4 Controller,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,";
        private const string Ps4V2Row = "030000004c050000cc09000000000000,PS4 Controller,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,x:b0,y:b3,";
        private const string Ps5Row = "030000004c050000e60c000000000000,PS5 Controller,a:b1,b:b2,back:b8,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,misc1:b13,rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,x:b0,y:b3,";

        [Fact]
        public void AMapping_IsParsedAsSdlParsesIt()
        {
            var b = RecorderReaderMatch.ParseMapping(Ps4Row + "platform:Windows,");
            Assert.Equal(22, b.Count);
            Assert.Contains(new GamepadBinding(BindKind.Axis, 3, -32768, 32767, 0, BindKind.Axis, RecorderReaderMatch.LeftTrigger, 0, 32767), b);
            Assert.Contains(new GamepadBinding(BindKind.Hat, 0, 0, 0, 4, BindKind.Button, RecorderReaderMatch.DpadDown, 0, 0), b);
            Assert.Contains(new GamepadBinding(BindKind.Button, 1, 0, 0, 0, BindKind.Button, RecorderReaderMatch.South, 0, 0), b);

            // Half axes and an inverted axis (SDL_gamepad.c:1873-1941).
            var half = RecorderReaderMatch.ParseMapping("guid,name,+leftx:b0,-leftx:b1,lefty:a1~,righttrigger:-a2,");
            Assert.Contains(new GamepadBinding(BindKind.Button, 0, 0, 0, 0, BindKind.Axis, 0, 0, 32767), half);
            Assert.Contains(new GamepadBinding(BindKind.Button, 1, 0, 0, 0, BindKind.Axis, 0, 0, -32768), half);
            Assert.Contains(new GamepadBinding(BindKind.Axis, 1, 32767, -32768, 0, BindKind.Axis, 1, -32768, 32767), half);
            Assert.Contains(new GamepadBinding(BindKind.Axis, 2, 0, -32768, 0, BindKind.Axis, RecorderReaderMatch.RightTrigger, 0, 32767), half);

            // A labeled mapping SDL had not yet made positional swaps its face
            // buttons as it parses (1877-1888, 1797-1836).
            var labeled = RecorderReaderMatch.ParseMapping("guid,name,a:b0,b:b1,hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,");
            Assert.Contains(new GamepadBinding(BindKind.Button, 0, 0, 0, 0, BindKind.Button, RecorderReaderMatch.East, 0, 0), labeled);
            // The negated hint SDL leaves after it converts the mapping does not.
            var positional = RecorderReaderMatch.ParseMapping("guid,name,a:b0,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,");
            Assert.Contains(new GamepadBinding(BindKind.Button, 0, 0, 0, 0, BindKind.Button, RecorderReaderMatch.South, 0, 0), positional);

            // A colon writes the input from its start again over what the
            // buffer held (SDL_gamepad.c:1991-1993): "b1:b2" reads b2.
            var twice = RecorderReaderMatch.ParseMapping("guid,name,a:b1:b2,");
            Assert.Equal(new[] { new GamepadBinding(BindKind.Button, 2, 0, 0, 0, BindKind.Button, RecorderReaderMatch.South, 0, 0) }, twice);
            // A shorter input after the colon keeps the longer one's tail:
            // "b12:b3" leaves "b32".
            var tail = RecorderReaderMatch.ParseMapping("guid,name,a:b12:b3,");
            Assert.Equal(new[] { new GamepadBinding(BindKind.Button, 32, 0, 0, 0, BindKind.Button, RecorderReaderMatch.South, 0, 0) }, tail);
        }

        [Fact]
        public void TheMappingIsEvaluated_AsSdlEvaluatesIt()
        {
            var bindings = RecorderReaderMatch.ParseMapping(Ps4Row);
            var s = new SdlJoystickState(6, 14, 1);
            for (int i = 0; i < 6; i++) s.AxisKnown[i] = true;
            for (int i = 0; i < 14; i++) s.ButtonKnown[i] = true;
            s.HatKnown[0] = true;

            s.Axes[3] = -32768;
            Assert.Equal((short)0, RecorderReaderMatch.GamepadAxis(bindings, RecorderReaderMatch.LeftTrigger, s));
            s.Axes[3] = 32767;
            Assert.Equal((short)32767, RecorderReaderMatch.GamepadAxis(bindings, RecorderReaderMatch.LeftTrigger, s));
            s.Axes[0] = -20000;
            Assert.Equal((short)-20000, RecorderReaderMatch.GamepadAxis(bindings, 0, s));

            s.Hats[0] = RecorderReaderMatch.HatDown | RecorderReaderMatch.HatLeft;
            Assert.True(RecorderReaderMatch.GamepadButton(bindings, RecorderReaderMatch.DpadDown, s));
            Assert.True(RecorderReaderMatch.GamepadButton(bindings, RecorderReaderMatch.DpadLeft, s));
            Assert.False(RecorderReaderMatch.GamepadButton(bindings, RecorderReaderMatch.DpadUp, s));

            // An axis read as a button is down past its midpoint (3896-3911).
            var axisButton = RecorderReaderMatch.ParseMapping("guid,name,a:+a0,");
            s.Axes[0] = 20000;
            Assert.True(RecorderReaderMatch.GamepadButton(axisButton, RecorderReaderMatch.South, s));
            s.Axes[0] = 10000;
            Assert.False(RecorderReaderMatch.GamepadButton(axisButton, RecorderReaderMatch.South, s));

            // An input the comparison cannot rebuild leaves its output out.
            s.AxisKnown[3] = false;
            Assert.Null(RecorderReaderMatch.GamepadAxis(bindings, RecorderReaderMatch.LeftTrigger, s));
            // An output no binding sets is left out too.
            Assert.Null(RecorderReaderMatch.GamepadButton(bindings, 15, s));
        }

        [Fact]
        public void PadForgesGamepadLayout_IsWrittenAsTheWrapperWritesIt()
        {
            var bindings = RecorderReaderMatch.ParseMapping(Ps4Row);
            var s = new SdlJoystickState(6, 24, 1);
            for (int i = 0; i < 6; i++) s.AxisKnown[i] = true;
            for (int i = 0; i < 24; i++) s.ButtonKnown[i] = true;
            s.HatKnown[0] = true;
            s.Axes[3] = 32767;
            s.Hats[0] = RecorderReaderMatch.HatRight;
            s.Buttons[8] = s.Buttons[9] = s.Buttons[12] = true;
            s.Buttons[23] = true;

            var e = RecorderReaderMatch.GamepadLayout(bindings, s, Ps4Row, extraAxes: false, rawAxes: 6, rawButtons: 24, touchpad: false);
            Assert.Contains(new ExpectedControl(RecordingField.Axis, 2, 65535), e);
            Assert.Contains(new ExpectedControl(RecordingField.Hat, 0, 9000), e);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 6, 1), e);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 7, 1), e);
            // Back and Start held together let Guide go (SdlDeviceWrapper.cs:2098-2101).
            Assert.Contains(new ExpectedControl(RecordingField.Button, 10, 0), e);
            // Raw buttons past 22 the mapping leaves free pass through.
            Assert.Contains(new ExpectedControl(RecordingField.Button, 23, 1), e);
            // The touchpad position is the touchpad block's on a device with one.
            var withPad = RecorderReaderMatch.GamepadLayout(bindings, s, Ps4Row, false, 6, 24, touchpad: true);
            Assert.DoesNotContain(withPad, x => x.Field == RecordingField.Button && x.Index == 16);
            Assert.Contains(e, x => x.Field == RecordingField.Button && x.Index == 16);
            // That one position alone: Misc2, position 17, is still the loop's
            // (SdlDeviceWrapper.cs:2127-2129, against 2276 for position 16).
            var misc2 = RecorderReaderMatch.ParseMapping("guid,name,touchpad:b0,misc2:b1,");
            var pad = new SdlJoystickState(0, 2, 0);
            pad.ButtonKnown[0] = pad.ButtonKnown[1] = true;
            pad.Buttons[0] = pad.Buttons[1] = true;
            var both = RecorderReaderMatch.GamepadLayout(misc2, pad, "guid,name,touchpad:b0,misc2:b1,", false, 0, 2, touchpad: true);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 17, 1), both);
            Assert.DoesNotContain(both, x => x.Field == RecordingField.Button && x.Index == 16);
        }

        [Fact]
        public void OnlyTheReportIdsThatCarryACompareControl_AreWatched()
        {
            // X on report 1, a button on report 2, and a value on report 3 that
            // no object of DirectInput's names.
            var objects = new List<DiObject>
            {
                new(RecorderReaderMatch.XAxis, AbsAxis, 0x01, 0x30, 1, 0, true),
                new(RecorderReaderMatch.Button, PushButton, 0x09, 1, 2, 0, false),
            };
            var controls = new List<HidControl>
            {
                new(0x01, 0x30, 1, 0, false, 0, 0, 255, 8, true),
                new(0x09, 1, 2, 0, true, 1, 0, 1, 1, true),
                new(0xFF00, 0x20, 3, 0, false, 2, 0, 255, 8, true),
            };
            var plan = RecorderReaderPlan.DirectInputPlan(objects, controls, null, false, false, false, 1, 1, 1, 0, 1, false);
            Assert.Equal(new byte[] { 1, 2 }, RecorderReaderPlan.UsedReportIds(plan));
            // A kind the count check leaves out takes its report with it.
            var noButtons = RecorderReaderPlan.DirectInputPlan(objects, controls, null, false, false, false, 1, 2, 1, 0, 2, false);
            Assert.Equal(new byte[] { 1 }, RecorderReaderPlan.UsedReportIds(noButtons));
        }

        [Fact]
        public void PadForgesJoystickLayout_PutsAxesPast24InTheSliders()
        {
            var s = new SdlJoystickState(26, 2, 1);
            for (int i = 0; i < 26; i++) { s.AxisKnown[i] = true; s.Axes[i] = 0; }
            s.ButtonKnown[0] = s.ButtonKnown[1] = true;
            s.Buttons[1] = true;
            s.HatKnown[0] = true;
            var e = RecorderReaderMatch.JoystickLayout(s, axesRead: 26, hatsRead: 1, buttonsRead: 2);
            Assert.Contains(new ExpectedControl(RecordingField.Slider, 1, 32768), e);
            Assert.Contains(new ExpectedControl(RecordingField.Hat, 0, -1), e);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 1, 1), e);
        }

        // ── The difference ──

        [Fact]
        public void TheDifference_NamesEachControlByTheRecordingsName_AsTheXInputComparisonDoes()
        {
            var state = new CustomInputState();
            state.Axis[2] = 32768;
            state.Povs[0] = -1;
            var agree = new List<ExpectedControl> { new(RecordingField.Axis, 2, 32768 + 4096) };
            Assert.Null(RecorderReaderMatch.Difference(agree, state, RecordingControlKind.Generic));
            var e = new List<ExpectedControl>
            {
                new(RecordingField.Axis, 2, 32768 + 4097), new(RecordingField.Button, 4, 1), new(RecordingField.Hat, 0, 9000),
            };
            Assert.Equal("Axis2 36865 while PadForge's reader reads 32768, Button4 down while PadForge's reader reads it up, Hat0 9000 while PadForge's reader reads -1",
                RecorderReaderMatch.Difference(e, state, RecordingControlKind.Generic));
            Assert.Equal("LeftTrigger 36865 while PadForge's reader reads 32768",
                RecorderReaderMatch.Difference(new List<ExpectedControl> { e[0] }, state, RecordingControlKind.Gamepad));
        }

        // ── Which second reader ──

        [Theory]
        [InlineData("030000004c050000c405000000000000", (int)ReaderClass.DirectInput, null)]
        [InlineData("030000004c050000c405000000006800", (int)ReaderClass.Hidapi, null)]
        [InlineData("050057564c050000e60c000000006800", (int)ReaderClass.None, "SDL's driver numbers its controls its own way")]
        [InlineData("030000007e0500000920000000006800", (int)ReaderClass.None, "SDL's driver numbers its controls its own way")]
        [InlineData("03000000de2800000312000000006700", (int)ReaderClass.None, "SDL reads it through GameInput")]
        [InlineData("05000000de2800000312000000006c00", (int)ReaderClass.None, "SDL reads it over Bluetooth LE")]
        [InlineData("03000000000000000000000000007300", (int)ReaderClass.None, "SDL reads it from a COM port")]
        [InlineData("00000000000000000000000000006400", (int)ReaderClass.None, "SDL reads it over the network")]
        [InlineData("05000000000000000000000000006200", (int)ReaderClass.None, "SDL reads it over Bluetooth serial")]
        [InlineData("05000000000000000000000000006900", (int)ReaderClass.None, "SDL reads it from keyboard keys")]
        [InlineData("0000000000000000000000000000760a", (int)ReaderClass.None, "PadForge reads it with no HID collection")]
        public void ADevicesSecondReader_FollowsSdlsDriverSignature(string guid, int expected, string reason)
        {
            ushort vendor = (ushort)Convert.ToInt32(guid.Substring(10, 2) + guid.Substring(8, 2), 16);
            ushort product = (ushort)Convert.ToInt32(guid.Substring(18, 2) + guid.Substring(16, 2), 16);
            var (cls, why) = RecorderReaderPlan.Classify(guid, xinputPlace: false, spaceMouse: false, vendor, product, gamepadLayout: true);
            Assert.Equal((ReaderClass)expected, cls);
            Assert.Equal(reason, why);
            Assert.Equal(ReaderClass.XInput, RecorderReaderPlan.Classify(guid, true, false, vendor, product, true).Class);
            Assert.Equal(ReaderClass.SpaceMouse, RecorderReaderPlan.Classify("0300000000000000000000000000760a", false, true, 0, 0, false).Class);
        }

        // ── The proof for SDL's Sony drivers ──

        /// <summary>A field of a report: its usage and its first bit counted
        /// from the report ID's byte.</summary>
        private readonly record struct Field(ushort Page, ushort Usage, int Bit);

        /// <summary>Report 1 of a USB DualShock 4 (ControllersInfo
        /// dualshock4_hid_report_descriptor.txt:25-79, the same layout in
        /// HIDMaestro's dualshock-4-v1.json for 054C:05C4).</summary>
        private static List<Field> DualShock4Report()
        {
            var f = new List<Field> { new(1, 0x30, 8), new(1, 0x31, 16), new(1, 0x32, 24), new(1, 0x35, 32), new(1, 0x39, 40) };
            for (int b = 1; b <= 14; b++) f.Add(new(9, (ushort)b, 43 + b));
            f.Add(new(1, 0x33, 64));
            f.Add(new(1, 0x34, 72));
            return f;
        }

        /// <summary>Report 1 of a USB DualSense (ControllersInfo
        /// dualsense_hid_report_descriptor.txt:23-71).</summary>
        private static List<Field> DualSenseReport()
        {
            var f = new List<Field>
            {
                new(1, 0x30, 8), new(1, 0x31, 16), new(1, 0x32, 24), new(1, 0x35, 32), new(1, 0x33, 40), new(1, 0x34, 48), new(1, 0x39, 64),
            };
            for (int b = 1; b <= 15; b++) f.Add(new(9, (ushort)b, 67 + b));
            return f;
        }

        /// <summary>The bit each driver reads for each control of PadForge's
        /// gamepad layout. PS4: the state packet starts after the report ID
        /// (SDL_hidapi_ps4.c:80-105), the face buttons are the high nibble of
        /// byte 5 (1064-1071), the hat its low nibble (1073-1105), the
        /// shoulders, Share, Options and the stick clicks byte 6 (1110-1118),
        /// PS byte 7 (1130-1134), the triggers bytes 8 and 9 (1137-1140).
        /// PS5: the sticks and triggers bytes 1 to 6 (SDL_hidapi_ps5.c:83-102),
        /// the face buttons and hat byte 8, the shoulders and clicks byte 9,
        /// PS byte 10 (1282-1349), the triggers 1351-1361.</summary>
        private static Dictionary<(RecordingField, int), int> DriverBits(bool ps5)
        {
            int face = ps5 ? 64 : 40, row2 = face + 8, row3 = face + 16;
            return new Dictionary<(RecordingField, int), int>
            {
                [(RecordingField.Axis, 0)] = 8,
                [(RecordingField.Axis, 1)] = 16,
                [(RecordingField.Axis, 3)] = 24,
                [(RecordingField.Axis, 4)] = 32,
                [(RecordingField.Axis, 2)] = ps5 ? 40 : 64,
                [(RecordingField.Axis, 5)] = ps5 ? 48 : 72,
                [(RecordingField.Hat, 0)] = face,
                [(RecordingField.Button, 2)] = face + 4,
                [(RecordingField.Button, 0)] = face + 5,
                [(RecordingField.Button, 1)] = face + 6,
                [(RecordingField.Button, 3)] = face + 7,
                [(RecordingField.Button, 4)] = row2,
                [(RecordingField.Button, 5)] = row2 + 1,
                [(RecordingField.Button, 6)] = row2 + 4,
                [(RecordingField.Button, 7)] = row2 + 5,
                [(RecordingField.Button, 8)] = row2 + 6,
                [(RecordingField.Button, 9)] = row2 + 7,
                [(RecordingField.Button, 10)] = row3,
            };
        }

        /// <summary>The digital trigger bits both drivers read as a full
        /// trigger with no analog value: byte 6 bits 2 and 3 for the PS4
        /// (SDL_hidapi_ps4.c:1124-1128), byte 9 bits 2 and 3 for the PS5
        /// (SDL_hidapi_ps5.c:1351-1361).</summary>
        private static (int Left, int Right) DriverTriggerBits(bool ps5) => ps5 ? (74, 75) : (50, 51);

        /// <summary>The DirectInput objects Wine lists for a report: values in
        /// the descriptor's order with each usage's type (joystick_hid.c:498-531,
        /// 801-870), then buttons (874-905).</summary>
        private static (List<DiObject>, List<HidControl>) Listed(List<Field> report)
        {
            var objects = new List<DiObject>();
            var controls = new List<HidControl>();
            foreach (var f in report.Where(f => f.Page == 1))
            {
                Guid type = f.Usage switch
                {
                    0x30 => RecorderReaderMatch.XAxis, 0x31 => RecorderReaderMatch.YAxis, 0x32 => RecorderReaderMatch.ZAxis,
                    0x33 => RecorderReaderMatch.RxAxis, 0x34 => RecorderReaderMatch.RyAxis, 0x35 => RecorderReaderMatch.RzAxis,
                    _ => RecorderReaderMatch.Pov,
                };
                objects.Add(f.Usage == 0x39 ? Hat() : Axis(type, 1, f.Usage));
                controls.Add(f.Usage == 0x39 ? new(1, 0x39, 1, 0, false, (ushort)controls.Count, 0, 7, 4, true) : Value(1, f.Usage, (ushort)controls.Count));
            }
            foreach (var f in report.Where(f => f.Page == 9))
            {
                objects.Add(ButtonObject(f.Usage));
                controls.Add(ButtonControl(f.Usage, (ushort)controls.Count));
            }
            return (objects, controls);
        }

        /// <summary>The report usage SDL's DirectInput row gives a control of
        /// PadForge's gamepad layout, through SDL's numbering of Wine's list.</summary>
        private static (ushort Page, ushort Usage)? RowUsage(string row, List<Field> report, RecordingField field, int index)
        {
            var (objects, controls) = Listed(report);
            var n = RecorderReaderMatch.Number(objects);
            var match = RecorderReaderMatch.MatchControls(objects, controls);
            var bindings = RecorderReaderMatch.ParseMapping(row);
            (BindKind Kind, int Output) output = field switch
            {
                RecordingField.Axis => (BindKind.Axis, new[] { 0, 1, RecorderReaderMatch.LeftTrigger, 2, 3, RecorderReaderMatch.RightTrigger }[index]),
                RecordingField.Hat => (BindKind.Button, RecorderReaderMatch.DpadUp),
                _ => (BindKind.Button, new[] { 0, 1, 2, 3, 9, 10, 4, 6, 7, 8, 5 }[index]),
            };
            foreach (var b in bindings)
            {
                if (b.OutputKind != output.Kind || b.Output != output.Output) continue;
                SdlInput input = b.InputKind switch
                {
                    BindKind.Axis => n.Axes[b.Input],
                    BindKind.Button => n.Buttons[b.Input],
                    _ => n.Hats[b.Input],
                };
                var c = controls[match[input.Source]];
                return (c.UsagePage, c.Usage);
            }
            return null;
        }

        [Theory]
        [InlineData(0x05C4)]
        [InlineData(0x09CC)]
        [InlineData(0x0CE6)]
        public void TheSonyDriversMeaning_IsTheDirectInputRowsThroughTheDescriptor(int product)
        {
            bool ps5 = product == 0x0CE6;
            var report = ps5 ? DualSenseReport() : DualShock4Report();
            string row = product switch { 0x05C4 => Ps4Row, 0x09CC => Ps4V2Row, _ => Ps5Row };
            var proven = RecorderReaderPlan.ProvenFor(0x054C, (ushort)product);
            Assert.NotNull(proven);
            var bits = DriverBits(ps5);
            Assert.Equal(bits.Count, proven.Length);
            foreach (var p in proven)
            {
                // The driver's place holds the proven usage in the descriptor...
                int bit = bits[(p.Field, p.Index)];
                var field = Assert.Single(report, f => f.Bit == bit);
                Assert.Equal((p.UsagePage, p.Usage), (field.Page, field.Usage));
                // ...and SDL's DirectInput row names the same usage for the control.
                Assert.Equal((p.UsagePage, p.Usage), RowUsage(row, report, p.Field, p.Index));
                if (p.Kind == PlanKind.Trigger)
                {
                    int digital = p.Index == 2 ? DriverTriggerBits(ps5).Left : DriverTriggerBits(ps5).Right;
                    Assert.Equal(p.FallbackButton, Assert.Single(report, f => f.Bit == digital).Usage);
                }
            }

            // Left out, each for its fact: on the DualSense, SDL's driver calls
            // the mute button Misc1 (SDL_gamepad.c:1520-1522, b12 being
            // SDL_hidapi_ps5.c:48-49's microphone, bit 82 = Button 15), and the
            // DirectInput row calls the touchpad click Misc1 (b13 = Button 14).
            if (ps5)
            {
                var bindings = RecorderReaderMatch.ParseMapping(row);
                var misc1 = Assert.Single(bindings, b => b.OutputKind == BindKind.Button && b.Output == 15);
                Assert.Equal(13, misc1.Input);
                Assert.Equal(15, Assert.Single(report, f => f.Bit == 82).Usage);
            }
            Assert.DoesNotContain(proven, p => p.Field == RecordingField.Button && p.Index >= 11);
        }

        [Fact]
        public void AProvenPad_IsReadAsItsDriverReadsIt_TheTriggerOnItsButtonToo()
        {
            var (_, controls) = Listed(DualShock4Report());
            var plan = RecorderReaderPlan.FixedPlan(ReaderClass.Hidapi, RecorderReaderPlan.SonyPad, controls);
            var values = new uint[controls.Count];
            var known = Enumerable.Repeat(true, controls.Count).ToArray();
            int Of(ushort page, ushort usage) => controls.FindIndex(c => c.UsagePage == page && c.Usage == usage);
            values[Of(1, 0x30)] = 255;
            values[Of(1, 0x39)] = 2;
            values[Of(9, 7)] = 1;
            values[Of(9, 9)] = values[Of(9, 10)] = values[Of(9, 13)] = 1;
            var e = RecorderReaderPlan.Expected(plan, values, known);
            Assert.Contains(new ExpectedControl(RecordingField.Axis, 0, 65535), e);
            Assert.Contains(new ExpectedControl(RecordingField.Hat, 0, 9000), e);
            // L2's bit with no analog value is a full trigger (SDL_hidapi_ps4.c:1124-1128).
            Assert.Contains(new ExpectedControl(RecordingField.Axis, 2, 65535), e);
            Assert.Contains(new ExpectedControl(RecordingField.Axis, 5, 0), e);
            // Back and Start let Guide go.
            Assert.Contains(new ExpectedControl(RecordingField.Button, 10, 0), e);
            string words = RecorderReaderPlan.MatchWords(plan);
            Assert.StartsWith("LeftX is its X Axis, LeftY its Y Axis, LeftTrigger its X Rotation", words);
            Assert.EndsWith(" by SDL's DirectInput mapping for it", words);
        }

        // ── The SpaceMouse ──

        [Fact]
        public void ASpaceMousesAxes_AreTheDecodersWithThePucksOwnMaximum()
        {
            // The Space Navigator declares -350 to 350 (spacemouse-AndunHH
            // SpaceNavigator.md:75-76), the decoder's own maximum.
            var controls = new List<HidControl>();
            for (int i = 0; i < 6; i++) controls.Add(new(1, (ushort)(0x30 + i), (byte)(i < 3 ? 1 : 2), 0, false, (ushort)i, -350, 350, 16, false));
            controls.Add(ButtonControl(1, 6));
            controls.Add(ButtonControl(2, 7));
            var plan = RecorderReaderPlan.FixedPlan(ReaderClass.SpaceMouse, RecorderReaderPlan.SpaceMouse(), controls);
            Assert.Equal(8, plan.Proven.Length);
            var values = new uint[controls.Count];
            var known = Enumerable.Repeat(true, controls.Count).ToArray();
            foreach (short raw in new short[] { -350, -175, 0, 1, 349, 350 })
            {
                values[0] = unchecked((ushort)raw);
                var e = RecorderReaderPlan.Expected(plan, values, known);
                Assert.Contains(new ExpectedControl(RecordingField.Axis, 0, RecorderReaderMatch.Unsigned(SpaceMouseDecoder.ToSdlAxis(raw))), e);
            }
            Assert.Equal("Axis0 is its X Axis, Axis1 its Y Axis, Axis2 its Z Axis, Axis3 its X Rotation, Axis4 its Y Rotation, Axis5 its Z Rotation, Button0 to Button1 its Buttons 1 to 2",
                RecorderReaderPlan.MatchWords(plan));
        }

        // ── A DirectInput plan ──

        [Fact]
        public void AFlightStick_IsRebuiltInTheJoystickLayout_AndCountsThatDoNotMatchAreLeftOut()
        {
            var (objects, controls) = FlightStick();
            var plan = RecorderReaderPlan.DirectInputPlan(objects, controls, null, gamepadLayout: false, extraAxes: false, touchpad: false,
                rawAxes: 4, rawButtons: 12, numAxes: 4, numHats: 1, numButtons: 12, gamepadOpened: false);
            Assert.Empty(plan.LeftOut);
            var values = new uint[controls.Count];
            var known = Enumerable.Repeat(true, controls.Count).ToArray();
            values[0] = 1023;
            values[4] = 6;
            values[5 + 2] = 1;
            var e = RecorderReaderPlan.Expected(plan, values, known);
            Assert.Contains(new ExpectedControl(RecordingField.Axis, 0, 65535), e);
            Assert.Contains(new ExpectedControl(RecordingField.Hat, 0, 27000), e);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 2, 1), e);
            Assert.Equal("Axis0 is its X Axis, Axis1 its Y Axis, Axis2 its Z Rotation, Axis3 its Slider, Hat0 its Hat Switch, Button0 to Button11 its Buttons 1 to 12",
                RecorderReaderPlan.MatchWords(plan));

            var miscounted = RecorderReaderPlan.DirectInputPlan(objects, controls, null, false, false, false,
                rawAxes: 5, rawButtons: 12, numAxes: 5, numHats: 1, numButtons: 12, gamepadOpened: false);
            Assert.Equal(new[] { "axes: SDL keeps 5 and DirectInput lists 4" }, miscounted.LeftOut);
            Assert.DoesNotContain(RecorderReaderPlan.Expected(miscounted, values, known), x => x.Field == RecordingField.Axis);
        }

        [Fact]
        public void AMappedStickOfAnAxisCountThatDoesNotMatch_IsLeftOut_NeverReadAsCentered()
        {
            var objects = new List<DiObject> { Axis(RecorderReaderMatch.XAxis, 0x01, 0x30), ButtonObject(1) };
            var controls = new List<HidControl> { Value(0x01, 0x30, 0), ButtonControl(1, 1) };
            const string mapping = "030000004f0400000ab1000000000000,Test Pad,a:b0,leftx:a0,platform:Windows,";
            // SDL keeps two axes, DirectInput lists one: SDL's axis 0 may be
            // another object, so the stick bound to it is not rebuilt.
            var plan = RecorderReaderPlan.DirectInputPlan(objects, controls, mapping, gamepadLayout: true, extraAxes: false, touchpad: false,
                rawAxes: 2, rawButtons: 1, numAxes: 6, numHats: 1, numButtons: 22, gamepadOpened: true);
            Assert.Equal(new[] { "axes: SDL keeps 2 and DirectInput lists 1" }, plan.LeftOut);
            var e = RecorderReaderPlan.Expected(plan, new uint[] { 255, 1 }, new[] { true, true });
            Assert.DoesNotContain(e, x => x.Field == RecordingField.Axis);
            Assert.Contains(new ExpectedControl(RecordingField.Button, 0, 1), e);
        }

        [Fact]
        public void ARelativeValue_IsLeftOutOfADirectInputDevice()
        {
            var objects = new List<DiObject> { Axis(RecorderReaderMatch.XAxis, 1, 0x30) };
            var controls = new List<HidControl> { Value(1, 0x30, 0, -127, 127, 8, absolute: false) };
            var plan = RecorderReaderPlan.DirectInputPlan(objects, controls, null, false, false, false, 1, 0, 1, 0, 0, false);
            Assert.Empty(RecorderReaderPlan.Expected(plan, new uint[] { 5 }, new[] { true }));
        }
    }
}
