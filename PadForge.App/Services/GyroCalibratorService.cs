using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.Data;

namespace PadForge.Services
{
    /// <summary>
    /// Samples the live <see cref="UserDevice.InputState"/>'s gyro readings
    /// while the user holds the controller still, averages each axis, and
    /// writes the result back as the (device, slot)'s at-rest bias on the
    /// associated <see cref="PadSetting"/>.
    /// <see cref="PadForge.Engine.Common.Mapping.SourceCoercion"/>'s gyro
    /// reader subtracts the bias inline so mappings don't drift the mouse
    /// or stick when the controller is stationary.
    ///
    /// <para>Per-(device, slot): the same physical pad in slot 0 and
    /// slot 1 gets two independent bias entries on two independent
    /// <c>PadSetting</c>s, so re-calibrating one slot does not disturb
    /// the other.</para>
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
        /// SettingsService to write PadSettings back to disk.</param>
        public GyroCalibratorService(Action persistCallback = null)
        {
            _persistCallback = persistCallback;
        }

        /// <summary>Auto-runs the 1500 ms calibration the first time a
        /// (device, slot) is seen with no calibration timestamp on its
        /// <see cref="PadSetting"/>. No-op if either argument is null or
        /// the device lacks a gyro.</summary>
        public Task EnsureAutoCalibratedAsync(UserDevice ud, PadSetting ps)
        {
            if (ud == null || ps == null) return Task.CompletedTask;
            if (!ud.HasGyro) return Task.CompletedTask;
            if (!string.IsNullOrEmpty(ps.GyroCalibratedAtUtc)) return Task.CompletedTask;
            return RecalibrateAsync(ud, ps, 1500);
        }

        /// <summary>Zeroes the gyro bias fields and clears the
        /// calibration timestamp on the given <see cref="PadSetting"/>,
        /// reverting the (device, slot) pair to its uncalibrated state.
        /// The next <see cref="EnsureAutoCalibratedAsync"/> pass will
        /// re-run the 1500 ms at-rest sample for that slot. Triggers the
        /// persist callback so the cleared state hits PadForge.xml.</summary>
        public void ResetCalibration(PadSetting ps)
        {
            if (ps == null) return;
            ps.GyroBiasPitch = "0";
            ps.GyroBiasYaw   = "0";
            ps.GyroBiasRoll  = "0";
            ps.GyroAuxBiasPitch = "0";
            ps.GyroAuxBiasYaw   = "0";
            ps.GyroAuxBiasRoll  = "0";
            ps.GyroCalibratedAtUtc = "";
            _persistCallback?.Invoke();
        }

        /// <summary>Samples <paramref name="ud"/>'s gyro readings for
        /// <paramref name="durationMs"/>, averages each axis, and writes
        /// the result to <paramref name="ps"/>'s bias fields. Returns
        /// false if the device went offline mid-sample or has no gyro.</summary>
        public Task<bool> RecalibrateAsync(UserDevice ud, PadSetting ps, int durationMs = 1500, CancellationToken ct = default)
        {
            if (ud == null || ps == null || !ud.HasGyro) return Task.FromResult(false);
            durationMs = Math.Clamp(durationMs, 250, 5000);
            return Task.Run(() => RunSampling(ud, ps, durationMs, ct), ct);
        }

        private bool RunSampling(UserDevice ud, PadSetting ps, int durationMs, CancellationToken ct)
        {
            double accPitch = 0, accYaw = 0, accRoll = 0;
            int samples = 0;
            // The aux gyro (#252) is sampled in the SAME at-rest pass: the
            // user is already holding both halves of a Joy-Con pair still,
            // and the left half's drift is its own number. Counted
            // separately so a device without the sensor leaves its stored
            // triple untouched rather than writing zeros over it.
            double accAuxPitch = 0, accAuxYaw = 0, accAuxRoll = 0;
            int auxSamples = 0;
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
                var gyroAux = state.GyroAux;
                if (gyroAux != null && gyroAux.Length >= 3)
                {
                    accAuxPitch += gyroAux[0];
                    accAuxYaw   += gyroAux[1];
                    accAuxRoll  += gyroAux[2];
                    auxSamples++;
                }
                try { Thread.Sleep(5); }
                catch (ThreadInterruptedException) { return false; }
            }
            if (samples == 0) return false;

            ps.GyroBiasPitch = ((float)(accPitch / samples)).ToString("R", CultureInfo.InvariantCulture);
            ps.GyroBiasYaw   = ((float)(accYaw   / samples)).ToString("R", CultureInfo.InvariantCulture);
            ps.GyroBiasRoll  = ((float)(accRoll  / samples)).ToString("R", CultureInfo.InvariantCulture);
            if (auxSamples > 0)
            {
                ps.GyroAuxBiasPitch = ((float)(accAuxPitch / auxSamples)).ToString("R", CultureInfo.InvariantCulture);
                ps.GyroAuxBiasYaw   = ((float)(accAuxYaw   / auxSamples)).ToString("R", CultureInfo.InvariantCulture);
                ps.GyroAuxBiasRoll  = ((float)(accAuxRoll  / auxSamples)).ToString("R", CultureInfo.InvariantCulture);
            }
            ps.GyroCalibratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            _persistCallback?.Invoke();
            return true;
        }
    }
}
