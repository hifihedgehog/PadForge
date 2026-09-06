using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    public enum LinkAssignmentStatus : byte
    {
        Ok, Denied, Unavailable, Stale, Invalid, Busy, TimedOut, Canceled
    }

    public sealed record LinkAssignmentSlot(byte Index, string Name, bool Assigned, bool CanAssign);

    public sealed record LinkAssignmentRequest(Guid RequestId, string DeviceId,
        bool IsSet = false, long Revision = 0, byte Slot = 0, bool Assigned = false);

    public sealed record LinkAssignmentReply(Guid RequestId, string DeviceId, LinkAssignmentStatus Status,
        long Revision = 0, string Profile = "", LinkAssignmentSlot[] Slots = null)
    {
        public static LinkAssignmentReply For(LinkAssignmentRequest request, LinkAssignmentStatus status)
            => new(request.RequestId, request.DeviceId, status);
    }

    /// <summary>Versioned, bounded payloads inside LinkSession's authenticated type 9.</summary>
    public static class LinkAssignmentCodec
    {
        public const int MaxSlots = 16;
        public const int MaxPayload = 4096;
        private const int MaxTextBytes = 128;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public static byte[] Encode(LinkAssignmentRequest request)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)1);
            writer.Write((byte)(request.IsSet ? 2 : 1));
            writer.Write(request.RequestId.ToByteArray());
            WriteText(writer, request.DeviceId);
            if (request.IsSet)
            {
                writer.Write(request.Revision);
                writer.Write(request.Slot);
                writer.Write(request.Assigned);
            }
            return stream.ToArray();
        }

        public static byte[] Encode(LinkAssignmentReply reply)
        {
            var slots = reply.Slots ?? Array.Empty<LinkAssignmentSlot>();
            if (slots.Length > MaxSlots) throw new ArgumentOutOfRangeException(nameof(reply));
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)1);
            writer.Write((byte)3);
            writer.Write(reply.RequestId.ToByteArray());
            WriteText(writer, reply.DeviceId);
            writer.Write((byte)reply.Status);
            writer.Write(reply.Revision);
            WriteText(writer, ClipText(reply.Profile));
            writer.Write((byte)slots.Length);
            foreach (var slot in slots)
            {
                writer.Write(slot.Index);
                WriteText(writer, ClipText(slot.Name));
                writer.Write(slot.Assigned);
                writer.Write(slot.CanAssign);
            }
            return stream.ToArray();
        }

        public static bool TryDecode(byte[] payload, out LinkAssignmentRequest request, out LinkAssignmentReply reply)
        {
            request = null;
            reply = null;
            if (payload == null || payload.Length > MaxPayload) return false;
            try
            {
                using var stream = new MemoryStream(payload, false);
                using var reader = new BinaryReader(stream, Utf8);
                if (reader.ReadByte() != 1) return false;
                byte operation = reader.ReadByte();
                byte[] idBytes = reader.ReadBytes(16);
                if (idBytes.Length != 16) return false;
                Guid id = new(idBytes);
                string device = ReadText(reader);
                if (id == Guid.Empty || string.IsNullOrEmpty(device)) return false;
                if (operation is 1 or 2)
                {
                    long revision = 0;
                    byte slot = 0;
                    bool assigned = false;
                    if (operation == 2)
                    {
                        revision = reader.ReadInt64();
                        slot = reader.ReadByte();
                        assigned = ReadBool(reader);
                        if (slot >= MaxSlots) return false;
                    }
                    request = new(id, device, operation == 2, revision, slot, assigned);
                }
                else if (operation == 3)
                {
                    var status = (LinkAssignmentStatus)reader.ReadByte();
                    if (status > LinkAssignmentStatus.Busy) return false;
                    long revision = reader.ReadInt64();
                    string profile = ReadText(reader);
                    int count = reader.ReadByte();
                    if (count > MaxSlots) return false;
                    var slots = new LinkAssignmentSlot[count];
                    int seen = 0;
                    for (int i = 0; i < count; i++)
                    {
                        byte slot = reader.ReadByte();
                        if (slot >= MaxSlots || (seen & (1 << slot)) != 0) return false;
                        seen |= 1 << slot;
                        slots[i] = new(slot, ReadText(reader), ReadBool(reader), ReadBool(reader));
                    }
                    reply = new(id, device, status, revision, profile, slots);
                }
                else return false;
                if (stream.Position != stream.Length) { request = null; reply = null; return false; }
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                request = null;
                reply = null;
                return false;
            }
        }

        private static void WriteText(BinaryWriter writer, string text)
        {
            byte[] bytes = Utf8.GetBytes(text ?? "");
            if (bytes.Length > MaxTextBytes) throw new ArgumentOutOfRangeException(nameof(text));
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count > MaxTextBytes) throw new InvalidDataException();
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return Utf8.GetString(bytes);
        }

        private static bool ReadBool(BinaryReader reader) => reader.ReadByte() switch
        {
            0 => false, 1 => true, _ => throw new InvalidDataException()
        };

        private static string ClipText(string text)
        {
            var result = new StringBuilder();
            int count = 0;
            foreach (var rune in (text ?? "").EnumerateRunes())
            {
                if (count + rune.Utf8SequenceLength > MaxTextBytes) break;
                result.Append(rune.ToString());
                count += rune.Utf8SequenceLength;
            }
            return result.ToString();
        }
    }

    /// <summary>Only the receiving UI dispatcher calls Execute. No gate spans a dispatcher hop.</summary>
    public sealed class LinkAssignmentContext
    {
        private readonly LinkAssignmentChannel _channel;
        private readonly long _receivedAt = Environment.TickCount64;
        public LinkAssignmentRequest Request { get; }
        public RemotePeerDevice Device { get; }
        public string PeerFingerprint => _channel.PeerFingerprint;

        internal LinkAssignmentContext(LinkAssignmentChannel channel, LinkAssignmentRequest request, RemotePeerDevice device)
        {
            _channel = channel;
            Request = request;
            Device = device;
        }

        public LinkAssignmentReply Execute(Func<LinkAssignmentReply> action)
            => _channel.Execute(this, _receivedAt, action);
    }

    /// <summary>
    /// One connection's assignment requests. Retries reuse plaintext IDs, never AEAD sequences.
    /// A dialog keeps this instance, so reconnecting cannot move an old edit to a new session.
    /// </summary>
    public sealed class LinkAssignmentChannel
    {
        private readonly object _stateLock = new();
        private readonly object _applyGate = new();
        private readonly Func<bool> _isCurrent;
        private readonly Func<bool> _authorized;
        private readonly Func<string, RemotePeerDevice> _resolve;
        private readonly Func<byte[], bool> _send;
        private readonly Func<LinkAssignmentContext, Task<LinkAssignmentReply>> _handler;
        private readonly Dictionary<Guid, (string Device, TaskCompletionSource<LinkAssignmentReply> Done)> _pending = new();
        private readonly Dictionary<Guid, (byte[] Payload, TaskCompletionSource<LinkAssignmentReply> Done)> _received = new();
        private volatile bool _closed;
        private int _inFlight;
        public string PeerFingerprint { get; }

        public LinkAssignmentChannel(string peerFingerprint, Func<bool> isCurrent, Func<bool> authorized,
            Func<string, RemotePeerDevice> resolve, Func<byte[], bool> send,
            Func<LinkAssignmentContext, Task<LinkAssignmentReply>> handler)
        {
            PeerFingerprint = peerFingerprint;
            _isCurrent = isCurrent;
            _authorized = authorized;
            _resolve = resolve;
            _send = send;
            _handler = handler;
        }

        public Task<LinkAssignmentReply> QueryAsync(string deviceId, CancellationToken cancellationToken = default)
            => SendAsync(new(Guid.NewGuid(), deviceId), cancellationToken);

        public Task<LinkAssignmentReply> SetAsync(string deviceId, long revision, byte slot, bool assigned,
            CancellationToken cancellationToken = default)
            => SendAsync(new(Guid.NewGuid(), deviceId, true, revision, slot, assigned), cancellationToken);

        public async Task<LinkAssignmentReply> SendAsync(LinkAssignmentRequest request,
            CancellationToken cancellationToken = default, int timeoutMs = 5000, int retryMs = 500)
        {
            byte[] payload = LinkAssignmentCodec.Encode(request);
            var done = new TaskCompletionSource<LinkAssignmentReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                if (_closed || !_isCurrent()) return LinkAssignmentReply.For(request, LinkAssignmentStatus.Unavailable);
                if (_pending.Count >= 4 || _pending.ContainsKey(request.RequestId))
                    return LinkAssignmentReply.For(request, LinkAssignmentStatus.Busy);
                _pending.Add(request.RequestId, (request.DeviceId, done));
            }
            try
            {
                long until = Environment.TickCount64 + Math.Max(1, timeoutMs);
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_closed || !_isCurrent()) return LinkAssignmentReply.For(request, LinkAssignmentStatus.Unavailable);
                    int remaining = (int)Math.Max(0, until - Environment.TickCount64);
                    if (remaining == 0) break;
                    try { _send(payload); } catch { /* the next attempt can use a recovered transport */ }
                    try { return await done.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(remaining, Math.Max(1, retryMs))), cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                return LinkAssignmentReply.For(request, cancellationToken.IsCancellationRequested
                    ? LinkAssignmentStatus.Canceled : LinkAssignmentStatus.TimedOut);
            }
            catch (OperationCanceledException) { return LinkAssignmentReply.For(request, LinkAssignmentStatus.Canceled); }
            finally { lock (_stateLock) _pending.Remove(request.RequestId); }
        }

        public void Receive(byte[] payload)
        {
            if (_closed || !_isCurrent() || !LinkAssignmentCodec.TryDecode(payload, out var request, out var reply)) return;
            if (reply != null)
            {
                lock (_stateLock)
                    if (_pending.TryGetValue(reply.RequestId, out var pending) && pending.Device == reply.DeviceId)
                        pending.Done.TrySetResult(reply);
                return;
            }
            if (!_authorized()) { SendReply(LinkAssignmentReply.For(request, LinkAssignmentStatus.Denied)); return; }
            var device = _resolve(request.DeviceId);
            if (device == null) { SendReply(LinkAssignmentReply.For(request, LinkAssignmentStatus.Unavailable)); return; }
            TaskCompletionSource<LinkAssignmentReply> completion = null;
            LinkAssignmentReply immediate = null;
            lock (_stateLock)
            {
                if (_closed) return;
                if (_received.TryGetValue(request.RequestId, out var existing))
                {
                    if (!payload.AsSpan().SequenceEqual(existing.Payload))
                        immediate = LinkAssignmentReply.For(request, LinkAssignmentStatus.Invalid);
                    else if (existing.Done.Task.IsCompletedSuccessfully) immediate = existing.Done.Task.Result;
                    else return; // The first request will send the shared result.
                }
                else
                {
                    if (_received.Count >= 64)
                    {
                        var oldest = _received.FirstOrDefault(x => x.Value.Done.Task.IsCompleted);
                        if (oldest.Value.Done != null) _received.Remove(oldest.Key);
                    }
                    if (_inFlight >= 4 || _received.Count >= 64)
                        immediate = LinkAssignmentReply.For(request, LinkAssignmentStatus.Busy);
                    else
                    {
                        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _received.Add(request.RequestId, ((byte[])payload.Clone(), completion));
                        _inFlight++;
                    }
                }
            }
            if (immediate != null) SendAuthorizedReply(request, immediate);
            else _ = ProcessAsync(new(this, request, device), completion);
        }

        private async Task ProcessAsync(LinkAssignmentContext context, TaskCompletionSource<LinkAssignmentReply> completion)
        {
            LinkAssignmentReply reply;
            try
            {
                reply = _handler == null ? LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Unavailable)
                    : await _handler(context).ConfigureAwait(false);
                reply ??= LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Unavailable);
            }
            catch { reply = LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Unavailable); }
            lock (_stateLock)
            {
                _inFlight--;
                completion.TrySetResult(reply);
            }
            SendAuthorizedReply(context.Request, reply);
        }

        internal LinkAssignmentReply Execute(LinkAssignmentContext context, long receivedAt, Func<LinkAssignmentReply> action)
        {
            lock (_applyGate)
            {
                if (_closed || !_isCurrent() || Environment.TickCount64 - receivedAt >= 5000)
                    return LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Unavailable);
                if (!_authorized()) return LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Denied);
                if (!ReferenceEquals(context.Device, _resolve(context.Request.DeviceId)))
                    return LinkAssignmentReply.For(context.Request, LinkAssignmentStatus.Unavailable);
                return action();
            }
        }

        private void SendAuthorizedReply(LinkAssignmentRequest request, LinkAssignmentReply reply)
        {
            if (!_authorized()) reply = LinkAssignmentReply.For(request, LinkAssignmentStatus.Denied);
            else if (_resolve(request.DeviceId) == null) reply = LinkAssignmentReply.For(request, LinkAssignmentStatus.Unavailable);
            SendReply(reply);
        }

        private void SendReply(LinkAssignmentReply reply)
        {
            if (_closed || !_isCurrent()) return;
            try { _send(LinkAssignmentCodec.Encode(reply)); } catch { /* a retry obtains the cached result */ }
        }

        /// <summary>Called after removal from LinkServer's list, with no server lock held.</summary>
        public void Close()
        {
            lock (_applyGate) _closed = true;
            lock (_stateLock)
            {
                foreach (var item in _pending)
                    item.Value.Done.TrySetResult(new(item.Key, item.Value.Device, LinkAssignmentStatus.Unavailable));
                _pending.Clear();
                _received.Clear();
            }
        }
    }
}
