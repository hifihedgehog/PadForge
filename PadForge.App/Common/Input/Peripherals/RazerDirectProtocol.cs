using System;
using System.Collections.Generic;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>The reports that paint a Razer product one color, by the
    /// name the references give each family. The order matters: every
    /// family from <see cref="Classic"/> on takes a color.</summary>
    internal enum RazerFamily : byte
    {
        /// <summary>No lighting command is known for the product.</summary>
        None,
        /// <summary>A device older than the 90-byte report.</summary>
        Legacy,
        /// <summary>A single-color LED that only turns on and off.</summary>
        Mono,
        /// <summary>Classic LED reports: state, color and effect per LED
        /// (class 03, commands 00, 01 and 02).</summary>
        Classic,
        /// <summary>The extended matrix effect, one report per LED (0F/02).</summary>
        Extended,
        /// <summary>The extended matrix frame of one row (0F/03), which
        /// paints the whole mouse after a mode report.</summary>
        ExtendedFrame,
        /// <summary>The standard matrix: frame rows (03/0B or 03/0C) shown
        /// by 03/0A, or the 03/0A effect alone.</summary>
        Standard,
        /// <summary>The mouse extended matrix effect, one report per LED (03/0D).</summary>
        MouseExtended,
        /// <summary>The extended matrix frame (#557): one 0F/03 row for
        /// each row of the frame, then the custom frame effect, 0F/02
        /// [00 00 08] at size 0C, which shows it. OpenRGB sends every
        /// frame of an extended matrix product so (OpenRGB
        /// RazerController.cpp:207-258, 793-810 and 931-940 at c6fc8016),
        /// and openrazer builds the same effect report
        /// (razerchromacommon.c:703-706).</summary>
        ExtendedMatrix,
    }

    /// <summary>What sets one row apart inside its family.</summary>
    [Flags]
    internal enum RazerRowFlags : byte
    {
        None = 0,
        /// <summary>Standard matrix: the frame is one line of LEDs, sent
        /// as 03/0C, where a keyboard's rows are 03/0B.</summary>
        LineRows = 1,
        /// <summary>Standard matrix: the product has no frame rows, so it
        /// is painted by the 03/0A static effect, a report with no storage
        /// byte, and only once a color has held
        /// (<see cref="RazerDirectProtocol.EffectHoldMs"/>).</summary>
        HeldEffect = 2,
        /// <summary>Classic LED: the effect report goes out ahead of the
        /// color report.</summary>
        EffectFirst = 4,
        /// <summary>Classic LED: no state report opens the claim.</summary>
        NoState = 8,
        /// <summary>A laptop, whose firmware ends host lighting when the
        /// host goes quiet, so a read keeps it awake.</summary>
        Laptop = 16,
        /// <summary>Standard matrix, painted by effect: the spectrum
        /// effect (03/0A [04]) is its hand-back.</summary>
        SpectrumBack = 32,
    }

    /// <summary>The read that opens a claim: a report that changes nothing
    /// and whose reply shows the device answers this transaction ID.</summary>
    internal enum RazerProbe : byte
    {
        None,
        /// <summary>0F/84 [01 LED 00], the extended matrix brightness.</summary>
        ExtendedBrightness,
        /// <summary>03/83 [01 LED 00], an LED's brightness.</summary>
        StandardBrightness,
        /// <summary>0E/84 [01 00], a laptop keyboard's brightness.</summary>
        LaptopBrightness,
        /// <summary>03/80 [01 LED 00], an LED's state.</summary>
        ClassicState,
        /// <summary>0F/80 at size 50, the list of lighting regions, the
        /// first read OpenRGB sends a product it paints through LED 00
        /// (RazerController.cpp:1197-1271 at c6fc8016).</summary>
        RegionList,
        /// <summary>00/BF at size 50, the devices a wireless receiver has
        /// paired, the read OpenRGB sends every product it opens
        /// (RazerController.cpp:1273-1306 and RazerControllerDetect.cpp:52
        /// at c6fc8016).</summary>
        PairedDevices,
        /// <summary>0F/81 [LED] at size 50, the effects one region takes,
        /// the first read OpenRGB sends an extended matrix product it paints
        /// through an LED other than 00 (RazerController.cpp:1207-1216 and
        /// 1242-1252 at c6fc8016).</summary>
        RegionEffects,
    }

    /// <summary>A HID collection the product table names as the one a
    /// product is driven through on Windows. An interface of -1 is any
    /// interface, for a product OpenRGB finds by usage alone (its
    /// REGISTER_HID_DETECTOR_PU, RazerControllerDetect.cpp at c6fc8016), and
    /// a usage page of 0 any usage, for one it finds by interface alone
    /// (REGISTER_HID_DETECTOR_I).</summary>
    internal readonly record struct RazerCollection(int Interface, ushort UsagePage, ushort Usage);

    internal enum RazerFallbackKind : byte
    {
        /// <summary>One standard matrix row (03/0B) and the show report.</summary>
        MatrixRow,
        /// <summary>One line of LEDs (03/0C) and the show report.</summary>
        LineRow,
        /// <summary>The 03/0A static effect, held like
        /// <see cref="RazerRowFlags.HeldEffect"/>.</summary>
        Effect,
    }

    /// <summary>Another family's paint, tried when a product refuses the
    /// first paint of its own.</summary>
    internal readonly record struct RazerFallback(RazerFallbackKind Kind, byte Columns);

    /// <summary>One product ID as <see cref="RazerDirectTable"/> lists it.
    /// A row says what the product is, and
    /// <see cref="RazerDirectProtocol"/> builds its reports from that.</summary>
    internal sealed class RazerDirectRow
    {
        public ushort ProductId { get; init; }

        /// <summary>The name the openrazer daemon gives the product, for
        /// the diagnostics log. A tab names a device by its own row.</summary>
        public string Name { get; init; }
        public RazerFamily Family { get; init; }

        /// <summary>The transaction ID openrazer puts on this product's
        /// static color report, tried first, and the other IDs one of the
        /// two projects sends to the same product, tried after it. No
        /// other ID is ever sent, but for the one a product's claim read
        /// has of its own (<see cref="ProbeTransactionId"/>).</summary>
        public byte TransactionId { get; init; }
        public byte[] OtherTransactionIds { get; init; } = Array.Empty<byte>();

        /// <summary>The LEDs painted one report each, or for
        /// <see cref="RazerFamily.ExtendedFrame"/> the one LED its reads
        /// name. Empty for the standard matrix, which has no LED byte.</summary>
        public byte[] Leds { get; init; } = Array.Empty<byte>();

        /// <summary>The LEDs whose spectrum effect is the hand-back when
        /// the device does not say what it showed. Empty where the product
        /// has no spectrum effect.</summary>
        public byte[] SpectrumLeds { get; init; } = Array.Empty<byte>();

        /// <summary>The frame a standard matrix product is painted with,
        /// and for <see cref="RazerFamily.ExtendedFrame"/> the LEDs in its
        /// one row.</summary>
        public byte Rows { get; init; }
        public byte Columns { get; init; }

        /// <summary>The transaction IDs openrazer puts on this product's
        /// frame rows and on the report that shows the frame, where they
        /// differ from <see cref="TransactionId"/>. 0 for the same.</summary>
        public byte RowTransactionId { get; init; }
        public byte ShowTransactionId { get; init; }

        public RazerProbe Probe { get; init; }
        public byte ProbeLed { get; init; }

        /// <summary>The transaction ID openrazer sends <see cref="Probe"/>
        /// under, where it is none of the IDs the product is painted under:
        /// nine mice and one keyboard are read under FF and painted under
        /// 3F. The read goes out under it first, as the one project that
        /// sends the read sends it. 0 for a product whose read goes out
        /// under its own IDs.</summary>
        public byte ProbeTransactionId { get; init; }
        public RazerRowFlags Flags { get; init; }
        public RazerFallback[] Fallbacks { get; init; } = Array.Empty<RazerFallback>();

        /// <summary>How long the device takes to have its reply ready, in
        /// microseconds (openrazer razermouse_driver.h:134-137 and
        /// razerkbd_driver.h:166-168).</summary>
        public int ReplyWaitUs { get; init; }

        /// <summary>The collection to open first, and a second one where a
        /// firmware update moved it.</summary>
        public RazerCollection[] Collections { get; init; } = Array.Empty<RazerCollection>();

        /// <summary>The HID report ID the product's feature report carries:
        /// 0 for every product but the Leviathan V2 speakers, whose report
        /// is 07 (OpenRGB RazerController.cpp:50-60 at c6fc8016, its
        /// report_index and response_index).</summary>
        public byte ReportId { get; init; }

        public bool HasColorPath => Family >= RazerFamily.Classic;
        public bool Has(RazerRowFlags flag) => (Flags & flag) != 0;

        /// <summary>A reply this slow is not waited for on the worker
        /// thread: the read is noted as due and other devices are served
        /// meanwhile.</summary>
        public bool SlowReply => ReplyWaitUs >= RazerDirectProtocol.SlowReplyUs;
    }

    /// <summary>What a reply says about the request it was read after.</summary>
    internal enum RazerReply
    {
        /// <summary>The reply is this request's, with status 02 or 01.</summary>
        Accepted,
        /// <summary>The reply is not this request's yet: it carries another
        /// command or transaction ID, or status 00. Read again.</summary>
        Early,
        /// <summary>Status 03, which is tried again.</summary>
        Failure,
        /// <summary>Status 04: nothing answered behind the receiver.</summary>
        Timeout,
        /// <summary>Status 05, which ends the attempt.</summary>
        NotSupported,
    }

    /// <summary>
    /// The Razer feature report as PadForge speaks it to light one device
    /// with no vendor software (#494). Pure: every method builds or reads
    /// bytes, and none touches a device. Line numbers are those of openrazer
    /// 477f4d6d and OpenRGB 01bd77c, the revisions the product table was
    /// read from.
    ///
    /// <para>One report carries every command: 90 bytes, sent and read back
    /// as a HID feature report with report ID 0 (openrazer
    /// driver/razercommon.h:124-136). Windows wants the ID in front, so the
    /// buffer is 91 bytes, the layout OpenRGB's hidapi build has
    /// (RazerController.h:236-249). After the ID: the status, the
    /// transaction ID, two bytes of remaining packets, the protocol type,
    /// the data size, the command class, the command ID, 80 argument bytes,
    /// the checksum and a reserved byte. A command ID with its top bit set
    /// is a read (razercommon.h:120).</para>
    ///
    /// <para>The checksum is the XOR of the report from its remaining
    /// packets through its last argument, buffer bytes 3 to 88, so the
    /// status and the transaction ID stay out of it (razercommon.c:111-124
    /// over the 90 bytes, RazerController.cpp:602-623 over the 91).</para>
    ///
    /// <para>Every write here that has a storage byte carries 00, which
    /// shows the color and writes nothing to the device's memory
    /// (razercommon.h:33-34, and the openrazer wiki's
    /// Unknown-commands.md:60, where 00 is "the no-store profile"). A color
    /// that follows a controller must not wear the device's flash on every
    /// change. The reads carry 01, as the references send them
    /// (razerchromacommon.c:729).</para>
    ///
    /// <para>No device-mode report (class 00, command 04) is built here or
    /// sent anywhere. Mode 03 hands a device's special keys to a program
    /// that must then handle them, and one that dies leaves the device
    /// there (razerchromacommon.c:15-39).</para>
    /// </summary>
    internal static class RazerDirectProtocol
    {
        internal const ushort VendorId = 0x1532;

        /// <summary>The feature report with its report ID byte, which is
        /// also the feature length the control collection declares.</summary>
        internal const int BufferLength = 91;
        internal const int MaxArguments = 80;

        internal const int StatusAt = 1;
        internal const int TransactionAt = 2;
        internal const int SizeAt = 6;
        internal const int ClassAt = 7;
        internal const int CommandAt = 8;
        internal const int ArgumentsAt = 9;
        internal const int CrcAt = 89;

        // Reply statuses (razercommon.h:79-83 and 107-113).
        internal const byte StatusNew = 0x00;
        internal const byte StatusBusy = 0x01;
        internal const byte StatusSuccess = 0x02;
        internal const byte StatusFailure = 0x03;
        internal const byte StatusTimeout = 0x04;
        internal const byte StatusNotSupported = 0x05;

        // LED IDs (razercommon.h:37-48). The underglow strip is 0A in the
        // region lists devices give (uncoil docs/PROTOCOL.md:194 and 209).
        internal const byte LedZero = 0x00;
        internal const byte LedScrollWheel = 0x01;
        internal const byte LedLogo = 0x04;
        internal const byte LedBacklight = 0x05;
        internal const byte LedUnderglow = 0x0A;
        internal const byte LedRightSide = 0x10;
        internal const byte LedLeftSide = 0x11;

        /// <summary>The reply wait from which a device counts as slow:
        /// openrazer's 31 ms for the newer wireless mice and 59.9 ms for
        /// the Viper Ultimate's class (razermouse_driver.h:135 and 137).
        /// The wired 600 microseconds and a wireless keyboard's 4.9 ms are
        /// waited for in place.</summary>
        internal const int SlowReplyUs = 31000;

        /// <summary>How long a color has to stay the same before the
        /// standard matrix static effect carries it. That report has no
        /// storage byte, and one owner of such a keyboard says its settings
        /// "are saved on the keyboard itself" (razer-life README.md:5), so
        /// a color that keeps moving must write nothing.</summary>
        internal const int EffectHoldMs = 1000;

        /// <summary>The most regions a list is believed to hold. OpenRGB
        /// reads at most 16 (RazerController.cpp:1212 and 1229), which is
        /// also all a report has room for.</summary>
        internal const int MaxRegions = 16;

        // ─────────────────────────────────────────────
        //  The report
        // ─────────────────────────────────────────────

        /// <summary>The XOR of buffer bytes 3 to 88.</summary>
        internal static byte Crc(ReadOnlySpan<byte> buffer)
        {
            byte crc = 0;
            for (int i = 3; i < CrcAt; i++) crc ^= buffer[i];
            return crc;
        }

        /// <summary>One request: the header, the arguments and the
        /// checksum. <paramref name="dataSize"/> is the size the report
        /// declares, which two commands set apart from the bytes they
        /// carry.</summary>
        internal static byte[] Report(byte transactionId, byte commandClass, byte commandId, int dataSize,
            ReadOnlySpan<byte> arguments)
        {
            if (arguments.Length > MaxArguments) throw new ArgumentOutOfRangeException(nameof(arguments));
            if ((uint)dataSize > MaxArguments) throw new ArgumentOutOfRangeException(nameof(dataSize));
            var buffer = new byte[BufferLength];
            buffer[TransactionAt] = transactionId;
            buffer[SizeAt] = (byte)dataSize;
            buffer[ClassAt] = commandClass;
            buffer[CommandAt] = commandId;
            arguments.CopyTo(buffer.AsSpan(ArgumentsAt));
            buffer[CrcAt] = Crc(buffer);
            return buffer;
        }

        private static byte[] Report(byte transactionId, byte commandClass, byte commandId, ReadOnlySpan<byte> arguments)
            => Report(transactionId, commandClass, commandId, arguments.Length, arguments);

        /// <summary>A request again under another transaction ID. The
        /// checksum does not cover the ID, so it stands.</summary>
        internal static byte[] WithTransactionId(byte[] request, byte transactionId)
        {
            var copy = (byte[])request.Clone();
            copy[TransactionAt] = transactionId;
            return copy;
        }

        private static byte R(int rgb) => (byte)(rgb >> 16);
        private static byte G(int rgb) => (byte)(rgb >> 8);
        private static byte B(int rgb) => (byte)rgb;

        /// <summary>A header and one color repeated, for a frame row.</summary>
        private static byte[] WithColors(ReadOnlySpan<byte> header, int columns, int rgb)
        {
            var arguments = new byte[header.Length + 3 * columns];
            header.CopyTo(arguments);
            for (int i = 0; i < columns; i++)
            {
                arguments[header.Length + 3 * i] = R(rgb);
                arguments[header.Length + 3 * i + 1] = G(rgb);
                arguments[header.Length + 3 * i + 2] = B(rgb);
            }
            return arguments;
        }

        /// <summary>A header and a color per column, for a frame row whose
        /// lights differ (#557).</summary>
        private static byte[] WithColors(ReadOnlySpan<byte> header, ReadOnlySpan<int> colors)
        {
            var arguments = new byte[header.Length + 3 * colors.Length];
            header.CopyTo(arguments);
            for (int i = 0; i < colors.Length; i++)
            {
                arguments[header.Length + 3 * i] = R(colors[i]);
                arguments[header.Length + 3 * i + 1] = G(colors[i]);
                arguments[header.Length + 3 * i + 2] = B(colors[i]);
            }
            return arguments;
        }

        // ─────────────────────────────────────────────
        //  Extended matrix effect (class 0F)
        // ─────────────────────────────────────────────

        /// <summary>0F/02 [00 LED 01 00 00 01 R G B]: the static effect on
        /// one LED, not stored. openrazer's capture of it with storage 01
        /// is "00 3f 0000 00 09 0f 02 010501000001ff0000"
        /// (razerchromacommon.c:507 and 511-520).</summary>
        internal static byte[] ExtendedStatic(byte transactionId, byte led, int rgb)
            => Report(transactionId, 0x0F, 0x02, stackalloc byte[] { 0x00, led, 0x01, 0x00, 0x00, 0x01, R(rgb), G(rgb), B(rgb) });

        /// <summary>0F/02 [00 LED 03 00 00 00]: the spectrum effect, the
        /// hand-back where the device does not say what it showed
        /// (razerchromacommon.c:603-608).</summary>
        internal static byte[] ExtendedSpectrum(byte transactionId, byte led)
            => Report(transactionId, 0x0F, 0x02, stackalloc byte[] { 0x00, led, 0x03, 0x00, 0x00, 0x00 });

        /// <summary>0F/02 [00 LED effect flags rate count colors]: any
        /// effect in the one argument layout the command has, for writing
        /// back what <see cref="StoredEffectRead"/> gave. The size is 6 and
        /// three for each color (OpenRGB RazerController.cpp:1034-1056).</summary>
        internal static byte[] ExtendedEffect(byte transactionId, byte led, byte effect, byte flags, byte rate,
            ReadOnlySpan<byte> colors)
        {
            var arguments = new byte[6 + colors.Length];
            arguments[1] = led;
            arguments[2] = effect;
            arguments[3] = flags;
            arguments[4] = rate;
            arguments[5] = (byte)(colors.Length / 3);
            colors.CopyTo(arguments.AsSpan(6));
            return Report(transactionId, 0x0F, 0x02, arguments);
        }

        /// <summary>0F/84 [01 LED 00]: the brightness read
        /// (razerchromacommon.c:729-739).</summary>
        internal static byte[] ExtendedBrightnessRead(byte transactionId, byte led)
            => Report(transactionId, 0x0F, 0x84, stackalloc byte[] { 0x01, led, 0x00 });

        /// <summary>0F/81 [LED] at size 50: the effects one lighting region
        /// takes, as OpenRGB asks it (RazerController.cpp:1244-1248 at
        /// c6fc8016). A read: the command ID has its top bit set.</summary>
        internal static byte[] RegionEffectsRead(byte transactionId, byte led)
            => Report(transactionId, 0x0F, 0x81, 0x50, stackalloc byte[] { led });

        /// <summary>0F/80 with no argument and size 50: the device's list
        /// of lighting regions (OpenRGB RazerController.cpp:1221).</summary>
        internal static byte[] RegionListRead(byte transactionId)
            => Report(transactionId, 0x0F, 0x80, 0x50, ReadOnlySpan<byte>.Empty);

        /// <summary>0F/82 [01 LED] with size 0C: the stored effect of one
        /// LED. One Synapse capture shows it, "0f 82
        /// 010500000000000000000000", beside the 0F/02 it went with
        /// (openrazer wiki Unknown-commands.md:71-72). No project sends it,
        /// so its reply is believed only where it passes
        /// <see cref="TryStoredEffect"/>.</summary>
        internal static byte[] StoredEffectRead(byte transactionId, byte led)
            => Report(transactionId, 0x0F, 0x82, 0x0C, stackalloc byte[] { 0x01, led });

        // ─────────────────────────────────────────────
        //  Extended frame of one row
        // ─────────────────────────────────────────────

        /// <summary>0F/02 [00 00 08 00 00 00]: the custom frame effect at
        /// size 6, which openrazer sends ahead of the row on these mice as
        /// "some sort of mode switcher required after initialization and
        /// before color switching" (razermouse_driver.c:1139-1147).</summary>
        internal static byte[] ExtendedFrameMode(byte transactionId)
            => Report(transactionId, 0x0F, 0x02, stackalloc byte[] { 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 });

        /// <summary>0F/03 [00 00 00 00 stop R G B ...]: row 0 from column
        /// 0 through the last LED, every LED one color, at its exact size.
        /// Three LEDs on the Naga mice (razerchromacommon.c:1444-1464) and
        /// two on the Basilisk Mobile (1422-1439). The fifth argument is
        /// the stop column of the frame row both builders spell out
        /// (razerchromacommon.c:751-776).</summary>
        internal static byte[] ExtendedFrameRow(byte transactionId, int leds, int rgb)
            => Report(transactionId, 0x0F, 0x03,
                WithColors(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00, (byte)(leds - 1) }, leds, rgb));

        /// <summary>0F/03 [00 00 row 00 stop R G B ...]: one row of an
        /// extended matrix frame from column 0, a color per column, at its
        /// exact size, 5 and three for each column. OpenRGB sets the row,
        /// the start and the stop column at arguments 2 to 4 and declares
        /// the row's length plus 5 (RazerController.cpp:793-810 at
        /// c6fc8016). openrazer's builder puts them at the same places
        /// (razerchromacommon.c:751-776) and declares 0x47 for most
        /// products, which is the same size for a row of 22 columns.</summary>
        internal static byte[] ExtendedMatrixRow(byte transactionId, int row, ReadOnlySpan<int> colors)
            => Report(transactionId, 0x0F, 0x03,
                WithColors(stackalloc byte[] { 0x00, 0x00, (byte)row, 0x00, (byte)(colors.Length - 1) }, colors));

        /// <summary>0F/02 [00 00 08] at size 0C: the custom frame effect,
        /// which shows the rows sent before it and stores nothing. Both
        /// projects build it so (OpenRGB RazerController.cpp:931-940 at
        /// c6fc8016, openrazer razerchromacommon.c:703-706).</summary>
        internal static byte[] ExtendedCustom(byte transactionId)
            => Report(transactionId, 0x0F, 0x02, 0x0C, stackalloc byte[] { 0x00, 0x00, 0x08 });

        // ─────────────────────────────────────────────
        //  Standard matrix (class 03)
        // ─────────────────────────────────────────────

        /// <summary>03/0B [FF row 00 stop R G B ...]: one row of the
        /// frame, from column 0, at its exact size. openrazer declares 46
        /// whatever the row holds and OpenRGB the bytes sent
        /// (razerchromacommon.c:450-472, RazerController.cpp:812-830): the
        /// exact size is what both send for a full 22-column row.</summary>
        internal static byte[] StandardRow(byte transactionId, int row, int columns, int rgb)
            => Report(transactionId, 0x03, 0x0B,
                WithColors(stackalloc byte[] { 0xFF, (byte)row, 0x00, (byte)(columns - 1) }, columns, rgb));

        /// <summary>03/0B [FF row 00 stop R G B ...] with a color per
        /// column (#557), the row OpenRGB sends in its Direct mode
        /// (RazerController.cpp:812-830 at c6fc8016).</summary>
        internal static byte[] StandardRow(byte transactionId, int row, ReadOnlySpan<int> colors)
            => Report(transactionId, 0x03, 0x0B,
                WithColors(stackalloc byte[] { 0xFF, (byte)row, 0x00, (byte)(colors.Length - 1) }, colors));

        /// <summary>03/0C [00 stop R G B ...]: a frame that is one line of
        /// LEDs. Both projects declare size 32 whatever the line holds
        /// (razerchromacommon.c:1019-1036, RazerController.cpp:777-791).</summary>
        internal static byte[] StandardLine(byte transactionId, int columns, int rgb)
            => Report(transactionId, 0x03, 0x0C, 0x32, WithColors(stackalloc byte[] { 0x00, (byte)(columns - 1) }, columns, rgb));

        /// <summary>03/0C [00 stop R G B ...] at size 32 with a color per
        /// LED (#557), as OpenRGB sends a linear frame
        /// (RazerController.cpp:777-791 at c6fc8016).</summary>
        internal static byte[] StandardLine(byte transactionId, ReadOnlySpan<int> colors)
            => Report(transactionId, 0x03, 0x0C, 0x32, WithColors(stackalloc byte[] { 0x00, (byte)(colors.Length - 1) }, colors));

        /// <summary>03/0A [05 00]: shows the frame the rows filled. The
        /// second byte is the storage byte, 00 (razerchromacommon.c:390-396,
        /// RazerController.cpp:942-950).</summary>
        internal static byte[] StandardShow(byte transactionId)
            => Report(transactionId, 0x03, 0x0A, stackalloc byte[] { 0x05, 0x00 });

        /// <summary>03/0A [06 R G B]: the static effect. It has no storage
        /// byte (razerchromacommon.c:274-282), so it is sent only where a
        /// product has no frame, and only for a color that held.</summary>
        internal static byte[] StandardStatic(byte transactionId, int rgb)
            => Report(transactionId, 0x03, 0x0A, stackalloc byte[] { 0x06, R(rgb), G(rgb), B(rgb) });

        /// <summary>03/0A [04]: the spectrum effect
        /// (razerchromacommon.c:246-249).</summary>
        internal static byte[] StandardSpectrum(byte transactionId)
            => Report(transactionId, 0x03, 0x0A, stackalloc byte[] { 0x04 });

        /// <summary>03/83 [01 LED 00]: an LED's brightness
        /// (razerchromacommon.c:195-202).</summary>
        internal static byte[] StandardBrightnessRead(byte transactionId, byte led)
            => Report(transactionId, 0x03, 0x83, stackalloc byte[] { 0x01, led, 0x00 });

        /// <summary>0E/84 [01 00]: a laptop keyboard's brightness
        /// (razerchromacommon.c:1008-1014).</summary>
        internal static byte[] LaptopBrightnessRead(byte transactionId)
            => Report(transactionId, 0x0E, 0x84, stackalloc byte[] { 0x01, 0x00 });

        // ─────────────────────────────────────────────
        //  A keyboard's region (#557)
        // ─────────────────────────────────────────────

        /// <summary>00/86 with no argument at size 00: the keyboard's layout
        /// and variant, as OpenRGB asks it under the product's own ID
        /// (RazerController.cpp:1308-1320 at c6fc8016, RazerController.h:61).
        /// openrazer asks the same command at size 02 under FF
        /// (razerkbd_driver.c:510-511 at 477f4d6d). A read: the command ID
        /// has its top bit set.</summary>
        internal static byte[] KeyboardInfoRead(byte transactionId)
            => Report(transactionId, 0x00, 0x86, 0x00, ReadOnlySpan<byte>.Empty);

        /// <summary>
        /// The region a keyboard's reply to <see cref="KeyboardInfoRead"/>
        /// names, as OpenRGB lays the keyboard out for it: its first argument
        /// is the layout (RazerController.cpp:1318), whose type
        /// GetKeyboardLayoutType gives (1322-1364, the values at
        /// RazerController.h:117-153), and the controller builds ISO QWERTY
        /// for ISO, ISO AZERTY for French, ISO QWERTZ for German, and ANSI
        /// QWERTY for ANSI, for JIS and for a layout it does not know
        /// (RGBController_Razer.cpp:238-258). Null for a reply that is not a
        /// success: the keyboard is laid out ANSI, as OpenRGB lays out one
        /// whose answer it cannot read.
        /// </summary>
        internal static HidFamilies.KeyboardLayout? RegionOf(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < BufferLength || reply[StatusAt] != StatusSuccess) return null;
            return reply[ArgumentsAt] switch
            {
                // UK, Nordic, Greek, Korean, Brazilian, Latin American
                // Spanish, Swiss, Spanish, Italian, Portuguese, Hebrew.
                2 or 6 or 7 or 9 or 13 or 14 or 15 or 16 or 17 or 18 or 19 => HidFamilies.KeyboardLayout.IsoQwerty,
                // French.
                4 => HidFamilies.KeyboardLayout.IsoAzerty,
                // German.
                3 => HidFamilies.KeyboardLayout.IsoQwertz,
                // US, Russian, Chinese Traditional, Turkish, Thai, Arabic,
                // Japanese, and any other.
                _ => HidFamilies.KeyboardLayout.AnsiQwerty,
            };
        }

        // ─────────────────────────────────────────────
        //  A wireless receiver (#557)
        // ─────────────────────────────────────────────

        /// <summary>The most devices a receiver's list is read for
        /// (OpenRGB RazerController.h:174 at c6fc8016).</summary>
        internal const int MaxPaired = 8;

        /// <summary>How long a report relayed to a device behind a
        /// receiver waits before each read of its reply, and how many
        /// reads it gets. A receiver answers busy while it relays and drops
        /// a report sent meanwhile, so OpenRGB reads the reply every 2 ms
        /// until it is final, at most 50 times (RazerController.cpp:2098-2124
        /// at c6fc8016).</summary>
        internal const int RelayPollUs = 2000;
        internal const int RelayPolls = 50;

        /// <summary>00/BF with no argument and size 50: the receiver's
        /// pairing slots (OpenRGB RazerController.cpp:1283-1284 and
        /// RazerController.h:62 at c6fc8016). A read: the command ID has
        /// its top bit set, and no device-mode report is ever built
        /// here.</summary>
        internal static byte[] PairedDevicesRead(byte transactionId)
            => Report(transactionId, 0x00, 0xBF, 0x50, ReadOnlySpan<byte>.Empty);

        /// <summary>
        /// The product IDs a receiver's reply to
        /// <see cref="PairedDevicesRead"/> lists, the receiver's own left
        /// out: a slot count, then for each slot a connection status and a
        /// big-endian product ID, FFFF for an empty slot. A paired device
        /// is listed whether or not it is connected, since a sleeping mouse
        /// reconnects only once it is used (OpenRGB
        /// RazerController.cpp:1276-1306 and RazerControllerDetect.cpp:54-60
        /// at c6fc8016). Empty for a reply that is not a success, which is
        /// what a product that is no receiver answers.
        /// </summary>
        internal static ushort[] PairedProductIds(ReadOnlySpan<byte> reply, ushort own)
        {
            if (reply.Length < BufferLength || reply[StatusAt] != StatusSuccess) return Array.Empty<ushort>();
            var arguments = reply.Slice(ArgumentsAt, MaxArguments);
            int slots = arguments[0];
            // The first eight slots that hold a device are read, the
            // receiver's own included, and that one is left out after.
            var listed = new List<ushort>();
            for (int slot = 0; slot < slots && slot * 3 + 3 < MaxArguments && listed.Count < MaxPaired; slot++)
            {
                var pid = (ushort)((arguments[slot * 3 + 2] << 8) | arguments[slot * 3 + 3]);
                if (pid != 0x0000 && pid != 0xFFFF) listed.Add(pid);
            }
            var pids = new List<ushort>(listed.Count);
            foreach (ushort pid in listed)
                if (pid != own && !pids.Contains(pid)) pids.Add(pid);
            return pids.ToArray();
        }

        /// <summary>What a reply says about a report relayed through a
        /// receiver: as <see cref="Check"/>, but busy is not final, and
        /// the reply is read again (OpenRGB RazerController.cpp:2114-2121
        /// at c6fc8016).</summary>
        internal static RazerReply CheckRelayed(ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply)
        {
            var verdict = Check(request, reply);
            return verdict == RazerReply.Accepted && reply[StatusAt] == StatusBusy ? RazerReply.Early : verdict;
        }

        // ─────────────────────────────────────────────
        //  Mouse extended matrix (03/0D)
        // ─────────────────────────────────────────────

        /// <summary>03/0D [00 LED 06 R G B]. openrazer's capture with
        /// storage 01 is "00 3f 0000 00 06 03 0d 010106 00ff00"
        /// (razerchromacommon.c:810-820).</summary>
        internal static byte[] MouseExtendedStatic(byte transactionId, byte led, int rgb)
            => Report(transactionId, 0x03, 0x0D, stackalloc byte[] { 0x00, led, 0x06, R(rgb), G(rgb), B(rgb) });

        /// <summary>03/0D [00 LED 04]: the spectrum effect
        /// (razerchromacommon.c:826-831).</summary>
        internal static byte[] MouseExtendedSpectrum(byte transactionId, byte led)
            => Report(transactionId, 0x03, 0x0D, stackalloc byte[] { 0x00, led, 0x04 });

        // ─────────────────────────────────────────────
        //  Classic LED (03/00, 03/01, 03/02)
        // ─────────────────────────────────────────────

        internal const byte ClassicEffectStatic = 0x00;

        /// <summary>The spectrum effect of a classic LED
        /// (razercommon.h:60-65).</summary>
        internal const byte ClassicEffectSpectrum = 0x04;

        /// <summary>03/00 [00 LED state]: an LED on or off
        /// (razerchromacommon.c:83-91).</summary>
        internal static byte[] ClassicState(byte transactionId, byte led, bool on)
            => Report(transactionId, 0x03, 0x00, stackalloc byte[] { 0x00, led, (byte)(on ? 0x01 : 0x00) });

        /// <summary>03/01 [00 LED R G B] (razerchromacommon.c:119-129).</summary>
        internal static byte[] ClassicColor(byte transactionId, byte led, int rgb)
            => Report(transactionId, 0x03, 0x01, stackalloc byte[] { 0x00, led, R(rgb), G(rgb), B(rgb) });

        /// <summary>03/02 [00 LED effect] (razerchromacommon.c:148-156).</summary>
        internal static byte[] ClassicEffect(byte transactionId, byte led, byte effect)
            => Report(transactionId, 0x03, 0x02, stackalloc byte[] { 0x00, led, effect });

        /// <summary>03/80 [01 LED 00] (razerchromacommon.c:107-114).</summary>
        internal static byte[] ClassicStateRead(byte transactionId, byte led)
            => Report(transactionId, 0x03, 0x80, stackalloc byte[] { 0x01, led, 0x00 });

        /// <summary>03/81 [01 LED 00 00 00] (razerchromacommon.c:134-140).</summary>
        internal static byte[] ClassicColorRead(byte transactionId, byte led)
            => Report(transactionId, 0x03, 0x81, stackalloc byte[] { 0x01, led, 0x00, 0x00, 0x00 });

        /// <summary>03/82 [01 LED 00] (razerchromacommon.c:164-171).</summary>
        internal static byte[] ClassicEffectRead(byte transactionId, byte led)
            => Report(transactionId, 0x03, 0x82, stackalloc byte[] { 0x01, led, 0x00 });

        // ─────────────────────────────────────────────
        //  Replies
        // ─────────────────────────────────────────────

        /// <summary>
        /// What a reply says about its request. It belongs to the request
        /// when it carries the same transaction ID, command class and
        /// command ID (OpenRGB RazerController.cpp:1183-1185. openrazer
        /// compares class and ID, razermouse_driver.c:136-143). A device
        /// asked too soon still shows the reply before it, or status 00,
        /// and is read again before anything is sent again (uncoil
        /// docs/PROTOCOL.md:59-60).
        ///
        /// <para>02 and 01 both count: "Some commands respond with 'busy'
        /// but succeed" (razermouse_driver.c:145-148). 05 ends the attempt
        /// (OpenSynapse RazerFeatureTransport.cs:351-354) and 03 is tried
        /// again (355-360). 04 is a receiver answering for a mouse that is
        /// asleep or on its cable (uncoil docs/PROTOCOL.md:238). The
        /// reply's checksum is not read: neither openrazer nor OpenRGB
        /// checks it.</para>
        /// </summary>
        internal static RazerReply Check(ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply)
        {
            if (reply.Length < BufferLength || request.Length < BufferLength
                || reply[TransactionAt] != request[TransactionAt]
                || reply[ClassAt] != request[ClassAt]
                || reply[CommandAt] != request[CommandAt])
                return RazerReply.Early;
            return reply[StatusAt] switch
            {
                StatusNew => RazerReply.Early,
                StatusBusy or StatusSuccess => RazerReply.Accepted,
                StatusTimeout => RazerReply.Timeout,
                StatusNotSupported => RazerReply.NotSupported,
                _ => RazerReply.Failure,
            };
        }

        /// <summary>An LED the region list may add a paint report for: the
        /// lit regions devices name. Anything else in a list, a battery or
        /// profile LED, is not a zone to color.</summary>
        internal static bool IsLitRegion(byte led)
            => led is LedScrollWheel or LedLogo or LedBacklight or LedUnderglow or LedRightSide or LedLeftSide;

        /// <summary>
        /// The regions a device lists in its reply to
        /// <see cref="RegionListRead"/>: five-byte records that open with
        /// the region's LED ID, ended by a record whose ID is 00 (OpenRGB
        /// RazerController.cpp:1229-1237. One keyboard's whole answer is
        /// "0519030612", openrazer wiki Unknown-commands.md:46). Empty
        /// for a reply that is not a success, and for a list that fills
        /// the report without its closing zero: such a list may run past
        /// the report, and a reply that is not a region list at all looks
        /// the same.
        /// </summary>
        internal static byte[] Regions(ReadOnlySpan<byte> reply)
        {
            if (reply.Length < BufferLength || reply[StatusAt] != StatusSuccess) return Array.Empty<byte>();
            var regions = new List<byte>();
            for (int at = 0; at + 5 <= MaxArguments; at += 5)
            {
                byte led = reply[ArgumentsAt + at];
                if (led == 0x00) return regions.ToArray();
                regions.Add(led);
            }
            return Array.Empty<byte>();
        }

        /// <summary>
        /// The LEDs a product is painted on: the row's own, and one more
        /// for each lit region the device lists that the row does not
        /// name, so a zone openrazer has no attribute for is not left on
        /// the device's own effect. A row that paints LED 00 gains
        /// nothing, since 00 is the whole device there, and an LED of the
        /// row that the list lacks is still painted.
        /// </summary>
        internal static byte[] PaintLeds(RazerDirectRow row, ReadOnlySpan<byte> regions)
        {
            if (row.Family != RazerFamily.Extended || regions.Length == 0
                || Array.IndexOf(row.Leds, LedZero) >= 0)
                return row.Leds;
            var leds = new List<byte>(row.Leds);
            foreach (byte region in regions)
                if (IsLitRegion(region) && !leds.Contains(region)) leds.Add(region);
            return leds.Count == row.Leds.Length ? row.Leds : leds.ToArray();
        }

        /// <summary>
        /// The report that writes back what <see cref="StoredEffectRead"/>
        /// gave for an LED, or false where the reply cannot be trusted.
        /// No source gives the reply's layout. Every read here that has a
        /// write twin answers with the write's arguments, so the reply is
        /// taken as [01 LED effect flags rate count colors] only when all
        /// of these hold: status 02, the stored profile and the LED that
        /// were asked for, an effect the family has (00 to 0A, OpenRGB
        /// RazerController.h:175-187 and uncoil docs/PROTOCOL.md:195) that
        /// is not the custom frame, and at most two colors, the most any
        /// effect takes (razerchromacommon.c:581-597).
        /// </summary>
        internal static bool TryStoredEffect(byte transactionId, byte led, ReadOnlySpan<byte> reply, out byte[] handBack)
        {
            handBack = null;
            if (reply.Length < BufferLength || reply[StatusAt] != StatusSuccess) return false;
            var arguments = reply.Slice(ArgumentsAt, MaxArguments);
            byte effect = arguments[2], count = arguments[5];
            if (arguments[0] != 0x01 || arguments[1] != led) return false;
            if (effect > 0x0A || effect == 0x08 || count > 2) return false;
            handBack = ExtendedEffect(transactionId, led, effect, arguments[3], arguments[4], arguments.Slice(6, 3 * count));
            return true;
        }

        /// <summary>
        /// The reports that write a classic LED's own color, effect and
        /// state back, from its three reads (razer_test
        /// src/led/razerclassicled.cpp:23-65 reads the same three), or
        /// false where a reply is not a success for this LED or holds a
        /// value the writes do not take: a state above 1 or an effect
        /// above 5, the ranges openrazer clamps to
        /// (razerchromacommon.c:88 and 153).
        ///
        /// <para><paramref name="withState"/> is false for a row whose
        /// claim sets no state (<see cref="RazerRowFlags.NoState"/>, the
        /// Tartarus Chroma). No reference sends that product a state report
        /// for its backlight: OpenRGB sends it the color and the effect
        /// alone (RazerController.cpp:1990-1995), and openrazer sends 03/00
        /// to its three profile LEDs only (razerkbd_driver.c:2103-2105). So
        /// its hand-back is the color and the effect, and its state reply
        /// is not read.</para>
        /// </summary>
        internal static bool TryClassicStored(byte transactionId, byte led, ReadOnlySpan<byte> state,
            ReadOnlySpan<byte> color, ReadOnlySpan<byte> effect, bool withState, out byte[][] handBack)
        {
            handBack = null;
            if ((withState && !IsClassicReply(state, led)) || !IsClassicReply(color, led) || !IsClassicReply(effect, led))
                return false;
            byte on = withState ? state[ArgumentsAt + 2] : (byte)0, mode = effect[ArgumentsAt + 2];
            if (on > 0x01 || mode > 0x05) return false;
            int rgb = (color[ArgumentsAt + 2] << 16) | (color[ArgumentsAt + 3] << 8) | color[ArgumentsAt + 4];
            handBack = withState
                ? new[]
                {
                    ClassicColor(transactionId, led, rgb),
                    ClassicEffect(transactionId, led, mode),
                    ClassicState(transactionId, led, on == 0x01),
                }
                : new[] { ClassicColor(transactionId, led, rgb), ClassicEffect(transactionId, led, mode) };
            return true;
        }

        private static bool IsClassicReply(ReadOnlySpan<byte> reply, byte led)
            => reply.Length >= BufferLength && reply[StatusAt] == StatusSuccess && reply[ArgumentsAt + 1] == led;

        // ─────────────────────────────────────────────
        //  A row's reports
        // ─────────────────────────────────────────────

        /// <summary>The transaction IDs to try for a product, in order.</summary>
        internal static byte[] TransactionIds(RazerDirectRow row)
        {
            var ids = new byte[1 + row.OtherTransactionIds.Length];
            ids[0] = row.TransactionId;
            row.OtherTransactionIds.CopyTo(ids, 1);
            return ids;
        }

        /// <summary>The transaction IDs the claim read is asked under, in
        /// order: the one openrazer reads it under where that is none of the
        /// product's own (<see cref="RazerDirectRow.ProbeTransactionId"/>),
        /// then the product's own.</summary>
        internal static byte[] ProbeIds(RazerDirectRow row)
        {
            byte[] ids = TransactionIds(row);
            if (row.ProbeTransactionId == 0 || Array.IndexOf(ids, row.ProbeTransactionId) >= 0) return ids;
            var asked = new byte[ids.Length + 1];
            asked[0] = row.ProbeTransactionId;
            ids.CopyTo(asked, 1);
            return asked;
        }

        /// <summary>The read that opens a claim.</summary>
        internal static byte[] ProbeRead(RazerDirectRow row, byte transactionId) => row.Probe switch
        {
            RazerProbe.ExtendedBrightness => ExtendedBrightnessRead(transactionId, row.ProbeLed),
            RazerProbe.StandardBrightness => StandardBrightnessRead(transactionId, row.ProbeLed),
            RazerProbe.LaptopBrightness => LaptopBrightnessRead(transactionId),
            RazerProbe.ClassicState => ClassicStateRead(transactionId, row.ProbeLed),
            RazerProbe.RegionList => RegionListRead(transactionId),
            RazerProbe.PairedDevices => PairedDevicesRead(transactionId),
            RazerProbe.RegionEffects => RegionEffectsRead(transactionId, row.ProbeLed),
            _ => null,
        };

        /// <summary>The reports that open a claim, sent again with every
        /// re-assert: the mode report of the one-row frame, and a classic
        /// LED's state.</summary>
        internal static List<byte[]> Claim(RazerDirectRow row, byte transactionId)
        {
            var reports = new List<byte[]>();
            if (row.Family == RazerFamily.ExtendedFrame) reports.Add(ExtendedFrameMode(transactionId));
            else if (row.Family == RazerFamily.Classic && !row.Has(RazerRowFlags.NoState))
                foreach (byte led in row.Leds) reports.Add(ClassicState(transactionId, led, on: true));
            return reports;
        }

        /// <summary>
        /// The reports that paint a product one color, on
        /// <paramref name="leds"/> where its family paints by LED
        /// (<see cref="PaintLeds"/>).
        ///
        /// <para>A frame's rows and the report that shows it go out under
        /// the IDs openrazer gives them where the table names any, which
        /// holds for as long as the product answered the row's first ID:
        /// rows under 80 and the show report under FF on a wired Mamba
        /// Chroma (razermouse_driver.c:3922-3925 and 1097-1102). A product that
        /// answered another of its IDs gets that one on every report, the
        /// way OpenRGB sends one ID per product
        /// (RazerController.cpp:66 and 643).</para>
        /// </summary>
        internal static List<byte[]> Paint(RazerDirectRow row, byte transactionId, byte[] leds, int rgb)
        {
            var reports = new List<byte[]>();
            switch (row.Family)
            {
                case RazerFamily.Extended:
                    foreach (byte led in leds) reports.Add(ExtendedStatic(transactionId, led, rgb));
                    break;
                case RazerFamily.ExtendedFrame:
                    reports.Add(ExtendedFrameRow(transactionId, row.Columns, rgb));
                    break;
                case RazerFamily.MouseExtended:
                    foreach (byte led in leds) reports.Add(MouseExtendedStatic(transactionId, led, rgb));
                    break;
                case RazerFamily.Classic:
                    foreach (byte led in leds)
                    {
                        var color = ClassicColor(transactionId, led, rgb);
                        var effect = ClassicEffect(transactionId, led, ClassicEffectStatic);
                        reports.Add(row.Has(RazerRowFlags.EffectFirst) ? effect : color);
                        reports.Add(row.Has(RazerRowFlags.EffectFirst) ? color : effect);
                    }
                    break;
                case RazerFamily.Standard:
                    if (row.Has(RazerRowFlags.HeldEffect))
                    {
                        reports.Add(StandardStatic(transactionId, rgb));
                        break;
                    }
                    FrameIds(row, transactionId, out byte rows, out byte show);
                    if (row.Has(RazerRowFlags.LineRows)) reports.Add(StandardLine(rows, row.Columns, rgb));
                    else
                        for (int index = 0; index < row.Rows; index++)
                            reports.Add(StandardRow(rows, index, row.Columns, rgb));
                    reports.Add(StandardShow(show));
                    break;
                case RazerFamily.ExtendedMatrix:
                    var line = new int[row.Columns];
                    Array.Fill(line, rgb);
                    for (int index = 0; index < row.Rows; index++)
                        reports.Add(ExtendedMatrixRow(transactionId, index, line));
                    reports.Add(ExtendedCustom(transactionId));
                    break;
            }
            return reports;
        }

        /// <summary>The IDs a standard matrix frame's rows and its show
        /// report go out under (<see cref="Paint(RazerDirectRow, byte, byte[], int)"/>).</summary>
        private static void FrameIds(RazerDirectRow row, byte transactionId, out byte rows, out byte show)
        {
            bool first = transactionId == row.TransactionId;
            rows = first && row.RowTransactionId != 0 ? row.RowTransactionId : transactionId;
            show = first && row.ShowTransactionId != 0 ? row.ShowTransactionId : transactionId;
        }

        /// <summary>
        /// The reports that paint a product light by light (#557): each
        /// light of <paramref name="lights"/> in its color from
        /// <paramref name="colors"/>, in the map's order, and every LED or
        /// cell the map leaves out in <paramref name="fill"/>.
        ///
        /// <para>A product painted one report per LED gets the same
        /// reports as <see cref="Paint(RazerDirectRow, byte, byte[], int)"/>,
        /// each in its own LED's color, the way openrazer paints each zone
        /// through its own attribute with the LED the attribute names
        /// (razermouse_driver.c:5185-5190 at 477f4d6d) and OpenRGB its
        /// custom products (RazerController.cpp:1501-1534 at c6fc8016).</para>
        ///
        /// <para>A product painted by frame gets the frame of the map's
        /// size with a color per cell: the rows and the show report of a
        /// standard matrix, the one row of a one-row extended frame behind
        /// the claim's mode report, or the extended matrix rows and the
        /// custom frame effect, which is also how a product painted by
        /// effect one color at a time is painted light by light. OpenRGB
        /// sends every frame so, a row at a time from row 0, then the
        /// report that shows it (RazerController.cpp:207-258 at
        /// c6fc8016).</para>
        /// </summary>
        internal static List<byte[]> PaintLights(RazerDirectRow row, RazerLightRow plan, IReadOnlyList<LightInfo> lights,
            byte transactionId, byte[] leds, IReadOnlyList<int> colors, int fill)
        {
            if (plan.Plan == RazerLightPlan.PerLed)
            {
                var reports = new List<byte[]>();
                foreach (byte led in leds)
                {
                    var one = Paint(row, transactionId, new[] { led }, ColorAt(lights, colors, led, fill));
                    reports.AddRange(one);
                }
                return reports;
            }

            int rowCount = plan.Rows, columns = plan.Columns;
            var grid = new int[rowCount * columns];
            Array.Fill(grid, fill & 0xFFFFFF);
            for (int i = 0; i < lights.Count && i < colors.Count; i++)
            {
                int address = lights[i].Address;
                if ((uint)address < (uint)grid.Length) grid[address] = colors[i] & 0xFFFFFF;
            }
            var frame = new List<byte[]>();
            switch (row.Family)
            {
                case RazerFamily.Standard:
                    FrameIds(row, transactionId, out byte rowsId, out byte showId);
                    if (row.Has(RazerRowFlags.LineRows)) frame.Add(StandardLine(rowsId, grid.AsSpan(0, columns)));
                    else
                        for (int index = 0; index < rowCount; index++)
                            frame.Add(StandardRow(rowsId, index, grid.AsSpan(index * columns, columns)));
                    frame.Add(StandardShow(showId));
                    break;
                case RazerFamily.ExtendedFrame:
                    frame.Add(ExtendedMatrixRow(transactionId, 0, grid.AsSpan(0, columns)));
                    break;
                default:
                    for (int index = 0; index < rowCount; index++)
                        frame.Add(ExtendedMatrixRow(transactionId, index, grid.AsSpan(index * columns, columns)));
                    frame.Add(ExtendedCustom(transactionId));
                    break;
            }
            return frame;
        }

        /// <summary>The color of the light at <paramref name="address"/>,
        /// or <paramref name="fill"/> where the map has none there.</summary>
        private static int ColorAt(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors, int address, int fill)
        {
            for (int i = 0; i < lights.Count && i < colors.Count; i++)
                if (lights[i].Address == address) return colors[i] & 0xFFFFFF;
            return fill & 0xFFFFFF;
        }

        /// <summary>The paint of a fallback, under the ID the product
        /// answered.</summary>
        internal static List<byte[]> Paint(RazerFallback fallback, byte transactionId, int rgb)
        {
            var reports = new List<byte[]>();
            switch (fallback.Kind)
            {
                case RazerFallbackKind.MatrixRow:
                    reports.Add(StandardRow(transactionId, 0, fallback.Columns, rgb));
                    reports.Add(StandardShow(transactionId));
                    break;
                case RazerFallbackKind.LineRow:
                    reports.Add(StandardLine(transactionId, fallback.Columns, rgb));
                    reports.Add(StandardShow(transactionId));
                    break;
                case RazerFallbackKind.Effect:
                    reports.Add(StandardStatic(transactionId, rgb));
                    break;
            }
            return reports;
        }

        /// <summary>The reads made before a claim's first write, for what
        /// the device showed: the stored effect of each painted LED on the
        /// extended families, and a classic LED's state, color and effect,
        /// in that order.</summary>
        internal static List<byte[]> StoredReads(RazerDirectRow row, byte transactionId, byte[] leds)
        {
            var reports = new List<byte[]>();
            if (row.Family is RazerFamily.Extended or RazerFamily.ExtendedFrame or RazerFamily.ExtendedMatrix)
                foreach (byte led in leds) reports.Add(StoredEffectRead(transactionId, led));
            else if (row.Family == RazerFamily.Classic)
                foreach (byte led in leds)
                {
                    reports.Add(ClassicStateRead(transactionId, led));
                    reports.Add(ClassicColorRead(transactionId, led));
                    reports.Add(ClassicEffectRead(transactionId, led));
                }
            return reports;
        }

        /// <summary>The spectrum effect on one LED of a family that has
        /// the report, or null. Spectrum is the openrazer daemon's default
        /// effect for a zone, and what it sets when a saved effect has no
        /// method on the device (daemon hardware/device_base.py:102 and
        /// 473-482).</summary>
        internal static byte[] Spectrum(RazerDirectRow row, byte transactionId, byte led) => row.Family switch
        {
            RazerFamily.Extended or RazerFamily.ExtendedFrame or RazerFamily.ExtendedMatrix => ExtendedSpectrum(transactionId, led),
            RazerFamily.MouseExtended => MouseExtendedSpectrum(transactionId, led),
            RazerFamily.Classic => ClassicEffect(transactionId, led, ClassicEffectSpectrum),
            _ => null,
        };

        /// <summary>
        /// The hand-back that needs no read: the spectrum effect on each
        /// LED the table lists for it, or the standard matrix spectrum on
        /// the one product painted by effect that has it. Nothing for a
        /// product painted by frame. A frame stores nothing, and the one
        /// report that would replace it with an effect of the device's
        /// own, 03/0A [04], has no storage byte and may be kept for good.
        /// </summary>
        internal static List<byte[]> SpectrumBack(RazerDirectRow row, byte transactionId)
        {
            var reports = new List<byte[]>();
            if (row.Family == RazerFamily.Standard)
            {
                if (row.Has(RazerRowFlags.SpectrumBack)) reports.Add(StandardSpectrum(transactionId));
                return reports;
            }
            foreach (byte led in row.SpectrumLeds) reports.Add(Spectrum(row, transactionId, led));
            return reports;
        }

        // ─────────────────────────────────────────────
        //  The control collection
        // ─────────────────────────────────────────────

        /// <summary>The collection number in an interface path, from
        /// "&amp;col" in its device segment, or 0 for a path without one,
        /// an interface with a single collection.</summary>
        internal static int CollectionNumberOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            int start = path.IndexOf('#');
            int end = start < 0 ? -1 : path.IndexOf('#', start + 1);
            string segment = start >= 0 && end > start ? path.Substring(start + 1, end - start - 1) : path;
            int at = segment.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0;
            int value = 0, read = 0;
            for (int i = at + 4; i < segment.Length && Uri.IsHexDigit(segment[i]); i++)
            {
                if (++read > 4) return 0;
                value = (value << 4) | Uri.FromHex(segment[i]);
            }
            return value;
        }

        /// <summary>
        /// Where a collection stands among the ones a product could be
        /// driven through, smaller first. The control collection is the one
        /// whose feature report is 91 bytes, and its usage varies, so
        /// nothing is chosen by usage alone: the interface the table names
        /// comes first, on it the usage the table names, then the lower
        /// interface and the lower collection number, the order
        /// openrazer-win tries them in (openrazer_win/core/transport.py:84-89).
        /// </summary>
        internal static int CollectionRank(RazerDirectRow row, HidCollectionInfo collection)
        {
            int named = 2 * row.Collections.Length;
            for (int i = 0; i < row.Collections.Length; i++)
            {
                var wanted = row.Collections[i];
                if (wanted.Interface >= 0 && wanted.Interface != collection.InterfaceNumber) continue;
                bool usage = wanted.UsagePage == collection.UsagePage && wanted.Usage == collection.Usage;
                named = Math.Min(named, 2 * i + (usage ? 0 : 1));
            }
            // An interface number is at most 255 and so is a collection
            // number on any device here, so the three never overlap.
            int iface = collection.InterfaceNumber < 0 ? 0xFF : Math.Min(collection.InterfaceNumber, 0xFF);
            return (named << 16) | (iface << 8) | Math.Min(CollectionNumberOf(collection.Path), 0xFF);
        }
    }
}
