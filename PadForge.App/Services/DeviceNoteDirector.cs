using System;
using System.Collections.Generic;
using System.Numerics;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>What a device note says (#597).</summary>
    internal enum DeviceNoteKind
    {
        /// <summary>Nothing.</summary>
        None,
        /// <summary>The device dropped and stayed away.</summary>
        Disconnected,
        /// <summary>The device connected.</summary>
        Connected,
        /// <summary>The device dropped three times in a minute. Said once,
        /// and the device says nothing more for five minutes.</summary>
        KeepsDisconnecting,
        /// <summary>The same three drops with the Device Disconnected Note
        /// off: the five minutes begin and no line shows.</summary>
        Silenced,
    }

    /// <summary>One device assigned to a virtual controller that exists and
    /// is on, as one walk reads it.</summary>
    internal readonly struct DeviceNoteReading
    {
        public DeviceNoteReading(Guid device, bool online, int slots, bool raisesNotes)
        {
            Device = device;
            Online = online;
            Slots = slots;
            RaisesNotes = raisesNotes;
        }

        public Guid Device { get; }

        public bool Online { get; }

        /// <summary>The controllers it is assigned to, a bit for each
        /// slot.</summary>
        public int Slots { get; }

        /// <summary>Whether the row is a connection. A row that PadForge or
        /// an extra feeds raises no note of its own, and still keeps a
        /// controller from reading as empty while it is online.</summary>
        public bool RaisesNotes { get; }
    }

    /// <summary>What the walk keeps of one device between two reads. Made by
    /// <see cref="Seed"/>, never by default.</summary>
    internal struct DeviceNoteState
    {
        /// <summary>The state the user was last told, or the state the
        /// device was first read in: connected or not.</summary>
        public bool Told;

        /// <summary>When the device was first read away in the absence that
        /// runs now, or <see cref="DeviceNoteDirector.Never"/> while it is
        /// connected.</summary>
        public long AwaySince;

        /// <summary>Whether that absence has held and been counted as a
        /// drop.</summary>
        public bool DropCounted;

        /// <summary>When a note last named the device, or Never.</summary>
        public long LastNote;

        /// <summary>When the last drop was counted, or Never.</summary>
        public long Drop1;

        /// <summary>When the drop before that one was counted, or
        /// Never.</summary>
        public long Drop2;

        /// <summary>The device dropped three times in a minute and its line
        /// is still to be said.</summary>
        public bool KeepsOwed;

        /// <summary>When the five minutes of silence end, or Never.</summary>
        public long SilentUntil;

        /// <summary>A device read for the first time: what it is now is
        /// what is known of it, so nothing is said. One that is away when
        /// first read was never seen to drop, so that absence counts as no
        /// drop.</summary>
        public static DeviceNoteState Seed(bool online, long nowMs) => new DeviceNoteState
        {
            Told = online,
            AwaySince = online ? DeviceNoteDirector.Never : nowMs,
            DropCounted = !online,
            LastNote = DeviceNoteDirector.Never,
            Drop1 = DeviceNoteDirector.Never,
            Drop2 = DeviceNoteDirector.Never,
            SilentUntil = DeviceNoteDirector.Never,
        };
    }

    /// <summary>One line for the flyout.</summary>
    internal readonly struct DeviceNote
    {
        public DeviceNote(DeviceNoteKind kind, int count, Guid device, int emptiedSlot)
        {
            Kind = kind;
            Count = count;
            Device = device;
            EmptiedSlot = emptiedSlot;
        }

        public DeviceNoteKind Kind { get; }

        /// <summary>How many devices the line stands for. A line for one
        /// names it.</summary>
        public int Count { get; }

        /// <summary>The device, when the line names one.</summary>
        public Guid Device { get; }

        /// <summary>The controller a drop left with no device connected, or
        /// -1.</summary>
        public int EmptiedSlot { get; }
    }

    /// <summary>
    /// Decides when the flyout names a mapped device that disconnects or
    /// connects (#597). A device has no event for either, and Step 2 of the
    /// engine clears a row's online flag with no word to anyone, so InputService
    /// reads every mapped row four times a second on the UI timer and hands
    /// the readings here. This class holds no window and reads no device, so
    /// its rules run in a test with no screen, as
    /// <see cref="LayerFlyoutDirector"/>'s do. The rule for one device is
    /// <see cref="Decide"/>, a pure function in the form of the battery
    /// alerts' (InputService.BatteryEdgeDecision).
    ///
    /// <para>What keeps it quiet:</para>
    /// <list type="bullet">
    /// <item>Nothing is said of a state the user never saw change. The first
    /// read of a device is what is known of it, at an engine start, after a
    /// profile switch and for a device assigned since.</item>
    /// <item>After an engine start, a sleep or a USB port PadForge cycled
    /// itself, the rows are not believed until an enumeration that began
    /// <see cref="SettleMs"/> later has finished. Then every device is read
    /// as for the first time.</item>
    /// <item>A drop has to hold <see cref="HoldMs"/>.</item>
    /// <item>A device is named once in <see cref="RateMs"/>. A change that
    /// falls inside that time is said when it ends, if it still stands
    /// then.</item>
    /// <item>Three drops in a minute are said once, as keeps disconnecting,
    /// and the device says nothing for five minutes.</item>
    /// <item>Devices that change within half a second share one line.</item>
    /// <item>Nothing is decided while the flyout shows a profile switch or
    /// the controllers turned on or off, so a change that ends before the
    /// flyout is free is never said. A line that is decided waits its turn
    /// behind a note that is up.</item>
    /// </list>
    /// </summary>
    internal sealed class DeviceNoteDirector
    {
        /// <summary>No time: a moment that has not come.</summary>
        internal const long Never = -1;

        /// <summary>How often InputService reads the rows.</summary>
        internal const int WalkMs = 250;

        /// <summary>How long a device has to stay away before its drop is
        /// counted and said.</summary>
        internal const int HoldMs = 1500;

        /// <summary>The least time between two notes that name one
        /// device.</summary>
        internal const int RateMs = 10_000;

        /// <summary>Devices that change within this time share one
        /// line.</summary>
        internal const int BurstMs = 500;

        /// <summary>Three drops within this time are a device that keeps
        /// disconnecting.</summary>
        internal const int FlapWindowMs = 60_000;

        /// <summary>How long such a device says nothing.</summary>
        internal const int SilenceMs = 300_000;

        /// <summary>After a mark, the rows are believed once an enumeration
        /// that began this much later has finished. The engine enumerates
        /// every two seconds, and a device that was there all along can
        /// reach its row an enumeration late: SDL's driver for the Xbox 360
        /// wireless receiver announces a pad when it reads the receiver's
        /// answer, and no enumeration waits for that. A USB device takes a
        /// moment to return after sleep or after its port was cycled.</summary>
        internal const int SettleMs = 5000;

        /// <summary>A walk that comes this long after the one before it
        /// stood still: the PC slept without a word, or the process was
        /// held. What the rows said before is old.</summary>
        internal const int StallMs = 5000;

        private readonly Dictionary<Guid, DeviceNoteState> _states = new Dictionary<Guid, DeviceNoteState>();
        private readonly HashSet<Guid> _read = new HashSet<Guid>();
        private readonly List<Guid> _gone = new List<Guid>();

        // The devices decided on and waiting out their half second, and
        // when the first of each kind was decided.
        private readonly List<Guid> _dropped = new List<Guid>();
        private readonly List<Guid> _returned = new List<Guid>();
        private long _droppedSince = Never;
        private long _returnedSince = Never;

        // The lines that wait for the flyout, the oldest first.
        private readonly List<DeviceNote> _lines = new List<DeviceNote>();

        // The moment the rows were last known to be old, and whether an
        // enumeration from after it is still awaited.
        private long _mark = Never;
        private bool _settling;

        private long _lastTick = Never;

        /// <summary>Told of a device that went quiet for five minutes, with
        /// or without its line. InputService writes the diagnostics line
        /// there.</summary>
        internal Action<Guid> Silenced { get; set; }

        /// <summary>Whether a device has been read since the last time
        /// everything was forgotten. For the tests.</summary>
        internal bool Knows(Guid device) => _states.ContainsKey(device);

        /// <summary>Forgets every device and every line, so the next walk
        /// reads each device as for the first time: a profile switch, which
        /// changes what is mapped, and an engine stop.</summary>
        internal void Reset()
        {
            _states.Clear();
            _dropped.Clear();
            _returned.Clear();
            _droppedSince = Never;
            _returnedSince = Never;
            _lines.Clear();
        }

        /// <summary>The rows are old from <paramref name="atMs"/> on: the
        /// engine started, Windows said it is going to sleep or has woken,
        /// or PadForge cycled a USB port. Everything is forgotten, and
        /// nothing is read until an enumeration that began
        /// <see cref="SettleMs"/> after the latest mark has finished. A
        /// sleep is marked at both ends, so the rows of the moments before
        /// it and of the moments after it are both left unread.</summary>
        internal void Mark(long atMs)
        {
            if (atMs > _mark) _mark = atMs;
            _settling = true;
            Reset();
        }

        /// <summary>One walk. <paramref name="rowsReadAt"/> is when the
        /// enumeration the rows come from began
        /// (InputManager.DevicesEnumeratedSince), or <see cref="Never"/>
        /// while the engine has listed nothing. <paramref name="waiting"/>
        /// holds every decision while the flyout shows something else.
        /// <paramref name="free"/> says the flyout can take a line now. The
        /// answer is that line, or one whose kind is None.</summary>
        internal DeviceNote Tick(long nowMs, long rowsReadAt, IReadOnlyList<DeviceNoteReading> mapped,
            bool noteDrops, bool noteReturns, bool waiting, bool free)
        {
            bool stood = _lastTick != Never && nowMs - _lastTick > StallMs;
            _lastTick = nowMs;
            if (stood) Mark(nowMs);
            if (rowsReadAt == Never) return default;
            if (_settling)
            {
                if (rowsReadAt < _mark + SettleMs) return default;
                _settling = false;
            }

            // Both notes off: nothing is kept, so a note turned on later
            // starts from what is true then.
            if (!noteDrops && !noteReturns)
            {
                Reset();
                return default;
            }

            _read.Clear();
            for (int i = 0; i < mapped.Count; i++)
            {
                var reading = mapped[i];
                if (!reading.RaisesNotes) continue;
                _read.Add(reading.Device);
                if (!_states.TryGetValue(reading.Device, out var state))
                {
                    _states[reading.Device] = DeviceNoteState.Seed(reading.Online, nowMs);
                    continue;
                }
                var (next, note) = Decide(state, reading.Online, nowMs, noteDrops, noteReturns, waiting);
                _states[reading.Device] = next;
                switch (note)
                {
                    case DeviceNoteKind.Disconnected:
                        if (_droppedSince == Never) _droppedSince = nowMs;
                        _dropped.Add(reading.Device);
                        break;
                    case DeviceNoteKind.Connected:
                        if (_returnedSince == Never) _returnedSince = nowMs;
                        _returned.Add(reading.Device);
                        break;
                    case DeviceNoteKind.KeepsDisconnecting:
                        _lines.Add(new DeviceNote(DeviceNoteKind.KeepsDisconnecting, 1, reading.Device, -1));
                        Silenced?.Invoke(reading.Device);
                        break;
                    case DeviceNoteKind.Silenced:
                        Silenced?.Invoke(reading.Device);
                        break;
                }
            }

            // A device that is no longer mapped is forgotten with what was
            // about to be said of it: its controller was deleted or turned
            // off, or it was taken off it.
            _gone.Clear();
            foreach (var device in _states.Keys)
                if (!_read.Contains(device)) _gone.Add(device);
            foreach (var device in _gone)
            {
                _states.Remove(device);
                _dropped.Remove(device);
                _returned.Remove(device);
            }

            if (_droppedSince != Never && nowMs - _droppedSince >= BurstMs)
            {
                Line(DeviceNoteKind.Disconnected, _dropped, mapped);
                _droppedSince = Never;
            }
            if (_returnedSince != Never && nowMs - _returnedSince >= BurstMs)
            {
                Line(DeviceNoteKind.Connected, _returned, mapped);
                _returnedSince = Never;
            }

            while (free && _lines.Count > 0)
            {
                var line = _lines[0];
                _lines.RemoveAt(0);
                // A line whose note was turned off while it waited, or whose
                // device has left its controller since, is not said.
                if (!(line.Kind == DeviceNoteKind.Connected ? noteReturns : noteDrops)) continue;
                if (line.Count == 1 && !_states.ContainsKey(line.Device)) continue;
                return line;
            }
            return default;
        }

        /// <summary>Makes one line of the devices that changed together and
        /// puts it behind the lines that wait. A line of the same kind that
        /// still waits there takes them in, so the flyout never falls
        /// behind what happened.</summary>
        private void Line(DeviceNoteKind kind, List<Guid> devices, IReadOnlyList<DeviceNoteReading> mapped)
        {
            if (devices.Count == 0) return;
            var line = devices.Count == 1
                ? new DeviceNote(kind, 1, devices[0], kind == DeviceNoteKind.Disconnected ? EmptiedSlot(mapped, devices[0]) : -1)
                : new DeviceNote(kind, devices.Count, Guid.Empty, -1);
            devices.Clear();
            int last = _lines.Count - 1;
            if (last >= 0 && _lines[last].Kind == kind)
                _lines[last] = new DeviceNote(kind, _lines[last].Count + line.Count, Guid.Empty, -1);
            else
                _lines.Add(line);
        }

        /// <summary>
        /// The rule for one device, pure so the tests pin it with no device:
        /// what the walk keeps of it, what it reads now, and the clock give
        /// what it keeps next and what, if anything, is said.
        ///
        /// <para>A return stands at once and a drop once it has held
        /// <see cref="HoldMs"/>. What stands is said when it differs from
        /// what the user was last told and the device has not been named for
        /// <see cref="RateMs"/>. Inside that time nothing is said, and the
        /// difference is said when the time ends if it still stands, so the
        /// last thing said of a device is never left wrong. A note that is
        /// off is taken as said.</para>
        ///
        /// <para>The third drop counted within <see cref="FlapWindowMs"/>
        /// is said as keeps disconnecting in place of its own line, and the
        /// device then says nothing for <see cref="SilenceMs"/>, during
        /// which what the user was told follows what is true.</para>
        ///
        /// <para><paramref name="waiting"/> keeps the start of an absence
        /// and decides nothing: the flyout shows a profile switch. A drop
        /// that outlasts the wait is said after it, and one that ends inside
        /// it never is.</para>
        /// </summary>
        internal static (DeviceNoteState Next, DeviceNoteKind Note) Decide(
            DeviceNoteState state, bool online, long nowMs, bool noteDrops, bool noteReturns, bool waiting)
        {
            var s = state;
            if (online)
            {
                s.AwaySince = Never;
                s.DropCounted = false;
            }
            else if (s.AwaySince == Never)
            {
                s.AwaySince = nowMs;
                s.DropCounted = false;
            }
            if (waiting) return (s, DeviceNoteKind.None);

            bool held = !online && nowMs - s.AwaySince >= HoldMs;
            bool silent = s.SilentUntil != Never && nowMs < s.SilentUntil;

            if (held && !s.DropCounted)
            {
                s.DropCounted = true;
                if (!silent)
                {
                    if (s.Drop2 != Never && nowMs - s.Drop2 <= FlapWindowMs)
                    {
                        s.KeepsOwed = true;
                        s.Drop1 = Never;
                        s.Drop2 = Never;
                    }
                    else
                    {
                        s.Drop2 = s.Drop1;
                        s.Drop1 = nowMs;
                    }
                }
            }

            bool stands = online || held;
            if (silent)
            {
                if (stands) s.Told = online;
                return (s, DeviceNoteKind.None);
            }

            bool named = s.LastNote != Never && nowMs - s.LastNote < RateMs;
            if (s.KeepsOwed)
            {
                if (noteDrops && named) return (s, DeviceNoteKind.None);
                s.KeepsOwed = false;
                s.SilentUntil = nowMs + SilenceMs;
                if (stands) s.Told = online;
                if (!noteDrops) return (s, DeviceNoteKind.Silenced);
                s.LastNote = nowMs;
                return (s, DeviceNoteKind.KeepsDisconnecting);
            }

            if (!stands || s.Told == online) return (s, DeviceNoteKind.None);
            if (!(online ? noteReturns : noteDrops))
            {
                s.Told = online;
                return (s, DeviceNoteKind.None);
            }
            if (named) return (s, DeviceNoteKind.None);
            s.Told = online;
            s.LastNote = nowMs;
            return (s, online ? DeviceNoteKind.Connected : DeviceNoteKind.Disconnected);
        }

        /// <summary>The lowest controller a device's drop left with no
        /// device connected, or -1. Every row assigned to the controller
        /// counts, a row that raises no note of its own included: a
        /// controller a merged keyboard row still drives has a device
        /// left.</summary>
        internal static int EmptiedSlot(IReadOnlyList<DeviceNoteReading> mapped, Guid device)
        {
            int own = 0, driven = 0;
            for (int i = 0; i < mapped.Count; i++)
            {
                var reading = mapped[i];
                if (reading.Device == device)
                {
                    // Back already: its controllers have it again.
                    if (reading.Online) return -1;
                    own |= reading.Slots;
                }
                else if (reading.Online)
                {
                    driven |= reading.Slots;
                }
            }
            int emptied = own & ~driven;
            return emptied == 0 ? -1 : BitOperations.TrailingZeroCount(emptied);
        }

        /// <summary>The words of a line. A line for one device names it, by
        /// the name the Devices list shows. <paramref name="controllerName"/>
        /// is the controller the drop emptied, or null.
        /// <paramref name="batteryPercent"/> is the returned device's level,
        /// or negative when PadForge does not know it.</summary>
        internal static string Text(DeviceNote note, string deviceName, string controllerName, int batteryPercent)
        {
            var s = Strings.Instance;
            switch (note.Kind)
            {
                case DeviceNoteKind.Disconnected:
                    if (note.Count > 1) return string.Format(s.Main_DevicesDisconnected_Format, note.Count);
                    return string.IsNullOrEmpty(controllerName)
                        ? string.Format(s.Main_DeviceDisconnected_Format, deviceName)
                        : string.Format(s.Main_DeviceDisconnectedLast_Format, deviceName, controllerName);
                case DeviceNoteKind.Connected:
                    if (note.Count > 1) return string.Format(s.Main_DevicesConnected_Format, note.Count);
                    return batteryPercent >= 0
                        ? string.Format(s.Main_DeviceConnectedBattery_Format, deviceName, batteryPercent)
                        : string.Format(s.Main_DeviceConnected_Format, deviceName);
                case DeviceNoteKind.KeepsDisconnecting:
                    return string.Format(s.Main_DeviceKeepsDisconnecting_Format, deviceName);
                default:
                    return string.Empty;
            }
        }
    }
}
