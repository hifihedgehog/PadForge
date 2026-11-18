using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Adaptive Trigger Effect action and its Stop (#531): the effects
    /// against the #529 encoder, the values the editor keeps, the settings
    /// round trip, the per-slot store, and both slot evaluators with the
    /// release hook. The store's wake-up is a static the engine points at
    /// the dispatcher, so this class runs with the other macro classes.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class MacroTriggerEffectTests : IDisposable
    {
        public void Dispose() => MacroTriggerEffectState.Changed = null;

        private static byte[] Encoded(int mode, params int[] values)
        {
            var block = new byte[11];
            Assert.True(GameModProtocol.TryEncodeTrigger(mode, values, block), "mode " + mode);
            return block;
        }

        private static MacroAction Effect(int mode, MacroTriggerEffectSide side, MacroTriggerEffectHold hold, int ms = 500)
        {
            var action = new MacroAction { Type = MacroActionType.TriggerEffect };
            action.TriggerEffectMode = mode;
            action.TriggerEffectSide = side;
            action.TriggerEffectHold = hold;
            action.TriggerEffectMs = ms;
            return action;
        }

        private static MacroItem PadMacro(ushort button, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "ATE",
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

        // ── The effects ──

        [Fact]
        public void EachEffect_EncodesAsTheGameModEncoderDoes()
        {
            Assert.Equal(20, TriggerEffectCatalog.Kinds.Count);
            Assert.Equal(TriggerEffectCatalog.Kinds.Count, TriggerEffectCatalog.Kinds.Select(k => k.Mode).Distinct().Count());
            foreach (var kind in TriggerEffectCatalog.Kinds)
            {
                int[] values = TriggerEffectCatalog.Defaults(kind.Mode);
                Assert.Equal(kind.Parameters.Length, values.Length);
                Assert.Equal(Encoded(kind.Mode, values), TriggerEffectCatalog.Encode(kind.Mode, values));
                foreach (var parameter in kind.Parameters)
                {
                    Assert.InRange(parameter.Default, parameter.Min, parameter.Max);
                    Assert.False(string.IsNullOrEmpty(parameter.Label));
                }
                Assert.False(string.IsNullOrEmpty(kind.Display));
            }
        }

        [Fact]
        public void TheEffects_AreTheDsxModesAModCanSend()
        {
            // DSX's duplicates of other modes (19 to 23) and VibrateTrigger
            // (8) stay out of the picker.
            var modes = TriggerEffectCatalog.Kinds.Select(k => k.Mode).OrderBy(m => m).ToArray();
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 13, 14, 15, 16, 17, 18, 24, 25, 26 }, modes);
            Assert.Equal(TriggerEffectCatalog.ResistanceMode, new MacroAction().TriggerEffectMode);
        }

        [Fact]
        public void ValuesTheEncoderRefuses_PlayNothing_AndTheEditorSaysSo()
        {
            var action = Effect(TriggerEffectCatalog.WeaponMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease);
            Assert.True(action.TriggerEffectIsValid);
            // Weapon's end zone must sit past its start.
            action.TriggerEffectRows[0].Value = 6;
            action.TriggerEffectRows[1].Value = 4;
            Assert.False(action.TriggerEffectIsValid);
            Assert.Null(TriggerEffectCatalog.Encode(action.TriggerEffectMode, action.TriggerEffectValues));

            var im = new InputManager();
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, new[] { PadMacro(Gamepad.A, action) });
            Assert.False(im.MacroTriggerEffects[0].Any);
        }

        [Fact]
        public void AParameterRow_HoldsItsValueInsideItsRange()
        {
            var action = Effect(TriggerEffectCatalog.ResistanceMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease);
            var strength = action.TriggerEffectRows[1];
            Assert.Equal(Strings.Instance.Pad_AT_Strength, strength.Label);
            strength.Value = 99;
            Assert.Equal(8, strength.Value);
            strength.Value = -5;
            Assert.Equal(1, strength.Value);
            Assert.Equal("3,1", action.TriggerEffectParameters);
        }

        /// <summary>A parameter's own reset returns that one value to the
        /// effect's default and leaves the others as they are.</summary>
        [Fact]
        public void AParameterRowsReset_RestoresItsOwnDefaultAlone()
        {
            var action = Effect(TriggerEffectCatalog.ResistanceMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease);
            var start = action.TriggerEffectRows[0];
            var strength = action.TriggerEffectRows[1];
            start.Value = 5;
            strength.Value = 2;
            Assert.Equal("5,2", action.TriggerEffectParameters);

            strength.ResetValueCommand.Execute(null);
            Assert.Equal(TriggerEffectCatalog.Defaults(TriggerEffectCatalog.ResistanceMode)[1], strength.Value);
            Assert.Equal(5, start.Value);
        }

        [Fact]
        public void TheEffectsReset_RestoresItsValues_EvenWhenTheEffectIsAlreadyFeedback()
        {
            var action = Effect(TriggerEffectCatalog.ResistanceMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease);
            var strength = action.TriggerEffectRows[1];
            strength.Value = 2;
            Assert.Equal("3,2", action.TriggerEffectParameters);
            var raised = new List<string>();
            strength.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            action.ResetSettingCommand.Execute(nameof(MacroAction.TriggerEffectMode));
            Assert.Equal(TriggerEffectCatalog.ResistanceMode, action.TriggerEffectMode);
            Assert.Equal(TriggerEffectCatalog.Defaults(TriggerEffectCatalog.ResistanceMode), action.TriggerEffectValues);
            Assert.Equal(6, strength.Value);
            Assert.Contains(nameof(TriggerEffectParameterValue.Value), raised);

            // From another effect, the reset returns to Feedback at its values.
            action.TriggerEffectMode = TriggerEffectCatalog.WeaponMode;
            action.TriggerEffectRows[0].Value = 5;
            action.ResetSettingCommand.Execute(nameof(MacroAction.TriggerEffectMode));
            Assert.Equal(TriggerEffectCatalog.ResistanceMode, action.TriggerEffectMode);
            Assert.Equal(TriggerEffectCatalog.Defaults(TriggerEffectCatalog.ResistanceMode), action.TriggerEffectValues);
        }

        [Fact]
        public void ANewEffect_StartsAtItsOwnValues_AndASavedListKeepsItsOwn()
        {
            var action = new MacroAction { Type = MacroActionType.TriggerEffect };
            action.TriggerEffectMode = TriggerEffectCatalog.MachineMode;
            Assert.Equal(TriggerEffectCatalog.Defaults(TriggerEffectCatalog.MachineMode), action.TriggerEffectValues);
            Assert.Equal(6, action.TriggerEffectRows.Count);

            // Loading sets the mode without its values replacing the saved
            // ones, and a short list takes the effect's values for the rest.
            action.LoadTriggerEffect(TriggerEffectCatalog.VibrationMode, "2,5");
            Assert.Equal(new[] { 2, 5, 30 }, action.TriggerEffectValues);
            Assert.Equal(TriggerEffectCatalog.VibrationMode, action.TriggerEffectKind.Mode);
            action.LoadTriggerEffect(TriggerEffectCatalog.SlopeFeedbackMode, null);
            Assert.Equal(TriggerEffectCatalog.Defaults(TriggerEffectCatalog.SlopeFeedbackMode), action.TriggerEffectValues);
        }

        [Fact]
        public void BothActions_RoundTripThroughTheSettingsFile()
        {
            var play = Effect(TriggerEffectCatalog.VibrationMode, MacroTriggerEffectSide.Left, MacroTriggerEffectHold.Duration, 750);
            play.TriggerEffectRows[2].Value = 40;
            var stop = new MacroAction { Type = MacroActionType.TriggerEffectStop, TriggerEffectSide = MacroTriggerEffectSide.Both };
            var macro = new MacroItem { Name = "RT" };
            macro.Actions.Add(play);
            macro.Actions.Add(stop);

            var data = SettingsService.BuildMacroDataForMacro(macro, 0);
            Assert.Equal("0,8,40", data.Actions[0].TriggerEffectParameters);
            // A Stop has no effect to save.
            Assert.Null(data.Actions[1].TriggerEffectParameters);

            var back = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            var playBack = back.Actions[0];
            Assert.Equal(MacroActionType.TriggerEffect, playBack.Type);
            Assert.Equal(MacroTriggerEffectSide.Left, playBack.TriggerEffectSide);
            Assert.Equal(TriggerEffectCatalog.VibrationMode, playBack.TriggerEffectMode);
            Assert.Equal(new[] { 0, 8, 40 }, playBack.TriggerEffectValues);
            Assert.Equal(MacroTriggerEffectHold.Duration, playBack.TriggerEffectHold);
            Assert.Equal(750, playBack.TriggerEffectMs);
            Assert.Equal(MacroActionType.TriggerEffectStop, back.Actions[1].Type);
            Assert.Equal(MacroTriggerEffectSide.Both, back.Actions[1].TriggerEffectSide);
        }

        [Fact]
        public void TheActionsAndTheirChoices_KeepTheirNumbers()
        {
            Assert.Equal(62, (int)MacroActionType.TriggerEffect);
            Assert.Equal(63, (int)MacroActionType.TriggerEffectStop);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<MacroTriggerEffectSide>().Select(v => (int)v));
            Assert.Equal(MacroTriggerEffectSide.Both, (MacroTriggerEffectSide)2);
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<MacroTriggerEffectHold>().Select(v => (int)v));
            Assert.Equal(MacroTriggerEffectHold.UntilStopped, (MacroTriggerEffectHold)2);
        }

        [Fact]
        public void BothActions_SitInTheRumbleGroup_WithTheirWords()
        {
            var rumble = MacroTypeCatalog.Choices.Single(c => c.Type == MacroActionType.Rumble);
            foreach (var type in new[] { MacroActionType.TriggerEffect, MacroActionType.TriggerEffectStop })
            {
                var choice = MacroTypeCatalog.Choices.Single(c => c.Type == type);
                Assert.Equal(rumble.Category, choice.Category);
                Assert.False(string.IsNullOrEmpty(choice.Label));
                Assert.False(string.IsNullOrEmpty(choice.Tooltip));
            }
            var summary = Effect(TriggerEffectCatalog.WeaponMode, MacroTriggerEffectSide.Left, MacroTriggerEffectHold.UntilStopped);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_TriggerEffect_Sticky_Format,
                Strings.Instance.Pad_AT_Weapon, Strings.Instance.MacroAction_TriggerEffect_SideLeft), summary.DisplayText);
        }

        // ── The store ──

        [Fact]
        public void TheStore_PlaysASideOrBoth_AndHandsOutCopies()
        {
            var store = new MacroTriggerEffectState(3);
            var block = Encoded(TriggerEffectCatalog.ResistanceMode, 3, 6);
            store.Play(block, MacroTriggerEffectSide.Right, long.MaxValue, null);
            store.Read(0, out var left, out var right);
            Assert.Null(left);
            Assert.Equal(block, right);
            right[0] = 0xFF;
            store.Read(0, out _, out right);
            Assert.Equal(block, right);

            store.Play(block, MacroTriggerEffectSide.Both, long.MaxValue, null);
            store.Read(0, out left, out right);
            Assert.Equal(block, left);
            store.Stop(MacroTriggerEffectSide.Left);
            store.Read(0, out left, out right);
            Assert.Null(left);
            Assert.NotNull(right);
            store.Clear();
            Assert.False(store.Any);
        }

        [Fact]
        public void TheStore_WakesTheDispatcher_OnlyWhenAProgramChanges()
        {
            var woken = new List<int>();
            MacroTriggerEffectState.Changed = woken.Add;
            var store = new MacroTriggerEffectState(4);
            var soft = Encoded(TriggerEffectCatalog.ResistanceMode, 3, 2);
            var hard = Encoded(TriggerEffectCatalog.ResistanceMode, 3, 8);
            store.Play(soft, MacroTriggerEffectSide.Right, long.MaxValue, null);
            store.Play(soft, MacroTriggerEffectSide.Right, long.MaxValue, null);
            Assert.Equal(new[] { 4 }, woken);
            store.Play(hard, MacroTriggerEffectSide.Right, long.MaxValue, null);
            store.Stop(MacroTriggerEffectSide.Left);
            Assert.Equal(new[] { 4, 4 }, woken);
            store.Stop(MacroTriggerEffectSide.Right);
            Assert.Equal(new[] { 4, 4, 4 }, woken);
        }

        [Fact]
        public void ATimedEffect_EndsAtItsTime_AndKeepsTheWriterAwakeUntilThen()
        {
            var woken = new List<int>();
            MacroTriggerEffectState.Changed = woken.Add;
            var store = new MacroTriggerEffectState(1);
            store.Play(Encoded(TriggerEffectCatalog.VibrationMode, 0, 8, 30), MacroTriggerEffectSide.Right, 1000, null);
            Assert.True(store.AnyTimed);
            store.Read(999, out _, out var right);
            Assert.NotNull(right);
            store.Read(1000, out _, out right);
            Assert.Null(right);
            Assert.False(store.Any);
            Assert.False(store.AnyTimed);
            Assert.Equal(new[] { 1, 1 }, woken);
        }

        [Fact]
        public void TheStore_LetsGoOfAMacrosEffects_ByOwner()
        {
            var store = new MacroTriggerEffectState(0);
            object a = new(), b = new();
            var block = Encoded(TriggerEffectCatalog.ResistanceMode, 3, 6);
            store.Play(block, MacroTriggerEffectSide.Right, long.MaxValue, a);
            store.Play(block, MacroTriggerEffectSide.Left, long.MaxValue, b);
            Assert.True(store.AnyOwned);
            store.ReleaseOwner(a);
            store.Read(0, out var left, out var right);
            Assert.Null(right);
            Assert.NotNull(left);
            store.ReleaseOwnersNotIn(owner => owner == a);
            Assert.False(store.Any);
            Assert.False(store.AnyOwned);
        }

        // ── The evaluators ──

        [Fact]
        public void AnEffectHeldUntilRelease_EndsWhenTheMacrosButtonLetsGo()
        {
            var im = new InputManager();
            var macro = PadMacro(Gamepad.A,
                Effect(TriggerEffectCatalog.ResistanceMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease));
            var macros = new[] { macro };
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            im.MacroTriggerEffects[0].Read(Environment.TickCount64, out _, out var right);
            Assert.Equal(Encoded(TriggerEffectCatalog.ResistanceMode, 3, 6), right);

            // Still held: the macro finished its one run, the effect stays.
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(im.MacroTriggerEffects[0].Any);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(im.MacroTriggerEffects[0].Any);
        }

        [Fact]
        public void TimedAndStickyEffects_OutliveTheRelease_AndTheStopEndsThem()
        {
            var im = new InputManager();
            var timed = PadMacro(Gamepad.A,
                Effect(TriggerEffectCatalog.VibrationMode, MacroTriggerEffectSide.Left, MacroTriggerEffectHold.Duration, 60000));
            var sticky = PadMacro(Gamepad.B,
                Effect(TriggerEffectCatalog.WeaponMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilStopped));
            var stop = PadMacro(Gamepad.X, new MacroAction { Type = MacroActionType.TriggerEffectStop, TriggerEffectSide = MacroTriggerEffectSide.Both });
            var macros = new[] { timed, sticky, stop };

            var gp = new Gamepad { Buttons = (ushort)(Gamepad.A | Gamepad.B) };
            im.EvaluateSlotMacros(ref gp, macros);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            var store = im.MacroTriggerEffects[0];
            store.Read(Environment.TickCount64, out var left, out var right);
            Assert.NotNull(left);
            Assert.NotNull(right);
            Assert.True(store.AnyTimed);
            Assert.False(store.AnyOwned);

            gp = new Gamepad { Buttons = Gamepad.X };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(store.Any);
        }

        [Fact]
        public void TheExtendedLane_PlaysTheEffect_AndItsReleaseEndsIt()
        {
            var im = new InputManager();
            var macro = new MacroItem
            {
                Name = "ATEX",
                IsEnabled = true,
                PadIndex = 0,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            macro.Actions.Add(Effect(TriggerEffectCatalog.SlopeFeedbackMode, MacroTriggerEffectSide.Both, MacroTriggerEffectHold.UntilRelease));
            var macros = new[] { macro };

            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = 1;
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            im.MacroTriggerEffects[0].Read(Environment.TickCount64, out var left, out var right);
            var expected = Encoded(TriggerEffectCatalog.SlopeFeedbackMode, 0, 9, 1, 8);
            Assert.Equal(expected, left);
            Assert.Equal(expected, right);

            raw = RawHidState.Create(8, 32, 1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.False(im.MacroTriggerEffects[0].Any);
        }

        private static readonly MethodInfo RunMacroPass = typeof(InputManager).GetMethod(
            "EvaluateMacros", BindingFlags.Instance | BindingFlags.NonPublic);

        [Fact]
        public void AMacroTurnedOffOrRemoved_LetsGoOfItsEffect()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var im = new InputManager();
            var macro = PadMacro(Gamepad.A,
                Effect(TriggerEffectCatalog.ResistanceMode, MacroTriggerEffectSide.Right, MacroTriggerEffectHold.UntilRelease));
            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, new[] { macro });
            Assert.True(im.MacroTriggerEffects[0].AnyOwned);

            // Turned off: the evaluators skip it, so its release hook never
            // runs again, and the pass's sweep ends the effect.
            im.MacroSnapshots[0] = new[] { macro };
            macro.IsEnabled = false;
            RunMacroPass.Invoke(im, null);
            Assert.False(im.MacroTriggerEffects[0].Any);

            // Gone from the slot, by a profile switch.
            macro.IsEnabled = true;
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, new[] { macro });
            Assert.True(im.MacroTriggerEffects[0].AnyOwned);
            im.MacroSnapshots[0] = Array.Empty<MacroItem>();
            RunMacroPass.Invoke(im, null);
            Assert.False(im.MacroTriggerEffects[0].Any);
        }

        /// <summary>The pass's release sweep costs the poll thread nothing
        /// while no effect is held: no closure per slot per pass.</summary>
        [Fact]
        public void TheMacroPass_AllocatesNothing_WithNoMacrosAndNoHeldEffect()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var im = new InputManager();
            var pass = (Action)Delegate.CreateDelegate(typeof(Action), im, RunMacroPass);
            pass();
            // Counted over windows the runtime left alone (AllocationWindow).
            foreach (long bytes in AllocationWindow.Clean(pass, 1000))
                Assert.Equal(0, bytes);
        }

        [Fact]
        public void BothEvaluators_DispatchBothActions()
        {
            string code = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs"))
                .Replace("\r\n", "\n");
            Assert.Equal(2, Count(code, "case MacroActionType.TriggerEffect:"));
            Assert.Equal(2, Count(code, "case MacroActionType.TriggerEffectStop:"));
            Assert.Equal(2, Count(code, "ApplyTriggerEffectAction(macro, action);"));
            Assert.Equal(2, Count(code, "MacroTriggerEffects[slotIndex].Stop(action.TriggerEffectSide);"));
            Assert.Equal(2, Count(code, "                else\n                    ReleaseHeldTriggerEffects(macro);"));
        }

        private static int Count(string code, string what)
        {
            int n = 0;
            for (int at = code.IndexOf(what, StringComparison.Ordinal); at >= 0; at = code.IndexOf(what, at + what.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found");
        }
    }

    /// <summary>
    /// The effects dispatcher's side of #531: where a macro's program sits
    /// in the Sony pass, and the copy an Xbox pad with Translate Adaptive
    /// Trigger Vibrations reads. The provider and the mods' holds are
    /// process-wide, so this runs with the game mod tests.
    /// </summary>
    [Collection("GameModStatics")]
    public sealed class MacroTriggerEffectDispatchTests : IDisposable
    {
        public MacroTriggerEffectDispatchTests() => GameModEffects.ResetForTest();

        public void Dispose()
        {
            UserEffectsDispatcher.MacroTriggerEffectsProvider = null;
            GameModEffects.ResetForTest();
        }

        private static byte[] Encoded(int mode, params int[] values)
        {
            var block = new byte[11];
            Assert.True(GameModProtocol.TryEncodeTrigger(mode, values, block), "mode " + mode);
            return block;
        }

        [Fact]
        public void AMacrosEffect_FillsOnlyADualSenseTrigger_NothingElseHolds()
        {
            var macroRight = Encoded(TriggerEffectCatalog.WeaponMode, 3, 6, 8);
            var macroLeft = Encoded(TriggerEffectCatalog.ResistanceMode, 3, 6);
            var game = new UserEffectsDispatcher.ExternalSubsystemOverrides { RightTriggerEffect = new byte[11] };

            var fill = UserEffectsDispatcher.FillFromMacroEffects(game, macroLeft, macroRight, isDs5: true);
            Assert.Same(game.RightTriggerEffect, fill.Overrides.RightTriggerEffect);
            Assert.False(fill.RightTrigger);
            Assert.Equal(macroLeft, fill.Overrides.LeftTriggerEffect);
            Assert.True(fill.LeftTrigger);
            Assert.False(fill.MuteLight || fill.PlayerLights);

            var ds4 = UserEffectsDispatcher.FillFromMacroEffects(default, macroLeft, macroRight, isDs5: false);
            Assert.Null(ds4.Overrides.LeftTriggerEffect);
            Assert.Null(ds4.Overrides.RightTriggerEffect);
            Assert.False(ds4.LeftTrigger || ds4.RightTrigger);
        }

        [Fact]
        public void TheImpulseTranslation_ReadsAMacrosProgram()
        {
            var store = new MacroTriggerEffectState(5);
            var block = Encoded(TriggerEffectCatalog.VibrationMode, 0, 8, 30);
            store.Play(block, MacroTriggerEffectSide.Right, long.MaxValue, null);
            var left = new byte[11];
            var right = new byte[11];

            Assert.False(UserEffectsDispatcher.TryGetInboundTriggerEffects(5, left, right));
            UserEffectsDispatcher.MacroTriggerEffectsProvider = slot => slot == 5 ? store : null;
            Assert.True(UserEffectsDispatcher.TryGetInboundTriggerEffects(5, left, right));
            Assert.Equal(block, right);
            Assert.Equal(new byte[11], left);

            store.Clear();
            Assert.False(UserEffectsDispatcher.TryGetInboundTriggerEffects(5, left, right));
        }
    }

    /// <summary>
    /// The trigger effect editor's parameter captions follow a language
    /// change (#531). A switch is process-wide, so this runs with the other
    /// culture tests.
    /// </summary>
    [Collection("CultureSwitching")]
    public sealed class MacroTriggerEffectCultureTests
    {
        [Fact]
        public void TheParameterCaptions_FollowALanguageChange()
        {
            var before = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("en"));
                var action = new MacroAction { Type = MacroActionType.TriggerEffect };
                var rows = action.TriggerEffectRows;
                Assert.Equal(2, rows.Count);
                var raised = new List<string>();
                foreach (var row in rows)
                    row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                string english = rows[1].Label;

                Strings.ChangeCulture(System.Globalization.CultureInfo.GetCultureInfo("de"));

                // The rows are the same objects, so the view rereads each
                // caption only on its own Label notification.
                Assert.Same(rows[1], action.TriggerEffectRows[1]);
                Assert.Equal(rows.Count, raised.Count(n => n == nameof(TriggerEffectParameterValue.Label)));
                Assert.NotEqual(english, rows[1].Label);
                Assert.Equal(Strings.Instance.Pad_AT_Strength, rows[1].Label);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}
