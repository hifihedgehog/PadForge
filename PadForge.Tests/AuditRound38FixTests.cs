using System;
using System.Linq;
using System.Reflection;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round 38 audited the delta since round 37: the gyro and touchpad
    /// rate lanes, the Workshop vocabulary work, and the touchpad mouse
    /// settings. Both defects it found were MIRROR-SURFACE gaps (lens 1m):
    /// a new field or lane added to a source of truth, and a surface that
    /// reproduces that source not updated to carry it.
    /// </summary>
    public class AuditRound38FixTests
    {
        private delegate void RefMutate(ref KbmRawState s);

        // ── 1m-2: the profile-snapshot checksum ───────────────────────────

        private static PadSetting WithTouchpad(Action<TouchpadGestureSettings> tune)
        {
            var ps = new PadSetting();
            var ts = new TouchpadGestureSettings();
            tune(ts);
            ps.TouchpadSettings = new[]
            { new TouchpadSettingsEntry { DeviceGuid = "dev", TouchpadIndex = 0, Settings = ts } };
            return ps;
        }

        [Fact]
        public void Checksum_SeparatesSettingsThatDifferOnlyInMomentum()
        {
            // The profile snapshot stores ONE PadSetting per distinct
            // checksum and rejoins entries by it, so a collision here means
            // the second device silently adopts the first device's glide.
            var a = WithTouchpad(t => t.MouseMomentum = false);
            var b = WithTouchpad(t => t.MouseMomentum = true);
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
        }

        [Fact]
        public void Checksum_SeparatesSettingsThatDifferOnlyInGlide()
        {
            var a = WithTouchpad(t => { t.MouseMomentum = true; t.MouseMomentumDecay = 0.85f; });
            var b = WithTouchpad(t => { t.MouseMomentum = true; t.MouseMomentumDecay = 0.95f; });
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
        }

        [Fact]
        public void Checksum_SeparatesSettingsThatDifferOnlyInJitterReduction()
        {
            var a = WithTouchpad(t => t.MouseJitterReduction = true);
            var b = WithTouchpad(t => t.MouseJitterReduction = false);
            Assert.NotEqual(a.ComputeChecksum(), b.ComputeChecksum());
        }

        [Fact]
        public void PositiveControl_IdenticalTouchpadSettingsStillCollide()
        {
            // Proves the three tests above measure the FIELDS and not some
            // incidental difference between two freshly built objects. If
            // this fails, every NotEqual above is vacuously true.
            var a = WithTouchpad(t => t.MouseMomentum = true);
            var b = WithTouchpad(t => t.MouseMomentum = true);
            Assert.Equal(a.ComputeChecksum(), b.ComputeChecksum());
        }

        [Fact]
        public void EveryTouchpadMouseSettingReachesTheChecksum()
        {
            // Footprint closure rather than a literal list: the next field
            // added to the mouse-output family fails here on the day it is
            // added, instead of shipping as a silent dedup collision.
            // Mouse* by prefix, plus the pointer-profile family by name: the
            // trio does not share the prefix, and round 40 found it riding
            // the checksum on manual inclusion alone, one deletion away from
            // a silent dedup collision.
            var mouseFields = typeof(TouchpadGestureSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => (p.Name.StartsWith("Mouse", StringComparison.Ordinal)
                             || p.Name == "PointerResponse"
                             || p.Name.StartsWith("Trackpad", StringComparison.Ordinal))
                            && p.CanRead && p.CanWrite)
                .ToList();
            Assert.True(mouseFields.Count >= 10, $"only {mouseFields.Count} mouse/pointer settings found");

            foreach (var f in mouseFields)
            {
                var baseline = new PadSetting();
                baseline.TouchpadSettings = new[]
                { new TouchpadSettingsEntry { DeviceGuid = "dev", TouchpadIndex = 0,
                                              Settings = new TouchpadGestureSettings() } };

                var altered = new PadSetting();
                var ts = new TouchpadGestureSettings();
                // Perturb this one field away from its default.
                if (f.PropertyType == typeof(bool))
                    f.SetValue(ts, !(bool)f.GetValue(ts));
                else if (f.PropertyType == typeof(float))
                    f.SetValue(ts, (float)f.GetValue(ts) + 0.37f);
                else if (f.PropertyType == typeof(string))
                    f.SetValue(ts, (f.GetValue(ts) as string ?? "") + "X");
                else continue;
                altered.TouchpadSettings = new[]
                { new TouchpadSettingsEntry { DeviceGuid = "dev", TouchpadIndex = 0, Settings = ts } };

                Assert.True(baseline.ComputeChecksum() != altered.ComputeChecksum(),
                    $"{f.Name} does not reach ComputeChecksum, so two devices differing only "
                    + "in it collide on the profile snapshot's dedup and the second is dropped");
            }
        }

        // ── 1m-1: the KBM preview's change gate ───────────────────────────

        [Fact]
        public void PreviewChangeGate_NoticesEveryLaneThatMovesTheCursor()
        {
            // The gate decides whether to repaint. A lane it does not compare
            // is a lane whose motion the preview never shows: gyro and
            // touchpad stopped writing MouseDeltaX/Y when they moved to their
            // own exact-counts lanes, so a gate naming only that pair left the
            // preview reporting a still cursor while the real one moved.
            var rest = new KbmRawState();

            // KbmRawState is a STRUCT, so an Action<KbmRawState> receives a
            // COPY and the mutation never reaches the caller's instance. A
            // harness written that way compares two resting states and passes
            // no matter what the gate does. Take it by ref.
            void AssertNoticed(string lane, RefMutate set)
            {
                var moved = new KbmRawState();
                set(ref moved);
                Assert.False(PadForge.Views.KBMPreviewView.SamePreviewState(rest, moved),
                    $"the preview gate ignores {lane}, so motion on that lane never repaints");
            }

            AssertNoticed("MouseDeltaX", (ref KbmRawState s) => s.MouseDeltaX = 4000);
            AssertNoticed("MouseDeltaY", (ref KbmRawState s) => s.MouseDeltaY = 4000);
            AssertNoticed("MouseGyroX", (ref KbmRawState s) => s.MouseGyroX = 3f);
            AssertNoticed("MouseGyroY", (ref KbmRawState s) => s.MouseGyroY = 3f);
            AssertNoticed("MouseTouchX", (ref KbmRawState s) => s.MouseTouchX = 3f);
            AssertNoticed("MouseTouchY", (ref KbmRawState s) => s.MouseTouchY = 3f);
            AssertNoticed("MouseFlickX", (ref KbmRawState s) => s.MouseFlickX = 300);
        }

        [Fact]
        public void PositiveControl_TwoRestingStatesCompareEqual()
        {
            // Without this the test above passes on a gate that returns false
            // unconditionally, which would repaint every frame and hide the
            // very thing it is meant to prove.
            Assert.True(PadForge.Views.KBMPreviewView.SamePreviewState(
                new KbmRawState(), new KbmRawState()));
        }
    }
}
