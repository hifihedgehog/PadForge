using System;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Packs an Extended slot's raw surface (RawHidState + TouchpadState +
    /// MotionSnapshot) into SInput's input report 0x01 for HIDMaestro's
    /// <c>sinput</c> persona (#584, asked in discussion #578), so a host that
    /// reads SInput through SDL's HIDAPI driver gets the frame a real SInput pad
    /// sends. The architecture is <see cref="ValveReportPackers"/>': the frame
    /// carries more than HMGamepadState models (two touchpads, an IMU with its
    /// own clock, four rear buttons), so it is packed app-side and submitted
    /// whole, and every raw slot resolves through
    /// <see cref="NintendoPreviewMap.IndexOf"/> against the SInput wire table.
    ///
    /// <para>The frame, report ID at byte 0, as SINPUT-LIB-HID 8dd1e5a builds
    /// it (sinput_lib_protocol.c:81-176, filled at :406-487) and SDL reads it
    /// (SDL fork 91e1a5afa3, SDL_hidapi_sinput.c:66-88, decoded at
    /// :839-1052):</para>
    /// <list type="bullet">
    /// <item>1 plug status, 2 charge percent.</item>
    /// <item>3 to 6 the 32 buttons, south at bit 0 (sinput_lib_types.h:222-283).
    /// SDL numbers the buttons the persona's feature reply enables in that bit
    /// order (:848-870) and its mapping binds button 0 to a, the south button
    /// (SDL_gamepad.c:967).</item>
    /// <item>7 to 14 the sticks, then 15 and 17 the triggers, signed 16-bit
    /// little-endian. SDL passes the sticks through unchanged (:889-912), so Y
    /// is down-positive, as the raw surface already is, and a trigger runs from
    /// -32768 at rest to 32767 at full pull (:917-930).</item>
    /// <item>19 the IMU timestamp in microseconds, which SDL turns into sensor
    /// time by its change, across the 32-bit wrap (:966-984).</item>
    /// <item>23 to 28 the accelerometer and 29 to 34 the gyroscope, in the
    /// ranges the feature reply advertises, <see cref="AccelRangeG"/> and
    /// <see cref="GyroRangeDps"/> (HIDMaestro#75). SDL rotates them into its
    /// own frame as SDL.x = -x, SDL.y = z, SDL.z = -y (:986-1013), and
    /// MotionSnapshot is in SDL's frame, so the packer applies the inverse.</item>
    /// <item>35 to 46 the two touch points: X, Y and pressure, which SDL reads
    /// as x / 65536 + 0.5, y / 65536 + 0.5 and pressure / 32768, a finger down
    /// while the pressure is above zero (:1017-1049).</item>
    /// </list>
    /// </summary>
    internal static class SInputReportPacker
    {
        /// <summary>The report's length with its ID (SDL_hidapi_sinput.c:48,
        /// SINPUT-LIB-HID sinput_lib_hid.c:44-112).</summary>
        internal const int ReportSize = 64;

        internal const byte ReportId = 0x01;

        /// <summary>The accelerometer range the persona advertises, ±8 g,
        /// SDL's SInput default (SDL_hidapi_sinput.c:64) and HOJA's
        /// (core_sinput.c:392).</summary>
        internal const int AccelRangeG = 8;

        /// <summary>The gyroscope range the persona advertises, ±2000 deg/s,
        /// SDL's SInput default (SDL_hidapi_sinput.c:63).</summary>
        internal const int GyroRangeDps = 2000;

        /// <summary>Plug status on the wire (SINPUT-LIB-HID
        /// sinput_lib_types.h:88-92, SDL_hidapi_sinput.c:932-960).</summary>
        internal const byte PlugUnknown = 0, PlugNoBattery = 1, PlugCharging = 2, PlugCharged = 3, PlugOnBattery = 4;

        /// <summary>True for a profile on the SInput wire.</summary>
        internal static bool IsSInputProfile(string profileId) => NintendoPreviewMap.IsSInput(profileId);

        /// <param name="batteryPercent">The slot's battery, -1 when its devices
        /// report none.</param>
        /// <param name="imuMicros">The frame's time in microseconds,
        /// <see cref="ValveReportPackers.MicrosNow"/>.</param>
        /// <param name="dest">At least <see cref="ReportSize"/> bytes. Byte 0
        /// is the report ID.</param>
        internal static void Pack(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            int batteryPercent,
            bool charging,
            uint imuMicros,
            Span<byte> dest)
        {
            const string P = "sinput";
            dest.Slice(0, ReportSize).Clear();
            dest[0] = ReportId;

            // A slot whose devices report no battery says so: 0 is "not
            // reported", and SDL then sends no power state at all (:961-963).
            if (batteryPercent < 0)
            {
                dest[1] = PlugUnknown;
            }
            else
            {
                int pct = Math.Clamp(batteryPercent, 0, 100);
                dest[1] = charging ? (pct >= 100 ? PlugCharged : PlugCharging) : PlugOnBattery;
                dest[2] = (byte)pct;
            }

            int lt = ValveReportPackers.Trigger(raw, ValveReportPackers.AxLT);
            int rt = ValveReportPackers.Trigger(raw, ValveReportPackers.AxRT);
            var d = ValveReportPackers.DPad(raw, P);
            var pads = ValveReportPackers.Pads(raw, tp, P);

            // Byte 3: south, east, west, north, then the D-pad's up, down,
            // left and right (sinput_lib_types.h:226-233).
            byte b3 = 0;
            if (ValveReportPackers.Btn(raw, P, "ButtonA")) b3 |= 0x01;
            if (ValveReportPackers.Btn(raw, P, "ButtonB")) b3 |= 0x02;
            if (ValveReportPackers.Btn(raw, P, "ButtonX")) b3 |= 0x04;
            if (ValveReportPackers.Btn(raw, P, "ButtonY")) b3 |= 0x08;
            if (d.up) b3 |= 0x10;
            if (d.down) b3 |= 0x20;
            if (d.left) b3 |= 0x40;
            if (d.right) b3 |= 0x80;
            dest[3] = b3;

            // Byte 4: the stick clicks, the bumpers, the digital trigger bits
            // and the upper grips, left then right (sinput_lib_types.h:242-249).
            // The trigger bits
            // set at the pull the Valve packers use, TriggerClickPull, and SDL
            // reads them as Misc 3 and Misc 4 beside the analog axes, its
            // dual-stage shape (SDL_gamepad.c:996-1003).
            byte b4 = 0;
            if (ValveReportPackers.Btn(raw, P, "LeftThumbButton")) b4 |= 0x01;
            if (ValveReportPackers.Btn(raw, P, "RightThumbButton")) b4 |= 0x02;
            if (ValveReportPackers.Btn(raw, P, "LeftShoulder")) b4 |= 0x04;
            if (ValveReportPackers.Btn(raw, P, "RightShoulder")) b4 |= 0x08;
            if (lt >= ValveReportPackers.TriggerClickPull) b4 |= 0x10;
            if (rt >= ValveReportPackers.TriggerClickPull) b4 |= 0x20;
            if (ValveReportPackers.Btn(raw, P, "Paddle2")) b4 |= 0x40;   // left paddle 1, SDL LEFT_PADDLE1
            if (ValveReportPackers.Btn(raw, P, "Paddle1")) b4 |= 0x80;   // right paddle 1, SDL RIGHT_PADDLE1
            dest[4] = b4;

            // Byte 5: Start, Back, Guide, Share, the lower grips and the two
            // touchpad clicks (sinput_lib_types.h:258-265).
            byte b5 = 0;
            if (ValveReportPackers.Btn(raw, P, "ButtonStart")) b5 |= 0x01;
            if (ValveReportPackers.Btn(raw, P, "ButtonBack")) b5 |= 0x02;
            if (ValveReportPackers.Btn(raw, P, "ButtonGuide")) b5 |= 0x04;
            if (ValveReportPackers.Btn(raw, P, "ButtonShare")) b5 |= 0x08;
            if (ValveReportPackers.Btn(raw, P, "Paddle4")) b5 |= 0x10;   // left paddle 2, SDL LEFT_PADDLE2
            if (ValveReportPackers.Btn(raw, P, "Paddle3")) b5 |= 0x20;   // right paddle 2, SDL RIGHT_PADDLE2
            if (pads.lClick) b5 |= 0x40;
            if (pads.rClick) b5 |= 0x80;
            dest[5] = b5;
            // Byte 6, power and Misc 4 to 10, stays clear: the persona's
            // feature reply enables none of it.

            ValveReportPackers.WriteI16(dest, 7, ValveReportPackers.Axis(raw, ValveReportPackers.AxLX));
            ValveReportPackers.WriteI16(dest, 9, ValveReportPackers.Axis(raw, ValveReportPackers.AxLY));
            ValveReportPackers.WriteI16(dest, 11, ValveReportPackers.Axis(raw, ValveReportPackers.AxRX));
            ValveReportPackers.WriteI16(dest, 13, ValveReportPackers.Axis(raw, ValveReportPackers.AxRY));
            // The raw surface carries a trigger from short.MinValue at rest to
            // short.MaxValue at full pull, the wire's own range.
            ValveReportPackers.WriteI16(dest, 15, ValveReportPackers.Axis(raw, ValveReportPackers.AxLT));
            ValveReportPackers.WriteI16(dest, 17, ValveReportPackers.Axis(raw, ValveReportPackers.AxRT));

            ValveReportPackers.WriteU32(dest, 19, imuMicros);
            ValveReportPackers.WriteI16(dest, 23, ScaleAccel(-motion.AccelX));
            ValveReportPackers.WriteI16(dest, 25, ScaleAccel(-motion.AccelZ));
            ValveReportPackers.WriteI16(dest, 27, ScaleAccel(motion.AccelY));
            ValveReportPackers.WriteI16(dest, 29, ScaleGyro(-motion.GyroPitch));
            ValveReportPackers.WriteI16(dest, 31, ScaleGyro(-motion.GyroRoll));
            ValveReportPackers.WriteI16(dest, 33, ScaleGyro(motion.GyroYaw));

            // The slot's two-finger surface splits as the Valve packers split
            // it, finger 0 on the left pad and finger 1 on the right. SDL
            // reads Y the way the surface keeps it, top down, so neither axis
            // flips.
            ValveReportPackers.WriteI16(dest, 35, pads.lTouch ? ValveReportPackers.NormToPad(tp.X0) : (short)0);
            ValveReportPackers.WriteI16(dest, 37, pads.lTouch ? ValveReportPackers.NormToPad(tp.Y0) : (short)0);
            ValveReportPackers.WriteI16(dest, 39, (short)ValveReportPackers.PadPressure(pads.lTouch, pads.lClick));
            ValveReportPackers.WriteI16(dest, 41, pads.rTouch ? ValveReportPackers.NormToPad(tp.X1) : (short)0);
            ValveReportPackers.WriteI16(dest, 43, pads.rTouch ? ValveReportPackers.NormToPad(tp.Y1) : (short)0);
            ValveReportPackers.WriteI16(dest, 45, (short)ValveReportPackers.PadPressure(pads.rTouch, pads.rClick));
        }

        /// <summary>A rotation rate in deg/s over the advertised ±2000 deg/s, the
        /// inverse of SDL's scale, wire * (pi / 180) / (32768 / 2000)
        /// (SDL_hidapi_sinput.c:260-263).</summary>
        internal static short ScaleGyro(float dps)
            => (short)Math.Clamp((int)MathF.Round(dps / GyroRangeDps * 32768f), short.MinValue, short.MaxValue);

        /// <summary>An acceleration in g over the advertised ±8 g, the
        /// inverse of SDL's scale, wire * 9.80665 / (32768 / 8)
        /// (SDL_hidapi_sinput.c:266-269).</summary>
        internal static short ScaleAccel(float g)
            => (short)Math.Clamp((int)MathF.Round(g / AccelRangeG * 32768f), short.MinValue, short.MaxValue);
    }
}
