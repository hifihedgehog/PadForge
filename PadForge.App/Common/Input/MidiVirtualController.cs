using System;
using PadForge.Engine;

using Microsoft.Windows.Devices.Midi2;
using Microsoft.Windows.Devices.Midi2.Endpoints.Virtual;
using Microsoft.Windows.Devices.Midi2.Messages;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual controller that creates a Windows MIDI Services virtual device
    /// and sends MIDI 1.0 messages (CC for axes, Note On/Off for buttons).
    /// The device appears system-wide as a MIDI endpoint that DAWs and synths can connect to.
    /// Falls back gracefully on systems without Windows MIDI Services.
    /// </summary>
    internal sealed class MidiVirtualController : IVirtualController
    {
        private static bool? _isAvailable;
        private static volatile bool _probeTimedOut;
        private static readonly object _availLock = new();
        private static Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer _initializer;

        private MidiSession _session;
        private MidiEndpointConnection _connection;
        private MidiVirtualDevice _virtualDevice;
        private bool _connected;
        private bool _disposed;

        // ── Endpoint identity + live registry ─────────────────────────
        // The unique id becomes the service's devnode instance id
        // (MIDIU_APPDEV_/MIDIU_APPPUB_ + id; MIDI reference:
        // Midi2.VirtualMidiEndpointManager.cpp:389, :285). That registry
        // outlives this process: a failed service-side teardown strands
        // the devnode AND, on the next create with the same id, the
        // service ADOPTS the corpse instead of failing cleanly
        // (MidiDeviceManager.cpp ERROR_ALREADY_EXISTS path). So the id
        // must be unique per CREATION, never a stable per-slot name.
        // The registry below is the in-process source of truth for which
        // endpoints are ours and alive; the input scanner and the
        // janitor both key off it instead of guessing from names.
        private string _uniqueEndpointId;

        // value: EndpointCreating = creating (endpoint may exist, not yet
        // open), EndpointReady = ready (device-side connection open;
        // loopback safe), any positive tick = ABANDONED at that tick (the
        // bounding timeout fired and nothing owns the outcome anymore).
        // Abandoned claims protect the devnode for a grace window in case
        // the hung RPC still lands, then expire so the janitor can
        // collect the corpse. Without the expiry, every hung create on a
        // sick service parked one devnode in Device Manager until the
        // next app launch (owner repro 2026-07-23: two switches to MIDI,
        // two stranded "PadForge MIDI 1" entries).
        private const long EndpointCreating = 0;
        private const long EndpointReady = -1;
        internal const int AbandonedGraceMs = 60_000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_liveEndpoints =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Unique service-registry id for one endpoint creation.
        /// Max 32 chars per the service contract (MIDI reference:
        /// json_defs.h MIDI_CONFIG_JSON_ENDPOINT_VIRTUAL_DEVICE_UNIQUE_ID_MAX_LEN).</summary>
        internal static string BuildUniqueEndpointId(int instanceNum)
            => $"PADFORGE_MIDI_{instanceNum}_{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

        /// <summary>True when the id belongs to an endpoint this process
        /// created and has not torn down. Works on devnode instance ids
        /// and endpoint interface ids alike, since both embed
        /// MIDIU_APPDEV_/MIDIU_APPPUB_ + the unique id. Creating and ready both count: the janitor must
        /// never remove an endpoint a connect is still materializing.</summary>
        internal static bool IsLiveEndpointInstance(string id)
            => MatchesRegistry(id, requireReady: false);

        /// <summary>True only when the owning controller finished its
        /// device-side open. The input scanner's loopback path opens the
        /// client-visible twin only in this state.</summary>
        internal static bool IsReadyEndpointInstance(string id)
            => MatchesRegistry(id, requireReady: true);

        // Test seams (InternalsVisibleTo PadForge.Tests): the real
        // registration lives in ConnectCore/DisconnectCore, which need the
        // MIDI service; tests drive the registry directly.
        internal static void RegisterEndpointForTest(string uniqueId, bool ready) => s_liveEndpoints[uniqueId] = ready ? EndpointReady : EndpointCreating;
        internal static void AbandonEndpointForTest(string uniqueId, long abandonedAtTick) => s_liveEndpoints[uniqueId] = abandonedAtTick;
        internal static void UnregisterEndpointForTest(string uniqueId) => s_liveEndpoints.TryRemove(uniqueId, out _);

        /// <summary>Drops abandoned claims whose grace window has passed.
        /// Called by the janitor at sweep start so expired corpses become
        /// sweep candidates.</summary>
        internal static void PruneExpiredEndpointClaims()
        {
            long now = Environment.TickCount64;
            foreach (var kvp in s_liveEndpoints)
                if (kvp.Value > 0 && now - kvp.Value >= AbandonedGraceMs)
                    s_liveEndpoints.TryRemove(kvp.Key, out _);
        }

        private static bool MatchesRegistry(string id, bool requireReady)
        {
            if (string.IsNullOrEmpty(id)) return false;
            long now = Environment.TickCount64;
            foreach (var kvp in s_liveEndpoints)
            {
                if (requireReady)
                {
                    if (kvp.Value != EndpointReady) continue;
                }
                else if (kvp.Value > 0 && now - kvp.Value >= AbandonedGraceMs)
                {
                    // Abandoned past grace: no longer a live claim.
                    continue;
                }
                if (id.IndexOf("MIDIU_APPDEV_" + kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("MIDIU_APPPUB_" + kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private readonly int _padIndex;
        private readonly int _channel; // 0-15
        private readonly int _instanceNum; // 1-based MIDI-type instance number

        // Change detection — only send messages when values actually change.
        private byte[] _lastCcValues;
        private bool[] _lastNotes;

        // CC numbers for each CC slot (index → MIDI CC number).
        internal int[] CcNumbers { get; set; } = { 1, 2, 3, 4, 5, 6 };

        // Note numbers for each note slot (index → MIDI note number).
        internal int[] NoteNumbers { get; set; } = { 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70 };

        // Note velocity for button presses.
        internal byte Velocity { get; set; } = 127;

        public VirtualControllerType Type => VirtualControllerType.Midi;
        public bool IsConnected => _connected;
        public int FeedbackPadIndex { get; set; }

        public MidiVirtualController(int padIndex, int channel, int instanceNum)
        {
            _padIndex = padIndex;
            _channel = Math.Clamp(channel, 0, 15);
            _instanceNum = instanceNum;
        }

        /// <summary>Windows MIDI Services calls are WinRT RPC and can hang
        /// outright when the service is broken (owner bench, 2026-07-23:
        /// an unbounded connect held the per-slot pending-task gate forever
        /// and the slot starved in Initializing). Every service touch is
        /// bounded: the core runs on an inner task; on timeout the hung
        /// call is orphaned (torn down if it ever lands) and the caller
        /// gets a clean failure, so createFailed latches and the slot
        /// frees for the next type.</summary>
        private const int ConnectTimeoutMs = 15_000;
        private const int DisconnectTimeoutMs = 8_000;

        public void Connect()
        {
            if (_connected) return;

            // Event-based bound, NOT Task.Wait(timeout): Wait can inline an
            // unstarted task onto the waiting thread, and an inlined body
            // ignores the timeout entirely (trace 2026-07-23: an 8 s bound
            // observed running 34+ s). A ManualResetEventSlim wait cannot
            // execute anything.
            Exception fault = null;
            var done = new System.Threading.ManualResetEventSlim(false);
            var work = System.Threading.Tasks.Task.Run(() =>
            {
                try { ConnectCore(); }
                catch (Exception ex) { fault = ex; }
                finally { done.Set(); }
            });
            if (!done.Wait(ConnectTimeoutMs))
            {
                // If the hung RPC ever completes, the session it built is
                // unreachable; tear it down on its own thread.
                work.ContinueWith(_ =>
                {
                    if (fault == null)
                        try { Disconnect(); } catch { /* best effort */ }
                }, System.Threading.Tasks.TaskScheduler.Default);
                // Nothing owns this creation anymore: demote its registry
                // claim to abandoned so the devnode the hung RPC may have
                // materialized gets collected after the grace window
                // instead of parking in Device Manager until next launch.
                var uid = _uniqueEndpointId;
                if (uid != null)
                    s_liveEndpoints.TryUpdate(uid, Environment.TickCount64, EndpointCreating);
                MidiEndpointJanitor.ScheduleSweep(AbandonedGraceMs + 5_000);
                throw new TimeoutException(
                    $"Windows MIDI Services did not answer within {ConnectTimeoutMs / 1000} s while creating '{"PadForge MIDI " + _instanceNum}'.");
            }
            if (fault != null)
                throw fault;
        }

        private void ConnectCore()
        {
            var deviceName = $"PadForge MIDI {_instanceNum}";

            // Register the identity BEFORE the service can materialize the
            // endpoint, so the janitor never sweeps a mid-create endpoint
            // and the scanner can tell "ours, still creating" from corpse.
            _uniqueEndpointId = BuildUniqueEndpointId(_instanceNum);
            s_liveEndpoints[_uniqueEndpointId] = EndpointCreating;

            // Define the virtual device.
            var declaredEndpointInfo = new MidiDeclaredEndpointInfo();
            declaredEndpointInfo.Name = deviceName;
            declaredEndpointInfo.ProductInstanceId = _uniqueEndpointId;
            declaredEndpointInfo.SpecificationVersionMajor = 1;
            declaredEndpointInfo.SpecificationVersionMinor = 1;
            declaredEndpointInfo.SupportsMidi10Protocol = true;
            declaredEndpointInfo.SupportsMidi20Protocol = false;
            declaredEndpointInfo.SupportsReceivingJitterReductionTimestamps = false;
            declaredEndpointInfo.SupportsSendingJitterReductionTimestamps = false;
            declaredEndpointInfo.HasStaticFunctionBlocks = true;

            var declaredDeviceIdentity = new MidiDeclaredDeviceIdentity();

            var userSuppliedInfo = new MidiEndpointUserSuppliedInfo();
            userSuppliedInfo.Name = deviceName;
            userSuppliedInfo.Description = $"PadForge virtual MIDI controller (slot {_padIndex + 1})";

            var config = new MidiVirtualDeviceCreationConfig(
                deviceName,
                "Virtual MIDI controller from PadForge",
                "PadForge",
                declaredEndpointInfo,
                declaredDeviceIdentity,
                userSuppliedInfo
            );

            // Single function block for MIDI 1.0 output.
            var block = new MidiFunctionBlock();
            block.Number = 0;
            block.Name = "Controller Output";
            block.IsActive = true;
            block.UIHint = MidiFunctionBlockUIHint.Sender;
            block.FirstGroup = new MidiGroup(0);
            block.GroupCount = 1;
            block.Direction = MidiFunctionBlockDirection.Bidirectional;
            block.RepresentsMidi10Connection = MidiFunctionBlockRepresentsMidi10Connection.YesBandwidthUnrestricted;
            block.MaxSystemExclusive8Streams = 0;
            block.MidiCIMessageVersionFormat = 0;
            config.FunctionBlocks.Add(block);

            try
            {
                _session = MidiSession.Create(deviceName);
                if (_session == null)
                    throw new InvalidOperationException("Failed to create MIDI session.");

                _virtualDevice = MidiVirtualDeviceManager.CreateVirtualDevice(config);
                if (_virtualDevice == null)
                    throw new InvalidOperationException("Failed to create virtual MIDI device.");

                _virtualDevice.SuppressHandledMessages = true;

                _connection = _session.CreateEndpointConnection(_virtualDevice.DeviceEndpointDeviceId);
                if (_connection == null)
                    throw new InvalidOperationException("Failed to create MIDI endpoint connection.");

                _connection.AddMessageProcessingPlugin(_virtualDevice);

                if (!_connection.Open())
                    throw new InvalidOperationException("Failed to open MIDI endpoint connection.");
            }
            catch
            {
                if (_connection != null && _session != null)
                    _session.DisconnectEndpointConnection(_connection.ConnectionId);
                _connection = null;
                _virtualDevice = null;
                _session?.Dispose();
                _session = null;
                // Creation failed partway: the service may have stranded
                // the half-made endpoint. Unregister and let the janitor
                // remove whatever the service left behind.
                s_liveEndpoints.TryRemove(_uniqueEndpointId, out _);
                MidiEndpointJanitor.ScheduleSweep(2_500);
                throw;
            }

            _connected = true;
            s_liveEndpoints[_uniqueEndpointId] = EndpointReady;

            // Initialize change detection arrays sized to match configured CC/note counts.
            _lastCcValues = new byte[CcNumbers.Length];
            for (int i = 0; i < _lastCcValues.Length; i++)
                _lastCcValues[i] = 64; // center for axes
            _lastNotes = new bool[NoteNumbers.Length];
        }

        public void Disconnect()
        {
            if (!_connected) return;

            // Same event-based bound as Connect (Task.Wait inlining trap).
            var done = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { DisconnectCore(); }
                catch { /* best effort */ }
                finally { done.Set(); }
            });
            if (!done.Wait(DisconnectTimeoutMs))
            {
                // Hung service teardown: orphan it. The fields are cleared
                // by the core whenever the RPC finally returns; this object
                // is discarded either way, and the pending-dispose gate is
                // what must not starve. Demote the registry claim so the
                // endpoint the service failed to tear down gets collected
                // after the grace window (DisconnectCore's finally removes
                // the claim outright if the RPC ever lands).
                _connected = false;
                var uid = _uniqueEndpointId;
                if (uid != null)
                    s_liveEndpoints.TryUpdate(uid, Environment.TickCount64, EndpointReady);
                MidiEndpointJanitor.ScheduleSweep(AbandonedGraceMs + 5_000);
            }
        }

        private void DisconnectCore()
        {
            if (!_connected) return;
            _connected = false;

            try
            {
                // Send Note Off for any held notes.
                if (_connection != null && _lastNotes != null)
                {
                    for (int i = 0; i < _lastNotes.Length && i < NoteNumbers.Length; i++)
                    {
                        if (_lastNotes[i])
                            SendNoteOff(NoteNumbers[i]);
                    }
                }
                _lastNotes = null;

                if (_connection != null && _session != null)
                {
                    _session.DisconnectEndpointConnection(_connection.ConnectionId);
                    _connection = null;
                }

                _virtualDevice = null;
                _session?.Dispose();
                _session = null;
            }
            finally
            {
                // Endpoint torn down (or as torn down as the service
                // allows; a throwing RPC lands here too). Unregister, then
                // sweep after a beat: the service gets first crack at its
                // own clean removal, and the janitor takes what it strands
                // (MidiEndpointTable.cpp OnDeviceDisconnected bails before
                // erasing when RemoveEndpoint fails).
                if (_uniqueEndpointId != null)
                    s_liveEndpoints.TryRemove(_uniqueEndpointId, out _);
                MidiEndpointJanitor.ScheduleSweep(2_500);
            }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            // Legacy path — not used for dynamic MIDI. Kept for IVirtualController interface.
        }

        /// <summary>
        /// Sends MIDI messages from a MidiRawState with arbitrary CC and note counts.
        /// Only sends messages when values change (change detection per CC and per note).
        /// </summary>
        public void SubmitMidiRawState(MidiRawState state)
        {
            if (!_connected || _connection == null) return;

            // CCs
            if (state.CcValues != null && _lastCcValues != null)
            {
                int ccCount = Math.Min(state.CcValues.Length, Math.Min(_lastCcValues.Length, CcNumbers.Length));
                for (int i = 0; i < ccCount; i++)
                {
                    if (state.CcValues[i] != _lastCcValues[i])
                    {
                        SendCC(CcNumbers[i], state.CcValues[i]);
                        _lastCcValues[i] = state.CcValues[i];
                    }
                }
            }

            // Notes
            if (state.Notes != null && _lastNotes != null)
            {
                int noteCount = Math.Min(state.Notes.Length, Math.Min(_lastNotes.Length, NoteNumbers.Length));
                for (int i = 0; i < noteCount; i++)
                {
                    if (state.Notes[i] != _lastNotes[i])
                    {
                        if (state.Notes[i])
                            SendNoteOn(NoteNumbers[i], Velocity);
                        else
                            SendNoteOff(NoteNumbers[i]);
                        _lastNotes[i] = state.Notes[i];
                    }
                }
            }
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // MIDI has no rumble feedback — no-op.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ─────────────────────────────────────────────
        //  MIDI message helpers (MIDI 1.0 via UMP)
        // ─────────────────────────────────────────────

        private void SendCC(int ccNumber, byte value)
        {
            var conn = _connection;
            if (conn == null) return;
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.ControlChange,
                new MidiChannel((byte)_channel),
                (byte)ccNumber,
                value);
            conn.SendSingleMessagePacket(msg);
        }

        private void SendNoteOn(int note, byte velocity)
        {
            var conn = _connection;
            if (conn == null) return;
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.NoteOn,
                new MidiChannel((byte)_channel),
                (byte)note,
                velocity);
            conn.SendSingleMessagePacket(msg);
        }

        private void SendNoteOff(int note)
        {
            var conn = _connection;
            if (conn == null) return;
            var msg = MidiMessageBuilder.BuildMidi1ChannelVoiceMessage(
                0,
                new MidiGroup(0),
                Midi1ChannelVoiceMessageStatus.NoteOff,
                new MidiChannel((byte)_channel),
                (byte)note,
                0);
            conn.SendSingleMessagePacket(msg);
        }

        // ─────────────────────────────────────────────
        //  Static availability check
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if Windows MIDI Services is available on this system.
        /// Caches the result after first check.
        /// </summary>
        public static bool IsAvailable()
        {
            if (_isAvailable.HasValue) return _isAvailable.Value;

            // Same bounded contract as Connect/Disconnect: the SDK probe
            // and EnsureServiceAvailable are WinRT RPC and can hang on a
            // broken service. A timed-out probe reads as unavailable for
            // this session (ResetAvailability re-probes after an install).
            if (_probeTimedOut) return false;
            bool result = false;
            var done = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { result = IsAvailableCore(); }
                catch { /* unavailable */ }
                finally { done.Set(); }
            });
            if (!done.Wait(10_000))
            {
                // Hung service: remember for the session so every later
                // create fails fast instead of re-paying the 10 s wait.
                // ResetAvailability clears this after a service install.
                _probeTimedOut = true;
                return false;
            }
            return result;
        }

        private static bool IsAvailableCore()
        {
            lock (_availLock)
            {
                if (_isAvailable.HasValue) return _isAvailable.Value;

                try
                {
                    _initializer = Microsoft.Windows.Devices.Midi2.Initialization.MidiDesktopAppSdkInitializer.Create();
                    if (!_initializer.InitializeSdkRuntime())
                    {
                        _initializer.Dispose();
                        _initializer = null;
                        _isAvailable = false;
                        return false;
                    }
                    if (!_initializer.EnsureServiceAvailable())
                    {
                        _initializer.Dispose();
                        _initializer = null;
                        _isAvailable = false;
                        return false;
                    }
                    _isAvailable = true;
                    return true;
                }
                catch
                {
                    _isAvailable = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// Resets the cached availability check so the next call to IsAvailable()
        /// re-evaluates. Call after installing MIDI Services.
        /// </summary>
        public static void ResetAvailability()
        {
            _probeTimedOut = false;
            lock (_availLock)
            {
                if (_initializer != null)
                {
                    _initializer.Dispose();
                    _initializer = null;
                }
                _isAvailable = null;
            }
        }

        /// <summary>
        /// Shuts down the MIDI Services SDK initializer. Call on app exit.
        /// </summary>
        /// <param name="skipDispose">
        /// When true, abandons the initializer without calling Dispose().
        /// Use before uninstalling MIDI Services — Dispose() calls into the
        /// runtime which triggers a native crash if the service is being removed.
        /// </param>
        public static void Shutdown(bool skipDispose = false)
        {
            if (_initializer != null)
            {
                if (!skipDispose)
                    try { _initializer.Dispose(); } catch { }
                _initializer = null;
            }
            lock (_availLock) { _isAvailable = null; }
        }
    }
}
