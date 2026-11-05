using System.Threading;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>What a worker read of a device, for the run after it: only from a
    /// worker still running when that run began, and once.</summary>
    public class WorkerSuccessionHandBackTests
    {
        private const string Device = "device";

        /// <summary>A worker on a thread of its own, which has read the device and
        /// holds until it is let go.</summary>
        private sealed class Held : System.IDisposable
        {
            private readonly ManualResetEventSlim _release = new(false);
            private readonly Thread _thread;
            public readonly WorkerRun Run = new();

            public Held(WorkerSuccession workers, string read)
            {
                using var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    workers.Enter(Run, 1, "test");
                    workers.NoteHandBack(Device, Run, read);
                    ready.Set();
                    _release.Wait(30000);
                }) { IsBackground = true };
                _thread.Start();
                Assert.True(ready.Wait(30000));
            }

            public void End()
            {
                _release.Set();
                Assert.True(_thread.Join(30000));
            }

            public void Dispose()
            {
                End();
                _release.Dispose();
            }
        }

        /// <summary>An earlier run's claim bars nobody: it is over, and a claim stays in the
        /// books after its run. A worker running late still takes the device and hands
        /// it back, until a later run claims it.</summary>
        [Fact]
        public void AnEarlierRunsClaim_DoesNotBarAWorkerRunningLate()
        {
            var workers = new WorkerSuccession();
            var earlier = new WorkerRun();
            workers.Enter(earlier, 1, "test");
            workers.Claim(Device, earlier);
            using var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");

            Assert.True(workers.MayReach(Device, late.Run));
            workers.Claim(Device, late.Run);
            Assert.True(workers.MayReach(Device, late.Run));
            Assert.False(workers.MayReach(Device, earlier));

            workers.Claim(Device, next);
            Assert.False(workers.MayReach(Device, late.Run));
        }

        /// <summary>A worker a later run has barred from the device, back from a read it
        /// hung in, leaves the bar and notes nothing.</summary>
        [Fact]
        public void ABarredWorker_StaysBarred_AndNotesNothing()
        {
            var workers = new WorkerSuccession();
            using var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            Assert.True(workers.TakeHandBack(Device, next, out string read));
            workers.Claim(Device, next);

            workers.Claim(Device, late.Run);
            workers.NoteHandBack(Device, late.Run, "the next run's picture");
            Assert.False(workers.MayReach(Device, late.Run));
            Assert.False(workers.HoldsHandBack(Device));
        }

        /// <summary>A program has the device: what any worker read of it is old, and an
        /// earlier worker sends it nothing.</summary>
        [Fact]
        public void LeftToAProgram_EndsWhatAnEarlierWorkerRead_AndBarsThatWorker()
        {
            var workers = new WorkerSuccession();
            using var late = new Held(workers, "own lighting");
            workers.Claim(Device, late.Run);
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            Assert.True(workers.MayReach(Device, late.Run));

            workers.LeftToAProgram(Device, next);
            Assert.False(workers.HoldsHandBack(Device));
            Assert.False(workers.MayReach(Device, late.Run));
            Assert.True(workers.MayReach(Device, next));
        }

        /// <summary>A worker a later one has replaced speaks only for itself: what the
        /// later one read stands, and the claim stays the later one's.</summary>
        [Fact]
        public void LeftToAProgram_FromAWorkerAlreadyReplaced_EndsOnlyItsOwn()
        {
            var workers = new WorkerSuccession();
            using var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            workers.NoteHandBack(Device, next, "what the next run read");
            workers.Claim(Device, next);

            workers.LeftToAProgram(Device, late.Run);
            Assert.True(workers.HoldsHandBack(Device));
            Assert.False(workers.MayReach(Device, late.Run));

            const string Other = "other";
            workers.NoteHandBack(Other, late.Run, "what it read");
            workers.LeftToAProgram(Other, late.Run);
            Assert.False(workers.HoldsHandBack(Other));
        }

        [Fact]
        public void TheNextRun_TakesWhatAWorkerStillRunningRead_Once()
        {
            var workers = new WorkerSuccession();
            using var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            Assert.True(workers.TakeHandBack(Device, next, out string read));
            Assert.Equal("own lighting", read);
            Assert.False(workers.TakeHandBack(Device, next, out read));
        }

        /// <summary>That worker ending after the run began changes nothing: what it
        /// read is still what the device showed.</summary>
        [Fact]
        public void TheNextRun_StillTakesIt_WhenThatWorkerEndsLater()
        {
            var workers = new WorkerSuccession();
            var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            late.Dispose();
            Assert.True(workers.TakeHandBack(Device, next, out string read));
            Assert.Equal("own lighting", read);
        }

        /// <summary>A worker that ended before the run began read old news: the user
        /// can have changed the lighting since.</summary>
        [Fact]
        public void TheNextRun_TakesNothingFromAWorkerThatHadEnded()
        {
            var workers = new WorkerSuccession();
            var ended = new Held(workers, "own lighting");
            ended.Dispose();
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            Assert.False(workers.TakeHandBack<string>(Device, next, out _));
        }

        [Fact]
        public void ARun_TakesNothingOfItsOwn_OfAnotherShape_OrHandedBack()
        {
            var workers = new WorkerSuccession();
            var run = new WorkerRun();
            workers.Enter(run, 1, "test");
            workers.NoteHandBack(Device, run, "own lighting");
            Assert.False(workers.TakeHandBack<string>(Device, run, out _));

            using var late = new Held(workers, "own lighting");
            var next = new WorkerRun();
            workers.Enter(next, 1, "test");
            Assert.False(workers.TakeHandBack<int>(Device, next, out _));
            // Another run's word does not end it.
            workers.HandedBack(Device, next);
            Assert.True(workers.TakeHandBack<string>(Device, next, out _));

            workers.NoteHandBack(Device, run, "own lighting");
            workers.HandedBack(Device, run);
            Assert.False(workers.TakeHandBack<string>(Device, next, out _));
        }
    }
}
