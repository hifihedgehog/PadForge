using System.Linq;
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
        public void AccelAux_RoundTripsWhenCapabilityPresent()
        {
            // #199: the Nunchuk / left Joy-Con accelerometer block.
            var s = new CustomInputState();
            s.AccelAux[0] = 1.25f; s.AccelAux[1] = -9.5f; s.AccelAux[2] = 0.75f;

            var bytes = CustomInputStateCodec.Encode(s, new CustomInputStateCodec.Caps(false, false, accelAux: true));
            var rt = CustomInputStateCodec.Decode(bytes);

            Assert.Equal(s.AccelAux, rt.AccelAux);
        }

        [Fact]
        public void AccelAux_OmittedWhenCapabilityAbsent()
        {
            var s = new CustomInputState();
            s.AccelAux[0] = 9.81f;

            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            var rt = CustomInputStateCodec.Decode(bytes);

            Assert.Equal(0f, rt.AccelAux[0]);
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

        // ── Post-3.5.0 families (#146 IR / #151 NIR / #154 JC2 mouse) ────────

        [Fact]
        public void WiiIr_RoundTripsWhenDetected()
        {
            var s = new CustomInputState();
            s.Ir = new WiiIrState { X = -0.5f, Y = 0.25f, Detected = true };
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.True(rt.Ir.Detected);
            Assert.Equal(-0.5f, rt.Ir.X);
            Assert.Equal(0.25f, rt.Ir.Y);
        }

        [Fact]
        public void WiiIr_OmittedWhenNotDetected_XYDoNotLeak()
        {
            // Producer contract: X/Y are only valid while Detected. An
            // undetected frame must not ship stale coordinates.
            var s = new CustomInputState();
            s.Ir = new WiiIrState { X = 0.9f, Y = 0.9f, Detected = false };
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.False(rt.Ir.Detected);
            Assert.Equal(0f, rt.Ir.X);
            Assert.Equal(0f, rt.Ir.Y);
        }

        [Fact]
        public void JoyConIrAndMouse_RoundTrip()
        {
            var s = new CustomInputState();
            s.JoyConIrIntensity = 0.75f;
            s.JoyCon2MouseDX = -13f;
            s.JoyCon2MouseDY = 42f;
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.Equal(0.75f, rt.JoyConIrIntensity);
            Assert.Equal(-13f, rt.JoyCon2MouseDX);
            Assert.Equal(42f, rt.JoyCon2MouseDY);
        }

        [Fact]
        public void MouseRaw_RoundTripsAndOmitsWhenIdle()
        {
            // #200: unclamped Raw Input counts ride their own tail block.
            var s = new CustomInputState();
            s.MouseRawDX = -300;
            s.MouseRawDY = 70000; // deliberately past int16 to pin the int32 width
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.Equal(-300, rt.MouseRawDX);
            Assert.Equal(70000, rt.MouseRawDY);

            // Idle mouse sends no block and decodes back to zero.
            var idle = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(Centered(), NoSensors));
            Assert.Equal(0, idle.MouseRawDX);
            Assert.Equal(0, idle.MouseRawDY);
        }

        [Fact]
        public void MouseRaw_CloneCarriesTheCounts()
        {
            var s = new CustomInputState { MouseRawDX = 17, MouseRawDY = -4 };
            var c = s.Clone();
            Assert.Equal(17, c.MouseRawDX);
            Assert.Equal(-4, c.MouseRawDY);
        }

        [Fact]
        public void MouseRaw_OldFrameClearsStaleTargetState()
        {
            // Absolute-frame contract extends to the MouseRaw block.
            var target = new CustomInputState { MouseRawDX = 55, MouseRawDY = -9 };
            Assert.True(CustomInputStateCodec.DecodeInto(
                CustomInputStateCodec.Encode(Centered(), NoSensors), target));
            Assert.Equal(0, target.MouseRawDX);
            Assert.Equal(0, target.MouseRawDY);
        }

        [Fact]
        public void NonFiniteSensorFloat_FailsClosedToNeutral()
        {
            // Consumer-side EMA smoothing (gyro, IR) latches NaN permanently
            // once poisoned, so the decoder rejects non-finite floats and
            // resets rather than half-applying (mirrors OutputEffectCodec's
            // HapticTone finiteness gate).
            var s = new CustomInputState();
            s.Gyro[1] = float.NaN;
            var bytes = CustomInputStateCodec.Encode(s, WithMotion);
            var target = new CustomInputState();
            target.Buttons[4] = true; // pre-existing state to confirm the reset
            Assert.False(CustomInputStateCodec.DecodeInto(bytes, target));
            Assert.Equal(0f, target.Gyro[1]);
            Assert.False(target.Buttons[4]);

            var ir = new CustomInputState();
            ir.Ir = new WiiIrState { X = float.PositiveInfinity, Y = 0f, Detected = true };
            Assert.False(CustomInputStateCodec.DecodeInto(
                CustomInputStateCodec.Encode(ir, NoSensors), target));
            Assert.False(target.Ir.Detected);
        }

        [Fact]
        public void Battery_DecodeClampsOutOfContractPercent()
        {
            // A version-skewed peer could ship 101..127 / -128..-2; decode
            // clamps to the encoder's [-1, 100] contract.
            var s = new CustomInputState { BatteryPercent = 50 };
            var bytes = CustomInputStateCodec.Encode(s, NoSensors);
            bytes[^2] = unchecked((byte)(sbyte)120); // battery block is percent + charging at the tail
            var rt = CustomInputStateCodec.Decode(bytes);
            Assert.Equal(100, rt.BatteryPercent);

            bytes[^2] = unchecked((byte)(sbyte)-77);
            rt = CustomInputStateCodec.Decode(bytes);
            Assert.Equal(-1, rt.BatteryPercent);
        }

        [Fact]
        public void NewFamilies_NeutralStateStillEncodesToThreeBytes()
        {
            // The compactness contract survives the new blocks: an idle pad
            // (no IR dot, camera off, mouse still) sends none of them.
            var bytes = CustomInputStateCodec.Encode(Centered(), NoSensors);
            Assert.Equal(3, bytes.Length);
        }

        [Fact]
        public void OldFrame_ClearsStaleNewFamilyStateInTarget()
        {
            // A pre-#146 peer's frame carries no new blocks. Decoding it into
            // a target holding stale IR/mouse values must clear them (the
            // absolute-frame contract extends to the new families).
            var target = new CustomInputState();
            target.Ir = new WiiIrState { X = 1f, Y = 1f, Detected = true };
            target.JoyConIrIntensity = 1f;
            target.JoyCon2MouseDX = 5f; target.JoyCon2MouseDY = 5f;

            Assert.True(CustomInputStateCodec.DecodeInto(
                CustomInputStateCodec.Encode(Centered(), NoSensors), target));
            Assert.False(target.Ir.Detected);
            Assert.Equal(0f, target.JoyConIrIntensity);
            Assert.Equal(0f, target.JoyCon2MouseDX);
            Assert.Equal(0f, target.JoyCon2MouseDY);
        }

        [Fact]
        public void TrailingBytes_Tolerated()
        {
            // This is the mechanism that makes appended blocks mixed-version
            // safe: an old decoder treats a newer frame's extra blocks as a
            // tail it never reads. Pin the tolerance.
            var s = new CustomInputState();
            s.Axis[0] = 12000;
            var full = CustomInputStateCodec.Encode(s, NoSensors);
            var padded = new byte[full.Length + 10];
            full.CopyTo(padded, 0);
            var target = new CustomInputState();
            Assert.True(CustomInputStateCodec.DecodeInto(padded, target));
            Assert.Equal(12000, target.Axis[0]);
        }

        [Fact]
        public void MalformedFrame_ResetsNewFamiliesAndMidiToNeutral()
        {
            var target = new CustomInputState { Midi = new MidiInputState() };
            target.Ir = new WiiIrState { X = 1f, Y = 1f, Detected = true };
            target.JoyCon2MouseDX = 9f;
            target.Midi.Notes[10] = true;
            target.Midi.PitchBend = 60000;

            Assert.False(CustomInputStateCodec.DecodeInto(new byte[] { 1, 0 }, target));
            Assert.False(target.Ir.Detected);
            Assert.Equal(0f, target.JoyCon2MouseDX);
            Assert.False(target.Midi.Notes[10]);
            Assert.Equal(MidiInputState.PitchBendCenter, target.Midi.PitchBend);
        }

        [Fact]
        public void BatteryCharging_SurvivesUnknownPercent()
        {
            var s = new CustomInputState { BatteryPercent = -1, BatteryCharging = true };
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.True(rt.BatteryCharging);
            Assert.Equal(-1, rt.BatteryPercent);
        }

        [Fact]
        public void CapSense_RoundTrips_AndOmitsTheUntouchedFrame()
        {
            // Touched channels ride the one-byte bitmask block (v26).
            var s = new CustomInputState { CapSense = new bool[4] };
            s.CapSense[0] = true;  // left stick top
            s.CapSense[3] = true;  // right grip
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.NotNull(rt.CapSense);
            Assert.True(rt.CapSense[0]);
            Assert.False(rt.CapSense[1]);
            Assert.False(rt.CapSense[2]);
            Assert.True(rt.CapSense[3]);

            // All-untouched omits the block: an omitted block decodes to
            // "nothing touched" (null array reads false everywhere),
            // exactly the neutral the encoder skipped.
            var idle = new CustomInputState { CapSense = new bool[4] };
            var rtIdle = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(idle, NoSensors));
            Assert.Null(rtIdle.CapSense);

            // A malformed follow-up frame resets a previously-touched
            // target to neutral (the decode contract).
            var target = new CustomInputState { CapSense = new bool[4] };
            target.CapSense[2] = true;
            Assert.False(CustomInputStateCodec.DecodeInto(new byte[] { 1, 0 }, target));
            Assert.All(target.CapSense, b => Assert.False(b));
        }

        /// <summary>
        /// Mirror-surface tripwire (code-audit lens 1m). CustomInputState's
        /// public field list must exactly match the set the codec knowingly
        /// carries. Adding a field to the class makes this test FAIL until
        /// the field is wired into Encode + DecodeInto + ResetToNeutral +
        /// Clone, or explicitly excluded here with a comment saying why.
        /// An unwired field is exactly how the #146/#151/#154 fields shipped
        /// dead over Remote Link for a month without any test noticing.
        /// </summary>
        [Fact]
        public void NfcTag_RoundTrips_AndOmitsTheIdleFrame()
        {
            // Span 5 (Any + 4 tag buttons); buttons 0 and 3 held.
            var s = new CustomInputState { NfcTag = new bool[5] };
            s.NfcTag[0] = true;  // Any NFC Tag
            s.NfcTag[3] = true;  // a registered tag
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.NotNull(rt.NfcTag);
            Assert.True(rt.NfcTag[0]);
            Assert.False(rt.NfcTag[1]);
            Assert.False(rt.NfcTag[2]);
            Assert.True(rt.NfcTag[3]);

            // An all-clear NFC array is omitted, so it decodes back to null,
            // exactly the neutral the encoder skipped (the CapSense contract).
            var idle = new CustomInputState { NfcTag = new bool[5] };
            var rtIdle = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(idle, NoSensors));
            Assert.Null(rtIdle.NfcTag);
        }

        [Fact]
        public void NfcTag_CarriesTheHighestButton255()
        {
            // The registry allows buttons 1..255 (a 256-element array).
            // The span byte stores span-1 so index 255 survives the wire
            // (Codex #3: a naive 255 clamp dropped it).
            var s = new CustomInputState { NfcTag = new bool[256] };
            s.NfcTag[255] = true;
            var rt = CustomInputStateCodec.Decode(CustomInputStateCodec.Encode(s, NoSensors));
            Assert.NotNull(rt.NfcTag);
            Assert.Equal(256, rt.NfcTag.Length);
            Assert.True(rt.NfcTag[255]);
            Assert.False(rt.NfcTag[0]);
        }

        [Fact]
        public void EveryStateField_IsAccountedForByTheCodec()
        {
            var known = new[]
            {
                "Axis", "Sliders", "Povs", "Buttons", "Gyro", "Accel",
                "AccelAux",
                // #252 aux gyro (SDL_SENSOR_GYRO_L, left Joy-Con of a pair):
                // wired into Encode / DecodeInto through the EXTENSION tail
                // (the u16 presence mask was full at Block.Nfc), plus
                // ResetToNeutral and Clone.
                "GyroAux",
                "Touchpads", "Midi", "Ir", "JoyConIrIntensity",
                "JoyCon2MouseDX", "JoyCon2MouseDY",
                "MouseRawDX", "MouseRawDY",
                "BatteryPercent", "BatteryCharging",
                // v26 capsense (the fork's SDL_GetGamepadCapSense): wired
                // into Encode (Block.CapSense bitmask byte), DecodeInto,
                // ResetToNeutral, and Clone.
                "CapSense",
                // #241 NFC tag buttons (the fork's SDL_GetGamepadNfcTagUid):
                // wired into Encode (Block.Nfc span + bitmask), DecodeInto,
                // ResetToNeutral, and Clone.
                "NfcTag",
            };
            var actual = typeof(CustomInputState)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => f.Name)
                .OrderBy(n => n)
                .ToArray();
            Assert.Equal(known.OrderBy(n => n).ToArray(), actual);
        }
    }
}
