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
            byte battery,
            byte connectState,
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
            byte battery, byte connectState, uint frameCounter, Span<byte> dest)
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
            if (gp.LeftTrigger  > 0x80FF)                   b5 |= 0x04; // L2 digital
            if (gp.RightTrigger > 0x80FF)                   b5 |= 0x08; // R2 digital
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

            // Battery (byte 11): low nibble = level 0..10, high nibble = flags
            // (the OS rolls these into the lvlSpecial byte at 30, but DS4Windows
            // and PS Remote Play also read this one).
            dest[11] = (byte)(battery & 0x0F);

            // Gyro (bytes 12-17), Accel (bytes 18-23): int16 LE.
            WriteI16(dest, 12, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 14, ScaleGyro(motion.GyroYaw));
            WriteI16(dest, 16, ScaleGyro(motion.GyroRoll));
            WriteI16(dest, 18, ScaleAccel(motion.AccelX));
            WriteI16(dest, 20, ScaleAccel(motion.AccelY));
            WriteI16(dest, 22, ScaleAccel(motion.AccelZ));

            // Bytes 24-28 are reserved (zero from dest.Clear()).

            // Battery level + USB/charging flags (byte 30).
            dest[30] = (byte)((battery / 10) | (connectState != 0 ? 0x10 : 0x00));

            // Bytes 31-32 reserved.

            // Touch packets (bytes 33-59): 3 packets × 9 bytes each. PadForge
            // tracks a single current frame, so packet count = 1 if either
            // finger is down, else 0. Previous-frame slots stay zero — that
            // matches what real DS4 firmware does between contact events.
            int touchPackets = (tp.Down0 || tp.Down1) ? 1 : 0;
            dest[33] = (byte)touchPackets;
            if (touchPackets > 0)
            {
                dest[34] = tp.PacketCounter;             // packet timestamp byte
                EncodeDs4Touch(dest.Slice(35, 8), tp);
            }

            // Bytes 61-62 padding (zero from Clear()).
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
            byte battery, byte connectState, uint frameCounter, Span<byte> dest)
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

            // Buttons + hat (bytes 7-10).
            // byte 7: bits 0-3 = D-pad 0..7 / 0x8=neutral; bits 4-7 = face buttons.
            // byte 8: shoulder + trigger digital + system buttons.
            // byte 9: PS + touchpad click + counter (low 6 bits).
            // byte 10: vendor / extra (mute on DualSense).
            dest[7] = (byte)(EncodeDpad(gp.Buttons)
                           | (gp.IsButtonPressed(Gamepad.X) ? 0x10 : 0)   // Square
                           | (gp.IsButtonPressed(Gamepad.A) ? 0x20 : 0)   // Cross
                           | (gp.IsButtonPressed(Gamepad.B) ? 0x40 : 0)   // Circle
                           | (gp.IsButtonPressed(Gamepad.Y) ? 0x80 : 0)); // Triangle

            byte b8 = 0;
            if (gp.IsButtonPressed(Gamepad.LEFT_SHOULDER))  b8 |= 0x01;
            if (gp.IsButtonPressed(Gamepad.RIGHT_SHOULDER)) b8 |= 0x02;
            if (gp.LeftTrigger  > 0x80FF)                   b8 |= 0x04;
            if (gp.RightTrigger > 0x80FF)                   b8 |= 0x08;
            if (gp.IsButtonPressed(Gamepad.BACK))           b8 |= 0x10; // Create
            if (gp.IsButtonPressed(Gamepad.START))          b8 |= 0x20; // Options
            if (gp.IsButtonPressed(Gamepad.LEFT_THUMB))     b8 |= 0x40;
            if (gp.IsButtonPressed(Gamepad.RIGHT_THUMB))    b8 |= 0x80;
            dest[8] = b8;

            byte b9 = (byte)((frameCounter >> 8) & 0x3F); // counter high
            if (gp.IsButtonPressed(Gamepad.GUIDE))     b9 |= 0x01; // PS
            if (gp.IsButtonPressed(Gamepad.TOUCHPAD)) b9 |= 0x02;
            if (tp.Click)                              b9 |= 0x02;
            dest[9] = b9;

            // byte 10 stays zero (mute, future button bits).

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

            // Sensor timestamp (bytes 27-30): 32-bit LE microseconds.
            WriteU32(dest, 27, (uint)(motion.TimestampUs & 0xFFFFFFFFL));

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

            // Battery (byte 52): high nibble = status, low nibble = level 0..10.
            // Status: 0x0 = discharging, 0x1 = charging, 0x2 = full.
            byte batteryStatusNibble = connectState != 0 ? (byte)0x10 : (byte)0x00;
            dest[52] = (byte)(batteryStatusNibble | ((battery / 10) & 0x0F));

            // Connect state (byte 53): 0x08 = USB, per SDL3 PS5 parser.
            dest[53] = connectState;

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

        // Gyro range ±2000 deg/s mapped to int16 (matches HandheldCompanion's
        // DualShock4Target reference scaling).
        private static short ScaleGyro(float degPerSec)
        {
            const float scale = 32767f / 2000f;
            float v = degPerSec * scale;
            if (v >  32767f) return  32767;
            if (v < -32768f) return -32768;
            return (short)v;
        }

        // Accel range ±4 g mapped to int16 (same HC reference).
        private static short ScaleAccel(float gForce)
        {
            const float scale = 32767f / 4f;
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
