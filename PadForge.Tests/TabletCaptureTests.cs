using System;
using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
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
        private int readCalls;
        internal int ReadCalls => Volatile.Read(ref readCalls);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readCalls);
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(1, false)]
    public async Task ReadFailureOnlyRollsBackBeforeTheFirstValidReport(int reportId, bool expectedRollback)
    {
        using var f = new TabletReportStateTests.Fixture();
        var input = new Input();
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var device = new WindowsTabletDevice(f.Decoder, (_, _, rollback, error) =>
        {
            if (error.Length > 0) failed.TrySetResult(rollback);
        }, () => input);
        device.SetCapture(true, true);
        await input.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reportId != 0)
        {
            input.Reports.Writer.TryWrite(new byte[] { (byte)reportId, 0 });
            await Until(() => input.ReadCalls >= 2);
        }
        if (reportId == 1)
        {
            Assert.Equal(TabletCaptureState.Captured, device.CaptureState);
            Assert.True(device.GetCurrentState().Touchpads[0].FingerDown[0]);
        }
        else Assert.Equal(TabletCaptureState.WaitingForInput, device.CaptureState);
        input.Reports.Writer.TryComplete(new IOException("device removed"));
        Assert.Equal(expectedRollback, await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(device.GetCurrentState().Touchpads[0].FingerDown[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawVisibilityLossReleasesSharedContactAndPreservesExclusiveContact(bool captured)
    {
        using var f = new TabletReportStateTests.Fixture();
        var input = new Input();
        using var device = new WindowsTabletDevice(f.Decoder, null, () => input);
        using var reader = new WindowsTabletReader();
        if (captured)
        {
            device.SetCapture(true);
            input.Reports.Writer.TryWrite(new byte[] { 1, 0 });
            await Until(() => device.CaptureState == TabletCaptureState.Captured);
        }
        else Assert.True(f.Decode());
        Assert.True(device.GetCurrentState().Touchpads[0].FingerDown[0]);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(WindowsTabletReader).GetField("running", flags).SetValue(reader, true);
        // Hold the census so the event's immediate state change is measured independently.
        typeof(WindowsTabletReader).GetField("enumerating", flags).SetValue(reader, 1);
        var devices = (Dictionary<string, WindowsTabletDevice>)typeof(WindowsTabletReader).GetField("devices", flags).GetValue(reader);
        var paths = (Dictionary<IntPtr, string>)typeof(WindowsTabletReader).GetField("rawPaths", flags).GetValue(reader);
        devices[device.DevicePath] = device;
        paths[new IntPtr(123)] = device.DevicePath;
        typeof(WindowsTabletReader).GetMethod("ProcessMessage", flags).Invoke(reader,
            new object[] { IntPtr.Zero, 0xFEu, new IntPtr(2), new IntPtr(123) });
        Assert.Equal(captured, device.GetCurrentState().Touchpads[0].FingerDown[0]);
        Assert.True(device.IsAttached);
        Assert.Same(device, Assert.Single(reader.GetDevices()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementRejectsRecaptureWithoutCancelingThePendingRestore(bool retireAfterRestoreRequest)
    {
        string identity = Guid.NewGuid().ToString("N");
        using var oldFixture = new TabletReportStateTests.Fixture(identity: identity);
        using var newFixture = new TabletReportStateTests.Fixture(identity: identity);
        using var releaseOpen = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feedback = new ConcurrentQueue<Action>();
        bool releasing = false;
        int opens = 0, restarts = 0, replacementSawRestarts = 0;
        var oldInput = new Input();
        var newInput = new Input();
        WindowsTabletDevice oldDevice = null;
        using (oldDevice = new WindowsTabletDevice(oldFixture.Decoder,
            (device, _, _, _) =>
            {
                if (Volatile.Read(ref releasing)) feedback.Enqueue(() => device.SetCapture(true, retry: true));
            }, () =>
            {
                if (Interlocked.Increment(ref opens) == 1) throw new IOException("collection is busy");
                opening.TrySetResult();
                if (!releaseOpen.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return oldInput;
            }, _ => Interlocked.Increment(ref restarts), () => true))
        using (var replacement = new WindowsTabletDevice(newFixture.Decoder, null, () =>
            {
                replacementSawRestarts = Volatile.Read(ref restarts);
                return newInput;
            }))
        {
            try
            {
                oldDevice.SetCapture(true);
                await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Volatile.Write(ref releasing, true);
                oldDevice.PrepareForUnhide(retireCapture: !retireAfterRestoreRequest);
                oldDevice.SetCapture(false);
                if (retireAfterRestoreRequest) oldDevice.PrepareForUnhide(retireCapture: true);
                Assert.True(feedback.Count >= 2, "The release must have generated real capture callbacks.");
                // Reproduce the queued policy feedback before disposal, while
                // the original native open still prevents restoration.
                foreach (var replay in feedback.ToArray()) replay();
                oldDevice.Dispose();
                replacement.SetCapture(true);
                releaseOpen.Set();
                await newInput.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, restarts);
                Assert.Equal(2, replacementSawRestarts);
                Assert.Equal(1, oldInput.DisposeCount);
                Assert.Equal(TabletCaptureState.Offline, oldDevice.CaptureState);
            }
            finally { releaseOpen.Set(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRestoreSurvivesDisposalAndPrecedesAReplacementReader(bool restoreFails)
    {
        string identity = Guid.NewGuid().ToString("N");
        using var oldFixture = new TabletReportStateTests.Fixture(identity: identity);
        using var newFixture = new TabletReportStateTests.Fixture(identity: identity);
        using var releaseOpen = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementOpening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0, restarts = 0, observedRestarts = 0;
        var staleInput = new Input();
        var newInput = new Input();
        using var oldDevice = new WindowsTabletDevice(oldFixture.Decoder, null, () =>
        {
            if (Interlocked.Increment(ref opens) == 1) throw new IOException("collection is busy");
            opening.TrySetResult();
            if (!releaseOpen.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return staleInput;
        }, _ =>
        {
            if (Interlocked.Increment(ref restarts) == 2 && restoreFails) throw new IOException("restore failed");
        }, () => true);
        using var newDevice = new WindowsTabletDevice(newFixture.Decoder, null, () =>
        {
            observedRestarts = Volatile.Read(ref restarts);
            replacementOpening.TrySetResult();
            return newInput;
        });
        try
        {
            oldDevice.SetCapture(true);
            await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldDevice.PrepareForUnhide();
            oldDevice.SetCapture(false);
            Assert.False(await Task.Run(() => oldDevice.WaitForTransition(TimeSpan.FromMilliseconds(25))));
            oldDevice.Dispose();
            newDevice.SetCapture(true);
            await Task.WhenAny(replacementOpening.Task, Task.Delay(200));
            Assert.False(replacementOpening.Task.IsCompleted);
            releaseOpen.Set();
            await newInput.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, restarts);
            Assert.Equal(2, observedRestarts);
            Assert.Equal(1, staleInput.DisposeCount);
            Assert.Equal(TabletCaptureState.Offline, oldDevice.CaptureState);
            Assert.True(await Task.Run(() => oldDevice.WaitForTransition(TimeSpan.FromSeconds(5))));
        }
        finally { releaseOpen.Set(); }
    }
}
