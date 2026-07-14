using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 B-15: the absolute touchpad pointer family
    /// "Touchpad {p} Pointer X|Y[ Left|Right]". The finger's ABSOLUTE pad
    /// position maps onto the screen (Steam's mouse_region 1:1 semantics),
    /// riding the Wii IR pointer's KbmRawState.MouseAbs* lane in Step 3.
    /// Coverage: corner/center/edge mapping math, the per-(slot, device,
    /// pad) margin stretch, half-windowed composition, the per-source
    /// region window (mouse_region geometry), the finger-lift engagement
    /// gate (freeze contract), grammar rejects, gesture-family exclusion,
    /// display strings and picker mirror entries, and the lens-1k
    /// persisted round-trip through the legacy migrator.
    /// </summary>
    public class TouchpadPointerTests
    {
        /// <summary>Away from every other test class's slots so the
        /// provider hooks and any static state never collide across
        /// parallel classes.</summary>
        private const int Slot = 41;

        private static CustomInputState TouchState(float x, float y = 0.5f, bool down = true)
        {
            var s = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = down;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            s.Touchpads = new[] { pad };
            return s;
        }

        private static float Eval(CustomInputState s, MappingSource src)
            => SourceCoercion.EvaluateForBipolarAxisTarget(s, src, Slot);

        // ── Pad-to-screen mapping math (corners, center, edges) ──

        [Fact]
        public void Absolute_CornersAndCenter_MapOneToOne()
        {
            var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X" };
            var y = new MappingSource { Descriptor = "Touchpad 0 Pointer Y" };

            // Top-left corner of the pad = top-left of the screen.
            Assert.Equal(-1f, Eval(TouchState(0f, 0f), x));
            Assert.Equal(-1f, Eval(TouchState(0f, 0f), y));
            // Bottom-right corner.
            Assert.Equal(1f, Eval(TouchState(1f, 1f), x));
            Assert.Equal(1f, Eval(TouchState(1f, 1f), y));
            // Center.
            Assert.Equal(0f, Eval(TouchState(0.5f, 0.5f), x));
            Assert.Equal(0f, Eval(TouchState(0.5f, 0.5f), y));
            // Quarter point: 0.25 pad → -0.5 screen.
            Assert.Equal(-0.5f, Eval(TouchState(0.25f), x));
        }

        [Fact]
        public void Invert_FlipsThroughTheStandardWrapper()
        {
            var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X", Invert = true };
            Assert.Equal(1f, Eval(TouchState(0f), x));
            Assert.Equal(-1f, Eval(TouchState(1f), x));
        }

        // ── Margin stretch (per-(slot, device, pad) Touchpad-tab tuning) ──

        [Fact]
        public void Stretch_ReachesScreenEdgeBeforePadEdge()
        {
            var prior = SourceCoercion.TouchpadMouseSettingsProvider;
            try
            {
                SourceCoercion.TouchpadMouseSettingsProvider = (slot, guid, pad) =>
                    new TouchpadGestureSettings { PointerStretchX = 2.0f, PointerStretchY = 1.0f };

                var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X", DeviceGuid = "b15-s1" };
                var y = new MappingSource { Descriptor = "Touchpad 0 Pointer Y", DeviceGuid = "b15-s1" };

                // Stretch 2.0 around the pad center: 0.75 pad → 1.0 → +1.
                Assert.Equal(1f, Eval(TouchState(0.75f), x));
                // 0.6 pad → 0.5 + 0.1*2 = 0.7 → +0.4.
                Assert.Equal(0.4f, Eval(TouchState(0.6f), x), 3);
                // Center is a fixed point.
                Assert.Equal(0f, Eval(TouchState(0.5f), x));
                // Past the stretched edge clamps, never wraps.
                Assert.Equal(1f, Eval(TouchState(0.9f), x));
                // Y keeps its own axis's stretch (1.0 here).
                Assert.Equal(0.5f, Eval(TouchState(0.5f, y: 0.75f), y), 3);
            }
            finally { SourceCoercion.TouchpadMouseSettingsProvider = prior; }
        }

        [Fact]
        public void Stretch_UsesEffectiveGuid_SoTranslatedEmptyGuidRowsTune()
        {
            var prior = SourceCoercion.TouchpadMouseSettingsProvider;
            try
            {
                string seenGuid = null;
                SourceCoercion.TouchpadMouseSettingsProvider = (slot, guid, pad) =>
                {
                    seenGuid = guid;
                    return new TouchpadGestureSettings { PointerStretchX = 2.0f };
                };
                // Empty source guid = "the device on the slot": the tuned
                // read must key the provider by the EVALUATED device (the
                // IR pointer's EffectiveDeviceGuid convention), not the
                // bare empty string.
                var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X", DeviceGuid = "" };
                float v = SourceCoercion.EvaluateForBipolarAxisTarget(
                    TouchState(0.75f), x, Slot, evaluatedDeviceGuid: "b15-dev-7");
                Assert.Equal(1f, v);
                Assert.Equal("b15-dev-7", seenGuid);
            }
            finally { SourceCoercion.TouchpadMouseSettingsProvider = prior; }
        }

        // ── Half-windowed composition (#9 B-1 windows on the pointer) ──

        [Fact]
        public void Halves_RenormalizeXAndGateEngagement()
        {
            var right = new MappingSource { Descriptor = "Touchpad 0 Pointer X Right" };
            // Right half [0.5..1] is a complete miniature pad.
            Assert.Equal(0f, Eval(TouchState(0.75f), right));
            Assert.Equal(-1f, Eval(TouchState(0.5f), right));
            Assert.Equal(1f, Eval(TouchState(1.0f), right));
            // Outside the half: neutral value, not engaged.
            Assert.Equal(0f, Eval(TouchState(0.25f), right));
            Assert.False(SourceCoercion.IsTouchpadPointerEngaged(
                TouchState(0.25f), "Touchpad 0 Pointer X Right"));

            // A windowed Y spans the full pad height but still gates on
            // the half.
            var rightY = new MappingSource { Descriptor = "Touchpad 0 Pointer Y Right" };
            Assert.Equal(1f, Eval(TouchState(0.75f, y: 1.0f), rightY));
            Assert.Equal(0f, Eval(TouchState(0.25f, y: 1.0f), rightY));

            // Boundary convention matches the Finger family: X == 0.5 is
            // Right.
            var left = new MappingSource { Descriptor = "Touchpad 0 Pointer X Left" };
            Assert.Equal(0f, Eval(TouchState(0.5f), left));
            Assert.Equal(-1f, Eval(TouchState(0.5f), right));
        }

        // ── Region window (mouse_region geometry on the source) ──

        [Fact]
        public void RegionParams_MapPadOntoTheWindow()
        {
            // A minimap-style region: centered at 25% of the screen,
            // spanning 50% of the axis.
            var x = new MappingSource
            {
                Descriptor = "Touchpad 0 Pointer X",
                ParamPointerCenter = 0.25,
                ParamPointerExtent = 0.5,
            };
            // Pad center → region center (-0.5 in bipolar).
            Assert.Equal(-0.5f, Eval(TouchState(0.5f), x));
            // Pad left edge → region left edge (-0.5 - 0.5 = -1).
            Assert.Equal(-1f, Eval(TouchState(0f), x));
            // Pad right edge → region right edge (0).
            Assert.Equal(0f, Eval(TouchState(1f), x));
        }

        [Fact]
        public void RegionParams_OversizedRegionClampsAtScreen()
        {
            // sensitivity scales above 100% push the region past the
            // screen; the read clamps exactly like Steam's cursor does.
            var x = new MappingSource
            {
                Descriptor = "Touchpad 0 Pointer X",
                ParamPointerExtent = 1.2,
            };
            Assert.Equal(-1f, Eval(TouchState(0f), x));
            Assert.Equal(1f, Eval(TouchState(1f), x));
            Assert.Equal(0f, Eval(TouchState(0.5f), x));
        }

        // ── Finger lift: the freeze contract's engagement gate ──

        [Fact]
        public void Lift_ReadsNeutralAndDisengages()
        {
            var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X" };
            // Value relaxes to 0; Step 3 gates MouseAbsValid on the
            // engagement check, so the 0 never drives the cursor (freeze).
            Assert.Equal(0f, Eval(TouchState(0.9f, down: false), x));
            Assert.False(SourceCoercion.IsTouchpadPointerEngaged(
                TouchState(0.9f, down: false), "Touchpad 0 Pointer X"));
            Assert.True(SourceCoercion.IsTouchpadPointerEngaged(
                TouchState(0.9f), "Touchpad 0 Pointer X"));
            // No touchpad on the device at all: never engaged.
            Assert.False(SourceCoercion.IsTouchpadPointerEngaged(
                new CustomInputState(), "Touchpad 0 Pointer X"));
        }

        [Fact]
        public void RelativeDeltaLane_ReadsZero_SoMixedRowsKeepTheirRelativeSources()
        {
            // Step 3 falls through to the delta lane exactly when no
            // pointer source on the row is engaged; on that lane a
            // position is not a delta, so an engaged-or-not pointer source
            // contributes nothing (corpus 3456927474 mixes gyro + stick +
            // a mouse_region pad on one summed KbmMouseX row, and the
            // pointer must never leak a constant absolute offset into that
            // delta sum).
            var x = new MappingSource { Descriptor = "Touchpad 0 Pointer X" };
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(
                TouchState(0.9f), x, Slot, relativeTouchpad: true));
        }

        // ── Grammar ──

        [Theory]
        [InlineData("Touchpad 0 Pointer X", true, 0, 0)]
        [InlineData("Touchpad 1 Pointer Y", true, 1, 1)]
        [InlineData("Touchpad 0 Pointer X Left", true, 0, 0)]
        [InlineData("Touchpad 0 Pointer Y Right", true, 0, 1)]
        [InlineData("Touchpad 0 Pointer Pressure", false, 0, 0)] // no pointer pressure
        [InlineData("Touchpad 0 Pointer X Up", false, 0, 0)]     // unknown half token
        [InlineData("Touchpad 0 Pointer", false, 0, 0)]          // missing axis
        [InlineData("Touchpad Pointer X", false, 0, 0)]          // missing pad index
        [InlineData("Touchpad 0 Finger 0 X", false, 0, 0)]       // finger family stays itself
        public void Grammar_ParsesExactSpellingsOnly(string descriptor, bool ok, int pad, int axis)
        {
            bool parsed = SourceCoercion.TryParseTouchpadPointer(descriptor,
                out int padIdx, out int axisOffset, out _);
            Assert.Equal(ok, parsed);
            if (ok)
            {
                Assert.Equal(pad, padIdx);
                Assert.Equal(axis, axisOffset);
            }
        }

        [Fact]
        public void Family_IsNotAGestureAndClassifiesAsPointer()
        {
            // "Pointer" must not fall into the gesture family (whose
            // provider lookups would read it as a never-firing gesture).
            Assert.False(SourceCoercion.IsTouchpadGestureDescriptor("Touchpad 0 Pointer X"));
            Assert.False(SourceCoercion.TryParseTouchpadGesture("Touchpad 0 Pointer X", out _, out _));
            Assert.Equal(SourceCoercion.SourceType.TouchpadPointer,
                SourceCoercion.ClassifyDescriptor("Touchpad 0 Pointer X Right"));
            // The finger and gesture families keep their classifications.
            Assert.Equal(SourceCoercion.SourceType.TouchpadButton,
                SourceCoercion.ClassifyDescriptor("Touchpad 0 Finger 0 X"));
            Assert.Equal(SourceCoercion.SourceType.TouchpadGesture,
                SourceCoercion.ClassifyDescriptor("Touchpad 0 SwipeUp"));
        }

        // ── Display strings and picker mirror entries ──

        [Fact]
        public void Display_RendersPointerNames()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(string.Format(si.Mapping_TouchpadPointerX_Format, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Pointer X", null));
            Assert.Equal(string.Format(si.Mapping_TouchpadPointerYRight_Format, 1),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 0 Pointer Y Right", null));
            Assert.Equal(string.Format(si.Mapping_TouchpadPointerXLeft_Format, 2),
                MappingDisplayResolver.ResolveDescriptorText("Touchpad 1 Pointer X Left", null));
        }

        [Fact]
        public void AnyDeviceGroup_OffersPointerEntries_HalvesOnPadZeroOnly()
        {
            var choices = MappingDisplayResolver.BuildDeviceAgnosticChoices();
            var descriptors = System.Array.ConvertAll(choices, c => c.Descriptor);
            Assert.Contains("Touchpad 0 Pointer X", descriptors);
            Assert.Contains("Touchpad 0 Pointer Y", descriptors);
            Assert.Contains("Touchpad 1 Pointer X", descriptors);
            Assert.Contains("Touchpad 0 Pointer X Left", descriptors);
            Assert.Contains("Touchpad 0 Pointer Y Right", descriptors);
            Assert.DoesNotContain("Touchpad 1 Pointer X Left", descriptors);
        }

        // ── VM knob gates (both display-VM twins) ──

        [Fact]
        public void VmGates_DeadZoneVisibleOnDiscreteTargets_HalfHidden()
        {
            // The pointer's button coercion thresholds on the per-source
            // DeadZone (the IR pointer's shape), so the knob must be
            // visible on discrete targets in BOTH VM twins (the
            // retroactive #146/#151 hidden-knob lesson). HalfAxis is
            // ignored by the read, so the Half checkbox stays hidden
            // (no inert knob).
            var msi = new PadForge.ViewModels.MappingSourceItem
            { Descriptor = "Touchpad 0 Pointer X Right", ParentTargetIsDiscrete = true };
            Assert.True(msi.IsDeadZoneApplicable);
            Assert.False(msi.IsHalfAxisApplicable);
            Assert.False(msi.IsGenericSensitivitySource);

            var mi = new PadForge.ViewModels.MappingItem("A", "ButtonA",
                PadForge.ViewModels.MappingCategory.Buttons);
            mi.LoadDescriptor("Touchpad 0 Pointer X Right");
            Assert.True(mi.IsDeadZoneApplicable);
            Assert.False(mi.IsHalfAxisApplicable);
        }

        // ── Macro trigger conversion ──

        [Fact]
        public void TriggerEntry_PointerAxesDoNotConvert()
        {
            // The Touchpad gesture-fire catch-all must not claim the
            // pointer family: a GestureDescriptor entry for "Pointer X"
            // would parse as no gesture and never fire. Positional
            // analogs stay mapping-only, like the finger X/Y reads.
            var axis = new PadForge.ViewModels.InputChoice
            { Descriptor = "Touchpad 0 Pointer X", DeviceGuid = "" };
            Assert.False(PadForge.ViewModels.MacroItem.TryBuildTriggerEntry(axis, out _));
            var half = new PadForge.ViewModels.InputChoice
            { Descriptor = "Touchpad 1 Pointer Y Right", DeviceGuid = "" };
            Assert.False(PadForge.ViewModels.MacroItem.TryBuildTriggerEntry(half, out _));
        }

        // ── Settings round-trip (the per-pad stretch fields) ──

        [Fact]
        public void PointerStretch_SurvivesCloneAndChecksum()
        {
            var s = new TouchpadGestureSettings { PointerStretchX = 1.8f, PointerStretchY = 2.0f };
            var clone = s.Clone();
            Assert.Equal(1.8f, clone.PointerStretchX);
            Assert.Equal(2.0f, clone.PointerStretchY);

            // The PadSetting checksum must see the stretch, or the
            // save-path dedup silently drops a pad whose ONLY difference
            // is the pointer tuning (the TPS: dedup trap).
            PadSetting A()
            {
                var ps = new PadSetting
                {
                    TouchpadSettings = new[]
                    {
                        new TouchpadSettingsEntry
                        {
                            DeviceGuid = "33333333-3333-3333-3333-333333333333",
                            TouchpadIndex = 0,
                            Settings = new TouchpadGestureSettings(),
                        },
                    },
                };
                return ps;
            }
            var plain = A();
            var tuned = A();
            tuned.TouchpadSettings[0].Settings.PointerStretchX = 1.8f;
            Assert.NotEqual(plain.ComputeChecksum(), tuned.ComputeChecksum());
        }

        // ── Lens 1k: persisted round-trip through the legacy migrator ──

        [Theory]
        [InlineData("Touchpad 0 Pointer X")]
        [InlineData("Touchpad 0 Pointer Y")]
        [InlineData("Touchpad 0 Pointer X Left")]
        [InlineData("Touchpad 0 Pointer X Right")]
        [InlineData("Touchpad 0 Pointer Y Left")]
        [InlineData("Touchpad 0 Pointer Y Right")]
        public void Migrator_KeepsPointerDescriptorsByteIdentical(string descriptor)
        {
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("44444444-4444-4444-4444-444444444444", ps) });
            var row = System.Linq.Enumerable.FirstOrDefault(ms.Rows, r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            var src = Assert.Single(row.Sources);
            Assert.Equal(descriptor, src.Descriptor);
            Assert.False(src.Invert);
            Assert.False(src.HalfAxis);
        }
    }
}
