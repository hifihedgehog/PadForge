using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PadForge.Common.Input
{
    internal static partial class AudioPassthroughService
    {
        internal sealed class PersonaOwner
        {
            public volatile bool Closed;
        }

        private static readonly object _personaFeedLock = new();
        private static readonly object _personaIoGate = new();
        private static readonly ConcurrentQueue<PersonaFeed> _retiredPersonaFeeds = new();
        private static readonly HashSet<Guid> _personaRingPruneCandidates = new();
        private static int _personaDrainQueued;
        private static int _personaRouteBatchDepth;
        internal static Func<string, IWaveIn> PersonaMicCaptureFactory = CreatePersonaMicCapture;
        internal static Action PersonaReconcileRequest = Reconcile;
        internal static Action<Action> PersonaCleanupQueue = action => ThreadPool.QueueUserWorkItem(_ => action());

        internal static void ClosePersonaOwner(PersonaOwner owner)
        {
            lock (_lock)
            lock (_personaFeedLock)
            {
                owner.Closed = true;
                foreach (var feed in _personaFeeds.Values.Where(f => ReferenceEquals(f.Owner, owner)).ToArray())
                    RetirePersonaFeedNoLock(feed);
            }
        }

        internal static void RequestPersonaReconcile(PersonaFeed feed)
        {
            if (feed == null) return;
            // Owner close and worker arming use the same locks. A delayed
            // request cannot pass its check, then restart a closed owner.
            lock (_lock)
            lock (_personaFeedLock)
            {
                if (!IsCurrentPersonaFeedNoLock(feed, feed.Slot, feed.RouteGeneration)) return;
                PersonaReconcileRequest();
            }
        }

        internal static PersonaFeed SnapshotPersonaFeed(int slot, out int generation)
        {
            lock (_personaFeedLock)
            {
                generation = 0;
                if (!_personaFeeds.TryGetValue(slot, out var feed) || feed.Retired
                    || !feed.Published || feed.Owner.Closed) return null;
                generation = feed.RouteGeneration;
                return feed;
            }
        }

        private static PersonaFeed[] SnapshotPersonaFeeds()
        {
            lock (_personaFeedLock) return _personaFeeds.Values.ToArray();
        }

        private static KeyValuePair<int, PersonaFeed>[] SnapshotPersonaFeedEntries()
        {
            lock (_personaFeedLock) return _personaFeeds.ToArray();
        }

        private static bool IsCurrentPersonaFeed(PersonaFeed feed, int slot, int generation)
        {
            lock (_personaFeedLock) return IsCurrentPersonaFeedNoLock(feed, slot, generation);
        }

        private static bool IsCurrentPersonaFeedNoLock(PersonaFeed feed, int slot, int generation)
            => feed != null && feed.Published && !feed.Retired && !feed.Owner.Closed && feed.Slot == slot
                && feed.RouteGeneration == generation
                && _personaFeeds.TryGetValue(slot, out var current) && ReferenceEquals(current, feed);

        internal static void RetirePersonaFeed(PersonaFeed feed)
        {
            if (feed == null) return;
            lock (_personaFeedLock) RetirePersonaFeedNoLock(feed);
        }

        private static void RetirePersonaFeedNoLock(PersonaFeed feed)
        {
            Guid[] priorTargets;
            lock (feed.CallbackGate)
            {
                if (feed.Retired) return;
                feed.Retired = true;
                feed.Published = false;
                feed.RouteGeneration++;
                priorTargets = feed.Targets;
                feed.Targets = Array.Empty<Guid>();
                feed.RoutingPending = false;
            }
            if (_personaFeeds.TryGetValue(feed.Slot, out var current) && ReferenceEquals(current, feed))
                _personaFeeds.TryRemove(feed.Slot, out _);
            // SDK events are field-like. Unsubscribe outside the callback gate
            // so retirement does not depend on an event accessor taking no lock.
            try { feed.Audio.Output.FramesReceived -= feed.FramesHandler; } catch { }
            try { feed.Audio.ControlChanged -= feed.ControlHandler; } catch { }
            try { feed.Audio.Microphone.StreamingChanged -= feed.StreamingHandler; } catch { }
            ClearUnusedPersonaRingsNoLock(priorTargets);
            if (Interlocked.Exchange(ref feed.CleanupQueued, 1) == 0)
                _retiredPersonaFeeds.Enqueue(feed);
            RequestRetiredPersonaDrain();
            // Retirement wakes an existing reconcile worker. It never arms one.
            _workSignal.Set();
        }

        private static void ClearUnusedPersonaRingsNoLock(IEnumerable<Guid> targets)
        {
            _personaRingPruneCandidates.UnionWith(targets);
            // A moved feed has no active targets until its new route resolves.
            // Wait for the complete arrangement before declaring a ring unused.
            if (_personaRouteBatchDepth != 0
                || _personaFeeds.Values.Any(f => !f.Retired && f.RoutingPending)) return;
            foreach (var guid in _personaRingPruneCandidates)
            {
                if (_personaFeeds.Values.Any(f => !f.Retired && f.Targets.Contains(guid))) continue;
                _personaSpeakerRings.TryRemove(guid, out _);
                _personaHapticRings.TryRemove(guid, out _);
            }
            _personaRingPruneCandidates.Clear();
        }

        private static void RequestRetiredPersonaDrain()
        {
            if (Interlocked.CompareExchange(ref _personaDrainQueued, 1, 0) != 0) return;
            PersonaCleanupQueue(() =>
            {
                try { DrainRetiredPersonaFeeds(); }
                finally
                {
                    Volatile.Write(ref _personaDrainQueued, 0);
                    if (!_retiredPersonaFeeds.IsEmpty) RequestRetiredPersonaDrain();
                }
            });
        }

        internal static void DrainRetiredPersonaFeeds()
        {
            // Independent of _running, including the terminal drain requested
            // by Shutdown. Only resources committed into the feed are closed.
            lock (_personaIoGate)
            {
                while (_retiredPersonaFeeds.TryDequeue(out var feed))
                {
                    if (feed.NativeCleanupComplete) continue;
                    try { StopPersonaMic(feed); } catch { }
                    try { StopBtMic(feed); } catch { }
                    try { StopUsbJack(feed); } catch { }
                    feed.NativeCleanupComplete = true;
                }
            }
        }

        internal static void ReroutePersonaFeeds(IReadOnlyList<(PersonaFeed Feed, int Slot)> moves)
        {
            lock (_personaFeedLock)
            {
                var moving = moves.Where(m => m.Feed != null && !m.Feed.Retired && m.Feed.Slot != m.Slot
                    && _personaFeeds.TryGetValue(m.Feed.Slot, out var f) && ReferenceEquals(f, m.Feed)).ToArray();
                _personaRouteBatchDepth++;
                try
                {
                    foreach (var move in moving) _personaFeeds.TryRemove(move.Feed.Slot, out _);
                    foreach (var (feed, slot) in moving)
                    {
                        if (_personaFeeds.TryGetValue(slot, out var displaced) && !ReferenceEquals(displaced, feed))
                            RetirePersonaFeedNoLock(displaced);
                        lock (feed.CallbackGate)
                        {
                            _personaRingPruneCandidates.UnionWith(feed.Targets);
                            feed.Targets = Array.Empty<Guid>();
                            feed.RoutingPending = true;
                            feed.Slot = slot;
                            feed.RouteGeneration++;
                        }
                        _personaFeeds[slot] = feed;
                    }
                }
                finally { _personaRouteBatchDepth--; }
                // Do not prune until all moved feeds have resolved membership.
                ClearUnusedPersonaRingsNoLock(Array.Empty<Guid>());
            }
        }

        private static IWaveIn CreatePersonaMicCapture(string hidPath)
        {
            Guid container = NativeMethods.GetContainerIdForDevicePath(hidPath);
            if (container == Guid.Empty) return null;
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (GetEndpointContainerId(dev) == container) return new WasapiCapture(dev);
            return null;
        }

        private static void DisposePersonaCapture(IWaveIn capture)
        {
            if (capture == null) return;
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }
    }
}
