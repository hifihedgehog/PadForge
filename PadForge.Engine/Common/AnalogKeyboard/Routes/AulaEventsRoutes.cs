using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// HallJoy's two AULA routes whose keyboards push per-key travel events
    /// after a one-time RAM switch (issue #468): the AULA MINI 60 HE family on
    /// the HFD channel, and the W669 firmware family (AULA WIN 60 HE, WIN 68
    /// HE, KP-TE153, Redragon K673 and K617) on its opcode 21 event stream.
    ///
    /// <para>Ported from HallJoy's source (AGPL-3.0, commit 378f9fe).
    /// Citations name files under its src/HallJoyProject/HallJoy folder, tests
    /// under src/HallJoyProject/tests and documents under docs.</para>
    ///
    /// <para>HallJoy's catalog ranks the MINI 60 second, right after ATTACK
    /// SHARK, and W669 after IROK NA87 (native_analog_backends.def:14, 17,
    /// 29, 40).
    /// <see cref="All"/> keeps the two in that relative order.</para>
    /// </summary>
    public static class AulaEventsRoutes
    {
        /// <summary>The key tables and model catalogs both routes read.</summary>
        public const string DataFile = "aulaevents.json";

        public const string Mini60Id = "halljoy-aula-mini60";
        public const string W669Id = "halljoy-aula-w669";

        /// <summary>Both routes in HallJoy's priority order.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[]
        {
            new AnalogKeyboardRoute
            {
                Id = Mini60Id,
                Protocol = AnalogKeyboardProtocol.AulaMini60,
                Matches = AulaMini60Protocol.Matches,
                CreateSession = info => new AulaMini60Session(info.ProductId),
                // Shared read-write handle with 512 input buffers
                // (aula_mini60_diagnostic.cpp:116-118).
                Writable = true,
                Exclusive = false,
                InputBuffers = AulaMini60Protocol.InputBuffers,
                // Writes wait 250 ms (aula_mini60_diagnostic.cpp:124-127),
                // and a keyboard that fails its connection is tried again
                // every 3 s, HallJoy's reconnect cadence.
                WriteTimeoutMs = 250,
                StartRetryMs = 3000,
                Name = info => AulaMini60Protocol.Model(info.ProductId)?.Name,
                Keys = info => AnalogKeyboardData.KeysOf(AulaMini60Protocol.FactoryTable),
            },
            new AnalogKeyboardRoute
            {
                Id = W669Id,
                Protocol = AnalogKeyboardProtocol.AulaW669,
                Matches = AulaW669Protocol.Matches,
                CreateSession = info => new AulaW669Session(info.ProductKey),
                // HallJoy tries a shared handle first and keeps it when the
                // proof passes (aula_w669_backend.cpp:390-414). PadForge opens
                // shared only, with HallJoy's 256 input buffers (:237).
                Writable = true,
                Exclusive = false,
                InputBuffers = AulaW669Protocol.InputBuffers,
                // WriteFile waits 120 ms (kIoTimeoutMs, aula_w669_backend.cpp:40).
                // HallJoy looks for these keyboards once per run, so a failed
                // proof is not retried while the keyboard stays plugged in.
                WriteTimeoutMs = 120,
            },
        };
    }
}
