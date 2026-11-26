using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A travel input (#600) read by a lane that keeps no wheel of its own,
    /// a menu's click, a macro's trigger, an activator: the app's wheel on
    /// the slot's runtime moves and presses as a row's does, on the polling
    /// thread only.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class TravelReadsTests : IDisposable
    {
        private const int Slot = 3;
        private readonly Func<CustomInputState, MappingSource, int, string, double?> _saved = SourceCoercion.TravelPositionProvider;

        public void Dispose() => SourceCoercion.TravelPositionProvider = _saved;

        private static CustomInputState Scroll(int units)
        {
            var s = new CustomInputState { MouseRawWheel = units };
            s.Axis[0] = s.Axis[1] = s.Axis[2] = 32767;
            return s;
        }

        [Fact]
        public void AMenusClickOnWheelTravel_PressesOnceTheWheelIsScrolledPastItsDeadzone()
        {
            SourceCoercion.TravelPositionProvider = InputManager.TravelPosition;
            var runtime = InputManager.GetSlotSourceKindRuntime(Slot);
            var click = new MappingSource { Kind = "Direct", Descriptor = SourceCoercion.WheelTravelDescriptor };
            string device = Guid.NewGuid().ToString();

            // Off the polling thread the read takes no wheel.
            bool offThread = false;
            var other = new System.Threading.Thread(() =>
            {
                offThread = SourceCoercion.EvaluateForButtonTarget(Scroll(1600), click, 50, Slot, device);
            });
            other.Start();
            other.Join();
            Assert.False(offThread);

            InputManager.MarkPollThread();
            for (int i = 0; i < 3; i++) InputManager.BeginTravelReadPass();   // a gap: the wheel starts at center
            Assert.False(SourceCoercion.EvaluateForButtonTarget(Scroll(0), click, 50, Slot, device));
            InputManager.BeginTravelReadPass();
            Assert.False(SourceCoercion.EvaluateForButtonTarget(Scroll(600), click, 50, Slot, device));   // 600 of 2,000: under half
            InputManager.BeginTravelReadPass();
            Assert.True(SourceCoercion.EvaluateForButtonTarget(Scroll(600), click, 50, Slot, device));    // 1,200: past it
            InputManager.BeginTravelReadPass();
            Assert.Equal(0.6f, SourceCoercion.EvaluateForBipolarAxisTarget(Scroll(0), click, Slot, false, device), 4);

            // A menu in Step 2 and a macro after Step 3 read the same poll's
            // state in one pass, and its counts are added once.
            InputManager.BeginTravelReadPass();
            var poll = Scroll(200);
            SourceCoercion.EvaluateForButtonTarget(poll, click, 50, Slot, device);
            runtime.FrameSeq++;                                              // Step 3 stamps the rows' next frame
            Assert.Equal(0.7f, SourceCoercion.EvaluateForBipolarAxisTarget(poll, click, Slot, false, device), 4);
        }

        [Fact]
        public void TwoMiceOnOneSlot_KeepTwoWheels()
        {
            SourceCoercion.TravelPositionProvider = InputManager.TravelPosition;
            string mouseA = Guid.NewGuid().ToString(), mouseB = Guid.NewGuid().ToString();
            var onA = new MappingSource { Kind = "Direct", Descriptor = SourceCoercion.WheelTravelDescriptor, DeviceGuid = mouseA };
            var onB = new MappingSource { Kind = "Direct", Descriptor = SourceCoercion.WheelTravelDescriptor, DeviceGuid = mouseB };
            InputManager.MarkPollThread();
            for (int i = 0; i < 3; i++) InputManager.BeginTravelReadPass();
            SourceCoercion.EvaluateForBipolarAxisTarget(Scroll(0), onA, Slot, false, mouseA);
            SourceCoercion.EvaluateForBipolarAxisTarget(Scroll(1000), onB, Slot, false, mouseB);
            InputManager.BeginTravelReadPass();
            // Only mouse B was scrolled: mouse A's reader stays at center.
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(Scroll(0), onA, Slot, false, mouseA), 4);
            Assert.Equal(0.5f, SourceCoercion.EvaluateForBipolarAxisTarget(Scroll(0), onB, Slot, false, mouseB), 4);
        }
    }
}
