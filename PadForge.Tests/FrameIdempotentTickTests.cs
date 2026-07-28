using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard: Incremental and Ramped advance once per FRAME, not
    /// once per device pass.
    ///
    /// <para>The Extended/KBM/MIDI evaluators run once per assigned DEVICE
    /// per frame, and a source with an empty device GUID matches every
    /// device, so a slot with two assigned devices reaches the same
    /// (slot, target, sourceIndex) accumulator twice in a single frame.
    /// StickTrim and FlickStick were both gated against exactly this pass
    /// model. These two were not, and each pass advanced them by the full
    /// frame dt: a 2 s sweep finished in 1 s on a two-device slot and
    /// 0.66 s on three, and a Ramped attack time was wrong by the same
    /// factor.</para>
    /// </summary>
    public class FrameIdempotentTickTests
    {
        private const string Up = "Button 0";
        private const string Down = "Button 1";

        private static CustomInputState StateWith(bool up, bool down)
        {
            var s = new CustomInputState();
            s.Buttons[0] = up;
            s.Buttons[1] = down;
            return s;
        }

        private static MappingSource Incremental() => new MappingSource
        {
            Kind = "Incremental",
            Descriptor = "LeftTrigger",
            ParamUp = Up,
            ParamDown = Down,
            ParamRate = 0.5,     // full range in 2 s
            ParamMin = 0,
            ParamMax = 1,
            ParamSticky = true,
        };

        private static MappingSource Ramped() => new MappingSource
        {
            Kind = "Ramped",
            Descriptor = "LeftStickX",
            ParamUp = Up,
            ParamDown = Down,
            ParamAttackTime = 1.0,
            ParamReleaseTime = 1.0,
        };

        [Fact]
        public void Incremental_TwoDevicePassesInOneFrame_AdvanceOnce()
        {
            var oneDevice = new SourceKindRuntime();
            var twoDevices = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);

            double a = 0, b = 0;
            for (int frame = 0; frame < 20; frame++)
            {
                oneDevice.FrameSeq++;
                a = oneDevice.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);

                twoDevices.FrameSeq++;
                b = twoDevices.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
                b = twoDevices.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            }

            // 20 frames x 50 ms = 1 s at half range per second = 0.5.
            Assert.Equal(0.5, a, 3);
            Assert.Equal(a, b, 6);   // pre-fix b was 1.0: double rate
        }

        [Fact]
        public void Ramped_TwoDevicePassesInOneFrame_AdvanceOnce()
        {
            var oneDevice = new SourceKindRuntime();
            var twoDevices = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);

            double a = 0, b = 0;
            for (int frame = 0; frame < 10; frame++)
            {
                oneDevice.FrameSeq++;
                a = oneDevice.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);

                twoDevices.FrameSeq++;
                b = twoDevices.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);
                b = twoDevices.TickRamped(0, "LeftStickX", 0, Ramped(), held, 0.05);
            }

            Assert.Equal(0.5, a, 3);   // 0.5 s of a 1 s attack
            Assert.Equal(a, b, 6);
        }

        [Fact]
        public void SecondPassReplaysTheSameValue_ItDoesNotReturnZero()
        {
            // The replay must return the frame's real output. Returning 0
            // (or the pre-tick accumulator) would make a two-device slot
            // flicker between the true value and a stale one, depending on
            // which pass wrote the target last.
            var rt = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);
            rt.FrameSeq++;
            double first = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double second = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double third = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            Assert.True(first > 0);
            Assert.Equal(first, second, 6);
            Assert.Equal(first, third, 6);
        }

        [Fact]
        public void DifferentSourceIndexesKeepIndependentAccumulators()
        {
            // The gate is per (slot, target, sourceIndex): a genuinely
            // different source on the same target must still tick in the
            // same frame.
            var rt = new SourceKindRuntime();
            var held = StateWith(up: true, down: false);
            rt.FrameSeq++;
            double s0 = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            double s1 = rt.TickIncremental(0, "LeftTrigger", 1, Incremental(), held, 0.05);
            Assert.True(s0 > 0);
            Assert.Equal(s0, s1, 6);   // same math, separate accumulators

            rt.FrameSeq++;
            double s0b = rt.TickIncremental(0, "LeftTrigger", 0, Incremental(), held, 0.05);
            Assert.True(s0b > s0);     // index 0 advanced on the new frame
        }

        [Fact]
        public void ReleaseEdgeIsNotDoubleProcessed()
        {
            // Non-sticky Incremental snaps to ParamMin when neither key is
            // held. Two passes in one frame must not re-run that.
            var src = Incremental();
            src.ParamSticky = false;
            var rt = new SourceKindRuntime();

            var held = StateWith(up: true, down: false);
            for (int f = 0; f < 10; f++) { rt.FrameSeq++; rt.TickIncremental(0, "T", 0, src, held, 0.05); }

            var released = StateWith(up: false, down: false);
            rt.FrameSeq++;
            double first = rt.TickIncremental(0, "T", 0, src, released, 0.05);
            double second = rt.TickIncremental(0, "T", 0, src, released, 0.05);
            Assert.Equal(src.ParamMin, first, 6);
            Assert.Equal(first, second, 6);
        }
    }
}
