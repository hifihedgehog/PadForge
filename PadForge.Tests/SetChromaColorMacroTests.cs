using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Set Chroma Color macro action (issue #468). Both dispatch loops
    /// carry it, and it asserts its color for its own slot on every frame it
    /// is current, which the Chroma worker paints onto the Razer devices
    /// assigned to that slot (#494, ChromaBackendTests pin that half). The
    /// loops' sink is swapped for a recorder here, so these tests never
    /// touch the process-wide assertion state.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class SetChromaColorMacroTests
    {
        private static MacroAction Chroma(byte r, byte g, byte b, int durationMs = 60000) => new()
        {
            Type = MacroActionType.SetChromaColor,
            LightbarR = r,
            LightbarG = g,
            LightbarB = b,
            DurationMs = durationMs,
        };

        private static MacroItem GamepadMacro(string name, ushort button, MacroAction action)
        {
            var m = new MacroItem
            {
                Name = name,
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = button,
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(action);
            return m;
        }

        /// <summary>Runs <paramref name="body"/> with the loops' Chroma sink
        /// recording every assertion's color, and the slots it named in
        /// <paramref name="slots"/>.</summary>
        private static List<(byte R, byte G, byte B)> Recording(Action body, List<int> slots = null)
        {
            var calls = new List<(byte, byte, byte)>();
            var saved = InputManager.MacroChromaSink;
            InputManager.MacroChromaSink = (slot, r, g, b) =>
            {
                calls.Add((r, g, b));
                slots?.Add(slot);
            };
            try { body(); }
            finally { InputManager.MacroChromaSink = saved; }
            return calls;
        }

        /// <summary>The color is the macro's own slot's (#494): the Chroma
        /// worker paints the Razer devices assigned to that slot only.</summary>
        [Fact]
        public void TheAssertion_CarriesTheMacrosSlot()
        {
            var im = new InputManager();
            var m = GamepadMacro("slot two", Gamepad.A, Chroma(9, 8, 7));
            m.PadIndex = 2;
            var slots = new List<int>();
            var calls = Recording(() =>
            {
                var gp = new Gamepad { Buttons = Gamepad.A };
                im.EvaluateSlotMacros(ref gp, new[] { m });
            }, slots);
            Assert.Equal(((byte)9, (byte)8, (byte)7), Assert.Single(calls));
            Assert.Equal(2, Assert.Single(slots));

            // The production sink is the facade's per-slot assertion.
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(),
                "PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"));
            Assert.Contains("NoteChromaColor(macro.PadIndex, action.LightbarR, action.LightbarG, action.LightbarB);", src);
            Assert.Equal(2, src.Split("NoteChromaColor(macro.PadIndex,").Length - 1);
            // Each loop hands the frame's colors to the sink once, after it.
            Assert.Equal(2, src.Split("FlushChromaColors();").Length - 1);
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralOutputs.AssertChromaMacro;", src);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        [Fact]
        public void TheGamepadLoop_AssertsTheColorEveryFrameWhileHeld()
        {
            var im = new InputManager();
            var macros = new[] { GamepadMacro("green", Gamepad.A, Chroma(0, 255, 0)) };
            var calls = Recording(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var gp = new Gamepad { Buttons = Gamepad.A };
                    im.EvaluateSlotMacros(ref gp, macros);
                }
            });
            Assert.Equal(3, calls.Count);
            Assert.All(calls, c => Assert.Equal(((byte)0, (byte)255, (byte)0), c));

            // Released: the action stops, so the assertions stop and the
            // service lets the color go.
            var after = Recording(() =>
            {
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                im.EvaluateSlotMacros(ref gp, macros);
            });
            Assert.Empty(after);
        }

        /// <summary>The Extended loop is the gamepad loop's mirror. A case
        /// present in only one loop is inert on the other's slots.</summary>
        [Fact]
        public void TheExtendedLoop_AssertsTheColorToo()
        {
            var im = new InputManager();
            var m = new MacroItem
            {
                Name = "raw",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.WhileHeld,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            m.Actions.Add(Chroma(10, 20, 30));
            var calls = Recording(() =>
            {
                var raw = RawHidState.Create(8, 32, 1);
                raw.Buttons[0] = 1;
                im.EvaluateSlotMacrosExtended(ref raw, new[] { m });
            });
            Assert.Equal(((byte)10, (byte)20, (byte)30), Assert.Single(calls));
        }

        /// <summary>A soft-press macro and a full-press macro both current
        /// in one frame: the sink hears the color of the one evaluated last,
        /// once, so the color a reader sees never flips between the two
        /// within a frame.</summary>
        [Fact]
        public void TwoMacrosInOneFrame_TheSinkHearsTheLastColorOnce()
        {
            var im = new InputManager();
            var soft = GamepadMacro("soft", Gamepad.A, Chroma(0, 255, 0));
            var full = GamepadMacro("full", Gamepad.B, Chroma(255, 0, 0));
            var calls = Recording(() =>
            {
                for (int frame = 0; frame < 3; frame++)
                {
                    var gp = new Gamepad { Buttons = (ushort)(Gamepad.A | Gamepad.B) };
                    im.EvaluateSlotMacros(ref gp, new[] { soft, full });
                }
            });
            Assert.Equal(3, calls.Count);
            Assert.All(calls, c => Assert.Equal(((byte)255, (byte)0, (byte)0), c));
        }

        [Fact]
        public void TheDuration_EndsTheAction()
        {
            var im = new InputManager();
            var m = GamepadMacro("blink", Gamepad.A, Chroma(1, 2, 3, durationMs: 0));
            m.TriggerMode = MacroTriggerMode.OnPress;
            m.RepeatMode = MacroRepeatMode.Once;
            var calls = Recording(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var gp = new Gamepad { Buttons = Gamepad.A };
                    im.EvaluateSlotMacros(ref gp, new[] { m });
                }
            });
            Assert.Single(calls);
        }

        [Fact]
        public void TheColorAndHold_SurviveSaveAndLoad()
        {
            var item = new MacroItem { Name = "rt" };
            item.Actions.Add(Chroma(0x12, 0x34, 0x56, durationMs: 750));
            var data = SettingsService.BuildMacroDataForMacro(item, 0);
            var back = Assert.Single(SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null).Actions);
            Assert.Equal(MacroActionType.SetChromaColor, back.Type);
            Assert.Equal(0x12, back.LightbarR);
            Assert.Equal(0x34, back.LightbarG);
            Assert.Equal(0x56, back.LightbarB);
            Assert.Equal(750, back.DurationMs);
        }

        [Fact]
        public void TheEditor_ShowsTheColorCardAndTheDurationRow()
        {
            var a = Chroma(0, 255, 0, durationMs: 500);
            Assert.True(a.IsSetChromaColorType);
            Assert.True(a.IsDurationType);
            Assert.False(a.IsSwitchLayerType);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_SetChromaColor_Format, "#00FF00", 500), a.DisplayText);

            var choice = Assert.Single(MacroTypeCatalog.Choices, c => c.Type == MacroActionType.SetChromaColor);
            Assert.Equal(Strings.Instance.Macro_Cat_Leds, choice.Category);
            Assert.Equal(Strings.Instance.MacroAction_SetChromaColor_Tooltip, choice.Tooltip);
        }
    }
}
