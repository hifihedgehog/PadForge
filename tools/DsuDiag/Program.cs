using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

// DSU/Cemuhook diagnostic client. Connects to PadForge's DSU server and
// reads the same six motion floats an emulator reads, after grip rotation
// and after the DSU sign convention, so what prints here is what the game
// gets.
//
// Usage:
//   DsuDiag                  live view, all active slots
//   DsuDiag 0                live view, slot 0 only
//   DsuDiag verdict          record 5 s on the first active slot, then judge
//   DsuDiag verdict 0        record 5 s on slot 0
//   DsuDiag verdict 0 8      record 8 s on slot 0
//
// The verdict answers two questions from one run:
//   HOLD   comes from the mean accelerometer over the still countdown, before
//          the recording window opens. Gravity names the axis pointing down,
//          which is how the controller was oriented. Averaged over the
//          rotation instead, gravity sweeps from axis to axis and the mean
//          magnitude collapses.
//   MOTION comes from the gyro over the recording window. Peak rate and
//          integrated angle per axis name which rotation was actually
//          performed.

const int Port = 26760;
const ushort ProtocolVersion = 1001;
const int HeaderSize = 16;
const int MaxSlots = 4;

bool verdictMode = args.Length > 0 &&
                   args[0].Equals("verdict", StringComparison.OrdinalIgnoreCase);
string[] rest = verdictMode ? args.Skip(1).ToArray() : args;

int? slotFilter = rest.Length > 0 && int.TryParse(rest[0], out int sf) ? sf : null;
double captureSeconds = rest.Length > 1 && double.TryParse(rest[1], out double cs) ? cs : 5.0;

using var udp = new UdpClient();
udp.Client.ReceiveTimeout = 2000;
var server = new IPEndPoint(IPAddress.Loopback, Port);

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

byte[] subPayload = new byte[8];
SendPacket(0x100002, subPayload);

if (verdictMode)
{
    RunVerdict();
    return;
}

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
        if (!TryReadSample(data, out int slot, out bool connected, out uint pktCount, out Sample s))
            continue;
        if (slotFilter.HasValue && slot != slotFilter.Value) continue;
        if (pktCount == lastPkt[slot]) continue;
        lastPkt[slot] = pktCount;

        Console.SetCursorPosition(0, displayStartRow + slot);
        string conn = connected ? "ON " : "off";
        Console.Write(
            $"  Slot {slot} [{conn}]  " +
            $"Accel  X:{s.AccelX,8:F3}  Y:{s.AccelY,8:F3}  Z:{s.AccelZ,8:F3}  |  " +
            $"Gyro  P:{s.Pitch,8:F2}  Y:{s.Yaw,8:F2}  R:{s.Roll,8:F2}" +
            "          ");
    }
    catch (SocketException)
    {
        SendPacket(0x100002, subPayload);
        lastSub = DateTime.UtcNow;
    }
}

void RunVerdict()
{
    Console.WriteLine();
    Console.WriteLine("VERDICT MODE");
    Console.WriteLine("  Hold the controller the way you mean to hold it and keep it still");
    Console.WriteLine("  until RECORDING appears. The hold is read during that countdown.");
    Console.WriteLine("  Then perform ONE rotation while it records.");
    Console.WriteLine();

    // The hold is sampled here, over the still countdown, not over the
    // recording window. Averaging accelerometer across the rotation sweeps
    // gravity from one axis to another, so the mean magnitude under-reads what
    // gravity actually was and the off-axis warning fires on a square hold.
    var hold = new List<(int Slot, Sample S)>();
    var holdSeen = new uint[MaxSlots];
    var holdStart = DateTime.UtcNow;
    int shownSecond = -1;

    while (true)
    {
        double left = 3.0 - (DateTime.UtcNow - holdStart).TotalSeconds;
        if (left <= 0) break;

        int secs = (int)Math.Ceiling(left);
        if (secs != shownSecond)
        {
            Console.Write($"\r  Hold still. Starting in {secs}...   ");
            shownSecond = secs;
        }

        if (udp.Available == 0) { Thread.Sleep(2); continue; }

        byte[] hd;
        try { hd = udp.Receive(ref server); }
        catch (SocketException) { continue; }

        if (!TryReadSample(hd, out int hSlot, out bool hConn, out uint hPkt, out Sample hSample))
            continue;
        if (!hConn) continue;
        if (slotFilter.HasValue && hSlot != slotFilter.Value) continue;
        if (hPkt == holdSeen[hSlot]) continue;
        holdSeen[hSlot] = hPkt;
        hold.Add((hSlot, hSample));
    }

    // Everything still queued arrived before the recording window. Left in
    // place it drains in one burst at the top of the loop, every packet
    // stamped with the same dequeue instant, which inflated both the sample
    // count and the reported rate.
    int dropped = DrainSocket();

    Console.WriteLine($"\r  RECORDING for {captureSeconds:F1} s. Do the motion now.        ");

    var samples = new List<Sample>();
    var start = DateTime.UtcNow;
    var subAt = DateTime.UtcNow;
    var seen = new uint[MaxSlots];
    int chosen = slotFilter ?? -1;

    while ((DateTime.UtcNow - start).TotalSeconds < captureSeconds)
    {
        if ((DateTime.UtcNow - subAt).TotalSeconds > 3)
        {
            SendPacket(0x100002, subPayload);
            subAt = DateTime.UtcNow;
        }

        try
        {
            byte[] data = udp.Receive(ref server);
            if (!TryReadSample(data, out int slot, out bool connected, out uint pkt, out Sample s))
                continue;
            if (!connected) continue;
            if (chosen < 0) chosen = slot;
            if (slot != chosen) continue;
            if (pkt == seen[slot]) continue;
            seen[slot] = pkt;

            s.At = DateTime.UtcNow;
            samples.Add(s);
        }
        catch (SocketException)
        {
            SendPacket(0x100002, subPayload);
            subAt = DateTime.UtcNow;
        }
    }

    Console.WriteLine("  Done.");
    Console.WriteLine();

    if (samples.Count < 10)
    {
        Console.WriteLine($"NOT ENOUGH DATA: {samples.Count} samples on slot {chosen}.");
        Console.WriteLine("Check that the slot has a motion-capable device assigned and that");
        Console.WriteLine("DSU is enabled for it.");
        return;
    }

    double span = (samples[^1].At - samples[0].At).TotalSeconds;
    double hz = span > 0 ? (samples.Count - 1) / span : 0;

    var stillHold = hold.Where(h => h.Slot == chosen).Select(h => h.S).ToList();

    double pkP = samples.Max(v => Math.Abs((double)v.Pitch));
    double pkY = samples.Max(v => Math.Abs((double)v.Yaw));
    double pkR = samples.Max(v => Math.Abs((double)v.Roll));

    double intP = 0, intY = 0, intR = 0;
    for (int i = 1; i < samples.Count; i++)
    {
        double dt = (samples[i].At - samples[i - 1].At).TotalSeconds;
        if (dt <= 0 || dt > 0.25) continue;
        intP += samples[i].Pitch * dt;
        intY += samples[i].Yaw * dt;
        intR += samples[i].Roll * dt;
    }

    Console.WriteLine($"Slot {chosen}: {samples.Count} samples over {span:F1} s ({hz:F0} Hz)");
    if (dropped > 0)
        Console.WriteLine($"  (Dropped {dropped} pre-window packet{(dropped == 1 ? "" : "s")}.)");
    Console.WriteLine();

    Console.WriteLine("HOLD  (mean accelerometer over the still countdown, g. Gravity names");
    Console.WriteLine("       which way was down.)");
    if (stillHold.Count < 3)
    {
        Console.WriteLine($"    Countdown sample count: {stillHold.Count}. That is too few to name a hold");
        Console.WriteLine("    axis. Check that the slot was sending before the countdown started.");
    }
    else
    {
        double ax = stillHold.Average(v => (double)v.AccelX);
        double ay = stillHold.Average(v => (double)v.AccelY);
        double az = stillHold.Average(v => (double)v.AccelZ);
        double gMag = Math.Sqrt(ax * ax + ay * ay + az * az);
        Console.WriteLine($"    X: {ax,7:F2}    Y: {ay,7:F2}    Z: {az,7:F2}   ({stillHold.Count} samples)");

        // A still controller reads about 1 g. Anything near zero means the
        // accelerometer sent nothing, and naming an axis off that noise prints
        // a confident answer built on no signal.
        if (gMag < 0.3)
        {
            Console.WriteLine($"    The accelerometer reported nothing usable: {gMag:F2} g total against the");
            Console.WriteLine("    1 g gravity reads on a still controller. No hold axis can be named.");
        }
        else
        {
            string gAxis = Dominant(Math.Abs(ax), Math.Abs(ay), Math.Abs(az), "X", "Y", "Z");
            double gVal = gAxis == "X" ? ax : gAxis == "Y" ? ay : az;
            Console.WriteLine($"    Gravity sits on {(gVal < 0 ? "-" : "+")}{gAxis} at {Math.Abs(gVal):F2} g " +
                              $"(total {gMag:F2} g).");
            double offAxis = Math.Sqrt(Math.Max(0, gMag * gMag - gVal * gVal));
            if (offAxis > 0.35)
                Console.WriteLine($"    WARNING: {offAxis:F2} g sits off that axis. The hold was tilted, " +
                                  "not square to any face.");
        }
    }
    Console.WriteLine();

    Console.WriteLine("MOTION  (gyro, degrees per second and integrated degrees)");
    Console.WriteLine($"    Pitch   peak {pkP,7:F1} deg/s    turned {intP,7:F0} deg");
    Console.WriteLine($"    Yaw     peak {pkY,7:F1} deg/s    turned {intY,7:F0} deg");
    Console.WriteLine($"    Roll    peak {pkR,7:F1} deg/s    turned {intR,7:F0} deg");
    Console.WriteLine();

    double top = Math.Max(pkP, Math.Max(pkY, pkR));
    if (top < 20)
    {
        Console.WriteLine("VERDICT: no real rotation was recorded. Peak rate stayed under 20 deg/s.");
        return;
    }

    string mAxis = Dominant(pkP, pkY, pkR, "PITCH", "YAW", "ROLL");
    double second = new[] { pkP, pkY, pkR }.OrderByDescending(v => v).Skip(1).First();

    Console.Write($"VERDICT: the dominant rotation was {mAxis}");
    if (second <= 0.001)
    {
        // No divisor, so there is no ratio to print. Report what was measured.
        Console.WriteLine(", and the other two axes read zero. The motion was on one axis alone.");
    }
    else
    {
        double ratio = top / second;
        if (ratio >= 3) Console.WriteLine($", cleanly, at {ratio:F1}x the next axis.");
        else if (ratio >= 1.5) Console.WriteLine($", but only {ratio:F1}x the next axis. The motion was mixed.");
        else Console.WriteLine($" by {ratio:F1}x, which is no separation at all. The motion was mixed.");
    }
}

// Empty the receive queue without blocking. Polling Available takes only what
// is already buffered, so no read has to time out to learn the queue is dry.
int DrainSocket()
{
    int n = 0;
    while (udp.Available > 0 && n < 20000)
    {
        try { udp.Receive(ref server); }
        catch (SocketException) { break; }
        n++;
    }
    return n;
}

string Dominant(double a, double b, double c, string na, string nb, string nc)
    => a >= b && a >= c ? na : b >= c ? nb : nc;

bool TryReadSample(byte[] data, out int slot, out bool connected, out uint pktCount, out Sample s)
{
    slot = -1; connected = false; pktCount = 0; s = default;
    if (data.Length < HeaderSize + 4) return false;
    uint msgType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderSize));
    if (msgType != 0x100002) return false;
    if (data.Length < HeaderSize + 84) return false;

    int o = HeaderSize + 4;
    slot = data[o + 0];
    if (slot >= MaxSlots) return false;
    connected = data[o + 11] != 0;
    pktCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 12));

    s = new Sample
    {
        AccelX = ReadFloat(data, o + 56),
        AccelY = ReadFloat(data, o + 60),
        AccelZ = ReadFloat(data, o + 64),
        Pitch = ReadFloat(data, o + 68),
        Yaw = ReadFloat(data, o + 72),
        Roll = ReadFloat(data, o + 76),
    };
    return true;
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

struct Sample
{
    public float AccelX, AccelY, AccelZ;
    public float Pitch, Yaw, Roll;
    public DateTime At;
}
