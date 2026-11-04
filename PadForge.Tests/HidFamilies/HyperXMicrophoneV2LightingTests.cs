using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using Q = PadForge.Common.Input.Peripherals.HyperXMicrophoneV2Lighting;

namespace PadForge.Tests
{
    /// <summary>The QuadCast 2 S against OpenRGB's HyperXMicrophoneV2Controller
    /// at the pinned commit (#557). Every expected byte is written out here
    /// from OpenRGB.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HyperXMicrophoneV2LightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { Q.Instance };

        private static HidCollectionInfo Collection()
            => CollectionFor(OpenRgbRegistrations.Of(Q.OpenRgbFamily).Single(), output: 64, input: 64);

        /// <summary>An answer that echoes a report at bytes 14 and 15.</summary>
        private static byte[] Echo(byte[] sent)
        {
            var answer = new byte[64];
            answer[14] = sent[0];
            answer[15] = sent[1];
            return answer;
        }

        [Fact]
        public void TheRegistration_IsMatched_ByItsOutputCollection()
        {
            var r = OpenRgbRegistrations.Of(Q.OpenRgbFamily).Single();
            // HyperXMicrophoneV2ControllerDetect.cpp:41.
            Assert.Equal(("HyperX QuadCast 2 S", 0x03F0, 0x02B5, 1, 0xFF13, 0xFF00),
                (r.Name, r.VendorId, r.ProductId, r.Interface, r.UsagePage, r.Usage));
            var model = Q.Instance.Match(Collection());
            Assert.NotNull(model);
            Assert.Equal(r.Name, model.Name);
            Assert.Same(model, HidLightingFamilies.Match(Collection()));
            Assert.Equal(new[] { VendorPrograms.Ngenuity }, model.Programs);
            // RGBController_HyperXMicrophoneV2.cpp:127-137.
            Assert.Equal(1000, model.KeepAliveMs);
            Assert.Equal(1000, model.KeepAliveIdleMs);
            Assert.Null(Q.Instance.Match(Collection() with { OutputReportLength = 63 }));
            Assert.Null(Q.Instance.Match(Collection() with { Usage = 1 }));
        }

        [Fact]
        public void TheLights_Are108_PlacedByOpenRgbsMatrix()
        {
            var map = Q.Instance.Match(Collection()).Map;
            Assert.Equal(108, map.Lights.Count);
            Assert.True(map.Positioned);
            Assert.Equal(Enumerable.Range(0, 108), map.Lights.Select(l => l.Address));
            Assert.All(map.Lights, l => Assert.Equal(LightKind.Other, l.Kind));
            // RGBController_HyperXMicrophoneV2.cpp:70-78: LED 26 at the top
            // left, LED 9 at the top right, LED 0 in the bottom row's
            // eleventh cell, LED 17 at the bottom right.
            Assert.Equal((0f, 0f), (map.Lights[26].X, map.Lights[26].Y));
            Assert.Equal((11f, 0f), (map.Lights[9].X, map.Lights[9].Y));
            Assert.Equal((10f, 8f), (map.Lights[0].X, map.Lights[0].Y));
            Assert.Equal((11f, 8f), (map.Lights[17].X, map.Lights[17].Y));
            Assert.Equal((5f, 4f), (map.Lights[67].X, map.Lights[67].Y));
            Assert.All(map.Lights, l => Assert.Equal((1f, 1f), (l.Width, l.Height)));
            Assert.Equal(108, map.Lights.Select(l => (l.X, l.Y)).Distinct().Count());
        }

        [Fact]
        public void APicture_IsTheHeaderThenSixColorReports_EachFollowedByARead()
        {
            var model = Q.Instance.Match(Collection());
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            byte[] last = null;
            io.Input = () => Echo(last);
            var session = (Q.Session)model.Begin(io, _ => { });
            var colors = Enumerable.Range(0, 108).Select(i => i << 16 | (255 - i) << 8 | (i ^ 0x55)).ToArray();
            // The renderer's colors come in map order, which is LED order.
            var recording = new RecordingChannel(io, frame => last = frame);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(recording, _ => { }, model.Map.Lights, colors));
            var sent = Calls(io);
            // out, in, seven times (HyperXMicrophoneV2Controller.cpp:182-213).
            Assert.Equal(Enumerable.Range(0, 14).Select(i => i % 2 == 0 ? "out" : "in"), sent.Select(s => s.Kind));
            var outs = sent.Where(s => s.Kind == "out").Select(s => s.Bytes).ToList();
            Assert.Equal(Hex(Bytes(64, (0, 0x44), (1, 0x01), (2, 0x06))), Hex(outs[0]));
            for (int report = 0; report < 6; report++)
            {
                var frame = outs[1 + report];
                Assert.Equal(64, frame.Length);
                Assert.Equal($"44 02 {report:X2} 00", Hex(frame, 4));
                int count = report < 5 ? 20 : 8;
                for (int c = 0; c < 20; c++)
                {
                    int led = report * 20 + c;
                    var rgb = frame.Skip(4 + 3 * c).Take(3).ToArray();
                    if (c < count) Assert.Equal(new[] { (byte)led, (byte)(255 - led), (byte)(led ^ 0x55) }, rgb);
                    else Assert.Equal(new byte[3], rgb);
                }
            }
            Assert.Equal(0, session.Mismatches);
            Assert.False(session.Feature);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);
            // The save's 03 01 06 never goes out (lines 215-271).
            Assert.DoesNotContain(outs, f => f[1] == 0x03);
        }

        [Fact]
        public void AnAnswerThatDoesNotEcho_GoesBy_AndNoAnswerFailsThePicture()
        {
            var model = Q.Instance.Match(Collection());
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            io.Input = () => new byte[64];
            var session = (Q.Session)model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[108]));
            Assert.Equal(7, session.Mismatches);
            io.Input = () => null;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new int[108]));
            Assert.Single(Calls(io).Skip(14), c => c.Kind == "out");
        }

        [Fact]
        public void AClaim_IsKeptAliveEverySecond_AndTheReleaseSendsNothing()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 64, InputLength = 64 };
            rig.Log.InputAnswer = Echo;
            var collection = KeyboardFamilyRig.Collection(0x03F0, 0x02B5, 0xFF13, 0xFF00, iface: 1, output: 64, input: 64);
            using var backend = rig.Lit(Families, collection, 0x123456);
            Assert.Equal(7, rig.Log.Of('O').Count);
            Assert.Equal("44 02 00 00 12 34 56 12 34 56", Hex(rig.Log.Of('O')[1], 10));
            rig.Advance(Q.KeepAliveMs - 1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(7, rig.Log.Of('O').Count);
            rig.Advance(1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(14, rig.Log.Of('O').Count);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        /// <summary>A channel that hands every output report to a callback
        /// before the inner channel takes it, so the inner channel's input
        /// can answer it.</summary>
        private sealed class RecordingChannel : IHidReportChannel
        {
            private readonly IHidReportChannel _inner;
            private readonly Action<byte[]> _sent;

            public RecordingChannel(IHidReportChannel inner, Action<byte[]> sent)
            {
                _inner = inner;
                _sent = sent;
            }

            public string Path => _inner.Path;
            public bool FeatureOnly => _inner.FeatureOnly;
            public int InputReportLength => _inner.InputReportLength;
            public int OutputReportLength => _inner.OutputReportLength;
            public int FeatureReportLength => _inner.FeatureReportLength;
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public bool GetFeature(byte reportId, byte[] buffer) => _inner.GetFeature(reportId, buffer);

            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
            {
                _sent(report);
                return _inner.WriteOutput(report, timeoutMs);
            }

            public int ReadInput(byte[] buffer, int timeoutMs) => _inner.ReadInput(buffer, timeoutMs);
            public void Dispose() => _inner.Dispose();
        }
    }
}
