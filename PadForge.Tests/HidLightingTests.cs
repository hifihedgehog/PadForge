using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Lenovo Legion keyboard family of the shared HID lighting worker
    /// (#557): its frames byte for byte against OpenRGB's
    /// LenovoUSBController_Gen7_8.cpp and against OpenRGB's own writes as
    /// the owner's Legion Pro 7 16AFR10H stores them, its lights against
    /// LenovoDevices.h, and its reading of the keyboard's answers.
    /// </summary>
    public class LenovoLegionLightingTests
    {
        internal static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, Math.Min(count, bytes.Length)).Replace('-', ' ');

        internal static byte[] Bytes(string hex) => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => Convert.ToByte(b, 16)).ToArray();

        internal static byte[] Padded(byte[] head)
        {
            var report = new byte[LenovoLegionLighting.PacketSize];
            head.CopyTo(report, 0);
            return report;
        }

        /// <summary>The 101 keyboard addresses in OpenRGB's order
        /// (LenovoDevices.h legion7_gen7and8_ansi_leds), as hex pairs low
        /// byte first.</summary>
        private static string KeyboardKeys()
            => string.Join(" ", LenovoLegionLighting.Gen10Lights().Take(101).Select(l => $"{l.Address & 0xFF:X2} {l.Address >> 8:X2}"));

        private static string EdgeKeys()
            => string.Join(" ", LenovoLegionLighting.Gen10Lights().Skip(101).Select(l => $"{l.Address & 0xFF:X2} {l.Address >> 8:X2}"));

        /// <summary>Profile 5 of the owner's keyboard as GET_PROFILE answered
        /// it on 2026-11-02: a write of OpenRGB's Static mode, the keyboard's
        /// 101 keys in 0x101010 and the edge lights left out, with OpenRGB's
        /// Gen 10 payload length 224.</summary>
        internal static byte[] OwnersProfile5Answer()
            => Padded(Bytes("07 CC E0 00 05 01 01 01 06 01 0B 02 00 03 00 04 03 05 02 06 00 01 10 10 10 65 " + KeyboardKeys()));

        /// <summary>Profile 6 of the owner's keyboard, read the same day:
        /// OpenRGB's Static mode with every one of the 130 lights white,
        /// payload length 282.</summary>
        internal static byte[] OwnersProfile6Answer()
            => Padded(Bytes("07 CC 1A 01 06 01 01 01 06 01 0B 02 00 03 00 04 03 05 02 06 00 01 FF FF FF 82 "
                            + KeyboardKeys() + " " + EdgeKeys()));

        /// <summary>Profile 4 of the owner's keyboard, written by another
        /// program with the length 960: Rain on the keys and Smooth on the
        /// edges, random colors, the keys sorted.</summary>
        internal static byte[] OwnersProfile4Answer()
        {
            var keys = LenovoLegionLighting.Gen10Lights().Take(101).Select(l => l.Address).OrderBy(a => a)
                .Select(a => $"{a & 0xFF:X2} {a >> 8:X2}");
            var edges = LenovoLegionLighting.Gen10Lights().Skip(101).Select(l => l.Address)
                .OrderBy(a => a == 0x05DD ? 2 : a >> 8 == 1 ? 0 : 1).ThenBy(a => a)
                .Select(a => $"{a & 0xFF:X2} {a >> 8:X2}");
            return Padded(Bytes("07 CC C0 03 04 01 01 01 06 01 07 02 03 03 00 04 00 05 01 06 00 00 65 "
                                + string.Join(" ", keys)
                                + " 02 06 01 06 02 03 03 00 04 00 05 01 06 00 00 1D " + string.Join(" ", edges)));
        }

        private static readonly ISet<int> Addresses = LenovoLegionLighting.Gen10Lights().Select(l => l.Address).ToHashSet();

        [Fact]
        public void TheProfileQuery_IsOpenRgbsGen10Frame()
        {
            var frame = LenovoLegionLighting.ActiveProfileQuery();
            Assert.Equal(960, frame.Length);
            // {REPORT_ID, GET_ACTIVE_PROFILE, 0xC0, 0x03} with bytes 2 and 3
            // overwritten by the payload length 1
            // (LenovoUSBController_Gen7_8.cpp:232-234).
            Assert.Equal("07 CA 01 00 00", Hex(frame, 5));
            Assert.All(frame.Skip(4), b => Assert.Equal(0, b));
        }

        [Fact]
        public void TheContentsQuery_IsOpenRgbsGen10Frame()
        {
            // {REPORT_ID, GET_PROFILE, 0xC0, 0x03, profile} with the length
            // PACKET_SIZE - 4 in bytes 2 and 3 (lines 269-271).
            var frame = LenovoLegionLighting.ContentsQuery(5);
            Assert.Equal(960, frame.Length);
            Assert.Equal("07 CC BC 03 05 00", Hex(frame, 6));
            Assert.All(frame.Skip(5), b => Assert.Equal(0, b));
        }

        [Fact]
        public void TheProfileSwitch_IsOpenRgbsGen10Frame()
        {
            // {REPORT_ID, SWITCH_PROFILE, 0xC0, 0x03, profile} with the
            // length 1 (lines 259-265).
            Assert.Equal("07 C8 01 00 03 00", Hex(LenovoLegionLighting.ProfileSwitch(3), 6));
        }

        [Fact]
        public void TheStaticFrame_IsOpenRgbsStaticGroups()
        {
            var lights = new[]
            {
                new LightInfo(0x0001, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0x03E9, LightKind.Edge, 0, LightKeySource.None),
            };
            var frame = LenovoLegionLighting.StaticFrame(5, lights, new[] { 0x112233, 0xAABBCC });
            // setLedsByGroup (lines 81-119): profile, 01, 01, then per
            // group its number, 06 01 mode 02 speed 03 spin 04 direction 05
            // color_mode 06 00, one color, the light count and the
            // addresses. Payload 3 + 2 x 21 = 45 (lines 122-125).
            Assert.Equal("07 CB 2D 00 05 01 01 "
                         + "01 06 01 0B 02 00 03 00 04 03 05 02 06 00 01 11 22 33 01 01 00 "
                         + "02 06 01 0B 02 00 03 00 04 03 05 02 06 00 01 AA BB CC 01 E9 03 00", Hex(frame, 50));
            Assert.Equal(960, frame.Length);
        }

        [Fact]
        public void TheStaticFrame_MatchesOpenRgbsWritesAsTheOwnersKeyboardHoldsThem()
        {
            // The keys in 0x101010 and the edges black: OpenRGB leaves the
            // black lights out, and so does PadForge.
            var lights = LenovoLegionLighting.Gen10Lights();
            var dim = Enumerable.Range(0, lights.Count).Select(n => n < 101 ? 0x101010 : 0).ToArray();
            var written = LenovoLegionLighting.StaticFrame(5, lights, dim);
            var answer = OwnersProfile5Answer();
            answer[1] = LenovoLegionLighting.SaveProfile;
            Assert.Equal(answer, written);

            // Every light white: one group of all 130 in OpenRGB's zone order.
            var white = LenovoLegionLighting.StaticFrame(6, lights, Enumerable.Repeat(0xFFFFFF, lights.Count).ToArray());
            var six = OwnersProfile6Answer();
            six[1] = LenovoLegionLighting.SaveProfile;
            Assert.Equal(six, white);
        }

        [Fact]
        public void TheStaticFrame_GroupsByColorInTheOrderTheyAppear_AndLeavesBlackLightsDark()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            var colors = new int[lights.Count];
            colors[0] = 0xFF0000;
            colors[1] = 0x00FF00;
            colors[2] = 0xFF0000;
            var groups = LenovoLegionLighting.Groups(lights, colors);
            Assert.Equal(new[] { 0xFF0000, 0x00FF00 }, groups.Select(g => g.Color));
            Assert.Equal(new[] { 0, 2 }, groups[0].Lights);
            Assert.Equal(new[] { 1 }, groups[1].Lights);

            // All black: GetLedGroups keeps the black group when it is the
            // only one (lines 721-726).
            var black = Assert.Single(LenovoLegionLighting.Groups(lights, new int[lights.Count]));
            Assert.Equal(0, black.Color);
            Assert.Equal(lights.Count, black.Lights.Count);
        }

        [Fact]
        public void APictureOfManyColors_FitsOneFrame_ByMergingTheNearest()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            // 130 distinct colors would take 7 + 130 x 21 bytes.
            var colors = Enumerable.Range(0, lights.Count).Select(n => 0x010000 * (n + 1) + 0x80).ToArray();
            var groups = LenovoLegionLighting.Groups(lights, colors);
            Assert.True(LenovoLegionLighting.SizeOf(groups) <= LenovoLegionLighting.PacketSize);
            Assert.Equal(36, groups.Count);
            Assert.Equal(Enumerable.Range(0, lights.Count), groups.SelectMany(g => g.Lights).OrderBy(n => n));
            // Each group keeps a color one of its lights asked for.
            Assert.All(groups, g => Assert.Contains(g.Lights, n => colors[n] == g.Color));
            var frame = LenovoLegionLighting.StaticFrame(5, lights, colors);
            Assert.Equal(LenovoLegionLighting.SizeOf(groups) - 4, frame[2] | frame[3] << 8);
        }

        [Fact]
        public void TheProfile_IsByteFourOfAnAnswerThatCarriesTheQuerysOpcode()
        {
            Assert.Equal((byte)5, LenovoLegionLighting.ProfileOf(new byte[] { 0x07, 0xCA, 0x01, 0x00, 0x05 }));
            Assert.Null(LenovoLegionLighting.ProfileOf(new byte[] { 0x07, 0xCD, 0x01, 0x00, 0x05 }));
            Assert.Null(LenovoLegionLighting.ProfileOf(new byte[] { 0x07, 0xCA, 0x01, 0x00 }));
            Assert.Null(LenovoLegionLighting.ProfileOf(null));
        }

        [Fact]
        public void TheContents_AreTheAnswerFromItsLengthBytes_ToItsLastEffect()
        {
            var contents = LenovoLegionLighting.ContentsOf(OwnersProfile5Answer(), 5);
            Assert.NotNull(contents);
            // The two length bytes and the 224 they count.
            Assert.Equal(226, contents.Length);
            Assert.Equal("E0 00 05 01 01 01 06", Hex(contents, 7));
            // Sent back as SAVE_PROFILE they are the frame that wrote them.
            var restore = LenovoLegionLighting.RestoreFrame(contents);
            var answer = OwnersProfile5Answer();
            answer[1] = LenovoLegionLighting.SaveProfile;
            Assert.Equal(answer, restore);

            // Another program's profile, length 960: two effects end at
            // byte 299, as the owner's keyboard answered.
            var four = LenovoLegionLighting.ContentsOf(OwnersProfile4Answer(), 4);
            Assert.Equal(299 - 2, four.Length);
            Assert.Equal("C0 03 04", Hex(four, 3));
        }

        [Fact]
        public void BytesLeftByAnotherReport_AfterTheLastEffect_StartNone()
        {
            // The owner's keyboard answered with the tail of an earlier
            // frame where its own answer ended.
            var answer = OwnersProfile4Answer();
            var leftover = Bytes("29 FF A5 00 00 29 FF A7 00 00 29 FF 9C 00 00 29 FF 9F 00 00 29 FF A1 00");
            leftover.CopyTo(answer, 299);
            Assert.Equal(297, LenovoLegionLighting.ContentsOf(answer, 4).Length);
            leftover.CopyTo(answer, 484);
            Assert.Equal(297, LenovoLegionLighting.ContentsOf(answer, 4).Length);
        }

        [Fact]
        public void AnAnswerForAnotherProfileOrOfAnotherKind_HoldsNoContents()
        {
            Assert.Null(LenovoLegionLighting.ContentsOf(OwnersProfile5Answer(), 4));
            var other = OwnersProfile5Answer();
            other[1] = 0xCA;
            Assert.Null(LenovoLegionLighting.ContentsOf(other, 5));
            // An effect that runs past the report.
            var cut = OwnersProfile5Answer();
            cut[25] = 0xFF;
            Assert.Null(LenovoLegionLighting.ContentsOf(cut.Take(200).ToArray(), 5));
            Assert.Null(LenovoLegionLighting.ContentsOf(null, 5));
        }

        [Fact]
        public void APictureOfPadForges_IsToldFromAnotherProgramsProfile()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            var picture = LenovoLegionLighting.StaticFrame(5, lights, Enumerable.Repeat(0x123456, lights.Count).ToArray());
            picture[1] = LenovoLegionLighting.GetProfile;
            Assert.True(LenovoLegionLighting.IsOwnPicture(LenovoLegionLighting.ContentsOf(picture, 5), 5, Addresses));
            // OpenRGB's Static mode writes the same frame.
            Assert.True(LenovoLegionLighting.IsOwnPicture(LenovoLegionLighting.ContentsOf(OwnersProfile5Answer(), 5), 5, Addresses));
            // Effects of the keyboard's own, the length 960, another profile.
            Assert.False(LenovoLegionLighting.IsOwnPicture(LenovoLegionLighting.ContentsOf(OwnersProfile4Answer(), 4), 4, Addresses));
            Assert.False(LenovoLegionLighting.IsOwnPicture(LenovoLegionLighting.ContentsOf(picture, 5), 4, Addresses));
            // A light the model does not have.
            Assert.False(LenovoLegionLighting.IsOwnPicture(LenovoLegionLighting.ContentsOf(picture, 5), 5, new HashSet<int> { 0x0001 }));
        }

        [Fact]
        public void TheGen10Board_HasOpenRgbsZonesInItsOrder()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            // 101 keyboard lights, 10 neon, the logo, 18 vents
            // (LenovoDevices.h:1739-1985, RGBController_Lenovo_Gen7_8.cpp:424-428).
            Assert.Equal(130, lights.Count);
            Assert.Equal(101, lights.Count(l => l.Address >> 8 == LenovoLegionLighting.KeyboardZone));
            Assert.Equal(Enumerable.Range(0xF5, 10).Select(n => 0x100 | n), lights.Skip(101).Take(10).Select(l => l.Address));
            Assert.All(lights.Skip(101).Take(10), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(0x05DD, lights[111].Address);
            Assert.Equal(LightKind.Logo, lights[111].Kind);
            Assert.Equal(Enumerable.Range(0xE9, 18).Select(n => 0x300 | n), lights.Skip(112).Select(l => l.Address));
            Assert.All(lights.Skip(112), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
        }

        [Fact]
        public void EveryKeyOfTheBoard_IsNamed_AndFnCarriesItsLabel()
        {
            var keys = LenovoLegionLighting.Gen10Lights().Take(101).ToList();
            var named = keys.Where(l => l.Kind == LightKind.Key).ToList();
            Assert.Equal(100, named.Count);
            Assert.All(named, l => Assert.NotEqual(0, l.Usage));
            Assert.Equal(100, named.Select(l => l.Usage).Distinct().Count());
            var fn = Assert.Single(keys, l => l.Kind != LightKind.Key);
            Assert.Equal(0x0080, fn.Address);
            Assert.Equal(LightLabel.Fn, fn.Label);
            // A few keys against the HID Usage Tables, by OpenRGB's numbers.
            Assert.Equal(0x29, keys.Single(l => l.Address == 0x01).Usage);  // Escape
            Assert.Equal(0x31, keys.Single(l => l.Address == 0x4E).Usage);  // \ (ANSI)
            Assert.Equal(0x04, keys.Single(l => l.Address == 0x6D).Usage);  // A
            Assert.Equal(0x58, keys.Single(l => l.Address == 0xA7).Usage);  // Number Pad Enter
            Assert.Equal(0x4F, keys.Single(l => l.Address == 0xA1).Usage);  // Right Arrow
        }

        [Fact]
        public void TheEdgeLights_CarryNumbersOnTheirChips_AsZonesDo()
        {
            // 28 chips all reading Edge could not be told apart.
            var map = new LightMap(LenovoLegionLighting.Gen10Lights(), "test");
            Assert.Equal(28, map.EdgeCount);
            Assert.Equal(0, map.EdgeNumberOf(0));      // Escape
            Assert.Equal(1, map.EdgeNumberOf(101));    // neon 1
            Assert.Equal(10, map.EdgeNumberOf(110));   // neon 10
            Assert.Equal(0, map.EdgeNumberOf(111));    // the logo
            Assert.Equal(11, map.EdgeNumberOf(112));   // vent 1
            Assert.Equal(28, map.EdgeNumberOf(129));   // vent 18
            Assert.Equal(0, map.EdgeNumberOf(130));
            var s = Strings.Instance;
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, s.Pad_Lighting_KeyColors_Edge_Format, 11),
                KeyLightsView.Legend(map.Lights[112], IntPtr.Zero, map.EdgeNumberOf(112)));

            // One edge light keeps the plain name.
            var single = new LightMap(new[]
            {
                new LightInfo(1, LightKind.Key, 0x29, LightKeySource.Device),
                new LightInfo(2, LightKind.Edge, 0, LightKeySource.None),
            }, "test");
            Assert.Equal(0, single.EdgeNumberOf(1));
            Assert.Equal(s.Pad_Lighting_KeyColors_Edge, KeyLightsView.Legend(single.Lights[1], IntPtr.Zero, single.EdgeNumberOf(1)));
        }

        [Fact]
        public void EveryOpenRgbNameOfTheTable_IsKnown()
        {
            foreach (var (_, name) in LenovoLegionLighting.Keys)
                Assert.True(OpenRgbKeys.TryGet(name, out _, out _), name);
            Assert.False(OpenRgbKeys.TryGet("Key: _", out _, out _));
        }

        private static HidCollectionInfo Collection(ushort pid = 0xC197, ushort page = 0xFF89, ushort usage = 0x07,
            ushort feature = 960)
            => new()
            {
                Path = $@"\\?\hid#vid_048d&pid_{pid:x4}&col02#8&11b458cb&0&0001",
                VendorId = 0x048D, ProductId = pid, UsagePage = page, Usage = usage, FeatureReportLength = feature,
            };

        [Fact]
        public void TheFamily_AnswersOnOpenRgbsCollectionAlone()
        {
            var model = HidLightingFamilies.Match(Collection());
            Assert.NotNull(model);
            Assert.Same(LenovoLegionLighting.Instance, model.Family);
            Assert.True(model.LitBesideWindows);
            Assert.Equal("lenovo:048D:C197", model.Map.Product);
            // A picture is written once it has held, at most twice a second.
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            // The keyboard collection of the same device, the controller's
            // other vendor collections, the 24-zone board, and a short
            // feature report.
            Assert.Null(HidLightingFamilies.Match(Collection(page: 0x0001, usage: 0x06)));
            Assert.Null(HidLightingFamilies.Match(Collection(usage: 0x10)));
            Assert.Null(HidLightingFamilies.Match(Collection(pid: 0xC195)));
            Assert.Null(HidLightingFamilies.Match(Collection(feature: 17)));
        }
    }

    /// <summary>
    /// A Lenovo Legion Gen 10 keyboard's lighting controller as the owner's
    /// answered on 2026-11-02 (#557). GET_ACTIVE_PROFILE answers at once.
    /// GET_PROFILE makes the profile it reads the active one and answers
    /// with the profile's frame as it was written, and with
    /// <see cref="LateAnswers"/> the first read after it returns the answer
    /// before it. SAVE_PROFILE stores its frame, SWITCH_PROFILE switches.
    /// </summary>
    internal sealed class FakeSpectrumKeyboard
    {
        private readonly object _gate = new();
        private byte[] _answer = new byte[LenovoLegionLighting.PacketSize];
        private byte[] _late;

        private byte _active = 5;
        public volatile bool LateAnswers;

        /// <summary>The active profile.</summary>
        public byte Active
        {
            get { lock (_gate) return _active; }
            set { lock (_gate) _active = value; }
        }

        /// <summary>Stores <paramref name="frame"/> in a profile, as another
        /// program's SAVE_PROFILE would.</summary>
        public void Store(byte profile, byte[] frame)
        {
            lock (_gate) Profiles[profile] = (byte[])frame.Clone();
        }

        /// <summary>Answers GET_ACTIVE_PROFILE with another opcode.</summary>
        public volatile bool Mute;

        /// <summary>The frames each profile was last written with.</summary>
        public readonly Dictionary<byte, byte[]> Profiles = new();

        public readonly List<string> Log = new();

        /// <summary>Every GET_PROFILE for a profile that was not the active
        /// one, which would have switched the keyboard to it.</summary>
        public readonly List<string> ForeignReads = new();

        public void Request(byte[] report)
        {
            lock (_gate)
            {
                switch (report[1])
                {
                    case LenovoLegionLighting.GetActiveProfile:
                        Log.Add("CA");
                        _late = null;
                        _answer = Mute
                            ? LenovoLegionLightingTests.Padded(new byte[] { 0x07, 0xCD, 0x01, 0x00, _active })
                            : LenovoLegionLightingTests.Padded(new byte[] { 0x07, 0xCA, 0x01, 0x00, _active });
                        break;
                    case LenovoLegionLighting.GetProfile:
                    {
                        byte profile = report[4];
                        Log.Add("CC " + profile);
                        if (profile != _active) ForeignReads.Add($"CC {profile} while {_active}");
                        _active = profile;
                        var stored = Stored(profile);
                        stored[1] = LenovoLegionLighting.GetProfile;
                        if (LateAnswers) _late = stored;
                        else _answer = stored;
                        break;
                    }
                    case LenovoLegionLighting.SaveProfile:
                        Log.Add("CB " + report[4]);
                        Profiles[report[4]] = (byte[])report.Clone();
                        break;
                    case LenovoLegionLighting.SwitchProfile:
                        Log.Add("C8 " + report[4]);
                        _active = report[4];
                        break;
                    default:
                        Log.Add(report[1].ToString("X2"));
                        break;
                }
            }
        }

        public byte[] Read()
        {
            lock (_gate)
            {
                var answer = _answer;
                if (_late != null)
                {
                    _answer = _late;
                    _late = null;
                }
                return (byte[])answer.Clone();
            }
        }

        /// <summary>The frame <paramref name="profile"/> holds, its opcode
        /// SAVE_PROFILE, or an empty profile.</summary>
        public byte[] Stored(byte profile)
        {
            lock (_gate)
                return Profiles.TryGetValue(profile, out var frame)
                    ? (byte[])frame.Clone()
                    : LenovoLegionLightingTests.Padded(new byte[] { 0x07, 0xCB, 0x03, 0x00, profile, 0x01, 0x01 });
        }

        public List<string> Calls()
        {
            lock (_gate) return Log.ToList();
        }
    }

    /// <summary>A channel to <see cref="FakeSpectrumKeyboard"/>.</summary>
    internal sealed class FakeSpectrumChannel : IHidReportChannel
    {
        public FakeSpectrumKeyboard Keyboard { get; init; }
        public string Path { get; init; }
        public bool FeatureOnly => false;
        public int InputReportLength => 0;
        public int OutputReportLength => 0;
        public int FeatureReportLength => LenovoLegionLighting.PacketSize;
        public volatile bool FailWrites;
        public volatile bool Disposed;

        public bool SetFeature(byte[] report)
        {
            if (FailWrites || Disposed || report == null) return false;
            Keyboard.Request(report);
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            if (FailWrites || Disposed || buffer == null) return false;
            var answer = Keyboard.Read();
            Array.Clear(buffer);
            Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs) => false;

        public int ReadInput(byte[] buffer, int timeoutMs) => -1;

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The shared HID lighting worker against a scripted Lenovo keyboard
    /// (#557): what it reads, what it saves before it writes, when a picture
    /// goes out, how it hands the profile back, and how a later run settles
    /// what a killed one left. The worker reads a clock the test sets, its
    /// waits move that clock, and a test changes one thing and then waits
    /// for two passes.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidLightingBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");
        private const string Product = "lenovo:048D:C197";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly FakeSpectrumKeyboard _keyboard = new();
        private readonly List<FakeSpectrumChannel> _channels = new();
        private readonly List<(string Path, byte[] Frame, int TimeoutMs)> _oneShots = new();
        private long _tick = 1_000_000;
        private int _opens;

        public HidLightingBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var five = LenovoLegionLightingTests.OwnersProfile5Answer();
            five[1] = LenovoLegionLighting.SaveProfile;
            _keyboard.Profiles[5] = five;
            var four = LenovoLegionLightingTests.OwnersProfile4Answer();
            four[1] = LenovoLegionLighting.SaveProfile;
            _keyboard.Profiles[4] = four;
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest();
        }

        private static string Hex(byte[] bytes, int count) => LenovoLegionLightingTests.Hex(bytes, count);

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);
        private int Opens => Volatile.Read(ref _opens);

        private IHidReportChannel Open(string path)
        {
            Interlocked.Increment(ref _opens);
            var channel = new FakeSpectrumChannel { Path = path, Keyboard = _keyboard };
            lock (_channels) _channels.Add(channel);
            return channel;
        }

        private FakeSpectrumChannel Channel(int index = -1)
        {
            lock (_channels) return index < 0 ? _channels[^1] : _channels[index];
        }

        private HidLightingBackend Started(params HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(null, Open, _ => Box, Tick,
                (path, frame, timeoutMs) =>
                {
                    lock (_oneShots) _oneShots.Add((path, (byte[])frame.Clone(), timeoutMs));
                    return true;
                },
                (_, _, _) => false, ms => Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(present);
            Passes(backend);
            return backend;
        }

        private static void Passes(HidLightingBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static HidCollectionInfo Spectrum => new()
        {
            Path = @"\\?\hid#vid_048d&pid_c197&col02#8&11b458cb&0&0001",
            VendorId = 0x048D, ProductId = 0xC197, UsagePage = 0xFF89, Usage = 0x07, FeatureReportLength = 960,
        };

        private static OutputPath PathOf(HidCollectionInfo collection) => new(OutputFamily.HidLightingUnit, collection.Path);

        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb, KeyFrame keys = null)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, keys: keys);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private static byte[] Original5()
            => LenovoLegionLighting.ContentsOf(LenovoLegionLightingTests.OwnersProfile5Answer(), 5);

        /// <summary>A picture of PadForge's in every light, as the keyboard
        /// stores it.</summary>
        private static byte[] Picture(byte profile, int rgb)
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            return LenovoLegionLighting.StaticFrame(profile, lights, Enumerable.Repeat(rgb, lights.Count).ToArray());
        }

        /// <summary>A claim on the keyboard, held until its first picture is
        /// written.</summary>
        private HidLightingBackend Lit(int rgb = 0x112233)
        {
            var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(rgb);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            return backend;
        }

        private List<string> Writes() => _keyboard.Calls().Where(c => c.StartsWith("CB") || c.StartsWith("C8")).ToList();

        [Fact]
        public void TheScan_ListsTheUnitAndPublishesItsLights_AndOpensNothing()
        {
            using var backend = Started(Spectrum);
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.Equal(OutputFamily.HidLightingUnit, unit.Family);
            Assert.Equal(Spectrum.Path, unit.Key);
            Assert.Equal(Box, unit.ContainerId);
            Assert.Equal((ushort)0x048D, unit.VendorId);
            Assert.Equal((ushort)0xC197, unit.ProductId);
            Assert.True(unit.LitBesideWindows);
            var map = PeripheralLightMaps.For(PathOf(Spectrum));
            Assert.NotNull(map);
            Assert.Equal(130, map.Lights.Count);
            Assert.Equal(0, Opens);
            Assert.Empty(_keyboard.Calls());
        }

        [Fact]
        public void AClaim_SavesTheActiveProfile_ThenStoresThePictureInIt()
        {
            using var backend = Lit();

            // The active profile, what it holds, then the picture, each
            // write behind a read of the active profile.
            Assert.Equal(new[] { "CA", "CA", "CC 5", "CA", "CB 5" }, _keyboard.Calls());
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
            // What profile 5 held is on record before the write, and the
            // crash path can put it back.
            var backup = Assert.IsType<KeyboardProfileBackups.Backup>(KeyboardProfileBackups.Get(Product));
            Assert.Equal(5, backup.Profile);
            Assert.Equal(Original5(), backup.Contents);
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(Spectrum.Path, record.Path);
            Assert.True(record.Feature);
            var restore = Assert.Single(record.Frames);
            Assert.Equal("07 CB E0 00 05 01 01 01 06 01 0B", Hex(restore, 11));
            Assert.Empty(_keyboard.ForeignReads);
        }

        [Fact]
        public void TheFirstWrite_WaitsForTheBackupToReachTheSettingsFile()
        {
            KeyboardProfileBackups.ResetForTest();
            using var backend = Lit();
            Assert.DoesNotContain("CB 5", _keyboard.Calls());
            Assert.NotNull(KeyboardProfileBackups.Get(Product));

            KeyboardProfileBackups.SaveRegistry(out int version);
            KeyboardProfileBackups.Persisted(version);
            Advance(LenovoLegionLighting.SavingMs);
            Passes(backend);
            Assert.Equal("CB 5", _keyboard.Calls().Last());
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
        }

        [Fact]
        public void AKeyFrame_ColorsItsKeyOverTheClaimsColor()
        {
            var keys = new KeyFrame(new Dictionary<ushort, int> { [0x29] = 0xFF0000 }, null, null, null);
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x000010, keys);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);

            var lights = LenovoLegionLighting.Gen10Lights();
            var colors = Enumerable.Repeat(0x000010, lights.Count).ToArray();
            colors[0] = 0xFF0000;   // Escape
            Assert.Equal(LenovoLegionLighting.StaticFrame(5, lights, colors), _keyboard.Stored(5));
        }

        [Fact]
        public void APictureTheKeyboardHolds_IsNotWrittenAgain()
        {
            using var backend = Lit();
            int writes = Writes().Count;
            for (int i = 0; i < 10; i++)
            {
                Advance(1000);
                Passes(backend);
            }
            Assert.Equal(writes, Writes().Count);
            Assert.Equal(1, Opens);
        }

        [Fact]
        public void AChangedPicture_HoldsFirst_AndKeepsTheRepaintInterval()
        {
            using var backend = Lit();
            int writes = Writes().Count;

            Light(0x445566);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            // Held long enough, but not yet 500 ms since the last write.
            Assert.Equal(writes, Writes().Count);

            Advance(LenovoLegionLighting.RepaintMs - LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.Equal(writes + 1, Writes().Count);
            Assert.Equal(Picture(5, 0x445566), _keyboard.Stored(5));
        }

        [Fact]
        public void APictureThatNeverHolds_IsNeverWritten()
        {
            using var backend = Lit();
            int writes = Writes().Count;
            for (int i = 0; i < 20; i++)
            {
                Light(0x010101 * (i + 2));
                Passes(backend);
                Advance(LenovoLegionLighting.HoldMs - 50);
            }
            Assert.Equal(writes, Writes().Count);

            // The last one holds.
            Advance(LenovoLegionLighting.RepaintMs);
            Passes(backend);
            Assert.Equal(writes + 1, Writes().Count);
        }

        [Fact]
        public void TheClaimsEnd_PutsTheProfileBack_ChecksIt_AndLetsTheBackupGo()
        {
            using var backend = Lit();
            int before = _keyboard.Calls().Count;

            Unlight();
            Passes(backend);
            // The active profile, what it holds (a picture of PadForge's),
            // the restore, and the read that checks it.
            Assert.Equal(new[] { "CA", "CA", "CC 5", "CB 5", "CA", "CC 5" }, _keyboard.Calls().Skip(before));
            var answer = LenovoLegionLightingTests.OwnersProfile5Answer();
            answer[1] = LenovoLegionLighting.SaveProfile;
            Assert.Equal(answer, _keyboard.Stored(5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
            Assert.True(Channel().Disposed);
            Assert.Empty(backend.Recorded);
            Assert.Empty(_keyboard.ForeignReads);
        }

        [Fact]
        public void AProfileKey_StopsTheWrites_UntilTheKeyboardIsBackOnItsProfile()
        {
            using var backend = Lit();
            // Fn+Space: the keyboard is on profile 4 now.
            _keyboard.Active = 4;
            Light(0x445566);
            Advance(LenovoLegionLighting.RepaintMs);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.DoesNotContain("CB 4", _keyboard.Calls());
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
            // The crash path cannot switch, so it holds nothing meanwhile.
            Assert.Empty(backend.Recorded);

            _keyboard.Active = 5;
            Advance(LenovoLegionLighting.AwayMs);
            Passes(backend);
            Assert.Equal(Picture(5, 0x445566), _keyboard.Stored(5));
            Assert.Single(backend.Recorded);
            Assert.Empty(_keyboard.ForeignReads);
        }

        [Fact]
        public void AHandBackFromAnotherProfile_SwitchesToItsOwn_PutsItBack_AndSwitchesBack()
        {
            using var backend = Lit();
            _keyboard.Active = 4;
            int before = _keyboard.Calls().Count;

            Unlight();
            Passes(backend);
            Assert.Equal(new[] { "CA", "C8 5", "CA", "CA", "CC 5", "CB 5", "CA", "CC 5", "C8 4", "CA" },
                _keyboard.Calls().Skip(before));
            Assert.Equal(4, _keyboard.Active);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
            Assert.Empty(_keyboard.ForeignReads);
        }

        private static byte[] Answer(byte[] stored)
        {
            var answer = (byte[])stored.Clone();
            answer[1] = LenovoLegionLighting.GetProfile;
            return answer;
        }

        [Fact]
        public void AnotherProgramsWriteMeanwhile_IsLeftAsItIs()
        {
            using var backend = Lit();
            // Lenovo's own software stores an effect in profile 5.
            var theirs = LenovoLegionLightingTests.OwnersProfile4Answer();
            theirs[1] = LenovoLegionLighting.SaveProfile;
            theirs[4] = 5;
            _keyboard.Store(5, theirs);
            int before = Writes().Count;

            Unlight();
            Passes(backend);
            Assert.Equal(before, Writes().Count);
            Assert.Equal(theirs, _keyboard.Stored(5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }

        [Fact]
        public void AKilledRunsPicture_IsPutBackByTheNextRuns_HandBack()
        {
            // A run was killed with its picture in profile 5 and its backup
            // on disk.
            KeyboardProfileBackups.LoadRegistry(new[] { (Product, 5, Convert.ToHexString(Original5())) });
            _keyboard.Store(5, Picture(5, 0x00FF00));
            Link(PathOf(Spectrum));
            Light(0x0000FF);

            using var backend = Started(Spectrum);
            // The backup stands for what the profile holds, and the crash
            // path can put it back from the start.
            Assert.Equal(new[] { "CA", "CA", "CC 5" }, _keyboard.Calls());
            var restore = Assert.Single(Assert.Single(backend.Recorded).Frames);
            Assert.Equal(Original5(), restore.AsSpan(2, Original5().Length).ToArray());
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.Equal(Original5(), KeyboardProfileBackups.Get(Product)?.Contents);
            Assert.Equal(Picture(5, 0x0000FF), _keyboard.Stored(5));

            Unlight();
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }

        [Fact]
        public void AKilledRunsPicture_IsPutBack_WithNoClaim()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (Product, 5, Convert.ToHexString(Original5())) });
            _keyboard.Store(5, Picture(5, 0x00FF00));

            using var backend = Started(Spectrum);
            // This run's claims resolve first.
            Assert.Empty(_keyboard.Calls());
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
            int opens = Opens;
            Assert.True(Channel().Disposed);

            // Nothing owed any more: nothing opened again.
            Advance(10_000);
            Passes(backend);
            Assert.Equal(opens, Opens);
        }

        [Fact]
        public void AKilledRunsPictureOnAnotherProfile_GoesBackFirst()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (Product, 5, Convert.ToHexString(Original5())) });
            _keyboard.Store(5, Picture(5, 0x00FF00));
            _keyboard.Active = 4;
            Link(PathOf(Spectrum));
            Light(0x0000FF);

            using var backend = Started(Spectrum);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            var calls = _keyboard.Calls();
            Assert.Equal(new[] { "CA", "C8 5", "CA", "CA", "CC 5", "CB 5", "CA", "CC 5", "C8 4", "CA" },
                calls.Take(10));
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            // Then the session holds profile 4, which the user chose.
            Assert.Equal(Picture(4, 0x0000FF), _keyboard.Stored(4));
            Assert.Equal((byte?)4, KeyboardProfileBackups.Get(Product)?.Profile);
            Assert.Empty(_keyboard.ForeignReads);
        }

        [Fact]
        public void ABackupWhoseProfileAnotherProgramWroteSince_IsDropped()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { (Product, 4, Convert.ToHexString(Original5())) });
            using var backend = Started(Spectrum);
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Null(KeyboardProfileBackups.Get(Product));
            // Switched to profile 4 to read it, and back to 5.
            Assert.Equal(5, _keyboard.Active);
            Assert.DoesNotContain(Writes(), w => w.StartsWith("CB"));
        }

        [Fact]
        public void AProfileKey_TakesTheRestoreOffTheCrashPathsRecord_WithinARecheck()
        {
            using var backend = Lit();
            Assert.Single(backend.Recorded);

            _keyboard.Active = 4;
            Advance(LenovoLegionLighting.RecheckMs);
            Passes(backend);
            Assert.Empty(backend.Recorded);
            Assert.DoesNotContain(Writes(), w => w.StartsWith("CB 4"));

            _keyboard.Active = 5;
            Advance(LenovoLegionLighting.RecheckMs);
            Passes(backend);
            Assert.Single(backend.Recorded);
            Assert.Empty(_keyboard.ForeignReads);
        }

        [Fact]
        public void TheClaimsEnd_PutsTheProfileBack_WhenAnswersComeLate()
        {
            // The check after the restore must not pass on the answer to the
            // read before it.
            _keyboard.LateAnswers = true;
            using var backend = Lit();
            Unlight();
            Passes(backend);
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
            Assert.True(Channel().Disposed);
        }

        [Fact]
        public void LateAnswers_AreWaitedFor()
        {
            _keyboard.LateAnswers = true;
            using var backend = Lit();
            Assert.Equal(Original5(), KeyboardProfileBackups.Get(Product)?.Contents);
            Assert.Equal(Picture(5, 0x112233), _keyboard.Stored(5));
        }

        [Fact]
        public void AProfileThatIsNotAnswered_ClaimsNothing_AndListsTheUnitAsleep()
        {
            _keyboard.Mute = true;
            using var backend = Lit();
            Assert.Equal(new[] { "CA", "CA", "CA" }, _keyboard.Calls());
            Assert.True(Channel().Disposed);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Empty(backend.Recorded);
            Assert.Null(KeyboardProfileBackups.Get(Product));
        }

        [Fact]
        public void AFailedWrite_ListsTheUnitAsleep_AndTheBackupWaitsForTheNextOpen()
        {
            using var backend = Lit();
            Channel().FailWrites = true;
            Light(0x445566);
            Advance(LenovoLegionLighting.RepaintMs);
            Passes(backend);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.NotNull(KeyboardProfileBackups.Get(Product));

            Advance(HidLightingBackend.RetryMs);
            Passes(backend, 3);
            Advance(LenovoLegionLighting.HoldMs);
            Passes(backend);
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
            // The new session found its own picture in profile 5 and keeps
            // the original it saved.
            Assert.Equal(Original5(), KeyboardProfileBackups.Get(Product)?.Contents);
            Assert.Equal(Picture(5, 0x445566), _keyboard.Stored(5));
        }

        [Fact]
        public void Stopping_PutsTheProfileBack_AndTakesTheLightsDown()
        {
            var backend = Lit();
            backend.Stop();
            Assert.Equal(Original5(), LenovoLegionLighting.ContentsOf(Answer(_keyboard.Stored(5)), 5));
            Assert.Null(KeyboardProfileBackups.Get(Product));
            Assert.Null(PeripheralLightMaps.For(PathOf(Spectrum)));
            backend.Dispose();
        }

        [Fact]
        public void TheCrashPath_PutsTheProfileBackThroughAFeatureReportOfItsOwn()
        {
            var backend = Lit();
            // The worker cannot reach the keyboard on its own channel.
            Channel().FailWrites = true;

            backend.ReleaseClaimedNow(Environment.TickCount64 + 2000);
            backend.Dispose();
            var original = LenovoLegionLightingTests.OwnersProfile5Answer();
            original[1] = LenovoLegionLighting.SaveProfile;
            lock (_oneShots)
                Assert.Contains(_oneShots, shot => shot.Path == Spectrum.Path && shot.Frame.SequenceEqual(original));
        }
    }

    /// <summary>The settings file carries a keyboard profile's backup, and a
    /// backup counts as saved only once a save wrote it (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class KeyboardProfileBackupsTests : IDisposable
    {
        public KeyboardProfileBackupsTests() => KeyboardProfileBackups.ResetForTest();

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        [Fact]
        public void ABackup_IsSavedOnlyOnceASaveWroteIt()
        {
            int raised = 0;
            EventHandler count = (_, _) => Interlocked.Increment(ref raised);
            KeyboardProfileBackups.Changed += count;
            try
            {
                KeyboardProfileBackups.Take("lenovo:048D:C197", 5, new byte[] { 0xE0, 0x00, 0x05, 0x01, 0x01 });
                Assert.Equal(1, raised);
                Assert.False(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));

                var saved = KeyboardProfileBackups.SaveRegistry(out int version);
                Assert.Equal(("lenovo:048D:C197", 5, "E000050101"), Assert.Single(saved));
                KeyboardProfileBackups.Persisted(version);
                Assert.True(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));

                // A newer backup waits for its own save.
                KeyboardProfileBackups.Take("lenovo:048D:C197", 4, new byte[] { 0x03, 0x00, 0x04, 0x01, 0x01 });
                Assert.False(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));
                KeyboardProfileBackups.Persisted(version);
                Assert.False(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));
            }
            finally
            {
                KeyboardProfileBackups.Changed -= count;
            }
        }

        [Fact]
        public void TheFilesBackups_LoadAsSaved_AndBadOnesAreSkipped()
        {
            KeyboardProfileBackups.LoadRegistry(new[]
            {
                ("lenovo:048D:C197", 5, "E000050101"),
                ("lenovo:048D:C999", 5, "not hex"),
                ("lenovo:048D:C998", 300, "E000050101"),
                ("", 5, "E000050101"),
            });
            var backup = KeyboardProfileBackups.Get("lenovo:048D:C197");
            Assert.Equal((byte?)5, backup?.Profile);
            Assert.Equal(new byte[] { 0xE0, 0x00, 0x05, 0x01, 0x01 }, backup?.Contents);
            Assert.True(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));
            Assert.Null(KeyboardProfileBackups.Get("lenovo:048D:C999"));
            Assert.Null(KeyboardProfileBackups.Get("lenovo:048D:C998"));
            Assert.Single(KeyboardProfileBackups.SaveRegistry(out _));
        }

        [Fact]
        public void AFileThatNamesNone_LeavesTheBackupsHeld()
        {
            // A reset to defaults loads a fresh AppSettingsData.
            KeyboardProfileBackups.Take("lenovo:048D:C197", 5, new byte[] { 0xE0, 0x00, 0x05, 0x01, 0x01 });
            KeyboardProfileBackups.LoadRegistry(new AppSettingsData().KeyboardProfileBackups?.Select(b => (b.Product, b.Profile, b.Contents)));
            Assert.Equal((byte?)5, KeyboardProfileBackups.Get("lenovo:048D:C197")?.Profile);
            // Not on disk yet, so still not saved.
            Assert.False(KeyboardProfileBackups.IsPersisted("lenovo:048D:C197"));

            // A file that names backups is the record.
            KeyboardProfileBackups.LoadRegistry(Array.Empty<(string, int, string)>());
            Assert.Null(KeyboardProfileBackups.Get("lenovo:048D:C197"));
        }

        [Fact]
        public void TheSettingsFile_HoldsTheBackups()
        {
            var data = new AppSettingsData
            {
                KeyboardProfileBackups = new[]
                {
                    new KeyboardProfileBackupData { Product = "lenovo:048D:C197", Profile = 5, Contents = "E000050101" },
                },
            };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(AppSettingsData));
            using var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, data);
            string xml = writer.ToString();
            Assert.Contains("<KeyboardProfileBackups>", xml);
            Assert.Contains("<Backup Product=\"lenovo:048D:C197\" Profile=\"5\" Contents=\"E000050101\" />", xml);
            using var reader = new System.IO.StringReader(xml);
            var back = (AppSettingsData)serializer.Deserialize(reader);
            var only = Assert.Single(back.KeyboardProfileBackups);
            Assert.Equal(("lenovo:048D:C197", 5, "E000050101"), (only.Product, only.Profile, only.Contents));
        }

        [Fact]
        public void TheSave_MarksTheBackupsItWrote_AsOnDisk()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "SettingsService.cs");
            int write = source.IndexOf("File.WriteAllBytes(filePath, serializedBytes);", StringComparison.Ordinal);
            int mark = source.IndexOf("KeyboardProfileBackups.Persisted(_profileBackupsBuilt);", StringComparison.Ordinal);
            Assert.True(write > 0 && mark > write, "the backups count as saved only after the file is written");
            Assert.Contains(".SaveRegistry(out _profileBackupsBuilt)", source);
            Assert.Contains("KeyboardProfileBackups.LoadRegistry(", source);
            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("KeyboardProfileBackups.Changed += OnKeyboardProfileBackupsChanged;", service);
            Assert.Contains("KeyboardProfileBackups.Changed -= OnKeyboardProfileBackupsChanged;", service);
        }
    }

    /// <summary>The linker's choice for a Lenovo Legion keyboard and the
    /// output-only rows' surfaces (#557).</summary>
    public class HidLightingLinkTests
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");

        [Fact]
        public void TheKeyboardsOwnController_IsChosenOverALampArrayWindowsLights_WhereItStaysInCharge()
        {
            var native = new[] { new PeripheralLinker.Candidate(new OutputPath(OutputFamily.HidLightingUnit, "s"), 0, false) { BesideWindows = true } };
            var plain = new[] { new PeripheralLinker.Candidate(new OutputPath(OutputFamily.RazerUnit, "r"), 0, false) };
            var litLamp = new[] { new PeripheralLinker.Candidate(new OutputPath(OutputFamily.LampArrayUnit, "l"), 0, true) };
            Assert.Equal(PeripheralLinker.LightingChoice.Native,
                PeripheralLinker.ChooseLighting(false, Array.Empty<OutputPath>(), false, native, litLamp, out _));
            Assert.Equal(PeripheralLinker.LightingChoice.LampArray,
                PeripheralLinker.ChooseLighting(false, Array.Empty<OutputPath>(), false, plain, litLamp, out _));
        }

        [Fact]
        public void TheLegionsKeyboardRow_IsLitThroughItsController_WhileWindowsHoldsItsLampArray()
        {
            // The owner's Legion: the keyboard collection and the lighting
            // controller are 048D:C197, the LampArray interface 048D:C193,
            // all three in the PC's own container.
            var pc = PeripheralLinker.LocalMachineContainer;
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x048D, 0xC197, pc, null);
            var spectrum = new DirectUnit(OutputFamily.HidLightingUnit, "spectrum", pc, 0x048D, 0xC197) { LitBesideWindows = true };
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", pc, 0x048D, 0xC193)
            {
                Kind = PeripheralLinker.LampArrayKindKeyboard, WindowsLit = true,
            };
            var inputs = new LinkInputs(HidppSnapshot.Empty, new PeripheralPresence(false, false, false, false, false))
            {
                Units = new[] { spectrum, lamp },
            };
            var links = PeripheralLinker.Build(new[] { row }, inputs).For(A);
            Assert.Equal(new[] { spectrum.Path }, links.Lighting);
            // The LampArray interface stays reached by the keyboard row, so
            // it gets no row of its own.
            Assert.Empty(PeripheralLinker.Orphans(new[] { row }, inputs));
        }

        [Fact]
        public void OnlyTheLightingAndRumbleRows_TakeOutputAlone()
        {
            Assert.True(InputDeviceType.IsOutputOnly(InputDeviceType.PeripheralLighting));
            Assert.True(InputDeviceType.IsOutputOnly(InputDeviceType.PeripheralHaptics));
            Assert.False(InputDeviceType.IsOutputOnly(InputDeviceType.Keyboard));
            Assert.False(InputDeviceType.IsOutputOnly(InputDeviceType.Gamepad));
            Assert.False(InputDeviceType.IsOutputOnly(InputDeviceType.Microphone));
        }

        [Fact]
        public void AnOutputOnlyRow_ShowsNoRawInputState_AndNoRuleBeforeIt()
        {
            var lighting = new DeviceRowViewModel { DeviceTypeKey = "PeripheralLighting" };
            Assert.False(lighting.ShowRawInputState);
            Assert.False(lighting.ShowRawInputDivider);
            var haptics = new DeviceRowViewModel { DeviceTypeKey = "PeripheralHaptics" };
            Assert.False(haptics.ShowRawInputState);
            var keyboard = new DeviceRowViewModel { DeviceTypeKey = "Keyboard" };
            Assert.True(keyboard.ShowRawInputState);
        }
    }
}
