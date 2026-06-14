using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Immutable description of one device a paired peer exposes, carried in the
    /// control-channel DEVICE_ADD message and used to build a <see cref="RemotePeerDevice"/>.
    /// </summary>
    public sealed class RemotePeerDeviceInfo
    {
        /// <summary>The peer's static-key fingerprint (SHA-256), hex. Salts this
        /// device's identity so two peers exposing the same controller type never
        /// alias each other through the ProductGuid reconnect fallback.</summary>
        public string PeerFingerprintHex { get; set; } = "";

        /// <summary>Stable id of the device on the peer's machine — stable across the
        /// remote program's restarts, not a session-local SDL instance id.</summary>
        public string PeerLocalDeviceId { get; set; } = "";

        public string Name { get; set; } = "Remote Device";
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public string SerialNumber { get; set; } = "";

        public int NumAxes { get; set; }
        public int NumButtons { get; set; }
        public int NumHats { get; set; }

        public bool HasRumble { get; set; }
        public bool HasRumbleTriggers { get; set; }
        public bool HasGyro { get; set; }
        public bool HasAccel { get; set; }
        public bool HasTouchpad { get; set; }
        public int NumTouchpads { get; set; }
        public int[] TouchpadFingerCounts { get; set; }

        /// <summary>The peer device's input-device-type constant (see InputDeviceType).</summary>
        public int InputDeviceType { get; set; } = PadForge.Engine.InputDeviceType.Gamepad;

        /// <summary>The mappable inputs forwarded from the peer device's own
        /// GetDeviceObjects(). When null/empty, a gamepad shape is synthesized.</summary>
        public DeviceObjectItem[] DeviceObjects { get; set; }
    }

    /// <summary>
    /// Receive-side representation of a device attached to a paired peer
    /// (issue #138). Implements <see cref="ISdlInputDevice"/> so it flows through
    /// the standard 6-step pipeline exactly like a local source — the transport's
    /// receive thread decodes each datagram into this device's state, and Step 2
    /// reads it through <see cref="GetCurrentState"/>.
    ///
    /// Two behaviors matter for correctness:
    ///  - Identity is salted by the peer fingerprint, so the non-peer-namespaced
    ///    ProductGuid reconnect fallback in FindOrCreateUserDevice can't migrate
    ///    one peer's slot/profile onto another peer sharing a controller type.
    ///  - <see cref="GetCurrentState"/> returns null once frames stop arriving for
    ///    longer than the stale window, the same contract SdlDeviceWrapper uses on
    ///    disconnect, so silent loss routes the device offline and releases held
    ///    inputs instead of latching the last frame forever.
    /// </summary>
    public sealed class RemotePeerDevice : ISdlInputDevice
    {
        private readonly object _stateLock = new object();
        private CustomInputState _currentState;
        private CustomInputState _back;

        private readonly Func<long> _nowTicks;
        private readonly long _staleTicks;
        private long _lastFrameTicks;
        private bool _everReceived;
        private volatile bool _connected;
        private volatile bool _disposed;

        private readonly int[] _supportedButtonIndices;

        public RemotePeerDeviceInfo Info { get; }

        /// <summary>This device's slot id on the link (its index in the owner's exposed
        /// list, symmetric across both peers). The reverse output channel stamps this
        /// slot so the owner maps an inbound effect back to the right physical device.</summary>
        public byte LinkSlot { get; set; }

        /// <summary>Raised when the engine asks this source to rumble; the transport
        /// forwards it back to the peer to drive the physical device.</summary>
        public event Action<ushort, ushort> RumbleRequested;

        /// <param name="staleAfterMs">No-frame window after which GetCurrentState
        /// returns null (offline). The heartbeat runs at 30-60 Hz, so a multi-hundred-ms
        /// gap already means the link is gone; the transport's own timeout is the
        /// authoritative unregister, this is the in-pipeline safety net.</param>
        /// <param name="nowTicksProvider">Monotonic tick source (test seam). Defaults to Stopwatch.</param>
        public RemotePeerDevice(RemotePeerDeviceInfo info, int staleAfterMs = 3000, Func<long> nowTicksProvider = null)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            _nowTicks = nowTicksProvider ?? Stopwatch.GetTimestamp;
            _staleTicks = Stopwatch.Frequency * Math.Max(1, staleAfterMs) / 1000;

            Name = info.Name;
            VendorId = info.VendorId;
            ProductId = info.ProductId;
            SerialNumber = info.SerialNumber ?? "";
            NumAxes = info.NumAxes;
            NumButtons = info.NumButtons;
            NumHats = info.NumHats;

            DevicePath = $"peer://{Short(info.PeerFingerprintHex)}/{info.PeerLocalDeviceId}";
            InstanceGuid = Md5Guid($"pflink-dev:{info.PeerFingerprintHex}:{info.PeerLocalDeviceId}");
            ProductGuid = Md5Guid($"pflink-prod:{info.PeerFingerprintHex}:{info.VendorId:X4}:{info.ProductId:X4}:{info.InputDeviceType}");
            SdlInstanceId = unchecked((uint)DevicePath.GetHashCode());

            _supportedButtonIndices = new int[Math.Max(0, NumButtons)];
            for (int i = 0; i < _supportedButtonIndices.Length; i++) _supportedButtonIndices[i] = i;

            // Start centered (codec neutral) and live, so registration doesn't blip
            // offline before the first frame; the stale window then governs liveness.
            _currentState = CustomInputStateCodec.CreateNeutral();
            _back = CustomInputStateCodec.CreateNeutral();
            _lastFrameTicks = _nowTicks();
            _connected = true;
        }

        // ── ISdlInputDevice identity / capabilities ─────────────────────────

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes { get; }
        public int NumButtons { get; }
        public int RawButtonCount => NumButtons;
        public int NumHats { get; }
        public int[] SupportedButtonIndices => _supportedButtonIndices;
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => Info.HasRumble;
        public bool HasRumbleTriggers => Info.HasRumbleTriggers;
        public bool HasHaptic => false; // DualSense-effect return path is a later milestone.
        public bool HasGyro => Info.HasGyro;
        public bool HasAccel => Info.HasAccel;
        public bool HasTouchpad => Info.HasTouchpad;
        public int NumTouchpads => Info.HasTouchpad ? Math.Max(1, Info.NumTouchpads) : 0;
        public int[] TouchpadFingerCounts => Info.TouchpadFingerCounts ?? Array.Empty<int>();
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _connected && !_disposed;
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber { get; }
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => Info.InputDeviceType;

        // ── State plumbing ──────────────────────────────────────────────────

        /// <summary>
        /// Decode and apply one input datagram payload (the bytes the codec
        /// produced, after the transport opened the AEAD). Latest-wins: the back
        /// buffer is decoded then swapped in under the lock. A malformed frame is
        /// dropped and the last good state is held — the next heartbeat supersedes it.
        /// Returns true when a frame was applied.
        /// </summary>
        private ulong _lastAppliedTs;

        public bool ApplyFramePayload(ReadOnlySpan<byte> payload) => ApplyFrameInternal(payload, 0, checkOrder: false);

        /// <summary>Apply a frame, dropping it if its send timestamp is older than the
        /// last applied frame's — the anti-replay window accepts in-window reorders, so
        /// absolute newest-wins is enforced here.</summary>
        public bool ApplyFramePayload(ReadOnlySpan<byte> payload, ulong timestampUs) => ApplyFrameInternal(payload, timestampUs, checkOrder: true);

        private bool ApplyFrameInternal(ReadOnlySpan<byte> payload, ulong timestampUs, bool checkOrder)
        {
            if (_disposed) return false;
            lock (_stateLock)
            {
                if (checkOrder && _everReceived && timestampUs != 0 && timestampUs <= _lastAppliedTs)
                    return false; // reordered older frame — newest already applied
                if (!CustomInputStateCodec.DecodeInto(payload, _back)) return false;
                (_currentState, _back) = (_back, _currentState);
                _lastFrameTicks = _nowTicks();
                if (timestampUs != 0) _lastAppliedTs = timestampUs;
                _everReceived = true;
                return true;
            }
        }

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed) return null;
            lock (_stateLock)
            {
                // Once frames have flowed, a silent gap past the stale window reads
                // as offline (null) so the pipeline neutralizes held inputs. Before
                // the first frame we report the centered baseline so a freshly
                // registered peer device doesn't flap offline while it waits.
                if (_everReceived && (_nowTicks() - _lastFrameTicks) > _staleTicks)
                    return null;
                return _currentState.Clone();
            }
        }

        public void SetConnected(bool connected) => _connected = connected;

        // ── Mapping surface ─────────────────────────────────────────────────

        public DeviceObjectItem[] GetDeviceObjects()
        {
            if (Info.DeviceObjects != null && Info.DeviceObjects.Length > 0)
                return Info.DeviceObjects;
            return SynthesizeGamepadObjects();
        }

        private DeviceObjectItem[] SynthesizeGamepadObjects()
        {
            int axes = Math.Min(Math.Max(NumAxes, 0), StandardAxisGuids.Length);
            int buttons = Math.Max(NumButtons, 0);
            int povs = Math.Max(NumHats, 0);
            var items = new DeviceObjectItem[axes + buttons + povs];
            int idx = 0, offset = 0;
            for (int i = 0; i < axes; i++)
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = StandardAxisGuids[i],
                    Name = StandardAxisNames[i],
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = (offset++) * 4
                };
            for (int i = 0; i < buttons; i++)
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = $"Button {i + 1}",
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (offset++) * 4
                };
            for (int i = 0; i < povs; i++)
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.PovController,
                    Name = povs > 1 ? $"D-Pad {i + 1}" : "D-Pad",
                    ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                    Offset = (offset++) * 4
                };
            return items;
        }

        // ── Feedback return path ────────────────────────────────────────────

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue)
        {
            RumbleRequested?.Invoke(low, high);
            return true;
        }

        public bool StopRumble()
        {
            RumbleRequested?.Invoke(0, 0);
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            _connected = false;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static readonly Guid[] StandardAxisGuids =
        {
            ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis,
            ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis
        };

        private static readonly string[] StandardAxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };

        private static string Short(string fingerprintHex)
            => string.IsNullOrEmpty(fingerprintHex) ? "anon"
               : fingerprintHex.Substring(0, Math.Min(8, fingerprintHex.Length));

        private static Guid Md5Guid(string identifier)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(identifier));
            return new Guid(hash);
        }
    }
}
