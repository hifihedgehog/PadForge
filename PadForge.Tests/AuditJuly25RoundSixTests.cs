using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-25 round six. R1: the #252 aux upgrade must
    /// never rewrite the primary gyro calibration, an unattended sampling
    /// pass must reject a moving pad instead of averaging the motion into
    /// the bias, and the caller-side latch must burn only when a pass
    /// actually starts.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundSixTests : IDisposable
    {
        private static readonly Guid DevGuid = new("cdcdcdcd-1111-2222-3333-444444444444");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public AuditJuly25RoundSixTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        /// <summary>Flips a state value on a DEDICATED thread, never the
        /// pool: the sampler itself queues through Task.Run, and a
        /// starved pool can delay a pooled mutator past the whole
        /// sampling window, which reads as "the motion gate is broken"
        /// when the harness simply never moved the pad. The constructor
        /// blocks until the first write landed, and FlipCount lets the
        /// test prove the pad genuinely moved during the window.</summary>
        private sealed class GyroMutator : IDisposable
        {
            private readonly Thread _thread;
            private readonly ManualResetEventSlim _started = new(false);
            private volatile bool _stop;
            private int _flips;

            public int FlipCount => Volatile.Read(ref _flips);

            public GyroMutator(Action<bool> write)
            {
                _thread = new Thread(() =>
                {
                    bool flip = false;
                    while (!_stop)
                    {
                        write(flip);
                        flip = !flip;
                        Interlocked.Increment(ref _flips);
                        _started.Set();
                        Thread.Sleep(1);
                    }
                })
                {
                    IsBackground = true,
                    // The three tests using this assert the pad really did
                    // move during a 250 ms window (FlipCount > 10). At
                    // default priority this thread is starved on a loaded
                    // machine and the guard trips -- reproduced by running
                    // the suite with 26 busy loops on 32 cores. It stands
                    // in for the poll thread, which the product also runs
                    // AboveNormal, so match that.
                    Priority = ThreadPriority.AboveNormal,
                };
                _thread.Start();
                _started.Wait(5000);
            }

            public void Dispose()
            {
                _stop = true;
                _thread.Join(5000);
                _started.Dispose();
            }
        }

        private static CustomInputState ArrangeDevice(out UserDevice ud,
            bool aux = true, bool online = true)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Round Six Pad",
                IsOnline = online,
                HasGyro = true,
                HasGyroAux = aux,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return state;
        }

        // ── R1a: the upgrade writes ONLY the aux triple ──

        /// <summary>THE POISONING FIX. A stamped profile reaching the #252
        /// upgrade carries the user's real primary calibration; the
        /// unattended connect-time pass must fill in the aux triple and
        /// touch nothing else. Before round six the branch called the
        /// full-fat sampler, which rewrote the primary and restamped the
        /// timestamp, and one moving run poisoned the primary permanently
        /// because a non-zero aux measurement retires the branch.</summary>
        [Fact]
        public async Task AuxUpgrade_WritesOnlyTheAuxTriple()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[0] = 0.01f; state.Gyro[1] = 0.02f; state.Gyro[2] = 0.03f;
            // Realistic at-rest drift (round eight: the plausibility
            // bound is 0.15 rad/s, so the old arbitrary 0.2..0.4 values
            // read as steady MOTION and were rightly rejected).
            state.GyroAux[0] = 0.02f; state.GyroAux[1] = 0.03f; state.GyroAux[2] = 0.04f;
            var ps = new PadSetting
            {
                GyroCalibratedAtUtc = "2026-01-01T00:00:00Z",
                GyroBiasPitch = "0.5",
                GyroBiasYaw = "0.6",
                GyroBiasRoll = "0.7",
            };
            var svc = new GyroCalibratorService();

            bool ok = await svc.EnsureAutoCalibratedAsync(ud, ps);

            Assert.True(ok);
            Assert.Equal("0.5", ps.GyroBiasPitch);
            Assert.Equal("0.6", ps.GyroBiasYaw);
            Assert.Equal("0.7", ps.GyroBiasRoll);
            Assert.Equal("2026-01-01T00:00:00Z", ps.GyroCalibratedAtUtc);
            Assert.Equal(0.02f, float.Parse(ps.GyroAuxBiasPitch,
                System.Globalization.CultureInfo.InvariantCulture), 3);
            Assert.Equal(0.03f, float.Parse(ps.GyroAuxBiasYaw,
                System.Globalization.CultureInfo.InvariantCulture), 3);
            Assert.Equal(0.04f, float.Parse(ps.GyroAuxBiasRoll,
                System.Globalization.CultureInfo.InvariantCulture), 3);
        }

        // ── R1b: the motion gate ──

        /// <summary>A first-time calibration fired while the pad is being
        /// moved must write NOTHING. The trigger is device-connect (power
        /// button just pressed, BT reconnect mid-game), so "the pad was
        /// in hand" is the common case, not the edge.</summary>
        [Fact]
        public async Task MovingPad_FailsTheRunAndWritesNothing()
        {
            var state = ArrangeDevice(out var ud, aux: false);
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            using var mut = new GyroMutator(f => state.Gyro[0] = f ? 1f : -1f);   // 2 rad/s peak-to-peak
            bool ok = await svc.RecalibrateAsync(ud, ps, 250);

            Assert.True(mut.FlipCount > 10, "harness: the pad never moved during the window");
            Assert.False(ok);
            Assert.Equal("0", ps.GyroBiasPitch);
            Assert.Equal("", ps.GyroCalibratedAtUtc ?? "");
        }

        /// <summary>The halves are separate hands: a still primary with a
        /// moving LEFT half writes the primary and leaves the aux triple
        /// at its default for the upgrade branch to retry, rather than
        /// averaging the left half's motion into its bias.</summary>
        [Fact]
        public async Task MovingAuxAlone_SuppressesOnlyTheAuxWrite()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[0] = 0.01f;
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            using var mut = new GyroMutator(f => state.GyroAux[1] = f ? 1f : -1f);
            bool ok = await svc.RecalibrateAsync(ud, ps, 250);

            Assert.True(mut.FlipCount > 10, "harness: the left half never moved during the window");
            Assert.True(ok);
            Assert.NotEqual("", ps.GyroCalibratedAtUtc ?? "");
            Assert.Equal("0", ps.GyroAuxBiasPitch);
            Assert.Equal("0", ps.GyroAuxBiasYaw);
            Assert.Equal("0", ps.GyroAuxBiasRoll);
        }

        /// <summary>The aux-only upgrade with a moving left half writes
        /// nothing at all and reports failure, so the caller releases its
        /// latch and the upgrade retries later instead of latching a
        /// motion-corrupted left-half bias forever.</summary>
        [Fact]
        public async Task AuxUpgrade_MovingAux_WritesNothing()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[0] = 0.01f;
            var ps = new PadSetting
            {
                GyroCalibratedAtUtc = "2026-01-01T00:00:00Z",
                GyroBiasPitch = "0.5",
            };
            var svc = new GyroCalibratorService();

            using var mut = new GyroMutator(f => state.GyroAux[2] = f ? 1f : -1f);
            bool ok = await svc.EnsureAutoCalibratedAsync(ud, ps);

            Assert.True(mut.FlipCount > 10, "harness: the left half never moved during the window");
            Assert.False(ok);
            Assert.Equal("0.5", ps.GyroBiasPitch);
            Assert.Equal("0", ps.GyroAuxBiasPitch);
            Assert.Equal("2026-01-01T00:00:00Z", ps.GyroCalibratedAtUtc);
        }

        // ── R1c: WouldCalibrate is the single pure decision ──

        /// <summary>The caller consults this BEFORE burning its one-shot
        /// latch, so a pair with nothing to do never consumes the key and
        /// a later profile switch bringing an uncalibrated PadSetting to
        /// the same (device, slot) still auto-calibrates.</summary>
        [Fact]
        public void WouldCalibrate_DecisionTable()
        {
            ArrangeDevice(out var ud);

            Assert.False(GyroCalibratorService.WouldCalibrate(null, new PadSetting()));
            Assert.False(GyroCalibratorService.WouldCalibrate(ud, null));

            // Never calibrated: due.
            Assert.True(GyroCalibratorService.WouldCalibrate(ud, new PadSetting()));

            // Stamped, aux still unset on an aux-capable device: the
            // #252 upgrade is due.
            var stampedUnset = new PadSetting { GyroCalibratedAtUtc = "2026-01-01T00:00:00Z" };
            Assert.True(GyroCalibratorService.WouldCalibrate(ud, stampedUnset));

            // Stamped with a measured aux: nothing to do.
            var stampedSet = new PadSetting
            {
                GyroCalibratedAtUtc = "2026-01-01T00:00:00Z",
                GyroAuxBiasPitch = "0.01",
            };
            Assert.False(GyroCalibratorService.WouldCalibrate(ud, stampedSet));

            // Stamped on a NON-aux device: nothing to do, and under the
            // old caller this pair burned the session latch anyway.
            ud.HasGyroAux = false;
            Assert.False(GyroCalibratorService.WouldCalibrate(ud, stampedUnset));

            ud.HasGyro = false;
            Assert.False(GyroCalibratorService.WouldCalibrate(ud, new PadSetting()));
        }

        // ── R2: the macro half of a mask rename is its own, LATER step ──

        /// <summary>Renaming a layer retags macros through this walk, which
        /// the Configure handler runs only AFTER every pad's picker choices
        /// have been rebuilt with the new mask: the picker's SelectedValue
        /// binding resolves the retagged value at write time, so pushing it
        /// before the choice existed blanked the picker on every rename.
        /// This pins the walk itself: own-mask macros follow on every pad,
        /// other scopes stay put.</summary>
        [Fact]
        public void RetagMacrosEverywhere_FollowsTheMaskAcrossPads()
        {
            var vm0 = new PadViewModel(0);
            var vm1 = new PadViewModel(1);
            vm0.Macros.Add(new MacroItem { LayerMask = "Shift" });
            vm0.Macros.Add(new MacroItem { LayerMask = "Aim" });
            vm1.Macros.Add(new MacroItem { LayerMask = "Shift" });

            PadForge.Views.PadPage.RetagMacrosEverywhere(
                new[] { vm0, vm1 }, "Shift", "Combat");

            Assert.Equal("Combat", vm0.Macros[0].LayerMask);
            Assert.Equal("Aim", vm0.Macros[1].LayerMask);
            Assert.Equal("Combat", vm1.Macros[0].LayerMask);
        }

        // ── R8: duplicate masks cannot reach the picker ──

        /// <summary>Pre-round-six persisted data can carry an activator
        /// whose mask collides with the synthetic Base. The picker dedupes
        /// by mask exactly like the cycle stops, so a reduction can never
        /// remove an instance a picker still has selected while an
        /// identical-valued twin survives.</summary>
        [Fact]
        public void DuplicateActivatorMask_YieldsOnePickerEntry()
        {
            var vm = new PadViewModel(0);
            var acts = new List<ShiftActivator>
            {
                new ShiftActivator { LayerMask = "Base", LayerName = "My Base", Mode = "Hold", Descriptor = "Button 9" },
            };
            vm.RebuildLayerTabs(acts);

            int baseEntries = 0;
            foreach (var c in vm.MacroLayerChoices)
                if (c.LayerMask == "Base") baseEntries++;
            Assert.Equal(1, baseEntries);
        }

        /// <summary>And new layers cannot take the identity in the first
        /// place: "Base" is the synthetic tab's mask and the #254
        /// macro-scope contract, so a layer NAMED Base derives the mask
        /// Base_2. Whitespace still falls back to Shift, and the ordinary
        /// suffix walk is unchanged.</summary>
        [Fact]
        public void NewLayerMask_CannotTakeBase()
        {
            var reserved = PadForge.Views.ShiftActivatorDialog.BuildReservedMasks(
                Array.Empty<ShiftActivator>());
            Assert.Contains("Base", reserved);
            Assert.Equal("Base_2",
                PadForge.Views.ShiftActivatorDialog.DeriveUniqueMask("Base", reserved));
            Assert.Equal("Shift",
                PadForge.Views.ShiftActivatorDialog.DeriveUniqueMask("   ", reserved));

            reserved.Add("Shift");
            Assert.Equal("Shift_2",
                PadForge.Views.ShiftActivatorDialog.DeriveUniqueMask("Shift", reserved));
        }
    }
}
