using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Skydimo's light strips and monitor backlights on its serial
    /// controller (#557), lit over their USB serial port as OpenRGB's
    /// SkydimoSerialController lights them.
    ///
    /// <para>The look and the identity. OpenRGB's detector takes every port
    /// of a 1A86:7523 device (SkydimoSerialControllerDetect.cpp:21-22 and
    /// 43), which is WCH's CH340 bridge that many other devices carry, and
    /// keeps a port only when the device answers "Moni-A" with a line
    /// starting "SK" and its model code (IsPresent and QueryInfo,
    /// SkydimoSerialController.cpp:155-268). A look asks no device anything,
    /// so the look lists every such port, and the question goes out when a
    /// session begins, at 115200 baud (SKYDIMO_SERIAL_BAUD_RATE,
    /// SkydimoSerialController.h:20): the port's waiting bytes are read and
    /// dropped, "Moni-A" goes out, and up to ten reads collect the answer,
    /// stopping at a line end or 63 bytes (lines 173-223). A port that does
    /// not answer as a Skydimo gets no session, nothing more is written to
    /// it, and the look leaves it out for as long as it stays present. A
    /// port whose write or read failed is no answer, and the worker tries
    /// it again as it tries any unit that failed.</para>
    ///
    /// <para>The lights. The model code picks the entry of OpenRGB's table
    /// (skydimo_model_entries, lines 35-79) and the layout ConfigureOutput
    /// builds from it (lines 270-407): a strip of its LEDs, or two sides,
    /// three or four edges of a screen laid out in a matrix, each light at
    /// its cell. The session's map carries them
    /// (<see cref="HidLightingSession.Map"/>). A code the table does not
    /// know, or SK0410, whose length the user sets in OpenRGB (resizable,
    /// starting at 0 LEDs, lines 129-148 and 282-290), takes one zone whose
    /// color goes to the most LEDs OpenRGB allows it, 150 or SK0410's 300
    /// (SKYDIMO_SERIAL_DEFAULT_MAX_LED_COUNT, SkydimoSerialController.h:28),
    /// so a strip of any length shows it, as the Cooler Master Small ARGB
    /// family does for its header.</para>
    ///
    /// <para>The frames. A picture is the header 41 64 61 00, the last LED's
    /// index in two bytes, high first, and red, green and blue of each LED
    /// (WriteFrame, lines 417-475). OpenRGB repeats the picture once 1.4 s
    /// have passed without one, then every 1.5 s, since the SK0L32 turns its
    /// output off when no frame comes for a short time
    /// (KeepaliveThreadFunction, RGBController_SkydimoSerial.cpp:16-17 and
    /// 163-187), and the model keeps the same keep-alive
    /// (<see cref="HidLightingModel.KeepAliveMs"/>). The device saves
    /// nothing ("@save :x:", line 23).</para>
    ///
    /// <para>Hand-back. OpenRGB sends a black frame when it closes
    /// (SetBlack, SkydimoSerialController.cpp:482-488, from
    /// ~RGBController_SkydimoSerial, RGBController_SkydimoSerial.cpp:55-64),
    /// and the session does the same. That frame is also the crash frame.
    /// The device then shows nothing until Skydimo's app, which the model
    /// names, sends it frames again.</para>
    /// </summary>
    internal sealed class SkydimoSerialLighting : IOwnLookFamily
    {
        internal const ushort VendorId = 0x1A86;
        internal const ushort ProductId = 0x7523;

        /// <summary>The constants of SkydimoSerialController.h:20-31.</summary>
        internal const int Baud = 115200;
        internal const string Query = "Moni-A";
        internal const int ModelIdLength = 6;
        internal const int ReadBufferSize = 64;
        internal const int MaxResponseSize = 63;
        internal const int QueryAttempts = 10;
        internal const int DefaultMaxLeds = 150;

        /// <summary>The frame header (skydimo_serial_frame_header, lines 83-86).</summary>
        internal static readonly byte[] Header = { 0x41, 0x64, 0x61, 0x00 };

        /// <summary>SKYDIMO_SERIAL_KEEPALIVE_PERIOD and the period less
        /// SKYDIMO_SERIAL_SLEEP_THRESHOLD (RGBController_SkydimoSerial.cpp:16-17).</summary>
        internal const int KeepAliveMs = 1500;
        internal const int KeepAliveIdleMs = 1400;

        /// <summary>No reference bounds the rate beyond the keep-alive, so
        /// the bound is PadForge's, the one the other families of the
        /// shared worker keep.</summary>
        internal const int RepaintMs = 50;

        /// <summary>How many reads drop the bytes waiting before the
        /// question, in place of serial_flush_rx (line 173), which the port
        /// channel has no call for.</summary>
        internal const int DrainReads = 8;

        internal enum Layout { None, Strip1, Perimeter3, Perimeter4, Sides2 }

        /// <summary>One row of skydimo_model_entries.</summary>
        internal sealed record Entry(string Model, Layout Layout, int Zone1, int Zone2, int Zone3, int Zone4, int Total, int Max);

        /// <summary>skydimo_model_entries (SkydimoSerialController.cpp:35-79).</summary>
        internal static readonly Entry[] Entries =
        {
            new("SK0121", Layout.Perimeter3, 13, 25, 13, 0, 51, 0),
            new("SK0124", Layout.Perimeter3, 14, 26, 14, 0, 54, 0),
            new("SK0127", Layout.Perimeter3, 17, 31, 17, 0, 65, 0),
            new("SK0132", Layout.Perimeter3, 20, 37, 20, 0, 77, 0),
            new("SK0134", Layout.Perimeter3, 15, 41, 15, 0, 71, 0),
            new("SK0149", Layout.Perimeter3, 19, 69, 19, 0, 107, 0),
            new("SK0201", Layout.Sides2, 20, 20, 0, 0, 40, 0),
            new("SK0202", Layout.Sides2, 30, 30, 0, 0, 60, 0),
            new("SK0204", Layout.Sides2, 25, 25, 0, 0, 50, 0),
            new("SK0402", Layout.Strip1, 72, 0, 0, 0, 72, 0),
            new("SK0403", Layout.Strip1, 96, 0, 0, 0, 96, 0),
            new("SK0404", Layout.Strip1, 144, 0, 0, 0, 144, 0),
            new("SK0410", Layout.None, 0, 0, 0, 0, 0, 300),
            new("SK0801", Layout.Strip1, 2, 0, 0, 0, 2, 0),
            new("SK0802", Layout.Strip1, 18, 0, 0, 0, 18, 0),
            new("SK0803", Layout.Strip1, 10, 0, 0, 0, 10, 0),
            new("SK0901", Layout.Strip1, 14, 0, 0, 0, 14, 0),
            new("SK0E01", Layout.Strip1, 16, 0, 0, 0, 16, 0),
            new("SK0F01", Layout.Sides2, 29, 29, 0, 0, 58, 0),
            new("SK0F02", Layout.Sides2, 25, 25, 0, 0, 50, 0),
            new("SK0H01", Layout.Strip1, 2, 0, 0, 0, 2, 0),
            new("SK0H02", Layout.Strip1, 4, 0, 0, 0, 4, 0),
            new("SK0K01", Layout.Strip1, 120, 0, 0, 0, 120, 0),
            new("SK0K02", Layout.Strip1, 15, 0, 0, 0, 15, 0),
            new("SK0L21", Layout.Perimeter4, 13, 25, 13, 25, 76, 0),
            new("SK0L24", Layout.Perimeter4, 14, 26, 14, 26, 80, 0),
            new("SK0L27", Layout.Perimeter4, 17, 31, 17, 31, 96, 0),
            new("SK0L32", Layout.Perimeter4, 20, 37, 20, 37, 114, 0),
            new("SK0L34", Layout.Perimeter4, 15, 41, 15, 41, 112, 0),
            new("SK0M01", Layout.Strip1, 24, 0, 0, 0, 24, 0),
            new("SK0N01", Layout.Strip1, 256, 0, 0, 0, 256, 0),
            new("SK0N02", Layout.Strip1, 1024, 0, 0, 0, 1024, 0),
            new("SK0N03", Layout.Strip1, 253, 0, 0, 0, 253, 0),
            new("SK0S01", Layout.Strip1, 32, 0, 0, 0, 32, 0),
            new("SKA124", Layout.Perimeter3, 18, 34, 18, 0, 70, 0),
            new("SKA127", Layout.Perimeter3, 20, 41, 20, 0, 81, 0),
            new("SKA132", Layout.Perimeter3, 25, 45, 25, 0, 95, 0),
            new("SKA134", Layout.Perimeter3, 21, 51, 21, 0, 93, 0),
            new("SKB124", Layout.Perimeter4, 18, 34, 18, 34, 104, 0),
            new("SKB127", Layout.Perimeter4, 20, 41, 20, 41, 122, 0),
            new("SKB132", Layout.Perimeter4, 25, 44, 25, 44, 138, 0),
            new("SKB134", Layout.Perimeter4, 21, 50, 21, 50, 142, 0),
        };

        private readonly Func<IReadOnlyList<ComPort>> _ports;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, ISerialLineChannel> _open;
        private readonly Func<string, byte[], int, bool> _writeOnce;

        /// <summary>The ports that answered as no Skydimo, kept while they
        /// stay in the port list. Worker thread only.</summary>
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public SkydimoSerialLighting()
            : this(null, null, null, null) { }

        /// <summary>Test seam: the port list, the container lookup, the
        /// opener and the crash path's one write.</summary>
        internal SkydimoSerialLighting(Func<IReadOnlyList<ComPort>> ports, Func<string, Guid> containerOf,
            Func<string, ISerialLineChannel> open, Func<string, byte[], int, bool> writeOnce)
        {
            _ports = ports ?? SerialControllers.ListComPorts;
            _containerOf = containerOf ?? OwnLightingChannels.ContainerOfInstance;
            _open = open ?? (path => OwnLightingChannels.OpenSerial(path, Baud));
            _writeOnce = writeOnce ?? ((path, bytes, timeoutMs) => OwnLightingChannels.SerialWriteOnce(path, Baud, bytes, timeoutMs));
            Model = new HidLightingModel
            {
                Family = this,
                Name = "Skydimo Serial",
                Map = new LightMap(ZoneLights(), "OpenRGB SkydimoSerialController, from the device") { Product = Product },
                Programs = new[] { VendorPrograms.Skydimo },
                RepaintMs = RepaintMs,
                KeepAliveMs = KeepAliveMs,
                KeepAliveIdleMs = KeepAliveIdleMs,
                Open = key =>
                {
                    var port = _open(key);
                    return port == null ? null : new KeyedPort(key, port);
                },
                BeginOwn = (channel, wait) => channel is KeyedPort port ? Begin(port) : null,
                WriteOnce = (key, frame, timeoutMs) => _writeOnce(key, frame, timeoutMs),
            };
        }

        internal const string Product = "skydimo:1A86:7523";

        public static readonly SkydimoSerialLighting Instance = new();

        public string Name => "Skydimo Serial";

        internal HidLightingModel Model { get; }

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        public IReadOnlyList<OwnDevice> Look()
        {
            IReadOnlyList<ComPort> ports;
            try { ports = _ports(); }
            catch { return null; }
            if (ports == null) return null;
            lock (_gate)
            {
                var present = new HashSet<string>(ports.Where(p => p?.PortName != null).Select(p => @"\\.\" + p.PortName),
                    StringComparer.OrdinalIgnoreCase);
                _refused.RemoveWhere(key => !present.Contains(key));
                return LightReports.SerialPorts(ports, _containerOf, VendorId, ProductId,
                    port => _refused.Contains(@"\\.\" + port.PortName) ? null : Model);
            }
        }

        /// <summary>Whether a port answered as no Skydimo and is left out of
        /// the look.</summary>
        internal bool Refused(string key)
        {
            lock (_gate) return _refused.Contains(key);
        }

        // ─────────────────────────────────────────────
        //  The identity
        // ─────────────────────────────────────────────

        /// <summary>The model code of an answer to "Moni-A", or null when the
        /// answer is no Skydimo's: at least six bytes once trailing line ends
        /// are dropped, starting S and K in either case, and no comma inside
        /// the code (QueryInfo, lines 220-264). The code is the first six
        /// bytes in upper case.</summary>
        internal static string ModelOf(IReadOnlyList<byte> answer)
        {
            if (answer == null) return null;
            int length = answer.Count;
            while (length > 0 && (answer[length - 1] == '\r' || answer[length - 1] == '\n')) length--;
            if (length < ModelIdLength) return null;
            if (char.ToUpperInvariant((char)answer[0]) != 'S' || char.ToUpperInvariant((char)answer[1]) != 'K') return null;
            for (int i = 0; i < length; i++)
            {
                if (answer[i] != ',') continue;
                if (i < ModelIdLength) return null;
                break;
            }
            var code = new StringBuilder(ModelIdLength);
            for (int i = 0; i < ModelIdLength; i++) code.Append(char.ToUpperInvariant((char)answer[i]));
            return code.ToString();
        }

        /// <summary>Asks the device for its model code: drops what waits,
        /// writes "Moni-A" and reads the answer as QueryInfo does
        /// (lines 173-223). Null when the device gave none, with
        /// <paramref name="failed"/> set when a write or a read failed, which
        /// says nothing of what the device is.</summary>
        internal static string Ask(ISerialLineChannel port, out bool failed)
        {
            failed = true;
            var buffer = new byte[ReadBufferSize];
            for (int i = 0; i < DrainReads; i++)
            {
                int dropped = port.Read(buffer);
                if (dropped < 0) return null;
                if (dropped == 0) break;
            }
            if (!port.Write(Encoding.ASCII.GetBytes(Query))) return null;
            var answer = new List<byte>(MaxResponseSize);
            for (int attempt = 0; attempt < QueryAttempts; attempt++)
            {
                int read = port.Read(buffer);
                if (read < 0) return null;
                for (int i = 0; i < read; i++) answer.Add(buffer[i]);
                if (answer.Count >= MaxResponseSize) break;
                if (answer.Count > 0)
                {
                    byte last = answer[^1];
                    byte previous = answer.Count >= 2 ? answer[^2] : (byte)0;
                    if (last == '\r' || last == '\n' || previous == '\r') break;
                }
            }
            failed = false;
            return ModelOf(answer);
        }

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>What ConfigureOutput sets for a model code: how many LEDs
        /// a picture carries and the light map. A code with a fixed layout
        /// lights each LED. Any other lights one zone over the most LEDs it
        /// allows.</summary>
        internal static (int Leds, LightMap Map) Output(string model)
        {
            var entry = Entries.FirstOrDefault(e => e.Model == model);
            if (entry == null || entry.Layout == Layout.None)
            {
                int max = entry != null && entry.Max != 0 ? entry.Max : DefaultMaxLeds;
                return (max, new LightMap(ZoneLights(), "OpenRGB SkydimoSerialController " + (model ?? "")) { Product = Product });
            }
            var cells = Matrix(entry, out int width);
            var lights = new LightInfo[entry.Total];
            for (int i = 0; i < entry.Total; i++)
                lights[i] = new LightInfo(i, LightKind.Other, 0, LightKeySource.None);
            if (cells != null)
                for (int cell = 0; cell < cells.Length; cell++)
                {
                    int led = cells[cell];
                    if (led >= 0 && led < lights.Length)
                        lights[led] = lights[led] with { X = cell % width, Y = cell / width, Width = 1, Height = 1, HasPosition = true };
                }
            return (entry.Total, new LightMap(lights, "OpenRGB SkydimoSerialController " + model) { Product = Product });
        }

        /// <summary>The one zone a strip of unknown length takes.</summary>
        internal static IReadOnlyList<LightInfo> ZoneLights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        /// <summary>ConfigureOutput's matrix (lines 302-406): each cell's LED,
        /// or -1, row by row, for a layout that has one, and null for a
        /// strip.</summary>
        internal static int[] Matrix(Entry entry, out int width)
        {
            width = 0;
            if (entry.Layout == Layout.Strip1 || entry.Layout == Layout.None) return null;
            int height;
            if (entry.Layout == Layout.Perimeter4)
            {
                height = Math.Max(entry.Zone1, entry.Zone3) + 2;
                width = Math.Max(entry.Zone2, entry.Zone4) + 2;
            }
            else if (entry.Layout == Layout.Perimeter3)
            {
                height = Math.Max(entry.Zone1, entry.Zone3) + 1;
                width = entry.Zone2 + 2;
            }
            else
            {
                height = Math.Max(entry.Zone1, entry.Zone2) + 2;
                width = Math.Max(3, (16 * height + 4) / 9);
            }
            var map = Enumerable.Repeat(-1, width * height).ToArray();
            int w = width;
            int led = 0;
            void Set(int y, int x)
            {
                if (y < height && x < w) map[y * w + x] = led++;
            }

            if (entry.Layout == Layout.Sides2)
            {
                for (int placed = 0, y = height - 2; placed < entry.Zone1 && y >= 1; placed++, y--) Set(y, 0);
            }
            else
            {
                int y = entry.Layout == Layout.Perimeter3 ? height - 1 : height - 2;
                for (int placed = 0; placed < entry.Zone1 && y >= 1; placed++, y--) Set(y, width - 1);
            }

            if (entry.Layout == Layout.Perimeter3 || entry.Layout == Layout.Perimeter4)
            {
                for (int placed = 0, x = width - 2; placed < entry.Zone2 && x >= 1; placed++, x--) Set(0, x);
            }
            else if (entry.Layout == Layout.Sides2)
            {
                for (int placed = 0, y = 1; placed < entry.Zone2 && y <= height - 2; placed++, y++) Set(y, width - 1);
            }

            int endY = entry.Layout == Layout.Perimeter3 ? height - 1 : height - 2;
            for (int placed = 0, y = 1; placed < entry.Zone3 && y <= endY; placed++, y++) Set(y, 0);

            if (entry.Layout == Layout.Perimeter4)
            {
                for (int placed = 0, x = 1; placed < entry.Zone4 && x <= width - 2; placed++, x++) Set(height - 1, x);
            }
            return map;
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>WriteFrame's bytes for <paramref name="count"/> LEDs.</summary>
        internal static byte[] Frame(IReadOnlyList<int> colors, int count)
        {
            var frame = new byte[Header.Length + 2 + count * 3];
            Header.CopyTo(frame, 0);
            int last = count > 0 ? count - 1 : 0;
            frame[4] = (byte)(last >> 8);
            frame[5] = (byte)last;
            for (int i = 0; i < count; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                frame[6 + i * 3] = LightReports.Red(rgb);
                frame[7 + i * 3] = LightReports.Green(rgb);
                frame[8 + i * 3] = LightReports.Blue(rgb);
            }
            return frame;
        }

        /// <summary>A session on the model code the device answers, or null
        /// when it answered as no Skydimo, which the look then leaves out,
        /// or when the port failed, which the worker tries again.</summary>
        private HidLightingSession Begin(KeyedPort port)
        {
            string model = Ask(port, out bool failed);
            if (model == null)
            {
                if (!failed)
                    lock (_gate) _refused.Add(port.Key);
                return null;
            }
            var (leds, map) = Output(model);
            bool zone = map.Lights.Count == 1 && map.Lights[0].Kind == LightKind.Zone;
            Func<int[], byte[]> frame = zone
                ? colors => Frame(Enumerable.Repeat(colors.Length > 0 ? colors[0] : 0, leds).ToArray(), leds)
                : colors => Frame(colors, leds);
            return new SerialFramesSession(port, zone ? 1 : leds, frame, handBack: Frame(null, leds), map: map);
        }

        /// <summary>A port and the key it was opened by.</summary>
        internal sealed class KeyedPort : ISerialLineChannel
        {
            private readonly ISerialLineChannel _port;

            public KeyedPort(string key, ISerialLineChannel port)
            {
                Key = key;
                _port = port;
            }

            public string Key { get; }

            public bool Write(byte[] bytes) => _port.Write(bytes);

            public int Read(byte[] buffer) => _port.Read(buffer);

            public void Dispose() => _port.Dispose();
        }
    }
}
