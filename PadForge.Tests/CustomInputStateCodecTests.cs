using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class CustomInputStateCodecTests
    {
        private static readonly CustomInputStateCodec.Caps NoSensors = new(gyro: false, accel: false);
        private static readonly CustomInputStateCodec.Caps WithMotion = new(gyro: true, accel: true);

        // A real centered device emits 32768 on the four stick axes (the producer
        // does (ushort)(raw - short.MinValue)); a fresh CustomInputState leaves
        // them at 0. The codec's neutral is the producer's centered value, so this
        // is the true "nothing touched" baseline that encodes to the empty frame.
        private static CustomInputState Centered()
        {
            var s = new CustomInputState();
            s.Axis[0] = 32768; s.Axis[1] = 32768; s.Axis[3] = 32768; s.Axis[4] = 32768;
            return s;
        }

        private static void AssertGamepadEqual(CustomInputState a, CustomInputState b)
        {
            Assert.Equal(a.Axis, b.Axis);
            Assert.Equal(a.Sliders, b.Sliders);
            Assert.Equal(a.Povs, b.Povs);
            Assert.Equal(a.Buttons, b.Buttons);
        }

        [Fact]
        public void NeutralGamepad_EncodesToThreeBytesAndRoundTrips()
        {
            var s = Centered(); // sticks at 32768, triggers 0, povs -1, no buttons
            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            Assert.Equal(3, bytes.Length); // version + presence(0); every block at neutral

            var rt = CustomInputStateCodec.Decode(bytes);
            AssertGamepadEqual(s, rt);
            // The decoded sticks are at the corrected 32768 center, not 32767.
            Assert.Equal(32768, rt.Axis[0]);
            Assert.Equal(0, rt.Axis[2]); // trigger neutral
            Assert.Equal(-1, rt.Povs[0]);
        }

        [Fact]
        public void TypicalGamepad_RoundTrips()
        {
            var s = new CustomInputState();
            s.Axis[0] = 5000;     // LX off-center
            s.Axis[1] = 32768;    // LY centered (omitted on the wire)
            s.Axis[2] = 40000;    // LT partly pressed
            s.Axis[4] = 60000;    // RY
            s.Sliders[0] = 12345;
            s.Povs[0] = 9000;     // East
            s.Buttons[0] = true;  // A
            s.Buttons[16] = true; // touchpad click
            s.Buttons[21] = true; // a high gamepad button

            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            var rt = CustomInputStateCodec.Decode(bytes);

            AssertGamepadEqual(s, rt);
            Assert.True(rt.Buttons[0] && rt.Buttons[16] && rt.Buttons[21]);
            Assert.False(rt.Buttons[1]);
            Assert.Equal(9000, rt.Povs[0]);
            Assert.Equal(12345, rt.Sliders[0]);
        }

        [Fact]
        public void Motion_RoundTripsWhenCapabilityPresent()
        {
            var s = new CustomInputState();
            s.Gyro[0] = 1.5f; s.Gyro[1] = -2.25f; s.Gyro[2] = 0.0f;
            s.Accel[0] = 9.81f; s.Accel[1] = 0.1f; s.Accel[2] = -3.3f;

            var bytes = CustomInputStateCodec.Encode(s, WithMotion);
            var rt = CustomInputStateCodec.Decode(bytes);

            Assert.Equal(s.Gyro, rt.Gyro);
            Assert.Equal(s.Accel, rt.Accel);
        }

        [Fact]
        public void Motion_OmittedWhenCapabilityAbsent()
        {
            var s = new CustomInputState();
            s.Gyro[0] = 1.5f; s.Accel[0] = 9.81f;

            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            var rt = CustomInputStateCodec.Decode(bytes);

            // No gyro/accel capability -> blocks absent -> decoded as zeroed.
            Assert.Equal(0f, rt.Gyro[0]);
            Assert.Equal(0f, rt.Accel[0]);
        }

        [Fact]
        public void Battery_RoundTripsAndOmitsWhenUnknown()
        {
            var known = new CustomInputState { BatteryPercent = 73, BatteryCharging = true };
            var rtKnown = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(known, NoSensors));
            Assert.Equal(73, rtKnown.BatteryPercent);
            Assert.True(rtKnown.BatteryCharging);

            var unknown = new CustomInputState(); // BatteryPercent defaults to -1
            var rtUnknown = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(unknown, NoSensors));
            Assert.Equal(-1, rtUnknown.BatteryPercent);
        }

        [Fact]
        public void Midi_FullNamespaceRoundTrips()
        {
            var s = new CustomInputState { Midi = new MidiInputState() };
            s.Midi.Notes[60] = true;   // middle C
            s.Midi.Notes[127] = true;
            s.Midi.Cc[1] = 100;        // mod wheel
            s.Midi.Cc[74] = 64;
            s.Midi.CcUp[16] = true;    // encoder CW pulse
            s.Midi.CcDown[17] = true;
            s.Midi.PitchBend = 40000;

            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            var rt = CustomInputStateCodec.Decode(bytes);

            Assert.NotNull(rt.Midi);
            Assert.Equal(s.Midi.Notes, rt.Midi.Notes);
            Assert.Equal(s.Midi.Cc, rt.Midi.Cc);
            Assert.Equal(s.Midi.CcUp, rt.Midi.CcUp);
            Assert.Equal(s.Midi.CcDown, rt.Midi.CcDown);
            Assert.Equal(40000, rt.Midi.PitchBend);
        }

        [Fact]
        public void Touchpad_RoundTripsContactIdAndPositions()
        {
            var s = new CustomInputState { Touchpads = new[] { new TouchpadInputState(2) } };
            var pad = s.Touchpads[0];
            pad.Clicked = true;
            pad.FingerDown[0] = true; pad.FingerContactId[0] = 7; pad.FingerX[0] = 0.25f; pad.FingerY[0] = 0.75f; pad.FingerPressure[0] = 0.5f;
            pad.FingerDown[1] = false; pad.FingerContactId[1] = -1;

            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            var rt = CustomInputStateCodec.Decode(bytes);

            Assert.NotNull(rt.Touchpads);
            Assert.Single(rt.Touchpads);
            var rtPad = rt.Touchpads[0];
            Assert.Equal(2, rtPad.MaxFingers);
            Assert.True(rtPad.Clicked);
            Assert.True(rtPad.FingerDown[0]);
            Assert.Equal(7, rtPad.FingerContactId[0]);
            Assert.Equal(-1, rtPad.FingerContactId[1]); // up-sentinel survives
            Assert.Equal(0.25f, rtPad.FingerX[0], 3);
            Assert.Equal(0.75f, rtPad.FingerY[0], 3);
            Assert.Equal(0.5f, rtPad.FingerPressure[0], 3);
        }

        [Fact]
        public void DecodeInto_IsAbsolute_PriorStateDoesNotLeak()
        {
            // Frame 1 sets a button + axis; frame 2 (neutral) must clear them.
            var target = new CustomInputState();
            var f1 = new CustomInputState();
            f1.Buttons[3] = true; f1.Axis[0] = 1000;
            Assert.True(CustomInputStateCodec.DecodeInto(CustomInputStateCodec.Encode(f1, NoSensors), target));
            Assert.True(target.Buttons[3]);
            Assert.Equal(1000, target.Axis[0]);

            var neutral = Centered();
            Assert.True(CustomInputStateCodec.DecodeInto(CustomInputStateCodec.Encode(neutral, NoSensors), target));
            Assert.False(target.Buttons[3]);          // released, not latched
            Assert.Equal(32768, target.Axis[0]);      // back to center
        }

        [Fact]
        public void DecodeInto_ReusesTargetWithoutReallocatingMidi()
        {
            var target = new CustomInputState { Midi = new MidiInputState() };
            var existingMidi = target.Midi;
            var s = new CustomInputState { Midi = new MidiInputState() };
            s.Midi.Notes[40] = true;

            Assert.True(CustomInputStateCodec.DecodeInto(CustomInputStateCodec.Encode(s, NoSensors), target));
            Assert.Same(existingMidi, target.Midi); // pooled, not reallocated
            Assert.True(target.Midi.Notes[40]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        public void Truncated_FailsClosedToNeutral(int chop)
        {
            var s = new CustomInputState();
            s.Axis[0] = 12000; s.Buttons[2] = true; s.Midi = new MidiInputState();
            s.Midi.Cc[5] = 99;
            var full = CustomInputStateCodec.Encode(s, NoSensors);
            var truncated = full[..System.Math.Max(0, full.Length - 1 - chop)];

            var target = new CustomInputState();
            target.Buttons[9] = true; // pre-existing state to confirm it gets cleared
            bool ok = CustomInputStateCodec.DecodeInto(truncated, target);

            Assert.False(ok);
            Assert.False(target.Buttons[9]);     // reset to neutral, not left half-applied
            Assert.Equal(32768, target.Axis[0]);
        }

        [Fact]
        public void UnknownVersion_Rejected()
        {
            var bytes = CustomInputStateCodec.Encode(new CustomInputState(), NoSensors);
            bytes[0] = 0xEE; // corrupt the version byte
            Assert.False(CustomInputStateCodec.DecodeInto(bytes, new CustomInputState()));
        }

        [Fact]
        public void AxisExtremes_Saturate()
        {
            var s = new CustomInputState();
            s.Axis[6] = 65535; s.Axis[7] = 1;
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.Equal(65535, rt.Axis[6]);
            Assert.Equal(1, rt.Axis[7]);
        }
    }
}
