using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HIDMaestro;
using PadForge.Engine.Common;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Where a Keyboard + Mouse slot's virtual HID keyboard or mouse sends its
    /// reports: a HIDMaestro controller in the app, a recorder in the tests.
    /// </summary>
    internal interface IKbmHidSink : IDisposable
    {
        void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys);
        void SubmitConsumer(ushort[] usages);
        void SubmitMouse(in HMMouseState state);

        /// <summary>The LEDs Windows last set, or null before it set
        /// any.</summary>
        HMKeyboardLeds? KeyboardLeds { get; }

        /// <summary>Raised on the SDK's output thread when Windows sets the
        /// LEDs.</summary>
        event Action<HMKeyboardLeds> KeyboardLedsChanged;
    }

    /// <summary>The app's sink: one HIDMaestro keyboard or mouse controller
    /// (HIDMaestro 1.12.0, HMController.SubmitKeyboard, SubmitConsumer and
    /// SubmitMouse).</summary>
    internal sealed class HMControllerSink : IKbmHidSink
    {
        private readonly HMController _controller;

        public HMControllerSink(HMController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _controller.KeyboardLedsChanged += OnLeds;
        }

        public void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys) => _controller.SubmitKeyboard(modifiers, keys);
        public void SubmitConsumer(ushort[] usages) => _controller.SubmitConsumer(usages);
        public void SubmitMouse(in HMMouseState state) => _controller.SubmitMouse(in state);
        public HMKeyboardLeds? KeyboardLeds => _controller.KeyboardLeds;
        public event Action<HMKeyboardLeds> KeyboardLedsChanged;

        private void OnLeds(HMController c, HMKeyboardLeds leds) => KeyboardLedsChanged?.Invoke(leds);

        public void Dispose()
        {
            _controller.KeyboardLedsChanged -= OnLeds;
            // HMController.Dispose lets go of every key and button the device
            // holds before the device goes, as unplugging it would.
            _controller.Dispose();
        }
    }

    /// <summary>One device that is both the slot's keyboard and its mouse,
    /// such as a keyboard with a TrackPoint. Each half holds a reference, and
    /// the device goes when the second lets go. HMController is thread-safe,
    /// so the two senders share it as they are.</summary>
    internal sealed class SharedKbmHidSink : IKbmHidSink
    {
        private readonly IKbmHidSink _inner;
        private int _users;

        public SharedKbmHidSink(IKbmHidSink inner, int users)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _users = users;
        }

        public void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys) => _inner.SubmitKeyboard(modifiers, keys);
        public void SubmitConsumer(ushort[] usages) => _inner.SubmitConsumer(usages);
        public void SubmitMouse(in HMMouseState state) => _inner.SubmitMouse(in state);
        public HMKeyboardLeds? KeyboardLeds => _inner.KeyboardLeds;

        public event Action<HMKeyboardLeds> KeyboardLedsChanged
        {
            add => _inner.KeyboardLedsChanged += value;
            remove => _inner.KeyboardLedsChanged -= value;
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _users) == 0) _inner.Dispose();
        }
    }

    /// <summary>
    /// A virtual HID keyboard a Keyboard + Mouse slot types through, in place
    /// of SendInput (HIDMaestro 1.12.0, HIDMaestro#67).
    ///
    /// <para>The poll thread hands it the slot's keys and never waits.
    /// HIDMaestro holds each keyboard report until the driver says HID class
    /// took the one before, and spaces reports a millisecond apart
    /// (HMController.AwaitPreviousDelivery), so a submit can block for up to
    /// 200 ms while Windows is not reading the device. A thread of its own
    /// sends, in order, every state the slot's keys passed through, so a tap
    /// shorter than a report interval still presses and releases. The same
    /// split as the SendInput mouse lane, whose hook-chain cost runs on the
    /// injector thread.</para>
    ///
    /// <para>Each key is resolved once, when it goes down
    /// (<see cref="KbmHidKeyMap.Resolve(int)"/>), and released as that same
    /// usage, so a layout switch while a key is held cannot strand it. A key
    /// the device's report cannot carry (<see cref="KbmDeviceCaps"/>) is not
    /// pressed. Past the slots of a key array, a key waits until one frees:
    /// HIDMaestro answers too many keys with ErrorRollOver in every slot, as
    /// a boot keyboard does, and Windows then changes nothing, so holding the
    /// newest key back is what Windows would show anyway, and the hooks get an
    /// exact account of every key the device reports.</para>
    /// </summary>
    internal sealed class KbmHidKeyboard : IDisposable
    {
        private readonly IKbmHidSink _sink;
        private readonly KbmDeviceCaps _caps;
        private readonly Func<int, KbmHidKeyMap.HidKey> _resolve;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<Frame> _queue = new();
        private volatile bool _stop;
        private int _disposed;

        // Poll-thread state: the usage each key index pressed, and the
        // usages in the order they went down, each with how many keys hold it.
        private readonly KbmHidKeyMap.HidKey[] _pressedAs = new KbmHidKeyMap.HidKey[256];
        private ulong _k0, _k1, _k2, _k3;
        private readonly List<int> _wantOrder = new();
        private readonly Dictionary<int, int> _wantCount = new();
        private Entry[] _lastKeyboard = Array.Empty<Entry>();
        private Entry[] _lastConsumer = Array.Empty<Entry>();

        // Sender-thread state: what the device was last told it holds, each
        // usage with the key index its press was recorded under for the hooks.
        private readonly Dictionary<int, Entry> _sent = new();

        private readonly object _ledLock = new();
        private HMKeyboardLeds? _leds;
        private bool _ledsFromEvent;

        /// <summary>Frames waiting past this many collapse into the newest
        /// state: Windows has stopped reading the device, and replaying a long
        /// backlog later would type into whatever has the focus by then.</summary>
        internal const int MaxQueuedFrames = 128;

        /// <summary>A usage as page &lt;&lt; 16 | usage, and the lowest key
        /// index holding it.</summary>
        private readonly struct Entry
        {
            public readonly int Key;
            public readonly int Index;
            public Entry(int key, int index) { Key = key; Index = index; }
        }

        /// <summary>The whole keyboard or consumer state of one report.</summary>
        private sealed class Frame
        {
            public bool Consumer;
            public Entry[] Entries;
        }

        public string ProfileId { get; }

        /// <summary>The LEDs Windows last set on the keyboard, or null before
        /// it set any.</summary>
        public HMKeyboardLeds? Leds { get { lock (_ledLock) return _leds; } }

        public KbmHidKeyboard(IKbmHidSink sink, KbmDeviceCaps caps, string profileId, int padIndex,
            Func<int, KbmHidKeyMap.HidKey> resolve = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _caps = caps ?? new KbmDeviceCaps();
            _resolve = resolve ?? KbmHidKeyMap.Resolve;
            ProfileId = profileId ?? string.Empty;
            // Attach, then read: Windows sends a new keyboard its LED state
            // while CreateController runs, so the state can land before any
            // handler is attached, and one of the two has it
            // (HMController.KeyboardLeds). An event that lands between the two
            // is newer than the read and keeps its value.
            _sink.KeyboardLedsChanged += OnLeds;
            var current = _sink.KeyboardLeds;
            lock (_ledLock)
                if (!_ledsFromEvent) _leds = current;
            _thread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = $"PadForge KBM keyboard pad {padIndex + 1}",
            };
            _thread.Start();
        }

        private void OnLeds(HMKeyboardLeds leds)
        {
            lock (_ledLock)
            {
                _leds = leds;
                _ledsFromEvent = true;
            }
        }

        private static int KeyOf(KbmHidKeyMap.HidKey k) => (k.Page << 16) | k.Usage;
        private static ushort PageOf(int key) => (ushort)(key >> 16);
        private static ushort UsageOf(int key) => (ushort)key;
        private static int ScanKeyOf(int key) => KbmHidKeyMap.ScanKeyOf(new KbmHidKeyMap.HidKey(PageOf(key), UsageOf(key)));

        /// <summary>Poll thread: the slot's keys after SOCD, as the
        /// KbmRawState words carry them.</summary>
        public void Submit(ulong k0, ulong k1, ulong k2, ulong k3)
        {
            if (k0 == _k0 && k1 == _k1 && k2 == _k2 && k3 == _k3) return;
            bool changed = Apply(ref _k0, k0, 0);
            changed |= Apply(ref _k1, k1, 64);
            changed |= Apply(ref _k2, k2, 128);
            changed |= Apply(ref _k3, k3, 192);
            if (changed) Publish();
        }

        /// <summary>Poll thread: lets go of every key.</summary>
        public void ReleaseAll() => Submit(0, 0, 0, 0);

        /// <summary>Records the keys that went down or up in one word. True
        /// when a usage the device can send was pressed or released.</summary>
        private bool Apply(ref ulong held, ulong now, int baseIndex)
        {
            ulong changed = held ^ now;
            held = now;
            if (changed == 0) return false;
            bool any = false;
            for (int bit = 0; bit < 64; bit++)
            {
                ulong mask = 1UL << bit;
                if ((changed & mask) == 0) continue;
                int index = baseIndex + bit;
                if ((now & mask) == 0)
                {
                    var was = _pressedAs[index];
                    _pressedAs[index] = KbmHidKeyMap.None;
                    if (was.IsNone) continue;
                    int key = KeyOf(was);
                    if (_wantCount.TryGetValue(key, out int n) && n > 1) _wantCount[key] = n - 1;
                    else
                    {
                        _wantCount.Remove(key);
                        _wantOrder.Remove(key);
                    }
                    any = true;
                }
                else
                {
                    var usage = _resolve(index);
                    bool sendable = usage.IsKeyboard ? usage.Usage <= 0xFF && _caps.CanSendKey((byte)usage.Usage)
                        : usage.IsConsumer && _caps.CanSendConsumer(usage.Usage);
                    if (!sendable) continue;
                    _pressedAs[index] = usage;
                    int key = KeyOf(usage);
                    if (_wantCount.TryGetValue(key, out int n)) _wantCount[key] = n + 1;
                    else
                    {
                        _wantCount[key] = 1;
                        _wantOrder.Add(key);
                    }
                    any = true;
                }
            }
            return any;
        }

        /// <summary>The lowest key index that holds <paramref name="key"/>,
        /// which the hooks see the key under.</summary>
        private int HoldingIndex(int key)
        {
            for (int i = 0; i < 256; i++)
                if (!_pressedAs[i].IsNone && KeyOf(_pressedAs[i]) == key) return i;
            return -1;
        }

        private void Publish()
        {
            var keyboard = new List<Entry>(8);
            var consumer = new List<Entry>(2);
            int keySlots = 0, consumerSlots = 0;
            foreach (int key in _wantOrder)
            {
                ushort usage = UsageOf(key);
                if (PageOf(key) == 0x07)
                {
                    if (!_caps.IsKeyBit((byte)usage))
                    {
                        if (keySlots >= _caps.KeyArraySlots) continue;
                        keySlots++;
                    }
                    keyboard.Add(new Entry(key, HoldingIndex(key)));
                }
                else
                {
                    if (!_caps.IsConsumerBit(usage))
                    {
                        if (consumerSlots >= _caps.ConsumerArraySlots) continue;
                        consumerSlots++;
                    }
                    consumer.Add(new Entry(key, HoldingIndex(key)));
                }
            }

            bool queued = false;
            if (!SameUsages(keyboard, _lastKeyboard))
            {
                _lastKeyboard = keyboard.ToArray();
                Enqueue(new Frame { Consumer = false, Entries = _lastKeyboard });
                queued = true;
            }
            if (!SameUsages(consumer, _lastConsumer))
            {
                _lastConsumer = consumer.ToArray();
                Enqueue(new Frame { Consumer = true, Entries = _lastConsumer });
                queued = true;
            }
            if (queued) _wake.Set();
        }

        private static bool SameUsages(List<Entry> a, Entry[] b)
        {
            if (a.Count != b.Length) return false;
            foreach (var x in a)
            {
                bool found = false;
                foreach (var y in b) if (y.Key == x.Key) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>Poll thread. Each frame carries a whole report, so a
        /// backlog collapses to the newest keyboard frame and the newest
        /// consumer frame without losing where the keys end up.</summary>
        private void Enqueue(Frame f)
        {
            if (_queue.Count >= MaxQueuedFrames)
            {
                Frame keyboard = null, consumer = null;
                while (_queue.TryDequeue(out var old))
                {
                    if (old.Consumer) consumer = old;
                    else keyboard = old;
                }
                if (f.Consumer) consumer = null;
                else keyboard = null;
                if (keyboard != null) _queue.Enqueue(keyboard);
                if (consumer != null) _queue.Enqueue(consumer);
            }
            _queue.Enqueue(f);
        }

        private void SendLoop()
        {
            try { SendFrames(); }
            catch (ObjectDisposedException) { /* disposed while it waited */ }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"KBMHID keyboard {ProfileId} sender stopped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SendFrames()
        {
            while (!_stop)
            {
                // A key held while a program drives PadForge is checked
                // against the window in front often, so it is let go before
                // Windows repeats it into a window with rights.
                _wake.WaitOne(SentAny && ProgramInputGate.InForce ? GateCheckMs : 250);
                while (!_stop && _queue.TryDequeue(out var f))
                {
                    if (f.Consumer) _wantConsumer = f;
                    else _wantKeyboard = f;
                    Send(ProgramInputGate.KeysBlocked ? Nothing(f.Consumer) : f);
                }
                if (!_stop) Reconcile();
            }
        }

        /// <summary>How often a held key is checked against the window in
        /// front while a program drives PadForge, in milliseconds. Windows
        /// starts repeating a held key after 250 at the shortest.</summary>
        internal const int GateCheckMs = 50;

        // Sender-thread state: the last whole state of each kind the slot
        // asked for, which the device takes again once the gate opens.
        private Frame _wantKeyboard, _wantConsumer;

        private bool SentAny
        {
            get { lock (_sent) return _sent.Count > 0; }
        }

        private static Frame Nothing(bool consumer) => new Frame { Consumer = consumer, Entries = Array.Empty<Entry>() };

        /// <summary>Sender thread: while the gate holds keys back the device
        /// holds none, and once it opens the device holds what the slot asks
        /// for again.</summary>
        private void Reconcile()
        {
            bool blocked = ProgramInputGate.KeysBlocked;
            Send(blocked ? Nothing(false) : _wantKeyboard ?? Nothing(false));
            Send(blocked ? Nothing(true) : _wantConsumer ?? Nothing(true));
        }

        /// <summary>Sender thread: one report, when it differs from what
        /// the device holds of its kind.</summary>
        private void Send(Frame f)
        {
            if (Holds(f)) return;
            // The hooks learn each press and release before the report
            // that carries it can reach them.
            NoteChanges(f);
            try
            {
                if (f.Consumer)
                {
                    var usages = new ushort[f.Entries.Length];
                    for (int i = 0; i < usages.Length; i++) usages[i] = UsageOf(f.Entries[i].Key);
                    _sink.SubmitConsumer(usages);
                }
                else
                {
                    byte modifiers = 0;
                    var keys = new List<HMKey>(f.Entries.Length);
                    foreach (var e in f.Entries)
                    {
                        ushort u = UsageOf(e.Key);
                        if (u >= 0xE0 && u <= 0xE7) modifiers |= (byte)(1 << (u - 0xE0));
                        else keys.Add((HMKey)u);
                    }
                    _sink.SubmitKeyboard((HMKeyModifiers)modifiers, keys.ToArray());
                }
            }
            catch (ObjectDisposedException) { _stop = true; }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"KBMHID keyboard {ProfileId} submit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Whether the device holds exactly the usages of
        /// <paramref name="f"/> for its kind already.</summary>
        private bool Holds(Frame f)
        {
            lock (_sent)
            {
                int held = 0;
                foreach (var kv in _sent)
                    if ((PageOf(kv.Key) == 0x0C) == f.Consumer) held++;
                if (held != f.Entries.Length) return false;
                foreach (var e in f.Entries)
                    if (!_sent.ContainsKey(e.Key)) return false;
                return true;
            }
        }

        /// <summary>Sender thread: tells the hooks which usages of the frame's
        /// kind go up and down against what the device last held.</summary>
        private void NoteChanges(Frame f)
        {
            lock (_sent)
            {
                List<int> gone = null;
                foreach (var kv in _sent)
                {
                    if ((PageOf(kv.Key) == 0x0C) != f.Consumer) continue;
                    bool kept = false;
                    foreach (var e in f.Entries) if (e.Key == kv.Key) { kept = true; break; }
                    if (!kept) (gone ??= new()).Add(kv.Key);
                }
                if (gone != null)
                {
                    foreach (int key in gone)
                    {
                        OwnHidOutput.NoteKeyUp(_sent[key].Index, ScanKeyOf(key));
                        _sent.Remove(key);
                    }
                }
                foreach (var e in f.Entries)
                {
                    if (_sent.ContainsKey(e.Key)) continue;
                    OwnHidOutput.NoteKeyDown(e.Index, ScanKeyOf(e.Key));
                    _sent[e.Key] = e;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop = true;
            _wake.Set();
            bool stopped = _thread.Join(1000);
            _sink.KeyboardLedsChanged -= OnLeds;
            // Disposing the sink lets go of every key the device holds. The
            // hooks hear about those releases first, so they pass as
            // PadForge's. Frames still queued never reached the device and
            // have nothing to release.
            lock (_sent)
            {
                foreach (var kv in _sent) OwnHidOutput.NoteKeyUp(kv.Value.Index, ScanKeyOf(kv.Key));
                _sent.Clear();
            }
            try { _sink.Dispose(); } catch { /* best effort */ }
            // A sender still inside a submit wakes once more on its way out.
            if (stopped) _wake.Dispose();
        }
    }

    /// <summary>
    /// A virtual HID mouse a Keyboard + Mouse slot points through, the twin of
    /// <see cref="KbmHidKeyboard"/>. Movement and wheel turns add up between
    /// reports, so none is lost. Button changes queue in order, so a click
    /// shorter than a report interval still clicks.
    /// </summary>
    internal sealed class KbmHidMouse : IDisposable
    {
        private readonly IKbmHidSink _sink;
        private readonly KbmDeviceCaps _caps;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<byte> _buttonStates = new();
        private volatile bool _stop;
        private int _disposed;
        private int _armed;

        private int _dx, _dy, _wheel, _pan;
        private byte _lastButtons;      // poll thread
        private byte _sentButtons;      // sender thread, under _buttonLock
        private readonly object _buttonLock = new();

        /// <summary>Button changes waiting past this many collapse to the
        /// newest, for the reason <see cref="KbmHidKeyboard.MaxQueuedFrames"/>
        /// gives.</summary>
        internal const int MaxQueuedButtonStates = 128;

        public string ProfileId { get; }

        public KbmHidMouse(IKbmHidSink sink, KbmDeviceCaps caps, string profileId, int padIndex)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _caps = caps ?? new KbmDeviceCaps();
            ProfileId = profileId ?? string.Empty;
            _thread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = $"PadForge KBM mouse pad {padIndex + 1}",
            };
            _thread.Start();
        }

        /// <summary>The KbmRawState button bits (0 left, 1 right, 2 middle,
        /// 3 back, 4 forward) the device can send.</summary>
        private byte Sendable(byte buttons)
        {
            byte m = 0;
            for (int i = 0; i < KbmDeviceCaps.WindowsMouseButtons; i++)
                if ((buttons & (1 << i)) != 0 && _caps.CanSendMouseButton(i)) m |= (byte)(1 << i);
            return m;
        }

        /// <summary>Poll thread: the slot's mouse buttons.</summary>
        public void SetButtons(byte buttons)
        {
            buttons = Sendable(buttons);
            if (buttons == _lastButtons) return;
            _lastButtons = buttons;
            if (_buttonStates.Count >= MaxQueuedButtonStates)
                while (_buttonStates.TryDequeue(out _)) { }
            _buttonStates.Enqueue(buttons);
            Signal();
        }

        /// <summary>Poll thread: relative movement in counts.</summary>
        public void AddMotion(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            Interlocked.Add(ref _dx, dx);
            Interlocked.Add(ref _dy, dy);
            Signal();
        }

        /// <summary>Poll thread: wheel detents, positive away from the
        /// user.</summary>
        public void AddWheel(int detents)
        {
            if (detents == 0 || !_caps.HasWheel) return;
            Interlocked.Add(ref _wheel, detents);
            Signal();
        }

        /// <summary>Poll thread: tilt detents, positive to the right.</summary>
        public void AddPan(int detents)
        {
            if (detents == 0 || !_caps.HasPan) return;
            Interlocked.Add(ref _pan, detents);
            Signal();
        }

        /// <summary>Wakes the sender on the first change after it went idle,
        /// so steady movement costs no kernel call per poll (the injector's
        /// MouseWorkArmed rule).</summary>
        private void Signal()
        {
            if (Interlocked.CompareExchange(ref _armed, 1, 0) == 0) _wake.Set();
        }

        /// <summary>KbmRawState button bit to the hooks' button id (0 left,
        /// 1 middle, 2 right, 3 back, 4 forward).</summary>
        private static readonly int[] HookIdOfBit = { 0, 2, 1, 3, 4 };

        private void SendLoop()
        {
            try { SendReports(); }
            catch (ObjectDisposedException) { /* disposed while it waited */ }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"KBMHID mouse {ProfileId} sender stopped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SendReports()
        {
            while (!_stop)
            {
                // A button held while a program drives PadForge is checked
                // against the windows often, so it is let go before it can
                // drag into a window with rights.
                _wake.WaitOne(_sentButtons != 0 && ProgramInputGate.InForce ? KbmHidKeyboard.GateCheckMs : 250);
                // Cleared before the drain, so a change that lands while it runs
                // signals again rather than waiting out the timeout.
                Volatile.Write(ref _armed, 0);
                while (!_stop)
                {
                    bool haveButtons = _buttonStates.TryDequeue(out byte buttons);
                    if (haveButtons) _wantButtons = buttons;
                    int dx = Interlocked.Exchange(ref _dx, 0);
                    int dy = Interlocked.Exchange(ref _dy, 0);
                    int wheel = Interlocked.Exchange(ref _wheel, 0);
                    int pan = Interlocked.Exchange(ref _pan, 0);
                    if (!haveButtons && dx == 0 && dy == 0 && wheel == 0 && pan == 0) break;
                    Submit(dx, dy, wheel, pan);
                }
                // The buttons follow the gate even when nothing moved: let go
                // while it holds them back, pressed again as the slot asks
                // once it opens.
                if (!_stop && Effective(out _, out _) != _sentButtons) Submit(0, 0, 0, 0);
            }
        }

        // Sender-thread state: the buttons the slot asks for, which the
        // device presses again once the gate opens.
        private byte _wantButtons;

        /// <summary>Sender thread: the buttons the device is to hold now. A
        /// press waits, and a held button is let go, while the gate holds
        /// buttons back.</summary>
        private byte Effective(out bool keysBlocked, out bool buttonsBlocked)
        {
            keysBlocked = ProgramInputGate.KeysBlocked;
            buttonsBlocked = keysBlocked || ProgramInputGate.ButtonsBlocked;
            return buttonsBlocked ? (byte)0 : _wantButtons;
        }

        /// <summary>Sender thread: one report with the buttons the gate
        /// lets the device hold, the movement it lets through while the
        /// window in front runs at medium integrity or below, and the wheel
        /// turns it lets through while buttons go too.</summary>
        private void Submit(int dx, int dy, int wheel, int pan)
        {
            byte buttons = Effective(out bool keysBlocked, out bool buttonsBlocked);
            if (keysBlocked) dx = dy = 0;
            if (buttonsBlocked) wheel = pan = 0;
            if (buttons == _sentButtons && dx == 0 && dy == 0 && wheel == 0 && pan == 0) return;
            lock (_buttonLock)
            {
                byte changed = (byte)(buttons ^ _sentButtons);
                for (int bit = 0; bit < KbmDeviceCaps.WindowsMouseButtons; bit++)
                    if ((changed & (1 << bit)) != 0)
                        OwnHidOutput.NoteButton(HookIdOfBit[bit], (buttons & (1 << bit)) != 0);
                _sentButtons = buttons;
            }
            try
            {
                _sink.SubmitMouse(new HMMouseState
                {
                    Buttons = (HMMouseButtons)buttons,
                    X = dx,
                    Y = dy,
                    Wheel = wheel,
                    Pan = pan,
                });
            }
            catch (ObjectDisposedException) { _stop = true; }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"KBMHID mouse {ProfileId} submit failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop = true;
            _wake.Set();
            bool stopped = _thread.Join(1000);
            // Disposing the sink lets go of the held buttons, which the hooks
            // hear about first.
            lock (_buttonLock)
            {
                for (int bit = 0; bit < KbmDeviceCaps.WindowsMouseButtons; bit++)
                    if ((_sentButtons & (1 << bit)) != 0) OwnHidOutput.NoteButton(HookIdOfBit[bit], down: false);
                _sentButtons = 0;
            }
            try { _sink.Dispose(); } catch { /* best effort */ }
            if (stopped) _wake.Dispose();
        }
    }
}
