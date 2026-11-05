using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Kraken V4 headsets, lit as OpenRGB's RazerKrakenV4Controller
    /// lights them (#557): the Kraken V4 wired (056B) and wireless (056C) and
    /// the Kraken Kitty V3 Pro wired (0587) and wireless (0588), each on the
    /// collection of usage page FF14, usage 1, on interface 5
    /// (RazerControllerDetect.cpp:442-445, read into <see cref="RazerHidTable"/>).
    /// openrazer has no driver for them.
    ///
    /// <para>The wire. Every report is the 64-byte output report 02, a
    /// Razer report shaped like the 90-byte one: status, transaction ID,
    /// remaining packets, protocol type, data size, command class, command
    /// ID, a wireless flag, 52 arguments, the checksum and a reserved byte
    /// (RazerKrakenV4Controller.h:29-43), written with hid_write
    /// (RazerKrakenV4Controller.cpp:281-284). The report ID is 02 and the
    /// transaction ID the product's razer_device entry's, 60 for all four
    /// (cpp:45-55, RazerDevices.cpp:8659-8810). The wireless flag is 80 on
    /// the two wireless products and 00 on the wired ones (cpp:57-70). The
    /// send leaves the checksum 00: OpenRGB computes it only on the reply
    /// buffer it reads into (cpp:274-284), and PadForge sends what OpenRGB
    /// sends.</para>
    ///
    /// <para>A picture is SetDirect, OpenRGB's Direct mode, which sends no
    /// mode report (RGBController_RazerKrakenV4.cpp:99-105 and 117-126):
    /// class 0F, command 03, data size 5 and three for each column,
    /// arguments 2 and 3 zero, argument 4 the last column, and the colors
    /// from argument 5 (cpp:120-136). The lights are the razer_device zones,
    /// a ring of 9 on the Kraken V4 and the ring and the ears on the Kitty V3
    /// Pro, 10 columns in all (RazerDevices.cpp:8644-8810).</para>
    ///
    /// <para>The hand-back is OpenRGB's Wave mode, the one effect it knows
    /// for these headsets: class 00, command 00, size 5, the mode command
    /// C0 with 01 and 04 in arguments 2 and 3, then the brightness command
    /// C1 with 01 and 255 (SetModeWave, cpp:149-158, SetBrightness, 138-147,
    /// DeviceUpdateMode, RGBController_RazerKrakenV4.cpp:117-126, Wave's
    /// brightness at its maximum, lines 38-43, GetMaxBrightness,
    /// cpp:108-118). No reference reads the headset's own effect back, so a
    /// headset whose own lighting was another effect shows Wave after
    /// it. Nothing in OpenRGB says these reports are stored on the
    /// headset.</para>
    ///
    /// <para>The headsets are Razer's, so the linker's Razer row reaches
    /// them and closes their path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).
    /// The model names Synapse too, so the worker sends them nothing while
    /// it runs, and no hand-back lands on Synapse's colors.</para>
    /// </summary>
    internal sealed class RazerKrakenV4Lighting : IHidLightingFamily
    {
        public static readonly RazerKrakenV4Lighting Instance = new();

        internal const string Detector = "DetectRazerKrakenV4Controllers";

        /// <summary>The products (RazerDevices.h:246-249).</summary>
        internal const ushort KrakenV4Wireless = 0x056C;
        internal const ushort KittyV3ProWireless = 0x0588;

        /// <summary>The report ID and the report's length with it
        /// (RazerKrakenV4Controller.cpp:45-50, h:29-43).</summary>
        internal const byte ReportId = 0x02;
        internal const int ReportLength = 64;

        /// <summary>Where the fields sit in the report.</summary>
        internal const int TransactionAt = 2;
        internal const int SizeAt = 6;
        internal const int ClassAt = 7;
        internal const int CommandAt = 8;
        internal const int WirelessAt = 9;
        internal const int ArgumentsAt = 10;
        internal const int ArgumentCount = 52;

        /// <summary>The lighting commands (RazerKrakenV4Controller.h:45-51).</summary>
        internal const byte SetMode = 0xC0;
        internal const byte SetBrightness = 0xC1;
        internal const byte ModeWave = 0x04;
        internal const byte MaxBrightness = 0xFF;

        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer Kraken V4";

        private readonly Dictionary<(string, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null || collection.OutputReportLength < ReportLength) return null;
            lock (_models)
            {
                var key = (registration.Name, collection.ProductId);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration.Name, collection.ProductId);
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId)
        {
            var device = RazerHidTable.Device(productId);
            byte transaction = device?.TransactionId ?? 0x60;
            int columns = device?.Columns ?? 1;
            byte wireless = WirelessFlag(productId);
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Programs = VendorPrograms.Razer,
                Map = RazerHidTable.MapOf(productId),
                RepaintMs = RepaintMs,
                Begin = (io, wait) => io == null ? null : new Session(transaction, wireless, columns),
            };
        }

        /// <summary>The wireless flag (RazerKrakenV4Controller.cpp:57-70).</summary>
        internal static byte WirelessFlag(ushort productId)
            => productId is KrakenV4Wireless or KittyV3ProWireless ? (byte)0x80 : (byte)0x00;

        /// <summary>razer_kraken_v4_create_report with its arguments
        /// (RazerKrakenV4Controller.cpp:178-201). The checksum stays 00, as
        /// razer_usb_send leaves it (lines 281-284).</summary>
        internal static byte[] Report(byte transaction, byte wireless, byte commandClass, byte command, int dataSize,
            ReadOnlySpan<byte> arguments)
        {
            if (arguments.Length > ArgumentCount) throw new ArgumentOutOfRangeException(nameof(arguments));
            var report = new byte[ReportLength];
            report[0] = ReportId;
            report[TransactionAt] = transaction;
            report[SizeAt] = (byte)dataSize;
            report[ClassAt] = commandClass;
            report[CommandAt] = command;
            report[WirelessAt] = wireless;
            arguments.CopyTo(report.AsSpan(ArgumentsAt));
            return report;
        }

        /// <summary>SetDirect (RazerKrakenV4Controller.cpp:120-136). A color
        /// the picture lacks is black.</summary>
        internal static byte[] DirectReport(byte transaction, byte wireless, int columns, IReadOnlyList<int> colors)
        {
            var arguments = new byte[5 + 3 * columns];
            arguments[4] = (byte)(columns - 1);
            for (int i = 0; i < columns; i++)
            {
                int rgb = colors != null && i < colors.Count ? colors[i] : 0;
                arguments[5 + 3 * i] = (byte)(rgb >> 16);
                arguments[6 + 3 * i] = (byte)(rgb >> 8);
                arguments[7 + 3 * i] = (byte)rgb;
            }
            return Report(transaction, wireless, 0x0F, 0x03, 5 + 3 * columns, arguments);
        }

        /// <summary>SetModeWave and SetBrightness(255), the hand-back
        /// (cpp:138-158).</summary>
        internal static byte[][] WaveMode(byte transaction, byte wireless) => new[]
        {
            Report(transaction, wireless, 0x00, 0x00, 0x05, stackalloc byte[] { SetMode, 0x00, 0x01, ModeWave }),
            Report(transaction, wireless, 0x00, 0x00, 0x05, stackalloc byte[] { SetBrightness, 0x00, 0x01, MaxBrightness }),
        };

        internal sealed class Session : HidLightingSession
        {
            private readonly byte _transaction;
            private readonly byte _wireless;
            private readonly int _columns;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(byte transaction, byte wireless, int columns)
            {
                _transaction = transaction;
                _wireless = wireless;
                _columns = columns;
            }

            public override bool Feature => false;

            /// <summary>The Wave mode, once a picture may have gone out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (_crash.Length == 0) _crash = WaveMode(_transaction, _wireless);
                return io.WriteOutput(DirectReport(_transaction, _wireless, _columns, colors))
                    ? HidLightingPaint.Sent
                    : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                foreach (var report in _crash)
                    if (!io.WriteOutput(report)) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
