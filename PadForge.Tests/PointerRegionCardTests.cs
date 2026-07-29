using System;
using System.Linq;
using System.Reflection;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The absolute pointer's screen region became ONE user-editable setting.
    /// It had been two: a per-pad PointerStretch with a UI and a floor of 1.0,
    /// and a per-source ParamPointerCenter/Extent with no UI at all, which is
    /// where every imported Steam mouse_region landed. They were the same
    /// quantity, so the only knob a user could reach moved in the one
    /// direction imports never used.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PointerRegionCardTests
    {
        private const int Slot = 0;

        private static CustomInputState TouchState(float x = 0.5f, float y = 0.5f)
        {
            var st = new CustomInputState();
            var pad = new TouchpadInputState(1);
            pad.FingerDown[0] = true;
            pad.FingerX[0] = x;
            pad.FingerY[0] = y;
            st.Touchpads = new[] { pad };
            return st;
        }

        private static float Eval(CustomInputState st, MappingSource src) =>
            SourceCoercion.EvaluateForBipolarAxisTarget(st, src, Slot, evaluatedDeviceGuid: "region-dev");

        private static IDisposable Provider(TouchpadGestureSettings s)
        {
            var prior = SourceCoercion.TouchpadMouseSettingsProvider;
            SourceCoercion.TouchpadMouseSettingsProvider = (slot, guid, pad) => s;
            return new Restore(() => SourceCoercion.TouchpadMouseSettingsProvider = prior);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _a;
            public Restore(Action a) { _a = a; }
            public void Dispose() => _a();
        }

        private static MappingSource X(double center = 0.5, double extent = 1.0) =>
            new MappingSource
            {
                Descriptor = "Touchpad 0 Pointer X",
                DeviceGuid = "region-dev",
                ParamPointerCenter = center,
                ParamPointerExtent = extent,
            };

        // ── The range fix ────────────────────────────────────────────────

        [Theory]
        // Every region size the 30-config translation corpus authors. Five of
        // the six are BELOW 1.0, and the superseded stretch knob was floored
        // at 1.0, so a user could see none of them and edit none of them.
        [InlineData(0.07)]
        [InlineData(0.11)]
        [InlineData(0.50)]
        [InlineData(0.70)]
        [InlineData(1.00)]
        [InlineData(1.20)]
        public void RegionSize_CoversEveryExtentTheCorpusAuthors(double size)
        {
            using (Provider(new TouchpadGestureSettings
            {
                PointerRegionAuthored = true,
                PointerRegionSizeX = (float)size,
            }))
            {
                // Pad edge maps to center + size, clamped to the screen edge.
                float expected = (float)Math.Min(1.0, size);
                Assert.Equal(expected, Eval(TouchState(1f), X()), 3);
                // The pad center is a fixed point at the screen center for
                // every size, so a smaller region SHRINKS about the middle
                // rather than sliding to a corner.
                Assert.Equal(0f, Eval(TouchState(0.5f), X()), 3);
            }
        }

        [Fact]
        public void RegionSizeBelowOne_ConfinesTheCursor_WhichStretchCouldNotExpress()
        {
            using (Provider(new TouchpadGestureSettings
            {
                PointerRegionAuthored = true,
                PointerRegionSizeY = 0.70f,
            }))
            {
                var y = new MappingSource { Descriptor = "Touchpad 0 Pointer Y", DeviceGuid = "region-dev" };
                // RCT3 Weno V0.1's vertical region: the finger sweeps 70% of
                // screen height, not the whole thing.
                Assert.Equal(-0.70f, Eval(TouchState(y: 0f), y), 3);
                Assert.Equal(0.70f, Eval(TouchState(y: 1f), y), 3);
            }
        }

        [Fact]
        public void RegionCenter_PlacesTheRectangle()
        {
            using (Provider(new TouchpadGestureSettings
            {
                PointerRegionAuthored = true,
                PointerRegionSizeX = 0.11f,
                PointerRegionCenterX = 0.09f,
            }))
            {
                // AOE II confines a pad to the bottom-left menu. Center 0.09
                // in screen fraction is -0.82 in NDC; the region spans that
                // point plus or minus 0.11.
                Assert.Equal(-0.82f, Eval(TouchState(0.5f), X()), 3);
                Assert.Equal(-0.93f, Eval(TouchState(0f), X()), 3);
                Assert.Equal(-0.71f, Eval(TouchState(1f), X()), 3);
            }
        }

        [Fact]
        public void PositiveControl_DefaultRegionIsStillTheFullScreenIdentityMap()
        {
            // Without this, every assertion above could pass on a read that
            // ignored the settings and returned the raw pad position.
            using (Provider(new TouchpadGestureSettings { PointerRegionAuthored = true }))
            {
                Assert.Equal(-1f, Eval(TouchState(0f), X()), 3);
                Assert.Equal(0f, Eval(TouchState(0.5f), X()), 3);
                Assert.Equal(1f, Eval(TouchState(1f), X()), 3);
            }
        }

        // ── The import handover ──────────────────────────────────────────

        [Fact]
        public void BeforeTheCardIsUsed_TheImportedSourceGeometryDrives()
        {
            // Import runs before a device is assigned, so a Steam
            // mouse_region cannot be written to per-device settings and
            // rides the mapping source instead.
            using (Provider(new TouchpadGestureSettings { PointerRegionAuthored = false }))
            {
                Assert.Equal(0.70f, Eval(TouchState(1f), X(extent: 0.70)), 3);
            }
        }

        [Fact]
        public void OnceTheCardIsUsed_ThePadSettingsWin()
        {
            using (Provider(new TouchpadGestureSettings
            {
                PointerRegionAuthored = true,
                PointerRegionSizeX = 0.50f,
            }))
            {
                // The source still carries the imported 0.70 and must now be
                // ignored, NOT multiplied in. Composing the two would give
                // 0.35 and is the "one setting layered on another" shape this
                // change exists to remove.
                Assert.Equal(0.50f, Eval(TouchState(1f), X(extent: 0.70)), 3);
            }
        }

        [Fact]
        public void ResettingToFullScreen_Sticks_EvenWithAnImportedRegionOnTheSource()
        {
            // The reason the handover is a FLAG and not a value test. Keyed on
            // "is the pad still at 0.5 / 1.0", a user who deliberately reset
            // the region to full screen would match that condition and get the
            // imported rectangle back, with no way to ever undo it.
            using (Provider(new TouchpadGestureSettings
            {
                PointerRegionAuthored = true,
                PointerRegionSizeX = 1.0f,
                PointerRegionCenterX = 0.5f,
            }))
            {
                Assert.Equal(1f, Eval(TouchState(1f), X(center: 0.09, extent: 0.11)), 3);
                Assert.Equal(-1f, Eval(TouchState(0f), X(center: 0.09, extent: 0.11)), 3);
            }
        }

        // ── Migration off the superseded stretch knob ────────────────────

        [Fact]
        public void LegacyStretchDeserializesIntoRegionSize_AndClaimsAuthorship()
        {
            // A profile written before the rename carries PointerStretchX.
            // Without the shim XmlSerializer drops the unknown attribute and
            // the user's tuning vanishes; without the authorship claim it
            // survives deserialization and is then ignored by the read.
            var s = new TouchpadGestureSettings { PointerStretchX = 1.5f };
            Assert.Equal(1.5f, s.PointerRegionSizeX);
            Assert.True(s.PointerRegionAuthored);

            using (Provider(s))
                Assert.Equal(1f, Eval(TouchState(0.833f), X()), 2);
        }

        [Fact]
        public void LegacyStretchIsNeverWrittenBack()
        {
            // Deserialize-only, so a file converges to the region names after
            // one save instead of carrying both spellings forever.
            var t = typeof(TouchpadGestureSettings);
            Assert.False((bool)t.GetMethod("ShouldSerializePointerStretchX")
                .Invoke(new TouchpadGestureSettings { PointerStretchX = 2f }, null));
            Assert.False((bool)t.GetMethod("ShouldSerializePointerStretchY")
                .Invoke(new TouchpadGestureSettings { PointerStretchY = 2f }, null));
        }

        // ── Mirror surfaces (lens 1m) ────────────────────────────────────

        private static PadSetting WithPad(Action<TouchpadGestureSettings> tune)
        {
            var ps = new PadSetting();
            var ts = new TouchpadGestureSettings();
            tune(ts);
            ps.TouchpadSettings = new[]
            { new TouchpadSettingsEntry { DeviceGuid = "dev", TouchpadIndex = 0, Settings = ts } };
            return ps;
        }

        [Fact]
        public void EveryRegionFieldReachesTheChecksum()
        {
            // Footprint closure, not a literal list: the profile snapshot
            // stores ONE PadSetting per distinct checksum and rejoins entries
            // by it, so a field missing here means two pads differing only in
            // it collide and the second silently adopts the first's rectangle.
            foreach (var name in new[] { "PointerRegionSizeX", "PointerRegionSizeY",
                                         "PointerRegionCenterX", "PointerRegionCenterY",
                                         "PointerRegionAuthored" })
            {
                var p = typeof(TouchpadGestureSettings).GetProperty(name);
                Assert.NotNull(p);
                var a = WithPad(_ => { });
                var b = WithPad(t =>
                {
                    if (p.PropertyType == typeof(bool)) p.SetValue(t, true);
                    else p.SetValue(t, (float)p.GetValue(t) + 0.37f);
                });
                Assert.True(a.ComputeChecksum() != b.ComputeChecksum(),
                    name + " does not reach ComputeChecksum, so two pads differing only in it "
                    + "collide on the profile snapshot's dedup and the second is dropped");
            }
        }

        [Fact]
        public void PositiveControl_IdenticalPadSettingsStillCollide()
        {
            Assert.Equal(WithPad(t => t.PointerRegionSizeX = 0.5f).ComputeChecksum(),
                         WithPad(t => t.PointerRegionSizeX = 0.5f).ComputeChecksum());
        }

        [Fact]
        public void EveryRegionFieldSurvivesClone()
        {
            var s = new TouchpadGestureSettings
            {
                PointerRegionSizeX = 0.11f,
                PointerRegionSizeY = 0.07f,
                PointerRegionCenterX = 0.09f,
                PointerRegionCenterY = 0.90f,
                PointerRegionAuthored = true,
            };
            var c = s.Clone();
            Assert.Equal(0.11f, c.PointerRegionSizeX);
            Assert.Equal(0.07f, c.PointerRegionSizeY);
            Assert.Equal(0.09f, c.PointerRegionCenterX);
            Assert.Equal(0.90f, c.PointerRegionCenterY);
            Assert.True(c.PointerRegionAuthored);
        }

        // ── The card must SHOW the imported region ───────────────────────

        private static MappingSet SetWithPointerRow(string target, string descriptor,
                                                    double center, double extent)
        {
            var row = new MappingRow { Target = target };
            row.Sources.Add(new MappingSource
            {
                Descriptor = descriptor,
                ParamPointerCenter = center,
                ParamPointerExtent = extent,
            });
            var set = new MappingSet();
            set.Rows.Add(row);
            return set;
        }

        [Fact]
        public void SeedFindsTheRegion_WhenTheRowsLiveInAnotherSlotsSet()
        {
            // The live layout that broke it, taken from the user's own
            // PadForge.xml after importing RCT3 Weno V0.1: the pointer rows
            // target KbmMouseX/Y and therefore sit in the KEYBOARD/MOUSE
            // slot's set (index 1), while the pad page being viewed is a
            // different slot. Searching only the current slot's set found
            // nothing and the card showed the full-screen default.
            var sets = new[]
            {
                new MappingSet(),                                                    // slot 0: Xbox
                SetWithPointerRow("KbmMouseX", "Touchpad 1 Pointer X", 0.5, 1.2),    // slot 1: KbM
            };

            Assert.True(PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 1, wantX: true, out double size, out double center));
            Assert.Equal(1.2, size, 3);
            Assert.Equal(0.5, center, 3);
        }

        [Fact]
        public void SeedReadsTheRealRctValues()
        {
            var sets = new[]
            {
                new MappingSet(),
                SetWithPointerRow("KbmMouseY", "Touchpad 1 Pointer Y", 0.44, 0.7),
            };
            Assert.True(PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 1, wantX: false, out double size, out double center));
            Assert.Equal(0.7, size, 3);
            Assert.Equal(0.44, center, 3);
        }

        [Fact]
        public void SeedShowsNothingForAPadTheConfigDoesNotMap()
        {
            // The region is PER PAD. A pad the config never mapped has no
            // rectangle, and the honest answer is the full-screen default.
            // An earlier cut fell back to any pad here, so selecting pad 1 or
            // pad 2 displayed the same rectangle and the per-pad card looked
            // broken. RCT3 Weno V0.1 maps only the right pad.
            var sets = new[]
            {
                SetWithPointerRow("KbmMouseX", "Touchpad 1 Pointer X", 0.5, 1.2),
            };
            Assert.False(PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 0, wantX: true, out double size, out double center));
            Assert.Equal(1.0, size, 3);
            Assert.Equal(0.5, center, 3);
        }

        [Fact]
        public void SeedShowsTheRegionForThePadThatDoesCarryOne()
        {
            // The other half of the pair, so the test above cannot pass on a
            // search that simply never finds anything.
            var sets = new[]
            {
                SetWithPointerRow("KbmMouseX", "Touchpad 1 Pointer X", 0.5, 1.2),
            };
            Assert.True(PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 1, wantX: true, out double size, out _));
            Assert.Equal(1.2, size, 3);
        }

        [Fact]
        public void TwoPadsWithTwoRegionsReadBackDifferently()
        {
            // The user-visible contract: switch the pad combo, see different
            // numbers.
            var sets = new[]
            {
                SetWithPointerRow("KbmMouseX", "Touchpad 0 Pointer X", 0.09, 0.11),
                SetWithPointerRow("KbmMouseX", "Touchpad 1 Pointer X", 0.50, 1.20),
            };
            PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 0, wantX: true, out double s0, out double c0);
            PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 1, wantX: true, out double s1, out double c1);
            Assert.Equal(0.11, s0, 3);
            Assert.Equal(0.09, c0, 3);
            Assert.Equal(1.20, s1, 3);
            Assert.Equal(0.50, c1, 3);
        }

        [Fact]
        public void SeedIgnoresTheOtherAxis()
        {
            var sets = new[]
            {
                SetWithPointerRow("KbmMouseX", "Touchpad 1 Pointer X", 0.5, 1.2),
            };
            Assert.False(PadForge.ViewModels.PadViewModel.FindPointerRegionAxis(
                sets, pad: 1, wantX: false, out _, out _));
        }

        [Fact]
        public void EveryRegionFieldHasAUiBindingAndAResetCommand()
        {
            // The bug this whole change fixes was a setting with no card. A
            // new region field added without one fails here on the day it is
            // added rather than shipping invisible.
            var vm = typeof(PadForge.ViewModels.PadViewModel);
            foreach (var axis in new[] { "SizeX", "SizeY", "CenterX", "CenterY" })
            {
                Assert.NotNull(vm.GetProperty("TouchpadPointerRegion" + axis));
                Assert.NotNull(vm.GetProperty("ResetTouchpadPointerRegion" + axis + "Command"));
            }
        }
    }
}
