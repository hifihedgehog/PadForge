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
    /// read gave fewer than 131 bytes, libratbag when it gave fewer than 123
    /// (driver-sinowealth.c:70 and 1183-1186), so the count of the read is
    /// read too (<see cref="IHidReportChannel.GetFeatureCounted"/>). Static's
    /// brightness is OpenRGB's default, normal (2). The session keeps the
    /// configuration it read first, and the hand-back reads the mouse again
    /// and puts back the mode and the Static fields, which leaves a DPI or a
    /// report rate the user changed meanwhile as it stands.</para>
    ///
    /// <para>The mice only libratbag lists. libratbag lights a firmware its
    /// device file has a section for and refuses any other ("Device with
    /// firmware version ... is not supported", driver-sinowealth.c:1730-1738),
    /// leaves alone a mouse whose effect byte says it has no LEDs
    /// (RGB_NOT_SUPPORTED, lines 191-195 and 1803), and writes the size the
    /// read gave less 8 where OpenRGB writes 0x7B
    /// (line 1953, data/devices/sinowealth-0051.device: "Configuration data
    /// size: 123 bytes"). A session does the same.</para>
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
    /// that owed. With no claim on the mouse the next run reads it and puts
    /// the lighting back, and the next session takes the backup as the mouse's
    /// own and hands it back when it ends. Either way a mouse that no longer
    /// shows a picture of PadForge's was set by something else, and the backup
    /// goes. Each mouse keeps a backup of its own
    /// (<see cref="KeyboardProfileBackups.DeviceKey"/>). The crash path sends
    /// the backup to the data collection, which for the Xenon 200 leaves
    /// out the command collection's report, since that one needs a read
    /// first.</para>
    ///
    /// <para>The Model O Eternal is left to Glorious CORE while it runs
    /// (<see cref="VendorPrograms.Glorious"/>). The first wired Model O and
    /// Model D are not among the products CORE lights, and no source names the
    /// processes of Genesis's software.</para>
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

        /// <summary>The least a read gives on a mouse libratbag lights
        /// (SINOWEALTH_CONFIG_SIZE_MIN, driver-sinowealth.c:70).</summary>
        internal const int LibratbagConfigSizeMin = 123;

        /// <summary>The write length OpenRGB always sends
        /// (SinowealthController.cpp:105).</summary>
        internal const byte ConfigWrite = 0x7B;

        /// <summary>RGB_NOT_SUPPORTED (driver-sinowealth.c:191-195).</summary>
        internal const byte EffectNoLeds = 0xFF;

        /// <summary>The Model O Eternal's product ID (its libratbag device
        /// file), the one mouse of this family Glorious CORE lights.</summary>
        internal const ushort EternalProduct = 0xA000;
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

        /// <summary>The firmware each mouse only libratbag lists has a section for with lights, in
        /// its device file: sinowealth-0027, sinowealth-0051 and glorious-model-o-eternal.</summary>
        internal static readonly IReadOnlyDictionary<ushort, string[]> LibratbagFirmware = new Dictionary<ushort, string[]>
        {
            [0x0027] = new[] { "3106", "3110", "V102" },
            [0x0051] = new[] { "2642" },
            [EternalProduct] = new[] { "1009" },
        };

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

        public IReadOnlyList<OwnDevice> Look() => Look(null);

        /// <summary>The look through the walk the worker holds, which saves a
        /// walk of its own at each of the worker's.</summary>
        public IReadOnlyList<OwnDevice> Look(IReadOnlyList<HidCollectionInfo> walk)
        {
            IReadOnlyList<HidCollectionInfo> all = walk;
            if (all == null)
            {
                try { all = _enumerate(); }
                catch { return null; }
            }
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
            bool held = false;
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = new LightMap(SinowealthLighting.OneLight(),
                    xenon ? "OpenRGB RGBController_GenesisXenon200.cpp" : "OpenRGB RGBController_Sinowealth.cpp") { Product = product },
                Programs = productId == EternalProduct ? VendorPrograms.Glorious : Array.Empty<VendorProgram>(),
                HoldMs = SinowealthLighting.StoredHoldMs,
                RepaintMs = SinowealthLighting.StoredRepaintMs,
                Open = dataPath => OpenPair(dataPath, commandPath, out held),
                OpenFoundHeld = () => held,
                BeginOwn = (handle, wait) => handle is Channels pair ? Begin(pair, wait, xenon, product, productId) : null,
                Owed = path => KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, path)) != null
                               && KeyboardProfileBackups.Failures(KeyboardProfileBackups.DeviceKey(product, path)) < SinowealthLighting.RepairTries,
                RepairOwn = (handle, wait) => handle is not Channels pair || Repair(pair, wait, xenon, product, productId),
                WriteOnce = (dataPath, frame, timeoutMs) => _setFeatureOnce(dataPath, frame, timeoutMs),
            };
        }

        /// <summary>The data and the command collections of one mouse, open
        /// for read and write, closed together. One channel when they are
        /// one collection. A mouse plugged back in has new handles for both,
        /// so either one dead says the pair is.</summary>
        internal sealed class Channels : IDisposable, IConnectedChannel
        {
            public Channels(IHidReportChannel data, IHidReportChannel command)
            {
                Data = data;
                Command = command;
            }

            public IHidReportChannel Data { get; }
            public IHidReportChannel Command { get; }

            public bool StillConnected => Connected(Data) && Connected(Command);

            private static bool Connected(IHidReportChannel channel) => channel is not IConnectedChannel { StillConnected: false };

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
        /// another program, as <paramref name="held"/> says: both sit on the
        /// vendor page, which Windows does not hold.</summary>
        private Channels OpenPair(string dataPath, string commandPath, out bool held)
        {
            held = false;
            IHidReportChannel data = null, command = null;
            try
            {
                data = _open(dataPath);
                held = data is { FeatureOnly: true };
                if (data == null || data.FeatureOnly) return Close(data, null);
                if (string.Equals(dataPath, commandPath, StringComparison.OrdinalIgnoreCase))
                    return new Channels(data, data);
                command = _open(commandPath);
                held = command is { FeatureOnly: true };
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
        /// either failed or the answer was shorter than
        /// <paramref name="fewest"/>, which SetMode writes nothing on
        /// (line 100). <paramref name="count"/> is the answer's length, 0 when
        /// the channel cannot tell.</summary>
        internal static byte[] ReadGloriousConfiguration(Channels pair, int fewest, out int count)
        {
            count = -1;
            if (!pair.Command.SetFeature(ConfigRequest())) return null;
            var answer = new byte[Math.Max(ConfigReportSize, pair.Data.FeatureReportLength)];
            count = pair.Data.GetFeatureCounted(0x04, answer);
            if (count < 0 || (count > 0 && count < fewest)) return null;
            var configuration = new byte[ConfigReportSize];
            Array.Copy(answer, configuration, ConfigReportSize);
            configuration[0] = 0x04;
            return configuration;
        }

        /// <summary>The length byte a write carries: OpenRGB's, or on a mouse
        /// only libratbag lists the size its read gave less 8
        /// (driver-sinowealth.c:1953). A count outside libratbag's bounds, or
        /// a channel that cannot tell, leaves OpenRGB's.</summary>
        internal static byte WriteLength(bool libratbagOnly, int count)
            => libratbagOnly && count >= LibratbagConfigSizeMin && count <= ConfigSize ? (byte)(count - 8) : ConfigWrite;

        /// <summary>Whether a configuration shows what
        /// <see cref="GloriousWrite"/> leaves for a color: the Static mode at
        /// the normal brightness.</summary>
        internal static bool GloriousLooksOurs(byte[] configuration)
            => configuration[0x35] == GloriousStatic && configuration[0x38] == (byte)((GloriousBrightnessNormal & 0xF) << 4);

        /// <summary>Whether a Xenon 200's report shows what
        /// <see cref="XenonWrite"/> leaves: the Static mode at its brightness.</summary>
        internal static bool XenonLooksOurs(byte[] report)
            => report[0x5D] == XenonStatic && report[0x60] == XenonBrightness;

        /// <summary>A configuration write, SetMode: the first 167 bytes of
        /// <paramref name="configuration"/>, byte 3 the write length, byte 6 =
        /// 0, and for a color the Static mode, the brightness and red, blue
        /// and green (SinowealthController.cpp:91-121). The rest of the 520
        /// bytes is 0.</summary>
        internal static byte[] GloriousWrite(byte[] configuration, int? rgb, bool rgbOrder = false, byte writeLength = ConfigWrite)
        {
            var report = new byte[ConfigReportSize];
            Array.Copy(configuration, report, Math.Min(ConfigSize, configuration.Length));
            report[0x03] = writeLength;
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

        private static HidLightingSession Begin(Channels pair, Action<int> wait, bool xenon, string product, ushort productId)
        {
            bool rgbOrder = false;
            bool libratbagOnly = false;
            if (!xenon)
            {
                string firmware = ReadFirmware(pair, wait);
                if (firmware == UnlitFirmware) return null;
                libratbagOnly = LibratbagFirmware.TryGetValue(productId, out var listed);
                if (libratbagOnly && Array.IndexOf(listed, firmware) < 0) return null;
                rgbOrder = firmware != null && RgbFirmware.Contains(firmware);
            }
            int fewest = libratbagOnly ? LibratbagConfigSizeMin : ConfigSizeMin;
            int count = 0;
            var read = xenon ? ReadXenonReport(pair.Data) : ReadGloriousConfiguration(pair, fewest, out count);
            if (read == null) return null;
            if (libratbagOnly && read[0x35] == EffectNoLeds) return null;
            int length = xenon ? XenonReportSize : ConfigSize;
            string key = KeyboardProfileBackups.SessionKey(product, pair.Data.Path);
            var original = new byte[length];
            Array.Copy(read, original, length);
            if (KeyboardProfileBackups.Get(key) is { } backup)
            {
                bool ours = xenon ? XenonLooksOurs(read) : GloriousLooksOurs(read);
                if (backup.Contents.Length == length && (ours || backup.Contents.AsSpan().SequenceEqual(original)))
                    original = backup.Contents;
                else
                    KeyboardProfileBackups.Drop(key);
            }
            return new Session(pair, xenon, key, original, rgbOrder, fewest, WriteLength(libratbagOnly, count));
        }

        /// <summary>The write that puts back only what a picture moved, over
        /// what the mouse holds now: the mode, the brightness and the color.</summary>
        private static byte[] RestoreOver(byte[] now, byte[] original, bool xenon, byte writeLength)
        {
            if (xenon)
            {
                var report = (byte[])now.Clone();
                report[0x5D] = original[0x5D];
                Array.Copy(original, 0x60, report, 0x60, 4);
                return report;
            }
            var write = GloriousWrite(now, null, false, writeLength);
            write[0x35] = original[0x35];
            Array.Copy(original, 0x38, write, 0x38, 4);
            return write;
        }

        /// <summary>Sends a restore: the Xenon 200's with its commit, the
        /// others followed by the wait a read needs. False when a write
        /// failed.</summary>
        private static bool SendRestore(Channels pair, Action<int> wait, bool xenon, byte[] restore)
        {
            if (!pair.Data.SetFeature(restore)) return false;
            if (xenon) return XenonCommit(pair.Command, restore);
            wait(GloriousWaitMs);
            return true;
        }

        /// <summary>Settles a backup an earlier run left, with no claim on the
        /// mouse, reading it first as <see cref="AsusLegacyUsbLighting.RepairSagaris"/>
        /// does. A mouse that shows the Static mode at PadForge's brightness
        /// takes its lighting back, and any other keeps what it holds. True
        /// when nothing is owed any more.</summary>
        private static bool Repair(Channels pair, Action<int> wait, bool xenon, string product, ushort productId)
        {
            string key = KeyboardProfileBackups.DeviceKey(product, pair.Data.Path);
            if (KeyboardProfileBackups.Get(key) is not { } backup) return true;
            bool libratbagOnly = !xenon && LibratbagFirmware.ContainsKey(productId);
            int count = 0;
            var now = xenon
                ? ReadXenonReport(pair.Data)
                : ReadGloriousConfiguration(pair, libratbagOnly ? LibratbagConfigSizeMin : ConfigSizeMin, out count);
            if (now != null)
            {
                int length = xenon ? XenonReportSize : ConfigSize;
                if (backup.Contents.Length != length
                    || now.AsSpan(0, length).SequenceEqual(backup.Contents)
                    || !(xenon ? XenonLooksOurs(now) : GloriousLooksOurs(now))
                    || SendRestore(pair, wait, xenon, RestoreOver(now, backup.Contents, xenon, WriteLength(libratbagOnly, count))))
                {
                    KeyboardProfileBackups.Drop(key);
                    return true;
                }
            }
            KeyboardProfileBackups.NoteFailure(key);
            return false;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly Channels _pair;
            private readonly bool _xenon, _rgbOrder;
            private readonly string _key;
            private readonly int _fewest;
            private readonly byte _writeLength;
            private readonly byte[][] _crash;

            public Session(Channels pair, bool xenon, string key, byte[] original, bool rgbOrder = false,
                int fewest = ConfigSizeMin, byte writeLength = ConfigWrite)
            {
                _pair = pair;
                _xenon = xenon;
                _rgbOrder = rgbOrder;
                _key = key;
                _fewest = fewest;
                _writeLength = writeLength;
                Original = original;
                _crash = new[] { Restore() };
            }

            /// <summary>What the mouse held before PadForge wrote: its first
            /// 167 configuration bytes, or the Xenon 200's report 0x04.</summary>
            public byte[] Original { get; }

            public override bool Feature => true;

            public override byte[][] CrashFrames => KeyboardProfileBackups.Get(_key) != null ? _crash : Array.Empty<byte[]>();

            /// <summary>The write that puts <see cref="Original"/> back whole,
            /// for the crash path, which cannot read first.</summary>
            internal byte[] Restore() => _xenon ? (byte[])Original.Clone() : GloriousWrite(Original, null, false, _writeLength);

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (KeyboardProfileBackups.Get(_key) == null)
                    KeyboardProfileBackups.Take(_key, 0, Original);
                if (!KeyboardProfileBackups.IsPersisted(_key))
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
                var configuration = ReadGloriousConfiguration(_pair, _fewest, out _);
                if (configuration == null) return HidLightingPaint.Failed;
                if (!_pair.Data.SetFeature(GloriousWrite(configuration, rgb, _rgbOrder, _writeLength))) return HidLightingPaint.Failed;
                wait(GloriousWaitMs);
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (KeyboardProfileBackups.Get(_key) == null) return true;
                var now = _xenon ? ReadXenonReport(_pair.Data) : ReadGloriousConfiguration(_pair, _fewest, out _);
                if (now == null) return false;
                if (!SendRestore(_pair, wait, _xenon, RestoreOver(now, Original, _xenon, _writeLength))) return false;
                KeyboardProfileBackups.Drop(_key);
                return true;
            }
        }
    }
}
