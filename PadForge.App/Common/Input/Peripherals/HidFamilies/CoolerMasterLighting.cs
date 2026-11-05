using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Cooler Master's keyboards, mice, mousepads, monitors, gaming desk,
    /// graphics cards and RGB controller, lit as OpenRGB's
    /// CoolerMasterController family lights them (#557). The collections
    /// are OpenRGB's registrations (CoolerMasterControllerDetect.cpp:389-451),
    /// matched by OpenRGB's rules, and the detector a collection answers to
    /// picks the protocol.
    ///
    /// <para>Every frame is an output report of 65 bytes with report ID 0,
    /// as every controller of the family writes it with hid_write. Where
    /// OpenRGB reads an answer after a write, PadForge reads one too. A read
    /// OpenRGB makes with hid_read and no time limit (the keyboards and the
    /// mousepad) waits up to <see cref="AnswerMs"/> here, and no answer in
    /// that time is a failure. A read OpenRGB bounds
    /// (hid_read_timeout, 250 ms in every controller's header) is bounded the
    /// same, and no answer is not a failure, as in OpenRGB. An answer is
    /// taken as hidapi hands it over: on Windows hidapi drops a report ID of
    /// 0 from the front (hidapi windows/hid.c:1221-1228), so byte k of an
    /// answer in OpenRGB is byte k + 1 of the report Windows delivers.</para>
    ///
    /// <para>The MK850 (0x0069) and the SK620 Black and White (0x0157,
    /// 0x0159). OpenRGB registers all three and holds their layouts, and
    /// DetectCoolerMasterV2Keyboards has no case for them, so it makes no
    /// controller (CoolerMasterControllerDetect.cpp:180-229). The SK620 has its
    /// case in the V2 controller's Initialize (CMKeyboardV2Controller.cpp:113-116),
    /// and the commit that added it means it lit ("add sk620", d86f59cf), so
    /// PadForge lights it as a V2 keyboard. The MK850 takes the frames of
    /// SignalRGB's plugin, the one source that drives it (CoolerMasterMk850.cs).
    /// The ARGB and Small ARGB controllers are lit only on the firmware their
    /// serial string names (CoolerMasterArgb.cs).</para>
    /// </summary>
    internal sealed partial class CoolerMasterLighting : IHidLightingFamily
    {
        public static readonly CoolerMasterLighting Instance = new();

        /// <summary>The family's folder name in OpenRGB, the key of its
        /// registrations.</summary>
        internal const string OpenRgbFamily = "CoolerMasterController";

        /// <summary>COOLERMASTER_VID (CoolerMasterControllerDetect.cpp:36).</summary>
        internal const ushort VendorId = 0x2516;

        /// <summary>The length of every frame, the report ID included
        /// (CM_KEYBOARD_WRITE_SIZE, CMKeyboardAbstractController.h:27, and the
        /// PACKET_SIZE or PACKET_LENGTH of every other controller).</summary>
        internal const int PacketLength = 65;

        /// <summary>The bound OpenRGB gives a read after a write
        /// (CM_MM_INTERRUPT_TIMEOUT, CMMMController.h:21, and the same 250 in
        /// every other controller's header).</summary>
        internal const int ReadMs = 250;

        /// <summary>How long a read OpenRGB makes without a bound waits here:
        /// hidapi's own bound for a write (hid.c:234). PadForge's.</summary>
        internal const int AnswerMs = 1000;

        /// <summary>The shortest time between two pictures for a device whose
        /// colors are not stored. No reference bounds the rate, so the bound
        /// is PadForge's, as the ASUS TUF family's.</summary>
        internal const int RepaintMs = 50;

        public string Name => "Cooler Master";

        private readonly ConcurrentDictionary<(string Detector, int ProductId), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily,
                collection.ProductId == Sk650Other ? collection with { ProductId = Sk650 }
                : collection.ProductId == Sk621 ? collection with { ProductId = Sk622B } : collection);
            if (registrations.Count == 0) return null;
            // OpenRGB runs the detectors in this order and keeps the first
            // that makes a controller.
            var registration = registrations[0];
            // The Radeon cards' registration names an interface and no usage
            // page, and every frame of theirs is an output report of this length.
            if (registration.Detector == "DetectCoolerMasterGPU" && collection.OutputReportLength < PacketLength) return null;
            var key = (registration.Detector, (int)collection.ProductId);
            if (_models.TryGetValue(key, out var known)) return known;
            var model = Model(registration, collection.ProductId);
            if (model == null) return null;
            return _models.GetOrAdd(key, model);
        }

        /// <summary>The model a detector makes for a product, or null where
        /// OpenRGB makes none PadForge can light.</summary>
        internal static HidLightingModel Model(OpenRgbRegistration registration, ushort productId)
        {
            switch (registration.Detector)
            {
                case "DetectCoolerMasterV1Keyboards":
                case "DetectCoolerMasterV2Keyboards":
                    return KeyboardModel(registration.Name, productId);
                case "DetectCoolerMasterMouse":
                    return MouseModel(registration.Name, productId);
                case "DetectCoolerMasterMouse711":
                    return Mm711Model(registration.Name, productId);
                case "DetectCoolerMasterMouse712":
                    return Mm712Model(registration.Name, productId);
                case "DetectCoolerMasterMousemats":
                    return Mp750Model(registration.Name, productId);
                case "DetectCoolerMasterMonitor":
                    return MonitorModel(registration.Name, productId);
                case "DetectCoolerMasterGD160":
                    return Gd160Model(registration.Name, productId);
                case "DetectCoolerMasterGPU":
                    return GpuModel(registration.Name, productId);
                case "DetectCoolerMasterRGB":
                    return RgbControllerModel(registration.Name, productId);
                case "DetectCoolerMasterARGB":
                    return ArgbModel(registration.Name, productId);
                case "DetectCoolerMasterSmallARGB":
                    return SmallArgbModel(registration.Name, productId);
                case "DetectCoolerMasterARGBGen2A1":
                    return ArgbGen2A1Model(registration.Name, productId);
                default:
                    return null;
            }
        }

        /// <summary>The product key a model's map is stored under.</summary>
        internal static string Product(ushort productId) => $"coolermaster:{VendorId:X4}:{productId:X4}";

        private static HidLightingModel NewModel(string name, ushort productId, IReadOnlyList<LightInfo> lights,
            string source, Func<IHidReportChannel, Action<int>, HidLightingSession> begin,
            int repaintMs = RepaintMs, int holdMs = 0)
            => new()
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(lights, source) { Product = Product(productId) },
                Programs = new[] { VendorPrograms.MasterPlus },
                RepaintMs = repaintMs,
                HoldMs = holdMs,
                Begin = begin,
            };

        // ─────────────────────────────────────────────
        //  Frames and exchanges
        // ─────────────────────────────────────────────

        /// <summary>A frame: report ID 0, then <paramref name="payload"/>,
        /// then <paramref name="fill"/> to the packet's end, as SendCommand
        /// fills it (CMKeyboardAbstractController.cpp:88-100).</summary>
        internal static byte[] Packet(ReadOnlySpan<byte> payload, byte fill = 0)
        {
            var frame = new byte[PacketLength];
            if (fill != 0) frame.AsSpan(1).Fill(fill);
            payload.CopyTo(frame.AsSpan(1));
            return frame;
        }

        /// <summary>An answer as hidapi hands it over on Windows: a report
        /// that starts with report ID 0 loses that byte
        /// (hidapi windows/hid.c:1221-1228).</summary>
        internal static byte[] AsHidapi(byte[] report, int count)
        {
            if (report == null || count <= 0) return Array.Empty<byte>();
            count = Math.Min(count, report.Length);
            return report[0] == 0 ? report.AsSpan(1, count - 1).ToArray() : report.AsSpan(0, count).ToArray();
        }

        /// <summary>One read after a write. Null when the read failed, an
        /// empty answer when none came in time.</summary>
        internal static byte[] Read(IHidReportChannel io, int timeoutMs)
        {
            var buffer = new byte[Math.Max(PacketLength, io.InputReportLength)];
            int count = io.ReadInput(buffer, timeoutMs);
            if (count < 0) return null;
            return AsHidapi(buffer, count);
        }

        /// <summary>A write and the read OpenRGB makes after it. Null when the
        /// write or the read failed, or, with <paramref name="required"/>,
        /// when no answer came within <see cref="AnswerMs"/>.</summary>
        internal static byte[] Exchange(IHidReportChannel io, byte[] frame, bool required, int timeoutMs = ReadMs)
        {
            if (io == null || !io.WriteOutput(frame)) return null;
            var answer = Read(io, required ? AnswerMs : timeoutMs);
            if (answer == null || (required && answer.Length == 0)) return null;
            return answer;
        }

        /// <summary>Writes each frame and reads the answer OpenRGB reads after
        /// it. False when one failed.</summary>
        internal static bool ExchangeAll(IHidReportChannel io, IEnumerable<byte[]> frames, bool required)
        {
            foreach (var frame in frames)
                if (Exchange(io, frame, required) == null) return false;
            return true;
        }

        /// <summary>Writes each frame with no read after it.</summary>
        internal static bool WriteAll(IHidReportChannel io, IEnumerable<byte[]> frames)
        {
            if (io == null) return false;
            foreach (var frame in frames)
                if (!io.WriteOutput(frame)) return false;
            return true;
        }

        internal static byte R(int color) => (byte)(color >> 16);
        internal static byte G(int color) => (byte)(color >> 8);
        internal static byte B(int color) => (byte)color;

        /// <summary>The colors of a picture by the lights' addresses, a later
        /// light over an earlier one at the same address, as OpenRGB fills its
        /// color map (CMKeyboardV1Controller.cpp:384-387).</summary>
        internal static int[] ByAddress(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int size)
        {
            var map = new int[size];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int address = lights[i].Address;
                if ((uint)address < (uint)size) map[address] = colors[i] & 0xFFFFFF;
            }
            return map;
        }

        /// <summary>A session that hands back with a fixed list of frames
        /// and sends them, with their reads, from <see cref="HandBack"/>. The
        /// crash path sends the same frames.</summary>
        internal abstract class Session : HidLightingSession
        {
            private byte[][] _handBack = Array.Empty<byte[]>();
            private byte[][] _crash = Array.Empty<byte[]>();

            public override bool Feature => false;

            public override byte[][] CrashFrames => _crash;

            /// <summary>The frames that return the device to its own
            /// lighting.</summary>
            public byte[][] HandBackFrames => _handBack;

            /// <summary>Sends the hand-back frames, by default each with the
            /// read OpenRGB makes after it.</summary>
            protected virtual bool SendHandBack(IHidReportChannel io) => ExchangeAll(io, _handBack, required: false);

            protected void SetHandBack(byte[][] frames) => _handBack = frames ?? Array.Empty<byte[]>();

            /// <summary>Puts the hand-back on the crash path's record once a
            /// picture may have gone out.</summary>
            protected void Owe()
            {
                if (_crash.Length == 0 && _handBack.Length > 0) _crash = _handBack;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                bool sent = SendHandBack(io);
                if (sent) _crash = Array.Empty<byte[]>();
                return sent;
            }
        }
    }
}
