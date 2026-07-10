using System;
using PadForge.Engine.Data;
using PadForge.Engine.Mouse;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Pins the #200 mouse-gesture contracts: the recognizer's classify-at-
    /// release semantics and latch-through-cooldown pulse, the settings
    /// persistence legs (checksum inclusion, the dedup-by-checksum trap),
    /// and the descriptor grammar.
    /// </summary>
    public class MouseGestureTests
    {
        private static MouseGestureSettings Enabled(int threshold = 150, int cooldown = 100, int button = 3)
            => new MouseGestureSettings
            {
                Enabled = true,
                GestureButton = button,
                FlickThresholdCounts = threshold,
                CooldownMs = cooldown,
            };

        // Drives one hold-flick-release cycle and returns the fired set.
        private static MouseGestureContext Flick(double dx, double dy, int threshold = 150)
        {
            var ctx = new MouseGestureContext();
            var s = Enabled(threshold);
            long now = 1000;
            MouseGestureRecognizer.Update(ctx, s, buttonDown: true, 0, 0, now);
            MouseGestureRecognizer.Update(ctx, s, buttonDown: true, dx, dy, now + 10);
            MouseGestureRecognizer.Update(ctx, s, buttonDown: false, 0, 0, now + 20);
            return ctx;
        }

        [Fact]
        public void Below_Threshold_Fires_Click()
        {
            var ctx = Flick(40, -30);
            Assert.Contains("Click", ctx.FiredGesturesThisFrame);
            Assert.Single(ctx.FiredGesturesThisFrame);
        }

        [Theory]
        [InlineData(-500, 20, "Left")]
        [InlineData(500, -20, "Right")]
        [InlineData(10, -500, "Up")]
        [InlineData(-10, 500, "Down")]
        public void Dominant_Axis_And_Sign_Classify(double dx, double dy, string expected)
        {
            var ctx = Flick(dx, dy);
            Assert.Contains(expected, ctx.FiredGesturesThisFrame);
            Assert.Single(ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Fires_Exactly_Once_And_Latches_Until_Cooldown_Expiry()
        {
            var ctx = new MouseGestureContext();
            var s = Enabled(threshold: 100, cooldown: 100);
            MouseGestureRecognizer.Update(ctx, s, true, 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s, true, 300, 0, 1010);
            MouseGestureRecognizer.Update(ctx, s, false, 0, 0, 1020);
            Assert.Contains("Right", ctx.FiredGesturesThisFrame);

            // Still asserted inside the cooldown window (slow consumers).
            MouseGestureRecognizer.Update(ctx, s, false, 0, 0, 1060);
            Assert.Contains("Right", ctx.FiredGesturesThisFrame);

            // Cleared at expiry.
            MouseGestureRecognizer.Update(ctx, s, false, 0, 0, 1121);
            Assert.Empty(ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Fresh_Press_Clears_Prior_Latch()
        {
            var ctx = new MouseGestureContext();
            var s = Enabled(threshold: 100, cooldown: 10000);
            MouseGestureRecognizer.Update(ctx, s, true, 0, 0, 1000);
            MouseGestureRecognizer.Update(ctx, s, true, 300, 0, 1010);
            MouseGestureRecognizer.Update(ctx, s, false, 0, 0, 1020);
            Assert.Contains("Right", ctx.FiredGesturesThisFrame);

            // New gesture starts before the (long) cooldown expires: the old
            // fire must not bleed into the new window.
            MouseGestureRecognizer.Update(ctx, s, true, 0, 0, 1030);
            Assert.Empty(ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Disabled_Resets_State()
        {
            var ctx = new MouseGestureContext();
            var s = Enabled();
            MouseGestureRecognizer.Update(ctx, s, true, 200, 0, 1000);
            s.Enabled = false;
            MouseGestureRecognizer.Update(ctx, s, true, 200, 0, 1010);
            Assert.False(ctx.ButtonWasDown);
            Assert.Equal(0, ctx.AccumDx);
        }

        [Fact]
        public void Descriptor_Grammar_Classifies_And_Parses()
        {
            Assert.Equal(SourceCoercion.SourceType.MouseGesture,
                SourceCoercion.ClassifyDescriptor("Mouse Gesture Left"));
            // No shadowing of the sibling Mouse families.
            Assert.Equal(SourceCoercion.SourceType.MouseCursor,
                SourceCoercion.ClassifyDescriptor("Mouse Position X"));
            Assert.Equal(SourceCoercion.SourceType.JoyCon2Mouse,
                SourceCoercion.ClassifyDescriptor("Mouse Motion X"));
            Assert.True(SourceCoercion.IsMouseGestureDescriptor("Mouse Gesture Click"));
            Assert.False(SourceCoercion.IsMouseGestureDescriptor("Mouse Motion X"));
            Assert.Equal("Down", SourceCoercion.ParseMouseGestureName("Mouse Gesture Down"));
        }

        [Fact]
        public void Settings_Clone_And_Xml_Defaults()
        {
            var s = new MouseGestureSettings { Enabled = true, GestureButton = 4, FlickThresholdCounts = 300, CooldownMs = 250 };
            var c = s.Clone();
            Assert.True(c.Enabled);
            Assert.Equal(4, c.GestureButton);
            Assert.Equal(300, c.FlickThresholdCounts);
            Assert.Equal(250, c.CooldownMs);
            var d = MouseGestureSettings.Default();
            Assert.False(d.Enabled);
            Assert.Equal(3, d.GestureButton);
        }

        [Fact]
        public void Checksum_Differs_When_MouseGestureSettings_Differ()
        {
            // The dedup-by-checksum trap: two PadSettings identical except for
            // the mouse-gesture sub-tree must hash differently, or SaveToFile
            // silently drops one and toggles revert on relaunch.
            var a = new PadSetting();
            var b = new PadSetting();
            b.MouseGestureSettings = new[]
            {
                new MouseGestureSettingsEntry
                {
                    DeviceGuid = "11111111-2222-3333-4444-555555555555",
                    Settings = new MouseGestureSettings { Enabled = true },
                },
            };
            a.UpdateChecksum();
            b.UpdateChecksum();
            Assert.NotEqual(a.PadSettingChecksum, b.PadSettingChecksum);

            // Entry ORDER must not change the hash (content-defined).
            var c = new PadSetting
            {
                MouseGestureSettings = new[]
                {
                    new MouseGestureSettingsEntry { DeviceGuid = "b", Settings = new MouseGestureSettings { Enabled = true } },
                    new MouseGestureSettingsEntry { DeviceGuid = "a", Settings = new MouseGestureSettings() },
                },
            };
            var d = new PadSetting
            {
                MouseGestureSettings = new[]
                {
                    new MouseGestureSettingsEntry { DeviceGuid = "a", Settings = new MouseGestureSettings() },
                    new MouseGestureSettingsEntry { DeviceGuid = "b", Settings = new MouseGestureSettings { Enabled = true } },
                },
            };
            c.UpdateChecksum();
            d.UpdateChecksum();
            Assert.Equal(c.PadSettingChecksum, d.PadSettingChecksum);
        }

        [Fact]
        public void CopyFrom_DeepCopies_MouseGestureSettings()
        {
            var src = new PadSetting
            {
                MouseGestureSettings = new[]
                {
                    new MouseGestureSettingsEntry
                    {
                        DeviceGuid = "abc",
                        Settings = new MouseGestureSettings { Enabled = true, FlickThresholdCounts = 200 },
                    },
                },
            };
            var dst = new PadSetting();
            dst.CopyFrom(src);
            Assert.NotNull(dst.MouseGestureSettings);
            Assert.Single(dst.MouseGestureSettings);
            Assert.True(dst.MouseGestureSettings[0].Settings.Enabled);
            // Deep, not shared.
            dst.MouseGestureSettings[0].Settings.FlickThresholdCounts = 999;
            Assert.Equal(200, src.MouseGestureSettings[0].Settings.FlickThresholdCounts);
        }
    }
}
