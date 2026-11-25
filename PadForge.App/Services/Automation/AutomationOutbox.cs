using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation
{
    /// <summary>The newest value of each axis and slider of one device a
    /// session follows as a stream. While a value waits to be written, a
    /// newer one takes its place and the queue keeps one entry for it.
    /// Guarded by its outbox's lock: the writer reads a value and frees its
    /// slot in one hold of it, so a newer value is written in that batch or
    /// queued for the next.</summary>
    internal sealed class AxisStreamSlots
    {
        public AxisStreamSlots(string deviceId, int[] rests = null)
        {
            DeviceId = deviceId;
            Rests = rests ?? DefaultRests();
        }

        public readonly string DeviceId;

        /// <summary>Each axis's rest, which a plain value is read against
        /// (#600).</summary>
        public readonly int[] Rests;

        internal static int[] DefaultRests()
        {
            var rests = new int[Engine.ProgramAxis.End];
            for (int i = 0; i < rests.Length; i++) rests[i] = Engine.PlainUnits.DefaultRest(i);
            return rests;
        }

        public readonly int[] Value = new int[Engine.ProgramAxis.End];
        public readonly bool[] Waiting = new bool[Engine.ProgramAxis.End];
    }

    /// <summary>
    /// What one session sends, and the one task that writes it (#597).
    /// Answers, rows and events go through one queue in the order they were
    /// made, so the lines of an answer stay together. The poll thread puts
    /// its events here and returns: it takes this lock for a moment and
    /// never waits on the program. An axis stream keeps only its newest
    /// value while a write is pending. A program that does not read is cut
    /// off once its queue passes <see cref="Cap"/>, and the pass that found
    /// it so goes on at once.
    ///
    /// <para>The writer takes everything that waits in one step under the
    /// lock, by swapping the queue's ring for a spare it holds, and formats
    /// the batch after the lock is released, so the poll thread never waits
    /// while text is formatted. An axis item's value is read under the lock
    /// item by item, as the slot is freed.</para>
    ///
    /// <para>After each batch is written to a program that gave a window in
    /// its hello, the registered message <c>PadForge.Wake</c> is posted to
    /// that window, once it is checked to belong to the pipe's client. The
    /// message only wakes the program. The data is in the pipe, so a message
    /// the program misses loses nothing.</para>
    /// </summary>
    internal sealed class AutomationOutbox
    {
        /// <summary>How many lines may wait before the program is cut off.</summary>
        internal const int DefaultCap = 16384;

        private enum Kind : byte { Text, Button, Hat, Crossing, Axis }

        private struct Item
        {
            public Kind Kind;
            public string Text;
            public string Device;
            public int Index;
            public int Value;
            public int Line;
            public AxisStreamSlots Slots;
            public Action After;
        }

        private readonly object _sync = new object();
        private readonly Stream _stream;
        private readonly Action _overflowed;
        private Item[] _ring = new Item[64];
        private int _head, _count;

        /// <summary>The ring the writer swaps in when it takes a batch: the
        /// one it took before, emptied. Only the writer touches it.</summary>
        private Item[] _spare = new Item[64];
        private int _signaled;
        private volatile bool _cutOff;
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

        /// <param name="stream">Where the lines go.</param>
        /// <param name="overflowed">Called once, on a pool thread, when the
        /// queue passes its cap.</param>
        /// <param name="cap">The cap.</param>
        public AutomationOutbox(Stream stream, Action overflowed, int cap = DefaultCap)
        {
            _stream = stream;
            _overflowed = overflowed;
            Cap = cap;
        }

        public int Cap { get; }

        /// <summary>Where lines go in place of the pipe once an assistant's
        /// session has handed its stream to the protocol (#597): the pipe is
        /// the protocol's then, and a line written into it would break it.
        /// A text line is handed over at once, its after-write step runs at
        /// once, and the events of devices followed are dropped, since an
        /// assistant follows nothing over lines.</summary>
        public Action<string> Sink
        {
            get => _sink;
            set => _sink = value;
        }

        private volatile Action<string> _sink;

        /// <summary>The window to wake after each batch, and the process it
        /// must belong to. Set once by hello.</summary>
        public IntPtr WakeWindow { get; set; }
        public AutomationClientProcess Process { get; set; }

        /// <summary>The session asked for plain units in its hello
        /// (<c>units=plain</c>, #600): axes are written as -100 to 100 or 0
        /// to 100 and hats in degrees. Set once, before any event.</summary>
        public bool Plain { get; set; }

        /// <summary>Whether the queue passed its cap.</summary>
        public bool CutOff => _cutOff;

        /// <summary>How many lines wait now.</summary>
        public int Waiting
        {
            get { lock (_sync) return _count; }
        }

        /// <summary>A line, as it is to be written. <paramref name="after"/>
        /// runs once the batch that holds it is in the pipe.</summary>
        public bool Line(string text, Action after = null)
            => Put(new Item { Kind = Kind.Text, Text = text, After = after });

        // The poll thread's four. Each formats nothing: the writer does.

        public bool Button(string device, int index, bool down)
            => Put(new Item { Kind = Kind.Button, Device = device, Index = index, Value = down ? 1 : 0 });

        public bool Hat(string device, int index, int value)
            => Put(new Item { Kind = Kind.Hat, Device = device, Index = index, Value = value });

        /// <summary>An axis past its line or back. <paramref name="line"/>
        /// is the line's number, which a named subscription's event repeats,
        /// or 0 for the <c>axis=</c> form's event.</summary>
        public bool Crossing(string device, int index, bool above, int line = 0)
            => Put(new Item { Kind = Kind.Crossing, Device = device, Index = index, Value = above ? 1 : 0, Line = line });

        /// <summary>An axis of a stream. A value of the same axis that still
        /// waits is replaced, and no second line is queued.</summary>
        public bool Axis(AxisStreamSlots slots, int index, int value)
        {
            if (_sink != null) return true;
            if (_cutOff) return false;
            lock (_sync)
            {
                slots.Value[index] = value;
                if (slots.Waiting[index]) return true;
                slots.Waiting[index] = true;
                if (!PutLocked(new Item { Kind = Kind.Axis, Slots = slots, Index = index })) return false;
            }
            Signal();
            return true;
        }

        private bool Put(in Item item)
        {
            var sink = _sink;
            if (sink != null)
            {
                if (item.Kind == Kind.Text && item.Text != null)
                {
                    try { sink(item.Text); } catch { /* the sink's failure is not the sender's */ }
                }
                var after = item.After;
                if (after != null) ThreadPool.UnsafeQueueUserWorkItem(_ => after(), null);
                return true;
            }
            if (_cutOff) return false;
            bool put;
            lock (_sync) put = PutLocked(item);
            if (put) Signal();
            return put;
        }

        private bool PutLocked(in Item item)
        {
            if (_cutOff) return false;
            if (_count >= Cap)
            {
                _cutOff = true;
                // Off this thread: the poll thread may be the one that
                // found it, and closing a pipe is not its work.
                ThreadPool.UnsafeQueueUserWorkItem(_ => _overflowed?.Invoke(), null);
                return false;
            }
            if (_count == _ring.Length)
            {
                var grown = new Item[_ring.Length * 2];
                for (int i = 0; i < _count; i++) grown[i] = _ring[(_head + i) % _ring.Length];
                _ring = grown;
                _head = 0;
            }
            _ring[(_head + _count) % _ring.Length] = item;
            _count++;
            return true;
        }

        private void Signal()
        {
            if (Interlocked.Exchange(ref _signaled, 1) == 0) _signal.Release();
        }

        /// <summary>Test seam: runs on the writer after each item of a batch
        /// is formatted, outside the lock. Null in production.</summary>
        internal Action FormattedForTest;

        /// <summary>
        /// Writes until <paramref name="stopping"/> is canceled or the pipe
        /// breaks. Each wake takes everything that waits, formats it outside
        /// the lock and writes it in one write.
        /// </summary>
        public async Task RunAsync(CancellationToken stopping)
        {
            var text = new StringBuilder(4096);
            var afters = new System.Collections.Generic.List<Action>();
            while (!stopping.IsCancellationRequested)
            {
                // A wake the program may have missed is posted again
                // (#600 item 24): AutoHotkey drops a message that comes while
                // the callback of the one before still runs, and the lines
                // then wait in the pipe. While the program has said nothing
                // since the last batch, the wake goes again every
                // RewakeMs, up to MostRewakes times.
                if (Volatile.Read(ref _wakesOwed) > 0)
                {
                    if (!await _signal.WaitAsync(RewakeMs, stopping).ConfigureAwait(false))
                    {
                        if (Volatile.Read(ref _heard) == _heardAtWake && Interlocked.Decrement(ref _wakesOwed) >= 0) Wake();
                        else Volatile.Write(ref _wakesOwed, 0);
                        continue;
                    }
                }
                else await _signal.WaitAsync(stopping).ConfigureAwait(false);
                Volatile.Write(ref _signaled, 0);
                text.Clear();
                afters.Clear();
                Item[] taken;
                int head, count;
                lock (_sync)
                {
                    taken = _ring;
                    head = _head;
                    count = _count;
                    if (count > 0)
                    {
                        _ring = _spare;
                        _head = 0;
                        _count = 0;
                    }
                }
                if (count == 0) continue;
                for (int i = 0; i < count; i++)
                {
                    ref Item item = ref taken[(head + i) % taken.Length];
                    Format(text, item);
                    if (item.After != null) afters.Add(item.After);
                    item = default;
                    FormattedForTest?.Invoke();
                }
                // Emptied, the ring taken is the next batch's spare.
                _spare = taken;
                if (text.Length > 0)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
                    await _stream.WriteAsync(bytes.AsMemory(), stopping).ConfigureAwait(false);
                    await _stream.FlushAsync(stopping).ConfigureAwait(false);
                    // What the program said is counted before the wake, so a
                    // line it sends as soon as it wakes counts as heard.
                    int heard = Volatile.Read(ref _heard);
                    if (Wake())
                    {
                        _heardAtWake = heard;
                        Volatile.Write(ref _wakesOwed, MostRewakes);
                    }
                }
                foreach (var after in afters)
                {
                    try { after(); } catch { /* the answer is out, and the loop goes on */ }
                }
            }
        }

        /// <summary>Outside the lock. An axis slot is read and freed under
        /// the lock, so the value written is the newest, and one that comes
        /// after it is queued again.</summary>
        private void Format(StringBuilder text, in Item item)
        {
            switch (item.Kind)
            {
                case Kind.Text:
                    // A line of no text carries only what runs after the
                    // write, such as a close.
                    if (item.Text != null) text.Append(item.Text).Append('\n');
                    break;
                case Kind.Button:
                    text.Append("e b ").Append(item.Device).Append(' ').Append(item.Index).Append(' ').Append(item.Value).Append('\n');
                    break;
                case Kind.Hat:
                    text.Append("e h ").Append(item.Device).Append(' ').Append(item.Index).Append(' ');
                    if (Plain) text.Append(Engine.PlainUnits.Format(Engine.PlainUnits.HatToPlain(item.Value)));
                    else text.Append(item.Value);
                    text.Append('\n');
                    break;
                case Kind.Crossing:
                    text.Append("e x ").Append(item.Device).Append(' ').Append(item.Index).Append(' ').Append(item.Value);
                    if (item.Line > 0) text.Append(' ').Append(item.Line);
                    text.Append('\n');
                    break;
                case Kind.Axis:
                    var slots = item.Slots;
                    int value;
                    lock (_sync)
                    {
                        value = slots.Value[item.Index];
                        slots.Waiting[item.Index] = false;
                    }
                    text.Append("e a ").Append(slots.DeviceId).Append(' ').Append(item.Index).Append(' ');
                    if (Plain) text.Append(Engine.PlainUnits.Format(Engine.PlainUnits.AxisToPlain(value, slots.Rests[item.Index])));
                    else text.Append(value);
                    text.Append('\n');
                    break;
            }
        }

        private static int _wakeMessage;

        /// <summary>The registered message <c>PadForge.Wake</c>.</summary>
        internal static int WakeMessage
        {
            get
            {
                int message = Volatile.Read(ref _wakeMessage);
                if (message == 0)
                {
                    message = unchecked((int)RegisterWindowMessageW("PadForge.Wake"));
                    Volatile.Write(ref _wakeMessage, message);
                }
                return message;
            }
        }

        /// <summary>How long a wake waits for the program to speak before it
        /// is posted again, and how many times it is.</summary>
        internal const int RewakeMs = 20, MostRewakes = 25;

        private int _heard, _heardAtWake, _wakesOwed;

        /// <summary>How many wakes were posted, for tests.</summary>
        internal int Wakes => Volatile.Read(ref _wakes);
        private int _wakes;

        /// <summary>The program sent a line: it is awake, and a wake owed is
        /// not posted again.</summary>
        public void Heard() => Interlocked.Increment(ref _heard);

        /// <summary>Test seam: stands in for posting the message. Null in
        /// the app.</summary>
        internal Action WakeForTest;

        private bool Wake()
        {
            var seam = WakeForTest;
            if (seam != null)
            {
                Interlocked.Increment(ref _wakes);
                seam();
                return true;
            }
            IntPtr window = WakeWindow;
            var process = Process;
            if (window == IntPtr.Zero || process == null) return false;
            // Checked again each time: a window that closed can come back as
            // another program's handle.
            if (!process.OwnsWindow(window)) return false;
            Interlocked.Increment(ref _wakes);
            return PostMessageW(window, unchecked((uint)WakeMessage), IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
