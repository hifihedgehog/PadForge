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
    /// <summary>Audit 2026-07-26 round eight: round seven's fixes audited
    /// as hard as their bugs. The calibration write-guard and the
    /// tightened plausibility bound, the hoisted serial-constrained
    /// flapped-unit rebind, the migration dedupe, the adoption re-key
    /// drain, and the Invert applicability gates.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly26RoundEightTests : IDisposable
    {
        private static readonly Guid DevGuid = new("edede000-8888-9999-aaaa-bbbbbbbbbbbb");
        private static readonly Guid ProductGuid = new("130328de-0000-0000-0000-000000000000");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedSlotSets;

        public AuditJuly26RoundEightTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedSlotSets = SettingsManager.SlotMappingSets;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedSlotSets;
            lock (InputManager.PendingDeviceGuidMigrationsLock)
                InputManager.PendingDeviceGuidMigrations.Clear();
        }

        private static CustomInputState ArrangeDevice(out UserDevice ud)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Round Eight Pad",
                IsOnline = true,
                HasGyro = true,
                HasGyroAux = false,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return state;
        }

        // ── R1: the optimistic write-guard ──

        /// <summary>Nothing cancels a running sampler, so before round
        /// eight a Reset Calibration clicked during the window was
        /// silently REVERTED to disk when the run completed a second
        /// later. The run now captures the calibration timestamp at entry
        /// and refuses to write when it changed underneath.</summary>
        [Fact]
        public async Task ResetDuringTheRun_IsNotReverted()
        {
            ArrangeDevice(out var ud);
            var ps = new PadSetting { GyroCalibratedAtUtc = "2026-01-01T00:00:00Z" };
            var svc = new GyroCalibratorService();

            var run = svc.RecalibrateAsync(ud, ps, 250);
            await Task.Delay(60);
            // The user's reset, mid-run.
            ps.GyroBiasPitch = "0";
            ps.GyroCalibratedAtUtc = "";

            bool ok = await run;

            Assert.False(ok);
            Assert.Equal("0", ps.GyroBiasPitch);
            Assert.Equal("", ps.GyroCalibratedAtUtc);
        }

        // ── R7: the tightened plausibility bound ──

        /// <summary>Round seven's 0.5 rad/s bound left a hole: a steady
        /// ~23 deg/s pan has zero peak-to-peak range AND an average under
        /// 0.5, so it calibrated itself into the bias. 0.3 rad/s sits in
        /// that exact band and must now be rejected.</summary>
        [Fact]
        public async Task SteadyRotationInTheOldBlindBand_IsRejected()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[2] = 0.3f;
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            bool ok = await svc.RecalibrateAsync(ud, ps, 250);

            Assert.False(ok);
            Assert.Equal("0", ps.GyroBiasRoll);
        }

        // ── R11: the hoisted, serial-constrained flapped-unit rebind ──

        private static UserDevice Row(Guid guid, string serial, bool online, uint claimant = 0)
        {
            var ud = new UserDevice
            {
                InstanceGuid = guid,
                ProductGuid = ProductGuid,
                ProductName = "Pad",
                SerialNumber = serial,
                IsOnline = online,
            };
            if (claimant != 0)
                ud.Device = new SdlDeviceWrapper { SdlInstanceId = claimant };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        /// <summary>Codex round-eight finding: when the shared-serial row
        /// was OFFLINE, step 1 adopted it directly and a merely-flapped
        /// LIVE twin was moved onto different assignments mid-session.
        /// The rebind scan now runs before the exact-match return.</summary>
        [Fact]
        public void FlappedTwin_WithItsSiblingOffline_StillRebindsToItsOwnRow()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var sharedGuid = Guid.NewGuid();
            Row(sharedGuid, "X", online: false);                    // sibling, offline
            var rowB = Row(Guid.NewGuid(), "X", online: true, claimant: 9);

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(sharedGuid, ProductGuid,
                new HashSet<uint> { 11 });                          // 9 left; back as 11

            Assert.Same(rowB, got);
        }

        /// <summary>The serial constraint is load-bearing: a THIRD
        /// same-model unit (different serial) sitting inside its own
        /// disconnect debounce must never be hijacked as "the flapped
        /// sibling", which swapped two units' identities and disposed the
        /// live one's wrapper.</summary>
        [Fact]
        public void ThirdUnitInItsDebounce_IsNotHijacked()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var sharedGuid = Guid.NewGuid();
            Row(sharedGuid, "X", online: true, claimant: 7);        // live sibling A
            var rowC = Row(Guid.NewGuid(), "Y", online: true, claimant: 5); // unrelated unit C, debouncing
            var cGuid = rowC.InstanceGuid;

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(sharedGuid, ProductGuid,
                new HashSet<uint> { 7, 9 });                        // twin B arriving as 9

            Assert.NotSame(rowC, got);
            Assert.Equal(cGuid, rowC.InstanceGuid);                 // C untouched
        }

        // ── R5: migration cannot manufacture duplicate rows ──

        /// <summary>When an orphaned setting already occupies the
        /// destination (newGuid, slot), the migration drops the old row
        /// instead of rewriting it into a duplicate, which would have
        /// shadowed the live PadSetting and double-fired samplers.</summary>
        [Fact]
        public void Adoption_DropsTheOrphanInsteadOfDuplicating()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var oldGuid = Guid.NewGuid();
            Row(oldGuid, "X", online: false);
            var newGuid = Guid.NewGuid();
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = oldGuid, MapTo = 0 });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = oldGuid, MapTo = 3 });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = newGuid, MapTo = 3 });
            }

            var im = new InputManager();
            im.FindOrCreateUserDevice(newGuid, ProductGuid);

            lock (SettingsManager.UserSettings.SyncRoot)
            {
                int slot3 = 0, slot0 = 0;
                foreach (var us in SettingsManager.UserSettings.Items)
                {
                    Assert.Equal(newGuid, us.InstanceGuid);
                    if (us.MapTo == 3) slot3++;
                    if (us.MapTo == 0) slot0++;
                }
                Assert.Equal(1, slot3);                             // no duplicate
                Assert.Equal(1, slot0);
            }
        }

        // ── R13: adoption re-keys reach the pinned references ──

        /// <summary>The poll thread records every adoption re-key for the
        /// UI thread to drain; without the record, device-pinned mapping
        /// rows kept the old guid and produced no output after any BT
        /// path change (the exact failure the remap helper's own doc
        /// names).</summary>
        [Fact]
        public void Adoption_QueuesThePinMigration()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            lock (InputManager.PendingDeviceGuidMigrationsLock)
                InputManager.PendingDeviceGuidMigrations.Clear();
            var oldGuid = Guid.NewGuid();
            Row(oldGuid, "X", online: false);

            var im = new InputManager();
            var newGuid = Guid.NewGuid();
            im.FindOrCreateUserDevice(newGuid, ProductGuid);

            lock (InputManager.PendingDeviceGuidMigrationsLock)
            {
                Assert.Contains(InputManager.PendingDeviceGuidMigrations,
                    m => m.Old == oldGuid && m.New == newGuid);
            }
        }

        /// <summary>The drain's row half: the remap helper rewrites a
        /// pinned source guid in the live mapping sets.</summary>
        [Fact]
        public void RemapHelper_RewritesAPinnedRowGuid()
        {
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var ms = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow
                    {
                        Target = "A",
                        LayerMask = "Base",
                        Sources = new List<MappingSource>
                        {
                            new MappingSource
                            {
                                Descriptor = "Button 0",
                                DeviceGuid = oldGuid.ToString().ToLowerInvariant(),
                            },
                        },
                    },
                },
            };
            SettingsManager.SlotMappingSets = new[] { ms };

            InputService.RemapDeviceGuidsInSlotMappingSets(
                new Dictionary<string, string>
                {
                    [oldGuid.ToString().ToLowerInvariant()] = newGuid.ToString().ToLowerInvariant(),
                });

            Assert.Equal(newGuid.ToString().ToLowerInvariant(),
                ms.Rows[0].Sources[0].DeviceGuid);
        }

        /// <summary>The drain's config half: the per-device slot config
        /// INSTANCE moves to the new identity, so effect-dispatcher
        /// subscriptions anchored to it stay valid and lighting settings
        /// follow the device.</summary>
        [Fact]
        public void RekeyDeviceConfig_MovesTheInstance()
        {
            var vm = new PadViewModel(0);
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var cfg = vm.GetOrCreateDeviceConfig(oldGuid);

            vm.RekeyDeviceConfig(oldGuid, newGuid);

            Assert.False(vm.PerDeviceSlotConfigs.ContainsKey(oldGuid));
            Assert.Same(cfg, vm.PerDeviceSlotConfigs[newGuid]);
        }

        // ── R15: the Invert checkbox is visibly inert where the engine
        //         ignores it ──

        [Fact]
        public void InvertApplicability_GridRow()
        {
            var m = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            Assert.True(m.IsInvertApplicable);

            bool raised = false;
            m.PropertyChanged += (_, e) =>
            { if (e.PropertyName == nameof(MappingItem.IsInvertApplicable)) raised = true; };

            m.IsHalfAxis = true;
            Assert.True(m.IsInvertApplicable);                      // Half alone: still live
            m.IsBidirectional = true;
            Assert.False(m.IsInvertApplicable);                     // Half + Either: inert
            Assert.True(raised);
        }

        [Fact]
        public void InvertApplicability_MergeSource()
        {
            var s = new MappingSourceItem();
            Assert.True(s.IsInvertApplicable);
            s.HalfAxis = true;
            s.Bidirectional = true;
            Assert.False(s.IsInvertApplicable);
        }
    }
}
