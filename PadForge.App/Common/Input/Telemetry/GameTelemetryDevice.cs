using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PadForge.Common.Telemetry;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>Why the row's socket is or is not listening.</summary>
    internal enum TelemetryListenState
    {
        /// <summary>No socket: Assetto Corsa is picked, which only the
        /// shared memory carries.</summary>
        SharedMemoryOnly = 0,
        Listening = 1,
        PortInUse = 2,
    }

    /// <summary>What the status line needs, read whole under the lock.</summary>
    internal readonly record struct TelemetryStatus(
        TelemetryListenState State,
        int Port,
        TelemetrySource Source,
        int F1Year,
        string CustomName,
        string Peer,
        int ForwardPort);

    /// <summary>
    /// The Game Telemetry row (issue #531): a synthetic
    /// <see cref="ISdlInputDevice"/> fed by the UDP telemetry driving games
    /// send and by Assetto Corsa's shared memory. <see cref="HeadTrackerDevice"/>'s
    /// shape: a socket on every interface at the configured port, one receive
    /// thread, the frame published under a lock, and a pooled clone on every
    /// read.
    ///
    /// <para>Every interface, because Project CARS 2 broadcasts and Forza on
    /// a console sends across the network. Every packet that arrives, read or
    /// not, is copied on to 127.0.0.1 at the forward port when one is set, so
    /// another tool keeps listening beside PadForge (Forza-Telemetry-Splitter,
    /// tm-bt-led's forwardPorts).</para>
    ///
    /// <para>The wheel RPM lights' hub (<see cref="TelemetryHub"/>) listens
    /// on 20777, 5300 and 4444 while a wheel wants lights. The row takes its
    /// port from the hub before it binds, the lights read the game from the
    /// row while it is live (<see cref="GameTelemetryRowSource"/>), and the
    /// port goes back to the hub when the row closes.</para>
    ///
    /// <para>Seven axes (Speed, RPM, Throttle, Brake, Clutch, Handbrake,
    /// Steering), one more per custom variable at the axis that variable
    /// keeps (<see cref="TelemetryVariable.Slot"/>), and 21 buttons (Driving,
    /// Reverse, Neutral, Gear 1 to Gear 18). After <see cref="SilenceMs"/>
    /// with no frame everything returns to rest, except the ETS2 plugin's
    /// values, held for <see cref="TruckSimulatorHoldMs"/> since it sends only
    /// when a value changes. The row stays online so mappings can be made
    /// before a game starts.</para>
    /// </summary>
    internal sealed class GameTelemetryDevice : ISdlInputDevice
    {
        private const ushort TelemetryVendorId = 0x4754;  // "GT"
        private const ushort TelemetryProductId = 0x5544; // "UD"
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        internal const string FirewallRuleName = "PadForge Game Telemetry";

        public const int SilenceMs = 1000;
        public const int TruckSimulatorHoldMs = 10_000;

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests GameTelemetryPortOffLimits), never by the app. The
        /// games on the PC send to these ports, and a running PadForge or
        /// SimHub may hold them. While this is set, a row opened on one of
        /// them binds nothing and reports the port in use. False in
        /// production.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>The ports games send to out of the box.</summary>
        internal static bool IsSharedPort(int port)
            => port is GameTelemetryRuntime.DefaultPort or GameTelemetryRuntime.ProjectCars2Port
                or GameTelemetryRuntime.TruckSimulatorPort;

        public const int SpeedAxis = 0, RpmAxis = 1, ThrottleAxis = 2, BrakeAxis = 3, ClutchAxis = 4,
            HandbrakeAxis = 5, SteeringAxis = 6;
        public const int DrivingButton = 0, ReverseButton = 1, NeutralButton = 2, FirstGearButton = 3;
        public const int MaxGearButtons = 18;
        public const int ButtonCount = FirstGearButton + MaxGearButtons;

        private static readonly string[] s_axisNames =
            { "Speed", "RPM", "Throttle", "Brake", "Clutch", "Handbrake", "Steering" };
        // Steering and the variables take a non-Slider axis GUID: the row
        // writes them into the state's Axis array, and a Slider GUID makes
        // the picker offer "Slider N", which reads the Sliders array instead
        // (the rule SdlDeviceWrapper.GetDeviceObjects gives its extra axes).
        private static readonly Guid[] s_axisGuids =
            { ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis, ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis, ObjectGuid.ZAxis };

        private readonly object _stateLock = new();
        private readonly CustomInputState _state = new();
        private readonly Func<long> _now;
        private readonly TelemetryReader _reader;
        private readonly TelemetryVariable[] _variables;
        private readonly int[] _axisIndices;
        private readonly int _port;
        private readonly Action<int> _configureFirewall;
        private readonly Func<KunosSharedMemory> _kunosFactory;
        private KunosSharedMemory _kunos;
        private Socket _socket;
        private Socket _forward;
        private IPEndPoint _forwardEndpoint;
        private Thread _thread;
        private bool _yieldedPort;
        private bool _bindFailureLogged;
        private volatile bool _running;
        private volatile bool _attached;
        private volatile bool _disposed;
        private volatile int _statusVersion;
        private TelemetryListenState _listen = TelemetryListenState.SharedMemoryOnly;
        private TelemetrySource _source;
        private int _f1Year;
        private string _customName = string.Empty;
        private string _peer = string.Empty;
        private long _frames;
        private PooledInputStatePair _statePool;

        /// <summary>The <see cref="GameTelemetryRuntime.Version"/> this row
        /// was opened under. The sweep reopens the row when it moves.</summary>
        public int ConfigVersion { get; }

        /// <summary>Reads the live configuration. Version first, for the
        /// reason <see cref="HeadTrackerDevice.FromCurrentSettings"/> gives:
        /// the setters bump it last.</summary>
        public static GameTelemetryDevice FromCurrentSettings()
        {
            int version = GameTelemetryRuntime.Version;
            GameTelemetryRuntime.ReadFormats(out var compiled, out var variables);
            var detectable = Array.FindAll(compiled, f => f.Detectable);
            var reader = new TelemetryReader(GameTelemetryRuntime.Format, GameTelemetryRuntime.FindPinned(compiled),
                detectable, variables.Length);
            return new GameTelemetryDevice(GameTelemetryRuntime.Port, reader, variables, version, null);
        }

        internal GameTelemetryDevice(int port, TelemetryReader reader, TelemetryVariable[] variables,
            int configVersion, Func<long> now, Action<int> configureFirewall = null,
            Func<KunosSharedMemory> kunosFactory = null)
        {
            _port = port;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _variables = variables ?? Array.Empty<TelemetryVariable>();
            ConfigVersion = configVersion;
            _now = now ?? (() => Environment.TickCount64);
            _configureFirewall = configureFirewall ?? (p =>
            {
                System.Threading.Tasks.Task.Run(() =>
                    PadForge.Services.WebControllerServer.EnsureInboundFirewallRule(FirewallRuleName, "UDP", p));
            });
            _kunosFactory = kunosFactory ?? (() => new KunosSharedMemory());
            // The standard axes, then each variable at the axis it keeps
            // (TelemetryVariable.Slot), so the list can have gaps where a
            // deleted variable's axis waits.
            var indices = new System.Collections.Generic.List<int>();
            for (int i = 0; i < GameTelemetryRuntime.StandardAxisCount; i++) indices.Add(i);
            foreach (var variable in _variables)
                if (variable.Slot >= 0 && variable.Slot < GameTelemetryRuntime.MaxVariableAxes)
                    indices.Add(GameTelemetryRuntime.StandardAxisCount + variable.Slot);
            indices.Sort();
            _axisIndices = indices.ToArray();
            // The identity never changes, so saved mappings survive a change
            // of game, port or format.
            Name = "Game Telemetry";
            DevicePath = "telemetry://game";
            InstanceGuid = Md5Guid("pftelemetry:game");
            ProductGuid = Md5Guid("pftelemetry-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            Rest();
        }

        public uint SdlInstanceId { get; }
        public string Name { get; }
        /// <summary>The axis space, up to the highest axis in use.
        /// <see cref="SupportedAxisIndices"/> names the ones that exist.</summary>
        public int NumAxes => _axisIndices[^1] + 1;
        public int NumButtons => ButtonCount;
        public int RawButtonCount => ButtonCount;
        public int NumHats => 0;
        public int[] SupportedButtonIndices => null;
        public int[] SupportedAxisIndices => _axisIndices;
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
        public ushort VendorId => TelemetryVendorId;
        public ushort ProductId => TelemetryProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.GameTelemetry;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>The axis names for a row with the given variables, then
        /// the buttons'. A variable's axis is the standard ones plus its
        /// slot. Shared with the tests.</summary>
        internal static string AxisName(int index, TelemetryVariable[] variables)
        {
            if (index < s_axisNames.Length) return s_axisNames[index];
            foreach (var variable in variables)
                if (variable.Slot == index - s_axisNames.Length) return variable.Name;
            return string.Empty;
        }

        internal static string ButtonName(int index) => index switch
        {
            DrivingButton => "Driving",
            ReverseButton => "Reverse",
            NeutralButton => "Neutral",
            _ => "Gear " + (index - FirstGearButton + 1),
        };

        /// <summary>Every axis and button by name.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            int axisCount = _axisIndices.Length;
            var items = new DeviceObjectItem[axisCount + ButtonCount];
            for (int a = 0; a < axisCount; a++)
            {
                int i = _axisIndices[a];
                items[a] = new DeviceObjectItem
                {
                    Name = AxisName(i, _variables),
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    ObjectTypeGuid = i < s_axisGuids.Length ? s_axisGuids[i] : ObjectGuid.ZAxis,
                    InputIndex = i,
                    Offset = i * 4,
                };
            }
            for (int b = 0; b < ButtonCount; b++)
                items[axisCount + b] = new DeviceObjectItem
                {
                    Name = ButtonName(b),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = b,
                };
            return items;
        }

        // ─── Status, for Extras, Home and the Devices page ───

        public int Port => _port;
        internal int BoundPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;
        internal IPAddress BoundAddress => (_socket?.LocalEndPoint as IPEndPoint)?.Address;

        /// <summary>The local address the socket binds. Every address in the
        /// app. The tests pass loopback, so the test host opens nothing the
        /// network can reach.</summary>
        internal IPAddress BindAddress { get; init; } = IPAddress.Any;

        /// <summary>Bumps when anything the status line shows changes.</summary>
        public int StatusVersion => _statusVersion;

        public TelemetryStatus Status
        {
            get
            {
                lock (_stateLock)
                    return new TelemetryStatus(_listen, _port, _source, _f1Year, _customName, _peer,
                        GameTelemetryRuntime.ForwardPort);
            }
        }

        /// <summary>The format this row reads.</summary>
        public GameTelemetryFormat Format => _reader.Format;

        // ─── Lifecycle ───

        /// <summary>Opens the socket unless only the shared memory is read.
        /// The row opens even when the port is taken, so the status line can
        /// say so, and <see cref="RetryBind"/> tries the port again.</summary>
        public bool Open()
        {
            if (_disposed) return false;
            if (_reader.ReadsUdp) BindUdp();
            if (_reader.ReadsSharedMemory) _kunos = _kunosFactory();
            _attached = true;
            _statusVersion++;
            return true;
        }

        /// <summary>Tries the port again while another program holds it, so
        /// the row starts listening once that program lets go, with no toggle
        /// and no restart. The device sweep calls it on the poll thread every
        /// 2 s. True when the row listens now.</summary>
        internal bool RetryBind()
        {
            if (_disposed || !_reader.ReadsUdp || _listen != TelemetryListenState.PortInUse) return false;
            BindUdp();
            if (_listen != TelemetryListenState.Listening) return false;
            lock (_stateLock) _statusVersion++;
            return true;
        }

        /// <summary>How many times the row has tried its port, for the tests.</summary>
        internal int BindAttempts { get; private set; }

        /// <summary>Binds the socket and starts the receive thread, or leaves
        /// the row reporting the port in use. A failure is logged once per
        /// run of failures, since the sweep retries every 2 s.</summary>
        private void BindUdp()
        {
            BindAttempts++;
            if (OffLimitsForTest && IsSharedPort(_port))
            {
                _listen = TelemetryListenState.PortInUse;
                return;
            }
            try
            {
                // The test host binds loopback alone (NetworkListenerGate).
                NetworkListenerGate.Enter(BindAddress);
                // While a wheel wants RPM lights, the hub listens on
                // 20777, 5300 and 4444. It closes its socket on this
                // port and reads the game from this row until the row
                // closes (GameTelemetryRowSource).
                TelemetryHub.YieldPort(_port);
                _yieldedPort = true;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket = s;
                // Suppress ICMP port-unreachable surfacing as a SocketException
                // on the next receive (the DSU server's rule).
                try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
                // Exclusive, so a second listener on the same port shows as
                // a conflict rather than taking half the packets.
                s.ExclusiveAddressUse = true;
                s.Bind(new IPEndPoint(BindAddress, _port));
                // The copy goes out from loopback, so the forward socket
                // listens on nothing the network can reach.
                var forward = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try { forward.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { }
                forward.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _forward = forward;
                _running = true;
                _listen = TelemetryListenState.Listening;
                _bindFailureLogged = false;
                _thread = new Thread(ReceiveLoop)
                {
                    Name = "PadForge.GameTelemetryUdp",
                    IsBackground = true,
                };
                _thread.Start();
                SdlDiagLog.WriteLine($"Game telemetry: listening on UDP port {_port}");
                if (!IPAddress.IsLoopback(BindAddress)) _configureFirewall(_port);
            }
            catch (Exception ex)
            {
                _listen = TelemetryListenState.PortInUse;
                try { _socket?.Dispose(); } catch { }
                try { _forward?.Dispose(); } catch { }
                _socket = null;
                _forward = null;
                ReclaimHubPort();
                if (!_bindFailureLogged)
                {
                    _bindFailureLogged = true;
                    SdlDiagLog.WriteLine($"Game telemetry: UDP port {_port} bind failed: {ex.Message}");
                }
            }
        }

        private void ReceiveLoop()
        {
            // A UDP datagram is at most 65,507 bytes.
            var buf = new byte[65536];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            IPAddress lastPeer = null;
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

                Forward(buf, n);
                string peer = null;
                if (ep is IPEndPoint ipep && !ipep.Address.Equals(lastPeer))
                {
                    lastPeer = ipep.Address;
                    peer = lastPeer.ToString();
                }
                try { Receive(buf.AsSpan(0, n), peer); }
                catch { /* a malformed packet: keep listening */ }
            }
        }

        /// <summary>Copies a packet to the forward port. A forward port equal
        /// to the listening port would send each packet back to this socket
        /// forever, so it counts as none.</summary>
        private void Forward(byte[] buf, int n)
        {
            int port = GameTelemetryRuntime.ForwardPort;
            var socket = _forward;
            if (port <= 0 || port == _port || socket == null) return;
            var endpoint = _forwardEndpoint;
            if (endpoint == null || endpoint.Port != port)
                _forwardEndpoint = endpoint = new IPEndPoint(IPAddress.Loopback, port);
            try { socket.SendTo(buf, 0, n, SocketFlags.None, endpoint); }
            catch { /* nobody listening there: the next packet tries again */ }
        }

        /// <summary>One UDP packet, as the receive thread delivers it.</summary>
        private void Receive(ReadOnlySpan<byte> packet, string peer)
        {
            long now = _now();
            lock (_stateLock)
            {
                if (peer != null && peer != _peer)
                {
                    _peer = peer;
                    _statusVersion++;
                }
                if (_reader.Read(packet, now)) NoteFrame();
            }
        }

        /// <summary>Caller holds the lock. Bumps the status when the sender
        /// changed, and logs the first frames of a run.</summary>
        private void NoteFrame()
        {
            var f = _reader.Frame;
            if (f.Source != _source || f.F1Year != _f1Year || !string.Equals(f.CustomName, _customName, StringComparison.Ordinal))
            {
                _source = f.Source;
                _f1Year = f.F1Year;
                _customName = f.CustomName;
                _statusVersion++;
            }
            long n = Interlocked.Increment(ref _frames);
            if ((n <= 64 && (n & (n - 1)) == 0) || (n & 4095) == 0)
                SdlDiagLog.WriteLine(
                    $"Game telemetry: frame #{n} from {f.Source} speed={f.SpeedMs * 3.6f:F0}km/h rpm={f.Rpm:F0}/{f.MaxRpm:F0} gear={f.Gear} thr={f.Throttle:F2} brk={f.Brake:F2}");
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            long now = _now();
            var kunos = _kunos;
            if (kunos != null && kunos.TryRead(now))
            {
                lock (_stateLock)
                {
                    if (_reader.ReadKunos(kunos.Physics, kunos.Statics, kunos.Graphics, now)) NoteFrame();
                }
            }
            lock (_stateLock)
            {
                var frame = _reader.Frame;
                long hold = frame.Source == TelemetrySource.TruckSimulator ? TruckSimulatorHoldMs : SilenceMs;
                bool live = _reader.LastFrameMs != 0 && now - _reader.LastFrameMs <= hold;
                if (live) Fill(frame);
                else
                {
                    Rest();
                    if (_source != TelemetrySource.None)
                    {
                        _source = TelemetrySource.None;
                        _f1Year = 0;
                        _customName = string.Empty;
                        _statusVersion++;
                    }
                }
                GameTelemetryRuntime.Publish(frame, _variables, live, now);
                var dst = _statePool.Next();
                _state.CopyInto(dst);
                return dst;
            }
        }

        /// <summary>Caller holds the lock.</summary>
        private void Fill(TelemetryFrame f)
        {
            var axes = _state.Axis;
            float fullSpeed = Math.Max(1, GameTelemetryRuntime.FullSpeedKmh);
            axes[SpeedAxis] = Unipolar(f.SpeedMs * 3.6f / fullSpeed);
            axes[RpmAxis] = Unipolar(f.MaxRpm > 0f ? f.Rpm / f.MaxRpm : 0f);
            axes[ThrottleAxis] = Unipolar(f.Throttle);
            axes[BrakeAxis] = Unipolar(f.Brake);
            axes[ClutchAxis] = Unipolar(f.Clutch);
            axes[HandbrakeAxis] = Unipolar(f.Handbrake);
            axes[SteeringAxis] = Centered(f.Steering);
            for (int v = 0; v < _variables.Length; v++)
            {
                var variable = _variables[v];
                if (variable.Slot < 0 || variable.Slot >= GameTelemetryRuntime.MaxVariableAxes) continue;
                float value = v < f.Variables.Length ? f.Variables[v] : 0f;
                double span = variable.Max - variable.Min;
                double share = span != 0 ? (value - variable.Min) / span : value >= variable.Max ? 1 : 0;
                axes[GameTelemetryRuntime.StandardAxisCount + variable.Slot] = Unipolar((float)share);
            }

            var buttons = _state.Buttons;
            for (int b = 0; b < ButtonCount; b++) buttons[b] = false;
            buttons[DrivingButton] = !f.DrivingReported || f.Driving;
            if (f.Gear < 0) buttons[ReverseButton] = true;
            else if (f.Gear == 0) buttons[NeutralButton] = true;
            else if (f.Gear <= MaxGearButtons) buttons[FirstGearButton + f.Gear - 1] = true;
        }

        /// <summary>Caller holds the lock, or the constructor runs.</summary>
        private void Rest()
        {
            var axes = _state.Axis;
            for (int i = 0; i < NumAxes; i++) axes[i] = 0;
            axes[SteeringAxis] = HeadPose.AxisCenter;
            var buttons = _state.Buttons;
            for (int b = 0; b < ButtonCount; b++) buttons[b] = false;
        }

        /// <summary>0 to 1 onto 0 to 65535, 0 at rest, the trigger convention
        /// (CustomInputState).</summary>
        internal static int Unipolar(float share)
        {
            if (!float.IsFinite(share)) return 0;
            return (int)MathF.Round(Math.Clamp(share, 0f, 1f) * 65535f);
        }

        /// <summary>-1 to 1 onto the stick range, 32768 at rest, the rule
        /// <see cref="HeadPose"/> uses.</summary>
        internal static int Centered(float value)
        {
            if (!float.IsFinite(value)) return HeadPose.AxisCenter;
            float v = Math.Clamp(value, -1f, 1f);
            return v >= 0f
                ? (int)MathF.Round(HeadPose.AxisCenter + v * 32767f)
                : (int)MathF.Round(HeadPose.AxisCenter + v * 32768f);
        }

        // ─── Test seams ───

        /// <summary>A UDP packet as the socket would deliver it.</summary>
        internal void InjectUdp(byte[] packet, string peer)
        {
            Forward(packet, packet.Length);
            Receive(packet, peer);
        }

        internal void AttachForTest() => _attached = true;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            _running = false;
            // Close first: that is what unblocks ReceiveFrom. Dispose runs on
            // the poll thread (the sweep retires the row), so the join is a
            // short courtesy, the Head Tracker's rule.
            try { _socket?.Close(); } catch { }
            try { _thread?.Join(50); } catch { }
            _thread = null;
            _socket = null;
            try { _forward?.Close(); } catch { }
            _forward = null;
            ReclaimHubPort();
            try { _kunos?.Dispose(); } catch { }
            _kunos = null;
            GameTelemetryRuntime.ClearValues();
        }

        /// <summary>Hands the port back to the wheel lights' hub once the
        /// socket is closed.</summary>
        private void ReclaimHubPort()
        {
            if (!_yieldedPort) return;
            _yieldedPort = false;
            TelemetryHub.ReclaimPort(_port);
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
