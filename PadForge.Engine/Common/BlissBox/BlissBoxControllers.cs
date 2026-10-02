using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
    /// joystick). The two generations number two controllers differently,
    /// and each tool names its own generation's: GPA 4.86 types an Atari
    /// driving controller 12 in its DE-9 driver (0x2436) and returns 66 from
    /// the same driver (0x22D7), which DeviceBuddy calls drivingcontroller
    /// and TOWNS, while the 3.0 firmware returns 66 for a Wii extension
    /// (0x23A9 to 0x23BB), which the API Tool calls WII_DRUM, and the API
    /// Tool calls 12 PSX_WHEEL (<see cref="Name"/>). The 2.0 firmware's
    /// PlayStation codes are brought in line when report 17 is parsed
    /// (<see cref="BlissBoxProtocol.ParseInfo"/>). SPEEK appears in no source
    /// beyond its code, and keeps it.</para>
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
    /// matched to a type by its name in BlissBox_lookUpName. That function
    /// names 27, 49 and 54 "SNES", "NEO" and "PCEngine", which no layout file
    /// carries, so those three are matched here by hand to supernintendo,
    /// neogeo and pce. A controller neither source lays out, or a button a
    /// source leaves unlabeled, keeps the joystick's own name, except the
    /// GPA's button 18 on a PlayStation pad, which the firmware sends for
    /// Select and Start (<see cref="PlayStationGpaButtons"/>).</para>
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

        /// <summary>
        /// The first of the four arrow buttons, or -1 when this controller has
        /// none. Both generations send the four directions as buttons of their
        /// own once opposite directions have been pressed, which a D-pad
        /// cannot do and a dance mat does:
        /// <list type="bullet">
        /// <item>The 3.0 firmware ORs them into the second button byte,
        /// buttons 10 to 13 (0x3295 to 0x32A9), for every controller but the
        /// NES Zapper, which skips the D-pad code (0x321E). It clears its
        /// latch (0x0354) at power-up (0x2FAB), on the path that restarts its
        /// main loop (0x3671, back to 0x2FD8), and when it starts a search
        /// after a controller its search found (0x3163, gated on 0x0356,
        /// which only the search loop sets at 0x31AE). A pad found at
        /// power-up is found outside that loop (0x3096), so a latch it set
        /// holds for the next controller. PadForge's native poll uses the same
        /// four buttons.</item>
        /// <item>GPA 4.86 writes them into the third, buttons 20 to 23 (0x34A1
        /// to 0x34B9). Its latch (0x055E) lives in RAM, starts at power-up from
        /// EEPROM 0x3D (0x373A), which a settings command writes (0x2CFB to
        /// 0x2D02), and is never set by the Genesis 3-button pad or the FM
        /// Towns pad (0x349B to 0x34A0). Once it is on, the arrows go out on
        /// every poll (0x34BC), those two pads included, until power-off or
        /// until something clears it. Every poll of a ColecoVision
        /// controller, Super Action Controller or ColecoVision wheel (types 1,
        /// 34 and 79) clears it (0x23DD), and that path writes the third byte
        /// itself (0x23E5). The Atari driver clears it when it retypes a
        /// joystick as a Trak-Ball (0x2408 to 0x241B), and the two routines
        /// that restore the defaults clear it and store 0 at EEPROM 0x3D
        /// (0x2C6A and 0x2C6F, 0x36A0 and 0x36A5). The PC-FX pad's own inputs
        /// share two of those bits (0x16AA to 0x16BD). None of those six gets
        /// the names, and no GPA layout exists for the ColecoVision
        /// three.</item>
        /// </list>
        /// Only a controller whose layout names a D-pad gets them. One
        /// without a D-pad cannot press opposite directions, and the keypads
        /// of the Jaguar and the Atari 5200 sit on buttons 10 to 13 on 3.0
        /// (0x2099, 0x1BDA). A controller with a D-pad that no source lays
        /// out keeps numbered names, and the firmware's arrows arrive on those
        /// numbered buttons.
        /// </summary>
        public static int FirstArrowButton(byte type, byte major)
        {
            if (HatName(type, major) == null) return -1;
            if (major == 3) return type == 28 ? -1 : 10;
            if (major >= 4) return type is 20 or 26 or 66 ? -1 : 20;
            return -1;
        }

        /// <summary>The arrow buttons, in the firmware's order: up, down,
        /// left, right (the D-pad bits 0x04, 0x08, 0x10 and 0x20 both
        /// firmwares move up the button byte).</summary>
        public static readonly string[] ArrowNames =
        {
            "Up Arrow", "Down Arrow", "Left Arrow", "Right Arrow",
        };

        /// <summary>How many motors the adapter drives on this controller: one,
        /// command 4, for the GameCube, Dreamcast and N64 controllers and the
        /// Dreamcast fishing rod, and two, commands 4 and 5, for the DualShock
        /// and DualShock 2. The API Tool's rumble form shows two motor
        /// controls for the DualShock, DualShock 2, neGcon and JogCon
        /// (rumble.cs:185), but psx-spx's list of pads with motors leaves out
        /// the neGcon ("Controllers - Vibration/Rumble Control") and the
        /// compatibility list gives the JogCon "no force feed back support"
        /// on 3.31 and 4.71, so both take none. The form's note that the rod's second
        /// motor is not worked out stands: GPA 4.86 drives the rod's second
        /// source only at full power from command 5 (0x0E25 to 0x0E31) and
        /// the first at its strength from command 4 (0x0E32 to 0x0E3F), so
        /// the rod takes command 4 alone. Any other controller has none and is
        /// sent nothing: the 3.0 firmware skips a controller poll after every
        /// write (0x090B, 0x31C6).</summary>
        public static int MotorCount(byte type) => type switch
        {
            9 or 16 or 19 or 73 => 1,
            115 or 121 => 2,
            _ => 0,
        };

        /// <summary>The axis the first pressure lands on: after the eight a
        /// port's joystick declares.</summary>
        public const int FirstPressureAxis = 8;

        /// <summary>The name of each of report 21's twelve bytes: the
        /// pressures a DualShock 2 sends after its sticks, in the pad's own
        /// order (psx-spx, "Controllers - Analog Buttons (Dualshock2)"). Every
        /// firmware copies them through unchanged: 2.0 stores the pad's reply
        /// in order (0x21F7 to 0x2204) and copies the pressures to report 21
        /// (0x2313 to 0x231C), and 3.0 does the same (0x2646 to 0x2651). The
        /// API Tool draws them this way for firmware 3 and up (Form1.cs:
        /// Cross from pressure 6, Circle 5, Square 7, Triangle 4). Its branch
        /// for 2.x takes the face buttons from other bytes, which the 2.0
        /// firmware's straight copy does not bear out.</summary>
        public static readonly IReadOnlyList<string> PressureNames = new[]
        {
            "D-Pad Right Pressure", "D-Pad Left Pressure", "D-Pad Up Pressure", "D-Pad Down Pressure",
            "Triangle Pressure", "Circle Pressure", "Cross Pressure", "Square Pressure",
            "L1 Pressure", "R1 Pressure", "L2 Pressure", "R2 Pressure",
        };

        /// <summary>The native channel as PadForge frames it needs firmware 3
        /// or later. The API Tool's memory manager refuses anything older
        /// (memManager.cs: "3.0 is required for this feature!"), and 2.0
        /// frames the channel differently: three message bytes in the header
        /// and no use byte (0x0832 to 0x0862).</summary>
        public const byte NativeChannelMajor = 3;

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
            [12] = "Atari driving controller",
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
            [34] = "Super Action Controller",
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

        /// <summary>The two codes 3.x firmware numbers differently from GPA's
        /// (see the class notes).</summary>
        private static readonly Dictionary<byte, string> Names3x = new()
        {
            [12] = "PlayStation wheel",
            [66] = "Wii drums",
        };

        /// <summary>The controller's name on this firmware generation, or
        /// null when no source names it, for the App's type-number
        /// fallback.</summary>
        public static string Name(byte type, byte major)
        {
            if (major < 4 && Names3x.TryGetValue(type, out var name)) return name;
            return Names.TryGetValue(type, out name) ? name : null;
        }

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

        /// <summary>A stick and two analog triggers in Z and Rz, which both
        /// firmwares fill for the Dreamcast pad and the Saturn 3D pad.</summary>
        private static readonly Dictionary<int, string> OneStickAndTriggers = new()
        {
            [0] = "Stick X", [1] = "Stick Y", [2] = "Left Trigger", [5] = "Right Trigger",
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

        /// <summary>A GPA's PlayStation pads, plus button 18. Holding Start
        /// arms GPA 4.86's hotkey once its timer runs out (0x32C8 to 0x3340,
        /// "holding start for 2 seconds" in the API document), and with
        /// Select held as well the firmware drops both and sends button 18
        /// alone (0x33FA to 0x3405), unless a port setting skips those
        /// routines (0x06AC, 0x3613 to 0x362A). DeviceBuddy draws that button
        /// on the pad, unlabeled (playstation.layout BTN_HOME). It takes
        /// SDL's name for the role, the guide, as PadForge names an SDL
        /// gamepad's (<see cref="GamepadObjectNames.Button"/>). The 3.0
        /// firmware has no such swap.</summary>
        private static readonly Dictionary<int, string> PlayStationGpaButtons = new(PlayStationButtons)
        {
            [18] = "Guide",
        };

        private static readonly Layout PlayStationGpa = new() { Buttons = PlayStationGpaButtons, Axes = TwoSticks };
        private static readonly Layout PlayStationDigitalGpa = new() { Buttons = PlayStationGpaButtons };
        private static readonly Layout AtariJoystick = new() { Buttons = new() { [0] = "Fire" } };
        private static readonly Layout OneDial = new() { Axes = new() { [7] = "Dial" }, DPad = false };

        /// <summary>Firmware 3.x: RetroArch's "Bliss-Box 4-Play TYPE Port
        /// 1.cfg" files (dinput, firmware 3.24). Button N is 0-based as there,
        /// and "h0" is the hat. The GameCube's and the Saturn 3D pad's
        /// triggers follow the 3.0 firmware Bliss-Box distributes, which
        /// reports itself as 3.34 and copies them into Z and Rz, axes 2 and 5,
        /// as they come from the pad (GameCube 0x1042 to 0x104A, Saturn 0x19A4
        /// to 0x19DC). RetroArch's GameCube file binds its shoulders to
        /// unsigned axes 6 and 7, which RetroArch ignores. They would be the
        /// Slider and Dial, which the GameCube driver never writes, so they
        /// keep the center each report starts from (0x311B).</summary>
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
                Axes = OneStickAndTriggers,
            },
            [9] = new()
            {
                Buttons = new() { [0] = "B", [1] = "A", [2] = "Y", [3] = "X", [4] = "Z", [5] = "Start" },
                Axes = new()
                {
                    [0] = "Left Stick X", [1] = "Left Stick Y", [3] = "C-Stick X", [4] = "C-Stick Y",
                    [2] = "Left Trigger", [5] = "Right Trigger",
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
            [8] = new() { Buttons = SaturnButtons, Axes = OneStickAndTriggers },
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
        /// L1 and R1 shapes) take the pad's own names. The Dreamcast pad's
        /// analog triggers, which its layout leaves undrawn, follow the
        /// firmware: GPA 4.86 copies them into Z and Rz as they come (0x0DEA
        /// to 0x0DEC), beside the digital L and R it sets past 0xC8 (0x0DB2).</summary>
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
                Axes = OneStickAndTriggers,
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
            [65] = PlayStationDigitalGpa,
            [83] = PlayStationGpa,
            [115] = PlayStationGpa,
            [119] = PlayStationGpa,
            [121] = PlayStationGpa,
            [3] = new() { Buttons = SaturnButtons },
            [8] = new() { Buttons = SaturnButtons, Axes = OneStickAndTriggers },
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

        /// <summary>True for an axis this controller's layout names as a
        /// trigger. Every firmware copies an analog trigger from the pad as it
        /// comes, 0 released, so the axis rests at its low end and travels
        /// one way (the #443 rule). The GPA's own XInput mode copies the same
        /// bytes straight into the triggers (0x298F to 0x29C7).</summary>
        public static bool IsTriggerAxis(byte type, byte major, int index)
            => AxisName(type, major, index) is "Left Trigger" or "Right Trigger";

        /// <summary>"D-Pad" when the controller's hat is its D-pad, else null
        /// to keep the joystick's own name.</summary>
        public static string HatName(byte type, byte major)
            => LayoutFor(type, major) is { DPad: true } ? "D-Pad" : null;

        /// <summary>The roles in SDL's gamepad layout the default mapping
        /// gives a controller's inputs. South to Guide are the button
        /// positions 0 to 10 of <see cref="GamepadObjectNames.Button"/> and
        /// Misc2 its position 17. LeftX to RightTrigger are the axis
        /// positions 0 to 5 of <see cref="GamepadObjectNames.Axis"/>, and a
        /// trigger takes a button as readily as an axis.</summary>
        private enum Role : byte
        {
            South, East, West, North, LeftShoulder, RightShoulder, Back, Start,
            LeftStick, RightStick, Guide, Misc2,
            LeftX, LeftY, LeftTrigger, RightX, RightY, RightTrigger,
        }

        /// <summary>The role of each input a controller's layout names, keyed
        /// by that name, so an input lands by what it is on either firmware,
        /// whichever index each sends it on.</summary>
        private sealed class Placement
        {
            public Dictionary<string, Role> Inputs { get; init; } = new();

            /// <summary>The hat is the D-pad.</summary>
            public bool DPad { get; init; } = true;
        }

        /// <summary>SDL's PS3 driver: Cross is the south button, Circle east,
        /// Square west and Triangle north (SDL_gamepad.c, the Sony face
        /// style), L2 and R2 the triggers, L3 and R3 the stick buttons, Select
        /// the back button and the PS button the guide
        /// (SDL_hidapi_ps3.c).</summary>
        private static readonly Placement PlayStationPlacement = new()
        {
            Inputs = new()
            {
                ["Cross"] = Role.South, ["Circle"] = Role.East, ["Square"] = Role.West, ["Triangle"] = Role.North,
                ["L1"] = Role.LeftShoulder, ["R1"] = Role.RightShoulder,
                ["L2"] = Role.LeftTrigger, ["R2"] = Role.RightTrigger,
                ["L3"] = Role.LeftStick, ["R3"] = Role.RightStick,
                ["Select"] = Role.Back, ["Start"] = Role.Start, ["Guide"] = Role.Guide,
                ["Left Stick X"] = Role.LeftX, ["Left Stick Y"] = Role.LeftY,
                ["Right Stick X"] = Role.RightX, ["Right Stick Y"] = Role.RightY,
            },
        };

        /// <summary>SDL's mapping for Nintendo's SNES pad for the Switch, by
        /// position: B south, A east, Y west, X north (SDL_gamepad.c, with the
        /// pad's buttons on the Pro Controller's bits per hid-nintendo.c
        /// snescon_button_mappings).</summary>
        private static readonly Placement SnesPlacement = new()
        {
            Inputs = new()
            {
                ["B"] = Role.South, ["A"] = Role.East, ["Y"] = Role.West, ["X"] = Role.North,
                ["L"] = Role.LeftShoulder, ["R"] = Role.RightShoulder,
                ["Select"] = Role.Back, ["Start"] = Role.Start,
            },
        };

        /// <summary>SDL's mapping for Nintendo's NES pads for the Switch. SDL
        /// reads a pad without a diamond of four face buttons by its letters
        /// (SDL_hidapi_switch.c AlwaysUsesLabels), so A is south and B
        /// east.</summary>
        private static readonly Placement NesPlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["Select"] = Role.Back, ["Start"] = Role.Start,
            },
        };

        /// <summary>SDL's mapping for Nintendo's N64 pad for the Switch, by
        /// letter as for the NES: A south, B east, C-Down west, C-Left north,
        /// C-Up the back button, C-Right Misc 2, Z the left trigger
        /// (SDL_gamepad.c, with the pad's buttons on the Pro Controller's bits
        /// per hid-nintendo.c n64con_button_mappings).</summary>
        private static readonly Placement N64Placement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["C-Down"] = Role.West, ["C-Left"] = Role.North,
                ["C-Up"] = Role.Back, ["C-Right"] = Role.Misc2, ["Z Trigger"] = Role.LeftTrigger,
                ["L"] = Role.LeftShoulder, ["R"] = Role.RightShoulder, ["Start"] = Role.Start,
                ["Stick X"] = Role.LeftX, ["Stick Y"] = Role.LeftY,
            },
        };

        /// <summary>SDL's mapping for the Genesis pad for the Switch, by
        /// letter: A south, B east, X west, Y north, C the right shoulder, Z
        /// the left, Mode the right trigger (SDL_gamepad.c, with the pad's
        /// buttons on the Pro Controller's bits per hid-nintendo.c
        /// gencon_button_mappings).</summary>
        private static readonly Placement GenesisPlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["X"] = Role.West, ["Y"] = Role.North,
                ["C"] = Role.RightShoulder, ["Z"] = Role.LeftShoulder, ["Mode"] = Role.RightTrigger,
                ["Start"] = Role.Start,
            },
        };

        /// <summary>SDL's GameCube adapter mapping, by position: A south, X
        /// east, B west, Y north, Z the right shoulder, the analog L and R
        /// the triggers and the C-stick the right stick (SDL_gamepad.c, over
        /// SDL_hidapi_gamecube.c's button order). SDL puts the digital L and R
        /// on Misc 3 and Misc 4, which no default binds.</summary>
        private static readonly Placement GameCubePlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["X"] = Role.East, ["B"] = Role.West, ["Y"] = Role.North,
                ["Z"] = Role.RightShoulder, ["Start"] = Role.Start,
                ["Left Trigger"] = Role.LeftTrigger, ["Right Trigger"] = Role.RightTrigger,
                ["Left Stick X"] = Role.LeftX, ["Left Stick Y"] = Role.LeftY,
                ["C-Stick X"] = Role.RightX, ["C-Stick Y"] = Role.RightY,
            },
        };

        /// <summary>SDL's Wii driver, by position: B south, A east, Y west, X
        /// north, L and R the shoulders, ZL and ZR the triggers, minus the
        /// back button, plus Start and Home the guide (SDL_hidapi_wii.c
        /// GAMEPAD_BUTTON_DEFS).</summary>
        private static readonly Placement WiiClassicPlacement = new()
        {
            Inputs = new()
            {
                ["B"] = Role.South, ["A"] = Role.East, ["Y"] = Role.West, ["X"] = Role.North,
                ["L"] = Role.LeftShoulder, ["R"] = Role.RightShoulder,
                ["ZL"] = Role.LeftTrigger, ["ZR"] = Role.RightTrigger,
                ["-"] = Role.Back, ["+"] = Role.Start, ["Home Button"] = Role.Guide,
                ["Left Stick X"] = Role.LeftX, ["Left Stick Y"] = Role.LeftY,
                ["Right Stick X"] = Role.RightX, ["Right Stick Y"] = Role.RightY,
            },
        };

        /// <summary>SDL's Wii driver for the Nunchuk: C the left shoulder, Z
        /// the left trigger, its stick the left stick
        /// (SDL_hidapi_wii.c HandleNunchuckButtonData).</summary>
        private static readonly Placement NunchukPlacement = new()
        {
            Inputs = new()
            {
                ["C"] = Role.LeftShoulder, ["Z"] = Role.LeftTrigger,
                ["Stick X"] = Role.LeftX, ["Stick Y"] = Role.LeftY,
            },
            DPad = false,
        };

        // The pads below have no SDL mapping. RetroArch's Bliss-Box 4-Play
        // files (firmware 3.24) bind each to its RetroPad, and RetroArch's
        // own SDL3 table puts the RetroPad's B on SDL's south button, A east,
        // Y west and X north (input_autodetect_builtin.c SDL3_DEFAULT_BINDS).

        /// <summary>The Dreamcast pad: A south, B east, X west, Y north, the
        /// analog L and R the triggers. The ASCII pad's digital L and R take
        /// the triggers, which it has no analog ones for.</summary>
        private static readonly Placement DreamcastPlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["X"] = Role.West, ["Y"] = Role.North,
                ["Start"] = Role.Start,
                ["Left Trigger"] = Role.LeftTrigger, ["Right Trigger"] = Role.RightTrigger,
                ["L"] = Role.LeftTrigger, ["R"] = Role.RightTrigger,
                ["Stick X"] = Role.LeftX, ["Stick Y"] = Role.LeftY,
            },
        };

        /// <summary>The Saturn pads: A south, B east, X west, Y north, Z and C
        /// the shoulders, where SDL's Genesis mapping puts the same six
        /// buttons, and L and R the triggers, analog on the 3D Control
        /// Pad.</summary>
        private static readonly Placement SaturnPlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["X"] = Role.West, ["Y"] = Role.North,
                ["Z"] = Role.LeftShoulder, ["C"] = Role.RightShoulder,
                ["Left Trigger"] = Role.LeftTrigger, ["Right Trigger"] = Role.RightTrigger,
                ["L"] = Role.LeftTrigger, ["R"] = Role.RightTrigger,
                ["Start"] = Role.Start,
                ["Stick X"] = Role.LeftX, ["Stick Y"] = Role.LeftY,
            },
        };

        /// <summary>The Neo Geo pad: A south, B east, C west, D
        /// north.</summary>
        private static readonly Placement NeoGeoPlacement = new()
        {
            Inputs = new()
            {
                ["A"] = Role.South, ["B"] = Role.East, ["C"] = Role.West, ["D"] = Role.North,
                ["Select"] = Role.Back, ["Start"] = Role.Start,
            },
        };

        /// <summary>The TurboGrafx-16 pad: II south, I east. The 6-button pad
        /// shares them and keeps III to VI unbound.</summary>
        private static readonly Placement TurboGrafxPlacement = new()
        {
            Inputs = new()
            {
                ["II"] = Role.South, ["I"] = Role.East, ["Select"] = Role.Back, ["Run"] = Role.Start,
            },
        };

        /// <summary>The 3DO pad: B south, A west, C east, X the back button,
        /// P Start.</summary>
        private static readonly Placement ThreeDoPlacement = new()
        {
            Inputs = new()
            {
                ["B"] = Role.South, ["A"] = Role.West, ["C"] = Role.East,
                ["L"] = Role.LeftShoulder, ["R"] = Role.RightShoulder,
                ["X"] = Role.Back, ["P"] = Role.Start,
            },
        };

        /// <summary>The Jaguar pad: B south, A west, C east. The keypad stays
        /// unbound.</summary>
        private static readonly Placement JaguarPlacement = new()
        {
            Inputs = new() { ["B"] = Role.South, ["A"] = Role.West, ["C"] = Role.East },
        };

        /// <summary>The Atari joystick: its fire button south.</summary>
        private static readonly Placement AtariPlacement = new()
        {
            Inputs = new() { ["Fire"] = Role.South },
        };

        /// <summary>The ColecoVision controller: the right fire button south,
        /// the left east, the disc the D-pad. The keypad stays
        /// unbound.</summary>
        private static readonly Placement ColecoPlacement = new()
        {
            Inputs = new() { ["Right Fire"] = Role.South, ["Left Fire"] = Role.East },
        };

        private static readonly Dictionary<byte, Placement> Placements = new()
        {
            [0] = AtariPlacement,
            [255] = AtariPlacement,
            [1] = ColecoPlacement,
            [3] = SaturnPlacement,
            [8] = SaturnPlacement,
            [9] = GameCubePlacement,
            [11] = JaguarPlacement,
            [13] = NunchukPlacement,
            [15] = DreamcastPlacement,
            [16] = DreamcastPlacement,
            [17] = NesPlacement,
            [19] = N64Placement,
            [20] = GenesisPlacement,
            [21] = GenesisPlacement,
            [23] = TurboGrafxPlacement,
            [54] = TurboGrafxPlacement,
            [25] = ThreeDoPlacement,
            [27] = SnesPlacement,
            [31] = WiiClassicPlacement,
            [49] = NeoGeoPlacement,
            [65] = PlayStationPlacement,
            [83] = PlayStationPlacement,
            [115] = PlayStationPlacement,
            [119] = PlayStationPlacement,
            [121] = PlayStationPlacement,
        };

        /// <summary>A DualShock 2's pressures for the button pressure targets,
        /// in their order: the four face buttons south, east, west and north,
        /// the shoulders, then the D-pad's up, down, left and right.</summary>
        private static readonly string[] PressureTargetNames =
        {
            "Cross Pressure", "Circle Pressure", "Square Pressure", "Triangle Pressure",
            "L1 Pressure", "R1 Pressure",
            "D-Pad Up Pressure", "D-Pad Down Pressure", "D-Pad Left Pressure", "D-Pad Right Pressure",
        };

        /// <summary>
        /// Where this controller's inputs land in SDL's gamepad layout, for the
        /// default mapping, or null when no source lays the controller out for
        /// this firmware or places its inputs. SDL's own mapping for the
        /// console's pad comes first. The pads SDL has none for follow
        /// RetroArch's Bliss-Box files. An input named as an analog trigger
        /// takes its role before a digital button that shares it.
        /// <paramref name="firstPressureAxis"/> is the port's first pressure
        /// axis, -1 when twelve do not fit after its own.
        /// </summary>
        public static BlissBoxGamepadMap GamepadMap(byte type, byte major, int firstPressureAxis = -1)
        {
            if (LayoutFor(type, major) is not { } layout || !Placements.TryGetValue(type, out var placement))
                return null;
            var buttons = new string[BlissBoxGamepadMap.ButtonPositions];
            var axes = new string[BlissBoxGamepadMap.AxisPositions];
            foreach (var input in layout.Axes.OrderBy(i => i.Key)) Place(input.Value, "Axis ", input.Key);
            foreach (var input in layout.Buttons.OrderBy(i => i.Key)) Place(input.Value, "Button ", input.Key);

            int[] pressure = null;
            if (HasPressure(type) && firstPressureAxis >= 0)
                pressure = Array.ConvertAll(PressureTargetNames, name => firstPressureAxis + PressureIndex(name));
            return new BlissBoxGamepadMap(buttons, axes, placement.DPad, pressure);

            void Place(string name, string kind, int index)
            {
                if (!placement.Inputs.TryGetValue(name, out Role role)) return;
                string source = kind + index.ToString(CultureInfo.InvariantCulture);
                if (role >= Role.LeftX) axes[role - Role.LeftX] ??= source;
                else buttons[role == Role.Misc2 ? 17 : (int)role] ??= source;
            }
        }

        private static int PressureIndex(string name)
        {
            for (int i = 0; i < PressureNames.Count; i++)
                if (PressureNames[i] == name) return i;
            throw new ArgumentException(name, nameof(name));
        }
    }
}
