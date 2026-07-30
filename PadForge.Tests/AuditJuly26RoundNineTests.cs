using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round nine: round eight's fixes audited
    /// as hard as their bugs. The macro half of a device re-key, the
    /// whole-state write-guard, the per-profile in-flight guard, the
    /// ledger prune, the anchor-free rebind, and the trigger arm whose
    /// missing branch made round eight's Invert gate lie.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly26RoundNineTests : IDisposable
    {
        private static readonly Guid ProductGuid = new("130328de-0000-0000-0000-000000000000");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly GlobalMacroData[] _savedGlobals;

        public AuditJuly26RoundNineTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedGlobals = SettingsManager.GlobalMacros;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.GlobalMacros = _savedGlobals;
            lock (InputManager.PendingDeviceGuidMigrationsLock)
                InputManager.PendingDeviceGuidMigrations.Clear();
        }

        // ── R2: the macro half of a device re-key ──

        /// <summary>Every remap lane covered mapping rows, activators, and
        /// menus while every MACRO pin stayed on the dead guid, so a
        /// device-pinned macro trigger, axis-follow source, disconnect
        /// target, expression variable, and per-device profile shortcut
        /// all went permanently dark after a Bluetooth re-key. The
        /// evaluator matches these by exact equality.</summary>
        [Fact]
        public void MacroRemap_FollowsEveryPinnedGuid()
        {
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var other = Guid.NewGuid();

            var vm = new PadViewModel(0);
            var mac = new MacroItem { TriggerDeviceGuid = oldGuid };
            mac.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new MacroItem.TriggerInputEntry { DeviceGuid = oldGuid, RawButton = 3 },
                new MacroItem.TriggerInputEntry { DeviceGuid = other, RawButton = 4 },
            });
            mac.Actions.Add(new MacroAction
            {
                SourceDeviceGuid = oldGuid,
                DisconnectDeviceGuid = oldGuid,
            });
            vm.Macros.Add(mac);

            SettingsManager.GlobalMacros = new[]
            {
                new GlobalMacroData
                {
                    TriggerDeviceGuid = oldGuid,
                    TriggerEntries = new[]
                    {
                        new TriggerButtonEntry { DeviceInstanceGuid = oldGuid },
                        new TriggerButtonEntry { DeviceInstanceGuid = other },
                    },
                },
            };

            InputService.RemapDeviceGuidsInMacros(
                new Dictionary<Guid, Guid> { [oldGuid] = newGuid }, new[] { vm });

            var entries = mac.GetTriggerInputEntries();
            Assert.Equal(newGuid, mac.TriggerDeviceGuid);
            Assert.Equal(newGuid, entries[0].DeviceGuid);
            Assert.Equal(other, entries[1].DeviceGuid);          // untouched
            Assert.Equal(newGuid, mac.Actions[0].SourceDeviceGuid);
            Assert.Equal(newGuid, mac.Actions[0].DisconnectDeviceGuid);
            Assert.Equal(newGuid, SettingsManager.GlobalMacros[0].TriggerDeviceGuid);
            Assert.Equal(newGuid, SettingsManager.GlobalMacros[0].TriggerEntries[0].DeviceInstanceGuid);
            Assert.Equal(other, SettingsManager.GlobalMacros[0].TriggerEntries[1].DeviceInstanceGuid);
        }

        /// <summary>The cached spec string is derived from the guid, so a
        /// remapped entry must regenerate it rather than keep serializing
        /// the dead identity.</summary>
        [Fact]
        public void MacroRemap_RegeneratesTheTriggerSpec()
        {
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var vm = new PadViewModel(0);
            var mac = new MacroItem();
            mac.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new MacroItem.TriggerInputEntry { DeviceGuid = oldGuid, RawButton = 3 },
            });
            vm.Macros.Add(mac);
            Assert.Contains(oldGuid.ToString(), mac.GetTriggerInputEntries()[0].DeviceGuidString);

            InputService.RemapDeviceGuidsInMacros(
                new Dictionary<Guid, Guid> { [oldGuid] = newGuid }, new[] { vm });

            Assert.Equal(newGuid.ToString(), mac.GetTriggerInputEntries()[0].DeviceGuidString);
        }

        // ── R4: the write-guard covers the WHOLE calibration state ──

        /// <summary>ResetCalibration writes six bias strings and THEN the
        /// timestamp. Round eight's guard read the timestamp alone, so a
        /// guard read landing mid-transaction passed and produced a
        /// hybrid: the sampler's measured bias with the reset's cleared
        /// timestamp. Comparing every field closes it.</summary>
        [Fact]
        public async Task HalfAppliedReset_IsNotClobbered()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                IsOnline = true,
                HasGyro = true,
                InputState = state,
            };
            var ps = new PadSetting
            {
                GyroCalibratedAtUtc = "2026-01-01T00:00:00Z",
                GyroBiasPitch = "0.5",
            };
            var svc = new GyroCalibratorService();

            var run = svc.RecalibrateAsync(ud, ps, 250);
            await Task.Delay(60);
            // The reset's FIRST write only: bias cleared, stamp still old.
            ps.GyroBiasPitch = "0";

            bool ok = await run;

            Assert.False(ok);
            Assert.Equal("0", ps.GyroBiasPitch);
        }

        // ── R5: one sampler per profile ──

        /// <summary>Reset fires its own auto-calibration while a manual
        /// run may still be live, and the two wrote the same PadSetting.
        /// The calibrator now refuses a second concurrent pass for one
        /// profile outright.</summary>
        [Fact]
        public async Task SecondConcurrentPass_ForOneProfile_IsRefused()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                IsOnline = true,
                HasGyro = true,
                InputState = new CustomInputState(),
            };
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            var first = svc.RecalibrateAsync(ud, ps, 250);
            bool second = await svc.RecalibrateAsync(ud, ps, 250);
            bool firstOk = await first;

            Assert.False(second);
            Assert.True(firstOk);

            // ...and the guard releases, so a later pass still runs.
            Assert.True(await svc.RecalibrateAsync(ud, ps, 250));
        }

        // ── R7: the rebind scan runs without an exact-GUID anchor ──

        /// <summary>Deleting a twin's offline sibling row (the Devices
        /// page allows it) left a flapped LIVE twin with no anchor: the
        /// scan was skipped, step 3 minted a fresh row, and the twin's own
        /// row was orphaned. The wrapper's serial is the quantity the
        /// constraint always meant.</summary>
        [Fact]
        public void FlappedTwin_WithNoSiblingRow_StillRebinds()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var rowB = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                ProductGuid = ProductGuid,
                SerialNumber = "X",
                IsOnline = true,
                Device = new SdlDeviceWrapper { SdlInstanceId = 9 },
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(rowB);

            var im = new InputManager();
            // The serial-derived guid matches NO row (the sibling's row is gone).
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), ProductGuid,
                new HashSet<uint> { 11 }, "X");

            Assert.Same(rowB, got);
        }

        /// <summary>The serial constraint still holds without the anchor:
        /// a different unit in its own debounce is not hijacked.</summary>
        [Fact]
        public void AnchorFreeScan_StillRefusesADifferentSerial()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var rowC = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                ProductGuid = ProductGuid,
                SerialNumber = "Y",
                IsOnline = true,
                Device = new SdlDeviceWrapper { SdlInstanceId = 5 },
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(rowC);

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), ProductGuid,
                new HashSet<uint> { 11 }, "X");

            Assert.NotSame(rowC, got);
        }

        // ── R3: the trigger arm that made the Invert gate lie ──

        /// <summary>Mouse Motion was the one trigger arm with no
        /// Bidirectional branch, so Invert stayed live in Half + Either
        /// while the editors greyed the checkbox out. It now mirrors
        /// around center like its Axis and Gyro-Lean siblings: both
        /// directions pull, and Invert changes nothing.</summary>
        [Fact]
        public void MouseMotionTrigger_HonoursBidirectional()
        {
            var s = new CustomInputState();
            s.JoyCon2MouseDY = -8;                    // half of full-scale, negative

            var plain = new MappingSource
            {
                Descriptor = "Mouse Motion Y",
                HalfAxis = true,
                Bidirectional = true,
            };
            var inverted = new MappingSource
            {
                Descriptor = "Mouse Motion Y",
                HalfAxis = true,
                Bidirectional = true,
                Invert = true,
            };

            float a = SourceCoercion.EvaluateForTriggerTarget(s, plain);
            float b = SourceCoercion.EvaluateForTriggerTarget(s, inverted);

            Assert.True(a > 0f, $"negative motion must still pull, got {a}");
            Assert.Equal(a, b, 4);                    // Invert genuinely inert
        }

        // ── R9: the third editor's Invert gate ──

        [Fact]
        public void MacroTriggerEntry_InvertApplicability()
        {
            var e = new MacroItem.TriggerInputEntry();
            Assert.True(e.IsInvertApplicable);
            e.HalfAxis = true;
            Assert.True(e.IsInvertApplicable);
            e.Bidirectional = true;
            Assert.False(e.IsInvertApplicable);
        }
    }
}
