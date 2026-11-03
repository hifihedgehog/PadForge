using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The four-zone keyboard of an HP Omen laptop, lit through HP's BIOS
    /// WMI method as OpenRGB's HPOmenLaptopController lights it (#557).
    ///
    /// <para>The look is OpenRGB's detector: the BIOS reports keyboard
    /// lighting, and a keyboard of the type OpenRGB names WITHOUT_NUMPAD
    /// (HPOmenLaptopWMIDetect_Windows.cpp:20,
    /// HPOmenLaptopController_Windows.cpp:283-316). Both are BIOS calls, so
    /// they are made only where WMI has HP's method class, and the answer
    /// is kept for the life of the process.</para>
    ///
    /// <para>A color goes out as OpenRGB's setColors sends it: the BIOS's
    /// color table is read, each zone's three bytes from byte 25 are
    /// replaced, and the table is written back
    /// (HPOmenLaptopController_Windows.cpp:258-281). OmenMon reads the same
    /// table the same way: its zone count, 24 bytes, then red, green and
    /// blue per zone, Right, Middle, Left, WASD (OmenMon
    /// Hardware/BiosData.cs:100-106 and 198-236, BiosCtl.cs:133-143).</para>
    ///
    /// <para>Hand-back. The table as the session first read it goes back,
    /// the way OmenMon writes a whole table it read (SetColorTable,
    /// BiosCtl.cs:141-143).</para>
    /// </summary>
    internal sealed class HpOmenLaptopLighting : IOwnLookFamily
    {
        public static readonly HpOmenLaptopLighting Instance = new();

        /// <summary>The keyboard commands, 131081, and the rest, 131080
        /// (HPOmenLaptopController_Windows.cpp:265 and 308, OmenMon
        /// BiosData.cs:22-27).</summary>
        internal const int KeyboardCommand = 0x20009;
        internal const int DefaultCommand = 0x20008;

        /// <summary>The command types OpenRGB sends with them
        /// (HPOmenLaptopController_Windows.cpp:265, 278, 292 and 308).</summary>
        internal const int HasLighting = 1;
        internal const int GetColorTable = 2;
        internal const int SetColorTable = 3;
        internal const int GetKeyboardType = 43;

        /// <summary>The keyboard type OpenRGB lights: its answer plus one is
        /// WITHOUT_NUMPAD (HPOmenLaptopController_Windows.h:20-27 and
        /// HPOmenLaptopController_Windows.cpp:310-312), OmenMon's TenKeyLess
        /// (BiosData.cs:93-98).</summary>
        internal const byte TenKeyLess = 2;

        /// <summary>Where the first zone's red is (HPOmenLaptopController_Windows.cpp:272).</summary>
        internal const int ColorsAt = 25;

        /// <summary>The length of a color table, hpqBIOSInt128's answer.</summary>
        internal const int TableLength = 128;

        /// <summary>The unit's key, OpenRGB's location for the device
        /// (RGBController_HPOmenLaptopWMI_Windows.cpp:33).</summary>
        internal const string Key = @"ROOT\\WMI:hpqBIntM";

        /// <summary>The shortest time between two colors, two BIOS calls
        /// each. No reference bounds the rate, so the bound is
        /// PadForge's.</summary>
        internal const int RepaintMs = 100;

        private readonly Func<bool> _classPresent;
        private readonly Func<IHpBiosChannel> _open;
        private readonly Func<int, int, byte[], int, bool> _callOnce;
        private OwnDevice[] _found;

        public HpOmenLaptopLighting()
            : this(null, null, null) { }

        /// <summary>Test seam: the method class check, the opener and the
        /// crash path's one call.</summary>
        internal HpOmenLaptopLighting(Func<bool> classPresent, Func<IHpBiosChannel> open,
            Func<int, int, byte[], int, bool> callOnce)
        {
            _classPresent = classPresent ?? OwnLightingChannels.HpBiosClassPresent;
            _open = open ?? OwnLightingChannels.OpenHpBios;
            _callOnce = callOnce ?? OwnLightingChannels.HpBiosCallOnce;
            Model = new HidLightingModel
            {
                Family = this,
                Name = "Omen 4-Zone Laptop Keyboard",
                Map = new LightMap(Lights(), "OpenRGB HPOmenLaptopController") { Product = "hp:omen-4zone" },
                Programs = new[] { VendorPrograms.OmenGamingHub },
                RepaintMs = RepaintMs,
                Open = _ => _open(),
                BeginOwn = (channel, _) => channel is IHpBiosChannel bios ? Begin(bios) : null,
                WriteOnce = (_, frame, timeoutMs) => _callOnce(KeyboardCommand, SetColorTable, frame, timeoutMs),
            };
        }

        public string Name => "HP Omen Laptop";

        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        /// <summary>OpenRGB's four lights in its order, WASD, Left, Middle,
        /// Right (RGBController_HPOmenLaptopWMI_Windows.cpp:76-90). A
        /// light's address is its zone in the BIOS table, which holds them
        /// the other way round (colors[3 - i] at zone i,
        /// HPOmenLaptopController_Windows.cpp:270-275).</summary>
        internal static IReadOnlyList<LightInfo> Lights() => new[]
        {
            new LightInfo(3, LightKind.Zone, 0, LightKeySource.None),
            new LightInfo(2, LightKind.Zone, 0, LightKeySource.None),
            new LightInfo(1, LightKind.Zone, 0, LightKeySource.None),
            new LightInfo(0, LightKind.Zone, 0, LightKeySource.None),
        };

        public IReadOnlyList<OwnDevice> Look()
        {
            var found = Volatile.Read(ref _found);
            if (found != null) return found;
            if (!_classPresent())
            {
                found = Array.Empty<OwnDevice>();
            }
            else
            {
                using var bios = _open();
                // A BIOS that could not be asked is asked again at the next
                // look.
                if (bios == null) return null;
                var lighting = bios.Call(KeyboardCommand, HasLighting, Array.Empty<byte>());
                var type = bios.Call(DefaultCommand, GetKeyboardType, Array.Empty<byte>());
                if (lighting == null || lighting.Length == 0 || type == null || type.Length == 0) return null;
                found = (lighting[0] & 1) == 1 && type[0] == TenKeyLess
                    ? new[]
                    {
                        new OwnDevice(Key, Model)
                        {
                            Container = PeripheralLinker.LocalMachineContainer, BuiltInKeyboard = true,
                        },
                    }
                    : Array.Empty<OwnDevice>();
            }
            Volatile.Write(ref _found, found);
            return found;
        }

        /// <summary>A session from the table the BIOS holds now, or null
        /// when it gave none.</summary>
        private static HidLightingSession Begin(IHpBiosChannel bios)
        {
            var table = bios.Call(KeyboardCommand, GetColorTable, Array.Empty<byte>());
            return IsTable(table) ? new Session(bios, table) : null;
        }

        private static bool IsTable(byte[] table) => table != null && table.Length >= ColorsAt + 12;

        /// <summary>A copy of <paramref name="table"/> with each light's
        /// color at its zone.</summary>
        internal static byte[] Painted(byte[] table, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var next = (byte[])table.Clone();
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int zone = lights[n].Address;
                if (zone < 0 || zone > 3) continue;
                int rgb = colors[n];
                next[ColorsAt + zone * 3] = (byte)(rgb >> 16);
                next[ColorsAt + zone * 3 + 1] = (byte)(rgb >> 8);
                next[ColorsAt + zone * 3 + 2] = (byte)rgb;
            }
            return next;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly IHpBiosChannel _bios;
            private readonly byte[] _original;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(IHpBiosChannel bios, byte[] original)
            {
                _bios = bios;
                _original = original;
            }

            public override bool Feature => true;

            /// <summary>The table as the session first read it, once a color
            /// may have gone out.</summary>
            public override byte[][] CrashFrames => _crash;

            /// <summary>Reads the table and writes it back with the colors,
            /// as OpenRGB's setColors does for every color.</summary>
            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                var table = _bios.Call(KeyboardCommand, GetColorTable, Array.Empty<byte>());
                if (!IsTable(table)) return HidLightingPaint.Failed;
                // Marked first: a write that reports a failure may still
                // have landed.
                if (_crash.Length == 0) _crash = new[] { (byte[])_original.Clone() };
                return _bios.Call(KeyboardCommand, SetColorTable, Painted(table, lights, colors)) != null
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (_bios.Call(KeyboardCommand, SetColorTable, _original) == null) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
