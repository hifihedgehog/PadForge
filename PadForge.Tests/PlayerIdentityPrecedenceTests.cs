using System;
using System.Collections.Generic;
using PadForge;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Player-identity precedence for devices feeding multiple virtual
    /// controllers: the smallest displayed player number owns every
    /// identity output (SlotOrders.GetIdentityPlayerNumber).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PlayerIdentityPrecedenceTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly List<int> _savedXbox;
        private readonly List<int> _savedKbm;
        private readonly bool[] _savedCreated;

        public PlayerIdentityPrecedenceTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedXbox = SettingsManager.XboxSlotOrder;
            _savedKbm = SettingsManager.KeyboardMouseSlotOrder;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.XboxSlotOrder = _savedXbox;
            SettingsManager.KeyboardMouseSlotOrder = _savedKbm;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
        }

        /// <summary>Slot 0 = Xbox group (displayed player 1), slot 1 =
        /// KbM group (displayed player 2).</summary>
        private static void ArrangeTwoSlots()
        {
            SettingsManager.XboxSlotOrder = new List<int> { 0 };
            SettingsManager.KeyboardMouseSlotOrder = new List<int> { 1 };
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;
            SettingsManager.SlotCreated[1] = true;
        }

        [Fact]
        public void SharedDevice_TakesSmallestDisplayedNumber_RegardlessOfItemsOrder()
        {
            ArrangeTwoSlots();
            var guid = Guid.NewGuid();
            var settings = new SettingsCollection();
            // KbM assignment enumerates FIRST: the pre-fix per-pair walk
            // wrote player 2 then player 1 on every reseed, which is the
            // reported fraction-of-a-second player-2 flash.
            settings.Items.Add(new Engine.Data.UserSetting { InstanceGuid = guid, MapTo = 1 });
            settings.Items.Add(new Engine.Data.UserSetting { InstanceGuid = guid, MapTo = 0 });
            SettingsManager.UserSettings = settings;

            Assert.Equal(1, SettingsManager.SlotOrders.GetIdentityPlayerNumber(guid));
        }

        [Fact]
        public void SingleAssignment_KeepsItsOwnNumber()
        {
            ArrangeTwoSlots();
            var guid = Guid.NewGuid();
            var settings = new SettingsCollection();
            settings.Items.Add(new Engine.Data.UserSetting { InstanceGuid = guid, MapTo = 1 });
            SettingsManager.UserSettings = settings;

            Assert.Equal(2, SettingsManager.SlotOrders.GetIdentityPlayerNumber(guid));
        }

        [Fact]
        public void UnassignedOrEmptyGuid_YieldsZero()
        {
            ArrangeTwoSlots();
            SettingsManager.UserSettings = new SettingsCollection();

            Assert.Equal(0, SettingsManager.SlotOrders.GetIdentityPlayerNumber(Guid.NewGuid()));
            Assert.Equal(0, SettingsManager.SlotOrders.GetIdentityPlayerNumber(Guid.Empty));
        }

        [Fact]
        public void UncreatedSlotAssignment_IsSkipped()
        {
            ArrangeTwoSlots();
            var guid = Guid.NewGuid();
            var settings = new SettingsCollection();
            settings.Items.Add(new Engine.Data.UserSetting { InstanceGuid = guid, MapTo = 5 });
            settings.Items.Add(new Engine.Data.UserSetting { InstanceGuid = guid, MapTo = 1 });
            SettingsManager.UserSettings = settings;

            // Slot 5 isn't created (GetGlobalSlotNumber = 0), so the KbM
            // assignment's player 2 wins instead of a bogus zero.
            Assert.Equal(2, SettingsManager.SlotOrders.GetIdentityPlayerNumber(guid));
        }
    }
}
