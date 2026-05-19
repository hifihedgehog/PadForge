using System;
using System.IO;
using System.Text;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Temporary diagnostic probe for the impulse-trigger pipeline.
    /// Logs every <c>OutputReceived</c> event PadForge gets from HM, with
    /// the raw byte payload and length. Driven by the global CLAUDE.md
    /// driver-investigation rules — raw-byte dumps at the driver's
    /// reception layer are ground truth, and every "no impulse trigger
    /// data arriving" claim needs a same-window positive control (the
    /// regular dual-rumble writes that DO arrive while impulse-trigger
    /// writes appear to vanish).
    ///
    /// Writes to %TEMP%\padforge-impulse-trigger-probe.log. Remove this
    /// file (the call site in HMaestroVirtualController + this whole
    /// class) once the impulse-trigger feature is confirmed working
    /// end-to-end.
    /// </summary>
    internal static class ImpulseTriggerProbe
    {
        private static readonly string s_path =
            Path.Combine(Path.GetTempPath(), "padforge-impulse-trigger-probe.log");
        private static readonly object s_lock = new();

        public static void Log(int padIndex, HIDMaestro.HMProfile profile, HIDMaestro.HMOutputSource source, ReadOnlySpan<byte> data)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
                sb.Append(" pad=").Append(padIndex);
                sb.Append(" src=").Append(source);
                sb.Append(" vid=").Append(profile?.VendorId.ToString("X4") ?? "----");
                sb.Append(" pid=").Append(profile?.ProductId.ToString("X4") ?? "----");
                sb.Append(" len=").Append(data.Length);
                sb.Append(" hex=");
                // Hex-dump entire payload so we see whether bytes 4/5 are
                // present (impulse triggers) and what bytes 0..3 look
                // like (header + main motors).
                for (int i = 0; i < data.Length; i++)
                {
                    sb.Append(data[i].ToString("X2"));
                    if (i + 1 < data.Length) sb.Append(' ');
                }
                if (data.Length >= 7)
                {
                    sb.Append("  [main L=").Append(data[2])
                      .Append(" R=").Append(data[3])
                      .Append(" trigL=").Append(data[4])
                      .Append(" trigR=").Append(data[5]).Append(']');
                }
                else if (data.Length >= 5)
                {
                    sb.Append("  [main L=").Append(data[2])
                      .Append(" R=").Append(data[3])
                      .Append(" — short, no trigger bytes]");
                }
                sb.Append('\n');

                lock (s_lock)
                {
                    File.AppendAllText(s_path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
