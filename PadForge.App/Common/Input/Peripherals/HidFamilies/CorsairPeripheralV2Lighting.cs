using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's CorsairPeripheralV2Controller family (#557): the Corsair
    /// keyboards, mice, MM700 mousemats and Slipstream receivers of the
    /// protocol ckb-next calls Bragi. The collections are OpenRGB's
    /// registrations (CorsairPeripheralV2ControllerDetect.cpp:92-138,
    /// through <see cref="OpenRgbRegistrations"/>). Every frame is a 65-byte
    /// output report, report ID 0, whose byte 1 is the write command: 08
    /// for a wired device, 09 for one behind a receiver, 80 for the K57
    /// (CorsairPeripheralV2Controller.h:24-30).
    ///
    /// <para>The open, as OpenRGB's controller makes it
    /// (CorsairPeripheralV2Controller.cpp:20-138). The device's product ID,
    /// property 12, asked up to eight times 120 ms apart until it answers
    /// one (GetAddressRetry and GetAddress, lines 264-319): a receiver
    /// answers its own, and is then asked again with write command 09 for
    /// the device behind it (lines 32-50). Then the vendor ID, property 11,
    /// whose answer OpenRGB keeps nothing of. A device whose product ID is
    /// not in OpenRGB's device list gets nothing more (lines 79-106,
    /// CorsairPeripheralV2ControllerDetect.cpp:42-53). Then the lighting
    /// resource: a transaction is opened on resource 22, and one the device
    /// refuses is opened on resource 1 instead, then closed (lines
    /// 108-137), except on the K100s, which stay on 22. Then software render
    /// mode, 01 03 00 02, and a read of property 5F
    /// (CorsairPeripheralV2HardwareController.cpp:15-19,
    /// CorsairPeripheralV2SoftwareController.cpp:15-19), and the board
    /// layout, property 41 (lines 259-262). Each write but the property
    /// reads is followed by a read of the answer, up to 50 ms, except on
    /// the K57, which answers none (skip_reads, lines 45-49).</para>
    ///
    /// <para>The lights are OpenRGB's per device
    /// (CorsairPeripheralV2Devices.cpp, through CorsairLights.g.cs): a
    /// keyboard's keys from its layout in KeyboardLayouts, built for the
    /// board the device reports, ISO for 2, JIS for 4 and ANSI for any
    /// other answer, each light's address its value in the layout, and any
    /// other zone's lights numbered after every light before them
    /// (RGBController_CorsairV2Hardware.cpp:87-214,
    /// RGBController_CorsairV2Software.cpp:74-201). A picture is one color
    /// per address up to the highest. A device of the software detector, or
    /// of the hardware one on resource 1, takes it as all reds, all greens
    /// and all blues. One of the hardware detector on resource 22 takes 12
    /// 00, then red, green and blue for each address
    /// (CorsairPeripheralV2HardwareController.cpp:26-88,
    /// CorsairPeripheralV2SoftwareController.cpp:26-47). It goes out in one
    /// transaction: whatever input is waiting is read away, the transaction
    /// opened, 06 with the length and 57 bytes, its answer read for up to 3
    /// ms or a 2 ms wait, 07 reports of 61 bytes each followed by 2 ms, and
    /// the transaction closed (SetLEDs, lines 361-457).</para>
    ///
    /// <para>The model of the device. Software render mode shows each
    /// picture as it comes and keeps none ("@save :x:" and ":robot:",
    /// RGBController_CorsairV2Software.cpp:22 and
    /// RGBController_CorsairV2Hardware.cpp:23), and it lasts until the
    /// device re-enumerates unless it is reverted. OpenRGB reverts it with
    /// hardware render mode, 01 03 00 01, when it closes a device, because
    /// a K70 Core RGB stops sending its knob's input while left in software
    /// mode (lines 140-153). That is the hand-back and the crash path. A
    /// device falls back to its own lighting after a minute without a
    /// picture, so OpenRGB sends the last one again once 30 seconds have
    /// passed without one, looking every 12.5 seconds
    /// (CORSAIR_V2_UPDATE_PERIOD and CORSAIR_V2_SLEEP_PERIOD,
    /// CorsairPeripheralV2Controller.h:34-35, and KeepaliveThread,
    /// RGBController_CorsairV2Hardware.cpp:63-70 and 238-252).</para>
    ///
    /// <para>No reference bounds how often a picture may go out, so the
    /// bound is PadForge's, as for the Lenovo Y740 to Y760. Corsair iCUE
    /// lights these devices while it runs.</para>
    /// </summary>
    internal sealed class CorsairPeripheralV2Lighting : IHidLightingFamily
    {
        public static readonly CorsairPeripheralV2Lighting Instance = new();

        /// <summary>The family's name in OpenRGB's registrations.</summary>
        internal const string OpenRgbFamily = "CorsairPeripheralV2Controller";

        internal const string DetectHardware = "DetectCorsairV2HardwareControllers";
        internal const string DetectSoftware = "DetectCorsairV2SoftwareControllers";

        internal const int RepaintMs = LenovoLegionUsbLighting.RepaintMs;

        /// <summary>CORSAIR_V2_TIMEOUT, CORSAIR_V2_TIMEOUT_SHORT and
        /// CORSAIR_V2_WRITE_PACE (CorsairPeripheralV2Controller.h:24-25 and 36).</summary>
        internal const int AnswerMs = 50;
        internal const int ShortAnswerMs = 3;
        internal const int PaceMs = 2;

        /// <summary>CORSAIR_V2_PID_RETRIES and CORSAIR_V2_PID_RETRY_DELAY
        /// (lines 45-46).</summary>
        internal const int PidRetries = 8;
        internal const int PidRetryMs = 120;

        /// <summary>CORSAIR_V2_UPDATE_PERIOD (CorsairPeripheralV2Controller.h:34): the last picture
        /// goes out again once this long passes without one, the resend
        /// included (RGBController_CorsairV2Software.cpp:205 and 225-238,
        /// RGBController_CorsairV2Hardware.cpp:218 and 238-252). OpenRGB looks
        /// every CORSAIR_V2_SLEEP_PERIOD, 12.5 s (line 35 of the header). The worker sends it
        /// at the 30 s itself.</summary>
        internal const int KeepAliveMs = 30000;

        /// <summary>The write commands (lines 27-28, and the K57's in
        /// CorsairPeripheralV2Controller.cpp:45-49).</summary>
        internal const byte WriteWired = 0x08;
        internal const byte WriteWireless = 0x09;
        internal const byte WriteK57 = 0x80;

        /// <summary>CORSAIR_V2_LIGHT_CTRL1 and CORSAIR_V2_LIGHT_CTRL2
        /// (lines 32-33).</summary>
        internal const byte LightCtrl1 = 1;
        internal const byte LightCtrl2 = 0x22;

        /// <summary>The 32-bit value OpenRGB's GetAddress returns for a
        /// protocol error, -1 (CorsairPeripheralV2Controller.cpp:311-317).</summary>
        internal const uint NoValue = uint.MaxValue;

        /// <summary>How many waiting input reports one picture reads away
        /// at most. OpenRGB reads until none comes in 3 ms
        /// (ClearPacketBuffer, lines 361-376) and a read error never ends its
        /// loop, so the bound is PadForge's.</summary>
        internal const int MaxClearReads = 64;

        /// <summary>The receivers (CorsairPeripheralV2Controller.cpp:36-43,
        /// CorsairPeripheralV2Devices.h:98, 107 and 109-111).</summary>
        internal static bool IsReceiver(uint productId)
            => productId is 0x1BA6 or 0x1B66 or 0x1B65 or 0x1BDC or 0x2B00 or LaptopReceiver;

        /// <summary>The dongle of Corsair's laptops, which only SignalRGB's plugin
        /// lists (Corsair_Bragi_Device.js, ProductIDList).</summary>
        internal const ushort LaptopReceiver = 0x0D01;

        /// <summary>BRAGI_RES_LIGHTING_MONOCHROME (ckb-next bragi_proto.h:61).</summary>
        internal const byte LightCtrlMonochrome = 0x10;

        /// <summary>CORSAIR_K57_RGB_WIRED_PID (CorsairPeripheralV2Devices.h:72).</summary>
        internal const ushort K57Wired = 0x1B6E;

        /// <summary>The K100s, whose lighting stays on resource 22 (lines
        /// 116-124).</summary>
        internal static bool KeepsResource22(uint productId) => productId is 0x1B7C or 0x1BC5 or 0x1B7D;

        private readonly Dictionary<(string Detector, int ProductId), HidLightingModel> _models = new();
        private readonly object _gate = new();

        public string Name => "Corsair (V2 peripherals)";

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != CorsairLights.VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            var registration = registrations.Count > 0 ? registrations[0] : PluginRegistration(collection);
            if (registration == null) return null;
            lock (_gate)
            {
                var key = (registration.Detector, registration.ProductId);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration);
                return model;
            }
        }

        private HidLightingModel Model(OpenRgbRegistration registration)
        {
            ushort productId = (ushort)registration.ProductId;
            string product = CorsairLights.Product(productId);
            bool hardware = registration.Detector == DetectHardware;
            var device = DeviceOf(productId);
            // A receiver's device is known once a session asks it.
            var map = device != null
                ? MapOf(device, KeyboardLayout.AnsiQwerty, out _)
                : new LightMap(Array.Empty<LightInfo>(), "OpenRGB CorsairPeripheralV2Devices.cpp, device not asked yet") { Product = product };
            return new HidLightingModel
            {
                Family = this,
                Name = registration.Name,
                Map = map,
                Programs = new[] { VendorPrograms.CorsairIcue },
                RepaintMs = RepaintMs,
                KeepAliveMs = KeepAliveMs,
                KeepAliveIdleMs = KeepAliveMs,
                Begin = (io, wait) => Begin(io, wait, hardware, product),
                Owed = () => CorsairSession.Owed(product),
                Repair = (io, wait) => CorsairSession.Repair(io, wait, product, 0),
            };
        }

        /// <summary>The device of OpenRGB's list with this product ID, or
        /// null (lines 79-92).</summary>
        internal static CorsairV2Device DeviceOf(uint productId)
        {
            foreach (var device in CorsairLights.V2Devices)
                if (device.ProductId == productId) return device;
            foreach (var device in PluginDevices.Value)
                if (device.ProductId == productId) return device;
            return null;
        }

        /// <summary>
        /// Corsair's devices on the same protocol that OpenRGB has no table for,
        /// and SignalRGB's plugins do (Corsair_Bragi_Device.js, Corsair_Headset_Controller_Bragi.js): the
        /// frames are this family's, opening the lighting endpoint and writing
        /// to it, and a mouse takes its colors as blocks of reds, greens and
        /// blues, as the plugin's ledSpacing says and this family's mice do. A
        /// keyboard takes triplets at the plugin's LED numbers, which on the
        /// K70 Pro Mini run four below this family's (its Esc is 37 where
        /// OpenRGB's is 41). The K70 Pro's third ID takes OpenRGB's K70 Pro, as
        /// the plugin lists them together.
        /// </summary>
        internal static readonly Lazy<CorsairV2Device[]> PluginDevices = new(() => PluginCorsairTables.Devices
            .Where(d => !IsReceiver(d.Product) && CorsairLights.V2Devices.All(v => v.ProductId != d.Product))
            .Select(PluginDevice)
            .Append(CorsairLights.K60RgbProDevice with { Name = "Corsair K60 PRO", ProductId = 0x1B83, Monochrome = true })
            .ToArray());

        /// <summary>The K70 Pro's ID OpenRGB does not list takes its table, as the
        /// plugin lists them together.</summary>
        private static CorsairV2Device PluginDevice(PluginCorsairDevice row)
        {
            if (row.Product == 0x1BD4) return CorsairLights.K70RgbProDevice with { Name = "Corsair K70 RGB PRO", ProductId = 0x1BD4 };
            var table = row.Table();
            var lights = table.Usages != null
                ? PluginKeyNames.Lights(table)
                : table.Names.Select((name, i) => PluginKeyNames.Named(table.Indexes[i], name)).ToArray();
            var type = row.Kind == PluginCorsairKind.Keyboard ? CorsairDeviceType.Keyboard : CorsairDeviceType.Mouse;
            return new CorsairV2Device(row.Name, row.Product, type, 1, table.Names.Length, Array.Empty<CorsairV2Zone>(), null)
            {
                OwnLights = lights,
                OwnBuffer = row.Spacing,
                OwnInterface = row.Interface,
            };
        }

        /// <summary>The registration OpenRGB would make for a device of
        /// <see cref="PluginDevices"/>: the interface and usage page of its
        /// others, the hardware detector's.</summary>
        internal static OpenRgbRegistration PluginRegistration(HidCollectionInfo collection)
        {
            var device = PluginDevices.Value.FirstOrDefault(d => d.ProductId == collection.ProductId);
            if (collection.UsagePage != 0xFF42) return null;
            string name = device?.Name ?? (collection.ProductId == LaptopReceiver ? "Corsair Laptop Slipstream Dongle" : null);
            if (name == null) return null;
            int number = device?.OwnInterface ?? 1;
            if (OpenRgbRegistrations.InterfaceOf(collection, OpenRgbRegistrations.BusOf(collection)) != number) return null;
            return new OpenRgbRegistration(OpenRgbFamily, name, DetectHardware, "SignalRGB", OpenRgbTransport.Hid,
                CorsairLights.VendorId, collection.ProductId, number, 0xFF42, OpenRgbRegistration.Any, OpenRgbRegistration.Any,
                true, false, "signal-plugins Corsair_Bragi_Device.js");
        }

        /// <summary>A device of the plugin's list, whose IDs the plugin names for a
        /// dongle in some places and the device on its cable in others (its library's
        /// "Slip Stream Dongle" for the Dark Core Pro against the standalone plugin's
        /// "Wired"). The session asks through the dongle's command first and takes
        /// the answer.</summary>
        internal static bool MayBeReceiver(uint productId) => PluginDevices.Value.Any(d => d.ProductId == productId);

        /// <summary>The board layout an answer to property 41 names
        /// (RGBController_CorsairV2Hardware.cpp:95-110).</summary>
        internal static KeyboardLayout LayoutOf(uint answer) => answer switch
        {
            2 => KeyboardLayout.IsoQwerty,
            4 => KeyboardLayout.Jis,
            _ => KeyboardLayout.AnsiQwerty,
        };

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>
        /// The lights of a device in a board layout, in OpenRGB's order, and
        /// the length of its color buffer (SetupZones,
        /// RGBController_CorsairV2Hardware.cpp:112-213). A matrix zone's
        /// lights are its layout's keys at their values, and the buffer
        /// reaches one past the highest value seen so far. Any other zone's
        /// lights each take the count of lights before them, and the buffer
        /// reaches that count. A mousepad's lights that are not its logo
        /// are edge lights.
        /// </summary>
        internal static List<LightInfo> Lights(CorsairV2Device device, KeyboardLayout layout, out int bufferLength)
        {
            if (device.OwnLights != null)
            {
                bufferLength = Math.Max(device.OwnLights.Max(l => l.Address) + 1, device.OwnBuffer);
                return device.OwnLights.ToList();
            }
            var lights = new List<LightInfo>();
            uint max = 0;
            foreach (var zone in device.Zones)
            {
                if (zone.Type == CorsairZoneType.Matrix)
                {
                    var recipe = KeyboardLayouts.Recipe(device.Layout);
                    if (recipe != null && recipe.Size != KeyboardSize.Empty)
                    {
                        var keys = recipe.Build(layout).Keys;
                        foreach (var key in keys)
                        {
                            var light = CorsairLights.Light((int)key.Value, key.Name, LightKind.Other);
                            lights.Add(light with { X = key.Col, Y = key.Row, Width = 1, Height = 1, HasPosition = true });
                            max = Math.Max(max, key.Value);
                        }
                    }
                    max++;
                    continue;
                }
                var fallback = device.Type == CorsairDeviceType.Mousemat ? LightKind.Edge : LightKind.Zone;
                int count = zone.Rows * zone.Cols;
                for (int i = 0; i < count; i++)
                    lights.Add(CorsairLights.Light(lights.Count, zone.Name + " " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), fallback));
                max = Math.Max(max, (uint)lights.Count);
            }
            bufferLength = (int)max;
            return CorsairLights.NumberZones(lights);
        }

        internal static LightMap MapOf(CorsairV2Device device, KeyboardLayout layout, out int bufferLength)
            => new(Lights(device, layout, out bufferLength), $"OpenRGB CorsairPeripheralV2Devices.cpp {device.Name}, {layout}")
            {
                Product = CorsairLights.Product(device.ProductId),
            };

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>A picture's data, one color per buffer address
        /// (SetLedsDirect). Blocks of all reds, greens and blues, or 12 00
        /// and a triplet per address.</summary>
        internal static byte[] PictureData(bool triplets, int bufferLength, IReadOnlyList<LightInfo> lights,
            IReadOnlyList<int> colors)
        {
            var at = new int[bufferLength];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int address = lights[n].Address;
                if ((uint)address < (uint)bufferLength) at[address] = colors[n];
            }
            if (triplets)
            {
                var data = new byte[2 + bufferLength * 3];
                data[0] = 0x12;
                data[1] = 0x00;
                for (int i = 0; i < bufferLength; i++)
                {
                    data[2 + i * 3] = (byte)(at[i] >> 16);
                    data[3 + i * 3] = (byte)(at[i] >> 8);
                    data[4 + i * 3] = (byte)at[i];
                }
                return data;
            }
            var blocks = new byte[bufferLength * 3];
            for (int i = 0; i < bufferLength; i++)
            {
                blocks[i] = (byte)(at[i] >> 16);
                blocks[bufferLength + i] = (byte)(at[i] >> 8);
                blocks[2 * bufferLength + i] = (byte)at[i];
            }
            return blocks;
        }

        /// <summary>The block-write reports of <paramref name="data"/>
        /// (SetLEDs, lines 391-454): 06, 00, the length low and high, two
        /// zeros, then 57 bytes, and 07, 00, then 61 bytes per report, the
        /// last one's tail zero.</summary>
        internal static List<byte[]> DataFrames(byte writeCmd, byte[] data)
        {
            var frames = new List<byte[]>();
            var first = CorsairLights.Frame(writeCmd, 0x06, 0x00, (byte)data.Length, (byte)(data.Length >> 8));
            int copy = Math.Min(data.Length, CorsairLights.PacketLength - 8);
            Array.Copy(data, 0, first, 8, copy);
            frames.Add(first);
            for (int index = copy; index < data.Length; index += CorsairLights.PacketLength - 4)
            {
                var next = CorsairLights.Frame(writeCmd, 0x07, 0x00);
                Array.Copy(data, index, next, 4, Math.Min(data.Length - index, CorsairLights.PacketLength - 4));
                frames.Add(next);
            }
            return frames;
        }

        // ─────────────────────────────────────────────
        //  The exchanges
        // ─────────────────────────────────────────────

        /// <summary>One device's writes and reads, with the write command
        /// and the read rule the open settled.</summary>
        internal sealed class Link
        {
            private readonly IHidReportChannel _io;
            private readonly Action<int> _wait;

            public Link(IHidReportChannel io, Action<int> wait)
            {
                _io = io;
                _wait = wait;
            }

            public byte WriteCmd = WriteWired;
            public byte LightCtrl = LightCtrl2;
            public bool SkipReads;

            public bool Write(byte[] frame) => _io.WriteOutput(frame);

            /// <summary>One answer, into a buffer whose byte 0 is the report
            /// ID: hidapi's byte n is byte n + 1 here. The count read, 0 for
            /// none in time, -1 for a failed read.</summary>
            public int Read(byte[] buffer, int ms) => _io.ReadInput(buffer, ms);

            public byte[] Buffer() => new byte[Math.Max(CorsairLights.PacketLength, _io.InputReportLength)];

            /// <summary>A write followed by a read of its answer, unless the
            /// device answers none (SetRenderMode, LightingControl,
            /// StopTransaction).</summary>
            public bool Exchange(byte[] frame)
            {
                if (!Write(frame)) return false;
                if (!SkipReads) Read(Buffer(), AnswerMs);
                return true;
            }

            /// <summary>GetAddress (lines 292-319): the property's 32-bit value,
            /// little-endian from hidapi's byte 3, <see cref="NoValue"/> when
            /// byte 2 names an error, 0 when nothing answered. Null when the
            /// write failed.</summary>
            public uint? Get(byte property)
            {
                if (!Write(CorsairLights.Frame(WriteCmd, 0x02, property))) return null;
                var answer = Buffer();
                if (Read(answer, AnswerMs) < 0) Array.Clear(answer);
                if (answer[3] > 0) return NoValue;
                return (uint)(answer[4] | answer[5] << 8 | answer[6] << 16 | answer[7] << 24);
            }

            /// <summary>GetAddressRetry (lines 270-290).</summary>
            public uint? GetRetry(byte property)
            {
                uint value = NoValue;
                for (int attempt = 0; attempt < PidRetries; attempt++)
                {
                    if (Get(property) is not uint got) return null;
                    value = got;
                    if (value != NoValue && value != 0) return value;
                    if (attempt + 1 < PidRetries) _wait(PidRetryMs);
                }
                return value;
            }

            /// <summary>StartTransaction (lines 321-340): the answer's error
            /// byte, or the command byte 0D still in the buffer when no
            /// answer came or none is read. -1 when the write failed.</summary>
            public int Start(byte option)
            {
                var frame = CorsairLights.Frame(WriteCmd, 0x0D, option, LightCtrl);
                if (!Write(frame)) return -1;
                if (SkipReads) return 0x0D;
                var answer = Buffer();
                return Read(answer, AnswerMs) > 0 ? answer[3] : 0x0D;
            }

            /// <summary>StopTransaction (lines 342-359).</summary>
            public bool Stop(byte option) => Exchange(CorsairLights.Frame(WriteCmd, 0x05, 0x01, option));

            /// <summary>SetRenderMode (lines 213-233): 01 hardware, 02
            /// software.</summary>
            public byte[] RenderMode(byte mode) => CorsairLights.Frame(WriteCmd, 0x01, 0x03, 0x00, mode);

            /// <summary>SetLEDs (lines 378-457).</summary>
            public bool SendData(byte[] data)
            {
                if (!SkipReads)
                {
                    var scratch = Buffer();
                    for (int i = 0; i < MaxClearReads && Read(scratch, ShortAnswerMs) > 0; i++) { }
                }
                if (Start(0) < 0) return false;
                var frames = DataFrames(WriteCmd, data);
                for (int i = 0; i < frames.Count; i++)
                {
                    if (!Write(frames[i])) return false;
                    if (i == 0)
                    {
                        if (!SkipReads && Read(Buffer(), ShortAnswerMs) <= 0) _wait(PaceMs);
                    }
                    else
                    {
                        _wait(PaceMs);
                    }
                }
                return Stop(0);
            }
        }

        private HidLightingSession Begin(IHidReportChannel io, Action<int> wait, bool hardware, string product)
        {
            var link = new Link(io, wait);
            if (link.GetRetry(0x12) is not uint pid) return null;
            if (IsReceiver(pid))
            {
                link.WriteCmd = WriteWireless;
                if (link.GetRetry(0x12) is not uint behind) return null;
                pid = behind;
            }
            else if (MayBeReceiver(pid))
            {
                link.WriteCmd = WriteWireless;
                if (link.GetRetry(0x12) is uint behind && DeviceOf(behind) != null) pid = behind;
                else link.WriteCmd = WriteWired;
            }
            else if (pid == K57Wired)
            {
                link.WriteCmd = WriteK57;
                link.LightCtrl = LightCtrl1;
                link.SkipReads = true;
            }
            // The vendor ID, read and dropped (lines 60-71).
            if (!link.Write(CorsairLights.Frame(link.WriteCmd, 0x02, 0x11))) return null;
            link.Read(link.Buffer(), AnswerMs);
            var device = DeviceOf(pid);
            if (device == null) return null;
            if (device.Monochrome) link.LightCtrl = LightCtrlMonochrome;
            else if (link.LightCtrl == LightCtrl2 && !KeepsResource22(pid))
            {
                int result = link.Start(0);
                if (result < 0) return null;
                if (result > 0)
                {
                    link.LightCtrl = LightCtrl1;
                    if (link.Start(0) < 0) return null;
                }
                if (!link.Stop(0)) return null;
            }
            if (!link.Exchange(link.RenderMode(0x02))) return null;
            if (!link.Exchange(CorsairLights.Frame(link.WriteCmd, 0x02, 0x5F))) return null;
            if (link.Get(0x41) is not uint layout) return null;
            var map = MapOf(device, LayoutOf(layout), out int bufferLength);
            bool triplets = hardware && link.LightCtrl == LightCtrl2;
            return new Session(product, link, device, map, bufferLength, triplets);
        }

        internal sealed class Session : CorsairSession
        {
            private readonly Link _link;

            public Session(string product, Link link, CorsairV2Device device, LightMap map, int bufferLength,
                bool triplets)
                : base(product, link.RenderMode(0x01), 1, 0)
            {
                _link = link;
                Device = device;
                Map = map;
                BufferLength = bufferLength;
                Triplets = triplets;
            }

            public CorsairV2Device Device { get; }
            public int BufferLength { get; }
            public bool Triplets { get; }
            public byte WriteCmd => _link.WriteCmd;
            public byte LightCtrl => _link.LightCtrl;
            public bool SkipReads => _link.SkipReads;

            /// <summary>The lights of the device the session asked, in the
            /// layout it reports (<see cref="HidLightingSession.Map"/>).</summary>
            public override LightMap Map { get; }

            protected override bool Send(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
                => _link.SendData(Device.Monochrome
                    ? PictureData(false, BufferLength, lights, colors).Take(BufferLength).ToArray()
                    : PictureData(Triplets, BufferLength, lights, colors));

            /// <summary>Hardware render mode and its answer (the
            /// destructor's SetRenderMode, lines 140-153).</summary>
            protected override bool SendHandBack(IHidReportChannel io, Action<int> wait)
                => _link.Exchange(_link.RenderMode(0x01));
        }
    }
}
