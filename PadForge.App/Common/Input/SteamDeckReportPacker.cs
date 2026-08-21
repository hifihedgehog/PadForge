using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Packs an Extended slot's raw surface (RawHidState + TouchpadState +
    /// MotionSnapshot) into the Steam Deck's native Neptune input frame, so
    /// HM's <c>SubmitRawReport</c> can deliver the report Steam expects from
    /// a real Deck controller (#338, asked in discussion #337).
    ///
    /// <para>Same architecture as <see cref="SonyReportPackers"/>, and for
    /// the same reason: the persona's input report carries more than
    /// HMGamepadState models (two trackpads, IMU, paddles), so the frame is
    /// packed app-side and submitted whole. Inert until HIDMaestro ships the
    /// <c>steam-deck-composite</c> persona profile (HM#56); the lookup misses
    /// for every profile id shipping today and the slot rides the ordinary
    /// raw-surface submit.</para>
    ///
    /// <para>Frame layout is dual-cited. Emitting side: HandheldCompanion
    /// <c>Targets/SteamDeckTarget.cs</c> (BuildReport), proven against live
    /// Steam per the reports in discussion #337. Consuming side: SDL
    /// <c>src/joystick/hidapi/steam/controller_structs.h</c>
    /// (SteamDeckStatePacket_t) and <c>SDL_hidapi_steamdeck.c</c> (the
    /// decode, including the button bit constants and the IMU frame map).
    /// Header 01 00 09 40, unPacketNum at 4, buttons at 8..14, pad
    /// coordinates 16..22, accel 24, gyro 30, quaternion 36 (zeroed: the
    /// slot has no fused orientation), raw triggers 44 (u16), sticks 48..54,
    /// pad pressures 56..58.</para>
    ///
    /// <para>One deliberate divergence from HandheldCompanion: unPacketNum
    /// increments. HC leaves bytes 4..7 zero, and SDL's struct comment
    /// licenses consumers to skip processing when the packet number has not
    /// changed, so a constant zero freezes any consumer honoring that
    /// contract. HC also writes stick-click pseudo-pressures at 60..62,
    /// past the struct's end; this packer stops where the struct stops.</para>
    /// </summary>
    internal static class SteamDeckReportPacker
    {
        /// <summary>Total report size. The Deck's vendor interface declares
        /// 64-byte id-less reports (steam-deck.json nativeDescriptor:
        /// usage page 0xFFFF, no Report ID items), so all 64 bytes are data
        /// from SubmitRawReport's point of view.</summary>
        internal const int ReportSize = 64;

        internal delegate void Packer(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest);

        /// <summary>HM profile id to packer. The id is the HM#56 spec's;
        /// adding the original Steam Controller later is one more row plus
        /// its own packer (its dongle frame differs).</summary>
        internal static readonly IReadOnlyDictionary<string, Packer> ByProfileId =
            new Dictionary<string, Packer>(StringComparer.OrdinalIgnoreCase)
            {
                { "steam-deck-composite", PackDeckReport09 },
            };

        internal static Packer ForProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId)) return null;
            ByProfileId.TryGetValue(profileId, out var p);
            return p;
        }

        // The raw surface's index space is the standardized 22-slot automap
        // space (the same one the web layouts and the automap write):
        // A0 B1 X2 Y3 LB4 RB5 Back6 Start7 LS8 RS9 Guide10 QAM11,
        // paddles 12..15 in SDL's R1,L1,R2,L2 order, TouchpadClick16;
        // axes LX0 LY1 LT2 RX3 RY4 RT5; hat = Povs[0].
        private const int BtnA = 0, BtnB = 1, BtnX = 2, BtnY = 3;
        private const int BtnLB = 4, BtnRB = 5, BtnBack = 6, BtnStart = 7;
        private const int BtnLS = 8, BtnRS = 9, BtnGuide = 10, BtnQam = 11;
        private const int BtnPaddleR1 = 12, BtnPaddleL1 = 13, BtnPaddleR2 = 14, BtnPaddleL2 = 15;
        private const int BtnPadClick = 16;

        private static bool Btn(in RawHidState r, int i)
        {
            if (r.Buttons == null) return false;
            int w = i / 32;
            return w < r.Buttons.Length && (r.Buttons[w] & (1u << (i % 32))) != 0;
        }

        private static void PackDeckReport09(
            in RawHidState raw,
            in TouchpadState tp,
            in MotionSnapshot motion,
            uint packetNum,
            Span<byte> dest)
        {
            dest.Slice(0, ReportSize).Clear();

            // ValveInReport header: version 0x0001, type 0x09
            // (ID_CONTROLLER_DECK_STATE), length 64.
            dest[0] = 0x01;
            dest[1] = 0x00;
            dest[2] = 0x09;
            dest[3] = 0x40;
            WriteU32(dest, 4, packetNum);

            short lx = Axis(raw, 0), ly = Axis(raw, 1);
            short rx = Axis(raw, 3), ry = Axis(raw, 4);
            int lt = Math.Clamp((int)Axis(raw, 2), 0, (int)short.MaxValue);
            int rt = Math.Clamp((int)Axis(raw, 5), 0, (int)short.MaxValue);

            // ulButtonsL bits 0..7 (STEAMDECK_LBUTTON: R2 0x01, L2 0x02,
            // R 0x04, L 0x08, Y 0x10, B 0x20, X 0x40, A 0x80). The digital
            // trigger bits follow the analog pull, HC's rule (> 0).
            byte b8 = 0;
            if (rt > 0) b8 |= 0x01;
            if (lt > 0) b8 |= 0x02;
            if (Btn(raw, BtnRB)) b8 |= 0x04;
            if (Btn(raw, BtnLB)) b8 |= 0x08;
            if (Btn(raw, BtnY)) b8 |= 0x10;
            if (Btn(raw, BtnB)) b8 |= 0x20;
            if (Btn(raw, BtnX)) b8 |= 0x40;
            if (Btn(raw, BtnA)) b8 |= 0x80;
            dest[8] = b8;

            // bits 8..15: DPadUp 0x01, Right 0x02, Left 0x04, Down 0x08,
            // Menu(Start) 0x10, Steam(Guide) 0x20, View(Back) 0x40, L5 0x80.
            var pov = DecodePov(raw);
            byte b9 = 0;
            if (pov.up) b9 |= 0x01;
            if (pov.right) b9 |= 0x02;
            if (pov.left) b9 |= 0x04;
            if (pov.down) b9 |= 0x08;
            if (Btn(raw, BtnStart)) b9 |= 0x10;
            if (Btn(raw, BtnGuide)) b9 |= 0x20;
            if (Btn(raw, BtnBack)) b9 |= 0x40;
            if (Btn(raw, BtnPaddleL2)) b9 |= 0x80;   // L5, SDL LEFT_PADDLE2
            dest[9] = b9;

            // bits 16..23: R5 0x01, LeftPadClick 0x02, RightPadClick 0x04,
            // LeftPadTouch 0x08, RightPadTouch 0x10, L3 0x40.
            // The slot models ONE touch surface with two fingers; the web
            // Deck layout feeds its left pad as finger 0 and its right pad
            // as finger 1, so that is the pad split here. The single click
            // slot (button 16, both web pad clicks land there) clicks the
            // touched pad, or the left one when neither reports touch.
            bool leftTouch = tp.Down0, rightTouch = tp.Down1;
            bool click = Btn(raw, BtnPadClick) || tp.Click;
            bool leftClick = click && (leftTouch || !rightTouch);
            bool rightClick = click && rightTouch;
            byte b10 = 0;
            if (Btn(raw, BtnPaddleR2)) b10 |= 0x01;  // R5, SDL RIGHT_PADDLE2
            if (leftClick) b10 |= 0x02;
            if (rightClick) b10 |= 0x04;
            if (leftTouch) b10 |= 0x08;
            if (rightTouch) b10 |= 0x10;
            if (Btn(raw, BtnLS)) b10 |= 0x40;
            dest[10] = b10;

            // bits 24..31: R3 0x04.
            if (Btn(raw, BtnRS)) dest[11] = 0x04;

            // ulButtonsH byte 13: L4 0x02 (SDL LEFT_PADDLE1), R4 0x04
            // (RIGHT_PADDLE1). Stick-touch bits stay 0: capacitive stick
            // touch has no source on this surface.
            byte b13 = 0;
            if (Btn(raw, BtnPaddleL1)) b13 |= 0x02;
            if (Btn(raw, BtnPaddleR1)) b13 |= 0x04;
            dest[13] = b13;

            // byte 14: QAM 0x04 (Special2 in HC's map, slot 11 in ours).
            if (Btn(raw, BtnQam)) dest[14] = 0x04;

            // Pad coordinates, signed i16, +Y up, 0 center. The slot's touch
            // frame is 0..1 top-down, so X spans the full range and Y flips.
            WriteI16(dest, 16, leftTouch ? NormToPad(tp.X0) : (short)0);
            WriteI16(dest, 18, leftTouch ? NormToPadY(tp.Y0) : (short)0);
            WriteI16(dest, 20, rightTouch ? NormToPad(tp.X1) : (short)0);
            WriteI16(dest, 22, rightTouch ? NormToPadY(tp.Y1) : (short)0);

            // IMU. MotionSnapshot is the SDL-native sensor frame in DSU
            // units (accel g, gyro deg/s; DsuMotionServer.cs:14). The Deck
            // frame map is the exact inverse of SDL_hidapi_steamdeck.c's
            // decode (lines 250..257): SDL.x = sGyroX, SDL.y = sGyroZ,
            // SDL.z = -sGyroY, gyro full scale 2000 deg/s over i16, accel
            // full scale 2 g (HC uses 16 LSB per deg/s, a 2 percent larger
            // scale; SDL's constant is the consuming truth, so it wins).
            WriteI16(dest, 24, ScaleAccel(motion.AccelX));
            WriteI16(dest, 26, ScaleAccel(-motion.AccelZ));
            WriteI16(dest, 28, ScaleAccel(motion.AccelY));
            WriteI16(dest, 30, ScaleGyro(motion.GyroPitch));
            WriteI16(dest, 32, ScaleGyro(-motion.GyroRoll));
            WriteI16(dest, 34, ScaleGyro(motion.GyroYaw));
            // Quaternion 36..43 stays zero.

            // Raw triggers (u16, 0..32767 pull range, HC's scale), sticks
            // (+Y up, so the HID top-down Y negates, saturating -32768).
            WriteU16(dest, 44, (ushort)lt);
            WriteU16(dest, 46, (ushort)rt);
            WriteI16(dest, 48, lx);
            WriteI16(dest, 50, NegateSat(ly));
            WriteI16(dest, 52, rx);
            WriteI16(dest, 54, NegateSat(ry));

            // Pad pressures: full scale while clicked, the value HC ships.
            WriteU16(dest, 56, leftClick ? ushort.MaxValue : (ushort)0);
            WriteU16(dest, 58, rightClick ? ushort.MaxValue : (ushort)0);
        }

        private static short Axis(in RawHidState r, int i)
            => r.Axes != null && i < r.Axes.Length ? r.Axes[i] : (short)0;

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
    }
}
