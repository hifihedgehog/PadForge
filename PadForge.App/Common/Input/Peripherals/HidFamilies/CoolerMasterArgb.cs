using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's ARGB controller (0x1011) and Small ARGB controller
    /// (0x1000) (#557), lit as OpenRGB's CMARGBController and
    /// CMSmallARGBController light them.
    ///
    /// <para>Which devices. OpenRGB makes a controller for either only when
    /// the device's USB serial number string names a firmware it knows, which
    /// these devices report there in place of a serial: A202011171238
    /// (0023) or A202105291658 (0028) for the ARGB controller, A202104052336
    /// (0012) for the Small (DetectCoolerMasterARGB and
    /// DetectCoolerMasterSmallARGB, CoolerMasterControllerDetect.cpp:86-111
    /// and 321-346; GetVersion, CMARGBController.cpp:60-80 and
    /// CMSmallARGBController.cpp:90-106; the strings at CMARGBController.h:30-32
    /// and CMSmallARGBController.h:64). The models ask the same of the serial
    /// string before the worker lists a collection
    /// (<see cref="HidLightingModel.Identify"/>).</para>
    ///
    /// <para>The ARGB controller. Four addressable headers and one RGB
    /// header, each lit as one zone. OpenRGB lights an addressable header
    /// light by light only once the user has told it how many LEDs the
    /// header drives (its zones start at 0 LEDs, RGBController_CMARGBController.cpp:416-422),
    /// so each header takes its zone's color through OpenRGB's Static mode,
    /// as a zone mode sends it (DeviceUpdateZoneMode, lines 522-619): 00 80
    /// 01 01, then 00 80 0B 02, the port, the mode 0A, 10 (no random
    /// colors), the speed 0, the brightness FF and the color (SetPortMode at
    /// CMARGBController.cpp:166-243, Static at RGBController_CMARGBController.cpp:288-299).
    /// The RGB header takes its color as OpenRGB's Direct mode sends it to
    /// that header, its Static mode 05 at brightness FF (lines 491-501):
    /// 00 80 01 03, then 00 80 04 05 10 00 FF and the color. Only the headers
    /// whose color changed go out.</para>
    ///
    /// <para>The Small ARGB controller has no Static mode, only effects and,
    /// on firmware 0012, Direct (RGBController_CMSmallARGBController.cpp:84-205).
    /// The session sets Direct as SetMode does (SendUpdate,
    /// CMSmallARGBController.cpp:224-251) and sends each picture as
    /// SetLedsDirect does (lines 173-222), its one color for 48 LEDs, the
    /// most the header takes (CM_SMALL_ARGB_MAX_LEDS,
    /// RGBController_CMSmallARGBController.h:19), so a strip of any length
    /// it drives shows it.</para>
    ///
    /// <para>What the writes do. OpenRGB marks both devices' modes as saved
    /// by the device ("@save :robot:", RGBController_CMARGBController.cpp:18,
    /// RGBController_CMSmallARGBController.cpp:18), so a picture is held
    /// 150 ms and pictures go out no sooner than 500 ms apart, PadForge's
    /// spacing for writes that land in a device's memory
    /// (<see cref="LenovoLegionLighting.HoldMs"/>). A session first reads
    /// what each header runs, as OpenRGB reads it to start in the device's
    /// own mode (GetPortStatus, CMARGBController.cpp:95-149, and GetStatus,
    /// CMSmallARGBController.cpp:44-70), and the hand-back writes that back
    /// with SetPortMode or SetMode, which is also the crash frames. A device
    /// that does not answer the read gets no session, since its own
    /// lighting could not be put back.</para>
    ///
    /// <para>No source names a process of Cooler Master's software for these
    /// controllers beyond MasterPlus, which the family names on every
    /// model.</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting
    {
        /// <summary>The firmware strings OpenRGB lights (CMARGBController.h:31-32,
        /// CMSmallARGBController.h:64).</summary>
        internal const string ArgbFirmware0023 = "A202011171238";
        internal const string ArgbFirmware0028 = "A202105291658";
        internal const string SmallArgbFirmware0012 = "A202104052336";

        /// <summary>The protocol's port IDs, cm_argb_port_index_to_id
        /// (CMARGBController.cpp:18-25, CMARGBController.h:49-56).</summary>
        internal static readonly byte[] ArgbPorts = { 0x01, 0x02, 0x04, 0x08, 0xFE };
        internal const int ArgbRgbPort = 4;

        /// <summary>The modes PadForge sends (CMARGBController.h:58-82,
        /// CMSmallARGBController.h:42-53).</summary>
        internal const byte ArgbStatic = 0x0A;
        internal const byte RgbStatic = 0x05;
        internal const byte ArgbPassthrough = 0xFF;
        internal const byte ArgbDirect = 0xFE;
        internal const byte SmallDirect = 0xFE;
        internal const byte SmallPassthrough = 0xFF;

        /// <summary>CM_ARGB_BRIGHTNESS_MAX and CM_SMALL_ARGB_BRIGHTNESS_MAX.</summary>
        internal const byte ArgbBrightness = 0xFF;

        /// <summary>CM_SMALL_ARGB_MAX_LEDS (RGBController_CMSmallARGBController.h:19).</summary>
        internal const int SmallArgbLeds = 48;

        /// <summary>The length of a status answer the session reads, the
        /// blue at byte 10 (GetPortStatus, CMARGBController.cpp:142-148).</summary>
        internal const int StatusLength = 11;

        internal static bool IsArgbFirmware(IHidReportChannel io)
            => io?.SerialNumber is ArgbFirmware0023 or ArgbFirmware0028;

        internal static bool IsSmallArgbFirmware(IHidReportChannel io) => io?.SerialNumber == SmallArgbFirmware0012;

        /// <summary>One header's mode, as GetPortStatus reads it and
        /// SetPortMode writes it.</summary>
        internal readonly record struct ArgbPortState(byte Mode, byte Speed, byte Brightness, bool Random, int Color);

        // ─────────────────────────────────────────────
        //  The ARGB controller
        // ─────────────────────────────────────────────

        private static HidLightingModel ArgbModel(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[]
            {
                ZoneLight(0, 1), ZoneLight(1, 2), ZoneLight(2, 3), ZoneLight(3, 4), ZoneLight(ArgbRgbPort, 5),
            }, "OpenRGB RGBController_CMARGBController.cpp") { Product = Product(productId) },
            Programs = new[] { VendorPrograms.MasterPlus },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Identify = IsArgbFirmware,
            Begin = (io, wait) => BeginArgb(io),
        };

        /// <summary>GetPortStatus's request (CMARGBController.cpp:107-131):
        /// 80 0B 01 and the port, or 80 0A 01 00 for the RGB header.</summary>
        internal static byte[] ArgbStatusQuery(int port)
            => port == ArgbRgbPort
                ? Packet(stackalloc byte[] { 0x80, 0x0A, 0x01, 0x00 })
                : Packet(stackalloc byte[] { 0x80, 0x0B, 0x01, ArgbPorts[port] });

        /// <summary>GetPortStatus's reading of an answer as hidapi hands it
        /// over, the RGB header's one byte earlier (lines 139-148). Null for
        /// an answer too short to hold it.</summary>
        internal static ArgbPortState? ArgbStatusOf(byte[] answer, int port)
        {
            int at = port == ArgbRgbPort ? 1 : 0;
            if (answer == null || answer.Length < StatusLength - at) return null;
            return new ArgbPortState(answer[4 - at], answer[6 - at], answer[7 - at], answer[5 - at] == 0x00,
                answer[8 - at] << 16 | answer[9 - at] << 8 | answer[10 - at]);
        }

        /// <summary>SetPortMode's reports (CMARGBController.cpp:166-243): for
        /// Direct one report, 80 01 01 02. Otherwise 80 01 and the function, then
        /// the mode report, 80 0B 02 and the port for an addressable header,
        /// 80 04 (80 01 for passthrough) for the RGB header two bytes
        /// earlier.</summary>
        internal static byte[][] ArgbSetPortMode(int port, ArgbPortState state)
        {
            bool argbHeader = ArgbPorts[port] != 0xFE;
            bool passthrough = state.Mode == ArgbPassthrough;
            if (state.Mode == ArgbDirect)
                return new[] { Packet(stackalloc byte[] { 0x80, 0x01, 0x01, 0x02 }) };
            byte function = passthrough ? (argbHeader ? (byte)0x02 : (byte)0x04) : (argbHeader ? (byte)0x01 : (byte)0x03);
            var first = Packet(stackalloc byte[] { 0x80, 0x01, function });
            var second = new byte[PacketLength];
            second[1] = 0x80;
            if (argbHeader)
            {
                second[2] = 0x0B;
                second[3] = 0x02;
                second[4] = ArgbPorts[port];
                second[5] = state.Mode;
                second[6] = state.Random ? (byte)0x00 : (byte)0x10;
                second[7] = state.Speed;
                second[8] = state.Brightness;
                Put(second, 9, state.Color);
            }
            else
            {
                second[2] = passthrough ? (byte)0x01 : (byte)0x04;
                second[3] = state.Mode;
                second[4] = state.Random ? (byte)0x00 : (byte)0x10;
                second[5] = state.Speed;
                second[6] = state.Brightness;
                Put(second, 7, state.Color);
            }
            return new[] { first, second };
        }

        /// <summary>A header's still color: the Static mode at full
        /// brightness, speed 0, no random colors.</summary>
        internal static byte[][] ArgbColor(int port, int color)
            => ArgbSetPortMode(port, new ArgbPortState(port == ArgbRgbPort ? RgbStatic : ArgbStatic, 0, ArgbBrightness,
                false, color & 0xFFFFFF));

        private static HidLightingSession BeginArgb(IHidReportChannel io)
        {
            if (io == null) return null;
            var states = new ArgbPortState[ArgbPorts.Length];
            for (int port = 0; port < states.Length; port++)
            {
                if (ArgbStatusOf(Exchange(io, ArgbStatusQuery(port), required: false), port) is not { } state) return null;
                states[port] = state;
            }
            return new ArgbSession(states);
        }

        internal sealed class ArgbSession : Session
        {
            private int[] _shown;

            public ArgbSession(ArgbPortState[] states)
            {
                States = states;
                var frames = new List<byte[]>();
                for (int port = 0; port < states.Length; port++) frames.AddRange(ArgbSetPortMode(port, states[port]));
                SetHandBack(frames.ToArray());
            }

            /// <summary>What each header ran when the session began.</summary>
            public IReadOnlyList<ArgbPortState> States { get; }

            /// <summary>The hand-back has no reads: OpenRGB reads nothing
            /// after SetPortMode.</summary>
            protected override bool SendHandBack(IHidReportChannel io) => WriteAll(io, HandBackFrames);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                var map = ByAddress(lights, colors, ArgbPorts.Length);
                for (int port = 0; port < map.Length; port++)
                {
                    if (_shown != null && _shown[port] == map[port]) continue;
                    if (!WriteAll(io, ArgbColor(port, map[port]))) return HidLightingPaint.Failed;
                }
                _shown = map;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                _shown = null;
                return base.HandBack(io, wait);
            }
        }

        // ─────────────────────────────────────────────
        //  The Small ARGB controller
        // ─────────────────────────────────────────────

        private static HidLightingModel SmallArgbModel(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(new[] { ZoneLight(0, 1) }, "OpenRGB RGBController_CMSmallARGBController.cpp")
            {
                Product = Product(productId),
            },
            Programs = new[] { VendorPrograms.MasterPlus },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            Identify = IsSmallArgbFirmware,
            Begin = (io, wait) => BeginSmallArgb(io),
        };

        /// <summary>GetStatus's two requests (CMSmallARGBController.cpp:44-61):
        /// 80 01 01 FF 01, then 80 0B 01 01.</summary>
        internal static byte[] SmallStatusOpen() => Packet(stackalloc byte[] { 0x80, 0x01, 0x01, 0xFF, 0x01 });
        internal static byte[] SmallStatusQuery() => Packet(stackalloc byte[] { 0x00, 0x0B, 0x01, 0x01 });

        /// <summary>GetStatus's reading of the second answer (lines 63-69).
        /// Null for one too short to hold it.</summary>
        internal static ArgbPortState? SmallStatusOf(byte[] answer)
            => answer == null || answer.Length < StatusLength
                ? null
                : new ArgbPortState(answer[4], answer[5], answer[7], answer[6] == 0x00,
                    answer[8] << 16 | answer[9] << 8 | answer[10]);

        /// <summary>SendUpdate's two reports (lines 224-249). The second is
        /// the first's buffer written over, as OpenRGB writes it.</summary>
        internal static byte[][] SmallSetMode(ArgbPortState state)
        {
            bool passthrough = state.Mode == SmallPassthrough;
            bool direct = state.Mode == SmallDirect;
            var first = new byte[PacketLength];
            first[1] = 0x80;
            first[2] = direct ? (byte)0x10 : (byte)0x01;
            first[3] = direct ? (byte)0x01 : passthrough ? (byte)0x02 : (byte)0x01;
            first[5] = passthrough ? (byte)0x00 : (byte)0x02;
            var second = (byte[])first.Clone();
            second[2] = 0x0B;
            second[3] = 0x02;
            second[4] = 0x01;
            second[5] = state.Mode;
            second[6] = state.Speed;
            second[7] = state.Random ? (byte)0x00 : (byte)0x10;
            second[8] = state.Brightness;
            Put(second, 9, state.Color);
            return new[] { first, second };
        }

        /// <summary>SetLedsDirect's reports (lines 173-222): the packet number
        /// at byte 1, 80 added to the last's, then 10 02 FF and the LED count
        /// with the colors from byte 6 in the first report, and the colors
        /// from byte 2 in each one after.</summary>
        internal static List<byte[]> SmallDirectFrames(IReadOnlyList<int> leds)
        {
            var colors = new List<byte>(leds.Count * 3);
            foreach (int color in leds)
            {
                colors.Add(R(color));
                colors.Add(G(color));
                colors.Add(B(color));
            }
            var frames = new List<byte[]>();
            var buffer = new byte[PacketLength];
            buffer[2] = 0x10;
            buffer[3] = 0x02;
            buffer[4] = 0xFF;
            buffer[5] = (byte)leds.Count;
            int at = 6, next = 0;
            while (next < colors.Count)
            {
                buffer[1] = (byte)frames.Count;
                while (at < PacketLength && next < colors.Count) buffer[at++] = colors[next++];
                if (next == colors.Count) buffer[1] += 0x80;
                frames.Add(buffer);
                buffer = new byte[PacketLength];
                at = 2;
            }
            return frames;
        }

        private static HidLightingSession BeginSmallArgb(IHidReportChannel io)
        {
            if (io == null || Exchange(io, SmallStatusOpen(), required: false) == null) return null;
            if (SmallStatusOf(Exchange(io, SmallStatusQuery(), required: false)) is not { } state) return null;
            return new SmallArgbSession(state);
        }

        internal sealed class SmallArgbSession : Session
        {
            private bool _direct;

            public SmallArgbSession(ArgbPortState state)
            {
                State = state;
                SetHandBack(SmallSetMode(state));
            }

            /// <summary>What the header ran when the session began.</summary>
            public ArgbPortState State { get; }

            /// <summary>SendUpdate's two reports, with the read OpenRGB makes
            /// after the second (CMSmallARGBController.cpp:249-250).</summary>
            private static bool SendMode(IHidReportChannel io, byte[][] frames)
                => io != null && io.WriteOutput(frames[0]) && Exchange(io, frames[1], required: false) != null;

            protected override bool SendHandBack(IHidReportChannel io) => SendMode(io, HandBackFrames);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                Owe();
                if (!_direct)
                {
                    // OpenRGB's Direct mode: no speed, full brightness, no
                    // color of its own (RGBController_CMSmallARGBController.cpp:88-96
                    // and 370-382).
                    if (!SendMode(io, SmallSetMode(new ArgbPortState(SmallDirect, 0, ArgbBrightness, false, 0))))
                        return HidLightingPaint.Failed;
                    _direct = true;
                }
                int color = ByAddress(lights, colors, 1)[0];
                var leds = new int[SmallArgbLeds];
                Array.Fill(leds, color);
                return WriteAll(io, SmallDirectFrames(leds)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                bool sent = base.HandBack(io, wait);
                if (sent) _direct = false;
                return sent;
            }
        }
    }
}
