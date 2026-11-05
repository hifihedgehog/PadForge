using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Dygma Raise, lit over its USB serial port with its firmware's
    /// Focus commands, as OpenRGB's DygmaRaiseController lights it (#557).
    ///
    /// <para>The look is OpenRGB's detector: every present COM port of a
    /// device whose hardware ID starts USB\VID_1209&amp;PID_2201
    /// (DygmaRaiseControllerDetect.cpp:21-37, find_usb_serial_port_win.cpp:54-95),
    /// read from the port list PadForge already keeps
    /// (<see cref="SerialControllers.ListComPorts"/>). The port is opened as
    /// OpenRGB opens it, at 115200 baud (DygmaRaiseController.h:22,
    /// serial_port.cpp:197-318).</para>
    ///
    /// <para>A picture goes out as OpenRGB's SendDirect sends it:
    /// "led.theme", then red, green and blue of each of the 132 lights in
    /// decimal, each after a space, and a newline
    /// (DygmaRaiseController.cpp:56-100). The firmware answers every
    /// command with a line that is a single period
    /// (Kaleidoscope-Dygma FocusSerial.cpp:53), which Bazecor reads as the
    /// end of the answer (Focus.ts:115-133), and the session reads it too,
    /// so the port's buffer never fills.</para>
    ///
    /// <para>Hand-back. "led.mode" with no number answers the firmware's
    /// LED mode, and with one sets that mode, which repaints every light
    /// in that mode (FocusLEDCommand, Kaleidoscope-Dygma
    /// src/kaleidoscope/plugin/LEDControl.cpp:70-85 and 375-397). Bazecor
    /// reads it the same way when it backs a keyboard up
    /// (Backup.ts:184). A session reads the mode before its first picture,
    /// and its hand-back sets it again. A keyboard that does not answer
    /// the read gets no picture.</para>
    /// </summary>
    internal sealed class DygmaRaiseLighting : IOwnLookFamily
    {
        internal const ushort VendorId = 0x1209;
        internal const ushort ProductId = 0x2201;

        /// <summary>DYGMA_RAISE_BAUD (DygmaRaiseController.h:22).</summary>
        internal const int Baud = 115200;

        /// <summary>The hardware ID prefix OpenRGB compares, its first 21
        /// characters (find_usb_serial_port_win.cpp:54-59 and 95).</summary>
        internal const string HardwarePrefix = @"USB\VID_1209&PID_2201";

        /// <summary>How long a session waits for the firmware's answer to a
        /// command. No reference times one: Bazecor's timeout is a field
        /// of its own (Focus.ts:209-211). The bound is PadForge's.</summary>
        internal const int AnswerWaitMs = 500;

        /// <summary>The shortest time between two pictures. A picture is a
        /// line of up to 1,594 bytes and no reference bounds the rate, so
        /// the bound is PadForge's.</summary>
        internal const int RepaintMs = 100;

        private readonly Func<IReadOnlyList<ComPort>> _ports;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, ISerialLineChannel> _open;
        private readonly Func<string, byte[], int, bool> _writeOnce;

        public DygmaRaiseLighting()
            : this(null, null, null, null) { }

        /// <summary>Test seam: the port list, the container lookup, the
        /// opener and the crash path's one write.</summary>
        internal DygmaRaiseLighting(Func<IReadOnlyList<ComPort>> ports, Func<string, Guid> containerOf,
            Func<string, ISerialLineChannel> open, Func<string, byte[], int, bool> writeOnce)
        {
            _ports = ports ?? SerialControllers.ListComPorts;
            _containerOf = containerOf ?? OwnLightingChannels.ContainerOfInstance;
            _open = open ?? (path => OwnLightingChannels.OpenSerial(path, Baud));
            _writeOnce = writeOnce ?? ((path, bytes, timeoutMs) => OwnLightingChannels.SerialWriteOnce(path, Baud, bytes, timeoutMs));
            Model = new HidLightingModel
            {
                Family = this,
                Name = "Dygma Raise",
                Map = new LightMap(Lights(), "OpenRGB DygmaRaiseController") { Product = "dygma:1209:2201" },
                Programs = new[] { VendorPrograms.Bazecor },
                RepaintMs = RepaintMs,
                Open = key => _open(key),
                OpenFoundHeld = () => OwnLightingChannels.SerialDenied,
                BeginOwn = (channel, wait) => channel is ISerialLineChannel port ? Begin(port, wait) : null,
                WriteOnce = (key, frame, timeoutMs) => _writeOnce(key, frame, timeoutMs),
            };
        }

        public string Name => "Dygma Raise";

        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        /// <summary>The 69 keys in OpenRGB's order, copied from
        /// RGBController_DygmaRaise.cpp:61-132 with each KEY_EN_ name written
        /// as its string from RGBControllerKeyNames.cpp. The thumb keys
        /// T1 to T8 send whatever the keymap puts on them, so no source
        /// names a key there.</summary>
        internal static readonly string[] Keys =
        {
            "Key: Escape", "Key: 1", "Key: 2", "Key: 3", "Key: 4", "Key: 5", "Key: 6",
            "Key: Tab", "Key: Q", "Key: W", "Key: E", "Key: R", "Key: T",
            "Key: Caps Lock", "Key: A", "Key: S", "Key: D", "Key: F", "Key: G",
            "Key: Left Shift", "Key: \\ (ISO)", "Key: Z", "Key: X", "Key: C", "Key: V", "Key: B",
            "Key: Left Control", "Key: Left Windows", "Key: Left Alt",
            "Key: T1", "Key: T2", "Key: T3", "Key: T4",
            "Key: Backspace", "Key: =", "Key: -", "Key: 0", "Key: 9", "Key: 8", "Key: 7",
            "Key: Enter", "Key: ]", "Key: [", "Key: P", "Key: O", "Key: I", "Key: U", "Key: Y",
            "Key: #", "Key: '", "Key: ;", "Key: L", "Key: K", "Key: J", "Key: H",
            "Key: Right Shift", "Key: /", "Key: .", "Key: ,", "Key: M", "Key: N",
            "Key: Right Control", "Key: Right Windows", "Key: Right Fn", "Key: Right Alt",
            "Key: T6", "Key: T5", "Key: T8", "Key: T7",
        };

        /// <summary>Declared after <see cref="Keys"/>, which its model's map
        /// is built from.</summary>
        public static readonly DygmaRaiseLighting Instance = new();

        /// <summary>OpenRGB's zones in its order: the 69 keys, the 62
        /// underglow lights and the Neuron's light
        /// (RGBController_DygmaRaise.cpp:54-59 and 172-224), each light's
        /// address its place in the theme line.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(Keys.Length + 62 + 1);
            foreach (string name in Keys)
            {
                int address = lights.Count;
                if (OpenRgbKeys.TryGet(name, out ushort usage, out var label) && usage != 0)
                    lights.Add(new LightInfo(address, LightKind.Key, usage, LightKeySource.Generated));
                else if (label != LightLabel.None)
                    lights.Add(new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = label });
                else
                    lights.Add(new LightInfo(address, LightKind.Key, 0, LightKeySource.None));
            }
            for (int n = 0; n < 62; n++)
                lights.Add(new LightInfo(lights.Count, LightKind.Edge, 0, LightKeySource.None));
            lights.Add(new LightInfo(lights.Count, LightKind.Logo, 0, LightKeySource.None));
            return lights;
        }

        public IReadOnlyList<OwnDevice> Look()
        {
            IReadOnlyList<ComPort> ports;
            try { ports = _ports(); }
            catch { return null; }
            if (ports == null) return null;
            var found = new List<OwnDevice>();
            foreach (var port in ports)
            {
                if (port?.InstanceId == null || port.PortName == null
                    || !port.InstanceId.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                Guid container;
                try { container = _containerOf(port.InstanceId); }
                catch { container = Guid.Empty; }
                // A port whose container cannot be read yet is left for the
                // next look: the key is no interface path to look it up by.
                if (container == Guid.Empty) continue;
                found.Add(new OwnDevice(@"\\.\" + port.PortName, Model)
                {
                    VendorId = VendorId, ProductId = ProductId, Container = container,
                });
            }
            return found;
        }

        // ─────────────────────────────────────────────
        //  The lines
        // ─────────────────────────────────────────────

        /// <summary>The picture as OpenRGB's SendDirect writes it
        /// (DygmaRaiseController.cpp:56-100): "led.theme", then " r g b" in
        /// decimal for each light, then a newline.</summary>
        internal static byte[] ThemeLine(IReadOnlyList<int> colors)
        {
            var line = new StringBuilder("led.theme", 9 + 12 * (colors?.Count ?? 0) + 1);
            if (colors != null)
            {
                foreach (int rgb in colors)
                {
                    line.Append(' ').Append(((rgb >> 16) & 0xFF).ToString(CultureInfo.InvariantCulture));
                    line.Append(' ').Append(((rgb >> 8) & 0xFF).ToString(CultureInfo.InvariantCulture));
                    line.Append(' ').Append((rgb & 0xFF).ToString(CultureInfo.InvariantCulture));
                }
            }
            line.Append('\n');
            return Encoding.ASCII.GetBytes(line.ToString());
        }

        /// <summary>A command with no argument, as Bazecor sends one: its
        /// name and a newline (Focus.ts:228-232).</summary>
        internal static byte[] Command(string name) => Encoding.ASCII.GetBytes(name + "\n");

        /// <summary>The command that sets a mode, as Bazecor sends a command
        /// with an argument: the name, a space, the argument, a newline
        /// (Focus.ts:228-232, and "led.mode 0" in Backup.ts:439).</summary>
        internal static byte[] ModeLine(int mode) => Command("led.mode " + mode.ToString(CultureInfo.InvariantCulture));

        /// <summary>Reads the firmware's answer up to the line that is a
        /// single period, as Bazecor reads it: lines end in "\r\n", and a
        /// line that is "." or ends in "." ends the answer, which is the
        /// lines before it, trimmed (Focus.ts:111-133). Null when the read
        /// failed or no such line came within <see cref="AnswerWaitMs"/>. The
        /// wait counts each empty read's time on the port beside the waits
        /// between reads, and the clock cuts off a device that keeps sending
        /// and ends no line.</summary>
        internal static string ReadAnswer(ISerialLineChannel port, Action<int> wait)
        {
            var text = new StringBuilder();
            var buffer = new byte[256];
            long deadline = Environment.TickCount64 + AnswerWaitMs;
            string result = null;
            for (int waited = 0; ; )
            {
                int read = port.Read(buffer);
                if (read < 0) return null;
                if (read > 0)
                {
                    text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    string all = text.ToString();
                    var lines = new List<string>();
                    int at = 0;
                    for (int end; (end = all.IndexOf("\r\n", at, StringComparison.Ordinal)) >= 0; at = end + 2)
                        lines.Add(all.Substring(at, end - at));
                    var answer = new StringBuilder();
                    foreach (string line in lines)
                    {
                        if (line == "." || line.EndsWith('.'))
                        {
                            result = answer.ToString().Trim();
                            break;
                        }
                        if (answer.Length > 0) answer.Append("\r\n");
                        answer.Append(line);
                    }
                    if (result != null) return result;
                    if (Environment.TickCount64 >= deadline) return null;
                    continue;
                }
                waited += OwnLightingChannels.SerialReadBoundMs(buffer.Length);
                if (waited >= AnswerWaitMs || Environment.TickCount64 >= deadline) return null;
                wait(50);
                waited += 50;
            }
        }

        /// <summary>A session on the mode the firmware answers now, or null
        /// when it answered none.</summary>
        private static HidLightingSession Begin(ISerialLineChannel port, Action<int> wait)
        {
            if (!port.Write(Command("led.mode"))) return null;
            string answer = ReadAnswer(port, wait);
            if (answer == null || !int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out int mode)
                || mode < 0 || mode > 255)
                return null;
            return new Session(port, wait, mode);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly ISerialLineChannel _port;
            private readonly Action<int> _wait;
            private readonly int _mode;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(ISerialLineChannel port, Action<int> wait, int mode)
            {
                _port = port;
                _wait = wait;
                _mode = mode;
            }

            /// <summary>The mode the firmware was in.</summary>
            public int Mode => _mode;

            public override bool Feature => false;

            /// <summary>The line that sets the mode again, once a picture
            /// may have gone out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                // Marked first: a write that reports a failure may still
                // have landed.
                if (_crash.Length == 0) _crash = new[] { ModeLine(_mode) };
                return _port.Write(ThemeLine(colors)) && ReadAnswer(_port, wait ?? _wait) != null
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (!_port.Write(ModeLine(_mode)) || ReadAnswer(_port, wait ?? _wait) == null) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
