using System;
using PadForge.Engine.Touchpad;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The pointer region is the ONE touchpad setting that resolves per pad.
    /// Everything else on TouchpadGestureSettings describes a user's hands and
    /// stays device-wide. A region is where on screen one particular pad
    /// points, and a config routinely gives two pads two different rectangles:
    /// AOE II's Steam Deck layout maps the left pad to the bottom-left menu
    /// (center 0.09/0.90, size 0.11 x 0.07) and the right pad to a wide
    /// shallow band. Collapsing those to one device rectangle loses the layout.
    /// </summary>
    public class PointerRegionPerPadTests
    {
        private const string Dev = "per-pad-dev";

        private static TouchpadSettingsEntry Entry(int pad, bool authored,
            float sizeX = 1f, float sizeY = 1f, float cx = 0.5f, float cy = 0.5f) =>
            new TouchpadSettingsEntry
            {
                DeviceGuid = Dev,
                TouchpadIndex = pad,
                Settings = new TouchpadGestureSettings
                {
                    PointerRegionAuthored = authored,
                    PointerRegionSizeX = sizeX,
                    PointerRegionSizeY = sizeY,
                    PointerRegionCenterX = cx,
                    PointerRegionCenterY = cy,
                },
            };

        [Fact]
        public void TwoPadsKeepTwoDifferentRectangles()
        {
            // The fidelity case. Before per-pad resolution the second pad's
            // rectangle was unreachable and both pads read the same one.
            var entries = new[]
            {
                Entry(0, authored: true, sizeX: 0.11f, sizeY: 0.07f, cx: 0.09f, cy: 0.90f),
                Entry(1, authored: true, sizeX: 1.20f, sizeY: 0.70f, cx: 0.50f, cy: 0.44f),
            };

            var p0 = TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 0).Settings;
            var p1 = TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 1).Settings;

            Assert.Equal(0.11f, p0.PointerRegionSizeX);
            Assert.Equal(0.09f, p0.PointerRegionCenterX);
            Assert.Equal(1.20f, p1.PointerRegionSizeX);
            Assert.Equal(0.44f, p1.PointerRegionCenterY);
        }

        [Fact]
        public void PositiveControl_TheTwoPadsAreActuallyDistinct()
        {
            // Without this the test above could pass on a resolver that
            // returned the same entry twice if both happened to hold equal
            // values.
            var entries = new[]
            {
                Entry(0, authored: true, sizeX: 0.11f),
                Entry(1, authored: true, sizeX: 1.20f),
            };
            Assert.NotSame(
                TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 0),
                TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 1));
        }

        [Fact]
        public void AnUnauthoredPadFallsBackToTheDeviceEntry_NotToAnotherPadsRegion()
        {
            // A device that never had a per-pad region authored must behave
            // exactly as it did before, and pad 1 must never inherit pad 0's
            // rectangle by accident.
            var entries = new[]
            {
                Entry(0, authored: true, sizeX: 0.4f),
            };
            var p1 = TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 1);
            Assert.NotNull(p1);
            Assert.Equal(0, p1.TouchpadIndex);
        }

        [Fact]
        public void AnUnauthoredPadEntryDoesNotShadowTheDeviceEntry()
        {
            // A stale index-1 entry with no authored region must not win over
            // the device-wide one, or a pad the user never configured would
            // read a full-screen default while the device carries a region.
            var entries = new[]
            {
                Entry(0, authored: true, sizeX: 0.4f),
                Entry(1, authored: false),
            };
            var p1 = TouchpadGestureSettings.ResolveRegionEntryForPad(entries, Dev, 1);
            Assert.Equal(0.4f, p1.Settings.PointerRegionSizeX);
        }

        [Fact]
        public void UnknownDeviceResolvesToNothing()
        {
            Assert.Null(TouchpadGestureSettings.ResolveRegionEntryForPad(
                new[] { Entry(0, authored: true) }, "some-other-device", 0));
        }
    }
}
