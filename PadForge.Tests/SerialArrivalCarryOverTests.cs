using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #562: a Wii Remote on a Mayflash DolphinBar had no serial until fork
    /// b8afb934a2 (hifihedgehog/SDL#47) gave it one from its EEPROM
    /// calibration, so its saved entry is keyed by the bar slot's path. The
    /// first time a remote arrives with a serial it takes the offline entry of
    /// the slot it sits in. The same-product fallback alone would hand it the
    /// first offline entry of its model, which on the reporter's PC can be
    /// another slot's or another remote's. After that, the serial finds the
    /// entry in any slot.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SerialArrivalCarryOverTests : IDisposable
    {
        private const ushort Vid = 0x057E;
        private const ushort Pid = 0x0306;
        private const string Slot1 = @"\\?\HID#VID_057E&PID_0306&MI_00#7&170fdced&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string Slot2 = @"\\?\HID#VID_057E&PID_0306&MI_01#7&2a3ff92b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string RemoteA = "wiical-0102030405060708090a0b0c0d0e0f101112131415";

        private static readonly Guid Product = SdlDeviceWrapper.BuildProductGuid(Vid, Pid);

        private readonly DeviceCollection _savedDevices;
        private readonly SettingsCollection _savedSettings;

        public SerialArrivalCarryOverTests()
        {
            _savedDevices = SettingsManager.UserDevices;
            _savedSettings = SettingsManager.UserSettings;
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
            lock (InputManager.PendingDeviceGuidMigrationsLock)
                InputManager.PendingDeviceGuidMigrations.Clear();
        }

        private static UserDevice Row(Guid guid, bool online = false)
        {
            var ud = new UserDevice { InstanceGuid = guid, ProductGuid = Product, IsOnline = online };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static void Assign(Guid guid, int slot)
        {
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = slot });
        }

        private static UserDevice Arrive(InputManager im, string path, string serial)
            => im.FindOrCreateUserDevice(
                SdlDeviceWrapper.BuildInstanceGuid(path, Vid, Pid, 7, serial),
                Product, new HashSet<uint>(), serial,
                SdlDeviceWrapper.BuildPathInstanceGuid(path, Vid, Pid, serial));

        [Fact]
        public void FirstArrivalWithASerial_TakesItsOwnSlotsEntry_NotTheFirstOfItsModel()
        {
            // An offline remote's entry comes first in the list, the one the
            // same-product fallback takes. The bar's two slots follow, slot 2
            // assigned to PadForge slot 6.
            var otherRemote = Row(Guid.NewGuid());
            Guid slot1Key = SdlDeviceWrapper.BuildInstanceGuid(Slot1, Vid, Pid, 0);
            Guid slot2Key = SdlDeviceWrapper.BuildInstanceGuid(Slot2, Vid, Pid, 0);
            var slot1 = Row(slot1Key);
            var slot2 = Row(slot2Key);
            Assign(slot2Key, 5);

            var got = Arrive(new InputManager(), Slot2, RemoteA);

            Guid serialKey = SdlDeviceWrapper.BuildInstanceGuid(Slot2, Vid, Pid, 7, RemoteA);
            Assert.Same(slot2, got);
            Assert.Equal(serialKey, got.InstanceGuid);
            Assert.Equal(slot1Key, slot1.InstanceGuid);
            Assert.NotEqual(serialKey, otherRemote.InstanceGuid);
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                var us = Assert.Single(SettingsManager.UserSettings.Items);
                Assert.Equal(serialKey, us.InstanceGuid);
                Assert.Equal(5, us.MapTo);
            }
            lock (InputManager.PendingDeviceGuidMigrationsLock)
                Assert.Contains((slot2Key, serialKey), InputManager.PendingDeviceGuidMigrations);
        }

        [Fact]
        public void NextTime_TheSerialFindsTheEntry_InAnotherBarSlot()
        {
            Guid slot1Key = SdlDeviceWrapper.BuildInstanceGuid(Slot1, Vid, Pid, 0);
            Guid slot2Key = SdlDeviceWrapper.BuildInstanceGuid(Slot2, Vid, Pid, 0);
            var slot1 = Row(slot1Key);
            var slot2 = Row(slot2Key);
            var im = new InputManager();
            var first = Arrive(im, Slot2, RemoteA);
            Assert.Same(slot2, first);
            first.IsOnline = false;

            // The bar hands the remote slot 1 this time.
            var again = Arrive(im, Slot1, RemoteA);

            Assert.Same(slot2, again);
            Assert.Equal(slot1Key, slot1.InstanceGuid);
        }

        [Fact]
        public void AnEntryInUse_IsNotTaken()
        {
            var otherRemote = Row(Guid.NewGuid());
            var slot2 = Row(SdlDeviceWrapper.BuildInstanceGuid(Slot2, Vid, Pid, 0), online: true);

            var got = Arrive(new InputManager(), Slot2, RemoteA);

            Assert.NotSame(slot2, got);
            Assert.Same(otherRemote, got);
        }

        [Fact]
        public void PathIdentity_OnlyForADeviceWithASerialAndAPathOfItsOwn()
        {
            Assert.Equal(SdlDeviceWrapper.BuildInstanceGuid(Slot1, Vid, Pid, 0),
                SdlDeviceWrapper.BuildPathInstanceGuid(Slot1, Vid, Pid, RemoteA));
            Assert.NotEqual(SdlDeviceWrapper.BuildInstanceGuid(Slot1, Vid, Pid, 0, RemoteA),
                SdlDeviceWrapper.BuildPathInstanceGuid(Slot1, Vid, Pid, RemoteA));
            Assert.Equal(Guid.Empty, SdlDeviceWrapper.BuildPathInstanceGuid(Slot1, Vid, Pid, null));
            Assert.Equal(Guid.Empty, SdlDeviceWrapper.BuildPathInstanceGuid(Slot1, Vid, Pid, ""));
            Assert.Equal(Guid.Empty, SdlDeviceWrapper.BuildPathInstanceGuid("", Vid, Pid, RemoteA));
            Assert.Equal(Guid.Empty, SdlDeviceWrapper.BuildPathInstanceGuid("XInput#1", 0x045E, 0x0B13, "serial"));
        }
    }
}
