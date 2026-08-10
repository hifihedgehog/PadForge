using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Pure math for the orientation-only firmware fallback (issue #188
    /// plan item 3): synthesizing an angular rate from consecutive
    /// rotation vectors when the descriptor reports rotation (usage
    /// 0x0544) but no gyro usage. The protocol's rotation vector is
    /// axis-angle in radians. The rate is the axis-angle of the relative
    /// rotation between two samples divided by their arrival gap, which
    /// for the small per-packet rotations at 25 Hz is the body-frame
    /// angular velocity.
    /// </summary>
    internal static class HeadTrackerMath
    {
        /// <summary>
        /// Computes the angular rate (rad/s, sensor frame) taking the
        /// orientation from the previous rotation vector to the current
        /// one over <paramref name="dtSeconds"/>. Returns false when the
        /// inputs cannot produce a meaningful rate (non-positive dt).
        /// </summary>
        internal static bool AngularRateFromRotationVectors(
            double px, double py, double pz,
            double cx, double cy, double cz,
            double dtSeconds, double[] rateOut)
        {
            if (dtSeconds <= 0) return false;

            // q_delta = conj(q_prev) * q_cur, expressed axis-angle / dt.
            Span<double> qp = stackalloc double[4];
            Span<double> qc = stackalloc double[4];
            FromRotationVector(px, py, pz, qp);
            FromRotationVector(cx, cy, cz, qc);

            // conj(qp) * qc (w, x, y, z)
            double w = qp[0] * qc[0] + qp[1] * qc[1] + qp[2] * qc[2] + qp[3] * qc[3];
            double x = qp[0] * qc[1] - qp[1] * qc[0] - qp[2] * qc[3] + qp[3] * qc[2];
            double y = qp[0] * qc[2] + qp[1] * qc[3] - qp[2] * qc[0] - qp[3] * qc[1];
            double z = qp[0] * qc[3] - qp[1] * qc[2] + qp[2] * qc[1] - qp[3] * qc[0];

            // Shortest arc: a negated quaternion is the same rotation.
            if (w < 0) { w = -w; x = -x; y = -y; z = -z; }

            double sinHalf = Math.Sqrt(x * x + y * y + z * z);
            if (sinHalf < 1e-12)
            {
                rateOut[0] = 0; rateOut[1] = 0; rateOut[2] = 0;
                return true;
            }
            double angle = 2.0 * Math.Atan2(sinHalf, Math.Clamp(w, -1.0, 1.0));
            double scale = angle / (sinHalf * dtSeconds);
            rateOut[0] = x * scale;
            rateOut[1] = y * scale;
            rateOut[2] = z * scale;
            return true;
        }

        /// <summary>Axis-angle rotation vector (radians) to a unit
        /// quaternion (w, x, y, z).</summary>
        private static void FromRotationVector(double rx, double ry, double rz, Span<double> q)
        {
            double angle = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (angle < 1e-12)
            {
                q[0] = 1; q[1] = 0; q[2] = 0; q[3] = 0;
                return;
            }
            double half = angle * 0.5;
            double s = Math.Sin(half) / angle;
            q[0] = Math.Cos(half);
            q[1] = rx * s;
            q[2] = ry * s;
            q[3] = rz * s;
        }
    }
}
