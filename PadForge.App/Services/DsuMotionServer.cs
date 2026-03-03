using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>
    /// Snapshot of a single slot's motion data, ready for DSU transmission.
    /// Units are already converted to DSU conventions:
    ///   Accel: g-force (1g ≈ 9.80665 m/s²)
    ///   Gyro: degrees per second
    /// </summary>
    public struct MotionSnapshot
    {
        public float AccelX, AccelY, AccelZ;
        public float GyroPitch, GyroYaw, GyroRoll;
        public long TimestampUs;
        public bool HasMotion;
    }

    /// <summary>
    /// UDP server implementing the cemuhook DSU (DualShock UDP) protocol.
    /// Streams controller motion data (gyro/accel) to emulators like Cemu,
    /// Dolphin, Yuzu, and Ryujinx on a configurable port (default 26760).
    ///
    /// Protocol spec: https://github.com/v1993/cemuhook-protocol
    /// </summary>
    public sealed class DsuMotionServer : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        private const int MaxSlots = 4;
        private const ushort ProtocolVersion = 1001;
        private const int HeaderSize = 16;

        // Server → Client message types
        private const uint MsgTypeVersion = 0x100000;
        private const uint MsgTypeControllerInfo = 0x100001;
        private const uint MsgTypePadData = 0x100002;

        // Client subscription timeout
        private const int ClientTimeoutMs = 5000;

        // IOControl to suppress ICMP port-unreachable resets on Windows
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _running;
        private uint _serverId;
        private int _port;

        /// <summary>Per-slot packet counter for the DSU protocol.</summary>
        private readonly uint[] _packetCounters = new uint[MaxSlots];

        /// <summary>
        /// Client subscriptions: (endpoint, slot) → last-seen timestamp.
        /// Protected by lock(_subscriptions).
        /// </summary>
        private readonly Dictionary<(EndPoint, int), long> _subscriptions = new();

        /// <summary>
        /// "Subscribe to all slots" clients: endpoint → last-seen timestamp.
        /// </summary>
        private readonly Dictionary<EndPoint, long> _allSlotSubscriptions = new();

        /// <summary>Slot connection states reported to clients.</summary>
        private readonly bool[] _slotConnected = new bool[MaxSlots];

        /// <summary>Slot has-motion states (device has gyro/accel sensors).</summary>
        private readonly bool[] _slotHasMotion = new bool[MaxSlots];

        /// <summary>Raised when server status changes (for UI display).</summary>
        public event EventHandler<string> StatusChanged;

        private bool _disposed;

        // ─────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts the DSU server on the specified port.
        /// </summary>
        public bool Start(int port = 26760)
        {
            if (_running)
                return true;

            _port = port;
            _serverId = (uint)Environment.TickCount;

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                // Suppress ICMP port-unreachable causing SocketException on next receive.
                try { _socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
                catch { /* Non-Windows or older OS — ignore */ }

                _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                _running = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    Name = "PadForge.DsuServer",
                    IsBackground = true
                };
                _receiveThread.Start();

                StatusChanged?.Invoke(this, $"Listening on :{port}");
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _socket?.Dispose();
                _socket = null;
                StatusChanged?.Invoke(this, $"Port {port} in use");
                return false;
            }
            catch (Exception)
            {
                _socket?.Dispose();
                _socket = null;
                StatusChanged?.Invoke(this, "Failed to start");
                return false;
            }
        }

        /// <summary>
        /// Stops the DSU server and closes the socket.
        /// </summary>
        public void Stop()
        {
            if (!_running)
                return;

            _running = false;

            try { _socket?.Close(); }
            catch { /* best effort */ }

            _receiveThread?.Join(2000);
            _receiveThread = null;
            _socket = null;

            lock (_subscriptions)
            {
                _subscriptions.Clear();
                _allSlotSubscriptions.Clear();
            }

            for (int i = 0; i < MaxSlots; i++)
                _packetCounters[i] = 0;

            StatusChanged?.Invoke(this, "Stopped");
        }

        // ─────────────────────────────────────────────
        //  Public API (called from polling thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Updates slot state and broadcasts motion data to subscribed clients.
        /// Called from the InputManager polling thread at ~1000Hz.
        /// </summary>
        public void BroadcastMotion(int slot, MotionSnapshot snapshot, bool connected)
        {
            if (!_running || _socket == null || slot < 0 || slot >= MaxSlots)
                return;

            _slotConnected[slot] = connected;
            _slotHasMotion[slot] = snapshot.HasMotion;

            // Only broadcast if there are subscribers.
            var endpoints = GetSubscribers(slot);
            if (endpoints.Count == 0)
                return;

            // Build pad data packet.
            var packet = BuildPadDataPacket(slot, snapshot, connected);

            foreach (var ep in endpoints)
            {
                try
                {
                    _socket.SendTo(packet, ep);
                }
                catch { /* Client gone — will timeout */ }
            }
        }

        // ─────────────────────────────────────────────
        //  Receive thread
        // ─────────────────────────────────────────────

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[1024];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    int received = _socket.ReceiveFrom(buffer, ref remoteEp);
                    if (received < HeaderSize + 4)
                        continue;

                    ProcessPacket(buffer, received, remoteEp);
                }
                catch (SocketException) when (!_running)
                {
                    break; // Socket closed during shutdown.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    // Malformed packet or transient error — continue.
                }
            }
        }

        private void ProcessPacket(byte[] data, int length, EndPoint sender)
        {
            // Validate magic: "DSUC" (client → server)
            if (data[0] != (byte)'D' || data[1] != (byte)'S' ||
                data[2] != (byte)'U' || data[3] != (byte)'C')
                return;

            // Read protocol version.
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            if (version > ProtocolVersion)
                return;

            // Read payload length.
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
            if (HeaderSize + payloadLength > length)
                return;

            // Verify CRC32.
            uint receivedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            // Zero out the CRC field for verification.
            data[8] = data[9] = data[10] = data[11] = 0;
            uint computedCrc = ComputeCrc32(data, HeaderSize + payloadLength);
            if (receivedCrc != computedCrc)
                return;

            // Read message type (first 4 bytes of payload).
            uint msgType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize));

            switch (msgType)
            {
                case MsgTypeVersion:
                    HandleVersionRequest(sender);
                    break;
                case MsgTypeControllerInfo:
                    HandleControllerInfoRequest(data, length, sender);
                    break;
                case MsgTypePadData:
                    HandlePadDataRequest(data, length, sender);
                    break;
            }
        }

        // ─────────────────────────────────────────────
        //  Message handlers
        // ─────────────────────────────────────────────

        private void HandleVersionRequest(EndPoint sender)
        {
            // Response: header + message type (4) + version (2) + padding (2) = 8 bytes payload
            byte[] packet = new byte[HeaderSize + 8];
            WriteHeader(packet, 8, MsgTypeVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(HeaderSize + 4), ProtocolVersion);
            // bytes [HeaderSize+6..7] are zero padding
            FinalizeCrc(packet);

            try { _socket.SendTo(packet, sender); }
            catch { }
        }

        private void HandleControllerInfoRequest(byte[] data, int length, EndPoint sender)
        {
            // Payload after message type: numPorts (4 bytes) + slot indices (numPorts bytes)
            if (length < HeaderSize + 8)
                return;

            int numPorts = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(HeaderSize + 4));
            if (numPorts < 0 || numPorts > MaxSlots)
                return;
            if (length < HeaderSize + 8 + numPorts)
                return;

            for (int i = 0; i < numPorts; i++)
            {
                int slot = data[HeaderSize + 8 + i];
                if (slot < 0 || slot >= MaxSlots)
                    continue;

                SendControllerInfo(slot, sender);
            }
        }

        private void HandlePadDataRequest(byte[] data, int length, EndPoint sender)
        {
            // Payload after message type: flags (1) + slot (1) + MAC (6)
            if (length < HeaderSize + 12)
                return;

            byte flags = data[HeaderSize + 4];
            byte slot = data[HeaderSize + 5];
            // MAC bytes at [HeaderSize + 6..11] — we ignore MAC-based registration.

            long now = Stopwatch.GetTimestamp();

            lock (_subscriptions)
            {
                if (flags == 0)
                {
                    // flags=0 means subscribe to all pads.
                    _allSlotSubscriptions[sender] = now;
                }
                else
                {
                    if ((flags & 0x01) != 0 && slot < MaxSlots)
                    {
                        // Subscribe to specific slot by ID.
                        _subscriptions[(sender, slot)] = now;
                    }

                    if ((flags & 0x02) != 0)
                    {
                        // Subscribe by MAC (we treat as all-slot).
                        _allSlotSubscriptions[sender] = now;
                    }
                }
            }
        }

        private void SendControllerInfo(int slot, EndPoint sender)
        {
            // Response payload: msgType(4) + slot(1) + slotState(1) + model(1) +
            //   connectionType(1) + MAC(6) + battery(1) + padding(1) = 16 bytes
            byte[] packet = new byte[HeaderSize + 16];
            WriteHeader(packet, 16, MsgTypeControllerInfo);

            packet[HeaderSize + 4] = (byte)slot;
            packet[HeaderSize + 5] = (byte)(_slotConnected[slot] ? 2 : 0); // 0=not connected, 2=connected
            packet[HeaderSize + 6] = (byte)(_slotHasMotion[slot] ? 2 : 0); // 0=none, 2=full gyro
            packet[HeaderSize + 7] = 0; // connection type: 0=N/A

            // MAC address: use a fake but unique MAC per slot.
            packet[HeaderSize + 8] = 0x00;
            packet[HeaderSize + 9] = 0x00;
            packet[HeaderSize + 10] = 0x00;
            packet[HeaderSize + 11] = 0x00;
            packet[HeaderSize + 12] = 0x00;
            packet[HeaderSize + 13] = (byte)slot;

            packet[HeaderSize + 14] = 0x05; // battery: charged
            packet[HeaderSize + 15] = 0;    // padding

            FinalizeCrc(packet);

            try { _socket.SendTo(packet, sender); }
            catch { }
        }

        // ─────────────────────────────────────────────
        //  Pad data packet building
        // ─────────────────────────────────────────────

        private byte[] BuildPadDataPacket(int slot, MotionSnapshot snapshot, bool connected)
        {
            // Pad data payload layout (offsets relative to start of payload, after msg type):
            //   [+0]     Slot number
            //   [+1]     Slot state (0=disconnected, 2=connected)
            //   [+2]     Device model (0=N/A, 2=full gyro)
            //   [+3]     Connection type (0=N/A)
            //   [+4..9]  MAC address (6 bytes)
            //   [+10]    Battery status
            //   [+11]    Connected flag (1=connected)
            //   [+12..15] Packet counter (uint32)
            //   [+16]    Buttons bitmask 1 (DPad-Left|Down|Right|Up, Options, R3, L3, Share)
            //   [+17]    Buttons bitmask 2 (Square, Cross, Circle, Triangle, R1, L1, R2, L2)
            //   [+18]    Home button
            //   [+19]    Touch button
            //   [+20]    Left stick X (0–255, 128=center)
            //   [+21]    Left stick Y (0–255, 128=center)
            //   [+22]    Right stick X
            //   [+23]    Right stick Y
            //   [+24..27] Analog D-Pad (left, down, right, up)
            //   [+28..35] Analog buttons (8 bytes)
            //   [+36..41] Touch 1 data (active, id, x16, y16)
            //   [+42..47] Touch 2 data
            //   [+48..55] Motion timestamp (uint64, microseconds)
            //   [+56..59] Accel X (float)
            //   [+60..63] Accel Y (float)
            //   [+64..67] Accel Z (float)
            //   [+68..71] Gyro Pitch (float)
            //   [+72..75] Gyro Yaw (float)
            //   [+76..79] Gyro Roll (float)
            // Total payload: 4 (msg type) + 80 = 84 bytes

            const int payloadSize = 84;
            byte[] packet = new byte[HeaderSize + payloadSize];
            WriteHeader(packet, payloadSize, MsgTypePadData);

            int o = HeaderSize + 4; // offset after message type

            // Controller info header (11 bytes)
            packet[o + 0] = (byte)slot;                               // slot
            packet[o + 1] = (byte)(connected ? 2 : 0);               // state
            packet[o + 2] = (byte)(snapshot.HasMotion ? 2 : 0);      // model
            packet[o + 3] = 0;                                        // connection type

            // MAC address: fake but unique per slot
            packet[o + 4] = 0x00;
            packet[o + 5] = 0x00;
            packet[o + 6] = 0x00;
            packet[o + 7] = 0x00;
            packet[o + 8] = 0x00;
            packet[o + 9] = (byte)slot;

            packet[o + 10] = 0x05; // battery: charged

            // Connected flag
            packet[o + 11] = (byte)(connected ? 1 : 0);

            // Packet counter
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(o + 12), _packetCounters[slot]);

            // Buttons bitmasks at +16..17 — zeroed (motion-only server)
            // Home/touch at +18..19 — zeroed

            // Left/right stick centered at 128
            packet[o + 20] = 128; // left stick X
            packet[o + 21] = 128; // left stick Y
            packet[o + 22] = 128; // right stick X
            packet[o + 23] = 128; // right stick Y

            // Analog D-pad at +24..27, analog buttons at +28..35 — zeroed
            // Touch 1 at +36..41, touch 2 at +42..47 — zeroed

            // Motion timestamp (microseconds)
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(o + 48), snapshot.TimestampUs);

            // Accelerometer (3 × float)
            WriteFloat(packet, o + 56, snapshot.AccelX);
            WriteFloat(packet, o + 60, snapshot.AccelY);
            WriteFloat(packet, o + 64, snapshot.AccelZ);

            // Gyroscope (3 × float)
            WriteFloat(packet, o + 68, snapshot.GyroPitch);
            WriteFloat(packet, o + 72, snapshot.GyroYaw);
            WriteFloat(packet, o + 76, snapshot.GyroRoll);

            FinalizeCrc(packet);

            _packetCounters[slot]++;
            return packet;
        }

        // ─────────────────────────────────────────────
        //  Subscription management
        // ─────────────────────────────────────────────

        private List<EndPoint> GetSubscribers(int slot)
        {
            var result = new List<EndPoint>();
            long now = Stopwatch.GetTimestamp();
            long timeoutTicks = Stopwatch.Frequency * ClientTimeoutMs / 1000;
            List<(EndPoint, int)> expired = null;
            List<EndPoint> expiredAll = null;

            lock (_subscriptions)
            {
                // Specific slot subscribers.
                foreach (var kvp in _subscriptions)
                {
                    if (kvp.Key.Item2 == slot)
                    {
                        if (now - kvp.Value > timeoutTicks)
                        {
                            expired ??= new();
                            expired.Add(kvp.Key);
                        }
                        else
                        {
                            result.Add(kvp.Key.Item1);
                        }
                    }
                }

                // All-slot subscribers.
                foreach (var kvp in _allSlotSubscriptions)
                {
                    if (now - kvp.Value > timeoutTicks)
                    {
                        expiredAll ??= new();
                        expiredAll.Add(kvp.Key);
                    }
                    else if (!result.Contains(kvp.Key))
                    {
                        result.Add(kvp.Key);
                    }
                }

                // Prune expired.
                if (expired != null)
                    foreach (var key in expired)
                        _subscriptions.Remove(key);

                if (expiredAll != null)
                    foreach (var key in expiredAll)
                        _allSlotSubscriptions.Remove(key);
            }

            return result;
        }

        // ─────────────────────────────────────────────
        //  Packet helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Writes the 16-byte server header:
        ///   [0..3]  "DSUS" magic
        ///   [4..5]  protocol version (1001)
        ///   [6..7]  payload length (excl. header)
        ///   [8..11] CRC32 (filled later by FinalizeCrc)
        ///   [12..15] server ID
        /// </summary>
        private void WriteHeader(byte[] packet, int payloadLength, uint msgType)
        {
            packet[0] = (byte)'D';
            packet[1] = (byte)'S';
            packet[2] = (byte)'U';
            packet[3] = (byte)'S';

            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ProtocolVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)payloadLength);

            // CRC32 at [8..11] — zero for now, filled by FinalizeCrc.
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), _serverId);

            // Message type is the first 4 bytes of the payload.
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), msgType);
        }

        /// <summary>
        /// Computes and writes the CRC32 into the header.
        /// </summary>
        private static void FinalizeCrc(byte[] packet)
        {
            // Zero the CRC field before computing.
            packet[8] = packet[9] = packet[10] = packet[11] = 0;
            uint crc = ComputeCrc32(packet, packet.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), crc);
        }

        private static uint ComputeCrc32(byte[] data, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
                crc = (crc >> 8) ^ Crc32Table[(crc ^ data[i]) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        /// <summary>Pre-computed CRC32 lookup table (standard polynomial 0xEDB88320).</summary>
        private static readonly uint[] Crc32Table = GenerateCrc32Table();

        private static uint[] GenerateCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                    entry = (entry & 1) != 0 ? (entry >> 1) ^ 0xEDB88320 : entry >> 1;
                table[i] = entry;
            }
            return table;
        }

        private static void WriteFloat(byte[] buffer, int offset, float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset), value);
        }

        // ─────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }
    }
}
