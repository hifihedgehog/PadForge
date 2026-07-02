using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>A reliable, ordered, message-framed duplex channel (the TCP control
    /// path). The in-memory implementation in tests and the TCP socket adapter both
    /// satisfy it, so the connection orchestration is transport-agnostic and
    /// deterministically testable.</summary>
    public interface ILinkControlChannel
    {
        Task SendAsync(byte[] message, CancellationToken ct);
        Task<byte[]> ReceiveAsync(CancellationToken ct);
    }

    /// <summary>What a first-contact peer presents for the human grant.</summary>
    public sealed class PendingPairing
    {
        public string Sas { get; init; }
        public string PeerFingerprintHex { get; init; }
    }

    /// <summary>The human's pairing decision: approve, and whether to restrict the
    /// peer to gamepad-only output. Implicitly converts from bool so a simple
    /// auto-approve callback (<c>_ => true</c>) still works.</summary>
    public sealed class PairingApproval
    {
        public bool Approved { get; init; }
        public bool GamepadOnly { get; init; }
        public static implicit operator PairingApproval(bool approved) => new() { Approved = approved };
    }

    /// <summary>Result of a completed control handshake: the data key for the UDP
    /// LinkSession, plus the RemotePeerDevices the peer exposed.</summary>
    public sealed class LinkConnectionResult
    {
        public byte[] DataKey { get; init; }
        public bool IsInitiator { get; init; }
        public byte[] PeerFingerprint { get; init; }
        public string PeerFingerprintHex { get; init; }
        public IReadOnlyList<RemotePeerDevice> RemoteDevices { get; init; }
    }

    /// <summary>Raised to abort a connection that failed authentication, approval, or framing.</summary>
    public sealed class LinkConnectionException : Exception
    {
        public LinkConnectionException(string message) : base(message) { }
    }

    /// <summary>
    /// Orchestrates one Remote Link connection over a control channel (issue #138):
    /// run the pairing handshake, gate first-contact on an explicit approval, derive
    /// separate control/data keys, exchange exposed-device lists under the control
    /// key, and hand back the data key + the peer's devices. Raw sockets are a thin
    /// adapter over <see cref="ILinkControlChannel"/>; this logic is transport-free.
    /// </summary>
    public static class LinkConnection
    {
        private static readonly byte[] ControlInfo = Encoding.ASCII.GetBytes("padforge-link v1 control");
        private static readonly byte[] DataInfo = Encoding.ASCII.GetBytes("padforge-link v1 data");
        private const byte CtrlDeviceList = 1;

        public static Task<LinkConnectionResult> RunInitiatorAsync(
            ILinkControlChannel channel, PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc, CancellationToken ct = default)
            => RunAsync(true, channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct);

        public static Task<LinkConnectionResult> RunResponderAsync(
            ILinkControlChannel channel, PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc, CancellationToken ct = default)
            => RunAsync(false, channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct);

        private static async Task<LinkConnectionResult> RunAsync(
            bool isInitiator, ILinkControlChannel channel, PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc, CancellationToken ct)
        {
            var hs = new LinkHandshake(identity, capabilities ?? Array.Empty<byte>(), isInitiator);

            // ── Authenticated key exchange over the reliable channel ──
            if (isInitiator)
            {
                await channel.SendAsync(hs.StartCommit(), ct);
                byte[] revealR = await channel.ReceiveAsync(ct);
                await channel.SendAsync(hs.OnResponderReveal(revealR), ct);
                byte[] confirm = await channel.ReceiveAsync(ct);
                hs.OnResponderConfirm(confirm);
            }
            else
            {
                byte[] commit = await channel.ReceiveAsync(ct);
                await channel.SendAsync(hs.OnInitiatorCommit(commit), ct);
                byte[] revealI = await channel.ReceiveAsync(ct);
                await channel.SendAsync(hs.OnInitiatorReveal(revealI), ct);
            }

            HandshakeResult result = hs.Result
                ?? throw new LinkConnectionException("Handshake did not complete.");

            // ── Admission: an unknown key always needs an explicit grant ──
            var decision = trust.Decide(result.PeerStaticPublicKey);
            if (decision == TrustDecision.FirstContact)
            {
                PairingApproval approval = approve?.Invoke(new PendingPairing
                {
                    Sas = result.Sas,
                    PeerFingerprintHex = Convert.ToHexString(result.PeerFingerprint),
                }) ?? false;
                if (!approval.Approved)
                    throw new LinkConnectionException("Pairing rejected by the user.");
                trust.Grant(result.PeerStaticPublicKey, name: "", pairedUtc: nowUtc, reconnect: true, gamepadOnly: approval.GamepadOnly);
            }
            // KnownAutoSelect / KnownManual: already pinned, the signature proved possession.

            // ── Separate control + data keys from the one session secret ──
            byte[] controlKey = PeerCrypto.DeriveKey(result.SessionKey, salt: null, ControlInfo);
            byte[] dataKey = PeerCrypto.DeriveKey(result.SessionKey, salt: null, DataInfo);
            // One session per side: its send salt pairs with the peer's recv salt and
            // vice versa, so the same object seals outbound and opens inbound.
            var control = new LinkSession(controlKey, isInitiator);

            // ── Exchange exposed-device lists (sealed). Send then receive: both
            //    sides write first, so a buffered channel never deadlocks. ──
            byte[] listPayload = EncodeDeviceList(exposeLocal ?? Array.Empty<RemotePeerDeviceInfo>());
            await channel.SendAsync(control.Seal(LinkMessageType.Input, CtrlDeviceList, 0, listPayload), ct);

            byte[] peerSealed = await channel.ReceiveAsync(ct);
            if (!control.Open(peerSealed, out _, out byte ctrlType, out _, out byte[] peerListPayload) || ctrlType != CtrlDeviceList)
                throw new LinkConnectionException("Bad or unauthenticated device-list message.");

            string peerFpHex = Convert.ToHexString(result.PeerFingerprint);
            List<RemotePeerDeviceInfo> peerInfos;
            try { peerInfos = DecodeDeviceList(peerListPayload); }
            catch { throw new LinkConnectionException("Malformed device-list payload."); }
            var remoteDevices = new List<RemotePeerDevice>();
            foreach (var info in peerInfos)
            {
                info.PeerFingerprintHex = peerFpHex; // identity is salted by the authenticated peer key
                // Name each device under the peer it came from, e.g. "DualSense (John's PC)",
                // the same labeling the hot-plug reconcile path applies. Without this, devices
                // already shared when the link comes up (the common case) carried no peer label,
                // because only ReconcileRemoteDevices did the suffixing.
                string peerLabel = trust?.ResolvePeerLabel(peerFpHex);
                if (!string.IsNullOrWhiteSpace(peerLabel))
                    info.Name = $"{info.Name} ({peerLabel})";
                remoteDevices.Add(new RemotePeerDevice(info));
            }

            return new LinkConnectionResult
            {
                DataKey = dataKey,
                IsInitiator = isInitiator,
                PeerFingerprint = result.PeerFingerprint,
                PeerFingerprintHex = peerFpHex,
                RemoteDevices = remoteDevices,
            };
        }

        // ── Device-list framing ─────────────────────────────────────────────

        // Marks the metadata extension appended after the v1 records: serial,
        // touchpad shape, and the forwarded DeviceObjects (named inputs). An
        // old decoder reads its `count` records and never looks at the tail,
        // so mixed versions interoperate; a new decoder facing an old sender
        // simply finds no tail. Same compat mechanism as the input codec's
        // appended blocks.
        private const byte DeviceListExtMagic = 0xE2;

        // Shared by the handshake exchange AND the post-connect DeviceList sync (#138).
        // Each entry leads with the owner's STABLE slot, and caps now carry HasHaptic +
        // Online so a remote wheel's FFB pipeline runs and active/inactive propagates.
        internal static byte[] EncodeDeviceList(IReadOnlyList<RemotePeerDeviceInfo> devices)
        {
            var buf = new List<byte> { (byte)Math.Min(devices.Count, 255) };
            int count = Math.Min(devices.Count, 255);
            for (int i = 0; i < count; i++)
            {
                var d = devices[i];
                buf.Add(d.Slot);
                WriteString(buf, d.PeerLocalDeviceId);
                WriteString(buf, d.Name);
                WriteU16(buf, d.VendorId);
                WriteU16(buf, d.ProductId);
                buf.Add((byte)Math.Clamp(d.NumAxes, 0, 255));
                buf.Add((byte)Math.Clamp(d.NumButtons, 0, 255));
                buf.Add((byte)Math.Clamp(d.NumHats, 0, 255));
                byte caps = 0;
                if (d.HasRumble) caps |= 1;
                if (d.HasRumbleTriggers) caps |= 2;
                if (d.HasGyro) caps |= 4;
                if (d.HasAccel) caps |= 8;
                if (d.HasTouchpad) caps |= 16;
                if (d.HasHaptic) caps |= 32;
                if (d.Online) caps |= 64;
                buf.Add(caps);
                WriteU16(buf, (ushort)d.InputDeviceType);
            }

            // Metadata extension (one section per v1 record, same order), so
            // a remote device's mapping picker and Devices-page preview show
            // the SAME named inputs the owner sees locally. Budgeted: the
            // periodic push travels as ONE UDP datagram and old peers receive
            // into a 4 KB buffer, so once the payload nears that floor the
            // remaining devices get empty object sections (the consumer falls
            // back to synthesized names for them) rather than the whole list
            // becoming undeliverable.
            const int PayloadBudget = 3800;
            buf.Add(DeviceListExtMagic);
            for (int i = 0; i < count; i++)
            {
                var d = devices[i];
                WriteString(buf, d.SerialNumber);
                int pads = Math.Clamp(d.NumTouchpads, 0, 255);
                buf.Add((byte)pads);
                for (int p = 0; p < pads; p++)
                {
                    int fingers = (d.TouchpadFingerCounts != null && p < d.TouchpadFingerCounts.Length)
                        ? d.TouchpadFingerCounts[p] : 0;
                    buf.Add((byte)Math.Clamp(fingers, 0, 255));
                }
                var objs = d.DeviceObjects ?? Array.Empty<DeviceObjectItem>();
                int objCount = Math.Min(objs.Length, 1024);
                // Estimate this section before committing to it: per object
                // 26 fixed bytes + UTF-8 name. Over budget -> empty section.
                if (objCount > 0)
                {
                    int estimate = 0;
                    for (int j = 0; j < objCount; j++)
                        estimate += 26 + Encoding.UTF8.GetByteCount(objs[j].Name ?? "");
                    if (buf.Count + 2 + estimate > PayloadBudget) objCount = 0;
                }
                WriteU16(buf, (ushort)objCount);
                for (int j = 0; j < objCount; j++)
                {
                    var obj = objs[j];
                    WriteString(buf, obj.Name);
                    WriteU16(buf, (ushort)Math.Clamp(obj.InputIndex, 0, ushort.MaxValue));
                    WriteU16(buf, (ushort)Math.Clamp(obj.Offset, 0, ushort.MaxValue));
                    WriteU32(buf, (uint)obj.ObjectType);
                    buf.AddRange(obj.ObjectTypeGuid.ToByteArray());
                }
            }
            return buf.ToArray();
        }

        internal static List<RemotePeerDeviceInfo> DecodeDeviceList(byte[] data)
        {
            var list = new List<RemotePeerDeviceInfo>();
            int o = 0;
            int count = data[o++];
            for (int i = 0; i < count; i++)
            {
                byte slot = data[o++];
                var info = new RemotePeerDeviceInfo
                {
                    Slot = slot,
                    PeerLocalDeviceId = ReadString(data, ref o),
                    Name = ReadString(data, ref o),
                    VendorId = ReadU16(data, ref o),
                    ProductId = ReadU16(data, ref o),
                    NumAxes = data[o++],
                    NumButtons = data[o++],
                    NumHats = data[o++],
                };
                byte caps = data[o++];
                info.HasRumble = (caps & 1) != 0;
                info.HasRumbleTriggers = (caps & 2) != 0;
                info.HasGyro = (caps & 4) != 0;
                info.HasAccel = (caps & 8) != 0;
                info.HasTouchpad = (caps & 16) != 0;
                info.HasHaptic = (caps & 32) != 0;
                info.Online = (caps & 64) != 0;
                info.InputDeviceType = ReadU16(data, ref o);
                list.Add(info);
            }

            // Metadata extension: absent from an old sender's payload, and a
            // malformed tail must not cost the already-decoded v1 records, so
            // any parse failure falls back to the v1 result (the consumer then
            // synthesizes generic objects exactly as it did before).
            try
            {
                if (o < data.Length && data[o] == DeviceListExtMagic)
                {
                    o++;
                    for (int i = 0; i < count; i++)
                    {
                        var info = list[i];
                        info.SerialNumber = ReadString(data, ref o);
                        int pads = data[o++];
                        var fingers = pads > 0 ? new int[pads] : Array.Empty<int>();
                        for (int p = 0; p < pads; p++) fingers[p] = data[o++];
                        info.NumTouchpads = pads;
                        info.TouchpadFingerCounts = fingers;
                        int objCount = ReadU16(data, ref o);
                        // Sanity-bound the allocation against what the payload
                        // can actually hold (>= 26 bytes per object): a count
                        // the bytes cannot back is malformed, fail to the
                        // v1 fallback below rather than allocating for it.
                        if (objCount > (data.Length - o) / 26 + 1)
                            throw new InvalidOperationException("object count exceeds payload");
                        if (objCount > 0)
                        {
                            var objs = new DeviceObjectItem[objCount];
                            for (int j = 0; j < objCount; j++)
                            {
                                var obj = new DeviceObjectItem
                                {
                                    Name = ReadString(data, ref o),
                                    InputIndex = ReadU16(data, ref o),
                                    Offset = ReadU16(data, ref o),
                                    ObjectType = (DeviceObjectTypeFlags)ReadU32(data, ref o),
                                };
                                obj.ObjectTypeGuid = new Guid(data.AsSpan(o, 16)); o += 16;
                                objs[j] = obj;
                            }
                            info.DeviceObjects = objs;
                        }
                    }
                }
            }
            catch
            {
                // The documented contract: a malformed extension tail costs
                // nothing but the metadata. Reset EVERY extension-carried
                // field on EVERY record, or a mid-record throw leaves garbage
                // (a string byte read as a touchpad count) half-applied.
                foreach (var info in list)
                {
                    info.SerialNumber = "";
                    info.NumTouchpads = 0;
                    info.TouchpadFingerCounts = null;
                    info.DeviceObjects = null;
                }
            }
            return list;
        }

        private static void WriteString(List<byte> buf, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            WriteU16(buf, (ushort)Math.Min(b.Length, ushort.MaxValue));
            buf.AddRange(b);
        }

        private static string ReadString(byte[] data, ref int o)
        {
            int len = ReadU16(data, ref o);
            string s = Encoding.UTF8.GetString(data, o, len);
            o += len;
            return s;
        }

        private static void WriteU16(List<byte> buf, ushort v)
        {
            buf.Add((byte)(v & 0xFF));
            buf.Add((byte)(v >> 8));
        }

        private static ushort ReadU16(byte[] data, ref int o)
        {
            ushort v = (ushort)(data[o] | (data[o + 1] << 8));
            o += 2;
            return v;
        }

        private static void WriteU32(List<byte> buf, uint v)
        {
            buf.Add((byte)(v & 0xFF));
            buf.Add((byte)((v >> 8) & 0xFF));
            buf.Add((byte)((v >> 16) & 0xFF));
            buf.Add((byte)(v >> 24));
        }

        private static uint ReadU32(byte[] data, ref int o)
        {
            uint v = (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));
            o += 4;
            return v;
        }
    }
}
