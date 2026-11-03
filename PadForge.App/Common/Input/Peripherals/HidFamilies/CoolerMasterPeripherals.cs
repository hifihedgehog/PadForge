using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's mice, mousepads, monitors, gaming desk, graphics cards
    /// and RGB controller (#557), each as OpenRGB's controller for it lights
    /// it. What each write does to the device's stored state, and how each
    /// goes back to its own lighting, is on each kind below.
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        internal static LightInfo ZoneLight(int address, int zone)
            => new(address, LightKind.Zone, 0, LightKeySource.None) { Zone = zone };

        internal static LightInfo LogoLight(int address) => new(address, LightKind.Logo, 0, LightKeySource.None);

        internal static LightInfo EdgeLight(int address) => new(address, LightKind.Edge, 0, LightKeySource.None);

        // ─────────────────────────────────────────────
        //  MM530, MM720, MM730 (CMMMController)
        // ─────────────────────────────────────────────

        /// <summary>CM_MM530_PID (CMMMController.h:28).</summary>
        internal const ushort Mm530 = 0x0065;

        /// <summary>CM_MM5XX_COMMAND and CM_MM7XX_COMMAND (CMMMController.h:53-54).</summary>
        internal const byte Mm5xxCommand = 0x2C;
        internal const byte Mm7xxCommand = 0x2B;

        /// <summary>CM_MM_MODE_CUSTOM and CM_MM_MODE_MULTILAYER
        /// (CMMMController.h:65-66).</summary>
        internal const byte MmCustom = 0xB0;
        internal const byte MmMultilayer = 0xE0;

        /// <summary>
        /// An MM530, MM720 or MM730: the wheel, the MM530's buttons and the
        /// logo (Init_Controller, RGBController_CMMMController.cpp:176-209).
        ///
        /// <para>A session starts as OpenRGB's controller does: 41 80, then
        /// reads of the color, the custom colors and the mode
        /// (CMMMController.cpp:17-67). The first picture selects the custom
        /// mode as OpenRGB's Direct mode does, with SendUpdate's frames for
        /// it (51 30, the mode's parameters, 51 28 00 00 B0, lines 217-267),
        /// and each picture is one 51 A8 frame (SetLedsDirect, lines
        /// 199-215). The save command (50 55, SendSavePacket, lines 340-346)
        /// is never sent, so nothing is stored.</para>
        ///
        /// <para>Hand-back: the custom colors as they were read, then the
        /// mode that was active, by SendUpdate's frames without the
        /// parameters, which are left as they were: 51 30 and 51 28 for the
        /// custom and multilayer modes, 41 80 and 51 28 for the others, as
        /// OpenRGB sends for its Off mode (lines 219-227).</para>
        /// </summary>
        private static HidLightingModel MouseModel(string name, ushort productId)
        {
            bool mm530 = productId == Mm530;
            var lights = mm530
                ? new[] { ZoneLight(0, 1), ZoneLight(1, 2), LogoLight(2) }
                : new[] { ZoneLight(0, 1), LogoLight(1) };
            return NewModel(name, productId, lights, "OpenRGB RGBController_CMMMController.cpp",
                (io, wait) => BeginMouse(io, mm530));
        }

        /// <summary>41 80 (SendInitPacket, CMMMController.cpp:297-303).</summary>
        internal static byte[] MmInit() => Packet(stackalloc byte[] { 0x41, 0x80 });

        /// <summary>51 28 00 00 and the mode (SendApplyPacket, lines 318-326).</summary>
        internal static byte[] MmApply(byte mode) => Packet(stackalloc byte[] { 0x51, 0x28, 0x00, 0x00, mode });

        /// <summary>51 30, with 01 at byte 5 for the multilayer mode
        /// (SendUsingZonesPacket, lines 305-316).</summary>
        internal static byte[] MmUsingZones(byte mode)
            => Packet(stackalloc byte[] { 0x51, 0x30, 0x00, 0x00, mode == MmMultilayer ? (byte)0x01 : (byte)0x00 });

        /// <summary>The custom mode's parameters as SendUpdate sends them for
        /// OpenRGB's Direct mode: speed 0, the mode's own default, byte 7
        /// 0x20, brightness 0xFF, no color, and 0xFF from byte 17 on
        /// (CMMMController.cpp:229-258, RGBController_CMMMController.cpp:39-47
        /// and 246-272).</summary>
        internal static byte[] MmCustomParameters(byte command)
        {
            var frame = Packet(stackalloc byte[] { 0x51, command, 0x00, 0x00, MmCustom, 0x00, 0x20, 0xFF, 0xFF, 0xFF });
            frame.AsSpan(17).Fill(0xFF);
            return frame;
        }

        /// <summary>51 A8 00 00 and the colors at the model's bytes: the MM530
        /// buttons at 5-7 and wheel at 11-13, the others wheel at 5-7 and
        /// buttons, black, at 11-13, every model the logo at 8-10
        /// (CMMMController.cpp:25-61 and 199-212).</summary>
        internal static byte[] MmColors(bool mm530, int wheel, int buttons, int logo)
        {
            var frame = Packet(stackalloc byte[] { 0x51, 0xA8, 0x00, 0x00 });
            Put(frame, mm530 ? 5 : 11, buttons);
            Put(frame, 8, logo);
            Put(frame, mm530 ? 11 : 5, wheel);
            return frame;
        }

        /// <summary>51 A8 00 00 and the nine bytes of colors an answer to
        /// 52 A8 carries at bytes 4-12, sent back as they were read
        /// (GetCustomStatus, CMMMController.cpp:87-105).</summary>
        internal static byte[] RestoreCustomColors(byte[] read)
        {
            var frame = Packet(stackalloc byte[] { 0x51, 0xA8, 0x00, 0x00 });
            read.AsSpan(4, 9).CopyTo(frame.AsSpan(5));
            return frame;
        }

        private static void Put(byte[] frame, int at, int color)
        {
            frame[at] = R(color);
            frame[at + 1] = G(color);
            frame[at + 2] = B(color);
        }

        /// <summary>The mode in an answer to 52 28, byte 4
        /// (GetModeStatus, CMMMController.cpp:107-116), or null for an answer
        /// that does not carry the request's type at byte 1, the byte
        /// GetCustomStatus waits for (lines 94-97).</summary>
        internal static byte? MmModeOf(byte[] answer)
            => answer != null && answer.Length > 4 && answer[1] == 0x28 ? answer[4] : null;

        /// <summary>The answer to 52 A8, read until its second byte is A8 or
        /// a read comes up short, as GetCustomStatus reads it
        /// (CMMMController.cpp:87-105). Null when none came.</summary>
        internal static byte[] ReadCustomStatus(IHidReportChannel io)
        {
            if (!io.WriteOutput(Packet(stackalloc byte[] { 0x52, 0xA8 }))) return null;
            for (int tries = 0; tries < 8; tries++)
            {
                var answer = Read(io, ReadMs);
                if (answer == null || answer.Length < 13) return null;
                if (answer[1] == 0xA8) return answer;
            }
            return null;
        }

        /// <summary>The hand-back of a mouse whose custom colors and mode
        /// were read.</summary>
        internal static byte[][] MmHandBack(byte[] custom, byte mode)
        {
            var frames = new List<byte[]> { RestoreCustomColors(custom) };
            if (mode == MmCustom || mode == MmMultilayer) frames.Add(MmUsingZones(mode));
            else frames.Add(MmInit());
            frames.Add(MmApply(mode));
            return frames.ToArray();
        }

        private static HidLightingSession BeginMouse(IHidReportChannel io, bool mm530)
        {
            byte command = mm530 ? Mm5xxCommand : Mm7xxCommand;
            if (Exchange(io, MmInit(), required: false) == null) return null;
            if (Exchange(io, Packet(stackalloc byte[] { 0x52, command }), required: false) == null) return null;
            var custom = ReadCustomStatus(io);
            if (custom == null) return null;
            if (MmModeOf(Exchange(io, Packet(stackalloc byte[] { 0x52, 0x28 }), required: false)) is not byte mode)
                return null;
            return new MouseSession(mm530, MmHandBack(custom, mode));
        }

        internal sealed class MouseSession : Session
        {
            private readonly bool _mm530;
            private bool _custom;

            public MouseSession(bool mm530, byte[][] handBack)
            {
                _mm530 = mm530;
                SetHandBack(handBack);
            }

            internal List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, bool enter)
            {
                var map = ByAddress(lights, colors, 3);
                var frames = new List<byte[]>();
                if (enter)
                {
                    frames.Add(MmUsingZones(MmCustom));
                    frames.Add(MmCustomParameters(_mm530 ? Mm5xxCommand : Mm7xxCommand));
                    frames.Add(MmApply(MmCustom));
                }
                frames.Add(_mm530 ? MmColors(true, map[0], map[1], map[2]) : MmColors(false, map[0], 0, map[1]));
                return frames;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                if (!ExchangeAll(io, PictureFrames(lights, colors, !_custom), required: false))
                    return HidLightingPaint.Failed;
                _custom = true;
                return HidLightingPaint.Sent;
            }
        }

        // ─────────────────────────────────────────────
        //  MM711 (CMMM711Controller)
        // ─────────────────────────────────────────────

        /// <summary>
        /// An MM711: the wheel and the logo
        /// (RGBController_CMMM711Controller.cpp Init_Controller).
        ///
        /// <para>A session starts as OpenRGB's controller does: 41 80, then
        /// reads of the color, the custom colors and the mode
        /// (CMMM711Controller.cpp:16-37). OpenRGB's Direct mode sends the
        /// colors alone (51 A8, SetLedsDirect, lines 145-160) with the apply
        /// of the custom mode left commented out (line 159), and selecting
        /// Direct sends nothing (DeviceUpdateMode), so its colors show only
        /// on a mouse already in the custom mode. Each picture here goes out
        /// as SignalRGB's MM711 plugin sends it, 41 80, 51 A8 and the apply
        /// of the custom mode, 51 28 00 00 B0
        /// (signal-plugins CoolerMaster_MM711_Mouse.js:139-172), with the read
        /// OpenRGB makes after each. Nothing is saved (50 55, lines
        /// 200-206).</para>
        ///
        /// <para>Hand-back: the custom colors as they were read, then 41 80
        /// and the apply of the mode that was active (SendApplyPacket,
        /// lines 190-198), its parameters left as they were.</para>
        /// </summary>
        private static HidLightingModel Mm711Model(string name, ushort productId)
            => NewModel(name, productId, new[] { ZoneLight(0, 1), LogoLight(1) },
                "OpenRGB RGBController_CMMM711Controller.cpp", (io, wait) => BeginMm711(io));

        /// <summary>51 A8 00 00, the wheel at bytes 5-7 and the logo at 8-10
        /// (SetLedsDirect, CMMM711Controller.cpp:145-157).</summary>
        internal static byte[] Mm711Colors(int wheel, int logo)
        {
            var frame = Packet(stackalloc byte[] { 0x51, 0xA8, 0x00, 0x00 });
            Put(frame, 5, wheel);
            Put(frame, 8, logo);
            return frame;
        }

        private static HidLightingSession BeginMm711(IHidReportChannel io)
        {
            if (Exchange(io, MmInit(), required: false) == null) return null;
            if (Exchange(io, Packet(stackalloc byte[] { 0x52, Mm7xxCommand }), required: false) == null) return null;
            var custom = ReadCustomStatus(io);
            if (custom == null) return null;
            if (MmModeOf(Exchange(io, Packet(stackalloc byte[] { 0x52, 0x28 }), required: false)) is not byte mode)
                return null;
            return new Mm711Session(new[] { RestoreCustomColors(custom), MmInit(), MmApply(mode) });
        }

        internal sealed class Mm711Session : Session
        {
            public Mm711Session(byte[][] handBack) => SetHandBack(handBack);

            internal static List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var map = ByAddress(lights, colors, 2);
                return new List<byte[]> { MmInit(), Mm711Colors(map[0], map[1]), MmApply(MmCustom) };
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                return ExchangeAll(io, PictureFrames(lights, colors), required: false)
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }
        }

        // ─────────────────────────────────────────────
        //  MM712 (CMMM712Controller)
        // ─────────────────────────────────────────────

        /// <summary>
        /// An MM712: one light, the logo (RGBController_CMMM712Controller.cpp
        /// Init_Controller).
        ///
        /// <para>A session starts with the normal state's init, 44 81 02, and
        /// reads the mode, 4C 81 07, whose answer carries it at byte 3
        /// (CMMM712Controller.cpp:72-79 and 153-158, MM712protocol.txt
        /// "Detecting the mode"). Each picture is OpenRGB's Direct mode:
        /// 5A 81 02, the direct state, then 5A 81 03 and the color, neither
        /// read, since in the direct state the mouse answers nothing
        /// (SetLedsDirect, lines 114-130, and MM712protocol.txt "DIRECT
        /// COMMANDS"). The direct state is not stored. Only 54 81 01 saves
        /// (SaveStatus, lines 187-191).</para>
        ///
        /// <para>Hand-back as OpenRGB leaves the direct state for a mode:
        /// 5A 81 01, the init, then the mode that was active, 4C 81 08
        /// (SendUpdate and SetMode, lines 132-178), its parameters left as
        /// they were.</para>
        /// </summary>
        private static HidLightingModel Mm712Model(string name, ushort productId)
            => NewModel(name, productId, new[] { LogoLight(0) }, "OpenRGB RGBController_CMMM712Controller.cpp",
                (io, wait) => BeginMm712(io));

        internal static byte[] Mm712Init() => Packet(stackalloc byte[] { 0x44, 0x81, 0x02 });
        internal static byte[] Mm712Direct(bool on) => Packet(stackalloc byte[] { 0x5A, 0x81, on ? (byte)0x02 : (byte)0x01 });
        internal static byte[] Mm712SetMode(byte mode) => Packet(stackalloc byte[] { 0x4C, 0x81, 0x08, mode });

        internal static byte[] Mm712Color(int color)
            => Packet(stackalloc byte[] { 0x5A, 0x81, 0x03, R(color), G(color), B(color) });

        /// <summary>The mode in an answer to 4C 81 07, byte 3
        /// (CM_MM712_MODE_BYTE - 1, CMMM712Controller.cpp:24 and 76), or null
        /// for an answer that is not one (MM712protocol.txt: 4D 81 07).</summary>
        internal static byte? Mm712ModeOf(byte[] answer)
            => answer != null && answer.Length > 3 && answer[0] == 0x4D && answer[1] == 0x81 && answer[2] == 0x07
                ? answer[3]
                : null;

        private static HidLightingSession BeginMm712(IHidReportChannel io)
        {
            if (Exchange(io, Mm712Init(), required: false) == null) return null;
            if (Mm712ModeOf(Exchange(io, Packet(stackalloc byte[] { 0x4C, 0x81, 0x07 }), required: false)) is not byte mode)
                return null;
            return new Mm712Session(new[] { Mm712Direct(false), Mm712Init(), Mm712SetMode(mode) });
        }

        internal sealed class Mm712Session : Session
        {
            public Mm712Session(byte[][] handBack) => SetHandBack(handBack);

            /// <summary>5A 81 01 has no answer, and the two after it do. The
            /// worker sends them through one channel, so each read is taken
            /// after its own write.</summary>
            protected override bool SendHandBack(IHidReportChannel io)
            {
                var frames = HandBackFrames;
                if (io == null || !io.WriteOutput(frames[0])) return false;
                for (int i = 1; i < frames.Length; i++)
                    if (Exchange(io, frames[i], required: false) == null) return false;
                return true;
            }

            internal static List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => new() { Mm712Direct(true), Mm712Color(ByAddress(lights, colors, 1)[0]) };

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                return WriteAll(io, PictureFrames(lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }
        }
    }
}
