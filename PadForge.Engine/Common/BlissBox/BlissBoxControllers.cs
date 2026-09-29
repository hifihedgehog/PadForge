using System.Collections.Generic;

namespace PadForge.Engine.Common.BlissBox
{
    /// <summary>
    /// The controllers a Bliss-Box port reads (issue #469): report 17's type
    /// code, a name for each, what each can do beyond buttons and sticks, and
    /// the names its buttons and axes carry on the port's joystick.
    ///
    /// <para>Type codes and meanings come from the API Tool's table
    /// (Form1.cs <c>controllerType[]</c>), main-original.c <c>getType</c> and
    /// DeviceBuddy's <c>BlissBox_lookUpName</c>, with the official
    /// compatibility list naming the ones the tools only abbreviate (HPD is
    /// the Master System's HPD-200 paddle, HAMMERHEAD InterAct's HammerheadFX,
    /// GRAVIS_EX the Gravis Xterminator, ZXSINC the ZX Spectrum's Sinclair
    /// joystick). Where the tools disagree, DeviceBuddy, the newer, wins: 66
    /// is the FM Towns pad. SAC and SPEEK appear in no source beyond their
    /// codes, and keep them.</para>
    ///
    /// <para>Button and axis names differ between firmware generations, so
    /// each comes from the source written for that generation: RetroArch's
    /// Bliss-Box 4-Play autoconfig files, stamped firmware 3.24, for 3.x, and
    /// DeviceBuddy's controller layouts for the GPA's 4.x. A layout's bit N
    /// is button N, its stream bytes 3 to 10 are axes 0 to 7 (X, Y, Z, Rx,
    /// Ry, Rz, slider, dial) and byte 11 is the hat. The two sources agree on
    /// the PlayStation, Nintendo 64, Saturn, TurboGrafx-16, 3DO and Wii
    /// Classic, and differ on the NES's A and B, the SNES's X and Y, the
    /// Genesis's C and Z, the Dreamcast's and GameCube's shoulders, and the
    /// Jaguar's A and C. The Jaguar keeps the joystick's names on 3.x, since
    /// nothing else settles which of its sources is right there. Only
    /// DeviceBuddy's layouts for the adapter's stream count (hat at byte 11),
    /// matched to a type by its name in BlissBox_lookUpName. A controller
    /// neither source lays out, or a button a source leaves unlabeled, keeps
    /// the joystick's own name.</para>
    ///
    /// <para>The names follow each adapter's default map. The GPA's
    /// alternate maps (one swaps Z with C and L with R), its custom maps and
    /// the API Tool's global mapper move buttons under them, and nothing in
    /// report 17 says which map is active.</para>
    ///
    /// <para>Names are invariant English strings that
    /// MappingDisplayResolver translates, as <see cref="GamepadObjectNames"/>'
    /// are. Letter and numeral legends (A, II, L1) stay as printed.</para>
    /// </summary>
    public static class BlissBoxControllers
    {
        public const byte TypeAtari = 0;
        public const byte TypeDreamcastAscii = 15;
        public const byte TypeDreamcast = 16;
        public const byte TypeNintendo64 = 19;
        public const byte TypePlayStationDigital = 65;
        public const byte TypeDualShock = 115;
        public const byte TypeDualShock2 = 121;
        public const byte TypeUnflashed = 255;

        /// <summary>The first of the four arrow buttons GPA 4.86 publishes
        /// once opposite directions are pressed (firmware 0x34A1 to 0x34B9),
        /// and where PadForge's native poll puts them on a 3.x adapter.</summary>
        public const int FirstArrowButton = 20;

        /// <summary>The arrow buttons, in the firmware's order: up, down,
        /// left, right (0x0556's bits 0x04, 0x08, 0x10, 0x20 shifted up by
        /// two).</summary>
        public static readonly string[] ArrowNames =
        {
            "Up Arrow", "Down Arrow", "Left Arrow", "Right Arrow",
        };

        /// <summary>The axis the first pressure lands on: after the eight a
        /// port's joystick declares.</summary>
        public const int FirstPressureAxis = 8;

        /// <summary>Report 21's twelve bytes in the DualShock 2's own order,
        /// as the API Tool draws them for firmware 3 and up (Form1.cs: Cross
        /// from pressure 6, Circle 5, Square 7, Triangle 4, up 2, down 3,
        /// left 1, right 0, and 8 to 11 on L1, R1, L2, R2).</summary>
        private static readonly string[] PressureNamesCurrent =
        {
            "D-Pad Right Pressure", "D-Pad Left Pressure", "D-Pad Up Pressure", "D-Pad Down Pressure",
            "Triangle Pressure", "Circle Pressure", "Cross Pressure", "Square Pressure",
            "L1 Pressure", "R1 Pressure", "L2 Pressure", "R2 Pressure",
        };

        /// <summary>The 2.x order, the API Tool's other branch: Cross from
        /// pressure 7, Circle 6, Square 4, Triangle 5 (Form1.cs picks it when
        /// the firmware major is 2 or less).</summary>
        private static readonly string[] PressureNamesV2 =
        {
            "D-Pad Right Pressure", "D-Pad Left Pressure", "D-Pad Up Pressure", "D-Pad Down Pressure",
            "Square Pressure", "Triangle Pressure", "Circle Pressure", "Cross Pressure",
            "L1 Pressure", "R1 Pressure", "L2 Pressure", "R2 Pressure",
        };

        /// <summary>The name of each of report 21's twelve bytes for a
        /// firmware major version.</summary>
        public static IReadOnlyList<string> PressureNames(byte major)
            => major > 2 ? PressureNamesCurrent : PressureNamesV2;

        private static readonly Dictionary<byte, string> Names = new()
        {
            [0] = "Atari joystick",
            [1] = "ColecoVision controller",
            [2] = "Amstrad GX4000 pad",
            [3] = "Saturn pad",
            [4] = "Atari 7800 controller",
            [5] = "Vectrex controller",
            [6] = "Atari 5200 controller",
            [7] = "Master System paddle",
            [8] = "Saturn 3D Control Pad",
            [9] = "GameCube controller",
            [10] = "Pippin AtMark controller",
            [11] = "Jaguar controller",
            [12] = "PlayStation wheel",
            [13] = "Wii Nunchuk",
            [14] = "Intellivision controller",
            [15] = "Dreamcast ASCII pad",
            [16] = "Dreamcast controller",
            [17] = "NES controller",
            [18] = "GameCube wheel",
            [19] = "Nintendo 64 controller",
            [20] = "Genesis 3-button pad",
            [21] = "Genesis 6-button pad",
            [22] = "Master System pad",
            [23] = "TurboGrafx-16 pad",
            [24] = "Amiga CD32 pad",
            [25] = "3DO controller",
            [26] = "PC-FX pad",
            [27] = "SNES controller",
            [28] = "NES Zapper",
            [29] = "Virtual Boy controller",
            [30] = "Arkanoid controller",
            [31] = "Wii Classic Controller",
            [32] = "Wii MotionPlus",
            [33] = "CD-i controller",
            [34] = "SAC",
            [35] = "Dreamcast Twin Stick",
            [36] = "NES Power Pad",
            [37] = "3DO analog controller",
            [38] = "Gravis Xterminator",
            [39] = "SideWinder gamepad",
            [40] = "HammerheadFX",
            [41] = "Atari paddles",
            [42] = "Bally Astrocade controller",
            [43] = "Atari keypad",
            [44] = "ZX Spectrum joystick",
            [45] = "SPEEK",
            [46] = "PC gameport joystick",
            [47] = "SNES NTT Data Keypad",
            [48] = "ColecoVision Flashback controller",
            [49] = "Neo Geo controller",
            [50] = "Atari 5200 Trak-Ball",
            [51] = "neGcon",
            [52] = "Famicom controller",
            [53] = "Famicom Arkanoid controller",
            [54] = "TurboGrafx-16 6-button pad",
            [55] = "Wii drums",
            [56] = "Arcade stick",
            [57] = "Super Scope",
            [58] = "Saturn Virtua Gun",
            [59] = "Master System Light Phaser",
            [60] = "Dreamcast light gun",
            [61] = "Gemini paddles",
            [62] = "Dreamcast wireless pad",
            [63] = "Family Trainer mat",
            [64] = "Atari Trak-Ball",
            [65] = "PlayStation digital pad",
            [66] = "FM Towns pad",
            [67] = "Wii guitar",
            [68] = "DJ Hero turntable",
            [69] = "uDraw tablet",
            [70] = "Atari 2600 Trak-Ball",
            [71] = "Famicom NTT Data controller",
            [72] = "Fairchild Channel F controller",
            [73] = "Dreamcast fishing rod",
            [74] = "Samba de Amigo maracas",
            [75] = "PlayStation guitar",
            [77] = "Atari 7800 FB",
            [78] = "XE-1 AP",
            [79] = "ColecoVision wheel",
            [83] = "PlayStation flight stick",
            [115] = "DualShock",
            [119] = "PlayStation pad",
            [121] = "DualShock 2",
            [127] = "JogCon",
            [255] = "Atari joystick",
        };

        /// <summary>The controller's name, or its type number when no source
        /// names it.</summary>
        public static string Name(byte type)
            => Names.TryGetValue(type, out var name) ? name : $"Type {type}";

        /// <summary>A DualShock 2 answers report 21 with its twelve pressure
        /// bytes. The API Tool polls it for PSX_DS2 alone (BBAPI.cs
        /// controllerDataPol) and DeviceBuddy for type 121 alone.</summary>
        public static bool HasPressure(byte type) => type == TypeDualShock2;

        /// <summary>A Dreamcast pad, the controllers whose VMU the adapter
        /// draws: DeviceBuddy shows the screen for its "dreamcast" layout,
        /// types 15 and 16, and the API Tool for DC_PAD.</summary>
        public static bool HasScreen(byte type) => type == TypeDreamcast || type == TypeDreamcastAscii;

        /// <summary>An N64 controller, whose Controller Pak the API Tool's
        /// memory manager reads and writes.</summary>
        public static bool HasControllerPak(byte type) => type == TypeNintendo64;

        /// <summary>A PlayStation digital pad, which is what a PlayStation
        /// dance mat reports: its controller ID is 0x41, and the adapter's
        /// type codes for PlayStation pads are their controller IDs (0x41
        /// digital, 0x53 flight stick, 0x73 DualShock, 0x79 DualShock 2).</summary>
        public static bool IsPlayStationDigital(byte type) => type == TypePlayStationDigital;

        private sealed class Layout
        {
            public Dictionary<int, string> Buttons { get; init; } = new();
            public Dictionary<int, string> Axes { get; init; } = new();

            /// <summary>The hat is a D-pad, as every layout that draws one at
            /// byte 11 names it.</summary>
            public bool DPad { get; init; } = true;
        }

        private static readonly Dictionary<int, string> PlayStationButtons = new()
        {
            [0] = "Cross", [1] = "Circle", [2] = "Square", [3] = "Triangle",
            [4] = "Select", [5] = "Start", [6] = "L1", [7] = "R1", [8] = "L2", [9] = "R2",
            [14] = "L3", [15] = "R3",
        };

        private static readonly Dictionary<int, string> TwoSticks = new()
        {
            [0] = "Left Stick X", [1] = "Left Stick Y", [3] = "Right Stick X", [4] = "Right Stick Y",
        };

        private static readonly Dictionary<int, string> OneStick = new()
        {
            [0] = "Stick X", [1] = "Stick Y",
        };

        private static readonly Dictionary<int, string> Nintendo64Buttons = new()
        {
            [0] = "B", [1] = "A", [2] = "C-Left", [3] = "C-Down", [4] = "Z Trigger", [5] = "Start",
            [6] = "L", [7] = "R", [8] = "C-Up", [9] = "C-Right",
        };

        private static readonly Dictionary<int, string> SaturnButtons = new()
        {
            [0] = "A", [1] = "B", [2] = "X", [3] = "Y", [5] = "Start", [6] = "Z", [7] = "C",
            [8] = "L", [9] = "R",
        };

        private static readonly Dictionary<int, string> TurboGrafxButtons = new()
        {
            [0] = "II", [1] = "I", [4] = "Select", [5] = "Run",
        };

        private static readonly Dictionary<int, string> ThreeDoButtons = new()
        {
            [0] = "A", [1] = "B", [7] = "C", [4] = "X", [5] = "P", [8] = "L", [9] = "R",
        };

        private static readonly Dictionary<int, string> WiiClassicButtons = new()
        {
            [0] = "B", [1] = "A", [2] = "Y", [3] = "X", [4] = "-", [5] = "+",
            [6] = "L", [7] = "R", [8] = "ZL", [9] = "ZR",
        };

        private static readonly Dictionary<int, string> JaguarKeypad = new()
        {
            [8] = "Keypad 1", [9] = "Keypad 2", [10] = "Keypad 3", [11] = "Keypad 4",
            [12] = "Keypad 5", [13] = "Keypad 6", [14] = "Keypad 7", [15] = "Keypad 8",
            [16] = "Keypad 9", [17] = "Keypad *", [18] = "Keypad 0", [19] = "Keypad #",
        };

        private static readonly Layout PlayStation = new() { Buttons = PlayStationButtons, Axes = TwoSticks };
        private static readonly Layout PlayStationDigital = new() { Buttons = PlayStationButtons };
        private static readonly Layout AtariJoystick = new() { Buttons = new() { [0] = "Fire" } };
        private static readonly Layout OneDial = new() { Axes = new() { [7] = "Dial" }, DPad = false };

        /// <summary>Firmware 3.x: RetroArch's "Bliss-Box 4-Play TYPE Port
        /// 1.cfg" files (dinput, firmware 3.24). Button N is 0-based as there,
        /// and "h0" is the hat.</summary>
        private static readonly Dictionary<byte, Layout> Generation3 = new()
        {
            [0] = AtariJoystick,
            [255] = AtariJoystick,
            [1] = new()
            {
                Buttons = new()
                {
                    [0] = "Left Fire", [1] = "Right Fire",
                    [8] = "Keypad 1", [9] = "Keypad 2", [11] = "Keypad 3", [10] = "Keypad 4",
                    [13] = "Keypad 5", [12] = "Keypad 6", [15] = "Keypad 7", [14] = "Keypad 8",
                    [16] = "Keypad 9", [18] = "Keypad 0",
                },
                // RetroArch labels its hat Up, Down, Left and Right, not a D-pad.
                DPad = false,
            },
            [16] = new()
            {
                Buttons = new() { [0] = "A", [1] = "B", [2] = "X", [3] = "Y", [5] = "Start" },
                Axes = new() { [0] = "Stick X", [1] = "Stick Y", [2] = "Left Trigger", [5] = "Right Trigger" },
            },
            [9] = new()
            {
                Buttons = new() { [0] = "B", [1] = "A", [2] = "Y", [3] = "X", [4] = "Z", [5] = "Start" },
                Axes = new()
                {
                    [0] = "Left Stick X", [1] = "Left Stick Y", [3] = "C-Stick X", [4] = "C-Stick Y",
                    [6] = "Left Trigger", [7] = "Right Trigger",
                },
            },
            [20] = new() { Buttons = new() { [0] = "A", [1] = "B", [7] = "C", [5] = "Start" } },
            [21] = new()
            {
                Buttons = new()
                {
                    [0] = "A", [1] = "B", [7] = "C", [2] = "X", [3] = "Y", [6] = "Z", [4] = "Mode", [5] = "Start",
                },
            },
            [19] = new() { Buttons = Nintendo64Buttons, Axes = OneStick },
            [49] = new()
            {
                Buttons = new() { [0] = "A", [3] = "B", [1] = "C", [2] = "D", [4] = "Select", [5] = "Start" },
            },
            [17] = new() { Buttons = new() { [0] = "B", [1] = "A", [4] = "Select", [5] = "Start" } },
            [65] = PlayStationDigital,
            [115] = PlayStation,
            [121] = PlayStation,
            [3] = new() { Buttons = SaturnButtons },
            [8] = new() { Buttons = SaturnButtons, Axes = OneStick },
            [27] = new()
            {
                Buttons = new()
                {
                    [0] = "B", [1] = "A", [2] = "Y", [3] = "X", [4] = "Select", [5] = "Start", [6] = "L", [7] = "R",
                },
            },
            [23] = new() { Buttons = TurboGrafxButtons },
            [25] = new() { Buttons = ThreeDoButtons },
            [31] = new() { Buttons = WiiClassicButtons, Axes = TwoSticks },
        };

        /// <summary>Firmware 4 and up: DeviceBuddy's controllers/*.layout.
        /// Template labels on pads whose shoulders are L and R (DeviceBuddy's
        /// L1 and R1 shapes) take the pad's own names.</summary>
        private static readonly Dictionary<byte, Layout> Generation4 = new()
        {
            [0] = AtariJoystick,
            [255] = AtariJoystick,
            [6] = new()
            {
                Buttons = new()
                {
                    [0] = "Fire 1", [1] = "Fire 2", [5] = "Start", [4] = "Pause", [2] = "Reset",
                    [8] = "Keypad 1", [9] = "Keypad 2", [10] = "Keypad 3", [11] = "Keypad 4",
                    [12] = "Keypad 5", [13] = "Keypad 6", [14] = "Keypad 7", [15] = "Keypad 8",
                    [16] = "Keypad 9", [17] = "Keypad *", [18] = "Keypad 0", [19] = "Keypad #",
                },
                Axes = new() { [1] = "Stick X", [0] = "Stick Y" },
                DPad = false,
            },
            [7] = OneDial,
            [30] = OneDial,
            [41] = new() { Axes = new() { [7] = "Dial 1", [6] = "Dial 2" }, DPad = false },
            [42] = new() { Axes = new() { [7] = "Dial" } },
            [61] = new() { Axes = new() { [6] = "Dial" } },
            [10] = new()
            {
                Buttons = new() { [0] = "R", [1] = "G", [2] = "Y", [3] = "B" },
                Axes = OneStick,
            },
            [33] = new() { Buttons = new() { [0] = "1", [1] = "2", [2] = "3" } },
            [15] = new()
            {
                Buttons = new() { [0] = "A", [1] = "B", [2] = "X", [3] = "Y", [5] = "Start", [6] = "L", [7] = "R" },
                Axes = OneStick,
            },
            [16] = new()
            {
                Buttons = new() { [0] = "A", [1] = "B", [2] = "X", [3] = "Y", [5] = "Start", [6] = "L", [7] = "R" },
                Axes = OneStick,
            },
            [9] = new()
            {
                Buttons = new()
                {
                    [0] = "B", [1] = "A", [2] = "Y", [3] = "X", [4] = "Z", [5] = "Start", [6] = "L", [7] = "R",
                },
                Axes = new()
                {
                    [0] = "Left Stick X", [1] = "Left Stick Y", [3] = "C-Stick X", [4] = "C-Stick Y",
                    [2] = "Left Trigger", [5] = "Right Trigger",
                },
            },
            [46] = new()
            {
                Buttons = new() { [0] = "1", [1] = "2", [2] = "3", [3] = "4" },
                Axes = TwoSticks,
                DPad = false,
            },
            [20] = new() { Buttons = new() { [0] = "A", [1] = "B", [6] = "C", [5] = "Start" } },
            [21] = new()
            {
                Buttons = new()
                {
                    [0] = "A", [1] = "B", [6] = "C", [2] = "X", [3] = "Y", [7] = "Z", [5] = "Start", [4] = "Mode",
                },
            },
            [11] = new()
            {
                Buttons = new(JaguarKeypad) { [7] = "A", [1] = "B", [0] = "C" },
            },
            [49] = new() { Buttons = new() { [4] = "Select", [5] = "Start" } },
            [17] = new() { Buttons = new() { [0] = "A", [1] = "B", [4] = "Select", [5] = "Start" } },
            [19] = new() { Buttons = Nintendo64Buttons, Axes = OneStick },
            [13] = new()
            {
                Buttons = new() { [0] = "Z", [1] = "C" },
                Axes = new() { [1] = "Stick X", [0] = "Stick Y" },
                DPad = false,
            },
            [54] = new()
            {
                Buttons = new()
                {
                    [0] = "II", [1] = "I", [6] = "III", [3] = "IV", [2] = "V", [7] = "VI", [4] = "Select", [5] = "Run",
                },
            },
            [65] = PlayStationDigital,
            [83] = PlayStation,
            [115] = PlayStation,
            [119] = PlayStation,
            [121] = PlayStation,
            [3] = new() { Buttons = SaturnButtons },
            [8] = new()
            {
                Buttons = SaturnButtons,
                Axes = new() { [0] = "Stick X", [1] = "Stick Y", [2] = "Left Trigger", [5] = "Right Trigger" },
            },
            [27] = new()
            {
                Buttons = new()
                {
                    [0] = "B", [1] = "A", [2] = "X", [3] = "Y", [4] = "Select", [5] = "Start", [6] = "L", [7] = "R",
                },
            },
            [23] = new() { Buttons = TurboGrafxButtons },
            [66] = new() { Buttons = new() { [0] = "A", [1] = "B", [4] = "Select", [5] = "Run" } },
            [29] = new()
            {
                Buttons = new()
                {
                    [0] = "B", [1] = "A", [5] = "Select", [4] = "Start", [6] = "L", [7] = "R",
                    [8] = "Right D-Pad Up", [3] = "Right D-Pad Down", [2] = "Right D-Pad Left", [9] = "Right D-Pad Right",
                },
            },
            [31] = new()
            {
                Buttons = new(WiiClassicButtons) { [18] = "Home Button" },
                Axes = TwoSticks,
            },
            [25] = new() { Buttons = ThreeDoButtons },
            [78] = new()
            {
                Buttons = new() { [0] = "B", [1] = "A", [4] = "Select", [5] = "Start" },
                Axes = OneStick,
                DPad = false,
            },
        };

        private static Layout LayoutFor(byte type, byte major)
        {
            if (major >= 4) return Generation4.TryGetValue(type, out var g4) ? g4 : null;
            if (major == 3) return Generation3.TryGetValue(type, out var g3) ? g3 : null;
            return null;
        }

        /// <summary>True when a source lays this controller out for this
        /// firmware, so its objects carry names.</summary>
        public static bool HasLayout(byte type, byte major) => LayoutFor(type, major) != null;

        /// <summary>The name button <paramref name="index"/> carries for this
        /// controller, or null to keep the joystick's own.</summary>
        public static string ButtonName(byte type, byte major, int index)
            => LayoutFor(type, major) is { } layout && layout.Buttons.TryGetValue(index, out var name) ? name : null;

        /// <summary>The name axis <paramref name="index"/> carries for this
        /// controller, or null to keep the joystick's own.</summary>
        public static string AxisName(byte type, byte major, int index)
            => LayoutFor(type, major) is { } layout && layout.Axes.TryGetValue(index, out var name) ? name : null;

        /// <summary>"D-Pad" when the controller's hat is its D-pad, else null
        /// to keep the joystick's own name.</summary>
        public static string HatName(byte type, byte major)
            => LayoutFor(type, major) is { DPad: true } ? "D-Pad" : null;
    }
}
