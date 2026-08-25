using System.Text;
using System.Windows.Input;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>Human-readable descriptions of learned handheld buttons
    /// (issue #343) for the Devices preview and the Learn dialog.</summary>
    internal static class HandheldKeyNames
    {
        /// <summary>"Ctrl + Win + F17", "Left Click + X2", in first-down order.</summary>
        public static string Describe(int[] keys)
        {
            if (keys == null || keys.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (int code in keys)
            {
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append(Name(code));
            }
            return sb.ToString();
        }

        public static string Name(int code)
        {
            if (HandheldChordDefinition.IsMouse(code))
            {
                return (code - HandheldChordDefinition.MouseCode) switch
                {
                    0 => "Left Click",
                    1 => "Middle Click",
                    2 => "Right Click",
                    3 => "X1",
                    4 => "X2",
                    _ => "Mouse " + (code - HandheldChordDefinition.MouseCode),
                };
            }
            switch (code)
            {
                case 0xA0: return "Shift";
                case 0xA1: return "Right Shift";
                case 0xA2: return "Ctrl";
                case 0xA3: return "Right Ctrl";
                case 0xA4: return "Alt";
                case 0xA5: return "Right Alt";
                case 0x5B: return "Win";
                case 0x5C: return "Right Win";
                case 0xFF: return "Reserved";
            }
            try
            {
                var key = KeyInterop.KeyFromVirtualKey(code);
                if (key != Key.None) return key.ToString();
            }
            catch { }
            return $"VK 0x{code:X2}";
        }

        /// <summary>Both delivery paths of an entry on one line.</summary>
        public static string DescribeEntry(HandheldButtonRegistry.Entry e)
        {
            if (e == null) return string.Empty;
            var sb = new StringBuilder();
            if (e.HasChord) sb.Append(Describe(e.Keys));
            if (e.HasReport)
            {
                if (sb.Length > 0) sb.Append("  |  ");
                sb.Append(e.Collection).Append(' ');
                sb.Append(e.ValueKind == VendorButtonKind.Bit
                    ? $"[{e.ReportId:X2}] byte {e.ByteIndex} bit 0x{e.Mask:X2}"
                    : $"[{e.ReportId:X2}] byte {e.ByteIndex} = 0x{e.Value:X2}");
            }
            if (e.HasWmi)
            {
                if (sb.Length > 0) sb.Append("  |  ");
                sb.Append(e.WmiClass).Append(' ').Append(e.WmiProperty).Append(" = ").Append(e.WmiValue);
            }
            return sb.ToString();
        }
    }
}
