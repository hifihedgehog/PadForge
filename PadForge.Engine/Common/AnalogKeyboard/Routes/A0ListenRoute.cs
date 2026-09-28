using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// Keyboards that push the 0xA0 key event on their own, found by listening
    /// (issue #468): HallEffectAnalogMapper's reader (Ricards Mironovs, MIT),
    /// written against an MCHOSE Jet 75, whose USB IDs no public source
    /// records. It hardcodes no vendor or product ID. It opens the HID
    /// collections, reads, and keeps the one whose reports start with 0xA0
    /// (HallAnalogMapper.py:652-678 and 741-788), then reads each event's key
    /// from byte 3 and a big-endian value from bytes 4 and 5, counting from
    /// the 0xA0 (HallAnalogMapper.py:809-813). It sends nothing.
    ///
    /// <para>This route does the same and sends nothing either. It opens a
    /// collection read-only and shared, and the row appears only once a valid
    /// event arrived, so a device that never sends one never shows up. It
    /// listens only where no other route applies: an unnumbered 64-byte
    /// input report on usage page 1, usage 0, the shape of the 0xA0 event on
    /// the keyboards that send it (NuPhy, the Madlions boards AnalogKeys
    /// reads, MCHOSE), and it comes after every other route, so a keyboard
    /// with a route of its own, including one that enables the stream with a
    /// command, is never read here while that route may still claim it.</para>
    ///
    /// <para>Scale: HallEffectAnalogMapper's defaults, a value of 30 or less
    /// is rest and 1600 is the bottom (HallAnalogMapper.py:113-119 and
    /// hall_config defaults at 176). The same field is the one HallJoy's MAD
    /// 68 Pro R route reads against the same 1600 ceiling
    /// (mad68pr_protocol.h:9-26).</para>
    /// </summary>
    public static class A0ListenRoute
    {
        public const string Id = "a0-listen";
        public const byte EventType = 0xA0;
        public const int RestValue = 30;
        public const float FullScale = 1600f;

        /// <summary>Windows length of the unnumbered 64-byte report: the
        /// report ID byte and 64 data bytes.</summary>
        public const int ReportLength = 65;

        public static readonly AnalogKeyboardRoute Route = new()
        {
            Id = Id,
            Protocol = AnalogKeyboardProtocol.A0Listen,
            Matches = Matches,
            CreateSession = _ => new A0ListenSession(),
            Writable = false,
            RegisterOnFirstReport = true,
        };

        /// <summary>An unnumbered 64-byte input report on usage page 1, usage
        /// 0, and no other route that applies to the collection.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
        {
            if (info == null || info.UsagePage != 1 || info.Usage != 0) return false;
            if (info.InputReportLength != ReportLength) return false;
            if (info.HasInputReport == null || !info.HasInputReport(0)) return false;
            foreach (var route in AnalogKeyboardRoutes.All)
            {
                if (route.Id == Id) continue;
                bool other;
                try { other = route.Matches(info); }
                catch { other = false; }
                if (other) return false;
            }
            return true;
        }

        /// <summary>One event, or false when the report is not one: report
        /// ID 0, the 0xA0 type, a key byte that is a keyboard usage (letters
        /// through the modifiers), and the value at bytes 4 and 5 after the
        /// type.</summary>
        public static bool TryParse(ReadOnlySpan<byte> raw, out int code, out int value)
        {
            code = 0;
            value = 0;
            if (raw.Length < 8 || raw[0] != 0 || raw[1] != EventType) return false;
            int key = raw[4];
            if (key < AnalogKeyCodes.A || (key > 0xA4 && (key < AnalogKeyCodes.LCtrl || key > AnalogKeyCodes.RMeta)))
                return false;
            code = key;
            value = (raw[5] << 8) | raw[6];
            return true;
        }

        /// <summary>Depth of an event value: 0 at 30 or less, value / 1600
        /// capped at 1 above.</summary>
        public static float Depth(int value) => value <= RestValue ? 0f : Math.Min(value / FullScale, 1f);
    }

    /// <summary>
    /// The listening session: one pass waits for one report and applies it
    /// when it is an event. A keyboard at rest sends nothing, which is a
    /// quiet pass. Each event carries one key, so the others keep their
    /// depths.
    /// </summary>
    public sealed class A0ListenSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait, so the reader sees a stop request.</summary>
        public const int WaitMs = 250;

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            if (!A0ListenRoute.TryParse(Buffer.AsSpan(0, n), out int code, out int value))
                return AnalogPollResult.Idle;
            output.Set(code, A0ListenRoute.Depth(value));
            return AnalogPollResult.Ok;
        }
    }
}
