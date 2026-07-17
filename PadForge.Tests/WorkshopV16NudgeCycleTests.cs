using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Translator v16 runtime proofs: MouseNudge enqueues exactly one
    /// signed pixel delta per fire into the accumulate-and-flush mouse
    /// lane; CycleTapList steps forward one item per fire, wraps when
    /// asked and parks at the end otherwise; the step CSV round-trips the
    /// settings DTO; and the materializer lowers the two new translated
    /// shapes end to end.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopV16NudgeCycleTests : IDisposable
    {
        private static readonly Guid DevGuid = new("88888888-2222-3333-4444-555555555555");
        private const int Slot = 6;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public WorkshopV16NudgeCycleTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static CustomInputState ArrangeSlotDevice()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "V16 Pad",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = Slot });
            return state;
        }

        private static MacroItem MacroWithEntries(MacroAction action,
            params MacroItem.TriggerInputEntry[] entries)
        {
            var m = new MacroItem
            {
                Name = "V16",
                IsEnabled = true,
                PadIndex = Slot,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
            };
            m.Actions.Add(action);
            m.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>(entries));
            return m;
        }

        // ── MouseNudge executor: one delta per fire ──

        [Fact]
        public void MouseNudge_AccumulatesOneSignedDeltaPerFire()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingMouseMoveForTests(); // clean slate

            var action = new MacroAction
            {
                Type = MacroActionType.MouseNudge,
                NudgeDx = 100,
                NudgeDy = -40,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });
            var macros = new[] { macro };

            state.Buttons[4] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((100, -40), InputManager.DrainPendingMouseMoveForTests());

            // Held: no repeat (the nudge advanced past its single action).
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((0, 0), InputManager.DrainPendingMouseMoveForTests());

            // Release and press again: exactly one more delta.
            state.Buttons[4] = false;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            state.Buttons[4] = true;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((100, -40), InputManager.DrainPendingMouseMoveForTests());
        }

        // ── CycleTapList executor: forward stepping, wrap and park ──

        private static void FireOnce(InputManager im, CustomInputState state, MacroItem[] macros)
        {
            state.Buttons[4] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            state.Buttons[4] = false;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
        }

        [Fact]
        public void CycleTapList_AdvancesPerFire_AndWraps()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingScrollForTests();

            // Wheel-tick steps so the output is observable without real
            // key injection: +1, +2, +3 WHEEL_DELTA ticks.
            var action = new MacroAction
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = "W:1,W:2,W:3",
                CycleWrap = true,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });
            var macros = new[] { macro };

            var seen = new List<int>();
            for (int fire = 0; fire < 4; fire++)
            {
                FireOnce(im, state, macros);
                seen.Add(InputManager.DrainPendingScrollForTests().Vertical);
            }
            // Steps 1, 2, 3, then the wrap back to 1.
            Assert.Equal(new[] { 120, 240, 360, 120 }, seen);
        }

        [Fact]
        public void CycleTapList_WrapOff_ParksAtTheEnd()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingScrollForTests();

            var action = new MacroAction
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = "W:1,W:2",
                CycleWrap = false,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });
            var macros = new[] { macro };

            var seen = new List<int>();
            for (int fire = 0; fire < 4; fire++)
            {
                FireOnce(im, state, macros);
                seen.Add(InputManager.DrainPendingScrollForTests().Vertical);
            }
            // Steps 1 and 2, then the parked end: no further output
            // (Steam's Wrap List - Off).
            Assert.Equal(new[] { 120, 240, 0, 0 }, seen);
        }

        [Fact]
        public void CycleTapList_MultiPartStop_FiresPartsTogether()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingScrollForTests();

            // One item slot carrying two bindings ('+'-joined): both fire
            // on the same detent.
            var action = new MacroAction
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = "W:1+H:-2",
                CycleWrap = true,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });

            FireOnce(im, state, new[] { macro });
            Assert.Equal((120, -240), InputManager.DrainPendingScrollForTests());
        }

        // ── DTO round-trip (settings XML + clipboard share these) ──

        [Fact]
        public void NudgeAndCycle_RoundTripThroughMacroData()
        {
            var m = new MacroItem { Name = "RT" };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.MouseNudge,
                NudgeDx = -300,
                NudgeDy = 25,
            });
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.CycleTapList,
                CycleStepsCsv = "K:49,B:4096,A:2:32767",
                CycleWrap = false,
            });
            var data = SettingsService.BuildMacroDataForMacro(m, 0);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);

            var nudge = clone.Actions[0];
            Assert.Equal(MacroActionType.MouseNudge, nudge.Type);
            Assert.Equal(-300, nudge.NudgeDx);
            Assert.Equal(25, nudge.NudgeDy);

            var cycle = clone.Actions[1];
            Assert.Equal(MacroActionType.CycleTapList, cycle.Type);
            Assert.Equal("K:49,B:4096,A:2:32767", cycle.CycleStepsCsv);
            Assert.False(cycle.CycleWrap);
            Assert.Equal(3, cycle.ParsedCycleSteps.Length);
        }

        [Fact]
        public void CycleStepPart_ParsesTheVocabulary_AndRejectsJunk()
        {
            Assert.True(CycleStepPart.TryParse("K:49", out var k));
            Assert.Equal(('K', 49), (k.Kind, k.Value));
            Assert.True(CycleStepPart.TryParse("A:5:32767", out var a));
            Assert.Equal(('A', 5, (short)32767), (a.Kind, a.Value, a.Value2));
            Assert.False(CycleStepPart.TryParse("K:junk", out _));
            Assert.False(CycleStepPart.TryParse("Z:1", out _));
            Assert.False(CycleStepPart.TryParse("A:5", out _)); // axis needs a value
            Assert.False(CycleStepPart.TryParse("K:49:1", out _)); // taps take one number
        }

        // ── Materializer lowering ──

        [Fact]
        public void Materializer_MouseNudge_CarriesSignedDeltasUnclamped()
        {
            var translated = new TranslatedProfile { NeedsKbmSlot = true };
            translated.KbmMappingSet.Rows.Add(new MappingRow { Target = "KbmKey20" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Nudge",
                Action = TranslatedMacroAction.MouseNudge,
                TriggerMode = "OnPress",
                DeltaX = -5000,
                DeltaY = 100,
                TriggerInputDescriptors = { "Gamepad ButtonA" },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var action = Assert.Single(profile.Macros).Actions.Single();
            Assert.Equal(MacroActionType.MouseNudge, action.Type);
            Assert.Equal(-5000, action.NudgeDx);
            Assert.Equal(100, action.NudgeDy);
        }

        [Fact]
        public void Materializer_CycleList_EncodesCsvFoldingSameItemSteps()
        {
            var translated = new TranslatedProfile { NeedsKbmSlot = true };
            translated.KbmMappingSet.Rows.Add(new MappingRow { Target = "KbmKey20" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Wheel list",
                Action = TranslatedMacroAction.CycleList,
                TriggerMode = "OnPress",
                CycleWrap = false,
                TriggerInputDescriptors = { "Touchpad 0 SwipeDown" },
                CycleSteps =
                {
                    new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.KeyTap, VirtualKey = 0x31, ItemIndex = 0 },
                    // Two bindings on item 1 fold into one '+'-joined stop.
                    new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.WheelTap, WheelTicks = 1, ItemIndex = 1 },
                    new TranslatedCycleStep
                    { Kind = TranslatedCycleStepKind.MouseButtonTap, MouseButtonIndex = 2, ItemIndex = 1 },
                    // SDL-frame stick up converts through the shared axis
                    // map (XInput +32767 on LeftStickY = ordinal 1).
                    new TranslatedCycleStep
                    {
                        Kind = TranslatedCycleStepKind.VcAxisTap,
                        TargetAxis = "LeftThumbAxisY",
                        TargetAxisNegative = true,
                        ItemIndex = 2,
                    },
                },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var action = Assert.Single(profile.Macros).Actions.Single();
            Assert.Equal(MacroActionType.CycleTapList, action.Type);
            Assert.False(action.CycleWrap);
            Assert.Equal(
                $"K:49,W:1+M:2,A:{(int)MacroAxisTarget.LeftStickY}:32767",
                action.CycleStepsCsv);
        }
    }
}
