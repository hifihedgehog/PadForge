using System;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// Samples the live <see cref="UserDevice.InputState"/>'s gyro readings
    /// while the user holds the controller still, averages each axis, and
    /// writes the result back as the device's at-rest bias.
    /// <see cref="PadForge.Engine.Common.Mapping.SourceCoercion"/>'s gyro
    /// reader subtracts the bias inline so mappings don't drift the mouse
    /// or stick when the controller is stationary.
    ///
    /// <para>Thread model: sampling runs on a worker task, polling
    /// <c>ud.InputState.Gyro[]</c> at ~5 ms intervals. The state object
    /// is mutated by the InputManager polling thread on every SDL update;
    /// reads are non-atomic on float arrays but tearing is acceptable
    /// here (the average across hundreds of samples washes out any
    /// half-written transient).</para>
    /// </summary>
    public sealed class GyroCalibratorService
    {
        private readonly Action _persistCallback;

        /// <param name="persistCallback">Called on completion to ask
        /// SettingsService to write UserDevices back to disk.</param>
        public GyroCalibratorService(Action persistCallback = null)
        {
            _persistCallback = persistCallback;
        }

        /// <summary>Auto-runs the 1500 ms calibration the first time a
        /// gyro-capable device is seen (GyroCalibratedAtUtc == default).
        /// No-op for already-calibrated devices.</summary>
        public Task EnsureAutoCalibratedAsync(UserDevice ud)
        {
            if (ud == null) return Task.CompletedTask;
            if (!ud.HasGyro) return Task.CompletedTask;
            if (ud.GyroCalibratedAtUtc != default) return Task.CompletedTask;
            return RecalibrateAsync(ud, 1500);
        }

        /// <summary>Zeroes the gyro bias fields and clears the
        /// <c>GyroCalibratedAtUtc</c> timestamp on <paramref name="ud"/>,
        /// reverting the device to its uncalibrated state. The next
        /// <see cref="EnsureAutoCalibratedAsync"/> pass (fired by
        /// InputService whenever it sees a gyro device with
        /// <c>GyroCalibratedAtUtc == default</c>) will re-run the
        /// 1500 ms at-rest sample on the next polling tick. Triggers
        /// the persist callback so the cleared state hits PadForge.xml.</summary>
        public void ResetCalibration(UserDevice ud)
        {
            if (ud == null || !ud.HasGyro) return;
            ud.GyroBiasPitch = 0f;
            ud.GyroBiasYaw   = 0f;
            ud.GyroBiasRoll  = 0f;
            ud.GyroCalibratedAtUtc = default;
            _persistCallback?.Invoke();
        }

        /// <summary>Samples <paramref name="ud"/>'s gyro readings for
        /// <paramref name="durationMs"/>, averages each axis, and writes
        /// the result to the UserDevice's bias fields. Returns false if
        /// the device went offline mid-sample or has no gyro.</summary>
        public Task<bool> RecalibrateAsync(UserDevice ud, int durationMs = 1500, CancellationToken ct = default)
        {
            if (ud == null || !ud.HasGyro) return Task.FromResult(false);
            durationMs = Math.Clamp(durationMs, 250, 5000);
            return Task.Run(() => RunSampling(ud, durationMs, ct), ct);
        }

        private bool RunSampling(UserDevice ud, int durationMs, CancellationToken ct)
        {
            double accPitch = 0, accYaw = 0, accRoll = 0;
            int samples = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // ~5 ms cadence — fast enough to catch the polling thread's
            // updates without burning CPU. ~200 samples per 1500 ms is
            // ample for averaging out small noise.
            while (sw.ElapsedMilliseconds < durationMs)
            {
                if (ct.IsCancellationRequested) return false;
                var state = ud.InputState;
                if (state == null || !ud.IsOnline) return false;
                var gyro = state.Gyro;
                if (gyro != null && gyro.Length >= 3)
                {
                    accPitch += gyro[0];
                    accYaw   += gyro[1];
                    accRoll  += gyro[2];
                    samples++;
                }
                try { Thread.Sleep(5); }
                catch (ThreadInterruptedException) { return false; }
            }
            if (samples == 0) return false;

            ud.GyroBiasPitch = (float)(accPitch / samples);
            ud.GyroBiasYaw   = (float)(accYaw   / samples);
            ud.GyroBiasRoll  = (float)(accRoll  / samples);
            ud.GyroCalibratedAtUtc = DateTime.UtcNow;

            _persistCallback?.Invoke();
            return true;
        }
    }
}
