using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// HallJoy's Neo65 SONIC HE+, SteelSeries Apex Pro and MCHOSE Mix 87 III
    /// routes (issue #468), read from HallJoy's source (AGPL-3.0, commit
    /// 378f9fe). HallJoy registers them right after its Razer Tartarus route
    /// and in this order (native_analog_backends.def:45-48), the order
    /// <see cref="All"/> keeps. Each route admits exact USB identities and one
    /// exact collection shape, so no other keyboard ever sees its commands.
    /// </summary>
    public static class NeoApexMixRoutes
    {
        /// <summary>The embedded key tables these routes read.</summary>
        public const string DataFile = "neoapexmix.json";

        /// <summary>The three routes in HallJoy's priority order: Neo65,
        /// SteelSeries Apex, MCHOSE Mix 87.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[]
        {
            // neo65_backend.cpp:59-63: exact VID/PID, FF60:61, 33-byte input
            // and output reports. Opened exclusively so another configurator
            // cannot change ranges mid-session (neo65_backend.cpp:99-101).
            new AnalogKeyboardRoute
            {
                Id = "halljoy-neo65",
                Protocol = AnalogKeyboardProtocol.Neo65,
                Matches = Neo65Protocol.Matches,
                CreateSession = info => new Neo65Session(info.ProductId),
                Exclusive = true,
                // Exchanges and frames have 100 ms, a key reads 0 100 ms after
                // its frame, and the worker reconnects every second
                // (neo65_backend.cpp:24, 89, 130, 154, 188).
                WriteTimeoutMs = 100,
                StaleAfterMs = 100,
                StartRetryMs = 1000,
                Name = _ => Neo65Protocol.ModelName,
                Keys = info => AnalogKeyboardData.KeysOf(Neo65Protocol.Table(info.ProductId)),
            },

            // steelseries_apex_protocol.h:8 and 12-14: three product IDs and
            // FFC0:1 with 65/65/643-byte reports. Exclusive, because the
            // answers carry no echo (steelseries_apex_backend.cpp:86-87, 134).
            new AnalogKeyboardRoute
            {
                Id = "halljoy-steelseries-apex",
                Protocol = AnalogKeyboardProtocol.SteelSeriesApex,
                Matches = SteelSeriesApexProtocol.Matches,
                CreateSession = info => new SteelSeriesApexSession(info.ProductId),
                Exclusive = true,
                // The same 100 ms budgets and 1 s reconnect
                // (steelseries_apex_backend.cpp:25, 104, 178, 203, 238).
                WriteTimeoutMs = 100,
                StaleAfterMs = 100,
                StartRetryMs = 1000,
                Name = info => SteelSeriesApexProtocol.ModelName(info.ProductId),
                Keys = _ => AnalogKeyboardData.KeysOf(SteelSeriesApexProtocol.BindableTable()),
            },

            // mchose_mix87_backend.cpp:67-74 and 250-253: 3837:300D, usage
            // 0001:0000, 65-byte unnumbered reports, and exactly one such
            // collection. Shared, so the vendor's M HUB can run beside it
            // (mchose_mix87_backend.cpp:91). The 86 keys are read from the
            // keyboard's flash during the handshake, so none are listed here.
            new AnalogKeyboardRoute
            {
                Id = "halljoy-mchose-mix87",
                Protocol = AnalogKeyboardProtocol.MchoseMix87,
                Matches = MchoseMix87Protocol.Matches,
                CreateSession = _ => new MchoseMix87Session(),
                // A write waits 300 ms (mchose_mix87_backend.cpp:101), the
                // worker retries every 5 s (:254), and a stop waits up to 25 s
                // so the flag's cleanup write can finish (:267).
                WriteTimeoutMs = MchoseMix87Session.WriteTimeoutMs,
                StartRetryMs = 5000,
                StopTimeoutMs = 25000,
                Name = _ => MchoseMix87Protocol.ModelName,
            },
        };
    }
}
