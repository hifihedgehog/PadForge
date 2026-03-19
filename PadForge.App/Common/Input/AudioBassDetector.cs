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

        // 8th-order cascaded single-pole IIR low-pass filter (48dB/octave).
        // Each stage adds 6dB/octave rolloff for a near-brick-wall response.
        private const int FilterOrder = 8;
        private readonly float[] _filterStates = new float[FilterOrder];
        private float _alpha;

        // Envelope follower output — the bass energy value (0.0–1.0).
        private volatile float _bassEnergy;
        private long _lastCallbackTick;

        // User-configurable parameters.
        private float _sensitivity = 4f;
        private float _cutoffHz = 80f;
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

        /// <summary>Motor value as ushort (0–65535).</summary>
        public ushort MotorValue => (ushort)(_bassEnergy * 65535f);

        /// <summary>Sensitivity multiplier (1.0–20.0). Default 4.0.</summary>
        public float Sensitivity
        {
            get => _sensitivity;
            set => _sensitivity = Math.Clamp(value, 1f, 20f);
        }

        /// <summary>Low-pass filter cutoff in Hz (40–200). Default 80.</summary>
        public float CutoffHz
        {
            get => _cutoffHz;
            set
            {
                _cutoffHz = Math.Clamp(value, 20f, 200f);
                // Alpha is recalculated on next DataAvailable if sample rate is known.
            }
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

        public void Stop()
        {
            _running = false;
            StopCapture();

            if (_enumerator != null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                _enumerator = null;
            }

            _bassEnergy = 0f;
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

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                // Start on a thread pool thread to avoid SynchronizationContext capture
                // which would force callbacks onto the UI thread.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { _capture?.StartRecording(); } catch { }
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

            // Recalculate alpha if cutoff changed.
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

            // Copy filter states to locals for the hot loop.
            Span<float> fs = stackalloc float[FilterOrder];
            for (int s = 0; s < FilterOrder; s++)
                fs[s] = _filterStates[s];

            float sumSq = 0f;

            for (int i = 0; i < floatSpan.Length; i += channels)
            {
                // Mix to mono (average channels).
                float sample = 0f;
                for (int ch = 0; ch < channels && (i + ch) < floatSpan.Length; ch++)
                    sample += floatSpan[i + ch];
                sample /= channels;

                // 8th-order cascaded single-pole IIR low-pass (48dB/octave).
                fs[0] += currentAlpha * (sample - fs[0]);
                for (int s = 1; s < FilterOrder; s++)
                    fs[s] += currentAlpha * (fs[s - 1] - fs[s]);

                sumSq += fs[FilterOrder - 1] * fs[FilterOrder - 1];
            }

            // Write filter states back.
            for (int s = 0; s < FilterOrder; s++)
                _filterStates[s] = fs[s];

            // RMS of filtered samples.
            float rms = MathF.Sqrt(sumSq / frameCount);

            // Scale by sensitivity, clamp to [0, 1].
            float scaled = Math.Clamp(rms * _sensitivity, 0f, 1f);

            // Envelope follower: fast attack, slow decay.
            float current = _bassEnergy;
            float coeff = scaled > current ? AttackCoeff : DecayCoeff;
            float smoothed = current + coeff * (scaled - current);
            _bassEnergy = smoothed;

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
                    if (_running)
                    {
                        StopCapture();
                        StartCapture();
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
                if (_running)
                {
                    StopCapture();
                    Array.Clear(_filterStates);
                    _bassEnergy = 0f;
                    StartCapture();
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
            return new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }
    }
}
