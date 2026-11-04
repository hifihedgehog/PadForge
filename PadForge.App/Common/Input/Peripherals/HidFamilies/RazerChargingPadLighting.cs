using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Razer Charging Pad Chroma (0F26), lit as OpenRGB's RazerController
    /// lights it (#557), on the consumer collection of interface 0
    /// (RazerControllerDetect.cpp:465, read into <see cref="RazerHidTable"/>).
    ///
    /// <para>Why a family of the shared worker. The pad takes the 90-byte
    /// report the Razer worker speaks, but both references switch it into
    /// the driver's device mode before a frame and out of it again for its
    /// own lighting, and the Razer worker's products have no such step: its
    /// rows, the claim and the hand-back it sends are built from fields
    /// (<see cref="RazerDirectRow"/>, <see cref="RazerRowFlags"/>) that hold
    /// no device mode. A session of the shared worker holds a step before
    /// the first picture and a hand-back of its own, with the crash path's
    /// frames, so the pad is lit here and the Razer worker's table leaves it
    /// out, as it did. The reports are the Razer worker's own builders
    /// (<see cref="RazerDirectProtocol"/>), sent as OpenRGB sends them
    /// (<see cref="RazerFeatureLink"/>).</para>
    ///
    /// <para>The device mode. OpenRGB switches the pad into device mode 03,
    /// software, as soon as it opens it: 00/04 [03 00] at size 2 under the
    /// product's transaction ID 1F (RazerController.cpp:68-77, 832-840 and
    /// 1572-1576, RazerDevices.cpp:9590-9609). openrazer sends the same mode
    /// before each frame, "Must be in driver mode for custom effects"
    /// (razeraccessory_driver.c:1138-1145), under 3F
    /// (razeraccessory_driver.c:112-121). A session sends it once, before
    /// its first picture, as OpenRGB does.</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode on an extended matrix
    /// product: the one row of ten colors, 0F/03 [00 00 00 00 09 RGB...] at
    /// size 35, then the custom frame effect 0F/02 [00 00 08] at size 0C,
    /// 1 ms before each (SetLEDs, RazerController.cpp:207-258, 793-810,
    /// 931-940 and 1735-1750). openrazer frames the pad's row the same way
    /// under 1F (razeraccessory_driver.c:1138-1145). The ten lights are the
    /// LED strip zone (RazerDevices.cpp:9574-9609).</para>
    ///
    /// <para>The hand-back is openrazer's: device mode 00, normal, "Must be
    /// in normal mode for hardware effects" (razeraccessory_driver.c:704-707),
    /// 00/04 [00 00] under 3F (lines 112-121, razerchromacommon.c:25-37). The
    /// pad then shows the effect it holds of its own. OpenRGB never sends
    /// it: it leaves the pad in software mode. The frames and the effect
    /// report store nothing (storage byte 00, RazerController.cpp:931-940).</para>
    ///
    /// <para>The pad is Razer's, so the linker's Razer row reaches it and
    /// closes its path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).</para>
    /// </summary>
    internal sealed class RazerChargingPadLighting : IHidLightingFamily
    {
        public static readonly RazerChargingPadLighting Instance = new();

        internal const string Detector = "DetectRazerControllers";

        /// <summary>The device modes (RazerController.h:37-41).</summary>
        internal const byte DeviceModeHardware = 0x00;
        internal const byte DeviceModeSoftware = 0x03;

        /// <summary>The transaction ID openrazer sends a device mode under
        /// (razeraccessory_driver.c:118).</summary>
        internal const byte OpenrazerModeTransaction = 0x3F;

        /// <summary>The wait before the row and before the effect
        /// (RazerController.cpp:242 and 247).</summary>
        internal const int RowWaitMs = 1;

        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer Charging Pad";

        private readonly Dictionary<string, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null || collection.FeatureReportLength < RazerDirectProtocol.BufferLength) return null;
            lock (_models)
            {
                if (!_models.TryGetValue(registration.Name, out var model))
                    _models[registration.Name] = model = Model(registration.Name, collection.ProductId);
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId)
        {
            var device = RazerHidTable.Device(productId);
            byte transaction = device?.TransactionId ?? 0x1F;
            int columns = device?.Columns ?? 1;
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = RazerHidTable.MapOf(productId),
                RepaintMs = RepaintMs,
                Begin = (io, wait) => io == null ? null : new Session(transaction, columns),
            };
        }

        /// <summary>razer_create_device_mode_report (RazerController.cpp:832-840).</summary>
        internal static byte[] DeviceMode(byte transaction, byte mode)
            => RazerDirectProtocol.Report(transaction, 0x00, 0x04, 2, stackalloc byte[] { mode, 0x00 });

        /// <summary>The hand-back, openrazer's normal mode.</summary>
        internal static byte[] HandBackReport() => DeviceMode(OpenrazerModeTransaction, DeviceModeHardware);

        /// <summary>One picture's row, colors from the picture's first, a
        /// color it lacks black.</summary>
        internal static byte[] Row(byte transaction, int columns, IReadOnlyList<int> colors)
        {
            var row = new int[columns];
            for (int i = 0; i < columns; i++) row[i] = colors != null && i < colors.Count ? colors[i] : 0;
            return RazerDirectProtocol.ExtendedMatrixRow(transaction, 0, row);
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly byte _transaction;
            private readonly int _columns;
            private bool _software;
            private byte[][] _crash = Array.Empty<byte[]>();

            public Session(byte transaction, int columns)
            {
                _transaction = transaction;
                _columns = columns;
            }

            public override bool Feature => true;

            /// <summary>The normal mode, once the software mode may have gone
            /// out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_software)
                {
                    _crash = new[] { HandBackReport() };
                    var mode = RazerFeatureLink.Send(io, DeviceMode(_transaction, DeviceModeSoftware), RazerGuard.CommandWaitMs);
                    if (mode == null) return Later();
                    if (mode == false) return HidLightingPaint.Failed;
                    _software = true;
                }
                wait?.Invoke(RowWaitMs);
                var row = RazerFeatureLink.Send(io, Row(_transaction, _columns, colors), RazerGuard.PaintWaitMs);
                if (row == null) return Later();
                if (row == false) return HidLightingPaint.Failed;
                wait?.Invoke(RowWaitMs);
                var show = RazerFeatureLink.Send(io, RazerDirectProtocol.ExtendedCustom(_transaction), RazerGuard.PaintWaitMs);
                if (show == null) return Later();
                return show == true ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            private HidLightingPaint Later()
            {
                LaterMs = RazerGuard.PaintWaitMs;
                return HidLightingPaint.Later;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                foreach (var report in _crash)
                    if (RazerFeatureLink.Send(io, report, RazerGuard.CommandWaitMs) != true) return false;
                _software = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
