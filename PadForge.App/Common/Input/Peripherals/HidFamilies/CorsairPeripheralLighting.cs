using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// OpenRGB's CorsairPeripheralController family (#557): the Corsair
    /// keyboards, mice, the MM800 mousemat and the ST100 headset stand of
    /// Corsair's older protocol, and the K65 Mini and K55 RGB PRO XT, which
    /// OpenRGB drives with controllers of their own in the same family. The
    /// collections are OpenRGB's registrations
    /// (CorsairPeripheralControllerDetect.cpp:182-239, through
    /// <see cref="OpenRgbRegistrations"/>). Every frame is a 65-byte output
    /// report, report ID 0, except the K65 Mini's 1025-byte one
    /// (CorsairPeripheralController.h:18, CorsairK65MiniController.h:16).
    ///
    /// <para>The older protocol. A session asks the device what it is, as
    /// OpenRGB's ReadFirmwareInfo does: 0E 01, read for a second, and when
    /// nothing comes, the same as a feature report and its answer read
    /// back, up to ten times until the answer's type byte says keyboard
    /// (C0), mouse (C1) or mousepad or stand (C2)
    /// (CorsairPeripheralController.cpp:522-602). The answer's byte 0x17 is
    /// the board's layout (lines 659-678, ckb-next firmware.c:82). A device
    /// that never names its type gets nothing more, as OpenRGB drops it
    /// (CorsairPeripheralControllerDetect.cpp:159-173). OpenRGB tells the
    /// keyboard tables apart by the product ID it reads from that answer
    /// (lines 616-653). ckb-next reads the same ID little-endian
    /// (firmware.c:64) where OpenRGB reads the keyboard's high byte first,
    /// so the session takes the collection's product ID, the one both
    /// compare against.</para>
    ///
    /// <para>Then OpenRGB's open: the special-function and lighting-control
    /// writes, 07 04 02 and 07 05 02 00 with 03 for a keyboard or a stand,
    /// 01 for a mouse and 04 for a mousepad, and for the K55, K95 Platinum,
    /// K70 MK.2 and K68 the key-input setup, 07 05 08 00 01 and four 07 40
    /// 1E reports of key numbers each set to C0 (lines 69-91, 370-520,
    /// 684-704). A keyboard picture is three banks of 168 bytes, red, green
    /// and blue, each light at its Corsair number, streamed 60, 60 and 24
    /// bytes at a time (48 for the K95s) with 7F reports and each bank
    /// committed with 07 28 (SetLEDsKeyboardFull and
    /// SubmitKeyboardFullColors, lines 186-257 and 855-883). The K55 takes
    /// one 07 25 report of its three zones (lines 885-921), a mouse one 07
    /// 22 report of its fifteen zones, each with its number, and a mousepad
    /// and the stand the same without the numbers (lines 949-1025). The
    /// stand's logo is OpenRGB's ninth light and sits fifth in the report
    /// (st100[], lines 61 and 164-182). A light's address is where its
    /// color goes: its Corsair number on a keyboard, its place in the report
    /// on the others.</para>
    ///
    /// <para>The model of the device. The software modes these writes set
    /// are stored nowhere ("@save :x:", RGBController_CorsairPeripheral.cpp:915)
    /// and OpenRGB never hands the device back when it closes it (lines
    /// 1082-1087). ckb-next does: 07 04 01, the hardware mode that brings
    /// back the device's own lighting, twice 30 ms apart for a keyboard
    /// ("It has to be sent twice for some reason", ckb-next
    /// src/daemon/device_keyboard.c:61-69, DELAY_30MS in usb.h:309-310) and
    /// once for a mouse, a mousepad and the ST100 (device_mouse.c:48-77,
    /// device_vtable.c:338-345, usb.c:308-317), the same report OpenRGB's
    /// SwitchMode(false) sends (CorsairPeripheralController.cpp:352-363).
    /// That is the hand-back and the crash path. OpenRGB's key-input setup
    /// is left as it set it: ckb-next undoes its own on Linux alone
    /// (device_keyboard.c:70-103).</para>
    ///
    /// <para>The K65 Mini and K55 RGB PRO XT speak Corsair's newer protocol:
    /// three writes that put the board in software mode and open its
    /// lighting (CorsairK65MiniController.cpp:55-90,
    /// CorsairK55RGBPROXTController.cpp:78-100), and one frame of every key
    /// for a picture (lines 92-118 and 102-133). Each falls back to its own
    /// lighting after a minute without a picture, so OpenRGB sends the last
    /// one again after 50 seconds without one, looking every 3 seconds
    /// (RGBController_CorsairK65Mini.cpp:157-164 and 238-249,
    /// RGBController_CorsairK55RGBPROXT.cpp:344-351 and 424-437). Neither
    /// controller hands the board back. The hand-back is the newer
    /// protocol's hardware render mode, 08 01 03 00 01, as OpenRGB's V2
    /// controller sends it when it closes a device
    /// (CorsairPeripheralV2Controller.cpp:140-153 and 213-233) and ckb-next
    /// when it goes idle (device_bragi.c:38-50, bragi_common.c:23-36). The
    /// K55 RGB PRO XT's own SwitchMode(false) writes 00 where the protocol
    /// has 08 (CorsairK55RGBPROXTController.cpp:276-285), so it is not
    /// copied.</para>
    ///
    /// <para>No reference bounds how often a picture may go out, so the
    /// bound is PadForge's, as for the Lenovo Y740 to Y760
    /// (<see cref="LenovoLegionUsbLighting.RepaintMs"/>). Corsair iCUE
    /// lights these devices while it runs.</para>
    /// </summary>
    internal sealed class CorsairPeripheralLighting : IHidLightingFamily
    {
        public static readonly CorsairPeripheralLighting Instance = new();

        /// <summary>The family's name in OpenRGB's registrations.</summary>
        internal const string OpenRgbFamily = "CorsairPeripheralController";

        internal const string DetectPeripheral = "DetectCorsairPeripheralControllers";
        internal const string DetectK65Mini = "DetectCorsairK65MiniControllers";
        internal const string DetectK55ProXt = "DetectCorsairK55RGBPROXTControllers";

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's.</summary>
        internal const int RepaintMs = LenovoLegionUsbLighting.RepaintMs;

        /// <summary>CORSAIR_FW_INFO_MAX_ATTEMPTS and the read's timeout
        /// (CorsairPeripheralController.h:19, CorsairPeripheralController.cpp:567).</summary>
        internal const int IdentifyAttempts = 10;
        internal const int IdentifyReadMs = 1000;

        /// <summary>The gap between the two hand-back reports of a keyboard
        /// (ckb-next DELAY_30MS).</summary>
        internal const int KeyboardHandBackGapMs = 30;

        /// <summary>The newer protocol's boards: the wait after each open
        /// write of the K65 Mini (CorsairK65MiniController.cpp:70-89), and
        /// the keep-alive OpenRGB keeps for both: the last picture again once
        /// 50 s pass without one, the resend included
        /// (RGBController_CorsairK65Mini.cpp:219 and 238-250,
        /// RGBController_CorsairK55RGBPROXT.cpp:394 and 424-437).
        /// OpenRGB looks every 3 s. The worker sends it at the 50 s
        /// itself.</summary>
        internal const int K65MiniOpenGapMs = 10;
        internal const int BoardKeepAliveMs = 50000;

        /// <summary>The K65 Mini's report length, PACKET_LENGTH
        /// (CorsairK65MiniController.h:16).</summary>
        internal const int K65MiniPacketLength = 1025;

        /// <summary>COLOR_BANK_SIZE (CorsairK55RGBPROXTController.cpp:14).</summary>
        internal const int K55ProXtBankSize = 137;

        /// <summary>The size of each color bank of a full keyboard
        /// (CorsairPeripheralController.cpp:188-190).</summary>
        internal const int KeyboardBankSize = 168;

        /// <summary>CORSAIR_TYPE_* (CorsairPeripheralController.h:63-72), the
        /// keyboard tables OpenRGB tells apart.</summary>
        internal enum Logical
        {
            Normal = 0,
            K95Platinum = 1,
            K95 = 2,
            K55 = 3,
            K70Mk2 = 4,
            K68 = 5,
            K70LuxRgb = 6,
        }

        /// <summary>CORSAIR_LAYOUT_* (CorsairPeripheralController.h:54-61).</summary>
        internal enum Physical
        {
            Ansi = 0,
            Iso = 1,
            Abnt = 2,
            Jis = 3,
            Dubeolsik = 4,
        }

        private readonly Dictionary<(string Detector, int ProductId, bool OneColor), HidLightingModel> _models = new();
        private readonly object _gate = new();

        public string Name => "Corsair (peripherals)";

        /// <summary>Other product IDs of devices OpenRGB lists, which SignalRGB's
        /// plugins list with them and drive with the same frames: the K65 Mini's
        /// (Corsair_K65_Mini_Keyboard.js:3), the Strafe's
        /// (Corsair_Strafe_Keyboard.js:3), the Sabre RGB's
        /// (Corsair_Bragi_Device.js:936-937), and the K70 MKII Rapidfire Red's
        /// (Corsair_Legacy_Keyboard.js:225). ckb-next (src/daemon/usb.h, led_keyboard.c and
        /// led_mouse.c) drives three more with the same frames: the K63's non-RGB model,
        /// red-only as OpenRGB's K68 RED is, its Sabre with the laser sensor, and the old
        /// Katar, whose one light is a zone of the same mouse frame.</summary>
        internal static readonly Dictionary<ushort, ushort> OtherIds = new()
        {
            [0x1B40] = 0x1B3F,
            [0x1B19] = 0x1B2F,
            [0x1B22] = 0x1B2F,
            [0x1BCF] = 0x1BAF,
            [0x1BC3] = 0x1BAF,
            [0x1BBD] = 0x1BAF,
            [0x1B15] = 0x1B20,
            [0x1B14] = 0x1B2F,
            [0x1B3A] = 0x1B49,
        };

        /// <summary>The boards whose LEDs are all red: ckb-next's non-RGB products (usb.h:61, 80,
        /// 86, 88, 111 and 112) and OpenRGB's K68 RED SHADOW. A picture goes to them as one
        /// brightness (<see cref="CorsairLights.Mono"/>).</summary>
        internal static readonly HashSet<ushort> OneColor = new() { 0x1B15, 0x1B36, 0x1B3A, 0x1B3F, 0x1B40, 0x1B44, 0x1BA5 };

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != CorsairLights.VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily,
                OtherIds.TryGetValue(collection.ProductId, out ushort sameAs) ? collection with { ProductId = sameAs } : collection);
            if (registrations.Count == 0) return null;
            var registration = registrations[0];
            lock (_gate)
            {
                bool oneColor = OneColor.Contains(collection.ProductId);
                var key = (registration.Detector, registration.ProductId, oneColor);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration, oneColor);
                return model;
            }
        }

        private HidLightingModel Model(OpenRgbRegistration registration, bool oneColor)
        {
            ushort productId = (ushort)registration.ProductId;
            string product = CorsairLights.Product(productId);
            string name = registration.Name;
            HidLightingModel model = registration.Detector switch
            {
                DetectK65Mini => new HidLightingModel
                {
                    Map = new LightMap(K65MiniLights(), "OpenRGB RGBController_CorsairK65Mini.cpp") { Product = product },
                    KeepAliveMs = BoardKeepAliveMs,
                    KeepAliveIdleMs = BoardKeepAliveMs,
                    Begin = (io, wait) => BeginK65Mini(io, wait, product),
                },
                DetectK55ProXt => new HidLightingModel
                {
                    Map = new LightMap(K55ProXtLights(), "OpenRGB RGBController_CorsairK55RGBPROXT.cpp") { Product = product },
                    KeepAliveMs = BoardKeepAliveMs,
                    KeepAliveIdleMs = BoardKeepAliveMs,
                    Begin = (io, wait) => BeginK55ProXt(io, wait, product),
                },
                _ => new HidLightingModel
                {
                    Map = new LightMap(Lights(TypeOf(productId), LogicalOf(productId), name), "OpenRGB RGBController_CorsairPeripheral.cpp")
                    { Product = product },
                    Begin = (io, wait) => BeginPeripheral(io, wait, productId, name, product, oneColor),
                },
            };
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = model.Map,
                Programs = new[] { VendorPrograms.CorsairIcue },
                RepaintMs = RepaintMs,
                KeepAliveMs = model.KeepAliveMs,
                KeepAliveIdleMs = model.KeepAliveIdleMs,
                Begin = model.Begin,
                Owed = () => CorsairSession.Owed(product),
                Repair = (io, wait) => CorsairSession.Repair(io, wait, product, KeyboardHandBackGapMs),
            };
        }

        // ─────────────────────────────────────────────
        //  What a device is
        // ─────────────────────────────────────────────

        /// <summary>The device type of a product, by the section of
        /// CorsairPeripheralControllerDetect.cpp its registration sits in
        /// (Keyboards, Mice, Mousemats, Headset Stands, lines 179-229). A
        /// session takes the type the device answers.</summary>
        internal static CorsairDeviceType TypeOf(ushort productId)
        {
            foreach (var section in CorsairLights.V1Sections)
            {
                if (section.ProductId != productId) continue;
                return section.Section switch
                {
                    "Mice" => CorsairDeviceType.Mouse,
                    "Mousemats" => CorsairDeviceType.Mousemat,
                    "Headset Stands" => CorsairDeviceType.HeadsetStand,
                    _ => CorsairDeviceType.Keyboard,
                };
            }
            return CorsairDeviceType.Keyboard;
        }

        /// <summary>The keyboard table of a product
        /// (CorsairPeripheralController.cpp:621-653).</summary>
        internal static Logical LogicalOf(ushort productId) => productId switch
        {
            0x1B2D => Logical.K95Platinum,
            0x1B11 => Logical.K95,
            0x1B3D => Logical.K55,
            0x1B33 => Logical.K70LuxRgb,
            0x1B38 or 0x1B49 or 0x1B6B or 0x1B55 or 0x1B48 => Logical.K70Mk2,
            0x1B4F => Logical.K68,
            _ => Logical.Normal,
        };

        /// <summary>The device type an identification answer names, by its
        /// type byte, or null (CorsairPeripheralController.cpp:596-711). A
        /// C2 device is the ST100 by its product ID, else a mousepad.</summary>
        internal static CorsairDeviceType? TypeOfAnswer(byte type, ushort productId) => type switch
        {
            0xC0 => CorsairDeviceType.Keyboard,
            0xC1 => CorsairDeviceType.Mouse,
            0xC2 => productId == 0x0A34 ? CorsairDeviceType.HeadsetStand : CorsairDeviceType.Mousemat,
            _ => null,
        };

        /// <summary>The board layout byte of an answer, ANSI for any value
        /// OpenRGB does not name (lines 659-678).</summary>
        internal static Physical PhysicalOf(byte value) => value <= 4 ? (Physical)value : Physical.Ansi;

        /// <summary>Byte 0x14 and 0x17 of the answer's data, after the report
        /// ID PadForge's reads keep at byte 0. hidapi drops that byte from
        /// an input report of a collection without IDs, and OpenRGB adds 1 to
        /// its offsets for the feature report, which keeps it (lines 557-596).</summary>
        internal const int AnswerTypeAt = 0x15;
        internal const int AnswerLayoutAt = 0x18;

        // ─────────────────────────────────────────────
        //  The lights
        // ─────────────────────────────────────────────

        /// <summary>The Corsair numbers ckb-next gives the two ISO keys whose
        /// OpenRGB names do not name them: 0x10 is "bslash_iso", the key
        /// beside Left Shift (KEY_102ND, usage 0x64), and 0x72 is "hash",
        /// the key beside Enter (usage 0x32) (ckb-next
        /// src/daemon/keymap.c, keymap[]). OpenRGB calls them "Key: / (ISO)"
        /// and "Key: \ (ISO)" (RGBController_CorsairPeripheral.cpp:292-293)
        /// and places them there in its grids (matrix_map, lines 18-24).</summary>
        internal static readonly IReadOnlyDictionary<int, ushort> IsoKeys = new Dictionary<int, ushort>
        {
            [0x10] = 0x64,
            [0x72] = 0x32,
        };

        /// <summary>The lights OpenRGB gives a device of this protocol, in
        /// its order (SetupZones, RGBController_CorsairPeripheral.cpp:1089-1317).</summary>
        internal static List<LightInfo> Lights(CorsairDeviceType type, Logical logical, string name)
        {
            var lights = new List<LightInfo>();
            switch (type)
            {
                case CorsairDeviceType.Keyboard:
                    if (logical == Logical.K55)
                    {
                        for (int i = 0; i < CorsairLights.LedNamesK55.Length; i++)
                            lights.Add(new LightInfo(i, LightKind.Zone, 0, LightKeySource.None));
                        break;
                    }
                    var (names, numbers, matrix) = Tables(logical);
                    for (int i = 0; i < names.Length; i++)
                    {
                        int address = numbers[i];
                        var light = IsoKeys.TryGetValue(address, out ushort iso)
                            ? new LightInfo(address, LightKind.Key, iso, LightKeySource.Generated)
                            : CorsairLights.Light(address, names[i], LightKind.Other);
                        lights.Add(CorsairLights.Placed(light, matrix, i));
                    }
                    break;
                case CorsairDeviceType.Mouse:
                {
                    // The names OpenRGB's code ends up with: the second
                    // test of the chain replaces what the first set for the
                    // M65 RGB Elite and the Harpoon RGB PRO (lines 1283-1304).
                    string[] mouse = name == "Corsair Ironclaw RGB" ? CorsairLights.CorsairM65EliteLeds
                        : name == "Corsair Sabre RGB" ? CorsairLights.CorsairSabreRgbLeds
                        : CorsairLights.CorsairMouseLeds;
                    for (int i = 0; i < MouseZones; i++)
                        lights.Add(CorsairLights.Light(i, mouse[i], LightKind.Zone));
                    break;
                }
                case CorsairDeviceType.Mousemat:
                    for (int i = 0; i < MouseZones; i++)
                        lights.Add(new LightInfo(i, LightKind.Edge, 0, LightKeySource.None));
                    break;
                case CorsairDeviceType.HeadsetStand:
                    // The base strip's eight lights, then the logo (lines
                    // 1225-1241), each at its place in the report.
                    for (int i = 0; i < CorsairLights.St100.Length; i++)
                        lights.Add(new LightInfo(CorsairLights.St100[i], i < StandStrip ? LightKind.Edge : LightKind.Logo, 0,
                            LightKeySource.None));
                    break;
            }
            return CorsairLights.NumberZones(lights);
        }

        /// <summary>The zones of a mouse and a mousepad (lines 1209-1223).</summary>
        internal const int MouseZones = 15;

        /// <summary>The ST100's base strip (lines 1226-1233).</summary>
        internal const int StandStrip = 8;

        /// <summary>A keyboard table's names, Corsair numbers and grid
        /// (lines 1100-1205 and 1257-1280, CorsairPeripheralController.cpp:203-231).</summary>
        internal static (string[] Names, int[] Numbers, CorsairMatrix Matrix) Tables(Logical logical) => logical switch
        {
            Logical.K95Platinum => (CorsairLights.LedNamesK95Plat, CorsairLights.KeysK95Plat, CorsairLights.MatrixMapK95Platinum),
            Logical.K95 => (CorsairLights.LedNamesK95, CorsairLights.KeysK95, CorsairLights.MatrixMapK95),
            Logical.K70Mk2 => (CorsairLights.LedNamesK70Mk2, CorsairLights.KeysK70Mk2, CorsairLights.MatrixMapK70Mk2),
            Logical.K70LuxRgb => (CorsairLights.LedNamesK70LuxRgb, CorsairLights.KeysK70Mk2, CorsairLights.MatrixMapK70LuxRgb),
            _ => (CorsairLights.LedNames, CorsairLights.Keys, CorsairLights.MatrixMap),
        };

        /// <summary>The K65 Mini's lights in OpenRGB's order, column by
        /// column, each at its position in the frame
        /// (RGBController_CorsairK65Mini.cpp:181-215). The two lights OpenRGB
        /// leaves unnamed are the space bar's left and right lights, by
        /// their notes and their cells either side of Space (lines 61, 87
        /// and 20-27).</summary>
        internal static List<LightInfo> K65MiniLights()
        {
            var matrix = CorsairLights.K65MiniMatrixMap;
            var lights = new List<LightInfo>();
            for (int col = 0; col < matrix.Cols; col++)
            {
                for (int row = 0; row < matrix.Rows; row++)
                {
                    int key = matrix.Cells[row * matrix.Cols + col];
                    if (key < 0) continue;
                    var (position, name) = CorsairLights.K65MiniKeys[key];
                    var light = name.Length == 0
                        ? new LightInfo(position, LightKind.Key, 0x2C, LightKeySource.Generated)
                        : CorsairLights.Light(position, name, LightKind.Other);
                    lights.Add(light with { X = col, Y = row, Width = 1, Height = 1, HasPosition = true });
                }
            }
            return lights;
        }

        /// <summary>The K55 RGB PRO XT's lights in OpenRGB's order, each at
        /// its place in the color banks (keys[],
        /// CorsairK55RGBPROXTController.cpp:19-25, and key_names,
        /// RGBController_CorsairK55RGBPROXT.cpp:28-188).</summary>
        internal static List<LightInfo> K55ProXtLights()
        {
            var lights = new List<LightInfo>();
            for (int i = 0; i < CorsairLights.K55ProXtKeyNames.Length; i++)
            {
                var light = CorsairLights.Light(CorsairLights.K55ProXtKeys[i], CorsairLights.K55ProXtKeyNames[i], LightKind.Other);
                lights.Add(CorsairLights.Placed(light, CorsairLights.K55ProXtMatrixMap, i));
            }
            return lights;
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        /// <summary>The firmware-information query, CORSAIR_COMMAND_READ and
        /// CORSAIR_PROPERTY_FIRMWARE_INFO (lines 557-559).</summary>
        internal static byte[] Query() => CorsairLights.Frame(0x0E, 0x01);

        /// <summary>CORSAIR_PROPERTY_SPECIAL_FUNCTION with a mode: 02
        /// software, 01 hardware (lines 352-363 and 499-520).</summary>
        internal static byte[] SpecialFunction(byte mode) => CorsairLights.Frame(0x07, 0x04, mode);

        /// <summary>CORSAIR_PROPERTY_LIGHTING_CONTROL, software, with byte 5
        /// by type (lines 370-415).</summary>
        internal static byte[] LightingControl(CorsairDeviceType type)
            => CorsairLights.Frame(0x07, 0x05, 0x02, 0x00, type switch
            {
                CorsairDeviceType.Mouse => 0x01,
                CorsairDeviceType.Mousemat => 0x04,
                _ => 0x03,
            });

        /// <summary>Whether a keyboard table takes the special-function
        /// write and the key-input setup (lines 80-90).</summary>
        internal static bool TakesKeyInput(Logical logical)
            => logical is Logical.K55 or Logical.K95Platinum or Logical.K70Mk2 or Logical.K68;

        /// <summary>SetupK55AndK95LightingControl (lines 421-497): 07 05 08 00
        /// 01, then four 07 40 1E reports of thirty key numbers each set to
        /// C0, the numbers counting up past the board's skip list.</summary>
        internal static List<byte[]> KeyInputFrames(Logical logical, Physical physical)
        {
            var frames = new List<byte[]> { CorsairLights.Frame(0x07, 0x05, 0x08, 0x00, 0x01) };
            int[] skipped = CorsairLights.KeyMappingK95PlatAnsi;
            if (physical == Physical.Iso)
                skipped = logical is Logical.K70Mk2 or Logical.K70LuxRgb
                    ? CorsairLights.KeyMappingK70Mk2PlatIso
                    : CorsairLights.KeyMappingK95PlatIso;
            int identifier = 0;
            for (int i = 0; i < 4; i++)
            {
                var frame = CorsairLights.Frame(0x07, 0x40, 0x1E);
                for (int j = 0; j < 30; j++)
                {
                    foreach (int skip in skipped)
                        if (identifier == skip) identifier++;
                    frame[5 + 2 * j] = (byte)identifier++;
                    frame[6 + 2 * j] = 0xC0;
                }
                frames.Add(frame);
            }
            return frames;
        }

        /// <summary>The writes after the identification, in OpenRGB's order:
        /// the special-function write ReadFirmwareInfo sends a mouse, a
        /// mousepad or a stand, and a keyboard's own, the lighting control,
        /// and a keyboard's key-input setup (lines 75-90 and 684-704). A
        /// device that is no keyboard has no keyboard table: OpenRGB leaves
        /// the table unset for it.</summary>
        internal static List<byte[]> OpenFrames(CorsairDeviceType type, Logical logical, Physical physical)
        {
            var frames = new List<byte[]>();
            bool keyboard = type == CorsairDeviceType.Keyboard;
            if (!keyboard || TakesKeyInput(logical)) frames.Add(SpecialFunction(0x02));
            frames.Add(LightingControl(type));
            if (keyboard && TakesKeyInput(logical)) frames.AddRange(KeyInputFrames(logical, physical));
            return frames;
        }

        /// <summary>A picture's reports, each color at its light's address.</summary>
        internal static List<byte[]> PictureFrames(CorsairDeviceType type, Logical logical,
            IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            var frames = new List<byte[]>();
            switch (type)
            {
                case CorsairDeviceType.Keyboard when logical == Logical.K55:
                {
                    // SubmitKeyboardZonesColors (lines 885-921).
                    var frame = CorsairLights.Frame(0x07, 0x25, 0x00, 0x00);
                    for (int n = 0; n < count; n++)
                    {
                        int at = lights[n].Address;
                        if ((uint)at >= 3) continue;
                        Put(frame, 5 + at * 3, colors[n]);
                    }
                    frames.Add(frame);
                    break;
                }
                case CorsairDeviceType.Keyboard:
                {
                    var red = new byte[KeyboardBankSize];
                    var green = new byte[KeyboardBankSize];
                    var blue = new byte[KeyboardBankSize];
                    for (int n = 0; n < count; n++)
                    {
                        int at = lights[n].Address;
                        if ((uint)at >= KeyboardBankSize) continue;
                        red[at] = (byte)(colors[n] >> 16);
                        green[at] = (byte)(colors[n] >> 8);
                        blue[at] = (byte)colors[n];
                    }
                    byte last = (byte)(logical is Logical.K95Platinum or Logical.K95 ? 48 : 24);
                    byte channel = 1;
                    foreach (var bank in new[] { red, green, blue })
                    {
                        frames.Add(Stream(1, 60, bank, 0));
                        frames.Add(Stream(2, 60, bank, 60));
                        frames.Add(Stream(3, last, bank, 120));
                        frames.Add(CorsairLights.Frame(0x07, 0x28, channel, 0x03, (byte)(channel == 3 ? 2 : 1)));
                        channel++;
                    }
                    break;
                }
                case CorsairDeviceType.Mouse:
                {
                    // SubmitMouseColors: zone, red, green, blue (lines 949-986).
                    var frame = CorsairLights.Frame(0x07, 0x22, MouseZones, 0x00);
                    for (int zone = 0; zone < MouseZones; zone++) frame[5 + zone * 4] = (byte)zone;
                    for (int n = 0; n < count; n++)
                    {
                        int at = lights[n].Address;
                        if ((uint)at >= MouseZones) continue;
                        Put(frame, 6 + at * 4, colors[n]);
                    }
                    frames.Add(frame);
                    break;
                }
                default:
                {
                    // SubmitMousematColors: red, green, blue (lines 988-1025),
                    // fifteen for the mousepad and nine for the stand.
                    int zones = type == CorsairDeviceType.HeadsetStand ? CorsairLights.St100.Length : MouseZones;
                    var frame = CorsairLights.Frame(0x07, 0x22, (byte)zones, 0x00);
                    for (int n = 0; n < count; n++)
                    {
                        int at = lights[n].Address;
                        if ((uint)at >= zones) continue;
                        Put(frame, 5 + at * 3, colors[n]);
                    }
                    frames.Add(frame);
                    break;
                }
            }
            return frames;
        }

        /// <summary>StreamPacket: 7F, the packet's number, its size, then the
        /// bytes from byte 5 (lines 722-753).</summary>
        private static byte[] Stream(byte packet, byte size, byte[] bank, int from)
        {
            var frame = CorsairLights.Frame(0x7F, packet, size);
            Array.Copy(bank, from, frame, 5, size);
            return frame;
        }

        private static void Put(byte[] frame, int at, int color)
        {
            frame[at] = (byte)(color >> 16);
            frame[at + 1] = (byte)(color >> 8);
            frame[at + 2] = (byte)color;
        }

        /// <summary>The K65 Mini's open: 08 01 03 00 02, 08 02 6E and 08 0D 01
        /// 22, each in a 1025-byte report followed by 10 ms
        /// (CorsairK65MiniController.cpp:55-90).</summary>
        internal static List<byte[]> K65MiniOpenFrames() => new()
        {
            K65MiniFrame(0x08, 0x01, 0x03, 0x00, 0x02),
            K65MiniFrame(0x08, 0x02, 0x6E),
            K65MiniFrame(0x08, 0x0D, 0x01, 0x22),
        };

        /// <summary>The K65 Mini's picture: 08 06 01 73 01 00 00 12 00, then
        /// red, green and blue at byte 10 plus three times each light's
        /// position (lines 92-118).</summary>
        internal static byte[] K65MiniPicture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = K65MiniFrame(0x08, 0x06, 0x01, 0x73, 0x01, 0x00, 0x00, 0x12);
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = 0x0A + lights[n].Address * 3;
                if (at < 0 || at + 2 >= frame.Length) continue;
                Put(frame, at, colors[n]);
            }
            return frame;
        }

        private static byte[] K65MiniFrame(params byte[] bytes)
        {
            var frame = new byte[K65MiniPacketLength];
            Array.Copy(bytes, 0, frame, 1, bytes.Length);
            return frame;
        }

        /// <summary>The K55 RGB PRO XT's open: 08 01 03 00 02, 08 02 5F and
        /// 08 0D 00 01 (CorsairK55RGBPROXTController.cpp:78-100).</summary>
        internal static List<byte[]> K55ProXtOpenFrames() => new()
        {
            CorsairLights.Frame(0x08, 0x01, 0x03, 0x00, 0x02),
            CorsairLights.Frame(0x08, 0x02, 0x5F),
            CorsairLights.Frame(0x08, 0x0D, 0x00, 0x01),
        };

        /// <summary>The K55 RGB PRO XT's picture (lines 102-133): the three
        /// banks of 137 bytes one after the other, each light at its place,
        /// sent 53 bytes from byte 12 of 08 06 00 9B 01, then 61 bytes from
        /// byte 4 of each of six 08 07 reports. Those carry 419 bytes where
        /// the banks hold 411, and OpenRGB reads the last 8 past its array.
        /// They go out as zeros here: the header announces 0x019B = 411
        /// bytes.</summary>
        internal static List<byte[]> K55ProXtPicture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var banks = new byte[53 + 6 * 61];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int n = 0; n < count; n++)
            {
                int at = lights[n].Address;
                if ((uint)at >= K55ProXtBankSize) continue;
                banks[at] = (byte)(colors[n] >> 16);
                banks[K55ProXtBankSize + at] = (byte)(colors[n] >> 8);
                banks[2 * K55ProXtBankSize + at] = (byte)colors[n];
            }
            var frames = new List<byte[]>(7);
            var first = CorsairLights.Frame(0x08, 0x06, 0x00, 0x9B, 0x01);
            Array.Copy(banks, 0, first, 12, 53);
            frames.Add(first);
            for (int i = 0; i < 6; i++)
            {
                var next = CorsairLights.Frame(0x08, 0x07, 0x00);
                Array.Copy(banks, 53 + i * 61, next, 4, 61);
                frames.Add(next);
            }
            return frames;
        }

        /// <summary>The newer protocol's hardware render mode
        /// (CorsairPeripheralV2Controller.cpp:213-233 with write command 08).</summary>
        internal static byte[] BoardHandBack() => CorsairLights.Frame(0x08, 0x01, 0x03, 0x00, 0x01);

        // ─────────────────────────────────────────────
        //  The exchanges
        // ─────────────────────────────────────────────

        /// <summary>ReadFirmwareInfo (lines 522-602): the device's type and
        /// board layout, or null when no answer named a type. Null too when
        /// the query could not be written.</summary>
        internal static (CorsairDeviceType Type, Physical Physical)? Identify(IHidReportChannel io, ushort productId)
        {
            var query = Query();
            for (int attempt = 0; attempt < IdentifyAttempts; attempt++)
            {
                if (!io.WriteOutput(query)) return null;
                var answer = new byte[Math.Max(CorsairLights.PacketLength, io.InputReportLength)];
                int read = io.ReadInput(answer, IdentifyReadMs);
                if (read == 0)
                {
                    answer = new byte[Math.Max(CorsairLights.PacketLength, io.FeatureReportLength)];
                    if (!io.SetFeature(query) || !io.GetFeature(0, answer)) Array.Clear(answer);
                }
                else if (read < 0)
                {
                    Array.Clear(answer);
                }
                var type = TypeOfAnswer(answer[AnswerTypeAt], productId);
                if (type is CorsairDeviceType found) return (found, PhysicalOf(answer[AnswerLayoutAt]));
            }
            return null;
        }

        private HidLightingSession BeginPeripheral(IHidReportChannel io, Action<int> wait, ushort productId, string name,
            string product, bool oneColor)
        {
            var identity = Identify(io, productId);
            if (identity == null) return null;
            var (type, physical) = identity.Value;
            var logical = type == CorsairDeviceType.Keyboard ? LogicalOf(productId) : Logical.Normal;
            foreach (var frame in OpenFrames(type, logical, physical))
                if (!io.WriteOutput(frame)) return null;
            var map = new LightMap(Lights(type, logical, name), $"OpenRGB RGBController_CorsairPeripheral.cpp, {type}")
            { Product = product };
            bool keyboard = type == CorsairDeviceType.Keyboard;
            return new PeripheralSession(product, type, logical, physical, map, keyboard ? 2 : 1, oneColor);
        }

        private HidLightingSession BeginK65Mini(IHidReportChannel io, Action<int> wait, string product)
        {
            foreach (var frame in K65MiniOpenFrames())
            {
                if (!io.WriteOutput(frame)) return null;
                wait(K65MiniOpenGapMs);
            }
            return new BoardSession(product, (lights, colors) => new List<byte[]> { K65MiniPicture(lights, colors) });
        }

        private HidLightingSession BeginK55ProXt(IHidReportChannel io, Action<int> wait, string product)
        {
            foreach (var frame in K55ProXtOpenFrames())
                if (!io.WriteOutput(frame)) return null;
            return new BoardSession(product, K55ProXtPicture);
        }

        /// <summary>A session of the older protocol.</summary>
        internal sealed class PeripheralSession : CorsairSession
        {
            private readonly bool _oneColor;

            public PeripheralSession(string product, CorsairDeviceType type, Logical logical,
                Physical physical, LightMap map, int handBacks, bool oneColor = false)
                : base(product, SpecialFunction(0x01), handBacks, KeyboardHandBackGapMs)
            {
                _oneColor = oneColor;
                Type = type;
                Logical = logical;
                Physical = physical;
                Map = map;
            }

            public CorsairDeviceType Type { get; }
            public Logical Logical { get; }
            public Physical Physical { get; }

            /// <summary>The lights of the type the device answered
            /// (<see cref="HidLightingSession.Map"/>).</summary>
            public override LightMap Map { get; }

            protected override bool Send(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
            {
                var shown = _oneColor ? colors.Select(CorsairLights.Mono).ToList() : colors;
                foreach (var frame in PictureFrames(Type, Logical, lights, shown))
                    if (!io.WriteOutput(frame)) return false;
                return true;
            }
        }

        /// <summary>A session of the K65 Mini or the K55 RGB PRO XT.</summary>
        internal sealed class BoardSession : CorsairSession
        {
            private readonly Func<IReadOnlyList<LightInfo>, IReadOnlyList<int>, List<byte[]>> _picture;

            public BoardSession(string product,
                Func<IReadOnlyList<LightInfo>, IReadOnlyList<int>, List<byte[]>> picture)
                : base(product, BoardHandBack(), 1, 0)
                => _picture = picture;

            protected override bool Send(IHidReportChannel io, Action<int> wait, IReadOnlyList<LightInfo> lights,
                IReadOnlyList<int> colors)
            {
                foreach (var frame in _picture(lights, colors))
                    if (!io.WriteOutput(frame)) return false;
                return true;
            }
        }
    }
}
