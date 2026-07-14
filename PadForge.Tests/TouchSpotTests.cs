using System;
using System.Linq;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Touchpad;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #178 touch spots (Left / Right / Top / Multitouch held zones) and
    /// #177 gesture macro-trigger entries. The zone boundaries are the
    /// DS4Windows ones: left/right split at 2/5 of the width (DS4W
    /// Mouse.cs isLeft: HwX &lt; 1920 * 2 / 5), Multitouch at 2+ fingers,
    /// exactly one spot candidate at a time. Top is our own coordinate
    /// band (top quarter) because DS4W's "Upper Touch" is a DS4 sensor
    /// quirk with no coordinate to borrow.
    /// </summary>
    public class TouchSpotTests
    {
        private static TouchpadGestureSettings SpotSettings() => new TouchpadGestureSettings
        {
            Enabled = true,
            EnableTouchSpots = true,
            Mode = "Both",
        };

        private static TouchpadInputState Pad(int fingers = 2) => new TouchpadInputState(fingers);

        private static void SetFinger(TouchpadInputState pad, int slot, bool down, float x, float y, int contactId)
        {
            pad.FingerDown[slot] = down;
            pad.FingerX[slot] = x;
            pad.FingerY[slot] = y;
            pad.FingerContactId[slot] = down ? contactId : -1;
        }

        private static readonly string[] SpotNames =
            { "TouchLeft", "TouchRight", "TouchTop", "TouchMulti" };

        private static string[] SpotKeys(TouchpadGestureContext ctx) =>
            ctx.FiredGesturesThisFrame
               .Where(k => SpotNames.Any(n => k.EndsWith(n, StringComparison.Ordinal)))
               .ToArray();

        [Theory]
        [InlineData(0.20f, 0.50f, "TouchLeft")]
        [InlineData(0.70f, 0.50f, "TouchRight")]
        [InlineData(0.50f, 0.10f, "TouchTop")]
        [InlineData(0.39f, 0.50f, "TouchLeft")]   // just inside the 2/5 split
        [InlineData(0.40f, 0.50f, "TouchRight")]  // at the split: DS4W isRight (HwX >= 768)
        [InlineData(0.10f, 0.24f, "TouchTop")]    // top band wins over left
        [InlineData(0.10f, 0.25f, "TouchLeft")]   // at the band edge: not top
        public void SingleFinger_ClassifiesExactlyOneSpot(float x, float y, string expected)
        {
            var ctx = new TouchpadGestureContext();
            var pad = Pad();
            SetFinger(pad, 0, true, x, y, 1);
            GestureRecognizer.Update(0, ctx, pad, SpotSettings(), 100);

            var spots = SpotKeys(ctx);
            Assert.Equal(new[] { $"Touchpad 0 {expected}" }, spots);
        }

        [Fact]
        public void Slide_AcrossTheSplit_ReleasesOldSpot_PressesNew()
        {
            var ctx = new TouchpadGestureContext();
            var settings = SpotSettings();
            var pad = Pad();

            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, settings, 100);
            Assert.Contains("Touchpad 0 TouchLeft", ctx.FiredGesturesThisFrame);

            SetFinger(pad, 0, true, 0.8f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, settings, 116);
            Assert.DoesNotContain("Touchpad 0 TouchLeft", ctx.FiredGesturesThisFrame);
            Assert.Contains("Touchpad 0 TouchRight", ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void TwoFingers_HoldMultitouch_AndSuppressCoordinateSpots()
        {
            var ctx = new TouchpadGestureContext();
            var settings = SpotSettings();
            var pad = Pad();

            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            SetFinger(pad, 1, true, 0.8f, 0.5f, 2);
            GestureRecognizer.Update(0, ctx, pad, settings, 100);

            Assert.Equal(new[] { "Touchpad 0 TouchMulti" }, SpotKeys(ctx));

            // Second finger lifts: back to the remaining finger's zone.
            SetFinger(pad, 1, false, 0.8f, 0.5f, -1);
            GestureRecognizer.Update(0, ctx, pad, settings, 116);
            Assert.Equal(new[] { "Touchpad 0 TouchLeft" }, SpotKeys(ctx));
        }

        [Fact]
        public void Lift_ReleasesTheSpotImmediately_NoCooldownLatch()
        {
            var ctx = new TouchpadGestureContext();
            var settings = SpotSettings();
            var pad = Pad();

            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, settings, 100);
            Assert.Contains("Touchpad 0 TouchLeft", ctx.FiredGesturesThisFrame);

            SetFinger(pad, 0, false, 0.2f, 0.5f, -1);
            GestureRecognizer.Update(0, ctx, pad, settings, 116);

            // Radial zones latch through the cooldown on purpose; spots
            // are held buttons and must let go the tick the finger lifts.
            Assert.Equal(GestureState.Cooldown, ctx.State);
            Assert.DoesNotContain("Touchpad 0 TouchLeft", ctx.FiredGesturesThisFrame);
            Assert.Null(ctx.CurrentTouchSpot);
        }

        [Fact]
        public void CategoryDisabled_FiresNothing()
        {
            var ctx = new TouchpadGestureContext();
            var settings = SpotSettings();
            settings.EnableTouchSpots = false;
            var pad = Pad();

            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, settings, 100);
            Assert.Empty(SpotKeys(ctx));
        }

        [Fact]
        public void GateTurnedOffMidHold_ReleasesTheHeldSpot()
        {
            var ctx = new TouchpadGestureContext();
            var settings = SpotSettings();
            var pad = Pad();

            SetFinger(pad, 0, true, 0.2f, 0.5f, 1);
            GestureRecognizer.Update(0, ctx, pad, settings, 100);
            Assert.Contains("Touchpad 0 TouchLeft", ctx.FiredGesturesThisFrame);

            settings.EnableTouchSpots = false;
            GestureRecognizer.Update(0, ctx, pad, settings, 116);
            Assert.Empty(SpotKeys(ctx));
            Assert.Null(ctx.CurrentTouchSpot);
        }

        [Fact]
        public void PadIndex_FlowsIntoTheKey()
        {
            var ctx = new TouchpadGestureContext();
            var pad = Pad();
            SetFinger(pad, 0, true, 0.7f, 0.5f, 1);
            GestureRecognizer.Update(1, ctx, pad, SpotSettings(), 100);
            Assert.Contains("Touchpad 1 TouchRight", ctx.FiredGesturesThisFrame);
        }

        [Fact]
        public void Settings_CloneCarriesEnableTouchSpots()
        {
            var s = new TouchpadGestureSettings { EnableTouchSpots = true };
            Assert.True(s.Clone().EnableTouchSpots);
        }

        // ─── #177: gesture macro-trigger entry spec round-trip ───

        [Theory]
        [InlineData("Touchpad 0 TouchLeft")]
        [InlineData("Touchpad 1 SwipeUp")]
        [InlineData("Touchpad 0 RadialZone8_3")]
        [InlineData("Touchpad 0 Custom_My Shape")]
        [InlineData("Touchpad 0 Custom_a:b|c")] // ':' and '|' are legal in names
        public void GestureTriggerEntry_SpecRoundTrips(string descriptor)
        {
            var entry = new MacroItem.TriggerInputEntry
            {
                DeviceGuid = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                GestureDescriptor = descriptor,
            };
            var parsed = MacroItem.TriggerInputEntry.Parse(entry.Spec);
            Assert.NotNull(parsed);
            Assert.Equal(entry.DeviceGuid, parsed.DeviceGuid);
            Assert.Equal(descriptor, parsed.GestureDescriptor);
            Assert.Equal(-1, parsed.RawButton);
            Assert.Null(parsed.Pov);
        }

        [Fact]
        public void LegacySpecs_StillParse()
        {
            var btn = MacroItem.TriggerInputEntry.Parse("in:11111111-1111-1111-1111-111111111111:btn:5");
            Assert.Equal(5, btn.RawButton);
            var pov = MacroItem.TriggerInputEntry.Parse("in:11111111-1111-1111-1111-111111111111:pov:0:9000");
            Assert.Equal("0:9000", pov.Pov);
            Assert.Null(btn.GestureDescriptor);
            Assert.Null(pov.GestureDescriptor);
        }

        [Fact]
        public void TryBuildTriggerEntry_ConvertsTheEntryShapes()
        {
            const string g = "11111111-1111-1111-1111-111111111111";
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Button 4", DeviceGuid = g }, out var btn));
            Assert.Equal(4, btn.RawButton);

            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Touchpad 0 Click", DeviceGuid = g }, out var click));
            Assert.Equal(16, click.RawButton);

            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "POV 0 Right", DeviceGuid = g }, out var pov));
            Assert.Equal("0:9000", pov.Pov);

            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Touchpad 0 TouchTop", DeviceGuid = g }, out var spot));
            Assert.Equal("Touchpad 0 TouchTop", spot.GestureDescriptor);

            // Continuous gesture axes and finger axes have no bool entry.
            Assert.False(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Touchpad 0 PinchAxis", DeviceGuid = g }, out _));
            Assert.False(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Touchpad 0 Finger 0 X", DeviceGuid = g }, out _));
            // An empty device guid converts to a device-free entry (#9 B-9,
            // the "(Any device)" picker group); an unparsable guid still
            // rejects.
            Assert.True(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Button 4", DeviceGuid = "" }, out var anyDev));
            Assert.Equal(System.Guid.Empty, anyDev.DeviceGuid);
            Assert.False(MacroItem.TryBuildTriggerEntry(
                new InputChoice { Descriptor = "Button 4", DeviceGuid = "not-a-guid" }, out _));
        }

        // ─── The migrator keeps the new descriptors byte-identical ───

        [Theory]
        [InlineData("Touchpad 0 TouchLeft")]
        [InlineData("Touchpad 0 TouchMulti")]
        public void BuildFromLegacy_KeepsTouchSpotDescriptorsIntact(string descriptor)
        {
            var ps = new PadSetting { LeftThumbAxisX = descriptor };
            var ms = MappingSetMigrator.BuildFromLegacy(
                0, new[] { ("11111111-1111-1111-1111-111111111111", ps) });

            var row = ms.Rows.FirstOrDefault(r => r.Target == "LeftThumbAxisX");
            Assert.NotNull(row);
            var src = Assert.Single(row.Sources);
            Assert.Equal(descriptor, src.Descriptor);
            Assert.False(src.Invert);
        }
    }
}
