using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// The UDP server DSX game mods send to (#529), on 127.0.0.1 and port
    /// 6969 unless told another.
    ///
    /// <para>Built the way <see cref="DsuMotionServer"/> is: a socket bound
    /// to loopback only, <c>SIO_UDP_CONNRESET</c> turned off so a mod that
    /// closes its socket cannot end the receive loop, one receive thread,
    /// and a test fence on the default port. Packets go to
    /// <see cref="GameModProtocol"/> and <see cref="GameModEffects"/>, and
    /// every packet gets DSX's reply at once, because the mods that wait for
    /// one block on Receive with no timeout.</para>
    ///
    /// <para>Every sender is the process that owns the UDP port a packet
    /// came from (<see cref="GameModSender"/>), so every port one program
    /// sends from leads to one sender. The receive loop's half-second
    /// timeout runs the watch that ends a sender's values when its process
    /// exits.</para>
    /// </summary>
    public sealed class GameModServer : IDisposable
    {
        internal const int DefaultPort = GameModProtocol.DefaultPort;

        // IOControl to suppress ICMP port-unreachable resets on Windows.
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        /// <summary>How often the receive loop wakes with nothing arriving,
        /// to look for senders that are gone.</summary>
        private const int WatchMs = 500;

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests GameModPortOffLimits), never by the app. Mods on
        /// the same PC send to the default port, and a running PadForge or
        /// DSX may hold it. While this is set, a start on that port binds
        /// nothing and answers false, as when the port is taken. False in
        /// production.</summary>
        internal static bool OffLimitsForTest;

        public enum ServerState
        {
            Stopped,
            Listening,
            PortInUse,
            Failed,
        }

        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _running;
        private int _port;
        private bool _disposed;
        private bool _standInWanted;

        /// <summary>Every sender, once each. Changed on the receive thread
        /// under <see cref="_sendersLock"/>, which the interface's reads
        /// take.</summary>
        private readonly List<GameModSender> _senders = new();
        private readonly object _sendersLock = new();

        /// <summary>The sender each UDP port leads to, and when that port
        /// last sent. A mod that opens a new socket every tick sends from a
        /// new port each time (the GTA V mods: DualSense4GTAV add.cs:28-31,
        /// 47-49), and each of those ports leads to the one sender of its
        /// process. A port quiet past <see cref="GameModSender.LapseMs"/> is
        /// forgotten, which ends nothing. Changed on the receive thread under
        /// <see cref="_sendersLock"/>.</summary>
        private readonly Dictionary<int, PortSeen> _ports = new();

        private sealed class PortSeen
        {
            internal GameModSender Sender;
            internal long LastMs;
        }
        private long _lastWatchMs;

        /// <summary>The last sender that named a controller, and the index it
        /// named, for the status line.</summary>
        private GameModSender _lastSender;
        private int _lastIndex;

        public ServerState State { get; private set; } = ServerState.Stopped;
        public int Port => _port;

        /// <summary>Raised when the state, a sender or the controller it
        /// sends to changes, on the thread that saw it.</summary>
        public event EventHandler StatusChanged;

        /// <summary>Starts listening. False when the port is taken or the
        /// bind fails, with <see cref="State"/> saying which.</summary>
        public bool Start(int port, bool standIn)
        {
            if (_running) return true;
            _port = port;
            _standInWanted = standIn;
            if (OffLimitsForTest && port == DefaultPort)
            {
                SetState(ServerState.PortInUse);
                return false;
            }

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try { _socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
                catch { /* Non-Windows or older OS */ }
                _socket.ReceiveTimeout = WatchMs;
                _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                _running = true;

                _receiveThread = new Thread(ReceiveLoop)
                {
                    Name = "PadForge.GameModServer",
                    IsBackground = true,
                };
                _receiveThread.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _socket?.Dispose();
                _socket = null;
                SetState(ServerState.PortInUse);
                return false;
            }
            catch (Exception)
            {
                _socket?.Dispose();
                _socket = null;
                SetState(ServerState.Failed);
                return false;
            }

            SetState(ServerState.Listening);
            // Mods find the port in files, and file work stays off the caller.
            ThreadPool.QueueUserWorkItem(_ => GameModPortFiles.Write(port));
            if (_standInWanted) DsxStandIn.Start();
            return true;
        }

        /// <summary>Starts or stops the DSX.exe stand-in while listening.</summary>
        public void SetStandIn(bool on)
        {
            _standInWanted = on;
            if (!_running) return;
            if (on) DsxStandIn.Start();
            else DsxStandIn.Stop();
        }

        /// <summary>Stops listening. The stand-in goes first, so no mod sees
        /// a DSX process without a server to answer it, and every value the
        /// mods hold ends.</summary>
        public void Stop()
        {
            if (!_running) return;
            DsxStandIn.Stop();
            _running = false;
            try { _socket?.Close(); }
            catch { /* best effort */ }
            _receiveThread?.Join(2000);
            _receiveThread = null;
            _socket = null;

            GameModEffects.ClearAll();
            lock (_sendersLock)
            {
                foreach (var sender in _senders) sender.Dispose();
                _senders.Clear();
                _ports.Clear();
                _lastSender = null;
            }
            SetState(ServerState.Stopped);
        }

        private void SetState(ServerState state)
        {
            State = state;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // ─────────────────────────────────────────────
        //  Receive thread
        // ─────────────────────────────────────────────

        private void ReceiveLoop()
        {
            // A UDP datagram is at most 65,507 bytes.
            byte[] buffer = new byte[65536];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    int received = _socket.ReceiveFrom(buffer, ref remote);
                    if (remote is IPEndPoint peer && IPAddress.IsLoopback(peer.Address))
                        Process(buffer.AsSpan(0, received), peer);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    // Nothing arrived: the watch below still runs.
                }
                catch (SocketException) when (!_running)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    // A malformed packet or a transient error: keep listening.
                }

                try { WatchSenders(Environment.TickCount64); }
                catch { /* the watch runs again next time */ }
            }
        }

        /// <summary>Answers a packet and then applies it. The reply goes
        /// first, so a mod waiting on it waits no longer than the reply
        /// takes to build.</summary>
        internal void Process(ReadOnlySpan<byte> datagram, IPEndPoint peer)
        {
            long now = Environment.TickCount64;
            var instructions = GameModProtocol.Parse(datagram);
            int firstIndex = 0;
            if (instructions != null)
            {
                foreach (var instruction in instructions)
                {
                    if (instruction.Type == GameModProtocol.GetDsxStatus || instruction.Parameters.Length == 0) continue;
                    firstIndex = instruction.Parameters[0];
                    break;
                }
            }

            try
            {
                byte[] reply = GameModProtocol.BuildReply(GameModStatusDevices.Read(), firstIndex, DateTime.Now);
                _socket?.SendTo(reply, peer);
            }
            catch { /* the mod went away: the watch ends its values */ }

            if (instructions == null || instructions.Count == 0) return;
            var sender = SenderFor(peer.Port, now);
            bool named = false;
            foreach (var instruction in instructions)
            {
                GameModEffects.Apply(instruction, sender);
                if (!named && instruction.Type != GameModProtocol.GetDsxStatus && instruction.Parameters.Length > 0)
                    named = true;
            }
            if (named && (_lastSender != sender || _lastIndex != firstIndex))
            {
                lock (_sendersLock)
                {
                    _lastSender = sender;
                    _lastIndex = firstIndex;
                }
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>The sender for a port. A sender whose process is gone is
        /// let go first, so a new program that reuses its port is
        /// identified anew. A port not heard from before leads to the live
        /// sender of the process that owns it, when there is one, so a
        /// program sending from many ports holds one process handle.</summary>
        private GameModSender SenderFor(int port, long now)
        {
            if (_ports.TryGetValue(port, out var seen))
            {
                var known = seen.Sender;
                if (!known.IsGone(now) || known.ProcessId == 0)
                {
                    seen.LastMs = now;
                    known.Touch(now);
                    return known;
                }
                Release(known);
            }

            int pid = GameModSender.FindUdpOwner(port);
            GameModSender sender = null;
            if (pid > 0)
            {
                foreach (var live in _senders)
                {
                    if (live.ProcessId == pid && !live.IsGone(now))
                    {
                        sender = live;
                        break;
                    }
                }
            }
            lock (_sendersLock)
            {
                if (sender == null)
                {
                    sender = GameModSender.Identify(port, pid, now);
                    _senders.Add(sender);
                }
                _ports[port] = new PortSeen { Sender = sender, LastMs = now };
            }
            sender.Touch(now);
            return sender;
        }

        /// <summary>Ends the values of every sender whose process exited, or
        /// that went quiet past the lapse when it has none to watch, and
        /// forgets the ports quiet past the lapse.</summary>
        private void WatchSenders(long now)
        {
            if (now - _lastWatchMs < WatchMs) return;
            _lastWatchMs = now;
            List<int> quiet = null;
            foreach (var pair in _ports)
                if (now - pair.Value.LastMs >= GameModSender.LapseMs) (quiet ??= new List<int>()).Add(pair.Key);
            if (quiet != null)
            {
                lock (_sendersLock)
                    foreach (int port in quiet) _ports.Remove(port);
            }

            List<GameModSender> gone = null;
            foreach (var sender in _senders)
                if (sender.IsGone(now)) (gone ??= new List<GameModSender>()).Add(sender);
            if (gone == null) return;
            foreach (var sender in gone) Release(sender);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Lets a sender go once: every port that led to it is
        /// forgotten, and every value it set ends.</summary>
        private void Release(GameModSender sender)
        {
            lock (_sendersLock)
            {
                if (!_senders.Remove(sender)) return;
                List<int> ports = null;
                foreach (var pair in _ports)
                    if (pair.Value.Sender == sender) (ports ??= new List<int>()).Add(pair.Key);
                if (ports != null)
                    foreach (int port in ports) _ports.Remove(port);
                if (_lastSender == sender) _lastSender = null;
            }
            GameModEffects.ReleaseSender(sender);
            sender.Dispose();
        }

        // ─────────────────────────────────────────────
        //  Status line
        // ─────────────────────────────────────────────

        /// <summary>The status line on Extras and Home: why the server is not listening, or
        /// that it listens, and who is sending to which virtual controller.</summary>
        public string FormatStatus()
        {
            string name;
            int index;
            lock (_sendersLock)
            {
                name = _lastSender != null ? (_lastSender.Name ?? Strings.Instance.GameMods_UnknownSender) : null;
                index = _lastIndex;
            }
            int number = 0, pastIndex = -1;
            if (name != null)
            {
                var order = GameModEffects.OrderedSlots();
                int i = index < 0 ? 0 : index;
                if (i < order.Length)
                {
                    number = SettingsManager.SlotOrders.GetGlobalSlotNumber(order[i]);
                    if (number <= 0) number = order[i] + 1;
                }
                else if (order.Length > 0)
                {
                    pastIndex = i;
                }
            }
            return FormatStatus(State, _port, name, number, pastIndex);
        }

        /// <summary>The line for a state, a port, the sender's name (null when
        /// none is sending), Home's number of the virtual controller
        /// its index names (0 when it names none), and the index it sent
        /// when that is past every virtual controller with a PlayStation
        /// controller while some have one (-1 otherwise). With no
        /// virtual controller named and no index past them, none has a
        /// PlayStation controller.</summary>
        internal static string FormatStatus(ServerState state, int port, string sender, int virtualController,
            int pastIndex = -1)
        {
            var s = Strings.Instance;
            switch (state)
            {
                case ServerState.Listening:
                    if (sender == null) return string.Format(s.Server_ListeningOn_Format, port);
                    if (virtualController > 0)
                        return string.Format(s.GameMods_Sending_Format, port, sender, virtualController);
                    return pastIndex >= 0
                        ? string.Format(s.GameMods_SendingPastControllers_Format, port, sender, pastIndex)
                        : string.Format(s.GameMods_SendingNoController_Format, port, sender);
                case ServerState.PortInUse:
                    return string.Format(s.GameMods_PortInUse_Format, port);
                case ServerState.Failed:
                    return s.Server_FailedToStart;
                default:
                    return s.Common_Stopped;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }

        /// <summary>Test seams: the ports heard from, and the senders they
        /// lead to, each counted once.</summary>
        internal int PortCountForTest
        {
            get { lock (_sendersLock) return _ports.Count; }
        }

        internal int SenderCountForTest
        {
            get { lock (_sendersLock) return _senders.Count; }
        }
    }

    /// <summary>
    /// The reply's device list (#529): one entry per controller index a mod
    /// can address, each describing the first online PlayStation pad on that
    /// virtual controller. Built on the receive thread, at most every 250 ms,
    /// since a mod that resends its state every frame asks for it every
    /// frame.
    /// </summary>
    internal static class GameModStatusDevices
    {
        private const long CacheMs = 250;
        private static IReadOnlyList<GameModProtocol.ReplyDevice> s_cached = Array.Empty<GameModProtocol.ReplyDevice>();
        private static long s_builtMs = long.MinValue / 2;

        internal static IReadOnlyList<GameModProtocol.ReplyDevice> Read()
        {
            long now = Environment.TickCount64;
            if (now - s_builtMs < CacheMs) return s_cached;
            s_cached = Build();
            s_builtMs = now;
            return s_cached;
        }

        private static IReadOnlyList<GameModProtocol.ReplyDevice> Build()
        {
            var order = GameModEffects.OrderedSlots();
            if (order.Length == 0) return Array.Empty<GameModProtocol.ReplyDevice>();
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return Array.Empty<GameModProtocol.ReplyDevice>();

            // Each slot's assigned rows in settings order, then the first
            // online Sony pad among them.
            var rows = new Dictionary<int, List<Guid>>();
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null || us.MapTo < 0 || Array.IndexOf(order, us.MapTo) < 0) continue;
                    if (!rows.TryGetValue(us.MapTo, out var list)) rows[us.MapTo] = list = new List<Guid>(2);
                    list.Add(us.InstanceGuid);
                }
            }

            var result = new List<GameModProtocol.ReplyDevice>(order.Length);
            for (int index = 0; index < order.Length; index++)
            {
                int slot = order[index];
                if (!rows.TryGetValue(slot, out var guids)) continue;
                Engine.Data.UserDevice pad = null;
                lock (devices.SyncRoot)
                {
                    foreach (var guid in guids)
                    {
                        foreach (var ud in devices.Items)
                        {
                            if (ud == null || ud.InstanceGuid != guid || !ud.IsOnline) continue;
                            if (!GameModEffects.IsSonyPad(ud.VendorId, ud.ProdId)) continue;
                            pad = ud;
                            break;
                        }
                        if (pad != null) break;
                    }
                }
                if (pad == null) continue;
                result.Add(Describe(index, slot, pad));
            }
            return result;
        }

        /// <summary>One entry, with DSX's device and connection numbers.</summary>
        internal static GameModProtocol.ReplyDevice Describe(int index, int slot, Engine.Data.UserDevice pad)
        {
            bool dualSense = GameModEffects.IsDualSense(pad.VendorId, pad.ProdId);
            int deviceType = pad.ProdId switch
            {
                0x0CE6 => 0,  // DUALSENSE
                0x0DF2 => 1,  // DUALSENSE_EDGE
                0x05C4 => 2,  // DUALSHOCK_V1
                0x09CC => 3,  // DUALSHOCK_V2
                _ => 4,       // DUALSHOCK_DONGLE, Sony's wireless adapter (0x0BA0)
            };
            int connection = pad.ProdId == 0x0BA0 ? 2
                : PlayStationEffectWriter.IsBluetoothPath(pad.DevicePath) ? 1 : 0;
            int battery = UserEffectsDispatcher.SlotBatteryPercentProvider?.Invoke(slot, pad.InstanceGuid) ?? 100;
            return new GameModProtocol.ReplyDevice
            {
                Index = index,
                MacAddress = pad.SerialNumber ?? "",
                DeviceType = deviceType,
                ConnectionType = connection,
                BatteryLevel = Math.Clamp(battery, 0, 100),
                IsSupportAT = dualSense,
                IsSupportLightBar = true,
                IsSupportPlayerLED = dualSense,
                IsSupportMicLED = dualSense,
            };
        }

        /// <summary>Test seam: forgets the cached list.</summary>
        internal static void ResetForTest()
        {
            s_cached = Array.Empty<GameModProtocol.ReplyDevice>();
            s_builtMs = long.MinValue / 2;
        }
    }
}
