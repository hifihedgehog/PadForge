using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Resources.Strings;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The light map of a Logitech keyboard with 0x8081 (#557), against the
    /// zone numbers of Solaar's KEYCODES and OpenRGB's tables, the region
    /// keys keyleds' layout files carry for each layout code, and the PRO X
    /// TKL RAPID's own numbers.
    /// </summary>
    public class HidppKeyMapTests
    {
        internal static IEnumerable<byte> Run(int first, int last) => Enumerable.Range(first, last - first + 1).Select(z => (byte)z);

        /// <summary>Every zone a G915 X reports: 1 to 111, the brightness
        /// and media keys, G1 to G9 and the logo (OpenRGB issue 4461,
        /// unplug_and_replug.pcapng).</summary>
        internal static readonly byte[] G915XZones =
            Run(1, 111).Concat(new byte[] { 153, 155, 156, 157, 158 }).Concat(Run(180, 188)).Append((byte)210).ToArray();

        /// <summary>A keyboard painted key by key with the zones, layout
        /// code and model IDs given.</summary>
        internal static HidppUnit Board(IEnumerable<byte> zones, int layout = 1, params ushort[] models)
            => HidppLightPlanTests.StreamUnit() with
            {
                Name = "G915 X", PerKeyZones = zones.ToArray(), KeyboardLayout = layout, ModelIds = models,
            };

        /// <summary>Each zone names the key whose usage is the zone plus 3,
        /// the modifiers sit at 104 to 111, and the extras are named by
        /// what they are. An ANSI board leaves out the ISO keys at 47 and 97
        /// and the zones 99 to 103 no board has.</summary>
        [Fact]
        public void TheZonesNameTheirKeys_AsSolaarAndOpenRgbNumberThem()
        {
            var map = HidppKeyMap.Of(Board(G915XZones, models: new ushort[] { 0xB38A, 0x40B5, 0xC356 }));
            LightInfo At(int zone) => map.Lights.Single(l => l.Address == zone);

            // A, Z, 1, Enter, Esc, backslash, F1, F12, Num Lock, keypad .
            // and the Application key (Solaar KEYCODES).
            Assert.Equal(new ushort[] { 0x04, 0x1D, 0x1E, 0x28, 0x29, 0x31, 0x3A, 0x45, 0x53, 0x63, 0x65 },
                new[] { 1, 26, 27, 37, 38, 46, 55, 66, 80, 96, 98 }.Select(z => At(z).Usage));
            Assert.Equal(Enumerable.Range(0xE0, 8).Select(u => (ushort)u), Run(104, 111).Select(z => At(z).Usage));
            Assert.All(map.Lights.Where(l => l.Kind == LightKind.Key), l => Assert.Equal(LightKeySource.Vendor, l.KeySource));

            Assert.Equal(new[] { LightLabel.Brightness, LightLabel.PlayPause, LightLabel.Mute, LightLabel.Next, LightLabel.Previous },
                new[] { 153, 155, 156, 157, 158 }.Select(z => At(z).Label));
            Assert.All(Run(180, 188), z => Assert.Equal(LightLabel.GKey, At(z).Label));
            Assert.Equal(Enumerable.Range(1, 9), Run(180, 188).Select(z => At(z).LabelNumber));
            Assert.Equal(LightKind.Logo, At(210).Kind);

            Assert.DoesNotContain(map.Lights, l => l.Address is 47 or 97 or (>= 99 and <= 103));
            Assert.Equal(104 + 5 + 9 + 1, map.Lights.Count);
            Assert.Equal(104, map.KeyCount);
            Assert.Equal(0, map.UnnamedKeyCount);
            Assert.True(map.HasKeys && map.PerLight);
        }

        /// <summary>The region keys a board keeps are the ones its layout
        /// code's board has: the keyleds layout files, read from Logitech's
        /// boards, for the codes they cover, OpenRGB's ISO and ABNT2 codes
        /// beyond them, and ANSI for any other code or none.</summary>
        [Theory]
        [InlineData(0x01, true, false, false)]
        [InlineData(0x09, true, false, false)]
        [InlineData(0x0B, true, false, false)]
        [InlineData(0x00, true, false, false)]
        [InlineData(0x33, true, false, false)]
        [InlineData(0x02, false, true, true)]
        [InlineData(0x03, false, true, true)]
        [InlineData(0x04, false, true, true)]
        [InlineData(0x05, false, true, true)]
        [InlineData(0x08, false, true, true)]
        [InlineData(0x0D, false, true, true)]
        [InlineData(0x0E, false, true, true)]
        [InlineData(0x0F, false, true, true)]
        [InlineData(0x11, false, true, true)]
        [InlineData(0x38, false, true, true)]
        [InlineData(0x41, false, true, true)]
        [InlineData(0x07, false, true, false)]
        [InlineData(0x0A, false, true, false)]
        public void TheRegionKeysAreTheOnesTheLayoutCodesBoardHas(int code, bool backslash, bool pound, bool leftOfZ)
        {
            var map = HidppKeyMap.Of(Board(Run(1, 111), code));
            Assert.Equal((backslash, pound, leftOfZ),
                (map.IndexesOf(0x31).Count == 1, map.IndexesOf(0x32).Count == 1, map.IndexesOf(0x64).Count == 1));
            Assert.Equal(0, map.Lights.Count(l => l.Address is >= 99 and <= 103));
        }

        /// <summary>The PRO X TKL RAPID has its five media keys at its own
        /// numbers (OpenRGB proxrapid_top_strip, Solaar
        /// keyboard_pro_x_rapid.py), and none of the usual extras its
        /// firmware also lists, which are not wired (OpenRGB
        /// known_kb_zone_policies).</summary>
        [Fact]
        public void TheProXTklRapid_HasItsOwnMediaKeys_AndNoneOfTheUsualExtras()
        {
            var zones = Run(1, 111).Concat(new byte[] { 150, 152, 153, 154, 155, 156, 157, 158 }).Concat(Run(180, 184))
                .Append((byte)210).Append((byte)254);
            var map = HidppKeyMap.Of(Board(zones, models: HidppKeyMap.ProXTklRapid));
            Assert.Equal(new (int, LightLabel)[]
            {
                (150, LightLabel.Brightness), (152, LightLabel.PlayPause), (153, LightLabel.Mute),
                (154, LightLabel.Next), (155, LightLabel.Previous),
            }, map.Lights.Where(l => l.Kind != LightKind.Key).Select(l => (l.Address, l.Label)));
            Assert.Equal(104, map.KeyCount);

            // Another board reads the same numbers as the usual extras.
            var other = HidppKeyMap.Of(Board(zones, models: 0xC356));
            Assert.Equal(LightLabel.Brightness, other.Lights.Single(l => l.Address == 153).Label);
            Assert.Contains(other.Lights, l => l.Address == 210);
        }

        /// <summary>A zone no reference names is a key light with no key,
        /// which Map Key Lights asks about. Zones 0, 254 and 255 are none.</summary>
        [Fact]
        public void AZoneNoReferenceNames_IsAKeyMapKeyLightsAsksAbout()
        {
            var map = HidppKeyMap.Of(Board(new byte[] { 1, 120, 200, 254 }));
            Assert.Equal(new[] { 1, 120, 200 }, map.Lights.Select(l => l.Address));
            Assert.Equal(2, map.UnnamedKeyCount);
            Assert.Equal((LightKind.Key, (ushort)0, LightKeySource.None),
                (map.Lights[1].Kind, map.Lights[1].Usage, map.Lights[1].KeySource));
            Assert.Null(HidppKeyMap.LightOf(0, HidppKeyMap.RegionKeys.Backslash, false));
            Assert.Null(HidppKeyMap.LightOf(255, HidppKeyMap.RegionKeys.Backslash, false));
        }

        /// <summary>The model Map Key Lights keeps its names under is the
        /// first model ID and the layout code, or the name with no model ID.
        /// Only a keyboard painted key by key through 0x8081 has a map.</summary>
        [Fact]
        public void TheModelIsTheFirstModelIdAndTheLayoutCode_AndOnlyAKeyboardPaintedKeyByKeyHasAMap()
        {
            Assert.Equal("hidpp:B38A:01", HidppKeyMap.Of(Board(G915XZones, models: new ushort[] { 0xB38A, 0x40B5, 0xC356 })).Product);
            Assert.Equal("hidpp:G915 X:04", HidppKeyMap.Of(Board(G915XZones, layout: 4)).Product);
            Assert.Null(HidppKeyMap.Of(Board(Array.Empty<byte>())));
            Assert.Null(HidppKeyMap.Of(Board(G915XZones) with { DeviceType = 3 }));
            Assert.Null(HidppKeyMap.Of(HidppLightPlanTests.BoardUnit()));
            Assert.Null(HidppKeyMap.Of(null));
        }

        /// <summary>A labeled light reads its label on its chip: a G-key as
        /// Logitech prints it, the media keys by the names the macro editor
        /// gives them, in the interface's language when it is drawn.</summary>
        [Fact]
        public void ALabeledLightReadsItsLabel()
        {
            var s = Strings.Instance;
            LightInfo Light(LightLabel label, int number = 0)
                => new(180, LightKind.Other, 0, LightKeySource.Vendor) { Label = label, LabelNumber = number };
            Assert.Equal("G5", KeyLightsView.Legend(Light(LightLabel.GKey, 5), IntPtr.Zero));
            Assert.Equal(s.Key_VolumeMute, KeyLightsView.Legend(Light(LightLabel.Mute), IntPtr.Zero));
            Assert.Equal(s.Key_MediaPlayPause, KeyLightsView.Legend(Light(LightLabel.PlayPause), IntPtr.Zero));
            Assert.Equal(s.Key_MediaNext, KeyLightsView.Legend(Light(LightLabel.Next), IntPtr.Zero));
            Assert.Equal(s.Key_MediaPrev, KeyLightsView.Legend(Light(LightLabel.Previous), IntPtr.Zero));
            Assert.Equal(s.Pad_Lighting_GuideLed_Brightness, KeyLightsView.Legend(Light(LightLabel.Brightness), IntPtr.Zero));
            Assert.Equal("G5", KeyLightsView.LightName(Light(LightLabel.GKey, 5), "G5", IntPtr.Zero));
            Assert.Null(KeyLightsView.LabelText(new LightInfo(1, LightKind.Other, 0, LightKeySource.None)));
        }
    }

    /// <summary>
    /// The frames that paint a 0x8081 keyboard zone by zone (#557): the
    /// grouping OpenRGB gives a frame's changed zones, the limits of each
    /// function per Logitech's x8081 document, and one frame end for the
    /// whole picture.
    /// </summary>
    public class HidppPerKeyFrameTests
    {
        private static string[] Text(IEnumerable<HidppStep> steps)
            => steps.Select(s => (s.Kind == HidppStepKind.Write ? "" : s.Kind.ToString().ToLowerInvariant() + " ")
                                 + HidppHapticsTests.Text(s.Frame)).ToArray();

        /// <summary>Zones 1 to 12: black, red on 2 to 4, green on 6 and 9,
        /// blue on 11, and 12 in a color of its own.</summary>
        private static int[] Picture() => new[]
        {
            0x000000, 0xFF0000, 0xFF0000, 0xFF0000, 0x000000, 0x00FF00,
            0x000000, 0x000000, 0x00FF00, 0x000000, 0x0000FF, 0x123456,
        };

        /// <summary>The whole picture is one frame: a range over every zone
        /// in the color most zones take, then each other color's run as a
        /// range, its scattered zones as one color, the lone colors four to
        /// a frame, and the frame end.</summary>
        [Fact]
        public void TheWholePicture_IsOneFrame_TheMostCommonColorFirst()
        {
            var unit = HidppKeyMapTests.Board(HidppKeyMapTests.Run(1, 12));
            var map = HidppKeyMap.Of(unit);
            var plan = HidppLightPlan.For(unit);
            Assert.True(plan.PaintsLights);
            Assert.Equal(new[]
            {
                "stream 11 FF 09 5C 01 D2",
                "stream 11 FF 09 6C 00 FF 00 06 09",
                "stream 11 FF 09 5C 02 04 FF",
                "stream 11 FF 09 1C 0B 00 00 FF 0C 12 34 56",
                "commit 11 FF 09 7C",
            }, Text(plan.PaintLights(map, Picture(), null, full: false)));

            // The keep-alive is the whole picture again.
            Assert.Equal(Text(plan.PaintLights(map, Picture(), null, full: false)),
                Text(plan.KeepAliveLights(0, map, Picture(), painted: true)));

            // A first paint is the one-color first paint in that color, its
            // clusters, Host streaming and its first frame, then the picture.
            Assert.Equal(new[]
            {
                "11 FF 08 1C 00 01 00 00 00 02 00 00 00 00 00 00 01",
                "11 FF 08 1C 01 01 00 00 00 02 00 00 00 00 00 00 01",
                "select 11 FF 08 1C FF 01 00 00 00 00 00 00 00 00 00 00 01",
                "stream 11 FF 09 5C 01 D2",
                "commit 11 FF 09 7C",
            }.Concat(Text(plan.PaintLights(map, Picture(), null, full: false))), Text(plan.PaintLights(map, Picture(), null, full: true)));
            Assert.Equal(Text(plan.PaintLights(map, Picture(), null, full: true)),
                Text(plan.KeepAliveLights(2, map, Picture(), painted: false)));
        }

        /// <summary>A change sends only the zones whose color changed.</summary>
        [Fact]
        public void AChange_SendsTheChangedZonesAlone()
        {
            var unit = HidppKeyMapTests.Board(HidppKeyMapTests.Run(1, 12));
            var map = HidppKeyMap.Of(unit);
            var plan = HidppLightPlan.For(unit);
            var shown = Picture();
            var colors = Picture();
            colors[2] = 0x0000FF;
            colors[4] = 0xABCDEF;
            Assert.Equal(new[] { "stream 11 FF 09 1C 03 00 00 FF 05 AB CD EF", "commit 11 FF 09 7C" },
                Text(plan.PaintLights(map, colors, shown, full: false)));
            Assert.Equal(new[] { "commit 11 FF 09 7C" }, Text(plan.PaintLights(map, shown, shown, full: false)));
        }

        /// <summary>Each function holds no more than Logitech's document
        /// lets it: three ranges, 13 zones of one color, four lone zones.
        /// Two neighbors of one color are not a run.</summary>
        [Fact]
        public void EachFrameHoldsNoMoreThanItsFunctionTakes()
        {
            var unit = HidppKeyMapTests.Board(HidppKeyMapTests.Run(1, 80));
            var map = HidppKeyMap.Of(unit);
            var plan = HidppLightPlan.For(unit);
            var colors = new int[map.Lights.Count];
            // An ANSI board has no zone 47, so a zone's place in the map is
            // looked up.
            void Set(int zone, int rgb) => colors[map.Lights.ToList().FindIndex(l => l.Address == zone)] = rgb;
            // Four runs of three in red: zones 1-3, 5-7, 9-11, 13-15.
            foreach (int run in new[] { 1, 5, 9, 13 })
                for (int z = run; z < run + 3; z++) Set(z, 0xFF0000);
            // Fourteen scattered greens, and one pair of neighbors: 20, 22,
            // ... 46, then 49 and 50.
            for (int z = 20; z <= 46; z += 2) Set(z, 0x00FF00);
            Set(49, 0x00FF00);
            Set(50, 0x00FF00);
            // Five lone colors on zones 60 to 64.
            for (int i = 0; i < 5; i++) Set(60 + i, 0x000010 + i);

            var lines = Text(plan.PaintLights(map, colors, new int[colors.Length], full: false));
            Assert.Equal(new[]
            {
                "stream 11 FF 09 1C 3C 00 00 10 3D 00 00 11 3E 00 00 12 3F 00 00 13",
                "stream 11 FF 09 1C 40 00 00 14",
            }, lines.Where(l => l.StartsWith("stream 11 FF 09 1C", StringComparison.Ordinal)));
            Assert.Equal(new[]
            {
                "stream 11 FF 09 6C 00 FF 00 14 16 18 1A 1C 1E 20 22 24 26 28 2A 2C",
                "stream 11 FF 09 6C 00 FF 00 2E 31 32",
            }, lines.Where(l => l.StartsWith("stream 11 FF 09 6C", StringComparison.Ordinal)));
            Assert.Equal(new[]
            {
                "stream 11 FF 09 5C 01 03 FF 00 00 05 07 FF 00 00 09 0B FF",
                "stream 11 FF 09 5C 0D 0F FF",
            }, lines.Where(l => l.StartsWith("stream 11 FF 09 5C", StringComparison.Ordinal)));
            Assert.Equal("commit 11 FF 09 7C", lines[^1]);
        }

        /// <summary>A plan that paints one color has no frames light by
        /// light, so the worker paints it one color.</summary>
        [Fact]
        public void OnlyTheStreamPlanPaintsLightByLight()
        {
            Assert.False(HidppLightPlan.For(HidppLightPlanTests.ClusterUnit()).PaintsLights);
            Assert.False(HidppLightPlan.For(HidppLightPlanTests.BoardUnit()).PaintsLights);
            Assert.False(HidppLightPlan.For(HidppLightPlanTests.ZoneUnit()).PaintsLights);
            Assert.False(HidppLightPlan.For(HidppLightPlanTests.StreamUnit()).WithoutPerKey.PaintsLights);
            Assert.Null(HidppLightPlan.For(HidppLightPlanTests.ClusterUnit()).PaintLights(LightMap.Empty, Array.Empty<int>(), null, true));
        }
    }
}
