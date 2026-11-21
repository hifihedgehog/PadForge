using System;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>
    /// Turns packets into frames for one format or for all of them (issue
    /// #531). The row owns one and calls it under its lock from the receive
    /// thread (UDP) and the poll thread (shared memory).
    ///
    /// <para>Auto-Detect tries the formats in an order that keeps the ones
    /// with a signature ahead of the ones known only by length: custom formats
    /// that name a length or a key, the ETS2 plugin's JSON, F1 (a year at
    /// byte 0), Project CARS 2 (a type at byte 10 with its size), OutGauge,
    /// EA SPORTS WRC, Codemasters, then Forza. Once a format sends a frame it
    /// holds: another format takes over after <see cref="ChallengerFrames"/>
    /// frames in a row, or after <see cref="HoldReleaseMs"/> of silence, so
    /// one stray packet from a second program cannot flip the row.</para>
    /// </summary>
    internal sealed class TelemetryReader
    {
        public const int ChallengerFrames = 3;
        public const long HoldReleaseMs = 1000;

        private readonly TelemetryDecoders.F1State _f1 = new();
        private readonly TelemetryDecoders.ProjectCars2State _projectCars2 = new();
        private readonly TelemetryFrame _scratch = new();
        private TelemetrySource _held;
        private string _heldName = string.Empty;
        private TelemetrySource _challenger;
        private string _challengerName = string.Empty;
        private int _challengerFrames;
        private TelemetrySource _peakSource;
        private float _peakRpm;

        public TelemetryReader(GameTelemetryFormat format, CompiledTelemetryFormat pinned,
            CompiledTelemetryFormat[] detectable, int variableCount)
        {
            Format = format;
            Pinned = pinned;
            Detectable = detectable ?? Array.Empty<CompiledTelemetryFormat>();
            VariableCount = Math.Max(0, variableCount);
            Frame.Reset(VariableCount);
            _scratch.Reset(VariableCount);
        }

        public GameTelemetryFormat Format { get; }
        public CompiledTelemetryFormat Pinned { get; }
        public CompiledTelemetryFormat[] Detectable { get; }
        public int VariableCount { get; }

        /// <summary>The latest frame. Changes only when a read returns true.</summary>
        public TelemetryFrame Frame { get; } = new();

        /// <summary>When the latest frame arrived, 0 before any.</summary>
        public long LastFrameMs { get; private set; }

        /// <summary>True while UDP packets can carry this format, false for
        /// Assetto Corsa, which only the shared memory reader feeds.</summary>
        public bool ReadsUdp => FormatReadsUdp(Format);

        /// <summary>The rule <see cref="ReadsUdp"/> follows, shared with the
        /// Game Telemetry page, which turns the port boxes off while it is false.</summary>
        internal static bool FormatReadsUdp(GameTelemetryFormat format) => format != GameTelemetryFormat.AssettoCorsa;

        /// <summary>True while the shared memory pages are polled.</summary>
        public bool ReadsSharedMemory => Format is GameTelemetryFormat.Auto or GameTelemetryFormat.AssettoCorsa;

        /// <summary>Reads one UDP packet. True when the frame changed.</summary>
        public bool Read(ReadOnlySpan<byte> packet, long nowMs)
        {
            if (!ReadsUdp || packet.IsEmpty) return false;
            _scratch.Reset(VariableCount);
            if (Decode(packet, _scratch) != TelemetryDecode.Frame) return false;
            return Accept(nowMs);
        }

        /// <summary>Reads the three Assetto Corsa pages. True when the frame
        /// changed.</summary>
        public bool ReadKunos(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> statics,
            ReadOnlySpan<byte> graphics, long nowMs)
        {
            if (!ReadsSharedMemory) return false;
            _scratch.Reset(VariableCount);
            if (!TelemetryDecoders.TryKunos(physics, statics, graphics, _scratch)) return false;
            return Accept(nowMs);
        }

        private TelemetryDecode Decode(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            switch (Format)
            {
                case GameTelemetryFormat.Forza: return Bool(TelemetryDecoders.TryForza(p, f));
                case GameTelemetryFormat.F1: return TelemetryDecoders.TryF1(p, _f1, f);
                case GameTelemetryFormat.Codemasters: return Bool(TelemetryDecoders.TryCodemasters(p, f));
                case GameTelemetryFormat.EaSportsWrc: return Bool(TelemetryDecoders.TryEaSportsWrc(p, f));
                case GameTelemetryFormat.OutGauge: return Bool(TelemetryDecoders.TryOutGauge(p, f));
                case GameTelemetryFormat.ProjectCars2: return TelemetryDecoders.TryProjectCars2(p, _projectCars2, f);
                case GameTelemetryFormat.TruckSimulator: return Bool(TelemetryDecoders.TryTruckSimulator(p, f));
                case GameTelemetryFormat.Custom: return Bool(Pinned != null && Pinned.TryRead(p, f, pinned: true));
                case GameTelemetryFormat.Auto: return DecodeAuto(p, f);
                default: return TelemetryDecode.No;
            }
        }

        private TelemetryDecode DecodeAuto(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            foreach (var format in Detectable)
            {
                if (format.TryRead(p, f, pinned: false)) return TelemetryDecode.Frame;
                f.Reset(VariableCount);
            }
            if (TelemetryDecoders.StartsWithObject(p))
                return Bool(TelemetryDecoders.TryTruckSimulator(p, f));
            var r = TelemetryDecoders.TryF1(p, _f1, f);
            if (r != TelemetryDecode.No) return r;
            r = TelemetryDecoders.TryProjectCars2(p, _projectCars2, f);
            if (r != TelemetryDecode.No) return r;
            if (TelemetryDecoders.TryOutGauge(p, f)) return TelemetryDecode.Frame;
            if (TelemetryDecoders.TryEaSportsWrc(p, f)) return TelemetryDecode.Frame;
            if (TelemetryDecoders.TryCodemasters(p, f)) return TelemetryDecode.Frame;
            return Bool(TelemetryDecoders.TryForza(p, f));
        }

        private static TelemetryDecode Bool(bool frame) => frame ? TelemetryDecode.Frame : TelemetryDecode.No;

        /// <summary>The hold, then the max RPM fallback, then the copy.</summary>
        private bool Accept(long nowMs)
        {
            var f = _scratch;
            if (Format == GameTelemetryFormat.Auto && _held != TelemetrySource.None
                && !SameSender(f.Source, f.CustomName, _held, _heldName)
                && nowMs - LastFrameMs < HoldReleaseMs)
            {
                if (SameSender(f.Source, f.CustomName, _challenger, _challengerName))
                    _challengerFrames++;
                else
                {
                    _challenger = f.Source;
                    _challengerName = f.CustomName;
                    _challengerFrames = 1;
                }
                if (_challengerFrames < ChallengerFrames) return false;
            }
            _challenger = TelemetrySource.None;
            _challengerName = string.Empty;
            _challengerFrames = 0;
            _held = f.Source;
            _heldName = f.CustomName;

            // A game with no max RPM gets the highest RPM seen since it
            // started sending, tm-bt-led's detectedMaxRpm. tm-bt-led starts
            // that at 5000, which would hold a truck governed near 2,500 RPM
            // to half scale, so the peak here starts at 0.
            if (_peakSource != f.Source)
            {
                _peakSource = f.Source;
                _peakRpm = 0f;
            }
            f.Clamp();
            if (f.Rpm > _peakRpm) _peakRpm = f.Rpm;
            if (f.MaxRpm <= 0f)
            {
                f.MaxRpm = _peakRpm;
                f.MaxRpmLearned = true;
            }

            f.CopyTo(Frame);
            LastFrameMs = nowMs;
            return true;
        }

        private static bool SameSender(TelemetrySource a, string aName, TelemetrySource b, string bName)
            => a == b && (a != TelemetrySource.Custom || string.Equals(aName, bName, StringComparison.Ordinal));
    }
}
