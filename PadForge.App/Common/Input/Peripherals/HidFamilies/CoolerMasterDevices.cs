using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's MP750 mousepads, ARGB monitors, GD160 desk, Radeon RX
    /// 6000 graphics cards and RGB controller (#557).
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        // ─────────────────────────────────────────────
        //  MP750 (CMMP750Controller)
        // ─────────────────────────────────────────────

        /// <summary>colour_mode_data, the first two bytes and defaults of each
        /// mode's frame (CMMP750Controller.cpp:15-23).</summary>
        internal static readonly byte[][] Mp750ModeData =
        {
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            new byte[] { 0x01, 0x04, 0xFF, 0x00, 0xFF, 0x00 },
            new byte[] { 0x02, 0x04, 0xFF, 0x00, 0xFF, 0x80 },
            new byte[] { 0x03, 0x04, 0xFF, 0x00, 0xFF, 0x80 },
            new byte[] { 0x04, 0x04, 0x80, 0x00, 0x00, 0x00 },
            new byte[] { 0x05, 0x04, 0x80, 0x00, 0x00, 0x00 },
        };

        /// <summary>speed_mode_data (CMMP750Controller.cpp:25-28).</summary>
        internal static readonly byte[] Mp750Speeds = { 0xFF, 0xE0, 0xC0, 0xA0, 0x80, 0x60, 0x40, 0x20, 0x00 };

        internal const byte Mp750Static = 0x01;
        internal const byte Mp750Breathing = 0x03;

        /// <summary>What GetStatus read (CMMP750Controller.cpp:54-84): the
        /// mode, the color and the speed's place in
        /// <see cref="Mp750Speeds"/>.</summary>
        internal readonly record struct Mp750State(byte Mode, int Color, int Speed);

        /// <summary>
        /// An MP750 mousepad: one light (RGBController_CMMP750Controller.cpp
        /// SetupZones).
        ///
        /// <para>The pad has no Direct mode. OpenRGB lights it through its
        /// Static mode, one frame per color (SetColor and SendUpdate,
        /// CMMP750Controller.cpp:142-176), and SignalRGB sends the same frame,
        /// then 00 06, at every render (signal-plugins
        /// CoolerMaster_MP750L_Mat.js:44-80). No source explains that second
        /// report, and OpenRGB lights the pad without it. OpenRGB's page tag
        /// says the controller saves on every update (@save :robot:,
        /// RGBController_CMMP750Controller.cpp:18, the meaning from its commit
        /// 8f876423). The Static mode carries no save flag (line 40), and
        /// SignalRGB sends the frame on every render, so pictures go out at the
        /// rate of a stream. The ARGB controller holds its addressable headers'
        /// mode frames, which no reference streams, by the rule in
        /// CoolerMasterArgb.cs. No source says whether the frame reaches the
        /// pad's flash. A session reads the state first, 07, whose
        /// answer is 80 05, the mode, the color and the speed (GetStatus,
        /// lines 54-84), and hands back by sending that state as SendUpdate
        /// would. An answer that is not one leaves nothing to hand back, so
        /// the session does not start.</para>
        /// </summary>
        private static HidLightingModel Mp750Model(string name, ushort productId)
            => NewModel(name, productId, new[] { ZoneLight(0, 1) }, "OpenRGB RGBController_CMMP750Controller.cpp",
                (io, wait) => BeginMp750(io));

        /// <summary>SendUpdate's frame (CMMP750Controller.cpp:151-176): the
        /// mode's row, then for a color mode the color and the speed, and
        /// for the others the speed at byte 3.</summary>
        internal static byte[] Mp750Frame(Mp750State state)
        {
            var frame = new byte[PacketLength];
            int mode = Math.Min((int)state.Mode, Mp750ModeData.Length - 1);
            Mp750ModeData[mode].CopyTo(frame, 1);
            byte speed = Mp750Speeds[Math.Clamp(state.Speed, 0, Mp750Speeds.Length - 1)];
            if (state.Mode > Mp750Breathing)
            {
                frame[3] = speed;
            }
            else
            {
                frame[3] = R(state.Color);
                frame[4] = G(state.Color);
                frame[5] = B(state.Color);
                frame[6] = speed;
            }
            return frame;
        }

        /// <summary>The state in an answer to 07, or null for an answer that
        /// does not start 80 05 (CMMP750Controller.cpp:62-73). The speed is
        /// the last place in the table whose byte is at least the one read,
        /// as OpenRGB's loop finds it.</summary>
        internal static Mp750State? Mp750StateOf(byte[] answer)
        {
            if (answer == null || answer.Length < 7 || answer[0] != 0x80 || answer[1] != 0x05) return null;
            int speed = 0;
            for (int i = 0; i < Mp750Speeds.Length && Mp750Speeds[i] >= answer[6]; i++) speed = i;
            return new Mp750State(answer[2], answer[3] << 16 | answer[4] << 8 | answer[5], speed);
        }

        private static HidLightingSession BeginMp750(IHidReportChannel io)
        {
            var frame = new byte[PacketLength];
            frame[1] = 0x07;
            if (Mp750StateOf(Exchange(io, frame, required: true)) is not { } state) return null;
            return new Mp750Session(state);
        }

        internal sealed class Mp750Session : Session
        {
            public Mp750Session(Mp750State state)
            {
                State = state;
                SetHandBack(new[] { Mp750Frame(state) });
            }

            public Mp750State State { get; }

            /// <summary>The frame of a picture: the Static mode with its
            /// color, at the speed read where the pad was in the Static mode,
            /// as SetColor keeps it, and else at the Static mode's own speed,
            /// 0, as choosing that mode sets it (SetMode, lines 134-140).</summary>
            internal byte[] PictureFrame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => Mp750Frame(new Mp750State(Mp750Static, ByAddress(lights, colors, 1)[0],
                    State.Mode == Mp750Static ? State.Speed : 0));

            protected override bool SendHandBack(IHidReportChannel io) => WriteAll(io, HandBackFrames);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                return io != null && io.WriteOutput(PictureFrame(lights, colors))
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }
        }

        // ─────────────────────────────────────────────
        //  GM27-FQS and GM34-CWQ monitors (CMMonitorController)
        // ─────────────────────────────────────────────

        /// <summary>The monitor's LEDs (RGBController_CMMonitorController.cpp
        /// SetupZones, lines 150-169).</summary>
        internal const int MonitorLeds = 47;

        /// <summary>CM_MONITOR_COLOR_DATA_LENGTH (CMMonitorController.h).</summary>
        internal const int MonitorColorData = 436;

        /// <summary>
        /// A GM27-FQS or GM34-CWQ monitor: 47 LEDs on its back, edge lights
        /// here.
        ///
        /// <para>Each picture is OpenRGB's Direct mode: software mode on once,
        /// 80 07 02 02 00 01, then seven frames of colors, none read
        /// (SendDirect and SetSoftwareModeEnabled,
        /// CMMonitorController.cpp:137-208). Hand-back is software mode off,
        /// 80 07 02 02 00 00, which OpenRGB sends ahead of any stored mode
        /// (SetMode, lines 53-58), so the monitor shows its own mode
        /// again.</para>
        /// </summary>
        private static HidLightingModel MonitorModel(string name, ushort productId)
        {
            var lights = new LightInfo[MonitorLeds];
            for (int i = 0; i < lights.Length; i++) lights[i] = EdgeLight(i);
            return NewModel(name, productId, lights, "OpenRGB RGBController_CMMonitorController.cpp",
                (io, wait) => new SoftwareModeSession(desk: false));
        }

        /// <summary>SetSoftwareModeEnabled (CMMonitorController.cpp:194-208).</summary>
        internal static byte[] MonitorSoftwareMode(bool on)
            => Packet(stackalloc byte[] { 0x80, 0x07, 0x02, 0x02, 0x00, on ? (byte)0x01 : (byte)0x00 });

        /// <summary>The seven frames of SendDirect (CMMonitorController.cpp:137-191):
        /// the frame's number, 0x86 for the last, the first with 07 02 02 01
        /// 80 and 58 bytes of colors, the rest with 63 bytes each.</summary>
        internal static List<byte[]> SevenFrames(IReadOnlyList<int> colors, int count, ReadOnlySpan<byte> header)
        {
            var data = new byte[MonitorColorData];
            for (int i = 0, at = 0; i < count && at + 2 < data.Length; i++)
            {
                data[at++] = R(colors[i]);
                data[at++] = G(colors[i]);
                data[at++] = B(colors[i]);
            }
            var frames = new List<byte[]>(7);
            int offset = 0;
            for (int i = 0; i < 7; i++)
            {
                var frame = new byte[PacketLength];
                frame[1] = i < 6 ? (byte)i : (byte)0x86;
                if (i == 0)
                {
                    header.CopyTo(frame.AsSpan(2));
                    data.AsSpan(offset, PacketLength - 7).CopyTo(frame.AsSpan(7));
                    offset += PacketLength - 7;
                }
                else
                {
                    data.AsSpan(offset, PacketLength - 2).CopyTo(frame.AsSpan(2));
                    offset += PacketLength - 2;
                }
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>A session for a device that takes colors in a software
        /// mode it is put into once and handed back by leaving it: the
        /// monitors and the desk.</summary>
        internal sealed class SoftwareModeSession : Session
        {
            private readonly bool _desk;
            private bool _on;

            public SoftwareModeSession(bool desk)
            {
                _desk = desk;
                SetHandBack(desk
                    ? new[] { Gd160ControlMode(1, false), Gd160ControlMode(2, false) }
                    : new[] { MonitorSoftwareMode(false) });
            }

            /// <summary>The frames that put the device into software
            /// mode.</summary>
            internal byte[][] OnFrames => _desk
                ? new[] { Gd160ControlMode(1, true), Gd160ControlMode(2, true) }
                : new[] { MonitorSoftwareMode(true) };

            internal List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, bool enter)
            {
                var frames = new List<byte[]>();
                if (enter) frames.AddRange(OnFrames);
                if (!_desk)
                {
                    var map = ByAddress(lights, colors, MonitorLeds);
                    frames.AddRange(SevenFrames(map, MonitorLeds, stackalloc byte[] { 0x07, 0x02, 0x02, 0x01, 0x80 }));
                }
                else
                {
                    frames.AddRange(Gd160ColorFrames(ByAddress(lights, colors, Gd160Leds * 2)));
                }
                return frames;
            }

            protected override bool SendHandBack(IHidReportChannel io) => WriteAll(io, HandBackFrames);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                if (!WriteAll(io, PictureFrames(lights, colors, !_on))) return HidLightingPaint.Failed;
                _on = true;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                bool back = base.HandBack(io, wait);
                if (back) _on = false;
                return back;
            }
        }

        // ─────────────────────────────────────────────
        //  GD160 desk (CMGD160Controller)
        // ─────────────────────────────────────────────

        /// <summary>CM_GD160_LEDS_PER_SIDE (CMGD160Controller.h).</summary>
        internal const int Gd160Leds = 96;

        /// <summary>CM_GD160_SIDE_DATA_LENGTH (CMGD160Controller.h).</summary>
        internal const int Gd160SideData = 436;

        /// <summary>
        /// A GD160 ARGB gaming desk: 96 LEDs at the front and 96 at the back
        /// (RGBController_CMGD160Controller.cpp:150-180), edge lights
        /// here.
        ///
        /// <para>Each picture is OpenRGB's Direct mode: software mode on once
        /// for each side, then seven frames of colors for each side, none
        /// read (SendColorData(colors, 0x07, 0x01, 0xFF, true) at
        /// RGBController_CMGD160Controller.cpp:190, CMGD160Controller.cpp:103-190).
        /// OpenRGB also resets the desk when it finds it (ResetDevice, lines
        /// 192-216), which PadForge does not send: a reset is no part of
        /// showing a picture, and the hand-back has to leave the desk's own
        /// lighting as it was. Hand-back is software mode off for each side,
        /// which OpenRGB sends ahead of any stored mode (SetMode, lines
        /// 60-66).</para>
        /// </summary>
        private static HidLightingModel Gd160Model(string name, ushort productId)
        {
            var lights = new LightInfo[Gd160Leds * 2];
            for (int i = 0; i < lights.Length; i++) lights[i] = EdgeLight(i);
            return NewModel(name, productId, lights, "OpenRGB RGBController_CMGD160Controller.cpp",
                (io, wait) => new SoftwareModeSession(desk: true));
        }

        /// <summary>SetControlMode for one side (CMGD160Controller.cpp:167-190).</summary>
        internal static byte[] Gd160ControlMode(byte side, bool software)
            => Packet(stackalloc byte[] { 0x80, 0x07, 0x02, side, 0x00, software ? (byte)0x01 : (byte)0x00 });

        /// <summary>SendColorData's fourteen frames, seven for the front and
        /// seven for the back (CMGD160Controller.cpp:103-165).</summary>
        internal static List<byte[]> Gd160ColorFrames(int[] colors)
        {
            var data = new byte[Gd160SideData * 2];
            for (int i = 0; i < colors.Length && i < Gd160Leds * 2; i++)
            {
                int at = i / Gd160Leds * Gd160SideData + i % Gd160Leds * 3;
                data[at] = R(colors[i]);
                data[at + 1] = G(colors[i]);
                data[at + 2] = B(colors[i]);
            }
            var frames = new List<byte[]>(14);
            for (int side = 1; side <= 2; side++)
            {
                int offset = (side - 1) * Gd160SideData;
                for (int packet = 0; packet < 7; packet++)
                {
                    var frame = new byte[PacketLength];
                    frame[1] = packet < 6 ? (byte)packet : (byte)0x86;
                    if (packet == 0)
                    {
                        frame[2] = 0x07;
                        frame[3] = 0x02;
                        frame[4] = (byte)side;
                        frame[5] = 0x01;
                        frame[6] = 0xFF;
                        data.AsSpan(offset, PacketLength - 7).CopyTo(frame.AsSpan(7));
                        offset += PacketLength - 7;
                    }
                    else
                    {
                        data.AsSpan(offset, PacketLength - 2).CopyTo(frame.AsSpan(2));
                        offset += PacketLength - 2;
                    }
                    frames.Add(frame);
                }
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  Radeon RX 6000 and 6900 (CMR6000Controller)
        // ─────────────────────────────────────────────

        /// <summary>COOLERMASTER_RADEON_6900_PID (CMR6000Controller.h:20).</summary>
        internal const ushort Radeon6900 = 0x015B;

        /// <summary>
        /// A Cooler Master Radeon RX 6000 or 6900 card: one light, its logo
        /// (RGBController_CMR6000Controller.cpp SetupZones).
        ///
        /// <para>Each picture is OpenRGB's Direct mode, mode 0 with the color
        /// at full brightness: 41 80, the 6900's second color, the mode
        /// frame, the color configuration and the apply (SendUpdate,
        /// CMR6000Controller.cpp:102-214, with the values
        /// RGBController_CMR6000Controller.cpp:184-208 passes). None is the
        /// save command.</para>
        ///
        /// <para>Hand-back: none. OpenRGB never reads the card's mode and its
        /// Shutdown sends nothing, and no other reference drives the card,
        /// so the card shows the last color PadForge sent until MasterPlus
        /// sets it or the PC restarts.</para>
        /// </summary>
        private static HidLightingModel GpuModel(string name, ushort productId)
            => NewModel(name, productId, new[] { LogoLight(0) }, "OpenRGB RGBController_CMR6000Controller.cpp",
                (io, wait) => new GpuSession(productId == Radeon6900));

        /// <summary>The frames of SendUpdate for OpenRGB's Direct mode: mode
        /// 0, speed 0xFF (Direct.speed), random 0, brightness 0xFF, the color
        /// and black second (CMR6000Controller.cpp:102-214). Each is
        /// followed by a read except the mode frame.</summary>
        internal static List<(byte[] Frame, bool Read)> GpuFrames(int color, bool r6900)
        {
            var frames = new List<(byte[], bool)> { (Packet(stackalloc byte[] { 0x41, 0x80 }), true) };
            if (r6900)
                frames.Add((Packet(stackalloc byte[] { 0x51, 0x9C, 0x01, 0x00, R(color), G(color), B(color), 0, 0, 0 }), true));
            var mode = new byte[PacketLength];
            mode.AsSpan().Fill(0xFF);
            mode[0x00] = 0x00;
            mode[0x01] = 0x51;
            mode[0x02] = 0x2C;
            mode[0x03] = 0x01;
            mode[0x04] = 0x00;
            mode[0x05] = 0x00;
            mode[0x06] = 0xFF;
            mode[0x07] = 0x00;
            mode[0x08] = 0xFF;
            mode[0x0A] = 0xFF;
            mode[0x0B] = R(color);
            mode[0x0C] = G(color);
            mode[0x0D] = B(color);
            mode[0x0E] = 0x00;
            mode[0x0F] = 0x00;
            mode[0x10] = 0x00;
            frames.Add((mode, false));
            var config = Packet(stackalloc byte[] { 0x51, 0xA0, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x05, 0x06 });
            config.AsSpan(0x0B, 0x1A - 0x0B).Fill(0x00);
            frames.Add((config, true));
            frames.Add((Packet(stackalloc byte[] { 0x51, 0x28, 0x00, 0x00, 0xE0 }), true));
            return frames;
        }

        internal sealed class GpuSession : Session
        {
            private readonly bool _r6900;

            public GpuSession(bool r6900) => _r6900 = r6900;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var (frame, read) in GpuFrames(ByAddress(lights, colors, 1)[0], _r6900))
                {
                    if (read ? Exchange(io, frame, required: false) == null : io == null || !io.WriteOutput(frame))
                        return HidLightingPaint.Failed;
                }
                return HidLightingPaint.Sent;
            }
        }

        // ─────────────────────────────────────────────
        //  RGB controller (CMRGBController)
        // ─────────────────────────────────────────────

        internal const byte RgbcBreathing = 0x01;
        internal const byte RgbcColorCycle = 0x02;
        internal const byte RgbcStar = 0x03;
        internal const byte RgbcMultiple = 0x04;
        internal const byte RgbcOff = 0xFE;

        /// <summary>The RGB controller's held picture time and repaint
        /// interval. Every picture ends with 50 55, which the keyboards and
        /// mice use as their save command, so each is taken to land in the
        /// controller's memory, and a picture goes out once it has held for
        /// 150 ms and no sooner than 500 ms after the one before, the bounds
        /// the Lenovo Legion family keeps for a stored picture. The bounds
        /// are PadForge's.</summary>
        internal const int RgbcHoldMs = 150;
        internal const int RgbcRepaintMs = 500;

        /// <summary>What a session read of the controller: the active mode,
        /// that mode's configuration and the four custom colors.</summary>
        internal readonly record struct RgbcState(byte Mode, byte Speed, byte Brightness, int Color1, int Color2,
            int[] Ports);

        /// <summary>
        /// The Cooler Master RGB controller: four 12 V RGB ports, one light
        /// each (RGBController_CMRGBController.cpp:222-245), zones 1 to 4.
        ///
        /// <para>Each picture is OpenRGB's Custom mode, the controller's
        /// per-port colors: SetLedsDirect (CMRGBController.cpp:275-285), which
        /// ends with 50 55. That is the save command of Cooler Master's
        /// keyboards (CMKeyboardAbstractController.cpp:174-177) and mice
        /// (CMMMController.cpp:340-346), so the colors are taken to be
        /// stored, and pictures are held and spaced
        /// (<see cref="RgbcHoldMs"/>).</para>
        ///
        /// <para>A session reads what it will put back first, as OpenRGB's
        /// controller reads it (ReadCurrentMode and ReadModeConfig, lines
        /// 243-260): the mode, that mode's configuration, and the custom
        /// colors, which OpenRGB reads only for the custom mode and PadForge
        /// reads always, since its pictures write them. A read that is not
        /// answered, or does not carry its request back, leaves nothing to
        /// put back, and the session does not start. Hand-back: the custom
        /// colors as read, through SetLedsDirect, then, for any other mode,
        /// that mode with its configuration through SetMode (lines
        /// 262-273).</para>
        /// </summary>
        private static HidLightingModel RgbControllerModel(string name, ushort productId)
            => NewModel(name, productId, new[] { ZoneLight(0, 1), ZoneLight(1, 2), ZoneLight(2, 3), ZoneLight(3, 4) },
                "OpenRGB RGBController_CMRGBController.cpp", (io, wait) => BeginRgbc(io),
                RgbcRepaintMs, RgbcHoldMs);

        internal static byte[] RgbcFlow(byte flag) => Packet(stackalloc byte[] { 0x41, flag });
        internal static byte[] RgbcApply() => Packet(stackalloc byte[] { 0x50, 0x55 });
        internal static byte[] RgbcSetMode(byte mode) => Packet(stackalloc byte[] { 0x51, 0x28, 0x00, 0x00, mode });
        internal static byte[] RgbcCustomStart() => Packet(stackalloc byte[] { 0x51, 0x30 });

        /// <summary>SendSetCustomColors (CMRGBController.cpp:83-114): the four
        /// colors from byte 5.</summary>
        internal static byte[] RgbcCustomColors(int c1, int c2, int c3, int c4)
        {
            var frame = Packet(stackalloc byte[] { 0x51, 0xA8 });
            Put(frame, 5, c1);
            Put(frame, 8, c2);
            Put(frame, 11, c3);
            Put(frame, 14, c4);
            return frame;
        }

        /// <summary>SendSetConfig, the full form, not multilayer
        /// (CMRGBController.cpp:145-203).</summary>
        internal static byte[] RgbcConfig(byte mode, byte speed, byte brightness, int color1, int color2)
        {
            if (mode == RgbcColorCycle)
            {
                brightness = 0xDF;
                color1 = 0xFFFFFF;
                color2 = 0x000000;
            }
            else if (mode == RgbcOff)
            {
                brightness = 0x03;
            }
            var frame = new byte[PacketLength];
            frame[1] = 0x51;
            frame[2] = 0x2C;
            frame[3] = 0x00;
            frame[5] = mode;
            frame[6] = speed;
            frame[7] = mode == RgbcBreathing ? (byte)0x20 : (byte)0x00;
            frame[8] = mode == RgbcStar ? (byte)0x19 : (byte)0xFF;
            frame[9] = 0xFF;
            frame[10] = brightness;
            Put(frame, 11, color1);
            Put(frame, 14, color2);
            frame.AsSpan(17).Fill(0xFF);
            return frame;
        }

        /// <summary>SetLedsDirect's frames (CMRGBController.cpp:275-285).</summary>
        internal static byte[][] RgbcDirect(int c1, int c2, int c3, int c4) => new[]
        {
            RgbcFlow(0x80), RgbcCustomStart(), RgbcCustomColors(c1, c2, c3, c4), RgbcSetMode(RgbcMultiple),
            RgbcCustomStart(), RgbcConfig(RgbcMultiple, 0x00, 0xFF, c1, 0), RgbcApply(), RgbcFlow(0x00),
        };

        /// <summary>SetMode's frames (CMRGBController.cpp:262-273).</summary>
        internal static byte[][] RgbcMode(byte mode, byte speed, byte brightness, int color1, int color2) => new[]
        {
            RgbcFlow(0x01), RgbcConfig(mode, speed, brightness, color1, color2), RgbcSetMode(mode), RgbcApply(),
            RgbcFlow(0x00),
        };

        /// <summary>The frames that put <paramref name="state"/> back.</summary>
        internal static byte[][] RgbcHandBack(RgbcState state)
        {
            var frames = new List<byte[]>(RgbcDirect(state.Ports[0], state.Ports[1], state.Ports[2], state.Ports[3]));
            if (state.Mode != RgbcMultiple)
                frames.AddRange(RgbcMode(state.Mode, state.Speed, state.Brightness, state.Color1, state.Color2));
            return frames.ToArray();
        }

        private static int ColorAt(byte[] answer, int at) => answer[at] << 16 | answer[at + 1] << 8 | answer[at + 2];

        /// <summary>The reads of ReadCurrentMode and ReadModeConfig
        /// (CMRGBController.cpp:56-68, 116-143, 205-229 and 243-260), each
        /// answer checked for its request's two bytes. Null when one is
        /// missing.</summary>
        internal static RgbcState? ReadRgbc(IHidReportChannel io)
        {
            if (Exchange(io, RgbcFlow(0x01), required: false) == null) return null;
            var modeAnswer = Exchange(io, Packet(stackalloc byte[] { 0x52, 0x28 }), required: false);
            if (!Echoes(modeAnswer, 0x28, 5)) return null;
            byte mode = modeAnswer[4];
            if (Exchange(io, RgbcFlow(0x00), required: false) == null) return null;
            var config = Exchange(io, Packet(stackalloc byte[] { 0x52, 0x2C, 0x00, 0x00, mode }), required: false);
            if (!Echoes(config, 0x2C, 16)) return null;
            var custom = Exchange(io, Packet(stackalloc byte[] { 0x52, 0xA8 }), required: false);
            if (!Echoes(custom, 0xA8, 16)) return null;
            return new RgbcState(mode, config[5], config[9], ColorAt(config, 10), ColorAt(config, 13),
                new[] { ColorAt(custom, 4), ColorAt(custom, 7), ColorAt(custom, 10), ColorAt(custom, 13) });
        }

        /// <summary>Whether an answer is long enough and carries its request's
        /// type at byte 1, the byte OpenRGB's own read of the custom colors
        /// waits for (CMMMController.cpp:94-97).</summary>
        private static bool Echoes(byte[] answer, byte type, int length)
            => answer != null && answer.Length >= length && answer[1] == type;

        private static HidLightingSession BeginRgbc(IHidReportChannel io)
            => ReadRgbc(io) is { } state ? new RgbcSession(state) : null;

        internal sealed class RgbcSession : Session
        {
            public RgbcSession(RgbcState state)
            {
                State = state;
                SetHandBack(RgbcHandBack(state));
            }

            public RgbcState State { get; }

            internal static byte[][] PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var map = ByAddress(lights, colors, 4);
                return RgbcDirect(map[0], map[1], map[2], map[3]);
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
    }
}
