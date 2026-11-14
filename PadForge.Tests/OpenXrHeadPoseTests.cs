using System;
using PadForge.Engine.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The OpenXR head pose reads the same way the SteamVR one does (#403).
    ///
    /// <para>Two head-tracking backends that disagreed about which way is
    /// positive would be a defect neither one's own tests would catch: each
    /// would be self-consistent, and the user would find it by leaning left
    /// and watching the bike go right on one setup and not the other. So the
    /// signs are pinned here by named physical movements rather than by the
    /// arithmetic that produces them.</para>
    /// </summary>
    public class OpenXrHeadPoseTests
    {
        private const double Tol = 1e-6;

        /// <summary>A rotation of <paramref name="deg"/> about an axis, as
        /// the quaternion OpenXR would report.</summary>
        private static (double X, double Y, double Z, double W) AxisAngle(
            double ax, double ay, double az, double deg)
        {
            double half = deg * Math.PI / 360.0;
            double s = Math.Sin(half);
            return (ax * s, ay * s, az * s, Math.Cos(half));
        }

        [Fact]
        public void TheIdentityOrientationIsLevelAndForward()
        {
            var (yaw, pitch, roll) = OpenXrHeadPose.EulerFromQuaternion(0, 0, 0, 1);
            Assert.Equal(0, yaw, 6);
            Assert.Equal(0, pitch, 6);
            Assert.Equal(0, roll, 6);
        }

        /// <summary>Turning right is positive yaw. In a Y-up right-handed
        /// space a right turn is a NEGATIVE rotation about +Y, which is
        /// exactly the sign that is easy to get backwards.</summary>
        [Theory]
        [InlineData(-30, 30)]
        [InlineData(-90, 90)]
        [InlineData(30, -30)]
        public void TurningRightReadsAsPositiveYaw(double rotationAboutY, double expectedYaw)
        {
            var q = AxisAngle(0, 1, 0, rotationAboutY);
            var (yaw, pitch, roll) = OpenXrHeadPose.EulerFromQuaternion(q.X, q.Y, q.Z, q.W);
            Assert.Equal(expectedYaw, yaw, 4);
            Assert.Equal(0, pitch, 4);
            Assert.Equal(0, roll, 4);
        }

        /// <summary>Looking up is positive pitch. A look up is a positive
        /// rotation about +X.</summary>
        [Theory]
        [InlineData(25, 25)]
        [InlineData(-25, -25)]
        public void LookingUpReadsAsPositivePitch(double rotationAboutX, double expectedPitch)
        {
            var q = AxisAngle(1, 0, 0, rotationAboutX);
            var (yaw, pitch, roll) = OpenXrHeadPose.EulerFromQuaternion(q.X, q.Y, q.Z, q.W);
            Assert.Equal(expectedPitch, pitch, 4);
            Assert.Equal(0, yaw, 4);
            Assert.Equal(0, roll, 4);
        }

        /// <summary>Tilting the head right is positive roll.
        ///
        /// <para>Forward points at -Z, so +Z points back at the user. By the
        /// right-hand rule a POSITIVE rotation about +Z carries the right ear
        /// upward, which is a tilt to the LEFT. The sign therefore inverts
        /// here, and that inversion is the whole reason this test names the
        /// movement rather than the arithmetic.</para></summary>
        [Theory]
        [InlineData(20, -20)]
        [InlineData(-20, 20)]
        public void TiltingRightReadsAsPositiveRoll(double rotationAboutZ, double expectedRoll)
        {
            var q = AxisAngle(0, 0, 1, rotationAboutZ);
            var (yaw, pitch, roll) = OpenXrHeadPose.EulerFromQuaternion(q.X, q.Y, q.Z, q.W);
            Assert.Equal(expectedRoll, roll, 4);
            Assert.Equal(0, yaw, 4);
            Assert.Equal(0, pitch, 4);
        }

        /// <summary>Both quaternion signs describe the same rotation, and a
        /// runtime may hand over either.</summary>
        [Fact]
        public void BothQuaternionSignsReadTheSame()
        {
            var q = AxisAngle(0.3, 0.8, 0.5, 47);
            double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            var a = OpenXrHeadPose.EulerFromQuaternion(q.X / n, q.Y / n, q.Z / n, q.W / n);
            var b = OpenXrHeadPose.EulerFromQuaternion(-q.X / n, -q.Y / n, -q.Z / n, -q.W / n);
            Assert.Equal(a.YawDeg, b.YawDeg, 6);
            Assert.Equal(a.PitchDeg, b.PitchDeg, 6);
            Assert.Equal(a.RollDeg, b.RollDeg, 6);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(180, 180)]
        [InlineData(181, -179)]
        [InlineData(-180, 180)]
        [InlineData(359, -1)]
        [InlineData(-359, 1)]
        [InlineData(720, 0)]
        public void YawWrapsIntoHalfOpenRange(double input, double expected)
        {
            Assert.Equal(expected, OpenXrHeadPose.WrapDegrees(input), 6);
        }

        /// <summary>The first sample defines neutral, so a user who is not
        /// sitting at their room's origin still starts centered.</summary>
        [Fact]
        public void TheFirstSampleBecomesNeutral()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];

            var q = AxisAngle(0, 1, 0, -40);   // facing 40 degrees right of the room
            Assert.True(OpenXrHeadPose.TryFillPose(1.5, 1.2, -0.7, q.X, q.Y, q.Z, q.W,
                                                   ref baseline, pose));

            Assert.Equal(0, pose[HeadPose.TX], 6);
            Assert.Equal(0, pose[HeadPose.TY], 6);
            Assert.Equal(0, pose[HeadPose.TZ], 6);
            Assert.Equal(0, pose[HeadPose.Yaw], 4);
            Assert.True(baseline.Captured);
        }

        /// <summary>Movement away from neutral, in centimeters, with forward
        /// positive the way OpenTrack reports it.</summary>
        [Fact]
        public void MovementIsRelativeToNeutralInCentimeters()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            OpenXrHeadPose.TryFillPose(1.0, 1.0, 1.0, 0, 0, 0, 1, ref baseline, pose);

            // 12 cm right, 7 cm up, 20 cm forward. Forward is -Z in OpenXR.
            OpenXrHeadPose.TryFillPose(1.12, 1.07, 0.80, 0, 0, 0, 1, ref baseline, pose);

            Assert.Equal(12, pose[HeadPose.TX], 4);
            Assert.Equal(7, pose[HeadPose.TY], 4);
            Assert.Equal(20, pose[HeadPose.TZ], 4);
        }

        /// <summary>Head elevation is what the reporter on #403 asked for
        /// first, and it has to survive the whole chain into the axis the
        /// mapping reads: standing up must push a stick UP, which is the LOW
        /// end of the unsigned axis.</summary>
        [Fact]
        public void RisingPushesTheStickUpAndCrouchingPushesItDown()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            var axes = new int[HeadPose.AxisCount];
            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);

            OpenXrHeadPose.TryFillPose(0, 1.75, 0, 0, 0, 0, 1, ref baseline, pose);
            HeadPose.FillAxes(pose, 90, 30, axes);
            Assert.True(axes[HeadPose.AxisY] < HeadPose.AxisCenter,
                "sitting up should read as a stick pushed up");

            OpenXrHeadPose.TryFillPose(0, 1.45, 0, 0, 0, 0, 1, ref baseline, pose);
            HeadPose.FillAxes(pose, 90, 30, axes);
            Assert.True(axes[HeadPose.AxisY] > HeadPose.AxisCenter,
                "tucking should read as a stick pushed down");
        }

        /// <summary>Leaning right drives the lateral axis high, which is what
        /// a stick pushed right reads as.</summary>
        [Fact]
        public void LeaningRightDrivesTheLateralAxisHigh()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            var axes = new int[HeadPose.AxisCount];
            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);

            OpenXrHeadPose.TryFillPose(0.18, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            HeadPose.FillAxes(pose, 90, 30, axes);
            Assert.True(axes[HeadPose.AxisX] > HeadPose.AxisCenter);

            OpenXrHeadPose.TryFillPose(-0.18, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            HeadPose.FillAxes(pose, 90, 30, axes);
            Assert.True(axes[HeadPose.AxisX] < HeadPose.AxisCenter);
        }

        /// <summary>Clearing the baseline recaptures neutral where the user
        /// is now. Tracking loss moves people, and coming back to a stick
        /// pinned at full deflection is the failure this prevents.</summary>
        [Fact]
        public void ClearingTheBaselineRecapturesNeutral()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            OpenXrHeadPose.TryFillPose(0.5, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            Assert.Equal(50, pose[HeadPose.TX], 4);

            baseline.Clear();
            Assert.False(baseline.Captured);
            OpenXrHeadPose.TryFillPose(0.5, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            Assert.Equal(0, pose[HeadPose.TX], 6);
        }

        /// <summary>A pose carrying NaN or infinity is rejected whole and
        /// leaves the previous one standing, because a NaN reaching the axis
        /// scaler reads as centered, which is indistinguishable from a user
        /// sitting still.</summary>
        [Theory]
        [InlineData(double.NaN, 0, 0, 0, 0, 0, 1)]
        [InlineData(0, double.PositiveInfinity, 0, 0, 0, 0, 1)]
        [InlineData(0, 0, double.NegativeInfinity, 0, 0, 0, 1)]
        [InlineData(0, 0, 0, double.NaN, 0, 0, 1)]
        [InlineData(0, 0, 0, 0, 0, 0, double.NaN)]
        public void ANonFinitePoseIsRejectedWhole(
            double px, double py, double pz, double qx, double qy, double qz, double qw)
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            OpenXrHeadPose.TryFillPose(0, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            OpenXrHeadPose.TryFillPose(0.11, 1.6, 0, 0, 0, 0, 1, ref baseline, pose);
            double keptX = pose[HeadPose.TX];

            Assert.False(OpenXrHeadPose.TryFillPose(px, py, pz, qx, qy, qz, qw, ref baseline, pose));
            Assert.Equal(keptX, pose[HeadPose.TX], 6);
        }

        /// <summary>A bad sample must not become the neutral either.</summary>
        [Fact]
        public void ANonFinitePoseCannotBecomeTheBaseline()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            Assert.False(OpenXrHeadPose.TryFillPose(double.NaN, 1.6, 0, 0, 0, 0, 1,
                                                    ref baseline, pose));
            Assert.False(baseline.Captured);
        }

        /// <summary>Pitch and roll have a natural zero, so they are absolute
        /// while position and yaw are not. A user who captures neutral while
        /// looking down should still read as looking down.</summary>
        [Fact]
        public void PitchAndRollAreNotBaselined()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            var q = AxisAngle(1, 0, 0, -20);          // looking down 20 degrees
            Assert.True(OpenXrHeadPose.TryFillPose(0, 1.6, 0, q.X, q.Y, q.Z, q.W,
                                                   ref baseline, pose));
            Assert.Equal(-20, pose[HeadPose.Pitch], 4);
        }

        /// <summary>Turning a pose into axes allocates nothing.
        ///
        /// <para>This runs about 125 times a second on a background thread
        /// for as long as the feature is on. Anything allocating in here is
        /// GC pressure a user never asked for, paid continuously while they
        /// play.</para></summary>
        [Fact]
        public void FillingAPoseAllocatesNothing()
        {
            var baseline = default(OpenXrHeadPose.Baseline);
            var pose = new double[HeadPose.PoseCount];
            var axes = new int[HeadPose.AxisCount];

            // Warm up, so first-call initialization is not measured.
            for (int i = 0; i < 256; i++)
            {
                OpenXrHeadPose.TryFillPose(0.1, 1.6, 0.2, 0, 0.2, 0, 0.98,
                                           ref baseline, pose);
                HeadPose.FillAxes(pose, 90, 30, axes);
            }

            // Counted over windows the runtime left alone: the thread's
            // count moves by itself in a full run (AllocationWindow).
            foreach (long bytes in AllocationWindow.Clean(() =>
            {
                OpenXrHeadPose.TryFillPose(0.1, 1.6, 0.2, 0, 0.2, 0, 0.98,
                                           ref baseline, pose);
                HeadPose.FillAxes(pose, 90, 30, axes);
            }, 2000))
                Assert.Equal(0, bytes);
        }

        /// <summary>The SteamVR path and this one are the same convention.
        /// Both are fed the same rotation and must agree.</summary>
        [Theory]
        [InlineData(0, 1, 0, -35)]
        [InlineData(1, 0, 0, 22)]
        [InlineData(0, 0, 1, -15)]
        public void TheAnglesAgreeWithTheSteamVrPath(double ax, double ay, double az, double deg)
        {
            var q = AxisAngle(ax, ay, az, deg);
            var mine = OpenXrHeadPose.EulerFromQuaternion(q.X, q.Y, q.Z, q.W);

            // The same rotation as the 3x4 matrix the SteamVR path reads.
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            var m = new Valve.VR.HmdMatrix34_t
            {
                m0 = (float)(1 - 2 * (y * y + z * z)),
                m1 = (float)(2 * (x * y - w * z)),
                m2 = (float)(2 * (x * z + w * y)),
                m4 = (float)(2 * (x * y + w * z)),
                m5 = (float)(1 - 2 * (x * x + z * z)),
                m6 = (float)(2 * (y * z - w * x)),
                m8 = (float)(2 * (x * z - w * y)),
                m9 = (float)(2 * (y * z + w * x)),
                m10 = (float)(1 - 2 * (x * x + y * y)),
            };
            var theirs = PadForge.Common.Input.OpenVrConsumerService.EulerFromPoseMatrix(m);

            Assert.Equal(theirs.YawDeg, mine.YawDeg, 3);
            Assert.Equal(theirs.PitchDeg, mine.PitchDeg, 3);
            Assert.Equal(theirs.RollDeg, mine.RollDeg, 3);
        }
    }
}
