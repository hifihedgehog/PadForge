using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Flydigi;
using PadForge.Services;
using Ota = PadForge.Engine.Flydigi.FlydigiScreenOta;
using Proto = PadForge.Engine.Flydigi.FlydigiApexProtocol;
using X = PadForge.Engine.Flydigi.FlydigiApex4XinputScreen;

namespace PadForge.Common.Input.Flydigi
{
    /// <summary>A picture for the pad's screen: its frames, 25,604 bytes
    /// each, and the time each one shows.</summary>
    internal sealed record FlydigiScreenPicture(IReadOnlyList<byte[]> Frames, int IntervalMs);

    /// <summary>Where a send has got to.</summary>
    internal enum FlydigiScreenStep
    {
        Starting,
        /// <summary>The pad is being switched to the mode the send needs.</summary>
        Switching,
        /// <summary>Waiting for the screen's serial port.</summary>
        WaitingForPort,
        /// <summary>The Apex 4 is being moved to WinUSB for the send.</summary>
        Binding,
        Erasing,
        Writing,
        Finishing,
        /// <summary>The Apex 4 is going back to Windows' Xbox driver.</summary>
        HandingBack,
        Done,
    }

    internal readonly record struct FlydigiScreenProgress(FlydigiScreenStep Step, int Done, int Total);

    /// <summary>Why a send did not finish.</summary>
    internal enum FlydigiScreenFailure
    {
        None,
        NotWired,
        TooManyFrames,
        NoPort,
        SeveralPorts,
        PortDidNotOpen,
        NoReply,
        Refused,
        /// <summary>The Apex 4 did not come back as an Xbox 360 pad.</summary>
        NoXboxPad,
        /// <summary>Another Xbox 360 pad is plugged in, which the binding
        /// would move too.</summary>
        OtherXboxPads,
        /// <summary>The pad that came back does not name Flydigi, or does
        /// not answer as an Apex 4.</summary>
        NotApex4,
        BindFailed,
        Canceled,
    }

    internal sealed class FlydigiScreenException : Exception
    {
        public FlydigiScreenFailure Failure { get; }

        public FlydigiScreenException(FlydigiScreenFailure failure, string detail = null)
            : base(detail ?? failure.ToString()) => Failure = failure;
    }

    /// <summary>
    /// Sends a picture to the screen of a Flydigi Apex 4, Apex 5 or Apex 6
    /// Pro (#586). The pad stores the picture and shows it until another is
    /// sent, so a send is something the user starts, never a live mode.
    ///
    /// <para>Apex 5 and Apex 6 Pro, by cable. HID command 31 adds a USB
    /// serial port, FFAA:5555, beside the gamepad. After 5 s the port is
    /// looked for for up to 30 s and opened at 921600 8N1 with DTR and RTS
    /// raised, the way Space Station's updater opens it. Then the picture's
    /// base address is read back from the chip, the sectors under the
    /// picture are erased, the picture goes out 55 bytes at a time, and a
    /// reset with its length and checksum ends the session. The chip then
    /// syncs for about 15 s and the pad restarts on its own
    /// (openflydigi flydigi/screen_ota.py and gpa6_screen.py, PROTOCOL.md
    /// sections 8d and 8e). Every address comes from the chip, so nothing
    /// here can reach the program region. A port that is there before the
    /// switch is a send that stopped partway, and it is used as it is, the
    /// reference's recovery path.</para>
    ///
    /// <para>Apex 4, by cable. Its screen is reachable only in Xbox mode
    /// over interface 0's interrupt endpoints (flydigi-space-station-mac
    /// docs/protocol.md sections 1 and 6). The pad is switched to Xbox mode
    /// with 05 ED, and must come back as the one new 045E:028E device with a
    /// USB product string that names Flydigi, with no other Xbox 360 pad
    /// plugged in, since the binding moves every device of those IDs.
    /// SDL's Xbox 360 driver is held off, WinUSB is bound to the pad
    /// (<see cref="VendorUsbDriverInstaller.Bind"/>), and the pad must
    /// answer the device-info command as an Apex 4 before anything else is
    /// sent. Each frame goes as a start, data packets of 26 bytes and an
    /// end, each acknowledged, then the picture's end. The pad is switched
    /// back with A5 17, and its node goes back to Windows' Xbox driver,
    /// whatever happened. A send cut off by a crash leaves the node on the
    /// record, and the next start gives it back.</para>
    /// </summary>
    internal static class FlydigiScreenSender
    {
        /// <summary>How long one frame takes, measured on each pad: about
        /// 25 s on an Apex 5 (PROTOCOL.md section 8d, 473 exchanges at 52 ms),
        /// about 7 s on an Apex 6 Pro, whose engine reads each reply as it
        /// lands, and about 4.5 s on an Apex 4 (docs/protocol.md section
        /// 6).</summary>
        internal static double SecondsPerFrame(FlydigiApexModel model) => model switch
        {
            FlydigiApexModel.Apex5 => 25.0,
            FlydigiApexModel.Apex6Pro => 7.3,
            _ => 4.5,
        };

        internal static int MaximumFrames(FlydigiApexModel model) => model switch
        {
            FlydigiApexModel.Apex5 => Ota.Apex5MaximumFrames,
            FlydigiApexModel.Apex6Pro => Ota.Apex6MaximumFrames,
            _ => X.MaximumFrames,
        };

        /// <summary>The time each frame shows: 1 ms to 2.5 s on an Apex 6,
        /// as its engine takes it, and from 10 ms on the others.</summary>
        internal static int ClampInterval(FlydigiApexModel model, int intervalMs)
            => model == FlydigiApexModel.Apex6Pro ? Math.Clamp(intervalMs, 1, 2500) : Math.Clamp(intervalMs, 10, 2550);

        /// <summary>Test seams: the COM ports present, how one opens, the
        /// clock's sleep and the Apex 4's USB steps.</summary>
        internal static Func<IReadOnlyList<string>> BootloaderPorts = ListBootloaderPorts;
        internal static Func<string, IOtaLink> OpenPort = SerialOtaLink.Open;
        internal static Action<int, CancellationToken> Sleep = (ms, ct) => ct.WaitHandle.WaitOne(ms);

        /// <summary>The sends running, one per pad.</summary>
        private static readonly HashSet<string> s_running = new(StringComparer.OrdinalIgnoreCase);

        internal static bool IsSending(FlydigiApexSession pad)
        {
            lock (s_running) return pad != null && s_running.Contains(pad.Path);
        }

        /// <summary>Sends <paramref name="picture"/> to the pad's screen, on a
        /// thread of its own. Throws <see cref="FlydigiScreenException"/>
        /// with the reason when it does not finish.</summary>
        internal static Task SendAsync(FlydigiApexSession pad, FlydigiScreenPicture picture, Apex4Binding binding,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
            => Task.Run(() => Send(pad, picture, binding, progress, ct), ct);

        internal static void Send(FlydigiApexSession pad, FlydigiScreenPicture picture, Apex4Binding binding,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
        {
            if (pad == null || picture?.Frames == null || picture.Frames.Count == 0)
                throw new FlydigiScreenException(FlydigiScreenFailure.Refused, "nothing to send");
            var model = pad.Identity.Model;
            if (!pad.Identity.Wired) throw new FlydigiScreenException(FlydigiScreenFailure.NotWired);
            if (picture.Frames.Count > MaximumFrames(model)) throw new FlydigiScreenException(FlydigiScreenFailure.TooManyFrames);
            foreach (var frame in picture.Frames)
                if (!FlydigiScreenFormat.IsFrame(frame))
                    throw new FlydigiScreenException(FlydigiScreenFailure.Refused, "not a screen frame");
            lock (s_running)
            {
                if (!s_running.Add(pad.Path)) throw new FlydigiScreenException(FlydigiScreenFailure.Refused, "a send is running");
            }
            try
            {
                SdlDiagLog.WriteLine($"FLYDIGI screen send: {model}, {picture.Frames.Count} frame(s), {picture.IntervalMs} ms");
                if (model == FlydigiApexModel.Apex4) SendApex4(pad, picture, binding, progress, ct);
                else SendSerial(pad, picture, progress, ct);
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Done, 1, 1));
                SdlDiagLog.WriteLine("FLYDIGI screen send done");
            }
            catch (OperationCanceledException)
            {
                SdlDiagLog.WriteLine("FLYDIGI screen send canceled");
                throw new FlydigiScreenException(FlydigiScreenFailure.Canceled);
            }
            catch (FlydigiScreenException ex)
            {
                SdlDiagLog.WriteLine($"FLYDIGI screen send stopped: {ex.Failure} {ex.Message}");
                throw;
            }
            finally
            {
                lock (s_running) s_running.Remove(pad.Path);
            }
        }

        // ─────────────────────────────────────────────
        //  Apex 5 and Apex 6 Pro: the serial bootloader
        // ─────────────────────────────────────────────

        private static void SendSerial(FlydigiApexSession pad, FlydigiScreenPicture picture,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
        {
            bool strict = pad.Identity.Model == FlydigiApexModel.Apex6Pro;
            int interval = ClampInterval(pad.Identity.Model, picture.IntervalMs);
            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Starting, 0, 1));

            var present = BootloaderPorts();
            if (present.Count > 1) throw new FlydigiScreenException(FlydigiScreenFailure.SeveralPorts);
            string port = present.Count == 1 ? present[0] : null;
            if (port == null)
            {
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Switching, 0, 1));
                byte[] upgrade = strict ? Proto.Apex6ScreenUpgrade() : Proto.Apex5ScreenUpgrade();
                // The pad's answer says nothing either way: an Apex 5 can add
                // the port with none. The port is what counts
                // (enter_upgrade_mode).
                pad.Exclusive(p => p.Write(upgrade));
                Sleep(Ota.SwitchSettleMs, ct);
                ct.ThrowIfCancellationRequested();
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.WaitingForPort, 0, 1));
                port = WaitForPort(ct);
            }
            else
            {
                SdlDiagLog.WriteLine("FLYDIGI screen: a bootloader port is there already, the send goes to it");
            }

            using var link = OpenPort(port) ?? throw new FlydigiScreenException(FlydigiScreenFailure.PortDidNotOpen, port);
            Upload(link, picture.Frames, interval, strict, progress, ct);
        }

        private static string WaitForPort(CancellationToken ct)
        {
            for (int waited = 0; ; waited += 250)
            {
                ct.ThrowIfCancellationRequested();
                var ports = BootloaderPorts();
                if (ports.Count > 1) throw new FlydigiScreenException(FlydigiScreenFailure.SeveralPorts);
                if (ports.Count == 1) return ports[0];
                if (waited >= Ota.PortTimeoutMs) throw new FlydigiScreenException(FlydigiScreenFailure.NoPort);
                Sleep(250, ct);
            }
        }

        /// <summary>The COM ports of the screen's bootloader, by the IDs in
        /// their instance IDs.</summary>
        private static IReadOnlyList<string> ListBootloaderPorts()
        {
            string ids = $"VID_{Ota.VendorId:X4}&PID_{Ota.ProductId:X4}";
            return SerialControllers.ListComPorts()
                .Where(p => p.InstanceId?.IndexOf(ids, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => p.PortName)
                .ToList();
        }

        /// <summary>The upload, in the order of screen_ota.upload: the base
        /// address, the version, the erases, the writes and the reset. Each
        /// step waits for the reply its state waits for.</summary>
        internal static uint Upload(IOtaLink link, IReadOnlyList<byte[]> frames, int intervalMs, bool strict,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
        {
            byte[] data = FlydigiScreenFormat.Join(frames);
            var body = Exchange(link, Ota.OpGetBase, Ota.BasePayload(frames.Count, intervalMs, strict), strict, ct);
            if (body.Length < 4) throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "no base address");
            uint baseAddress = (uint)(body[0] | (body[1] << 8) | (body[2] << 16) | (body[3] << 24));
            int erases = Ota.Erases(data.Length), writes = Ota.Writes(data.Length);
            if (strict)
            {
                long eraseSize = (long)erases * Ota.EraseBlock;
                if (baseAddress % Ota.EraseBlock != 0 || baseAddress + eraseSize > 0x1_0000_0000L)
                    throw new FlydigiScreenException(FlydigiScreenFailure.Refused, "unaligned picture address");
                // The engine pauses after the base address's reply.
                Sleep(200, ct);
            }
            Exchange(link, Ota.OpGetVersion, ReadOnlySpan<byte>.Empty, strict, ct);

            int total = erases + writes, done = 0;
            for (int i = 0; i < erases; i++)
            {
                ct.ThrowIfCancellationRequested();
                Exchange(link, Ota.OpErase, Ota.AddressPayload(baseAddress + (uint)(i * Ota.EraseBlock)), strict, ct);
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Erasing, ++done, total));
            }
            for (int i = 0; i < writes; i++)
            {
                ct.ThrowIfCancellationRequested();
                int offset = i * Ota.WriteChunk;
                var chunk = data.AsSpan(offset, Math.Min(Ota.WriteChunk, data.Length - offset));
                Exchange(link, Ota.OpWrite, Ota.WritePayload(baseAddress + (uint)offset, chunk, strict), strict, ct);
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Writing, ++done, total));
            }
            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Finishing, done, total));
            // No retry of the reset: a second one could land after the chip
            // restarted (_exchange_checked with retries=0).
            Exchange(link, Ota.OpReset, Ota.ResetPayload(data), strict, ct, retries: 0);
            return baseAddress;
        }

        private static byte[] Exchange(IOtaLink link, byte opcode, ReadOnlySpan<byte> payload, bool strict,
            CancellationToken ct, int retries = Ota.StrictRetries)
        {
            byte[] packet = Ota.Packet(opcode, payload, strict);
            if (!strict)
            {
                if (!link.Write(packet)) throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "write failed");
                var reply = link.ReadReply(Ota.ReplyTimeoutMs, ct);
                if (!Ota.ParseLegacy(reply, out _, out byte got, out byte[] body))
                    throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, $"no reply to opcode {opcode}");
                if (got != opcode)
                    throw new FlydigiScreenException(FlydigiScreenFailure.Refused, $"opcode {opcode} answered as {got}");
                return body;
            }

            var buffered = new List<byte>();
            byte[] sent = payload.ToArray();
            for (int attempt = 0; attempt <= retries; attempt++)
            {
                if (!link.Write(packet)) throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "write failed");
                long deadline = Environment.TickCount64 + Ota.StrictReplyMs;
                buffered.Clear();
                // A reply that answers another packet never moves the send on,
                // and never stretches the wait.
                for (int read = 0; read < 32; read++)
                {
                    ct.ThrowIfCancellationRequested();
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) break;
                    var chunk = link.ReadChunk((int)left, ct);
                    if (chunk.Length == 0) break;
                    buffered.AddRange(chunk);
                    switch (Ota.ReadStrict(buffered, opcode, sent, out byte result, out byte[] body))
                    {
                        case Ota.StrictReading.Accepted: return body;
                        case Ota.StrictReading.Refused:
                            throw new FlydigiScreenException(FlydigiScreenFailure.Refused, $"opcode {opcode}: result {result}");
                    }
                }
            }
            throw new FlydigiScreenException(FlydigiScreenFailure.NoReply,
                opcode == Ota.OpReset ? "no answer to the reset" : $"no answer to opcode {opcode}");
        }

        // ─────────────────────────────────────────────
        //  Apex 4: Xbox mode over WinUSB
        // ─────────────────────────────────────────────

        /// <summary>What the Apex 4's send asks of the rest of PadForge: the
        /// record that a node is owed a hand-back, made and settled in the
        /// settings file before and after the binding, and SDL's Xbox 360
        /// driver held off while PadForge has the pad.</summary>
        internal sealed class Apex4Binding
        {
            public Action<string> Owe { get; init; } = _ => { };
            public Action<string> Settle { get; init; } = _ => { };
            public Action<bool> HoldSdl { get; init; } = _ => { };
        }

        private const ushort XboxVendorId = 0x045E;
        private const ushort XboxProductId = 0x028E;

        /// <summary>How long the Apex 4 has to come back as an Xbox 360 pad
        /// after 05 ED: the reference waits 4 s for the re-enumeration and
        /// another 2 s before it asks again (ControllerModel.swift
        /// switchMode).</summary>
        internal const int XboxModeTimeoutMs = 10_000;

        /// <summary>Test seams for the USB steps.</summary>
        internal static Func<IReadOnlyList<VendorUsbDriverInstaller.UsbNode>> UsbNodes = VendorUsbDriverInstaller.ListPresentUsbNodes;
        internal static Func<string, string> ProductStringOf = BusReportedName;
        internal static Func<VendorUsbDriverInstaller.BindPlan, string, IReadOnlyList<VendorUsbDriverInstaller.UsbNode>, CancellationToken, bool> BindWinUsb =
            (plan, id, nodes, ct) => VendorUsbDriverInstaller.Bind(plan, id, nodes, Log, ct, from: new[] { "xusb22" });
        internal static Func<string, CancellationToken, bool> RestoreDriver = (id, ct) => VendorUsbDriverInstaller.Restore(id, Log, ct);
        internal static Func<string, IXboxPipe> OpenXbox = OpenWinUsbPad;

        private static void Log(string line) => SdlDiagLog.WriteLine("FLYDIGI screen: " + line);

        private static IEnumerable<VendorUsbDriverInstaller.UsbNode> XboxPads(IReadOnlyList<VendorUsbDriverInstaller.UsbNode> nodes)
            => (nodes ?? Array.Empty<VendorUsbDriverInstaller.UsbNode>())
                .Where(n => n.Vid == XboxVendorId && n.Pid == XboxProductId && n.Interface < 0);

        private static void SendApex4(FlydigiApexSession pad, FlydigiScreenPicture picture, Apex4Binding binding,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
        {
            binding ??= new Apex4Binding();
            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Starting, 0, 1));
            // The binding moves every device of these IDs, so no other may
            // be there to move.
            if (XboxPads(UsbNodes()).Any()) throw new FlydigiScreenException(FlydigiScreenFailure.OtherXboxPads);

            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Switching, 0, 1));
            pad.Exclusive(p => p.Write(Proto.Apex4Command(X.DInputSwitchToXInput)));

            VendorUsbDriverInstaller.UsbNode node = default;
            bool found = false;
            for (int waited = 0; waited < XboxModeTimeoutMs && !found; waited += 250)
            {
                Sleep(250, ct);
                ct.ThrowIfCancellationRequested();
                var pads = XboxPads(UsbNodes()).ToList();
                if (pads.Count > 1) throw new FlydigiScreenException(FlydigiScreenFailure.OtherXboxPads);
                if (pads.Count == 1)
                {
                    node = pads[0];
                    found = true;
                }
            }
            if (!found) throw new FlydigiScreenException(FlydigiScreenFailure.NoXboxPad);
            string name = ProductStringOf(node.InstanceId);
            Log($"{node.InstanceId} came back as \"{name}\" on {node.Service}");
            if (string.IsNullOrEmpty(name) || name.IndexOf("Flydigi", StringComparison.OrdinalIgnoreCase) < 0)
                throw new FlydigiScreenException(FlydigiScreenFailure.NotApex4, name);

            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Binding, 0, 1));
            // On the record before the driver moves, so a crash from here on
            // still hands the node back at the next start.
            binding.Owe(node.InstanceId);
            binding.HoldSdl(true);
            bool switchedBack = false;
            try
            {
                var plan = new VendorUsbDriverInstaller.BindPlan(VendorUsbDriverInstaller.BindId(node), "Flydigi Apex 4");
                if (!BindWinUsb(plan, node.InstanceId, UsbNodes(), ct))
                    throw new FlydigiScreenException(FlydigiScreenFailure.BindFailed);
                using var pipe = OpenXbox(node.InstanceId) ?? throw new FlydigiScreenException(FlydigiScreenFailure.BindFailed, "WinUSB did not open");
                int type = Apex4DeviceType(pipe, ct, out byte connection);
                Log($"device info: type {type}, connection {connection}");
                if (type < 0 || !Proto.IsApex4DeviceType((byte)type))
                    throw new FlydigiScreenException(FlydigiScreenFailure.NotApex4, $"device type {type}");
                UploadApex4(pipe, picture, progress, ct);
                // Back to DInput, where PadForge drives the pad's triggers
                // and lights (DeviceSession.switchMode).
                pipe.Write(X.SwitchToDInput());
                switchedBack = true;
            }
            finally
            {
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.HandingBack, 0, 1));
                binding.HoldSdl(false);
                // The node goes back to Windows' driver whatever happened: a
                // node that left for DInput loses PadForge's package, so it
                // takes xusb22 when it comes back.
                bool restored = RestoreDriver(node.InstanceId, CancellationToken.None);
                if (restored) binding.Settle(node.InstanceId);
                Log($"hand-back of {node.InstanceId}: {(restored ? "done" : "owed to the next start")}, switched back {switchedBack}");
            }
        }

        /// <summary>The pad's answer to the device-info command: its type, or
        /// -1 when none came in 2 s.</summary>
        private static int Apex4DeviceType(IXboxPipe pipe, CancellationToken ct, out byte connection)
        {
            connection = 0;
            if (!pipe.Write(X.DeviceInfoRequest())) return -1;
            long deadline = Environment.TickCount64 + 2000;
            var report = new byte[64];
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                int got = pipe.Read(report, 100);
                if (got < 0) return -1;
                if (got == 0) continue;
                int type = X.ParseDeviceType(report.AsSpan(0, got), out connection);
                if (type >= 0) return type;
            }
            return -1;
        }

        /// <summary>start, data and end for each frame, then the picture's
        /// end, each sent again until it is acknowledged, five times at the
        /// most (DeviceSession.perform).</summary>
        internal static void UploadApex4(IXboxPipe pipe, FlydigiScreenPicture picture,
            IProgress<FlydigiScreenProgress> progress, CancellationToken ct)
        {
            int frames = picture.Frames.Count;
            byte period = X.Period(picture.IntervalMs);
            int perFrame = (FlydigiScreenFormat.FrameLength + X.Chunk - 1) / X.Chunk + 2;
            int total = frames * perFrame + 1, done = 0;
            for (int index = 0; index < frames; index++)
            {
                var frame = picture.Frames[index];
                Step(pipe, X.Start(index + 1, frames, period, frame.Length), X.CmdScreenStart, X.CmdScreenStart, X.ReplyMs, ct);
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Writing, ++done, total));
                for (int offset = 0; offset < frame.Length; offset += X.Chunk)
                {
                    var chunk = frame.AsSpan(offset, Math.Min(X.Chunk, frame.Length - offset));
                    Step(pipe, X.Data(offset, chunk), X.CmdScreenData, X.CmdScreenData, X.DataReplyMs, ct);
                    progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Writing, ++done, total));
                }
                Step(pipe, X.End(index + 1, frame.Length), X.CmdScreenEnd, X.CmdScreenEnd, X.ReplyMs, ct);
                progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Writing, ++done, total));
            }
            progress?.Report(new FlydigiScreenProgress(FlydigiScreenStep.Finishing, done, total));
            Step(pipe, X.EndAll(frames), X.CmdScreenEnd, X.CmdScreenEndAll, X.ReplyMs, ct);
        }

        private static void Step(IXboxPipe pipe, byte[] packet, byte ack, byte otherAck, int timeoutMs, CancellationToken ct)
        {
            var report = new byte[64];
            for (int attempt = 0; attempt < X.Attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (!pipe.Write(packet)) throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "write failed");
                long deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    int got = pipe.Read(report, 100);
                    if (got < 0) throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "read failed");
                    if (got == 0 || !X.ParseAck(report.AsSpan(0, got), out byte command, out byte result)) continue;
                    if (command != ack && command != otherAck) continue;
                    // 0 is done. Anything else asks for the packet again.
                    if (result == 0) return;
                    break;
                }
            }
            throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, $"no acknowledgement of {packet[1]:X2}");
        }

        /// <summary>The product string the device gave, as Windows keeps
        /// it.</summary>
        private static string BusReportedName(string instanceId)
        {
            try
            {
                var device = Nefarius.Utilities.DeviceManagement.PnP.PnPDevice.GetDeviceByInstanceId(instanceId);
                return device.GetProperty<string>(Nefarius.Utilities.DeviceManagement.PnP.DevicePropertyKey.Device_BusReportedDeviceDesc);
            }
            catch
            {
                return null;
            }
        }

        private static IXboxPipe OpenWinUsbPad(string instanceId)
        {
            var paths = VendorUsbDriverInstaller.ActiveInterfacePaths(VendorUsbDriverInstaller.InterfaceGuid);
            if (paths == null) return null;
            string token = instanceId.Replace('\\', '#') + "#";
            string path = paths.FirstOrDefault(p => p.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
            var usb = WinUsbTouchSenseUsb.Open(path);
            if (usb == null) return null;
            if (!usb.Describe(out _, out byte inPipe, out byte outPipe))
            {
                usb.Dispose();
                return null;
            }
            return new WinUsbXboxPipe(usb, inPipe != 0 ? inPipe : X.InEndpoint, outPipe != 0 ? outPipe : X.OutEndpoint);
        }

        /// <summary>The pad's interrupt pipes.</summary>
        internal interface IXboxPipe : IDisposable
        {
            bool Write(byte[] packet);

            /// <summary>One report, waiting at most
            /// <paramref name="timeoutMs"/>: its length, 0 for none, -1 when
            /// the read failed.</summary>
            int Read(byte[] buffer, int timeoutMs);
        }

        private sealed class WinUsbXboxPipe : IXboxPipe
        {
            private readonly WinUsbTouchSenseUsb _usb;
            private readonly byte _in, _out;
            private int _timeout = -1;

            public WinUsbXboxPipe(WinUsbTouchSenseUsb usb, byte inPipe, byte outPipe)
            {
                _usb = usb;
                _in = inPipe;
                _out = outPipe;
                _usb.SetTimeout(_out, 1000);
            }

            public bool Write(byte[] packet) => _usb.WritePipe(_out, packet);

            public int Read(byte[] buffer, int timeoutMs)
            {
                if (timeoutMs != _timeout)
                {
                    _usb.SetTimeout(_in, timeoutMs);
                    _timeout = timeoutMs;
                }
                return _usb.ReadPipe(_in, buffer, out _);
            }

            public void Dispose() => _usb.Dispose();
        }
    }

    /// <summary>The screen bootloader's serial port.</summary>
    internal interface IOtaLink : IDisposable
    {
        bool Write(byte[] data);

        /// <summary>What arrives within <paramref name="timeoutMs"/>, empty
        /// when nothing did.</summary>
        byte[] ReadChunk(int timeoutMs, CancellationToken ct);

        /// <summary>One whole reply: what arrives until the port has been
        /// quiet for 50 ms after the first byte, which a reply under five
        /// bytes needs (OtaLink.read_reply). Empty when nothing came.</summary>
        byte[] ReadReply(int timeoutMs, CancellationToken ct);
    }

    /// <summary>
    /// A COM port opened the way Space Station's updater opens the screen's:
    /// 921600 baud, 8 data bits, no parity, one stop bit, no flow control,
    /// DTR and RTS raised (PROTOCOL.md section 8d, ScanDevices). Reads use
    /// the timeouts that hand back the first bytes as they land and wait no
    /// longer than asked.
    /// </summary>
    internal sealed class SerialOtaLink : IOtaLink
    {
        private readonly SafeFileHandle _handle;

        private SerialOtaLink(SafeFileHandle handle) => _handle = handle;

        internal static IOtaLink Open(string comName)
        {
            string path = @"\\.\" + comName;
            // The test host opens no device (DirectDeviceAccess).
            if (string.IsNullOrEmpty(comName) || DirectDeviceAccess.Refuses(path)) return null;
            var handle = CreateFileW(path, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            SetupComm(handle, 4096, 4096);
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            GetCommState(handle, ref dcb);
            dcb.BaudRate = Ota.BaudRate;
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            // fBinary on, every flow control off, DTR and RTS enabled.
            dcb.Flags = 0x1 | (1u << 4) | (1u << 12);
            if (!SetCommState(handle, ref dcb))
            {
                handle.Dispose();
                return null;
            }
            EscapeCommFunction(handle, 5);
            EscapeCommFunction(handle, 3);
            PurgeComm(handle, 0x0004 | 0x0008);
            return new SerialOtaLink(handle);
        }

        public bool Write(byte[] data)
        {
            var timeouts = new COMMTIMEOUTS { WriteTotalTimeoutConstant = (uint)Ota.ReplyTimeoutMs };
            SetCommTimeouts(_handle, ref timeouts);
            return WriteFile(_handle, data, data.Length, out int written, IntPtr.Zero) && written == data.Length;
        }

        public byte[] ReadChunk(int timeoutMs, CancellationToken ct)
        {
            // MAXDWORD, MAXDWORD and a constant: the read returns as soon as a
            // byte is there, and after the constant when none came.
            var buffer = new byte[64];
            long deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return Array.Empty<byte>();
                var timeouts = new COMMTIMEOUTS
                {
                    ReadIntervalTimeout = uint.MaxValue,
                    ReadTotalTimeoutMultiplier = uint.MaxValue,
                    ReadTotalTimeoutConstant = (uint)Math.Min(left, 250),
                };
                SetCommTimeouts(_handle, ref timeouts);
                if (!ReadFile(_handle, buffer, buffer.Length, out int read, IntPtr.Zero))
                    throw new FlydigiScreenException(FlydigiScreenFailure.NoReply, "the port went away");
                if (read > 0) return buffer.AsSpan(0, read).ToArray();
            }
        }

        public byte[] ReadReply(int timeoutMs, CancellationToken ct)
        {
            var first = ReadChunk(timeoutMs, ct);
            if (first.Length == 0) return first;
            var reply = new List<byte>(first);
            while (true)
            {
                var more = ReadChunk(50, ct);
                if (more.Length == 0) return reply.ToArray();
                reply.AddRange(more);
            }
        }

        public void Dispose() => _handle.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct DCB
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COMMTIMEOUTS
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetupComm(SafeFileHandle file, uint inQueue, uint outQueue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommState(SafeFileHandle file, ref DCB dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommTimeouts(SafeFileHandle file, ref COMMTIMEOUTS timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EscapeCommFunction(SafeFileHandle file, uint function);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PurgeComm(SafeFileHandle file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, int count, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);
    }
}
