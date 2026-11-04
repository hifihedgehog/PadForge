using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The HYTE CNVS against OpenRGB's HYTEMousematController
    /// (#557).</summary>
    public class HyteMousematLightingTests
    {
        [Fact]
        public void TheStream_IsStreamingCommand_At72Percent()
        {
            var colors = Enumerable.Repeat(0, 50).ToArray();
            colors[0] = 0x6496C8;
            colors[49] = 0xFFFFFF;
            var stream = HyteMousematLighting.Stream(colors);
            Assert.Equal(157, stream.Length);
            // HYTEMousematController_Windows_MacOS.cpp:65-100: green, red,
            // blue at 72/100, rounded down: 150 -> 108, 100 -> 72, 200 -> 144,
            // 255 -> 183.
            Assert.Equal("FF EE 02 01 00 32 00 6C 48 90", Hex(stream, 10));
            Assert.Equal("B7 B7 B7", Hex(stream.Skip(154).ToArray()));
            Assert.Equal(50, HyteMousematLighting.Lights().Count);
            Assert.All(HyteMousematLighting.Lights(), l => Assert.Equal(LightKind.Edge, l.Kind));
        }

        [Fact]
        public void ASession_TurnsTheAnimationOffOnce_AndBackOnAtTheHandBack()
        {
            var port = new FakeSerialPort();
            var shots = new List<(string, byte[], int)>();
            var family = new HyteMousematLighting(() => Array.Empty<ComPort>(), _ => Guid.Empty, _ => port,
                (key, bytes, ms) => { shots.Add((key, bytes, ms)); return true; });
            var model = family.Model;
            Assert.Equal(new[] { VendorPrograms.HyteNexus }, model.Programs);
            var session = model.BeginOwn(model.Open(@"\\.\COM8"), _ => { });
            Assert.Empty(session.CrashFrames);
            var colors = Distinct(50);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, colors));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, colors));
            var writes = port.Written();
            Assert.Equal(3, writes.Count);
            Assert.Equal("FF DC 05 00", Hex(writes[0]));
            Assert.Equal(Hex(HyteMousematLighting.Stream(colors)), Hex(writes[1]));
            Assert.Equal(Hex(writes[1]), Hex(writes[2]));
            Assert.Equal("FF DC 05 01", Hex(Assert.Single(session.CrashFrames)));
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal("FF DC 05 01", Hex(port.Written()[3]));
            Assert.Empty(session.CrashFrames);
            Assert.True(model.WriteOnce(@"\\.\COM8", HyteMousematLighting.AnimationOn, 200));
            Assert.Equal(@"\\.\COM8", Assert.Single(shots).Item1);
        }
    }

    /// <summary>A serial family of the lane through the shared worker: the
    /// look lists the port, a claim opens it and paints, the crash path's
    /// record carries the family's writer, and the claim's end hands back
    /// (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class SerialLightWorkerTests : IDisposable
    {
        private static readonly Guid Device = new("00000000-0000-0000-0000-0000000000c1");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private long _tick = 1_000_000;

        public SerialLightWorkerTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
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

        [Fact]
        public void TheHyteCnvs_IsFoundLitAndHandedBackThroughTheWorker()
        {
            var port = new FakeSerialPort();
            var ports = new[] { new ComPort("COM8", "HYTE CNVS (COM8)", @"USB\VID_3402&PID_0B01\8&2&0&1") };
            var family = new HyteMousematLighting(() => ports, _ => Box, _ => port, (_, _, _) => true);
            using var backend = new HidLightingBackend(new IHidLightingFamily[] { family }, _ => null, _ => Box,
                () => Interlocked.Read(ref _tick), (_, _, _) => true, (_, _, _) => true,
                ms => Interlocked.Add(ref _tick, ms), idleTickMs: 5);
            backend.Start();
            backend.Rescan(new HidCollectionInfo[0]);
            Passes(backend);
            var unit = Assert.Single(backend.Snapshot.Units);
            Assert.Equal(@"\\.\COM8", unit.Key);
            Assert.Equal((ushort)0x3402, unit.VendorId);
            Assert.Equal((ushort)0x0B01, unit.ProductId);
            var path = new OutputPath(OutputFamily.HidLightingUnit, @"\\.\COM8");
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = Device, Lighting = new[] { path } } }));
            PeripheralOutputs.SetLighting(Device, slot: 0, player: 1, 0x10, 0x20, 0x30);
            Passes(backend);
            var writes = port.Written();
            Assert.Equal("FF DC 05 00", Hex(writes[0]));
            // 0x102030 at 72 percent: green 0x17, red 0x0B, blue 0x22.
            Assert.Equal("FF EE 02 01 00 32 00 17 0B 22", Hex(writes[1], 10));
            var record = Assert.Single(backend.Recorded);
            Assert.NotNull(record.Write);
            Assert.Equal("FF DC 05 01", Hex(Assert.Single(record.Frames)));
            PeripheralOutputs.ReleaseLighting(Device, 0);
            Passes(backend, 3);
            Assert.Equal("FF DC 05 01", Hex(port.Written().Last()));
            Assert.True(port.Disposed);
            Assert.Empty(backend.Recorded);
        }
    }
}
