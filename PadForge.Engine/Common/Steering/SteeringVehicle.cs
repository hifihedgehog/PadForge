using System;
using System.Collections.Generic;

namespace PadForge.Engine.Steering
{
    /// <summary>
    /// The four settings of the steering feel model that each move one of
    /// the measures steering is tested by (Balachandran and Gerdes,
    /// "Designing Steering Feel for Steer-by-Wire Vehicles Using Objective
    /// Measures", IEEE/ASME Transactions on Mechatronics 2014, Table II):
    /// the tire moment gain (torque stiffness), the jacking torque spring
    /// constant (on-center feel), and the power assist weighting's width
    /// and lower limit (linearity). Damping is the fourth of the paper's
    /// critical settings and lives with the wheel, since it is a force on
    /// the wheel's motion and no part of the torque curve.
    /// </summary>
    public readonly struct SteeringFeel
    {
        /// <summary>Tire moment gain K (equation 4): the share of the
        /// assisted tire moment that reaches the handwheel.</summary>
        public readonly double Gain;

        /// <summary>Jacking torque spring constant in N·m per radian of
        /// road wheel angle (equation 10, with no dead band).</summary>
        public readonly double JackingStiffness;

        /// <summary>Standard deviation of the power assist weighting in
        /// radians of front slip angle (equation 13).</summary>
        public readonly double AssistWidth;

        /// <summary>Lower limit of the power assist weighting, 0 to 1
        /// (equation 13). 1 is no assist.</summary>
        public readonly double AssistFloor;

        public SteeringFeel(double gain, double jackingStiffness, double assistWidth, double assistFloor)
        {
            Gain = gain;
            JackingStiffness = jackingStiffness;
            AssistWidth = assistWidth;
            AssistFloor = assistFloor;
        }

        /// <summary>This feel under the user's three scales, each in
        /// percent with 100 the car as it is. Steering Weight scales the
        /// gain and On-Center Feel the jacking stiffness. Power Assist
        /// scales how far the weighting falls below 1: none at 0, the
        /// car's own at 100, and more above it, down to a floor of
        /// <see cref="LeastAssistFloor"/>.</summary>
        public SteeringFeel Scaled(double weightPercent, double onCenterPercent, double assistPercent)
        {
            double floor = 1.0 - (1.0 - AssistFloor) * Math.Max(0.0, assistPercent) / 100.0;
            return new SteeringFeel(
                Gain * Math.Max(0.0, weightPercent) / 100.0,
                JackingStiffness * Math.Max(0.0, onCenterPercent) / 100.0,
                AssistWidth,
                Math.Clamp(floor, LeastAssistFloor, 1.0));
        }

        /// <summary>The least the assist weighting may fall to under the
        /// Power Assist scale, so the torque never falls to nothing.</summary>
        public const double LeastAssistFloor = 0.05;
    }

    /// <summary>
    /// A car for the steady-state steering model: the bicycle model's
    /// mass and axle distances, each axle's cornering stiffness, the
    /// steering ratio and the two trails, with the feel the car has at
    /// every scale on 100.
    /// </summary>
    public sealed class SteeringVehicle
    {
        public const double Gravity = 9.81;

        /// <summary>Mechanical trail in meters, the same for every car
        /// here: no source below prints one, and 20 mm is an ordinary
        /// caster trail.</summary>
        public const double DefaultMechanicalTrail = 0.020;

        /// <summary>Pneumatic trail at zero slip in meters, the same for
        /// every car here. The brush tire model puts it at a third of
        /// half the contact length (Pacejka, Tire and Vehicle Dynamics,
        /// the brush model's aligning torque), which is 30 mm for a
        /// 180 mm contact patch.</summary>
        public const double DefaultPneumaticTrail = 0.030;

        /// <summary>The stable key a setting stores.</summary>
        public string Id { get; }

        /// <summary>Vehicle mass in kg.</summary>
        public double Mass { get; }

        /// <summary>Distance from the center of gravity to the front axle in meters.</summary>
        public double A { get; }

        /// <summary>Distance from the center of gravity to the rear axle in meters.</summary>
        public double B { get; }

        /// <summary>Front axle cornering stiffness in N/rad.</summary>
        public double FrontStiffness { get; }

        /// <summary>Rear axle cornering stiffness in N/rad.</summary>
        public double RearStiffness { get; }

        /// <summary>Handwheel angle over road wheel angle.</summary>
        public double SteeringRatio { get; }

        public double MechanicalTrail { get; }
        public double PneumaticTrail { get; }

        /// <summary>The car's feel with every scale on 100.</summary>
        public SteeringFeel Feel { get; }

        public double Wheelbase => A + B;

        /// <summary>Static load on the front axle in newtons.</summary>
        public double FrontLoad => Mass * Gravity * B / Wheelbase;

        /// <summary>Static load on the rear axle in newtons.</summary>
        public double RearLoad => Mass * Gravity * A / Wheelbase;

        public SteeringVehicle(string id, double mass, double a, double b, double frontStiffness, double rearStiffness,
            double steeringRatio, SteeringFeel feel,
            double mechanicalTrail = DefaultMechanicalTrail, double pneumaticTrail = DefaultPneumaticTrail)
        {
            Id = id;
            Mass = mass;
            A = a;
            B = b;
            FrontStiffness = frontStiffness;
            RearStiffness = rearStiffness;
            SteeringRatio = steeringRatio;
            Feel = feel;
            MechanicalTrail = mechanicalTrail;
            PneumaticTrail = pneumaticTrail;
        }

        // ─────────────────────────────────────────────
        //  The cars
        // ─────────────────────────────────────────────

        /// <summary>Axle cornering stiffness per newton of axle load for
        /// the three CommonRoad cars, in 1/rad: the tire's p_ky1 of
        /// -21.92 (commonroad-vehicle-models 3.0.2,
        /// parameters_tire.yaml). Its single-track model takes each
        /// axle's lateral force as mu times C_S times the axle load times
        /// the slip angle, with mu = p_dy1 and C_S = -p_ky1 / p_dy1
        /// (vehicle_dynamics_st.py:41-43), and the two multiply to
        /// 21.92.</summary>
        public const double CommonRoadStiffnessPerLoad = 21.92;

        /// <summary>60 mph in m/s, the speed the paper's weave test and
        /// its production ranges are stated at.</summary>
        public const double SixtyMph = 60 * 0.44704;

        /// <summary>
        /// A production car from a CommonRoad parameter set. The rear axle
        /// takes the tire's stiffness for its load. The front axle takes
        /// an effective stiffness, lower than the tire's, as steering
        /// compliance makes it on a real car: the one that puts the
        /// linear model's steering sensitivity at 60 mph on
        /// <paramref name="sensitivityAt60"/> g per 100 degrees of
        /// handwheel. That value and the steering ratio are stated for
        /// each car here, inside the range the paper quotes for
        /// production cars (0.27 to 1.42, its Table III).
        /// </summary>
        public static SteeringVehicle Production(string id, double mass, double a, double b,
            double steeringRatio, double sensitivityAt60, SteeringFeel feel)
        {
            double wheelbase = a + b;
            double rear = CommonRoadStiffnessPerLoad * mass * Gravity * a / wheelbase;
            double speedSquared = SixtyMph * SixtyMph;
            // Lateral acceleration per radian of road wheel angle that the sensitivity asks for.
            double gain = sensitivityAt60 / 100.0 * Gravity * steeringRatio * (180.0 / Math.PI);
            // Linear bicycle model: ay / delta = U^2 / (L + K U^2), so K, the understeer in rad per m/s^2:
            double understeer = (speedSquared / gain - wheelbase) / speedSquared;
            // K = (m / L) (b / Cf - a / Cr), solved for the front axle.
            double front = b / (understeer * wheelbase / mass + a / rear);
            return new SteeringVehicle(id, mass, a, b, front, rear, steeringRatio, feel);
        }

        /// <summary>Stanford's X1, the paper's own test car: its Table I
        /// (mass 1973 kg, a 1.53 m, b 1.23 m, cornering stiffness 110 and
        /// 148 kN/rad). The paper prints no steering ratio. 16 puts the
        /// model's steering sensitivity at 2.48 and 0.49 g per 100
        /// degrees at 60 and 25 mph, where its Table III gives 2.33 and
        /// 0.52. The feel is fitted to that table: on-center feel 17 and
        /// 22 N·m/g at 60 and 25 mph, linearity 25 percent at 60
        /// mph.</summary>
        public static readonly SteeringVehicle StanfordX1 = new SteeringVehicle(
            "x1", 1973.0, 1.53, 1.23, 110e3, 148e3, 16.0,
            new SteeringFeel(0.039198, 754.05, 0.008508, 0.40));

        /// <summary>A compact car: the Ford Escort set of Althoff and
        /// Würsching, "CommonRoad: Vehicle Models" (2020), from the U.S.
        /// DOT vehicle dynamics data (parameters_vehicle1.yaml). Stated:
        /// ratio 17, sensitivity 1.10, on-center feel 14 N·m/g and
        /// linearity 30 percent at 60 mph.</summary>
        public static readonly SteeringVehicle Compact = Production(
            "compact", 1225.8878467253344, 0.88392, 1.50876, 17.0, 1.10,
            new SteeringFeel(0.032240, 865.82, 0.012309, 0.40));

        /// <summary>A sedan: the BMW 320i set of the same source
        /// (parameters_vehicle2.yaml). Stated: ratio 16, sensitivity
        /// 1.00, on-center feel 17 N·m/g and linearity 25 percent at 60
        /// mph, the on-center feel and linearity the paper chose for its
        /// own car.</summary>
        public static readonly SteeringVehicle Sedan = Production(
            "sedan", 1093.2952334674046, 1.1561957064, 1.4227170936, 16.0, 1.00,
            new SteeringFeel(0.049658, 638.98, 0.012810, 0.40));

        /// <summary>A van: the VW Vanagon set of the same source
        /// (parameters_vehicle3.yaml). Stated: ratio 19, sensitivity
        /// 0.70, on-center feel 12 N·m/g and linearity 20 percent at 60
        /// mph.</summary>
        public static readonly SteeringVehicle Van = Production(
            "van", 1478.8979637767998, 1.1507916024, 1.3211363976000001, 19.0, 0.70,
            new SteeringFeel(0.025406, 930.39, 0.013990, 0.40));

        /// <summary>A race car: the car of TUM's race trajectory planner
        /// (TUMFTM/global_racetrajectory_optimization, params/racecar.ini
        /// at a9995e2): mass 1200 kg, 1.6 m and 1.4 m from the center of
        /// gravity to the axles, and Magic Formula tires with B 10, C 2.5
        /// and D = F_z (1 - 0.1 F_z / 3000) at a friction coefficient of
        /// 1. A tire's cornering stiffness is B C D and an axle has two
        /// tires. Stated: ratio 12, on-center feel 26 N·m/g and linearity
        /// 80 percent at 60 mph, which is little assist. Its steering
        /// sensitivity, 3.6 g per 100 degrees at 60 mph, is far above a
        /// production car's, as a race car's is.</summary>
        public static readonly SteeringVehicle RaceCar = new SteeringVehicle(
            "race", 1200.0, 1.6, 1.4, RaceCarAxle(1.4), RaceCarAxle(1.6), 12.0,
            new SteeringFeel(0.090920, 437.23, 0.005161, 0.85));

        /// <summary>An axle of the race car: two tires, each with
        /// stiffness B C D at its static load, the load carried through
        /// the other axle's distance from the center of gravity.</summary>
        private static double RaceCarAxle(double otherAxleDistance)
        {
            double tireLoad = 1200.0 * Gravity * otherAxleDistance / 3.0 / 2.0;
            double d = tireLoad * (1.0 - 0.1 * tireLoad / 3000.0);
            return 2.0 * 10.0 * 2.5 * d;
        }

        /// <summary>The cars in the order the list shows them. The first
        /// is the default.</summary>
        public static readonly IReadOnlyList<SteeringVehicle> All = new[] { Sedan, Compact, Van, RaceCar, StanfordX1 };

        /// <summary>The car a stored key names, or the default for a key
        /// no car has.</summary>
        public static SteeringVehicle ById(string id)
        {
            foreach (var vehicle in All)
                if (string.Equals(vehicle.Id, id, StringComparison.OrdinalIgnoreCase))
                    return vehicle;
            return All[0];
        }
    }
}
