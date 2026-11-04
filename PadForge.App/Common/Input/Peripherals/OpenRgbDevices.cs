using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// What PadForge reads from an OpenRGB controller's description (#557):
    /// which device it is, from OpenRGB's own location string, and its
    /// light map, from its LED names, zones and matrix maps. Pure.
    /// </summary>
    internal static class OpenRgbDevices
    {
        /// <summary>The mode PadForge colors a controller in. OpenRGB's
        /// own search for a mode to color LED by LED tries "Direct" first
        /// (RGBController/RGBController.cpp:1499-1521, SetCustomMode).
        /// RGB.NET's OpenRGB provider takes only a controller with a Direct
        /// mode and sets that mode (RGB.NET.Devices.OpenRGB
        /// OpenRGBDeviceProvider.cs:111-122), and Aurora names its switch
        /// for every other controller a dangerous one
        /// (AuroraDeviceManager/Devices/RGBNet/Implementations/OpenRgbNetDevice.cs:37-40):
        /// the Custom and Static modes the server falls back on can keep
        /// each update in the device's memory. PadForge takes Direct
        /// alone.</summary>
        internal const string DirectModeName = "Direct";

        private const string HidPrefix = "HID: ";

        /// <summary>The start of a Windows device interface path, the form
        /// hidapi gives a collection's path in.</summary>
        private const string InterfacePrefix = @"\\?\";

        /// <summary>The location of a controller behind a Logitech
        /// LIGHTSPEED receiver: the receiver's path, then the device's slot
        /// (Controllers/LogitechController/LogitechLightspeedController/LogitechLightspeedController.cpp:29).</summary>
        private const string ReceiverMarker = "Wireless Index:";

        /// <summary>The location a serial device's controller names, "COM: "
        /// and the port (Controllers/DygmaRaiseController/DygmaRaiseController.cpp:51-54).</summary>
        private const string SerialPrefix = "COM: ";

        /// <summary>The index of the controller's Direct mode, the one its
        /// LEDs take colors in one by one, or -1 when it has none.</summary>
        public static int DirectMode(OpenRgbController controller)
        {
            if (controller?.Modes == null) return -1;
            for (int i = 0; i < controller.Modes.Count; i++)
            {
                var mode = controller.Modes[i];
                if (mode != null && mode.Name == DirectModeName && mode.ColorMode == OpenRgbProtocol.ModeColorsPerLed)
                    return i;
            }
            return -1;
        }

        /// <summary>The HID path of a controller found by its HID
        /// collection: OpenRGB's location is "HID: " and the path the
        /// controller opened (Controllers/RazerController/RazerController/RazerController.cpp:142,
        /// one of 205 controllers that return that string), and a
        /// receiver's adds the device's slot after a space. A few families
        /// give the path alone, with no "HID: " ahead of it: the Cooler
        /// Master keyboards (CMKeyboardAbstractController.cpp:21 and 73-76),
        /// the EVGA mice (EVGAMouseController.cpp:52 and 90-93) and the
        /// Lenovo laptop keyboards (LenovoUSBController.cpp:21 and 45), each
        /// path a Windows device interface path that starts "\\?\". Null for
        /// any other location.</summary>
        public static string HidPath(string location)
        {
            if (location == null) return null;
            string rest;
            if (location.StartsWith(HidPrefix, StringComparison.Ordinal)) rest = location.Substring(HidPrefix.Length);
            else if (location.StartsWith(InterfacePrefix, StringComparison.Ordinal)) rest = location;
            else return null;
            int end = 0;
            while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
            return end == 0 ? null : rest.Substring(0, end);
        }

        /// <summary>Whether the controller is a device behind a receiver
        /// that other devices share.</summary>
        public static bool OnReceiver(string location)
            => location != null && location.Contains(ReceiverMarker, StringComparison.Ordinal);

        /// <summary>Whether the controller lights the PC's own keyboard
        /// through the PC's firmware: an ASUS TUF laptop's through its ACPI
        /// device (Controllers/AsusTUFLaptopController/RGBController_AsusTUFLaptop_Windows.cpp:31)
        /// or an HP Omen laptop's through its BIOS WMI method
        /// (Controllers/HPOmenLaptopController/RGBController_HPOmenLaptopWMI_Windows.cpp:33).
        /// The shared worker keys its units of those families by the same
        /// strings.</summary>
        public static bool BuiltInKeyboard(string location)
            => location == AsusTufLaptopLighting.Key || location == HpOmenLaptopLighting.Key;

        /// <summary>The key of the shared worker's unit that a controller
        /// with no HID location holds: the same string for a laptop's own
        /// keyboard, and for a serial device the port's device name, the way
        /// the Dygma Raise family keys its unit (DygmaRaiseLighting.Look).
        /// Null for any other location.</summary>
        public static string HeldKey(string location)
        {
            if (BuiltInKeyboard(location)) return location;
            if (location != null && location.StartsWith(SerialPrefix, StringComparison.Ordinal))
            {
                string port = location.Substring(SerialPrefix.Length).Trim();
                return port.Length == 0 ? null : @"\\.\" + port;
            }
            return null;
        }

        private static readonly Regex UsbIds = new(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The Bluetooth form, where four digits of the vendor ID's
        /// source come ahead of it: "_vid&amp;0002046d_pid&amp;b023".</summary>
        private static readonly Regex BluetoothIds = new(@"_vid&[0-9a-f]{4}([0-9a-f]{4})_pid&([0-9a-f]{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The vendor and product ID a HID path names, or zeros.</summary>
        public static (ushort VendorId, ushort ProductId) IdsOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return (0, 0);
            var match = UsbIds.Match(path);
            if (!match.Success) match = BluetoothIds.Match(path);
            if (!match.Success) return (0, 0);
            return (ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        /// <summary>The LampArrayKind-style kind of a controller on a
        /// receiver, by OpenRGB's device type: 1 for a keyboard, a keypad or
        /// a laptop, 2 for a mouse, 0 for any other type.</summary>
        public static int KindOf(int type) => type switch
        {
            OpenRgbProtocol.DeviceKeyboard or OpenRgbProtocol.DeviceKeypad or OpenRgbProtocol.DeviceLaptop
                => PeripheralLinker.LampArrayKindKeyboard,
            OpenRgbProtocol.DeviceMouse => PeripheralLinker.LampArrayKindMouse,
            _ => 0,
        };

        /// <summary>The LEDs a zone holds in the controller's color buffer:
        /// its count, and one for a zone sized by hand for effects only
        /// (RGBController.cpp:2326-2341, LEDsInZone).</summary>
        public static int LedsIn(OpenRgbZone zone)
        {
            if (zone == null) return 0;
            uint count = zone.LedsCount;
            if ((zone.Flags & OpenRgbProtocol.ZoneFlagSizeEffectsOnly) != 0 && count > 1) count = 1;
            return (int)Math.Min(count, int.MaxValue);
        }

        /// <summary>
        /// The light map of a controller: one light per LED, in the order
        /// of its color buffer, which is the order UpdateLEDs takes colors
        /// in. A zone's LEDs start where the zones before it end
        /// (RGBController.cpp:2343-2390, SetupColors).
        ///
        /// <para>An LED whose name OpenRGB's key names give a usage
        /// (<see cref="OpenRgbKeys"/>) is a key light. One whose name has a
        /// label, a media key or an indicator, is an Other light with that
        /// label, as in the layouts PadForge ports from OpenRGB
        /// (KeyboardLayoutManager.Lights). One whose name or zone names a
        /// logo is a logo light. Any other LED in a keyboard's matrix is an
        /// Other light, and any other LED of a zone is a light of that zone,
        /// the zones numbered from 1 in OpenRGB's order. A light in a
        /// matrix zone is placed at its cell, row and column, the way
        /// OpenRGB's own device view places it (qt/DeviceView.cpp:1263-1275).</para>
        /// </summary>
        public static LightMap Map(OpenRgbController controller)
        {
            if (controller == null) return LightMap.Empty;
            int total = controller.Colors?.Length ?? 0;
            var lights = new LightInfo[total];
            var placed = new bool[total];
            var cells = new (int Row, int Col)[total];
            bool keyboard = KindOf(controller.Type) == PeripheralLinker.LampArrayKindKeyboard;

            int start = 0, zoneNumber = 0;
            var zones = controller.Zones ?? Array.Empty<OpenRgbZone>();
            var zoneOf = new int[total];
            Array.Fill(zoneOf, -1);
            for (int z = 0; z < zones.Count; z++)
            {
                var zone = zones[z];
                int count = LedsIn(zone);
                for (int i = start; i < start + count && i < total; i++) zoneOf[i] = z;
                if (zone != null && zone.Matrix.Length > 0 && zone.MatrixWidth > 0)
                {
                    for (int cell = 0; cell < zone.Matrix.Length; cell++)
                    {
                        uint local = zone.Matrix[cell];
                        if (local == OpenRgbProtocol.NoLed || local >= (uint)count) continue;
                        long led = start + local;
                        if (led >= total) continue;
                        placed[led] = true;
                        cells[led] = ((int)(cell / zone.MatrixWidth), (int)(cell % zone.MatrixWidth));
                    }
                }
                start += count;
            }

            var numbers = new int[zones.Count];
            for (int i = 0; i < total; i++)
            {
                string name = controller.Leds != null && i < controller.Leds.Count ? controller.Leds[i] : null;
                int z = zoneOf[i];
                var zone = z >= 0 ? zones[z] : null;
                LightInfo light;
                bool named = OpenRgbKeys.TryGet(name, out ushort usage, out var label, out int number);
                if (named && usage != 0)
                    light = new LightInfo(i, LightKind.Key, usage, LightKeySource.Generated);
                else if (named)
                    light = new LightInfo(i, LightKind.Other, 0, LightKeySource.None) { Label = label, LabelNumber = number };
                else if (NamesLogo(name) || NamesLogo(zone?.Name))
                    light = new LightInfo(i, LightKind.Logo, 0, LightKeySource.None);
                else if (keyboard && zone != null && zone.Type == OpenRgbProtocol.ZoneMatrix)
                    light = new LightInfo(i, LightKind.Other, 0, LightKeySource.None);
                else
                {
                    int key = Math.Max(z, 0);
                    if (z >= 0 && numbers[z] == 0) numbers[z] = ++zoneNumber;
                    light = new LightInfo(i, LightKind.Zone, 0, LightKeySource.None)
                    {
                        Zone = z >= 0 ? numbers[key] : Math.Max(1, zoneNumber),
                    };
                }
                if (placed[i])
                    light = light with { X = cells[i].Col, Y = cells[i].Row, Width = 1, Height = 1, HasPosition = true };
                lights[i] = light;
            }
            return new LightMap(lights, "OpenRGB SDK server: " + controller.Name);
        }

        private static bool NamesLogo(string name)
            => name != null && name.Contains("Logo", StringComparison.OrdinalIgnoreCase);
    }
}
