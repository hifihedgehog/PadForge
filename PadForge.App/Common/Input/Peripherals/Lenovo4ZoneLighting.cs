using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The four-zone keyboards of the Lenovo Legion 5 of 2020 to 2024, the
    /// LOQ of 2023 and 2024 and their IdeaPad twins (#557), as OpenRGB's
    /// Lenovo4ZoneUSBController and L5P-Keyboard-RGB drive them: one
    /// 33-byte feature report, ID 0xCC, on usage page 0xFF89 and usage
    /// 0xCC (Lenovo4ZoneUSBControllerDetect.cpp:24-28 and 48-55,
    /// L5P-Keyboard-RGB driver/src/lib.rs:14-26).
    ///
    /// <para>The model of the device. The report sets the effect, its speed,
    /// the brightness and the four zones' colors at once, and shows at once
    /// (Lenovo4ZoneUSBController.cpp:28-44, lib.rs:55-98). No reference
    /// reads the keyboard's state back, so nothing a session finds can be
    /// put back. L5P-Keyboard-RGB writes a fresh state when it starts and
    /// none when it stops, and draws its own effects by sending static
    /// colors one after another, every 20 ms in its swipe and fade effects
    /// (app/src/manager/effects/swipe.rs:53, fade.rs:42). Lenovo Legion
    /// Toolkit takes the keyboard from Lenovo's firmware with the WMI method
    /// LENOVO_GAMEZONE_DATA.SetLightControlOwner(1) before it writes, gives
    /// it back with SetLightControlOwner(0) when it exits, and writes its
    /// preset again after the laptop wakes, so the firmware does not keep a
    /// written state through sleep (RGBKeyboardBacklightController.cs:48-89,
    /// App.xaml.cs:208-215, PowerStateListener.cs:146). OpenRGB and
    /// L5P-Keyboard-RGB write the report without taking the keyboard, so
    /// a session does not take it either, and hands it back as the Toolkit
    /// does when it exits: SetLightControlOwner(0) on a fenced WMI channel
    /// (<see cref="OwnLightingChannels.SetLenovoLightControlOwner"/>). The
    /// crash path sends HID reports only and cannot make that call, so a
    /// session records the hand-back as owed in the settings file
    /// (<see cref="KeyboardProfileBackups"/>, profile 0) before its first
    /// picture, and the next run makes the call for a run that ended
    /// without it.</para>
    ///
    /// <para>The frame: 0xCC 0x16, the static effect 1, speed 1, brightness
    /// 2, then the four zones' red, green and blue, then 0, and the two wave
    /// directions 0 (KeyboardState and setMode, LenovoDevices4Zone.h:52-95,
    /// Lenovo4ZoneUSBController.cpp:30-42). Brightness 2 is the higher of
    /// the two the keyboard has (LENOVO_4_ZONE_BRIGHTNESS_HIGH,
    /// LenovoDevices4Zone.h:37-41, lib.rs:29), the brightness Lenovo Legion
    /// Toolkit sends for High (Convert, RGBKeyboardBacklightController.cs:336-341),
    /// so the picture shows at the brightness its colors carry.</para>
    /// </summary>
    internal sealed class Lenovo4ZoneLighting : IHidLightingFamily
    {
        public static readonly Lenovo4ZoneLighting Instance = new();

        internal const ushort VendorId = 0x048D;
        internal const ushort UsagePage = 0xFF89;
        internal const ushort Usage = 0xCC;

        /// <summary>LENOVO_4_ZONE_HID_PACKET_SIZE (Lenovo4ZoneUSBController.h:25).</summary>
        internal const int PacketSize = 33;

        internal const byte ReportId = 0xCC;
        internal const byte Command = 0x16;
        internal const byte StaticEffect = 0x01;
        internal const byte Speed = 0x01;
        internal const byte Brightness = 0x02;

        /// <summary>How long the hand-back's WMI call may take before the
        /// worker goes on without it.</summary>
        internal const int OwnerCallMs = 2000;

        internal const int RepairTries = 3;
        internal const int SavingMs = 100;

        /// <summary>What a session records in
        /// <see cref="KeyboardProfileBackups"/>, profile 0, standing for
        /// "this keyboard's hand-back is owed": the frame's first bytes.</summary>
        internal static readonly byte[] OwedRecord = { ReportId, Command, 0x00 };

        /// <summary>The hand-back's call, the fenced WMI channel unless a
        /// test puts a fake in its place.</summary>
        internal static Func<int, bool> GiveBack = owner => OwnLightingChannels.SetLenovoLightControlOwner(owner, OwnerCallMs);

        /// <summary>The shortest time between two pictures, the spacing of
        /// L5P-Keyboard-RGB's own streamed effects (swipe.rs:53).</summary>
        internal const int RepaintMs = 20;

        /// <summary>The product IDs: OpenRGB's (LenovoDevices4Zone.h:20-27)
        /// and the ones only L5P-Keyboard-RGB lists, the Legion 5 of 2024,
        /// its Pro, the LOQ of 2024 and the LOQ of 2023 (lib.rs:15-17 and
        /// 20).</summary>
        internal static readonly IReadOnlyDictionary<ushort, string> Products = new Dictionary<ushort, string>
        {
            [0xC963] = "Lenovo IdeaPad 3-15ach6 / Legion 5 2021 IdeaPad",
            [0xC985] = "Lenovo Legion 5 2023",
            [0xC984] = "Lenovo Legion 5 2023 IdeaPad",
            [0xC975] = "Lenovo Legion 5 2022",
            [0xC973] = "Lenovo Legion 5 2022 IdeaPad",
            [0xC965] = "Lenovo Legion 5 2021",
            [0xC955] = "Lenovo Legion 5 2020",
            [0xC995] = "Lenovo Legion 5 Pro 2024",
            [0xC994] = "Lenovo Legion 5 2024",
            [0xC993] = "Lenovo LOQ 2024",
            [0xC983] = "Lenovo LOQ 2023",
        };

        public string Name => "Lenovo 4-zone";

        private static readonly Dictionary<ushort, HidLightingModel> s_models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            if (collection.FeatureReportLength < PacketSize) return null;
            if (!Products.TryGetValue(collection.ProductId, out string name)) return null;
            lock (s_models)
            {
                if (!s_models.TryGetValue(collection.ProductId, out var model))
                    s_models[collection.ProductId] = model = Model(name, collection.ProductId);
                return model;
            }
        }

        private static HidLightingModel Model(string name, ushort productId)
        {
            string product = $"lenovo:{VendorId:X4}:{productId:X4}";
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(Lights(), "OpenRGB LenovoDevices4Zone.h") { Product = product },
                Programs = VendorPrograms.Lenovo4Zone,
                HoldMs = 0,
                RepaintMs = RepaintMs,
                Begin = (_, _) => new Session(product),
                Owed = _ => KeyboardProfileBackups.Get(product) != null
                             && KeyboardProfileBackups.Failures(product) < RepairTries,
                Repair = (_, _) => Repair(product),
            };
        }

        /// <summary>Makes the hand-back a run that ended without it owes.
        /// True when nothing is owed any more.</summary>
        private static bool Repair(string product)
        {
            if (KeyboardProfileBackups.Get(product) == null) return true;
            if (GiveBack(0))
            {
                KeyboardProfileBackups.Drop(product);
                return true;
            }
            KeyboardProfileBackups.NoteFailure(product);
            return false;
        }

        /// <summary>The four zones from left to right, addressed by their
        /// numbers 0 to 3 (lenovo_4_zone_leds, LenovoDevices4Zone.h:101-107,
        /// RGBController_Lenovo4ZoneUSB.cpp:106-112).</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(4);
            foreach (var led in LenovoLights.Lenovo4ZoneLeds)
                lights.Add(new LightInfo(led.Number, LightKind.Zone, 0, LightKeySource.None) { Zone = led.Number + 1 });
            return lights;
        }

        /// <summary>The frame for <paramref name="colors"/>, one per light of
        /// <paramref name="lights"/>, each placed by its zone number. A zone
        /// the picture leaves out is dark.</summary>
        internal static byte[] Frame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = Command;
            frame[2] = StaticEffect;
            frame[3] = Speed;
            frame[4] = Brightness;
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int zone = lights[n].Address;
                if (zone < 0 || zone > 3) continue;
                int color = colors[n];
                frame[5 + 3 * zone] = (byte)(color >> 16);
                frame[6 + 3 * zone] = (byte)(color >> 8);
                frame[7 + 3 * zone] = (byte)color;
            }
            return frame;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly string _product;

            public Session(string product) => _product = product;

            public override bool Feature => true;

            /// <summary>None: the hand-back is a WMI call, which the next run
            /// makes when this one could not.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // The owed hand-back reaches the settings file before the
                // first picture.
                if (KeyboardProfileBackups.Get(_product) == null)
                    KeyboardProfileBackups.Take(_product, 0, OwedRecord);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                return io.SetFeature(Frame(lights, colors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            /// <summary>SetLightControlOwner(0), Lenovo Legion Toolkit's
            /// hand-back, while one is owed: this session's picture went out,
            /// or an earlier run's did.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (KeyboardProfileBackups.Get(_product) == null) return true;
                if (!GiveBack(0)) return false;
                KeyboardProfileBackups.Drop(_product);
                return true;
            }
        }
    }
}
