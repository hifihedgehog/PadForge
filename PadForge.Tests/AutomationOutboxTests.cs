using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The outbox's writer takes a session's waiting lines in one step under
    /// its lock and formats them after the lock is released (#597): the
    /// poll thread's puts never wait on formatting, lines keep their order
    /// across the swap of rings, and an axis value set while a batch formats
    /// is written in that batch or the next, never lost.
    /// </summary>
    public class AutomationOutboxTests
    {
        /// <summary>What the writer wrote, read while it writes.</summary>
        private sealed class Captured : Stream
        {
            private readonly MemoryStream _bytes = new();
            private readonly object _gate = new();

            public string Text
            {
                get { lock (_gate) return Encoding.UTF8.GetString(_bytes.ToArray()); }
            }

            public string[] Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate) _bytes.Write(buffer, offset, count);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static async Task Until(Func<bool> done, int ms = 10000)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(clock.ElapsedMilliseconds < ms, "timed out");
                await Task.Delay(5);
            }
        }

        [Fact]
        public async Task Lines_KeepTheirOrder_AcrossEverySwap()
        {
            var captured = new Captured();
            var outbox = new AutomationOutbox(captured, () => { });
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() => outbox.RunAsync(stop.Token));
            const int count = 20000;
            // A put from another thread while the writer formats, so batches
            // of every size are taken and rings are swapped many times. The
            // producer keeps the queue under the cap, which a busy PC's writer
            // would otherwise let it pass.
            var producer = Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    while (outbox.Waiting > 2000) Thread.Yield();
                    if ((i & 1) == 0) Assert.True(outbox.Line("l " + i));
                    else Assert.True(outbox.Button("dev", i, true));
                    if (i % 500 == 0) Thread.Yield();
                }
            });
            await producer;
            await Until(() => captured.Lines.Length >= count);
            var lines = captured.Lines;
            Assert.Equal(count, lines.Length);
            for (int i = 0; i < count; i++)
                Assert.Equal((i & 1) == 0 ? "l " + i : "e b dev " + i + " 1", lines[i]);
            Assert.False(outbox.CutOff);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        }

        /// <summary>A value set before its item is formatted replaces the
        /// queued one in the same batch. A value set after it was formatted
        /// is queued again and written in the next batch.</summary>
        [Fact]
        public async Task AnAxisValue_SetWhileABatchFormats_IsWrittenInThatBatchOrTheNext()
        {
            // Set after the first item, before the axis item: the same batch.
            var captured = new Captured();
            var outbox = new AutomationOutbox(captured, () => { });
            var slots = new AxisStreamSlots("dev1");
            int formatted = 0;
            outbox.FormattedForTest = () =>
            {
                if (Interlocked.Increment(ref formatted) == 1)
                    Assert.True(Task.Run(() => outbox.Axis(slots, 0, 2)).Wait(5000));
            };
            outbox.Line("a");
            outbox.Axis(slots, 0, 1);
            outbox.Line("b");
            using (var stop = new CancellationTokenSource())
            {
                var writer = Task.Run(() => outbox.RunAsync(stop.Token));
                await Until(() => captured.Lines.Length >= 3);
                await Task.Delay(200);
                Assert.Equal(new[] { "a", "e a dev1 0 2", "b" }, captured.Lines);
                Assert.Equal(0, outbox.Waiting);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
            }

            // Set after the axis item was formatted: queued again, next batch.
            captured = new Captured();
            outbox = new AutomationOutbox(captured, () => { });
            slots = new AxisStreamSlots("dev2");
            formatted = 0;
            var local = outbox;
            var localSlots = slots;
            outbox.FormattedForTest = () =>
            {
                if (Interlocked.Increment(ref formatted) == 2)
                    Assert.True(Task.Run(() => local.Axis(localSlots, 0, 3)).Wait(5000));
            };
            outbox.Line("a");
            outbox.Axis(slots, 0, 1);
            outbox.Line("b");
            using (var stop = new CancellationTokenSource())
            {
                var writer = Task.Run(() => local.RunAsync(stop.Token));
                await Until(() => captured.Lines.Length >= 4);
                Assert.Equal(new[] { "a", "e a dev2 0 1", "b", "e a dev2 0 3" }, captured.Lines);
                Assert.False(slots.Waiting[0]);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
            }
        }

        /// <summary>While the writer is in the middle of formatting a batch,
        /// every put the poll thread makes, and a line of an answer, goes
        /// through at once: the lock is not held.</summary>
        [Fact]
        public async Task ThePollThreadsPut_IsNeverHeld_WhileTextIsFormatted()
        {
            var captured = new Captured();
            var outbox = new AutomationOutbox(captured, () => { });
            var slots = new AxisStreamSlots("dev");
            using var formatting = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int formatted = 0;
            outbox.FormattedForTest = () =>
            {
                if (Interlocked.Increment(ref formatted) != 50) return;
                // The writer is held here, mid-batch, until the puts are done.
                formatting.Set();
                Assert.True(release.Wait(10000), "the puts never finished");
            };
            for (int i = 0; i < 100; i++) outbox.Line("l " + i);
            outbox.Axis(slots, 1, 5);
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() => outbox.RunAsync(stop.Token));
            Assert.True(formatting.Wait(10000), "the writer never formatted");

            var puts = Task.Run(() =>
            {
                Assert.True(outbox.Button("dev", 1, true));
                Assert.True(outbox.Hat("dev", 0, 2));
                Assert.True(outbox.Crossing("dev", 2, true));
                Assert.True(outbox.Axis(slots, 1, 6));
                Assert.True(outbox.Axis(slots, 2, 7));
                Assert.True(outbox.Line("after"));
            });
            bool through = await Task.WhenAny(puts, Task.Delay(5000)) == puts;
            release.Set();
            Assert.True(through, "a put waited on the writer's formatting");
            await puts;

            await Until(() => captured.Text.Contains("after\n", StringComparison.Ordinal));
            var lines = captured.Lines;
            Assert.Equal(Enumerable.Range(0, 100).Select(i => "l " + i), lines.Take(100));
            // The axis queued before the batch was taken was written with the
            // newest value, 6, set while the batch formatted. The puts made
            // meanwhile follow in the next batch, in their order.
            Assert.Equal(new[] { "e a dev 1 6", "e b dev 1 1", "e h dev 0 2", "e x dev 2 1", "e a dev 2 7", "after" }, lines.Skip(100));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        }
    }
}
