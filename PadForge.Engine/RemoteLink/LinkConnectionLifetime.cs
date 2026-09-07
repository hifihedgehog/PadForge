using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PadForge.Engine.RemoteLink
{
    public sealed class LinkDeviceInventory : IReadOnlyList<RemotePeerDeviceInfo>
    {
        private readonly RemotePeerDeviceInfo[] _items;
        private readonly Dictionary<string, object> _sourceIdentities;
        public long Revision { get; }
        public LinkDeviceInventory(long revision, IEnumerable<RemotePeerDeviceInfo> items,
            IReadOnlyDictionary<string, object> sourceIdentities = null)
        {
            Revision = revision;
            _items = items.Select(item => item.CloneForSlot(item.Slot)).ToArray();
            _sourceIdentities = sourceIdentities?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        internal object SourceIdentity(string deviceId)
            => _sourceIdentities != null && _sourceIdentities.TryGetValue(deviceId, out var source) ? source : null;
        public int Count => _items.Length;
        public RemotePeerDeviceInfo this[int index] => _items[index];
        public IEnumerator<RemotePeerDeviceInfo> GetEnumerator() => ((IEnumerable<RemotePeerDeviceInfo>)_items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class LinkHandshakeProgress
    {
        private int _inventorySendStarted;
        internal bool InventorySendStarted => Volatile.Read(ref _inventorySendStarted) != 0;
        internal void MarkInventorySendStarted() => Volatile.Write(ref _inventorySendStarted, 1);
    }

    internal sealed class LinkExposureSnapshot : IReadOnlyList<RemotePeerDeviceInfo>
    {
        internal long ServerGeneration { get; set; }
        internal LinkReconnectAuthorization Reconnect { get; set; }
        internal LinkHandshakeProgress HandshakeProgress { get; set; }
        internal Func<CancellationToken, LinkAdmission> AdmissionFactory { get; set; }
        internal readonly LinkSlotReservations Reservations;
        internal readonly LinkDeviceInventory Source;
        internal readonly RemotePeerDeviceInfo[] Wire;
        internal LinkExposureSnapshot(IReadOnlyList<RemotePeerDeviceInfo> source)
        {
            if (source is LinkExposureSnapshot original)
            {
                ServerGeneration = original.ServerGeneration;
                Reconnect = original.Reconnect;
                HandshakeProgress = original.HandshakeProgress;
                AdmissionFactory = original.AdmissionFactory;
            }
            Source = source is LinkExposureSnapshot prepared ? prepared.Source
                : source as LinkDeviceInventory ?? new LinkDeviceInventory(0, source ?? Array.Empty<RemotePeerDeviceInfo>());
            Reservations = new LinkSlotReservations();
            if (!Reservations.TryPrepare(Source, out Wire, out var active))
                throw new LinkConnectionException("The local device inventory exceeds the link address space.");
            Reservations.Publish(active);
        }
        public int Count => Wire.Length;
        public RemotePeerDeviceInfo this[int index] => Wire[index];
        public IEnumerator<RemotePeerDeviceInfo> GetEnumerator() => ((IEnumerable<RemotePeerDeviceInfo>)Wire).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class LinkReconnectAuthorization
    {
        private readonly byte[] _peerKey;
        private readonly Func<bool> _authorized;
        internal LinkReconnectAuthorization(byte[] peerKey, Func<bool> authorized)
        {
            _peerKey = (byte[])peerKey.Clone();
            _authorized = authorized;
        }
        internal bool CanPublish => _authorized();
        internal bool Allows(byte[] peerKey) => CanPublish && peerKey.AsSpan().SequenceEqual(_peerKey);
    }

    internal sealed class LinkSourceBinding
    {
        internal string DeviceId { get; }
        internal byte Slot { get; }
        internal object SourceIdentity { get; }
        internal LinkSourceBinding(string deviceId, byte slot, object sourceIdentity)
        { DeviceId = deviceId; Slot = slot; SourceIdentity = sourceIdentity; }
    }

    internal sealed class LinkSlotReservations
    {
        private readonly Dictionary<string, byte> _reserved = new(StringComparer.Ordinal);
        private Dictionary<byte, LinkSourceBinding> _active = new();
        internal int ReservedCount => _reserved.Count;
        internal Dictionary<byte, LinkSourceBinding> Active => Volatile.Read(ref _active);

        internal bool TryPrepare(IReadOnlyList<RemotePeerDeviceInfo> source,
            out RemotePeerDeviceInfo[] wire, out Dictionary<byte, LinkSourceBinding> active)
        {
            wire = null;
            active = null;
            if (source.Count > 255) return false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in source)
                if (item == null || string.IsNullOrEmpty(item.PeerLocalDeviceId) || !ids.Add(item.PeerLocalDeviceId))
                    throw new LinkConnectionException("Invalid local device identity.");
            int needed = ids.Count(id => !_reserved.ContainsKey(id));
            if (256 - _reserved.Count < needed) return false;
            var used = new HashSet<byte>(_reserved.Values);
            wire = new RemotePeerDeviceInfo[source.Count];
            active = new Dictionary<byte, LinkSourceBinding>();
            for (int i = 0; i < source.Count; i++)
            {
                var item = source[i];
                if (!_reserved.TryGetValue(item.PeerLocalDeviceId, out byte slot))
                {
                    int free = item.Slot;
                    if (used.Contains((byte)free))
                    {
                        free = 0;
                        while (used.Contains((byte)free)) free++;
                    }
                    slot = (byte)free;
                    _reserved.Add(item.PeerLocalDeviceId, slot);
                    used.Add(slot);
                }
                wire[i] = item.CloneForSlot(slot);
                var inventory = source as LinkDeviceInventory ?? (source as LinkExposureSnapshot)?.Source;
                object sourceIdentity = inventory?.SourceIdentity(item.PeerLocalDeviceId);
                // Unrelated inventory changes retain this source's activation identity.
                active.Add(slot, Active.TryGetValue(slot, out var current) && current.DeviceId == item.PeerLocalDeviceId
                    && ReferenceEquals(current.SourceIdentity, sourceIdentity)
                    ? current : new LinkSourceBinding(item.PeerLocalDeviceId, slot, sourceIdentity));
            }
            return true;
        }

        internal void Publish(Dictionary<byte, LinkSourceBinding> active)
        {
            var current = Active;
            if (current.Count == active.Count
                && active.All(pair => current.TryGetValue(pair.Key, out var binding) && ReferenceEquals(binding, pair.Value))) return;
            Volatile.Write(ref _active, active);
        }
        internal bool TrySlot(string id, out byte slot)
        {
            if (_reserved.TryGetValue(id, out slot) && Active.TryGetValue(slot, out var current))
                return string.Equals(id, current.DeviceId, StringComparison.Ordinal);
            return false;
        }
    }

    public sealed class LinkConnectionLifetime
    {
        private readonly object _commitGate = new();
        private readonly object _sendGate = new();
        private readonly Func<bool> _isCurrent;
        private readonly Func<LinkMessageType, byte, ulong, byte[], bool> _send;
        private readonly LinkSlotReservations _local;
        private readonly ConcurrentDictionary<(byte Slot, int Family), uint> _effects = new();
        private readonly Dictionary<byte, string> _peerReservations = new();
        private Dictionary<byte, string> _peerActive = new();
        private readonly Dictionary<byte, uint> _peerInputFloor = new();
        private volatile bool _retired;
        private long _localRevision;
        private bool _hasPeerInventory;
        private uint _peerInventorySequence;
        public string PeerFingerprint { get; }
        public bool IsCurrent => !_retired && _isCurrent();

        internal LinkConnectionLifetime(string fingerprint, LinkExposureSnapshot exposure, Func<bool> isCurrent,
            Func<LinkMessageType, byte, ulong, byte[], bool> send)
        {
            PeerFingerprint = fingerprint;
            _local = exposure.Reservations;
            _localRevision = exposure.Source.Revision;
            _isCurrent = isCurrent;
            _send = send;
        }

        internal enum InventoryResult { Sent, Unavailable, Stale, Exhausted }

        internal InventoryResult PublishLocalInventory(IReadOnlyList<RemotePeerDeviceInfo> source)
        {
            lock (_sendGate)
            {
                RemotePeerDeviceInfo[] wire;
                Dictionary<byte, LinkSourceBinding> active;
                long revision = (source as LinkDeviceInventory)?.Revision ?? 0;
                lock (_commitGate)
                {
                    if (!IsCurrent) return InventoryResult.Unavailable;
                    if (revision != 0 && revision <= _localRevision) return InventoryResult.Stale;
                    if (!_local.TryPrepare(source, out wire, out active)) return InventoryResult.Exhausted;
                }
                var payload = LinkConnection.EncodeDeviceList(wire);
                Dictionary<byte, LinkSourceBinding> previous;
                lock (_commitGate)
                {
                    if (!IsCurrent) return InventoryResult.Unavailable;
                    previous = _local.Active;
                    _local.Publish(active);
                }
                bool sent = false;
                try { sent = _send(LinkMessageType.DeviceList, 0, NowUs(), payload); }
                finally
                {
                    if (!sent) lock (_commitGate) _local.Publish(previous);
                }
                if (!sent) return InventoryResult.Unavailable;
                lock (_commitGate)
                {
                    if (!IsCurrent) return InventoryResult.Unavailable;
                    _local.Publish(active);
                    _localRevision = revision;
                }
                return InventoryResult.Sent;
            }
        }

        internal IReadOnlyDictionary<byte, LinkSourceBinding> CaptureInputBindings() => _local.Active;

        internal LinkSourceBinding CaptureLocalBinding(byte slot)
            => _local.Active.TryGetValue(slot, out var binding) ? binding : null;

        private bool IsCurrentBinding(LinkSourceBinding binding)
            => binding != null && ReferenceEquals(binding, CaptureLocalBinding(binding.Slot));

        internal bool SendInput(IReadOnlyDictionary<byte, LinkSourceBinding> bindings, string sourceId, byte[] payload, ulong timestamp)
        {
            lock (_sendGate)
            {
                byte slot;
                lock (_commitGate)
                    if (!IsCurrent || !_local.TrySlot(sourceId, out slot)
                        || !bindings.TryGetValue(slot, out var captured) || !IsCurrentBinding(captured)) return false;
                return _send(LinkMessageType.Input, slot, timestamp, payload);
            }
        }

        internal bool Send(LinkMessageType type, byte slot, byte[] payload, ulong timestamp = 0, string deviceId = null)
        {
            lock (_sendGate)
            {
                lock (_commitGate)
                {
                    if (!IsCurrent) return false;
                    if (deviceId != null && (!_peerActive.TryGetValue(slot, out var current) || current != deviceId)) return false;
                }
                return _send(type, slot, timestamp == 0 ? NowUs() : timestamp, payload);
            }
        }

        internal string LocalDeviceId(byte slot) => CaptureLocalBinding(slot)?.DeviceId;

        internal bool TryApplyInput(byte slot, uint sequence, Action action)
        {
            lock (_commitGate)
            {
                if (!IsCurrent || !_peerActive.ContainsKey(slot)) return false;
                if (_peerInputFloor.TryGetValue(slot, out uint floor) && !AntiReplayWindow.IsAfter(sequence, floor))
                    return false;
                action();
                return true;
            }
        }

        internal bool TryCommit(LinkSourceBinding binding, uint sequence, int family, Action action)
        {
            lock (_commitGate)
            {
                if (!IsCurrent || !IsCurrentBinding(binding)) return false;
                if (family is >= 1 and <= 6)
                {
                    var key = (binding.Slot, family);
                    if (_effects.TryGetValue(key, out uint previous) && !AntiReplayWindow.IsAfter(sequence, previous)) return false;
                    _effects[key] = sequence;
                }
                action();
                return true;
            }
        }

        internal bool IsLatest(LinkSourceBinding binding, uint sequence, int family)
            => IsCurrent && IsCurrentBinding(binding)
                && _effects.TryGetValue((binding.Slot, family), out uint current) && current == sequence;

        internal bool TryCommitLatest(LinkSourceBinding binding, uint sequence, int family, Action action)
        {
            lock (_commitGate)
            {
                if (!IsLatest(binding, sequence, family)) return false;
                action();
                return true;
            }
        }

        internal bool AcceptPeerInventory(IReadOnlyList<RemotePeerDeviceInfo> devices, uint sequence,
            bool initial, out bool conflict, Action apply = null)
        {
            lock (_commitGate)
            {
                conflict = false;
                if (_retired || (!initial && !IsCurrent)) return false;
                if (!initial && _hasPeerInventory && !AntiReplayWindow.IsAfter(sequence, _peerInventorySequence)) return false;
                var slots = new HashSet<byte>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var device in devices)
                {
                    if (device == null || string.IsNullOrEmpty(device.PeerLocalDeviceId)
                        || !slots.Add(device.Slot) || !ids.Add(device.PeerLocalDeviceId)) return false;
                    if (_peerReservations.TryGetValue(device.Slot, out var id)
                        && !string.Equals(id, device.PeerLocalDeviceId, StringComparison.Ordinal)) conflict = true;
                }
                if (conflict) return false;
                foreach (var device in devices)
                {
                    _peerReservations[device.Slot] = device.PeerLocalDeviceId;
                    if (!initial && !_peerActive.ContainsKey(device.Slot)) _peerInputFloor[device.Slot] = sequence;
                }
                _peerActive = devices.ToDictionary(device => device.Slot, device => device.PeerLocalDeviceId);
                if (!initial) { _hasPeerInventory = true; _peerInventorySequence = sequence; }
                apply?.Invoke();
                return true;
            }
        }

        public void Retire()
        {
            lock (_commitGate) _retired = true;
        }

        private static ulong NowUs() => (ulong)(System.Diagnostics.Stopwatch.GetTimestamp()
            * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
    }

    public sealed class LinkIncomingFrame
    {
        internal LinkSourceBinding Binding { get; }
        public LinkConnectionLifetime Connection { get; }
        public LinkMessageType Type { get; }
        public byte Slot { get; }
        public string DeviceId { get; }
        public uint Sequence { get; }
        public byte[] Payload { get; }
        public string PeerFingerprint => Connection.PeerFingerprint;
        internal LinkIncomingFrame(LinkConnectionLifetime connection, LinkMessageType type, byte slot,
            uint sequence, byte[] payload)
        {
            Connection = connection;
            Type = type;
            Slot = slot;
            Sequence = sequence;
            Binding = connection.CaptureLocalBinding(slot);
            DeviceId = Binding?.DeviceId;
            Payload = payload;
        }
        public bool TryCommit(int family, Action action)
            => Connection.TryCommit(Binding, Sequence, family, action);
        public LinkEffectTicket Ticket(int family) => new(this, family);
    }

    public sealed class LinkEffectTicket
    {
        private readonly LinkIncomingFrame _frame;
        private readonly int _family;
        internal LinkEffectTicket(LinkIncomingFrame frame, int family) { _frame = frame; _family = family; }
        public bool IsCurrent => _frame.Connection.IsLatest(_frame.Binding, _frame.Sequence, _family);
        public bool TryCommit(Action action) => _frame.Connection.TryCommitLatest(
            _frame.Binding, _frame.Sequence, _family, action);
    }
}
