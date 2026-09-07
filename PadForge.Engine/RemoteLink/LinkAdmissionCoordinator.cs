using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    internal sealed class LinkAdmissionCoordinator
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _revoked = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LinkAdmission> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SemaphoreSlim> _initiators = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<LinkAdmission> _attempts = new();
        private long _revision;
        private long _serverEpoch;

        internal Func<CancellationToken, LinkAdmission> Capture()
        {
            long revision, epoch;
            lock (_gate) { revision = _revision; epoch = _serverEpoch; }
            return token =>
            {
                var attempt = new LinkAdmission(this, revision, epoch, token);
                lock (_gate) _attempts.Add(attempt);
                return attempt;
            };
        }

        internal bool HasPending(string fingerprint)
        {
            lock (_gate) return _attempts.Any(attempt => string.Equals(attempt.Peer, fingerprint, StringComparison.OrdinalIgnoreCase)
                && !attempt.Published && Valid(attempt));
        }

        private bool Valid(LinkAdmission attempt)
            => !attempt.Ended && !attempt.Rejected && !attempt.Token.IsCancellationRequested
                && attempt.ServerEpoch == _serverEpoch
                && (attempt.Peer == null || !_revoked.TryGetValue(attempt.Peer, out long revoked)
                    || revoked <= attempt.Revision);

        internal async Task BindAsync(LinkAdmission attempt, string fingerprint, bool initiator, bool canonical)
        {
            SemaphoreSlim serial = null;
            lock (_gate)
            {
                attempt.Peer = fingerprint;
                attempt.Canonical = canonical;
                if (!Valid(attempt)) throw new LinkConnectionException("Connection admission was canceled.");
                if (initiator)
                {
                    if (!_initiators.TryGetValue(fingerprint, out serial))
                        _initiators[fingerprint] = serial = new SemaphoreSlim(1, 1);
                }
            }
            if (serial != null)
            {
                await serial.WaitAsync(attempt.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!Valid(attempt))
                    {
                        serial.Release();
                        throw new LinkConnectionException("Connection admission was canceled.");
                    }
                    attempt.InitiatorLease = serial;
                }
            }
            LinkAdmission displaced = null;
            lock (_gate)
            {
                if (!Valid(attempt)) throw new LinkConnectionException("Connection admission was canceled.");
                if (_pending.TryGetValue(fingerprint, out var current) && !ReferenceEquals(current, attempt))
                {
                    if (Valid(current) && !(canonical && !current.Canonical))
                        throw new LinkConnectionException("Another connection admission is in progress.");
                    current.Rejected = true;
                    displaced = current;
                }
                _pending[fingerprint] = attempt;
            }
            displaced?.Cancel();
        }

        internal bool TryRun(LinkAdmission attempt, Action action)
        {
            lock (_gate)
            {
                if (!Valid(attempt) || attempt.Published || attempt.Peer == null
                    || !_pending.TryGetValue(attempt.Peer, out var current) || !ReferenceEquals(current, attempt)) return false;
                action();
                return true;
            }
        }

        internal bool TryPublish(LinkAdmission attempt, Func<bool> publish)
        {
            lock (_gate)
            {
                if (!Valid(attempt) || attempt.Published || attempt.Peer == null
                    || !_pending.TryGetValue(attempt.Peer, out var current) || !ReferenceEquals(current, attempt)) return false;
                if (!publish()) return false;
                attempt.Published = true;
                _pending.Remove(attempt.Peer);
                return true;
            }
        }

        internal void Revoke(string fingerprint, Action removeConnections)
        {
            LinkAdmission[] canceled;
            lock (_gate)
            {
                _revoked[fingerprint] = ++_revision;
                canceled = _attempts.Where(attempt => string.Equals(attempt.Peer, fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var attempt in canceled) attempt.Rejected = true;
                _pending.Remove(fingerprint);
                // This callback changes server membership only. Retirement follows outside this gate.
                removeConnections();
            }
            foreach (var attempt in canceled) attempt.Cancel();
        }

        internal bool TryInvalidatePeer(string fingerprint, Func<bool> removeConnections)
        {
            LinkAdmission[] canceled;
            lock (_gate)
            {
                // The caller checks its recovery lifetime and removes membership
                // under the server lock. Publication uses this same admission gate.
                if (!removeConnections()) return false;
                _revoked[fingerprint] = ++_revision;
                canceled = _attempts.Where(attempt => string.Equals(attempt.Peer, fingerprint, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var attempt in canceled) attempt.Rejected = true;
                _pending.Remove(fingerprint);
            }
            foreach (var attempt in canceled) attempt.Cancel();
            return true;
        }

        internal void Stop()
        {
            LinkAdmission[] canceled;
            lock (_gate)
            {
                ++_serverEpoch;
                canceled = _attempts.ToArray();
                foreach (var attempt in canceled) attempt.Rejected = true;
                _pending.Clear();
            }
            foreach (var attempt in canceled) attempt.Cancel();
        }

        internal void PeerObserved(LinkAdmission attempt)
        {
            bool published;
            lock (_gate) published = attempt.Published;
            if (published) End(attempt);
        }

        internal void End(LinkAdmission attempt)
        {
            SemaphoreSlim serial;
            lock (_gate)
            {
                if (attempt.Ended) return;
                attempt.Ended = true;
                _attempts.Remove(attempt);
                if (attempt.Peer != null && _pending.TryGetValue(attempt.Peer, out var current) && ReferenceEquals(current, attempt))
                    _pending.Remove(attempt.Peer);
                serial = attempt.InitiatorLease;
                attempt.InitiatorLease = null;
            }
            attempt.Cancel();
            serial?.Release();
            attempt.DisposeCancellation();
        }
    }

    internal sealed class LinkAdmission : IDisposable
    {
        private readonly LinkAdmissionCoordinator _owner;
        private readonly CancellationTokenSource _cancellation;
        internal readonly long Revision;
        internal readonly long ServerEpoch;
        internal readonly CancellationToken Token;
        internal string Peer;
        internal bool Canonical, Published, Rejected, Ended;
        internal SemaphoreSlim InitiatorLease;
        internal LinkAdmission(LinkAdmissionCoordinator owner, long revision, long serverEpoch, CancellationToken token)
        {
            _owner = owner;
            Revision = revision;
            ServerEpoch = serverEpoch;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _cancellation.Token;
        }
        internal Task BindAsync(string fingerprint, bool initiator, bool canonical)
            => _owner.BindAsync(this, fingerprint, initiator, canonical);
        internal bool TryRun(Action action) => _owner.TryRun(this, action);
        internal bool TryPublish(Func<bool> action) => _owner.TryPublish(this, action);
        internal void PeerObserved() => _owner.PeerObserved(this);
        internal void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        internal void DisposeCancellation() => _cancellation.Dispose();
        public void Dispose() => _owner.End(this);
    }
}
