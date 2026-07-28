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
    /// at 7–8, vendor blob 9–62 (timestamp 9–10, battery 11, gyro 12–17,
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
        /// <summary>Packs the host-frame state into <paramref name="dest"/>
        /// (must be at least 63 bytes — exactly the data portion of a 64-byte
        /// USB Report 0x01).</summary>
        internal delegate void Packer(
            in Gamepad gp,
            in TouchpadState tp,
            in MotionSnapshot motion,
            byte batteryPercent,
            bool charging,
            uint frameCounter,
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

        // ── DS4 USB Report 0x01 (DS4_REPORT_EX) ─────────────────────────────
        // Touchpad resolution per Sony firmware: 1920 × 943.
        private const int Ds4TouchWidth = 1920;
        private const int Ds4TouchHeight = 943;

        private static void PackDs4UsbReport01(
            in Gamepad gp, in TouchpadState tp, in MotionSnapshot motion,
            byte batteryPercent, bool charging, uint frameCounter, Span<byte> dest)
        {
            dest.Clear();

            // Sticks (bytes 0-3): center 0x80. XInput Y is +up; DS4 firmware
            // is +down (HID convention) so Y axes are inverted.
            dest[0] = ToDs4Axis(gp.ThumbLX);
            dest[1] = ToDs4Axis((short)-gp.ThumbLY);
            dest[2] = ToDs4Axis(gp.ThumbRX);
            dest[3] = ToDs4Axis((short)-gp.ThumbRY);

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

            // Timestamp (bytes 9-10): 16-bit LE, ~187.5 LSB / ms in stock DS4
            // firmware. Just feed an incrementing 16-bit counter; games use
            // it to detect duplicate or stale frames, not to derive wall
            // clock time.
            ushort ts = (ushort)(frameCounter * 188);
            dest[9]  = (byte)(ts & 0xFF);
            dest[10] = (byte)(ts >> 8);

            // Battery (byte 11): legacy bBatteryLvl. Modern readers (DS4Windows,
            // SDL3, ds4drv) consult byte 30 instead, but byte 11 is the older
            // surface and harmless to populate.
            dest[11] = ScaleDs4BatteryNibble(batteryPercent, charging);

            // Gyro (bytes 12-17), Accel (bytes 18-23): int16 LE.
            WriteI16(dest, 12, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 14, ScaleGyro(motion.GyroYaw));
            WriteI16(dest, 16, ScaleGyro(motion.GyroRoll));
            WriteI16(dest, 18, ScaleAccel(motion.AccelX));
            WriteI16(dest, 20, ScaleAccel(motion.AccelY));
            WriteI16(dest, 22, ScaleAccel(motion.AccelZ));

            // Bytes 24-28 are reserved (zero from dest.Clear()).

            // bBatteryLvlSpecial (byte 30): low nibble = battery level scaled
            // to maxBatteryValue (8 when discharging, 11 when USB charging),
            // bit 4 = USB charging flag. DS4 readers (Ryochan7's DS4Reader,
            // DS4Windows) decode this byte for the canonical battery surface.
            dest[30] = (byte)(ScaleDs4BatteryNibble(batteryPercent, charging)
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
            byte batteryPercent, bool charging, uint frameCounter, Span<byte> dest)
        {
            dest.Clear();

            // Sticks + triggers inline (bytes 0-5). Y inverted vs XInput.
            dest[0] = ToDs4Axis(gp.ThumbLX);
            dest[1] = ToDs4Axis((short)-gp.ThumbLY);
            dest[2] = ToDs4Axis(gp.ThumbRX);
            dest[3] = ToDs4Axis((short)-gp.ThumbRY);
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
            // Only the physical source was honoured here, so a macro bound to
            // Touchpad reached the host on a virtual DS4 and silently did
            // nothing on a virtual DualSense.
            if (gp.IsButtonPressed(Gamepad.TOUCHPAD)) b9 |= 0x02;
            if (tp.Click)                             b9 |= 0x02; // Touchpad click
            // bit 0x04 = Mute (mic), bits 0x10-0x80 = DualSense Edge function /
            // paddle buttons. Left at 0 — wiring MISC1 / paddles into the
            // virtual output requires plumbing state.Buttons[11..15] into the
            // packer, which is a separate task.
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

            // Bytes 40-47 reserved (8 bytes, zeros).

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
