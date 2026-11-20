using System;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>The game a user picks on the Dashboard's Game Telemetry card
    /// (issue #531). Saved by name. APPEND-ONLY: a profile written by a later
    /// build must still read on this one.</summary>
    public enum GameTelemetryFormat
    {
        /// <summary>Tell the game from the packets that arrive.</summary>
        Auto = 0,
        Forza = 1,
        F1 = 2,
        /// <summary>DiRT Rally 2.0 and the other Codemasters games sending
        /// the 264-byte <c>extradata="3"</c> packet.</summary>
        Codemasters = 3,
        EaSportsWrc = 4,
        /// <summary>OutGauge, from BeamNG.drive or Live for Speed.</summary>
        OutGauge = 5,
        /// <summary>Project CARS 2's UDP version 2, which Automobilista 2 also
        /// sends.</summary>
        ProjectCars2 = 6,
        /// <summary>Assetto Corsa and Assetto Corsa Competizione, read from
        /// their shared memory rather than UDP.</summary>
        AssettoCorsa = 7,
        /// <summary>Euro Truck Simulator 2 and American Truck Simulator,
        /// through MrHokss's ets2-telemetry-udp plugin.</summary>
        TruckSimulator = 8,
        /// <summary>A custom format, named by
        /// <c>GameTelemetryRuntime.CustomFormatName</c>.</summary>
        Custom = 9,
    }

    /// <summary>What sent the latest frame, for the status line.</summary>
    public enum TelemetrySource
    {
        None = 0,
        ForzaSled,
        ForzaMotorsport7,
        ForzaMotorsport,
        ForzaHorizon,
        F1,
        Codemasters,
        EaSportsWrc,
        BeamNg,
        LiveForSpeed,
        ProjectCars2,
        AssettoCorsa,
        AssettoCorsaCompetizione,
        TruckSimulator,
        Custom,
    }

    /// <summary>
    /// One reading of a game's telemetry (issue #531) in fixed units, the
    /// same for every format: speed in meters per second, RPM, gear with -1
    /// for reverse and 0 for neutral, the pedals from 0 to 1, steering
    /// from -1 (full left) to 1 (full right), and the road's grip under the
    /// front wheels against dry asphalt's (#588). A field a game does not
    /// send stays at its rest value. Reused in place: a decoder writes into it and
    /// the row copies it under its lock.
    /// </summary>
    internal sealed class TelemetryFrame
    {
        public float SpeedMs;
        public float Rpm;
        /// <summary>0 when the game sends none. The reader then supplies the
        /// highest RPM seen and sets <see cref="MaxRpmLearned"/>.</summary>
        public float MaxRpm;
        /// <summary>The reader filled <see cref="MaxRpm"/> from the highest
        /// RPM seen, because the game sent none.</summary>
        public bool MaxRpmLearned;
        public int Gear;
        public float Throttle;
        public float Brake;
        public float Clutch;
        public float Handbrake;
        public float Steering;
        /// <summary>The road's grip under the front wheels against dry
        /// asphalt's (PadForge.Engine.Steering.RoadGrip), 0 where the game
        /// sends no word on the road. A wheel's centering reads it.</summary>
        public float Grip;
        /// <summary>The game's own "driving" flag, meaningful only while
        /// <see cref="DrivingReported"/> is set: Forza's IsRaceOn, Project
        /// CARS 2's playing state, Assetto Corsa's live status.</summary>
        public bool Driving;
        public bool DrivingReported;
        /// <summary>Custom variables in <c>GameTelemetryRuntime</c>'s order,
        /// in their own units after scaling.</summary>
        public float[] Variables = Array.Empty<float>();
        public TelemetrySource Source;
        /// <summary>The F1 game's year, for the status line: the game year
        /// byte when the packet carries one that is not 0, the packet
        /// format's year otherwise.</summary>
        public int F1Year;
        /// <summary>The custom format's name when <see cref="Source"/> is
        /// Custom.</summary>
        public string CustomName = string.Empty;

        /// <summary>Puts every field at rest and sizes the variables.</summary>
        public void Reset(int variableCount)
        {
            SpeedMs = 0f;
            Rpm = 0f;
            MaxRpm = 0f;
            MaxRpmLearned = false;
            Gear = 0;
            Throttle = 0f;
            Brake = 0f;
            Clutch = 0f;
            Handbrake = 0f;
            Steering = 0f;
            Grip = 0f;
            Driving = false;
            DrivingReported = false;
            if (Variables.Length != variableCount)
                Variables = variableCount == 0 ? Array.Empty<float>() : new float[variableCount];
            else
                Array.Clear(Variables);
            Source = TelemetrySource.None;
            F1Year = 0;
            CustomName = string.Empty;
        }

        public void CopyTo(TelemetryFrame dst)
        {
            dst.SpeedMs = SpeedMs;
            dst.Rpm = Rpm;
            dst.MaxRpm = MaxRpm;
            dst.MaxRpmLearned = MaxRpmLearned;
            dst.Gear = Gear;
            dst.Throttle = Throttle;
            dst.Brake = Brake;
            dst.Clutch = Clutch;
            dst.Handbrake = Handbrake;
            dst.Steering = Steering;
            dst.Grip = Grip;
            dst.Driving = Driving;
            dst.DrivingReported = DrivingReported;
            if (dst.Variables.Length != Variables.Length)
                dst.Variables = Variables.Length == 0 ? Array.Empty<float>() : new float[Variables.Length];
            Array.Copy(Variables, dst.Variables, Variables.Length);
            dst.Source = Source;
            dst.F1Year = F1Year;
            dst.CustomName = CustomName;
        }

        /// <summary>Clamps the pedals and the steering into range, so a game
        /// that overshoots or sends a NaN cannot push an axis past its end.</summary>
        public void Clamp()
        {
            SpeedMs = Finite(Math.Abs(SpeedMs));
            Rpm = Math.Max(0f, Finite(Rpm));
            MaxRpm = Math.Max(0f, Finite(MaxRpm));
            Throttle = Unit(Throttle);
            Brake = Unit(Brake);
            Clutch = Unit(Clutch);
            Handbrake = Unit(Handbrake);
            Steering = Math.Clamp(Finite(Steering), -1f, 1f);
            Grip = Math.Clamp(Finite(Grip), 0f, 1f);
        }

        private static float Finite(float v) => float.IsFinite(v) ? v : 0f;
        private static float Unit(float v) => Math.Clamp(Finite(v), 0f, 1f);
    }
}
