using System;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The UI timer's lanes copy the device list into an array they keep,
    /// made new only when the list's length changes. The dashboard and idle
    /// lanes each made a new copy 30 times a second (measured 2026-11-16).
    /// </summary>
    public class UiTimerDeviceRowsTests
    {
        [Fact]
        public void TheDeviceCopyIsMadeNewOnlyWhenTheListChangesLength()
        {
            var copy = typeof(InputService).GetMethod("CopyDeviceRows", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(copy);
            var devices = new DeviceCollection();
            var a = new UserDevice { InstanceGuid = Guid.NewGuid() };
            var b = new UserDevice { InstanceGuid = Guid.NewGuid() };
            devices.Items.Add(a);
            devices.Items.Add(b);

            var args = new object[] { devices, null };
            var first = (UserDevice[])copy.Invoke(null, args);
            Assert.Equal(new[] { a, b }, first);
            Assert.Same(first, args[1]);

            devices.Items.Reverse();
            var second = (UserDevice[])copy.Invoke(null, args);
            Assert.Same(first, second);
            Assert.Equal(new[] { b, a }, second);

            var c = new UserDevice { InstanceGuid = Guid.NewGuid() };
            devices.Items.Add(c);
            var third = (UserDevice[])copy.Invoke(null, args);
            Assert.NotSame(first, third);
            Assert.Equal(new[] { b, a, c }, third);
        }
    }
}
