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
        /// dialog's live readout: (source name, text, confidence, fired).</summary>
        public event Action<string, string, float, bool> PhraseHeard;

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

        public static VoiceMacroService Start()
        {
            lock (_startLock)
            {
                if (Active != null) return Active;
                if (!IsAvailable)
                {
                    Engine.SdlDiagLog.WriteLine("VOICE no installed speech recognizer; voice macros unavailable");
                    return null;
                }
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
            public System.Speech.Recognition.SpeechRecognitionEngine Engine;
            public VoicePcmStream Pcm;
            public WasapiCapture Capture;   // null when the pad tee feeds it
            public int Gen;
        }

        private readonly object _sessionsLock = new();
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private int _grammarGen;
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
                    int gen = _grammarGen;
                    var desired = Enabled && VoicePhraseRegistry.Count > 0
                        ? ComputeDesiredSources()
                        : new List<(string Key, string Name, Guid Pad, string Endpoint)>();

                    lock (_sessionsLock)
                    {
                        // Tear down sessions whose source vanished or whose
                        // grammar is stale.
                        foreach (var key in _sessions.Keys.ToList())
                        {
                            var ses = _sessions[key];
                            bool still = desired.Any(d => d.Key == key);
                            if (!still || ses.Gen != gen)
                            {
                                TearDown(ses, still ? "grammar rebuild" : "source gone");
                                _sessions.Remove(key);
                            }
                        }
                        // Build sessions for new sources.
                        foreach (var d in desired)
                        {
                            if (_sessions.ContainsKey(d.Key)) continue;
                            var ses = Build(d.Key, d.Name, d.Pad, d.Endpoint, gen);
                            if (ses != null) _sessions[d.Key] = ses;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("VOICE reconcile error: " + ex.Message);
                    for (int i = 0; i < 20 && !_disposed; i++) Thread.Sleep(100);
                }
                Thread.Sleep(500);
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
            try
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    foreach (var ud in SettingsManager.UserDevices.Items)
                    {
                        if (ud == null || !ud.IsOnline) continue;
                        if (IsPadWithEmbeddedMic(ud))
                            list.Add(("pad:" + ud.InstanceGuid, ud.ResolvedName, ud.InstanceGuid, null));
                    }
                }
            }
            catch { }
            foreach (var (endpointId, _) in MicrophoneInputDevice.OpenEndpoints())
                list.Add(("ep:" + endpointId, "mic", Guid.Empty, endpointId));
            return list;
        }

        /// <summary>True when this pad's microphone is EMBEDDED, reachable
        /// only through PadForge: a DualSense or DualSense Edge (PID gates
        /// mirror SDL's controller list) on a Bluetooth HID path. The same
        /// pad wired exposes a real endpoint instead and is deliberately
        /// excluded here.</summary>
        internal static bool IsPadWithEmbeddedMic(PadForge.Engine.Data.UserDevice ud)
            => ud.VendorId == 0x054C
               && (ud.ProdId == 0x0CE6 || ud.ProdId == 0x0DF2)
               && ud.DevicePath != null
               && ud.DevicePath.IndexOf("bthenum", StringComparison.OrdinalIgnoreCase) >= 0;

        private Session Build(string key, string name, Guid padGuid, string endpointId, int gen)
        {
            try
            {
                var installed = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
                if (installed.Count == 0) return null;
                var info = installed[0];
                var phrases = VoicePhraseRegistry.Phrases;
                if (phrases.Count == 0) return null;

                var engine = new System.Speech.Recognition.SpeechRecognitionEngine(info);
                var gb = new System.Speech.Recognition.GrammarBuilder(
                    new System.Speech.Recognition.Choices(phrases.Select(p => p.Phrase).ToArray()))
                { Culture = info.Culture };
                engine.LoadGrammar(new System.Speech.Recognition.Grammar(gb));

                var ses = new Session
                {
                    Key = key, DisplayName = name, PadGuid = padGuid,
                    EndpointId = endpointId, Engine = engine, Gen = gen,
                    Pcm = new VoicePcmStream(),
                };
                engine.SpeechRecognized += (s, e) => OnRecognized(ses, e.Result?.Text, e.Result?.Confidence ?? 0f);
                engine.SpeechRecognitionRejected += (s, e) =>
                {
                    Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] rejected (nearest \"{e.Result?.Text}\" conf={e.Result?.Confidence ?? 0f:F2})");
                    PhraseHeard?.Invoke(ses.DisplayName, e.Result?.Text ?? string.Empty, e.Result?.Confidence ?? 0f, false);
                };

                var fmt = new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                    16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                    System.Speech.AudioFormat.AudioChannel.Mono);
                engine.SetInputToAudioStream(ses.Pcm, fmt);

                if (endpointId != null)
                {
                    ses.Capture = OpenEndpointCapture(endpointId, ses.Pcm);
                    if (ses.Capture == null)
                    {
                        Engine.SdlDiagLog.WriteLine("VOICE [" + name + "] endpoint unavailable");
                        engine.Dispose(); ses.Pcm.Dispose();
                        return null;
                    }
                }
                else
                {
                    // A pad session's only lane is the in-process tee, which
                    // runs while the pad's Bluetooth mic decode is up (the
                    // full profile). On non-full profiles nothing opens the
                    // mic session yet, so the session waits and the
                    // reconciler retries as the lane appears.
                    if (!AudioPassthroughService.IsBtMicLaneActive(padGuid))
                    {
                        engine.Dispose(); ses.Pcm.Dispose();
                        return null;
                    }
                }

                engine.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
                Engine.SdlDiagLog.WriteLine($"VOICE [{name}] listening ({info.Culture.Name}, {phrases.Count} phrases, "
                    + (endpointId != null ? "endpoint" : ses.Capture != null ? "pad USB mic" : "pad BT direct") + ")");
                return ses;
            }
            catch (Exception ex)
            {
                Engine.SdlDiagLog.WriteLine("VOICE [" + name + "] session build failed: " + ex.Message);
                return null;
            }
        }

        private void TearDown(Session ses, string reason)
        {
            try { ses.Capture?.StopRecording(); } catch { }
            try { ses.Capture?.Dispose(); } catch { }
            try { ses.Engine?.RecognizeAsyncCancel(); } catch { }
            try { ses.Engine?.Dispose(); } catch { }
            try { ses.Pcm?.Dispose(); } catch { }
            Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] session down ({reason})");
        }

        private void OnRecognized(Session ses, string text, float conf)
        {
            bool gate = ListenGateOpen;
            bool fires = gate && conf >= MinConfidence;
            Engine.SdlDiagLog.WriteLine($"VOICE [{ses.DisplayName}] heard \"{text}\" conf={conf:F2} gate={(gate ? 1 : 0)} fires={(fires ? 1 : 0)}");
            PhraseHeard?.Invoke(ses.DisplayName, text ?? string.Empty, conf, fires);
            if (!fires) return;
            int button = VoicePhraseRegistry.ButtonForPhrase(text);
            if (ses.PadGuid != Guid.Empty) VoicePulse.Stamp(ses.PadGuid, button);
            else MicrophoneInputDevice.StampPulse(ses.EndpointId, button);
        }

        // ── The Bluetooth tee entry point. AudioPassthroughService calls
        // this with each decoded 48 kHz block, keyed by pad, whenever a
        // pad session wants it. 48 to 16 kHz is an exact 3:1 average;
        // speech energy sits far below the fold.
        public static bool PadMicWanted(Guid padGuid)
        {
            var svc = Active;
            if (svc == null || !Enabled) return false;
            if (ListeningMode != 0 && !ListenGateOpen) return false;
            lock (svc._sessionsLock)
                return svc._sessions.TryGetValue("pad:" + padGuid, out var ses) && ses.Capture == null;
        }

        public static void SubmitPadMic48k(Guid padGuid, ReadOnlySpan<short> interleaved, int channels)
        {
            var svc = Active;
            if (svc == null || channels <= 0) return;
            VoicePcmStream sink = null;
            lock (svc._sessionsLock)
                if (svc._sessions.TryGetValue("pad:" + padGuid, out var ses) && ses.Capture == null)
                    sink = ses.Pcm;
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
        private static WasapiCapture OpenEndpointCapture(string endpointId, VoicePcmStream sink)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                MMDevice dev = null;
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    if (string.Equals(d.ID, endpointId, StringComparison.Ordinal)) { dev = d; break; }
                return dev == null ? null : StartCapture(dev, sink);
            }
            catch { return null; }
        }

        private static WasapiCapture StartCapture(MMDevice dev, VoicePcmStream sink)
        {
            var cap = new WasapiCapture(dev);
            int inRate = cap.WaveFormat.SampleRate;
            int inCh = cap.WaveFormat.Channels;
            bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                || (cap.WaveFormat is WaveFormatExtensible wfx
                    && wfx.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            int inStride = inCh * (isFloat ? 4 : 2);
            double pos = 0, step = inRate / 16000.0;
            cap.DataAvailable += (_, a) =>
            {
                int frames = a.BytesRecorded / inStride;
                if (frames <= 0) return;
                if (ListeningMode != 0 && !ListenGateOpen) { pos = 0; return; }
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
                    outBuf[n++] = (byte)(q & 0xFF);
                    outBuf[n++] = (byte)((q >> 8) & 0xFF);
                    pos += step;
                }
                pos -= frames;
                if (pos < 0) pos = 0;
                if (n > 0) sink.Write(outBuf[..n]);
            };
            cap.StartRecording();
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
        private sealed class VoicePcmStream : Stream
        {
            private readonly object _sync = new();
            private readonly byte[] _ring = new byte[64 * 1024];
            private int _head, _count;
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
                lock (_sync)
                {
                    while (_count == 0 && !_closed) Monitor.Wait(_sync, 250);
                    if (_count == 0) return 0;
                    int n = Math.Min(count, _count);
                    for (int i = 0; i < n; i++)
                    {
                        buffer[offset + i] = _ring[_head];
                        _head = (_head + 1) % _ring.Length;
                    }
                    _count -= n;
                    return n;
                }
            }

            protected override void Dispose(bool disposing)
            {
                lock (_sync) { _closed = true; Monitor.PulseAll(_sync); }
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => long.MaxValue;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
