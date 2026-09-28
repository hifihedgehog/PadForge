using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// ASUS ROG Azoth 96 HE (M901), wired, read with the protocol of HallJoy's
    /// M901 diagnostic backend (rog_azoth96he_diagnostic_backend.cpp, HallJoy
    /// commit 378f9fe) and its firmware reconnaissance
    /// (docs/research/ROG_AZOTH_96_HE_M901_FIRMWARE_RECON_2026-09-06.md),
    /// checked against ASUS Gear Link, the keyboard's WebHID configurator, and
    /// the M901 firmware 7.00.30 it downloads (analog-keyboard-references
    /// asus-gear-link; chunk BJ-_oHSK.js is "BJ" and dDfc5LWq.js "dD" below,
    /// offsets in characters). HallJoy keeps its route out of ordinary builds
    /// because no one has traced the keyboard (recon:92-101).
    ///
    /// <para>Two collections of one keyboard take part. The control
    /// collection, usage page 0xFF00 usage 1, declares no report ID (its
    /// descriptor at firmware file offset 0x5CD17), and takes
    /// <c>51 61 00 00</c> and zeros, Gear Link's setKeyTravelNotify for every
    /// key (BJ 436220, recon:64-85). The firmware runs commands only from its
    /// interrupt OUT endpoint, which WriteFile reaches. A SET_REPORT control
    /// transfer, which HidD_SetOutputReport sends, returns success and does
    /// nothing (radio core 0x0E09050C against 0x0E090658), so the enable goes
    /// out as an output report write. The enable is a lease of 60 that runs
    /// out in about a minute, with no command that ends it (radio core
    /// 0x0E07C3C0, 0x0E07DE76, 0x0E08FEAA), and Gear Link sends it again every
    /// 30 s (dD 253281).</para>
    ///
    /// <para>The events arrive on the usage page 0xFFC0 usage 1 collection as
    /// input report 3, 20 bytes after the ID: <c>7E</c>, the firmware key as
    /// a little-endian u16 and the travel as a little-endian u16 in 0.01 mm
    /// (BJ 455992, recon:57-85). Gear Link draws the travel on a gauge of 350
    /// (dD 212695), so 350 is the bottom of the press. The firmware tracks
    /// one key at a time: the first key past 0.10 mm, streamed while it is
    /// held and closed with one travel of 0 (radio core 0x0E093272 to
    /// 0x0E09336A, 0x0E0953DC). <c>80 26</c> is switch calibration and is
    /// never sent (recon:87-90).</para>
    ///
    /// <para>The firmware key is the IBM key-position number (W is 18, Esc
    /// 110). The key table is the firmware's own map from those numbers to
    /// HID usages (radio core 0x0E0C1188), which agrees with Gear Link's key
    /// tables on 105 of 106 keys. A key the map has no usage for, Fn among
    /// them, is published by its position, 0x600 plus the key.</para>
    /// </summary>
    public static class RogAzoth96HeProtocol
    {
        public const ushort VendorId = 0x0B05;

        /// <summary>The wired application PID. The bootloader (0x1C11), the
        /// RF keyboard (0x1C12), BLE (0x1C13) and the receiver (0x1AD0) are
        /// not admitted (recon:25-38).</summary>
        public const ushort ProductId = 0x1C10;

        public const ushort ControlUsagePage = 0xFF00;
        public const ushort EventUsagePage = 0xFFC0;
        public const ushort VendorUsage = 0x0001;

        /// <summary>HallJoy's control report length (kControlReportBytes,
        /// backend:33), required of the input and output reports alike
        /// (backend:172-173).</summary>
        public const int ControlReportLength = 64;

        /// <summary>The event collection's Windows input length: report ID 3
        /// and 20 bytes (kEventReportBytes, backend:34). It has no output
        /// report (backend:188).</summary>
        public const int EventReportLength = 21;

        public const byte EventReportId = 0x03;
        public const byte TravelEvent = 0x7E;

        /// <summary>Travel at 3.50 mm, the deepest point Gear Link offers.</summary>
        public const int FullTravel = 350;

        public const string ModelName = "ROG Azoth 96 HE";

        /// <summary>The firmware key to key code table in the data file.</summary>
        public const string TableName = "rog_azoth96he";

        /// <summary>The control collection of a wired Azoth 96 HE whose event
        /// collection is present too: HallJoy starts only when both are
        /// (backend:185-211).</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => IsControl(info) && FindEvents(info) != null;

        /// <summary>HallJoy's control test (backend:132-183): exact VID and
        /// PID, FF00:1, 64-byte input and output reports. HallJoy counts 64
        /// bytes, while the recon describes 64-byte packets without a report
        /// ID, which Windows reports as 65 with the ID byte, so either length
        /// is admitted. Report ID 0 confirms the collection numbers no
        /// reports (recon:37 and 56), which the enable frame depends on.</summary>
        public static bool IsControl(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && info.ProductId == ProductId
               && info.UsagePage == ControlUsagePage
               && info.Usage == VendorUsage
               && IsControlLength(info.InputReportLength)
               && IsControlLength(info.OutputReportLength)
               && info.HasOutputReport(0);

        private static bool IsControlLength(int length)
            => length == ControlReportLength || length == ControlReportLength + 1;

        /// <summary>HallJoy's event test (backend:132-183 and 188): exact VID
        /// and PID, FFC0:1, a 21-byte input report and no output report, and
        /// input report 3 declared.</summary>
        public static bool IsEvents(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && info.ProductId == ProductId
               && info.UsagePage == EventUsagePage
               && info.Usage == VendorUsage
               && info.InputReportLength == EventReportLength
               && info.OutputReportLength == 0
               && info.HasInputReport(EventReportId);

        /// <summary>The event collection of the same keyboard, the route's
        /// companion. HallJoy pairs the first control and the first event
        /// collection it enumerates (backend:252-265), which would cross two
        /// keyboards. Pairing through the siblings keeps each to its own.</summary>
        public static AnalogKeyboardDeviceInfo FindEvents(AnalogKeyboardDeviceInfo info)
        {
            if (info?.Siblings == null) return null;
            foreach (var sibling in info.Siblings)
                if (IsEvents(sibling)) return sibling;
            return null;
        }

        public static int[] Table() => AnalogKeyboardData.Table(OtherRoutes.DataFile, TableName);

        /// <summary>The travel notification enable for all keys, report ID 0
        /// first: <c>51 61 00 00</c>, key 0 in bytes 4 and 5 meaning every
        /// key (recon:67-74, Gear Link's makeCommand at BJ 330165). HallJoy's
        /// frame puts 0x51 where Windows expects the report ID (backend:215-219),
        /// the Linux hidraw form. Windows takes the report ID first, and the
        /// transport pads the frame to the collection's 65 bytes.</summary>
        public static byte[] EnableTravelRequest() => new byte[] { 0x00, 0x51, 0x61, 0x00, 0x00 };

        /// <summary>RecordTravel's decoder (backend:223-229): report 3, event
        /// 7E, then the firmware key and the travel, both little-endian.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> report, out int key, out int travel)
        {
            key = 0;
            travel = 0;
            if (report.Length < 6 || report[0] != EventReportId || report[1] != TravelEvent) return false;
            key = report[2] | (report[3] << 8);
            travel = report[4] | (report[5] << 8);
            return true;
        }

        /// <summary>The code for a firmware key: its HID usage from the
        /// firmware's map, else its position, 0 for a key past 0xFF.</summary>
        public static int CodeFor(int[] table, int key)
        {
            int code = table != null && key >= 0 && key < table.Length ? table[key] : 0;
            if (code != 0) return code;
            return key > 0 && key <= 0xFF ? AnalogKeyCodes.PositionBase + key : 0;
        }

        /// <summary>Depth for a travel in 0.01 mm, 3.50 mm at the bottom.</summary>
        public static float Depth(int travel)
            => travel <= 0 ? 0f : Math.Min(travel / (float)FullTravel, 1f);
    }

    /// <summary>
    /// An Azoth 96 HE conversation: the enable at the start and again every
    /// 30 s while it runs, as Gear Link keeps its lease (dD 253281), then one
    /// travel event per pass from the event collection. A failed enable does
    /// not end the session, as HallJoy keeps reading (backend:280-289): the
    /// next renewal tries again. No command turns the events off, so
    /// <see cref="AnalogKeyboardSession.Stop"/> sends nothing and the lease
    /// runs out on its own within a minute.
    ///
    /// <para>Each event replaces the key set, since the firmware tracks one
    /// key at a time. The firmware streams a held key without pause, so half a
    /// second without an event releases it, Gear Link's own timeout for its
    /// travel preview (dD 253586). That also releases a key whose closing 0
    /// was lost when a lease ran out under it.</para>
    /// </summary>
    public sealed class RogAzoth96HeSession : AnalogKeyboardSession
    {
        /// <summary>HallJoy's read slice (kReadSliceMs, backend:37).</summary>
        public const int WaitMs = 100;

        /// <summary>How often the enable goes out again (dD 253281).</summary>
        public const int RenewMs = 30000;

        /// <summary>How long a held key may go without an event (dD 253586).</summary>
        public const int ReleaseMs = 500;

        private readonly int[] _table;
        private readonly Func<long> _clock;
        private long _nextRenew;
        private long _lastEvent;

        /// <param name="clock">Milliseconds, Environment.TickCount64 unless a
        /// test supplies its own.</param>
        public RogAzoth96HeSession(Func<long> clock = null)
        {
            _table = RogAzoth96HeProtocol.Table();
            _clock = clock ?? (() => Environment.TickCount64);
        }

        /// <summary>Whether the last enable write succeeded.</summary>
        public bool EnableSent { get; private set; }

        public override string ModelName => RogAzoth96HeProtocol.ModelName;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_table);

        public override bool Start(IAnalogKeyboardTransport io)
        {
            Enable(io);
            return true;
        }

        /// <summary>The enable, written to the interrupt OUT endpoint, the only
        /// path the firmware runs commands from.</summary>
        private void Enable(IAnalogKeyboardTransport io)
        {
            EnableSent = io.Send(RogAzoth96HeProtocol.EnableTravelRequest());
            _nextRenew = _clock() + RenewMs;
        }

        /// <summary>A read error ends the session (HallJoy leaves its loop,
        /// backend:304-309). A report of another length, ID or event is
        /// skipped, as HallJoy skips it (backend:300-302 and 225).</summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            if (_clock() >= _nextRenew) Enable(io);
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == RogAzoth96HeProtocol.EventReportLength
                && RogAzoth96HeProtocol.TryDecode(Buffer.AsSpan(0, n), out int key, out int travel))
            {
                int code = RogAzoth96HeProtocol.CodeFor(_table, key);
                if (code != 0)
                {
                    _lastEvent = _clock();
                    output.ResetForReuse();
                    output.Set(code, RogAzoth96HeProtocol.Depth(travel));
                    return AnalogPollResult.Ok;
                }
            }
            if (output.Count > 0 && _clock() - _lastEvent > ReleaseMs)
            {
                output.ResetForReuse();
                return AnalogPollResult.Ok;
            }
            return AnalogPollResult.Idle;
        }
    }
}
