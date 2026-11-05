using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        // The probe's answer. One volatile word, written under _availLock
        // and read without it by ActiveApi, ProbeFailed and the polling
        // thread. A Nullable<bool> here was two bytes that an optimized
        // reader on ARM64 could see torn mid-reset.
        private const int ProbeNotRun = 0;
        private const int ProbeAvailable = 1;
        private const int ProbeUnavailable = 2;
        private static volatile int _probeState;
        private static volatile bool _probeTimedOut;
        private static readonly object _availLock = new();
        private static volatile IMidiBackend _backend;
        private static Func<IMidiBackend> s_backendFactory = CreateBackend;
        private static Func<IMidiBackend> s_legacyFactory = CreateLegacyBackend;

        private IMidiVirtualEndpoint _endpoint;
        private volatile bool _connected;
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

        // Change detection. Only send messages when values actually change.
        private byte[] _lastCcValues;
        private bool[] _lastNotes;

        // Which CC entries have gone out since they took their number, so a
        // switch controller is released only where PadForge pressed it.
        // ConnectCore and ApplyLayout seed _lastCcValues at 64 as a
        // placeholder, which is not a value any receiver heard.
        private bool[] _ccSent;

        // What the receiver is still owed after the API did not take it, by
        // channel and number as the wire word packs them (WireKey), under
        // _sendLock: a Note Off, and a controller's value plus one (0 when
        // nothing is owed). The send worker records a refusal there and the
        // next submit sends it again (QueueOwed), so a lost Note Off does not
        // leave a note sounding until the button is pressed and released
        // again. They are keyed by what went out on the wire, not by the
        // slot's layout: a release that a MIDI bar edit or the teardown
        // queued is owed on the channel and number it was queued for, and an
        // edit drops nothing that is owed. Anything queued for a channel and
        // number settles what is owed there. A Note On is never sent twice,
        // since a duplicate would retrigger the note, and nothing is owed to
        // a port that is gone. The polling thread reads _owedCount and
        // _owedCc without the lock.
        private readonly bool[] _owedNoteOff = new bool[16 * 128];
        private readonly int[] _owedCc = new int[16 * 128];
        private int _owedCount;

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

        /// <summary>The backend generation this controller's endpoint was
        /// made in. Step 5 rebuilds the slot once it differs from
        /// <see cref="Generation"/>, since the backend or service the endpoint
        /// talks to is gone.</summary>
        internal int CreatedGeneration { get; private set; }

        /// <summary>True once a send showed the port itself is gone: under
        /// the legacy API, a handle whose device left answers every message
        /// with an error for good, even after the device is back under the
        /// same name. Step 5 rebuilds the slot, which opens the port
        /// again.</summary>
        internal bool EndpointLost => _endpoint?.Lost == true;

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
        /// frees for the next type. Tests shorten the create's
        /// bound.</summary>
        internal static int ConnectTimeoutMs = 15_000;
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

            // One service restart per create at most, and one wait for each
            // service instance to load its transports.
            bool restarted = false, waitedForTransports = false;
            while (true)
            {
                int gen = Interlocked.Increment(ref _creationGen);
                // A hang in a legacy open is a WinMM driver's, which a
                // service restart does not always reach (118).
                bool legacyCall = _backend is MidiBackendLegacy;
                // Read before the create, since a service that finishes
                // loading as the create fails reads as loaded afterward.
                bool transportsLoading = !waitedForTransports && TransportsLoading();

                // Event-based bound, NOT Task.Wait(timeout): Wait can inline
                // an unstarted task onto the waiting thread, and an inlined
                // body ignores the timeout entirely (trace 2026-07-23: an
                // 8 s bound observed running 34+ s). A ManualResetEventSlim
                // wait cannot execute anything.
                Exception fault = null;
                var done = new ManualResetEventSlim(false);
                Task.Run(() =>
                {
                    try { ConnectCore(gen); }
                    catch (Exception ex) { fault = ex; }
                    finally { done.Set(); }
                });
                if (done.Wait(ConnectTimeoutMs))
                {
                    if (fault == null) return;
                    // The service turns a virtual device create down until it
                    // has loaded the virtual transport, and it answers a
                    // connectivity check after 10 s whether or not it has
                    // loaded them all (bench 2026-10-28: loaded 10.15 s after
                    // a restart, and the create made at 10.0 s failed). The
                    // failed attempt already released its endpoint claim.
                    if (transportsLoading)
                    {
                        waitedForTransports = true;
                        AwaitTransports();
                        continue;
                    }
                    // A refused session is what a stopped or broken service
                    // answers, so it gets the restart a hung create gets.
                    if (fault is MidiSessionUnavailableException && !restarted)
                    {
                        restarted = true;
                        if (RestartService("the service refused a MIDI session", legacyCall: false) == MidiServiceRestart.Running)
                        {
                            if (!AvailableAfterRestart())
                                throw new MidiServiceUnavailableException("PadForge MIDI " + _instanceNum);
                            waitedForTransports = false;
                            continue;
                        }
                    }
                    throw fault;
                }

                // Timeout. Invalidate the attempt (a late completion now
                // tears down its own locals) and demote the registry claim
                // to abandoned so whatever devnode the hung RPC materialized
                // gets collected after the grace window instead of parking
                // in Device Manager until next launch.
                Interlocked.Increment(ref _creationGen);
                var uid = _uniqueEndpointId;
                if (uid != null)
                    s_liveEndpoints.TryUpdate(uid, Environment.TickCount64, EndpointCreating);
                MidiEndpointJanitor.ScheduleSweep(AbandonedGraceMs + 5_000);

                // A hung create is the wedged-midisrv signature (bench
                // 2026-07-23: instance stuck in StopPending; kill+restart
                // took the same create from 15 s timeout to 63 ms). Recover
                // the service (cooldown-gated) and retry once.
                if (!restarted)
                {
                    restarted = true;
                    if (RestartService("a MIDI create hung", legacyCall) == MidiServiceRestart.Running)
                    {
                        if (!AvailableAfterRestart())
                            throw new MidiServiceUnavailableException("PadForge MIDI " + _instanceNum);
                        waitedForTransports = false;
                        continue;
                    }
                }

                if (legacyCall)
                    throw new MidiPortException(MidiPortProblem.DidNotOpen, OutputPort,
                        $"MIDI output port '{OutputPort}' did not answer within {ConnectTimeoutMs / 1000} s.");
                throw new TimeoutException(
                    $"Windows MIDI Services did not answer within {ConnectTimeoutMs / 1000} s while creating '{"PadForge MIDI " + _instanceNum}'.");
            }
        }

        /// <summary>Whether the service the API in use talks through is still
        /// loading its transports (<see cref="MidiServiceRecovery.TransportsLoaded"/>).
        /// False for an API that does not talk to it, and for a service that
        /// gives no such signal.</summary>
        private static bool TransportsLoading()
            => _backend is { } backend && TalksToService(backend)
               && MidiServiceRecovery.TransportsLoaded(0) == false;

        /// <summary>Waits for the service to load its transports, through the
        /// create path's bound, and logs how that went.</summary>
        private static void AwaitTransports()
        {
            SdlDiagLog.WriteLine("MIDIRECOVER a create failed while midisrv was loading its transports: waiting for them");
            long start = Environment.TickCount64;
            bool? loaded = MidiServiceRecovery.TransportsLoaded(ConnectTimeoutMs);
            SdlDiagLog.WriteLine(loaded switch
            {
                true => $"MIDIRECOVER midisrv loaded its transports {Environment.TickCount64 - start} ms later: creating again",
                false => $"MIDIRECOVER midisrv was still loading its transports after {ConnectTimeoutMs / 1000} s: creating again",
                null => "MIDIRECOVER midisrv stopped signaling its transports: creating again",
            });
        }

        /// <summary>Whether MIDI answers on the instance a create's own
        /// service restart brought up. That instance's first probe waits for
        /// the service's device enumeration, up to the 10 s Microsoft's client
        /// gives it (MidiSrvTransport.cpp VerifyConnectivity,
        /// g_midiSynchronizedStartTimeoutMs), and 10 s is also the bound an
        /// ask gives a probe. So the ask gave up in the moment the answer came
        /// (bench 2026-10-28: 2 ms after the bound), and the create failed
        /// with MIDI running. A probe the ask gave up on is waited for here
        /// through the create path's bound, counted from the probe's start,
        /// the bound the recovery watches a probe for before it restarts
        /// anything (<see cref="ProbeWatchMs"/>). The probe read again after
        /// the wait may have answered in the instant after the ask, so it is
        /// read whether or not one was pending.</summary>
        private static bool AvailableAfterRestart()
        {
            if (IsAvailable()) return true;
            var pending = s_hungProbe;
            if (pending != null)
                pending.Done.Wait(ProbeWatchMs(pending.StartedMs, Environment.TickCount64));
            return IsAvailable();
        }

        /// <summary>Restarts the service for a call that hung or a session it
        /// refused (a create, MIDI input, a port listing, the release before
        /// a runtime uninstall), where a restart can reach the call
        /// (<see cref="ServiceRestartCanHelp"/>). Whenever
        /// the instance the call talked to ended, running again or not, a new
        /// generation starts, so the slots and inputs made on it rebuild. The
        /// uninstall latch stays up through it. The cached answer is reset
        /// before the restart reads as over, so no probe and no look at the
        /// service instance (<see cref="CheckServiceInstance"/>) lands
        /// between the two. The new generation starts once it does read as
        /// over, so the slots it sets rebuilding find a probe that may
        /// run.</summary>
        internal static MidiServiceRestart RestartService(string trigger, bool legacyCall)
        {
            if (!ServiceRestartCanHelp(legacyCall)) return MidiServiceRestart.Refused;
            bool reset = false;
            var restart = MidiServiceRecovery.TryRestart(trigger, outcome =>
            {
                if (outcome is not (MidiServiceRestart.Running or MidiServiceRestart.EndedNotRunning)) return;
                lock (_availLock) ResetLocked();
                reset = true;
            });
            if (reset) Interlocked.Increment(ref s_generation);
            return restart;
        }

        /// <summary>What a watchdog's request for a service restart would
        /// meet, for its diagnostics line: a restart, another restart's
        /// cooldown, or a call no restart reaches.
        /// <paramref name="restarts"/> says whether the request is worth
        /// making. A watchdog that makes none looks again when it next
        /// fires. It runs on the poll thread, so it reads the last answer on
        /// the MIDI service and asks the service manager nothing
        /// (<see cref="MidiServiceRecovery.ServiceLastSeen"/>).</summary>
        internal static string DescribeRestartRequest(bool legacyCall, out bool restarts)
        {
            restarts = false;
            if (!MidiApiSelection.ServiceRestartCanHelp(MidiApiSelection.OsBuild, MidiApiSelection.ApiMode(), legacyCall)
                || !MidiServiceRecovery.ServiceLastSeen)
                return "no MIDI service restart reaches a classic MIDI driver";
            if (MidiServiceRecovery.CooldownRemainingMs > 0)
                return $"a MIDI service restart was tried less than {MidiServiceRecovery.CooldownMs / 1000} s ago, so none follows yet";
            restarts = true;
            return "restarting the MIDI service";
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
            int generation;
            try
            {
                // The generation is read before the backend, so a reset
                // between the two reads costs a rebuild instead of stamping a
                // torn-down backend's endpoint as current.
                generation = Generation;
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
                if (gen != Volatile.Read(ref _creationGen) || _connected)
                {
                    // Superseded while creating: this endpoint belongs to
                    // no one. Dispose it without touching instance fields.
                    TeardownLocalCreation(endpoint, uid);
                    return;
                }

                _endpoint = endpoint;
                ApiKind = kind;
                CreatedGeneration = generation;
                s_liveEndpoints[uid] = EndpointReady;

                // Initialize change detection arrays sized to match configured CC/note counts.
                _lastCcValues = new byte[CcNumbers.Length];
                for (int i = 0; i < _lastCcValues.Length; i++)
                    _lastCcValues[i] = 64; // center for axes
                _lastNotes = new bool[NoteNumbers.Length];
                _ccSent = new bool[CcNumbers.Length];
                StartSending();
                _connected = true;
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
            var done = new ManualResetEventSlim(false);
            Task.Run(() =>
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
                StopSending(drainMs: 0);
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
                // Held notes get their Note Off, and a switch controller
                // PadForge left on (sustain and the other pedals, CC 64-69)
                // gets its off, on the pair each went out on. MIDI 1.0 has a
                // damper hold every note on its channel, Note Offs included,
                // so a sustain left down at teardown kept the receiver's notes
                // sounding. No CC 121 or 123 goes out: on a shared legacy port
                // they reset every channel for every program (RtMidi issue
                // #222). Both ride the send queue, the Note Offs behind what
                // the polling thread queued last and a controller's off in
                // the place of any value still waiting for it, and they leave
                // before the port closes. A release the API refused before
                // the teardown and still owed goes ahead of them. No submit
                // follows from here, so the send worker sends a refused
                // release again itself while the drain lasts.
                if (_endpoint != null)
                {
                    QueueOwed(teardown: true);
                    var notes = _lastNotes;
                    if (notes != null)
                    {
                        for (int i = 0; i < notes.Length && i < NoteNumbers.Length; i++)
                        {
                            if (notes[i])
                                QueueSend(Midi1Status.NoteOff, NoteNumbers[i], 0, force: true);
                        }
                    }
                    ReleaseSwitchControllers(_lastCcValues, CcNumbers, _ccSent, keep: null);
                }
                _lastNotes = null;
                StopSending(drainMs: SendDrainMs);

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
            // Legacy path. Not used for dynamic MIDI. Kept for IVirtualController interface.
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
        /// channel and number survive keeps its last value. A switch
        /// controller (CC 64-69: sustain, portamento, sostenuto, soft pedal,
        /// legato, hold 2) that PadForge left on and whose pair does not
        /// survive gets its off on the old pair, since a sustain left down
        /// there holds every note on that channel. The rest start over as at
        /// connect, so the next submit presses held buttons on their new
        /// notes and sends each moved CC that is off center. A new velocity
        /// applies from the next Note On. A velocity of 0 sends 1, since a
        /// MIDI 1.0 Note On with velocity 0 is a Note Off.</para>
        ///
        /// <para>What the receiver is still owed after the API refused it
        /// stays owed through the edit, on the channel and number it went out
        /// on, and so does a release this edit queues
        /// (<see cref="QueueOwed"/>). The edit dropped what was owed, and a
        /// refused release of its own was never sent again, so a note or a
        /// pedal stayed down on the channel the slot had left.</para>
        /// </summary>
        internal void ApplyLayout(int channel, int startCc, int ccCount, int startNote, int noteCount, byte velocity)
        {
            Velocity = Math.Max(velocity, (byte)1);

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

            // Released before the channel moves, since a queued message
            // takes _channel as it is queued. Before Connect there is no
            // change state yet: ConnectCore sizes it from the numbers set
            // below.
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
                        QueueSend(Midi1Status.NoteOff, oldNotes[i], 0, force: true);
                }
            }

            var sentCcs = _lastCcValues;
            var oldCcs = CcNumbers;
            var ccSent = _ccSent;
            byte[] keptCcs = null;
            bool[] keptSent = null;
            if (sentCcs != null)
            {
                keptCcs = new byte[ccCount];
                keptSent = new bool[ccCount];
                var survives = new bool[Math.Min(sentCcs.Length, oldCcs.Length)];
                for (int i = 0; i < ccCount; i++)
                {
                    bool kept = sameChannel && i < sentCcs.Length && i < oldCcs.Length && ccNumbers[i] == oldCcs[i];
                    keptCcs[i] = kept ? sentCcs[i] : (byte)64; // center for axes, as at connect
                    keptSent[i] = kept && ccSent != null && i < ccSent.Length && ccSent[i];
                    if (kept) survives[i] = true;
                }
                ReleaseSwitchControllers(sentCcs, oldCcs, ccSent, survives);
            }

            _channel = channel;
            CcNumbers = ccNumbers;
            NoteNumbers = noteNumbers;
            if (keptNotes != null) _lastNotes = keptNotes;
            if (keptCcs != null)
            {
                _lastCcValues = keptCcs;
                _ccSent = keptSent;
            }
            _layoutStartCc = startCc;
            _layoutCcCount = ccCount;
            _layoutStartNote = startNote;
            _layoutNoteCount = noteCount;
            _layoutApplied = true;
        }

        /// <summary>The switch controllers of MIDI 1.0 (CC 64 sustain to
        /// CC 69 hold 2), read as on at 64 and above (NAudio
        /// MidiController, citing midi.org).</summary>
        private static bool IsSwitchController(int ccNumber) => ccNumber >= 64 && ccNumber <= 69;

        /// <summary>Queues an off for each switch controller PadForge left
        /// on, on the channel it went out on, skipping the entries
        /// <paramref name="keep"/> marks as surviving.</summary>
        private void ReleaseSwitchControllers(byte[] values, int[] numbers, bool[] sent, bool[] keep)
        {
            if (values == null || numbers == null || sent == null) return;
            int count = Math.Min(values.Length, Math.Min(numbers.Length, sent.Length));
            for (int i = 0; i < count; i++)
            {
                if (keep != null && i < keep.Length && keep[i]) continue;
                if (sent[i] && IsSwitchController(numbers[i]) && values[i] >= 64)
                    QueueSend(Midi1Status.ControlChange, numbers[i], 0, force: true);
            }
        }

        /// <summary>
        /// Sends MIDI messages from a MidiRawState with arbitrary CC and note counts.
        /// Only sends messages when values change (change detection per CC and per note),
        /// plus what the receiver is still owed after the API did not take
        /// it (<see cref="QueueOwed"/>). Each message
        /// is queued for the send worker, so the polling thread never waits on
        /// the API. A CC value takes the place of one still waiting for the
        /// same controller, so a port slower than the submits gets the newest
        /// value next. A note the worker has no room for stays uncommitted,
        /// and the next submit tries it again.
        /// </summary>
        public void SubmitMidiRawState(MidiRawState state)
        {
            if (!_connected || _endpoint == null) return;
            bool owed = Volatile.Read(ref _owedCount) != 0;
            int channel = _channel;

            // CCs. One the receiver is owed goes out again at the value the
            // control holds now, where change detection alone would send
            // nothing while the control rests.
            var lastCcs = _lastCcValues;
            if (state.CcValues != null && lastCcs != null)
            {
                var sent = _ccSent;
                int ccCount = Math.Min(state.CcValues.Length, Math.Min(lastCcs.Length, CcNumbers.Length));
                for (int i = 0; i < ccCount; i++)
                {
                    bool again = owed && Volatile.Read(ref _owedCc[WireKey(channel, CcNumbers[i])]) != 0;
                    if (state.CcValues[i] == lastCcs[i] && !again) continue;
                    if (!QueueSend(Midi1Status.ControlChange, CcNumbers[i], state.CcValues[i])) continue;
                    lastCcs[i] = state.CcValues[i];
                    if (sent != null && i < sent.Length) sent[i] = true;
                }
            }

            // Notes
            var lastNotes = _lastNotes;
            if (state.Notes != null && lastNotes != null)
            {
                int noteCount = Math.Min(state.Notes.Length, Math.Min(lastNotes.Length, NoteNumbers.Length));
                for (int i = 0; i < noteCount; i++)
                {
                    if (state.Notes[i] == lastNotes[i]) continue;
                    if (!QueueSend(state.Notes[i] ? Midi1Status.NoteOn : Midi1Status.NoteOff, NoteNumbers[i],
                            state.Notes[i] ? Velocity : 0))
                        continue;
                    lastNotes[i] = state.Notes[i];
                }
            }

            if (owed) QueueOwed(teardown: false);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            // MIDI has no rumble feedback. No-op.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ─────────────────────────────────────────────
        //  The send worker
        // ─────────────────────────────────────────────
        //
        // The polling thread never waits on a send. A WinMM short message
        // "might not return until the message has been sent to the output
        // device" (Microsoft's midiOutShortMsg remarks), about 1 ms per
        // message on a 31,250-baud DIN port. On the new MIDI stack wdmaud2
        // waits for the service's reader on every short message, up to 1 s
        // when the reader stalls (MidiXProc.cpp WaitForSendComplete), and a
        // Windows MIDI Services send waits 1 s once its shared ring is full.
        // So the polling thread queues each message and one worker per
        // controller sends them in order, the thread-and-wake shape of the
        // peripheral output host (PeripheralOutputHost). A message keeps the
        // channel it was queued on.
        //
        // The polling thread can submit faster than a port takes messages:
        // a stick on two CCs is about 2,000 messages a second at 1 kHz, and
        // a DIN port takes about 1,000. A queue that kept every value ran up
        // to its 512-message limit behind, so the synth went on sweeping
        // after the stick had stopped, and the stale values held the queue
        // full, where a button's Note On found no room. So a Control Change
        // is a value, not an event: one entry waits per channel and
        // controller number, a newer value takes its place there, and the
        // worker reads the value as it sends. Notes are events: each Note On
        // and Note Off is queued, sent in the order it was queued, and never
        // replaced. What waits is then at most one entry for each controller
        // plus the notes not yet sent, however long a control moves.

        private readonly struct QueuedSend
        {
            public QueuedSend(Midi1Status status, int channel, int data1, int data2)
            {
                Status = status;
                Channel = channel;
                Data1 = data1;
                Data2 = data2;
            }

            public readonly Midi1Status Status;
            public readonly int Channel, Data1, Data2;
        }

        /// <summary>A controller value waiting for the worker.</summary>
        private struct WaitingCc
        {
            public bool Waiting;
            public int Value;
        }

        /// <summary>A channel and a note or controller number as the wire
        /// word packs them: 4 bits of channel, 7 of number.</summary>
        private static int WireKey(int channel, int number) => ((channel & 0xF) << 7) | (number & 0x7F);

        /// <summary>The most note messages waiting for the worker.
        /// Controller values do not count against it and are never refused:
        /// one waits per controller. It is reached only when notes keep
        /// arriving faster than the port takes them, which for a hand on
        /// buttons means a port that has stopped taking messages, and it is
        /// what bounds how far behind the notes can run there. A press or
        /// release that finds it reached is not queued, stays uncommitted,
        /// and is tried again on the next submit, so it goes out once the
        /// port moves if the button is still where it was. A teardown's and
        /// a layout edit's releases are queued past it. A Note Off still
        /// owed after a refusal waits for room like any note, and stays
        /// owed meanwhile.</summary>
        internal const int SendQueueLimit = 512;

        /// <summary>How long a teardown waits for the queued messages, its
        /// own Note Offs included, to go out before it closes the port.
        /// Tests set it.</summary>
        internal static int SendDrainMs = 3_000;

        private readonly object _sendLock = new();
        private readonly Queue<QueuedSend> _sendQueue = new();
        // Under _sendLock: the controller values that wait, by channel and
        // then controller number (a channel's row is made at its first
        // value), and how many of the queue's entries are notes.
        private readonly WaitingCc[][] _waitingCcs = new WaitingCc[16][];
        private int _notesQueued;
        private Thread _sendThread;
        private bool _sendStop;
        private int _sendsPending;

        // Under _sendLock. Closing: the teardown has begun, no submit
        // follows, and the worker sends a refused release again itself.
        // Drain over: the teardown's drain has ended, what still waits is
        // dropped, and nothing is queued again.
        private bool _sendClosing;
        private bool _sendDrainOver;

        private void StartSending()
        {
            lock (_sendLock)
            {
                if (_sendThread != null) return;
                _sendStop = false;
                _sendClosing = false;
                _sendDrainOver = false;
                _sendThread = new Thread(SendLoop) { IsBackground = true, Name = $"MidiSend {_instanceNum}" };
                _sendThread.Start();
            }
        }

        /// <summary>Asks the worker to send what is queued and end, and waits
        /// up to <paramref name="drainMs"/> for it. A worker held in a send
        /// past that ends on its own once the send returns. Then the drain is
        /// over: what still waits is dropped and no refused release is sent
        /// again, so nothing more reaches the port once it closes but a send
        /// the worker was already in.</summary>
        private void StopSending(int drainMs)
        {
            Thread thread;
            lock (_sendLock)
            {
                thread = _sendThread;
                _sendThread = null;
                _sendStop = true;
                Monitor.PulseAll(_sendLock);
            }
            if (thread != null && drainMs > 0 && thread != Thread.CurrentThread)
            {
                try { thread.Join(drainMs); } catch { }
            }
            lock (_sendLock)
            {
                _sendDrainOver = true;
                int dropped = _sendQueue.Count;
                _sendQueue.Clear();
                _notesQueued = 0;
                foreach (var row in _waitingCcs)
                    if (row != null) Array.Clear(row);
                if (dropped > 0) Interlocked.Add(ref _sendsPending, -dropped);
            }
        }

        /// <summary>Queues one message for the worker on the slot's channel
        /// (<see cref="EnqueueLocked"/>). False when the message was not
        /// queued: the worker is stopping, or it is a note,
        /// <see cref="SendQueueLimit"/> notes wait already and
        /// <paramref name="force"/> is not set.</summary>
        private bool QueueSend(Midi1Status status, int data1, int data2, bool force = false)
        {
            lock (_sendLock)
            {
                if (_sendThread == null) return false;
                return EnqueueLocked(status, _channel, data1, data2, force);
            }
        }

        /// <summary>Queues one message on <paramref name="channel"/>, under
        /// <see cref="_sendLock"/>. A Control Change takes the place of a
        /// value still waiting for the same channel and controller, and is
        /// never refused for room. A message queued for a channel and number
        /// settles what the receiver was owed there: a newer value, a press
        /// that takes the place of a Note Off, or the release itself. False
        /// when it is a note, <see cref="SendQueueLimit"/> notes wait already
        /// and <paramref name="force"/> is not set.</summary>
        private bool EnqueueLocked(Midi1Status status, int channel, int data1, int data2, bool force)
        {
            int key = WireKey(channel, data1);
            if (status == Midi1Status.ControlChange)
            {
                var row = _waitingCcs[channel & 0xF] ??= new WaitingCc[128];
                ref var waiting = ref row[data1 & 0x7F];
                bool inLine = waiting.Waiting;
                waiting = new WaitingCc { Waiting = true, Value = data2 };
                SettleCc(key);
                if (inLine) return true;
                // Its place in line. The value is read as it is sent.
                _sendQueue.Enqueue(new QueuedSend(status, channel, data1, 0));
            }
            else
            {
                if (!force && _notesQueued >= SendQueueLimit) return false;
                SettleNoteOff(key);
                _notesQueued++;
                _sendQueue.Enqueue(new QueuedSend(status, channel, data1, data2));
            }
            Interlocked.Increment(ref _sendsPending);
            Monitor.Pulse(_sendLock);
            return true;
        }

        private void SendLoop()
        {
            while (true)
            {
                QueuedSend item;
                lock (_sendLock)
                {
                    while (_sendQueue.Count == 0)
                    {
                        if (_sendStop) return;
                        Monitor.Wait(_sendLock);
                    }
                    item = _sendQueue.Dequeue();
                    if (item.Status == Midi1Status.ControlChange)
                    {
                        // The value the controller holds now, which may be
                        // newer than the one that took this place in line.
                        ref var waiting = ref _waitingCcs[item.Channel & 0xF][item.Data1 & 0x7F];
                        item = new QueuedSend(item.Status, item.Channel, item.Data1, waiting.Value);
                        waiting.Waiting = false;
                    }
                    else
                    {
                        _notesQueued--;
                    }
                }
                try
                {
                    if (!SendNow(item)) Refused(item);
                }
                finally
                {
                    Interlocked.Decrement(ref _sendsPending);
                }
            }
        }

        /// <summary>Sends one message and says whether the API took it. A
        /// dying or restarting service must fail a message, never the
        /// worker.</summary>
        private bool SendNow(in QueuedSend item)
        {
            var endpoint = _endpoint;
            if (endpoint == null) return false;
            try { return endpoint.Send(item.Status, item.Channel, item.Data1, item.Data2); }
            catch { return false; }
        }

        /// <summary>Whether the port is gone for good: closed, or a legacy
        /// port whose device left (<see cref="EndpointLost"/>). No send
        /// reaches it, so nothing is owed to it.</summary>
        private bool PortGone
        {
            get
            {
                var endpoint = _endpoint;
                return endpoint == null || endpoint.Lost;
            }
        }

        /// <summary>A message that lets something go: a Note Off, or a
        /// switch controller below 64.</summary>
        private static bool IsRelease(in QueuedSend item)
            => item.Status == Midi1Status.NoteOff
               || (item.Status == Midi1Status.ControlChange && IsSwitchController(item.Data1) && item.Data2 < 64);

        /// <summary>What follows a message the API did not take. A Note Off
        /// or a controller's value is owed to the receiver on the channel and
        /// number it went out on, and the next submit sends it again
        /// (<see cref="QueueOwed"/>). A Note On is never sent twice, since a
        /// duplicate would retrigger the note. Once the teardown has begun no
        /// submit follows, so the worker sends a refused release again
        /// itself, a millisecond apart, until the port takes it, the port is
        /// gone, or the drain is over (<see cref="StopSending"/>). Nothing is
        /// owed to a port that is gone.</summary>
        private void Refused(in QueuedSend item)
        {
            if (item.Status == Midi1Status.NoteOn || PortGone) return;
            lock (_sendLock)
            {
                if (_sendDrainOver) return;
                if (!_sendClosing)
                {
                    Owe(item);
                    return;
                }
                if (!IsRelease(item)) return;
            }
            Thread.Sleep(1);
            lock (_sendLock)
            {
                if (_sendDrainOver) return;
                EnqueueLocked(item.Status, item.Channel, item.Data1, item.Data2, force: true);
            }
        }

        /// <summary>Records what a refused message leaves owed, under
        /// <see cref="_sendLock"/>.</summary>
        private void Owe(in QueuedSend item)
        {
            int key = WireKey(item.Channel, item.Data1);
            if (item.Status == Midi1Status.NoteOff)
            {
                if (_owedNoteOff[key]) return;
                _owedNoteOff[key] = true;
            }
            else
            {
                bool owedAlready = _owedCc[key] != 0;
                Volatile.Write(ref _owedCc[key], item.Data2 + 1);
                if (owedAlready) return;
            }
            Volatile.Write(ref _owedCount, _owedCount + 1);
        }

        private void SettleNoteOff(int key)
        {
            if (!_owedNoteOff[key]) return;
            _owedNoteOff[key] = false;
            Volatile.Write(ref _owedCount, _owedCount - 1);
        }

        private void SettleCc(int key)
        {
            if (_owedCc[key] == 0) return;
            Volatile.Write(ref _owedCc[key], 0);
            Volatile.Write(ref _owedCount, _owedCount - 1);
        }

        /// <summary>
        /// Queues what the receiver is still owed. A Note Off goes out again
        /// on the channel and key it was queued for, unless the slot holds
        /// that note down now: a press since took its place, or the teardown
        /// releases the note itself. A controller the slot's layout owns gets
        /// its current value again from the submit's own loop. On a pair the
        /// slot left, or at the teardown, only a release is still the slot's
        /// to send, as a pedal let up, and any other value is dropped.
        ///
        /// <para>On a submit a Note Off waits for room like any note. At the
        /// teardown (<paramref name="teardown"/>) every release goes past the
        /// limit, and from then on the worker sends a refused release again
        /// itself (<see cref="Refused"/>).</para>
        /// </summary>
        private void QueueOwed(bool teardown)
        {
            lock (_sendLock)
            {
                if (teardown) _sendClosing = true;
                if (_owedCount == 0) return;
                int channel = _channel;
                var notes = NoteNumbers;
                var held = _lastNotes;
                var ccs = CcNumbers;
                for (int key = 0; key < _owedNoteOff.Length && _owedCount > 0; key++)
                {
                    int ch = key >> 7, number = key & 0x7F;
                    if (_owedNoteOff[key])
                    {
                        if (HeldDown(ch, number, channel, notes, held)) SettleNoteOff(key);
                        else EnqueueLocked(Midi1Status.NoteOff, ch, number, 0, force: teardown);
                    }
                    int value = _owedCc[key] - 1;
                    if (value < 0) continue;
                    if (!teardown && ch == channel && Array.IndexOf(ccs, number) >= 0) continue;
                    if (IsSwitchController(number) && value < 64)
                        EnqueueLocked(Midi1Status.ControlChange, ch, number, value, force: teardown);
                    else
                        SettleCc(key);
                }
            }
        }

        /// <summary>Whether the slot holds note <paramref name="number"/>
        /// down on channel <paramref name="ch"/>.</summary>
        private static bool HeldDown(int ch, int number, int channel, int[] notes, bool[] held)
        {
            if (ch != channel || held == null || notes == null) return false;
            for (int i = 0; i < held.Length && i < notes.Length; i++)
                if (held[i] && notes[i] == number) return true;
            return false;
        }

        /// <summary>Test seam: waits until every queued message has been
        /// handed to the endpoint.</summary>
        internal bool WaitForSends(int timeoutMs = 5_000)
            => SpinWait.SpinUntil(() => Volatile.Read(ref _sendsPending) == 0, timeoutMs);

        /// <summary>Test seam: how many messages wait behind the one the
        /// worker is sending.</summary>
        internal int QueuedSends
        {
            get { lock (_sendLock) return _sendQueue.Count; }
        }

        /// <summary>Test seam: how many messages the port has not answered
        /// yet, the one the worker is sending included.</summary>
        internal int PendingSends => Volatile.Read(ref _sendsPending);

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
            // Without this the next device sweep re-probes (the release
            // clears the cached answer), opens a new initializer and
            // sessions on the runtime the uninstaller is removing. #128's
            // input enumeration runs that sweep on a timer, so the window is
            // about a second wide.
            if (_runtimeSuppressed) return false;
            int state = _probeState;
            if (state != ProbeNotRun) return state == ProbeAvailable;

            // Same bounded contract as Connect/Disconnect: activating the
            // API and EnsureServiceAvailable are WinRT calls and can hang on
            // a broken service. A probe an ask gave up on reads as
            // unavailable until it answers or the recovery below gets an
            // answer, and every ask meanwhile keeps that recovery going.
            if (_probeTimedOut)
            {
                RecoverFromHungProbe();
                return false;
            }

            // No probe starts inside a service restart PadForge is
            // performing. The service is stopping or starting then, a
            // Windows MIDI Services API that refuses there leaves the legacy
            // API in its place, and every MIDI slot rebuilds with no port.
            // The restart ends in a reset, and the ask after it probes the
            // instance that runs.
            if (MidiServiceRecovery.RestartInFlight) return false;

            var probe = StartProbe();
            if (AwaitProbe(probe)) return probe.Result;

            // Slow or hung: every later ask fails fast instead of re-paying
            // the wait, and the recovery watches the probe off this thread,
            // so MIDI comes back without anything else restarting.
            RecoverFromHungProbe();
            return false;
        }

        /// <summary>How long an ask waits for one availability probe. A
        /// probe still running then reads as unavailable and is left to the
        /// recovery, which keeps its answer if one comes. Tests shorten
        /// it.</summary>
        internal static int ProbeTimeoutMs = 10_000;

        /// <summary>How long the recovery waits for a hung probe to return
        /// once the service under it was restarted. Tests shorten it.</summary>
        internal static int HungProbeReturnMs = 30_000;

        private sealed class Probe
        {
            public readonly long StartedMs = Environment.TickCount64;
            public readonly ManualResetEventSlim Done = new(false);

            /// <summary>Completes with <see cref="Done"/>, for the recovery,
            /// which watches a probe without parking a thread on it.</summary>
            public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public volatile bool Result;

            /// <summary>Set under <see cref="s_hungLock"/> once the probe can
            /// hang no more: as it keeps its answer, and as it returns. An
            /// ask whose wait runs out in that instant does not mark it
            /// hung.</summary>
            public bool Returned;

            /// <summary>Whether the probe's answer became the cached
            /// one.</summary>
            public volatile bool KeptItsAnswer;
        }

        private static Probe StartProbe()
        {
            var probe = new Probe();
            Task.Run(() =>
            {
                try { probe.Result = IsAvailableCore(probe); }
                catch { /* unavailable */ }
                finally
                {
                    lock (s_hungLock) probe.Returned = true;
                    probe.Done.Set();
                    probe.Finished.TrySetResult();
                }
            });
            return probe;
        }

        /// <summary>Waits for a probe within <see cref="ProbeTimeoutMs"/>.
        /// False when it did not finish. It is then the hung probe the
        /// recovery watches, unless it kept its answer or returned in the
        /// last instant.</summary>
        private static bool AwaitProbe(Probe probe)
        {
            if (probe.Done.Wait(ProbeTimeoutMs)) return true;
            lock (s_hungLock)
            {
                if (!probe.Returned)
                {
                    s_hungProbe = probe;
                    _probeTimedOut = true;
                    return false;
                }
            }
            // It kept its answer or returned in the instant after the wait.
            // A probe that kept its answer has still to leave the lock and
            // hand back its result, so this waits for the result the ask
            // reads.
            probe.Done.Wait(ProbeTimeoutMs);
            return true;
        }

        // The hung state: _probeTimedOut, and the probe the recovery
        // watches, the newest one an ask gave up on. Every later probe queues
        // behind it on _availLock. Both change under s_hungLock, which is
        // taken inside _availLock and never around it.
        private static readonly object s_hungLock = new();
        private static volatile Probe s_hungProbe;

        // Whether the probe holding _availLock is in the legacy API's start,
        // for the restart gate (ServiceRestartCanHelp).
        private static volatile bool s_probeInLegacy;

        private static int s_probeRecoveryRunning;

        /// <summary>The floor under the recovery's wait for a cooldown to
        /// run out, so one that reads as just over never spins it.</summary>
        private const int RecoveryLookMs = 50;

        /// <summary>
        /// Watches a probe an ask gave up on until MIDI has an answer. A hung
        /// probe left the Settings card reading Not Running and every MIDI
        /// slot failing until a runtime was installed or removed, and the
        /// card told the user to restart Windows.
        ///
        /// <para>A probe that is slow, not hung, answers by itself, and that
        /// answer is kept: Microsoft's connectivity check waits up to 10 s
        /// for the service's device enumeration before it answers
        /// (MidiSrvTransport.cpp, g_midiSynchronizedStartTimeoutMs), which is
        /// the bound an ask gives a probe. So the probe is watched the whole
        /// time, and nothing is restarted while it can still be a slow one:
        /// the service restart comes for a probe still pending at the create
        /// path's bound (<see cref="ConnectTimeoutMs"/>), the bound a hung
        /// create gets before its own.</para>
        ///
        /// <para>From there the service is restarted once per cooldown until
        /// a probe has an answer, whether a restart was refused inside
        /// another restart's cooldown, failed, or left the service still
        /// hung. A probe the service was restarted under keeps no answer
        /// when it returns (its answer straddles the restart), so the probe
        /// runs again against the new instance. A probe that hangs in a
        /// classic MIDI driver, which no service restart reaches
        /// (<see cref="ServiceRestartCanHelp"/>), is waited for instead, and
        /// its answer is kept when it comes. The slots that failed for want
        /// of an API retry once the API settles (Step 5), and MIDI input
        /// reopens on the new backend. While an uninstall of the older
        /// runtime holds MIDI released, the recovery probes nothing.</para>
        /// </summary>
        private static void RecoverFromHungProbe()
        {
            if (Interlocked.Exchange(ref s_probeRecoveryRunning, 1) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    Probe graced = null;
                    bool loggedWait = false;
                    while (_probeTimedOut)
                    {
                        var hung = s_hungProbe;
                        if (hung == null) return;
                        if (_probeState != ProbeNotRun)
                        {
                            // An answer stands: nothing is hung any more.
                            lock (s_hungLock)
                            {
                                if (ReferenceEquals(s_hungProbe, hung))
                                {
                                    s_hungProbe = null;
                                    _probeTimedOut = false;
                                }
                            }
                            continue;
                        }

                        if (!hung.Finished.Task.IsCompleted)
                        {
                            int wait;
                            bool restarted = false;
                            if (!ServiceRestartCanHelp(s_probeInLegacy))
                            {
                                if (!loggedWait)
                                {
                                    loggedWait = true;
                                    SdlDiagLog.WriteLine("MIDIRECOVER the probe hangs in a classic MIDI driver: waiting for it without a service restart");
                                }
                                wait = HungProbeReturnMs;
                            }
                            else if (!ReferenceEquals(graced, hung))
                            {
                                // Up to the create path's bound the probe
                                // may be a slow one that answers by itself.
                                graced = hung;
                                wait = ProbeWatchMs(hung.StartedMs, Environment.TickCount64);
                            }
                            else
                            {
                                // Refused inside another restart's cooldown,
                                // or the instance did not end: the probe is
                                // watched through what is left of the
                                // cooldown, and the restart is tried again.
                                SyncPointForTest?.Invoke("recovery-before-restart");
                                var restart = MidiServiceRecovery.TryRestart("the availability probe hung");
                                restarted = restart is MidiServiceRestart.Running or MidiServiceRestart.EndedNotRunning;
                                wait = restarted
                                    ? HungProbeReturnMs
                                    : Math.Max(MidiServiceRecovery.CooldownRemainingMs, RecoveryLookMs);
                            }
                            bool returned = await ProbeReturnsWithin(hung, wait);
                            if (restarted && !returned)
                                SdlDiagLog.WriteLine("MIDIRECOVER the hung probe did not return after the service restart");
                            // A probe keeps no answer once a restart has
                            // begun under it, so one this probe kept came in
                            // the instant before, from the instance that has
                            // just ended.
                            if (restarted && hung.KeptItsAnswer) ResetAvailability();
                            continue;
                        }

                        // The probe returned and left no answer, as one the
                        // service was restarted under does. A new generation
                        // starts and the probe runs again.
                        if (!ResetAfterHungProbe(hung))
                        {
                            await Task.Delay(RecoveryLookMs).ConfigureAwait(false);
                            continue;
                        }
                        if (_runtimeSuppressed) return;
                        var again = StartProbe();
                        if (AwaitProbe(again))
                        {
                            LogProbeAnswer("the probe ran again");
                            return;
                        }
                    }
                }
                catch { /* best effort, like the create path's recovery */ }
                finally { Interlocked.Exchange(ref s_probeRecoveryRunning, 0); }
            });
        }

        /// <summary>How much longer the recovery only watches a probe before
        /// it restarts the service: what is left of the create path's bound
        /// (<see cref="ConnectTimeoutMs"/>) since the probe started.</summary>
        internal static int ProbeWatchMs(long probeStartedMs, long nowMs)
            => (int)Math.Clamp(ConnectTimeoutMs - (nowMs - probeStartedMs), 0, int.MaxValue);

        /// <summary>The recovery's reset once the probe it watched has
        /// returned with no answer. It resets only while that probe is still
        /// the hung one, since a reset elsewhere may have taken over, and it
        /// never waits for the lock: a probe started since may hold it, and
        /// the recovery is what watches that probe. False when it did not
        /// reset.</summary>
        private static bool ResetAfterHungProbe(Probe hung)
        {
            if (!Monitor.TryEnter(_availLock)) return false;
            try
            {
                if (!ReferenceEquals(s_hungProbe, hung)) return false;
                ResetLocked();
            }
            finally { Monitor.Exit(_availLock); }
            Interlocked.Increment(ref s_generation);
            return true;
        }

        /// <summary>Waits up to <paramref name="ms"/> for a probe to return,
        /// holding no thread meanwhile. True once it has.</summary>
        private static async Task<bool> ProbeReturnsWithin(Probe probe, int ms)
        {
            var returned = probe.Finished.Task;
            if (returned.IsCompleted || ms <= 0) return returned.IsCompleted;
            using var cancel = new CancellationTokenSource();
            var bound = Task.Delay(ms, cancel.Token);
            SyncPointForTest?.Invoke("recovery-watching");
            await Task.WhenAny(returned, bound).ConfigureAwait(false);
            cancel.Cancel();
            return returned.IsCompleted;
        }

        /// <summary>Writes the answer a probe left, for the diagnostics
        /// log.</summary>
        private static void LogProbeAnswer(string what)
        {
            int state = _probeState;
            if (state == ProbeAvailable)
                SdlDiagLog.WriteLine($"MIDIRECOVER {what}: MIDI available on {ActiveApi}");
            else if (state == ProbeUnavailable)
                SdlDiagLog.WriteLine($"MIDIRECOVER {what}: no MIDI API started");
        }

        /// <summary>Whether restarting midisrv can free a MIDI call that hangs
        /// or a session it refuses, on this PC
        /// (<see cref="MidiApiSelection.ServiceRestartCanHelp"/>): not on
        /// Windows before 24H2, which has no MIDI service, not in Legacy API
        /// mode, not for a legacy call in Hybrid mode, and not on a PC that
        /// has no MIDI service whatever its build
        /// (<see cref="MidiServiceRecovery.ServiceExists"/>). There a hang is
        /// a classic driver's, and a restart of a service that does not
        /// exist was tried once per cooldown.</summary>
        internal static bool ServiceRestartCanHelp(bool legacyCall)
            => MidiApiSelection.ServiceRestartCanHelp(MidiApiSelection.OsBuild, MidiApiSelection.ApiMode(), legacyCall)
               && MidiServiceRecovery.ServiceExists;

        /// <summary>Test seam: called at named points of the probe, of its
        /// recovery, of the check that follows a refusal, and of the
        /// uninstall's holds, its release and its end, so a test can hold
        /// one thread there while another runs, drive an interleaving in a
        /// fixed order, and learn where a thread is without reading its
        /// state. Null in production.</summary>
        internal static Action<string> SyncPointForTest;

        private static bool IsAvailableCore(Probe probe)
        {
            SyncPointForTest?.Invoke("probe-before-lock");
            bool refused;
            bool late;
            lock (_availLock)
            {
                int state = _probeState;
                if (state != ProbeNotRun) return state == ProbeAvailable;
                // An uninstall of the older runtime is in flight. Checked
                // under the lock, since StartProbe skips the check IsAvailable
                // makes, and a probe queued behind a reset must not load the
                // files the uninstaller is removing.
                if (_runtimeSuppressed) return false;

                // No service restart PadForge performs may be in flight as
                // the starts begin, and none may begin before they end. The
                // count is read first, so a restart that begins between the
                // two reads shows in one of them.
                int restartsBegun = MidiServiceRecovery.RestartsBegun;
                if (MidiServiceRecovery.RestartInFlight) return false;

                // Windows MIDI Services first. Where neither of its APIs
                // starts (none present, Legacy API mode, a service that
                // refuses), the legacy API, which every Windows has. A probe
                // that hangs in the first never reaches the second: the
                // bound in IsAvailable gives up on both, and WinMM routes
                // through the same service on the new MIDI stack anyway.
                // While an uninstall of the older runtime keeps Windows MIDI
                // Services from starting, the probe takes the legacy API and
                // reads as refused, so the check that follows a refusal tries
                // Windows MIDI Services once the uninstall is over.
                s_probeInLegacy = false;
                IMidiBackend backend = null;
                refused = WmsStartHeld;
                if (!refused) backend = TryStart(s_backendFactory, out refused);
                if (backend == null)
                {
                    s_probeInLegacy = true;
                    backend = TryStart(s_legacyFactory, out _);
                }
                if (MidiServiceRecovery.RestartsBegun != restartsBegun)
                {
                    // The service was restarted while this probe ran, so its
                    // answer straddles the restart: a Windows MIDI Services
                    // start the dying instance failed lands on the legacy
                    // API, on a PC whose new instance would take it. It
                    // keeps nothing and lets go of what it started. Whoever
                    // restarted the service resets, and the probe after that
                    // meets the instance that runs.
                    try { backend?.Stop(skipDispose: false); } catch { }
                    return false;
                }
                _backend = backend;
                s_wmsRefused = refused && backend != null;
                s_servicePid = backend != null && TalksToService(backend) ? MidiServiceRecovery.ServiceProcessId() : 0;
                _probeState = backend != null ? ProbeAvailable : ProbeUnavailable;
                // An answer stands, however long it took, so nothing is hung
                // any more: not this probe, and not one that queued behind
                // it and reads this answer next.
                lock (s_hungLock)
                {
                    late = _probeTimedOut;
                    probe.Returned = true;
                    s_hungProbe = null;
                    _probeTimedOut = false;
                }
                probe.KeptItsAnswer = true;
            }
            if (late) LogProbeAnswer("a probe answered after its bound");
            if (s_wmsRefused) RecheckRefusedApi();
            return _probeState == ProbeAvailable;
        }

        /// <summary>Creates and starts one backend, or returns null. A start
        /// that failed or threw partway may hold the App SDK runtime's
        /// initializer, so it is released here rather than abandoned. A
        /// failed start is logged with its API, since it otherwise reads
        /// only as the legacy API on the Settings card.
        /// <paramref name="refused"/> is set when the factory built a backend
        /// that did not start.</summary>
        private static IMidiBackend TryStart(Func<IMidiBackend> factory, out bool refused)
        {
            refused = false;
            IMidiBackend backend = null;
            try
            {
                backend = factory?.Invoke();
                if (backend == null) return null;
                if (backend.Start()) return backend;
                refused = true;
                SdlDiagLog.WriteLine($"MIDIPROBE MIDI {backend.Kind} did not start");
            }
            catch (Exception ex)
            {
                refused = backend != null;
                SdlDiagLog.WriteLine(backend != null
                    ? $"MIDIPROBE MIDI {backend.Kind} start threw {ex.GetType().Name} 0x{ex.HResult:X8}"
                    : $"MIDIPROBE MIDI API creation threw {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
            try { backend?.Stop(skipDispose: false); } catch { }
            return null;
        }

        /// <summary>The production backend: the API
        /// <see cref="MidiApiSelection.Choose(int, Func{int}, Func{bool}, out int)"/>
        /// picks for this PC, or null when neither is available. Runs inside
        /// the bounded probe, since activating the in-box API loads its DLL.
        /// An in-box API that Windows registers but that fails to activate is
        /// logged and kept for the Settings card, and the legacy API runs in
        /// its place.</summary>
        private static IMidiBackend CreateBackend()
        {
            if (RealBackendsOffLimitsForTest) return null;
            var kind = MidiApiSelection.Choose(
                MidiApiSelection.OsBuild,
                MidiApiSelection.ProbeInBoxActivation,
                MidiApiSelection.IsAppSdkRuntimeInstalled,
                out int inBoxFailure);
            s_inBoxFailureHr = inBoxFailure;
            if (inBoxFailure != 0)
                SdlDiagLog.WriteLine($"MIDIPROBE in-box activation failed 0x{inBoxFailure:X8}, legacy API");
            switch (kind)
            {
                case MidiApiKind.InBox: return new MidiBackendInBox();
                case MidiApiKind.AppSdk: return new MidiBackendAppSdk();
                default: return null;
            }
        }

        private static IMidiBackend CreateLegacyBackend()
            => RealBackendsOffLimitsForTest ? null : new MidiBackendLegacy();

        /// <summary>Test seam: set by the test assembly for the life of its
        /// process. The production factories then make no backend, so a
        /// probe that runs with no fake in place (between two tests, or on
        /// a worker that outlived its test) finds no MIDI API where it
        /// would have started Windows MIDI Services or WinMM on this PC.
        /// Each real backend refuses on its own account too
        /// (<see cref="MidiBackendInBox.OffLimitsForTest"/>,
        /// <see cref="MidiBackendAppSdk.OffLimitsForTest"/>,
        /// <see cref="WinMmMidi.OffLimitsForTest"/>). False in production,
        /// where nothing sets it.</summary>
        internal static bool RealBackendsOffLimitsForTest;

        /// <summary>The backend the last successful probe started, or null.
        /// MIDI input rides the same one.</summary>
        internal static IMidiBackend Backend => _backend;

        /// <summary>The API in use: the last successful probe's, else
        /// None.</summary>
        internal static MidiApiKind ActiveApi
            => _probeState == ProbeAvailable ? (_backend?.Kind ?? MidiApiKind.None) : MidiApiKind.None;

        /// <summary>True when a probe ran and found no working API, or an ask
        /// gave up on a probe that has not answered yet, and nothing has
        /// reset it since. The Settings card reads it as Not
        /// Running.</summary>
        internal static bool ProbeFailed
        {
            get
            {
                int state = _probeState;
                return state == ProbeUnavailable || (_probeTimedOut && state == ProbeNotRun);
            }
        }

        /// <summary>Why MIDI is not running, for the Settings card's detail
        /// line while <see cref="ProbeFailed"/> or the uninstall latch
        /// holds.</summary>
        internal static MidiNotRunningReason NotRunningReason
            => _runtimeSuppressed ? MidiNotRunningReason.Uninstalling
                : ProbeHungInDriver ? MidiNotRunningReason.DriverDidNotRespond
                : MidiNotRunningReason.ServiceDidNotRespond;

        /// <summary>True while a probe an ask gave up on hangs in a classic
        /// MIDI driver, which no service restart reaches
        /// (<see cref="ServiceRestartCanHelp"/>).</summary>
        internal static bool ProbeHungInDriver => _probeTimedOut && !ServiceRestartCanHelp(s_probeInLegacy);

        private static volatile int s_inBoxFailureHr;

        /// <summary>The HRESULT of an in-box API that Windows registers but
        /// that failed to activate at the last probe, or 0. The legacy API
        /// runs in its place, and the Settings card names the
        /// failure.</summary>
        internal static int InBoxActivationFailure => s_inBoxFailureHr;

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): replaces
        /// the backend factories and clears the cached probe. A test that
        /// names no legacy factory gets no fallback, so a fake that refuses
        /// to start never reaches the real WinMM. The API mode reads as full
        /// Windows MIDI Services on Windows 11 24H2 and no service instance is
        /// watched, so a test never depends on this PC's Windows, its
        /// registry or its midisrv. Nothing is left holding the uninstall
        /// latch. A null <paramref name="factory"/> restores both production
        /// factories and the real readings. The factories change under the
        /// lock, with the reset, so a start a test left under way (a probe,
        /// or the check that follows a refusal) never picks up the next
        /// test's factory, or the production one.</summary>
        internal static void UseBackendFactoryForTest(Func<IMidiBackend> factory, Func<IMidiBackend> legacyFactory = null)
        {
            MidiApiSelection.ApiModeSeam = factory == null ? null : () => MidiApiSelection.ApiModeFull;
            MidiApiSelection.OsBuildSeam = factory == null ? null : () => MidiApiSelection.AppSdkMinBuild;
            MidiServiceRecovery.ProcessIdSeam = factory == null ? null : () => 0u;
            MidiServiceRecovery.ServiceExistsSeam = factory == null ? null : () => true;
            MidiServiceRecovery.ForgetServiceAnswerForTest();
            lock (_availLock)
            {
                s_backendFactory = factory ?? CreateBackend;
                s_legacyFactory = factory == null ? CreateLegacyBackend : legacyFactory;
                s_uninstallHolds = 0;
                Interlocked.Exchange(ref s_startHolds, 0);
                _runtimeSuppressed = false;
                ResetLocked();
            }
            Interlocked.Increment(ref s_generation);
        }

        /// <summary>
        /// Resets the cached availability check so the next call to
        /// IsAvailable() re-evaluates, and starts a new generation. Call after
        /// the older runtime is installed. Like the resets PadForge makes on
        /// its own (a service restart, a service instance that changed, a
        /// Windows MIDI Services API that starts after all), it leaves the
        /// uninstall latch up, since a probe then could load the files the
        /// uninstaller is removing. Only the end of the release that set the
        /// latch lifts it (<see cref="EndUninstallHold"/>).
        /// </summary>
        public static void ResetAvailability()
        {
            lock (_availLock) ResetLocked();
            Interlocked.Increment(ref s_generation);
        }

        /// <summary>The reset itself, under <see cref="_availLock"/>. The
        /// hung state changes only here, once the lock is held: a reset that
        /// waits behind a probe hung on a dead service leaves the recovery's
        /// flags alone meanwhile, so the recovery still restarts the service
        /// that frees the probe and the reset with it.</summary>
        private static void ResetLocked()
        {
            lock (s_hungLock)
            {
                _probeTimedOut = false;
                s_hungProbe = null;
            }
            var backend = _backend;
            _backend = null;
            backend?.Stop(skipDispose: false);
            _probeState = ProbeNotRun;
            s_wmsRefused = false;
            s_servicePid = 0;
            Interlocked.Exchange(ref s_serviceDownSinceMs, 0);
            s_inBoxFailureHr = 0;
        }

        private static int s_generation;

        /// <summary>Bumped by every reset: a runtime install or uninstall,
        /// every service restart PadForge performs, and a service instance
        /// that changed under PadForge (<see cref="CheckServiceInstance"/>).
        /// A controller or input session made in an older generation talks to
        /// a backend or a service that is gone, so Step 5 rebuilds the slot
        /// and the input sweep reopens its connections.</summary>
        internal static int Generation => Volatile.Read(ref s_generation);

        // ── The service instance the probe found ──

        // midisrv's process id when the probe settled on an API that runs
        // through it, else 0.
        private static volatile uint s_servicePid;

        // When the recorded instance was first seen not running, or 0.
        private static long s_serviceDownSinceMs;

        /// <summary>How long the service may read as not running before MIDI
        /// is rebuilt all the same: the 30 s a restart PadForge performs
        /// gives the new instance to be running. Tests shorten it.</summary>
        internal static int ServiceDownResetMs = 30_000;

        /// <summary>Whether <paramref name="backend"/> runs through midisrv:
        /// both Windows MIDI Services APIs, and WinMM where it runs through
        /// the service (<see cref="MidiApiSelection.WinMmUsesService"/>):
        /// the new MIDI stack in full Windows MIDI Services mode, where
        /// wdmaud2 hands every port to it.</summary>
        private static bool TalksToService(IMidiBackend backend)
            => backend.Kind != MidiApiKind.Legacy
               || MidiApiSelection.WinMmUsesService(MidiApiSelection.OsBuild, MidiApiSelection.ApiMode());

        /// <summary>
        /// Starts a new generation when the MIDI service the probe found is
        /// no longer the instance running: restarted from Microsoft's MIDI
        /// Settings, its console or the Services app, or restarted by the
        /// service manager after a crash. A stopping service destroys every
        /// virtual device it made (Midi2.VirtualMidiEndpointManager.cpp),
        /// and a connection has no auto-reconnect unless asked for, so a
        /// slot kept sending into a dead ring with nothing to say so. The
        /// generation bump rebuilds every running MIDI slot and reopens MIDI
        /// input, as after a restart PadForge performs. Called from the
        /// MIDI input sweep's enumeration worker, off the polling thread.
        ///
        /// <para>The reset waits for an instance that runs. While the service
        /// stops and starts it reads as not running, and a probe then lands
        /// inside the restart: a Windows MIDI Services API refuses there, the
        /// legacy API takes its place, and every slot rebuilds with no port.
        /// A service that stays down past <see cref="ServiceDownResetMs"/>
        /// rebuilds MIDI all the same. A restart PadForge performs is left
        /// to its own reset.</para>
        /// </summary>
        internal static void CheckServiceInstance()
        {
            uint recorded = s_servicePid;
            if (recorded == 0 || _probeState != ProbeAvailable) return;
            if (MidiServiceRecovery.RestartInFlight) return;
            uint now = MidiServiceRecovery.ServiceProcessId();
            if (now == recorded)
            {
                Interlocked.Exchange(ref s_serviceDownSinceMs, 0);
                return;
            }
            if (now == 0)
            {
                long tick = Math.Max(1, Environment.TickCount64);
                long since = Interlocked.CompareExchange(ref s_serviceDownSinceMs, tick, 0);
                if (since == 0 || tick - since < ServiceDownResetMs) return;
            }

            // Only while that instance is still the one recorded: a reset
            // elsewhere, and the probe after it, may have recorded the new
            // one already.
            lock (_availLock)
            {
                if (s_servicePid != recorded) return;
                ResetLocked();
            }
            Interlocked.Increment(ref s_generation);
            SdlDiagLog.WriteLine(now == 0
                ? $"MIDIRECOVER midisrv ({recorded}) stayed down: rebuilding MIDI"
                : $"MIDIRECOVER midisrv changed under PadForge ({recorded} to {now}): rebuilding MIDI");
        }

        // ── A Windows MIDI Services API that refused the probe ──

        // Set when the committed probe fell back to the legacy API because a
        // Windows MIDI Services backend did not start.
        private static volatile bool s_wmsRefused;
        // The generation the newest refusal check serves. One check runs
        // per generation: a check from an older one ends at its next look,
        // and never holds back the check of a newer probe.
        private static int s_refusalRecheckGen = -1;
        private static int s_trialRunning;

        /// <summary>How long after a refusal the first check runs. Later
        /// checks wait the recovery cooldown. Tests shorten it.</summary>
        internal static int RefusalRecheckMs = 10_000;

        /// <summary>
        /// A Windows MIDI Services API that refused the probe leaves the
        /// legacy API running, and that answer stood for the life of the
        /// process. A refusal can pass on its own: EnsureServiceAvailable
        /// returns false on any failed connectivity RPC (Microsoft's
        /// MidiSrvTransport.cpp VerifyConnectivity), such as one made while
        /// the demand-started service was still starting at sign-in. So
        /// while the legacy API runs for that reason, a fresh Windows MIDI
        /// Services backend is started beside the one in use, first after
        /// <see cref="RefusalRecheckMs"/> and then once per recovery
        /// cooldown, and once one starts the probe runs again and takes it.
        /// Nothing here restarts the service: MIDI keeps running on the
        /// legacy API meanwhile, and a restart would drop other programs'
        /// MIDI sessions on a PC whose MIDI works. In Legacy API mode the
        /// service never runs, so nothing is checked. While an uninstall of
        /// the older runtime runs, a check starts nothing
        /// (<see cref="TrialStartsWindowsMidiServices"/>), and the checks go
        /// on once it is over.
        /// </summary>
        private static void RecheckRefusedApi()
        {
            int generation = Generation;
            if (Interlocked.Exchange(ref s_refusalRecheckGen, generation) == generation) return;
            Task.Run(async () =>
            {
                try
                {
                    for (int check = 1; ; check++)
                    {
                        await Task.Delay(check == 1 ? RefusalRecheckMs : MidiServiceRecovery.CooldownMs);
                        if (Generation != generation || !s_wmsRefused || _probeState != ProbeAvailable
                            || _runtimeSuppressed || MidiApiSelection.ApiMode() == MidiApiSelection.ApiModeLegacy)
                            return;
                        SyncPointForTest?.Invoke("recheck-look");
                        if (!TrialStartsWindowsMidiServices()) continue;
                        // The probe in the new generation starts a check of its
                        // own if Windows MIDI Services refuses it again.
                        SdlDiagLog.WriteLine("MIDIPROBE Windows MIDI Services starts now: probing again");
                        ResetAvailability();
                        if (_runtimeSuppressed) return;
                        if (!AwaitProbe(StartProbe())) RecoverFromHungProbe();
                        return;
                    }
                }
                catch { /* best effort */ }
            });
        }

        /// <summary>Starts a Windows MIDI Services backend on the side, within
        /// <see cref="ProbeTimeoutMs"/>, and lets it go. True when it started.
        /// The backend in use is not touched. One trial runs at a time: a
        /// trial still held by a hung service is not joined by another. A
        /// trial begins only while the legacy API still runs for a refusal
        /// and no uninstall of the older runtime holds Windows MIDI Services
        /// from starting, both read under the lock a hold is taken under, and
        /// a hold waits for a trial already begun (<see cref="AwaitTrial"/>).
        /// So no trial starts the runtime, or is still starting it, once an
        /// uninstall holds it.</summary>
        private static bool TrialStartsWindowsMidiServices()
        {
            if (Interlocked.Exchange(ref s_trialRunning, 1) != 0) return false;
            bool started = false;
            var done = new ManualResetEventSlim(false);
            Task.Run(() =>
            {
                IMidiBackend trial = null;
                bool begun = false;
                try
                {
                    Func<IMidiBackend> factory;
                    lock (_availLock)
                    {
                        begun = !WmsStartHeld && s_wmsRefused && _probeState == ProbeAvailable;
                        s_trialStarting = begun;
                        factory = s_backendFactory;
                    }
                    if (begun)
                    {
                        trial = factory?.Invoke();
                        started = trial != null && trial.Start();
                    }
                }
                catch { started = false; }
                finally
                {
                    try { trial?.Stop(skipDispose: false); } catch { }
                    if (begun)
                    {
                        lock (_availLock)
                        {
                            s_trialStarting = false;
                            Monitor.PulseAll(_availLock);
                        }
                    }
                    Interlocked.Exchange(ref s_trialRunning, 0);
                    done.Set();
                }
            });
            return done.Wait(ProbeTimeoutMs) && started;
        }

        // Whether a trial is between its start and its stop, under
        // _availLock.
        private static bool s_trialStarting;

        /// <summary>Waits, with <see cref="_availLock"/> held on entry and on
        /// return, until no trial start is under way. The lock is let go
        /// meanwhile, so a trial a hung service holds keeps nothing else
        /// waiting. Whoever takes a hold for an uninstall calls it before the
        /// hold, with no gap between the two: a trial that began just before
        /// would otherwise still be starting the runtime as its uninstaller
        /// started.</summary>
        private static void AwaitTrial()
        {
            while (s_trialStarting)
            {
                SyncPointForTest?.Invoke("hold-waits-for-trial");
                Monitor.Wait(_availLock);
            }
        }

        // ── The uninstall latch ──

        // How many releases hold MIDI released, and whether any does, for
        // the readers that take no lock. Both change under _availLock.
        private static int s_uninstallHolds;
        private static volatile bool _runtimeSuppressed;

        // How many uninstalls made under the legacy API hold only Windows
        // MIDI Services from starting. Taken under _availLock, so the API in
        // use cannot change as one is taken, and ended without it.
        private static int s_startHolds;

        // Whether anything holds Windows MIDI Services from starting. A
        // release does too.
        private static bool WmsStartHeld
            => Volatile.Read(ref s_uninstallHolds) != 0 || Volatile.Read(ref s_startHolds) != 0;

        /// <summary>True while an uninstall of the older runtime holds MIDI
        /// released (<see cref="SuppressForUninstall"/>). The Settings card
        /// reads it as Not Running.</summary>
        internal static bool IsSuppressedForUninstall => _runtimeSuppressed;

        /// <summary>True while an uninstall of the older runtime holds
        /// anything: MIDI released, or under the legacy API only Windows MIDI
        /// Services from starting.</summary>
        internal static bool IsHeldForUninstall => WmsStartHeld;

        /// <summary>True while an ask has given up on a probe that has not
        /// returned. Its recovery restarts the service where a restart can
        /// reach the hang, so nothing else restarts it on the probe's
        /// account.</summary>
        internal static bool ProbeHung => _probeTimedOut;

        /// <summary>
        /// Takes what an uninstall of the older runtime can hold without
        /// releasing MIDI, decided and taken under one lock so the API in use
        /// cannot change in between.
        ///
        /// <para>Under the in-box API nothing is held
        /// (<see cref="MidiUninstallHold.None"/>): it is part of Windows, and
        /// a probe there never reaches the runtime. Under the legacy API MIDI
        /// runs on WinMM ports, which the uninstaller does not touch, so MIDI
        /// keeps running, and only Windows MIDI Services is held from
        /// starting (<see cref="MidiUninstallHold.StartHeld"/>). Where the
        /// legacy API runs because the runtime's backend did not start,
        /// both the check that follows such a refusal
        /// (<see cref="RecheckRefusedApi"/>) and any probe after a reset
        /// would start that backend again, loading the files the uninstaller
        /// is removing.</para>
        ///
        /// <para>Otherwise the runtime is in use or nothing has started, and
        /// the answer is <see cref="MidiUninstallHold.Released"/> with
        /// nothing taken yet: the caller closes MIDI input and then calls
        /// <see cref="SuppressForUninstall"/>.</para>
        /// </summary>
        internal static MidiUninstallHold TryHoldWithoutRelease()
        {
            SyncPointForTest?.Invoke("hold-before-lock");
            lock (_availLock)
            {
                while (true)
                {
                    var backend = _probeState == ProbeAvailable ? _backend : null;
                    if (backend == null) return MidiUninstallHold.Released;
                    if (backend.Kind == MidiApiKind.InBox) return MidiUninstallHold.None;
                    if (backend.Kind != MidiApiKind.Legacy) return MidiUninstallHold.Released;
                    if (!s_trialStarting) break;
                    // The wait lets the lock go, and a trial that started
                    // Windows MIDI Services is followed by a reset and a
                    // probe, so the API in use is read again after it.
                    AwaitTrial();
                }
                Interlocked.Increment(ref s_startHolds);
                return MidiUninstallHold.StartHeld;
            }
        }

        /// <summary>
        /// Releases the older runtime and keeps it released, for its
        /// uninstall. Only the older runtime needs this: the in-box API is
        /// part of Windows and its uninstaller never touches it.
        ///
        /// <para>Releasing drops the backend: its App SDK initializer and,
        /// through MIDI input's shutdown before this, its sessions. It does
        /// not unload the runtime's DLLs. Microsoft's projection has the
        /// native Shutdown commented out of ShutdownSdkRuntime (rc-3 and rc-4
        /// MidiDesktopAppSdkInitializer.cs), and an activated WinRT DLL stays
        /// mapped for the life of the process anyway. The uninstall reaches
        /// its files because the window declines Restart Manager's request to
        /// close PadForge (MainWindow.OnQueryEndSession) and Windows Installer
        /// schedules held files for removal.</para>
        ///
        /// <para>The latch is the other half. The release clears the cached
        /// availability answer, so the next enumeration sweep would re-probe
        /// and open new initializers and sessions on the runtime while the
        /// uninstaller was removing it. Availability stays false until the
        /// release ends (<see cref="EndUninstallHold"/>), which the uninstall
        /// does once its uninstaller has exited. The latch is set once the
        /// lock is held, so the recovery of a probe that hangs meanwhile
        /// still restarts the service that frees it.</para>
        ///
        /// <para>Each release holds the latch on its own account, and the
        /// latch stands while any of them does. A release that misses its
        /// bound undoes itself whenever it gets through, and an uninstaller
        /// that outlives its wait ends its release when it exits, so two can
        /// overlap: a second uninstall while the first one's uninstaller
        /// still runs. One of them ending leaves the other its latch.</para>
        /// </summary>
        public static void SuppressForUninstall()
        {
            SyncPointForTest?.Invoke("latch-before-lock");
            lock (_availLock)
            {
                AwaitTrial();
                s_uninstallHolds++;
                _runtimeSuppressed = true;
                var backend = _backend;
                _backend = null;
                backend?.Stop(skipDispose: false);
                _probeState = ProbeNotRun;
            }
        }

        /// <summary>
        /// Ends one hold an uninstall took. Ending
        /// <see cref="MidiUninstallHold.StartHeld"/> lets Windows MIDI
        /// Services start again once nothing else holds it, and changes
        /// nothing else: MIDI kept running. Ending a release
        /// (<see cref="SuppressForUninstall"/>) lifts the latch when it was
        /// the last one, and resets, so the next ask probes again and takes
        /// the in-box API where Windows has it, the runtime if it is still
        /// installed, else the legacy API. True when the latch lifted. An
        /// end with nothing of its kind held changes nothing.
        ///
        /// <para>Ending <see cref="MidiUninstallHold.StartHeld"/> takes no
        /// lock. MIDI runs on the legacy API under that hold, and a probe
        /// there can hang in a classic driver with the lock held, which
        /// would keep the uninstall that just finished from returning to the
        /// card.</para>
        /// </summary>
        public static bool EndUninstallHold(MidiUninstallHold hold = MidiUninstallHold.Released)
        {
            if (hold == MidiUninstallHold.StartHeld)
            {
                int held;
                do
                {
                    held = Volatile.Read(ref s_startHolds);
                    if (held == 0) return false;
                }
                while (Interlocked.CompareExchange(ref s_startHolds, held - 1, held) != held);
                return false;
            }
            if (hold != MidiUninstallHold.Released) return false;
            lock (_availLock)
            {
                if (s_uninstallHolds == 0) return false;
                if (--s_uninstallHolds > 0) return false;
                _runtimeSuppressed = false;
                ResetLocked();
            }
            Interlocked.Increment(ref s_generation);
            return true;
        }

        /// <summary>
        /// Releases the MIDI API. Call on app exit, through
        /// <see cref="ShutdownForExit"/>.
        /// </summary>
        /// <param name="skipDispose">
        /// When true, drops the older runtime's initializer without calling
        /// its Dispose. No production caller passes it: Dispose makes no
        /// native call (Microsoft's projection has the native Shutdown
        /// commented out), so disposing is as safe while the service goes
        /// away as at any other time.
        /// </param>
        public static void Shutdown(bool skipDispose = false)
        {
            lock (_availLock)
            {
                var backend = _backend;
                _backend = null;
                backend?.Stop(skipDispose);
                _probeState = ProbeNotRun;
            }
        }

        /// <summary>App exit's MIDI teardown: the shared input session, then
        /// the API, on a worker, waited on for at most
        /// <paramref name="timeoutMs"/>. Both can wait on a dead service: the
        /// session's Dispose is service RPC (#1047), and the release waits
        /// behind a probe that hangs. A teardown that misses the bound is left
        /// to the process's exit, as <see cref="Disconnect"/> leaves a hung
        /// teardown, so PadForge still closes. True when it
        /// finished.</summary>
        internal static bool ShutdownForExit(int timeoutMs = DisconnectTimeoutMs)
        {
            var done = new ManualResetEventSlim(false);
            Task.Run(() =>
            {
                try
                {
                    MidiInputRuntime.Shutdown();
                    Shutdown();
                }
                catch { /* best effort */ }
                finally { done.Set(); }
            });
            return done.Wait(timeoutMs);
        }
    }
}
