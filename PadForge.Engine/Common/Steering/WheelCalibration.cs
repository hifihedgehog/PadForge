using System;
using System.Globalization;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// What the Release Test measured on one wheel (#588): the numbers no
    /// driver states and the centering math needs. It belongs to the
    /// wheel, so it is kept on the device record
    /// (<see cref="PadForge.Engine.Data.UserDevice.WheelCalibration"/>).
    ///
    /// <para>A wheel's spring and damper take a coefficient, and nothing
    /// says how strong each coefficient is or that twice the coefficient
    /// is twice the strength: Logitech's spring has sixteen steps
    /// (new-lg4ff hid-lg4ff.c, lg4ff_update_slot). So the test measures
    /// each at the coefficients in <see cref="SpringCoefficients"/> and
    /// <see cref="DamperCoefficients"/>, the spring is only ever sent at
    /// one of its measured coefficients, and the damper's strength
    /// between its two is read off the line through them, at a
    /// coefficient the wheel's firmware has a step for
    /// (<see cref="DamperGrid"/>).</para>
    /// </summary>
    public sealed class WheelCalibration
    {
        /// <summary>The spring coefficients measured, of 10000.</summary>
        public static readonly int[] SpringCoefficients = { 3333, 6667, 10000 };

        /// <summary>The damper coefficients measured, of 10000: eight
        /// fifteenths and the whole. A firmware that keeps the
        /// coefficient in four bits has fifteen steps above nothing
        /// (new-lg4ff SCALE_COEFF(k, 4), and hid-fanatecff the same), and
        /// a coefficient of that many fifteenths lands on that step, so
        /// both measurements are of whole steps on such a wheel and of
        /// plain shares on a finer one.</summary>
        public static readonly int[] DamperCoefficients = { 5333, 10000 };

        /// <summary>The steps a damper's coefficient is taken to have
        /// where nothing says: hundredths.</summary>
        public const int FineDamperSteps = 100;

        /// <summary>The steps of a firmware that keeps the coefficient
        /// in four bits.</summary>
        public const int FourBitDamperSteps = 15;

        /// <summary>Moment of inertia of the rim and what turns with it, in kg·m².</summary>
        public double Inertia { get; init; }

        /// <summary>The wheel's own friction in N·m.</summary>
        public double Friction { get; init; }

        /// <summary>Seconds from a torque command to the wheel's first motion.</summary>
        public double Delay { get; init; }

        /// <summary>The damping the wheel's damper gives at the first of
        /// <see cref="DamperCoefficients"/> and at the full coefficient,
        /// in N·m·s/rad. 0 where the wheel showed none.</summary>
        public double DamperMid { get; init; }
        public double DamperFull { get; init; }

        /// <summary>The stiffness the wheel's spring gives at a third,
        /// two thirds and the whole of its coefficient, in N·m per unit
        /// of deflection, where a unit is half the rotation range. The
        /// firmware works on the axis's position, so the stiffness per
        /// radian is this over half the range in radians. 0 where the
        /// wheel showed none.</summary>
        public double SpringThird { get; init; }
        public double SpringTwoThirds { get; init; }
        public double SpringFull { get; init; }

        /// <summary>+1 when a positive level pushes the wheel toward
        /// lower axis values, the convention the force feedback pass
        /// uses, -1 when the wheel answered the other way.</summary>
        public int ForceSign { get; init; } = 1;

        /// <summary>The damping ratio the test chose, of the wheel's
        /// inertia against the curve's stiffness at center, for a return
        /// that does not swing past center.</summary>
        public double ReturnRatio { get; init; }

        /// <summary>The most torque the wheel gave when measured, in N·m,
        /// which every number above was scaled by.</summary>
        public double WheelTorque { get; init; }

        public bool IsValid => Inertia > 0 && Delay > 0 && WheelTorque > 0;

        /// <summary>The wheel's damper showed itself at one coefficient
        /// or the other.</summary>
        public bool HasDamper => DamperMid > 0 || DamperFull > 0;

        /// <summary>The spring's stiffness at one of the measured
        /// coefficients, by its place in <see cref="SpringCoefficients"/>.</summary>
        public double SpringAt(int index) => index switch { 0 => SpringThird, 1 => SpringTwoThirds, _ => SpringFull };

        /// <summary>The spring's stiffness at any coefficient, for a
        /// simulation of the measured wheel: the measured figure at a
        /// measured coefficient, and between two of them the line
        /// through the nearest that showed a spring.</summary>
        public double SpringStiffnessAt(int coefficient)
        {
            double lowC = 0, lowK = 0;
            for (int i = 0; i < SpringCoefficients.Length; i++)
            {
                double k = SpringAt(i);
                if (k <= 0) continue;
                if (coefficient <= SpringCoefficients[i])
                    return lowK + (k - lowK) * (coefficient - lowC) / (SpringCoefficients[i] - lowC);
                lowC = SpringCoefficients[i];
                lowK = k;
            }
            return lowK;
        }

        /// <summary>The damping a coefficient gives, in N·m·s/rad: on the
        /// line from nothing through the two measured points. A wheel
        /// measured at one point only reads on the line through it, and
        /// one whose full coefficient showed less than its middle one
        /// reads level between them.</summary>
        public double DampingAt(int coefficient)
        {
            double midAt = DamperCoefficients[0];
            double mid = DamperMid > 0 ? DamperMid : DamperFull * midAt / 10000.0;
            double full = Math.Max(DamperFull > 0 ? DamperFull : mid * 10000.0 / midAt, mid);
            if (coefficient <= midAt) return mid * Math.Max(0, coefficient) / midAt;
            return mid + (full - mid) * (Math.Min(coefficient, 10000) - midAt) / (10000.0 - midAt);
        }

        /// <summary>The coefficient of step <paramref name="step"/> of
        /// <paramref name="steps"/>.</summary>
        public static int DamperGrid(int step, int steps) => (int)Math.Round(step * 10000.0 / steps);

        /// <summary>The least coefficient that gives a damping, among
        /// the coefficients a damper of <paramref name="steps"/> steps
        /// has, or the full coefficient where the damper has less than
        /// that to give.</summary>
        public int DamperCoefficientFor(double damping, int steps = FineDamperSteps)
        {
            if (damping <= 0 || !HasDamper) return 0;
            steps = Math.Clamp(steps, 1, 1000);
            int lo = 0, hi = steps;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (DampingAt(DamperGrid(mid, steps)) < damping) lo = mid + 1; else hi = mid;
            }
            return DamperGrid(lo, steps);
        }

        /// <summary>The measurement in the newton meters of another
        /// torque figure. Every torque in it was read as a share of the
        /// wheel's torque, so the inertia, the friction, the damper and
        /// the spring scale with the figure. The delay, the sign and
        /// the return ratio are the wheel's whatever the figure.</summary>
        public WheelCalibration ScaledTo(double wheelTorque)
        {
            if (wheelTorque <= 0 || WheelTorque <= 0 || wheelTorque == WheelTorque) return this;
            double scale = wheelTorque / WheelTorque;
            return new WheelCalibration
            {
                Inertia = Inertia * scale,
                Friction = Friction * scale,
                Delay = Delay,
                DamperMid = DamperMid * scale,
                DamperFull = DamperFull * scale,
                SpringThird = SpringThird * scale,
                SpringTwoThirds = SpringTwoThirds * scale,
                SpringFull = SpringFull * scale,
                ForceSign = ForceSign,
                ReturnRatio = ReturnRatio,
                WheelTorque = wheelTorque,
            };
        }

        private const string Version = "1";
        private const int Values = 11;

        /// <summary>The saved form: version and values, invariant
        /// culture, separated by semicolons.</summary>
        public string Format()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join(";",
                Version,
                Inertia.ToString("R", ic), Friction.ToString("R", ic), Delay.ToString("R", ic),
                DamperMid.ToString("R", ic), DamperFull.ToString("R", ic),
                SpringThird.ToString("R", ic), SpringTwoThirds.ToString("R", ic), SpringFull.ToString("R", ic),
                ForceSign.ToString(ic), ReturnRatio.ToString("R", ic), WheelTorque.ToString("R", ic));
        }

        /// <summary>Reads the saved form. Empty, another version or a
        /// value that is no number reads as no measurement.</summary>
        public static WheelCalibration Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text.Split(';');
            if (parts.Length != Values + 1 || parts[0] != Version) return null;
            var ic = CultureInfo.InvariantCulture;
            var values = new double[Values];
            for (int i = 0; i < Values; i++)
            {
                if (!double.TryParse(parts[i + 1], NumberStyles.Float, ic, out values[i])
                    || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                    return null;
            }
            var result = new WheelCalibration
            {
                Inertia = values[0],
                Friction = Math.Max(0, values[1]),
                Delay = values[2],
                DamperMid = Math.Max(0, values[3]),
                DamperFull = Math.Max(0, values[4]),
                SpringThird = Math.Max(0, values[5]),
                SpringTwoThirds = Math.Max(0, values[6]),
                SpringFull = Math.Max(0, values[7]),
                ForceSign = values[8] < 0 ? -1 : 1,
                ReturnRatio = Math.Clamp(values[9], 0, 2),
                WheelTorque = values[10],
            };
            return result.IsValid ? result : null;
        }
    }
}
