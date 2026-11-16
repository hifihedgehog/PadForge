using System;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Reads that run on every poll and built something on each one: the
    /// lean latch's key, which was composed from the device's text and the
    /// slot on every read, and a touchpad finger's movement, whose tracker
    /// was put back in its table as a new node on every poll and whose
    /// first-contact factory captured the sample on every read. At 1000 Hz
    /// that is garbage for as long as the row is read. Each is counted over
    /// windows the runtime left alone (<see cref="AllocationWindow"/>).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PollPathAllocationTests : IDisposable
    {
        private const int Slot = 9;
        private const string Device = "c9c9c9c9-0000-0000-0000-0000000000c9";

        private readonly Func<string, (float gx, float gy, float gz)> _savedGravity = SourceCoercion.GravityProvider;
        private readonly Func<string, (float gx, float gy, float gz)> _savedGravityAux = SourceCoercion.GravityProviderAux;

        /// <summary>The gravity both sensors report. A lean is measured
        /// from the grip the first sample is taken in, so a test reads once
        /// held level and then tilts.</summary>
        private static (float gx, float gy, float gz) s_gravity;

        private static readonly (float gx, float gy, float gz) Level = (0f, 9.8f, 0f);
        private static readonly (float gx, float gy, float gz) Tilted = (4f, 8.9f, 0.5f);

        public PollPathAllocationTests()
        {
            s_gravity = Level;
            SourceCoercion.GravityProvider = _ => s_gravity;
            SourceCoercion.GravityProviderAux = _ => s_gravity;
        }

        public void Dispose()
        {
            SourceCoercion.GravityProvider = _savedGravity;
            SourceCoercion.GravityProviderAux = _savedGravityAux;
        }

        private static void AssertNothing(long[] windows)
        {
            foreach (long bytes in windows) Assert.Equal(0, bytes);
        }

        /// <summary>The helper sees an allocation in a window, so a zero
        /// below is a reading and not a blind spot.</summary>
        [Fact]
        public void AnAllocationInAWindowIsCounted()
        {
            long[] windows = AllocationWindow.Clean(() => AllocationWindow.Kept = new object(), 200);
            foreach (long bytes in windows) Assert.True(bytes >= 200 * 24, bytes.ToString());
            AllocationWindow.Kept = null;
        }

        /// <summary>A lean source on a button or trigger row is read through
        /// the static lean read, for the body's sensor and for the second
        /// sensor of a Nunchuk or a left Joy-Con.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ALeanReadOnAButtonOrTriggerRowAllocatesNothing(bool aux)
        {
            var src = new MappingSource { Kind = aux ? "MotionLeanAuxX" : "MotionLeanX", DeviceGuid = Device };
            SourceCoercion.ReadMotionLeanValue(src, Device, aux, Slot);   // the resting grip
            s_gravity = Tilted;
            float read = 0f;
            long[] windows = AllocationWindow.Clean(
                () => read += Math.Abs(SourceCoercion.ReadMotionLeanValue(src, Device, aux, Slot)), 200);
            AssertNothing(windows);
            Assert.True(read > 0f, "the lean was read");
        }

        /// <summary>The Gyro Lean input composes the same latch key.</summary>
        [Fact]
        public void AGyroLeanReadAllocatesNothing()
        {
            var src = new MappingSource { Kind = "Direct", DeviceGuid = Device, Descriptor = SourceCoercion.GyroLeanXDescriptor };
            float read = 0f;
            long[] windows = AllocationWindow.Clean(
                () => read += Math.Abs(SourceCoercion.ReadGyroLean(src, SourceCoercion.GyroLeanXDescriptor, Device, Slot)), 200);
            AssertNothing(windows);
        }

        /// <summary>The stick lane's lean tick keys the second sensor's
        /// latch by the device and a suffix, which it joined on every
        /// tick.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ALeanTickOnAStickRowAllocatesNothing(bool aux)
        {
            var runtime = new SourceKindRuntime();
            var state = new CustomInputState();
            var src = new MappingSource { Kind = aux ? "MotionLeanAuxX" : "MotionLeanX", DeviceGuid = Device };
            SourceCoercion.BeginPollFrame();
            runtime.TickMotionLean(Slot, "LeftThumbAxisX", 0, src, state, Device, aux);   // the resting grip
            s_gravity = Tilted;
            double read = 0;
            long[] windows = AllocationWindow.Clean(() =>
            {
                SourceCoercion.BeginPollFrame();
                read += Math.Abs(runtime.TickMotionLean(Slot, "LeftThumbAxisX", 0, src, state, Device, aux));
            }, 200);
            AssertNothing(windows);
            Assert.True(read > 0, "the lean was read");
        }

        private delegate bool TouchAxisRead(CustomInputState state, MappingSource src, string descriptor,
            int slotIndex, string evaluatedDeviceGuid, out float bipolar);

        /// <summary>A finger that moves on a touchpad is read as its
        /// movement since the last poll, on every poll while it is
        /// down.</summary>
        [Theory]
        [InlineData("Touchpad 0 Finger 0 X")]
        [InlineData("Touchpad 0 Finger 0 Y")]
        public void AMovingFingersReadAllocatesNothing(string descriptor)
        {
            var read = (TouchAxisRead)typeof(SourceCoercion)
                .GetMethod("TryReadTouchpadAxis", BindingFlags.NonPublic | BindingFlags.Static)
                .CreateDelegate(typeof(TouchAxisRead));
            var savedFeel = SourceCoercion.TouchpadMouseSettingsProvider;
            SourceCoercion.TouchpadMouseSettingsProvider = null;
            try
            {
                var pad = new TouchpadInputState(2);
                pad.FingerDown[0] = true;
                pad.FingerX[0] = pad.FingerY[0] = 0.2f;
                var state = new CustomInputState { Touchpads = new[] { pad } };
                var src = new MappingSource { Kind = "Direct", DeviceGuid = Device, Descriptor = descriptor };
                float moved = 0f;
                int served = 0;
                long[] windows = AllocationWindow.Clean(() =>
                {
                    SourceCoercion.BeginPollFrame();
                    float next = pad.FingerX[0] > 0.8f ? 0.2f : pad.FingerX[0] + 0.001f;
                    pad.FingerX[0] = pad.FingerY[0] = next;
                    if (read(state, src, descriptor, Slot, Device, out float value)) served++;
                    moved += Math.Abs(value);
                }, 200);
                AssertNothing(windows);
                Assert.True(served > 0 && moved > 0f, "the finger's movement was read");
            }
            finally
            {
                SourceCoercion.TouchpadMouseSettingsProvider = savedFeel;
                // Lift the finger so the tracker this test made is dropped.
                var lifted = new TouchpadInputState(2);
                var liftedState = new CustomInputState { Touchpads = new[] { lifted } };
                read(liftedState, new MappingSource { Kind = "Direct", DeviceGuid = Device, Descriptor = descriptor },
                    descriptor, Slot, Device, out _);
            }
        }

        /// <summary>A second row on the same finger in one poll is served the
        /// movement the first read took, and the next poll measures from
        /// where that one left off. The tracker is now one object updated in
        /// place, so the two rules it keeps are pinned here.</summary>
        [Fact]
        public void TwoRowsOnOneFingerShareAPollsMovement()
        {
            var read = (TouchAxisRead)typeof(SourceCoercion)
                .GetMethod("TryReadTouchpadAxis", BindingFlags.NonPublic | BindingFlags.Static)
                .CreateDelegate(typeof(TouchAxisRead));
            var savedFeel = SourceCoercion.TouchpadMouseSettingsProvider;
            SourceCoercion.TouchpadMouseSettingsProvider = null;
            const string descriptor = "Touchpad 0 Finger 1 X";
            var pad = new TouchpadInputState(2);
            var state = new CustomInputState { Touchpads = new[] { pad } };
            var src = new MappingSource { Kind = "Direct", DeviceGuid = Device, Descriptor = descriptor };
            try
            {
                pad.FingerDown[1] = true;
                pad.FingerX[1] = pad.FingerY[1] = 0.30f;
                SourceCoercion.BeginPollFrame();
                Assert.True(read(state, src, descriptor, Slot, Device, out float seed));
                Assert.Equal(0f, seed);   // first contact reads no movement

                pad.FingerX[1] = 0.35f;
                SourceCoercion.BeginPollFrame();
                Assert.True(read(state, src, descriptor, Slot, Device, out float first));
                Assert.True(read(state, src, descriptor, Slot, Device, out float second));
                Assert.NotEqual(0f, first);
                Assert.Equal(first, second);

                // No movement since: the next poll reads none.
                SourceCoercion.BeginPollFrame();
                Assert.True(read(state, src, descriptor, Slot, Device, out float still));
                Assert.Equal(0f, still);

                // The same distance back reads the opposite movement.
                pad.FingerX[1] = 0.30f;
                SourceCoercion.BeginPollFrame();
                Assert.True(read(state, src, descriptor, Slot, Device, out float back));
                Assert.Equal(-first, back, 4);
            }
            finally
            {
                SourceCoercion.TouchpadMouseSettingsProvider = savedFeel;
                pad.FingerDown[1] = false;
                read(state, src, descriptor, Slot, Device, out _);
            }
        }

        /// <summary>The idle countdown and the Quick Charge check run for
        /// every device on every pass. Quick Charge's wired drop ran a
        /// lambda that captured a local of the check, so the compiler
        /// built the lambda's object at the check's entry: 24 bytes a
        /// device a pass, and with twelve devices the poll thread's whole
        /// 289 bytes a pass (measured 2026-11-16).</summary>
        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(true, 300)]
        public void TheIdleAndQuickChargeChecksAllocateNothing(bool quickCharge, int idleSeconds)
        {
            var device = new UserDevice { QuickChargeEnabled = quickCharge, IdleDisconnectSeconds = idleSeconds };
            var state = new CustomInputState();
            device.OldInputState = state;
            long[] windows = AllocationWindow.Clean(
                () => PadForge.Common.Input.InputManager.UpdateIdleDisconnect(device, state), 1000);
            AssertNothing(windows);
        }
    }
}
