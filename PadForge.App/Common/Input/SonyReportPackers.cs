using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Packs PadForge's input state (Gamepad + TouchpadState + MotionSnapshot +
    /// battery) into the canonical Sony USB Report 0x01 byte layout for either
    /// DS4 or DualSense, so HM's <c>SubmitRawReport</c> can deliver a complete
    /// input report with touchpad / gyro / accel / battery — fields that the
    /// standard <c>HMGamepadState</c> surface doesn't model.
    ///
    /// <para>Two layouts are supported. Both ride Report ID 0x01 with a 64-byte
    /// total report size (= 63 bytes of data after HM prepends the Report ID
    /// for us), but the byte positions are different:</para>
    ///
    /// <list type="bullet">
    /// <item><b>DS4 Type 1</b> — sticks at 0–3, hat+buttons at 4–6, triggers
    /// at 7–8, vendor blob 9–62 (timestamp 9–10, legacy battery 11, gyro 12–17,
    /// accel 18–23, touchpad packets at 32+). Layout sourced from
    /// <c>ViGEmClient/include/ViGEm/Common.h</c> <c>DS4_REPORT_EX</c> struct.</item>
    /// <item><b>DualSense USB</b> — sticks+triggers inline at 0–5, counter at 6,
    /// hat+buttons at 7–10, packet sequence 11–14, gyro+accel at 15–26,
    /// sensor timestamp 27–30, touchpad packets at 32+, battery at 52.
    /// Layout sourced from
    /// <c>SDL3-build/SDL/src/joystick/hidapi/SDL_hidapi_ps5.c</c>
    /// <c>PS5StatePacket_t</c> (the "full" struct used for genuine Sony
    /// VID/PID 054C:0CE6/0DF2 controllers; alt-report path is for third-party
    /// PS5 pads).</item>
    /// </list>
    ///
    /// <para>Pinned to HM v1.2.0 profile descriptors. If a future HM rev adds a
    /// new Sony profile ID with the same shape (USB Report 0x01, 64-byte
    /// report), add it to <see cref="ByProfileId"/>. If a profile changes its
    /// vendor-blob layout, the packer needs a per-profile branch.</para>
    /// </summary>
    internal static class SonyReportPackers
    {
        /// <summary>Packs the host-frame state into <paramref name="dest"/>,
        /// which holds at least 63 bytes, the data portion of a 64-byte USB
        /// Report 0x01. <paramref name="frameCounter"/> counts the frames
        /// sent, and <paramref name="micros"/> is the time of this one in
        /// microseconds (<see cref="ValveReportPackers.MicrosNow"/>), which
        /// the DualShock 4 frame carries as its timestamp.</summary>
        internal delegate void Packer(
            in Gamepad gp,
            in TouchpadState tp,
            in MotionSnapshot motion,
            byte batteryPercent,
            bool charging,
            uint frameCounter,
            uint micros,
            Span<byte> dest);

        /// <summary>HM profile ID → packer. Only USB-shape profiles (Report
        /// 0x01, 64-byte size) are wired today. BT variants ride different
        /// report IDs with extra prefix bytes and aren't covered here.</summary>
        internal static readonly IReadOnlyDictionary<string, Packer> ByProfileId =
            new Dictionary<string, Packer>(StringComparer.OrdinalIgnoreCase)
            {
                { "dualshock-4-v1",      PackDs4UsbReport01 },
                { "dualshock-4-v1-full", PackDs4UsbReport01 },
                { "dualshock-4-v2",      PackDs4UsbReport01 },
                { "dualsense",           PackDualSenseUsbReport01 },
                { "dualsense-edge",      PackDualSenseUsbReport01 },
                // Composite USB personas (HM#39). Each carries a HID
                // interface byte-identical to its base profile: same
                // report 0x01, same 64-byte size, same descriptor. Without
                // these entries the lookup falls through to plain
                // SubmitState and the pad silently loses touchpad, gyro,
                // accel and battery, which is what a user sees as "the
                // touchpad does not work at all" on the Full profile.
                { "dualsense-composite",      PackDualSenseUsbReport01 },
                { "dualsense-edge-composite", PackDualSenseUsbReport01 },
                { "dualshock-4-v2-composite", PackDs4UsbReport01 },
            };

        /// <summary>Lookup helper. Returns null if no packer is registered for
        /// the given profile, in which case Step 5 falls back to plain
        /// <c>SubmitState</c> (no touchpad/gyro/accel/battery passthrough).</summary>
        internal static Packer ForProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            ByProfileId.TryGetValue(profileId, out var p);
            return p;
        }

        private static readonly Packer DualSensePacker = PackDualSenseUsbReport01;

        /// <summary>True for every profile <see cref="PackDualSenseUsbReport01"/>
        /// serves, the ones whose report carries trigger feedback bytes.</summary>
        internal static bool IsDualSenseProfile(string profileId)
            => !string.IsNullOrEmpty(profileId)
            && ByProfileId.TryGetValue(profileId, out var p)
            && p.Equals(DualSensePacker);

        /// <summary>Stamps a physical DualSense's payload bytes 40..47 into a
        /// packed DualSense report, little-endian from
        /// <paramref name="statusBytes"/> (payload byte 40 from bits 0..7, byte
        /// 47 from bits 56..63). Zero leaves the eight bytes as the packer
        /// wrote them, which is the pre-#433 report. See
        /// <see cref="DualSenseStatusBytes"/> for the field map.</summary>
        internal static void ApplyDualSenseStatusBytes(Span<byte> dest, ulong statusBytes)
        {
            if (statusBytes == 0 || dest.Length < 48) return;
            for (int i = 0; i < 8; i++)
                dest[40 + i] = (byte)(statusBytes >> (8 * i));
        }

        // ── DS4 USB Report 0x01 (DS4_REPORT_EX) ─────────────────────────────
        // Touchpad resolution per Sony firmware: 1920 × 943.
        private const int Ds4TouchWidth = 1920;
        private const int Ds4TouchHeight = 943;

        /// <summary>The DualShock 4's 16-bit frame timestamp for a time in
        /// microseconds. Its unit is 16/3 of a microsecond (5.33 µs), and
        /// it wraps every 349.5 ms as the pad's own does.</summary>
        internal static ushort Ds4Timestamp(uint micros) => (ushort)((ulong)micros * 3 / 16);

        private static void PackDs4UsbReport01(
            in Gamepad gp, in TouchpadState tp, in MotionSnapshot motion,
            byte batteryPercent, bool charging, uint frameCounter, uint micros, Span<byte> dest)
        {
            dest.Clear();

            // Sticks (bytes 0-3): center 0x80. XInput Y is +up; DS4 firmware
            // is +down (HID convention) so Y axes are inverted.
            dest[0] = ToDs4Axis(gp.ThumbLX);
            // No (short) re-narrowing on the negation: -(-32768) is 32768,
            // which wraps back to -32768 in a short and packs full-DOWN as
            // 0x00 (full up). ToDs4Axis takes an int and clamps, so the
            // widened value lands at 255 the way every other deflection
            // does. Reachable whenever a source saturates an axis to
            // short.MinValue (an AxisAdd macro on top of a deflected stick).
            dest[1] = ToDs4Axis(-gp.ThumbLY);
            dest[2] = ToDs4Axis(gp.ThumbRX);
            dest[3] = ToDs4Axis(-gp.ThumbRY);

            // Buttons + hat (bytes 4-6).
            // byte 4: bits 0-3 = D-pad as 0..7 / 0x8=neutral; bits 4-7 = face buttons.
            // byte 5: bits 0-3 = shoulder/trigger digital; bits 4-7 = system buttons.
            // byte 6: bit 0 = PS, bit 1 = touchpad click, bits 2-7 = report counter.
            dest[4] = (byte)(EncodeDpad(gp.Buttons)
                           | (gp.IsButtonPressed(Gamepad.X)         ? 0x10 : 0)   // Square
                           | (gp.IsButtonPressed(Gamepad.A)         ? 0x20 : 0)   // Cross
                           | (gp.IsButtonPressed(Gamepad.B)         ? 0x40 : 0)   // Circle
                           | (gp.IsButtonPressed(Gamepad.Y)         ? 0x80 : 0)); // Triangle

            byte b5 = 0;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  b5 |= 0x01;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) b5 |= 0x02;
            // Real DS4 / DualSense: digital L2/R2 bits ride the analog axis
            // — any non-zero pull asserts the corresponding button. The
            // earlier 0x80FF (~50%) threshold here was a leftover from
            // bridging XInput's no-digital-trigger surface and made DInput
            // observers report button 7/8 only on hard pulls.
            if (gp.LeftTrigger  > 0)                        b5 |= 0x04; // L2 digital
            if (gp.RightTrigger > 0)                        b5 |= 0x08; // R2 digital
            if (gp.IsButtonPressed(Gamepad.BACK))           b5 |= 0x10; // Share
            if (gp.IsButtonPressed(Gamepad.START))          b5 |= 0x20; // Options
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     b5 |= 0x40; // L3
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    b5 |= 0x80; // R3
            dest[5] = b5;

            byte b6 = (byte)((frameCounter & 0x3F) << 2);
            if (gp.IsButtonPressed(Gamepad.GUIDE))    b6 |= 0x01;
            if (gp.IsButtonPressed(Gamepad.TOUCHPAD)) b6 |= 0x02;
            if (tp.Click)                              b6 |= 0x02;
            dest[6] = b6;

            // Triggers (bytes 7-8): scale XInput ushort 0..65535 to 0..255.
            dest[7] = (byte)(gp.LeftTrigger  >> 8);
            dest[8] = (byte)(gp.RightTrigger >> 8);

            // Timestamp (bytes 9-10): 16-bit LE, the time of the frame.
            // Readers take the difference between two frames as the time
            // between them: SDL adds it to its sensor clock
            // (SDL_hidapi_ps4.c, "Sensor timestamp is in 5.33us units") and
            // DS4Windows hands it to its gyro as the elapsed time
            // (DS4Device.cs, deltaTimeCurrent = tempDelta * 16u / 3u). The
            // frame counter times 188 stood here, one millisecond a frame,
            // which is the pad's clock only at 1000 frames a second: at any
            // other rate a reader's gyro turned too far or too little by the
            // ratio, ten times too far at 10000.
            ushort ts = Ds4Timestamp(micros);
            dest[9]  = (byte)(ts & 0xFF);
            dest[10] = (byte)(ts >> 8);

            // Battery (byte 11): legacy bBatteryLvl. Every modern reader
            // consults byte 29 below instead. No reference in the references
            // folder reads this one, so it is written for older parsers only.
            dest[11] = ScaleDs4BatteryNibble(batteryPercent, charging);

            // Gyro (bytes 12-17), Accel (bytes 18-23): int16 LE.
            WriteI16(dest, 12, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 14, ScaleGyro(motion.GyroYaw));
            WriteI16(dest, 16, ScaleGyro(motion.GyroRoll));
            WriteI16(dest, 18, ScaleAccel(motion.AccelX));
            WriteI16(dest, 20, ScaleAccel(motion.AccelY));
            WriteI16(dest, 22, ScaleAccel(motion.AccelZ));

            // Bytes 24-28 are reserved (zero from dest.Clear()).

            // bBatteryLvlSpecial (byte 29): low nibble = battery level scaled
            // to maxBatteryValue (8 when discharging, 11 when USB charging),
            // bit 4 = USB charging flag. This is the canonical battery surface
            // every reader consults.
            //
            // 29, not 30. This buffer holds DATA BYTES ONLY: SubmitRawReport
            // takes the frame without its report id and the driver heads it,
            // and every other field here is numbered that way (timestamp 9,
            // gyro 12, accel 18, touch 34, all matching SDL's PS4StatePacket_t,
            // which SDL points at &data[1]). The references number theirs from
            // the report id instead, so DS4Windows reads inputReport[30] while
            // its own stick X sits at inputReport[1]. Taking that 30 across to
            // a data-only buffer wrote the battery into a pad byte and left
            // the real one at zero, and zero decodes as on-battery at the
            // bottom of the scale: SDL_hidapi_ps4.c reads level 0 with the
            // charging bit clear and reports 5 percent, so Steam and every
            // other reader showed a flat low battery whatever the pad had.
            // The DualSense packer was never wrong here, which is why that
            // one always read correctly.
            dest[29] = (byte)(ScaleDs4BatteryNibble(batteryPercent, charging)
                            | (charging ? 0x10 : 0x00));

            // bTouchPacketsN (byte 32): number of touch packets (0..3). PadForge
            // delivers one current-frame snapshot per polling tick, so packet
            // count = 1 whenever any finger is down (or just lifted in this
            // frame).
            //
            // sCurrentTouch (bytes 33-41) layout per ViGEm DS4_REPORT_EX:
            //   33: bPacketCounter
            //   34: bIsUpTrackingNum1 (bit 7 = NOT down, bits 0-6 = tracking ID)
            //   35-37: bTouchData1 (12-bit X + 12-bit Y)
            //   38: bIsUpTrackingNum2
            //   39-41: bTouchData2
            //
            // sPreviousTouch[0..1] (bytes 42-50, 51-59) stay zero — real DS4
            // firmware leaves them unset between contact events anyway.
            // Always emit the touch block so idle/lift frames carry the finger-up
            // bit (EncodeDs4Touch writes 0x80 when a finger is up), matching real
            // DS4 firmware and the DualSense builder below. Leaving bytes 33-41 at
            // 0x00 on idle reads as a phantom finger down at the origin in parsers
            // that don't gate on bTouchPacketsN. dest[32] still reports 0 packets
            // on idle for parsers that do respect the count.
            int touchPackets = (tp.Down0 || tp.Down1) ? 1 : 0;
            dest[32] = (byte)touchPackets;
            dest[33] = tp.PacketCounter;
            EncodeDs4Touch(dest.Slice(34, 8), tp);

            // Bytes 60-62 padding (zero from Clear()).
        }

        // Scale a 0..100 percent value to the DS4 byte-30 nibble. Sony's
        // firmware uses different ranges depending on charging state — 0..8
        // when discharging, 0..11 when USB charging. Reader decode:
        //     percent = (nibble & 0x0F) * 100 / maxBatteryValue
        // Source: Ryochan7/DS4MapperTest DS4Reader.cs:357-361 and
        // DS4Device.cs::BATTERY_MAX / BATTERY_MAX_USB constants.
        private static byte ScaleDs4BatteryNibble(byte batteryPercent, bool charging)
        {
            if (batteryPercent > 100) batteryPercent = 100;
            int max = charging ? 11 : 8;
            int nibble = (batteryPercent * max + 50) / 100;
            if (nibble > max) nibble = max;
            return (byte)(nibble & 0x0F);
        }

        private static byte ToDs4Axis(int signedShort)
        {
            int v = (signedShort + 32768) >> 8;
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        // 1 packet of DS4_TOUCH = 8 bytes after the per-packet timestamp:
        //   bIsUpTrackingNum1 + bTouchData1[3] + bIsUpTrackingNum2 + bTouchData2[3]
        private static void EncodeDs4Touch(Span<byte> dst, in TouchpadState tp)
        {
            dst[0] = (byte)((tp.Down0 ? 0x00 : 0x80) | (tp.PacketCounter & 0x7F));
            PackTouch12(dst.Slice(1, 3), tp.X0, tp.Y0, Ds4TouchWidth, Ds4TouchHeight);
            dst[4] = (byte)((tp.Down1 ? 0x00 : 0x80) | ((tp.PacketCounter + 1) & 0x7F));
            PackTouch12(dst.Slice(5, 3), tp.X1, tp.Y1, Ds4TouchWidth, Ds4TouchHeight);
        }

        // ── DualSense USB Report 0x01 (PS5StatePacket_t) ────────────────────
        // Touchpad resolution per Sony firmware: 1920 × 1080.
        private const int DsTouchWidth = 1920;
        private const int DsTouchHeight = 1080;

        private static void PackDualSenseUsbReport01(
            in Gamepad gp, in TouchpadState tp, in MotionSnapshot motion,
            byte batteryPercent, bool charging, uint frameCounter, uint micros, Span<byte> dest)
        {
            dest.Clear();

            // Sticks + triggers inline (bytes 0-5). Y inverted vs XInput.
            dest[0] = ToDs4Axis(gp.ThumbLX);
            // No (short) re-narrowing on the negation: -(-32768) is 32768,
            // which wraps back to -32768 in a short and packs full-DOWN as
            // 0x00 (full up). ToDs4Axis takes an int and clamps, so the
            // widened value lands at 255 the way every other deflection
            // does. Reachable whenever a source saturates an axis to
            // short.MinValue (an AxisAdd macro on top of a deflected stick).
            dest[1] = ToDs4Axis(-gp.ThumbLY);
            dest[2] = ToDs4Axis(gp.ThumbRX);
            dest[3] = ToDs4Axis(-gp.ThumbRY);
            dest[4] = (byte)(gp.LeftTrigger  >> 8);
            dest[5] = (byte)(gp.RightTrigger >> 8);

            // Counter byte (6).
            dest[6] = (byte)(frameCounter & 0xFF);

            // Buttons + hat (bytes 7-10). Byte 9 layout per the DualSense USB
            // input-report parser in SDL_hidapi_ps5.c (rgucButtonsAndHat[2]):
            // bit 0 = PS, bit 1 = Touchpad click, bit 2 = Mute (mic), bit 3
            // reserved, bits 4-7 = DualSense Edge function/paddle buttons.
            // Byte 9 contains NO counter — the controller has its own counter
            // at byte 6 and a 32-bit packet sequence at bytes 11-14.
            dest[7] = (byte)(EncodeDpad(gp.Buttons)
                           | (gp.IsButtonPressed(Gamepad.X) ? 0x10 : 0)   // Square
                           | (gp.IsButtonPressed(Gamepad.A) ? 0x20 : 0)   // Cross
                           | (gp.IsButtonPressed(Gamepad.B) ? 0x40 : 0)   // Circle
                           | (gp.IsButtonPressed(Gamepad.Y) ? 0x80 : 0)); // Triangle

            byte b8 = 0;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  b8 |= 0x01;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) b8 |= 0x02;
            // Real DualSense: digital L2/R2 bits ride the analog axis —
            // any non-zero pull asserts the corresponding button. See the
            // matching comment in PackDs4UsbReport01.
            if (gp.LeftTrigger  > 0)                        b8 |= 0x04;
            if (gp.RightTrigger > 0)                        b8 |= 0x08;
            if (gp.IsButtonPressed(Gamepad.BACK))           b8 |= 0x10; // Create
            if (gp.IsButtonPressed(Gamepad.START))          b8 |= 0x20; // Options
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     b8 |= 0x40;
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    b8 |= 0x80;
            dest[8] = b8;

            byte b9 = 0;
            if (gp.IsButtonPressed(Gamepad.GUIDE))    b9 |= 0x01; // PS
            // Both sources, matching the DS4 packer. tp.Click is the PHYSICAL
            // touchpad press; Gamepad.TOUCHPAD is a mapped or macro-driven one.
            // Only the physical source was honored here, so a macro bound to
            // Touchpad reached the host on a virtual DS4 and silently did
            // nothing on a virtual DualSense.
            if (gp.IsButtonPressed(Gamepad.TOUCHPAD)) b9 |= 0x02;
            if (tp.Click)                             b9 |= 0x02; // Touchpad click
            // bit 0x04 = Mute (mic), bits 0x10-0x80 = the DualSense Edge
            // function / paddle buttons, per SDL_hidapi_ps5.c's parser
            // (LEFT_FUNCTION 0x10, RIGHT_FUNCTION 0x20, LEFT_PADDLE 0x40,
            // RIGHT_PADDLE 0x80). Packed unconditionally: the bits are
            // only ever set when a mapping targets them, plain-DS5
            // parsers read the Edge bits solely on Edge PIDs, and the
            // mute bit is a declared button (USAGE_MAX 15) on every DS5
            // descriptor.
            if (gp.MicMute)       b9 |= 0x04;
            if (gp.LeftFunction)  b9 |= 0x10;
            if (gp.RightFunction) b9 |= 0x20;
            if (gp.LeftPaddle)    b9 |= 0x40;
            if (gp.RightPaddle)   b9 |= 0x80;
            dest[9] = b9;

            // byte 10 stays zero (reserved / future button bits).

            // Packet sequence (bytes 11-14): 32-bit LE counter — increments
            // every frame.
            WriteU32(dest, 11, frameCounter);

            // Gyro (bytes 15-20), Accel (bytes 21-26): int16 LE.
            WriteI16(dest, 15, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 17, ScaleGyro(motion.GyroYaw));
            WriteI16(dest, 19, ScaleGyro(motion.GyroRoll));
            WriteI16(dest, 21, ScaleAccel(motion.AccelX));
            WriteI16(dest, 23, ScaleAccel(motion.AccelY));
            WriteI16(dest, 25, ScaleAccel(motion.AccelZ));

            // Sensor timestamp (bytes 27-30): 32-bit LE. The DualSense
            // 0x31 sensor-timestamp field counts in 0.33 µs ticks
            // (SDL3's PS5 driver decodes it as ticks * 1000 / 3 ns), so
            // microseconds must be multiplied by 3 to land in the right
            // unit. Writing raw µs made consumers see the sensor clock
            // advance at 1/3 real rate.
            WriteU32(dest, 27, (uint)((motion.TimestampUs * 3L) & 0xFFFFFFFFL));

            // Sensor temp (byte 31) stays zero — informational, not required.

            // Touchpad packets (bytes 32-39): finger 0 at 32-35, finger 1 at
            // 36-39. Each finger = counter byte (bit 7 = NOT down) + 3 packed
            // touch bytes (12-bit X + 12-bit Y).
            dest[32] = (byte)((tp.Down0 ? 0x00 : 0x80) | (tp.PacketCounter & 0x7F));
            PackTouch12(dest.Slice(33, 3), tp.X0, tp.Y0, DsTouchWidth, DsTouchHeight);
            dest[36] = (byte)((tp.Down1 ? 0x00 : 0x80) | ((tp.PacketCounter + 1) & 0x7F));
            PackTouch12(dest.Slice(37, 3), tp.X1, tp.Y1, DsTouchWidth, DsTouchHeight);

            // Bytes 40-47: a real DualSense reports the touch timestamp (40),
            // the right and left adaptive-trigger feedback (41, 42: a stop-
            // location nibble and a status nibble each), the host timestamp
            // echo (43-46) and the active trigger-effect mode nibbles (47).
            // Games with adaptive triggers read 41, 42 and 47 back (#433).
            // The packer leaves them zero. Step 5 stamps the assigned physical
            // pad's bytes through ApplyDualSenseStatusBytes once the SDL fork
            // publishes them.

            // Timer 2 (bytes 48-51): another 32-bit counter — feed the same
            // sequence so games checking for monotonic timer don't trip.
            WriteU32(dest, 48, frameCounter);

            // Battery (byte 52): low nibble = level 0..10 (percent / 10),
            // high nibble = status (0x0 = discharging, 0x1 = charging,
            // 0x2 = full). SDL3's PS5 parser decode:
            //     status = (byte >> 4) & 0x0F
            //     percent = (byte & 0x0F) * 10
            int dsLevel = (batteryPercent + 5) / 10;
            if (dsLevel > 10) dsLevel = 10;
            byte status = charging ? (batteryPercent >= 100 ? (byte)0x2 : (byte)0x1) : (byte)0x0;
            dest[52] = (byte)((status << 4) | (dsLevel & 0x0F));

            // Connect state (byte 53): 0x08 = USB cable, per SDL3 PS5 parser.
            // PadForge's virtual is always "USB" from the host's perspective
            // (HM creates the virtual HID on a USB-shaped bus enumerator).
            dest[53] = 0x08;

            // Bytes 54-62 padding (zero).
        }

        // ── Shared helpers ──────────────────────────────────────────────────

        // D-pad encoding: 8-way as 0..7 (N, NE, E, SE, S, SW, W, NW), 0x8 = neutral.
        private static byte EncodeDpad(ushort buttons)
        {
            bool up    = (buttons & Gamepad.DPAD_UP)    != 0;
            bool down  = (buttons & Gamepad.DPAD_DOWN)  != 0;
            bool left  = (buttons & Gamepad.DPAD_LEFT)  != 0;
            bool right = (buttons & Gamepad.DPAD_RIGHT) != 0;

            if (up    && right) return 1;
            if (right && down)  return 3;
            if (down  && left)  return 5;
            if (left  && up)    return 7;
            if (up)    return 0;
            if (right) return 2;
            if (down)  return 4;
            if (left)  return 6;
            return 8; // neutral
        }

        // Gyro deg/s to the raw int16 a Sony report carries. The consumer
        // decodes it with no hardware calibration (PadForge's virtual pad
        // serves no IMU calibration feature report), so SDL3's PS5/PS4
        // HIDAPI fallback applies: deg/s = raw * 64 / GYRO_RES_PER_DEGREE,
        // with GYRO_RES_PER_DEGREE = 1024. Inverting: raw = deg/s * 16.
        // (The old 32767/2000 ≈ 16.38 over-scaled gyro by ~2.4 %.)
        private static short ScaleGyro(float degPerSec)
        {
            const float scale = 1024f / 64f; // = 16
            float v = degPerSec * scale;
            if (v >  32767f) return  32767;
            if (v < -32768f) return -32768;
            return (short)v;
        }

        // Accel g to raw int16. SDL3's no-calibration fallback is
        // g = raw / ACCEL_RES_PER_G, ACCEL_RES_PER_G = 8192, so
        // raw = g * 8192.
        private static short ScaleAccel(float gForce)
        {
            const float scale = 8192f;
            float v = gForce * scale;
            if (v >  32767f) return  32767;
            if (v < -32768f) return -32768;
            return (short)v;
        }

        // 12-bit X + 12-bit Y packed into 3 bytes. Sony firmware convention:
        //   byte[0] = X & 0xFF
        //   byte[1] = ((X >> 8) & 0x0F) | ((Y << 4) & 0xF0)
        //   byte[2] = (Y >> 4) & 0xFF
        private static void PackTouch12(Span<byte> dst, float xNorm, float yNorm, int w, int h)
        {
            int x = (int)(Math.Clamp(xNorm, 0f, 1f) * (w - 1));
            int y = (int)(Math.Clamp(yNorm, 0f, 1f) * (h - 1));
            dst[0] = (byte)(x & 0xFF);
            dst[1] = (byte)(((x >> 8) & 0x0F) | ((y << 4) & 0xF0));
            dst[2] = (byte)((y >> 4) & 0xFF);
        }

        private static void WriteI16(Span<byte> dst, int offset, short value)
        {
            dst[offset    ] = (byte)(value & 0xFF);
            dst[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteU32(Span<byte> dst, int offset, uint value)
        {
            dst[offset    ] = (byte)(value & 0xFF);
            dst[offset + 1] = (byte)((value >> 8)  & 0xFF);
            dst[offset + 2] = (byte)((value >> 16) & 0xFF);
            dst[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
