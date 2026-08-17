using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PadForge.Common.Input;

namespace PadForge.Services
{
    /// <summary>
    /// Voice macro recognition (issue #317). One session PER MICROPHONE:
    /// each microphone-bearing pad and each standalone microphone device
    /// gets its own speech engine fed by its own audio, and a recognition
    /// pulses the phrase buttons of the device that heard it. There is no
    /// shared microphone and no voice pseudo-device.
    ///
    /// Sources:
    ///  - A DualSense over Bluetooth on the full profile: the in-process
    ///    Opus tee. AudioPassthroughService hands each decoded block here
    ///    keyed by pad, and no Windows audio device is involved.
    ///  - Any microphone that surfaces as a Windows endpoint, including a
    ///    WIRED DualSense's: a standalone Microphone device row, captured
    ///    directly. Phrases live wherever the microphone surfaces.
    ///
    /// Engine: SAPI with a closed grammar over an app-owned PCM stream
    /// (probe-proven: a synthesized phrase round-trips at 0.99 confidence
    /// unpackaged with no endpoint involved). The WinRT recognizer exposes
    /// no audio-input API and cannot take a direct feed.
    /// </summary>
    internal sealed class VoiceMacroService : IDisposable
    {
        public static VoiceMacroService Active { get; private set; }

        /// <summary>Every recognition attempt on any session, for the manage
        /// dialog's live readout: (source name, text, confidence, fired).
        /// STATIC, deliberately: the dialog can open before the service
        /// starts or across a service restart, and an instance subscription
        /// taken at the wrong moment went silently dead (field report: a
        /// fired phrase with the dialog open produced no readout because
        /// the dialog held a stale instance).</summary>
        public static event Action<string, string, float, bool> PhraseHeard;

        // ── Settings (persisted via SettingsService) ──
        public static volatile bool Enabled;
        public static float MinConfidence = 0.80f;
        /// <summary>0 = always listening, 1 = push-to-talk via the
        /// VoiceListenWhileHeld macro action.</summary>
        public static volatile int ListeningMode;

        // Push-to-talk gate: a decaying heartbeat, never a latch. The
        // continuous macro action beats it every frame while held, so a
        // macro that dies mid-hold closes listening ~100 ms later.
        private static long _listenHeldUntil;
        public static void NoteListenHeld()
            => Interlocked.Exchange(ref _listenHeldUntil, Environment.TickCount64 + 100);
        public static bool ListenGateOpen
            => ListeningMode == 0 || Environment.TickCount64 < Interlocked.Read(ref _listenHeldUntil);

        public static bool IsAvailable
        {
            get
            {
                try { return System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
                catch { return false; }
            }
        }

        private static readonly object _startLock = new();

        /// <summary>Set by the input manager's shutdown BEFORE Shutdown()
        /// runs, so a mic-sweep worker already past its own suppress check
        /// cannot resurrect the service after it was torn down.</summary>
        public static volatile bool SuppressStart;

        public static VoiceMacroService Start()
        {
            lock (_startLock)
            {
                if (Active != null) return Active;
                if (SuppressStart) return null;
                if (!IsAvailable)
                    // No SAPI recognizer (N editions, some locales) is not
                    // fatal: Vosk needs none of it. Sessions simply wait
                    // for the model.
                    Engine.SdlDiagLog.WriteLine("VOICE no installed SAPI recognizer; Vosk carries recognition once its model is ready");
                try
                {
                    var svc = new VoiceMacroService();
                    svc._worker = new Thread(svc.WorkerLoop) { IsBackground = true, Name = "VoiceMacro" };
                    svc._worker.Start();
                    Active = svc;
                    Engine.SdlDiagLog.WriteLine("VOICE service up (enabled=" + Enabled
                        + " phrases=" + VoicePhraseRegistry.Count + ")");
                    return svc;
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("VOICE start failed: " + ex.Message);
                    return null;
                }
            }
        }

        public static void Shutdown()
        {
            lock (_startLock)
            {
                try { Active?.Dispose(); } catch { }
                Active = null;
            }
        }

        // ── Sessions ──

        /// <summary>One microphone, one engine. Key: "pad:{guid}" for a
        /// mic-bearing controller, "ep:{endpointId}" for a standalone
        /// microphone device.</summary>
        private sealed class Session
        {
            public string Key;
            public string DisplayName;
            public Guid PadGuid;            // Guid.Empty for endpoint sessions
            public string EndpointId;       // null for pad tee sessions
            public Guid BridgePadGuid;      // endpoint sessions: the pad sharing
                                            // this endpoint's USB container (a
                                            // wired DualSense), stamped so the
                                            // pad's own Voice Phrase bindings
                                            // fire too. Guid.Empty otherwise.
            public bool OwnsBtMicLane;      // pad session that opened the mic itself
            public long SelfTestUntil;      // pulses suppressed while injecting
            public System.Speech.Recognition.SpeechRecognitionEngine Engine;   // SAPI fallback only
            public VoicePcmStream Pcm;                                          // SAPI fallback only
            public IVoicePcmSink Sink;              // where producers write, either engine
            public long MuteProducersUntilTicks;    // self-test owns the pipe below this
            public WasapiCapture Capture;   // null when the pad tee feeds it
            public int Gen;
        }

        private readonly object _sessionsLock = new();
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private int _grammarGen;
        private int _lastGenSeen = -1;
        // Worker-thread only: unbuildable sources escalate their retry
        // delay instead of paying a full engine construction every 1.5 s.
        private readonly Dictionary<string, (long Until, int Fails)> _buildBackoff = new();
        private long _nextBridgeRetry;
        private bool _lastVoskReady;
        private volatile bool _disposed;
        private Thread _worker;

        private VoiceMacroService()
        {
            VoicePhraseRegistry.RegistryChanged += OnRegistryChanged;
        }

        private void OnRegistryChanged(object s, EventArgs e)
            => Interlocked.Increment(ref _grammarGen);

        /// <summary>Reconciles sessions against the microphones that should
        /// be listening. Grammar or registry changes bump the generation and
        /// every session rebuilds through a full stop-swap-start, the only
        /// safe cycle for a compiled grammar and a bound input.</summary>
        private void WorkerLoop()
        {
            while (!_disposed)
            {
                try
                {
                    if (Enabled && VoicePhraseRegistry.Count > 0)
                        VoskModelStore.EnsureStarted();
                    bool voskReady = VoskModelStore.IsReady;
                    if (voskReady != _lastVoskReady)
                    {
                        _lastVoskReady = voskReady;
                        Interlocked.Increment(ref _grammarGen);
                        Engine.SdlDiagLog.WriteLine("VOICE engine switch: " + (voskReady ? "vosk" : "sapi fallback"));
                    }
                    int gen = _grammarGen;
                    if (gen != _lastGenSeen) { _lastGenSeen = gen; _buildBackoff.Clear(); }
                    var desired = Enabled && VoicePhraseRegistry.Count > 0
                        ? ComputeDesiredSources()
                        : new List<(string Key, string Name, Guid Pad, string Endpoint)>();

                    // Tear down sessions whose source vanished or whose
                    // grammar is stale. Mutation under the lock, the work
                    // outside it: Build can take real time (engine creation,
                    // WASAPI open), and the tee, the listen gate, and
                    // recognition dispatch all contend this lock.
                    List<Session> retire = new();
                    lock (_sessionsLock)
                    {
                        foreach (var key in _sessions.Keys.ToList())
                        {
                            var ses = _sessions[key];
                            bool still = desired.Any(d => d.Key == key);
                            if (!still || ses.Gen != gen)
                            {
                                retire.Add(ses);
                                _sessions.Remove(key);
                            }
                        }
                    }
                    foreach (var ses in retire)
                        TearDown(ses, "retired");
                    foreach (var d in desired)
                    {
                        bool have;
                        lock (_sessionsLock) have = _sessions.ContainsKey(d.Key);
                        if (have) { _buildBackoff.Remove(d.Key); continue; }
                        if (_buildBackoff.TryGetValue(d.Key, out var bo)
                            && Environment.TickCount64 < bo.Until) continue;
                        var ses = Build(d.Key, d.Name, d.Pad, d.Endpoint, gen);
                        if (ses == null)
                        {
                            int fails = bo.Fails + 1;
                            long delay = Math.Min(30_000L, 1500L << Math.Min(fails, 4));
                            _buildBackoff[d.Key] = (Environment.TickCount64 + delay, fails);
                            continue;
                        }
                        _buildBackoff.Remove(d.Key);
                        lock (_sessionsLock) _sessions[d.Key] = ses;
                    }
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("VOICE reconcile error: " + ex.Message);
                    for (int i = 0; i < 20 && !_disposed; i++) Thread.Sleep(100);
                }
                // A wired pad that enumerated after its endpoint's session
                // built gets bridged late rather than never.
                try
                {
                    if (Environment.TickCount64 >= _nextBridgeRetry)
                    {
                        _nextBridgeRetry = Environment.TickCount64 + 10_000;
                        List<Session> unbridged = null;
                        lock (_sessionsLock)
                            foreach (var s2 in _sessions.Values)
                                if (s2.EndpointId != null && s2.BridgePadGuid == Guid.Empty)
                                    (unbridged ??= new List<Session>()).Add(s2);
                        if (unbridged != null)
                            foreach (var s2 in unbridged)
                            {
                                var g = ResolveEndpointPad(s2.EndpointId);
                                if (g != Guid.Empty)
                                {
                                    s2.BridgePadGuid = g;
                                    Engine.SdlDiagLog.WriteLine($"VOICE [{s2.DisplayName}] endpoint bridged to pad {g}");
                                }
                            }
                    }
                }
                catch { }
                Thread.Sleep(1500);
            }
            lock (_sessionsLock)
            {
                foreach (var ses in _sessions.Values) TearDown(ses, "shutdown");
                _sessions.Clear();
            }
        }

        /// <summary>Microphones that should be listening. The rule (owner
        /// directive): phrases live WHEREVER THE MICROPHONE SURFACES. A
        /// Bluetooth DualSense exposes no system microphone, so the pad
        /// itself carries its phrases and a pad session listens through the
        /// in-process tee. A wired DualSense exposes a real capture
        /// endpoint, which appears as a standalone Microphone device like
        /// any other mic, so the pad gets no session of its own.</summary>
        private static List<(string Key, string Name, Guid Pad, string Endpoint)> ComputeDesiredSources()
        {
            var list = new List<(string, string, Guid, string)>();
            foreach (var (endpointId, name) in MicrophoneInputDevice.OpenEndpoints())
                list.Add(("ep:" + endpointId, name, Guid.Empty, endpointId));
            try
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                    {
                        if (ud == null || !ud.IsOnline) continue;
                        if (IsPadWithEmbeddedMic(ud))
                            list.Add(("pad:" + ud.InstanceGuid, ud.ResolvedName, ud.InstanceGuid, ud.DevicePath));
                    }
                }
            }
            catch (Exception ex)
            {
                Engine.SdlDiagLog.WriteLine("VOICE pad scan failed: " + ex.Message);
            }
            return list;
        }

        /// <summary>True when this pad's microphone is EMBEDDED and does
        /// NOT currently surface as a Windows endpoint: a DualSense or
        /// DualSense Edge (PID gates mirror SDL's controller list) over
        /// Bluetooth (the canonical transport check; BT HID paths carry the
        /// {00001124} service GUID, not "bthenum") whose container has no
        /// active capture endpoint. Wired, the pad's own endpoint carries
        /// the phrases; on the full persona profile the persona's headset
        /// mic endpoint does. Phrases live wherever the microphone
        /// surfaces, and only when it surfaces NOWHERE does the pad itself
        /// carry them.</summary>
        internal static bool IsPadWithEmbeddedMic(PadForge.Engine.Data.UserDevice ud)
            => ud.VendorId == 0x054C
               && (ud.ProdId == 0x0CE6 || ud.ProdId == 0x0DF2)
               && PadForge.Common.DeviceTransport.IsBluetooth(ud.DevicePath, ud.VendorId, ud.ProdId)
               // On the full profile the persona decodes this pad's mic
               // into a real capture endpoint (a DIFFERENT container, the
               // HM virtual device's, so a container compare cannot see
               // it). That endpoint row owns the phrases; the pad carries
               // them only when its mic surfaces nowhere.
               && !AudioPassthroughService.IsBtMicLaneActive(ud.InstanceGuid)
               && !PadMicSurfacesAsEndpoint(ud);

        /// <summary>Does any active capture endpoint share this pad's USB
        /// container (the persona's headset mic, or the pad's own wired
        /// audio)? If so, that endpoint row owns the phrases.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (long Until, bool Value)>
            _surfaceCache = new();

        private static bool PadMicSurfacesAsEndpoint(PadForge.Engine.Data.UserDevice ud)
        {
            // A COM endpoint enumeration per reconcile pass showed up as
            // polling dips, so the answer is cached. Five seconds is fast
            // enough for a plug event and invisible to the poll thread.
            long now = Environment.TickCount64;
            if (_surfaceCache.TryGetValue(ud.InstanceGuid, out var hit) && now < hit.Until)
                return hit.Value;
            bool value = false;
            try
            {
                Guid container = AudioPassthroughService.DevicePathContainerId(ud.DevicePath);
                if (container != Guid.Empty)
                {
                    using var en = new MMDeviceEnumerator();
                    foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        if (AudioPassthroughService.EndpointContainerId(dev) == container)
                        { value = true; break; }
                }
            }
            catch { }
            _surfaceCache[ud.InstanceGuid] = (now + 5000, value);
            return value;
        }

        /// <summary>The assigned phrase-bearing pad sharing a capture
        /// endpoint's USB container: a wired DualSense whose microphone
        /// surfaces as this endpoint. Guid.Empty when none does (system
        /// mics, and the persona's headset endpoint, whose container is
        /// the HM virtual device's). Runs on the reconciler worker at
        /// session build, never on the poll thread.</summary>
        private static Guid ResolveEndpointPad(string endpointId)
        {
            try
            {
                Guid container = Guid.Empty;
                using (var en = new MMDeviceEnumerator())
                    foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        if (string.Equals(d.ID, endpointId, StringComparison.Ordinal))
                        { container = AudioPassthroughService.EndpointContainerId(d); break; }
                if (container == Guid.Empty) return Guid.Empty;
                lock (SettingsManager.UserDevices.SyncRoot)
                    foreach (var ud in SettingsManager.UserDevices.Items)
                        if (ud != null && ud.HasVoicePhrases
                            && AudioPassthroughService.DevicePathContainerId(ud.DevicePath) == container)
                            return ud.InstanceGuid;
            }
            catch { }
            return Guid.Empty;
        }

        private Session Build(string key, string name, Guid padGuid, string endpointOrPath, int gen)
        {
            // Tracked so the blanket catch can undo a partial build: an
            // exception after resources attach must not leak an engine, a
            // capture, or an opened BT mic lane no TearDown will ever see.
            Session partial = null;
            System.Speech.Recognition.SpeechRecognitionEngine partialEngine = null;
            try
            {
                var phrases = VoicePhraseRegistry.Phrases;
                if (phrases.Count == 0) return null;

                bool isEndpointSrc = padGuid == Guid.Empty;
                var voskModel = VoskModelStore.Model;
                if (voskModel != null)
                {
                    var vses = new Session
                    {
                        Key = key, DisplayName = name, PadGuid = padGuid,
                        EndpointId = isEndpointSrc ? endpointOrPath : null,
                        Gen = gen,
                    };
                    partial = vses;
                    vses.Sink = new VoskSession(voskModel,
                        phrases.Select(x => x.Phrase).ToArray(),
                        (text, conf) => OnRecognized(vses, text, conf),
                        text =>
                        {
                            Engine.SdlDiagLog.WriteLine($"VOICE [{vses.DisplayName}] garbage \"{text}\" ([unk])");
                            PhraseHeard?.Invoke(vses.DisplayName, text, 0f, false);
                        });
                    if (isEndpointSrc)
                    {
                        vses.Capture = OpenEndpointCapture(endpointOrPath, vses);
                        if (vses.Capture == null)
                        {
                            Engine.SdlDiagLog.WriteLine("VOICE [" + name + "] endpoint unavailable");
                            vses.Sink.Dispose();
                            return null;
                        }
                        vses.BridgePadGuid = ResolveEndpointPad(endpointOrPath);
                    }
                    else if (!AudioPassthroughService.IsBtMicLaneActive(padGuid))
                    {
                        if (!AudioPassthroughService.StartVoiceBtMic(padGuid, endpointOrPath))
                        {
                            vses.Sink.Dispose();
                            return null;
                        }
                        vses.OwnsBtMicLane = true;
                    }
                    Engine.SdlDiagLog.WriteLine($"VOICE [{name}] listening (vosk, {phrases.Count} phrases, "
                        + (isEndpointSrc ? "endpoint" : vses.OwnsBtMicLane ? "pad BT direct, own session" : "pad BT direct, persona tee") + ")");
                    MaybeRunSelfTest(vses);
                    return vses;
                }

                var installed = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
                if (installed.Count == 0) return null;
                var info = installed[0];

                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] build: engine");
                var engine = new System.Speech.Recognition.SpeechRecognitionEngine(info);
                partialEngine = engine;
                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] build: grammar");
                var gb = new System.Speech.Recognition.GrammarBuilder(
                    new System.Speech.Recognition.Choices(phrases.Select(p => p.Phrase).ToArray()))
                { Culture = info.Culture };
                var phraseGrammar = new System.Speech.Recognition.Grammar(gb) { Name = "phrases" };
                engine.LoadGrammar(phraseGrammar);
                // The dictation sink. With only the closed grammar loaded,
                // the engine has nowhere to put non-matching speech, so
                // everything funnels into the nearest phrase and confidence
                // stops discriminating (field report: "meow" outscoring a
                // real "hello" FOR "hello"). With free dictation loaded
                // beside it, garbage goes to dictation, and a phrase result
                // only wins when it genuinely beats free speech.
                try
                {
                    // Priority above the phrases: a near-tie between "sounds
                    // like the phrase" and "sounds like speech" resolves to
                    // garbage, so only a clear phrase match survives. Field
                    // case: "meow" splitting the difference with "hello".
                    engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar()
                    { Name = "__sink", Priority = 127 });
                }
                catch { /* a recognizer without dictation keeps the closed grammar alone */ }

                bool isEndpoint = padGuid == Guid.Empty;
                var ses = new Session
                {
                    Key = key, DisplayName = name, PadGuid = padGuid,
                    EndpointId = isEndpoint ? endpointOrPath : null,
                    Engine = engine, Gen = gen,
                    Pcm = new VoicePcmStream(),
                };
                ses.Sink = ses.Pcm;
                partial = ses;
                partialEngine = null;   // ses.Engine owns it from here
                engine.SpeechRecognized += (s, e) =>
                {
                    // Dictation-sink wins are garbage by definition: the
                    // utterance resembled free speech more than any phrase.
                    if (e.Result?.Grammar?.Name == "__sink")
                    {
                        Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] garbage \"{e.Result?.Text}\" (dictation sink)");
                        PhraseHeard?.Invoke(ses.DisplayName, e.Result?.Text ?? string.Empty, 0f, false);
                        return;
                    }
                    // The engine's runner-up hypotheses, logged so a
                    // misrecognition shows its margins instead of just its
                    // winner: how far garbage sat behind the phrase is the
                    // number that places the confidence floor.
                    try
                    {
                        var alts = e.Result?.Alternates;
                        if (alts != null && alts.Count > 1)
                        {
                            var sb = new System.Text.StringBuilder();
                            for (int i = 1; i < alts.Count && i < 4; i++)
                                sb.Append(i > 1 ? " | " : "").Append('"').Append(alts[i].Text).Append("\" ").Append(alts[i].Confidence.ToString("F2"));
                            Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] alternates: {sb}");
                        }
                    }
                    catch { }
                    OnRecognized(ses, e.Result?.Text, e.Result?.Confidence ?? 0f);
                };
                engine.RecognizeCompleted += (s, e) =>
                    Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] recognize COMPLETED"
                        + (e.Cancelled ? " (cancelled)" : "") + (e.Error != null ? " error=" + e.Error.Message : ""));
                engine.SpeechRecognitionRejected += (s, e) =>
                {
                    Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] rejected (nearest \"{e.Result?.Text}\" conf={e.Result?.Confidence ?? 0f:F2})");
                    PhraseHeard?.Invoke(ses.DisplayName, e.Result?.Text ?? string.Empty, e.Result?.Confidence ?? 0f, false);
                };

                // The engine's own audio-state machine is the decisive
                // diagnostic: Stopped means the stream never fed it,
                // permanent Silence means audio arrives but carries nothing,
                // Speech means the chain is alive end to end.
                engine.AudioStateChanged += (s2, e2) =>
                    Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] audio state -> {e2.AudioState}");

                var fmt = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                    16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                    System.Speech.AudioFormat.AudioChannel.Mono);
                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] build: bind stream");
                engine.SetInputToAudioStream(ses.Pcm, fmt);

                if (isEndpoint)
                {
                    ses.Capture = OpenEndpointCapture(endpointOrPath, ses);
                    if (ses.Capture == null)
                    {
                        Engine.SdlDiagLog.WriteLine("VOICE [" + name + "] endpoint unavailable");
                        engine.Dispose(); ses.Pcm.Dispose();
                        return null;
                    }
                    ses.BridgePadGuid = ResolveEndpointPad(endpointOrPath);
                }
                else
                {
                    // A pad session feeds from the in-process tee. On the
                    // full profile the persona's mic decode supplies it. On
                    // every other profile PadForge opens the pad's mic
                    // session ITSELF: the voice-only lane sends the same
                    // open/close reports and runs the same 0x31 HasMic
                    // decode, so the pad's embedded mic works on all
                    // profiles.
                    if (!AudioPassthroughService.IsBtMicLaneActive(padGuid))
                    {
                        if (!AudioPassthroughService.StartVoiceBtMic(padGuid, endpointOrPath))
                        {
                            engine.Dispose(); ses.Pcm.Dispose();
                            return null;
                        }
                        ses.OwnsBtMicLane = true;
                    }
                }

                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] build: start recognize");
                engine.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] listening ({info.Culture.Name}, {phrases.Count} phrases, "
                    + (isEndpoint ? "endpoint" : ses.OwnsBtMicLane ? "pad BT direct, own session" : "pad BT direct, persona tee") + ")");
                MaybeRunSelfTest(ses);
                return ses;
            }
            catch (Exception ex)
            {
                Engine.SdlDiagLog.WriteLine("VOICE [" + name + "] session build failed: " + ex.Message);
                if (partial != null)
                {
                    if (partial.OwnsBtMicLane)
                        try { AudioPassthroughService.StopVoiceBtMic(partial.PadGuid); } catch { }
                    try { partial.Capture?.StopRecording(); } catch { }
                    try { partial.Capture?.Dispose(); } catch { }
                    try { partial.Sink?.Dispose(); } catch { }
                    try { partial.Engine?.Dispose(); } catch { }
                }
                else if (partialEngine != null)
                {
                    try { partialEngine.Dispose(); } catch { }
                }
                return null;
            }
        }

        private void TearDown(Session ses, string reason)
        {
            // Order is life or death here, learned live: the engine's audio
            // thread parks inside the pipe's blocking Read, so disposing the
            // engine FIRST joins a thread that can never return and the
            // whole reconciler hangs (seen at 20:06:57: one CLOSE line,
            // then silence, no session ever built again). Producers stop,
            // the pipe closes (its reader gets end-of-stream), and only
            // then does the engine wind down.
            if (ses.OwnsBtMicLane)
                try { AudioPassthroughService.StopVoiceBtMic(ses.PadGuid); } catch { }
            try { ses.Capture?.StopRecording(); } catch { }
            try { ses.Capture?.Dispose(); } catch { }
            try { ses.Sink?.Dispose(); } catch { }
            try { ses.Engine?.RecognizeAsyncCancel(); } catch { }
            try { ses.Engine?.Dispose(); } catch { }
            Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] session down ({reason})");
        }

        // One in-process proof per launch, diagnostics builds only: the
        // first session gets the first registered phrase synthesized to
        // 16 kHz PCM and injected into its own stream. A "(self-test)"
        // heard line then proves engine, grammar, and stream live in the
        // shipped binary, so a silent microphone can never be mistaken for
        // a broken recognition chain. Pulses are suppressed for the
        // window, so the injection can never fire a binding.
        private static int _selfTestDone;
        private void MaybeRunSelfTest(Session ses)
        {
            if (!Engine.SdlDiagLog.IsMirroring) return;
            if (Interlocked.Exchange(ref _selfTestDone, 1) != 0) return;
            var phrase = VoicePhraseRegistry.Phrases.FirstOrDefault()?.Phrase;
            if (string.IsNullOrEmpty(phrase)) { _selfTestDone = 0; return; }
            new Thread(() =>
            {
                try
                {
                    Thread.Sleep(1500);
                    // The startup reconcile shuffles sessions (a pad session
                    // can retire when the persona lane arrives), so pick the
                    // target NOW, from whatever is actually alive.
                    lock (_sessionsLock)
                    {
                        ses = _sessions.Values.FirstOrDefault(x => x.Capture != null)
                              ?? _sessions.Values.FirstOrDefault();
                    }
                    if (ses == null)
                    {
                        Engine.SdlDiagLog.WriteLine("VOICE self-test: no live session yet, re-arming");
                        _selfTestDone = 0;
                        return;
                    }
                    System.Threading.Volatile.Write(ref ses.SelfTestUntil, Environment.TickCount64 + 10000);
                    System.Threading.Volatile.Write(ref ses.MuteProducersUntilTicks, Environment.TickCount64 + 6000);
                    var wav = new MemoryStream();
                    using (var tts = new System.Speech.Synthesis.SpeechSynthesizer())
                    {
                        tts.SetOutputToAudioStream(wav,
                            new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                                16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                                System.Speech.AudioFormat.AudioChannel.Mono));
                        tts.Speak(phrase);
                    }
                    var bytes = wav.ToArray();
                    // Synthesis took time; the reconciler may have retired
                    // the target meanwhile (a 25 ms race did exactly that
                    // once). Only a session still in the table gets the
                    // injection; otherwise the shot re-arms.
                    lock (_sessionsLock)
                    {
                        if (!_sessions.Values.Contains(ses))
                        {
                            Engine.SdlDiagLog.WriteLine("VOICE self-test target retired mid-synthesis, re-arming");
                            _selfTestDone = 0;
                            return;
                        }
                    }
                    Engine.SdlDiagLog.WriteLine($"VOICE self-test injecting \"{phrase}\" ({bytes.Length} bytes) into [{ses.DisplayName}]");
                    // Paced like a live mic (32 bytes/ms at 16 kHz), plus a
                    // half-second silence tail so end-of-utterance detection
                    // has room to close.
                    var sink = ses.Sink;
                    for (int off = 0; off < bytes.Length; off += 640)
                    {
                        if (_disposed) return;
                        sink.Write(bytes.AsSpan(off, Math.Min(640, bytes.Length - off)));
                        Thread.Sleep(18);
                    }
                    var silence = new byte[640];
                    for (int t = 0; t < 30; t++) { sink.Write(silence); Thread.Sleep(18); }
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("VOICE self-test failed to inject: " + ex.Message);
                }
            })
            { IsBackground = true, Name = "VoiceSelfTest" }.Start();
        }

        private void OnRecognized(Session ses, string text, float conf)
        {
            bool selfTest = Environment.TickCount64 < System.Threading.Volatile.Read(ref ses.SelfTestUntil);
            bool gate = ListenGateOpen;
            bool fires = !selfTest && gate && conf >= MinConfidence;
            Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] heard \"{text}\" conf={conf:F2} gate={(gate ? 1 : 0)} fires={(fires ? 1 : 0)}"
                + (selfTest ? " (self-test)" : ""));
            PhraseHeard?.Invoke(ses.DisplayName, text ?? string.Empty, conf, fires);
            if (!fires) return;
            int button = VoicePhraseRegistry.ButtonForPhrase(text);
            if (ses.PadGuid != Guid.Empty) VoicePulse.Stamp(ses.PadGuid, button);
            else
            {
                MicrophoneInputDevice.StampPulse(ses.EndpointId, button);
                // A recognition through an endpoint the pad itself exposes
                // (a wired DualSense's mic, matched by USB container at
                // build) stamps that pad, so its own Voice Phrase bindings
                // fire. The persona's headset endpoint lives in a different
                // container; there the sole-BT-pad bridge answers instead.
                if (ses.BridgePadGuid != Guid.Empty)
                    VoicePulse.Stamp(ses.BridgePadGuid, button);
                else if (AudioPassthroughService.TryGetSoleBtMicPad(out var padGuid))
                    VoicePulse.Stamp(padGuid, button);
            }
        }

        // ── The Bluetooth tee entry point. AudioPassthroughService calls
        // this with each decoded 48 kHz block, keyed by pad, whenever a
        // pad session wants it. 48 to 16 kHz is an exact 3:1 average;
        // speech energy sits far below the fold.
        // The tee runs per decoded 10 ms block on the BT reader thread:
        // the session key is cached per pad so the hot path allocates
        // nothing.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _padKeys = new();
        private static string PadKey(Guid g) => _padKeys.GetOrAdd(g, k => "pad:" + k);

        public static bool PadMicWanted(Guid padGuid)
        {
            var svc = Active;
            if (svc == null || !Enabled) return false;
            if (ListeningMode != 0 && !ListenGateOpen) return false;
            lock (svc._sessionsLock)
                return svc._sessions.TryGetValue(PadKey(padGuid), out var ses) && ses.Capture == null;
        }

        public static void SubmitPadMic48k(Guid padGuid, ReadOnlySpan<short> interleaved, int channels)
        {
            var svc = Active;
            if (svc == null || channels <= 0) return;
            Session target = null;
            lock (svc._sessionsLock)
                if (svc._sessions.TryGetValue(PadKey(padGuid), out var ses) && ses.Capture == null)
                    target = ses;
            if (target == null) return;
            // The self-test owns the pipe while it injects, same gate the
            // WASAPI producer honors: live tee audio would shred both.
            if (Environment.TickCount64 < System.Threading.Volatile.Read(ref target.MuteProducersUntilTicks)) return;
            IVoicePcmSink sink = target.Sink;
            if (sink == null) return;

            int frames = interleaved.Length / channels;
            int outFrames = frames / 3;
            if (outFrames <= 0) return;
            Span<byte> outBuf = outFrames * 2 <= 4096 ? stackalloc byte[outFrames * 2] : new byte[outFrames * 2];
            for (int o = 0; o < outFrames; o++)
            {
                int acc = 0;
                for (int k = 0; k < 3; k++)
                {
                    int f = o * 3 + k;
                    int sum = 0;
                    for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
                    acc += sum / channels;
                }
                short q = (short)(acc / 3);
                outBuf[o * 2] = (byte)(q & 0xFF);
                outBuf[o * 2 + 1] = (byte)((q >> 8) & 0xFF);
            }
            sink.Write(outBuf);
        }

        // ── WASAPI capture into a session's stream: any endpoint, any
        // format, resampled to 16 kHz mono. Format handling mirrors the
        // persona mic capture (float or int16, downmix by average).
        private static WasapiCapture OpenEndpointCapture(string endpointId, Session ses)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                MMDevice dev = null;
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    if (string.Equals(d.ID, endpointId, StringComparison.Ordinal)) { dev = d; break; }
                return dev == null ? null : StartCapture(dev, ses);
            }
            catch { return null; }
        }

        private static WasapiCapture StartCapture(MMDevice dev, Session ses)
        {
            var sink = ses.Sink;
            var cap = new WasapiCapture(dev);
            int inRate = cap.WaveFormat.SampleRate;
            int inCh = cap.WaveFormat.Channels;
            bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                || (cap.WaveFormat is WaveFormatExtensible wfx
                    && wfx.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            int inStride = inCh * (isFloat ? 4 : 2);
            double pos = 0, step = inRate / 16000.0;
            long capCalls = 0, capBytesOut = 0, capNextLog = 0;
            int capPeak = 0;
            cap.DataAvailable += (_, a) =>
            {
                int frames = a.BytesRecorded / inStride;
                if (frames <= 0) return;
                // Liveness proof: one line on the first callback, then a
                // 5 s heartbeat with the format and throughput, so a dead
                // or silent capture names itself in the log.
                capCalls++;
                long nowT = Environment.TickCount64;
                if (capNextLog == 0)
                {
                    capNextLog = nowT + 5000;
                    Engine.SdlDiagLog.WriteLine("VOICE capture alive: " + dev.FriendlyName
                        + " fmt=" + cap.WaveFormat.SampleRate + "Hz/" + inCh + "ch/"
                        + (isFloat ? "f32" : "i16"));
                }
                else if (nowT >= capNextLog)
                {
                    capNextLog = nowT + 5000;
                    Engine.SdlDiagLog.WriteLine("VOICE capture stats: " + dev.FriendlyName
                        + " callbacks=" + capCalls + " bytesTo16k=" + capBytesOut
                        + " peak16k=" + capPeak + "/32767");
                    capPeak = 0;
                }
                if (ListeningMode != 0 && !ListenGateOpen) { pos = 0; return; }
                // The self-test owns the pipe while it injects: mixing live
                // room audio into the synthesized phrase shreds both.
                if (Environment.TickCount64 < System.Threading.Volatile.Read(ref ses.MuteProducersUntilTicks)) return;
                float Mono(int f)
                {
                    float sum = 0f;
                    for (int c = 0; c < inCh; c++)
                        sum += isFloat
                            ? BitConverter.ToSingle(a.Buffer, f * inStride + c * 4)
                            : BitConverter.ToInt16(a.Buffer, f * inStride + c * 2) / 32768f;
                    return sum / inCh;
                }
                Span<byte> outBuf = stackalloc byte[(int)(frames / step) * 2 + 8];
                int n = 0;
                while (pos < frames - 1)
                {
                    int i0 = (int)pos;
                    float frac = (float)(pos - i0);
                    float s0 = Mono(i0);
                    float v = s0 + (Mono(i0 + 1) - s0) * frac;
                    short q = (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);
                    int aq = q < 0 ? -q : q;
                    if (aq > capPeak) capPeak = aq;
                    outBuf[n++] = (byte)(q & 0xFF);
                    outBuf[n++] = (byte)((q >> 8) & 0xFF);
                    pos += step;
                }
                pos -= frames;
                if (pos < 0) pos = 0;
                if (n > 0) { capBytesOut += n; sink.Write(outBuf[..n]); }
            };
            try { cap.StartRecording(); }
            catch { try { cap.Dispose(); } catch { } throw; }
            return cap;
        }

        public void Dispose()
        {
            _disposed = true;
            VoicePhraseRegistry.RegistryChanged -= OnRegistryChanged;
            try { _worker?.Join(2000); } catch { }
        }

        /// <summary>Blocking, bounded PCM pipe between a producer and SAPI's
        /// reader thread. Read blocks on empty so the engine idles on
        /// silence; Dispose releases the reader with end-of-stream.</summary>
        private sealed class VoicePcmStream : Stream, IVoicePcmSink
        {
            private readonly object _sync = new();
            private readonly byte[] _ring = new byte[64 * 1024];
            private int _head, _count;
            private long _readPos;
            private bool _closed;

            public override void Write(ReadOnlySpan<byte> data)
            {
                lock (_sync)
                {
                    if (_closed) return;
                    foreach (byte b in data)
                    {
                        if (_count == _ring.Length)
                        {
                            // Overflow drops the OLDEST audio: recognition
                            // wants fresh speech, not a backlog.
                            _head = (_head + 1) % _ring.Length;
                            _count--;
                        }
                        _ring[(_head + _count) % _ring.Length] = b;
                        _count++;
                    }
                    Monitor.PulseAll(_sync);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // SAPI treats a PARTIAL read as end-of-stream and completes
                // the whole recognition session on the spot. Proven in the
                // shipped binary by the self-test: 40 KB injected, zero
                // events, because the very first partial return ended the
                // engine silently. Block until the full request is
                // available; only a closed pipe may return short.
                lock (_sync)
                {
                    count = Math.Min(count, _ring.Length);
                    while (_count < count && !_closed) Monitor.Wait(_sync, 250);
                    int n = Math.Min(count, _count);
                    if (n == 0) return 0;
                    for (int i = 0; i < n; i++)
                    {
                        buffer[offset + i] = _ring[_head];
                        _head = (_head + 1) % _ring.Length;
                    }
                    _count -= n;
                    _readPos += n;
                    return n;
                }
            }

            protected override void Dispose(bool disposing)
            {
                lock (_sync) { _closed = true; Monitor.PulseAll(_sync); }
                base.Dispose(disposing);
            }

            // SAPI probes the stream surface when the session starts, and
            // ANY throw here kills the engine on the spot: the live log
            // showed "recognize COMPLETED error=Specified method is not
            // supported" within a millisecond of every start. So nothing
            // throws. Seek and Position-set acknowledge without moving (the
            // pipe is live audio, there is nowhere to seek), SetLength and
            // the writer-side Write are ignored.
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => long.MaxValue;
            public override long Position { get => _readPos; set { } }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => _readPos;
            public override void SetLength(long v) { }
            public override void Write(byte[] b, int o, int c) { }
        }
    }
}
