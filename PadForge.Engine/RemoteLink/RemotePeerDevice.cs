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

        /// <summary>Raw HID button count, which for a gamepad exceeds the 22
        /// standardized SDL slots when the device has native buttons past the
        /// gamepad layout (fight sticks, extra paddles, force-raw DS3). The
        /// per-frame state codec already ships Buttons[0..N] by raw index;
        /// carrying this lets the consumer offer those extras in its mapping
        /// picker instead of capping at NumButtons. 0 means "same as
        /// NumButtons" (old peers, or a device with no extras).</summary>
        public int RawButtonCount { get; set; }

        /// <summary>Raw HID axis count, the axis twin of
        /// <see cref="RawButtonCount"/>. SDL puts device-specific analog data
        /// beyond the six standard gamepad axes on raw joystick axes 6+, and
        /// SdlDeviceWrapper already tracks this locally for exactly that
        /// reason. Without it crossing the wire, a remote device's extra axes
        /// were undiscoverable and the consumer's picker capped at NumAxes.
        /// 0 means "same as NumAxes" (old peers, or a device with no
        /// extras).</summary>
        public int RawAxisCount { get; set; }

        /// <summary>The raw axes past the standard six should surface as
        /// generic "Axis N" sources. Carried separately because it is NOT
        /// derivable from the counts: it deliberately excludes devices whose
        /// extra axes are already dedicated sensor sources (Wii IR pointer,
        /// Joy-Con NIR / mouse), where a raw coordinate would be noise in the
        /// picker. Rides caps2 bit 2 in the v3 tail.</summary>
        public bool HasExtraGenericAxes { get; set; }

        public bool HasRumble { get; set; }
        public bool HasRumbleTriggers { get; set; }
        /// <summary>The device exposes DirectInput-style haptic FFB (wheels, FFB sticks).
        /// Advertised so the consumer's ForceFeedbackState instantiates and the FFB
        /// pipeline runs for the remote device (issue #138 reverse output relay).</summary>
        public bool HasHaptic { get; set; }
        public bool HasGyro { get; set; }
        public bool HasAccel { get; set; }
        public bool HasAccelAux { get; set; }

        /// <summary>Aux (left-side) gyro: left Joy-Con of a pair (#252).</summary>
        public bool HasGyroAux { get; set; }
        public bool HasTouchpad { get; set; }
        public int NumTouchpads { get; set; }
        public int[] TouchpadFingerCounts { get; set; }

        /// <summary>Whether the owner's device carries an NFC reader (#241).
        /// Rides the v3 capability tail, because the v1 caps byte was
        /// exhausted at bit 128. False from a peer that predates the tail,
        /// which correctly hides the sources rather than offering ones the
        /// old owner cannot serve.</summary>
        public bool HasNfcReader { get; set; }

        /// <summary>The peer device's input-device-type constant (see InputDeviceType).</summary>
        public int InputDeviceType { get; set; } = PadForge.Engine.InputDeviceType.Gamepad;

        /// <summary>Whether the device is currently active on the owner (#138 live device
        /// sync). Carried in the DeviceList message so the consumer shows active/inactive.</summary>
        public bool Online { get; set; } = true;

        /// <summary>The owner's STABLE link slot for this device (#138 live device sync) —
        /// assigned once and kept while the device is shared, so a device hot-plugged after
        /// connect routes input/output by a slot that never shifts. Carried in the device
        /// list (handshake + periodic sync) so both ends agree on the routing slot.</summary>
        public byte Slot { get; set; }

        /// <summary>The mappable inputs forwarded from the peer device's own
        /// GetDeviceObjects(). When null/empty, a gamepad shape is synthesized.</summary>
        public DeviceObjectItem[] DeviceObjects { get; set; }

        /// <summary>The owner's REAL set of usable button indices, sparse.
        /// A gamepad reports 22 standardized slots but physically has only
        /// some of them: the owner gates slots 11-21 on SDL_GamepadHasButton,
        /// so a 2026 Steam Controller supports 18 of the 22. The consumer used
        /// to synthesize a dense 0..RawButtonCount-1 range and therefore
        /// listed buttons the device does not have (owner report: 22 shown,
        /// 18 real). Null means the peer predates the v6 tail, in which case
        /// the dense fallback is all we can do.</summary>
        public int[] SupportedButtonIndices { get; set; }

        /// <summary>The owner's real set of usable axis positions, sparse.
        /// Null means dense. Same reason as the button set: NumAxes is the
        /// standardized 6-slot gamepad space, not a count of what the pad
        /// physically has.</summary>
        public int[] SupportedAxisIndices { get; set; }

        /// <summary>The owner's SDL joystick GUID, so a peer's controller can
        /// be reported upstream with a complete dossier.</summary>
        public string SdlGuid { get; set; }
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
        private readonly int[] _supportedAxisIndices;

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
            NumAxes = info.NumAxes;
            NumButtons = info.NumButtons;
            NumHats = info.NumHats;
            // Never below NumButtons: a 0 (old peer) or a device with no
            // extras falls back to the standardized count.
            _rawButtonCount = Math.Max(info.NumButtons, info.RawButtonCount);

            DevicePath = $"peer://{Short(info.PeerFingerprintHex)}/{info.PeerLocalDeviceId}";
            InstanceGuid = Md5Guid($"pflink-dev:{info.PeerFingerprintHex}:{info.PeerLocalDeviceId}");
            ProductGuid = Md5Guid($"pflink-prod:{info.PeerFingerprintHex}:{info.VendorId:X4}:{info.ProductId:X4}:{info.InputDeviceType}");
            SdlInstanceId = unchecked((uint)DevicePath.GetHashCode());

            // The full raw button set is mappable, so support every index the
            // extras reach, not just the standardized 22.
            // Prefer the OWNER's real sparse set. Synthesizing a dense range
            // here listed buttons the physical device does not have.
            if (info.SupportedButtonIndices is { Length: > 0 })
            {
                _supportedButtonIndices = (int[])info.SupportedButtonIndices.Clone();
            }
            else
            {
                _supportedButtonIndices = new int[Math.Max(0, _rawButtonCount)];
                for (int i = 0; i < _supportedButtonIndices.Length; i++) _supportedButtonIndices[i] = i;
            }
            if (info.SupportedAxisIndices is { Length: > 0 })
            {
                _supportedAxisIndices = (int[])info.SupportedAxisIndices.Clone();
            }
            else
            {
                int denseAxes = Math.Max(0, Math.Max(info.NumAxes, info.RawAxisCount));
                _supportedAxisIndices = new int[denseAxes];
                for (int i = 0; i < denseAxes; i++) _supportedAxisIndices[i] = i;
            }

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
        private readonly int _rawButtonCount;
        public int RawButtonCount => _rawButtonCount;
        public int NumHats { get; }
        public int[] SupportedButtonIndices => _supportedButtonIndices;
        /// <summary>The owner's real raw axis count. The interface defaults
        /// (RawAxisCount => NumAxes, HasExtraGenericAxes => false) silently
        /// capped a shared 16-axis device (DS3 in SDF mode, flight sticks) at
        /// 6 pickable axes on the consumer, even though both values were
        /// already CARRIED on the wire (v3 caps bit 2 + v4 tail) and the
        /// per-frame codec ships all 24 axes. UserDevice.LoadFromDevice
        /// computes the pickable inventory from exactly these two members.</summary>
        public int RawAxisCount => Math.Max(Info.NumAxes, Info.RawAxisCount);
        public bool HasExtraGenericAxes => Info.HasExtraGenericAxes;
        /// <summary>The owner's real axis set (v7 tail): the axis twin of
        /// <see cref="SupportedButtonIndices"/>. A stickless gamepad must not
        /// offer Left/Right Stick axes the default profile would auto-bind.</summary>
        public int[] SupportedAxisIndices => _supportedAxisIndices;
        /// <summary>The owner's SDL GUID (v7 tail), so the Devices page shows
        /// it and a mapping report filed for a peer's pad is complete.</summary>
        public string SdlGuid => Info.SdlGuid ?? string.Empty;
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => Info.HasRumble;
        public bool HasRumbleTriggers => Info.HasRumbleTriggers;
        // Advertised so the consumer's FFB pipeline (ForceFeedbackState) runs for a
        // remote wheel / FFB stick and its directional/condition output gets captured
        // and relayed to the owner. The owner re-creates the SDL haptic effect on the
        // real handle; the consumer never opens an SDL haptic for a peer device.
        public bool HasHaptic => Info.HasHaptic;
        public bool HasGyro => Info.HasGyro;
        public bool HasAccel => Info.HasAccel;
        public bool HasAccelAux => Info.HasAccelAux;
        public bool HasGyroAux => Info.HasGyroAux;
        public bool HasNfcReader => Info.HasNfcReader;
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
        /// <summary>Live read from Info: the device-list reconcile refreshes
        /// Info.SerialNumber in place for already-registered devices, and a
        /// constructor snapshot would never see it (audit F7).</summary>
        public string SerialNumber => Info.SerialNumber ?? "";

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
                // Copy into the caller-facing pooled pair instead of a
                // fresh Clone per 1 kHz poll. Under _stateLock because the
                // receive thread swaps _currentState/_back mid-copy
                // otherwise. The null-on-stale contract above is unchanged.
                var dst = _statePool.Next();
                _currentState.CopyInto(dst);
                return dst;
            }
        }

        // Pooled per-tick output (poll thread is the sole GetCurrentState
        // caller once the UI reads ud.InputState instead).
        private PadForge.Engine.PooledInputStatePair _statePool;

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
            // Emit every advertised axis, not just the first 6. A remote wheel /
            // flight stick exposes more than 6 axes (NumAxes is carried on the
            // device list and the per-frame state codec ships Axis[0..N]); capping
            // at 6 dropped those extra axes so they couldn't be mapped on the
            // consumer. The first 6 keep the standard X/Y/Z/Rx/Ry/Rz GUIDs; the
            // rest get Slider GUIDs with generic names, mirroring SdlDeviceWrapper.
            // The REAL inventory: every axis the owner ships (RawAxisCount
            // covers extras past the standardized 6), minus standard slots the
            // pad does not physically have (the sparse set).
            int axes = Math.Max(RawAxisCount, 0);
            var supportedAxes = _supportedAxisIndices;
            bool axesSparse = supportedAxes != null && supportedAxes.Length > 0 && supportedAxes.Length != axes;
            // RawButtonCount, not NumButtons: emit a pickable object for every
            // raw button the owner ships, including the extras past the 22
            // standardized gamepad slots. Falls back to NumButtons for old
            // peers (RawButtonCount is maxed with it in the ctor).
            int buttons = Math.Max(RawButtonCount, 0);
            // Only the buttons the owner actually has, named the way the owner
            // names them. Emitting 0..RawButtonCount-1 with generic labels put
            // phantom entries in the consumer's picker and hid A/B/X/Y behind
            // "Button 1..N".
            var supported = _supportedButtonIndices;
            bool sparse = supported != null && supported.Length > 0 && supported.Length != buttons;
            int povs = Math.Max(NumHats, 0);
            int buttonSlots = sparse ? supported.Length : buttons;
            int axisSlots = axesSparse ? supportedAxes.Length : axes;
            var items = new DeviceObjectItem[axisSlots + buttonSlots + povs];
            int idx = 0, offset = 0;
            for (int a = 0; a < axisSlots; a++)
            {
                int i = axesSparse ? supportedAxes[a] : a;
                var item = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = (offset++) * 4
                };
                if (i < StandardAxisGuids.Length)
                {
                    item.ObjectTypeGuid = StandardAxisGuids[i];
                    // The shared table, so the label matches the owner's exact
                    // string and LocalizeObjectName's literal switch hits.
                    item.Name = GamepadObjectNames.Axis(i);
                }
                else if (i < CustomInputState.MaxAxis)
                {
                    // Axes 6..23 land in Axis[] (the frame codec's Axis block covers
                    // 0..23), so surface them as the Axis family: a Slider GUID routed
                    // them to "Slider N" descriptors, which read Sliders[] and were
                    // DEAD for these axes. A non-Slider axis GUID keeps IsAxis true /
                    // IsSlider false. Mirrors SdlDeviceWrapper's raw-open emit.
                    item.ObjectTypeGuid = ObjectGuid.ZAxis;
                    item.Name = $"Axis {i}";
                }
                else
                {
                    // True overflow: only axes 24+ live in Sliders[]. Key InputIndex
                    // and the name on the Sliders[] STORAGE index (i - MaxAxis), not
                    // the raw axis number. "Slider 24" fails the idx < MaxSliders
                    // guard and reads dead while the value sits in Sliders[0..7].
                    item.InputIndex = i - CustomInputState.MaxAxis;
                    item.ObjectTypeGuid = ObjectGuid.Slider;
                    item.Name = $"Slider {i - CustomInputState.MaxAxis}";
                }
                items[idx++] = item;
            }
            for (int b = 0; b < buttonSlots; b++)
            {
                int i = sparse ? supported[b] : b;
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = GamepadButtonLabel(i, NumButtons),
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (offset++) * 4
                };
            }
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

        /// <summary>Names a standardized gamepad slot with the OWNER'S exact
        /// string (the shared GamepadObjectNames table, which SdlDeviceWrapper
        /// also uses), so the label localizes and the same pad reads the same
        /// on both machines. Raw extras keep the flat numbering.</summary>
        private static string GamepadButtonLabel(int i, int numButtons)
            => i >= numButtons ? $"Button {i}" : GamepadObjectNames.Button(i);

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
