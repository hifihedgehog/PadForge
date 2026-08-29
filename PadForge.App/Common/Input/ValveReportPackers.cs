using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Models2D;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Packs an Extended slot's raw surface (RawHidState + TouchpadState +
    /// MotionSnapshot) into the native input frame of a Valve controller, so
    /// HM's <c>SubmitRawReport</c> can deliver the report Steam expects from
    /// the real device (#338, asked in discussion #337).
    ///
    /// <para>Same architecture as <see cref="SonyReportPackers"/>, and for
    /// the same reason: each persona's input report carries more than
    /// HMGamepadState models (two trackpads, four rear buttons, IMU), so the
    /// frame is packed app-side and submitted whole.</para>
    ///
    /// <para>Every raw slot is resolved through
    /// <see cref="NintendoPreviewMap.IndexOf"/> against the profile's wire
    /// table. The tables are the ONLY place a slot order is written down;
    /// the mapping grid, the labels, the preview bridge and these packers all
    /// read the same one, so a slot cannot mean one control in the grid and
    /// another on the wire.</para>
    ///
    /// <para>Three frames, three sources:</para>
    /// <list type="bullet">
    /// <item>Steam Deck: SDL <c>steam/controller_structs.h</c>
    /// (SteamDeckStatePacket_t) and <c>SDL_hidapi_steamdeck.c</c>, cross-
    /// checked against HandheldCompanion <c>Targets/SteamDeckTarget.cs</c>,
    /// proven against live Steam per discussion #337.</item>
    /// <item>Steam Controller 2015: SDL <c>SDL_hidapi_steam.c</c> lines
    /// 118-140 (the STEAM_*_MASK bits) and <c>controller_structs.h</c>
    /// (ValveInReportHeader_t + ValveControllerStatePacket_t). HIDMaestro's
    /// own extended-report spec for steam-controller-composite lists the
    /// same offsets, and a test asserts this packer against that spec.</item>
    /// <item>Steam Controller 2026: SDL <c>controller_structs.h</c>
    /// (TritonMTUFull_t, report 0x42, 54 bytes) as reproduced verbatim and
    /// checked against nine thousand captured frames in sc2-research
    /// <c>docs/HID_REPORT_FORMAT.md</c>, which also resolves the button bits
    /// HIDMaestro's spec leaves unnamed.</item>
    /// </list>
    /// </summary>
    internal static class ValveReportPackers
    {
        /// <summary>Largest frame any Valve persona submits. The Deck and
        /// the 2015 pad use 64-byte id-less reports; the 2026 pad's report
        /// 0x42 is 54 bytes including its id byte.</summary>
        internal const int MaxReportSize = 64;

        internal delegate void PackerFn(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest);

        internal sealed class Packer
        {
            public string ProfileId;
            public int Size;
            public PackerFn Pack;
        }

        /// <summary>HM profile id to packer. Both 2015 profiles share one
        /// frame: the plain umdf2 profile exposes the wired controller's
        /// 64-byte vendor report and the composite persona's extended report
        /// is that same ValveInReport.</summary>
        private static readonly Dictionary<string, Packer> ByProfileId =
            new Dictionary<string, Packer>(StringComparer.OrdinalIgnoreCase)
            {
                { "steam-deck-composite",       new Packer { ProfileId = "steam-deck-composite",       Size = 64, Pack = PackDeckReport09 } },
                { "steam-controller",           new Packer { ProfileId = "steam-controller",           Size = 64, Pack = PackSteamController2015 } },
                { "steam-controller-composite", new Packer { ProfileId = "steam-controller-composite", Size = 64, Pack = PackSteamController2015 } },
                { "steam-controller-2",         new Packer { ProfileId = "steam-controller-2",         Size = 54, Pack = PackSteamController2026 } },
            };

        internal static Packer ForProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            ByProfileId.TryGetValue(profileId, out var p);
            return p;
        }

        // ── Raw surface readers ──────────────────────────────────────────

        private static bool Btn(in RawHidState r, string profileId, string role)
        {
            int i = NintendoPreviewMap.IndexOf(profileId, role);
            if (i < 0 || r.Buttons == null) return false;
            int w = i / 32;
            return w < r.Buttons.Length && (r.Buttons[w] & (1u << (i % 32))) != 0;
        }

        private static short Axis(in RawHidState r, int i)
            => r.Axes != null && i < r.Axes.Length ? r.Axes[i] : (short)0;

        /// <summary>A trigger slot, rescaled from the raw surface's range to
        /// the wire's.
        ///
        /// <para>The engine stores a trigger BIPOLAR, rest at short.MinValue
        /// and full pull at short.MaxValue (MapToRawTriggerAxis, whose own
        /// comment is "Trigger rest is short.MinValue"). Every Valve wire
        /// carries it UNSIGNED, 0 to 32767, which is what SDL's drivers
        /// decode back with * 2 - 32768 (SDL_hidapi_steam.c 1645,
        /// _steamdeck.c 234, _steam_triton.c 222).</para>
        ///
        /// <para>These packers CLAMPED to [0, 32767] instead of rescaling.
        /// Rest and full pull both came out right by luck, and everything
        /// between did not: the whole lower half of the travel read as zero,
        /// then the upper half swept the entire range.</para></summary>
        private static int Trigger(in RawHidState r, int i)
            => r.Axes != null && i < r.Axes.Length ? (r.Axes[i] + 32768) / 2 : 0;

        // Valve axis slots, the interleaved [LX LY LT | RX RY RT] layout
        // ComputeAxisLayout produces for two sticks and two triggers.
        private const int AxLX = 0, AxLY = 1, AxLT = 2, AxRX = 3, AxRY = 4, AxRT = 5;

        private static (bool up, bool right, bool down, bool left) DecodePov(in RawHidState r)
        {
            int pov = r.Povs != null && r.Povs.Length > 0 ? r.Povs[0] : -1;
            if (pov < 0) return (false, false, false, false);
            // Hundredths of degrees, 8-way: each cardinal owns +/- 67.5 deg.
            return (pov > 29250 || pov < 6750,
                    pov > 2250 && pov < 15750,
                    pov > 11250 && pov < 24750,
                    pov > 20250 && pov < 33750);
        }

        /// <summary>The D-pad as four booleans whichever way the wire
        /// carries it: discrete buttons where the table has them, else the
        /// hat.</summary>
        private static (bool up, bool right, bool down, bool left) DPad(in RawHidState r, string profileId)
        {
            if (!NintendoPreviewMap.DPadIsHat(profileId))
                return (Btn(r, profileId, "DPadUp"), Btn(r, profileId, "DPadRight"),
                        Btn(r, profileId, "DPadDown"), Btn(r, profileId, "DPadLeft"));
            return DecodePov(r);
        }

        /// <summary>The two trackpads from the slot's single two-finger
        /// touch surface: finger 0 is the left pad, finger 1 the right, the
        /// same split the web Deck layout feeds. A pad click comes from its
        /// own raw slot, or from the shared touch click on whichever pad is
        /// touched (the left one when neither is).</summary>
        private static (bool lTouch, bool rTouch, bool lClick, bool rClick) Pads(
            in RawHidState raw, in TouchpadState tp, string profileId)
        {
            bool lTouch = tp.Down0, rTouch = tp.Down1;
            bool lClick = Btn(raw, profileId, "LeftTouchpadClick");
            bool rClick = Btn(raw, profileId, "RightTouchpadClick");
            if (tp.Click)
            {
                if (rTouch && !lTouch) rClick = true;
                else lClick = true;
            }
            return (lTouch, rTouch, lClick, rClick);
        }

        private static short NormToPad(float v)
            => (short)Math.Clamp((int)MathF.Round((v * 2f - 1f) * short.MaxValue), short.MinValue, short.MaxValue);

        private static short NormToPadY(float v) => NegateSat(NormToPad(v));

        private static short NegateSat(short v)
            => v == short.MinValue ? short.MaxValue : (short)(-v);

        private static short ScaleGyro(float dps)
            => (short)Math.Clamp((int)MathF.Round(dps / 2000f * 32768f), short.MinValue, short.MaxValue);

        private static short ScaleAccel(float g)
            => (short)Math.Clamp((int)MathF.Round(g / 2f * 32768f), short.MinValue, short.MaxValue);

        private static void WriteI16(Span<byte> b, int off, short v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU16(Span<byte> b, int off, ushort v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(Span<byte> b, int off, uint v)
        {
            b[off] = (byte)(v & 0xFF);
            b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF);
            b[off + 3] = (byte)((v >> 24) & 0xFF);
        }

        // ── Steam Deck, ValveInReport type 0x09 ─────────────────────────

        /// <summary>Header 01 00 09 40, unPacketNum at 4, buttons at 8..14,
        /// pad coordinates 16..22, accel 24, gyro 30, quaternion 36 (zeroed:
        /// the slot has no fused orientation), raw triggers 44 (u16), sticks
        /// 48..54, pad pressures 56..58. unPacketNum increments: SDL's
        /// struct comment licenses consumers to skip a frame whose number
        /// has not changed, so a constant would freeze them.</summary>
        private static void PackDeckReport09(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest)
        {
            const string P = "steam-deck-composite";
            dest.Slice(0, 64).Clear();
            dest[0] = 0x01;
            dest[1] = 0x00;
            dest[2] = 0x09;
            dest[3] = 0x40;
            WriteU32(dest, 4, packetNum);

            short lx = Axis(raw, AxLX), ly = Axis(raw, AxLY);
            short rx = Axis(raw, AxRX), ry = Axis(raw, AxRY);
            int lt = Trigger(raw, AxLT);
            int rt = Trigger(raw, AxRT);

            // ulButtonsL bits 0..7 (STEAMDECK_LBUTTON: R2 0x01, L2 0x02,
            // R 0x04, L 0x08, Y 0x10, B 0x20, X 0x40, A 0x80). The digital
            // trigger bits follow the analog pull, HC's rule (> 0).
            byte b8 = 0;
            if (rt > 0) b8 |= 0x01;
            if (lt > 0) b8 |= 0x02;
            if (Btn(raw, P, "RightShoulder")) b8 |= 0x04;
            if (Btn(raw, P, "LeftShoulder")) b8 |= 0x08;
            if (Btn(raw, P, "ButtonY")) b8 |= 0x10;
            if (Btn(raw, P, "ButtonB")) b8 |= 0x20;
            if (Btn(raw, P, "ButtonX")) b8 |= 0x40;
            if (Btn(raw, P, "ButtonA")) b8 |= 0x80;
            dest[8] = b8;

            // bits 8..15: DPadUp 0x01, Right 0x02, Left 0x04, Down 0x08,
            // View(Back) 0x10, Steam(Guide) 0x20, Menu(Start) 0x40, L5 0x80.
            // SDL_hidapi_steamdeck.c lines 61-63: STEAMDECK_LBUTTON_VIEW =
            // 0x00001000, _STEAM = 0x00002000, _MENU = 0x00004000, and SDL
            // maps VIEW to SDL_GAMEPAD_BUTTON_BACK and MENU to _START.
            // HIDMaestro's spec for this persona names the same bits. An
            // earlier version of this packer had View and Menu swapped, and
            // its test pinned the swap; a spec-driven test caught it.
            var d = DPad(raw, P);
            byte b9 = 0;
            if (d.up) b9 |= 0x01;
            if (d.right) b9 |= 0x02;
            if (d.left) b9 |= 0x04;
            if (d.down) b9 |= 0x08;
            if (Btn(raw, P, "ButtonBack")) b9 |= 0x10;
            if (Btn(raw, P, "ButtonGuide")) b9 |= 0x20;
            if (Btn(raw, P, "ButtonStart")) b9 |= 0x40;
            if (Btn(raw, P, "Paddle4")) b9 |= 0x80;     // L5, SDL LEFT_PADDLE2
            dest[9] = b9;

            // bits 16..23: R5 0x01, LeftPadClick 0x02, RightPadClick 0x04,
            // LeftPadTouch 0x08, RightPadTouch 0x10, L3 0x40.
            var pads = Pads(raw, tp, P);
            byte b10 = 0;
            if (Btn(raw, P, "Paddle3")) b10 |= 0x01;    // R5, SDL RIGHT_PADDLE2
            if (pads.lClick) b10 |= 0x02;
            if (pads.rClick) b10 |= 0x04;
            if (pads.lTouch) b10 |= 0x08;
            if (pads.rTouch) b10 |= 0x10;
            if (Btn(raw, P, "LeftThumbButton")) b10 |= 0x40;
            dest[10] = b10;

            // bits 24..31: R3 0x04.
            if (Btn(raw, P, "RightThumbButton")) dest[11] = 0x04;

            // ulButtonsH byte 13: L4 0x02 (SDL LEFT_PADDLE1), R4 0x04
            // (RIGHT_PADDLE1). Stick-touch bits stay 0: capacitive stick
            // touch has no source on this surface.
            byte b13 = 0;
            if (Btn(raw, P, "Paddle2")) b13 |= 0x02;
            if (Btn(raw, P, "Paddle1")) b13 |= 0x04;
            dest[13] = b13;

            // byte 14: QAM 0x04.
            if (Btn(raw, P, "ButtonQuickAccess")) dest[14] = 0x04;

            // Pad coordinates, signed i16, +Y up, 0 center. The slot's touch
            // frame is 0..1 top-down, so X spans the full range and Y flips.
            WriteI16(dest, 16, pads.lTouch ? NormToPad(tp.X0) : (short)0);
            WriteI16(dest, 18, pads.lTouch ? NormToPadY(tp.Y0) : (short)0);
            WriteI16(dest, 20, pads.rTouch ? NormToPad(tp.X1) : (short)0);
            WriteI16(dest, 22, pads.rTouch ? NormToPadY(tp.Y1) : (short)0);

            // IMU. MotionSnapshot is the SDL-native sensor frame in DSU
            // units (accel g, gyro deg/s). The Deck frame map is the exact
            // inverse of SDL_hidapi_steamdeck.c's decode: SDL.x = sGyroX,
            // SDL.y = sGyroZ, SDL.z = -sGyroY, gyro full scale 2000 deg/s
            // over i16, accel full scale 2 g.
            WriteI16(dest, 24, ScaleAccel(motion.AccelX));
            WriteI16(dest, 26, ScaleAccel(-motion.AccelZ));
            WriteI16(dest, 28, ScaleAccel(motion.AccelY));
            WriteI16(dest, 30, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 32, ScaleGyro(-motion.GyroRoll));
            WriteI16(dest, 34, ScaleGyro(motion.GyroYaw));
            // Quaternion 36..43 stays zero.

            WriteU16(dest, 44, (ushort)lt);
            WriteU16(dest, 46, (ushort)rt);
            WriteI16(dest, 48, lx);
            WriteI16(dest, 50, NegateSat(ly));
            WriteI16(dest, 52, rx);
            WriteI16(dest, 54, NegateSat(ry));

            WriteU16(dest, 56, pads.lClick ? ushort.MaxValue : (ushort)0);
            WriteU16(dest, 58, pads.rClick ? ushort.MaxValue : (ushort)0);
        }

        // ── Steam Controller 2015, ValveInReport type 0x01 ──────────────

        // SDL_hidapi_steam.c lines 118-140. Bit numbers of the 24-bit
        // ulButtons field at bytes 8..10.
        private const int Sc15RightTrigger = 0, Sc15LeftTrigger = 1;
        private const int Sc15RightBumper = 2, Sc15LeftBumper = 3;
        private const int Sc15Y = 4, Sc15B = 5, Sc15X = 6, Sc15A = 7;
        private const int Sc15DpadUp = 8, Sc15DpadRight = 9, Sc15DpadLeft = 10, Sc15DpadDown = 11;
        private const int Sc15Menu = 12, Sc15Steam = 13, Sc15Escape = 14;      // Select, Steam, Start
        private const int Sc15BackLeft = 15, Sc15BackRight = 16;               // grips
        private const int Sc15LeftPadClicked = 17, Sc15RightPadClicked = 18;
        private const int Sc15LeftPadFinger = 19, Sc15RightPadFinger = 20;
        private const int Sc15JoystickButton = 22;

        /// <summary>ValveInReportHeader_t: version 0x0001, type 0x01
        /// (ID_CONTROLLER_STATE), length 60. Then unPacketNum at 4, the
        /// 24-bit button mask at 8..10 with the 8-bit triggers at 11 and 12
        /// sharing the same 8-byte union, left pad at 16/18, right pad at
        /// 20/22, the 16-bit triggers at 24/26 (sent redundantly over wire,
        /// SDL's comment), accel at 28..32, gyro at 34..38, quaternion at
        /// 40..46 zeroed. controller_structs.h ValveControllerStatePacket_t;
        /// HIDMaestro's steam-controller-composite spec lists the same
        /// offsets.
        ///
        /// <para>The right pad rides the right-stick axes when no finger is
        /// on the touch surface, which is how SDL and Steam treat that pad
        /// (SDL_hidapi_steam.c maps it to the right stick), and the finger
        /// bit follows any deflection so Steam sees pad input. The left
        /// pad's four directional click zones are the wire's D-pad bits and
        /// come from the hat.</para></summary>
        private static void PackSteamController2015(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest)
        {
            const string P = "steam-controller";
            dest.Slice(0, 64).Clear();
            dest[0] = 0x01;
            dest[1] = 0x00;
            dest[2] = 0x01;
            dest[3] = 0x3C;
            WriteU32(dest, 4, packetNum);

            int lt = Trigger(raw, AxLT);
            int rt = Trigger(raw, AxRT);
            short lx = Axis(raw, AxLX), ly = Axis(raw, AxLY);
            short rx = Axis(raw, AxRX), ry = Axis(raw, AxRY);

            var d = DPad(raw, P);
            var pads = Pads(raw, tp, P);
            bool rightPadFromStick = !pads.rTouch && (rx != 0 || ry != 0);

            uint bits = 0;
            void Set(int bit, bool on) { if (on) bits |= 1u << bit; }
            Set(Sc15RightTrigger, rt > 0);
            Set(Sc15LeftTrigger, lt > 0);
            Set(Sc15RightBumper, Btn(raw, P, "RightShoulder"));
            Set(Sc15LeftBumper, Btn(raw, P, "LeftShoulder"));
            Set(Sc15Y, Btn(raw, P, "ButtonY"));
            Set(Sc15B, Btn(raw, P, "ButtonB"));
            Set(Sc15X, Btn(raw, P, "ButtonX"));
            Set(Sc15A, Btn(raw, P, "ButtonA"));
            Set(Sc15DpadUp, d.up);
            Set(Sc15DpadRight, d.right);
            Set(Sc15DpadLeft, d.left);
            Set(Sc15DpadDown, d.down);
            Set(Sc15Menu, Btn(raw, P, "ButtonBack"));
            Set(Sc15Steam, Btn(raw, P, "ButtonGuide"));
            Set(Sc15Escape, Btn(raw, P, "ButtonStart"));
            Set(Sc15BackLeft, Btn(raw, P, "LeftGrip"));
            Set(Sc15BackRight, Btn(raw, P, "RightGrip"));
            Set(Sc15LeftPadClicked, pads.lClick);
            Set(Sc15RightPadClicked, pads.rClick);
            Set(Sc15LeftPadFinger, pads.lTouch);
            Set(Sc15RightPadFinger, pads.rTouch || rightPadFromStick);
            Set(Sc15JoystickButton, Btn(raw, P, "LeftThumbButton"));
            dest[8] = (byte)(bits & 0xFF);
            dest[9] = (byte)((bits >> 8) & 0xFF);
            dest[10] = (byte)((bits >> 16) & 0xFF);
            dest[11] = (byte)(lt >> 7);     // 8-bit trigger, 0..255
            dest[12] = (byte)(rt >> 7);

            // Pads: signed i16, +Y up, 0 center. Left from finger 0. Right
            // from finger 1 when touched, else from the right-stick axes.
            WriteI16(dest, 16, pads.lTouch ? NormToPad(tp.X0) : (short)0);
            WriteI16(dest, 18, pads.lTouch ? NormToPadY(tp.Y0) : (short)0);
            if (pads.rTouch)
            {
                WriteI16(dest, 20, NormToPad(tp.X1));
                WriteI16(dest, 22, NormToPadY(tp.Y1));
            }
            else
            {
                WriteI16(dest, 20, rx);
                WriteI16(dest, 22, NegateSat(ry));
            }

            WriteU16(dest, 24, (ushort)lt);
            WriteU16(dest, 26, (ushort)rt);

            // IMU, same frame map as the Deck (SDL_hidapi_steam.c decodes
            // the 2015 pad's sensors the same way).
            WriteI16(dest, 28, ScaleAccel(motion.AccelX));
            WriteI16(dest, 30, ScaleAccel(-motion.AccelZ));
            WriteI16(dest, 32, ScaleAccel(motion.AccelY));
            WriteI16(dest, 34, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 36, ScaleGyro(-motion.GyroRoll));
            WriteI16(dest, 38, ScaleGyro(motion.GyroYaw));
            // Quaternion 40..46 stays zero.
        }

        // ── Steam Controller 2026, report 0x42 (TritonMTUFull_t) ────────

        // sc2-research docs/HID_REPORT_FORMAT.md, TritonButtons, bytes
        // 0x02..0x05 as a little-endian uint32. Bit numbers.
        private const int TrA = 0, TrB = 1, TrX = 2, TrY = 3, TrQam = 4, TrR3 = 5, TrView = 6, TrR4 = 7;
        private const int TrR5 = 8, TrRB = 9, TrDpadDown = 10, TrDpadRight = 11, TrDpadLeft = 12, TrDpadUp = 13, TrMenu = 14, TrL3 = 15;
        private const int TrSteam = 16, TrL4 = 17, TrL5 = 18, TrLB = 19, TrRStickTouch = 20, TrRPadTouch = 21, TrRPadClick = 22, TrRTrigClick = 23;
        private const int TrLStickTouch = 24, TrLPadTouch = 25, TrLPadClick = 26, TrLTrigClick = 27, TrRGripTouch = 28, TrLGripTouch = 29;

        /// <summary>Report 0x42, 54 bytes: id, seq, uint32 buttons at 2,
        /// triggers i16 at 6/8, sticks at 10..16, left pad 18/20 with
        /// pressure at 22, right pad 24/26 with pressure at 28, IMU timestamp
        /// at 30, accel 34..38, gyro 40..44, quaternion 46..52 zeroed. Y on
        /// the left pad is flipped on the wire (SDL3 PR #15528). Triggers
        /// are int16 on this wire, so the 0..32767 pull maps straight in.
        /// Capacitive stick and grip touch have no source and stay 0.</summary>
        private static void PackSteamController2026(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest)
        {
            const string P = "steam-controller-2";
            dest.Slice(0, 54).Clear();
            dest[0] = 0x42;
            dest[1] = (byte)(packetNum & 0xFF);

            int lt = Trigger(raw, AxLT);
            int rt = Trigger(raw, AxRT);
            short lx = Axis(raw, AxLX), ly = Axis(raw, AxLY);
            short rx = Axis(raw, AxRX), ry = Axis(raw, AxRY);
            var d = DPad(raw, P);
            var pads = Pads(raw, tp, P);

            uint bits = 0;
            void Set(int bit, bool on) { if (on) bits |= 1u << bit; }
            Set(TrA, Btn(raw, P, "ButtonA"));
            Set(TrB, Btn(raw, P, "ButtonB"));
            Set(TrX, Btn(raw, P, "ButtonX"));
            Set(TrY, Btn(raw, P, "ButtonY"));
            Set(TrQam, Btn(raw, P, "ButtonQuickAccess"));
            Set(TrR3, Btn(raw, P, "RightThumbButton"));
            Set(TrView, Btn(raw, P, "ButtonBack"));
            Set(TrR4, Btn(raw, P, "Paddle1"));
            Set(TrR5, Btn(raw, P, "Paddle3"));
            Set(TrRB, Btn(raw, P, "RightShoulder"));
            Set(TrDpadDown, d.down);
            Set(TrDpadRight, d.right);
            Set(TrDpadLeft, d.left);
            Set(TrDpadUp, d.up);
            Set(TrMenu, Btn(raw, P, "ButtonStart"));
            Set(TrL3, Btn(raw, P, "LeftThumbButton"));
            Set(TrSteam, Btn(raw, P, "ButtonGuide"));
            Set(TrL4, Btn(raw, P, "Paddle2"));
            Set(TrL5, Btn(raw, P, "Paddle4"));
            Set(TrLB, Btn(raw, P, "LeftShoulder"));
            Set(TrRPadTouch, pads.rTouch);
            Set(TrRPadClick, pads.rClick);
            Set(TrRTrigClick, rt > 0);
            Set(TrLPadTouch, pads.lTouch);
            Set(TrLPadClick, pads.lClick);
            Set(TrLTrigClick, lt > 0);
            WriteU32(dest, 2, bits);

            WriteI16(dest, 6, (short)lt);
            WriteI16(dest, 8, (short)rt);
            WriteI16(dest, 10, lx);
            WriteI16(dest, 12, NegateSat(ly));
            WriteI16(dest, 14, rx);
            WriteI16(dest, 16, NegateSat(ry));

            WriteI16(dest, 18, pads.lTouch ? NormToPad(tp.X0) : (short)0);
            WriteI16(dest, 20, pads.lTouch ? NormToPadY(tp.Y0) : (short)0);  // SDL decodes both pads as y = -wire/65536 + 0.5 (_steam_triton.c 252 / 264)
            WriteU16(dest, 22, pads.lClick ? ushort.MaxValue : (ushort)0);
            WriteI16(dest, 24, pads.rTouch ? NormToPad(tp.X1) : (short)0);
            WriteI16(dest, 26, pads.rTouch ? NormToPadY(tp.Y1) : (short)0);
            WriteU16(dest, 28, pads.rClick ? ushort.MaxValue : (ushort)0);

            WriteU32(dest, 30, packetNum);
            WriteI16(dest, 34, ScaleAccel(motion.AccelX));
            WriteI16(dest, 36, ScaleAccel(-motion.AccelZ));
            WriteI16(dest, 38, ScaleAccel(motion.AccelY));
            WriteI16(dest, 40, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 42, ScaleGyro(-motion.GyroRoll));
            WriteI16(dest, 44, ScaleGyro(motion.GyroYaw));
            // Quaternion 46..52 stays zero.
        }
    }
}
