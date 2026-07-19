using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 B-9: device-free macro triggers. A TriggerInputEntry with
    /// Guid.Empty means "the device on the macro's slot", resolved per slot
    /// device exactly like the mapping engine's empty MappingSource.DeviceGuid
    /// contract, so paddle / touchpad / gyro-hosted workshop bindings can
    /// trigger without a concrete device pinned at import time. Coverage:
    /// both dispatch paths fire from an empty-guid entry built the same way
    /// the picker builds it, concrete-guid behavior is unchanged (positive
    /// control beside the silent case), descriptor entries evaluate gyro and
    /// finger-down through the engine readers, the picker's "(Any device)"
    /// group converts, and the spec round-trips the empty guid.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacroDeviceFreeTriggerTests : IDisposable
    {
        private static readonly Guid DevGuid = new("55555555-6666-7777-8888-999999999999");
        private static readonly Guid ForeignGuid = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        private const int Slot = 7;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public MacroDeviceFreeTriggerTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        /// <summary>Seeds the statics with one online device mapped to
        /// <see cref="Slot"/> and returns its mutable input state.</summary>
        private static CustomInputState ArrangeSlotDevice()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var state = new CustomInputState();
            var ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "B9 Pad",
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

        private static MacroItem MacroWithEntries(params MacroItem.TriggerInputEntry[] entries)
        {
            var m = new MacroItem
            {
                Name = "B9",
                IsEnabled = true,
                PadIndex = Slot,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
            };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                ButtonFlags = Gamepad.B,
                CustomButtons = "00000002,00000000,00000000,00000000",
            });
            m.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>(entries));
            return m;
        }

        /// <summary>Builds the entry exactly like the picker does: through
        /// TryBuildTriggerEntry from an "(Any device)" choice (empty
        /// DeviceGuid string, same shape InputService populates).</summary>
        private static MacroItem.TriggerInputEntry AnyDeviceEntry(string descriptor)
        {
            var choice = new InputChoice { Descriptor = descriptor, DeviceGuid = "" };
            Assert.True(MacroItem.TryBuildTriggerEntry(choice, out var entry));
            Assert.Equal(Guid.Empty, entry.DeviceGuid);
            return entry;
        }

        // ── Dispatch: both paths fire from an empty-guid Gamepad descriptor ──

        [Fact]
        public void EmptyGuidPaddleEntry_Fires_GamepadPath_WithSameWindowNegativeControl()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            // "Gamepad Paddle1" folds to raw Button 12 at pick time.
            var firing = MacroWithEntries(AnyDeviceEntry("Gamepad Paddle1"));
            // Same-window silent control: an empty-guid entry whose button
            // is NOT held must stay quiet in the very same evaluation.
            var silent = MacroWithEntries(AnyDeviceEntry("Gamepad Paddle2"));
            var macros = new[] { firing, silent };

            state.Buttons[12] = true; // Paddle1 held on the slot's device
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);

            Assert.True(firing.Actions[0].VcToggleLatched);
            Assert.False(silent.Actions[0].VcToggleLatched);
        }

        [Fact]
        public void EmptyGuidPaddleEntry_Fires_ExtendedPath()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();
            var macro = MacroWithEntries(AnyDeviceEntry("Gamepad Paddle1"));
            var macros = new[] { macro };

            state.Buttons[12] = true;
            var raw = RawHidState.Create(8, 32, 1);
            im.EvaluateSlotMacrosExtended(ref raw, macros);

            Assert.True(macro.Actions[0].VcToggleLatched);
            Assert.Equal(0x2u, raw.Buttons[0] & 0x2u); // latch written to the raw shape
        }

        [Fact]
        public void EmptyGuidEntry_SlotWithoutDevices_StaysQuiet()
        {
            ArrangeSlotDevice();
            var im = new InputManager();
            var macro = MacroWithEntries(AnyDeviceEntry("Gamepad Paddle1"));
            macro.PadIndex = Slot + 1; // no device mapped there

            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros: new[] { macro });
            Assert.False(macro.Actions[0].VcToggleLatched);
        }

        // ── Positive control: concrete-guid behavior unchanged ──

        [Fact]
        public void ConcreteGuidEntry_OwnSlotFires_ForeignDeviceStillBlocked()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            // Pinned to the slot's own device: fires (positive control).
            var pinned = MacroWithEntries(new MacroItem.TriggerInputEntry
            { DeviceGuid = DevGuid, RawButton = 12 });
            // Pinned to a device that is not on the slot: blocked, the #112
            // containment this wave must not loosen.
            var foreign = MacroWithEntries(new MacroItem.TriggerInputEntry
            { DeviceGuid = ForeignGuid, RawButton = 12 });

            state.Buttons[12] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { pinned, foreign });

            Assert.True(pinned.Actions[0].VcToggleLatched);
            Assert.False(foreign.Actions[0].VcToggleLatched);
        }

        // ── Descriptor entries through the engine readers ──

        [Fact]
        public void GyroDescriptorEntry_FiresPastRateThreshold_BothPaths()
        {
            var state = ArrangeSlotDevice();
            var im = new InputManager();

            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;   // engine defaults
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                var macro = MacroWithEntries(AnyDeviceEntry("Gyro Pitch"));
                var macros = new[] { macro };

                // Below the 30°/s default threshold: quiet (same window).
                state.Gyro[0] = 0.1f;
                var gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.False(macro.Actions[0].VcToggleLatched);

                // A deliberate twist: fires on the Gamepad path.
                state.Gyro[0] = 2.0f;
                gp = new Gamepad();
                im.EvaluateSlotMacros(ref gp, macros);
                Assert.True(macro.Actions[0].VcToggleLatched);

                // Extended path: fresh macro, same descriptor entry.
                var macroExt = MacroWithEntries(AnyDeviceEntry("Gyro Pitch"));
                var raw = RawHidState.Create(8, 32, 1);
                im.EvaluateSlotMacrosExtended(ref raw, new[] { macroExt });
                Assert.True(macroExt.Actions[0].VcToggleLatched);
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }

        [Fact]
        public void FingerDownDescriptorEntry_TracksContact()
        {
            var state = ArrangeSlotDevice();
            var pad = new TouchpadInputState(1);
            state.Touchpads = new[] { pad };
            var im = new InputManager();

            var macro = MacroWithEntries(AnyDeviceEntry("Touchpad 0 Finger 0 Down"));
            var macros = new[] { macro };

            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.False(macro.Actions[0].VcToggleLatched); // lifted: quiet

            pad.FingerDown[0] = true;
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, macros);
            Assert.True(macro.Actions[0].VcToggleLatched);
        }

        // ── Picker: the "(Any device)" group converts like the mapping picker ──

        [Fact]
        public void AnyDeviceGroup_ConvertsThroughTheSameFilterThePickerUses()
        {
            // Mirror InputService.PopulateAvailableInputs' macro filter over
            // the real device-agnostic group.
            var offered = MappingDisplayResolver.BuildDeviceAgnosticChoices()
                .Select(c => new InputChoice
                { Descriptor = c.Descriptor, DisplayName = c.DisplayName, DeviceGuid = string.Empty })
                .Where(c => MacroItem.TryBuildTriggerEntry(c, out _))
                .Select(c => c.Descriptor)
                .ToArray();

            Assert.Contains("Gamepad ButtonA", offered);
            Assert.Contains("Gamepad Paddle1", offered);
            Assert.Contains("Gamepad DPadUp", offered);
            Assert.Contains("Gamepad LeftStickX", offered);
            Assert.Contains("Gyro Pitch", offered);
            Assert.Contains("Touchpad 0 Finger 0 Down", offered);
            Assert.Contains("Touchpad 0 Click", offered);
            Assert.Contains("Touchpad 0 TouchLeft", offered);
            // Continuous shapes stay unconvertible.
            Assert.DoesNotContain("Touchpad 0 Finger 0 X", offered);
            Assert.DoesNotContain("Touchpad 0 StickX", offered);
            // Pressure converts since #239: the bool branch reads it
            // against the per-source threshold (zone windows included).
            Assert.Contains("Touchpad 0 Finger 0 Pressure", offered);
        }

        [Fact]
        public void AnyDeviceGroup_EntryShapes_MatchTheEvaluatorContract()
        {
            Assert.Equal(0, AnyDeviceEntry("Gamepad ButtonA").RawButton);
            Assert.Equal(12, AnyDeviceEntry("Gamepad Paddle1").RawButton);
            Assert.Equal("0:0", AnyDeviceEntry("Gamepad DPadUp").Pov);
            Assert.Equal(MacroAxisTarget.LeftStickX, AnyDeviceEntry("Gamepad LeftStickX").AxisTarget);
            Assert.Equal("Gyro Pitch", AnyDeviceEntry("Gyro Pitch").SourceDescriptor);
            Assert.Equal("Touchpad 0 Finger 0 Down", AnyDeviceEntry("Touchpad 0 Finger 0 Down").SourceDescriptor);
            Assert.Equal("Touchpad 0 TouchLeft", AnyDeviceEntry("Touchpad 0 TouchLeft").GestureDescriptor);
        }

        // ── Persistence: the empty guid survives the spec round-trip ──

        [Fact]
        public void EmptyGuidEntries_RoundTripThroughTriggerInputs()
        {
            var source = new MacroItem { Name = "RT" };
            source.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                AnyDeviceEntry("Gamepad Paddle1"),
                AnyDeviceEntry("Gyro Pitch"),
            });

            string spec = source.TriggerInputs;
            Assert.Equal(
                "in:00000000-0000-0000-0000-000000000000:btn:12"
                + "|in:00000000-0000-0000-0000-000000000000:sd:Gyro Pitch",
                spec);

            var restored = new MacroItem { Name = "RT2", TriggerInputs = spec };
            var entries = restored.GetTriggerInputEntries();
            Assert.Equal(2, entries.Count);
            Assert.Equal(Guid.Empty, entries[0].DeviceGuid);
            Assert.Equal(12, entries[0].RawButton);
            Assert.Equal(Guid.Empty, entries[1].DeviceGuid);
            Assert.Equal("Gyro Pitch", entries[1].SourceDescriptor);
            Assert.True(restored.UsesRawTrigger);
            Assert.True(restored.UsesDescriptorTrigger);

            // A payload-less entry still voids to "" and drops out of the
            // joined spec (empty-or-null, matching the pre-change shape).
            var empty = new MacroItem { Name = "RT3" };
            empty.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            { new MacroItem.TriggerInputEntry() });
            Assert.True(string.IsNullOrEmpty(empty.TriggerInputs));
        }

        // ── Editor round-trip: the chip and summary carry the sentinel ──

        [Fact]
        public void DisplaySurfaces_RenderTheAnyDeviceSentinel()
        {
            ArrangeSlotDevice(); // display resolvers walk the statics
            var macro = MacroWithEntries(AnyDeviceEntry("Gamepad Paddle1"));

            string sentinel = PadForge.Resources.Strings.Strings.Instance.Mapping_AnyDevice;
            Assert.Contains(sentinel, macro.TriggerDisplayText);

            var chip = macro.TriggerInputItems.First();
            Assert.StartsWith(sentinel + ":", chip.Label);
        }
    }
}
