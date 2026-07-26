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
    /// <para>At-rest is VERIFIED, not assumed (rounds six and seven):
    /// each sampled axis's peak-to-peak range must stay under
    /// <see cref="MotionRangeLimit"/> AND its average magnitude under
    /// <see cref="MaxPlausibleBias"/>, or the run writes nothing and
    /// returns false. The range test catches varying motion, the
    /// magnitude test catches steady rotation and frozen mid-motion
    /// streams, and neither alone is a gate. The auto-calibration
    /// trigger is device-connect, which is exactly when the pad is
    /// plausibly in the user's hands, and a bias averaged from motion
    /// made every gyro row read a large constant rate at rest. Writes
    /// are also guarded optimistically against a reset or concurrent
    /// calibration finishing mid-run (round eight, R1).</para>
    ///
    /// <para>Thread model: sampling runs on a worker task, polling
    /// <c>ud.InputState.Gyro[]</c> at ~5 ms intervals. The state object
    /// is mutated by the InputManager polling thread on every SDL update;
    /// reads are non-atomic on float arrays but tearing is acceptable
    /// here (the average across hundreds of samples washes out any
    /// half-written transient, and a torn value large enough to matter
    /// trips the motion gate instead of landing in the bias).</para>
    /// </summary>
    public sealed class GyroCalibratorService
    {
        /// <summary>Maximum per-axis peak-to-peak gyro range (rad/s) a
        /// sampling run may see and still count as at-rest. At-rest
        /// sensor noise plus hand tremor stays an order of magnitude
        /// below this ~20°/s bound; deliberately moving the pad is an
        /// order of magnitude above it.</summary>
        internal const float MotionRangeLimit = 0.35f;

        /// <summary>Largest average magnitude (rad/s) a measured bias may
        /// carry per axis (round seven, R2; tightened round eight). Genuine
        /// at-rest drift sits near 0.02 rad/s and pathological units reach
        /// perhaps 0.05, so 0.15 keeps a 3x margin over the worst real
        /// bias. The peak-to-peak gate alone cannot reject a pad rotating
        /// at a STEADY rate, or a state stream frozen on a mid-motion
        /// sample, because constant values have zero range; the original
        /// 0.5 bound still let a slow ~23 deg/s pan calibrate itself into
        /// the bias.</summary>
        internal const float MaxPlausibleBias = 0.15f;

        private readonly Action _persistCallback;

        /// <param name="persistCallback">Called on completion to ask
        /// SettingsService to write PadSettings back to disk.</param>
        public GyroCalibratorService(Action persistCallback = null)
        {
            _persistCallback = persistCallback;
        }

        /// <summary>Whether <see cref="EnsureAutoCalibratedAsync"/> would
        /// start a sampling pass for this (device, profile) pair right
        /// now: gyro-capable, and either never calibrated (no timestamp)
        /// or aux-capable with the aux triple still at the field default
        /// (the #252 upgrade). Pure and cheap; the caller consults it
        /// BEFORE burning its one-shot per-session latch, so a pair with
        /// nothing to do never consumes the latch (round six, R1: the old
        /// order latched every considered pair, and a later profile
        /// switch bringing an uncalibrated PadSetting to the same
        /// (device, slot) could not auto-calibrate until restart).</summary>
        public static bool WouldCalibrate(UserDevice ud, PadSetting ps)
        {
            if (ud == null || ps == null) return false;
            if (!ud.HasGyro) return false;
            if (string.IsNullOrEmpty(ps.GyroCalibratedAtUtc)) return true;
            bool auxUnset = ps.GyroAuxBiasPitch == "0"
                && ps.GyroAuxBiasYaw == "0"
                && ps.GyroAuxBiasRoll == "0";
            return ud.HasGyroAux && auxUnset;
        }

        /// <summary>Auto-runs the 1500 ms calibration the first time a
        /// (device, slot) is seen with no calibration timestamp on its
        /// <see cref="PadSetting"/>, plus the #252 upgrade for a stamped
        /// profile whose aux triple was never measured. The upgrade
        /// samples the AUX TRIPLE ALONE: the stored primary bias is the
        /// user's real calibration and an unattended connect-time pass
        /// has no business rewriting it (round six, R1: the full-fat
        /// call here re-sampled the primary with the pad plausibly in
        /// hand, and one moving run poisoned it permanently because a
        /// non-zero aux measurement retires the branch). Returns false
        /// without sampling when there is nothing to do. Otherwise
        /// returns the sampling result, where false means the run wrote
        /// nothing (offline mid-run, or the motion gate rejected it)
        /// and the caller may retry.</summary>
        public Task<bool> EnsureAutoCalibratedAsync(UserDevice ud, PadSetting ps)
        {
            if (!WouldCalibrate(ud, ps)) return Task.FromResult(false);
            bool auxOnly = !string.IsNullOrEmpty(ps.GyroCalibratedAtUtc);
            return RecalibrateAsync(ud, ps, 1500, auxOnly: auxOnly);
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
        /// false, writing nothing, if the device went offline mid-sample,
        /// has no gyro, the motion gates rejected the run (peak-to-peak
        /// range over <see cref="MotionRangeLimit"/>, or average over
        /// <see cref="MaxPlausibleBias"/>), or the profile's calibration
        /// state changed underneath the run (reset / concurrent write).
        /// <paramref name="auxOnly"/> (the #252 upgrade) writes the aux
        /// triple alone and leaves the primary bias and the calibration
        /// timestamp untouched.</summary>
        public Task<bool> RecalibrateAsync(UserDevice ud, PadSetting ps, int durationMs = 1500,
            CancellationToken ct = default, bool auxOnly = false)
        {
            if (ud == null || ps == null || !ud.HasGyro) return Task.FromResult(false);
            durationMs = Math.Clamp(durationMs, 250, 5000);
            // ONE sampler per profile at a time (round nine, R5). The
            // write-guard is a last line of defense, not a concurrency
            // policy: two runs against one PadSetting could tear the aux
            // triple (the aux-only lane never writes the timestamp the
            // guard reads), and Reset-then-auto-fire genuinely produced a
            // second live sampler on the same object. Refusing here kills
            // manual-vs-manual, manual-vs-auto, and auto-vs-auto at the
            // source. A refusal reports false, which the auto caller
            // treats as "work remains" and retries.
            lock (_inFlightLock)
            {
                if (!_inFlight.Add(ps)) return Task.FromResult(false);
            }
            return Task.Run(() =>
            {
                try { return RunSampling(ud, ps, durationMs, ct, auxOnly); }
                finally { lock (_inFlightLock) _inFlight.Remove(ps); }
            }, ct);
        }

        // Reference-identity set: one entry per PadSetting with a live
        // sampling pass. Profiles are cloned on load, so two UserSettings
        // never share one PadSetting and this cannot over-serialize
        // unrelated slots.
        private static readonly HashSet<PadSetting> _inFlight =
            new(ReferenceEqualityComparer.Instance);
        private static readonly object _inFlightLock = new();

        private bool RunSampling(UserDevice ud, PadSetting ps, int durationMs, CancellationToken ct, bool auxOnly)
        {
            // Optimistic write-guard (round eight R1, widened round nine
            // R4): snapshot the WHOLE calibration state at entry and
            // refuse to write when any of it moved underneath the run.
            // Nothing cancels a sampler, so without this a user's Reset
            // during the 1.5 s window was silently REVERTED to disk a
            // second later. Round eight guarded the timestamp ALONE,
            // which is one field of a seven-field non-atomic
            // transaction: ResetCalibration writes six bias strings and
            // THEN the stamp, and a clipboard paste (PadSetting.CopyFrom,
            // reflection, stamp last) leaves a microsecond-scale window,
            // so a guard read landing mid-transaction saw the old stamp,
            // passed, and produced a hybrid (measured bias, cleared
            // stamp). Comparing every field closes both orderings.
            // First intervening writer wins; this run reports false and
            // the caller may retry.
            var entry = Snapshot(ps);
            double accPitch = 0, accYaw = 0, accRoll = 0;
            int samples = 0;
            // The aux gyro (#252) is sampled in the SAME at-rest pass: the
            // user is already holding both halves of a Joy-Con pair still,
            // and the left half's drift is its own number. Gated on
            // ud.HasGyroAux (audit 2026-07-25, C24): CustomInputState
            // always allocates the array, so the old null check was dead
            // and every calibration of ANY gyro device overwrote the
            // stored aux triple with zeros.
            double accAuxPitch = 0, accAuxYaw = 0, accAuxRoll = 0;
            int auxSamples = 0;
            // Per-axis peak-to-peak extremes for the motion gate:
            // [0..2] primary, [3..5] aux.
            var lo = new float[6];
            var hi = new float[6];
            for (int i = 0; i < 6; i++) { lo[i] = float.MaxValue; hi[i] = float.MinValue; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // ~5 ms cadence: fast enough to catch the polling thread's
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
                    for (int i = 0; i < 3; i++)
                    {
                        float v = gyro[i];
                        if (v < lo[i]) lo[i] = v;
                        if (v > hi[i]) hi[i] = v;
                    }
                    accPitch += gyro[0];
                    accYaw   += gyro[1];
                    accRoll  += gyro[2];
                    samples++;
                }
                var gyroAux = state.GyroAux;
                if (ud.HasGyroAux && gyroAux != null && gyroAux.Length >= 3)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float v = gyroAux[i];
                        if (v < lo[3 + i]) lo[3 + i] = v;
                        if (v > hi[3 + i]) hi[3 + i] = v;
                    }
                    accAuxPitch += gyroAux[0];
                    accAuxYaw   += gyroAux[1];
                    accAuxRoll  += gyroAux[2];
                    auxSamples++;
                }
                try { Thread.Sleep(5); }
                catch (ThreadInterruptedException) { return false; }
            }
            bool primaryStill = samples > 0 && HeldStill(lo, hi, 0)
                && PlausibleAverage(accPitch, accYaw, accRoll, samples);
            bool auxStill = auxSamples > 0 && HeldStill(lo, hi, 3)
                && PlausibleAverage(accAuxPitch, accAuxYaw, accAuxRoll, auxSamples);

            // The write-guard proper: any change to the calibration state
            // means a reset, a paste, or another pass landed during this
            // run. Its result is the newer truth and this run must not
            // clobber it.
            if (!SameSnapshot(entry, Snapshot(ps)))
                return false;

            if (auxOnly)
            {
                // #252 upgrade: write ONLY the never-measured aux triple,
                // and only from a run where the left half genuinely held
                // still. The primary bias and the timestamp are the
                // user's existing calibration and stay untouched. A
                // rejected run returns false so the caller can retry; a
                // legitimately-zero measurement writes "0" and re-runs at
                // next launch, which is now genuinely harmless because
                // this branch cannot reach the primary.
                if (!auxStill) return false;
                WriteAuxTriple(ps, accAuxPitch, accAuxYaw, accAuxRoll, auxSamples);
                _persistCallback?.Invoke();
                return true;
            }

            // Full calibration: a moving pad writes nothing (round six,
            // R1). A still primary with a moving aux is possible (the
            // halves are separate hands), so the primary is written and
            // the aux triple stays at its default for the upgrade branch
            // to retry.
            if (!primaryStill) return false;
            ps.GyroBiasPitch = AvgStr(accPitch, samples);
            ps.GyroBiasYaw   = AvgStr(accYaw, samples);
            ps.GyroBiasRoll  = AvgStr(accRoll, samples);
            if (auxStill)
                WriteAuxTriple(ps, accAuxPitch, accAuxYaw, accAuxRoll, auxSamples);
            ps.GyroCalibratedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            _persistCallback?.Invoke();
            return true;
        }

        private static bool HeldStill(float[] lo, float[] hi, int offset)
        {
            for (int i = 0; i < 3; i++)
                if (hi[offset + i] - lo[offset + i] > MotionRangeLimit) return false;
            return true;
        }

        /// <summary>The whole calibration state of a profile, for the
        /// write-guard's before / after comparison.</summary>
        private static string[] Snapshot(PadSetting ps) => new[]
        {
            ps.GyroBiasPitch, ps.GyroBiasYaw, ps.GyroBiasRoll,
            ps.GyroAuxBiasPitch, ps.GyroAuxBiasYaw, ps.GyroAuxBiasRoll,
            ps.GyroCalibratedAtUtc,
        };

        private static bool SameSnapshot(string[] a, string[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool PlausibleAverage(double accA, double accB, double accC, int n)
            => Math.Abs(accA / n) <= MaxPlausibleBias
            && Math.Abs(accB / n) <= MaxPlausibleBias
            && Math.Abs(accC / n) <= MaxPlausibleBias;

        private static string AvgStr(double acc, int n)
            => ((float)(acc / n)).ToString("R", CultureInfo.InvariantCulture);

        private static void WriteAuxTriple(PadSetting ps, double p, double y, double r, int n)
        {
            ps.GyroAuxBiasPitch = AvgStr(p, n);
            ps.GyroAuxBiasYaw   = AvgStr(y, n);
            ps.GyroAuxBiasRoll  = AvgStr(r, n);
        }
    }
}
