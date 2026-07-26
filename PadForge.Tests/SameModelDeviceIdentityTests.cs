using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
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

        // ── The twin gate: identical serials must not merge live pads ──
        //
        // Serial outranks device path when the identity GUID is built, so
        // two units reporting the IDENTICAL serial string (a real clone-pad
        // shape) arrive with the SAME GUID. The distinctness pins above
        // never covered that: they mint distinct GUIDs per unit. The gate:
        // an exact-GUID row whose claiming wrapper's SDL instance is still
        // PRESENT belongs to a live sibling, so the newcomer gets a
        // session-minted row instead of stealing it. A claimant that has
        // left the present set is the ordinary reconnect rebind and adopts
        // as always. Only the SDL sweep passes the present set.

        private static PadForge.Engine.Data.UserDevice LiveRow(
            InputManager im, Guid guid, uint claimantSdlId)
        {
            var row = im.FindOrCreateUserDevice(guid, SteamProductGuid);
            row.ProductGuid = SteamProductGuid;
            row.IsOnline = true;
            row.Device = new SdlDeviceWrapper { SdlInstanceId = claimantSdlId };
            return row;
        }

        /// <summary>THE twin pin. The second unit with the same serial gets
        /// its own row under a minted identity, and the live sibling's row,
        /// wrapper, and GUID are untouched.</summary>
        [Fact]
        public void IdenticalSerialTwin_GetsItsOwnRow_AndTheLiveRowIsUntouched()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var sharedGuid = Guid.NewGuid();          // both units derive this from the shared serial
            var rowA = LiveRow(im, sharedGuid, claimantSdlId: 7);
            var wrapperA = rowA.Device;

            var present = new HashSet<uint> { 7, 9 }; // sibling still present; incoming is 9
            var rowB = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid, present);

            Assert.NotSame(rowA, rowB);
            Assert.Equal(sharedGuid, rowA.InstanceGuid);
            Assert.Same(wrapperA, rowA.Device);       // live wrapper not stolen or disposed
            Assert.NotEqual(sharedGuid, rowB.InstanceGuid);
            Assert.NotEqual(Guid.Empty, rowB.InstanceGuid);
        }

        /// <summary>A reconnect of the SAME unit arrives under a new SDL
        /// instance while the old one has left the present set, so the
        /// rebind flow the disconnect debounce relies on is untouched.</summary>
        [Fact]
        public void SameUnitReconnect_WhoseClaimantLeft_StillRebindsToItsRow()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var sharedGuid = Guid.NewGuid();
            var rowA = LiveRow(im, sharedGuid, claimantSdlId: 7);

            var present = new HashSet<uint> { 9 };    // 7 is gone; 9 is the new connection
            var got = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid, present);

            Assert.Same(rowA, got);
            Assert.Equal(sharedGuid, got.InstanceGuid);
        }

        /// <summary>Across launches the twin's persisted row is recycled by
        /// the drawer adoption KEEPING ITS OWN identity (round seven, R5).
        /// The incoming serial GUID collides with the live sibling, and the
        /// round-six design minted a fresh GUID per resolve, which re-keyed
        /// the twin every launch: per-device slot configs could never
        /// persist, device-pinned mapping rows died on reconnect, and
        /// Remote Link's stable-id contract broke. The row's existing GUID
        /// is what its UserSetting already references, so nothing migrates
        /// and everything GUID-keyed stays valid.</summary>
        [Fact]
        public void TwinRowFromLastLaunch_IsRecycledKeepingItsIdentity()
        {
            using var _ = new DeviceListScope();
            var savedSettings = SettingsManager.UserSettings;
            SettingsManager.UserSettings = new SettingsCollection();
            try
            {
                var im = new InputManager();
                var sharedGuid = Guid.NewGuid();
                LiveRow(im, sharedGuid, claimantSdlId: 7);

                var lastLaunchGuid = Guid.NewGuid();
                var twinRow = Stored(lastLaunchGuid, "");     // offline, same product
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(new PadForge.Engine.Data.UserSetting
                    { InstanceGuid = lastLaunchGuid, MapTo = 3 });

                var present = new HashSet<uint> { 7 };
                var got = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid, present);

                Assert.Same(twinRow, got);
                Assert.Equal(lastLaunchGuid, got.InstanceGuid);   // identity KEPT
                lock (SettingsManager.UserSettings.SyncRoot)
                    Assert.Equal(lastLaunchGuid,
                        SettingsManager.UserSettings.Items[0].InstanceGuid);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
            }
        }

        /// <summary>A twin that flaps back INSIDE the disconnect debounce:
        /// its own row is still marked online but its old SDL instance has
        /// left the present set. That is the same physical unit
        /// re-identifying, so it rebinds to its own row (round seven, R4).
        /// The round-six design minted a fresh row here, which orphaned the
        /// twin's assignment (no output) and left its stale wrapper to the
        /// debounce path's unconditional dispose, re-opening the shared
        /// HIDAPI-context churn the 2026-07-11 audit fixed.</summary>
        [Fact]
        public void FlappedTwin_InsideTheDebounce_RebindsToItsOwnRow()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var sharedGuid = Guid.NewGuid();
            LiveRow(im, sharedGuid, claimantSdlId: 7);

            var rowB = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid,
                new HashSet<uint> { 7, 9 });
            rowB.ProductGuid = SteamProductGuid;
            rowB.IsOnline = true;
            rowB.Device = new SdlDeviceWrapper { SdlInstanceId = 9 };
            var mintedGuid = rowB.InstanceGuid;

            // B's old instance 9 has left; it returns as 11 while A stays.
            var got = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid,
                new HashSet<uint> { 7, 11 });

            Assert.Same(rowB, got);
            Assert.Equal(mintedGuid, got.InstanceGuid);
        }

        /// <summary>Ordinary drawer adoption follows EVERY slot the device
        /// is assigned to. The migrator's old first-match break orphaned
        /// slots 2..N under a comment claiming one UserSetting per device,
        /// while the data model documents one PER SLOT (round seven, R6;
        /// pre-existing since v2.0.0-beta).</summary>
        [Fact]
        public void Adoption_MigratesEverySlotAssignment()
        {
            using var _ = new DeviceListScope();
            var savedSettings = SettingsManager.UserSettings;
            SettingsManager.UserSettings = new SettingsCollection();
            try
            {
                var oldGuid = Guid.NewGuid();
                Stored(oldGuid, UnitA);                       // offline, adoptable
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    SettingsManager.UserSettings.Items.Add(new PadForge.Engine.Data.UserSetting
                    { InstanceGuid = oldGuid, MapTo = 0 });
                    SettingsManager.UserSettings.Items.Add(new PadForge.Engine.Data.UserSetting
                    { InstanceGuid = oldGuid, MapTo = 3 });
                }

                var im = new InputManager();
                var newGuid = Guid.NewGuid();
                var got = im.FindOrCreateUserDevice(newGuid, SteamProductGuid);

                Assert.Equal(newGuid, got.InstanceGuid);
                lock (SettingsManager.UserSettings.SyncRoot)
                {
                    Assert.Equal(newGuid, SettingsManager.UserSettings.Items[0].InstanceGuid);
                    Assert.Equal(newGuid, SettingsManager.UserSettings.Items[1].InstanceGuid);
                }
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
            }
        }

        /// <summary>Callers that pass no present set (touchpad, keyboard,
        /// mouse, remote devices) keep today's semantics exactly: an exact
        /// online match returns the row, twin gate never engages.</summary>
        [Fact]
        public void NullPresentSet_KeepsTodaySemantics_ForNonSweepCallers()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var guid = Guid.NewGuid();
            var rowA = LiveRow(im, guid, claimantSdlId: 7);

            var got = im.FindOrCreateUserDevice(guid, SteamProductGuid);

            Assert.Same(rowA, got);
        }

        /// <summary>Three identical-serial units at once occupy three rows
        /// with three distinct identities.</summary>
        [Fact]
        public void ThreeIdenticalTwins_GetThreeRows()
        {
            using var _ = new DeviceListScope();
            var im = new InputManager();
            var sharedGuid = Guid.NewGuid();

            var rowA = LiveRow(im, sharedGuid, claimantSdlId: 7);

            var rowB = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid,
                new HashSet<uint> { 7, 8 });
            rowB.ProductGuid = SteamProductGuid;
            rowB.IsOnline = true;
            rowB.Device = new SdlDeviceWrapper { SdlInstanceId = 8 };

            var rowC = im.FindOrCreateUserDevice(sharedGuid, SteamProductGuid,
                new HashSet<uint> { 7, 8, 9 });

            Assert.NotSame(rowA, rowB);
            Assert.NotSame(rowA, rowC);
            Assert.NotSame(rowB, rowC);
            Assert.NotEqual(rowB.InstanceGuid, rowC.InstanceGuid);
        }
    }
}
