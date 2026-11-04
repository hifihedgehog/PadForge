using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The first Kraken headsets, lit as OpenRGB's RazerKrakenController
    /// lights them (#557): the Kraken 7.1 (0501, 0506), the Kraken 7.1 Chroma
    /// (0504), the Kraken 7.1 V2 (0510), the Kraken Kitty Black Edition V2
    /// (0560) and the Kraken Ultimate (0527), each on the consumer collection
    /// of interface 3 (RazerControllerDetect.cpp:430-437, read into
    /// <see cref="RazerHidTable"/>).
    ///
    /// <para>The wire. Every request is the 37-byte output report 04: the
    /// destination, the length, the address high byte first and up to 32
    /// arguments (RazerKrakenController.h:47-55, RazerKrakenController.cpp:140-159,
    /// openrazer razerkraken_driver.h:144-152). OpenRGB writes it with
    /// hid_write (RazerKrakenController.cpp:398-401) and openrazer sends it
    /// as SET_REPORT output report 4 to interface 3 (razerkraken_driver.c:47-73),
    /// the same report. Destination 40 writes the headset's RAM, 00 reads
    /// it, and the answer is the input report 05 with the bytes from
    /// argument 1 on (razerkraken_driver.h:54-64, razerkraken_driver.c:111-173
    /// and 844-856). The addresses are a product's: the effect byte at
    /// 172D and the custom color at 1189 on the V2, the Kitty Black Edition
    /// V2 and the Ultimate, the effect byte at 1008 and the custom color at
    /// 1189 on the others (RazerKrakenController.cpp:43-63,
    /// razerkraken_driver.h:73-136).</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode, SetModeCustom: the color
    /// written to the custom address, then the effect byte 01, the static
    /// light on (RazerKrakenController.cpp:301-328,
    /// RGBController_RazerKraken.cpp:162-169). The Kraken 7.1 takes no color,
    /// and OpenRGB sends it the effect byte alone (lines 317-325), which
    /// turns its logo light on. PadForge sends that light OpenRGB's Off mode,
    /// the effect byte 00 (razer_set_mode_none, lines 330-343), for a black
    /// picture, the one color it can show off. openrazer writes the same
    /// two reports for its custom effect and waits nothing between them
    /// (razerkraken_driver.c:354-386).</para>
    ///
    /// <para>The lights. OpenRGB's Kraken controller sends the first color
    /// alone (RGBController_RazerKraken.cpp:162-169), so each headset is one
    /// light, the Kitty Black Edition V2 too, whose four LEDs OpenRGB lists
    /// (RazerDevices.cpp:8424-8467) and paints in that one color. The Kraken
    /// 7.1 has no razer_device entry, and OpenRGB names its zones from the
    /// list's first entry instead (the device_index of 0 at
    /// RazerKrakenController.cpp:24, never set for it at lines 29-38).</para>
    ///
    /// <para>What the writes do. They go to RAM, never the EEPROM, which
    /// destination 20 reads (razerkraken_driver.h:61-63), so a replug shows
    /// the headset's own lighting again. The hand-back is openrazer's: the
    /// session reads the effect byte and, on a headset that takes a color,
    /// the custom color with its intensity byte, before it writes anything
    /// (get_current_effect and get_rgb_from_addr, razerkraken_driver.c:111-173),
    /// and writes them back, the color as openrazer's 4-byte custom write
    /// (lines 354-386), then the effect byte. OpenRGB has no hand-back. A
    /// headset whose reads go unanswered is lit and handed back nothing, as
    /// <see cref="LogitechG600Lighting"/> treats a mouse whose profile it
    /// cannot read.</para>
    ///
    /// <para>The headsets are Razer's, so the linker's Razer row reaches
    /// them and closes their path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).</para>
    /// </summary>
    internal sealed class RazerKrakenLighting : IHidLightingFamily
    {
        public static readonly RazerKrakenLighting Instance = new();

        internal const string Detector = "DetectRazerKrakenControllers";

        /// <summary>The product IDs (RazerDevices.h:234-241).</summary>
        internal const ushort KrakenClassic = 0x0501;
        internal const ushort KrakenClassicAlt = 0x0506;
        internal const ushort Kraken = 0x0504;
        internal const ushort KrakenV2 = 0x0510;
        internal const ushort KrakenUltimate = 0x0527;
        internal const ushort KrakenKittyBlackV2 = 0x0560;

        /// <summary>The request and the answer report IDs, and the request's
        /// length with its ID (razerkraken_driver.h:57-58 and 144-152).</summary>
        internal const byte RequestId = 0x04;
        internal const byte AnswerId = 0x05;
        internal const int RequestLength = 37;

        /// <summary>The destinations (razerkraken_driver.h:60-64).</summary>
        internal const byte WriteRam = 0x40;
        internal const byte ReadRam = 0x00;

        /// <summary>The addresses (RazerKrakenController.cpp:43-63).</summary>
        internal const ushort KylieEffect = 0x172D;
        internal const ushort RainieEffect = 0x1008;
        internal const ushort CustomColor = 0x1189;

        /// <summary>The effect byte's static light bit (RazerKrakenController.h:29-42).</summary>
        internal const byte EffectStatic = 0x01;
        internal const byte EffectOff = 0x00;

        /// <summary>How long openrazer waits for an answer to a read
        /// (razerkraken_driver.c:125 and 156).</summary>
        internal const int AnswerMs = 25;

        /// <summary>The most input reports a read sets aside before the
        /// answer: a consumer collection carries the headset's own reports
        /// too. The bound is PadForge's.</summary>
        internal const int ReadLimit = 8;

        /// <summary>The shortest time between two pictures, PadForge's bound
        /// for writes that store nothing (<see cref="AsusTufLaptopLighting.RepaintMs"/>).</summary>
        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer Kraken";

        private readonly Dictionary<(string, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null || collection.OutputReportLength < RequestLength) return null;
            lock (_models)
            {
                var key = (registration.Name, collection.ProductId);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration.Name, collection.ProductId);
                return model;
            }
        }

        internal HidLightingModel Model(string name, ushort productId) => new()
        {
            Family = this,
            Name = name,
            Map = MapOf(productId),
            RepaintMs = RepaintMs,
            Begin = (io, wait) => Session.Begin(io, productId),
        };

        /// <summary>One light, the one color OpenRGB sends.</summary>
        internal static LightMap MapOf(ushort productId)
        {
            var device = RazerHidTable.Device(productId);
            string source = device != null
                ? $"OpenRGB {device.Source}, first color (RGBController_RazerKraken.cpp:162-169)"
                : "OpenRGB RazerControllerDetect.cpp, first color (RGBController_RazerKraken.cpp:162-169)";
            return new LightMap(new[] { new LightInfo(0, LightKind.Zone, 0, LightKeySource.None) { Zone = 1 } }, source)
            {
                Product = RazerHidTable.Product(productId),
            };
        }

        /// <summary>Whether a product takes a color: the four OpenRGB
        /// writes the custom color to (RazerKrakenController.cpp:317-325).</summary>
        internal static bool TakesColor(ushort productId)
            => productId is Kraken or KrakenV2 or KrakenKittyBlackV2 or KrakenUltimate;

        /// <summary>The effect byte's address (RazerKrakenController.cpp:43-63).</summary>
        internal static ushort EffectAddress(ushort productId)
            => productId is KrakenV2 or KrakenKittyBlackV2 or KrakenUltimate ? KylieEffect : RainieEffect;

        /// <summary>razer_kraken_create_report with its arguments
        /// (RazerKrakenController.cpp:140-159).</summary>
        internal static byte[] Request(byte destination, byte length, ushort address, ReadOnlySpan<byte> arguments)
        {
            var report = new byte[RequestLength];
            report[0] = RequestId;
            report[1] = destination;
            report[2] = length;
            report[3] = (byte)(address >> 8);
            report[4] = (byte)address;
            arguments.CopyTo(report.AsSpan(5));
            return report;
        }

        /// <summary>The custom color write of SetModeCustom (line 303).</summary>
        internal static byte[] ColorWrite(int rgb)
            => Request(WriteRam, 3, CustomColor, stackalloc byte[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb });

        /// <summary>The effect byte write (lines 304 and 332).</summary>
        internal static byte[] EffectWrite(ushort productId, byte effect)
            => Request(WriteRam, 1, EffectAddress(productId), stackalloc byte[] { effect });

        /// <summary>The reports of one picture.</summary>
        internal static byte[][] Picture(ushort productId, int rgb)
        {
            if (TakesColor(productId)) return new[] { ColorWrite(rgb), EffectWrite(productId, EffectStatic) };
            return new[] { EffectWrite(productId, (rgb & 0xFFFFFF) == 0 ? EffectOff : EffectStatic) };
        }

        /// <summary>Sends a read and returns the answer's bytes from argument
        /// 1 on, or null when none came (razerkraken_driver.c:111-173).
        /// Input reports already waiting are set aside first, so an answer
        /// left from before is not taken for this one.</summary>
        internal static byte[] Read(IHidReportChannel io, ushort address, byte length)
        {
            var buffer = new byte[Math.Max(io.InputReportLength, RequestLength)];
            for (int i = 0; i < ReadLimit; i++)
            {
                int stale = io.ReadInput(buffer, 0);
                if (stale < 0) return null;
                if (stale == 0) break;
            }
            if (!io.WriteOutput(Request(ReadRam, length, address, ReadOnlySpan<byte>.Empty))) return null;
            for (int i = 0; i < ReadLimit; i++)
            {
                int read = io.ReadInput(buffer, AnswerMs);
                if (read <= 0) return null;
                if (buffer[0] != AnswerId || read < 1 + length) continue;
                return buffer.AsSpan(1, length).ToArray();
            }
            return null;
        }

        internal sealed class Session : HidLightingSession
        {
            private readonly ushort _productId;
            private readonly byte[][] _handBack;

            private Session(ushort productId, byte[][] handBack)
            {
                _productId = productId;
                _handBack = handBack;
            }

            /// <summary>Reads what the headset shows: the effect byte and, on
            /// a headset that takes a color, the custom color. A read that
            /// goes unanswered leaves the session with no hand-back.</summary>
            internal static Session Begin(IHidReportChannel io, ushort productId)
            {
                if (io == null) return null;
                byte[] effect = Read(io, EffectAddress(productId), 1);
                byte[][] handBack = Array.Empty<byte[]>();
                if (effect != null)
                {
                    if (!TakesColor(productId))
                    {
                        handBack = new[] { EffectWrite(productId, effect[0]) };
                    }
                    else
                    {
                        byte[] color = Read(io, CustomColor, 4);
                        if (color != null)
                            handBack = new[] { Request(WriteRam, 4, CustomColor, color), EffectWrite(productId, effect[0]) };
                    }
                }
                return new Session(productId, handBack);
            }

            public override bool Feature => false;

            public override byte[][] CrashFrames => _handBack;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (colors == null || colors.Count == 0) return HidLightingPaint.Sent;
                foreach (var report in Picture(_productId, colors[0]))
                    if (!io.WriteOutput(report)) return HidLightingPaint.Failed;
                return HidLightingPaint.Sent;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                foreach (var report in _handBack)
                    if (!io.WriteOutput(report)) return false;
                return true;
            }
        }
    }
}
