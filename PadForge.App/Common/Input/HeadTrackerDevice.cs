using System;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>Which source delivered the latest pose.</summary>
    internal enum HeadTrackerSource
    {
        None = 0,
        Udp = 1,
        FreeTrack = 2,
    }

    /// <summary>
    /// The Head Tracker device row (issue #355): a synthetic
    /// <see cref="ISdlInputDevice"/> with six absolute axes fed by OpenTrack's
    /// "UDP over network" output and by the FreeTrack 2.0 shared memory,
    /// both at once. The <see cref="SystemMotionDevice"/> shape: a source
    /// publishes under a lock and the poll reads a pooled clone.
    ///
    /// <para>UDP: a socket on every interface at the configured port, one
    /// receive thread, 48-byte datagrams decoded by
    /// <see cref="HeadPose.TryDecodeOpenTrackUdp"/>. FreeTrack: the
    /// <c>FT_SharedMem</c> mapping polled from the read path, a new pose
    /// recognized by its DataID changing. The two carry the same pose from
    /// the same tracker, so interleaving them is harmless.</para>
    ///
    /// <para>Silence. A tracker that stops (OpenTrack closed, the camera lost
    /// the face) must not leave a stick pinned: after <see cref="SilenceMs"/>
    /// without a pose the axes return to center. The row stays online so
    /// mappings can be made before the tracker is started.</para>
    /// </summary>
    internal sealed class HeadTrackerDevice : ISdlInputDevice
    {
        private const ushort HeadTrackerVendorId = 0x4854;  // "HT"
        private const ushort HeadTrackerProductId = 0x4F54; // "OT"
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        internal const string FirewallRuleName = "PadForge Head Tracking";

        /// <summary>Poses older than this recenter the axes.</summary>
        public const int SilenceMs = 1000;

        private static readonly int[] s_axisIndices = { 0, 1, 2, 3, 4, 5 };
        private static readonly string[] s_axisNames =
            { "Head Yaw", "Head Pitch", "Head Roll", "Head X", "Head Y", "Head Z" };
        private static readonly Guid[] s_axisGuids =
            { ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis, ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis };

        private readonly object _stateLock = new();
        private readonly CustomInputState _state = new();
        private readonly double[] _pose = new double[HeadPose.PoseCount];
        private readonly Func<long> _now;
        private long _lastPoseTicks; // 0 = never
        private HeadTrackerSource _source;
        private string _udpPeer = string.Empty;
        private volatile int _statusVersion;
        private volatile bool _attached;
        private volatile bool _disposed;
        private long _samples;

        private readonly int _port;
        private readonly bool _freeTrackEnabled;
        private Socket _socket;
        private Thread _thread;
        private volatile bool _running;
        private volatile bool _udpBindFailed;
        private volatile bool _freeTrackFailed;

        private FreeTrackReader _freeTrack;
        private readonly byte[] _ftBuf = new byte[HeadPose.FreeTrackHeapBytes];
        private readonly double[] _ftPose = new double[HeadPose.PoseCount];
        private uint _ftLastId;
        private bool _ftSeen;

        private PooledInputStatePair _statePool;

        /// <summary>The <see cref="HeadTrackingRuntime.Version"/> this row
        /// was opened under. The sweep reopens the row when it moves.</summary>
        public int ConfigVersion { get; }

        /// <summary>Reads the live configuration. Version is read FIRST and
        /// the settings after it, because the setters bump Version last: read
        /// the other way round, a device could capture the NEW version with
        /// the OLD port, and the sweep's reconfigured check would then never
        /// fire again for that change.</summary>
        public static HeadTrackerDevice FromCurrentSettings()
        {
            int version = HeadTrackingRuntime.Version;
            return new HeadTrackerDevice(HeadTrackingRuntime.UdpPort, HeadTrackingRuntime.FreeTrackEnabled, version, null);
        }

        internal HeadTrackerDevice(int port, bool freeTrack, int configVersion, Func<long> now)
        {
            _port = port;
            _freeTrackEnabled = freeTrack;
            ConfigVersion = configVersion;
            _now = now ?? (() => Environment.TickCount64);
            Name = "Head Tracker (OpenTrack)";
            DevicePath = "headtrack://opentrack";
            InstanceGuid = Md5Guid("pfheadtrack:opentrack");
            ProductGuid = Md5Guid("pfheadtrack-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            HeadPose.CenterAxes(_state.Axis);
        }

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => HeadPose.AxisCount;
        public int NumButtons => 0;
        public int RawButtonCount => 0;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => Array.Empty<int>();
        public int[] SupportedAxisIndices => s_axisIndices;
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => false;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad => false;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached && !_disposed;
        public ushort VendorId => HeadTrackerVendorId;
        public ushort ProductId => HeadTrackerProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.HeadTracker;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>Six named absolute axes at indices 0 to 5.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var items = new DeviceObjectItem[HeadPose.AxisCount];
            for (int i = 0; i < items.Length; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = s_axisNames[i],
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    ObjectTypeGuid = s_axisGuids[i],
                    InputIndex = i,
                    Offset = i * 4,
                };
            return items;
        }

        // ─── Status, for the Devices page ───

        public int UdpPort => _port;
        public bool UdpBindFailed => _udpBindFailed;
        public bool FreeTrackEnabled => _freeTrackEnabled;

        /// <summary>FreeTrack was asked for and its mapping did not open.
        /// Without this the status line reads the same as a mapping nobody
        /// writes to, which is the one case a user cannot diagnose.</summary>
        public bool FreeTrackFailed => _freeTrackEnabled && _freeTrackFailed;

        /// <summary>Bumps whenever <see cref="Source"/> or
        /// <see cref="UdpPeer"/> changes, so a preview loop can rebuild its
        /// status text only then.</summary>
        public int StatusVersion => _statusVersion;

        public HeadTrackerSource Source
        {
            get { lock (_stateLock) return _source; }
        }

        /// <summary>"address:port" of the last UDP sender.</summary>
        public string UdpPeer
        {
            get { lock (_stateLock) return _udpPeer; }
        }

        // ─── Lifecycle ───

        /// <summary>Binds the UDP socket and opens the FreeTrack mapping.
        /// Neither blocks. The row opens even when both fail, so the status
        /// line can say why nothing arrives.</summary>
        public bool Open()
        {
            if (_disposed) return false;
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // Suppress ICMP port-unreachable surfacing as a SocketException
                // on the next receive (the DSU server's rule).
                try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
                // Exclusive: a second listener (OpenTrack's own UDP tracker
                // on the same port) must surface as a conflict, not steal
                // half the datagrams.
                s.ExclusiveAddressUse = true;
                s.Bind(new IPEndPoint(IPAddress.Any, _port));
                _socket = s;
                _running = true;
                _thread = new Thread(ReceiveLoop)
                {
                    Name = "PadForge.HeadTrackerUdp",
                    IsBackground = true,
                };
                _thread.Start();
                SdlDiagLog.WriteLine($"Head tracker: listening on UDP port {_port}");
                // Best effort, off-thread: netsh can block for seconds.
                System.Threading.Tasks.Task.Run(() =>
                    PadForge.Services.WebControllerServer.EnsureInboundFirewallRule(FirewallRuleName, "UDP", _port));
            }
            catch (Exception ex)
            {
                _udpBindFailed = true;
                try { _socket?.Dispose(); } catch { }
                _socket = null;
                SdlDiagLog.WriteLine($"Head tracker: UDP port {_port} bind failed: {ex.Message}");
            }

            if (_freeTrackEnabled)
            {
                var ft = new FreeTrackReader();
                if (ft.Open())
                {
                    _freeTrack = ft;
                    SdlDiagLog.WriteLine("Head tracker: FreeTrack shared memory open");
                }
                else
                {
                    ft.Dispose();
                    _freeTrackFailed = true;
                }
            }

            _attached = true;
            return true;
        }

        private void ReceiveLoop()
        {
            var buf = new byte[256];
            var pose = new double[HeadPose.PoseCount];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            IPEndPoint lastPeer = null;
            int errors = 0;
            while (_running)
            {
                int n;
                try
                {
                    n = _socket.ReceiveFrom(buf, ref ep);
                    errors = 0;
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException)
                {
                    if (!_running) break;
                    if (++errors > 10) Thread.Sleep(50);
                    continue;
                }
                catch (Exception) { break; }

                if (n < HeadPose.OpenTrackUdpBytes) continue;
                if (!HeadPose.TryDecodeOpenTrackUdp(buf.AsSpan(0, n), pose)) continue;

                string peer = null;
                if (ep is IPEndPoint ipep && (lastPeer == null || !lastPeer.Equals(ipep)))
                {
                    lastPeer = new IPEndPoint(ipep.Address, ipep.Port);
                    peer = lastPeer.ToString();
                }
                Publish(pose, HeadTrackerSource.Udp, peer);
            }
        }

        /// <summary>Reads the FreeTrack heap and publishes a pose when the
        /// DataID moved. The first read is only a baseline: the heap keeps
        /// the last pose of a previous run, and a stale mapping must not
        /// move the axes. Only a change proves a writer.</summary>
        private void PollFreeTrack()
        {
            var ft = _freeTrack;
            if (ft == null) return;
            if (!ft.TryRead(_ftBuf)) return;
            OnFreeTrackHeap(_ftBuf);
        }

        private void OnFreeTrackHeap(ReadOnlySpan<byte> heap)
        {
            if (!HeadPose.TryDecodeFreeTrackHeap(heap, out uint id, _ftPose)) return;
            if (_ftSeen && id == _ftLastId) return;
            bool first = !_ftSeen;
            _ftSeen = true;
            _ftLastId = id;
            if (first) return;
            Publish(_ftPose, HeadTrackerSource.FreeTrack, null);
        }

        private void Publish(double[] pose, HeadTrackerSource source, string peer)
        {
            lock (_stateLock)
            {
                Array.Copy(pose, _pose, HeadPose.PoseCount);
                _lastPoseTicks = _now();
                if (_source != source || (peer != null && peer != _udpPeer))
                {
                    _source = source;
                    if (peer != null) _udpPeer = peer;
                    _statusVersion++;
                }
            }
            long n = Interlocked.Increment(ref _samples);
            if ((n <= 64 && (n & (n - 1)) == 0) || (n & 4095) == 0)
                SdlDiagLog.WriteLine(
                    $"Head tracker: pose #{n} via {source} yaw={pose[HeadPose.Yaw]:F1} pitch={pose[HeadPose.Pitch]:F1} roll={pose[HeadPose.Roll]:F1} x={pose[HeadPose.TX]:F1} y={pose[HeadPose.TY]:F1} z={pose[HeadPose.TZ]:F1}");
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            PollFreeTrack();
            lock (_stateLock)
            {
                long now = _now();
                bool live = _lastPoseTicks != 0 && now - _lastPoseTicks <= SilenceMs;
                if (live)
                {
                    HeadPose.FillAxes(_pose, HeadTrackingRuntime.RotationRangeDeg,
                        HeadTrackingRuntime.TranslationRangeCm, _state.Axis);
                }
                else
                {
                    HeadPose.CenterAxes(_state.Axis);
                    if (_source != HeadTrackerSource.None)
                    {
                        _source = HeadTrackerSource.None;
                        _statusVersion++;
                    }
                }
                var dst = _statePool.Next();
                _state.CopyInto(dst);
                return dst;
            }
        }

        // ─── Test seams ───

        /// <summary>A UDP datagram as the socket would deliver it.</summary>
        internal void InjectUdp(byte[] datagram, string peer)
        {
            var pose = new double[HeadPose.PoseCount];
            if (datagram.Length < HeadPose.OpenTrackUdpBytes) return;
            if (!HeadPose.TryDecodeOpenTrackUdp(datagram, pose)) return;
            Publish(pose, HeadTrackerSource.Udp, peer);
        }

        /// <summary>A FreeTrack heap image as one poll would read it.</summary>
        internal void InjectFreeTrackHeap(byte[] heap) => OnFreeTrackHeap(heap);

        internal void AttachForTest() => _attached = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            _running = false;
            // Close first: that is what unblocks ReceiveFrom. Dispose runs on
            // the POLL thread (the sweep retires the row), so the join is a
            // courtesy with a short bound rather than a wait. Every exit path
            // in the loop already swallows a disposed socket, so a thread
            // that outlives this call ends on its next iteration anyway.
            try { _socket?.Close(); } catch { }
            try { _thread?.Join(50); } catch { }
            _thread = null;
            _socket = null;
            try { _freeTrack?.Dispose(); } catch { }
            _freeTrack = null;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }

    /// <summary>
    /// The FreeTrack 2.0 client side (issue #355), the reference
    /// freetrackclient.c: <c>CreateFileMapping</c> on <c>FT_SharedMem</c> so
    /// launch order does not matter, a 16 ms wait on <c>FT_Mutext</c> per
    /// read, then a copy of the heap. When the mutex cannot be created this
    /// reads unlocked, which the reference client does NOT do (its FTGetData
    /// copies nothing at all in that case). A torn pose is a worse answer
    /// than a stale one, but no answer at all is worse still for a source
    /// whose only job is to report one.
    /// </summary>
    internal sealed class FreeTrackReader : IDisposable
    {
        public const string HeapName = "FT_SharedMem";
        public const string MutexName = "FT_Mutext";
        private const int MutexWaitMs = 16;

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private Mutex _mutex;

        public bool Open()
        {
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(HeapName, HeadPose.FreeTrackHeapBytes, MemoryMappedFileAccess.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, HeadPose.FreeTrackHeapBytes, MemoryMappedFileAccess.Read);
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine("Head tracker: FreeTrack mapping failed " + ex.Message);
                Dispose();
                return false;
            }
            try { _mutex = new Mutex(false, MutexName); }
            catch { _mutex = null; }
            return true;
        }

        /// <summary>Copies the heap into <paramref name="dst"/>. False when
        /// the mutex was busy past the wait or the view is gone.</summary>
        public bool TryRead(byte[] dst)
        {
            var view = _view;
            if (view == null) return false;
            bool held = false;
            try
            {
                var m = _mutex;
                if (m != null)
                {
                    try { held = m.WaitOne(MutexWaitMs); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) return false;
                }
                view.ReadArray(0, dst, 0, dst.Length);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (held)
                {
                    try { _mutex?.ReleaseMutex(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _view = null;
            _mmf = null;
            _mutex = null;
        }
    }
}
