using System;
using System.Collections.Generic;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// HallJoy's MADLIONS MAD 68 Pro R, ATK Hex80 and IROK M484 routes
    /// (issue #468), read from HallJoy's source (AGPL-3.0) at commit 378f9fe8.
    /// File:line citations in this group are relative to
    /// src/HallJoyProject/HallJoy/ in that tree.
    ///
    /// <para>HallJoy's release catalog registers Mad68ProR fourth and Hex80
    /// fifth, after Shark, Mini60 and KeychronOnboard
    /// (native_analog_backends.def:13-23, tools/build.ps1:614-615), and
    /// IrokNa87 ninth, after AulaWin60He and before AulaW669
    /// (native_analog_backends.def:27-40), so <see cref="All"/> keeps that
    /// order. The IROK NA87 Mag and the AJAZZ AK820 MAX RGB share
    /// one USB identity and one HallJoy backend whose session picks the mode
    /// from the firmware identity (irok_na87_backend.cpp:884-907). They are one
    /// route here for the same reason, so the AJAZZ keyboard runs under the
    /// <see cref="AnalogKeyboardProtocol.IrokNa87"/> route and names itself
    /// after its handshake.</para>
    ///
    /// <para>The existing Soup Madlions route (VID 373B, usage page 0xFF60,
    /// PIDs 1053 to 1056, 105D, 1058 to 105A, 105C and 10A7) and the MAD 68
    /// Pro R route share a vendor. The MAD 68 Pro R route admits only what
    /// HallJoy's routing admits: a 65-byte vendor collection on usage
    /// 0001:0000 or interface 1, whose PID is 1109 or whose strings name the
    /// MAD68 family. A Soup PID reaches its handshake only in that case, and
    /// the handshake is HallJoy's A9 probe, which a VIA collection does not
    /// acknowledge.</para>
    /// </summary>
    public static class MadlionsRoutes
    {
        /// <summary>The data file this group's tables live in.</summary>
        public const string DataFile = "madlions.json";

        private static readonly Lazy<AnalogKeyboardRoute[]> _all = new(Build);

        /// <summary>The group's routes in HallJoy's priority order:
        /// MAD 68 Pro R, then ATK Hex80, then the IROK M484 route.</summary>
        public static IReadOnlyList<AnalogKeyboardRoute> All => _all.Value;

        private static AnalogKeyboardRoute[] Build() => new[]
        {
            new AnalogKeyboardRoute
            {
                Id = "halljoy-mad68-a0",
                Protocol = AnalogKeyboardProtocol.Mad68ProR,
                Matches = Mad68ProRProtocol.Matches,
                CreateSession = info => new Mad68ProRSession(info.ProductId, info.VersionNumber),
                // Shared read and write, 256 input buffers (mad68pr_backend.cpp:469-474, 716).
                Writable = true,
                Exclusive = false,
                InputBuffers = 256,
                // HallJoy proves a collection once, when routing is prepared
                // (mad68pr_backend.cpp:2257-2295), and its worker reopens only
                // the collections it claimed, 250 ms after every attempt
                // (EnumerateCandidates(true), :567-574, 2169, 2201).
                ProbeOnce = true,
                StartRetryMs = 250,
                ReconnectMs = 250,
                // Stop's closing A9 writes get the time HallJoy's stop gives
                // its worker (kStopJoinTimeoutMs, :60, 2395), plus the write
                // timeouts of up to three A9 writes and the pass in flight.
                StopTimeoutMs = 4500,
                Name = info => Mad68ProRProtocol.ModelNameFor(info.ProductId),
                Keys = _ => Mad68ProRProtocol.KeyOrder(),
            },
            new AnalogKeyboardRoute
            {
                Id = "halljoy-hex80-0x96",
                Protocol = AnalogKeyboardProtocol.AtkHex80,
                Matches = AtkHex80Protocol.Matches,
                CreateSession = _ => new AtkHex80Session(),
                // Shared read and write, 128 input buffers (hex80_backend.cpp:362-372).
                Writable = true,
                Exclusive = false,
                InputBuffers = 128,
                // Writes wait 40 ms (hex80_backend.cpp:37, 374-381). HallJoy
                // proves a collection once, when routing is prepared
                // (:694-728), and its worker reopens only the collections it
                // claimed, 250 ms after every attempt (EnumerateCandidates(true),
                // :242-260, 613, 643).
                WriteTimeoutMs = 40,
                ProbeOnce = true,
                StartRetryMs = 250,
                ReconnectMs = 250,
                Name = _ => AtkHex80Protocol.ModelName,
                Keys = _ => AtkHex80Protocol.KeyOrder(),
            },
            new AnalogKeyboardRoute
            {
                Id = "halljoy-irok-na87-m484",
                Protocol = AnalogKeyboardProtocol.IrokNa87,
                Matches = IrokM484Protocol.Matches,
                CreateSession = _ => new IrokM484Session(),
                // Shared read and write, 256 input buffers. The release never
                // opens exclusively (irok_na87_backend.cpp:260-272, 404, 488, 828).
                Writable = true,
                Exclusive = false,
                InputBuffers = 256,
                // Writes wait 120 ms (irok_na87_backend.cpp:47, 274-291). The
                // worker tries again 200 ms after a session and 1 s after an
                // attempt that ran none (kReconnectMs, :49, 918). A handshake
                // that proved the NA87 or AJAZZ identity keeps the keyboard
                // for this route, so KeyAxis never arms it after a slow reply.
                WriteTimeoutMs = 120,
                StartRetryMs = 1000,
                ReconnectMs = 200,
                // The metadata cannot tell the NA87 from the AJAZZ, so the row
                // keeps the product string until the handshake names the model.
                Name = _ => null,
                Keys = _ => IrokM484Protocol.FactoryKeyOrder(),
            },
        };
    }
}
