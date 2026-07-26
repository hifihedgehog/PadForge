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
    /// <summary>Audit 2026-07-26 round ten: round nine's fixes audited as
    /// hard as their bugs. Sequential re-key application (the chain
    /// collapse merged two devices onto one identity), the serial gate on
    /// the anchor-free rebind, the three device pins no remap lane
    /// covered, the armed-window clear on a re-pin, and the in-flight
    /// guard's cancellation leak.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly26RoundTenTests : IDisposable
    {
        private static readonly Guid ProductGuid = new("130328de-0000-0000-0000-000000000000");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly GlobalMacroData[] _savedGlobals;

        public AuditJuly26RoundTenTests()
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

        // ── The re-key application order ──
        //
        // Round nine collapsed queued re-keys into one dictionary by
        // following "this pair's NEW guid equals an earlier pair's OLD
        // guid" as chain evidence. That holds for ONE row re-keyed twice
        // and fails for two devices swapping ports, where dev2's old guid
        // legitimately equals dev1's new one. These pin the semantics the
        // sequential application must produce; the collapse got the last
        // two wrong.

        private static Guid Apply(IReadOnlyList<(Guid Old, Guid New)> pending, Guid start)
        {
            // Mirrors the drain's loop: each queued pair is one pass.
            Guid cur = start;
            foreach (var (o, n) in pending)
                if (cur == o) cur = n;
            return cur;
        }

        [Fact]
        public void RekeyOrder_ChainOfOneRow_LandsOnTheTerminal()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
            var pending = new[] { (a, b), (b, c) };
            Assert.Equal(c, Apply(pending, a));
            Assert.Equal(c, Apply(pending, b));
        }

        [Fact]
        public void RekeyOrder_TwoDevicesSwappingPorts_AreNotMerged()
        {
            // dev1 leaves A and takes C; dev2 then takes A.
            Guid a = Guid.NewGuid(), c = Guid.NewGuid(), b = Guid.NewGuid();
            var pending = new[] { (a, c), (b, a) };

            Assert.Equal(c, Apply(pending, a));   // dev1's rows follow dev1
            Assert.Equal(a, Apply(pending, b));   // dev2's rows follow dev2, NOT onto c
            Assert.NotEqual(Apply(pending, a), Apply(pending, b));
        }

        [Fact]
        public void RekeyOrder_GuidReturningToAPreviousValue_EndsWhereItStarted()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var pending = new[] { (a, b), (b, a) };
            Assert.Equal(a, Apply(pending, a));   // identity, not b
        }

        // ── The serial gate on the anchor-free rebind ──

        private static UserDevice Row(string serial, bool online, uint claimant)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                ProductGuid = ProductGuid,
                SerialNumber = serial,
                IsOnline = online,
                Device = new SdlDeviceWrapper { SdlInstanceId = claimant },
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        /// <summary>A brand-new SERIALLESS pad must not adopt a sibling's
        /// row just because that sibling is momentarily absent from the
        /// SDL list. Empty == empty carries no identity information, and
        /// anchor-free it matched anything, so the newcomer inherited the
        /// sibling's mappings and the two swapped when it returned.</summary>
        [Fact]
        public void SerurallessNewPad_DoesNotStealAFlappedSiblingsRow()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var rowA = Row("", online: true, claimant: 9);   // sibling, SDL id absent below

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), ProductGuid,
                new HashSet<uint> { 11 }, "");

            Assert.NotSame(rowA, got);
        }

        /// <summary>The round-nine fix it must not break: a twin WITH a
        /// serial still rebinds anchor-free (the deleted-sibling case).
        /// A twin collision only exists when the serial is non-empty.</summary>
        [Fact]
        public void SerialBearingTwin_StillRebindsAnchorFree()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var rowB = Row("X", online: true, claimant: 9);

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), ProductGuid,
                new HashSet<uint> { 11 }, "X");

            Assert.Same(rowB, got);
        }

        // ── The three device pins no remap lane covered ──

        /// <summary>Gyro Aim Engage, both trigger-route activators, and the
        /// audio-mirror engage source are exact-equality device pins on
        /// PadSetting / DeviceSlotConfig. The mapping-set and macro lanes
        /// never saw them, so each went dark after a re-key exactly like
        /// the macro pins did.</summary>
        [Fact]
        public void PadSettingDevicePins_FollowTheRekey()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();
            var other = Guid.NewGuid();

            var ps = new PadSetting
            {
                GyroAimEngageDeviceGuid = oldGuid.ToString(),
                LeftTriggerRouteActivatorDeviceGuid = oldGuid.ToString(),
                RightTriggerRouteActivatorDeviceGuid = other.ToString(),
            };
            var us = new UserSetting { InstanceGuid = oldGuid, MapTo = 0 };
            us.SetPadSetting(ps);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            InputService.RemapDeviceGuidsInPadSettingsForTests(oldGuid, newGuid);

            Assert.Equal(newGuid.ToString(), ps.GyroAimEngageDeviceGuid);
            Assert.Equal(newGuid.ToString(), ps.LeftTriggerRouteActivatorDeviceGuid);
            Assert.Equal(other.ToString(), ps.RightTriggerRouteActivatorDeviceGuid);
        }

        // ── The armed-window clear on a re-pin ──

        /// <summary>Re-pinning a macro trigger to a different device
        /// invalidates its armed window exactly as re-authoring the entry
        /// list does. Writing the guids directly bypassed the clear
        /// SetTriggerInputEntries performs, so device A's press window was
        /// credited to device B and an untouched pad produced a synthetic
        /// release edge.</summary>
        [Fact]
        public void MacroRepin_ClearsTheArmedWindow()
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

            mac.TriggerHoldStartUtc = DateTime.UtcNow;
            mac.TriggerHoldFired = true;
            mac.TriggerPressStreak = 2;
            mac.WasTriggerActive = true;

            InputService.RemapDeviceGuidsInMacros(
                new Dictionary<Guid, Guid> { [oldGuid] = newGuid }, new[] { vm });

            Assert.Equal(DateTime.MinValue, mac.TriggerHoldStartUtc);
            Assert.False(mac.TriggerHoldFired);
            Assert.Equal(0, mac.TriggerPressStreak);
            Assert.False(mac.WasTriggerActive);
        }

        /// <summary>A macro on an UNRELATED device keeps its armed state:
        /// the clear must follow the re-pin, not every remap call.</summary>
        [Fact]
        public void MacroNotRepinned_KeepsItsArmedWindow()
        {
            var vm = new PadViewModel(0);
            var mac = new MacroItem();
            mac.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new MacroItem.TriggerInputEntry { DeviceGuid = Guid.NewGuid(), RawButton = 3 },
            });
            vm.Macros.Add(mac);
            mac.TriggerPressStreak = 2;
            mac.WasTriggerActive = true;

            InputService.RemapDeviceGuidsInMacros(
                new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() }, new[] { vm });

            Assert.Equal(2, mac.TriggerPressStreak);
            Assert.True(mac.WasTriggerActive);
        }

        // ── The in-flight guard must not leak on cancellation ──

        /// <summary>The guard is entered BEFORE Task.Run, and Task.Run with
        /// an already-signalled token never invokes the delegate, so the
        /// releasing finally would never run and the profile would be
        /// locked out of calibration for the process lifetime. The token
        /// is no longer passed to Task.Run; RunSampling checks it itself.</summary>
        [Fact]
        public async Task PreCancelledToken_DoesNotLockTheProfileOut()
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

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool cancelledRun = await svc.RecalibrateAsync(ud, ps, 250, cts.Token);
            Assert.False(cancelledRun);
            Assert.False(GyroCalibratorService.IsSampling(ps));

            // The profile is still usable.
            Assert.True(await svc.RecalibrateAsync(ud, ps, 250));
        }
    }
}
