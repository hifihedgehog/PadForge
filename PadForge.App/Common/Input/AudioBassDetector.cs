using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Captures system audio via WASAPI loopback and extracts bass frequency
    /// energy to drive controller rumble motors.
    /// </summary>
    public sealed class AudioBassDetector : IDisposable, IMMNotificationClient
    {
        private WasapiCapture _capture;
        private MMDeviceEnumerator _enumerator;

        // Serializes every _capture lifecycle transition. Three parties race
        // on the field otherwise: Stop() (UI), OnRecordingStopped's 500 ms
        // delayed restart worker, and OnDefaultDeviceChanged's 200 ms worker
        // (a device switch typically fires both ~300 ms apart). An
        // interleaved Stop/Start pair can orphan a live, subscribed capture
        // that keeps feeding the energy fields until process exit.
        private readonly object _restartGate = new();

        // 8th-order cascaded single-pole IIR low-pass filter (48dB/octave).
        // Each stage adds 6dB/octave rolloff for a near-brick-wall response.
        private const int FilterOrder = 8;
        private readonly float[] _filterStates = new float[FilterOrder];
        private float _alpha;

        // Parallel filter chain for the trigger-motor audio rumble path,
        // with its own sensitivity + cutoff so the Impulse Triggers tab
        // is independent of Force Feedback's Audio Bass Rumble settings.
        private readonly float[] _triggerFilterStates = new float[FilterOrder];
        private float _triggerAlpha;

        // Envelope follower output — the bass energy value (0.0–1.0).
        private volatile float _bassEnergy;
        private volatile float _triggerBassEnergy;
        // Pre-filter full-spectrum peak (0.0–1.0). Computed in the same
        // OnDataAvailable pass as the bass-filtered RMS, but BEFORE the
        // 8th-order IIR low-pass touches the samples — so audio-to-LED
        // sees the full audio waveform regardless of what bass-cutoff
        // the user has set for the rumble feature. Sampled per-buffer.
        private volatile float _fullSpectrumPeak;
        private long _lastCallbackTick;

        // User-configurable parameters.
        private float _sensitivity = 4f;
        private float _cutoffHz = 80f;
        private float _triggerSensitivity = 4f;
        private float _triggerCutoffHz = 80f;
        private float _leftMotorScale = 1f;
        private float _rightMotorScale = 0.5f;

        private bool _running;
        private bool _disposed;

        // Attack/decay coefficients for the envelope follower.
        // Near-instant attack for responsive bass hits; moderate decay for smooth fade-out.
        private const float AttackCoeff = 0.9f;
        private const float DecayCoeff = 0.15f;

        /// <summary>Current bass energy (0.0–1.0). Lockless read from polling thread.</summary>
        public float BassEnergy => _bassEnergy;

        /// <summary>Full-spectrum audio peak (0.0–1.0) — pre-filter, so
        /// not affected by the bass-cutoff setting. Drives audio-to-LED
        /// for the lightbar. Lockless read.</summary>
        public float FullSpectrumPeak => _fullSpectrumPeak;

        /// <summary>Motor value as ushort (0–65535).</summary>
        public ushort MotorValue => (ushort)(_bassEnergy * 65535f);

        /// <summary>Trigger-path bass energy (0.0–1.0). Independent of the
        /// main-motor path; uses <see cref="TriggerSensitivity"/> and
        /// <see cref="TriggerCutoffHz"/>.</summary>
        public float TriggerBassEnergy => _triggerBassEnergy;

        /// <summary>Trigger-path motor value as ushort (0–65535).</summary>
        public ushort TriggerMotorValue => (ushort)(_triggerBassEnergy * 65535f);

        /// <summary>Sensitivity multiplier (1.0–20.0). Default 4.0.</summary>
        public float Sensitivity
        {
            get => _sensitivity;
            set => _sensitivity = Math.Clamp(value, 1f, 20f);
        }

        /// <summary>Low-pass filter cutoff in Hz (20–200). Default 80.</summary>
        public float CutoffHz
        {
            get => _cutoffHz;
            set
            {
                _cutoffHz = Math.Clamp(value, 20f, 200f);
                // Alpha is recalculated on next DataAvailable if sample rate is known.
            }
        }

        /// <summary>Sensitivity multiplier for the trigger-path bass
        /// detector (1.0–20.0). Default 4.0.</summary>
        public float TriggerSensitivity
        {
            get => _triggerSensitivity;
            set => _triggerSensitivity = Math.Clamp(value, 1f, 20f);
        }

        /// <summary>Low-pass filter cutoff for the trigger-path bass
        /// detector, in Hz (20–200). Default 80.</summary>
        public float TriggerCutoffHz
        {
            get => _triggerCutoffHz;
            set => _triggerCutoffHz = Math.Clamp(value, 20f, 200f);
        }

        /// <summary>Left motor scale (0.0–1.0). Default 1.0.</summary>
        public float LeftMotorScale
        {
            get => _leftMotorScale;
            set => _leftMotorScale = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>Right motor scale (0.0–1.0). Default 0.5.</summary>
        public float RightMotorScale
        {
            get => _rightMotorScale;
            set => _rightMotorScale = Math.Clamp(value, 0f, 1f);
        }

        public bool Start()
        {
            if (_running) return true;

            lock (_restartGate)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(this);

                    if (!StartCapture())
                        return false;

                    _running = true;
                    return true;
                }
                catch
                {
                    Stop();
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_restartGate)
            {
                _running = false;
                StopCapture();

                if (_enumerator != null)
                {
                    try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                    _enumerator = null;
                }

                _bassEnergy = 0f;
                _triggerBassEnergy = 0f;
                _fullSpectrumPeak = 0f;
            }
        }

        /// <summary>
        /// Decay bass energy when WASAPI stops delivering buffers (silence).
        /// Call from the polling thread once per frame.
        /// </summary>
        public void DecayIfSilent()
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastCallbackTick) > 50)
            {
                float current = _bassEnergy;
                if (current > 0.001f)
                    _bassEnergy = current * 0.95f;
                else
                    _bassEnergy = 0f;

                float triggerCurrent = _triggerBassEnergy;
                if (triggerCurrent > 0.001f)
                    _triggerBassEnergy = triggerCurrent * 0.95f;
                else
                    _triggerBassEnergy = 0f;

                float currentPeak = _fullSpectrumPeak;
                if (currentPeak > 0.001f)
                    _fullSpectrumPeak = currentPeak * 0.95f;
                else
                    _fullSpectrumPeak = 0f;
            }
        }

        // ─── Capture lifecycle ───

        private bool StartCapture()
        {
            try
            {
                _capture = new FastLoopbackCapture();
                int sampleRate = _capture.WaveFormat.SampleRate;
                RecalcAlpha(sampleRate);
                Array.Clear(_filterStates);
                Array.Clear(_triggerFilterStates);

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                // Start on a thread pool thread to avoid SynchronizationContext capture
                // which would force callbacks onto the UI thread. The lambda starts
                // the instance it was queued for, never the field: a restart between
                // queue and run must not start the replacement capture twice.
                var cap = _capture;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { cap.StartRecording(); } catch { }
                });

                return true;
            }
            catch
            {
                StopCapture();
                return false;
            }
        }

        private void StopCapture()
        {
            if (_capture != null)
            {
                try { _capture.StopRecording(); } catch { }
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                try { _capture.Dispose(); } catch { }
                _capture = null;
            }
        }

        private void RecalcAlpha(int sampleRate)
        {
            double twoPiCutoff = 2.0 * Math.PI * _cutoffHz;
            _alpha = (float)(twoPiCutoff / (twoPiCutoff + sampleRate));
            double twoPiTriggerCutoff = 2.0 * Math.PI * _triggerCutoffHz;
            _triggerAlpha = (float)(twoPiTriggerCutoff / (twoPiTriggerCutoff + sampleRate));
        }

        // ─── WASAPI callbacks ───

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;

            var floatSpan = MemoryMarshal.Cast<byte, float>(
                new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));

            int channels = _capture?.WaveFormat?.Channels ?? 2;
            int frameCount = floatSpan.Length / channels;
            if (frameCount == 0) return;

            // Recalculate alpha if cutoff changed (main + trigger paths).
            int sr = _capture?.WaveFormat?.SampleRate ?? 48000;
            float currentAlpha = _alpha;
            {
                double twoPiCutoff = 2.0 * Math.PI * _cutoffHz;
                float expectedAlpha = (float)(twoPiCutoff / (twoPiCutoff + sr));
                if (Math.Abs(expectedAlpha - currentAlpha) > 0.0001f)
                {
                    _alpha = expectedAlpha;
                    currentAlpha = expectedAlpha;
                }
            }
            float currentTriggerAlpha = _triggerAlpha;
            {
                double twoPiTriggerCutoff = 2.0 * Math.PI * _triggerCutoffHz;
                float expectedTriggerAlpha = (float)(twoPiTriggerCutoff / (twoPiTriggerCutoff + sr));
                if (Math.Abs(expectedTriggerAlpha - currentTriggerAlpha) > 0.0001f)
                {
                    _triggerAlpha = expectedTriggerAlpha;
                    currentTriggerAlpha = expectedTriggerAlpha;
                }
            }

            // Copy filter states to locals for the hot loop.
            Span<float> fs = stackalloc float[FilterOrder];
            Span<float> tfs = stackalloc float[FilterOrder];
            for (int s = 0; s < FilterOrder; s++)
            {
                fs[s] = _filterStates[s];
                tfs[s] = _triggerFilterStates[s];
            }

            float sumSq = 0f;
            float triggerSumSq = 0f;
            float fullPeak = 0f;

            for (int i = 0; i < floatSpan.Length; i += channels)
            {
                // Mix to mono (average channels).
                float sample = 0f;
                for (int ch = 0; ch < channels && (i + ch) < floatSpan.Length; ch++)
                    sample += floatSpan[i + ch];
                sample /= channels;

                // Tap the mono signal BEFORE the IIR low-pass — this is
                // the full-spectrum peak that drives audio-to-lightbar.
                // Splitting here means the lightbar sees the same waveform
                // the user actually hears, while the rumble path keeps
                // its bass-cutoff filter for low-end thump detection.
                float absSample = sample < 0f ? -sample : sample;
                if (absSample > fullPeak)
                    fullPeak = absSample;

                // 8th-order cascaded single-pole IIR low-pass (48dB/octave) — main.
                fs[0] += currentAlpha * (sample - fs[0]);
                for (int s = 1; s < FilterOrder; s++)
                    fs[s] += currentAlpha * (fs[s - 1] - fs[s]);

                sumSq += fs[FilterOrder - 1] * fs[FilterOrder - 1];

                // Parallel filter chain — trigger path, own cutoff.
                tfs[0] += currentTriggerAlpha * (sample - tfs[0]);
                for (int s = 1; s < FilterOrder; s++)
                    tfs[s] += currentTriggerAlpha * (tfs[s - 1] - tfs[s]);

                triggerSumSq += tfs[FilterOrder - 1] * tfs[FilterOrder - 1];
            }

            _fullSpectrumPeak = fullPeak;

            // Write filter states back (both chains).
            for (int s = 0; s < FilterOrder; s++)
            {
                _filterStates[s] = fs[s];
                _triggerFilterStates[s] = tfs[s];
            }

            // RMS of filtered samples.
            float rms = MathF.Sqrt(sumSq / frameCount);
            float triggerRms = MathF.Sqrt(triggerSumSq / frameCount);

            // Scale by sensitivity, clamp to [0, 1].
            float scaled = Math.Clamp(rms * _sensitivity, 0f, 1f);
            float triggerScaled = Math.Clamp(triggerRms * _triggerSensitivity, 0f, 1f);

            // Envelope follower: fast attack, slow decay (per chain).
            float current = _bassEnergy;
            float coeff = scaled > current ? AttackCoeff : DecayCoeff;
            _bassEnergy = current + coeff * (scaled - current);

            float triggerCurrent = _triggerBassEnergy;
            float triggerCoeff = triggerScaled > triggerCurrent ? AttackCoeff : DecayCoeff;
            _triggerBassEnergy = triggerCurrent + triggerCoeff * (triggerScaled - triggerCurrent);

            Interlocked.Exchange(ref _lastCallbackTick, Environment.TickCount64);
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            // If still running, recording stopped unexpectedly — try to restart.
            if (_running && e.Exception == null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(500);
                    lock (_restartGate)
                    {
                        if (_running)
                        {
                            StopCapture();
                            StartCapture();
                        }
                    }
                });
            }
        }

        // ─── IMMNotificationClient (device change) ───

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Only care about render device changes (output audio).
            if (flow != DataFlow.Render || role != Role.Multimedia)
                return;

            if (!_running) return;

            // Restart capture on the new default device.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(200); // Brief delay for device to settle.
                lock (_restartGate)
                {
                    if (_running)
                    {
                        StopCapture();
                        Array.Clear(_filterStates);
                        Array.Clear(_triggerFilterStates);
                        _bassEnergy = 0f;
                        _triggerBassEnergy = 0f;
                        StartCapture();
                    }
                }
            });
        }

        // Unused IMMNotificationClient members.
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

        // ─── IDisposable ───

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }

    /// <summary>
    /// WasapiLoopbackCapture with a reduced buffer size (10ms instead of the
    /// default 100ms) for low-latency audio-reactive rumble.
    /// </summary>
    internal class FastLoopbackCapture : WasapiCapture
    {
        public FastLoopbackCapture()
            : base(GetDefaultRenderDevice(), false, 1)
        {
            ShareMode = AudioClientShareMode.Shared;
        }

        private static MMDevice GetDefaultRenderDevice()
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }
    }
}
