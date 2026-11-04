using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Roccat and Turtle Beach Vulcan, Pyro and Magma keyboards, lit as
    /// OpenRGB's RoccatVulcanKeyboardController lights them (#557). Each
    /// keyboard is two HID collections: a control collection that takes
    /// feature reports and a LED collection that takes the colors as output
    /// reports and answers each picture with an input report.
    ///
    /// <para>The two collections. The unit is the control collection, the
    /// one a detector registration of the family matches on the control
    /// interface and usage page OpenRGB opens for the product, and its LED
    /// collection is the model's sibling: the one of the same product on
    /// the LED interface and usage page (RoccatControllerDetect.cpp:101-203,
    /// the interfaces and pages at lines 122-153,
    /// <see cref="HidLightingModel.Siblings"/>). OpenRGB pairs the first two
    /// it finds that no other keyboard took. The worker pairs the two that
    /// share a container, which is the same pair for one keyboard and the
    /// right pair for two of a kind, and lists the keyboard only while both
    /// are in the walk, as OpenRGB makes it only once it found both
    /// (lines 155-201).</para>
    ///
    /// <para>The session. It reads the device info report as OpenRGB's
    /// InitDeviceInfo reads it, twice (RoccatVulcanKeyboardController.cpp:72-148):
    /// byte 2 the firmware version, which decides the byte order of the
    /// color frame's length (IsBigEndianDirectMode, lines 155-164), and
    /// byte 6 the layout, which picks the keyboard's map
    /// (<see cref="RoccatVulcanLayouts"/>). OpenRGB enters its Direct mode
    /// as soon as it finds a keyboard (RGBController_RoccatVulcanKeyboard.cpp:263-267):
    /// the mode report with mode 0x0B, the ready wait, the direct switch on,
    /// the ready wait (DeviceUpdateMode, lines 299-316). The first picture
    /// does the same, and each picture after it is OpenRGB's SendColors:
    /// output reports of 65 bytes to the LED collection, then the input
    /// reports drained and one more awaited for 20 ms (lines 197-398 and
    /// 651-665).</para>
    ///
    /// <para>What the writes do to the keyboard. The mode report is the one
    /// OpenRGB sends for its Static, Rainbow Wave and Default modes, which
    /// it marks as saved by the keyboard (MODE_FLAG_AUTOMATIC_SAVE,
    /// RGBController_RoccatVulcanKeyboard.cpp:63-94), so the Direct mode
    /// report is taken to replace the stored lighting effect as well. The
    /// direct switch and the colors are not stored. No reference reads the
    /// mode report back: OpenRGB, Eruption (roccat_vulcan_1xx.rs:593-669),
    /// roccat-vulcan-api-rs (reports.rs) and SignalRGB
    /// (Roccat_Vulcan_TKL_Keyboard.js:57-101) all write it whole. So the
    /// hand-back is OpenRGB's Default mode, "the default mode when a mode is
    /// software generated, but Swarm is inactive"
    /// (RoccatVulcanKeyboardController.h:43-49): the mode report with mode
    /// 0x02, the ready wait, the direct switch off, the ready wait. The
    /// keyboard then shows its own default effect, which OpenRGB says is
    /// often close to a rainbow (RGBController_RoccatVulcanKeyboard.cpp:26-28),
    /// and not the effect Swarm last stored. roccat-vulcan-api-rs hands back
    /// the same way, to a rainbow, when it is dropped (keyboard.rs:247-251).
    /// The crash path sends the two reports with no wait between.</para>
    ///
    /// <para>ROCCAT Swarm lights these keyboards, and while it runs the
    /// family leaves them to it (<see cref="VendorPrograms.RoccatSwarm"/>).</para>
    /// </summary>
    internal sealed class RoccatVulcanLighting : IHidLightingFamily
    {
        public static readonly RoccatVulcanLighting Instance = new();

        internal const string FamilyName = "RoccatController";
        internal const string Detector = "DetectRoccatVulcanKeyboardControllers";

        internal const ushort RoccatVendorId = 0x1E7D;
        internal const ushort TurtleBeachVendorId = 0x10F5;

        /// <summary>The products (RoccatVulcanKeyboardController.h:23-36).</summary>
        internal const ushort Vulcan100Aimo = 0x307A;
        internal const ushort Vulcan120Aimo = 0x3098;
        internal const ushort VulcanTkl = 0x2FEE;
        internal const ushort VulcanPro = 0x30F7;
        internal const ushort VulcanTklPro = 0x311A;
        internal const ushort VulcanII = 0x2F4E;
        internal const ushort VulcanIIMini = 0x2F09;
        internal const ushort VulcanIIMax = 0x2EE2;
        internal const ushort Pyro = 0x314C;
        internal const ushort Magma = 0x3124;
        internal const ushort MagmaMini = 0x69A0;
        internal const ushort TurtleBeachVulcanII = 0x501B;
        internal const ushort TurtleBeachVulcanIITkl = 0x5023;
        internal const ushort TurtleBeachVulcanIITklPro = 0x5001;

        /// <summary>The modes (RoccatVulcanKeyboardController.h:38-50).</summary>
        internal const byte ModeDirect = 0x0B;
        internal const byte ModeDefault = 0x02;

        /// <summary>The speed and brightness SendMode puts in for the 0 each
        /// mode PadForge sends carries (RoccatVulcanKeyboardController.cpp:402-403,
        /// the values at RoccatVulcanKeyboardController.h:52-60): the Direct
        /// and Default modes have no speed, and the Direct mode's brightness
        /// is the default or, on a Vulcan 100 or 120 Aimo, none
        /// (RGBController_RoccatVulcanKeyboard.cpp:47-94).</summary>
        internal const byte SpeedDefault = 0x06;
        internal const byte BrightnessDefault = 0x45;

        /// <summary>The device info and ready reports.</summary>
        internal const byte ReadyReport = 0x04;
        internal const int ReadyTries = 100;
        internal const int ReadyWaitMs = 25;

        /// <summary>AwaitResponse(20) after each picture
        /// (RoccatVulcanKeyboardController.cpp:396-397 and 651-655).</summary>
        internal const int AwaitMs = 20;

        /// <summary>The longest drain of input reports before a picture's
        /// answer is awaited. OpenRGB drains until a read with no wait finds
        /// none (lines 657-665). The bound is PadForge's.</summary>
        internal const int DrainLimit = 64;

        /// <summary>The shortest time between two pictures. A picture is up
        /// to nine reports and an answer awaited for 20 ms, and no
        /// reference bounds the rate, so the bound is PadForge's.</summary>
        internal const int RepaintMs = 50;

        /// <summary>The products whose reports OpenRGB builds the second way:
        /// a 9-byte info report 0x09, the direct switch 0E 05, and a color
        /// frame with a packet number in every packet
        /// (RoccatVulcanKeyboardController.cpp:77-95, 170-188 and 203-247).</summary>
        internal static bool SecondGeneration(ushort pid) => pid is Pyro or Magma or MagmaMini or VulcanPro or VulcanTklPro
            or VulcanII or VulcanIIMini or VulcanIIMax or TurtleBeachVulcanII or TurtleBeachVulcanIITklPro;

        /// <summary>One model per registration and product. Locked: a worker
        /// a timed-out Stop left behind can match while the next one
        /// does.</summary>
        private readonly Dictionary<(string Name, ushort VendorId, ushort ProductId), HidLightingModel> _models = new();

        public string Name => "Roccat Vulcan";

        /// <summary>The model at a control collection: the first of the
        /// family's registrations the collection answers to is the Vulcan
        /// detector's, whose interface and usage page are the control
        /// collection's (RoccatControllerDetect.cpp:392-405 against lines
        /// 122-153).</summary>
        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection?.Path == null) return null;
            OpenRgbRegistration registration = null;
            foreach (var r in OpenRgbRegistrations.Match(FamilyName, collection))
            {
                registration = r;
                break;
            }
            if (registration == null || registration.Detector != Detector) return null;
            var key = (registration.Name, collection.VendorId, collection.ProductId);
            lock (_models)
            {
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration.Name, collection.VendorId, collection.ProductId);
                return model;
            }
        }

        /// <summary>The LED collection's interface and usage page, and the
        /// control collection's, OpenRGB opens for a product
        /// (RoccatControllerDetect.cpp:122-153).</summary>
        internal static (int LedInterface, int LedPage, int ControlInterface, int ControlPage) Interfaces(ushort pid)
            => pid switch
            {
                VulcanPro or VulcanTklPro or Pyro or Magma or MagmaMini or VulcanII or VulcanIIMini or VulcanIIMax
                    or TurtleBeachVulcanII => (3, 0xFF00, 1, 0xFF01),
                TurtleBeachVulcanIITklPro => (4, 0xFF00, 1, 0x0001),
                _ => (3, 0x0001, 1, 0x000B),
            };

        internal HidLightingModel Model(string name, ushort vendorId, ushort productId)
        {
            string product = Product(vendorId, productId);
            string table = RoccatVulcanLayouts.TableOf(vendorId, productId);
            var (ledInterface, ledPage, _, _) = Interfaces(productId);
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = MapOf(table, RoccatVulcanLayouts.LayoutUs, product),
                Programs = VendorPrograms.Roccat,
                RepaintMs = RepaintMs,
                Siblings = new[] { new HidSibling(ledInterface, (ushort)ledPage) },
                BeginWith = (control, siblings, wait) => siblings?.Count == 1
                    ? Begin(control, siblings[0], wait, productId, table, product)
                    : null,
            };
        }

        internal static string Product(ushort vendorId, ushort productId) => $"roccat:{vendorId:X4}:{productId:X4}";

        /// <summary>The map of a table's entry for a reported layout.</summary>
        internal static LightMap MapOf(string table, int reported, string product)
        {
            var layout = RoccatVulcanLayouts.Find(table, reported);
            return new LightMap(RoccatVulcanLayouts.Lights(layout), "OpenRGB " + layout.Source) { Product = product };
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        /// <summary>The device info report's ID and length, the ID
        /// included (RoccatVulcanKeyboardController.cpp:77-100).</summary>
        internal static (byte Id, int Length) InfoReport(ushort pid) => SecondGeneration(pid) ? ((byte)0x09, 9) : ((byte)0x0F, 8);

        /// <summary>The ready report's length (lines 615-626).</summary>
        internal static int ReadyLength(ushort pid) => pid is Pyro or Magma or MagmaMini or VulcanPro or VulcanTklPro ? 4 : 3;

        /// <summary>The direct switch, EnableDirect (lines 166-195).</summary>
        internal static byte[] DirectSwitch(ushort pid, bool on) => SecondGeneration(pid)
            ? new byte[] { 0x0E, 0x05, (byte)(on ? 1 : 0), 0x00, 0x00 }
            : new byte[] { 0x15, 0x00, (byte)(on ? 1 : 0) };

        /// <summary>The mode report, SendMode with no colors (lines 400-609):
        /// the report ID, the length in one byte or two, then 00, the mode,
        /// the speed and the brightness in the order of the protocol, the
        /// Vulcan II Mini's and the Turtle Beach TKL Pro's own bytes, and a
        /// 16-bit sum of every byte before it, low byte first. Every mode
        /// PadForge sends has no colors (Direct and Default).</summary>
        internal static byte[] ModeReport(ushort pid, byte mode, byte speed = SpeedDefault, byte brightness = BrightnessDefault)
        {
            (int length, int protocol) = pid switch
            {
                Pyro => (365, 2),
                Magma or MagmaMini => (26, 2),
                VulcanPro or VulcanTklPro => (371, 2),
                VulcanII or TurtleBeachVulcanII => (377, 2),
                VulcanIIMax => (542, 2),
                VulcanIIMini => (332, 2),
                TurtleBeachVulcanIITklPro => (284, 2),
                _ => (443, 1),
            };
            var buf = new byte[length];
            int header = length > 255 ? 2 : 1;
            buf[0] = (byte)(protocol == 1 ? 0x0D : 0x11);
            if (header == 1)
            {
                buf[1] = (byte)length;
            }
            else
            {
                buf[1] = (byte)(length % 256);
                buf[2] = (byte)(length / 256);
            }
            int offset = header + 1;
            buf[offset + 0] = 0x00;
            buf[offset + 1] = mode;
            buf[offset + 2] = speed;
            if (protocol == 1)
            {
                buf[offset + 3] = 0x00;
                buf[offset + 4] = brightness;
                buf[offset + 5] = 0x00;
            }
            else
            {
                buf[offset + 3] = brightness;
                buf[offset + 4] = 0x00;
                buf[offset + 5] = 0x00;
            }
            if (pid == VulcanIIMini)
            {
                buf[offset + 0] = 0x02;
                buf[offset + 4] = 0xFB;
            }
            if (pid == TurtleBeachVulcanIITklPro)
            {
                buf[offset + 0] = 0x03;
                buf[offset + 4] = 0x0B;
                buf[offset + 5] = 0x01;
            }
            ushort total = 0;
            for (int i = 0; i < length - 2; i++) total += buf[i];
            buf[length - 2] = (byte)(total & 0xFF);
            buf[length - 1] = (byte)(total >> 8);
            return buf;
        }

        /// <summary>roccat_vulcan_ii_mini_slot_map (lines 23-32): where each
        /// of the Vulcan II Mini's 69 LED values sits in its frame.</summary>
        internal static readonly byte[] MiniSlots =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            18, 19, 20, 21, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33,
            34, 35, 36, 37, 38, 39, 40, 42, 43, 45, 46, 48, 49, 50, 51,
            52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 64, 65, 66, 68,
            70, 71, 72, 73, 74, 75, 76,
        };

        /// <summary>
        /// The color frame, SendColors (lines 197-394): packets of 65 bytes,
        /// byte 0 the report ID 0. A first-generation frame is one stream of
        /// 436 bytes cut into packets, A1 01 and the length leading it. A
        /// second-generation frame numbers every packet, A1 and the number,
        /// and the first carries the length. The colors go in blocks of
        /// <c>column_length</c> LEDs, the reds, then the greens, then the
        /// blues, except on the Turtle Beach TKL Pro, whose frame holds every
        /// red, then every green, then every blue. A length above 255 takes
        /// two bytes, high byte first on firmware OpenRGB reads as big-endian.
        /// A byte OpenRGB would write past the frame is left out: those
        /// writes run past the end of its buffers.
        /// </summary>
        internal static byte[][] ColorPackets(ushort pid, bool bigEndian, IReadOnlyList<(int Value, int Rgb)> colors)
        {
            (int length, int columnLength, int protocol) = pid switch
            {
                Magma or MagmaMini => (64, 5, 2),
                Pyro => (378, 1, 2),
                VulcanPro or VulcanTklPro => (384, 12, 2),
                VulcanII or TurtleBeachVulcanII => (396, 1, 2),
                VulcanIIMax => (567, 1, 2),
                VulcanIIMini => (345, 1, 2),
                TurtleBeachVulcanIITklPro => (320, 1, 2),
                _ => (436, 12, 1),
            };
            int count = (int)Math.Ceiling(length / 64.0);
            var bufs = new byte[count][];
            for (int p = 0; p < count; p++) bufs[p] = new byte[65];
            if (protocol > 1)
            {
                for (int p = 0; p < count; p++)
                {
                    bufs[p][1] = 0xA1;
                    bufs[p][2] = (byte)(p + 1);
                }
            }
            else
            {
                bufs[0][1] = 0xA1;
                bufs[0][2] = 0x01;
            }
            int headerFirst = length > 255 ? 4 : 3;
            if (headerFirst == 3)
            {
                bufs[0][3] = (byte)length;
            }
            else if (bigEndian)
            {
                bufs[0][3] = (byte)((length >> 8) & 0xFF);
                bufs[0][4] = (byte)(length & 0xFF);
            }
            else
            {
                bufs[0][3] = (byte)(length & 0xFF);
                bufs[0][4] = (byte)((length >> 8) & 0xFF);
            }
            int dataLength = 64 - headerFirst;

            void Put(int packet, int at, byte value)
            {
                if (packet >= 0 && packet < count && at >= 0 && at < 65) bufs[packet][at] = value;
            }

            int hwLeds = 0;
            if (pid == TurtleBeachVulcanIITklPro)
            {
                foreach (var (value, _) in colors)
                    if (value > hwLeds) hwLeds = value;
                hwLeds += 1;
            }
            foreach (var (value, rgb) in colors)
            {
                byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
                if (pid == TurtleBeachVulcanIITklPro)
                {
                    if (value >= hwLeds) continue;
                    int red = value, green = value + hwLeds, blue = value + 2 * hwLeds;
                    Put(red / dataLength, red % dataLength + headerFirst + 1, r);
                    Put(green / dataLength, green % dataLength + headerFirst + 1, g);
                    Put(blue / dataLength, blue % dataLength + headerFirst + 1, b);
                    continue;
                }
                int index = value;
                if (pid == VulcanIIMini)
                {
                    if (index >= MiniSlots.Length) continue;
                    index = MiniSlots[index];
                }
                int column = index / columnLength;
                int row = index % columnLength;
                if (protocol == 1)
                {
                    int offset = column * 3 * columnLength + row + headerFirst;
                    Put(offset / 64, offset % 64 + 1, r);
                    offset += columnLength;
                    Put(offset / 64, offset % 64 + 1, g);
                    offset += columnLength;
                    Put(offset / 64, offset % 64 + 1, b);
                }
                else
                {
                    int offset = column * 3 * columnLength + row;
                    Put(offset / dataLength, offset % dataLength + headerFirst + 1, r);
                    offset += columnLength;
                    Put(offset / dataLength, offset % dataLength + headerFirst + 1, g);
                    offset += columnLength;
                    Put(offset / dataLength, offset % dataLength + headerFirst + 1, b);
                }
            }
            return bufs;
        }

        /// <summary>IsBigEndianDirectMode (lines 155-164): never on a Vulcan
        /// II Mini, else a firmware whose major version is 1 or more and
        /// whose minor version is 16 or more, as OpenRGB compares them, so
        /// 2.05 reads as little-endian.</summary>
        internal static bool BigEndian(ushort pid, byte version)
            => pid != VulcanIIMini && version / 100 >= 1 && version % 100 >= 16;

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        /// <summary>WaitUntilReady (lines 611-649): the ready report read
        /// until its byte 1 is 1, 100 times at most, 25 ms apart. A keyboard
        /// that never reports ready is written to all the same, as OpenRGB
        /// does. False when a read failed.</summary>
        internal static bool WaitUntilReady(IHidReportChannel control, Action<int> wait, ushort pid)
        {
            var buf = new byte[ReadyLength(pid)];
            buf[0] = ReadyReport;
            for (int i = 0; buf[1] != 1 && i < ReadyTries; i++)
            {
                if (i != 0) wait(ReadyWaitMs);
                if (!control.GetFeature(ReadyReport, buf)) return false;
            }
            return true;
        }

        private static HidLightingSession Begin(IHidReportChannel control, IHidReportChannel led, Action<int> wait,
            ushort pid, string table, string product)
        {
            if (control == null || led == null) return null;
            var (id, length) = InfoReport(pid);
            var buf = new byte[length];
            buf[0] = id;
            if (!control.GetFeature(id, buf)) return null;
            // OpenRGB reads the report a second time into the same buffer
            // and takes its bytes from that read (lines 104-110 and 133-136).
            var again = new byte[length];
            again[0] = id;
            if (control.GetFeature(id, again)) Array.Copy(again, buf, length);
            byte version = buf[2];
            int layout = buf[6];
            // The Magma has no per-key lighting and OpenRGB takes the US
            // layout for it (lines 117-125).
            if (pid is Magma or MagmaMini) layout = RoccatVulcanLayouts.LayoutUs;
            return new Session(control, led, wait, pid, version, MapOf(table, layout, product));
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly IHidReportChannel _control;
            private readonly IHidReportChannel _led;
            private readonly Action<int> _wait;
            private readonly ushort _pid;
            private readonly LightMap _map;
            private bool _direct;
            private byte[][] _crash = Array.Empty<byte[]>();

            /// <summary>The control collection's channel and the LED
            /// collection's, which the worker opened and closes.</summary>
            public Session(IHidReportChannel control, IHidReportChannel led, Action<int> wait, ushort pid, byte version,
                LightMap map)
            {
                _control = control;
                _led = led;
                _wait = wait;
                _pid = pid;
                Version = version;
                _map = map;
            }

            public byte Version { get; }

            public bool BigEndian => RoccatVulcanLighting.BigEndian(_pid, Version);

            public override LightMap Map => _map;

            public override bool Feature => true;

            /// <summary>The Default mode and the direct switch off, once the
            /// Direct mode may have gone out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                wait ??= _wait;
                if (!_direct)
                {
                    // Marked first: a write that reports a failure may still
                    // have landed.
                    _crash = new[] { ModeReport(_pid, ModeDefault), DirectSwitch(_pid, false) };
                    if (!_control.SetFeature(ModeReport(_pid, ModeDirect))
                        || !WaitUntilReady(_control, wait, _pid)
                        || !_control.SetFeature(DirectSwitch(_pid, true))
                        || !WaitUntilReady(_control, wait, _pid))
                        return HidLightingPaint.Failed;
                    _direct = true;
                }
                var list = new List<(int, int)>(lights.Count);
                for (int i = 0; i < lights.Count && i < colors.Count; i++) list.Add((lights[i].Address, colors[i]));
                foreach (var packet in ColorPackets(_pid, BigEndian, list))
                    if (!_led.WriteOutput(packet)) return HidLightingPaint.Failed;
                var answer = new byte[65];
                for (int i = 0; i < DrainLimit; i++)
                {
                    int read = _led.ReadInput(answer, 0);
                    if (read < 0) return HidLightingPaint.Failed;
                    if (read == 0) break;
                }
                return _led.ReadInput(answer, AwaitMs) < 0 ? HidLightingPaint.Failed : HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_direct && _crash.Length == 0) return true;
                wait ??= _wait;
                if (!_control.SetFeature(ModeReport(_pid, ModeDefault))
                    || !WaitUntilReady(_control, wait, _pid)
                    || !_control.SetFeature(DirectSwitch(_pid, false))
                    || !WaitUntilReady(_control, wait, _pid))
                    return false;
                _direct = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
