using System;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual controller that creates a Windows MIDI Services virtual device
    /// and sends MIDI 1.0 messages (CC for axes, Note On/Off for buttons).
    /// The device appears system-wide as a MIDI endpoint that DAWs and synths can connect to.
    /// Falls back gracefully on systems without Windows MIDI Services.
    ///
    /// <para>The availability probe picks the API (<see cref="MidiApiSelection"/>):
    /// the in-box Windows.Devices.Midi2 where Windows registers it, else the
    /// App SDK runtime where that is installed, else the legacy WinMM API
    /// (<see cref="MidiBackendLegacy"/>), which cannot create a port and
    /// opens the port the slot's <see cref="OutputPort"/> names instead.
    /// Everything after the probe goes through the chosen
    /// <see cref="IMidiBackend"/>.</para>
    /// </summary>
    internal sealed class MidiVirtualController : IVirtualController
    {
        private static bool? _isAvailable;
        private static volatile bool _probeTimedOut;
        private static readonly object _availLock = new();
        private static volatile IMidiBackend _backend;
        private static Func<IMidiBackend> s_backendFactory = CreateBackend;
        private static Func<IMidiBackend> s_legacyFactory = CreateLegacyBackend;

        private IMidiVirtualEndpoint _endpoint;
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

        /// <summary>The unique service-registry id of this controller's
        /// endpoint, or null before the first create attempt.</summary>
        internal string UniqueEndpointId => _uniqueEndpointId;

        /// <summary>Demotes the endpoint's registry claim out of READY so
        /// the input scanner stops opening the loopback twin. Call BEFORE
        /// teardown: the claim stays janitor-protected for the abandonment
        /// grace, and DisconnectCore's finally removes it outright.</summary>
        internal void MarkClosing()
        {
            var uid = _uniqueEndpointId;
            if (uid != null)
                s_liveEndpoints.TryUpdate(uid, Environment.TickCount64, EndpointReady);
        }

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
        private int _channel; // 0-15, from the constructor, then ApplyLayout
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

        /// <summary>The output port this slot sends to under the legacy API,
        /// by the name the slot's port picker saved
        /// (<see cref="MidiBackendLegacy.OpenOutputPort"/>). Unused by the
        /// Windows MIDI Services APIs, which create the slot's own
        /// port.</summary>
        internal string OutputPort { get; set; } = string.Empty;

        /// <summary>The API this controller's endpoint was made with. Step 5
        /// rebuilds the slot when the probe settles on another one, as when
        /// the runtime is installed under the legacy API.</summary>
        internal MidiApiKind ApiKind { get; private set; }

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

        // Claim-or-dispose generation: each create attempt carries a gen;
        // a timeout bumps it, so a late-landing creation sees itself
        // superseded and tears down its OWN locals instead of committing
        // over a newer attempt's fields. This is what makes the in-place
        // retry after service recovery race-free.
        private int _creationGen;
        private readonly object _stateLock = new();

        public void Connect()
        {
            if (_connected) return;

            for (int attempt = 1; ; attempt++)
            {
                int gen = System.Threading.Interlocked.Increment(ref _creationGen);

                // Event-based bound, NOT Task.Wait(timeout): Wait can inline
                // an unstarted task onto the waiting thread, and an inlined
                // body ignores the timeout entirely (trace 2026-07-23: an
                // 8 s bound observed running 34+ s). A ManualResetEventSlim
                // wait cannot execute anything.
                Exception fault = null;
                var done = new System.Threading.ManualResetEventSlim(false);
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { ConnectCore(gen); }
                    catch (Exception ex) { fault = ex; }
                    finally { done.Set(); }
                });
                if (done.Wait(ConnectTimeoutMs))
                {
                    if (fault != null) throw fault;
                    return;
                }

                // Timeout. Invalidate the attempt (a late completion now
                // tears down its own locals) and demote the registry claim
                // to abandoned so whatever devnode the hung RPC materialized
                // gets collected after the grace window instead of parking
                // in Device Manager until next launch.
                System.Threading.Interlocked.Increment(ref _creationGen);
                var uid = _uniqueEndpointId;
                if (uid != null)
                    s_liveEndpoints.TryUpdate(uid, Environment.TickCount64, EndpointCreating);
                MidiEndpointJanitor.ScheduleSweep(AbandonedGraceMs + 5_000);

                // A hung create is the wedged-midisrv signature (bench
                // 2026-07-23: instance stuck in StopPending; kill+restart
                // took the same create from 15 s timeout to 63 ms). Recover
                // the service once per process and retry once.
                if (attempt == 1 && MidiServiceRecovery.TryRecoverOnce())
                {
                    ResetAvailability();
                    if (!IsAvailable())
                        throw new TimeoutException(
                            $"Windows MIDI Services stayed unavailable after a service restart while creating '{"PadForge MIDI " + _instanceNum}'.");
                    continue;
                }

                throw new TimeoutException(
                    $"Windows MIDI Services did not answer within {ConnectTimeoutMs / 1000} s while creating '{"PadForge MIDI " + _instanceNum}'.");
            }
        }

        private void ConnectCore(int gen)
        {
            if (_connected) return;

            var deviceName = $"PadForge MIDI {_instanceNum}";

            // Register the identity BEFORE the service can materialize the
            // endpoint, so the janitor never sweeps a mid-create endpoint
            // and the scanner can tell "ours, still creating" from corpse.
            var uid = BuildUniqueEndpointId(_instanceNum);
            s_liveEndpoints[uid] = EndpointCreating;
            _uniqueEndpointId = uid;

            // Everything is built in LOCALS and committed to the instance
            // fields only while this attempt is still the current
            // generation. A superseded attempt (Connect timed out, maybe
            // already retrying) tears down what it built and touches
            // nothing shared.
            IMidiVirtualEndpoint endpoint;
            MidiApiKind kind;
            try
            {
                var backend = _backend
                    ?? throw new InvalidOperationException("Windows MIDI Services is not available.");
                kind = backend.Kind;
                endpoint = backend is MidiBackendLegacy legacy
                    ? legacy.OpenOutputPort(OutputPort)
                    : backend.CreateVirtualEndpoint(deviceName, uid, _padIndex);
            }
            catch
            {
                // Creation failed partway: the service may have stranded
                // the half-made endpoint. The backend already tore down what
                // it built. Unregister, and let the janitor remove whatever
                // the service left.
                ReleaseEndpointClaim(uid);
                throw;
            }

            lock (_stateLock)
            {
                if (gen != System.Threading.Volatile.Read(ref _creationGen) || _connected)
                {
                    // Superseded while creating: this endpoint belongs to
                    // no one. Dispose it without touching instance fields.
                    TeardownLocalCreation(endpoint, uid);
                    return;
                }

                _endpoint = endpoint;
                ApiKind = kind;
                _connected = true;
                s_liveEndpoints[uid] = EndpointReady;

                // Initialize change detection arrays sized to match configured CC/note counts.
                _lastCcValues = new byte[CcNumbers.Length];
                for (int i = 0; i < _lastCcValues.Length; i++)
                    _lastCcValues[i] = 64; // center for axes
                _lastNotes = new bool[NoteNumbers.Length];
            }
        }

        private static void TeardownLocalCreation(IMidiVirtualEndpoint endpoint, string uid)
        {
            try { endpoint.DisconnectConnection(); } catch { /* best effort */ }
            try { endpoint.CloseSession(); } catch { /* best effort */ }
            ReleaseEndpointClaim(uid);
        }

        private static void ReleaseEndpointClaim(string uid)
        {
            s_liveEndpoints.TryRemove(uid, out _);
            MidiEndpointJanitor.ScheduleSweep(2_500);
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
                if (_endpoint != null && _lastNotes != null)
                {
                    for (int i = 0; i < _lastNotes.Length && i < NoteNumbers.Length; i++)
                    {
                        if (_lastNotes[i])
                            SendNoteOff(NoteNumbers[i]);
                    }
                }
                _lastNotes = null;

                var endpoint = _endpoint;
                if (endpoint != null)
                {
                    endpoint.DisconnectConnection();
                    _endpoint = null;
                    endpoint.CloseSession();
                }
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

        // The MIDI bar settings CcNumbers and NoteNumbers were last built
        // from, for ApplyLayout's change test. One thread at a time touches
        // them, as with the submit: the creating task before Connect, then
        // the polling thread.
        private bool _layoutApplied;
        private int _layoutStartCc, _layoutCcCount, _layoutStartNote, _layoutNoteCount;

        /// <summary>
        /// Takes the slot's MIDI bar settings: channel (0-15), the first CC
        /// and note numbers with their counts, and the velocity. The creating
        /// task calls it before Connect, and Step 5 calls it before every
        /// submit, the way the keyboard and mouse slot takes its SOCD
        /// settings, so an edit reaches a running slot without closing its
        /// port. Unchanged settings return after the compares, with nothing
        /// allocated.
        ///
        /// <para>A held note whose channel and number survive the edit stays
        /// held. Every other held note is released on the channel and number
        /// it went out on, since a Note Off ends only the Note On with the
        /// same channel and key (keyboardmania-input-to-virtual-midi keeps
        /// each pressed note for its release for that reason). A CC whose
        /// channel and number survive keeps its last value. The rest start
        /// over as at connect, so the next submit presses held buttons on
        /// their new notes and sends each moved CC that is off center. A new
        /// velocity applies from the next Note On.</para>
        /// </summary>
        internal void ApplyLayout(int channel, int startCc, int ccCount, int startNote, int noteCount, byte velocity)
        {
            Velocity = velocity;

            // The bar clamps each field as it is set, but a start and its
            // count change in two steps, so a reader between them sees the
            // new start with the old count. Clamping again keeps every
            // number inside 0-127.
            channel = Math.Clamp(channel, 0, 15);
            startCc = Math.Clamp(startCc, 0, 127);
            ccCount = Math.Clamp(ccCount, 0, 128 - startCc);
            startNote = Math.Clamp(startNote, 0, 127);
            noteCount = Math.Clamp(noteCount, 0, 128 - startNote);
            if (_layoutApplied && channel == _channel
                && startCc == _layoutStartCc && ccCount == _layoutCcCount
                && startNote == _layoutStartNote && noteCount == _layoutNoteCount)
                return;

            var ccNumbers = new int[ccCount];
            for (int i = 0; i < ccCount; i++) ccNumbers[i] = startCc + i;
            var noteNumbers = new int[noteCount];
            for (int i = 0; i < noteCount; i++) noteNumbers[i] = startNote + i;
            bool sameChannel = channel == _channel;

            // Released before the channel moves, since Send reads _channel.
            // Before Connect there is no change state yet: ConnectCore sizes
            // it from the numbers set below.
            var heldNotes = _lastNotes;
            var oldNotes = NoteNumbers;
            bool[] keptNotes = null;
            if (heldNotes != null)
            {
                keptNotes = new bool[noteCount];
                for (int i = 0; i < heldNotes.Length && i < oldNotes.Length; i++)
                {
                    if (!heldNotes[i]) continue;
                    if (sameChannel && i < noteCount && noteNumbers[i] == oldNotes[i])
                        keptNotes[i] = true;
                    else
                        SendNoteOff(oldNotes[i]);
                }
            }

            var sentCcs = _lastCcValues;
            var oldCcs = CcNumbers;
            byte[] keptCcs = null;
            if (sentCcs != null)
            {
                keptCcs = new byte[ccCount];
                for (int i = 0; i < ccCount; i++)
                {
                    keptCcs[i] = sameChannel && i < sentCcs.Length && i < oldCcs.Length && ccNumbers[i] == oldCcs[i]
                        ? sentCcs[i]
                        : (byte)64; // center for axes, as at connect
                }
            }

            _channel = channel;
            CcNumbers = ccNumbers;
            NoteNumbers = noteNumbers;
            if (keptNotes != null) _lastNotes = keptNotes;
            if (keptCcs != null) _lastCcValues = keptCcs;
            _layoutStartCc = startCc;
            _layoutCcCount = ccCount;
            _layoutStartNote = startNote;
            _layoutNoteCount = noteCount;
            _layoutApplied = true;
        }

        /// <summary>
        /// Sends MIDI messages from a MidiRawState with arbitrary CC and note counts.
        /// Only sends messages when values change (change detection per CC and per note).
        /// </summary>
        public void SubmitMidiRawState(MidiRawState state)
        {
            if (!_connected || _endpoint == null) return;

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
            => Send(Midi1Status.ControlChange, ccNumber, value);

        private void SendNoteOn(int note, byte velocity)
            => Send(Midi1Status.NoteOn, note, velocity);

        private void SendNoteOff(int note)
            => Send(Midi1Status.NoteOff, note, 0);

        private void Send(Midi1Status status, int data1, int data2)
        {
            var endpoint = _endpoint;
            if (endpoint == null) return;
            // The send is service RPC. A dying or restarting midisrv
            // must fail a message, never the polling thread.
            try { endpoint.Send(status, _channel, data1, data2); } catch { /* dropped */ }
        }

        // ─────────────────────────────────────────────
        //  Static availability check
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if a MIDI API started on this system: a Windows MIDI
        /// Services API, else the legacy one. Caches the result after first
        /// check.
        /// </summary>
        public static bool IsAvailable()
        {
            // Latched down for an uninstall of the older runtime in flight.
            // Without this the next device sweep re-probes (Shutdown clears
            // the cached answer), recreates the initializer, and reloads the
            // very dlls the uninstaller is trying to delete. #128's input
            // enumeration runs that sweep on a timer, so the window is about
            // a second wide.
            if (_runtimeSuppressed) return false;
            if (_isAvailable.HasValue) return _isAvailable.Value;

            // Same bounded contract as Connect/Disconnect: activating the
            // API and EnsureServiceAvailable are WinRT calls and can hang on
            // a broken service. A timed-out probe reads as unavailable for
            // this session (ResetAvailability re-probes).
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
                // ResetAvailability clears this.
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

                // Windows MIDI Services first. Where neither of its APIs
                // starts (none present, Legacy API mode, a service that
                // refuses), the legacy API, which every Windows has. A probe
                // that hangs in the first never reaches the second: the
                // bound in IsAvailable gives up on both, and WinMM routes
                // through the same service on the new MIDI stack anyway.
                var backend = TryStart(s_backendFactory) ?? TryStart(s_legacyFactory);
                _backend = backend;
                _isAvailable = backend != null;
                return backend != null;
            }
        }

        /// <summary>Creates and starts one backend, or returns null. A start
        /// that failed or threw partway may hold the App SDK runtime's
        /// initializer, so it is released here rather than
        /// abandoned.</summary>
        private static IMidiBackend TryStart(Func<IMidiBackend> factory)
        {
            IMidiBackend backend = null;
            try
            {
                backend = factory?.Invoke();
                if (backend != null && backend.Start()) return backend;
            }
            catch { }
            try { backend?.Stop(skipDispose: false); } catch { }
            return null;
        }

        /// <summary>The production backend: the API
        /// <see cref="MidiApiSelection.Choose"/> picks for this PC, or null
        /// when neither is available. Runs inside the bounded probe, since
        /// activating the in-box API loads its DLL.</summary>
        private static IMidiBackend CreateBackend()
        {
            switch (MidiApiSelection.Choose(
                MidiApiSelection.OsBuild,
                MidiApiSelection.ProbeInBoxActivation,
                MidiApiSelection.IsAppSdkRuntimeInstalled))
            {
                case MidiApiKind.InBox: return new MidiBackendInBox();
                case MidiApiKind.AppSdk: return new MidiBackendAppSdk();
                default: return null;
            }
        }

        private static IMidiBackend CreateLegacyBackend() => new MidiBackendLegacy();

        /// <summary>The backend the last successful probe started, or null.
        /// MIDI input rides the same one.</summary>
        internal static IMidiBackend Backend => _backend;

        /// <summary>The API in use: the last successful probe's, else
        /// None.</summary>
        internal static MidiApiKind ActiveApi
            => _isAvailable == true ? (_backend?.Kind ?? MidiApiKind.None) : MidiApiKind.None;

        /// <summary>True when a probe ran and found no working API, or timed
        /// out, and nothing has reset it since. The Settings card uses it to
        /// tell an API that is present but did not start from one that is
        /// missing.</summary>
        internal static bool ProbeFailed
            => _isAvailable == false || (_probeTimedOut && _isAvailable == null);

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): replaces
        /// the backend factories and clears the cached probe. A test that
        /// names no legacy factory gets no fallback, so a fake that refuses
        /// to start never reaches the real WinMM. A null
        /// <paramref name="factory"/> restores both production
        /// factories.</summary>
        internal static void UseBackendFactoryForTest(Func<IMidiBackend> factory, Func<IMidiBackend> legacyFactory = null)
        {
            s_backendFactory = factory ?? CreateBackend;
            s_legacyFactory = factory == null ? CreateLegacyBackend : legacyFactory;
            ResetAvailability();
        }

        /// <summary>
        /// Resets the cached availability check so the next call to IsAvailable()
        /// re-evaluates. Call after the older runtime is uninstalled, and
        /// after a service restart.
        /// </summary>
        public static void ResetAvailability()
        {
            _probeTimedOut = false;
            // An uninstall finishing (or a manual refresh) is the event that
            // makes MIDI worth probing again, so it is what lifts the latch.
            _runtimeSuppressed = false;
            lock (_availLock)
            {
                var backend = _backend;
                _backend = null;
                backend?.Stop(skipDispose: false);
                _isAvailable = null;
            }
        }

        private static volatile bool _runtimeSuppressed;

        /// <summary>
        /// Releases the older runtime and keeps it released, for its
        /// uninstall. Only the older runtime needs this: the in-box API is
        /// part of Windows and its uninstaller never touches it.
        ///
        /// <para>Dispose is what calls ShutdownSdkRuntime and lets go of the
        /// SDK's native dlls. Abandoning the initializer instead left them
        /// loaded, the bundle's Restart Manager closed PadForge to reach its
        /// own files, and the app vanished mid-click. Disposing is safe HERE
        /// because the service still exists at this instant: the uninstaller
        /// has not started yet.</para>
        ///
        /// <para>The latch is the other half. Shutdown alone clears the cached
        /// availability answer, so the next enumeration sweep would re-probe
        /// and load the runtime straight back in while the uninstaller was
        /// working. Availability stays false until <see cref="ResetAvailability"/>
        /// lifts it, which the uninstall calls when it finishes.</para>
        /// </summary>
        public static void SuppressForUninstall()
        {
            _runtimeSuppressed = true;
            Shutdown(skipDispose: false);
        }

        /// <summary>
        /// Releases the MIDI API. Call on app exit.
        /// </summary>
        /// <param name="skipDispose">
        /// When true, abandons the older runtime's initializer without
        /// calling Dispose(), for teardown while the service may ALREADY be
        /// mid-removal (app exit racing an external uninstall): Dispose()
        /// calls into the runtime and crashes if the service is going away
        /// under it. The in-app uninstall does NOT use this: it goes through
        /// SuppressForUninstall, which disposes while the service still
        /// exists so the SDK's dlls are actually released.
        /// </param>
        public static void Shutdown(bool skipDispose = false)
        {
            var backend = _backend;
            _backend = null;
            backend?.Stop(skipDispose);
            lock (_availLock) { _isAvailable = null; }
        }
    }
}
