using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine;

namespace PadForge.Common.Input
{
    /// <summary>
    /// A Sony headset head-tracker IMU (WH-1000XM5 family) exposed over
    /// Bluetooth Classic as an Android Head Tracker HID sensor collection,
    /// surfaced to the pipeline as a motion-only <see cref="ISdlInputDevice"/>
    /// (issue #188). Protocol facts and every HID call sequence are ported
    /// from the reference implementation NicholasSlattery/sony-head-tracker
    /// (MIT): discovery by sensor page 0x20 / usage 0xE1 plus the
    /// #AndroidHeadTracker# sensor-description marker, one combined feature
    /// report per report ID for the enable sequence (interval + Power Full +
    /// All Events + optional v2 ACL transport), and fully descriptor-driven
    /// input parsing via HidP with zero hard-coded byte offsets.
    ///
    /// Like <see cref="MidiInputDevice"/>, the device owns its reader
    /// thread and publishes state under a lock; the 1 kHz poll reads a
    /// pooled clone. Packets arrive at the device-advertised rate (~25/s on
    /// the WH-1000XM5), so <see cref="GetCurrentState"/> holds the last
    /// sample and only reports offline (null) past a stale window measured
    /// in seconds, the RemotePeerDevice contract.
    /// </summary>
    internal sealed class SonyHeadsetMotionDevice : ISdlInputDevice
    {
        /// <summary>No-packet window after which the device reads as
        /// offline. Sony firmware delivers ~25 packets/s; seconds of
        /// silence mean the link is gone, not jittering.</summary>
        internal const int StaleWindowMs = 5000;

        // Ingest-time frame remap into the SDL native frame, the single
        // sign/order seam for this source (feedback_axis_sign_multilayer).
        // Reference types.hpp FilterConfig.axes: YXZ order with X and Z
        // inverted, the map proven for WH-1000XM5 head tracking, applied
        // by the reference to angular velocity and acceleration alike
        // (orientation.cpp:18-20, math.cpp remap). Hypothesis-under-test
        // for the gyro-aim feel until real hardware confirms it.
        private static readonly int[] MapIndex = { 1, 0, 2 };
        private static readonly float[] MapSign = { -1f, 1f, -1f };

        private readonly object _stateLock = new();
        private CustomInputState _state;
        private readonly Func<long> _nowTicks;
        private readonly long _staleTicks;
        private long _lastSampleTicks;
        private bool _everReceived;
        private volatile bool _attached;
        private volatile bool _disposed;

        private SafeFileHandle _handle;
        private IntPtr _preparsed;
        private Thread _reader;
        private ManualResetEvent _readEvent;
        private SonyHeadsetHid.ParsedField[] _inputFields;
        private ushort _inputReportLength;

        // Rotation-only firmware fallback (#188 plan item 3): when the
        // descriptor exposes the rotation vector but no gyro usage, the
        // rate is synthesized from consecutive rotation vectors.
        private bool _synthesizeGyro;
        private double[] _prevRotation;
        private long _prevRotationTicks;

        // A gyro usage can exist in the descriptor while the firmware
        // streams zeros in it (WH-1000XM5, hardware-observed 2026-08-07:
        // thousands of packets, gyro pinned at exactly zero through
        // motion). The reference drives head tracking from the rotation
        // vector, so angular velocity is synthesized from rotation until
        // the device's own gyro field ever produces a nonzero value; from
        // then on the device values win.
        private bool _gyroFieldLive;

        /// <summary>Consecutive all-zero gyro samples seen while the field
        /// was believed live. At the threshold the belief is revoked and
        /// rotation-derived synthesis resumes.</summary>
        private int _gyroZeroRun;

        /// <summary>~2 s at the family's ~25 Hz report rate: long enough
        /// that a genuinely live gyro resting perfectly still does not lose
        /// the lane on a brief idle, short enough that a glitch cannot cost
        /// the user their head tracking for the session.</summary>
        private const int GyroZeroRunToRevoke = 50;

        // Reader diagnostics: sparse, so a silent stream is diagnosable
        // from the DIAG ring without flooding it at packet rate.
        private long _packetsPublished;
        private long _packetsSkipped;

        public SonyHeadsetMotionDevice(SonyHeadsetMotionRuntime.Candidate candidate,
            Func<long> nowTicksProvider = null)
        {
            _nowTicks = nowTicksProvider ?? Stopwatch.GetTimestamp;
            _staleTicks = Stopwatch.Frequency * StaleWindowMs / 1000;

            Name = candidate.Name;
            DevicePath = candidate.Path;
            VendorId = candidate.VendorId;
            ProductId = candidate.ProductId;
            HasAccel = candidate.HasAccel;
            BluetoothAddress = candidate.BluetoothAddress;
            // The HID interface path is stable for the same BTHENUM child
            // across reboots, so it anchors slot assignments and profiles
            // (ConsumerControlWrapper's identity pattern).
            InstanceGuid = Md5Guid("pfheadset:" + candidate.Path.ToLowerInvariant());
            ProductGuid = Md5Guid($"pfheadset-prod:{candidate.VendorId:X4}:{candidate.ProductId:X4}");
            SdlInstanceId = unchecked((uint)DevicePath.GetHashCode());

            _state = new CustomInputState();
            _lastSampleTicks = _nowTicks();
        }

        // ─────────────────────────────────────────────
        //  ISdlInputDevice identity / capabilities
        // ─────────────────────────────────────────────

        // Motion-only surface: the state lives in Gyro[]/Accel[], not the
        // gamepad axis/button arrays, and the mapping picker offers this
        // device through MappingDisplayResolver's motion block keyed on
        // HasGyro/HasAccel (the MIDI empty-objects pattern).
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => true;
        public bool HasAccel { get; }
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        /// <summary>Owning paired device's 48-bit Bluetooth address
        /// (0 when unresolved). The sweep keys the automatic HID-service
        /// re-request on it after the sensor channel drops.</summary>
        internal ulong BluetoothAddress { get; }
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.HeadsetMotion;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;
        public DeviceObjectItem[] GetDeviceObjects() => Array.Empty<DeviceObjectItem>();

        // ─────────────────────────────────────────────
        //  Open: enable sequence + reader thread
        // ─────────────────────────────────────────────

        /// <summary>
        /// Opens the collection, runs the enable sequence, and starts the
        /// reader. Blocking Bluetooth I/O: call from the background sweep
        /// worker, never the polling thread.
        /// </summary>
        public bool Open()
        {
            if (_disposed) return false;
            var handle = SonyHeadsetHid.CreateFile(DevicePath,
                SonyHeadsetHid.GENERIC_READ | SonyHeadsetHid.GENERIC_WRITE,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING,
                SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return false; }

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)
                    || SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return Fail(handle, ref preparsed);

                var inputValues = SonyHeadsetHid.GetValueCaps(SonyHeadsetHid.HidP_Input, preparsed, caps.NumberInputValueCaps);
                var featureValues = SonyHeadsetHid.GetValueCaps(SonyHeadsetHid.HidP_Feature, preparsed, caps.NumberFeatureValueCaps);
                var featureButtons = SonyHeadsetHid.GetButtonCaps(SonyHeadsetHid.HidP_Feature, preparsed, caps.NumberFeatureButtonCaps);

                // The enable sequence must land or the stream never starts;
                // a device that refuses it is not a connected source
                // (reference: configure failure aborts the connect).
                if (!SonyHeadsetHid.ConfigureHeadTrackerFeatures(handle, preparsed, caps, featureValues, featureButtons))
                    return Fail(handle, ref preparsed);

                _inputFields = SonyHeadsetHid.BuildParsedFields(inputValues);
                bool hasGyroUsage = false;
                bool hasRotation = false;
                foreach (var f in _inputFields)
                {
                    if (f.Kind == SonyHeadsetHid.FieldKind.GyroVector
                        || f.Kind == SonyHeadsetHid.FieldKind.GyroScalar) hasGyroUsage = true;
                    if (f.Kind == SonyHeadsetHid.FieldKind.Rotation) hasRotation = true;
                }
                // Orientation-only firmware: synthesize the rate from
                // consecutive rotation vectors. A descriptor with neither
                // gyro nor rotation carries nothing this source can serve.
                _synthesizeGyro = !hasGyroUsage;
                if (_synthesizeGyro && !hasRotation)
                    return Fail(handle, ref preparsed);

                _inputReportLength = caps.InputReportByteLength;
                _handle = handle;
                _preparsed = preparsed;
                _readEvent = new ManualResetEvent(false);
                _attached = true;
                _reader = new Thread(ReaderLoop)
                {
                    IsBackground = true,
                    Name = "PadForge.HeadsetMotion"
                };
                _reader.Start();
                return true;
            }
            catch
            {
                // A throw after the fields were assigned (Thread.Start is
                // the only candidate) must un-assign them, or Fail's free
                // of the local preparsed leaves _preparsed dangling and
                // the caller's Dispose() double-frees it.
                _attached = false;
                _handle = null;
                _preparsed = IntPtr.Zero;
                _readEvent?.Dispose();
                _readEvent = null;
                return Fail(handle, ref preparsed);
            }
        }

        private bool Fail(SafeFileHandle handle, ref IntPtr preparsed)
        {
            if (preparsed != IntPtr.Zero) { SonyHeadsetHid.HidD_FreePreparsedData(preparsed); preparsed = IntPtr.Zero; }
            handle.Dispose();
            return false;
        }

        // ─────────────────────────────────────────────
        //  Reader thread: overlapped reads → state
        // ─────────────────────────────────────────────

        private void ReaderLoop()
        {
            var report = new byte[_inputReportLength];
            var values = new double[3];
            var gyro = new double[3];
            var accel = new double[3];
            // The kernel owns the read buffer and the OVERLAPPED until each
            // I/O completes, which is long after ReadFile returns, so both
            // are pinned for the thread's whole lifetime.
            var reportPin = GCHandle.Alloc(report, GCHandleType.Pinned);
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            bool ioPending = false;
            try
            {
                var overlapped = new NativeOverlapped
                {
                    EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
                };
                try
                {
                    ReadPackets(report, values, gyro, accel, overlapped, overlappedPtr,
                        reportPin.AddrOfPinnedObject(), ref ioPending);
                }
                catch
                {
                    // A dying Bluetooth link can surface as an exception
                    // from any native call here; the sweep's IsAttached
                    // check retires the device. Never let the reader
                    // thread take the process down (MidiInputDevice's
                    // callback discipline).
                }
            }
            finally
            {
                // A pending read must fully complete before the buffer and
                // OVERLAPPED can be released back to the GC/heap.
                DrainPendingRead(overlappedPtr, ref ioPending);
                Marshal.FreeHGlobal(overlappedPtr);
                reportPin.Free();
                _attached = false;
            }
        }

        private void ReadPackets(byte[] report, double[] values, double[] gyro, double[] accel,
            NativeOverlapped overlapped, IntPtr overlappedPtr, IntPtr reportPtr, ref bool ioPending)
        {
            while (_attached && !_disposed)
            {
                _readEvent.Reset();
                Marshal.StructureToPtr(overlapped, overlappedPtr, false);
                ioPending = false;
                if (!SonyHeadsetHid.ReadFile(_handle, reportPtr,
                        (uint)report.Length, out uint bytes, overlappedPtr))
                {
                    int readError = Marshal.GetLastWin32Error();
                    if (readError != SonyHeadsetHid.ERROR_IO_PENDING)
                    {
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"Headset: ReadFile failed (Win32 {readError}); reader exiting");
                        break;
                    }
                    ioPending = true;
                }
                // Bounded waits so teardown is never stranded behind a
                // silent device (reference reader: 100 ms slices).
                while (_attached && !_disposed)
                {
                    if (_readEvent.WaitOne(100)) break;
                }
                if (!_attached || _disposed)
                {
                    DrainPendingRead(overlappedPtr, ref ioPending);
                    break;
                }
                if (!SonyHeadsetHid.GetOverlappedResult(_handle, overlappedPtr, out bytes, false))
                {
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"Headset: read completion failed (Win32 {Marshal.GetLastWin32Error()}); reader exiting");
                    DrainPendingRead(overlappedPtr, ref ioPending);
                    break;
                }
                ioPending = false;
                if (bytes == 0) continue;

                bool gotGyro = false, gotAccel = false, gotRotation = false;
                double r0 = 0, r1 = 0, r2 = 0;
                foreach (ref var field in _inputFields.AsSpan())
                {
                    // Per the reference parse loop: skip fields whose
                    // report ID does not match byte 0 of this report.
                    if (field.ReportId != 0 && report[0] != field.ReportId) continue;
                    switch (field.Kind)
                    {
                        case SonyHeadsetHid.FieldKind.Rotation:
                            if (SonyHeadsetHid.ReadVector(_preparsed, in field, report, (int)bytes, values))
                            { r0 = values[0]; r1 = values[1]; r2 = values[2]; gotRotation = true; }
                            break;
                        case SonyHeadsetHid.FieldKind.GyroVector:
                            if (SonyHeadsetHid.ReadVector(_preparsed, in field, report, (int)bytes, values))
                            { gyro[0] = values[0]; gyro[1] = values[1]; gyro[2] = values[2]; gotGyro = true; }
                            break;
                        case SonyHeadsetHid.FieldKind.AccelVector:
                            if (SonyHeadsetHid.ReadVector(_preparsed, in field, report, (int)bytes, values))
                            { accel[0] = values[0]; accel[1] = values[1]; accel[2] = values[2]; gotAccel = true; }
                            break;
                        case SonyHeadsetHid.FieldKind.GyroScalar:
                            if (SonyHeadsetHid.ReadScalar(_preparsed, in field, report, (int)bytes, out double gv))
                            { gyro[field.Axis] = gv; gotGyro = true; }
                            break;
                        case SonyHeadsetHid.FieldKind.AccelScalar:
                            if (SonyHeadsetHid.ReadScalar(_preparsed, in field, report, (int)bytes, out double av))
                            { accel[field.Axis] = av; gotAccel = true; }
                            break;
                    }
                }

                long now = _nowTicks();
                if (!gotGyro && _synthesizeGyro && gotRotation)
                    gotGyro = SynthesizeGyroFromRotation(r0, r1, r2, now, gyro);
                else if (gotGyro)
                {
                    bool nonZero = Math.Abs(gyro[0]) > 1e-9 || Math.Abs(gyro[1]) > 1e-9 || Math.Abs(gyro[2]) > 1e-9;
                    if (nonZero)
                    {
                        _gyroZeroRun = 0;
                        if (!_gyroFieldLive)
                        {
                            _gyroFieldLive = true;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                "Headset: gyro field is live; using device angular velocity");
                        }
                    }
                    else if (gotRotation)
                    {
                        // Field present but silent. The rotation vector is
                        // the authoritative motion source, so synthesize.
                        //
                        // REVOCABLE, not a one-way latch (audit 2026-08-08,
                        // lens 1t). This family streams an all-zero gyro
                        // word while rotation carries the real motion, so a
                        // single nonzero sample (a startup artifact, one
                        // glitched decode) used to disable synthesis for
                        // the life of the device object with no exit but a
                        // reopen. A sustained zero run now hands the lane
                        // back to rotation.
                        if (_gyroFieldLive && ++_gyroZeroRun >= GyroZeroRunToRevoke)
                        {
                            _gyroFieldLive = false;
                            _gyroZeroRun = 0;
                            PadForge.Engine.SdlDiagLog.WriteLine(
                                "Headset: gyro field went silent; falling back to rotation-derived rate");
                        }
                        if (!_gyroFieldLive)
                            gotGyro = SynthesizeGyroFromRotation(r0, r1, r2, now, gyro);
                    }
                }

                if (!gotGyro && !gotAccel)
                {
                    // Packets arriving but nothing decodable is its own
                    // failure mode; make it visible without packet-rate spam.
                    _packetsSkipped++;
                    if (_packetsSkipped == 1 || (_packetsSkipped & 1023) == 0)
                        PadForge.Engine.SdlDiagLog.WriteLine(
                            $"Headset: packet #{_packetsSkipped} carried no decodable motion (reportId={report[0]}, {bytes} bytes, rotation={gotRotation})");
                    continue;
                }

                lock (_stateLock)
                {
                    if (gotGyro)
                        for (int i = 0; i < 3; i++)
                            _state.Gyro[i] = (float)(gyro[MapIndex[i]] * MapSign[i]);
                    if (gotAccel)
                        for (int i = 0; i < 3; i++)
                            _state.Accel[i] = (float)(accel[MapIndex[i]] * MapSign[i]);
                    _lastSampleTicks = now;
                    _everReceived = true;
                }
                _packetsPublished++;
                // Dense at the start (powers of two to 64), sparse after,
                // so a short manual test window is fully visible in the
                // DIAG ring.
                long n = _packetsPublished;
                if ((n <= 64 && (n & (n - 1)) == 0) || (n & 1023) == 0)
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        $"Headset: sample #{n} gyro=({gyro[0]:F3},{gyro[1]:F3},{gyro[2]:F3}) rot=({r0:F3},{r1:F3},{r2:F3}) live={_gyroFieldLive} synthUsage={_synthesizeGyro}");
            }
        }

        /// <summary>Cancels an in-flight overlapped read and waits for its
        /// completion, so the kernel is done with the pinned buffer.</summary>
        private void DrainPendingRead(IntPtr overlappedPtr, ref bool ioPending)
        {
            if (!ioPending) return;
            try
            {
                var handle = _handle;
                if (handle != null && !handle.IsInvalid)
                {
                    SonyHeadsetHid.CancelIoEx(handle, overlappedPtr);
                    SonyHeadsetHid.GetOverlappedResult(handle, overlappedPtr, out _, true);
                }
            }
            catch { }
            ioPending = false;
        }

        /// <summary>
        /// Rotation-only fallback: angular velocity from the quaternion
        /// delta of consecutive rotation vectors over their arrival gap.
        /// The protocol's rotation vector is axis-angle in radians
        /// (issue #188 protocol notes).
        /// </summary>
        internal bool SynthesizeGyroFromRotation(double r0, double r1, double r2, long nowTicks, double[] gyroOut)
        {
            // Snapshot the previous sample BEFORE overwriting the storage:
            // _prevRotation is the same array across calls, so reading it
            // after the write would compare the current sample to itself
            // and synthesize a permanent zero rate.
            bool hadPrev = _prevRotation != null;
            double p0 = 0, p1 = 0, p2 = 0;
            long prevTicks = _prevRotationTicks;
            if (hadPrev) { p0 = _prevRotation[0]; p1 = _prevRotation[1]; p2 = _prevRotation[2]; }
            _prevRotation ??= new double[3];
            _prevRotation[0] = r0; _prevRotation[1] = r1; _prevRotation[2] = r2;
            _prevRotationTicks = nowTicks;
            if (!hadPrev) return false;
            double dt = (nowTicks - prevTicks) / (double)Stopwatch.Frequency;
            if (dt <= 0 || dt > 1.0) return false;
            return HeadTrackerMath.AngularRateFromRotationVectors(
                p0, p1, p2, r0, r1, r2, dt, gyroOut);
        }

        // ─────────────────────────────────────────────
        //  State read (poll thread)
        // ─────────────────────────────────────────────

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                // Null past the stale window once samples have flowed, so
                // silent link loss routes the device offline instead of a
                // frozen gyro steering aim forever (RemotePeerDevice
                // contract). Before the first sample, the zero baseline
                // keeps a freshly registered device from flapping.
                if (_everReceived && (_nowTicks() - _lastSampleTicks) > _staleTicks)
                    return null;
                var dst = _statePool.Next();
                _state.CopyInto(dst);
                return dst;
            }
        }

        /// <summary>Test seam: publish a gyro/accel sample through the same
        /// lock and remap the reader thread uses.</summary>
        internal void InjectSample(double[] gyro, double[] accel)
        {
            lock (_stateLock)
            {
                if (gyro != null)
                    for (int i = 0; i < 3; i++)
                        _state.Gyro[i] = (float)(gyro[MapIndex[i]] * MapSign[i]);
                if (accel != null)
                    for (int i = 0; i < 3; i++)
                        _state.Accel[i] = (float)(accel[MapIndex[i]] * MapSign[i]);
                _lastSampleTicks = _nowTicks();
                _everReceived = true;
            }
        }

        /// <summary>Test seam: mark the wrapper live without a device.</summary>
        internal void AttachForTest() => _attached = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            var reader = _reader;
            _reader = null;
            bool joined = true;
            if (reader != null)
            {
                try { if (_handle != null && !_handle.IsInvalid) SonyHeadsetHid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
                try { _readEvent?.Set(); } catch { }
                joined = reader.Join(2000);
            }
            if (!joined)
            {
                // The reader is still inside a native call that touches the
                // handle, the preparsed data, or the event. Freeing them now
                // would be a use-after-free; leak the trio instead (the
                // SafeFileHandle finalizer eventually closes the handle,
                // which unsticks any pending I/O). The 100 ms wait slices
                // make this a near-impossible path.
                return;
            }
            if (_preparsed != IntPtr.Zero)
            {
                SonyHeadsetHid.HidD_FreePreparsedData(_preparsed);
                _preparsed = IntPtr.Zero;
            }
            _handle?.Dispose();
            _handle = null;
            _readEvent?.Dispose();
            _readEvent = null;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
