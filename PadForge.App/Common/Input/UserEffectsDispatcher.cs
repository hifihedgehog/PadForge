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
        private const ushort PidStandard = 0x0CE6;  // DualSense
        private const ushort PidEdge = 0x0DF2;      // DualSense Edge
        // DualShock 4 family — three PIDs cover the v1, v1 alternate, and
        // v2 hardware revisions. Same VID, different output report
        // shape (Report 0x05 USB / 0x11 BT, no AT / no player LEDs / no
        // mic LED). Lighting tab base color + audio modes apply via the
        // DS4 path; AT and Indicator LED settings are silently ignored.
        private const ushort Ds4Pid_V1     = 0x05C4;
        private const ushort Ds4Pid_V1Alt  = 0x09CC;
        private const ushort Ds4Pid_V2     = 0x0BA0;

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

        /// <summary>Static provider for the current rumble state of a
        /// given (slot, physical device) pair, returned as 8-bit
        /// right/left motor values (0..255). InputService wires this to
        /// scale the slot's raw <c>VibrationStates</c> by the specific
        /// device's PadSetting (audio rumble + ForceOverall + motor
        /// strengths + swap), so each Sony device mapped to the slot can
        /// have different gain or audio rumble settings. The synthesizer
        /// carries these values in every effect packet plus asserts bit
        /// 0 of validFlag1, so the 30 Hz lightbar dispatch doesn't crowd
        /// SDL3's separate SDL_RumbleJoystick writes off the BT HID
        /// channel.</summary>
        public static Func<int, Guid, (byte right, byte left)> SlotRumbleForDeviceProvider { get; set; }

        /// <summary>Static provider for the slot's raw (unscaled) rumble
        /// used for change detection only — when this changes mid audio
        /// tick, the dispatcher forces a fresh dispatch so the per-device
        /// motor bytes propagate immediately rather than waiting for the
        /// next audio peak update. Per-device scaling happens later via
        /// <see cref="SlotRumbleForDeviceProvider"/> in the device loop.</summary>
        public static Func<int, (byte right, byte left)> SlotRawRumbleProvider { get; set; }

        /// <summary>Static provider for the per-slot test-rumble target
        /// GUID. Returns <see cref="Guid.Empty"/> when no test rumble is
        /// active for the slot. When set, the dispatcher zeros the rumble
        /// bytes (and clears the rumble-emulation bit on DS5) for any
        /// physical device whose InstanceGuid doesn't match — otherwise an
        /// Xbox VC test rumble would still ride the dispatcher's effect
        /// packet and rumble every Sony device mapped to the slot. Step 2's
        /// SDL physical-rumble path already honors this filter via
        /// <c>InputManager.TestRumbleTargetGuid</c>.</summary>
        public static Func<int, Guid> TestRumbleTargetGuidProvider { get; set; }

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
        private volatile bool _disposed;

        // Per-mode runtime state. The synthesizer is stateless; the
        // dispatcher carries random-colour memory across audio onsets,
        // the active input-reactive pulse, and the previous button mask
        // for rising-edge detection.
        //
        // Per-(slot, device) state lives in <see cref="_deviceStates"/>
        // — each Sony device on the slot picks its own random hue or
        // palette entry per press so two DualSenses with different
        // palettes (or different per-device random rolls) flash
        // independently. The pulse start timestamp and previous button
        // mask are slot-level (one button-press event drives every
        // device's pulse together).
        private uint _randomColor;
        private bool _audioOnsetActive;
        private long _pulseStartMs;
        private ushort _lastButtons;
        private readonly Random _rng = new Random();

        private sealed class DeviceState
        {
            public uint PulseColor;
            public int PalettePulseIndex;
        }
        private readonly Dictionary<Guid, DeviceState> _deviceStates = new();

        private DeviceState GetOrCreateDeviceState(Guid deviceGuid)
        {
            if (!_deviceStates.TryGetValue(deviceGuid, out var state))
            {
                state = new DeviceState();
                _deviceStates[deviceGuid] = state;
            }
            return state;
        }

        /// <summary>Static provider returning every per-device
        /// <see cref="PlayStationSlotConfig"/> on a slot. The dispatcher's
        /// device loop reads this to synthesize per-device output (each
        /// device renders its own LightbarMode + colors / palette).
        /// Wired by InputService to
        /// <c>InputManager._perDevicePlayStationConfigs[slot]</c>.</summary>
        public static Func<int, IReadOnlyDictionary<Guid, PlayStationSlotConfig>> SlotPerDeviceConfigsProvider { get; set; }

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
            // should be running. So can the macro-override expiry — when
            // a macro fires LightbarColor and the slot's mode is Off, the
            // timer is otherwise asleep and the override packet would
            // never go out without this nudge.
            if (e.PropertyName == nameof(PlayStationSlotConfig.LightbarMode)
                || e.PropertyName == nameof(PlayStationSlotConfig.LightbarPeriodMs)
                || e.PropertyName == nameof(PlayStationSlotConfig.MacroOverrideExpiresAtUtc))
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
                  or LightbarMode.InputReactive
                  or LightbarMode.InputReactiveCycle
                  or LightbarMode.InputReactiveFixed;

        private void UpdateAnimTimer()
        {
            // Timer wants to run when:
            //   - LightbarMode is animated (audio / breathing / etc.), or
            //   - A Reactive macro override is in flight (intensity is
            //     decaying and needs per-tick re-dispatch).
            // A Sticky override has constant RGB and constant intensity
            // (1.0), so the dispatcher just needs the one snapshot fired
            // off the OnConfigChanged event. No timer required.
            // Walk every per-device config on the slot — the timer runs
            // when any device wants animation or has a reactive override
            // in flight, not just the SelectedMappedDevice's. Falls back
            // to the anchor _config when the per-device dictionary
            // hasn't been wired yet (early startup).
            bool wantTimer = false;
            if (!_disposed)
            {
                var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
                if (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
                {
                    foreach (var kvp in perDeviceCfgs)
                    {
                        var devCfg = kvp.Value;
                        if (devCfg == null) continue;
                        if (IsAnimated(devCfg.LightbarMode))
                        {
                            wantTimer = true;
                            break;
                        }
                        if (devCfg.HasActiveMacroLightbarOverride
                            && devCfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive)
                        {
                            wantTimer = true;
                            break;
                        }
                    }
                }
                else if (_config != null)
                {
                    bool reactiveOverrideRunning =
                        _config.HasActiveMacroLightbarOverride
                        && _config.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
                    wantTimer = IsAnimated(_config.LightbarMode) || reactiveOverrideRunning;
                }
            }

            if (wantTimer && !_animTickActive)
            {
                _animTickActive = true;
                _animTimer = new System.Threading.Timer(
                    OnAnimTick, null, AnimTickMs, AnimTickMs);
                DiagLog($"anim timer started anchorMode={_config?.LightbarMode}");
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
        private byte _lastDispatchedRumbleR;
        private byte _lastDispatchedRumbleL;
        private bool _lastTickOverrideActive;

        private void OnAnimTick(object _)
        {
            if (_disposed || _config == null) return;

            // Aggregate state across every per-device config on the
            // slot. The timer only stops when NO device wants it.
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
            bool anyAnimated = false;
            bool anyReactiveRunning = false;
            bool anyAudioMode = false;
            bool anyAudioPulseRandom = false;
            float maxSensitivity = (float)_config.AudioLightbarSensitivity;
            if (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
            {
                maxSensitivity = 0f;
                foreach (var kvp in perDeviceCfgs)
                {
                    var devCfg = kvp.Value;
                    if (devCfg == null) continue;
                    var devMode = devCfg.LightbarMode;
                    if (IsAnimated(devMode)) anyAnimated = true;
                    if (IsAudioMode(devMode))
                    {
                        anyAudioMode = true;
                        if (devMode == LightbarMode.AudioPulseRandom) anyAudioPulseRandom = true;
                    }
                    var s = (float)devCfg.AudioLightbarSensitivity;
                    if (s > maxSensitivity) maxSensitivity = s;
                    if (devCfg.HasActiveMacroLightbarOverride
                        && devCfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive)
                        anyReactiveRunning = true;
                }
            }
            else
            {
                // No per-device dictionary wired yet — fall back to anchor.
                var mode = _config.LightbarMode;
                anyAnimated = IsAnimated(mode);
                anyAudioMode = IsAudioMode(mode);
                anyAudioPulseRandom = mode == LightbarMode.AudioPulseRandom;
                bool overrideActive = _config.HasActiveMacroLightbarOverride;
                anyReactiveRunning = overrideActive && _config.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
            }

            // If no device wants an animated mode or a running Reactive
            // override, dispatch one final snapshot (so a just-expired
            // override hands off cleanly to the configured base/off
            // state) and stop the timer. Sticky holds don't keep the
            // timer running — RGB and intensity are constant.
            if (!anyAnimated && !anyReactiveRunning)
            {
                if (_lastTickOverrideActive)
                {
                    DispatchSnapshot();
                }
                _lastTickOverrideActive = false;
                StopAnimTimer();
                return;
            }
            _lastTickOverrideActive = anyReactiveRunning;

            // Reactive-only path (no device animated): dispatch every
            // tick so each device's intensity ramp is smooth. Skip the
            // audio/pulse recomputation — the synthesizer pulls each
            // device's intensity from its own config.
            if (!anyAnimated && anyReactiveRunning)
            {
                DispatchSnapshot();
                return;
            }

            // Slot-level audio peak — used only by the steady-state
            // early-exit below. Per-device peak scaling happens inside
            // the device synth call. Use the slot's max sensitivity so
            // the early-exit threshold doesn't suppress a device that's
            // more sensitive than the selected one.
            float rawPeak = AudioPeakProvider?.Invoke() ?? 0f;
            float scaled = Math.Clamp(rawPeak * maxSensitivity, 0f, 1f);

            // Roll a new random colour on the rising edge of an audio
            // onset, so AudioPulseRandom flashes a fresh hue per pulse.
            // Slot-level — every AudioPulseRandom device on the slot
            // shares the same per-onset hue.
            if (anyAudioPulseRandom)
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

            // Drain button rising edges. The slot-level button mask is
            // shared across devices but each device rolls its own pulse
            // colour using its own LightbarMode + palette, so two
            // DualSenses on the slot can flash independently.
            DrainInputPulses();

            if (anyAudioMode)
            {
                float delta = MathF.Abs(scaled - _lastDispatchedPeak);
                bool zeroCrossing =
                    (scaled == 0f && _lastDispatchedPeak > 0f)
                    || (_lastDispatchedPeak == 0f && scaled > 0f);

                // Don't suppress the dispatch when game rumble changes —
                // even a steady audio peak shouldn't stall the rumble
                // passthrough. Uses the slot's raw rumble for change
                // detection; per-device scaling happens later in the
                // device loop via SlotRumbleForDeviceProvider.
                var r = SlotRawRumbleProvider?.Invoke(_padIndex) ?? ((byte)0, (byte)0);
                bool rumbleChanged = r.right != _lastDispatchedRumbleR || r.left != _lastDispatchedRumbleL;

                // Suppress the rainbow-pulse mode's special-case
                // anti-skip only when ANY device is on AudioPulseRainbow
                // — keeps that mode's per-tick hue rotation alive.
                bool anyAudioPulseRainbow = AnyDeviceMode(perDeviceCfgs, LightbarMode.AudioPulseRainbow);
                if (!zeroCrossing && !rumbleChanged && delta < 0.004f && !anyAudioPulseRainbow)
                    return;
                _lastDispatchedPeak = scaled;
                _lastDispatchedRumbleR = r.right;
                _lastDispatchedRumbleL = r.left;
            }

            DispatchSnapshot(scaled);
        }

        /// <summary>True when any device's config on the slot is in the
        /// given <see cref="LightbarMode"/>. Used by tick-suppression
        /// special-cases (e.g. AudioPulseRainbow's per-tick rotation).</summary>
        private static bool AnyDeviceMode(IReadOnlyDictionary<Guid, PlayStationSlotConfig> cfgs, LightbarMode mode)
        {
            if (cfgs == null) return false;
            foreach (var kvp in cfgs)
                if (kvp.Value != null && kvp.Value.LightbarMode == mode) return true;
            return false;
        }

        private static bool IsAudioMode(LightbarMode m) =>
            m is LightbarMode.AudioPulse
              or LightbarMode.AudioPulseRandom
              or LightbarMode.AudioPulseRainbow
              or LightbarMode.AudioThresholds
              or LightbarMode.AudioGradient
              or LightbarMode.AudioCrossFade;

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
            // Slot-level button-press detection — one rising-edge event
            // per tick fans out to every per-device pulse below.
            var provider = SlotButtonsProvider;
            ushort buttons = provider != null ? provider(_padIndex) : (ushort)0;
            ushort newlyPressed = (ushort)(buttons & ~_lastButtons);
            _lastButtons = buttons;
            if (newlyPressed == 0) return;

            // Roll per-device pulse colour using each device's own
            // mode + palette. A device in InputReactive (random hue)
            // gets its own random roll; a device in InputReactiveCycle
            // advances its own palette index; a device in
            // InputReactiveFixed needs no roll (synthesizer reads the
            // device's static LightbarRed/G/B).
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
            if (perDeviceCfgs != null)
            {
                foreach (var kvp in perDeviceCfgs)
                {
                    var devCfg = kvp.Value;
                    if (devCfg == null) continue;
                    var devMode = devCfg.LightbarMode;
                    if (devMode != LightbarMode.InputReactive
                        && devMode != LightbarMode.InputReactiveCycle
                        && devMode != LightbarMode.InputReactiveFixed)
                        continue;

                    var state = GetOrCreateDeviceState(kvp.Key);
                    if (devMode == LightbarMode.InputReactive)
                    {
                        int h = _rng.Next(0, 360);
                        HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
                        state.PulseColor = (uint)((r << 16) | (g << 8) | b);
                    }
                    else if (devMode == LightbarMode.InputReactiveCycle)
                    {
                        var palette = devCfg.SnapshotLightbarPalette();
                        int n = palette.Length;
                        if (n > 0)
                        {
                            state.PalettePulseIndex = (state.PalettePulseIndex + 1) % n;
                            var entry = palette[state.PalettePulseIndex];
                            state.PulseColor = (uint)((entry.R << 16) | (entry.G << 8) | entry.B);
                        }
                        else
                        {
                            state.PulseColor = 0;
                        }
                    }
                    // InputReactiveFixed: synthesizer reads the device's
                    // own LightbarRed/G/B; no per-device pulse colour to
                    // roll here.
                }
            }
            _pulseStartMs = Environment.TickCount64;
        }

        private float ComputePulseIntensity(long nowMs, PlayStationSlotConfig cfg)
        {
            if (_pulseStartMs == 0 || cfg == null) return 0f;
            long elapsed = nowMs - _pulseStartMs;
            int hold = Math.Max(cfg.LightbarInputHoldMs, 0);
            int decay = Math.Max(cfg.LightbarInputDecayMs, 0);
            if (elapsed < 0) return 1f;
            if (elapsed < hold) return 1f;
            if (decay <= 0) return elapsed >= hold ? 0f : 1f;
            long fadeElapsed = elapsed - hold;
            if (fadeElapsed >= decay) return 0f;
            return 1f - (float)fadeElapsed / decay;
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
            float rawAudioPeak = AudioPeakProvider?.Invoke() ?? 0f;
            // Per-device peak scaling happens inside the device loop (each
            // device has its own AudioLightbarSensitivity); this fallback
            // uses the slot's "anchor" config sensitivity for the
            // non-tick path's pre-loop default.
            float peakForSynthDefault = audioPeak >= 0f
                ? audioPeak
                : Math.Clamp(
                    rawAudioPeak * (float)_config.AudioLightbarSensitivity,
                    0f, 1f);
            long nowMs = Environment.TickCount64;

            // Test-rumble target for this slot. When set, only the matching
            // device receives the rumble bytes inside the effect packet —
            // every other Sony device mapped to the slot still gets its
            // lightbar / trigger / mic-LED updates but with rumble bytes
            // zeroed out. Without this gate, an Xbox-VC test rumble would
            // ride the dispatcher's 30 Hz packet to every DualSense mapped
            // to the slot. Step 2's SDL physical-rumble path already honors
            // the same filter via InputManager.TestRumbleTargetGuid.
            Guid testTarget = TestRumbleTargetGuidProvider?.Invoke(_padIndex) ?? Guid.Empty;

            // Per-(slot, device) lighting configs — each Sony device on
            // the slot synthesizes from its own LightbarMode / colors /
            // palette / decay so two DualSenses can light up
            // independently. Falls back to the dispatcher's anchor
            // _config (the slot's selected device) when the per-device
            // dictionary hasn't been wired yet.
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);

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

            int sent = 0, skippedNotPs = 0, skippedOffline = 0, skippedNoHandle = 0, errors = 0;
            int allPsOnline = 0, allPsOnlineMapped = 0;
            byte[] ds5Buffer = new byte[Ds5EffectSynthesizer.PayloadSize];
            byte[] ds4UsbBuf = null;
            byte[] ds4BtBuf = null;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null) continue;

                    bool isDs5 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == PidStandard || ud.ProdId == PidEdge);
                    bool isDs4 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == Ds4Pid_V1 || ud.ProdId == Ds4Pid_V1Alt || ud.ProdId == Ds4Pid_V2);
                    bool isPs = isDs5 || isDs4;
                    if (isPs && ud.IsOnline) allPsOnline++;

                    bool inMappedGuids = guids.Contains(ud.InstanceGuid);
                    if (isPs && ud.IsOnline && inMappedGuids) allPsOnlineMapped++;

                    if (isPs)
                    {
                        bool isBt = Ds5RawHidWriter.IsBluetoothPath(ud.DevicePath);
                        string family = isDs5 ? "DS5" : "DS4";
                        DiagLog($"  device {family} guid={ud.InstanceGuid} vid={ud.VendorId:X4} pid={ud.ProdId:X4} online={ud.IsOnline} mapped={inMappedGuids} bt={isBt} path={ud.DevicePath}");
                    }

                    if (!inMappedGuids) continue;
                    if (!ud.IsOnline) { skippedOffline++; continue; }
                    if (!isPs) { skippedNotPs++; continue; }

                    string path = ud.DevicePath;
                    if (string.IsNullOrEmpty(path)) { skippedNoHandle++; continue; }
                    bool isBluetooth = Ds5RawHidWriter.IsBluetoothPath(path);

                    // Per-device rumble bytes — each Sony device on the
                    // slot pulls its OWN PadSetting (audio rumble + gain
                    // + motor balance + swap) so different physical
                    // devices on the same slot get different output.
                    var perDevRumble = SlotRumbleForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid)
                                       ?? ((byte)0, (byte)0);
                    // Test-rumble target gates the rumble bytes only —
                    // lightbar/trigger/mic-LED still update on non-target
                    // devices so an active animation doesn't freeze across
                    // the 500 ms test window.
                    bool deliverRumble = testTarget == Guid.Empty || ud.InstanceGuid == testTarget;
                    byte rR = deliverRumble ? perDevRumble.right : (byte)0;
                    byte rL = deliverRumble ? perDevRumble.left  : (byte)0;

                    // Resolve this device's per-device lighting config.
                    // Falls back to the slot's anchor config if missing
                    // (transient case before the dictionary is wired).
                    PlayStationSlotConfig devCfg = null;
                    if (perDeviceCfgs != null
                        && perDeviceCfgs.TryGetValue(ud.InstanceGuid, out var resolved))
                        devCfg = resolved;
                    devCfg ??= _config;
                    if (devCfg == null) continue;

                    // Per-device peak scaling (each device has own
                    // AudioLightbarSensitivity).
                    float devPeak = audioPeak >= 0f
                        ? audioPeak
                        : Math.Clamp(
                            rawAudioPeak * (float)devCfg.AudioLightbarSensitivity,
                            0f, 1f);

                    // Per-device pulse colour + intensity (DrainInputPulses
                    // rolled per-device above).
                    var devState = _deviceStates.TryGetValue(ud.InstanceGuid, out var ds) ? ds : null;
                    uint devPulseColor = devState?.PulseColor ?? 0;
                    float devPulseIntensity = ComputePulseIntensity(nowMs, devCfg);

                    try
                    {
                        bool ok;
                        if (isDs5)
                        {
                            // ── CRITICAL: DS5 effect packet rumble byte contract ──
                            //
                            // PadForge writes DS5 effect packets via raw HID
                            // (Ds5RawHidWriter) at up to 30 Hz, BYPASSING SDL3
                            // entirely. SDL3's PS5 driver also writes effect
                            // packets — for SDL_RumbleJoystick calls the SDL
                            // path carries the audio-mixed rumble bytes from
                            // ForceFeedbackState.SetDeviceForces. Two writers,
                            // same DS5: per Ds5RawHidWriter's own docstring,
                            // "the firmware applies whichever WriteFile lands
                            // most recently."
                            //
                            // That means: every PadForge dispatcher write is
                            // ALSO writing rumble bytes from this packet's
                            // perspective. If the dispatcher writes 0 motor
                            // values 30 Hz between SDL's audio-rumble writes,
                            // motors pulse audio→0→audio→0 — average strength
                            // collapses (the v3.1.x audio-rumble regression).
                            //
                            // Two rules that MUST hold for audio rumble to
                            // feel right:
                            //   1. Bit 0 of validFlag1 (EnableRumbleEmulation)
                            //      stays set unconditionally on every
                            //      dispatcher packet. Clearing it ("disable
                            //      compatibility motor mode") races SDL's
                            //      bit-0-set writes off the channel.
                            //   2. The rumble bytes the dispatcher carries
                            //      MUST include audio mix (when audio rumble
                            //      is enabled) so the dispatcher reinforces
                            //      SDL's audio rumble rather than fighting
                            //      it. SlotRumbleForDeviceProvider runs
                            //      ScaleRumbleForDevice for this — it pulls
                            //      raw VibrationStates (game rumble) and
                            //      mixes audio in, yielding the same value
                            //      SDL sends.
                            //
                            // For test-rumble target gating (only the picked
                            // device should rumble), we still zero rR/rL on
                            // non-target devices — but bit 0 stays set so the
                            // firmware applies our zero in compatibility mode
                            // (a transient zero SDL's next write can replace
                            // if it really wants to drive that device). That
                            // matches 3.1.0 behavior; it does NOT compound
                            // into a steady-state motor kill.
                            int ds5Len = Ds5EffectSynthesizer.Build(
                                devCfg, ds5Buffer, devPeak, nowMs,
                                _randomColor, devPulseColor, devPulseIntensity,
                                rR, rL);
                            if (ds5Len <= 0) { errors++; continue; }
                            ok = Ds5RawHidWriter.Write(path, ds5Buffer.AsSpan(0, ds5Len));
                            DiagLog($"  raw-write ds5 ok={ok} rumble=({rR},{rL}) testTarget={testTarget != Guid.Empty} deliverRumble={deliverRumble} diag='{Ds5RawHidWriter.LastWriteDiag}'");
                        }
                        else
                        {
                            // DS4 path — full output report built inline
                            // (USB report 0x05 = 32 bytes, BT report 0x11
                            // = 78 bytes with CRC32 trailer). Synthesizes
                            // per-device using each DS4's own config so
                            // mode / colors / palette match the
                            // Lighting tab for that device.
                            byte[] packet;
                            if (isBluetooth)
                            {
                                if (ds4BtBuf == null) ds4BtBuf = new byte[Ds4EffectSynthesizer.BluetoothPacketSize];
                                int n = Ds4EffectSynthesizer.BuildBluetooth(
                                    devCfg, ds4BtBuf, devPeak, nowMs,
                                    _randomColor, devPulseColor, devPulseIntensity,
                                    rR, rL);
                                if (n <= 0) { errors++; continue; }
                                packet = ds4BtBuf;
                            }
                            else
                            {
                                if (ds4UsbBuf == null) ds4UsbBuf = new byte[Ds4EffectSynthesizer.UsbPacketSize];
                                int n = Ds4EffectSynthesizer.BuildUsb(
                                    devCfg, ds4UsbBuf, devPeak, nowMs,
                                    _randomColor, devPulseColor, devPulseIntensity,
                                    rR, rL);
                                if (n <= 0) { errors++; continue; }
                                packet = ds4UsbBuf;
                            }
                            ok = Ds5RawHidWriter.WriteFullPacket(path, packet);
                            DiagLog($"  raw-write ds4 bt={isBluetooth} ok={ok} rumble=({rR},{rL}) testTarget={testTarget != Guid.Empty} deliverRumble={deliverRumble} diag='{Ds5RawHidWriter.LastWriteDiag}'");
                        }

                        if (ok) sent++;
                        else errors++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        DiagLog($"raw-write threw: {ex.GetType().Name} {ex.Message}");
                    }
                }
            }
            DiagLog($"DispatchSnapshot sent={sent} skipped(not-ps)={skippedNotPs} skipped(offline)={skippedOffline} skipped(no-handle)={skippedNoHandle} errors={errors} allPsOnline={allPsOnline} allPsOnlineMapped={allPsOnlineMapped}");
        }
    }
}
