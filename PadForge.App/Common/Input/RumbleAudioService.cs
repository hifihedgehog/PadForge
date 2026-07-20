using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Rumble-to-audio (bass shaker / LFE) renderer, issue #236. Routes
    /// the game feedback each slot's VIRTUAL CONTROLLER receives to WASAPI
    /// render endpoints as low-frequency sine tones.
    ///
    /// <para>Data path: VC output callbacks fill a controller-local pack;
    /// the poll thread's feedback lane evaluates the slot's four fixed
    /// voice bindings once per tick and publishes the result here
    /// (<see cref="PublishIfCurrent"/>); the render thread reads the
    /// published packs as its ONLY input. This class never references
    /// VibrationStates, FinalVibrationStates, macro rumble, test rumble,
    /// or any per-physical-device projection, which is what makes the
    /// audio-rumble feedback loop (shaker tone → loopback →
    /// AudioBassDetector → audio rumble → louder tone) impossible by
    /// construction.</para>
    ///
    /// <para>Players are keyed by ENDPOINT, not slot: all slots routed to
    /// one endpoint share a single WasapiOut, one shared sample clock, and
    /// one composite limiter, so equal-frequency voices across slots stay
    /// phase-locked and multi-slot peaks clamp once, coherently.</para>
    ///
    /// <para>Lifecycle: silence is an EXPLICIT EDGE, never an inference
    /// from callback inactivity. The poll lane republishes every tick
    /// while the pipeline runs; idle entry, engine stop, panic quiesce,
    /// and slot delete call <see cref="SilenceSlot"/> /
    /// <see cref="SilenceAll"/> synchronously, and the per-slot
    /// generation makes a stale in-flight poll publish lose against a
    /// newer silence edge. A configured-but-unresolved endpoint FAILS
    /// CLOSED (no fallback device).</para>
    /// </summary>
    internal static class RumbleAudioService
    {
        private const int MaxSlots = 16;

        // ── The published per-slot state (poll thread → render thread) ──
        private static readonly long[] _packs = new long[MaxSlots];
        private static readonly int[] _generations = new int[MaxSlots];
        private static readonly long[] _lastPublishMs = new long[MaxSlots];

        // ── Endpoint players ──
        private sealed class EndpointPlayer
        {
            public string EndpointId = "";      // resolved MMDevice.ID
            public WasapiOut Player;
            public RumbleAudioSampleProvider Provider;
            public string FriendlyName = "";
        }

        private static readonly object _lock = new();
        private static readonly List<EndpointPlayer> _players = new();
        private static Timer _reconcileTimer;
        private static int _reconcileBusy;

        /// <summary>Per-slot status line for the UI: null/empty = inactive,
        /// otherwise a resolved friendly name or an error marker the VM
        /// localizes. Written under _lock on reconcile, read by the UI.</summary>
        private static readonly string[] _slotStatus = new string[MaxSlots];

        // ── Poll-thread surface ──────────────────────────────────────────

        /// <summary>Current generation for <see cref="PublishIfCurrent"/>.
        /// The poll lane captures this BEFORE reading the VC pack, so a
        /// silence edge that lands mid-evaluation wins.</summary>
        public static int GetGeneration(int slot)
            => (uint)slot < MaxSlots ? Volatile.Read(ref _generations[slot]) : 0;

        /// <summary>Publishes a fully-evaluated pack unless a silence edge
        /// advanced the slot's generation since <paramref name="generation"/>
        /// was captured. Poll thread only. Also refreshes the watchdog
        /// timestamp, so a live producer republishing an unchanged latched
        /// pack keeps it sounding (Xbox rumble is latched state, not a
        /// keepalive; callback inactivity must never mean zero).</summary>
        public static void PublishIfCurrent(int slot, int generation, long packed)
        {
            if ((uint)slot >= MaxSlots) return;
            if (Volatile.Read(ref _generations[slot]) != generation) return;
            Volatile.Write(ref _packs[slot], packed);
            Volatile.Write(ref _lastPublishMs[slot], Environment.TickCount64);
        }

        /// <summary>Synchronous silence edge for one slot: advances the
        /// generation (so an in-flight poll publish is discarded) and
        /// zeroes ALL FOUR voices. Call on slot delete, disable, VC
        /// destroy, profile transitions, and reset.</summary>
        public static void SilenceSlot(int slot)
        {
            if ((uint)slot >= MaxSlots) return;
            Interlocked.Increment(ref _generations[slot]);
            Volatile.Write(ref _packs[slot], 0L);
            Volatile.Write(ref _lastPublishMs[slot], Environment.TickCount64);
        }

        /// <summary>Synchronous silence edge for every slot: idle entry,
        /// engine stop, panic quiesce.</summary>
        public static void SilenceAll()
        {
            for (int i = 0; i < MaxSlots; i++) SilenceSlot(i);
        }

        // ── Test tones (UI). Deliberately OUTSIDE the published packs so
        // the provenance rule stays intact: test audio never touches
        // VibrationStates (the FFB tab's test rumble does, which is one
        // of the reasons the audio path must not read that array), and
        // the packs stay pure game feedback. The provider mixes the test
        // lane in as extra per-voice target amplitude. ──

        private static readonly long[] _testPacks = new long[MaxSlots];
        private static readonly long[] _testExpiryMs = new long[MaxSlots];
        private static readonly long[] _sweepStartMs = new long[MaxSlots];
        private static readonly long[] _sweepEndMs = new long[MaxSlots];

        /// <summary>Plays the given voice at full authored gain for
        /// <paramref name="durationMs"/> (ramped by the provider's normal
        /// click-suppression envelope). UI thread.</summary>
        public static void PulseTestVoice(int slot, int voice, int durationMs)
        {
            if ((uint)slot >= MaxSlots || (uint)voice >= 4) return;
            long now = Environment.TickCount64;
            Volatile.Write(ref _testPacks[slot],
                Engine.Common.LfeOutputState.Pack(
                    voice == 0 ? (ushort)65535 : (ushort)0,
                    voice == 1 ? (ushort)65535 : (ushort)0,
                    voice == 2 ? (ushort)65535 : (ushort)0,
                    voice == 3 ? (ushort)65535 : (ushort)0));
            Volatile.Write(ref _testExpiryMs[slot], now + Math.Clamp(durationMs, 100, 10000));
            Volatile.Write(ref _sweepStartMs[slot], 0L);
            Volatile.Write(ref _sweepEndMs[slot], 0L);
        }

        /// <summary>Starts a 20..120 Hz frequency sweep on the slot's LOW
        /// voice routing (the resonance-finding tool). UI thread.</summary>
        public static void StartSweep(int slot, int durationMs)
        {
            if ((uint)slot >= MaxSlots) return;
            long now = Environment.TickCount64;
            Volatile.Write(ref _testPacks[slot], Engine.Common.LfeOutputState.Pack(65535, 0, 0, 0));
            Volatile.Write(ref _testExpiryMs[slot], now + Math.Clamp(durationMs, 500, 20000));
            Volatile.Write(ref _sweepStartMs[slot], now);
            Volatile.Write(ref _sweepEndMs[slot], now + Math.Clamp(durationMs, 500, 20000));
        }

        /// <summary>Stops any test tone / sweep on the slot immediately
        /// (the provider's ramp makes it click-free). UI thread.</summary>
        public static void StopTest(int slot)
        {
            if ((uint)slot >= MaxSlots) return;
            Volatile.Write(ref _testExpiryMs[slot], 0L);
            Volatile.Write(ref _testPacks[slot], 0L);
            Volatile.Write(ref _sweepStartMs[slot], 0L);
            Volatile.Write(ref _sweepEndMs[slot], 0L);
        }

        /// <summary>Render-thread read of the slot's active test pack
        /// (0 when expired) plus the sweep carrier override in Hz
        /// (0 = no sweep).</summary>
        public static long ReadTestPack(int slot, out int sweepCarrierHz)
        {
            sweepCarrierHz = 0;
            if ((uint)slot >= MaxSlots) return 0L;
            long now = Environment.TickCount64;
            if (now >= Volatile.Read(ref _testExpiryMs[slot])) return 0L;
            long start = Volatile.Read(ref _sweepStartMs[slot]);
            long end = Volatile.Read(ref _sweepEndMs[slot]);
            if (start > 0 && end > start)
            {
                float progress = Math.Clamp((now - start) / (float)(end - start), 0f, 1f);
                sweepCarrierHz = (int)(Engine.Data.RumbleAudioConfig.MinFrequencyHz
                    + progress * (Engine.Data.RumbleAudioConfig.MaxFrequencyHz
                                  - Engine.Data.RumbleAudioConfig.MinFrequencyHz));
            }
            return Volatile.Read(ref _testPacks[slot]);
        }

        // ── Render-thread surface ────────────────────────────────────────

        /// <summary>The published pack (render thread + UI meters).</summary>
        public static long ReadPack(int slot)
            => (uint)slot < MaxSlots ? Volatile.Read(ref _packs[slot]) : 0L;

        /// <summary>Watchdog timestamp of the last publish (render thread).</summary>
        public static long ReadLastPublishMs(int slot)
            => (uint)slot < MaxSlots ? Volatile.Read(ref _lastPublishMs[slot]) : 0L;

        /// <summary>UI status for the slot's endpoint row: null = no
        /// player, "!" prefix = fail-closed error marker.</summary>
        public static string GetSlotStatus(int slot)
            => (uint)slot < MaxSlots ? Volatile.Read(ref _slotStatus[slot]) : null;

        // ── Service lifecycle ────────────────────────────────────────────

        /// <summary>Starts the periodic reconcile worker. Idempotent; call
        /// on engine start and after any rumble-audio config edit.</summary>
        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_reconcileTimer != null) return;
                _reconcileTimer = new Timer(_ => { try { Reconcile(); } catch { } }, null, 0, 5000);
            }
        }

        /// <summary>Stops every player (with the click-suppression fade)
        /// and the worker. Engine shutdown / app exit.</summary>
        public static void StopAll()
        {
            Timer timer;
            List<EndpointPlayer> players;
            lock (_lock)
            {
                timer = _reconcileTimer;
                _reconcileTimer = null;
                players = new List<EndpointPlayer>(_players);
                _players.Clear();
                for (int i = 0; i < MaxSlots; i++) _slotStatus[i] = null;
            }
            timer?.Dispose();
            SilenceAll();
            foreach (var p in players) FadeStopDispose(p);
        }

        /// <summary>Rebuilds the endpoint→player table from the current
        /// per-slot configs. Runs on the timer worker and after config
        /// edits; never on the UI thread's critical path and never on the
        /// poll thread (WASAPI activation blocks).</summary>
        public static void Reconcile()
        {
            if (Interlocked.Exchange(ref _reconcileBusy, 1) == 1) return;
            try
            {
                ReconcileCore();
            }
            finally
            {
                Volatile.Write(ref _reconcileBusy, 0);
            }
        }

        private static void ReconcileCore()
        {
            // 1. Snapshot the desired routing: slot → (endpointId, config).
            //    Configs live on the slot MappingSets; reference reads are
            //    safe cross-thread (the UI swaps whole references).
            var sets = SettingsManager.SlotMappingSets;
            var desired = new List<(int Slot, RumbleAudioConfig Cfg)>();
            if (sets != null)
            {
                for (int slot = 0; slot < Math.Min(sets.Length, MaxSlots); slot++)
                {
                    var cfg = sets[slot]?.RumbleAudio;
                    if (cfg != null && cfg.Enabled) desired.Add((slot, cfg));
                }
            }

            string[] newStatus = new string[MaxSlots];

            // Nothing wanted and nothing playing: skip the COM enumerator
            // + default-endpoint query this 5 s pass otherwise pays for
            // an empty reconcile.
            if (desired.Count == 0)
            {
                bool anyPlayers;
                lock (_lock) anyPlayers = _players.Count > 0;
                if (!anyPlayers)
                {
                    for (int i = 0; i < MaxSlots; i++)
                        Volatile.Write(ref _slotStatus[i], newStatus[i]);
                    return;
                }
            }

            // 2. Resolve endpoints. "" = system default render endpoint;
            //    anything else must match an ACTIVE endpoint by ID or the
            //    slot fails closed for this pass.
            using var en = new MMDeviceEnumerator();
            string defaultId = null, defaultName = null;
            try
            {
                using var dd = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = dd.ID;
                defaultName = dd.FriendlyName;
            }
            catch { }

            // endpoint key → (resolved id, name, voices, uses default route)
            var wanted = new Dictionary<string, (string Name,
                List<RumbleAudioSampleProvider.Voice> Voices)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (slot, cfg) in desired)
            {
                string resolvedId = null, resolvedName = null;
                bool viaDefault = string.IsNullOrEmpty(cfg.EndpointId);
                if (viaDefault)
                {
                    resolvedId = defaultId;
                    resolvedName = defaultName;
                }
                else
                {
                    try
                    {
                        using var dev = en.GetDevice(cfg.EndpointId);
                        if (dev != null && dev.State == DeviceState.Active)
                        {
                            resolvedId = dev.ID;
                            resolvedName = dev.FriendlyName;
                        }
                    }
                    catch { }
                }

                if (resolvedId == null)
                {
                    // FAIL CLOSED: selection preserved, nothing rendered.
                    newStatus[slot] = "!missing";
                    continue;
                }
                newStatus[slot] = resolvedName ?? resolvedId;

                if (!wanted.TryGetValue(resolvedId, out var entry))
                {
                    entry = (resolvedName ?? "",
                        new List<RumbleAudioSampleProvider.Voice>());
                    wanted[resolvedId] = entry;
                }

                bool stereo = string.Equals(cfg.ChannelMode, "Stereo", StringComparison.OrdinalIgnoreCase);
                float master = Math.Clamp(cfg.MasterGainPercent, 0, 100) / 100f;
                for (int voice = 0; voice < 4; voice++)
                {
                    string source = RumbleAudioConfig.SourceOrder[voice];
                    var authored = cfg.FindVoice(source);
                    bool enabled = authored?.Enabled ?? true;
                    int freq = authored?.FrequencyHz ?? RumbleAudioConfig.DefaultFrequencyHz[voice];
                    int gainPct = authored?.GainPercent ?? 100;
                    if (!enabled || gainPct <= 0 || master <= 0f) continue;
                    freq = Math.Clamp(freq, RumbleAudioConfig.MinFrequencyHz, RumbleAudioConfig.MaxFrequencyHz);
                    // Controller stereo: low + left trigger on the left
                    // channel, high + right trigger on the right. Mono:
                    // every voice on both.
                    bool toLeft = !stereo || voice == 0 || voice == 2;
                    bool toRight = !stereo || voice == 1 || voice == 3;
                    entry.Voices.Add(new RumbleAudioSampleProvider.Voice
                    {
                        Slot = slot,
                        VoiceIndex = voice,
                        FrequencyHz = freq,
                        Gain = Math.Clamp(gainPct, 0, 100) / 100f * master,
                        ToLeft = toLeft,
                        ToRight = toRight,
                    });
                }
            }

            // 3. Diff against live players: retire, retarget, create.
            // WASAPI activation (BuildPlayer) and the fade-out sleep both
            // run OUTSIDE _lock (audit: holding the lock across activation
            // stalled EnsureStarted callers on the UI/startup path). Plan
            // under the lock, build unlocked, commit under a short re-check
            // (the AudioPassthroughService committed pattern).
            var toDispose = new List<EndpointPlayer>();
            var toBuild = new List<(string Id, string Name, RumbleAudioSampleProvider.Voice[] Voices)>();
            lock (_lock)
            {
                for (int i = _players.Count - 1; i >= 0; i--)
                {
                    var p = _players[i];
                    bool keep = wanted.ContainsKey(p.EndpointId)
                        && PlayerAlive(p);
                    if (!keep)
                    {
                        toDispose.Add(p);
                        _players.RemoveAt(i);
                    }
                }

                foreach (var kv in wanted)
                {
                    var existing = _players.Find(p =>
                        string.Equals(p.EndpointId, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        // Carry envelopes across the rebuild so a reconcile
                        // mid-rumble doesn't click.
                        var old = existing.Provider.GetVoices();
                        var next = kv.Value.Voices.ToArray();
                        foreach (var nv in next)
                        {
                            foreach (var ov in old)
                            {
                                if (ov.Slot == nv.Slot && ov.VoiceIndex == nv.VoiceIndex)
                                {
                                    nv.Envelope = ov.Envelope;
                                    // Phase carries too; a changed carrier
                                    // reseeds onto the shared clock inside
                                    // the provider (LastCarrier mismatch).
                                    nv.PhaseAcc = ov.PhaseAcc;
                                    nv.LastCarrier = ov.LastCarrier;
                                    break;
                                }
                            }
                        }
                        existing.Provider.SetVoices(next);
                        existing.FriendlyName = kv.Value.Name;
                        continue;
                    }

                    toBuild.Add((kv.Key, kv.Value.Name,
                        kv.Value.Voices.ToArray()));
                }

                for (int i = 0; i < MaxSlots; i++)
                    Volatile.Write(ref _slotStatus[i], newStatus[i]);
            }

            foreach (var b in toBuild)
            {
                var built = BuildPlayer(b.Id, b.Name, b.Voices);
                if (built == null) continue;
                bool committed = false;
                lock (_lock)
                {
                    // Commit only while the worker is alive and nobody
                    // else claimed the endpoint in the window.
                    if (_reconcileTimer != null && !_players.Exists(p =>
                            string.Equals(p.EndpointId, b.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        _players.Add(built);
                        committed = true;
                    }
                }
                if (!committed) FadeStopDispose(built);
            }

            foreach (var p in toDispose) FadeStopDispose(p);
        }

        private static bool PlayerAlive(EndpointPlayer p)
        {
            try { return p.Player != null && p.Player.PlaybackState == PlaybackState.Playing; }
            catch { return false; }
        }

        private static EndpointPlayer BuildPlayer(string endpointId, string name,
            RumbleAudioSampleProvider.Voice[] voices)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDevice(endpointId);
                if (dev == null || dev.State != DeviceState.Active) return null;
                var provider = new RumbleAudioSampleProvider();
                provider.SetVoices(voices);
                // Shared, event-sync, 30 ms: the AudioPassthroughService
                // recipe. Shared mode auto-converts our float 48k stereo
                // to the endpoint mix format (AutoConvertPcm).
                var player = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
                player.Init(provider);
                player.Play();
                return new EndpointPlayer
                {
                    EndpointId = endpointId,
                    Player = player,
                    Provider = provider,
                    FriendlyName = name ?? "",
                };
            }
            catch
            {
                // Best-effort: a failed build retries on the next pass.
                return null;
            }
        }

        /// <summary>Click-free teardown: fade, wait a couple of buffers,
        /// stop, dispose. Worker / caller thread only; never the poll
        /// thread and never inside _lock.</summary>
        private static void FadeStopDispose(EndpointPlayer p)
        {
            try
            {
                p.Provider?.BeginFadeOut();
                // Two 30 ms buffers cover the ~15 ms envelope decay.
                Thread.Sleep(60);
            }
            catch { }
            try { p.Player?.Stop(); } catch { }
            try { p.Player?.Dispose(); } catch { }
        }
    }
}
