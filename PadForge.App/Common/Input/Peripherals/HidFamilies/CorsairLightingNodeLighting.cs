using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Corsair LT100 towers (1B1C:0C23) and LS100 strips (1B1C:0C1E), lit
    /// as OpenRGB's CorsairLightingNodeController lights them
    /// (CorsairLightingNodeControllerDetect.cpp:47 and 50). The family's other
    /// five products are parts inside a PC and stay out
    /// (tools/gen_openrgb_registrations.py, SOME_PRODUCTS). Every report is a
    /// 65-byte output report, report ID 0, and each is followed by a read of
    /// the 17-byte answer for at most 15 ms, as WriteAndRead does
    /// (CorsairLightingNodeController.h:20-22, CorsairLightingNodeController.cpp:511-536).
    /// A read that times out is no failure there, and none here.
    ///
    /// <para>The channels. OpenRGB gives every product of the family two zones
    /// with no LEDs until its user sets a count (SetupZones,
    /// RGBController_CorsairLightingNode.cpp:298-373, leds_max 204 at line 328).
    /// How many channels each product has is not in its code. SignalRGB's
    /// plugins and CorsairLightingProtocol's examples hold it, and agree: the
    /// LT100 drives one channel of up to four towers of 27 LEDs, 108 in all
    /// (Corsair_LT100_Lighting_Tower.js:35-40, Corsair_LT100_Light_Tower.json,
    /// LEDController.h:276, LT100.ino:21-32), and the LS100 two channels
    /// (Corsair_LS100_Light_Strips.js:31-34, LS100.ino:19-40), each taking
    /// SignalRGB's 138 at most. OpenRGB's comments beside the registrations
    /// say the reverse ("1 channel" for the LS100, "2 channels" for the LT100),
    /// and nothing in its code acts on them. Each channel is a strip of the
    /// map (<see cref="LightStrip"/>), sized on the Lighting tab. A strip with
    /// no count is one zone whose color goes to every LED the channel takes,
    /// so the towers or strips light whatever their number. iCUE asks the
    /// device for its count (0x3C, then 0x3D, CorsairLightingProtocol
    /// LEDController.cpp:168-180), but no host reference sends that pair and
    /// the device side gives no wait between them, so the count is the user's,
    /// as in OpenRGB and SignalRGB.</para>
    ///
    /// <para>The frames. A session begins with the firmware request, 00 02,
    /// and a device that does not answer gets none, where OpenRGB goes on
    /// either way (SendFirmwareRequest, CorsairLightingNodeController.cpp:216-241).
    /// A picture is OpenRGB's Direct mode, channel by channel (SetChannelLEDs,
    /// lines 163-214): the port state 00 38, the channel and 02 for software
    /// control (SendPortState, lines 439-464), then each run of up to 50 LEDs
    /// as three reports, 00 32, the channel, the first LED, the count and the
    /// color, 0 red, 1 green and 2 blue, with that many values from byte 6
    /// (SendDirect, lines 243-278), and the commit, 00 33 FF (SendCommit, lines
    /// 280-305).</para>
    ///
    /// <para>Two writes of OpenRGB's are not copied. OpenRGB sets each channel's
    /// brightness to 100 when it takes Direct mode (00 39, the channel and the
    /// level, SetBrightness and SendBrightness, CorsairLightingNodeController.cpp:91-97
    /// and 466-499, from DeviceUpdateMode, RGBController_CorsairLightingNode.cpp:406-428).
    /// The device stores that level, and it scales only the device's own
    /// effects, never software colors (CorsairLightingProtocol
    /// LEDController.cpp:140-145 and FastLEDController.cpp:436-451). Sending it
    /// would change the brightness the user set in iCUE for good and change no
    /// picture, and SignalRGB never sends it. And the values go out halved, as
    /// SignalRGB sends them to these two products and to no other product of
    /// the family (BrightnessLimiter, Corsair_LT100_Lighting_Tower.js:18,
    /// Corsair_LS100_Light_Strips.js:20). iCUE sends them 0 to 127 alone, even
    /// at its full brightness (CorsairLightingProtocol README.md:186-192,
    /// FastLEDControllerUtils.h:101-104). OpenRGB holds no word on that range
    /// and sends 0 to 255, so the range is the maker's.</para>
    ///
    /// <para>Keep-alive and hand-back. The device goes back to its own lighting
    /// when 20 s pass without a report (CorsairLightingNodeController.cpp:31-36),
    /// so a commit goes out once 5 s pass without one, as OpenRGB's thread
    /// sends it (KeepaliveThread, lines 51-61). The hand-back is port state 01,
    /// hardware control, for each channel, as SignalRGB's Shutdown sends it
    /// (Corsair_LT100_Lighting_Tower.js:54-58, Corsair_LS100_Light_Strips.js:48-51),
    /// and then a commit, as OpenRGB ends its own switch to hardware control
    /// (SetChannelEffect, lines 99-161). The same frames are the crash path, so
    /// a device left under software control goes back at once, not after the
    /// 20 s. OpenRGB takes Corsair's system-wide
    /// mutex around each exchange so it can run beside iCUE
    /// (CorsairLightingNodeController.cpp:526, CorsairDeviceGuard.cpp:50-61).
    /// PadForge leaves the device to iCUE while iCUE runs, as it does every
    /// Corsair device, so it takes no such lock.</para>
    ///
    /// <para>No reference bounds how often a picture may go out, so the bound
    /// is PadForge's, as for Corsair's peripherals
    /// (<see cref="CorsairPeripheralLighting.RepaintMs"/>).</para>
    /// </summary>
    internal sealed class CorsairLightingNodeLighting : IHidLightingFamily
    {
        public static readonly CorsairLightingNodeLighting Instance = new();

        internal const string OpenRgbFamily = "CorsairLightingNodeController";

        /// <summary>CORSAIR_LS100_PID and CORSAIR_LT100_PID
        /// (CorsairLightingNodeControllerDetect.cpp:21 and 24).</summary>
        internal const ushort Ls100 = 0x0C1E;
        internal const ushort Lt100 = 0x0C23;

        /// <summary>CORSAIR_LIGHTING_NODE_WRITE_PACKET_SIZE, _READ_PACKET_SIZE
        /// and _READ_TIMEOUT (CorsairLightingNodeController.h:20-22).</summary>
        internal const int WriteLength = 65;
        internal const int ReadLength = 17;
        internal const int ReadMs = 15;

        /// <summary>How long the first answer may take. OpenRGB waits 15 ms and
        /// goes on without it. A device that never answers gets no session.</summary>
        internal const int BeginReadMs = 1000;

        /// <summary>The packet IDs PadForge sends (CorsairLightingNodeController.h:25-32).</summary>
        internal const byte FirmwareRequest = 0x02;
        internal const byte Direct = 0x32;
        internal const byte Commit = 0x33;
        internal const byte PortState = 0x38;

        /// <summary>CORSAIR_LIGHTING_NODE_PORT_STATE_HARDWARE and _SOFTWARE
        /// (CorsairLightingNodeController.h:47-48).</summary>
        internal const byte Hardware = 0x01;
        internal const byte Software = 0x02;

        /// <summary>The most colors of one Direct report (SetChannelLEDs,
        /// CorsairLightingNodeController.cpp:180-189).</summary>
        internal const int ColorsPerReport = 50;

        /// <summary>An LT100 tower's LEDs, and the four towers its channel
        /// takes (Corsair_LT100_Light_Tower.json, LEDController.h:276).</summary>
        internal const int TowerLeds = 27;
        internal const int Lt100Leds = 4 * TowerLeds;

        /// <summary>The most LEDs an LS100 channel takes
        /// (Corsair_LS100_Light_Strips.js:31-34).</summary>
        internal const int Ls100Leds = 138;

        /// <summary>A commit once 5 s pass without one (KeepaliveThread,
        /// CorsairLightingNodeController.cpp:51-61).</summary>
        internal const int KeepAliveMs = 5000;

        public string Name => "Corsair (LT100, LS100)";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var r = LightReports.Registered(OpenRgbFamily, collection);
            if (r == null || collection.OutputReportLength < WriteLength || collection.InputReportLength < ReadLength) return null;
            return _models.Get(r.Name + ":" + r.ProductId, () => Model(r.Name, (ushort)r.ProductId));
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = Map(productId, StripCounts.None),
            Programs = new[] { VendorPrograms.CorsairIcue },
            RepaintMs = CorsairPeripheralLighting.RepaintMs,
            KeepAliveMs = KeepAliveMs,
            KeepAliveIdleMs = KeepAliveMs,
            Begin = (io, _) => Begin(io, productId),
        };

        /// <summary>The most LEDs each channel of the product takes, by
        /// channel.</summary>
        internal static int[] ChannelLeds(ushort productId)
            => productId == Lt100 ? new[] { Lt100Leds } : new[] { Ls100Leds, Ls100Leds };

        /// <summary>A light's address on a sized strip: its strip's number,
        /// then its LED. A strip with no count is one zone light, its channel
        /// as the address.</summary>
        internal static int StripAddress(int strip, int led) => strip << 8 | led;

        /// <summary>The product's lights: each channel one zone, or with a count
        /// set for it that many edge lights, in the order its LEDs take their
        /// colors.</summary>
        internal static LightMap Map(ushort productId, IReadOnlyList<int> counts)
        {
            var most = ChannelLeds(productId);
            var lights = new List<LightInfo>();
            var strips = new LightStrip[most.Length];
            for (int channel = 0; channel < most.Length; channel++)
            {
                int strip = channel + 1;
                int leds = Math.Min(StripCounts.At(counts, strip), most[channel]);
                strips[channel] = new LightStrip(strip, most[channel], leds);
                if (leds == 0) lights.Add(new LightInfo(channel, LightKind.Zone, 0, LightKeySource.None) { Zone = strip });
                for (int led = 0; led < leds; led++)
                    lights.Add(new LightInfo(StripAddress(strip, led), LightKind.Edge, 0, LightKeySource.None) { Zone = strip });
            }
            return new LightMap(lights, "OpenRGB RGBController_CorsairLightingNode.cpp")
            {
                Product = $"corsair:{CorsairLights.VendorId:X4}:{productId:X4}",
                Strips = strips,
            };
        }

        /// <summary>The colors <paramref name="channel"/> takes in a picture, one
        /// per LED: its edge lights' in map order, or with none its zone's color
        /// on every LED it takes.</summary>
        internal static List<int> ChannelColors(int channel, int most, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var leds = new List<int>();
            int zone = -1;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                var light = lights[i];
                if (light.Kind == LightKind.Edge && light.Address >> 8 == channel + 1) leds.Add(colors[i] & 0xFFFFFF);
                else if (light.Kind == LightKind.Zone && light.Zone == channel + 1) zone = colors[i] & 0xFFFFFF;
            }
            if (leds.Count == 0 && zone >= 0)
                for (int led = 0; led < most; led++) leds.Add(zone);
            return leds;
        }

        private static byte[] Report(params byte[] bytes)
        {
            var report = new byte[WriteLength];
            Array.Copy(bytes, 0, report, 1, bytes.Length);
            return report;
        }

        /// <summary>SendFirmwareRequest: 00 02.</summary>
        internal static byte[] FirmwareReport() => Report(FirmwareRequest);

        /// <summary>SendPortState: 00 38, the channel and the state.</summary>
        internal static byte[] PortStateReport(int channel, byte state) => Report(PortState, (byte)channel, state);

        /// <summary>SendCommit: 00 33 FF.</summary>
        internal static byte[] CommitReport() => Report(Commit, 0xFF);

        /// <summary>SendDirect: 00 32, the channel, the first LED, the count and
        /// the color, then that many values from byte 6, each halved to the
        /// maker's 0 to 127.</summary>
        internal static byte[] DirectReport(int channel, int start, int count, int color, IReadOnlyList<int> leds)
        {
            var report = Report(Direct, (byte)channel, (byte)start, (byte)count, (byte)color);
            int shift = 16 - 8 * color;
            for (int i = 0; i < count; i++)
                report[6 + i] = (byte)(((leds[start + i] >> shift) & 0xFF) >> 1);
            return report;
        }

        /// <summary>SetChannelLEDs: software control, each run of 50 LEDs as a
        /// red, a green and a blue report, then the commit.</summary>
        internal static List<byte[]> ChannelFrames(int channel, IReadOnlyList<int> leds)
        {
            var frames = new List<byte[]> { PortStateReport(channel, Software) };
            for (int start = 0; start < leds.Count; start += ColorsPerReport)
            {
                int count = Math.Min(ColorsPerReport, leds.Count - start);
                for (int color = 0; color < 3; color++) frames.Add(DirectReport(channel, start, count, color, leds));
            }
            frames.Add(CommitReport());
            return frames;
        }

        /// <summary>Hardware control for each channel, then a commit.</summary>
        internal static byte[][] HandBackFrames(ushort productId)
        {
            int channels = ChannelLeds(productId).Length;
            var frames = new byte[channels + 1][];
            for (int channel = 0; channel < channels; channel++) frames[channel] = PortStateReport(channel, Hardware);
            frames[channels] = CommitReport();
            return frames;
        }

        /// <summary>WriteAndRead: the report out, then its answer read for at
        /// most 15 ms. False only when the write or the read failed.</summary>
        internal static bool Send(IHidReportChannel io, byte[] report)
            => io.WriteOutput(report) && io.ReadInput(new byte[Math.Max(ReadLength, io.InputReportLength)], ReadMs) >= 0;

        internal static HidLightingSession Begin(IHidReportChannel io, ushort productId)
        {
            if (io == null || !io.WriteOutput(FirmwareReport())) return null;
            var answer = new byte[Math.Max(ReadLength, io.InputReportLength)];
            return io.ReadInput(answer, BeginReadMs) > 0 ? new Session(productId) : null;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly ushort _productId;
            private readonly byte[][] _handBack;
            private LightMap _map;
            private bool _taken;

            public Session(ushort productId)
            {
                _productId = productId;
                _handBack = HandBackFrames(productId);
            }

            public override bool Feature => false;

            /// <summary>The model's map until a count is set.</summary>
            public override LightMap Map => _map;

            /// <summary>The device is told nothing of a count: OpenRGB's
            /// SendLEDCount sends nothing (CorsairLightingNodeController.cpp:501-504).</summary>
            public override bool SizeStrips(IHidReportChannel io, Action<int> wait, IReadOnlyList<int> counts)
            {
                _map = CorsairLightingNodeLighting.Map(_productId, counts);
                return true;
            }

            public override byte[][] CrashFrames => _taken ? _handBack : Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                var most = ChannelLeds(_productId);
                // Taken before the first write: a picture cut short still leaves a channel
                // under software control.
                _taken = true;
                for (int channel = 0; channel < most.Length; channel++)
                    foreach (var frame in ChannelFrames(channel, ChannelColors(channel, most[channel], lights, colors)))
                        if (!Send(io, frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>The commit alone, as OpenRGB's thread sends it.</summary>
            public override bool KeepAlive(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
                => io != null && Send(io, CommitReport());

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_taken) return true;
                if (io == null) return false;
                foreach (var frame in _handBack)
                    if (!Send(io, frame)) return false;
                return true;
            }
        }
    }
}
