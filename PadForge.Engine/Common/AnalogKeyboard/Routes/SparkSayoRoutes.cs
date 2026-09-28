using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The last two routes of HallJoy's native catalog (issue #468), read
    /// from its source (AGPL-3.0, commit 378f9fe8): SparkLink V2, the JingTai
    /// V2 row protocol of the IROK, CAROTMAS and EWEADN boards, then the
    /// SayoDevice depth protocol. HallJoy registers them last and starts its
    /// catalog in order, so every other native route has had its turn at a
    /// collection first (native_analog_backends.def:1-3 and :49-50,
    /// native_analog_backend_registry.cpp:116-136).
    ///
    /// <para>Both routes only read. Neither sends anything that changes the
    /// keyboard, and neither sends anything when it stops
    /// (backend_sparklink.inc:1465-1563, backend_sayo.inc:653-755).</para>
    /// </summary>
    public static class SparkSayoRoutes
    {
        /// <summary>The embedded tables both routes read.</summary>
        public const string DataFile = "sparksayo.json";

        /// <summary>HallJoy's descriptor ids, "sparklink" and "sayo-depth"
        /// (backend.cpp:4534 and :4569), under the port's prefix.</summary>
        public const string SparkLinkId = "halljoy-sparklink";
        public const string SayoId = "halljoy-sayo-depth";

        /// <summary>Both routes in HallJoy's priority order, SparkLink first.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[]
        {
            new AnalogKeyboardRoute
            {
                Id = SparkLinkId,
                Protocol = AnalogKeyboardProtocol.SparkLink,
                Matches = SparkLinkProtocol.Matches,
                CreateSession = info => new SparkLinkSession(info.ProductId),
                // Read and write, shared, 64 input buffers: the data handle
                // HallJoy opens (backend_sparklink.inc:1027-1049 and :1086).
                // It refuses a candidate it can only read (:1674-1680), which
                // a failed read/write open here does too.
                Writable = true,
                Exclusive = false,
                InputBuffers = SparkLinkProtocol.InputBuffers,
                // Writes wait 250 ms (backend_sparklink.inc:656, 663) and the
                // route reconnects every 2 s (kSparkReconnectIntervalMs, :18).
                WriteTimeoutMs = 250,
                StartRetryMs = 2000,
                ReconnectMs = 2000,
                Name = info => SparkLinkProtocol.ModelName(info.ProductId),
                // The key map comes from the keyboard's base layer at Start.
                Keys = null,
            },
            new AnalogKeyboardRoute
            {
                Id = SayoId,
                Protocol = AnalogKeyboardProtocol.Sayo,
                Matches = SayoDepthProtocol.Matches,
                CreateSession = info => new SayoDepthSession(info.ProductId),
                // Read and write, shared, 64 input buffers (backend_sayo.inc:532-554
                // and :571), and only a writable handle is kept (:643-644).
                Writable = true,
                Exclusive = false,
                InputBuffers = SayoDepthProtocol.InputBuffers,
                // Writes wait 20 to 30 ms (backend_sayo.inc:212, 266, 282), a
                // depth reads 0 160 ms after its packet (kSayoDepthFreshMs,
                // :20), and the route reconnects every 2 s (:17).
                WriteTimeoutMs = 30,
                StaleAfterMs = 160,
                StartRetryMs = 2000,
                ReconnectMs = 2000,
                Name = info => SayoDepthProtocol.ModelName(info.ProductId),
                // Manual layout Z, X and C until Start reads the configuration.
                Keys = info => (int[])SayoDepthProtocol.Factory.Clone(),
            },
        };
    }
}
