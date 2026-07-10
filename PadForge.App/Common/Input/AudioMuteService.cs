using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Thin cache over CoreAudio's per-endpoint mute state, used by the
    /// DualSense mic-LED FollowDeviceMute mode. The synthesizer reads
    /// <see cref="GetMuteState"/> at 30 Hz; this service keeps a 4 Hz
    /// background poll on every device id that's been queried recently
    /// so the synthesizer never blocks on a COM call.
    ///
    /// <para>Design: subscription is implicit. Calling
    /// <see cref="GetMuteState"/> registers the device id as "active";
    /// the poll timer refreshes any active id every 250 ms. After 30 s
    /// without a query the id is dropped from the active set so an
    /// unplugged device or an unused config doesn't keep an MMDevice
    /// reference alive forever.</para>
    /// </summary>
    public static class AudioMuteService
    {
        public readonly record struct EndpointInfo(string Id, string FriendlyName, bool IsInput);

        private const int PollIntervalMs = 250;
        private const long StaleSubscriptionMs = 30_000;

        private static readonly object _gate = new();
        private static readonly Dictionary<string, MMDevice> _devices = new();
        private static readonly Dictionary<string, bool> _muteCache = new();
        private static readonly Dictionary<string, long> _lastTouchTicks = new();
        private static MMDeviceEnumerator _enumerator;
        private static Timer _pollTimer;

        /// <summary>Enumerate every active input + output endpoint.
        /// Re-enumerates on every call — populating a settings-page combo
        /// box should not require an explicit refresh button. Returns
        /// empty on any failure (no audio stack, COM unavailable, etc.).</summary>
        public static EndpointInfo[] EnumerateEndpoints()
        {
            try
            {
                var collection = GetEnumerator()
                    .EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
                var list = new List<EndpointInfo>(collection.Count);
                foreach (var d in collection)
                {
                    using (d)
                    {
                        bool isInput = d.DataFlow == DataFlow.Capture;
                        list.Add(new EndpointInfo(d.ID, d.FriendlyName, isInput));
                    }
                }
                return list
                    .OrderBy(e => e.IsInput ? 0 : 1)  // inputs first
                    .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<EndpointInfo>();
            }
        }

        /// <summary>Resolve a device-id string to its current mute state.
        /// Returns null when the id is empty, the device is gone, or the
        /// COM call fails. The first call for a new id reads synchronously
        /// so the caller doesn't see null for a full poll interval; every
        /// subsequent call returns cached state refreshed by the
        /// background timer.</summary>
        public static bool? GetMuteState(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;

            EnsurePollTimer();
            long now = Environment.TickCount64;
            lock (_gate)
            {
                _lastTouchTicks[deviceId] = now;
                if (_muteCache.TryGetValue(deviceId, out var cached)) return cached;
            }

            try
            {
                var dev = ResolveDevice(deviceId);
                if (dev == null) return null;
                bool muted = dev.AudioEndpointVolume.Mute;
                lock (_gate) _muteCache[deviceId] = muted;
                return muted;
            }
            catch
            {
                return null;
            }
        }

        private static MMDevice ResolveDevice(string deviceId)
        {
            lock (_gate)
            {
                if (_devices.TryGetValue(deviceId, out var cached)) return cached;
            }
            try
            {
                var dev = GetEnumerator().GetDevice(deviceId);
                lock (_gate)
                {
                    // The GetMuteState (30 Hz) and Poll (4 Hz) threads can both miss
                    // the cache and resolve the same id concurrently. Keep whichever
                    // landed first and dispose our loser, so the extra MMDevice (a COM
                    // reference) doesn't leak until process exit.
                    if (_devices.TryGetValue(deviceId, out var raced))
                    {
                        dev?.Dispose();
                        return raced;
                    }
                    _devices[deviceId] = dev;
                }
                return dev;
            }
            catch
            {
                return null;
            }
        }

        private static MMDeviceEnumerator GetEnumerator()
        {
            if (_enumerator != null) return _enumerator;
            lock (_gate) _enumerator ??= new MMDeviceEnumerator();
            return _enumerator;
        }

        private static void EnsurePollTimer()
        {
            if (_pollTimer != null) return;
            lock (_gate)
            {
                _pollTimer ??= new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
            }
        }

        private static void Poll()
        {
            string[] active;
            long now = Environment.TickCount64;
            lock (_gate)
            {
                // Drop subscriptions that haven't been touched in 30 s so
                // an MMDevice reference doesn't outlive the consumer.
                var stale = _lastTouchTicks
                    .Where(kv => (now - kv.Value) >= StaleSubscriptionMs)
                    .Select(kv => kv.Key).ToList();
                foreach (var id in stale)
                {
                    _lastTouchTicks.Remove(id);
                    _muteCache.Remove(id);
                    if (_devices.Remove(id, out var staleDev)) staleDev?.Dispose();
                }
                active = _lastTouchTicks.Keys.ToArray();
            }

            foreach (var id in active)
            {
                try
                {
                    var dev = ResolveDevice(id);
                    if (dev == null) continue;
                    bool muted = dev.AudioEndpointVolume.Mute;
                    lock (_gate) _muteCache[id] = muted;
                }
                catch
                {
                    lock (_gate)
                    {
                        _muteCache.Remove(id);
                        if (_devices.Remove(id, out var badDev)) badDev?.Dispose();
                    }
                }
            }
        }
    }
}
