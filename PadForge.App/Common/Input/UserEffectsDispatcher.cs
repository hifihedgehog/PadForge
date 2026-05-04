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
        /// given pad index, returned as 8-bit right/left motor values
        /// (0..255). InputService wires this to read from
        /// <c>InputManager.VibrationStates[i]</c>, scaled from the
        /// underlying ushort. The synthesizer carries these values in
        /// every effect packet plus asserts bit 0 of validFlag1, so the
        /// 30 Hz lightbar dispatch doesn't crowd SDL3's separate
        /// SDL_RumbleJoystick writes off the BT HID channel.</summary>
        public static Func<int, (byte right, byte left)> SlotRumbleProvider { get; set; }

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
            bool reactiveOverrideRunning =
                _config != null
                && _config.HasActiveMacroLightbarOverride
                && _config.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;

            bool wantTimer = !_disposed
                && _config != null
                && (IsAnimated(_config.LightbarMode) || reactiveOverrideRunning);

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
        private byte _lastDispatchedRumbleR;
        private byte _lastDispatchedRumbleL;
        private bool _lastTickOverrideActive;

        private void OnAnimTick(object _)
        {
            if (_disposed || _config == null) return;
            var mode = _config.LightbarMode;
            bool overrideActive = _config.HasActiveMacroLightbarOverride;
            bool reactiveRunning = overrideActive && _config.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
            bool animated = IsAnimated(mode);

            // If neither an animated mode nor a running Reactive override
            // needs us, dispatch one final snapshot (so a just-expired
            // override hands off cleanly to the configured base/off
            // state) and stop the timer. Sticky holds don't keep the
            // timer running — RGB and intensity are constant.
            if (!animated && !reactiveRunning)
            {
                if (_lastTickOverrideActive)
                {
                    // Override just expired this tick — flush a final
                    // packet so the lightbar transitions back cleanly.
                    DispatchSnapshot();
                }
                _lastTickOverrideActive = false;
                StopAnimTimer();
                return;
            }
            _lastTickOverrideActive = reactiveRunning;

            // Reactive-only path (mode is idle): dispatch every tick so
            // the intensity ramp is smooth. Skip the audio/pulse
            // recomputation — the synthesizer pulls intensity directly
            // from the config.
            if (!animated && reactiveRunning)
            {
                DispatchSnapshot();
                return;
            }

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

            // Drain button rising edges into pulses for the InputReactive
            // variants (random per press, cycle palette, or fixed slot color).
            if (mode == LightbarMode.InputReactive
                || mode == LightbarMode.InputReactiveCycle
                || mode == LightbarMode.InputReactiveFixed)
                DrainInputPulses(mode);

            if (audioMode)
            {
                float delta = MathF.Abs(scaled - _lastDispatchedPeak);
                bool zeroCrossing =
                    (scaled == 0f && _lastDispatchedPeak > 0f)
                    || (_lastDispatchedPeak == 0f && scaled > 0f);

                // Don't suppress the dispatch when game rumble changes —
                // even a steady audio peak shouldn't stall the rumble
                // passthrough.
                var r = SlotRumbleProvider?.Invoke(_padIndex) ?? ((byte)0, (byte)0);
                bool rumbleChanged = r.right != _lastDispatchedRumbleR || r.left != _lastDispatchedRumbleL;

                if (!zeroCrossing && !rumbleChanged && delta < 0.004f && mode != LightbarMode.AudioPulseRainbow)
                    return;
                _lastDispatchedPeak = scaled;
                _lastDispatchedRumbleR = r.right;
                _lastDispatchedRumbleL = r.left;
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

        private void DrainInputPulses(LightbarMode mode)
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
                if (mode == LightbarMode.InputReactive)
                {
                    int h = _rng.Next(0, 360);
                    HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
                    _pulseColor = (uint)((r << 16) | (g << 8) | b);
                }
                else // InputReactiveCycle
                {
                    // Timer thread can't read the live ObservableCollection
                    // directly without racing concurrent UI-thread palette
                    // edits — snapshot under the config's palette lock.
                    var palette = _config.SnapshotLightbarPalette();
                    int n = palette.Length;
                    if (n > 0)
                    {
                        _palettePulseIndex = (_palettePulseIndex + 1) % n;
                        var entry = palette[_palettePulseIndex];
                        _pulseColor = (uint)((entry.R << 16) | (entry.G << 8) | entry.B);
                    }
                    else
                    {
                        _pulseColor = 0;
                    }
                }
                _pulseStartMs = Environment.TickCount64;
            }
        }

        private float ComputePulseIntensity(long nowMs)
        {
            if (_pulseStartMs == 0 || _config == null) return 0f;
            long elapsed = nowMs - _pulseStartMs;
            int hold = Math.Max(_config.LightbarInputHoldMs, 0);
            int decay = Math.Max(_config.LightbarInputDecayMs, 0);
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
            float peakForSynth = audioPeak >= 0f
                ? audioPeak
                : Math.Clamp(
                    (AudioPeakProvider?.Invoke() ?? 0f)
                    * (float)_config.AudioLightbarSensitivity,
                    0f, 1f);
            long nowMs = Environment.TickCount64;
            float pulseIntensity = ComputePulseIntensity(nowMs);
            var rumble = SlotRumbleProvider?.Invoke(_padIndex) ?? ((byte)0, (byte)0);

            // Test-rumble target for this slot. When set, only the matching
            // device receives the rumble bytes inside the effect packet —
            // every other Sony device mapped to the slot still gets its
            // lightbar / trigger / mic-LED updates but with rumble bytes
            // zeroed out. Without this gate, an Xbox-VC test rumble would
            // ride the dispatcher's 30 Hz packet to every DualSense mapped
            // to the slot. Step 2's SDL physical-rumble path already honors
            // the same filter via InputManager.TestRumbleTargetGuid.
            Guid testTarget = TestRumbleTargetGuidProvider?.Invoke(_padIndex) ?? Guid.Empty;

            // Synthesize the DS5 payload once. Fan out below covers any
            // DualSense / DualSense Edge mapped to this slot. The DS4
            // payload is synthesized lazily inside the device loop the
            // first time a DS4 is encountered, since the USB and BT
            // packet shapes differ enough that we need per-device work.
            var ds5Buffer = new byte[Ds5EffectSynthesizer.PayloadSize];
            int ds5Len = Ds5EffectSynthesizer.Build(
                _config, ds5Buffer, peakForSynth, nowMs,
                _randomColor, _pulseColor, pulseIntensity,
                rumble.right, rumble.left);
            if (ds5Len <= 0) { DiagLog("DispatchSnapshot synth-len=0"); return; }

            // Build a parallel "no rumble" DS5 buffer once when test rumble
            // is active so the per-device write below is a buffer pick, not
            // a per-device synthesis. byte[1] bit 0 (EnableRumbleEmulation)
            // is cleared here too — without that gate the firmware ignores
            // the zeroed motor bytes and the rumble persists.
            byte[] ds5BufferNoRumble = null;
            if (testTarget != Guid.Empty)
            {
                ds5BufferNoRumble = new byte[Ds5EffectSynthesizer.PayloadSize];
                Buffer.BlockCopy(ds5Buffer, 0, ds5BufferNoRumble, 0, ds5Len);
                ds5BufferNoRumble[1] &= 0xFE; // clear validFlag1 bit 0
                ds5BufferNoRumble[2] = 0;     // OffRumbleRight
                ds5BufferNoRumble[3] = 0;     // OffRumbleLeft
            }

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

                    // Test-rumble target gates the rumble bytes only —
                    // lightbar/trigger/mic-LED still update on non-target
                    // devices so an active animation doesn't freeze across
                    // the 500 ms test window.
                    bool deliverRumble = testTarget == Guid.Empty || ud.InstanceGuid == testTarget;
                    byte rR = deliverRumble ? rumble.right : (byte)0;
                    byte rL = deliverRumble ? rumble.left  : (byte)0;

                    try
                    {
                        bool ok;
                        if (isDs5)
                        {
                            // DS5 path — wrap the 47-byte payload in the
                            // standard USB (0x02) or BT (0x31) envelope.
                            byte[] payload = deliverRumble ? ds5Buffer : ds5BufferNoRumble;
                            ok = Ds5RawHidWriter.Write(path, payload.AsSpan(0, ds5Len));
                            DiagLog($"  raw-write ds5 ok={ok} testTarget={testTarget != Guid.Empty} deliverRumble={deliverRumble} diag='{Ds5RawHidWriter.LastWriteDiag}'");
                        }
                        else
                        {
                            // DS4 path — full output report built inline
                            // (USB report 0x05 = 32 bytes, BT report 0x11
                            // = 78 bytes with CRC32 trailer). Lazily synth
                            // the per-shape buffer on first hit. When test
                            // rumble is active the rumble bytes vary per
                            // device, so we rebuild on every iteration —
                            // negligible cost vs. the 500 ms test window.
                            byte[] packet;
                            if (isBluetooth)
                            {
                                if (ds4BtBuf == null) ds4BtBuf = new byte[Ds4EffectSynthesizer.BluetoothPacketSize];
                                int n = Ds4EffectSynthesizer.BuildBluetooth(
                                    _config, ds4BtBuf, peakForSynth, nowMs,
                                    _randomColor, _pulseColor, pulseIntensity,
                                    rR, rL);
                                if (n <= 0) { errors++; continue; }
                                packet = ds4BtBuf;
                            }
                            else
                            {
                                if (ds4UsbBuf == null) ds4UsbBuf = new byte[Ds4EffectSynthesizer.UsbPacketSize];
                                int n = Ds4EffectSynthesizer.BuildUsb(
                                    _config, ds4UsbBuf, peakForSynth, nowMs,
                                    _randomColor, _pulseColor, pulseIntensity,
                                    rR, rL);
                                if (n <= 0) { errors++; continue; }
                                packet = ds4UsbBuf;
                            }
                            ok = Ds5RawHidWriter.WriteFullPacket(path, packet);
                            DiagLog($"  raw-write ds4 bt={isBluetooth} ok={ok} testTarget={testTarget != Guid.Empty} deliverRumble={deliverRumble} diag='{Ds5RawHidWriter.LastWriteDiag}'");
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
