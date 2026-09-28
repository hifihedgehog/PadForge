using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The Redragon M68, E-YOOSO HZ-68 and Redragon K712 RGB-M on the
    /// 0416:7372 controller (issue #468), KeyAxis's reader (RigZeeel, MIT):
    /// its PROTOCOL.md and app.py. HallJoy's IROK NA87 route reads the same
    /// controller only for the two board identities it knows, and this route
    /// takes the collection when that route's identity check turns it down.
    ///
    /// <para>The vendor collection is usage page FF1B (PROTOCOL.md:53-57).
    /// Every command is output report 0x01 with 63 payload bytes
    /// (PROTOCOL.md:61-76). Arming sends <c>21 00 00 00 18 02</c> and the
    /// 17 per-key actuation bytes the vendor tool sends, which go to volatile
    /// state (PROTOCOL.md:90-100, app.py:116-120), and disarming sends
    /// <c>21 00 00 00 18 03</c> (PROTOCOL.md:102-111). KeyAxis sends nothing
    /// else, and neither does this route. The keyboard then pushes a frame
    /// whenever a key's depth changes: <c>01 21 .. .. .. 03 .. row col
    /// depth</c>, depth 0 to 40 in 0.1 mm, and frame type 01 is an
    /// acknowledgement (PROTOCOL.md:116-143, app.py:437-451).</para>
    ///
    /// <para>No source maps the matrix to key legends: KeyAxis learns each
    /// key's row and column by asking the user to press it
    /// (PROTOCOL.md:135-137, app.py:237-285). A key is therefore published by
    /// its matrix position, <see cref="AnalogKeyCodes.PositionBase"/> plus row
    /// * 32 + column, and the picker lists each position once the keyboard
    /// has reported it.</para>
    /// </summary>
    public static class KeyAxisRoute
    {
        public const string Id = "keyaxis-0416-7372";
        public const ushort VendorId = 0x0416;
        public const ushort ProductId = 0x7372;
        public const ushort UsagePage = 0xFF1B;
        public const byte ReportId = 0x01;
        public const byte Command = 0x21;
        public const byte TravelFrame = 0x03;
        public const int FullDepth = 40;

        /// <summary>The arm payload, report ID first (app.py:116-120).</summary>
        public static byte[] ArmRequest() => new byte[]
        {
            ReportId,
            0x21, 0x00, 0x00, 0x00, 0x18, 0x02,
            0x3E, 0x26, 0x3E, 0x1E, 0x1E, 0x1E, 0x3E, 0x1E, 0x1E,
            0x3E, 0x1E, 0x3E, 0x2E, 0x10, 0x2E, 0x30, 0x3E,
        };

        /// <summary>The disarm payload, report ID first (app.py:121).</summary>
        public static byte[] DisarmRequest() => new byte[] { ReportId, 0x21, 0x00, 0x00, 0x00, 0x18, 0x03 };

        public static readonly AnalogKeyboardRoute Route = new()
        {
            Id = Id,
            Protocol = AnalogKeyboardProtocol.KeyAxis,
            Matches = Matches,
            CreateSession = _ => new KeyAxisSession(),
            // KeyAxis's name for the board when its product string is empty.
            Name = info => string.IsNullOrWhiteSpace(info.ProductString) ? "Redragon M68 / E-YOOSO HZ-68" : null,
        };

        /// <summary>0416:7372 on usage page FF1B with 64-byte output reports
        /// numbered 1, the interface KeyAxis selects (PROTOCOL.md:53-57).</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null && info.VendorId == VendorId && info.ProductId == ProductId
               && info.UsagePage == UsagePage && info.OutputReportLength >= 64
               && info.HasOutputReport != null && info.HasOutputReport(ReportId);

        /// <summary>A travel frame's row, column and depth, depth capped at
        /// 40 (app.py:437-451). False for an acknowledgement or any other
        /// report.</summary>
        public static bool TryParse(ReadOnlySpan<byte> raw, out int row, out int column, out int depth)
        {
            row = column = depth = 0;
            if (raw.Length < 10 || raw[0] != ReportId || raw[1] != Command || raw[5] != TravelFrame) return false;
            row = raw[7];
            column = raw[8];
            depth = Math.Min((int)raw[9], FullDepth);
            return true;
        }

        /// <summary>The code a matrix position publishes under, 0 when the
        /// position falls outside the 8 by 32 grid the code range holds.</summary>
        public static int PositionCode(int row, int column)
            => row >= 0 && row < 8 && column >= 0 && column < 32
                ? AnalogKeyCodes.PositionBase + row * 32 + column
                : 0;
    }

    /// <summary>
    /// The KeyAxis session: Start arms the stream, each pass waits for one
    /// frame, and Stop disarms, as KeyAxis does on every stop, disconnect and
    /// exit (PROTOCOL.md:110-111). Each frame changes one key, so the others
    /// keep their depths.
    /// </summary>
    public sealed class KeyAxisSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait, so the reader sees a stop request.</summary>
        public const int WaitMs = 250;

        private bool _armed;

        public override bool Start(IAnalogKeyboardTransport io)
        {
            // KeyAxis treats a write that does not go out whole as the wrong
            // interface (PROTOCOL.md:75-76). A timed-out write may still have
            // reached the keyboard, so the stream is disarmed then too.
            _armed = true;
            if (io.Send(KeyAxisRoute.ArmRequest())) return true;
            Stop(io);
            return false;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            if (!KeyAxisRoute.TryParse(Buffer.AsSpan(0, n), out int row, out int column, out int depth))
                return AnalogPollResult.Idle;
            int code = KeyAxisRoute.PositionCode(row, column);
            if (code == 0) return AnalogPollResult.Idle;
            output.Set(code, depth / (float)KeyAxisRoute.FullDepth);
            return AnalogPollResult.Ok;
        }

        public override void Stop(IAnalogKeyboardTransport io)
        {
            if (!_armed) return;
            _armed = false;
            try { io.Send(KeyAxisRoute.DisarmRequest()); } catch { }
        }
    }
}
