using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A hardware (or virtual) MIDI input endpoint exposed to the input
    /// pipeline as a standard <see cref="ISdlInputDevice"/> — the symmetric
    /// counterpart of <see cref="MidiVirtualController"/> (issue #128).
    ///
    /// The device listens to the ENTIRE MIDI namespace: all 128 notes, all
    /// 128 CCs, and pitch bend, channel-merged (a note or CC means the same
    /// thing whatever channel it arrives on). There is nothing to configure
    /// — a MIDI device is already defined. The live state lands in
    /// <see cref="CustomInputState.Midi"/> (a dedicated sub-state, not the
    /// gamepad axis/button arrays), and mapping descriptors of the form
    /// "Midi Note N" / "Midi CC N" / "Midi Pitch Bend" resolve against it.
    ///
    /// State is written by the WinRT message callback and read by the
    /// polling thread, with the same clone-under-lock discipline as
    /// <see cref="WebControllerDevice"/>.
    /// </summary>
    internal sealed class MidiInputDevice : ISdlInputDevice
    {
        private const ushort MidiVendorId = 0x4D49;  // "MI"
        private const ushort MidiProductId = 0x4D44; // "MD"

        // Relative-encoder pulse shaping. An endless encoder in BINARY-OFFSET
        // relative mode (a.k.a. "Relative 2") sends a CC value of 0x40 ± delta
        // per detent (0x41 = +1, 0x3F = −1, center 0x40). Values within
        // ±RelativeMax of center are read as relative deltas and turned into
        // momentary "+"/"−" button pulses (one per detent); values further out
        // are treated as an absolute fader and never pulse. Each pulse presses
        // for PulseOnMs, releases for PulseGapMs, then the next queued detent
        // fires — long enough for a 60 Hz poll to catch every step.
        //
        // Only binary-offset is decoded. A two's-complement encoder
        // ("Relative 1" / Mackie: 0x01 = +1, 0x7F = -1) sends values outside
        // the band, so it reads as absolute jumps. A signed-bit encoder
        // ("Relative 3") is misread: a turn one way sends 0x41 and up, which
        // lands inside the band and pulses Up, and a turn the other way sends
        // 0x01 and up, which reads as an absolute fader. Most controllers
        // default to binary-offset or are switchable.
        private const int RelativeCenter = 0x40; // 64
        private const int RelativeMax = 16;
        // 24 ms pressed guarantees overlap with at least one frame of a 60 Hz
        // (16.7 ms) game poll; 12 ms gap caps throughput at ~28 detents/sec.
        private const int PulseOnMs = 24;
        private const int PulseGapMs = 12;
        // Shallow queue: one detent = one ~36 ms press, so a button can emit
        // at most ~28/sec. A faster spin would otherwise pile up pulses that
        // keep firing for seconds after the encoder physically stops. Cap the
        // backlog so any post-spin tail is at most 4 × 36 ms ≈ 144 ms; excess
        // detents in a too-fast spin are dropped rather than lagged.
        private const int MaxPendingPulses = 4;

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private volatile bool _attached;

        // Pulse machine state (per CC, ×2 for up/down: index 2*cc = up,
        // 2*cc+1 = down). Lives on the device, not the snapshot, since the
        // timing persists across polls. Guarded by _stateLock.
        private readonly int[] _pulsePending = new int[MidiInputState.CcCount * 2];
        private readonly byte[] _pulsePhase = new byte[MidiInputState.CcCount * 2]; // 0 idle, 1 on, 2 gap
        private readonly long[] _pulsePhaseUntil = new long[MidiInputState.CcCount * 2];

        private readonly string _endpointId;
        private IMidiInputConnection _connection;

        public MidiInputDevice(string endpointId, string name)
        {
            _endpointId = endpointId;
            Name = name;
            DevicePath = $"midi://{endpointId}";
            InstanceGuid = InstanceGuidFor(endpointId);
            ProductGuid = Md5Guid("pfmidi-in-product:" + name);
            SdlInstanceId = SyntheticInstanceId.From(endpointId);

            var state = new CustomInputState { Midi = new MidiInputState() };
            _state = state;
        }

        /// <summary>The device identity an endpoint gets, known before the
        /// device exists, so the legacy sweep can ask whether a port is
        /// assigned to a slot before opening it.</summary>
        internal static Guid InstanceGuidFor(string endpointId) => Md5Guid("pfmidi-in:" + endpointId);

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        // The MIDI surface lives entirely in CustomInputState.Midi, not in
        // the gamepad Axis[]/Buttons[] arrays, so the device reports zero of
        // those. Its mappable controls come from MappingDisplayResolver's
        // MIDI block and resolve through the "Midi ..." descriptor family.
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
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
        public bool IsAttached => _attached;
        public ushort VendorId => MidiVendorId;
        public ushort ProductId => MidiProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.Midi;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        // GetDeviceObjects is empty: the mapping picker lists MIDI controls
        // through MappingDisplayResolver's dedicated MIDI block (the touchpad
        // pattern), not through the generic Axis/Button enumeration.
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        // ─────────────────────────────────────────────
        //  Connection lifecycle
        // ─────────────────────────────────────────────

        /// <summary>Opens the endpoint connection on the shared input
        /// session. Returns false when MIDI services are unavailable or
        /// the endpoint refuses the connection.</summary>
        public bool Open()
        {
            // Every call here is Windows MIDI Services WinRT RPC, and this
            // runs on the POLLING THREAD via the Step 1 sweep. A hung
            // service wedged the whole engine through exactly this lane
            // (live stack 2026-07-23: DisconnectEndpointConnection under
            // UpdateMidiInputDevices under PollingLoop). Same event-bounded
            // contract as the virtual-controller side; an event wait cannot
            // inline the worker body.
            IMidiInputConnection conn = null;
            bool ok = false;
            var done = new System.Threading.ManualResetEventSlim(false);
            var work = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var session = MidiInputRuntime.Session;
                    if (session == null) return;

                    // Subscribes OnUmp before the open, as the direct
                    // MessageReceived subscription did.
                    conn = session.CreateConnection(_endpointId, OnUmp);
                    if (conn == null) return;

                    if (!conn.Open())
                    {
                        conn.Detach();
                        // CreateEndpointConnection already registered this
                        // connection in the session; undo that on the
                        // failure path too (the success path does it in
                        // Dispose).
                        MidiInputRuntime.Disconnect(conn);
                        conn = null;
                        return;
                    }
                    ok = true;
                }
                catch { conn = null; }
                finally { done.Set(); }
            });
            if (!done.Wait(OpenTimeoutMs))
            {
                // Hung open: orphan it. If the RPC ever lands, tear the
                // stray connection down on its own thread.
                work.ContinueWith(_ =>
                {
                    var stray = conn;
                    if (stray != null)
                    {
                        try { stray.Detach(); } catch { }
                        MidiInputRuntime.Disconnect(stray);
                    }
                }, System.Threading.Tasks.TaskScheduler.Default);
                return false;
            }
            if (!ok) return false;
            _connection = conn;
            _attached = true;
            return true;
        }

        private const int OpenTimeoutMs = 3_000;

        /// <summary>Whether the endpoint connection is open. The legacy API
        /// lists a port it has not opened (InputManager Phase 1e).</summary>
        internal bool IsOpen => _connection != null;

        public void Dispose() => CloseConnection();

        /// <summary>Closes the connection and keeps the device, for the
        /// legacy API, which opens a port while it is assigned to a slot and
        /// closes it when the assignment goes. The state returns to rest so a
        /// note held at the close is not still held at the next
        /// open.</summary>
        internal void Close()
        {
            CloseConnection();
            lock (_stateLock)
            {
                _state = new CustomInputState { Midi = new MidiInputState() };
                Array.Clear(_pulsePending);
                Array.Clear(_pulsePhase);
                Array.Clear(_pulsePhaseUntil);
            }
        }

        private void CloseConnection()
        {
            _attached = false;
            var conn = _connection;
            _connection = null;
            if (conn != null)
            {
                try { conn.Detach(); } catch { }
                MidiInputRuntime.Disconnect(conn);
            }
        }

        // ─────────────────────────────────────────────
        //  Message parsing (UMP) — full namespace, omni
        // ─────────────────────────────────────────────

        /// <summary>One received message as its first two UMP words. The
        /// backend passes the second word for a 64-bit message (type 0x4)
        /// and 0 otherwise. Internal for the test seam: the WinRT callback
        /// is not constructible in tests.</summary>
        internal void OnUmp(uint w0, uint w1)
        {
            try
            {
                uint mt = w0 >> 28;

                if (mt == 0x2)
                {
                    // MIDI 1.0 channel voice (32-bit UMP).
                    int opcode = (int)((w0 >> 20) & 0xF);
                    int data1 = (int)((w0 >> 8) & 0x7F);
                    int data2 = (int)(w0 & 0x7F);
                    switch (opcode)
                    {
                        case 0x9: SetNote(data1, data2 != 0); break; // velocity 0 = NoteOff
                        case 0x8: SetNote(data1, false); break;
                        case 0xB: SetCc(data1, data2); break;
                        case 0xE: SetPitchBend(((data2 << 7) | data1) * 65535 / 16383); break;
                    }
                }
                else if (mt == 0x4)
                {
                    // MIDI 2.0 channel voice (64-bit UMP).
                    int opcode = (int)((w0 >> 20) & 0xF);
                    int index = (int)((w0 >> 8) & 0x7F);
                    switch (opcode)
                    {
                        // MIDI 2.0 NoteOn velocity 0 is a valid note-on.
                        case 0x9: SetNote(index, true); break;
                        case 0x8: SetNote(index, false); break;
                        case 0xB: SetCc(index, (int)(w1 >> 25)); break; // 32-bit CC -> 7-bit
                        case 0xE: SetPitchBend((int)(w1 >> 16)); break; // 32-bit -> 16-bit
                    }
                }
            }
            catch
            {
                // A malformed packet must never take down the WinRT
                // callback thread. Drop it.
            }
        }

        // Internal for the audit test seam (2026-07-25): the WinRT
        // callback is not constructible in tests.
        internal void SetNote(int note, bool down)
        {
            if (note < 0 || note >= MidiInputState.NoteCount) return;
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.Notes[note] = down;
                _state = s;
            }
        }

        internal void SetCc(int cc, int value7)
        {
            if (cc < 0 || cc >= MidiInputState.CcCount) return;
            int v = Math.Clamp(value7, 0, 127);
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.Cc[cc] = (byte)v;
                // MIDI channel-mode messages: All Sound Off (CC 120) and All Notes
                // Off (CC 123) silence every sounding note. Clear the note lanes so
                // mapped note-buttons release. A controller that ends a phrase with
                // one of these instead of per-note NoteOff would otherwise leave
                // every mapped note latched on. Omni Off/On and Mono/Poly
                // (CC 124-127) carry All Notes Off semantics per MIDI 1.0,
                // so they clear the lanes too (2026-07-25 audit). CC 122
                // (Local Control) does not.
                if (cc == 120 || (cc >= 123 && cc <= 127))
                    System.Array.Clear(s.Midi.Notes, 0, s.Midi.Notes.Length);
                // CC 121 Reset All Controllers (RP-015, scoped to the lanes
                // this consumer models): pitch bend recenters, mod wheel and
                // the pedals drop, expression returns to full, RPN/NRPN
                // selectors return to null. Bank/volume/pan/sound lanes are
                // deliberately NOT reset, per RP-015. Without this, a
                // keyboard panic (121+123) released mapped notes but left a
                // mapped Pitch Bend axis frozen off-center forever.
                if (cc == 121)
                {
                    s.Midi.PitchBend = 32768;
                    s.Midi.Cc[1] = 0;                                  // mod wheel
                    s.Midi.Cc[11] = 127;                               // expression
                    for (int p = 64; p <= 67; p++) s.Midi.Cc[p] = 0;   // pedals
                    for (int p = 98; p <= 101; p++) s.Midi.Cc[p] = 127; // (N)RPN null
                    // Kill the reset lanes' queued/in-flight encoder pulses,
                    // or CcUp/CcDown momentaries keep firing after the reset.
                    ResetPulseLane(1);
                    ResetPulseLane(11);
                    for (int p = 64; p <= 67; p++) ResetPulseLane(p);
                    for (int p = 98; p <= 101; p++) ResetPulseLane(p);
                }
                _state = s;

                // Relative-encoder reading: a value near 0x40 is a signed
                // detent delta (0x41 = +1, 0x3F = −1). Queue one pulse per
                // detent on the up/down lane. Values outside the relative
                // band are an absolute fader and never pulse.
                int delta = v - RelativeCenter;
                if (delta > 0 && delta <= RelativeMax)
                    _pulsePending[2 * cc] = Math.Min(MaxPendingPulses, _pulsePending[2 * cc] + delta);
                else if (delta < 0 && -delta <= RelativeMax)
                    _pulsePending[2 * cc + 1] = Math.Min(MaxPendingPulses, _pulsePending[2 * cc + 1] - delta);
            }
        }

        /// <summary>Clears one CC lane's encoder-pulse machine, both
        /// directions: pending detents and any in-flight press/gap phase.
        /// Caller holds _stateLock (the pulse arrays are lock-guarded).</summary>
        private void ResetPulseLane(int cc)
        {
            int up = 2 * cc, down = 2 * cc + 1;
            if (down >= _pulsePending.Length) return;
            _pulsePending[up] = 0; _pulsePending[down] = 0;
            _pulsePhase[up] = 0; _pulsePhase[down] = 0;
            // Deadline stamps reset with their phase (audit 2026-07-25,
            // C23): today every phase entry re-stamps before reading, but
            // a reset that leaves a stale deadline is one future phase-0
            // read away from replaying it.
            _pulsePhaseUntil[up] = 0; _pulsePhaseUntil[down] = 0;
        }

        internal void SetPitchBend(int scaled)
        {
            lock (_stateLock)
            {
                var s = _state.Clone();
                s.Midi.PitchBend = Math.Clamp(scaled, 0, 65535);
                _state = s;
            }
        }

        // Pooled per-tick snapshot (poll thread is the sole caller): the
        // old shape deep-cloned TWICE per 1 kHz read (base clone + return
        // clone). The base republish was redundant: every read rewrites
        // ALL CcUp/CcDown stamps from the pulse machine's own state, so
        // event writers never depended on the stamped base.
        private PadForge.Engine.PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            lock (_stateLock)
            {
                // Advance the encoder pulse machine and stamp the momentary
                // CcUp/CcDown button states into the snapshot.
                long now = Environment.TickCount64;
                var s = _statePool.Next();
                _state.CopyInto(s);
                for (int i = 0; i < _pulsePending.Length; i++)
                {
                    bool pressed = false;
                    switch (_pulsePhase[i])
                    {
                        case 0: // idle
                            if (_pulsePending[i] > 0)
                            {
                                _pulsePhase[i] = 1;
                                _pulsePhaseUntil[i] = now + PulseOnMs;
                                pressed = true;
                            }
                            break;
                        case 1: // pressed
                            if (now < _pulsePhaseUntil[i]) pressed = true;
                            else
                            {
                                _pulsePhase[i] = 2;
                                _pulsePhaseUntil[i] = now + PulseGapMs;
                                _pulsePending[i]--;
                            }
                            break;
                        case 2: // release gap
                            if (now >= _pulsePhaseUntil[i]) _pulsePhase[i] = 0;
                            break;
                    }
                    int cc = i >> 1;
                    if ((i & 1) == 0) s.Midi.CcUp[cc] = pressed;
                    else s.Midi.CcDown[cc] = pressed;
                }
                return s;
            }
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }

    /// <summary>
    /// Shared Windows MIDI Services session for input endpoints, plus the
    /// endpoint enumeration the device thread consumes. Rides the API that
    /// <see cref="MidiVirtualController.IsAvailable"/> selects and starts,
    /// and never starts anything when no MIDI API is available.
    /// </summary>
    internal static class MidiInputRuntime
    {
        private static readonly object _lock = new();
        private static volatile IMidiInputSession _session;

        /// <summary>The shared input session, or null when Windows MIDI
        /// Services is unavailable.</summary>
        public static IMidiInputSession Session
        {
            get
            {
                if (_session != null) return _session;
                if (!MidiVirtualController.IsAvailable()) return null;
                var backend = MidiVirtualController.Backend;
                if (backend == null) return null;
                lock (_lock)
                {
                    if (_session != null) return _session;
                    // Bounded like every other service touch; a hung
                    // Create must not strand whichever thread first asks
                    // for the session.
                    IMidiInputSession created = null;
                    var done = new System.Threading.ManualResetEventSlim(false);
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { created = backend.CreateInputSession("PadForge MIDI Input"); }
                        catch { }
                        finally { done.Set(); }
                    });
                    if (!done.Wait(3_000)) return null;
                    _session = created;
                    return _session;
                }
            }
        }

        /// <summary>Fire-and-forget: the disconnect RPC runs on a worker
        /// and NOTHING waits on it. Teardown outcome is irrelevant to the
        /// caller (the connection object is discarded either way), and a
        /// hung service must never hold the polling thread again (live
        /// stack 2026-07-23). At worst a hung RPC parks one thread-pool
        /// thread until the service answers or the process exits. Skipped
        /// once <see cref="Shutdown"/> has run: the connection closed with
        /// its session, and the older runtime may be released by then.</summary>
        public static void Disconnect(IMidiInputConnection connection)
        {
            if (connection == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (_session != null)
                        connection.Disconnect();
                }
                catch { }
            });
        }

        /// <summary>Enumerates connectable MIDI endpoints: normal message
        /// endpoints only. PadForge's own MIDI virtual controllers stay
        /// visible through their CLIENT-side twin, which the service
        /// publishes as a normal endpoint (MIDI reference:
        /// Midi2.VirtualMidiEndpointManager.cpp CreateClientVisibleEndpoint,
        /// EndpointDeviceType = Normal). The device-side responder twin is
        /// for the device host application only per the service's own
        /// description string, and enumerating it is how the input lane
        /// used to poke stranded responder corpses every sweep.</summary>
        public static List<(string Id, string Name)> EnumerateEndpoints()
        {
            if (!MidiVirtualController.IsAvailable()) return new List<(string Id, string Name)>();
            try
            {
                var backend = MidiVirtualController.Backend;
                if (backend != null) return backend.EnumerateNormalEndpoints();
            }
            catch { }
            return new List<(string Id, string Name)>();
        }

        /// <summary>Drops the shared session after its backend was torn down,
        /// without waiting on it. The service behind it may be gone, and the
        /// polling thread must never wait on a dead call. The next asker gets
        /// a session from the current backend.</summary>
        public static void ResetSession()
        {
            IMidiInputSession old;
            lock (_lock)
            {
                old = _session;
                _session = null;
            }
            if (old != null)
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { old.Dispose(); }
                    catch { }
                });
        }

        /// <summary>Tears down the shared session. Call on app exit, before
        /// <see cref="MidiVirtualController.Shutdown"/>.</summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                try { _session?.Dispose(); } catch { }
                _session = null;
            }
        }
    }
}
