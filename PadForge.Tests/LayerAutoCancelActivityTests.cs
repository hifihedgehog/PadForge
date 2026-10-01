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
