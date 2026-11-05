using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Keychron K3 V2 optical keyboard (#557), lit key by key as
    /// OpenRGB's KeychronKeyboardController lights it: 05AC:024F, interface
    /// 0, usage page 1, usage 6 (KeychronKeyboardControllerDetect.cpp:19-24
    /// and 44), 65-byte feature reports with report ID 0
    /// (KeychronKeyboardController.h:18-19, Send and Read,
    /// KeychronKeyboardController.cpp:271-297).
    ///
    /// <para>The frames. OpenRGB lights it only in its Custom mode, and each
    /// picture rewrites the keyboard's whole LED effect area, all 18 pages
    /// (SetMode, KeychronKeyboardController.cpp:58-214): customization on
    /// (04 18) and a read, the start of the effect pages (04 13, 0x12
    /// pages) and a read, five pages of the 20 effects with OpenRGB's
    /// defaults, three empty pages, nine pages of key colors, the page of
    /// the active effect, the end of the pages (04 02) and a read, and the
    /// start of the effect (04 F0), each report and read followed by 10 ms.
    /// The VSG Mintaka's controller is this one, "based on
    /// KeychronKeyboardController" (MintakaKeyboardController.cpp:4-5),
    /// and the two send the same bytes: SetMode, Send and Read are line for
    /// line the same, and the 20 effects and their defaults are the same
    /// (RGBController_KeychronKeyboard.cpp:441-591 against
    /// RGBController_MintakaKeyboard.cpp:90-240). So the frames come from
    /// <see cref="MintakaKeyboardLighting.Picture"/>, and only the keys
    /// differ.</para>
    ///
    /// <para>The keys. OpenRGB's k3_keychron table (RGBController_KeychronKeyboard.cpp:211-410):
    /// LEDs in the order SetupZones walks the 6 by 16 grid, column by
    /// column (lines 604-650), each named by the grid cell's index into the
    /// names, and each LED's color at four times its led_sequence_positions
    /// place (SetLedSequencePositions and SetMode,
    /// KeychronKeyboardController.cpp:53-56 and 178-185). Two names of the
    /// table are wrong for their cells: index 64, between "-" and
    /// Backspace in the number row (grid row 1, column 12), is named
    /// Number Pad +, and index 67, between "/" and Up Arrow in the shift
    /// row (row 4, column 12), is named Left Shift, which the table also
    /// gives index 4. Here they are "=" and Right Shift, the keys at those
    /// cells of an ANSI board, so no key frame lights two keys as one. The
    /// "Light" key, index 78, has no usage and is an Other light.</para>
    ///
    /// <para>What the writes do. OpenRGB marks the Custom mode as one that
    /// saves by itself (MODE_FLAG_AUTOMATIC_SAVE, line 444), and every
    /// picture writes the stored effect table whole, so the effects the
    /// user had set are replaced from the first picture on, which the row's
    /// line says (<see cref="HidLightingModel.ReplacesStoredEffects"/>). The
    /// new ones are white where they take a color, for the reason
    /// <see cref="MintakaKeyboardLighting"/> gives. A picture goes out once it
    /// has held for 150 ms and no sooner than 500 ms after the one before, the
    /// spacing PadForge keeps for every write that lands in a keyboard's memory
    /// (<see cref="LenovoLegionLighting.HoldMs"/>).</para>
    ///
    /// <para>Windows. The collection OpenRGB registers is the keyboard
    /// collection itself, which Windows holds and opens for another program
    /// with no access right only. Feature reports pass on that handle
    /// (hidapi windows/hid.c:1009-1026), and they are all this keyboard
    /// takes, so its model tells the worker to keep it
    /// (<see cref="HidLightingModel.HeldByWindows"/>).</para>
    ///
    /// <para>Hand-back. No source reads the effect area back, and OpenRGB
    /// sends nothing when it closes (the destructor, lines 597-602), so the
    /// keyboard keeps PadForge's last picture in its Custom mode until
    /// another program or its own controls change it, and the effects it held
    /// before the first picture do not come back. No source names a program
    /// that lights it.</para>
    /// </summary>
    internal sealed class KeychronKeyboardLighting : IHidLightingFamily
    {
        public static readonly KeychronKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "KeychronKeyboardController";

        /// <summary>KEYCHRON_KEYBOARD_VID (KeychronKeyboardControllerDetect.cpp:19).</summary>
        internal const ushort VendorId = 0x05AC;

        /// <summary>The grid's width and height (RGBController_KeychronKeyboard.cpp:213-214).</summary>
        internal const int Width = 16;
        internal const int Height = 6;

        /// <summary>The two names the table gets wrong, by name index, and
        /// the keys at their cells (see the class comment).</summary>
        internal static readonly IReadOnlyDictionary<int, string> NameFixes = new Dictionary<int, string>
        {
            [64] = "Key: =",
            [67] = "Key: Right Shift",
        };

        public string Name => "Keychron";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.FeatureReportLength < MintakaKeyboardLighting.PacketSize)
                return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The LEDs in SetupZones's order, column by column, each
        /// at its color buffer place (lines 627-646).</summary>
        internal static List<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(KeyboardLedNames.KeychronPlaces.Length);
            int led = 0;
            for (int w = 0; w < Width; w++)
            {
                for (int h = 0; h < Height; h++)
                {
                    int key = KeyboardLedNames.KeychronMatrix[h * Width + w];
                    if (key < 0) continue;
                    if (led < KeyboardLedNames.KeychronPlaces.Length)
                    {
                        string name = NameFixes.TryGetValue(key, out var fixedName) ? fixedName : KeyboardLedNames.KeychronNames[key];
                        if (KeyboardLedNames.Named(KeyboardLedNames.KeychronPlaces[led], name) is { } light)
                            lights.Add(light);
                    }
                    led++;
                }
            }
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_KeychronKeyboard.cpp")
            {
                Product = $"keychron:{VendorId:X4}:{productId:X4}",
            },
            HoldMs = LenovoLegionLighting.HoldMs,
            RepaintMs = LenovoLegionLighting.RepaintMs,
            HeldByWindows = true,
            ReplacesStoredEffects = true,
            Begin = (_, _) => new MintakaKeyboardLighting.Session(),
        };
    }
}
