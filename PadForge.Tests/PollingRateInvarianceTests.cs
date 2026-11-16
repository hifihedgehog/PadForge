using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a mapping does must not change with how often the engine runs.
    /// Each rule here that once counted passes, or sized something by them,
    /// is run on the loop's own schedule (<see cref="PollSchedule"/>) at
    /// several rates and held to one answer. The clock is the test's: ten
    /// million ticks a second, a sleep that ends on a half millisecond
    /// grid as Windows ends one, and a pass that costs 37 microseconds, as
    /// one measured at 10000 passes a second (2026-11-16). So at 4000 and
    /// 10000 the passes come as the loop runs them, two and five at a
    /// wake, the first long and the rest short.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class PollingRateInvarianceTests : IDisposable
    {
        private const long Second = 10_000_000;
        private const long Grid = 5_000;
        private const long PassCost = 370;

        private readonly Func<float> _savedPollHz = SourceCoercion.PollHzProvider;
        private readonly Func<int, string, int, PadForge.Engine.Touchpad.TouchpadGestureSettings> _savedTouchFeel =
            SourceCoercion.TouchpadMouseSettingsProvider;

        public void Dispose()
        {
            SourceCoercion.PollHzProvider = _savedPollHz;
            SourceCoercion.TouchpadMouseSettingsProvider = _savedTouchFeel;
            // A frame of a millisecond or more, so no later test reads on
            // the last short pass these left behind.
            SourceCoercion.BeginPollFrame();
        }

        /// <summary>When each pass of a run starts, in ticks.</summary>
        private static List<long> PassStarts(int hz, double seconds)
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, hz, now);
            var starts = new List<long>();
            long end = (long)(seconds * Second);
            while (now < end)
            {
                starts.Add(now);
                now += PassCost;
                long until = schedule.Next(now);
                if (until == 0) continue;
                now = (until + Grid - 1) / Grid * Grid;
                schedule.Woke(now);
            }
            return starts;
        }

        private static double Seconds(long ticks) => ticks / (double)Second;

        /// <summary>The pass length the loop hands the evaluator: the
        /// pass's own above the reference rate, and none at or under
        /// it.</summary>
        private static double ShortPass(int hz, double passSeconds)
            => hz > SourceCoercion.ReferencePassHz ? passSeconds : 0;

        private static int s_slot = 7300;
        private static int NewSlot() => System.Threading.Interlocked.Increment(ref s_slot);

        // ── The schedule itself ──

        [Fact]
        public void AboveTheWakesRateThePassesComeSeveralAtAWake()
        {
            // The premise of the rest: these runs are not evenly spaced.
            var starts = PassStarts(10000, 0.1);
            var gaps = Enumerable.Range(1, starts.Count - 1).Select(i => starts[i] - starts[i - 1]).ToList();
            Assert.Contains(gaps, g => g == PassCost);
            Assert.Contains(gaps, g => g > 5 * PassCost);
            Assert.InRange(starts.Count, 990, 1010);
        }

        // ── A stick fling (the stick trackball) ──

        [Theory]
        [InlineData(250)]
        [InlineData(1000)]
        [InlineData(4000)]
        [InlineData(10000)]
        public void AFlingLaunchesAtThePushesSpeed(int hz)
        {
            int slot = NewSlot();
            var starts = PassStarts(hz, 0.2);
            double launch = double.NaN;
            for (int i = 1; i < starts.Count; i++)
            {
                double t = Seconds(starts[i]);
                float dt = (float)Seconds(starts[i] - starts[i - 1]);
                // Pushed to 0.9 for 50 ms and let go: the stick snaps back
                // to center over the next 40 ms, as a released one does.
                float fx = t < 0.050 ? 0.9f : t < 0.090 ? (float)(0.9 * (0.090 - t) / 0.040) : 0f;
                float counts = SourceCoercion.TickStickCoast(slot, "d", fx, 0f, dt, starts[i], Second, true, 0.90f).X;
                if (fx == 0f) { launch = counts / dt; break; }
            }
            // 0.9 of the full 1200 counts a second, less one pass of
            // friction. With an entry a pass the ring reached back 12.8 ms
            // at 10000 passes a second and launched at 0.29.
            Assert.InRange(launch, 0.9 * 1200 * 0.97, 0.9 * 1200 * 1.001);
        }

        [Fact]
        public void TheFlingRingReachesBackTheWholeWindow()
        {
            var ball = typeof(SourceCoercion).GetNestedType("StickBall", BindingFlags.NonPublic);
            int ringLen = (int)ball.GetField("RingLen").GetRawConstantValue();
            float entry = (float)ball.GetField("EntrySeconds").GetRawConstantValue();
            float window = (float)typeof(SourceCoercion)
                .GetField("StickFlingWindowSeconds", BindingFlags.NonPublic | BindingFlags.Static)
                .GetRawConstantValue();
            Assert.True((ringLen - 1) * (double)entry >= window - 1e-6,
                $"{ringLen} entries {entry} s apart reach back less than the {window} s window");
        }

        // ── Flick stick smoothing ──

        private static CustomInputState StickAt(double angleDeg, double len)
        {
            double rad = angleDeg * Math.PI / 180.0;
            var s = new CustomInputState();
            s.Axis[3] = 32768 + (int)Math.Round(-len * Math.Sin(rad) * 32767);
            s.Axis[4] = 32768 + (int)Math.Round(-len * Math.Cos(rad) * 32767);
            return s;
        }

        [Theory]
        [InlineData(250)]
        [InlineData(1000)]
        [InlineData(4000)]
        [InlineData(10000)]
        public void ASmallTurnOfAFlickStickArrivesOverTheSameTime(int hz)
        {
            SourceCoercion.PollHzProvider = () => hz;
            var runtime = new SourceKindRuntime();
            var src = new MappingSource
            {
                Descriptor = "Flick Stick Right",
                // A hundred times JSM's 14400 counts a turn, so the 0.3
                // degree step below is 1200 counts and its arrival can be
                // timed to a pass. The smoothing's thresholds scale with it.
                ParamFlickCountsPer360 = 1_440_000,
                ParamFlickSnapMode = "None",
                ParamFlickSnapStrength = 1.0,
                ParamFlickDeadzoneAngle = 181,   // every flick folds to 0: the counts are the turn's alone
                ParamFlickSmooth = -1,
                ParamFlickOnEngage = false,
                ParamFlickTime = 0.1,
            };
            const double stepAt = 0.15, step = 1200;
            var starts = PassStarts(hz, 0.30);
            long seq = 1;
            double total = 0, sawStepAt = double.NaN, atHalfTheWindow = double.NaN, allInAt = double.NaN;
            for (int i = 1; i < starts.Count; i++)
            {
                double t = Seconds(starts[i]);
                double dt = Seconds(starts[i] - starts[i - 1]);
                var state = t < 0.05 ? StickAt(0, 0) : t < stepAt ? StickAt(-90, 1) : StickAt(-90.3, 1);
                int counts = runtime.TickFlickStick(0, "KbmMouseX", 0, src, state, dt, seq++);
                if (t < stepAt) { Assert.Equal(0, counts); continue; }
                if (double.IsNaN(sawStepAt)) sawStepAt = t;
                total += counts;
                if (double.IsNaN(atHalfTheWindow) && t >= sawStepAt + 0.032) atHalfTheWindow = total;
                if (double.IsNaN(allInAt) && total >= step * 0.995) allInAt = t - sawStepAt;
            }
            // Half of it in half the 64 ms window, give or take the pass
            // that carries a share on arrival, and all of it by the
            // window's end. Sized by 256 samples the window was 25.6 ms at
            // 10000 passes a second, and all of it was in by then.
            Assert.InRange(atHalfTheWindow, step * 0.47, step * 0.60);
            Assert.InRange(allInAt, 0.058, 0.066);
            // The stick's two axes are whole numbers, so the turn is a few
            // counts off 0.3 degrees.
            Assert.InRange(total, step * 0.99, step * 1.01);
        }

        [Theory]
        [InlineData(60, 4)]
        [InlineData(144, 10)]
        [InlineData(250, 16)]
        [InlineData(1000, 64)]
        [InlineData(4000, 256)]
        [InlineData(10000, 640)]
        public void TheFlickWindowIsSizedFromTheRateTheAppNames(int hz, int samples)
        {
            SourceCoercion.PollHzProvider = () => hz;
            // Whatever the pass's own length.
            Assert.Equal(samples, SourceKindRuntime.FlickWindowSamples(0.00004));
            Assert.Equal(samples, SourceKindRuntime.FlickWindowSamples(0.004));
        }

        [Fact]
        public void WithNoRateNamedTheFlickWindowIsSizedFromThePass()
        {
            SourceCoercion.PollHzProvider = null;
            Assert.Equal(16, SourceKindRuntime.FlickWindowSamples(0.004));
            Assert.Equal(64, SourceKindRuntime.FlickWindowSamples(0.001));
        }

        [Fact]
        public void TheFlickWindowFitsItsSamplesAtTheFastestRate()
            => Assert.True(SourceKindRuntime.FlickNumSamples * 1000
                >= SourceKindRuntime.FlickWindowMs * InputManager.MaxPollingRateHz);

        // ── A finger's movement on a scroll row ──

        private delegate bool TouchAxisRead(CustomInputState state, MappingSource src, string descriptor,
            int slotIndex, string evaluatedDeviceGuid, out float bipolar);

        [Theory]
        [InlineData(1000)]
        [InlineData(2000)]
        [InlineData(4000)]
        [InlineData(10000)]
        public void AFingersMovementTurnsTheWheelAsFar(int hz)
        {
            var read = (TouchAxisRead)typeof(SourceCoercion)
                .GetMethod("TryReadTouchpadAxis", BindingFlags.NonPublic | BindingFlags.Static)
                .CreateDelegate(typeof(TouchAxisRead));
            float scale = (float)typeof(SourceCoercion)
                .GetField("TouchpadDeltaScale", BindingFlags.NonPublic | BindingFlags.Static)
                .GetRawConstantValue();
            SourceCoercion.TouchpadMouseSettingsProvider = null;
            const string device = "7a7a7a7a-0000-0000-0000-00000000007a";
            const string descriptor = "Touchpad 0 Finger 0 X";
            int slot = NewSlot();
            var pad = new TouchpadInputState(2);
            var state = new CustomInputState { Touchpads = new[] { pad } };
            var src = new MappingSource { Kind = "Direct", DeviceGuid = device, Descriptor = descriptor };
            try
            {
                pad.FingerDown[0] = true;
                pad.FingerX[0] = 0.2f;
                const int reports = 100;
                const float perReport = 0.002f;
                // What the wheel turns by: the row's deflection times the
                // time it stood, summed.
                double spent = 0;
                var starts = PassStarts(hz, 0.5);
                for (int i = 1; i < starts.Count; i++)
                {
                    double t = Seconds(starts[i]);
                    double dt = Seconds(starts[i] - starts[i - 1]);
                    // The pad reports every 4 ms, and the finger crosses
                    // 0.002 of it a report for 0.4 s.
                    long report = Math.Min((long)(t / 0.004), reports);
                    pad.FingerX[0] = 0.2f + perReport * report;
                    SourceCoercion.BeginPollFrame(0, ShortPass(hz, dt));
                    Assert.True(read(state, src, descriptor, slot, device, out float deflection));
                    spent += deflection * dt;
                }
                // Each report's movement, read as a deflection, for one
                // reference pass. Read on the pass that saw it alone, a
                // movement at 10000 passes a second stood for 0.04 to
                // 0.35 ms.
                double expected = reports * perReport * scale * SourceCoercion.ReferencePassSeconds;
                Assert.InRange(spent, expected * 0.97, expected * 1.03);
            }
            finally
            {
                pad.FingerDown[0] = false;
                read(state, src, descriptor, slot, device, out _);
            }
        }

        [Fact]
        public void APassOfAMillisecondOrMoreReadsTheMovementItself()
        {
            // At the rates the engine had before, nothing changes: 250
            // passes a second read a report's movement for their whole
            // 4 ms, as they always did.
            var delta = typeof(SourceCoercion).GetNestedType("TouchpadAxisDelta", BindingFlags.NonPublic);
            object tracker = Activator.CreateInstance(delta);
            var spend = delta.GetMethod("Spend");
            Assert.Equal(0.25f, (float)spend.Invoke(tracker, new object[] { 0.25f, 0.0 }));
            Assert.Equal(0f, (float)spend.Invoke(tracker, new object[] { 0f, 0.0 }));
            Assert.Equal(-0.1f, (float)spend.Invoke(tracker, new object[] { -0.1f, 0.0 }));
        }

        // ── The legacy gyro smoothing ──

        [Theory]
        [InlineData(1000)]
        [InlineData(2000)]
        [InlineData(4000)]
        [InlineData(10000)]
        public void TheLegacyGyroSmoothingSettlesInTheSameTime(int hz)
        {
            string device = "ema-rate-" + hz;
            int slot = NewSlot();
            var starts = PassStarts(hz, 0.05);
            float settled = 0f;
            for (int i = 1; i < starts.Count && Seconds(starts[i]) <= 0.020; i++)
            {
                SourceCoercion.BeginPollFrame(0, ShortPass(hz, Seconds(starts[i] - starts[i - 1])));
                settled = SourceCoercion.ApplyGyroSmoothing(device, slot, 0, 10f, 0.9f);
            }
            // Twenty milliseconds of a weight of 0.9 a millisecond. Applied
            // once a pass, 10000 passes a second had settled all the way.
            double expected = 10 * (1 - Math.Pow(0.9, 20));
            Assert.InRange(settled, expected * 0.96, expected * 1.04);
        }

        // ── The Wii pointer's FPS Mouse ──

        [Fact]
        public void FpsMouseScalesAShortPassAsTheReferencePass()
        {
            Assert.Equal(0.001f, InputManager.FpsPassSeconds(0, 123, Second));
            Assert.Equal(0.001f, InputManager.FpsPassSeconds(1000, 1000 + PassCost, Second));
            Assert.Equal(0.001f, InputManager.FpsPassSeconds(1000, 1000 + 1_000, Second));
            Assert.Equal(0.004f, InputManager.FpsPassSeconds(1000, 1000 + 40_000, Second), 6);
            Assert.Equal(0.05f, InputManager.FpsPassSeconds(1000, 1000 + Second, Second));
        }

        // ── Frames that go out on every pass ──

        [Fact]
        public void AStillPadsFramesStayNearAThousandASecond()
        {
            var gate = new EveryPassFrameGate();
            int sent = 0;
            foreach (long start in PassStarts(10000, 1.0))
                if (gate.Due(changed: false, start * Stopwatch.Frequency / Second)) sent++;
            // One a millisecond on the half millisecond grid, where every
            // pass sent 10000.
            Assert.InRange(sent, 990, 1340);
        }

        [Fact]
        public void AFrameThatSaysSomethingNewGoesOutOnItsOwnPass()
        {
            var gate = new EveryPassFrameGate();
            long spacing = EveryPassFrameGate.Spacing;
            Assert.True(gate.Due(changed: false, 1_000));               // the first
            Assert.False(gate.Due(changed: false, 1_000 + 1));          // nothing new, too soon
            Assert.True(gate.Due(changed: true, 1_000 + 2));            // something new: at once
            Assert.False(gate.Due(changed: false, 1_000 + 2 + spacing - 1));
            Assert.True(gate.Due(changed: false, 1_000 + 2 + spacing)); // nothing new, spaced
        }

        [Fact]
        public void EverySenderOfAFrameAPassAsksTheGate()
        {
            string step5 = Read("PadForge.App/Common/Input/InputManager.Step5.VirtualDevices.cs");
            // The Valve, SInput and Sony personas, each packed twice: once
            // to ask, with the counter and clocks at zero, and once to send.
            Assert.Equal(3, Count(step5, "if (EveryPassFrameGate.Engaged)"));
            Assert.Equal(3, Count(step5, ".RawReportDue("));
            // A frame counter counts the frames sent.
            Assert.Equal(1, Count(step5, "_sonyFrameCounter++"));
            Assert.Equal(1, Count(step5, "_deckFrameCounter++"));
            Assert.Contains("_sonyFrameCounter++", Between(step5, "if (sonyDue)", "hmPs.SubmitRawReport(rawReportScratch);"));
            Assert.Contains("_deckFrameCounter++", Between(step5, "if (valveDue)", "hmExt.SubmitRawReport(valveFrame);"));

            // The extended PlayStation state and a raw frame with motion.
            string controller = Read("PadForge.App/Common/Input/HMaestroVirtualController.cs");
            Assert.Equal(2, Count(controller, "if (EveryPassFrameGate.Engaged)"));
            Assert.Equal(3, Count(controller, "_everyPassGate.Due("));

            Assert.Contains("if (EveryPassFrameGate.Engaged)", Read("PadForge.App/Common/Input/HMaestroVRController.cs"));
            Assert.Contains("EveryPassFrameGate.Engaged)", Read("PadForge.App/Services/DsuMotionServer.cs"));

            // And the loop engages it only above the rate a frame a pass
            // was always sent at.
            Assert.Contains("bool spaceFrames = rateHz > EveryPassFrameGate.EveryPassUpToHz;",
                Read("PadForge.App/Common/Input/InputManager.cs"));
            Assert.Equal(1000, EveryPassFrameGate.EveryPassUpToHz);
        }

        // ── The loop tells the evaluator about a short pass ──

        [Fact]
        public void TheLoopHandsTheEvaluatorAPassShorterThanTheReference()
        {
            string loop = Read("PadForge.App/Common/Input/InputManager.cs");
            Assert.Contains("if (rateHz > Engine.Common.Mapping.SourceCoercion.ReferencePassHz)", loop);
            Assert.Contains("Engine.Common.Mapping.SourceCoercion.BeginPollFrame(pollRun, shortPassSeconds);", loop);
            Assert.Equal(1000, SourceCoercion.ReferencePassHz);
            Assert.Equal(0.001, SourceCoercion.ReferencePassSeconds);
        }

        private static string Root()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }

        private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

        private static int Count(string text, string part)
            => text.Split(new[] { part }, StringSplitOptions.None).Length - 1;

        private static string Between(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, start);
            int b = text.IndexOf(end, a, StringComparison.Ordinal);
            Assert.True(b > a, end);
            return text[a..b];
        }
    }

    /// <summary>
    /// The comparisons that decide whether a frame says something new
    /// (<see cref="FrameContent"/>). Each field of a state is changed alone:
    /// a field the comparison leaves out reads as the same frame, and a
    /// change in it would wait for the gate's spacing.
    /// </summary>
    public class FrameContentTests
    {
        [Fact]
        public void EveryFieldOfAGamepadCounts()
            => EveryFieldCounts<Gamepad>((a, b) => FrameContent.Same(in a, in b));

        [Fact]
        public void EveryFieldOfATouchpadCounts()
            => EveryFieldCounts<TouchpadState>((a, b) => FrameContent.Same(in a, in b));

        [Fact]
        public void EveryFieldOfTheButtonPressuresCounts()
            => EveryFieldCounts<ButtonPressureState>((a, b) => FrameContent.Same(in a, in b));

        [Fact]
        public void EveryFieldOfAVrStateCounts()
        {
            EveryFieldCounts<VrRawState>((a, b) => FrameContent.Same(in a, in b));
            EveryFieldCounts<VrHandRaw>((a, b) => FrameContent.Same(in a, in b));
        }

        [Fact]
        public void EveryFieldOfTheMotionCountsButItsClock()
            => EveryFieldCounts<MotionSnapshot>((a, b) => FrameContent.Same(in a, in b),
                nameof(MotionSnapshot.TimestampUs));

        private static void EveryFieldCounts<T>(Func<T, T, bool> same, params string[] clocks) where T : struct
        {
            Assert.True(same(default, default));
            var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotEmpty(fields);
            foreach (var field in fields)
            {
                object boxed = default(T);
                field.SetValue(boxed, Changed(field.FieldType));
                bool readsTheSame = same((T)boxed, default);
                Assert.True(readsTheSame == clocks.Contains(field.Name),
                    $"{typeof(T).Name}.{field.Name} changed and the frame read as {(readsTheSame ? "the same" : "new")}");
            }
        }

        private static object Changed(Type type)
        {
            if (type == typeof(bool)) return true;
            if (type.IsPrimitive) return Convert.ChangeType(1, type);
            // A state inside a state: its first field changed.
            object inner = Activator.CreateInstance(type);
            var first = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
            first.SetValue(inner, Changed(first.FieldType));
            return inner;
        }
    }

    /// <summary>
    /// The virtual DualShock 4's frame timestamp. A reader takes the
    /// difference between two frames' timestamps as the time between them
    /// (SDL_hidapi_ps4.c, DS4Windows DS4Device.cs), so it is the frame's
    /// time, in units of 16/3 of a microsecond.
    /// </summary>
    public class Ds4TimestampTests
    {
        [Theory]
        [InlineData(0u, 0)]
        [InlineData(16u, 3)]
        [InlineData(1_000u, 187)]
        [InlineData(4_000u, 750)]
        [InlineData(349_525u, 65_535)]
        [InlineData(349_526u, 0)]
        public void ItCountsSixteenThirdsOfAMicrosecond(uint micros, int expected)
            => Assert.Equal((ushort)expected, SonyReportPackers.Ds4Timestamp(micros));

        [Fact]
        public void ItRunsOnAcrossTheWrapOfTheClock()
        {
            ushort before = SonyReportPackers.Ds4Timestamp(uint.MaxValue);
            ushort after = SonyReportPackers.Ds4Timestamp(0);
            Assert.InRange(unchecked((ushort)(after - before)), 0, 1);
        }

        [Theory]
        [InlineData("dualshock-4-v1")]
        [InlineData("dualshock-4-v2")]
        [InlineData("dualshock-4-v2-composite")]
        public void AFramesTimestampIsItsTimeWhateverItsCount(string profile)
        {
            static ushort Stamp(string profile, uint frameCounter, uint micros)
            {
                var gp = default(Gamepad);
                var tp = default(TouchpadState);
                var motion = default(MotionSnapshot);
                var dest = new byte[63];
                SonyReportPackers.ForProfile(profile)(in gp, in tp, in motion, 100, false, frameCounter, micros, dest);
                return (ushort)(dest[9] | dest[10] << 8);
            }
            // The same time on two frames: the same stamp, where a count of
            // frames gave 188 more.
            Assert.Equal(Stamp(profile, 7, 100_000), Stamp(profile, 8, 100_000));
            // Two frames 0.1 ms apart, as at 10000 a second, are 18 or 19
            // units apart. Counted in frames they were 188, a millisecond.
            int apart = unchecked((ushort)(Stamp(profile, 2, 100_100) - Stamp(profile, 1, 100_000)));
            Assert.InRange(apart, 18, 19);
            // And 4 ms apart, as a real pad's are on USB, 750.
            Assert.Equal(750, unchecked((ushort)(Stamp(profile, 2, 104_000) - Stamp(profile, 1, 100_000))));
        }
    }
}
