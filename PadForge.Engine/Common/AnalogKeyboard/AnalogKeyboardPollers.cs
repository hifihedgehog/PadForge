using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// The Soup and AnalogSense families that answer requests instead of
    /// pushing reports, a pass at a time. A pass sends the family's requests,
    /// reads the answers and writes the complete key set into the output. The
    /// caller owns pacing and failure counting.
    /// </summary>
    public abstract class AnalogKeyboardPoller : AnalogKeyboardSession
    {
        public static AnalogKeyboardPoller Create(AnalogKeyboardProtocol protocol, ushort vendorId, ushort productId)
        {
            switch (protocol)
            {
                case AnalogKeyboardProtocol.DrunkDeer:
                    return new DrunkDeerPoller();
                case AnalogKeyboardProtocol.Keychron:
                    var keychron = AnalogKeyboardCatalog.KeychronLayout(vendorId, productId);
                    return keychron == null ? null : new KeychronPoller(keychron);
                case AnalogKeyboardProtocol.Madlions:
                    var madlions = AnalogKeyboardCatalog.MadlionsLayout(productId);
                    return madlions == null ? null : new MadlionsPoller(madlions);
                case AnalogKeyboardProtocol.Bytech:
                    return new BytechPoller();
            }
            return null;
        }
    }

    /// <summary>
    /// DrunkDeer: request report 4 <c>B6 03 01</c>, then three answers of 59
    /// key bytes each, a 21-column grid scaled /40. Soup's
    /// getActiveKeysDrunkdeer and AnalogSense.js's AsProviderDrunkdeer. The
    /// answer index comes from byte 3 of the answer's data, as AnalogSense.js
    /// reads it, rather than from arrival order as Soup assumes.
    /// </summary>
    public sealed class DrunkDeerPoller : AnalogKeyboardPoller
    {
        public const int KeyBytesPerAnswer = 59;
        private readonly byte[] _grid = new byte[KeyBytesPerAnswer * 3];

        public static byte[] Request()
        {
            var r = new byte[64];
            r[0] = AnalogKeyboardCatalog.DrunkDeerReportId;
            r[1] = 0xB6;
            r[2] = 0x03;
            r[3] = 0x01;
            return r;
        }

        /// <summary>Copies one answer's key bytes into the grid. Returns the
        /// answer index (0 to 2), or -1 when the report is not an answer.
        /// The answer's report ID is not checked: neither reference checks
        /// it, and the vendor collection carries nothing else.</summary>
        public static int ApplyAnswer(ReadOnlySpan<byte> raw, byte[] grid)
        {
            // Report ID, three header bytes, the index, then the keys.
            if (raw.Length < 6) return -1;
            int index = raw[4];
            if (index > 2) return -1;
            int keys = Math.Min(raw.Length - 5, KeyBytesPerAnswer);
            raw.Slice(5, keys).CopyTo(grid.AsSpan(index * KeyBytesPerAnswer, keys));
            return index;
        }

        public static void ParseGrid(ReadOnlySpan<byte> grid, AnalogKeyInputState output)
        {
            output.ResetForReuse();
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] == 0) continue;
                int code = AnalogKeyCodes.DrunkDeerToCode(i);
                if (code != 0) output.Set(code, grid[i] / 40f);
            }
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            io.DiscardStale();
            if (!io.Send(Request())) return AnalogPollResult.Failed;
            Array.Clear(_grid);
            int seen = 0;
            long deadline = Environment.TickCount64 + AnswerTimeoutMs;
            while (seen != 0b111)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return AnalogPollResult.NoAnswer;
                int n = io.Receive(Buffer, remaining);
                if (n < 0) return AnalogPollResult.Failed;
                if (n == 0) return AnalogPollResult.NoAnswer;
                int index = ApplyAnswer(Buffer.AsSpan(0, n), _grid);
                if (index >= 0) seen |= 1 << index;
            }
            ParseGrid(_grid, output);
            return AnalogPollResult.Ok;
        }
    }

    /// <summary>
    /// Keychron and Lemokey HE boards over the VIA raw HID channel. The first
    /// pass asks for the version (<c>A9 01</c>). A last byte of 0x45 marks the
    /// AnalogSense firmware, which answers <c>A9 31</c> with every key in four
    /// 30-byte answers. Stock firmware answers <c>A9 30 row col</c> one key at
    /// a time, so a pass reads the keys that are down or moving plus a
    /// rotating group of four, Soup's getActiveKeysKeychron pass order.
    /// Travel under 5 is rest, and 235 is the bottom.
    /// </summary>
    public sealed class KeychronPoller : AnalogKeyboardPoller
    {
        private readonly AnalogKeyCodes.Layout _layout;
        private readonly byte[] _travel;
        private int _amVersion = -1;
        private bool _fullReports;
        private int _state = 1;

        public KeychronPoller(AnalogKeyCodes.Layout layout)
        {
            _layout = layout;
            _travel = new byte[layout.Size];
        }

        /// <summary>True once the version answer said the firmware reports
        /// every key at once.</summary>
        public bool FullReports => _fullReports;

        public static byte[] Request(byte command, byte a = 0, byte b = 0)
        {
            var r = new byte[33];
            r[1] = 0xA9;
            r[2] = command;
            r[3] = a;
            r[4] = b;
            return r;
        }

        /// <summary>Depth for a Keychron travel byte.</summary>
        public static float Depth(int travel) => travel >= 5 ? Math.Min(travel / 235f, 1f) : 0f;

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_amVersion < 0)
            {
                io.DiscardStale();
                if (!io.Send(Request(0x01))) return AnalogPollResult.Failed;
                int off = ReceiveMatching(io, 0xA9, 0x01, out int len);
                if (off == -2) return AnalogPollResult.Failed;
                if (off < 0 || len < off + 3) return AnalogPollResult.NoAnswer;
                _amVersion = Buffer[off + 2];
                _fullReports = Buffer[len - 1] == 0x45;
            }
            return _fullReports ? FullPass(io, output) : StockPass(io, output, isHeld);
        }

        private AnalogPollResult FullPass(IAnalogKeyboardTransport io, AnalogKeyInputState output)
        {
            io.DiscardStale();
            if (!io.Send(Request(0x31))) return AnalogPollResult.Failed;
            for (int answer = 0; answer < 4; answer++)
            {
                int off = ReceiveMatching(io, 0xA9, 0x31, out int len);
                if (off == -2) return AnalogPollResult.Failed;
                if (off < 0) return AnalogPollResult.NoAnswer;
                int bytes = Math.Min(len - off - 2, 30);
                for (int i = 0; i < bytes; i++)
                {
                    int index = answer * 30 + i;
                    if (index < _travel.Length) _travel[index] = Buffer[off + 2 + i];
                }
            }
            Publish(output);
            return AnalogPollResult.Ok;
        }

        private AnalogPollResult StockPass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            var keys = _layout.Keys;
            int valueAt = _amVersion >= 4 ? 6 : 3;
            for (int i = 0; i < keys.Length; i++)
            {
                int code = keys[i];
                if (code == AnalogKeyCodes.None) continue;
                bool held = isHeld != null && isHeld(code);
                if (!held && _travel[i] == 0 && _state != (i >> 2) + 1) continue;
                io.DiscardStale();
                if (!io.Send(Request(0x30, (byte)(i / _layout.Columns), (byte)(i % _layout.Columns))))
                    return AnalogPollResult.Failed;
                int off = ReceiveMatching(io, 0xA9, 0x30, out int len);
                if (off == -2) return AnalogPollResult.Failed;
                if (off < 0) return AnalogPollResult.NoAnswer;
                _travel[i] = len > off + valueAt ? Buffer[off + valueAt] : (byte)0;
            }
            // Soup advances the rotation after every pass and wraps past the
            // last group.
            if (_state++ == (keys.Length >> 2) + 1) _state = 1;
            Publish(output);
            return AnalogPollResult.Ok;
        }

        private void Publish(AnalogKeyInputState output)
        {
            output.ResetForReuse();
            var keys = _layout.Keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == AnalogKeyCodes.None) continue;
                float depth = Depth(_travel[i]);
                if (depth > 0f) output.Set(keys[i], depth);
            }
        }
    }

    /// <summary>
    /// Madlions HE boards over the VIA raw HID channel: <c>02 96 1C</c> with
    /// the first key's offset at data byte 6 and a count of 4 at byte 7, and
    /// an answer carrying four keys of 5 bytes each after a 7-byte header, the
    /// travel a big-endian u16 at the end of each, 350 at the bottom. A pass
    /// reads the groups of four holding a key that is down or moving, plus a
    /// rotating block of sixteen keys, Soup's getActiveKeysMadlions pass order.
    /// </summary>
    public sealed class MadlionsPoller : AnalogKeyboardPoller
    {
        public const float FullTravel = 350f;
        private readonly AnalogKeyCodes.Layout _layout;
        private readonly ushort[] _travel;
        private int _state;

        public MadlionsPoller(AnalogKeyCodes.Layout layout)
        {
            _layout = layout;
            _travel = new ushort[layout.Size];
        }

        public static byte[] Request(int offset)
        {
            var r = new byte[33];
            r[1] = 0x02;
            r[2] = 0x96;
            r[3] = 0x1C;
            r[7] = (byte)offset;
            r[8] = 4;
            return r;
        }

        /// <summary>Reads the four travels out of an answer. False when the
        /// answer is too short to carry them.</summary>
        public static bool ReadAnswer(ReadOnlySpan<byte> raw, Span<ushort> travel)
        {
            var data = AnalogKeyboardParsers.StripZeroReportId(raw);
            if (data.Length < 7 + 4 * 5) return false;
            for (int i = 0; i < 4; i++)
            {
                int at = 7 + i * 5 + 3;
                travel[i] = (ushort)((data[at] << 8) | data[at + 1]);
            }
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            var keys = _layout.Keys;
            Span<ushort> answer = stackalloc ushort[4];
            for (int offset = 0; offset < keys.Length; offset += 4)
            {
                bool wanted = (offset >> 4) == _state;
                for (int i = 0; i < 4 && !wanted; i++)
                {
                    int index = offset + i;
                    if (index >= keys.Length || keys[index] == AnalogKeyCodes.None) continue;
                    if (_travel[index] != 0 || (isHeld != null && isHeld(keys[index]))) wanted = true;
                }
                if (!wanted) continue;

                io.DiscardStale();
                if (!io.Send(Request(offset))) return AnalogPollResult.Failed;
                long deadline = Environment.TickCount64 + AnswerTimeoutMs;
                int n;
                while (true)
                {
                    int remaining = (int)(deadline - Environment.TickCount64);
                    if (remaining <= 0) return AnalogPollResult.NoAnswer;
                    n = io.Receive(Buffer, remaining);
                    if (n < 0) return AnalogPollResult.Failed;
                    if (n == 0) return AnalogPollResult.NoAnswer;
                    if (ReadAnswer(Buffer.AsSpan(0, n), answer)) break;
                }
                for (int i = 0; i < 4; i++)
                {
                    int index = offset + i;
                    if (index < keys.Length) _travel[index] = answer[i];
                }
            }
            if (_state++ == (keys.Length >> 4)) _state = 0;

            output.ResetForReuse();
            for (int i = 0; i < keys.Length; i++)
                if (keys[i] != AnalogKeyCodes.None && _travel[i] != 0)
                    output.Set(keys[i], _travel[i] / FullTravel);
            return AnalogPollResult.Ok;
        }
    }

    /// <summary>
    /// Bytech chips (Redragon K709 HE): report 9 carrying <c>97 00</c> and a
    /// checksum in its last byte, answered with <c>97 01</c>, a byte count at
    /// data byte 5 and 4-byte entries of big-endian position and distance, 355
    /// at the bottom and 10 or less at rest. AnalogSense.js's
    /// AsProviderBytech, the one reference. It re-asks after every answer and
    /// once a second besides, so a pass waits up to a second for its answer.
    /// </summary>
    public sealed class BytechPoller : AnalogKeyboardPoller
    {
        public const int PayloadLength = 63;
        public const int AnswerWaitMs = 1000;

        /// <summary>The request, report ID first: the payload's last byte is
        /// 255 minus the sum of the report ID and every other payload byte,
        /// modulo 256.</summary>
        public static byte[] Request()
        {
            var r = new byte[1 + PayloadLength];
            r[0] = AnalogKeyboardCatalog.BytechReportId;
            r[1] = 0x97;
            r[2] = 0x00;
            int sum = AnalogKeyboardCatalog.BytechReportId;
            for (int i = 1; i < r.Length - 1; i++) sum += r[i];
            r[r.Length - 1] = (byte)(255 - (sum % 256));
            return r;
        }

        /// <summary>Parses an answer into <paramref name="output"/>, replacing
        /// it. False when the report is not an answer.</summary>
        public static bool ParseAnswer(ReadOnlySpan<byte> raw, AnalogKeyInputState output)
        {
            // The answer's data starts with 97 01, after a report ID when the
            // collection numbers its reports.
            int off;
            if (raw.Length >= 2 && raw[0] == 0x97 && raw[1] == 0x01) off = 0;
            else if (raw.Length >= 3 && raw[1] == 0x97 && raw[2] == 0x01) off = 1;
            else return false;
            var data = raw.Slice(off);
            if (data.Length < 6) return false;
            int count = data[5];
            output.ResetForReuse();
            for (int i = 0; i < count; i += 4)
            {
                if (10 + i > data.Length) break;
                int position = (data[6 + i] << 8) | data[7 + i];
                int distance = (data[8 + i] << 8) | data[9 + i];
                int code = AnalogKeyCodes.BytechToCode(position);
                if (code != 0 && distance > 10) output.Set(code, Math.Min(distance / 355f, 1f));
            }
            return true;
        }

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (!io.Send(Request())) return AnalogPollResult.Failed;
            long deadline = Environment.TickCount64 + AnswerWaitMs;
            while (true)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0) return AnalogPollResult.NoAnswer;
                int n = io.Receive(Buffer, remaining);
                if (n < 0) return AnalogPollResult.Failed;
                if (n == 0) return AnalogPollResult.NoAnswer;
                if (ParseAnswer(Buffer.AsSpan(0, n), output)) return AnalogPollResult.Ok;
            }
        }
    }
}
