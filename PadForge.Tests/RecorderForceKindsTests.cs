using System;
using System.Linq;
using System.Reflection;
using PadForge.Engine;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Per device, the effect kinds it refused or that it takes every effect
    /// asked of it (#600 item 32): said when the refused kinds change, and
    /// once more for every device asked for an effect as the file ends.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecorderForceKindsTests
    {
        private const uint Constant = 1u << 0, Sine = 1u << 1, Spring = 1u << 7, LeftRight = 1u << 11;

        /// <summary>What the force writer tells a recording's ear.</summary>
        private sealed class Ear : PadForge.Engine.Recording.IForceTrace
        {
            public readonly System.Collections.Generic.List<string> Heard = new();
            public void Update(ISdlInputDevice device, uint effectType, bool made, bool started) { lock (Heard) Heard.Add("update " + effectType + " " + made + " " + started); }
            public void Stopped(ISdlInputDevice device) { lock (Heard) Heard.Add("stopped"); }
            public void Forcing(ISdlInputDevice device) { lock (Heard) Heard.Add("forcing"); }
            public void Failed(ISdlInputDevice device, string what, uint effectType) { lock (Heard) Heard.Add("failed " + what + " " + effectType); }
        }

        [Fact]
        public void TheForceWriter_TellsTheEar_EachEffectsKind_ARefusal_AndThatItForces()
        {
            var sink = PadForge.Engine.Recording.ForceTrace.Sink;
            var ear = new Ear();
            using var fixture = new HapticEffectFixture();
            PadForge.Engine.Recording.ForceTrace.Sink = ear;
            try
            {
                // A constant force the device takes, then a spring it will
                // not make.
                fixture.Apply(fixture.Directional());
                fixture.FailCreateOfType = (ushort)Spring;
                fixture.Apply(fixture.Condition());
            }
            finally { PadForge.Engine.Recording.ForceTrace.Sink = sink; }
            System.Collections.Generic.List<string> heard;
            lock (ear.Heard) heard = new(ear.Heard);
            Assert.Contains("forcing", heard);
            Assert.Contains("update " + Constant + " True True", heard);
            Assert.Contains("failed " + PadForge.Engine.Recording.ForceTrace.Making + " " + Spring, heard);
            Assert.Equal("making", PadForge.Engine.Recording.ForceTrace.Making);
        }

        [Fact]
        public void ASnapshot_EndsWithNoForceLines_WhereARecordingOfTheSameCaptureHasThem()
        {
            using var host = new RecorderTestHost();
            var wheel = RecorderTestKit.Pad("Test Wheel");
            var live = DispatchProxy.Create<ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            wheel.Device = live;
            host.DeviceList.Add(wheel);
            // The minute kept arms a capture, which both files are made from.
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp(), KeepLastMinute = true };
            try
            {
                var capture = recorder.CaptureForTest;
                capture.Input(wheel, RecorderTestKit.Rest());
                capture.ForceKind(live, Constant, refused: false);
                var taken = recorder.Snapshot(wait: true);
                Assert.True(taken.Ok);
                Assert.DoesNotContain(RecorderTestKit.Events(taken.Path), e => e.Layer == 'F');

                Assert.True(recorder.Start().Ok);
                string path = recorder.Stop(wait: true).Path;
                Assert.Contains(RecorderTestKit.Events(path), e => e.Layer == 'F' && e.Payload == "effects asked: constant. Takes every effect asked of it.");
            }
            finally { recorder.KeepLastMinute = false; }
        }

        [Fact]
        public void EachDevice_SaysTheKindsItRefused_OrThatItTakesEveryEffect()
        {
            using var host = new RecorderTestHost();
            var wheel = RecorderTestKit.Pad("Test Wheel");
            var pad = RecorderTestKit.Pad("Test Pad");
            var quiet = RecorderTestKit.Pad("Test Quiet Pad");
            host.DeviceList.Add(wheel);
            host.DeviceList.Add(pad);
            host.DeviceList.Add(quiet);
            var recorder = new ProblemRecorder(host) { FrozenClock = System.Diagnostics.Stopwatch.GetTimestamp() };
            Assert.True(recorder.Start().Ok);
            var capture = recorder.CaptureForTest;
            var wheelLive = DispatchProxy.Create<ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            var padLive = DispatchProxy.Create<ISdlInputDevice, RecordingDepthTests.NothingBehind>();
            wheel.Device = wheelLive;
            pad.Device = padLive;
            capture.Input(wheel, RecorderTestKit.Rest());
            capture.Input(pad, RecorderTestKit.Rest());
            capture.Input(quiet, RecorderTestKit.Rest());

            // The wheel takes a constant force and refuses the spring. The
            // pad takes its rumble and refuses a sine.
            capture.ForceKind(wheelLive, Constant, refused: false);
            capture.ForceKind(wheelLive, Spring, refused: true);
            capture.ForceKind(padLive, LeftRight, refused: false);
            capture.ForceKind(padLive, Sine, refused: true);
            recorder.WaitForFlush();
            recorder.WaitForFlush();
            // Later the wheel takes the spring.
            capture.ForceKind(wheelLive, Spring, refused: false);
            recorder.WaitForFlush();
            recorder.WaitForFlush();

            string path = recorder.Stop(wait: true).Path;
            var feedback = RecorderTestKit.Events(path).Where(e => e.Layer == 'F').Select(e => (e.Subject, e.Payload)).ToList();
            Assert.Contains(("D1", "refused effects: spring"), feedback);
            Assert.Contains(("D1", "takes every effect asked of it"), feedback);
            Assert.Contains(("D1", "effects asked: constant, spring. Takes every effect asked of it."), feedback);
            Assert.Contains(("D2", "refused effects: sine"), feedback);
            Assert.Contains(("D2", "effects asked: sine, left and right motors. Refused: sine."), feedback);
            // A device asked for no effect says nothing of effects.
            Assert.DoesNotContain(feedback, f => f.Subject == "D3");
        }
    }
}
