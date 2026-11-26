using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// An axis marked as moving relatively (#600), as the polling thread
    /// reads it: no allocation per read, a left-out axis's total included,
    /// and a Neg leg on another axis keeps no relative mark.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class RelativeAxisReadTests : IDisposable
    {
        private const string Path = @"\\?\HID#VID_FR01&PID_0001#relative-read-test";
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            RelativeHidAxes.ForgetForTest(Path);
            SettingsManager.UserDevices = _savedDevices;
        }

        private static RelativeHidAxes.Value Hid(ushort usage, bool relative)
            => new(0x01, usage, 0, 8, relative ? -127 : 0, relative);

        [Fact]
        public void ALeftOutAxisTotal_IsReadWithNoAllocation()
        {
            // An absolute X and Y and a relative dial DirectInput left out:
            // SDL lists two axes, and the dial is PadForge's Axis 2.
            var layout = RelativeHidAxes.Build(Path, new[] { Hid(0x30, false), Hid(0x31, false), Hid(0x37, true) });
            RelativeHidAxes.RegisterForTest(layout);
            RelativeHidAxes.Add(layout, v => v.Usage == 0x37 ? 5u : null);
            Assert.True(RelativeHidAxes.TryGetTotal(Path, 2, 2, out long first));
            Assert.Equal(5, first);

            long before = GC.GetAllocatedBytesForCurrentThread();
            long sum = 0;
            for (int i = 0; i < 1000; i++)
                if (RelativeHidAxes.TryGetTotal(Path, 2, 2, out long total)) sum += total;
            long grown = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(5000, sum);
            Assert.Equal(0, grown);
        }

        [Fact]
        public void ARelativeAxisTotal_ReadsBothPathsWithNoAllocation()
        {
            var guid = Guid.NewGuid();
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = guid, ProductGuid = guid, InstanceName = "Spinner", ProductName = "Spinner",
                CapType = InputDeviceType.Joystick, IsOnline = true, IsEnabled = true,
                Device = new SdlDeviceWrapper(),
            });
            SettingsManager.UserDevices = devices;
            string id = guid.ToString();
            Assert.Null(InputManager.RelativeAxisTotal(id, false, 2));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) InputManager.RelativeAxisTotal(id, false, 2);
            long grown = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, grown);
        }

        [Fact]
        public void ANegLegOnAnotherAxis_KeepsNoRelativeMark()
        {
            var row = new MappingItem("Left Stick X", "LeftThumbAxisX", MappingCategory.LeftStick);
            row.LoadDescriptor("Axis 2");
            row.PrimarySourceDeviceGuid = Guid.NewGuid().ToString();
            row.ParamAxisRelative = true;

            var primary = new PadForge.Engine.Data.MappingSource { Kind = "Direct", Descriptor = "Axis 2" };
            row.StoreMouseTravel(primary);
            Assert.True(primary.ParamAxisRelative);

            var neg = new PadForge.Engine.Data.MappingSource { Kind = "Direct", Descriptor = "Axis 3" };
            row.StoreMouseTravel(neg);
            Assert.False(neg.ParamAxisRelative);
        }
    }
}
