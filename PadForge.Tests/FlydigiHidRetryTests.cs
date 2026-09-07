using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiHidRetryTests
    {
        private const int D = FlydigiReprobePolicy.DelayMs;
        private const string PathA = @"\\?\HID#VID_37D7&PID_2401&MI_01#retry-test";

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        public void UnavailableCounter_BoundsNativeReadsAndRecovers(int failedValue)
        {
            using var rig = new Rig();
            rig.CounterValue = failedValue < 0 ? null : (uint)failedValue;
            var before = rig.Observation();
            rig.TickAt(1000);
            Assert.Equal(1, rig.CounterCalls);
            Assert.Equal(0, rig.PathCalls);
            Assert.Equal(0, rig.NudgeCalls);
            Assert.Equal(before, rig.Observation());

            for (long now = 1001; now < 1000 + D; now++) rig.TickAt(now);
            Assert.Equal(1, rig.CounterCalls);
            rig.TickAt(1000 + D);
            Assert.Equal(2, rig.CounterCalls);
            Assert.Equal(0, rig.PathCalls);
            Assert.Equal(before, rig.Observation());

            rig.CounterValue = 9;
            for (long now = 1000 + D + 1; now < 1000 + 2 * D; now++) rig.TickAt(now);
            Assert.Equal(2, rig.CounterCalls);
            Assert.Equal(0, rig.PathCalls);
            rig.TickAt(1000 + 2 * D);
            Assert.Equal(3, rig.CounterCalls);
            Assert.Equal(1, rig.PathCalls);
            Assert.True(rig.Observation().Known);
            Assert.Equal(9u, rig.Observation().Counter);
            Assert.Equal(1000 + 2 * D, rig.Observation().LastObserved);
            Assert.Equal(1000 + 3 * D, rig.Observation().Confirmation);
            Assert.Equal(1, rig.Policy.Tracked);
            Assert.Equal(0, rig.NudgeCalls);
        }

        [Fact]
        public void FailedPaths_BoundCounterAndEnumerationAndKeepDeferredChanges()
        {
            using var rig = new Rig();
            rig.TickAt(1000);
            rig.TickAt(1000 + D);
            Assert.Equal(1, rig.NudgeCalls);
            Assert.Equal(1, rig.Policy.LastAttempt);
            rig.RememberOrdinary(44);
            var before = rig.Observation();
            int counterCalls = rig.CounterCalls, pathCalls = rig.PathCalls;

            rig.CounterValue = 8;
            rig.PresentPaths = null;
            rig.TickAt(1000 + D + 1);
            Assert.Equal(counterCalls + 1, rig.CounterCalls);
            Assert.Equal(pathCalls + 1, rig.PathCalls);
            Assert.Equal(before, rig.Observation());
            Assert.Equal(1, rig.NudgeCalls);

            rig.CounterValue = 9;
            rig.PresentPaths = new List<string> { PathA };
            for (long now = 1000 + D + 2; now < 1000 + 2 * D + 1; now++) rig.TickAt(now);
            Assert.Equal(counterCalls + 1, rig.CounterCalls);
            Assert.Equal(pathCalls + 1, rig.PathCalls);
            Assert.Equal(before, rig.Observation());

            rig.TickAt(1000 + 2 * D + 1);
            Assert.Equal(counterCalls + 2, rig.CounterCalls);
            Assert.Equal(pathCalls + 2, rig.PathCalls);
            Assert.Equal(9u, rig.Observation().Counter);
            Assert.Equal(1000 + 3 * D + 1, rig.Observation().Confirmation);
            Assert.Equal("", rig.Observation().OrdinaryIds);
            Assert.Equal(2, rig.Policy.LastAttempt);
            Assert.Equal(2, rig.NudgeCalls);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void FailureDoesNotAcknowledgeAPendingConfirmation(int failure)
        {
            using var rig = new Rig { PresentPaths = new List<string>() };
            rig.TickAt(1000);
            Assert.Equal(1000 + D, rig.Observation().Confirmation);
            Assert.False(rig.Policy.Armed);
            var before = rig.Observation();
            if (failure == 0) rig.CounterValue = null;
            else if (failure == 1) rig.CounterValue = 0;
            else rig.PresentPaths = null;

            rig.TickAt(1000 + D);
            Assert.Equal(before, rig.Observation());
            int calls = rig.CounterCalls, paths = rig.PathCalls;
            rig.CounterValue = 7;
            rig.PresentPaths = new List<string>();
            rig.TickAt(1000 + 2 * D - 1);
            Assert.Equal(calls, rig.CounterCalls);
            Assert.Equal(paths, rig.PathCalls);

            rig.TickAt(1000 + 2 * D);
            Assert.Equal(calls + 1, rig.CounterCalls);
            Assert.Equal(paths + 1, rig.PathCalls);
            Assert.Equal(0, rig.Observation().Confirmation);
            Assert.Equal(1000 + 2 * D, rig.Observation().LastObserved);
            rig.CounterValue = 8;
            rig.TickAt(1000 + 2 * D + 1);
            Assert.Equal(paths + 2, rig.PathCalls);
            Assert.Equal(8u, rig.Observation().Counter);
        }

        [Fact]
        public void RecoveryDoesNotRenewAnExhaustedProbeBudget()
        {
            using var rig = new Rig();
            for (int i = 0; i <= FlydigiReprobePolicy.MaxAttempts; i++) rig.TickAt(1000 + i * D);
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, rig.NudgeCalls);
            Assert.False(rig.Policy.Armed);
            var before = rig.Observation();
            long failedAt = 1000 + FlydigiReprobePolicy.MaxAttempts * D + 1;
            rig.CounterValue = null;
            rig.TickAt(failedAt);
            Assert.Equal(before, rig.Observation());

            rig.CounterValue = 8;
            rig.TickAt(failedAt + D);
            Assert.Equal(8u, rig.Observation().Counter);
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, rig.Policy.LastAttempt);
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, rig.NudgeCalls);
            Assert.False(rig.Policy.Armed);
        }

        [Fact]
        public void HealthyCounterChangesStillEnumerateImmediately()
        {
            using var rig = new Rig { PresentPaths = new List<string>() };
            rig.TickAt(1000);
            for (long now = 1001; now < 1000 + D; now++) rig.TickAt(now);
            Assert.Equal(D, rig.CounterCalls);
            Assert.Equal(1, rig.PathCalls);
            rig.TickAt(1000 + D);
            Assert.Equal(2, rig.PathCalls);
            Assert.Equal(0, rig.Observation().Confirmation);

            rig.CounterValue = 8;
            rig.TickAt(1000 + D + 1);
            rig.CounterValue = 9;
            rig.TickAt(1000 + D + 2);
            Assert.Equal(4, rig.PathCalls);
            Assert.Equal(D + 3, rig.CounterCalls);
            Assert.Equal(9u, rig.Observation().Counter);
            Assert.Equal(0, rig.NudgeCalls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FailureDelayStartsAfterTheFailingNativeCall(bool pathsFail)
        {
            using var rig = new Rig();
            if (pathsFail)
            {
                rig.PresentPaths = null;
                rig.PathReadAdvanceMs = 2 * D;
            }
            else
            {
                rig.CounterValue = null;
                rig.CounterReadAdvanceMs = 2 * D;
            }
            rig.TickAt(1000);
            Assert.Equal(1, rig.CounterCalls);
            int paths = rig.PathCalls;
            rig.CounterValue = 9;
            rig.PresentPaths = new List<string> { PathA };
            rig.CounterReadAdvanceMs = rig.PathReadAdvanceMs = 0;

            rig.TickAt(1000 + 2 * D + 1);
            rig.TickAt(1000 + 3 * D - 1);
            Assert.Equal(1, rig.CounterCalls);
            Assert.Equal(paths, rig.PathCalls);
            rig.TickAt(1000 + 3 * D);
            Assert.Equal(2, rig.CounterCalls);
            Assert.Equal(paths + 1, rig.PathCalls);
            Assert.Equal(9u, rig.Observation().Counter);
        }

        [Fact]
        public void HealthyObservationKeepsItsClockAfterTheCounterRead()
        {
            using var rig = new Rig { CounterReadAdvanceMs = 250, PresentPaths = new List<string>() };
            rig.TickAt(1000);
            Assert.Equal(1250, rig.Observation().LastObserved);
            Assert.Equal(1250 + D, rig.Observation().Confirmation);
            Assert.Equal(1, rig.PathCalls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BackoffPollsAllocateNothingAndCallNoNativeReader(bool pathsFail)
        {
            using var rig = new Rig();
            if (pathsFail) rig.PresentPaths = null;
            else rig.CounterValue = null;
            rig.TickAt(1000);
            for (int i = 0; i < 64; i++) rig.TickAt(1001);
            int count = rig.CounterCalls, paths = rig.PathCalls;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) rig.TickAt(1001);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(count, rig.CounterCalls);
            Assert.Equal(paths, rig.PathCalls);
            Assert.Equal(0, rig.NudgeCalls);
        }

        [Fact]
        public void QuietHealthyPollsAllocateNothingAndKeepReadingTheCounter()
        {
            using var rig = new Rig { PresentPaths = new List<string>() };
            rig.TickAt(1000);
            rig.TickAt(1000 + D);
            for (int i = 0; i < 64; i++) rig.TickAt(1000 + D + 1);
            int count = rig.CounterCalls, paths = rig.PathCalls;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) rig.TickAt(1000 + D + 1);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(count + 10000, rig.CounterCalls);
            Assert.Equal(paths, rig.PathCalls);
        }

        private readonly record struct ObservationState(uint Counter, bool Known, long LastObserved,
            long Confirmation, int Tracked, int Attempt, bool Armed, string OrdinaryIds);

        private sealed class Rig : IDisposable
        {
            private readonly InputManager manager = new();
            private readonly Func<long> clock;
            private readonly Func<uint?> readCount;
            private readonly Func<List<string>> readPaths;
            private readonly Func<(bool written, string value)> nudge;
            public FlydigiReprobePolicy Policy { get; }
            public uint? CounterValue { get; set; } = 7;
            public List<string> PresentPaths { get; set; } = new() { PathA };
            public int CounterCalls { get; private set; }
            public int PathCalls { get; private set; }
            public int NudgeCalls { get; private set; }
            public int CounterReadAdvanceMs { get; set; }
            public int PathReadAdvanceMs { get; set; }
            private long now;

            public Rig()
            {
                clock = () => now;
                readCount = () => { CounterCalls++; now += CounterReadAdvanceMs; return CounterValue; };
                readPaths = () => { PathCalls++; now += PathReadAdvanceMs; return PresentPaths; };
                nudge = () => { NudgeCalls++; return (false, "1"); };
                Policy = Field<FlydigiReprobePolicy>("_flydigiReprobe");
            }

            public void TickAt(long value)
            {
                now = value;
                manager.FlydigiReprobeTick(clock, readCount, readPaths, nudge);
            }
            public void RememberOrdinary(uint id) => Field<HashSet<uint>>("_flydigiLastOrdinaryIds").Add(id);
            private T Field<T>(string name) => (T)typeof(InputManager)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            public ObservationState Observation() => new(
                Field<uint>("_flydigiChangeCount"), Field<bool>("_flydigiChangeCountKnown"),
                Field<long>("_flydigiObserveTick"), Field<long>("_flydigiConfirmDue"),
                Policy.Tracked, Policy.LastAttempt, Policy.Armed,
                string.Join(",", Field<HashSet<uint>>("_flydigiLastOrdinaryIds").OrderBy(id => id)));
            public void Dispose() => manager.Dispose();
        }
    }
}
