using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// One mode of an OpenRGB controller as the server describes it
    /// (OpenRGB RGBController/RGBController.cpp:2944-3078,
    /// GetModeDescriptionData), kept whole so it can be sent back unchanged.
    /// <see cref="Value"/> is the internal value a server of protocol 5 or
    /// below sends and wants back (Documentation/OpenRGBSDK.md:214).
    /// Brightness is 0 below protocol 3, which does not carry it.
    /// </summary>
    internal sealed record OpenRgbMode(string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax,
        uint BrightnessMin, uint BrightnessMax, uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness,
        uint Direction, uint ColorMode, uint[] Colors);

    /// <summary>One zone of an OpenRGB controller
    /// (RGBController.cpp:3163-3337, GetZoneDescriptionData). The matrix
    /// holds, row by row, the zone's own index of the LED at each cell, or
    /// <see cref="OpenRgbProtocol.NoLed"/>. <see cref="Flags"/> is 0 below
    /// protocol 5.</summary>
    internal sealed record OpenRgbZone(string Name, int Type, uint LedsMin, uint LedsMax, uint LedsCount,
        uint MatrixHeight, uint MatrixWidth, uint[] Matrix, uint Flags);

    /// <summary>One controller of an OpenRGB server, as the reply to a
    /// controller data request describes it (RGBController.cpp:2519-2706,
    /// GetDeviceDescriptionData). <see cref="Colors"/> are the LEDs' colors
    /// as the server holds them, 0x00BBGGRR each.</summary>
    internal sealed class OpenRgbController
    {
        public int Type { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Vendor { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Serial { get; init; } = string.Empty;
        public string Location { get; init; } = string.Empty;
        public int ActiveMode { get; init; }
        public IReadOnlyList<OpenRgbMode> Modes { get; init; } = Array.Empty<OpenRgbMode>();
        public IReadOnlyList<OpenRgbZone> Zones { get; init; } = Array.Empty<OpenRgbZone>();
        public IReadOnlyList<string> Leds { get; init; } = Array.Empty<string>();
        public uint[] Colors { get; init; } = Array.Empty<uint>();
        public uint Flags { get; init; }
    }

    /// <summary>
    /// OpenRGB's SDK protocol as the server at the pinned commit serves it
    /// to a client of protocol 5 or below: the packets PadForge sends, built
    /// byte for byte, and the replies it reads (#557). Every packet is a
    /// 16-byte header, "ORGB", the device ID, the packet ID and the data's
    /// length, each a 32-bit integer in the server's own byte order
    /// (OpenRGB NetworkProtocol.h:52-58), which is little-endian on every
    /// PC PadForge runs on. Pure, so every byte is testable without a
    /// server.
    ///
    /// <para>The highest version PadForge asks for is 5, the last released
    /// one (Documentation/OpenRGBSDK.md:9-19, where 6 is marked unreleased,
    /// and NetworkProtocol.h:24-26). The server stores the lower of the
    /// two (NetworkServer.cpp:1670-1691), and below 6 it sends no
    /// acknowledgements (NetworkServer.cpp:3562-3570), no server name or
    /// flags (lines 3719-3790), and addresses each controller by its index
    /// in the list (lines 4444-4480). The only packet it sends unasked is
    /// the device list changed notice (lines 4017-4026).</para>
    /// </summary>
    internal static class OpenRgbProtocol
    {
        /// <summary>The server's default port, "ORGB" on a telephone keypad
        /// (NetworkProtocol.h:40-44).</summary>
        internal const int DefaultPort = 6742;

        /// <summary>The highest protocol version PadForge speaks.</summary>
        internal const uint ClientVersion = 5;

        internal const int HeaderLength = 16;

        /// <summary>The largest packet the server accepts
        /// (NetworkProtocol.h:36-38), the bound on a reply PadForge
        /// reads.</summary>
        internal const uint MaxPacket = 1024 * 1024 * 8;

        /// <summary>The packet IDs PadForge uses (NetworkProtocol.h:107-195).</summary>
        internal const uint RequestControllerCount = 0;
        internal const uint RequestControllerData = 1;
        internal const uint RequestProtocolVersion = 40;
        internal const uint SetClientName = 50;
        internal const uint DeviceListUpdated = 100;
        internal const uint UpdateLeds = 1050;
        internal const uint UpdateMode = 1101;

        /// <summary>A mode's colors are each LED's own
        /// (RGBControllerInterface.h:102, MODE_COLORS_PER_LED).</summary>
        internal const uint ModeColorsPerLed = 1;

        /// <summary>A matrix cell with no LED (qt/DeviceView.cpp:1270-1272).</summary>
        internal const uint NoLed = 0xFFFFFFFF;

        /// <summary>A zone whose size is set by hand and is one LED to
        /// per-LED modes (RGBControllerInterface.h:128-130).</summary>
        internal const uint ZoneFlagSizeEffectsOnly = 1;

        /// <summary>The zone types (RGBControllerInterface.h:153-159).</summary>
        internal const int ZoneSingle = 0;
        internal const int ZoneLinear = 1;
        internal const int ZoneMatrix = 2;

        /// <summary>The device types PadForge tells apart
        /// (RGBControllerInterface.h:184-205).</summary>
        internal const int DeviceKeyboard = 5;
        internal const int DeviceMouse = 6;
        internal const int DeviceKeypad = 18;
        internal const int DeviceLaptop = 19;

        private static readonly byte[] Magic = { (byte)'O', (byte)'R', (byte)'G', (byte)'B' };

        /// <summary>One packet: the header and its data
        /// (NetworkProtocol.h:52-58, NetworkProtocol.cpp InitNetPacketHeader).</summary>
        public static byte[] Packet(uint device, uint id, ReadOnlySpan<byte> data)
        {
            var packet = new byte[HeaderLength + data.Length];
            Magic.CopyTo(packet, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), device);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), id);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)data.Length);
            data.CopyTo(packet.AsSpan(HeaderLength));
            return packet;
        }

        /// <summary>Whether a header is one: the magic, then the device ID,
        /// the packet ID and the data's length.</summary>
        public static bool TryReadHeader(ReadOnlySpan<byte> header, out uint device, out uint id, out uint size)
        {
            device = id = size = 0;
            if (header.Length < HeaderLength || !header.Slice(0, 4).SequenceEqual(Magic)) return false;
            device = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
            id = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
            size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
            return true;
        }

        /// <summary>The client's highest version, in a packet of its own
        /// (NetworkClient.cpp:2686-2699, SendRequest_ProtocolVersion).</summary>
        public static byte[] ProtocolVersionRequest(uint version)
        {
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, version);
            return Packet(0, RequestProtocolVersion, data);
        }

        /// <summary>The client's name with its terminating zero, which the
        /// server shows in its client list (NetworkClient.cpp:2662-2673,
        /// SendData_ClientString).</summary>
        public static byte[] ClientName(string name)
        {
            byte[] text = Encoding.UTF8.GetBytes(name ?? string.Empty);
            var data = new byte[text.Length + 1];
            text.CopyTo(data, 0);
            return Packet(0, SetClientName, data);
        }

        /// <summary>The request for the number of controllers, with no data
        /// (NetworkClient.cpp:2675-2684).</summary>
        public static byte[] ControllerCountRequest() => Packet(0, RequestControllerCount, ReadOnlySpan<byte>.Empty);

        /// <summary>The request for one controller's description: the
        /// negotiated version as its data, none at version 0
        /// (NetworkClient.cpp:582-616). The server describes the controller
        /// at the version the request names (NetworkServer.cpp:1105-1126).</summary>
        public static byte[] ControllerDataRequest(uint index, uint version)
        {
            if (version == 0) return Packet(index, RequestControllerData, ReadOnlySpan<byte>.Empty);
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, version);
            return Packet(index, RequestControllerData, data);
        }

        /// <summary>A color of PadForge's, 0xRRGGBB, as OpenRGB holds one:
        /// red in the low byte, then green, then blue
        /// (RGBControllerInterface.h:23-29, ToRGBColor).</summary>
        public static uint ToRgbColor(int rgb)
            => (uint)(((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16));

        /// <summary>An OpenRGB color as PadForge holds one, 0xRRGGBB
        /// (RGBControllerInterface.h:25-27).</summary>
        public static int FromRgbColor(uint color)
            => (int)(((color & 0xFF) << 16) | (color & 0xFF00) | ((color >> 16) & 0xFF));

        /// <summary>UpdateLEDs: the data's size, the number of colors and
        /// each color (RGBController_Network.cpp:522-561,
        /// CreateUpdateLEDsPacket, and Documentation/OpenRGBSDK.md:838-848).
        /// The server refuses a packet whose size field is not the packet's
        /// length (NetworkServer.cpp:3123-3133), and more colors than the
        /// controller has (RGBController.cpp:3654-3660).</summary>
        public static byte[] UpdateLedsRequest(uint index, IReadOnlyList<uint> colors)
        {
            int count = colors?.Count ?? 0;
            var data = new byte[4 + 2 + 4 * count];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)count);
            for (int i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6 + 4 * i), colors[i]);
            return Packet(index, UpdateLeds, data);
        }

        /// <summary>UpdateMode: the data's size, the mode's index and the
        /// mode as the server describes one at the negotiated version
        /// (RGBController_Network.cpp:565-613, CreateUpdateModePacket). The
        /// server sets the mode's values and makes it the active mode only
        /// where they fit the mode it has (NetworkServer.cpp:3163-3268 and
        /// RGBController.cpp:4820-4859), so a mode is sent back as it was
        /// read.</summary>
        public static byte[] UpdateModeRequest(uint index, int modeIndex, OpenRgbMode mode, uint version)
        {
            var body = new List<byte>(64);
            WriteMode(body, mode, version);
            var data = new byte[8 + body.Count];
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), modeIndex);
            body.CopyTo(data, 8);
            return Packet(index, UpdateMode, data);
        }

        /// <summary>One mode at a version (RGBController.cpp:2944-3078).</summary>
        private static void WriteMode(List<byte> to, OpenRgbMode mode, uint version)
        {
            WriteString(to, mode.Name);
            if (version < 6) WriteInt(to, mode.Value);
            WriteUInt(to, mode.Flags);
            WriteUInt(to, mode.SpeedMin);
            WriteUInt(to, mode.SpeedMax);
            if (version >= 3)
            {
                WriteUInt(to, mode.BrightnessMin);
                WriteUInt(to, mode.BrightnessMax);
            }
            WriteUInt(to, mode.ColorsMin);
            WriteUInt(to, mode.ColorsMax);
            WriteUInt(to, mode.Speed);
            if (version >= 3) WriteUInt(to, mode.Brightness);
            WriteUInt(to, mode.Direction);
            WriteUInt(to, mode.ColorMode);
            var colors = mode.Colors ?? Array.Empty<uint>();
            WriteUShort(to, (ushort)colors.Length);
            foreach (uint color in colors) WriteUInt(to, color);
        }

        private static void WriteString(List<byte> to, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            WriteUShort(to, (ushort)(bytes.Length + 1));
            to.AddRange(bytes);
            to.Add(0);
        }

        private static void WriteUShort(List<byte> to, ushort value)
        {
            to.Add((byte)value);
            to.Add((byte)(value >> 8));
        }

        private static void WriteUInt(List<byte> to, uint value)
        {
            to.Add((byte)value);
            to.Add((byte)(value >> 8));
            to.Add((byte)(value >> 16));
            to.Add((byte)(value >> 24));
        }

        private static void WriteInt(List<byte> to, int value) => WriteUInt(to, (uint)value);

        /// <summary>The number of controllers in the reply to a count
        /// request, or -1 for a reply too short. Below version 6 the reply
        /// is the count alone (NetworkServer.cpp:3586-3635).</summary>
        public static int ReadCount(ReadOnlySpan<byte> data)
            => data.Length < 4 ? -1 : (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), int.MaxValue);

        /// <summary>The server's highest version in the reply to a version
        /// request, or -1 for a reply too short
        /// (NetworkServer.cpp:3704-3717).</summary>
        public static long ReadVersion(ReadOnlySpan<byte> data)
            => data.Length < 4 ? -1 : BinaryPrimitives.ReadUInt32LittleEndian(data);

        /// <summary>
        /// A controller from the reply to a controller data request, read at
        /// the version the request named: the reply's size, then the
        /// description (NetworkServer.cpp:3637-3702 and
        /// RGBController.cpp:2519-2706). Null when the reply is cut short or
        /// names more than it holds.
        /// </summary>
        public static OpenRgbController ReadController(ReadOnlySpan<byte> data, uint version)
        {
            var reader = new Reader(data);
            try
            {
                reader.UInt();
                int type = reader.Int();
                string name = reader.String();
                string vendor = version >= 1 ? reader.String() : string.Empty;
                string description = reader.String();
                string fw = reader.String();
                string serial = reader.String();
                string location = reader.String();

                int modeCount = reader.UShort();
                int active = reader.Int();
                var modes = new OpenRgbMode[modeCount];
                for (int i = 0; i < modeCount; i++) modes[i] = ReadMode(ref reader, version);

                int zoneCount = reader.UShort();
                var zones = new OpenRgbZone[zoneCount];
                for (int i = 0; i < zoneCount; i++) zones[i] = ReadZone(ref reader, version);

                int ledCount = reader.UShort();
                var leds = new string[ledCount];
                for (int i = 0; i < ledCount; i++)
                {
                    leds[i] = reader.String();
                    if (version < 6) reader.UInt();
                }

                int colorCount = reader.UShort();
                var colors = new uint[colorCount];
                for (int i = 0; i < colorCount; i++) colors[i] = reader.UInt();

                uint flags = 0;
                if (version >= 5)
                {
                    int names = reader.UShort();
                    for (int i = 0; i < names; i++) reader.String();
                    flags = reader.UInt();
                }

                return new OpenRgbController
                {
                    Type = type, Name = name, Vendor = vendor, Description = description, Version = fw,
                    Serial = serial, Location = location, ActiveMode = active, Modes = modes, Zones = zones,
                    Leds = leds, Colors = colors, Flags = flags,
                };
            }
            catch (ShortReply)
            {
                return null;
            }
        }

        /// <summary>One mode (RGBController.cpp:2944-3078).</summary>
        private static OpenRgbMode ReadMode(ref Reader reader, uint version)
        {
            string name = reader.String();
            int value = version < 6 ? reader.Int() : 0;
            uint flags = reader.UInt();
            uint speedMin = reader.UInt();
            uint speedMax = reader.UInt();
            uint brightnessMin = 0, brightnessMax = 0, brightness = 0;
            if (version >= 3)
            {
                brightnessMin = reader.UInt();
                brightnessMax = reader.UInt();
            }
            uint colorsMin = reader.UInt();
            uint colorsMax = reader.UInt();
            uint speed = reader.UInt();
            if (version >= 3) brightness = reader.UInt();
            uint direction = reader.UInt();
            uint colorMode = reader.UInt();
            int count = reader.UShort();
            var colors = new uint[count];
            for (int i = 0; i < count; i++) colors[i] = reader.UInt();
            return new OpenRgbMode(name, value, flags, speedMin, speedMax, brightnessMin, brightnessMax, colorsMin,
                colorsMax, speed, brightness, direction, colorMode, colors);
        }

        /// <summary>One zone (RGBController.cpp:3163-3337), with its segments
        /// read past (lines 3080-3131): PadForge colors a zone's LEDs one by
        /// one and needs no segment.</summary>
        private static OpenRgbZone ReadZone(ref Reader reader, uint version)
        {
            string name = reader.String();
            int type = reader.Int();
            uint min = reader.UInt();
            uint max = reader.UInt();
            uint count = reader.UInt();
            int matrixLength = reader.UShort();
            uint height = 0, width = 0;
            uint[] matrix = Array.Empty<uint>();
            if (matrixLength > 0)
            {
                height = reader.UInt();
                width = reader.UInt();
                ulong cells = (ulong)height * width;
                if (cells * 4 + 8 != (ulong)matrixLength) throw new ShortReply();
                matrix = new uint[cells];
                for (int i = 0; i < matrix.Length; i++) matrix[i] = reader.UInt();
            }
            if (version >= 4)
            {
                int segments = reader.UShort();
                for (int i = 0; i < segments; i++)
                {
                    reader.String();
                    reader.Int();
                    reader.UInt();
                    reader.UInt();
                }
            }
            uint flags = version >= 5 ? reader.UInt() : 0;
            return new OpenRgbZone(name, type, min, max, count, height, width, matrix, flags);
        }

        private sealed class ShortReply : Exception { }

        /// <summary>Reads fields in the server's byte order, and throws
        /// <see cref="ShortReply"/> past the end.</summary>
        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _data;
            private int _at;

            public Reader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _at = 0;
            }

            private ReadOnlySpan<byte> Take(int count)
            {
                if (count < 0 || _at + count > _data.Length) throw new ShortReply();
                var slice = _data.Slice(_at, count);
                _at += count;
                return slice;
            }

            public ushort UShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            public uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
            public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

            /// <summary>A string: its length with the terminating zero, then
            /// its bytes (RGBController.cpp:2546-2550). The text ends at the
            /// first zero.</summary>
            public string String()
            {
                int length = UShort();
                var bytes = Take(length);
                int end = bytes.IndexOf((byte)0);
                return Encoding.UTF8.GetString(end < 0 ? bytes : bytes.Slice(0, end));
            }
        }
    }
}
