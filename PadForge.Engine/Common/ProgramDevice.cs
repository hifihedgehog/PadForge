using System;
using System.Collections.Generic;
using System.Threading;

namespace PadForge.Engine
{
    /// <summary>One axis or slider a program declared: where it sits in the
    /// state, its name and its rest. Indexes 0 to 23 are the axes and 24 to
    /// 31 the sliders, one numbering for both on the Automation pipe.</summary>
    public readonly record struct ProgramAxis(int Index, string Name, int Rest)
    {
        /// <summary>The first index that names a slider.</summary>
        public const int FirstSlider = CustomInputState.MaxAxis;

        /// <summary>One past the last slider.</summary>
        public const int End = CustomInputState.MaxAxis + CustomInputState.MaxSliders;

        public bool IsSlider => Index >= FirstSlider;
    }

    /// <summary>
    /// What a program's device has (#597): named buttons, axes, sliders and
    /// hats, sensors, touchpads, a battery, and the feedback it takes. Up to
    /// what <see cref="CustomInputState"/> holds. Made once per declaration
    /// and never changed after it is handed to a device.
    /// </summary>
    public sealed class ProgramDeviceShape
    {
        /// <summary>The buttons by index, each with its name. Indexes are
        /// below <see cref="CustomInputState.MaxButtons"/>.</summary>
        public IReadOnlyList<(int Index, string Name)> Buttons { get; init; } = Array.Empty<(int, string)>();

        /// <summary>The axes and sliders, in index order.</summary>
        public IReadOnlyList<ProgramAxis> Axes { get; init; } = Array.Empty<ProgramAxis>();

        /// <summary>The hats' names, hat 0 first. At most
        /// <see cref="CustomInputState.MaxPovs"/>.</summary>
        public IReadOnlyList<string> Hats { get; init; } = Array.Empty<string>();

        public bool Gyro { get; init; }
        public bool Accel { get; init; }

        /// <summary>The finger count of each touchpad, pad 0 first.</summary>
        public IReadOnlyList<int> TouchpadFingers { get; init; } = Array.Empty<int>();

        public bool Battery { get; init; }

        /// <summary>The feedback the device takes, each passed on to the
        /// program.</summary>
        public bool Rumble { get; init; }
        public bool TriggerRumble { get; init; }

        /// <summary>The device takes adaptive trigger effects (#600 item 18):
        /// the 11-byte programs a game sends a DualSense's two triggers,
        /// passed on as <c>e trigfx</c>.</summary>
        public bool TriggerEffects { get; init; }
        public bool Light { get; init; }
        public bool Player { get; init; }

        /// <summary>The <see cref="InputDeviceType"/> the device reports. A
        /// device a program declares on the pipe is a Program device. The
        /// built-in Test Controller reports a gamepad's, so it is read and
        /// mapped in SDL's gamepad layout as a web pad is, and Replay gives
        /// each device the type its recording names.</summary>
        public int DeviceType { get; init; } = InputDeviceType.Program;

        /// <summary>The rest of axis or slider <paramref name="index"/>:
        /// what was declared, and 0 for one that was not.</summary>
        public int RestOf(int index)
        {
            foreach (var axis in Axes)
                if (axis.Index == index) return axis.Rest;
            return 0;
        }
    }

    /// <summary>
    /// Several controls of a program's device set as one change (#597), the
    /// way the Automation pipe's <c>s</c> line names them. Applied in one
    /// swap, so a stick's two axes never land on different passes. Values
    /// are already in <see cref="CustomInputState"/>'s ranges.
    /// </summary>
    public sealed class ProgramDeviceChange
    {
        public readonly List<(int Index, bool Down)> Buttons = new();

        /// <summary>Axes 0 to 23 and sliders 24 to 31.</summary>
        public readonly List<(int Index, int Value)> Axes = new();
        public readonly List<(int Index, int Value)> Hats = new();

        /// <summary>Hat directions set on their own (#600 item 21), each
        /// pressed or released. They go into their hat as the change lands,
        /// after the change's whole hats, beside the hat's other directions.
        /// Up and Down together cancel, as Left and Right do.</summary>
        public readonly List<(int Hat, HatDirections Direction, bool Down)> Directions = new();
        public float[] Gyro;
        public float[] Accel;
        public readonly List<(int Pad, int Finger, float X, float Y, bool Down, float Pressure)> Fingers = new();
        public readonly List<(int Pad, bool Clicked)> Clicks = new();
        public int? Battery;
        public bool? Charging;

        /// <summary>Every control goes to its rest first, before the rest
        /// of the change applies (#600): the pipe's <c>rest</c> word. The
        /// battery stays as it was.</summary>
        public bool Rest;

        public bool IsEmpty => !Rest && Buttons.Count == 0 && Axes.Count == 0 && Hats.Count == 0 && Directions.Count == 0
            && Gyro == null && Accel == null && Fingers.Count == 0 && Clicks.Count == 0 && Battery == null && Charging == null;
    }

    /// <summary>
    /// A device a program adds over the Automation pipe (#597): a script, an
    /// assistant or a plugin, and later the built-in Test Controller and
    /// Replay. Its shape is what the program declared, with the names it
    /// gave, and its state is what the program last set.
    ///
    /// <para>State is copy-on-write, as <see cref="WebControllerDevice"/>
    /// keeps it: each change clones the whole state under a lock, edits the
    /// copy and publishes it. A change that would be a control's second
    /// before the next read waits in a queue (#600 item 22), and the poll
    /// thread's read lands what waits under the same lock, held for that
    /// alone, then copies the state into a pooled buffer.</para>
    ///
    /// <para>Feedback goes back to the program. Rumble arrives through
    /// <see cref="SetRumble"/>, trigger rumble through
    /// <see cref="SetTriggerRumble"/>, the light and the player number
    /// through <see cref="IRelayedFeedbackDevice"/>. The last value of each
    /// is kept, so a program that connects again and declares the same key
    /// hears where things stand.</para>
    /// </summary>
    public sealed class ProgramDevice : ISdlInputDevice, IRelayedFeedbackDevice
    {
        /// <summary>The vendor and product IDs every program device reports:
        /// PadForge's own, apart from any hardware's, so no writer keyed on
        /// a vendor takes it for a real pad.</summary>
        public const ushort ProgramVendorId = 0xBEEF;
        public const ushort ProgramProductId = 0xC0DE;

        private readonly object _stateLock = new object();
        // Not volatile: it is read with Volatile.Read by reference, which a
        // volatile field refuses (CS0420), as WebControllerDevice notes.
        private CustomInputState _state;
        private ProgramDeviceShape _shape;
        private string _name;
        private volatile bool _attached;
        private PooledInputStatePair _pool;

        // Feedback last passed on, for a program that comes back.
        private long _rumble = -1, _triggerRumble = -1;
        private int _led = -1, _player = -1;

        /// <param name="instanceGuid">Stable for the program and the key it
        /// gave, so mappings survive a restart of either side.</param>
        /// <param name="productGuid">The device's own, so one device's first
        /// connect never adopts another's saved row.</param>
        /// <param name="devicePath">A path of PadForge's own scheme, which
        /// names no hardware.</param>
        /// <param name="name">The name the program gave, already
        /// cleaned.</param>
        /// <param name="shape">What the device has.</param>
        public ProgramDevice(Guid instanceGuid, Guid productGuid, string devicePath, string name, ProgramDeviceShape shape)
        {
            InstanceGuid = instanceGuid;
            ProductGuid = productGuid;
            DevicePath = devicePath ?? string.Empty;
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
            Reshape(name, shape);
        }

        /// <summary>
        /// Takes a new name and shape, as a program that declares the same
        /// key again does. The device object, its identity and its mappings
        /// stay. Its state starts at the new shape's rest, and
        /// <see cref="CapabilitiesChanged"/> is raised for the row to read
        /// the shape again.
        /// </summary>
        public void Reshape(string name, ProgramDeviceShape shape)
        {
            shape ??= new ProgramDeviceShape();
            lock (_stateLock)
            {
                Volatile.Write(ref _shape, shape);
                Volatile.Write(ref _name, name ?? string.Empty);
                Volatile.Write(ref _state, RestState(shape, battery: -1, charging: false));
                DropWaitingLocked();
            }
            CapabilitiesChanged?.Invoke();
        }

        /// <summary>Takes a new name and keeps the shape and the state, as a
        /// name PadForge words itself does when the interface language
        /// changes. <see cref="CapabilitiesChanged"/> is raised for the row
        /// to read the name again.</summary>
        public void Rename(string name)
        {
            name ??= string.Empty;
            if (string.Equals(Volatile.Read(ref _name), name, StringComparison.Ordinal)) return;
            Volatile.Write(ref _name, name);
            CapabilitiesChanged?.Invoke();
        }

        /// <summary>The shape in force.</summary>
        public ProgramDeviceShape Shape => Volatile.Read(ref _shape);

        /// <summary>The name the device's row keeps as its product's, when it
        /// is not the name shown: a built-in plugin's device keeps a name in
        /// no language here, and the name shown is made from it in the
        /// language in use each time it is shown, an offline row from an
        /// earlier run included. Null keeps the name shown. Set before the
        /// device is registered.</summary>
        public string ProductName { get; set; }

        /// <summary>Raised after <see cref="Reshape"/>, on the caller's
        /// thread.</summary>
        public event Action CapabilitiesChanged;

        private static CustomInputState RestState(ProgramDeviceShape shape, int battery, bool charging)
        {
            var s = new CustomInputState();
            foreach (var axis in shape.Axes)
            {
                if (axis.IsSlider) s.Sliders[axis.Index - ProgramAxis.FirstSlider] = axis.Rest;
                else s.Axis[axis.Index] = axis.Rest;
            }
            if (shape.TouchpadFingers.Count > 0)
            {
                s.Touchpads = new TouchpadInputState[shape.TouchpadFingers.Count];
                for (int p = 0; p < s.Touchpads.Length; p++)
                {
                    var pad = new TouchpadInputState(shape.TouchpadFingers[p]);
                    for (int f = 0; f < pad.MaxFingers; f++) pad.FingerContactId[f] = -1;
                    s.Touchpads[p] = pad;
                }
            }
            s.BatteryPercent = shape.Battery ? battery : -1;
            s.BatteryCharging = shape.Battery && charging;
            return s;
        }

        // ── One change per control per read (#600 item 22) ──

        /// <summary>The most changes that wait for later reads. Past it a
        /// change is folded into the last one waiting, its values over that
        /// one's.</summary>
        public const int MostWaiting = 1024;

        /// <summary>How long without a read before the device counts as not
        /// polled, and a change lands at once whatever it touches: the engine
        /// is stopped or the device is not on its list.</summary>
        public static readonly long NotPolledTicks = System.Diagnostics.Stopwatch.Frequency / 10;

        // Every control a change can touch, as one number: buttons, then
        // axes and sliders, hats, the sensors, the battery, the touchpads'
        // clicks and fingers.
        private const int KeyAxes = CustomInputState.MaxButtons;
        private const int KeyHats = KeyAxes + ProgramAxis.End;
        private const int KeyGyro = KeyHats + CustomInputState.MaxPovs;
        private const int KeyAccel = KeyGyro + 1;
        private const int KeyBattery = KeyAccel + 1;
        private const int KeyCharging = KeyBattery + 1;
        private const int KeyClicks = KeyCharging + 1;
        private const int KeyFingers = KeyClicks + 8;
        private const int KeyCount = KeyFingers + 8 * 16;

        // Touched since the last read, under the state lock: the controls a
        // change landed on that no read has taken yet.
        private readonly bool[] _touched = new bool[KeyCount];
        private readonly List<int> _touchedList = new();
        private readonly List<(ProgramDeviceChange Change, long Due)> _waiting = new();
        private readonly List<(ProgramDeviceChange Change, long Due)> _kept = new();
        private readonly bool[] _held = new bool[KeyCount];
        private readonly List<int> _heldList = new();
        private volatile int _waitingCount;
        private long _lastRead;

        /// <summary>Test seam: the clock, in Stopwatch ticks. Null reads
        /// Stopwatch.</summary>
        public Func<long> ClockForTest { get; set; }

        private long Now() => ClockForTest?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>How many changes wait for a later read, for tests.</summary>
        public int Waiting => _waitingCount;

        private static void Keys(ProgramDeviceChange change, List<int> into)
        {
            into.Clear();
            if (change.Rest)
            {
                // Rest touches every control.
                for (int k = 0; k < KeyCount; k++) into.Add(k);
                return;
            }
            foreach (var (index, _) in change.Buttons) if ((uint)index < CustomInputState.MaxButtons) into.Add(index);
            foreach (var (index, _) in change.Axes) if ((uint)index < ProgramAxis.End) into.Add(KeyAxes + index);
            foreach (var (index, _) in change.Hats) if ((uint)index < CustomInputState.MaxPovs) into.Add(KeyHats + index);
            foreach (var (hat, _, _) in change.Directions) if ((uint)hat < CustomInputState.MaxPovs) into.Add(KeyHats + hat);
            if (change.Gyro != null) into.Add(KeyGyro);
            if (change.Accel != null) into.Add(KeyAccel);
            if (change.Battery != null) into.Add(KeyBattery);
            if (change.Charging != null) into.Add(KeyCharging);
            foreach (var (pad, _) in change.Clicks) if ((uint)pad < 8) into.Add(KeyClicks + pad);
            foreach (var (pad, finger, _, _, _, _) in change.Fingers)
                if ((uint)pad < 8 && (uint)finger < 16) into.Add(KeyFingers + pad * 16 + finger);
        }

        private readonly List<int> _keys = new();

        /// <summary>
        /// Applies several controls as one change, no sooner than
        /// <paramref name="due"/> (Stopwatch ticks, 0 for now). A control
        /// changes at most once between two reads: a change that touches a
        /// control already changed since the last read, or that is not due,
        /// waits for a later read, as does every later change of the same
        /// control, so a press and its release sent between two passes land
        /// on two passes in their order. A control the shape does not have
        /// is left out, and values outside a control's range are brought
        /// inside it. Called from the program's connection.
        /// </summary>
        public void Apply(ProgramDeviceChange change, long due = 0)
        {
            if (change == null || change.IsEmpty) return;
            lock (_stateLock)
            {
                long now = Now();
                if (now - _lastRead > NotPolledTicks)
                {
                    // Nothing reads the device: what waits lands now, in
                    // order, and a change not due yet still waits.
                    FlushLocked(now, all: false);
                    if (_waiting.Count == 0 && due <= now)
                    {
                        Land(change);
                        return;
                    }
                }
                Keys(change, _keys);
                bool free = _waiting.Count == 0 && due <= now;
                if (free)
                    foreach (int k in _keys)
                        if (_touched[k]) { free = false; break; }
                if (free)
                {
                    Land(change);
                    Touch(_keys);
                    return;
                }
                if (_waiting.Count >= MostWaiting)
                {
                    Fold(_waiting[^1].Change, change);
                    return;
                }
                _waiting.Add((change, due));
                _waitingCount = _waiting.Count;
            }
        }

        private void Touch(List<int> keys)
        {
            foreach (int k in keys)
            {
                if (_touched[k]) continue;
                _touched[k] = true;
                _touchedList.Add(k);
            }
        }

        /// <summary>Puts <paramref name="later"/>'s values into
        /// <paramref name="into"/>, later values over earlier ones.</summary>
        private static void Fold(ProgramDeviceChange into, ProgramDeviceChange later)
        {
            if (later.Rest)
            {
                into.Rest = true;
                into.Buttons.Clear(); into.Axes.Clear(); into.Hats.Clear(); into.Directions.Clear();
                into.Fingers.Clear(); into.Clicks.Clear();
                into.Gyro = null; into.Accel = null;
            }
            into.Buttons.AddRange(later.Buttons);
            into.Axes.AddRange(later.Axes);
            // A whole hat set later stands over the directions set before
            // it, which would otherwise go in after it.
            foreach (var (index, _) in later.Hats) into.Directions.RemoveAll(d => d.Hat == index);
            into.Hats.AddRange(later.Hats);
            into.Directions.AddRange(later.Directions);
            into.Fingers.AddRange(later.Fingers);
            into.Clicks.AddRange(later.Clicks);
            if (later.Gyro != null) into.Gyro = later.Gyro;
            if (later.Accel != null) into.Accel = later.Accel;
            if (later.Battery != null) into.Battery = later.Battery;
            if (later.Charging != null) into.Charging = later.Charging;
        }

        /// <summary>Under the lock, at a read: the changes that wait land in
        /// their order, each control once, counting the changes that landed
        /// since the last read, and a change that cannot land holds back
        /// every later change of its controls.</summary>
        private void DrainLocked(long now)
        {
            if (_waiting.Count == 0) return;
            _kept.Clear();
            foreach (var item in _waiting)
            {
                Keys(item.Change, _keys);
                bool land = item.Due <= now;
                if (land)
                    foreach (int k in _keys)
                        if (_touched[k] || _held[k]) { land = false; break; }
                if (land)
                {
                    Land(item.Change);
                    Touch(_keys);
                    continue;
                }
                _kept.Add(item);
                foreach (int k in _keys)
                {
                    if (_held[k]) continue;
                    _held[k] = true;
                    _heldList.Add(k);
                }
            }
            foreach (int k in _heldList) _held[k] = false;
            _heldList.Clear();
            _waiting.Clear();
            _waiting.AddRange(_kept);
            _kept.Clear();
            _waitingCount = _waiting.Count;
        }

        /// <summary>Under the lock, with nothing reading the device: every
        /// change due lands in its order, all when
        /// <paramref name="all"/>.</summary>
        private void FlushLocked(long now, bool all)
        {
            foreach (int k in _touchedList) _touched[k] = false;
            _touchedList.Clear();
            if (_waiting.Count == 0) return;
            int landed = 0;
            foreach (var item in _waiting)
            {
                if (!all && item.Due > now) break;
                Land(item.Change);
                landed++;
            }
            _waiting.RemoveRange(0, landed);
            _waitingCount = _waiting.Count;
        }

        /// <summary>Lands the changes that are due when nothing has read the
        /// device for a while: the sessions' clock calls it, so a timed step
        /// lands with the engine stopped too.</summary>
        public void LandDue()
        {
            if (_waitingCount == 0) return;
            lock (_stateLock)
            {
                long now = Now();
                if (now - _lastRead > NotPolledTicks) FlushLocked(now, all: false);
            }
        }

        private void DropWaitingLocked()
        {
            _waiting.Clear();
            _waitingCount = 0;
            foreach (int k in _touchedList) _touched[k] = false;
            _touchedList.Clear();
        }

        /// <summary>Under the lock: one change on the state, published.</summary>
        private void Land(ProgramDeviceChange change)
        {
            var shape = _shape;
            var s = change.Rest
                ? RestState(shape, _state.BatteryPercent, _state.BatteryCharging)
                : _state.Clone();
            foreach (var (index, down) in change.Buttons)
                if (HasButton(shape, index)) s.Buttons[index] = down;
            foreach (var (index, value) in change.Axes)
            {
                if (!HasAxis(shape, index)) continue;
                int v = Math.Clamp(value, 0, 65535);
                if (index >= ProgramAxis.FirstSlider) s.Sliders[index - ProgramAxis.FirstSlider] = v;
                else s.Axis[index] = v;
            }
            foreach (var (index, value) in change.Hats)
                if (index >= 0 && index < shape.Hats.Count)
                    s.Povs[index] = value >= 0 && value < 36000 ? value : -1;
            if (change.Directions.Count > 0) LandDirections(change, shape, s);
            if (change.Gyro != null && shape.Gyro)
                for (int i = 0; i < 3; i++) s.Gyro[i] = Finite(change.Gyro[i]);
            if (change.Accel != null && shape.Accel)
                for (int i = 0; i < 3; i++) s.Accel[i] = Finite(change.Accel[i]);
            foreach (var (padIndex, finger, x, y, down, pressure) in change.Fingers)
            {
                if (s.Touchpads == null || padIndex < 0 || padIndex >= s.Touchpads.Length) continue;
                var pad = s.Touchpads[padIndex];
                if (finger < 0 || finger >= pad.MaxFingers) continue;
                pad.FingerX[finger] = Math.Clamp(Finite(x), 0f, 1f);
                pad.FingerY[finger] = Math.Clamp(Finite(y), 0f, 1f);
                pad.FingerPressure[finger] = down ? Math.Clamp(Finite(pressure), 0f, 1f) : 0f;
                bool was = pad.FingerDown[finger];
                pad.FingerDown[finger] = down;
                if (down && !was) pad.FingerContactId[finger] = _nextContact++;
                else if (!down && was) pad.FingerContactId[finger] = -1;
            }
            foreach (var (padIndex, clicked) in change.Clicks)
            {
                if (s.Touchpads == null || padIndex < 0 || padIndex >= s.Touchpads.Length) continue;
                s.Touchpads[padIndex].Clicked = clicked;
                // The first pad's click rides button 16 as well, where
                // every other touchpad device carries it.
                if (padIndex == 0) s.Buttons[16] = clicked;
            }
            if (shape.Battery)
            {
                if (change.Battery.HasValue) s.BatteryPercent = Math.Clamp(change.Battery.Value, 0, 100);
                if (change.Charging.HasValue) s.BatteryCharging = change.Charging.Value;
            }
            Volatile.Write(ref _state, s);
        }

        /// <summary>A change's hat directions into the state it lands on
        /// (#600 item 21): each hat's directions together, over the hat as
        /// the change's whole hats left it, a later word for the same
        /// direction over an earlier one. A hat keeps its exact angle unless
        /// they change which directions it holds.</summary>
        private static void LandDirections(ProgramDeviceChange change, ProgramDeviceShape shape, CustomInputState s)
        {
            int hats = Math.Min(shape.Hats.Count, CustomInputState.MaxPovs);
            Span<int> pressed = stackalloc int[CustomInputState.MaxPovs];
            Span<int> released = stackalloc int[CustomInputState.MaxPovs];
            pressed.Clear();
            released.Clear();
            foreach (var (hat, direction, down) in change.Directions)
            {
                if (hat < 0 || hat >= hats) continue;
                int bit = (int)direction;
                if (down) { pressed[hat] |= bit; released[hat] &= ~bit; }
                else { released[hat] |= bit; pressed[hat] &= ~bit; }
            }
            for (int hat = 0; hat < hats; hat++)
            {
                if (pressed[hat] == 0 && released[hat] == 0) continue;
                var was = PlainUnits.HatToDirections(s.Povs[hat]);
                var now = (was | (HatDirections)pressed[hat]) & ~(HatDirections)released[hat];
                if (now != was) s.Povs[hat] = PlainUnits.DirectionsToHat(now);
            }
        }

        private int _nextContact = 1;

        /// <summary>
        /// Takes a whole state as the device's next one, as Replay feeds a
        /// recorded frame. The state is copied, so the caller may change its
        /// own afterward. Called from the feeder's thread.
        /// </summary>
        public void Publish(CustomInputState state)
        {
            if (state == null) return;
            var s = state.Clone();
            lock (_stateLock)
            {
                DropWaitingLocked();
                Volatile.Write(ref _state, s);
            }
        }

        private static float Finite(float value) => float.IsFinite(value) ? value : 0f;

        private static bool HasButton(ProgramDeviceShape shape, int index)
        {
            foreach (var (i, _) in shape.Buttons)
                if (i == index) return true;
            return false;
        }

        private static bool HasAxis(ProgramDeviceShape shape, int index)
        {
            foreach (var axis in shape.Axes)
                if (axis.Index == index) return true;
            return false;
        }

        /// <summary>Everything at rest: buttons up, axes and sliders at their
        /// declared rest, hats centered, motion still and fingers lifted. The
        /// battery keeps its level. Used when the program leaves or stops
        /// answering, so nothing it held stays held.</summary>
        public void Rest()
        {
            lock (_stateLock)
            {
                var current = _state;
                DropWaitingLocked();
                Volatile.Write(ref _state, RestState(_shape, current.BatteryPercent, current.BatteryCharging));
            }
        }

        /// <summary>Whether the program's connection is live. The row goes
        /// offline through the input manager, not through this.</summary>
        public void SetAttached(bool attached) => _attached = attached;

        // ── ISdlInputDevice ──

        public uint SdlInstanceId { get; }
        public string Name => Volatile.Read(ref _name);

        public int NumAxes
        {
            get
            {
                int max = -1;
                foreach (var axis in Shape.Axes) if (!axis.IsSlider && axis.Index > max) max = axis.Index;
                return max + 1;
            }
        }

        public int NumButtons
        {
            get
            {
                int max = -1;
                foreach (var (index, _) in Shape.Buttons) if (index > max) max = index;
                return max + 1;
            }
        }

        public int RawButtonCount => NumButtons;
        public int NumHats => Shape.Hats.Count;

        /// <summary>The declared buttons, in order. Empty when the program
        /// declared none: the device has no buttons, which is a claim and
        /// not the absence of one.</summary>
        public int[] SupportedButtonIndices
        {
            get
            {
                var buttons = Shape.Buttons;
                var list = new int[buttons.Count];
                for (int i = 0; i < list.Length; i++) list[i] = buttons[i].Index;
                Array.Sort(list);
                return list;
            }
        }

        /// <summary>The declared axes below the sliders, in order. Empty when
        /// there are none, in the same sense.</summary>
        public int[] SupportedAxisIndices
        {
            get
            {
                var list = new List<int>();
                foreach (var axis in Shape.Axes) if (!axis.IsSlider) list.Add(axis.Index);
                list.Sort();
                return list.ToArray();
            }
        }

        public IntPtr GamepadHandle => IntPtr.Zero;
        public bool HasRumble => Shape.Rumble;
        public bool HasRumbleTriggers => Shape.TriggerRumble;
        public bool HasHaptic => false;
        public bool HasGyro => Shape.Gyro;
        public bool HasAccel => Shape.Accel;
        public bool HasTouchpad => Shape.TouchpadFingers.Count > 0;
        public int NumTouchpads => Shape.TouchpadFingers.Count;

        public int[] TouchpadFingerCounts
        {
            get
            {
                var fingers = Shape.TouchpadFingers;
                var counts = new int[fingers.Count];
                for (int i = 0; i < counts.Length; i++) counts[i] = fingers[i];
                return counts;
            }
        }

        public bool? TouchpadPressureSupported => HasTouchpad ? true : null;
        public bool? TouchpadClickSupported => HasTouchpad ? true : null;
        public HapticEffectStrategy HapticStrategy => HapticEffectStrategy.None;
        public IntPtr HapticHandle => IntPtr.Zero;
        public uint HapticFeatures => 0;
        public int NumHapticAxes => 0;
        public bool IsAttached => _attached;
        public ushort VendorId => ProgramVendorId;
        public ushort ProductId => ProgramProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        /// <summary>The state the program set, copied into a pooled buffer.
        /// The changes that wait land first, one change per control per read
        /// (#600 item 22). The poll thread is the only reader, once a pass,
        /// and the lock it takes is held for the drain alone.</summary>
        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            var live = Live;
            CustomInputState src;
            if (live != null) src = live();
            else
            {
                lock (_stateLock)
                {
                    long now = Now();
                    _lastRead = now;
                    DrainLocked(now);
                    // What landed is read now: the next change of any
                    // control is the next read's.
                    foreach (int k in _touchedList) _touched[k] = false;
                    _touchedList.Clear();
                    src = _state;
                }
            }
            var dst = _pool.Next();
            src.CopyInto(dst);
            return dst;
        }

        /// <summary>The state worked out on the poll thread at each read
        /// (#600 item 19): the Test Controller's motion, as fine as the
        /// polling rate. Null reads the state last published. Set before
        /// the device is registered.</summary>
        public Func<CustomInputState> Live { get; set; }

        /// <summary>The state as published now, for a reader off the poll
        /// thread that only reads it.</summary>
        public CustomInputState PeekState() => Volatile.Read(ref _state);

        /// <summary>Each declared control under the name the program gave:
        /// the axes, then the sliders, the buttons and the hats.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var shape = Shape;
            var items = new List<DeviceObjectItem>(shape.Axes.Count + shape.Buttons.Count + shape.Hats.Count);
            foreach (var axis in shape.Axes)
            {
                items.Add(axis.IsSlider
                    ? new DeviceObjectItem
                    {
                        InputIndex = axis.Index - ProgramAxis.FirstSlider,
                        ObjectTypeGuid = ObjectGuid.Slider,
                        Name = axis.Name,
                        ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                        Offset = axis.Index * 4,
                    }
                    : new DeviceObjectItem
                    {
                        InputIndex = axis.Index,
                        // Any axis GUID that is not the slider's: only
                        // IsSlider is read from it.
                        ObjectTypeGuid = axis.Index < AxisGuids.Length ? AxisGuids[axis.Index] : ObjectGuid.ZAxis,
                        Name = axis.Name,
                        ObjectType = DeviceObjectTypeFlags.AbsoluteAxis,
                        Offset = axis.Index * 4,
                    });
            }
            foreach (var (index, name) in shape.Buttons)
                items.Add(new DeviceObjectItem
                {
                    InputIndex = index,
                    ObjectTypeGuid = ObjectGuid.Button,
                    Name = name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    Offset = (ProgramAxis.End + index) * 4,
                });
            for (int i = 0; i < shape.Hats.Count; i++)
                items.Add(new DeviceObjectItem
                {
                    InputIndex = i,
                    ObjectTypeGuid = ObjectGuid.PovController,
                    Name = shape.Hats[i],
                    ObjectType = DeviceObjectTypeFlags.PointOfViewController,
                    Offset = (ProgramAxis.End + CustomInputState.MaxButtons + i) * 4,
                });
            return items.ToArray();
        }

        private static readonly Guid[] AxisGuids =
        {
            ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis,
            ObjectGuid.RxAxis, ObjectGuid.RyAxis, ObjectGuid.RzAxis,
        };

        public int GetInputDeviceType() => Shape.DeviceType;

        // ── Feedback ──

        /// <summary>The game's rumble, low and high motor. Raised on the
        /// poll thread when it changes.</summary>
        public event Action<ushort, ushort> RumbleRequested;

        /// <summary>The trigger motors, left and right.</summary>
        public event Action<ushort, ushort> TriggerRumbleRequested;

        /// <summary>The light's color.</summary>
        public event Action<byte, byte, byte> LedChanged;

        /// <summary>The player number, from 1.</summary>
        public event Action<int> PlayerChanged;

        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue)
        {
            if (!Shape.Rumble) return false;
            long packed = ((long)low << 16) | high;
            if (Interlocked.Exchange(ref _rumble, packed) != packed) RumbleRequested?.Invoke(low, high);
            return true;
        }

        public bool StopRumble() => SetRumble(0, 0);

        /// <summary>Takes the trigger motors itself: the device has no
        /// gamepad handle, and the program plays them.</summary>
        public bool SetTriggerRumble(ushort left, ushort right, uint durationMs = uint.MaxValue)
        {
            if (!Shape.TriggerRumble) return false;
            long packed = ((long)left << 16) | right;
            if (Interlocked.Exchange(ref _triggerRumble, packed) != packed) TriggerRumbleRequested?.Invoke(left, right);
            return true;
        }

        /// <summary>The trigger effects, left then right, 11 bytes each, as
        /// the game wrote them to the slot's DualSense (#600 item 18). Raised
        /// on the poll thread when they change.</summary>
        public event Action<byte[], byte[]> TriggerEffectsRequested;

        private byte[] _triggerEffects;

        /// <summary>Takes the slot's trigger effects and passes on a change.
        /// Called on the poll thread each pass, so an unchanged pair costs a
        /// compare and nothing more.</summary>
        public bool SetTriggerEffects(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (!Shape.TriggerEffects || left.Length < 11 || right.Length < 11) return false;
            var last = Volatile.Read(ref _triggerEffects);
            if (last != null && left.Slice(0, 11).SequenceEqual(last.AsSpan(0, 11)) && right.Slice(0, 11).SequenceEqual(last.AsSpan(11, 11)))
                return true;
            var both = new byte[22];
            left.Slice(0, 11).CopyTo(both);
            right.Slice(0, 11).CopyTo(both.AsSpan(11));
            Volatile.Write(ref _triggerEffects, both);
            TriggerEffectsRequested?.Invoke(both.AsSpan(0, 11).ToArray(), both.AsSpan(11, 11).ToArray());
            return true;
        }

        /// <summary>The trigger effects last passed on, or null.</summary>
        public (byte[] Left, byte[] Right)? LastTriggerEffects
        {
            get
            {
                var both = Volatile.Read(ref _triggerEffects);
                return both == null ? null : (both.AsSpan(0, 11).ToArray(), both.AsSpan(11, 11).ToArray());
            }
        }

        public bool HasLightbar => Shape.Light;
        public bool LightbarIsDs4 => false;
        public bool HasIndicatorLeds => false;

        public void SetLed(byte r, byte g, byte b)
        {
            if (!Shape.Light) return;
            int packed = (r << 16) | (g << 8) | b;
            if (Interlocked.Exchange(ref _led, packed) != packed) LedChanged?.Invoke(r, g, b);
        }

        public void SetPlayerNumber(int number)
        {
            if (!Shape.Player || number <= 0) return;
            if (Interlocked.Exchange(ref _player, number) != number) PlayerChanged?.Invoke(number);
        }

        /// <summary>The feedback last passed on, each null until the game
        /// or PadForge first set it.</summary>
        public (ushort Low, ushort High)? LastRumble => Unpack(Volatile.Read(ref _rumble));
        public (ushort Left, ushort Right)? LastTriggerRumble => Unpack(Volatile.Read(ref _triggerRumble));
        public (byte R, byte G, byte B)? LastLed
        {
            get
            {
                int led = Volatile.Read(ref _led);
                return led < 0 ? null : ((byte)(led >> 16), (byte)(led >> 8), (byte)led);
            }
        }
        public int? LastPlayer
        {
            get
            {
                int player = Volatile.Read(ref _player);
                return player <= 0 ? null : player;
            }
        }

        private static (ushort, ushort)? Unpack(long packed)
            => packed < 0 ? null : ((ushort)(packed >> 16), (ushort)packed);

        public void Dispose()
        {
            _attached = false;
        }
    }
}
