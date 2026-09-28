using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.AnalogKeyboard;

namespace PadForge.Common.Input
{
    /// <summary>What <see cref="AnalogKeyboardDevice.Open"/> found.</summary>
    internal enum AnalogKeyboardOpenResult
    {
        /// <summary>A route's handshake succeeded and the reader runs.</summary>
        Opened,
        /// <summary>The collection could not be opened or did not answer,
        /// likely held by the vendor's software: retry after the cooldown.</summary>
        Busy,
        /// <summary>Every matching route talked to the keyboard and none
        /// recognized it: leave it alone while it stays plugged in.</summary>
        NotSupported,
        /// <summary>No route recognized the keyboard, and one of them asks for
        /// another try after <see cref="AnalogKeyboardDevice.RetryAfterMs"/>,
        /// as its reference reconnects on a timer.</summary>
        RetryLater,
    }

    /// <summary>
    /// One analog keyboard as a mappable device row (issue #468, asked for in
    /// discussion #463). Every key's press depth lands in
    /// <see cref="CustomInputState.AnalogKeys"/> and maps through the
    /// <c>"Analog Key N"</c> descriptor, the way a MIDI endpoint publishes its
    /// namespace on its own sub-state (<see cref="MidiInputDevice"/>).
    ///
    /// <para>Opening tries each route that accepted the collection's metadata,
    /// in priority order, and keeps the first whose handshake succeeds. Then a
    /// reader thread owns the channel and runs the route's passes back to
    /// back: a blocking read for the families that push their state, a round
    /// of requests for the ones that answer. It writes the live key set under
    /// a lock and the poll thread copies it out, the
    /// <see cref="LogitechGKeysDevice"/> discipline.</para>
    /// </summary>
    internal sealed class AnalogKeyboardDevice : ISdlInputDevice
    {
        /// <summary>How often a Razer row checks that Synapse still runs.</summary>
        private const int SynapseCheckIntervalMs = 2000;

        /// <summary>How long a normal stop waits for the reader to finish its
        /// pass and let the route undo what it changed on the keyboard, before
        /// it cancels the I/O outright.</summary>
        private const int GracefulStopMs = 1500;

        private readonly AnalogKeyboardCandidate _candidate;
        private readonly object _stateLock = new();
        private readonly AnalogKeyInputState _live = new();
        private readonly Dictionary<int, int> _heldVirtualKeys = new();
        private AnalogKeyboardHidChannel _channel;
        private AnalogKeyboardSession _session;
        private AnalogKeyboardRoute _route;
        private Thread _reader;
        private volatile bool _attached;
        private volatile bool _disposed;
        private volatile bool _stopRequested;
        private volatile bool _synapseRunning = true;
        private long _reports;
        private long _lastSetTick;
        private int _staleAfterMs;
        private PooledInputStatePair _statePool;
        private int[] _keyOrder;
        private HashSet<int> _knownKeys;

        public AnalogKeyboardDevice(AnalogKeyboardCandidate candidate)
        {
            _candidate = candidate;
            Name = candidate.Name;
            HidPath = candidate.Path;
            DevicePath = "analogkb://" + candidate.IdentityKey.ToLowerInvariant();
            InstanceGuid = Md5Guid("pfanalogkb:" + candidate.IdentityKey.ToLowerInvariant());
            ushort identityPid = AnalogKeyboardCatalog.IdentityProductId(candidate.VendorId, candidate.ProductId);
            ProductGuid = Md5Guid($"pfanalogkb-product:{candidate.VendorId:X4}:{identityPid:X4}");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        /// <summary>The HID collection this row reads, the sweep's key.</summary>
        public string HidPath { get; }

        /// <summary>The second collection the route reads from, when it
        /// commands one collection and reads another, or null. The sweep leaves
        /// it alone while this row owns it.</summary>
        public string CompanionPath { get; private set; }

        /// <summary>The identity every collection of this keyboard shares.</summary>
        public string IdentityKey => _candidate.IdentityKey;

        /// <summary>The route that won the handshake, or the first candidate
        /// before <see cref="Open"/>.</summary>
        public AnalogKeyboardProtocol Protocol => _route?.Protocol ?? _candidate.Protocol;

        /// <summary>False while a Razer row waits for Synapse, which is the
        /// only thing that makes its keyboard send analog reports.</summary>
        public bool SynapseRunning => _synapseRunning;

        public bool NeedsSynapse => _session?.NeedsSynapse ?? false;

        /// <summary>Reports parsed or passes answered, which tells a user
        /// "found" from "found and reading".</summary>
        public long ReportCount => Interlocked.Read(ref _reports);

        /// <summary>The keys the input picker lists for this keyboard.</summary>
        public int[] KeyOrder => _keyOrder ?? AnalogKeyCodes.FullKeyboard;

        /// <summary>True for a row that waits for its first key set before it
        /// registers, a route that recognizes its keyboards by their reports.</summary>
        public bool RegistersOnFirstReport => _route?.RegisterOnFirstReport == true;

        /// <summary>After <see cref="AnalogKeyboardOpenResult.RetryLater"/>,
        /// how long the sweep waits before trying again.</summary>
        public int RetryAfterMs { get; private set; }

        // ─── ISdlInputDevice identity / capabilities ───
        // The keys live on CustomInputState.AnalogKeys, not in the numbered
        // arrays, so the row reports none of those. Its picker entries come
        // from MappingDisplayResolver's analog key block, the MIDI pattern.
        public uint SdlInstanceId { get; }
        public string Name { get; private set; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public int[] SupportedAxisIndices => Array.Empty<int>();
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => _candidate.VendorId;
        public ushort ProductId => _candidate.ProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => _candidate.Serial;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.AnalogKeyboard;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        // ─── Lifecycle ───

        /// <summary>Tries each matching route: opens the collection its way,
        /// runs its handshake, and on the first success starts the reader.
        /// Blocking I/O, so the sweep's worker calls it, never the poll
        /// thread.</summary>
        public AnalogKeyboardOpenResult Open()
        {
            if (_disposed) return AnalogKeyboardOpenResult.Busy;
            bool busy = false;
            int retryMs = 0;
            foreach (var route in _candidate.Routes)
            {
                var info = _candidate.Info;
                AnalogKeyboardDeviceInfo companion = null;
                if (route.Companion != null)
                {
                    try { companion = route.Companion(info); }
                    catch { companion = null; }
                    if (companion == null) continue;
                }

                var channel = AnalogKeyboardHidChannel.Open(info, route, companion);
                if (channel == null)
                {
                    busy = true;
                    continue;
                }

                AnalogKeyboardSession session = null;
                bool started = false;
                try
                {
                    session = route.CreateSession(info);
                    started = session != null && session.Start(channel);
                }
                catch
                {
                    started = false;
                }
                if (!started)
                {
                    channel.Close();
                    if (route.StartRetryMs > 0 && session?.NoStartRetry != true
                        && (retryMs == 0 || route.StartRetryMs < retryMs))
                        retryMs = route.StartRetryMs;
                    continue;
                }

                _route = route;
                _session = session;
                _channel = channel;
                CompanionPath = companion?.Path;
                _staleAfterMs = Math.Max(0, route.StaleAfterMs);
                Name = session.ModelName ?? AnalogKeyboardHidRuntime.NameFor(info, route);
                _keyOrder = session.KeyOrder ?? SafeKeys(route, info);
                _knownKeys = new HashSet<int>(KeyOrder);
                foreach (int code in KeyOrder)
                {
                    int vk = VirtualKeyForCode(code);
                    if (vk != 0) _heldVirtualKeys[code] = vk;
                }
                _attached = true;
                try
                {
                    _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "PadForge.AnalogKeyboard" };
                    _reader.Start();
                    return AnalogKeyboardOpenResult.Opened;
                }
                catch
                {
                    // The session started, so it may have changed the
                    // keyboard: undo that before the handle closes.
                    _attached = false;
                    _channel = null;
                    try { session.Stop(channel); } catch { }
                    channel.Close();
                    return AnalogKeyboardOpenResult.Busy;
                }
            }
            if (busy) return AnalogKeyboardOpenResult.Busy;
            if (retryMs > 0)
            {
                RetryAfterMs = retryMs;
                return AnalogKeyboardOpenResult.RetryLater;
            }
            return AnalogKeyboardOpenResult.NotSupported;
        }

        private static int[] SafeKeys(AnalogKeyboardRoute route, AnalogKeyboardDeviceInfo info)
        {
            try { return route.Keys?.Invoke(info); }
            catch { return null; }
        }

        /// <summary>Stops the reader. It finishes its pass and lets the route
        /// undo what it changed on the keyboard. A reader that does not finish
        /// in time has its I/O canceled. A reader still stuck in a native call
        /// owns the channel's buffers, so they leak rather than being freed
        /// under it (the headset rule).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopRequested = true;
            _attached = false;
            var reader = _reader;
            _reader = null;
            int graceful = _route != null && _route.StopTimeoutMs > 0 ? _route.StopTimeoutMs : GracefulStopMs;
            if (reader != null && !reader.Join(graceful))
            {
                _channel?.Abort();
                reader.Join(1000);
            }
            else if (reader == null)
            {
                _channel?.Close();
            }
        }

        private void ReaderLoop()
        {
            var channel = _channel;
            var session = _session;
            try
            {
                RunPasses(channel, session);
            }
            catch
            {
                // A vanishing device surfaces as a throw from any native
                // call. The sweep retires the row off IsAttached.
            }
            finally
            {
                _attached = false;
                lock (_stateLock) _live.ResetForReuse();
                try { session.Stop(channel); } catch { }
                try { channel.Close(); } catch { }
            }
        }

        private void RunPasses(AnalogKeyboardHidChannel channel, AnalogKeyboardSession session)
        {
            var pass = new AnalogKeyInputState();
            Func<int, bool> isHeld = IsHeldByWindows;
            int misses = 0;
            long nextSynapseCheck = 0;
            int minInterval = session is AnalogKeyboardPoller ? PollPeriodMs : 0;
            while (!_stopRequested)
            {
                if (session.NeedsSynapse)
                {
                    long now = Environment.TickCount64;
                    if (now >= nextSynapseCheck)
                    {
                        nextSynapseCheck = now + SynapseCheckIntervalMs;
                        bool running = AnalogKeyboardHidRuntime.IsSynapseRunning();
                        _synapseRunning = running;
                        // Without Synapse the keyboard stops reporting, and a
                        // key held at that moment would stay down forever.
                        if (!running)
                        {
                            pass.ResetForReuse();
                            lock (_stateLock) _live.ResetForReuse();
                        }
                    }
                }

                long started = Environment.TickCount64;
                var result = session.Pass(channel, pass, isHeld);
                if (_stopRequested) break;
                switch (result)
                {
                    case AnalogPollResult.Failed:
                        return;
                    case AnalogPollResult.NoAnswer:
                        // An unanswered pass releases every key, as Soup's
                        // reader does, so a key held when the keyboard stops
                        // answering does not stay down while the misses add up.
                        // A route with a staleness window keeps the last set
                        // until the window runs out, as its reference does.
                        if (_staleAfterMs == 0)
                        {
                            pass.ResetForReuse();
                            lock (_stateLock) _live.ResetForReuse();
                        }
                        if (++misses >= session.MissLimit) return;
                        continue;
                    case AnalogPollResult.Idle:
                        continue;
                }
                misses = 0;
                lock (_stateLock)
                {
                    pass.CopyInto(_live);
                    _lastSetTick = Environment.TickCount64;
                }
                NoteNewKeys(pass);
                Interlocked.Increment(ref _reports);
                long spent = Environment.TickCount64 - started;
                if (spent < minInterval) Thread.Sleep((int)(minInterval - spent));
            }
        }

        /// <summary>Adds the keys a pass reported that the row's list lacks,
        /// so the input picker offers them too: the routes that know their
        /// keyboards' keys only by position learn them as they are pressed,
        /// and a key a table missed still shows up.</summary>
        private void NoteNewKeys(AnalogKeyInputState pass)
        {
            List<int> added = null;
            for (int i = 0; i < pass.Count; i++)
                if (_knownKeys.Add(pass.Codes[i])) (added ??= new List<int>()).Add(pass.Codes[i]);
            if (added == null) return;
            added.Sort();
            var current = KeyOrder;
            var order = new int[current.Length + added.Count];
            current.CopyTo(order, 0);
            added.CopyTo(order, current.Length);
            _keyOrder = order;
            AnalogKeyboardRuntime.SetKeyOrder(InstanceGuid, order);
        }

        /// <summary>Shortest time between the starts of two passes of a Soup
        /// or AnalogSense polled family. A keyboard answers in a few
        /// milliseconds, and the vendor's own configurator shares this
        /// channel. The HallJoy routes pace themselves.</summary>
        private const int PollPeriodMs = 2;

        /// <summary>Whether Windows sees the key down, the polled routes' cue
        /// to read it this pass. Soup asks DirectInput for the same fact
        /// (DigitalKeyboard), here through the virtual key the current layout
        /// gives the key's scan code.</summary>
        private bool IsHeldByWindows(int code)
            => _heldVirtualKeys.TryGetValue(code, out int vk) && (GetAsyncKeyState(vk) & 0x8000) != 0;

        private static int VirtualKeyForCode(int code)
        {
            int scan = AnalogKeyCodes.Ps2Scancode(code);
            if (scan == 0) return 0;
            return (int)MapVirtualKeyW((uint)scan, MapvkVscToVkEx);
        }

        // ─── State read (poll thread) ───

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            var s = _statePool.Next();
            s.AnalogKeys ??= new AnalogKeyInputState();
            lock (_stateLock)
            {
                // A route whose depths expire by wall clock publishes nothing
                // while a pass has been stuck past its limit.
                if (_staleAfterMs > 0 && _live.Count > 0
                    && Environment.TickCount64 - _lastSetTick > _staleAfterMs)
                    s.AnalogKeys.ResetForReuse();
                else
                    _live.CopyInto(s.AnalogKeys);
            }
            return s;
        }

        /// <summary>Test seam: sets a key's depth as a report would.</summary>
        internal void InjectForTest(int code, float depth)
        {
            lock (_stateLock) _live.Set(code, depth);
        }

        /// <summary>Test seam: mark live without opening a device.</summary>
        internal void AttachForTest() => _attached = true;

        /// <summary>Test seam: take a route's row settings as Open does.</summary>
        internal void UseRouteForTest(AnalogKeyboardRoute route)
        {
            _route = route;
            _staleAfterMs = Math.Max(0, route.StaleAfterMs);
            _knownKeys = new HashSet<int>(KeyOrder);
            _lastSetTick = Environment.TickCount64;
        }

        /// <summary>Test seam: when the last key set arrived.</summary>
        internal void SetLastReportTickForTest(long tick)
        {
            lock (_stateLock) _lastSetTick = tick;
        }

        /// <summary>Test seam: a pass's keys as the reader notes them.</summary>
        internal void NoteKeysForTest(AnalogKeyInputState pass) => NoteNewKeys(pass);

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }

        private const uint MapvkVscToVkEx = 3;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyW(uint code, uint mapType);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
    }
}
