using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Haptics;
using PadForge.Models2D;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SInput support (#584, discussion #578): SInput pads as input devices,
    /// and the packer behind the HIDMaestro <c>sinput</c> persona.
    ///
    /// <para>The wire side is checked against a port of SDL's own SInput code
    /// at the fork PadForge ships, 51470ad786: the feature reply parse
    /// (SDL_hidapi_sinput.c:437-557), the style decode (:274-434), the report
    /// decode (:839-1052) and the mapping builder (SDL_gamepad.c:848-1069).
    /// The persona's feature reply is the one HIDMaestro#75 specifies, so the
    /// packer, the reply and SDL's mapping are checked against one another
    /// rather than against this repo's reading of them.</para>
    /// </summary>
    public class SInputSupportTests
    {
        private const string P = "sinput";

        // ── identity ───────────────────────────────────────────────────

        /// <summary>SDL_IsJoystickSInputController, SDL_joystick.c:4469-4485,
        /// with the IDs at usb_ids.h:30, 94 and 313-323.</summary>
        [Theory]
        [InlineData(0x2E8A, 0x10C6, true)]   // generic SInput
        [InlineData(0x2E8A, 0x10DF, true)]   // ProGCC
        [InlineData(0x2E8A, 0x10DD, true)]   // GC Ultimate
        [InlineData(0x2E8A, 0x10E0, true)]   // Bonziri FireBird
        [InlineData(0x2E8A, 0x10E5, true)]   // Void Gaming PS4 FireBird
        [InlineData(0x38D2, 0x1014, true)]   // Void Gaming Genesis
        [InlineData(0x2E8A, 0x10C7, false)]
        [InlineData(0x2E8A, 0x0003, false)]
        [InlineData(0x38D2, 0x1015, false)]
        [InlineData(0x057E, 0x2009, false)]
        public void Identity_IsSdlsSInputList(int vid, int pid, bool want)
            => Assert.Equal(want, SInputControllerIdentity.IsSInput(vid, pid));

        // ── a port of SDL's SInput code ────────────────────────────────

        /// <summary>The feature reply HIDMaestro#75 specifies for the
        /// persona, report ID at byte 0.</summary>
        internal static byte[] PersonaFeatureReply()
        {
            var r = new byte[64];
            r[0] = 0x02; r[1] = 0x02;          // report 0x02, the features command
            r[2] = 0x01; r[3] = 0x00;          // protocol version 1
            r[4] = 0xFD;                       // rumble, accel, gyro, both sticks, both analog triggers
            r[5] = 0x01;                       // touchpads (no RGB: SDL sends a color once, the capture lapses)
            r[6] = 0x01;                       // SDL_GAMEPAD_TYPE_STANDARD
            r[7] = 0x20;                       // face style ABXY
            r[8] = 0xE8; r[9] = 0x03;          // 1000 µs
            r[10] = 0x08; r[11] = 0x00;        // ±8 g
            r[12] = 0xD0; r[13] = 0x07;        // ±2000 deg/s
            r[14] = 0xFF; r[15] = 0xFF; r[16] = 0xFF; r[17] = 0x00;
            r[18] = 0x02; r[19] = 0x01;        // two touchpads, one finger each
            // The serial: 2E 8A 10 C6 00, then the controller's index (0 here).
            r[20] = 0x2E; r[21] = 0x8A; r[22] = 0x10; r[23] = 0xC6; r[24] = 0x00; r[25] = 0x00;
            return r;
        }

        /// <summary>The device SDL builds from a feature reply.</summary>
        internal sealed class SdlSInput
        {
            public bool LeftStick, RightStick, LeftTrigger, RightTrigger, Accel, Gyro, Touchpad, Rumble, PlayerLeds, Rgb;
            public int TouchpadCount, FingerCount, PollingUs, AccelRange, GyroRange;
            public readonly byte[] Masks = new byte[4];
            public bool Dpad;
            /// <summary>Mapping element to joystick button index.</summary>
            public readonly Dictionary<string, int> Buttons = new();

            /// <summary>ProcessSDLFeaturesResponse (SDL_hidapi_sinput.c:437-557)
            /// with DeviceDynamicEncodingSetup (:274-434) and the mapping
            /// builder (SDL_gamepad.c:848-1069). The reply's report ID is byte
            /// 0, and SDL parses from byte 2 (:599-600).</summary>
            public static SdlSInput FromReply(byte[] reply)
            {
                var s = new SdlSInput();
                var d = reply.AsSpan(2);
                s.Rumble = (d[2] & 0x01) != 0;
                s.PlayerLeds = (d[2] & 0x02) != 0;
                s.Accel = (d[2] & 0x04) != 0;
                s.Gyro = (d[2] & 0x08) != 0;
                s.LeftStick = (d[2] & 0x10) != 0;
                s.RightStick = (d[2] & 0x20) != 0;
                s.LeftTrigger = (d[2] & 0x40) != 0;
                s.RightTrigger = (d[2] & 0x80) != 0;
                s.Touchpad = (d[3] & 0x01) != 0;
                s.Rgb = (d[3] & 0x02) != 0;
                s.PollingUs = d[6] | (d[7] << 8);
                s.AccelRange = d[8] | (d[9] << 8);
                s.GyroRange = d[10] | (d[11] << 8);
                byte[] usage = { d[12], d[13], d[14], d[15] };
                s.TouchpadCount = d[16];
                s.FingerCount = d[17];

                // DeviceDynamicEncodingSetup, :274-434.
                byte[] mask = new byte[4];
                mask[0] = 0xFF;
                mask[2] |= 0x01;   // start
                bool lb = (usage[1] & 0x04) != 0, rb = (usage[1] & 0x08) != 0;
                int bumpers = lb && rb ? 2 : lb || rb ? 1 : 0;
                if (lb && rb) mask[1] |= 0x0C;
                else if (lb) mask[1] |= 0x04;
                else if (rb) mask[1] |= 0x08;
                bool digital = (usage[1] & 0x30) != 0;
                bool analog = s.LeftTrigger || s.RightTrigger;
                bool t1 = (usage[2] & 0x40) != 0, t2 = (usage[2] & 0x80) != 0;
                bool leftStick = false, rightStick = false;
                if (s.LeftStick && s.RightStick) { mask[1] |= 0x03; leftStick = rightStick = true; }
                else if (s.LeftStick) { mask[1] |= 0x01; leftStick = true; }
                else if (s.RightStick) { mask[1] |= 0x02; rightStick = true; }
                bool dualStage = analog && digital;
                bool digitalOnly = !analog && digital;
                if (digital) mask[1] |= 0x30;
                bool pg1 = (usage[1] & 0xC0) != 0, pg2 = (usage[2] & 0x30) != 0;
                int paddlePairs = pg1 && pg2 ? 2 : pg1 ? 1 : 0;
                if (paddlePairs >= 1) mask[1] |= 0xC0;
                if (paddlePairs == 2) mask[2] |= 0x30;
                bool back = (usage[2] & 0x02) != 0, guide = (usage[2] & 0x04) != 0, share = (usage[2] & 0x08) != 0;
                int meta = share ? 3 : guide ? 2 : back ? 1 : 0;
                if (meta == 3) mask[2] |= 0x0E;
                else if (meta == 2) mask[2] |= 0x06;
                else if (meta == 1) mask[2] |= 0x02;
                int touch = t1 && t2 ? 2 : t1 ? 1 : 0;
                if (touch == 2) mask[2] |= 0xC0;
                else if (touch == 1) mask[2] |= 0x40;
                int miscButtons = (usage[3] & 0x0F) switch { 0x0F => 4, 0x07 => 3, 0x03 => 2, 0x01 => 1, _ => 0 };
                mask[3] = miscButtons switch { 4 => 0x0F, 3 => 0x07, 2 => 0x03, 1 => 0x01, _ => 0 };
                Array.Copy(mask, s.Masks, 4);

                // The D-pad becomes the hat (:543-552).
                if ((s.Masks[0] & 0xF0) == 0xF0) { s.Dpad = true; s.Masks[0] &= 0x0F; }

                // SDL_SInputStylesMapExtraction, SDL_gamepad.c:848-1069.
                int b = 0;
                int miscButton = 2, miscEnd = 2 + miscButtons;
                void Add(string name) => s.Buttons[name] = b++;
                Add("a"); Add("b"); Add("x"); Add("y");
                if (leftStick) Add("leftstick");
                if (rightStick) Add("rightstick");
                if (bumpers > 0) Add("leftshoulder");
                if (bumpers > 1) Add("rightshoulder");
                if (digitalOnly) { Add("lefttrigger"); Add("righttrigger"); }
                else if (dualStage)
                {
                    if (miscEnd < 3) miscEnd = 3;
                    Add("misc" + miscEnd++);
                    Add("misc" + miscEnd++);
                }
                if (paddlePairs > 0) { Add("paddle2"); Add("paddle1"); }
                Add("start");
                if (meta >= 1) Add("back");
                if (meta >= 2) Add("guide");
                if (meta == 3) Add("misc1");
                if (paddlePairs > 1) { Add("paddle4"); Add("paddle3"); }
                if (touch >= 1) Add("touchpad");
                if (touch == 2) Add("misc" + miscEnd++);
                for (int i = 0; i < miscButtons; i++) Add("misc" + miscButton++);
                return s;
            }

            /// <summary>The mapping string SDL_GetGamepadMapping returns, the
            /// buttons only, in the builder's order.</summary>
            public string MappingString() =>
                "03000000000000000000000000000000,SInput,face:abxy,"
                + string.Concat(Buttons.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}:b{kv.Value},"))
                + "platform:Windows,";

            /// <summary>The mapping elements whose joystick button a report
            /// presses: HandleStatePacket's loop (SDL_hidapi_sinput.c:848-870)
            /// numbers the enabled bits in order.</summary>
            public HashSet<string> Pressed(byte[] report)
            {
                var down = new HashSet<int>();
                int idx = 0;
                for (int by = 0; by < 4; by++)
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((Masks[by] & (1 << bit)) == 0) continue;
                        if ((report[3 + by] & (1 << bit)) != 0) down.Add(idx);
                        idx++;
                    }
                return Buttons.Where(kv => down.Contains(kv.Value)).Select(kv => kv.Key).ToHashSet();
            }

            /// <summary>The hat, from byte 3's D-pad bits (:872-886).</summary>
            public (bool up, bool down, bool left, bool right) Hat(byte[] report)
                => ((report[3] & 0x10) != 0, (report[3] & 0x20) != 0, (report[3] & 0x40) != 0, (report[3] & 0x80) != 0);

            private static short S16(byte[] r, int i) => (short)(r[i] | (r[i + 1] << 8));

            /// <summary>The gyroscope in SDL's frame, rad/s (:986-999, scale :260-263).</summary>
            public (float x, float y, float z) Gyro3(byte[] r)
            {
                float k = MathF.PI / 180f / (32768f / GyroRange);
                return (-S16(r, 29) * k, S16(r, 33) * k, -S16(r, 31) * k);
            }

            /// <summary>The accelerometer in SDL's frame, m/s² (:1001-1013, scale :266-269).</summary>
            public (float x, float y, float z) Accel3(byte[] r)
            {
                float k = 9.80665f / (32768f / AccelRange);
                return (-S16(r, 23) * k, S16(r, 27) * k, -S16(r, 25) * k);
            }

            /// <summary>One touchpad's finger (:1017-1049).</summary>
            public (bool down, float x, float y, float pressure) Touch(byte[] r, int pad)
            {
                int o = pad == 0 ? 35 : 41;
                short x = S16(r, o), y = S16(r, o + 2);
                ushort p = (ushort)(r[o + 4] | (r[o + 5] << 8));
                return (p > 0, x / 65536f + 0.5f, y / 65536f + 0.5f, p / 32768f);
            }

            public static short Axis(byte[] r, int offset) => S16(r, offset);
        }

        private static RawHidState RestRaw()
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Povs[0] = -1;
            raw.Axes[ValveReportPackers.AxLT] = raw.Axes[ValveReportPackers.AxRT] = short.MinValue;
            return raw;
        }

        private static byte[] Pack(RawHidState raw, TouchpadState tp = default, MotionSnapshot motion = default,
            int battery = -1, bool charging = false, uint micros = 0)
        {
            var dest = new byte[SInputReportPacker.ReportSize];
            SInputReportPacker.Pack(raw, tp, motion, battery, charging, micros, dest);
            return dest;
        }

        // ── the persona's feature reply, read by SDL ───────────────────

        [Fact]
        public void FeatureReply_GivesSdlOpenPucksShape()
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            Assert.True(sdl.Rumble && sdl.Accel && sdl.Gyro && sdl.LeftStick && sdl.RightStick && sdl.LeftTrigger && sdl.RightTrigger);
            Assert.False(sdl.PlayerLeds);
            Assert.True(sdl.Touchpad);
            Assert.False(sdl.Rgb);
            Assert.Equal(1000, sdl.PollingUs);
            Assert.Equal(SInputReportPacker.AccelRangeG, sdl.AccelRange);
            Assert.Equal(SInputReportPacker.GyroRangeDps, sdl.GyroRange);
            Assert.Equal((2, 1), (sdl.TouchpadCount, sdl.FingerCount));
            Assert.True(sdl.Dpad);
            // SDL_gamepad.c:848-1069 for these masks, in order.
            Assert.Equal(new[]
                {
                    "a", "b", "x", "y", "leftstick", "rightstick", "leftshoulder", "rightshoulder",
                    "misc3", "misc4", "paddle2", "paddle1", "start", "back", "guide", "misc1",
                    "paddle4", "paddle3", "touchpad", "misc5",
                },
                sdl.Buttons.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray());
        }

        // ── the packer, read back through SDL ──────────────────────────

        /// <summary>Every row of the SInput wire table presses exactly the
        /// SDL gamepad button its role names, through the persona's reply and
        /// SDL's decode.</summary>
        [Theory]
        [InlineData("ButtonA", "a")]
        [InlineData("ButtonB", "b")]
        [InlineData("ButtonX", "x")]
        [InlineData("ButtonY", "y")]
        [InlineData("LeftThumbButton", "leftstick")]
        [InlineData("RightThumbButton", "rightstick")]
        [InlineData("LeftShoulder", "leftshoulder")]
        [InlineData("RightShoulder", "rightshoulder")]
        [InlineData("Paddle1", "paddle1")]
        [InlineData("Paddle2", "paddle2")]
        [InlineData("Paddle3", "paddle3")]
        [InlineData("Paddle4", "paddle4")]
        [InlineData("ButtonStart", "start")]
        [InlineData("ButtonBack", "back")]
        [InlineData("ButtonGuide", "guide")]
        [InlineData("ButtonShare", "misc1")]
        [InlineData("LeftTouchpadClick", "touchpad")]
        [InlineData("RightTouchpadClick", "misc5")]
        public void Packer_EveryRolePressesItsSdlButton(string role, string sdlButton)
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            Assert.Empty(sdl.Pressed(Pack(RestRaw())));
            var raw = RestRaw();
            int i = NintendoPreviewMap.IndexOf(P, role);
            Assert.True(i >= 0, role);
            raw.SetButton(i, true);
            Assert.Equal(new[] { sdlButton }, sdl.Pressed(Pack(raw)).ToArray());
        }

        [Theory]
        [InlineData("DPadUp")]
        [InlineData("DPadDown")]
        [InlineData("DPadLeft")]
        [InlineData("DPadRight")]
        public void Packer_DPadButtonsAreSdlsHat(string role)
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            var raw = RestRaw();
            raw.SetButton(NintendoPreviewMap.IndexOf(P, role), true);
            var r = Pack(raw);
            Assert.Empty(sdl.Pressed(r));
            var hat = sdl.Hat(r);
            Assert.Equal((role == "DPadUp", role == "DPadDown", role == "DPadLeft", role == "DPadRight"), hat);
        }

        /// <summary>The trigger clicks set at the Valve packers' pull and
        /// read as SDL's dual-stage Misc 3 and Misc 4.</summary>
        [Fact]
        public void Packer_TriggerPullsPressMisc3AndMisc4AtTheValveThreshold()
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            var raw = RestRaw();
            // The raw trigger is bipolar, and the threshold is on the 0..32767 pull.
            raw.Axes[ValveReportPackers.AxLT] = (short)(ValveReportPackers.TriggerClickPull * 2 - 32768 - 2);
            raw.Axes[ValveReportPackers.AxRT] = short.MaxValue;
            Assert.Equal(new[] { "misc4" }, sdl.Pressed(Pack(raw)).ToArray());
            raw.Axes[ValveReportPackers.AxLT] = (short)(ValveReportPackers.TriggerClickPull * 2 - 32768);
            Assert.Equal(new[] { "misc3", "misc4" }, sdl.Pressed(Pack(raw)).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void Packer_SticksAndTriggersPassThroughAsSdlReadsThem()
        {
            var raw = RestRaw();
            raw.Axes[ValveReportPackers.AxLX] = -12345;
            raw.Axes[ValveReportPackers.AxLY] = 23456;     // down
            raw.Axes[ValveReportPackers.AxRX] = 32767;
            raw.Axes[ValveReportPackers.AxRY] = -32768;    // up
            var r = Pack(raw);
            Assert.Equal(-12345, SdlSInput.Axis(r, 7));
            Assert.Equal(23456, SdlSInput.Axis(r, 9));
            Assert.Equal(32767, SdlSInput.Axis(r, 11));
            Assert.Equal(-32768, SdlSInput.Axis(r, 13));
            // A trigger at rest reads SDL's rest, and a full pull its full.
            Assert.Equal(short.MinValue, SdlSInput.Axis(r, 15));
            Assert.Equal(short.MinValue, SdlSInput.Axis(r, 17));
            raw.Axes[ValveReportPackers.AxLT] = short.MaxValue;
            raw.Axes[ValveReportPackers.AxRT] = 0;
            r = Pack(raw);
            Assert.Equal(short.MaxValue, SdlSInput.Axis(r, 15));
            Assert.Equal(0, SdlSInput.Axis(r, 17));
        }

        /// <summary>MotionSnapshot is SDL's frame in g and deg/s, and SDL's
        /// SInput decode hands it back in that frame in m/s² and rad/s.</summary>
        [Fact]
        public void Packer_MotionComesBackInSdlsFrame()
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            var motion = new MotionSnapshot
            {
                AccelX = 0.25f, AccelY = -1.0f, AccelZ = 0.5f,
                GyroPitch = 100f, GyroYaw = -250f, GyroRoll = 30f,
                HasMotion = true,
            };
            var r = Pack(RestRaw(), motion: motion, micros: 123456789);
            var g = sdl.Gyro3(r);
            float rad = MathF.PI / 180f;
            float gyroLsb = SInputReportPacker.GyroRangeDps / 32768f * rad;
            Assert.InRange(g.x - motion.GyroPitch * rad, -gyroLsb, gyroLsb);
            Assert.InRange(g.y - motion.GyroYaw * rad, -gyroLsb, gyroLsb);
            Assert.InRange(g.z - motion.GyroRoll * rad, -gyroLsb, gyroLsb);
            var a = sdl.Accel3(r);
            float accelLsb = SInputReportPacker.AccelRangeG / 32768f * 9.80665f;
            Assert.InRange(a.x - motion.AccelX * 9.80665f, -accelLsb, accelLsb);
            Assert.InRange(a.y - motion.AccelY * 9.80665f, -accelLsb, accelLsb);
            Assert.InRange(a.z - motion.AccelZ * 9.80665f, -accelLsb, accelLsb);
            Assert.Equal(123456789u, BitConverter.ToUInt32(r, 19));
        }

        [Fact]
        public void Packer_TouchSurfaceSplitsIntoTheTwoTouchpads()
        {
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            var tp = new TouchpadState { Down0 = true, X0 = 0.25f, Y0 = 0.75f, Down1 = true, X1 = 1f, Y1 = 0f };
            var r = Pack(RestRaw(), tp);
            var left = sdl.Touch(r, 0);
            var right = sdl.Touch(r, 1);
            Assert.True(left.down && right.down);
            float tol = 1f / 32768f;
            Assert.InRange(left.x - 0.25f, -tol, tol);
            Assert.InRange(left.y - 0.75f, -tol, tol);
            Assert.InRange(right.x - 1f, -tol, tol);
            Assert.InRange(right.y - 0f, -tol, tol);
            Assert.InRange(left.pressure, 0.999f, 1f);

            var up = Pack(RestRaw());
            Assert.False(sdl.Touch(up, 0).down);
            Assert.False(sdl.Touch(up, 1).down);
        }

        [Theory]
        [InlineData(-1, false, 0, 0)]                                   // not reported
        [InlineData(55, false, SInputReportPacker.PlugOnBattery, 55)]
        [InlineData(55, true, SInputReportPacker.PlugCharging, 55)]
        [InlineData(100, true, SInputReportPacker.PlugCharged, 100)]
        [InlineData(140, false, SInputReportPacker.PlugOnBattery, 100)]
        public void Packer_CarriesTheSlotsBattery(int pct, bool charging, int plug, int charge)
        {
            var r = Pack(RestRaw(), battery: pct, charging: charging);
            Assert.Equal(SInputReportPacker.ReportId, r[0]);
            Assert.Equal(plug, r[1]);
            Assert.Equal(charge, r[2]);
        }

        // ── the second touchpad's click on a real SInput pad ───────────

        /// <summary>Feature replies for three pad shapes, run through the
        /// port of SDL's builder, and the click position the wrapper reads.
        /// OpenPuck's shape (mode_sinput.cpp:155-185) puts the second click on
        /// Misc 5, a pad with a power button and digital-free analog triggers
        /// on Misc 3, and a pad with neither on Misc 2.</summary>
        [Theory]
        [InlineData(0xFD, 0xFF, 0xFF, 0x00, 20)]   // OpenPuck: dual-stage triggers, two touchpads
        [InlineData(0xFD, 0xCF, 0xFF, 0x01, 18)]   // analog triggers only, a power button
        [InlineData(0xFD, 0xCF, 0xFF, 0x00, 17)]   // analog triggers only, no misc
        public void SecondTouchpadClick_IsReadWhereSdlMapsIt(byte ff1, byte mask1, byte mask2, byte mask3, int position)
        {
            var reply = PersonaFeatureReply();
            reply[4] = ff1;
            reply[15] = mask1;
            reply[16] = mask2;
            reply[17] = mask3;
            var sdl = SdlSInput.FromReply(reply);
            Assert.Equal(position, SdlDeviceWrapper.SInputSecondTouchpadClickPosition(sdl.MappingString()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("030000,Pad,a:b0,b:b1,platform:Windows,")]                   // no touchpad
        [InlineData("030000,Pad,touchpad:b4,leftstick:b5,platform:Windows,")]     // next button is no misc
        [InlineData("030000,Pad,touchpad:b4,misc1:b5,platform:Windows,")]         // Misc 1 is never a pad click
        public void SecondTouchpadClick_FallsBackWithoutAMiscAfterTheTouchpad(string mapping)
            => Assert.Equal(-1, SdlDeviceWrapper.SInputSecondTouchpadClickPosition(mapping));

        // ── HD haptics: the type 1 frame ───────────────────────────────

        [Fact]
        public void HapticFrame_IsTheLibrarysPackedStruct()
        {
            var f = HapticToneEncoder.EncodeSInputHaptics(160f, 1f, 0.5f);
            Assert.Equal(48, f.Length);
            Assert.Equal(new byte[] { 0x03, 0x01, 0x01 }, f.Take(3).ToArray());
            ushort U(int i) => (ushort)(f[i] | (f[i + 1] << 8));
            Assert.Equal(160, U(3));      // left frequency 1
            Assert.Equal(65535, U(5));    // left amplitude 1
            Assert.Equal(0, U(7));        // left band 2
            Assert.Equal(0, U(9));
            Assert.Equal(160, U(11));     // right frequency 1, set though only the left drives HOJA
            Assert.Equal(32768, U(13));   // right amplitude 1
            Assert.Equal(0, U(15));
            Assert.Equal(0, U(17));
            Assert.All(f.Skip(19), b => Assert.Equal(0, b));

            // OpenPuck plays the louder band per side (mode_sinput.cpp:292-299).
            Assert.Equal(65535, Math.Max(U(5), U(9)));
            Assert.Equal(32768, Math.Max(U(13), U(17)));
        }

        [Fact]
        public void HapticFrame_WithBothSidesSilentIsTheAllZeroStop()
        {
            var f = HapticToneEncoder.EncodeSInputHaptics(440f, 0f, 0f);
            Assert.Equal(new byte[] { 0x03, 0x01, 0x01 }, f.Take(3).ToArray());
            Assert.All(f.Skip(3), b => Assert.Equal(0, b));
        }

        [Theory]
        [InlineData(440f, 440f)]
        [InlineData(2000f, 1000f)]
        [InlineData(5000f, 1250f)]
        [InlineData(20f, 80f)]
        [InlineData(float.NaN, HapticToneEncoder.SInputFreqMin)]
        [InlineData(float.PositiveInfinity, HapticToneEncoder.SInputFreqMin)]
        [InlineData(-5f, HapticToneEncoder.SInputFreqMin)]
        public void HapticFrequency_FoldsIntoTheBandByOctaves(float hz, float want)
            => Assert.Equal(want, HapticToneEncoder.FoldSInputFrequency(hz));

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(-1f, 0)]
        [InlineData(float.NaN, 0)]
        [InlineData(0.5f, 32768)]
        [InlineData(1f, 65535)]
        [InlineData(3f, 65535)]
        public void HapticAmplitude_IsOver65535(float amp, int want)
            => Assert.Equal(want, HapticToneEncoder.SInputAmplitude(amp));

        [Fact]
        public void ToneService_TakesSInputPadsAsAHapticFamily()
        {
            Assert.True(HapticToneService.DeviceHasHaptics(new Engine.Data.UserDevice { VendorId = 0x2E8A, ProdId = 0x10C6 }));
            Assert.True(HapticToneService.DeviceHasHaptics(new Engine.Data.UserDevice { VendorId = 0x38D2, ProdId = 0x1014 }));
            Assert.False(HapticToneService.DeviceHasHaptics(new Engine.Data.UserDevice { VendorId = 0x2E8A, ProdId = 0x10C7 }));
        }

        // ── the wire table ─────────────────────────────────────────────

        [Fact]
        public void WireFamily_IsLetteredWithTheValveRoles()
        {
            Assert.Equal(NintendoPreviewMap.Family.SInput, NintendoPreviewMap.FamilyOf(P));
            Assert.True(NintendoPreviewMap.IsLettered(P));
            Assert.True(NintendoPreviewMap.UsesValveRoles(P));
            Assert.False(NintendoPreviewMap.IsValve(P));
            Assert.True(NintendoPreviewMap.IsSInput(P));
            Assert.Equal(2, NintendoPreviewMap.StickCount(P));
            Assert.Equal(2, NintendoPreviewMap.TriggerCount(P));
            Assert.False(NintendoPreviewMap.DPadIsHat(P));
            Assert.Equal(NintendoPreviewMap.AxisTable("steam-deck-composite"), NintendoPreviewMap.AxisTable(P));
            Assert.Equal(22, NintendoPreviewMap.ButtonCount(P));
            Assert.Equal(NintendoPreviewMap.Family.None, NintendoPreviewMap.FamilyOf("joypad-steam-input"));
        }

        [Fact]
        public void WireTable_RoundTripsEveryRow()
        {
            var table = NintendoPreviewMap.ButtonTable(P);
            Assert.Equal(table.Length, table.Distinct().Count());
            for (int i = 0; i < table.Length; i++)
            {
                Assert.Equal($"RawBtn{i}", NintendoPreviewMap.ToRaw(table[i], P));
                Assert.Equal(table[i], NintendoPreviewMap.ToPreview($"RawBtn{i}", P));
            }
        }

        /// <summary>Misc 1 lands on Share on SInput's wire and on Quick
        /// Access on the Valve wires, by the same automap position.</summary>
        [Fact]
        public void Automap_PutsMisc1OnShareHereAndOnQuickAccessOnValve()
        {
            Assert.Equal(NintendoPreviewMap.ToRaw("ButtonShare", P),
                NintendoPreviewMap.RawForGamepadTarget("ButtonShare", P, valve: true));
            Assert.Equal(NintendoPreviewMap.ToRaw("ButtonQuickAccess", "steam-deck-composite"),
                NintendoPreviewMap.RawForGamepadTarget("ButtonShare", "steam-deck-composite", valve: true));
            Assert.Equal(NintendoPreviewMap.ToRaw("Paddle1", P),
                NintendoPreviewMap.RawForGamepadTarget("RightPaddle", P, valve: true));
            Assert.Equal(NintendoPreviewMap.ToRaw("Paddle4", P),
                NintendoPreviewMap.RawForGamepadTarget("LeftFunction", P, valve: true));
        }

        [Fact]
        public void TranslateRawTarget_CarriesRolesBetweenSInputAndTheSteamDeck()
        {
            string guideOnDeck = NintendoPreviewMap.ToRaw("ButtonGuide", "steam-deck-composite");
            Assert.Equal(NintendoPreviewMap.ToRaw("ButtonGuide", P),
                NintendoPreviewMap.TranslateRawTarget(guideOnDeck, "steam-deck-composite", P));
            // Quick Access has no SInput row, so a binding on it is dropped.
            Assert.Null(NintendoPreviewMap.TranslateRawTarget(
                NintendoPreviewMap.ToRaw("ButtonQuickAccess", "steam-deck-composite"), "steam-deck-composite", P));
        }

        [Fact]
        public void EverySInputRowIsNamedAsAGamepadNamesIt()
        {
            Assert.True(MacroButtonNames.IsValveLetteredProfile(P));
            int n = NintendoPreviewMap.ButtonCount(P);
            for (int i = 1; i <= n; i++)
            {
                string label = MacroButtonNames.RawButtonLabel(P, i);
                Assert.False(string.IsNullOrWhiteSpace(label), $"row {i} has no label");
                Assert.DoesNotContain("Button ", label);
            }
            string L(string role) => MacroButtonNames.RawButtonLabel(P, NintendoPreviewMap.IndexOf(P, role) + 1);
            Assert.Equal("Guide", L("ButtonGuide"));
            Assert.Equal("Back", L("ButtonBack"));
            Assert.Equal("Start", L("ButtonStart"));
            Assert.Equal("Share", L("ButtonShare"));
            Assert.Equal("Left Shoulder", L("LeftShoulder"));
            Assert.Equal("Left Stick Button", L("LeftThumbButton"));
            Assert.Equal("R4", L("Paddle1"));
            Assert.Equal("L5", L("Paddle4"));
            Assert.Equal("Left Pad Click", L("LeftTouchpadClick"));
            // The Valve pads keep their own names.
            Assert.Equal("Steam", MacroButtonNames.RawButtonLabel("steam-deck-composite",
                NintendoPreviewMap.IndexOf("steam-deck-composite", "ButtonGuide") + 1));
        }

        // ── HIDMaestro 2.2.0's personas ────────────────────────────────

        /// <summary>The persona the bundled HIDMaestro.Core ships answers
        /// the features command with the reply this file ports SDL against,
        /// read from the profile embedded in the DLL.</summary>
        [Fact]
        public void TheBundledPersonaAnswersTheReplyThePortReads()
        {
            var asm = typeof(HIDMaestro.HMContext).Assembly;
            string name = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith("sinput.json", StringComparison.OrdinalIgnoreCase));
            using var stream = asm.GetManifestResourceStream(name);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var rule = doc.RootElement.GetProperty("protocol").GetProperty("commands").GetProperty("rules")
                .EnumerateArray().Single(r => r.GetProperty("match").GetString() == "03 02");
            Assert.Equal(64, rule.GetProperty("replyLength").GetInt32());
            var reply = new List<byte>();
            foreach (var part in rule.GetProperty("reply").EnumerateArray())
            {
                if (part.TryGetProperty("hex", out var hex)) reply.AddRange(Convert.FromHexString(hex.GetString()));
                else reply.Add(0);   // the controller's index, controller 0
            }
            Assert.Equal(PersonaFeatureReply().Take(reply.Count).ToArray(), reply.ToArray());
        }

        [Fact]
        public void TheCatalogOffersTheSInputPersonaUnderExtended()
        {
            var sinput = HMaestroProfileCatalog.GetProfileById(P);
            Assert.NotNull(sinput);
            Assert.Equal((ushort)0x2E8A, sinput.VendorId);
            Assert.Equal((ushort)0x10C6, sinput.ProductId);
            Assert.Equal("SInput Gamepad", sinput.ProductString);
            Assert.True(sinput.IsDeployable);
            Assert.False(HMaestroProfileCatalog.IsWithheldProfile(P));
            Assert.Contains(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == P);
            // joypad-steam-input stays a numbered Extended profile.
            Assert.Contains(HMaestroProfileCatalog.ExtendedProfiles, p => p.Id == "joypad-steam-input");
        }

        /// <summary>Both personas' haptics arrive decoded, so the Bass Shakers
        /// gate sees a feedback source with no PID block, while only the Deck
        /// persona's feature writes go to the Deck decode.</summary>
        [Fact]
        public void SInputFeedbackIsClaimedWithoutReachingTheDeckDecode()
        {
            foreach (string id in new[] { P, "joypad-steam-input", "JOYPAD-STEAM-INPUT" })
            {
                Assert.True(HMaestroVirtualController.DecodesFeedbackWithoutPidBlock(id), id);
                Assert.True(HMaestroVirtualController.DecodesSInputMotors(id), id);
                Assert.False(HMaestroVirtualController.DecodesDeckFeatureFeedback(id), id);
            }
            Assert.True(HMaestroVirtualController.DecodesDeckFeatureFeedback("steam-deck-composite"));
            Assert.False(HMaestroVirtualController.DecodesSInputMotors("steam-deck-composite"));
            Assert.False(HMaestroVirtualController.DecodesSInputMotors("joypad-generic-hid"));
            Assert.True(HMaestroVirtualController.MotorWriteAllowed(0x2E8A, false));
        }

        /// <summary>The decoded pair reaches the slot's inbound rumble pack
        /// (#236) on both personas, the branch the Switch Pro's decode
        /// takes.</summary>
        [Fact]
        public void TheDecodedPairFeedsTheInboundPack()
        {
            string vc = Source("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            Assert.Contains("else if (_profile.VendorId == NintendoVid || DecodesSInputMotors(_profile.Id))", vc);
            Assert.Contains("&& DecodesDeckFeatureFeedback(_profile.Id)", vc);
        }

        // ── frames for a live check through SDL ────────────────────────

        /// <summary>SDL_GamepadButton for each role on the SInput wire,
        /// through the persona's reply and SDL's mapping (the
        /// FeatureReply_GivesSdlOpenPucksShape list).</summary>
        internal static readonly (string Role, int SdlButton)[] RoleButtons =
        {
            ("ButtonA", 0), ("ButtonB", 1), ("ButtonX", 2), ("ButtonY", 3),
            ("ButtonBack", 4), ("ButtonGuide", 5), ("ButtonStart", 6),
            ("LeftThumbButton", 7), ("RightThumbButton", 8),
            ("LeftShoulder", 9), ("RightShoulder", 10),
            ("DPadUp", 11), ("DPadDown", 12), ("DPadLeft", 13), ("DPadRight", 14),
            ("ButtonShare", 15), ("Paddle1", 16), ("Paddle2", 17), ("Paddle3", 18), ("Paddle4", 19),
            ("LeftTouchpadClick", 20), ("RightTouchpadClick", 24),
        };

        /// <summary>Writes PadForge's packed frames, each with the SDL state
        /// it must produce, for a probe that submits them to the live persona
        /// and reads SDL. Runs only when PADFORGE_SINPUT_FRAMES names the
        /// output file, the PluginRowFramesDump pattern. One line per case:
        /// name, the 64-byte frame in hex, then key=value expectations.</summary>
        [Fact]
        public void PackedFramesForALiveCheck()
        {
            string path = Environment.GetEnvironmentVariable("PADFORGE_SINPUT_FRAMES");
            if (string.IsNullOrEmpty(path)) return;
            var sdl = SdlSInput.FromReply(PersonaFeatureReply());
            var lines = new List<string>();
            void Case(string name, byte[] frame, string expect) =>
                lines.Add(name + "|" + Convert.ToHexString(frame) + "|" + expect);

            Case("rest", Pack(RestRaw()), "buttons=;lx=0;ly=0;rx=0;ry=0;lt=0;rt=0");
            foreach (var (role, button) in RoleButtons)
            {
                var raw = RestRaw();
                raw.SetButton(NintendoPreviewMap.IndexOf(P, role), true);
                Case("role " + role, Pack(raw), "buttons=" + button);
            }

            var pulled = RestRaw();
            pulled.Axes[ValveReportPackers.AxLT] = short.MaxValue;
            pulled.Axes[ValveReportPackers.AxRT] = short.MaxValue;
            // Full pulls set the dual-stage clicks, Misc 3 and Misc 4.
            Case("triggers full", Pack(pulled), "buttons=22,23;lt=32767;rt=32767");

            var sticks = RestRaw();
            sticks.Axes[ValveReportPackers.AxLX] = -12345;
            sticks.Axes[ValveReportPackers.AxLY] = 23456;
            sticks.Axes[ValveReportPackers.AxRX] = 32767;
            sticks.Axes[ValveReportPackers.AxRY] = -32768;
            Case("sticks", Pack(sticks), "buttons=;lx=-12345;ly=23456;rx=32767;ry=-32768");

            var motion = new MotionSnapshot
            {
                AccelX = 0.25f, AccelY = -1.0f, AccelZ = 0.5f,
                GyroPitch = 100f, GyroYaw = -250f, GyroRoll = 30f, HasMotion = true,
            };
            var m = Pack(RestRaw(), motion: motion, micros: 1000);
            var g = sdl.Gyro3(m);
            var a = sdl.Accel3(m);
            Case("motion", m, FormattableString.Invariant(
                $"buttons=;gyro={g.x:R},{g.y:R},{g.z:R};accel={a.x:R},{a.y:R},{a.z:R}"));

            var tp = new TouchpadState { Down0 = true, X0 = 0.25f, Y0 = 0.75f, Down1 = true, X1 = 1f, Y1 = 0f };
            var t = Pack(RestRaw(), tp);
            var t0 = sdl.Touch(t, 0);
            var t1 = sdl.Touch(t, 1);
            Case("touch", t, FormattableString.Invariant(
                $"buttons=;t0={t0.x:R},{t0.y:R},{t0.pressure:R};t1={t1.x:R},{t1.y:R},{t1.pressure:R}"));

            File.WriteAllLines(path, lines);
        }

        // ── the player LED and its relay ───────────────────────────────

        private static string Source(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        private static string Body(string src, string startMarker, string endMarker)
        {
            int a = src.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.True(a >= 0, startMarker);
            int b = src.IndexOf(endMarker, a + startMarker.Length, StringComparison.Ordinal);
            Assert.True(b > a, endMarker);
            return src.Substring(a, b - a);
        }

        /// <summary>SDL lights an SInput pad's player LED from the index it
        /// gives the pad itself (SDL_joystick.c:3619-3624 and 2416-2421,
        /// SDL_hidapi_sinput.c:691-709), so the wrapper writes the slot's index
        /// over it, and both ends of the remote link carry the number.</summary>
        [Fact]
        public void PlayerIndex_ReachesSInputPadsHereAndOverTheLink()
        {
            string wrapper = Body(Source("PadForge.Engine", "Common", "SdlDeviceWrapper.cs"),
                "public bool SetPlayerIndex(int playerIndex)", "/// HOME button LED brightness");
            Assert.Contains("SInputControllerIdentity.IsSInput(VendorId, ProductId)", wrapper);

            string service = Source("PadForge.App", "Services", "InputService.cs");
            string receive = Body(service, "case OutputEffectCodec.Kind.PlayerIndex:", "case OutputEffectCodec.Kind.GuideLed:");
            Assert.Contains("SInputControllerIdentity.IsSInput(source.VendorId, source.ProductId)", receive);
            string send = Body(service, "public void ReseedPlayerIdentities(", "ApplyOnceAll();");
            Assert.Contains("SInputControllerIdentity.IsSInput(rpd.VendorId, rpd.ProductId)", send);
        }
    }

    /// <summary>The rows an Extended slot on the SInput persona gets (#584):
    /// one row per wire-table index, the D-pad on its four buttons, the Share
    /// row the Valve arrangement now carries, both touchpads' rows and the
    /// Motion rows, all through InitializeRawSurfaceMappings' Valve branch.</summary>
    [Collection("SettingsManagerStatics")]
    public class SInputGridRowTests
    {
        private static List<MappingItem> Rows() =>
            RestoredPad.Build(0, VirtualControllerType.Extended, "sinput").Mappings.ToList();

        [Fact]
        public void EveryWireIndexHasExactlyOneRow()
        {
            var targets = Rows().Select(m => m.TargetSettingName).ToList();
            int n = NintendoPreviewMap.ButtonCount("sinput");
            for (int i = 0; i < n; i++)
                Assert.Equal(1, targets.Count(t => t == $"RawBtn{i}"));
            Assert.DoesNotContain(targets, t => t == $"RawBtn{n}");
            Assert.DoesNotContain(targets, t => t.StartsWith("RawPov", StringComparison.Ordinal));
        }

        [Fact]
        public void ShareHasItsNamedRow()
        {
            var share = Assert.Single(Rows(), m => m.TargetSettingName == NintendoPreviewMap.ToRaw("ButtonShare", "sinput"));
            Assert.Equal("Share", share.TargetLabel);
        }

        [Fact]
        public void BothTouchpadsAndTheMotionRowsArePresent()
        {
            var targets = Rows().Select(m => m.TargetSettingName).ToHashSet();
            foreach (string t in new[] { "TouchpadX1", "TouchpadY1", "TouchpadContact1", "TouchpadX2", "TouchpadY2", "TouchpadContact2", "MotionGyro", "MotionAccel" })
                Assert.Contains(t, targets);
            for (int a = 0; a < 6; a++)
                Assert.Contains($"RawAxis{a}", targets);
        }
    }
}
