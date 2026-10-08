using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>What the HID++ worker has found, swapped whole so the linker
    /// never reads half a scan.</summary>
    internal sealed class HidppSnapshot
    {
        public static readonly HidppSnapshot Empty = new(Array.Empty<HidppUnit>(), Array.Empty<Guid>(), scanned: false);

        public IReadOnlyList<HidppUnit> Units { get; }

        /// <summary>Containers with a slot still to be asked: a device there
        /// may yet answer, so a row there keeps the outputs it had.</summary>
        public IReadOnlyCollection<Guid> PendingContainers { get; }

        /// <summary>False until the worker's first scan has run, so nothing
        /// it has not looked for reads as missing.</summary>
        public bool Scanned { get; }

        public HidppSnapshot(IReadOnlyList<HidppUnit> units, IReadOnlyCollection<Guid> pending, bool scanned = true)
        {
            Units = units;
            PendingContainers = pending;
            Scanned = scanned;
        }
    }

    /// <summary>
    /// The Logitech HID++ worker (#494). One thread owns every HID++
    /// collection, so a probe, a haptic pulse and a color never interleave
    /// on one channel. The first build's worker shape (6b0eeb3c removed its
    /// global lane), with the targets taken from the link table instead of
    /// every device found:
    ///
    /// <para>A unit plays rumble when a row linked to it has a level, the
    /// strongest when two rows link it. The shaper picks the waveform and
    /// the repeat interval (<see cref="HapticRumbleShaper"/>), and silence
    /// resets the interval so the first pulse of new rumble plays at once.</para>
    ///
    /// <para>Discovery waits for a quiet second whenever something plays, so
    /// a probe never delays a pulse. It finds any device that answers, with
    /// no device list, and asks a silent slot again after 5, 10, then 15
    /// seconds. On a Bolt, Unifying or LIGHTSPEED receiver it first reads
    /// the receiver's pairing table, so a slot with no device is never asked
    /// and never holds its rows' outputs (<see cref="HidppReceiverProtocol"/>).
    /// A found device is asked for its configuration every 30 seconds while
    /// quiet: one that does not answer went to sleep, and its slot reopens so
    /// the next scan finds it when it wakes.</para>
    /// </summary>
    internal sealed class HidppBackend : IDisposable
    {
        internal const int FirstScanMs = 5000;
        internal const int RescanMs = 30000;
        internal const int CheckMs = 30000;
        internal const int QuietMs = 1000;

        /// <summary>The worker's cadence while a unit is linked for rumble,
        /// and while nothing is.</summary>
        internal const int ActiveTickMs = 10;
        internal const int IdleTickMs = 100;

        private sealed class Channel
        {
            public IHidppChannel Io;
            public HidppPathState State;
            public Guid Container;
            public readonly List<HidppUnit> Units = new();
            public readonly Dictionary<byte, (int Level, long NextPlay)> Play = new();
        }

        private readonly Func<IReadOnlyList<VendorHidCollection>> _enumerate;
        private readonly Func<VendorHidCollection, VendorHidCollection, IHidppChannel> _open;
        private readonly Func<string, Guid> _containerOf;
        private readonly int _activeTickMs;
        private readonly int _idleTickMs;
        private readonly AutoResetEvent _wake = new(false);

        /// <summary>Each present path's container ID, looked up once per
        /// appearance. Worker thread only.</summary>
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private HidppSnapshot _snapshot = HidppSnapshot.Empty;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;
        private int _scans;

        public HidppBackend()
            : this(null, null, null, ActiveTickMs, IdleTickMs) { }

        /// <summary>Test seam: the enumeration (every vendor collection, the
        /// long ones and their short siblings), the channel opener (a long
        /// collection and its receiver's short one, or null) and the
        /// container lookup, so a bench drives the worker against scripted
        /// channels.</summary>
        internal HidppBackend(Func<IReadOnlyList<VendorHidCollection>> enumerate,
            Func<VendorHidCollection, VendorHidCollection, IHidppChannel> open, Func<string, Guid> containerOf,
            int activeTickMs, int idleTickMs)
        {
            _enumerate = enumerate ?? VendorHidRuntime.Enumerate;
            _open = open ?? ((c, s) => HidppChannel.Open(c, s));
            _containerOf = containerOf ?? AudioPassthroughService.DevicePathContainerId;
            _activeTickMs = activeTickMs;
            _idleTickMs = idleTickMs;
        }

        public HidppSnapshot Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>Scans this worker ran.</summary>
        internal int ScanCount => Volatile.Read(ref _scans);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>A HID++ long collection. Mice and receivers on usage page
        /// 0xFF00 usage 2, keyboards on 0xFF43 usage 0x0602, both with the
        /// 20-byte report (OpenRGB LogitechControllerDetect.cpp:162-166 and
        /// 574-587).</summary>
        internal static bool IsHidppLong(VendorHidCollection c)
            => c.VendorId == HidppHapticProtocol.VendorId
               && c.InputReportLength == HidppHapticProtocol.LongReportLength
               && ((c.UsagePage == HidppHapticProtocol.UsagePage && c.Usage == HidppHapticProtocol.LongUsage)
                   || (c.UsagePage == 0xFF43 && c.Usage == 0x0602));

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "PeripheralHidpp" };
            _thread.Start();
            PeripheralOutputs.HapticsChanged += Wake;
        }

        public void Stop()
        {
            PeripheralOutputs.HapticsChanged -= Wake;
            if (_thread == null) return;
            _stop = true;
            _wake.Set();
            try { _thread.Join(3000); } catch { }
            _thread = null;
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void Worker()
        {
            var channels = new Dictionary<string, Channel>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, HidppPathState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                long start = Environment.TickCount64;
                long nextScan = start, nextCheck = start + CheckMs, quietSince = start;
                while (!_stop)
                {
                    long now = Environment.TickCount64;
                    var links = PeripheralOutputs.Links;
                    bool anyLinked = PlayHaptics(channels, states, links, now, out bool playing, ref nextScan);
                    if (playing) quietSince = now;
                    bool quiet = now - quietSince >= QuietMs;

                    if (now >= nextScan && (!playing || quiet))
                    {
                        Scan(channels, states, now);
                        // A slot still waiting for its device, one asleep at
                        // launch or one that went to sleep, is asked again on
                        // its own backoff, so the scan comes as often as that
                        // can matter. With nothing waiting, a slow rescan finds
                        // what gets plugged in or paired.
                        nextScan = Environment.TickCount64 + (AnyWaiting(states) ? FirstScanMs : RescanMs);
                    }
                    if (now >= nextCheck && quiet)
                    {
                        // A device that went to sleep is asked for again on
                        // the fast cadence, so it is found soon after it wakes.
                        if (Check(channels, states))
                            nextScan = Math.Min(nextScan, Environment.TickCount64 + FirstScanMs);
                        nextCheck = Environment.TickCount64 + CheckMs;
                    }

                    _wake.WaitOne(anyLinked ? _activeTickMs : _idleTickMs);
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ worker fault: " + ex.GetType().Name);
            }
            finally
            {
                foreach (var channel in channels.Values)
                {
                    try { channel.Io.Dispose(); } catch { }
                }
                Volatile.Write(ref _snapshot, HidppSnapshot.Empty);
            }
        }

        /// <summary>One pass of pulses. True when any unit is linked for
        /// rumble, which keeps the worker on its fast tick.</summary>
        private bool PlayHaptics(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            LinkTable links, long now, out bool playing, ref long nextScan)
        {
            playing = false;
            bool anyLinked = false;
            List<string> dead = null;
            foreach (var pair in channels)
            {
                var channel = pair.Value;
                foreach (var unit in channel.Units)
                {
                    if (!unit.HasHaptics) continue;
                    var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
                    if (!links.ByPath.TryGetValue(path, out var devices)) continue;
                    anyLinked = true;
                    float amplitude = 0f;
                    foreach (var device in devices)
                    {
                        var row = links.For(device);
                        if (row == null || Array.IndexOf(row.Haptics, path) < 0) continue;
                        float a = PeripheralOutputs.AmplitudeOf(device);
                        if (a > amplitude) amplitude = a;
                    }
                    channel.Play.TryGetValue(unit.DeviceIndex, out var play);
                    int level = HapticRumbleShaper.Level(amplitude, play.Level);
                    if (level == 0)
                    {
                        channel.Play[unit.DeviceIndex] = (0, 0);
                        continue;
                    }
                    playing = true;
                    if (now < play.NextPlay)
                    {
                        channel.Play[unit.DeviceIndex] = (level, play.NextPlay);
                        continue;
                    }
                    byte? waveform = HapticRumbleShaper.Waveform(level, unit.WaveformMask);
                    if (waveform == null) continue;
                    if (!channel.Io.Write(HidppHapticProtocol.Play(unit.DeviceIndex, unit.HapticIndex, waveform.Value)))
                    {
                        (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                    channel.Play[unit.DeviceIndex] = (level, now + HapticRumbleShaper.IntervalMs(amplitude));
                }
            }
            if (dead != null)
            {
                foreach (var path in dead)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL HID++ write failed, dropping " + path);
                    Close(channels, states, path, forget: true);
                }
                nextScan = now;
                Publish(channels, states);
            }
            return anyLinked;
        }

        private void Scan(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states, long now)
        {
            Interlocked.Increment(ref _scans);
            var all = _enumerate();
            if (all == null) return;
            var collections = all.Where(IsHidppLong).ToList();

            var present = new HashSet<string>(collections.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in states.Keys.Where(p => !present.Contains(p)).ToList())
            {
                Close(channels, states, path, forget: true);
                _containers.Remove(path);
            }

            foreach (var collection in collections)
            {
                if (_stop) return;
                if (!states.TryGetValue(collection.Path, out var state))
                {
                    states[collection.Path] = state = new HidppPathState
                    {
                        ReceiverKind = collection.UsagePage == HidppHapticProtocol.UsagePage
                            ? HidppReceiverProtocol.KindOf(collection.ProductId)
                            : HidppReceiverKind.None,
                    };
                }
                if (!_containers.TryGetValue(collection.Path, out var container))
                {
                    try { container = _containerOf(collection.Path); }
                    catch { container = Guid.Empty; }
                    _containers[collection.Path] = container;
                }
                // A known receiver's pairing table is read at every scan, so a
                // device paired since the last one is found.
                bool pairing = state.ReceiverKind != HidppReceiverKind.None
                               && state.PairingMisses < HidppUnitProbe.PairingGiveUp;
                if (!pairing && !HasQuestions(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path))) continue;

                bool open = channels.TryGetValue(collection.Path, out var channel);
                var io = open ? channel.Io
                    : _open(collection, state.ReceiverKind != HidppReceiverKind.None
                        ? HidppReceiverProtocol.ShortSibling(collection, all)
                        : null);
                if (io == null)
                {
                    HidppUnitProbe.MissOpenSlots(state, now, HidppHapticProtocol.IsBluetoothPath(collection.Path));
                    continue;
                }
                var found = HidppUnitProbe.Probe(io, state, container, now);
                if (found.Count > 0)
                {
                    if (!open)
                    {
                        channel = new Channel { Io = io, State = state, Container = container };
                        channels[collection.Path] = channel;
                    }
                    channel.Units.AddRange(found);
                    foreach (var unit in found)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"PERIPHERAL HID++ found '{unit.Name}' index=0x{unit.DeviceIndex:X2} type={unit.DeviceType}"
                            + $" haptic=0x{unit.HapticIndex:X2} mask=0x{unit.WaveformMask:X8}"
                            + $" rgb={unit.RgbFeatureId:X4} zones={unit.Zones.Length} battery={unit.BatteryFeatureId:X4}");
                }
                else if (!open)
                {
                    io.Dispose();
                }

                if (state.Dead) Close(channels, states, collection.Path, forget: true);
            }
            // A slot that settled changes what the linker may conclude even
            // with nothing found, so every scan publishes.
            Publish(channels, states);
        }

        /// <summary>Whether a scan has anything to ask this path: a slot not
        /// yet answered whose backoff is over.</summary>
        private static bool HasQuestions(HidppPathState state, long now, bool bluetooth)
        {
            int first = state.Receiver ? HidppHapticProtocol.FirstReceiverIndex : 0;
            int last = state.Direct || bluetooth ? 0 : HidppHapticProtocol.LastReceiverIndex;
            for (int slot = first; slot <= last; slot++)
                if (state.Open(slot) && now >= state.RetryAt[slot]) return true;
            return false;
        }

        /// <summary>Whether any present path has a slot still waiting for
        /// its device.</summary>
        private static bool AnyWaiting(Dictionary<string, HidppPathState> states)
        {
            foreach (var pair in states)
                if (!pair.Value.Dead && HidppUnitProbe.Pending(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key)))
                    return true;
            return false;
        }

        /// <summary>Asks each found unit something cheap: a haptic unit its
        /// configuration, any other its name feature. One that does not
        /// answer went to sleep or away. It leaves the list and its slot
        /// reopens, so the next scan finds it again once it answers. True
        /// when a slot reopened.</summary>
        private bool Check(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            bool changed = false;
            bool reopened = false;
            foreach (var path in channels.Keys.ToList())
            {
                if (_stop) return reopened;
                var channel = channels[path];
                for (int i = channel.Units.Count - 1; i >= 0; i--)
                {
                    var unit = channel.Units[i];
                    bool? alive;
                    if (unit.HapticIndex != 0)
                    {
                        bool? enabled = HidppHapticProbe.ReadFeedbackEnabled(channel.Io, unit.DeviceIndex, unit.HapticIndex);
                        alive = enabled != null;
                        if (enabled != null && enabled.Value != unit.FeedbackEnabled)
                        {
                            channel.Units[i] = unit with { FeedbackEnabled = enabled.Value };
                            changed = true;
                        }
                    }
                    else
                    {
                        var reply = channel.Io.Request(unit.DeviceIndex, 0x00, 0,
                            new byte[] { 0x00, 0x05 }, HidppHapticProtocol.TimeoutMs(channel.Io.Bluetooth));
                        // A receiver's HID++ 1.0 error means the device is
                        // out of reach, as in ReadFeedbackEnabled.
                        alive = reply.Kind == HidppReplyKind.Answer
                                || (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10);
                    }
                    if (alive == true) continue;
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL HID++ '{unit.Name}' stopped answering");
                    channel.Units.RemoveAt(i);
                    channel.Play.Remove(unit.DeviceIndex);
                    channel.State.Reopen(unit.DeviceIndex);
                    changed = true;
                    reopened = true;
                }
                if (channel.Units.Count == 0)
                {
                    Close(channels, states, path, forget: false);
                    changed = true;
                }
            }
            if (changed) Publish(channels, states);
            return reopened;
        }

        /// <summary>Closes a path's channel. Forgetting also drops what the
        /// path answered, for one that vanished or failed.</summary>
        private static void Close(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states,
            string path, bool forget)
        {
            if (channels.Remove(path, out var channel))
            {
                try { channel.Io.Dispose(); } catch { }
            }
            if (forget) states.Remove(path);
        }

        private void Publish(Dictionary<string, Channel> channels, Dictionary<string, HidppPathState> states)
        {
            var units = new List<HidppUnit>();
            foreach (var channel in channels.Values) units.AddRange(channel.Units);
            var pending = new HashSet<Guid>();
            foreach (var pair in states)
            {
                if (pair.Value.Dead) continue;
                if (!HidppUnitProbe.HasOpenSlot(pair.Value, HidppHapticProtocol.IsBluetoothPath(pair.Key))) continue;
                if (_containers.TryGetValue(pair.Key, out var container) && container != Guid.Empty)
                    pending.Add(container);
            }
            Volatile.Write(ref _snapshot, new HidppSnapshot(units, pending));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }
    }
}
