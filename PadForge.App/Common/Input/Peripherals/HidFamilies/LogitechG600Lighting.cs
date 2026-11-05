using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Logitech G600 mouse that OpenRGB's LogitechG600Controller lights
    /// (#557). It speaks no HID++: OpenRGB keeps it out of its HID++ 2.0
    /// detectors, "the G600 (page 0xFF80) ... not HID++ 2.0"
    /// (LogitechControllerDetect.cpp:1787), and PadForge's HID++ worker
    /// takes only pages 0xFF00 and 0xFF43 (HidppBackend.IsHidppLong). The
    /// collection is OpenRGB's registration, 046D:C24A on interface 1, usage
    /// page 0xFF80 (LogitechControllerDetect.cpp:83 and 1547).
    ///
    /// <para>A picture is OpenRGB's Direct mode: the feature report F1, red,
    /// green, blue, the mode 00 and the speed, 8 bytes
    /// (LogitechG600Controller.cpp:49-68, LogitechG600Controller.h:19-24).
    /// Direct leaves the speed at a mode's default, 0
    /// (RGBController_LogitechG600.cpp:35-40, RGBControllerInterface.h:339).
    /// OpenRGB offers no save for the mouse ("@save :o:",
    /// RGBController_LogitechG600.cpp:18) and sends nothing but F1.
    /// libratbag stores a color only by writing a whole profile report
    /// (driver-logitech-g600.c:477-590), which is never sent here. The one
    /// light is the side lights' zone (RGBController_LogitechG600.cpp:72-88).</para>
    ///
    /// <para>Hand-back. The mouse stores its lighting in each of its three
    /// profiles, reports F3 to F5 of 154 bytes, as red, green, blue, the
    /// effect and its duration in seconds behind the report ID, and names
    /// the active profile in the high four bits of byte 1 of the feature
    /// report F0 (libratbag src/driver-logitech-g600.c:43-53, 61-87,
    /// 194-224 and 324-399). Those five bytes are F1's five, in F1's order,
    /// the effect numbered as OpenRGB numbers F1's modes (solid 0, breathe
    /// 1, cycle 2) and the duration in OpenRGB's unit for F1's speed,
    /// seconds. So a session reads the active profile's lighting at its
    /// start, with feature reads that change nothing, and hands back by sending
    /// those bytes as F1. The mouse's profile button can change the active
    /// profile at any time, so the session reads F0 again on the model's
    /// recheck interval, and after a switch it reads the new profile's lighting
    /// for the hand-back and sends its picture once more, since no source says
    /// whether the switch overwrites F1's color. A mouse whose profile cannot
    /// be read is lit, and handed back the last lighting read or nothing.</para>
    ///
    /// <para>The mouse is Logitech's, so the linker's Logitech row lights
    /// it and leaves it to G HUB or Logitech Gaming Software while one runs
    /// (PeripheralLinker.Build). The model names both programs, so the
    /// worker lets go without the hand-back then.</para>
    /// </summary>
    internal sealed class LogitechG600Lighting : IHidLightingFamily
    {
        public static readonly LogitechG600Lighting Instance = new();

        /// <summary>OpenRGB's registration (LogitechControllerDetect.cpp:83
        /// and 1547).</summary>
        internal const ushort VendorId = PeripheralLinker.LogitechVid;
        internal const ushort ProductId = 0xC24A;
        internal const int Interface = 1;
        internal const ushort UsagePage = 0xFF80;

        /// <summary>The live lighting report and its length
        /// (LogitechG600Controller.cpp:56-67).</summary>
        internal const byte LedReportId = 0xF1;
        internal const int LedReportLength = 8;

        /// <summary>The active-profile report and the first profile report,
        /// whose length is the profile struct's with its ID
        /// (driver-logitech-g600.c:43-49 and 97-98).</summary>
        internal const byte ActiveReportId = 0xF0;
        internal const byte FirstProfileReportId = 0xF3;
        internal const int ProfileCount = 3;
        internal const int ProfileReportLength = 154;

        /// <summary>The shortest time between two pictures, PadForge's
        /// bound for a mode that saves nothing
        /// (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        /// <summary>How often a session reads the active profile again. No
        /// reference times it, so it is PadForge's, the interval of
        /// <see cref="LenovoLegionLighting.RecheckMs"/>.</summary>
        internal const int RecheckMs = LenovoLegionLighting.RecheckMs;

        public string Name => "Logitech G600";

        private static readonly Lazy<HidLightingModel> s_model = new(() => new HidLightingModel
        {
            Family = Instance,
            Name = "Logitech G600 Gaming Mouse",
            Map = new LightMap(Lights(), "OpenRGB LogitechG600Controller") { Product = "logitech:046D:C24A" },
            RepaintMs = RepaintMs,
            RecheckMs = RecheckMs,
            Programs = VendorPrograms.Logitech,
            Begin = (io, _) => Session.Begin(io),
        });

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId || collection.ProductId != ProductId) return null;
            if (collection.UsagePage != UsagePage || collection.FeatureReportLength < LedReportLength) return null;
            int bus = OpenRgbRegistrations.BusOf(collection);
            return OpenRgbRegistrations.InterfaceOf(collection, bus) == Interface ? s_model.Value : null;
        }

        /// <summary>The side lights, one zone, address 0.</summary>
        internal static IReadOnlyList<LightInfo> Lights()
            => new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } };

        /// <summary>F1 with a color, a mode and a speed.</summary>
        internal static byte[] LedFrame(byte r, byte g, byte b, byte mode, byte speed)
            => new byte[] { LedReportId, r, g, b, mode, speed, 0x00, 0x00 };

        /// <summary>The Direct picture: one color, mode 0, speed 0.</summary>
        internal static byte[] Frame(int rgb) => LedFrame((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0x00, 0x00);

        /// <summary>The active profile from the F0 report, or -1 when it
        /// names none of the three.</summary>
        internal static int ActiveProfile(byte[] active)
        {
            if (active == null || active.Length < 2 || active[0] != ActiveReportId) return -1;
            int profile = active[1] >> 4;
            return profile < ProfileCount ? profile : -1;
        }

        /// <summary>The hand-back from a profile report: its lighting bytes
        /// 1 to 5 sent as F1. Null for a report that is not that
        /// profile's.</summary>
        internal static byte[] HandBackFrame(byte[] profile, int index)
        {
            if (profile == null || profile.Length < 6 || profile[0] != FirstProfileReportId + index) return null;
            return LedFrame(profile[1], profile[2], profile[3], profile[4], profile[5]);
        }

        internal sealed class Session : HidLightingSession
        {
            private byte[][] _handBack = Array.Empty<byte[]>();
            private int _profile = -1;
            private bool _repaint;

            /// <summary>Reads the active profile's lighting, the session's
            /// hand-back. A collection too short for the profile report, or
            /// a read that fails or names no profile, leaves the session with
            /// none.</summary>
            internal static Session Begin(IHidReportChannel io)
            {
                var session = new Session();
                session.ReadProfile(io);
                return session;
            }

            /// <summary>Reads F0, and the lighting of a profile other than the
            /// one last read. True when the hand-back moved to it. A read that
            /// fails leaves the hand-back as it was.</summary>
            private bool ReadProfile(IHidReportChannel io)
            {
                if (io == null || io.FeatureReportLength < ProfileReportLength) return false;
                var active = new byte[4];
                int index = io.GetFeature(ActiveReportId, active) ? ActiveProfile(active) : -1;
                if (index < 0 || index == _profile) return false;
                var profile = new byte[ProfileReportLength];
                if (!io.GetFeature((byte)(FirstProfileReportId + index), profile)) return false;
                var frame = HandBackFrame(profile, index);
                if (frame == null) return false;
                _profile = index;
                _handBack = new[] { frame };
                return true;
            }

            public override bool Feature => true;

            public override byte[][] CrashFrames => _handBack;

            /// <summary>True whatever the reads did: the session goes on without
            /// a profile it can read, as it starts without one, and a mouse that
            /// left fails at its next picture or at the next walk.</summary>
            public override bool Recheck(IHidReportChannel io, Action<int> wait)
            {
                _repaint = ReadProfile(io);
                return true;
            }

            internal override bool RepaintDue => _repaint;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                return io.SetFeature(Frame(colors[0])) ? HidLightingPaint.Sent : HidLightingPaint.Failed;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                foreach (var frame in _handBack)
                    if (!io.SetFeature(frame)) return false;
                return true;
            }
        }
    }
}
