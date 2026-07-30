using System;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    // Locks the trigger-click activation contract (owner report
    // 2026-07-22): raw buttons the profile layout declares as
    // LeftTriggerClick / RightTriggerClick (ZL/ZR on the Switch Pro)
    // fire on ANY nonzero value when fed by a physical trigger AXIS,
    // not at the generic 50% axis-to-button midpoint. That is the
    // DS4/DualSense digital trigger-follower contract, and HIDMaestro's
    // virtual DS4/DualSense derives its bits the same way
    // (HidReportBuilder: value > 0). Explicit per-row thresholds still
    // win, and the mask is derived from profile role metadata, never
    // hardcoded indices.
    public class ZlZrTriggerClickTests
    {
        private static RawHidState Eval(int axis2Value, int triggerClickMask, string explicitThreshold = null)
        {
            var raw = new RawHidState
            {
                Axes = new short[8],
                Buttons = new uint[1],
                Povs = new int[1],
                HardwareAxes = new short[8],
            };
            var state = new CustomInputState();
            state.Axis[2] = axis2Value;
            var ps = new PadSetting();
            ps.SetRawMapping("RawBtn6", "Axis 2");
            if (explicitThreshold != null)
                ps.SetMappingDeadZone("RawBtn6", explicitThreshold);
            var cfg = new CustomControllerLayout
            {
                Axes = 4, Buttons = 14, Povs = 1, Sticks = 2, Triggers = 0,
                TriggerClickButtonMask = triggerClickMask,
            };
            InputManager.MapInputToExtendedRaw(ref raw, state, ps, cfg, null, "", 0);
            return raw;
        }

        private const int ZlZrMask = (1 << 6) | (1 << 7);

        private static bool Zl(in RawHidState r) => (r.Buttons[0] & (1u << 6)) != 0;

        [Fact]
        public void AnyNonzeroPullFiresDeclaredTriggerClick()
        {
            // The DS4/DualSense digital trigger-follower contract: ANY
            // detected nonzero value engages. One count is enough.
            var raw = Eval(1, ZlZrMask);
            Assert.True(Zl(raw));
            raw = Eval((int)(65535 * 0.10), ZlZrMask);
            Assert.True(Zl(raw));
        }

        [Fact]
        public void RestingTriggerStaysReleased()
        {
            // Exactly zero = released; there is no sub-activation band.
            var raw = Eval(0, ZlZrMask);
            Assert.False(Zl(raw));
        }

        [Fact]
        public void AnyNonzeroPullFiresThroughTheMappingSetPathToo()
        {
            // The grid-row path (TryEvaluateMappingSetButton ->
            // SourceCoercion) must carry the same any-nonzero contract
            // as the legacy fallback exercised above; its threshold
            // floor lives in a different file.
            var raw = new RawHidState
            {
                Axes = new short[8],
                Buttons = new uint[1],
                Povs = new int[1],
                HardwareAxes = new short[8],
            };
            var state = new CustomInputState();
            state.Axis[2] = 1;
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "RawBtn6",
                Sources = { new MappingSource { Descriptor = "Axis 2", DeviceGuid = "" } },
            });
            var cfg = new CustomControllerLayout
            {
                Axes = 4, Buttons = 14, Povs = 1, Sticks = 2, Triggers = 0,
                TriggerClickButtonMask = ZlZrMask,
            };
            InputManager.MapInputToExtendedRaw(ref raw, state, new PadSetting(), cfg, ms, "", 0);
            Assert.True(Zl(raw));

            // And zero stays released on the same path.
            raw.Buttons[0] = 0;
            state.Axis[2] = 0;
            InputManager.MapInputToExtendedRaw(ref raw, state, new PadSetting(), cfg, ms, "", 0);
            Assert.False(Zl(raw));
        }

        [Fact]
        public void UndeclaredButtonsKeepTheMidpointDefault()
        {
            var raw = Eval((int)(65535 * 0.10), triggerClickMask: 0);
            Assert.False(Zl(raw));
            raw = Eval((int)(65535 * 0.60), triggerClickMask: 0);
            Assert.True(Zl(raw));
        }

        [Fact]
        public void ExplicitRowThresholdStillWins()
        {
            // Authored 60% threshold: a 30% pull must not fire even on a
            // declared trigger-click button.
            var raw = Eval((int)(65535 * 0.30), ZlZrMask, explicitThreshold: "60");
            Assert.False(Zl(raw));
            raw = Eval((int)(65535 * 0.70), ZlZrMask, explicitThreshold: "60");
            Assert.True(Zl(raw));
        }

        [Fact]
        public void MaskDerivesFromProfileRoleMetadata()
        {
            var layout = new HMGamepadLayout
            {
                ShoulderButtons = new[]
                {
                    new HMButtonBinding { Role = HMButtonRole.LeftBumper, ButtonIndex = 4 },
                    new HMButtonBinding { Role = HMButtonRole.RightBumper, ButtonIndex = 5 },
                    new HMButtonBinding { Role = HMButtonRole.LeftTriggerClick, ButtonIndex = 6 },
                    new HMButtonBinding { Role = HMButtonRole.RightTriggerClick, ButtonIndex = 7 },
                },
            };
            Assert.Equal(ZlZrMask, PadForge.Services.InputService.TriggerClickButtonMaskFrom(layout));
            // Non-gamepad layouts declare no trigger clicks.
            Assert.Equal(0, PadForge.Services.InputService.TriggerClickButtonMaskFrom(null));
        }

        [Fact]
        public void ShippedSwitchProProfileDeclaresZlZr()
        {
            // End-to-end: the real catalog profile the Nintendo slot type
            // runs must yield exactly ZL/ZR from its role metadata. If
            // this fails after a profile update, the layout's
            // trigger-click roles moved and the activation contract
            // follows them automatically; the test documents the pairing.
            var profile = HMaestroProfileCatalog.GetProfileById("switch-pro");
            Assert.NotNull(profile);
            Assert.Equal(ZlZrMask,
                PadForge.Services.InputService.TriggerClickButtonMaskFrom(profile.Layout));
        }
    }
}
