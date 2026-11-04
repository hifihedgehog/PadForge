using System;
using System.Collections.Generic;
using PadForge.Common.Input;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// Alienware and Dell lighting OpenRGB and SignalRGB have no row for, as
    /// alienfx-tools lights it (AlienFX-SDK/AlienFX_SDK, #557). OpenRGB lists two
    /// products of the laptop controller and alienfx-tools knows any of them, since it
    /// tells a protocol by the vendor and the collection's report length
    /// (AlienFXProbeDevice, AlienFX_SDK.cpp:194-235).
    ///
    /// <para>One light for the whole device. alienfx-gui asks its user to map each light of
    /// a device to what it lights, and neither it nor the SDK carries such a map, so no
    /// reference says which ID is which key. Every ID a protocol can name takes the color.
    /// An ID a device does not have is ignored there, as alienfx-gui's light probe relies
    /// on.</para>
    ///
    /// <para>Protocols. Report 02 on 187C with 9 bytes (4 bits a channel) or 12 bytes: reset,
    /// a color with a mask of lights, loop and update (Reset, SetMaskAndColor, SetMultiColor
    /// and UpdateColors). Report 00 on 187C with 34 bytes: control 4 and 1, 27 with the color
    /// and up to 26 IDs, then control 3. Feature report CC on 0D62 usage CC, the per-key
    /// keyboards of the same laptops: reset, status, 8C 02 with four bytes per key and an
    /// ID from 1, loop and update. Report 00 on 187C or 0424 with 65 bytes filled with FF: 92
    /// 37 with a mask and a checksum. On 0461, the mice, 40 10 0C per light with the answer
    /// read after it. On 04F2, the keyboards, a feature report with the count of lights, then
    /// report 01 with four blocks of twelve bytes each. Nothing here is stored: each
    /// protocol's save is a command of its own.</para>
    ///
    /// <para>0424, 0461 and 04F2 are chip makers that sell to anyone, so a collection there
    /// must name Alienware in its strings. alienfx-tools does not ask, since its user runs
    /// it on an Alienware. Hand-back. Nothing goes out, since no reference reads back what
    /// the device showed, and the device shows PadForge's last picture until Alienware
    /// Command Center or a restart sets it again.</para>
    /// </summary>
    internal sealed class AlienFxLighting : IHidLightingFamily
    {
        public static readonly AlienFxLighting Instance = new();

        internal const ushort Alienware = 0x187C, Darfon = 0x0D62, Microchip = 0x0424, Primax = 0x0461, Chicony = 0x04F2;

        internal enum Api { V2, V3, V4, V5, V6, V7, V8 }

        /// <summary>The IDs a whole device takes per protocol: a mask of 24, of 8, the 26 of one
        /// frame, the 128 of a keyboard's IDs in a byte, the mouse's few.</summary>
        internal const int V4Lights = 26, V5Lights = 128, V7Lights = 4, V8Lights = 128;

        /// <summary>brightnessScale's maximum for the protocols that send it
        /// (AlienFX_SDK.h:15) with the SDK's own default where no brightness is set.</summary>
        private const byte V5Bright = 0xFF, V6Bright = 0x64, V7Bright = 0x64;

        /// <summary>The SDK polls up to a second on the old protocols and with no bound on
        /// the laptop controller. These bounds keep a device that never says ready from holding
        /// the worker: its picture goes out after them, as the SDK's own does past its count.</summary>
        private const int ReadyPolls = 10, ReadyWaitMs = 10;
        private const int V4ReadyPolls = 25, V4ReadyWaitMs = 20;

        /// <summary>ALIENFX_V2_READY and ALIENFX_V4_BUSY (AlienFX_SDK.h:15-20).</summary>
        private const byte V2Ready = 0x10, V4Busy = 34;

        public string Name => "AlienFX";

        private readonly Dictionary<(ushort, ushort, Api), HidLightingModel> _models = new();

        /// <summary>AlienFXProbeDevice: the protocol of a collection, or null.</summary>
        internal static Api? ApiOf(HidCollectionInfo c)
        {
            switch (c.VendorId)
            {
                case Darfon:
                    return c.Usage == 0xCC && c.OutputReportLength == 0 && c.FeatureReportLength > 0 ? Api.V5 : null;
                case Alienware:
                    return c.OutputReportLength switch
                    {
                        9 => Api.V2,
                        12 => Api.V3,
                        34 => Api.V4,
                        65 => Api.V6,
                        _ => null,
                    };
                case Microchip when c.OutputReportLength == 65 && c.ProductId != 0x274C:
                    return Api.V6;
                case Primax when c.OutputReportLength == 65:
                    return Api.V7;
                case Chicony when c.OutputReportLength == 65:
                    return Api.V8;
                default:
                    return null;
            }
        }

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || ApiOf(collection) is not Api api) return null;
            bool ask = collection.VendorId is Microchip or Primax or Chicony;
            var key = (collection.VendorId, collection.ProductId, api);
            lock (_models)
            {
                if (_models.TryGetValue(key, out var known)) return known;
                ushort vendor = collection.VendorId, product = collection.ProductId;
                return _models[key] = new HidLightingModel
                {
                    Family = this,
                    Name = vendor == Darfon ? "Alienware Laptop Keyboard" : vendor == Primax ? "Alienware Mouse"
                        : vendor == Chicony ? "Alienware Keyboard" : "Alienware Lighting",
                    Map = new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } }, "alienfx-tools AlienFX_SDK.cpp")
                    {
                        Product = $"alienfx:{vendor:X4}:{product:X4}",
                    },
                    Programs = new[] { VendorPrograms.AlienwareCommandCenter },
                    RepaintMs = 100,
                    Identify = ask ? NamesAlienware : null,
                    Begin = (io, wait) => Begin(io, wait, api),
                };
            }
        }

        internal static bool NamesAlienware(IHidReportChannel io)
            => (io.ProductString ?? "").Contains("Alienware", StringComparison.OrdinalIgnoreCase)
               || (io.ManufacturerString ?? "").Contains("Alienware", StringComparison.OrdinalIgnoreCase);

        private static byte[] Frame(int length, params byte[] head)
        {
            var frame = new byte[length];
            head.CopyTo(frame, 0);
            return frame;
        }

        // ── Report 02 on 187C ──

        /// <summary>SetMaskAndColor: op 3 for a color, the chain's number, a mask of every light,
        /// then the color twice, at 4 bits a channel on the 9-byte protocol.</summary>
        internal static byte[] V1Color(Api api, int rgb)
        {
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            return api == Api.V2
                ? Frame(9, 0x02, 0x03, 0x01, 0xFF, 0xFF, 0xFF, (byte)((r & 0xF0) | (g >> 4)), (byte)((b & 0xF0) | (r >> 4)), (byte)((g & 0xF0) | (b >> 4)))
                : Frame(12, 0x02, 0x03, 0x01, 0xFF, 0xFF, 0xFF, r, g, b, r, g, b);
        }

        /// <summary>WaitForReady: the status command, then the input report. A channel that
        /// cannot read an input report waits as long once.</summary>
        private static void WaitV1Ready(IHidReportChannel io, Action<int> wait, int length)
        {
            if (io is not IHidInputReportReader reader)
            {
                wait?.Invoke(ReadyPolls * ReadyWaitMs);
                return;
            }
            var answer = new byte[Math.Max(length, io.InputReportLength)];
            for (int i = 0; i < ReadyPolls; i++)
            {
                if (!io.WriteOutput(Frame(length, 0x02, 0x06))) return;
                if (reader.GetInputReport(0x02, answer) && (answer[0] == V2Ready || answer[1] == V2Ready)) return;
                wait?.Invoke(ReadyWaitMs);
            }
        }

        /// <summary>IsDeviceReady: byte 2 of input report 0 is not the busy status, and 0 is a
        /// device that says nothing (AlienFX_SDK.cpp, IsDeviceReady).</summary>
        private static void WaitV4Ready(IHidReportChannel io, Action<int> wait)
        {
            if (io is not IHidInputReportReader reader)
            {
                wait?.Invoke(V4ReadyWaitMs);
                return;
            }
            var answer = new byte[Math.Max(34, io.InputReportLength)];
            for (int i = 0; i < V4ReadyPolls; i++)
            {
                if (!reader.GetInputReport(0x00, answer) || answer[2] != V4Busy) return;
                wait?.Invoke(V4ReadyWaitMs);
            }
        }

        // ── Report 00 on 187C ──

        internal static byte[] V4Control(byte type) => Frame(34, 0x00, 0x03, 0x21, 0x00, type, 0xFF, 0xFF);

        internal static byte[] V4Color(int rgb)
        {
            var frame = Frame(34, 0x00, 0x03, 0x27, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x00, V4Lights);
            for (int i = 0; i < V4Lights; i++) frame[8 + i] = (byte)i;
            return frame;
        }

        // ── Feature report CC on 0D62 ──

        internal static List<byte[]> V5Picture(int length, int rgb)
        {
            var frames = new List<byte[]>();
            for (int id = 0; id < V5Lights;)
            {
                var frame = Frame(length, 0xCC, 0x8C, 0x02);
                for (int at = 4; at + 3 < length && id < V5Lights; at += 4, id++)
                {
                    frame[at] = (byte)(id + 1);
                    frame[at + 1] = (byte)(rgb >> 16);
                    frame[at + 2] = (byte)(rgb >> 8);
                    frame[at + 3] = (byte)rgb;
                }
                frames.Add(frame);
            }
            return frames;
        }

        // ── Report 00 with 65 bytes ──

        /// <summary>COMMV6_colorSet with SetMaskAndColor's bytes for a color, on a frame filled
        /// with FF. The last is the checksum, the color, the mask and 8 in XOR.</summary>
        internal static byte[] V6Color(int rgb)
        {
            var frame = new byte[65];
            Array.Fill(frame, (byte)0xFF);
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            const byte mask = 0xFF;
            frame[0] = 0x00;
            frame[1] = 0x92;
            frame[2] = 0x37;
            frame[3] = 0x0A;
            frame[6] = 0x87;
            frame[8] = 0x04;
            frame[9] = mask;
            frame[10] = r;
            frame[11] = g;
            frame[12] = b;
            frame[13] = V6Bright;
            frame[14] = (byte)(r ^ g ^ b ^ mask ^ 8);
            return frame;
        }

        internal static byte[] V6Reset()
        {
            var frame = new byte[65];
            Array.Fill(frame, (byte)0xFF);
            frame[0] = 0x00;
            frame[1] = 0x95;
            frame[2] = frame[3] = frame[4] = 0x00;
            return frame;
        }

        // ── 0461 and 04F2 ──

        /// <summary>SetAction for a color: 40 10 0C 00, op 1, the brightness, the light and the
        /// color.</summary>
        internal static byte[] V7Color(byte light, int rgb)
            => Frame(65, 0x00, 0x40, 0x10, 0x0C, 0x00, 0x01, V7Bright, light, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        /// <summary>COMMV8_readyToColor with the count, which goes as a feature report.</summary>
        internal static byte[] V8Ready(int count) => Frame(65, 0x01, 0x0E, (byte)count, 0x00, 0x01);

        /// <summary>AddV8DataBlock for a color, four blocks of twelve bytes to a frame, from byte 5
        /// every fifteen. Byte 4 is the frame's number.</summary>
        internal static List<byte[]> V8Picture(int rgb)
        {
            var frames = new List<byte[]>();
            byte number = 1;
            for (int id = 0; id < V8Lights; number++)
            {
                var frame = Frame(65, 0x01, 0x0E, 0x01, 0x00, number);
                for (int at = 5; at < 65 && id < V8Lights; at += 15, id++)
                {
                    frame[at] = (byte)id;
                    frame[at + 1] = 0x81;
                    frame[at + 2] = 0x00;
                    frame[at + 3] = 0xA5;
                    frame[at + 4] = 0x00;
                    frame[at + 5] = frame[at + 8] = (byte)(rgb >> 16);
                    frame[at + 6] = frame[at + 9] = (byte)(rgb >> 8);
                    frame[at + 7] = frame[at + 10] = (byte)rgb;
                    frame[at + 11] = 0x02;
                }
                frames.Add(frame);
            }
            return frames;
        }

        internal static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, Api api)
        {
            if (io == null) return null;
            switch (api)
            {
                case Api.V5:
                    if (!io.SetFeature(Frame(io.FeatureReportLength, 0xCC, 0x94))) return null;
                    if (!io.SetFeature(Frame(io.FeatureReportLength, 0xCC, 0x83, 0x38, 0x9C, V5Bright))) return null;
                    break;
                case Api.V6:
                    if (!io.WriteOutput(V6Reset())) return null;
                    break;
            }
            return new Session(api);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Api _api;

            public Session(Api api) => _api = api;

            public override bool Feature => _api == Api.V5;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                return Send(io, wait, rgb) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            private bool Send(IHidReportChannel io, Action<int> wait, int rgb)
            {
                switch (_api)
                {
                    case Api.V2:
                    case Api.V3:
                        int length = _api == Api.V2 ? 9 : 12;
                        if (!io.WriteOutput(Frame(length, 0x02, 0x07, 0x04))) return false;
                        WaitV1Ready(io, wait, length);
                        return io.WriteOutput(V1Color(_api, rgb)) && io.WriteOutput(Frame(length, 0x02, 0x04))
                               && io.WriteOutput(Frame(length, 0x02, 0x05));
                    case Api.V4:
                        WaitV4Ready(io, wait);
                        return io.WriteOutput(V4Control(4)) && io.WriteOutput(V4Control(1)) && io.WriteOutput(V4Color(rgb))
                               && io.WriteOutput(V4Control(3));
                    case Api.V5:
                        int feature = io.FeatureReportLength;
                        // Reset, then GetDeviceStatus: the status command and a read.
                        if (!io.SetFeature(Frame(feature, 0xCC, 0x94)) || !io.SetFeature(Frame(feature, 0xCC, 0x93))) return false;
                        io.GetFeature(0xCC, new byte[Math.Max(feature, 64)]);
                        foreach (var frame in V5Picture(feature, rgb))
                            if (!io.SetFeature(frame)) return false;
                        return io.SetFeature(Frame(feature, 0xCC, 0x8C, 0x13)) && io.SetFeature(Frame(feature, 0xCC, 0x8B, 0x01, 0xFF));
                    case Api.V6:
                        return io.WriteOutput(V6Color(rgb));
                    case Api.V7:
                        var answer = new byte[Math.Max(65, io.InputReportLength)];
                        for (byte light = 0; light < V7Lights; light++)
                        {
                            if (!io.WriteOutput(V7Color(light, rgb))) return false;
                            io.ReadInput(answer, 50);
                        }
                        return true;
                    default:
                        if (!io.SetFeature(V8Ready(V8Lights))) return false;
                        wait?.Invoke(6);
                        foreach (var frame in V8Picture(rgb))
                            if (!io.WriteOutput(frame)) return false;
                        return true;
                }
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
