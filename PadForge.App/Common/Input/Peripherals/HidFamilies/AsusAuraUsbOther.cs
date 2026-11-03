using System;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The ASUS Aura devices of OpenRGB's AsusAuraUSBController that are no
    /// keyboard or mouse (#557).
    ///
    /// <para>Mousemats. The ROG Balteus and Balteus Qi: report EE, C0 81 and
    /// fifteen LEDs, each 00 and its color (AsusAuraMousematController.cpp:69-93,
    /// RGBController_AsusAuraMousemat.cpp:190-217).</para>
    ///
    /// <para>Headset stands. The ROG Throne family: two frames, C0 81 00 00
    /// with the first fifteen LEDs and C0 81 01 00 with the last three, the
    /// seventeen underglow LEDs then the logo
    /// (AsusAuraHeadsetStandController.cpp:69-113,
    /// RGBController_AsusAuraHeadsetStand.cpp:120-158).</para>
    ///
    /// <para>Monitors. Feature report 03 of eight bytes, 02 A1 80, then a
    /// register and a value: the PG32UQ and XG32VC first take 20 and 30, each
    /// LED takes its red, blue and green at three registers from 16, or from
    /// 0 on the XG279Q, and A0 01 applies them
    /// (AsusAuraMonitorController.cpp:53-123, RGBController_AsusAuraMonitor.cpp:77-91).</para>
    ///
    /// <para>Mainboards, addressable controllers, the AURA Terminal and the
    /// Strix LC III. The session reads the firmware string (EC 82) and the
    /// config table (EC B0) as the controller does when it is made
    /// (AsusAuraUSBController.cpp:17-25 and 70-146), and its map from
    /// them (AsusAuraMainboardController.cpp:15-58,
    /// AsusAuraAddressableController.cpp:15-24): the mainboard's own LEDs
    /// first, then one light per addressable header. A mainboard then takes
    /// SetGen1 (EC 52 53 00 01, AsusAuraMainboardController.cpp:64-86), and
    /// its own LEDs go into Direct mode (EC 35 with the effect channel and
    /// FF, lines 160-188) and take EC 40 frames of twenty LEDs, the last one
    /// marked with 80 (AsusAuraUSBController.cpp:148-212). OpenRGB sizes an
    /// addressable header only when the user says how many LEDs it has, and
    /// lights none until then (RGBController_AsusAuraUSB.cpp:233-270 and
    /// 359-365). PadForge lights each header as one zone through OpenRGB's
    /// Static effect for that channel, which needs no count, and sends it only
    /// when the header's color changes: EC 3B on an addressable controller
    /// (AsusAuraAddressableController.cpp:60-96), and EC 35 then EC 36 with
    /// the header's bit in the mask on a mainboard
    /// (AsusAuraMainboardController.cpp:111-153 and 190-225). None of these
    /// frames is followed by the commit, EC 3F 55, that saves a mainboard's
    /// effect (lines 227-247).</para>
    ///
    /// <para>Coolers. The Ryuo and Ryujin take their Direct mode (EC 3B 00 22
    /// FF) and then EC 40 80 00 with the LED count and colors, twelve LEDs on
    /// a Ryuo and five on a Ryujin (AsusAuraRyuoAIOController.cpp:15-26,
    /// 71-125). The Strix LC takes EC 3B 00 FF and then EC 40 00 FF 04 with
    /// its four LEDs from byte 5 and 77 10 F3 after them
    /// (AsusROGStrixLCController.cpp:14-23, 67-128).</para>
    ///
    /// <para>The ROG Ally. Feature report 5A of 64 bytes. The controller
    /// sends 5D B9 and 5D with "ASUS Tech.Inc." when it is made, and OpenRGB's
    /// Direct mode sets the brightness to its maximum, 3, with 5A BA C5 C4
    /// (AsusROGAllyController.cpp:16-23, 58-106,
    /// RGBController_AsusROGAlly.cpp:37-45 and 186-193). Its four LEDs, two
    /// per stick, take 5A D1 08 0C and their colors (lines 108-130).</para>
    /// </summary>
    internal sealed partial class AsusAuraUsbLighting
    {
        // ─────────────────────────────────────────────
        //  Mousemats and headset stands
        // ─────────────────────────────────────────────

        /// <summary>The mousemat's report ID (AsusAuraMousematController.cpp:78).</summary>
        internal const byte MousematReport = 0xEE;

        internal const int MousematLeds = 15, HeadsetStandLeds = 18;

        private HidLightingModel Mousemat(string name, int productId)
            => Model(name, productId,
                new LightMap(Edges(MousematLeds), "OpenRGB RGBController_AsusAuraMousemat.cpp") { Product = ProductKey(productId) },
                (io, wait) => io == null ? null : new FramesSession(MousematFrames));

        /// <summary>UpdateLeds of a mousemat (AsusAuraMousematController.cpp:69-93).</summary>
        internal static List<byte[]> MousematFrames(IReadOnlyList<int> colors)
        {
            var frame = Frame(PacketSize, MousematReport, 0xC0, 0x81, 0x00, 0x00);
            for (int i = 0; i < MousematLeds; i++) Put(frame, 6 + i * 4, At(colors, i));
            return new List<byte[]> { frame };
        }

        /// <summary>The stand's seventeen underglow LEDs and its logo
        /// (RGBController_AsusAuraHeadsetStand.cpp:120-158).</summary>
        private HidLightingModel HeadsetStand(string name, int productId)
        {
            var lights = new LightInfo[HeadsetStandLeds];
            for (int i = 0; i < HeadsetStandLeds - 1; i++) lights[i] = Edge(i);
            lights[HeadsetStandLeds - 1] = Logo(HeadsetStandLeds - 1);
            return Model(name, productId,
                new LightMap(lights, "OpenRGB RGBController_AsusAuraHeadsetStand.cpp") { Product = ProductKey(productId) },
                (io, wait) => io == null ? null : new FramesSession(HeadsetStandFrames));
        }

        /// <summary>UpdateLeds of a headset stand (AsusAuraHeadsetStandController.cpp:69-113).</summary>
        internal static List<byte[]> HeadsetStandFrames(IReadOnlyList<int> colors)
        {
            var first = Frame(PacketSize, 0x00, 0xC0, 0x81, 0x00, 0x00);
            for (int i = 0; i < 15; i++) Put(first, 6 + i * 4, At(colors, i));
            var second = Frame(PacketSize, 0x00, 0xC0, 0x81, 0x01, 0x00);
            for (int i = 0; i < 3; i++) Put(second, 6 + i * 4, At(colors, 15 + i));
            return new List<byte[]> { first, second };
        }

        private static int At(IReadOnlyList<int> colors, int index)
            => colors != null && index < colors.Count ? colors[index] : 0;

        /// <summary>A session whose every picture is a list of output frames
        /// and which has nothing to hand back.</summary>
        internal sealed class FramesSession : HidLightingSession
        {
            private readonly Func<IReadOnlyList<int>, List<byte[]>> _frames;

            public FramesSession(Func<IReadOnlyList<int>, List<byte[]>> frames) => _frames = frames;

            public override bool Feature => false;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in _frames(colors))
                    if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Monitors
        // ─────────────────────────────────────────────

        /// <summary>The monitor PIDs that take the two opening registers
        /// (AsusAuraMonitorController.h:19-25).</summary>
        internal const int Pg32uq = 0x19B9, Xg32vc = 0x1968, Xg279q = 0x1919;

        internal const int MonitorLength = 8, MonitorLeds = 3;

        /// <summary>The monitor's three backlight LEDs
        /// (RGBController_AsusAuraMonitor.cpp:53-75), lighting behind the
        /// screen.</summary>
        private HidLightingModel Monitor(string name, int productId)
        {
            var lights = new LightInfo[MonitorLeds];
            for (int i = 0; i < MonitorLeds; i++) lights[i] = Other(i);
            return Model(name, productId,
                new LightMap(lights, "OpenRGB RGBController_AsusAuraMonitor.cpp") { Product = ProductKey(productId) },
                (io, wait) => io == null ? null : new MonitorSession(productId));
        }

        private static byte[] MonitorFrame(byte register, byte value)
            => Frame(MonitorLength, 0x03, 0x02, 0xA1, 0x80, register, value);

        /// <summary>DeviceUpdateLEDs' feature reports
        /// (RGBController_AsusAuraMonitor.cpp:77-91).</summary>
        internal static List<byte[]> MonitorFrames(int productId, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            if (productId == Pg32uq || productId == Xg32vc)
            {
                frames.Add(MonitorFrame(0x20, 0x00));
                frames.Add(MonitorFrame(0x30, 0x00));
            }
            int offset = productId == Xg279q ? 0 : 16;
            for (int led = 0; led < MonitorLeds; led++)
            {
                int rgb = At(colors, led);
                frames.Add(MonitorFrame((byte)(offset + led * 3), (byte)(rgb >> 16)));
                frames.Add(MonitorFrame((byte)(offset + led * 3 + 1), (byte)rgb));
                frames.Add(MonitorFrame((byte)(offset + led * 3 + 2), (byte)(rgb >> 8)));
            }
            frames.Add(MonitorFrame(0xA0, 0x01));
            return frames;
        }

        internal sealed class MonitorSession : HidLightingSession
        {
            private readonly int _productId;

            public MonitorSession(int productId) => _productId = productId;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in MonitorFrames(_productId, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Mainboards, addressable controllers, the Terminal
        // ─────────────────────────────────────────────

        /// <summary>The report ID and the commands of the Aura USB
        /// controllers (AsusAuraUSBController.h:44-55,
        /// AsusAuraAddressableController.h:19-22, AsusAuraMainboardController.h:19-24).</summary>
        internal const byte AuraReport = 0xEC, AuraFirmware = 0x82, AuraConfig = 0xB0, AuraDirect = 0x40,
            AuraApply = 0x80, AuraOverflow = 0x10, AddressableEffect = 0x3B, MainboardEffect = 0x35,
            MainboardEffectColor = 0x36;

        /// <summary>AURA_MODE_STATIC and AURA_MODE_DIRECT (AsusAuraUSBController.h:23 and 41).</summary>
        internal const byte AuraModeStatic = 0x01, AuraModeDirect = 0xFF;

        /// <summary>LEDS_PER_PACKET (AsusAuraUSBController.h:63).</summary>
        internal const int AuraLedsPerPacket = 0x14;

        /// <summary>One channel of an Aura USB controller, an AuraDeviceInfo
        /// (AsusAuraUSBController.h:65-72).</summary>
        internal readonly record struct AuraChannel(byte EffectChannel, byte DirectChannel, byte Leds, byte Headers,
                                                    bool Addressable);

        /// <summary>The channels a config table and firmware string give: on
        /// a mainboard its own LEDs first, where it has any, with their
        /// effect channel numbered from the header count on AULA3-AR32
        /// firmware, then each header
        /// (AsusAuraMainboardController.cpp:15-58), and on an addressable
        /// controller each header alone (AsusAuraAddressableController.cpp:15-24).</summary>
        internal static List<AuraChannel> AuraChannels(byte[] config, string firmware, bool mainboard)
        {
            var channels = new List<AuraChannel>();
            byte headers = config[0x02];
            if (!mainboard)
            {
                for (int i = 0; i < headers; i++) channels.Add(new AuraChannel(0x01, (byte)i, 0x01, 0, true));
                return channels;
            }
            byte boardLeds = config[0x1B];
            byte rgbHeaders = config[0x1D];
            byte effect = 0;
            if (boardLeds < rgbHeaders) rgbHeaders = 0;
            if (boardLeds > 0)
            {
                bool fromZero = firmware != null && firmware.StartsWith("AULA3-AR32", StringComparison.Ordinal);
                channels.Add(new AuraChannel(fromZero ? headers : effect, 0x04, boardLeds, rgbHeaders, false));
                if (!fromZero) effect++;
            }
            for (int i = 0; i < headers; i++)
            {
                channels.Add(new AuraChannel(effect, (byte)i, 0x01, 0, true));
                effect++;
            }
            return channels;
        }

        /// <summary>The lights of the channels: each of a mainboard's own LEDs
        /// and 12 V headers, then one per addressable header, every light a
        /// zone numbered in order.</summary>
        internal static LightMap AuraUsbMap(int productId, IReadOnlyList<AuraChannel> channels)
        {
            var lights = new List<LightInfo>();
            for (int c = 0; c < channels.Count; c++)
            {
                int count = channels[c].Addressable ? 1 : channels[c].Leds;
                for (int led = 0; led < count; led++)
                    lights.Add(Zone(c << 8 | led, lights.Count + 1));
            }
            return new LightMap(lights, "OpenRGB AsusAuraUSBController config table") { Product = ProductKey(productId) };
        }

        private HidLightingModel AuraUsb(string name, int productId, bool mainboard)
            => Model(name, productId,
                new LightMap(Array.Empty<LightInfo>(), "OpenRGB AsusAuraUSBController") { Product = ProductKey(productId) },
                (io, wait) => BeginAuraUsb(io, productId, mainboard));

        /// <summary>GetFirmwareVersion and GetConfigTable
        /// (AsusAuraUSBController.cpp:70-146): a reply 02 carries the
        /// firmware string, a reply 30 the config table from byte 4. Then
        /// SetGen1 on a mainboard, and Direct mode on its own LEDs, the
        /// SetMode OpenRGB's first update sends for each channel with LEDs
        /// (RGBController_AsusAuraUSB.cpp:311-366). Null when the config
        /// table did not come.</summary>
        internal static HidLightingSession BeginAuraUsb(IHidReportChannel io, int productId, bool mainboard)
        {
            if (io == null) return null;
            var version = Ask(io, Frame(PacketSize, AuraReport, AuraFirmware));
            string firmware = version != null && version[1] == 0x02
                ? Encoding.ASCII.GetString(version, 2, 16).TrimEnd('\0')
                : null;
            var table = Ask(io, Frame(PacketSize, AuraReport, AuraConfig));
            if (table == null || table[1] != 0x30) return null;
            var config = new byte[60];
            Array.Copy(table, 4, config, 0, 60);
            var channels = AuraChannels(config, firmware, mainboard);
            if (mainboard)
            {
                if (!io.WriteOutput(Frame(PacketSize, AuraReport, 0x52, 0x53, 0x00, 0x01))) return null;
                foreach (var channel in channels)
                    if (!channel.Addressable && !io.WriteOutput(MainboardEffectFrame(channel.EffectChannel, AuraModeDirect)))
                        return null;
            }
            return new AuraUsbSession(AuraUsbMap(productId, channels), channels, mainboard);
        }

        /// <summary>SendEffect of a mainboard (AsusAuraMainboardController.cpp:160-188),
        /// with no shutdown effect.</summary>
        internal static byte[] MainboardEffectFrame(byte effectChannel, byte mode)
            => Frame(PacketSize, AuraReport, MainboardEffect, effectChannel, 0x00, 0x00, mode);

        /// <summary>SendEffect of an addressable controller
        /// (AsusAuraAddressableController.cpp:60-96).</summary>
        internal static byte[] AddressableEffectFrame(byte channel, byte mode, int rgb)
        {
            var frame = Frame(PacketSize, AuraReport, AddressableEffect, channel, 0x00, mode);
            Put(frame, 0x05, rgb);
            return frame;
        }

        /// <summary>SendColor of a mainboard (AsusAuraMainboardController.cpp:190-225):
        /// the mask of LEDs from <paramref name="start"/>, high byte first, and
        /// the colors at their places.</summary>
        internal static byte[] MainboardColorFrame(int start, int count, int rgb)
        {
            int mask = ((1 << count) - 1) << start;
            var frame = Frame(PacketSize, AuraReport, MainboardEffectColor, (byte)(mask >> 8), (byte)mask, 0x00);
            for (int led = 0; led < count; led++)
                if (0x05 + 3 * (start + led) + 2 < PacketSize) Put(frame, 0x05 + 3 * (start + led), rgb);
            return frame;
        }

        /// <summary>SendDirect (AsusAuraUSBController.cpp:148-212): twenty LEDs
        /// to a frame from an offset, the apply bit on the last frame, and the
        /// 8-bit overflow flag and offset past 255 LEDs.</summary>
        internal static List<byte[]> AuraDirectFrames(byte device, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = colors?.Count ?? 0;
            byte command = device;
            byte offset = 0;
            int inPacket = AuraLedsPerPacket;
            int configured = 0;
            while (configured < count)
            {
                if (configured + inPacket < count)
                {
                    configured += inPacket;
                }
                else
                {
                    command |= AuraApply;
                    inPacket = count - configured;
                    configured = count;
                }
                var frame = Frame(PacketSize, AuraReport, AuraDirect, command, offset, (byte)inPacket);
                for (int i = 0; i < inPacket; i++) Put(frame, 0x05 + i * 3, colors[configured - inPacket + i]);
                frames.Add(frame);
                offset += (byte)inPacket;
                if (configured > 255)
                {
                    command |= AuraOverflow;
                    offset = (byte)(configured - 256);
                }
            }
            return frames;
        }

        internal sealed class AuraUsbSession : HidLightingSession
        {
            private readonly LightMap _map;
            private readonly List<AuraChannel> _channels;
            private readonly bool _mainboard;
            private readonly int?[] _headerColors;

            public AuraUsbSession(LightMap map, List<AuraChannel> channels, bool mainboard)
            {
                _map = map;
                _channels = channels;
                _mainboard = mainboard;
                _headerColors = new int?[channels.Count];
            }

            public override bool Feature => false;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int c = 0; c < _channels.Count; c++)
                {
                    var channel = _channels[c];
                    var mine = new List<int>();
                    for (int i = 0; i < count; i++)
                        if (lights[i].Address >> 8 == c) mine.Add(colors[i]);
                    if (mine.Count == 0) continue;
                    if (!channel.Addressable)
                    {
                        foreach (var frame in AuraDirectFrames(channel.DirectChannel, mine))
                            if (!io.WriteOutput(frame)) return HidLightingPaint.Failed;
                        continue;
                    }
                    int rgb = mine[0] & 0xFFFFFF;
                    if (_headerColors[c] == rgb) continue;
                    if (!SendStatic(io, c, channel, rgb)) return HidLightingPaint.Failed;
                    _headerColors[c] = rgb;
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>OpenRGB's Static mode on one header channel: SetMode
            /// of an addressable controller, or of a mainboard, whose color
            /// frame starts at the LEDs of the channels before it
            /// (AsusAuraMainboardController.cpp:111-153).</summary>
            private bool SendStatic(IHidReportChannel io, int index, AuraChannel channel, int rgb)
            {
                if (!_mainboard) return io.WriteOutput(AddressableEffectFrame((byte)index, AuraModeStatic, rgb));
                if (!io.WriteOutput(MainboardEffectFrame(channel.EffectChannel, AuraModeStatic))) return false;
                int start = 0;
                for (int i = 0; i < index; i++) start += _channels[i].Leds;
                return io.WriteOutput(MainboardColorFrame((byte)start, channel.Leds, rgb));
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Coolers
        // ─────────────────────────────────────────────

        private HidLightingModel Ryuo(string name, int productId)
        {
            int leds = name.Contains("Ryujin", StringComparison.Ordinal) ? 5 : 12;
            return Model(name, productId,
                new LightMap(Edges(leds), "OpenRGB AsusAuraRyuoAIOController.cpp") { Product = ProductKey(productId) },
                (io, wait) =>
                {
                    // SetMode(MODE_DIRECT) with the Direct mode's speed and
                    // direction, 0 (RGBController_AsusAuraRyuoAIO.cpp:194-199,
                    // RGBControllerInterface.h:325-343).
                    if (io == null || !io.WriteOutput(Frame(PacketSize, AuraReport, AddressableEffect, 0x00, 0x22, 0xFF)))
                        return null;
                    return new FramesSession(c => RyuoFrames(leds, c));
                });
        }

        /// <summary>SetLedsDirect of a Ryuo (AsusAuraRyuoAIOController.cpp:71-88).</summary>
        internal static List<byte[]> RyuoFrames(int leds, IReadOnlyList<int> colors)
        {
            var frame = Frame(PacketSize, AuraReport, AuraDirect, 0x80, 0x00, (byte)leds);
            for (int i = 0; i < leds; i++) Put(frame, 5 + i * 3, At(colors, i));
            return new List<byte[]> { frame };
        }

        internal const int StrixLcLeds = 4;

        private HidLightingModel StrixLc(string name, int productId)
            => Model(name, productId,
                new LightMap(Edges(StrixLcLeds), "OpenRGB AsusROGStrixLCController.cpp") { Product = ProductKey(productId) },
                (io, wait) =>
                {
                    // SetMode(MODE_DIRECT) (RGBController_AsusROGStrixLC.cpp:195-199).
                    if (io == null || !io.WriteOutput(Frame(PacketSize, AuraReport, AddressableEffect, 0x00, 0xFF)))
                        return null;
                    return new FramesSession(StrixLcFrames);
                });

        /// <summary>SetLedsDirect of a Strix LC (AsusROGStrixLCController.cpp:67-92).</summary>
        internal static List<byte[]> StrixLcFrames(IReadOnlyList<int> colors)
        {
            var frame = Frame(PacketSize, AuraReport, AuraDirect, 0x00, 0xFF, StrixLcLeds);
            for (int i = 0; i < StrixLcLeds; i++) Put(frame, 5 + i * 3, At(colors, i));
            frame[5 + StrixLcLeds * 3] = 0x77;
            frame[6 + StrixLcLeds * 3] = 0x10;
            frame[7 + StrixLcLeds * 3] = 0xF3;
            return new List<byte[]> { frame };
        }

        // ─────────────────────────────────────────────
        //  The ROG Ally
        // ─────────────────────────────────────────────

        internal const int AllyLength = 64, AllyLeds = 4;

        /// <summary>The Ally's sticks, two LEDs each, left then right
        /// (RGBController_AsusROGAlly.cpp:125-166).</summary>
        private HidLightingModel Ally(string name, int productId)
            => Model(name, productId,
                new LightMap(new[] { Zone(0, 1), Zone(1, 1), Zone(2, 2), Zone(3, 2) }, "OpenRGB RGBController_AsusROGAlly.cpp")
                {
                    Product = ProductKey(productId),
                },
                (io, wait) =>
                {
                    if (io == null) return null;
                    foreach (var frame in AllyOpening())
                        if (!io.SetFeature(frame)) return null;
                    return new AllySession();
                });

        /// <summary>SendInitialization and UpdateBrightness(3)
        /// (AsusROGAllyController.cpp:58-106).</summary>
        internal static List<byte[]> AllyOpening()
        {
            var id = Frame(AllyLength, 0x5D);
            Encoding.ASCII.GetBytes("ASUS Tech.Inc.").CopyTo(id, 1);
            return new List<byte[]>
            {
                Frame(AllyLength, 0x5D, 0xB9),
                id,
                Frame(AllyLength, 0x5A, 0xBA, 0xC5, 0xC4, 0x03),
            };
        }

        /// <summary>UpdateLeds of the Ally (AsusROGAllyController.cpp:108-130).</summary>
        internal static byte[] AllyFrame(IReadOnlyList<int> colors)
        {
            var frame = Frame(AllyLength, 0x5A, 0xD1, 0x08, 0x0C);
            for (int i = 0; i < AllyLeds; i++) Put(frame, 4 + i * 3, At(colors, i));
            return frame;
        }

        internal sealed class AllySession : HidLightingSession
        {
            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io.SetFeature(AllyFrame(colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
