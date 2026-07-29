using System;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Steam config assumes ONE controller, so its tuning is per physical
    /// input: "the right stick uses this deadzone shape", "gyro engages on
    /// this button". PadForge already owns settings for those, with cards.
    /// The import could not write them because it runs before a device is
    /// assigned and they are keyed by device guid, so it parked them on the
    /// slot and the engine consulted the parking spot at runtime.
    ///
    /// <para>That made the parking spot a second, invisible settings system.
    /// Worst case: the stick deadzone shape read returned the stamp
    /// unconditionally on an Authoritative slot, so the user's own Dead Zone
    /// Shape control was overridden and editing it did nothing.</para>
    ///
    /// <para>The stamps are folded into the device's own settings at
    /// assignment now, and cleared.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopTuningApplierTests : IDisposable
    {
        private const int Slot = 0;

        // SlotMappingSets is a STATIC. Replacing it without putting it back
        // leaks into every other test that reads it: doing so reddened five
        // ShortPressAndMacroLayerTests with nothing to do with this applier.
        private readonly MappingSet[] _priorSets = SettingsManager.SlotMappingSets;

        public void Dispose() => SettingsManager.SlotMappingSets = _priorSets;

        private static MappingSet ArrangeSlot(Action<MappingSet> tune)
        {
            var sets = new MappingSet[InputManager.MaxPads];
            var set = new MappingSet { Authoritative = true };
            tune(set);
            sets[Slot] = set;
            SettingsManager.SlotMappingSets = sets;
            return set;
        }

        [Fact]
        public void EveryDeviceAssignmentPathAppliesTheStamps()
        {
            // Grep-as-a-test, and it exists because the first cut wired the
            // applier into ONE of the two assignment paths. The runtime
            // overlays it replaced applied on every path by construction, so
            // an entry point that assigns a device without folding the stamps
            // silently drops the imported tuning for whichever path the user
            // takes. A third path added later fails here on arrival.
            var src = System.IO.File.ReadAllText(
                System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Services", "DeviceService.cs"));

            // Split into member bodies on the indentation marker. Plain
            // string splitting on purpose: no regex, no escape literals.
            var methods = src.Split(
                new[] { "        private ", "        public ", "        internal " },
                StringSplitOptions.None);

            var assigners = methods
                .Where(m => m.Contains("SettingsManager.AssignDeviceToSlot("))
                .ToList();
            Assert.True(assigners.Count >= 2,
                "expected at least 2 assignment paths, found " + assigners.Count);

            var unguarded = assigners
                .Where(m => !m.Contains("WorkshopTuningApplier.ApplyToAssignedDevice"))
                .Select(m => new string(m.TakeWhile(c => c != '(').ToArray()).Trim())
                .ToList();

            Assert.True(unguarded.Count == 0,
                "these assignment paths never fold the Workshop stamps into the "
                + "device's own settings: " + string.Join(" | ", unguarded));
        }

        private static string RepoRoot()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void StickDeadZoneShapeLandsInTheDevicesOwnSetting()
        {
            var set = ArrangeSlot(s =>
            {
                s.WorkshopLeftStickDeadZoneShape = "1";
                s.WorkshopRightStickDeadZoneShape = "0";
            });
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("1", ps.LeftThumbDeadZoneShape);
            Assert.Equal("0", ps.RightThumbDeadZoneShape);
            // Consumed, so it cannot re-apply over a later user edit.
            Assert.Equal("", set.WorkshopLeftStickDeadZoneShape);
            Assert.Equal("", set.WorkshopRightStickDeadZoneShape);
        }

        [Fact]
        public void AUserChoiceIsNeverOverwritten()
        {
            // Re-assigning a device must not silently discard tuning the user
            // set by hand.
            var set = ArrangeSlot(s => s.WorkshopLeftStickDeadZoneShape = "1");
            var ps = new PadSetting { LeftThumbDeadZoneShape = "0" };

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("0", ps.LeftThumbDeadZoneShape);
            Assert.Equal("", set.WorkshopLeftStickDeadZoneShape);
        }

        [Fact]
        public void GyroEngageButtonLandsInTheDevicesOwnSetting()
        {
            var set = ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageToggle = false;
                s.WorkshopGyroEngageInvert = false;
            });
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));

            Assert.Equal("Gamepad LeftShoulder", ps.GyroAimEngageButton);
            Assert.Equal("Hold", ps.GyroAimEngageMode);
            Assert.Equal("", set.WorkshopGyroEngageDescriptor);
        }

        [Fact]
        public void SteamsInvertedEngageBecomesTheReleaseToEngageMode()
        {
            // gyro_button_invert means the gyro fires while the button is NOT
            // held. It used to ride a hidden per-slot flag no card could
            // reach; removing the overlay without this mapping would have
            // silently dropped the behavior, which is what a CS0649 "never
            // assigned" warning caught mid-change.
            ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageInvert = true;
            });
            var ps = new PadSetting();

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("ReleaseToEngage", ps.GyroAimEngageMode);
        }

        [Fact]
        public void ToggleWinsOverInvert()
        {
            ArrangeSlot(s =>
            {
                s.WorkshopGyroEngageDescriptor = "Gamepad LeftShoulder";
                s.WorkshopGyroEngageToggle = true;
                s.WorkshopGyroEngageInvert = true;
            });
            var ps = new PadSetting();

            WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps);

            Assert.Equal("Toggle", ps.GyroAimEngageMode);
        }

        [Fact]
        public void PositiveControl_NoStampsMeansNoChange()
        {
            // Without this every assertion above could pass on an applier that
            // wrote unconditionally.
            ArrangeSlot(_ => { });
            var ps = new PadSetting();
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            Assert.Equal("2", ps.LeftThumbDeadZoneShape);
            Assert.Equal("", ps.GyroAimEngageButton);
        }

        [Fact]
        public void ApplyingTwiceIsAnoop()
        {
            // The stamp is cleared on the first pass, so a second assignment
            // cannot resurrect it over a value the user has since changed.
            ArrangeSlot(s => s.WorkshopLeftStickDeadZoneShape = "1");
            var ps = new PadSetting();

            Assert.True(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            ps.LeftThumbDeadZoneShape = "0";           // user changes their mind
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, ps));
            Assert.Equal("0", ps.LeftThumbDeadZoneShape);
        }

        [Fact]
        public void NoSlotMappingSetIsSafe()
        {
            SettingsManager.SlotMappingSets = null;
            Assert.False(WorkshopTuningApplier.ApplyToAssignedDevice(Slot, new PadSetting()));
        }
    }
}
