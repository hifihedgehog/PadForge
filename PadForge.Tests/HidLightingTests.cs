using System;
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
    /// LenovoUSBController_Gen7_8.cpp, its lights against LenovoDevices.h,
    /// and the answer the owner's Legion Pro 7 16AFR10H gave to
    /// GET_ACTIVE_PROFILE (07 CA 01 00 05).
    /// </summary>
    public class LenovoLegionLightingTests
    {
        private static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, count).Replace('-', ' ');

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
        public void DirectMode_GoesOnAndOffOnTheProfileRead()
        {
            // {REPORT_ID, SET_DIRECT_MODE, 0xC0, 0x03, 0x01 or 0x02, profile}
            // with the length 2 in bytes 2 and 3 (lines 134-166).
            Assert.Equal("07 D0 02 00 01 05 00", Hex(LenovoLegionLighting.Direct(on: true, 5), 7));
            Assert.Equal("07 D0 02 00 02 05 00", Hex(LenovoLegionLighting.Direct(on: false, 5), 7));
            Assert.Equal(960, LenovoLegionLighting.Direct(on: true, 5).Length);
        }

        [Fact]
        public void TheColorFrame_CarriesEachLightsAddressLowByteFirst_ThenItsColor()
        {
            var lights = new[]
            {
                new LightInfo(0x0001, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(0x03E9, LightKind.Edge, 0, LightKeySource.None),
            };
            var frame = LenovoLegionLighting.Colors(lights, new[] { 0x112233, 0xAABBCC });
            // {REPORT_ID, DIRECT_MODE, length} then value & 0xFF, value >> 8,
            // R, G, B per light (lines 179-196), length 2 x 5.
            Assert.Equal("07 A1 0A 00 01 00 11 22 33 E9 03 AA BB CC 00", Hex(frame, 15));
        }

        [Fact]
        public void TheColorFrame_HoldsEveryLightOfTheGen10Board()
        {
            var lights = LenovoLegionLighting.Gen10Lights();
            Assert.True(lights.Count <= LenovoLegionLighting.MaxLightsPerFrame);
            var frame = LenovoLegionLighting.Colors(lights, Enumerable.Repeat(0x010203, lights.Count).ToArray());
            int length = frame[2] | frame[3] << 8;
            Assert.Equal(lights.Count * 5, length);
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
            // The keyboard collection of the same device, the controller's
            // other vendor collections, the 24-zone board OpenRGB gives no
            // Direct mode, and a short feature report.
            Assert.Null(HidLightingFamilies.Match(Collection(page: 0x0001, usage: 0x06)));
            Assert.Null(HidLightingFamilies.Match(Collection(usage: 0x10)));
            Assert.Null(HidLightingFamilies.Match(Collection(pid: 0xC195)));
            Assert.Null(HidLightingFamilies.Match(Collection(feature: 17)));
        }
    }

    /// <summary>
    /// The shared HID lighting worker against scripted report channels
    /// (#557), with the Lenovo family: what it opens and when, the frames it
    /// sends, its re-assert and repaint intervals, its hand-back and what it
    /// owes a device that stopped answering. The bench is the SteelSeries
    /// worker's: the worker reads a clock the test sets, and a test changes
    /// one thing and then waits for two passes.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class HidLightingBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");
        private const string Answer = "07 CA 01 00 05";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly List<FakeReportChannel> _channels = new();
        private readonly List<(string Path, string Frame, int TimeoutMs)> _oneShots = new();
        private volatile Func<byte, byte[]> _feature = _ => Bytes(Answer);
        private long _tick = 1_000_000;
        private int _opens;

        public HidLightingBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private static byte[] Bytes(string hex) => hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();
        private static string Hex(byte[] bytes, int count) => BitConverter.ToString(bytes, 0, Math.Min(count, bytes.Length)).Replace('-', ' ');

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);
        private int Opens => Volatile.Read(ref _opens);

        private IHidReportChannel Open(string path)
        {
            Interlocked.Increment(ref _opens);
            var channel = new FakeReportChannel
            {
                Path = path, InputReportLength = 0, OutputReportLength = 0, FeatureReportLength = 960, Ticks = Tick,
                Feature = id => _feature(id),
            };
            lock (_channels) _channels.Add(channel);
            return channel;
        }

        private FakeReportChannel Channel(int index = -1)
        {
            lock (_channels) return index < 0 ? _channels[^1] : _channels[index];
        }

        private HidLightingBackend Started(params HidCollectionInfo[] present)
        {
            var backend = new HidLightingBackend(null, Open, _ => Box, Tick,
                (path, frame, timeoutMs) =>
                {
                    lock (_oneShots) _oneShots.Add((path, Hex(frame, 6), timeoutMs));
                    return true;
                },
                (_, _, _) => false, activeTickMs: 1, idleTickMs: 5);
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

        /// <summary>The calls a channel took: "set" with the first bytes of
        /// the report, or "get" with the report ID.</summary>
        private static List<string> Calls(FakeReportChannel channel)
            => channel.Calls.Select(c => c.Kind + " " + Hex(c.Bytes, c.Kind == "set" ? 6 : 1)).ToList();

        private static List<byte[]> Sets(FakeReportChannel channel)
            => channel.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

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
        }

        [Fact]
        public void AClaim_ReadsTheProfile_ThenEntersDirectModeAndPaintsEveryLight()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);

            var channel = Channel();
            Assert.Equal(new[] { "set 07 CA 01 00 00 00", "get 07", "set 07 D0 02 00 01 05", "set 07 A1 8A 02 01 00" },
                Calls(channel));
            var colors = Sets(channel)[2];
            // Escape first, then every light in the map's order, each in the
            // claim's color.
            Assert.Equal("01 00 11 22 33", BitConverter.ToString(colors, 4, 5).Replace('-', ' '));
            Assert.Equal("FA 03 11 22 33", BitConverter.ToString(colors, 4 + 129 * 5, 5).Replace('-', ' '));
            // The hand-back was on record before the first frame went out.
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(Spectrum.Path, record.Path);
            Assert.True(record.Feature);
            Assert.Equal("07 D0 02 00 02 05", Hex(Assert.Single(record.Frames), 6));
        }

        [Fact]
        public void AKeyFrame_ColorsItsKeyOverTheClaimsColor()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            var keys = new KeyFrame(new Dictionary<ushort, int> { [0x29] = 0xFF0000 }, null, null, null);
            Light(0x000010, keys);
            Passes(backend);

            var colors = Sets(Channel())[2];
            Assert.Equal("01 00 FF 00 00", BitConverter.ToString(colors, 4, 5).Replace('-', ' '));
            Assert.Equal("02 00 00 00 10", BitConverter.ToString(colors, 9, 5).Replace('-', ' '));
        }

        [Fact]
        public void AnUnchangedPicture_GoesOutAgainOnTheReassertInterval()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var channel = Channel();
            Assert.Equal(4, channel.Calls.Count);

            Advance(LenovoLegionLighting.ReassertMs - 1);
            Passes(backend);
            Assert.Equal(4, channel.Calls.Count);

            Advance(1);
            Passes(backend);
            Assert.Equal(new[] { "set 07 D0 02 00 01 05", "set 07 A1 8A 02 01 00" }, Calls(channel).Skip(4));
            Assert.Equal(1, Opens);
        }

        [Fact]
        public void AChangedColor_WaitsForTheRepaintInterval()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var channel = Channel();

            Advance(10);
            Light(0x445566);
            Passes(backend);
            Assert.Equal(4, channel.Calls.Count);

            Advance(LenovoLegionLighting.RepaintMs - 10);
            Passes(backend);
            var sets = Sets(channel);
            Assert.Equal(5, sets.Count);
            Assert.Equal("01 00 44 55 66", BitConverter.ToString(sets[4], 4, 5).Replace('-', ' '));
        }

        [Fact]
        public void TheClaimsEnd_HandsBackOnTheSameProfile_AndCloses()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var channel = Channel();

            Unlight();
            Passes(backend);
            Assert.Equal("set 07 D0 02 00 02 05", Calls(channel).Last());
            Assert.True(channel.Disposed);
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AProfileThatIsNotAnswered_ClaimsNothing_AndListsTheUnitAsleep()
        {
            _feature = _ => Bytes("07 CD 01 00 05");
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);

            Assert.Equal(new[] { "set 07 CA 01 00 00 00", "get 07" }, Calls(Channel()));
            Assert.True(Channel().Disposed);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AFailedPaint_OwesTheHandBack_WhichGoesOutFirstWhenTheUnitAnswers()
        {
            using var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var first = Channel();

            first.FailWrites = true;
            Advance(LenovoLegionLighting.RepaintMs);
            Light(0x445566);
            Passes(backend);
            Assert.True(Assert.Single(backend.Snapshot.Units).Asleep);
            // Still on record while it is owed.
            Assert.Single(backend.Recorded);

            Advance(HidLightingBackend.RetryMs);
            Passes(backend, 3);
            // The try that finds the unit opens it and sends the hand-back
            // alone, then a new open claims it again.
            Assert.Equal(new[] { "set 07 D0 02 00 02 05" }, Calls(Channel(1)));
            Assert.Equal(new[] { "set 07 CA 01 00 00 00", "get 07", "set 07 D0 02 00 01 05", "set 07 A1 8A 02 01 00" },
                Calls(Channel(2)));
            Assert.False(Assert.Single(backend.Snapshot.Units).Asleep);
        }

        [Fact]
        public void Stopping_HandsBackWhatItHolds_AndTakesTheLightsDown()
        {
            var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var channel = Channel();

            backend.Stop();
            Assert.Equal("set 07 D0 02 00 02 05", Calls(channel).Last());
            Assert.Null(PeripheralLightMaps.For(PathOf(Spectrum)));
            backend.Dispose();
        }

        [Fact]
        public void TheCrashPath_HandsBackThroughAFeatureReportOfItsOwn()
        {
            var backend = Started(Spectrum);
            Link(PathOf(Spectrum));
            Light(0x112233);
            Passes(backend);
            var channel = Channel();
            // The worker cannot get the hand-back out on its own channel.
            channel.FailWrites = true;

            backend.ReleaseClaimedNow(Environment.TickCount64 + 2000);
            backend.Dispose();
            lock (_oneShots)
                Assert.Contains(_oneShots, shot => shot.Path == Spectrum.Path && shot.Frame == "07 D0 02 00 02 05");
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
