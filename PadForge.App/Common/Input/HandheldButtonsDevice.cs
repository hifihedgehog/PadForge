using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PadForge.Engine;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The per-machine "hidden buttons" device row for handheld PCs
    /// (issue #343), the NFC reader's shape: a synthetic
    /// <see cref="ISdlInputDevice"/> whose buttons are the entries of
    /// <see cref="HandheldButtonRegistry"/>, each at its stable index.
    ///
    /// <para>Two delivery paths feed it. Chord buttons come from
    /// <see cref="HandheldChordRuntime.Engine"/>, which the low-level hooks
    /// feed. Report buttons come from the vendor HID collections this
    /// device keeps open: exactly the ones a definition names, or every
    /// present one while a Learn dialog is capturing. A press asserts the
    /// button for at least <see cref="PulseMs"/> (a firmware chord is down
    /// and up within milliseconds, and a macro poll must still catch the
    /// edge), and a Value-kind report button releases
    /// <see cref="VendorReportLearner.ValueHoldMs"/> after its last
    /// matching report, since event-style firmware sends no release.</para>
    /// </summary>
    internal sealed class HandheldButtonsDevice : ISdlInputDevice
    {
        private const ushort HandheldVendorId = 0x4850;  // "HP"
        private const ushort HandheldProductId = 0x4842; // "HB"
        private const int PulseMs = 175;

        private readonly object _stateLock = new();
        private readonly CustomInputState _state = new();
        private volatile bool _attached;
        private volatile bool _disposed;

        private readonly bool[] _chordDown = new bool[CustomInputState.MaxButtons];
        private readonly bool[] _reportDown = new bool[CustomInputState.MaxButtons];
        private readonly long[] _pulseUntil = new long[CustomInputState.MaxButtons];
        private readonly long[] _valueUntil = new long[CustomInputState.MaxButtons];

        // Report definitions by collection key, rebuilt on registry change.
        private volatile Dictionary<string, VendorButtonDefinition[]> _reportDefs =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object _readersLock = new();
        private readonly Dictionary<string, VendorHidReader> _readers = new(StringComparer.OrdinalIgnoreCase);

        private volatile HandheldLearnSession _learn;

        /// <summary>The open row, for the Learn dialog (NfcReaderService's
        /// Active pattern). Null while the feature is off.</summary>
        public static HandheldButtonsDevice Active { get; private set; }

        private Action<int, bool> _buttonHandler;
        private Action<int[]> _captureHandler;
        private EventHandler _registryHandler;

        public HandheldButtonsDevice(MachineIdentity machine)
        {
            string key = machine?.Key ?? string.Empty;
            Name = (machine?.DisplayName ?? "This PC") + " Hidden Buttons";
            DevicePath = "handheld://" + key.ToLowerInvariant();
            InstanceGuid = Md5Guid("pfhandheld:" + key);
            ProductGuid = Md5Guid("pfhandheld-product");
            SdlInstanceId = SyntheticInstanceId.From(DevicePath);
        }

        // Span the RANGE of stable registry buttons, not the count, so a
        // removed middle entry leaves a gap instead of renumbering.
        private static int ButtonSpan => 1 + HandheldButtonRegistry.MaxButtonInUse;

        // ─── ISdlInputDevice identity / capabilities ───
        public uint SdlInstanceId { get; }
        public string Name { get; }
        public int NumAxes => 0;
        public int NumButtons => ButtonSpan;
        public int RawButtonCount => ButtonSpan;
        public int NumHats => 0;
        public int[] SupportedButtonIndices
        {
            get
            {
                var list = new List<int>();
                foreach (var e in HandheldButtonRegistry.Entries) list.Add(e.Button);
                return list.ToArray();
            }
        }
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
        public ushort VendorId => HandheldVendorId;
        public ushort ProductId => HandheldProductId;
        public Guid InstanceGuid { get; }
        public Guid ProductGuid { get; }
        public string DevicePath { get; }
        public string SerialNumber => string.Empty;
        public string SdlGuid => string.Empty;

        public int GetInputDeviceType() => InputDeviceType.HandheldButtons;
        public bool SetRumble(ushort low, ushort high, uint durationMs = uint.MaxValue) => false;
        public bool StopRumble() => false;

        /// <summary>The picker lists every learned button by its name, each
        /// at its stable raw index.</summary>
        public DeviceObjectItem[] GetDeviceObjects()
        {
            var entries = HandheldButtonRegistry.Entries;
            var items = new DeviceObjectItem[entries.Count];
            for (int i = 0; i < entries.Count; i++)
                items[i] = new DeviceObjectItem
                {
                    Name = entries[i].Name,
                    ObjectType = DeviceObjectTypeFlags.PushButton,
                    ObjectTypeGuid = ObjectGuid.Button,
                    InputIndex = entries[i].Button,
                };
            return items;
        }

        // ─── Lifecycle ───

        public bool Open()
        {
            if (_disposed) return false;
            HandheldChordRuntime.Start();
            _buttonHandler = OnChordButton;
            _captureHandler = OnChordCaptured;
            _registryHandler = (s, e) => ApplyRegistry();
            HandheldChordRuntime.Engine.ButtonChanged += _buttonHandler;
            HandheldChordRuntime.Engine.CaptureCompleted += _captureHandler;
            HandheldButtonRegistry.RegistryChanged += _registryHandler;
            ApplyRegistry();
            _attached = true;
            Active = this;
            // The chords just reached the engine; the hook host decides
            // on hooks from that, so tell it now rather than at the next
            // device change.
            HandheldButtonRegistry.NotifyActivity();
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            if (ReferenceEquals(Active, this)) Active = null;
            if (_buttonHandler != null) HandheldChordRuntime.Engine.ButtonChanged -= _buttonHandler;
            if (_captureHandler != null) HandheldChordRuntime.Engine.CaptureCompleted -= _captureHandler;
            if (_registryHandler != null) HandheldButtonRegistry.RegistryChanged -= _registryHandler;
            _buttonHandler = null;
            _captureHandler = null;
            _registryHandler = null;
            HandheldChordRuntime.Engine.SetChords(null);
            CloseAllReaders();
            HandheldButtonRegistry.NotifyActivity();
        }

        /// <summary>Pushes the registry into the chord engine and the report
        /// evaluator. Buttons no longer defined release at once.</summary>
        private void ApplyRegistry()
        {
            var entries = HandheldButtonRegistry.Entries;
            HandheldChordRuntime.Engine.SetChords(HandheldButtonRegistry.Chords);
            var defs = new Dictionary<string, List<VendorButtonDefinition>>(StringComparer.OrdinalIgnoreCase);
            var defined = new bool[CustomInputState.MaxButtons];
            foreach (var e in entries)
            {
                if (e.Button >= 0 && e.Button < defined.Length) defined[e.Button] = true;
                if (!e.HasReport) continue;
                if (!defs.TryGetValue(e.Collection, out var list)) defs[e.Collection] = list = new List<VendorButtonDefinition>();
                list.Add(e.ToReport());
            }
            var frozen = new Dictionary<string, VendorButtonDefinition[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in defs) frozen[kv.Key] = kv.Value.ToArray();
            _reportDefs = frozen;
            lock (_stateLock)
            {
                for (int b = 0; b < defined.Length; b++)
                {
                    if (defined[b]) continue;
                    _chordDown[b] = false;
                    _reportDown[b] = false;
                    _pulseUntil[b] = 0;
                    _valueUntil[b] = 0;
                }
            }
        }

        private void OnChordButton(int button, bool down)
        {
            if (button < 0 || button >= _chordDown.Length) return;
            lock (_stateLock)
            {
                _chordDown[button] = down;
                if (down) _pulseUntil[button] = Environment.TickCount64 + PulseMs;
            }
        }

        private void OnChordCaptured(int[] keys)
        {
            _learn?.OnChordCaptured(keys);
        }

        // ─── Vendor readers (sweep worker) ───

        /// <summary>Opens the collections the definitions name (or every
        /// present one during a capture) and closes the rest, plus any
        /// reader whose thread died. Blocking opens: worker thread only.</summary>
        internal void SyncReaders(List<VendorHidCollection> present)
        {
            if (_disposed || present == null) return;
            bool captureAll = HandheldButtonRegistry.LearnCaptureActive;
            var wanted = HandheldButtonRegistry.RequiredCollections;
            var byKey = new Dictionary<string, VendorHidCollection>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in present)
            {
                if (!byKey.ContainsKey(c.Key)) byKey[c.Key] = c;
                if (captureAll) wanted.Add(c.Key);
            }

            lock (_readersLock)
            {
                if (_disposed) return;
                List<string> gone = null;
                foreach (var kv in _readers)
                {
                    bool unwanted = !wanted.Contains(kv.Key);
                    bool dead = !kv.Value.IsAttached;
                    bool vanished = !byKey.TryGetValue(kv.Key, out var c)
                        || !string.Equals(c.Path, kv.Value.Collection.Path, StringComparison.OrdinalIgnoreCase);
                    if (unwanted || dead || vanished) (gone ??= new List<string>()).Add(kv.Key);
                }
                if (gone != null)
                    foreach (var key in gone)
                    {
                        var r = _readers[key];
                        _readers.Remove(key);
                        r.ReportReceived -= OnReport;
                        r.Dispose();
                    }

                foreach (var key in wanted)
                {
                    if (_readers.ContainsKey(key)) continue;
                    if (!byKey.TryGetValue(key, out var c)) continue;
                    var reader = new VendorHidReader(c);
                    reader.ReportReceived += OnReport;
                    if (!reader.Open())
                    {
                        reader.ReportReceived -= OnReport;
                        reader.Dispose();
                        PadForge.Engine.SdlDiagLog.WriteLine($"Handheld: could not open vendor collection {c.Key} ({c.Name})");
                        continue;
                    }
                    _readers[key] = reader;
                    PadForge.Engine.SdlDiagLog.WriteLine($"Handheld: reading vendor collection {c.Key} ({c.Name}), {c.InputReportLength}-byte reports");
                }
            }
        }

        /// <summary>Enumerates and syncs off the caller's thread right away,
        /// so a Learn dialog does not wait out the sweep cadence for the
        /// vendor collections to open.</summary>
        internal void SyncReadersNow()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var present = VendorHidRuntime.Enumerate();
                    if (present != null) SyncReaders(present);
                }
                catch { }
            });
        }

        private void CloseAllReaders()
        {
            lock (_readersLock)
            {
                foreach (var kv in _readers)
                {
                    kv.Value.ReportReceived -= OnReport;
                    kv.Value.Dispose();
                }
                _readers.Clear();
            }
        }

        /// <summary>Open collection keys, for the details pane.</summary>
        internal string[] OpenCollections
        {
            get { lock (_readersLock) { var a = new string[_readers.Count]; _readers.Keys.CopyTo(a, 0); return a; } }
        }

        private void OnReport(VendorHidReader reader, byte[] buffer, int length)
        {
            var learn = _learn;
            if (learn != null)
                learn.OnReport(reader.Collection.Key, reader.Collection.Name, buffer, length);

            if (!_reportDefs.TryGetValue(reader.Collection.Key, out var defs)) return;
            var span = new ReadOnlySpan<byte>(buffer, 0, length);
            long now = Environment.TickCount64;
            lock (_stateLock)
            {
                foreach (var d in defs)
                {
                    if (d.Button < 0 || d.Button >= _reportDown.Length) continue;
                    // A definition for another report id says nothing about
                    // this report; leave its state alone.
                    if (d.ReportId != 0 && length > 0 && buffer[0] != d.ReportId) continue;
                    bool hit = d.Evaluate(span);
                    if (d.Kind == VendorButtonKind.Bit)
                    {
                        if (hit && !_reportDown[d.Button]) _pulseUntil[d.Button] = now + PulseMs;
                        _reportDown[d.Button] = hit;
                    }
                    else if (hit)
                    {
                        _valueUntil[d.Button] = now + VendorReportLearner.ValueHoldMs;
                    }
                }
            }
        }

        // ─── Learn ───

        internal void BeginLearn(HandheldLearnSession session)
        {
            _learn = session;
            HandheldChordRuntime.Engine.BeginCapture(Environment.TickCount64);
        }

        internal void EndLearn()
        {
            _learn = null;
            HandheldChordRuntime.Engine.CancelCapture();
        }

        // ─── State read (poll thread) ───

        private PooledInputStatePair _statePool;

        public CustomInputState GetCurrentState(bool forceRaw = false)
        {
            if (_disposed || !_attached) return null;
            lock (_stateLock)
            {
                long now = Environment.TickCount64;
                var s = _statePool.Next();
                _state.CopyInto(s);
                int n = Math.Min(ButtonSpan, s.Buttons.Length);
                for (int b = 0; b < s.Buttons.Length; b++)
                {
                    bool pressed = b < n && (_chordDown[b] || _reportDown[b]
                        || (_pulseUntil[b] != 0 && now < _pulseUntil[b])
                        || (_valueUntil[b] != 0 && now < _valueUntil[b]));
                    if (!pressed) { _pulseUntil[b] = 0; _valueUntil[b] = 0; }
                    // Written EVERY poll, true or false, so a released
                    // button produces its falling edge (the NFC lesson).
                    s.Buttons[b] = pressed;
                }
                return s;
            }
        }

        /// <summary>Test seam: a report as the reader would deliver it.</summary>
        internal void InjectReportForTest(string collectionKey, byte[] report)
        {
            var c = new VendorHidCollection { Key = collectionKey, Name = collectionKey, Path = collectionKey, InputReportLength = (ushort)report.Length };
            OnReport(new VendorHidReader(c), report, report.Length);
        }

        /// <summary>Test seam: mark live without hooks or readers.</summary>
        internal void AttachForTest()
        {
            _registryHandler = (s, e) => ApplyRegistry();
            HandheldButtonRegistry.RegistryChanged += _registryHandler;
            ApplyRegistry();
            _attached = true;
        }

        private static Guid Md5Guid(string identifier)
        {
            using var md5 = MD5.Create();
            return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(identifier)));
        }
    }
}
