using System;
using System.IO;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Wii Remote as a light gun (#485): the IR pointer read holds the last
    /// aim through sight loss, the aim is twist-compensated the way Touchmote's
    /// pointer_considerRotation does it, and the remote takes the GunCon 2's
    /// four-target calibration.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WiiLightGunTests
    {
        // ── Holding the aim through sight loss ──

        private static float Read(CustomInputState state, MappingSource src, int slot)
            => SourceCoercion.EvaluateForBipolarAxisTarget(state, src, slotIndex: slot);

        private static CustomInputState Aim(float x)
        {
            var s = new CustomInputState();
            s.Ir.X = x;
            s.Ir.Detected = true;
            return s;
        }

        private static CustomInputState Lost()
        {
            var s = new CustomInputState();
            s.Ir.Detected = false;
            return s;
        }

        [Fact]
        public void SightLoss_HoldsTheLastAim_WithEachSourcesOwnSensitivity()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var full = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "hold-a" };
                var half = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "hold-a", IrPointerSensitivity = 0.5 };

                // Aim 0.25 stretches to 0.45 (the lineage's x1.8).
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, Read(Aim(0.25f), full, 0), precision: 5);
                Assert.Equal(0.225f, Read(Aim(0.25f), half, 0), precision: 5);

                // The bar leaves the camera's view: both rows keep their
                // reading instead of jumping to the middle of the screen.
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, Read(Lost(), full, 0), precision: 5);
                Assert.Equal(0.225f, Read(Lost(), half, 0), precision: 5);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.45f, Read(Lost(), full, 0), precision: 5);

                // The bar comes back: the read follows the new aim at once.
                SourceCoercion.BeginPollFrame();
                Assert.Equal(-0.36f, Read(Aim(-0.2f), full, 0), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        [Fact]
        public void SightLoss_IsHeldPerSlot_AndASlotThatNeverSawTheBarReadsCenter()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "hold-b" };
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.9f, Read(Aim(0.5f), src, 2), precision: 5);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.9f, Read(Lost(), src, 2), precision: 5);
                Assert.Equal(0f, Read(Lost(), src, 3), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        /// <summary>The GunCon 2's Gun Aim X and Y are the IR Pointer sources
        /// under the gun's names, so its aim holds while it points off the
        /// screen to reload.</summary>
        [Fact]
        public void AGunCon2_HoldsItsAimOffTheScreen()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var src = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "hold-gun" };
                var (aimX, _, _) = SdlDeviceWrapper.GunCon2Aim(584, 130, GunCon2Calibration.Default);
                var state = new CustomInputState();
                SdlDeviceWrapper.ApplyGunCon2(state, 584, 130, GunCon2Calibration.Default);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(aimX, Read(state, src, 0), precision: 5);

                state = new CustomInputState();
                SdlDeviceWrapper.ApplyGunCon2(state, 5, 130, GunCon2Calibration.Default);
                Assert.False(state.Ir.Detected);
                SourceCoercion.BeginPollFrame();
                Assert.Equal(aimX, Read(state, src, 0), precision: 5);
                Assert.NotEqual(0f, aimX);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        [Fact]
        public void ForgettingTheDevice_DropsTheHeldAim()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var src = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "hold-c" };
                var s = new CustomInputState();
                s.Ir.Y = 0.3f;
                s.Ir.Detected = true;
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.6f, Read(s, src, 0), precision: 5);

                // The removal path names the device in any case.
                SourceCoercion.ForgetIrPointerForDevice("HOLD-C");
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, Read(Lost(), src, 0), precision: 5);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        // ── Twist compensation, Touchmote's pointer_considerRotation ──

        private const float G = 9.80665f;

        [Theory]
        [InlineData(0f, G, 0, 0)]          // face up: upright
        [InlineData(0f, -G, 0, 2)]         // face down: upside down
        [InlineData(G, 0f, 0, 3)]          // +X down the side
        [InlineData(-G, 0f, 0, 1)]         // -X
        [InlineData(1f, 1f, 2, 2)]         // both under the 0.2 g margin: no change
        [InlineData(6f, 5f, 0, 0)]         // X leads by less than the margin: stays upright
        [InlineData(-5f, 6f, 1, 1)]        // Z leads by less than the margin: stays on its side
        [InlineData(-5f, 8f, 1, 0)]        // Z leads by more: upright
        public void Orientation_FollowsTheLeadingAxis_WithTouchmotesMargin(float x, float z, int from, int expected)
            => Assert.Equal(expected, SdlDeviceWrapper.UpdateIrOrientation(from, x, z));

        [Theory]
        [InlineData(0, 400, 384, 600, 384, 0)]   // upright: the smaller X is left
        [InlineData(0, 600, 384, 400, 384, 1)]
        [InlineData(0, 500, 300, 500, 400, 1)]   // a tie goes to dot 1
        [InlineData(2, 600, 384, 400, 384, 0)]   // upside down: the larger X
        [InlineData(1, 500, 500, 500, 300, 0)]   // orientation 1: the larger Y
        [InlineData(3, 500, 300, 500, 500, 0)]   // orientation 3: the smaller Y
        public void LeftDot_FollowsTouchmotesTable(int orientation, int d0x, int d0y, int d1x, int d1y, int expected)
            => Assert.Equal(expected, SdlDeviceWrapper.PickIrLeftDot(orientation,
                (short)d0x, (short)d0y, (short)d1x, (short)d1y));

        [Fact]
        public void PairAngle_IsTheLeftToRightVectorInPixels()
        {
            Assert.Equal(0f, SdlDeviceWrapper.IrPairAngle(0, 400, 384, 600, 384), precision: 6);
            Assert.Equal((float)Math.Atan2(100, 200), SdlDeviceWrapper.IrPairAngle(0, 400, 300, 600, 400), precision: 6);
            // The same dots with dot 1 as the left LED point the other way.
            Assert.Equal((float)Math.Atan2(-100, -200), SdlDeviceWrapper.IrPairAngle(1, 400, 300, 600, 400), precision: 6);
            // Two dots on one pixel give 0 rather than NaN.
            Assert.Equal(0f, SdlDeviceWrapper.IrPairAngle(0, 500, 300, 500, 300));
        }

        /// <summary>The turn runs in camera pixels, which are square: a
        /// quarter turn carries an aim at the edge of the picture's width the
        /// same number of pixels below its center, past the bottom edge of the
        /// shorter height. A turn and its inverse cancel.</summary>
        [Fact]
        public void Rotation_TurnsTheAimInCameraPixels()
        {
            var (x, y) = SdlDeviceWrapper.RotateIrAim(1f, 0f, (float)(Math.PI / 2));
            Assert.Equal(0f, x, precision: 5);
            Assert.Equal(1023.5f / 767.5f, y, precision: 4);
            var (bx, by) = SdlDeviceWrapper.RotateIrAim(0.3f, -0.2f, 0.7f);
            var (rx, ry) = SdlDeviceWrapper.RotateIrAim(bx, by, -0.7f);
            Assert.Equal(0.3f, rx, precision: 5);
            Assert.Equal(-0.2f, ry, precision: 5);
        }

        private static long Ms(int ms) => ms * System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <summary>A rolled remote aims where the same remote held level aims.
        /// Rolling the remote turns the camera's picture about its center, so
        /// the rolled dots are the level dots turned about the center in
        /// pixels, and the compensated aim must land on the level aim. The
        /// model here shares no code with the compensation. A turn in the
        /// aim's unevenly scaled units misses the level aim at every one of
        /// these angles.</summary>
        [Theory]
        [InlineData(15)]
        [InlineData(-15)]
        [InlineData(30)]
        [InlineData(-30)]
        [InlineData(45)]
        [InlineData(-45)]
        public void ARolledRemote_AimsWhereTheLevelRemoteAims(int degrees)
        {
            const double cx = 1023.5 / 2, cy = 767.5 / 2;
            double t = degrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
            short Px(int x, int y) => (short)Math.Round(cx + (x - cx) * c - (y - cy) * s);
            short Py(int x, int y) => (short)Math.Round(cy + (x - cx) * s + (y - cy) * c);

            // Level pairs 300 pixels apart: a quarter of the picture's width
            // out, a quarter of its height up, and off toward a lower corner.
            foreach (var (mx, my) in new[] { (768, 384), (512, 192), (312, 534) })
            {
                int lx = mx - 150, rx = mx + 150;
                short d0x = Px(lx, my), d0y = Py(lx, my), d1x = Px(rx, my), d1y = Py(rx, my);
                Assert.InRange(d0x, (short)0, (short)1023);
                Assert.InRange(d1x, (short)0, (short)1023);
                Assert.InRange(d0y, (short)0, (short)767);
                Assert.InRange(d1y, (short)0, (short)767);

                var w = new SdlDeviceWrapper();
                var (x, y, det, _) = w.StepIrPointer(d0x, d0y, d1x, d1y, 0f, G, Ms(0), trigger: false);
                var (ex, ey, _) = SdlDeviceWrapper.ComputeIrAim((short)lx, (short)my, (short)rx, (short)my);
                Assert.True(det);
                Assert.Equal(ex, x, 0.005f);
                Assert.Equal(ey, y, 0.005f);
            }
        }

        [Fact]
        public void ALevelPair_KeepsThePlainMidpoint()
        {
            var w = new SdlDeviceWrapper();
            var (x, y, det, calibrated) = w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(0), trigger: false);
            var (mx, my, _) = SdlDeviceWrapper.ComputeIrAim(300, 350, 500, 350);
            Assert.True(det);
            Assert.False(calibrated);
            Assert.Equal(mx, x, precision: 5);
            Assert.Equal(my, y, precision: 5);
        }

        [Fact]
        public void ARolledPair_TurnsTheAimBackByThePairsAngle()
        {
            var w = new SdlDeviceWrapper();
            var (x, y, det, _) = w.StepIrPointer(400, 300, 600, 400, 0f, G, Ms(0), trigger: false);
            var (mx, my, _) = SdlDeviceWrapper.ComputeIrAim(400, 300, 600, 400);
            var (ex, ey) = SdlDeviceWrapper.RotateIrAim(mx, my, (float)Math.Atan2(100, 200));
            Assert.True(det);
            Assert.Equal(ex, x, precision: 5);
            Assert.Equal(ey, y, precision: 5);
            Assert.NotEqual(mx, x, precision: 3);
        }

        [Fact]
        public void TheLeftDot_HoldsWhileTracking_AndIsPickedAgainAfterALoss()
        {
            var w = new SdlDeviceWrapper();
            // Upright, dot 0 is the left LED.
            w.StepIrPointer(400, 384, 600, 384, 0f, G, Ms(0), false);

            // The dots trade sides while still tracked (the remote rolled
            // past upright): dot 0 stays the left LED, so the pair reads
            // turned by half a circle.
            var (x, y, _, _) = w.StepIrPointer(600, 384, 400, 384, 0f, G, Ms(10), false);
            var (mx, my, _) = SdlDeviceWrapper.ComputeIrAim(600, 384, 400, 384);
            var (ex, ey) = SdlDeviceWrapper.RotateIrAim(mx, my, (float)Math.PI);
            Assert.Equal(Math.Clamp(ex, -1f, 1f), x, precision: 5);
            Assert.Equal(Math.Clamp(ey, -1f, 1f), y, precision: 5);

            // A lost pair ends the track, and the next one picks afresh:
            // upright, the smaller X, now dot 1, so the pair reads level.
            w.StepIrPointer(-1, -1, -1, -1, 0f, G, Ms(20), false);
            var (lx, ly, det, _) = w.StepIrPointer(600, 384, 400, 384, 0f, G, Ms(30), false);
            Assert.True(det);
            Assert.Equal(mx, lx, precision: 5);
            Assert.Equal(my, ly, precision: 5);
        }

        [Fact]
        public void AnUpsideDownRemote_TakesTheOtherDotAsLeft()
        {
            var w = new SdlDeviceWrapper();
            var (x, y, _, _) = w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(0), false);
            var (mx, my, _) = SdlDeviceWrapper.ComputeIrAim(400, 384, 600, 384);
            var (ex, ey) = SdlDeviceWrapper.RotateIrAim(mx, my, (float)Math.PI);
            Assert.Equal(Math.Clamp(ex, -1f, 1f), x, precision: 5);
            Assert.Equal(Math.Clamp(ey, -1f, 1f), y, precision: 5);
        }

        /// <summary>Touchmote smooths the accelerometer once per 10 ms report,
        /// 0.9 to 0.1. Spread over 1 ms polls the weight is the same: ten polls
        /// move the reading a tenth of the way, where ten full steps would have
        /// turned it over. A long track does turn it over.</summary>
        [Fact]
        public void TheAccelerometer_IsSmoothedPerReport_NotPerPoll()
        {
            var w = new SdlDeviceWrapper();
            w.StepIrPointer(400, 384, 600, 384, 0f, G, Ms(0), false); // seeded upright
            // 10 ms of upside-down polls, one report: still upright.
            for (int t = 1; t <= 10; t++)
                w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(t), false);
            w.StepIrPointer(-1, -1, -1, -1, 0f, -G, Ms(11), false);
            var (sx, _, _, _) = w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(12), false);
            var (mx, _, _) = SdlDeviceWrapper.ComputeIrAim(400, 384, 600, 384);
            Assert.Equal(mx, sx, precision: 5);

            // Two seconds of it: the smoothed reading is upside down, and
            // the next track takes the other dot as left.
            for (int t = 13; t <= 2000; t++)
                w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(t), false);
            w.StepIrPointer(-1, -1, -1, -1, 0f, -G, Ms(2001), false);
            var (ux, _, _, _) = w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(2002), false);
            Assert.Equal(-mx, ux, precision: 4);
        }

        /// <summary>A stretch with no polls, however long, counts as one
        /// report: one smoothing step moves the reading at most a tenth of the
        /// way, as one report would, however long the poll loop
        /// stalled.</summary>
        [Fact]
        public void ALongGap_CountsAsOneReport()
        {
            var w = new SdlDeviceWrapper();
            w.StepIrPointer(400, 384, 600, 384, 0f, G, Ms(0), false); // seeded upright
            w.StepIrPointer(-1, -1, -1, -1, 0f, -G, Ms(1), false);
            // A second later, upside down: one report moves the reading a
            // tenth of the way, so the track still starts upright.
            var (x, _, _, _) = w.StepIrPointer(400, 384, 600, 384, 0f, -G, Ms(1000), false);
            var (mx, _, _) = SdlDeviceWrapper.ComputeIrAim(400, 384, 600, 384);
            Assert.Equal(mx, x, precision: 5);
        }

        /// <summary>The accelerometer is smoothed while the bar is out of view
        /// too, since both IR reports the fork asks for carry it
        /// (SDL_hidapi_wii.c HandleWiiRemoteAccelData). A remote turned over while it could not
        /// see the bar starts its next track upside down. Smoothing only the
        /// tracked polls, as Touchmote does, kept the orientation of before
        /// the turn and flipped the aim.</summary>
        [Fact]
        public void TurningOverOutOfSight_CountsForTheNextTrack()
        {
            var w = new SdlDeviceWrapper();
            w.StepIrPointer(200, 384, 400, 384, 0f, G, Ms(0), false); // tracked upright
            for (int t = 1; t <= 2000; t++)
                w.StepIrPointer(-1, -1, -1, -1, 0f, -G, Ms(t), false);
            var (x, _, det, _) = w.StepIrPointer(200, 384, 400, 384, 0f, -G, Ms(2001), false);
            var (mx, _, _) = SdlDeviceWrapper.ComputeIrAim(200, 384, 400, 384);
            Assert.True(det);
            Assert.Equal(-mx, x, precision: 4);
        }

        /// <summary>SDL reads 0 on both axes before the accelerometer's first
        /// report, which is no sample, and the first real sample seeds the
        /// reading. Seeded with the zeros, a remote that found the bar in its
        /// first reports took the upright pick even held upside down, and the
        /// pick holds for the whole track.</summary>
        [Fact]
        public void TheZerosBeforeTheFirstSample_SeedNothing()
        {
            var w = new SdlDeviceWrapper();
            // Tracked before any accelerometer report: upright.
            var (ux, _, _, _) = w.StepIrPointer(200, 384, 400, 384, 0f, 0f, Ms(0), false);
            var (mx, _, _) = SdlDeviceWrapper.ComputeIrAim(200, 384, 400, 384);
            Assert.Equal(mx, ux, precision: 4);
            w.StepIrPointer(-1, -1, -1, -1, 0f, 0f, Ms(1), false);
            // The first report says upside down, and the next track is.
            var (x, _, _, _) = w.StepIrPointer(200, 384, 400, 384, 0f, -G, Ms(2), false);
            Assert.Equal(-mx, x, precision: 4);
        }

        // ── The calibration shot and window ──

        [Fact]
        public void TheShot_IsTheAimAtTheBPress_InPointerCounts()
        {
            var w = new SdlDeviceWrapper();
            Assert.False(w.TryGetWiiPointerShot(out _, out _, out _, out _));

            w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(0), trigger: false);
            Assert.False(w.TryGetWiiPointerShot(out _, out _, out _, out _));

            var (x, y, _, _) = w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(1), trigger: true);
            Assert.True(w.TryGetWiiPointerShot(out short cx, out short cy, out bool on, out int shot));
            Assert.Equal((short)Math.Round(x * SdlDeviceWrapper.WiiPointerCountsX), cx);
            Assert.Equal((short)Math.Round(y * SdlDeviceWrapper.WiiPointerCountsY), cy);
            Assert.True(on);
            Assert.Equal(1, shot);

            // Held is one shot. A press while the bar is out of view is a
            // shot off the screen.
            w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(2), trigger: true);
            Assert.True(w.TryGetWiiPointerShot(out _, out _, out _, out shot));
            Assert.Equal(1, shot);
            w.StepIrPointer(-1, -1, -1, -1, 0f, G, Ms(3), trigger: false);
            w.StepIrPointer(-1, -1, -1, -1, 0f, G, Ms(4), trigger: true);
            Assert.True(w.TryGetWiiPointerShot(out _, out _, out on, out shot));
            Assert.False(on);
            Assert.Equal(2, shot);
        }

        /// <summary>Shots at the screen's four targets from where the player
        /// stands fit a window, and the remote then aims at each target's own
        /// spot on the screen through every IR Pointer source.</summary>
        [Fact]
        public void ACalibratedRemote_AimsAtTheSpotsItWasCalibratedOn()
        {
            var targets = GunCalibrationScreen.Targets;
            var shots = new (int X, int Y)[targets.Length];
            for (int i = 0; i < targets.Length; i++)
                shots[i] = ((int)Math.Round(-180 + targets[i].U * 420), (int)Math.Round(-150 + targets[i].V * 260));
            Assert.True(GunCon2Calibration.TryFit(targets, shots, out var window));

            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                SourceCoercion.IrTuningProvider = null;
                var srcX = new MappingSource { Descriptor = "IR Pointer X", DeviceGuid = "cal-a" };
                var srcY = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "cal-a" };
                foreach (var (u, v) in new[] { (0.1, 0.1), (0.5, 0.5), (0.9, 0.9), (0.25, 0.75) })
                {
                    float aimX = (float)(-180 + u * 420) / SdlDeviceWrapper.WiiPointerCountsX;
                    float aimY = (float)(-150 + v * 260) / SdlDeviceWrapper.WiiPointerCountsY;
                    var (sx, sy) = SdlDeviceWrapper.ApplyWiiPointerCalibration(aimX, aimY, window);
                    var state = new CustomInputState();
                    state.Ir.X = sx;
                    state.Ir.Y = sy;
                    state.Ir.Detected = true;
                    SourceCoercion.BeginPollFrame();
                    Assert.Equal((float)(u * 2 - 1), Read(state, srcX, 0), precision: 2);
                    Assert.Equal((float)(v * 2 - 1), Read(state, srcY, 0), precision: 2);
                }
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        [Fact]
        public void TheWindow_ClampsAimPastTheScreen()
        {
            var window = new GunCon2Calibration(-200, 200, -150, 150);
            var (x, y) = SdlDeviceWrapper.ApplyWiiPointerCalibration(0.9f, -0.9f, window);
            Assert.Equal(1f / SourceCoercion.IrMarginStretchX, x, precision: 5);
            Assert.Equal(-1f / SourceCoercion.IrMarginStretchY, y, precision: 5);
        }

        [Fact]
        public void APollMapsTheCompensatedAimThroughTheWindow()
        {
            var w = new SdlDeviceWrapper();
            var window = new GunCon2Calibration(-200, 200, -150, 150);
            w.WiiPointerCalibration = window;
            var (x, y, det, calibrated) = w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(0), false);
            var (mx, my, _) = SdlDeviceWrapper.ComputeIrAim(300, 350, 500, 350);
            var (ex, ey) = SdlDeviceWrapper.ApplyWiiPointerCalibration(mx, my, window);
            Assert.True(det);
            Assert.True(calibrated);
            Assert.Equal(ex, x, precision: 5);
            Assert.Equal(ey, y, precision: 5);

            // The shot is taken before the window maps it: the fit runs on it.
            w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(1), true);
            Assert.True(w.TryGetWiiPointerShot(out short cx, out _, out _, out _));
            Assert.Equal((short)Math.Round(mx * SdlDeviceWrapper.WiiPointerCountsX), cx);
        }

        // ── The Pointer tab's bar offset ──

        [Fact]
        public void ACalibratedAim_SkipsTheBarOffset()
        {
            var prev = SourceCoercion.IrTuningProvider;
            try
            {
                // Sensor bar above the screen, 0.3, no smoothing.
                SourceCoercion.IrTuningProvider = (dev, slot) => (-0.3f, 0f);
                var src = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "bar-a" };
                var state = new CustomInputState();
                state.Ir.Y = 0.1f;
                state.Ir.Detected = true;

                // Aim 0.1 stretches to 0.2 (the lineage's x2.0), and the
                // offset moves it up.
                SourceCoercion.BeginPollFrame();
                Assert.Equal(-0.1f, Read(state, src, 0), precision: 5);

                // A calibrated aim already has the bar's place in it.
                state.Ir.Calibrated = true;
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0.2f, Read(state, src, 0), precision: 5);

                // A GunCon 2's beam window places its aim on the picture with
                // no bar, so it is marked calibrated, and the offset a Copy From
                // of a remote's settings carries onto the gun leaves it alone.
                var gun = new CustomInputState();
                SdlDeviceWrapper.ApplyGunCon2(gun, 300, 130, GunCon2Calibration.Default);
                Assert.True(gun.Ir.Calibrated);
                var gunSrc = new MappingSource { Descriptor = "IR Pointer Y", DeviceGuid = "bar-gun" };
                SourceCoercion.BeginPollFrame();
                Assert.Equal(0f, Read(gun, gunSrc, 0), precision: 5);

                // Off the screen it aims nowhere, and nothing is marked.
                SdlDeviceWrapper.ApplyGunCon2(gun, 5, 130, GunCon2Calibration.Default);
                Assert.False(gun.Ir.Detected);
                Assert.False(gun.Ir.Calibrated);
            }
            finally
            {
                SourceCoercion.IrTuningProvider = prev;
            }
        }

        // ── Buttons, the Devices page row and the calibration screen ──

        [Theory]
        [InlineData("Nintendo Wii Remote", 0, 5)]
        [InlineData("Nintendo Wii Remote with Nunchuk", 0, 5)]
        [InlineData("Nintendo Wii Remote with Classic Controller", 16, 21)]
        [InlineData(null, 0, 5)]
        public void TheRemotesBAndHome_FollowTheConfiguration(string name, int b, int home)
            => Assert.Equal((b, home), WiiRemoteIdentity.RemoteButtons(name));

        [Fact]
        public void TheDevicesRow_SpeaksOfTheRemote()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            var row = new DeviceRowViewModel { GunIsWiiRemote = true };
            Assert.Equal(si.Devices_WiiAimDefault, row.GunCalibrationStatus);
            Assert.Equal(si.Devices_WiiCalibrateTooltip, row.GunCalibrateTooltip);
            row.GunCalibration = "-200,200,-150,150";
            Assert.Equal(si.Devices_WiiAimCalibrated, row.GunCalibrationStatus);

            var gun = new DeviceRowViewModel();
            Assert.Equal(si.Devices_GunCalibrateTooltip, gun.GunCalibrateTooltip);
            Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                si.Devices_GunWindowDefault, 175, 720, 20, 240), gun.GunCalibrationStatus);
        }

        [Fact]
        public void TheCalibrationScreen_ReadsTheRemotesShotAndHome()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            var w = new SdlDeviceWrapper();
            var gun = CalibrationGun.ForWiiRemote(w);
            Assert.False(gun.LatestShot().Any);
            Assert.False(gun.CancelHeld());

            var (x, _, _, _) = w.StepIrPointer(300, 350, 500, 350, 0f, G, Ms(0), true);
            var (any, cx, _, onScreen, shot) = gun.LatestShot();
            Assert.True(any);
            Assert.True(onScreen);
            Assert.Equal(1, shot);
            Assert.Equal((short)Math.Round(x * SdlDeviceWrapper.WiiPointerCountsX), cx);

            typeof(SdlDeviceWrapper).GetField("_wiiHomeHeld",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(w, true);
            Assert.True(gun.CancelHeld());
            Assert.Equal(si.GunCalibration_WiiInstruction, gun.Instruction);
            Assert.Equal(si.GunCalibration_WiiOffScreen, gun.OffScreen);
            Assert.Equal(si.GunCalibration_WiiCancel, gun.Cancel);
        }

        [Fact]
        public void TheCalibrationScreen_StillReadsTheGunCon2ItsOwnWay()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            var w = new SdlDeviceWrapper();
            var device = new UserDevice { VendorId = 0x0B9A, ProdId = 0x016A };
            var gun = CalibrationGun.ForGunCon2(device, w);
            Assert.False(gun.LatestShot().Any);

            w.RecordGunCon2Trigger(300, 120, triggerDown: true);
            var (any, x, y, onScreen, pull) = gun.LatestShot();
            Assert.True(any);
            Assert.Equal((short)300, x);
            Assert.Equal((short)120, y);
            Assert.True(onScreen);
            Assert.Equal(1, pull);

            // Off screen by GunconUSB's rule.
            w.RecordGunCon2Trigger(300, 120, triggerDown: false);
            w.RecordGunCon2Trigger(5, 120, triggerDown: true);
            Assert.False(gun.LatestShot().OnScreen);

            // A and B are buttons 1 and 2.
            Assert.False(gun.CancelHeld());
            device.InputState = new CustomInputState();
            device.InputState.Buttons[2] = true;
            Assert.True(gun.CancelHeld());
            Assert.Equal(si.GunCalibration_Instruction, gun.Instruction);
            Assert.Equal(si.GunCalibration_OffScreen, gun.OffScreen);
            Assert.Equal(si.GunCalibration_Cancel, gun.Cancel);
        }

        // ── Wiring that runs only with a live device ──

        private static string RepoText(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, relative)).Replace("\r\n", "\n");
        }

        [Fact]
        public void TheRemoteTakesItsCalibration_OnConnectAndOnSave()
        {
            string ud = RepoText("PadForge.Engine/Data/UserDevice.cs");
            Assert.Contains("else if (wrapper.HasIrCamera)\n                wrapper.WiiPointerCalibration = GunCon2Calibration.TryParse(GunCalibration, out var pointer) ? pointer : null;", ud);
            string ds = RepoText("PadForge.App/Services/DeviceService.cs");
            Assert.Contains("else if (gun.HasIrCamera)\n                        gun.WiiPointerCalibration = PadForge.Engine.GunCon2Calibration.TryParse(ud.GunCalibration, out var pointer)", ds);
        }

        [Fact]
        public void TheRowShowsTheSection_AndThePollMarksACalibratedAim()
        {
            string svc = RepoText("PadForge.App/Services/InputService.cs");
            Assert.Contains("row.GunIsWiiRemote = !ud.IsGunCon2 && ud.HasIrCamera;", svc);
            // A gun shared over Remote Link is calibrated where it is plugged
            // in, so its row here has no section (#248).
            Assert.Contains("row.ShowGunCalibration = DeviceRowViewModel.ComputeShowGunCalibration(\n                ud.IsGunCon2 || ud.HasIrCamera, ud.DevicePath);", svc);
            Assert.Contains("&& (w.IsGunCon2 || w.HasIrCamera);", svc);
            string wrapper = RepoText("PadForge.Engine/Common/SdlDeviceWrapper.cs");
            Assert.Contains("var (x, y, detected, calibrated) = StepIrPointer(", wrapper);
            Assert.Contains("state.Ir.Calibrated = calibrated;", wrapper);
            // The fork's SDL X is the remote's X negated and its SDL Y the
            // remote's Z (SDL_hidapi_wii.c HandleWiiRemoteAccelData).
            Assert.Contains("accel ? -state.Accel[0] : 0f,\n                accel ? state.Accel[1] : 0f,", wrapper);
            Assert.Contains("var (bButton, homeButton) = WiiRemoteIdentity.RemoteButtons(Name);", wrapper);
        }

        [Fact]
        public void DeviceRemoval_ForgetsTheHeldAim()
        {
            string step1 = RepoText("PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs");
            int teardown = step1.IndexOf("private void NeutralizeMappedOutputsFor(UserDevice ud)", StringComparison.Ordinal);
            int forget = step1.IndexOf("SourceCoercion.ForgetIrPointerForDevice(", teardown, StringComparison.Ordinal);
            Assert.True(teardown >= 0 && forget > teardown && forget - teardown < 1500);
        }

        [Fact]
        public void TheCalibrateButton_TakesAWiiRemote()
        {
            string page = RepoText("PadForge.App/Views/DevicesPage.xaml.cs");
            Assert.Contains("else if (wrapper.HasIrCamera) gun = CalibrationGun.ForWiiRemote(wrapper);", page);
            Assert.Contains("dev.GunCalibration = wrapper.IsGunCon2 && calibration.IsDefault ? string.Empty : calibration.ToString();", page);
            string xaml = RepoText("PadForge.App/Views/DevicesPage.xaml");
            Assert.Contains("ToolTip=\"{Binding SelectedDevice.GunCalibrateTooltip}\"", xaml);
        }
    }
}
