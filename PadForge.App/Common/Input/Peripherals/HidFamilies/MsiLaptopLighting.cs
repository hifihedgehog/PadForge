using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The SteelSeries lighting controllers of MSI laptops OpenRGB's
    /// MSILaptopController lights (#557): the per-key keyboard ("KLC",
    /// 1038:1122 and 1038:113A), the light bar ("ALC", 1038:1161) and the
    /// GE68HX's light bar with its logo ("KLC light bar", 1038:114B). The
    /// collections are OpenRGB's registrations
    /// (MSILaptopControllerDetect.cpp:330-333) with feature reports of
    /// <see cref="PacketSize"/> bytes. Which device a collection is comes
    /// from its product ID, and which lights it has from the laptop: OpenRGB
    /// lights a device only on a laptop of its model table, found by the
    /// DMI manufacturer and product name (GetMSILaptopModelDMI, lines
    /// 274-288 and 290-314), and so does this family
    /// (<see cref="HidFamilyDmi"/>).
    ///
    /// <para>The colors go out as OpenRGB's Direct mode sends them, its only
    /// mode (RGBController_MSILaptop.cpp:37-41): the keyboard and the light
    /// bar take one 525-byte feature report of LED IDs and colors, and the
    /// GE68HX's light bar takes its colors 30 at a time and then a commit
    /// report (SetLEDs, MSILaptopController.cpp:62-147). OpenRGB marks the
    /// mode as one that saves nothing ("@save :x:",
    /// RGBController_MSILaptop.cpp:18).</para>
    ///
    /// <para>Hand-back. Nothing goes out: no reference reads the
    /// controller's state or returns it to its own effect. The keyboard
    /// shows PadForge's last picture until SteelSeries GG or MSI's program
    /// sets another, or the controller restarts.</para>
    ///
    /// <para>SteelSeries' program lights these laptops' keyboards on
    /// Windows: msi-perkeyrgb's protocol comes from the traffic between the
    /// SteelSeries Engine and the keyboard (msi-perkeyrgb README.md:101-105).
    /// While SteelSeries GG, the Engine's successor, runs, the device is left
    /// to it (<see cref="VendorPrograms.SteelSeriesGG"/>).</para>
    /// </summary>
    internal sealed partial class MsiLaptopLighting : IHidLightingFamily
    {
        /// <summary>One laptop of OpenRGB's model table (MSILaptopModel,
        /// MSILaptopController.h:32-48).</summary>
        internal sealed record ModelRow(string Manufacturer, string Product, (string Name, byte Value)[] KeyboardLeds,
                                        int GridHeight, int GridWidth, uint[] Grid, (string Name, byte Value)[] BarLeds,
                                        int BarLength);

        /// <summary>The device a collection is, by its product ID
        /// (msi_laptop_device, MSILaptopController.h:17-23, picked in
        /// DetectMSILaptop, MSILaptopControllerDetect.cpp:299-314).</summary>
        internal enum DeviceKind
        {
            Keyboard,
            LightBar,
            KeyboardLightBar,
        }

        public static readonly MsiLaptopLighting Instance = new();

        internal const string OpenRgbFamily = "MSILaptopController";

        /// <summary>The product IDs (MSILaptopControllerDetect.cpp:19-22).</summary>
        internal const ushort RaiderA18KeyboardPid = 0x1122;
        internal const ushort RaiderA18LightBarPid = 0x1161;
        internal const ushort Ge68hxKeyboardPid = 0x113A;
        internal const ushort Ge68hxLightBarPid = 0x114B;

        /// <summary>MSI_LAPTOP_PACKET_SIZE, every feature report's length
        /// with the report ID (MSILaptopController.cpp:18 and 68).</summary>
        internal const int PacketSize = 525;

        /// <summary>The commit report's length (MSILaptopController.cpp:69
        /// and 113).</summary>
        internal const int CommitSize = 65;

        /// <summary>MSI_LAPTOP_PAYLOAD_OFFSET (MSILaptopController.cpp:19).</summary>
        internal const int PayloadOffset = 5;

        /// <summary>MSI_LAPTOP_COMMAND and the KLC and ALC packet IDs
        /// (MSILaptopController.cpp:15-17).</summary>
        internal const byte Command = 0x0C;
        internal const byte KeyboardPacket = 0x66;
        internal const byte LightBarPacket = 0x06;

        /// <summary>The most LEDs one report of the GE68HX's light bar
        /// carries (MSILaptopController.cpp:78-81).</summary>
        internal const int LedsPerBarReport = 30;

        /// <summary>The shortest time between two pictures. The mode saves
        /// nothing and no reference bounds the rate, so the bound is
        /// PadForge's, the ASUS TUF family's.</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        private readonly Func<string> _manufacturer;
        private readonly Func<string> _product;
        private ModelRow _laptop;
        private int _looked;
        private readonly Dictionary<(ushort, ModelRow), HidLightingModel> _models = new();

        public MsiLaptopLighting()
            : this(null, null) { }

        /// <summary>Test seam: the DMI manufacturer and product name
        /// reads.</summary>
        internal MsiLaptopLighting(Func<string> manufacturer, Func<string> product)
        {
            _manufacturer = manufacturer ?? HidFamilyDmi.Manufacturer;
            _product = product ?? HidFamilyDmi.ProductName;
        }

        public string Name => "MSI Laptop";

        /// <summary>The device a product ID names, or null.</summary>
        internal static DeviceKind? KindOf(ushort productId) => productId switch
        {
            RaiderA18KeyboardPid or Ge68hxKeyboardPid => DeviceKind.Keyboard,
            RaiderA18LightBarPid => DeviceKind.LightBar,
            Ge68hxLightBarPid => DeviceKind.KeyboardLightBar,
            _ => null,
        };

        /// <summary>The laptop of OpenRGB's table the PC is, by its DMI
        /// manufacturer and product name compared exactly, or null. Read
        /// once, when a collection first matches, and kept: a read that
        /// failed is tried again at the next match.</summary>
        internal ModelRow Laptop()
        {
            if (Volatile.Read(ref _looked) != 0) return Volatile.Read(ref _laptop);
            string manufacturer = _manufacturer();
            string product = _product();
            if (manufacturer == null || product == null) return null;
            ModelRow found = null;
            foreach (var row in Models)
                if (row.Manufacturer == manufacturer && row.Product == product)
                {
                    found = row;
                    break;
                }
            Volatile.Write(ref _laptop, found);
            Volatile.Write(ref _looked, 1);
            return found;
        }

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.FeatureReportLength < PacketSize) return null;
            if (KindOf(collection.ProductId) is not DeviceKind kind) return null;
            if (kind == DeviceKind.KeyboardLightBar && collection.OutputReportLength < CommitSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            var laptop = Laptop();
            if (laptop == null) return null;
            lock (_models)
            {
                if (!_models.TryGetValue((collection.ProductId, laptop), out var model))
                    _models[(collection.ProductId, laptop)] = model = Model(registrations[0].Name, collection.ProductId, kind, laptop);
                return model;
            }
        }

        private HidLightingModel Model(string name, ushort productId, DeviceKind kind, ModelRow laptop)
            => new()
            {
                Family = this,
                Name = name + " (" + laptop.Product + ")",
                Map = new LightMap(Lights(kind, laptop), "OpenRGB MSILaptopController")
                {
                    Product = $"msi-laptop:1038:{productId:X4}:{laptop.Product}",
                },
                Programs = new[] { VendorPrograms.SteelSeriesGG },
                RepaintMs = RepaintMs,
                Begin = (_, _) => new Session(kind),
            };

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The lights of a device in OpenRGB's order, each light's
        /// address its LED ID (SetupZones, RGBController_MSILaptop.cpp:53-134).
        /// The keyboard's keys sit on its grid. The light bars' LEDs are
        /// edge lights and their "Logo" LED a logo light.</summary>
        internal static IReadOnlyList<LightInfo> Lights(DeviceKind kind, ModelRow laptop)
        {
            var lights = new List<LightInfo>();
            if (kind == DeviceKind.Keyboard)
            {
                for (int i = 0; i < laptop.KeyboardLeds.Length; i++)
                {
                    var (name, id) = laptop.KeyboardLeds[i];
                    var light = KeyLight(name, id);
                    lights.Add(Place(light, laptop.Grid, laptop.GridWidth, i));
                }
                return lights;
            }
            foreach (var (name, id) in laptop.BarLeds)
                lights.Add(name == "Logo"
                    ? new LightInfo(id, LightKind.Logo, 0, LightKeySource.None)
                    : new LightInfo(id, LightKind.Edge, 0, LightKeySource.None));
            return lights;
        }

        /// <summary>A keyboard LED. Its name gives its key where OpenRGB's
        /// key names do. The keyboard numbers its LEDs by the key's HID
        /// usage, every key named in the table agrees, so the "Home/Page Up"
        /// key, which no key name covers, is the key its ID is: 0x4B, Page
        /// Up. An ID outside the Keyboard/Keypad page's keys (Right Fn's
        /// 0xF0) names no key.</summary>
        internal static LightInfo KeyLight(string name, byte id)
        {
            if (OpenRgbKeys.TryGet(name, out ushort usage, out var label) && usage != 0)
                return new LightInfo(id, LightKind.Key, usage, LightKeySource.Generated);
            if (label != LightLabel.None)
                return new LightInfo(id, LightKind.Other, 0, LightKeySource.None) { Label = label };
            if (id >= 0x04 && id <= 0xE7)
                return new LightInfo(id, LightKind.Key, id, LightKeySource.Vendor);
            return new LightInfo(id, LightKind.Key, 0, LightKeySource.None);
        }

        /// <summary>The light placed at the grid cell that holds LED
        /// <paramref name="index"/>, in key widths.</summary>
        private static LightInfo Place(LightInfo light, uint[] grid, int width, int index)
        {
            for (int cell = 0; cell < grid.Length; cell++)
                if (grid[cell] == (uint)index)
                    return light with { X = cell % width, Y = cell / width, Width = 1, Height = 1, HasPosition = true };
            return light;
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>The keyboard's or the light bar's one report: the report
        /// ID 0, MSI_LAPTOP_COMMAND, the packet ID in byte 3, then four bytes
        /// per LED from byte 5, its ID and its color, and 0xFF in every
        /// slot no LED takes (SetLEDs, MSILaptopController.cpp:117-145).</summary>
        internal static byte[] SlotFrame(DeviceKind kind, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = new byte[PacketSize];
            frame[1] = Command;
            frame[3] = kind == DeviceKind.Keyboard ? KeyboardPacket : LightBarPacket;
            for (int i = 0; i < (PacketSize - PayloadOffset) / 4; i++)
                frame[PayloadOffset + i * 4] = 0xFF;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = PayloadOffset + n * 4;
                if (at + 3 >= PacketSize) break;
                frame[at] = (byte)lights[n].Address;
                frame[at + 1] = (byte)(colors[n] >> 16);
                frame[at + 2] = (byte)(colors[n] >> 8);
                frame[at + 3] = (byte)colors[n];
            }
            return frame;
        }

        /// <summary>The GE68HX light bar's reports: per 30 LEDs a feature
        /// report of 0E, the LED count in byte 3, and 12 bytes per LED from
        /// byte 5, its color, six zeros, 01 and 00 and its zone ID
        /// (MSILaptopController.cpp:74-105).</summary>
        internal static List<byte[]> BarFrames(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frames = new List<byte[]>();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int sent = 0; sent < count;)
            {
                int inPacket = Math.Min(count - sent, LedsPerBarReport);
                var frame = new byte[PacketSize];
                frame[1] = 0x0E;
                frame[3] = (byte)inPacket;
                for (int i = 0; i < inPacket; i++)
                {
                    int n = sent + i;
                    int at = PayloadOffset + i * 12;
                    frame[at] = (byte)(colors[n] >> 16);
                    frame[at + 1] = (byte)(colors[n] >> 8);
                    frame[at + 2] = (byte)colors[n];
                    frame[at + 9] = 0x01;
                    frame[at + 11] = (byte)lights[n].Address;
                }
                frames.Add(frame);
                sent += inPacket;
            }
            return frames;
        }

        /// <summary>The commit output report after the light bar's colors
        /// (MSILaptopController.cpp:107-113).</summary>
        internal static byte[] CommitFrame()
        {
            var frame = new byte[CommitSize];
            frame[1] = 0x0D;
            frame[3] = 0x02;
            return frame;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly DeviceKind _kind;

            public Session(DeviceKind kind) => _kind = kind;

            public override bool Feature => true;

            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (_kind != DeviceKind.KeyboardLightBar)
                    return io.SetFeature(SlotFrame(_kind, lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
                foreach (var frame in BarFrames(lights, colors))
                    if (!io.SetFeature(frame)) return HidLightingPaint.Failed;
                return io.WriteOutput(CommitFrame()) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
