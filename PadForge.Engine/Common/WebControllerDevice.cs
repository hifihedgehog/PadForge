using System;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Engine
{
    /// <summary>
    /// Virtual input device representing a browser-connected gamepad.
    /// State is written by the WebSocket receive thread and read by the polling thread.
    /// Implements <see cref="ISdlInputDevice"/> so it flows through the standard
    /// 6-step input pipeline alongside physical devices.
    /// </summary>
    public class WebControllerDevice : ISdlInputDevice
    {
        // Standard gamepad layout: 6 axes, 11 buttons, 1 POV.
        private const int NumGamepadAxes = 6;
        private const int NumGamepadButtons = 11;
        private const int NumGamepadPovs = 1;

        // Distinctive VID/PID to avoid virtual-controller filter false positives.
        private const ushort WebVendorId = 0xBEEF;
        private const ushort WebProductId = 0xCA7E;

        /// <summary>Base ProductGuid for web controllers. The instance
        /// <see cref="ProductGuid"/> property mixes the layout key into
        /// this so xbox360 / ds4 / touchpad clients each carry a distinct
        /// product identity. Without per-layout differentiation, the
        /// FindOrCreateUserDevice "BT reconnect" fallback in
        /// InputManager.Step1 would migrate an offline xbox360 web
        /// device's slot mapping onto a freshly-connecting ds4 web
        /// device (and vice versa), since the fallback gates only on
        /// ProductGuid + offline status. Real hardware distinct enough
        /// to share a layout still shares a ProductGuid; web layouts are
        /// software constructs and are deliberately treated as separate
        /// products.</summary>
        private static readonly Guid WebProductGuidBase =
            new Guid("BEBC0000-0000-0000-0000-CAFEFACE0001");

        // Axis names matching the standard gamepad layout (LX, LY, LT, RX, RY, RT).
        private static readonly Guid[] AxisGuids =
        {
            ObjectGuid.XAxis,  ObjectGuid.YAxis,  ObjectGuid.ZAxis,
            ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis
        };

        private static readonly string[] AxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };

        private static readonly string[] ButtonNames =
            { "A", "B", "X", "Y", "Left Shoulder", "Right Shoulder", "Back", "Start", "Left Stick Button", "Right Stick Button", "Guide" };

        // Thread-safe state: written by WebSocket thread, read by polling thread.
        // _currentState is non-volatile; the lock-protected writes (UpdateAxis /
        // UpdateButton mutate under _stateLock) and Volatile.Read on the get
        // path provide the memory ordering. Marking the field volatile AND
        // passing it to Volatile.Read by ref triggered CS0420 because the
        // ref takes the field's address, dropping the volatile contract at
        // the call site.
        private CustomInputState _currentState = new CustomInputState();
        private readonly object _stateLock = new object();
        private volatile bool _connected;

        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => _isTouchpadDevice ? 0 : NumGamepadAxes;
        // NumButtons describes the slot range, not the count of populated
        // slots. The touchpad click rides Buttons[16] (SDL_GAMEPAD_BUTTON_TOUCHPAD's
        // canonical slot), so touchpad-equipped layouts need NumButtons=17
        // for dense-iter consumers (macro-trigger recorder, mapping picker's
        // raw-button list) to actually reach slot 16. The sparse
        // SupportedButtonIndices below is what the Devices preview reads
        // to surface which slots are real, so its grid still shows 11
        // circles for a ds4 gamepad client (plus the touchpad-click circle)
        // and 1 for the touchpad-only client. GetDeviceObjects deliberately
        // does NOT add slot 16 — the mapping picker keeps the canonical
        // "Touchpad 0 Click" descriptor as the single source-of-truth.
        public int NumButtons =>
            _isTouchpadDevice ? 17
            : HasTouchpad ? 17
            : NumGamepadButtons;
        public int RawButtonCount => NumButtons;
        public int NumHats => _isTouchpadDevice ? 0 : NumGamepadPovs;
        public int[] SupportedButtonIndices
        {
            get
            {
                if (_isTouchpadDevice)
                    return _touchpadOnlyButtons;
                if (HasTouchpad)
                {
                    var a = new int[NumGamepadButtons + 1];
                    for (int i = 0; i < NumGamepadButtons; i++) a[i] = i;
                    a[NumGamepadButtons] = 16;
                    return a;
                }
                var dense = new int[NumGamepadButtons];
                for (int i = 0; i < NumGamepadButtons; i++) dense[i] = i;
                return dense;
            }
        }
        private static readonly int[] _touchpadOnlyButtons = { 16 };
        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => true;
        public bool HasRumbleTriggers => false;
        public bool HasHaptic => false;
        public bool HasGyro => false;
        public bool HasAccel => false;
        public bool HasTouchpad { get; set; }
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public ushort VendorId => WebVendorId;
        public ushort ProductId => WebProductId;
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }

        public bool IsAttached => _connected;

        /// <summary>
        /// Fired on the polling thread when SetRumble is called.
        /// Parameters: (lowFrequency, highFrequency) in 0-65535 range.
        /// </summary>
        public event Action<ushort, ushort> RumbleRequested;

        /// <summary>
        /// Creates a new web controller device.
        /// </summary>
        /// <param name="clientId">Unique client identifier (from browser localStorage).</param>
        /// <param name="displayName">Human-readable name (e.g. "Web Controller 1").</param>
        /// <param name="isTouchpad">When true, device reports as touchpad type with HasTouchpad=true.</param>
        /// <param name="layoutKey">Layout discriminator ("xbox360" / "ds4" / "touchpad").
        /// Mixed into <see cref="ProductGuid"/> so different layouts read as different products
        /// and the BT-reconnect fallback in <c>FindOrCreateUserDevice</c> doesn't migrate one
        /// layout's slot mapping onto another. Defaults to "xbox360" for back-compat with any
        /// historical caller that didn't pass a layout (kept just so the constructor signature
        /// is additive — every WebControllerServer call site passes the explicit value).</param>
        public WebControllerDevice(string clientId, string displayName, bool isTouchpad = false, string layoutKey = "xbox360")
        {
            Name = displayName;
            DevicePath = $"web://{clientId}";
            InstanceGuid = BuildGuid(clientId);
            ProductGuid = BuildProductGuid(layoutKey);
            SdlInstanceId = (uint)clientId.GetHashCode();
            HasTouchpad = isTouchpad;
            _isTouchpadDevice = isTouchpad;

            // Center stick axes at midpoint, triggers at 0 (full off).
            var state = new CustomInputState();
            if (!isTouchpad)
            {
                for (int i = 0; i < NumGamepadAxes; i++)
                    state.Axis[i] = (i == 2 || i == 5) ? 0 : 32767;
            }
            Volatile.Write(ref _currentState, state);
        }

        private readonly bool _isTouchpadDevice;

        /// <summary>Updates an axis value. Called from WebSocket receive thread.</summary>
        /// <param name="code">Axis index (0=LX, 1=LY, 2=LT, 3=RX, 4=RY, 5=RT).</param>
        /// <param name="value">Unsigned value 0-65535 (center = 32767).</param>
        public void UpdateAxis(int code, int value)
        {
            if (code < 0 || code >= NumGamepadAxes) return;
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Axis[code] = value;
                _currentState = s;
            }
        }

        /// <summary>Updates a button state. Called from WebSocket receive thread.</summary>
        /// <param name="code">Button index (0=A, 1=B, ..., 10=Guide, 20=Touchpad Click).</param>
        /// <param name="pressed">True if pressed.</param>
        public void UpdateButton(int code, bool pressed)
        {
            if (code < 0 || code >= CustomInputState.MaxButtons) return;
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Buttons[code] = pressed;
                _currentState = s;
            }
        }

        /// <summary>Updates POV hat value. Called from WebSocket receive thread.</summary>
        /// <param name="value">Centidegrees (0=N, 9000=E, etc.) or -1 for centered.</param>
        public void UpdatePov(int value)
        {
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                s.Povs[0] = value;
                _currentState = s;
            }
        }

        /// <summary>Updates a touchpad finger's position and contact state.
        /// Phone clients drive a single-pad two-finger virtual touchpad
        /// over WebSocket. Contact IDs are synthesized on rising / falling
        /// edges so the gesture engine sees a stable input shape across
        /// devices.</summary>
        public void UpdateTouchpadFinger(int finger, float x, float y, bool down)
        {
            lock (_stateLock)
            {
                var s = _currentState.Clone();
                if (s.Touchpads == null || s.Touchpads.Length < 1
                    || s.Touchpads[0] == null || s.Touchpads[0].MaxFingers < 2)
                {
                    s.Touchpads = new[] { new TouchpadInputState(2) };
                }
                var pad = s.Touchpads[0];
                if (finger >= 0 && finger < pad.MaxFingers)
                {
                    pad.FingerX[finger] = x;
                    pad.FingerY[finger] = y;
                    pad.FingerPressure[finger] = down ? 1f : 0f;
                    bool wasDown = pad.FingerDown[finger];
                    pad.FingerDown[finger] = down;
                    if (down && !wasDown) pad.FingerContactId[finger] = _webContactIdNext++;
                    else if (!down && wasDown) pad.FingerContactId[finger] = -1;
                }
                _currentState = s;
            }
        }

        // Monotonic contact-ID counter for the web phone-controller's
        // virtual touchpad surface. Increments on each finger rising edge.
        private int _webContactIdNext = 1;

        /// <summary>Sets the connection state.</summary>
        public void SetConnected(bool connected) => _connected = connected;

        // Pooled per-tick output. The publisher is strict copy-on-write
        // (every mutator clones, edits, swaps), so a single volatile grab
        // plus CopyInto needs no lock.
        private PadForge.Engine.PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            var src = _currentState;
            var dst = _statePool.Next();
            src.CopyInto(dst);
            return dst;
        }

        public DeviceObjectItem[] GetDeviceObjects()
        {
            // Touchpad-only web layout has no axes / buttons / POVs —
            // returning the gamepad shape here would surface 6 phantom
            // axes + 11 phantom buttons + 1 phantom D-Pad in the source
            // picker alongside the touchpad sources (since
            // MappingDisplayResolver.BuildInputChoices walks DeviceObjects
            // unconditionally). The touchpad-specific sources come from
            // BuildInputChoices's HasTouchpad block on their own.
            if (_isTouchpadDevice)
                return Array.Empty<DeviceObjectItem>();

            // Gamepad-shaped surface (6 axes + 11 buttons + 1 POV).
            // Touchpad finger axes and the touchpad click button do NOT
            // belong here — finger data lives in CustomInputState.Touchpad*
            // and the click bit lives at Buttons[16]. Adding them here
            // would double-list the mapping dropdown: BuildInputChoices
            // walks DeviceObjects AND has its own canonical
            // "Touchpad 0 Finger N X/Y/Down" + "Touchpad 0 Click" block
            // for any HasTouchpad device, so the same inputs appeared
            // twice. The touchpad descriptors are the right source for
            // mappings; this surface stays gamepad-shaped.
            var items = new DeviceObjectItem[NumGamepadAxes + NumGamepadButtons + NumGamepadPovs];
            int idx = 0;

            // Axes.
            for (int i = 0; i < NumGamepadAxes; i++)
            {
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = AxisGuids[i],
                    Name = AxisNames[i],
                    ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                    Offset = i * 4
                };
            }

            // Buttons.
            for (int i = 0; i < NumGamepadButtons; i++)
            {
                items[idx++] = new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = ButtonNames[i],
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (NumGamepadAxes + i) * 4
                };
            }

            // POV.
            items[idx++] = new DeviceObjectItem
            {
                InputIndex = 0,
                ObjectTypeGuid = ObjectGuid.PovController,
                Name = "D-Pad",
                ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                Offset = (NumGamepadAxes + NumGamepadButtons) * 4
            };

            return items;
        }

        public int GetInputDeviceType() => _isTouchpadDevice ? InputDeviceType.Touchpad : InputDeviceType.Gamepad;

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
            _connected = false;
            GC.SuppressFinalize(this);
        }

        private static Guid BuildGuid(string identifier)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identifier));
            return new Guid(hash);
        }

        /// <summary>Mixes the layout key into <see cref="WebProductGuidBase"/>
        /// so each web layout reports a distinct ProductGuid. Hashed
        /// rather than indexed so adding a future layout (e.g. "joycon")
        /// doesn't shift any existing product identity.</summary>
        private static Guid BuildProductGuid(string layoutKey)
        {
            string normalized = (layoutKey ?? "xbox360").Trim().ToLowerInvariant();
            if (normalized.Length == 0) normalized = "xbox360";
            using var md5 = MD5.Create();
            byte[] basebytes = WebProductGuidBase.ToByteArray();
            byte[] saltbytes = Encoding.UTF8.GetBytes(":" + normalized);
            byte[] combined = new byte[basebytes.Length + saltbytes.Length];
            Buffer.BlockCopy(basebytes, 0, combined, 0, basebytes.Length);
            Buffer.BlockCopy(saltbytes, 0, combined, basebytes.Length, saltbytes.Length);
            byte[] hash = md5.ComputeHash(combined);
            return new Guid(hash);
        }
    }
}
