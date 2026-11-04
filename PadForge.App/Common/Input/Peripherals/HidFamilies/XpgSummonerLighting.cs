using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The XPG Summoner keyboard (#557), lit key by key as OpenRGB's
    /// XPGSummonerKeyboardController lights it: 125F:9418 on interface 2,
    /// usage page 0xFF01, usage 1 (XPGSummonerControllerDetect.cpp:35), the
    /// collection SignalRGB's plugin takes too (signal-plugins
    /// XPG_Summoner_Keyboard.js:127-134). Its 104 keys are OpenRGB's
    /// led_names, each at the frame index matrix_map gives its place
    /// (RGBController_XPGSummoner.cpp:35-43, 66-172 and 244-258).
    ///
    /// <para>The model of the device. A session sends 07 EA 00 00 in a
    /// 265-byte output report, as OpenRGB's controller does when it opens
    /// (SendInitialize, XPGSummonerController.cpp:31-40). A picture is two
    /// 265-byte output reports, 07 A3 08 00, the report's number and 00, then
    /// 256 bytes of the 504-byte frame, which holds brightness 0x64, red,
    /// green and blue at four times each key's index (SendColors and
    /// SendColorDataPacket, lines 70-108; DeviceUpdateLEDs,
    /// RGBController_XPGSummoner.cpp:267-289), each followed by a 2 ms
    /// wait. OpenRGB's Direct mode does not save (its flags, line 201).</para>
    ///
    /// <para>Hand-back. OpenRGB sends nothing when it closes. SignalRGB's
    /// plugin returns the keyboard to its own lighting with 07 A6 00 4D in a
    /// 265-byte feature report, its "Hardware" report (XPG_Summoner_Keyboard.js:67-74),
    /// which is the hand-back here and what the crash path sends. XPG's
    /// program, XPG Prime, lights the keyboard while it runs, and SignalRGB
    /// stands down while XPG-Prime.exe runs (ConflictingProcesses, lines
    /// 20-22), so the device is left to it meanwhile
    /// (<see cref="VendorPrograms.XpgPrime"/>).</para>
    /// </summary>
    internal sealed class XpgSummonerLighting : IHidLightingFamily
    {
        public static readonly XpgSummonerLighting Instance = new();

        internal const string OpenRgbFamily = "XPGSummonerKeyboardController";

        /// <summary>XPG_VID (XPGSummonerController.h:21).</summary>
        internal const ushort VendorId = 0x125F;

        /// <summary>The report length (XPGSummonerController.cpp:33-38 and 93).</summary>
        internal const int PacketSize = 265;

        /// <summary>The frame, 126 LEDs of 4 bytes, and the block each report
        /// carries (lines 72-76).</summary>
        internal const int FrameLength = 126 * 4;
        internal const int Block = 256;

        /// <summary>The brightness byte ahead of each color (RGBController_XPGSummoner.cpp:269).</summary>
        internal const byte Brightness = 0x64;

        internal const int GapMs = 2;

        /// <summary>The shortest time between two pictures. No reference
        /// bounds it, so the bound is PadForge's, the one the other keyboard
        /// families of the shared worker keep.</summary>
        internal const int RepaintMs = 50;

        public string Name => "XPG";

        private readonly KeyboardLedNames.ModelCache _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection == null || collection.VendorId != VendorId) return null;
            var registrations = OpenRgbRegistrations.Match(OpenRgbFamily, collection);
            if (registrations.Count == 0) return null;
            if (collection.OutputReportLength != 0 && collection.OutputReportLength < PacketSize) return null;
            return _models.Get(registrations[0].Name, () => Model(registrations[0].Name, collection.ProductId));
        }

        /// <summary>The keys in OpenRGB's order: matrix_map row by row, each
        /// cell that holds an index taking the next name (lines 244-258).</summary>
        internal static List<LightInfo> Lights()
        {
            var lights = new List<LightInfo>(KeyboardLedNames.XpgNames.Length);
            int next = 0;
            foreach (int cell in KeyboardLedNames.XpgMatrix)
            {
                if (cell < 0 || next >= KeyboardLedNames.XpgNames.Length) continue;
                if (KeyboardLedNames.Named(cell, KeyboardLedNames.XpgNames[next++]) is { } light) lights.Add(light);
            }
            return lights;
        }

        private static HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = Instance,
            Name = name,
            Map = new LightMap(Lights(), "OpenRGB RGBController_XPGSummoner.cpp")
            {
                Product = $"xpg:{VendorId:X4}:{productId:X4}",
            },
            RepaintMs = RepaintMs,
            Programs = new[] { VendorPrograms.XpgPrime },
            Begin = (io, wait) => Begin(io, wait),
        };

        /// <summary>SendInitialize (lines 31-40).</summary>
        internal static byte[] Initialize()
        {
            var report = new byte[PacketSize];
            report[0] = 0x07;
            report[1] = 0xEA;
            return report;
        }

        /// <summary>SignalRGB's "Hardware" report (XPG_Summoner_Keyboard.js:72).</summary>
        internal static byte[] Hardware()
        {
            var report = new byte[PacketSize];
            report[0] = 0x07;
            report[1] = 0xA6;
            report[3] = 0x4D;
            return report;
        }

        /// <summary>The picture's two reports (lines 70-108).</summary>
        internal static List<byte[]> Picture(IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
        {
            var frame = new byte[FrameLength];
            int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                int at = lights[i].Address * 4;
                if (at < 0 || at + 3 >= FrameLength) continue;
                frame[at] = Brightness;
                frame[at + 1] = (byte)(colors[i] >> 16);
                frame[at + 2] = (byte)(colors[i] >> 8);
                frame[at + 3] = (byte)colors[i];
            }
            var reports = new List<byte[]>(2);
            for (int zone = 0; zone < 2; zone++)
            {
                int offset = zone * Block;
                var report = new byte[PacketSize];
                report[0] = 0x07;
                report[1] = 0xA3;
                report[2] = 0x08;
                report[4] = (byte)zone;
                Array.Copy(frame, offset, report, 6, Math.Min(Block, FrameLength - offset));
                reports.Add(report);
            }
            return reports;
        }

        private static HidLightingSession Begin(IHidReportChannel io, Action<int> wait)
        {
            if (io == null || !io.WriteOutput(Initialize())) return null;
            wait(GapMs);
            return new Session();
        }

        internal sealed class Session : HidLightingSession
        {
            private byte[][] _crash = { Hardware() };

            /// <summary>The hand-back is SignalRGB's feature report.</summary>
            public override bool Feature => true;

            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (io == null) return HidLightingPaint.Failed;
                foreach (var report in Picture(lights, colors))
                {
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                    wait(GapMs);
                }
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                if (io == null || !io.SetFeature(_crash[0])) return false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
