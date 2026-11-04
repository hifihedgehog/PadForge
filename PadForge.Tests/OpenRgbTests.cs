using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using Xunit;
using static PadForge.Tests.OpenRgbServerWrites;

namespace PadForge.Tests
{
    /// <summary>The controllers the OpenRGB tests stand in for (#557).</summary>
    internal static class OpenRgbSamples
    {
        internal const string KeyboardPath = @"\\?\hid#vid_1532&pid_024e&mi_02#7&2a1b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        internal const string ReceiverPath = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#8&1d2e3f&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        /// <summary>A Direct mode as OpenRGB's controllers declare one:
        /// per-LED colors, MODE_FLAG_HAS_PER_LED_COLOR (1 &lt;&lt; 5,
        /// RGBControllerInterface.h:69) and MODE_COLORS_PER_LED (line 102).</summary>
        internal static OpenRgbMode Direct(int value = 0xFFFF)
            => new("Direct", value, 1u << 5, 0, 0, 0, 100, 0, 0, 0, 100, 0, 1, Array.Empty<uint>());

        /// <summary>A Static mode with one mode-specific color,
        /// MODE_FLAG_HAS_MODE_SPECIFIC_COLOR (1 &lt;&lt; 6) and
        /// MODE_COLORS_MODE_SPECIFIC (2).</summary>
        internal static OpenRgbMode Static(uint color = 0x0000FF)
            => new("Static", 1, 1u << 6, 0, 0, 0, 100, 1, 1, 0, 80, 0, 2, new[] { color });

        /// <summary>A Breathing mode with speed (1 &lt;&lt; 0) and random
        /// colors (1 &lt;&lt; 7), colored at random (3).</summary>
        internal static OpenRgbMode Breathing()
            => new("Breathing", 2, (1u << 0) | (1u << 7), 1, 5, 0, 100, 0, 0, 3, 100, 0, 3, Array.Empty<uint>());

        /// <summary>A keyboard with a 2 by 3 matrix of five keys, a logo and
        /// an underglow of two, in Static mode.</summary>
        internal static OpenRgbController Keyboard(string path = KeyboardPath, int active = 1) => new()
        {
            Type = OpenRgbProtocol.DeviceKeyboard,
            Name = "Razer BlackWidow V3",
            Vendor = "Razer",
            Description = "Razer Keyboard Device",
            Version = "1.02",
            Serial = "PM2038H01234567",
            Location = "HID: " + path,
            ActiveMode = active,
            Modes = new[] { Direct(), Static(), Breathing() },
            Zones = new[]
            {
                new OpenRgbZone("Keyboard", OpenRgbProtocol.ZoneMatrix, 5, 5, 5, 2, 3,
                    new uint[] { 0, 1, OpenRgbProtocol.NoLed, 2, 3, 4 }, 0),
                new OpenRgbZone("Logo", OpenRgbProtocol.ZoneSingle, 1, 1, 1, 0, 0, Array.Empty<uint>(), 0),
                new OpenRgbZone("Underglow", OpenRgbProtocol.ZoneLinear, 2, 2, 2, 0, 0, Array.Empty<uint>(), 0),
            },
            Leds = new[] { "Key: Escape", "Key: F1", "Key: A", "Key: Media Mute", "Key: Macro 1", "Logo", "Underglow LED 1", "Underglow LED 2" },
            Colors = new uint[] { 0x0000FF, 0x0000FF, 0x0000FF, 0x0000FF, 0x0000FF, 0x00FF00, 0xFF0000, 0xFF0000 },
            Flags = 0,
        };

        /// <summary>A mouse behind a LIGHTSPEED receiver, in Direct mode.</summary>
        internal static OpenRgbController Mouse() => new()
        {
            Type = OpenRgbProtocol.DeviceMouse,
            Name = "Logitech G502 LIGHTSPEED",
            Vendor = "Logitech",
            Location = "HID: " + ReceiverPath + " (Receiver) \r\nWireless Index: 1",
            ActiveMode = 0,
            Modes = new[] { Direct(), Static() },
            Zones = new[]
            {
                new OpenRgbZone("Logo", OpenRgbProtocol.ZoneSingle, 1, 1, 1, 0, 0, Array.Empty<uint>(), 0),
                new OpenRgbZone("Scroll Wheel", OpenRgbProtocol.ZoneSingle, 1, 1, 1, 0, 0, Array.Empty<uint>(), 0),
            },
            Leds = new[] { "Logo", "Scroll Wheel" },
            Colors = new uint[] { 0x112233, 0x445566 },
        };

        /// <summary>A mousepad with no Direct mode.</summary>
        internal static OpenRgbController Mousepad() => new()
        {
            Type = 7,
            Name = "SteelSeries QcK Prism",
            Vendor = "SteelSeries",
            Location = @"HID: \\?\hid#vid_1038&pid_150a#6&aa&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            ActiveMode = 0,
            Modes = new[] { Static() },
            Zones = new[] { new OpenRgbZone("Pad", OpenRgbProtocol.ZoneLinear, 2, 2, 2, 0, 0, Array.Empty<uint>(), 0) },
            Leds = new[] { "Top", "Bottom" },
            Colors = new uint[] { 0, 0 },
        };
    }

    /// <summary>
    /// The packets PadForge sends an OpenRGB SDK server and the replies it
    /// reads (#557), each written out from the server's own code at the
    /// pinned commit (<see cref="OpenRgbServerWrites"/>), never from the
    /// builder under test.
    /// </summary>
    public class OpenRgbProtocolTests
    {
        /// <summary>NetworkClient.cpp:2686-2699: the header, packet ID 40
        /// (NetworkProtocol.h:112), and the client's version as its data.</summary>
        [Fact]
        public void TheVersionRequest_NamesVersion5()
            => Assert.Equal("4F 52 47 42 00 00 00 00 28 00 00 00 04 00 00 00 05 00 00 00",
                Hex(OpenRgbProtocol.ProtocolVersionRequest(OpenRgbProtocol.ClientVersion)));

        /// <summary>NetworkClient.cpp:2662-2673: packet ID 50 and the name
        /// with its terminating zero.</summary>
        [Fact]
        public void TheClientName_EndsWithAZero()
            => Assert.Equal("4F 52 47 42 00 00 00 00 32 00 00 00 09 00 00 00 50 61 64 46 6F 72 67 65 00",
                Hex(OpenRgbProtocol.ClientName("PadForge")));

        [Fact]
        public void TheCountRequest_HasNoData()
            => Assert.Equal("4F 52 47 42 00 00 00 00 00 00 00 00 00 00 00 00",
                Hex(OpenRgbProtocol.ControllerCountRequest()));

        /// <summary>NetworkClient.cpp:585-616: the controller's index in the
        /// header, and the version as data except at version 0.</summary>
        [Fact]
        public void TheControllerRequest_CarriesTheVersion_ExceptAtVersion0()
        {
            Assert.Equal("4F 52 47 42 03 00 00 00 01 00 00 00 04 00 00 00 05 00 00 00",
                Hex(OpenRgbProtocol.ControllerDataRequest(3, 5)));
            Assert.Equal("4F 52 47 42 03 00 00 00 01 00 00 00 00 00 00 00",
                Hex(OpenRgbProtocol.ControllerDataRequest(3, 0)));
        }

        /// <summary>RGBController_Network.cpp:522-561 and
        /// RGBController.cpp:2477-2500: the data's size counting itself,
        /// the number of colors, then each color with red in its low byte
        /// (RGBControllerInterface.h:29). Packet ID 1050 is 0x041A.</summary>
        [Fact]
        public void UpdateLeds_IsTheSizeTheCountAndEachColor()
        {
            var colors = new[] { OpenRgbProtocol.ToRgbColor(0x112233), OpenRgbProtocol.ToRgbColor(0xAABBCC) };
            Assert.Equal("4F 52 47 42 02 00 00 00 1A 04 00 00 0E 00 00 00 "
                         + "0E 00 00 00 02 00 11 22 33 00 AA BB CC 00",
                Hex(OpenRgbProtocol.UpdateLedsRequest(2, colors)));
        }

        /// <summary>RGBController_Network.cpp:565-613: the data's size, the
        /// mode's index, then the mode as GetModeDescriptionData writes it
        /// at the version. Packet ID 1101 is 0x044D.</summary>
        [Theory]
        [InlineData(5u)]
        [InlineData(3u)]
        [InlineData(2u)]
        [InlineData(0u)]
        public void UpdateMode_IsTheSizeTheIndexAndTheModeAtTheVersion(uint version)
        {
            var mode = OpenRgbSamples.Static(0x123456);
            var body = new List<byte>();
            Mode(body, mode, version);
            var data = new List<byte>();
            U32(data, (uint)(8 + body.Count));
            U32(data, 1);
            data.AddRange(body);
            Assert.Equal(Hex(Packet(4, 1101, data.ToArray())), Hex(OpenRgbProtocol.UpdateModeRequest(4, 1, mode, version)));
        }

        /// <summary>At version 5 the mode's value goes back as it came, and
        /// brightness is carried from version 3 on (RGBController.cpp:2970-3039).</summary>
        [Fact]
        public void UpdateMode_SendsTheValueBack_AndBrightnessFromVersion3()
        {
            var mode = OpenRgbSamples.Direct(0x2A);
            byte[] five = OpenRgbProtocol.UpdateModeRequest(0, 0, mode, 5);
            byte[] two = OpenRgbProtocol.UpdateModeRequest(0, 0, mode, 2);
            Assert.Equal(two.Length + 12, five.Length);
            // After the header, the size and the index: the name "Direct"
            // and its zero, then the value.
            Assert.Equal("07 00 44 69 72 65 63 74 00 2A 00 00 00", Hex(five.Skip(24).Take(13)));
        }

        [Fact]
        public void Colors_CarryRedInTheLowByte()
        {
            Assert.Equal(0x00332211u, OpenRgbProtocol.ToRgbColor(0x112233));
            Assert.Equal(0x112233, OpenRgbProtocol.FromRgbColor(0x00332211));
            Assert.Equal(0x0000FFu, OpenRgbProtocol.ToRgbColor(0xFF0000));
            Assert.Equal(0xFF0000u, OpenRgbProtocol.ToRgbColor(0x0000FF));
            Assert.Equal(0xFFFFFFu, OpenRgbProtocol.ToRgbColor(unchecked((int)0xFFFFFFFF)));
        }

        [Fact]
        public void AHeader_IsReadOnlyWithItsMagic()
        {
            var packet = Packet(7, 1, new byte[] { 1, 2 });
            Assert.True(OpenRgbProtocol.TryReadHeader(packet, out uint device, out uint id, out uint size));
            Assert.Equal((7u, 1u, 2u), (device, id, size));
            packet[3] = (byte)'C';
            Assert.False(OpenRgbProtocol.TryReadHeader(packet, out _, out _, out _));
            Assert.False(OpenRgbProtocol.TryReadHeader(new byte[15], out _, out _, out _));
        }

        /// <summary>The description at each version, with segments where
        /// they are carried.</summary>
        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(2u)]
        [InlineData(3u)]
        [InlineData(4u)]
        [InlineData(5u)]
        public void AController_IsReadAtTheVersionItWasAskedAt(uint version)
        {
            var sent = OpenRgbSamples.Keyboard();
            var flagged = new OpenRgbController
            {
                Type = sent.Type, Name = sent.Name, Vendor = sent.Vendor, Description = sent.Description,
                Version = sent.Version, Serial = sent.Serial, Location = sent.Location, ActiveMode = sent.ActiveMode,
                Modes = sent.Modes, Leds = sent.Leds, Colors = sent.Colors, Flags = 0x30,
                Zones = sent.Zones.Select(z => z with { Flags = 0x1000000 }).ToArray(),
            };
            var read = OpenRgbProtocol.ReadController(Controller(flagged, version, segmentsPerZone: 2,
                displayNames: new[] { "Esc" }), version);
            Assert.NotNull(read);
            Assert.Equal(sent.Type, read.Type);
            Assert.Equal(sent.Name, read.Name);
            Assert.Equal(version >= 1 ? "Razer" : string.Empty, read.Vendor);
            Assert.Equal(sent.Location, read.Location);
            Assert.Equal(sent.Serial, read.Serial);
            Assert.Equal(1, read.ActiveMode);
            Assert.Equal(3, read.Modes.Count);
            var stat = read.Modes[1];
            Assert.Equal("Static", stat.Name);
            Assert.Equal(1, stat.Value);
            Assert.Equal(2u, stat.ColorMode);
            Assert.Equal(new uint[] { 0x0000FF }, stat.Colors);
            Assert.Equal(version >= 3 ? 80u : 0u, stat.Brightness);
            Assert.Equal(version >= 3 ? 100u : 0u, stat.BrightnessMax);
            Assert.Equal(3, read.Zones.Count);
            var matrix = read.Zones[0];
            Assert.Equal((2u, 3u), (matrix.MatrixHeight, matrix.MatrixWidth));
            Assert.Equal(new uint[] { 0, 1, OpenRgbProtocol.NoLed, 2, 3, 4 }, matrix.Matrix);
            Assert.Equal(version >= 5 ? 0x1000000u : 0u, matrix.Flags);
            Assert.Equal(sent.Leds, read.Leds);
            Assert.Equal(sent.Colors, read.Colors);
            Assert.Equal(version >= 5 ? 0x30u : 0u, read.Flags);
        }

        /// <summary>A reply cut short anywhere reads as no controller.</summary>
        [Fact]
        public void AReplyCutShort_IsNoController()
        {
            byte[] whole = Controller(OpenRgbSamples.Keyboard(), 5, segmentsPerZone: 1);
            Assert.NotNull(OpenRgbProtocol.ReadController(whole, 5));
            for (int length = 0; length < whole.Length; length++)
                Assert.Null(OpenRgbProtocol.ReadController(whole.AsSpan(0, length), 5));
        }

        /// <summary>The matrix's byte length must be its height times its
        /// width cells and the two sizes (RGBController.cpp:2879-2897).</summary>
        [Fact]
        public void AMatrixWhoseLengthDisagreesWithItsSize_IsNoController()
        {
            byte[] whole = Controller(OpenRgbSamples.Keyboard(), 5);
            // The matrix length field of the first zone: after the reply's
            // size, the type, five strings and the modes.
            var body = new List<byte>();
            U32(body, 0);
            U32(body, 5);
            foreach (var s in new[] { "Razer BlackWidow V3", "Razer", "Razer Keyboard Device", "1.02", "PM2038H01234567", "HID: " + OpenRgbSamples.KeyboardPath })
                Str(body, s);
            U16(body, 3);
            U32(body, 1);
            foreach (var mode in OpenRgbSamples.Keyboard().Modes) Mode(body, mode, 5);
            U16(body, 3);
            Str(body, "Keyboard");
            int at = body.Count + 16;
            Assert.Equal(8 + 4 * 6, BitConverter.ToUInt16(whole, at));
            whole[at] += 4;
            Assert.Null(OpenRgbProtocol.ReadController(whole, 5));
        }

        [Fact]
        public void TheCountAndVersionReplies_AreOneNumber()
        {
            Assert.Equal(4, OpenRgbProtocol.ReadCount(new byte[] { 4, 0, 0, 0 }));
            Assert.Equal(-1, OpenRgbProtocol.ReadCount(new byte[] { 4, 0, 0 }));
            Assert.Equal(6L, OpenRgbProtocol.ReadVersion(new byte[] { 6, 0, 0, 0 }));
            Assert.Equal(-1L, OpenRgbProtocol.ReadVersion(Array.Empty<byte>()));
        }
    }

    /// <summary>What PadForge reads from a controller's description:
    /// the device its location names, its Direct mode and its lights.</summary>
    public class OpenRgbDevicesTests
    {
        [Fact]
        public void AHidLocation_GivesThePath_AndAReceiversItsPathAlone()
        {
            Assert.Equal(OpenRgbSamples.KeyboardPath, OpenRgbDevices.HidPath(OpenRgbSamples.Keyboard().Location));
            Assert.Equal(OpenRgbSamples.ReceiverPath, OpenRgbDevices.HidPath(OpenRgbSamples.Mouse().Location));
            Assert.True(OpenRgbDevices.OnReceiver(OpenRgbSamples.Mouse().Location));
            Assert.False(OpenRgbDevices.OnReceiver(OpenRgbSamples.Keyboard().Location));
            // A family that gives the path alone, the Cooler Master
            // keyboards' (CMKeyboardAbstractController.cpp:21 and 73-76).
            Assert.Equal(OpenRgbSamples.KeyboardPath, OpenRgbDevices.HidPath(OpenRgbSamples.KeyboardPath));
            Assert.Null(OpenRgbDevices.HidPath("USB: 1-4"));
            Assert.Null(OpenRgbDevices.HidPath("IP: 192.168.1.20"));
            Assert.Null(OpenRgbDevices.HidPath("HID: "));
            Assert.Null(OpenRgbDevices.HidPath(@"\\.\ATKACPI"));
            Assert.Null(OpenRgbDevices.HidPath(null));
        }

        /// <summary>The ASUS TUF and HP Omen locations
        /// (RGBController_AsusTUFLaptop_Windows.cpp:31 and
        /// RGBController_HPOmenLaptopWMI_Windows.cpp:33, written out from
        /// their C++ strings), and a serial device's "COM: " and port
        /// (DygmaRaiseController.cpp:51-54).</summary>
        [Fact]
        public void ALaptopsOwnKeyboard_AndASerialDevice_NameTheSharedWorkersKey()
        {
            Assert.True(OpenRgbDevices.BuiltInKeyboard(@"\\.\ATKACPI"));
            Assert.True(OpenRgbDevices.BuiltInKeyboard(@"ROOT\\WMI:hpqBIntM"));
            Assert.False(OpenRgbDevices.BuiltInKeyboard(@"ROOT\WMI:hpqBIntM"));
            Assert.Equal(AsusTufLaptopLighting.Key, OpenRgbDevices.HeldKey(@"\\.\ATKACPI"));
            Assert.Equal(HpOmenLaptopLighting.Key, OpenRgbDevices.HeldKey(@"ROOT\\WMI:hpqBIntM"));
            Assert.Equal(@"\\.\COM5", OpenRgbDevices.HeldKey("COM: COM5"));
            Assert.Null(OpenRgbDevices.HeldKey("COM: "));
            Assert.Null(OpenRgbDevices.HeldKey(OpenRgbSamples.Keyboard().Location));
            Assert.Null(OpenRgbDevices.HeldKey(null));
        }

        [Fact]
        public void APath_NamesItsIds_OnUsbAndBluetooth()
        {
            Assert.Equal(((ushort)0x1532, (ushort)0x024E), OpenRgbDevices.IdsOf(OpenRgbSamples.KeyboardPath));
            Assert.Equal(((ushort)0x046D, (ushort)0xB023), OpenRgbDevices.IdsOf(
                @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002046d_pid&b023&col01#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
            Assert.Equal(((ushort)0, (ushort)0), OpenRgbDevices.IdsOf(@"\\?\hid#acpi#1"));
            Assert.Equal(((ushort)0, (ushort)0), OpenRgbDevices.IdsOf(null));
        }

        /// <summary>RGBControllerInterface.h:181-206: keyboard 5, mouse 6,
        /// keypad 18, laptop 19.</summary>
        [Fact]
        public void TheDeviceType_GivesTheKind()
        {
            Assert.Equal(1, OpenRgbDevices.KindOf(5));
            Assert.Equal(1, OpenRgbDevices.KindOf(18));
            Assert.Equal(1, OpenRgbDevices.KindOf(19));
            Assert.Equal(2, OpenRgbDevices.KindOf(6));
            Assert.Equal(0, OpenRgbDevices.KindOf(7));
            Assert.Equal(0, OpenRgbDevices.KindOf(21));
        }

        /// <summary>RGBController.cpp:2326-2341: an effects-only zone is one
        /// LED in the color buffer.</summary>
        [Fact]
        public void AnEffectsOnlyZone_HoldsOneLed()
        {
            var zone = new OpenRgbZone("Strip", 1, 0, 60, 10, 0, 0, Array.Empty<uint>(), OpenRgbProtocol.ZoneFlagSizeEffectsOnly);
            Assert.Equal(1, OpenRgbDevices.LedsIn(zone));
            Assert.Equal(0, OpenRgbDevices.LedsIn(zone with { LedsCount = 0 }));
            Assert.Equal(10, OpenRgbDevices.LedsIn(zone with { Flags = 0 }));
        }

        [Fact]
        public void TheDirectMode_IsTheOneNamedDirect_WithAColorPerLed()
        {
            Assert.Equal(0, OpenRgbDevices.DirectMode(OpenRgbSamples.Keyboard()));
            Assert.Equal(-1, OpenRgbDevices.DirectMode(OpenRgbSamples.Mousepad()));
            var odd = OpenRgbSamples.Keyboard();
            var modes = new[] { OpenRgbSamples.Static(), OpenRgbSamples.Direct() with { ColorMode = 2 }, OpenRgbSamples.Direct() };
            Assert.Equal(2, OpenRgbDevices.DirectMode(new OpenRgbController { Modes = modes }));
            Assert.Equal(-1, OpenRgbDevices.DirectMode(new OpenRgbController
            {
                Modes = new[] { OpenRgbSamples.Direct() with { Name = "direct" } },
            }));
            Assert.Equal(-1, OpenRgbDevices.DirectMode(null));
            Assert.NotNull(odd);
        }

        /// <summary>One light per LED in buffer order: keys by OpenRGB's key
        /// names, a labeled media key, an unnamed matrix LED as Other, the
        /// logo, and the underglow as zone 1, with each matrix LED at its
        /// cell (qt/DeviceView.cpp:1263-1275).</summary>
        [Fact]
        public void AKeyboardsMap_HasALightPerLed_ByName_ZoneAndCell()
        {
            var map = OpenRgbDevices.Map(OpenRgbSamples.Keyboard());
            Assert.Equal(8, map.Lights.Count);
            Assert.Equal(Enumerable.Range(0, 8), map.Lights.Select(l => l.Address));
            var esc = map.Lights[0];
            Assert.Equal((LightKind.Key, (ushort)0x29, LightKeySource.Generated), (esc.Kind, esc.Usage, esc.KeySource));
            Assert.Equal((0f, 0f, true), (esc.X, esc.Y, esc.HasPosition));
            Assert.Equal((LightKind.Key, (ushort)0x3A, 1f, 0f), (map.Lights[1].Kind, map.Lights[1].Usage, map.Lights[1].X, map.Lights[1].Y));
            Assert.Equal((LightKind.Key, (ushort)0x04, 0f, 1f), (map.Lights[2].Kind, map.Lights[2].Usage, map.Lights[2].X, map.Lights[2].Y));
            Assert.Equal((LightKind.Other, LightLabel.Mute, 1f, 1f), (map.Lights[3].Kind, map.Lights[3].Label, map.Lights[3].X, map.Lights[3].Y));
            Assert.Equal((LightKind.Other, LightLabel.None, 2f, 1f), (map.Lights[4].Kind, map.Lights[4].Label, map.Lights[4].X, map.Lights[4].Y));
            Assert.Equal(LightKind.Logo, map.Lights[5].Kind);
            Assert.False(map.Lights[5].HasPosition);
            Assert.Equal((LightKind.Zone, 1), (map.Lights[6].Kind, map.Lights[6].Zone));
            Assert.Equal((LightKind.Zone, 1), (map.Lights[7].Kind, map.Lights[7].Zone));
            Assert.Equal(3, map.KeyCount);
            Assert.Equal(0, map.UnnamedKeyCount);
            Assert.True(map.PerLight && map.HasKeys);
        }

        [Fact]
        public void AMousesMap_HasItsLogo_AndItsOtherZonesNumbered()
        {
            var map = OpenRgbDevices.Map(OpenRgbSamples.Mouse());
            Assert.Equal(LightKind.Logo, map.Lights[0].Kind);
            Assert.Equal((LightKind.Zone, 1), (map.Lights[1].Kind, map.Lights[1].Zone));
            Assert.False(map.HasKeys);
        }

        /// <summary>A zone's LEDs start where the zones before it end in the
        /// color buffer (RGBController.cpp:2343-2390), an effects-only zone
        /// counting one.</summary>
        [Fact]
        public void AZonesLeds_StartWhereTheZonesBeforeItEnd()
        {
            var controller = new OpenRgbController
            {
                Type = 4,
                Name = "Strip",
                Zones = new[]
                {
                    new OpenRgbZone("Effects", 1, 0, 60, 30, 0, 0, Array.Empty<uint>(), OpenRgbProtocol.ZoneFlagSizeEffectsOnly),
                    new OpenRgbZone("Case", OpenRgbProtocol.ZoneMatrix, 2, 2, 2, 1, 2, new uint[] { 1, 0 }, 0),
                },
                Leds = new[] { "Effects LED", "Case LED 1", "Case LED 2" },
                Colors = new uint[3],
            };
            var map = OpenRgbDevices.Map(controller);
            Assert.Equal(new[] { 1, 2, 2 }, map.Lights.Select(l => l.Zone));
            // The case is a matrix of one row, LED 1 of the zone first.
            Assert.Equal((1f, 0f), (map.Lights[1].X, map.Lights[1].Y));
            Assert.Equal((0f, 0f), (map.Lights[2].X, map.Lights[2].Y));
            Assert.False(map.Lights[0].HasPosition);
        }
    }

    /// <summary>A conversation with a server, opened as OpenRGB's own
    /// client opens one (NetworkClient.cpp:1759-1855).</summary>
    public class OpenRgbSessionTests
    {
        [Fact]
        public void ASession_AsksForVersion5_ThenSaysItsName_AndSpeaksTheLowerVersion()
        {
            var server = new FakeOpenRgbServer { Version = 6 };
            var session = OpenRgbSession.Begin(server.Link(), 50, 50);
            Assert.NotNull(session);
            Assert.Equal(5u, session.Version);
            Assert.Equal(5u, server.ClientVersion);
            Assert.Equal("PadForge", server.ClientName);
            var ids = server.Packets().Select(p => BitConverter.ToUInt32(p, 8)).ToArray();
            Assert.Equal(new uint[] { 40, 50 }, ids);

            server = new FakeOpenRgbServer { Version = 4 };
            Assert.Equal(4u, OpenRgbSession.Begin(server.Link(), 50, 50).Version);
        }

        /// <summary>No reply inside the wait: the server speaks version 0
        /// (NetworkClient.cpp:1786-1803).</summary>
        [Fact]
        public void AServerThatDoesNotAnswerTheVersion_SpeaksVersion0()
        {
            var server = new FakeOpenRgbServer { AnswersVersion = false };
            var session = OpenRgbSession.Begin(server.Link(), 20, 20);
            Assert.Equal(0u, session.Version);
            server.Controllers.Add(OpenRgbSamples.Keyboard());
            var list = session.ReadControllers();
            Assert.Equal("Razer BlackWidow V3", Assert.Single(list).Name);
            Assert.Equal(string.Empty, list[0].Vendor);
            // The controller request carries no version at version 0.
            Assert.Equal(16, server.Packets(OpenRgbProtocol.RequestControllerData).Single().Length);
        }

        [Fact]
        public void AClosedConnection_OpensNoSession()
        {
            var server = new FakeOpenRgbServer { Down = true };
            var link = server.Link();
            Assert.Null(OpenRgbSession.Begin(link, 20, 20));
            Assert.True(link.Disposed);
            Assert.Null(OpenRgbSession.Begin(null));
        }

        [Fact]
        public void TheList_IsEveryControllerByIndex_AtTheNegotiatedVersion()
        {
            var server = new FakeOpenRgbServer();
            server.Controllers.AddRange(new[] { OpenRgbSamples.Keyboard(), OpenRgbSamples.Mouse() });
            var session = OpenRgbSession.Begin(server.Link(), 50, 50);
            var list = session.ReadControllers();
            Assert.Equal(new[] { "Razer BlackWidow V3", "Logitech G502 LIGHTSPEED" }, list.Select(c => c.Name));
            var requests = server.Packets(OpenRgbProtocol.RequestControllerData);
            Assert.Equal(new[] { "4F 52 47 42 00 00 00 00 01 00 00 00 04 00 00 00 05 00 00 00",
                    "4F 52 47 42 01 00 00 00 01 00 00 00 04 00 00 00 05 00 00 00" },
                requests.Select(p => Hex(p)));
            Assert.False(session.ListChanged);
        }

        /// <summary>A device list changed notice that comes while a reply is
        /// waited for, or between two, is noted for the next read
        /// (Documentation/OpenRGBSDK.md:115-117).</summary>
        [Fact]
        public void ADeviceListNotice_IsNotedWhereverItComes()
        {
            var server = new FakeOpenRgbServer();
            server.Controllers.Add(OpenRgbSamples.Keyboard());
            var link = server.Link();
            var session = OpenRgbSession.Begin(link, 50, 50);
            Assert.True(session.Drain());
            Assert.False(session.ListChanged);
            link.Notice();
            Assert.True(session.Drain());
            Assert.True(session.ListChanged);

            // A read clears it, and a notice ahead of a reply sets it again.
            link.Notice();
            var list = session.ReadControllers();
            Assert.Single(list);
            Assert.True(session.ListChanged);
            Assert.Single(session.ReadControllers());
            Assert.False(session.ListChanged);

            server.Down = true;
            Assert.False(session.Drain());
            Assert.Null(session.ReadControllers());
        }
    }
}
