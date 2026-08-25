using System;
using System.Buffers.Binary;
using Xunit;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #355 wire contracts, grounded in the cloned opentrack sources:
    /// the UDP datagram is sizeof(double[6]) in enum Axis order
    /// (proto-udp, api/plugin-api.hpp), NaN and infinity reject the datagram
    /// (tracker-udp), and the FreeTrack heap is read back through the
    /// inverse of proto-ft's writer (negated radians for yaw and pitch,
    /// radians for roll, millimeters for translation).
    /// </summary>
    public class HeadPoseDecodeTests
    {
        internal static byte[] Udp(params double[] v)
        {
            var b = new byte[HeadPose.OpenTrackUdpBytes];
            for (int i = 0; i < v.Length; i++)
                BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(i * 8, 8), v[i]);
            return b;
        }

        internal static byte[] Heap(uint dataId, float yaw, float pitch, float roll, float x, float y, float z)
        {
            var b = new byte[HeadPose.FreeTrackHeapBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), dataId);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12, 4), yaw);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16, 4), pitch);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20, 4), roll);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24, 4), x);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28, 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32, 4), z);
            return b;
        }

        [Fact]
        public void OpenTrackUdp_SixLittleEndianDoubles_InAxisOrder()
        {
            var pose = new double[6];
            Assert.True(HeadPose.TryDecodeOpenTrackUdp(Udp(1, 2, 3, 10, 20, 30), pose));
            Assert.Equal(1, pose[HeadPose.TX]);
            Assert.Equal(2, pose[HeadPose.TY]);
            Assert.Equal(3, pose[HeadPose.TZ]);
            Assert.Equal(10, pose[HeadPose.Yaw]);
            Assert.Equal(20, pose[HeadPose.Pitch]);
            Assert.Equal(30, pose[HeadPose.Roll]);
        }

        [Fact]
        public void OpenTrackUdp_NaNOrInfinity_RejectsTheWholeDatagram()
        {
            var pose = new double[] { 7, 7, 7, 7, 7, 7 };
            Assert.False(HeadPose.TryDecodeOpenTrackUdp(Udp(1, 2, 3, 10, double.NaN, 30), pose));
            Assert.False(HeadPose.TryDecodeOpenTrackUdp(Udp(double.PositiveInfinity, 2, 3, 10, 20, 30), pose));
            Assert.All(pose, v => Assert.Equal(7, v));
        }

        [Fact]
        public void OpenTrackUdp_ShortDatagramRejected_LongOneReadsItsFirst48Bytes()
        {
            var pose = new double[6];
            Assert.False(HeadPose.TryDecodeOpenTrackUdp(new byte[47], pose));
            var longer = new byte[64];
            Udp(0, 0, 0, 45, 0, 0).CopyTo(longer, 0);
            Assert.True(HeadPose.TryDecodeOpenTrackUdp(longer, pose));
            Assert.Equal(45, pose[HeadPose.Yaw]);
        }

        [Fact]
        public void FreeTrackHeap_OffsetsAndSigns_InvertOpenTracksWriter()
        {
            const float d2r = (float)(Math.PI / 180.0);
            // proto-ft: Yaw = -yaw*d2r, Pitch = -pitch*d2r, Roll = roll*d2r, X = tx*10.
            var heap = Heap(5, -10 * d2r, -20 * d2r, 30 * d2r, 15, -20, 300);
            var pose = new double[6];
            Assert.True(HeadPose.TryDecodeFreeTrackHeap(heap, out uint id, pose));
            Assert.Equal(5u, id);
            Assert.Equal(10, pose[HeadPose.Yaw], 3);
            Assert.Equal(20, pose[HeadPose.Pitch], 3);
            Assert.Equal(30, pose[HeadPose.Roll], 3);
            Assert.Equal(1.5, pose[HeadPose.TX], 3);
            Assert.Equal(-2.0, pose[HeadPose.TY], 3);
            Assert.Equal(30.0, pose[HeadPose.TZ], 3);
        }

        [Fact]
        public void FreeTrackHeap_TooShort_Rejected()
        {
            var pose = new double[6];
            Assert.False(HeadPose.TryDecodeFreeTrackHeap(new byte[35], out _, pose));
        }

        [Fact]
        public void ToAxis_Rest_Ends_Clamp_AndBadRange()
        {
            Assert.Equal(32768, HeadPose.ToAxis(0, 90));
            Assert.Equal(65535, HeadPose.ToAxis(90, 90));
            Assert.Equal(0, HeadPose.ToAxis(-90, 90));
            Assert.Equal(65535, HeadPose.ToAxis(180, 90));
            Assert.Equal(0, HeadPose.ToAxis(-1000, 90));
            Assert.Equal(49152, HeadPose.ToAxis(45, 90));
            Assert.Equal(32768, HeadPose.ToAxis(5, 0));
            Assert.Equal(32768, HeadPose.ToAxis(double.NaN, 90));
        }

        [Fact]
        public void FillAxes_YawRightReadsHigh_VerticalAxesInStickOrientation()
        {
            var pose = new double[6];
            pose[HeadPose.Yaw] = 45;
            pose[HeadPose.Pitch] = 45;  // head up
            pose[HeadPose.TX] = 15;
            pose[HeadPose.TY] = 15;     // head up
            var axes = new int[HeadPose.AxisCount];
            HeadPose.FillAxes(pose, 90, 30, axes);
            Assert.Equal(49152, axes[HeadPose.AxisYaw]);
            Assert.Equal(16384, axes[HeadPose.AxisPitch]); // up = low end, like a stick pushed up
            Assert.Equal(32768, axes[HeadPose.AxisRoll]);
            Assert.Equal(49152, axes[HeadPose.AxisX]);
            Assert.Equal(16384, axes[HeadPose.AxisY]);
            Assert.Equal(32768, axes[HeadPose.AxisZ]);
        }
    }

    /// <summary>The Head Tracker row: poses land on the six axes, silence
    /// recenters after one second, the FreeTrack first read is only a
    /// baseline, and the row rests at center before any pose.</summary>
    public class HeadTrackerDeviceTests
    {
        private sealed class Clock { public long Now = 10_000; }

        private static (HeadTrackerDevice dev, Clock clock) Make()
        {
            var clock = new Clock();
            var dev = new HeadTrackerDevice(4242, false, 0, () => clock.Now);
            dev.AttachForTest();
            return (dev, clock);
        }

        [Fact]
        public void UdpPose_LandsOnTheAxes_AndNamesItsPeer()
        {
            var (dev, _) = Make();
            dev.InjectUdp(HeadPoseDecodeTests.Udp(0, 0, 0, 45, 0, 0), "127.0.0.1:5000");
            var s = dev.GetCurrentState();
            Assert.NotNull(s);
            Assert.Equal(49152, s.Axis[0]);
            for (int i = 1; i < 6; i++) Assert.Equal(32768, s.Axis[i]);
            Assert.Equal(HeadTrackerSource.Udp, dev.Source);
            Assert.Equal("127.0.0.1:5000", dev.UdpPeer);
        }

        [Fact]
        public void Silence_RecentersAfterOneSecond()
        {
            var (dev, clock) = Make();
            dev.InjectUdp(HeadPoseDecodeTests.Udp(0, 0, 0, 90, 0, 0), "127.0.0.1:5000");
            Assert.Equal(65535, dev.GetCurrentState().Axis[0]);
            clock.Now += HeadTrackerDevice.SilenceMs;
            Assert.Equal(65535, dev.GetCurrentState().Axis[0]);
            clock.Now += 1;
            Assert.Equal(32768, dev.GetCurrentState().Axis[0]);
            Assert.Equal(HeadTrackerSource.None, dev.Source);
        }

        [Fact]
        public void FreeTrack_FirstReadIsABaseline_OnlyAChangedDataIdIsAPose()
        {
            var (dev, _) = Make();
            const float d2r = (float)(Math.PI / 180.0);
            var heap7 = HeadPoseDecodeTests.Heap(7, -90 * d2r, 0, 0, 0, 0, 0);
            dev.InjectFreeTrackHeap(heap7);
            Assert.Equal(32768, dev.GetCurrentState().Axis[0]); // a stale heap from a previous run
            dev.InjectFreeTrackHeap(heap7);
            Assert.Equal(32768, dev.GetCurrentState().Axis[0]); // still no writer
            dev.InjectFreeTrackHeap(HeadPoseDecodeTests.Heap(8, -90 * d2r, 0, 0, 0, 0, 0));
            Assert.Equal(65535, dev.GetCurrentState().Axis[0]);
            Assert.Equal(HeadTrackerSource.FreeTrack, dev.Source);
        }

        [Fact]
        public void BeforeAnyPose_RowIsOnlineAtRest()
        {
            var (dev, _) = Make();
            Assert.True(dev.IsAttached);
            Assert.Equal(6, dev.NumAxes);
            var s = dev.GetCurrentState();
            Assert.NotNull(s);
            for (int i = 0; i < 6; i++) Assert.Equal(32768, s.Axis[i]);
            Assert.Equal(HeadTrackerSource.None, dev.Source);
        }

        [Fact]
        public void Disposed_ReturnsNothing()
        {
            var (dev, _) = Make();
            dev.Dispose();
            Assert.False(dev.IsAttached);
            Assert.Null(dev.GetCurrentState());
        }

        [Fact]
        public void DeviceObjects_SixNamedAbsoluteAxes_AtIndices0To5()
        {
            var (dev, _) = Make();
            var objs = dev.GetDeviceObjects();
            Assert.Equal(6, objs.Length);
            string[] names = { "Head Yaw", "Head Pitch", "Head Roll", "Head X", "Head Y", "Head Z" };
            for (int i = 0; i < 6; i++)
            {
                Assert.True(objs[i].IsAxis);
                Assert.False(objs[i].IsSlider);
                Assert.Equal(i, objs[i].InputIndex);
                Assert.Equal(names[i], objs[i].Name);
            }
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, dev.SupportedAxisIndices);
        }

        [Fact]
        public void Identity_IsTheHeadTrackerType()
        {
            var (dev, _) = Make();
            Assert.Equal(InputDeviceType.HeadTracker, dev.GetInputDeviceType());
            Assert.Equal("headtrack://opentrack", dev.DevicePath);
            Assert.False(dev.HasGyro);
            Assert.Equal(0, dev.NumButtons);
        }
    }

    /// <summary>CapType is serialized as an int, so the ordinal is pinned
    /// past the two #343 rows and never moves.</summary>
    public class HeadTrackerTypePinTests
    {
        [Fact]
        public void HeadTrackerOrdinal_IsPinnedPastSystemMotion()
        {
            Assert.Equal(33, InputDeviceType.SystemMotion);
            Assert.Equal(34, InputDeviceType.HeadTracker);
        }
    }
}
