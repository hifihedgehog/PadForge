using System;
using System.ComponentModel;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Per-virtual-DS5-VC dispatcher for Feature B (user-configured
    /// adaptive trigger / lightbar / audio effects). Subscribes to the
    /// slot's <see cref="PlayStationSlotConfig"/> PropertyChanged and
    /// re-synthesizes + sends the DS5 effect message to every assigned
    /// physical DualSense whenever the user touches a setting on the
    /// Adaptive Triggers or Lighting tab.
    ///
    /// <para>Game-driven Feature A passthrough (handled by
    /// <see cref="DualSensePassthroughDispatcher"/>) runs independently
    /// — game writes win per packet because they fire at high cadence.
    /// Feature B fills the silence between game writes: when the
    /// user-configured layer is enabled and no game has written
    /// recently, the trigger / lightbar settings the user picked here
    /// are what the physical pad reflects.</para>
    ///
    /// <para>The dispatcher writes synchronously on the UI thread when
    /// PropertyChanged fires. The cost is one byte-array allocation
    /// (47 bytes), one synthesizer call, and one
    /// <see cref="SDL_SendGamepadEffect"/> per assigned physical DS5.
    /// Total is well under a millisecond per user-interaction event,
    /// which is bounded by how fast a human can drag a slider.</para>
    /// </summary>
    internal sealed class UserEffectsDispatcher : IDisposable
    {
        private const ushort SonyVid = 0x054C;
        private const ushort PidStandard = 0x0CE6;
        private const ushort PidEdge = 0x0DF2;

        /// <summary>Static provider for the system audio peak (0..1).
        /// InputService wires this to <c>AudioBassDetector.FullSpectrumPeak</c>
        /// at startup. Returns 0 when the detector hasn't been initialized
        /// yet — audio-to-lightbar then dispatches a black frame, harmless.</summary>
        public static Func<float> AudioPeakProvider { get; set; }

        /// <summary>Static provider for the current button-state bitmap
        /// of a given pad index. InputService wires this to read from
        /// <c>InputManager.CombinedOutputStates[i].Buttons</c>. Used by
        /// <see cref="LightbarMode.InputReactive"/> to detect rising edges
        /// and enqueue a fading pulse.</summary>
        public static Func<int, ushort> SlotButtonsProvider { get; set; }

        // Animated-lightbar polling cadence — 30Hz is enough to feel
        // responsive without flooding the BT HID write path. WriteFile
        // open+close is ~1ms per call; 30Hz = 30ms budget.
        private const int AnimTickMs = 33;

        // Audio onset threshold for AudioPulseRandom: peak rising from
        // below this to above it counts as a pulse onset and rolls a
        // new random colour.
        private const float AudioOnsetEnter = 0.30f;
        private const float AudioOnsetExit  = 0.15f;

        private readonly int _padIndex;
        private PlayStationSlotConfig _config;
        private System.Threading.Timer _animTimer;
        private bool _animTickActive;
        private bool _disposed;

        // Per-mode runtime state. The synthesizer is stateless; the
        // dispatcher carries random-colour memory across audio onsets,
        // the active input-reactive pulse, and the previous button mask
        // for rising-edge detection.
        private uint _randomColor;
        private bool _audioOnsetActive;
        private uint _pulseColor;
        private long _pulseStartMs;
        private ushort _lastButtons;
        private int _palettePulseIndex;
        private readonly Random _rng = new Random();

        public UserEffectsDispatcher(int padIndex, PlayStationSlotConfig config)
        {
            _padIndex = padIndex;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            RollRandomColor();
            UpdateAnimTimer();
            DiagLog($"ctor padIndex={padIndex} config={(config == null ? "null" : "ok")}");
        }

        // ────────────────────────────────────────────────
        //  Diagnostic file log
        // ────────────────────────────────────────────────
        // Writes to %TEMP%\padforge-ds5-passthrough.log so the developer
        // can inspect the Feature B dispatch chain without attaching a
        // debugger. Best-effort — IO failures are swallowed.
        private static readonly string DiagLogPath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-ds5-passthrough.log");
        private static readonly object DiagLock = new();

        private void DiagLog(string line)
        {
            try
            {
                lock (DiagLock)
                {
                    System.IO.File.AppendAllText(DiagLogPath,
                        $"{DateTime.UtcNow:HH:mm:ss.fff} pad={_padIndex} {line}\n");
                }
            }
            catch { }
        }

        /// <summary>Re-binds to a new <see cref="PlayStationSlotConfig"/>
        /// instance. Used when the parent <see cref="PadViewModel"/>
        /// reassigns its config via the setter (e.g. profile load).</summary>
        public void Rebind(PlayStationSlotConfig config)
        {
            if (_disposed) return;
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            UpdateAnimTimer();
            // Push a snapshot immediately so the assigned DS5 reflects
            // the new config without waiting for the next user edit.
            ApplyOnce();
        }

        /// <summary>Manually trigger one apply pass. Used after the
        /// dispatcher is constructed (initial state) and from Rebind.</summary>
        public void ApplyOnce()
        {
            if (_disposed || _config == null) return;
            DispatchSnapshot();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAnimTimer();
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = null;
        }

        private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            DiagLog($"OnConfigChanged property={e.PropertyName}");
            // Mode / period changes can flip whether the periodic timer
            // should be running.
            if (e.PropertyName == nameof(PlayStationSlotConfig.LightbarMode)
                || e.PropertyName == nameof(PlayStationSlotConfig.LightbarPeriodMs))
                UpdateAnimTimer();
            DispatchSnapshot();
        }

        // ────────────────────────────────────────────────
        //  Animation / audio / input-reactive timer
        // ────────────────────────────────────────────────
        // Runs while the active LightbarMode is animated (anything that
        // depends on time, audio peak, or input state). Idle modes (Off
        // and Static) only dispatch on config changes, so the timer
        // stays parked.

        private static bool IsAnimated(LightbarMode mode) =>
            mode is LightbarMode.Breathing
                  or LightbarMode.Rainbow
                  or LightbarMode.ColorCycle
                  or LightbarMode.AudioPulse
                  or LightbarMode.AudioPulseRandom
                  or LightbarMode.AudioPulseRainbow
                  or LightbarMode.AudioThresholds
                  or LightbarMode.AudioGradient
                  or LightbarMode.AudioCrossFade
                  or LightbarMode.InputReactive;

        private void UpdateAnimTimer()
        {
            bool wantTimer = !_disposed
                && _config != null
                && IsAnimated(_config.LightbarMode);

            if (wantTimer && !_animTickActive)
            {
                _animTickActive = true;
                _animTimer = new System.Threading.Timer(
                    OnAnimTick, null, AnimTickMs, AnimTickMs);
                DiagLog($"anim timer started mode={_config.LightbarMode}");
            }
            else if (!wantTimer && _animTickActive)
            {
                StopAnimTimer();
            }
        }

        private void StopAnimTimer()
        {
            _animTickActive = false;
            try { _animTimer?.Dispose(); } catch { }
            _animTimer = null;
            _lastDispatchedPeak = -1f;
        }

        private float _lastDispatchedPeak = -1f;

        private void OnAnimTick(object _)
        {
            if (_disposed || _config == null) return;
            var mode = _config.LightbarMode;
            if (!IsAnimated(mode)) return;

            // Audio-driven modes also do an early-exit if the peak hasn't
            // changed by 1/255 (≈0.004), to avoid flooding the HID pipe
            // with no-op packets while the signal is steady. Time-based
            // and input-reactive modes always dispatch — they animate on
            // every tick by definition.
            bool audioMode =
                mode is LightbarMode.AudioPulse
                     or LightbarMode.AudioPulseRandom
                     or LightbarMode.AudioPulseRainbow
                     or LightbarMode.AudioThresholds
                     or LightbarMode.AudioGradient
                     or LightbarMode.AudioCrossFade;

            float rawPeak = AudioPeakProvider?.Invoke() ?? 0f;
            float scaled = Math.Clamp(rawPeak * (float)_config.AudioLightbarSensitivity, 0f, 1f);

            // Roll a new random colour on the rising edge of an audio
            // onset, so AudioPulseRandom flashes a fresh hue per pulse.
            if (mode == LightbarMode.AudioPulseRandom)
            {
                if (!_audioOnsetActive && scaled >= AudioOnsetEnter)
                {
                    _audioOnsetActive = true;
                    RollRandomColor();
                }
                else if (_audioOnsetActive && scaled <= AudioOnsetExit)
                {
                    _audioOnsetActive = false;
                }
            }

            // Drain button rising edges into pulses for InputReactive.
            if (mode == LightbarMode.InputReactive)
                DrainInputPulses();

            if (audioMode)
            {
                float delta = MathF.Abs(scaled - _lastDispatchedPeak);
                bool zeroCrossing =
                    (scaled == 0f && _lastDispatchedPeak > 0f)
                    || (_lastDispatchedPeak == 0f && scaled > 0f);
                if (!zeroCrossing && delta < 0.004f && mode != LightbarMode.AudioPulseRainbow)
                    return;
                _lastDispatchedPeak = scaled;
            }

            DispatchSnapshot(scaled);
        }

        private void RollRandomColor()
        {
            // Pick a vivid hue uniformly. Saturation+value pinned to 1
            // so the colour reads cleanly through the diffuser at any
            // peak intensity.
            int h = _rng.Next(0, 360);
            HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
            _randomColor = (uint)((r << 16) | (g << 8) | b);
        }

        private void DrainInputPulses()
        {
            if (_config == null) return;
            var provider = SlotButtonsProvider;
            ushort buttons = provider != null ? provider(_padIndex) : (ushort)0;
            ushort newlyPressed = (ushort)(buttons & ~_lastButtons);
            _lastButtons = buttons;

            if (newlyPressed != 0)
            {
                // One pulse per tick is plenty even if multiple buttons
                // came down in the same frame — last-press-wins matches
                // how the user perceives a chord vs a sequence.
                if (_config.LightbarInputRandomize)
                {
                    int h = _rng.Next(0, 360);
                    HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
                    _pulseColor = (uint)((r << 16) | (g << 8) | b);
                }
                else
                {
                    _palettePulseIndex = (_palettePulseIndex + 1) & 3;
                    var (pr, pg, pb) = _palettePulseIndex switch
                    {
                        0 => (_config.LightbarPalette1R, _config.LightbarPalette1G, _config.LightbarPalette1B),
                        1 => (_config.LightbarPalette2R, _config.LightbarPalette2G, _config.LightbarPalette2B),
                        2 => (_config.LightbarPalette3R, _config.LightbarPalette3G, _config.LightbarPalette3B),
                        _ => (_config.LightbarPalette4R, _config.LightbarPalette4G, _config.LightbarPalette4B),
                    };
                    _pulseColor = (uint)((pr << 16) | (pg << 8) | pb);
                }
                _pulseStartMs = Environment.TickCount64;
            }
        }

        private float ComputePulseIntensity(long nowMs)
        {
            if (_pulseStartMs == 0 || _config == null) return 0f;
            long elapsed = nowMs - _pulseStartMs;
            int decay = Math.Max(_config.LightbarInputDecayMs, 50);
            if (elapsed <= 0) return 1f;
            if (elapsed >= decay) return 0f;
            return 1f - (float)elapsed / decay;
        }

        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double rp, gp, bp;
            if (h < 60)       { rp = c; gp = x; bp = 0; }
            else if (h < 120) { rp = x; gp = c; bp = 0; }
            else if (h < 180) { rp = 0; gp = c; bp = x; }
            else if (h < 240) { rp = 0; gp = x; bp = c; }
            else if (h < 300) { rp = x; gp = 0; bp = c; }
            else              { rp = c; gp = 0; bp = x; }
            r = (byte)Math.Round((rp + m) * 255);
            g = (byte)Math.Round((gp + m) * 255);
            b = (byte)Math.Round((bp + m) * 255);
        }

        private void DispatchSnapshot(float audioPeak = -1f)
        {
            if (_config == null) { DiagLog("DispatchSnapshot config=null"); return; }

            // For non-tick dispatches (slider drag, OnDevicesUpdated re-
            // apply, etc.), pull the current peak so the audio path
            // doesn't snap to black between ticks. The synthesizer
            // ignores the peak when the active mode doesn't read it.
            float peakForSynth = audioPeak >= 0f
                ? audioPeak
                : Math.Clamp(
                    (AudioPeakProvider?.Invoke() ?? 0f)
                    * (float)_config.AudioLightbarSensitivity,
                    0f, 1f);
            long nowMs = Environment.TickCount64;
            float pulseIntensity = ComputePulseIntensity(nowMs);

            // Synthesize once per dispatch; reuse the buffer across the
            // multi-DS5 fan-out below.
            var buffer = new byte[Ds5EffectSynthesizer.PayloadSize];
            int len = Ds5EffectSynthesizer.Build(
                _config, buffer, peakForSynth, nowMs,
                _randomColor, _pulseColor, pulseIntensity);
            if (len <= 0) { DiagLog("DispatchSnapshot synth-len=0"); return; }

            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null)
            {
                DiagLog($"DispatchSnapshot settings={(settings == null ? "null" : "ok")} devices={(devices == null ? "null" : "ok")}");
                return;
            }

            // Resolve assigned DS5 GUIDs.
            var guids = new System.Collections.Generic.List<Guid>(4);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != _padIndex) continue;
                    if (us.InstanceGuid == Guid.Empty) continue;
                    guids.Add(us.InstanceGuid);
                }
            }
            DiagLog($"DispatchSnapshot mappedGuids={guids.Count}");
            if (guids.Count == 0) return;

            int sent = 0, skippedNotDs5 = 0, skippedOffline = 0, skippedNoHandle = 0, errors = 0;
            int allDs5Online = 0, allDs5OnlineMapped = 0;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null) continue;

                    // Per-device diagnostic — log every DS5 we see (mapped or not)
                    // so post-reconnect drift in InstanceGuid vs UserSettings.MapTo
                    // surfaces in the log.
                    bool isDs5 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == PidStandard || ud.ProdId == PidEdge);
                    if (isDs5 && ud.IsOnline) allDs5Online++;

                    bool inMappedGuids = guids.Contains(ud.InstanceGuid);
                    if (isDs5 && ud.IsOnline && inMappedGuids) allDs5OnlineMapped++;

                    if (isDs5)
                    {
                        bool isBt = Ds5RawHidWriter.IsBluetoothPath(ud.DevicePath);
                        DiagLog($"  device guid={ud.InstanceGuid} vid={ud.VendorId:X4} pid={ud.ProdId:X4} online={ud.IsOnline} mapped={inMappedGuids} bt={isBt} path={ud.DevicePath}");
                    }

                    if (!inMappedGuids) continue;
                    if (!ud.IsOnline) { skippedOffline++; continue; }
                    if (ud.VendorId != SonyVid) { skippedNotDs5++; continue; }
                    if (ud.ProdId != PidStandard && ud.ProdId != PidEdge) { skippedNotDs5++; continue; }

                    // Raw HID write — bypasses SDL3's PS5 driver, which
                    // races its own UpdateEffects packets against ours
                    // (SetDevicePlayerIndex on USB connect, BT
                    // CheckPendingLEDReset at ~10s post-connect, etc.).
                    // OpenRGB uses the same approach and has zero
                    // hot-plug issues with the lightbar.
                    string path = ud.DevicePath;
                    if (string.IsNullOrEmpty(path)) { skippedNoHandle++; continue; }

                    try
                    {
                        bool ok = Ds5RawHidWriter.Write(path, buffer.AsSpan(0, len));
                        DiagLog($"  raw-write ok={ok} diag='{Ds5RawHidWriter.LastWriteDiag}'");
                        if (ok) sent++;
                        else errors++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        DiagLog($"Ds5RawHidWriter.Write threw: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
            DiagLog($"DispatchSnapshot sent={sent} skipped(not-ds5)={skippedNotDs5} skipped(offline)={skippedOffline} skipped(no-handle)={skippedNoHandle} errors={errors} allDs5Online={allDs5Online} allDs5OnlineMapped={allDs5OnlineMapped}");
        }
    }
}
