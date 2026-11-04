using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The MNT Reform and Pocket Reform keyboards (#557), lit key by key as
    /// OpenRGB's MNTKeyboardController lights them: 1209:6D02 and 1209:6D06
    /// on interface 0, usage page 1, usage 6, the keyboard collection itself
    /// (MNTKeyboardControllerDetect.cpp:19-20 and 50-51; MNTKeyboardController.h:28-31).
    /// Their keys are OpenRGB's led_names of each board in a 6 by 14 and a 6
    /// by 12 grid, each addressed by its place in the grid
    /// (RGBController_MNTReformKeyboard.cpp:21-121,
    /// RGBController_MNTPocketReformKeyboard.cpp:21-111; SetupZones,
    /// RGBController_MNTKeyboard.cpp:35-49).
    ///
    /// <para>The model of the device. A picture is one output report per
    /// row, "xXRGB" (its first byte, 'x', is the report ID), the row's
    /// number, then each key of the row as blue, green and red
    /// (SendColorMatrix, MNTKeyboardController.cpp:19-32; DeviceUpdateLEDs,
    /// RGBController_MNTKeyboard.cpp:51-64; CMD_PREFIX and CMD_OFFSET,
    /// MNTKeyboardController.h:24-26). OpenRGB opens the keyboard by setting
    /// every key white (CommonInit, lines 14-26), which PadForge's first
    /// picture takes the place of. The keyboard keeps no mode, so there is
    /// nothing to hand back, and it shows PadForge's last picture until its
    /// firmware or another program sets one. No source names a program that
    /// lights it.</para>
    ///
    /// <para>Windows. These keyboards cannot be lit on Windows, by OpenRGB
    /// or PadForge. The keyboard's firmware takes the colors only as an
    /// output report, report type 2 with report ID 'x', and ignores a
    /// feature report (tud_hid_set_report_cb, MNT reform
    /// reform2-keyboard4-fw/src/main.c:589-662, at 9dcb9a8). OpenRGB writes
    /// that output report with hid_write on the keyboard collection
    /// (SendColorMatrix, MNTKeyboardController.cpp:19-32). Windows holds a
    /// keyboard collection for itself and opens it for another program with
    /// no access right only, on which only feature reports pass (hidapi
    /// windows/hid.c:1009-1026), so the output report never leaves the PC.
    /// The firmware's descriptor also declares no report 'x' at all, only
    /// the keyboard's, the consumer controls' and the trackpad's
    /// (desc_hid_report, usb_descriptors.c:296-302). The model does not ask
    /// the worker to keep a handle with no access right
    /// (<see cref="HidLightingModel.HeldByWindows"/>), which would carry no
    /// output report, so the worker lists the unit asleep. Lighting them on
    /// Windows needs firmware that declares a collection of its own, on a
    /// vendor usage page, whose output or feature report carries the
    /// "xXRGB" rows: Windows opens such a collection for read and write.</para>
    /// </summary>
    internal sealed class MntKeyboardLighting : IHidLightingFamily
    {
        public static readonly MntKeyboardLighting Instance = new();

        internal const string OpenRgbFamily = "MNTKeyboardController";

        /// <summary>KBD_VID and the two PIDs (MNTKeyboardController.h:28,
        /// MNTKeyboardControllerDetect.cpp:19-20).</summary>
        internal const ushort VendorId = 0x1209;
        internal const ushort ReformPid = 0x6D02;
        internal const ushort PocketReformPid = 0x6D06;

        /// <summary>KBD_ROWS and the column counts (MNTKeyboardController.h:21,
        /// MNTReformKeyboardController.h:16, MNTPocketReformKeyboardController.h:16).</summary>
        internal const int Rows = 6;
        internal const int ReformCols = 14;
        internal const int PocketReformCols = 12;

        /// <summary>"xXRGB" and the row byte after it (MNTKeyboardController.h:24-26).</summary>
        internal static ReadOnlySpan<byte> Prefix => "xXRGB"u8;
        internal const int RowAt = 5;
        internal const int DataAt = 6;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "MNT";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            bool pocket = collection.ProductId == PocketReformPid;
            int cols = pocket ? PocketReformCols : ReformCols;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < DataAt + cols * 3) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId, pocket));
        }

        /// <summary>The lights of a board, each at its place in the grid.</summary>
        internal static List<LightInfo> Lights(bool pocket)
            => KeyboardLedNames.FromNames(pocket ? KeyboardLedNames.MntPocketReformNames : KeyboardLedNames.MntReformNames,
                i => i, Rows * (pocket ? PocketReformCols : ReformCols));

        private static HidLightingModel Model(string name, ushort productId, bool pocket)
        {
            int cols = pocket ? PocketReformCols : ReformCols;
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                Map = new LightMap(Lights(pocket), pocket
                    ? "OpenRGB RGBController_MNTPocketReformKeyboard.cpp"
                    : "OpenRGB RGBController_MNTReformKeyboard.cpp")
                {
                    Product = $"mnt:{VendorId:X4}:{productId:X4}",
                },
                RepaintMs = RepaintMs,
                Begin = (_, _) => new Session(cols),
            };
        }

        /// <summary>The picture's six reports, one per row.</summary>
        internal static List<byte[]> Picture(int cols, IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var grid = KeyboardLedNames.ByAddress(lights, colors, Rows * cols);
            var reports = new List<byte[]>(Rows);
            for (int row = 0; row < Rows; row++)
            {
                var report = new byte[DataAt + cols * 3];
                Prefix.CopyTo(report);
                report[RowAt] = (byte)row;
                for (int col = 0; col < cols; col++)
                {
                    int color = grid[row * cols + col];
                    report[DataAt + col * 3] = (byte)color;
                    report[DataAt + col * 3 + 1] = (byte)(color >> 8);
                    report[DataAt + col * 3 + 2] = (byte)(color >> 16);
                }
                reports.Add(report);
            }
            return reports;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly int _cols;

            public Session(int cols) => _cols = cols;

            public override bool Feature => false;

            /// <summary>Nothing to undo: see the class comment.</summary>
            public override byte[][] CrashFrames => Array.Empty<byte[]>();

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(_cols, lights, colors))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            /// <summary>Nothing to send: see the class comment.</summary>
            public override bool HandBack(IHidReportChannel io, Action<int> wait) => true;
        }
    }
}
