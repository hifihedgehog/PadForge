using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A scripted Logitech device for the lighting beyond the
    /// zone-only class (#494): its features at fixed indexes, the clusters
    /// and multi-cluster effects of its 0x8071, the zone pages of its
    /// 0x8081, the zones of its 0x8070, its mode, its claim flags and its
    /// model ID, answering the way the captures cited in each test show a
    /// real one answering.</summary>
    internal sealed class HidppLightDevice
    {
        public byte Index = HidppHapticProtocol.DirectDeviceIndex;
        public string Name = "G915";
        public byte Type;
        public readonly Dictionary<ushort, byte> Features = new() { [0x0005] = 0x03 };

        /// <summary>Each cluster's location and the IDs of its effects.</summary>
        public (ushort Location, ushort[] Effects)[] Clusters = Array.Empty<(ushort, ushort[])>();

        /// <summary>The IDs of the effects cluster 0xFF lists.</summary>
        public ushort[] MultiCluster = Array.Empty<ushort>();

        /// <summary>What the second byte of a device or a cluster answer
        /// carries: 0xFF by Logitech's document, 0x00 on a G915 X.</summary>
        public byte InfoSecondByte = 0xFF;

        /// <summary>The three pages of 0x8081 zone bits, 14 bytes each, or
        /// null for a page the device refuses.</summary>
        public byte[][] ZonePages = { new byte[14], new byte[14], new byte[14] };

        /// <summary>The zones of its 0x8070, as lists of effect IDs.</summary>
        public ushort[][] Zones = Array.Empty<ushort[]>();

        /// <summary>The location each 0x8070 zone answers, 1 (primary) for a
        /// zone this leaves out.</summary>
        public ushort[] ZoneLocations = Array.Empty<ushort>();

        /// <summary>The first byte of a mode read's answer.</summary>
        public byte Mode = 0x01;

        /// <summary>The flags and the events a claim read answers.</summary>
        public byte ClaimFlags, ClaimEvents;

        /// <summary>The layout code its 0x4540 answers: 1, US, the code
        /// keyleds reads from an American board (layouts README).</summary>
        public byte Layout = 0x01;

        /// <summary>The key types its 0x8080 lists, each with the count its
        /// function 1 gives, which bounds the pages and need not be the
        /// number of keys: a G810 counts 105 keys and lists 111
        /// (g810-led-resources dumps/g810-spectrum/set_keys_colors.pcapng).</summary>
        public (HidppKeyType Type, int Count)[] KeyTypes = Array.Empty<(HidppKeyType, int)>();

        /// <summary>The transports byte and the six model ID bytes of its
        /// 0x0003 answer.</summary>
        public byte Transports = 0x0E;
        public byte[] Model = { 0xB3, 0x54, 0x40, 0x7C, 0xC3, 0x3E };

        public byte BatteryPercent = 70;

        /// <summary>Requests that go unanswered, and requests the device
        /// refuses with an error code.</summary>
        public Func<byte, byte, byte[], bool> Silent = (f, fn, p) => false;
        public Func<byte, byte, byte[], byte?> Refuse = (f, fn, p) => null;

        private byte At(ushort feature) => Features.TryGetValue(feature, out byte at) ? at : (byte)0;

        /// <summary>The feature at an index, Root at 0, or null for an index
        /// the device has nothing at.</summary>
        public ushort? FeatureAt(byte index)
        {
            if (index == 0) return 0x0000;
            foreach (var pair in Features)
                if (pair.Value == index) return pair.Key;
            return null;
        }

        public HidppReply Answer(byte d, byte f, byte fn, byte[] p)
        {
            if (d != Index || Silent(f, fn, p)) return HidppHapticsTests.NoAnswer();
            if (Refuse(f, fn, p) is byte code) return new HidppReply(HidppReplyKind.Error, code);
            if (f == 0 && fn == 0)
                return HidppHapticsTests.Ans(d, 0, 0, At((ushort)((p[0] << 8) | p[1])));
            if (f == At(0x0005))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Name);
                return fn switch
                {
                    0 => HidppHapticsTests.Ans(d, f, 0, (byte)bytes.Length),
                    1 => HidppHapticsTests.Ans(d, f, 1, bytes.Skip(p[0]).Take(16).ToArray()),
                    2 => HidppHapticsTests.Ans(d, f, 2, Type),
                    _ => HidppHapticsTests.NoAnswer(),
                };
            }
            if (At(0x0003) != 0 && f == At(0x0003) && fn == 0)
                return HidppHapticsTests.Ans(d, f, 0, new byte[] { 0x05, 0x11, 0x22, 0x33, 0x44, 0x00, Transports }
                    .Concat(Model).ToArray());
            if (At(0x4540) != 0 && f == At(0x4540) && fn == 0) return HidppHapticsTests.Ans(d, f, 0, Layout);
            if (At(0x8071) != 0 && f == At(0x8071))
            {
                if (fn == 0) return Info(d, f, p[0], p[1]);
                if (fn == 5 && p[0] == 0x00) return HidppHapticsTests.Ans(d, f, 5, 0x00, ClaimFlags, ClaimEvents);
                // A write the worker awaits is echoed, as a device echoes it.
                return HidppHapticsTests.Ans(d, f, fn, p);
            }
            if (At(0x8081) != 0 && f == At(0x8081))
            {
                if (fn != 0) return HidppHapticsTests.Ans(d, f, fn, p);
                byte[] page = p[1] < ZonePages.Length ? ZonePages[p[1]] : null;
                return page == null
                    ? new HidppReply(HidppReplyKind.Error, 0x02)
                    : HidppHapticsTests.Ans(d, f, 0, new byte[] { 0x00, p[1] }.Concat(page).ToArray());
            }
            if (At(0x8070) != 0 && f == At(0x8070))
            {
                switch (fn)
                {
                    case 0: return HidppHapticsTests.Ans(d, f, 0, (byte)Zones.Length);
                    case 1:
                        ushort location = p[0] < ZoneLocations.Length ? ZoneLocations[p[0]] : (ushort)1;
                        return HidppHapticsTests.Ans(d, f, 1, p[0], (byte)(location >> 8), (byte)location, (byte)Zones[p[0]].Length);
                    case 2:
                        ushort effect = Zones[p[0]][p[1]];
                        return HidppHapticsTests.Ans(d, f, 2, p[0], p[1], (byte)(effect >> 8), (byte)effect);
                }
            }
            if (At(0x8080) != 0 && f == At(0x8080))
            {
                // The key list the way a G810 gives it: the type bits and the
                // rest of function 0's answer as the capture shows it, the
                // count and top values per type, and pages of 14 keys on the
                // very long report. Writes are echoed.
                ushort asked = p.Length >= 2 ? (ushort)((p[0] << 8) | p[1]) : (ushort)0;
                switch (fn)
                {
                    case 0:
                        int flags = 0;
                        foreach (var (type, _) in KeyTypes) flags |= type.Type;
                        return HidppHapticsTests.Ans(d, f, 0, (byte)(flags >> 8), (byte)flags, 0x00, 0x00, 0x0D);
                    case 1:
                        foreach (var (type, count) in KeyTypes)
                            if (type.Type == asked)
                                return HidppHapticsTests.Ans(d, f, 1, (byte)(count >> 8), (byte)count,
                                    type.MaxRed, type.MaxGreen, type.MaxBlue);
                        return new HidppReply(HidppReplyKind.Error, 0x02);
                    case 2:
                        int offset = (p[2] << 8) | p[3];
                        foreach (var (type, _) in KeyTypes)
                            if (type.Type == asked)
                            {
                                var page = new List<byte> { p[0], p[1], p[2], p[3] };
                                for (int i = offset; i < Math.Min(type.Keys.Length, offset + 14); i++)
                                    page.AddRange(new byte[] { type.Keys[i], 0x00, 0xDC, 0xFF });
                                return HidppHapticsTests.AnsVeryLong(d, f, 2, page.ToArray());
                            }
                        return new HidppReply(HidppReplyKind.Error, 0x02);
                    default:
                        return HidppHapticsTests.Ans(d, f, fn, p);
                }
            }
            // A mode read gives the mode. A mode write is answered under its
            // own header, as a device answers every request.
            if (At(0x8100) != 0 && f == At(0x8100))
                return fn == 2 ? HidppHapticsTests.Ans(d, f, 2, Mode) : HidppHapticsTests.Ans(d, f, fn, p);
            if (At(0x8101) != 0 && f == At(0x8101) && fn == 6)
                return p[0] == 0x00 ? HidppHapticsTests.Ans(d, f, 6, Mode, 0x00, 0x01) : HidppHapticsTests.Ans(d, f, 6, p);
            if (At(0x1004) != 0 && f == At(0x1004) && fn == 1) return HidppHapticsTests.Ans(d, f, 1, BatteryPercent, 8);
            return HidppHapticsTests.NoAnswer();
        }

        private HidppReply Info(byte d, byte f, byte cluster, byte effect)
        {
            if (cluster == 0xFF && effect == 0xFF)
                return HidppHapticsTests.Ans(d, f, 0, 0xFF, InfoSecondByte, (byte)Clusters.Length, 0x00, 0x63, 0x00, 0x04,
                    (byte)MultiCluster.Length);
            if (cluster == 0xFF)
                return effect < MultiCluster.Length
                    ? HidppHapticsTests.Ans(d, f, 0, 0xFF, effect, (byte)(MultiCluster[effect] >> 8), (byte)MultiCluster[effect])
                    : new HidppReply(HidppReplyKind.Error, 0x02);
            if (cluster >= Clusters.Length) return new HidppReply(HidppReplyKind.Error, 0x02);
            var (location, effects) = Clusters[cluster];
            if (effect == 0xFF)
                return HidppHapticsTests.Ans(d, f, 0, cluster, InfoSecondByte, (byte)(location >> 8), (byte)location,
                    (byte)effects.Length);
            return effect < effects.Length
                ? HidppHapticsTests.Ans(d, f, 0, cluster, effect, (byte)(effects[effect] >> 8), (byte)effects[effect])
                : new HidppReply(HidppReplyKind.Error, 0x02);
        }

        public HidppHapticsTests.FakeChannel Channel(string path = null, bool bluetooth = false)
            => path == null
                ? new HidppHapticsTests.FakeChannel { Answer = Answer, Bluetooth = bluetooth }
                : new HidppHapticsTests.FakeChannel { Path = path, Answer = Answer, Bluetooth = bluetooth };

        public HidppUnit Describe(HidppHapticsTests.FakeChannel channel, out bool incomplete)
            => HidppUnitProbe.Describe(channel, Index, At(0x0005), Guid.Empty, 300, out incomplete);

        // ── The devices the references show ──

        /// <summary>A zone-only 0x8070 mouse: one zone, static second.</summary>
        public static HidppLightDevice ZoneOnly(byte index = 1)
        {
            var device = new HidppLightDevice { Index = index, Name = "G502", Type = 3 };
            device.Features[0x8070] = 0x0E;
            device.Features[0x1004] = 0x08;
            device.Zones = new[] { new ushort[] { 0x0000, 0x0001 } };
            return device;
        }

        /// <summary>A G810: 0x8080 at 0x0C and 0x8070 at 0x0D with two
        /// zones, the indexes of g810-led-resources
        /// dumps/g810-spectrum/set_fx_color_00ff00.pcapng.</summary>
        public static HidppLightDevice G810()
        {
            var device = new HidppLightDevice { Name = "G810", Type = 0 };
            device.Features[0x8080] = 0x0C;
            device.Features[0x8070] = 0x0D;
            device.Zones = new[] { new ushort[] { 0x0000, 0x0001 }, new ushort[] { 0x0000, 0x0001 } };
            return device;
        }

        /// <summary>A G810 with its keys listed as it lists them to Logitech
        /// Gaming Software (set_keys_colors.pcapng): the keys type counting
        /// 105 and listing 0x04 to 0x65, the JIS keys 0x87 to 0x8B and the
        /// modifiers, five media keys, the logo type counting one and listing
        /// two, and five indicators. Model ID C331 on its cable, layout code
        /// 3 (UK), with 0x0003 and 0x4540 at indexes the capture does not
        /// show.</summary>
        public static HidppLightDevice G810Keys()
        {
            var device = G810();
            device.Features[0x0003] = 0x02;
            device.Features[0x4540] = 0x0E;
            device.Layout = 0x03;
            device.Transports = 0x08;
            device.Model = new byte[] { 0xC3, 0x31, 0x00, 0x00, 0x00, 0x00 };
            byte[] keys = Enumerable.Range(0x04, 98).Concat(Enumerable.Range(0x87, 5)).Concat(Enumerable.Range(0xE0, 8))
                .Select(k => (byte)k).ToArray();
            device.KeyTypes = new[]
            {
                (new HidppKeyType(0x0001, 0xFF, 0xFF, 0xFF, keys), 105),
                (new HidppKeyType(0x0002, 0xFF, 0xFF, 0xFF, new byte[] { 0xCD, 0xB6, 0xB5, 0xB7, 0xE2 }), 5),
                (new HidppKeyType(0x0010, 0xFF, 0xFF, 0xFF, new byte[] { 0x01, 0x02 }), 1),
                (new HidppKeyType(0x0040, 0xFF, 0xFF, 0xFF, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }), 5),
            };
            return device;
        }

        /// <summary>A G903 LIGHTSPEED behind a receiver: 0x8071 at 0x07 and
        /// 0x8100 at 0x08, model ID 4087C0910000 (Solaar docs/devices, its
        /// dump for 4087), with the DPI indicator as its Primary cluster
        /// and a Logo cluster, four effects each with Fixed second (OpenLogi
        /// pull request 523).</summary>
        public static HidppLightDevice G903(byte index = 1)
        {
            var device = new HidppLightDevice { Index = index, Name = "G903", Type = 3 };
            device.Features[0x8071] = 0x07;
            device.Features[0x8100] = 0x08;
            device.Features[0x0003] = 0x02;
            ushort[] effects = { 0x0000, 0x0001, 0x0003, 0x000A };
            device.Clusters = new[] { ((ushort)0x0001, effects), ((ushort)0x0002, effects) };
            device.Transports = 0x0C;
            device.Model = new byte[] { 0x40, 0x87, 0xC0, 0x91, 0x00, 0x00 };
            return device;
        }

        /// <summary>A G915 behind a receiver: 0x8071 at 0x0A and 0x8081 at
        /// 0x0B (Solaar docs/devices, the G915 TKL's dump for 408E, whose
        /// indexes these are), the Logo as cluster 0 with four effects and
        /// the keys as cluster 1 with seven, Fixed second in both (the
        /// G915 TKL's lists in Solaar issue 3266), the 115 zones Logitech's
        /// own program paints on a G815 (g810-led-resources
        /// dumps/g815/G815-keys-ALL-FD7600.pcapng) with the bits of zones 0
        /// and 255 set as Logitech's document has a device report them,
        /// 0x8100 at 0x15, and 0x4540 at 0x10, where the G915 TKL lists it
        /// (Solaar docs/devices, 408E). Its model ID is the G915's,
        /// B354407CC33E.</summary>
        public static HidppLightDevice G915(byte index = 1)
        {
            var device = new HidppLightDevice { Index = index, Name = "G915", Type = 0 };
            device.Features[0x8071] = 0x0A;
            device.Features[0x8081] = 0x0B;
            device.Features[0x8100] = 0x15;
            device.Features[0x0003] = 0x02;
            device.Features[0x4540] = 0x10;
            device.Clusters = new[]
            {
                ((ushort)0x0002, new ushort[] { 0x0000, 0x0001, 0x0003, 0x000A }),
                ((ushort)0x0001, new ushort[] { 0x0000, 0x0001, 0x000A, 0x0003, 0x0004, 0x000B, 0x000C }),
            };
            device.ZonePages = new[]
            {
                HidppHapticsTests.Hex("FF FF FF FF FF 7F FF FF FF FF FF FF 05 FF", 14),
                HidppHapticsTests.Hex("00 00 00 00 00 7A 00 00 F0 01 00 00 04 00", 14),
                HidppHapticsTests.Hex("00 00 00 80 00 00 00 00 00 00 00 00 00 00", 14),
            };
            return device;
        }

        /// <summary>A G915 X on its cable, as it answered Logitech's own
        /// program (OpenRGB issue 4461, unplug_and_replug.pcapng): 0x8071
        /// at 0x08, 0x8081 at 0x09, 0x8101 at 0x0E, 00 in the second byte
        /// of its device and cluster answers, the keys as cluster 0 with
        /// six effects and the logo as cluster 1 with four, eight
        /// multi-cluster effects with Host streaming second, and 126
        /// zones.</summary>
        public static HidppLightDevice G915X()
        {
            var device = new HidppLightDevice { Name = "G915 X LS", Type = 0, InfoSecondByte = 0x00, Mode = 0x03 };
            device.Features[0x8071] = 0x08;
            device.Features[0x8081] = 0x09;
            device.Features[0x8101] = 0x0E;
            device.Features[0x0003] = 0x02;
            device.Clusters = new[]
            {
                ((ushort)0x0001, new ushort[] { 0x0000, 0x0001, 0x000A, 0x0015, 0x0016, 0x0017 }),
                ((ushort)0x0002, new ushort[] { 0x0000, 0x0001, 0x000A, 0x0015 }),
            };
            device.MultiCluster = new ushort[] { 0x000C, 0x0013, 0x0016, 0x0017, 0x000A, 0x0015, 0x0018, 0x0019 };
            device.ZonePages = new[]
            {
                HidppHapticsTests.Hex("FF FF FF FF FF FF FF FF FF FF FF FF FF FF", 14),
                HidppHapticsTests.Hex("00 00 00 00 00 7A 00 00 F0 1F 00 00 04 00", 14),
                new byte[14],
            };
            device.Model = new byte[] { 0xB3, 0x8A, 0x40, 0xB5, 0xC3, 0x56 };
            return device;
        }
    }

    /// <summary>
    /// What a frame to each HID++ feature may be (#494), for every test that
    /// reads what the lighting built or what the worker sent: the functions
    /// this build uses and no other, and never a value that writes a
    /// device's memory. The values that would, and the calls left out with
    /// them:
    ///
    /// <para>0x8071 setRgbClusterEffect with bit 1 of byte 12 set,
    /// "non-volatile (EEPROM)" (Logitech's "x8071 - RGB Effects", version 4,
    /// p.11). 0x8071 function 3, manageNvConfig, which sets a non-volatile
    /// capability (p.19), and functions 2, 4, 6, 7 and 9 of the list on
    /// p.1-2, the last of them shutdown. 0x8081 frameEnd with byte 0 at 1,
    /// "volatile and non-volatile (RAM and EEPROM)" (Logitech's "x8081 -
    /// Per-Key Lighting", version 0, p.16). 0x8070 setZoneEffect with byte
    /// 12 at 1 or 2, "volatile and non-volatile (RAM and EEPROM)" and
    /// "non-volatile only (EEPROM)" (Logitech's "x8070 - Color LED
    /// Effects", version 7, p.6-7), the byte libratbag sets to 1 to "write
    /// to RAM and flash" (hidpp20.c:1032), and with it the functions of
    /// that feature this build has no use for, setNvConfig among them
    /// (p.1). 0x8100 functions 6 to 8, the writes to the profile memory
    /// (hidpp20.c:1789-1791).</para>
    /// </summary>
    internal static class HidppFrameRules
    {
        /// <summary>Null for a frame the lighting, the probe and rumble may
        /// send to <paramref name="feature"/>, else what is wrong with it.</summary>
        public static string Refusal(ushort? feature, byte[] frame)
        {
            int function = frame[3] >> 4;
            byte P(int i) => frame[4 + i];
            bool ok;
            switch (feature)
            {
                case 0x0000: ok = function == 0; break;
                case HidppUnitProtocol.DeviceInformation: ok = function == 0; break;
                case HidppUnitProtocol.DeviceTypeAndName: ok = function <= 2; break;
                case HidppUnitProtocol.BatteryStatus: ok = function == 0; break;
                case HidppUnitProtocol.UnifiedBattery: ok = function == 1; break;
                case HidppHapticProtocol.HapticFeature: ok = function is 0 or 1 or 4; break;
                case HidppUnitProtocol.ColorLedEffects:
                    ok = function is 0 or 1 or 2 or 8 || (function == 3 && P(12) == 0);
                    break;
                case HidppUnitProtocol.RgbEffects:
                    ok = function == 0
                         // The effect: volatile memory alone, in the full power mode.
                         || (function == 1 && P(12) == 0x01)
                         // The claim: a read, or a write of the flags this build sets.
                         || (function == 5 && (P(0) == 0x00 || (P(0) == 0x01 && P(1) is 0x00 or 0x01 or 0x03
                                                                   && P(2) is 0x00 or 0x05 or 0x07)))
                         // The power mode: set, Full.
                         || (function == 8 && P(0) == 0x01 && P(1) == 0x01);
                    break;
                // 0x8080: the key list, key colors, fills and the commit. The
                // key list's fifth byte is OpenRGB's persistence option,
                // volatile (ReadPerKey8080Colors).
                case HidppUnitProtocol.PerKeyLighting:
                    ok = function is 0 or 1 or 3 or 4 or 5 || (function == 2 && P(4) == 0x00);
                    break;
                case HidppUnitProtocol.PerKeyLightingV2:
                    ok = function is 0 or 1 or 5 or 6 || (function == 7 && P(0) == 0x00);
                    break;
                case HidppUnitProtocol.KeyboardLayout: ok = function == 0; break;
                case HidppUnitProtocol.OnboardProfiles:
                    ok = function == 2 || (function == 1 && P(0) is 0x01 or 0x02);
                    break;
                case HidppUnitProtocol.ProfileManagement:
                    ok = function == 6 && P(0) is 0x00 or 0x03 or 0x05;
                    break;
                default:
                    return feature == null ? "no feature at index 0x" + frame[2].ToString("X2") : $"feature {feature:X4}";
            }
            return ok ? null : $"feature {feature:X4} function {function}: {HidppHapticsTests.Text(frame)}";
        }
    }

    /// <summary>
    /// The frames of Logitech lighting beyond the zone-only class (#494),
    /// byte for byte against the captures of Logitech's own programs and
    /// against Logitech's documents. Every expected frame is written out in
    /// hex. No Logitech 0x8071 or per-key device is on the bench, so the
    /// device side is what the references show and no more.
    /// </summary>
    public class HidppLightFrameTests
    {
        private static void Same(string expected, byte[] frame)
            => Assert.Equal(HidppHapticsTests.Text(HidppHapticsTests.Hex(expected)), HidppHapticsTests.Text(frame));

        /// <summary>The 0x8071 frames Logitech's own program sent a wired
        /// G815, with 0x8071 at index 0x0F and the software ID this worker
        /// uses (g810-led-resources dumps/g815/G815-Hotplug-to-start.pcapng
        /// and G815-FX-color-8D40FF.pcapng).</summary>
        [Fact]
        public void TheClusterFrames_AreTheOnesLogitechsOwnProgramSends()
        {
            Same("11 FF 0F 0C FF FF 00", HidppUnitProtocol.RgbGetInfo(0xFF, 0x0F, 0xFF, 0xFF));
            Same("11 FF 0F 0C 01 FF 00", HidppUnitProtocol.RgbGetInfo(0xFF, 0x0F, 0x01, 0xFF));
            Same("11 FF 0F 0C 01 01 00", HidppUnitProtocol.RgbGetInfo(0xFF, 0x0F, 0x01, 0x01));
            Same("11 FF 0F 5C 00", HidppUnitProtocol.RgbSwControlRead(0xFF, 0x0F));
            Same("11 FF 0F 5C 01 03 07", HidppUnitProtocol.RgbSwControl(0xFF, 0x0F, 0x03, 0x07));
            Same("11 FF 0F 8C 01 01", HidppUnitProtocol.RgbPowerFull(0xFF, 0x0F));
            Same("11 FF 0F 5C 01 03 05", HidppUnitProtocol.RgbSwControl(0xFF, 0x0F, 0x03, 0x05));
            Same("11 FF 0F 1C 01 01 8D 40 FF 02 00 00 00 00 00 00 01 00 00 00",
                HidppUnitProtocol.RgbFixedColor(0xFF, 0x0F, new HidppZone(1, 1), 0x8D, 0x40, 0xFF));
            Same("11 FF 11 1C 02", HidppUnitProtocol.ModeWrite(HidppUnitProtocol.OnboardProfiles, 0xFF, 0x11, 0x02));
            Same("11 FF 11 2C", HidppUnitProtocol.ModeRead(HidppUnitProtocol.OnboardProfiles, 0xFF, 0x11));
        }

        /// <summary>The claim with the cluster flag alone and the release,
        /// by Logitech's document: function 5, [set, flags, events]. The
        /// Fixed frame carries 0x01 in byte 12, full power and volatile
        /// memory, and 0x02 before it for black as well, the frame
        /// Logitech's own program sends a G915 X for all lights off (OpenRGB
        /// issue 4461, all_off.pcapng: 11 FF 08 1D 00 01 00 00 00 02 00 00
        /// 00 00 00 00 01).</summary>
        [Fact]
        public void TheLightestClaimAndTheRelease_AreFunctionFive_AndBlackKeepsItsMarker()
        {
            Same("11 01 0A 5C 01 01 00", HidppUnitProtocol.RgbSwControl(0x01, 0x0A, HidppUnitProtocol.SwControlClusters, 0x00));
            Same("11 01 0A 5C 01 00 00", HidppUnitProtocol.RgbSwControl(0x01, 0x0A, 0x00, 0x00));
            byte[] black = HidppUnitProtocol.RgbFixedColor(0xFF, 0x08, new HidppZone(0, 1), 0, 0, 0);
            Same("11 FF 08 1C 00 01 00 00 00 02 00 00 00 00 00 00 01", black);
            Assert.Equal(0x01, black[16]);
            Assert.Equal(0x02, black[9]);
            // The persistence bits are the low two of byte 12: bit 1 would
            // write the device's memory.
            Assert.Equal(0, black[16] & 0x02);
        }

        /// <summary>The frames the integration rules rest on, for device
        /// index 1. The 0x8070 claim's bytes on a 0x8071 index are the
        /// power mode call and no claim, which is why a hand-back is kept
        /// as frames and never built from an index.</summary>
        [Fact]
        public void TheFramesOfTheTwoEffectFeatures_AreNotEachOthers()
        {
            Same("11 01 06 8C 01 01", HidppUnitProtocol.SetSwControl(0x01, 0x06, claim: true));
            Same("11 01 06 3C 00 01 12 34 56 02", HidppUnitProtocol.SetStaticColor(0x01, 0x06, new HidppZone(0, 1), 0x12, 0x34, 0x56));
            Same("11 01 06 8C 00 00", HidppUnitProtocol.SetSwControl(0x01, 0x06, claim: false));
            Same("11 01 06 5C 01 01 00", HidppUnitProtocol.RgbSwControl(0x01, 0x06, 0x01, 0x00));
            Same("11 01 06 5C 01 00 00", HidppUnitProtocol.RgbSwControl(0x01, 0x06, 0x00, 0x00));
            Same("11 01 06 1C 00 01 12 34 56 02 00 00 00 00 00 00 01",
                HidppUnitProtocol.RgbFixedColor(0x01, 0x06, new HidppZone(0, 1), 0x12, 0x34, 0x56));

            Assert.Equal(HidppUnitProtocol.SetSwControl(0x01, 0x06, claim: true), HidppUnitProtocol.RgbPowerFull(0x01, 0x06));
            Assert.NotEqual(HidppUnitProtocol.SetSwControl(0x01, 0x06, claim: true),
                HidppUnitProtocol.RgbSwControl(0x01, 0x06, 0x01, 0x00));
            Assert.NotEqual(HidppUnitProtocol.SetSwControl(0x01, 0x06, claim: false),
                HidppUnitProtocol.RgbSwControl(0x01, 0x06, 0x00, 0x00));
        }

        /// <summary>The mode frames of both features: 0x8100 reads with
        /// function 2 and sets with function 1, 1 on-board and 2 host
        /// (libratbag hidpp20.c:1784-1785 and 1798-1800). 0x8101 does both
        /// with function 6, 5 host and 3 on-board, and reads with 00, the
        /// read a G915 X answered in both modes (OpenRGB issue 4461,
        /// unplug_and_replug.pcapng).</summary>
        [Fact]
        public void TheModeFrames_FollowEachFeaturesOwnNumbers()
        {
            const ushort onboard = HidppUnitProtocol.OnboardProfiles, profile = HidppUnitProtocol.ProfileManagement;
            Same("11 01 09 2C", HidppUnitProtocol.ModeRead(onboard, 0x01, 0x09));
            Same("11 01 09 1C 02", HidppUnitProtocol.ModeWrite(onboard, 0x01, 0x09, HidppUnitProtocol.HostMode(onboard)));
            Same("11 01 09 1C 01", HidppUnitProtocol.ModeWrite(onboard, 0x01, 0x09, HidppUnitProtocol.OnBoardMode(onboard)));
            Same("11 01 09 6C 00", HidppUnitProtocol.ModeRead(profile, 0x01, 0x09));
            Same("11 01 09 6C 05", HidppUnitProtocol.ModeWrite(profile, 0x01, 0x09, HidppUnitProtocol.HostMode(profile)));
            Same("11 01 09 6C 03", HidppUnitProtocol.ModeWrite(profile, 0x01, 0x09, HidppUnitProtocol.OnBoardMode(profile)));
            Same("11 FF 0E 6C 05", HidppUnitProtocol.ModeWrite(profile, 0xFF, 0x0E, 0x05));
        }

        /// <summary>The 0x8081 frames, with the feature at index 0x09 as on
        /// the G915 X of OpenRGB issue 4461: the zone page question, one
        /// range, the frame end with every byte zero, and the Host
        /// streaming choice on cluster FF, which is that capture's
        /// 11 FF 08 1D FF 01 ... 01 with this worker's software ID.</summary>
        [Fact]
        public void TheStreamFrames_AreTheOnesLogitechsOwnProgramSendsAG915X()
        {
            Same("11 FF 09 0C 00 01 00", HidppUnitProtocol.PerKeyZonePage(0xFF, 0x09, 1));
            Same("11 FF 09 5C 01 D2 FD 76 00", HidppUnitProtocol.PerKeyRange(0xFF, 0x09, 0x01, 0xD2, 0xFD, 0x76, 0x00));
            Same("11 01 07 5C 01 FE 12 34 56", HidppUnitProtocol.PerKeyRange(0x01, 0x07, 0x01, 0xFE, 0x12, 0x34, 0x56));
            // The byte after the one range ends the list.
            Assert.Equal(0x00, HidppUnitProtocol.PerKeyRange(0x01, 0x07, 0x01, 0xFE, 0x12, 0x34, 0x56)[9]);
            byte[] end = HidppUnitProtocol.PerKeyFrameEnd(0xFF, 0x09);
            Same("11 FF 09 7C", end);
            Assert.All(end.Skip(4), b => Assert.Equal(0, b));
            Same("11 01 07 7C", HidppUnitProtocol.PerKeyFrameEnd(0x01, 0x07));
            Same("11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01 00 00 00", HidppUnitProtocol.RgbHostStreaming(0xFF, 0x08, 0x01));
            Same("11 FF 08 0C FF 01 00", HidppUnitProtocol.RgbGetInfo(0xFF, 0x08, 0xFF, 0x01));
            Same("11 FF 08 5C 00", HidppUnitProtocol.RgbSwControlRead(0xFF, 0x08));
        }

        /// <summary>The 0x8080 frames Logitech Gaming Software sent a G810
        /// for a fixed 00 FF 00 (g810-led-resources
        /// dumps/g810-spectrum/set_fx_color_00ff00.pcapng): the indicator
        /// fill, the media fill with its red raised to 1, and the commit.
        /// Black keeps its red at 0, so black is dark.</summary>
        [Fact]
        public void TheFillFrames_AreTheOnesLogitechGamingSoftwareSendsAG810()
        {
            Same("11 FF 0C 4C 00 40 00 FF 00", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeIndicators, 0x00, 0xFF, 0x00));
            Same("11 FF 0C 4C 00 02 01 FF 00", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeMedia,
                HidppUnitProtocol.MediaRed(0x00, 0xFF, 0x00), 0xFF, 0x00));
            Same("11 FF 0C 5C", HidppUnitProtocol.PerKeyCommit(0xFF, 0x0C));
            Assert.Equal(0x00, HidppUnitProtocol.MediaRed(0x00, 0x00, 0x00));
            Assert.Equal(0x01, HidppUnitProtocol.MediaRed(0x00, 0x00, 0x01));
            Assert.Equal(0x80, HidppUnitProtocol.MediaRed(0x80, 0xFF, 0x00));
            Same("11 FF 0C 4C 00 02 00 00 00", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeMedia,
                HidppUnitProtocol.MediaRed(0, 0, 0), 0, 0));
            Same("11 FF 0C 4C 00 01", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeKeys, 0, 0, 0));
            Same("11 FF 0C 4C 00 04", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeGKeys, 0, 0, 0));
            Same("11 FF 0C 4C 00 10", HidppUnitProtocol.PerKeyFill(0xFF, 0x0C, HidppUnitProtocol.KeyTypeLogo, 0, 0, 0));
        }

        /// <summary>The G915 TKL is known by its model ID on any transport:
        /// its receiver ID in the place of the receiver's link, or its USB
        /// ID in the place of USB (Solaar docs/devices, its dump for 408E:
        /// "Model ID: B35F408EC343"). An ID in another transport's place is
        /// another device's.</summary>
        [Fact]
        public void TheKeyboardThatMustStayOnBoard_IsKnownByItsModelId_OnAnyTransport()
        {
            static HidppReply Info(byte transports, params byte[] model)
                => HidppHapticsTests.Ans(0x01, 0x02, 0, new byte[] { 0x05, 1, 2, 3, 4, 0x00, transports }.Concat(model).ToArray());

            Assert.True(HidppUnitProtocol.KeepsOnBoardMode(Info(0x0E, 0xB3, 0x5F, 0x40, 0x8E, 0xC3, 0x43)));
            // The G915: B354407CC33E.
            Assert.False(HidppUnitProtocol.KeepsOnBoardMode(Info(0x0E, 0xB3, 0x54, 0x40, 0x7C, 0xC3, 0x3E)));
            // The G915 X: B38A40B5C356.
            Assert.False(HidppUnitProtocol.KeepsOnBoardMode(Info(0x0E, 0xB3, 0x8A, 0x40, 0xB5, 0xC3, 0x56)));
            // One transport alone: the receiver's link, or USB.
            Assert.True(HidppUnitProtocol.KeepsOnBoardMode(Info(0x04, 0x40, 0x8E, 0, 0, 0, 0)));
            Assert.True(HidppUnitProtocol.KeepsOnBoardMode(Info(0x08, 0xC3, 0x43, 0, 0, 0, 0)));
            // The same numbers in the place of Bluetooth name no such keyboard.
            Assert.False(HidppUnitProtocol.KeepsOnBoardMode(Info(0x01, 0x40, 0x8E, 0, 0, 0, 0)));
            Assert.False(HidppUnitProtocol.KeepsOnBoardMode(Info(0x02, 0xC3, 0x43, 0, 0, 0, 0)));
            Assert.False(HidppUnitProtocol.KeepsOnBoardMode(Info(0x00, 0x40, 0x8E, 0xC3, 0x43, 0, 0)));
        }
    }

    /// <summary>
    /// The light plans (#494): for each pair of features, the frames that
    /// claim, paint, hold and hand back at each claim level, as lists a
    /// worker sends in order. Pure, so every list is compared whole.
    /// </summary>
    public class HidppLightPlanTests
    {
        private static string[] Text(IEnumerable<HidppStep> steps)
            => steps.Select(s => (s.Kind == HidppStepKind.Write ? "" : s.Kind.ToString().ToLowerInvariant() + " ")
                                 + HidppHapticsTests.Text(s.Frame)).ToArray();

        private static void Same(HidppStep[] steps, params string[] expected) => Assert.Equal(expected, Text(steps));

        /// <summary>A zone-only 0x8070 unit at device index 1.</summary>
        internal static HidppUnit ZoneUnit(bool probed = true) => new()
        {
            ChannelPath = "rx", DeviceIndex = 1, DeviceType = 3,
            RgbFeatureId = HidppUnitProtocol.ColorLedEffects, RgbIndex = 0x0E,
            EffectFeatureId = probed ? HidppUnitProtocol.ColorLedEffects : (ushort)0,
            EffectIndex = probed ? (byte)0x0E : (byte)0,
            Zones = new[] { new HidppZone(0, 1) },
        };

        /// <summary>A G810 on its cable: 0x8070 at 0x0D, 0x8080 at 0x0C.</summary>
        internal static HidppUnit BoardUnit() => new()
        {
            ChannelPath = "usb", DeviceIndex = 0xFF, DeviceType = 0,
            RgbFeatureId = HidppUnitProtocol.PerKeyLighting, RgbIndex = 0x0C,
            EffectFeatureId = HidppUnitProtocol.ColorLedEffects, EffectIndex = 0x0D,
            PerKeyFeatureId = HidppUnitProtocol.PerKeyLighting, PerKeyIndex = 0x0C,
            Zones = new[] { new HidppZone(0, 1), new HidppZone(1, 1) },
        };

        /// <summary>A 0x8071 device behind a receiver, clusters only: 0x8071
        /// at 0x0A with two clusters and 0x8100 at 0x15.</summary>
        internal static HidppUnit ClusterUnit(int level = 2, ushort mode = HidppUnitProtocol.OnboardProfiles) => new()
        {
            ChannelPath = "rx", DeviceIndex = 1, DeviceType = 3,
            RgbFeatureId = HidppUnitProtocol.RgbEffects, RgbIndex = 0x0A,
            EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = 0x0A,
            Zones = new[] { new HidppZone(0, 1), new HidppZone(1, 1) },
            ModeFeatureId = mode, ModeIndex = mode == 0 ? (byte)0 : (byte)0x15, MaxClaimLevel = level,
        };

        /// <summary>A G915 X on its cable: 0x8071 at 0x08, 0x8081 at 0x09
        /// with zones 0x01 to 0xD2, Host streaming at entry 1 and 0x8101 at
        /// 0x0E.</summary>
        internal static HidppUnit StreamUnit(int hostStreaming = 1, int type = 0) => new()
        {
            ChannelPath = "usb", DeviceIndex = 0xFF, DeviceType = type,
            RgbFeatureId = HidppUnitProtocol.RgbEffects, RgbIndex = 0x08,
            EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = 0x08,
            Zones = new[] { new HidppZone(0, 1), new HidppZone(1, 1) },
            PerKeyFeatureId = HidppUnitProtocol.PerKeyLightingV2, PerKeyIndex = 0x09, PerKeyMin = 0x01, PerKeyMax = 0xD2,
            HostStreamingIndex = hostStreaming,
            ModeFeatureId = HidppUnitProtocol.ProfileManagement, ModeIndex = 0x0E, MaxClaimLevel = 2,
        };

        [Fact]
        public void EachPairOfFeaturesHasItsPlan_AndAUnitWithNothingToPaintHasNone()
        {
            Assert.IsType<ZonePlan>(HidppLightPlan.For(ZoneUnit()));
            // A unit built from its first RGB feature alone is the same class.
            Assert.IsType<ZonePlan>(HidppLightPlan.For(ZoneUnit(probed: false)));
            Assert.IsType<ZonePerKeyPlan>(HidppLightPlan.For(BoardUnit()));
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(ClusterUnit()));
            Assert.IsType<ClusterStreamPlan>(HidppLightPlan.For(StreamUnit()));
            // A mouse with both features stays on its clusters, and so does
            // a keyboard whose 0x8081 reports no zone.
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(StreamUnit(type: 3)));
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(StreamUnit() with { PerKeyMin = 0, PerKeyMax = 0 }));
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(StreamUnit(type: -1)));

            Assert.Null(HidppLightPlan.For(null));
            Assert.Null(HidppLightPlan.For(new HidppUnit()));
            Assert.Null(HidppLightPlan.For(ClusterUnit() with { Zones = Array.Empty<HidppZone>() }));
            // A per-key feature with no effect feature has no claim to take.
            Assert.Null(HidppLightPlan.For(new HidppUnit
            {
                RgbFeatureId = HidppUnitProtocol.PerKeyLighting, RgbIndex = 0x0C,
                PerKeyFeatureId = HidppUnitProtocol.PerKeyLighting, PerKeyIndex = 0x0C,
            }));

            Assert.Equal((0, 33, 0), (HidppLightPlan.For(ZoneUnit()).FrameGapMs, HidppLightPlan.For(ZoneUnit()).PaintMs,
                HidppLightPlan.For(ZoneUnit()).MaxLevel));
            Assert.Equal((0, 33, 0), (HidppLightPlan.For(BoardUnit()).FrameGapMs, HidppLightPlan.For(BoardUnit()).PaintMs,
                HidppLightPlan.For(BoardUnit()).MaxLevel));
            Assert.Equal((8, 50, 2), (HidppLightPlan.For(ClusterUnit()).FrameGapMs, HidppLightPlan.For(ClusterUnit()).PaintMs,
                HidppLightPlan.For(ClusterUnit()).MaxLevel));
            Assert.Equal((8, 50, 1), (HidppLightPlan.For(ClusterUnit(1)).FrameGapMs, HidppLightPlan.For(ClusterUnit(1)).PaintMs,
                HidppLightPlan.For(ClusterUnit(1)).MaxLevel));
            Assert.Equal((8, 50, 2), (HidppLightPlan.For(StreamUnit()).FrameGapMs, HidppLightPlan.For(StreamUnit()).PaintMs,
                HidppLightPlan.For(StreamUnit()).MaxLevel));
        }

        /// <summary>The zone-only plan is the first build's frames: the
        /// claim, one static effect per zone, both again at a keep-alive,
        /// [00 00] at the hand-back, whatever level is asked for.</summary>
        [Fact]
        public void TheZonePlan_IsTheFramesTheFirstBuildSent()
        {
            var plan = HidppLightPlan.For(ZoneUnit());
            foreach (int level in new[] { 0, 1, 2 })
            {
                Same(plan.Claim(level, 0), "11 01 0E 8C 01 01");
                foreach (bool painted in new[] { true, false })
                    Same(plan.KeepAlive(level, 0xFF0000, painted), "11 01 0E 8C 01 01", "11 01 0E 3C 00 01 FF 00 00 02");
                Same(plan.HandBack(level, 0), "11 01 0E 8C");
            }
            Same(plan.Paint(0xFF0000, full: true), "11 01 0E 3C 00 01 FF 00 00 02");
            Same(plan.Paint(0x0000FF, full: false), "11 01 0E 3C 00 01 00 00 FF 02");
            Same(plan.Paint(0x000000, full: false), "11 01 0E 3C 00 01");
            Assert.Null(plan.ModeRead);
            Assert.Null(plan.ClaimRead);
            Assert.Null(plan.WithoutPerKey);
            Assert.False(plan.SwitchesMode(2, 0x01));
        }

        /// <summary>The G810 plan: the claim, the two zones, then the fills
        /// and the commit Logitech Gaming Software sends for a fixed color
        /// (g810-led-resources dumps/g810-spectrum/set_fx_color_00ff00.pcapng),
        /// with the commit awaited. Black fills the keys, the G-keys and
        /// the logo as well, and the hand-back leaves the indicators and
        /// the media keys white before it releases.</summary>
        [Fact]
        public void TheBoardPlan_AddsTheVendorsFillsAndAnAwaitedCommit()
        {
            var plan = HidppLightPlan.For(BoardUnit());
            Same(plan.Claim(0, 0), "11 FF 0D 8C 01 01");
            foreach (bool full in new[] { true, false })
            {
                Same(plan.Paint(0x00FF00, full),
                    "11 FF 0D 3C 00 01 00 FF 00 02", "11 FF 0D 3C 01 01 00 FF 00 02",
                    "fill 11 FF 0C 4C 00 40 00 FF", "fill 11 FF 0C 4C 00 02 01 FF",
                    "commit 11 FF 0C 5C");
                Same(plan.Paint(0xFF0000, full),
                    "11 FF 0D 3C 00 01 FF 00 00 02", "11 FF 0D 3C 01 01 FF 00 00 02",
                    "fill 11 FF 0C 4C 00 40 FF", "fill 11 FF 0C 4C 00 02 FF",
                    "commit 11 FF 0C 5C");
                Same(plan.Paint(0x000000, full),
                    "11 FF 0D 3C 00 01", "11 FF 0D 3C 01 01",
                    "fill 11 FF 0C 4C 00 40", "fill 11 FF 0C 4C 00 02",
                    "fill 11 FF 0C 4C 00 01", "fill 11 FF 0C 4C 00 04", "fill 11 FF 0C 4C 00 10",
                    "commit 11 FF 0C 5C");
            }
            Same(plan.KeepAlive(0, 0x00FF00, painted: true),
                "11 FF 0D 8C 01 01",
                "11 FF 0D 3C 00 01 00 FF 00 02", "11 FF 0D 3C 01 01 00 FF 00 02",
                "fill 11 FF 0C 4C 00 40 00 FF", "fill 11 FF 0C 4C 00 02 01 FF",
                "commit 11 FF 0C 5C");
            Same(plan.HandBack(0, 0),
                "fill 11 FF 0C 4C 00 40 FF FF FF", "fill 11 FF 0C 4C 00 02 FF FF FF",
                "commit 11 FF 0C 5C",
                "11 FF 0D 8C");
            // A refused fill keeps the board on its keys: only a refused key
            // frame sends it to its zones.
            Assert.False(plan.FallsBackOn(HidppStepKind.Fill));
        }

        /// <summary>The cluster plan's claim at each level. Level 0 is
        /// function 5 with the cluster flag alone, levels 1 and 2 the claim
        /// Logitech's own program makes (g810-led-resources
        /// dumps/g815/G815-Hotplug-to-start.pcapng: 5C 01 03 07, 8C 01 01,
        /// 5C 01 03 05), and level 2 puts the switch to host mode first,
        /// only for a device read in its on-board memory mode.</summary>
        [Fact]
        public void TheClusterPlan_ClaimsByLevel_AndSwitchesOnlyADeviceReadOnBoard()
        {
            var plan = HidppLightPlan.For(ClusterUnit());
            Same(plan.Claim(0, 0x01), "11 01 0A 5C 01 01");
            Same(plan.Claim(1, 0x01), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Same(plan.Claim(2, 0x01), "11 01 15 1C 02", "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            // Read in host mode already, or not read at all: left alone.
            Same(plan.Claim(2, 0x02), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Same(plan.Claim(2, 0x00), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Assert.True(plan.SwitchesMode(2, 0x01));
            Assert.False(plan.SwitchesMode(1, 0x01));
            Assert.False(plan.SwitchesMode(0, 0x01));
            Assert.False(plan.SwitchesMode(2, 0x02));
            Assert.False(plan.SwitchesMode(2, 0x00));
            Assert.Equal(HidppHapticsTests.Hex("11 01 15 2C"), plan.ModeRead);
            Assert.Equal(0x02, plan.ModeOf(HidppHapticsTests.Ans(1, 0x15, 2, 0x02)));

            // Feature 0x8101: read with 00, 3 on-board, 5 host.
            var profile = HidppLightPlan.For(ClusterUnit(mode: HidppUnitProtocol.ProfileManagement));
            Assert.Equal(HidppHapticsTests.Hex("11 01 15 6C"), profile.ModeRead);
            Same(profile.Claim(2, 0x03), "11 01 15 6C 05", "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Same(profile.Claim(2, 0x00), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Same(profile.Claim(2, 0x05), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
            Same(profile.HandBack(2, 0x03), "11 01 0A 5C 01", "11 01 15 6C 03");
            Same(profile.HandBack(2, 0x00), "11 01 0A 5C 01");

            // No mode feature: nothing to read and nothing to switch.
            var none = HidppLightPlan.For(ClusterUnit(level: 1, mode: 0));
            Assert.Null(none.ModeRead);
            Assert.False(none.SwitchesMode(2, 0x01));
            Same(none.Claim(2, 0x01), "11 01 0A 5C 01 03 07", "11 01 0A 8C 01 01", "11 01 0A 5C 01 03 05");
        }

        /// <summary>The cluster plan paints one Fixed effect per cluster,
        /// byte 12 at 01, holds the claim with the frames of its level, and
        /// hands back with [01 00 00], then the mode it switched away from
        /// and nothing it did not switch.</summary>
        [Fact]
        public void TheClusterPlan_PaintsHoldsAndHandsBack()
        {
            var plan = HidppLightPlan.For(ClusterUnit());
            string[] red =
            {
                "11 01 0A 1C 00 01 FF 00 00 02 00 00 00 00 00 00 01",
                "11 01 0A 1C 01 01 FF 00 00 02 00 00 00 00 00 00 01",
            };
            Same(plan.Paint(0xFF0000, full: true), red);
            Same(plan.Paint(0xFF0000, full: false), red);
            Same(plan.Paint(0x000000, full: false),
                "11 01 0A 1C 00 01 00 00 00 02 00 00 00 00 00 00 01",
                "11 01 0A 1C 01 01 00 00 00 02 00 00 00 00 00 00 01");

            foreach (bool painted in new[] { true, false })
            {
                Same(plan.KeepAlive(0, 0xFF0000, painted), new[] { "11 01 0A 5C 01 01" }.Concat(red).ToArray());
                // Never 01 03 07 and never the mode frame.
                Same(plan.KeepAlive(1, 0xFF0000, painted), new[] { "11 01 0A 5C 01 03 05", "11 01 0A 8C 01 01" }.Concat(red).ToArray());
                Same(plan.KeepAlive(2, 0xFF0000, painted), new[] { "11 01 0A 5C 01 03 05", "11 01 0A 8C 01 01" }.Concat(red).ToArray());
            }

            Same(plan.HandBack(0, 0x01), "11 01 0A 5C 01");
            Same(plan.HandBack(1, 0x01), "11 01 0A 5C 01");
            Same(plan.HandBack(2, 0x01), "11 01 0A 5C 01", "11 01 15 1C 01");
            Same(plan.HandBack(2, 0x02), "11 01 0A 5C 01");
            Same(plan.HandBack(2, 0x00), "11 01 0A 5C 01");
            Assert.Null(plan.ClaimRead);
            Assert.Null(plan.WithoutPerKey);
        }

        /// <summary>The stream plan's first paint: the cluster colors, the
        /// Host streaming choice, then the range and the frame end twice.
        /// A later paint, and the keep-alive while the claim stands, is the
        /// range and the frame end alone, with no frame on the 0x8071
        /// index. Every per-key frame is awaited.</summary>
        [Fact]
        public void TheStreamPlan_PaintsTheClustersOnce_ThenThePerKeyFramesAlone()
        {
            var plan = HidppLightPlan.For(StreamUnit());
            string[] range = { "stream 11 FF 09 5C 01 D2 FD 76", "commit 11 FF 09 7C" };
            Same(plan.Paint(0xFD7600, full: true), new[]
            {
                "11 FF 08 1C 00 01 FD 76 00 02 00 00 00 00 00 00 01",
                "11 FF 08 1C 01 01 FD 76 00 02 00 00 00 00 00 00 01",
                "select 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01",
            }.Concat(range).Concat(range).ToArray());
            Same(plan.Paint(0xFD7600, full: false), range);
            Same(plan.KeepAlive(0, 0xFD7600, painted: true), range);
            Same(plan.KeepAlive(2, 0xFD7600, painted: true), range);
            // Until a first paint has landed the keep-alive is that paint.
            Assert.Equal(Text(plan.Paint(0xFD7600, full: true)), Text(plan.KeepAlive(0, 0xFD7600, painted: false)));
            Assert.Equal(Text(plan.Paint(0xFD7600, full: true)), Text(plan.KeepAlive(2, 0xFD7600, painted: false)));
            Same(plan.Paint(0x0000FF, full: false), "stream 11 FF 09 5C 01 D2 00 00 FF", "commit 11 FF 09 7C");
            Same(plan.Paint(0x000000, full: false), "stream 11 FF 09 5C 01 D2", "commit 11 FF 09 7C");

            // A keyboard that lists no Host streaming effect is sent none.
            Same(HidppLightPlan.For(StreamUnit(hostStreaming: -1)).Paint(0xFD7600, full: true), new[]
            {
                "11 FF 08 1C 00 01 FD 76 00 02 00 00 00 00 00 00 01",
                "11 FF 08 1C 01 01 FD 76 00 02 00 00 00 00 00 00 01",
            }.Concat(range).Concat(range).ToArray());

            // The claim, its levels and the hand-back are the cluster plan's.
            Same(plan.Claim(0, 0x03), "11 FF 08 5C 01 01");
            Same(plan.Claim(2, 0x03), "11 FF 0E 6C 05", "11 FF 08 5C 01 03 07", "11 FF 08 8C 01 01", "11 FF 08 5C 01 03 05");
            Same(plan.HandBack(0, 0x03), "11 FF 08 5C 01");
            Same(plan.HandBack(2, 0x03), "11 FF 08 5C 01", "11 FF 0E 6C 03");
        }

        /// <summary>The stream plan's keep-alive reads the claim first. The
        /// claim stands while the cluster flag is set, whatever else is,
        /// and the plan it falls back to paints the clusters alone.</summary>
        [Fact]
        public void TheStreamPlan_ReadsItsClaim_AndFallsBackToTheClusters()
        {
            var plan = HidppLightPlan.For(StreamUnit());
            Assert.Equal(HidppHapticsTests.Hex("11 FF 08 5C"), plan.ClaimRead);
            Assert.True(plan.ClaimStands(HidppHapticsTests.Ans(0xFF, 0x08, 5, 0x00, 0x01, 0x00)));
            Assert.True(plan.ClaimStands(HidppHapticsTests.Ans(0xFF, 0x08, 5, 0x00, 0x03, 0x05)));
            Assert.False(plan.ClaimStands(HidppHapticsTests.Ans(0xFF, 0x08, 5, 0x00, 0x00, 0x00)));
            Assert.False(plan.ClaimStands(HidppHapticsTests.Ans(0xFF, 0x08, 5, 0x00, 0x02, 0x04)));

            var fallback = plan.WithoutPerKey;
            Assert.IsType<ClusterPlan>(fallback);
            Same(fallback.Paint(0xFD7600, full: true),
                "11 FF 08 1C 00 01 FD 76 00 02 00 00 00 00 00 00 01",
                "11 FF 08 1C 01 01 FD 76 00 02 00 00 00 00 00 00 01");
            Assert.Equal(Text(plan.HandBack(2, 0x03)), Text(fallback.HandBack(2, 0x03)));
            Assert.Null(fallback.ClaimRead);
        }

        /// <summary>Every unit shape a plan is built for, with its features
        /// at the indexes its frames go to.</summary>
        internal static HidppUnit[] EveryUnitShape() => new[]
        {
            ZoneUnit(), ZoneUnit(probed: false), BoardUnit(),
            ClusterUnit(), ClusterUnit(level: 1), ClusterUnit(level: 0),
            ClusterUnit(mode: HidppUnitProtocol.ProfileManagement), ClusterUnit(level: 1, mode: 0),
            StreamUnit(), StreamUnit(hostStreaming: -1), StreamUnit(hostStreaming: 0), StreamUnit(type: 3),
        };

        /// <summary>Every frame a plan can produce: each list at each level,
        /// the levels a caller should never pass included, with each mode a
        /// read can give and a spread of colors, and the two reads.</summary>
        internal static List<byte[]> EveryFrame(HidppLightPlan plan)
        {
            var frames = new List<byte[]>();
            foreach (int level in new[] { -1, 0, 1, 2, 3 })
            foreach (byte saved in new byte[] { 0x00, 0x01, 0x02, 0x03, 0x05, 0xFF })
            {
                frames.AddRange(HidppLightPlan.Frames(plan.Claim(level, saved)));
                frames.AddRange(HidppLightPlan.Frames(plan.HandBack(level, saved)));
                foreach (int rgb in new[] { 0x000000, 0x123456, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF })
                {
                    frames.AddRange(HidppLightPlan.Frames(plan.KeepAlive(level, rgb, painted: true)));
                    frames.AddRange(HidppLightPlan.Frames(plan.KeepAlive(level, rgb, painted: false)));
                    frames.AddRange(HidppLightPlan.Frames(plan.Paint(rgb, full: true)));
                    frames.AddRange(HidppLightPlan.Frames(plan.Paint(rgb, full: false)));
                }
            }
            if (plan.ModeRead != null) frames.Add(plan.ModeRead);
            if (plan.ClaimRead != null) frames.Add(plan.ClaimRead);
            return frames;
        }

        /// <summary>No plan, at any level, for any color and after any mode
        /// read, builds a frame that writes a device's memory, or a call
        /// this build has no business making
        /// (<see cref="HidppFrameRules"/>): on a 0x8071 index only the
        /// claim, the power mode as "set Full", and effect frames whose
        /// byte 12 is 01, the non-volatile bit clear. On a 0x8081 index
        /// only the range and a frame end of zeros. On a 0x8080 index only
        /// the fill and the commit. On a 0x8070 index only the claim and
        /// the static effect with its persistence byte clear. On the mode
        /// feature's index only the read and the two modes. The plan a unit
        /// falls back to is held to the same.</summary>
        [Fact]
        public void NoPlanBuildsAFrameThatWritesTheDevicesMemory()
        {
            int seen = 0;
            foreach (var unit in EveryUnitShape())
            {
                var plans = new List<HidppLightPlan> { HidppLightPlan.For(unit) };
                if (plans[0].WithoutPerKey != null) plans.Add(plans[0].WithoutPerKey);
                foreach (var plan in plans)
                {
                    foreach (byte[] frame in EveryFrame(plan))
                    {
                        seen++;
                        string text = HidppHapticsTests.Text(frame);
                        Assert.Equal(HidppHapticProtocol.LongReportLength, frame.Length);
                        Assert.Equal(HidppHapticProtocol.LongReportId, frame[0]);
                        Assert.Equal(unit.DeviceIndex, frame[1]);
                        Assert.Equal(HidppHapticProtocol.SoftwareId, frame[3] & 0x0F);

                        // The index says which feature the frame is for: the
                        // effect feature, the per-key one or the mode's, and
                        // a plan writes to no other.
                        byte effectIndex = unit.EffectFeatureId != 0 ? unit.EffectIndex : unit.RgbIndex;
                        ushort? feature =
                            frame[2] == effectIndex ? (unit.EffectFeatureId != 0 ? unit.EffectFeatureId : unit.RgbFeatureId)
                            : frame[2] == unit.PerKeyIndex && unit.PerKeyIndex != 0 ? unit.PerKeyFeatureId
                            : frame[2] == unit.ModeIndex && unit.ModeIndex != 0 ? unit.ModeFeatureId
                            : null;
                        Assert.True(feature != null, "a frame to an index with no lighting feature: " + text);
                        Assert.True(feature != 0x0000, text);
                        string refusal = HidppFrameRules.Refusal(feature, frame);
                        Assert.True(refusal == null, refusal);

                        // A static effect on 0x8070 carries nothing past its
                        // color marker, and a frame end nothing at all.
                        if (feature == HidppUnitProtocol.ColorLedEffects && frame[3] >> 4 == HidppUnitProtocol.FunctionSetZoneEffect)
                            Assert.All(frame.Skip(10), b => Assert.Equal(0, b));
                        if (feature == HidppUnitProtocol.PerKeyLightingV2 && frame[3] >> 4 == HidppUnitProtocol.FunctionPerKeyV2FrameEnd)
                            Assert.All(frame.Skip(4), b => Assert.Equal(0, b));
                    }
                }
            }
            // The walk above saw frames, and a great many.
            Assert.True(seen > 5000, seen + " frames");
        }

        /// <summary>The rule the tests above and the worker benches hold
        /// every frame to refuses the frames that would write a device's
        /// memory, so a pass under it means something.</summary>
        [Theory]
        // 0x8071: the non-volatile bit, both bits, power save, then
        // manageNvConfig, the power timers and shutdown.
        [InlineData(0x8071, "11 01 06 1C 00 01 12 34 56 02 00 00 00 00 00 00 02")]
        [InlineData(0x8071, "11 01 06 1C 00 01 12 34 56 02 00 00 00 00 00 00 03")]
        [InlineData(0x8071, "11 01 06 1C 00 01 12 34 56 02 00 00 00 00 00 00 05")]
        [InlineData(0x8071, "11 01 06 1C 00 01 12 34 56 02")]
        [InlineData(0x8071, "11 01 06 2C 00 01")]
        [InlineData(0x8071, "11 01 06 3C 01 00 01 01")]
        [InlineData(0x8071, "11 01 06 4C 01 00 00")]
        [InlineData(0x8071, "11 01 06 6C 00 01")]
        [InlineData(0x8071, "11 01 06 7C 01 01")]
        [InlineData(0x8071, "11 01 06 7C 00")]
        [InlineData(0x8071, "11 01 06 8C 01 02")]
        [InlineData(0x8071, "11 01 06 8C 00")]
        [InlineData(0x8071, "11 01 06 9C")]
        [InlineData(0x8071, "11 01 06 5C 01 04 00")]
        // 0x8081: a frame end that persists, and the setters this build
        // does not use: five zones in a row and the two delta
        // compressions.
        [InlineData(0x8081, "11 01 07 7C 01")]
        [InlineData(0x8081, "11 01 07 2C 01 12 34 56")]
        [InlineData(0x8081, "11 01 07 3C 01 12 34 56")]
        [InlineData(0x8081, "11 01 07 4C 01 12 34 56")]
        // 0x4540: anything but the read of the layout code.
        [InlineData(0x4540, "11 01 0A 1C 01")]
        // 0x8070: the static effect written to flash.
        [InlineData(0x8070, "11 01 06 3C 00 01 12 34 56 02 00 00 00 00 00 00 01")]
        [InlineData(0x8070, "11 01 06 4C 00")]
        // 0x8080, 0x8100 and 0x8101: anything but the fill, the commit, the
        // mode read and the two modes.
        [InlineData(0x8080, "11 FF 0C 6C 00 01")]
        [InlineData(0x8100, "11 01 09 6C 00 01")]
        [InlineData(0x8100, "11 01 09 7C 00 01")]
        [InlineData(0x8100, "11 01 09 8C")]
        [InlineData(0x8100, "11 01 09 3C 00 01")]
        [InlineData(0x8100, "11 01 09 1C 03")]
        [InlineData(0x8101, "11 01 09 6C 01")]
        [InlineData(0x8101, "11 01 09 1C 05")]
        // A feature this build never writes to.
        [InlineData(0x4522, "11 01 0A 1C 01")]
        [InlineData(0x8040, "11 01 0A 1C 01")]
        public void TheFrameRule_RefusesWhatWouldWriteMemory_AndWhatThisBuildNeverSends(int feature, string frame)
        {
            Assert.NotNull(HidppFrameRules.Refusal((ushort)feature, HidppHapticsTests.Hex(frame)));
        }

        /// <summary>And it takes the frames the references show.</summary>
        [Theory]
        [InlineData(0x8071, "11 01 06 1C 00 01 12 34 56 02 00 00 00 00 00 00 01")]
        [InlineData(0x8071, "11 01 06 5C 01 01 00")]
        [InlineData(0x8071, "11 01 06 5C 01 00 00")]
        [InlineData(0x8071, "11 01 06 5C 01 03 07")]
        [InlineData(0x8071, "11 01 06 5C 01 03 05")]
        [InlineData(0x8071, "11 01 06 5C 00")]
        [InlineData(0x8071, "11 01 06 8C 01 01")]
        [InlineData(0x8071, "11 01 06 0C FF FF 00")]
        [InlineData(0x8081, "11 01 07 5C 01 FE 12 34 56")]
        [InlineData(0x8081, "11 01 07 1C 01 12 34 56")]
        [InlineData(0x8081, "11 01 07 6C 12 34 56 01 FE")]
        [InlineData(0x8081, "11 01 07 7C")]
        [InlineData(0x4540, "11 01 0A 0C")]
        [InlineData(0x8070, "11 01 06 8C 01 01")]
        [InlineData(0x8070, "11 01 06 8C 00 00")]
        [InlineData(0x8070, "11 01 06 3C 00 01 12 34 56 02")]
        [InlineData(0x8080, "11 FF 0C 4C 00 40 00 FF 00")]
        [InlineData(0x8080, "11 FF 0C 5C")]
        [InlineData(0x8100, "11 01 09 2C")]
        [InlineData(0x8100, "11 01 09 1C 02")]
        [InlineData(0x8100, "11 01 09 1C 01")]
        [InlineData(0x8101, "11 01 09 6C 00")]
        [InlineData(0x8101, "11 01 09 6C 05")]
        [InlineData(0x8101, "11 01 09 6C 03")]
        [InlineData(0x19B0, "11 01 0B 4C 02")]
        [InlineData(0x0000, "11 01 00 0C 80 71")]
        public void TheFrameRule_TakesTheFramesTheReferencesShow(int feature, string frame)
        {
            Assert.Null(HidppFrameRules.Refusal((ushort)feature, HidppHapticsTests.Hex(frame)));
        }
    }

    /// <summary>
    /// What the probe learns of a device lit beyond the zone-only class
    /// (#494): its effect feature and its per-key feature as separate
    /// pairs, the clusters of a 0x8071 with their Fixed effect, the zones of
    /// a 0x8081, its mode feature, and the claim levels it offers. Scripted
    /// from the answers the captures show.
    /// </summary>
    public class HidppLightProbeTests
    {
        private static string[] Asked(HidppHapticsTests.FakeChannel channel, byte feature)
            => channel.Traffic.Where(t => t.Frame[2] == feature).Select(t => HidppHapticsTests.Text(t.Frame)).ToArray();

        /// <summary>A zone-only device is asked exactly what it was asked
        /// before: the five lookups, its zones, its battery, and nothing of
        /// the mode features or the model ID.</summary>
        [Fact]
        public void AZoneOnlyDevice_IsAskedNothingMore_AndIsPaintedThroughTheFeatureFound()
        {
            var device = HidppLightDevice.ZoneOnly();
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x8081, 0x8080, 0x8070, 0x1004 }, channel.RootLookups);
            Assert.Equal((HidppUnitProtocol.ColorLedEffects, (byte)0x0E), (unit.EffectFeatureId, unit.EffectIndex));
            Assert.Equal((0, 0, 0), (unit.PerKeyFeatureId, unit.ModeFeatureId, unit.MaxClaimLevel));
            Assert.True(unit.DirectRgb && unit.HasLightPlan);
            Assert.IsType<ZonePlan>(HidppLightPlan.For(unit));
            Assert.Equal(-1, unit.HostStreamingIndex);
        }

        /// <summary>A G915 behind a receiver: the first pass finds 0x8071,
        /// and after the batteries the probe looks up 0x8081, walks both
        /// clusters to their Fixed effect and no further, reads the three
        /// zone pages, reads the layout code and the model IDs for the
        /// keyboard's light map (#557), then looks up 0x8101 and 0x8100.
        /// The model IDs read once serve the mode's question too. It
        /// offers every level.</summary>
        [Fact]
        public void AG915_EndsWithItsClustersItsZonesItsModeFeatureAndEveryLevel()
        {
            var device = HidppLightDevice.G915();
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(new ushort[]
            {
                0x19B0, 0x8071, 0x1004, 0x1000, 0x8081, 0x4540, 0x0003, 0x8101, 0x8100,
            }, channel.RootLookups);
            Assert.Equal(115, unit.PerKeyZones.Length);
            Assert.Equal(1, unit.KeyboardLayout);
            Assert.Equal(new ushort[] { 0xB354, 0x407C, 0xC33E }, unit.ModelIds);
            Assert.Single(Asked(channel, 0x10));
            Assert.Single(Asked(channel, 0x02));
            Assert.Equal(HidppUnitProtocol.RgbEffects, unit.RgbFeatureId);
            Assert.Equal((HidppUnitProtocol.RgbEffects, (byte)0x0A), (unit.EffectFeatureId, unit.EffectIndex));
            Assert.Equal((HidppUnitProtocol.PerKeyLightingV2, (byte)0x0B), (unit.PerKeyFeatureId, unit.PerKeyIndex));
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);
            Assert.Equal(((byte)0x01, (byte)0xD2), (unit.PerKeyMin, unit.PerKeyMax));
            Assert.Equal((HidppUnitProtocol.OnboardProfiles, (byte)0x15), (unit.ModeFeatureId, unit.ModeIndex));
            Assert.Equal(2, unit.MaxClaimLevel);
            Assert.Equal(-1, unit.HostStreamingIndex);
            Assert.False(unit.DirectRgb);
            Assert.True(unit.HasRgb && unit.HasLightPlan && unit.IsKeyboardKind);
            Assert.IsType<ClusterStreamPlan>(HidppLightPlan.For(unit));

            // The device, then each cluster and its effects up to Fixed.
            Assert.Equal(new[]
            {
                "11 01 0A 0C FF FF",
                "11 01 0A 0C 00 FF", "11 01 0A 0C", "11 01 0A 0C 00 01",
                "11 01 0A 0C 01 FF", "11 01 0A 0C 01", "11 01 0A 0C 01 01",
            }, Asked(channel, 0x0A));
            Assert.Equal(new[] { "11 01 0B 0C", "11 01 0B 0C 00 01", "11 01 0B 0C 00 02" }, Asked(channel, 0x0B));
        }

        /// <summary>The G915 TKL has the same features and never gets the
        /// level that switches its on-board memory mode off: its top row
        /// lives in the on-board profile (OpenRGB
        /// LogitechHIDPP20Controller.h:199 and 221).</summary>
        [Fact]
        public void TheG915Tkl_NeverOffersTheLevelThatLeavesItsOnBoardMode()
        {
            var device = HidppLightDevice.G915();
            device.Model = new byte[] { 0xB3, 0x5F, 0x40, 0x8E, 0xC3, 0x43 };
            var unit = device.Describe(device.Channel(), out bool incomplete);
            Assert.False(incomplete);
            Assert.Equal(1, unit.MaxClaimLevel);
            Assert.Equal(HidppUnitProtocol.OnboardProfiles, unit.ModeFeatureId);

            // A device that lists the feature and refuses the read cannot
            // be told from it, and is not switched either.
            var refusing = HidppLightDevice.G915();
            refusing.Refuse = (f, fn, p) => f == 0x02 ? (byte)0x05 : null;
            Assert.Equal(1, refusing.Describe(refusing.Channel(), out incomplete).MaxClaimLevel);
            Assert.False(incomplete);

            // One with no such feature is not that keyboard.
            var without = HidppLightDevice.G915();
            without.Features.Remove(0x0003);
            Assert.Equal(2, without.Describe(without.Channel(), out incomplete).MaxClaimLevel);
        }

        /// <summary>A G915 X, with the answers it gave Logitech's own
        /// program (OpenRGB issue 4461, unplug_and_replug.pcapng): 00 in the
        /// second byte of its device and cluster answers, its keys as
        /// cluster 0, Host streaming second among eight multi-cluster
        /// effects, and 126 zones from 0x01 to 0xD2. The walk for Host
        /// streaming stops where it finds it.</summary>
        [Fact]
        public void AG915X_IsReadAsItAnswersLogitechsOwnProgram()
        {
            var device = HidppLightDevice.G915X();
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);
            Assert.Equal(((byte)0x01, (byte)0xD2), (unit.PerKeyMin, unit.PerKeyMax));
            Assert.Equal(1, unit.HostStreamingIndex);
            Assert.Equal((HidppUnitProtocol.ProfileManagement, (byte)0x0E), (unit.ModeFeatureId, unit.ModeIndex));
            Assert.Equal(2, unit.MaxClaimLevel);
            Assert.IsType<ClusterStreamPlan>(HidppLightPlan.For(unit));
            // 0x8101 is found, so 0x8100 is never looked up.
            Assert.DoesNotContain((ushort)0x8100, channel.RootLookups);
            // Each question once: the 00 where the question carried FF is
            // the device's way and no reason to ask again. Entry 2 of the
            // multi-cluster list is never asked for.
            Assert.Equal(new[]
            {
                "11 FF 08 0C FF FF",
                "11 FF 08 0C 00 FF", "11 FF 08 0C", "11 FF 08 0C 00 01",
                "11 FF 08 0C 01 FF", "11 FF 08 0C 01", "11 FF 08 0C 01 01",
                "11 FF 08 0C FF", "11 FF 08 0C FF 01",
            }, Asked(channel, 0x08));
        }

        /// <summary>The Fixed effect is found by its ID wherever a cluster
        /// lists it. A cluster that lists none is left out, and so is one
        /// whose location is a state of a microphone's one light, whose
        /// effects are never asked for.</summary>
        [Fact]
        public void TheFixedEffectIsFoundById_AndAClusterWithNoneOrWithAStateForALocationIsLeftOut()
        {
            var device = HidppLightDevice.G903();
            device.Clusters = new[]
            {
                ((ushort)0x0001, new ushort[] { 0x0000, 0x000A, 0x0001 }),   // Fixed third
                ((ushort)0x0002, new ushort[] { 0x0000, 0x000A, 0x0003 }),   // no Fixed
                ((ushort)0x0081, new ushort[] { 0x0001 }),                   // a state, not a place
                ((ushort)0x0005, new ushort[] { 0x0001 }),                   // Fixed first
            };
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(new[] { new HidppZone(0, 2), new HidppZone(3, 0) }, unit.Zones);
            Assert.DoesNotContain("11 01 07 0C 02", Asked(channel, 0x07));
            Assert.Contains("11 01 07 0C 02 FF", Asked(channel, 0x07));
        }

        /// <summary>A G903: clusters only, 0x8100, every level, and no zone
        /// page is ever asked for. With no mode feature a 0x8071 device
        /// offers its lighting power and nothing above it, and its model ID
        /// is never asked for.</summary>
        [Fact]
        public void AClusterOnlyDevice_OffersHostModeOnlyWithAModeFeature()
        {
            var device = HidppLightDevice.G903();
            var channel = device.Channel();
            var unit = device.Describe(channel, out bool incomplete);
            Assert.False(incomplete);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);
            Assert.Equal((0, 0), (unit.PerKeyFeatureId, unit.PerKeyIndex));
            Assert.Equal((HidppUnitProtocol.OnboardProfiles, (byte)0x08, 2), (unit.ModeFeatureId, unit.ModeIndex, unit.MaxClaimLevel));
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(unit));
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x1004, 0x1000, 0x8081, 0x8101, 0x8100, 0x0003 }, channel.RootLookups);

            var plain = HidppLightDevice.G903();
            plain.Features.Remove(0x8100);
            channel = plain.Channel();
            unit = plain.Describe(channel, out incomplete);
            Assert.False(incomplete);
            Assert.Equal((0, 0, 1), (unit.ModeFeatureId, unit.ModeIndex, unit.MaxClaimLevel));
            Assert.DoesNotContain((ushort)0x0003, channel.RootLookups);
        }

        /// <summary>A mouse with both features (the G502 X PLUS: one
        /// cluster, zones 1 to 8) has its zones read and stays on its
        /// cluster, so its multi-cluster effects are never walked.</summary>
        [Fact]
        public void AMouseWithBothFeatures_StaysOnItsCluster()
        {
            var device = HidppLightDevice.G915X();
            device.Type = 3;
            device.Clusters = new[] { ((ushort)0x0001, new ushort[] { 0x0000, 0x0001, 0x000A, 0x0003 }) };
            device.ZonePages = new[] { HidppHapticsTests.Hex("FE 01", 14), new byte[14], new byte[14] };
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(((byte)0x01, (byte)0x08), (unit.PerKeyMin, unit.PerKeyMax));
            Assert.Equal(-1, unit.HostStreamingIndex);
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(unit));
            Assert.DoesNotContain("11 FF 08 0C FF", Asked(channel, 0x08));
        }

        /// <summary>A G810: the first pass finds 0x8080, and after the
        /// batteries the probe looks up the 0x8070 it is claimed and
        /// painted through and walks its zones. It offers no level, and
        /// neither mode feature is looked up.</summary>
        [Fact]
        public void AG810_IsPaintedThroughIts0x8070_WithItsPerKeyFeatureBeside()
        {
            var device = HidppLightDevice.G810();
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x8081, 0x8080, 0x1004, 0x1000, 0x8070 }, channel.RootLookups);
            Assert.Equal(HidppUnitProtocol.PerKeyLighting, unit.RgbFeatureId);
            Assert.Equal((HidppUnitProtocol.ColorLedEffects, (byte)0x0D), (unit.EffectFeatureId, unit.EffectIndex));
            Assert.Equal((HidppUnitProtocol.PerKeyLighting, (byte)0x0C), (unit.PerKeyFeatureId, unit.PerKeyIndex));
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);
            Assert.Equal(0, unit.MaxClaimLevel);
            Assert.False(unit.DirectRgb);
            Assert.True(unit.HasLightPlan);
            Assert.IsType<ZonePerKeyPlan>(HidppLightPlan.For(unit));

            // A board with a per-key feature and no effect feature has no
            // claim to take, and no plan.
            var bare = HidppLightDevice.G810();
            bare.Features.Remove(0x8070);
            unit = bare.Describe(bare.Channel(), out incomplete);
            Assert.False(incomplete);
            Assert.Equal(HidppUnitProtocol.PerKeyLighting, unit.PerKeyFeatureId);
            Assert.False(unit.HasLightPlan);
            Assert.Null(HidppLightPlan.For(unit));

            // 0x8081 with 0x8070 and no 0x8071: the zones, and no zone page.
            var odd = HidppLightDevice.G810();
            odd.Features.Remove(0x8080);
            odd.Features[0x8081] = 0x0C;
            channel = odd.Channel();
            unit = odd.Describe(channel, out incomplete);
            Assert.False(incomplete);
            Assert.Equal((HidppUnitProtocol.PerKeyLightingV2, HidppUnitProtocol.ColorLedEffects), (unit.PerKeyFeatureId, unit.EffectFeatureId));
            Assert.IsType<ZonePlan>(HidppLightPlan.For(unit));
            Assert.Empty(Asked(channel, 0x0C));
        }

        /// <summary>The zone pages: bit i of a page is zone page * 112 + i.
        /// Zones 0 and 255 are no zones though their bits may be set, a
        /// page the device refuses holds none, and a keyboard whose only
        /// zone is 254 has that zone alone.</summary>
        [Fact]
        public void TheZonePresence_IsEveryZoneBetweenOneAnd254_LowestFirst()
        {
            static byte[] Zones(params byte[][] pages)
            {
                var device = HidppLightDevice.G915X();
                device.ZonePages = pages;
                Assert.True(HidppUnitProbe.ZonePresence(device.Channel(), 0xFF, 0x09, 300, out byte[] zones, out bool missed));
                Assert.False(missed);
                return zones;
            }
            static IEnumerable<byte> Run(int first, int last) => Enumerable.Range(first, last - first + 1).Select(z => (byte)z);

            // The G915 X's own pages: zone 0's bit is set and is no zone.
            byte[] g915x = Run(1, 111).Concat(new byte[] { 153, 155, 156, 157, 158 }).Concat(Run(180, 188))
                .Append((byte)210).ToArray();
            Assert.Equal(g915x, Zones(HidppLightDevice.G915X().ZonePages));
            // A G515 LS TKL: zones 1 to 79, 97 to 111 and 254.
            Assert.Equal(Run(1, 79).Concat(Run(97, 111)).Append((byte)254), Zones(
                HidppHapticsTests.Hex("FE FF FF FF FF FF FF FF FF FF 00 00 FE FF", 14), new byte[14],
                HidppHapticsTests.Hex("00 00 00 40", 14)));
            // Zone 254 alone, with zone 255's bit set beside it.
            Assert.Equal(new byte[] { 0xFE }, Zones(new byte[14], new byte[14], HidppHapticsTests.Hex("00 00 00 C0", 14)));
            // A refused third page keeps the first two.
            Assert.Equal(g915x, Zones(HidppLightDevice.G915X().ZonePages[0], HidppLightDevice.G915X().ZonePages[1], null));
            // No zone at all.
            Assert.Empty(Zones(HidppHapticsTests.Hex("01", 14), new byte[14], HidppHapticsTests.Hex("00 00 00 80", 14)));

            var silent = HidppLightDevice.G915X();
            silent.Silent = (f, fn, p) => f == 0x09 && p[1] == 1;
            var channel = silent.Channel();
            Assert.True(HidppUnitProbe.ZonePresence(channel, 0xFF, 0x09, 300, out _, out bool missed));
            Assert.True(missed);
            // Nothing is asked past the page that went unanswered.
            Assert.Equal(2, channel.Asked.Count);
        }

        /// <summary>A request of the lighting walk that goes unanswered
        /// leaves the unit incomplete, to be asked again in full, and
        /// nothing is asked past it, wherever it falls.</summary>
        [Theory]
        [InlineData("lookup-8081")]
        [InlineData("device")]
        [InlineData("cluster-1")]
        [InlineData("effect")]
        [InlineData("page-2")]
        [InlineData("multi-cluster")]
        [InlineData("lookup-4540")]
        [InlineData("layout")]
        [InlineData("lookup-8101")]
        [InlineData("lookup-0003")]
        [InlineData("model")]
        public void AnUnansweredRequestOfTheLightingWalk_LeavesTheUnitIncomplete_AndEndsTheWalk(string at)
        {
            var device = HidppLightDevice.G915X();
            device.Features[0x4540] = 0x0B;
            device.Silent = (f, fn, p) => at switch
            {
                "lookup-8081" => f == 0 && p[0] == 0x80 && p[1] == 0x81,
                "device" => f == 0x08 && p[0] == 0xFF && p[1] == 0xFF,
                "cluster-1" => f == 0x08 && p[0] == 0x01 && p[1] == 0xFF,
                "effect" => f == 0x08 && p[0] == 0x00 && p[1] == 0x01,
                "page-2" => f == 0x09 && p[1] == 0x02,
                "multi-cluster" => f == 0x08 && p[0] == 0xFF && p[1] == 0x01,
                "lookup-4540" => f == 0 && p[0] == 0x45 && p[1] == 0x40,
                "layout" => f == 0x0B,
                "lookup-8101" => f == 0 && p[0] == 0x81 && p[1] == 0x01,
                "lookup-0003" => f == 0 && p[0] == 0x00 && p[1] == 0x03,
                _ => f == 0x02,
            };
            var channel = device.Channel();

            var unit = device.Describe(channel, out bool incomplete);

            Assert.True(incomplete);
            Assert.NotNull(unit);
            // The unit comes back as the first pass left it, for the caller
            // to drop: no plan is built on half a walk.
            Assert.False(unit.HasLightPlan);
            var last = channel.Traffic.Last().Frame;
            Assert.True(device.Silent(last[2], (byte)(last[3] >> 4), last.Skip(4).ToArray()),
                "the last request sent is the one that went unanswered");
        }

        /// <summary>The second pass of the first-pass lookup: 0x8081 is
        /// asked once in the first pass, where a 0x8071 device is never
        /// asked, so the walk's own lookup is its first.</summary>
        [Fact]
        public void TheFirstPassStopsAt0x8071_SoThePerKeyLookupIsTheWalksOwn()
        {
            var device = HidppLightDevice.G915X();
            var channel = device.Channel();
            device.Describe(channel, out _);
            Assert.Equal(1, channel.RootLookups.Count(id => id == 0x8081));
            Assert.Equal(new ushort[] { 0x19B0, 0x8071 }, channel.RootLookups.Take(2).ToArray());
        }

        /// <summary>An answer that names another question is not this
        /// question's answer: the same function byte can carry another
        /// program's. It is asked for once more. The second answer is taken
        /// when it names the question, or when it is the first one again
        /// byte for byte, a device that names no question. Two different
        /// answers that both name another question leave the unit
        /// incomplete.</summary>
        [Fact]
        public void AnAnswerToAnotherQuestionIsAskedForAgain_AndTakenOnlyWhenItSettles()
        {
            // Another program's answer for cluster 1 lands on the question
            // about cluster 0, once.
            var device = HidppLightDevice.G915();
            int strays = 0;
            var channel = device.Channel();
            channel.Answer = (d, f, fn, p) => f == 0x0A && fn == 0 && p[0] == 0x00 && p[1] == 0xFF && strays++ == 0
                ? device.Answer(d, f, fn, new byte[] { 0x01, 0xFF, 0x00 })
                : device.Answer(d, f, fn, p);
            var unit = device.Describe(channel, out bool incomplete);
            Assert.False(incomplete);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);
            Assert.Equal(2, Asked(channel, 0x0A).Count(a => a == "11 01 0A 0C 00 FF"));

            // A device that names no question: the same bytes twice.
            device = HidppLightDevice.G915();
            channel = device.Channel();
            channel.Answer = (d, f, fn, p) =>
            {
                var reply = device.Answer(d, f, fn, p);
                if (f != 0x0A || fn != 0 || p[1] == 0xFF || reply.Kind != HidppReplyKind.Answer) return reply;
                // The effect answer without its two leading bytes.
                return HidppHapticsTests.Ans(d, f, 0, 0xEE, 0xEE, reply.Param(2), reply.Param(3));
            };
            unit = device.Describe(channel, out incomplete);
            Assert.False(incomplete);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, unit.Zones);

            // Two different strays in a row: nothing can be concluded.
            device = HidppLightDevice.G915();
            int turn = 0;
            channel = device.Channel();
            channel.Answer = (d, f, fn, p) => f == 0x0A && fn == 0 && p[0] == 0x00 && p[1] == 0x01
                ? HidppHapticsTests.Ans(d, f, 0, 0x01, (byte)(0x02 + turn++), 0x00, 0x01)
                : device.Answer(d, f, fn, p);
            unit = device.Describe(channel, out incomplete);
            Assert.True(incomplete);
            Assert.False(unit.HasLightPlan);
        }
    }

    /// <summary>
    /// The HID++ worker lighting a unit by its plan (#494): the frames it
    /// sends at each claim level, in order and byte for byte, against a
    /// scripted device. The worker runs on a clock each test moves by hand,
    /// so the spacing between frames, the floor between paints and the
    /// keep-alive interval are stepped through and never waited for.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidppLightWorkerTests : IDisposable
    {
        public HidppLightWorkerTests() => Reset();
        public void Dispose() => Reset();

        private static void Reset()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            LearnedKeyMaps.ResetForTest();
        }

        private static readonly Guid Container = new("0a0b0c0d-1111-2222-3333-444455556666");

        internal static readonly VendorHidCollection Receiver = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#light",
            VendorId = 0x046D, ProductId = 0xC539, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
        };

        /// <summary>A keyboard of the G915 family on its cable: usage page
        /// 0xFF00 usage 2 (OpenRGB LogitechControllerDetect.cpp:1777).</summary>
        private static readonly VendorHidCollection Cable = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c356&mi_02&col02#light",
            VendorId = 0x046D, ProductId = 0xC356, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
        };

        /// <summary>A G810 on its cable: usage page 0xFF43, usage 0x0602 for
        /// its 20-byte reports (OpenRGB LogitechControllerDetect.cpp:162-166
        /// and 1779).</summary>
        private static readonly VendorHidCollection Board = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c331&mi_01&col03#light",
            VendorId = 0x046D, ProductId = 0xC331, UsagePage = 0xFF43, Usage = 0x0602, InputReportLength = 20,
        };

        private static readonly VendorHidCollection Bluetooth = new()
        {
            Path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b38a&col03#light",
            VendorId = 0x046D, ProductId = 0xB38A, UsagePage = 0xFF43, Usage = 0x0202, InputReportLength = 20,
        };

        /// <summary>One scripted device, the worker that found it, and the
        /// worker's clock.</summary>
        private sealed class Bench : IDisposable
        {
            private long _now = 100_000;
            public readonly HidppLightDevice Device;
            public readonly HidppHapticsTests.FakeChannel Channel;
            public readonly HidppBackend Backend;
            public readonly VendorHidCollection Collection;
            public readonly HidppUnit Unit;
            public readonly OutputPath Path;
            public readonly Guid Row = Guid.NewGuid();

            /// <summary>What the crash path's one-shot writer was handed, and
            /// whether that writer reports its writes as failed.</summary>
            public readonly ConcurrentQueue<(string Path, byte[] Frame)> Once = new();
            public readonly ConcurrentQueue<int> OnceTimeouts = new();
            public volatile bool OnceFails;

            /// <summary>Whether the walk still finds the unit's collection.</summary>
            public volatile bool Present = true;

            public Bench(HidppLightDevice device, VendorHidCollection collection = null, int reassertMs = int.MaxValue,
                int idleTickMs = 1, bool veryLong = false)
            {
                Device = device;
                Collection = collection ?? (device.Index == HidppHapticProtocol.DirectDeviceIndex ? Cable : Receiver);
                Channel = device.Channel(Collection.Path, HidppHapticProtocol.IsBluetoothPath(Collection.Path));
                Channel.HasVeryLong = veryLong;
                Backend = new HidppBackend(() => Present ? new[] { Collection } : Array.Empty<VendorHidCollection>(),
                    (c, s) => Channel, _ => Container, 1, idleTickMs, reassertMs,
                    (path, frame, timeout) =>
                    {
                        Once.Enqueue((path, frame));
                        OnceTimeouts.Enqueue(timeout);
                        return !OnceFails;
                    },
                    ticks: () => Interlocked.Read(ref _now));
                Backend.Start();
                Assert.True(SpinWait.SpinUntil(() => Backend.Snapshot.Units.Count == 1, 30000), "the scan found the unit");
                Unit = Backend.Snapshot.Units[0];
                Path = new OutputPath(OutputFamily.HidppUnit, Unit.Key);
                Passes();
                Clear();
                _foundAt = _past.Count;
            }

            /// <summary>The traffic each <see cref="Clear"/> set aside, and
            /// how much of it is the scan that found the unit.</summary>
            private readonly List<(string Kind, byte[] Frame)> _past = new();
            private readonly int _foundAt;

            public long Now => Interlocked.Read(ref _now);

            public void Link()
                => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Row, Lighting = new[] { Path } } }));

            public void Light(int rgb, int level = 0, KeyFrame keys = null)
                => PeripheralOutputs.SetLighting(Row, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, level, keys);

            public void Release() => PeripheralOutputs.ReleaseLighting(Row, 0);

            /// <summary>One whole pass of the worker's, begun after this
            /// call: once two more have started, one began after the call
            /// and has ended.</summary>
            public void Passes()
            {
                int seen = Backend.PassCount;
                Assert.True(SpinWait.SpinUntil(() => Backend.PassCount >= seen + 2, 30000), "the worker made a pass");
            }

            /// <summary>Moves the clock and lets a pass see it.</summary>
            public void Tick(int ms)
            {
                Interlocked.Add(ref _now, ms);
                Passes();
            }

            /// <summary>Moves the clock with no pass awaited, for a worker
            /// that is held inside a request.</summary>
            public void Advance(int ms) => Interlocked.Add(ref _now, ms);

            /// <summary>The lighting traffic so far, once at least
            /// <paramref name="count"/> frames of it are out: the clock is
            /// stepped by the frame spacing until they are, and twice more,
            /// which would have carried any frame that had no business
            /// following.</summary>
            public string[] After(int count)
            {
                for (int i = 0; i < 400 && Lit().Length < count; i++) Tick(HidppBackend.FrameTickMs);
                Tick(HidppBackend.FrameTickMs);
                Tick(HidppBackend.FrameTickMs);
                return Lit();
            }

            /// <summary>Makes the scripted device keep the mode a 0x8100
            /// mode frame sets, written or asked, as a device does. Without
            /// it the device answers every mode read with the mode it was
            /// made with.</summary>
            public void FollowTheMode()
            {
                Assert.Equal(HidppUnitProtocol.OnboardProfiles, Unit.ModeFeatureId);
                var answer = Channel.Answer;
                Channel.Answer = (d, f, fn, p) =>
                {
                    if (d == Unit.DeviceIndex && f == Unit.ModeIndex && fn == HidppUnitProtocol.FunctionOnboardSetMode && p.Length > 0)
                        Device.Mode = p[0];
                    return answer(d, f, fn, p);
                };
                Channel.Writing = frame =>
                {
                    if (frame[1] == Unit.DeviceIndex && frame[2] == Unit.ModeIndex
                        && frame[3] >> 4 == HidppUnitProtocol.FunctionOnboardSetMode)
                        Device.Mode = frame[4];
                };
            }

            /// <summary>Waits until the worker has let go of the unit.</summary>
            public void Away()
                => Assert.True(SpinWait.SpinUntil(() => Backend.Snapshot.Units.Count == 0, 30000), "the unit was let go");

            /// <summary>Moves the clock scan by scan until the worker has
            /// found the unit again.</summary>
            public void Back()
            {
                for (int i = 0; i < 12 && Backend.Snapshot.Units.Count == 0; i++)
                {
                    Advance(HidppBackend.FirstScanMs);
                    Passes();
                    Passes();
                }
                Assert.Single(Backend.Snapshot.Units);
            }

            /// <summary>The lighting traffic since the last clear, once at
            /// least <paramref name="count"/> frames of it are out, without
            /// the questions the probe asks the effect feature as it finds
            /// the unit again (0x8071 getInfo, function 0).</summary>
            public string[] AfterFound(int count)
            {
                for (int i = 0; i < 400 && SinceFound().Length < count; i++) Tick(HidppBackend.FrameTickMs);
                Tick(HidppBackend.FrameTickMs);
                Tick(HidppBackend.FrameTickMs);
                return SinceFound();
            }

            /// <summary>The same traffic as it stands, with the clock left
            /// where it is.</summary>
            public string[] SinceFound()
            {
                string probe = $"ask 11 {Unit.DeviceIndex:X2} {Unit.EffectIndex:X2} 0";
                return Lit().Where(l => !l.StartsWith(probe, StringComparison.Ordinal)).ToArray();
            }

            /// <summary>Every frame so far on the unit's lighting and mode
            /// features, written or asked, in order.</summary>
            public string[] Lit()
                => HidppHapticsTests.Lines(Channel.Traffic.Where(t => t.Frame[0] is 0x11 or 0x12 && t.Frame[1] == Unit.DeviceIndex
                    && t.Frame[2] != 0
                    && (t.Frame[2] == Unit.EffectIndex || t.Frame[2] == Unit.PerKeyIndex || t.Frame[2] == Unit.ModeIndex)));

            public void Clear()
            {
                while (Channel.Traffic.TryDequeue(out var frame)) _past.Add(frame);
                while (Channel.Writes.TryDequeue(out _)) { }
            }

            /// <summary>Everything that went out since the unit was found,
            /// whatever was cleared since, for a test of what never went.</summary>
            public (string Kind, byte[] Frame)[] Ever() => _past.Skip(_foundAt).Concat(Channel.Traffic).ToArray();

            /// <summary>The first frame of the whole run, the scan included,
            /// that the lighting, the probe and rumble have no business
            /// sending (<see cref="HidppFrameRules"/>), or null. A frame to
            /// the device is held to its feature's rule, a frame to any
            /// other slot can only be the question that looks for a device
            /// there, and a short frame only a receiver's register read.</summary>
            public string Refused()
            {
                foreach (var (kind, frame) in _past.Concat(Channel.Traffic))
                {
                    string text = kind + " " + HidppHapticsTests.Text(frame);
                    if (frame[0] == HidppReceiverProtocol.ShortReportId)
                    {
                        if (frame[2] != HidppReceiverProtocol.GetLongRegister) return "a short frame that reads no register: " + text;
                        continue;
                    }
                    if (frame[0] is not (HidppHapticProtocol.LongReportId or HidppHapticProtocol.VeryLongReportId))
                        return "a report of another kind: " + text;
                    string refusal = frame[1] == Device.Index
                        ? HidppFrameRules.Refusal(Device.FeatureAt(frame[2]), frame)
                        : frame[2] == 0 && frame[3] >> 4 == 0 ? null : "a frame to a slot no device was found at";
                    if (refusal != null) return refusal + ": " + text;
                }
                return null;
            }

            /// <summary>What the crash path has on record: each unit's
            /// hand-back frames.</summary>
            public string[] Record()
                => Backend.ClaimedRecords.SelectMany(r => r.HandBack.Select(HidppHapticsTests.Text)).ToArray();

            /// <summary>Stops the worker, and then holds everything the run
            /// sent, its hand-back at the stop included, to the rule that no
            /// frame writes a device's memory.</summary>
            public void Dispose()
            {
                Backend.Dispose();
                string refused = Refused();
                Assert.True(refused == null, refused);
                // The crash path's frames are the hand-back's, held to the
                // same rule.
                foreach (var (_, frame) in Once)
                {
                    string refusal = HidppFrameRules.Refusal(Device.FeatureAt(frame[2]), frame);
                    Assert.True(refusal == null, refusal);
                }
            }
        }

        private static string Fixed(string head, int cluster, int rgb)
            => $"write {head} 1C {cluster:X2} 01 {(rgb >> 16) & 0xFF:X2} {(rgb >> 8) & 0xFF:X2} {rgb & 0xFF:X2} 02 00 00 00 00 00 00 01";

        private static string[] Clusters(string head, int rgb) => new[] { Fixed(head, 0, rgb), Fixed(head, 1, rgb) };

        private static string[] Join(params object[] parts)
            => parts.SelectMany(p => p is string s ? new[] { s } : (string[])p).ToArray();

        // The G903: 0x8071 at 0x07, 0x8100 at 0x08, behind a receiver at slot 1.
        private const string Mouse = "11 01 07";
        private static readonly string[] MouseClaim = { "write 11 01 07 5C 01 01" };
        private static readonly string[] MouseVendorClaim =
        {
            "write 11 01 07 5C 01 03 07", "write 11 01 07 8C 01 01", "write 11 01 07 5C 01 03 05",
        };
        private static readonly string[] MouseVendorHold = { "write 11 01 07 5C 01 03 05", "write 11 01 07 8C 01 01" };

        // Its hand-back: the release, and after a claim that switched the
        // mode, the mode that claim read. Each goes out as a request.
        private const string MouseRelease = "ask 11 01 07 5C 01";
        private const string MouseRestore = "ask 11 01 08 1C 01";

        // The G915 X: 0x8071 at 0x08, 0x8081 at 0x09, 0x8101 at 0x0E, on its cable.
        private const string Keys = "11 FF 08";
        private static string[] Stream(int rgb, string kind = "ask")
        {
            string range = $"{kind} 11 FF 09 5C 01 D2 {(rgb >> 16) & 0xFF:X2} {(rgb >> 8) & 0xFF:X2} {rgb & 0xFF:X2}";
            // The frame as a line carries no trailing zeros.
            while (range.EndsWith(" 00", StringComparison.Ordinal)) range = range[..^3];
            return new[] { range, $"{kind} 11 FF 09 7C" };
        }
        /// <summary>A key frame that colors A alone (#557).</summary>
        private static KeyFrame KeyA(int rgb) => new(new Dictionary<ushort, int> { [0x04] = rgb }, null, null, null);

        private static string[] FirstPaint(int rgb, string kind = "ask") => Join(
            "write 11 FF 08 5C 01 01", Clusters(Keys, rgb),
            $"{kind} 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01",
            Stream(rgb, kind), Stream(rgb, kind));

        /// <summary>A 0x8071 unit at level 0: the claim is function 5 with
        /// the cluster flag alone, each cluster gets the Fixed effect, a new
        /// color paints the clusters again, and the hand-back is function 5
        /// with no flag, sent as a request. The 0x8070 release is never sent
        /// to it, which on this feature would set the power mode. The crash
        /// path's record holds the frame the hand-back sends.</summary>
        [Fact]
        public void AClusterUnitAtLevelZero_IsClaimedPaintedAndHandedBack_ThroughFunctionFive()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            Assert.IsType<ClusterPlan>(HidppLightPlan.For(bench.Unit));
            bench.Link();
            bench.Passes();
            Assert.Empty(bench.Lit());

            bench.Light(0xFF0000);
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0xFF0000)), bench.After(3));
            Assert.Equal(new[] { "11 01 07 5C 01" }, bench.Record());

            bench.Clear();
            bench.Light(0x0000FF);
            Assert.Equal(Clusters(Mouse, 0x0000FF), bench.After(2));

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease }, bench.Lit());
            Assert.Empty(bench.Record());
            // The release was the one request, nothing went to the mode
            // feature, and the 0x8070 release never went to the 0x8071 index.
            Assert.Single(bench.Ever(), t => t.Kind == "ask" && t.Frame[1] == 1 && t.Frame[2] == 0x07);
            Assert.DoesNotContain(bench.Ever(), t => t.Frame[1] == 1 && t.Frame[2] == 0x08);
            Assert.DoesNotContain(bench.Ever(), t => t.Frame.SequenceEqual(HidppUnitProtocol.SetSwControl(1, 0x07, claim: false)));
        }

        /// <summary>The spacing between two frames to a 0x8071 unit is
        /// counted on the worker's clock: with the clock still, the frame
        /// after the claim does not go out, however many passes run. It
        /// goes at 8 ms and not at 7. While a frame waits the worker ticks
        /// at the frame spacing, and once the queue is empty it is back on
        /// its idle tick. No pass sleeps the spacing out.</summary>
        [Fact]
        public void FramesToAClusterUnitKeepTheirSpacing_OnAFastTick_WithNoSleep()
        {
            const int idleTickMs = 200;
            using var bench = new Bench(HidppLightDevice.G903(), idleTickMs: idleTickMs);
            bench.Link();
            bench.Light(0xFF0000);
            bench.Passes();
            Assert.Equal(MouseClaim, bench.Lit());
            bench.Passes();
            bench.Passes();
            Assert.Equal(MouseClaim, bench.Lit());
            Assert.Equal(HidppBackend.FrameTickMs, WorkerCadence.WaitAfterAFullPass(() => bench.Backend.PassCount, () => bench.Backend.WaitMs));

            bench.Tick(HidppBackend.FrameTickMs - 1);
            Assert.Single(bench.Lit());
            bench.Tick(1);
            Assert.Equal(2, bench.Lit().Length);
            bench.Tick(HidppBackend.FrameTickMs - 1);
            Assert.Equal(2, bench.Lit().Length);
            bench.Tick(1);
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0xFF0000)), bench.Lit());
            Assert.Equal(idleTickMs, WorkerCadence.WaitAfterAFullPass(() => bench.Backend.PassCount, () => bench.Backend.WaitMs));

            // The spacing is no sleep: the worker file holds none.
            string worker = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "HidppBackend.cs");
            Assert.DoesNotContain("Thread.Sleep", worker);
            Assert.DoesNotContain("Task.Delay", worker);
        }

        /// <summary>Levels 0 and 1 never touch the mode feature: not at the
        /// claim, not at a keep-alive, not at the hand-back. Level 1 claims
        /// as Logitech's own program does and holds with [01 03 05] and
        /// power mode Full, never [01 03 07] again.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void LevelsZeroAndOne_SendNoFrameToTheModeFeature_AtAnyPoint(int level)
        {
            using var bench = new Bench(HidppLightDevice.G903(), reassertMs: 1000);
            string[] claim = level == 0 ? MouseClaim : MouseVendorClaim;
            string[] hold = level == 0 ? MouseClaim : MouseVendorHold;
            bench.Link();
            bench.Light(0xFF0000, level);
            Assert.Equal(Join(claim, Clusters(Mouse, 0xFF0000)), bench.After(claim.Length + 2));

            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join(hold, Clusters(Mouse, 0xFF0000)), bench.After(hold.Length + 2));

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease }, bench.Lit());
            Assert.DoesNotContain(bench.Ever(), t => t.Frame[1] == 1 && t.Frame[2] == 0x08);
            // The release is the one frame that was awaited.
            Assert.Single(bench.Ever(), t => t.Kind == "ask" && t.Frame[1] == 1 && t.Frame[2] == 0x07);
        }

        /// <summary>Level 2 reads the mode first, switches a device read in
        /// its on-board memory mode to host mode, and then claims as level 1
        /// does. The crash path has the hand-back, mode restore last, by
        /// the time the first frame of the claim is written. A keep-alive
        /// reads the mode, and with the device still in host mode it
        /// repeats neither the switch nor the first claim frame. The
        /// hand-back releases, waits for that answer, and puts back the
        /// mode that was read.</summary>
        [Fact]
        public void LevelTwo_ReadsTheMode_SwitchesADeviceReadOnBoard_AndWritesBackWhatItRead()
        {
            using var bench = new Bench(HidppLightDevice.G903(), reassertMs: 1000);
            Assert.Equal(2, bench.Unit.MaxClaimLevel);
            string[] recordAtFirstWrite = null;
            bench.FollowTheMode();
            var follow = bench.Channel.Writing;
            bench.Channel.Writing = frame =>
            {
                recordAtFirstWrite ??= bench.Record();
                follow(frame);
            };
            bench.Link();
            bench.Light(0xFF0000, level: 2);

            Assert.Equal(Join("ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));
            Assert.Equal(new[] { "11 01 07 5C 01", "11 01 08 1C 01" }, recordAtFirstWrite);
            Assert.Equal(new[] { "11 01 07 5C 01", "11 01 08 1C 01" }, bench.Record());

            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 01 08 2C", MouseVendorHold, Clusters(Mouse, 0xFF0000)), bench.After(5));

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, MouseRestore }, bench.Lit());
            Assert.Empty(bench.Record());
        }

        /// <summary>A device read in host mode already is left there: no
        /// mode frame at the claim and none at the hand-back, so a device
        /// its owner keeps in host mode stays in it.</summary>
        [Fact]
        public void LevelTwo_OnADeviceReadInHostMode_SendsNoModeFrameAtEitherEnd()
        {
            var device = HidppLightDevice.G903();
            device.Mode = 0x02;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            Assert.Equal(Join("ask 11 01 08 2C", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(6));
            Assert.Equal(new[] { "11 01 07 5C 01" }, bench.Record());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease }, bench.Lit());
        }

        /// <summary>The same on feature 0x8101, with a G915 X's own
        /// answers: read with 00, 03 while on-board, switched with 05 and
        /// put back with 03.</summary>
        [Fact]
        public void LevelTwo_OnFeature0x8101_ReadsWithZero_SwitchesWithFive_AndPutsBackThree()
        {
            using var bench = new Bench(HidppLightDevice.G915X());
            Assert.IsType<ClusterStreamPlan>(HidppLightPlan.For(bench.Unit));
            bench.Link();
            bench.Light(0xFD7600, level: 2);
            var lines = bench.After(12);
            Assert.Equal(Join("ask 11 FF 0E 6C", "write 11 FF 0E 6C 05",
                "write 11 FF 08 5C 01 03 07", "write 11 FF 08 8C 01 01", "write 11 FF 08 5C 01 03 05",
                FirstPaint(0xFD7600).Skip(1).ToArray()), lines);
            Assert.Equal(new[] { "11 FF 08 5C 01", "11 FF 0E 6C 03" }, bench.Record());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { "ask 11 FF 08 5C 01", "ask 11 FF 0E 6C 03" }, bench.Lit());
        }

        /// <summary>A claim that asks for level 2 on a unit that offers
        /// level 1, the G915 TKL's case, is held at level 1: the vendor's
        /// claim, and neither a read of the mode nor a frame to it.</summary>
        [Fact]
        public void AClaimAtLevelTwo_OnAUnitThatOffersLevelOne_IsHeldAtLevelOne_WithNoModeFrame()
        {
            var device = HidppLightDevice.G903();
            device.Transports = 0x0E;
            device.Model = new byte[] { 0xB3, 0x5F, 0x40, 0x8E, 0xC3, 0x43 };
            using var bench = new Bench(device);
            Assert.Equal(1, bench.Unit.MaxClaimLevel);
            Assert.Equal(HidppUnitProtocol.OnboardProfiles, bench.Unit.ModeFeatureId);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            Assert.Equal(Join(MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(5));

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease }, bench.Lit());
            Assert.DoesNotContain(bench.Ever(), t => t.Frame[1] == 1 && t.Frame[2] == 0x08);
        }

        /// <summary>A level that changes while the unit is lit: the unit is
        /// handed back at the level it was held at and claimed at the new
        /// one in the same pass. With the clock still, one pass sends the
        /// old level's hand-back and makes the new claim's read, and the
        /// crash path already holds the new claim's hand-back. The claim's
        /// frames then follow at their spacing.</summary>
        [Fact]
        public void ALevelChangeWhileLit_HandsBackAtTheOldLevel_AndClaimsAtTheNewOne_InTheSamePass()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            bench.Link();
            bench.Light(0xFF0000, level: 0);
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0xFF0000)), bench.After(3));

            // Up to level 2.
            bench.Clear();
            bench.Light(0xFF0000, level: 2);
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, "ask 11 01 08 2C" }, bench.Lit());
            Assert.Equal(new[] { "11 01 07 5C 01", "11 01 08 1C 01" }, bench.Record());
            Assert.Equal(Join(MouseRelease, "ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim,
                Clusters(Mouse, 0xFF0000)), bench.After(8));

            // Down to level 1: the mode goes back with the old claim, and
            // the new one reads nothing.
            bench.Clear();
            bench.Light(0xFF0000, level: 1);
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, MouseRestore }, bench.Lit());
            Assert.Equal(new[] { "11 01 07 5C 01" }, bench.Record());
            Assert.Equal(Join(MouseRelease, MouseRestore, MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));
        }

        /// <summary>A mode read that goes unanswered makes no claim: with
        /// no mode on record nothing could be put back. The read is made
        /// again on the next passes, five times, and then once each
        /// keep-alive interval. When the device answers, the claim goes
        /// ahead.</summary>
        [Fact]
        public void AModeReadThatGoesUnanswered_MakesNoClaim_AndIsAskedAgainWithBoundedPatience()
        {
            var device = HidppLightDevice.G903();
            bool silent = true;
            device.Silent = (f, fn, p) => silent && f == 0x08;
            using var bench = new Bench(device, reassertMs: 1000);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            for (int i = 0; i < 6; i++) bench.Passes();
            Assert.Equal(Enumerable.Repeat("ask 11 01 08 2C", HidppLightPlan.FailedPaintLimit).ToArray(), bench.Lit());
            Assert.Empty(bench.Record());

            bench.Clear();
            bench.Tick(999);
            Assert.Empty(bench.Lit());
            bench.Tick(1);
            bench.Passes();
            Assert.Equal(new[] { "ask 11 01 08 2C" }, bench.Lit());

            silent = false;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));
        }

        /// <summary>A device that refuses the mode read, where one that is
        /// asleep leaves it unanswered, is claimed all the same: its mode is
        /// unknown, so it is not switched, the claim is level 1's, and the
        /// hand-back is the release alone, with no mode owed. A claim at a
        /// lower level is never held up by a read that failed before it.</summary>
        [Fact]
        public void AModeReadTheDeviceRefuses_ClaimsWithoutTheSwitch_AndOwesNoModeAtTheHandBack()
        {
            var device = HidppLightDevice.G903();
            device.Refuse = (f, fn, p) => f == 0x08 && fn == 2 ? (byte)0x02 : null;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            Assert.Equal(Join("ask 11 01 08 2C", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(6));
            Assert.Equal(new[] { "11 01 07 5C 01" }, bench.Record());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease }, bench.Lit());

            // Five reads in a row go unanswered, and the claim that then
            // asks for level 0 is made at once, with no read.
            bench.Tick(HidppBackend.FrameTickMs);
            device.Refuse = (f, fn, p) => null;
            device.Silent = (f, fn, p) => f == 0x08;
            bench.Clear();
            bench.Light(0x00FF00, level: 2);
            for (int i = 0; i < 6; i++) bench.Passes();
            Assert.Equal(Enumerable.Repeat("ask 11 01 08 2C", HidppLightPlan.FailedPaintLimit).ToArray(), bench.Lit());
            bench.Clear();
            bench.Light(0x00FF00, level: 0);
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0x00FF00)), bench.After(3));
        }

        /// <summary>Two colors inside the floor between paints give one
        /// paint, the newer: the worker keeps no queue of colors, it paints
        /// the color current when the floor has passed.</summary>
        [Fact]
        public void TwoColorsInsideTheFloor_GiveOnePaint_TheNewer()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            bench.Link();
            bench.Light(0xFF0000);
            bench.Passes();
            long painted = bench.Now;
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0xFF0000)), bench.After(3));

            bench.Clear();
            bench.Light(0x00FF00);
            bench.Tick((int)(painted + ClusterPlan.ClusterPaintMs - 1 - bench.Now));
            Assert.Empty(bench.Lit());
            bench.Light(0x0000FF);
            bench.Tick(1);
            Assert.Equal(Clusters(Mouse, 0x0000FF), bench.After(2));
        }

        /// <summary>A scan waits for a claim in flight: with the next scan
        /// due and frames still queued, no scan starts, and the pass that
        /// sends the last frame runs it.</summary>
        [Fact]
        public void AScanWaitsForTheFramesInFlight()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            bench.Link();
            bench.Light(0xFF0000);
            bench.Passes();
            Assert.Single(bench.Lit());
            int scans = bench.Backend.ScanCount;

            // The scan comes due with two frames still to go.
            bench.Tick(HidppBackend.FirstScanMs);
            Assert.Equal(2, bench.Lit().Length);
            bench.Passes();
            Assert.Equal(scans, bench.Backend.ScanCount);
            bench.Tick(HidppBackend.FrameTickMs);
            Assert.Equal(3, bench.Lit().Length);
            Assert.True(SpinWait.SpinUntil(() => bench.Backend.ScanCount == scans + 1, 30000), "the scan ran once the queue was empty");
        }

        /// <summary>The crash path writes the frames on its record, in
        /// their order, through the one-shot writer and to the unit's own
        /// collection: at level 2 the release and then the mode, and never
        /// the 0x8070 release. The worker is held inside a request while
        /// it runs, so its own hand-back cannot be what is seen.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void TheCrashPath_WritesTheRecordedFrames_AndAtLevelTwoEndsWithTheModeRestore(int level)
        {
            using var gate = new ManualResetEventSlim(false);
            using var bench = new Bench(HidppLightDevice.G903());
            try
            {
                bench.Link();
                bench.Light(0xFF0000, level);
                bench.After(level == 0 ? 3 : 7);
                string[] expected = level == 0
                    ? new[] { "11 01 07 5C 01" }
                    : new[] { "11 01 07 5C 01", "11 01 08 1C 01" };
                Assert.Equal(expected, bench.Record());

                // The next scan's pairing read never comes back.
                int asked = bench.Channel.RegistersAsked.Count;
                bench.Channel.Register = (r, s) => { gate.Wait(30000); return HidppHapticsTests.NoAnswer(); };
                bench.Advance(HidppBackend.FirstScanMs);
                Assert.True(SpinWait.SpinUntil(() => bench.Channel.RegistersAsked.Count > asked, 30000), "the worker is inside its scan");

                bench.Backend.ReleaseClaimedNow();

                Assert.Equal(expected, bench.Once.Select(w => HidppHapticsTests.Text(w.Frame)).ToArray());
                Assert.All(bench.Once, w => Assert.Equal(Receiver.Path, w.Path));
                Assert.Empty(bench.Record());
                bench.Backend.ReleaseClaimedNow();
                Assert.Equal(expected.Length, bench.Once.Count);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>A unit whose one-shot write fails on the crash path is
        /// left there: the frames behind the failed one would wait out the
        /// same timeout on the same path, in a process that is ending.</summary>
        [Fact]
        public void TheCrashPath_LeavesAUnitAtItsFirstFailedWrite()
        {
            using var gate = new ManualResetEventSlim(false);
            using var bench = new Bench(HidppLightDevice.G903());
            try
            {
                bench.Link();
                bench.Light(0xFF0000, level: 2);
                bench.After(7);
                Assert.Equal(2, bench.Record().Length);

                int asked = bench.Channel.RegistersAsked.Count;
                bench.Channel.Register = (r, s) => { gate.Wait(30000); return HidppHapticsTests.NoAnswer(); };
                bench.Advance(HidppBackend.FirstScanMs);
                Assert.True(SpinWait.SpinUntil(() => bench.Channel.RegistersAsked.Count > asked, 30000), "the worker is inside its scan");

                bench.OnceFails = true;
                bench.Backend.ReleaseClaimedNow();

                Assert.Equal(new[] { "11 01 07 5C 01" }, bench.Once.Select(w => HidppHapticsTests.Text(w.Frame)).ToArray());
                Assert.Empty(bench.Record());
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>The engine stops while a unit is held at level 2: the
        /// worker releases it, waits for that answer, and puts the mode
        /// back before the channel closes.</summary>
        [Fact]
        public void AStopAtLevelTwo_HandsBackWithTheModeRestoreLast_BeforeTheChannelCloses()
        {
            var device = HidppLightDevice.G903();
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            bench.Clear();
            // Whether the channel was closed already when the mode frame
            // reached the device.
            bool? closedAtRestore = null;
            bench.Channel.Answer = (d, f, fn, p) =>
            {
                if (f == 0x08 && fn == 1) closedAtRestore = bench.Channel.Disposed;
                return device.Answer(d, f, fn, p);
            };

            bench.Backend.Stop();

            Assert.Equal(new[] { MouseRelease, MouseRestore }, bench.Lit());
            Assert.True(closedAtRestore.HasValue, "the mode frame never reached the device");
            Assert.False(closedAtRestore.Value);
            Assert.True(bench.Channel.Disposed);
            Assert.Empty(bench.Record());
        }

        /// <summary>A hand-back frame that goes unanswered is written once
        /// more: it may have been lost close behind a color frame, and it
        /// changes nothing a second time. An answered one is sent once.</summary>
        [Fact]
        public void AHandBackFrameThatGoesUnanswered_IsWrittenOnceMore()
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent && f == 0x07 && fn == 5;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000);
            bench.After(3);

            bench.Clear();
            silent = true;
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, "write 11 01 07 5C 01" }, bench.Lit());
            Assert.Empty(bench.Record());

            // The claim made after it keeps its distance from that frame.
            bench.Clear();
            silent = false;
            bench.Light(0x0000FF);
            bench.Passes();
            Assert.Empty(bench.Lit());
            Assert.Equal(Join(MouseClaim, Clusters(Mouse, 0x0000FF)), bench.After(3));
        }

        /// <summary>A unit that stops answering is let go at the next
        /// liveness check with no frame: a device that slept has lost its
        /// claim by itself, and frames to it would be waits. Its hand-back
        /// is owed all the same and stays on the crash path's record, since
        /// a device that missed one answer still holds the claim, and at
        /// level 2 is still in host mode. When the unit is found again the
        /// hand-back goes out before anything else, with the mode put back
        /// where the device is still out of it, and the standing claim is
        /// then made afresh, read first.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void AUnitThatStopsAnswering_IsSentNothing_AndIsHandedBackWhenItIsFoundAgain(int level)
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            using var bench = new Bench(device);
            bench.FollowTheMode();
            bench.Link();
            bench.Light(0xFF0000, level);
            bench.After(level == 0 ? 3 : 7);
            string[] owed = bench.Record();
            Assert.Equal(level == 0 ? new[] { "11 01 07 5C 01" } : new[] { "11 01 07 5C 01", "11 01 08 1C 01" }, owed);

            bench.Clear();
            silent = true;
            bench.Advance(HidppBackend.CheckMs);
            bench.Away();
            bench.Passes();
            Assert.Empty(bench.Lit());
            Assert.Equal(owed, bench.Record());
            Assert.Empty(bench.Once);

            // It was awake all along and one answer short. Found again, it
            // is handed back first, and then claimed as a unit just found.
            // The claim's first frame keeps the plan's distance from the
            // hand-back's last: with the clock standing still it has not
            // gone out, where the mode read, a request, has.
            silent = false;
            bench.Back();
            Assert.Equal(level == 0
                ? new[] { MouseRelease }
                : new[] { "ask 11 01 08 2C", MouseRelease, MouseRestore, "ask 11 01 08 2C" }, bench.SinceFound());
            Assert.Equal(level == 0
                ? Join(MouseRelease, MouseClaim, Clusters(Mouse, 0xFF0000))
                : Join("ask 11 01 08 2C", MouseRelease, MouseRestore,
                    "ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)),
                bench.AfterFound(level == 0 ? 4 : 10));
            Assert.Equal(owed, bench.Record());
        }

        /// <summary>A device that slept is back in its on-board memory mode
        /// when it is found again. The release is paid, the mode is read
        /// and not written a second time, and with the claim ended
        /// meanwhile nothing else goes to the unit.</summary>
        [Fact]
        public void AnOwedHandBack_WritesNoModeToADeviceThatRestarted_AndNothingMoreWithItsClaimEnded()
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            using var bench = new Bench(device);
            bench.FollowTheMode();
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            Assert.Equal(0x02, device.Mode);

            bench.Clear();
            silent = true;
            bench.Advance(HidppBackend.CheckMs);
            bench.Away();
            Assert.Equal(new[] { "11 01 07 5C 01", "11 01 08 1C 01" }, bench.Record());

            // It slept, and woke in its on-board memory mode. The claim
            // ended while it was away.
            device.Mode = 0x01;
            bench.Release();
            silent = false;
            int waits = bench.Channel.Waits.Count;
            bench.Back();
            Assert.Equal(new[] { "ask 11 01 08 2C", MouseRelease }, bench.AfterFound(2));
            Assert.Empty(bench.Record());
            // The one mode read made since the unit came back was given
            // the link's own time, and nothing was left for the stop.
            Assert.Equal(new[] { HidppHapticProtocol.TimeoutMs(false) },
                bench.Channel.Waits.Skip(waits).Where(w => w.Feature == 0x08 && w.Function == 2).Select(w => w.TimeoutMs));
            bench.Backend.Stop();
            Assert.Empty(bench.Once);
        }

        /// <summary>A write that fails drops the whole path, and each
        /// claimed unit on it is owed its hand-back. A mouse held at level
        /// 2 is still in host mode when its path answers again, and that
        /// is the claim's doing: it is handed back with the mode the claim
        /// first read, and the claim made afresh reads the device's own
        /// mode. Read as its owner's choice, host mode would never be put
        /// back.</summary>
        [Fact]
        public void AUnitDroppedByAFailedWrite_IsHandedBackWithTheModeItsClaimRead_AndNotTakenForOneKeptInHostMode()
        {
            var device = HidppLightDevice.G903();
            using var bench = new Bench(device);
            bench.FollowTheMode();
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            string[] owed = { "11 01 07 5C 01", "11 01 08 1C 01" };
            Assert.Equal(owed, bench.Record());

            // A new color, and its write fails.
            bench.Clear();
            bench.Channel.FailWrites = true;
            bench.Light(0x00FF00, level: 2);
            bench.Away();
            Assert.Equal(owed, bench.Record());
            Assert.Equal(0x02, device.Mode);

            bench.Channel.FailWrites = false;
            bench.Back();
            Assert.Equal(Join("ask 11 01 08 2C", MouseRelease, MouseRestore,
                "ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0x00FF00)), bench.AfterFound(10));
            Assert.Equal(owed, bench.Record());

            // The end of this claim puts the on-board mode back.
            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, MouseRestore }, bench.Lit());
            Assert.Equal(0x01, device.Mode);
            Assert.Empty(bench.Record());
        }

        /// <summary>A write that fails while a scan asks the path drops the
        /// path as one that fails under a color does. Each claimed unit on it
        /// is owed its hand-back and stays on the crash path's record, the
        /// path is asked again on the fast interval, and a mouse held at
        /// level 2 is handed back with the mode its claim read before it is
        /// claimed afresh.</summary>
        [Fact]
        public void AUnitDroppedByAWriteThatFailsInAScan_IsOwedItsHandBack_AndIsFoundAgainOnTheFastInterval()
        {
            var device = HidppLightDevice.G903();
            using var bench = new Bench(device);
            bench.FollowTheMode();
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            string[] owed = { "11 01 07 5C 01", "11 01 08 1C 01" };
            Assert.Equal(owed, bench.Record());

            // The color stands, so nothing is written until the scan asks a
            // slot whose backoff is over, and that write fails.
            bench.Clear();
            int scans = bench.Backend.ScanCount;
            bench.Channel.FailWrites = true;
            bench.Advance(HidppBackend.FirstScanMs);
            bench.Away();
            Assert.True(bench.Backend.ScanCount > scans, "the scan dropped the path");
            Assert.Equal(owed, bench.Record());
            Assert.Equal(0x02, device.Mode);

            // One fast interval later the path is asked again and the unit
            // is found: handed back first, with the mode put back, and then
            // claimed as a unit just found, its own mode read.
            bench.Channel.FailWrites = false;
            bench.Advance(HidppBackend.FirstScanMs);
            bench.Passes();
            bench.Passes();
            Assert.Single(bench.Backend.Snapshot.Units);
            Assert.Equal(Join("ask 11 01 08 2C", MouseRelease, MouseRestore,
                "ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.AfterFound(10));
            Assert.Equal(owed, bench.Record());

            // The end of this claim puts the on-board mode back.
            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { MouseRelease, MouseRestore }, bench.Lit());
            Assert.Equal(0x01, device.Mode);
            Assert.Empty(bench.Record());
        }

        /// <summary>A hand-back whose write fails is still owed. The path
        /// is dropped with the claim on its books, and the hand-back is
        /// paid when the path answers again.</summary>
        [Fact]
        public void AHandBackWhoseWriteFails_IsStillOwed_AndIsPaidWhenThePathAnswersAgain()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            bench.Link();
            bench.Light(0xFF0000);
            bench.After(3);
            string[] owed = { "11 01 07 5C 01" };
            Assert.Equal(owed, bench.Record());

            bench.Clear();
            bench.Channel.FailWrites = true;
            bench.Release();
            bench.Away();
            Assert.Equal(owed, bench.Record());

            bench.Channel.FailWrites = false;
            bench.Back();
            Assert.Equal(new[] { MouseRelease }, bench.AfterFound(1));
            Assert.Empty(bench.Record());
        }

        /// <summary>A receiver's slot can be given to another device while
        /// the first is away. The frames on the books name the features of
        /// the device that left, at its indexes, so a unit found at the
        /// slot under another name, or with a feature elsewhere, is sent
        /// none of them, and the debt ends.</summary>
        [Theory]
        [InlineData("name")]
        [InlineData("index")]
        public void WhatIsOwedAtASlot_IsNotPaidToAnotherDeviceFoundThere(string differs)
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            var bench = new Bench(device);
            try
            {
                bench.Link();
                bench.Light(0xFF0000, level: 2);
                bench.After(7);

                bench.Clear();
                silent = true;
                bench.Advance(HidppBackend.CheckMs);
                bench.Away();
                Assert.NotEmpty(bench.Record());
                bench.Release();

                // Another mouse is paired at the slot.
                if (differs == "name") device.Name = "G703";
                else device.Features[0x8100] = 0x09;
                silent = false;
                bench.Back();
                bench.Passes();
                Assert.Empty(bench.Record());
                // Neither the release nor a mode frame went to it.
                Assert.DoesNotContain(bench.Channel.Traffic, t => t.Frame[2] == 0x07 && t.Frame[3] >> 4 == 5);
                Assert.DoesNotContain(bench.Channel.Traffic, t => t.Frame[2] is 0x08 or 0x09 && t.Frame[3] >> 4 == 1 && t.Kind != "reg");
                bench.Backend.Stop();
                Assert.Empty(bench.Once);
            }
            finally
            {
                // The bench's own rule reads every frame by the first
                // device's features, which the second one moved.
                bench.Backend.Dispose();
            }
        }

        /// <summary>At a stop, what is still owed goes out through a write
        /// that opens its own handle, each frame in its order and bounded,
        /// as on the crash path: the unit was not found again, so no
        /// channel of the worker's reaches it. A unit is left at its first
        /// failed write.</summary>
        [Theory]
        [InlineData(0, false)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        public void AtAStop_WhatIsOwedGoesOutThroughAHandleOfItsOwn(int level, bool fails)
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000, level);
            bench.After(level == 0 ? 3 : 7);
            string[] owed = bench.Record();

            bench.Clear();
            silent = true;
            bench.Advance(HidppBackend.CheckMs);
            bench.Away();
            bench.OnceFails = fails;
            bench.Backend.Stop();

            Assert.Equal(fails ? owed.Take(1) : owed, bench.Once.Select(o => HidppHapticsTests.Text(o.Frame)));
            Assert.All(bench.Once, o => Assert.Equal(bench.Collection.Path, o.Path));
            Assert.All(bench.OnceTimeouts, t => Assert.Equal(HidppBackend.OneShotMs, t));
            Assert.Equal(200, HidppBackend.OneShotMs);
            Assert.Empty(bench.Lit());
            Assert.Empty(bench.Record());
        }

        /// <summary>A Logitech program that starts has every Logitech
        /// device, one PadForge let go of among them. What that unit was
        /// owed ends with no frame, then and when the unit is found again:
        /// a hand-back after the program's own claim would clear that
        /// claim.</summary>
        [Fact]
        public void WhenALogitechProgramStarts_WhatIsOwedEndsWithNoFrame()
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);

            bench.Clear();
            silent = true;
            bench.Advance(HidppBackend.CheckMs);
            bench.Away();
            Assert.NotEmpty(bench.Record());

            PeripheralOutputs.Presence = new PeripheralPresence(true, true, false, false, false);
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = bench.Row, DirectWhenClosed = true,
                    Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") },
                },
            }));
            bench.Passes();
            Assert.Empty(bench.Record());

            silent = false;
            bench.Back();
            Assert.Empty(bench.AfterFound(0));
            bench.Backend.Stop();
            Assert.Empty(bench.Once);
        }

        /// <summary>A collection that leaves the walk takes what was owed
        /// on it along: nothing reaches a device that is not there, and the
        /// crash path's record lets go of it.</summary>
        [Fact]
        public void WhatIsOwedOnACollectionThatLeft_EndsWithIt()
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent;
            using var bench = new Bench(device);
            bench.Link();
            bench.Light(0xFF0000);
            bench.After(3);

            bench.Clear();
            silent = true;
            bench.Advance(HidppBackend.CheckMs);
            bench.Away();
            Assert.NotEmpty(bench.Record());

            bench.Present = false;
            int scans = bench.Backend.ScanCount;
            for (int i = 0; i < 12 && bench.Backend.ScanCount == scans; i++)
            {
                bench.Advance(HidppBackend.FirstScanMs);
                bench.Passes();
            }
            Assert.True(bench.Backend.ScanCount > scans, "a scan ran");
            bench.Passes();
            Assert.Empty(bench.Record());
            bench.Backend.Stop();
            Assert.Empty(bench.Once);
        }

        /// <summary>A unit switched to host mode that slept and woke is
        /// back in its on-board memory mode, with no word. A plan that
        /// holds its claim by repeating it has no claim read to show that,
        /// so its keep-alive reads the mode: in host mode still, the hold
        /// and the colors follow as ever, and found back in the mode the
        /// claim switched it from, the unit is claimed again as it was
        /// first claimed, switch included. A read that goes unanswered
        /// changes nothing. Below level 2, and on a device that was never
        /// switched, no read is made.</summary>
        [Fact]
        public void TheKeepAliveOfAUnitSwitchedToHostMode_ReadsTheMode_AndClaimsAgainOnADeviceThatRestarted()
        {
            var device = HidppLightDevice.G903();
            bool silent = false;
            device.Silent = (f, fn, p) => silent && f == 0x08;
            using var bench = new Bench(device, reassertMs: 1000);
            bench.FollowTheMode();
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            string[] owed = { "11 01 07 5C 01", "11 01 08 1C 01" };
            Assert.Equal(owed, bench.Record());

            // In host mode still.
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 01 08 2C", MouseVendorHold, Clusters(Mouse, 0xFF0000)), bench.After(5));
            Assert.Contains(bench.Channel.Waits, w => w.Feature == 0x08 && w.Function == 2 && w.TimeoutMs == HidppLightPlan.AckTimeoutMs);

            // It slept and woke.
            device.Mode = 0x01;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));
            Assert.Equal(0x02, device.Mode);
            Assert.Equal(owed, bench.Record());

            // The read goes unanswered.
            silent = true;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 01 08 2C", MouseVendorHold, Clusters(Mouse, 0xFF0000)), bench.After(5));
            silent = false;

            // Held at level 1 from here: the hand-back of the old claim,
            // the new claim, and keep-alives that read nothing.
            bench.Clear();
            bench.Light(0xFF0000, level: 1);
            Assert.Equal(Join(MouseRelease, MouseRestore, MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));
            Assert.Equal(0x01, device.Mode);
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join(MouseVendorHold, Clusters(Mouse, 0xFF0000)), bench.After(4));
        }

        /// <summary>A device its owner keeps in host mode is not switched
        /// by a claim at level 2, so its keep-alive has no mode of its own
        /// to watch and reads nothing.</summary>
        [Fact]
        public void TheKeepAliveOfAUnitThatWasNeverSwitched_ReadsNothing()
        {
            var device = HidppLightDevice.G903();
            device.Mode = 0x02;
            using var bench = new Bench(device, reassertMs: 1000);
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(6);
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join(MouseVendorHold, Clusters(Mouse, 0xFF0000)), bench.After(4));
        }

        /// <summary>A Logitech program starts while a unit is claimed, at
        /// level 2 with its mode switched: the path is withdrawn and not one
        /// frame more is written, no release, no mode, and no request. The
        /// same holds while the claim's own frames are still queued. When
        /// the program stops the unit is claimed afresh, read first.</summary>
        [Fact]
        public void WhenALogitechProgramStarts_NothingMoreIsSent_WhateverThePlanAndTheLevel()
        {
            using var bench = new Bench(HidppLightDevice.G903(), reassertMs: 1000);
            var gHubRuns = new PeripheralPresence(true, true, false, false, false);
            var throughGHub = new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = bench.Row, DirectWhenClosed = true,
                    Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") },
                },
            });
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.After(7);
            Assert.NotEmpty(bench.Record());

            bench.Clear();
            PeripheralOutputs.Presence = gHubRuns;
            PeripheralOutputs.PublishLinks(throughGHub);
            bench.Passes();
            bench.Tick(1000);
            bench.Tick(1000);
            Assert.Empty(bench.Lit());
            Assert.Empty(bench.Record());

            // It stops: the standing claim is made again from its read.
            PeripheralOutputs.Presence = PeripheralPresence.None;
            bench.Link();
            Assert.Equal(Join("ask 11 01 08 2C", "write 11 01 08 1C 02", MouseVendorClaim, Clusters(Mouse, 0xFF0000)), bench.After(7));

            // It starts again while a new claim's frames are still queued.
            bench.Release();
            bench.Passes();
            bench.Tick(HidppBackend.FrameTickMs);
            bench.Clear();
            bench.Light(0x00FF00, level: 2);
            bench.Passes();
            Assert.Equal(new[] { "ask 11 01 08 2C", "write 11 01 08 1C 02" }, bench.Lit());
            PeripheralOutputs.Presence = gHubRuns;
            PeripheralOutputs.PublishLinks(throughGHub);
            bench.Passes();
            bench.Tick(HidppBackend.FrameTickMs);
            bench.Tick(1000);
            Assert.Equal(new[] { "ask 11 01 08 2C", "write 11 01 08 1C 02" }, bench.Lit());
            Assert.Empty(bench.Record());
        }

        /// <summary>The G810 class in the worker: the 0x8070 claim, the two
        /// zones, the two awaited fills and the awaited commit, all in one
        /// pass. Black adds the three fills. The hand-back leaves the
        /// indicators and the media keys white, commits, and releases, and
        /// the crash path holds those four frames.</summary>
        [Fact]
        public void ABoardWithPerKeyFills_IsPaintedInOnePass_AndHandedBackWithItsIndicatorsWhite()
        {
            using var bench = new Bench(HidppLightDevice.G810(), Board);
            Assert.IsType<ZonePerKeyPlan>(HidppLightPlan.For(bench.Unit));
            string[] green =
            {
                "write 11 FF 0D 3C 00 01 00 FF 00 02", "write 11 FF 0D 3C 01 01 00 FF 00 02",
                "ask 11 FF 0C 4C 00 40 00 FF", "ask 11 FF 0C 4C 00 02 01 FF", "ask 11 FF 0C 5C",
            };
            bench.Link();
            bench.Light(0x00FF00);
            bench.Passes();
            // The clock has not moved: nothing here waits on a spacing.
            Assert.Equal(Join("write 11 FF 0D 8C 01 01", green), bench.Lit());
            Assert.Equal(new[] { "11 FF 0C 4C 00 40 FF FF FF", "11 FF 0C 4C 00 02 FF FF FF", "11 FF 0C 5C", "11 FF 0D 8C" }, bench.Record());

            bench.Clear();
            bench.Light(0xFF0000);
            bench.Tick(HidppBackend.PaintMs);
            Assert.Equal(new[]
            {
                "write 11 FF 0D 3C 00 01 FF 00 00 02", "write 11 FF 0D 3C 01 01 FF 00 00 02",
                "ask 11 FF 0C 4C 00 40 FF", "ask 11 FF 0C 4C 00 02 FF", "ask 11 FF 0C 5C",
            }, bench.Lit());

            bench.Clear();
            bench.Light(0x000000);
            bench.Tick(HidppBackend.PaintMs);
            Assert.Equal(new[]
            {
                "write 11 FF 0D 3C 00 01", "write 11 FF 0D 3C 01 01",
                "ask 11 FF 0C 4C 00 40", "ask 11 FF 0C 4C 00 02",
                "ask 11 FF 0C 4C 00 01", "ask 11 FF 0C 4C 00 04", "ask 11 FF 0C 4C 00 10",
                "ask 11 FF 0C 5C",
            }, bench.Lit());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[]
            {
                "ask 11 FF 0C 4C 00 40 FF FF FF", "ask 11 FF 0C 4C 00 02 FF FF FF", "ask 11 FF 0C 5C", "write 11 FF 0D 8C",
            }, bench.Lit());
            Assert.Empty(bench.Record());
        }

        /// <summary>A board that refuses one fill, as one with no key of that
        /// type may: the paint goes on to the commit, the color counts as
        /// shown, and no one-color fallback paint follows.</summary>
        [Fact]
        public void ARefusedFill_LeavesThePaintGoing()
        {
            var device = HidppLightDevice.G810();
            device.Refuse = (f, fn, p) => f == 0x0C && fn == 4 && p.Length > 1 && p[1] == 0x04 ? (byte)0x02 : null;
            using var bench = new Bench(device, Board);
            bench.Link();
            bench.Light(0x000000);
            bench.Passes();
            Assert.Equal(new[]
            {
                "write 11 FF 0D 8C 01 01", "write 11 FF 0D 3C 00 01", "write 11 FF 0D 3C 01 01",
                "ask 11 FF 0C 4C 00 40", "ask 11 FF 0C 4C 00 02",
                "ask 11 FF 0C 4C 00 01", "ask 11 FF 0C 4C 00 04", "ask 11 FF 0C 4C 00 10",
                "ask 11 FF 0C 5C",
            }, bench.Lit());
            bench.Clear();
            bench.Tick(HidppBackend.PaintMs);
            Assert.Empty(bench.Lit());
        }

        /// <summary>A fill that goes unanswered ends the paint before the
        /// commit, so the color is not taken as shown and the next pass
        /// paints it again, up to the failed-paint limit.</summary>
        [Fact]
        public void AnUnansweredFill_EndsThePaintBeforeTheCommit()
        {
            var device = HidppLightDevice.G810();
            device.Silent = (f, fn, p) => f == 0x0C && fn == 4 && p.Length > 1 && p[1] == 0x02;
            using var bench = new Bench(device, Board);
            bench.Link();
            bench.Light(0x00FF00);
            for (int i = 0; i < 8; i++) bench.Passes();
            var lit = bench.Lit();
            Assert.Single(lit, l => l == "write 11 FF 0D 8C 01 01");
            Assert.DoesNotContain("ask 11 FF 0C 5C", lit);
            Assert.Equal(HidppLightPlan.FailedPaintLimit, lit.Count(l => l == "ask 11 FF 0C 4C 00 02 01 FF"));
            Assert.Equal(1 + 4 * HidppLightPlan.FailedPaintLimit, lit.Length);
        }

        /// <summary>A commit that is not answered: the color is not taken
        /// as shown, so the next pass paints again, and after five paints in
        /// a row the unit waits for its keep-alive, which tries once per
        /// interval. Once the board answers, a keep-alive's paint counts,
        /// and the worker paints only for a new color again.</summary>
        [Fact]
        public void AnUnansweredCommit_IsPaintedAgain_AndAfterFiveInARowWaitsForTheKeepAlive()
        {
            var device = HidppLightDevice.G810();
            bool silent = true;
            device.Silent = (f, fn, p) => silent && f == 0x0C && fn == 5;
            using var bench = new Bench(device, Board, reassertMs: 1000);
            int Commits() => bench.Lit().Count(l => l == "ask 11 FF 0C 5C");
            bench.Link();
            bench.Light(0x00FF00);
            for (int i = 0; i < 8; i++) bench.Passes();

            Assert.Equal(HidppLightPlan.FailedPaintLimit, Commits());
            // The claim once, then five whole paints.
            Assert.Equal(1 + 5 * HidppLightPlan.FailedPaintLimit, bench.Lit().Length);
            Assert.Equal("write 11 FF 0D 8C 01 01", bench.Lit()[0]);
            Assert.Single(bench.Lit(), l => l == "write 11 FF 0D 8C 01 01");

            // The keep-alive is the next try, and the one after it.
            bench.Clear();
            bench.Tick(999);
            Assert.Empty(bench.Lit());
            bench.Tick(1);
            for (int i = 0; i < 3; i++) bench.Passes();
            Assert.Equal(1, Commits());
            Assert.Equal(6, bench.Lit().Length);

            // The board answers: the keep-alive's paint is taken, and the
            // same color is not painted again.
            silent = false;
            bench.Clear();
            bench.Tick(1000);
            for (int i = 0; i < 3; i++) bench.Passes();
            Assert.Equal(1, Commits());
            Assert.Equal(6, bench.Lit().Length);
            bench.Clear();
            bench.Tick(HidppBackend.PaintMs);
            Assert.Empty(bench.Lit());
            bench.Light(0xFF0000);
            bench.Tick(HidppBackend.PaintMs);
            Assert.Equal(5, bench.Lit().Length);
        }

        /// <summary>A G915 X in the worker, at level 0: the claim, the two
        /// cluster colors, the Host streaming choice, then the range over
        /// every zone and the frame end, twice, each per-key frame awaited.
        /// A new color is the range and the frame end alone, with no frame
        /// on the 0x8071 index. The hand-back is the release alone.</summary>
        [Fact]
        public void AKeyboardWithBothFeatures_IsPaintedThroughItsClustersOnce_ThenThroughItsZones()
        {
            using var bench = new Bench(HidppLightDevice.G915X());
            Assert.Equal(1, bench.Unit.HostStreamingIndex);
            bench.Link();
            bench.Light(0xFD7600);
            Assert.Equal(FirstPaint(0xFD7600), bench.After(8));
            Assert.Equal(new[] { "11 FF 08 5C 01" }, bench.Record());

            bench.Clear();
            bench.Light(0x0000FF);
            Assert.Equal(Stream(0x0000FF), bench.After(2));

            bench.Clear();
            bench.Light(0x000000);
            Assert.Equal(Stream(0x000000), bench.After(2));

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { "ask 11 FF 08 5C 01" }, bench.Lit());
        }

        /// <summary>A G915 X with its layout code read: its light map is
        /// published for the Lighting tab (#557), and a key frame paints it
        /// zone by zone. The first paint is the one-color first paint in
        /// the color most zones take, then that color over every zone, A
        /// in its own color and the frame end, each frame awaited.</summary>
        [Fact]
        public void AKeyFrame_PaintsAG915XZoneByZone_AndItsMapIsPublished()
        {
            var device = HidppLightDevice.G915X();
            device.Features[0x4540] = 0x0B;
            using var bench = new Bench(device);
            var map = PeripheralLightMaps.For(bench.Path);
            Assert.NotNull(map);
            Assert.Equal("hidpp:B38A:01", map.Product);
            Assert.Equal(119, map.Lights.Count);

            bench.Link();
            bench.Light(0x112233, keys: KeyA(0xFF0000));
            Assert.Equal(Join("write 11 FF 08 5C 01 01", Clusters(Keys, 0x112233),
                "ask 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01", Stream(0x112233),
                "ask 11 FF 09 5C 01 D2 11 22 33", "ask 11 FF 09 1C 01 FF", "ask 11 FF 09 7C"), bench.After(9));
        }

        /// <summary>A changed key sends its zone alone. A frame that goes
        /// away paints the keyboard one color again, with the range and
        /// the frame end.</summary>
        [Fact]
        public void AChangedKey_SendsItsZoneAlone_AndNoFrameIsOneColorAgain()
        {
            using var bench = new Bench(HidppLightDevice.G915X());
            bench.Link();
            bench.Light(0x112233, keys: KeyA(0xFF0000));
            bench.After(9);

            bench.Clear();
            bench.Light(0x112233, keys: KeyA(0x00FF00));
            Assert.Equal(new[] { "ask 11 FF 09 1C 01 00 FF", "ask 11 FF 09 7C" }, bench.After(2));

            bench.Clear();
            bench.Light(0x112233);
            Assert.Equal(Stream(0x112233), bench.After(2));
        }

        /// <summary>While the claim stands, the keep-alive repaints the
        /// whole picture in one frame, so a zone the keyboard never took
        /// is put right.</summary>
        [Fact]
        public void TheKeepAliveOfAKeyFrame_RepaintsTheWholePicture()
        {
            var device = HidppLightDevice.G915X();
            using var bench = new Bench(device, reassertMs: 1000);
            bench.Link();
            bench.Light(0x112233, keys: KeyA(0xFF0000));
            bench.After(9);

            device.ClaimFlags = HidppUnitProtocol.SwControlClusters;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(new[]
            {
                "ask 11 FF 08 5C", "ask 11 FF 09 5C 01 D2 11 22 33", "ask 11 FF 09 1C 01 FF", "ask 11 FF 09 7C",
            }, bench.After(4));
        }

        /// <summary>A keyboard that refuses a zone frame goes back to its
        /// clusters, and its map becomes theirs, so the Lighting tab offers
        /// the colors it can show: the keys cluster and the logo.</summary>
        [Fact]
        public void ARefusedZoneFrame_SendsTheKeyboardToItsClusters_AndItsMapToTheirs()
        {
            var device = HidppLightDevice.G915X();
            device.Refuse = (f, fn, p) => f == 0x09 && fn == HidppUnitProtocol.FunctionPerKeyV2SetIndividual ? (byte)0x02 : null;
            using var bench = new Bench(device);
            Assert.NotNull(PeripheralLightMaps.For(bench.Path));
            bench.Link();
            bench.Light(0x112233, keys: KeyA(0xFF0000));
            var lines = bench.After(10);
            Assert.Contains("ask 11 FF 09 1C 01 FF", lines);
            Assert.Equal(Clusters(Keys, 0x112233), lines.TakeLast(2));
            var map = PeripheralLightMaps.For(bench.Path);
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo }, map.Lights.Select(l => l.Kind));
            Assert.Null(map.Product);
        }

        /// <summary>A mouse with two clusters takes a color for each (#557):
        /// the logo group's color on its logo cluster and the claim's on the
        /// other, each the Fixed effect, under the claim. Its map is the two
        /// clusters.</summary>
        [Fact]
        public void AMouseWithTwoClusters_PaintsEachItsOwnColor()
        {
            using var bench = new Bench(HidppLightDevice.G903());
            var map = PeripheralLightMaps.For(bench.Path);
            Assert.Equal(new[] { LightKind.Zone, LightKind.Logo }, map.Lights.Select(l => l.Kind));
            bench.Link();
            bench.Light(0x112233, keys: new KeyFrame(null, new Dictionary<string, int> { ["logo"] = 0x00FF00 }, null, null));
            Assert.Equal(Join(MouseClaim, Fixed(Mouse, 0, 0x112233), Fixed(Mouse, 1, 0x00FF00)), bench.After(3));

            bench.Clear();
            bench.Light(0x112233, keys: new KeyFrame(null, new Dictionary<string, int> { ["logo"] = 0x0000FF }, null, null));
            Assert.Equal(new[] { Fixed(Mouse, 1, 0x0000FF) }, bench.After(1));
        }

        /// <summary>A zone no reference names is a key with no key yet. A
        /// name learned for it with Map Key Lights reaches the published
        /// map at the next pass, and the key's color then reaches that
        /// zone.</summary>
        [Fact]
        public void ALearnedNameReachesTheMap_AndTheKeysColorReachesItsZone()
        {
            var device = HidppLightDevice.G915X();
            device.ZonePages[1][0] |= 0x01;
            using var bench = new Bench(device);
            var map = PeripheralLightMaps.For(bench.Path);
            Assert.Equal(1, map.UnnamedKeyCount);

            LearnedKeyMaps.Set(map.Product, 112, 0x68);
            bench.Passes();
            map = PeripheralLightMaps.For(bench.Path);
            Assert.Equal(0, map.UnnamedKeyCount);
            var learned = map.Lights.Single(l => l.Address == 112);
            Assert.Equal((LightKind.Key, (ushort)0x68, LightKeySource.Learned), (learned.Kind, learned.Usage, learned.KeySource));

            bench.Link();
            bench.Light(0x112233, keys: new KeyFrame(new Dictionary<ushort, int> { [0x68] = 0x0000FF }, null, null, null));
            Assert.Contains("ask 11 FF 09 1C 70 00 00 FF", bench.After(9));
        }

        /// <summary>Map Key Lights' probe lights one zone white and every
        /// other off, ahead of the claim, in the fewest frames: the whole
        /// picture, black over every zone and Esc white. When it ends the
        /// claim's picture comes back.</summary>
        [Fact]
        public void TheProbeLightsOneZone_AheadOfTheClaim_AndThenTheClaimReturns()
        {
            using var bench = new Bench(HidppLightDevice.G915X());
            bench.Link();
            bench.Light(0x112233);
            bench.After(8);

            bench.Clear();
            PeripheralOutputs.SetProbe(bench.Path, new KeyFrame(null, null, new Dictionary<int, int> { [38] = 0xFFFFFF }, 0));
            Assert.Equal(new[] { "ask 11 FF 09 5C 01 D2", "ask 11 FF 09 1C 26 FF FF FF", "ask 11 FF 09 7C" }, bench.After(3));

            bench.Clear();
            PeripheralOutputs.SetProbe(bench.Path, null);
            Assert.Equal(Stream(0x112233), bench.After(2));
        }

        /// <summary>A G810 whose very long collection is open, with a key
        /// frame (#557): the claim, the static effect on each zone in black,
        /// which passes the per-key buffer through, each key type filled
        /// with the color most of its keys take, A in its own color on a
        /// very long frame, and the awaited commit. Its map is published.
        /// A changed key sends that key and the commit.</summary>
        [Fact]
        public void AG810WithAKeyFrame_IsPaintedKeyByKey_AndAChangeSendsTheKeyAlone()
        {
            using var bench = new Bench(HidppLightDevice.G810Keys(), Board, veryLong: true);
            var map = PeripheralLightMaps.For(bench.Path);
            Assert.NotNull(map);
            Assert.Equal("hidpp:C331:03", map.Product);
            bench.Link();
            bench.Light(0x112233, keys: KeyA(0xFF0000));
            Assert.Equal(new[]
            {
                "write 11 FF 0D 8C 01 01", "write 11 FF 0D 3C 00 01", "write 11 FF 0D 3C 01 01",
                "ask 11 FF 0C 4C 00 01 11 22 33", "ask 11 FF 0C 4C 00 02 11 22 33",
                "ask 11 FF 0C 4C 00 10 11 22 33", "ask 11 FF 0C 4C 00 40 11 22 33",
                "ask 12 FF 0C 3C 00 01 00 01 04 FF", "ask 11 FF 0C 5C",
            }, bench.After(9));

            bench.Clear();
            bench.Light(0x112233, keys: KeyA(0x00FF00));
            Assert.Equal(new[] { "ask 12 FF 0C 3C 00 01 00 01 04 00 FF", "ask 11 FF 0C 5C" }, bench.After(2));
        }

        /// <summary>A G810 that refuses a key frame is painted one color from
        /// then on and its map is taken down. A refused commit is a failed
        /// paint and nothing more.</summary>
        [Fact]
        public void AG810ThatRefusesAKeyFrame_IsPaintedOneColor_AndARefusedCommitIsAFailedPaint()
        {
            var device = HidppLightDevice.G810Keys();
            device.Refuse = (f, fn, p) => f == 0x0C && fn == HidppUnitProtocol.FunctionPerKeySetKeys ? (byte)0x02 : null;
            using (var bench = new Bench(device, Board, veryLong: true))
            {
                bench.Link();
                bench.Light(0x112233, keys: KeyA(0xFF0000));
                var lines = bench.After(12);
                Assert.Contains("ask 12 FF 0C 3C 00 01 00 01 04 FF", lines);
                Assert.Equal("ask 11 FF 0C 5C", lines[^1]);
                Assert.Null(PeripheralLightMaps.For(bench.Path));
                bench.Release();
            }

            var commits = HidppLightDevice.G810Keys();
            commits.Refuse = (f, fn, p) => f == 0x0C && fn == HidppUnitProtocol.FunctionPerKeyCommit ? (byte)0x02 : null;
            using var other = new Bench(commits, Board, veryLong: true);
            other.Link();
            other.Light(0x112233, keys: KeyA(0xFF0000));
            other.After(9);
            Assert.NotNull(PeripheralLightMaps.For(other.Path));
        }

        /// <summary>A G810 whose very long collection is not open has its
        /// keys never asked for and no map, and is painted one color.</summary>
        [Fact]
        public void AG810WithoutItsVeryLongCollection_IsPaintedOneColor()
        {
            using var bench = new Bench(HidppLightDevice.G810Keys(), Board);
            Assert.Null(PeripheralLightMaps.For(bench.Path));
            Assert.Empty(bench.Unit.KeyTypes);
        }

        /// <summary>The keep-alive of a keyboard that streams reads its
        /// claim first. While the cluster flag stands it repaints through
        /// the per-key frames alone, since a cluster frame would take the
        /// display back. When the flag is gone, a keyboard that slept and
        /// woke, it claims and paints in full again. When the read goes
        /// unanswered it sends nothing more that pass.</summary>
        [Fact]
        public void AStreamingKeyboardsKeepAlive_ReadsItsClaim_AndClaimsAgainOnlyWhenItIsGone()
        {
            var device = HidppLightDevice.G915X();
            bool silent = false;
            device.Silent = (f, fn, p) => silent && f == 0x08 && fn == 5;
            using var bench = new Bench(device, reassertMs: 1000);
            bench.Link();
            bench.Light(0xFD7600);
            bench.After(8);

            // The claim stands: flags 01, as the claim set them.
            device.ClaimFlags = HidppUnitProtocol.SwControlClusters;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", Stream(0xFD7600)), bench.After(3));

            // The read goes unanswered: nothing more this time.
            silent = true;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(new[] { "ask 11 FF 08 5C" }, bench.After(1));
            silent = false;

            // The claim is gone: the whole first paint again.
            device.ClaimFlags = 0x00;
            device.ClaimEvents = 0x00;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", FirstPaint(0xFD7600)), bench.After(9));
            Assert.Equal(new[] { "11 FF 08 5C 01" }, bench.Record());
        }

        /// <summary>A claim at level 2 that the keep-alive finds gone is
        /// made again as it was first made: the switch to host mode, the
        /// claim, the first paint whole. The mode is not read again. The
        /// read made at the first claim still says what the keyboard is
        /// owed, so a keyboard that reads as in host mode by then, with its
        /// claim gone, still gets its on-board memory mode back at the
        /// hand-back, and the crash path's record never changes.</summary>
        [Fact]
        public void AClaimAtLevelTwoFoundGone_IsMadeAgainWithTheModeFirstRead_AndStillOwesIt()
        {
            var device = HidppLightDevice.G915X();
            using var bench = new Bench(device, reassertMs: 1000);
            string[] owed = { "11 FF 08 5C 01", "11 FF 0E 6C 03" };
            bench.Link();
            bench.Light(0xFD7600, level: 2);
            bench.After(12);
            Assert.Equal(owed, bench.Record());

            device.Mode = 0x00;
            device.ClaimFlags = 0x00;
            bench.Clear();
            bench.Tick(1000);
            var lines = bench.After(12);
            Assert.Equal(Join("ask 11 FF 08 5C", "write 11 FF 0E 6C 05",
                "write 11 FF 08 5C 01 03 07", "write 11 FF 08 8C 01 01", "write 11 FF 08 5C 01 03 05",
                FirstPaint(0xFD7600).Skip(1).ToArray()), lines);
            Assert.DoesNotContain("ask 11 FF 0E 6C", lines);
            Assert.Equal(owed, bench.Record());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { "ask 11 FF 08 5C 01", "ask 11 FF 0E 6C 03" }, bench.Lit());
        }

        /// <summary>Over Bluetooth the per-key frames are written and not
        /// awaited: an idle Bluetooth LE link loses answers, so a wait
        /// there would stall the paint and confirm nothing. The keep-alive
        /// reads its claim all the same, with that link's own patience,
        /// since nothing else shows a keyboard that slept and woke. While
        /// the claim stands the per-key frames follow the read, when it is
        /// gone the first paint is made whole again, and when the read goes
        /// unanswered nothing more goes out that time.</summary>
        [Fact]
        public void OverBluetooth_ThePerKeyFramesAreWritten_AndTheKeepAliveStillReadsItsClaim()
        {
            var device = HidppLightDevice.G915X();
            bool silent = false;
            device.Silent = (f, fn, p) => silent && f == 0x08 && fn == 5;
            using var bench = new Bench(device, Bluetooth, reassertMs: 1000);
            Assert.True(bench.Channel.Bluetooth);
            bench.Link();
            bench.Light(0xFD7600);
            Assert.Equal(FirstPaint(0xFD7600, "write"), bench.After(8));
            Assert.DoesNotContain(bench.Lit(), l => l.StartsWith("ask", StringComparison.Ordinal));

            device.ClaimFlags = HidppUnitProtocol.SwControlClusters;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", Stream(0xFD7600, "write")), bench.After(3));
            Assert.Contains(bench.Channel.Waits,
                w => w.Feature == 0x08 && w.Function == 5 && w.TimeoutMs == HidppHapticProtocol.TimeoutMs(true));
            Assert.Equal(700, HidppHapticProtocol.TimeoutMs(true));

            silent = true;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(new[] { "ask 11 FF 08 5C" }, bench.After(1));
            silent = false;

            device.ClaimFlags = 0x00;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", FirstPaint(0xFD7600, "write")), bench.After(9));
        }

        /// <summary>A keyboard that refuses a per-key frame is lit through
        /// its clusters from then on: the cluster colors are sent again,
        /// since choosing Host streaming may have taken the display from
        /// them, a new color is cluster frames, and the keep-alive is the
        /// cluster plan's. A keyboard that refuses only the Host streaming
        /// choice keeps its per-key frames.</summary>
        [Fact]
        public void ARefusedPerKeyFrame_SendsTheUnitBackToItsClusters_AndARefusedChoiceDoesNot()
        {
            var device = HidppLightDevice.G915X();
            device.Refuse = (f, fn, p) => f == 0x09 && fn == 5 ? (byte)0x05 : null;
            using (var bench = new Bench(device, reassertMs: 1000))
            {
                bench.Link();
                bench.Light(0xFD7600);
                Assert.Equal(Join("write 11 FF 08 5C 01 01", Clusters(Keys, 0xFD7600),
                    "ask 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01", "ask 11 FF 09 5C 01 D2 FD 76",
                    Clusters(Keys, 0xFD7600)), bench.After(7));

                bench.Clear();
                bench.Light(0x0000FF);
                Assert.Equal(Clusters(Keys, 0x0000FF), bench.After(2));

                bench.Clear();
                bench.Tick(1000);
                Assert.Equal(Join("write 11 FF 08 5C 01 01", Clusters(Keys, 0x0000FF)), bench.After(3));
            }

            PeripheralOutputs.ResetForTest();
            var choosy = HidppLightDevice.G915X();
            choosy.Refuse = (f, fn, p) => f == 0x08 && fn == 1 ? (byte)0x02 : null;
            using (var bench = new Bench(choosy))
            {
                bench.Link();
                bench.Light(0xFD7600);
                Assert.Equal(FirstPaint(0xFD7600), bench.After(8));
                bench.Clear();
                bench.Light(0x0000FF);
                Assert.Equal(Stream(0x0000FF), bench.After(2));
            }
        }

        /// <summary>A frame end that is not answered fails the paint. A
        /// first paint runs again from the cluster colors, five times, and
        /// then waits for the keep-alive. The keep-alive of a keyboard
        /// whose first paint never landed is that paint whole, under the
        /// claim that still stands. Once it lands, a new color and the next
        /// keep-alive are the range and the frame end alone again.</summary>
        [Fact]
        public void AnUnansweredFrameEnd_RunsTheFirstPaintAgain_FiveTimesAndThenAtEachKeepAlive()
        {
            var device = HidppLightDevice.G915X();
            bool silent = true;
            device.Silent = (f, fn, p) => silent && f == 0x09 && fn == 7;
            device.ClaimFlags = HidppUnitProtocol.SwControlClusters;
            using var bench = new Bench(device, reassertMs: 1000);
            const string select = "ask 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01";
            string[] attempt = Join(Clusters(Keys, 0xFD7600), select, "ask 11 FF 09 5C 01 D2 FD 76", "ask 11 FF 09 7C");
            bench.Link();
            bench.Light(0xFD7600);

            var lines = bench.After(1 + 5 * attempt.Length);
            Assert.Equal(Join("write 11 FF 08 5C 01 01", attempt, attempt, attempt, attempt, attempt), lines);

            // The keep-alive: the claim reads as standing, and the first
            // paint is tried whole, once.
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", attempt), bench.After(1 + attempt.Length));

            // The keyboard answers: the next keep-alive's paint lands.
            silent = false;
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", Clusters(Keys, 0xFD7600), select, Stream(0xFD7600), Stream(0xFD7600)), bench.After(8));

            bench.Clear();
            bench.Light(0x0000FF);
            Assert.Equal(Stream(0x0000FF), bench.After(2));
            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(Join("ask 11 FF 08 5C", Stream(0x0000FF)), bench.After(3));
        }

        /// <summary>A zone-only unit on the worker's clock: the frames the
        /// first build sent, all written, none awaited, the claim and the
        /// color in one pass, with nothing asked of the mode features or
        /// of the model ID.</summary>
        [Fact]
        public void AZoneOnlyUnit_KeepsItsFramesItsTimingAndItsQuestions()
        {
            using var bench = new Bench(HidppLightDevice.ZoneOnly(), reassertMs: 1000);
            Assert.IsType<ZonePlan>(HidppLightPlan.For(bench.Unit));
            // Past the lookup of 0x0005 that finds a slot alive, the first
            // build's six and no more.
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x8081, 0x8080, 0x8070, 0x1004 },
                bench.Channel.RootLookups.Where(id => id != HidppUnitProtocol.DeviceTypeAndName));
            bench.Link();
            bench.Light(0xFF0000, level: 2);
            bench.Passes();
            Assert.Equal(new[] { "write 11 01 0E 8C 01 01", "write 11 01 0E 3C 00 01 FF 00 00 02" }, bench.Lit());
            Assert.Equal(new[] { "11 01 0E 8C" }, bench.Record());

            bench.Clear();
            bench.Light(0x0000FF, level: 2);
            bench.Tick(HidppBackend.PaintMs - 1);
            Assert.Empty(bench.Lit());
            bench.Tick(1);
            Assert.Equal(new[] { "write 11 01 0E 3C 00 01 00 00 FF 02" }, bench.Lit());

            bench.Clear();
            bench.Tick(1000);
            Assert.Equal(new[] { "write 11 01 0E 8C 01 01", "write 11 01 0E 3C 00 01 00 00 FF 02" }, bench.Lit());

            bench.Clear();
            bench.Release();
            bench.Passes();
            Assert.Equal(new[] { "write 11 01 0E 8C" }, bench.Lit());
            Assert.DoesNotContain(bench.Channel.Asked, a => a.Feature == 0x0E && a.Function is 3 or 8);
        }
    }

    /// <summary>
    /// The HID++ lane and the PC it is tested on (#494). Everything the
    /// lane does to a device goes through three shared helpers: the reader
    /// a channel opens, the write a channel makes, and the one-shot write
    /// of the crash path. The test host refuses all three for its whole
    /// life (<see cref="RealHidAccess"/>), so a build of this lane with a
    /// guard taken out fails its test and reaches no mouse, keyboard or
    /// receiver on the bench. The lane has no call of its own to a device,
    /// which the first test here keeps true.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidppOffLimitsTests : IDisposable
    {
        public HidppOffLimitsTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static string Source(params string[] parts)
            => AuditDelta20261002Tests.RepoText(new[] { "PadForge.App", "Common", "Input" }.Concat(parts).ToArray());

        [Fact]
        public void TheLaneReachesADeviceOnlyThroughTheSharedHelpersTheTestHostRefuses()
        {
            string haptics = Source("HidppHaptics.cs");
            string units = Source("Peripherals", "HidppUnits.cs");
            string backend = Source("Peripherals", "HidppBackend.cs");
            string plans = Source("Peripherals", "HidppLightPlan.cs");
            string all = haptics + units + backend + plans;

            // No call of its own to a device, a process or the registry.
            foreach (string call in new[]
            {
                "DllImport", "LibraryImport", "CreateFile", "WriteFile", "ReadFile", "DeviceIoControl", "HidD_",
                "SafeFileHandle", "FileStream", "HidReportChannel", "Process.Start", "Registry",
            })
                Assert.DoesNotContain(call, all);

            // Of the shared helpers it names these members and no other:
            // the two fenced writes, the one that only forgets a closed
            // path, the reader whose open is fenced, and the walk that
            // lists collections and writes to none.
            static string[] Members(string source, string type) => System.Text.RegularExpressions.Regex
                .Matches(source, type + @"\.(\w+)").Select(m => m.Groups[1].Value).Distinct().OrderBy(m => m, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "ResetDevice", "Write", "WriteOnce" }, Members(all, "RawHidOutput"));
            Assert.Equal(new[] { "Enumerate" }, Members(all, "VendorHidRuntime"));
            // One reader each for the long collection, a receiver's short one
            // and a keyboard's very long one (#557).
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(haptics, @"new VendorHidReader\(").Count);
            Assert.DoesNotContain("new VendorHidReader(", units + backend + plans);

            // A channel is refused where it opens, and writes where the
            // refusal is.
            Assert.Contains("if (!channel._reader.Open())", haptics);
            Assert.Contains("_write = RawHidOutput.Write;", haptics);
            // The worker opens a channel through that one door, and its
            // crash path writes through the fenced one-shot helper.
            Assert.Contains("_open = openWithVeryLong", backend);
            Assert.Contains("?? (open != null ? (c, s, v) => open(c, s) : (c, s, v) => HidppChannel.Open(c, s, v));", backend);
            Assert.Contains("_writeOnce = writeOnce ?? RawHidOutput.WriteOnce;", backend);
            Assert.Contains("try { written = _writeOnce(record.Path, frame, OneShotMs); }", backend);
            // The stop path's write of what a unit is owed is the one other.
            Assert.Contains("try { written = _writeOnce(pair.Key, step.Frame, OneShotMs); }", backend);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(backend, @"_writeOnce\(").Count);
        }

        /// <summary>In the test host the shipping channel opens nothing
        /// and both writes refuse, and a worker left to its own opener and
        /// its own one-shot writer finds no unit and writes no frame. The
        /// collection is one the test hands in, named as Windows names a
        /// receiver's, with an instance no device has.</summary>
        [Fact]
        public void InTheTestHost_TheShippingChannelOpensNothing_AndTheWorkersOwnOpenerFindsNothing()
        {
            Assert.True(RealHidAccess.Refused);
            var collection = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#padforge-test-no-such-collection#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x046D, ProductId = 0xC539, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
            };
            Assert.True(HidppBackend.IsHidppLong(collection));
            Assert.Null(HidppChannel.Open(collection));
            byte[] release = HidppHapticsTests.Hex("11 01 07 5C 01");
            Assert.False(RawHidOutput.Write(collection.Path, release));
            Assert.False(RawHidOutput.WriteOnce(collection.Path, release, 200));

            int opened = 0;
            using var backend = new HidppBackend(() => { Interlocked.Increment(ref opened); return new[] { collection }; },
                null, _ => Guid.NewGuid(), 1, 5);
            backend.Start();
            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Scanned, 30000), "the worker scanned");
            Assert.True(Volatile.Read(ref opened) >= 1);
            Assert.Empty(backend.Snapshot.Units);
            backend.ReleaseClaimedNow();
            Assert.Empty(backend.ClaimedRecords);
        }
    }
}
