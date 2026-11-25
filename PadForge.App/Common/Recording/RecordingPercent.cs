using System;
using System.Globalization;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Recording;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// The review's reading aid (#600): an input or output line's sticks,
    /// triggers and sliders in percent, shown beside the line and never in
    /// the file, which keeps the exact values. A stick reads -100 to 100 with
    /// 0 at rest and a trigger or slider 0 to 100, in the plain units programs read
    /// (<see cref="PlainUnits"/>).
    /// </summary>
    internal static class RecordingPercent
    {
        /// <summary>The percent words of a line, such as "LeftX 22%,
        /// RightTrigger 100%", or an empty string for a line with no axis.</summary>
        internal static string Of(string line)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            if (!RecordingCodec.TrySplitEvent(line, out _, out char layer, out _, out var payload)) return string.Empty;
            if (layer is not (RecordingCodec.LayerInput or RecordingCodec.LayerOutput)) return string.Empty;
            var sb = new StringBuilder();
            while (!payload.IsEmpty)
            {
                int space = payload.IndexOf(' ');
                var token = space < 0 ? payload : payload.Slice(0, space);
                payload = space < 0 ? default : payload.Slice(space + 1);
                int eq = token.IndexOf('=');
                if (eq <= 0 || !char.IsAsciiLetterUpper(token[0])) continue;
                var name = token.Slice(0, eq);
                if (!int.TryParse(token.Slice(eq + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)) continue;
                double? plain = layer == RecordingCodec.LayerInput ? InputPercent(name, value) : OutputPercent(name, value);
                if (plain == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(' ').Append(Math.Round(plain.Value).ToString("0", CultureInfo.CurrentCulture)).Append('%');
            }
            return sb.ToString();
        }

        private static double? InputPercent(ReadOnlySpan<char> name, int raw)
        {
            if (!RecordingControlNames.TryInput(name, out var field, out int index)) return null;
            if (field == RecordingField.Slider) return PlainUnits.AxisToPlain(raw, 0);
            if (field != RecordingField.Axis) return null;
            // A gamepad's triggers rest at 0, every other axis at its middle.
            bool trigger = name.SequenceEqual("LeftTrigger") || name.SequenceEqual("RightTrigger");
            return PlainUnits.AxisToPlain(raw, trigger ? 0 : PlainUnits.DefaultRest(index));
        }

        private static double? OutputPercent(ReadOnlySpan<char> name, int raw)
        {
            if (name.SequenceEqual("LeftX") || name.SequenceEqual("LeftY") || name.SequenceEqual("RightX") || name.SequenceEqual("RightY"))
                return raw < 0 ? raw * 100.0 / 32768 : raw * 100.0 / 32767;
            if (name.SequenceEqual("LeftTrigger") || name.SequenceEqual("RightTrigger"))
                return raw * 100.0 / 65535;
            return null;
        }
    }
}
