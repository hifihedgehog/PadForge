using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PadForge.Engine.Tablets;

public enum TabletCaptureState
{
    Shared,
    Switching,
    WaitingForInput,
    Captured,
    Failed,
    Offline
}

public sealed class WindowsTabletDevice : ISdlInputDevice
{
    private readonly TabletReportDescriptor descriptor;
    private readonly object gate = new();
    private readonly Action<WindowsTabletDevice, int, bool, string> changed;
    private CancellationTokenSource request;
    private CaptureInput stream;
    private readonly Func<Stream> openInput;
    private readonly Action<CancellationToken> restart;
    private readonly Func<bool> isStarted;
    private Task operation = Task.CompletedTask;
    private int generation;
    private bool closed, captureWanted, attemptedCapture, requestInvalidated, captureRetiring;
    private TabletCaptureState captureState;
    private string captureError = "";
    private PooledInputStatePair statePool;
    private readonly int[] buttonIndices;

    internal WindowsTabletDevice(TabletReportDescriptor report, Action<WindowsTabletDevice, int, bool, string> onChanged,
        Func<Stream> openInput = null, Action<CancellationToken> restart = null, Func<bool> isStarted = null)
    {
        descriptor = report;
        changed = onChanged;
        this.openInput = openInput ?? (() => new FileStream(DevicePath, FileMode.Open, FileAccess.Read, FileShare.None, descriptor.ReportLength, FileOptions.Asynchronous));
        this.restart = restart ?? (stop => TabletNative.RestartCollection(DeviceInstanceId, stop));
        this.isStarted = isStarted ?? (() => TabletNative.IsStarted(DeviceInstanceId));
        InstanceGuid = SdlDeviceWrapper.BuildInstanceGuid(report.Path, report.Vendor, report.Product, 0);
        ProductGuid = SdlDeviceWrapper.BuildInstanceGuid($"tablet:{report.Vendor:X4}:{report.Product:X4}", report.Vendor, report.Product, 0);
        buttonIndices = Enumerable.Range(0, report.ButtonCount).ToArray();
    }

    public uint SdlInstanceId => (uint)(InstanceGuid.GetHashCode() & int.MaxValue);
    public string Name => descriptor.Name;
    public int NumAxes => 0;
    public int NumButtons => buttonIndices.Length;
    public int RawButtonCount => NumButtons;
    public int NumHats => 0;
    public int[] SupportedButtonIndices => buttonIndices;
    public int[] SupportedAxisIndices => Array.Empty<int>();
    public IntPtr GamepadHandle => IntPtr.Zero;
    public bool HasRumble => false;
    public bool HasRumbleTriggers => false;
    public bool HasHaptic => false;
    public bool HasGyro => false;
    public bool HasAccel => false;
    public bool HasTouchpad => true;
    public int NumTouchpads => 1;
    public int[] TouchpadFingerCounts { get; } = new[] { 1 };
    public bool? TouchpadPressureSupported => descriptor.HasPressure;
    public bool? TouchpadClickSupported => false;
    public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
    public IntPtr HapticHandle => IntPtr.Zero;
    public uint HapticFeatures => 0;
    public int NumHapticAxes => 0;
    public bool IsAttached { get { lock (gate) return !closed; } }
    public ushort VendorId => descriptor.Vendor;
    public ushort ProductId => descriptor.Product;
    public Guid InstanceGuid { get; }
    public Guid ProductGuid { get; }
    public string DevicePath => descriptor.Path;
    public string DeviceInstanceId => descriptor.InstanceId;
    public string SerialNumber => descriptor.Serial;
    public string SdlGuid => "";
    public TabletCaptureState CaptureState { get { lock (gate) return captureState; } }
    public string CaptureError { get { lock (gate) return captureError; } }
    public bool CaptureWasRequested { get { lock (gate) return attemptedCapture; } }
    public bool IsCurrentCaptureRevision(int version) => Owns(version);
    public bool CanRollbackCaptureRevision(int version)
    {
        lock (gate) return generation == version || closed && generation == version + 1;
    }

    public CustomInputState GetCurrentState(bool forceRaw = false)
    {
        var state = statePool.Next();
        descriptor.CopyInto(state);
        return state;
    }

    public DeviceObjectItem[] GetDeviceObjects()
        => buttonIndices.Select(index => new DeviceObjectItem
        {
            InputIndex = index,
            ObjectTypeGuid = ObjectGuid.Button,
            ObjectType = DeviceObjectTypeFlags.PushButton,
            Name = descriptor.Buttons[index].Page == 13 ? descriptor.Buttons[index].Usage switch
            {
                0x44 => "Pen Barrel",
                0x5A => "Pen Secondary Barrel",
                0x45 => "Pen Eraser",
                0x3C => "Pen Inverted",
                0x32 => "Pen In Range",
                _ => $"Button {index}"
            } : $"Button {index}",
            Offset = index * 4
        }).ToArray();

    public int GetInputDeviceType() => InputDeviceType.Tablet;
    public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
    public bool StopRumble() => false;

    internal void FeedRaw(IntPtr report, int length)
    {
        lock (gate)
        {
            if (closed || stream != null || captureWanted) return;
            descriptor.Decode(report, length);
        }
    }

    internal void ClearSharedState()
    {
        lock (gate)
        {
            if (closed || stream != null || captureWanted) return;
            descriptor.Reset();
        }
    }

    public void PrepareForUnhide()
        => PrepareForUnhide(retireCapture: false);

    /// <summary>Retirement prevents this wrapper from accepting another capture request.</summary>
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
            descriptor.Reset();
        }
        Cancel(cancel);
        close?.Dispose();
        changed?.Invoke(this, version, false, "");
    }

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
            descriptor.Reset();
            // Reserve before scheduling so a replacement reader follows the pending restore.
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
            descriptor.Reset();
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
        try
        {
            // Even a canceled turn drains in order. It cannot let a later reader overtake native I/O.
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
            try { input = new CaptureInput(openInput()); }
            catch (IOException)
            {
                restart(stop);
                var timer = Stopwatch.StartNew();
                while (input == null)
                {
                    stop.ThrowIfCancellationRequested();
                    try { if (isStarted()) input = new CaptureInput(openInput()); }
                    catch (IOException) when (timer.ElapsedMilliseconds < 3000) { }
                    if (input == null)
                    {
                        if (timer.ElapsedMilliseconds >= 3000) throw new IOException("The tablet input collection did not become available after restart.");
                        await Task.Delay(50, stop).ConfigureAwait(false);
                    }
                }
            }
            lock (gate)
            {
                if (closed || generation != version) return;
                stream = input;
            }
            SetStatus(version, TabletCaptureState.WaitingForInput, "");
            byte[] bytes = new byte[descriptor.ReportLength];
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    int count = await input.Stream.ReadAsync(bytes, stop).ConfigureAwait(false);
                    if (count == 0) throw new IOException("The tablet input stream ended.");
                    bool recognized;
                    lock (gate)
                    {
                        if (closed || generation != version) return;
                        recognized = descriptor.Decode(pin.AddrOfPinnedObject(), count);
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
                if (closed || generation != version)
                {
                    if (!finishingRestore) return;
                }
                else descriptor.Reset();
            }
            SdlDiagLog.WriteLine($"TABLET {(wanted ? "capture" : "restore")} failed: {DeviceInstanceId}: {error.Message}");
            SetStatus(version, TabletCaptureState.Failed, error.Message, rollback);
        }
        finally
        {
            try { input?.Dispose(); }
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
            // An already-requested unhide must finish after an outstanding native open returns.
            // Its queue turn also blocks a replacement reader from opening too early.
            cancel = captureWanted ? request : null;
            close = stream;
            stream = null;
        }
        Cancel(cancel);
        close?.Dispose();
        descriptor.Dispose();
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
