using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Two physically distinct units of the SAME controller model must not
    /// collide onto one Devices-list row. Reported 2026-07-25 with two Steam
    /// Controllers: connecting the second filled in the first's entry.
    ///
    /// Root cause: the reconnect fallback in FindOrCreateUserDevice adopts any
    /// OFFLINE record with a matching ProductGuid, and ProductGuid is VID+PID
    /// only. It exists so a Bluetooth controller can reclaim its settings after
    /// its device path changes, so it cannot simply be removed. The serial is
    /// the discriminator: when both sides report one and they differ, it is
    /// provably a different unit.
    ///
    /// The asymmetry is deliberate and is the whole design. SDL frees the 2015
    /// Steam Controller's serial on Windows ("We get a garbage serial number on
    /// Windows", SDL_hidapi_steam.c), so that model has NO per-unit identity and
    /// must keep adopting. The 2026 controller reports its BLE MAC as the serial
    /// and is fully distinguishable.
    /// </summary>
    public class SameModelDeviceIdentityTests
    {
        // Real shape from the owner's hardware: the 2026 Steam Controller's
        // serial is its BLE MAC, lowercase hex, also embedded in the HID path.
        private const string UnitA = "e3a4e86c8422";
        private const string UnitB = "f1b7d95a3011";

        [Fact]
        public void TwoUnitsWithDifferentSerials_AreNotTheSameUnit()
        {
            Assert.True(InputManager.IsDifferentPhysicalUnit(UnitA, UnitB));
        }

        [Fact]
        public void TheSameUnitReconnecting_IsAdopted()
        {
            // The case the fallback exists for: same physical controller, new
            // device path after a BT reconnect. Must still be adopted.
            Assert.False(InputManager.IsDifferentPhysicalUnit(UnitA, UnitA));
        }

        /// <summary>Serial casing varies by transport, so a case difference is
        /// the SAME unit, not a different one. Getting this wrong would break
        /// reconnect for every device whose serial arrives uppercased.</summary>
        [Theory]
        [InlineData("e3a4e86c8422", "E3A4E86C8422")]
        [InlineData("E3A4E86C8422", "e3a4e86c8422")]
        [InlineData(" e3a4e86c8422 ", "e3a4e86c8422")]
        public void SerialComparison_IgnoresCaseAndSurroundingSpace(string stored, string incoming)
        {
            Assert.False(InputManager.IsDifferentPhysicalUnit(stored, incoming));
        }

        // ── the ADOPTION PATH itself, not just the predicate ──

        private sealed class DeviceListScope : IDisposable
        {
            private readonly DeviceCollection _saved;
            public DeviceListScope()
            {
                _saved = SettingsManager.UserDevices;
                SettingsManager.UserDevices = new DeviceCollection();
            }
            public void Dispose() => SettingsManager.UserDevices = _saved;
        }

        private static readonly Guid SteamProductGuid = new("130328de-0000-0000-0000-000000000000");

        private static PadForge.Engine.Data.UserDevice Stored(Guid guid, string serial)
        {
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = guid,
                ProductGuid = SteamProductGuid,
                ProductName = "Steam Controller",
                SerialNumber = serial,
                IsOnline = false,          // the state the fallback looks for
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        /// <summary>THE reported bug: unit A is saved and offline, unit B of the
        /// same model connects. B must get its OWN row; A's identity and
        /// settings must survive untouched.</summary>
        [Fact]
        public void SecondUnitOfTheSameModel_DoesNotAdoptTheFirstsRow()
        {
            using var _ = new DeviceListScope();
            var guidA = Guid.NewGuid();
            var stored = Stored(guidA, UnitA);

            var im = new InputManager();
            var guidB = Guid.NewGuid();
            var got = im.FindOrCreateUserDevice(guidB, SteamProductGuid, UnitB);

            Assert.NotSame(stored, got);                 // a NEW row
            Assert.Equal(guidA, stored.InstanceGuid);    // A's identity intact
            Assert.Equal(UnitA, stored.SerialNumber);    // A's serial intact
        }

        /// <summary>The behavior the fallback exists for must still work: the
        /// SAME unit reconnecting with a new path adopts its old row and keeps
        /// its settings.</summary>
        [Fact]
        public void SameUnitWithANewPath_StillAdoptsItsOwnRow()
        {
            using var _ = new DeviceListScope();
            var stored = Stored(Guid.NewGuid(), UnitA);

            var im = new InputManager();
            var newGuid = Guid.NewGuid();
            var got = im.FindOrCreateUserDevice(newGuid, SteamProductGuid, UnitA);

            Assert.Same(stored, got);                    // adopted
            Assert.Equal(newGuid, got.InstanceGuid);     // migrated to the new identity
        }

        /// <summary>A device with no per-unit identity (the 2015 Steam
        /// Controller on Windows, or any record saved before serials were
        /// captured) must keep adopting, or it would strand on a fresh empty
        /// row every reconnect.</summary>
        [Fact]
        public void SeriallessDevice_StillAdopts()
        {
            using var _ = new DeviceListScope();
            var stored = Stored(Guid.NewGuid(), "");

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), SteamProductGuid, "");

            Assert.Same(stored, got);
        }

        /// <summary>No serial on either side means no evidence, so adoption
        /// must proceed. This is the 2015 Steam Controller path on Windows and
        /// every record saved before serials were captured; blocking here would
        /// strand those devices with a fresh empty row on every reconnect.</summary>
        [Theory]
        [InlineData(null, "e3a4e86c8422")]      // legacy stored record
        [InlineData("", "e3a4e86c8422")]
        [InlineData("   ", "e3a4e86c8422")]
        [InlineData("e3a4e86c8422", null)]      // device reports no serial
        [InlineData("e3a4e86c8422", "")]
        [InlineData(null, null)]                // neither side knows
        [InlineData("", "")]
        public void MissingSerialOnEitherSide_StillAdopts(string stored, string incoming)
        {
            Assert.False(InputManager.IsDifferentPhysicalUnit(stored, incoming));
        }
    }
}
