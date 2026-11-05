using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Logitech keyboard of the G810 class painted key by key (#557): the
    /// walk of its key list through the very long report, its light map
    /// with the region keys, language keys and phantom extras left out, the
    /// frames that paint it, and the channel that carries those frames.
    /// </summary>
    public class HidppKeyTypeTests
    {
        private static string[] Text(IEnumerable<HidppStep> steps)
            => steps.Select(s => (s.Kind == HidppStepKind.Write ? "" : s.Kind.ToString().ToLowerInvariant() + " ")
                                 + HidppHapticsTests.Text(s.Frame)).ToArray();

        private static HidppUnit Probe(HidppLightDevice device, bool veryLong, out HidppHapticsTests.FakeChannel channel)
        {
            channel = device.Channel();
            channel.HasVeryLong = veryLong;
            var unit = device.Describe(channel, out bool incomplete);
            Assert.False(incomplete);
            return unit;
        }

        /// <summary>The probe walks the key list as Logitech Gaming Software
        /// and OpenRGB do: function 0, then for each type function 1 and as
        /// many pages of function 2 as its count needs. The G810's keys type
        /// counts 105 and its eight pages list 111, and its logo type counts
        /// one and lists two. The region and the model follow.</summary>
        [Fact]
        public void AG810sKeyList_IsWalkedPageByPage_AsItsCountBoundsIt()
        {
            var unit = Probe(HidppLightDevice.G810Keys(), veryLong: true, out var channel);
            Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0010, 0x0040 }, unit.KeyTypes.Select(k => k.Type));
            Assert.Equal(new[] { 111, 5, 2, 5 }, unit.KeyTypes.Select(k => k.Keys.Length));
            Assert.Equal(3, unit.KeyboardLayout);
            Assert.Equal(new ushort[] { 0xC331 }, unit.ModelIds);

            var asked = channel.Asked.Where(a => a.Feature == 0x0C).Select(a => a.Function).ToArray();
            Assert.Equal(new byte[] { 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 1, 2, 1, 2, 1, 2 }, asked);
            // Each page names its type and its offset, with the volatile
            // persistence byte OpenRGB sends.
            Assert.Contains("ask 11 FF 0C 2C 00 01 00 62", channel.Traffic.Select(t => t.Kind + " " + HidppHapticsTests.Text(t.Frame)));
            Assert.IsType<ZonePerKeyPlan>(HidppLightPlan.For(unit));
            Assert.True(HidppLightPlan.For(unit).PaintsLights);
        }

        /// <summary>Without its very long collection a G810's key list cannot
        /// be read, so it is never asked for, and the board is painted one
        /// color as before.</summary>
        [Fact]
        public void WithoutTheVeryLongCollection_TheKeyListIsNeverAskedFor()
        {
            var unit = Probe(HidppLightDevice.G810Keys(), veryLong: false, out var channel);
            Assert.Empty(unit.KeyTypes);
            Assert.DoesNotContain(channel.Asked, a => a.Feature == 0x0C);
            Assert.False(HidppLightPlan.For(unit).PaintsLights);
            Assert.Null(HidppKeyMap.Of(unit));
        }

        /// <summary>A type whose count cannot be read is paged until a page
        /// lists nothing, and a type whose count is 0 has no keys.</summary>
        [Fact]
        public void ATypeWithNoCount_IsPagedUntilAPageIsEmpty_AndACountOfZeroHasNoKeys()
        {
            var device = HidppLightDevice.G810Keys();
            device.Refuse = (f, fn, p) => f == 0x0C && fn == 1 && p[1] == 0x02 ? (byte)0x05 : null;
            device.KeyTypes[3] = (device.KeyTypes[3].Type, 0);
            var unit = Probe(device, veryLong: true, out _);
            Assert.Equal(new ushort[] { 0x0001, 0x0002, 0x0010 }, unit.KeyTypes.Select(k => k.Type));
            var media = unit.KeyTypes[1];
            Assert.Equal(new byte[] { 0xCD, 0xB6, 0xB5, 0xB7, 0xE2 }, media.Keys);
        }

        /// <summary>A key type holds its bit and its keys. Nothing paints by
        /// the top values getKeyTypeInfo answers: colors go out as 8-bit
        /// bytes, as OpenRGB's SetPerKey8080 sends them.</summary>
        [Fact]
        public void AKeyType_HoldsItsBitAndItsKeys_Alone()
            => Assert.Equal(new[] { "Type", "Keys" },
                typeof(HidppKeyType).GetConstructors().Single().GetParameters().Select(p => p.Name));

        /// <summary>The G810's map: its keys by usage, with the ANSI backslash
        /// and the JIS keys of a UK board left out, its logo without the
        /// second logo its firmware lists, and its five indicators and five
        /// media keys by name, the extras OpenRGB's G810 strip places.</summary>
        [Fact]
        public void AG810sMap_HoldsTheKeysAndExtrasTheBoardHas()
        {
            var unit = Probe(HidppLightDevice.G810Keys(), veryLong: true, out _);
            var map = HidppKeyMap.Of(unit);
            Assert.Equal("hidpp:C331:03", map.Product);
            Assert.Equal(105, map.KeyCount);
            Assert.Empty(map.IndexesOf(0x31));
            Assert.Single(map.IndexesOf(0x32));
            Assert.Single(map.IndexesOf(0x64));
            Assert.All(new ushort[] { 0x87, 0x88, 0x89, 0x8A, 0x8B }, u => Assert.Empty(map.IndexesOf(u)));
            Assert.Equal(LightKeySource.Device, map.Lights[map.IndexesOf(0x04)[0]].KeySource);
            Assert.Equal(HidppKeyMap.AddressOf(0x0001, 0x04), map.Lights[map.IndexesOf(0x04)[0]].Address);

            var extras = map.Lights.Where(l => l.Kind != LightKind.Key).Select(l => (l.Address, l.Kind, l.Label)).ToArray();
            Assert.Equal(new (int, LightKind, LightLabel)[]
            {
                (0x02CD, LightKind.Other, LightLabel.PlayPause), (0x02B6, LightKind.Other, LightLabel.Previous),
                (0x02B5, LightKind.Other, LightLabel.Next), (0x02B7, LightKind.Other, LightLabel.Stop),
                (0x02E2, LightKind.Other, LightLabel.Mute), (0x1001, LightKind.Logo, LightLabel.None),
                (0x4001, LightKind.Status, LightLabel.Brightness), (0x4002, LightKind.Status, LightLabel.GameMode),
                (0x4003, LightKind.Status, LightLabel.CapsLock), (0x4004, LightKind.Status, LightLabel.ScrollLock),
                (0x4005, LightKind.Status, LightLabel.NumLock),
            }, extras);
        }

        /// <summary>A board no reference has studied keeps every light it
        /// lists beyond the main block, and the studied boards keep the
        /// extras their tables place: the G410 its first two indicators, the
        /// G512 the Caps Lock and Game Mode lights, the G910 its G-keys, logo
        /// and nameplate and none of its media keys.</summary>
        [Fact]
        public void EachStudiedBoardKeepsItsOwnExtras_AndAnUnstudiedBoardKeepsAll()
        {
            HidppUnit Board(ushort model) => Probe(HidppLightDevice.G810Keys(), veryLong: true, out _) with
            {
                ModelIds = new[] { model },
            };
            int Extras(ushort model) => HidppKeyMap.Of(Board(model)).Lights.Count(l => l.Kind != LightKind.Key);
            Assert.Equal(12, Extras(0xC3FF));
            Assert.Contains(HidppKeyMap.Of(Board(0xC3FF)).Lights, l => l.Address == 0x1002);
            Assert.Equal(new[] { 0x4001, 0x4002 },
                HidppKeyMap.Of(Board(0xC330)).Lights.Where(l => l.Kind != LightKind.Key).Select(l => l.Address));
            Assert.Equal(new[] { 0x4002, 0x4003 },
                HidppKeyMap.Of(Board(0xC342)).Lights.Where(l => l.Kind != LightKind.Key).Select(l => l.Address));

            var g910 = Board(0xC32B) with
            {
                KeyTypes = new[]
                {
                    new HidppKeyType(0x0002, new byte[] { 0xB5, 0xB6 }),
                    new HidppKeyType(0x0004, Enumerable.Range(1, 9).Select(k => (byte)k).ToArray()),
                    new HidppKeyType(0x0010, new byte[] { 0x01, 0x02 }),
                },
            };
            var lights = HidppKeyMap.Of(g910).Lights;
            Assert.Equal(Enumerable.Range(1, 9), lights.Where(l => l.Label == LightLabel.GKey).Select(l => l.LabelNumber));
            Assert.Equal(2, lights.Count(l => l.Kind == LightKind.Logo));
            Assert.DoesNotContain(lights, l => l.Address >> 8 == 0x02);
        }

        /// <summary>The JIS keys stay on a Japanese board and the Korean keys
        /// on a Korean one.</summary>
        [Fact]
        public void TheLanguageKeysStayOnTheirOwnLayouts()
        {
            Assert.NotNull(HidppKeyMap.KeyLightOf(0x0001, 0x87, 0x0A, null));
            Assert.Null(HidppKeyMap.KeyLightOf(0x0001, 0x87, 0x01, null));
            Assert.NotNull(HidppKeyMap.KeyLightOf(0x0001, 0x90, 0x09, null));
            Assert.Null(HidppKeyMap.KeyLightOf(0x0001, 0x90, 0x03, null));
            Assert.Null(HidppKeyMap.KeyLightOf(0x0001, 0x31, 0x0A, null));
        }

        /// <summary>The whole picture: the static effect on both zones in
        /// black, each type filled with the color most of its keys take, the
        /// keys of every other color on very long frames, 14 to a frame, and
        /// the commit. A media key's red follows the fill's rule. A change
        /// sends the changed keys and the commit.</summary>
        [Fact]
        public void TheWholePicture_PassesTheZonesThrough_FillsEachType_AndSetsTheRest()
        {
            var unit = Probe(HidppLightDevice.G810Keys(), veryLong: true, out _);
            var map = HidppKeyMap.Of(unit);
            var plan = HidppLightPlan.For(unit);
            var colors = new int[map.Lights.Count];
            Array.Fill(colors, 0x112233);
            int A = map.IndexesOf(0x04)[0];
            colors[A] = 0xFF0000;
            int mute = map.Lights.ToList().FindIndex(l => l.Address == 0x02E2);
            colors[mute] = 0x00FF00;
            // Sixteen keys in blue: two frames.
            for (ushort u = 0x3A; u < 0x3A + 16; u++) colors[map.IndexesOf(u)[0]] = 0x0000FF;

            var lines = Text(plan.PaintLights(map, colors, null, full: true));
            Assert.Equal(new[]
            {
                "11 FF 0D 3C 00 01", "11 FF 0D 3C 01 01",
                "fill 11 FF 0C 4C 00 01 11 22 33", "fill 11 FF 0C 4C 00 02 11 22 33", "fill 11 FF 0C 4C 00 10 11 22 33",
                "fill 11 FF 0C 4C 00 40 11 22 33",
            }, lines.Take(6));
            var keyFrames = lines.Where(l => l.StartsWith("stream 12 FF 0C 3C 00 01", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, keyFrames.Length);
            Assert.StartsWith("stream 12 FF 0C 3C 00 01 00 0E 04 FF 00 00 3A 00 00 FF", keyFrames[0]);
            Assert.StartsWith("stream 12 FF 0C 3C 00 01 00 03", keyFrames[1]);
            Assert.Contains("stream 12 FF 0C 3C 00 02 00 01 E2 01 FF", lines);
            Assert.Equal("commit 11 FF 0C 5C", lines[^1]);

            var changed = (int[])colors.Clone();
            changed[A] = 0x00FF00;
            Assert.Equal(new[] { "stream 12 FF 0C 3C 00 01 00 01 04 00 FF", "commit 11 FF 0C 5C" },
                Text(plan.PaintLights(map, changed, colors, full: false)));

            // The keep-alive claims again and sends the whole picture.
            Assert.Equal(new[] { "11 FF 0D 8C 01 01" }.Concat(lines), Text(plan.KeepAliveLights(0, map, colors, painted: true)));

            // Only a refused key frame falls back to one color.
            Assert.True(plan.FallsBackOn(HidppStepKind.Stream));
            Assert.False(plan.FallsBackOn(HidppStepKind.Commit));
            Assert.False(plan.WithoutPerKey.PaintsLights);
            Assert.Null(plan.WithoutPerKey.WithoutPerKey);
        }

        /// <summary>The very long collection is the keyboard's own, found by
        /// the path key on usage page 0xFF43 usage 0x0604, and a collection on
        /// another page has none.</summary>
        [Fact]
        public void TheVeryLongCollection_IsTheSiblingOnTheSameInterface()
        {
            static VendorHidCollection Collection(string path, ushort page, ushort usage, ushort length) => new()
            {
                Path = path, VendorId = 0x046D, ProductId = 0xC331, UsagePage = page, Usage = usage, InputReportLength = length,
            };
            var longOne = Collection(@"\\?\hid#vid_046d&pid_c331&mi_01&col03#7&1a&0&0002#{4d1e55b2}", 0xFF43, 0x0602, 20);
            var veryLong = Collection(@"\\?\hid#vid_046d&pid_c331&mi_01&col04#7&1a&0&0003#{4d1e55b2}", 0xFF43, 0x0604, 64);
            var otherBoard = Collection(@"\\?\hid#vid_046d&pid_c331&mi_01&col04#7&2b&0&0003#{4d1e55b2}", 0xFF43, 0x0604, 64);
            Assert.Same(veryLong, HidppReceiverProtocol.VeryLongSibling(longOne, new[] { otherBoard, veryLong }));
            Assert.Null(HidppReceiverProtocol.VeryLongSibling(longOne, new[] { otherBoard }));
            var mouse = Collection(@"\\?\hid#vid_046d&pid_c331&mi_01&col03#7&1a&0&0002#{4d1e55b2}", 0xFF00, 0x0002, 20);
            Assert.Null(HidppReceiverProtocol.VeryLongSibling(mouse, new[] { veryLong }));
        }

        /// <summary>The channel writes a very long frame to the very long
        /// collection and takes its answer from there, and a channel without
        /// one sends no very long frame.</summary>
        [Fact]
        public void TheChannel_SendsVeryLongFramesThere_AndTakesTheirAnswers()
        {
            const string LongPath = @"\\?\hid#vid_046d&pid_c331&mi_01&col03#a", VeryLongPath = @"\\?\hid#vid_046d&pid_c331&mi_01&col04#b";
            var written = new List<(string Path, byte[] Frame)>();
            HidppChannel channel = null;
            channel = new HidppChannel(LongPath, null, (path, frame) =>
            {
                written.Add((path, frame));
                // The key list's page comes back on the very long report.
                if (frame[2] == 0x0C && frame[3] >> 4 == 2)
                {
                    var answer = new byte[64];
                    new byte[] { 0x12, 0xFF, 0x0C, 0x2C, 0x00, 0x01, 0x00, 0x00, 0x04, 0x00, 0xDC, 0xFF }.CopyTo(answer, 0);
                    channel.Feed(answer);
                }
                return true;
            }, VeryLongPath);
            Assert.True(channel.HasVeryLong);

            Assert.True(channel.Write(HidppHapticProtocol.VeryLongFrame(0xFF, 0x0C, 3, new byte[] { 0x00, 0x01, 0x00, 0x01, 0x04, 0xFF })));
            Assert.Equal(VeryLongPath, written[^1].Path);
            Assert.Equal(64, written[^1].Frame.Length);
            Assert.True(channel.Write(HidppHapticProtocol.Frame(0xFF, 0x0C, 5, Array.Empty<byte>())));
            Assert.Equal(LongPath, written[^1].Path);

            var reply = channel.RequestFrame(HidppHapticProtocol.Frame(0xFF, 0x0C, 2, new byte[] { 0x00, 0x01, 0x00, 0x00 }), 500);
            Assert.Equal(HidppReplyKind.Answer, reply.Kind);
            Assert.Equal(0x04, reply.Param(4));
            Assert.Equal(LongPath, written[^1].Path);

            var plain = new HidppChannel(LongPath, null, (path, frame) => { written.Add((path, frame)); return true; });
            Assert.False(plain.HasVeryLong);
            int before = written.Count;
            Assert.False(plain.Write(HidppHapticProtocol.VeryLongFrame(0xFF, 0x0C, 3, new byte[] { 0x00, 0x01 })));
            Assert.Equal(before, written.Count);
        }

        /// <summary>The key colors frame, byte for byte as Logitech Gaming
        /// Software sends one, with this build's software ID.</summary>
        [Fact]
        public void TheKeyColorsFrame_IsTheOneLogitechGamingSoftwareSends()
        {
            var frame = HidppUnitProtocol.PerKeySetKeys(0xFF, 0x0C, 0x0040, new (byte, int)[]
            {
                (0x05, 0x00DCFF), (0x04, 0x00DCFF), (0x03, 0x00DCFF), (0x02, 0x00DCFF), (0x01, 0x00DCFF),
            });
            Assert.Equal(64, frame.Length);
            // set_keys_colors.pcapng: 12 FF 0C 3A 00 40 00 05 05 00 DC FF 04 00 DC FF ... with software ID 0x0A.
            Assert.Equal("12 FF 0C 3C 00 40 00 05 05 00 DC FF 04 00 DC FF 03 00 DC FF 02 00 DC FF 01 00 DC FF",
                HidppHapticsTests.Text(frame));
        }
    }
}
