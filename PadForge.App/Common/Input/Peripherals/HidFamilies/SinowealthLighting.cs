using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One light of the Genesis Thor 300: OpenRGB's name for it, the
    /// key index its single-LED report takes, and its cell in the 6 by 23
    /// matrix (SinowealthKeyboard90Controller.h:22-144).</summary>
    internal readonly record struct SinowealthLed(string Name, byte Index, byte Row, byte Col);

    /// <summary>
    /// The devices of OpenRGB's SinowealthController that answer on one HID
    /// collection (#557): the Sinowealth keyboards with product ID 0x010C,
    /// the Genesis Thor 300, the ZET Fury Pro and the Glorious Model O and
    /// Model D Wireless on cable or dongle. The Glorious Model O, Model D
    /// and Everest GT-100 and the Genesis Xenon 200 need two collections
    /// each and are lit by <see cref="SinowealthMouseLighting"/>.
    ///
    /// <para>OpenRGB tells the devices that share a product ID apart by the
    /// feature report each answers (SinowealthControllerDetect.cpp:93-255):
    /// the ZET Fury Pro by a report 0x04 of 59 bytes (lines 276-294), the
    /// Xenon 200 on the same product by one of 154 (lines 257-274), and the
    /// 0x010C keyboards by a report 0x06 of 520 (lines 415-443). Windows
    /// gives each collection's longest feature report, so a collection is
    /// matched when one of the family's registrations matches it and its
    /// feature report has the length the detector reads.</para>
    ///
    /// <para>The 0x010C keyboards. The detector sends 06 82 01 00 01 00 06
    /// in a report of 520 bytes, reads report 0x06 back, and takes byte 13
    /// for the model, which picks the keyboard's name and layout
    /// (lines 415-443, SinowealthKeyboard10cDevices.cpp:299-321). A model
    /// it does not know makes no controller. The colors go in one report:
    /// 06 08 00 00 01 00 7A 01, then red, green and blue for each light by
    /// its LED index, then 1 ms (SinowealthKeyboard10cController.cpp:63-84).
    /// The layout gives each key its LED index
    /// (RGBController_SinowealthKeyboard10c.cpp:117-154). The keyboard
    /// leaves this Direct mode when the colors stop, so OpenRGB sends them
    /// again once a second has passed without a picture, looking every half
    /// second (lines 63-69 and 181-191). A session sends its last report
    /// again every second (<see cref="HidLightingSession.Recheck"/>). The
    /// mode is stored nowhere ("@save :x:", lines 21-30), and the hand-back
    /// is to stop sending, after which the keyboard returns to its own
    /// lighting.</para>
    ///
    /// <para>The Genesis Thor 300. It has no Direct mode ("@comment Direct
    /// mode is not supported by the keyboard",
    /// RGBController_SinowealthKeyboard90.cpp:16-25). OpenRGB lights it
    /// per key in its Custom mode, which the keyboard keeps (Custom carries
    /// MODE_FLAG_AUTOMATIC_SAVE, lines 58-66): 0A 03 01, then the mode
    /// report 0A 0A 13 with the brightness, the speed and the color, then
    /// 0A 0C 01 and the key index, red, green and blue for each of the 104
    /// lights, then the same report for key 0x89 to commit, every report an
    /// 8-byte feature report (lines 114-128, SinowealthKeyboard90Controller.cpp:59-119).
    /// The mode report's brightness is Custom's, full by default, and its
    /// speed and color are SendMode's defaults, normal and rainbow
    /// (SinowealthKeyboard90Controller.h:213-219). No reference reads the
    /// keyboard's mode or colors back, so nothing returns it to the effect
    /// it had: the hand-back sends nothing and the keyboard keeps the last
    /// picture in its Custom mode. Since every picture is stored, a picture
    /// is held before it goes out and pictures go out at most twice a
    /// second, the bounds the Lenovo Gen 10 keyboard's stored writes use
    /// (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>The ZET Fury Pro. OpenRGB reads the mouse's whole configuration
    /// (feature report 0x04, 59 bytes), puts the mode, the direction and
    /// the seven colors in, and writes it back, at every picture
    /// (SinowealthController1007.cpp:63-128). The Custom mode is per light,
    /// with the default speed: mode 0x60 + 2, direction 0
    /// (lines 17-28 and 82-98, RGBController_Sinowealth1007.cpp:48-54 and
    /// 232-253). Every mode carries MODE_FLAG_AUTOMATIC_SAVE, so the
    /// configuration is the mouse's stored one. A session reads it before
    /// its first picture and keeps it in <see cref="KeyboardProfileBackups"/>,
    /// on disk before anything is written, and the hand-back writes it back
    /// whole, which returns the mouse's mode and colors. A run that ends
    /// without the hand-back leaves it owed, and the next run writes it
    /// back once the mouse is present and unclaimed.</para>
    ///
    /// <para>The Glorious Model O and Model D Wireless. OpenRGB gives them
    /// no light of their own and colors them through their modes, which the
    /// mouse keeps (RGBController_SinowealthGMOW.cpp:13-20 and 85-96). Its
    /// Static mode is three feature reports of 65 bytes, each after 50 ms:
    /// the mode report with the color, black sent as red 1, then the wired
    /// and the wireless brightness (SinowealthGMOWController.cpp:18-40 and
    /// 100-241). Static's brightness is the middle one, 0x7F
    /// (RGBController_SinowealthGMOW.cpp:85-96), and OpenRGB passes the
    /// mode's place in its list, 4, which is GMOW_MODE_STATIC
    /// (line 189, SinowealthGMOWController.h:20-31). No reference reads the
    /// mouse's mode back (SignalRGB's Glorious_Model_O_Wireless_-_Wired.js
    /// writes it the same way and reads nothing), so the hand-back sends
    /// nothing and the mouse keeps the last color in its Static mode,
    /// written as the Thor 300's pictures are.</para>
    ///
    /// <para>No program is named for these devices: no source names the
    /// processes of Glorious CORE, Genesis's or ZET's software.</para>
    /// </summary>
    internal sealed class SinowealthLighting : IHidLightingFamily
    {
        public static readonly SinowealthLighting Instance = new();

        internal const string FamilyName = "SinowealthController";
        internal const ushort VendorId = 0x258A;

        internal const string ZetDetector = "DetectZetFuryPro";
        internal const string GmowCableDetector = "DetectGMOW_Cable";
        internal const string GmowDongleDetector = "DetectGMOW_Dongle";
        internal const string Thor300Detector = "DetectSinowealthGenesisKeyboard";
        internal const string Keyboard10cDetector = "DetectSinowealthKeyboard10c";

        /// <summary>The feature report lengths the detectors read
        /// (SinowealthControllerDetect.cpp:280 and 418-419).</summary>
        internal const int ZetReportLength = 59;
        internal const int Keyboard10cReportLength = 520;

        /// <summary>The bounds of a device whose every picture is stored:
        /// the hold and the spacing the Lenovo Gen 10 keyboard's stored
        /// writes use.</summary>
        internal const int StoredHoldMs = LenovoLegionLighting.HoldMs;
        internal const int StoredRepaintMs = LenovoLegionLighting.RepaintMs;

        /// <summary>The shortest time between two pictures of a device in a
        /// mode that is stored nowhere. No reference bounds it.</summary>
        internal const int RepaintMs = 33;

        /// <summary>How often a 0x010C keyboard's last picture goes again,
        /// OpenRGB's keepalive: after a second with no picture
        /// (RGBController_SinowealthKeyboard10c.cpp:185).</summary>
        internal const int KeepaliveMs = 1000;

        /// <summary>How often a picture that waits for its backup to reach
        /// the settings file looks again.</summary>
        internal const int SavingMs = 100;

        /// <summary>How many times one run tries to hand back what an
        /// earlier run left owed.</summary>
        internal const int RepairTries = 3;

        public string Name => "Sinowealth";

        private readonly Dictionary<(ushort, string), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            foreach (var registration in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                string detector = registration.Detector;
                bool fits = detector switch
                {
                    ZetDetector => collection.FeatureReportLength == ZetReportLength,
                    Keyboard10cDetector => collection.FeatureReportLength == Keyboard10cReportLength,
                    GmowCableDetector or GmowDongleDetector or Thor300Detector => true,
                    _ => false,
                };
                if (!fits) continue;
                lock (_models)
                {
                    var key = (collection.ProductId, detector);
                    if (_models.TryGetValue(key, out var model)) return model;
                    model = Model(registration.Name, collection.ProductId, detector);
                    if (model != null) _models[key] = model;
                    return model;
                }
            }
            return null;
        }

        internal static string Product(ushort productId) => $"sinowealth:{VendorId:X4}:{productId:X4}";

        private HidLightingModel Model(string name, ushort productId, string detector)
        {
            string product = Product(productId);
            switch (detector)
            {
                case Keyboard10cDetector:
                    return new HidLightingModel
                    {
                        Family = this, Name = name,
                        Map = new LightMap(Array.Empty<LightInfo>(), "OpenRGB SinowealthKeyboard10cDevices.cpp") { Product = product },
                        RepaintMs = RepaintMs, RecheckMs = KeepaliveMs,
                        Begin = (io, wait) => Keyboard10cSession.Begin(io, product),
                    };
                case Thor300Detector:
                    return new HidLightingModel
                    {
                        Family = this, Name = name,
                        Map = new LightMap(Thor300Lights(), "OpenRGB SinowealthKeyboard90Controller.h") { Product = product },
                        HoldMs = StoredHoldMs, RepaintMs = StoredRepaintMs,
                        Begin = (io, wait) => new Thor300Session(),
                    };
                case ZetDetector:
                    return new HidLightingModel
                    {
                        Family = this, Name = name,
                        Map = new LightMap(ZetLights(), "OpenRGB RGBController_Sinowealth1007.cpp") { Product = product },
                        HoldMs = StoredHoldMs, RepaintMs = StoredRepaintMs,
                        Begin = (io, wait) => ZetSession.Begin(io, product),
                        Owed = () => KeyboardProfileBackups.Get(product) != null
                                     && KeyboardProfileBackups.Failures(product) < RepairTries,
                        Repair = (io, _) => ZetSession.Repair(io, product),
                    };
                default:
                    return new HidLightingModel
                    {
                        Family = this, Name = name,
                        Map = new LightMap(OneLight(), "OpenRGB RGBController_SinowealthGMOW.cpp") { Product = product },
                        HoldMs = StoredHoldMs, RepaintMs = StoredRepaintMs,
                        Begin = (io, wait) => new GmowSession(),
                    };
            }
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>A device OpenRGB colors as a whole: one zone.</summary>
        internal static IReadOnlyList<LightInfo> OneLight()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        /// <summary>The ZET Fury Pro's seven lights, zones 1 to 7 in OpenRGB's
        /// order, each its place in the color run.</summary>
        internal static IReadOnlyList<LightInfo> ZetLights()
        {
            var lights = new List<LightInfo>(SinowealthLayouts.ZetFuryPro.Length);
            for (int i = 0; i < SinowealthLayouts.ZetFuryPro.Length; i++)
                lights.Add(new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 });
            return lights;
        }

        /// <summary>A light named by OpenRGB: a key where the name has a
        /// usage, else another light with the name's label, as the layouts'
        /// lights are (KeyboardLayoutManager.Lights).</summary>
        internal static LightInfo Named(int address, string name)
        {
            OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number);
            return usage != 0
                ? new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated)
                : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
        }

        /// <summary>The Thor 300's 104 lights, each addressed by the key index
        /// its report takes and placed at its matrix cell.</summary>
        internal static IReadOnlyList<LightInfo> Thor300Lights()
        {
            var lights = new List<LightInfo>(SinowealthLayouts.Thor300.Length);
            foreach (var led in SinowealthLayouts.Thor300)
                lights.Add(Named(led.Index, led.Name) with { X = led.Col, Y = led.Row, Width = 1, Height = 1, HasPosition = true });
            return lights;
        }

        /// <summary>The model's row of a 0x010C keyboard's model byte, its
        /// recipe and name, or null for one OpenRGB does not know
        /// (SinowealthKeyboard10cDevices.cpp:299-321).</summary>
        internal static KeyboardLayoutModel Keyboard10cModel(byte modelId)
        {
            string key = $"0x{modelId:X2}";
            foreach (var model in KeyboardLayouts.Models)
                if (model.Family == "Sinowealth" && model.ProductId == -1 && model.Key == key)
                    return model;
            return null;
        }

        /// <summary>A 0x010C keyboard's lights: its recipe built ANSI, each
        /// key addressed by its LED index, and a key with no index left out,
        /// as OpenRGB skips its cell (RGBController_SinowealthKeyboard10c.cpp:96-154).</summary>
        internal static IReadOnlyList<LightInfo> Keyboard10cLights(KeyboardLayoutModel model)
        {
            var lights = new List<LightInfo>();
            foreach (var light in KeyboardLayouts.Build(model.Recipe, KeyboardLayout.AnsiQwerty)
                         .Lights((_, key) => key.Value == KeyboardLayoutManager.NoKey ? -1 : (int)key.Value))
                if (light.Address >= 0) lights.Add(light);
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The 0x010C keyboards
        // ─────────────────────────────────────────────

        /// <summary>The detector's model query, 520 bytes
        /// (SinowealthControllerDetect.cpp:418).</summary>
        internal static byte[] ModelQuery()
        {
            var report = new byte[Keyboard10cReportLength];
            report[0] = 0x06;
            report[1] = 0x82;
            report[2] = 0x01;
            report[4] = 0x01;
            report[6] = 0x06;
            return report;
        }

        /// <summary>The colors report, SetLEDsDirect
        /// (SinowealthKeyboard10cController.cpp:63-84). A light whose index
        /// is past the report goes nowhere.</summary>
        internal static byte[] Keyboard10cFrame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var report = new byte[Keyboard10cReportLength];
            report[0] = 0x06;
            report[1] = 0x08;
            report[4] = 0x01;
            report[6] = 0x7A;
            report[7] = 0x01;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = 8 + lights[n].Address * 3;
                if (lights[n].Address < 0 || at + 2 >= report.Length) continue;
                int rgb = colors[n];
                report[at] = (byte)(rgb >> 16);
                report[at + 1] = (byte)(rgb >> 8);
                report[at + 2] = (byte)rgb;
            }
            return report;
        }

        /// <summary>The wait after the colors report (line 83).</summary>
        internal const int Keyboard10cWaitMs = 1;

        internal sealed class Keyboard10cSession : HidLightingSession
        {
            private byte[] _last;

            private Keyboard10cSession(byte modelId, LightMap map)
            {
                ModelId = modelId;
                Map = map;
            }

            public byte ModelId { get; }

            /// <summary>The keyboard's own layout, read from its model byte.</summary>
            public override LightMap Map { get; }

            public override bool Feature => true;

            /// <summary>Nothing: the keyboard leaves Direct mode on its own
            /// once the reports stop.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            /// <summary>Asks the model byte as the detector does and starts a
            /// session on the layout it names, or answers null for no answer
            /// or a model OpenRGB does not know.</summary>
            public static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                if (!io.SetFeature(ModelQuery())) return null;
                var answer = new byte[Math.Max(Keyboard10cReportLength, io.FeatureReportLength)];
                if (!io.GetFeature(0x06, answer)) return null;
                byte modelId = answer[13];
                var model = Keyboard10cModel(modelId);
                if (model == null) return null;
                string mapProduct = $"{product}:{modelId:X2}";
                var map = new LightMap(Keyboard10cLights(model), $"OpenRGB {model.Recipe}") { Product = mapProduct };
                return new Keyboard10cSession(modelId, map);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var report = Keyboard10cFrame(lights, colors);
                if (!io.SetFeature(report)) return HidLightingPaint.Failed;
                wait(Keyboard10cWaitMs);
                _last = report;
                return HidLightingPaint.Sent;
            }

            /// <summary>The keepalive: the last picture again.</summary>
            public override bool Recheck(IHidReportChannel io, Action<int> wait)
            {
                if (_last == null) return true;
                if (!io.SetFeature(_last)) return false;
                wait(Keyboard10cWaitMs);
                return true;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  The Genesis Thor 300
        // ─────────────────────────────────────────────

        /// <summary>The Custom mode and SendMode's defaults
        /// (SinowealthKeyboard90Controller.h:146-198 and 213-219), and the
        /// commit key (SinowealthKeyboard90Controller.cpp:116-119).</summary>
        internal const byte Thor300ModeCustom = 0x13;
        internal const byte Thor300BrightnessFull = 0x04;
        internal const byte Thor300SpeedNormal = 0x02;
        internal const byte Thor300ColorRainbow = 0x07;
        internal const byte Thor300CommitKey = 0x89;

        /// <summary>SendFeatureReport: report 0x0A, the command and five
        /// arguments, 8 bytes (SinowealthKeyboard90Controller.cpp:59-91).</summary>
        internal static byte[] Thor300Report(byte cmd, byte a1 = 0, byte a2 = 0, byte a3 = 0, byte a4 = 0, byte a5 = 0)
            => new byte[] { 0x0A, cmd, a1, a2, a3, a4, a5, 0x00 };

        /// <summary>A picture's reports, DeviceUpdateLEDs
        /// (RGBController_SinowealthKeyboard90.cpp:114-128).</summary>
        internal static List<byte[]> Thor300Frames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>
            {
                Thor300Report(0x03, 0x01),
                Thor300Report(0x0A, Thor300ModeCustom, Thor300BrightnessFull, Thor300SpeedNormal, Thor300ColorRainbow),
            };
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int rgb = colors[n];
                frames.Add(Thor300Report(0x0C, 0x01, (byte)lights[n].Address, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            }
            frames.Add(Thor300Report(0x0C, 0x01, Thor300CommitKey));
            return frames;
        }

        internal sealed class Thor300Session : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing: no reference returns the keyboard to its
            /// own effect.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Thor300Frames(lights, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  The ZET Fury Pro
        // ─────────────────────────────────────────────

        /// <summary>The Custom mode with the default speed and the right
        /// direction (SinowealthController1007.h:25-55).</summary>
        internal const byte ZetModeCustomDefault = 0x60 + 2;
        internal const byte ZetDirectionRight = 0x00;

        /// <summary>A configuration with the Custom mode and seven colors,
        /// SendPacket (SinowealthController1007.cpp:63-112): byte 21 the
        /// mode, 22 the direction, 23 to 43 red, green and blue per
        /// light.</summary>
        internal static byte[] ZetFrame(byte[] configuration, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var report = new byte[ZetReportLength];
            Array.Copy(configuration, report, Math.Min(configuration.Length, ZetReportLength));
            report[0] = 0x04;
            report[21] = ZetModeCustomDefault;
            report[22] = ZetDirectionRight;
            Array.Clear(report, 23, 21);
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int i = lights[n].Address;
                if ((uint)i >= 7) continue;
                int rgb = colors[n];
                report[23 + i * 3] = (byte)(rgb >> 16);
                report[24 + i * 3] = (byte)(rgb >> 8);
                report[25 + i * 3] = (byte)rgb;
            }
            return report;
        }

        /// <summary>The configuration, GetProfile: feature report 0x04 of
        /// 59 bytes (SinowealthController1007.cpp:114-128), or null.</summary>
        internal static byte[] ReadZetConfiguration(IHidReportChannel io)
        {
            var answer = new byte[Math.Max(ZetReportLength, io.FeatureReportLength)];
            if (!io.GetFeature(0x04, answer)) return null;
            var configuration = new byte[ZetReportLength];
            Array.Copy(answer, configuration, ZetReportLength);
            configuration[0] = 0x04;
            return configuration;
        }

        internal sealed class ZetSession : HidLightingSession
        {
            private readonly string _product;
            private readonly byte[][] _crash;

            private ZetSession(string product, byte[] original)
            {
                _product = product;
                Original = original;
                _crash = new[] { original };
            }

            /// <summary>The configuration the mouse had before PadForge wrote
            /// any: an earlier run's backup where one is owed, else the one
            /// read now.</summary>
            public byte[] Original { get; }

            public override bool Feature => true;

            public override byte[][] CrashFrames => KeyboardProfileBackups.Get(_product) != null ? _crash : Array.Empty<byte[]>();

            public static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                var read = ReadZetConfiguration(io);
                if (read == null) return null;
                var owed = KeyboardProfileBackups.Get(product);
                var original = owed is { } backup && backup.Contents.Length == ZetReportLength ? backup.Contents : read;
                return new ZetSession(product, original);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (KeyboardProfileBackups.Get(_product) == null)
                    KeyboardProfileBackups.Take(_product, 0, Original);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                // OpenRGB reads the configuration again before each write
                // (SinowealthController1007.cpp:102).
                var configuration = ReadZetConfiguration(io);
                if (configuration == null) return HidLightingPaint.Failed;
                return io.SetFeature(ZetFrame(configuration, lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (KeyboardProfileBackups.Get(_product) == null) return true;
                if (!io.SetFeature(Original)) return false;
                KeyboardProfileBackups.Drop(_product);
                return true;
            }

            /// <summary>Writes back what an earlier run left owed. True when
            /// nothing is owed any more.</summary>
            public static bool Repair(IHidReportChannel io, string product)
            {
                var owed = KeyboardProfileBackups.Get(product);
                if (owed == null) return true;
                if (owed.Value.Contents.Length == ZetReportLength && io.SetFeature(owed.Value.Contents))
                {
                    KeyboardProfileBackups.Drop(product);
                    return true;
                }
                KeyboardProfileBackups.NoteFailure(product);
                return false;
            }
        }

        // ─────────────────────────────────────────────
        //  The Glorious Model O and Model D Wireless
        // ─────────────────────────────────────────────

        /// <summary>GMOW_PACKET_SIZE, the mode and the brightness
        /// (SinowealthGMOWController.h:18-50).</summary>
        internal const int GmowPacketSize = 65;
        internal const byte GmowModeStatic = 0x04;
        internal const byte GmowBrightnessMid = 0x7F;

        /// <summary>The wait before each report (SinowealthGMOWController.cpp:231-238).</summary>
        internal const int GmowWaitMs = 50;

        /// <summary>SetMode for the Static mode: the mode report, the wired
        /// brightness and the wireless brightness
        /// (SinowealthGMOWController.cpp:18-40, 100-121 and 156-174).</summary>
        internal static byte[][] GmowFrames(int rgb)
        {
            var mode = new byte[GmowPacketSize];
            mode[0x03] = 0x02;
            mode[0x05] = 0x02;
            mode[0x07] = 0x01;
            mode[0x08] = 0xFF;
            mode[0x09] = GmowModeStatic;
            mode[0x04] = 0x05;
            mode[0x0B] = 0x09;
            byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
            if (r == 0 && g == 0 && b == 0) r = 1;
            mode[0x0C] = r;
            mode[0x0D] = g;
            mode[0x0E] = b;

            var wired = new byte[GmowPacketSize];
            wired[0x03] = 0x02;
            wired[0x04] = 0x02;
            wired[0x05] = 0x02;
            wired[0x06] = 0x02;
            wired[0x07] = 0x01;
            wired[0x08] = GmowBrightnessMid;

            var wireless = (byte[])wired.Clone();
            wireless[0x07] = 0x00;
            return new[] { mode, wired, wireless };
        }

        internal sealed class GmowSession : HidLightingSession
        {
            public override bool Feature => true;

            /// <summary>Nothing: no reference reads the mouse's mode back.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int rgb = colors != null && colors.Count > 0 ? colors[0] : 0;
                foreach (var frame in GmowFrames(rgb))
                {
                    wait(GmowWaitMs);
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
