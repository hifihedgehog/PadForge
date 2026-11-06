using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The lighting controller of a Lenovo Legion laptop's Spectrum keyboard,
    /// the ITE controller OpenRGB drives as its Gen 7 and 8 controller
    /// (#557): the Legion 7 and 7S of Gen 7 to 9, the Legion 7 Gen 10 and
    /// the Legion 5 Gen 10. The collections, the frames and the lights are
    /// OpenRGB's at the pinned commit: the collection
    /// (Controllers/LenovoControllers/LenovoUSBControllerDetect.cpp:26-30
    /// and 73-80), the frames (LenovoUSBController_Gen7_8/LenovoUSBController_Gen7_8.cpp
    /// with the opcodes of LenovoUSBController_Gen7_8.h:24-34) and the
    /// lights (LenovoDevices.h and RGBController_Lenovo_Gen7_8.cpp:31-78 and
    /// 401-428). On the owner's Legion Pro 7 16AFR10H the collection is
    /// 048D:C197 COL02, usage page 0xFF89 and usage 0x07, with 960-byte
    /// feature reports.
    ///
    /// <para>Two frame formats. The Gen 10 keyboards (C197, C195) carry a
    /// frame's payload length in bytes 2 and 3 (SetGen10PayloadLength,
    /// LenovoUSBController_Gen7_8.cpp:18-27). The keyboards before them
    /// carry 0xC0 0x03 there, and a SAVE_PROFILE frame its whole length in
    /// byte 2 (lines 83-84 and 128), as Lenovo Legion Toolkit writes
    /// them (LENOVO_SPECTRUM_HEADER and
    /// LENOVO_SPECTRUM_EFFECT_DESCRIPTION.ToBytes, Native.cs:145-152 and
    /// 467-469).</para>
    ///
    /// <para>The model of the keyboard's memory. A keyboard keeps up to
    /// seven lighting profiles (Lenovo Legion Toolkit switches among 0 to
    /// 6, SetProfileAsync), and shows the active one. SAVE_PROFILE (0xCB)
    /// replaces what a profile holds and stays in it. GET_PROFILE (0xCC)
    /// reads a profile and, on the owner's Gen 10 keyboard, makes it the
    /// active one, so only the active profile is ever read.
    /// GET_ACTIVE_PROFILE (0xCA), the key count and the key pages (0xC4,
    /// 0xC5) change nothing. SET_DIRECT_MODE (0xD0) on or off and the
    /// direct frames (0xA1) change what shows and nothing that is stored.
    /// A profile key moves the keyboard to another profile, and a second
    /// program that writes a profile replaces what PadForge wrote.</para>
    ///
    /// <para>Gen 10: still pictures stored in the profile. The colors are
    /// stored in the keyboard's active lighting profile, as OpenRGB's
    /// Static mode stores them: one SAVE_PROFILE frame with a static group
    /// for each color (setLedsByGroup, and GetLedGroups in
    /// RGBController_Lenovo_Gen7_8.cpp:657-806). The keyboard then shows
    /// them on its own and keeps them. A stream of colors does not show
    /// well on Gen 10. OpenRGB's Direct mode frames showed for a moment and
    /// gave way to the stored effect on the owner's Legion, sent every five
    /// seconds, every 60 ms with D0 01 ahead of each frame, and every 60 ms
    /// behind a single D0 01. With the Aurora Sync group stored first
    /// (<see cref="StreamHeader"/>), frames sent every 60 ms with D0 01
    /// ahead of each changed the keys, but the keys went dark for a moment
    /// at every change (2026-11-03). Lenovo Legion Toolkit's maintained
    /// fork hides its streamed Aurora Sync on Gen 10 machines as
    /// "unsupported on modern Legions" (commit fc8c4f41,
    /// SpectrumKeyboardBacklightController.cs:211-214). Its maintainer
    /// reports Lenovo moved that effect to Windows Dynamic Lighting
    /// (LenovoLegionToolkit-Team issue 112), and on a Legion Pro 7 16IAX10H
    /// its Aurora Sync left the keys dark or frozen (issue 335). The Gen 10
    /// tools that color keys one by one write static groups into the
    /// profile as well: legion-spectrum-control, built on a Legion Pro 7
    /// 16IAX10H (spectrum-ctl.py cmd_keys), and the Toolkit's fork
    /// (SetProfileDescriptionAsync). The Legion 5 Gen 10's 24-zone board
    /// has no Direct mode at all in OpenRGB (IsKeyboardOnlyDevice,
    /// RGBController_Lenovo_Gen7_8.cpp:26-29 and 319-331), so it takes
    /// stored pictures only. A Gen 10 controller is never streamed to.</para>
    ///
    /// <para>Gen 7 to 9: a stream. These keyboards take OpenRGB's Direct
    /// mode: SET_DIRECT_MODE on for the active profile, then the profile
    /// stored as one black Direct group over every light, then direct
    /// frames of every light's color (DeviceUpdateMode and DeviceUpdateLEDs,
    /// RGBController_Lenovo_Gen7_8.cpp:507-515 and 531-555). Lenovo Legion
    /// Toolkit's Aurora Sync, written for these generations, stores the
    /// same group and streams the same frames every 60 ms
    /// (SpectrumKeyboardBacklightController.cs:41 and 470-568). The group
    /// writes over the profile, so the profile is backed up first and
    /// written back at the hand-back, with direct mode turned off ahead of
    /// it, as Lenovo Legion Toolkit turns Aurora Sync off when it stops
    /// (lines 556-563).</para>
    ///
    /// <para>The profile is the keyboard's own memory, so PadForge puts back
    /// what it held. Before its first write a session reads the profile
    /// (GET_PROFILE) and saves the answer in the settings file
    /// (<see cref="KeyboardProfileBackups"/>). Its hand-back writes the
    /// answer back, reads the profile again to check it, and lets the
    /// backup go. A run that ends without a hand-back, as a forced kill
    /// does, leaves the backup, and the next run puts it back. Three facts
    /// of the owner's keyboard, read and never written, shape this. Its
    /// answer to GET_PROFILE is the profile's frame as it was written, its
    /// length bytes included: OpenRGB's writes answer with their payload
    /// lengths of 224 and 282 bytes, other programs' with 960. The answer
    /// sent back as SAVE_PROFILE therefore restores the profile byte for
    /// byte. GET_PROFILE also makes the profile it reads the active one,
    /// so a session reads only the active profile and checks that it is
    /// active right before every read and write. And that answer comes
    /// late: a read right after the request can return the answer before
    /// it. A keyboard before Gen 10 gets the answer back behind the length
    /// byte its own format carries.</para>
    ///
    /// <para>A build of 2026-11-03 (6411cbdc) streamed a Gen 10 keyboard
    /// through the Aurora Sync group, and a run of it killed mid-stream
    /// leaves the group in the profile with its backup on disk. A run of
    /// the Gen 7 to 9 stream killed the same way leaves the same group. The
    /// group reads as a picture of PadForge's (<see cref="IsOwnPicture"/>),
    /// and putting the profile back turns direct mode off first, as OpenRGB
    /// turns its Direct mode off before it writes the next mode
    /// (<see cref="StopStream"/>). A session that opens on the group puts
    /// the profile back before anything else.</para>
    ///
    /// <para>A profile key that moves the keyboard to another profile is
    /// the user choosing its stored lighting. The session writes nothing
    /// until the keyboard is back on its profile, as OpenRGB adopts a
    /// profile the keyboard switched to (DeviceUpdateMode,
    /// RGBController_Lenovo_Gen7_8.cpp:480-487). A session also reads
    /// which profile is active every 2 s, so the crash path writes the
    /// profile back only while it is the active one. A hand-back while
    /// another profile is active switches to the session's profile, writes
    /// it back and switches back, waiting the 100 ms after each switch that
    /// Lenovo Legion Toolkit waits (SetProfileAsync).</para>
    ///
    /// <para>Every Gen 10 write lands in the keyboard's memory, so a changed
    /// picture goes out once it has held for 150 ms, the spacing of
    /// legion-spectrum-control's live preview (spectrum-web.py previewHW),
    /// and no sooner than 500 ms after the write before it. A picture that
    /// never holds, a rainbow or a light that follows the sound, leaves the
    /// keyboard on the last picture that did.</para>
    ///
    /// <para>The key map. A per-key keyboard reports its grid and the key
    /// code in each cell: GET_KEY_COUNT, then GET_KEY_PAGE for each row and
    /// once more for the keys outside the grid (GetKeyMapAsync,
    /// SpectrumKeyboardBacklightController.cs:427-468). A key code is the
    /// light's address. Lenovo Legion Toolkit takes a board with key 0xA9
    /// as JIS and one with key 0xA8 as ISO (GetKeyboardLayoutAsync, lines
    /// 113-118) and draws each (SpectrumKeyboardISOControl.xaml and
    /// SpectrumKeyboardJisControl.xaml). OpenRGB keeps an ANSI table only.
    /// A session reads the map when it starts, lights the keys the keyboard
    /// reports, and names them by its layout (<see cref="Session.Map"/>).
    /// The worker publishes that map from then on, for as long as the
    /// keyboard stays, so an ANSI board shows its own keys and an ISO or
    /// JIS board shows its own, named. Until a session has read the map,
    /// the published map holds OpenRGB's lights and, unnamed, the keys
    /// only an ISO or JIS board has.</para>
    ///
    /// <para>Windows Dynamic Lighting. The same keyboards also offer a
    /// LampArray interface (048D:C193 on the owner's Legion, a second
    /// interface of 048D:C195 itself on a Legion Pro 5 16IAX10), and an option
    /// in the BIOS decides whether it takes colors. With Dynamic Lighting
    /// off there, the interface takes none, even while the signed-in user's
    /// switch for it reads on, and this controller lights the keys. With it
    /// on, the interface shows what a host sends as it comes: a color cycle
    /// sent as one range update every 50 ms played on the owner's Legion.
    /// Every report either collection answers read the same under both
    /// settings, save the one that echoes the last lamp asked about, so a
    /// Gen 10 row lights through both, and colors that change show through
    /// the interface wherever the firmware lets it, as for any controller
    /// that keeps still pictures (<see cref="PeripheralLinker.ChooseLighting"/>).
    /// That BIOS option was seen on one laptop, so no line a user reads names
    /// it as fact. The Legion Pro
    /// 5 16IAX10's interface took colors on Linux while the keyboard's own
    /// effect stayed on top (hid-rgb-ctl issue 10), which is what the owner's
    /// does with the option off. That report did not look at the option, and
    /// that tool's README puts it down to a firmware mode, so that keyboard
    /// lights through both as well. Where Windows
    /// Dynamic Lighting lights that interface, this controller lights the
    /// keys alone. OpenRGB drives it with no regard to Windows, and Lenovo
    /// Legion Toolkit's maintained fork drives it while Windows Dynamic
    /// Lighting is on, warning only that Windows "might prevent" it
    /// (LenovoLegionToolkit-Team KeyboardBacklightPage.xaml.cs
    /// CheckDynamicLightingWarningAsync), so this path is chosen over that
    /// interface then (<see cref="HidLightingModel.LitBesideWindows"/>).</para>
    ///
    /// <para>Lenovo's programs. Lenovo Vantage, Legion Space and Legion
    /// Zone light the keyboard, and while any of them runs the keyboard is
    /// left to it (<see cref="VendorPrograms.LenovoSpectrum"/>): Lenovo
    /// Legion Toolkit's Spectrum page stands down for the same three. The
    /// Toolkit itself is not among them. On a Gen 10 machine it hides its
    /// one streamed effect, Aurora Sync
    /// (LenovoLegionToolkit-Team
    /// SpectrumKeyboardBacklightController.cs:211-214), and the profile
    /// keys only start that effect (lines 62-83), so it writes the
    /// keyboard only on the user's own action: lighting picked on its
    /// page, or an automation step the user set up
    /// (LenovoLegionToolkit.Lib.Automation/Steps/SpectrumKeyboardBacklight*AutomationStep.cs).
    /// That is the user choosing that lighting, as with a profile
    /// key. Before Gen 10 the Toolkit streams Aurora Sync only on a profile
    /// the user gave that effect (StartAuroraIfNeededAsync,
    /// LenovoLegionToolkit SpectrumKeyboardBacklightController.cs:333-361),
    /// the user's choice again.</para>
    /// </summary>
    internal sealed class LenovoLegionLighting : IHidLightingFamily
    {
        public static readonly LenovoLegionLighting Instance = new();

        internal const ushort VendorId = 0x048D;
        internal const ushort UsagePage = 0xFF89;
        internal const ushort Usage = 0x07;

        /// <summary>OpenRGB's PACKET_SIZE, the report ID included, and the
        /// feature length the owner's controller reports.</summary>
        internal const int PacketSize = 960;
        internal const byte ReportId = 0x07;
        internal const byte DirectFrameOpcode = 0xA1;
        internal const byte KeyCount = 0xC4;
        internal const byte KeyPage = 0xC5;
        internal const byte SwitchProfile = 0xC8;
        internal const byte GetActiveProfile = 0xCA;
        internal const byte SaveProfile = 0xCB;
        internal const byte GetProfile = 0xCC;

        /// <summary>SET_DIRECT_MODE (LenovoUSBController_Gen7_8.h:33).</summary>
        internal const byte SetDirectMode = 0xD0;

        /// <summary>The product IDs (LenovoDevices.h:34-41).</summary>
        internal const ushort Legion7SGen7 = 0xC977;
        internal const ushort Legion7Gen7 = 0xC978;
        internal const ushort Legion7Gen8 = 0xC988;
        internal const ushort Legion7SGen8 = 0xC987;
        internal const ushort Legion7Gen9 = 0xC997;
        internal const ushort Legion7Gen9H = 0xC998;
        internal const ushort Legion7Gen10 = 0xC197;
        internal const ushort Legion5Gen10 = 0xC195;

        /// <summary>The highest profile an answer may name: Lenovo Legion
        /// Toolkit switches to profiles 0 to 6 and refuses any other
        /// (SetProfileAsync).</summary>
        internal const byte MaxProfile = 6;

        /// <summary>The two frame formats.</summary>
        internal enum Format
        {
            /// <summary>0xC0 0x03 in bytes 2 and 3, the whole length in byte
            /// 2 of a SAVE_PROFILE frame.</summary>
            Gen7,

            /// <summary>The payload length in bytes 2 and 3.</summary>
            Gen10,
        }

        /// <summary>The header of OpenRGB's static group as GetLedGroups
        /// fills it for its Static mode with a color per light, in the order
        /// setLedsByGroup sends it (LenovoUSBController_Gen7_8.cpp:91-104):
        /// mode 0x0B (RGBController_Lenovo_Gen7_8.h:27), speed and spin 0,
        /// direction 0x03 for the mode's default MODE_DIRECTION_LEFT
        /// (RGBControllerInterface.h:84 and 339-341,
        /// RGBController_Lenovo_Gen7_8.cpp:744-753), color mode 0x02 (lines
        /// 794-797). Two profiles of the owner's keyboard hold OpenRGB's
        /// writes with this header.</summary>
        internal static ReadOnlySpan<byte> StaticHeader => new byte[]
        {
            0x06, 0x01, 0x0B, 0x02, 0x00, 0x03, 0x00, 0x04, 0x03, 0x05, 0x02, 0x06, 0x00,
        };

        /// <summary>The header of the Direct group OpenRGB stores when its
        /// Direct mode starts: mode 0x0D, Lenovo Legion Toolkit's
        /// LegionAuraSync (Native.cs:89) and OpenRGB's Direct mode
        /// (RGBController_Lenovo_Gen7_8.h:29), the rest as
        /// <see cref="StaticHeader"/>, since GetLedGroups fills both alike
        /// (RGBController_Lenovo_Gen7_8.cpp:692-803).</summary>
        internal static ReadOnlySpan<byte> StreamHeader => new byte[]
        {
            0x06, 0x01, 0x0D, 0x02, 0x00, 0x03, 0x00, 0x04, 0x03, 0x05, 0x02, 0x06, 0x00,
        };

        /// <summary>How long a changed Gen 10 picture holds before it is
        /// written: legion-spectrum-control writes its live preview at most
        /// once every 150 ms (spectrum-web.py previewHW).</summary>
        internal const int HoldMs = 150;

        /// <summary>The shortest time between two Gen 10 writes. The
        /// keyboard keeps every write in its memory and no reference streams
        /// this frame, so the bound is PadForge's.</summary>
        internal const int RepaintMs = 500;

        /// <summary>How often a Gen 7 to 9 stream sends a changed picture,
        /// Lenovo Legion Toolkit's Aurora Sync interval
        /// (SpectrumKeyboardBacklightController.cs:41).</summary>
        internal const int StreamRepaintMs = 60;

        /// <summary>The most lights one direct frame carries: five bytes a
        /// light behind the four-byte header: the Gen 10 loop stops there
        /// (setLedsDirect, LenovoUSBController_Gen7_8.cpp:186), and the
        /// loop before Gen 10 would write past the packet with more (line
        /// 210).</summary>
        internal const int MaxDirectLights = (PacketSize - 4) / 5;

        /// <summary>How often a read looks for the late answer to
        /// GET_PROFILE, and for how long. On the owner's keyboard the answer
        /// was there 100 ms after the request.</summary>
        internal const int AnswerPollMs = 20;
        internal const int AnswerWaitMs = 300;

        /// <summary>The wait after a profile switch, Lenovo Legion
        /// Toolkit's (SetProfileAsync), and after a write that a read then
        /// checks.</summary>
        internal const int SettleMs = 100;

        /// <summary>How often a picture that waits for the keyboard to come
        /// back to its profile asks again.</summary>
        internal const int AwayMs = 2000;

        /// <summary>How often a picture that waits for its backup to reach
        /// the settings file looks again.</summary>
        internal const int SavingMs = 100;

        /// <summary>How many times in a row a run tries to put back a profile
        /// an earlier run left, before it waits for the count to clear
        /// (<see cref="KeyboardProfileBackups.FailuresClearMs"/>).</summary>
        internal const int RepairTries = 3;

        /// <summary>How often a session whose profile may hold a picture of
        /// PadForge's reads which profile is active. Lenovo Legion Toolkit
        /// learns of a profile key from the keyboard's key events
        /// (SpectrumKeyboardBacklightController.cs:71-92). PadForge reads the
        /// active profile, which changes nothing on the keyboard.</summary>
        internal const int RecheckMs = 2000;

        /// <summary>The widest key page and the most rows a key map answer
        /// may name: a page answer holds 32 items
        /// (LENOVO_SPECTRUM_GET_KEY_PAGE_RESPONSE, Native.cs:244-255), and
        /// the largest grid Lenovo Legion Toolkit draws is 22 by 9
        /// (GetKeyboardLayoutAsync, lines 104-110).</summary>
        internal const int MaxKeyPageWidth = 32;
        internal const int MaxKeyRows = 16;

        public string Name => "Lenovo Legion";

        /// <summary>One model's protocol: its frame format, whether it
        /// streams, and whether it reads the keyboard's key map.</summary>
        internal sealed record Spec(string Product, Format Format, bool Stream, bool ReadsKeyMap,
            ISet<int> Addresses, ISet<int> Base, IReadOnlyList<LightInfo> Lights);

        private static readonly Dictionary<ushort, Lazy<HidLightingModel>> s_models = new()
        {
            [Legion7SGen7] = new(() => Model("Lenovo Legion 7S Gen 7", Legion7SGen7, Gen7Lights(Legion7SGen7), Format.Gen7, true)),
            [Legion7Gen7] = new(() => Model("Lenovo Legion 7 Gen 7", Legion7Gen7, Gen7Lights(Legion7Gen7), Format.Gen7, true)),
            [Legion7Gen8] = new(() => Model("Lenovo Legion 7 Gen 8", Legion7Gen8, Gen7Lights(Legion7Gen8), Format.Gen7, true)),
            [Legion7SGen8] = new(() => Model("Lenovo Legion 7S Gen 8", Legion7SGen8, Gen7Lights(Legion7SGen8), Format.Gen7, true)),
            [Legion7Gen9] = new(() => Model("Lenovo Legion 7 Gen 9", Legion7Gen9, Gen7Lights(Legion7Gen9), Format.Gen7, true)),
            [Legion7Gen9H] = new(() => Model("Lenovo Legion 7 Gen 9", Legion7Gen9H, Gen7Lights(Legion7Gen9H), Format.Gen7, true)),
            [Legion7Gen10] = new(() => Model("Lenovo Legion 7 Gen 10", Legion7Gen10, Gen10Lights(), Format.Gen10, false)),
            [Legion5Gen10] = new(() => Model("Lenovo Legion 5 Gen 10", Legion5Gen10, Legion5Gen10Lights(), Format.Gen10, false)),
        };

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId
                || collection.UsagePage != UsagePage || collection.Usage != Usage)
                return null;
            // A collection with shorter feature reports is not the one
            // OpenRGB opens.
            if (collection.FeatureReportLength < PacketSize) return null;
            return s_models.TryGetValue(collection.ProductId, out var model) ? model.Value : null;
        }

        private static HidLightingModel Model(string name, ushort productId, IReadOnlyList<LightInfo> baseLights,
            Format format, bool stream)
        {
            string product = $"lenovo:{VendorId:X4}:{productId:X4}";
            bool perKey = productId != Legion5Gen10;
            var lights = perKey ? WithRegionKeys(baseLights) : baseLights;
            var spec = new Spec(product, format, stream, perKey, LenovoLights.Addresses(lights),
                LenovoLights.Addresses(baseLights), lights);
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(lights, "OpenRGB LenovoDevices.h")
                {
                    Product = product,
                },
                LitBesideWindows = true,
                Programs = VendorPrograms.LenovoSpectrum,
                HoldMs = stream ? 0 : HoldMs,
                RepaintMs = stream ? StreamRepaintMs : RepaintMs,
                RecheckMs = RecheckMs,
                Begin = (io, wait) => Begin(io, wait, spec),
                Owed = _ => KeyboardProfileBackups.Get(product) != null
                             && KeyboardProfileBackups.Failures(product) < RepairTries,
                Repair = (io, wait) => Repair(io, wait, spec),
            };
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The zone IDs (LenovoDevices.h:1913-1985).</summary>
        internal const int KeyboardZone = 0;

        /// <summary>The keys of legion7_gen7and8_ansi_leds, in its order
        /// (LenovoDevices.h:1739-1855).</summary>
        internal static IReadOnlyList<LenovoLed> Keys => LenovoLights.Legion7Gen7and8AnsiLeds;

        /// <summary>The zones OpenRGB gives the Legion 7 Gen 10, in its
        /// order: the keyboard, the neon, the logo and the vents
        /// (RGBController_Lenovo_Gen7_8.cpp:401-428). A light's address is
        /// its zone's ID in the high byte and its own number in the low one
        /// (line 455).</summary>
        internal static IReadOnlyList<LightInfo> Gen10Lights() => LenovoLights.Build(new[]
        {
            (LenovoLights.Legion7Gen7and8KbdAnsi, LightKind.Edge),
            (LenovoLights.Legion7Gen7and8Neon, LightKind.Edge),
            (LenovoLights.LenovoLegion7gen7Logo, LightKind.Logo),
            (LenovoLights.LenovoLegion7gen10Vents, LightKind.Edge),
        });

        /// <summary>The zones OpenRGB gives a Gen 7 to 9 keyboard
        /// (RGBController_Lenovo_Gen7_8.cpp:401-422): the keyboard, the neon
        /// on every model, and on the Legion 7 Gen 7 the logo and its eight
        /// vents.</summary>
        internal static IReadOnlyList<LightInfo> Gen7Lights(ushort productId)
        {
            var zones = new List<(LenovoZone, LightKind)>
            {
                (LenovoLights.Legion7Gen7and8KbdAnsi, LightKind.Edge),
                (LenovoLights.Legion7Gen7and8Neon, LightKind.Edge),
            };
            if (productId == Legion7Gen7)
            {
                zones.Add((LenovoLights.LenovoLegion7gen7Logo, LightKind.Logo));
                zones.Add((LenovoLights.LenovoLegion7gen7Vents, LightKind.Edge));
            }
            return LenovoLights.Build(zones);
        }

        /// <summary>The Legion 5 Gen 10's 24 zones, four rows of six
        /// (RGBController_Lenovo_Gen7_8.cpp:31-78 and 404-407), zone ID 0.</summary>
        internal static IReadOnlyList<LightInfo> Legion5Gen10Lights()
            => LenovoLights.Build(new[] { (LenovoLights.Legion5gen10Kbd24zone, LightKind.Zone) });

        /// <summary>The keys only an ISO or a JIS board reports, by their
        /// codes in Lenovo Legion Toolkit's drawings: 0xA8, the ISO # key
        /// and the JIS ¥ key (SpectrumKeyboardISOControl.xaml:365,
        /// SpectrumKeyboardJisControl.xaml:211), and the JIS @ key, 無変換,
        /// 変換 and かな (SpectrumKeyboardJisControl.xaml:294 and 482-497).</summary>
        internal static readonly int[] RegionKeys = { 0xA8, 0x60, 0xA9, 0xAA, 0xAB };

        /// <summary><paramref name="lights"/> with the keys of
        /// <see cref="RegionKeys"/> after them, unnamed, since the name each
        /// takes depends on the board.</summary>
        internal static IReadOnlyList<LightInfo> WithRegionKeys(IReadOnlyList<LightInfo> lights)
        {
            var all = new List<LightInfo>(lights);
            foreach (int code in RegionKeys)
                all.Add(new LightInfo(KeyboardZone << 8 | code, LightKind.Key, 0, LightKeySource.None));
            return all;
        }

        /// <summary>A board's layout, as Lenovo Legion Toolkit tells it.</summary>
        internal enum Layout
        {
            Ansi,
            Iso,
            Jis,
        }

        /// <summary>JIS when the board reports key 0xA9, ISO when it reports
        /// 0xA8, ANSI otherwise (GetKeyboardLayoutAsync,
        /// SpectrumKeyboardBacklightController.cs:113-118).</summary>
        internal static Layout LayoutOf(ISet<int> keys)
            => keys.Contains(0xA9) ? Layout.Jis : keys.Contains(0xA8) ? Layout.Iso : Layout.Ansi;

        /// <summary>The usage each key takes on an ISO board where it differs
        /// from OpenRGB's ANSI table, by its place in Lenovo Legion Toolkit's
        /// ISO drawing: 0x4E sits left of Z, the ISO \ key, usage 0x64
        /// (SpectrumKeyboardISOControl.xaml:394), and 0xA8 right of ', the
        /// ISO # key, usage 0x32 (line 365) (HID Usage Tables 1.6, section
        /// 10).</summary>
        internal static readonly IReadOnlyDictionary<int, ushort> IsoUsages = new Dictionary<int, ushort>
        {
            [0x4E] = 0x64,
            [0xA8] = 0x32,
        };

        /// <summary>The usage each key takes on a JIS board where it differs
        /// from OpenRGB's ANSI table, by its place in Lenovo Legion Toolkit's
        /// JIS drawing. The @ key, 0x60, and the [ key, 0x4C, follow P where
        /// the ANSI [ and ] keys sit, so they send 0x2F and 0x30
        /// (SpectrumKeyboardJisControl.xaml:294-299). The ] key, 0x4D,
        /// follows :, the place of the ISO # key, 0x32 (line 370). ¥ is
        /// 0xA8 (line 211), 0x89. 無変換, 変換 and かな are 0xA9, 0xAA and
        /// 0xAB (lines 482-497), 0x8B, 0x8A and 0x88 (HID Usage Tables 1.6,
        /// section 10). The drawing puts 0x4E left of Z, where a JIS board
        /// has no key and its ろ key sits right of /, so 0x4E is left for Map
        /// Key Lights to name (0).</summary>
        internal static readonly IReadOnlyDictionary<int, ushort> JisUsages = new Dictionary<int, ushort>
        {
            [0x60] = 0x2F,
            [0x4C] = 0x30,
            [0x4D] = 0x32,
            [0x4E] = 0,
            [0xA8] = 0x89,
            [0xA9] = 0x8B,
            [0xAA] = 0x8A,
            [0xAB] = 0x88,
        };

        /// <summary>The model's lights as a board that reported
        /// <paramref name="keys"/> has them: its keyboard keys named by its
        /// layout, the keys it did not report left out, and keys it reported
        /// that no table holds added unnamed. The lights outside the
        /// keyboard zone stay as OpenRGB has them.</summary>
        internal static LightMap RegionMap(IReadOnlyList<LightInfo> lights, ISet<int> keys, string product)
        {
            var layout = LayoutOf(keys);
            var usages = layout switch
            {
                Layout.Iso => IsoUsages,
                Layout.Jis => JisUsages,
                _ => null,
            };
            var result = new List<LightInfo>(lights.Count);
            var seen = new HashSet<int>();
            foreach (var light in lights)
            {
                if (light.Address >> 8 != KeyboardZone)
                {
                    result.Add(light);
                    continue;
                }
                int code = light.Address;
                if (!keys.Contains(code)) continue;
                seen.Add(code);
                if (usages != null && usages.TryGetValue(code, out ushort usage))
                    result.Add(new LightInfo(code, LightKind.Key, usage, usage == 0 ? LightKeySource.None : LightKeySource.Device));
                else if (light.Kind == LightKind.Key)
                    result.Add(light with { KeySource = light.Usage == 0 ? LightKeySource.None : LightKeySource.Device });
                else
                    result.Add(light);
            }
            var extra = new List<int>();
            foreach (int code in keys)
                if (code >> 8 == KeyboardZone && !seen.Contains(code)) extra.Add(code);
            extra.Sort();
            foreach (int code in extra)
                result.Add(new LightInfo(code, LightKind.Key, 0, LightKeySource.None));
            return new LightMap(result, $"Lenovo Legion Toolkit key map, {layout}") { Product = product };
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>A frame in <paramref name="format"/>: the report ID, the
        /// opcode, the length bytes, then the payload, padded to the packet
        /// (LenovoUSBController_Gen7_8.cpp:18-27 and 134-166).</summary>
        internal static byte[] Frame(byte opcode, ReadOnlySpan<byte> payload, Format format = Format.Gen10)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = opcode;
            if (format == Format.Gen10)
            {
                frame[2] = (byte)(payload.Length & 0xFF);
                frame[3] = (byte)(payload.Length >> 8);
            }
            else
            {
                frame[2] = 0xC0;
                frame[3] = 0x03;
            }
            payload.CopyTo(frame.AsSpan(4));
            return frame;
        }

        /// <summary>GET_ACTIVE_PROFILE, whose answer carries the profile in
        /// byte 4 (getCurrentProfileId, LenovoUSBController_Gen7_8.cpp:230-238):
        /// {07, CA, C0, 03} with the payload length 1 on Gen 10.</summary>
        internal static byte[] ActiveProfileQuery(Format format = Format.Gen10)
            => Frame(GetActiveProfile, format == Format.Gen10 ? stackalloc byte[1] : ReadOnlySpan<byte>.Empty, format);

        /// <summary>SWITCH_PROFILE (LenovoUSBController_Gen7_8.cpp:259-265).</summary>
        internal static byte[] ProfileSwitch(byte profile, Format format = Format.Gen10)
            => Frame(SwitchProfile, stackalloc byte[] { profile }, format);

        /// <summary>SET_DIRECT_MODE off for a profile (setLedsDirectOff,
        /// LenovoUSBController_Gen7_8.cpp:151-166).</summary>
        internal static byte[] StreamOff(byte profile, Format format = Format.Gen10)
            => Frame(SetDirectMode, stackalloc byte[] { 0x02, profile }, format);

        /// <summary>SET_DIRECT_MODE on for a profile (setLedsDirectOn,
        /// LenovoUSBController_Gen7_8.cpp:134-149). Never sent to Gen 10.</summary>
        internal static byte[] StreamOn(byte profile)
            => Frame(SetDirectMode, stackalloc byte[] { 0x01, profile }, Format.Gen7);

        /// <summary>GET_PROFILE for <paramref name="profile"/>, with the
        /// length OpenRGB gives it on Gen 10, the packet less its header
        /// (getProfileSettings, LenovoUSBController_Gen7_8.cpp:267-272).</summary>
        internal static byte[] ContentsQuery(byte profile, Format format = Format.Gen10)
        {
            var frame = Frame(GetProfile, stackalloc byte[] { profile }, format);
            if (format == Format.Gen10)
            {
                frame[2] = (PacketSize - 4) & 0xFF;
                frame[3] = (PacketSize - 4) >> 8;
            }
            return frame;
        }

        /// <summary>GET_KEY_COUNT, parameter 7
        /// (LENOVO_SPECTRUM_GET_KEY_COUNT_REQUEST, Lenovo Legion Toolkit
        /// Native.cs:217-222, the header of lines 145-152 with size 0xC0).
        /// The maintained fork sends the same on Gen 10
        /// (LenovoLegionToolkit-Team Native.cs:222-226).</summary>
        internal static byte[] KeyCountQuery() => Frame(KeyCount, stackalloc byte[] { 0x07 }, Format.Gen7);

        /// <summary>GET_KEY_PAGE for row <paramref name="row"/>, parameter 7,
        /// or for the keys outside the grid, parameter 8 and row 0
        /// (LENOVO_SPECTRUM_GET_KEY_PAGE_REQUEST, Native.cs:236-242).</summary>
        internal static byte[] KeyPageQuery(byte row, bool outside = false)
            => Frame(KeyPage, stackalloc byte[] { (byte)(outside ? 0x08 : 0x07), row }, Format.Gen7);

        /// <summary>SAVE_PROFILE with <paramref name="contents"/> as
        /// GET_PROFILE answered them: on Gen 10 the frame that wrote them,
        /// sent again. Before Gen 10, behind the whole length in byte 2 and
        /// 0x03 in byte 3, as setLedsByGroup sends it there
        /// (LenovoUSBController_Gen7_8.cpp:83-84 and 128).</summary>
        internal static byte[] RestoreFrame(byte[] contents, Format format = Format.Gen10)
        {
            var frame = new byte[PacketSize];
            frame[0] = ReportId;
            frame[1] = SaveProfile;
            if (format == Format.Gen10)
            {
                contents.CopyTo(frame, 2);
            }
            else
            {
                frame[2] = (byte)(contents.Length + 2);
                frame[3] = 0x03;
                contents.AsSpan(2).CopyTo(frame.AsSpan(4));
            }
            return frame;
        }

        /// <summary>SAVE_PROFILE with a static group for each color of the
        /// picture, as OpenRGB's setLedsByGroup sends its Static mode
        /// (LenovoUSBController_Gen7_8.cpp:62-132): profile, 0x01, 0x01, then
        /// each group's number from 1, its header, its one color and its
        /// lights' addresses low byte first, and the length bytes (lines
        /// 122-129).</summary>
        internal static byte[] StaticFrame(byte profile, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors,
            Format format = Format.Gen10)
            => GroupFrame(profile, lights, Groups(lights, colors), StaticHeader, format);

        /// <summary>SAVE_PROFILE with the one black Direct group over every
        /// light, as OpenRGB stores it when its Direct mode starts: GetLedGroups
        /// puts every light in color 0 and keeps that group, the only one
        /// (RGBController_Lenovo_Gen7_8.cpp:692-726 and 794-797).</summary>
        internal static byte[] StreamGroupFrame(byte profile, IReadOnlyList<LightInfo> lights, Format format)
        {
            var all = new List<int>(lights.Count);
            for (int n = 0; n < lights.Count; n++) all.Add(n);
            return GroupFrame(profile, lights, new List<(int, List<int>)> { (0, all) }, StreamHeader, format);
        }

        private static byte[] GroupFrame(byte profile, IReadOnlyList<LightInfo> lights,
            List<(int Color, List<int> Lights)> groups, ReadOnlySpan<byte> header, Format format)
        {
            var frame = new byte[PacketSize];
            int at = 0;
            frame[at++] = ReportId;
            frame[at++] = SaveProfile;
            at += 2;
            frame[at++] = profile;
            frame[at++] = 0x01;
            frame[at++] = 0x01;
            for (int g = 0; g < groups.Count; g++)
            {
                var (color, members) = groups[g];
                frame[at++] = (byte)(g + 1);
                header.CopyTo(frame.AsSpan(at));
                at += header.Length;
                frame[at++] = 1;
                frame[at++] = (byte)(color >> 16);
                frame[at++] = (byte)(color >> 8);
                frame[at++] = (byte)color;
                frame[at++] = (byte)members.Count;
                foreach (int n in members)
                {
                    frame[at++] = (byte)(lights[n].Address & 0xFF);
                    frame[at++] = (byte)(lights[n].Address >> 8);
                }
            }
            if (format == Format.Gen10)
            {
                frame[2] = (byte)((at - 4) & 0xFF);
                frame[3] = (byte)((at - 4) >> 8);
            }
            else
            {
                frame[2] = (byte)at;
                frame[3] = 0x03;
            }
            return frame;
        }

        /// <summary>A direct frame before Gen 10: 07 A1 C0 03, then each
        /// light's address low byte first and its red, green and blue
        /// (setLedsDirect, LenovoUSBController_Gen7_8.cpp:201-219). At most
        /// <see cref="MaxDirectLights"/> lights.</summary>
        internal static byte[] DirectFrame(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = Frame(DirectFrameOpcode, ReadOnlySpan<byte>.Empty, Format.Gen7);
            int count = Math.Min(Math.Min(lights?.Count ?? 0, colors?.Count ?? 0), MaxDirectLights);
            int at = 4;
            for (int n = 0; n < count; n++)
            {
                int address = lights[n].Address;
                int color = colors[n];
                frame[at++] = (byte)(address & 0xFF);
                frame[at++] = (byte)(address >> 8);
                frame[at++] = (byte)(color >> 16);
                frame[at++] = (byte)(color >> 8);
                frame[at++] = (byte)color;
            }
            return frame;
        }

        /// <summary>The lights of each color, by their place in the map, the
        /// colors in the order they first appear. A black light is left out,
        /// so it stays dark, unless every light is black, as GetLedGroups
        /// keeps a black group only when it is the only one
        /// (RGBController_Lenovo_Gen7_8.cpp:684-691 and 721-726). Groups that
        /// would not fit one frame are merged two at a time, the nearest
        /// colors first, in the color of the larger. OpenRGB sends such
        /// groups on in further frames that repeat the header, and no
        /// reference reads a profile longer than one frame back, since
        /// GET_PROFILE answers with one report.</summary>
        internal static List<(int Color, List<int> Lights)> Groups(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            var groups = new List<(int Color, List<int> Lights)>();
            var byColor = new Dictionary<int, int>();
            for (int n = 0; n < count; n++)
            {
                int color = colors[n] & 0xFFFFFF;
                if (color == 0) continue;
                if (!byColor.TryGetValue(color, out int g))
                {
                    byColor[color] = g = groups.Count;
                    groups.Add((color, new List<int>()));
                }
                groups[g].Lights.Add(n);
            }
            if (groups.Count == 0)
            {
                var all = new List<int>(count);
                for (int n = 0; n < count; n++) all.Add(n);
                if (all.Count > 0) groups.Add((0, all));
                return groups;
            }
            while (groups.Count > 1 && SizeOf(groups) > PacketSize)
            {
                int a = 0, b = 1, nearest = int.MaxValue;
                for (int i = 0; i < groups.Count; i++)
                    for (int j = i + 1; j < groups.Count; j++)
                    {
                        int distance = Distance(groups[i].Color, groups[j].Color);
                        if (distance < nearest)
                        {
                            nearest = distance;
                            a = i;
                            b = j;
                        }
                    }
                var (colorA, lightsA) = groups[a];
                var (colorB, lightsB) = groups[b];
                int color = lightsB.Count > lightsA.Count ? colorB : colorA;
                lightsA.AddRange(lightsB);
                lightsA.Sort();
                groups[a] = (color, lightsA);
                groups.RemoveAt(b);
            }
            return groups;
        }

        /// <summary>The bytes a frame of <paramref name="groups"/> takes: the
        /// seven ahead of the groups, then per group its number, header,
        /// color count, color, light count and two bytes per light.</summary>
        internal static int SizeOf(List<(int Color, List<int> Lights)> groups)
        {
            int size = 7;
            foreach (var group in groups) size += 1 + StaticHeader.Length + 1 + 3 + 1 + 2 * group.Lights.Count;
            return size;
        }

        private static int Distance(int x, int y)
        {
            int r = (x >> 16 & 0xFF) - (y >> 16 & 0xFF);
            int g = (x >> 8 & 0xFF) - (y >> 8 & 0xFF);
            int b = (x & 0xFF) - (y & 0xFF);
            return r * r + g * g + b * b;
        }

        // ─────────────────────────────────────────────
        //  The answers
        // ─────────────────────────────────────────────

        /// <summary>The profile in an answer to <see cref="ActiveProfileQuery"/>,
        /// or null when the answer is not one: OpenRGB reads byte 4 of any
        /// answer longer than four bytes, and here the answer must also
        /// carry the query's opcode, since a profile read wrong would be
        /// written over.</summary>
        internal static byte? ProfileOf(byte[] answer)
            => answer != null && answer.Length > 4 && answer[0] == ReportId && answer[1] == GetActiveProfile
                ? answer[4]
                : null;

        /// <summary>What <paramref name="profile"/> holds, in an answer to
        /// <see cref="ContentsQuery"/>: its length bytes, the profile byte
        /// and the two after it, and its effects, up to the first byte that
        /// starts none. Null when the answer is not one for that profile or
        /// an effect runs past it.</summary>
        internal static byte[] ContentsOf(byte[] answer, byte profile)
        {
            if (answer == null || answer.Length < 7 || answer[0] != ReportId || answer[1] != GetProfile
                || answer[4] != profile)
                return null;
            int end = EffectsEnd(answer, 7, out bool whole);
            if (!whole || end - 2 > KeyboardProfileBackups.MaxContents) return null;
            return answer.AsSpan(2, end - 2).ToArray();
        }

        /// <summary>Where the effects that start at <paramref name="start"/>
        /// end. An effect starts with its number, no lower than the one
        /// before (LENOVO_SPECTRUM_EFFECT_DESCRIPTION.FromBytes,
        /// LenovoLegionToolkit-Team Native.cs:374-380), and its header's
        /// fixed bytes, which every writer sends (setLedsByGroup lines
        /// 91-104, LENOVO_SPECTRUM_EFFECT_HEADER in Native.cs:158-179,
        /// spectrum-ctl.py build_effect_header). Bytes left after the last
        /// effect from another report start none. <paramref name="whole"/> is
        /// false when an effect runs past the buffer.</summary>
        private static int EffectsEnd(byte[] bytes, int start, out bool whole)
        {
            whole = true;
            int at = start, last = 1;
            while (at + 15 <= bytes.Length)
            {
                int number = bytes[at];
                if (number < last || !IsEffectHeader(bytes, at + 1)) break;
                int keysAt = at + 15 + 3 * bytes[at + 14];
                if (keysAt >= bytes.Length)
                {
                    whole = false;
                    break;
                }
                int next = keysAt + 1 + 2 * bytes[keysAt];
                if (next > bytes.Length)
                {
                    whole = false;
                    break;
                }
                last = number;
                at = next;
            }
            return at;
        }

        private static bool IsEffectHeader(byte[] bytes, int at)
            => bytes[at] == 0x06 && bytes[at + 1] == 0x01 && bytes[at + 3] == 0x02 && bytes[at + 5] == 0x03
               && bytes[at + 7] == 0x04 && bytes[at + 9] == 0x05 && bytes[at + 11] == 0x06;

        /// <summary>Whether <paramref name="contents"/> are a picture
        /// <see cref="StaticFrame"/> wrote, or the Direct group a stream
        /// stores: on Gen 10 the payload length in the length bytes, then
        /// static or Direct groups numbered from 1, one color each, over
        /// lights of the model, none twice. OpenRGB's Static mode writes the
        /// same, so a profile it wrote after a run of PadForge's ended without
        /// a hand-back reads as PadForge's. The length bytes of an answer
        /// before Gen 10 are not checked: no reference reads them.
        /// <paramref name="anyLight"/> checks the shape alone, for a board
        /// whose lights are not known yet.</summary>
        internal static bool IsOwnPicture(byte[] contents, byte profile, ISet<int> addresses, Format format = Format.Gen10,
            bool anyLight = false)
        {
            if (contents == null || contents.Length < 5
                || (format == Format.Gen10 && (contents[0] | contents[1] << 8) != contents.Length - 2)
                || contents[2] != profile || contents[3] != 0x01 || contents[4] != 0x01)
                return false;
            var seen = new HashSet<int>();
            int at = 5, number = 0;
            while (at < contents.Length)
            {
                if (at + 19 > contents.Length || contents[at] != number + 1 || contents[at + 14] != 1)
                    return false;
                var header = contents.AsSpan(at + 1, StaticHeader.Length);
                if (!header.SequenceEqual(StaticHeader) && !header.SequenceEqual(StreamHeader))
                    return false;
                int keys = contents[at + 18];
                if (keys == 0 || at + 19 + 2 * keys > contents.Length) return false;
                for (int k = 0; k < keys; k++)
                {
                    int address = contents[at + 19 + 2 * k] | contents[at + 20 + 2 * k] << 8;
                    if ((!anyLight && (addresses == null || !addresses.Contains(address))) || !seen.Add(address)) return false;
                }
                number++;
                at += 19 + 2 * keys;
            }
            return number > 0;
        }

        /// <summary>Whether <paramref name="contents"/> are the Direct group a
        /// stream stores.</summary>
        internal static bool HoldsStream(byte[] contents, byte profile, ISet<int> addresses, Format format = Format.Gen10)
            => IsOwnPicture(contents, profile, addresses, format)
               && contents.AsSpan(6, StreamHeader.Length).SequenceEqual(StreamHeader);

        /// <summary>The key codes in a key count answer and the key page
        /// answers after it, or null when an answer is not one: the count
        /// answer carries the rows in byte 5 and the keys per row in byte
        /// 6, a page answer its parameter and row in bytes 4 and 5 and then
        /// three bytes a key, an index and the code low byte first
        /// (LENOVO_SPECTRUM_GET_KEY_COUNT_RESPONSE and
        /// LENOVO_SPECTRUM_GET_KEY_PAGE_RESPONSE, Native.cs:224-255). A code
        /// of 0 is an empty cell.</summary>
        internal static bool KeysOf(byte[] answer, byte parameter, byte row, int width, ISet<int> into)
        {
            if (answer == null || answer.Length < 6 + 3 * width || answer[0] != ReportId || answer[1] != KeyPage
                || answer[4] != parameter || answer[5] != row)
                return false;
            for (int x = 0; x < width; x++)
            {
                int code = answer[7 + 3 * x] | answer[8 + 3 * x] << 8;
                if (code != 0) into.Add(code);
            }
            return true;
        }

        private static bool Same(byte[] x, byte[] y) => x != null && y != null && x.AsSpan().SequenceEqual(y);

        /// <summary>Whether two answers hold the same profile. Before Gen 10
        /// the length bytes are the writer's own count, so the bytes after
        /// them decide.</summary>
        private static bool SameProfile(byte[] x, byte[] y, Format format)
            => format == Format.Gen10
                ? Same(x, y)
                : x != null && y != null && x.Length >= 2 && y.Length >= 2 && x.AsSpan(2).SequenceEqual(y.AsSpan(2));

        // ─────────────────────────────────────────────
        //  The exchanges
        // ─────────────────────────────────────────────

        /// <summary>The active profile, or null when the exchange failed or
        /// three answers in a row were not one. Its answer comes at once on
        /// the owner's keyboard, and another program's exchange can come
        /// between the request and the read, hence the tries.</summary>
        internal static byte? ReadActiveProfile(IHidReportChannel io, Action<int> wait, Format format = Format.Gen10)
        {
            if (io == null) return null;
            var answer = new byte[Math.Max(PacketSize, io.FeatureReportLength)];
            for (int tries = 0; tries < 3; tries++)
            {
                if (tries > 0) wait(AnswerPollMs);
                if (!io.SetFeature(ActiveProfileQuery(format)) || !io.GetFeature(ReportId, answer)) return null;
                if (ProfileOf(answer) is byte profile && profile <= MaxProfile) return profile;
            }
            return null;
        }

        /// <summary>Sends <paramref name="request"/> and reads until an
        /// answer passes <paramref name="accept"/>, for at most
        /// <see cref="AnswerWaitMs"/>. False when an exchange failed or no
        /// answer passed.</summary>
        private static bool Ask(IHidReportChannel io, Action<int> wait, byte[] request, Func<byte[], bool> accept)
        {
            var answer = new byte[Math.Max(PacketSize, io.FeatureReportLength)];
            if (!io.SetFeature(request)) return false;
            for (int waited = 0; ; waited += AnswerPollMs)
            {
                if (!io.GetFeature(ReportId, answer)) return false;
                if (accept(answer)) return true;
                if (waited >= AnswerWaitMs) return false;
                wait(AnswerPollMs);
            }
        }

        /// <summary>What <paramref name="profile"/> holds, once it reads as
        /// the active profile, since GET_PROFILE makes the profile it reads
        /// the active one. That read also leaves an answer of another kind
        /// standing, so an older answer to GET_PROFILE cannot pass for this
        /// one, and the late answer is looked for until it comes. Null when
        /// the profile is not active or no answer came.</summary>
        internal static byte[] ReadContents(IHidReportChannel io, Action<int> wait, byte profile, Format format = Format.Gen10)
        {
            if (ReadActiveProfile(io, wait, format) != profile) return null;
            byte[] contents = null;
            return Ask(io, wait, ContentsQuery(profile, format), a => (contents = ContentsOf(a, profile)) != null)
                ? contents
                : null;
        }

        /// <summary>Every key code the keyboard reports, in its grid and
        /// outside it, as Lenovo Legion Toolkit reads them
        /// (GetKeyMapAsync, SpectrumKeyboardBacklightController.cs:427-468).
        /// Null when an answer did not come or named a grid no answer can
        /// hold.</summary>
        internal static HashSet<int> ReadKeyMap(IHidReportChannel io, Action<int> wait)
        {
            int rows = 0, width = 0;
            if (!Ask(io, wait, KeyCountQuery(), a =>
                {
                    if (a[0] != ReportId || a[1] != KeyCount || a[4] != 0x07) return false;
                    rows = a[5];
                    width = a[6];
                    return true;
                }))
                return null;
            if (rows < 1 || rows > MaxKeyRows || width < 1 || width > MaxKeyPageWidth) return null;
            var keys = new HashSet<int>();
            for (int y = 0; y < rows; y++)
            {
                byte row = (byte)y;
                if (!Ask(io, wait, KeyPageQuery(row), a => KeysOf(a, 0x07, row, width, keys))) return null;
            }
            if (!Ask(io, wait, KeyPageQuery(0, outside: true), a => KeysOf(a, 0x08, 0, width, keys))) return null;
            return keys;
        }

        private static bool SwitchTo(IHidReportChannel io, Action<int> wait, byte profile, Format format)
        {
            if (!io.SetFeature(ProfileSwitch(profile, format))) return false;
            wait(SettleMs);
            return ReadActiveProfile(io, wait, format) == profile;
        }

        /// <summary>Writes <paramref name="contents"/> back to the active
        /// <paramref name="profile"/> and reads it again. True when it reads
        /// as they were, or at least no longer as a picture of PadForge's on
        /// <paramref name="addresses"/>, the lights the caller judged the
        /// picture by, so a keyboard that stores the bytes its own way is not
        /// written again and again.</summary>
        private static bool Restore(IHidReportChannel io, Action<int> wait, byte profile, byte[] contents, ISet<int> addresses, Spec spec)
        {
            if (!io.SetFeature(RestoreFrame(contents, spec.Format))) return false;
            wait(SettleMs);
            var now = ReadContents(io, wait, profile, spec.Format);
            return now != null && (SameProfile(now, contents, spec.Format) || !IsOwnPicture(now, profile, addresses, spec.Format));
        }

        /// <summary>SET_DIRECT_MODE off for a profile that holds the Direct
        /// group, ahead of the write that replaces the group, as OpenRGB
        /// turns its Direct mode off before it writes the next mode
        /// (DeviceUpdateMode, RGBController_Lenovo_Gen7_8.cpp:499-505 and
        /// 525-528). True when the profile holds no such group or the frame
        /// went out.</summary>
        private static bool StopStream(IHidReportChannel io, byte profile, byte[] contents, ISet<int> addresses, Spec spec)
            => !HoldsStream(contents, profile, addresses, spec.Format) || io.SetFeature(StreamOff(profile, spec.Format));

        /// <summary>The lights a picture of PadForge's in
        /// <paramref name="contents"/> may hold. The tables, unless the
        /// contents have a picture's shape over a light no table lists: a board
        /// can report keys none of its drawings hold
        /// (<see cref="RegionMap"/>), and a session lights them, so the
        /// keyboard's own key map is read and its keys count too. Null when
        /// that read did not answer, and then nothing is known about the
        /// contents and nothing may be dropped on their account.</summary>
        private static ISet<int> OwnAddresses(IHidReportChannel io, Action<int> wait, Spec spec, byte[] contents, byte profile)
        {
            if (!spec.ReadsKeyMap || IsOwnPicture(contents, profile, spec.Addresses, spec.Format)
                || !IsOwnPicture(contents, profile, null, spec.Format, anyLight: true))
                return spec.Addresses;
            var keys = ReadKeyMap(io, wait);
            keys?.UnionWith(spec.Addresses);
            return keys;
        }

        /// <summary>Puts <paramref name="backup"/> back, with
        /// <paramref name="active"/> the profile active now, and lets the
        /// backup go. The profile is written only while it holds a picture of
        /// PadForge's: one that holds its old contents again, or another
        /// program's, is left as it is. A backup of another profile is put
        /// back on that profile and the keyboard switched back to
        /// <paramref name="active"/>. True when nothing is owed any
        /// more.</summary>
        private static bool Settle(IHidReportChannel io, Action<int> wait, Spec spec,
            KeyboardProfileBackups.Backup backup, byte active)
        {
            byte profile = backup.Profile;
            if (profile > MaxProfile)
            {
                KeyboardProfileBackups.Drop(spec.Product);
                return true;
            }
            bool away = profile != active;
            if (away && !SwitchTo(io, wait, profile, spec.Format)) return false;
            var now = ReadContents(io, wait, profile, spec.Format);
            bool settled = now != null && SameProfile(now, backup.Contents, spec.Format);
            if (now != null && !settled && OwnAddresses(io, wait, spec, now, profile) is { } addresses)
            {
                settled = !IsOwnPicture(now, profile, addresses, spec.Format)
                          || (StopStream(io, profile, now, addresses, spec)
                              && Restore(io, wait, profile, backup.Contents, addresses, spec));
            }
            if (settled) KeyboardProfileBackups.Drop(spec.Product);
            // Back to the profile the user chose, whatever the write did.
            if (away && !SwitchTo(io, wait, active, spec.Format)) return false;
            return settled;
        }

        /// <summary>Starts a session on the active profile. A backup an
        /// earlier run left of another profile is settled first, since the
        /// backup holds one profile at a time. A backup of this profile
        /// stands in for what the profile holds when that is a picture of
        /// PadForge's, and is dropped when another program wrote the profile
        /// since. The Direct group a killed stream left is settled at once,
        /// so no picture waits on it. A per-key keyboard's key map is read
        /// last: a session whose map read fails lights OpenRGB's
        /// lights.</summary>
        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, Spec spec)
        {
            string product = spec.Product;
            if (ReadActiveProfile(io, wait, spec.Format) is not byte active) return null;
            var backup = KeyboardProfileBackups.Get(product);
            if (backup is { } other && other.Profile != active)
            {
                if (KeyboardProfileBackups.Failures(product) >= RepairTries) return null;
                if (!Settle(io, wait, spec, other, active))
                {
                    KeyboardProfileBackups.NoteFailure(product);
                    return null;
                }
                backup = null;
            }
            var contents = ReadContents(io, wait, active, spec.Format);
            if (contents == null) return null;
            byte[] original = contents;
            bool ours = false;
            if (backup is { } own)
            {
                if (SameProfile(contents, own.Contents, spec.Format))
                {
                    original = own.Contents;
                }
                else
                {
                    var addresses = OwnAddresses(io, wait, spec, contents, active);
                    if (addresses == null) return null;
                    if (HoldsStream(contents, active, addresses, spec.Format))
                    {
                        if (KeyboardProfileBackups.Failures(product) >= RepairTries) return null;
                        if (!Settle(io, wait, spec, own, active))
                        {
                            KeyboardProfileBackups.NoteFailure(product);
                            return null;
                        }
                        original = own.Contents;
                    }
                    else if (IsOwnPicture(contents, active, addresses, spec.Format))
                    {
                        // An earlier run's picture, never handed back.
                        original = own.Contents;
                        ours = true;
                    }
                    else
                    {
                        KeyboardProfileBackups.Drop(product);
                    }
                }
            }
            var keys = spec.ReadsKeyMap ? ReadKeyMap(io, wait) : null;
            return new Session(spec, active, original, ours, keys);
        }

        /// <summary>Settles a backup an earlier run left, with no claim on
        /// the keyboard. True when nothing is owed any more.</summary>
        private static bool Repair(IHidReportChannel io, Action<int> wait, Spec spec)
        {
            if (KeyboardProfileBackups.Get(spec.Product) is not { } backup) return true;
            if (ReadActiveProfile(io, wait, spec.Format) is byte active && Settle(io, wait, spec, backup, active))
                return true;
            KeyboardProfileBackups.NoteFailure(spec.Product);
            return false;
        }

        /// <summary>A session on the profile active when it began.</summary>
        internal sealed class Session : HidLightingSession
        {
            private readonly Spec _spec;
            private readonly byte _profile;
            private readonly byte[] _original;

            /// <summary>The profile may hold a picture of PadForge's: one went
            /// out, or was tried, or an earlier run left one.</summary>
            private bool _ours;

            /// <summary>Another profile was active at the last read.</summary>
            private bool _away;

            /// <summary>A stream's direct mode is on and its group stored
            /// since the keyboard was last seen on the profile.</summary>
            private bool _entered;

            private bool _repaintDue;

            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(Spec spec, byte profile, byte[] original, bool ours, HashSet<int> keys)
            {
                _spec = spec;
                _profile = profile;
                _original = original;
                _ours = ours;
                Keys = keys;
                if (keys != null)
                {
                    Layout = LayoutOf(keys);
                    Map = RegionMap(spec.Lights, keys, spec.Product);
                }
                Track();
            }

            public byte Profile => _profile;

            /// <summary>What the profile held before PadForge wrote it.</summary>
            public byte[] Original => _original;

            /// <summary>The key codes the keyboard reported, or null when it
            /// was not asked or did not answer.</summary>
            public IReadOnlySet<int> Keys { get; }

            /// <summary>The board's layout, from <see cref="Keys"/>.</summary>
            public Layout Layout { get; }

            /// <summary>The lights as this board has them, named by its
            /// layout, or null when no key map was read, which keeps the
            /// model's (<see cref="HidLightingSession.Map"/>).</summary>
            public override LightMap Map { get; }

            public override bool Feature => true;

            /// <summary>A stream's keyboard came back to its profile, which
            /// holds the one black Direct group a stream stores, so the same
            /// picture has to go out again to enter the stream again. Lenovo
            /// Legion Toolkit restarts its stream on every profile change
            /// (SpectrumKeyboardBacklightController.cs, SetProfileAsync).
            /// True until a picture has entered it.</summary>
            internal override bool RepaintDue => _repaintDue;

            /// <summary>The restore, while the profile may hold a picture of
            /// PadForge's and is the active one, behind direct mode off for a
            /// stream. A hand-back on another profile takes switches the
            /// crash path cannot wait on, and is left to the backup.</summary>
            public override byte[][] CrashFrames => _crash;

            /// <summary>Whether a light at <paramref name="address"/> goes
            /// into the frames: one of OpenRGB's, or one the keyboard
            /// reported.</summary>
            internal bool Lights(int address) => _spec.Base.Contains(address) || (Keys?.Contains(address) ?? false);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // The backup reaches the settings file before the first
                // write it guards.
                if (KeyboardProfileBackups.Get(_spec.Product) is not { } held || held.Profile != _profile
                    || !Same(held.Contents, _original))
                    KeyboardProfileBackups.Take(_spec.Product, _profile, _original);
                if (!KeyboardProfileBackups.IsPersisted(_spec.Product))
                {
                    LaterMs = SavingMs;
                    return HidLightingPaint.Later;
                }
                var (picked, pickedColors) = Pick(lights, colors);
                // A stream is entered again after the keyboard was seen on
                // another profile: the recheck clears _entered when it finds
                // the keyboard away, and asks for the picture again when it
                // finds the keyboard back (RepaintDue), since the worker sends
                // no picture that did not change.
                if (!_spec.Stream || !_entered)
                {
                    if (ReadActiveProfile(io, wait, _spec.Format) is not byte active) return HidLightingPaint.Failed;
                    _away = active != _profile;
                    if (_away)
                    {
                        Track();
                        LaterMs = AwayMs;
                        return HidLightingPaint.Later;
                    }
                    // Marked first: a write that reports a failure may still
                    // have landed.
                    _ours = true;
                    Track();
                    if (!_spec.Stream)
                        return io.SetFeature(StaticFrame(_profile, picked, pickedColors, _spec.Format))
                            ? HidLightingPaint.Sent
                            : HidLightingPaint.Failed;
                    // OpenRGB's order: direct mode on, then the group
                    // (RGBController_Lenovo_Gen7_8.cpp:507-515).
                    if (!io.SetFeature(StreamOn(_profile)) || !io.SetFeature(StreamGroupFrame(_profile, picked, _spec.Format)))
                        return HidLightingPaint.Failed;
                    _entered = true;
                    _repaintDue = false;
                }
                return io.SetFeature(DirectFrame(picked, pickedColors)) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            private (IReadOnlyList<LightInfo>, IReadOnlyList<int>) Pick(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                var picked = new List<LightInfo>(count);
                var pickedColors = new List<int>(count);
                for (int n = 0; n < count; n++)
                {
                    if (!Lights(lights[n].Address)) continue;
                    picked.Add(lights[n]);
                    pickedColors.Add(colors[n]);
                }
                return (picked, pickedColors);
            }

            /// <summary>Reads which profile is active while the profile may
            /// hold a picture of PadForge's, so the restore stays on the
            /// crash path's record only while it is the active one.</summary>
            public override bool Recheck(IHidReportChannel io, Action<int> wait)
            {
                if (!_ours) return true;
                if (ReadActiveProfile(io, wait, _spec.Format) is not byte active) return false;
                bool wasAway = _away;
                _away = active != _profile;
                if (_away) _entered = false;
                else if (wasAway && _spec.Stream) _repaintDue = true;
                Track();
                return true;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_ours)
                {
                    // Nothing of PadForge's went out, so the profile holds
                    // its own contents.
                    if (KeyboardProfileBackups.Get(_spec.Product) is { } held && held.Profile == _profile
                        && Same(held.Contents, _original))
                        KeyboardProfileBackups.Drop(_spec.Product);
                    return true;
                }
                if (ReadActiveProfile(io, wait, _spec.Format) is not byte active) return false;
                if (!Settle(io, wait, _spec, new KeyboardProfileBackups.Backup(_profile, _original), active))
                    return false;
                _ours = false;
                Track();
                return true;
            }

            private void Track()
            {
                bool owed = _ours && !_away;
                if (owed == _crash.Length > 0) return;
                if (!owed)
                    _crash = Array.Empty<byte[]>();
                else if (_spec.Stream)
                    _crash = new[] { StreamOff(_profile, _spec.Format), RestoreFrame(_original, _spec.Format) };
                else
                    _crash = new[] { RestoreFrame(_original, _spec.Format) };
            }
        }
    }
}
