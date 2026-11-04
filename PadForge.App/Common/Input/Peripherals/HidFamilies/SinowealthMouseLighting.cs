using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Sinowealth mice OpenRGB reaches through two HID collections
    /// (#557): the Glorious Model O and Model D and the Everest GT-100
    /// (DetectSinowealthMouse) and the Genesis Xenon 200
    /// (DetectGenesisXenon200).
    ///
    /// <para>The look. These mice show several collections on one
    /// interface, usage page and usage, and OpenRGB finds the two it needs
    /// by the feature reports each answers (SinowealthControllerDetect.cpp:93-255):
    /// for the Glorious mice the one that takes the 6-byte command report
    /// 0x05 and the one that answers report 0x04 of 520 bytes, with at least
    /// three collections on page 0xFF00 (lines 296-318), and for the Xenon
    /// 200 the one that answers report 0x04 of 154 bytes and the one that
    /// answers report 0x08 of 9, with at least five (lines 257-274). The
    /// worker hands a family one collection, so this family finds the two
    /// on its own from the HID walk (<see cref="IOwnLookFamily"/>), among
    /// the collections of the product on page 0xFF00 in one container: the
    /// data collection by its feature report's length, and the command
    /// collection by the command report's. A Glorious mouse whose command
    /// report sits in the data collection, where OpenRGB's two handles are
    /// one (SinowealthController.cpp:35-42), takes both through that
    /// collection. The unit's key is the data collection's path.</para>
    ///
    /// <para>The Glorious mice. OpenRGB has no Direct mode for them and
    /// colors their one light through the Static mode of their stored
    /// configuration ("@save :robot:", every mode with
    /// MODE_FLAG_AUTOMATIC_SAVE, RGBController_Sinowealth.cpp:14-45): the
    /// command 05 11 to the command collection, the configuration read as
    /// report 0x04 of 520 bytes from the data collection, its first 167
    /// bytes written back with byte 3 = 0x7B for a write, byte 6 = 0, the
    /// mode 0x02 at 0x35, the brightness in the high nibble of 0x38 and the
    /// color as red, blue, green at 0x39, then 50 ms
    /// (SinowealthController.cpp:91-220). OpenRGB writes nothing when the
    /// read gave fewer than 131 bytes. Static's brightness is OpenRGB's
    /// default, normal (2). The session keeps the configuration it read
    /// first, and the hand-back writes it back the same way, which puts
    /// back the mouse's own mode and colors.</para>
    ///
    /// <para>The Xenon 200. OpenRGB colors it through its Static mode too
    /// ("@save :white_check_mark:", RGBController_GenesisXenon200.cpp:12-46):
    /// report 0x04 read whole, the mode 0x18 at 0x5D, the brightness value
    /// at 0x60, red, green and blue at 0x61, written back, then report 0x08
    /// read from the command collection and sent back as it came
    /// (GenesisXenon200Controller.cpp:37-56, RGBController_GenesisXenon200.h:15-22).
    /// Static's brightness is OpenRGB's default, 1, whose value is 0x51.
    /// The hand-back writes back report 0x04 as the session first read it,
    /// and sends report 0x08 back the same way.</para>
    ///
    /// <para>The writes are stored, so a picture is held and pictures are
    /// spaced as the Lenovo Gen 10 keyboard's stored writes are
    /// (<see cref="LenovoLegionLighting"/>). What a session read first is
    /// kept in <see cref="KeyboardProfileBackups"/>, on disk before
    /// anything is written. A run that ends without its hand-back leaves
    /// that owed, and the next session of the mouse takes the backup as
    /// the mouse's own and hands it back when it ends. The crash path sends
    /// the backup to the data collection, which for the Xenon 200 leaves
    /// out the command collection's report, since that one needs a read
    /// first.</para>
    ///
    /// <para>No program is named for these mice: no source names the
    /// processes of Glorious CORE or Genesis's software.</para>
    /// </summary>
    internal sealed class SinowealthMouseLighting : IOwnLookFamily
    {
        public static readonly SinowealthMouseLighting Instance = new();

        internal const string MouseDetector = "DetectSinowealthMouse";
        internal const string XenonDetector = "DetectGenesisXenon200";
        internal const ushort VendorPage = 0xFF00;

        /// <summary>SINOWEALTH_CONFIG_SIZE, _MIN, _REPORT_SIZE and
        /// SINOWEALTH_COMMAND_REPORT_SIZE (SinowealthController.h:18-21).</summary>
        internal const int ConfigSize = 167;
        internal const int ConfigSizeMin = 131;
        internal const int ConfigReportSize = 520;
        internal const int CommandReportSize = 6;

        /// <summary>The Xenon 200's two reports (SinowealthControllerDetect.cpp:260).</summary>
        internal const int XenonReportSize = 154;
        internal const int XenonCommitSize = 9;

        /// <summary>The fewest collections on page 0xFF00 each detector
        /// wants (lines 262 and 303).</summary>
        internal const int MouseCollections = 3;
        internal const int XenonCollections = 5;

        /// <summary>GLORIOUS_MODE_STATIC and GLORIOUS_BRIGHTNESS_NORMAL
        /// (SinowealthController.h:23-49).</summary>
        internal const byte GloriousStatic = 0x02;
        internal const byte GloriousBrightnessNormal = 0x02;

        /// <summary>The wait after a configuration write (SinowealthController.cpp:181-187).</summary>
        internal const int GloriousWaitMs = 50;

        /// <summary>GENESIS_XENON_200_MODE_STATIC and the default
        /// brightness's value (RGBController_GenesisXenon200.h:15-22,
        /// RGBController_GenesisXenon200.cpp:35-46).</summary>
        internal const byte XenonStatic = 0x18;
        internal const byte XenonBrightness = 0x51;

        private readonly Func<IReadOnlyList<HidCollectionInfo>> _enumerate;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, IHidReportChannel> _open;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Dictionary<string, HidLightingModel> _models = new(StringComparer.OrdinalIgnoreCase);

        public SinowealthMouseLighting()
            : this(null, null, null, null) { }

        /// <summary>Test seam: the HID walk, the container lookup, the
        /// channel opener and the crash path's one feature report.</summary>
        internal SinowealthMouseLighting(Func<IReadOnlyList<HidCollectionInfo>> enumerate, Func<string, Guid> containerOf,
            Func<string, IHidReportChannel> open, Func<string, byte[], int, bool> setFeatureOnce)
        {
            // The walk opens each collection with no access right to read
            // its descriptor, which the test host leaves alone
            // (RealHidAccess).
            _enumerate = enumerate ?? (() => RealHidAccess.Refused ? Array.Empty<HidCollectionInfo>() : HidCollectionRuntime.Enumerate());
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _open = open ?? HidReportChannel.Open;
            _setFeatureOnce = setFeatureOnce ?? HidReportChannel.SetFeatureOnce;
        }

        public string Name => "Sinowealth mice";

        public HidLightingModel Match(HidCollectionInfo collection) => null;

        /// <summary>The detector of the family's registrations a collection
        /// answers to that this family lights, or null.</summary>
        internal static OpenRgbRegistration RegistrationOf(HidCollectionInfo collection)
        {
            foreach (var r in OpenRgbRegistrations.Match(SinowealthLighting.FamilyName, collection))
                if (r.Detector is MouseDetector or XenonDetector)
                    return r;
            foreach (var (vendor, product, name) in LibratbagMice)
                if (collection.VendorId == vendor && collection.ProductId == product)
                    return new OpenRgbRegistration(SinowealthLighting.FamilyName, name, MouseDetector, "libratbag", OpenRgbTransport.Hid,
                        vendor, product, OpenRgbRegistration.Any, VendorPage, OpenRgbRegistration.Any, OpenRgbRegistration.Any, true, false, "libratbag");
            return null;
        }

        /// <summary>Mice on the same configuration report that only libratbag lists, its
        /// sinowealth-0027, sinowealth-0051 and glorious-model-o-eternal device files. The
        /// offsets of its sinowealth_config_report are OpenRGB's: rgb_effect at 0x35,
        /// single_mode at 0x38 and single_color at 0x39 (driver-sinowealth.h).</summary>
        internal static readonly (ushort Vendor, ushort Product, string Name)[] LibratbagMice =
        {
            (0x258A, 0x0027, "DreamMachines DM5 Blink, Genesis Xenon 770, Glorious Model O"),
            (0x258A, 0x0051, "T-Dagger Imperial"),
            (0x3794, 0xA000, "Glorious Model O Eternal"),
        };

        /// <summary>libratbag's firmware versions with LedType=RGB, where OpenRGB's Static
        /// mode sends red, blue, green as the Glorious mice want it.</summary>
        internal static readonly HashSet<string> RgbFirmware = new(StringComparer.Ordinal) { "3106", "3110", "2642", "V127", "V287" };

        /// <summary>The firmware libratbag lists with LedType=None: the G-Wolves Hati HT-M.</summary>
        internal const string UnlitFirmware = "V161";

        /// <summary>sinowealth_get_fw_version (driver-sinowealth.c:1043-1056): 05 01, then
        /// the same report's four characters from byte 2. Null with no answer.</summary>
        internal static string ReadFirmware(Channels pair, Action<int> wait)
        {
            if (!pair.Command.SetFeature(new byte[] { 0x05, 0x01, 0x00, 0x00, 0x00, 0x00 })) return null;
            wait?.Invoke(10);
            var answer = new byte[Math.Max(CommandReportSize, pair.Command.FeatureReportLength)];
            if (!pair.Command.GetFeature(0x05, answer) || answer[1] != 0x01) return null;
            return System.Text.Encoding.ASCII.GetString(answer, 2, 4);
        }

        public IReadOnlyList<OwnDevice> Look()
        {
            IReadOnlyList<HidCollectionInfo> all;
            try { all = _enumerate(); }
            catch { return null; }
            if (all == null) return null;
            var found = new List<OwnDevice>();
            var containers = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            Guid ContainerOf(string path)
            {
                if (containers.TryGetValue(path, out var known)) return known;
                Guid container;
                try { container = _containerOf(path); }
                catch { container = Guid.Empty; }
                containers[path] = container;
                return container;
            }
            foreach (var data in all)
            {
                if (data?.Path == null || data.UsagePage != VendorPage) continue;
                var registration = RegistrationOf(data);
                if (registration == null) continue;
                bool xenon = registration.Detector == XenonDetector;
                if (data.FeatureReportLength != (xenon ? XenonReportSize : ConfigReportSize)) continue;
                Guid container = ContainerOf(data.Path);
                if (container == Guid.Empty) continue;
                // The command collection, and the count of the product's
                // collections on the vendor page, in this container.
                int commandLength = xenon ? XenonCommitSize : CommandReportSize;
                HidCollectionInfo command = null;
                int count = 0;
                foreach (var c in all)
                {
                    if (c?.Path == null || c.VendorId != data.VendorId || c.ProductId != data.ProductId
                        || c.UsagePage != VendorPage || ContainerOf(c.Path) != container)
                        continue;
                    count++;
                    if (command == null && c.FeatureReportLength == commandLength) command = c;
                }
                if (count < (xenon ? XenonCollections : MouseCollections)) continue;
                if (command == null)
                {
                    // The Xenon 200's commit report is a collection of its
                    // own, as the detector reads it apart (line 260).
                    if (xenon) continue;
                    command = data;
                }
                found.Add(new OwnDevice(data.Path, ModelFor(registration, data, command.Path))
                {
                    VendorId = data.VendorId, ProductId = data.ProductId, Container = container,
                });
            }
            return found;
        }

        /// <summary>The model of one mouse, the same object at every look
        /// while its two paths stay. Locked: a worker a timed-out Stop left
        /// behind can look while the next one does.</summary>
        private HidLightingModel ModelFor(OpenRgbRegistration registration, HidCollectionInfo data, string commandPath)
        {
            string key = data.Path + "|" + commandPath;
            lock (_models)
            {
                if (_models.TryGetValue(key, out var model)) return model;
                model = Model(registration.Name, data.ProductId, registration.Detector == XenonDetector, commandPath);
                _models[key] = model;
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId, bool xenon, string commandPath)
        {
            string product = SinowealthLighting.Product(productId);
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = new LightMap(SinowealthLighting.OneLight(),
                    xenon ? "OpenRGB RGBController_GenesisXenon200.cpp" : "OpenRGB RGBController_Sinowealth.cpp") { Product = product },
                HoldMs = SinowealthLighting.StoredHoldMs,
                RepaintMs = SinowealthLighting.StoredRepaintMs,
                Open = dataPath => OpenPair(dataPath, commandPath),
                BeginOwn = (handle, wait) => handle is Channels pair ? Begin(pair, wait, xenon, product) : null,
                WriteOnce = (dataPath, frame, timeoutMs) => _setFeatureOnce(dataPath, frame, timeoutMs),
            };
        }

        /// <summary>The data and the command collections of one mouse, open
        /// for read and write, closed together. One channel when they are
        /// one collection.</summary>
        internal sealed class Channels : IDisposable
        {
            public Channels(IHidReportChannel data, IHidReportChannel command)
            {
                Data = data;
                Command = command;
            }

            public IHidReportChannel Data { get; }
            public IHidReportChannel Command { get; }

            public void Dispose()
            {
                try { Data?.Dispose(); } catch { }
                if (!ReferenceEquals(Command, Data))
                {
                    try { Command?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>Opens both collections for read and write, or neither. A
        /// collection that opens with no access right only is held by
        /// another program.</summary>
        private Channels OpenPair(string dataPath, string commandPath)
        {
            IHidReportChannel data = null, command = null;
            try
            {
                data = _open(dataPath);
                if (data == null || data.FeatureOnly) return Close(data, null);
                if (string.Equals(dataPath, commandPath, StringComparison.OrdinalIgnoreCase))
                    return new Channels(data, data);
                command = _open(commandPath);
                if (command == null || command.FeatureOnly) return Close(data, command);
                return new Channels(data, command);
            }
            catch
            {
                return Close(data, command);
            }
        }

        private static Channels Close(IHidReportChannel data, IHidReportChannel command)
        {
            try { data?.Dispose(); } catch { }
            try { command?.Dispose(); } catch { }
            return null;
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        /// <summary>The command that asks for the configuration, GetProfile
        /// (SinowealthController.cpp:190-199).</summary>
        internal static byte[] ConfigRequest() => new byte[] { 0x05, 0x11, 0x00, 0x00, 0x00, 0x00 };

        /// <summary>GetProfile: the request to the command collection, then
        /// report 0x04 from the data collection (lines 190-220). Null when
        /// either failed.</summary>
        internal static byte[] ReadGloriousConfiguration(Channels pair)
        {
            if (!pair.Command.SetFeature(ConfigRequest())) return null;
            var answer = new byte[Math.Max(ConfigReportSize, pair.Data.FeatureReportLength)];
            if (!pair.Data.GetFeature(0x04, answer)) return null;
            var configuration = new byte[ConfigReportSize];
            Array.Copy(answer, configuration, ConfigReportSize);
            configuration[0] = 0x04;
            return configuration;
        }

        /// <summary>A configuration write, SetMode: the first 167 bytes of
        /// <paramref name="configuration"/>, byte 3 = 0x7B, byte 6 = 0, and
        /// for a color the Static mode, the brightness and red, blue and
        /// green (SinowealthController.cpp:91-121). The rest of the 520
        /// bytes is 0.</summary>
        internal static byte[] GloriousWrite(byte[] configuration, int? rgb, bool rgbOrder = false)
        {
            var report = new byte[ConfigReportSize];
            Array.Copy(configuration, report, Math.Min(ConfigSize, configuration.Length));
            report[0x03] = 0x7B;
            report[0x06] = 0x00;
            if (rgb is int color)
            {
                report[0x35] = GloriousStatic;
                report[0x38] = (byte)((GloriousBrightnessNormal & 0xF) << 4);
                report[0x39] = (byte)(color >> 16);
                report[0x3A] = (byte)(rgbOrder ? color >> 8 : color);
                report[0x3B] = (byte)(rgbOrder ? color : color >> 8);
            }
            return report;
        }

        /// <summary>Report 0x04 read whole from the Xenon 200's data
        /// collection (GenesisXenon200Controller.cpp:39-43), or null.</summary>
        internal static byte[] ReadXenonReport(IHidReportChannel data)
        {
            var answer = new byte[Math.Max(XenonReportSize, data.FeatureReportLength)];
            if (!data.GetFeature(0x04, answer)) return null;
            var report = new byte[XenonReportSize];
            Array.Copy(answer, report, XenonReportSize);
            report[0] = 0x04;
            return report;
        }

        /// <summary>SaveMode's write for the Static mode (lines 45-51).</summary>
        internal static byte[] XenonWrite(byte[] read, int rgb)
        {
            var report = (byte[])read.Clone();
            report[0x5D] = XenonStatic;
            report[0x60] = XenonBrightness;
            report[0x61] = (byte)(rgb >> 16);
            report[0x62] = (byte)(rgb >> 8);
            report[0x63] = (byte)rgb;
            return report;
        }

        /// <summary>SaveMode's commit (lines 53-55): the buffer's byte 0 set
        /// to 0x08, report 0x08 read into it from the command collection,
        /// and its first 9 bytes sent back. A read that fails leaves the
        /// buffer as it was, and OpenRGB sends it all the same.</summary>
        internal static bool XenonCommit(IHidReportChannel command, byte[] written)
        {
            var buffer = (byte[])written.Clone();
            buffer[0] = 0x08;
            var answer = new byte[Math.Max(XenonCommitSize, command.FeatureReportLength)];
            if (command.GetFeature(0x08, answer)) Array.Copy(answer, buffer, XenonCommitSize);
            var commit = new byte[XenonCommitSize];
            Array.Copy(buffer, commit, XenonCommitSize);
            return command.SetFeature(commit);
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        private static HidLightingSession Begin(Channels pair, Action<int> wait, bool xenon, string product)
        {
            bool rgbOrder = false;
            if (!xenon)
            {
                string firmware = ReadFirmware(pair, wait);
                if (firmware == UnlitFirmware) return null;
                rgbOrder = firmware != null && RgbFirmware.Contains(firmware);
            }
            var read = xenon ? ReadXenonReport(pair.Data) : ReadGloriousConfiguration(pair);
            if (read == null) return null;
            int length = xenon ? XenonReportSize : ConfigSize;
            var owed = KeyboardProfileBackups.Get(product);
            byte[] original;
            if (owed is { } backup && backup.Contents.Length == length)
                original = backup.Contents;
            else
            {
                original = new byte[length];
                Array.Copy(read, original, length);
            }
            return new Session(pair, xenon, product, original, rgbOrder);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Channels _pair;
            private readonly bool _xenon, _rgbOrder;
            private readonly string _product;
            private readonly byte[][] _crash;

            public Session(Channels pair, bool xenon, string product, byte[] original, bool rgbOrder = false)
            {
                _pair = pair;
                _xenon = xenon;
                _rgbOrder = rgbOrder;
                _product = product;
                Original = original;
                _crash = new[] { Restore() };
            }

            /// <summary>What the mouse held before PadForge wrote: its first
            /// 167 configuration bytes, or the Xenon 200's report 0x04.</summary>
            public byte[] Original { get; }

            public override bool Feature => true;

            public override byte[][] CrashFrames => KeyboardProfileBackups.Get(_product) != null ? _crash : Array.Empty<byte[]>();

            /// <summary>The write that puts <see cref="Original"/> back.</summary>
            internal byte[] Restore() => _xenon ? (byte[])Original.Clone() : GloriousWrite(Original, null);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (KeyboardProfileBackups.Get(_product) == null)
                    KeyboardProfileBackups.Take(_product, 0, Original);
                if (!KeyboardProfileBackups.IsPersisted(_product))
                {
                    LaterMs = SinowealthLighting.SavingMs;
                    return HidLightingPaint.Later;
                }
                int rgb = colors != null && colors.Count > 0 ? colors[0] & 0xFFFFFF : 0;
                if (_xenon)
                {
                    // SaveMode reads the report again before each write.
                    var read = ReadXenonReport(_pair.Data);
                    if (read == null) return HidLightingPaint.Failed;
                    var written = XenonWrite(read, rgb);
                    if (!_pair.Data.SetFeature(written)) return HidLightingPaint.Failed;
                    return XenonCommit(_pair.Command, written) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
                }
                var configuration = ReadGloriousConfiguration(_pair);
                if (configuration == null) return HidLightingPaint.Failed;
                if (!_pair.Data.SetFeature(GloriousWrite(configuration, rgb, _rgbOrder))) return HidLightingPaint.Failed;
                wait(GloriousWaitMs);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (KeyboardProfileBackups.Get(_product) == null) return true;
                var restore = Restore();
                if (!_pair.Data.SetFeature(restore)) return false;
                if (_xenon)
                {
                    if (!XenonCommit(_pair.Command, restore)) return false;
                }
                else
                {
                    wait(GloriousWaitMs);
                }
                KeyboardProfileBackups.Drop(_product);
                return true;
            }
        }
    }
}
