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
    /// <para>Hand-back. The table as it stood before PadForge's first color
    /// goes back, the way OmenMon writes a whole table it read (SetColorTable,
    /// BiosCtl.cs:141-143). The table holds nothing that tells PadForge's
    /// colors from the user's, so it is kept in
    /// <see cref="KeyboardProfileBackups"/>, on disk before the first write,
    /// as <see cref="LenovoK510Lighting"/> keeps its state. A failed BIOS call
    /// or a forced kill ends a session with no hand-back, and the next session
    /// takes the backup as the original, not the colors PadForge left. With no
    /// later claim, the repair (<see cref="HidLightingModel.RepairOwn"/>) puts the
    /// backup back after a failed call of this process's, and after a kill when the
    /// BIOS still holds the table PadForge last kept beside it. Any other table can
    /// be one the Omen Gaming Hub stored after such a run ended, and a write with no
    /// claim behind it could undo the user's own, so it stays. Keeping a table takes
    /// a save of the settings, so a picture is kept once it has stood a while: a kill
    /// during an animation leaves that table too. A claim is the user asking for
    /// PadForge's lighting, so a table the Hub stored is taken for one of PadForge's
    /// only then, and the backup goes back over it at the hand-back.</para>
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

        /// <summary>The key the keyboard's backup is kept under. A laptop has
        /// one, so its product key is enough.</summary>
        internal const string Product = "hp:omen-4zone";

        /// <summary>How often a picture that waits for its backup to reach the
        /// settings file looks again.</summary>
        internal const int SavingMs = LenovoLegionLighting.SavingMs;

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
        private readonly Func<long> _ticks;
        private volatile bool _leftAlone;
        private volatile bool _leftColors;
        private OwnDevice[] _found;

        public HpOmenLaptopLighting()
            : this(null, null, null) { }

        /// <summary>Test seam: the method class check, the opener and the
        /// crash path's one call.</summary>
        internal HpOmenLaptopLighting(Func<bool> classPresent, Func<IHpBiosChannel> open,
            Func<int, int, byte[], int, bool> callOnce, Func<long> ticks = null)
        {
            _classPresent = classPresent ?? OwnLightingChannels.HpBiosClassPresent;
            _open = open ?? OwnLightingChannels.OpenHpBios;
            _callOnce = callOnce ?? OwnLightingChannels.HpBiosCallOnce;
            _ticks = ticks ?? (() => Environment.TickCount64);
            Model = new HidLightingModel
            {
                Family = this,
                Name = "Omen 4-Zone Laptop Keyboard",
                Map = new LightMap(Lights(), "OpenRGB HPOmenLaptopController") { Product = Product },
                Programs = new[] { VendorPrograms.OmenGamingHub },
                RepaintMs = RepaintMs,
                Open = _ => _open(),
                BeginOwn = (channel, _) => channel is IHpBiosChannel bios ? Begin(bios) : null,
                WriteOnce = (_, frame, timeoutMs) => _callOnce(KeyboardCommand, SetColorTable, frame, timeoutMs),
                Owed = _ => !_leftAlone && KeyboardProfileBackups.Get(Product) != null,
                RepairOwn = (channel, _) => channel is IHpBiosChannel bios && Repair(bios),
                RecheckMs = ShownStandsMs,
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

        /// <summary>The key the table PadForge last kept goes under, beside the
        /// backup.</summary>
        internal const string ShownKey = Product + ":shown";

        /// <summary>A picture that has stood this long is kept. An animation's never
        /// stands that long.</summary>
        internal const int ShownStandsMs = 10000;

        /// <summary>A session from the table an earlier session saved and
        /// never put back, else the one the BIOS holds now. Null when the BIOS
        /// gave none.</summary>
        private HidLightingSession Begin(IHpBiosChannel bios)
        {
            var table = bios.Call(KeyboardCommand, GetColorTable, Array.Empty<byte>());
            if (!IsTable(table)) return null;
            _leftAlone = false;
            return KeyboardProfileBackups.Get(Product) is { } owed && owed.Contents.Length == table.Length
                ? new Session(this, bios, owed.Contents, _ticks, ours: true)
                : new Session(this, bios, table, _ticks);
        }

        /// <summary>Puts back the table an earlier session saved, with no claim on
        /// the keyboard, when a session of this process left colors (a failed call) or
        /// the BIOS holds the colors that session last kept. Any other colors stay, and
        /// are not asked about again until the next session. True when nothing is left
        /// to do now, false when the BIOS did not answer.</summary>
        private bool Repair(IHpBiosChannel bios)
        {
            if (KeyboardProfileBackups.Get(Product) is not { } owed) return true;
            var table = bios.Call(KeyboardCommand, GetColorTable, Array.Empty<byte>());
            if (!IsTable(table)) return false;
            if (_leftColors || (KeyboardProfileBackups.Get(ShownKey) is { } shown && SameColors(table, shown.Contents)))
            {
                if (bios.Call(KeyboardCommand, SetColorTable, owed.Contents) == null) return false;
                _leftColors = false;
                KeyboardProfileBackups.Drop(Product);
                KeyboardProfileBackups.Drop(ShownKey);
                return true;
            }
            _leftAlone = true;
            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HidLighting HP Omen keyboard holds colors other than the ones PadForge kept, and they stay");
            return true;
        }

        private static bool SameColors(byte[] table, byte[] other)
            => IsTable(other) && table.AsSpan(ColorsAt, 12).SequenceEqual(other.AsSpan(ColorsAt, 12));

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
            private readonly HpOmenLaptopLighting _family;
            private readonly byte[] _original;
            private readonly Func<long> _ticks;
            private byte[][] _crash = Array.Empty<byte[]>();
            private long _paintedAt;
            private bool _painted;
            private byte[] _notKept;

            public Session(HpOmenLaptopLighting family, IHpBiosChannel bios, byte[] original, Func<long> ticks, bool ours = false)
            {
                _family = family;
                _bios = bios;
                _original = original;
                _ticks = ticks;
                if (ours) _crash = new[] { (byte[])original.Clone() };
            }

            /// <summary>Keeps a picture that has stood, on the model's interval. Nothing
            /// goes to the BIOS.</summary>
            public override bool Recheck(IHidReportChannel io, Action<int> wait)
            {
                if (_notKept != null && _ticks() - _paintedAt >= ShownStandsMs)
                {
                    KeyboardProfileBackups.Take(ShownKey, 0, _notKept);
                    _notKept = null;
                }
                return true;
            }

            public override bool Feature => true;

            /// <summary>The table as it stood before PadForge's first color,
            /// once a color may have gone out.</summary>
            public override byte[][] CrashFrames => _crash;

            /// <summary>Reads the table and writes it back with the colors,
            /// as OpenRGB's setColors does for every color.</summary>
            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // The backup reaches the settings file before the first
                // write it guards.
                if (KeyboardProfileBackups.Get(Product) == null)
                    KeyboardProfileBackups.Take(Product, 0, _original);
                if (!KeyboardProfileBackups.IsPersisted(Product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                var table = _bios.Call(KeyboardCommand, GetColorTable, Array.Empty<byte>());
                if (!IsTable(table)) return HidLightingPaint.Failed;
                // Marked first: a write that reports a failure may still
                // have landed.
                if (_crash.Length == 0) _crash = new[] { (byte[])_original.Clone() };
                _family._leftColors = true;
                var painted = Painted(table, lights, colors);
                if (_bios.Call(KeyboardCommand, SetColorTable, painted) == null) return HidLightingPaint.Failed;
                // The first is kept at once, for a kill in the first seconds.
                if (_painted) _notKept = painted;
                else KeyboardProfileBackups.Take(ShownKey, 0, painted);
                _painted = true;
                _paintedAt = _ticks();
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length > 0)
                {
                    if (_bios.Call(KeyboardCommand, SetColorTable, _original) == null) return false;
                    _crash = Array.Empty<byte[]>();
                }
                _family._leftColors = false;
                KeyboardProfileBackups.Drop(Product);
                KeyboardProfileBackups.Drop(ShownKey);
                return true;
            }
        }
    }
}
