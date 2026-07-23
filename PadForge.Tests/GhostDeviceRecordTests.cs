using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    // Locks the duplicate-guid ghost-record defenses (owner report
    // 2026-07-22): an EMPTY duplicate of a real device record (same
    // InstanceGuid, no caps, no name) shadowed the real record in
    // first-match lookups, so ReAutoMapSlot's CreateDefaultPadSetting
    // saw CapType 0 and authored an EMPTY PadSetting on a Nintendo
    // type switch. Load-time dedupe keeps the richest record per guid;
    // FindDeviceByInstanceGuid prefers a caps-bearing record among
    // mid-session duplicates.
    [Collection("SettingsManagerStatics")]
    public class GhostDeviceRecordTests
    {
        private static UserDevice Rich(Guid g) => new UserDevice
        {
            InstanceGuid = g,
            InstanceName = "Nintendo Switch Pro Controller",
            CapType = InputDeviceType.Gamepad,
            DeviceObjects = new[]
            {
                new DeviceObjectItem { ObjectType = DeviceObjectTypeFlags.AbsoluteAxis, InputIndex = 0 },
            },
        };

        private static UserDevice Ghost(Guid g) => new UserDevice { InstanceGuid = g };

        [Fact]
        public void DedupeKeepsRichestRegardlessOfOrder()
        {
            var g = Guid.NewGuid();
            int dropped = 0;
            var ghostFirst = SettingsService.DedupeDevicesByGuid(
                new List<UserDevice> { Ghost(g), Rich(g) }, ref dropped);
            Assert.Single(ghostFirst);
            Assert.Equal("Nintendo Switch Pro Controller", ghostFirst[0].InstanceName);
            Assert.Equal(1, dropped);

            dropped = 0;
            var richFirst = SettingsService.DedupeDevicesByGuid(
                new List<UserDevice> { Rich(g), Ghost(g) }, ref dropped);
            Assert.Single(richFirst);
            Assert.Equal("Nintendo Switch Pro Controller", richFirst[0].InstanceName);
            Assert.Equal(1, dropped);
        }

        [Fact]
        public void DedupeLeavesDistinctAndEmptyGuidRecordsAlone()
        {
            int dropped = 0;
            var a = Rich(Guid.NewGuid());
            var b = Rich(Guid.NewGuid());
            var e1 = Ghost(Guid.Empty);
            var e2 = Ghost(Guid.Empty);
            var result = SettingsService.DedupeDevicesByGuid(
                new List<UserDevice> { a, e1, b, e2 }, ref dropped);
            Assert.Equal(4, result.Count);
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void LookupPrefersCapsBearingRecordOverGhost()
        {
            var g = Guid.NewGuid();
            var saved = SettingsManager.UserDevices;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                lock (SettingsManager.UserDevices.SyncRoot)
                {
                    SettingsManager.UserDevices.Items.Add(Ghost(g));
                    SettingsManager.UserDevices.Items.Add(Rich(g));
                }
                var found = SettingsManager.FindDeviceByInstanceGuid(g);
                Assert.NotNull(found);
                Assert.Equal("Nintendo Switch Pro Controller", found.InstanceName);
            }
            finally
            {
                SettingsManager.UserDevices = saved;
            }
        }
    }
}
