using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Translator v15 runtime proofs: the gyro-hosted swipe's half-stamped
    /// descriptor trigger fires on ONE signed rotation direction and
    /// re-arms below the rate threshold; the AxisHold executor asserts a
    /// full trigger pull / stick deflection per frame for its duration;
    /// MouseWheelTap sends exactly one signed WHEEL_DELTA tick per fire;
    /// and the materializer lowers the new translated shapes (VcAxisTap /
    /// HoldVcAxis / MouseWheelTap, plus the sdh trigger spec) end to end.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkshopV15SwipeAxisTests : IDisposable
    {
        private static readonly Guid DevGuid = new("77777777-2222-3333-4444-555555555555");
        private const int Slot = 6;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public WorkshopV15SwipeAxisTests()
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
                ProductName = "V15 Pad",
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
                Name = "V15",
                IsEnabled = true,
                PadIndex = Slot,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
            };
            m.Actions.Add(action);
            m.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>(entries));
            return m;
        }

        private static MacroAction ToggleAction() => new()
        {
            Type = MacroActionType.ToggleVcButton,
            ButtonFlags = Gamepad.B,
            CustomButtons = "00000002,00000000,00000000,00000000",
        };

        /// <summary>A gyro descriptor entry stamped the way the workshop
        /// materializer stamps it (half selector on the descriptor read).</summary>
        private static MacroItem.TriggerInputEntry GyroHalfEntry(string descriptor, bool invert)
        {
            var choice = new InputChoice { Descriptor = descriptor, DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(descriptor, entry.SourceDescriptor);
            entry.HalfAxis = true;
            entry.Invert = invert;
            return entry;
        }

        // ── Gyro swipe one-shot: signed direction gating, both ways ──

        [Fact]
        public void GyroHalfTrigger_FiresOnItsOwnSignedDirectionOnly()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null; // engine defaults
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                // North = pitch upper half, south = pitch lower half (SDL
                // frame per SDL_sensor.h; Dolphin's SDL_AXES_GYRO pins
                // "Pitch Up" = axis 0 scale +1).
                var north = MacroWithEntries(ToggleAction(), GyroHalfEntry("Gyro Pitch", invert: false));
                var south = MacroWithEntries(ToggleAction(), GyroHalfEntry("Gyro Pitch", invert: true));
                var macros = new[] { north, south };

                // Flick up: a deliberate positive pitch rate. Only the
                // upper-half macro fires; the lower half stays quiet in
                // the same window (the direction gate both ways).
                state.Gyro[0] = 2.0f; // ~115 deg/s, past the 30 deg/s gate
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(north.Actions[0].VcToggleLatched);
                Assert.False(south.Actions[0].VcToggleLatched);

                // Flick down: negative rate. The lower half fires, and the
                // upper half does NOT re-fire (unsigned |rate| would have).
                state.Gyro[0] = -2.0f;
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(north.Actions[0].VcToggleLatched);  // unchanged
                Assert.True(south.Actions[0].VcToggleLatched);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        [Fact]
        public void GyroHalfTrigger_OneShotPerFlick_RearmsBelowThreshold()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                var macro = MacroWithEntries(ToggleAction(), GyroHalfEntry("Gyro Pitch", invert: false));
                var macros = new[] { macro };

                // Entering the flick fires exactly once.
                state.Gyro[0] = 2.0f;
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(macro.Actions[0].VcToggleLatched);

                // Held past the threshold: no second fire (OnPress edge).
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(macro.Actions[0].VcToggleLatched);

                // Dropping below the 30 deg/s gate re-arms the one-shot.
                state.Gyro[0] = 0.1f;
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(macro.Actions[0].VcToggleLatched);

                // The next flick fires again (latch flips back off).
                state.Gyro[0] = 2.0f;
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.False(macro.Actions[0].VcToggleLatched);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        [Fact]
        public void GyroHalfEntry_SpecRoundTripsTheStamp()
        {
            var source = new MacroItem { Name = "RT" };
            source.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                GyroHalfEntry("Gyro Yaw", invert: true),
            });

            string spec = source.TriggerInputs;
            Assert.Equal("in:00000000-0000-0000-0000-000000000000:sdh:1:1:0:0:Gyro Yaw", spec);

            var restored = new MacroItem { Name = "RT2", TriggerInputs = spec };
            var entry = Assert.Single(restored.GetTriggerInputEntries());
            Assert.Equal("Gyro Yaw", entry.SourceDescriptor);
            Assert.True(entry.HalfAxis);
            Assert.True(entry.Invert);
            Assert.False(entry.Bidirectional);
            Assert.Equal(0, entry.DescriptorDeadZone);
            // The stamp rides the cached engine source the evaluator reads.
            Assert.True(entry.DescriptorSource.HalfAxis);
            Assert.True(entry.DescriptorSource.Invert);
            Assert.Equal(0, entry.DescriptorSource.DeadZone);

            // Plain descriptor entries keep the byte-identical legacy form.
            var plain = new MacroItem { Name = "RT3" };
            var choice = new InputChoice { Descriptor = "Gyro Pitch", DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var plainEntry));
            plain.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry> { plainEntry });
            Assert.Equal("in:00000000-0000-0000-0000-000000000000:sd:Gyro Pitch", plain.TriggerInputs);
        }

        // ── AxisHold executor ──

        [Fact]
        public void AxisHold_AssertsFullTriggerPull_EveryFrameForTheDuration()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            var action = new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftTrigger,
                AxisValue = 32767, // full pull on the 0..32767 pull scale
                DurationMs = 60000,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 3 });
            var macros = new[] { macro };

            state.Buttons[3] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, (int)gp.LeftTrigger); // 32767 doubles to FULL pull
            Assert.True(macro.IsExecuting);

            // Re-asserted on the next frame (gp rebuilds per frame).
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(65535, (int)gp.LeftTrigger);
        }

        [Fact]
        public void AxisHold_ZeroDuration_AssertsExactlyOneFrame()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            var action = new MacroAction
            {
                Type = MacroActionType.AxisHold,
                AxisTarget = MacroAxisTarget.LeftStickY,
                AxisValue = 32767, // XInput frame: +32767 = stick up
                DurationMs = 0,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 3 });
            var macros = new[] { macro };

            state.Buttons[3] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(32767, (int)gp.ThumbLY); // asserted on the firing frame

            // Duration elapsed on the first frame, sequence done: the next
            // frame writes nothing and the axis reads released.
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal(0, (int)gp.ThumbLY);
            Assert.False(macro.IsExecuting);
        }

        // ── MouseWheelTap executor ──

        [Fact]
        public void MouseWheelTap_SendsOneSignedDetentPerFire()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingScrollForTests(); // clean slate

            var action = new MacroAction
            {
                Type = MacroActionType.MouseWheelTap,
                AxisValue = 0, // 0 reads as +1 tick
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });
            var macros = new[] { macro };

            state.Buttons[4] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            var (v1, h1) = InputManager.DrainPendingScrollForTests();
            Assert.Equal(120, v1); // one WHEEL_DELTA, up
            Assert.Equal(0, h1);

            // Held: no repeat (the tap advanced past its single action).
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.Equal((0, 0), InputManager.DrainPendingScrollForTests());
        }

        [Fact]
        public void MouseWheelTap_HorizontalLane_CarriesSignedTickCount()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            InputManager.DrainPendingScrollForTests();

            var action = new MacroAction
            {
                Type = MacroActionType.MouseWheelTap,
                AxisValue = -2, // two ticks left
                WheelHorizontal = true,
            };
            var macro = MacroWithEntries(action, new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 4 });

            state.Buttons[4] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { macro });
            var (v, h) = InputManager.DrainPendingScrollForTests();
            Assert.Equal(0, v);
            Assert.Equal(-240, h);
        }

        // ── Materializer lowering ──

        [Fact]
        public void Materializer_VcAxisTap_LowersToAxisHoldWithXInputFrameSign()
        {
            var translated = new TranslatedProfile { NeedsXboxSlot = true };
            translated.XboxMappingSet.Rows.Add(new MappingRow { Target = "ButtonA" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Tap up",
                Action = TranslatedMacroAction.VcAxisTap,
                TriggerMode = "OnPress",
                TargetAxis = "LeftThumbAxisY",
                TargetAxisNegative = true, // SDL-frame up
                TriggerInputDescriptors = { "Gamepad ButtonA" },
            });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Tap pull",
                Action = TranslatedMacroAction.VcAxisTap,
                TriggerMode = "OnRelease",
                TargetAxis = "RightTrigger",
                TriggerInputDescriptors = { "Gamepad ButtonB" },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            Assert.Equal(2, profile.Macros.Length);

            var up = profile.Macros[0].Actions.Single();
            Assert.Equal(MacroActionType.AxisHold, up.Type);
            Assert.Equal(MacroAxisTarget.LeftStickY, up.AxisTarget);
            Assert.Equal(32767, (int)up.AxisValue); // SDL up = XInput +32767
            Assert.Equal(MacroRepeatMode.Once, profile.Macros[0].RepeatMode);

            var pull = profile.Macros[1].Actions.Single();
            Assert.Equal(MacroAxisTarget.RightTrigger, pull.AxisTarget);
            Assert.Equal(32767, (int)pull.AxisValue); // full pull on the pull scale
            Assert.Equal(MacroTriggerMode.OnRelease, profile.Macros[1].TriggerMode);
        }

        [Fact]
        public void Materializer_HoldVcAxis_TakesTheHoldVcButtonRepeatShape()
        {
            var translated = new TranslatedProfile { NeedsXboxSlot = true };
            translated.XboxMappingSet.Rows.Add(new MappingRow { Target = "ButtonA" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Hold pull",
                Action = TranslatedMacroAction.HoldVcAxis,
                TriggerMode = "HoldForMs",
                TriggerHoldMs = 300,
                TargetAxis = "LeftTrigger",
                TriggerInputDescriptors = { "Gamepad ButtonA" },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var data = Assert.Single(profile.Macros);
            Assert.Equal(MacroActionType.AxisHold, data.Actions.Single().Type);
            Assert.Equal(MacroRepeatMode.UntilRelease, data.RepeatMode);
            Assert.Equal(0, data.RepeatDelayMs);
            Assert.Equal(MacroTriggerMode.HoldForMs, data.TriggerMode);
            Assert.Equal(300, data.TriggerHoldMs);
        }

        [Fact]
        public void Materializer_GyroSwipeTrigger_StampsTheDescriptorHalf()
        {
            var translated = new TranslatedProfile { NeedsKbmSlot = true };
            translated.KbmMappingSet.Rows.Add(new MappingRow { Target = "KbmKey20" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Flick south",
                Action = TranslatedMacroAction.KeyTap,
                TriggerMode = "OnPress",
                VirtualKey = 0x20,
                TriggerInputDescriptors = { "Gyro Pitch" },
                TriggerDescriptorHalfAxis = true,
                TriggerDescriptorInvert = true,
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var data = Assert.Single(profile.Macros);
            Assert.Equal("in:00000000-0000-0000-0000-000000000000:sdh:1:1:0:0:Gyro Pitch",
                data.TriggerInputs);
        }

        [Fact]
        public void Materializer_MouseWheelTap_CarriesTicksAndLane()
        {
            var translated = new TranslatedProfile { NeedsKbmSlot = true };
            translated.KbmMappingSet.Rows.Add(new MappingRow { Target = "KbmKey20" });
            translated.Macros.Add(new TranslatedMacro
            {
                Name = "Wheel tick",
                Action = TranslatedMacroAction.MouseWheelTap,
                TriggerMode = "OnPress",
                WheelTicks = -1,
                WheelHorizontal = false,
                TriggerInputDescriptors = { "Gamepad ButtonA" },
            });

            var profile = WorkshopProfileMaterializer.Materialize(translated);
            var action = Assert.Single(profile.Macros).Actions.Single();
            Assert.Equal(MacroActionType.MouseWheelTap, action.Type);
            Assert.Equal(-1, (int)action.AxisValue);
            Assert.False(action.WheelHorizontal);
        }

        // ── Shift activator half stamp ──

        [Fact]
        public void ShiftActivator_AxisHalf_RoundTripsThroughXml()
        {
            var act = new ShiftActivator
            {
                Descriptor = "Gamepad LeftStickX",
                Kind = "Axis",
                Mode = "Toggle",
                LayerMask = "L1",
                AxisThreshold = 0.5,
                AxisHalf = true,
                AxisInvert = true,
            };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(ShiftActivator));
            using var sw = new System.IO.StringWriter();
            serializer.Serialize(sw, act);
            using var sr = new System.IO.StringReader(sw.ToString());
            var back = (ShiftActivator)serializer.Deserialize(sr);
            Assert.True(back.AxisHalf);
            Assert.True(back.AxisInvert);
        }
    }
}
