using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What one step of a plugin device's frames does.</summary>
    internal enum PluginStepKind : byte
    {
        /// <summary>Writes an output report (device.write).</summary>
        Output,

        /// <summary>Sends a feature report (device.send_report).</summary>
        Feature,

        /// <summary>Reads the input report that answers a write, for at most
        /// <see cref="PluginStep.Value"/> ms (device.read). No answer in that
        /// time is no failure, as in the plugins.</summary>
        ReadInput,

        /// <summary>Reads the feature report with the ID in
        /// <see cref="PluginStep.Value"/> (device.get_report).</summary>
        ReadFeature,

        /// <summary>Waits <see cref="PluginStep.Value"/> ms (device.pause).</summary>
        Wait,
    }

    /// <summary>One step of a plugin device's frames.</summary>
    internal readonly record struct PluginStep(PluginStepKind Kind, byte[] Bytes, int Value)
    {
        public static PluginStep Output(byte[] frame) => new(PluginStepKind.Output, frame, 0);
        public static PluginStep Feature(byte[] frame) => new(PluginStepKind.Feature, frame, 0);
        public static PluginStep ReadInput(int ms = PluginDeviceLighting.AnswerMs) => new(PluginStepKind.ReadInput, null, ms);
        public static PluginStep ReadFeature(byte id) => new(PluginStepKind.ReadFeature, null, id);
        public static PluginStep Wait(int ms) => new(PluginStepKind.Wait, null, ms);
    }

    /// <summary>A keyboard's keys as a plugin lists them
    /// (<see cref="PluginKeyTables"/>): each key's name, LED index and
    /// place.</summary>
    internal sealed record PluginKeyTable(string Plugin, string[] Names, int[] Indexes, byte[] X, byte[] Y);

    /// <summary>One device a plugin drives, by its product ID: the
    /// collection its frames go to, its lights, and the frames.</summary>
    internal sealed class PluginDevice
    {
        public string Name { get; init; }
        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }

        /// <summary>The plugin, under signal-plugins/Plugins.</summary>
        public string Plugin { get; init; }

        /// <summary>The interface, usage page and usage a plugin's Validate
        /// asks of the collection. <see cref="OpenRgbRegistration.Any"/>:
        /// any.</summary>
        public int Interface { get; init; } = OpenRgbRegistration.Any;
        public int UsagePage { get; init; } = OpenRgbRegistration.Any;
        public int Usage { get; init; } = OpenRgbRegistration.Any;

        /// <summary>The report lengths the frames take, the report ID
        /// included. Plugins name an interface alone where several collections
        /// share it, and these tell the one that carries the frames.</summary>
        public int OutputLength { get; init; }
        public int FeatureLength { get; init; }

        public IReadOnlyList<LightInfo> Lights { get; init; }

        /// <summary>The colors <see cref="Picture"/> takes, by address.</summary>
        public int Slots { get; init; }

        /// <summary>The steps of one picture, from the colors by address.</summary>
        public Func<int[], IEnumerable<PluginStep>> Picture { get; init; }

        /// <summary>The steps of the plugin's Initialize, ahead of the first
        /// picture.</summary>
        public IReadOnlyList<PluginStep> Start { get; init; } = Array.Empty<PluginStep>();

        /// <summary>The steps that return the device to its own lighting.
        /// Empty for a device whose plugin only sends it one last color, which
        /// PadForge does not do.</summary>
        public IReadOnlyList<PluginStep> HandBack { get; init; } = Array.Empty<PluginStep>();

        /// <summary>The shortest time between two pictures. A plugin's Render
        /// runs at the renderer's own rate and names none, so this is
        /// PadForge's, unless the plugin waits after its frames.</summary>
        public int RepaintMs { get; init; } = PluginDeviceLighting.DefaultRepaintMs;

        public IReadOnlyList<VendorProgram> Programs { get; init; } = Array.Empty<VendorProgram>();

        /// <summary>A collection of the device the frames depend on, beside
        /// the unit's own.</summary>
        public IReadOnlyList<HidSibling> Siblings { get; init; } = Array.Empty<HidSibling>();

        /// <summary>For a device with <see cref="Siblings"/>: makes the
        /// session's <see cref="Picture"/> from the channels opened on
        /// them.</summary>
        public Func<IReadOnlyList<IHidReportChannel>, Func<int[], IEnumerable<PluginStep>>> PictureWith { get; init; }

        /// <summary>For a device whose lights are strips: the LEDs a strip can
        /// take, one entry per strip. <see cref="Slots"/> and the addresses then
        /// run by <see cref="StripPicture"/>'s rule, strip times
        /// <see cref="PluginDeviceLighting.StripSpan"/> plus the LED.</summary>
        public IReadOnlyList<int> StripMaxLeds { get; init; } = Array.Empty<int>();

        /// <summary>The LEDs a strip with no count set takes one color across,
        /// each plugin's own count for a strip it knows nothing about.</summary>
        public int StripDefaultLeds { get; init; }

        /// <summary>The steps of a picture for a device with strips, from the
        /// colors by address and each strip's LED count, its default where no
        /// count is set.</summary>
        public Func<int[], IReadOnlyList<int>, IEnumerable<PluginStep>> StripPicture { get; init; }

        /// <summary>For a device with <see cref="Siblings"/>: the steps of its
        /// start and hand-back that go to the first sibling.</summary>
        public IReadOnlyList<PluginStep> SiblingStart { get; init; } = Array.Empty<PluginStep>();
        public IReadOnlyList<PluginStep> SiblingHandBack { get; init; } = Array.Empty<PluginStep>();

        /// <summary>How often the session's steps of <see cref="Start"/> and a
        /// picture go out again with no change, for a device that can lose
        /// software control on its own. 0: never.</summary>
        public int KeepAliveMs { get; init; }

        /// <summary>Whether the frames are feature reports.</summary>
        public bool Feature => FeatureLength > 0 && OutputLength == 0;
    }

    /// <summary>
    /// The lit peripherals OpenRGB has no controller for and SignalRGB's plugins
    /// drive (#557). OpenRGB is PadForge's source wherever it has a device.
    /// signal-plugins (github.com/SRGBmods) is the source only for these, read
    /// as a record of what each device takes: its report lengths, its frames,
    /// its LED order. The repository has no license file, so none of its code
    /// is copied.
    ///
    /// <para>The steps of each device are the plugin's own: its Initialize,
    /// then per picture its Render, reads and waits included. An Initialize
    /// that fails is no failure, as in the plugins, which ignore what a write
    /// returns. Some Initialize frames name a report ID the collection may
    /// not have.</para>
    ///
    /// <para>The hand-back. Most of these plugins only send a last color the
    /// user chose, so their devices keep what they last showed until their own
    /// software or a power cycle changes it, and nothing goes out. A plugin
    /// whose Shutdown returns control to the device gives its frames here,
    /// with the color left out.</para>
    ///
    /// <para>What is not here. Parts inside a PC, network lights and devices
    /// with no lights: HyperX's Cloud Flight plugin says it lacks on-board
    /// LEDs and only reads the battery.</para>
    /// </summary>
    internal sealed partial class PluginDeviceLighting : IHidLightingFamily
    {
        public static readonly PluginDeviceLighting Instance = new();

        /// <summary>The bound on a read after a write. The plugins name none,
        /// and the devices answer at once, so a missing answer costs one
        /// frame's time.</summary>
        internal const int AnswerMs = 20;

        /// <summary>The shortest time between two pictures where a plugin
        /// names none: PadForge's.</summary>
        internal const int DefaultRepaintMs = 33;

        /// <summary>How far apart two strips' addresses are, more than any strip
        /// takes.</summary>
        internal const int StripSpan = 8192;

        public string Name => "Plugin devices";

        /// <summary>Every device of the table.</summary>
        internal static IReadOnlyList<PluginDevice> Devices => s_devices.Value;

        // Built on first use: the rows read tables that other parts of this
        // class initialize.
        private static readonly Lazy<IReadOnlyList<PluginDevice>> s_devices = new(() => BuildDevices().ToList());

        private static readonly Lazy<Dictionary<(ushort, ushort), List<PluginDevice>>> s_byProduct = new(() => Devices
            .GroupBy(d => (d.VendorId, d.ProductId))
            .ToDictionary(g => g.Key, g => g.ToList()));

        private readonly Dictionary<PluginDevice, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || !s_byProduct.Value.TryGetValue((collection.VendorId, collection.ProductId), out var rows))
                return null;
            int number = OpenRgbRegistrations.InterfaceOf(collection, OpenRgbRegistrations.BusOf(collection));
            foreach (var device in rows)
            {
                if (device.Interface != OpenRgbRegistration.Any && device.Interface != number) continue;
                if (device.UsagePage != OpenRgbRegistration.Any && device.UsagePage != collection.UsagePage) continue;
                if (device.Usage != OpenRgbRegistration.Any && device.Usage != collection.Usage) continue;
                if (collection.OutputReportLength < device.OutputLength) continue;
                if (collection.FeatureReportLength < device.FeatureLength) continue;
                lock (_models)
                {
                    if (!_models.TryGetValue(device, out var model)) _models[device] = model = ModelOf(device);
                    return model;
                }
            }
            return null;
        }

        private HidLightingModel ModelOf(PluginDevice device) => new()
        {
            Family = this,
            Name = device.Name,
            Map = MapOf(device, Array.Empty<int>()),
            Programs = device.Programs,
            RepaintMs = device.RepaintMs,
            KeepAliveMs = device.KeepAliveMs,
            KeepAliveIdleMs = device.KeepAliveMs,
            Siblings = device.Siblings,
            Begin = device.Siblings.Count > 0 ? null : (io, _) => io == null ? null : new Session(device, device.Picture),
            BeginWith = device.Siblings.Count == 0 ? null : (io, siblings, _) =>
                io == null ? null : new Session(device, device.PictureWith?.Invoke(siblings), siblings),
        };

        /// <summary>A device's lights. For strips: the count set for a strip
        /// gives it that many lights, and with none it is one light across its
        /// default LEDs.</summary>
        internal static LightMap MapOf(PluginDevice device, IReadOnlyList<int> counts)
        {
            string source = "SignalRGB " + device.Plugin;
            string product = $"plugin:{device.VendorId:X4}:{device.ProductId:X4}";
            if (device.StripMaxLeds.Count == 0) return new LightMap(device.Lights, source) { Product = product };
            var lights = new List<LightInfo>();
            var strips = new LightStrip[device.StripMaxLeds.Count];
            for (int s = 0; s < strips.Length; s++)
            {
                int leds = Math.Min(StripCounts.At(counts, s + 1), device.StripMaxLeds[s]);
                strips[s] = new LightStrip(s + 1, device.StripMaxLeds[s], leds);
                if (leds == 0) lights.Add(Zone(s * StripSpan, s + 1));
                else for (int l = 0; l < leds; l++) lights.Add(Edge(s * StripSpan + l));
            }
            return new LightMap(lights, source) { Product = product, Strips = strips };
        }

        /// <summary>A strip's colors from the colors by address, one per LED.</summary>
        internal static int[] StripColors(int[] byAddress, int strip, int leds, bool counted)
        {
            var colors = new int[leds];
            int start = strip * StripSpan;
            if (!counted) Array.Fill(colors, byAddress[start]);
            else Array.Copy(byAddress, start, colors, 0, leds);
            return colors;
        }

        // ─────────────────────────────────────────────
        //  Frames
        // ─────────────────────────────────────────────

        /// <summary>A frame of <paramref name="length"/> bytes that starts with
        /// <paramref name="head"/>, zero after it.</summary>
        internal static byte[] Frame(int length, params byte[] head)
        {
            var frame = new byte[length];
            head.CopyTo(frame, 0);
            return frame;
        }

        /// <summary>A frame of <paramref name="length"/> bytes with its bytes
        /// written out one by one at the offsets given, zero elsewhere.</summary>
        internal static byte[] Frame(int length, params (int At, byte Value)[] bytes)
        {
            var frame = new byte[length];
            foreach (var (at, value) in bytes) frame[at] = value;
            return frame;
        }

        internal static byte R(int color) => (byte)(color >> 16);
        internal static byte G(int color) => (byte)(color >> 8);
        internal static byte B(int color) => (byte)color;

        /// <summary>Puts a color at <paramref name="at"/> as red, green, blue.</summary>
        internal static void PutRgb(byte[] frame, int at, int color)
        {
            frame[at] = R(color);
            frame[at + 1] = G(color);
            frame[at + 2] = B(color);
        }

        // ─────────────────────────────────────────────
        //  Lights
        // ─────────────────────────────────────────────

        /// <summary>Numbered zones, each its index for its address.</summary>
        internal static IReadOnlyList<LightInfo> Zones(int count)
            => Enumerable.Range(0, count).Select(i => Zone(i, i + 1)).ToArray();

        /// <summary>Edge lights, each its index for its address.</summary>
        internal static IReadOnlyList<LightInfo> Edges(int count)
            => Enumerable.Range(0, count).Select(Edge).ToArray();

        internal static LightInfo Zone(int address, int number)
            => new(address, LightKind.Zone, 0, LightKeySource.None) { Zone = number };

        internal static LightInfo Edge(int address) => new(address, LightKind.Edge, 0, LightKeySource.None);
        internal static LightInfo Logo(int address) => new(address, LightKind.Logo, 0, LightKeySource.None);
        internal static LightInfo Status(int address) => new(address, LightKind.Status, 0, LightKeySource.None);

        internal static LightInfo Wheel(int address)
            => new(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.ScrollWheel };

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        internal sealed class Session : HidLightingSession
        {
            private readonly PluginDevice _device;
            private readonly Func<int[], IEnumerable<PluginStep>> _picture;
            private readonly IReadOnlyList<IHidReportChannel> _siblings;
            private readonly byte[][] _crashFrames;
            private bool _started;
            private LightMap _map;
            private IReadOnlyList<int> _counts = Array.Empty<int>();

            public Session(PluginDevice device, Func<int[], IEnumerable<PluginStep>> picture,
                IReadOnlyList<IHidReportChannel> siblings = null)
            {
                _device = device;
                _picture = picture;
                _siblings = siblings ?? Array.Empty<IHidReportChannel>();
                _map = device.StripMaxLeds.Count > 0 ? MapOf(device, _counts) : null;
                // The crash path sends one kind of report, the session's.
                var kind = device.Feature ? PluginStepKind.Feature : PluginStepKind.Output;
                _crashFrames = device.HandBack.Where(s => s.Kind == kind).Select(s => s.Bytes).ToArray();
            }

            public override bool Feature => _device.Feature;

            public override LightMap Map => _map;

            public override bool SizeStrips(IHidReportChannel io, Action<int> wait, IReadOnlyList<int> counts)
            {
                if (_device.StripMaxLeds.Count == 0) return true;
                _counts = counts ?? Array.Empty<int>();
                _map = MapOf(_device, _counts);
                return true;
            }

            /// <summary>The hand-back, once a picture may have gone out.</summary>
            public override byte[][] CrashFrames => _started ? _crashFrames : Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (!_started)
                {
                    _started = true;
                    if (_siblings.Count > 0) foreach (var step in _device.SiblingStart) Run(_siblings[0], wait, step);
                    foreach (var step in _device.Start) Run(io, wait, step);
                }
                var byAddress = CoolerMasterLighting.ByAddress(lights, colors, _device.Slots);
                var steps = _device.StripPicture != null
                    ? _device.StripPicture(byAddress, LedCounts())
                    : _picture(byAddress);
                foreach (var step in steps)
                    if (!Run(io, wait, step)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>A device with a keep-alive can lose software control
            /// on its own, so the keep-alive takes it again.</summary>
            public override bool KeepAlive(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return false;
                foreach (var step in _device.Start) Run(io, wait, step);
                return Paint(io, wait, lights, colors) != HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (!_started) return true;
                if (io == null) return false;
                foreach (var step in _device.HandBack)
                    if (!Run(io, wait, step)) return false;
                if (_siblings.Count > 0)
                    foreach (var step in _device.SiblingHandBack)
                        if (!Run(_siblings[0], wait, step)) return false;
                _started = false;
                return true;
            }

            /// <summary>Each strip's count, negative where none is set, which a
            /// picture reads as one color across the default LEDs.</summary>
            private int[] LedCounts()
            {
                var leds = new int[_device.StripMaxLeds.Count];
                for (int s = 0; s < leds.Length; s++)
                {
                    int count = Math.Min(StripCounts.At(_counts, s + 1), _device.StripMaxLeds[s]);
                    leds[s] = count > 0 ? count : -_device.StripDefaultLeds;
                }
                return leds;
            }

            /// <summary>Runs one step. False when a write failed. A read that
            /// brings nothing is no failure, as in the plugins, which use the
            /// read to pace and to keep the answers from piling up.</summary>
            private static bool Run(IHidReportChannel io, Action<int> wait, PluginStep step)
            {
                switch (step.Kind)
                {
                    case PluginStepKind.Output:
                        return io.WriteOutput(step.Bytes);
                    case PluginStepKind.Feature:
                        return io.SetFeature(step.Bytes);
                    case PluginStepKind.ReadInput:
                        io.ReadInput(new byte[Math.Max(io.InputReportLength, 1)], step.Value);
                        return true;
                    case PluginStepKind.ReadFeature:
                        io.GetFeature((byte)step.Value, new byte[Math.Max(io.FeatureReportLength, 1)]);
                        return true;
                    case PluginStepKind.Wait:
                        wait?.Invoke(step.Value);
                        return true;
                    default:
                        return true;
                }
            }
        }
    }
}
