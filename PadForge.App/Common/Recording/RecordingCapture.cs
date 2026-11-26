using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Recording;
using PadForge.Services;

namespace PadForge.Common.Recording
{
    /// <summary>One change the poll thread hands over: plain values, with
    /// the time it was captured.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RecordingEntry
    {
        public long Ticks;
        public int A, B, C;
        public ushort Index;
        public RecordingField Field;
        public byte Subject;
    }

    /// <summary>
    /// The ring the poll thread writes changes into and the recorder's own
    /// thread reads them out of (#600). One writer and one reader, so neither
    /// takes a lock: the writer owns the head and the reader the tail, and
    /// each reads the other's with a volatile read. A push makes nothing. A
    /// push that finds the ring full is counted and left out.
    ///
    /// <para>What a tap pushes becomes the reader's all at once, at
    /// <see cref="Commit"/>: the changes of one device on one pass are one
    /// line of the file, and a reader that came by between two of them
    /// would cut the line in two.</para>
    /// </summary>
    internal sealed class RecordingRing
    {
        private readonly RecordingEntry[] _entries;
        private readonly int _mask;
        private int _head;
        private int _write;
        private int _tail;
        private long _lost;

        public RecordingRing(int capacity)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "A power of two.");
            _entries = new RecordingEntry[capacity];
            _mask = capacity - 1;
        }

        public int Capacity => _entries.Length;

        /// <summary>The changes left out because the ring was full. Written
        /// by the poll thread alone.</summary>
        public long Lost => Volatile.Read(ref _lost);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(long ticks, RecordingField field, int subject, int index, int a, int b = 0, int c = 0)
        {
            int write = _write;
            int next = (write + 1) & _mask;
            if (next == Volatile.Read(ref _tail))
            {
                _lost++;
                return;
            }
            ref RecordingEntry e = ref _entries[write];
            e.Ticks = ticks;
            e.A = a;
            e.B = b;
            e.C = c;
            e.Index = (ushort)index;
            e.Field = field;
            e.Subject = (byte)subject;
            _write = next;
        }

        /// <summary>Hands what was pushed since the last commit to the
        /// reader: one volatile write, and none when nothing was pushed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit()
        {
            if (_write != _head) Volatile.Write(ref _head, _write);
        }

        /// <summary>Copies what the ring holds into <paramref name="into"/>,
        /// oldest first, and frees it. The reader's thread only.</summary>
        public int Drain(RecordingEntry[] into)
        {
            int tail = _tail;
            int head = Volatile.Read(ref _head);
            int n = 0;
            while (tail != head && n < into.Length)
            {
                into[n++] = _entries[tail];
                tail = (tail + 1) & _mask;
            }
            Volatile.Write(ref _tail, tail);
            return n;
        }
    }

    /// <summary>What was last recorded of one device, which the next state
    /// is compared with. Its arrays are made once, at their largest, so
    /// nothing is made while a recording runs. Poll thread only, but for
    /// <see cref="Device"/> and <see cref="KeysRead"/>.</summary>
    internal sealed class InputTrack
    {
        public const int NoShape = int.MinValue;

        public volatile UserDevice Device;

        /// <summary>The key indexes of this keyboard a mapping, a layer or a
        /// macro reads, as 256 bits, or null when none is known. The
        /// recorder's thread swaps it whole.</summary>
        public volatile ulong[] KeysRead;

        /// <summary>-1 before the device was seen, 1 online, 0 offline.</summary>
        public int Online = -1;
        public long SeenPass = -1;

        public readonly int[] Axis = new int[CustomInputState.MaxAxis];
        public readonly int[] Sliders = new int[CustomInputState.MaxSliders];
        public readonly int[] Povs = new int[CustomInputState.MaxPovs];
        public readonly bool[] Buttons = new bool[CustomInputState.MaxButtons];
        public readonly int[] Gyro = new int[3], Accel = new int[3], GyroAux = new int[3], AccelAux = new int[3];

        public int PadCount;
        public readonly int[] Fingers = new int[RecordingCodec.MaxTouchpads];
        public readonly bool[] Click = new bool[RecordingCodec.MaxTouchpads];
        private const int Touches = RecordingCodec.MaxTouchpads * RecordingCodec.MaxFingers;
        public readonly bool[] TouchDown = new bool[Touches];
        public readonly int[] TouchId = new int[Touches];
        public readonly int[] TouchX = new int[Touches], TouchY = new int[Touches], TouchPressure = new int[Touches];

        public bool HasMidi;
        public readonly bool[] Notes = new bool[MidiInputState.NoteCount];
        public readonly byte[] Cc = new byte[MidiInputState.CcCount];
        public readonly bool[] CcUp = new bool[MidiInputState.CcCount], CcDown = new bool[MidiInputState.CcCount];
        public int PitchBend;

        public bool HasAnalog;
        public int AnalogCount;
        public readonly int[] AnalogCodes = new int[AnalogKeyInputState.MaxKeys];
        public readonly int[] AnalogDepth = new int[AnalogKeyInputState.CodeCount];

        public int IrX, IrY, IrFlags, JoyConIr, RingCon;
        public bool MouseMoving, JoyConMouseMoving, WheelMoving;
        public int CapCount, NfcCount;
        public readonly bool[] Cap = new bool[RecordingCodec.MaxCapSense];
        public readonly bool[] Nfc = new bool[RecordingCodec.MaxNfcTags];
        public int BatteryPercent;
        public bool BatteryCharging;
        public int RumbleA, RumbleB;

        /// <summary>What force updates asked of the device this second, and
        /// what the last second's line said (#600). Poll thread.</summary>
        public int ForceUpdates, ForceMade, ForceStarts;
        public int ForceUpdatesSaid, ForceMadeSaid, ForceStartsSaid;

        /// <summary>The effect kinds asked of the device and the kinds it
        /// refused, as SDL_HAPTIC_ bits (#600). The poll thread writes them,
        /// the recorder's thread reads them.</summary>
        public uint ForceAsked, ForceRefused;

        /// <summary>How many reads of the device failed in a row, up to the
        /// read that works again (#600).</summary>
        public int ReadFailures;

        public InputTrack() => Reset();

        /// <summary>Back to a state with nothing in it and no optional part:
        /// what a device that comes online is compared with, so its first
        /// frame says everything that is not at rest.</summary>
        public void Reset()
        {
            Array.Clear(Axis);
            Array.Clear(Sliders);
            Array.Fill(Povs, -1);
            Array.Clear(Buttons);
            Array.Clear(Gyro);
            Array.Clear(Accel);
            Array.Clear(GyroAux);
            Array.Clear(AccelAux);
            PadCount = 0;
            Array.Fill(Fingers, NoShape);
            Array.Clear(Click);
            Array.Clear(TouchDown);
            Array.Fill(TouchId, -1);
            Array.Clear(TouchX);
            Array.Clear(TouchY);
            Array.Clear(TouchPressure);
            HasMidi = false;
            ResetMidi();
            HasAnalog = false;
            ResetAnalog();
            IrX = IrY = IrFlags = JoyConIr = RingCon = 0;
            MouseMoving = JoyConMouseMoving = WheelMoving = false;
            CapCount = NfcCount = 0;
            Array.Clear(Cap);
            Array.Clear(Nfc);
            BatteryPercent = -1;
            BatteryCharging = false;
        }

        public void ResetMidi()
        {
            Array.Clear(Notes);
            Array.Clear(Cc);
            Array.Clear(CcUp);
            Array.Clear(CcDown);
            PitchBend = MidiInputState.PitchBendCenter;
        }

        public void ResetAnalog()
        {
            for (int i = 0; i < AnalogCount; i++) AnalogDepth[AnalogCodes[i]] = 0;
            AnalogCount = 0;
        }

        public void ResetPad(int pad)
        {
            Click[pad] = false;
            int from = pad * RecordingCodec.MaxFingers;
            for (int i = from; i < from + RecordingCodec.MaxFingers; i++)
            {
                TouchDown[i] = false;
                TouchId[i] = -1;
                TouchX[i] = TouchY[i] = TouchPressure[i] = 0;
            }
        }
    }

    /// <summary>What was last recorded of one virtual controller's frame and
    /// of what a game sent it. Poll thread only.</summary>
    internal sealed class OutputTrack
    {
        public readonly RecordedOutputState Last = new();
        public int Sent = -1, SentType = int.MinValue;
        public int RumbleA, RumbleB;
        public bool FlickMoving, GyroMoving, TouchMoving, CoastMoving;
    }

    /// <summary>
    /// The poll thread's side of a problem recording (#600). Each pass it
    /// compares every device's state, every virtual controller's frame and
    /// what games sent back with what it last recorded, and pushes the
    /// fields that changed into a ring as plain values, each with its own
    /// capture time. It allocates nothing and takes no lock: the tracks are
    /// made when the capture is, a device finds its track through a table of
    /// hints, and a change costs a few stores and one volatile write.
    ///
    /// <para>Everything a device reports is recorded as it is, a keyboard's
    /// keys and a mouse's buttons included. One switch, off unless the user
    /// turns it on, writes the letter and number keys that no mapping and no
    /// macro reads as one key that is no key
    /// (<see cref="TypedKey"/>), each press and release at its own time.</para>
    /// </summary>
    internal sealed class RecordingCapture
    {
        /// <summary>The most devices one capture follows. A device past
        /// this is counted and left out.</summary>
        internal const int MaxDevices = 256;

        /// <summary>The key index a typed letter or number is written as
        /// while <see cref="MaskTypedKeys"/> is on: one Windows gives to no
        /// key.</summary>
        internal const int TypedKey = RecordingControlNames.TypedKeyCode;

        /// <summary>The capture the poll thread writes to, or null while
        /// nothing records. Set and cleared by the recorder.</summary>
        internal static volatile RecordingCapture Active;

        internal readonly RecordingRing Ring;
        private readonly InputTrack[] _tracks = new InputTrack[MaxDevices];
        private readonly OutputTrack[] _outputs = new OutputTrack[PadForge.Common.Input.InputManager.MaxPads];
        private readonly int[] _hints = new int[256];
        private int _count;
        private long _untracked;
        private long _pass;

        // The timing of the second under way.
        private long _secondStart;
        private int _rest = -1;
        private long _timedTicks;
        private int _timedPasses;

        /// <summary>The parts the capture takes (#600). A part no recording
        /// holds costs the poll thread one volatile read a tap.</summary>
        internal volatile RecordingParts Parts = RecordingParts.All;

        /// <summary>Whether a keyboard's letter and number keys that nothing
        /// reads are written as <see cref="TypedKey"/>.</summary>
        internal volatile bool MaskTypedKeys;

        /// <summary>When the last pass ended, on the capture's clock, for the
        /// recorder's watch on an engine that stops running passes (#600).
        /// Zero before the first.</summary>
        internal long LastPass => Volatile.Read(ref _lastPass);
        private long _lastPass;

        /// <summary>Whether the engine rests (<see cref="Rest"/>), for the
        /// same watch: a suspended engine runs no pass by design.</summary>
        internal int RestState => Volatile.Read(ref _restSeen);
        private int _restSeen;

        // The device calls under way (#600), one a slot: which track, what
        // call and since when. Slot 0 is the poll thread's. The others are
        // taken for one call each by any other thread, as the Sony pads'
        // effect reports are written. The calling thread writes its slot,
        // and the recorder's thread reads every slot to say a call that does
        // not return while it hangs.
        internal const int CallSlots = 5;
        private readonly int[] _callTrack = NewCallTracks(), _callKind = new int[CallSlots];
        private readonly long[] _callStart = new long[CallSlots];
        private int _callThread;

        private static int[] NewCallTracks()
        {
            var tracks = new int[CallSlots];
            Array.Fill(tracks, -1);
            return tracks;
        }

        /// <summary>The device call under way in a slot: its track, its kind
        /// and when it began, or a track of -1. The track is read first, and
        /// a call writes it last, so the time read after it is the call's
        /// own.</summary>
        internal (int Track, int Kind, long Start) CallIn(int slot)
        {
            int track = Volatile.Read(ref _callTrack[slot]);
            if (track < 0) return (-1, 0, 0);
            return (track, Volatile.Read(ref _callKind[slot]), Volatile.Read(ref _callStart[slot]));
        }

        /// <summary>A device call that took longer than this is written
        /// with its device.</summary>
        internal const int SlowCallMs = 20;

        /// <summary>A call to a device begins on the poll thread.</summary>
        internal void BeginCall(UserDevice device, int kind)
        {
            int index = TrackOf(device);
            _callThread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _callKind[0], kind);
            Volatile.Write(ref _callStart[0], Ticks());
            Volatile.Write(ref _callTrack[0], index);
        }

        /// <summary>The poll thread's call ends: one that took long is
        /// written with its device, how long it took and what it was. Poll
        /// thread only.</summary>
        internal void EndCall()
        {
            int index = _callTrack[0];
            Volatile.Write(ref _callTrack[0], -1);
            if (index < 0 || (Parts & RecordingParts.Timing) == 0) return;
            long now = Ticks();
            long ms = (now - _callStart[0]) * 1000 / Stopwatch.Frequency;
            if (ms < SlowCallMs) return;
            Ring.Push(now, RecordingField.TimingCall, index, 0, (int)Math.Min(ms, int.MaxValue), _callKind[0]);
            Ring.Commit();
        }

        /// <summary>The poll thread's call under way is of another kind than
        /// it began as: a feedback write that reaches a device's force
        /// effects or a wheel's force reports is a force write. Nothing from
        /// another thread, or with no call under way.</summary>
        internal void CallIs(int kind)
        {
            if (Volatile.Read(ref _callTrack[0]) < 0 || Environment.CurrentManagedThreadId != _callThread) return;
            Volatile.Write(ref _callKind[0], kind);
        }

        /// <summary>A call to a device begins on a thread other than the poll
        /// thread: the slot it takes, or 0 when the device has no track, the
        /// file holds no timing or every slot is in use. Any thread.</summary>
        internal int BeginOtherCall(int track, int kind)
        {
            if (track < 0 || (Parts & RecordingParts.Timing) == 0) return 0;
            for (int slot = 1; slot < CallSlots; slot++)
            {
                // Taken first, then filled: the watch reads a slot whose
                // track is below 0 as free.
                if (Interlocked.CompareExchange(ref _callTrack[slot], -2, -1) != -1) continue;
                Volatile.Write(ref _callKind[slot], kind);
                Volatile.Write(ref _callStart[slot], Ticks());
                Volatile.Write(ref _callTrack[slot], track);
                return slot;
            }
            return 0;
        }

        /// <summary>The call of a slot <see cref="BeginOtherCall"/> gave
        /// ends: how long it took in milliseconds, and the slot is free again.
        /// Any thread.</summary>
        internal long EndOtherCall(int slot)
        {
            if (slot <= 0 || slot >= CallSlots) return 0;
            long ms = (Ticks() - Volatile.Read(ref _callStart[slot])) * 1000 / Stopwatch.Frequency;
            Volatile.Write(ref _callTrack[slot], -1);
            return ms;
        }

        /// <summary>The track of a device by its path, or -1. It makes no
        /// track, so a thread other than the poll thread may ask.</summary>
        internal int TrackOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            int count = DeviceCount;
            for (int i = 0; i < count; i++)
                if (string.Equals(_tracks[i].Device?.DevicePath, path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>The track of a device the engine drives through
        /// <paramref name="live"/>, or -1. Poll thread.</summary>
        internal int TrackOfLive(ISdlInputDevice live)
        {
            if (live == null) return -1;
            int last = _lastForceTrack;
            int count = _count;
            if ((uint)last < (uint)count && ReferenceEquals(_tracks[last].Device?.Device, live)) return last;
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(_tracks[i].Device?.Device, live)) continue;
                _lastForceTrack = i;
                return i;
            }
            return -1;
        }
        private int _lastForceTrack = -1;

        /// <summary>The device a track follows, or null.</summary>
        internal UserDevice DeviceOfTrack(int track) => (uint)track < (uint)DeviceCount ? _tracks[track].Device : null;

        /// <summary>Counts what a force update asked of a device (#600): an
        /// update, an effect made, an effect started again. Poll thread.</summary>
        internal void CountForce(ISdlInputDevice live, bool made, bool started)
        {
            if ((Parts & RecordingParts.Feedback) == 0) return;
            int index = TrackOfLive(live);
            if (index < 0) return;
            var t = _tracks[index];
            t.ForceUpdates++;
            if (made) t.ForceMade++;
            if (started) t.ForceStarts++;
        }

        /// <summary>An effect of one kind (an SDL_HAPTIC_ bit) asked of a
        /// device (#600): taken, or refused when making it failed. A kind
        /// the device takes later is no longer refused. Poll thread.</summary>
        internal void ForceKind(ISdlInputDevice live, uint kind, bool refused)
        {
            if ((Parts & RecordingParts.Feedback) == 0 || kind == 0) return;
            int index = TrackOfLive(live);
            if (index < 0) return;
            var t = _tracks[index];
            if ((t.ForceAsked & kind) == 0) Volatile.Write(ref t.ForceAsked, t.ForceAsked | kind);
            uint was = t.ForceRefused;
            uint now = refused ? was | kind : was & ~kind;
            if (now != was) Volatile.Write(ref t.ForceRefused, now);
        }

        /// <summary>The effect kinds asked of a track's device and the kinds
        /// it refuses, for the recorder's thread.</summary>
        internal (uint Asked, uint Refused) ForceKinds(int track)
        {
            if ((uint)track >= (uint)DeviceCount) return (0, 0);
            var t = _tracks[track];
            return (Volatile.Read(ref t.ForceAsked), Volatile.Read(ref t.ForceRefused));
        }

        /// <summary>Writes each device's force counts of the second that
        /// ended, when they differ from the second before. Poll thread.</summary>
        private void ForceSecond(long now)
        {
            if ((Parts & RecordingParts.Feedback) == 0) return;
            int count = _count;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                var t = _tracks[i];
                if (t.ForceUpdates != t.ForceUpdatesSaid || t.ForceMade != t.ForceMadeSaid || t.ForceStarts != t.ForceStartsSaid)
                {
                    Ring.Push(now, RecordingField.ForceCounts, i, 0, t.ForceUpdates, t.ForceMade, t.ForceStarts);
                    t.ForceUpdatesSaid = t.ForceUpdates;
                    t.ForceMadeSaid = t.ForceMade;
                    t.ForceStartsSaid = t.ForceStarts;
                    any = true;
                }
                t.ForceUpdates = t.ForceMade = t.ForceStarts = 0;
            }
            if (any) Ring.Commit();
        }

        /// <summary>A failed read of a device: how many of its reads have
        /// failed in a row, this one included. Poll thread.</summary>
        internal int ReadFailed(UserDevice device)
        {
            int index = TrackOf(device);
            if (index < 0) return 0;
            return ++_tracks[index].ReadFailures;
        }

        /// <summary>True on the passes whose cost is measured: one in
        /// sixty-four, so the measuring costs next to nothing.</summary>
        internal bool Timed => (_pass & 63) == 0;

        internal RecordingCapture(int ringCapacity = 1 << 17)
        {
            Ring = new RecordingRing(ringCapacity);
            for (int i = 0; i < _tracks.Length; i++) _tracks[i] = new InputTrack();
            for (int i = 0; i < _outputs.Length; i++) _outputs[i] = new OutputTrack();
        }

        /// <summary>How many devices have a track. Tracks are handed out in
        /// order and never taken back.</summary>
        internal int DeviceCount => Volatile.Read(ref _count);

        internal InputTrack Track(int index) => _tracks[index];

        /// <summary>The devices past <see cref="MaxDevices"/> that reported
        /// and were left out, counted once a pass each.</summary>
        internal long Untracked => Volatile.Read(ref _untracked);

        /// <summary>Gives a device a track before the capture is armed, so
        /// the devices of the list are numbered in the list's order. Not for
        /// the poll thread.</summary>
        internal int Prepare(UserDevice device) => TrackOf(device);

        private int TrackOf(UserDevice device)
        {
            int slot = (RuntimeHelpers.GetHashCode(device) >> 3) & (_hints.Length - 1);
            int hint = _hints[slot] - 1;
            int count = _count;
            if ((uint)hint < (uint)count && ReferenceEquals(_tracks[hint].Device, device)) return hint;
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(_tracks[i].Device, device)) continue;
                _hints[slot] = i + 1;
                return i;
            }
            if (count >= MaxDevices)
            {
                _untracked++;
                return -1;
            }
            _tracks[count].Device = device;
            Volatile.Write(ref _count, count + 1);
            _hints[slot] = count + 1;
            return count;
        }

        /// <summary>Test seam: ticks added to every capture time, so a test
        /// lives through minutes in a moment. The recorder's own clock adds
        /// the same.</summary>
        internal long Shift;

        /// <summary>Test seam: a Stopwatch timestamp the capture's clock
        /// stands still at, so a test's times are what it moved them by
        /// and never what the machine spent. Zero follows the real clock.</summary>
        internal long Frozen;

        /// <summary>The capture's time now. The recorder's thread keeps its
        /// time by this clock too.</summary>
        internal long Ticks() => (Frozen != 0 ? Frozen : Stopwatch.GetTimestamp()) + Shift;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long Now(ref long now)
        {
            if (now == 0) now = Ticks();
            return now;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        // ─────────────────────────────────────────────
        //  Devices
        // ─────────────────────────────────────────────

        /// <summary>True for the key indexes of the letters, the digits of
        /// the top row and the digits of the number pad.</summary>
        internal static bool IsTypedKeyIndex(int index)
            => index is (>= 0x41 and <= 0x5A) or (>= 0x30 and <= 0x39) or (>= 0x60 and <= 0x69);

        /// <summary>Records what changed in a device's state on this pass,
        /// and answers how many failed reads in a row this good one ends, or
        /// 0. Poll thread only.</summary>
        internal int Input(UserDevice device, CustomInputState s)
        {
            if ((Parts & RecordingParts.Input) == 0) return 0;
            int index = TrackOf(device);
            if (index < 0) return 0;
            var t = _tracks[index];
            int ended = t.ReadFailures;
            t.ReadFailures = 0;
            t.SeenPass = _pass;
            var ring = Ring;
            long now = 0;

            if (t.Online != 1)
            {
                // Its first state is compared with a state at rest, so the
                // line says all of it.
                t.Reset();
                t.Online = 1;
                ring.Push(Now(ref now), RecordingField.Online, index, 0, 1);
            }

            if (!s.Axis.AsSpan().SequenceEqual(t.Axis))
            {
                Now(ref now);
                for (int i = 0; i < CustomInputState.MaxAxis; i++)
                {
                    int value = s.Axis[i];
                    if (value == t.Axis[i]) continue;
                    t.Axis[i] = value;
                    ring.Push(now, RecordingField.Axis, index, i, value);
                }
            }
            if (!s.Sliders.AsSpan().SequenceEqual(t.Sliders))
            {
                Now(ref now);
                for (int i = 0; i < CustomInputState.MaxSliders; i++)
                {
                    int value = s.Sliders[i];
                    if (value == t.Sliders[i]) continue;
                    t.Sliders[i] = value;
                    ring.Push(now, RecordingField.Slider, index, i, value);
                }
            }
            if (!s.Povs.AsSpan().SequenceEqual(t.Povs))
            {
                Now(ref now);
                for (int i = 0; i < CustomInputState.MaxPovs; i++)
                {
                    int value = s.Povs[i];
                    if (value == t.Povs[i]) continue;
                    t.Povs[i] = value;
                    ring.Push(now, RecordingField.Hat, index, i, value);
                }
            }
            if (!s.Buttons.AsSpan().SequenceEqual(t.Buttons))
            {
                Now(ref now);
                bool mask = MaskTypedKeys && device.IsKeyboard;
                ulong[] read = mask ? t.KeysRead : null;
                for (int i = 0; i < CustomInputState.MaxButtons; i++)
                {
                    bool down = s.Buttons[i];
                    if (down == t.Buttons[i]) continue;
                    t.Buttons[i] = down;
                    int written = mask && IsTypedKeyIndex(i) && (read == null || (read[i >> 6] & (1UL << (i & 63))) == 0)
                        ? TypedKey : i;
                    ring.Push(now, RecordingField.Button, index, written, down ? 1 : 0);
                }
            }

            Vector(ring, ref now, index, RecordingField.Gyro, s.Gyro, t.Gyro);
            Vector(ring, ref now, index, RecordingField.Accel, s.Accel, t.Accel);
            Vector(ring, ref now, index, RecordingField.GyroAux, s.GyroAux, t.GyroAux);
            Vector(ring, ref now, index, RecordingField.AccelAux, s.AccelAux, t.AccelAux);

            Touch(ring, ref now, index, s, t);
            if (s.Midi != null || t.HasMidi) Midi(ring, ref now, index, s.Midi, t);
            if (s.AnalogKeys != null || t.HasAnalog) Analog(ring, ref now, index, s.AnalogKeys, t);

            int irX = Bits(s.Ir.X), irY = Bits(s.Ir.Y), irFlags = (s.Ir.Detected ? 1 : 0) | (s.Ir.Calibrated ? 2 : 0);
            if (irX != t.IrX || irY != t.IrY || irFlags != t.IrFlags)
            {
                t.IrX = irX;
                t.IrY = irY;
                t.IrFlags = irFlags;
                ring.Push(Now(ref now), RecordingField.IrPointer, index, 0, irX, irY, irFlags);
            }
            int joyConIr = Bits(s.JoyConIrIntensity);
            if (joyConIr != t.JoyConIr)
            {
                t.JoyConIr = joyConIr;
                ring.Push(Now(ref now), RecordingField.JoyConIr, index, 0, joyConIr);
            }
            int ringCon = Bits(s.RingConStrain);
            if (ringCon != t.RingCon)
            {
                t.RingCon = ringCon;
                ring.Push(Now(ref now), RecordingField.RingCon, index, 0, ringCon);
            }

            // Counts of one pass: every pass that has any is written, and
            // the first pass without, so the counts add up to what moved.
            bool joyConMoving = s.JoyCon2MouseDX != 0f || s.JoyCon2MouseDY != 0f;
            if (joyConMoving || t.JoyConMouseMoving)
            {
                t.JoyConMouseMoving = joyConMoving;
                ring.Push(Now(ref now), RecordingField.JoyCon2Mouse, index, 0, Bits(s.JoyCon2MouseDX), Bits(s.JoyCon2MouseDY));
            }
            bool mouseMoving = (s.MouseRawDX | s.MouseRawDY) != 0;
            if (mouseMoving || t.MouseMoving)
            {
                t.MouseMoving = mouseMoving;
                ring.Push(Now(ref now), RecordingField.MouseRaw, index, 0, s.MouseRawDX, s.MouseRawDY);
            }
            bool wheelMoving = (s.MouseRawWheel | s.MouseRawWheelH) != 0;
            if (wheelMoving || t.WheelMoving)
            {
                t.WheelMoving = wheelMoving;
                ring.Push(Now(ref now), RecordingField.MouseWheel, index, 0, s.MouseRawWheel, s.MouseRawWheelH);
            }

            Flags(ring, ref now, index, RecordingField.CapSense, RecordingCodec.Shape.CapSense, s.CapSense, t.Cap, ref t.CapCount);
            Flags(ring, ref now, index, RecordingField.NfcTag, RecordingCodec.Shape.Nfc, s.NfcTag, t.Nfc, ref t.NfcCount);

            if (s.BatteryPercent != t.BatteryPercent || s.BatteryCharging != t.BatteryCharging)
            {
                t.BatteryPercent = s.BatteryPercent;
                t.BatteryCharging = s.BatteryCharging;
                ring.Push(Now(ref now), RecordingField.Battery, index, 0, s.BatteryPercent, s.BatteryCharging ? 1 : 0);
            }
            ring.Commit();
            return ended;
        }

        private void Vector(RecordingRing ring, ref long now, int index, RecordingField field, float[] values, int[] last)
        {
            int a = Bits(values[0]), b = Bits(values[1]), c = Bits(values[2]);
            if (a == last[0] && b == last[1] && c == last[2]) return;
            last[0] = a;
            last[1] = b;
            last[2] = c;
            ring.Push(Now(ref now), field, index, 0, a, b, c);
        }

        private void Flags(RecordingRing ring, ref long now, int index, RecordingField field, int shape,
            bool[] values, bool[] last, ref int lastCount)
        {
            int count = values == null ? 0 : Math.Min(values.Length, last.Length);
            if (count != lastCount)
            {
                // A part that grows keeps what it had, and its new places
                // start off.
                for (int i = lastCount; i < count; i++) last[i] = false;
                for (int i = count; i < lastCount; i++) last[i] = false;
                lastCount = count;
                ring.Push(Now(ref now), RecordingField.Shape, index, shape, count);
            }
            for (int i = 0; i < count; i++)
            {
                if (values[i] == last[i]) continue;
                last[i] = values[i];
                ring.Push(Now(ref now), field, index, i, values[i] ? 1 : 0);
            }
        }

        private void Touch(RecordingRing ring, ref long now, int index, CustomInputState s, InputTrack t)
        {
            var pads = s.Touchpads;
            int padCount = pads == null ? 0 : Math.Min(pads.Length, RecordingCodec.MaxTouchpads);
            if (padCount != t.PadCount)
            {
                // A touchpad that is gone and comes back starts anew.
                for (int p = padCount; p < t.PadCount; p++) t.Fingers[p] = InputTrack.NoShape;
                t.PadCount = padCount;
                ring.Push(Now(ref now), RecordingField.Shape, index, RecordingCodec.Shape.Touchpads, padCount);
            }
            for (int p = 0; p < padCount; p++)
            {
                var pad = pads[p];
                int fingers = pad == null ? -1 : Math.Min(pad.MaxFingers, RecordingCodec.MaxFingers);
                if (fingers != t.Fingers[p])
                {
                    t.Fingers[p] = fingers;
                    t.ResetPad(p);
                    ring.Push(Now(ref now), RecordingField.Shape, index, RecordingCodec.Shape.FirstTouchpadFingers + p, fingers);
                }
                if (pad == null) continue;
                if (pad.Clicked != t.Click[p])
                {
                    t.Click[p] = pad.Clicked;
                    ring.Push(Now(ref now), RecordingField.TouchClick, index, p, pad.Clicked ? 1 : 0);
                }
                int from = p * RecordingCodec.MaxFingers;
                for (int f = 0; f < fingers; f++)
                {
                    int at = from + f;
                    bool down = pad.FingerDown[f];
                    int id = pad.FingerContactId[f];
                    if (down != t.TouchDown[at] || id != t.TouchId[at])
                    {
                        t.TouchDown[at] = down;
                        t.TouchId[at] = id;
                        ring.Push(Now(ref now), RecordingField.TouchContact, index, (p << 4) | f, down ? 1 : 0, id);
                    }
                    int x = Bits(pad.FingerX[f]), y = Bits(pad.FingerY[f]), pressure = Bits(pad.FingerPressure[f]);
                    if (x != t.TouchX[at] || y != t.TouchY[at] || pressure != t.TouchPressure[at])
                    {
                        t.TouchX[at] = x;
                        t.TouchY[at] = y;
                        t.TouchPressure[at] = pressure;
                        ring.Push(Now(ref now), RecordingField.TouchPosition, index, (p << 4) | f, x, y, pressure);
                    }
                }
            }
        }

        private void Midi(RecordingRing ring, ref long now, int index, MidiInputState midi, InputTrack t)
        {
            if ((midi != null) != t.HasMidi)
            {
                t.HasMidi = midi != null;
                t.ResetMidi();
                ring.Push(Now(ref now), RecordingField.Shape, index, RecordingCodec.Shape.Midi, midi != null ? 1 : 0);
            }
            if (midi == null) return;
            if (!midi.Notes.AsSpan().SequenceEqual(t.Notes))
            {
                Now(ref now);
                for (int i = 0; i < MidiInputState.NoteCount; i++)
                {
                    if (midi.Notes[i] == t.Notes[i]) continue;
                    t.Notes[i] = midi.Notes[i];
                    ring.Push(now, RecordingField.MidiNote, index, i, midi.Notes[i] ? 1 : 0);
                }
            }
            if (!midi.Cc.AsSpan().SequenceEqual(t.Cc))
            {
                Now(ref now);
                for (int i = 0; i < MidiInputState.CcCount; i++)
                {
                    if (midi.Cc[i] == t.Cc[i]) continue;
                    t.Cc[i] = midi.Cc[i];
                    ring.Push(now, RecordingField.MidiCc, index, i, midi.Cc[i]);
                }
            }
            if (!midi.CcUp.AsSpan().SequenceEqual(t.CcUp))
            {
                Now(ref now);
                for (int i = 0; i < MidiInputState.CcCount; i++)
                {
                    if (midi.CcUp[i] == t.CcUp[i]) continue;
                    t.CcUp[i] = midi.CcUp[i];
                    ring.Push(now, RecordingField.MidiCcUp, index, i, midi.CcUp[i] ? 1 : 0);
                }
            }
            if (!midi.CcDown.AsSpan().SequenceEqual(t.CcDown))
            {
                Now(ref now);
                for (int i = 0; i < MidiInputState.CcCount; i++)
                {
                    if (midi.CcDown[i] == t.CcDown[i]) continue;
                    t.CcDown[i] = midi.CcDown[i];
                    ring.Push(now, RecordingField.MidiCcDown, index, i, midi.CcDown[i] ? 1 : 0);
                }
            }
            if (midi.PitchBend != t.PitchBend)
            {
                t.PitchBend = midi.PitchBend;
                ring.Push(Now(ref now), RecordingField.MidiPitchBend, index, 0, midi.PitchBend);
            }
        }

        private void Analog(RecordingRing ring, ref long now, int index, AnalogKeyInputState keys, InputTrack t)
        {
            if ((keys != null) != t.HasAnalog)
            {
                t.HasAnalog = keys != null;
                t.ResetAnalog();
                ring.Push(Now(ref now), RecordingField.Shape, index, RecordingCodec.Shape.AnalogKeys, keys != null ? 1 : 0);
            }
            if (keys == null) return;
            int count = Math.Min(keys.Count, AnalogKeyInputState.MaxKeys);

            // A key that was down and is no longer listed went up.
            for (int i = 0; i < t.AnalogCount; i++)
            {
                int code = t.AnalogCodes[i];
                bool listed = false;
                for (int k = 0; k < count; k++)
                    if (keys.Codes[k] == code) { listed = true; break; }
                if (listed) continue;
                t.AnalogDepth[code] = 0;
                ring.Push(Now(ref now), RecordingField.AnalogKey, index, code, 0);
            }
            int kept = 0;
            for (int k = 0; k < count; k++)
            {
                int code = keys.Codes[k];
                if ((uint)code >= AnalogKeyInputState.CodeCount) continue;
                int depth = Bits(keys.Depths[k]);
                if (depth != t.AnalogDepth[code])
                {
                    t.AnalogDepth[code] = depth;
                    ring.Push(Now(ref now), RecordingField.AnalogKey, index, code, depth);
                }
                t.AnalogCodes[kept++] = code;
            }
            t.AnalogCount = kept;
        }

        /// <summary>Records that a device's read failed or its handle was
        /// lost, at once and with the reason. Poll thread only.</summary>
        internal void InputLost(UserDevice device, int reason)
        {
            if ((Parts & RecordingParts.Input) == 0) return;
            int index = TrackOf(device);
            if (index < 0) return;
            var t = _tracks[index];
            if (t.Online == 0) return;
            t.Online = 0;
            Ring.Push(Ticks(), RecordingField.Online, index, 0, 0, reason);
            Ring.Commit();
        }

        // ─────────────────────────────────────────────
        //  Virtual controllers
        // ─────────────────────────────────────────────

        /// <summary>Records what became of a slot's frame when that changes,
        /// and the slot's type with it.</summary>
        internal void Sent(int slot, int state, int type)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var t = _outputs[slot];
            if (t.Sent == state && t.SentType == type) return;
            t.Sent = state;
            long now = Ticks();
            if (t.SentType != type)
            {
                t.SentType = type;
                t.Last.Type = type;
                Ring.Push(now, RecordingField.OutType, slot, 0, type);
            }
            Ring.Push(now, RecordingField.Sent, slot, 0, state, type);
            Ring.Commit();
        }

        internal void OutGamepad(int slot, in Gamepad gp)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            var ring = Ring;
            long now = 0;
            if (gp.Buttons != last.Buttons)
            {
                last.Buttons = gp.Buttons;
                ring.Push(Now(ref now), RecordingField.GpButtons, slot, 0, gp.Buttons);
            }
            if (gp.LeftTrigger != last.LeftTrigger)
            {
                last.LeftTrigger = gp.LeftTrigger;
                ring.Push(Now(ref now), RecordingField.GpLeftTrigger, slot, 0, gp.LeftTrigger);
            }
            if (gp.RightTrigger != last.RightTrigger)
            {
                last.RightTrigger = gp.RightTrigger;
                ring.Push(Now(ref now), RecordingField.GpRightTrigger, slot, 0, gp.RightTrigger);
            }
            if (gp.ThumbLX != last.LeftX)
            {
                last.LeftX = gp.ThumbLX;
                ring.Push(Now(ref now), RecordingField.GpLeftX, slot, 0, gp.ThumbLX);
            }
            if (gp.ThumbLY != last.LeftY)
            {
                last.LeftY = gp.ThumbLY;
                ring.Push(Now(ref now), RecordingField.GpLeftY, slot, 0, gp.ThumbLY);
            }
            if (gp.ThumbRX != last.RightX)
            {
                last.RightX = gp.ThumbRX;
                ring.Push(Now(ref now), RecordingField.GpRightX, slot, 0, gp.ThumbRX);
            }
            if (gp.ThumbRY != last.RightY)
            {
                last.RightY = gp.ThumbRY;
                ring.Push(Now(ref now), RecordingField.GpRightY, slot, 0, gp.ThumbRY);
            }
            int extra = (gp.Share ? 1 : 0) | (gp.MicMute ? 2 : 0) | (gp.LeftPaddle ? 4 : 0) | (gp.RightPaddle ? 8 : 0)
                | (gp.LeftFunction ? 16 : 0) | (gp.RightFunction ? 32 : 0);
            if (extra != last.Extra)
            {
                last.Extra = extra;
                ring.Push(Now(ref now), RecordingField.GpExtra, slot, 0, extra);
            }
            ring.Commit();
        }

        internal void OutRaw(int slot, in RawHidState raw)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            var ring = Ring;
            long now = 0;
            var axes = raw.Axes;
            for (int i = 0; i < RecordedOutputState.RawAxisCount; i++)
            {
                short value = axes != null && i < axes.Length ? axes[i] : (short)0;
                if (value == last.RawAxes[i]) continue;
                last.RawAxes[i] = value;
                ring.Push(Now(ref now), RecordingField.RawAxis, slot, i, value);
            }
            var buttons = raw.Buttons;
            for (int i = 0; i < RecordedOutputState.RawButtonWords; i++)
            {
                uint value = buttons != null && i < buttons.Length ? buttons[i] : 0u;
                if (value == last.RawButtons[i]) continue;
                last.RawButtons[i] = value;
                ring.Push(Now(ref now), RecordingField.RawButtons, slot, i, (int)value);
            }
            var hats = raw.Povs;
            for (int i = 0; i < RecordedOutputState.RawHatCount; i++)
            {
                int value = hats != null && i < hats.Length ? hats[i] : -1;
                if (value == last.RawHats[i]) continue;
                last.RawHats[i] = value;
                ring.Push(Now(ref now), RecordingField.RawHat, slot, i, value);
            }
            ring.Commit();
        }

        internal void OutKbm(int slot, in KbmRawState k)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var track = _outputs[slot];
            var last = track.Last;
            var ring = Ring;
            long now = 0;
            Keys(ring, ref now, slot, 0, k.Keys0, last);
            Keys(ring, ref now, slot, 1, k.Keys1, last);
            Keys(ring, ref now, slot, 2, k.Keys2, last);
            Keys(ring, ref now, slot, 3, k.Keys3, last);
            if (k.MouseButtons != last.MouseButtons)
            {
                last.MouseButtons = k.MouseButtons;
                ring.Push(Now(ref now), RecordingField.KbmMouseButtons, slot, 0, k.MouseButtons);
            }
            if (k.MouseDeltaX != last.MoveX || k.MouseDeltaY != last.MoveY)
            {
                last.MoveX = k.MouseDeltaX;
                last.MoveY = k.MouseDeltaY;
                ring.Push(Now(ref now), RecordingField.KbmMove, slot, 0, k.MouseDeltaX, k.MouseDeltaY);
            }
            if (k.ScrollDelta != last.Scroll || k.ScrollDeltaH != last.ScrollH)
            {
                last.Scroll = k.ScrollDelta;
                last.ScrollH = k.ScrollDeltaH;
                ring.Push(Now(ref now), RecordingField.KbmScroll, slot, 0, k.ScrollDelta, k.ScrollDeltaH);
            }
            int absoluteFlags = (k.MouseAbsValid ? 1 : 0) | (k.MouseAbsXValid ? 2 : 0) | (k.MouseAbsYValid ? 4 : 0);
            if (Bits(k.MouseAbsX) != Bits(last.AbsoluteX) || Bits(k.MouseAbsY) != Bits(last.AbsoluteY) || absoluteFlags != last.AbsoluteFlags)
            {
                last.AbsoluteX = k.MouseAbsX;
                last.AbsoluteY = k.MouseAbsY;
                last.AbsoluteFlags = absoluteFlags;
                ring.Push(Now(ref now), RecordingField.KbmAbsolute, slot, 0, Bits(k.MouseAbsX), Bits(k.MouseAbsY), absoluteFlags);
            }

            // Exact counts of one pass, written as a device's are.
            bool flick = k.MouseFlickX != 0;
            if (flick || track.FlickMoving)
            {
                track.FlickMoving = flick;
                ring.Push(Now(ref now), RecordingField.KbmFlick, slot, 0, k.MouseFlickX);
            }
            bool gyro = k.MouseGyroX != 0f || k.MouseGyroY != 0f;
            if (gyro || track.GyroMoving)
            {
                track.GyroMoving = gyro;
                ring.Push(Now(ref now), RecordingField.KbmGyro, slot, 0, Bits(k.MouseGyroX), Bits(k.MouseGyroY));
            }
            bool touch = k.MouseTouchX != 0f || k.MouseTouchY != 0f;
            if (touch || track.TouchMoving)
            {
                track.TouchMoving = touch;
                ring.Push(Now(ref now), RecordingField.KbmTouch, slot, 0, Bits(k.MouseTouchX), Bits(k.MouseTouchY));
            }
            bool coast = k.MouseStickCoastX != 0f || k.MouseStickCoastY != 0f;
            if (coast || track.CoastMoving)
            {
                track.CoastMoving = coast;
                ring.Push(Now(ref now), RecordingField.KbmCoast, slot, 0, Bits(k.MouseStickCoastX), Bits(k.MouseStickCoastY));
            }
            ring.Commit();
        }

        private void Keys(RecordingRing ring, ref long now, int slot, int word, ulong value, RecordedOutputState last)
        {
            if (value == last.Keys[word]) return;
            last.Keys[word] = value;
            ring.Push(Now(ref now), RecordingField.KbmKeys, slot, word, (int)(uint)value, (int)(uint)(value >> 32));
        }

        internal void OutMidi(int slot, in MidiRawState midi)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            var ring = Ring;
            long now = 0;
            var cc = midi.CcValues;
            int ccCount = cc == null ? 0 : Math.Min(cc.Length, RecordedOutputState.MidiCount);
            for (int i = 0; i < ccCount; i++)
            {
                if (cc[i] == last.MidiCc[i]) continue;
                last.MidiCc[i] = cc[i];
                ring.Push(Now(ref now), RecordingField.MidiOutCc, slot, i, cc[i]);
            }
            var notes = midi.Notes;
            int noteCount = notes == null ? 0 : Math.Min(notes.Length, RecordedOutputState.MidiCount);
            for (int i = 0; i < noteCount; i++)
            {
                if (notes[i] == last.MidiNotes[i]) continue;
                last.MidiNotes[i] = notes[i];
                ring.Push(Now(ref now), RecordingField.MidiOutNote, slot, i, notes[i] ? 1 : 0);
            }
            ring.Commit();
        }

        internal void OutVr(int slot, in VrRawState vr)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            long now = 0;
            Hand(Ring, ref now, slot, 0, in vr.Left, ref last.Left);
            Hand(Ring, ref now, slot, 1, in vr.Right, ref last.Right);
            Ring.Commit();
        }

        private void Hand(RecordingRing ring, ref long now, int slot, int hand, in VrHandRaw raw, ref VrHandRaw last)
        {
            if (raw.Buttons != last.Buttons || raw.Trigger != last.Trigger || raw.Grip != last.Grip)
            {
                last.Buttons = raw.Buttons;
                last.Trigger = raw.Trigger;
                last.Grip = raw.Grip;
                ring.Push(Now(ref now), RecordingField.VrHand, slot, hand, raw.Buttons, raw.Trigger, raw.Grip);
            }
            if (raw.StickX != last.StickX || raw.StickY != last.StickY)
            {
                last.StickX = raw.StickX;
                last.StickY = raw.StickY;
                ring.Push(Now(ref now), RecordingField.VrStick, slot, hand, raw.StickX, raw.StickY);
            }
        }

        internal void OutTouch(int slot, in TouchpadState touch)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            var ring = Ring;
            long now = 0;
            if (touch.Down0 != last.TouchDown0 || Bits(touch.X0) != Bits(last.TouchX0) || Bits(touch.Y0) != Bits(last.TouchY0))
            {
                last.TouchDown0 = touch.Down0;
                last.TouchX0 = touch.X0;
                last.TouchY0 = touch.Y0;
                ring.Push(Now(ref now), RecordingField.OutTouch, slot, 0, touch.Down0 ? 1 : 0, Bits(touch.X0), Bits(touch.Y0));
            }
            if (touch.Down1 != last.TouchDown1 || Bits(touch.X1) != Bits(last.TouchX1) || Bits(touch.Y1) != Bits(last.TouchY1))
            {
                last.TouchDown1 = touch.Down1;
                last.TouchX1 = touch.X1;
                last.TouchY1 = touch.Y1;
                ring.Push(Now(ref now), RecordingField.OutTouch, slot, 1, touch.Down1 ? 1 : 0, Bits(touch.X1), Bits(touch.Y1));
            }
            if (touch.Click != last.TouchClick)
            {
                last.TouchClick = touch.Click;
                ring.Push(Now(ref now), RecordingField.OutTouchClick, slot, 0, touch.Click ? 1 : 0);
            }
            ring.Commit();
        }

        internal void OutMotion(int slot, in MotionSnapshot motion)
        {
            if ((Parts & RecordingParts.Output) == 0) return;
            var last = _outputs[slot].Last;
            long now = 0;
            int gx = Bits(motion.GyroPitch), gy = Bits(motion.GyroYaw), gz = Bits(motion.GyroRoll);
            if (gx != Bits(last.Gyro[0]) || gy != Bits(last.Gyro[1]) || gz != Bits(last.Gyro[2]))
            {
                last.Gyro[0] = motion.GyroPitch;
                last.Gyro[1] = motion.GyroYaw;
                last.Gyro[2] = motion.GyroRoll;
                Ring.Push(Now(ref now), RecordingField.OutGyro, slot, 0, gx, gy, gz);
            }
            int ax = Bits(motion.AccelX), ay = Bits(motion.AccelY), az = Bits(motion.AccelZ);
            if (ax != Bits(last.Accel[0]) || ay != Bits(last.Accel[1]) || az != Bits(last.Accel[2]))
            {
                last.Accel[0] = motion.AccelX;
                last.Accel[1] = motion.AccelY;
                last.Accel[2] = motion.AccelZ;
                Ring.Push(Now(ref now), RecordingField.OutAccel, slot, 0, ax, ay, az);
            }
            Ring.Commit();
        }

        // ─────────────────────────────────────────────
        //  Feedback and the end of a pass
        // ─────────────────────────────────────────────

        private static int Pack(ushort low, ushort high) => low | (high << 16);

        /// <summary>Records the rumble a game asks of a slot when it
        /// changes.</summary>
        internal void GameRumble(int slot, Vibration v)
        {
            if ((Parts & RecordingParts.Feedback) == 0) return;
            if (v == null) return;
            var t = _outputs[slot];
            int a = Pack(v.LeftMotorSpeed, v.RightMotorSpeed), b = Pack(v.LeftTriggerMotorSpeed, v.RightTriggerMotorSpeed);
            if (a == t.RumbleA && b == t.RumbleB) return;
            t.RumbleA = a;
            t.RumbleB = b;
            Ring.Push(Ticks(), RecordingField.GameRumble, slot, 0, a, b);
            Ring.Commit();
        }

        /// <summary>What every pass ends with: the rumble last written to
        /// each device, and the devices that went away without a failed
        /// read. Poll thread only.</summary>
        internal void EndPass()
        {
            int count = _count;
            long now = 0;
            bool feedback = (Parts & RecordingParts.Feedback) != 0;
            for (int i = 0; i < count; i++)
            {
                var t = _tracks[i];
                var device = t.Device;
                if (t.Online != 1)
                {
                    // With the input part left out no device is marked
                    // online here, and the rumble written to one that is
                    // online still belongs to the feedback part (#600).
                    if ((Parts & RecordingParts.Input) != 0 || device == null || !device.IsOnline) continue;
                }
                else if (t.SeenPass != _pass && !device.IsOnline)
                {
                    t.Online = 0;
                    Ring.Push(Now(ref now), RecordingField.Online, i, 0, 0, RecordingCodec.OfflineReason.Gone);
                    continue;
                }
                var force = device.ForceFeedbackState;
                if (force == null || !feedback) continue;
                int a = Pack(force.LeftMotorSpeed, force.RightMotorSpeed), b = Pack(force.LeftTriggerMotorSpeed, force.RightTriggerMotorSpeed);
                if (a == t.RumbleA && b == t.RumbleB) continue;
                t.RumbleA = a;
                t.RumbleB = b;
                Ring.Push(Now(ref now), RecordingField.DeviceRumble, i, 0, a, b);
            }
            Ring.Commit();
            _pass++;
            Volatile.Write(ref _lastPass, Ticks());
        }

        /// <summary>Adds what one tap of a measured pass spent in the
        /// capture.</summary>
        internal void AddCost(long ticks) => _timedTicks += ticks;

        /// <summary>Adds what the end of a measured pass spent, and counts
        /// the pass.</summary>
        internal void AddPassCost(long ticks)
        {
            _timedTicks += ticks;
            _timedPasses++;
        }

        /// <summary>A marker set on the poll thread, by a macro, at the time
        /// it fired.</summary>
        internal void Mark()
        {
            Ring.Push(Ticks(), RecordingField.Marker, 0, 0, 0);
            Ring.Commit();
        }

        /// <summary>Says whether the engine rests: 0 it runs, 1 it idles with
        /// no slot in use, 2 it is suspended behind another window. Written
        /// when it changes.</summary>
        internal void Rest(int rest)
        {
            Volatile.Write(ref _restSeen, rest);
            if ((Parts & RecordingParts.Timing) == 0) return;
            if (rest == _rest) return;
            _rest = rest;
            Ring.Push(Ticks(), RecordingField.TimingRest, 0, 0, rest);
            Ring.Commit();
        }

        /// <summary>A pass that took long: the SDL read, the sweep and the
        /// whole pass, in milliseconds.</summary>
        internal void Stall(long sdlMs, long sweepMs, long passMs)
        {
            if ((Parts & RecordingParts.Timing) == 0) return;
            Ring.Push(Ticks(), RecordingField.TimingStall, 0, 0,
                (int)Math.Min(sdlMs, int.MaxValue), (int)Math.Min(sweepMs, int.MaxValue), (int)Math.Min(passMs, int.MaxValue));
            Ring.Commit();
        }

        /// <summary>A pass that began long after the one before it: the time
        /// between the two, and the time the set rate gives, in
        /// microseconds.</summary>
        internal void Gap(long gapTicks, int rateHz)
        {
            if ((Parts & RecordingParts.Timing) == 0) return;
            Ring.Push(Ticks(), RecordingField.TimingGap, 0, 0,
                (int)Math.Min(gapTicks * 1_000_000 / Stopwatch.Frequency, int.MaxValue), 1_000_000 / Math.Max(1, rateHz));
            Ring.Commit();
        }

        /// <summary>Where the capture's second stands at
        /// <paramref name="now"/>: 0 inside one, 1 at the first pass it
        /// sees, when the poll loop's sums hold what came before and are to
        /// be cleared, and 2 when a second just ended and its figures are
        /// due.</summary>
        internal int SecondPhase(long now)
        {
            if (_secondStart == 0)
            {
                _secondStart = now;
                return 1;
            }
            return now - _secondStart >= Stopwatch.Frequency ? 2 : 0;
        }

        /// <summary>
        /// Writes one second's timing: the passes, the set rate and the rate
        /// they ran at, a pass's mean work and the longest, how late they
        /// began, and each step's share, from the sums the poll loop keeps.
        /// The capture's own cost is the mean of the measured passes.
        /// </summary>
        internal void Second(long now, int rateHz, long passes, long sleeps, long workTicks, long longestTicks,
            long lateTicks, long latestTicks, long[] stepTicks)
        {
            long span = now - _secondStart;
            _secondStart = now;
            ForceSecond(now);
            if (passes <= 0 || span <= 0 || (Parts & RecordingParts.Timing) == 0) return;
            var ring = Ring;
            double perPass = 1e7 / Stopwatch.Frequency / passes;   // tenths of a microsecond a pass, per tick
            int rateMilli = (int)Math.Min(passes * 1000.0 * Stopwatch.Frequency / span, int.MaxValue);
            int capture = _timedPasses == 0 ? 0 : (int)(_timedTicks * 1e7 / Stopwatch.Frequency / _timedPasses);
            _timedTicks = 0;
            _timedPasses = 0;
            ring.Push(now, RecordingField.TimingPasses, 0, 0, (int)Math.Min(passes, int.MaxValue), rateHz, rateMilli);
            ring.Push(now, RecordingField.TimingWork, 0, 0, (int)(workTicks * perPass),
                (int)Math.Min(longestTicks * 1_000_000 / Stopwatch.Frequency, int.MaxValue), capture);
            ring.Push(now, RecordingField.TimingLate, 0, 0, (int)(lateTicks * perPass),
                (int)Math.Min(latestTicks * 1_000_000 / Stopwatch.Frequency, int.MaxValue), (int)Math.Min(sleeps, int.MaxValue));
            for (int i = 0; i < stepTicks.Length; i++)
                ring.Push(now, RecordingField.TimingStep, 0, i, (int)(stepTicks[i] * perPass));
            ring.Commit();
        }
    }
}
