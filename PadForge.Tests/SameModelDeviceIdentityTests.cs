using System;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How PadForge identifies two or more controllers of the SAME model.
    ///
    /// The policy is CONNECTION ORDER, not hardware identity (owner decision,
    /// 2026-07-25). Identical controllers are physically indistinguishable to
    /// their owner: without labelling the shell you cannot tell which unit you
    /// pulled out of the drawer. So the first unit powered on claims the stored
    /// entry and the mappings already built for it, whichever unit it is. What
    /// must hold is that identity is STABLE while PadForge runs.
    ///
    /// A serial gate was tried here and reverted. Blocking adoption when the
    /// serials differ is precisely the drawer case, and it made the second
    /// controller come up blank instead of inheriting the user's config.
    /// Serial-pinned identity belongs only where the user has a mental model of
    /// a specific pairing (DualShock 3, Wii); that is a separate per-device-class
    /// decision and is not this generic path.
    /// </summary>
    /// <remarks>Shares the SettingsManagerStatics collection: these tests swap
    /// SettingsManager.UserDevices, a global static other classes also replace,
    /// so without this the class races them by construction.</remarks>
    [Collection("SettingsManagerStatics")]
    public class SameModelDeviceIdentityTests
    {
        // Real shape from the owner's hardware: the 2026 Steam Controller's
        // serial is its BLE MAC. The 2015 model has NO serial on Windows at
        // all, because SDL frees it as garbage in HIDAPI_DriverSteam_InitDevice.
        private const string UnitA = "e3a4e86c8422";

        private static readonly Guid SteamProductGuid = new("130328de-0000-0000-0000-000000000000");

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

        private static PadForge.Engine.Data.UserDevice Stored(Guid guid, string serial)
        {
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = guid,
                ProductGuid = SteamProductGuid,
                ProductName = "Steam Controller",
                SerialNumber = serial,
                IsOnline = false,          // the state the adoption path looks for
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        // ── The property that matters: identity is stable while running ──

        /// <summary>Two units connected AT THE SAME TIME occupy two rows. This
        /// never depended on serials: each unit is marked online the instant it
        /// is resolved, and adoption only ever considers OFFLINE candidates, so
        /// a later unit in the same sweep cannot land on an earlier one's row.
        /// Pinned for the 2015 shape (both serials blank) because that is the
        /// configuration with no per-unit identity available anywhere.</summary>
        [Theory]
        [InlineData("", "")]                            // 2015: no serials at all
        [InlineData("e3a4e86c8422", "f1b7d95a3011")]    // 2026: distinct BLE MACs
        [InlineData(null, null)]
        public void TwoUnitsConnectedSimultaneously_GetSeparateRows(string serialA, string serialB)
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();

            var guidA = Guid.NewGuid();
            var rowA = im.FindOrCreateUserDevice(guidA, SteamProductGuid);
            rowA.ProductGuid = SteamProductGuid;
            rowA.SerialNumber = serialA;
            rowA.IsOnline = true;                       // as the sweep does immediately

            var guidB = Guid.NewGuid();
            var rowB = im.FindOrCreateUserDevice(guidB, SteamProductGuid);
            rowB.SerialNumber = serialB;

            Assert.NotSame(rowA, rowB);
            Assert.Equal(guidA, rowA.InstanceGuid);     // A untouched
            Assert.Equal(guidB, rowB.InstanceGuid);
        }

        /// <summary>Four on one 2015 dongle, which exposes them as USB
        /// interfaces 1-4 sharing VID, PID and a blank serial. Four rows.</summary>
        [Fact]
        public void FourUnitsOnOneDongle_GetFourRows()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var guids = new Guid[4];
            var rows = new PadForge.Engine.Data.UserDevice[4];

            for (int i = 0; i < 4; i++)
            {
                guids[i] = Guid.NewGuid();              // distinct: paths differ by interface
                rows[i] = im.FindOrCreateUserDevice(guids[i], SteamProductGuid);
                rows[i].ProductGuid = SteamProductGuid;
                rows[i].SerialNumber = "";
                rows[i].IsOnline = true;
            }

            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    Assert.NotSame(rows[i], rows[j]);
            for (int i = 0; i < 4; i++)
                Assert.Equal(guids[i], rows[i].InstanceGuid);
        }

        // ── The drawer contract: first one on claims the entry ──

        /// <summary>THE contract. A DIFFERENT physical unit of the same model,
        /// powered on while the stored one is offline, adopts the stored entry
        /// and inherits its mappings. The user cannot tell the units apart, so
        /// the one they switch on is the one that should work.</summary>
        [Fact]
        public void ADifferentUnitPoweredOnFirst_InheritsTheStoredEntry()
        {
            using var _ = new DeviceListScope();
            var stored = Stored(Guid.NewGuid(), UnitA);   // configured with unit A

            var im = new InputManager();
            var guidB = Guid.NewGuid();                   // unit B is what got grabbed
            var got = im.FindOrCreateUserDevice(guidB, SteamProductGuid);

            Assert.Same(stored, got);                     // B inherits A's config
            Assert.Equal(guidB, got.InstanceGuid);        // and the entry follows B
        }

        /// <summary>The same unit reconnecting with a new device path also
        /// adopts. This is the case the fallback was originally written for
        /// (Bluetooth paths change), and it stays intact.</summary>
        [Fact]
        public void SameUnitWithANewPath_StillAdoptsItsOwnRow()
        {
            using var _ = new DeviceListScope();
            var stored = Stored(Guid.NewGuid(), UnitA);

            var im = new InputManager();
            var newGuid = Guid.NewGuid();
            var got = im.FindOrCreateUserDevice(newGuid, SteamProductGuid);

            Assert.Same(stored, got);
            Assert.Equal(newGuid, got.InstanceGuid);
        }

        /// <summary>A device with no per-unit identity at all (the 2015 Steam
        /// Controller on Windows) adopts too, by the same rule.</summary>
        [Fact]
        public void SeriallessDevice_StillAdopts()
        {
            using var _ = new DeviceListScope();
            var stored = Stored(Guid.NewGuid(), "");

            var im = new InputManager();
            var got = im.FindOrCreateUserDevice(Guid.NewGuid(), SteamProductGuid);

            Assert.Same(stored, got);
        }

        /// <summary>Adoption is scoped to the MODEL. A different product never
        /// adopts, so a 2015 controller and a 2026 controller (different PIDs,
        /// therefore different ProductGuids) can never take each other's entry
        /// no matter what order they are switched on in.</summary>
        [Fact]
        public void ADifferentModel_NeverAdopts()
        {
            using var _ = new DeviceListScope();
            var stored2026 = Stored(Guid.NewGuid(), UnitA);          // PID 1303

            var im = new InputManager();
            var guid2015 = Guid.NewGuid();
            var productGuid2015 = new Guid("114228de-0000-0000-0000-000000000000");  // PID 1142
            var got = im.FindOrCreateUserDevice(guid2015, productGuid2015);

            Assert.NotSame(stored2026, got);
            Assert.Equal(UnitA, stored2026.SerialNumber);            // 2026 entry untouched
        }
    }
}
