using System;
using System.Collections.Concurrent;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    internal static class HidOutputDiagnostics
    {
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> Last = new(StringComparer.OrdinalIgnoreCase);

        // At most one line per path/phase per five seconds, including failures.
        internal static bool ShouldLog(string path, string phase)
        {
            var phases = Last.GetOrAdd(path ?? "", static _ => new ConcurrentDictionary<string, long>());
            long now = Environment.TickCount64;
            if (phases.TryGetValue(phase, out long previous) && now - previous < 5000) return false;
            phases[phase] = now;
            return true;
        }

        internal static void Result(string path, string phase, byte[] report, int logicalLength,
            int error, uint transferred, long started)
        {
            if (!ShouldLog(path, phase)) return;
            SdlDiagLog.WriteLine($"[RawHidOutput] phase={phase} path={path} "
                + $"reportId=0x{report[0]:X2} logicalLength={logicalLength} outputLength={report.Length} "
                + $"win32={error} transferred={transferred} elapsedMs={Environment.TickCount64 - started}");
        }
    }
}
