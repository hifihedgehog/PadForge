using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// HyperX keyboards, Kingston's and HP's, lit as OpenRGB's
    /// HyperXKeyboardController family lights them (#557), key by key. The
    /// collections are OpenRGB's Windows registrations (the _WIN32 branch of
    /// each *ControllerDetect.cpp), matched by OpenRGB's rules, and the
    /// detector picks the protocol.
    ///
    /// <para>Every one of these keyboards is driven in OpenRGB's Direct mode
    /// alone and keeps its colors only while they keep coming: each
    /// RGBController sends the last picture again whenever none went out for
    /// 50 ms (KeepaliveThread, checked every 10 ms), and the Alloy Elite 2's
    /// after 1000 ms (RGBController_HyperXAlloyElite2.cpp:308-323), and each
    /// picture, a resend included, starts the wait again (line 287). The
    /// Alloy Origins controllers never set that time, so OpenRGB resends
    /// their picture on every 10 ms check
    /// (RGBController_HyperXAlloyOrigins.cpp:280-293). PadForge keeps the
    /// family's 50 ms for them. Here the worker sends the last
    /// picture again on the same terms, as the session's keep-alive
    /// (<see cref="HidLightingModel.KeepAliveMs"/>). None of the frames is a
    /// save, and no reference hands one of these keyboards back: OpenRGB's
    /// Shutdown sends nothing, and SignalRGB's plugins send a last color
    /// (signal-plugins HyperX_Alloy_Elite_2.js:87-89). When the frames stop,
    /// the keyboard is taken to go back to its own lighting, as the
    /// keep-alive implies. That has not been seen on a device.</para>
    ///
    /// <para>The Alloy Elite RGB's Direct mode in OpenRGB first stores a
    /// static effect in profile 1 when Direct is chosen (DeviceUpdateMode,
    /// RGBController_HyperXAlloyElite.cpp:322-333, SetMode and SendEffect,
    /// HyperXAlloyEliteController.cpp:67-130 and 269-370), which no
    /// reference reads back to restore. PadForge sends the direct frames
    /// alone, as OpenRGB does from start-up while Direct is the active mode
    /// it starts in. Whether that keyboard shows them without the static
    /// effect has not been seen on a device.</para>
    /// </summary>
    internal sealed class HyperXKeyboardLighting : IHidLightingFamily
    {
        public static readonly HyperXKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "HyperXKeyboardController";

        /// <summary>HYPERX_KEYBOARD_VID and HP_KEYBOARD_VID
        /// (HyperXAlloyElite2ControllerDetect.cpp:18 and 24).</summary>
        internal const ushort KingstonVid = 0x0951;
        internal const ushort HpVid = 0x03F0;

        /// <summary>The keep-alive: a picture goes out again once none went
        /// out for this long (KeepaliveThread in every RGBController of the
        /// family but the Alloy Elite 2's).</summary>
        internal const int KeepAliveMs = 50;

        /// <summary>The Alloy Elite 2's keep-alive
        /// (RGBController_HyperXAlloyElite2.cpp:316).</summary>
        internal const int Elite2KeepAliveMs = 1000;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it beyond the keep-alive, so the bound is PadForge's, as
        /// the Cooler Master family's.</summary>
        internal const int RepaintMs = 50;

        /// <summary>The bound of the Alloy Origins Core's variant read, which
        /// OpenRGB makes with hid_read and no bound
        /// (HyperXAlloyOriginsCoreController.cpp:89-96). PadForge's.</summary>
        internal const int AnswerMs = 1000;

        public string Name => "HyperX";

        private readonly ConcurrentDictionary<(string Detector, int ProductId), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || (collection.VendorId != KingstonVid && collection.VendorId != HpVid)) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            // Several registrations name an interface alone, and the collection
            // that takes the frames is the one with reports long enough for them.
            if (ProtocolOf(registration.Detector) is Protocol protocol && !Carries(protocol, collection)) return null;
            var key = (registration.Detector, (int)collection.ProductId);
            if (_models.TryGetValue(key, out var known)) return known;
            var model = Model(registration, collection.ProductId);
            return model == null ? null : _models.GetOrAdd(key, model);
        }

        /// <summary>Whether a collection has the reports a protocol sends: the Alloy Elite's and
        /// the FPS's feature reports of 264 bytes (HyperXAlloyEliteController.cpp:266,
        /// HyperXAlloyFPSController.cpp:135), the other Alloys' of 65
        /// (HyperXAlloyElite2Controller.cpp:182, HyperXAlloyOriginsController.cpp:115,
        /// HyperXAlloyOrigins60and65Controller.cpp:104), and the output reports of 65 of the rest
        /// (HyperXAlloyOriginsCoreController.cpp:89, HyperXEve1800Controller.cpp:61).</summary>
        internal static bool Carries(Protocol protocol, HidCollectionInfo collection) => protocol switch
        {
            Protocol.Elite or Protocol.Fps => collection.FeatureReportLength >= 264,
            Protocol.OriginsCore or Protocol.Eve1800 or Protocol.Origins2_65 => collection.OutputReportLength >= 65,
            _ => collection.FeatureReportLength >= 65,
        };

        /// <summary>The protocol a detector drives.</summary>
        internal enum Protocol
        {
            Elite2,
            Elite,
            Fps,
            Origins,
            Origins60And65,
            OriginsCore,
            Eve1800,
            Origins2_65,
        }

        internal static Protocol? ProtocolOf(string detector) => detector switch
        {
            "DetectHyperXAlloyElite2" => Protocol.Elite2,
            "DetectHyperXAlloyElite" => Protocol.Elite,
            "DetectHyperXAlloyFPS" => Protocol.Fps,
            "DetectHyperXAlloyOrigins" => Protocol.Origins,
            "DetectHyperXAlloyOrigins60and65" => Protocol.Origins60And65,
            "DetectHyperXAlloyOriginsCore" => Protocol.OriginsCore,
            "DetectHyperXEve1800" => Protocol.Eve1800,
            "DetectHyperXOrigins2_65" => Protocol.Origins2_65,
            _ => null,
        };

        /// <summary>HYPERX_ALLOY_ORIGINS_65_HP_PID, the one Origins 60 and 65
        /// product with the 65's layout (GetAlloyOrigins60and65MappingLayoutType,
        /// HyperXAlloyOrigins60and65ControllerDetect.cpp:28-41).</summary>
        internal const ushort Origins65Hp = 0x038F;

        internal static HidLightingModel Model(OpenRgbRegistration registration, ushort productId)
        {
            if (ProtocolOf(registration.Detector) is not Protocol protocol) return null;
            var lights = Lights(protocol, productId, KeyboardLayout.AnsiQwerty);
            string product = $"hyperx:{registration.VendorId:X4}:{productId:X4}";
            return new HidLightingModel
            {
                Family = Instance,
                Name = registration.Name,
                Map = new LightMap(lights, "OpenRGB HyperXKeyboardController") { Product = product },
                Programs = new[] { VendorPrograms.Ngenuity },
                RepaintMs = RepaintMs,
                KeepAliveMs = protocol == Protocol.Elite2 ? Elite2KeepAliveMs : KeepAliveMs,
                KeepAliveIdleMs = protocol == Protocol.Elite2 ? Elite2KeepAliveMs : KeepAliveMs,
                Begin = (io, wait) => Begin(io, protocol, productId, product),
            };
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The lights in the order of the colors OpenRGB hands its
        /// controller: the led_names table of the model, or for the Alloy
        /// Origins Core the layout OpenRGB builds for it, each light's
        /// address its place in that order, or its LED value for the
        /// Core.</summary>
        internal static IReadOnlyList<LightInfo> Lights(Protocol protocol, ushort productId, KeyboardLayout region)
        {
            switch (protocol)
            {
                case Protocol.OriginsCore:
                    // KeyboardLayoutManager(layout, KEYBOARD_SIZE_TKL, hyperx_core_layout)
                    // (RGBController_HyperXAlloyOriginsCore.cpp:155-182).
                    return KeyboardLayouts.Build("hyperx_core_layout", region).Lights((_, key) => (int)key.Value);
                case Protocol.Eve1800:
                {
                    // Ten zones, "Zone 1" to "Zone 10" (RGBController_HyperXEve1800.cpp:16 and 66-80).
                    var zones = new LightInfo[10];
                    for (int i = 0; i < zones.Length; i++)
                        zones[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
                    return zones;
                }
                default:
                    return Named(NamesOf(protocol, productId));
            }
        }

        internal static string[] NamesOf(Protocol protocol, ushort productId) => protocol switch
        {
            Protocol.Elite2 => OpenRgbLedNames.HyperXAlloyElite2,
            Protocol.Elite => OpenRgbLedNames.HyperXAlloyElite,
            Protocol.Fps => OpenRgbLedNames.HyperXAlloyFps,
            Protocol.Origins => OpenRgbLedNames.HyperXAlloyOrigins,
            Protocol.Origins60And65 => productId == Origins65Hp
                ? OpenRgbLedNames.HyperXAlloyOrigins65
                : OpenRgbLedNames.HyperXAlloyOrigins60,
            Protocol.Origins2_65 => OpenRgbLedNames.HyperXOrigins2_65,
            _ => Array.Empty<string>(),
        };

        /// <summary>Lights from OpenRGB LED names, addressed by their place: a
        /// key by its usage (<see cref="OpenRgbKeys"/>), an "RGB Strip" LED as
        /// an edge light, and any other name as another light with its
        /// label, as KeyboardLayoutManager.Lights makes them.</summary>
        internal static IReadOnlyList<LightInfo> Named(IReadOnlyList<string> names)
        {
            var lights = new LightInfo[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (name.StartsWith("RGB Strip", StringComparison.Ordinal))
                {
                    lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
                    continue;
                }
                OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number);
                lights[i] = usage != 0
                    ? new LightInfo(i, LightKind.Key, usage, LightKeySource.Generated)
                    : new LightInfo(i, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
            }
            return lights;
        }

        /// <summary>The colors of a picture in the order of the lights'
        /// addresses, <paramref name="size"/> of them.</summary>
        internal static int[] Ordered(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int size)
            => CoolerMasterLighting.ByAddress(lights, colors, size);

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>SKIP_INDICES, the Alloy Elite 2's empty positions
        /// (HyperXAlloyElite2Controller.cpp:19).</summary>
        internal static readonly int[] Elite2Skip = { 23, 29, 41, 47, 70, 71, 76, 77, 87, 88, 93, 99, 100, 102, 108, 113 };

        /// <summary>skip_idx, the Alloy Origins' empty positions
        /// (HyperXAlloyOriginsController.cpp:17).</summary>
        internal static readonly int[] OriginsSkip =
            { 23, 29, 41, 47, 59, 70, 71, 87, 88, 93, 99, 100, 102, 108, 113, 114, 120, 123, 124 };

        private static void Entry(byte[] frame, int at, int color)
        {
            frame[at] = 0x81;
            frame[at + 1] = (byte)(color >> 16);
            frame[at + 2] = (byte)(color >> 8);
            frame[at + 3] = (byte)color;
        }

        /// <summary>The Alloy Elite 2's picture: 04 F2, then 0x81 R G B
        /// entries sixteen to a 65-byte report, a blank entry at each skipped
        /// position, then empty reports up to nine (SetLEDsDirect and
        /// SendDirectInitialization, HyperXAlloyElite2Controller.cpp:56-183).
        /// All feature reports.</summary>
        internal static List<byte[]> Elite2Frames(int[] colors)
        {
            var frames = new List<byte[]> { Report65(0x04, 0xF2) };
            var buf = new byte[65];
            int at = 1, colorIndex = 0, skipped = 0, sent = 0, skip = 0;
            while (colorIndex < colors.Length)
            {
                buf[at] = 0x81;
                if (Elite2Skip[skip] == colorIndex + skipped)
                {
                    skip++;
                    if (skip >= Elite2Skip.Length) skip = 0;
                    skipped++;
                }
                else
                {
                    Entry(buf, at, colors[colorIndex]);
                    colorIndex++;
                }
                at += 4;
                if (at >= buf.Length || colorIndex == colors.Length)
                {
                    frames.Add(buf);
                    buf = new byte[65];
                    at = 1;
                    sent++;
                }
            }
            for (; sent < 9; sent++) frames.Add(new byte[65]);
            return frames;
        }

        private static byte[] Report65(params byte[] payload)
        {
            var frame = new byte[65];
            payload.CopyTo(frame, 1);
            return frame;
        }

        /// <summary>The Alloy Origins' and Origins 60 and 65's picture: 04 F2
        /// with the report count at byte 9, then that many reports of up to
        /// sixteen 0x81 R G B entries, one black entry in a report with no
        /// colors left. The Origins first puts a black color at each skipped
        /// position (SetLEDsDirect, HyperXAlloyOriginsController.cpp:56-156,
        /// HyperXAlloyOrigins60and65Controller.cpp:51-148). All feature
        /// reports.</summary>
        internal static List<byte[]> OriginsFrames(int[] colors, int reports, int[] skip)
        {
            var all = new List<int>(colors);
            if (skip != null)
                foreach (int at in skip)
                    all.Insert(Math.Min(at, all.Count), 0);
            var init = Report65(0x04, 0xF2);
            init[9] = (byte)reports;
            var frames = new List<byte[]> { init };
            int left = all.Count, sent = 0;
            for (int p = 0; p < reports; p++)
            {
                var frame = new byte[65];
                int count = left > 16 ? 16 : left;
                if (count > 0)
                {
                    for (int i = 0; i < count; i++) Entry(frame, i * 4 + 1, all[sent + i]);
                    sent += count;
                    left -= count;
                }
                else
                {
                    Entry(frame, 1, 0);
                }
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>keys[], the byte each of the 106 keys' color takes in the
        /// Alloy Elite's and Alloy FPS's direct report
        /// (HyperXAlloyEliteController.cpp:17-25, HyperXAlloyFPSController.cpp:17-25).</summary>
        internal static readonly byte[] EliteKeyBytes =
        {
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14,
            0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x20, 0x21, 0x22,
            0x23, 0x24, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30,
            0x31, 0x32, 0x33, 0x34, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3E, 0x3F, 0x41,
            0x44, 0x45, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x51, 0x54, 0x55,
            0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5E, 0x5F, 0x61, 0x64, 0x65, 0x68, 0x69, 0x6A,
            0x6B, 0x6C, 0x6E, 0x6F, 0x74, 0x75, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E,
            0x7F, 0x81, 0x84, 0x85, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0x91,
            0x94, 0x95,
        };

        /// <summary>extended_red, extended_grn and extended_blu, the bytes of
        /// the Alloy Elite's extended report for its 22 other LEDs
        /// (HyperXAlloyEliteController.cpp:27-29).</summary>
        internal static readonly byte[] EliteExtendedRed =
            { 0x08, 0x48, 0x88, 0x09, 0x89, 0x0A, 0x8A, 0x0B, 0x8B, 0x0C, 0x8C, 0x0D, 0x8D, 0x0E, 0x8F, 0x8E, 0x0F, 0x4F, 0x92, 0x13, 0x93, 0x12 };
        internal static readonly byte[] EliteExtendedGreen =
            { 0x29, 0x28, 0x78, 0x19, 0x79, 0x1A, 0x7A, 0x1B, 0x7B, 0x1C, 0x7C, 0x1D, 0x7D, 0x1E, 0x6E, 0x7E, 0x1F, 0x6F, 0x82, 0x23, 0x83, 0x22 };
        internal static readonly byte[] EliteExtendedBlue =
            { 0x39, 0x38, 0x68, 0x3A, 0x69, 0x2A, 0x6A, 0x2B, 0x6B, 0x2C, 0x6C, 0x2D, 0x6D, 0x2E, 0x5E, 0x5D, 0x2F, 0x5F, 0x72, 0x33, 0x73, 0x32 };

        /// <summary>The direct report of one color channel: report ID 7, 0x16,
        /// the channel, 0xA0, then each key's value at its byte, 264 bytes
        /// (SendDirect, HyperXAlloyEliteController.cpp:442-475,
        /// HyperXAlloyFPSController.cpp:103-136).</summary>
        internal static byte[] ChannelReport(byte channel, int[] colors, int shift)
        {
            var frame = new byte[264];
            frame[0] = 0x07;
            frame[1] = 0x16;
            frame[2] = channel;
            frame[3] = 0xA0;
            for (int i = 0; i < EliteKeyBytes.Length; i++) frame[EliteKeyBytes[i]] = (byte)(colors[i] >> shift);
            return frame;
        }

        /// <summary>The Alloy Elite's extended report: channel 4, the 22
        /// LEDs after the 106 keys at their bytes, from byte 0x08 to 0x93
        /// (SetLEDsDirect and SendDirectExtended,
        /// HyperXAlloyEliteController.cpp:131-180 and 477-509). OpenRGB leaves
        /// the bytes no LED takes as the stack holds them. Here they are
        /// 0.</summary>
        internal static byte[] ExtendedReport(int[] colors)
        {
            var data = new byte[150];
            for (int i = 0; i < 22; i++)
            {
                int color = colors[106 + i];
                data[EliteExtendedRed[i]] = (byte)(color >> 16);
                data[EliteExtendedGreen[i]] = (byte)(color >> 8);
                data[EliteExtendedBlue[i]] = (byte)color;
            }
            var frame = new byte[264];
            frame[0] = 0x07;
            frame[1] = 0x16;
            frame[2] = 0x04;
            frame[3] = 0xA0;
            for (int i = 0x08; i < 0x94; i++) frame[i] = data[i];
            return frame;
        }

        /// <summary>The Alloy Origins Core's color buffer: 380 bytes in
        /// segments of 48, two sectors of six LEDs each, green, red and blue
        /// 16 bytes apart (SetLEDsDirect, HyperXAlloyOriginsCoreController.cpp:115-163),
        /// sent as reports of A2, a sequence number, 00, the length and up
        /// to 60 bytes (SendRGBToDevice, lines 165-190). Output reports.</summary>
        internal static List<byte[]> CoreFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var buffer = new byte[380];
            int count = Math.Min(Math.Min(lights.Count, colors.Count), 94);
            for (int i = 0; i < count; i++)
            {
                int index = lights[i].Address;
                int at = index / 12 * 48 + (index / 6 & 1) * 8 + index % 6;
                if (at + 32 >= buffer.Length) continue;
                buffer[at] = (byte)(colors[i] >> 8);
                buffer[at + 16] = (byte)(colors[i] >> 16);
                buffer[at + 32] = (byte)colors[i];
            }
            var frames = new List<byte[]>();
            int sent = 0, payload = 60, sequence = 0;
            while (sent < buffer.Length)
            {
                if (buffer.Length - sent < payload) payload = buffer.Length - sent;
                var frame = new byte[65];
                frame[1] = 0xA2;
                frame[2] = (byte)sequence++;
                frame[4] = (byte)payload;
                Array.Copy(buffer, sent, frame, 5, payload);
                frames.Add(frame);
                sent += payload;
            }
            return frames;
        }

        /// <summary>The Eve 1800's and Origins 2 65's picture: report 0x44,
        /// 01 04 00 to start, then reports of 0x44, 02, the report's number
        /// (always 0 on the Eve), 00 and up to twenty colors, one black color
        /// in a report with none left (SetLEDsDirect,
        /// HyperXEve1800Controller.cpp:64-121, HyperXOrigins2_65Controller.cpp:51-151).
        /// Output reports.</summary>
        internal static List<byte[]> Report44Frames(int[] colors, int reports, bool numbered)
        {
            var init = new byte[65];
            init[0] = 0x44;
            init[1] = 0x01;
            init[2] = 0x04;
            var frames = new List<byte[]> { init };
            int left = colors.Length, sent = 0;
            for (int p = 0; p < reports; p++)
            {
                var frame = new byte[65];
                frame[0] = 0x44;
                frame[1] = 0x02;
                frame[2] = numbered ? (byte)p : (byte)0;
                int count = left > 20 ? 20 : left;
                if (count == 0) count = 1;
                for (int i = 0; i < count; i++)
                {
                    int color = left > 0 ? colors[sent + i] : 0;
                    frame[4 + i * 3] = (byte)(color >> 16);
                    frame[5 + i * 3] = (byte)(color >> 8);
                    frame[6 + i * 3] = (byte)color;
                }
                if (left > 0)
                {
                    sent += count;
                    left -= count;
                }
                frames.Add(frame);
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        /// <summary>The Alloy Origins Core's variant in an answer to 10, byte
        /// 56 (GetVariant, HyperXAlloyOriginsCoreController.cpp:71-101):
        /// HYPERX_ALLOY_ORIGINS_CORE_ABNT2 (0x10) names the Brazilian layout
        /// and anything else ANSI (RGBController_HyperXAlloyOriginsCore.cpp:155-165).</summary>
        internal static KeyboardLayout CoreRegionOf(byte[] answer)
            => answer != null && answer.Length > 56 && answer[56] == 0x10 ? KeyboardLayout.Abnt2 : KeyboardLayout.AnsiQwerty;

        private static HidLightingSession Begin(IHidReportChannel io, Protocol protocol, ushort productId, string product)
        {
            if (io == null) return null;
            LightMap map = null;
            if (protocol == Protocol.OriginsCore)
            {
                var query = new byte[65];
                query[1] = 0x10;
                var answer = CoolerMasterLighting.Exchange(io, query, required: true, AnswerMs);
                if (answer == null) return null;
                var region = CoreRegionOf(answer);
                if (region != KeyboardLayout.AnsiQwerty)
                    map = new LightMap(Lights(protocol, productId, region), "OpenRGB HyperXKeyboardController")
                    {
                        Product = product,
                    };
            }
            return new Session(protocol, map);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly LightMap _map;

            public Session(Protocol protocol, LightMap map)
            {
                Protocol = protocol;
                _map = map;
            }

            public Protocol Protocol { get; }

            public override LightMap Map => _map;

            /// <summary>The Alloy Elite, the FPS and every Alloy Origins but
            /// the Core take feature reports. The Core, the Eve 1800 and the
            /// Origins 2 65 take output reports (hid_write).</summary>
            public override bool Feature => Protocol is not (Protocol.OriginsCore or Protocol.Eve1800 or Protocol.Origins2_65);

            /// <summary>Nothing hands one of these keyboards back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>The waits OpenRGB makes after a report, in ms: 5 between
            /// the Alloy Elite's channels, 10 between the FPS's
            /// (HyperXAlloyEliteController.cpp:161-179,
            /// HyperXAlloyFPSController.cpp:80-90).</summary>
            internal int GapMs => Protocol switch
            {
                Protocol.Elite => 5,
                Protocol.Fps => 10,
                _ => 0,
            };

            internal List<byte[]> PictureFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                switch (Protocol)
                {
                    case Protocol.Elite2:
                        return Elite2Frames(Ordered(lights, colors, lights.Count));
                    case Protocol.Origins:
                        return OriginsFrames(Ordered(lights, colors, lights.Count), 9, OriginsSkip);
                    case Protocol.Origins60And65:
                        return OriginsFrames(Ordered(lights, colors, lights.Count), 5, null);
                    case Protocol.Elite:
                    {
                        var ordered = Ordered(lights, colors, 128);
                        return new List<byte[]>
                        {
                            ChannelReport(0x01, ordered, 16), ChannelReport(0x02, ordered, 8), ChannelReport(0x03, ordered, 0),
                            ExtendedReport(ordered),
                        };
                    }
                    case Protocol.Fps:
                    {
                        var ordered = Ordered(lights, colors, 106);
                        return new List<byte[]>
                        {
                            ChannelReport(0x01, ordered, 16), ChannelReport(0x02, ordered, 8), ChannelReport(0x03, ordered, 0),
                        };
                    }
                    case Protocol.OriginsCore:
                        return CoreFrames(lights, colors);
                    case Protocol.Eve1800:
                        // SendDirectColorPacket(&colors[0], colors.size()), one report.
                        return Report44Frames(Ordered(lights, colors, lights.Count), 1, false);
                    default:
                        return Report44Frames(Ordered(lights, colors, lights.Count), 4, true);
                }
            }

            private bool Send(IHidReportChannel io, Action<int> wait, List<byte[]> frames)
            {
                if (io == null) return false;
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i > 0 && GapMs > 0) wait(GapMs);
                    if (!(Feature ? io.SetFeature(frames[i]) : io.WriteOutput(frames[i]))) return false;
                }
                return true;
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var frames = PictureFrames(lights, colors);
                return Send(io, wait, frames) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
