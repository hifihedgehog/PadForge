using System;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Common.Mapping;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the 2026-07-25 round-two audit fixes: the
    /// layout-aware raw axis map (C34), the raw trigger coordinate frame
    /// (C36), the ladder-vs-toggle latch clear (C29), the full-chord and
    /// mode gates on consume arming (C30), the postpone-set lifecycle
    /// (C35), the calibrator aux capability gate and upgrade path
    /// (C24/C39), the pulse-flag retype clear (C40), and the strict aux
    /// descriptor spelling (C45).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundTwoTests : IDisposable
    {
        private static readonly Guid DevGuid = new("42424242-4242-4242-4242-424242424242");
        private static readonly string DevGuidStr = DevGuid.ToString();
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;

        public AuditJuly25RoundTwoTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            // The suppression sets are static per-slot state: leave them
            // empty for the next test class.
            InputManager.ClearAllShiftRuntime();
        }

        private static CustomInputState ArrangeSlotDevice(out UserDevice ud, int slot = 0)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Audit Pad",
                IsOnline = true,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(
                    new UserSetting { InstanceGuid = DevGuid, MapTo = slot });
            return state;
        }

        private static MacroItem RawMacro(string triggerInputs, MacroTriggerMode mode, params MacroAction[] actions)
        {
            var m = new MacroItem
            {
                Name = "A2",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = mode,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = true,
                TriggerInputs = triggerInputs,
            };
            foreach (var a in actions) m.Actions.Add(a);
            return m;
        }

        // ── C34: layout-aware raw axis map ──

        /// <summary>On a 2-stick/0-trigger layout (the virtual Switch Pro
        /// shape) RightStickX packs at index 2, not the default map's 3.
        /// The old hardcoded map wrote RightStickY's channel instead.</summary>
        [Fact]
        public void RawAxisMap_ResolvesThroughTheSlotLayout()
        {
            ArrangeSlotDevice(out var ud);
            ud.InputState.Buttons[0] = true;
            var im = new InputManager();
            im.SlotCustomLayouts[0] = new CustomControllerLayout
            { Axes = 4, Buttons = 14, Povs = 1, Sticks = 2, Triggers = 0 };

            var m = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress,
                new MacroAction
                {
                    Type = MacroActionType.AxisSet,
                    AxisTarget = MacroAxisTarget.RightStickX,
                    AxisValue = 20000,
                });
            var raw = new RawHidState { Axes = new short[4] };
            im.EvaluateSlotMacrosExtended(ref raw, new[] { m });

            Assert.Equal(20000, raw.Axes[2]);   // RX on the 2/0 packing
            Assert.Equal(0, raw.Axes[3]);       // RY untouched (old map hit this)
        }

        /// <summary>Direct-call tests and unprimed slots keep the default
        /// 2-stick/2-trigger map (fallback when no layout is stashed).</summary>
        [Fact]
        public void RawAxisMap_DefaultLayoutKeepsTheFixedMap()
        {
            ArrangeSlotDevice(out var ud);
            ud.InputState.Buttons[0] = true;
            var im = new InputManager();

            var m = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress,
                new MacroAction
                {
                    Type = MacroActionType.AxisSet,
                    AxisTarget = MacroAxisTarget.RightStickX,
                    AxisValue = 20000,
                });
            var raw = new RawHidState { Axes = new short[6] };
            im.EvaluateSlotMacrosExtended(ref raw, new[] { m });
            Assert.Equal(20000, raw.Axes[3]);
        }

        // ── C36: raw trigger coordinate frame ──

        /// <summary>Raw trigger channels rest at short.MinValue. The
        /// editor's 0..32767 pull scale doubles onto that span, so 0%
        /// lands at REST, not at the signed midpoint (half-pulled).</summary>
        [Fact]
        public void RawTriggerSet_ZeroPercentIsRest_FullIsFull()
        {
            ArrangeSlotDevice(out var ud);
            ud.InputState.Buttons[0] = true;
            var im = new InputManager();

            var zero = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress,
                new MacroAction
                {
                    Type = MacroActionType.AxisSet,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = 0,
                });
            var raw = new RawHidState { Axes = new short[6] };
            raw.Axes[2] = 12345; // some pulled state the set must override
            im.EvaluateSlotMacrosExtended(ref raw, new[] { zero });
            Assert.Equal(short.MinValue, raw.Axes[2]);

            ArrangeSlotDevice(out var ud2);
            ud2.InputState.Buttons[0] = true;
            var im2 = new InputManager();
            var full = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress,
                new MacroAction
                {
                    Type = MacroActionType.AxisSet,
                    AxisTarget = MacroAxisTarget.LeftTrigger,
                    AxisValue = short.MaxValue,
                });
            var raw2 = new RawHidState { Axes = new short[6] };
            im2.EvaluateSlotMacrosExtended(ref raw2, new[] { full });
            Assert.True(raw2.Axes[2] >= short.MaxValue - 1, $"full pull, got {raw2.Axes[2]}");
        }

        // ── C29: the ladder clears a Toggle Axis latch on its target ──

        [Fact]
        public void LadderStep_ClearsAToggleAxisLatch_OnTheSameTarget()
        {
            ArrangeSlotDevice(out var ud);
            var im = new InputManager();

            var toggle = new MacroAction
            {
                Type = MacroActionType.ToggleVcAxis,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = short.MaxValue,
                VcAxisToggleLatched = true, // already latched
            };
            var ladder = new MacroAction
            {
                Type = MacroActionType.AxisSetLatched,
                AxisTarget = MacroAxisTarget.LeftStickX,
                AxisValue = -16384,
            };
            var m = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress, ladder);
            m.Actions.Add(toggle); // toggle sits AFTER the ladder step in list order

            ud.InputState.Buttons[0] = true;
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { m });

            Assert.True(ladder.VcAxisToggleLatched, "ladder step latched");
            Assert.False(toggle.VcAxisToggleLatched,
                "the later toggle latch must be cleared or it overwrites the ladder every frame");
        }

        // ── C30: consume arms only on the FULL trigger, and never for
        //         modes that ignore the entry list ──

        [Fact]
        public void Consume_PartialChordDoesNotSuppress_FullChordDoes()
        {
            var state = ArrangeSlotDevice(out _);
            var im = new InputManager();
            var m = RawMacro(
                "in:" + DevGuidStr + ":btn:0|in:" + DevGuidStr + ":btn:5",
                MacroTriggerMode.OnPress,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A });
            im.MacroSnapshots[0] = new[] { m };

            // One chord member held: the macro cannot fire, so nothing is
            // eaten (the old per-entry arming suppressed Button 0 here).
            state.Buttons[0] = true;
            im.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"),
                "partial chord must not consume");

            // Both held: the full trigger reads active, both members eaten.
            state.Buttons[5] = true;
            im.RebuildConsumedTriggerSources();
            Assert.True(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"));
            Assert.True(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 5"));
        }

        [Fact]
        public void Consume_AlwaysMode_NeverEatsStaleEntries()
        {
            var state = ArrangeSlotDevice(out _);
            var im = new InputManager();
            var m = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.Always,
                new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A });
            im.MacroSnapshots[0] = new[] { m };

            state.Buttons[0] = true;
            im.RebuildConsumedTriggerSources();
            Assert.False(InputManager.IsSourceSuppressedPostpone(0, DevGuidStr, "Button 0"),
                "Always mode ignores TriggerInputs, so it must not consume them");
        }

        // ── C35: the postpone set empties with its activators ──

        [Fact]
        public void PostponeSuppression_ClearsWhenActivatorsVanish()
        {
            var state = ArrangeSlotDevice(out _, slot: 3);
            var set = new MappingSet();
            set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuidStr,
                Descriptor = "Button 7",
                LayerMask = "Shift1",
                Mode = "Hold",
                PostponeMapping = false,
            });

            state.Buttons[7] = true;
            InputManager.ResolveActiveLayerMask(3, set, state, DevGuidStr);
            Assert.True(InputManager.IsSourceSuppressedPostpone(3, DevGuidStr, "Button 7"),
                "held postpone activator suppresses its own source");

            // Profile switch to a set with no activators: the suppression
            // must die with the machinery (the old early return kept the
            // key forever).
            var bare = new MappingSet();
            InputManager.ResolveActiveLayerMask(3, bare, state, DevGuidStr);
            Assert.False(InputManager.IsSourceSuppressedPostpone(3, DevGuidStr, "Button 7"),
                "no activators left, nothing may stay suppressed");
        }

        // ── C24 / C39: calibrator aux gating ──

        /// <summary>A device without the aux sensor must never overwrite a
        /// stored aux triple: the state array is always allocated, so the
        /// capability flag is the only real gate.</summary>
        [Fact]
        public async Task Calibrator_WithoutAuxCapability_LeavesStoredAuxBias()
        {
            ArrangeSlotDevice(out var ud);
            ud.HasGyro = true;
            ud.HasGyroAux = false;
            var ps = new PadSetting
            {
                GyroAuxBiasPitch = "0.5",
                GyroAuxBiasYaw = "0.25",
                GyroAuxBiasRoll = "-0.1",
            };
            var svc = new GyroCalibratorService();
            bool ok = await svc.RecalibrateAsync(ud, ps, 250);
            Assert.True(ok);
            Assert.Equal("0.5", ps.GyroAuxBiasPitch);
            Assert.Equal("0.25", ps.GyroAuxBiasYaw);
            Assert.Equal("-0.1", ps.GyroAuxBiasRoll);
        }

        /// <summary>A profile stamped before the aux fields existed still
        /// gets the aux pass; one whose aux triple is set does not.</summary>
        [Fact]
        public void Calibrator_UpgradePath_RunsOnlyWhenAuxUnset()
        {
            ArrangeSlotDevice(out var ud);
            ud.HasGyro = true;
            ud.HasGyroAux = true;
            var svc = new GyroCalibratorService();

            var stampedUnset = new PadSetting { GyroCalibratedAtUtc = "2026-01-01T00:00:00Z" };
            Assert.False(ReferenceEquals(
                svc.EnsureAutoCalibratedAsync(ud, stampedUnset), Task.CompletedTask));

            var stampedSet = new PadSetting
            {
                GyroCalibratedAtUtc = "2026-01-01T00:00:00Z",
                GyroAuxBiasPitch = "0.01",
            };
            Assert.True(ReferenceEquals(
                svc.EnsureAutoCalibratedAsync(ud, stampedSet), Task.CompletedTask));

            // AND THE CALLER MUST ACTUALLY DELIVER IT (audit 2026-07-25, G1).
            // The two assertions above call the calibrator DIRECTLY, so they
            // stayed green for months while the only automatic caller skipped
            // every profile carrying a timestamp: the upgrade branch above
            // could not be reached in the product at all. Testing the callee
            // in isolation proves the decision is right, never that anything
            // asks it. This is the caller's half.
            Assert.True(InputService.ShouldConsiderForGyroAutoCalibration(ud, stampedUnset),
                "a timestamped, aux-unset profile must still reach the calibrator");
            Assert.True(InputService.ShouldConsiderForGyroAutoCalibration(ud, stampedSet),
                "the caller must not pre-judge; the calibrator owns the decision");

            // The cheap filters it IS allowed to apply.
            ud.IsOnline = false;
            Assert.False(InputService.ShouldConsiderForGyroAutoCalibration(ud, stampedUnset));
            ud.IsOnline = true;
            ud.HasGyro = false;
            Assert.False(InputService.ShouldConsiderForGyroAutoCalibration(ud, stampedUnset));
        }

        // ── C40: hidden pulse dies on retype ──

        [Fact]
        public void TypeChange_ClearsPulse_WhenNewTypeHidesTheCheckbox()
        {
            var a = new MacroAction
            {
                Type = MacroActionType.ToggleVcAxis,
                PulseWhileLatched = true,
            };
            a.Type = MacroActionType.AxisSetLatched;
            Assert.False(a.PulseWhileLatched,
                "AxisSetLatched hides the pulse checkbox, so the flag must not survive the retype");

            var b = new MacroAction
            {
                Type = MacroActionType.ToggleVcButton,
                PulseWhileLatched = true,
            };
            b.Type = MacroActionType.ToggleVcAxis; // still pulse-capable
            Assert.True(b.PulseWhileLatched, "pulse-capable retype keeps the choice");
        }

        // ── C16: raw AxisScale survives an unmaterialized axis surface ──

        [Fact]
        public void RawAxisScale_NullAxes_DoesNotThrow()
        {
            ArrangeSlotDevice(out var ud);
            ud.InputState.Buttons[0] = true;
            var im = new InputManager();
            var m = RawMacro("in:" + DevGuidStr + ":btn:0", MacroTriggerMode.OnPress,
                new MacroAction
                {
                    Type = MacroActionType.AxisScale,
                    AxisTarget = MacroAxisTarget.LeftStickX,
                    AxisValue = 16384,
                    DurationMs = 1000,
                });
            var raw = new RawHidState(); // Axes == null: slot not yet shaped
            im.EvaluateSlotMacrosExtended(ref raw, new[] { m }); // must not throw
        }

        // ── C45: mangled aux spellings fail closed ──

        [Fact]
        public void MangledAuxSpelling_ReadsNothing_NotThePrimary()
        {
            var oldTuning = SourceCoercion.GyroTuningProvider;
            var oldBias = SourceCoercion.GyroBiasProvider;
            var oldAuxBias = SourceCoercion.GyroAuxBiasProvider;
            var oldEngage = SourceCoercion.AimEngageStateProvider;
            try
            {
                SourceCoercion.GyroTuningProvider = null;
                SourceCoercion.GyroBiasProvider = null;
                SourceCoercion.GyroAuxBiasProvider = null;
                SourceCoercion.AimEngageStateProvider = null;

                var st = new CustomInputState();
                st.Gyro[1] = 2.0f;     // primary yaw spinning
                st.GyroAux[1] = 0f;    // aux still

                float v = SourceCoercion.EvaluateForBipolarAxisTarget(
                    st,
                    new MappingSource { Kind = "Direct", Descriptor = "Gyro L  Yaw", DeviceGuid = DevGuidStr },
                    0, false, DevGuidStr);
                Assert.True(Math.Abs(v) < 0.001f,
                    $"a double-space aux spelling must fail closed, got {v} (the primary's rate)");
            }
            finally
            {
                SourceCoercion.GyroTuningProvider = oldTuning;
                SourceCoercion.GyroBiasProvider = oldBias;
                SourceCoercion.GyroAuxBiasProvider = oldAuxBias;
                SourceCoercion.AimEngageStateProvider = oldEngage;
            }
        }
    }
}
