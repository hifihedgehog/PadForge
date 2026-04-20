using System;
using System.Text;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // ─────────────────────────────────────────────
        //  Diagnostic call-rate reporter
        //
        //  Samples the XInputHook per-slot counters and each
        //  HMaestroVirtualController's SubmitState counter every 5 seconds,
        //  logging per-second rates to C:\PadForge\call-rate.log.
        //
        //  Purpose: quantify PadForge's load on WUDFHost during the
        //  two-xinputhid-virtual saturation scenario. Standalone
        //  HIDMaestroTest topped out at ~3% CPU across all test patterns
        //  (see hifihedgehog/HIDMaestro#3), so the saturation is driven by
        //  something PadForge adds. Likely candidates:
        //   - SDL3's XInput backend polling all 4 slots at polling rate
        //   - SubmitState at a higher effective rate than expected
        //   - Some mismatch between nominal and actual rates
        //
        //  This reporter tells us whether the observed in-process
        //  XInput traffic and submission rate match expectations, and
        //  makes the difference between single-virtual and dual-virtual
        //  workloads measurable.
        // ─────────────────────────────────────────────

        private const int CallRateSampleIntervalCycles = 5000;
        private int _callRateReporterCycles;

        private void MaybeReportCallRates()
        {
            if (++_callRateReporterCycles < CallRateSampleIntervalCycles) return;
            _callRateReporterCycles = 0;

            if (!XInputHook.IsInstalled) return;

            long[] hookCounts;
            try { hookCounts = XInputHook.SampleAndResetCallCounts(); }
            catch { return; }

            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] 5s sample | XInputGetState: ");
            for (int s = 0; s < 4; s++)
            {
                long masked = hookCounts[s];
                long forwarded = hookCounts[s + 4];
                sb.Append($"s{s}(m={masked / 5}/s,f={forwarded / 5}/s) ");
            }
            sb.Append("| SubmitState: ");
            int msPadCount = 0;
            for (int i = 0; i < MaxPads; i++)
            {
                if (_virtualControllers[i] is HMaestroVirtualController hm
                    && hm.Type == VirtualControllerType.Microsoft)
                {
                    long calls = hm.SampleAndResetSubmitStateCalls();
                    sb.Append($"pad{i}={calls / 5}/s ");
                    msPadCount++;
                }
            }
            if (msPadCount == 0) sb.Append("(no Microsoft virtuals)");
            sb.Append($" | mask=0x{XInputHook.IgnoreSlotMask:X}");

            try
            {
                System.IO.File.AppendAllText(@"C:\PadForge\call-rate.log", sb.ToString() + "\n");
            }
            catch { }
        }
    }
}
