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
