using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Engine.Tablets;
using Xunit;

namespace PadForge.Tests;

public class TabletCaptureTests
{
    private sealed class Input : Stream
    {
        internal readonly Channel<byte[]> Reports = Channel.CreateUnbounded<byte[]>();
        internal readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            var report = await Reports.Reader.ReadAsync(cancellationToken);
            report.CopyTo(buffer);
            return report.Length;
        }
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            Reports.Writer.TryComplete();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task CapturePublishesReportsAndUnhideClosesOnlyOnceBeforeRestore()
    {
        using var f = new TabletReportStateTests.Fixture();
        var input = new Input();
        int restarts = 0;
        using var device = new WindowsTabletDevice(f.Decoder, null, () => input, _ =>
        {
            Assert.Equal(1, input.DisposeCount);
            Interlocked.Increment(ref restarts);
        });
        device.SetCapture(true);
        await input.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TabletCaptureState.WaitingForInput, device.CaptureState);
        input.Reports.Writer.TryWrite(new byte[] { 1, 0 });
        await Until(() => device.CaptureState == TabletCaptureState.Captured);
        Assert.True(device.GetCurrentState().Touchpads[0].FingerDown[0]);
        device.PrepareForUnhide();
        Assert.Equal(TabletCaptureState.Switching, device.CaptureState);
        Assert.Equal(1, input.DisposeCount);
        Assert.False(device.GetCurrentState().Touchpads[0].FingerDown[0]);
        device.SetCapture(false);
        await Until(() => device.CaptureState == TabletCaptureState.Shared);
        Assert.Equal(1, restarts);
        Assert.Equal(1, input.DisposeCount);
    }

    [Fact]
    public async Task PolicyFailureCannotBeOverwrittenByAnOlderRestore()
    {
        using var f = new TabletReportStateTests.Fixture();
        var input = new Input();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var device = new WindowsTabletDevice(f.Decoder, null, () => input, _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });
        try
        {
            device.SetCapture(true);
            await input.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.SetCapture(false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.ReportPolicyFailure("readback failed");
            release.Set();
            Assert.True(await Task.Run(() => device.WaitForTransition(TimeSpan.FromSeconds(5))));
            Assert.Equal(TabletCaptureState.Failed, device.CaptureState);
            Assert.Equal("readback failed", device.CaptureError);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task AnOpenThatFinishesAfterUnhideCannotPublishOrLeakItsHandle()
    {
        using var f = new TabletReportStateTests.Fixture();
        var input = new Input();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<TabletCaptureState>();
        using var device = new WindowsTabletDevice(f.Decoder, (d, _, _, _) => events.Enqueue(d.CaptureState), () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return input;
        }, _ => { });
        try
        {
            device.SetCapture(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.SetCapture(false);
            release.Set();
            await Until(() => device.CaptureState == TabletCaptureState.Shared);
            Assert.Equal(1, input.DisposeCount);
            Assert.DoesNotContain(TabletCaptureState.WaitingForInput, events);
            Assert.DoesNotContain(TabletCaptureState.Captured, events);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCaptureOnlyRequestsRollbackForAFreshCloak(bool fresh)
    {
        using var f = new TabletReportStateTests.Fixture();
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new WindowsTabletDevice(f.Decoder, (_, _, rollback, error) =>
        {
            if (error == "access denied") result.TrySetResult(rollback);
        }, () => throw new UnauthorizedAccessException("access denied"));
        device.SetCapture(true, fresh);
        Assert.Equal(fresh, await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TabletCaptureState.Failed, device.CaptureState);
    }

    [Fact]
    public async Task DisposalKeepsAFailedCloaksRollbackValidButANewerRequestCancelsIt()
    {
        using var f = new TabletReportStateTests.Fixture();
        var failure = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new WindowsTabletDevice(f.Decoder, (_, version, rollback, _) =>
        {
            if (rollback) failure.TrySetResult(version);
        }, () => throw new UnauthorizedAccessException());
        device.SetCapture(true, true);
        int revision = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        device.Dispose();
        Assert.False(device.IsCurrentCaptureRevision(revision));
        Assert.True(device.CanRollbackCaptureRevision(revision));
        Assert.False(device.CanRollbackCaptureRevision(revision - 1));
    }

    [Fact]
    public void ADisposedReaderCannotStartAnotherMessageLoop()
    {
        using var reader = new WindowsTabletReader();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Start());
    }
}
