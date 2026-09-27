using System;
using System.Globalization;

namespace PadForge.Engine
{
    /// <summary>
    /// The GunCon 2 beam counts that meet the edges of the picture: X at the
    /// left and right edges, Y at the top and bottom. The counts depend on the
    /// CRT, the video mode and the game, so every PC tool for the gun has a
    /// calibration step (hifihedgehog/SDL#33 Part 9, docs/README-guncon.md in
    /// the fork). beardypig's and psakhis's guncon2 tools shoot four targets
    /// set in from the corners and extend the shots to the edges
    /// (calibrate.py). This fits a line through the shots on each axis and
    /// reads it at the edges, which holds for any target layout. Immutable,
    /// so the poll thread can read it while the UI replaces it.
    /// </summary>
    public sealed class GunCon2Calibration
    {
        /// <summary>The window the PC tools start from: X 175 to 720 and
        /// Y 20 to 240 (beardypig guncon2, README.md and guncon2.c).</summary>
        public static readonly GunCon2Calibration Default = new GunCon2Calibration(175, 720, 20, 240);

        /// <summary>The smallest span, in beam counts, a fitted axis may
        /// cover. Shots that land closer together than this came from the
        /// same spot, not from targets across the screen.</summary>
        public const int MinimumSpan = 16;

        public int MinX { get; }
        public int MaxX { get; }
        public int MinY { get; }
        public int MaxY { get; }

        public GunCon2Calibration(int minX, int maxX, int minY, int maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        public bool IsDefault => MinX == Default.MinX && MaxX == Default.MaxX
            && MinY == Default.MinY && MaxY == Default.MaxY;

        /// <summary>"minX,maxX,minY,maxY", the form the settings file keeps.</summary>
        public override string ToString() => string.Join(",",
            MinX.ToString(CultureInfo.InvariantCulture), MaxX.ToString(CultureInfo.InvariantCulture),
            MinY.ToString(CultureInfo.InvariantCulture), MaxY.ToString(CultureInfo.InvariantCulture));

        /// <summary>The stored window, or <see cref="Default"/> when the text
        /// is empty or does not describe a usable window.</summary>
        public static GunCon2Calibration Parse(string text) =>
            TryParse(text, out var calibration) ? calibration : Default;

        public static bool TryParse(string text, out GunCon2Calibration calibration)
        {
            calibration = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split(',');
            if (parts.Length != 4) return false;
            var values = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            var parsed = new GunCon2Calibration(values[0], values[1], values[2], values[3]);
            if (!parsed.IsUsable) return false;
            calibration = parsed;
            return true;
        }

        /// <summary>Both axes span at least <see cref="MinimumSpan"/> counts
        /// and every edge is a count the axes can carry (SDL clamps them to
        /// 0..32767).</summary>
        public bool IsUsable =>
            Math.Abs(MaxX - MinX) >= MinimumSpan && Math.Abs(MaxY - MinY) >= MinimumSpan
            && InRange(MinX) && InRange(MaxX) && InRange(MinY) && InRange(MaxY);

        private static bool InRange(int count) => count >= -32768 && count <= 32767;

        /// <summary>Fits the window from shots at known targets. Each target is
        /// a position on the picture as a fraction of its width (U) and height
        /// (V), 0 at the left and top edges. On each axis the fit is the
        /// least-squares line through (fraction, count), read at 0 and 1.
        /// False when the targets do not differ on an axis, or the fitted
        /// window is not usable.</summary>
        public static bool TryFit(ReadOnlySpan<(double U, double V)> targets, ReadOnlySpan<(int X, int Y)> shots,
            out GunCon2Calibration calibration)
        {
            calibration = null;
            if (targets.Length < 2 || targets.Length != shots.Length) return false;

            if (!TryFitAxis(targets, shots, horizontal: true, out double minX, out double maxX)) return false;
            if (!TryFitAxis(targets, shots, horizontal: false, out double minY, out double maxY)) return false;

            var fitted = new GunCon2Calibration(
                (int)Math.Round(minX), (int)Math.Round(maxX),
                (int)Math.Round(minY), (int)Math.Round(maxY));
            if (!fitted.IsUsable) return false;
            calibration = fitted;
            return true;
        }

        private static bool TryFitAxis(ReadOnlySpan<(double U, double V)> targets, ReadOnlySpan<(int X, int Y)> shots,
            bool horizontal, out double atStart, out double atEnd)
        {
            atStart = atEnd = 0;
            int n = targets.Length;
            double meanF = 0, meanC = 0;
            for (int i = 0; i < n; i++)
            {
                meanF += horizontal ? targets[i].U : targets[i].V;
                meanC += horizontal ? shots[i].X : shots[i].Y;
            }
            meanF /= n;
            meanC /= n;

            double sff = 0, sfc = 0;
            for (int i = 0; i < n; i++)
            {
                double f = (horizontal ? targets[i].U : targets[i].V) - meanF;
                double c = (horizontal ? shots[i].X : shots[i].Y) - meanC;
                sff += f * f;
                sfc += f * c;
            }
            if (sff < 1e-9) return false;

            double slope = sfc / sff;
            double intercept = meanC - slope * meanF;
            if (double.IsNaN(slope) || double.IsInfinity(slope)) return false;
            atStart = intercept;
            atEnd = intercept + slope;
            return true;
        }
    }
}
