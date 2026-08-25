using System;
using System.Buffers.Binary;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// Pure decoding and scaling for head-tracking sources (issue #355).
    /// No Windows calls, so the wire contracts are replay-testable from
    /// bytes.
    ///
    /// <para>The pose is kept in OpenTrack's own convention: six doubles in
    /// the order TX, TY, TZ, Yaw, Pitch, Roll (opentrack
    /// api/plugin-api.hpp, enum Axis), translation in centimeters and
    /// rotation in degrees (the FreeTrack writer in
    /// proto-ft/ftnoir_protocol_ft.cpp multiplies translation by 10 for
    /// millimeters and rotation by pi/180 for radians). Positive yaw moves
    /// OpenTrack's mouse output right and positive pitch moves it up
    /// (proto-mouse/ftnoir_protocol_mouse.cpp, the invert table).</para>
    /// </summary>
    public static class HeadPose
    {
        /// <summary>OpenTrack "UDP over network": sizeof(double[6]) per pose
        /// (proto-udp/ftnoir_protocol_ftn.cpp, udp::pose).</summary>
        public const int OpenTrackUdpBytes = 48;

        /// <summary>FreeTrack 2.0 heap: FTData (92 bytes) + GameID + an
        /// 8-byte table + GameID2 (freetrackclient/fttypes.h).</summary>
        public const int FreeTrackHeapBytes = 108;

        // Pose indices, opentrack's enum Axis.
        public const int TX = 0, TY = 1, TZ = 2, Yaw = 3, Pitch = 4, Roll = 5;
        public const int PoseCount = 6;

        // Device axis indices on the Head Tracker row.
        public const int AxisYaw = 0, AxisPitch = 1, AxisRoll = 2, AxisX = 3, AxisY = 4, AxisZ = 5;
        public const int AxisCount = 6;

        /// <summary>Rest value of a centered axis (CustomInputState doc:
        /// center is 32768, not the arithmetic midpoint).</summary>
        public const int AxisCenter = 32768;

        /// <summary>
        /// Decodes one OpenTrack UDP datagram: six little-endian doubles.
        /// A datagram carrying NaN or infinity is rejected whole, the rule
        /// OpenTrack's own UDP tracker applies
        /// (tracker-udp/ftnoir_tracker_udp.cpp). Longer datagrams are read
        /// for their first 48 bytes, as that receiver does.
        /// </summary>
        public static bool TryDecodeOpenTrackUdp(ReadOnlySpan<byte> datagram, Span<double> pose)
        {
            if (datagram.Length < OpenTrackUdpBytes || pose.Length < PoseCount) return false;
            Span<double> tmp = stackalloc double[PoseCount];
            for (int i = 0; i < PoseCount; i++)
            {
                double v = BinaryPrimitives.ReadDoubleLittleEndian(datagram.Slice(i * 8, 8));
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
                tmp[i] = v;
            }
            tmp.CopyTo(pose);
            return true;
        }

        /// <summary>
        /// Decodes the FreeTrack heap into the OpenTrack convention by
        /// inverting OpenTrack's writer (proto-ft freetrack::pose): yaw and
        /// pitch are negated radians, roll is radians, translation is
        /// millimeters. Offsets (fttypes.h FTData): DataID 0, CamWidth 4,
        /// CamHeight 8, Yaw 12, Pitch 16, Roll 20, X 24, Y 28, Z 32.
        /// <paramref name="dataId"/> increments once per written pose, so
        /// an unchanged value means no new pose.
        /// </summary>
        public static bool TryDecodeFreeTrackHeap(ReadOnlySpan<byte> heap, out uint dataId, Span<double> pose)
        {
            dataId = 0;
            if (heap.Length < 36 || pose.Length < PoseCount) return false;
            dataId = BinaryPrimitives.ReadUInt32LittleEndian(heap);
            float yaw = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(12, 4));
            float pitch = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(16, 4));
            float roll = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(20, 4));
            float x = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(24, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(28, 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(heap.Slice(32, 4));
            if (!float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(roll)
                || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return false;
            const double r2d = 180.0 / Math.PI;
            pose[Yaw] = -yaw * r2d;
            pose[Pitch] = -pitch * r2d;
            pose[Roll] = roll * r2d;
            pose[TX] = x / 10.0;
            pose[TY] = y / 10.0;
            pose[TZ] = z / 10.0;
            return true;
        }

        /// <summary>A pose value against its full-deflection range, into
        /// the unsigned axis space: -range reads 0, rest reads 32768, +range
        /// reads 65535, beyond either end clamps. A non-positive range or a
        /// NaN reads rest.</summary>
        public static int ToAxis(double value, double range)
        {
            if (!(range > 0) || double.IsNaN(value)) return AxisCenter;
            double f = Math.Clamp(value / range, -1.0, 1.0);
            return f >= 0
                ? (int)Math.Round(AxisCenter + f * 32767.0)
                : (int)Math.Round(AxisCenter + f * 32768.0);
        }

        /// <summary>
        /// Fills the six device axes from a pose. The vertical axes (pitch
        /// and Y) are stored in stick orientation, up at the low end, so a
        /// mapping onto a stick's Y axis needs no inversion: head up reads
        /// as stick pushed up. Yaw and X right read high, like a stick
        /// pushed right.
        /// </summary>
        public static void FillAxes(ReadOnlySpan<double> pose, double rotationRange, double translationRange, Span<int> axes)
        {
            axes[AxisYaw] = ToAxis(pose[Yaw], rotationRange);
            axes[AxisPitch] = ToAxis(-pose[Pitch], rotationRange);
            axes[AxisRoll] = ToAxis(pose[Roll], rotationRange);
            axes[AxisX] = ToAxis(pose[TX], translationRange);
            axes[AxisY] = ToAxis(-pose[TY], translationRange);
            axes[AxisZ] = ToAxis(pose[TZ], translationRange);
        }

        /// <summary>Every device axis at rest.</summary>
        public static void CenterAxes(Span<int> axes)
        {
            for (int i = 0; i < AxisCount && i < axes.Length; i++)
                axes[i] = AxisCenter;
        }
    }
}
