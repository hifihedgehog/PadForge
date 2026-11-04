using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The per-key keyboards of Clevo laptops and their resellers' (TUXEDO,
    /// PCSpecialist and others) on ITE's 8291 (048D:600B) and 829x
    /// (048D:8910) controllers, lit by OpenRGB's ClevoKeyboardController
    /// (#557). The collections are OpenRGB's registrations
    /// (ClevoKeyboardControllerDetect.cpp:60-61): usage page 0xFF03 with
    /// 8-byte feature reports and 65-byte output reports for the 8291, usage
    /// page 0xFF89 usage 0xCC with 6-byte feature reports for the 829x.
    ///
    /// <para>The lights are the keys of OpenRGB's layouts, built by its
    /// keyboard layout manager in the ISO region its code always passes
    /// (RGBController_ClevoKeyboard.cpp:375-434): the 829x's full layout,
    /// and for the 8291 the TUXEDO Stellaris 16 Gen7 AMD's layout where the
    /// DMI SKU is STELLARIS16A07 (lines 384-389, <see cref="HidFamilyDmi"/>),
    /// else the TKL layout with its number pad. A light's address is its key
    /// value: the 8291's index in its 6 by 21 color grid, the 829x's LED ID.
    /// OpenRGB also offers the 8291 an alternate map for three keys that
    /// some boards wire elsewhere, which the user picks
    /// (lines 556-608); here the default map stands, and Map Key Lights
    /// covers a board that differs. The 829x's value list names a Left Fn
    /// key in its bottom row (ClevoKeyboardDevices.cpp:131-132) that the
    /// layout manager's base layout does not have, so OpenRGB's own build
    /// gives Left Windows the ID its comment calls Left Fn's, and each key
    /// after it to Right Fn the ID of the key to its left. The map follows
    /// OpenRGB's build.</para>
    ///
    /// <para>A picture goes out as OpenRGB's Direct mode sends it
    /// (SendColors, ClevoKeyboardController.cpp:366-435), at the mode's full
    /// brightness (RGBController_ClevoKeyboard.cpp:42-50). The 8291 takes
    /// the Direct mode report, then for each of its six rows a row report
    /// and an output report of the row's 21 blues, greens and reds. The 829x
    /// takes the brightness report and then one report per LED of its 6 by
    /// 20 grid. A grid cell no key holds is black. OpenRGB marks Direct as
    /// a mode that saves nothing ("@save :x:"), and the 829x's save to the
    /// BIOS is a command of its own that PadForge never sends
    /// (SaveToBios829x, line 187-190).</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the
    /// keyboard's mode back. The keyboard shows PadForge's last picture
    /// until the laptop's own program sets another or the laptop restarts.
    /// No source names that program's process, so none is listed.</para>
    /// </summary>
    internal sealed class ClevoLighting : IHidLightingFamily
    {
        public static readonly ClevoLighting Instance = new();

        internal const string OpenRgbFamily = "ClevoKeyboardController";

        /// <summary>The layout table's family name
        /// (<see cref="KeyboardLayouts.ModelsOf"/>).</summary>
        internal const string LayoutFamily = "ClevoKeyboard";

        internal const ushort Ite8291Pid = 0x600B;
        internal const ushort Ite829xPid = 0x8910;

        /// <summary>The 8291's sizes and commands
        /// (ClevoKeyboardController.h:34-45).</summary>
        internal const int ReportSize8291 = 8;
        internal const int RowDataSize = 65;
        internal const int Rows8291 = 6;
        internal const int Cols8291 = 21;
        internal const byte Brightness8291 = 0x32;
        internal const byte CommandMode = 0x08;
        internal const byte CommandRow = 0x16;
        internal const byte PowerOn = 0x02;

        /// <summary>CLEVO_KEYBOARD_MODE_DIRECT (ClevoKeyboardController.h:88).</summary>
        internal const byte ModeDirect = 0x33;

        /// <summary>The 829x's sizes and commands
        /// (ClevoKeyboardController.h:53-63).</summary>
        internal const int ReportSize829x = 6;
        internal const byte ReportId829x = 0xCC;
        internal const int Rows829x = 6;
        internal const int Cols829x = 20;
        internal const byte Brightness829x = 0x0A;
        internal const byte CommandSetLed = 0x01;
        internal const byte CommandBrightness = 0x09;

        /// <summary>The DMI SKU that picks the Stellaris 16 Gen7 AMD's layout
        /// (RGBController_ClevoKeyboard.cpp:388).</summary>
        internal const string StellarisSku = "STELLARIS16A07";

        /// <summary>The shortest time between two pictures, PadForge's bound
        /// for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        private readonly Func<string> _sku;
        private string _foundSku;
        private readonly Dictionary<(ushort, string), HidLightingModel> _models = new();

        public ClevoLighting()
            : this(null) { }

        /// <summary>Test seam: the DMI SKU read.</summary>
        internal ClevoLighting(Func<string> sku) => _sku = sku ?? HidFamilyDmi.ProductSku;

        public string Name => "Clevo";

        /// <summary>The 829x's LED ID of a grid cell, CLEVO_829X_LED_ID
        /// (ClevoKeyboardController.h:59).</summary>
        internal static int LedId829x(int row, int col) => ((row & 0x07) << 5) | (col & 0x1F);

        /// <summary>The layout table's key for a controller and a SKU: the
        /// 829x's own, the Stellaris 16 Gen7 AMD's, or the default.</summary>
        internal static string LayoutKey(bool ite829x, string sku)
            => ite829x ? "CLEVO_KB_ITE829X" : sku == StellarisSku ? StellarisSku : "";

        /// <summary>The DMI SKU, read once an 8291 matches, and again at the
        /// next match while the read fails.</summary>
        private string Sku()
        {
            string sku = Volatile.Read(ref _foundSku);
            if (sku != null) return sku;
            sku = _sku();
            if (sku != null) Volatile.Write(ref _foundSku, sku);
            return sku;
        }

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null) return null;
            bool ite829x = collection.ProductId == Ite829xPid;
            if (!ite829x && collection.ProductId != Ite8291Pid) return null;
            if (ite829x ? collection.FeatureReportLength < ReportSize829x
                    : collection.FeatureReportLength < ReportSize8291 || collection.OutputReportLength < RowDataSize)
                return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            string key;
            if (ite829x) key = LayoutKey(true, null);
            else if (Sku() is string sku) key = LayoutKey(false, sku);
            else return null;
            lock (_models)
            {
                if (!_models.TryGetValue((collection.ProductId, key), out var model))
                    _models[(collection.ProductId, key)] = model = Model(registrations[0].Name, collection.ProductId, key);
                return model;
            }
        }

        private HidLightingModel Model(string name, ushort productId, string key)
        {
            bool ite829x = productId == Ite829xPid;
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = new LightMap(Lights(key), "OpenRGB ClevoKeyboardController")
                {
                    Product = $"clevo:048D:{productId:X4}" + (key.Length > 0 && !ite829x ? ":" + key : ""),
                },
                RepaintMs = RepaintMs,
                Begin = (_, _) => new Session(ite829x),
            };
        }

        /// <summary>The keys of the layout the table names for
        /// <paramref name="key"/>, in the ISO region, each light's address
        /// its key value. A key whose value an earlier key holds has no LED
        /// of its own and is left out: the 829x's value list gives Escape the
        /// LED ID 0 and writes 0 as well for the keys it has no LED for,
        /// Scroll Lock, Pause, the ISO # and the ISO backslash
        /// (clevo_829x_full_values, ClevoKeyboardDevices.cpp:114-133).
        /// OpenRGB keeps them, and as each value points at the color of the
        /// last key that holds it (BuildBufferMap,
        /// RGBController_ClevoKeyboard.cpp:452-458), its Escape shows the ISO
        /// backslash's color.</summary>
        internal static IReadOnlyList<LightInfo> Lights(string key)
        {
            foreach (var row in KeyboardLayouts.ModelsOf(LayoutFamily, -1))
                if (row.Key == key)
                {
                    var lights = new List<LightInfo>();
                    var held = new HashSet<int>();
                    foreach (var light in KeyboardLayouts.Build(row.Recipe, KeyboardLayout.IsoQwerty).Lights((_, led) => (int)led.Value))
                        if (held.Add(light.Address)) lights.Add(light);
                    return lights;
                }
            return Array.Empty<LightInfo>();
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>The colors by key value, as BuildBufferMap points each
        /// value at its key's color, the later key where two share one
        /// (RGBController_ClevoKeyboard.cpp:436-459). A value no key has is
        /// black.</summary>
        internal static int[] ByValue(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int size)
        {
            var byValue = new int[size];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int value = lights[n].Address;
                if (value >= 0 && value < size) byValue[value] = colors[n] & 0xFFFFFF;
            }
            return byValue;
        }

        /// <summary>The 8291's reports: the Direct mode, then per row the
        /// row report and its output report, blues, then greens, then reds,
        /// from byte 2 (ClevoKeyboardController.cpp:400-434).</summary>
        internal static List<(bool Feature, byte[] Report)> Frames8291(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byValue = ByValue(lights, colors, Rows8291 * Cols8291);
            var frames = new List<(bool, byte[])>
            {
                (true, new byte[] { CommandMode, PowerOn, ModeDirect, 0, Brightness8291, 0, 0, 0 }),
            };
            for (int row = 0; row < Rows8291; row++)
            {
                frames.Add((true, new byte[] { CommandRow, 0, (byte)row, 0, 0, 0, 0, 0 }));
                var data = new byte[RowDataSize];
                for (int col = 0; col < Cols8291; col++)
                {
                    int rgb = byValue[row * Cols8291 + col];
                    data[2 + col] = (byte)rgb;
                    data[2 + Cols8291 + col] = (byte)(rgb >> 8);
                    data[2 + Cols8291 * 2 + col] = (byte)(rgb >> 16);
                }
                frames.Add((false, data));
            }
            return frames;
        }

        /// <summary>The 829x's reports: the brightness at speed 0, then
        /// CC 01, the LED ID and its color for each cell of the grid, row by
        /// row (ClevoKeyboardController.cpp:371-394).</summary>
        internal static List<byte[]> Frames829x(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var byValue = ByValue(lights, colors, LedId829x(Rows829x - 1, Cols829x - 1) + 1);
            var frames = new List<byte[]> { new byte[] { ReportId829x, CommandBrightness, Brightness829x, 0, 0, 0 } };
            for (int row = 0; row < Rows829x; row++)
                for (int col = 0; col < Cols829x; col++)
                {
                    int id = LedId829x(row, col);
                    int rgb = byValue[id];
                    frames.Add(new byte[] { ReportId829x, CommandSetLed, (byte)id, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb });
                }
            return frames;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _ite829x;

            public Session(bool ite829x) => _ite829x = ite829x;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (_ite829x)
                {
                    foreach (var frame in Frames829x(lights, colors))
                        if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                    return HidLightingPaint.Sent;
                }
                foreach (var (feature, report) in Frames8291(lights, colors))
                    if (!(feature ? io.SetFeature(report) : io.WriteOutput(report))) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
