using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Redragon's Holtek-based mice, the M711 Cobra to the M711-FPS-1, lit as
    /// OpenRGB's RedragonController lights them (#557). The collections are
    /// OpenRGB's registrations, interface 2 and usage page 0xFFA0
    /// (RedragonControllerDetect.cpp:55-65), with feature reports of 16
    /// bytes, report ID 2 included (REDRAGON_MOUSE_REPORT_ID and _SIZE,
    /// RedragonMouseController.h:17-18).
    ///
    /// <para>What a write does. Every report is a write into the mouse's
    /// memory at an address, 02 F3, the address low byte first, the length
    /// and the data from byte 8 (SendWritePacket,
    /// RedragonMouseController.cpp:135-167), made to take effect by the
    /// apply, 02 F1 02 04 (SendMouseApply, lines 112-133). OpenRGB has no
    /// Direct mode for these mice and marks every mode as one the mouse
    /// saves on its own ("@direct :x:", "@save :robot:",
    /// MODE_FLAG_AUTOMATIC_SAVE, RGBController_RedragonMouse.cpp:14-69), so
    /// every picture lands in the mouse's stored settings. The address
    /// 0x0449 holds the color, an on byte, the speed and the mode of
    /// profile 1, and 0x002C the active profile: OpenRGB's controller makes
    /// profile 1 the active one when it opens (lines 22-25). The color
    /// records of profiles 2 to 5 follow at 0x0451 to 0x0469 in steps of 8
    /// (mouse_m908 include/m711/data.cpp:133-148, the vendor software's
    /// capture in documentation/default_m908.txt:1-14).</para>
    ///
    /// <para>A session. The first picture does what OpenRGB does when it
    /// opens the mouse and applies its Static mode: profile 1 made active
    /// and applied, then the Static mode written whole, the color, 01, the
    /// speed 0 and the mode 0x02, and applied
    /// (RedragonMouseController.cpp:22-25 and 87-106,
    /// RGBController_RedragonMouse.cpp:118-135). Each later picture writes
    /// the color alone and applies it, as OpenRGB's DeviceUpdateLEDs does
    /// (lines 98-106). Since every picture is stored, a picture is held
    /// before it goes out and pictures are spaced as the Lenovo Gen 10
    /// keyboard's stored writes are (<see cref="LenovoLegionLighting"/>).</para>
    ///
    /// <para>Hand-back. OpenRGB reads nothing back. mouse_m908 reads these
    /// mice's settings, bracketed by 02 F5 00 and 02 F5 01 as the vendor's
    /// software brackets its transfers (default_m908.txt:1 and 159): a read
    /// request, 02 F2, the address, the length, sent as feature report 2,
    /// and the answer read back as feature report 2, its data from byte 8
    /// (mouse_m711::read_and_print_settings, include/m711/readers.cpp:115-160,
    /// the requests in include/m711/data.cpp:293-303, the same in the M715,
    /// M908 and M607 tables). A session reads the active profile at 0x002C
    /// and profile 1's record at 0x0449 that way before it writes, keeps
    /// them in <see cref="KeyboardProfileBackups"/>, on disk before the
    /// first write, and the hand-back writes them back in OpenRGB's write
    /// shape: the record's first six bytes, the ones OpenRGB's Static mode
    /// writes, applied, then the profile, applied. A run that ends without
    /// its hand-back leaves that owed, and the next run writes it back once
    /// the mouse is present and unclaimed. A mouse that does not answer the
    /// read is lit all the same, with nothing to hand back, and keeps
    /// PadForge's last color stored until the user or Redragon's software
    /// sets another. No source names that software's processes, so none is
    /// in the vendor-program table.</para>
    /// </summary>
    internal sealed class RedragonMouseLighting : IHidLightingFamily
    {
        public static readonly RedragonMouseLighting Instance = new();

        internal const string OpenRgbFamily = "RedragonController";

        /// <summary>REDRAGON_MOUSE_VID (RedragonControllerDetect.cpp:20).</summary>
        internal const ushort HoltekVid = 0x04D9;

        /// <summary>REDRAGON_MOUSE_REPORT_ID and REDRAGON_MOUSE_REPORT_SIZE
        /// (RedragonMouseController.h:17-18).</summary>
        internal const byte ReportId = 0x02;
        internal const int ReportSize = 16;

        /// <summary>The active profile's address and profile 1's color
        /// record (RedragonMouseController.cpp:24 and 69).</summary>
        internal const ushort ProfileAddress = 0x002C;
        internal const ushort ColorAddress = 0x0449;

        /// <summary>REDRAGON_MOUSE_MODE_STATIC (RedragonMouseController.h:25).</summary>
        internal const byte StaticMode = 0x02;

        /// <summary>The bytes of profile 1's record mouse_m908 reads
        /// (include/m711/data.cpp:296), and the six OpenRGB's Static mode
        /// writes (RedragonMouseController.cpp:96-105).</summary>
        internal const int RecordRead = 7;
        internal const int RecordWritten = 6;

        /// <summary>The bounds of a mouse that stores every picture.</summary>
        internal const int HoldMs = LenovoLegionLighting.HoldMs;
        internal const int RepaintMs = LenovoLegionLighting.RepaintMs;

        /// <summary>How often a picture that waits for its backup to reach
        /// the settings file looks again, and how many times one run tries
        /// to hand back what an earlier run left owed, as the Sinowealth
        /// mice's.</summary>
        internal const int SavingMs = SinowealthLighting.SavingMs;
        internal const int RepairTries = SinowealthLighting.RepairTries;

        public string Name => "Redragon mice";

        private readonly Dictionary<int, HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != HoltekVid) return null;
            if (collection.FeatureReportLength < ReportSize) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            lock (_models)
            {
                if (!_models.TryGetValue(collection.ProductId, out var model))
                    _models[collection.ProductId] = model = Model(registrations[0].Name, collection.ProductId);
                return model;
            }
        }

        /// <summary>The key a mouse model's backup is kept under.</summary>
        internal static string Product(ushort productId) => $"redragon:{HoltekVid:X4}:{productId:X4}";

        internal static HidLightingModel Model(string name, ushort productId)
        {
            string product = Product(productId);
            return new HidLightingModel
            {
                Family = Instance,
                Name = name,
                // One zone, "Mouse" (RGBController_RedragonMouse.cpp:81-96).
                Map = new LightMap(SinowealthLighting.OneLight(), "OpenRGB RGBController_RedragonMouse.cpp") { Product = product },
                HoldMs = HoldMs,
                RepaintMs = RepaintMs,
                Begin = (io, wait) => Session.Begin(io, product),
                Owed = () => KeyboardProfileBackups.Get(product) != null
                             && KeyboardProfileBackups.Failures(product) < RepairTries,
                Repair = (io, _) => Session.Repair(io, product),
            };
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        private static byte[] Report(byte command, ushort address, byte length)
        {
            var report = new byte[ReportSize];
            report[0] = ReportId;
            report[1] = command;
            report[2] = (byte)address;
            report[3] = (byte)(address >> 8);
            report[4] = length;
            return report;
        }

        /// <summary>SendWritePacket: 02 F3, the address, the length, the data
        /// from byte 8 (RedragonMouseController.cpp:135-167).</summary>
        internal static byte[] Write(ushort address, ReadOnlySpan<byte> data)
        {
            var report = Report(0xF3, address, (byte)data.Length);
            data.CopyTo(report.AsSpan(8));
            return report;
        }

        /// <summary>SendMouseApply: 02 F1 02 04 (lines 112-133).</summary>
        internal static byte[] Apply()
        {
            var report = new byte[ReportSize];
            report[0] = ReportId;
            report[1] = 0xF1;
            report[2] = 0x02;
            report[3] = 0x04;
            return report;
        }

        /// <summary>The vendor software's bracket around a transfer, 02 F5 00
        /// before and 02 F5 01 after (mouse_m908 include/m711/data.cpp:294
        /// and 777).</summary>
        internal static byte[] Bracket(bool end)
        {
            var report = new byte[ReportSize];
            report[0] = ReportId;
            report[1] = 0xF5;
            report[2] = end ? (byte)0x01 : (byte)0x00;
            return report;
        }

        /// <summary>A read request, 02 F2, the address, the length
        /// (include/m711/data.cpp:295-296).</summary>
        internal static byte[] ReadRequest(ushort address, byte length) => Report(0xF2, address, length);

        /// <summary>The first picture of a session: profile 1 made active and
        /// applied, then the Static mode with the color, applied.</summary>
        internal static byte[][] EnterFrames(int rgb) => new[]
        {
            Write(ProfileAddress, stackalloc byte[] { 0x00 }),
            Apply(),
            Write(ColorAddress, stackalloc byte[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x01, 0x00, StaticMode }),
            Apply(),
        };

        /// <summary>A later picture: the color, applied
        /// (SendMouseColor, RedragonMouseController.cpp:56-70).</summary>
        internal static byte[][] ColorFrames(int rgb) => new[]
        {
            Write(ColorAddress, stackalloc byte[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb }),
            Apply(),
        };

        /// <summary>The hand-back of a backup: the record's six bytes,
        /// applied, then the profile, applied.</summary>
        internal static byte[][] RestoreFrames(byte profile, byte[] record) => new[]
        {
            Write(ColorAddress, record.AsSpan(0, RecordWritten)),
            Apply(),
            Write(ProfileAddress, stackalloc byte[] { profile }),
            Apply(),
        };

        /// <summary>One value read: the request sent, feature report 2 read
        /// back, and its <paramref name="length"/> bytes from byte 8. Null
        /// when the read failed. The write failing is told apart by
        /// <paramref name="writeFailed"/>.</summary>
        internal static byte[] ReadValue(IHidReportChannel io, ushort address, byte length, out bool writeFailed)
        {
            writeFailed = !io.SetFeature(ReadRequest(address, length));
            if (writeFailed) return null;
            var answer = new byte[Math.Max(ReportSize, io.FeatureReportLength)];
            if (!io.GetFeature(ReportId, answer)) return null;
            var value = new byte[length];
            Array.Copy(answer, 8, value, 0, length);
            return value;
        }

        private static bool SendAll(IHidReportChannel io, byte[][] frames)
        {
            foreach (var frame in frames)
                if (!io.SetFeature(frame)) return false;
            return true;
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        internal sealed class Session : HidLightingSession
        {
            private readonly string _product;
            private readonly byte[][] _restore;
            private bool _entered;

            private Session(string product, byte profile, byte[] record)
            {
                _product = product;
                Profile = profile;
                Record = record;
                _restore = record == null ? Array.Empty<byte[]>() : RestoreFrames(profile, record);
            }

            /// <summary>The active profile and profile 1's record before
            /// PadForge wrote any: an earlier run's backup where one is owed,
            /// else what the session read. A null record: the mouse did not
            /// answer, and nothing is handed back.</summary>
            public byte Profile { get; }
            public byte[] Record { get; }

            public override bool Feature => true;

            public override byte[][] CrashFrames
                => Record != null && KeyboardProfileBackups.Get(_product) != null ? _restore : Array.Empty<byte[]>();

            /// <summary>Reads what the hand-back needs, bracketed as the
            /// vendor's software brackets it. Null when a write failed, and
            /// then nothing is lit.</summary>
            public static HidLightingSession Begin(IHidReportChannel io, string product)
            {
                if (io == null) return null;
                if (KeyboardProfileBackups.Get(product) is { } owed && owed.Contents.Length == RecordWritten)
                    return new Session(product, owed.Profile, owed.Contents);
                if (!io.SetFeature(Bracket(end: false))) return null;
                var profile = ReadValue(io, ProfileAddress, 1, out bool failed);
                if (failed) return null;
                byte[] record = null;
                if (profile != null)
                {
                    var read = ReadValue(io, ColorAddress, RecordRead, out failed);
                    if (failed) return null;
                    if (read != null) record = read.AsSpan(0, RecordWritten).ToArray();
                }
                if (!io.SetFeature(Bracket(end: true))) return null;
                return new Session(product, record == null ? (byte)0 : profile[0], record);
            }

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                if (Record != null)
                {
                    if (KeyboardProfileBackups.Get(_product) == null)
                        KeyboardProfileBackups.Take(_product, Profile, Record);
                    if (!KeyboardProfileBackups.IsPersisted(_product))
                    {
                        LaterMs = SavingMs;
                        return HidLightingPaint.Later;
                    }
                }
                int rgb = CoolerMasterLighting.ByAddress(lights, colors, 1)[0];
                if (!SendAll(io, _entered ? ColorFrames(rgb) : EnterFrames(rgb))) return HidLightingPaint.Failed;
                _entered = true;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (Record == null || KeyboardProfileBackups.Get(_product) == null) return true;
                if (io == null || !SendAll(io, _restore)) return false;
                KeyboardProfileBackups.Drop(_product);
                return true;
            }

            /// <summary>Writes back what an earlier run left owed. True when
            /// nothing is owed any more.</summary>
            public static bool Repair(IHidReportChannel io, string product)
            {
                var owed = KeyboardProfileBackups.Get(product);
                if (owed == null) return true;
                if (io != null && owed.Value.Contents.Length == RecordWritten
                    && SendAll(io, RestoreFrames(owed.Value.Profile, owed.Value.Contents)))
                {
                    KeyboardProfileBackups.Drop(product);
                    return true;
                }
                KeyboardProfileBackups.NoteFailure(product);
                return false;
            }
        }
    }
}
