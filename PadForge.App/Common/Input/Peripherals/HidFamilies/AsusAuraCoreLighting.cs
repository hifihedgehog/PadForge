using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Management;
using System.Threading;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's AsusAuraCoreController family (#557): the keyboards of ASUS
    /// laptops lit through the Aura Core controller, by its two
    /// sub-controllers, all under Controllers/AsusAuraCoreController at the
    /// pinned commit.
    ///
    /// <para>Aura Core (AsusAuraCoreController, 0B05:1854, 1866 and 1869 on
    /// any collection). OpenRGB tells the kind by which feature report the
    /// collection answers: 5D is a four-zone keyboard, and 5E the GA15DH's
    /// strip of twenty LEDs (IdentifyDevice, AsusAuraCoreController.cpp:290-344).
    /// A collection that answers neither gets no controller
    /// (AsusAuraCoreControllerDetect.cpp:19-38), and one with no feature
    /// report at all cannot answer, so it is not matched. The session asks
    /// the same two reads. The GA15DH then takes the handshake, "ASUS
    /// Tech.Inc." and 05 20 31 00 10 each with a read after (lines 346-410),
    /// and its Direct mode, BC D0 and one frame per sixteen LEDs (lines
    /// 179-224), and is lit with BC D0 frames of sixteen LEDs (lines 226-288).
    /// It writes nothing to the device's memory: asusctl writes its per-key
    /// BC frames the same way, with no apply after (asusd
    /// src/aura_laptop/mod.rs:179-205).</para>
    ///
    /// <para>The four-zone keyboard has no Direct mode. OpenRGB colors each
    /// zone with its Static mode, B3 with the zone from 1, speed FF and
    /// direction 0, then B5 and B4 (RGBController_AsusAuraCore.cpp:223-309,
    /// AsusAuraCoreController.cpp:86-177). B4 makes the setting stay: "Changes
    /// won't persist unless apply is set" (asusd src/aura_laptop/mod.rs:118-125),
    /// so every color is written into the keyboard's memory. A picture goes
    /// out once it has held for 150 ms and no sooner than 500 ms after the one
    /// before, the bounds of <see cref="LenovoLegionLighting"/>, whose writes
    /// land in the keyboard's memory too, and a picture that never holds
    /// leaves the keyboard on the last one that did. No reference reads the
    /// mode back, so nothing is put back: the keyboard keeps PadForge's last
    /// colors until Armoury Crate or G-Helper sets its lighting again.</para>
    ///
    /// <para>Aura Core laptops (AsusAuraCoreLaptopController, the Strix SCAR
    /// 15 and 17 collections FF31:79). OpenRGB knows a laptop by its
    /// mainboard name (Win32_BaseBoard Product, dmiinfo.cpp:31-36), and a
    /// laptop not in its list gets no controller
    /// (AsusAuraCoreLaptopController.cpp:34-72). Its zones are the generated
    /// layouts (<see cref="KeyboardLayouts"/>, family "AsusAuraCore"), built
    /// in ISO where the laptop's answer to 05 20 31 00 10 says 2 at byte 12
    /// and in ANSI otherwise (lines 99-139, RGBController_AsusAuraCoreLaptop.cpp:288-300),
    /// each light at its LED value. The session sends what OpenRGB's Direct
    /// mode sends: BC with 00, or 01 on a one-zone keyboard, then the
    /// brightness at its maximum, 3, with BA C5 C4, and BC again
    /// (AsusAuraCoreLaptopController.cpp:161-209 and 304-313,
    /// RGBController_AsusAuraCoreLaptop.cpp:53-61 and 271). It leaves out the
    /// power settings OpenRGB writes from its own settings file
    /// (AsusAuraCoreLaptopController.cpp:376-459), which are the laptop's
    /// stored settings and not lighting. A picture is the BC frames of
    /// SetLedsDirect: 167 keys sixteen to a frame, then one frame of the LEDs
    /// past them (lines 211-302), in a buffer as long as OpenRGB's
    /// (RGBController_AsusAuraCoreLaptop.cpp:285-395). OpenRGB reads past its
    /// buffer where a laptop has fewer than 167 LEDs, and PadForge sends
    /// zeros there. Nothing is written to the laptop's memory, as with the
    /// GA15DH. No reference returns these laptops to their own lighting while
    /// they run, so the keyboard keeps PadForge's last picture, at brightness
    /// 3, until Armoury Crate or G-Helper sets it again or the laptop
    /// restarts.</para>
    ///
    /// <para>The SCAR 17's collection, 1866 FF31:79, is a candidate of both
    /// detectors, and OpenRGB tries the Aura Core one first, which takes any
    /// collection that answers report 5D. The session asks it the same way
    /// and lights it as a laptop only when it answers neither 5D nor 5E.</para>
    ///
    /// <para>Armoury Crate lights these keyboards, and while it runs each is
    /// left to it (<see cref="VendorPrograms.ArmouryCrate"/>).</para>
    /// </summary>
    internal sealed class AsusAuraCoreLighting : IHidLightingFamily
    {
        public static readonly AsusAuraCoreLighting Instance = new();

        internal const string OpenRgbFamily = "AsusAuraCoreController";
        internal const ushort VendorId = 0x0B05;

        /// <summary>The report IDs of the two Aura Core kinds and of the
        /// laptops (AsusAuraCoreController.cpp:303 and 326,
        /// AsusAuraCoreLaptopController.h:73).</summary>
        internal const byte KeyboardReport = 0x5D, Ga15dhReport = 0x5E;

        /// <summary>The frame lengths (AsusAuraCoreController.cpp:315 and 337,
        /// AsusAuraCoreLaptopController.h:21).</summary>
        internal const int KeyboardLength = 17, Ga15dhLength = 64, LaptopLength = 64;

        /// <summary>The commands (AsusAuraCoreController.h:36-43).</summary>
        internal const byte Update = 0xB3, Apply = 0xB4, Set = 0xB5, Brightness = 0xBA, Direct = 0xBC;

        internal const int KeyboardZones = 4, Ga15dhLeds = 20, Ga15dhLedsPerFrame = 16;

        /// <summary>SetLedsDirect's key set and LEDs per frame
        /// (AsusAuraCoreLaptopController.cpp:218 and 239).</summary>
        internal const int LaptopKeySet = 167, LaptopLedsPerFrame = 16;

        /// <summary>ASUSAURACORELAPTOP_LAYOUT_ISO (AsusAuraCoreLaptopController.h:84).</summary>
        internal const byte LaptopIso = 0x02;

        /// <summary>The brightness OpenRGB's Direct mode sets
        /// (ASUSAURACORELAPTOP_BRIGHTNESS_MAX, AsusAuraCoreLaptopController.h:30).</summary>
        internal const byte LaptopBrightness = 3;

        /// <summary>Set by the test assembly for the life of its process, never
        /// by the app: the mainboard name is then not read.</summary>
        internal static bool OffLimitsForTest;

        public string Name => "ASUS Aura Core";

        private readonly Func<string> _mainboard;
        private string _board;
        private int _boardRead;
        private readonly ConcurrentDictionary<string, HidLightingModel> _models = new(StringComparer.Ordinal);

        public AsusAuraCoreLighting()
            : this(null) { }

        /// <summary>Test seam: the mainboard name read.</summary>
        internal AsusAuraCoreLighting(Func<string> mainboard) => _mainboard = mainboard ?? MainboardName;

        /// <summary>The mainboard's name as OpenRGB's DMIInfo reads it on
        /// Windows, Product of Win32_BaseBoard (dmiinfo.cpp:31-34), or null.
        /// A read.</summary>
        internal static string MainboardName()
        {
            if (OffLimitsForTest) return null;
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
                using var results = searcher.Get();
                foreach (ManagementBaseObject row in results)
                    using (row)
                        return row["Product"]?.ToString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The mainboard name, read once per family.</summary>
        internal string Board()
        {
            if (Volatile.Read(ref _boardRead) != 0) return Volatile.Read(ref _board);
            string board = _mainboard();
            Volatile.Write(ref _board, board);
            Volatile.Write(ref _boardRead, 1);
            return board;
        }

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var found = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (found.Count == 0) return null;
            // Both kinds read and write feature reports.
            if (collection.FeatureReportLength == 0) return null;
            var first = found[0];
            if (first.Detector == "DetectAsusAuraCoreControllers")
            {
                bool laptop = found.Exists(r => r.Detector == "DetectAsusAuraCoreLaptopControllers");
                var rows = laptop ? LaptopRows(Board()) : null;
                return _models.GetOrAdd($"core:{collection.ProductId:X4}:{rows?.Count ?? 0}",
                    _ => CoreModel(first.Name, collection.ProductId, rows));
            }
            if (first.Detector != "DetectAsusAuraCoreLaptopControllers") return null;
            var laptopRows = LaptopRows(Board());
            if (laptopRows == null) return null;
            return _models.GetOrAdd($"laptop:{collection.ProductId:X4}",
                _ => LaptopModel(first.Name, collection.ProductId, laptopRows));
        }

        private HidLightingModel Model(string name, LightMap map, int holdMs, int repaintMs,
            Func<IHidReportChannel, Action<int>, HidLightingSession> begin)
            => new()
            {
                Family = this,
                Name = name,
                Map = map,
                Programs = new[] { VendorPrograms.ArmouryCrate },
                HoldMs = holdMs,
                RepaintMs = repaintMs,
                Begin = begin,
            };

        private static string ProductKey(int productId, string detail)
            => $"asus:{VendorId:X4}:{productId:X4}:{detail}";

        // ─────────────────────────────────────────────
        //  Aura Core
        // ─────────────────────────────────────────────

        /// <summary>The four zones of the keyboard kind
        /// (RGBController_AsusAuraCore.cpp:184-191).</summary>
        internal static LightMap KeyboardMap(int productId)
        {
            var lights = new LightInfo[KeyboardZones];
            for (int i = 0; i < KeyboardZones; i++)
                lights[i] = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None) { Zone = i + 1 };
            return new LightMap(lights, "OpenRGB RGBController_AsusAuraCore.cpp") { Product = ProductKey(productId, "4zone") };
        }

        /// <summary>The GA15DH's twenty LEDs (RGBController_AsusAuraCore.cpp:192-199),
        /// a strip.</summary>
        internal static LightMap Ga15dhMap(int productId)
        {
            var lights = new LightInfo[Ga15dhLeds];
            for (int i = 0; i < Ga15dhLeds; i++) lights[i] = new LightInfo(i, LightKind.Edge, 0, LightKeySource.None);
            return new LightMap(lights, "OpenRGB RGBController_AsusAuraCore.cpp") { Product = ProductKey(productId, "ga15dh") };
        }

        /// <summary>The model of an Aura Core collection. Its map is the
        /// four-zone keyboard's until a session tells the kind. A collection
        /// the laptop detector also takes falls back to the laptop with
        /// <paramref name="laptopRows"/> where it answers neither report.</summary>
        private HidLightingModel CoreModel(string name, int productId, IReadOnlyList<KeyboardLayoutModel> laptopRows)
            => Model(name, KeyboardMap(productId), LenovoLegionLighting.HoldMs, LenovoLegionLighting.RepaintMs,
                (io, wait) => BeginCore(io, wait, productId, laptopRows));

        /// <summary>IdentifyDevice's reads, then the kind's session.</summary>
        internal static HidLightingSession BeginCore(IHidReportChannel io, Action<int> wait, int productId,
            IReadOnlyList<KeyboardLayoutModel> laptopRows)
        {
            if (io == null) return null;
            var buffer = new byte[Math.Max(64, io.FeatureReportLength)];
            if (io.GetFeature(KeyboardReport, buffer)) return new KeyboardSession(productId);
            if (io.GetFeature(Ga15dhReport, buffer))
            {
                foreach (var frame in Ga15dhHandshake())
                {
                    if (!io.SetFeature(frame)) return null;
                    io.GetFeature(Ga15dhReport, buffer);
                }
                foreach (var frame in Ga15dhInit())
                    if (!io.SetFeature(frame)) return null;
                return new Ga15dhSession(productId);
            }
            return laptopRows != null ? BeginLaptop(io, laptopRows, productId) : null;
        }

        /// <summary>SendUpdate of the keyboard kind (AsusAuraCoreController.cpp:86-129):
        /// the zone from 1, the mode, the color, the speed and the
        /// direction.</summary>
        internal static byte[] KeyboardUpdateFrame(int zone, int rgb)
        {
            var frame = new byte[KeyboardLength];
            frame[0] = KeyboardReport;
            frame[1] = Update;
            frame[2] = (byte)(zone + 1);
            frame[3] = 0x00;
            AsusAuraUsbLighting.Put(frame, 4, rgb);
            frame[7] = 0xFF;
            frame[8] = 0x00;
            return frame;
        }

        internal static byte[] KeyboardCommand(byte command)
        {
            var frame = new byte[KeyboardLength];
            frame[0] = KeyboardReport;
            frame[1] = command;
            return frame;
        }

        /// <summary>DeviceUpdateSingleLED for each zone in order:
        /// SendUpdate, SendSet, SendApply (RGBController_AsusAuraCore.cpp:248-309).</summary>
        internal static List<byte[]> KeyboardFrames(IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            for (int zone = 0; zone < Math.Min(KeyboardZones, colors?.Count ?? 0); zone++)
            {
                frames.Add(KeyboardUpdateFrame(zone, colors[zone]));
                frames.Add(KeyboardCommand(Set));
                frames.Add(KeyboardCommand(Apply));
            }
            return frames;
        }

        internal sealed class KeyboardSession : HidLightingSession
        {
            private readonly LightMap _map;

            public KeyboardSession(int productId) => _map = KeyboardMap(productId);

            public override bool Feature => true;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in KeyboardFrames(colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        /// <summary>SendIdString and SendQuery (AsusAuraCoreController.cpp:361-410):
        /// "ASUS Tech.Inc." with its closing zero, then 05 20 31 00 10.</summary>
        internal static List<byte[]> Ga15dhHandshake()
        {
            var id = new byte[Ga15dhLength];
            id[0] = Ga15dhReport;
            System.Text.Encoding.ASCII.GetBytes("ASUS Tech.Inc.").CopyTo(id, 1);
            var query = new byte[Ga15dhLength];
            query[0] = Ga15dhReport;
            query[1] = 0x05;
            query[2] = 0x20;
            query[3] = 0x31;
            query[4] = 0x00;
            query[5] = 0x10;
            return new List<byte[]> { id, query };
        }

        /// <summary>InitDirectMode (AsusAuraCoreController.cpp:179-224): BC D0,
        /// then the same frame with 01 02 00, the frame number and the LED
        /// count of each frame of sixteen.</summary>
        internal static List<byte[]> Ga15dhInit()
        {
            var frames = new List<byte[]>();
            var frame = new byte[Ga15dhLength];
            frame[0] = Ga15dhReport;
            frame[1] = Direct;
            frame[2] = 0xD0;
            frames.Add((byte[])frame.Clone());
            int left = Ga15dhLeds;
            byte number = 0;
            while (left > 0)
            {
                frame[3] = 0x01;
                frame[4] = 0x02;
                frame[5] = 0x00;
                frame[6] = number++;
                frame[7] = (byte)Math.Min(left, Ga15dhLedsPerFrame);
                frame[8] = 0x00;
                frames.Add((byte[])frame.Clone());
                left -= Ga15dhLedsPerFrame;
            }
            return frames;
        }

        /// <summary>UpdateDirect (AsusAuraCoreController.cpp:226-288): BC D0 01
        /// 02 00, the frame number and its LED count, then the colors from
        /// byte 9, black past the colors given.</summary>
        internal static List<byte[]> Ga15dhFrames(IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int left = Ga15dhLeds, index = 0;
            byte number = 0;
            while (left > 0)
            {
                var frame = new byte[Ga15dhLength];
                frame[0] = Ga15dhReport;
                frame[1] = Direct;
                frame[2] = 0xD0;
                frame[3] = 0x01;
                frame[4] = 0x02;
                frame[5] = 0x00;
                frame[6] = number++;
                frame[7] = (byte)Math.Min(left, Ga15dhLedsPerFrame);
                int at = 9, set = 0;
                while (at < Ga15dhLength && left > 0 && set < Ga15dhLedsPerFrame)
                {
                    int rgb = colors != null && index < colors.Count ? colors[index] : 0;
                    AsusAuraUsbLighting.Put(frame, at, rgb);
                    at += 3;
                    left--;
                    index++;
                    set++;
                }
                frames.Add(frame);
            }
            return frames;
        }

        internal sealed class Ga15dhSession : HidLightingSession
        {
            private readonly LightMap _map;

            public Ga15dhSession(int productId) => _map = Ga15dhMap(productId);

            public override bool Feature => true;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in Ga15dhFrames(colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }

        // ─────────────────────────────────────────────
        //  Aura Core laptops
        // ─────────────────────────────────────────────

        /// <summary>The generated rows of a laptop by its mainboard name, one
        /// per zone with a layout, in the device list's order, or null for a
        /// laptop OpenRGB does not list.</summary>
        internal static List<KeyboardLayoutModel> LaptopRows(string board)
        {
            if (string.IsNullOrEmpty(board)) return null;
            List<KeyboardLayoutModel> rows = null;
            foreach (var model in KeyboardLayouts.Models)
                if (model.Family == "AsusAuraCore" && model.Key == board)
                    (rows ??= new List<KeyboardLayoutModel>()).Add(model);
            return rows;
        }

        /// <summary>A laptop's lights in a region, zone by zone, each key at
        /// its LED value. A light that is no key is a logo light by the name
        /// "Logo", an edge light in the lightbar or the lid, a zone where a
        /// zone is lit as one ("Keyboard"), and any other light another
        /// light. <paramref name="bufferLength"/> is the length of OpenRGB's
        /// buffer: the highest LED value plus one for each zone with a
        /// layout, counted zone by zone (RGBController_AsusAuraCoreLaptop.cpp:306-395).</summary>
        internal static LightMap LaptopMap(int productId, IReadOnlyList<KeyboardLayoutModel> rows, KeyboardLayout layout,
            out int bufferLength)
        {
            var lights = new List<LightInfo>();
            uint max = 0;
            foreach (var row in rows)
            {
                var manager = KeyboardLayouts.Build(row.Recipe, layout);
                if (manager != null)
                {
                    var built = manager.Lights((i, key) => (int)key.Value);
                    for (int i = 0; i < built.Count; i++)
                    {
                        var light = built[i];
                        max = Math.Max(max, (uint)light.Address);
                        // Only the keyboard's own cells place a light: each
                        // zone counts its rows and columns from 0.
                        if (row.Zone != "Keyboard")
                            light = light with { X = 0, Y = 0, Width = 0, Height = 0, HasPosition = false };
                        lights.Add(LaptopLight(light, manager.GetKeyNameAt(i), row.Zone));
                    }
                }
                max++;
            }
            bufferLength = (int)max;
            return new LightMap(lights, "OpenRGB AsusAuraCoreLaptopDevices.cpp")
            {
                Product = ProductKey(productId, rows.Count > 0 ? rows[0].Key + ":" + layout : layout.ToString()),
            };
        }

        internal static LightInfo LaptopLight(LightInfo light, string name, string zone)
        {
            if (light.Kind == LightKind.Key || light.Label != LightLabel.None) return light;
            var bare = light with { Kind = LightKind.Other };
            if (name == "Logo") return bare with { Kind = LightKind.Logo };
            if (zone == "Lightbar" || zone == "Lid") return bare with { Kind = LightKind.Edge };
            if (name == "Keyboard") return bare with { Kind = LightKind.Zone, Zone = 1 };
            return bare;
        }

        private HidLightingModel LaptopModel(string name, int productId, IReadOnlyList<KeyboardLayoutModel> rows)
            => Model(name, LaptopMap(productId, rows, KeyboardLayout.AnsiQwerty, out _), 0, HidppBackend.PaintMs,
                (io, wait) => BeginLaptop(io, rows, productId));

        /// <summary>Whether a laptop's first zone is one light: an empty base
        /// with one edit (AsusAuraCoreLaptopController.cpp:199-200).</summary>
        internal static bool SingleZone(IReadOnlyList<KeyboardLayoutModel> rows)
        {
            if (rows == null || rows.Count == 0) return false;
            var recipe = KeyboardLayouts.Recipe(rows[0].Recipe);
            return recipe != null && recipe.Size == KeyboardSize.Empty && recipe.Edits.Length > 0
                   && recipe.Edits[0].Length == 1;
        }

        internal static byte[] LaptopFrame(params byte[] head)
        {
            var frame = new byte[LaptopLength];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>GetKeyboardLayout's request (AsusAuraCoreLaptopController.cpp:99-139).</summary>
        internal static byte[] LaptopLayoutQuery() => LaptopFrame(KeyboardReport, 0x05, 0x20, 0x31, 0x00, 0x10);

        /// <summary>SendInitDirectMode, SendBrightness at 3 and
        /// SendInitDirectMode again (AsusAuraCoreLaptopController.cpp:71,
        /// 183-185, 193-209 and 304-313).</summary>
        internal static List<byte[]> LaptopOpening(bool singleZone)
        {
            var init = LaptopFrame(KeyboardReport, Direct, (byte)(singleZone ? 0x01 : 0x00));
            return new List<byte[]>
            {
                init,
                LaptopFrame(KeyboardReport, Brightness, 0xC5, 0xC4, LaptopBrightness),
                (byte[])init.Clone(),
            };
        }

        /// <summary>Reads the layout and puts the laptop in Direct mode. Null
        /// when a write failed.</summary>
        internal static HidLightingSession BeginLaptop(IHidReportChannel io, IReadOnlyList<KeyboardLayoutModel> rows,
            int productId)
        {
            if (io == null || rows == null || rows.Count == 0) return null;
            var scratch = new byte[Math.Max(LaptopLength, io.InputReportLength)];
            AsusAuraUsbLighting.Clear(io, scratch);
            bool single = SingleZone(rows);
            var opening = LaptopOpening(single);
            // The first BC goes out when the controller is made, before the
            // layout read.
            if (!io.SetFeature(opening[0])) return null;
            var answer = new byte[Math.Max(LaptopLength, io.FeatureReportLength)];
            var layout = KeyboardLayout.AnsiQwerty;
            if (io.SetFeature(LaptopLayoutQuery()) && io.GetFeature(KeyboardReport, answer) && answer[12] == LaptopIso)
                layout = KeyboardLayout.IsoQwerty;
            if (!io.SetFeature(opening[1]) || !io.SetFeature(opening[2])) return null;
            var map = LaptopMap(productId, rows, layout, out int bufferLength);
            return new LaptopSession(map, bufferLength);
        }

        /// <summary>SetLedsDirect (AsusAuraCoreLaptopController.cpp:211-302)
        /// with each light's color at its LED value in a buffer of
        /// <paramref name="bufferLength"/>, taken as a byte.</summary>
        internal static List<byte[]> LaptopFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int bufferLength)
        {
            int count = (byte)bufferLength;
            var keys = new int[Math.Max(count, LaptopKeySet + LaptopLedsPerFrame)];
            int n = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < n; i++)
            {
                int at = lights[i].Address;
                if (at >= 0 && at < count) keys[at] = colors[i];
            }
            var frames = new List<byte[]>();
            if (count == 1)
            {
                var one = LaptopFrame(KeyboardReport, Direct, 0x01, 0x01);
                AsusAuraUsbLighting.Put(one, 9, keys[0]);
                frames.Add(one);
                return frames;
            }
            var frame = LaptopFrame(KeyboardReport, Direct, 0x00, 0x01, 0x01, 0x01, 0x00, LaptopLedsPerFrame, 0x00);
            for (int i = 0; i < LaptopKeySet; i += LaptopLedsPerFrame)
            {
                int remaining = LaptopKeySet - i;
                if (remaining < LaptopLedsPerFrame)
                {
                    frame[7] = (byte)remaining;
                    Array.Clear(frame, 9, LaptopLength - 9);
                }
                frame[6] = (byte)i;
                for (int k = 0; k < frame[7]; k++) AsusAuraUsbLighting.Put(frame, 9 + k * 3, keys[i + k]);
                frames.Add((byte[])frame.Clone());
            }
            frame[4] = 0x04;
            frame[5] = 0x00;
            frame[6] = 0x00;
            frame[7] = 0x00;
            Array.Clear(frame, 9, LaptopLength - 9);
            for (int k = LaptopKeySet; k < count && 9 + (k - LaptopKeySet) * 3 + 2 < LaptopLength; k++)
                AsusAuraUsbLighting.Put(frame, 9 + (k - LaptopKeySet) * 3, keys[k]);
            frames.Add(frame);
            return frames;
        }

        internal sealed class LaptopSession : HidLightingSession
        {
            private readonly LightMap _map;
            private readonly int _bufferLength;

            public LaptopSession(LightMap map, int bufferLength)
            {
                _map = map;
                _bufferLength = bufferLength;
            }

            public override bool Feature => true;

            public override LightMap Map => _map;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                foreach (var frame in LaptopFrames(lights, colors, _bufferLength))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
