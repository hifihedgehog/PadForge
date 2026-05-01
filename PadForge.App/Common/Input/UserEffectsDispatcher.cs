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

        // Audio-to-lightbar polling cadence — 30Hz is enough to feel
        // responsive without flooding the BT HID write path. WriteFile
        // open+close is ~1ms per call; 30Hz = 30ms budget.
        private const int AudioTickMs = 33;

        private readonly int _padIndex;
        private PlayStationSlotConfig _config;
        private System.Threading.Timer _audioTimer;
        private bool _audioTickActive;
        private bool _disposed;

        public UserEffectsDispatcher(int padIndex, PlayStationSlotConfig config)
        {
            _padIndex = padIndex;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            UpdateAudioTimer();
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
            UpdateAudioTimer();
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
            StopAudioTimer();
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = null;
        }

        private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            DiagLog($"OnConfigChanged property={e.PropertyName}");
            // The audio-lightbar toggle / sensitivity changes need to
            // start/stop the periodic timer.
            if (e.PropertyName == nameof(PlayStationSlotConfig.AudioLightbarEnabled))
                UpdateAudioTimer();
            // Every PlayStationSlotConfig field change re-applies the
            // full message. Synthesis is cheap; the alternative would be
            // a per-field write that misses subtle interactions between
            // enable_bits flags. Last-writer-wins matches the game-driven
            // path's semantics.
            DispatchSnapshot();
        }

        // ────────────────────────────────────────────────
        //  Audio-to-lightbar timer
        // ────────────────────────────────────────────────
        // Started while AudioLightbarEnabled is on, stopped otherwise.
        // Each tick reads AudioPeakProvider() and re-dispatches if the
        // peak changed enough to be worth a write — avoids flooding the
        // HID pipe with no-op packets when the audio signal is steady.

        private void UpdateAudioTimer()
        {
            bool wantTimer = !_disposed
                && _config != null
                && _config.AudioLightbarEnabled;

            if (wantTimer && !_audioTickActive)
            {
                _audioTickActive = true;
                _audioTimer = new System.Threading.Timer(
                    OnAudioTick, null, AudioTickMs, AudioTickMs);
                DiagLog("audio timer started");
            }
            else if (!wantTimer && _audioTickActive)
            {
                StopAudioTimer();
            }
        }

        private void StopAudioTimer()
        {
            _audioTickActive = false;
            try { _audioTimer?.Dispose(); } catch { }
            _audioTimer = null;
            _lastDispatchedPeak = -1f;
        }

        private float _lastDispatchedPeak = -1f;

        private void OnAudioTick(object _)
        {
            if (_disposed || _config == null || !_config.AudioLightbarEnabled) return;

            float rawPeak = AudioPeakProvider?.Invoke() ?? 0f;
            float scaled = Math.Clamp(rawPeak * (float)_config.AudioLightbarSensitivity, 0f, 1f);

            // Skip dispatch if peak hasn't changed by at least one
            // perceptible step (1/255 ≈ 0.004). Bypass the skip when
            // the value crosses zero — going dark needs to apply
            // immediately even from a small change.
            float delta = MathF.Abs(scaled - _lastDispatchedPeak);
            bool zeroCrossing =
                (scaled == 0f && _lastDispatchedPeak > 0f)
                || (_lastDispatchedPeak == 0f && scaled > 0f);
            if (!zeroCrossing && delta < 0.004f) return;

            _lastDispatchedPeak = scaled;
            DispatchSnapshot(scaled);
        }

        private void DispatchSnapshot(float audioPeak = -1f)
        {
            if (_config == null) { DiagLog("DispatchSnapshot config=null"); return; }

            // For non-audio-driven dispatches (slider drag, OnDevicesUpdated
            // re-apply, etc.), pull the current peak so the audio path
            // doesn't snap to black between timer ticks. The synthesizer
            // ignores the peak when AudioLightbarEnabled is false.
            float peakForSynth = audioPeak >= 0f
                ? audioPeak
                : Math.Clamp(
                    (AudioPeakProvider?.Invoke() ?? 0f)
                    * (float)_config.AudioLightbarSensitivity,
                    0f, 1f);

            // Synthesize once per dispatch; reuse the buffer across the
            // multi-DS5 fan-out below.
            var buffer = new byte[Ds5EffectSynthesizer.PayloadSize];
            int len = Ds5EffectSynthesizer.Build(_config, buffer, peakForSynth);
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
