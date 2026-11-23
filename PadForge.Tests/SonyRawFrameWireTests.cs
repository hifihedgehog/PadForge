using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A USB Sony persona's packed frame reaches HIDMaestro in the form its
    /// SubmitRawReport documents: the 63 data bytes of input report 0x01,
    /// whose id HIDMaestro puts back in front (eb2f2ce0,
    /// HMController.SubmitRawReport and EmitExtendedFrame, which head a frame
    /// one byte short of the declared report with the declared id).
    ///
    /// <para>HIDMaestro 2.3.0 made the USB Sony profiles always armed
    /// ("Since 2.3.0 the USB Sony profiles are always armed", HMController's
    /// constructor). PadForge sent every always-armed profile's frames
    /// through SubmitRawExtendedReport, which writes the caller's bytes
    /// verbatim, with no id and none of the profile's fixed bytes
    /// (SubmitRawExtendedReportCore). So PadForge 6.0.0's DualSense and
    /// DualShock 4 frames went out headed by the left stick's X instead of
    /// 0x01, and Windows took no input from them.</para>
    /// </summary>
    public class SonyRawFrameWireTests
    {
        /// <summary>Records which HIDMaestro call each frame reached and its
        /// bytes.</summary>
        private sealed class Recorder : IHMaestroSubmitTarget
        {
            private readonly List<(bool Verbatim, byte[] Bytes)> _raw = new();

            public void SubmitState(in HMGamepadState state) { }

            public void SubmitRawReport(ReadOnlySpan<byte> report)
            {
                lock (_raw) _raw.Add((false, report.ToArray()));
            }

            public void SubmitRawExtendedReport(ReadOnlySpan<byte> report)
            {
                lock (_raw) _raw.Add((true, report.ToArray()));
            }

            /// <summary>The first frame, waiting for a submit lane's thread
            /// when the profile has one.</summary>
            public (bool Verbatim, byte[] Bytes) First()
            {
                long until = Environment.TickCount64 + 5000;
                while (true)
                {
                    lock (_raw)
                        if (_raw.Count > 0) return _raw[0];
                    if (Environment.TickCount64 > until) throw new TimeoutException("no frame reached HIDMaestro");
                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>A connected controller with no device: its frames go to
        /// <paramref name="target"/>.</summary>
        private static HMaestroVirtualController Connected(HMProfile profile, VirtualControllerType type, IHMaestroSubmitTarget target)
        {
            var shell = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(HMContext));
            var vc = new HMaestroVirtualController(shell, profile, type)
            {
                CreateControllerSeam = (p, key) => null,
                SubmitTargetSeam = () => target,
            };
            vc.Connect();
            return vc;
        }

        [Fact]
        public void EverySonyPackedFrameReachesHIDMaestroAsDataBytes_ThatTakeReport01()
        {
            var packed = new List<string>();
            foreach (var profile in HMaestroProfileCatalog.AllProfiles)
            {
                var packer = SonyReportPackers.ForProfile(profile.Id);
                if (packer == null) continue;
                packed.Add(profile.Id);

                var gp = default(Gamepad);
                var tp = default(TouchpadState);
                var motion = default(MotionSnapshot);
                var frame = new byte[63];
                packer(in gp, in tp, in motion, 100, false, 1, 0, frame);

                var recorder = new Recorder();
                var vc = Connected(profile, VirtualControllerType.PlayStation, recorder);
                try
                {
                    Assert.False(vc.RawFramesCarryReportId, profile.Id + ": a Sony packer's frame is the report's data bytes");
                    vc.SubmitRawReport(frame);
                    var (verbatim, bytes) = recorder.First();
                    Assert.False(verbatim, profile.Id + ": the packed frame went to SubmitRawExtendedReport, which sends it with no report id");
                    Assert.Equal(frame, bytes);
                }
                finally
                {
                    vc.Dispose();
                }

                // HIDMaestro heads the data with the report id when the frame
                // is one byte short of the report the profile declares.
                var spec = profile.ExtendedReport;
                if (spec is { AlwaysArmed: true })
                {
                    Assert.Equal((byte)0x01, spec.ReportIdByte);
                    Assert.Equal(frame.Length + 1, spec.Size);
                }
                Assert.Equal(frame.Length + 1, profile.InputReportSize);
            }
            Assert.Equal(SonyReportPackers.ByProfileId.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                packed.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>The positive control beside it: a frame built with its own
        /// report id still goes out verbatim. The 2026 Steam Controller's frame
        /// carries 0x42 at byte 0 on a descriptor that leads with a mouse
        /// (HIDMaestro#58).</summary>
        [Fact]
        public void AFrameThatCarriesItsOwnIdStillGoesOutVerbatim()
        {
            var profile = HMaestroProfileCatalog.AllProfiles.First(p => p.Id == "steam-controller-2");
            var frame = new byte[profile.InputReportSize];
            frame[0] = 0x42;
            var recorder = new Recorder();
            var vc = Connected(profile, VirtualControllerType.Extended, recorder);
            try
            {
                Assert.True(vc.RawFramesCarryReportId);
                vc.SubmitRawReport(frame);
                var (verbatim, bytes) = recorder.First();
                Assert.True(verbatim);
                Assert.Equal(frame, bytes);
            }
            finally
            {
                vc.Dispose();
            }
        }
    }
}
