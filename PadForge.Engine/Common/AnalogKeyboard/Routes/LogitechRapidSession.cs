using System;

namespace PadForge.Engine.Common.AnalogKeyboard
{
    /// <summary>
    /// Logitech PRO X TKL RAPID, read passively from its HID++ collection as
    /// the AnalogSense author's capture gist reads it
    /// (gist.github.com/Sainan/0f21e6937ee72b7979ec44ee9fde6016, commit
    /// 8d7939c). The firmware reports only the key pressed furthest down
    /// ("The List", AnalogSense/universal-analog-plugin issue #1), so this
    /// route can read one key at a time and no more.
    ///
    /// <para>The reports are HID++ long reports on usage page 0xFF00 usage 2
    /// (logitech-analogue-report.cpp:3-6): <c>11 FF 0F 00</c>, the key in
    /// byte 4 and the depth in byte 5, 0x01 to 0x28 on the way down while A
    /// is pressed and 0 on release (pressing-a.txt, releasing-d.txt). Byte 8
    /// repeats byte 5, and bytes 6 and 7 hold flags no source explains. The
    /// depth peaks at 40 in the capture, the 4.0 mm top of the keyboard's
    /// adjustable actuation range, so the byte reads as 0.1 mm steps and 40
    /// is the bottom of the press. Feature index 0x0F is where this firmware
    /// puts the reporting feature, and HID++ lets a firmware renumber its
    /// features, so a different firmware may report elsewhere.</para>
    ///
    /// <para>No source maps byte 4 beyond the two captures, A at 0x23 and D
    /// at 0x22. The Logitech key numberings on record do not fit them: the
    /// HID++ lighting zones put A at 1 and D at 4 (Solaar
    /// special_keys.py:1425-1430, OpenRGB RGBController_LogitechHIDPP20.cpp:156-167),
    /// and the USB usages put them at 4 and 7 (special_keys.py:629-633).
    /// Neither Solaar, OpenRGB nor libratbag reads these reports. Every other
    /// value is published as PadForge's vendor code 0x600 plus byte 4.</para>
    ///
    /// <para>The product ID is 0xC35B, and the keyboard is wired only:
    /// Logitech's specification page lists it as VID 046D, PID C35B, with a
    /// wired USB connection (support.logi.com article 23628794895383).
    /// OpenRGB's HID++ 2.0 controller files its PRO X RAPID layout under the
    /// same ID (RGBController_LogitechHIDPP20.cpp:375-383
    /// and 459-462), Solaar's per-key test names the board "PRO X RAPID" with
    /// wpid C35B (tests/solaar/ui/test_perkey_layouts.py:10-19), and Linux
    /// bug 221379 lists "Logitech PRO X RAPID keyboard (046d:c35b)".</para>
    /// </summary>
    public static class LogitechRapidProtocol
    {
        public const ushort VendorId = 0x046D;
        public const ushort ProductId = 0xC35B;
        public const ushort UsagePage = 0xFF00;
        public const ushort Usage = 0x0002;

        /// <summary>The HID++ long report ID.</summary>
        public const byte LongReportId = 0x11;

        /// <summary>The header of the travel reports in the gist: device
        /// index 0xFF (the wired device itself), feature index 0x0F, and
        /// function 0 with software ID 0, an event rather than an answer.</summary>
        public const byte DeviceIndex = 0xFF;
        public const byte FeatureIndex = 0x0F;
        public const byte FunctionAndSoftwareId = 0x00;

        /// <summary>The depth at the bottom of the press, 4.0 mm.</summary>
        public const int FullTravel = 40;

        public const string ModelName = "Logitech PRO X TKL RAPID";

        /// <summary>The byte 4 to key code table in the data file.</summary>
        public const string TableName = "logitech_rapid";

        /// <summary>The gist's collection test (logitech-analogue-report.cpp:3-6)
        /// narrowed to the PRO X TKL RAPID's product ID, on a collection that
        /// declares the long report.</summary>
        public static bool Matches(AnalogKeyboardDeviceInfo info)
            => info != null
               && info.VendorId == VendorId
               && info.ProductId == ProductId
               && info.UsagePage == UsagePage
               && info.Usage == Usage
               && info.HasInputReport(LongReportId);

        public static int[] Table() => AnalogKeyboardData.Table(OtherRoutes.DataFile, TableName);

        /// <summary>A travel report: the long report ID and the gist's header,
        /// then the key in byte 4 and the depth in byte 5.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> report, out int key, out int depth)
        {
            key = 0;
            depth = 0;
            if (report.Length < 6
                || report[0] != LongReportId
                || report[1] != DeviceIndex
                || report[2] != FeatureIndex
                || report[3] != FunctionAndSoftwareId) return false;
            key = report[4];
            depth = report[5];
            return true;
        }

        /// <summary>Depth over 40, clamped at the bottom.</summary>
        public static float Depth(int depth)
            => depth <= 0 ? 0f : Math.Min(depth, FullTravel) / (float)FullTravel;
    }

    /// <summary>
    /// A PRO X TKL RAPID conversation: nothing is written, and each pass
    /// waits for one report. Since the keyboard reports only the deepest key,
    /// a report replaces the whole key state: the key it names is the one
    /// whose depth is known, and a key it no longer names is released rather
    /// than left at its last depth. A report at depth 0 leaves no key down.
    /// </summary>
    public sealed class LogitechRapidSession : AnalogKeyboardSession
    {
        /// <summary>Longest single wait, so the reader sees a stop request.</summary>
        public const int WaitMs = 250;

        private readonly int[] _table;

        public LogitechRapidSession()
        {
            _table = LogitechRapidProtocol.Table();
        }

        public override string ModelName => LogitechRapidProtocol.ModelName;

        public override int[] KeyOrder => AnalogKeyboardData.KeysOf(_table);

        public override AnalogPollResult Pass(IAnalogKeyboardTransport io, AnalogKeyInputState output,
            Func<int, bool> isHeld)
        {
            int n = io.Receive(Buffer, WaitMs);
            if (n < 0) return AnalogPollResult.Failed;
            if (n == 0) return AnalogPollResult.Idle;
            if (!LogitechRapidProtocol.TryDecode(Buffer.AsSpan(0, n), out int key, out int depth))
                return AnalogPollResult.Idle;
            output.ResetForReuse();
            int code = key < _table.Length ? _table[key] : 0;
            if (code != 0) output.Set(code, LogitechRapidProtocol.Depth(depth));
            return AnalogPollResult.Ok;
        }
    }
}
