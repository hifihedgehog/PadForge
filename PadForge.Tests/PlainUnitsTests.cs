using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The one converter between raw values and plain units (#600): sticks
    /// -100 to 100 with 0 at rest, triggers and sliders 0 to 100, hats in
    /// degrees or -1, hat directions, buttons true or false. Round trips at
    /// the edges and over every raw value.
    /// </summary>
    public class PlainUnitsTests
    {
        [Theory]
        [InlineData(32768)]
        [InlineData(32767)]
        public void AStick_HasItsRestAt0_AndItsEndsAt100(int rest)
        {
            Assert.Equal(0, PlainUnits.AxisToPlain(rest, rest));
            Assert.Equal(-100, PlainUnits.AxisToPlain(0, rest));
            Assert.Equal(100, PlainUnits.AxisToPlain(65535, rest));
            Assert.Equal(rest, PlainUnits.AxisToRaw(0, rest));
            Assert.Equal(0, PlainUnits.AxisToRaw(-100, rest));
            Assert.Equal(65535, PlainUnits.AxisToRaw(100, rest));
            Assert.True(PlainUnits.IsCentered(rest));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65535)]
        [InlineData(1000)]
        [InlineData(32766)]
        public void AnyOtherRest_MakesATriggerFrom0To100(int rest)
        {
            Assert.False(PlainUnits.IsCentered(rest));
            Assert.Equal(0, PlainUnits.AxisToPlain(0, rest));
            Assert.Equal(100, PlainUnits.AxisToPlain(65535, rest));
            Assert.Equal(65535, PlainUnits.AxisToRaw(100, rest));
            Assert.False(PlainUnits.AxisAccepts(-1, rest));
            Assert.True(PlainUnits.AxisAccepts(0, rest));
        }

        [Fact]
        public void AnUnknownRest_IsAStickForAnAxis_AndASliderFromIndex24()
        {
            Assert.Equal(32768, PlainUnits.DefaultRest(0));
            Assert.Equal(32768, PlainUnits.DefaultRest(23));
            Assert.Equal(0, PlainUnits.DefaultRest(ProgramAxis.FirstSlider));
            Assert.Equal(0, PlainUnits.DefaultRest(ProgramAxis.End - 1));
        }

        [Theory]
        [InlineData(32768)]
        [InlineData(32767)]
        [InlineData(0)]
        public void EveryRawValue_ComesBackFromItsPlainText_AndPlainRisesWithRaw(int rest)
        {
            double last = double.NegativeInfinity;
            for (int raw = 0; raw <= 65535; raw++)
            {
                double plain = PlainUnits.AxisToPlain(raw, rest);
                Assert.True(plain > last, "plain falls or stays at raw " + raw);
                last = plain;
                string text = PlainUnits.Format(plain);
                double read = double.Parse(text, CultureInfo.InvariantCulture);
                Assert.Equal(raw, PlainUnits.AxisToRaw(read, rest));
            }
        }

        [Theory]
        [InlineData(-100.0, 0)]
        [InlineData(-50.0, 16384)]
        [InlineData(-0.001, 32768)]
        [InlineData(0.0, 32768)]
        [InlineData(33.3, 43679)]
        [InlineData(100.0, 65535)]
        [InlineData(250.0, 65535)]
        [InlineData(-250.0, 0)]
        public void APlainStickValue_GivesItsRawValue_AndOneOutsideIsHeldToTheEnd(double plain, int raw)
            => Assert.Equal(raw, PlainUnits.AxisToRaw(plain, 32768));

        [Fact]
        public void PlainText_HasAPointThreeDecimalsAtMost_AndNoMinusZero()
        {
            Assert.Equal("0", PlainUnits.Format(-0.0001));
            Assert.Equal("-12.5", PlainUnits.Format(-12.5));
            Assert.Equal("22.071", PlainUnits.Format(22.07104));
            var german = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("12.5", PlainUnits.Format(12.5));
            }
            finally { CultureInfo.CurrentCulture = german; }
        }

        [Fact]
        public void AHat_IsInDegrees_OrMinusOneAtRest()
        {
            Assert.Equal(-1, PlainUnits.HatToPlain(-1));
            Assert.Equal(0, PlainUnits.HatToPlain(0));
            Assert.Equal(90, PlainUnits.HatToPlain(9000));
            Assert.Equal(359.99, PlainUnits.HatToPlain(35999));
            Assert.Equal(-1, PlainUnits.HatToPlain(36000));
            Assert.Equal(9000, PlainUnits.HatToRaw(90));
            Assert.Equal(9000, PlainUnits.HatToRaw(450));
            Assert.Equal(0, PlainUnits.HatToRaw(359.999));
            Assert.Equal(-1, PlainUnits.HatToRaw(-1));
            for (int raw = -1; raw < 36000; raw++)
                Assert.Equal(raw, PlainUnits.HatToRaw(double.Parse(PlainUnits.Format(PlainUnits.HatToPlain(raw)), CultureInfo.InvariantCulture)));
        }

        [Theory]
        [InlineData(-1, HatDirections.None, "")]
        [InlineData(0, HatDirections.Up, "Up")]
        [InlineData(4500, HatDirections.Up | HatDirections.Right, "Up+Right")]
        [InlineData(9000, HatDirections.Right, "Right")]
        [InlineData(13500, HatDirections.Right | HatDirections.Down, "Right+Down")]
        [InlineData(18000, HatDirections.Down, "Down")]
        [InlineData(22500, HatDirections.Down | HatDirections.Left, "Down+Left")]
        [InlineData(27000, HatDirections.Left, "Left")]
        [InlineData(31500, HatDirections.Up | HatDirections.Left, "Up+Left")]
        [InlineData(2249, HatDirections.Up, "Up")]
        [InlineData(2250, HatDirections.Up | HatDirections.Right, "Up+Right")]
        [InlineData(35999, HatDirections.Up, "Up")]
        public void AHatsDirections_AreTheNearestPoint_ADiagonalHoldingTwo(int raw, HatDirections directions, string text)
        {
            Assert.Equal(directions, PlainUnits.HatToDirections(raw));
            Assert.Equal(text, PlainUnits.DirectionsText(directions));
            if (raw % 4500 == 0) Assert.Equal(raw, PlainUnits.DirectionsToHat(directions));
            if (text.Length > 0)
            {
                Assert.True(PlainUnits.TryParseDirections(text.ToLowerInvariant(), out var read));
                Assert.Equal(directions, read);
            }
        }

        [Fact]
        public void OppositeDirections_Cancel()
        {
            Assert.Equal(-1, PlainUnits.DirectionsToHat(HatDirections.Up | HatDirections.Down));
            Assert.Equal(9000, PlainUnits.DirectionsToHat(HatDirections.Up | HatDirections.Down | HatDirections.Right));
            Assert.False(PlainUnits.TryParseDirections("Up+Sideways", out _));
        }

        [Fact]
        public void TheWire_TakesRawNumbers_AndPercentAgainstTheRest()
        {
            Assert.Null(PlainUnits.ReadAxis("40000", 32768, out int raw));
            Assert.Equal(40000, raw);
            Assert.Null(PlainUnits.ReadAxis("50%", 32768, out raw));
            Assert.Equal(49152, raw);
            Assert.Null(PlainUnits.ReadAxis("-30%", 32768, out raw));
            Assert.Equal(PlainUnits.AxisToRaw(-30, 32768), raw);
            Assert.Null(PlainUnits.ReadAxis("50%", 0, out raw));
            Assert.Equal(32768, raw);
            Assert.NotNull(PlainUnits.ReadAxis("-30%", 0, out _));
            Assert.NotNull(PlainUnits.ReadAxis("101%", 32768, out _));
            Assert.NotNull(PlainUnits.ReadAxis("65536", 32768, out _));
            Assert.NotNull(PlainUnits.ReadAxis("ten", 32768, out _));
        }

        [Theory]
        [InlineData("9000", 9000)]
        [InlineData("-1", -1)]
        [InlineData("90deg", 9000)]
        [InlineData("22.5°", 2250)]
        [InlineData("-1deg", -1)]
        [InlineData("Up+Right", 4500)]
        [InlineData("left", 27000)]
        [InlineData("rest", -1)]
        public void TheWire_TakesAHatRaw_InDegrees_OrAsDirections(string text, int raw)
        {
            Assert.Null(PlainUnits.ReadHat(text, out int read));
            Assert.Equal(raw, read);
        }

        [Theory]
        [InlineData("36000")]
        [InlineData("-2")]
        [InlineData("90")]
        public void AHatOutsideItsRange_IsRefused(string text)
        {
            if (text == "90")
            {
                // A whole number with no mark stays raw: 90 is 0.9 degrees.
                Assert.Null(PlainUnits.ReadHat(text, out int raw));
                Assert.Equal(90, raw);
                return;
            }
            Assert.NotNull(PlainUnits.ReadHat(text, out _));
        }

        [Fact]
        public void AButton_IsTrueOrFalse_Or1Or0()
        {
            Assert.True(PlainUnits.TryReadButton("true", out bool down) && down);
            Assert.True(PlainUnits.TryReadButton("1", out down) && down);
            Assert.True(PlainUnits.TryReadButton("false", out down) && !down);
            Assert.False(PlainUnits.TryReadButton("2", out _));
            Assert.True(PlainUnits.ButtonToPlain(1));
            Assert.Equal(0, PlainUnits.ButtonToRaw(false));
        }
    }

    /// <summary>Plain units on the Automation pipe (#600): an s line and own
    /// take percent, degrees and directions, rest puts a device at rest, and
    /// a session that says units=plain reads events and states in plain
    /// units. A raw session's lines stay as they were.</summary>
    [Collection("SettingsManagerStatics")]
    public class AutomationPlainUnitsTests
    {
        private const string Shape = "{\"buttons\":[\"A\",\"B\"],\"axes\":[\"X\",\"Y\",{\"index\":2,\"name\":\"Trigger\",\"rest\":\"0%\"}],"
                                     + "\"sliders\":[\"Throttle\"],\"hats\":[\"Pad\"]}";

        [Fact]
        public void AnSLine_TakesPercentDegreesDirectionsAndTheRestWord()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=plain name=\"Plain\" " + Shape).Substring(6);
            var device = rig.Host.Sessions.Devices.Find(id).Device;
            Assert.Equal("@1 ok", client.Final("@1 s " + id + " a0=-100% a1=50% a2=50% a24=25% h0=Up+Right b0=true"));
            var s = device.PeekState();
            Assert.Equal(0, s.Axis[0]);
            Assert.Equal(49152, s.Axis[1]);
            Assert.Equal(32768, s.Axis[2]);
            Assert.Equal(PlainUnits.AxisToRaw(25, 0), s.Sliders[0]);
            Assert.Equal(4500, s.Povs[0]);
            Assert.True(s.Buttons[0]);

            // A trigger takes no negative percent.
            Assert.StartsWith("err bad-argument ", client.Final("s " + id + " a2=-10%"));
            // Degrees, and a raw value as before.
            Assert.Equal("@2 ok", client.Final("@2 s " + id + " h0=270deg a0=40000"));
            Assert.Equal(27000, device.PeekState().Povs[0]);
            Assert.Equal(40000, device.PeekState().Axis[0]);

            // rest first, then the rest of the line.
            Assert.Equal("@3 ok", client.Final("@3 s " + id + " rest b1=1"));
            s = device.PeekState();
            Assert.Equal(32768, s.Axis[0]);
            Assert.Equal(-1, s.Povs[0]);
            Assert.False(s.Buttons[0]);
            Assert.True(s.Buttons[1]);
        }

        [Fact]
        public void Rest_PutsEveryControlAtRest_InOneWord_AndKeepsTheDeviceOnline()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            using var client = rig.Open();
            string id = client.Final("declare key=rest name=\"Rest\" " + Shape).Substring(6);
            var entry = rig.Host.Sessions.Devices.Find(id);
            Assert.Equal("@1 ok", client.Final("@1 s " + id + " a0=0 a2=65535 h0=9000 b0=1"));
            Assert.Equal("ok", client.Final("rest " + id));
            var s = entry.Device.PeekState();
            Assert.Equal(32768, s.Axis[0]);
            Assert.Equal(0, s.Axis[2]);
            Assert.Equal(-1, s.Povs[0]);
            Assert.False(s.Buttons[0]);
            Assert.NotNull(rig.Host.Sessions.Devices.Find(id));
            using var other = rig.Open("Other");
            Assert.StartsWith("err not-owner ", other.Final("rest " + id));
        }

        [Fact]
        public void Rest_IsRefusedBelowSendInputs()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Open();
            Assert.StartsWith("err not-allowed", client.Final("rest abcdefgh"));
        }

        [Fact]
        public void APlainSession_ReadsEventsAndStatesInPlainUnits_AndARawOneAsBefore()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var plain = rig.Open("Plain", " units=plain");
            using var raw = rig.Open("Raw");
            pad.ReadInputState.Axis[0] = 32768;
            foreach (var c in new[] { plain, raw })
            {
                Assert.Equal("ok", c.Final("sub " + id + " hats"));
                Assert.Equal("ok", c.Final("sub " + id + " axes 1000"));
            }
            rig.Pass(pad);
            pad.ReadInputState.Povs[0] = 4500;
            pad.ReadInputState.Axis[0] = 0;
            pad.ReadInputState.Axis[2] = 65535;
            rig.Pass(pad);
            var plainLines = plain.Drain(300);
            var rawLines = raw.Drain(300);
            Assert.Contains("e h " + id + " 0 45", plainLines);
            Assert.Contains("e a " + id + " 0 -100", plainLines);
            Assert.Contains("e a " + id + " 2 100", plainLines);
            Assert.Contains("e h " + id + " 0 4500", rawLines);
            Assert.Contains("e a " + id + " 0 0", rawLines);
            Assert.Contains("e a " + id + " 2 65535", rawLines);

            // The state line: axis 0 a stick, axis 2 a gamepad's trigger.
            string state = plain.Final("read " + id);
            Assert.Contains(" a=-100,", state);
            Assert.Contains(",100,", state);
            Assert.Contains(" h=45,", state);
            Assert.Contains(" a=0,", raw.Final("read " + id));
        }

        [Fact]
        public void ACrossing_TakesAPercent()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open("Plain", " units=plain");
            Assert.Equal("ok", client.Final("sub " + id + " axis=0 above=80% hyst=5%"));
            var axis = pad.ReadInputState.Axis;
            foreach (int v in new[] { 32768, PlainUnits.AxisToRaw(79, 32768), PlainUnits.AxisToRaw(80, 32768), PlainUnits.AxisToRaw(76, 32768), PlainUnits.AxisToRaw(60, 32768) })
            {
                axis[0] = v;
                rig.Pass(pad);
            }
            Assert.Equal(new[] { "e x " + id + " 0 1", "e x " + id + " 0 0" }, client.Drain(300));
            Assert.StartsWith("err bad-argument ", client.Final("sub " + id + " axis=0 above=120%"));
        }

        [Fact]
        public void AHello_TakesUnitsPlainOrRaw_AndNothingElse()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = rig.Connect();
            Assert.StartsWith("err bad-argument ", client.Final("hello name=\"X\" kind=script units=metric"));
            Assert.EndsWith(" units=plain", client.Final("hello name=\"X\" kind=script units=plain"));
        }

        [Fact]
        public void AHold_TakesPlainValues()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.SendInputs);
            var pad = rig.AddDevice(InputDeviceType.Gamepad);
            string id = AutomationPipeRig.IdOf(pad);
            using var client = rig.Open();
            Assert.Equal("ok", client.Final("own " + id + " a0=-50% h0=Down b2=true"));
            var holds = PadForge.Common.Input.ControlHolds.Current[pad.InstanceGuid];
            Assert.Contains(holds.Axes, a => a.Index == 0 && a.Value == 16384);
            Assert.Contains(holds.Hats, h => h.Index == 0 && h.Value == 18000);
            Assert.Contains(holds.Buttons, b => b.Index == 2 && b.Down);
            Assert.StartsWith("err bad-argument ", client.Final("own " + id + " a0=200%"));
        }

        [Fact]
        public void ADeclaration_TakesARestAsAShareOfTheTravel()
        {
            var shape = ProgramDeviceDeclaration.Parse("{\"axes\":[{\"name\":\"T\",\"rest\":\"0%\"},{\"index\":1,\"name\":\"S\",\"rest\":\"50%\"}]}", out string error);
            Assert.Null(error);
            Assert.Equal(0, shape.RestOf(0));
            Assert.Equal(32768, shape.RestOf(1));
            Assert.Null(ProgramDeviceDeclaration.Parse("{\"axes\":[{\"rest\":\"150%\"}]}", out error));
            Assert.NotNull(error);
        }
    }
}
