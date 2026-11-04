using System;

namespace PadForge.Common.Input.Peripherals.HidFamilies
{
    /// <summary>
    /// The 90-byte Razer feature report as the shared worker's Razer families
    /// send it (#557): the ARGB controller's control interface and the
    /// Charging Pad Chroma, which OpenRGB drives through RazerController
    /// (RazerControllerDetect.cpp:164 and 465). <see cref="RazerDirectProtocol"/>
    /// builds the reports, the same builders the Razer worker sends.
    ///
    /// <para>A write is sent as OpenRGB's razer_usb_send sends it: the
    /// checksum the builder put in, then the feature report, under the lock
    /// third-party Razer tools share, with no reply read
    /// (RazerController.cpp:2091-2129, wait_for_response false at line 30).
    /// A read is openrazer's razer_send_payload: the request, the reply read
    /// back as feature report 00 after the accessory wait, 600
    /// microseconds, which Windows takes as 1 ms (openrazer-win
    /// openrazer_win/core/transport.py:37-39), and five tries 10 ms apart
    /// until the reply is this request's with status 02 or 01
    /// (razeraccessory_driver.c:31-44 and 49-86, razeraccessory_driver.h:38,
    /// <see cref="RazerDirectProtocol.Check"/>).</para>
    /// </summary>
    internal static class RazerFeatureLink
    {
        /// <summary>razer_send_payload's tries and the wait between them
        /// (razeraccessory_driver.c:55 and 84-85).</summary>
        internal const int Tries = 5;
        internal const int RetryMs = 10;

        /// <summary>The accessory reply wait as Windows waits it.</summary>
        internal const int ReplyWaitMs = 1;

        /// <summary>The lock. In the test host it is never taken
        /// (<see cref="RazerGuard.SharedMutexOffLimits"/>).</summary>
        private static readonly IRazerGuard Guard = new RazerGuard();

        /// <summary>Sends one write under the lock, waiting at most
        /// <paramref name="guardWaitMs"/> for it. Null when the lock stayed
        /// taken, else whether the report went out.</summary>
        internal static bool? Send(IHidReportChannel io, byte[] report, int guardWaitMs)
        {
            if (!Guard.Take(guardWaitMs)) return null;
            try { return io.SetFeature(report); }
            finally { Guard.Release(); }
        }

        /// <summary>Sends a request and reads its reply, as razer_send_payload
        /// does. The reply, or null when no try gave a reply of this request
        /// with status 02 or 01.</summary>
        internal static byte[] Query(IHidReportChannel io, Action<int> wait, byte[] request)
        {
            for (int attempt = 0; attempt < Tries; attempt++)
            {
                if (attempt > 0) wait?.Invoke(RetryMs);
                if (!Guard.Take(RazerGuard.CommandWaitMs)) continue;
                try
                {
                    if (!io.SetFeature(request)) continue;
                    wait?.Invoke(ReplyWaitMs);
                    var reply = new byte[RazerDirectProtocol.BufferLength];
                    if (!io.GetFeature(request[0], reply)) continue;
                    if (RazerDirectProtocol.Check(request, reply) == RazerReply.Accepted) return reply;
                }
                finally { Guard.Release(); }
            }
            return null;
        }
    }
}
