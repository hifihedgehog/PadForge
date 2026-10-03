using System;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Toggle layer's Auto-Cancel After Inactivity (#206) switches the layer
    /// off once none of its rows produce output. Every row family counts: the
    /// gamepad rows and the rows the four TryEvaluateMappingSet* evaluators
    /// write (Extended, keyboard and mouse, MIDI, VR, touchpad, Motion). Each
    /// test holds the layer's input past the timeout, then lets it rest past
    /// the timeout, so the layer must stay on and then switch off.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class LayerAutoCancelActivityTests
    {
        private const int AutoCancelMs = 300;
        private const int Frames = 25;   // 25 x 20 ms, past the timeout

        private static MappingSource Src(string descriptor) => new() { Descriptor = descriptor };

        private static MappingSet LayerSet(string target, params MappingSource[] sources)
        {
            var ms = new MappingSet();
            var row = new MappingRow { Target = target, LayerMask = "View" };
            row.Sources.AddRange(sources);
            ms.Rows.Add(row);
            ms.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Button 28", Mode = "Toggle",
                LayerMask = "View", LayerName = "View", Kind = "Button",
                DelayMs = 0, AutoCancelMs = AutoCancelMs,
            });
            return ms;
        }

        /// <summary>Engages the layer, then runs frames in poll order: the
        /// activator tick (which applies auto-cancel), then the row.</summary>
        private static string Run(int slot, MappingSet ms, CustomInputState state, Action evaluate, bool engage)
        {
            if (engage)
            {
                state.Buttons[28] = true;
                Assert.Equal("View", InputManager.ResolveActiveLayerMask(slot, ms, state, ""));
                state.Buttons[28] = false;
            }
            string mask = "";
            for (int i = 0; i < Frames; i++)
            {
                Thread.Sleep(20);
                mask = InputManager.ResolveActiveLayerMask(slot, ms, state, "");
                evaluate();
            }
            return mask;
        }

        private static void HeldThenRested(int slot, MappingSet ms, CustomInputState state,
            Action press, Action release, Action evaluate)
        {
            InputManager.ClearAllShiftRuntime();
            try
            {
                press();
                Assert.Equal("View", Run(slot, ms, state, evaluate, engage: true));
                release();
                Assert.Equal("Base", Run(slot, ms, state, evaluate, engage: false));
            }
            finally { InputManager.ClearAllShiftRuntime(); }
        }

        [Fact]
        public void AGamepadRowKeepsItsLayerOn()
        {
            var ms = LayerSet("ButtonA", Src("Button 16"));
            var state = new CustomInputState();
            var apply = typeof(InputManager).GetMethod("ApplyMappingSetToGamepad", BindingFlags.Static | BindingFlags.NonPublic);
            HeldThenRested(3, ms, state,
                () => state.Buttons[16] = true, () => state.Buttons[16] = false,
                () => apply.Invoke(null, new object[] { state, ms, "", 50, 3, new Gamepad() }));
        }

        [Fact]
        public void AnExtendedButtonRowKeepsItsLayerOn()
        {
            var ms = LayerSet("RawBtn60", Src("Button 16"));
            var state = new CustomInputState();
            HeldThenRested(4, ms, state,
                () => state.Buttons[16] = true, () => state.Buttons[16] = false,
                () => InputManager.TryEvaluateMappingSetButton(state, ms, "", 4, "RawBtn60", 50, out _));
        }

        [Fact]
        public void ATwoSourceButtonRowKeepsItsLayerOn()
        {
            var ms = LayerSet("RawBtn60", Src("Button 16"), Src("Button 17"));
            var state = new CustomInputState();
            HeldThenRested(5, ms, state,
                () => state.Buttons[16] = true, () => state.Buttons[16] = false,
                () => InputManager.TryEvaluateMappingSetButton(state, ms, "", 5, "RawBtn60", 50, out _));
        }

        [Fact]
        public void AnAxisRowKeepsItsLayerOn()
        {
            var ms = LayerSet("RawAxis0", Src("Button 16"));
            var state = new CustomInputState();
            HeldThenRested(6, ms, state,
                () => state.Buttons[16] = true, () => state.Buttons[16] = false,
                () => InputManager.TryEvaluateMappingSetBipolarAxis(state, ms, "", 6, "RawAxis0", out _));
        }

        [Fact]
        public void ATriggerRowKeepsItsLayerOn()
        {
            var ms = LayerSet("RawAxis5", Src("Button 16"));
            var state = new CustomInputState();
            HeldThenRested(7, ms, state,
                () => state.Buttons[16] = true, () => state.Buttons[16] = false,
                () => InputManager.TryEvaluateMappingSetRawTrigger(state, ms, "", 7, "RawAxis5", out _));
        }

        private static readonly MethodInfo BeginFrame = typeof(InputManager).GetMethod(
            "BeginFrameMultiSourceTracking", BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>One poll frame of a keyboard-and-mouse rate lane: the
        /// frame stamp the poll loop starts each frame with, then the
        /// lane.</summary>
        private static Action Lane(string name, CustomInputState state, MappingSet ms, int slot)
        {
            var lane = typeof(InputManager).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(lane);
            return () =>
            {
                BeginFrame.Invoke(null, null);
                lane.Invoke(null, new object[] { state, ms, "", slot });
            };
        }

        /// <summary>A layer's gyro mouse row aims through the gyro rate lane,
        /// not a row evaluator, and the lane recorded no output, so Auto-Cancel
        /// switched the layer off while the controller turned.</summary>
        [Fact]
        public void AGyroMouseRowKeepsItsLayerOn()
        {
            var ms = LayerSet("KbmMouseX", Src("Gyro Yaw"));
            var state = new CustomInputState();
            HeldThenRested(9, ms, state,
                () => state.Gyro[0] = state.Gyro[1] = state.Gyro[2] = 3f,
                () => state.Gyro[0] = state.Gyro[1] = state.Gyro[2] = 0f,
                Lane("TickGyroMouseSources", state, ms, 9));
        }

        /// <summary>A finger resting on a trackpad mouse row is output, as it
        /// is on a touchpad row, though the cursor does not move.</summary>
        [Fact]
        public void ATrackpadMouseRowKeepsItsLayerOnWithAFingerAtRest()
        {
            var ms = LayerSet("KbmMouseX", Src("Touchpad 0 Finger 0 X"));
            var pad = new TouchpadInputState(2);
            pad.FingerX[0] = 0.5f;
            pad.FingerY[0] = 0.5f;
            var state = new CustomInputState { Touchpads = new[] { pad } };
            HeldThenRested(10, ms, state,
                () => pad.FingerDown[0] = true, () => pad.FingerDown[0] = false,
                Lane("TickTouchpadMouseSources", state, ms, 10));
        }

        /// <summary>A windowed trackpad mouse row reads a finger only inside
        /// its window, so a finger resting inside keeps the layer on, and
        /// one resting outside is no output and lets it switch off.</summary>
        [Fact]
        public void AWindowedTrackpadMouseRowCountsOnlyAFingerInItsWindow()
        {
            var ms = LayerSet("KbmMouseX", Src("Touchpad 0 Finger 0 X Left"));
            var pad = new TouchpadInputState(2);
            pad.FingerY[0] = 0.5f;
            var state = new CustomInputState { Touchpads = new[] { pad } };
            var lane = Lane("TickTouchpadMouseSources", state, ms, 11);

            pad.FingerX[0] = 0.25f;
            HeldThenRested(11, ms, state,
                () => pad.FingerDown[0] = true, () => pad.FingerDown[0] = false, lane);

            pad.FingerX[0] = 0.75f;
            InputManager.ClearAllShiftRuntime();
            try
            {
                pad.FingerDown[0] = true;
                Assert.Equal("Base", Run(11, ms, state, lane, engage: true));
            }
            finally
            {
                pad.FingerDown[0] = false;
                InputManager.ClearAllShiftRuntime();
            }
        }

        /// <summary>A flick stick held past its threshold is output between
        /// flicks, as a held stick is on a stick row.</summary>
        [Fact]
        public void AFlickStickRowKeepsItsLayerOnWhileItsStickIsHeld()
        {
            var ms = LayerSet("KbmMouseX", Src("Flick Stick Right"));
            var state = new CustomInputState();
            state.Axis[3] = state.Axis[4] = 32768;
            HeldThenRested(11, ms, state,
                () => state.Axis[3] = 65535, () => state.Axis[3] = 32768,
                Lane("TickFlickStickSources", state, ms, 11));
        }

        /// <summary>An engaged absolute touchpad pointer is output.</summary>
        [Fact]
        public void AnEngagedTouchpadPointerKeepsItsLayerOn()
        {
            var ms = LayerSet("KbmMouseX", Src("Touchpad 0 Pointer X"));
            var pad = new TouchpadInputState(2);
            pad.FingerX[0] = 0.5f;
            pad.FingerY[0] = 0.5f;
            var state = new CustomInputState { Touchpads = new[] { pad } };
            var find = typeof(InputManager).GetMethod("FindEngagedTouchpadPointerSource", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(find);
            HeldThenRested(12, ms, state,
                () => pad.FingerDown[0] = true, () => pad.FingerDown[0] = false,
                () => find.Invoke(null, new object[] { state, ms, "KbmMouseX", 12, "" }));
        }

        [Fact]
        public void ATouchpadRowKeepsItsLayerOnWithAFingerAtTheCenter()
        {
            var ms = LayerSet("TouchpadX1", Src("Touchpad 0 Finger 0 X"));
            var pad = new TouchpadInputState(2);
            pad.FingerX[0] = 0.5f;
            pad.FingerY[0] = 0.5f;
            var state = new CustomInputState { Touchpads = new[] { pad } };
            HeldThenRested(8, ms, state,
                () => pad.FingerDown[0] = true, () => pad.FingerDown[0] = false,
                () => InputManager.TryEvaluateMappingSetTouchpadAxis(state, ms, "", 8, "TouchpadX1", 0, out _));
        }
    }
}
