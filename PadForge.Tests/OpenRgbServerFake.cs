using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;

namespace PadForge.Tests
{
    /// <summary>
    /// Writes what an OpenRGB server sends, field by field in the order the
    /// server's own code writes it at the pinned commit, so the tests read
    /// the protocol from the reference and never from the parser under
    /// test: GetDeviceDescriptionData (RGBController/RGBController.cpp:2519-2706),
    /// GetModeDescriptionData (lines 2944-3078), GetZoneDescriptionData
    /// (lines 3163-3337), GetSegmentDescriptionData (lines 3080-3131),
    /// GetLEDDescriptionData (lines 2802-2825), GetColorDescriptionData
    /// (lines 2477-2500) and SendReply_ControllerData's leading size
    /// (NetworkServer.cpp:3659-3683).
    /// </summary>
    internal static class OpenRgbServerWrites
    {
        internal static void U16(List<byte> to, int value)
        {
            to.Add((byte)value);
            to.Add((byte)(value >> 8));
        }

        internal static void U32(List<byte> to, uint value)
        {
            to.Add((byte)value);
            to.Add((byte)(value >> 8));
            to.Add((byte)(value >> 16));
            to.Add((byte)(value >> 24));
        }

        internal static void Str(List<byte> to, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            U16(to, bytes.Length + 1);
            to.AddRange(bytes);
            to.Add(0);
        }

        /// <summary>One mode, GetModeDescriptionData.</summary>
        internal static void Mode(List<byte> to, OpenRgbMode mode, uint version)
        {
            Str(to, mode.Name);
            if (version < 6) U32(to, (uint)mode.Value);
            U32(to, mode.Flags);
            U32(to, mode.SpeedMin);
            U32(to, mode.SpeedMax);
            if (version >= 3)
            {
                U32(to, mode.BrightnessMin);
                U32(to, mode.BrightnessMax);
            }
            U32(to, mode.ColorsMin);
            U32(to, mode.ColorsMax);
            U32(to, mode.Speed);
            if (version >= 3) U32(to, mode.Brightness);
            U32(to, mode.Direction);
            U32(to, mode.ColorMode);
            U16(to, mode.Colors.Length);
            foreach (uint c in mode.Colors) U32(to, c);
        }

        /// <summary>One zone, GetZoneDescriptionData, with
        /// <paramref name="segments"/> segments of its own name.</summary>
        internal static void Zone(List<byte> to, OpenRgbZone zone, uint version, int segments = 0)
        {
            Str(to, zone.Name);
            U32(to, (uint)zone.Type);
            U32(to, zone.LedsMin);
            U32(to, zone.LedsMax);
            U32(to, zone.LedsCount);
            if (zone.Matrix.Length == 0) U16(to, 0);
            else
            {
                U16(to, 8 + 4 * zone.Matrix.Length);
                U32(to, zone.MatrixHeight);
                U32(to, zone.MatrixWidth);
                foreach (uint cell in zone.Matrix) U32(to, cell);
            }
            if (version >= 4)
            {
                U16(to, segments);
                for (int i = 0; i < segments; i++)
                {
                    Str(to, zone.Name + " segment " + i);
                    U32(to, 1);
                    U32(to, (uint)i);
                    U32(to, 1);
                }
            }
            if (version >= 5) U32(to, zone.Flags);
        }

        /// <summary>The reply to a controller data request: the whole
        /// reply's size, then the device description.</summary>
        internal static byte[] Controller(OpenRgbController c, uint version, int segmentsPerZone = 0,
            string[] displayNames = null)
        {
            var body = new List<byte>();
            U32(body, (uint)c.Type);
            Str(body, c.Name);
            if (version >= 1) Str(body, c.Vendor);
            Str(body, c.Description);
            Str(body, c.Version);
            Str(body, c.Serial);
            Str(body, c.Location);
            U16(body, c.Modes.Count);
            U32(body, (uint)c.ActiveMode);
            foreach (var mode in c.Modes) Mode(body, mode, version);
            U16(body, c.Zones.Count);
            foreach (var zone in c.Zones) Zone(body, zone, version, segmentsPerZone);
            U16(body, c.Leds.Count);
            for (int i = 0; i < c.Leds.Count; i++)
            {
                Str(body, c.Leds[i]);
                if (version < 6) U32(body, (uint)(0x100 + i));
            }
            U16(body, c.Colors.Length);
            foreach (uint color in c.Colors) U32(body, color);
            if (version >= 5)
            {
                displayNames ??= Array.Empty<string>();
                U16(body, displayNames.Length);
                foreach (var name in displayNames) Str(body, name);
                U32(body, c.Flags);
            }
            var reply = new List<byte>();
            U32(reply, (uint)(4 + body.Count));
            reply.AddRange(body);
            return reply.ToArray();
        }

        /// <summary>A packet header and its data, InitNetPacketHeader
        /// (NetworkProtocol.h:52-58).</summary>
        internal static byte[] Packet(uint device, uint id, byte[] data)
        {
            var to = new List<byte> { (byte)'O', (byte)'R', (byte)'G', (byte)'B' };
            U32(to, device);
            U32(to, id);
            U32(to, (uint)data.Length);
            to.AddRange(data);
            return to.ToArray();
        }

        internal static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>An OpenRGB server of protocol <see cref="Version"/> as the
    /// pinned server answers a client of protocol 5 or below: a version
    /// reply, the count alone, each controller at the version its request
    /// names, and nothing for any other packet (NetworkServer.cpp:1105-1126,
    /// 1331-1338, 3562-3570, 3586-3635, 3637-3717).</summary>
    internal sealed class FakeOpenRgbServer
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _received = new();

        public uint Version = 6;
        public bool AnswersVersion = true;
        public List<OpenRgbController> Controllers = new();
        public int Connections;
        public string ClientName;

        /// <summary>Set: every link made from now on refuses, and every open
        /// one is closed.</summary>
        public volatile bool Down;

        /// <summary>The version the last client asked for.</summary>
        public uint ClientVersion;

        public FakeOpenRgbLink Link()
        {
            lock (_gate) Connections++;
            return new FakeOpenRgbLink(this);
        }

        public void Received(byte[] packet)
        {
            lock (_gate) _received.Add(packet);
        }

        /// <summary>Every packet the clients sent, in order.</summary>
        public List<byte[]> Packets()
        {
            lock (_gate) return _received.ToList();
        }

        /// <summary>The packets with ID <paramref name="id"/>.</summary>
        public List<byte[]> Packets(uint id)
            => Packets().Where(p => BitConverter.ToUInt32(p, 8) == id).ToList();

        public void Clear()
        {
            lock (_gate) _received.Clear();
        }
    }

    internal sealed class FakeOpenRgbLink : IOpenRgbLink
    {
        private readonly FakeOpenRgbServer _server;
        private readonly Queue<OpenRgbPacket> _replies = new();
        private readonly object _gate = new();
        public bool Disposed;
        private uint _version;

        public FakeOpenRgbLink(FakeOpenRgbServer server) => _server = server;

        /// <summary>Puts a device list changed notice in the reply
        /// stream.</summary>
        public void Notice()
        {
            lock (_gate) _replies.Enqueue(new OpenRgbPacket(0, OpenRgbProtocol.DeviceListUpdated, Array.Empty<byte>()));
        }

        public bool Send(byte[] packet)
        {
            if (Disposed || _server.Down) return false;
            _server.Received(packet);
            uint device = BitConverter.ToUInt32(packet, 4);
            uint id = BitConverter.ToUInt32(packet, 8);
            uint size = BitConverter.ToUInt32(packet, 12);
            var data = packet.Skip(16).Take((int)size).ToArray();
            lock (_gate)
            {
                switch (id)
                {
                    case OpenRgbProtocol.RequestProtocolVersion:
                        _server.ClientVersion = BitConverter.ToUInt32(data, 0);
                        _version = Math.Min(_server.ClientVersion, _server.Version);
                        if (_server.AnswersVersion)
                            _replies.Enqueue(new OpenRgbPacket(0, id, BitConverter.GetBytes(_server.Version)));
                        break;
                    case OpenRgbProtocol.SetClientName:
                        _server.ClientName = Encoding.UTF8.GetString(data, 0, Math.Max(0, data.Length - 1));
                        break;
                    case OpenRgbProtocol.RequestControllerCount:
                        _replies.Enqueue(new OpenRgbPacket(0, id, BitConverter.GetBytes((uint)_server.Controllers.Count)));
                        break;
                    case OpenRgbProtocol.RequestControllerData:
                        uint asked = data.Length == 4 ? BitConverter.ToUInt32(data, 0) : 0;
                        if (device < _server.Controllers.Count)
                            _replies.Enqueue(new OpenRgbPacket(device, id,
                                OpenRgbServerWrites.Controller(_server.Controllers[(int)device], asked)));
                        break;
                }
            }
            return true;
        }

        public OpenRgbReceive Receive(int timeoutMs, out OpenRgbPacket packet)
        {
            packet = null;
            if (Disposed || _server.Down) return OpenRgbReceive.Closed;
            lock (_gate)
            {
                if (_replies.Count == 0) return OpenRgbReceive.Timeout;
                packet = _replies.Dequeue();
                return OpenRgbReceive.Packet;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
