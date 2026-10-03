using System;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Up and Down keys of an Incremental or Ramp source read a
    /// touchpad gesture, a mouse gesture or a menu cell as a Button row reads
    /// it, for the source's slot and device. Their reader took the descriptor
    /// alone, so a gesture picked as a key never moved the value. The Invert
    /// on Hold modifier's mirror reads the same families.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class IncrementalGestureKeyTests : IDisposable
    {
        private const int Slot = 2;
        private const string Pad = "e6000000-0000-0000-0000-0000000000e6";

        private readonly Func<int, string, int, string, bool> _savedFired = SourceCoercion.TouchpadGestureFiredProvider;
        private readonly Func<int, string, int, string, float> _savedAxis = SourceCoercion.TouchpadGestureAxisProvider;
        private readonly Func<int, string, string, bool> _savedMouse = SourceCoercion.MouseGestureFiredProvider;
        private readonly Func<int, string, int, int, bool> _savedMenu = SourceCoercion.MenuItemFiredProvider;

        public void Dispose()
        {
            SourceCoercion.TouchpadGestureFiredProvider = _savedFired;
            SourceCoercion.TouchpadGestureAxisProvider = _savedAxis;
            SourceCoercion.MouseGestureFiredProvider = _savedMouse;
            SourceCoercion.MenuItemFiredProvider = _savedMenu;
        }

        private string _fired;

        /// <summary>The gesture named in <see cref="_fired"/> is fired on the
        /// pad's touchpad 0 on this slot, keyed as the live lookup keys it.</summary>
        private void StubGestures()
            => SourceCoercion.TouchpadGestureFiredProvider = (slot, guid, padIdx, name) =>
                slot == Slot && guid == Pad && padIdx == 0 && name == _fired;

        private static MappingSource Incremental(string up, string down, string device = Pad) => new()
        {
            Kind = "Incremental", DeviceGuid = device, ParamUp = up, ParamDown = down,
            ParamRate = 1.0, ParamMin = 0, ParamMax = 1, ParamSticky = true,
        };

        private static double Tick(SourceKindRuntime rt, MappingSource src, string evaluated = null)
        {
            rt.FrameSeq++;
            return rt.TickIncremental(Slot, "LeftTrigger", 0, src, new CustomInputState(), 0.05, evaluated);
        }

        /// <summary>A swipe holds its key for as long as the gesture stays
        /// fired, so each frame of it steps the value and the value holds
        /// once it stops. Down takes it back.</summary>
        [Fact]
        public void ASwipeKey_StepsTheIncrementalValue()
        {
            StubGestures();
            var rt = new SourceKindRuntime();
            var src = Incremental("Touchpad 0 SwipeUp", "Touchpad 0 SwipeDown");

            _fired = "SwipeUp";
            Tick(rt, src);
            double up = Tick(rt, src);
            Assert.Equal(0.1, up, 6);

            _fired = null;
            Assert.Equal(up, Tick(rt, src), 6);

            _fired = "SwipeDown";
            Assert.Equal(0.05, Tick(rt, src), 6);
        }

        /// <summary>A source with no device of its own reads its keys from the
        /// device the evaluator is reading.</summary>
        [Fact]
        public void AnAnyDeviceSource_ReadsTheKeyOnTheEvaluatedDevice()
        {
            StubGestures();
            var rt = new SourceKindRuntime();
            var src = Incremental("Touchpad 0 LongPress", "", device: "");

            _fired = "LongPress";
            Assert.Equal(0.05, Tick(rt, src, evaluated: Pad), 6);
            Assert.Equal(0.05, Tick(rt, src, evaluated: null), 6);
        }

        [Fact]
        public void AMouseGestureKey_DrivesTheRamp()
        {
            bool left = false;
            SourceCoercion.MouseGestureFiredProvider = (slot, guid, name) =>
                left && slot == Slot && guid == Pad && name == "Left";
            var rt = new SourceKindRuntime();
            var src = new MappingSource
            {
                Kind = "Ramped", DeviceGuid = Pad, ParamUp = "Mouse Gesture Right", ParamDown = "Mouse Gesture Left",
                ParamAttackTime = 0.5, ParamReleaseTime = 0.5, ParamAutocenter = true,
            };

            left = true;
            rt.FrameSeq++;
            double v = rt.TickRamped(Slot, "LeftThumbAxisX", 0, src, new CustomInputState(), 0.05);
            Assert.Equal(-0.1, v, 6);

            left = false;
            rt.FrameSeq++;
            Assert.Equal(0.0, rt.TickRamped(Slot, "LeftThumbAxisX", 0, src, new CustomInputState(), 0.05), 6);
        }

        [Fact]
        public void AMenuCellKey_StepsTheValue()
        {
            bool cell = true;
            SourceCoercion.MenuItemFiredProvider = (slot, guid, menu, item) =>
                cell && slot == Slot && guid == Pad && menu == 3 && item == 1;
            var rt = new SourceKindRuntime();
            var src = Incremental("Menu 3 Item 1", "");

            Assert.Equal(0.05, Tick(rt, src), 6);
            cell = false;
            Assert.Equal(0.05, Tick(rt, src), 6);
        }

        /// <summary>A continuous gesture holds the key past half, the read a
        /// Button row takes of it.</summary>
        [Fact]
        public void APinchAxisKey_HoldsPastHalf()
        {
            float pinch = 0.8f;
            SourceCoercion.TouchpadGestureAxisProvider = (slot, guid, padIdx, name) =>
                slot == Slot && guid == Pad && padIdx == 0 && name == "PinchAxis" ? pinch : 0f;
            var rt = new SourceKindRuntime();
            var src = Incremental("Touchpad 0 PinchAxis", "");

            Assert.Equal(0.05, Tick(rt, src), 6);
            pinch = 0.2f;
            Assert.Equal(0.05, Tick(rt, src), 6);
        }

        /// <summary>The keys the reader already took read as they did.</summary>
        [Fact]
        public void AButtonKey_StillSteps()
        {
            var rt = new SourceKindRuntime();
            var src = Incremental("Button 4", "");
            var held = new CustomInputState();
            held.Buttons[4] = true;
            rt.FrameSeq++;
            Assert.Equal(0.05, rt.TickIncremental(Slot, "LeftTrigger", 0, src, held, 0.05), 6);
        }

        /// <summary>The Invert on Hold modifier's reader in SourceEvaluator
        /// mirrors the key reader and reads a gesture modifier.</summary>
        [Fact]
        public void AGestureModifier_InvertsTheButton()
        {
            StubGestures();
            var src = new MappingSource
            {
                Kind = "InvertOnHold", Descriptor = "Button 0", DeviceGuid = Pad,
                ParamModifier = "Touchpad 0 TwoFingerTap",
            };
            var state = new CustomInputState();
            state.Buttons[0] = true;

            _fired = null;
            Assert.True(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
            _fired = "TwoFingerTap";
            Assert.False(SourceEvaluator.EvaluateForButtonTarget(state, src, 50, Slot, "ButtonA", 0, null, 0));
        }
    }
}
