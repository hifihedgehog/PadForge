using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>One device the iCUE SDK lists, as CorsairDeviceInfo holds it
    /// (cue-sdk src/include/iCUESDK.h:212-220): its CorsairDeviceType, its
    /// id, serial number and model, and its LED and channel counts.</summary>
    internal sealed record IcueDeviceInfo(int Type, string Id, string Serial, string Model, int LedCount, int ChannelCount);

    /// <summary>One LED of a device and where it sits, CorsairLedPosition
    /// (iCUESDK.h:222-227): in millimeters for keyboards, mice, mousemats
    /// and headset stands.</summary>
    internal readonly record struct IcueLedPosition(uint Id, double X, double Y);

    /// <summary>One LED and its color, CorsairLedColor (iCUESDK.h:299-306):
    /// the LED id, red, green and blue from 0 to 255, and the alpha, "0 for
    /// completely translucent to 255 for opaque". Laid out as the SDK takes
    /// it, 8 bytes, so an array goes to CorsairSetLedColors as it is.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal readonly record struct IcueLedColor(uint Id, byte R, byte G, byte B, byte A);

    /// <summary>
    /// The native seam of the iCUE worker (#557). Production is
    /// <see cref="PadForge.Services.IcueSdkNative"/>, and a test scripts a
    /// fake. Every call is made from the worker's thread alone, the one
    /// RGB.NET's provider makes them from under its lock
    /// (RGB.NET.Devices.Corsair CorsairDeviceProvider.cs:21 and 336).
    /// Each call that the SDK answers with a CorsairError returns it as an
    /// int (<see cref="IcueRoute.Success"/> and the rest).
    /// </summary>
    internal interface IIcueNative
    {
        /// <summary>Finds and loads the SDK's client library and resolves
        /// its exports. False, with the reason, when it cannot.</summary>
        bool TryLoad(out string detail);

        /// <summary>CorsairConnect with a handler that keeps the newest
        /// session state (<see cref="State"/>).</summary>
        int Connect();

        /// <summary>The CorsairSessionState the SDK last reported, 0 before
        /// any.</summary>
        int State { get; }

        /// <summary>CorsairGetDevices with a type mask.</summary>
        int GetDevices(int typeMask, out IReadOnlyList<IcueDeviceInfo> devices);

        /// <summary>CorsairGetLedPositions for one device.</summary>
        int GetLedPositions(string deviceId, out IReadOnlyList<IcueLedPosition> leds);

        /// <summary>CorsairRequestControl for one device.</summary>
        int RequestControl(string deviceId, int accessLevel);

        /// <summary>CorsairSetLedColors for one device.</summary>
        int SetLedColors(string deviceId, IcueLedColor[] colors);

        /// <summary>CorsairDisconnect.</summary>
        int Disconnect();

        /// <summary>Frees the library. Idempotent.</summary>
        void Unload();
    }

    /// <summary>
    /// What the iCUE route knows of Corsair's iCUE SDK v4 (#557), read from
    /// the SDK's own header and documentation (CorsairOfficial/cue-sdk at
    /// 890bc3e, src/include/iCUESDK.h and docs/index.html, the page
    /// corsairofficial.github.io/cue-sdk) and from RGB.NET's provider for it
    /// (RGB.NET.Devices.Corsair at 3c5af72): the paths a Corsair row is lit
    /// through while iCUE runs, and the light map of a device type from the
    /// LEDs iCUE reports.
    ///
    /// <para>Matching. CorsairDeviceInfo names a device by an id that is
    /// unique "at least within session", a serial number, a model name and a
    /// type (iCUESDK.h:212-220). It gives no USB path and no product ID,
    /// and PadForge does not read a device's serial number, so a row is
    /// matched to the devices of its type, the way a Razer row is matched to
    /// a Chroma category (<see cref="PeripheralLinker.ChromaCategories"/>).
    /// The row's type comes from its product, by OpenRGB's tables the Corsair
    /// families already carry (<see cref="RowType"/>). One path per type
    /// (<see cref="Types"/>) reaches every device iCUE lists of that type,
    /// and the smallest displayed player number rules it
    /// (<see cref="OutputPath.Shared"/>).</para>
    /// </summary>
    internal static class IcueRoute
    {
        // CorsairError (iCUESDK.h:22-32).
        internal const int Success = 0;
        internal const int NotConnected = 1;
        internal const int NoControl = 2;
        internal const int IncompatibleProtocol = 3;
        internal const int InvalidArguments = 4;
        internal const int InvalidOperation = 5;
        internal const int DeviceNotFound = 6;
        internal const int NotAllowed = 7;

        // CorsairSessionState (iCUESDK.h:34-43).
        internal const int StateClosed = 1;
        internal const int StateConnecting = 2;
        internal const int StateTimeout = 3;
        internal const int StateConnectionRefused = 4;
        internal const int StateConnectionLost = 5;
        internal const int StateConnected = 6;

        /// <summary>CAL_Shared, the access level RGB.NET requests unless it
        /// is told to take exclusive control (iCUESDK.h:184-190,
        /// CorsairDeviceProvider.cs:56 and 163).</summary>
        internal const int AccessShared = 0;

        /// <summary>CORSAIR_DEVICE_COUNT_MAX and CORSAIR_DEVICE_LEDCOUNT_MAX
        /// (iCUESDK.h:13-14).</summary>
        internal const int DeviceCountMax = 64;
        internal const int LedCountMax = 512;

        /// <summary>CORSAIR_STRING_SIZE_M, the length of a device id
        /// (iCUESDK.h:11 and 17).</summary>
        internal const int StringSizeM = 128;

        /// <summary>The alpha of a painted LED, opaque, and of a LED handed
        /// back, "completely translucent" (iCUESDK.h:305). A shared client's
        /// layer sits over iCUE's own at priority 128 against iCUE's 127
        /// (docs, CorsairSetLayerPriority), so a translucent LED shows iCUE's
        /// lighting beneath it.</summary>
        internal const byte Opaque = 255;
        internal const byte Clear = 0;

        /// <summary>The device types a Corsair row can be, each with the path
        /// key it is lit through and its CorsairDeviceType
        /// (iCUESDK.h:45-62).</summary>
        internal static readonly (string Key, int DeviceType)[] Types =
        {
            ("keyboard", 0x0001),
            ("mouse", 0x0002),
            ("mousemat", 0x0004),
            ("headsetstand", 0x0010),
        };

        /// <summary>The mask of every type in <see cref="Types"/>, for
        /// CorsairGetDevices.</summary>
        internal static int TypeMask
        {
            get
            {
                int mask = 0;
                foreach (var (_, type) in Types) mask |= type;
                return mask;
            }
        }

        /// <summary>The path key of a CorsairDeviceType, or null for a type
        /// no Corsair row is lit through.</summary>
        internal static string KeyOf(int deviceType)
        {
            foreach (var (key, type) in Types)
                if (type == deviceType) return key;
            return null;
        }

        /// <summary>The path key of an OpenRGB Corsair device type.</summary>
        internal static string KeyOf(CorsairDeviceType type) => type switch
        {
            CorsairDeviceType.Mouse => "mouse",
            CorsairDeviceType.Mousemat => "mousemat",
            CorsairDeviceType.HeadsetStand => "headsetstand",
            _ => "keyboard",
        };

        /// <summary>The type of a Corsair row's device: the type OpenRGB's
        /// V2 table gives its product (CorsairPeripheralV2Devices.cpp,
        /// <see cref="CorsairPeripheralV2Lighting.DeviceOf"/>), else the
        /// section of CorsairPeripheralControllerDetect.cpp its product is
        /// registered in (<see cref="CorsairPeripheralLighting.TypeOf"/>),
        /// else, for a product neither lists, such as a wireless receiver,
        /// the row's own kind: a keyboard row is a keyboard and any other a
        /// mouse.</summary>
        internal static CorsairDeviceType RowType(ushort productId, int capType)
        {
            if (CorsairPeripheralV2Lighting.DeviceOf(productId) is { } device) return device.Type;
            foreach (var section in CorsairLights.V1Sections)
                if (section.ProductId == productId) return CorsairPeripheralLighting.TypeOf(productId);
            return PeripheralLinker.IsKeyboard(capType) ? CorsairDeviceType.Keyboard : CorsairDeviceType.Mouse;
        }

        /// <summary>
        /// The route's line in the link pass (#557 item 11): while the
        /// program holding a Corsair row's device is iCUE and its SDK's
        /// library is found (<see cref="PeripheralPresence.CorsairIcueSdk"/>),
        /// the row's type path goes into <paramref name="shared"/>, and the
        /// row is lit through iCUE. Only a Corsair device: Lenovo's Legion
        /// laptops name iCUE too (<see cref="VendorPrograms.LenovoLegionUsb"/>),
        /// and no source shows iCUE's SDK listing their keyboards.
        /// </summary>
        internal static void AddShared(List<OutputPath> shared, VendorProgram holder, PeripheralPresence presence, LinkRow row)
        {
            if (!ReferenceEquals(holder, VendorPrograms.CorsairIcue) || presence?.CorsairIcueSdk != true) return;
            if (row.VendorId != CorsairLights.VendorId) return;
            var path = new OutputPath(OutputFamily.IcueDevice, KeyOf(RowType(row.ProductId, row.CapType)));
            if (!shared.Contains(path)) shared.Add(path);
        }

        /// <summary>
        /// One light for a LED iCUE reports, addressed by its LED id. An id
        /// is the group in bits 31 to 16 and the index in bits 15 to 0
        /// (docs, "LED identification"). In the keyboard group the index is
        /// CorsairLedId_Keyboard, and the key is named by the table generated
        /// from the SDK's header (<see cref="IcueLeds.KeyboardKeys"/>): a key
        /// with a usage is a key light, one with a label another light with
        /// it, and an index the table lacks a key no source named. The G-key
        /// group is the G-keys by index, as RGB.NET maps it
        /// (Generic/LedMappings.cs:195-197). The edge group is the keyboard's
        /// light pipe, and the vendor group lights such as a profile switch
        /// or a dial ring (iCUESDKLedIdEnum.h:5-8). Any other group's LED is
        /// a zone, numbered by <see cref="Map"/>. Each light sits where iCUE
        /// places it, in millimeters.
        /// </summary>
        internal static LightInfo LightOf(in IcueLedPosition led)
        {
            int group = (int)(led.Id >> 16);
            int index = (int)(led.Id & 0xFFFF);
            int address = unchecked((int)led.Id);
            LightInfo light;
            if (group == IcueLeds.GroupKeyboard)
            {
                light = new LightInfo(address, LightKind.Key, 0, LightKeySource.None);
                foreach (var key in IcueLeds.KeyboardKeys)
                {
                    if (key.Index != index) continue;
                    light = key.Usage != 0
                        ? new LightInfo(address, LightKind.Key, key.Usage, LightKeySource.Vendor)
                        : new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = key.Label };
                    break;
                }
            }
            else if (group == IcueLeds.GroupKeyboardGKeys)
                light = new LightInfo(address, LightKind.Other, 0, LightKeySource.None) { Label = LightLabel.GKey, LabelNumber = index };
            else if (group == IcueLeds.GroupKeyboardEdge)
                light = new LightInfo(address, LightKind.Edge, 0, LightKeySource.None);
            else if (group == IcueLeds.GroupKeyboardOem)
                light = new LightInfo(address, LightKind.Other, 0, LightKeySource.None);
            else
                light = new LightInfo(address, LightKind.Zone, 0, LightKeySource.None);
            return light with { X = (float)led.X, Y = (float)led.Y, HasPosition = true };
        }

        /// <summary>The light map of a type: every LED the type's devices
        /// report, each once by its id, in id order, which puts the keys
        /// first in the keyboard group's order, as RGB.NET orders a device's
        /// LEDs (Generic/LedMappings.cs:170 and 188). A LED two devices
        /// report keeps the first one's place. Zones are numbered from 1 in
        /// that order. Null for no LEDs.</summary>
        internal static LightMap Map(string key, IEnumerable<IReadOnlyList<IcueLedPosition>> devices)
        {
            var byId = new SortedDictionary<uint, IcueLedPosition>();
            foreach (var leds in devices)
            {
                if (leds == null) continue;
                foreach (var led in leds)
                    if ((led.Id & 0xFFFF) != 0 && !byId.ContainsKey(led.Id)) byId.Add(led.Id, led);
            }
            if (byId.Count == 0) return null;
            var lights = new List<LightInfo>(byId.Count);
            int zone = 0;
            foreach (var led in byId.Values)
            {
                var light = LightOf(led);
                if (light.Kind == LightKind.Zone) light = light with { Zone = ++zone };
                lights.Add(light);
            }
            return new LightMap(lights, "iCUE SDK LED positions, " + key) { Product = "icue:" + key };
        }

        /// <summary>The colors of one device's LEDs from a picture of its
        /// type's map: each LED the color its light was rendered, opaque,
        /// in the order the device reports its LEDs. A LED the map lacks
        /// takes <paramref name="fill"/>.</summary>
        internal static IcueLedColor[] Colors(IReadOnlyList<IcueLedPosition> leds, LightMap map, int[] colors, int fill)
        {
            var index = new Dictionary<int, int>(map.Lights.Count);
            for (int i = 0; i < map.Lights.Count; i++) index[map.Lights[i].Address] = i;
            var result = new IcueLedColor[leds.Count];
            for (int i = 0; i < leds.Count; i++)
            {
                int rgb = index.TryGetValue(unchecked((int)leds[i].Id), out int at) ? colors[at] : fill;
                result[i] = new IcueLedColor(leds[i].Id, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, Opaque);
            }
            return result;
        }

        /// <summary>The colors that hand a device's LEDs back to iCUE: each
        /// one black and translucent (<see cref="Clear"/>).</summary>
        internal static IcueLedColor[] Cleared(IReadOnlyList<IcueLedPosition> leds)
        {
            var result = new IcueLedColor[leds.Count];
            for (int i = 0; i < leds.Count; i++) result[i] = new IcueLedColor(leds[i].Id, 0, 0, 0, Clear);
            return result;
        }
    }
}
