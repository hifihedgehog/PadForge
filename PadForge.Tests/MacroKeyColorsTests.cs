using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Set Key Colors and Clear Key Colors (#557): the lights an action
    /// names, the per-slot store and the frame it paints over the Lighting
    /// tab's colors, both slot evaluators with the release hook, the
    /// settings round trip, the action picker and the editor's key picker.
    /// The store's wake-up is a static the engine points at the dispatcher,
    /// so this class runs with the other macro classes.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MacroKeyColorsTests : IDisposable
    {
        public void Dispose() => MacroKeyColorState.Changed = null;

        private static readonly LightMap Keys = new(new[]
        {
            new LightInfo(0, LightKind.Key, 0x1E, LightKeySource.Device),   // 1
            new LightInfo(1, LightKind.Key, 0x1F, LightKeySource.Device),   // 2
            new LightInfo(2, LightKind.Key, 0x20, LightKeySource.Device),   // 3
            new LightInfo(3, LightKind.Key, 0x21, LightKeySource.Device),   // 4
            new LightInfo(4, LightKind.Key, 0x04, LightKeySource.Device),   // A
            new LightInfo(5, LightKind.Logo, 0, LightKeySource.None),
        }, "test");

        private static KeyColorTarget[] Row => KeyColorTarget.Parse("k1E,k1F,k20,k21");

        private static MacroAction SetColors(string targets, int rgb, MacroKeyColorStyle style = MacroKeyColorStyle.Steady,
            MacroKeyColorHold hold = MacroKeyColorHold.Duration, int ms = 3000, MacroKeyColorRest rest = MacroKeyColorRest.Keep)
        {
            var action = new MacroAction
            {
                Type = MacroActionType.SetKeyColors,
                LightbarR = (byte)(rgb >> 16),
                LightbarG = (byte)(rgb >> 8),
                LightbarB = (byte)rgb,
                KeyColorStyle = style,
                KeyColorHold = hold,
                KeyColorMs = ms,
                KeyColorRest = rest,
            };
            action.KeyColorTargets = targets;
            return action;
        }

        private static MacroItem PadMacro(ushort button, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "KC",
                IsEnabled = true,
                PadIndex = 0,
                TriggerButtons = button,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            foreach (var action in actions) m.Actions.Add(action);
            return m;
        }

        // ── The lights an action names ──

        [Fact]
        public void TheTargetsKeepTheirOrder_DropRepeatsAndWhatTheReaderDoesNotKnow()
        {
            var targets = KeyColorTarget.Parse("k21, k1e,gedge,k21,gbogus,kZZ,k00,gzone3,all,x");
            Assert.Equal("k21,k1E,gedge,gzone3,all", KeyColorTarget.Format(targets));
            Assert.Equal(0x21, targets[0].Usage);
            Assert.True(targets[^1].IsAll);
            Assert.Empty(KeyColorTarget.Parse(null));
            Assert.Equal(string.Empty, KeyColorTarget.Format(null));

            var action = new MacroAction { Type = MacroActionType.SetKeyColors };
            action.KeyColorTargets = "k04,k04,gedge";
            Assert.Equal("k04,gedge", action.KeyColorTargets);
            Assert.Equal(2, action.KeyColorTargetCount);
        }

        // ── The store ──

        [Fact]
        public void ASteadyActionPaintsItsKeys_OverWhatIsBeneath()
        {
            var store = new MacroKeyColorState(0);
            var layer = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0x00FF00 }, null, null, 0x101010);
            Assert.Same(layer, store.Compose(layer, 0));

            store.Play(new object(), Row, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, 1000, null);
            var frame = store.Compose(layer, 500);
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000, 0x00FF00, 0x101010 },
                LightRenderer.Render(Keys, 0x111111, frame));

            // Past its time it ends, and what was beneath shows again.
            Assert.Same(layer, store.Compose(layer, 1000));
            Assert.False(store.Any);
        }

        [Fact]
        public void TheOtherLights_CanBeKept_TurnedOff_OrGivenTheSecondColor()
        {
            var layer = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0x00FF00 }, null, null, null);
            var off = new MacroKeyColorState(0);
            off.Play(new object(), Row, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Off, 0, 0, long.MaxValue, null);
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000, 0, 0 },
                LightRenderer.Render(Keys, 0x111111, off.Compose(layer, 0)));

            var color = new MacroKeyColorState(0);
            color.Play(new object(), Row, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Color, 0x0000FF, 0, long.MaxValue, null);
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000, 0x0000FF, 0x0000FF },
                LightRenderer.Render(Keys, 0x111111, color.Compose(layer, 0)));

            var all = new MacroKeyColorState(0);
            all.Play(new object(), new[] { KeyColorTarget.All }, 0xABCDEF, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, null);
            Assert.All(LightRenderer.Render(Keys, 0x111111, all.Compose(layer, 0)), c => Assert.Equal(0xABCDEF, c));
        }

        [Fact]
        public void AFadeDimsOverItsTime()
        {
            var store = new MacroKeyColorState(0);
            store.Play(new object(), Row, 0xC86432, MacroKeyColorStyle.Fade, MacroKeyColorRest.Keep, 0, 1000, 2000, null);
            Assert.Equal(0xC86432, LightRenderer.Render(Keys, 0, store.Compose(null, 1000))[0]);
            Assert.Equal(0x643219, LightRenderer.Render(Keys, 0, store.Compose(null, 1500))[0]);
            Assert.Equal(0x140A05, LightRenderer.Render(Keys, 0, store.Compose(null, 1900))[0]);
        }

        [Fact]
        public void ACountdownTurnsItsKeysOffOneByOne_TheLastPickedFirst()
        {
            var store = new MacroKeyColorState(0);
            store.Play(new object(), Row, 0xFF0000, MacroKeyColorStyle.Countdown, MacroKeyColorRest.Keep, 0, 0, 4000, null);
            int[] At(long ms) => LightRenderer.Render(Keys, 0x111111, store.Compose(null, ms)).Take(4).ToArray();
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000 }, At(0));
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0xFF0000 }, At(999));
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0xFF0000, 0 }, At(1000));
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0, 0 }, At(2500));
            Assert.Equal(new[] { 0xFF0000, 0, 0, 0 }, At(3999));
            // Ended: the keys show what is beneath, here the claim's color.
            Assert.Equal(new[] { 0x111111, 0x111111, 0x111111, 0x111111 }, At(4000));
        }

        [Fact]
        public void NewerActionsGoOnTop_AndFiringOneAgainStartsItOverOnTop()
        {
            var store = new MacroKeyColorState(0);
            object red = new(), blue = new();
            store.Play(red, KeyColorTarget.Parse("k1E,k1F"), 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, null);
            store.Play(blue, KeyColorTarget.Parse("k1F,k20"), 0x0000FF, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, null);
            Assert.Equal(new[] { 0xFF0000, 0x0000FF, 0x0000FF }, LightRenderer.Render(Keys, 0, store.Compose(null, 0)).Take(3));

            store.Play(red, KeyColorTarget.Parse("k1E,k1F"), 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 10, 5000, null);
            Assert.Equal(new[] { 0xFF0000, 0xFF0000, 0x0000FF }, LightRenderer.Render(Keys, 0, store.Compose(null, 20)).Take(3));
            Assert.True(store.AnyTimed);
        }

        [Fact]
        public void AHeldActionFiredAgainChangesNothing_AndWakesNothing()
        {
            int wakes = 0;
            MacroKeyColorState.Changed = _ => wakes++;
            var store = new MacroKeyColorState(0);
            object action = new(), macro = new();
            store.Play(action, Row, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, macro);
            store.Play(action, Row, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 5, long.MaxValue, macro);
            Assert.Equal(1, wakes);
            Assert.True(store.AnyOwned);
            Assert.False(store.AnyTimed);

            store.ReleaseOwner(new object());
            Assert.Equal(1, wakes);
            store.ReleaseOwner(macro);
            Assert.False(store.Any);
            Assert.Equal(2, wakes);
        }

        [Fact]
        public void ClearEndsTheActionsThatLightAKeyItNames_OrEveryOne()
        {
            var store = new MacroKeyColorState(0);
            store.Play(new object(), KeyColorTarget.Parse("k1E"), 1, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, null);
            store.Play(new object(), KeyColorTarget.Parse("k04,glogo"), 2, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, long.MaxValue, null);
            store.Clear(KeyColorTarget.Parse("glogo"));
            var shown = LightRenderer.Render(Keys, 0, store.Compose(null, 0));
            Assert.Equal(1, shown[0]);
            Assert.Equal(0, shown[4]);

            store.Clear(Array.Empty<KeyColorTarget>());
            Assert.False(store.Any);
        }

        [Fact]
        public void AnActionThatNamesNothing_PlaysNothing()
        {
            var store = new MacroKeyColorState(0);
            store.Play(new object(), Array.Empty<KeyColorTarget>(), 1, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, 0, 100, null);
            Assert.False(store.Any);
        }

        // ── The evaluators ──

        [Fact]
        public void AnActionHeldUntilRelease_EndsWhenTheMacrosButtonLetsGo()
        {
            var im = new InputManager();
            var macros = new[] { PadMacro(Gamepad.A, SetColors("k1E,k1F", 0xFF0000, hold: MacroKeyColorHold.UntilRelease)) };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            var store = im.MacroKeyColors[0];
            Assert.True(store.AnyOwned);
            Assert.Equal(0xFF0000, LightRenderer.Render(Keys, 0, store.Compose(null, Environment.TickCount64))[0]);

            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(store.Any);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(store.Any);
        }

        [Fact]
        public void ACountdownOutlivesTheRelease_AndClearKeyColorsEndsIt()
        {
            var im = new InputManager();
            var countdown = PadMacro(Gamepad.A, SetColors("k1E,k1F,k20", 0x00FF00, MacroKeyColorStyle.Countdown, ms: 60000));
            var clear = PadMacro(Gamepad.B, new MacroAction { Type = MacroActionType.ClearKeyColors, KeyColorTargets = "k20" });
            var macros = new[] { countdown, clear };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            var store = im.MacroKeyColors[0];
            Assert.True(store.AnyTimed);
            Assert.False(store.AnyOwned);

            gp = new Gamepad { Buttons = Gamepad.B };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(store.Any);
        }

        [Fact]
        public void TheExtendedLane_PlaysTheColors_AndItsReleaseEndsThem()
        {
            var im = new InputManager();
            var macro = new MacroItem
            {
                Name = "KCX",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            macro.Actions.Add(SetColors("all", 0x123456, hold: MacroKeyColorHold.UntilRelease));
            var macros = new[] { macro };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.All(LightRenderer.Render(Keys, 0, im.MacroKeyColors[0].Compose(null, Environment.TickCount64)),
                c => Assert.Equal(0x123456, c));

            raw = RawHidState.Create(8, 32, 1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(im.MacroKeyColors[0].Any);
        }

        // ── Saving, the picker, the words ──

        [Fact]
        public void BothActions_RoundTripThroughTheSettingsFile()
        {
            var set = SetColors("k21,k1E,gzone2", 0x112233, MacroKeyColorStyle.Countdown, MacroKeyColorHold.UntilCleared, 45000,
                MacroKeyColorRest.Color);
            set.KeyRestR = 1;
            set.KeyRestG = 2;
            set.KeyRestB = 3;
            var clear = new MacroAction { Type = MacroActionType.ClearKeyColors, KeyColorTargets = "glogo" };
            var other = new MacroAction { Type = MacroActionType.Delay };
            var macro = new MacroItem { Name = "KC" };
            macro.Actions.Add(set);
            macro.Actions.Add(clear);
            macro.Actions.Add(other);

            var data = SettingsService.BuildMacroDataForMacro(macro, 0);
            Assert.Equal("k21,k1E,gzone2", data.Actions[0].KeyColorTargets);
            // An action of another kind saves no lights.
            Assert.Null(data.Actions[2].KeyColorTargets);

            var back = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            var setBack = back.Actions[0];
            Assert.Equal(MacroActionType.SetKeyColors, setBack.Type);
            Assert.Equal("k21,k1E,gzone2", setBack.KeyColorTargets);
            Assert.Equal(MacroKeyColorStyle.Countdown, setBack.KeyColorStyle);
            Assert.Equal(MacroKeyColorHold.UntilCleared, setBack.KeyColorHold);
            Assert.Equal(MacroKeyColorHold.Duration, setBack.EffectiveKeyColorHold);
            Assert.Equal(45000, setBack.KeyColorMs);
            Assert.Equal(MacroKeyColorRest.Color, setBack.KeyColorRest);
            Assert.Equal((byte)2, setBack.KeyRestG);
            Assert.Equal(0x11, setBack.LightbarR);
            Assert.Equal("glogo", back.Actions[1].KeyColorTargets);
        }

        [Fact]
        public void TheActionsAndTheirChoices_KeepTheirNumbers()
        {
            Assert.Equal(64, (int)MacroActionType.SetKeyColors);
            Assert.Equal(65, (int)MacroActionType.ClearKeyColors);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<MacroKeyColorStyle>().Select(v => (int)v));
            Assert.Equal(MacroKeyColorStyle.Countdown, (MacroKeyColorStyle)2);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<MacroKeyColorHold>().Select(v => (int)v));
            Assert.Equal(MacroKeyColorHold.UntilCleared, (MacroKeyColorHold)2);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<MacroKeyColorRest>().Select(v => (int)v));
            Assert.Equal(MacroKeyColorRest.Color, (MacroKeyColorRest)2);
        }

        [Fact]
        public void BothActions_SitWithTheLightingActions_WithTheirWords()
        {
            var chroma = MacroTypeCatalog.Choices.Single(c => c.Type == MacroActionType.SetChromaColor);
            foreach (var type in new[] { MacroActionType.SetKeyColors, MacroActionType.ClearKeyColors })
            {
                var choice = MacroTypeCatalog.Choices.Single(c => c.Type == type);
                Assert.Equal(chroma.Category, choice.Category);
                Assert.False(string.IsNullOrEmpty(choice.Label));
                Assert.False(string.IsNullOrEmpty(choice.Tooltip));
            }
            var countdown = SetColors("k1E,k1F", 0xFF0000, MacroKeyColorStyle.Countdown, ms: 5000);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_SetKeyColors_Countdown_Format, 2, "#FF0000", 5000),
                countdown.DisplayText);
            var held = SetColors("k1E", 0x00FF00, hold: MacroKeyColorHold.UntilRelease);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_SetKeyColors_Held_Format, 1, "#00FF00"), held.DisplayText);
            Assert.Equal(Strings.Instance.MacroAction_ClearKeyColors_All,
                new MacroAction { Type = MacroActionType.ClearKeyColors }.DisplayText);
        }

        [Fact]
        public void ThePickerKeepsTheOrderPicked_AndNumbersIt()
        {
            var action = SetColors("", 0xFF0000);
            var picker = action.KeyTargetPicker;
            IKeyLightsSource source = picker;
            int a = picker.Map.IndexesOf(0x04)[0], w = picker.Map.IndexesOf(0x1A)[0], one = picker.Map.IndexesOf(0x1E)[0];
            source.Toggle(w);
            source.Toggle(a);
            Assert.Equal("k1A,k04", action.KeyColorTargets);
            Assert.Equal(1, source.OrderOf(w));
            Assert.Equal(2, source.OrderOf(a));
            Assert.Equal(0, source.OrderOf(one));
            Assert.Equal(0xFF0000, source.OwnColorOf(a));
            Assert.Null(source.OwnColorOf(one));

            Assert.True(source.TogglePressed(0x1E));
            Assert.Equal("k1A,k04,k1E", action.KeyColorTargets);
            source.Toggle(w);
            Assert.Equal("k04,k1E", action.KeyColorTargets);

            picker.SelectNumberRowCommand.Execute(null);
            Assert.Equal("k1E,k1F,k20,k21,k22,k23,k24,k25,k26,k27", action.KeyColorTargets);

            picker.AllLights = true;
            Assert.Equal("all", action.KeyColorTargets);
            Assert.All(Enumerable.Range(0, picker.Map.Lights.Count), i => Assert.Equal(0xFF0000, source.OwnColorOf(i)));
            source.Toggle(a);
            Assert.False(picker.AllLights);
            Assert.Equal("k04", action.KeyColorTargets);
        }

        [Fact]
        public void ThePickerKeepsASavedLightItHasNoPlaceFor_AndOffersEveryGroup()
        {
            var action = SetColors("k04,gzone9", 0xFF0000);
            var picker = action.KeyTargetPicker;
            IKeyLightsSource source = picker;
            source.Toggle(picker.Map.IndexesOf(0x1A)[0]);
            Assert.Equal("k04,k1A,gzone9", action.KeyColorTargets);

            var map = KeyTargetPickerViewModel.GenericMap(KeyboardFamilies.FullsizeAnsi, 4);
            var groups = map.Lights.Where(l => l.Kind != LightKind.Key).Select(l => l.Group).ToArray();
            Assert.Equal(new[] { "edge", "logo", "status", "other", "zone1", "zone2", "zone3", "zone4" }, groups);
            // Every key the layout draws, once.
            Assert.Equal(map.KeyCount, map.Lights.Count(l => l.Kind == LightKind.Key));
            Assert.True(map.KeyCount >= 100);
        }
    }
}
