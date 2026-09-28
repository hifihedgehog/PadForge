using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// ASUS ROG Azoth 96 HE (M901), wired, read the way HallJoy's M901
    /// diagnostic backend reads it (rog_azoth96he_diagnostic_backend.cpp,
    /// HallJoy commit 378f9fe) with the protocol facts of HallJoy's firmware
    /// reconnaissance (docs/research/ROG_AZOTH_96_HE_M901_FIRMWARE_RECON_2026-09-06.md).
    /// HallJoy keeps this route out of its ordinary builds because no one has
    /// traced the keyboard yet (recon:92-101, SUPPORTED_HARDWARE.md:166).
    ///
    /// <para>Two collections of one keyboard take part. The control
    /// collection, usage page 0xFF00 usage 1 with no report ID, takes
    /// <c>51 61 00 00</c> and zeros, which ASUS Gear Link names
    /// setKeyTravelNotify and sends to turn on travel events for every key
    /// (recon:64-85, backend:213-221). The events arrive on the usage page
    /// 0xFFC0 usage 1 collection as input report 3, 20 bytes after the ID:
    /// <c>7E</c>, the firmware key as a little-endian u16, and the travel as
    /// a little-endian u16 (recon:57-85, backend:223-229). Gear Link gives
    /// the switch range as 0.10 to 3.50 mm in 0.01 mm steps (recon:42-45),
    /// so 350 is the bottom of the press. <c>80 26</c> is switch
    /// calibration and is never sent (recon:87-90, backend:216).</para>
    ///
    /// <para>No source maps the firmware key to a key (recon:92-101), so a
    /// key is published as PadForge's vendor code 0x600 plus the firmware
    /// key. A firmware key above 0xFF has no code in that range and is
    /// dropped.</para>
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
        /// key (recon:67-74). HallJoy's frame puts 0x51 where Windows expects
        /// the report ID (backend:215-219), which Windows refuses for a
        /// collection without report IDs or strips before the device sees it.
        /// Here the report ID comes first and the command follows. The
        /// transport pads it to the collection's output length.</summary>
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

        /// <summary>The code for a firmware key, 0 when it has none.</summary>
        public static int CodeFor(int[] table, int key)
            => table != null && key >= 0 && key < table.Length ? table[key] : 0;

        /// <summary>Depth for a travel in 0.01 mm, 3.50 mm at the bottom.</summary>
        public static float Depth(int travel)
            => travel <= 0 ? 0f : Math.Min(travel / (float)FullTravel, 1f);
    }

    /// <summary>
    /// An Azoth 96 HE conversation: the enable once at the start, then one
    /// travel event per pass from the event collection. HallJoy sends the
    /// enable with HidD_SetOutputReport and keeps reading when it fails
    /// (backend:280-289), so a failed enable does not end the session: the
    /// events may already be on. No source documents a command that turns
    /// the events off. HallJoy's stop only cancels its reads and closes its
    /// handles (backend:339-360), so <see cref="AnalogKeyboardSession.Stop"/>
    /// sends nothing.
    /// </summary>
    public sealed class RogAzoth96HeSession : AnalogKeyboardSession
    {
        /// <summary>HallJoy's read slice (kReadSliceMs, backend:37).</summary>
        public const int WaitMs = 100;

        private readonly int[] _table;

        public RogAzoth96HeSession()
        {
            _table = RogAzoth96HeProtocol.Table();
        }

        /// <summary>Whether the enable write succeeded.</summary>
        public bool EnableSent { get; private set; }

        public override string ModelName => RogAzoth96HeProtocol.ModelName;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_table);

        public override bool Start(IAnalogKeyboardTransport io)
        {
            EnableSent = io.SendOutputReport(RogAzoth96HeProtocol.EnableTravelRequest());
            return true;
        }

        /// <summary>A read error ends the session (HallJoy leaves its loop,
        /// backend:304-309). A report of another length, ID or event is
        /// skipped, as HallJoy skips it (backend:300-302 and 225).</summary>
        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n != RogAzoth96HeProtocol.EventReportLength) return AnalogPollResult.Idle;
            if (!RogAzoth96HeProtocol.TryDecode(Buffer.AsSpan(0, n), out int key, out int travel))
                return AnalogPollResult.Idle;
            int code = RogAzoth96HeProtocol.CodeFor(_table, key);
            if (code == 0) return AnalogPollResult.Idle;
            output.Set(code, RogAzoth96HeProtocol.Depth(travel));
            return AnalogPollResult.Ok;
        }
    }
}
