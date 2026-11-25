using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using PadForge.Common.Recording;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A force-feedback device's motors by the axes they act on (#600 item
    /// 32), read through DirectInput as SDL finds them for its haptic device,
    /// on a thread of their own. A file names them on the device's line once
    /// a read kept them, and among the events otherwise, as soon as a read
    /// does.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderMotorAxesTests
    {
        /// <summary>A device SDL drives through a haptic device.</summary>
        public class HapticBehind : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_" + nameof(ISdlInputDevice.HapticHandle)) return new IntPtr(1);
                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
            }
        }

        private static UserDevice Wheel()
        {
            var wheel = RecorderTestKit.Pad("Test Wheel", 0x046D, 0xC24F);
            wheel.ActuatorCount = 2;
            wheel.Device = DispatchProxy.Create<ISdlInputDevice, HapticBehind>();
            return wheel;
        }

        /// <summary>A host with a force-feedback wheel (D1) and a pad whose
        /// rumble has no haptic device (D2): live, with motors, and no
        /// haptic handle.</summary>
        private static RecorderTestHost WheelAndPad()
        {
            var host = new RecorderTestHost();
            host.DeviceList.Add(Wheel());
            var pad = RecorderTestKit.Pad("Test Pad");
            pad.ActuatorCount = 2;
            pad.Device = DispatchProxy.Create<ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            host.DeviceList.Add(pad);
            return host;
        }

        /// <summary>A read that names motors for every product, so only the
        /// haptic handle decides which device is asked about.</summary>
        private static IReadOnlyList<string> AnyProductsMotors(ushort vendor, ushort product)
            => new[] { "X Axis", "Y Axis" };

        [Fact]
        public void MotorsAReadAlreadyNamed_AreOnTheDeviceLine_OfARecordingAndASnapshot()
        {
            var seam = ForceActuators.Seam;
            ForceActuators.Seam = AnyProductsMotors;
            try
            {
                using var host = WheelAndPad();
                var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                string path = recorder.Stop(wait: true).Path;
                var lines = RecorderTestKit.Lines(path);
                Assert.Contains(" ffmotors=\"X Axis and Y Axis\"", lines.First(l => l.StartsWith("D1 ", StringComparison.Ordinal)));
                Assert.DoesNotContain("ffmotors", lines.First(l => l.StartsWith("D2 ", StringComparison.Ordinal)));
                // Named in the header, so not again among the events.
                Assert.DoesNotContain(RecorderTestKit.Events(path), e => e.Payload.StartsWith("motors act on", StringComparison.Ordinal));

                var taken = recorder.Snapshot(wait: true);
                Assert.True(taken.Ok);
                Assert.Contains(" ffmotors=\"X Axis and Y Axis\"", RecorderTestKit.Lines(taken.Path).First(l => l.StartsWith("D1 ", StringComparison.Ordinal)));
            }
            finally { ForceActuators.Seam = seam; }
        }

        [Fact]
        public void MotorsNoReadHadNamed_AreSaidAmongTheEvents_OnceARead_KeepsThem()
        {
            var seam = ForceActuators.Seam;
            bool kept = ForceActuators.SeamKept;
            ForceActuators.Seam = AnyProductsMotors;
            ForceActuators.SeamKept = false;
            try
            {
                using var host = WheelAndPad();
                var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                // The read ends while it records.
                ForceActuators.SeamKept = true;
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                string path = recorder.Stop(wait: true).Path;
                Assert.DoesNotContain("ffmotors", RecorderTestKit.Lines(path).First(l => l.StartsWith("D1 ", StringComparison.Ordinal)));
                var feedback = RecorderTestKit.Events(path).Where(e => e.Layer == 'F').ToList();
                Assert.Single(feedback, e => e.Subject == "D1" && e.Payload == "motors act on X Axis and Y Axis");
                // A pad whose rumble has no haptic device is not asked about.
                Assert.DoesNotContain(feedback, e => e.Subject == "D2");
            }
            finally
            {
                ForceActuators.Seam = seam;
                ForceActuators.SeamKept = kept;
            }
        }

        [Fact]
        public void AWheelFirstConnectedWhileRecording_IsNamedAmongTheEvents()
        {
            var seam = ForceActuators.Seam;
            ForceActuators.Seam = AnyProductsMotors;
            try
            {
                using var host = new RecorderTestHost();
                host.DeviceList.Add(RecorderTestKit.Pad("Test Pad"));
                var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
                Assert.True(recorder.Start().Ok);
                var wheel = Wheel();
                host.DeviceList.Add(wheel);
                recorder.CaptureForTest.Input(wheel, RecorderTestKit.Rest());
                recorder.WaitForFlush();
                recorder.WaitForFlush();
                string path = recorder.Stop(wait: true).Path;
                Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'F' && e.Subject == "D2" && e.Payload == "motors act on X Axis and Y Axis");
            }
            finally { ForceActuators.Seam = seam; }
        }

        [Fact]
        public void ARead_IsMadeOnlyForAProductAskedFor_AndAMissStaysAMiss()
        {
            const uint wheel = 0xC24F046D, pad = 0x0CE6054C, later = 0x0B050E10;
            int reads = 0;
            var cache = new ForceActuators.Cache(() =>
            {
                reads++;
                return new Dictionary<uint, string[]> { [wheel] = new[] { "X Axis" }, [later] = new[] { "X Axis", "Y Axis" } };
            });
            Func<Action, Task> now = run => { run(); return Task.CompletedTask; };

            cache.ReadSoon(now);
            Assert.Equal(0, reads);
            Assert.Null(cache.Kept(wheel));
            Assert.Null(cache.Kept(pad));
            cache.ReadSoon(now);
            Assert.Equal(1, reads);
            Assert.Equal(new[] { "X Axis" }, cache.Kept(wheel));
            Assert.Null(cache.Kept(pad));
            Assert.True(cache.Missed(pad));
            // Nothing new asked: no read, however often the watch asks.
            cache.ReadSoon(now);
            Assert.Equal(1, reads);
            // A product the last read listed though nobody asked is kept.
            Assert.Equal(new[] { "X Axis", "Y Axis" }, cache.Kept(later));
            Assert.Null(cache.Kept(0x00010001));
            cache.ReadSoon(now);
            Assert.Equal(2, reads);
            Assert.True(cache.Missed(0x00010001));

            // A read that fails leaves what it was asked for a miss.
            var broken = new ForceActuators.Cache(() => throw new InvalidOperationException("no DirectInput"));
            Assert.Null(broken.Kept(wheel));
            broken.ReadSoon(now);
            Assert.True(broken.Missed(wheel));
        }

        [Fact]
        public void DirectInputsRecords_AreLaidOutAsDinputHGivesThem()
        {
            // DIDEVICEINSTANCEW and DIDEVICEOBJECTINSTANCEW of the Windows
            // SDK's dinput.h, DIRECTINPUT_VERSION 0x0800.
            Assert.Equal(1100, Unsafe.SizeOf<ForceActuators.DeviceInstance>());
            Assert.Equal(576, Unsafe.SizeOf<ForceActuators.ObjectInstance>());
            Assert.Equal("X Axis", ForceActuators.NameOf(new Guid("A36D02E0-C9F3-11CF-BFC7-444553540000"), 0));
            Assert.Equal("Y Axis", ForceActuators.NameOf(new Guid("A36D02E1-C9F3-11CF-BFC7-444553540000"), 4));
            Assert.Equal("Z Rotation", ForceActuators.NameOf(new Guid("A36D02E3-C9F3-11CF-BFC7-444553540000"), 20));
            Assert.Equal("the axis at offset 40", ForceActuators.NameOf(Guid.NewGuid(), 40));
            Assert.Equal(0xC24F046Du, ForceActuators.Product(0x046D, 0xC24F));
            // A HID device's product GUID carries its IDs under the PIDVID
            // mark. The system mouse's does not.
            Assert.Equal(0xC24F046Du, ForceActuators.ProductOf(new Guid("C24F046D-0000-0000-0000-504944564944")));
            Assert.Equal(0u, ForceActuators.ProductOf(new Guid("6F1D2B60-D5A0-11CF-BFC7-444553540000")));
            Assert.Equal("X Axis", ForceActuators.Join(new[] { "X Axis" }));
            Assert.Equal("X Axis, Y Axis and Z Axis", ForceActuators.Join(new[] { "X Axis", "Y Axis", "Z Axis" }));
            // The test host asks DirectInput nothing.
            Assert.True(ForceActuators.OffLimitsForTest);
            Assert.Null(ForceActuators.Kept(0x046D, 0xC24F));
            Assert.False(ForceActuators.Missed(0x046D, 0xC24F));
        }

        /// <summary>DirectInput asked on this PC, by hand: set
        /// PADFORGE_LIVE_DIRECTINPUT to a file for what it lists, every
        /// attached force-feedback game controller and its motors' axes.</summary>
        [Fact]
        public void DirectInput_IsAskedOnThisPc_InALiveRun()
        {
            string report = Environment.GetEnvironmentVariable("PADFORGE_LIVE_DIRECTINPUT");
            if (string.IsNullOrEmpty(report)) return;
            bool fence = ForceActuators.OffLimitsForTest;
            ForceActuators.OffLimitsForTest = false;
            try
            {
                var read = ForceActuators.ReadAll();
                Assert.NotNull(read);
                // The positive control: the same four calls on the attached
                // pointing devices, every axis counted.
                var pointers = ForceActuators.ReadAll(ForceActuators.PointingDevices, ForceActuators.AttachedOnly, actuatorsOnly: false);
                Assert.NotNull(pointers);
                File.WriteAllText(report, "DirectInput listed " + read.Count + " attached force-feedback game controllers"
                    + string.Concat(read.Select(kv => Environment.NewLine + kv.Key.ToString("X8") + ": " + ForceActuators.Join(kv.Value)))
                    + Environment.NewLine + "and " + pointers.Count + " attached pointing devices, every axis"
                    + string.Concat(pointers.Select(kv => Environment.NewLine + kv.Key.ToString("X8") + ": " + ForceActuators.Join(kv.Value))));
            }
            finally { ForceActuators.OffLimitsForTest = fence; }
        }
    }
}
