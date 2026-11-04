using System;
using System.Collections.Generic;
using PadForge.Common.Input.Peripherals.HidFamilies;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// The Razer Chroma Addressable RGB Controller (0F1F), lit as OpenRGB's
    /// DetectRazerARGBControllers and RGBController_RazerAddressable light it
    /// (#557). It sits inside the PC and drives six strips.
    ///
    /// <para>The two collections. The controller takes the 90-byte Razer
    /// report on interface 0 and 320-byte ARGB packets on interface 1
    /// (RazerControllerDetect.cpp:103-180). The unit is the interface 0
    /// collection, the one the registration matches (line 466, read into
    /// <see cref="RazerHidTable"/>), and the interface 1 collection of the
    /// same controller is the model's sibling
    /// (<see cref="HidLightingModel.Siblings"/>). OpenRGB opens interface 1
    /// by its number alone, on any usage page (lines 142-151), and a
    /// sibling names a page. No source gives the page: linux-hardware's
    /// lsusb probes of the controller list both report descriptors as
    /// unavailable, 22 and 23 bytes long. So the family learns it from the
    /// walk: an interface 1 collection of the product that the worker
    /// offers it gives its page to every model made after, and until one
    /// has, a model takes the unit's own page. The two descriptors differ by
    /// one byte, the length a 320-byte report count takes over a 90-byte one,
    /// so the two pages are taken to be the same (an inference). A walk that
    /// lists interface 0 first and a page that differs cost the unit one
    /// walk: it is listed at the next.</para>
    ///
    /// <para>A picture is OpenRGB's Direct mode. SetLEDs sends each of the
    /// six channels as one ARGB packet, a feature report with report ID 00:
    /// 04, or 84 for the sixth channel, the channel twice, 00, the last
    /// index 79, and 80 colors, red, green and blue, in a packet of 321
    /// bytes with the ID, 1 ms apart (RazerController.cpp:207-258, 741-775,
    /// 1474-1499 and 2131-2135, RazerController.h:254-263). openrazer sends
    /// the same packet as SET_REPORT feature report 00 to interface 1
    /// (razercommon.c:190-232, razercommon.h:138-146). OpenRGB's custom-mode
    /// step sends nothing for this controller (RazerController.cpp:1752-1753).
    /// openrazer's daemon follows the frames with the custom frame effect
    /// (razeraccessory_driver.c:584-614). PadForge follows OpenRGB, the
    /// source of truth. Choosing the Direct mode in OpenRGB also sends the
    /// brightness, 255, to each channel's LED, 1A to 1F, not stored
    /// (RGBController_RazerAddressable.cpp:272-282 and 345-348,
    /// RazerController.cpp:1439-1457 and 719-728), and a session sends
    /// that with its first picture. Every 90-byte report carries the
    /// transaction ID 3F of the product's razer_device entry
    /// (RazerDevices.cpp:9083-9102, RazerController.cpp:63-66). OpenRGB's
    /// strips start with no LEDs until its user sizes them
    /// (RGBController_RazerAddressable.cpp:189-192), and its packets carry
    /// 80 colors for every channel all the same. Until a count is set on the
    /// Lighting tab, PadForge's map is the 80 lights of each channel, a
    /// channel's lights past the end of its strip lighting nothing. A count
    /// narrows the channel's lights to that many, the rest of its packet
    /// black, and goes out as the size report OpenRGB sends when its user
    /// sizes a channel (DeviceConfigureZone,
    /// RGBController_RazerAddressable.cpp:235-247, RazerController.cpp:172-177
    /// and 681-708).</para>
    ///
    /// <para>The hand-back. The packets and the brightness store nothing.
    /// A session reads each channel's stored brightness at its start, as
    /// openrazer reads it (0F/84 [01 LED], razeraccessory_driver.c:1612-1620,
    /// razerchromacommon.c:731-739), and the stored effect of LED 05, the
    /// read the Razer worker makes (<see cref="RazerDirectProtocol.StoredEffectRead"/>).
    /// It hands back that effect where the reply passes
    /// <see cref="RazerDirectProtocol.TryStoredEffect"/>, else the spectrum
    /// effect, OpenRGB's Spectrum Cycle on LED 05 (RazerController.cpp:972-981
    /// and 1893-1909), and then each channel's brightness that was read.
    /// openrazer and the Razer worker reach the reply the same way
    /// (<see cref="RazerFeatureLink"/>).</para>
    ///
    /// <para>The controller is Razer's, so the linker's Razer row reaches it
    /// and closes its path while Synapse runs
    /// (<see cref="PeripheralLinker.VendorProgramRuns"/>).</para>
    /// </summary>
    internal sealed class RazerArgbLighting : IHidLightingFamily
    {
        public static readonly RazerArgbLighting Instance = new();

        internal const string Detector = "DetectRazerARGBControllers";

        /// <summary>The ARGB interface (RazerControllerDetect.cpp:147-151).</summary>
        internal const int ArgbInterface = 1;

        /// <summary>The ARGB packet with its report ID byte, and the colors
        /// it carries (RazerController.h:254-263, razercommon.h:138-146).</summary>
        internal const int PacketLength = 321;
        internal const int ColorsAt = 6;
        internal const int Channels = 6;
        internal const int ChannelLeds = 80;

        /// <summary>The packet's report byte below the sixth channel and on
        /// it (RazerController.cpp:755-762).</summary>
        internal const byte PacketLow = 0x04;
        internal const byte PacketHigh = 0x84;

        /// <summary>The wait before each packet and after the last
        /// (RazerController.cpp:242 and 247).</summary>
        internal const int PacketWaitMs = 1;

        /// <summary>The channels' LED IDs (RazerController.h:91-96) and the
        /// effects' LED (RazerDevices.cpp:9090).</summary>
        internal const byte FirstChannelLed = 0x1A;
        internal const byte EffectLed = 0x05;

        internal const byte MaxBrightness = 0xFF;

        internal const int RepaintMs = AsusTufLaptopLighting.RepaintMs;

        public string Name => "Razer ARGB";

        /// <summary>The usage page each product's ARGB interface showed in a
        /// walk, and the models made per product and page.</summary>
        private readonly Dictionary<ushort, ushort> _pages = new();
        private readonly Dictionary<(string, ushort, ushort), HidLightingModel> _models = new();

        public HidLightingModel Match(HidCollectionInfo collection)
        {
            if (collection?.Path == null) return null;
            if (IsArgbInterface(collection))
            {
                lock (_models) _pages[collection.ProductId] = collection.UsagePage;
                return null;
            }
            var registration = RazerHidTable.Match(collection, Detector);
            if (registration == null || collection.FeatureReportLength < RazerDirectProtocol.BufferLength) return null;
            lock (_models)
            {
                ushort page = _pages.TryGetValue(collection.ProductId, out var learned) ? learned : collection.UsagePage;
                var key = (registration.Name, collection.ProductId, page);
                if (!_models.TryGetValue(key, out var model))
                    _models[key] = model = Model(registration.Name, collection.ProductId, page);
                return model;
            }
        }

        /// <summary>Whether a collection is the ARGB interface of a product a
        /// registration of the detector names: the product's IDs on
        /// interface 1 (RazerControllerDetect.cpp:138-151).</summary>
        internal static bool IsArgbInterface(HidCollectionInfo collection)
        {
            if (OpenRgbRegistrations.InterfaceOf(collection, OpenRgbRegistrations.BusOf(collection)) != ArgbInterface)
                return false;
            foreach (var registration in RazerHidTable.Of(Detector))
                if (registration.VendorId == collection.VendorId && registration.ProductId == collection.ProductId)
                    return true;
            return false;
        }

        internal HidLightingModel Model(string name, ushort productId, ushort argbPage)
        {
            byte transaction = RazerHidTable.Device(productId)?.TransactionId ?? 0x3F;
            return new HidLightingModel
            {
                Family = this,
                Name = name,
                Map = StripMap(productId, null),
                RepaintMs = RepaintMs,
                Siblings = new[] { new HidSibling(ArgbInterface, argbPage) },
                BeginWith = (control, siblings, wait) => siblings?.Count == 1
                    ? Session.Begin(control, siblings[0], wait, transaction, productId)
                    : null,
            };
        }

        // ─────────────────────────────────────────────
        //  The reports
        // ─────────────────────────────────────────────

        /// <summary>The controller's lights, each channel's 80 or the count set
        /// for it. A light's address is its place among all 480, so a channel's
        /// count leaves the others' addresses.</summary>
        internal static LightMap StripMap(ushort productId, IReadOnlyList<int> counts)
        {
            var table = RazerHidTable.MapOf(productId);
            var strips = new LightStrip[Channels];
            for (int c = 0; c < Channels; c++)
                strips[c] = new LightStrip(c + 1, ChannelLeds, Math.Min(StripCounts.At(counts, c + 1), ChannelLeds));
            var lights = new List<LightInfo>(table.Lights.Count);
            foreach (var light in table.Lights)
            {
                int channel = light.Address / ChannelLeds;
                if (channel >= Channels || strips[channel].Leds == 0 || light.Address % ChannelLeds < strips[channel].Leds)
                    lights.Add(light);
            }
            return new LightMap(lights, table.Source) { Product = table.Product, Strips = strips };
        }

        /// <summary>razer_create_addressable_size_report
        /// (RazerController.cpp:681-708): 0F/08 at size 0D, 06, then per channel
        /// 19 and its count, or its number and 0 with no count.</summary>
        internal static byte[] SizeReport(byte transaction, IReadOnlyList<int> counts)
        {
            Span<byte> arguments = stackalloc byte[1 + 2 * Channels];
            arguments[0] = Channels;
            for (int c = 0; c < Channels; c++)
            {
                int count = Math.Min(StripCounts.At(counts, c + 1), ChannelLeds);
                arguments[1 + 2 * c] = count == 0 ? (byte)(c + 1) : (byte)0x19;
                arguments[2 + 2 * c] = (byte)count;
            }
            return RazerDirectProtocol.Report(transaction, 0x0F, 0x08, 0x0D, arguments);
        }

        /// <summary>razer_create_custom_frame_argb_report for one channel,
        /// from column 0 to 79 (RazerController.cpp:741-775), with the report
        /// ID byte 00 in front. A color the picture lacks is black.</summary>
        internal static byte[] Packet(int channel, IReadOnlyList<int> colors, int first)
        {
            var packet = new byte[PacketLength];
            packet[1] = channel < 5 ? PacketLow : PacketHigh;
            packet[2] = (byte)channel;
            packet[3] = (byte)channel;
            packet[5] = ChannelLeds - 1;
            for (int i = 0; i < ChannelLeds; i++)
            {
                int at = first + i;
                int rgb = colors != null && at < colors.Count ? colors[at] : 0;
                packet[ColorsAt + 3 * i] = (byte)(rgb >> 16);
                packet[ColorsAt + 3 * i + 1] = (byte)(rgb >> 8);
                packet[ColorsAt + 3 * i + 2] = (byte)rgb;
            }
            return packet;
        }

        /// <summary>0F/04 [storage LED brightness] at size 3, the extended
        /// matrix brightness (RazerController.cpp:719-728), not stored.</summary>
        internal static byte[] BrightnessWrite(byte transaction, byte led, byte brightness)
            => RazerDirectProtocol.Report(transaction, 0x0F, 0x04, 3, stackalloc byte[] { 0x00, led, brightness });

        /// <summary>The brightness of every channel at 255, SetBrightness(255)
        /// (RazerController.cpp:1439-1457).</summary>
        internal static byte[][] FullBrightness(byte transaction)
        {
            var reports = new byte[Channels][];
            for (int i = 0; i < Channels; i++) reports[i] = BrightnessWrite(transaction, (byte)(FirstChannelLed + i), MaxBrightness);
            return reports;
        }

        // ─────────────────────────────────────────────
        //  The session
        // ─────────────────────────────────────────────

        internal sealed class Session : HidLightingSession
        {
            private readonly IHidReportChannel _control;
            private readonly IHidReportChannel _argb;
            private readonly byte _transaction;
            private readonly byte[][] _handBack;
            private bool _direct;
            private byte[][] _crash = Array.Empty<byte[]>();
            private readonly ushort _productId;
            private LightMap _map;
            private IReadOnlyList<int> _sizeSent = StripCounts.None;

            private Session(IHidReportChannel control, IHidReportChannel argb, byte transaction, byte[][] handBack,
                ushort productId)
            {
                _control = control;
                _argb = argb;
                _transaction = transaction;
                _handBack = handBack;
                _productId = productId;
            }

            /// <summary>The model's map until a count is set.</summary>
            public override LightMap Map => _map;

            /// <summary>The channels' lights, and the size report OpenRGB sends
            /// where a count changed. Another program holding the Razer lock
            /// costs the report, not the count: the packets do not depend on
            /// it.</summary>
            public override bool SizeStrips(IHidReportChannel io, Action<int> wait, IReadOnlyList<int> counts)
            {
                _map = StripMap(_productId, counts);
                if (StripCounts.Same(_sizeSent, counts)) return true;
                var sent = RazerFeatureLink.Send(_control, SizeReport(_transaction, counts), RazerGuard.CommandWaitMs);
                if (sent == true) _sizeSent = counts;
                return sent != false;
            }

            /// <summary>Reads the stored effect of LED 05 and each channel's
            /// stored brightness, the hand-back. Null for an ARGB collection
            /// whose feature report is shorter than the packet.</summary>
            internal static Session Begin(IHidReportChannel control, IHidReportChannel argb, Action<int> wait,
                byte transaction, ushort productId)
            {
                if (control == null || argb == null || argb.FeatureReportLength < PacketLength) return null;
                var handBack = new List<byte[]>();
                var stored = RazerFeatureLink.Query(control, wait,
                    RazerDirectProtocol.StoredEffectRead(transaction, EffectLed));
                handBack.Add(stored != null && RazerDirectProtocol.TryStoredEffect(transaction, EffectLed, stored, out var effect)
                    ? effect
                    : RazerDirectProtocol.ExtendedSpectrum(transaction, EffectLed));
                for (int i = 0; i < Channels; i++)
                {
                    byte led = (byte)(FirstChannelLed + i);
                    var reply = RazerFeatureLink.Query(control, wait, RazerDirectProtocol.ExtendedBrightnessRead(transaction, led));
                    if (reply == null || reply[RazerDirectProtocol.ArgumentsAt + 1] != led) continue;
                    handBack.Add(BrightnessWrite(transaction, led, reply[RazerDirectProtocol.ArgumentsAt + 2]));
                }
                return new Session(control, argb, transaction, handBack.ToArray(), productId);
            }

            public override bool Feature => true;

            /// <summary>The hand-back, once the first picture may have gone
            /// out.</summary>
            public override byte[][] CrashFrames => _crash;

            public override HidLightingPaint Paint(IHidReportChannel io, Action<int> wait,
                IReadOnlyList<LightInfo> lights, IReadOnlyList<int> colors)
            {
                if (!_direct) _crash = _handBack;
                var byAddress = new int[Channels * ChannelLeds];
                int count = Math.Min(lights?.Count ?? 0, colors?.Count ?? 0);
                for (int i = 0; i < count; i++)
                    if ((uint)lights[i].Address < (uint)byAddress.Length) byAddress[lights[i].Address] = colors[i];
                for (int channel = 0; channel < Channels; channel++)
                {
                    wait?.Invoke(PacketWaitMs);
                    var sent = RazerFeatureLink.Send(_argb, Packet(channel, byAddress, channel * ChannelLeds), RazerGuard.PaintWaitMs);
                    if (sent == null) return Later();
                    if (sent == false) return HidLightingPaint.Failed;
                }
                wait?.Invoke(PacketWaitMs);
                if (!_direct)
                {
                    foreach (var report in FullBrightness(_transaction))
                    {
                        var sent = RazerFeatureLink.Send(_control, report, RazerGuard.CommandWaitMs);
                        if (sent == null) return Later();
                        if (sent == false) return HidLightingPaint.Failed;
                    }
                    _direct = true;
                }
                return HidLightingPaint.Sent;
            }

            /// <summary>Another program held the Razer lock: the picture is
            /// tried again after a paint's wait for it.</summary>
            private HidLightingPaint Later()
            {
                LaterMs = RazerGuard.PaintWaitMs;
                return HidLightingPaint.Later;
            }

            public override bool HandBack(IHidReportChannel io, Action<int> wait)
            {
                if (_crash.Length == 0) return true;
                foreach (var report in _crash)
                    if (RazerFeatureLink.Send(_control, report, RazerGuard.CommandWaitMs) != true) return false;
                _direct = false;
                _crash = Array.Empty<byte[]>();
                return true;
            }
        }
    }
}
