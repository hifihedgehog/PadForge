using System;
using System.Linq;
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
    /// <summary>
    /// The levels the mappings list draws on its tags, read 30 times a
    /// second for every live tag of the shown controller.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MappingListTagLevelTests : IDisposable
    {
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose() => SettingsManager.UserDevices = _savedDevices;

        /// <summary>A controller whose stick row reads Axis 0 of a connected
        /// pad, the pad's record in the device list, and the row's tag.</summary>
        private static (PadViewModel Vm, CustomInputState State, RowTag Tag) PadWithAnAxisTag()
        {
            var vm = new PadViewModel(0);
            vm.RebuildMappings();
            Guid pad = Guid.NewGuid();
            vm.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = pad, Name = "Pad", TypeKey = "Gamepad", IsOnline = true });
            var state = new CustomInputState();
            SettingsManager.UserDevices = new DeviceCollection();
            var ud = new UserDevice
            {
                InstanceGuid = pad,
                ProductName = "Pad",
                InstanceName = "Pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            var row = vm.Mappings.First(m => m.TargetSettingName == "LeftThumbAxisX");
            row.PrimarySourceDeviceGuid = pad.ToString("D");
            row.PrimarySourceDeviceLabel = "Pad";
            row.LoadDescriptor("Axis 0");
            vm.FlushList();
            return (vm, state, Assert.Single(row.LiveTags));
        }

        /// <summary>A tick finds each tag's device and input once. After
        /// that it reads the levels with no lock taken and nothing made, so
        /// the poll thread, which holds the device list's lock, never waits
        /// on the list, and a tick of unchanged levels allocates nothing.</summary>
        [Fact]
        public async Task TheLevels_AreReadWithoutTheDeviceLockOrAnAllocation()
        {
            var (vm, state, tag) = PadWithAnAxisTag();
            state.Axis[0] = 65535;
            InputService.UpdateListTagLevels(vm);
            Assert.Equal(1.0, tag.Level);
            Assert.True(tag.IsCentered);

            InputService.UpdateListTagLevels(vm);
            long before = GC.GetAllocatedBytesForCurrentThread();
            InputService.UpdateListTagLevels(vm);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (SettingsManager.UserDevices.SyncRoot) { held.Set(); release.Wait(); }
            }) { IsBackground = true };
            holder.Start();
            held.Wait();
            try
            {
                state.Axis[0] = 0;
                var read = Task.Run(() => InputService.UpdateListTagLevels(vm));
                Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))) == read, "the list waited on the device lock");
                Assert.Equal(0.0, tag.Level);
            }
            finally
            {
                release.Set();
                holder.Join();
            }
        }

        /// <summary>A tag's input is found once in the state's arrays: an
        /// axis in the axes, a slider in the sliders, an alias through the
        /// input it names, and nothing for an input past the arrays or one
        /// that is neither.</summary>
        [Theory]
        [InlineData("Axis 3", 3, false)]
        [InlineData("Slider 1", 1, true)]
        [InlineData("IAxis 2", 2, false)]
        [InlineData("Gamepad LeftStickY", 1, false)]
        [InlineData("Axis 99", -1, false)]
        [InlineData("Button 2", -1, false)]
        public void AnInput_IsFoundInItsArrayOnce(string descriptor, int index, bool slider)
        {
            var find = typeof(InputService).GetMethod("LevelInputIndex",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            Assert.NotNull(find);
            var args = new object[] { descriptor, false };
            Assert.Equal(index, (int)find.Invoke(null, args));
            if (index >= 0) Assert.Equal(slider, (bool)args[1]);
        }

        /// <summary>A stop clears what the stopped timer wrote on the list's
        /// tags and on the Touchpad card: a level stands at rest, with no
        /// percent, and no finger is on the pad.</summary>
        [Fact]
        public void AStop_ClearsTheTagsLevelsAndTheTouchpadsFingers()
        {
            var (vm, state, tag) = PadWithAnAxisTag();
            state.Axis[0] = 65535;
            InputService.UpdateListTagLevels(vm);
            Assert.Equal((1.0, "100%"), (tag.Level, tag.LevelText.Replace("\u00A0", "").Replace(" ", "")));
            vm.DeviceTouchDown0 = true;
            vm.DeviceTouchDown1 = true;

            InputService.ClearLiveReadings(vm);

            Assert.Equal((0.5, string.Empty), (tag.Level, tag.LevelText));
            Assert.False(vm.DeviceTouchDown0);
            Assert.False(vm.DeviceTouchDown1);
        }

        /// <summary>A tag follows the device list: a device whose record
        /// comes after the tag's first tick is read once it is there, and a
        /// record made again for the same device is read in place of the old
        /// one.</summary>
        [Fact]
        public void ATag_FollowsTheDeviceList()
        {
            var (vm, state, tag) = PadWithAnAxisTag();
            var first = SettingsManager.UserDevices.Items[0];
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Clear();
            state.Axis[0] = 65535;
            InputService.UpdateListTagLevels(vm);
            Assert.Equal(0.0, tag.Level);

            // The record comes: the next tick reads it.
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(first);
            InputService.UpdateListTagLevels(vm);
            Assert.Equal(1.0, tag.Level);

            // The record is made again, with a state of its own.
            var again = new UserDevice
            {
                InstanceGuid = first.InstanceGuid,
                ProductName = "Pad",
                InstanceName = "Pad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                InputState = new CustomInputState(),
            };
            again.InputState.Axis[0] = 0;
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Remove(first);
                SettingsManager.UserDevices.Items.Add(again);
            }
            InputService.UpdateListTagLevels(vm);
            Assert.Equal(0.0, tag.Level);
        }
    }
}
