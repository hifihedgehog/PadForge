using System.Diagnostics;
using System.Runtime.InteropServices;
using PadForge.Engine.Tablets;

namespace PadForge.Engine.Touchpads;

/// <summary>What a capture needs from the reader that owns it: the
/// collection's layout, a place for the touchpad's state while it is
/// read, its reports, and the end of that place. The reader is the
/// production host (<see cref="PrecisionTouchpadReader"/>). A test hands a
/// capture a host of its own.</summary>
internal interface ITouchpadCaptureHost
{
    /// <summary>Reads the collection's layout through a handle that asks
    /// for no access. Throws IOException when the collection cannot be
    /// opened.</summary>
    ITouchpadLayout Describe(TouchpadCapture capture);

    /// <summary>Puts the touchpad on the device list under its own path,
    /// read from now on through the capture. Returns the key its state is
    /// filed under.</summary>
    IntPtr Publish(TouchpadCapture capture, ITouchpadLayout layout);

    /// <summary>One input report. True when it was the touchpad's input
    /// report rather than another report of the collection.</summary>
    bool Feed(IntPtr key, ITouchpadLayout layout, IntPtr report, int length);

    /// <summary>Takes the touchpad's state off the device list.</summary>
    void Withdraw(IntPtr key);
}

/// <summary>A collection's layout, held while it is read.</summary>
internal interface ITouchpadLayout : IDisposable
{
    /// <summary>The length of the collection's longest input report.</summary>
    int ReportLength { get; }
}

/// <summary>
/// A precision touchpad PadForge reads alone, the pen tablet's capture
/// (<see cref="WindowsTabletDevice"/>) for a touchpad. HidHide hides the
/// touchpad's collection from every program PadForge has not listed, and
/// decides only when a program opens it (HidHide Logic.c), so Windows' own
/// reader keeps the handle it had. The capture opens the collection with
/// no sharing. While Windows still holds it that open fails, and the
/// capture restarts the collection, which closes Windows' handle. Windows'
/// reopen is then refused, and PadForge's goes through. Giving the touchpad
/// back restarts the collection again once HidHide shows it, so Windows
/// opens it as it would at start-up.
///
/// <para>The touchpad stays in the mode Windows set: Windows' configuration
/// driver owns the collection that sets the input mode, and opens it for
/// itself (Microsoft, Using Report Descriptors to Support Capability
/// Discovery, Feature Report Exclusivity), so the touchpad keeps reporting
/// through its touchpad collection and not through its mouse collection
/// (Microsoft, touchpad configuration collection, Input Mode).</para>
///
/// <para>The state machine is the tablet's, transition for transition: a
/// generation that every request advances and every result checks, a turn
/// per collection so a restart and the next open never overlap
/// (<see cref="TabletCaptureQueue"/>), and a restore that finishes even
/// after the capture was disposed.</para>
/// </summary>
public sealed class TouchpadCapture : IDisposable
{
    private readonly object gate = new();
    private readonly ITouchpadCaptureHost host;
    private readonly Action<TouchpadCapture, int, bool, string> changed;
    private readonly Func<int, Stream> openInput;
    private readonly Action<CancellationToken> restart;
    private readonly Func<bool> isStarted;
    private CancellationTokenSource request;
    private CaptureInput stream;
    private Task operation = Task.CompletedTask;
    private int generation;
    private bool closed, captureWanted, attemptedCapture, requestInvalidated, captureRetiring;
    private TabletCaptureState captureState;
    private string captureError = "";

    internal TouchpadCapture(ITouchpadCaptureHost host, string path, string instanceId, string name,
        ushort vendorId, ushort productId, Action<TouchpadCapture, int, bool, string> onChanged,
        Func<int, Stream> openInput = null, Action<CancellationToken> restart = null, Func<bool> isStarted = null)
    {
        this.host = host;
        changed = onChanged;
        DevicePath = path;
        DeviceInstanceId = instanceId;
        Name = name;
        VendorId = vendorId;
        ProductId = productId;
        this.openInput = openInput ?? (length => TabletNative.OpenCapture(DevicePath, length));
        this.restart = restart ?? (stop => TabletNative.RestartCollection(DeviceInstanceId, stop));
        this.isStarted = isStarted ?? (() => TabletNative.IsStarted(DeviceInstanceId));
    }

    /// <summary>The touchpad collection's device path, the one its row was
    /// named by, so the row keeps its ID while PadForge reads it.</summary>
    public string DevicePath { get; }

    /// <summary>The touchpad collection's instance ID, which HidHide hides.</summary>
    public string DeviceInstanceId { get; }

    public string Name { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }

    public TabletCaptureState CaptureState { get { lock (gate) return captureState; } }
    public string CaptureError { get { lock (gate) return captureError; } }
    public bool CaptureWasRequested { get { lock (gate) return attemptedCapture; } }

    /// <summary>True while PadForge holds the collection: it opened it and
    /// reads it, whether or not a report came yet.</summary>
    public bool IsReading
    {
        get
        {
            lock (gate)
                return !closed && stream != null
                    && captureState is TabletCaptureState.WaitingForInput or TabletCaptureState.Captured;
        }
    }

    public bool IsCurrentCaptureRevision(int version) => Owns(version);

    public bool CanRollbackCaptureRevision(int version)
    {
        lock (gate) return generation == version || closed && generation == version + 1;
    }

    public void PrepareForUnhide() => PrepareForUnhide(retireCapture: false);

    /// <summary>Closes PadForge's handle before HidHide shows the
    /// collection again, so the restart that follows hands it to Windows.
    /// Retirement keeps this capture from accepting another request.</summary>
    public void PrepareForUnhide(bool retireCapture)
    {
        CancellationTokenSource cancel;
        CaptureInput close;
        int version;
        lock (gate)
        {
            captureRetiring |= retireCapture;
            if (closed || !captureWanted) return;
            version = ++generation;
            requestInvalidated = true;
            captureState = TabletCaptureState.Switching;
            captureError = "";
            cancel = request;
            close = stream;
            stream = null;
        }
        Cancel(cancel);
        close?.Dispose();
        changed?.Invoke(this, version, false, "");
    }

    /// <summary>Starts reading the collection alone, or gives it back. A
    /// capture that ends restarts the collection, so Windows opens it again.
    /// <paramref name="rollbackNewHide"/> marks a capture whose hide was
    /// just written: should it fail before a report arrives, its status
    /// change asks for that hide to be taken back.</summary>
    public void SetCapture(bool wanted, bool rollbackNewHide = false, bool retry = false)
    {
        CancellationTokenSource previous;
        CaptureInput close;
        CancellationTokenSource next;
        int version;
        bool restore;
        lock (gate)
        {
            if (closed || wanted && captureRetiring || !retry && wanted == captureWanted && !requestInvalidated) return;
            restore = attemptedCapture && !wanted;
            captureWanted = wanted;
            requestInvalidated = false;
            attemptedCapture |= wanted;
            version = ++generation;
            previous = request;
            request = next = new CancellationTokenSource();
            close = stream;
            stream = null;
            captureState = wanted || restore ? TabletCaptureState.Switching : TabletCaptureState.Shared;
            captureError = "";
            // Reserved before the task is scheduled, so a later request for
            // this collection waits for this one's restart.
            var turn = TabletCaptureQueue.Reserve(DeviceInstanceId);
            operation = Task.Run(() => ChangeCaptureAsync(version, wanted, restore, rollbackNewHide, next, turn));
        }
        Cancel(previous);
        close?.Dispose();
        changed?.Invoke(this, version, false, "");
    }

    public bool WaitForTransition(TimeSpan timeout)
    {
        Task pending;
        lock (gate) pending = operation;
        try { return pending.Wait(timeout); } catch (AggregateException) { return true; }
    }

    /// <summary>The hiding pass could not confirm what HidHide holds, so the
    /// capture stops and says why.</summary>
    public void ReportPolicyFailure(string error)
    {
        CancellationTokenSource cancel;
        CaptureInput close;
        int version;
        lock (gate)
        {
            if (closed) return;
            captureWanted = false;
            requestInvalidated = true;
            version = ++generation;
            cancel = request;
            close = stream;
            stream = null;
        }
        Cancel(cancel);
        close?.Dispose();
        SetStatus(version, TabletCaptureState.Failed, error);
    }

    private bool Owns(int version)
    {
        lock (gate) return !closed && generation == version;
    }

    private async Task ChangeCaptureAsync(int version, bool wanted, bool restore, bool rollback,
        CancellationTokenSource cancellation, TabletCaptureQueue.Turn turn)
    {
        CancellationToken stop = cancellation.Token;
        bool finishingRestore = false;
        CaptureInput input = null;
        ITouchpadLayout layout = null;
        IntPtr key = IntPtr.Zero;
        try
        {
            // A canceled turn still drains in order, so it cannot let a later
            // open overtake its own native I/O.
            await turn.Predecessor.ConfigureAwait(false);
            stop.ThrowIfCancellationRequested();
            lock (gate)
            {
                bool mayFinishAfterClose = restore && closed && !captureWanted && generation == version + 1;
                if ((closed || generation != version) && !mayFinishAfterClose) return;
                finishingRestore = restore;
            }
            if (!wanted)
            {
                if (restore) restart(stop);
                SetStatus(version, TabletCaptureState.Shared, "");
                return;
            }

            // The layout through a handle that asks for no access, which
            // Windows' exclusive read handle does not refuse.
            layout = host.Describe(this);
            try { input = new CaptureInput(openInput(layout.ReportLength)); }
            catch (IOException)
            {
                // Windows still holds the collection. The restart closes its
                // handle, and HidHide refuses the reopen.
                restart(stop);
                var timer = Stopwatch.StartNew();
                while (input == null)
                {
                    stop.ThrowIfCancellationRequested();
                    try { if (isStarted()) input = new CaptureInput(openInput(layout.ReportLength)); }
                    catch (IOException) when (timer.ElapsedMilliseconds < 3000) { }
                    if (input == null)
                    {
                        if (timer.ElapsedMilliseconds >= 3000) throw new IOException("The touchpad collection did not become available after restart.");
                        await Task.Delay(50, stop).ConfigureAwait(false);
                    }
                }
            }
            lock (gate)
            {
                if (closed || generation != version) return;
                stream = input;
            }
            key = host.Publish(this, layout);
            SetStatus(version, TabletCaptureState.WaitingForInput, "");
            byte[] bytes = new byte[Math.Max(1, layout.ReportLength)];
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int count = await input.Stream.ReadAsync(bytes, stop).ConfigureAwait(false);
                    if (count == 0) throw new IOException("The touchpad input stream ended.");
                    bool recognized;
                    lock (gate)
                    {
                        if (closed || generation != version) return;
                        recognized = host.Feed(key, layout, pin.AddrOfPinnedObject(), count);
                    }
                    if (recognized)
                    {
                        rollback = false;
                        SetStatus(version, TabletCaptureState.Captured, "");
                    }
                }
            }
            finally { pin.Free(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) when (stop.IsCancellationRequested || !Owns(version) && !finishingRestore) { }
        catch (Exception error)
        {
            lock (gate)
            {
                if ((closed || generation != version) && !finishingRestore) return;
            }
            SdlDiagLog.WriteLine($"TOUCHPAD {(wanted ? "capture" : "restore")} failed: {DeviceInstanceId}: {error.Message}");
            SetStatus(version, TabletCaptureState.Failed, error.Message, rollback);
        }
        finally
        {
            try
            {
                if (key != IntPtr.Zero) host.Withdraw(key);
                input?.Dispose();
                layout?.Dispose();
            }
            finally
            {
                lock (gate) { if (ReferenceEquals(stream, input)) stream = null; }
                cancellation.Dispose();
                turn.Complete();
            }
        }
    }

    private void SetStatus(int version, TabletCaptureState status, string error, bool rollback = false)
    {
        bool notify;
        lock (gate)
        {
            if (closed || generation != version) return;
            notify = captureState != status || captureError != error;
            captureState = status;
            captureError = error;
        }
        if (notify) changed?.Invoke(this, version, rollback, error);
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Restarts a touchpad collection no capture reads, so Windows
    /// opens it again now that HidHide shows it: a hide taken back while the
    /// engine was stopped, or one an earlier session left. It waits for any
    /// capture's turn on the collection, as a capture's own restart does.</summary>
    public static Task RestartForWindows(string instanceId)
    {
        var turn = TabletCaptureQueue.Reserve(instanceId);
        return Task.Run(async () =>
        {
            try
            {
                await turn.Predecessor.ConfigureAwait(false);
                TabletNative.RestartCollection(instanceId, CancellationToken.None);
                SdlDiagLog.WriteLine($"TOUCHPAD restarted for Windows: {instanceId}");
            }
            catch (Exception error)
            {
                SdlDiagLog.WriteLine($"TOUCHPAD restart for Windows failed: {instanceId}: {error.Message}");
            }
            finally { turn.Complete(); }
        });
    }

    /// <summary>Whether Windows lists the collection as present now.</summary>
    public static bool IsNodePresent(string instanceId) => TabletNative.IsPresent(instanceId);

    public void Dispose()
    {
        CancellationTokenSource cancel;
        CaptureInput close;
        lock (gate)
        {
            if (closed) return;
            closed = true;
            generation++;
            captureState = TabletCaptureState.Offline;
            // A give-back already asked for finishes after an open still under
            // way returns, and its turn keeps the next open waiting for it.
            cancel = captureWanted ? request : null;
            close = stream;
            stream = null;
        }
        Cancel(cancel);
        close?.Dispose();
    }

    private sealed class CaptureInput : IDisposable
    {
        private readonly object disposeGate = new();
        private bool disposed;
        internal Stream Stream { get; }
        internal CaptureInput(Stream stream) => Stream = stream;
        public void Dispose()
        {
            lock (disposeGate)
            {
                if (disposed) return;
                disposed = true;
                Stream.Dispose();
            }
        }
    }
}
