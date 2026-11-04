using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Wooting route's reports (#557), written out from Wooting's RGB SDK
    /// at 7e091a0d (src/wooting-usb.c, src/wooting-rgb-sdk.c). The first
    /// protocol's reports come from a port of the SDK's C made apart from
    /// the builder under test (lane script wooting_v1_golden.py), which
    /// reads the SDK's tables out of its source.
    /// </summary>
    public class WootingRgbSdkFrameTests
    {
        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        /// <summary>wooting_usb_send_feature_buff (wooting-usb.c:684-701):
        /// report 0 and D0 DA, or report 1 and D1 DA on the third protocol,
        /// then the command and four zero parameters.</summary>
        [Fact]
        public void ACommand_IsTheSdksEightBytes()
        {
            Assert.Equal("00D0DA2100000000", Hex(WootingRgbSdkRoute.Command(WootingKeyboardLighting.Protocol.V2, 33)));
            Assert.Equal("00D0DA2000000000", Hex(WootingRgbSdkRoute.Command(WootingKeyboardLighting.Protocol.V1, 32)));
            Assert.Equal("01D1DA2000000000", Hex(WootingRgbSdkRoute.Command(WootingKeyboardLighting.Protocol.V3, 32)));
            Assert.Equal((32, 33, 11), (WootingRgbSdkRoute.ResetAllCommand, WootingRgbSdkRoute.ColorInitCommand,
                WootingRgbSdkRoute.RawColorsReport));
        }

        /// <summary>The answer the SDK reads: 128, 256 or one report
        /// (wooting-usb.c:27-29 and 703-718).</summary>
        [Fact]
        public void TheAnswerSize_IsTheSdks()
        {
            Assert.Equal(128, WootingRgbSdkRoute.ResponseSize(WootingKeyboardLighting.Protocol.V1));
            Assert.Equal(256, WootingRgbSdkRoute.ResponseSize(WootingKeyboardLighting.Protocol.V2));
            Assert.Equal(1, WootingRgbSdkRoute.ResponseSize(WootingKeyboardLighting.Protocol.V3));
        }

        /// <summary>encodeColor and decodeColor (wooting-rgb-sdk.c:83-98):
        /// 0xFF8040 is F800 | 0400 | 0008.</summary>
        [Fact]
        public void AColor_IsTheSdksSixteenBits()
        {
            Assert.Equal(0xFC08, WootingRgbSdkRoute.Encode(0xFF8040));
            Assert.Equal(0xFFFF, WootingRgbSdkRoute.Encode(0xFFFFFF));
            Assert.Equal(((byte)0xF8, (byte)0x80, (byte)0x40), WootingRgbSdkRoute.Decode(0xFC08));
        }

        private static (LightInfo[] Lights, int[] Colors) Picture(params (int Row, int Col, int Rgb)[] cells)
            => (cells.Select(c => new LightInfo(c.Row * 21 + c.Col, LightKind.Key, 0, LightKeySource.None)).ToArray(),
                cells.Select(c => c.Rgb).ToArray());

        /// <summary>wooting_usb_send_buffer_v2 (wooting-usb.c:606-654): report
        /// 0 of 257 bytes, 00 D0 DA 0B, then the matrix low byte first. The
        /// cell at row 5, column 20 is the 126th.</summary>
        [Fact]
        public void TheSecondProtocol_SendsTheMatrixInOneReport()
        {
            var (lights, colors) = Picture((0, 0, 0xFF8040), (5, 20, 0x0000FF));
            var report = Assert.Single(WootingRgbSdkRoute.Reports(WootingKeyboardLighting.Protocol.V2,
                WootingKeyboardLighting.KeyboardType.Full, 257, lights, colors));
            Assert.Equal(257, report.Length);
            Assert.Equal("00D0DA0B08FC", Hex(report[..6]));
            Assert.Equal("1F00", Hex(report[254..256]));
            Assert.Equal(0, report[256]);
            Assert.All(report[6..254], b => Assert.Equal(0, b));
        }

        /// <summary>A collection of 64-byte reports takes the same report in
        /// four, each report 0 and the next 64 bytes after the ID byte
        /// (wooting-usb.c:620-640).</summary>
        [Fact]
        public void TheSecondProtocol_OnSmallReports_SendsFourPackets()
        {
            var (lights, colors) = Picture((0, 0, 0xFF8040), (5, 20, 0x0000FF));
            var packets = WootingRgbSdkRoute.Reports(WootingKeyboardLighting.Protocol.V2,
                WootingKeyboardLighting.KeyboardType.Sixty, 65, lights, colors);
            Assert.Equal(4, packets.Count);
            Assert.All(packets, p => Assert.Equal(65, p.Length));
            Assert.All(packets, p => Assert.Equal(0, p[0]));
            Assert.Equal("00D0DA0B08FC", Hex(packets[0][..6]));
            // Bytes 253 and 254 of the whole report (cell 125) are the
            // fourth packet's 62nd and 63rd after its ID byte.
            Assert.Equal("1F00", Hex(packets[3][62..64]));
        }

        /// <summary>wooting_usb_send_buffer_v3 (wooting-usb.c:656-682):
        /// report 0x05 of 2047 bytes, 05 D1 DA 0B, then the matrix.</summary>
        [Fact]
        public void TheThirdProtocol_SendsReportFiveOf2047Bytes()
        {
            var (lights, colors) = Picture((0, 0, 0xFF8040));
            var report = Assert.Single(WootingRgbSdkRoute.Reports(WootingKeyboardLighting.Protocol.V3,
                WootingKeyboardLighting.KeyboardType.Eighty, 2047, lights, colors));
            Assert.Equal(2047, report.Length);
            Assert.Equal("05D1DA0B08FC", Hex(report[..6]));
            Assert.All(report.Skip(6), b => Assert.Equal(0, b));
        }

        /// <summary>The first protocol: the same cells on a Two and a One,
        /// from the SDK's C (wooting-rgb-sdk.c:204-244 and 308-362,
        /// wooting-usb.c:541-604). Escape 0xFF8040, the ANSI Enter 0x00FF00
        /// (copied to the ISO Enter), the left Shift 0x0000FF (copied to
        /// the ISO Shift), and row 5 column 19 white, which the One, 17
        /// columns wide, does not have. The One gets four reports.</summary>
        [Fact]
        public void TheFirstProtocol_IsTheSdksDriverBuffers()
        {
            var (lights, colors) = Picture((0, 0, 0xFF8040), (3, 13, 0x00FF00), (4, 0, 0x0000FF), (5, 19, 0xFFFFFF));
            string[] two =
            {
                "00D0DA0B0000EC000000000000000000000000000000320000000000000000000000000000000D0000000000000000EC00EC0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000F4F7",
                "00D0DA0B0060000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007CE8",
                "00D0DA0B0100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000F70000F7000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000CDBA",
                "00D0DA0B016000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000DBC5",
                "00D0DA0B020000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000EC000000000000000000000000000000F7000000000000000000000000000000EC000000000000000000000000000000000000000000000000000000000000000000000000E225",
            };
            Assert.Equal(two, WootingRgbSdkRoute.Reports(WootingKeyboardLighting.Protocol.V1,
                WootingKeyboardLighting.KeyboardType.Full, 129, lights, colors).Select(Hex));
            Assert.Equal(two.Take(4), WootingRgbSdkRoute.Reports(WootingKeyboardLighting.Protocol.V1,
                WootingKeyboardLighting.KeyboardType.Tkl, 129, lights, colors).Select(Hex));
        }

        /// <summary>gammaFilter (wooting-rgb-sdk.c:36-54) as the golden
        /// script read it from the C, and get_safe_led_idex's table
        /// (lines 61-73), which the Wooting family's generated table
        /// holds.</summary>
        [Fact]
        public void TheGammaAndLedTables_AreTheSdks()
        {
            int[] gamma =
            {
                0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6,
                6, 6, 7, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 12, 12, 13, 13, 13, 13,
                14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 22, 22, 23, 24, 24, 25, 25,
                26, 26, 26, 27, 27, 28, 29, 29, 30, 31, 32, 32, 33, 34, 35, 35, 36, 37, 38, 39, 39, 40, 41, 42, 42, 43, 43,
                44, 44, 45, 45, 46, 47, 48, 49, 50, 50, 51, 51, 52, 52, 53, 54, 54, 55, 55, 56, 56, 57, 57, 58, 58, 59, 59,
                60, 60, 61, 61, 62, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 77, 78, 79, 81, 82, 83, 85, 86,
                87, 89, 90, 92, 93, 95, 96, 98, 99, 101, 102, 104, 105, 107, 109, 110, 112, 114, 115, 117, 119, 120, 122,
                124, 126, 127, 129, 131, 133, 135, 137, 138, 140, 142, 144, 146, 148, 150, 152, 154, 156, 158, 160, 162,
                164, 167, 169, 171, 173, 175, 177, 180, 182, 184, 186, 189, 191, 193, 196, 198, 200, 203, 205, 208, 210,
                213, 215, 218, 220, 223, 225, 228, 231, 233, 236, 239, 241, 244, 247, 249, 252, 255,
            };
            Assert.Equal(gamma, WootingRgbSdkRoute.Gamma.Select(b => (int)b));
            int[] ledIndex =
            {
                0, 255, 11, 12, 23, 24, 36, 47, 85, 84, 49, 48, 59, 61, 73, 81, 80, 113, 114, 115, 116,
                2, 1, 14, 13, 26, 25, 35, 38, 37, 87, 86, 95, 51, 63, 75, 72, 74, 96, 97, 98, 99,
                3, 4, 15, 16, 27, 28, 39, 42, 40, 88, 89, 52, 53, 71, 76, 83, 77, 102, 103, 104, 100,
                5, 6, 17, 18, 29, 30, 41, 46, 44, 90, 93, 54, 57, 65, 255, 255, 255, 105, 106, 107, 255,
                9, 8, 19, 20, 31, 34, 32, 45, 43, 91, 92, 55, 255, 66, 255, 78, 255, 108, 109, 110, 101,
                10, 22, 21, 255, 255, 255, 33, 255, 255, 255, 94, 58, 67, 68, 70, 79, 82, 255, 111, 112, 255,
            };
            Assert.Equal(ledIndex, WootingLayouts.V1LedIndex.Select(b => (int)b));
            Assert.Equal((95, 17), WootingRgbSdkRoute.V1Board(WootingKeyboardLighting.KeyboardType.Tkl));
            Assert.Equal((116, 21), WootingRgbSdkRoute.V1Board(WootingKeyboardLighting.KeyboardType.Full));
        }

        /// <summary>A light whose cell is outside the matrix is dropped.</summary>
        [Fact]
        public void ACellOutsideTheMatrix_IsDropped()
        {
            var matrix = WootingRgbSdkRoute.Matrix(new[]
            {
                new LightInfo(126, LightKind.Key, 0, LightKeySource.None), new LightInfo(-1, LightKind.Key, 0, LightKeySource.None),
                new LightInfo(3, LightKind.Key, 0, LightKeySource.None),
            }, new[] { 0xFFFFFF, 0xFFFFFF, 0xFFFFFF });
            Assert.Equal(126, matrix.Length);
            Assert.Equal(new[] { 3 }, Enumerable.Range(0, 126).Where(i => matrix[i] != 0));
        }
    }

    /// <summary>The Wooting route in the link pass (#557): while Wootility
    /// runs, a Wooting keyboard's row is lit through the route, by its
    /// shared-worker unit's collection.</summary>
    public class WootingRgbSdkLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000d7");
        private static readonly Guid Box = new("55555555-6666-7777-8888-999999999999");

        private static (LinkRow Row, DirectUnit Unit) Wooting(bool asleep = false)
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x31E3, 0x1402, Box, null);
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, @"\\?\hid#vid_31e3&pid_1402&mi_02#cfg", Box, 0x31E3, 0x1402)
            {
                Programs = VendorPrograms.Wooting,
                Asleep = asleep,
            };
            return (row, unit);
        }

        private static LinkInputs Inputs(IEnumerable<string> running, params DirectUnit[] units)
            => new(HidppSnapshot.Empty,
                PeripheralPresence.WithRunning(new PeripheralPresence(false, false, false, false, false), running))
            {
                Units = units,
            };

        [Fact]
        public void WhileWootilityRuns_TheRowIsLitThroughTheRoute()
        {
            var (row, unit) = Wooting();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "Wootility.exe" }, unit)).For(A);
            Assert.Equal(new[] { new OutputPath(OutputFamily.WootingRgbSdk, unit.Key) }, links.Lighting);
            Assert.Null(links.LeftToProgram);
            Assert.True(links.DirectWhenClosed);
            Assert.False(links.Lighting[0].Shared);
        }

        [Fact]
        public void WhileWootilityDoesNotRun_TheSharedWorkerLightsIt()
        {
            var (row, unit) = Wooting();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "explorer.exe" }, unit)).For(A);
            Assert.Equal(new[] { unit.Path }, links.Lighting);
        }

        /// <summary>A unit that does not answer gives no route path, and
        /// the row is left to Wootility.</summary>
        [Fact]
        public void AnAsleepUnit_LeavesTheRowToWootility()
        {
            var (row, unit) = Wooting(asleep: true);
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "Wootility.exe" }, unit)).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal("Wootility", links.LeftToProgram);
        }

        [Fact]
        public void AnotherProgramsUnit_GetsNoRoutePath()
        {
            var shared = new List<OutputPath>();
            var (row, unit) = Wooting();
            var other = unit with { Programs = VendorPrograms.Roccat };
            WootingRgbSdkRoute.AddShared(shared, VendorPrograms.WootingWootility, new[] { other }, row);
            WootingRgbSdkRoute.AddShared(shared, VendorPrograms.RoccatSwarm, new[] { unit }, row);
            Assert.Empty(shared);
            Assert.True(WootingRgbSdkRoute.IsWooting(unit));
            Assert.False(WootingRgbSdkRoute.IsWooting(other));
        }
    }

    /// <summary>A Wooting keyboard's configuration collection as the route
    /// sees it: it logs each report and answers each command with one input
    /// report, as the SDK reads it.</summary>
    internal sealed class FakeWootingChannel : IHidReportChannel
    {
        private readonly object _gate = new();
        public readonly List<(char Kind, byte[] Report)> Sent = new();
        private int _answers;
        public volatile bool FailOutput;
        public volatile bool Answer = true;
        public volatile bool Disposed;

        public string Path { get; init; }
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 257;
        public int OutputReportLength { get; init; } = 257;
        public int FeatureReportLength { get; init; } = 9;

        public List<(char Kind, byte[] Report)> Log()
        {
            lock (_gate) return Sent.ToList();
        }

        public bool SetFeature(byte[] report)
        {
            if (Disposed) return false;
            lock (_gate)
            {
                Sent.Add(('F', (byte[])report.Clone()));
                if (Answer) _answers++;
            }
            return true;
        }

        public bool GetFeature(byte reportId, byte[] buffer) => false;

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (Disposed || FailOutput) return false;
            lock (_gate) Sent.Add(('O', (byte[])report.Clone()));
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            lock (_gate)
            {
                Sent.Add(('R', Array.Empty<byte>()));
                if (_answers == 0) return 0;
                _answers--;
            }
            Array.Clear(buffer);
            return buffer.Length;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// The Wooting route's worker against a scripted collection (#557): what
    /// a claim makes it send, the hand-back, the cases where another writer
    /// takes the keyboard, and the crash path.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class WootingRgbSdkBackendTests : IDisposable
    {
        private const string Key = @"\\?\hid#vid_31e3&pid_1402&mi_02#cfg";
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000d8");
        private static readonly OutputPath Route = new(OutputFamily.WootingRgbSdk, Key);
        private static readonly OutputPath Own = new(OutputFamily.HidLightingUnit, Key);

        private readonly List<FakeWootingChannel> _channels = new();
        private readonly List<(string Path, byte[] Report)> _oneShots = new();
        private volatile bool _opens = true;
        private ushort _page = WootingKeyboardLighting.PageV2;
        private int _outputLength = 257;
        private long _tick = 1_000_000;

        public WootingRgbSdkBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private IHidReportChannel Open(string path)
        {
            if (!_opens) return null;
            var channel = new FakeWootingChannel { Path = path, OutputReportLength = _outputLength };
            lock (_channels) _channels.Add(channel);
            return channel;
        }

        private bool OneShot(string path, byte[] report, int timeoutMs)
        {
            lock (_oneShots) _oneShots.Add((path, report));
            return true;
        }

        private List<(char Kind, byte[] Report)> Everything()
        {
            lock (_channels) return _channels.SelectMany(c => c.Log()).ToList();
        }

        private WootingRgbSdkBackend Started()
        {
            var backend = new WootingRgbSdkBackend(Open, OneShot, Tick, idleTickMs: 5, responseTimeoutMs: 1);
            backend.Start();
            backend.Rescan(new[]
            {
                new HidCollectionInfo { Path = Key.ToUpperInvariant(), VendorId = 0x31E3, ProductId = 0x1402, UsagePage = _page },
            });
            Passes(backend);
            return backend;
        }

        private static void Passes(WootingRgbSdkBackend backend, int count = 2)
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

        private static void Light(int rgb)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

        private WootingRgbSdkBackend Lit(int rgb = 0xFF8040)
        {
            var backend = Started();
            Link(Route);
            Light(rgb);
            Passes(backend, 3);
            return backend;
        }

        /// <summary>A linked path's lights are published, nothing is opened
        /// until a claim resolves on it.</summary>
        [Fact]
        public void ALinkedPath_PublishesItsLights_AndOpensNothingUnclaimed()
        {
            using var backend = Started();
            Link(Route);
            Passes(backend);
            var map = PeripheralLightMaps.For(Route);
            Assert.Equal(WootingKeyboardLighting.Lights(WootingKeyboardLighting.KeyboardType.Eighty).Count, map.Lights.Count);
            lock (_channels) Assert.Empty(_channels);
            Assert.Empty(backend.Snapshot.Units);
            Assert.True(backend.Snapshot.Scanned);
        }

        /// <summary>A claim: the color init command and the keyboard's answer
        /// read (wooting-usb.c:488-490, 720-754), then the picture as one
        /// 257-byte report, every light the claim's color.</summary>
        [Fact]
        public void AClaim_SendsTheInitCommand_ReadsTheAnswer_ThenThePicture()
        {
            using var backend = Lit(0xFF8040);
            var log = Everything();
            Assert.Equal('F', log[0].Kind);
            Assert.Equal("00D0DA2100000000", Hex(log[0].Report));
            Assert.Equal('R', log[1].Kind);
            var picture = Assert.Single(log, e => e.Kind == 'O').Report;
            Assert.Equal(257, picture.Length);
            Assert.Equal("00D0DA0B08FC", Hex(picture[..6]));
            Assert.Equal(new[] { Key }, backend.Recorded);
            Assert.Equal(BackendState.Connected, PeripheralOutputs.StateOf(OutputFamily.WootingRgbSdk));
        }

        [Fact]
        public void TheSamePicture_IsNotSentAgain()
        {
            using var backend = Lit();
            Advance(1000);
            Passes(backend, 3);
            Assert.Single(Everything(), e => e.Kind == 'O');
            Light(0x00FF00);
            Advance(1000);
            Passes(backend, 2);
            Assert.Equal(2, Everything().Count(e => e.Kind == 'O'));
        }

        /// <summary>The third protocol's command is report 1 with D1 DA, and
        /// its picture report 5 (wooting-usb.c:656-701).</summary>
        [Fact]
        public void TheThirdProtocol_UsesItsOwnReports()
        {
            _page = WootingKeyboardLighting.PageV3;
            _outputLength = 2047;
            using var backend = Lit();
            var log = Everything();
            Assert.Equal("01D1DA2100000000", Hex(log[0].Report));
            var picture = Assert.Single(log, e => e.Kind == 'O').Report;
            Assert.Equal(2047, picture.Length);
            Assert.Equal(5, picture[0]);
        }

        /// <summary>When no claim resolves any more: the reset command, its
        /// answer read, and the handle closed (wooting-rgb-sdk.c:106-124).</summary>
        [Fact]
        public void TheHandBack_IsTheResetCommand()
        {
            using var backend = Lit();
            Unlight();
            Passes(backend, 3);
            var log = Everything();
            var last = log.Last(e => e.Kind == 'F');
            Assert.Equal("00D0DA2000000000", Hex(last.Report));
            lock (_channels) Assert.True(_channels.Single().Disposed);
            Assert.Empty(backend.Recorded);
        }

        /// <summary>Once Wootility is closed the shared worker's own path to
        /// the keyboard is linked, and it opens the keyboard with its own
        /// init and picture: no reset goes out under it.</summary>
        [Fact]
        public void WhenTheSharedWorkerTakesTheKeyboard_NoResetIsSent()
        {
            using var backend = Lit();
            Link(Own);
            Passes(backend, 3);
            Assert.Single(Everything(), e => e.Kind == 'F');
            lock (_channels) Assert.True(_channels.Single().Disposed);
            Assert.Empty(backend.Recorded);
            Assert.True(WootingRgbSdkBackend.TakenOver(Key));
        }

        /// <summary>A failed write closes the handle and owes the reset,
        /// which goes out once the collection opens with no claim on it.</summary>
        [Fact]
        public void AFailedWrite_OwesTheReset_PaidWhenTheCollectionOpensAgain()
        {
            using var backend = Lit();
            lock (_channels) _channels.Single().FailOutput = true;
            Light(0x00FF00);
            Advance(1000);
            Passes(backend, 2);
            lock (_channels) Assert.True(_channels.Single().Disposed);
            Assert.Equal(new[] { Key }, backend.Recorded);
            Unlight();
            Advance(WootingRgbSdkBackend.RetryMs);
            Passes(backend, 3);
            FakeWootingChannel second;
            lock (_channels) second = _channels[1];
            Assert.Equal("00D0DA2000000000", Hex(second.Log().First(e => e.Kind == 'F').Report));
            Assert.Empty(backend.Recorded);
        }

        /// <summary>A keyboard whose collection left the walk is owed
        /// nothing, and no handle is opened for it.</summary>
        [Fact]
        public void AKeyboardThatLeft_IsOwedNothing()
        {
            using var backend = Lit();
            lock (_channels) _channels.Single().FailOutput = true;
            Light(0x00FF00);
            Advance(1000);
            Passes(backend, 2);
            Assert.Equal(new[] { Key }, backend.Recorded);
            backend.Rescan(Array.Empty<HidCollectionInfo>());
            Unlight();
            Advance(WootingRgbSdkBackend.RetryMs);
            Passes(backend, 3);
            Assert.Empty(backend.Recorded);
            lock (_channels) Assert.Single(_channels);
        }

        [Fact]
        public void ACollectionThatWillNotOpen_IsTriedAgainAfterTheRetry()
        {
            _opens = false;
            using var backend = Lit();
            Assert.Empty(Everything());
            _opens = true;
            Passes(backend, 2);
            Assert.Empty(Everything());
            Advance(WootingRgbSdkBackend.RetryMs);
            Passes(backend, 3);
            Assert.Single(Everything(), e => e.Kind == 'O');
        }

        /// <summary>The worker's stop sends the reset, and the crash path
        /// resets a keyboard still on record through a handle of its own.</summary>
        [Fact]
        public void TheStop_Resets_AndTheCrashPathResetsWhatIsOnRecord()
        {
            var backend = Lit();
            backend.Stop();
            Assert.Equal("00D0DA2000000000", Hex(Everything().Last(e => e.Kind == 'F').Report));
            backend.Dispose();

            var held = Lit();
            lock (_channels) foreach (var c in _channels) c.FailOutput = true;
            Light(0x00FF00);
            Advance(1000);
            Passes(held, 2);
            _opens = false;
            Assert.Equal(new[] { Key }, held.Recorded);
            held.ReleaseClaimedNow(Tick() + 1000);
            lock (_oneShots)
            {
                var shot = Assert.Single(_oneShots);
                Assert.Equal(Key, shot.Path);
                Assert.Equal("00D0DA2000000000", Hex(shot.Report));
            }
            held.Dispose();
        }
    }
}
