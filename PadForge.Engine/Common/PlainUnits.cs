using System;
using System.Globalization;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>The four directions of a hat, as a program names them. A
    /// diagonal holds two.</summary>
    [Flags]
    public enum HatDirections
    {
        None = 0,
        Up = 1,
        Right = 2,
        Down = 4,
        Left = 8,
    }

    /// <summary>
    /// The one converter between PadForge's raw values and the units a
    /// program reads and writes (#600): buttons true or false, sticks from
    /// -100 to 100 with 0 at rest, triggers and sliders from 0 to 100, hats
    /// in degrees or -1, and hat directions Up, Right, Down and Left. The
    /// Automation pipe, the three script libraries and the assistant tools
    /// go through it, so a value means the same everywhere.
    ///
    /// <para>Raw values are <see cref="CustomInputState"/>'s: axes and
    /// sliders 0 to 65535, hats in hundredths of a degree clockwise from up,
    /// or -1 at rest.</para>
    ///
    /// <para>An axis's kind follows its rest. One that rests within one step
    /// of the middle (32767 or 32768) is a stick: 0 is its rest, -100 is raw
    /// 0 and 100 is raw 65535, each half scaled on its own, so the rest is
    /// exactly 0 whichever of the two middles the device uses. Any other
    /// rest, an odd one included, makes a trigger or slider: 0 is raw 0 and
    /// 100 is raw 65535, whatever the rest. An axis whose rest is not known
    /// is a stick below <see cref="ProgramAxis.FirstSlider"/> and a slider
    /// from there, as a declaration without a rest makes them.</para>
    ///
    /// <para>Plain values are written with up to three decimals, which is
    /// finer than one raw step on every axis (100 / 65535 is 0.0015), so a
    /// raw value written as plain and read back is the same raw value.</para>
    /// </summary>
    public static class PlainUnits
    {
        public const int RawMax = 65535;
        public const int RawCenter = 32768;
        public const int HatRest = -1;
        /// <summary>A hat's raw values run below this: hundredths of a full
        /// turn.</summary>
        public const int HatTurn = 36000;
        public const double Full = 100.0;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // ── Axes ──

        /// <summary>Whether an axis that rests at <paramref name="rest"/> is a
        /// stick (centered) rather than a trigger or slider.</summary>
        public static bool IsCentered(int rest) => rest is RawCenter or RawCenter - 1;

        /// <summary>The rest of an axis whose rest is not known: the middle
        /// for an axis, 0 for a slider.</summary>
        public static int DefaultRest(int index) => index < ProgramAxis.FirstSlider ? RawCenter : 0;

        /// <summary>A raw axis value in plain units, for an axis that rests at
        /// <paramref name="rest"/>.</summary>
        public static double AxisToPlain(int raw, int rest)
        {
            raw = Math.Clamp(raw, 0, RawMax);
            if (!IsCentered(rest)) return raw * Full / RawMax;
            if (raw == rest) return 0;
            return raw < rest
                ? (raw - rest) * Full / rest
                : (raw - rest) * Full / (RawMax - rest);
        }

        /// <summary>A plain axis value as raw, for an axis that rests at
        /// <paramref name="rest"/>. Outside its range it is held to the
        /// nearer end: use <see cref="AxisAccepts"/> to refuse it
        /// instead.</summary>
        public static int AxisToRaw(double plain, int rest)
        {
            if (double.IsNaN(plain)) plain = 0;
            if (!IsCentered(rest))
                return (int)Math.Round(Math.Clamp(plain, 0, Full) * RawMax / Full, MidpointRounding.AwayFromZero);
            plain = Math.Clamp(plain, -Full, Full);
            double raw = plain < 0 ? rest + plain * rest / Full : rest + plain * (RawMax - rest) / Full;
            return Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 0, RawMax);
        }

        /// <summary>Whether a plain value lies in an axis's range: -100 to
        /// 100 for a stick, 0 to 100 for a trigger or slider.</summary>
        public static bool AxisAccepts(double plain, int rest)
            => !double.IsNaN(plain) && plain <= Full && plain >= (IsCentered(rest) ? -Full : 0);

        // ── Buttons ──

        public static bool ButtonToPlain(int raw) => raw != 0;

        public static int ButtonToRaw(bool down) => down ? 1 : 0;

        // ── Hats ──

        /// <summary>A raw hat as degrees, 0 to under 360 clockwise from up,
        /// or -1 at rest.</summary>
        public static double HatToPlain(int raw)
            => raw < 0 || raw >= HatTurn ? HatRest : raw / 100.0;

        /// <summary>Degrees as a raw hat. Any angle is taken round the turn
        /// (450 is 90). A negative value is the rest.</summary>
        public static int HatToRaw(double degrees)
        {
            if (double.IsNaN(degrees) || degrees < 0) return HatRest;
            int raw = (int)Math.Round(degrees * 100, MidpointRounding.AwayFromZero) % HatTurn;
            return raw;
        }

        /// <summary>The directions a hat holds: the nearest of the eight
        /// points of the compass, a diagonal holding its two
        /// directions.</summary>
        public static HatDirections HatToDirections(int raw)
        {
            if (raw < 0 || raw >= HatTurn) return HatDirections.None;
            int point = (int)Math.Round(raw / 4500.0, MidpointRounding.AwayFromZero) % 8;
            return point switch
            {
                0 => HatDirections.Up,
                1 => HatDirections.Up | HatDirections.Right,
                2 => HatDirections.Right,
                3 => HatDirections.Right | HatDirections.Down,
                4 => HatDirections.Down,
                5 => HatDirections.Down | HatDirections.Left,
                6 => HatDirections.Left,
                _ => HatDirections.Left | HatDirections.Up,
            };
        }

        /// <summary>Directions as a raw hat. Up and Down together cancel,
        /// as Left and Right do, and none is the rest. The poll thread calls
        /// it for a held or claimed direction on every pass, so it tests the
        /// bits: Enum.HasFlag boxes both of its values until the JIT
        /// optimizes the caller.</summary>
        public static int DirectionsToHat(HatDirections directions)
        {
            bool up = (directions & HatDirections.Up) != 0, down = (directions & HatDirections.Down) != 0;
            bool right = (directions & HatDirections.Right) != 0, left = (directions & HatDirections.Left) != 0;
            if (up && down) up = down = false;
            if (left && right) left = right = false;
            if (up) return right ? 4500 : left ? 31500 : 0;
            if (down) return right ? 13500 : left ? 22500 : 18000;
            if (right) return 9000;
            if (left) return 27000;
            return HatRest;
        }

        /// <summary>The words of a direction set, joined by +, as
        /// <c>Up+Right</c>. Empty for none.</summary>
        public static string DirectionsText(HatDirections directions)
        {
            var sb = new StringBuilder();
            foreach (var d in new[] { HatDirections.Up, HatDirections.Right, HatDirections.Down, HatDirections.Left })
            {
                if (!directions.HasFlag(d)) continue;
                if (sb.Length > 0) sb.Append('+');
                sb.Append(d.ToString());
            }
            return sb.ToString();
        }

        /// <summary>Reads direction words joined by + (<c>Up+Right</c>),
        /// without regard to case.</summary>
        public static bool TryParseDirections(string text, out HatDirections directions)
        {
            directions = HatDirections.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string part in text.Split('+'))
            {
                switch (part.Trim().ToLowerInvariant())
                {
                    case "up": directions |= HatDirections.Up; break;
                    case "right": directions |= HatDirections.Right; break;
                    case "down": directions |= HatDirections.Down; break;
                    case "left": directions |= HatDirections.Left; break;
                    default: directions = HatDirections.None; return false;
                }
            }
            return true;
        }

        // ── Text ──

        /// <summary>A plain value as a program reads it: up to three
        /// decimals, a point, no group separator, and no -0.</summary>
        public static string Format(double plain)
        {
            double rounded = Math.Round(plain, 3, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0;
            return rounded.ToString("0.###", Invariant);
        }

        /// <summary>Reads a percentage written as a number and a percent
        /// sign: <c>50%</c>, <c>-30%</c>, <c>12.5%</c>.</summary>
        public static bool TryParsePercent(string text, out double plain)
        {
            plain = 0;
            if (string.IsNullOrEmpty(text) || text[^1] != '%') return false;
            return TryNumber(text.Substring(0, text.Length - 1), out plain);
        }

        /// <summary>
        /// Reads an axis value on the Automation pipe: raw (a whole number 0
        /// to 65535) or plain (a number and a percent sign). Null when it
        /// reads, else the reason in words.
        /// </summary>
        public static string ReadAxis(string text, int rest, out int raw)
        {
            raw = 0;
            if (TryParsePercent(text, out double plain))
            {
                if (!AxisAccepts(plain, rest))
                    return IsCentered(rest) ? "a stick is -100% to 100%" : "a trigger or slider is 0% to 100%";
                raw = AxisToRaw(plain, rest);
                return null;
            }
            if (!int.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out raw) || raw < 0 || raw > RawMax)
                return "an axis is 0 to 65535, or a percent such as 50%";
            return null;
        }

        /// <summary>
        /// Reads a hat value on the Automation pipe. A whole number with no
        /// unit is raw, hundredths of a degree or -1, as it has always been.
        /// Plain forms carry their own mark: degrees end in <c>deg</c> or
        /// <c>°</c> (<c>90deg</c>), and directions are words joined by +
        /// (<c>Up+Right</c>). <c>rest</c> is the rest. Null when it reads,
        /// else the reason in words.
        /// </summary>
        public static string ReadHat(string text, out int raw)
        {
            raw = HatRest;
            const string needs = "a hat is -1, 0 to 35999 hundredths, degrees such as 90deg, or directions such as Up+Right";
            if (string.IsNullOrWhiteSpace(text)) return needs;
            string t = text.Trim();
            if (string.Equals(t, "rest", StringComparison.OrdinalIgnoreCase)) return null;
            string degrees = t.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ? t.Substring(0, t.Length - 3)
                : t.EndsWith("°", StringComparison.Ordinal) ? t.Substring(0, t.Length - 1) : null;
            if (degrees != null)
            {
                if (!TryNumber(degrees, out double d)) return needs;
                raw = d < 0 ? HatRest : HatToRaw(d);
                return null;
            }
            if (TryParseDirections(t, out var directions))
            {
                raw = DirectionsToHat(directions);
                return null;
            }
            if (!int.TryParse(t, NumberStyles.AllowLeadingSign, Invariant, out raw) || (raw != HatRest && (raw < 0 || raw >= HatTurn)))
            {
                raw = HatRest;
                return needs;
            }
            return null;
        }

        /// <summary>Reads a button on the Automation pipe: 0 or 1, or true or
        /// false.</summary>
        public static bool TryReadButton(string text, out bool down)
        {
            down = false;
            switch (text?.Trim().ToLowerInvariant())
            {
                case "1": case "true": down = true; return true;
                case "0": case "false": return true;
                default: return false;
            }
        }

        private static bool TryNumber(string text, out double value)
            => double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
