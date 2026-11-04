using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Chroma Addressable RGB Controller against OpenRGB's
    /// DetectRazerARGBControllers and RGBController_RazerAddressable
    /// (#557).</summary>
    public class RazerArgbLightingTests
    {
        private static string Packet(byte report, byte channel, string colors)
            => $"00 {report:X2} {channel:X2} {channel:X2} 00 4F " + colors
               + string.Concat(Enumerable.Repeat(" 00", 321 - 6 - colors.Split(' ').Length));

        /// <summary>One channel's packet: report ID 00, 04 or 84 on the sixth
        /// channel, the channel twice, 00, 4F, then 80 colors
        /// (RazerController.cpp:741-775, h:254-263).</summary>
        [Fact]
        public void APacket_IsOpenRgbs()
        {
            var colors = Enumerable.Range(0, 480).Select(i => i * 0x000101 + 0x110000).ToArray();
            string Rgb(int from) => string.Join(" ", colors.Skip(from).Take(80)
                .SelectMany(c => new[] { (c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF }).Select(b => b.ToString("X2")));
            Assert.Equal(Packet(0x04, 0, Rgb(0)), Hex(RazerArgbLighting.Packet(0, colors, 0)));
            Assert.Equal(Packet(0x04, 4, Rgb(320)), Hex(RazerArgbLighting.Packet(4, colors, 320)));
            Assert.Equal(Packet(0x84, 5, Rgb(400)), Hex(RazerArgbLighting.Packet(5, colors, 400)));
            Assert.Equal(321, RazerArgbLighting.Packet(0, colors, 0).Length);
        }

        private static (RazerScriptChannel Control, RazerScriptChannel Argb) Channels(Func<byte[], byte[]> reply)
            => (new RazerScriptChannel { FeatureReply = reply }, new RazerScriptChannel { FeatureReportLength = 321 });

        /// <summary>The session reads LED 05's stored effect and each
        /// channel's stored brightness (0F/84 [01 LED], razerchromacommon.c:731-739,
        /// razeraccessory_driver.c:1612-1620) under 3F, then sends the six
        /// packets 1 ms apart to the ARGB collection, and with the first
        /// picture the brightness 255 to channels 1A to 1F, not stored
        /// (RazerController.cpp:207-258 and 1439-1457,
        /// RGBController_RazerAddressable.cpp:272-282 and 345-348). The
        /// hand-back is the stored effect and each brightness read.</summary>
        [Fact]
        public void ASession_ReadsTheHandBack_ThenPaintsSixChannels()
        {
            var (control, argb) = Channels(request => request[8] switch
            {
                0x82 => RazerReport90.Reply(request, (2, 0x04), (3, 0x00), (4, 0x02), (5, 0x01), (6, 0x11), (7, 0x22), (8, 0x33)),
                0x84 => RazerReport90.Reply(request, (2, (byte)(0x40 + request[10]))),
                _ => null,
            });
            var model = RazerArgbLighting.Instance.Model("Razer Chroma Addressable RGB Controller", 0x0F1F, 0xFF00);
            var session = model.BeginWith(control, new[] { argb }, control.Wait);
            Assert.NotNull(session);
            Assert.Equal(RazerReport90.Of(0x3F, 0x0F, 0x82, 0x0C, 0x01, 0x05), Hex(control.Writes[0]));
            Assert.Equal(Enumerable.Range(0x1A, 6).Select(led => RazerReport90.Of(0x3F, 0x0F, 0x84, 0x03, 0x01, (byte)led, 0x00)),
                control.Writes.Skip(1).Select(w => Hex(w)));
            Assert.Empty(session.CrashFrames);

            var colors = Enumerable.Range(0, 480).Select(i => 0x010203 * (i % 80)).ToArray();
            int waits = control.Log.Count(l => l == "W 1");
            Assert.Equal(HidLightingPaint.Sent, session.Paint(control, control.Wait, model.Map.Lights, colors));
            // One before each packet and one after the last (lines 242 and 247).
            Assert.Equal(7, control.Log.Count(l => l == "W 1") - waits);
            Assert.Equal(6, argb.Writes.Count);
            Assert.Equal(new byte[] { 0x04, 0x04, 0x04, 0x04, 0x04, 0x84 }, argb.Writes.Select(w => w[1]));
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5 }, argb.Writes.Select(w => w[2]));
            Assert.Equal(Enumerable.Range(0x1A, 6).Select(led => RazerReport90.Of(0x3F, 0x0F, 0x04, 0x03, 0x00, (byte)led, 0xFF)),
                control.Writes.Skip(7).Select(w => Hex(w)));
            var handBack = new[] { RazerReport90.Of(0x3F, 0x0F, 0x02, 0x09, 0x00, 0x05, 0x04, 0x00, 0x02, 0x01, 0x11, 0x22, 0x33) }
                .Concat(Enumerable.Range(0x1A, 6).Select(led => RazerReport90.Of(0x3F, 0x0F, 0x04, 0x03, 0x00, (byte)led, (byte)(0x40 + led))));
            Assert.Equal(handBack, session.CrashFrames.Select(f => Hex(f)));
            Assert.True(session.Feature);

            // The next picture sends the packets alone.
            int before = control.Writes.Count;
            session.Paint(control, control.Wait, model.Map.Lights, colors);
            Assert.Equal(12, argb.Writes.Count);
            Assert.Equal(before, control.Writes.Count);

            control.Writes.Clear();
            Assert.True(session.HandBack(control, control.Wait));
            Assert.Equal(handBack, control.Writes.Select(w => Hex(w)));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>A stored effect that cannot be read, or reads as the
        /// custom frame, gives way to the spectrum effect on LED 05
        /// (RazerController.cpp:972-981), and a channel whose brightness
        /// was not read is not written back.</summary>
        [Fact]
        public void AnUnreadEffect_HandsBackTheSpectrum()
        {
            // Channel 1C answers. 1D answers for another LED, 1E with status
            // 05, not supported, and the rest not at all.
            var (control, argb) = Channels(request =>
            {
                if (request[8] == 0x82) return RazerReport90.Reply(request, (2, 0x08));
                if (request[8] != 0x84) return null;
                if (request[10] == 0x1C) return RazerReport90.Reply(request, (2, 0x80));
                if (request[10] == 0x1D) return RazerReport90.Reply(request, (1, 0x1E), (2, 0x81));
                if (request[10] != 0x1E) return null;
                var refused = RazerReport90.Reply(request, (2, 0x82));
                refused[1] = 0x05;
                return refused;
            });
            var model = RazerArgbLighting.Instance.Model("x", 0x0F1F, 0xFF00);
            var session = model.BeginWith(control, new[] { argb }, control.Wait);
            session.Paint(control, control.Wait, model.Map.Lights, new int[480]);
            Assert.Equal(new[]
            {
                RazerReport90.Of(0x3F, 0x0F, 0x02, 0x06, 0x00, 0x05, 0x03, 0x00, 0x00, 0x00),
                RazerReport90.Of(0x3F, 0x0F, 0x04, 0x03, 0x00, 0x1C, 0x80),
            }, session.CrashFrames.Select(f => Hex(f)));
            // Five tries 10 ms apart for each read with no reply of its own
            // (razeraccessory_driver.c:49-86): 1A, 1B, 1E and 1F. The stored
            // effect, 1C and 1D answered at the first.
            Assert.Equal(5 * 4 + 3, control.Log.Count(l => l == "G 00"));
            Assert.Equal(4 * 4, control.Log.Count(l => l == "W 10"));
        }

        /// <summary>An ARGB collection shorter than the packet begins no
        /// session.</summary>
        [Fact]
        public void AShortArgbCollection_BeginsNothing()
        {
            var model = RazerArgbLighting.Instance.Model("x", 0x0F1F, 0xFF00);
            var control = new RazerScriptChannel();
            Assert.Null(model.BeginWith(control, new[] { new RazerScriptChannel { FeatureReportLength = 320 } }, control.Wait));
            Assert.Empty(control.Writes);
        }

        /// <summary>The sibling is interface 1 of the same controller on the
        /// page an interface 1 collection showed in a walk, or the unit's own
        /// page until one has (RazerControllerDetect.cpp:138-151).</summary>
        [Fact]
        public void TheSibling_IsInterfaceOne_OnThePageAWalkShowed()
        {
            var family = new RazerArgbLighting();
            var unit = CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F1F), feature: 91);
            var guessed = family.Match(unit with { UsagePage = 0xFF07 });
            Assert.Equal(new HidSibling(1, 0xFF07), Assert.Single(guessed.Siblings));
            var argb = unit with
            {
                Path = unit.Path.Replace("mi_00", "mi_01"), InterfaceNumber = 1, UsagePage = 0xFF42, FeatureReportLength = 321,
            };
            Assert.Null(family.Match(argb));
            var learned = family.Match(unit with { UsagePage = 0xFF07 });
            Assert.Equal(new HidSibling(1, 0xFF42), Assert.Single(learned.Siblings));
            Assert.True(learned.Siblings[0].Is(unit, argb));
            // Another product's interface 1 teaches nothing.
            Assert.Null(family.Match(argb with { ProductId = 0x0F20 }));
            Assert.Same(learned, family.Match(unit with { UsagePage = 0xFF07 }));
        }
    }

    /// <summary>The ARGB controller through the shared worker: listed only
    /// with its ARGB interface, lit through both, handed back on the
    /// control collection (#557).</summary>
    public class RazerArgbWorkerTests
    {
        [Fact]
        public void TheWorker_LightsTheControllerThroughBothCollections()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 321 };
            var unit = KeyboardFamilyRig.Collection(0x1532, 0x0F1F, 0xFF00, 0x0001, iface: 0, feature: 91, tail: "a");
            var argb = KeyboardFamilyRig.Collection(0x1532, 0x0F1F, 0xFF00, 0x0001, iface: 1, feature: 321, tail: "b");
            var families = new IHidLightingFamily[] { new RazerArgbLighting() };
            using var backend = rig.Started(families, unit);
            // The unit alone is not listed.
            Assert.Empty(backend.Snapshot.Units);
            backend.Rescan(new[] { argb, unit });
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(unit.Path, Assert.Single(backend.Snapshot.Units).Key);

            rig.Log.FeatureAnswer = (_, last) => last == null ? null : RazerReport90.Reply(last, (2, 0x55));
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x123456);
            KeyboardFamilyRig.Passes(backend, 3);
            var packets = rig.Log.Of('F').Where(f => f.Length == 321).ToList();
            Assert.Equal(6, packets.Count);
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, packets[0].Skip(6).Take(3));
            lock (rig.Channels) Assert.Contains(rig.Channels, c => c.Path == argb.Path);
            Assert.NotEmpty(backend.Recorded);

            rig.Log.Clear();
            rig.Release(backend);
            var back = rig.Log.Of('F');
            Assert.NotEmpty(back);
            Assert.All(back, f => Assert.Equal(91, f.Length));
        }
    }
}
