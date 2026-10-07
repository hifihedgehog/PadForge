using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>Connection state the service reports to its owner, who maps
    /// it to localized status text on the UI thread.</summary>
    public enum MouseHapticsState
    {
        Stopped,
        Searching,
        Active,
    }

    /// <summary>A mouse the service is sending rumble to. A null name is a
    /// Logitech device that did not give one. FeedbackOff means the mouse has
    /// haptic feedback turned off in its own settings.</summary>
    public readonly record struct MouseHapticTarget(string Name, bool FeedbackOff);

    /// <summary>
    /// How rumble strength becomes mouse haptics, shared by both transports.
    /// Three levels with hysteresis, so rumble that hovers on a boundary does
    /// not flip the level every tick. A Logitech mouse plays one waveform per
    /// pulse, chosen by level from the mask it reports, at an interval that
    /// shortens as rumble grows: 250 ms at the faintest, 80 ms at full
    /// strength, the cooldown mxhaptics ships for its impact events.
    /// </summary>
    internal static class MouseRumbleShaper
    {
        internal const float LightOn = 0.05f;
        internal const float LightOff = 0.03f;
        internal const float MediumOn = 0.33f;
        internal const float MediumOff = 0.30f;
        internal const float StrongOn = 0.66f;
        internal const float StrongOff = 0.63f;

        internal const int SlowestMs = 250;
        internal const int FastestMs = 80;

        internal static int Level(float amplitude, int current)
        {
            int up = amplitude >= StrongOn ? 3 : amplitude >= MediumOn ? 2 : amplitude >= LightOn ? 1 : 0;
            int down = amplitude >= StrongOff ? 3 : amplitude >= MediumOff ? 2 : amplitude >= LightOff ? 1 : 0;
            if (up > current) return up;
            if (down < current) return down;
            return current;
        }

        internal static int IntervalMs(float amplitude)
        {
            float t = (amplitude - LightOn) / (1f - LightOn);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return (int)MathF.Round(SlowestMs - t * (SlowestMs - FastestMs));
        }

        /// <summary>The collision waveform for a level, falling back to the
        /// nearest one the mouse lists. Null when it lists none of the three.</summary>
        internal static byte? Waveform(int level, uint mask)
        {
            if (level <= 0) return null;
            byte[] order = level switch
            {
                1 => new[] { HidppHapticProtocol.SubtleCollision, HidppHapticProtocol.DampCollision, HidppHapticProtocol.SharpCollision },
                2 => new[] { HidppHapticProtocol.DampCollision, HidppHapticProtocol.SubtleCollision, HidppHapticProtocol.SharpCollision },
                _ => new[] { HidppHapticProtocol.SharpCollision, HidppHapticProtocol.DampCollision, HidppHapticProtocol.SubtleCollision },
            };
            foreach (byte waveform in order)
                if ((mask & (1u << waveform)) != 0) return waveform;
            return null;
        }
    }

    /// <summary>
    /// Rumble on haptic mice (#494, asked in discussion #488): the Logitech
    /// MX Master 4 over HID++ (<see cref="HidppHapticProtocol"/>) and the
    /// SteelSeries Rival 500, 700 and 710 through GG's GameSense server
    /// (<see cref="GameSenseTactile"/>). The Razer Sensa lane's shape, leg
    /// for leg: a static amplitude the poll thread publishes, an armed flag
    /// that makes the publish one volatile read when the feature is off, one
    /// worker thread with the bounded predecessor join, and states the owner
    /// maps to text.
    ///
    /// <para>Discovery runs on the worker too. It waits for a quiet second
    /// whenever something is playing, so a probe never delays a pulse, and
    /// it finds any HID++ device that reports feature 0x19B0, with no model
    /// list.</para>
    /// </summary>
    public sealed class MouseHapticsService : IDisposable
    {
        /// <summary>The published rumble amplitude, 0..1, as float bits.</summary>
        private static int s_amplitudeBits;

        /// <summary>True while a worker runs.</summary>
        private static int s_publisherArmed;

        /// <summary>The most recent worker, joined by the next one before it
        /// arms: the Sensa lane's F10 rule, with its bounded wait.</summary>
        private static Thread s_lastWorker;

        internal const int DefaultPredecessorJoinMs = 10000;

        /// <summary>How long a Logitech scan waits when none is found.</summary>
        internal const int DefaultScanMs = 5000;

        /// <summary>A scan for more devices, a check that the found ones still
        /// answer and a refresh of their feedback setting, each at most this
        /// often and only in a quiet second.</summary>
        internal const int RescanMs = 30000;
        internal const int CheckMs = 30000;
        internal const int QuietMs = 1000;
        internal const int GameSenseRetryMs = 15000;

        internal const string GameSenseName = "SteelSeries GG";

        private sealed class LogitechEntry
        {
            public IHidppChannel Channel;
            public HidppPathState State;
            public readonly List<HidppHapticDevice> Devices = new();
            public readonly Dictionary<byte, long> NextPlay = new();
        }

        private readonly int _scanMs;
        private readonly int _tickMs;
        private readonly int _predecessorJoinMs;
        private readonly Func<IReadOnlyList<VendorHidCollection>> _enumerate;
        private readonly Func<VendorHidCollection, IHidppChannel> _openChannel;
        private readonly string _corePropsPath;
        private readonly int _httpTimeoutMs;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;
        private int _scans;

        /// <summary>Bumped whenever the target list changes, so the worker
        /// builds and reports the list only then. Worker thread only.</summary>
        private int _targetsVersion;

        /// <summary>Raised from the worker thread. The owner marshals to the
        /// UI thread.</summary>
        public event Action<MouseHapticsState, IReadOnlyList<MouseHapticTarget>> StateChanged;

        public MouseHapticsService(int scanMs = DefaultScanMs, int tickMs = 10)
            : this(scanMs, tickMs, DefaultPredecessorJoinMs, null, null, null, GameSenseTactile.DefaultHttpTimeoutMs) { }

        /// <summary>Test seam: the HID enumeration, the channel opener, the
        /// GameSense address file and the join deadline, so a bench drives
        /// the worker against fakes and a local server.</summary>
        internal MouseHapticsService(int scanMs, int tickMs, int predecessorJoinMs,
            Func<IReadOnlyList<VendorHidCollection>> enumerate,
            Func<VendorHidCollection, IHidppChannel> openChannel,
            string corePropsPath, int httpTimeoutMs)
        {
            _scanMs = scanMs;
            _tickMs = tickMs;
            _predecessorJoinMs = predecessorJoinMs;
            _enumerate = enumerate ?? EnumerateHidpp;
            _openChannel = openChannel ?? (c => HidppChannel.Open(c));
            _corePropsPath = corePropsPath;
            _httpTimeoutMs = httpTimeoutMs;
        }

        /// <summary>Whether the poll-thread publisher should bother.</summary>
        public static bool PublisherArmed => Volatile.Read(ref s_publisherArmed) != 0;

        /// <summary>Publishes the merged rumble amplitude (0..1).</summary>
        public static void PublishAmplitude(float amplitude)
            => Volatile.Write(ref s_amplitudeBits, BitConverter.SingleToInt32Bits(
                amplitude < 0f ? 0f : (amplitude > 1f ? 1f : amplitude)));

        /// <summary>The engine's silence edge: stop, idle entry and focus
        /// suspend, where the publishing lane does not run.</summary>
        public static void Silence() => Volatile.Write(ref s_amplitudeBits, 0);

        internal static float PublishedAmplitude
            => BitConverter.Int32BitsToSingle(Volatile.Read(ref s_amplitudeBits));

        /// <summary>Max of the four packed feedback voices, normalized 0..1,
        /// the same reduction <see cref="SensaHapticsService.PackToAmplitude"/>
        /// makes.</summary>
        public static float PackToAmplitude(long pack)
        {
            ushort a = (ushort)(pack & 0xFFFF);
            ushort b = (ushort)((pack >> 16) & 0xFFFF);
            ushort c = (ushort)((pack >> 32) & 0xFFFF);
            ushort d = (ushort)((pack >> 48) & 0xFFFF);
            int max = Math.Max(Math.Max(a, b), Math.Max(c, d));
            return max / 65535f;
        }

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>Logitech scans this worker ran.</summary>
        internal int ScanCount => Volatile.Read(ref _scans);

        /// <summary>The HID++ long collections present: vendor 046D, usage
        /// page 0xFF00 usage 2, a 20-byte report. Null when enumeration
        /// itself failed.</summary>
        private static IReadOnlyList<VendorHidCollection> EnumerateHidpp()
            => VendorHidRuntime.Enumerate()?
                .Where(c => c.VendorId == HidppHapticProtocol.VendorId
                            && c.UsagePage == HidppHapticProtocol.UsagePage
                            && c.Usage == HidppHapticProtocol.LongUsage
                            && c.InputReportLength == HidppHapticProtocol.LongReportLength)
                .ToList();

        public void Start()
        {
            if (_thread != null) return; // Already started.
            _stop = false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "MouseHaptics" };
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop = true;
            try { _thread.Join(3000); } catch { }
            _thread = null;
        }

        private void Report(MouseHapticsState state, IReadOnlyList<MouseHapticTarget> targets)
        {
            try { StateChanged?.Invoke(state, targets); } catch { }
        }

        private void Worker()
        {
            var entries = new Dictionary<string, LogitechEntry>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, HidppPathState>(StringComparer.OrdinalIgnoreCase);
            GameSenseTactile gameSense = null;
            try
            {
                // The Sensa lane's predecessor join (SensaHapticsService.Worker):
                // a worker that outlived its Stop disarms the publisher in its
                // finally, which must land before this one arms. On the
                // deadline this worker hands the slot back and quits.
                var prev = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
                if (prev != null && prev != Thread.CurrentThread && prev.IsAlive
                    && !prev.Join(_predecessorJoinMs))
                {
                    Interlocked.CompareExchange(ref s_lastWorker, prev, Thread.CurrentThread);
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"MOUSEHAPTICS predecessor join timed out after {_predecessorJoinMs} ms, worker quitting without arming");
                    return; // finally reports Stopped.
                }
                Volatile.Write(ref s_publisherArmed, 1);
                gameSense = new GameSenseTactile(_corePropsPath, _httpTimeoutMs);

                long start = Environment.TickCount64;
                long nextScan = start, nextGameSense = start, nextCheck = start + CheckMs, quietSince = start;
                int level = 0;
                int reportedVersion = -1;
                var paths = new List<string>();

                while (!_stop)
                {
                    long now = Environment.TickCount64;
                    float amplitude = PublishedAmplitude;
                    level = MouseRumbleShaper.Level(amplitude, level);
                    if (level > 0) quietSince = now;
                    bool quiet = now - quietSince >= QuietMs;
                    bool playing = entries.Count > 0 || gameSense.Connected;

                    if (now >= nextScan && (!playing || quiet))
                    {
                        Scan(entries, states, now);
                        nextScan = Environment.TickCount64 + (entries.Count == 0 ? _scanMs : RescanMs);
                    }
                    if (!gameSense.Connected && now >= nextGameSense && (!playing || quiet))
                    {
                        if (gameSense.TryConnect(now))
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine("MOUSEHAPTICS GameSense bound");
                            _targetsVersion++;
                        }
                        else
                        {
                            nextGameSense = Environment.TickCount64 + GameSenseRetryMs;
                        }
                    }
                    if (now >= nextCheck && quiet)
                    {
                        Check(entries, states);
                        nextCheck = Environment.TickCount64 + CheckMs;
                    }

                    now = Environment.TickCount64;
                    if (entries.Count > 0)
                    {
                        paths.Clear();
                        paths.AddRange(entries.Keys);
                        foreach (var path in paths)
                        {
                            if (Play(entries[path], level, amplitude, now)) continue;
                            PadForge.Engine.SdlDiagLog.WriteLine("MOUSEHAPTICS write failed, dropping " + path);
                            Close(entries, states, path, forget: true);
                            nextScan = now;
                        }
                    }
                    if (gameSense.Connected && !gameSense.Render(level, now))
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine("MOUSEHAPTICS GameSense stopped answering");
                        nextGameSense = now + GameSenseRetryMs;
                        _targetsVersion++;
                    }

                    // The list is built only when it changed, not every tick.
                    if (_targetsVersion != reportedVersion)
                    {
                        reportedVersion = _targetsVersion;
                        var targets = Targets(entries, gameSense);
                        Report(targets.Count > 0 ? MouseHapticsState.Active : MouseHapticsState.Searching, targets);
                    }

                    Thread.Sleep(_tickMs);
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("MOUSEHAPTICS worker fault: " + ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref s_publisherArmed, 0);
                Volatile.Write(ref s_amplitudeBits, 0);
                foreach (var entry in entries.Values)
                {
                    try { entry.Channel.Dispose(); } catch { }
                }
                try { gameSense?.Dispose(); } catch { }
                Report(MouseHapticsState.Stopped, Array.Empty<MouseHapticTarget>());
            }
        }

        private static List<MouseHapticTarget> Targets(Dictionary<string, LogitechEntry> entries, GameSenseTactile gameSense)
        {
            var targets = new List<MouseHapticTarget>();
            foreach (var entry in entries.Values)
                foreach (var device in entry.Devices)
                    targets.Add(new MouseHapticTarget(device.Name, !device.FeedbackEnabled));
            if (gameSense.Connected) targets.Add(new MouseHapticTarget(GameSenseName, false));
            return targets;
        }

        /// <summary>One pulse per device when its interval is up. Silence
        /// resets the interval, so the first pulse of new rumble plays at
        /// once. False when a write failed.</summary>
        private static bool Play(LogitechEntry entry, int level, float amplitude, long now)
        {
            foreach (var device in entry.Devices)
            {
                if (level == 0)
                {
                    entry.NextPlay[device.DeviceIndex] = 0;
                    continue;
                }
                entry.NextPlay.TryGetValue(device.DeviceIndex, out long next);
                if (now < next) continue;
                byte? waveform = MouseRumbleShaper.Waveform(level, device.WaveformMask);
                if (waveform == null) continue;
                if (!entry.Channel.Write(HidppHapticProtocol.Play(device.DeviceIndex, device.HapticIndex, waveform.Value)))
                    return false;
                entry.NextPlay[device.DeviceIndex] = now + MouseRumbleShaper.IntervalMs(amplitude);
            }
            return true;
        }

        private void Scan(Dictionary<string, LogitechEntry> entries, Dictionary<string, HidppPathState> states, long now)
        {
            Interlocked.Increment(ref _scans);
            var collections = _enumerate();
            if (collections == null) return;

            var present = new HashSet<string>(collections.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in states.Keys.Where(p => !present.Contains(p)).ToList())
                Close(entries, states, path, forget: true);

            foreach (var collection in collections)
            {
                if (_stop) return;
                if (!states.TryGetValue(collection.Path, out var state))
                    states[collection.Path] = state = new HidppPathState();
                bool open = entries.TryGetValue(collection.Path, out var entry);
                if (!HasQuestions(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path))) continue;

                var channel = open ? entry.Channel : _openChannel(collection);
                if (channel == null) continue;
                var found = HidppHapticProbe.Probe(channel, state, now);
                // A device that lists none of the three collisions has
                // nothing rumble can play. Its slot stays answered.
                foreach (var mute in found.Where(d => MouseRumbleShaper.Waveform(1, d.WaveformMask) == null))
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"MOUSEHAPTICS '{mute.Name}' lists no collision waveform, mask=0x{mute.WaveformMask:X8}");
                found.RemoveAll(d => MouseRumbleShaper.Waveform(1, d.WaveformMask) == null);
                if (found.Count > 0)
                {
                    if (!open)
                    {
                        entry = new LogitechEntry { Channel = channel, State = state };
                        entries[collection.Path] = entry;
                    }
                    entry.Devices.AddRange(found);
                    _targetsVersion++;
                    foreach (var device in found)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"MOUSEHAPTICS found '{device.Name}' index=0x{device.DeviceIndex:X2} feature=0x{device.HapticIndex:X2} mask=0x{device.WaveformMask:X8} feedback={(device.FeedbackEnabled ? "on" : "off")}");
                }
                else if (!open)
                {
                    channel.Dispose();
                }

                if (state.Dead) Close(entries, states, collection.Path, forget: true);
            }
        }

        /// <summary>Whether a scan has anything to ask this path: a slot not
        /// yet answered whose backoff is over.</summary>
        private static bool HasQuestions(HidppPathState state, long now, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (!state.Settled[slot] && now >= state.RetryAt[slot]) return true;
            return false;
        }

        /// <summary>Asks each found device for its configuration. One that
        /// answers refreshes its feedback setting. One that does not went to
        /// sleep or away: it leaves the list and its slot reopens, so the next
        /// scan finds it again once it answers.</summary>
        private void Check(Dictionary<string, LogitechEntry> entries, Dictionary<string, HidppPathState> states)
        {
            foreach (var path in entries.Keys.ToList())
            {
                if (_stop) return;
                var entry = entries[path];
                for (int i = entry.Devices.Count - 1; i >= 0; i--)
                {
                    var device = entry.Devices[i];
                    bool? enabled = HidppHapticProbe.ReadFeedbackEnabled(entry.Channel, device);
                    if (enabled == null)
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine($"MOUSEHAPTICS '{device.Name}' stopped answering");
                        entry.Devices.RemoveAt(i);
                        entry.NextPlay.Remove(device.DeviceIndex);
                        entry.State.Reopen(device.DeviceIndex);
                        _targetsVersion++;
                    }
                    else if (enabled.Value != device.FeedbackEnabled)
                    {
                        entry.Devices[i] = device with { FeedbackEnabled = enabled.Value };
                        _targetsVersion++;
                    }
                }
                if (entry.Devices.Count == 0) Close(entries, states, path, forget: false);
            }
        }

        /// <summary>Closes a path's channel. Forgetting also drops what the
        /// path answered, for one that vanished or failed.</summary>
        private void Close(Dictionary<string, LogitechEntry> entries, Dictionary<string, HidppPathState> states,
            string path, bool forget)
        {
            if (entries.Remove(path, out var entry))
            {
                try { entry.Channel.Dispose(); } catch { }
                _targetsVersion++;
            }
            if (forget) states.Remove(path);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
