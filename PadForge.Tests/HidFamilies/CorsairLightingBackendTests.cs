using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shared HID worker with the two Corsair families (#557): the open,
    /// the picture, the crash path's record, the keep-alive, the hand-back
    /// and the hand-back a killed run left.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class CorsairLightingBackendTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000a1");

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly object _gate = new();
        private readonly List<byte[]> _outputs = new();
        private long _tick = 1_000_000;

        public CorsairLightingBackendTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            KeyboardProfileBackups.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private List<byte[]> Outputs()
        {
            lock (_gate) return _outputs.ToList();
        }

        /// <summary>A Corsair device on every open: the older protocol
        /// answers that it is a keyboard, the newer one its product ID and
        /// layout. Block writes get no answer.</summary>
        private IHidReportChannel Open(string path)
        {
            ushort pid = Convert.ToUInt16(path.Substring(path.IndexOf("pid_", StringComparison.Ordinal) + 4, 4), 16);
            var channel = new FakeCorsairChannel
            {
                Path = path,
                Answer = q =>
                {
                    var answer = new byte[65];
                    answer[1] = q[1];
                    answer[2] = q[2];
                    if (q[1] == 0x0E)
                    {
                        answer[1 + 0x14] = 0xC0;
                        return answer;
                    }
                    if (q[2] is 0x06 or 0x07) return null;
                    if (q[2] == 0x02 && q[3] == 0x12)
                    {
                        answer[4] = (byte)pid;
                        answer[5] = (byte)(pid >> 8);
                    }
                    return answer;
                },
            };
            return new Recorder(channel, this);
        }

        /// <summary>Passes every call through to the fake and keeps the
        /// output reports for the test, under a lock: the worker writes on
        /// its own thread.</summary>
        private sealed class Recorder : IHidReportChannel
        {
            private readonly FakeCorsairChannel _inner;
            private readonly CorsairLightingBackendTests _test;

            public Recorder(FakeCorsairChannel inner, CorsairLightingBackendTests test)
            {
                _inner = inner;
                _test = test;
            }

            public string Path => _inner.Path;
            public bool FeatureOnly => false;
            public int InputReportLength => 65;
            public int OutputReportLength => 65;
            public int FeatureReportLength => 65;
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public bool GetFeature(byte reportId, byte[] buffer) => _inner.GetFeature(reportId, buffer);
            public int ReadInput(byte[] buffer, int timeoutMs) => _inner.ReadInput(buffer, timeoutMs);

            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
            {
                lock (_test._gate) _test._outputs.Add((byte[])report.Clone());
                return _inner.WriteOutput(report, timeoutMs);
            }

            public void Dispose() { }
        }

        private HidLightingBackend Started(HidCollectionInfo present)
        {
            var families = new IHidLightingFamily[]
            {
                new CorsairPeripheralLighting(Tick), new CorsairPeripheralV2Lighting(Tick),
            };
            var backend = new HidLightingBackend(families, Open, _ => Box, Tick,
                (_, _, _) => true, (_, _, _) => true, ms => Advance(ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new[] { present });
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

        private static OutputPath PathOf(HidCollectionInfo collection) => new(OutputFamily.HidLightingUnit, collection.Path);

        private static void Link(params OutputPath[] paths)
            => PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = paths } }));

        private static void Light(int rgb)
            => PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static void Unlight() => PeripheralOutputs.ReleaseLighting(Device, 0);

        private HidLightingBackend Lit(HidCollectionInfo collection, int rgb = 0x112233)
        {
            var backend = Started(collection);
            Link(PathOf(collection));
            Light(rgb);
            Passes(backend, 3);
            return backend;
        }

        private static string Hex(byte[] bytes, int count) => CorsairPeripheralLightingTests.Hex(bytes, count);

        [Fact]
        public void AKeyboardClaim_Opens_Paints_AndHandsBack()
        {
            var k70 = CorsairPeripheralLightingTests.Collection(0x1B13);
            using var backend = Lit(k70);
            var outputs = Outputs();
            Assert.Equal("00 0E 01", Hex(outputs[0], 3));
            Assert.Equal("00 07 05 02 00 03", Hex(outputs[1], 6));
            // The claim's color in every bank: red 11 at Escape, number 0.
            var picture = outputs.Skip(2).ToList();
            Assert.Equal(12, picture.Count);
            Assert.Equal(0x11, picture[0][5]);
            Assert.Equal(0x22, picture[4][5]);
            Assert.Equal(0x33, picture[8][5]);
            var record = Assert.Single(backend.Recorded);
            Assert.False(record.Feature);
            Assert.Equal(2, record.Frames.Length);
            Assert.NotNull(KeyboardProfileBackups.Get("corsair:1B1C:1B13"));
            Assert.Equal(113, PeripheralLightMaps.For(PathOf(k70)).Lights.Count);

            Unlight();
            Passes(backend);
            Assert.Equal(new[] { "00 07 04 01", "00 07 04 01" }, Outputs().Skip(14).Select(f => Hex(f, 4)));
            Assert.Null(KeyboardProfileBackups.Get("corsair:1B1C:1B13"));
            Assert.Empty(backend.Recorded);
        }

        [Fact]
        public void AKilledRunsDevice_IsHandedBack_WithNoClaim()
        {
            KeyboardProfileBackups.LoadRegistry(new[] { ("corsair:1B1C:1BF0", 1, "0008010300010000") });
            using var backend = Started(CorsairPeripheralLightingTests.Collection(0x1BF0, page: 0xFF42));
            Assert.Empty(Outputs());
            Advance(HidLightingBackend.RepairGraceMs);
            Passes(backend);
            Assert.Equal(new[] { "00 08 01 03 00 01" }, Outputs().Select(f => Hex(f, 6)));
            Assert.Null(KeyboardProfileBackups.Get("corsair:1B1C:1BF0"));
        }

        [Fact]
        public void AV2Device_IsKeptAlive_ThroughTheWorkersRecheck()
        {
            var m75 = CorsairPeripheralLightingTests.Collection(0x1BF0, page: 0xFF42);
            using var backend = Lit(m75);
            int Blocks() => Outputs().Count(f => f[2] == 0x06);
            Assert.Equal(1, Blocks());
            Advance(CorsairPeripheralV2Lighting.KeepAliveMs + CorsairPeripheralV2Lighting.KeepAliveCheckMs);
            Passes(backend, 3);
            Assert.Equal(2, Blocks());
            Unlight();
            Passes(backend);
            Assert.Equal("00 08 01 03 00 01", Hex(Outputs()[^1], 6));
        }
    }
}
