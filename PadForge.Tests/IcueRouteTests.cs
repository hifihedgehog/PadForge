using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the iCUE route knows of Corsair's iCUE SDK v4 (#557): the LED id
    /// table, the light map of a type, the colors a device takes, the struct
    /// layouts, a row's type and the link pass's line. Expected values are
    /// written out from cue-sdk 890bc3e (src/include/iCUESDK.h,
    /// iCUESDKLedIdEnum.h, docs/index.html) and HID Usage Tables 1.6,
    /// section 10.
    /// </summary>
    public class IcueRouteTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000c1");
        private static readonly Guid Box = new("44444444-5555-6666-7777-888888888888");

        // ── The LED id table ──

        /// <summary>Entries of CorsairLedId_Keyboard (iCUESDKLedIdEnum.h),
        /// each with the HID usage of its key or the label of its light.</summary>
        [Theory]
        [InlineData(1, 0x29, LightLabel.None, "CLK_Escape")]
        [InlineData(2, 0x3A, LightLabel.None, "CLK_F1")]
        [InlineData(13, 0x45, LightLabel.None, "CLK_F12")]
        [InlineData(14, 0x35, LightLabel.None, "CLK_GraveAccentAndTilde")]
        [InlineData(15, 0x1E, LightLabel.None, "CLK_1")]
        [InlineData(24, 0x27, LightLabel.None, "CLK_0")]
        [InlineData(30, 0x1A, LightLabel.None, "CLK_W")]
        [InlineData(53, 0x31, LightLabel.None, "CLK_Backslash")]
        [InlineData(56, 0x64, LightLabel.None, "CLK_NonUsBackslash")]
        [InlineData(69, 0xE3, LightLabel.None, "CLK_LeftGui")]
        [InlineData(73, 0xE7, LightLabel.None, "CLK_RightGui")]
        [InlineData(74, 0x65, LightLabel.None, "CLK_Application")]
        [InlineData(77, 0x90, LightLabel.None, "CLK_Lang1")]
        [InlineData(79, 0x87, LightLabel.None, "CLK_International1")]
        [InlineData(83, 0x8B, LightLabel.None, "CLK_International5")]
        [InlineData(93, 0x52, LightLabel.None, "CLK_UpArrow")]
        [InlineData(96, 0x4F, LightLabel.None, "CLK_RightArrow")]
        [InlineData(97, 0x32, LightLabel.None, "CLK_NonUsTilde")]
        [InlineData(98, 0, LightLabel.Brightness, "CLK_Brightness")]
        [InlineData(99, 0, LightLabel.None, "CLK_WinLock")]
        [InlineData(100, 0, LightLabel.Mute, "CLK_Mute")]
        [InlineData(102, 0, LightLabel.Previous, "CLK_ScanPreviousTrack")]
        [InlineData(103, 0, LightLabel.PlayPause, "CLK_PlayPause")]
        [InlineData(104, 0, LightLabel.Next, "CLK_ScanNextTrack")]
        [InlineData(109, 0x5F, LightLabel.None, "CLK_Keypad7")]
        [InlineData(116, 0x59, LightLabel.None, "CLK_Keypad1")]
        [InlineData(119, 0x85, LightLabel.None, "CLK_KeypadComma")]
        [InlineData(120, 0x58, LightLabel.None, "CLK_KeypadEnter")]
        [InlineData(121, 0x62, LightLabel.None, "CLK_Keypad0")]
        [InlineData(122, 0x63, LightLabel.None, "CLK_KeypadPeriodAndDelete")]
        [InlineData(126, 0, LightLabel.None, "CLK_M1")]
        [InlineData(129, 0, LightLabel.Fn, "CLK_Fn")]
        public void TheKeyboardTable_HoldsTheHeadersIndex_AndTheKeysUsage(int index, int usage, object label, string name)
        {
            var entry = Assert.Single(IcueLeds.KeyboardKeys, k => k.Index == index);
            Assert.Equal((ushort)usage, entry.Usage);
            Assert.Equal((LightLabel)label, entry.Label);
            Assert.Equal(name, entry.Name);
        }

        /// <summary>CLK_Escape = 1 to CLK_Fn = 129, CLK_Invalid left out,
        /// each index once, and the groups of CorsairLedGroup.</summary>
        [Fact]
        public void TheKeyboardTable_HasEveryIndexOfTheHeader_Once()
        {
            Assert.Equal(Enumerable.Range(1, 129), IcueLeds.KeyboardKeys.Select(k => k.Index));
            Assert.Equal(0, IcueLeds.GroupKeyboard);
            Assert.Equal(1, IcueLeds.GroupKeyboardGKeys);
            Assert.Equal(2, IcueLeds.GroupKeyboardEdge);
            Assert.Equal(3, IcueLeds.GroupKeyboardOem);
            Assert.Equal(4, IcueLeds.GroupMouse);
            Assert.Equal(5, IcueLeds.GroupMousemat);
            Assert.Equal(7, IcueLeds.GroupHeadsetStand);
            Assert.Equal("890bc3e27e651f19ff73eff87bfa6497bb93dac5", IcueLeds.SdkCommit);
        }

        // ── The light map ──

        private static IcueLedPosition Led(int group, int index, double x = 0, double y = 0)
            => new((uint)(group << 16 | index), x, y);

        /// <summary>The docs' own examples (docs/index.html, "LED
        /// identification"): 0x00000005 is F4 in the keyboard group,
        /// 0x00010001 the G1 key, 0x00060001 the headset's first LED.</summary>
        [Fact]
        public void ALedId_IsItsGroupAndItsIndex_AsTheDocsExamplesHaveIt()
        {
            var f4 = IcueRoute.LightOf(new IcueLedPosition(0x00000005, 12.5, 3.25));
            Assert.Equal(LightKind.Key, f4.Kind);
            Assert.Equal((ushort)0x3D, f4.Usage);
            Assert.Equal(LightKeySource.Vendor, f4.KeySource);
            Assert.Equal(5, f4.Address);
            Assert.True(f4.HasPosition);
            Assert.Equal((12.5f, 3.25f), (f4.X, f4.Y));

            var g1 = IcueRoute.LightOf(new IcueLedPosition(0x00010001, 0, 0));
            Assert.Equal(LightKind.Other, g1.Kind);
            Assert.Equal(LightLabel.GKey, g1.Label);
            Assert.Equal(1, g1.LabelNumber);
            Assert.Equal(0x00010001, g1.Address);

            var headset = IcueRoute.LightOf(new IcueLedPosition(0x00060001, 0, 0));
            Assert.Equal(LightKind.Zone, headset.Kind);
        }

        [Fact]
        public void TheEdgeGroup_IsEdgeLights_TheVendorGroupOtherLights_AndAnUnknownKeyNamesNoKey()
        {
            Assert.Equal(LightKind.Edge, IcueRoute.LightOf(Led(2, 3)).Kind);
            var oem = IcueRoute.LightOf(Led(3, 1));
            Assert.Equal(LightKind.Other, oem.Kind);
            Assert.Equal(LightLabel.None, oem.Label);
            var media = IcueRoute.LightOf(Led(0, 103));
            Assert.Equal(LightKind.Other, media.Kind);
            Assert.Equal(LightLabel.PlayPause, media.Label);
            // An index past CLK_Fn: a key light no source named.
            var unknown = IcueRoute.LightOf(Led(0, 200));
            Assert.Equal(LightKind.Key, unknown.Kind);
            Assert.Equal(0, unknown.Usage);
            Assert.Equal(LightKeySource.None, unknown.KeySource);
        }

        /// <summary>Two devices of a type give one map: each LED once, in id
        /// order, the first device's place kept, zones numbered from 1, and
        /// an invalid index (0) left out.</summary>
        [Fact]
        public void AMap_IsEveryLedOfTheTypesDevices_OnceEach_InIdOrder()
        {
            var first = new[] { Led(4, 2, 10, 20), Led(4, 1, 5, 6), Led(4, 0) };
            var second = new[] { Led(4, 3, 30, 40), Led(4, 1, 99, 99) };
            var map = IcueRoute.Map("mouse", new IReadOnlyList<IcueLedPosition>[] { first, second, null });
            Assert.Equal(new[] { 0x40001, 0x40002, 0x40003 }, map.Lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3 }, map.Lights.Select(l => l.Zone));
            Assert.Equal((5f, 6f), (map.Lights[0].X, map.Lights[0].Y));
            Assert.Equal("icue:mouse", map.Product);
            Assert.Null(IcueRoute.Map("mouse", new IReadOnlyList<IcueLedPosition>[] { Array.Empty<IcueLedPosition>() }));
        }

        /// <summary>A device's colors follow the order it reports its LEDs
        /// in, opaque, and a LED the map lacks takes the fill.</summary>
        [Fact]
        public void ADevicesColors_AreItsLedsInItsOrder_Opaque()
        {
            var map = IcueRoute.Map("keyboard", new IReadOnlyList<IcueLedPosition>[] { new[] { Led(0, 1), Led(0, 30) } });
            var leds = new[] { Led(0, 30), Led(0, 1), Led(0, 31) };
            var colors = IcueRoute.Colors(leds, map, new[] { 0x112233, 0x445566 }, 0x778899);
            Assert.Equal(new[]
            {
                new IcueLedColor(30, 0x44, 0x55, 0x66, 255),
                new IcueLedColor(1, 0x11, 0x22, 0x33, 255),
                new IcueLedColor(31, 0x77, 0x88, 0x99, 255),
            }, colors);
            Assert.Equal(new[] { new IcueLedColor(30, 0, 0, 0, 0), new IcueLedColor(1, 0, 0, 0, 0) },
                IcueRoute.Cleared(new[] { Led(0, 30), Led(0, 1) }));
        }

        // ── The SDK's layouts ──

        /// <summary>CorsairLedColor is an unsigned int and four bytes
        /// (iCUESDK.h:299-306), 8 bytes laid out in that order.</summary>
        [Fact]
        public void ALedColor_IsLaidOutAsTheHeaderHasIt()
        {
            Assert.Equal(8, Marshal.SizeOf<IcueLedColor>());
            var color = new IcueLedColor(0x00010203, 0x10, 0x20, 0x30, 0x40);
            var bytes = new byte[8];
            var handle = GCHandle.Alloc(new[] { color }, GCHandleType.Pinned);
            try { Marshal.Copy(handle.AddrOfPinnedObject(), bytes, 0, 8); }
            finally { handle.Free(); }
            Assert.Equal(new byte[] { 0x03, 0x02, 0x01, 0x00, 0x10, 0x20, 0x30, 0x40 }, bytes);
        }

        /// <summary>CorsairDeviceInfo: an enum, three char arrays of
        /// CORSAIR_STRING_SIZE_M (128) and two ints (iCUESDK.h:10-17 and
        /// 212-220). CorsairLedPosition: an unsigned int, then two doubles on
        /// 8-byte bounds (222-227). The device id goes as a zero-ended
        /// buffer of 128.</summary>
        [Fact]
        public void TheNativeLayer_ReadsTheHeadersStructSizes()
        {
            Assert.Equal(396, IcueSdkNative.DeviceInfoSize);
            Assert.Equal(24, IcueSdkNative.LedPositionSize);
            Assert.Equal(128, IcueRoute.StringSizeM);
            Assert.Equal(64, IcueRoute.DeviceCountMax);
            Assert.Equal(512, IcueRoute.LedCountMax);
            var id = IcueSdkNative.Id("{abc}");
            Assert.Equal(128, id.Length);
            Assert.Equal(new byte[] { (byte)'{', (byte)'a', (byte)'b', (byte)'c', (byte)'}', 0 }, id.Take(6));
            Assert.Equal(127, IcueSdkNative.Id(new string('x', 300)).TakeWhile(b => b != 0).Count());
        }

        /// <summary>CorsairError, CorsairSessionState, CorsairAccessLevel and
        /// CorsairDeviceType (iCUESDK.h:22-62, 184-190).</summary>
        [Fact]
        public void TheSdksNumbers_AreTheHeaders()
        {
            Assert.Equal((0, 1, 6), (IcueRoute.Success, IcueRoute.NotConnected, IcueRoute.DeviceNotFound));
            Assert.Equal((5, 6), (IcueRoute.StateConnectionLost, IcueRoute.StateConnected));
            Assert.Equal(0, IcueRoute.AccessShared);
            Assert.Equal(new[] { ("keyboard", 1), ("mouse", 2), ("mousemat", 4), ("headsetstand", 0x10) }, IcueRoute.Types);
            Assert.Equal(0x17, IcueRoute.TypeMask);
            Assert.Equal("mouse", IcueRoute.KeyOf(2));
            Assert.Null(IcueRoute.KeyOf(8));
        }

        // ── A row's type ──

        [Theory]
        [InlineData(0x1B13, InputDeviceType.Keyboard, "keyboard")] // K70 RGB, V1 Keyboards
        [InlineData(0x1B34, InputDeviceType.Keyboard, "mouse")] // Glaive RGB, V1 Mice
        [InlineData(0x1B3B, InputDeviceType.Mouse, "mousemat")] // MM800, V1 Mousemats
        [InlineData(0x0A34, InputDeviceType.Mouse, "headsetstand")] // ST100, V1 Headset Stands
        [InlineData(0x1B4B, InputDeviceType.Keyboard, "mouse")] // Dark Core SE, V2
        [InlineData(0x1B6E, InputDeviceType.Mouse, "keyboard")] // K57 wired, V2
        [InlineData(0x1FFF, InputDeviceType.Keyboard, "keyboard")] // listed nowhere
        [InlineData(0x1FFF, InputDeviceType.Mouse, "mouse")]
        public void ARowsType_IsItsProducts_ElseItsOwnKind(int productId, int capType, string key)
            => Assert.Equal(key, IcueRoute.KeyOf(IcueRoute.RowType((ushort)productId, capType)));

        // ── The link pass ──

        private static (LinkRow Row, DirectUnit Unit) Corsair(ushort productId = 0x1B13, int capType = InputDeviceType.Keyboard)
        {
            var row = new LinkRow(A, capType, 0x1B1C, productId, Box, null);
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, "corsair", Box, 0x1B1C, productId)
            {
                Programs = new[] { VendorPrograms.CorsairIcue },
            };
            return (row, unit);
        }

        private static LinkInputs Inputs(bool sdk, IEnumerable<string> running, params DirectUnit[] units)
            => new(HidppSnapshot.Empty,
                PeripheralPresence.WithRunning(new PeripheralPresence(false, false, false, false, false) { CorsairIcueSdk = sdk }, running))
            {
                Units = units,
            };

        [Fact]
        public void WhileICueRuns_ACorsairRow_IsLitThroughItsTypesPath()
        {
            var (row, unit) = Corsair();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, new[] { "iCUE.exe" }, unit)).For(A);
            Assert.Equal(new[] { new OutputPath(OutputFamily.IcueDevice, "keyboard") }, links.Lighting);
            Assert.Null(links.LeftToProgram);
            Assert.True(links.DirectWhenClosed);
            Assert.True(links.HasVendorProgram);

            var (mouseRow, mouse) = Corsair(0x1B34, InputDeviceType.Mouse);
            var mouseLinks = PeripheralLinker.Build(new[] { mouseRow }, Inputs(true, new[] { "iCUE.exe" }, mouse)).For(A);
            Assert.Equal(new[] { new OutputPath(OutputFamily.IcueDevice, "mouse") }, mouseLinks.Lighting);
        }

        [Fact]
        public void WithoutTheSdksLibrary_TheRowIsLeftToICue()
        {
            var (row, unit) = Corsair();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(false, new[] { "iCUE.exe" }, unit)).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal("Corsair iCUE", links.LeftToProgram);
        }

        [Fact]
        public void WhileICueDoesNotRun_TheRowIsLitOnItsOwn()
        {
            var (row, unit) = Corsair();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, new[] { "explorer.exe" }, unit)).For(A);
            Assert.Equal(new[] { unit.Path }, links.Lighting);
            Assert.False(links.DirectWhenClosed);
        }

        /// <summary>A Lenovo Legion keyboard names iCUE among its programs
        /// (<see cref="VendorPrograms.LenovoLegionUsb"/>), and stays left to
        /// it: the route takes Corsair devices only.</summary>
        [Fact]
        public void ALenovoKeyboardThatNamesICue_IsNotLitThroughIt()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x048D, 0xC965, Box, null);
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, "legion", Box, 0x048D, 0xC965)
            {
                Programs = VendorPrograms.LenovoLegionUsb,
            };
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, new[] { "iCUE.exe" }, unit)).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal("Corsair iCUE", links.LeftToProgram);
        }

        [Fact]
        public void AnotherProgram_AddsNoICuePath()
        {
            var shared = new List<OutputPath>();
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x1B1C, 0x1B13, Box, null);
            var presence = new PeripheralPresence(false, false, false, false, false) { CorsairIcueSdk = true };
            IcueRoute.AddShared(shared, VendorPrograms.LenovoVantage, presence, row);
            IcueRoute.AddShared(shared, null, presence, row);
            Assert.Empty(shared);
            IcueRoute.AddShared(shared, VendorPrograms.CorsairIcue, presence, row);
            IcueRoute.AddShared(shared, VendorPrograms.CorsairIcue, presence, row);
            Assert.Equal(new[] { new OutputPath(OutputFamily.IcueDevice, "keyboard") }, shared);
        }

        /// <summary>The fallback to PadForge's own copy of the library is
        /// looked for where iCUE 5 installs itself, and the test host loads
        /// no library.</summary>
        [Fact]
        public void TheLibrary_IsLookedForInICuesFolder()
        {
            string expected = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Corsair", "Corsair iCUE5 Software", "iCUESDK.x64_2019.dll");
            Assert.Equal(new[] { expected }, IcueSdkNative.LibraryPaths());
            Assert.True(IcueSdkNative.OffLimitsForTest);
            var native = new IcueSdkNative();
            Assert.False(native.TryLoad(out string detail));
            Assert.Equal("the test host loads no vendor library", detail);
            Assert.Equal(IcueRoute.NotConnected, native.Connect());
        }
    }

    /// <summary>A scripted iCUE SDK: it records each call and answers from
    /// what the test gives it.</summary>
    internal sealed class FakeIcue : IIcueNative
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = new();
        public volatile int StateNow;
        public volatile bool Loads = true;
        public int ConnectResult = IcueRoute.Success;
        public int SetResult = IcueRoute.Success;
        public List<IcueDeviceInfo> Devices = new();
        public Dictionary<string, IcueLedPosition[]> Leds = new();
        public readonly List<(string Id, IcueLedColor[] Colors)> Sets = new();
        public int Unloads;

        public int State => StateNow;

        private void Log(string call)
        {
            lock (_gate) _calls.Add(call);
        }

        public List<string> Calls()
        {
            lock (_gate) return _calls.ToList();
        }

        public List<(string Id, IcueLedColor[] Colors)> SetsNow()
        {
            lock (_gate) return Sets.ToList();
        }

        public bool TryLoad(out string detail)
        {
            Log("load");
            detail = Loads ? "loaded" : "missing";
            return Loads;
        }

        /// <summary>What the first connect answers, before
        /// <see cref="ConnectResult"/>.</summary>
        public int? FirstConnect;

        public int Connect()
        {
            Log("connect");
            if (FirstConnect is int first)
            {
                FirstConnect = null;
                return first;
            }
            return ConnectResult;
        }

        public int GetDevices(int typeMask, out IReadOnlyList<IcueDeviceInfo> devices)
        {
            Log("devices " + typeMask.ToString("X"));
            lock (_gate) devices = Devices.ToList();
            return IcueRoute.Success;
        }

        public int GetLedPositions(string deviceId, out IReadOnlyList<IcueLedPosition> leds)
        {
            Log("leds " + deviceId);
            lock (_gate) leds = Leds.TryGetValue(deviceId, out var found) ? found : null;
            return leds == null ? IcueRoute.DeviceNotFound : IcueRoute.Success;
        }

        public int RequestControl(string deviceId, int accessLevel)
        {
            Log("control " + deviceId + " " + accessLevel);
            return IcueRoute.Success;
        }

        public int SetLedColors(string deviceId, IcueLedColor[] colors)
        {
            Log("set " + deviceId);
            lock (_gate)
            {
                if (SetResult == IcueRoute.Success) Sets.Add((deviceId, (IcueLedColor[])colors.Clone()));
                return SetResult;
            }
        }

        public int Disconnect()
        {
            Log("disconnect");
            return IcueRoute.Success;
        }

        public void Unload()
        {
            Log("unload");
            Interlocked.Increment(ref Unloads);
        }
    }

    /// <summary>
    /// The iCUE worker against a scripted SDK (#557): when it loads and
    /// connects, what it reads, what each claim makes it send, and how it
    /// hands back, as RGB.NET's provider and the SDK's docs have it.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class IcueBackendTests : IDisposable
    {
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000c2");
        private static readonly OutputPath Keyboard = new(OutputFamily.IcueDevice, "keyboard");
        private static readonly OutputPath Mouse = new(OutputFamily.IcueDevice, "mouse");

        private readonly FakeIcue _sdk = new();
        private int _made;
        private long _tick = 1_000_000;

        public IcueBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            _sdk.Devices.Add(new IcueDeviceInfo(1, "{kbd}", "S1", "K70 RGB PRO", 3, 0));
            _sdk.Devices.Add(new IcueDeviceInfo(2, "{mouse}", "S2", "DARK CORE RGB PRO", 2, 0));
            _sdk.Devices.Add(new IcueDeviceInfo(0x80, "{dram}", "S3", "VENGEANCE", 10, 0));
            _sdk.Leds["{kbd}"] = new[]
            {
                new IcueLedPosition(1, 10, 10), new IcueLedPosition(30, 40, 30), new IcueLedPosition(0x10001, 0, 30),
            };
            _sdk.Leds["{mouse}"] = new[] { new IcueLedPosition(0x40001, 1, 1), new IcueLedPosition(0x40002, 2, 2) };
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private IcueBackend Started()
        {
            var backend = new IcueBackend(() =>
            {
                Interlocked.Increment(ref _made);
                return _sdk;
            }, Tick, idleTickMs: 5);
            backend.Start();
            Passes(backend);
            return backend;
        }

        private static void Passes(IcueBackend backend, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = backend.PassCount;
                backend.Wake();
                Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
            }
        }

        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb, KeyFrame keys = null)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, keys: keys);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private IcueBackend Connected(int rgb = 0x102030, params OutputPath[] paths)
        {
            var backend = Started();
            Link(paths.Length == 0 ? new[] { Keyboard } : paths);
            Light(rgb);
            Passes(backend);
            _sdk.StateNow = IcueRoute.StateConnected;
            Passes(backend, 3);
            return backend;
        }

        [Fact]
        public void WithNothingClaimed_TheSdkIsNeverLoaded()
        {
            using var backend = Started();
            Link(Keyboard);
            Passes(backend);
            Assert.Empty(_sdk.Calls());
            Assert.True(backend.Snapshot.Scanned);
            Assert.Empty(backend.Snapshot.Units);
            Assert.Equal(BackendState.Idle, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
        }

        /// <summary>A claim loads the library and calls CorsairConnect once.
        /// Until the SDK says connected, nothing else is called and the tab
        /// waits.</summary>
        [Fact]
        public void AClaim_LoadsAndConnectsOnce_AndWaitsForTheSdkToConnect()
        {
            using var backend = Started();
            Link(Keyboard);
            Light(0x102030);
            Passes(backend, 4);
            Assert.Equal(new[] { "load", "connect" }, _sdk.Calls());
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
            Assert.True(backend.WaitMs <= IcueBackend.StatePollMs);
        }

        /// <summary>Once connected: the device list by the four types' mask,
        /// each listed device of those types read and asked for shared
        /// control (CorsairDeviceProvider.cs:155-165), the map published, and
        /// the keyboard painted opaque in its own LED order. The memory
        /// module is no type a row is lit through.</summary>
        [Fact]
        public void OnceConnected_TheDevicesAreRead_AndTheClaimedTypeIsPainted()
        {
            using var backend = Connected(0x102030);
            var calls = _sdk.Calls();
            Assert.Equal(new[] { "load", "connect", "devices 17", "leds {kbd}", "control {kbd} 0", "leds {mouse}", "control {mouse} 0", "set {kbd}" },
                calls);
            var map = PeripheralLightMaps.For(Keyboard);
            Assert.Equal(new[] { 1, 30, 0x10001 }, map.Lights.Select(l => l.Address));
            Assert.Equal(2, PeripheralLightMaps.For(Mouse).Lights.Count);
            var set = Assert.Single(_sdk.SetsNow());
            Assert.Equal("{kbd}", set.Id);
            Assert.Equal(new[]
            {
                new IcueLedColor(1, 0x10, 0x20, 0x30, 255),
                new IcueLedColor(30, 0x10, 0x20, 0x30, 255),
                new IcueLedColor(0x10001, 0x10, 0x20, 0x30, 255),
            }, set.Colors);
            Assert.Equal(BackendState.Connected, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
        }

        [Fact]
        public void TheSamePicture_IsNotSentAgain_AndANewOneIs()
        {
            using var backend = Connected(0x102030);
            Advance(1000);
            Passes(backend, 3);
            Assert.Single(_sdk.SetsNow());
            Light(0x405060);
            Advance(1000);
            Passes(backend, 2);
            var sets = _sdk.SetsNow();
            Assert.Equal(2, sets.Count);
            Assert.All(sets[1].Colors, c => Assert.Equal((0x40, 0x50, 0x60, 255), (c.R, c.G, c.B, c.A)));
        }

        /// <summary>A key frame paints the key's LED: W is CLK_W, index 30,
        /// usage 0x1A.</summary>
        [Fact]
        public void AKeyFrame_PaintsItsKey()
        {
            using var backend = Connected(0x000000);
            Light(0x000000, new KeyFrame(new Dictionary<ushort, int> { [0x1A] = 0xFF0000 }, null, null, null));
            Advance(1000);
            Passes(backend, 2);
            var last = _sdk.SetsNow().Last();
            Assert.Equal(new IcueLedColor(30, 0xFF, 0, 0, 255), last.Colors[1]);
            Assert.Equal(new IcueLedColor(1, 0, 0, 0, 255), last.Colors[0]);
        }

        /// <summary>Pictures of one type go out no closer than 33 ms
        /// apart.</summary>
        [Fact]
        public void PicturesOfOneType_AreAtLeastThirtyThreeMillisecondsApart()
        {
            using var backend = Connected(0x102030);
            Light(0x405060);
            Passes(backend, 2);
            Assert.Single(_sdk.SetsNow());
            Assert.True(backend.WaitMs <= IcueBackend.RepaintMs);
            Advance(IcueBackend.RepaintMs);
            Passes(backend, 2);
            Assert.Equal(2, _sdk.SetsNow().Count);
        }

        /// <summary>A type no claim resolves on any more is handed back
        /// translucent, and with nothing claimed the session ends:
        /// disconnect and unload (CorsairDeviceProvider.cs:334-348).</summary>
        [Fact]
        public void AnUnclaimedType_IsClearedTranslucent_AndTheSessionEnds()
        {
            using var backend = Connected(0x102030);
            Unlight();
            Passes(backend, 3);
            var sets = _sdk.SetsNow();
            Assert.Equal(2, sets.Count);
            Assert.All(sets[1].Colors, c => Assert.Equal((0, 0, 0, 0), (c.R, c.G, c.B, c.A)));
            Assert.Equal(new[] { 1u, 30u, 0x10001u }, sets[1].Colors.Select(c => c.Id));
            var calls = _sdk.Calls();
            Assert.Equal(new[] { "set {kbd}", "disconnect", "unload" }, calls.Skip(calls.Count - 3));
            Assert.Null(PeripheralLightMaps.For(Keyboard));
            Assert.Equal(BackendState.Idle, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
        }

        /// <summary>Two types claimed, then one let go: only that type's
        /// device is cleared, and the session stays.</summary>
        [Fact]
        public void OneTypeLetGo_ClearsItsDevicesOnly()
        {
            using var backend = Connected(0x102030, Keyboard, Mouse);
            Assert.Equal(new[] { "{kbd}", "{mouse}" }, _sdk.SetsNow().Select(s => s.Id));
            Link(Keyboard);
            Passes(backend, 3);
            var sets = _sdk.SetsNow();
            Assert.Equal(3, sets.Count);
            Assert.Equal("{mouse}", sets[2].Id);
            Assert.All(sets[2].Colors, c => Assert.Equal(0, c.A));
            Assert.DoesNotContain("disconnect", _sdk.Calls());
        }

        /// <summary>The SDK lost its connection, as at Win+L (docs, "Win+L"):
        /// the devices and their maps go, the tab waits, and once it is back
        /// the devices are read again and painted again.</summary>
        [Fact]
        public void ALostConnection_DropsTheDevices_AndTheirReturnRepaintsThem()
        {
            using var backend = Connected(0x102030);
            _sdk.StateNow = IcueRoute.StateConnectionLost;
            Passes(backend, 2);
            Assert.Null(PeripheralLightMaps.For(Keyboard));
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
            _sdk.StateNow = IcueRoute.StateConnected;
            Advance(1000);
            Passes(backend, 3);
            Assert.Equal(2, _sdk.SetsNow().Count);
            Assert.Equal(2, _sdk.Calls().Count(c => c == "devices 17"));
            Assert.Equal(1, _sdk.Calls().Count(c => c == "connect"));
        }

        /// <summary>A call answered "not connected" ends the devices the same
        /// way, and the list is read again.</summary>
        [Fact]
        public void ANotConnectedAnswer_ForgetsTheDevices()
        {
            using var backend = Connected(0x102030);
            _sdk.SetResult = IcueRoute.NotConnected;
            Light(0x405060);
            Advance(1000);
            Passes(backend, 2);
            Assert.Null(PeripheralLightMaps.For(Keyboard));
            Assert.True(_sdk.Calls().Count(c => c == "devices 17") >= 2);
        }

        /// <summary>The list is read again every five seconds, and a new
        /// device of a claimed type is painted.</summary>
        [Fact]
        public void TheListIsReadAgain_AndANewDeviceIsPainted()
        {
            using var backend = Connected(0x102030);
            lock (_sdk.Devices) _sdk.Devices.Add(new IcueDeviceInfo(1, "{kbd2}", "S4", "K100", 1, 0));
            _sdk.Leds["{kbd2}"] = new[] { new IcueLedPosition(1, 0, 0) };
            Advance(IcueBackend.DeviceReadMs);
            Passes(backend, 3);
            Assert.Contains(_sdk.SetsNow(), s => s.Id == "{kbd2}");
        }

        [Fact]
        public void ALibraryThatWillNotLoad_IsTriedAgainAfterTheRetry()
        {
            _sdk.Loads = false;
            using var backend = Started();
            Link(Keyboard);
            Light(0x102030);
            Passes(backend, 3);
            Assert.Equal(1, _sdk.Calls().Count(c => c == "load"));
            Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.IcueDevice));
            Advance(IcueBackend.RetryMs);
            Passes(backend, 2);
            Assert.Equal(2, _sdk.Calls().Count(c => c == "load"));
            Assert.DoesNotContain("connect", _sdk.Calls());
        }

        [Fact]
        public void ARefusedConnect_UnloadsAndTriesAgainAfterTheRetry()
        {
            _sdk.ConnectResult = IcueRoute.InvalidArguments;
            using var backend = Started();
            Link(Keyboard);
            Light(0x102030);
            Passes(backend, 3);
            Assert.Equal(new[] { "load", "connect", "unload" }, _sdk.Calls());
            Advance(IcueBackend.RetryMs);
            Passes(backend, 2);
            Assert.Equal(2, _sdk.Calls().Count(c => c == "connect"));
        }

        /// <summary>CorsairConnect refuses a client already connected or
        /// connecting with CE_InvalidOperation (docs, CorsairConnect): the
        /// session left behind is disconnected and the connect goes again
        /// once.</summary>
        [Fact]
        public void AConnectRefusedAsAlreadyConnected_DisconnectsAndConnectsAgainOnce()
        {
            _sdk.FirstConnect = IcueRoute.InvalidOperation;
            using var backend = Started();
            Link(Keyboard);
            Light(0x102030);
            Passes(backend, 3);
            Assert.Equal(new[] { "load", "connect", "disconnect", "connect" }, _sdk.Calls());
            _sdk.StateNow = IcueRoute.StateConnected;
            Passes(backend, 3);
            Assert.Single(_sdk.SetsNow());
        }

        /// <summary>The worker's stop clears what it painted and disconnects.
        /// The crash path calls nothing on the SDK from its own thread.</summary>
        [Fact]
        public void TheStop_HandsBackAndDisconnects_AndTheCrashPathOnlyStopsTheWorker()
        {
            var backend = Connected(0x102030);
            backend.ReleaseClaimedNow(Tick() + 1000);
            Assert.True(SpinWait.SpinUntil(() => _sdk.Calls().Contains("disconnect"), 30000));
            var sets = _sdk.SetsNow();
            Assert.All(sets.Last().Colors, c => Assert.Equal(0, c.A));
            backend.Dispose();
            Assert.Equal(1, _sdk.Calls().Count(c => c == "disconnect"));
        }

        [Fact]
        public void TheRouteLine_NamesICue()
        {
            Assert.Equal("Corsair iCUE", PeripheralRouteText.LightingApp(OutputFamily.IcueDevice));
            Assert.Null(PeripheralRouteText.LightingApp(OutputFamily.WootingRgbSdk));
            Assert.True(Keyboard.Shared);
        }
    }
}
