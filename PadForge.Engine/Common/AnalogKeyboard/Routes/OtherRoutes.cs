using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// Analog keyboards outside HallJoy's catalog and the Soup and
    /// AnalogSense families (issue #468): the Finalmouse Centerpiece Pro from
    /// LeiterConsulting's Soup fork, keyboards on the libhmk open firmware,
    /// the ROG Azoth 96 HE from HallJoy's disabled M901 diagnostic, and the
    /// Logitech PRO X TKL RAPID's single-key reports. Each route admits exact
    /// USB identities and one exact collection shape, so no other keyboard
    /// ever sees its writes, and no two of them match the same collection,
    /// so their order changes nothing.
    /// </summary>
    public static class OtherRoutes
    {
        /// <summary>The embedded key tables these routes read.</summary>
        public const string DataFile = "others.json";

        /// <summary>The four routes: Centerpiece Pro, libhmk, Azoth 96 HE,
        /// PRO X TKL RAPID.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All { get; } = new[]
        {
            // AnalogueKeyboard.cpp:206-217 on the Soup fork. Shared, as the
            // fork opens it (hwHid.cpp:171-175), so Finalmouse's XPanel can
            // run beside it and refresh the same read-only request.
            new AnalogKeyboardRoute
            {
                Id = "finalmouse-centerpiece-pro",
                Protocol = AnalogKeyboardProtocol.FinalmouseCenterpiecePro,
                Matches = CenterpieceProProtocol.Matches,
                CreateSession = _ => new CenterpieceProSession(),
                Name = _ => CenterpieceProProtocol.ModelName,
                Keys = _ => AnalogKeyboardData.KeysOf(CenterpieceProProtocol.Table()),
            },

            // libhmk's raw HID collection, VID 0xAB50 and the PIDs of the
            // keyboards in its repository. Exclusive, because an answer
            // carries only its command ID (commands.c:479-480) and another
            // program's answers would pass for this route's. The keys come
            // from the keyboard's own keymap during the handshake.
            new AnalogKeyboardRoute
            {
                Id = "libhmk",
                Protocol = AnalogKeyboardProtocol.Libhmk,
                Matches = LibhmkProtocol.Matches,
                CreateSession = _ => new LibhmkSession(),
                Exclusive = true,
            },

            // rog_azoth96he_diagnostic_backend.cpp:28-35 and 132-211: the
            // FF00 control collection, commanded, with the FFC0 event
            // collection of the same keyboard read beside it. Both shared,
            // as HallJoy opens them (backend:260-265).
            new AnalogKeyboardRoute
            {
                Id = "halljoy-rog-azoth-96-he",
                Protocol = AnalogKeyboardProtocol.RogAzoth96He,
                Matches = RogAzoth96HeProtocol.Matches,
                CreateSession = _ => new RogAzoth96HeSession(),
                Companion = RogAzoth96HeProtocol.FindEvents,
                Name = _ => RogAzoth96HeProtocol.ModelName,
                Keys = _ => AnalogKeyboardData.KeysOf(RogAzoth96HeProtocol.Table()),
            },

            // The HID++ long-report collection (logitech-analogue-report.cpp:3-6),
            // opened for reading only, as the gist sends nothing to it.
            new AnalogKeyboardRoute
            {
                Id = "logitech-pro-x-tkl-rapid",
                Protocol = AnalogKeyboardProtocol.LogitechRapid,
                Matches = LogitechRapidProtocol.Matches,
                CreateSession = _ => new LogitechRapidSession(),
                Writable = false,
                Name = _ => LogitechRapidProtocol.ModelName,
                Keys = _ => AnalogKeyboardData.KeysOf(LogitechRapidProtocol.Table()),
            },
        };
    }
}
