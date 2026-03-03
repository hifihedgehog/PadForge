using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

// DSU/Cemuhook diagnostic client — connects to PadForge's DSU server
// and displays received motion data in real-time.

const int Port = 26760;
const ushort ProtocolVersion = 1001;
const int HeaderSize = 16;
const int MaxSlots = 4;

// Parse optional slot filter from command line (default: show first active)
int? slotFilter = args.Length > 0 && int.TryParse(args[0], out int sf) ? sf : null;

using var udp = new UdpClient();
udp.Client.ReceiveTimeout = 2000;
var server = new IPEndPoint(IPAddress.Loopback, Port);

uint packetId = 0;

// Send version request
SendPacket(0x100000, Array.Empty<byte>());
Console.WriteLine("Sent version request to 127.0.0.1:26760...");

try
{
    byte[] resp = udp.Receive(ref server);
    Console.WriteLine($"Server responded! ({resp.Length} bytes)");
}
catch (SocketException)
{
    Console.WriteLine("ERROR: No response from server. Is PadForge running with DSU enabled?");
    return;
}

// Subscribe to all pads (flags=0)
byte[] subPayload = new byte[8];
SendPacket(0x100002, subPayload);

Console.WriteLine();
Console.WriteLine("Perform these motions one at a time and note which values change:");
Console.WriteLine("  1. Hold FLAT on table   -> accel should show ~1G on gravity axis");
Console.WriteLine("  2. PITCH forward        -> tilt top edge away from you");
Console.WriteLine("  3. YAW left             -> rotate counter-clockwise (from above)");
Console.WriteLine("  4. ROLL right           -> tilt right side down");
if (slotFilter.HasValue)
    Console.WriteLine($"\nShowing slot {slotFilter.Value} only. Press Ctrl+C to exit.\n");
else
    Console.WriteLine("\nShowing all active slots. Pass slot number as argument to filter.\n");

// Fixed display lines for each slot
int displayStartRow = Console.CursorTop;
for (int i = 0; i < MaxSlots; i++)
    Console.WriteLine($"  Slot {i}: (no data)");
Console.WriteLine();

var lastSub = DateTime.UtcNow;
var lastPkt = new uint[MaxSlots];

while (true)
{
    if ((DateTime.UtcNow - lastSub).TotalSeconds > 3)
    {
        SendPacket(0x100002, subPayload);
        lastSub = DateTime.UtcNow;
    }

    try
    {
        byte[] data = udp.Receive(ref server);
        if (data.Length < HeaderSize + 4) continue;

        uint msgType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize));
        if (msgType != 0x100002) continue;
        if (data.Length < HeaderSize + 84) continue;

        int o = HeaderSize + 4;
        int slot = data[o + 0];
        if (slot >= MaxSlots) continue;
        if (slotFilter.HasValue && slot != slotFilter.Value) continue;

        bool connected = data[o + 11] != 0;
        uint pktCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 12));

        // Skip if packet count hasn't changed (duplicate)
        if (pktCount == lastPkt[slot]) continue;
        lastPkt[slot] = pktCount;

        float accelX = ReadFloat(data, o + 56);
        float accelY = ReadFloat(data, o + 60);
        float accelZ = ReadFloat(data, o + 64);
        float gyroPitch = ReadFloat(data, o + 68);
        float gyroYaw = ReadFloat(data, o + 72);
        float gyroRoll = ReadFloat(data, o + 76);

        // Write to the fixed row for this slot
        Console.SetCursorPosition(0, displayStartRow + slot);
        string conn = connected ? "ON " : "off";
        Console.Write(
            $"  Slot {slot} [{conn}]  " +
            $"Accel  X:{accelX,8:F3}  Y:{accelY,8:F3}  Z:{accelZ,8:F3}  |  " +
            $"Gyro  P:{gyroPitch,8:F2}  Y:{gyroYaw,8:F2}  R:{gyroRoll,8:F2}" +
            "          ");
    }
    catch (SocketException)
    {
        SendPacket(0x100002, subPayload);
        lastSub = DateTime.UtcNow;
    }
}

void SendPacket(uint msgType, byte[] payload)
{
    int payloadSize = 4 + payload.Length;
    byte[] packet = new byte[HeaderSize + payloadSize];

    packet[0] = (byte)'D'; packet[1] = (byte)'S'; packet[2] = (byte)'U'; packet[3] = (byte)'C';
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), ProtocolVersion);
    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)payloadSize);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 12345);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderSize), msgType);
    if (payload.Length > 0)
        Buffer.BlockCopy(payload, 0, packet, HeaderSize + 4, payload.Length);

    uint crc = Crc32(packet);
    BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), crc);

    udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, Port));
    packetId++;
}

float ReadFloat(byte[] data, int offset) => BitConverter.ToSingle(data, offset);

uint Crc32(byte[] data)
{
    uint crc = 0xFFFFFFFF;
    for (int i = 0; i < data.Length; i++)
    {
        crc ^= data[i];
        for (int j = 0; j < 8; j++)
            crc = (crc >> 1) ^ (0xEDB88320 & ~((crc & 1) - 1));
    }
    return ~crc;
}
