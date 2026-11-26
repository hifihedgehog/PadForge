using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The layouts of controllers' relative values (#600): built once per
    /// handle on Raw Input's pump, known by path with or without a relative
    /// value, never listed from the polling thread, and dropped with their
    /// preparsed data when the device is removed.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class RelativeHidAxesLifecycleTests : IDisposable
    {
        private const string Path = @"\\?\HID#VID_FR02&PID_0002#lifecycle-test";
        private static readonly IntPtr Handle = (IntPtr)0x7F0F01;

        public void Dispose()
        {
            RelativeHidAxes.BuildSeam = null;
            RelativeHidAxes.HandlesSeam = null;
            RelativeHidAxes.FreeSeam = null;
            RawInputListener.ConsumerFreeSeam = null;
            RelativeHidAxes.Forget(Handle);
            RelativeHidAxes.ForgetForTest(Path);
        }

        private static RelativeHidAxes.Layout Spinner()
            => RelativeHidAxes.Build(Path, new[] { new RelativeHidAxes.Value(0x01, 0x30, 0, 8, -127, true) });

        [Fact]
        public void TwoThreadsAskingForANewHandle_BuildItOnce_AndThePathNamesTheLayoutTheyGet()
        {
            int builds = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            RelativeHidAxes.BuildSeam = _ =>
            {
                if (Interlocked.Increment(ref builds) == 1)
                {
                    entered.Set();
                    release.Wait(2000);
                }
                return (Spinner(), null);
            };
            RelativeHidAxes.Layout a = null, b = null;
            var first = new Thread(() => a = RelativeHidAxes.LayoutFor(Handle));
            first.Start();
            Assert.True(entered.Wait(2000));
            var second = new Thread(() => b = RelativeHidAxes.LayoutFor(Handle));
            second.Start();
            second.Join(300);
            release.Set();
            first.Join();
            second.Join();

            Assert.Equal(1, builds);
            Assert.Same(a, b);
            RelativeHidAxes.Add(a, v => v.Relative ? 3u : null);
            Assert.True(RelativeHidAxes.TryGetTotal(Path, 0, 1, out long total));
            Assert.Equal(3, total);
        }

        [Fact]
        public void ThePollingThreadListsNoDevice_AndAControllerWithNoRelativeValueIsReadOnce()
        {
            int lists = 0;
            RelativeHidAxes.HandlesSeam = () => { lists++; return new[] { Handle }; };
            RelativeHidAxes.BuildSeam = _ => (null, Path);

            // The device sweep opens the joystick before the pump read it.
            Assert.False(RelativeHidAxes.TryLeftOutRelative(Path, 2, out _));
            Assert.Equal(0, RelativeHidAxes.LeftOutRelative(Path, 2));
            Assert.False(RelativeHidAxes.IsKnown(Path));
            Assert.Equal(0, lists);

            // The pump reads what is present.
            RelativeHidAxes.LearnPresent();
            Assert.Equal(1, lists);
            Assert.True(RelativeHidAxes.IsKnown(Path));
            Assert.True(RelativeHidAxes.TryLeftOutRelative(Path, 2, out int leftOut));
            Assert.Equal(0, leftOut);
            Assert.Equal(0, RelativeHidAxes.LeftOutRelative(Path, 2));
            Assert.Equal(1, lists);
        }

        [Fact]
        public void ARemovedControllerWithNoRelativeValue_LeavesNoPathEntry()
        {
            RelativeHidAxes.BuildSeam = _ => (null, Path);
            RawInputListener.OnDeviceChange(1, Handle);                 // GIDC_ARRIVAL
            Assert.True(RelativeHidAxes.IsKnown(Path));
            RawInputListener.OnDeviceChange(2, Handle);                 // GIDC_REMOVAL
            Assert.False(RelativeHidAxes.KnowsHandleForTest(Handle));
            Assert.False(RelativeHidAxes.IsKnown(Path));
        }

        [Fact]
        public void ARemovedDevice_DropsItsLayoutAndItsConsumerData_AndFreesBothBlocks()
        {
            IntPtr layoutBlock = Marshal.AllocHGlobal(16);
            IntPtr consumerBlock = Marshal.AllocHGlobal(16);
            RelativeHidAxes.BuildSeam = _ =>
            {
                var layout = Spinner();
                layout.Preparsed = layoutBlock;
                return (layout, null);
            };
            var freed = new List<IntPtr>();
            Action<IntPtr> free = p => { freed.Add(p); Marshal.FreeHGlobal(p); };
            RelativeHidAxes.FreeSeam = free;
            RawInputListener.ConsumerFreeSeam = free;

            RawInputListener.OnDeviceChange(1, Handle);                 // GIDC_ARRIVAL
            Assert.True(RelativeHidAxes.KnowsHandleForTest(Handle));
            Assert.True(RelativeHidAxes.IsKnown(Path));
            RawInputListener.PutConsumerPreparsedForTest(Handle, consumerBlock);
            RawInputListener.PutConsumerStateForTest(Handle, new[] { true });

            RawInputListener.OnDeviceChange(2, Handle);                 // GIDC_REMOVAL
            Assert.False(RelativeHidAxes.KnowsHandleForTest(Handle));
            Assert.False(RelativeHidAxes.IsKnown(Path));
            Assert.False(RelativeHidAxes.TryGetTotal(Path, 0, 1, out _));
            Assert.False(RawInputListener.HasConsumerPreparsedForTest(Handle));
            Assert.False(RawInputListener.HasConsumerStateForTest(Handle));        // no key stays held
            Assert.Contains(layoutBlock, freed);
            Assert.Contains(consumerBlock, freed);
        }
    }
}
