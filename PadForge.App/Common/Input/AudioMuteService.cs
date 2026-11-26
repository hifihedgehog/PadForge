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
            => TryEnumerate(out EndpointInfo[] endpoints) ? endpoints : Array.Empty<EndpointInfo>();

        private static bool TryEnumerate(out EndpointInfo[] endpoints)
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
                endpoints = list
                    .OrderBy(e => e.IsInput ? 0 : 1)  // inputs first
                    .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return true;
            }
            catch
            {
                endpoints = Array.Empty<EndpointInfo>();
                return false;
            }
        }

        // ── The endpoints' names, kept until Windows reports a change ──

        private static string[] _endpointNames;
        private static bool _endpointNamesCurrent;
        private static NameWatch _nameWatch;

        /// <summary>
        /// The friendly names of every active input and output endpoint, as
        /// the privacy scrubber searches every read and recording for them
        /// (#600). They are read once and kept until Windows reports an
        /// endpoint added, removed, changed in state or changed in a
        /// property, a rename among them, through the endpoint notification
        /// AudioBassDetector takes as well (IMMNotificationClient). A read
        /// is a call into Windows' audio service: it took 371 ms at the
        /// median and up to 584 ms under a load of one busy process per core,
        /// on the line handler of every Automation read that refreshed the
        /// scrubber. A failed read, or a notification that could not be
        /// registered, keeps nothing, so the next call reads again.
        /// </summary>
        public static string[] EndpointNames()
        {
            lock (_gate)
            {
                if (_endpointNamesCurrent && _endpointNames != null) return _endpointNames;
            }
            bool watched = WatchNames();
            lock (_gate)
            {
                // Current from before the read: a change Windows reports
                // during it marks the names stale again.
                _endpointNamesCurrent = watched;
                _endpointNames = null;
            }
            if (!TryEnumerate(out EndpointInfo[] endpoints))
            {
                lock (_gate) _endpointNamesCurrent = false;
                return Array.Empty<string>();
            }
            var names = new string[endpoints.Length];
            for (int i = 0; i < endpoints.Length; i++) names[i] = endpoints[i].FriendlyName;
            lock (_gate)
            {
                if (_endpointNamesCurrent) _endpointNames = names;
            }
            return names;
        }

        /// <summary>Registers the notification once. False when Windows
        /// refused it.</summary>
        private static bool WatchNames()
        {
            lock (_gate)
            {
                if (_nameWatch != null) return true;
            }
            var watch = new NameWatch();
            try
            {
                if (GetEnumerator().RegisterEndpointNotificationCallback(watch) != 0) return false;
            }
            catch
            {
                return false;
            }
            lock (_gate)
            {
                if (_nameWatch == null)
                {
                    _nameWatch = watch;
                    return true;
                }
            }
            // Another caller registered first: one registration stands.
            try { GetEnumerator().UnregisterEndpointNotificationCallback(watch); } catch { }
            return true;
        }

        /// <summary>Marks the kept names stale. Windows calls it on its own
        /// thread.</summary>
        private static void NamesChanged()
        {
            lock (_gate)
            {
                _endpointNamesCurrent = false;
                _endpointNames = null;
            }
        }

        /// <summary>Test seam: what Windows' notification does.</summary>
        internal static void NamesChangedForTest() => NamesChanged();

        /// <summary>Whether names are kept now, for tests.</summary>
        internal static bool EndpointNamesKept
        {
            get { lock (_gate) return _endpointNamesCurrent && _endpointNames != null; }
        }

        /// <summary>Every report that can change an endpoint's name or the
        /// set of active endpoints marks the kept names stale. A new default
        /// device changes neither.</summary>
        private sealed class NameWatch : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
        {
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => NamesChanged();
            public void OnDeviceAdded(string pwstrDeviceId) => NamesChanged();
            public void OnDeviceRemoved(string deviceId) => NamesChanged();
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => NamesChanged();
        }

        /// <summary>The PC's default microphone, Windows' default capture
        /// endpoint, as its endpoint id. Empty when there is none or the
        /// audio stack cannot say.</summary>
        public static string DefaultCaptureId()
        {
            try
            {
                using var device = GetEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                return device?.ID ?? string.Empty;
            }
            catch
            {
                return string.Empty;
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
                List<string> stale = null;
                foreach (var kv in _lastTouchTicks)
                    if ((now - kv.Value) >= StaleSubscriptionMs)
                        (stale ??= new List<string>()).Add(kv.Key);
                if (stale != null)
                    foreach (var id in stale)
                    {
                        _lastTouchTicks.Remove(id);
                        _muteCache.Remove(id);
                        if (_devices.Remove(id, out var staleDev)) staleDev?.Dispose();
                    }

                // Empty subscription set: stop ticking entirely. The next
                // FollowDeviceMute query re-arms via EnsurePollTimer, so
                // this 4 Hz timer no longer runs forever (with per-tick
                // LINQ) after its first-ever use.
                if (_lastTouchTicks.Count == 0)
                {
                    _pollTimer?.Dispose();
                    _pollTimer = null;
                    return;
                }
                active = new string[_lastTouchTicks.Count];
                _lastTouchTicks.Keys.CopyTo(active, 0);
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
