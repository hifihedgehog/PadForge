using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The LightSalt keyboard and keypad (#557), lit key by key as OpenRGB's
    /// LightSaltController lights them: 0483:5750 on interface 1, usage page
    /// 1, usage 0 (LightSaltControllerDetect.cpp:18-19 and 57), 33-byte
    /// output reports with report ID 0 and 32-byte input reports
    /// (LightSaltController.h:18-20).
    ///
    /// <para>Which device. 0483:5750 is STMicroelectronics' IDs, which other
    /// devices built on its chips carry too, so OpenRGB writes to a device
    /// only when its manufacturer and product strings both hold "Light&amp;Salt",
    /// and only then asks its class (LightSaltController.cpp:38-76). The
    /// model asks the same of the two strings before the worker lists the
    /// collection (<see cref="HidLightingModel.Identify"/>), so the family
    /// never writes to a device whose strings it has not seen.</para>
    ///
    /// <para>The model of the device. A session asks the class, 00 FE FF,
    /// and the model, 00 FE F5 06, each answered by a 32-byte input report
    /// within 100 ms; the class string "Light&amp;Salt_keyboard1" is the
    /// keyboard and "Light&amp;Salt_KEY_PAD" the keypad, and any other ends
    /// the session before anything is lit (QueryDeviceModel,
    /// QueryDeviceClass and SetDeviceType, lines 236-311; LightSaltControllerDetect.cpp:21-55).
    /// It then selects the Custom mode, the brightness 00 FE 05 and, 25 ms
    /// later, the mode 00 FE 03 00 00 (DeviceUpdateMode,
    /// RGBController_LightSalt.cpp:420-427; SetBrightness and SetMode,
    /// LightSaltController.cpp:195-234). OpenRGB's Custom mode starts at
    /// brightness 0x80 of 0xFF (RGBController_LightSalt.cpp:55-65), and
    /// PadForge sends 0xFF, since it dims a color itself. A picture is the
    /// color filter, 00 FE 04 and white, OpenRGB's default filter
    /// (SetupZones, line 370), then one report per row of each color set,
    /// 00, 03 plus the set, 01 plus the row times the columns, and the
    /// row's colors, a key's color lightened halfway to white in each set
    /// its deflection bits name, then the apply report 00 FE 01 and the sets
    /// (DeviceUpdateColors and DeflectColor, RGBController_LightSalt.cpp:16-32
    /// and 373-403; SetFilter, SetColors and ApplyColors,
    /// LightSaltController.cpp:131-176 and 210-222). Nothing of that saves:
    /// OpenRGB saves only on a manual save, with 00 FE F0 (SaveColors,
    /// lines 178-193; MODE_FLAG_MANUAL_SAVE, RGBController_LightSalt.cpp:59).</para>
    ///
    /// <para>The keys. The keyboard's 83 keys in four sets of nine rows of
    /// ten, and the keypad's 18 in one set of five rows of ten, each at the
    /// cell OpenRGB's led_indices gives it, with OpenRGB's names
    /// (RGBController_LightSaltKeyboard.cpp:18-207, RGBController_LightSaltKeypad.cpp:18-131).
    /// OpenRGB's Color Filter light is a tint over every key, not a light,
    /// and is left white.</para>
    ///
    /// <para>Hand-back. No source reads the mode the device ran before or
    /// returns it to it, and OpenRGB sends nothing when it closes, so the
    /// device keeps PadForge's last picture in its Custom mode. No source
    /// names a program that lights it.</para>
    /// </summary>
    internal sealed class LightSaltLighting : IHidLightingFamily
    {
        public static readonly LightSaltLighting Instance = new();

        internal const string OpenRgbFamily = "LightSaltController";
        internal const ushort VendorId = 0x0483;

        /// <summary>LIGHTSALT_WRITE_LENGTH and LIGHTSALT_READ_LENGTH (LightSaltController.h:18-20).</summary>
        internal const int WriteLength = 33;
        internal const int ReadLength = 32;
        internal const int AnswerMs = 100;
        internal const int ModeMs = 25;

        internal const byte Brightness = 0xFF;

        internal const string KeyboardClass = "Light&Salt_keyboard1";
        internal const string KeypadClass = "Light&Salt_KEY_PAD";
        internal const string Maker = "Light&Salt";

        /// <summary>The keyboard's and the keypad's sets, rows and columns
        /// (RGBController_LightSaltKeyboard.cpp:18-26, RGBController_LightSaltKeypad.cpp:18-26).</summary>
        internal static readonly (int Sets, int Rows, int Columns) Keyboard = (4, 9, 10);
        internal static readonly (int Sets, int Rows, int Columns) Keypad = (1, 5, 10);

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "LightSalt";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength < WriteLength) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The lights of the keyboard or the keypad, each at its
        /// cell, row times ten plus column.</summary>
        internal static LightMap Map(bool keypad, ushort productId)
        {
            var names = keypad ? KeyboardLedNames.LightSaltKeypadNames : KeyboardLedNames.LightSaltKeyboardNames;
            var cells = keypad ? KeyboardLedNames.LightSaltKeypadIndices : KeyboardLedNames.LightSaltKeyboardIndices;
            return new LightMap(KeyboardLedNames.FromNames(names, i => cells[i]), keypad
                ? "OpenRGB RGBController_LightSaltKeypad.cpp"
                : "OpenRGB RGBController_LightSaltKeyboard.cpp")
            {
                Product = $"lightsalt:{VendorId:X4}:{productId:X4}:{(keypad ? "keypad" : "keyboard")}",
            };
        }

        private HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = this,
            Name = name,
            Map = Map(false, productId),
            RepaintMs = RepaintMs,
            Identify = IsLightSalt,
            Begin = (io, wait) => Begin(io, wait, productId),
        };

        /// <summary>The detector's check: the manufacturer and the product
        /// strings both hold "Light&amp;Salt" (strstr,
        /// LightSaltController.cpp:44-63). A string that could not be read
        /// is empty there, and holds nothing.</summary>
        internal static bool IsLightSalt(IHidReportChannel io)
            => io != null && (io.ManufacturerString ?? "").Contains(Maker, StringComparison.Ordinal)
            && (io.ProductString ?? "").Contains(Maker, StringComparison.Ordinal);

        internal static byte[] Report(params byte[] bytes)
        {
            var report = new byte[WriteLength];
            bytes.AsSpan(0, Math.Min(bytes.Length, WriteLength)).CopyTo(report);
            return report;
        }

        /// <summary>A query and its answer: the 32 bytes after the report ID,
        /// or null when no 32-byte answer came (lines 236-286).</summary>
        internal static byte[] Ask(IHidReportChannel io, byte[] query)
        {
            if (!io.WriteOutput(query)) return null;
            var buffer = new byte[Math.Max(ReadLength + 1, io.InputReportLength)];
            int length = io.ReadInput(buffer, AnswerMs);
            if (length != ReadLength + 1) return null;
            return buffer.AsSpan(1, ReadLength).ToArray();
        }

        /// <summary>The string an answer starts with, up to its first zero.</summary>
        internal static string Text(byte[] answer)
        {
            int end = Array.IndexOf(answer, (byte)0);
            return Encoding.ASCII.GetString(answer, 0, end < 0 ? answer.Length : end);
        }

        /// <summary>DeflectColor (RGBController_LightSalt.cpp:16-32).</summary>
        internal static int Deflect(bool deflection, int color)
        {
            if (!deflection) return color & 0xFFFFFF;
            int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
            r += (255 - r) * 50 / 100;
            g += (255 - g) * 50 / 100;
            b += (255 - b) * 50 / 100;
            return r << 16 | g << 8 | b;
        }

        /// <summary>A picture's reports (DeviceUpdateColors, lines 373-403).</summary>
        internal static List<byte[]> Picture(bool keypad, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var (sets, rows, columns) = keypad ? Keypad : Keyboard;
            var deflections = keypad ? KeyboardLedNames.LightSaltKeypadDeflections : KeyboardLedNames.LightSaltKeyboardDeflections;
            var cells = keypad ? KeyboardLedNames.LightSaltKeypadIndices : KeyboardLedNames.LightSaltKeyboardIndices;
            var data = new int[sets, rows, columns];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int cell = lights[i].Address;
                int led = Array.IndexOf(cells, cell);
                if (led < 0 || cell / columns >= rows) continue;
                for (int set = 0; set < sets; set++)
                    data[set, cell / columns, cell % columns] = Deflect((deflections[led] & 1 << set) != 0, colors[i]);
            }
            var reports = new List<byte[]> { Report(0x00, 0xFE, 0x04, 0xFF, 0xFF, 0xFF) };
            for (int set = 0; set < sets; set++)
            {
                for (int row = 0; row < rows; row++)
                {
                    var report = Report(0x00, (byte)(0x03 + set), (byte)(0x01 + row * columns));
                    for (int column = 0; column < columns; column++)
                    {
                        int color = data[set, row, column];
                        report[3 + column * 3] = (byte)(color >> 16);
                        report[4 + column * 3] = (byte)(color >> 8);
                        report[5 + column * 3] = (byte)color;
                    }
                    reports.Add(report);
                }
            }
            var apply = Report(0x00, 0xFE, 0x01);
            for (int set = 0; set < sets; set++) apply[3 + set] = (byte)(0x03 + set);
            reports.Add(apply);
            return reports;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait, ushort productId)
        {
            if (io == null) return null;
            // OpenRGB keeps the model only for its description and goes on
            // without it (lines 246-258).
            Ask(io, Report(0x00, 0xFE, 0xF5, 0x06));
            var answer = Ask(io, Report(0x00, 0xFE, 0xFF));
            if (answer == null) return null;
            string kind = Text(answer);
            bool keypad;
            if (kind == KeyboardClass) keypad = false;
            else if (kind == KeypadClass) keypad = true;
            else return null;
            if (!io.WriteOutput(Report(0x00, 0xFE, 0x05, Brightness))) return null;
            wait(ModeMs);
            if (!io.WriteOutput(Report(0x00, 0xFE, 0x03, 0x00, 0x00, 0x00))) return null;
            return new Session(keypad, keypad ? Map(true, productId) : null);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly bool _keypad;
            private readonly LightMap _map;

            public Session(bool keypad, LightMap map)
            {
                _keypad = keypad;
                _map = map;
            }

            /// <summary>The keypad's map when the device said it is one.</summary>
            public override LightMap Map => _map;

            public override bool Feature => false;

            /// <summary>Nothing undoes the Custom mode: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(_keypad, lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
