using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The poll loop's schedule, driven by a clock of the test's own:
    /// ten million ticks a second, as Windows counts them, and a sleep that
    /// ends on the next tick of a half millisecond grid, as Windows ends
    /// one.</summary>
    public class PollScheduleTests
    {
        private const long Second = 10_000_000;
        private const long Grid = 5_000;

        /// <summary>Runs a loop for <paramref name="seconds"/>: each pass
        /// takes <paramref name="passCost"/> ticks, and a sleep ends on the
        /// grid's first tick at or after the time asked for. Returns when
        /// each pass started.</summary>
        private static List<long> Run(int hz, double seconds, long passCost = 100, long grid = Grid, long start = 1_234)
        {
            long now = start;
            var schedule = new PollSchedule(Second, grid, hz, now);
            var starts = new List<long>();
            long end = start + (long)(seconds * Second);
            while (now < end)
            {
                starts.Add(now);
                now += passCost;
                long until = schedule.Next(now);
                if (until == 0) continue;
                Assert.True(until > now, "a sleep was asked for a time that had passed");
                now = (until + grid - 1) / grid * grid;
                schedule.Woke(now);
            }
            return starts;
        }

        [Theory]
        [InlineData(60)]
        [InlineData(144)]
        [InlineData(250)]
        [InlineData(1000)]
        [InlineData(2000)]
        [InlineData(4000)]
        [InlineData(8000)]
        [InlineData(10000)]
        public void TheRateHoldsOverTenSeconds(int hz)
        {
            var starts = Run(hz, 10);
            // A sleep ends up to one grid interval late, so the count is
            // within a pass or two of the rate's own.
            Assert.InRange(starts.Count, hz * 10 - 2, hz * 10 + 2);
        }

        [Theory]
        [InlineData(144)]
        [InlineData(333)]
        [InlineData(7001)]
        public void ARateThatDoesNotDivideTheClockDoesNotDrift(int hz)
        {
            // An interval rounded to whole ticks loses or gains the same part
            // of a tick every pass. 7001 passes a second would run 2 a
            // second fast on an interval rounded down.
            var starts = Run(hz, 100, passCost: 10);
            Assert.InRange(starts.Count, hz * 100 - 2, hz * 100 + 2);
        }

        [Fact]
        public void AtOrUnderTheGridsRateEachPassHasItsOwnWake()
        {
            foreach (int hz in new[] { 250, 1000, 2000 })
            {
                // From a tick of the grid, so the first sleep is a whole one.
                var starts = Run(hz, 2, start: 0);
                long period = Second / hz;
                for (int i = 1; i < starts.Count; i++)
                    Assert.Equal(period, starts[i] - starts[i - 1]);
            }
        }

        [Fact]
        public void AboveTheGridsRateThePassesOfOneWakeRunBackToBack()
        {
            // 10000 a second on a half millisecond grid: five passes are due
            // at each wake, the first after the sleep and four behind it.
            var starts = Run(10000, 2, passCost: 100, start: 0);
            var gaps = Enumerable.Range(1, starts.Count - 1).Select(i => starts[i] - starts[i - 1]).ToList();
            int backToBack = gaps.Count(g => g == 100);
            int afterASleep = gaps.Count(g => g > 100);
            Assert.InRange(afterASleep, 2 * 2000 - 2, 2 * 2000 + 2);
            Assert.InRange(backToBack, 4 * afterASleep - 8, 4 * afterASleep + 8);
            // No pass ran while none was due: a wake never comes sooner than
            // the grid's next tick.
            Assert.All(gaps.Where(g => g > 100), g => Assert.True(g >= Grid - 5 * 100, "gap " + g));
        }

        /// <summary>A pass that took far longer than the bound drops the
        /// passes it missed, and the loop sleeps a whole interval before the
        /// next: the stall used up the tick's time a burst may have. The
        /// pass after a stall ran at once, so a loop whose every pass took
        /// that long never slept.</summary>
        [Fact]
        public void AStallIsFollowedByNoBurst()
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, 1000, now);
            for (int i = 0; i < 10; i++)
            {
                now += 100;
                long until = schedule.Next(now);
                Assert.NotEqual(0, until);
                now = until;
                schedule.Woke(now);
            }
            // A pass that took fifty milliseconds, as a device sweep can.
            now += 50 * Second / 1000;
            long after = schedule.Next(now);
            Assert.Equal(now + Second / 1000, after);   // a whole interval from the stall's end
            now = after;
            schedule.Woke(now);
            now += 100;
            Assert.Equal(after + Second / 1000, schedule.Next(now)); // and on at the rate, nothing owed
        }

        /// <summary>A wake that came far behind the schedule, as after a
        /// resume from sleep, starts the schedule again from that moment:
        /// within the tick's time after the wake the next pass is due at
        /// once, and none of the missed ones follow.</summary>
        [Fact]
        public void AWakeFarBehindStartsTheScheduleAgain()
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, 1000, now);
            now += 100;
            long until = schedule.Next(now);
            now = until + 50 * Second / 1000;   // the wake came fifty milliseconds late
            schedule.Woke(now);
            now += 100;
            Assert.Equal(0, schedule.Next(now));
            long start = now;
            now += 100;
            Assert.Equal(start + Second / 1000, schedule.Next(now));
        }

        /// <summary>A processor whose every pass outlasts the stall bound and
        /// an interval still sleeps after each pass. The stall branch
        /// answered "run now" every time, so the loop never slept at the
        /// poll thread's raised priority (#331).</summary>
        [Theory]
        [InlineData(10000, 30_000)]   // 3 ms passes at 10000 a second
        [InlineData(1000, 60_000)]    // 6 ms passes at 1000 a second
        [InlineData(60, 900_000)]     // 90 ms passes at 60 a second
        public void APassLongerThanTheStallBoundIsFollowedByASleep(int hz, long passCost)
        {
            var starts = Run(hz, 2, passCost: passCost);
            var gaps = Enumerable.Range(1, starts.Count - 1).Select(i => starts[i] - starts[i - 1]).ToList();
            Assert.NotEmpty(gaps);
            Assert.All(gaps, g => Assert.True(g > passCost, "a pass ran back to back after one of " + passCost + " ticks"));
        }

        [Fact]
        public void AWakeThatLandsLateIsCaughtUp()
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, 4000, now);
            now += 100;
            long until = schedule.Next(now);
            Assert.Equal(2_500, until);
            // The wake lands a millisecond late: five passes are owed, the
            // one that was due and the four that came due meanwhile.
            now = until + 10_000;
            schedule.Woke(now);
            int ran = 0;
            while (true)
            {
                ran++;
                now += 100;
                until = schedule.Next(now);
                if (until != 0) break;
            }
            Assert.Equal(5, ran);
            Assert.Equal(15_000, until);
        }

        [Fact]
        public void AProcessorThatCannotHoldTheRateStillSleeps()
        {
            // 10000 a second with passes of 0.15 ms: 1.5 processors' work.
            var starts = Run(10000, 2, passCost: 1_500);
            var gaps = Enumerable.Range(1, starts.Count - 1).Select(i => starts[i] - starts[i - 1]).ToList();
            int sleeps = gaps.Count(g => g > 1_500);
            // It sleeps at least every other grid interval, where a loop
            // that ran every owed pass would never sleep at all.
            Assert.True(sleeps >= 2 * 2000 / 2 - 2, sleeps + " sleeps in two seconds");
            // And no run of passes back to back lasts much past one grid
            // interval.
            long run = 0, longest = 0;
            foreach (long gap in gaps)
            {
                run = gap == 1_500 ? run + gap : 0;
                longest = Math.Max(longest, run);
            }
            Assert.True(longest <= Grid + 1_500, "passes ran back to back for " + longest + " ticks");
        }

        [Fact]
        public void ARateChangeTakesEffectAtTheNextPass()
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, 1000, now);
            now += 100;
            Assert.Equal(10_000, schedule.Next(now));
            now = 10_000;
            schedule.Woke(now);
            schedule.SetRate(250);
            now += 100;
            Assert.Equal(10_000 + 40_000, schedule.Next(now));
            Assert.Equal(250, schedule.Hz);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void ARateUnderOneIsRefused(int hz)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new PollSchedule(Second, Grid, hz, 0));

        [Theory]
        [InlineData(250, 0)]        // a pass on the grid starts on its time
        [InlineData(1000, 0)]
        [InlineData(4000, 2_500)]   // every other pass falls between two ticks
        [InlineData(10000, 4_000)]  // the first of five waits four passes' time
        public void APassStartsNoLaterThanTheNextWakeAfterItWasDue(int hz, long worstExpected)
        {
            long now = 0;
            var schedule = new PollSchedule(Second, Grid, hz, now);
            long worst = 0;
            for (int pass = 0; pass < hz * 2; pass++)
            {
                long late = now - schedule.Due;
                Assert.True(late >= 0, "a pass ran " + -late + " ticks before it was due");
                worst = Math.Max(worst, late);
                now += 100;
                long until = schedule.Next(now);
                if (until == 0) continue;
                now = (until + Grid - 1) / Grid * Grid;
                schedule.Woke(now);
            }
            Assert.Equal(worstExpected, worst);
        }

        [Fact]
        public void TheStallBoundIsTwoMillisecondsOrFourPasses()
        {
            Assert.Equal(Second / 500, new PollSchedule(Second, Grid, 10000, 0).StallBound);
            Assert.Equal(Second / 500, new PollSchedule(Second, Grid, 2000, 0).StallBound);
            Assert.Equal(4 * (Second / 60), new PollSchedule(Second, Grid, 60, 0).StallBound);
        }
    }

    /// <summary>The polling rate as a setting.</summary>
    public class PollingRatesTests
    {
        [Theory]
        [InlineData(0, 1000, 1000)]     // no override: the global rate
        [InlineData(4000, 1000, 4000)]  // an override outranks it
        [InlineData(0, 10, 60)]         // either is held to the loop's range
        [InlineData(0, 50000, 10000)]
        [InlineData(20000, 1000, 10000)]
        [InlineData(30, 1000, 60)]
        [InlineData(-1, 250, 250)]
        public void TheOverrideWinsWhenItHasOne(int overrideHz, int globalHz, int expected)
            => Assert.Equal(expected, PollingRates.Resolve(overrideHz, globalHz));

        [Theory]
        [InlineData(1, 1000)]
        [InlineData(2, 500)]
        [InlineData(3, 333)]
        [InlineData(4, 250)]
        [InlineData(8, 125)]
        [InlineData(16, 63)]
        [InlineData(0, 1000)]   // out of the old range: held to it first
        [InlineData(40, 63)]
        public void AnOldIntervalBecomesItsRate(int intervalMs, int expected)
            => Assert.Equal(expected, PollingRates.FromIntervalMs(intervalMs));

        [Fact]
        public void EveryOldIntervalIsARateTheLoopRunsAt()
        {
            for (int ms = 1; ms <= 16; ms++)
                Assert.InRange(PollingRates.FromIntervalMs(ms), InputManager.MinPollingRateHz, InputManager.MaxPollingRateHz);
        }

        [Fact]
        public void TheChoicesRunFastestFirstInsideTheRange()
        {
            Assert.Equal(PollingRates.Choices.OrderByDescending(hz => hz), PollingRates.Choices);
            Assert.Equal(InputManager.MaxPollingRateHz, PollingRates.Choices[0]);
            Assert.Equal(InputManager.MinPollingRateHz, PollingRates.Choices[^1]);
            Assert.Contains(InputManager.DefaultPollingRateHz, PollingRates.Choices);
        }

        [Theory]
        [InlineData(1000, "en-US", "1000 Hz (1 ms)")]
        [InlineData(4000, "en-US", "4000 Hz (0.25 ms)")]
        [InlineData(10000, "en-US", "10000 Hz (0.1 ms)")]
        [InlineData(60, "en-US", "60 Hz (16.667 ms)")]
        [InlineData(4000, "de-DE", "4000 Hz (0,25 ms)")]
        public void ARateIsLabeledWithItsInterval(int hz, string culture, string expected)
            => Assert.Equal(expected, PollingRates.Label(hz, System.Globalization.CultureInfo.GetCultureInfo(culture), "Hz", "ms"));

        [Fact]
        public void ARatesLabelTakesItsUnitsFromTheInterfaceLanguage()
            => Assert.Equal("4000 Гц (0,25 мс)",
                PollingRates.Label(4000, System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), "Гц", "мс"));

        /// <summary>The rate Home and the heartbeat show, in whole hertz
        /// (PollRateMeter), driven by the loop's own schedule on a test
        /// clock: every second reads the rate though each wake comes up to
        /// half a millisecond late, and the second with a 20 ms stall reads
        /// lower, by the passes the stall dropped. Timed by when its passes
        /// ended, the same loop reads a hertz or more off now and then: here
        /// at 4000 and 10000, where several passes end at each wake, as Home
        /// showed 1001 Hz at 1000.</summary>
        [Theory]
        [InlineData(250)]
        [InlineData(1000)]
        [InlineData(4000)]
        [InlineData(10000)]
        public void TheShownRateHoldsThroughLateWakes_AndAStallShowsWhatItDropped(int hz)
        {
            const long Frequency = 10_000_000, Grid = Frequency / 2000, Work = 200;
            string Shown(double rate) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0} Hz", rate);
            var random = new Random(hz);
            long now = 0;
            var schedule = new PollSchedule(Frequency, Grid, hz, now);
            var byDue = new PollRateMeter();
            var byEnd = new PollRateMeter();
            var shown = new List<double>();
            var ended = new List<double>();
            for (int pass = 0; pass < hz * 6; pass++)
            {
                if (pass == hz * 4) now += Frequency / 50;
                if (byDue.TryPass(schedule.Due, Frequency, out double rate)) shown.Add(rate);
                now += Work;
                if (byEnd.TryPass(now, Frequency, out double endRate)) ended.Add(endRate);
                long sleepUntil = schedule.Next(now);
                if (sleepUntil != 0)
                {
                    now = sleepUntil + random.Next(0, 5200);
                    schedule.Woke(now);
                }
            }

            Assert.True(shown.Count >= 5, shown.Count + " readings");
            var off = shown.Where(r => Shown(r) != Shown(hz)).ToList();
            Assert.Single(off);
            Assert.True(off[0] < hz * 0.995, $"the stalled second read {off[0]:F1} Hz at {hz}");
            if (hz > 1000)
                Assert.Contains(ended, r => Shown(r) != Shown(hz) && r > hz * 0.995);

            string loop = ExtrasPageTests.RepoText("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("if (_rateMeter.TryPass(schedule.Due, Stopwatch.Frequency, out double passRate))", loop);
        }
    }
}
