using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Full-repo audit #2, batch 2 pins on the App side: the
    /// materializer's axis-turbo release stop (M2) and wheel-turbo cadence
    /// (T1), the trigger-entry windowed-click and finger-window grammar
    /// (M8/M9), the widened sdh spec gate (G3), the editor exposure of
    /// MacroActionType 43-46 (M1), the hold-pair latch lowering (M4), and
    /// the hold-pair stale-release cancel (M6).</summary>
    public class AuditTwoBatchTwoTests
    {
        private static TranslatedProfile XboxProfile()
            => new() { Name = "Audit2", NeedsXboxSlot = true };

        // ─── M2: axis turbo stops on release ────────────────────────────

        [Fact]
        public void Materialize_AxisTurbo_StopsOnRelease()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Turbo LT (button_a)",
                Action = TranslatedMacroAction.RepeatVcAxisWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                TargetAxis = "LeftTrigger",
                IntervalMs = 120,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            var a = Assert.Single(m.Actions);
            Assert.Equal(MacroActionType.RepeatVcAxisWhileHeld, a.Type);
            // A continuous action pulses for as long as the macro executes;
            // only UntilRelease stops execution when the trigger releases.
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
        }

        // ─── T1: wheel turbo pulses at the authored rate ────────────────

        [Fact]
        public void Materialize_WheelTurbo_PulsesAtAuthoredRate()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Turbo wheel (button_a)",
                Action = TranslatedMacroAction.RepeatWheelWhileHeld,
                TriggerMode = "WhileHeld",
                TriggerXboxButtons = Gamepad.A,
                WheelTicks = -1,
                IntervalMs = 250,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            var m = Assert.Single(p.Macros);
            var a = Assert.Single(m.Actions);
            Assert.Equal(MacroActionType.MouseWheelTap, a.Type);
            Assert.Equal(-1, a.AxisValue);
            Assert.False(a.WheelHorizontal);
            // One detent per interval: the one-shot tap re-runs on the
            // repeat machinery until the trigger releases.
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
            Assert.Equal(250, m.RepeatDelayMs);
        }

        // ─── M8: windowed clicks convert to descriptor entries ──────────

        [Theory]
        [InlineData("Touchpad 0 Click Left")]
        [InlineData("Touchpad 0 Click Upper")]
        [InlineData("Touchpad 1 Click North")]
        [InlineData("Touchpad 2 Click West")]
        public void TryBuildTriggerEntry_WindowedClick_RidesDescriptor(string descriptor)
        {
            var choice = new InputChoice { Descriptor = descriptor, DeviceGuid = string.Empty };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(descriptor, entry.SourceDescriptor);
            Assert.True(string.IsNullOrEmpty(entry.GestureDescriptor));
            Assert.Equal(-1, entry.RawButton);
        }

        [Fact]
        public void TryBuildTriggerEntry_PlainClick_KeepsRawButton16()
        {
            var choice = new InputChoice { Descriptor = "Touchpad 0 Click", DeviceGuid = string.Empty };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(16, entry.RawButton);
        }

        [Fact]
        public void TryBuildTriggerEntry_UnknownClickWindow_Fails()
        {
            var choice = new InputChoice { Descriptor = "Touchpad 0 Click Sideways", DeviceGuid = string.Empty };
            Assert.False(MacroItem.TryBuildTriggerEntry(choice, out _));
        }

        // ─── M9: every v18 finger-window form converts ──────────────────

        [Theory]
        [InlineData("Touchpad 0 Finger 0 Down")]
        [InlineData("Touchpad 0 Finger 0 Down Left")]
        [InlineData("Touchpad 0 Finger 0 Down Right")]
        [InlineData("Touchpad 0 Finger 0 Down Upper")]
        [InlineData("Touchpad 0 Finger 0 Down Lower")]
        [InlineData("Touchpad 0 Finger 0 Down North")]
        [InlineData("Touchpad 0 Finger 0 Down South")]
        [InlineData("Touchpad 0 Finger 0 Down East")]
        [InlineData("Touchpad 0 Finger 0 Down West")]
        [InlineData("Touchpad 0 Finger 0 Down North Left")]
        [InlineData("Touchpad 0 Finger 0 Down South Right")]
        public void TryBuildTriggerEntry_FingerWindows_AllV18FormsConvert(string descriptor)
        {
            var choice = new InputChoice { Descriptor = descriptor, DeviceGuid = string.Empty };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(descriptor, entry.SourceDescriptor);
        }

        [Theory]
        [InlineData("Touchpad 0 Finger 0 Down Junk")]
        [InlineData("Touchpad 0 Finger 0 Down North Junk")]
        [InlineData("Touchpad 0 Finger 0 Down Upper Left")] // only quadrants compose
        [InlineData("Touchpad 0 Finger 0 X")]
        // "Pressure" left this list with #239 (the bool read exists now);
        // its convertibility is pinned in MacroDeviceFreeTriggerTests and
        // out-of-grammar pressure windows still fail below.
        [InlineData("Touchpad 0 Finger 0 Pressure Junk")]
        public void TryBuildTriggerEntry_OutOfGrammarFingerForms_Fail(string descriptor)
        {
            var choice = new InputChoice { Descriptor = descriptor, DeviceGuid = string.Empty };
            Assert.False(MacroItem.TryBuildTriggerEntry(choice, out _));
        }

        // ─── G3: the sdh spec gate covers every non-default stamp ───────

        [Theory]
        [InlineData(false, true, false, 0)]  // Invert only
        [InlineData(false, false, true, 0)]  // Bidirectional only
        [InlineData(false, false, false, 30)] // deadzone only (pre-fix reachable)
        [InlineData(true, false, false, 0)]  // half only (pre-fix reachable)
        public void TriggerEntrySpec_WritesSdh_ForAnyNonDefaultStamp(
            bool half, bool invert, bool bidir, int dz)
        {
            var entry = new MacroItem.TriggerInputEntry
            {
                DeviceGuid = Guid.Empty,
                SourceDescriptor = "Gyro Yaw",
                HalfAxis = half,
                Invert = invert,
                Bidirectional = bidir,
                DescriptorDeadZone = dz,
            };
            Assert.Contains(":sdh:", entry.Spec);

            var parsed = MacroItem.TriggerInputEntry.Parse(entry.Spec);
            Assert.NotNull(parsed);
            Assert.Equal("Gyro Yaw", parsed.SourceDescriptor);
            Assert.Equal(half, parsed.HalfAxis);
            Assert.Equal(invert, parsed.Invert);
            Assert.Equal(bidir, parsed.Bidirectional);
            if (dz > 0) Assert.Equal(dz, parsed.DescriptorDeadZone);
        }

        [Fact]
        public void TriggerEntrySpec_UnstampedDescriptor_KeepsPlainSd()
        {
            var entry = new MacroItem.TriggerInputEntry
            {
                DeviceGuid = Guid.Empty,
                SourceDescriptor = "Gyro Yaw",
            };
            Assert.Contains(":sd:", entry.Spec);
            Assert.DoesNotContain(":sdh:", entry.Spec);
        }

        // ─── M1: editor exposure for MacroActionType 43-46 ──────────────

        [Theory]
        [InlineData(MacroActionType.ToggleMouseButton)]
        [InlineData(MacroActionType.ToggleVcAxis)]
        [InlineData(MacroActionType.RepeatVcAxisWhileHeld)]
        [InlineData(MacroActionType.ToggleWheel)]
        public void LatchFamily_DisplayText_IsNotUnknown(MacroActionType type)
        {
            var a = new MacroAction { Type = type };
            Assert.NotEqual(Strings.Instance.Macro_UnknownAction, a.DisplayText);
            Assert.False(string.IsNullOrWhiteSpace(a.DisplayText));
        }

        [Fact]
        public void LatchFamily_EditorGates_SurfaceTheRightPanels()
        {
            var mouse = new MacroAction { Type = MacroActionType.ToggleMouseButton };
            Assert.True(mouse.IsToggleMouseButtonType);
            Assert.True(mouse.IsAnyMouseButtonType);
            Assert.True(mouse.IsPulseCapableType);
            Assert.False(mouse.IsRepeatIntervalType); // no pulse yet

            var axis = new MacroAction { Type = MacroActionType.ToggleVcAxis };
            Assert.True(axis.IsToggleVcAxisType);
            Assert.True(axis.IsAnyAxisValueType);
            Assert.True(axis.IsPulseCapableType);

            var turbo = new MacroAction { Type = MacroActionType.RepeatVcAxisWhileHeld };
            Assert.True(turbo.IsRepeatVcAxisWhileHeldType);
            Assert.True(turbo.IsAnyAxisValueType);
            Assert.True(turbo.IsRepeatIntervalType);
            Assert.False(turbo.IsPulseCapableType);

            var wheel = new MacroAction { Type = MacroActionType.ToggleWheel };
            Assert.True(wheel.IsToggleWheelType);
            Assert.True(wheel.IsAnyWheelTapType);
            Assert.True(wheel.IsRepeatIntervalType);
        }

        [Fact]
        public void PulseWhileLatched_SurfacesTheIntervalRow()
        {
            var a = new MacroAction { Type = MacroActionType.ToggleKey };
            Assert.True(a.IsPulseCapableType);
            Assert.False(a.IsRepeatIntervalType);
            a.PulseWhileLatched = true;
            Assert.True(a.IsRepeatIntervalType);
        }

        [Theory]
        [InlineData(MacroActionType.ToggleMouseButton)]
        [InlineData(MacroActionType.ToggleVcAxis)]
        [InlineData(MacroActionType.RepeatVcAxisWhileHeld)]
        [InlineData(MacroActionType.ToggleWheel)]
        public void LatchFamily_PickerStrings_ResolveInEveryExposedKey(MacroActionType type)
        {
            string key = $"MacroAction_Type_{type}";
            string value = Strings.Get(key);
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.NotEqual(key, value);
        }

        // ─── M4: hold pairs latch instead of raw press legs ─────────────

        private static TranslatedMacro HoldKeyMacro(int vk, int delayEndMs = 0) => new()
        {
            Name = "Hold key (button_a)",
            Action = TranslatedMacroAction.HoldKey,
            TriggerMode = "OnPress",
            TriggerXboxButtons = Gamepad.A,
            VirtualKey = vk,
            DelayEndMs = delayEndMs,
        };

        private static MacroItem[] Materialized(TranslatedProfile t)
            => WorkshopProfileMaterializer.Materialize(t).Macros
                .Select(md => SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null))
                .ToArray();

        [Fact]
        public void Materialize_HoldKey_LowersToLatchSetClearPair()
        {
            var t = XboxProfile();
            t.Macros.Add(HoldKeyMacro(0xA2, delayEndMs: 200));
            t.Macros.Add(HoldKeyMacro(0x43));

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.Equal(4, p.Macros.Length);
            var press = p.Macros[0];
            var release = p.Macros[1];

            // The press leg SETs the ToggleKey latch, so the held key
            // rides the reconcile's engine-stop / profile-switch release
            // paths. The old KeyPress Down leg bypassed _latchedKeysDown
            // and stranded a held Ctrl on engine stop (M4).
            Assert.Equal(MacroTriggerMode.OnPress, press.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, press.RepeatMode);
            var set = Assert.Single(press.Actions);
            Assert.Equal(MacroActionType.ToggleKey, set.Type);
            Assert.Equal(0xA2, set.KeyCode);
            Assert.Equal(MacroLatchDirection.On, set.LatchDirection);

            // The OnRelease twin CLEARs it, behind its delay_end Delay.
            Assert.Equal(MacroTriggerMode.OnRelease, release.TriggerMode);
            Assert.Equal(2, release.Actions.Length);
            Assert.Equal(MacroActionType.Delay, release.Actions[0].Type);
            Assert.Equal(200, release.Actions[0].DurationMs);
            Assert.Equal(MacroActionType.ToggleKey, release.Actions[1].Type);
            Assert.Equal(0xA2, release.Actions[1].KeyCode);
            Assert.Equal(MacroLatchDirection.Off, release.Actions[1].LatchDirection);

            // The shared nonzero PairId is the runtime link (Off clears
            // the twin's latch; a starting leg cancels its twin), and
            // each pair gets its own id so unrelated holds never
            // cross-cancel.
            Assert.NotEqual(0, press.PairId);
            Assert.Equal(press.PairId, release.PairId);
            Assert.Equal(p.Macros[2].PairId, p.Macros[3].PairId);
            Assert.NotEqual(press.PairId, p.Macros[2].PairId);
        }

        [Fact]
        public void Materialize_HoldMouseButton_LowersToLatchSetClearPair()
        {
            var t = XboxProfile();
            t.Macros.Add(new TranslatedMacro
            {
                Name = "Hold mouse RIGHT (button_a)",
                Action = TranslatedMacroAction.HoldMouseButton,
                TriggerMode = "OnPress",
                TriggerXboxButtons = Gamepad.A,
                MouseButtonIndex = 1,
            });

            var p = WorkshopProfileMaterializer.Materialize(t);
            Assert.Equal(2, p.Macros.Length);
            var set = Assert.Single(p.Macros[0].Actions);
            Assert.Equal(MacroActionType.ToggleMouseButton, set.Type);
            Assert.Equal((MacroMouseButton)1, set.MouseButton);
            Assert.Equal(MacroLatchDirection.On, set.LatchDirection);
            var clear = Assert.Single(p.Macros[1].Actions);
            Assert.Equal(MacroActionType.ToggleMouseButton, clear.Type);
            Assert.Equal((MacroMouseButton)1, clear.MouseButton);
            Assert.Equal(MacroLatchDirection.Off, clear.LatchDirection);
            Assert.Equal(p.Macros[0].PairId, p.Macros[1].PairId);
            Assert.NotEqual(0, p.Macros[0].PairId);
        }

        [Fact]
        public void HoldPairFields_RoundTripThroughTheDtos()
        {
            var m = new MacroItem { Name = "RT", PairId = 3 };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ToggleKey,
                KeyCode = 0xA2,
                LatchDirection = MacroLatchDirection.Off,
            });
            var md = SettingsService.BuildMacroDataForMacro(m, 0);
            Assert.Equal(3, md.PairId);
            Assert.Equal(MacroLatchDirection.Off, Assert.Single(md.Actions).LatchDirection);

            var clone = SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null);
            Assert.Equal(3, clone.PairId);
            Assert.Equal(MacroLatchDirection.Off, Assert.Single(clone.Actions).LatchDirection);
        }

        [Fact]
        public void HoldPair_EngineStop_ReleasesTheHeldKey()
        {
            var t = XboxProfile();
            // VK_NONAME (0xFC, "reserved, no effect"): the Down / Up really
            // route through SendInput but carry an inert key, so the test
            // never types into the host (the MacroWave1b press-leg pattern).
            t.Macros.Add(HoldKeyMacro(0xFC));
            var macros = Materialized(t);

            var im = new InputManager();
            im.MacroSnapshots[0] = macros;

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);   // press leg fires: latch SET
            Assert.Contains((ushort)0xFC, im._desiredLatchedKeys);

            im.ReconcileLatchedKeys();               // the engine-frame settle
            Assert.Contains((ushort)0xFC, im._latchedKeysDown);

            // The polling loop's exit path releases the reconcile-held key.
            // The old KeyPress Down leg never entered _latchedKeysDown, so
            // engine stop or profile switch stranded it logically pressed.
            im.ReleaseAllLatchedMacroKeys();
            Assert.Empty(im._latchedKeysDown);
        }

        // ─── M6: re-press cancels the pending delayed release ───────────

        [Fact]
        public void HoldPair_RePress_CancelsThePendingDelayedRelease()
        {
            var t = XboxProfile();
            t.Macros.Add(HoldKeyMacro(0xFC, delayEndMs: 30));
            var macros = Materialized(t);
            var press = macros[0];
            var release = macros[1];

            var im = new InputManager();
            im.MacroSnapshots[0] = macros;

            var gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);   // press #1: latch SET
            Assert.True(press.Actions[0].KeyToggleLatched);

            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);   // release: Delay(30) in flight
            Assert.True(release.IsExecuting);

            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);   // re-press inside the delay
            Assert.False(release.IsExecuting);       // stale release cancelled

            System.Threading.Thread.Sleep(45);       // past the delay window
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);   // would advance the stale Delay
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);   // would execute the stale Clear
            // Without the cancel, the stale Clear fires here and releases
            // the NEW hold mid-press.
            Assert.True(press.Actions[0].KeyToggleLatched);
            im._desiredLatchedKeys.Clear();
            gp = new Gamepad { Buttons = Gamepad.A };
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Contains((ushort)0xFC, im._desiredLatchedKeys);

            // The real release still lands: the falling edge starts a
            // fresh Delay + Clear, and the Off reaches the PRESS leg's
            // latch through the PairId (each leg latches its own action
            // instance).
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);   // fresh Delay starts
            System.Threading.Thread.Sleep(45);
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);   // Delay elapses, advance
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);   // Clear executes
            Assert.False(press.Actions[0].KeyToggleLatched);
            im._desiredLatchedKeys.Clear();
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Empty(im._desiredLatchedKeys);
        }

        [Fact]
        public void HoldPair_ExtendedTwin_MouseButton_SetClearAndCancel()
        {
            // Hand-built pair on the Extended dispatch twin. Out-of-range
            // button value: SendMouseButtonInput's switch default drops
            // it, so no real click can reach the OS.
            const MacroMouseButton inert = (MacroMouseButton)99;
            var press = new MacroItem
            {
                Name = "hold",
                IsEnabled = true,
                PadIndex = 0,
                PairId = 7,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.UntilRelease,
                ConsumeTriggerButtons = false,
            };
            press.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ToggleMouseButton,
                MouseButton = inert,
                LatchDirection = MacroLatchDirection.On,
            });
            var release = new MacroItem
            {
                Name = "release",
                IsEnabled = true,
                PadIndex = 0,
                PairId = 7,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnRelease,
                ConsumeTriggerButtons = false,
            };
            release.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DurationMs = 30 });
            release.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ToggleMouseButton,
                MouseButton = inert,
                LatchDirection = MacroLatchDirection.Off,
            });
            var macros = new[] { press, release };

            var im = new InputManager();
            im.MacroSnapshots[0] = macros;

            var raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // press: latch SET
            Assert.True(press.Actions[0].MouseToggleLatched);
            Assert.Contains(inert, im._desiredLatchedMouseButtons);

            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // Delay in flight
            Assert.True(release.IsExecuting);

            raw = RawState(0x1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // re-press cancels the twin
            Assert.False(release.IsExecuting);

            // Full release: the Off leg's clear reaches the press leg's latch.
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // fresh Delay starts
            System.Threading.Thread.Sleep(45);
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // Delay elapses, advance
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);  // Clear executes
            Assert.False(press.Actions[0].MouseToggleLatched);
            im._desiredLatchedMouseButtons.Clear();
            raw = RawState(0);
            im.EvaluateSlotMacrosExtended(ref raw, macros);
            Assert.Empty(im._desiredLatchedMouseButtons);
        }

        private static RawHidState RawState(uint pressedWord0)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Buttons[0] = pressedWord0;
            return raw;
        }
    }
}
