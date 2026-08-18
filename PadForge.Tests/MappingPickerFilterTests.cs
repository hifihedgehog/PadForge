using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    // Locks the mapping picker's shared-list filter (#322, discussion
    // #302): the pure predicate (find-as-you-type + hidden devices), the
    // "any" key for the device-agnostic group, and the persisted joined
    // form's round trip.
    [Collection("SettingsManagerStatics")]
    public class MappingPickerFilterTests
    {
        private static InputChoice C(string display, string guid)
            => new InputChoice { Descriptor = display, DisplayName = display, DeviceGuid = guid, DeviceLabel = guid == "" ? "(Any device)" : "Pad" };

        [Fact]
        public void Search_MatchesDisplayNameCaseInsensitive()
        {
            var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.True(PadViewModel.MatchesPickerFilter(C("Gyro Yaw", "g1"), "yaw", none));
            Assert.True(PadViewModel.MatchesPickerFilter(C("Gyro Yaw", "g1"), "GYRO", none));
            Assert.False(PadViewModel.MatchesPickerFilter(C("Gyro Yaw", "g1"), "trigger", none));
            Assert.True(PadViewModel.MatchesPickerFilter(C("Gyro Yaw", "g1"), "", none));
        }

        [Fact]
        public void HiddenDevice_HidesItsChoices_AndOnlyIts()
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "g1" };
            Assert.False(PadViewModel.MatchesPickerFilter(C("Button 0", "g1"), "", hidden));
            Assert.True(PadViewModel.MatchesPickerFilter(C("Button 0", "g2"), "", hidden));
        }

        [Fact]
        public void AnyDeviceGroup_FiltersUnderTheAnyKey()
        {
            // The device-agnostic group (empty guid) hides under "any",
            // which is how the Steam-import abstract entries get hidden.
            Assert.Equal("any", PadViewModel.PickerFilterKey(C("Gamepad A", "")));
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" };
            Assert.False(PadViewModel.MatchesPickerFilter(C("Gamepad A", ""), "", hidden));
            Assert.True(PadViewModel.MatchesPickerFilter(C("Button 0", "g1"), "", hidden));
        }

        [Fact]
        public void HiddenKeys_RoundTripThroughTheJoinedForm()
        {
            var vm = new PadViewModel(0);
            vm.SetHiddenPickerDeviceKeys(new[] { "any", "aaaa-bbbb" });
            string joined = vm.GetHiddenPickerDeviceKeysJoined();
            var vm2 = new PadViewModel(1);
            vm2.SetHiddenPickerDeviceKeys(joined.Split(';', StringSplitOptions.RemoveEmptyEntries));
            Assert.True(vm2.HiddenPickerDeviceKeys.SetEquals(vm.HiddenPickerDeviceKeys));
            Assert.Equal(2, vm2.HiddenPickerDeviceKeys.Count);
        }

        [Fact]
        public void RowSearch_MatchesTargetLabelAndSelectedSource()
        {
            // The visible half of the search: TABLE ROWS filter live.
            var row = new MappingItem("Left Bumper", "LeftShoulder", MappingCategory.Buttons);
            Assert.True(PadViewModel.RowMatchesSearch(row, ""));
            Assert.True(PadViewModel.RowMatchesSearch(row, "bumper"));
            Assert.True(PadViewModel.RowMatchesSearch(row, "LEFT"));
            Assert.False(PadViewModel.RowMatchesSearch(row, "trigger"));
        }

        [Fact]
        public void SharedRebuild_SuppressesRowWriteBack()
        {
            // The recorded ComboBox write-back lesson, on the shared path:
            // while the shared list mutates between Begin and End, a row's
            // selection sync must be suppressed, and End re-resolves from
            // the row's own stored descriptor.
            var shared = new System.Collections.ObjectModel.ObservableCollection<InputChoice>();
            var row = new MappingItem("Button A", "ButtonA", MappingCategory.Buttons);
            row.UseSharedAvailableInputs(shared);
            Assert.Same(shared, row.AvailableInputs);

            var choice = C("Button 3", "g1");
            row.BeginSharedListRebuild();
            shared.Clear();
            shared.Add(choice);
            // A live ComboBox writing back mid-rebuild must be ignored:
            // the suppression flag is the row's only defense, and End
            // restores sync. The seam test in the legacy suite proved the
            // same contract for the per-row era.
            row.EndSharedListRebuild();
            Assert.Same(shared, row.AvailableInputs);
        }
    }
}
